using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.WorkObjects.cs  (partial of CombatManager)
//
// Purpose:        Combat overhaul stage C7: objects a unit WORKS with
//                 the Interact action, as it already works a lever.
//                 A lever is tied to a scheduled map event. A work
//                 object carries its own effect, so it can be placed
//                 on any map with no event behind it.
//
//                 Two kinds in this stage:
//                   gate_winch    opens or closes the gate within 4 tiles of it
//                   supply_crate  the unit that opens it regains health
//                   signal_fire   put out before an enemy lights it (C9)
//
//                 To add a kind: a spec in MapObjectCatalog, a line in
//                 IsWorkObject and WorkText, a case in TryWorkObject.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.Actions.cs (the Interact button and the
//                   click routing call in here),
//                 CombatManager.MapObjects.cs (SpawnMapObject, fieldObjects),
//                 CombatManager.Reach.cs (the barricade gives the winch
//                   its gate),
//                 MapObjectCatalog.cs (the specs)
// See:            docs/combat_c7_work_objects_v1.md
// ============================================================

public partial class CombatManager
{
    private const string WinchKind = "gate_winch";
    private const string GateKind = "barricade_gate";
    private const string SupplyKind = "supply_crate";

    /// <summary>Health a unit regains from one supply crate. Starting value.</summary>
    private const int SupplyHeal = 5;

    /// <summary>Damage to a unit standing in the gateway when the gate closes. Starting value.</summary>
    private const int GateCrushDamage = 6;

    /// <summary>A winch works the gate within this many tiles of itself.</summary>
    private const int WinchReach = 4;

    /// <summary>Each winch's gate tiles, found the first time it is asked about.</summary>
    private readonly Dictionary<Unit, List<Vector2I>> _winchGateTiles = new();

    /// <summary>The tiles <paramref name="winch"/> closes and opens: where a gate
    /// piece stands near it, and the map's own gate tiles near it (a barrier's gate
    /// starts open, so it has tiles and no pieces).</summary>
    private List<Vector2I> GateTilesFor(Unit winch)
    {
        if (_winchGateTiles.TryGetValue(winch, out var known))
            return known;
        var tiles = new List<Vector2I>();
        if (winch?.CurrentTile == null || grid == null)
            return tiles;
        Vector2I at = winch.CurrentTile.Axial;
        foreach (var o in fieldObjects)
        {
            if (o == null || !IsInstanceValid(o) || o.IsDeathQueued || o.CurrentTile == null)
                continue;
            if (o.MapObjectKind != GateKind || grid.Distance(o.CurrentTile.Axial, at) > WinchReach)
                continue;
            tiles.Add(o.CurrentTile.Axial);
        }
        foreach (var g in grid.ObstructionGateTiles)
            if (grid.Distance(g, at) <= WinchReach && !tiles.Contains(g))
                tiles.Add(g);
        foreach (var g in _reachGateTiles)   // the reach barricade's gateway, which starts open
            if (grid.Distance(g, at) <= WinchReach && !tiles.Contains(g))
                tiles.Add(g);
        if (tiles.Count > 0)
            _winchGateTiles[winch] = tiles;
        return tiles;
    }

    private List<Unit> StandingGatePieces(List<Vector2I> tiles)
    {
        var pieces = new List<Unit>();
        foreach (var t in tiles)
        {
            var occ = grid.GetTile(t)?.Occupant;
            if (occ != null && IsInstanceValid(occ) && occ.IsMapObject && occ.MapObjectKind == GateKind
                && occ.Stats.IsAlive && !occ.IsDeathQueued)
                pieces.Add(occ);
        }
        return pieces;
    }

    /// <summary>True for a standing map object that answers to Interact with an
    /// effect of its own. Levers are not in here: they keep their own path.</summary>
    private static bool IsWorkObject(Unit obj)
    {
        if (obj == null || !GodotObject.IsInstanceValid(obj) || !obj.IsMapObject)
            return false;
        if (obj.Stats == null || !obj.Stats.IsAlive || obj.IsDeathQueued)
            return false;
        return obj.MapObjectKind == WinchKind || obj.MapObjectKind == SupplyKind
            || obj.MapObjectKind == SignalKind;   // C9: lit or not; a lit one says so when worked
    }

    /// <summary>What working <paramref name="obj"/> would do for
    /// <paramref name="worker"/>, for the action bar's tooltip.</summary>
    private string WorkText(Unit obj, Unit worker)
    {
        switch (obj?.MapObjectKind)
        {
            case WinchKind:
            {
                var tiles = GateTilesFor(obj);
                if (tiles.Count == 0)
                    return "the Gate Winch is not tied to a gate";
                return StandingGatePieces(tiles).Count > 0
                    ? "work the Gate Winch: opens the gate"
                    : $"work the Gate Winch: closes the gate. Any unit standing in the gateway, yours or theirs, takes {GateCrushDamage} damage and is thrown clear";
            }
            case SupplyKind:
            {
                int heal = worker == null ? SupplyHeal : Math.Min(SupplyHeal, worker.Stats.MaxHealth - worker.Stats.Health);
                return heal > 0
                    ? $"open the Field Supplies: regain {heal} health"
                    : "open the Field Supplies: no use at full health";
            }
            case SignalKind:
                return _signalLit
                    ? "the Signal Fire is already lit: putting it out now changes nothing"
                    : "put out the Signal Fire: the enemy can no longer call reinforcements with it";
            default:
                return "nothing to work";
        }
    }

    /// <summary>Interact on a work object. Same gates and the same price as a lever:
    /// stand beside it, be able to act, pay one melee action's AP. Nothing is spent
    /// when the object has nothing to do.</summary>
    private void TryWorkObject(Unit unit, Unit obj)
    {
        if (unit?.CurrentTile == null || obj?.CurrentTile == null || !IsWorkObject(obj))
            return;
        string who = string.IsNullOrEmpty(unit.DisplayName) ? unit.Name.ToString() : unit.DisplayName;
        if (grid.Distance(unit.CurrentTile.Axial, obj.CurrentTile.Axial) != 1)
        {
            combatUI?.AppendActionLog($"{who} must stand beside the {obj.DisplayName}.");
            return;
        }
        if (!unit.CanAct())
        {
            combatUI?.AppendActionLog($"{who} is frozen!");
            return;
        }
        int cost = MartialAPCosts.AttackMelee;

        switch (obj.MapObjectKind)
        {
            case WinchKind:
            {
                var tiles = GateTilesFor(obj);
                if (tiles.Count == 0)
                {
                    combatUI?.AppendActionLog("The Gate Winch is not tied to a gate.");
                    return;
                }
                var standing = StandingGatePieces(tiles);
                if (standing.Count > 0)
                {
                    if (!unit.TrySpendAP(cost))
                    {
                        combatUI?.AppendActionLog($"{who} needs {cost} AP to work the Gate Winch.");
                        return;
                    }
                    foreach (var g in standing)
                        RemoveFieldObjectQuietly(g);
                    combatUI?.AppendActionLog($"{who} works the Gate Winch. The gate is open.");
                    break;
                }

                // Closing. The gate comes down on whatever stands in it: a unit is
                // hurt and thrown clear, and the gate then closes on its tile. Only a
                // field object in the gateway (a shoved boulder) keeps a tile open.
                var gateTiles = new List<TileData>();
                foreach (var t in tiles)
                {
                    var td = grid.GetTile(t);
                    if (td == null || !td.IsWalkable || td.IsBlocked)
                        continue;
                    if (td.Occupant != null && td.Occupant.IsMapObject)
                        continue;
                    gateTiles.Add(td);
                }
                if (gateTiles.Count == 0)
                {
                    combatUI?.AppendActionLog("The gate cannot close: the gateway is blocked.");
                    return;
                }
                if (!unit.TrySpendAP(cost))
                {
                    combatUI?.AppendActionLog($"{who} needs {cost} AP to work the Gate Winch.");
                    return;
                }
                combatUI?.AppendActionLog($"{who} works the Gate Winch. The gate comes down.");
                Vector2I winchAt = obj.CurrentTile.Axial;
                int closed = 0;
                foreach (var td in gateTiles)
                {
                    if (td.Occupant != null)
                        GateFallsOn(td.Occupant, tiles, winchAt);
                    if (!td.IsOccupied && SpawnMapObject(GateKind, td) != null)
                        closed++;
                }
                combatUI?.AppendActionLog(closed == tiles.Count
                    ? "The gate is closed."
                    : $"The gate is closed on {closed} of {tiles.Count} tiles. The rest are still blocked.");
                CheckCombatEnd();   // the gate may have killed the last enemy
                break;
            }
            case SupplyKind:
            {
                int heal = Math.Min(SupplyHeal, unit.Stats.MaxHealth - unit.Stats.Health);
                if (heal <= 0)
                {
                    combatUI?.AppendActionLog($"{who} is at full health. The Field Supplies are left where they are.");
                    return;
                }
                if (!unit.TrySpendAP(cost))
                {
                    combatUI?.AppendActionLog($"{who} needs {cost} AP to open the Field Supplies.");
                    return;
                }
                unit.Stats.Health += heal;
                unit.RefreshHealthBar();
                RemoveFieldObjectQuietly(obj);
                combatUI?.AppendActionLog($"{who} opens the Field Supplies and regains {heal} health. The supplies are used up.");
                break;
            }
            case SignalKind:
            {
                if (_signalLit)
                {
                    combatUI?.AppendActionLog("The Signal Fire is already lit. Putting it out now changes nothing.");
                    return;
                }
                if (!unit.TrySpendAP(cost))
                {
                    combatUI?.AppendActionLog($"{who} needs {cost} AP to put out the Signal Fire.");
                    return;
                }
                RemoveFieldObjectQuietly(obj);
                ReleaseSignalRunner();
                combatUI?.AppendActionLog($"{who} puts out the Signal Fire. No reinforcements can be called with it.");
                break;
            }
            default:
                return;
        }

        unit.Stats.HasActed = true;
        RefreshCoverMarkers();
        RefreshThreatTiles();
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        ClearMoveTiles();
        if (selectedUnit != null)
            ShowMoveTilesWithCost(selectedUnit);
    }

    /// <summary>The closing gate lands on <paramref name="victim"/>, friend or foe:
    /// it takes <see cref="GateCrushDamage"/> and, if it lives, is thrown to the free
    /// tile beside it that is farthest from the winch, so a unit caught in the
    /// gateway ends up on the far side from whoever closed it. With nowhere to be
    /// thrown it stays, and the gate stays open on that tile.</summary>
    private void GateFallsOn(Unit victim, List<Vector2I> gateTiles, Vector2I winchAt)
    {
        if (victim == null || !IsInstanceValid(victim) || victim.CurrentTile == null || !victim.Stats.IsAlive)
            return;
        string name = string.IsNullOrEmpty(victim.DisplayName) ? victim.Name.ToString() : victim.DisplayName;
        Vector2I from = victim.CurrentTile.Axial;

        combatUI?.AppendActionLog($"The gate comes down on {name}: {GateCrushDamage} damage.");
        victim.ApplyDamage(GateCrushDamage);
        if (!IsInstanceValid(victim) || !victim.Stats.IsAlive || victim.IsDeathQueued || victim.CurrentTile == null)
            return;   // it died there, so its tile is free for the gate

        TileData landing = null;
        int farthest = -1;
        foreach (var n in grid.GetNeighbors(from))
        {
            if (gateTiles.Contains(n))
                continue;
            var td = grid.GetTile(n);
            if (td == null || !td.CanEnter(victim))
                continue;
            int d = grid.Distance(n, winchAt);
            if (d > farthest)
            {
                farthest = d;
                landing = td;
            }
        }
        if (landing == null)
        {
            combatUI?.AppendActionLog($"{name} has nowhere to be thrown and holds the gateway open.");
            return;
        }
        victim.PlaceOnTile(landing, MovementKind.Forced);
        combatUI?.AppendActionLog($"{name} is thrown clear of the gate.");
    }

    /// <summary>Takes a field object off the board with no death effect and no
    /// "is destroyed" line: it was used or opened, not broken. Frees its tile and
    /// stops it blocking sight.</summary>
    private void RemoveFieldObjectQuietly(Unit obj)
    {
        if (obj == null || !IsInstanceValid(obj) || obj.IsDeathQueued)
            return;
        var tile = obj.CurrentTile;
        if (tile != null)
        {
            tile.BlocksLineOfSight = false;
            tile.AuthoredCover = CoverKind.None;
        }
        fieldObjects.Remove(obj);
        obj.Die();
    }
}
