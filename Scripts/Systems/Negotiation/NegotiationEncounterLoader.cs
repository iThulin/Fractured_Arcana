using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// NegotiationEncounterLoader.cs
//
// Purpose:        Loads NegotiationEncounterData from
//                 Data/Negotiations/*.json. Per-session cache.
// Layer:          Loader
// Collaborators:  NpcArchetype.cs (NegotiationEncounterData
//                 schema), NegotiationManager.cs (caller)
// See:            README §6 (Negotiation)
// ============================================================

/// <summary>Lazy loader + per-session cache for negotiation encounter JSON. Each encounter file is read at most once per process.</summary>
public static class NegotiationEncounterLoader
{
    private const string DIR = "res://Data/Negotiations/";

    private static readonly Dictionary<string, NegotiationEncounterData> _cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Returns a PER-TABLE CLONE of the cached encounter. The cache
    /// keeps the pristine authored data; every table gets its own copy, so
    /// runtime mutations (revealed hidden terms, Beguile-shifted starting
    /// tension, injected tuition terms) can never leak into the next visit
    /// to the same encounter, which they previously did.</summary>
    public static NegotiationEncounterData Load(string id)
    {
        var pristine = LoadPristine(id);
        if (pristine == null) return null;
        return JsonSerializer.Deserialize<NegotiationEncounterData>(
            JsonSerializer.Serialize(pristine, JsonOptions), JsonOptions);
    }

    private static NegotiationEncounterData LoadPristine(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;

        string path = $"{DIR}{id}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.PrintErr($"NegotiationEncounterLoader: No file at {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var data = JsonSerializer.Deserialize<NegotiationEncounterData>(
                file.GetAsText(), JsonOptions);

            if (data != null) _cache[id] = data;
            GD.Print($"NegotiationEncounterLoader: Loaded '{id}'");
            return data;
        }
        catch (Exception e)
        {
            GD.PrintErr($"NegotiationEncounterLoader: Error loading {id}: {e.Message}");
            return null;
        }
    }

    /// <summary>Quiet existence probe for candidate pooling: no error spam
    /// for the (expected) region-specific files that don't exist. Returns the
    /// PRISTINE cached instance (read-only use only; PickForTerrain re-Loads
    /// by id so the winner is handed out as a proper clone).</summary>
    private static NegotiationEncounterData TryLoad(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        if (!FileAccess.FileExists($"{DIR}{id}.json")) return null;
        return LoadPristine(id);
    }

    /// <summary>Suffixes tried per region, and the generic pool. v2: the
    /// archetype-generic encounters make all six NPC archetypes reachable;
    /// previously only {region}_commander and generic_merchant could ever
    /// be picked, so Scholar/Opportunist/Idealist/Survivor were dead data.</summary>
    private static readonly string[] RegionSuffixes =
        { "commander", "merchant", "scholar", "opportunist", "idealist", "survivor" };
    private static readonly string[] GenericPool =
    {
        "generic_merchant", "generic_scholar", "generic_opportunist",
        "generic_idealist", "generic_survivor",
    };

    /// <summary>
    /// Pick a random negotiation encounter appropriate for a terrain type.
    /// Region-specific encounters ({regionId}_{archetype}) are weighted
    /// double so authored flavor wins over the generic pool when it exists.
    /// </summary>
    public static NegotiationEncounterData PickForTerrain(string terrain, string regionId)
    {
        var available = new List<NegotiationEncounterData>();

        // Region-authored encounters first, each counted twice for weight.
        foreach (var suffix in RegionSuffixes)
        {
            var data = TryLoad($"{regionId}_{suffix}");
            if (data != null) { available.Add(data); available.Add(data); }
        }

        // The generic archetype pool.
        foreach (var id in GenericPool)
        {
            var data = TryLoad(id);
            if (data != null) available.Add(data);
        }

        if (available.Count == 0) return null;
        // Pick from pristine instances, then hand out a clone of the winner.
        var picked = available[(int)(GD.Randi() % (uint)available.Count)];
        return Load(picked.Id);
    }

    public static void ClearCache() => _cache.Clear();
}
