using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// UnitRegistry.cs  (U1)
//
// Purpose:        Process-wide loader + registry for UnitDefinitions
//                 (Data/Units/*.json), mirroring ItemDatabase's idiom.
//                 The single place combat resolves a unit's stats,
//                 keyed by id. Also holds the EnemyArchetype -> id alias
//                 table so the legacy enum (and authored encounter JSON
//                 that names archetypes) keeps resolving during U1; the
//                 enum is removed in U2.
// Layer:          Loader
// Collaborators:  UnitDefinition.cs, EnemyArchetype.cs (facade),
//                 EncounterPoolLoader.cs (alias resolution),
//                 CombatManager.cs (spawn).
// See:            build_order_v3 §4 (U1)
// ============================================================

/// <summary>Loads and caches UnitDefinitions. Lazy load on first access;
/// robust to missing data (logs, returns a non-null default so combat never
/// null-refs). Legacy EnemyArchetype resolves through the alias table.</summary>
public static class UnitRegistry
{
    private const string UNITS_DIR = "res://Data/Units/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    // Legacy enum -> canonical unit id. Alias only; the enum dies in U2.
    private static readonly Dictionary<EnemyArchetype, string> ArchetypeToId = new()
    {
        { EnemyArchetype.Soldier,  "generic_soldier" },
        { EnemyArchetype.Brute,    "generic_brute" },
        { EnemyArchetype.Defender, "generic_defender" },
        { EnemyArchetype.Ranger,   "generic_ranger" },
        { EnemyArchetype.Wizard,   "generic_wizard" },
    };

    private static readonly Dictionary<string, UnitDefinition> _cache = new();
    private static readonly UnitDefinition _fallback = new() { Id = "fallback", ThreatLabel = "Unknown" };
    private static bool _loaded = false;

    public static void LoadAll()
    {
        if (_loaded) return;
        _loaded = true;
        _cache.Clear();

        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(UNITS_DIR)))
        {
            GD.PrintErr($"UnitRegistry: No units directory at {UNITS_DIR}");
            return;
        }

        using var dir = DirAccess.Open(UNITS_DIR);
        if (dir == null) return;

        dir.ListDirBegin();
        string filename = dir.GetNext();
        while (filename != "")
        {
            if (!dir.CurrentIsDir() && filename.EndsWith(".json"))
            {
                LoadFile($"{UNITS_DIR}{filename}");
            }
            filename = dir.GetNext();
        }
        dir.ListDirEnd();

        GD.Print($"UnitRegistry: Loaded {_cache.Count} unit definition(s).");
    }

    private static void LoadFile(string path)
    {
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return;

            var def = JsonSerializer.Deserialize<UnitDefinition>(file.GetAsText(), JsonOptions);
            if (def == null || string.IsNullOrEmpty(def.Id)) return;

            _cache[def.Id] = def;
        }
        catch (Exception e)
        {
            GD.PrintErr($"UnitRegistry: Error loading {path}: {e.Message}");
        }
    }

    /// <summary>Definition by id, or a logged non-null fallback if absent.</summary>
    public static UnitDefinition Get(string id)
    {
        LoadAll();
        if (_cache.TryGetValue(id, out var def)) return def;
        GD.PrintErr($"UnitRegistry: No unit definition '{id}' — using fallback.");
        return _fallback;
    }

    public static string IdForArchetype(EnemyArchetype a) =>
        ArchetypeToId.TryGetValue(a, out var id) ? id : "generic_soldier";

    /// <summary>Definition for a legacy archetype via the alias table. Never null.</summary>
    public static UnitDefinition ForArchetype(EnemyArchetype a) => Get(IdForArchetype(a));

    /// <summary>Resolve an authored token — a legacy enum name ("Soldier") OR a
    /// unit id ("generic_soldier") — to an EnemyArchetype for the loader. Enum
    /// names win; unit ids fall back through the alias table. (Loader aliases.)</summary>
    public static bool TryResolveArchetype(string token, out EnemyArchetype archetype)
    {
        if (Enum.TryParse(token, ignoreCase: true, out archetype))
        {
            return true;
        }
        foreach (var kvp in ArchetypeToId)
        {
            if (string.Equals(kvp.Value, token, StringComparison.OrdinalIgnoreCase))
            {
                archetype = kvp.Key;
                return true;
            }
        }
        archetype = EnemyArchetype.Soldier;
        return false;
    }

    // ── Verification (U1 exit: parity + serialization round-trip) ──────────

    /// <summary>Assert every generic_* def loaded from JSON matches the stats the
    /// old EnemyArchetypeData hardcoded (parity), and that a UnitDefinition
    /// survives a JSON round-trip. Prints PASS/FAIL; PushErrors on failure. The
    /// expected table below is the test oracle — the ONLY place the old numbers
    /// still live. Wired to the CampusScreen debug panel.</summary>
    public static bool AssertParityAndRoundTrip()
    {
        LoadAll();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== UNIT REGISTRY — PARITY + ROUND-TRIP ===");
        bool ok = true;

        // id, HP, spd, armor, range, dmg, prefDist, label, r, g, b
        (string id, int hp, int spd, int arm, int rng, int dmg, int pd, string lbl, float r, float g, float b)[] expected =
        {
            ("generic_soldier",  20, 2, 0, 1, 5, 1, "Soldier",  1.0f, 0.25f, 0.25f),
            ("generic_brute",    40, 1, 0, 1, 8, 1, "Brute",    0.8f, 0.2f,  0.9f),
            ("generic_defender", 25, 1, 5, 1, 4, 1, "Defender", 0.2f, 0.5f,  0.9f),
            ("generic_ranger",   15, 2, 0, 3, 4, 3, "Ranger",   0.2f, 0.8f,  0.3f),
            ("generic_wizard",   12, 1, 0, 5, 9, 4, "Wizard",   0.9f, 0.9f,  0.1f),
        };

        foreach (var e in expected)
        {
            var d = Get(e.id);
            bool m = d.Id == e.id && d.MaxHealth == e.hp && d.BaseSpeed == e.spd &&
                     d.Armor == e.arm && d.AttackRange == e.rng && d.AttackDamage == e.dmg &&
                     d.PreferredDistance == e.pd && d.ThreatLabel == e.lbl &&
                     Mathf.IsEqualApprox(d.ColorR, e.r) && Mathf.IsEqualApprox(d.ColorG, e.g) &&
                     Mathf.IsEqualApprox(d.ColorB, e.b);
            sb.AppendLine(m ? $"  {e.id}: PARITY OK" : $"  {e.id}: PARITY FAIL (json drifted from spec)");
            ok &= m;
        }

        // Round-trip one definition through the same options the loader uses.
        var probe = Get("generic_wizard");
        var rt = JsonSerializer.Deserialize<UnitDefinition>(
            JsonSerializer.Serialize(probe, JsonOptions), JsonOptions);
        bool rok = rt != null && rt.Id == probe.Id && rt.MaxHealth == probe.MaxHealth &&
                   rt.AttackDamage == probe.AttackDamage && rt.BehaviorKey == probe.BehaviorKey &&
                   Mathf.IsEqualApprox(rt.ColorB, probe.ColorB);
        sb.AppendLine(rok ? "  UnitDefinition round-trip: OK" : "  UnitDefinition round-trip: FAIL");
        ok &= rok;

        sb.AppendLine(ok ? "RESULT: ALL PASSED" : "RESULT: FAILURES ABOVE");
        GD.Print(sb.ToString());
        if (!ok) GD.PushError("[UnitRegistry] Parity/round-trip assertion FAILED — see Output.");
        return ok;
    }
}
