using System;
using System.Collections.Generic;
using Godot;

// ============================================================
// ChannelResolver.cs
//
// Purpose:        Resolves a channeled cast by applying the next
//                 upgrade stage to the relevant card half and
//                 returning it for cast-time use. Nothing is
//                 saved — the upgrade is temporary and discarded
//                 after resolution.
// Layer:          System
// Collaborators:  CardUpgradeApplier.cs (Apply),
//                 OwnedCard (current tier + chosen branch),
//                 GuildSaveData / PlayerDeckService (lookup),
//                 CombatManager.cs (call site)
// ============================================================

public static class ChannelResolver
{
    /// <summary>
    /// Returns a temporary upgraded CardHalf for a channeled cast.
    /// Applies the next upgrade stage above the card's current tier.
    /// Returns null if the card cannot be channeled (already max stage,
    /// no upgrade defined, or CanChannel is false).
    /// </summary>
    public static CardHalf ResolveChannel(CardHalf half, Card cardInstance)
    {
        if (half == null || !half.CanChannel) return null;
        if (cardInstance == null) return null;

        var save = SaveManager.ActiveSave;
        if (save?.PlayerDeck?.Cards == null) return null;

        OwnedCard owned = null;
        foreach (var c in save.PlayerDeck.Cards)
        {
            if (string.Equals(c.BlueprintId, cardInstance.BlueprintId,
                StringComparison.OrdinalIgnoreCase))
            { owned = c; break; }
        }

        bool isTop = half == cardInstance.TopHalf;

        int currentTopTier = owned?.TopTier ?? 0;
        int currentBotTier = owned?.BotTier ?? 0;

        // Channel previews the next tier on the relevant half
        int channelTopTier = isTop
            ? Mathf.Min(currentTopTier + 1, 4)
            : currentTopTier;
        int channelBotTier = isTop
            ? currentBotTier
            : Mathf.Min(currentBotTier + 1, 4);

        var upgraded = CardUpgradeApplier.Apply(
            cardInstance.BlueprintId, channelTopTier, channelBotTier);
        if (upgraded == null) return null;

        return isTop ? upgraded.TopHalf : upgraded.BottomHalf;
    }

    /// <summary>
    /// Returns the extra mana cost for channeling. Currently flat 1.
    /// </summary>
    public static int ChannelManaCost => 1;
}