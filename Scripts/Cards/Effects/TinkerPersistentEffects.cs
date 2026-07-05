using Godot;
using System;

// ============================================================
// TinkerPersistentEffects.cs
//
// Purpose:        Tinker persistent effects and the leaf effects
//                 that spawn them. Assembly Line deploys a free
//                 Drone at the start of each player turn for a few
//                 turns; Disruption Field is a lingering zone that
//                 damages (and optionally slows) enemies inside it
//                 once per round. Both follow the project pattern:
//                 a leaf EffectBase.Resolve adds a PersistentEffect
//                 to GameState.ActiveEffects, which the combat loop
//                 ticks at start-of-player-turn and culls on expiry.
// Layer:          Effects
// Collaborators:  PersistentEffect.cs (base), GameState.cs
//                 (ActiveEffects, OnSummonRequested, Grid),
//                 Unit.Construct.cs, CombatManager.cs (tick driver
//                 + the summon handler that enforces the cap)
// Notes:          Tinker constructs deploy through the standard
//                 summon path, so Assembly Line drones respect the
//                 live construct cap automatically.
// ============================================================

// ── Assembly Line (persistent) ──────────────────────────────────────
/// <summary>
/// At the start of each player turn, deploys a free Drone on an empty tile adjacent
/// to the owner. Spawns through GameState.OnSummonRequested, so it obeys the
/// construct cap and benefits from the current Schematics deploy bonus.
/// </summary>
public sealed class AssemblyLinePersistentEffect : PersistentEffect
{
    public Unit OwnerUnit;

    public AssemblyLinePersistentEffect(int turns, Entity owner, Unit ownerUnit)
    {
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;

        if (s?.OnSummonRequested == null || s.Grid == null)
            return;
        if (OwnerUnit == null || !OwnerUnit.Stats.IsAlive || OwnerUnit.CurrentTile == null)
            return;

        foreach (var n in s.Grid.GetNeighbors(OwnerUnit.CurrentTile.Axial))
        {
            var td = s.Grid.GetTile(n);
            if (td != null && td.IsWalkable && !td.IsBlocked && td.Occupant == null)
            {
                var spawned = s.OnSummonRequested("drone", td, OwnerUnit.TeamId);
                if (spawned != null)
                    s.Log("[AssemblyLine] A drone rolls off the line.");
                else
                    s.Log("[AssemblyLine] Line idle — construct cap reached.");
                return;
            }
        }

        s.Log("[AssemblyLine] No open tile to deploy a drone this turn.");
    }
}

// ── Disruption Field (persistent zone) ──────────────────────────────
/// <summary>
/// Lingering zone centered on a tile. Once per round (at start-of-player-turn) every
/// enemy inside the radius takes damage and, optionally, is slowed. Allies and the
/// owner's constructs are spared. Ticks on the player turn only — for a control zone
/// this resolves as one pulse per round.
/// </summary>
public sealed class DisruptionFieldZone : PersistentEffect
{
    public Vector2I Center;
    public int Radius, DamagePerTurn;
    public bool Slows;
    public Unit OwnerUnit;

    public DisruptionFieldZone(Vector2I center, int radius, int damagePerTurn,
        bool slows, int turns, Entity owner, Unit ownerUnit)
    {
        Center = center;
        Radius = radius;
        DamagePerTurn = damagePerTurn;
        Slows = slows;
        TurnsRemaining = turns;
        Owner = owner;
        OwnerUnit = ownerUnit;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        if (s?.Grid == null)
            return;

        int ownerTeam = OwnerUnit?.TeamId ?? (Owner == s.PlayerA ? 0 : 1);

        foreach (var u in s.UnitsInPlay)
        {
            if (u == null || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (u.TeamId == ownerTeam)
                continue;
            if (s.Grid.Distance(Center, u.CurrentTile.Axial) > Radius)
                continue;

            if (DamagePerTurn > 0)
                u.ApplyDamage(DamagePerTurn);
            if (Slows && u.Stats.IsAlive)
                u.ApplyStatus("slowed", 1);
        }

        s.Log($"[DisruptionField] Pulsed at {Center}. {TurnsRemaining} turn(s) left.");
    }
}

// ── Leaf creators ───────────────────────────────────────────────────

/// <summary>Leaf effect: brings an Assembly Line online for a number of turns.</summary>
public sealed class AssemblyLineEffect : EffectBase
{
    private readonly int _turns;
    public AssemblyLineEffect(int turns) { _turns = Math.Max(1, turns); }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.ActiveEffects == null)
        {
            s?.Log("[AssemblyLine] No active-effects list — cannot start line.");
            return;
        }
        s.ActiveEffects.Add(new AssemblyLinePersistentEffect(_turns, caster, s.ActiveCasterUnit));
        s.Log($"[AssemblyLine] Online for {_turns} turn(s).");
    }
}

/// <summary>Leaf effect: deploys a Disruption Field zone. Center is the targeted tile/unit, else the caster's tile.</summary>
public sealed class DisruptionFieldEffect : EffectBase
{
    private readonly int _radius, _damage, _turns;
    private readonly bool _slows;

    public DisruptionFieldEffect(int radius, int damage, bool slows, int turns)
    {
        _radius = Math.Max(0, radius);
        _damage = Math.Max(0, damage);
        _slows = slows;
        _turns = Math.Max(1, turns);
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.ActiveEffects == null)
        {
            s?.Log("[DisruptionField] No active-effects list — cannot deploy.");
            return;
        }

        var center = ResolveCenter(s, targets);
        s.ActiveEffects.Add(new DisruptionFieldZone(
            center, _radius, _damage, _slows, _turns, caster, s.ActiveCasterUnit));
        s.Log($"[DisruptionField] Deployed at {center} (r{_radius}, {_damage}/turn, {_turns}t).");
    }

    private Vector2I ResolveCenter(GameState s, TargetSet targets)
    {
        if (targets != null)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is TileData td) return td.Axial;
                if (obj is HexTile tv) return tv.Axial;
                if (obj is Unit u && u.CurrentTile != null) return u.CurrentTile.Axial;
            }
        }
        return s.ActiveCasterUnit?.CurrentTile?.Axial ?? Vector2I.Zero;
    }
}
