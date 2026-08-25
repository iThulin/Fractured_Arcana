using Godot;
using System;
using System.Threading.Tasks;

// ============================================================
// CombatManager.WarpChanneler.cs  (partial)
//
// Purpose:        The "warp_channeler" behavior, the siege answer to
//                 "sit safely behind the door": a two-activation
//                 CHANNEL that, on release, teleports the unit past
//                 walls to a telegraphed tile beside its target.
//                 Interruptible: ANY damage while charging collapses
//                 the rift (warp only; the blast wizard's channel is
//                 deliberately NOT damage-interruptible; changing that
//                 would rebalance an existing unit as a side effect).
//                 Rides the existing Channel/Release intent machinery
//                 (ChannelTile, "wizard_charging", glyphs, the
//                 disable-break at RunEnemyTurn). New code is only the
//                 planner, the two executors, and the interrupt hook.
// Layer:          Combat / enemy AI
// Collaborators:  CombatManager.EnemyIntents (planner map + dispatch),
//                 CombatManager.Triggers (HandleUnitStruck interrupt),
//                 Unit.PlaceOnTile (MovementKind.Teleport, which is
//                 occupancy-safe, no OnMoved: a rift is not walking),
//                 Data/Units/veil_warper.json (first carrier)
// Notes:          Ritardando (EnemySpellCostIncrease) deliberately NOT
//                 applied to warp channels in v1. The counter-play is
//                 damage interruption, not drag stacking. Revisit if
//                 playtests want both.
// ============================================================

public partial class CombatManager
{
    private const string WarpBehaviorKey = "warp_channeler";

    /// <summary>Max tiles between warper and landing tile. Close-range by
    /// ruling: a rift, not a route.</summary>
    private const int WarpRange = 6;

    /// <summary>Fight-normally distance: already at arm's reach → no warp.</summary>
    private const int WarpMinUsefulDistance = 3;

    private static bool IsWarpChanneler(Unit u) =>
        u != null && string.Equals(u.BehaviorKey, WarpBehaviorKey,
                                   StringComparison.OrdinalIgnoreCase);

    // ── Planner ──────────────────────────────────────────────────────────────

    private EnemyIntent PlanWarpChanneler(Unit enemy)
    {
        // Charging → the rift opens this activation (locked at channel start;
        // a delay/retarget here would cheat the telegraph the player answered).
        if (enemy.HasStatus("wizard_charging") && enemy.ChannelTile.HasValue)
        {
            var locked = enemy.ChannelTile.Value;
            return new EnemyIntent
            {
                Kind = IntentKind.Release,
                TargetTile = locked,
                ThreatTiles = { locked },
                Value = 0,
                BaseValue = 0,
            };
        }

        var target = FindNearestPlayerUnit(enemy);
        if (target?.CurrentTile == null || enemy.CurrentTile == null)
            return PlanSoldier(enemy);

        // Close enough to fight like anyone else. The veil is for walls.
        if (grid.Distance(enemy.CurrentTile.Axial, target.CurrentTile.Axial)
            <= WarpMinUsefulDistance)
            return PlanSoldier(enemy);

        var dest = PickWarpDestination(enemy, target);
        if (dest == null)
            return PlanSoldier(enemy);

        return new EnemyIntent
        {
            Kind = IntentKind.Channel,
            TargetUnit = target,
            TargetTile = dest,
            ThreatTiles = { dest.Value },
            Value = 0,
            BaseValue = 0,
        };
    }

    /// <summary>Nearest free, walkable, unblocked tile adjacent to the target
    /// (ring 1, then ring 2), within WarpRange of the warper. Pathing and
    /// walls are IGNORED (that is the entire point), but occupancy is not.
    /// Deterministic tiebreak.</summary>
    private Vector2I? PickWarpDestination(Unit enemy, Unit target)
    {
        Vector2I from = enemy.CurrentTile.Axial;
        Vector2I around = target.CurrentTile.Axial;

        Vector2I? best = null;
        int bestKey = int.MaxValue;
        foreach (var kvp in grid.Tiles)
        {
            var td = kvp.Value;
            if (td == null || !td.IsWalkable || td.IsBlocked || td.IsOccupied)
                continue;
            int dTarget = grid.Distance(kvp.Key, around);
            if (dTarget < 1 || dTarget > 2)
                continue;
            if (grid.Distance(kvp.Key, from) > WarpRange)
                continue;
            // rank: closer to target first, then lexicographic for determinism
            int key = dTarget * 1_000_000 + (kvp.Key.X + 500) * 1000 + (kvp.Key.Y + 500);
            if (key < bestKey)
            {
                bestKey = key;
                best = kvp.Key;
            }
        }
        return best;
    }

    // ── Executors ────────────────────────────────────────────────────────────

    private async Task ExecuteWarpStart(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy) || intent.TargetTile == null)
            return;

        enemy.ChannelTile = intent.TargetTile;
        enemy.ApplyStatus("wizard_charging", 2);

        string msg = $"{enemy.Name} tears at the veil and a rift forms. Strike it to collapse the channel!";
        GD.Print($"[Warp] {enemy.Name} channels a rift to {intent.TargetTile.Value}.");
        combatUI?.AppendActionLog(msg);
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    private async Task ExecuteWarpRelease(Unit enemy, EnemyIntent intent)
    {
        if (!IsValidActor(enemy))
            return;

        Vector2I? locked = enemy.ChannelTile ?? intent.TargetTile;
        enemy.ChannelTile = null;
        enemy.RemoveStatus("wizard_charging");

        if (locked == null)
            return;

        // Land on the locked tile; if the player body-blocked it, the nearest
        // free neighbor; if the whole pocket is plugged, the rift fizzles.
        // Body-blocking is real counter-play and must pay off.
        TileData dest = grid.GetTile(locked.Value);
        if (dest == null || dest.IsBlocked || !dest.IsWalkable || dest.IsOccupied)
        {
            dest = null;
            foreach (var n in grid.GetNeighbors(locked.Value))
            {
                var td = grid.GetTile(n);
                if (td != null && td.IsWalkable && !td.IsBlocked && !td.IsOccupied)
                { dest = td; break; }
            }
        }

        if (dest == null)
        {
            string blocked = $"{enemy.Name}: the rift finds no ground and collapses.";
            GD.Print("[Warp] " + blocked);
            combatUI?.AppendActionLog(blocked);
            return;
        }

        enemy.PlaceOnTile(dest);   // MovementKind.Teleport: a rift is not walking
        string msg = $"{enemy.Name} steps through the veil!";
        GD.Print($"[Warp] {enemy.Name} emerges at {dest.Axial}.");
        combatUI?.AppendActionLog(msg);
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    // ── Interrupt (called from HandleUnitStruck) ─────────────────────────────

    /// <summary>Damage collapses a WARP channel (warp only; see header).
    /// Call on every strike; no-ops for everyone else.</summary>
    private void TryInterruptWarpChannel(Unit struck, int hpLoss)
    {
        if (hpLoss <= 0 || !IsWarpChanneler(struck))
            return;
        if (!struck.HasStatus("wizard_charging") || struck.ChannelTile == null)
            return;

        struck.ChannelTile = null;
        struck.RemoveStatus("wizard_charging");
        struck.CurrentIntent = null;
        struck.ClearIntentDisplay();

        string msg = $"{struck.Name}'s rift collapses. The channel is broken!";
        GD.Print("[Warp] " + msg);
        combatUI?.AppendActionLog($"── {msg} ──");
    }
}
