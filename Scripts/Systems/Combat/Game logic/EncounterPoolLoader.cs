using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// ============================================================
// EncounterPoolLoader.cs
//
// Purpose:        Parses the "encounterPools" block of region
//                 JSON into tier × terrain → enemy-composition
//                 tables, and exposes a Pick() to materialise
//                 an EncounterDefinition from the pool given a
//                 tier and terrain context.
// Layer:          Loader
// Collaborators:  RegionDefinition.cs (host JSON file),
//                 EncounterDefinition.cs (produced output),
//                 EnemyArchetype.cs (composition string → enum),
//                 EncounterRouter.cs (caller)
// See:            README §4.2 (Adding a Region)
// ============================================================

/// <summary>Raw JSON shape for one enemy slot in a pool's composition list. The <c>archetype</c> string must match an <see cref="EnemyArchetype"/> enum name (case-insensitive).</summary>
public class EnemySlotData
{
    [JsonPropertyName("archetype")]
    public string Archetype { get; set; } = "Soldier";
}

/// <summary>
/// One named composition — a flat list of enemy slots.
/// e.g. { "name": "patrol", "enemies": [{"archetype":"Soldier"},{"archetype":"Ranger"}] }
/// </summary>
public class CompositionData
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("enemies")]
    public List<EnemySlotData> Enemies { get; set; } = new();
}

/// <summary>
/// All compositions for one tier (skirmish / battle / siege / ambush).
/// </summary>
public class TierPoolData
{
    [JsonPropertyName("skirmish")]
    public List<CompositionData> Skirmish { get; set; } = new();

    [JsonPropertyName("battle")]
    public List<CompositionData> Battle { get; set; } = new();

    [JsonPropertyName("siege")]
    public List<CompositionData> Siege { get; set; } = new();

    [JsonPropertyName("ambush")]
    public List<CompositionData> Ambush { get; set; } = new();
}

/// <summary>
/// Loads and caches encounter pools from region JSON.
/// Picks a random composition for a given tier + terrain combo.
/// </summary>
public static class EncounterPoolLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    // Cache: regionId → TierPoolData
    private static readonly Dictionary<string, TierPoolData> _cache = new();

    /// <summary>
    /// Loads the encounter pool for a region. Returns null if not found.
    /// The pool is expected at the "encounterPools" key in the region JSON.
    /// </summary>
    public static TierPoolData Load(string regionId)
    {
        if (_cache.TryGetValue(regionId, out var cached)) return cached;

        string path = $"res://Data/Regions/{regionId}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.PrintErr($"EncounterPoolLoader: No region file at {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            // Parse the whole file as a generic JSON document so we can extract
            // just the encounterPools key without duplicating RegionDefinition fields.
            using var doc = JsonDocument.Parse(file.GetAsText());
            if (!doc.RootElement.TryGetProperty("encounterPools", out var poolEl))
            {
                GD.Print($"EncounterPoolLoader: No 'encounterPools' key in {path} — using defaults.");
                return null;
            }

            var pool = JsonSerializer.Deserialize<TierPoolData>(
                poolEl.GetRawText(), JsonOptions);

            if (pool != null)
                _cache[regionId] = pool;

            return pool;
        }
        catch (Exception e)
        {
            GD.PrintErr($"EncounterPoolLoader: Error loading {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pick a random EncounterDefinition for the given region, tier, terrain,
    /// and difficulty multiplier.
    ///
    /// Falls back to a hardcoded default if the region has no pool data.
    /// </summary>
    public static EncounterDefinition Pick(
        string regionId,
        EncounterTier tier,
        string terrainType,
        float difficultyMult = 1.0f)
    {
        var pool = Load(regionId);
        var compositions = GetTierList(pool, tier);

        // If no compositions found, fall back to a sensible default
        if (compositions == null || compositions.Count == 0)
        {
            GD.Print($"EncounterPoolLoader: No compositions for {regionId}/{tier} — using fallback.");
            return BuildFallback(tier, regionId, terrainType, difficultyMult);
        }

        // Pick a random composition from the list
        int idx = (int)(GD.Randi() % (uint)compositions.Count);
        var comp = compositions[idx];

        return BuildDefinition(comp, tier, regionId, terrainType, difficultyMult);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<CompositionData> GetTierList(TierPoolData pool, EncounterTier tier)
    {
        if (pool == null) return null;
        return tier switch
        {
            EncounterTier.Skirmish => pool.Skirmish,
            EncounterTier.Battle => pool.Battle,
            EncounterTier.Siege => pool.Siege,
            EncounterTier.Ambush => pool.Ambush,
            _ => pool.Battle,
        };
    }

    private static EncounterDefinition BuildDefinition(
        CompositionData comp,
        EncounterTier tier,
        string regionId,
        string terrainType,
        float difficultyMult)
    {
        var def = new EncounterDefinition
        {
            Id = $"{regionId}_{tier}_{comp.Name}",
            DisplayName = comp.Name,
            Tier = tier,
            RegionId = regionId,
            TerrainType = terrainType,
            DifficultyMult = difficultyMult,
        };

        foreach (var slot in comp.Enemies)
        {
            if (Enum.TryParse<EnemyArchetype>(slot.Archetype, ignoreCase: true, out var archetype))
                def.Enemies.Add(new EnemySlot(archetype, difficultyMult));
            else
                GD.PrintErr($"EncounterPoolLoader: Unknown archetype '{slot.Archetype}' in {comp.Name}");
        }

        return def;
    }

    /// <summary>
    /// Hardcoded fallback compositions — used when a region has no pool data,
    /// or as the default before any JSON is authored.
    /// Mirrors QueueDefaultEncounter() in CombatManager.
    /// </summary>
    private static EncounterDefinition BuildFallback(
        EncounterTier tier,
        string regionId,
        string terrainType,
        float difficultyMult)
    {
        var def = new EncounterDefinition
        {
            Id = $"fallback_{tier}",
            DisplayName = $"Fallback {tier}",
            Tier = tier,
            RegionId = regionId,
            TerrainType = terrainType,
            DifficultyMult = difficultyMult,
        };

        var slots = tier switch
        {
            EncounterTier.Skirmish => new[] { EnemyArchetype.Soldier, EnemyArchetype.Ranger },
            EncounterTier.Siege => new[] { EnemyArchetype.Brute, EnemyArchetype.Defender,
                                              EnemyArchetype.Ranger, EnemyArchetype.Wizard },
            EncounterTier.Ambush => new[] { EnemyArchetype.Soldier, EnemyArchetype.Ranger,
                                              EnemyArchetype.Soldier },
            _ => new[] { EnemyArchetype.Soldier, EnemyArchetype.Ranger,
                                              EnemyArchetype.Wizard },  // Battle default
        };

        foreach (var a in slots)
            def.Enemies.Add(new EnemySlot(a, difficultyMult));

        return def;
    }
}
