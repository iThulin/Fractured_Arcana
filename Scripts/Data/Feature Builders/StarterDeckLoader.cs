using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// StarterDeckLoader.cs
//
// Purpose:        Reads Data/StarterDecks/{school}_starter.json
//                 and builds a List<Card> from CardDatabase
//                 blueprints. Falls back to BuildRandomDeck if
//                 the file is missing or a card id can't be
//                 resolved. Also writes the OwnedCard entries
//                 into a PlayerDeckSave so the persistent deck
//                 is seeded on first run.
// Layer:          Loader
// Collaborators:  CardDatabase.cs (blueprint lookup),
//                 GuildSaveData.cs (PlayerDeckSave / OwnedCard),
//                 DeckManager.cs (calls BuildStarterCards),
//                 SaveManager.cs (persists the result)
// See:            README §4.1 (Adding a Card),
//                 Progression design doc §2 (Starter Decks)
// ============================================================

/// <summary>
/// Loads a school's curated starter deck from
/// <c>Data/StarterDecks/{school}_starter.json</c> and hydrates
/// <see cref="Card"/> instances from the live <see cref="CardDatabase"/>.
/// On first call for a save, also seeds <see cref="PlayerDeckSave"/>
/// so the persistent deck exists before any run begins.
/// </summary>
public static class StarterDeckLoader
{
    private const string STARTER_DIR = "res://Data/StarterDecks/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns a ready-to-use list of <see cref="Card"/> instances for
    /// <paramref name="school"/>. Blueprint ids that can't be resolved are
    /// skipped with a warning — check the console if your deck is short.
    /// Falls back to <see cref="CardDatabase.BuildRandomDeck"/> (count 10)
    /// when the JSON file is absent.
    /// </summary>
    public static List<Card> BuildStarterCards(CardSchool school)
    {
        var entries = LoadEntries(school);
        if (entries == null || entries.Count == 0)
        {
            GD.PrintErr($"[StarterDeckLoader] No starter file for {school}, falling back to random deck.");
            return CardDatabase.BuildRandomDeck(school, 10);
        }

        var cards = new List<Card>();
        foreach (var entry in entries)
        {
            var bp = CardDatabase.Blueprints.Find(b =>
                string.Equals(b.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

            if (bp == null)
            {
                GD.PrintErr($"[StarterDeckLoader] Blueprint not found: '{entry.Id}' — skipping. " +
                            $"Check Data/StarterDecks/{SchoolToFileName(school)} and verify the id " +
                            $"matches a registered CardBlueprint.Id (format: 'school:TopName|BotName').");
                continue;
            }

            for (int i = 0; i < entry.Count; i++)
                cards.Add(CardDatabase.Instantiate(bp));
        }

        if (cards.Count == 0)
        {
            GD.PrintErr($"[StarterDeckLoader] All entries failed for {school}, falling back to random deck.");
            return CardDatabase.BuildRandomDeck(school, 10);
        }

        GD.Print($"[StarterDeckLoader] Built {cards.Count} starter cards for {school}.");
        return cards;
    }

    /// <summary>
    /// Seeds <paramref name="save"/>.PlayerDeck with <see cref="OwnedCard"/>
    /// entries for each card in the starter deck, then sets all of them as
    /// active (they all go into the first run). No-op if PlayerDeck already
    /// has cards (i.e. a returning save).
    /// Call once from <c>SaveManager.NewGame</c> after school selection.
    /// </summary>
    public static void SeedStarterDeck(GuildSaveData save, CardSchool school)
    {
        if (save == null) return;

        // Initialize PlayerDeck if it hasn't been set yet.
        save.PlayerDeck ??= new PlayerDeckSave();

        if (save.PlayerDeck.Cards != null && save.PlayerDeck.Cards.Count > 0)
        {
            GD.Print("[StarterDeckLoader] PlayerDeck already seeded, skipping.");
            return;
        }

        save.PlayerDeck.Cards = new List<OwnedCard>();
        save.PlayerDeck.ActiveDeckInstanceIds = new List<string>();

        var entries = LoadEntries(school);
        if (entries == null) entries = new List<StarterEntry>();

        foreach (var entry in entries)
        {
            var bp = CardDatabase.Blueprints.Find(b =>
                string.Equals(b.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

            if (bp == null) continue;

            for (int i = 0; i < entry.Count; i++)
            {
                var owned = new OwnedCard
                {
                    BlueprintId = bp.Id,
                    InstanceId = Guid.NewGuid().ToString("N"),
                    Grafts = new List<string>(),
                    IsStarter = true,
                };
                save.PlayerDeck.Cards.Add(owned);
                save.PlayerDeck.ActiveDeckInstanceIds.Add(owned.InstanceId);
            }
        }

        // If the entries were all missing from the database, seed from random
        // so the save is never left with an empty deck.
        if (save.PlayerDeck.Cards.Count == 0)
        {
            GD.PrintErr("[StarterDeckLoader] SeedStarterDeck produced 0 cards — seeding 10 random.");
            var random = CardDatabase.BuildRandomDeck(school, 10);
            foreach (var card in random)
            {
                var bp = CardDatabase.Blueprints.Find(b =>
                    b.Prebuilt == card || b.Id == $"{card.TopHalf?.School}:{card.TopHalf?.Name}|{card.BottomHalf?.Name}");

                var owned = new OwnedCard
                {
                    BlueprintId = bp?.Id ?? card.CardName,
                    InstanceId = Guid.NewGuid().ToString("N"),
                    Grafts = new List<string>(),
                    IsStarter = true,
                };
                save.PlayerDeck.Cards.Add(owned);
                save.PlayerDeck.ActiveDeckInstanceIds.Add(owned.InstanceId);
            }
        }

        GD.Print($"[StarterDeckLoader] Seeded {save.PlayerDeck.Cards.Count} OwnedCards into PlayerDeckSave.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static List<StarterEntry> LoadEntries(CardSchool school)
    {
        string path = $"{STARTER_DIR}{SchoolToFileName(school)}";
        if (!FileAccess.FileExists(path))
        {
            GD.PrintErr($"[StarterDeckLoader] File not found: {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var doc = JsonSerializer.Deserialize<StarterDeckDoc>(file.GetAsText(), JsonOptions);
            return doc?.Cards;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[StarterDeckLoader] Parse error in {path}: {e.Message}");
            return null;
        }
    }

    private static string SchoolToFileName(CardSchool school)
        => $"{school.ToString().ToLowerInvariant()}_starter.json";

    // ── JSON DTOs ────────────────────────────────────────────────────────

    private class StarterDeckDoc
    {
        public string School { get; set; }
        public List<StarterEntry> Cards { get; set; }
    }

    private class StarterEntry
    {
        public string Id { get; set; }
        public int Count { get; set; } = 1;
    }
}
