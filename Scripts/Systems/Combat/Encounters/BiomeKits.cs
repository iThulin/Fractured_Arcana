using Godot;
using System.Collections.Generic;

// ============================================================
// BiomeKits.cs
//
// Purpose:        Combat overhaul stage C4: what a biome adds to
//                 every fight on its terrain, whatever battlefield
//                 was rolled. A kit is a name, the rules in plain
//                 words, map events (the clock) and field objects
//                 (the things to work). Data only: the events are
//                 ordinary MapEventDefs and the objects are ordinary
//                 map objects.
// Layer:          Data
// Collaborators:  Data/Encounters/biome_kits.json (source),
//                 CombatManager.Biomes.cs (applies a kit),
//                 MapRecipe.cs (MapEventDef, the dictionary readers)
// See:            docs/combat_c4_biome_kits_v1.md
// ============================================================

public sealed class BiomeKit
{
    public string Terrain = "";
    public string Name = "";
    public List<string> Rules = new();
    /// <summary>Raw event dictionaries, exactly as a recipe's map_events entries.
    /// Built into fresh MapEventDefs per fight (BuildEvents).</summary>
    public List<Godot.Collections.Dictionary> Events = new();
    public List<(string Kind, string At)> Objects = new();
}

public static class BiomeKits
{
    private const string KitsPath = "res://Data/Encounters/biome_kits.json";

    private static Dictionary<string, BiomeKit> _kits;

    /// <summary>The kit for an overworld terrain name (OverworldHex.TerrainType), or null.</summary>
    public static BiomeKit For(string terrain)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(terrain))
            return null;
        return _kits.TryGetValue(terrain, out var kit) ? kit : null;
    }

    /// <summary>Fresh event defs for one fight. A kit event that names kinds in
    /// skip_if_recipe_has is dropped when the fight already has one of them, and any
    /// kit event is dropped when the fight already has an event of its own kind, so a
    /// battlefield with its own clock is never given a second one.</summary>
    public static List<MapEventDef> BuildEvents(BiomeKit kit, IReadOnlyList<MapEventDef> present)
    {
        var built = new List<MapEventDef>();
        if (kit == null)
            return built;

        var presentKinds = new HashSet<string>();
        if (present != null)
        {
            foreach (var ev in present)
            {
                if (ev != null && !string.IsNullOrEmpty(ev.Kind))
                    presentKinds.Add(ev.Kind);
            }
        }

        foreach (var source in kit.Events)
        {
            string kind = MapRecipe.Str(source, "kind", "");
            if (string.IsNullOrEmpty(kind) || presentKinds.Contains(kind))
                continue;

            bool blocked = false;
            if (source.ContainsKey("skip_if_recipe_has"))
            {
                foreach (var item in source["skip_if_recipe_has"].AsGodotArray())
                {
                    if (presentKinds.Contains(item.AsString()))
                    {
                        blocked = true;
                        break;
                    }
                }
            }
            if (blocked)
                continue;

            var raw = source.Duplicate(true);
            var def = new MapEventDef
            {
                Kind = kind,
                Round = Mathf.Max(1, MapRecipe.Int(raw, "round", 1)),
                Telegraph = Mathf.Max(1, MapRecipe.Int(raw, "telegraph", 1)),   // a biome always warns a round ahead
                RepeatEvery = Mathf.Max(0, MapRecipe.Int(raw, "repeat_every", 0)),
                Id = MapRecipe.Str(raw, "id", ""),
                When = MapRecipe.Str(raw, "when", ""),
                Raw = raw,
            };
            built.Add(def);
        }
        return built;
    }

    private static void EnsureLoaded()
    {
        if (_kits != null)
            return;
        _kits = new Dictionary<string, BiomeKit>(System.StringComparer.OrdinalIgnoreCase);

        using var file = FileAccess.Open(KitsPath, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            GD.PushWarning($"[BiomeKits] Could not open {KitsPath}. No biome adds anything to a fight.");
            return;
        }
        var json = new Json();
        if (json.Parse(file.GetAsText()) != Error.Ok)
        {
            GD.PushError($"[BiomeKits] Parse error in {KitsPath}: {json.GetErrorMessage()} (line {json.GetErrorLine()})");
            return;
        }

        var root = json.Data.AsGodotDictionary();
        if (!root.ContainsKey("kits"))
            return;
        var kits = root["kits"].AsGodotDictionary();
        var aliases = new Dictionary<string, string>();

        foreach (var key in kits.Keys)
        {
            string terrain = key.AsString();
            var entry = kits[key].AsGodotDictionary();
            if (entry.ContainsKey("same_as"))
            {
                aliases[terrain] = entry["same_as"].AsString();
                continue;
            }

            var kit = new BiomeKit
            {
                Terrain = terrain,
                Name = MapRecipe.Str(entry, "name", terrain),
            };
            if (entry.ContainsKey("rules"))
            {
                foreach (var line in entry["rules"].AsGodotArray())
                    kit.Rules.Add(line.AsString());
            }
            if (entry.ContainsKey("events"))
            {
                foreach (var ev in entry["events"].AsGodotArray())
                    kit.Events.Add(ev.AsGodotDictionary());
            }
            if (entry.ContainsKey("objects"))
            {
                foreach (var ob in entry["objects"].AsGodotArray())
                {
                    var od = ob.AsGodotDictionary();
                    string kind = MapRecipe.Str(od, "kind", "");
                    if (!string.IsNullOrEmpty(kind))
                        kit.Objects.Add((kind, MapRecipe.Str(od, "at", "midpoint")));
                }
            }
            _kits[terrain] = kit;
        }

        foreach (var pair in aliases)
        {
            if (_kits.TryGetValue(pair.Value, out var target))
                _kits[pair.Key] = target;
            else
                GD.PushWarning($"[BiomeKits] '{pair.Key}' is the same as '{pair.Value}', which has no kit.");
        }

        GD.Print($"[BiomeKits] {_kits.Count} terrain(s) carry a kit.");
    }
}
