using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// PersistentEffect.cs
//
// Purpose:        Persistent effects — zones, auras, and other
//                 state-machines that live across turns and tick
//                 at the start of each player turn. Spawned by
//                 leaf effects (CreateMaelstromEffect,
//                 AvatarTransformEffect) and tracked on
//                 GameState.ActiveEffects.
// Layer:          Effects
// Collaborators:  GameState.cs (ActiveEffects list, Tick driver),
//                 CompositeEffects.cs (CreateMaelstromEffect and
//                 AvatarTransformEffect spawn instances here),
//                 Effect.cs (DealDamageEffect queries
//                 AvatarAuraEffect for the bonus damage stack),
//                 ElementalAttunement.cs (ElementTag mapping)
// See:            README §6 — Persistent Effects,
//                 README §6 — Elemental Attunement
// ============================================================

/// <summary>
/// Abstract base for any effect that ticks across turns. <see cref="Tick"/> is invoked
/// once per player turn by the combat loop; the implementation is responsible for
/// decrementing <see cref="TurnsRemaining"/>. The combat loop garbage-collects entries
/// where <see cref="IsExpired"/> is true.
/// </summary>
public abstract class PersistentEffect
{
    /// <summary>Turns this effect has left before it should be culled. Implementations decrement this in <see cref="Tick"/>.</summary>
    public int TurnsRemaining;

    /// <summary>The casting Entity. Used to determine team affiliation for friendly-fire filtering.</summary>
    public Entity Owner;

    /// <summary>Called once per player turn at start-of-turn. Implementation must decrement <see cref="TurnsRemaining"/>.</summary>
    public abstract void Tick(GameState s);

    /// <summary>True once <see cref="TurnsRemaining"/> reaches 0. The combat loop garbage-collects expired entries.</summary>
    public bool IsExpired => TurnsRemaining <= 0;
}

// ── Maelstrom Zone ──────────────────────────────────────────

/// <summary>
/// Rotating storm zone. Each tick: imbues every tile in radius with Lightning, deals
/// <see cref="Damage"/> to every enemy in radius, and pushes each surviving enemy one
/// tile in the current rotation direction (advances through the 6 hex directions over
/// successive ticks). When <see cref="Freezes"/> is set, also applies the frozen status.
/// </summary>
public class MaelstromEffect : PersistentEffect
{
    public Vector2I Center;
    public int Radius;
    public int Damage;
    public bool Freezes;

    // Track rotation direction (0-5, one of the 6 hex directions)
    private int _rotationStep = 0;

    private static readonly Vector2I[] HexDirs =
    {
        new Vector2I(1, 0),  new Vector2I(1, -1), new Vector2I(0, -1),
        new Vector2I(-1, 0), new Vector2I(-1, 1), new Vector2I(0, 1)
    };

    public MaelstromEffect(Vector2I center, int radius, int damage,
        int turns, Entity owner, bool freezes = false)
    {
        Center = center;
        Radius = radius;
        Damage = damage;
        TurnsRemaining = turns;
        Owner = owner;
        Freezes = freezes;
    }

    public override void Tick(GameState s)
    {
        if (s?.Grid == null) return;

        Unit ownerUnit = null;
        if (Owner == s.PlayerA) ownerUnit = s.PlayerUnit;
        else if (Owner == s.PlayerB) ownerUnit = s.EnemyUnit;

        // Get current rotation direction
        var rotDir = HexDirs[_rotationStep % 6];

        // Imbue all tiles in radius with storm
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(Center, kvp.Key) > Radius) continue;
            var tile = kvp.Value;
            if (tile == null) continue;
            tile.ElementType = TileElementType.Lightning;
            tile.ElementStrength = 1.0f;
            s.Grid.ApplyVisualToTile(tile);
        }

        // Deal damage and push enemies clockwise
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !Godot.GodotObject.IsInstanceValid(unit))
                continue;
            if (!unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (ownerUnit != null && unit.TeamId == ownerUnit.TeamId) continue;

            int dist = s.Grid.Distance(Center, unit.CurrentTile.Axial);
            if (dist > Radius) continue;

            // Deal damage
            unit.ApplyDamage(Damage);
            s.Log($"[Maelstrom] {unit.Name} takes {Damage} damage.");

            // Re-check after damage — unit may have died and CurrentTile nulled
            if (!Godot.GodotObject.IsInstanceValid(unit) || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;

            // Push clockwise — find the neighbor in rotation direction
            var current = unit.CurrentTile.Axial;
            var pushTarget = current + rotDir;
            var pushTile = s.Grid.GetTile(pushTarget);

            if (pushTile != null && pushTile.CanEnter(unit))
            {
                unit.CurrentTile.ClearOccupant(unit);
                unit.PlaceOnTile(pushTile);
                s.Log($"[Maelstrom] {unit.Name} pushed clockwise.");
            }

            if (Freezes)
            {
                unit.ApplyStatus("frozen", 1);
                s.Log($"[Maelstrom] {unit.Name} frozen.");
            }
        }

        // Advance rotation
        _rotationStep = (_rotationStep + 1) % 6;
        TurnsRemaining--;

        s.Log($"[Maelstrom] Ticked. {TurnsRemaining} turns remaining.");
    }
}

// ── Avatar Aura ─────────────────────────────────────────────

/// <summary>
/// Spell-cast aura created by <c>AvatarTransformEffect</c>. While active, every spell cast by
/// the owner gets +<see cref="BonusDamage"/> (queried by <c>DealDamageEffect</c> via
/// <c>GameState.GetActiveEffect&lt;AvatarAuraEffect&gt;</c>), and <see cref="OnSpellCast"/>
/// random-imbues each spell's target tile.
/// </summary>
public class AvatarAuraEffect : PersistentEffect
{
    /// <summary>Bonus damage added to every spell cast while this aura is active.</summary>
    public int BonusDamage;

    private static readonly TileElementType[] Elements =
    {
        TileElementType.Fire, TileElementType.Frost,
        TileElementType.Lightning, TileElementType.Earth
    };
    private Random _rng = new();

    public AvatarAuraEffect(int turns, int bonusDamage, Entity owner)
    {
        TurnsRemaining = turns;
        BonusDamage = bonusDamage;
        Owner = owner;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        s.Log($"[Avatar] Aura ticking. {TurnsRemaining} turns remaining.");
    }

    /// <summary>Hook invoked by the combat runner after every successful spell resolution by the owner. Random-imbues each target tile and logs the bonus damage application.</summary>
    public void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (s?.Grid == null || targets == null) return;

        // Random element imbue on target tile
        var element = Elements[_rng.Next(Elements.Length)];

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td) tile = td;
            else if (obj is HexTile tv) tile = s.Grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null) tile = u.CurrentTile;

            if (tile != null)
            {
                tile.ElementType = element;
                tile.ElementStrength = 1.0f;
                if (element == TileElementType.Fire) tile.IsHazardous = true;
                tile.TileView?.SetElement(element);
                s.Log($"[Avatar] Imbued {tile.Axial} with {element}.");
            }
        }

        s.Log($"[Avatar] Spell deals +{BonusDamage} bonus damage.");
    }
}

// In PersistentEffect.cs — add after AvatarAuraEffect

// ── Hollow Mantle ────────────────────────────────────────────

/// <summary>
/// Necromancer legendary aura. While active: spells cost 1 less mana
/// and the caster cannot be reduced below 1HP by any single hit.
/// Ticks down each turn; expired by the combat loop automatically.
/// </summary>
public class HollowMantleEffect : PersistentEffect
{
    public HollowMantleEffect(int turns, Entity owner)
    {
        TurnsRemaining = turns;
        Owner = owner;
    }

    public override void Tick(GameState s)
    {
        TurnsRemaining--;
        s.Log($"[HollowMantle] {TurnsRemaining} turn(s) remaining.");
    }

    /// <summary>
    /// Called by DealDamageEffect (or wherever damage is applied) to clamp
    /// incoming damage so the caster cannot drop below 1HP in a single hit.
    /// Wire this into Unit.ApplyDamage the same way AvatarAuraEffect.BonusDamage
    /// is queried by DealDamageEffect.
    /// </summary>
    public int ClampDamage(Unit target, int incomingDamage)
    {
        if (target.Stats.Health <= 1) return incomingDamage;
        return Math.Min(incomingDamage, target.Stats.Health - 1);
    }
}