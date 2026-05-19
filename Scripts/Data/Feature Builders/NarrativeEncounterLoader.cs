using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// NarrativeEncounterLoader.cs
//
// Purpose:        Loads narrative encounter pools from JSON.
//                 Each region has its own pool file
//                 (<regionId>_encounters.json) which is merged
//                 with a "generic_encounters" fallback pool.
//                 Provides a terrain-aware random picker that
//                 excludes already-completed encounter IDs.
// Layer:          Loader
// Collaborators:  NarrativeEncounterData.cs (schema),
//                 EncounterRouter.cs (caller),
//                 NarrativeEncounterPanel.cs (display)
// See:            README §4.3 (Adding a Narrative Encounter)
// ============================================================

/// <summary>Pool loader + random picker for narrative encounters. Combines per-region and generic pools, filters out completed one-shots, and prefers terrain-matched entries when available.</summary>
public static class NarrativeEncounterLoader
{
    private const string ENCOUNTERS_DIR = "res://Data/Encounters/";

    private static readonly Dictionary<string, List<NarrativeEncounterData>> _cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    /// <summary>
    /// Load encounters for a region. Combines region-specific + generic pools.
    /// </summary>
    public static List<NarrativeEncounterData> LoadForRegion(string regionId)
    {
        var combined = new List<NarrativeEncounterData>();

        // Region-specific pool (if file exists)
        var regionPool = LoadFile($"{regionId}_encounters");
        if (regionPool != null) combined.AddRange(regionPool);

        // Generic pool (always included as fallback content)
        var generic = LoadFile("generic_encounters");
        if (generic != null) combined.AddRange(generic);

        return combined;
    }

    private static List<NarrativeEncounterData> LoadFile(string fileNoExt)
    {
        if (_cache.TryGetValue(fileNoExt, out var cached)) return cached;

        string path = $"{ENCOUNTERS_DIR}{fileNoExt}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.Print($"NarrativeEncounterLoader: No file at {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var encounters = JsonSerializer.Deserialize<List<NarrativeEncounterData>>(
                file.GetAsText(), JsonOptions);

            if (encounters == null) return null;

            _cache[fileNoExt] = encounters;
            GD.Print($"NarrativeEncounterLoader: Loaded {encounters.Count} from {fileNoExt}");
            return encounters;
        }
        catch (Exception e)
        {
            GD.PrintErr($"NarrativeEncounterLoader: Error loading {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pick a random encounter from a pool, filtered by terrain.
    /// Prefers terrain-matched encounters when available; falls back to generic.
    /// Filters out encounters that have already been completed (one-shot pattern).
    /// </summary>
    public static NarrativeEncounterData PickRandom(
        List<NarrativeEncounterData> pool,
        string terrainName,
        List<string> completedIds)
    {
        if (pool == null || pool.Count == 0) return null;

        var eligible = new List<NarrativeEncounterData>();
        foreach (var enc in pool)
        {
            // Skip completed unique encounters (those with an Id)
            if (!string.IsNullOrEmpty(enc.Id) && completedIds != null
                && completedIds.Contains(enc.Id))
                continue;

            eligible.Add(enc);
        }

        if (eligible.Count == 0) return null;

        // Prefer terrain matches
        var terrainMatched = new List<NarrativeEncounterData>();
        foreach (var enc in eligible)
        {
            if (enc.TerrainTags.Count == 0 ||
                enc.TerrainTags.Contains(terrainName))
                terrainMatched.Add(enc);
        }

        var finalPool = terrainMatched.Count > 0 ? terrainMatched : eligible;
        return finalPool[(int)(GD.Randi() % (uint)finalPool.Count)];
    }

    public static void ClearCache() => _cache.Clear();
}