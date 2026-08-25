using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardMintService.cs
//
// Purpose:        Scribe a copy of a card you have already discovered,
//                 at the tier you have already mastered.
//
//                 This is the "Library research" verb from
//                 progression_card_acquisition_v1 §8: the DETERMINISTIC
//                 conversion the doc flagged as the most-missed omission:
//
//                   "Everything above is stochastic. Without a deterministic
//                    conversion (spend X, receive the specific blueprint you
//                    named), a player chasing an archetype is hostage to RNG
//                    across multiple cycles. Build this. It is the difference
//                    between 'slow reveal' and 'grind'."
//
//                 Priced in Arcane Splinters rather than lore + lunations,
//                 and capped per cycle. The cap is the whole safety design:
//                 unbounded minting would make the post-combat draft
//                 pointless and the cycle reseed cosmetic. You would
//                 rebuild your best deck in ten minutes and the roguelite
//                 loop would flatten. Bounded, it converts the reseed from a
//                 POWER reset into a DECK-BUILDING reset, which is the
//                 better game.
//
// Layer:          Data / Feature builder
// Collaborators:  CardMasteryService.cs (the tier you have proven),
//                 EternalLedger.UnlockedCardBlueprintIds (discovery),
//                 CycleState.MintsThisCycle (the budget),
//                 PlayerSession feature flags (Scriptorum tier ceiling),
//                 CardLibraryUi.cs (the surface)
// See:            docs/progression_card_acquisition_v1_2.md
// ============================================================

/// <summary>Report on whether one blueprint can be minted right now, and at what cost.</summary>
public readonly struct MintStatus
{
    public bool CanMint { get; init; }
    public int SplinterCost { get; init; }
    public int TopTier { get; init; }
    public int BotTier { get; init; }
    public int MintsUsed { get; init; }
    public int MintsAllowed { get; init; }

    /// <summary>One player-facing sentence naming what is missing. Empty when CanMint.</summary>
    public string Blocker { get; init; }
}

public static class CardMintService
{
    // ── Tuning ───────────────────────────────────────────────────────────

    /// <summary>Splinter price by rarity. Legendaries are absent on purpose (see Cost).</summary>
    public const int CostCommon   = 20;
    public const int CostUncommon = 35;
    public const int CostRare     = 60;

    private const string LibraryId = "arcane_library";

    // ── Budget ───────────────────────────────────────────────────────────

    /// <summary>
    /// Mints allowed this cycle, taken from the Arcane Library's tier, so 0 without one and
    /// 3 at full tier. Making the cap a building tier means the answer to "I want
    /// to mint more" is a campus investment, which is the meta-layer doing its job.
    /// </summary>
    public static int MintsAllowed(GuildSaveData save)
    {
        var lib = save?.Ledger?.Buildings?.FirstOrDefault(b =>
            b != null && string.Equals(b.Id, LibraryId, StringComparison.OrdinalIgnoreCase));

        if (lib == null || !lib.IsFunctional) return 0;
        return Math.Max(0, lib.Tier);
    }

    public static int MintsUsed(GuildSaveData save) => Math.Max(0, save?.Cycle?.MintsThisCycle ?? 0);

    public static int MintsRemaining(GuildSaveData save) =>
        Math.Max(0, MintsAllowed(save) - MintsUsed(save));

    // ── Price ────────────────────────────────────────────────────────────

    /// <summary>
    /// Splinter cost, or -1 when the card can never be minted. Legendaries return
    /// -1: they are Regalia, granted at milestones only, and letting the Library
    /// print them would undo the §6a ruling through the back door.
    /// </summary>
    public static int Cost(CardBlueprint bp) => bp?.Rarity switch
    {
        null                 => -1,
        CardRarity.Common    => CostCommon,
        CardRarity.Uncommon  => CostUncommon,
        CardRarity.Rare      => CostRare,
        CardRarity.Legendary => -1,
        _                    => CostCommon,
    };

    /// <summary>
    /// What the Scriptorum would charge to raise a copy from base to these tiers.
    /// Read straight from CardUpgradeScreen.CardUpgradeCosts so the two can never
    /// drift apart under retuning.
    ///
    /// THIS IS THE LOAD-BEARING PRICE. Charging rarity alone would sell a mastered
    /// 3/3 Common for 20✦ against the 115✦ it costs to upgrade one by hand, an 83%
    /// discount on the entire upgrade economy, three times per cycle, forever. Worse,
    /// it made mint→disenchant net-POSITIVE on Commons (a minted 3/3 has
    /// PointsSpent 5, disenchanting for 3 + 5×4 = 23 against a 20✦ mint), turning
    /// the per-cycle cap into a free-reroll budget instead of a real cost.
    ///
    /// Minting now charges full freight for the tiers. What the player buys with the
    /// convenience is skipping re-acquisition and re-casting, and the casts are
    /// already permanent, which is the actual reward.
    /// </summary>
    public static int TierCost(int topTier, int botTier)
    {
        if (topTier <= 0 && botTier <= 0) return 0;

        var half = CardUpgradeScreen.CardUpgradeCosts.HalfTierCost;
        int total = CardUpgradeScreen.CardUpgradeCosts.SharedUpgradeCost;   // buys 1/1

        for (int t = 2; t <= topTier; t++)
            total += half[Math.Min(t, half.Length - 1)];
        for (int t = 2; t <= botTier; t++)
            total += half[Math.Min(t, half.Length - 1)];

        return total;
    }

    // ── Tier ceiling ─────────────────────────────────────────────────────

    /// <summary>
    /// Highest tier a minted copy may arrive at, from the Scriptorum's
    /// card_upgrade_stage_N feature flags. Minting reproduces mastery; it does not
    /// bypass the building that grants tiers in the first place.
    /// </summary>
    public static int TierCeiling()
    {
        if (PlayerSession.HasFeature("card_upgrade_stage_3")) return 3;
        if (PlayerSession.HasFeature("card_upgrade_stage_2")) return 2;
        if (PlayerSession.HasFeature("card_upgrade_stage_1")) return 1;
        return 0;
    }

    // ── Evaluate ─────────────────────────────────────────────────────────

    /// <param name="atBase">
    /// Scribe a plain copy at 0/0 for the rarity price alone, instead of reproducing
    /// the mastered tiers. Without this option a player who took a card to 3/3 could
    /// price themselves out of ever minting it, because mastery would make the card MORE
    /// expensive to copy, which is backwards.
    /// </param>
    public static MintStatus Evaluate(GuildSaveData save, CardBlueprint bp, bool atBase = false)
    {
        int used = MintsUsed(save);
        int allowed = MintsAllowed(save);

        MintStatus Fail(string why) => new()
        {
            CanMint = false, Blocker = why, SplinterCost = Cost(bp),
            MintsUsed = used, MintsAllowed = allowed,
        };

        if (bp == null) return Fail("No card selected.");

        int baseCost = Cost(bp);
        if (baseCost < 0)
            return Fail("Legendaries are regalia. They are given at milestones, never scribed.");

        if (allowed <= 0)
            return Fail("The Arcane Library must stand before anything can be copied here.");

        var unlocked = save?.Ledger?.UnlockedCardBlueprintIds;
        if (unlocked == null || !unlocked.Contains(bp.Id))
            return Fail("You have not discovered this card. The Library can only copy what it holds.");

        if (used >= allowed)
            return Fail($"The scriptorium is spent for this timeline ({used}/{allowed}).");

        int ceiling = TierCeiling();
        var best = CardMasteryService.Best(save, bp.Id);

        int top = atBase ? 0 : Math.Min(best.BestTopTier, ceiling);
        int bot = atBase ? 0 : Math.Min(best.BestBotTier, ceiling);
        int cost = baseCost + TierCost(top, bot);

        if ((save?.Cycle?.ArcaneSplinters ?? 0) < cost)
            return Fail($"Not enough splinters. {cost} needed.");

        return new MintStatus
        {
            CanMint = true,
            SplinterCost = cost,
            TopTier = top,
            BotTier = bot,
            MintsUsed = used,
            MintsAllowed = allowed,
            Blocker = "",
        };
    }

    // ── Mint ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scribe a copy into the collection (stash, not the active deck, since slotting is
    /// still a separate gold cost in the deck editor). Re-evaluates rather than
    /// trusting the caller. Returns the new OwnedCard, or null on refusal.
    /// </summary>
    public static OwnedCard Mint(GuildSaveData save, CardBlueprint bp, bool atBase = false)
    {
        var status = Evaluate(save, bp, atBase);
        if (!status.CanMint)
        {
            GD.PrintErr($"[Mint] Refused '{bp?.Id}': {status.Blocker}");
            return null;
        }

        save.Cycle.ArcaneSplinters -= status.SplinterCost;
        save.Cycle.MintsThisCycle = MintsUsed(save) + 1;

        save.Cycle.PlayerDeck ??= new PlayerDeckSave();

        // RealCards explicitly, NOT the routed .Cards property. That property
        // forwards to DebugCards whenever the static PlayerDeckSave.UseDebugDeck is
        // set, and the card library also runs as an in-combat pause overlay, so a
        // mint taken there after CombatDebugLauncher flipped the flag would charge
        // the real economy and drop the card into the scratch collection, where the
        // player would never find it.
        save.Cycle.PlayerDeck.RealCards ??= new List<OwnedCard>();

        var owned = new OwnedCard
        {
            BlueprintId = bp.Id,
            InstanceId = Guid.NewGuid().ToString("N"),
            TopTier = status.TopTier,
            BotTier = status.BotTier,
            // Reproduce the point accounting the upgrade screen would have charged:
            // 1 for the shared 1/1 base, then 1 per half-tier above 1. Without this
            // a minted card would read as having free upgrade points remaining.
            PointsSpent = PointsFor(status.TopTier, status.BotTier),
            Grafts = new List<string>(),
            IsStarter = false,
            IsRegalia = false,
        };

        save.Cycle.PlayerDeck.RealCards.Add(owned);

        GD.Print($"[Mint] Scribed '{bp.Id}' at {owned.TopTier}/{owned.BotTier} " +
                 $"for {status.SplinterCost}✦ " +
                 $"({save.Cycle.MintsThisCycle}/{status.MintsAllowed} this cycle). " +
                 $"In the stash. Slot it in the deck editor.");
        return owned;
    }

    /// <summary>Upgrade points a copy at these tiers represents. Mirrors CardUpgradeScreen's charges.</summary>
    public static int PointsFor(int topTier, int botTier)
    {
        if (topTier <= 0 && botTier <= 0) return 0;
        return 1 + Math.Max(0, topTier - 1) + Math.Max(0, botTier - 1);
    }
}
