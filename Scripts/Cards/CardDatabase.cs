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
// See:            README §3 (Architecture — card pipeline),
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
    // Halves are treated as read-only recipes by combat — if that changes,
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
    /// Find a blueprint by card name (case-insensitive).
    /// Used for companion contributions and other content references.
    /// </summary>
    public static CardBlueprint GetByName(string cardName)
    {
        if (string.IsNullOrEmpty(cardName)) return null;
        foreach (var bp in Blueprints)
        {
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

    /// <summary>Builds a random deck of <paramref name="count"/> cards from the given school. Duplicates allowed. Returns an empty list if no cards in the database belong to the school.</summary>
    public static List<Card> BuildRandomDeck(CardSchool school, int count, int? seed = null)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var pool = Blueprints.Where(b => b.School == school).ToList();

        if (pool.Count == 0)
        {
            GD.PrintErr($"No cards in database for school {school}.");
            return new List<Card>();
        }

        // Duplicates allowed — fine for tiny pools, harmless for large ones.
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

    /// <summary>Builds a rarity-weighted deck (common 4x, uncommon 3x, rare 2x, legendary 1x). Useful when the pool is large enough to span the full rarity ladder.</summary>
    public static List<Card> BuildWeightedDeck(CardSchool school, int count, int? seed = null)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var pool = Blueprints.Where(b => b.School == school).ToList();

        if (pool.Count == 0) return new List<Card>();

        var weighted = new List<CardBlueprint>();
        foreach (var bp in pool)
        {
            int weight = bp.Rarity switch
            {
                CardRarity.Common => 4,
                CardRarity.Uncommon => 3,
                CardRarity.Rare => 2,
                CardRarity.Legendary => 1,
                _ => 4
            };
            for (int i = 0; i < weight; i++) weighted.Add(bp);
        }

        var result = new List<Card>();
        for (int i = 0; i < count; i++)
            result.Add(Instantiate(weighted[rng.Next(weighted.Count)]));
        return result;
    }

    /// <summary>Picks <paramref name="choices"/> cards using post-draft rarity odds (50% Common / 30% Uncommon / 15% Rare / 5% Legendary). Falls back to any rarity when the target tier is empty for the school.</summary>
    public static List<Card> GetDraftChoices(CardSchool school, int choices = 3, int? seed = null)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var pool = Blueprints.Where(b => b.School == school).ToList();
        if (pool.Count == 0) return new List<Card>();

        var result = new List<Card>();
        for (int i = 0; i < choices; i++)
        {
            double roll = rng.NextDouble();
            CardRarity target = roll < 0.50 ? CardRarity.Common
                            : roll < 0.80 ? CardRarity.Uncommon
                            : roll < 0.95 ? CardRarity.Rare
                            : CardRarity.Legendary;

            var tierPool = pool.Where(b => b.Rarity == target).ToList();
            if (tierPool.Count == 0) tierPool = pool;
            if (tierPool.Count == 0) continue;

            result.Add(Instantiate(tierPool[rng.Next(tierPool.Count)]));
        }
        return result;
    }
}
