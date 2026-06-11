using Godot;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// Bestiary.cs
//
// Purpose:        Process-wide registry of wildlife stat blocks for the
//                 Druid school, loaded from Data/bestiary.json. Mirrors
//                 MapRecipeRegistry: lazy-loaded, keyed by name. The
//                 summon handler consults this before its hardcoded
//                 switch, so adding an animal is a JSON edit only.
// Layer:          Loader / Data
// Collaborators:  Data/bestiary.json (source),
//                 CombatManager.RegisterSummonHandler (consumer),
//                 GrowthManager.cs (resolves "auto" -> a bestiary key),
//                 growth_profiles.json (per-terrain wildlife pools).
// See:            README §6 (school mechanics), §2 (Data/ vs Scripts/Data/)
// ============================================================

/// <summary>Stat block + behaviour tags for one wildlife unit. Tags (pack/bulwark/charge/scout) are read by the unit-behaviour dispatcher; until that is wired they spawn as vanilla stat blocks.</summary>
public class WildlifeDef
{
    public int Hp = 10;
    public int Damage = 0;
    public int Speed = 1;
    public int Armor = 0;
    public List<string> Tags = new();
    public string Notes = "";
}

public static class Bestiary
{
    private static readonly Dictionary<string, WildlifeDef> _defs = new();
    private static bool _loaded;

    public static void EnsureLoaded(string resPath = "res://Data/bestiary.json")
    {
        if (_loaded) return;
        Load(resPath);
        _loaded = true;
    }

    public static void Reload(string resPath = "res://Data/bestiary.json")
    {
        _loaded = false;
        EnsureLoaded(resPath);
    }

    /// <summary>Case-insensitive lookup. Returns false (and null) for unknown keys.</summary>
    public static bool TryGet(string key, out WildlifeDef def)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(key)) { def = null; return false; }
        return _defs.TryGetValue(key.ToLowerInvariant(), out def);
    }

    public static WildlifeDef Get(string key) => TryGet(key, out WildlifeDef d) ? d : null;

    public static bool Contains(string key)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(key) && _defs.ContainsKey(key.ToLowerInvariant());
    }

    public static IReadOnlyCollection<string> Keys
    {
        get { EnsureLoaded(); return _defs.Keys; }
    }

    private static void Load(string resPath)
    {
        _defs.Clear();

        using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PushError($"[Bestiary] Could not open {resPath}.");
            return;
        }

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true
        };

        Dictionary<string, WildlifeDef> raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, WildlifeDef>>(f.GetAsText(), opts);
        }
        catch (System.Exception e)
        {
            GD.PushError($"[Bestiary] Parse error in {resPath}: {e.Message}");
            return;
        }

        if (raw == null) return;

        foreach (KeyValuePair<string, WildlifeDef> kvp in raw)
            _defs[kvp.Key.ToLowerInvariant()] = kvp.Value;

        GD.Print($"[Bestiary] Loaded {_defs.Count} wildlife definition(s).");
    }
}
