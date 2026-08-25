using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardMasteryService.cs
//
// Purpose:        Permanent, per-BLUEPRINT card mastery on the
//                 EternalLedger: casts and best tiers reached.
//
//                 Before this, both lived only on OwnedCard, which hangs
//                 off CycleState.PlayerDeck and is replaced wholesale
//                 every cycle. Two consequences the player felt:
//                   • The upgrade gate (CardMasteryThresholds) reset, so
//                     you re-cast a card 10/20/35 times to re-earn points
//                     you had already earned in a previous timeline.
//                   • Every tier you had ever bought evaporated, and a
//                     second copy of a card always started at base.
//
//                 Knowing a card well is KNOWLEDGE, and knowledge lives in
//                 the loom (progression_persistence_model_v1 §2: lore and
//                 unlocked blueprints were already there for this reason).
//                 Splinters spent on tiers are still spent per copy; what
//                 persists is the right to spend them and the ceiling you
//                 have proven you can reach.
//
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.CardMastery (the record),
//                 CastMasteryTracker.cs (cast hook),
//                 CardUpgradeScreen.cs (gate + tier hook),
//                 CardMintService.cs (reproduces the mastered tier)
// See:            docs/progression_card_acquisition_v1_2.md
//
// ⚠ THIRD "MASTERY" IN THIS CODEBASE. Disambiguate in every comment:
//     • CardMastery   (this):  per blueprint, permanent, gates upgrades.
//     • SchoolMastery:         per school, permanent, gates declaration.
//     • CastMastery(Tracker):  the per-copy counter this now shadows.
// ============================================================

/// <summary>
/// Reads and writes <see cref="EternalLedger.CardMastery"/>. Every gate that used
/// to read <c>OwnedCard.CastCount</c> should read <see cref="Casts"/> instead.
/// </summary>
public static class CardMasteryService
{
    // ── Casts ────────────────────────────────────────────────────────────

    /// <summary>Record one cast of a blueprint. Returns the new lifetime total.</summary>
    public static int RecordCast(GuildSaveData save, string blueprintId)
    {
        if (save?.Ledger == null || string.IsNullOrWhiteSpace(blueprintId)) return 0;

        var rec = save.Ledger.GetCardMastery(blueprintId);
        rec.Casts++;
        return rec.Casts;
    }

    /// <summary>
    /// Lifetime casts of a blueprint. Takes the max of the permanent record and the
    /// per-copy counter, so a save that predates the permanent record is never
    /// worse off than it was: its existing OwnedCard.CastCount still counts until
    /// the ledger record overtakes it.
    /// </summary>
    public static int Casts(GuildSaveData save, string blueprintId, int ownedCopyCasts = 0)
    {
        if (save?.Ledger?.CardMastery == null || string.IsNullOrWhiteSpace(blueprintId))
            return ownedCopyCasts;

        int permanent = save.Ledger.CardMastery.TryGetValue(blueprintId, out var rec)
            ? rec.Casts : 0;
        return Math.Max(permanent, ownedCopyCasts);
    }

    // ── Tiers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stamp a copy's tiers into the permanent high-water mark. Call after any
    /// upgrade purchase. Monotonic: it only ever raises, so disenchanting or
    /// losing the copy never costs the player the ceiling they proved.
    /// </summary>
    public static void RecordTiers(GuildSaveData save, OwnedCard owned)
    {
        if (save?.Ledger == null || owned == null ||
            string.IsNullOrWhiteSpace(owned.BlueprintId)) return;

        var rec = save.Ledger.GetCardMastery(owned.BlueprintId);

        bool raised = false;
        if (owned.TopTier > rec.BestTopTier) { rec.BestTopTier = owned.TopTier; raised = true; }
        if (owned.BotTier > rec.BestBotTier) { rec.BestBotTier = owned.BotTier; raised = true; }
        if (owned.PointsSpent > rec.BestPointsSpent) { rec.BestPointsSpent = owned.PointsSpent; raised = true; }

        if (raised)
            GD.Print($"[CardMastery] '{owned.BlueprintId}' high-water → " +
                     $"{rec.BestTopTier}/{rec.BestBotTier} ({rec.BestPointsSpent} pts).");
    }

    /// <summary>The permanent record for a blueprint, or an empty one. Never null.</summary>
    public static CardMasteryRecord Best(GuildSaveData save, string blueprintId)
    {
        if (save?.Ledger?.CardMastery == null || string.IsNullOrWhiteSpace(blueprintId))
            return new CardMasteryRecord();
        return save.Ledger.CardMastery.TryGetValue(blueprintId, out var rec)
            ? rec : new CardMasteryRecord();
    }

    /// <summary>
    /// Fold every owned copy's tiers and casts into the permanent record. Called on
    /// load so an existing save's accumulated mastery is captured before its deck is
    /// next reseeded and the per-copy numbers are lost for good. Idempotent.
    /// </summary>
    public static int AbsorbOwnedCopies(GuildSaveData save)
    {
        var cards = save?.Cycle?.PlayerDeck?.Cards;
        if (cards == null || save.Ledger == null) return 0;

        int touched = 0;
        foreach (var owned in cards)
        {
            if (owned == null || string.IsNullOrWhiteSpace(owned.BlueprintId)) continue;

            var rec = save.Ledger.GetCardMastery(owned.BlueprintId);
            bool raised = false;

            // Casts take the MAX, never the sum: two copies of one card in a deck
            // each track their own casts, and adding them would inflate the record
            // every time this ran. Max is idempotent; sum is not.
            if (owned.CastCount > rec.Casts) { rec.Casts = owned.CastCount; raised = true; }
            if (owned.TopTier > rec.BestTopTier) { rec.BestTopTier = owned.TopTier; raised = true; }
            if (owned.BotTier > rec.BestBotTier) { rec.BestBotTier = owned.BotTier; raised = true; }
            if (owned.PointsSpent > rec.BestPointsSpent) { rec.BestPointsSpent = owned.PointsSpent; raised = true; }

            if (raised) touched++;
        }

        if (touched > 0)
            GD.Print($"[CardMastery] Absorbed {touched} owned copy record(s) into the loom.");
        return touched;
    }
}
