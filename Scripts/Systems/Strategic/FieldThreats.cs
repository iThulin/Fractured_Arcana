using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// FieldThreats.cs
//
// Purpose:        What comes for the field party while it is on the road.
//                 Ruled 2026-09-22 (Magos): the party can be intercepted
//                 by patrols.
//
//                 Travel between anchors is ABSTRACT: the party is not
//                 rendered, no window is loaded, no hex is walked. That is
//                 a deliberate pacing choice and it is also what made the
//                 road free. A force crossing hostile ground with a
//                 cycle's spoils on its back and nothing able to touch it
//                 is a gift, and it is the exact gift patrols were given
//                 road bias to take away from the castle.
//
//                 Two sources, mirroring CastleThreats:
//
//                   PATROL      a hit and run after the goods. Takes a
//                               share of what the party is CARRYING and
//                               costs them time. Common, survivable, and
//                               the reason carrying the hold across the
//                               map is a decision rather than a formality.
//                   KINGDOM     soldiers, where the locals are hostile.
//                               The party is TURNED BACK to where it set
//                               out from, and someone is hurt. Expensive:
//                               a lost journey is lost lunations.
//
//                 Why the stakes are Carrying, travel phases and injury:
//                 those are the three things a TRAVELLING party has that
//                 persist in CycleState. It has no window, no Hull and no
//                 ExpeditionHP while abstract, so inventing a stat to
//                 damage would mean inventing a stat that only exists to
//                 be damaged.
//
//                 Why no tactical fight: a fight needs a battlefield, and
//                 an abstract journey has no ground to fight on. The
//                 castle gets one because it is parked SOMEWHERE. Giving
//                 the party one would mean picking a tile at random and
//                 pretending, which reads worse than an honest report.
//
//                 Rolls are DETERMINISTIC per lunation (world seed,
//                 lunation, the party's tile and its remaining phases), so
//                 reloading cannot reroll an interception away.
// Layer:          System (strategic)
// Collaborators:  CycleState, FieldParty (Carrying, TravelPhasesRemaining,
//                 DestX/DestY), CouncilQueries.StanceFor,
//                 CompanionInjurySystem (the injury), StrategicView
//                 .RunLunationTick (the caller, immediately BEFORE
//                 FieldMarch.Tick)
// See:            docs/session_log_2026-09-21_expedition_v2_step1_data_layer.md
// ============================================================

/// <summary>What caught the party.</summary>
public enum FieldThreatKind
{
    None = 0,
    PatrolIntercept = 1,
    KingdomIntercept = 2,
}

/// <summary>The outcome of one lunation's interception roll. Runtime only: the
/// state changes are already written into the cycle by the time this returns.</summary>
public sealed class FieldThreatResult
{
    public FieldThreatKind Kind = FieldThreatKind.None;
    public string Report = "";
    public bool Happened => Kind != FieldThreatKind.None;
}

/// <summary>Rolls and resolves attacks on a field party crossing the map.</summary>
public static class FieldThreats
{
    // ── Tuning (starting values, all of them) ────────────────────────────

    /// <summary>Chance a single TILE of road brings trouble (2026-09-28).
    ///
    /// <para>Was a roll once per lunation for every party still walking. Under
    /// the day clock a party walks a tile every four days, so any march that
    /// began and ended between two new moons was never rolled at all, whatever
    /// ground it crossed: the road was safe by accident of timing. The roll now
    /// happens as each tile is walked, which makes distance the risk directly
    /// and ends the timing gap.</para>
    ///
    /// <para>Sized against the table below, which was the per-moon roll's
    /// measured intent: 5% a tile on neutral ground puts a twelve-tile march at
    /// 54% clear (was 54.7%), a three-tile hop at 86% (83.8%).</para></summary>
    public const int TileChancePercent = 5;

    /// <summary>Stance of the tile they are walking.</summary>
    public const int HostileBonus = 7;
    public const int UnfriendlyBonus = 3;
    public const int FriendlyPenalty = -3;
    public const int AlliedPenalty = -4;

    /// <summary>Each companion past the first lowers the odds a tile. Numbers
    /// deter opportunists, and this is the lever that makes the roster picker a
    /// decision about the ROAD and not only about the dive at the far end.</summary>
    public const int EscortPercentEach = 2;

    /// <summary>Most the escort can ever take off, and the floor under the
    /// result. A big party is safer, never safe: an interception the player can
    /// switch off by over-staffing is a mechanic that stops existing the moment
    /// it is understood.</summary>
    public const int MaxEscortReduction = 4;
    public const int MinTileChancePercent = 2;
    public const int MaxTileChancePercent = 40;

    /// <summary>Share of what the party is carrying a patrol makes off with.</summary>
    public const int PatrolTakePercent = 35;

    /// <summary>Days a patrol costs them: they scatter and regroup. Two
    /// tiles' walking. (Was "3 phases" added to the tiles remaining, which the
    /// day clock recomputes from distance every step, so it had stopped costing
    /// anything.)</summary>
    public const int PatrolDelayDays = 8;

    /// <summary>Lunations of injury a kingdom interception inflicts on one
    /// companion. Matches the infirmary's own units so recovery runs on the
    /// clock that already exists.</summary>
    public const int KingdomInjuryLunations = 2;

    /// <summary>Odds that an interception in unfriendly or worse ground is
    /// soldiers rather than opportunists.
    ///
    /// <para>40, not 55. A turn-back costs the whole march, so at 55 a long
    /// hostile crossing failed outright half the time and hostile ground became
    /// impassable by attrition. At 40 most hostile interceptions are robberies:
    /// the party gets through, poorer. Crossing a hostile kingdom is expensive,
    /// which is correct, rather than closed, which is not.</para></summary>
    public const int KingdomSharePercent = 40;

    // ── Measured behaviour at the values above ───────────────────────────
    //
    //    20,000 simulated journeys per cell, rolling once per lunation and
    //    decrementing 8 phases per lunation, in the same order the live code
    //    uses (roll, then tick).
    //
    //      journey / ground / party     clear   robbed   turned back
    //      short  3p  neutral  x1       83.8%    16.2%      0.0%
    //      short  3p  neutral  x3       96.2%     3.8%      0.0%
    //      short  3p  hostile  x1       59.3%    24.5%     16.2%
    //      medium 6p  neutral  x1       78.2%    21.8%      0.0%
    //      medium 6p  hostile  x1       53.0%    24.6%     22.3%
    //      long  12p  neutral  x1       54.7%    45.3%      0.0%
    //      long  12p  neutral  x3       73.6%    26.4%      0.0%
    //      long  12p  hostile  x1       22.8%    37.8%     39.4%
    //      long  12p  hostile  x3       36.5%    33.6%     29.9%
    //
    //    What the shape says, and what to watch in play: a short hop in
    //    friendly country is nearly free, which is right, because the anchor
    //    network is supposed to be usable. A long haul is a real gamble even at
    //    peace. Escort moves every row by 10 to 20 points, so the roster picker
    //    is a decision about the ROAD and not only about the dive. Hostile
    //    ground is expensive rather than closed.
    //
    //    That table is the per-moon roll's INTENT, kept as the target. The
    //    per-tile roll (2026-09-28, closed-form, one interception a march)
    //    lands on it:
    //
    //      journey / ground / party     clear   robbed   turned back
    //      short   3  neutral  x1       85.7%    14.3%      0.0%
    //      short   3  hostile  x1       68.1%    19.1%     12.7%
    //      medium  6  neutral  x1       73.5%    26.5%      0.0%
    //      medium  6  hostile  x1       46.4%    32.1%     21.4%
    //      long   12  neutral  x1       54.0%    46.0%      0.0%
    //      long   12  neutral  x3       78.5%    21.5%      0.0%
    //      long   12  hostile  x1       21.6%    47.1%     31.4%
    //      long   12  hostile  x3       36.8%    37.9%     25.3%
    //
    //    The number most likely to be wrong is TileChancePercent, because it
    //    is the one that compounds.

    // ── The roll ─────────────────────────────────────────────────────────

    /// <summary>Roll one party for the tile it has just walked onto. Called by
    /// FieldMarch.StepDay after the step and before arrival, so the last tile
    /// of a march (the destination) is never where they are caught. At most one
    /// interception a march: once waylaid they are watchful, and a second
    /// robbery on the same road would be a death spiral rather than a risk.
    ///
    /// <para>Returns the report, or null when nothing happened.</para></summary>
    public static string RollForStep(CycleState cycle, FieldParty party)
    {
        if (party == null || party.InterceptedThisMarch)
        {
            return null;
        }
        var result = RollForParty(cycle, party);
        if (!result.Happened)
        {
            return null;
        }
        party.InterceptedThisMarch = true;
        ScryInbox.Post(cycle, ScryChannel.Sending, $"{party.Name}: on the road", result.Report, party.Id, "road");
        return result.Report;
    }

    /// <summary>One party, one tile. Non-throwing; a partial save degrades
    /// to "nothing happened" rather than breaking the world tick.</summary>
    public static FieldThreatResult RollForParty(CycleState cycle, FieldParty party)
    {
        var none = new FieldThreatResult();
        if (cycle?.World == null || party == null
            || party.State != FieldPartyState.Travelling)
        {
            return none;
        }

        // The ground they are crossing: the tile they just walked onto. Was the
        // journey's midpoint while the roll was once a moon; a roll per tile
        // can read the real ground at each step.
        string kingdomId = cycle.World.InBounds(party.X, party.Y)
            ? (cycle.World.GetTile(party.X, party.Y).KingdomId ?? "")
            : "";
        var stance = CouncilQueries.StanceFor(cycle, kingdomId);

        int escort = EscortCount(cycle, party);
        int reduction = Math.Min(MaxEscortReduction,
                                 Math.Max(0, escort - 1) * EscortPercentEach);

        int chance = TileChancePercent
                   + stance switch
                     {
                         KingdomStance.Hostile => HostileBonus,
                         KingdomStance.Unfriendly => UnfriendlyBonus,
                         KingdomStance.Friendly => FriendlyPenalty,
                         KingdomStance.Allied => AlliedPenalty,
                         _ => 0,
                     }
                   - reduction;
        chance = Math.Clamp(chance, MinTileChancePercent, MaxTileChancePercent);

        var rng = RngFor(cycle, party);
        if (rng.Next(100) >= chance)
        {
            return none;
        }

        bool soldiers = stance <= KingdomStance.Unfriendly
                        && rng.Next(100) < KingdomSharePercent;
        return soldiers
            ? ResolveKingdomIntercept(cycle, party, kingdomId)
            : ResolvePatrolIntercept(cycle, party, rng);
    }

    // ── Resolutions ──────────────────────────────────────────────────────

    /// <summary>Opportunists. They want what the party is carrying, they take a
    /// share of it, and they cost the party time. They do not try to kill
    /// anybody, which is what keeps this the common case.</summary>
    private static FieldThreatResult ResolvePatrolIntercept(
        CycleState cycle, FieldParty party, Random rng)
    {
        var carrying = party.Carrying;
        bool hadGoods = carrying != null && !carrying.IsEmpty;

        string took = "nothing worth taking";
        if (hadGoods)
        {
            int g = Share(carrying.Gold);
            int s = Share(carrying.Splinters);
            int m = Share(carrying.Materials);
            int u = Share(carrying.Supplies);
            carrying.Gold -= g;
            carrying.Splinters -= s;
            carrying.Materials -= m;
            carrying.Supplies -= u;
            took = $"{g}g {s}sp {m}mat {u}sup";
        }

        party.TravelDayAccum -= PatrolDelayDays;

        var result = new FieldThreatResult
        {
            Kind = FieldThreatKind.PatrolIntercept,
            Report = hadGoods
                ? $"{party.Name} is waylaid on the road. The patrol takes {took} and rides off; "
                  + $"the party loses {PatrolDelayDays} days regrouping."
                : $"{party.Name} is waylaid on the road. The patrol finds nothing worth taking "
                  + $"and costs them {PatrolDelayDays} days.",
        };
        GD.Print($"[FieldThreats] {result.Report}");
        return result;
    }

    /// <summary>Soldiers. The party does not get through: they are turned back
    /// to where they set out from and the journey is lost, with someone hurt.
    ///
    /// <para>Turning them BACK rather than killing them is the point. The cost
    /// is the lunations the march already spent plus the lunations it will spend
    /// again, which is expensive without being a wipe, and it leaves the player
    /// a decision (try again, go another way, or make peace) rather than a
    /// funeral.</para></summary>
    private static FieldThreatResult ResolveKingdomIntercept(
        CycleState cycle, FieldParty party, string kingdomId)
    {
        party.State = FieldPartyState.AtAnchor;
        party.TravelPhasesRemaining = 0;
        party.DestinationAnchorKey = "";
        party.DestX = -1;
        party.DestY = -1;

        string hurt = InjureOne(cycle, party);
        string who = string.IsNullOrEmpty(kingdomId) ? "Soldiers" : $"{kingdomId}'s soldiers";

        var result = new FieldThreatResult
        {
            Kind = FieldThreatKind.KingdomIntercept,
            Report = $"{who} bar the road. {party.Name} is turned back and the march is lost."
                   + (string.IsNullOrEmpty(hurt)
                        ? ""
                        : $" {hurt} is hurt and out for {KingdomInjuryLunations} lunation(s)."),
        };
        GD.Print($"[FieldThreats] {result.Report}");
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static int Share(int amount)
        => amount <= 0 ? 0 : Math.Max(1, amount * PatrolTakePercent / 100);

    private static int EscortCount(CycleState cycle, FieldParty party)
    {
        if (cycle.Companions == null || party.MemberCompanionIds == null)
        {
            return 0;
        }
        int n = 0;
        foreach (var id in party.MemberCompanionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c != null && c.IsRecruited && !c.IsPermadead && !c.IsInjured)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>Hurt the first able member. Returns the name, or empty if the
    /// party is the wizard alone, in which case the lost march IS the whole
    /// cost and no one is invented to bleed.</summary>
    private static string InjureOne(CycleState cycle, FieldParty party)
    {
        if (cycle.Companions == null || party.MemberCompanionIds == null)
        {
            return "";
        }
        foreach (var id in party.MemberCompanionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c != null && c.IsRecruited && !c.IsPermadead && !c.IsInjured)
            {
                c.InjuredLunationsRemaining = KingdomInjuryLunations;
                return c.Name;
            }
        }
        return "";
    }

    /// <summary>Deterministic per lunation, party position and journey. Same
    /// anti-save-scum rule as CastleThreats: a threat table you can reload away
    /// is not a threat table.</summary>
    private static Random RngFor(CycleState cycle, FieldParty party)
    {
        unchecked
        {
            int seed = cycle.WorldSeed;
            seed = (seed * 397) ^ (cycle.Calendar?.AbsoluteDay ?? 0);
            seed = (seed * 397) ^ party.X;
            seed = (seed * 397) ^ party.Y;
            seed = (seed * 397) ^ party.TravelPhasesRemaining;
            seed = (seed * 397) ^ (party.Id?.GetHashCode() ?? 0);
            return new Random(seed);
        }
    }
}
