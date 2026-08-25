using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatLootTable.cs
//
// Purpose:        Q4.4, combat item drops. The spec's "v1 tier
//                 tables carried" were never actually built (see
//                 the Q4 reconciliation in the 2026-08-13 session
//                 log); until now, encounter choices were the only
//                 item faucet in the game. This is the primary
//                 faucet: a post-victory roll keyed to territory
//                 tier and encounter tier. Q4 STARTING VALUES.
// Layer:          Data (FeatureBuilders)
// Collaborators:  ExpeditionManager (combat-victory return, the
//                 sole caller), ItemDatabase, ArmoryData.
// Rules:          Legendary never drops here. Relics have their
//                 own routing (ArchmageRelics) and the Auction
//                 House rule stands. Siege/Boss guarantee a roll;
//                 Siege ("elite") rolls twice, keeps both.
// ============================================================

/// <summary>Post-combat drop table. Chance and rarity band scale with the
/// territory tier under the fight; hard encounters pay better.</summary>
public static class CombatLootTable
{
    // ── Tuning (Q4 starting values) ──────────────────────────────────────

    /// <summary>Drop chance (percent) by territory tier (index clamped 1–3)
    /// for ordinary encounters. Siege and Boss skip the chance gate.</summary>
    private static readonly int[] DropChanceByTier = { 0, 20, 28, 36 };

    /// <summary>Rarity weights (Common, Uncommon, Rare) by territory tier.</summary>
    private static readonly (int c, int u, int r)[] RarityByTier =
    {
        (0, 0, 0),      // unused
        (60, 35, 5),    // tier 1
        (40, 45, 15),   // tier 2
        (25, 50, 25),   // tier 3
    };

    /// <summary>Roll the drops for a won fight. Returns 0–2 definitions
    /// (already resolved, caller adds to the Armory and toasts).</summary>
    public static List<ItemDefinition> Roll(int territoryTier, EncounterTier encounterTier)
    {
        var drops = new List<ItemDefinition>();
        int tier = Mathf.Clamp(territoryTier, 1, 3);

        var rng = new RandomNumberGenerator();
        rng.Randomize();

        bool guaranteed = encounterTier == EncounterTier.Siege
                          || encounterTier == EncounterTier.Boss;
        int rolls = encounterTier == EncounterTier.Siege ? 2 : 1;

        for (int i = 0; i < rolls; i++)
        {
            if (!guaranteed && rng.RandiRange(1, 100) > DropChanceByTier[tier])
                continue;
            var def = PickByRarity(rng, tier, drops);
            if (def != null) drops.Add(def);
        }
        return drops;
    }

    private static ItemDefinition PickByRarity(RandomNumberGenerator rng, int tier,
        List<ItemDefinition> exclude)
    {
        var (c, u, r) = RarityByTier[tier];
        int roll = rng.RandiRange(1, c + u + r);
        string rarity = roll <= c ? "Common" : roll <= c + u ? "Uncommon" : "Rare";

        var all = ItemDatabase.GetAll();
        if (all == null || all.Count == 0) return null;

        var band = all.Where(d => d.Rarity == rarity && !exclude.Contains(d)).ToList();
        if (band.Count == 0)
            band = all.Where(d => d.Rarity != "Legendary" && !exclude.Contains(d)).ToList();
        if (band.Count == 0) return null;

        return band[rng.RandiRange(0, band.Count - 1)];
    }
}
