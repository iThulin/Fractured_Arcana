using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// ArchmageRegistry.cs
//
// Purpose:        Lazy loader and per-session cache for
//                 ArchmageDefinition JSON files. Mirrors the
//                 pattern of RegionLoader — load once, cache,
//                 expose by id. Loaded from Data/Archmagi/.
// Layer:          System
// Collaborators:  ArchmageDefinition.cs (the schema),
//                 CampaignGenerator.cs (enumerates all),
//                 CampaignState.cs (looks up by id),
//                 CampusMentorPanel.cs (mentor dialogue),
//                 OverworldFactionManager.cs (faction data)
// ============================================================

/// <summary>Lazy loader and per-session cache for archmage definitions. Load once per process; clear on hot-reload.</summary>
public static class ArchmageRegistry
{
    private const string ARCHMAGI_DIR = "res://Data/Archmagi/";

    private static readonly Dictionary<string, ArchmageDefinition> _cache = new();
    private static bool _loaded = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>All loaded archmagi. Triggers load on first access.</summary>
    public static IReadOnlyDictionary<string, ArchmageDefinition> All
    {
        get { EnsureLoaded(); return _cache; }
    }

    /// <summary>Get one archmage by id. Returns null if not found.</summary>
    public static ArchmageDefinition Get(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        EnsureLoaded();
        return _cache.TryGetValue(id, out var def) ? def : null;
    }

    /// <summary>All archmagi that can be placed in regions (excludes villain factions).</summary>
    public static List<ArchmageDefinition> GetPlaceable()
    {
        EnsureLoaded();
        var result = new List<ArchmageDefinition>();
        foreach (var def in _cache.Values)
            if (!def.IsVillainFaction)
                result.Add(def);
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

    public static void ClearCache()
    {
        _cache.Clear();
        _loaded = false;
    }

    // ── Internal ──────────────────────────────────────────────────────────

    private static void LoadAll()
    {
        _cache.Clear();

        using var dir = DirAccess.Open(ARCHMAGI_DIR);
        if (dir == null)
        {
            GD.PushWarning($"[ArchmageRegistry] Directory not found: {ARCHMAGI_DIR}");
            return;
        }

        foreach (var filename in dir.GetFiles())
        {
            if (!filename.EndsWith(".json"))
                continue;

            string path = ARCHMAGI_DIR + filename;

            try
            {
                using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                if (fa == null)
                {
                    GD.PushWarning($"[ArchmageRegistry] Could not open {path}");
                    continue;
                }

                var def = JsonSerializer.Deserialize<ArchmageDefinition>(
                    fa.GetAsText(), JsonOptions);

                if (def == null || string.IsNullOrEmpty(def.Id))
                {
                    GD.PushWarning($"[ArchmageRegistry] {path} has no 'id'; skipped.");
                    continue;
                }

                _cache[def.Id] = def;
            }
            catch (Exception ex)
            {
                GD.PushError($"[ArchmageRegistry] Failed to load {path}: {ex.Message}");
            }
        }

        GD.Print($"[ArchmageRegistry] Loaded {_cache.Count} archmage definition(s).");
    }
}
