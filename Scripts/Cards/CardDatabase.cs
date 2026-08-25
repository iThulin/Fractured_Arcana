using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardDatabase.cs
//
// Purpose:        Process-wide registry of card blueprints
//                 loaded from JSON. Each blueprint is a
//                 compiled Card template that gets cloned (new
//                 InstanceId) every time the card appears in a
//                 deck. Also hosts the deck-building helpers
//                 (random, weighted, draft).
// Layer:          Data
// Collaborators:  CardLoader.cs / JsonCardLoader.cs (populate
//                 the registry at startup), CardRuntime.cs
//                 (Card / CardHalf types), UnitDeckData.cs
//                 (consumes BuildRandomDeck during init)
// See:            README §3 (Architecture, card pipeline),
//                 README §4.1 (Adding a Card)
// ============================================================

/// <summary>One entry in the card database. Holds a compiled <see cref="Prebuilt"/> card that's cloned (fresh <see cref="Card.InstanceId"/>) every time the card lands in a deck.</summary>
public sealed class CardBlueprint
{
    /// <summary>Composite key combining school and both half names. Distinct across the database.</summary>
    public string Id;

    /// <summary>School the card belongs to. Used by school-filtered deck building.</summary>
    public CardSchool School;

    /// <summary>Rarity tier. Affects draft odds.</summary>
    public CardRarity Rarity;

    /// <summary>Compiled card template. Cloned on every <see cref="CardDatabase.Instantiate"/> call.</summary>
    public Card Prebuilt;
}

/// <summary>Process-wide registry of card blueprints. Populated at startup by the JSON loader; queried by deck builders and any system that needs to spawn or list cards.</summary>
public static class CardDatabase
{
    /// <summary>All registered blueprints. Filled at startup by the loader; queried thereafter.</summary>
    public static readonly List<CardBlueprint> Blueprints = new();

    /// <summary>Adds a compiled card to the database. Called by the JSON loader once per card. Null cards are logged and skipped.</summary>
    public static void RegisterPrebuiltCard(Card card, string jsonId = null)
    {
        if (card == null) { GD.PrintErr("RegisterPrebuiltCard: null card"); return; }

        var school = card.TopHalf?.School ?? card.BottomHalf?.School ?? CardSchool.Tinker;
        var topName = card.TopHalf?.Name ?? "";
        var botName = card.BottomHalf?.Name ?? "";
        string displayKey = $"{school}:{topName}|{botName}";

        // Use jsonId if provided, fall back to composite key
        string blueprintId = jsonId ?? displayKey;
        card.BlueprintId = blueprintId;

        Blueprints.Add(new CardBlueprint
        {
            Id = blueprintId,
            School = school,
            Rarity = card.Rarity,
            Prebuilt = card
        });

        // Per-card registration log, visible only when Godot runs with --verbose.
        if (OS.IsStdOutVerbose())
            GD.Print($"[BlueprintId] school={school} id=\"{displayKey}\"");
    }

    /// <summary>Returns a fresh <see cref="Card"/> instance (unique <see cref="Card.InstanceId"/>) cloned from the blueprint. The CardHalf objects are reused as read-only recipes; if combat ever mutates a half in place, this needs to become a deep clone.</summary>
    public static Card Instantiate(CardBlueprint bp)
    {
        if (bp.Prebuilt == null)
        {
            GD.PrintErr($"Blueprint {bp.Id} has no Prebuilt card. Did registration fail?");
            return null;
        }
        return ClonePrebuilt(bp.Prebuilt);
    }

    // Shallow clone: new Card shell (fresh InstanceId) reusing compiled halves.
    // Halves are treated as read-only recipes by combat. If that changes,
    // this needs to become a deep clone.
    private static Card ClonePrebuilt(Card src)
    {
        return new Card
        {
            CardName = src.CardName,
            BlueprintId = src.BlueprintId,
            Rarity = src.Rarity,
            TopHalf = src.TopHalf,
            BottomHalf = src.BottomHalf
        };
    }

    /// <summary>Prints per-school blueprint counts plus the total to the Godot console. Diagnostic.</summary>
    public static void LogCounts()
    {
        var counts = Blueprints
            .GroupBy(b => b.School)
            .Select(g => $"{g.Key}:{g.Count()}")
            .ToList();

        GD.Print("Blueprint counts => " + string.Join(", ", counts));
        GD.Print($"CardDatabase now holds {Blueprints.Count} blueprints");
    }

    /// <summary>
    /// Find a blueprint by blueprint id (e.g. "enchanter_snare_glyph") or by
    /// display card name (case-insensitive). Companion contributedCardIds are
    /// JSON ids, not display names, and matching only on CardName made every
    /// companion contribution report "missing card" (2026-07-29 playtest).
    /// </summary>
    public static CardBlueprint GetByName(string cardName)
    {
        if (string.IsNullOrEmpty(cardName)) return null;
        foreach (var bp in Blueprints)
        {
            if (string.Equals(bp.Id, cardName, StringComparison.OrdinalIgnoreCase))
                return bp;
            if (string.Equals(bp.Prebuilt?.CardName, cardName,
                StringComparison.OrdinalIgnoreCase))
                return bp;
        }
        return null;
    }

    /// <summary>
    /// Returns a display name combining school and both half names.
    /// Format: "[School] TopName / BottomName"
    /// </summary>
    public static string GetDisplayName(CardBlueprint bp, OwnedCard owned = null)
    {
        if (bp == null) return "Unknown";

        string school = bp.School.ToString();

        if (owned != null && owned.IsBaseUpgraded)
        {
            var upgraded = CardUpgradeApplier.Apply(
                owned.BlueprintId, owned.TopTier, owned.BotTier);
            if (upgraded != null)
            {
                string top = upgraded.TopHalf?.Name ?? "";
                string bot = upgraded.BottomHalf?.Name ?? "";
                if (!string.IsNullOrEmpty(top) && !string.IsNullOrEmpty(bot))
                    return $"[{school}] {top} / {bot}";
            }
        }

        string baseTop = bp.Prebuilt?.TopHalf?.Name ?? "";
        string baseBot = bp.Prebuilt?.BottomHalf?.Name ?? "";
        if (!string.IsNullOrEmpty(baseTop) && !string.IsNullOrEmpty(baseBot))
            return $"[{school}] {baseTop} / {baseBot}";
        return bp.Id;
    }

    // ── Deck building ───────────────────────────────────────────────────
    //
    // NOTE: while the JSON pool is small, these allow duplicates so decks
    // can still be built. When the pool grows past target deck size, switch
    // to unique-picking by removing the duplicate-allowing code path.

    /// <summary>Builds a random deck of <paramref name="count"/> cards from the given school. Duplicates allowed. Excludes Legendaries, which are Regalia, granted at milestones, and must never appear in a randomly generated pile (this method backs companion AI decks and the missing-starter-file fallback, both of which used to hand out Legendaries for free). Returns an empty list if no cards in the database belong to the school.</summary>
    public static List<Card> BuildRandomDeck(CardSchool school, int count, int? seed = null)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var pool = Blueprints
            .Where(b => b.School == school && b.Rarity != CardRarity.Legendary)
            .ToList();

        if (pool.Count == 0)
        {
            GD.PrintErr($"No cards in database for school {school}.");
            return new List<Card>();
        }

        // Duplicates allowed: fine for tiny pools, harmless for large ones.
        var result = new List<Card>();
        for (int i = 0; i < count; i++)
            result.Add(Instantiate(pool[rng.Next(pool.Count)]));
        return result;
    }

    /// <summary>Picks one random blueprint from the database (optionally filtered) and returns a fresh instance. Returns null when no blueprint passes the filter.</summary>
    public static Card RandomCard(Random rng, Func<CardBlueprint, bool> filter = null)
    {
        var pool = (filter == null) ? Blueprints : Blueprints.Where(filter).ToList();
        if (pool.Count == 0) return null;

        return Instantiate(pool[rng.Next(pool.Count)]);
    }

    /// <summary>Builds a rarity-weighted deck (common 4x, uncommon 3x, rare 2x). Legendaries are excluded (see <see cref="BuildRandomDeck"/>). Useful when the pool is large enough to span the rarity ladder.</summary>
    public static List<Card> BuildWeightedDeck(CardSchool school, int count, int? seed = null)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var pool = Blueprints
            .Where(b => b.School == school && b.Rarity != CardRarity.Legendary)
            .ToList();

        if (pool.Count == 0) return new List<Card>();

        var weighted = new List<CardBlueprint>();
        foreach (var bp in pool)
        {
            int weight = bp.Rarity switch
            {
                CardRarity.Common => 4,
                CardRarity.Uncommon => 3,
                CardRarity.Rare => 2,
                CardRarity.Legendary => 0,
                _ => 4
            };
            for (int i = 0; i < weight; i++) weighted.Add(bp);
        }

        var result = new List<Card>();
        for (int i = 0; i < count; i++)
            result.Add(Instantiate(weighted[rng.Next(weighted.Count)]));
        return result;
    }

    // ── The draft gate ───────────────────────────────────────────────────
    //
    // REMOVED 2026-08-04: GetDraftChoices(school, choices, seed).
    // It had ZERO call sites. The live reward path is
    // CardRewardScreen.GenerateOffers, which built its own pool. The two had
    // drifted: GetDraftChoices implemented the 2026-07-10 Adept-as-neutral
    // ruling ("the LAST slot always offers an Adept card") and the live path
    // did not, so a ruling recorded as shipped was never in effect. The
    // guarantee now lives in the live path, and this is the single pool
    // builder both drafting and any future acquisition surface must use.
    // See docs/progression_card_acquisition_v1.md §1d, §5, §6a.

    /// <summary>
    /// THE canonical draft pool for a school. Every card-offering surface must
    /// go through here. The two rules below are easy to forget and expensive
    /// to miss, and a second hand-rolled pool is exactly how they got lost once
    /// already.
    ///
    /// Applies, in order:
    ///  1. <b>School filter.</b>
    ///  2. <b>Legendary exclusion.</b> Legendaries are Regalia: milestone
    ///     grants, never draftable (design doc §6a). This is where "weight 0"
    ///     is enforced; do not re-add a Legendary weight anywhere.
    ///  3. <b>Unlock filter.</b> Only blueprints in
    ///     <see cref="EternalLedger.UnlockedCardBlueprintIds"/> may be offered.
    ///     Until 2026-08-04 that list was written but never read, so every card
    ///     in the game was offerable on run one.
    ///
    /// Falls back to the unfiltered (still Legendary-free) school pool and logs
    /// an error rather than returning empty: a blank reward screen is the worst
    /// possible failure of this change.
    /// </summary>
    public static List<CardBlueprint> DraftablePool(CardSchool school, GuildSaveData save)
    {
        var schoolPool = Blueprints
            .Where(b => b.School == school && b.Rarity != CardRarity.Legendary)
            .ToList();

        if (schoolPool.Count == 0)
            return schoolPool;

        var unlocked = save?.Ledger?.UnlockedCardBlueprintIds;
        if (unlocked == null || unlocked.Count == 0)
        {
            GD.PrintErr($"[CardDatabase] No unlocked blueprints recorded, so offering the " +
                        $"full {school} pool. StarterDeckLoader.SeedUnlockedPool should " +
                        $"have run at guild creation.");
            return schoolPool;
        }

        var unlockedSet = new HashSet<string>(unlocked, StringComparer.OrdinalIgnoreCase);
        var gated = schoolPool.Where(b => unlockedSet.Contains(b.Id)).ToList();

        //  4. Owned Regalia are never draftable, at ANY rarity. Legendaries are
        //     already gone at step 2, but companion capstones grant the
        //     companion's own contributed card (design doc §6d), which is usually
        //     Common or Uncommon, and SeedUnlockedPool unlocks every Common and
        //     Uncommon in every school. Without this the player could draft
        //     duplicates of the unique artifact their dead friend left them.
        //     Enforced here rather than by withholding the unlock, because the
        //     unlock list is seeded wholesale and cannot express an exception.
        var regalia = save?.Ledger?.RegaliaBlueprintIds;
        if (regalia != null && regalia.Count > 0)
        {
            var regaliaSet = new HashSet<string>(regalia, StringComparer.OrdinalIgnoreCase);
            var withoutRegalia = gated.Where(b => !regaliaSet.Contains(b.Id)).ToList();
            if (withoutRegalia.Count > 0)
                gated = withoutRegalia;
            // If excluding Regalia would empty the pool, keep the gated pool.
            // An offered duplicate beats a blank reward screen.
        }

        if (gated.Count == 0)
        {
            GD.PrintErr($"[CardDatabase] Unlock filter emptied the {school} pool " +
                        $"({schoolPool.Count} candidates, {unlocked.Count} unlocks), " +
                        $"so it is falling back to the unfiltered school pool.");
            return schoolPool;
        }

        return gated;
    }

    /// <summary>
    /// Rarity-weighted expansion of <see cref="DraftablePool"/> (Common 4×,
    /// Uncommon 3×, Rare 2×). Legendary carries no weight because it is never
    /// in the pool (see <see cref="DraftablePool"/> rule 2).
    /// </summary>
    public static List<CardBlueprint> WeightedDraftPool(CardSchool school, GuildSaveData save)
    {
        var weighted = new List<CardBlueprint>();
        foreach (var bp in DraftablePool(school, save))
        {
            int w = bp.Rarity switch
            {
                CardRarity.Common => 4,
                CardRarity.Uncommon => 3,
                CardRarity.Rare => 2,
                // Belt-and-braces: DraftablePool already filtered these out. Spelled
                // out rather than left to the catch-all so that loosening rule 2
                // cannot silently give Legendaries the HIGHEST weight in the pool.
                CardRarity.Legendary => 0,
                _ => 4,
            };
            for (int i = 0; i < w; i++) weighted.Add(bp);
        }
        return weighted;
    }
}
