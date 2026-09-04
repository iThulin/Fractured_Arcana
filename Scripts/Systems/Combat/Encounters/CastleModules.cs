using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CastleModules.cs
//
// Purpose:        Castle module content (Data/CastleModules/*.json,
//                 mobile fortress spec §6) and the guild's installed
//                 loadout. A module may carry a `station` block: then it
//                 claims a rampart tile in castle defence and grants its
//                 effect to whoever stands there (castle_defense_v1 §3).
//                 Overworld effects (fuel, hull, scry) are read by the
//                 expedition side through the same defs.
// Layer:          Systems / Combat / Encounters
// Collaborators:  CastleDefenseCompiler (station tiles), CombatManager
//                 .CastleDefense (station effects), GuildSaveData
//                 (CastleModules)
// See:            docs/castle_defense_v1.md
// ============================================================

public sealed class CastleStationSpec
{
    public string Kind = "";
    public string Label = "";
    public int RangeBonus;
    public int DamageBonus;
    public int Shield;
    public int CoverBonus;
    public int Radius;
    public int Repair;
}

public sealed class CastleModuleDef
{
    public string Id = "";
    public string Name = "";
    public int Cost;
    public string Effect = "none";
    public int Magnitude;
    public string Flavor = "";
    public CastleStationSpec Station;   // null when the module has no combat station
}

public static class CastleModules
{
    private static readonly Dictionary<string, CastleModuleDef> _defs = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>Starter pair every castle carries until the campus install UI (F5)
    /// lets the player change it. Two slots by ruling; Tinker gets three.</summary>
    public static readonly string[] DefaultLoadout = { "ballista_nest", "ward_lantern" };

    public static IReadOnlyDictionary<string, CastleModuleDef> All { get { EnsureLoaded(); return _defs; } }

    public static CastleModuleDef Get(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(id) && _defs.TryGetValue(id, out var d) ? d : null;
    }

    /// <summary>The guild's installed modules in station order. Backfills the
    /// starter pair into an empty save list so old saves field a castle.</summary>
    public static List<string> InstalledIds()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return new List<string>(DefaultLoadout);
        if (save.CastleModules == null || save.CastleModules.Count == 0)
            save.CastleModules = new List<string>(DefaultLoadout);
        return save.CastleModules;
    }

    /// <summary>Installed modules that carry a station, in order: these are the
    /// tiles the compiler lays out.</summary>
    public static List<string> InstalledStationIds()
    {
        var list = new List<string>();
        foreach (var id in InstalledIds())
            if (Get(id)?.Station != null)
                list.Add(id);
        return list;
    }

    /// <summary>Sum of `ambush_delay` magnitudes across installed modules
    /// (Waystone Focus is -1). Negative shortens the wizard's arrival.</summary>
    public static int AmbushDelayModifier()
    {
        int total = 0;
        foreach (var id in InstalledIds())
        {
            var d = Get(id);
            if (d != null && d.Effect == "ambush_delay")
                total += d.Magnitude;
        }
        return total;
    }

    public static void EnsureLoaded(string dir = "res://Data/CastleModules")
    {
        if (_loaded)
            return;
        _loaded = true;
        _defs.Clear();

        using var da = DirAccess.Open(dir);
        if (da == null)
        {
            GD.PushWarning($"[CastleModules] Directory not found: {dir}");
            return;
        }
        foreach (var file in da.GetFiles())
        {
            if (!file.EndsWith(".json"))
                continue;
            string path = dir.TrimEnd('/') + "/" + file;
            using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (fa == null)
                continue;
            var json = new Json();
            if (json.Parse(fa.GetAsText()) != Error.Ok)
            {
                GD.PushError($"[CastleModules] Parse error in {path}: {json.GetErrorMessage()}");
                continue;
            }
            var d = json.Data.AsGodotDictionary();
            var def = new CastleModuleDef
            {
                Id = MapRecipe.Str(d, "id", ""),
                Name = MapRecipe.Str(d, "name", ""),
                Cost = MapRecipe.Int(d, "cost", 0),
                Effect = MapRecipe.Str(d, "effect", "none"),
                Magnitude = MapRecipe.Int(d, "magnitude", 0),
                Flavor = MapRecipe.Str(d, "flavor", ""),
            };
            if (d.ContainsKey("station"))
            {
                var sd = d["station"].AsGodotDictionary();
                def.Station = new CastleStationSpec
                {
                    Kind = MapRecipe.Str(sd, "kind", ""),
                    Label = MapRecipe.Str(sd, "label", def.Name),
                    RangeBonus = MapRecipe.Int(sd, "range_bonus", 0),
                    DamageBonus = MapRecipe.Int(sd, "damage_bonus", 0),
                    Shield = MapRecipe.Int(sd, "shield", 0),
                    CoverBonus = MapRecipe.Int(sd, "cover_bonus", 0),
                    Radius = MapRecipe.Int(sd, "radius", 0),
                    Repair = MapRecipe.Int(sd, "repair", 0),
                };
            }
            if (!string.IsNullOrEmpty(def.Id))
                _defs[def.Id] = def;
        }
        GD.Print($"[CastleModules] Loaded {_defs.Count} module(s) from {dir}.");
    }
}
