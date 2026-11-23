using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.Reach.cs
//
// Purpose:        Combat overhaul stage C2: the "reach" objective.
//                 The fight is won when the party's leader and half
//                 the party stand on the marked ground at a round
//                 boundary, whatever is still alive behind them.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.Objectives.cs (the zone, the round
//                   boundary, the banner, the victory latch),
//                 CombatManager.Outcome.cs (WithdrawLeader),
//                 CombatManager.cs (ConfigureAndGenerateMap sets
//                   the board up),
//                 HexGridManager.Reach.cs (board size, enemy seat,
//                   the path guarantee),
//                 CombatObjectiveDef.cs (KindReach)
// See:            docs/combat_c2_reach_and_field_board_v1.md
// ============================================================

public partial class CombatManager
{
    /// <summary>Size steps a reach fight adds to its board (HexGridManager.BoardGrow).
    /// Starting value: the radius 5 hexagon becomes radius 7.</summary>
    private const int ReachBoardGrow = 2;

    /// <summary>Where the marked ground sits when the objective names nowhere: the
    /// enemy's side of the board, with the enemy moved to the middle.</summary>
    private const string ReachDefaultAnchor = "enemy_anchor";

    private bool ReachActive =>
        _objective != null && _objective.Kind == CombatObjectiveDef.KindReach;

    /// <summary>Fills a reach objective's defaults and sets the grid up for it. Called
    /// from ConfigureAndGenerateMap before GenerateMap, for every fight, so a fight
    /// that is not a reach fight has the three knobs put back to off.</summary>
    private void ConfigureGridForReach(CombatObjectiveDef obj)
    {
        if (grid == null)
            return;

        bool reach = obj != null && obj.Kind == CombatObjectiveDef.KindReach;
        if (!reach)
        {
            grid.BoardGrow = 0;
            grid.EnemySpawnOverride = "";
            grid.MustReachToken = "";
            return;
        }

        if (string.IsNullOrEmpty(obj.ZoneAnchor) || obj.ZoneAnchor == "player_spawn")
            obj.ZoneAnchor = ReachDefaultAnchor;
        if (obj.ZoneRadius < 1)
            obj.ZoneRadius = 2;

        grid.BoardGrow = ReachBoardGrow;
        grid.EnemySpawnOverride = "midpoint";   // they hold the middle; the way out is past them
        grid.MustReachToken = obj.ZoneAnchor;
        grid.ForcedDeploymentId = "line";       // every recipe with deployments has it
        GD.Print($"[Objective] reach: marked ground at '{obj.ZoneAnchor}' radius {obj.ZoneRadius}, " +
                 $"board grown by {ReachBoardGrow}, enemy seeded at the midpoint.");
    }

    /// <summary>The party as the objective counts it: living companions and the
    /// wizard. Summons, spirits, constructs, wildlife, the ward and doors do not count.</summary>
    private void CountReachParty(out int living, out int inZone, out bool leaderIn, out string leaderName)
    {
        living = 0;
        inZone = 0;
        leaderIn = false;
        leaderName = "your leader";

        var leader = WithdrawLeader();
        if (leader != null)
            leaderName = string.IsNullOrEmpty(leader.DisplayName) ? leader.Name.ToString() : leader.DisplayName;

        if (_objectiveZone == null)
            return;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || u.Stats == null || !u.Stats.IsAlive)
                continue;
            if (u.IsStructure || u.IsObjectiveWard || string.IsNullOrEmpty(u.CompanionId))
                continue;
            living++;
            if (u.CurrentTile == null || !_objectiveZone.Contains(u.CurrentTile.Axial))
                continue;
            inZone++;
            if (u == leader)
                leaderIn = true;
        }
    }

    private static int ReachNeeded(int living) => Mathf.Max(1, (living + 1) / 2);

    /// <summary>From EvaluateObjectiveRoundBoundary, after the rounds check and before
    /// waves. A latch like every other objective outcome: CheckCombatEnd declares it.</summary>
    private void EvaluateReachBoundary()
    {
        if (!ReachActive || _objectiveVictory || _objectiveDefeat)
            return;
        EnsureObjectiveZone();
        CountReachParty(out int living, out int inZone, out bool leaderIn, out string leaderName);
        if (living == 0)
            return;
        int needed = ReachNeeded(living);
        if (inZone >= needed && leaderIn)
        {
            _objectiveVictory = true;
            GD.Print($"[Objective] reach met: {inZone}/{living} on the marked ground with the leader.");
            combatUI?.AppendActionLog($"── {leaderName} and the party are through. The objective is met. ──");
        }
        else
        {
            GD.Print($"[Objective] reach: {inZone}/{needed} needed on the marked ground, leader there: {leaderIn}.");
        }
    }

    /// <summary>The reach objective's banner line.</summary>
    private string ReachBannerLabel()
    {
        if (grid == null)
            return "Get your leader and half your party to the marked ground.";
        EnsureObjectiveZone();
        CountReachParty(out int living, out int inZone, out bool leaderIn, out string leaderName);
        int needed = ReachNeeded(living);
        return $"Get {leaderName} and half your party to the marked ground: " +
               $"{inZone} / {needed} there, {leaderName} {(leaderIn ? "is there" : "is not there yet")}. " +
               "Checked at the end of each round.";
    }
}
