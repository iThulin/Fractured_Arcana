using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardCommissionService.cs
//
// Purpose:        The DISCOVERY pity-timer — the last unbuilt half of the
//                 "Library research" verb from
//                 progression_card_acquisition_v1 §8:
//
//                   "Everything above is stochastic. Without a deterministic
//                    conversion — spend X, receive the specific blueprint you
//                    NAMED — a player chasing an archetype is hostage to RNG
//                    across multiple cycles. Build this. Omitting it will be
//                    the most-reported complaint about the whole system; it is
//                    the difference between 'slow reveal' and 'grind'."
//
//                 Where minting (CardMintService) COPIES a card you have
//                 already discovered, this DISCOVERS one you have not: the
//                 player names a locked Rare, pays gold, and it unlocks after
//                 a fixed number of lunations. That delay is the whole design
//                 — an instant unlock would make every Rare free the moment
//                 the Forbidden Archives are built, collapsing the slow reveal
//                 the §5 seed deliberately created by locking Rares.
//
//                 Home: the Arcane Library's "forbidden_archives" T3 feature
//                 flag — granted by arcane_library.json and, until now, set
//                 and never consumed (v1 §1, session_log_2026-08-05).
//
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.CardCommissions (the in-flight list),
//                 EternalLedger.UnlockedCardBlueprintIds (the payoff),
//                 CardDatabase (blueprint lookup + rarity),
//                 MarginaliaService (excluded — its own verb owns those),
//                 PlayerSession.HasFeature("forbidden_archives") (the gate),
//                 StrategicView.RunLunationTick (the once-per-lunation tick),
//                 CardLibraryUi.cs (the surface)
// See:            docs/progression_card_acquisition_v1.md §8;
//                 docs/progression_card_acquisition_v1_2.md B4
// ============================================================

/// <summary>Report on whether one blueprint can be commissioned right now, and at what cost.</summary>
public readonly struct CommissionStatus
{
    public bool CanCommission { get; init; }
    public int GoldCost { get; init; }
    public int Lunations { get; init; }
    public int InFlight { get; init; }
    public int MaxConcurrent { get; init; }

    /// <summary>Lunations left on an EXISTING commission for this card, or -1 if none is in flight.</summary>
    public int PendingLunations { get; init; }

    /// <summary>One player-facing sentence naming what is missing. Empty when CanCommission.</summary>
    public string Blocker { get; init; }
}

public static class CardCommissionService
{
    // ── Tuning (starting values — empirical, tune in place) ───────────────
    //
    // Gold, not splinters: minting already owns the splinter economy, and
    // keeping the two verbs on different currencies stops one from cannibalising
    // the other. Discovery is a research EXPENDITURE (gold + time); copying is a
    // scriptorium OUTPUT (splinters). Confidence on the exact numbers is
    // moderate — they are playtest anchors, not commitments. If archetype-chasing
    // still feels like grind, cut ResearchLunations before cutting gold; if Rares
    // arrive too cheaply, raise gold before shortening the timer.

    /// <summary>Lunations a commission takes to deliver. A 12-lunation cycle means
    /// commissioning early lands the card mid-cycle — a real wait, not an instant.</summary>
    public const int ResearchLunations = 3;

    public const int CostRareGold     = 250;
    public const int CostUncommonGold = 120;   // edge case — Uncommons are seeded unlocked
    public const int CostCommonGold   = 60;    // edge case — Commons are seeded unlocked

    private const string LibraryId   = "arcane_library";
    private const string FeatureFlag = "forbidden_archives";

    // ── Availability ──────────────────────────────────────────────────────

    /// <summary>The verb is available only with the Arcane Library's Forbidden
    /// Archives (T3 feature flag). Recomputed features can be stale off the campus
    /// path, so callers on other screens should refresh before trusting this.</summary>
    public static bool ArchivesAvailable() => PlayerSession.HasFeature(FeatureFlag);

    /// <summary>Max commissions in flight at once — the Arcane Library's tier, so 3
    /// with the Forbidden Archives (which only exist at T3). A concurrency cap is
    /// what keeps the pity-timer a deliberate choice rather than a bulk order that
    /// floods discovery in a single wait.</summary>
    public static int MaxConcurrent(GuildSaveData save)
    {
        var lib = save?.Ledger?.Buildings?.FirstOrDefault(b =>
            b != null && string.Equals(b.Id, LibraryId, StringComparison.OrdinalIgnoreCase));
        if (lib == null || !lib.IsFunctional) return 0;
        return Math.Max(0, lib.Tier);
    }

    public static int InFlightCount(GuildSaveData save) =>
        save?.Ledger?.CardCommissions?.Count ?? 0;

    // ── Cost ──────────────────────────────────────────────────────────────

    /// <summary>Gold cost by rarity, or -1 when the card can never be commissioned
    /// (Legendaries are Regalia — milestone grants only, never researched).</summary>
    public static int GoldCost(CardBlueprint bp) => bp?.Rarity switch
    {
        null                 => -1,
        CardRarity.Common    => CostCommonGold,
        CardRarity.Uncommon  => CostUncommonGold,
        CardRarity.Rare      => CostRareGold,
        CardRarity.Legendary => -1,
        _                    => CostRareGold,
    };

    // ── Query ─────────────────────────────────────────────────────────────

    /// <summary>The existing in-flight commission for a blueprint, or null.</summary>
    public static CardCommission Find(GuildSaveData save, string blueprintId)
    {
        if (save?.Ledger?.CardCommissions == null || string.IsNullOrEmpty(blueprintId))
            return null;
        return save.Ledger.CardCommissions.FirstOrDefault(c =>
            c != null && string.Equals(c.BlueprintId, blueprintId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a blueprint is a legitimate research target: not already
    /// known, not a Legendary, and not a Marginalia reward (that card has its own
    /// acquisition verb — defeating the faction — and the Library note already
    /// tells the player so; letting them buy it here would undercut that verb).</summary>
    public static bool IsCommissionable(GuildSaveData save, CardBlueprint bp)
    {
        if (save?.Ledger == null || bp == null) return false;
        if (bp.Rarity == CardRarity.Legendary) return false;
        if (MarginaliaService.IsMarginaliaCard(bp.Id)) return false;

        var unlocked = save.Ledger.UnlockedCardBlueprintIds;
        if (unlocked != null &&
            unlocked.Any(id => string.Equals(id, bp.Id, StringComparison.OrdinalIgnoreCase)))
            return false;   // already discovered — mint it, don't research it

        return true;
    }

    /// <summary>Full report for the UI. Re-derives everything; never trusts a caller.</summary>
    public static CommissionStatus Evaluate(GuildSaveData save, CardBlueprint bp)
    {
        int gold = GoldCost(bp);
        int inFlight = InFlightCount(save);
        int max = MaxConcurrent(save);
        var existing = Find(save, bp?.Id);

        CommissionStatus Fail(string why) => new()
        {
            CanCommission = false, Blocker = why, GoldCost = gold, Lunations = ResearchLunations,
            InFlight = inFlight, MaxConcurrent = max,
            PendingLunations = existing?.LunationsRemaining ?? -1,
        };

        if (bp == null) return Fail("No card selected.");
        if (!ArchivesAvailable())
            return Fail("The Forbidden Archives must stand (Arcane Library, tier III) before research can begin.");
        if (existing != null)
            return Fail($"Already under research — {existing.LunationsRemaining} lunation(s) remain.");
        if (bp.Rarity == CardRarity.Legendary || gold < 0)
            return Fail("This is Regalia. It is granted at a milestone, never researched.");
        if (MarginaliaService.IsMarginaliaCard(bp.Id))
            return Fail("This one is earned in the field, not the stacks — defeat its faction to learn it.");
        if (!IsCommissionable(save, bp))
            return Fail("You already know this card. The Library can copy it — see the scribing options.");
        if (max <= 0)
            return Fail("The Forbidden Archives must stand before research can begin.");
        if (inFlight >= max)
            return Fail($"The Archives are at capacity ({inFlight}/{max}). Wait for a commission to complete.");
        if ((save?.Cycle?.Gold ?? 0) < gold)
            return Fail($"Not enough gold — {gold} needed.");

        return new CommissionStatus
        {
            CanCommission = true, GoldCost = gold, Lunations = ResearchLunations,
            InFlight = inFlight, MaxConcurrent = max, PendingLunations = -1, Blocker = "",
        };
    }

    // ── Commit ────────────────────────────────────────────────────────────

    /// <summary>Place a research commission: charge gold up front, append the
    /// in-flight entry. Re-evaluates rather than trusting the caller. Returns the
    /// commission, or null on refusal.</summary>
    public static CardCommission Commission(GuildSaveData save, CardBlueprint bp)
    {
        var status = Evaluate(save, bp);
        if (!status.CanCommission)
        {
            GD.PrintErr($"[Commission] Refused '{bp?.Id}': {status.Blocker}");
            return null;
        }

        save.Ledger.CardCommissions ??= new List<CardCommission>();
        save.Gold -= status.GoldCost;

        var commission = new CardCommission
        {
            BlueprintId = bp.Id,
            LunationsRemaining = status.Lunations,
            GoldPaid = status.GoldCost,
        };
        save.Ledger.CardCommissions.Add(commission);

        GD.Print($"[Commission] Research begun on '{bp.Id}' for {status.GoldCost} gold — " +
                 $"delivers in {status.Lunations} lunation(s) " +
                 $"({status.InFlight + 1}/{status.MaxConcurrent} in flight).");
        return commission;
    }

    // ── Tick + settlement ─────────────────────────────────────────────────

    /// <summary>
    /// Advance every in-flight commission by one lunation and unlock any that
    /// reach zero. Call EXACTLY ONCE per lunation, from the single lunation-tick
    /// chokepoint (StrategicView.RunLunationTick) — a second call in the same
    /// lunation would double-count the timer, the canonical save-adjacent bug.
    /// Returns the number of cards unlocked this tick.
    /// </summary>
    public static int TickLunation(GuildSaveData save)
    {
        var list = save?.Ledger?.CardCommissions;
        if (list == null || list.Count == 0) return 0;

        int unlocked = 0;
        // Iterate a copy so settlement can mutate the live list safely.
        foreach (var c in list.ToList())
        {
            if (c == null) { list.Remove(c); continue; }
            c.LunationsRemaining--;
            if (c.LunationsRemaining <= 0)
                unlocked += Settle(save, c);
        }
        return unlocked;
    }

    /// <summary>
    /// Self-heal pass: unlock and clear any commission already at zero (or below)
    /// without decrementing. Safe to call on load — a commission that completed
    /// but whose settlement was lost to a crash between tick and save is paid on
    /// the next load, matching the ProgressionSweep reconciler discipline.
    /// </summary>
    public static int Reconcile(GuildSaveData save)
    {
        var list = save?.Ledger?.CardCommissions;
        if (list == null || list.Count == 0) return 0;

        int unlocked = 0;
        foreach (var c in list.ToList())
        {
            if (c == null) { list.Remove(c); continue; }
            if (c.LunationsRemaining <= 0)
                unlocked += Settle(save, c);
        }
        return unlocked;
    }

    // ── Debug ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Testing shortcut: place a commission on a random legitimate target
    /// (locked, non-Legendary, non-Marginalia, not already in flight),
    /// BYPASSING the Archives flag, gold, and capacity gates — the same
    /// "skip the slow gate" contract the campus debug grants use. Charges no
    /// gold. Defaults to a 1-lunation timer so a single lunation advance
    /// settles it. Returns the blueprint id commissioned, or null if none was
    /// available. Prefers Rares (the real use case); falls back to any locked
    /// target so the button still does something on an all-unlocked save.
    /// </summary>
    public static string DebugCommissionRandom(GuildSaveData save, int lunations = 1)
    {
        if (save?.Ledger == null) return null;
        save.Ledger.CardCommissions ??= new List<CardCommission>();

        bool Available(CardBlueprint b) =>
            IsCommissionable(save, b) && Find(save, b.Id) == null;

        var rares = CardDatabase.Blueprints
            .Where(b => b.Rarity == CardRarity.Rare && Available(b)).ToList();
        var pool = rares.Count > 0
            ? rares
            : CardDatabase.Blueprints.Where(Available).ToList();

        if (pool.Count == 0)
        {
            GD.Print("[Commission][Debug] No locked, commissionable card available.");
            return null;
        }

        var bp = pool[new Random().Next(pool.Count)];
        save.Ledger.CardCommissions.Add(new CardCommission
        {
            BlueprintId = bp.Id,
            LunationsRemaining = Math.Max(1, lunations),
            GoldPaid = 0,
        });
        GD.Print($"[Commission][Debug] Commissioned '{bp.Id}' ({bp.Rarity}) — " +
                 $"settles in {Math.Max(1, lunations)} lunation(s). Advance the moon to test.");
        return bp.Id;
    }

    /// <summary>Unlock a completed commission's card and remove it from the list.
    /// Idempotent on the unlock (a set-style add) so a card unlocked by another
    /// path in the meantime is not duplicated.</summary>
    private static int Settle(GuildSaveData save, CardCommission c)
    {
        save.Ledger.CardCommissions.Remove(c);
        if (string.IsNullOrWhiteSpace(c.BlueprintId)) return 0;

        save.Ledger.UnlockedCardBlueprintIds ??= new List<string>();
        bool already = save.Ledger.UnlockedCardBlueprintIds.Any(id =>
            string.Equals(id, c.BlueprintId, StringComparison.OrdinalIgnoreCase));
        if (!already)
            save.Ledger.UnlockedCardBlueprintIds.Add(c.BlueprintId);

        GD.Print($"[Commission] Research complete — '{c.BlueprintId}' is now discovered " +
                 $"and enters the draft pool. Copy it at the Arcane Library.");
        return already ? 0 : 1;
    }
}
