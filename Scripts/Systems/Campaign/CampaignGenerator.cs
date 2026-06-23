using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CampaignGenerator.cs
//
// Purpose:        Pure static generator that takes a campaign
//                 seed, the player's school, and a set of
//                 placeable regions, and produces a fully
//                 initialised CampaignState (archmage placement,
//                 dispositions, corruption seed). Called once at
//                 cycle start.
//
//                 As of the open-world refactor, the REGION SET
//                 is supplied by the caller (StrategicMapGenerator)
//                 rather than read from RegionLoader directly —
//                 the placement rules now run on the generated
//                 strategic geography. The parameterless overload
//                 is preserved for any legacy/standalone caller
//                 and simply reads all regions as before.
//
//                 Placement rules (UNCHANGED):
//                   1. Shuffle archmagi with the seeded RNG.
//                   2. Player's own-school archmage forced out of
//                      tier 1 (betrayal weight).
//                   3. Tier 3 and 2 regions filled first.
//                   4. Co-conspirator = archmage in the last
//                      tier-3 region.
//                   5. Fewer archmagi than regions ⇒ some regions
//                      get no archmage.
// Layer:          System
// Collaborators:  ArchmageRegistry.cs, RegionLoader.cs,
//                 StrategicMapGenerator.cs (primary caller now),
//                 CampaignState.cs
// ============================================================

/// <summary>Lightweight region descriptor used during campaign generation.</summary>
public struct PlaceableRegion
{
    public string Id;
    public int Tier; // distance-derived tier (open world) or BaseDifficultyTier (legacy)
}

/// <summary>Generates a <see cref="CampaignState"/> deterministically from a seed.</summary>
public static class CampaignGenerator
{
    private const string FINAL_BATTLE_REGION = "the_convergence";

    /// <summary>
    /// Legacy entry point: generates from ALL loaded regions (reads
    /// RegionLoader). Preserved for standalone use; the open-world path
    /// uses the overload that accepts a pre-built region set.
    /// </summary>
    public static CampaignState Generate(int campaignSeed, string playerSchool)
        => Generate(campaignSeed, playerSchool, BuildPlaceableRegions());

    /// <summary>
    /// Open-world entry point: generates over a caller-supplied set of
    /// placeable regions (StrategicMapGenerator passes the placed provinces
    /// with distance-derived tiers). All placement rules are identical to
    /// the legacy path — only the region SOURCE differs.
    /// </summary>
    /// <param name="campaignSeed">Deterministic seed.</param>
    /// <param name="playerSchool">Player's school (betrayal placement rule).</param>
    /// <param name="regions">Placeable regions with tiers, supplied by the caller.</param>
    public static CampaignState Generate(int campaignSeed, string playerSchool,
                                         List<PlaceableRegion> regions)
    {
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)campaignSeed;

        var state = new CampaignState
        {
            CampaignSeed = campaignSeed,
            CorruptionTickInterval = 60
        };

        regions ??= new List<PlaceableRegion>();

        // ── Archmage pool (placeable only — excludes villain factions) ───
        var pool = ArchmageRegistry.GetPlaceable();

        if (pool.Count == 0)
        {
            GD.PushWarning("[CampaignGenerator] No placeable archmagi found.");
            InitializeEmptyState(state, regions);
            return state;
        }

        // ── Shuffle, then enforce betrayal placement ─────────────────────
        pool = ShuffleList(pool, rng);
        EnforceBetrayalPlacement(pool, regions, playerSchool);

        // ── Sort regions: tier 3 first, then 2, then 1 ───────────────────
        var sortedRegions = regions.OrderByDescending(r => r.Tier).ToList();

        // ── Assign archmagi to regions ───────────────────────────────────
        for (int i = 0; i < sortedRegions.Count; i++)
        {
            state.RegionArchmageMap[sortedRegions[i].Id] =
                i < pool.Count ? pool[i].Id : "";
        }

        // ── Co-conspirator = archmage in the last tier-3 region ──────────
        var tier3Regions = sortedRegions.Where(r => r.Tier == 3).ToList();
        if (tier3Regions.Count > 0)
        {
            var coConspiratorRegion = tier3Regions[tier3Regions.Count - 1];
            state.CoConspirator = state.RegionArchmageMap.TryGetValue(
                coConspiratorRegion.Id, out var ccId) ? ccId : "";
        }

        // ── Initialise dispositions and corruption ───────────────────────
        foreach (var pair in state.RegionArchmageMap)
        {
            if (!string.IsNullOrEmpty(pair.Value))
                state.Dispositions[pair.Value] = ArchmageDisposition.Unknown;

            state.CorruptionLevels[pair.Key] = 0;
        }

        GD.Print($"[CampaignGenerator] Campaign generated. Seed={campaignSeed}, " +
                 $"CoConspirator='{state.CoConspirator}', " +
                 $"Regions={state.RegionArchmageMap.Count}, " +
                 $"Archmagi placed={pool.Count}");

        foreach (var pair in state.RegionArchmageMap)
            GD.Print($"  {pair.Key} → {(string.IsNullOrEmpty(pair.Value) ? "(no archmage)" : pair.Value)}");

        return state;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Legacy region source: all regions from RegionLoader, excluding the
    /// final battle map, with their authored BaseDifficultyTier.
    /// </summary>
    private static List<PlaceableRegion> BuildPlaceableRegions()
    {
        var result = new List<PlaceableRegion>();
        var allRegions = RegionLoader.LoadAll();

        foreach (var region in allRegions)
        {
            if (region.Id == FINAL_BATTLE_REGION)
                continue;

            result.Add(new PlaceableRegion
            {
                Id = region.Id,
                Tier = region.BaseDifficultyTier
            });
        }

        return result;
    }

    private static void EnforceBetrayalPlacement(
        List<ArchmageDefinition> pool,
        List<PlaceableRegion> regions,
        string playerSchool)
    {
        if (string.IsNullOrEmpty(playerSchool) || pool.Count == 0)
            return;

        int tier3Count = regions.Count(r => r.Tier == 3);
        int tier2Count = regions.Count(r => r.Tier == 2);
        int safeSlots = tier3Count + tier2Count;

        int betrayalIdx = pool.FindIndex(a =>
            string.Equals(a.School, playerSchool, System.StringComparison.OrdinalIgnoreCase));

        if (betrayalIdx < 0)
            return;
        if (betrayalIdx < safeSlots)
            return;

        int swapTarget = Mathf.Min(tier3Count, pool.Count - 1);
        if (swapTarget == betrayalIdx)
            return;

        (pool[swapTarget], pool[betrayalIdx]) = (pool[betrayalIdx], pool[swapTarget]);

        GD.Print($"[CampaignGenerator] Betrayal placement: moved '{pool[swapTarget].Id}' " +
                 $"to tier-2 slot (index {swapTarget}).");
    }

    private static void InitializeEmptyState(CampaignState state, List<PlaceableRegion> regions)
    {
        foreach (var r in regions)
        {
            state.RegionArchmageMap[r.Id] = "";
            state.CorruptionLevels[r.Id] = 0;
        }
    }

    private static List<T> ShuffleList<T>(List<T> list, RandomNumberGenerator rng)
    {
        var result = new List<T>(list);
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.Randi() % (uint)(i + 1));
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }
}
