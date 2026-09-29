using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// PostingThreats.cs: what the world does to what you hold (2026-09-24)
//
// Purpose:        Field Party v1, spec section 6. Two rolls at the new moon.
//                 (1) A posted force can be hit: one member injured and the
//                 moon's work lost; a line held in hostile country can be
//                 driven off. (2) An UNGARRISONED secured outpost in Hostile
//                 ground can be overrun, which is the reason garrisons exist.
//
//                 Same shape as SortieThreats on purpose: base plus stance,
//                 minus bodies, floored, deterministic per moon and tile.
//                 Nothing here is terminal. A detachment is never wiped and
//                 never loses more than one member's availability a moon.
//
// Layer:          Systems / Strategic
// Collaborators:  FieldParty, FieldPostings, CouncilQueries.StanceFor,
//                 WorldData.StagingPoints, ScryInbox
// ============================================================

public static class PostingThreats
{
    public const int BaseChancePercent = 8;
    public const int HostileBonus = 22;
    public const int UnfriendlyBonus = 10;
    public const int FriendlyPenalty = -8;
    public const int AlliedPenalty = -20;
    public const int StalledBonus = 10;
    public const int DefenderPercentEach = 5;
    public const int MaxDefenderReduction = 20;
    public const int MinChancePercent = 3;
    public const int MaxChancePercent = 90;

    /// <summary>A line held in Hostile country: this share of hits drive the
    /// detachment off the front instead of wounding one of them.</summary>
    public const int DrivenOffSharePercent = 30;
    public const int InjuryLunations = 2;

    /// <summary>Overrun: per moon, per ungarrisoned Secured point in a
    /// Hostile kingdom. Small on purpose; it exists so "hold ground" has a
    /// consequence, not to simulate a war.</summary>
    public const int OverrunChancePercent = 10;

    public sealed class Result
    {
        public bool Happened;
        public bool LosesEffect;
        public string Report = "";
    }

    /// <summary>Roll for one working force. Called from FieldPostings.TickLunation.</summary>
    public static Result RollForForce(CycleState cycle, FieldParty force)
    {
        var result = new Result();
        var world = cycle?.World;
        if (world == null || force == null || force.State != FieldPartyState.Working
            || !world.InBounds(force.X, force.Y))
        {
            return result;
        }

        string kid = world.GetTile(force.X, force.Y).KingdomId;
        var stance = string.IsNullOrEmpty(kid) ? KingdomStance.Neutral : CouncilQueries.StanceFor(cycle, kid);
        int able = FieldPostings.AbleCount(cycle, force);
        int reduction = Math.Min(MaxDefenderReduction, Math.Max(0, able - 1) * DefenderPercentEach);

        int chance = BaseChancePercent
                   + stance switch
                     {
                         KingdomStance.Hostile => HostileBonus,
                         KingdomStance.Unfriendly => UnfriendlyBonus,
                         KingdomStance.Friendly => FriendlyPenalty,
                         KingdomStance.Allied => AlliedPenalty,
                         _ => 0,
                     }
                   + (force.WorkStalled ? StalledBonus : 0)
                   - reduction
                   // A Warden keeps watch (Vocations, 2026-09-25).
                   - (Vocations.BestRank(cycle, force, Vocations.Warden) >= 1 ? Vocations.WardenThreatReduction : 0);
        chance = Math.Clamp(chance, MinChancePercent, MaxChancePercent);

        var rng = RngFor(cycle, force.X, force.Y, force.WorkKind, force.Id);
        if (rng.Next(100) >= chance)
        {
            return result;
        }

        result.Happened = true;
        string place = $"({force.X},{force.Y})";

        // Hostile ground sends soldiers, and soldiers are a fight (2026-09-28).
        // The moon's work is lost now; the wound is OWED, offered on the map
        // as stand or yield. One owed at a time, as with the castle: a second
        // assault on top of an unanswered one punishes a debt not yet paid.
        if (stance == KingdomStance.Hostile && FieldPostings.AbleCount(cycle, force) > 0
            && string.IsNullOrEmpty(cycle.PendingPostingAssaultForceId))
        {
            cycle.PendingPostingAssaultForceId = force.Id;
            cycle.PendingPostingAssaultKingdomId = kid ?? "";
            cycle.PostingDefenseLaunched = false;
            result.LosesEffect = true;
            result.Report = $"Soldiers of {CouncilTick.CourtDisplayName(cycle, kid)} come for {force.Name} at {place}. "
                          + "They will stand or yield when you next look at the map.";
            ScryInbox.Post(cycle, ScryChannel.Sending, $"{force.Name}: soldiers", result.Report, force.Id, "post");
            return result;
        }

        if (force.WorkKind == FieldPostings.HoldLine && stance == KingdomStance.Hostile
            && rng.Next(100) < DrivenOffSharePercent)
        {
            FieldPostings.Stop(cycle, force);
            result.LosesEffect = true;
            result.Report = $"{force.Name} are driven off the line at {place}. They hold their ground but the front is lost to them; "
                          + "they await orders.";
            ScryInbox.Post(cycle, ScryChannel.Sending, $"{force.Name}: driven off", result.Report, force.Id, "post");
            return result;
        }

        // A Physician shortens the wound; a Master prevents it. The moon's
        // work is still lost: the skirmish happened (Vocations, 2026-09-25).
        int physician = Vocations.BestRank(cycle, force, Vocations.Physician);
        var hurt = physician >= 3 ? null : FirstAble(cycle, force);
        if (physician >= 3)
        {
            result.LosesEffect = true;
            result.Report = $"{force.Name} are harried at {place} and lose the moon's work; their physician sees nobody wounded.";
        }
        else if (hurt != null)
        {
            int moons = physician >= 2 ? Vocations.PhysicianInjuryMoons : InjuryLunations;
            hurt.InjuredLunationsRemaining = Math.Max(hurt.InjuredLunationsRemaining, moons);
            result.LosesEffect = true;
            result.Report = $"{hurt.Name} is hurt in a skirmish at {place}, out for {moons} moon(s). "
                          + $"{force.Name} lose the moon's work.";
        }
        else
        {
            // Nobody able to be hurt: the posting simply loses the moon.
            result.LosesEffect = true;
            result.Report = $"{force.Name} are harried at {place} and lose the moon's work.";
        }
        ScryInbox.Post(cycle, ScryChannel.Sending, $"{force.Name}: attacked", result.Report, force.Id, "post");
        return result;
    }

    /// <summary>The owed fight declined (2026-09-28): what the roll would have
    /// done had it not been soldiers. A held line is driven off; any other
    /// posting keeps its place and loses one of its people for a while.</summary>
    public static string Yield(CycleState cycle, FieldParty force)
    {
        if (cycle == null || force == null)
        {
            return null;
        }
        string place = $"({force.X},{force.Y})";
        if (force.State == FieldPartyState.Working && force.WorkKind == FieldPostings.HoldLine)
        {
            FieldPostings.Stop(cycle, force);
            return $"{force.Name} give up the line at {place} rather than fight for it. They await orders.";
        }
        int physician = Vocations.BestRank(cycle, force, Vocations.Physician);
        var hurt = FirstAble(cycle, force);
        if (hurt == null || physician >= 3)
        {
            return $"{force.Name} give ground at {place} and nobody is hurt.";
        }
        int moons = physician >= 2 ? Vocations.PhysicianInjuryMoons : InjuryLunations;
        hurt.InjuredLunationsRemaining = Math.Max(hurt.InjuredLunationsRemaining, moons);
        return $"{force.Name} give ground at {place}; {hurt.Name} is hurt covering them, out for {moons} moon(s).";
    }

    /// <summary>The owed fight lost: two of them hurt and the posting broken.
    /// Never a wipe; nothing dies off-screen, and this was not off-screen.</summary>
    public static string Beaten(CycleState cycle, FieldParty force)
    {
        if (cycle == null || force == null)
        {
            return null;
        }
        var names = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var hurt = FirstAble(cycle, force);
            if (hurt == null)
            {
                break;
            }
            hurt.InjuredLunationsRemaining = Math.Max(hurt.InjuredLunationsRemaining, InjuryLunations);
            names.Add(hurt.Name);
        }
        if (force.State == FieldPartyState.Working)
        {
            FieldPostings.Stop(cycle, force);
        }
        return names.Count == 0
            ? $"{force.Name} are driven from their post."
            : $"{force.Name} are driven from their post; {string.Join(" and ", names)} are hurt, out for {InjuryLunations} moon(s).";
    }

    /// <summary>Every Secured staging point standing in a Hostile kingdom with
    /// no garrison on it rolls to be overrun. Overrun means Available = false,
    /// which the deploy drawer, the supply leash and the anchor list already
    /// honour; walking a party onto it re-secures it (Resecure).</summary>
    public static string RollOverruns(CycleState cycle)
    {
        var world = cycle?.World;
        if (world?.StagingPoints == null)
        {
            return null;
        }
        var lines = new List<string>();
        foreach (var sp in world.StagingPoints)
        {
            if (sp == null || sp.Source != "Secured" || !sp.Available || !world.InBounds(sp.X, sp.Y))
            {
                continue;
            }
            string kid = world.GetTile(sp.X, sp.Y).KingdomId;
            if (string.IsNullOrEmpty(kid) || CouncilQueries.StanceFor(cycle, kid) != KingdomStance.Hostile)
            {
                continue;
            }
            if (IsGarrisoned(cycle, sp.X, sp.Y))
            {
                continue;
            }
            var rng = RngFor(cycle, sp.X, sp.Y, "overrun", "");
            if (rng.Next(100) >= OverrunChancePercent)
            {
                continue;
            }
            sp.Available = false;
            string name = string.IsNullOrEmpty(sp.Name) ? "an outpost" : sp.Name;
            string line = $"{name} at ({sp.X},{sp.Y}) is overrun by {CouncilTick.CourtDisplayName(cycle, kid)}. "
                        + "It no longer stages or supplies you. Walk a party onto it to take it back.";
            ScryInbox.Post(cycle, ScryChannel.Sending, "Ground lost", line, "", "hold");
            lines.Add(line);
            GD.Print($"[PostingThreats] {line}");
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>A party standing on an overrun Secured point takes it back.
    /// Called on arrival and on teleport (FieldMarch.BankHere).</summary>
    public static string Resecure(CycleState cycle, int x, int y)
    {
        var world = cycle?.World;
        if (world?.StagingPoints == null)
        {
            return null;
        }
        foreach (var sp in world.StagingPoints)
        {
            if (sp != null && sp.Source == "Secured" && !sp.Available && sp.X == x && sp.Y == y)
            {
                sp.Available = true;
                string name = string.IsNullOrEmpty(sp.Name) ? "the outpost" : sp.Name;
                string line = $"{name} at ({x},{y}) is yours again.";
                ScryInbox.Post(cycle, ScryChannel.Note, "Ground retaken", line, "", "hold");
                return line;
            }
        }
        return null;
    }

    public static bool IsGarrisoned(CycleState cycle, int x, int y)
    {
        if (cycle?.FieldParties == null)
        {
            return false;
        }
        foreach (var p in cycle.FieldParties)
        {
            if (p != null && p.State == FieldPartyState.Working && p.WorkKind == FieldPostings.Garrison
                && p.X == x && p.Y == y)
            {
                return true;
            }
        }
        return false;
    }

    private static Companion FirstAble(CycleState cycle, FieldParty force)
    {
        if (cycle?.Companions == null || force?.MemberCompanionIds == null)
        {
            return null;
        }
        foreach (var id in force.MemberCompanionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c != null && c.IsRecruited && !c.IsPermadead && !c.IsInjured)
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>Deterministic per world seed, moon, tile, kind and force: a
    /// threat table you can reload away is not a threat table.</summary>
    private static Random RngFor(CycleState cycle, int x, int y, string kind, string forceId)
    {
        unchecked
        {
            int seed = cycle.WorldSeed;
            seed = (seed * 397) ^ (cycle.Calendar?.CurrentLunation ?? 0);
            seed = (seed * 397) ^ x;
            seed = (seed * 397) ^ y;
            seed = (seed * 397) ^ (kind?.GetHashCode() ?? 5);
            seed = (seed * 397) ^ (forceId?.GetHashCode() ?? 17);
            return new Random(seed);
        }
    }
}
