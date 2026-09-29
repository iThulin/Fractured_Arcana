using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardHalls.cs
//
// Purpose:        The two card buildings' rules (campus_building_upgrades_
//                 design_v1 §13): the Dissolution Chamber and the Arcane
//                 Library.
//
//                 One deck floor (ruled 2026-09-29). Before this, a run
//                 needed 10 active cards (PlayerDeckSave.MinDeckSize) while
//                 the Chamber's T3 "floor 3" only moved the DISENCHANT floor,
//                 so it did nothing. Now there is one floor for both:
//                 10, or 7 under Thin Deck.
//
//                 Dissolution Chamber T3 doctrines:
//                   Thin Deck      the floor is 7, and a wizard whose deck
//                                  sits exactly at the floor draws one extra
//                                  card on the first turn of every fight.
//                   Transmutation  a card may be turned into a random other
//                                  unlocked card of the same school and
//                                  rarity, for the splinters disenchanting it
//                                  would have paid. Upgrades are refunded as
//                                  splinters. Seeded by the card, so a reload
//                                  shows the same result.
//                 Arcane Library:
//                   T2             commissions (research) open here, moved
//                                  down from T3; T2 runs 2 at once, T3 runs 3.
//                   T3 Forbidden Archives  research takes 2 lunations, not 3.
//                   T3 Chancery    every parley opens with your school's
//                                  sweep card in hand, and every Lore term's
//                                  valuation starts read.
// Layer:          Data / rules
// Collaborators:  Charters (the gate), PlayerDeckService, DeckEditorUi,
//                 CombatManager (the first-turn draw), CardCommissionService,
//                 NegotiationState (Chancery), DisenchantValues.
// ============================================================

/// <summary>Dissolution Chamber and Arcane Library rules. Stateless.</summary>
public static class CardHalls
{
    // ── Dissolution Chamber ──────────────────────────────────────────────
    public const string DissolutionId = "dissolution_chamber";
    public const string ThinDeck = "thin_deck";
    public const string Transmutation = "transmutation";

    /// <summary>The deck floor with no doctrine: the run minimum and the disenchant floor.</summary>
    public const int BaseDeckFloor = 10;

    /// <summary>The floor under Thin Deck.</summary>
    public const int ThinDeckFloor = 7;

    public static bool ThinDeckActive(GuildSaveData save) => Charters.IsActive(save, DissolutionId, ThinDeck);

    public static bool TransmutationActive(GuildSaveData save) => Charters.IsActive(save, DissolutionId, Transmutation);

    /// <summary>THE deck floor: the fewest active cards a run needs, and the
    /// count below which an active card cannot be disenchanted.</summary>
    public static int DeckFloor(GuildSaveData save) => ThinDeckActive(save) ? ThinDeckFloor : BaseDeckFloor;

    /// <summary>Thin Deck's payoff: the active deck sits exactly at the floor.</summary>
    public static bool ThinDeckDraws(GuildSaveData save)
        => ThinDeckActive(save)
           && (save?.PlayerDeck?.ActiveDeckInstanceIds?.Count ?? 0) == ThinDeckFloor;

    /// <summary>Splinters a transmutation costs: what disenchanting the card
    /// would have paid, before its upgrade refund.</summary>
    public static int TransmuteCost(OwnedCard card) => DisenchantValues.BaseYieldOf(card);

    /// <summary>The card this one would become, or null when there is none to
    /// become (no other unlocked card of its school and rarity).</summary>
    public static CardBlueprint TransmuteTarget(GuildSaveData save, OwnedCard card)
    {
        if (save?.UnlockedCardBlueprintIds == null || card == null)
        {
            return null;
        }
        var bp = CardDatabase.Blueprints.Find(b => string.Equals(b.Id, card.BlueprintId, StringComparison.OrdinalIgnoreCase));
        if (bp == null)
        {
            return null;
        }
        var pool = new List<CardBlueprint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in save.UnlockedCardBlueprintIds)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                continue;   // a duplicate listing must not weight the roll
            }
            var other = CardDatabase.Blueprints.Find(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
            if (other == null || other.Id == bp.Id || other.School != bp.School || other.Rarity != bp.Rarity)
            {
                continue;
            }
            if (other.Rarity == CardRarity.Legendary)
            {
                continue;   // Regalia are never made at a bench
            }
            pool.Add(other);
        }
        if (pool.Count == 0)
        {
            return null;
        }
        pool.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        int seed = 17;
        foreach (char ch in (card.InstanceId ?? "") + "|" + card.BlueprintId)
        {
            seed = unchecked(seed * 31 + ch);
        }
        return pool[(int)((uint)seed % (uint)pool.Count)];
    }

    /// <summary>Why this card cannot be transmuted now, or null.</summary>
    public static string CannotTransmuteReason(GuildSaveData save, OwnedCard card)
    {
        if (!TransmutationActive(save))
        {
            return "Needs Transmutation chartered at the Grand Hall.";
        }
        if (card == null || card.IsStarter || card.IsRegalia)
        {
            return "Starter cards and Regalia cannot be transmuted.";
        }
        if (TransmuteTarget(save, card) == null)
        {
            return "No other unlocked card of this school and rarity to become.";
        }
        int cost = TransmuteCost(card);
        if (save.ArcaneSplinters + DisenchantValues.UpgradeRefundOf(card) < cost)
        {
            return $"Transmuting costs {cost} splinters.";
        }
        return null;
    }

    /// <summary>Turn the card into its target, in place (same instance, same deck
    /// slot). Upgrades are refunded as splinters; grafts and the cast count are
    /// cleared. Returns the report line, or null when refused.</summary>
    public static string TryTransmute(GuildSaveData save, OwnedCard card)
    {
        if (CannotTransmuteReason(save, card) != null)
        {
            return null;
        }
        var target = TransmuteTarget(save, card);
        int cost = TransmuteCost(card);
        int refund = DisenchantValues.UpgradeRefundOf(card);
        string before = card.BlueprintId;

        save.ArcaneSplinters += refund - cost;
        card.BlueprintId = target.Id;
        card.TopTier = 0;
        card.BotTier = 0;
        card.PointsSpent = 0;
        card.Grafts?.Clear();
        card.CastCount = 0;
        SaveManager.Save();
        GD.Print($"[Transmute] '{before}' became '{target.Id}' ({cost} splinters, {refund} refunded).");
        return refund > 0
            ? $"Transmuted into {target.Prebuilt?.TopHalf?.Name ?? target.Id}. Its upgrades return as {refund} splinters."
            : $"Transmuted into {target.Prebuilt?.TopHalf?.Name ?? target.Id}.";
    }

    // ── Arcane Library ───────────────────────────────────────────────────
    public const string LibraryId = "arcane_library";
    public const string ForbiddenArchives = "forbidden_archives";
    public const string Chancery = "chancery";

    /// <summary>Research lunations with and without the Forbidden Archives.</summary>
    public const int ArchivesResearchLunations = 2;

    public static int LibraryTier(GuildSaveData save)
    {
        var b = CampusBlight.Find(save, LibraryId);
        return b != null && b.IsFunctional ? b.Tier : 0;
    }

    public static bool ForbiddenArchivesActive(GuildSaveData save) => Charters.IsActive(save, LibraryId, ForbiddenArchives);

    public static bool ChanceryActive(GuildSaveData save) => Charters.IsActive(save, LibraryId, Chancery);

    /// <summary>Commissions open at the Library's second tier (moved down from T3).</summary>
    public static bool CommissionsOpen(GuildSaveData save) => LibraryTier(save) >= 2;
}
