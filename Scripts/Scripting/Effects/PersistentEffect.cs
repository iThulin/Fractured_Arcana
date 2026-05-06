using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// PersistentEffect — A zone or aura that resolves each turn
//
// Add to GameState.ActiveEffects. GameRunner calls Tick() at
// the start of each player turn.
// ============================================================

public abstract class PersistentEffect
{
    public int TurnsRemaining;
    public Entity Owner;

    public abstract void Tick(GameState s);
    public bool IsExpired => TurnsRemaining <= 0;
}

// ── Maelstrom Zone ──────────────────────────────────────────
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
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (ownerUnit != null && unit.TeamId == ownerUnit.TeamId) continue;

            int dist = s.Grid.Distance(Center, unit.CurrentTile.Axial);
            if (dist > Radius) continue;

            // Deal damage
            unit.ApplyDamage(Damage);
            s.Log($"[Maelstrom] {unit.Name} takes {Damage} damage.");

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
public class AvatarAuraEffect : PersistentEffect
{
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

    // Called from GameRunner after every successful cast
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