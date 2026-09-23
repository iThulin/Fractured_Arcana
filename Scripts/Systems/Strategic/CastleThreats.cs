using Godot;
using System;

// ============================================================
// CastleThreats.cs
//
// Purpose:        What comes for the fortress while it sits exposed.
//                 Ruled 2026-09-21 (Magos): all three sources, each with
//                 its own character and its own stakes.
//
//                   SCRIPTED        drives the narrative. Authored events
//                                   call ForceThreat when the story needs
//                                   the castle hit. Never rolled.
//                   KINGDOM         hostility turns into aggression. A
//                                   kingdom that hates the guild comes to
//                                   hurt it: the work is undone and the
//                                   crews are scattered, so the resupply
//                                   gets LONGER. This is the spiral the
//                                   player has to break.
//                   PATROL          a quick hit and run to deprive an
//                                   enemy of resources. It does not try to
//                                   kill the castle, it robs the hold and
//                                   leaves. Cheap, frequent, survivable.
//
//                 Why the stakes are expressed in repair lunations and
//                 hold contents rather than Hull: Hull lives on the
//                 ExpeditionManager for the duration of a sortie and is
//                 NOT persisted between them. A parked castle has no Hull
//                 number to damage. Repair lunations and the hold are the
//                 two things that survive in CycleState, they are already
//                 the currency parking is priced in, and setting the
//                 resupply back is exactly what "the crews were scattered"
//                 means. No new persisted stat is invented to say a thing
//                 the existing two already say.
//
//                 Rolls are DETERMINISTIC per lunation (seeded from the
//                 world seed, the lunation and the castle's tile), so
//                 reloading a save cannot reroll a raid away. Save
//                 scumming a threat table is the fastest way to make one
//                 meaningless.
// Layer:          System (strategic)
// Collaborators:  CycleState (CastleExposed, CastleRepairLunations,
//                 CastleHold, PendingCastleAssaultKingdomId),
//                 CouncilQueries.StanceFor (the hostility source),
//                 ExpeditionAnchors.Crew (who is aboard to fight),
//                 StrategicView.RunLunationTick (the caller)
// See:            docs/session_log_2026-09-21_expedition_v2_step1_data_layer.md
// ============================================================

/// <summary>What came for the castle.</summary>
public enum CastleThreatKind
{
    None = 0,
    PatrolRaid = 1,
    KingdomAssault = 2,
    Scripted = 3,
}

/// <summary>The outcome of one lunation's threat roll, for the caller to
/// report. Runtime only: the state changes are already written into the
/// cycle by the time this is returned.</summary>
public sealed class CastleThreatResult
{
    public CastleThreatKind Kind = CastleThreatKind.None;
    public string AttackerKingdomId = "";
    public string Report = "";

    /// <summary>True when a tactical castle defence is owed. The assault has
    /// already applied its strategic cost either way, so a player who never
    /// answers it is set back rather than let off.</summary>
    public bool RoutesToCombat = false;

    public bool Happened => Kind != CastleThreatKind.None;
}

/// <summary>Rolls and resolves attacks on a parked, exposed fortress.</summary>
public static class CastleThreats
{
    // ── Tuning (starting values, all of them) ────────────────────────────

    /// <summary>Chance per exposed lunation that anything comes at all.</summary>
    public const int BaseChancePercent = 25;

    /// <summary>Stance shifts the odds. A kingdom that hates you is watching
    /// the camp; one that likes you keeps its patrols off your back.</summary>
    public const int HostileBonus = 25;
    public const int UnfriendlyBonus = 10;
    public const int FriendlyPenalty = -15;
    public const int AlliedPenalty = -25;

    /// <summary>Share of the hold a successful raid carries off.</summary>
    public const int RaidTakePercent = 25;

    /// <summary>Each crew member aboard adds this much chance to drive a raid
    /// off before it reaches the hold. The crew is the castle's defence, which
    /// is what makes leaving the fortress thinly staffed cost something.</summary>
    public const int CrewDriveOffPercentEach = 25;

    /// <summary>Lunations a kingdom assault adds to the resupply.</summary>
    public const int AssaultRepairSetback = 2;

    /// <summary>Hard ceiling on the resupply, however many assaults land.
    ///
    /// <para>Without it the spiral runs away. Simulated over 40,000 trials: a
    /// 4-lunation bill in hostile ground (50 percent per lunation, 60 percent of
    /// hits being assaults at +2 each) settles at about TEN lunations of a
    /// twelve-lunation cycle. That is not a setback, it is a lost timeline, and
    /// a player cannot act their way out of it because being parked is the very
    /// thing drawing the attacks. The ceiling keeps a bad parking decision
    /// expensive without making it terminal.</para></summary>
    public const int MaxTotalRepairLunations = 6;

    /// <summary>Share of the hold an assault destroys outright. Lower than a
    /// raid's: they came to break the castle, not to shop.</summary>
    public const int AssaultSpoilPercent = 15;

    // ── The roll ─────────────────────────────────────────────────────────

    /// <summary>Roll one exposed lunation. Returns a result with Kind None when
    /// the castle is not exposed or nothing came. Non-throwing.</summary>
    public static CastleThreatResult RollForLunation(CycleState cycle)
    {
        var none = new CastleThreatResult();
        if (cycle == null || !cycle.CastleExposed)
        {
            return none;
        }

        string kingdomId = KingdomAtCastle(cycle);
        var stance = CouncilQueries.StanceFor(cycle, kingdomId);

        int chance = BaseChancePercent + stance switch
        {
            KingdomStance.Hostile => HostileBonus,
            KingdomStance.Unfriendly => UnfriendlyBonus,
            KingdomStance.Friendly => FriendlyPenalty,
            KingdomStance.Allied => AlliedPenalty,
            _ => 0,
        };
        chance = Math.Clamp(chance, 0, 95);

        var rng = RngFor(cycle);
        if (rng.Next(100) >= chance)
        {
            return none;
        }

        // Who shows up follows from how the locals feel. A hostile kingdom
        // sends soldiers; anywhere else it is opportunists after the hold.
        //
        // A second assault never lands while the first is still unanswered.
        // That is a design rule before it is a balance one: an assault owes the
        // player a FIGHT, and stacking another on top punishes them for a debt
        // they have not been given the chance to pay. It is also what keeps the
        // resupply from spiralling, since only raids can land in the meantime
        // and raids cost resources rather than time.
        bool assaultOwed = !string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId);
        bool assault = !assaultOwed
                       && stance <= KingdomStance.Unfriendly
                       && rng.Next(100) < 60;
        return assault
            ? ResolveKingdomAssault(cycle, kingdomId, rng)
            : ResolvePatrolRaid(cycle, kingdomId, rng);
    }

    /// <summary>The scripted door. Authored narrative events call this to bring
    /// something down on the castle regardless of the odds. Applies the same
    /// costs as a rolled threat of that kind, so story and simulation cannot
    /// drift apart in what an attack actually means.</summary>
    public static CastleThreatResult ForceThreat(CycleState cycle, CastleThreatKind kind,
                                                 string attackerKingdomId = "")
    {
        if (cycle == null || !cycle.CastleParked)
        {
            return new CastleThreatResult();
        }

        var rng = RngFor(cycle);
        string kid = string.IsNullOrEmpty(attackerKingdomId) ? KingdomAtCastle(cycle) : attackerKingdomId;

        var result = kind switch
        {
            CastleThreatKind.PatrolRaid => ResolvePatrolRaid(cycle, kid, rng),
            CastleThreatKind.KingdomAssault => ResolveKingdomAssault(cycle, kid, rng),
            _ => new CastleThreatResult(),
        };
        if (result.Happened)
        {
            result.Kind = kind == CastleThreatKind.Scripted ? CastleThreatKind.Scripted : result.Kind;
        }
        return result;
    }

    // ── Resolutions ──────────────────────────────────────────────────────

    /// <summary>Hit and run. They are not here to fight the castle, they are
    /// here to take what it is carrying. The crew may drive them off first.</summary>
    private static CastleThreatResult ResolvePatrolRaid(CycleState cycle, string kingdomId, Random rng)
    {
        var result = new CastleThreatResult
        {
            Kind = CastleThreatKind.PatrolRaid,
            AttackerKingdomId = kingdomId,
        };

        int crew = ExpeditionAnchors.Crew(cycle).Count;
        int driveOff = Math.Clamp(crew * CrewDriveOffPercentEach, 0, 90);
        if (rng.Next(100) < driveOff)
        {
            result.Report = "Raiders came for the camp in the night. The crew turned them back before " +
                            "they reached the hold.";
            return result;
        }

        var hold = cycle.CastleHold;
        if (hold == null || hold.IsEmpty)
        {
            result.Report = "Raiders swept the camp and found the hold already empty. " +
                            "They left with nothing but the measure of your defences.";
            return result;
        }

        int gold = Take(ref hold.Gold, RaidTakePercent);
        int spl = Take(ref hold.Splinters, RaidTakePercent);
        int mat = Take(ref hold.Materials, RaidTakePercent);
        int sup = Take(ref hold.Supplies, RaidTakePercent);

        result.Report = $"Raiders hit the camp and were gone before the crew formed up. " +
                        $"The hold is lighter by {gold}g {spl}sp {mat}mat {sup}sup.";
        return result;
    }

    /// <summary>A kingdom that hates the guild sends soldiers. They come to
    /// break the work: crews scatter, the waystone goes dark, and the resupply
    /// takes longer. The strategic cost lands immediately, so an unanswered
    /// assault still hurts.</summary>
    private static CastleThreatResult ResolveKingdomAssault(CycleState cycle, string kingdomId, Random rng)
    {
        var result = new CastleThreatResult
        {
            Kind = CastleThreatKind.KingdomAssault,
            AttackerKingdomId = kingdomId,
            RoutesToCombat = true,
        };

        cycle.CastleRepairLunations = Math.Min(
            cycle.CastleRepairLunations + AssaultRepairSetback, MaxTotalRepairLunations);
        cycle.PendingCastleAssaultKingdomId = kingdomId;

        var hold = cycle.CastleHold;
        int gold = 0, spl = 0, mat = 0, sup = 0;
        if (hold != null && !hold.IsEmpty)
        {
            gold = Take(ref hold.Gold, AssaultSpoilPercent);
            spl = Take(ref hold.Splinters, AssaultSpoilPercent);
            mat = Take(ref hold.Materials, AssaultSpoilPercent);
            sup = Take(ref hold.Supplies, AssaultSpoilPercent);
        }

        string spoils = (gold + spl + mat + sup) > 0
            ? $" Stores burned in the assault: {gold}g {spl}sp {mat}mat {sup}sup."
            : "";

        result.Report = $"Soldiers of {kingdomId} fell on the camp while the waystone was open. " +
                        $"The crews scattered and the work is undone: the resupply stands at " +
                        $"{cycle.CastleRepairLunations} lunation(s).{spoils}";
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Remove a percentage (at least 1 when there is anything at all)
    /// and return what was taken.</summary>
    private static int Take(ref int pool, int percent)
    {
        if (pool <= 0)
        {
            return 0;
        }
        int taken = pool * percent / 100;
        if (taken < 1)
        {
            taken = 1;
        }
        if (taken > pool)
        {
            taken = pool;
        }
        pool -= taken;
        return taken;
    }

    /// <summary>Deterministic per lunation and per parking spot, so reloading
    /// cannot reroll a raid away.</summary>
    private static Random RngFor(CycleState cycle)
    {
        unchecked
        {
            int seed = cycle.WorldSeed;
            seed = (seed * 397) ^ (cycle.Calendar?.CurrentLunation ?? 0);
            seed = (seed * 397) ^ cycle.CastleX;
            seed = (seed * 397) ^ cycle.CastleY;
            seed = (seed * 397) ^ cycle.CastleRepairLunations;
            return new Random(seed);
        }
    }

    private static string KingdomAtCastle(CycleState cycle)
    {
        if (cycle.World == null || !cycle.World.InBounds(cycle.CastleX, cycle.CastleY))
        {
            return "";
        }
        return cycle.World.GetTile(cycle.CastleX, cycle.CastleY).KingdomId ?? "";
    }
}
