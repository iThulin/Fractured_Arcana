using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// PlayerDeckService.cs
//
// Purpose:        Bridges GuildSaveData.PlayerDeck (persistence)
//                 with the live Card instances used in combat.
//                 Hydrates OwnedCards → Cards at run start.
//                 Handles post-combat card acquisition (writing
//                 new OwnedCards back to the save).
//                 Enforces deck size limits.
// Layer:          System
// Collaborators:  GuildSaveData.cs / PlayerDeckSave / OwnedCard,
//                 CardDatabase.cs (blueprint lookup),
//                 SaveManager.cs (persists changes),
//                 DeckManager.cs (receives the hydrated deck),
//                 StarterDeckLoader.cs (seeds the first deck)
// See:            Progression design doc §2-3
// ============================================================

/// <summary>
/// Runtime service that reads <see cref="PlayerDeckSave"/> and
/// produces live <see cref="Card"/> instances for use in combat.
/// Also handles adding cards earned during a run back to the save.
/// </summary>
public static class PlayerDeckService
{
    // ── Run-start hydration ──────────────────────────────────────────────

    /// <summary>
    /// Produces a list of live <see cref="Card"/> instances from the
    /// player's active deck slot in <paramref name="save"/>.
    /// Blueprint ids that can't be resolved are skipped with a warning.
    /// If the active deck is empty or invalid, falls back to the full
    /// owned card list, then to a random starter deck.
    /// </summary>
    public static List<Card> HydrateActiveDeck(GuildSaveData save)
    {
        if (save?.PlayerDeck == null)
        {
            GD.PrintErr("[PlayerDeckService] PlayerDeck is null — falling back to random.");
            return CardDatabase.BuildRandomDeck(ParseSchool(save?.SelectedSchool), 10);
        }

        var deck = save.PlayerDeck;

        // If ActiveDeckInstanceIds is empty, treat all owned cards as active.
        IEnumerable<string> activeIds = (deck.ActiveDeckInstanceIds?.Count > 0)
            ? deck.ActiveDeckInstanceIds
            : deck.Cards?.Select(c => c.InstanceId) ?? Enumerable.Empty<string>();

        // Build an instanceId → OwnedCard lookup.
        var lookup = new Dictionary<string, OwnedCard>();
        if (deck.Cards != null)
            foreach (var oc in deck.Cards)
                if (!string.IsNullOrEmpty(oc.InstanceId))
                    lookup[oc.InstanceId] = oc;

        var cards = new List<Card>();
        foreach (var id in activeIds)
        {
            if (!lookup.TryGetValue(id, out var owned))
            {
                GD.PrintErr($"[PlayerDeckService] InstanceId '{id}' not found in owned cards — skipping.");
                continue;
            }

            var card = InstantiateOwnedCard(owned);
            if (card != null)
                cards.Add(card);
        }

        if (cards.Count == 0)
        {
            GD.PrintErr("[PlayerDeckService] HydrateActiveDeck produced 0 cards — falling back to random.");
            return CardDatabase.BuildRandomDeck(ParseSchool(save.SelectedSchool), 10);
        }

        GD.Print($"[PlayerDeckService] Hydrated {cards.Count} cards for run.");
        return cards;
    }

    // ── Card acquisition (post-combat reward) ───────────────────────────

    /// <summary>
    /// Adds a newly acquired <paramref name="blueprintId"/> to the player's
    /// owned collection in <paramref name="save"/>. The new copy starts at
    /// UpgradeTier 0 with no grafts and is NOT auto-slotted into the active
    /// deck — the player must do that in the deck editor.
    /// Also records the blueprintId in UnlockedCardBlueprintIds if not
    /// already present (so it shows up in future draft pools).
    /// Call this from your CardRewardScene when the player picks a card.
    /// </summary>
    public static OwnedCard AddCardToCollection(GuildSaveData save, string blueprintId)
    {
        if (save == null || string.IsNullOrEmpty(blueprintId)) return null;

        save.PlayerDeck ??= new PlayerDeckSave();
        save.PlayerDeck.Cards ??= new List<OwnedCard>();
        save.UnlockedCardBlueprintIds ??= new List<string>();

        var owned = new OwnedCard
        {
            BlueprintId  = blueprintId,
            InstanceId   = Guid.NewGuid().ToString("N"),
            UpgradeTier  = 0,
            Grafts       = new List<string>(),
            IsStarter    = false,
        };

        save.PlayerDeck.Cards.Add(owned);

        if (!save.UnlockedCardBlueprintIds.Contains(blueprintId))
            save.UnlockedCardBlueprintIds.Add(blueprintId);

        GD.Print($"[PlayerDeckService] Added '{blueprintId}' to collection (instance: {owned.InstanceId}).");
        return owned;
    }

    /// <summary>
    /// Slots an owned card (by InstanceId) into the active run deck,
    /// subject to <see cref="PlayerDeckSave.MaxDeckSize"/>.
    /// Returns false and logs a warning if the deck is full or the
    /// instance doesn't exist in the collection.
    /// </summary>
    public static bool SlotCard(GuildSaveData save, string instanceId)
    {
        if (save?.PlayerDeck == null) return false;

        var deck = save.PlayerDeck;
        deck.ActiveDeckInstanceIds ??= new List<string>();

        if (deck.ActiveDeckInstanceIds.Contains(instanceId))
        {
            GD.PrintErr($"[PlayerDeckService] Card '{instanceId}' is already slotted.");
            return false;
        }

        if (deck.ActiveDeckInstanceIds.Count >= PlayerDeckSave.MaxDeckSize)
        {
            GD.PrintErr($"[PlayerDeckService] Deck is full ({PlayerDeckSave.MaxDeckSize} cards).");
            return false;
        }

        bool exists = deck.Cards?.Any(c => c.InstanceId == instanceId) ?? false;
        if (!exists)
        {
            GD.PrintErr($"[PlayerDeckService] InstanceId '{instanceId}' not found in collection.");
            return false;
        }

        deck.ActiveDeckInstanceIds.Add(instanceId);
        return true;
    }

    /// <summary>
    /// Removes a non-starter card from the active deck slot (sends it
    /// to the stash). Starter cards are silently ignored.
    /// </summary>
    public static bool UnslotCard(GuildSaveData save, string instanceId)
    {
        if (save?.PlayerDeck == null) return false;

        var owned = save.PlayerDeck.Cards?.Find(c => c.InstanceId == instanceId);
        if (owned == null) return false;

        if (owned.IsStarter)
        {
            GD.PrintErr($"[PlayerDeckService] Cannot unslot starter card '{instanceId}'.");
            return false;
        }

        save.PlayerDeck.ActiveDeckInstanceIds?.Remove(instanceId);
        return true;
    }

    // ── Deck validity ────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the active deck meets the minimum size requirement.
    /// Call before starting a run to guard against empty/undersized decks.
    /// </summary>
    public static bool IsActiveDeckValid(GuildSaveData save)
    {
        int count = save?.PlayerDeck?.ActiveDeckInstanceIds?.Count ?? 0;
        return count >= PlayerDeckSave.MinDeckSize;
    }

    /// <summary>
    /// Diagnostic: logs the full owned collection and active deck to console.
    /// </summary>
    public static void PrintDeckState(GuildSaveData save)
    {
        if (save?.PlayerDeck == null) { GD.Print("[PlayerDeckService] No PlayerDeck."); return; }

        GD.Print($"[PlayerDeckService] Owned: {save.PlayerDeck.Cards?.Count ?? 0}, " +
                 $"Active: {save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0}");

        if (save.PlayerDeck.Cards == null) return;
        foreach (var c in save.PlayerDeck.Cards)
        {
            bool slotted = save.PlayerDeck.ActiveDeckInstanceIds?.Contains(c.InstanceId) ?? false;
            GD.Print($"  [{(slotted ? "ACTIVE" : "stash ")}] {c.BlueprintId} " +
                     $"tier:{c.UpgradeTier} starter:{c.IsStarter} id:{c.InstanceId[..8]}…");
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static Card InstantiateOwnedCard(OwnedCard owned)
    {
        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, owned.BlueprintId, StringComparison.OrdinalIgnoreCase));

        if (bp == null)
        {
            GD.PrintErr($"[PlayerDeckService] Blueprint not found: '{owned.BlueprintId}'. " +
                        $"Has the card been renamed or removed?");
            return null;
        }

        var card = CardDatabase.Instantiate(bp);

        // TODO: apply UpgradeTier via CardUpgradeApplier once that system exists.
        // TODO: apply Grafts via CardGraftApplier once that system exists.
        // Both are no-ops at tier 0 / empty grafts, so this is safe to ship.

        return card;
    }

    private static CardSchool ParseSchool(string school)
    {
        if (Enum.TryParse<CardSchool>(school, true, out var result))
            return result;
        return CardSchool.Elementalist;
    }
}
