using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.EnemyBreach.cs  (partial of CombatManager)
//
// Purpose:        Combat overhaul stage C9: enemies that cope with a
//                 wall across the board (stages C6 and C8).
//
//                 The enemy mover scores a destination by straight-line
//                 distance to its mark. With a long wall in the way that
//                 walks it into the wall and leaves it there, even when
//                 a gate stands open further along. Two answers:
//
//                   1. On a map with a long wall, a mover scores by
//                      WALKING distance, so it goes round by the gate.
//                   2. When no walking route exists at all (the gate is
//                      closed), it walks to the weakest breakable thing
//                      between it and its mark and hammers it.
//
//                 Maps with no long wall are untouched: the mover scores
//                 exactly as before.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.EnemyIntents.cs (MoveTowardTile calls
//                   in here),
//                 HexGridManager.Obstruction.cs (WalkField, ActiveObstruction),
//                 CombatManager.Reach.cs (the barricade count)
// See:            docs/combat_c9_enemy_interactions_v1.md
// ============================================================

public partial class CombatManager
{
    /// <summary>True on a map with a wall long enough to trap the straight-line
    /// mover: a barrier, or a reach fight's barricade.</summary>
    private bool LongWallOnMap =>
        grid != null && (grid.ActiveObstruction == "barrier" || _reachBarricadePieces > 0);

    /// <summary>Walking distance from every reachable tile to <paramref name="goal"/>,
    /// or null on a map with no long wall (the caller then scores as it always did).</summary>
    private Dictionary<Vector2I, int> WallAwareField(Vector2I goal)
        => LongWallOnMap ? grid.WalkField(goal) : null;

    private int ScoreAlongField(Unit enemy, Vector2I coord, Vector2I goal, Dictionary<Vector2I, int> field)
    {
        int steps = field.TryGetValue(coord, out int d) ? d : 999;
        return -100 * steps + StepPreferenceScore(enemy, coord, goal);
    }

    /// <summary>The breakable thing to hammer when <paramref name="enemy"/> has no
    /// walking route to its mark: a field object or an obstacle tile with health
    /// that touches both the ground the enemy can walk and the ground its mark can.
    /// Nearest first, weakest on a tie. Null when nothing qualifies.</summary>
    private Vector2I? FindBreachTarget(Unit enemy, Dictionary<Vector2I, int> goalField)
    {
        if (enemy?.CurrentTile == null || goalField == null)
            return null;
        var mine = grid.WalkField(enemy.CurrentTile.Axial);

        Vector2I? best = null;
        int bestScore = int.MaxValue;
        foreach (var td in grid.Tiles.Values)
        {
            if (td == null)
                continue;
            int hp;
            var occ = td.Occupant;
            if (occ != null && IsInstanceValid(occ) && occ.IsMapObject && occ.Stats.IsAlive && !occ.IsDeathQueued)
                hp = occ.Stats.Health;
            else if (td.IsBlocked && td.ObstacleHp > 0)
                hp = td.ObstacleHp;
            else
                continue;

            int nearMine = int.MaxValue;
            bool touchesGoal = false;
            foreach (var n in grid.GetNeighbors(td.Axial))
            {
                if (mine.TryGetValue(n, out int dm) && dm < nearMine)
                    nearMine = dm;
                if (goalField.ContainsKey(n))
                    touchesGoal = true;
            }
            if (nearMine == int.MaxValue || !touchesGoal)
                continue;

            int score = nearMine * 10 + hp;
            if (score < bestScore)
            {
                bestScore = score;
                best = td.Axial;
            }
        }
        return best;
    }

    /// <summary>A Gate Winch <paramref name="enemy"/> can walk to whose gate is
    /// closed. Nearest first. Null when there is none, and then it hammers.</summary>
    private Unit FindWinchToOpen(Unit enemy)
    {
        if (enemy?.CurrentTile == null)
            return null;
        var mine = grid.WalkField(enemy.CurrentTile.Axial);
        Unit best = null;
        int bestDist = int.MaxValue;
        foreach (var o in fieldObjects)
        {
            if (o == null || !IsInstanceValid(o) || o.IsDeathQueued || o.CurrentTile == null)
                continue;
            if (o.MapObjectKind != WinchKind || !o.Stats.IsAlive)
                continue;
            if (StandingGatePieces(GateTilesFor(o)).Count == 0)
                continue;   // its gate is already open
            int near = int.MaxValue;
            foreach (var n in grid.GetNeighbors(o.CurrentTile.Axial))
                if (mine.TryGetValue(n, out int d) && d < near)
                    near = d;
            if (near < bestDist)
            {
                bestDist = near;
                best = o;
            }
        }
        return best;
    }

    /// <summary>The enemy opens a closed gate at its winch, for the AP it kept back
    /// for its strike. Enemies only open: they do not close a gate.</summary>
    private void EnemyWorksWinch(Unit enemy, Unit winch)
    {
        if (!IsValidActor(enemy) || winch == null || !IsInstanceValid(winch) || winch.CurrentTile == null)
            return;
        if (grid.Distance(enemy.CurrentTile.Axial, winch.CurrentTile.Axial) != 1)
        {
            combatUI?.AppendActionLog($"{enemy.Name} cannot reach you and heads for the Gate Winch.");
            return;
        }
        var standing = StandingGatePieces(GateTilesFor(winch));
        if (standing.Count == 0)
            return;
        if (!enemy.TrySpendAP(ReservedAttackAP(enemy)))
            return;
        foreach (var g in standing)
            RemoveFieldObjectQuietly(g);
        combatUI?.AppendActionLog($"{enemy.Name} works the Gate Winch. The gate is open.");
        RefreshCoverMarkers();
        RefreshThreatTiles();
    }

    /// <summary>One blow at the thing in the way, paid for with the AP the mover
    /// kept back for its strike.</summary>
    private void StrikeBreach(Unit enemy, Vector2I at)
    {
        if (!IsValidActor(enemy) || grid.Distance(enemy.CurrentTile.Axial, at) != 1)
            return;
        var td = grid.GetTile(at);
        if (td == null)
            return;
        if (!enemy.TrySpendAP(ReservedAttackAP(enemy)))
            return;
        int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;

        var occ = td.Occupant;
        if (occ != null && IsInstanceValid(occ) && occ.IsMapObject && occ.Stats.IsAlive)
        {
            combatUI?.AppendActionLog($"{enemy.Name} cannot reach you and hammers the {occ.DisplayName}: {dmg}.");
            occ.ApplyDamage(dmg);
        }
        else if (td.IsBlocked && td.ObstacleHp > 0)
        {
            string name = ObstacleCatalog.GetOrFallback(td.ObstacleKind)?.Label ?? td.ObstacleKind;
            combatUI?.AppendActionLog($"{enemy.Name} cannot reach you and hammers the {name}: {dmg}.");
            grid.DamageObstacle(td, dmg, m => combatUI?.AppendActionLog(m));
            RefreshCoverMarkers();
        }
        RefreshThreatTiles();
    }
}
