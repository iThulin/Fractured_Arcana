using Godot;
using System.Collections.Generic;

// ============================================================
// TerrainRecipeMap.cs
//
// Purpose:        Maps an overworld terrain type to the combat map
//                 recipe id used to generate that fight's arena.
//                 Loaded from Data/Maps/terrain_map.json so the
//                 mapping is authorable, not hard-coded. This is the
//                 one-directional seam between overworld and combat:
//                 overworld emits a TerrainType, combat consumes a
//                 recipe id. Neither side's generation touches this.
// Layer:          System (data lookup)
// Collaborators:  CombatManager (resolves before GenerateMap),
//                 OverworldHex.TerrainType (key source),
//                 MapRecipeRegistry (the recipe ids resolve into)
// ============================================================

public static class TerrainRecipeMap
{
    private static readonly Dictionary<string, string> _map = new();
    private static string _fallback = "heathland";
    private static bool _loaded;

    public static void EnsureLoaded(string path = "res://Data/Maps/terrain_map.json")
    {
        if (_loaded)
            return;
        Load(path);
        _loaded = true;
    }

    public static void Reload(string path = "res://Data/Maps/terrain_map.json")
    {
        _loaded = false;
        EnsureLoaded(path);
    }

    /// <summary>Resolves a terrain enum-name string to a recipe id; returns the fallback on any miss.</summary>
    public static string Resolve(string terrainName)
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(terrainName) && _map.TryGetValue(terrainName, out var id))
            return id;
        return _fallback;
    }

    public static string Resolve(OverworldHex.TerrainType terrain) => Resolve(terrain.ToString());

    private static void Load(string path)
    {
        _map.Clear();

        using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (fa == null)
        {
            GD.PushWarning($"[TerrainRecipeMap] Missing {path}; every terrain will use the default fallback.");
            return;
        }

        var json = new Json();
        if (json.Parse(fa.GetAsText()) != Error.Ok)
        {
            GD.PushError($"[TerrainRecipeMap] Parse error in {path}: {json.GetErrorMessage()} (line {json.GetErrorLine()})");
            return;
        }

        var d = json.Data.AsGodotDictionary();

        if (d.ContainsKey("fallback"))
            _fallback = d["fallback"].AsString();

        if (d.ContainsKey("terrain"))
        {
            var t = d["terrain"].AsGodotDictionary();
            foreach (var key in t.Keys)
                _map[key.AsString()] = t[key].AsString();
        }

        GD.Print($"[TerrainRecipeMap] Loaded {_map.Count} terrain→recipe mappings (fallback: {_fallback}).");
    }
}
