using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.ZoneOfControl.cs
//
// Purpose:        Free strikes when a unit walks out of an adjacent
//                 enemy's reach (cover_and_zoc_v1 §3). Installed into
//                 Unit.ZoneOfControlStrike so the walk loop in
//                 Unit.TryMoveTo can ask for a strike between steps
//                 without knowing about the manager. Also the query the
//                 move highlight and the enemy AI use to price an exit.
// Layer:          Systems / Combat / Core (partial of CombatManager)
// Collaborators:  Unit (ZoneOfControlLeavers, ExertsZoneOfControl),
//                 HexGridManager (adjacency), CombatUI (action log)
// See:            docs/cover_and_zoc_v1.md
// ============================================================

public partial class CombatManager
{
    /// <summary>Wire the resolver. Called from _Ready; cleared on exit so a
    /// headless test that outlives this scene never strikes through a dead manager.</summary>
    private void InstallZoneOfControl()
    {
        Unit.ZoneOfControlStrike = ResolveZoneOfControlStrike;
    }

    private void UninstallZoneOfControl()
    {
        if (Unit.ZoneOfControlStrike == ResolveZoneOfControlStrike)
            Unit.ZoneOfControlStrike = null;
    }

    /// <summary>One free melee swing from <paramref name="striker"/> at
    /// <paramref name="mover"/>, who is stepping out of reach. Base attack damage
    /// only: no stance riders, no Ambush doubling, no charge. A free strike is a
    /// reflex, not a prepared attack, and keeping it flat keeps it readable. Costs
    /// no AP and does not mark the striker as having attacked. Returns true when the
    /// mover can no longer continue (dead or queued for death).</summary>
    private bool ResolveZoneOfControlStrike(Unit striker, Unit mover)
    {
        if (striker == null || mover == null || !IsInstanceValid(striker) || !IsInstanceValid(mover))
            return false;
        if (!striker.ExertsZoneOfControl() || !mover.Stats.IsAlive || mover.IsDeathQueued)
            return false;

        int damage = striker.ModifyOutgoingAttackDamage(striker.AttackDamage);
        if (damage <= 0)
            return false;

        string line = $"{mover.Name} breaks away from {striker.Name} and takes a free strike for {damage}.";
        GD.Print($"[ZoC] {line}");
        combatUI?.AppendActionLog(line);

        mover.ApplyDamage(damage, striker, Delivery.Melee);
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();

        return !mover.Stats.IsAlive || mover.IsDeathQueued;
    }

    /// <summary>Total free-strike damage <paramref name="mover"/> would eat walking
    /// from its tile to <paramref name="dest"/> along the grid's chosen path. Zero
    /// when the walk leaves nobody's reach. Used to tint the move highlight and by
    /// the enemy AI to price a retreat.</summary>
    public int ZoneOfControlCostTo(Unit mover, Vector2I dest)
    {
        if (mover?.CurrentTile == null || grid == null)
            return 0;
        var path = grid.GetPathTo(mover, grid.GetTile(dest));
        if (path == null || path.Count == 0)
            return 0;

        int total = 0;
        var struck = new HashSet<Unit>();
        var from = mover.CurrentTile;
        foreach (var coord in path)
        {
            var step = grid.GetTile(coord);
            if (step == null)
                break;
            foreach (var s in mover.ZoneOfControlLeavers(grid, from, step))
                if (struck.Add(s))
                    total += Math.Max(0, s.ModifyOutgoingAttackDamage(s.AttackDamage));
            from = step;
        }
        return total;
    }
}
