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

        // Fragment-arc pool (always included): the six Seal Fragment recoveries.
        var fragmentArcs = LoadFile("fragment_arcs");
        if (fragmentArcs != null) combined.AddRange(fragmentArcs);

        // Ripple pool (always included): quest-triggered reactive encounters.
        // Each entry uses encounter-level RequiredFlag so it only surfaces when
        // the quest-event shim has set the matching qe_* trigger flag.
        var ripples = LoadFile("ripples");
        if (ripples != null) combined.AddRange(ripples);

        // Companion mission pool (always included): arc-stage beats. Gated at
        // pick time by CompanionArcTracker.StageEncounterEligible (recruited,
        // prior stages complete, party present when required), not by flags.
        var missions = LoadFile("companion_missions");
        if (missions != null) combined.AddRange(missions);

        return combined;
    }

    /// <summary>Find an encounter by id in the companion-mission pool. Used by
    /// the campus host to launch campus-located arc stages directly.</summary>
    public static NarrativeEncounterData FindMissionById(string encounterId)
    {
        if (string.IsNullOrEmpty(encounterId)) return null;
        var missions = LoadFile("companion_missions");
        if (missions == null) return null;
        foreach (var enc in missions)
            if (enc.Id == encounterId) return enc;
        return null;
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
        List<string> completedIds,
        GuildSaveData save = null)
    {
        if (pool == null || pool.Count == 0) return null;

        var eligible = new List<NarrativeEncounterData>();
        foreach (var enc in pool)
        {
            // Skip completed unique encounters (those with an Id)
            if (!string.IsNullOrEmpty(enc.Id) && completedIds != null
                && completedIds.Contains(enc.Id))
                continue;

            // Skip encounters whose encounter-level RequiredFlag is unmet
            if (!string.IsNullOrEmpty(enc.RequiredFlag)
                && (save == null || !save.HasFlag(enc.RequiredFlag)))
                continue;

            // Companion arc stages only surface when they are the companion's
            // CURRENT stage in a valid expedition context (recruited, prior
            // stages done, party present when required). Non-arc encounters
            // pass through untouched.
            if (save != null && !CompanionArcTracker.StageEncounterEligible(enc.Id, save))
                continue;

            eligible.Add(enc);
        }

        if (eligible.Count == 0) return null;

        // Companion arc beats take priority over ambient content: the player
        // brought this companion along on purpose, and the loom answers.
        if (save != null)
        {
            var arcBeats = new List<NarrativeEncounterData>();
            foreach (var enc in eligible)
                if (CompanionArcTracker.IsStageEncounter(enc.Id))
                    arcBeats.Add(enc);
            if (arcBeats.Count > 0)
                return arcBeats[(int)(GD.Randi() % (uint)arcBeats.Count)];
        }

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