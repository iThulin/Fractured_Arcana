using System;
using System.Collections.Generic;

// ============================================================
// RemoteCasting.cs
//
// Purpose:        Remote casting through the lens (campus_building_upgrades_
//                 design_v1 §20; expedition handoff §4.5). When the wizard is
//                 not on the board, they can still reach into a fight from the
//                 campus: a few times a fight, a spell from the wizard's own
//                 deck is cast through a fielded ally, who is the focus (range
//                 and line of sight are measured from them). The ally spends
//                 nothing; the lens does.
//
//                 Hard rule (handoff §4.5): a remote spell never deals damage.
//                 Which spells qualify is decided by what they DO, not by an
//                 authored list: every effect in the half must be on the
//                 allowlist below (movement, terrain and hazards, shields and
//                 armour and healing, cleansing, non-damaging conditions,
//                 reading intent), and the targeting must be one the item
//                 path can aim. SignatureSpell.CastableRemotely is the derived
//                 fact's old name; nothing sets it by hand.
//
//                 Where it works:
//                   a field party's dive without the wizard   Scrying Chambers
//                                                             T1+, 1 cast a
//                                                             fight, 2 Essence
//                   a posting's defense, a castle defense     The Hand only
//                   before the wizard arrives
//                 The Hand (Scrying Chambers T3 doctrine): +1 cast a fight,
//                 and the lens reaches posting and castle defenses.
// Layer:          Data / rules
// Collaborators:  CombatManager.RemoteCast.cs (tray card, picker, casting
//                 through the item seam), LoreHalls (The Hand), Charters,
//                 CampusBlight (functional tier), GrimoireState (Essence),
//                 StatusCatalog (which conditions hurt).
// ============================================================

/// <summary>Remote casting rules. Stateless.</summary>
public static class RemoteCasting
{
    public const int BaseCastsPerFight = 1;
    public const int TheHandBonusCasts = 1;

    /// <summary>Essence a remote cast draws in a dive (the expedition's pool).</summary>
    public const int EssenceCost = 2;

    /// <summary>Where the lens is being asked to reach.</summary>
    public enum Reach
    {
        None,          // the wizard is on the board: they cast in person
        Dive,          // a field party's dive
        Defense,       // a posting's defense, or a castle defense before the wizard arrives
    }

    public static int ScryingTier(GuildSaveData save)
    {
        var b = CampusBlight.Find(save, LoreHalls.ScryingId);
        return b != null && b.IsFunctional ? b.Tier : 0;
    }

    public static bool TheHandActive(GuildSaveData save)
        => Charters.IsActive(save, LoreHalls.ScryingId, LoreHalls.TheHand);

    public static int CastsPerFight(GuildSaveData save)
        => BaseCastsPerFight + (TheHandActive(save) ? TheHandBonusCasts : 0);

    /// <summary>Why the lens cannot reach this fight at all, or null when it can.</summary>
    public static string UnavailableReason(GuildSaveData save, Reach reach)
    {
        switch (reach)
        {
            case Reach.None:
                return "The wizard is on the field and casts in person.";
            case Reach.Dive:
                return ScryingTier(save) >= 1 ? null : "Needs working Scrying Chambers: the lens is theirs.";
            case Reach.Defense:
                return TheHandActive(save) ? null : "Only The Hand (Scrying Chambers doctrine) reaches a defense.";
        }
        return "The lens cannot reach here.";
    }

    // ── What qualifies ───────────────────────────────────────────────────

    /// <summary>Leaf effects a remote spell may carry: none deals damage itself.
    /// Hazards (imbued tiles, glyphs) are placements; the ground does the rest.
    /// Left out on review because they hurt on their own: Push and aimed push
    /// (collision damage), Raise Terrain (crushes whoever stands there). Dash
    /// only on the caster (aimed at a unit it shoves), an imbued tile only
    /// without its bonus damage: both checked in EffectAllowed.</summary>
    private static readonly HashSet<string> AllowedEffects = new(StringComparer.Ordinal)
    {
        // Movement
        "TeleportEffect", "PullEffect", "MoveToTileEffect",
        // Terrain and hazards
        "CreateRubbleEffect", "ImbueAreaEffect", "ImbuePathEffect",
        "SeedGrowthEffect", "AdvanceGrowthEffect", "SpreadGrowthEffect", "EntangleEffect",
        "CreatePhaseTilesEffect", "HallowTileEffect", "HallowAreaEffect",
        "PlaceGlyphEffect", "PrepareGlyphEffect", "PrepareGlyphAreaEffect", "EtchWardEffect",
        "CreateMemorialEffect", "CreateMemorialGroundEffect", "CreateMemorialGroundAreaEffect",
        // Protection and mending
        "GiveShieldEffect", "GiveArmorEffect", "GiveTargetArmorEffect", "HealEffect", "TempBuffEffect",
        "CleanseDebuffsEffect",
        // Weakening and reading
        "RemoveArmorEffect", "DispelEffect", "PeekIntentEffect",
    };

    /// <summary>Composites whose children decide.</summary>
    private static readonly HashSet<string> Composites = new(StringComparer.Ordinal)
    {
        "SequenceEffect", "ConditionalEffect", "ForEachTargetEffect",
    };

    /// <summary>Conditions a remote spell may not lay even though they do not
    /// tick damage: turning an enemy on its friends, and a geas that draws blood.</summary>
    private static readonly HashSet<string> DeniedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dominated", "geas",
    };

    /// <summary>Can this half be cast through the lens? No damage anywhere in
    /// it, and a targeting shape the item path can aim.</summary>
    public static bool IsCastableRemotely(CardHalf half)
    {
        if (half == null || half.Effects == null || half.Effects.Length == 0)
            return false;
        if (!CombatManager.ItemCanAim(half.Targeting))
            return false;
        bool selfAimed = half.Targeting == null || half.Targeting is SelectSelfTarget;
        foreach (var e in half.Effects)
            if (!EffectAllowed(e, 0, selfAimed))
                return false;
        return true;
    }

    private static bool EffectAllowed(IEffect e, int depth, bool selfAimed)
    {
        if (e == null || depth > 8)
            return false;
        if (e.Tags != null)
            foreach (var t in e.Tags)
                if (t == "Damage" || t == "SelfDamage")
                    return false;
        string name = e.GetType().Name;
        if (Composites.Contains(name))
        {
            bool any = false;
            foreach (var c in e.Children)
            {
                any = true;
                if (!EffectAllowed(c, depth + 1, selfAimed))
                    return false;
            }
            return any;
        }
        if (e is ApplyStatusEffect st)
        {
            // A condition that hurts every turn is damage by another name.
            return StatusCatalog.IsKnown(st.StatusName)
                   && !DeniedStatuses.Contains(st.StatusName)
                   && StatusCatalog.Get(st.StatusName).Tone != StatusTone.Damage;
        }
        if (e is DashEffect)
            return selfAimed;                 // aimed at a unit, a dash is a shove
        if (e is ImbueTileEffect imbue)
            return imbue.BonusDamage == 0;    // the bonus lands at once
        return AllowedEffects.Contains(name);
    }

    /// <summary>The wizard's deck as remote spells: every distinct castable half
    /// of the active deck, upgrades applied. Fresh instances, never the deck's own.</summary>
    public static List<CardHalf> Spells(GuildSaveData save)
    {
        var list = new List<CardHalf>();
        var deck = save?.PlayerDeck;
        if (deck?.Cards == null || deck.Cards.Count == 0)
            return list;
        // HydrateActiveDeck deals a RANDOM deck when nothing resolves; the lens
        // must never offer spells the guild does not own, so check first.
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var oc in deck.Cards)
            if (oc != null && !string.IsNullOrEmpty(oc.InstanceId))
                owned.Add(oc.InstanceId);
        bool anyResolves = deck.ActiveDeckInstanceIds == null || deck.ActiveDeckInstanceIds.Count == 0
            ? owned.Count > 0
            : deck.ActiveDeckInstanceIds.Exists(owned.Contains);
        if (!anyResolves)
            return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in PlayerDeckService.HydrateActiveDeck(save))
        {
            foreach (var half in new[] { card?.TopHalf, card?.BottomHalf })
            {
                if (half == null || !seen.Add(half.Name + "|" + half.RulesText))
                    continue;
                if (IsCastableRemotely(half))
                    list.Add(half);
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }
}
