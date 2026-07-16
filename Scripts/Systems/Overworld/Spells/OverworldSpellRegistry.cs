using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// OverworldSpellRegistry.cs  (S1, 2026-07-15)
//
// Purpose:        Lazy loader and per-session cache for overworld
//                 spell definitions. Mirrors ArchmageRegistry's
//                 pattern (load once, cache, expose by id), with
//                 one difference: each JSON file in
//                 Data/OverworldSpells/ holds an ARRAY of
//                 definitions (one file per school + general.json),
//                 so a school's whole set is authored side by side.
// Layer:          System
// Collaborators:  OverworldSpellDefinition.cs (schema),
//                 OverworldSpellManager.cs (lookup),
//                 GrimoirePanel.cs (display)
// See:            overworld_spell_system_v1_1.docx §13
// ============================================================

/// <summary>Lazy loader and per-session cache for overworld spell
/// definitions. Load once per process; Reload() for hot-reload.</summary>
public static class OverworldSpellRegistry
{
    private const string SPELLS_DIR = "res://Data/OverworldSpells/";

    private static readonly Dictionary<string, OverworldSpellDefinition> _cache = new();
    private static bool _loaded = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>All loaded spells by id. Triggers load on first access.</summary>
    public static IReadOnlyDictionary<string, OverworldSpellDefinition> All
    {
        get { EnsureLoaded(); return _cache; }
    }

    /// <summary>One spell by id, or null.</summary>
    public static OverworldSpellDefinition Get(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        EnsureLoaded();
        return _cache.TryGetValue(id, out var def) ? def : null;
    }

    /// <summary>A school's Attunement definition, or null.</summary>
    public static OverworldSpellDefinition AttunementFor(string school)
    {
        EnsureLoaded();
        foreach (var def in _cache.Values)
            if (def.IsAttunement && def.School == school)
                return def;
        return null;
    }

    /// <summary>S4: every learnable definition — neither innate nor
    /// Attunement: the 8 school exemplars + the 4 Generals. This IS the
    /// acquisition pool (§11); sorted by id for stable presentation.</summary>
    public static List<OverworldSpellDefinition> Learnables()
    {
        EnsureLoaded();
        var result = new List<OverworldSpellDefinition>();
        foreach (var def in _cache.Values)
            if (!def.IsInnate && !def.IsAttunement)
                result.Add(def);
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    /// <summary>A school's innate spells (excludes the Attunement).</summary>
    public static List<OverworldSpellDefinition> InnatesFor(string school)
    {
        EnsureLoaded();
        var result = new List<OverworldSpellDefinition>();
        foreach (var def in _cache.Values)
            if (def.IsInnate && !def.IsAttunement && def.School == school)
                result.Add(def);
        // Stable presentation order regardless of file/dictionary order.
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        LoadAll();
        _loaded = true;
    }

    public static void Reload()
    {
        _loaded = false;
        _cache.Clear();
        EnsureLoaded();
    }

    // ── Internal ──────────────────────────────────────────────────────────

    private static void LoadAll()
    {
        _cache.Clear();

        using var dir = DirAccess.Open(SPELLS_DIR);
        if (dir == null)
        {
            GD.PushWarning($"[OverworldSpellRegistry] Directory not found: {SPELLS_DIR}");
            return;
        }

        foreach (var filename in dir.GetFiles())
        {
            if (!filename.EndsWith(".json"))
                continue;

            string path = SPELLS_DIR + filename;
            try
            {
                using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                if (fa == null)
                {
                    GD.PushWarning($"[OverworldSpellRegistry] Could not open {path}");
                    continue;
                }

                var defs = JsonSerializer.Deserialize<List<OverworldSpellDefinition>>(
                    fa.GetAsText(), JsonOptions);
                if (defs == null)
                    continue;

                foreach (var def in defs)
                {
                    if (def == null || string.IsNullOrEmpty(def.Id))
                    {
                        GD.PushWarning($"[OverworldSpellRegistry] {path}: entry with no 'id'; skipped.");
                        continue;
                    }
                    if (_cache.ContainsKey(def.Id))
                        GD.PushWarning($"[OverworldSpellRegistry] Duplicate id '{def.Id}' ({path}) — last wins.");
                    _cache[def.Id] = def;
                }
            }
            catch (Exception ex)
            {
                GD.PushError($"[OverworldSpellRegistry] Failed to load {path}: {ex.Message}");
            }
        }

        GD.Print($"[OverworldSpellRegistry] Loaded {_cache.Count} overworld spell definition(s).");
    }
}
