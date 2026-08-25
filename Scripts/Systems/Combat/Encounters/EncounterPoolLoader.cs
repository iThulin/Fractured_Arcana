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
//                 UnitRegistry.cs (token → unit id resolution),
//                 EncounterRouter.cs (caller)
// See:            README §4.2 (Adding a Region)
// ============================================================

/// <summary>Raw JSON shape for one enemy slot in a pool's composition list. The
/// <c>archetype</c> string is either an exact UnitRegistry id ("generic_soldier",
/// "conductor_honored_dead") or a legacy archetype name ("Soldier") resolved
/// through the registry's alias table (units doc §6 step 2). The JSON key stays
/// "archetype" so every authored pool keeps working unmodified.</summary>
public class EnemySlotData
{
    [JsonPropertyName("archetype")]
    public string Archetype { get; set; } = "Soldier";
}

/// <summary>
/// One named composition, which is a flat list of enemy slots.
/// e.g. { "name": "patrol", "enemies": [{"archetype":"Soldier"},{"archetype":"Ranger"}] }
/// </summary>
public class CompositionData
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("enemies")]
    public List<EnemySlotData> Enemies { get; set; } = new();

    // E5: optional per-composition battlefield recipe. Absent = region terrain default.
    [JsonPropertyName("map_recipe")]
    public string MapRecipe { get; set; } = "";

    // ── O-track: both optional, both absent on every pool authored to date ──
    [JsonPropertyName("objective")]
    public ObjectiveData Objective { get; set; } = null;

    [JsonPropertyName("waves")]
    public List<WaveData> Waves { get; set; } = new();
}

/// <summary>Raw JSON shape for a composition's optional objective block.</summary>
public class ObjectiveData
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "annihilate";

    [JsonPropertyName("rounds")]
    public int Rounds { get; set; } = 0;

    [JsonPropertyName("wardUnitId")]
    public string WardUnitId { get; set; } = "";

    [JsonPropertyName("breachLimit")]
    public int BreachLimit { get; set; } = 2;

    [JsonPropertyName("zoneAnchor")]
    public string ZoneAnchor { get; set; } = "player_spawn";

    [JsonPropertyName("zoneRadius")]
    public int ZoneRadius { get; set; } = 2;

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

/// <summary>Raw JSON shape for one reinforcement wave.</summary>
public class WaveData
{
    [JsonPropertyName("round")]
    public int Round { get; set; } = 3;

    [JsonPropertyName("enemies")]
    public List<EnemySlotData> Enemies { get; set; } = new();

    [JsonPropertyName("announce")]
    public string Announce { get; set; } = "";
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
        if (_cache.TryGetValue(regionId, out var cached))
            return cached;

        string path = $"res://Data/Regions/{regionId}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.PrintErr($"EncounterPoolLoader: No region file at {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null)
                return null;

            // Parse the whole file as a generic JSON document so we can extract
            // just the encounterPools key without duplicating RegionDefinition fields.
            using var doc = JsonDocument.Parse(file.GetAsText());
            if (!doc.RootElement.TryGetProperty("encounterPools", out var poolEl))
            {
                GD.Print($"EncounterPoolLoader: No 'encounterPools' key in {path}. Using defaults.");
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
            GD.Print($"EncounterPoolLoader: No compositions for {regionId}/{tier}. Using fallback.");
            return BuildFallback(tier, regionId, terrainType, difficultyMult);
        }

        // Pick a random composition from the list
        int idx = (int)(GD.Randi() % (uint)compositions.Count);
        var comp = compositions[idx];

        return BuildDefinition(comp, tier, regionId, terrainType, difficultyMult);
    }

    /// <summary>
    /// Materialise an EncounterDefinition from an ARCHMAGE faction pool. This is the
    /// parallel of Pick() for region pools. Returns null when the archmage has no
    /// authored group for this tier (or all slots fail to parse), so the caller
    /// can fall back to the region pool. RegionId is passed through for combat-map
    /// theming, NOT taken from the archmage (archmages aren't regions).
    /// </summary>
    public static EncounterDefinition PickFromArchmage(
        ArchmageDefinition arch,
        string regionId,
        EncounterTier tier,
        string terrainType,
        float difficultyMult = 1.0f)
    {
        if (arch == null)
            return null;

        var groups = arch.FactionEncounters?.GetTier(tier.ToString());
        if (groups == null || groups.Count == 0)
            return null; // no themed group at this tier, so the caller uses the region pool

        int idx = (int)(GD.Randi() % (uint)groups.Count);
        var group = groups[idx];

        float groupMult = difficultyMult * (group.DifficultyMult <= 0f ? 1f : group.DifficultyMult);

        var def = new EncounterDefinition
        {
            Id = $"{arch.Id}_{tier}_{group.Name}",
            DisplayName = group.Name,
            Tier = tier,
            RegionId = regionId,
            TerrainType = terrainType,
            DifficultyMult = groupMult,
        };

        foreach (var slot in group.Enemies)
        {
            if (UnitRegistry.TryResolveId(slot.Archetype, out var unitId))
            {
                float slotMult = groupMult * (slot.DifficultyMult <= 0f ? 1f : slot.DifficultyMult);
                def.Enemies.Add(new EnemySlot(unitId, slotMult));
            }
            else
            {
                GD.PrintErr($"EncounterPoolLoader: Unknown unit '{slot.Archetype}' in archmage group {arch.Id}/{group.Name}");
            }
        }

        return def.Enemies.Count > 0 ? def : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<CompositionData> GetTierList(TierPoolData pool, EncounterTier tier)
    {
        if (pool == null)
            return null;
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
            MapRecipe = comp.MapRecipe ?? "",
        };

        foreach (var slot in comp.Enemies)
        {
            if (UnitRegistry.TryResolveId(slot.Archetype, out var unitId))
                def.Enemies.Add(new EnemySlot(unitId, difficultyMult));
            else
                GD.PrintErr($"EncounterPoolLoader: Unknown unit '{slot.Archetype}' in pool composition '{comp.Name}'");
        }

        ApplyObjectiveAndWaves(def, comp, difficultyMult);
        return def;
    }

    /// <summary>
    /// Translates a composition's optional <c>objective</c> / <c>waves</c> blocks
    /// onto the definition. Validation is LOUD and happens HERE, at load, where
    /// the author can see it. It never happens at fight time, where a malformed objective
    /// would silently degrade into an ordinary kill-fight and look like a design
    /// choice. A rejected objective leaves the encounter as annihilate; a rejected
    /// wave is dropped and the rest of the fight is unaffected.
    /// </summary>
    private static void ApplyObjectiveAndWaves(
        EncounterDefinition def, CompositionData comp, float difficultyMult)
    {
        if (def == null || comp == null)
            return;

        if (comp.Objective != null)
        {
            string kind = (comp.Objective.Kind ?? "").Trim().ToLowerInvariant();

            if (!CombatObjectiveDef.IsKnownKind(kind))
            {
                GD.PrintErr($"EncounterPoolLoader: composition '{comp.Name}' declares unknown " +
                            $"objective kind '{comp.Objective.Kind}'. Objective IGNORED " +
                            "(this fight will run as annihilate).");
            }
            else if (!CombatObjectiveDef.IsImplementedKind(kind))
            {
                GD.PrintErr($"EncounterPoolLoader: composition '{comp.Name}' declares objective " +
                            $"kind '{kind}', which this build does not implement yet. Objective " +
                            "IGNORED (this fight will run as annihilate).");
            }
            else if (kind != CombatObjectiveDef.KindAnnihilate)
            {
                bool needsRounds = kind == CombatObjectiveDef.KindSurvive
                                || kind == CombatObjectiveDef.KindHoldZone;
                if (needsRounds && comp.Objective.Rounds <= 0)
                {
                    GD.PrintErr($"EncounterPoolLoader: composition '{comp.Name}' objective " +
                                $"'{kind}' needs rounds > 0 (got {comp.Objective.Rounds}). " +
                                "Objective IGNORED.");
                }
                else
                {
                    def.Objective = new CombatObjectiveDef
                    {
                        Kind = kind,
                        Rounds = comp.Objective.Rounds,
                        WardUnitId = comp.Objective.WardUnitId ?? "",
                        BreachLimit = comp.Objective.BreachLimit,
                        ZoneAnchor = comp.Objective.ZoneAnchor ?? "player_spawn",
                        ZoneRadius = comp.Objective.ZoneRadius,
                        Description = comp.Objective.Description ?? "",
                    };
                }
            }
        }

        if (comp.Waves != null)
        {
            foreach (var w in comp.Waves)
            {
                if (w == null)
                    continue;

                if (w.Round <= 1)
                {
                    GD.PrintErr($"EncounterPoolLoader: composition '{comp.Name}' has a wave at " +
                                $"round {w.Round}; waves must arrive at round 2 or later " +
                                "(round 1 is the initial roster's job). Wave DROPPED.");
                    continue;
                }

                var wave = new ReinforcementWave
                {
                    Round = w.Round,
                    Announce = w.Announce ?? "",
                };

                foreach (var slot in w.Enemies ?? new List<EnemySlotData>())
                {
                    if (UnitRegistry.TryResolveId(slot.Archetype, out var waveUnitId))
                        wave.Enemies.Add(new EnemySlot(waveUnitId, difficultyMult));
                    else
                        GD.PrintErr($"EncounterPoolLoader: Unknown unit '{slot.Archetype}' in " +
                                    $"wave (round {w.Round}) of composition '{comp.Name}'");
                }

                if (wave.Enemies.Count > 0)
                    def.Waves.Add(wave);
                else
                    GD.PrintErr($"EncounterPoolLoader: wave at round {w.Round} of composition " +
                                $"'{comp.Name}' resolved to zero units. Wave DROPPED.");
            }

            def.Waves.Sort((a, b) => a.Round.CompareTo(b.Round));
        }

        if (def.Objective != null || def.Waves.Count > 0)
        {
            string kindLabel = def.Objective == null
                ? CombatObjectiveDef.KindAnnihilate
                : def.Objective.Kind;
            int roundsLabel = def.Objective == null ? 0 : def.Objective.Rounds;
            GD.Print($"[Objective] Composition '{comp.Name}': kind={kindLabel}, " +
                     $"rounds={roundsLabel}, waves={def.Waves.Count}.");
        }
    }

    /// <summary>
    /// Hardcoded fallback compositions, used when a region has no pool data,
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
            EncounterTier.Skirmish => new[] { "generic_soldier", "generic_ranger" },
            EncounterTier.Siege => new[] { "generic_brute", "generic_defender",
                                              "generic_ranger", "generic_wizard" },
            EncounterTier.Ambush => new[] { "generic_soldier", "generic_ranger",
                                              "generic_soldier" },
            _ => new[] { "generic_soldier", "generic_ranger",
                                              "generic_wizard" },  // Battle default
        };

        foreach (var id in slots)
            def.Enemies.Add(new EnemySlot(id, difficultyMult));

        return def;
    }
}
