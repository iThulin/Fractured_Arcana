using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CampaignGenerator.cs
//
// Purpose:        Pure static generator that takes a campaign
//                 seed, the list of placeable archmagi, and
//                 the list of ordered regions, and produces a
//                 fully initialised CampaignState. Called once
//                 at new-game time; the result is stored in
//                 GuildSaveData and never regenerated.
//
//                 Placement rules:
//                   1. Shuffle archmagi with the seeded RNG.
//                   2. Player's own school archmage is forced
//                      to tier 2 or 3 (betrayal weight).
//                   3. Tier 3 and 2 regions are filled first;
//                      remaining archmagi go into tier 1.
//                   4. The co-conspirator is the archmage
//                      placed in the last tier-3 region
//                      (the penultimate challenge before The
//                      Convergence).
//                   5. Not every region gets an archmage —
//                      if there are fewer archmagi than
//                      placeable regions, some regions have
//                      only geographic faction encounters.
// Layer:          System
// Collaborators:  ArchmageRegistry.cs, RegionLoader.cs,
//                 WorldMapLoader.cs, GuildSaveData.cs
// ============================================================

/// <summary>Lightweight region descriptor used during campaign generation. Avoids a dependency on the full RegionDefinition.</summary>
public struct PlaceableRegion
{
    public string Id;
    public int Tier; // BaseDifficultyTier from RegionDefinition
}

/// <summary>Generates a <see cref="CampaignState"/> deterministically from a seed. Call once per new game; store the result in <see cref="GuildSaveData.Campaign"/>.</summary>
public static class CampaignGenerator
{
    // The Convergence is the final battle map — never receives an archmage.
    private const string FINAL_BATTLE_REGION = "the_convergence";

    /// <summary>
    /// Generates a complete campaign state from the given seed.
    /// </summary>
    /// <param name="campaignSeed">Deterministic seed — same seed always produces the same world.</param>
    /// <param name="playerSchool">The player's selected school (e.g. "Elementalist"). Used to enforce the betrayal placement rule.</param>
    public static CampaignState Generate(int campaignSeed, string playerSchool)
    {
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)campaignSeed;

        var state = new CampaignState
        {
            CampaignSeed = campaignSeed,
            CorruptionTickInterval = 60
        };

        // ── Build region list (excluding the final battle map) ───────────
        var regions = BuildPlaceableRegions();

        // ── Build archmage pool (placeable only — excludes villain factions) ─
        var pool = ArchmageRegistry.GetPlaceable();

        if (pool.Count == 0)
        {
            GD.PushWarning("[CampaignGenerator] No placeable archmagi found. Campaign will have no faction leaders.");
            InitializeEmptyState(state, regions);
            return state;
        }

        // ── Shuffle archmagi ─────────────────────────────────────────────
        pool = ShuffleList(pool, rng);

        // ── Enforce betrayal placement rule ──────────────────────────────
        // The player's own school archmage must not appear in a tier-1 region.
        // If they ended up at the front of the shuffled list (which maps to
        // tier-3 positions), that's fine. If they ended up at the position
        // that would land in tier-1, swap them forward.
        EnforceBetrayalPlacement(pool, regions, playerSchool);

        // ── Sort regions: tier 3 first, then 2, then 1 ──────────────────
        var sortedRegions = regions
            .OrderByDescending(r => r.Tier)
            .ToList();

        // ── Assign archmagi to regions ───────────────────────────────────
        for (int i = 0; i < sortedRegions.Count; i++)
        {
            state.RegionArchmageMap[sortedRegions[i].Id] =
                i < pool.Count ? pool[i].Id : "";
        }

        // ── Determine co-conspirator ─────────────────────────────────────
        // The last tier-3 region in the sorted list (highest index among
        // tier-3 entries) is the penultimate region before The Convergence.
        // Its archmage is the co-conspirator revealed in the intro.
        var tier3Regions = sortedRegions.Where(r => r.Tier == 3).ToList();
        if (tier3Regions.Count > 0)
        {
            // The last tier-3 region in the seeded ordering
            var coConspiractorRegion = tier3Regions[tier3Regions.Count - 1];
            state.CoConspirator = state.RegionArchmageMap.TryGetValue(
                coConspiractorRegion.Id, out var ccId) ? ccId : "";
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
    /// Builds the list of placeable regions from loaded region data.
    /// Reads all regions from RegionLoader, excludes the final battle map.
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

    /// <summary>
    /// Ensures the player's school archmage isn't placed in a tier-1 region.
    /// If they would be (i.e., their shuffled position maps to a tier-1 slot),
    /// swaps them forward into the first tier-2 slot.
    /// </summary>
    private static void EnforceBetrayalPlacement(
        List<ArchmageDefinition> pool,
        List<PlaceableRegion> regions,
        string playerSchool)
    {
        if (string.IsNullOrEmpty(playerSchool) || pool.Count == 0)
            return;

        // Count tier-3 and tier-2 regions to know which pool indices are "safe"
        int tier3Count = regions.Count(r => r.Tier == 3);
        int tier2Count = regions.Count(r => r.Tier == 2);
        int safeSlots = tier3Count + tier2Count; // indices 0 .. safeSlots-1 are safe

        // Find the player's school archmage in the pool
        int betrayalIdx = pool.FindIndex(a =>
            string.Equals(a.School, playerSchool, System.StringComparison.OrdinalIgnoreCase));

        if (betrayalIdx < 0)
            return; // player's school archmage not in pool (shouldn't happen)

        // If they're already in a safe slot, nothing to do
        if (betrayalIdx < safeSlots)
            return;

        // Swap them into the last safe slot (first tier-2 position)
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

    /// <summary>Fisher-Yates shuffle using the seeded RNG.</summary>
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
