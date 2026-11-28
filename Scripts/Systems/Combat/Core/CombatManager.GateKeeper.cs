using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// CombatManager.GateKeeper.cs  (partial of CombatManager)
//
// Purpose:        Combat overhaul stage C9: the enemy uses the Gate
//                 Winch. A winch stands on the DEFENDER'S side of a
//                 defensive work. In a reach fight the barricade is the
//                 enemy's, so its winch is inside the ring and one enemy
//                 mans it. The gate starts open. When a party unit comes
//                 near, the keeper closes it, and whatever stands in the
//                 gateway is hurt and thrown out.
//
//                 Same shape as a post (CombatManager.Posts.cs) and the
//                 Signal Fire's runner: a runtime-only behaviour key for
//                 one unit for one fight, a planner that returns a plain
//                 Guard beat, a hook at the head of ExecuteGuardIntent.
//                 Once the gate has been closed the keeper becomes an
//                 ordinary post-holder beside the winch. It closes the
//                 gate once.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.Reach.cs (the barricade, the gate tiles,
//                   the winch),
//                 CombatManager.Posts.cs (AssignEnemyPosts calls
//                   AssignGateKeeper; GivePost, CanHoldPost, PlanHoldPost),
//                 CombatManager.WorkObjects.cs (GateFallsOn, the gate kind),
//                 CombatManager.EnemyIntents.cs (planner table,
//                   ExecuteGuardIntent),
//                 UIContent.cs (the behaviour line)
// See:            docs/combat_c9_enemy_interactions_v1.md
// ============================================================

public partial class CombatManager
{
    private const string ManWinchKey = "man_winch";

    /// <summary>The keeper closes the gate when a party unit is within this many
    /// tiles of a gate tile at the keeper's activation. Starting value.</summary>
    private const int GateKeeperTrigger = 4;

    private Unit _gateKeeper;

    /// <summary>From AssignEnemyPosts, after the picket. Spawns one more of the
    /// fight's own enemies beside the winch, inside the barricade, and gives it the
    /// winch to man. When nobody suitable can be placed the gate is simply closed
    /// from the start, as it was before there was a keeper.</summary>
    private void AssignGateKeeper()
    {
        if (!ReachActive || _reachGateTiles.Count == 0)
            return;
        Unit keeper = null;
        if (_reachWinch != null && IsInstanceValid(_reachWinch) && _reachWinch.CurrentTile != null)
        {
            var roster = EncounterContextCarrier.Current?.Enemies;
            Vector2I winchAt = _reachWinch.CurrentTile.Axial;
            if (roster != null && roster.Count > 0)
            {
                foreach (var n in grid.GetNeighbors(winchAt))
                {
                    var td = grid.GetTile(n);
                    if (td == null || td.IsOccupied || !td.IsWalkable || td.IsBlocked)
                        continue;
                    bool besideGateway = false;
                    foreach (var g in _reachGateTiles)
                        if (grid.Distance(n, g) <= 1)
                            besideGateway = true;
                    if (besideGateway)
                        continue;   // not in the gateway, and not on the way into it
                    if (grid.Distance(n, _reachExitCentre) > grid.Distance(winchAt, _reachExitCentre))
                        continue;   // inside the ring, not out through the gate
                    // The first roster entry that can hold a post. Others are tried in turn.
                    for (int i = 0; i < roster.Count && keeper == null; i++)
                    {
                        var unit = SpawnRegistryUnit(roster[i].UnitId, td, teamId: 1,
                                                     difficultyMult: roster[i].DifficultyMult,
                                                     isMidFightSummon: false);
                        if (unit == null)
                            continue;
                        if (CanHoldPost(unit))
                            keeper = unit;
                        else
                            break;   // it stays as one more defender inside; stop adding bodies
                    }
                    break;
                }
            }
        }

        if (keeper == null)
        {
            CloseReachGate(null);
            GD.Print("[Gate] no keeper could be placed: the barricade gate starts closed.");
            return;
        }

        _gateKeeper = keeper;
        keeper.BehaviorKey = ManWinchKey;
        string who = string.IsNullOrEmpty(keeper.DisplayName) ? keeper.Name.ToString() : keeper.DisplayName;
        GD.Print($"[Gate] {keeper.Name} mans the Gate Winch at {_reachWinch.CurrentTile.Axial}.");
        combatUI?.AppendActionLog(
            $"── {who} mans the Gate Winch inside the barricade. The gate is open. On its turn it closes the gate " +
            $"if one of your units is within {GateKeeperTrigger} tiles of the gate. A unit standing in the gateway " +
            $"then takes {GateCrushDamage} damage and is thrown out. Kill it first, or break in. ──");
        RefreshEnemyRoster();
    }

    /// <summary>Closes the reach barricade's gate. Units in the gateway are hurt and
    /// thrown clear, away from the winch.</summary>
    private void CloseReachGate(Unit by)
    {
        Vector2I winchAt = _reachWinch != null && IsInstanceValid(_reachWinch) && _reachWinch.CurrentTile != null
            ? _reachWinch.CurrentTile.Axial
            : _reachExitCentre;
        int closed = 0;
        foreach (var t in _reachGateTiles)
        {
            var td = grid.GetTile(t);
            if (td == null || !td.IsWalkable || td.IsBlocked)
                continue;
            if (td.Occupant != null && td.Occupant.IsMapObject)
                continue;
            if (td.Occupant != null)
                GateFallsOn(td.Occupant, _reachGateTiles, winchAt);
            if (!td.IsOccupied && SpawnMapObject(GateKind, td) != null)
                closed++;
        }
        if (by != null)
            combatUI?.AppendActionLog(closed == _reachGateTiles.Count
                ? $"{by.Name} works the Gate Winch. The gate is closed."
                : $"{by.Name} works the Gate Winch. The gate is closed on {closed} of {_reachGateTiles.Count} tiles.");
        RefreshCoverMarkers();
        RefreshThreatTiles();
    }

    private bool PartyNearReachGate()
    {
        foreach (var p in playerUnits)
        {
            if (p == null || !IsInstanceValid(p) || p.Stats == null || !p.Stats.IsAlive || p.CurrentTile == null)
                continue;
            foreach (var t in _reachGateTiles)
                if (grid.Distance(p.CurrentTile.Axial, t) <= GateKeeperTrigger)
                    return true;
        }
        return false;
    }

    /// <summary>The keeper stops manning the winch and holds a post where it stands.</summary>
    private void KeeperTakesPost(Unit enemy)
    {
        if (enemy == _gateKeeper)
            _gateKeeper = null;
        if (enemy?.CurrentTile != null)
            GivePost(enemy, enemy.CurrentTile.Axial);
        else if (enemy != null)
            enemy.BehaviorKey = "melee_advance";
    }

    /// <summary>The man_winch planner: a plain Guard beat. The work is done in
    /// TryExecuteManWinch.</summary>
    private EnemyIntent PlanManWinch(Unit enemy)
    {
        return new EnemyIntent
        {
            Kind = IntentKind.Guard,
            TargetUnit = enemy,
            Value = 0,
            BaseValue = 0
        };
    }

    /// <summary>From the head of ExecuteGuardIntent. True when the unit is the gate
    /// keeper and its beat was handled here.</summary>
    private Task<bool> TryExecuteManWinch(Unit enemy)
    {
        if (enemy == null || enemy.BehaviorKey != ManWinchKey)
            return Task.FromResult(false);

        bool winchStands = _reachWinch != null && IsInstanceValid(_reachWinch)
            && !_reachWinch.IsDeathQueued && _reachWinch.Stats.IsAlive && _reachWinch.CurrentTile != null;
        bool beside = winchStands && enemy.CurrentTile != null
            && grid.Distance(enemy.CurrentTile.Axial, _reachWinch.CurrentTile.Axial) == 1;
        if (!winchStands || !beside)
        {
            // The winch is broken, or the keeper was pushed off it. It fights on from where it is.
            KeeperTakesPost(enemy);
            return Task.FromResult(true);
        }
        if (!PartyNearReachGate())
            return Task.FromResult(true);   // nobody near yet: the gate stays open

        CloseReachGate(enemy);
        KeeperTakesPost(enemy);
        return Task.FromResult(true);
    }
}
