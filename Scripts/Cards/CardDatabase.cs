using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardDatabase — pure JSON/code path.
//
// All CSV support removed. Cards come from JSON via
// CardLoaderV2.LoadCardsFromJson, which calls RegisterPrebuiltCard
// for each card.
// ============================================================

public sealed class CardBlueprint
{
    public string Id;
    public CardSchool School;
    public CardRarity Rarity;
    public Card Prebuilt; // The compiled card template. Cloned on Instantiate.
}

public static class CardDatabase
{
    public static readonly List<CardBlueprint> Blueprints = new();

    // Register a card loaded from JSON (or built in code).
    // Called by CardLoaderV2 / JsonCardLoader.
    public static void RegisterPrebuiltCard(Card card)
    {
        if (card == null) { GD.PrintErr("RegisterPrebuiltCard: null card"); return; }

        var school = card.TopHalf?.School ?? card.BottomHalf?.School ?? CardSchool.Tinker;
        var topName = card.TopHalf?.Name ?? "";
        var botName = card.BottomHalf?.Name ?? "";

        Blueprints.Add(new CardBlueprint
        {
            Id = $"{school}:{topName}|{botName}",
            School = school,
            Rarity = card.Rarity,
            Prebuilt = card
        });
    }

    // Returns a NEW runtime Card instance (unique InstanceId) for play.
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
            Rarity = src.Rarity,
            TopHalf = src.TopHalf,
            BottomHalf = src.BottomHalf
        };
    }

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

    // -------- Deck building --------
    //
    // While the JSON pool is small (4 cards during refactor), these allow
    // duplicates so decks can still be built. When the pool grows past
    // your target deck size, you can switch back to unique-picking
    // behaviour by removing the duplicate-allowing code path.

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

    public static Card RandomCard(Random rng, Func<CardBlueprint, bool> filter = null)
    {
        var pool = (filter == null) ? Blueprints : Blueprints.Where(filter).ToList();
        if (pool.Count == 0) return null;

        return Instantiate(pool[rng.Next(pool.Count)]);
    }

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
