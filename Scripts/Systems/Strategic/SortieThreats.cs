using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SortieThreats.cs
//
// Purpose:        What the world does to a force left IN THE FIELD while
//                 the wizard looks elsewhere. Ruled 2026-09-23 (Magos).
//
//                 The scrying table (increment 7) lets a run be frozen
//                 mid-sortie and resumed later. Until this file, a frozen
//                 force was IMMUNE: CastleThreats gates on the castle
//                 being parked, FieldThreats on the party travelling, and
//                 a force that is neither slipped through both. Leaving
//                 the castle in the field was strictly safer than parking
//                 it, which inverts the entire point of parking.
//
//                 Three consequences, each mild alone and honest together:
//
//                   UPKEEP     a month camped costs the castle fuel and the
//                              party rations. Eating is not free.
//                   RAID       patrols find a force that is not moving. A
//                              share of what it has EARNED this sortie is
//                              taken and it is hurt. The chance climbs each
//                              lunation it sits, because the locals notice.
//                   ASSAULT    a hostile kingdom sends soldiers at a frozen
//                              CASTLE. Reuses PendingCastleAssaultKingdomId
//                              and the tactical defence that already exists
//                              for a parked one; the sortie's Hull takes the
//                              opening blow.
//
//                 One rule above all of them: NOTHING DIES OFF-SCREEN. A
//                 frozen force is never reduced below one Hull, one Health,
//                 or zero fuel, and is never wiped. The player comes back
//                 to a damaged force, never to a funeral they did not
//                 watch. The table encourages leaving a force out; a
//                 penalty harsh enough to stop that would make the table
//                 decoration.
//
//                 Every event is written as a SENDING into ScryInbox, so
//                 the stone flares with it the next time the wizard sits
//                 down, and as a line in PendingSiegeReports for the map.
//
//                 Deterministic per lunation, force and tile, like every
//                 other threat table here.
// Layer:          System (strategic)
// Collaborators:  CycleState (CastleSortie, FieldParties[].Sortie,
//                 ScryInbox, PendingCastleAssaultKingdomId),
//                 CouncilQueries.StanceFor, ExpeditionAnchors.Crew,
//                 StrategicView.RunLunationTick (the caller)
// See:            docs/session_log_2026-09-21_expedition_v2_step1_data_layer.md
// ============================================================

/// <summary>Rolls and resolves what happens to frozen sorties on the tick.</summary>
public static class SortieThreats
{
    // ── Tuning (starting values) ─────────────────────────────────────────

    /// <summary>Fuel the castle burns holding station for a lunation. The
    /// furnace is banked, not out.</summary>
    public const int CastleIdleFuel = 3;

    /// <summary>Rations a party eats camped for a lunation.</summary>
    public const int PartyIdleRations = 4;

    /// <summary>Chance anything finds a frozen force on its first lunation
    /// alone.</summary>
    public const int BaseChancePercent = 12;

    /// <summary>Added per lunation ALREADY sat there. A force that has not moved
    /// for three moons is a landmark.</summary>
    public const int PerLunationFrozenChance = 8;

    public const int HostileBonus = 22;
    public const int UnfriendlyBonus = 10;
    public const int FriendlyPenalty = -10;
    public const int AlliedPenalty = -20;

    /// <summary>Each crew member or party member past the first takes this off.
    /// Capped, because a force the locals cannot touch is a force with no reason
    /// to ever come home.</summary>
    public const int DefenderPercentEach = 5;
    public const int MaxDefenderReduction = 20;

    /// <summary>Floor on the roll. Measured before shipping: without it a
    /// five-strong crew in friendly country rolled ZERO for three straight
    /// lunations, which is a force with no reason to ever come home. Three
    /// percent is small enough to leave and large enough to exist.</summary>
    public const int MinChancePercent = 3;

    /// <summary>Share of the sortie's earned spoils a raid carries off.</summary>
    public const int RaidTakePercent = 30;

    /// <summary>Hull or Health a raid costs, as a share of maximum.</summary>
    public const int RaidHurtPercent = 15;

    /// <summary>In unfriendly or worse ground, odds a hit on a CASTLE is soldiers
    /// rather than opportunists.</summary>
    public const int AssaultSharePercent = 35;

    /// <summary>Hull an assault costs, as a share of maximum. The rest of the
    /// assault is the fight the player is owed.</summary>
    public const int AssaultHurtPercent = 30;

    // ── Measured behaviour at the values above ───────────────────────────
    //
    //    20,000 trials per cell, BEFORE the floor. Chance that a frozen force
    //    is hit at least once over N lunations:
    //
    //      ground / defenders   N=1   N=2   N=3   N=4
    //      friendly  x1          2%   12%   27%   47%
    //      friendly  x5          0%    0%    0%    6%   <- the floor exists for this row
    //      neutral   x1         12%   29%   50%   68%
    //      neutral   x3          2%   11%   28%   47%
    //      hostile   x1         35%   62%   82%   92%
    //      hostile   x5         14%   33%   53%   71%
    //
    //    With MinChancePercent 3 the friendly x5 row becomes about 3/6/9/12,
    //    which is the intent: unlikely, never impossible.
    //
    //    The shape: one lunation away is cheap, three is a gamble, and hostile
    //    ground is where you do not leave things. Nothing here is terminal
    //    because nothing here can kill.
    //
    //    An earlier draft of this comment was written BEFORE running the
    //    simulation and happened to be close. It was still a guess with a
    //    table drawn around it, which is the exact thing measuring exists to
    //    replace.

    // ── The roll ─────────────────────────────────────────────────────────

    /// <summary>Roll every frozen sortie for this lunation. Returns one joined
    /// report line for the map, or null when nothing happened. Sendings go
    /// into the inbox as a side effect.</summary>
    public static string RollForLunation(CycleState cycle)
    {
        if (cycle == null)
        {
            return null;
        }

        string report = null;

        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            string line = ResolveFrozen(cycle, cycle.CastleSortie, isCastle: true, forceId: "",
                                        name: "The castle");
            if (!string.IsNullOrEmpty(line))
            {
                report = line;
            }
        }

        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p?.Sortie == null || !p.Sortie.IsLive())
                {
                    continue;
                }
                string line = ResolveFrozen(cycle, p.Sortie, isCastle: false, forceId: p.Id,
                                            name: p.Name);
                if (!string.IsNullOrEmpty(line))
                {
                    report = report == null ? line : report + " " + line;
                }
            }
        }
        return report;
    }

    private static string ResolveFrozen(CycleState cycle, SortieState slot, bool isCastle,
                                        string forceId, string name)
    {
        var notes = new List<string>();

        // ── Upkeep, always ───────────────────────────────────────────────
        int upkeep = isCastle ? CastleIdleFuel : PartyIdleRations;
        int before = slot.StepsRemaining;
        slot.StepsRemaining = Math.Max(0, slot.StepsRemaining - upkeep);
        if (before != slot.StepsRemaining)
        {
            notes.Add(isCastle
                ? $"The banked furnace burns {before - slot.StepsRemaining} fuel holding station."
                : $"Camped, they eat through {before - slot.StepsRemaining} rations.");
        }

        // ── The roll ─────────────────────────────────────────────────────
        string kingdomId = cycle.World != null && cycle.World.InBounds(slot.X, slot.Y)
            ? (cycle.World.GetTile(slot.X, slot.Y).KingdomId ?? "")
            : "";
        var stance = CouncilQueries.StanceFor(cycle, kingdomId);

        int defenders = isCastle
            ? ExpeditionAnchors.Crew(cycle).Count
            : PartyStrength(cycle, forceId);
        int reduction = Math.Min(MaxDefenderReduction,
                                 Math.Max(0, defenders - 1) * DefenderPercentEach);

        int chance = BaseChancePercent
                   + slot.LunationsFrozen * PerLunationFrozenChance
                   + stance switch
                     {
                         KingdomStance.Hostile => HostileBonus,
                         KingdomStance.Unfriendly => UnfriendlyBonus,
                         KingdomStance.Friendly => FriendlyPenalty,
                         KingdomStance.Allied => AlliedPenalty,
                         _ => 0,
                     }
                   - reduction;
        chance = Math.Clamp(chance, MinChancePercent, 90);

        slot.LunationsFrozen++;

        var rng = RngFor(cycle, slot, forceId);
        string headline = null;
        string body = null;

        if (rng.Next(100) < chance)
        {
            bool soldiers = isCastle
                            && stance <= KingdomStance.Unfriendly
                            && string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId)
                            && rng.Next(100) < AssaultSharePercent;
            if (soldiers)
            {
                int hurt = Hurt(slot, AssaultHurtPercent);
                cycle.PendingCastleAssaultKingdomId = kingdomId;
                headline = "The castle is under assault";
                body = $"{KingdomName(kingdomId)} moved on the fortress while you looked away. "
                     + $"The opening blow cost {hurt} Hull. Soldiers are in the camp: "
                     + "the defence is owed, and the castle cannot move until it is answered.";
                notes.Add($"{name} is ASSAULTED by {KingdomName(kingdomId)}: -{hurt} Hull, soldiers in the camp.");
            }
            else
            {
                string took = Rob(slot);
                int hurt = Hurt(slot, RaidHurtPercent);
                string what = isCastle ? "Hull" : "Health";
                headline = isCastle ? "The castle was raided" : $"{name} was raided";
                body = $"A patrol found them holding station and hit them for what they carried. "
                     + $"They lost {took} and {hurt} {what}. "
                     + (slot.LunationsFrozen > 1
                         ? "They have been sitting long enough to be noticed."
                         : "They were seen the first moon they stopped.");
                notes.Add($"{name} is RAIDED: -{took}, -{hurt} {what}.");
            }
        }

        // ── Report ───────────────────────────────────────────────────────
        if (headline != null)
        {
            PostSending(cycle, headline, body, forceId);
        }
        else if (notes.Count > 0)
        {
            // Upkeep alone is desk news, not a sending: nobody reaches through
            // the stone to say the furnace is banked.
            PostNote(cycle, $"{name}, holding station", string.Join(" ", notes), forceId);
        }

        if (notes.Count == 0)
        {
            return null;
        }
        string line = $"{name} (in the field): {string.Join(" ", notes)}";
        GD.Print($"[SortieThreats] {line}");
        return line;
    }

    // ── Consequences ─────────────────────────────────────────────────────

    /// <summary>Hurt a frozen force by a share of its maximum. NEVER below one:
    /// nothing dies off-screen.</summary>
    private static int Hurt(SortieState slot, int percent)
    {
        if (slot.MaxHP <= 0)
        {
            return 0;
        }
        int amount = Math.Max(1, slot.MaxHP * percent / 100);
        int floor = 1;
        int actual = Math.Min(amount, Math.Max(0, slot.CurrentHP - floor));
        slot.CurrentHP -= actual;
        return actual;
    }

    /// <summary>Take a share of what the sortie has EARNED. The banked treasury
    /// is untouched: a raid on a force in the field robs the force.</summary>
    private static string Rob(SortieState slot)
    {
        int g = Share(slot.GoldEarned);
        int s = Share(slot.SplinterEarned);
        int m = Share(slot.MaterialEarned);
        int u = Share(slot.SuppliesEarned);
        slot.GoldEarned -= g;
        slot.SplinterEarned -= s;
        slot.MaterialEarned -= m;
        slot.SuppliesEarned -= u;
        return g + s + m + u == 0 ? "nothing worth taking" : $"{g}g {s}sp {m}mat {u}sup";
    }

    private static int Share(int amount)
        => amount <= 0 ? 0 : Math.Max(1, amount * RaidTakePercent / 100);

    // ── Messages ─────────────────────────────────────────────────────────

    private static void PostSending(CycleState cycle, string title, string body, string forceId)
        => Post(cycle, ScryChannel.Sending, title, body, forceId);

    private static void PostNote(CycleState cycle, string title, string body, string forceId)
        => Post(cycle, ScryChannel.Note, title, body, forceId);

    /// <summary>Written straight into the inbox. The expedition delivers unread
    /// sendings the next time the stone is sat at, which is the moment the
    /// player can actually do something about them.</summary>
    private static void Post(CycleState cycle, ScryChannel channel, string title, string body,
                             string forceId)
        => ScryInbox.Post(cycle, channel, title, body, forceId, "sortie");

    // ── Helpers ──────────────────────────────────────────────────────────

    private static int PartyStrength(CycleState cycle, string partyId)
    {
        if (cycle.FieldParties == null || cycle.Companions == null)
        {
            return 0;
        }
        foreach (var p in cycle.FieldParties)
        {
            if (p == null || p.Id != partyId || p.MemberCompanionIds == null)
            {
                continue;
            }
            int n = 0;
            foreach (var id in p.MemberCompanionIds)
            {
                var c = cycle.Companions.Find(x => x != null && x.Id == id);
                if (c != null && c.IsRecruited && !c.IsPermadead && !c.IsInjured)
                {
                    n++;
                }
            }
            return n;
        }
        return 0;
    }

    private static string KingdomName(string kingdomId)
        => string.IsNullOrEmpty(kingdomId) ? "Soldiers" : kingdomId;

    private static Random RngFor(CycleState cycle, SortieState slot, string forceId)
    {
        unchecked
        {
            int seed = cycle.WorldSeed;
            seed = (seed * 397) ^ (cycle.Calendar?.CurrentLunation ?? 0);
            seed = (seed * 397) ^ slot.X;
            seed = (seed * 397) ^ slot.Y;
            seed = (seed * 397) ^ slot.LunationsFrozen;
            seed = (seed * 397) ^ (forceId?.GetHashCode() ?? 17);
            return new Random(seed);
        }
    }
}
