using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.Reactions.cs
//
// Purpose:        Armed martial reactions (Martial Maneuvers & Edge
//                 spec v1 §7, M4). A reaction is a Self-shaped maneuver
//                 with IsReaction: arming pays its AP and Edge on the
//                 player's turn, the zone it covers is drawn on the
//                 board, and it fires by itself during the enemy phase.
//                 Unfired, it refunds its Edge at the unit's next turn
//                 start (a refund is not a gain: idle decay still bites).
//                 One armed reaction per unit.
//
//                 Two triggers at launch. enemy_move_end (Brace): the
//                 first enemy to end its movement in the zone is struck
//                 before it acts. enemy_enter_zone (Set Against Charge):
//                 an enemy stepping into the zone is struck and its
//                 movement ends on that tile.
//
//                 Enemy planners read the zones (§7b): scouts, stalkers
//                 and ranged units pay extra to path through them, so a
//                 detour of one tile wins; charge and pack units ignore
//                 them and walk into the spear.
// Layer:          Combat rules (partial of CombatManager)
// Collaborators:  Unit.cs (ArmedReaction, WalkStepReaction hook,
//                 MovementInterrupted), HexGridManager.Pathfinding.cs
//                 (ExtraStepCost hook), CombatManager.EnemyIntents.cs
//                 (CanSpendMoveAP gate, StrikeTile and post-intent call
//                 sites), CombatManager.Maneuvers.cs (arming entry),
//                 MovementZoneRenderer (the zone overlay)
// ============================================================

public partial class CombatManager
{
    private const string ReactionTriggerMoveEnd = "enemy_move_end";
    private const string ReactionTriggerEnterZone = "enemy_enter_zone";

    /// <summary>One overlay per armed unit; the zone follows the unit.</summary>
    private readonly Dictionary<Unit, MovementZoneRenderer> _reactionZones = new();
    /// <summary>Union of every armed zone, for the routing cost hook ONLY. Rebuilt
    /// on completed walks and on arm/fire/refund/death, so it can lag a push or a
    /// blink by one action; routing tolerates that, triggers do not use it.</summary>
    private readonly HashSet<Vector2I> _armedZoneTiles = new();

    private static readonly Color ReactionZoneColor = new(0.55f, 0.85f, 1.0f, 0.90f);   // steel, same family as the tray envelope
    private const float ReactionZoneFill = 0.10f;
    /// <summary>Routing cost a zone tile adds for a unit that routes around zones.
    /// 2 makes a one-tile detour strictly cheaper and a two-tile detour a tie (§7b).</summary>
    private const int ReactionZoneRouteCost = 2;

    // ── Install ──────────────────────────────────────────────────────────────

    private void InstallReactions()
    {
        Unit.WalkStepReaction = OnEnemyWalkStep;
        HexGridManager.ExtraStepCost = ReactionRoutingCost;
    }

    private void UninstallReactions()
    {
        if (Unit.WalkStepReaction == OnEnemyWalkStep)
            Unit.WalkStepReaction = null;
        if (HexGridManager.ExtraStepCost == ReactionRoutingCost)
            HexGridManager.ExtraStepCost = null;
        ClearAllReactionZones();
    }

    // ── Arming and refund ────────────────────────────────────────────────────

    /// <summary>Called by ResolveManeuver once AP and Edge are paid.</summary>
    private void ArmReaction(Unit u, ManeuverDefinition m, int edgePaid)
    {
        u.ArmedReaction = m;
        u.ArmedReactionEdge = edgePaid;
        string msg = $"{u.Name} arms {m.DisplayName}: {ReactionTriggerText(u, m)}";
        GD.Print($"[Reaction] {msg}");
        combatUI?.AppendActionLog(msg);
        RefreshAllReactionZones();
    }

    /// <summary>The trigger sentence the unit panel and the log show.</summary>
    private static string ReactionTriggerText(Unit u, ManeuverDefinition m)
    {
        bool guardian = (u.ActiveStance?.ManeuverRider ?? StanceManeuverRider.None) == StanceManeuverRider.GuardianReactions;
        string allies = guardian ? " (or beside an adjacent ally)" : "";
        return m.ReactionTrigger switch
        {
            ReactionTriggerMoveEnd => $"the first enemy to end its movement adjacent{allies} is struck before it acts.",
            ReactionTriggerEnterZone => $"an enemy moving within {m.Reach}{allies} is struck and stops there.",
            _ => m.Text,
        };
    }

    /// <summary>Spec §7a rule 2: an unfired reaction refunds its Edge at the unit's
    /// next turn start. Called beside EdgeRules.OnTurnStart.</summary>
    private void RefundUnfiredReaction(Unit u)
    {
        if (u?.ArmedReaction == null)
            return;
        var m = u.ArmedReaction;
        int edge = u.ArmedReactionEdge;
        u.ArmedReaction = null;
        u.ArmedReactionEdge = 0;
        if (edge > 0)
            EdgeRules.Refund(u, edge);
        combatUI?.AppendActionLog($"{u.Name}'s {m.DisplayName} lapses unfired" +
                                  (edge > 0 ? $": {edge} Edge returned." : "."));
        RefreshAllReactionZones();
    }

    // ── Zones ────────────────────────────────────────────────────────────────

    /// <summary>Tiles the armed reaction covers: everything within the maneuver's
    /// reach of the unit, and, with the Guardian rider, the same radius around
    /// each adjacent friendly unit (spec §7a rule 6).</summary>
    private HashSet<Vector2I> ReactionZone(Unit u)
    {
        var zone = new HashSet<Vector2I>();
        var m = u?.ArmedReaction;
        if (m == null || u.CurrentTile == null || grid == null)
            return zone;

        int reach = Math.Max(1, m.Reach);
        AddRadius(zone, u.CurrentTile.Axial, reach);

        if ((u.ActiveStance?.ManeuverRider ?? StanceManeuverRider.None) == StanceManeuverRider.GuardianReactions)
        {
            foreach (var n in grid.GetNeighbors(u.CurrentTile.Axial))
            {
                var ally = grid.GetTile(n)?.Occupant;
                if (ally != null && ally != u && ally.TeamId == u.TeamId && ally.Stats.IsAlive && !ally.IsMapObject)
                    AddRadius(zone, n, reach);
            }
        }
        zone.Remove(u.CurrentTile.Axial);
        return zone;
    }

    private void AddRadius(HashSet<Vector2I> into, Vector2I center, int radius)
    {
        foreach (var kv in grid.Tiles)
        {
            int d = grid.Distance(center, kv.Key);
            if (d >= 1 && d <= radius)
                into.Add(kv.Key);
        }
    }

    /// <summary>Redraws every armed zone and rebuilds the routing union. Cheap
    /// enough to call on any movement.</summary>
    private void RefreshAllReactionZones()
    {
        _armedZoneTiles.Clear();
        var armed = new HashSet<Unit>();
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.ArmedReaction == null)
                continue;
            armed.Add(u);
            var zone = ReactionZone(u);
            _armedZoneTiles.UnionWith(zone);
            var r = ReactionRendererFor(u);
            if (r == null)
                continue;
            r.Clear();
            if (zone.Count > 0)
                r.ShowOutline(zone, grid, ReactionZoneColor, ReactionZoneFill);
        }
        // Drop overlays of units no longer armed (fired, refunded, dead).
        foreach (var kv in new List<KeyValuePair<Unit, MovementZoneRenderer>>(_reactionZones))
        {
            if (armed.Contains(kv.Key))
                continue;
            if (kv.Value != null && IsInstanceValid(kv.Value))
                kv.Value.Clear();
        }
    }

    private MovementZoneRenderer ReactionRendererFor(Unit u)
    {
        if (grid == null)
            return null;
        if (_reactionZones.TryGetValue(u, out var r) && r != null && IsInstanceValid(r))
            return r;
        r = new MovementZoneRenderer
        {
            Name = $"ReactionZone_{u.Name}",
            HexRadius = grid.HexRadius,
        };
        grid.AddChild(r);
        _reactionZones[u] = r;
        return r;
    }

    private void ClearAllReactionZones()
    {
        foreach (var kv in _reactionZones)
            if (kv.Value != null && IsInstanceValid(kv.Value))
                kv.Value.Clear();
        _reactionZones.Clear();
        _armedZoneTiles.Clear();
    }

    /// <summary>A dead unit's reaction dies with it: no refund, no overlay.</summary>
    private void DropReactionOnDeath(Unit u)
    {
        if (u == null || u.ArmedReaction == null)
            return;
        u.ArmedReaction = null;
        u.ArmedReactionEdge = 0;
        RefreshAllReactionZones();
    }

    // ── Triggers ─────────────────────────────────────────────────────────────

    /// <summary>Unit.WalkStepReaction: the mover has just landed on
    /// <paramref name="entered"/>. Every Set Against Charge whose zone holds that
    /// tile fires. Returns true to end the walk there (spec §6a: "its movement
    /// ends there"); the mover loops also stop, through MovementInterrupted.</summary>
    private bool OnEnemyWalkStep(Unit mover, TileData entered)
    {
        if (CombatSim.Active || mover == null || entered == null || !IsInstanceValid(mover))
            return false;
        if (mover.IsPlayerControlled || !mover.Stats.IsAlive || mover.IsDeathQueued)
            return false;
        // No union precheck here: the union is rebuilt on walks, not on pushes,
        // slides or blinks, so the per-unit zone below is the authority.

        bool fired = false;
        foreach (var p in new List<Unit>(playerUnits))
        {
            if (p == null || !IsInstanceValid(p) || !p.Stats.IsAlive || p.ArmedReaction == null)
                continue;
            if (!string.Equals(p.ArmedReaction.ReactionTrigger, ReactionTriggerEnterZone, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ReactionZone(p).Contains(entered.Axial))
                continue;
            FireReaction(p, mover);
            fired = true;
            if (!mover.Stats.IsAlive || mover.IsDeathQueued)
                break;
        }

        if (fired)
        {
            mover.MovementInterrupted = true;
            if (mover.Stats.IsAlive)
                combatUI?.AppendActionLog($"{mover.Name}'s advance is stopped.");
        }
        return fired;
    }

    /// <summary>Brace: called when an enemy has finished moving and is about to act
    /// (StrikeTile), and again after its intent resolves for beats that never
    /// strike. Each armed Brace fires at most once, so the second call is a no-op
    /// for anyone the first already paid.</summary>
    private void FireMoveEndReactions(Unit enemy)
    {
        if (CombatSim.Active || enemy == null || !IsInstanceValid(enemy))
            return;
        if (enemy.IsPlayerControlled || !enemy.Stats.IsAlive || enemy.IsDeathQueued || enemy.CurrentTile == null)
            return;

        foreach (var p in new List<Unit>(playerUnits))
        {
            if (p == null || !IsInstanceValid(p) || !p.Stats.IsAlive || p.ArmedReaction == null)
                continue;
            if (!string.Equals(p.ArmedReaction.ReactionTrigger, ReactionTriggerMoveEnd, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ReactionZone(p).Contains(enemy.CurrentTile.Axial))
                continue;
            FireReaction(p, enemy);
            if (!enemy.Stats.IsAlive || enemy.IsDeathQueued)
                break;
        }
    }

    /// <summary>The armed reaction lands on <paramref name="enemy"/> and is spent.
    /// Damage is ATK plus the card's delta, times its multiplier, plus the stance's
    /// maneuver damage riders. Like the zone-of-control strike it applies damage
    /// directly, with no trigger-stack drain mid-walk; item onAttack procs do not
    /// ride reactions at launch.</summary>
    private void FireReaction(Unit reactor, Unit enemy)
    {
        var m = reactor.ArmedReaction;
        if (m == null)
            return;
        reactor.ArmedReaction = null;
        reactor.ArmedReactionEdge = 0;

        var rider = reactor.ActiveStance?.ManeuverRider ?? StanceManeuverRider.None;
        int damage = reactor.AttackDamage + (m.Effect("damage")?.Value ?? 0);
        var mult = m.Effect("damage_multiplier");
        if (mult != null && mult.Value >= 2)
            damage *= mult.Value;
        if (rider == StanceManeuverRider.DamageVsVulnerable && enemy.HasStatus("vulnerable"))
            damage += 1;
        if (rider == StanceManeuverRider.DamageBelowHalf && reactor.Stats.Health * 2 < reactor.Stats.MaxHealth)
            damage += 1;

        EdgeRules.OnAttackHits(reactor, new List<Unit> { enemy });
        RefreshOpeningsMarkers(reactor, new List<Unit> { enemy });   // Opportunist (M5)

        int markedBonus = 0;
        if (enemy.HasStatus("marked"))
        {
            markedBonus = 3;
            enemy.RemoveStatus("marked");
            combatUI?.AppendActionLog($"[Mark] {enemy.Name} was marked, for +{markedBonus} damage!");
        }
        damage = Math.Max(1, damage + markedBonus);

        string msg = $"[{m.DisplayName}] {reactor.Name} strikes {enemy.Name} for {damage} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);

        var delivery = reactor.CurrentTile != null && enemy.CurrentTile != null
            && grid.Distance(reactor.CurrentTile, enemy.CurrentTile) > 1
            ? Delivery.Bolt : Delivery.Melee;
        CombatPresenter.EmitStrike(reactor, enemy, delivery);
        enemy.ApplyDamage(damage, reactor, delivery);

        reactor.HasAttackedThisCombat = true;
        CombatTelemetry.RecordReactionFire(reactor.Name, m.Id, roundNumber);
        if (rider == StanceManeuverRider.GainShield)
        {
            reactor.Stats.Shield += 1;
            reactor.RefreshHealthBar();
        }

        RefreshAllReactionZones();
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        _pruneNeeded = true;
    }

    // ── Enemy routing (spec §7b) ─────────────────────────────────────────────

    /// <summary>HexGridManager.ExtraStepCost: zone tiles cost more for units that
    /// route around them (scouts, stalkers, ranged). charge and pack ignore zones;
    /// so does everyone on the player's side, so player pathing never changes.</summary>
    private int ReactionRoutingCost(Unit mover, TileData tile)
    {
        if (_armedZoneTiles.Count == 0 || mover == null || tile == null)
            return 0;
        if (mover.IsPlayerControlled || !_armedZoneTiles.Contains(tile.Axial))
            return 0;
        if (mover.HasBehaviorTag("charge") || mover.HasBehaviorTag("pack"))
            return 0;
        bool routes = mover.HasBehaviorTag("scout")
                      || string.Equals(mover.BehaviorKey, "melee_hunt_wounded", StringComparison.OrdinalIgnoreCase)
                      || mover.AttackRange > 1;
        return routes ? ReactionZoneRouteCost : 0;
    }
}
