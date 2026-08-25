using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// RegaliaService.cs
//
// Purpose:        Regalia: the ONE sanctioned exception to the deck
//                 reseed. Legendaries are no longer draftable (their
//                 draft weight is 0); they are named artifacts granted
//                 at milestones, owned permanently on the EternalLedger,
//                 and carried into a fresh cycle K at a time.
//
//                 Fiction (narrative_frame_intro_finale_v1 R5): the
//                 fragments are trans-temporal objects. A card cut from a
//                 shard is inside the Long Second with you. It does not
//                 reseed because it was never in the timeline that resets.
//
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.cs (RegaliaBlueprintIds: ownership),
//                 CycleState.cs (CarriedRegaliaIds: this cycle's picks),
//                 ProgressionSweep.cs (the automatic granter),
//                 SaveManager.SeedDeckForSchool (seeds carried into deck),
//                 CampusExpeditionPanel.cs (the carry picker),
//                 CardDatabase.DraftablePool (enforces weight 0)
// See:            docs/progression_card_acquisition_v1.md §6
//
// AMENDS progression_persistence_model_v1.md §5, which listed the run deck
// as unconditionally WIPED. That line now reads "→ starter, plus up to K
// Regalia". User-authorized 2026-08-04. This is the only exception.
// ============================================================

/// <summary>
/// Ownership, carry limits, and deck seeding for Regalia. Grants are idempotent:
/// calling Grant twice for the same blueprint is a no-op, which is what lets
/// <see cref="ProgressionSweep"/> run on every save without duplicating rewards.
/// </summary>
public static class RegaliaService
{
    /// <summary>Carry limit floor: you may always bring one artifact.</summary>
    public const int BaseCarrySlots = 1;

    /// <summary>Shards per additional carry slot. 6 shards → K = 4.</summary>
    public const int ShardsPerExtraSlot = 2;

    // ── Ownership (permanent) ────────────────────────────────────────────

    /// <summary>
    /// Grant a Regalia permanently. Idempotent. Returns false if already owned
    /// or if the blueprint does not exist. Does NOT slot it into any deck;
    /// carrying is a separate, bounded decision (<see cref="SetCarried"/>).
    /// </summary>
    public static bool Grant(GuildSaveData save, string blueprintId, string reason)
    {
        if (save?.Ledger == null || string.IsNullOrWhiteSpace(blueprintId))
            return false;

        save.Ledger.RegaliaBlueprintIds ??= new List<string>();

        if (save.Ledger.RegaliaBlueprintIds.Contains(blueprintId))
            return false;

        var bp = FindBlueprint(blueprintId);
        if (bp == null)
        {
            GD.PrintErr($"[Regalia] Grant failed: no blueprint '{blueprintId}'. " +
                        $"Has the card been renamed or removed? ({reason})");
            return false;
        }

        save.Ledger.RegaliaBlueprintIds.Add(blueprintId);

        // Legendaries are also knowledge, harmlessly so, since DraftablePool
        // drops them anyway; recording it keeps the card library honest.
        //
        // Non-Legendary Regalia (companion signature cards, per design doc §6d)
        // are deliberately NOT unlocked. A Rare companion card added to the
        // draft pool would let the player draft duplicates of the artifact that
        // is supposed to be the unique thing their friend left behind.
        if (bp.Rarity == CardRarity.Legendary)
        {
            save.Ledger.UnlockedCardBlueprintIds ??= new List<string>();
            if (!save.Ledger.UnlockedCardBlueprintIds.Contains(blueprintId))
                save.Ledger.UnlockedCardBlueprintIds.Add(blueprintId);
        }

        GD.Print($"[Regalia] GRANTED '{blueprintId}': {reason} " +
                 $"(now own {save.Ledger.RegaliaBlueprintIds.Count})");
        return true;
    }

    public static bool IsOwned(GuildSaveData save, string blueprintId) =>
        save?.Ledger?.RegaliaBlueprintIds?.Contains(blueprintId) ?? false;

    /// <summary>
    /// Every Regalia the player owns, as blueprints, ordered Legendaries first
    /// and then by grant order.
    ///
    /// The rarity sort is load-bearing, not cosmetic: this order IS the default
    /// carry selection, both in the picker's pre-selection and in the auto-carry
    /// fallback. Companion capstones pay in the companion's own contributed card
    /// (design doc §6d), which is usually Common or Uncommon. In pure grant
    /// order an early companion arc would push a shard's Legendary out of the
    /// default K slots.
    /// </summary>
    public static List<CardBlueprint> Owned(GuildSaveData save)
    {
        var ids = save?.Ledger?.RegaliaBlueprintIds;
        if (ids == null || ids.Count == 0) return new List<CardBlueprint>();

        var result = new List<CardBlueprint>();
        foreach (var id in ids)
        {
            var bp = FindBlueprint(id);
            if (bp != null) result.Add(bp);
            else GD.PrintErr($"[Regalia] Owned id '{id}' resolves to no blueprint. Skipped.");
        }

        // OrderBy is stable in LINQ-to-objects, so grant order survives within
        // each rarity band.
        return result
            .OrderByDescending(b => b.Rarity == CardRarity.Legendary ? 1 : 0)
            .ToList();
    }

    // ── Carry limit ──────────────────────────────────────────────────────

    /// <summary>
    /// Shards permanently collected, counted off the fragment metaflags that
    /// ExpeditionManager stamps (fragment_&lt;key&gt;_collected).
    /// </summary>
    public static int ShardsCollected(GuildSaveData save)
    {
        var flags = save?.Ledger?.MetaNarrativeFlags;
        if (flags == null) return 0;

        return flags.Count(f => !string.IsNullOrEmpty(f)
                                && f.StartsWith("fragment_", StringComparison.Ordinal)
                                && f.EndsWith("_collected", StringComparison.Ordinal));
    }

    /// <summary>
    /// K: how many Regalia may be carried into a cycle. Anchored to the Sixfold
    /// Seal rather than to campus tiers (there is no global campus tier in code;
    /// tiers are per-building). 0-1 shards → 1, 2-3 → 2, 4-5 → 3, 6 → 4.
    /// </summary>
    public static int MaxCarry(GuildSaveData save) =>
        BaseCarrySlots + (ShardsCollected(save) / ShardsPerExtraSlot);

    // ── Carry selection (per cycle) ──────────────────────────────────────

    public static List<string> GetCarried(GuildSaveData save)
    {
        if (save?.Cycle == null) return new List<string>();
        save.Cycle.CarriedRegaliaIds ??= new List<string>();
        return save.Cycle.CarriedRegaliaIds;
    }

    /// <summary>
    /// Set this cycle's carried Regalia. Silently drops ids the player does not own
    /// and clamps to <see cref="MaxCarry"/>. The UI should prevent both, but a save
    /// edited by hand or carried across a shard loss must not be able to smuggle
    /// artifacts past the limit. Returns the accepted list.
    /// </summary>
    public static List<string> SetCarried(GuildSaveData save, IEnumerable<string> ids)
    {
        if (save?.Cycle == null) return new List<string>();

        int k = MaxCarry(save);
        var accepted = (ids ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .Where(id => IsOwned(save, id))
            .Take(k)
            .ToList();

        save.Cycle.CarriedRegaliaIds = accepted;
        GD.Print($"[Regalia] Carrying {accepted.Count}/{k} into cycle " +
                 $"{save.Cycle.CycleNumber}: {string.Join(", ", accepted)}");
        return accepted;
    }

    // ── Pending selection (survives the CycleState swap) ─────────────────
    //
    // The carry picker runs on the OLD cycle, but BeginNewCycle replaces
    // CycleState wholesale. Writing the selection straight to
    // Cycle.CarriedRegaliaIds would throw it away one line later. So the picker
    // stages here, and SeedCarriedIntoDeck (called after the new CycleState
    // exists) consumes it. Single-frame lifetime; a lost staging degrades to
    // the auto-carry default below rather than to nothing.
    private static List<string> _pendingCarry;

    /// <summary>
    /// Which save slot the staging belongs to. The picker stages at BUILD time
    /// (no player interaction required), and a static outlives scene and slot
    /// changes, so without this, opening the picker in slot 0, backing out, and
    /// later ending a cycle in slot 1 would consume slot 0's staging. Every id
    /// would fail the IsOwned filter, leaving an empty carry that still counted
    /// as "the player chose", and slot 1 would start with no artifacts at all.
    /// </summary>
    private static int _pendingSlot = -1;

    /// <summary>Stage a carry selection to be applied when the next cycle's deck is seeded.</summary>
    public static void StagePendingCarry(IEnumerable<string> ids)
    {
        _pendingCarry = (ids ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        _pendingSlot = SaveManager.ActiveSlot;
        GD.Print($"[Regalia] Staged {_pendingCarry.Count} for the next cycle (slot {_pendingSlot}).");
    }

    // ── Deck seeding ─────────────────────────────────────────────────────

    /// <summary>
    /// Mint an OwnedCard for each carried Regalia and slot it into the active deck.
    /// Called from SaveManager.SeedDeckForSchool AFTER the starter deck is seeded,
    /// so Regalia ride on top of the 10-card floor rather than displacing it.
    /// Idempotent within a cycle: a Regalia already present in the deck is skipped.
    /// </summary>
    public static int SeedCarriedIntoDeck(GuildSaveData save)
    {
        if (save?.Cycle?.PlayerDeck == null) return 0;

        // 1. A staged pick from the carry screen wins, INCLUDING a deliberate
        //    empty one. "Carry nothing" is a legal choice (an off-school artifact
        //    you cannot fuel is worse than a tenth starter card), so a staged
        //    empty list must suppress the default below.
        //
        //    But ONLY a deliberately empty staging suppresses it. A staging that
        //    held ids and was emptied by SetCarried's ownership filter means the
        //    staging was stale, not that the player chose nothing. Falling back
        //    to the default is right there.
        bool hadStaged = _pendingCarry != null && _pendingSlot == SaveManager.ActiveSlot;
        bool stagedEmptyOnPurpose = hadStaged && _pendingCarry.Count == 0;

        if (_pendingCarry != null && !hadStaged)
            GD.Print($"[Regalia] Discarding a staging from slot {_pendingSlot} " +
                     $"(active slot is {SaveManager.ActiveSlot}).");

        if (hadStaged)
            SetCarried(save, _pendingCarry);

        _pendingCarry = null;
        _pendingSlot = -1;

        var carried = GetCarried(save);

        // 2. No pick made (new game, the picker was skipped, or the staging was
        //    stale): default to the first K. The player should never silently
        //    lose the artifacts they earned just because a screen did not run.
        if (!stagedEmptyOnPurpose && carried.Count == 0)
        {
            // Owned() is Legendary-first, so the default matches what the picker
            // would have pre-selected. Using the raw id list here instead would
            // silently disagree with the UI.
            var owned = Owned(save);
            if (owned.Count == 0) return 0;

            carried = SetCarried(save, owned.Take(MaxCarry(save)).Select(b => b.Id));
            GD.Print($"[Regalia] No selection staged. Auto-carrying {carried.Count} " +
                     $"(Legendaries first, then grant order).");
        }

        if (carried.Count == 0) return 0;

        // Re-validate at seed time: the carry list was chosen before the cycle
        // rolled, and MaxCarry can move if a shard was lost to the Keepers.
        carried = SetCarried(save, carried);

        var deck = save.Cycle.PlayerDeck;
        deck.Cards ??= new List<OwnedCard>();
        deck.ActiveDeckInstanceIds ??= new List<string>();

        int seeded = 0;
        foreach (var id in carried)
        {
            if (deck.Cards.Any(c => c.IsRegalia && c.BlueprintId == id))
                continue;

            if (FindBlueprint(id) == null)
            {
                GD.PrintErr($"[Regalia] Carried id '{id}' has no blueprint. Not seeded.");
                continue;
            }

            var owned = new OwnedCard
            {
                BlueprintId = id,
                InstanceId = Guid.NewGuid().ToString("N"),
                Grafts = new List<string>(),
                IsStarter = false,
                IsRegalia = true,
            };
            deck.Cards.Add(owned);

            // Own it regardless, but respect the active-deck ceiling that the
            // deck editor and combat both assume. PlayerDeckService.SlotCard is
            // the enforcing path for normal slotting; this one has to enforce
            // it itself. An artifact that will not fit stays in the stash.
            if (deck.ActiveDeckInstanceIds.Count < PlayerDeckSave.MaxDeckSize)
                deck.ActiveDeckInstanceIds.Add(owned.InstanceId);
            else
                GD.Print($"[Regalia] '{id}' owned but not slotted: active deck is at " +
                         $"the {PlayerDeckSave.MaxDeckSize}-card ceiling.");

            seeded++;
        }

        if (seeded > 0)
            GD.Print($"[Regalia] Seeded {seeded} Regalia into the cycle deck " +
                     $"(deck now {deck.ActiveDeckInstanceIds.Count} cards).");
        return seeded;
    }

    // ── Grant selection ──────────────────────────────────────────────────

    /// <summary>
    /// Pick a Legendary of <paramref name="school"/> that has not been granted yet.
    /// Returns null when the school has no ungranted Legendary left, which is a real
    /// state, not a bug: Adept has zero Legendaries by design (the undeclared school
    /// has no artifacts), and three schools sit at exactly zero slack. Callers must
    /// handle null by paying the milestone in SchoolMastery instead.
    /// </summary>
    public static CardBlueprint PickLegendaryForSchool(GuildSaveData save, string school)
    {
        if (string.IsNullOrWhiteSpace(school)) return null;
        if (!Enum.TryParse<CardSchool>(school.Trim(), ignoreCase: true, out var cs))
            return null;

        var owned = save?.Ledger?.RegaliaBlueprintIds ?? new List<string>();

        return CardDatabase.Blueprints
            .Where(b => b.School == cs
                        && b.Rarity == CardRarity.Legendary
                        && !owned.Contains(b.Id))
            .OrderBy(b => b.Id, StringComparer.Ordinal)   // deterministic, not random
            .FirstOrDefault();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static CardBlueprint FindBlueprint(string id) =>
        CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
}
