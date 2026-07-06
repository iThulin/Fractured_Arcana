using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ElementalistEffects.cs
//
// Purpose:        Elementalist school effects — terrain/element composites and the
//                 Maelstrom / Avatar persistent auras.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase, core leaves),
//                 PersistentEffect.cs (PersistentEffect base),
//                 CardScriptRegistry.Elementalist.cs (registration)
// Notes:          Extracted from Effect.cs / CompositeEffects.cs /
//                 PersistentEffect.cs — pure move, no behavior change.
// ============================================================

/// <summary>Elementalist capstone. Randomly imbues every tile within radius around the caster, then damages each enemy by <c>uniqueElementsAdjacent × Damage</c>.</summary>
public sealed class PrimordialSurgeEffect : EffectBase
{
    public int Radius;
    public int Damage;
    private static readonly TileElementType[] Elements =
    {
        TileElementType.Fire, TileElementType.Frost,
        TileElementType.Lightning, TileElementType.Earth
    };
    private static readonly Random _rng = new();

    public PrimordialSurgeEffect(int radius = 4, int damage = 4) { Radius = radius; Damage = damage; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null)
            return;

        var center = casterUnit.CurrentTile.Axial;

        // Imbue tiles within radius
        int imbued = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null)
                continue;
            if (s.Grid.Distance(center, kvp.Key) > Radius)
                continue;

            var element = Elements[_rng.Next(Elements.Length)];
            tile.ElementType = element;
            tile.ElementStrength = 1.0f;
            if (element == TileElementType.Fire)
                tile.IsHazardous = true;
            tile.TileView?.SetElement(element);
            imbued++;
        }

        s.Log($"[PrimordialSurge] Imbued {imbued} tiles within {Radius} range.");

        // Damage enemies based on unique adjacent elements
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
                continue;

            var adjacentElements = new HashSet<TileElementType>();

            if (unit.CurrentTile.ElementType != TileElementType.None)
                adjacentElements.Add(unit.CurrentTile.ElementType);

            foreach (var neighbor in s.Grid.GetNeighbors(unit.CurrentTile.Axial))
            {
                var tile = s.Grid.GetTile(neighbor);
                if (tile != null && tile.ElementType != TileElementType.None)
                    adjacentElements.Add(tile.ElementType);
            }

            int uniqueCount = adjacentElements.Count;
            if (uniqueCount > 0)
            {
                int totalDmg = uniqueCount * Damage;
                unit.ApplyDamage(totalDmg);
                s.Log($"[PrimordialSurge] {unit.Name}: {uniqueCount} element(s), takes {totalDmg} damage.");
            }
        }
    }
}

/// <summary>Elementalist capstone. Destroys all imbued tiles within radius, deals <c>destroyed × DamagePerTile</c> to every enemy in radius, and draws <c>destroyed / TilesPerDraw</c> cards.</summary>
public sealed class CataclysmEffect : EffectBase
{
    public int Radius;
    public int DamagePerTile;
    public int TilesPerDraw;

    public CataclysmEffect(int radius = 4, int damagePerTile = 2, int tilesPerDraw = 3)
    {
        Radius = radius;
        DamagePerTile = damagePerTile;
        TilesPerDraw = tilesPerDraw;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null)
            return;

        var center = casterUnit.CurrentTile.Axial;

        // Clear imbued tiles within radius
        int destroyed = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null)
                continue;
            if (s.Grid.Distance(center, kvp.Key) > Radius)
                continue;
            if (tile.ElementType == TileElementType.None)
                continue;

            tile.ElementType = TileElementType.None;
            tile.ElementStrength = 0f;
            tile.IsHazardous = false;
            tile.TileView?.SetElement(TileElementType.None);
            destroyed++;
        }

        s.Log($"[Cataclysm] Destroyed {destroyed} imbued tile(s) within {Radius} range.");

        if (destroyed == 0)
            return;

        // Damage enemies in radius
        int totalDmg = destroyed * DamagePerTile;
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
                continue;
            if (s.Grid.Distance(center, unit.CurrentTile.Axial) > Radius)
                continue;

            unit.ApplyDamage(totalDmg);
            s.Log($"[Cataclysm] {unit.Name} takes {totalDmg} damage ({destroyed} x {DamagePerTile}).");
        }

        int cardsToDraw = destroyed / TilesPerDraw;
        if (cardsToDraw > 0)
        {
            var drawUnit = FindCasterUnit(s, caster);
            if (drawUnit?.DeckData != null)
            {
                drawUnit.DeckData.Draw(cardsToDraw);
                s.OnDrawCards?.Invoke(drawUnit);
                s.Log($"[Cataclysm] Draw {cardsToDraw} card(s).");
            }
            s.Log($"[Cataclysm] Draw {cardsToDraw} card(s).");
        }
    }
}

/// <summary>Elementalist capstone — board-wipe. Counts unique elements imbued across the entire grid, purges them all, and deals <c>uniqueElements × DamagePerElement</c> to every unit. Allies take half damage when <see cref="HalfToAllies"/>.</summary>
public sealed class RagnarokEffect : EffectBase
{
    public int DamagePerElement;
    public bool HalfToAllies;

    public RagnarokEffect(int damagePerElement = 7, bool halfToAllies = false)
    {
        DamagePerElement = damagePerElement;
        HalfToAllies = halfToAllies;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);

        // Count unique elements on the board
        var uniqueElements = new HashSet<TileElementType>();
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile != null && tile.ElementType != TileElementType.None)
                uniqueElements.Add(tile.ElementType);
        }

        int elementCount = uniqueElements.Count;
        if (elementCount == 0)
        {
            s.Log("[Ragnarok] No elements on the board. No damage dealt.");
            return;
        }

        int totalDmg = elementCount * DamagePerElement;
        s.Log($"[Ragnarok] {elementCount} unique element(s) found. Dealing {totalDmg} damage to all units!");

        // Purge all imbued tiles
        int purged = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null || tile.ElementType == TileElementType.None)
                continue;
            tile.ElementType = TileElementType.None;
            tile.ElementStrength = 0f;
            tile.IsHazardous = false;
            tile.TileView?.SetElement(TileElementType.None);
            purged++;
        }
        s.Log($"[Ragnarok] Purged {purged} imbued tiles.");

        // Deal damage to ALL units
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive)
                continue;

            int dmg = totalDmg;
            if (HalfToAllies && casterUnit != null && unit.TeamId == casterUnit.TeamId)
                dmg = totalDmg / 2;

            unit.ApplyDamage(dmg);
            s.Log($"[Ragnarok] {unit.Name} takes {dmg} damage.");
        }
    }
}

/// <summary>Elementalist capstone. Imbues every tile in radius with a random element, then snaps every elemental attunement counter on the caster to <see cref="AttunementSetTo"/>. See README §7 — JSON key is `attunement_set_to`, NOT `attunement_counters`.</summary>
public sealed class ElementalConvergenceEffect : EffectBase
{
    public int Radius;
    public int AttunementSetTo;

    private static readonly TileElementType[] Elements =
    {
        TileElementType.Fire, TileElementType.Frost,
        TileElementType.Lightning, TileElementType.Earth
    };
    private Random _rng = new();

    public ElementalConvergenceEffect(int radius = 3, int attunementSetTo = 3)
    {
        Radius = radius;
        AttunementSetTo = attunementSetTo;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null)
            return;

        var center = casterUnit.CurrentTile.Axial;

        // Imbue all tiles within radius with random elements
        int imbued = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null)
                continue;
            if (s.Grid.Distance(center, kvp.Key) > Radius)
                continue;

            var element = Elements[_rng.Next(Elements.Length)];
            tile.ElementType = element;
            tile.ElementStrength = 1.0f;
            if (element == TileElementType.Fire)
                tile.IsHazardous = true;
            tile.TileView?.SetElement(element);
            imbued++;
        }

        s.Log($"[Convergence] Imbued {imbued} tiles within {Radius} range with random elements.");

        // Set all attunement counters
        if (casterUnit.Attunement is ElementalAttunement att)
        {
            foreach (var element in new[] { ElementTag.Fire, ElementTag.Ice, ElementTag.Storm, ElementTag.Earth })
            {
                att.Charges[element] = AttunementSetTo;
            }
            s.Log($"[Convergence] All attunement counters set to {AttunementSetTo}!");
        }
    }
}

/// <summary>Elementalist capstone. Destroys all stone-typed tiles, earth-imbued tiles, and stone obstacles within radius of the target, replaces them with rubble, and deals <c>destroyed × DamagePerTile</c> to the single nearest enemy.</summary>
public sealed class TectonicShatterEffect : EffectBase
{
    public int Radius;
    public int DamagePerTile;

    public TectonicShatterEffect(int radius = 3, int damagePerTile = 5)
    {
        Radius = radius;
        DamagePerTile = damagePerTile;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null)
            return;

        // Center on the target, not the caster
        Vector2I center = casterUnit.CurrentTile.Axial;
        if (targets != null)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is Unit u && u.CurrentTile != null)
                { center = u.CurrentTile.Axial; break; }
                if (obj is TileData td)
                { center = td.Axial; break; }
                if (obj is HexTile tv)
                { center = tv.Axial; break; }
            }
        }

        // Find and destroy all stone tiles in radius
        int destroyed = 0;
        var destroyedTiles = new List<TileData>();

        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) > Radius)
                continue;
            var tile = kvp.Value;
            if (tile == null)
                continue;

            bool isStone = tile.TerrainType == TileTerrainType.Stone ||
                           tile.ElementType == TileElementType.Earth ||
                            (tile.IsBlocked &&
                            (tile.ObstacleKind == "rock" ||
                            tile.ObstacleKind == "stone" ||
                            tile.ObstacleKind == "boulder" ||
                            tile.ObstacleKind == "stone_pillar"));

            if (!isStone)
                continue;

            // Destroy it — clear obstacle, set to difficult terrain
            tile.IsBlocked = false;
            tile.IsWalkable = true;
            tile.BlocksLineOfSight = false;
            tile.ObstacleKind = "";
            tile.ElementType = TileElementType.None;
            tile.ElementStrength = 0f;
            tile.ApplyTerrainModifier("rubble");
            s.Grid.ApplyVisualToTile(tile);

            // Remove any unit occupying the obstacle (summons like stone pillars)
            if (tile.Occupant != null)
            {
                string unitName = tile.Occupant.Name.ToString().ToLowerInvariant();
                bool isPillar = unitName.Contains("pillar") ||
                                unitName.Contains("boulder") ||
                                tile.Occupant.Stats.BaseSpeed == 0;

                if (isPillar)
                {
                    tile.Occupant.ApplyDamage(999);
                    s.Log($"[TectonicShatter] Destroyed {tile.Occupant.Name} at {tile.Axial}.");
                }
            }

            var tileView = tile.TileView;
            if (tileView != null)
            {
                var obstacles = s.Grid.GetTree().GetNodesInGroup("generated_obstacle");
                foreach (Node node in obstacles)
                {
                    if (node is Node3D n3d &&
                        n3d.GlobalPosition.DistanceTo(tileView.GlobalPosition) < 0.5f)
                    {
                        n3d.QueueFree();
                        break;
                    }
                }
            }

            destroyedTiles.Add(tile);
            destroyed++;
        }

        s.Log($"[TectonicShatter] Destroyed {destroyed} stone feature(s) in radius {Radius}.");

        if (destroyed == 0)
            return;

        // For each destroyed tile, deal damage to nearest enemy
        int totalDmg = destroyed * DamagePerTile;

        // Find nearest enemy to center
        Unit nearest = null;
        int nearestDist = int.MaxValue;
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
                continue;
            int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
            if (dist < nearestDist)
            { nearest = unit; nearestDist = dist; }
        }

        if (nearest != null)
        {
            nearest.ApplyDamage(totalDmg);
            s.Log($"[TectonicShatter] {nearest.Name} takes {totalDmg} damage ({destroyed} x {DamagePerTile}).");
        }
    }
}

/// <summary>Elementalist capstone. Reshapes all tiles within radius into a terrain matching the caster's highest attunement element (Fire→Lava, Ice→Ice, Storm/Earth→Stone), pushes every enemy in the area outward to the edge, then deals <see cref="Damage"/> to each.</summary>
public sealed class TerraformEffect : EffectBase
{
    public int Radius;
    public int Damage;

    public TerraformEffect(int radius = 3, int damage = 6)
    {
        Radius = radius;
        Damage = damage;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null)
            return;

        // Find center from targets or use caster
        Vector2I center = casterUnit.CurrentTile.Axial;
        if (targets != null)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is Unit u && u.CurrentTile != null)
                { center = u.CurrentTile.Axial; break; }
                if (obj is TileData td)
                { center = td.Axial; break; }
                if (obj is HexTile tv)
                { center = tv.Axial; break; }
            }
        }

        // Determine reshape element from highest attunement
        var element = casterUnit.HighestAttunementElement;

        TileTerrainType newTerrain = element switch
        {
            ElementTag.Fire => TileTerrainType.Lava,
            ElementTag.Ice => TileTerrainType.Ice,
            ElementTag.Storm => TileTerrainType.Stone,
            ElementTag.Earth => TileTerrainType.Stone,
            _ => TileTerrainType.Stone
        };

        TileElementType newElement = element switch
        {
            ElementTag.Fire => TileElementType.Fire,
            ElementTag.Ice => TileElementType.Frost,
            ElementTag.Storm => TileElementType.Lightning,
            ElementTag.Earth => TileElementType.Earth,
            _ => TileElementType.Earth
        };

        // Reshape all tiles in radius
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) > Radius)
                continue;
            var tile = kvp.Value;
            if (tile == null)
                continue;

            tile.TerrainType = newTerrain;
            tile.ElementType = newElement;
            tile.ElementStrength = 1.0f;

            if (newTerrain == TileTerrainType.Lava)
            {
                tile.IsHazardous = true;
                tile.MoveCost = 2;
            }
            else if (newTerrain == TileTerrainType.Ice)
            {
                tile.IsHazardous = false;
                tile.MoveCost = 1;
            }
            else
            {
                tile.MoveCost = 1;
            }

            s.Grid.ApplyVisualToTile(tile);
        }

        s.Log($"[Terraform] Reshaped {Radius}-tile radius at {center} to {newTerrain} ({element}).");

        // Push enemies outward then damage them
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
                continue;

            int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
            if (dist > Radius)
                continue;

            // Push to edge: push (Radius - dist + 1) tiles away
            int pushTiles = Radius - dist + 1;
            int pushed = 0;

            for (int i = 0; i < pushTiles; i++)
            {
                var current = unit.CurrentTile.Axial;
                TileData bestTile = null;
                int bestDist = -1;

                foreach (var neighbor in s.Grid.GetNeighbors(current))
                {
                    var td = s.Grid.GetTile(neighbor);
                    if (td == null || !td.CanEnter(unit))
                        continue;
                    int distFromCenter = s.Grid.Distance(center, neighbor);
                    if (distFromCenter > bestDist)
                    {
                        bestDist = distFromCenter;
                        bestTile = td;
                    }
                }

                if (bestTile != null)
                {
                    unit.CurrentTile.ClearOccupant(unit);
                    unit.PlaceOnTile(bestTile);
                    pushed++;
                }
                else
                    break;
            }

            // Damage
            unit.ApplyDamage(Damage);
            s.Log($"[Terraform] {unit.Name} pushed {pushed} tile(s) to edge, takes {Damage} damage.");
        }
    }
}

/// <summary>Elementalist legendary capstone. Grants the caster immediate armor and optional bonus speed, hooks up a movement-trail callback that random-imbues every tile the caster vacates, and registers an <see cref="AvatarAuraEffect"/> persistent zone for the duration. Cleanup happens at end of turn after the aura expires.</summary>
public sealed class AvatarTransformEffect : EffectBase
{
    public int Turns;
    public int BonusDamage;
    public int Armor;
    public int BonusSpeed;

    public AvatarTransformEffect(int turns = 3, int bonusDamage = 3,
        int armor = 7, int bonusSpeed = 0)
    {
        Turns = turns;
        BonusDamage = bonusDamage;
        Armor = armor;
        BonusSpeed = bonusSpeed;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit == null)
            return;

        // Apply immediate armor
        casterUnit.Stats.Armor += Armor;
        casterUnit.RefreshHealthBar();

        // Apply bonus speed
        if (BonusSpeed > 0)
        {
            casterUnit.Stats.BaseSpeed += BonusSpeed;
            casterUnit.Stats.MovePoints += BonusSpeed;
        }

        // Apply imbue path for movement trails
        s.OnTurnEndCleanups ??= new List<Action>();

        Action<TileData> onLeave = null;
        var rng = new Random();
        TileElementType[] elements =
        {
            TileElementType.Fire, TileElementType.Frost,
            TileElementType.Lightning, TileElementType.Earth
        };

        onLeave = (leftTile) =>
        {
            if (leftTile == null || s?.Grid == null)
                return;
            leftTile.ElementType = elements[rng.Next(elements.Length)];
            leftTile.ElementStrength = 1.0f;
            leftTile.TileView?.SetElement(leftTile.ElementType);
            s.Log($"[Avatar] Trail imbued {leftTile.Axial} with {leftTile.ElementType}.");
        };

        casterUnit.OnTileLeft += onLeave;

        // Add the aura to persistent effects
        s.ActiveEffects ??= new List<PersistentEffect>();
        var aura = new AvatarAuraEffect(Turns, BonusDamage, caster);
        s.ActiveEffects.Add(aura);

        // Clean up movement trail callback when aura expires
        s.OnTurnEndCleanups.Add(() =>
        {
            if (aura.IsExpired)
            {
                casterUnit.OnTileLeft -= onLeave;
                if (BonusSpeed > 0)
                    casterUnit.Stats.BaseSpeed -= BonusSpeed;
                s.Log("[Avatar] Avatar aura expired.");
            }
        });

        s.Log($"[Avatar] Avatar of Elements activated for {Turns} turns. +{Armor} armor, +{BonusDamage} spell damage.");
    }
}

/// <summary>Spawns a persistent <see cref="MaelstromEffect"/> zone centered on the target tile (or caster if no target). The zone imbues, damages, and rotates pushes each turn for the duration; setting <see cref="Freezes"/> also applies the frozen status on tick.</summary>
public sealed class CreateMaelstromEffect : EffectBase
{
    public int Radius;
    public int Damage;
    public int Turns;
    public bool Freezes;

    public CreateMaelstromEffect(int radius = 3, int damage = 2,
        int turns = 3, bool freezes = false)
    {
        Radius = radius;
        Damage = damage;
        Turns = turns;
        Freezes = freezes;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        // Find center from target
        Vector2I center = default;
        bool found = false;

        if (targets != null)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is Unit u && u.CurrentTile != null)
                { center = u.CurrentTile.Axial; found = true; break; }
                if (obj is TileData td)
                { center = td.Axial; found = true; break; }
                if (obj is HexTile tv)
                { center = tv.Axial; found = true; break; }
            }
        }

        if (!found)
        {
            var casterUnit = FindCasterUnit(s, caster);
            if (casterUnit?.CurrentTile != null)
            { center = casterUnit.CurrentTile.Axial; found = true; }
        }

        if (!found)
        { s.Log("[Maelstrom] No center found."); return; }

        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new MaelstromEffect(center, Radius, Damage, Turns, caster, Freezes));

        s.Log($"[Maelstrom] Created at {center}, radius {Radius}, {Turns} turns, damage {Damage}.");
    }
}

/// <summary>
/// Imbues all tiles within <see cref="Radius"/> with the caster's highest
/// attunement element, then deals <see cref="DamagePerTile"/> damage to every
/// enemy for each tile imbued. If the caster has no attunement charges, falls
/// back to Fire. When <see cref="ElementCount"/> is 2, imbues with the top two
/// attunement elements (alternating by tile distance).
/// JSON keys: "type": "worldshaper", "radius": n, "damage_per_tile": m,
/// "elements": 1 or 2.
/// </summary>
public sealed class WorldshaperEffect : EffectBase
{
    public int Radius;
    public int DamagePerTile;
    public int ElementCount; // 1 = highest only, 2 = top two

    public WorldshaperEffect(int radius = 3, int damagePerTile = 3, int elementCount = 1)
    {
        Radius = radius;
        DamagePerTile = damagePerTile;
        ElementCount = elementCount;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null)
            return;

        var attunement = casterUnit.Attunement as ElementalAttunement;
        var elements = GetTopElements(attunement, ElementCount);

        var center = casterUnit.CurrentTile.Axial;
        int imbued = 0;
        int tileIndex = 0;

        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) > Radius)
                continue;
            var tile = kvp.Value;
            if (tile == null)
                continue;

            // Cycle through the chosen elements when ElementCount > 1
            var element = elements[tileIndex % elements.Count];
            tileIndex++;

            TileElementType tileElement = MapToTileElement(element);
            tile.ElementType = tileElement;
            tile.ElementStrength = 1.0f;
            if (tileElement == TileElementType.Fire)
                tile.IsHazardous = true;
            tile.TileView?.SetElement(tileElement);
            imbued++;
        }

        s.Log($"[Worldshaper] Imbued {imbued} tiles within {Radius} " +
              $"with {string.Join(", ", elements)}.");

        if (DamagePerTile <= 0 || imbued == 0)
            return;

        int totalDmg = imbued * DamagePerTile;
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (unit.TeamId == casterUnit.TeamId)
                continue;
            if (s.Grid.Distance(center, unit.CurrentTile.Axial) > Radius)
                continue;

            unit.ApplyDamage(totalDmg);
            s.Log($"[Worldshaper] {unit.Name} takes {totalDmg} damage ({imbued} tiles × {DamagePerTile}).");
        }
    }

    private static List<ElementTag> GetTopElements(ElementalAttunement attunement, int count)
    {
        // Default to Fire if no attunement available
        if (attunement == null)
            return new List<ElementTag> { ElementTag.Fire };

        // Sort all four elements by charge count descending
        var sorted = new List<(ElementTag element, int charges)>
        {
            (ElementTag.Fire,  attunement.Charges[ElementTag.Fire]),
            (ElementTag.Ice,   attunement.Charges[ElementTag.Ice]),
            (ElementTag.Storm, attunement.Charges[ElementTag.Storm]),
            (ElementTag.Earth, attunement.Charges[ElementTag.Earth]),
        };
        sorted.Sort((a, b) => b.charges.CompareTo(a.charges));

        var result = new List<ElementTag>();
        for (int i = 0; i < Math.Min(count, sorted.Count); i++)
        {
            // Skip elements with 0 charges when picking second element
            if (i > 0 && sorted[i].charges == 0)
                break;
            result.Add(sorted[i].element);
        }

        // Always return at least one element
        if (result.Count == 0)
            result.Add(ElementTag.Fire);

        return result;
    }

    private static TileElementType MapToTileElement(ElementTag element) => element switch
    {
        ElementTag.Fire => TileElementType.Fire,
        ElementTag.Ice => TileElementType.Frost,
        ElementTag.Storm => TileElementType.Lightning,
        ElementTag.Earth => TileElementType.Earth,
        _ => TileElementType.None
    };
}

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
        if (s?.Grid == null)
            return;

        Unit ownerUnit = null;
        if (Owner == s.PlayerA)
            ownerUnit = s.PlayerUnit;
        else if (Owner == s.PlayerB)
            ownerUnit = s.EnemyUnit;

        // Get current rotation direction
        var rotDir = HexDirs[_rotationStep % 6];

        // Imbue all tiles in radius with storm
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(Center, kvp.Key) > Radius)
                continue;
            var tile = kvp.Value;
            if (tile == null)
                continue;
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
            if (ownerUnit != null && unit.TeamId == ownerUnit.TeamId)
                continue;

            int dist = s.Grid.Distance(Center, unit.CurrentTile.Axial);
            if (dist > Radius)
                continue;

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
    public override void OnSpellCast(GameState s, Unit casterUnit, TargetSet targets)
    {
        if (s?.Grid == null || targets == null)
            return;

        // Random element imbue on target tile
        var element = Elements[_rng.Next(Elements.Length)];

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td)
                tile = td;
            else if (obj is HexTile tv)
                tile = s.Grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null)
                tile = u.CurrentTile;

            if (tile != null)
            {
                tile.ElementType = element;
                tile.ElementStrength = 1.0f;
                if (element == TileElementType.Fire)
                    tile.IsHazardous = true;
                tile.TileView?.SetElement(element);
                s.Log($"[Avatar] Imbued {tile.Axial} with {element}.");
            }
        }

        s.Log($"[Avatar] Spell deals +{BonusDamage} bonus damage.");
    }
}


/// <summary>
/// Elemental Sight / Grand Confluence (Worldshaper tiers 3-4): reads the land —
/// gains one attunement charge per distinct element imbued on tiles within
/// radius of the caster. Bypasses cast-driven opposition reduction (it is a
/// reading, not a casting) via ElementalAttunement.GainCharge.
/// JSON: { "type": "attunement_per_nearby_element", "radius": 3 }
/// </summary>
public sealed class AttunementPerNearbyElementEffect : EffectBase
{
	public int Radius;
	public AttunementPerNearbyElementEffect(int radius) { Radius = radius; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null || s.Grid == null)
			return;
		if (casterUnit.Attunement is not ElementalAttunement attunement)
		{
			s.Log("[ElementalSight] Caster has no elemental attunement.");
			return;
		}

		var center = casterUnit.CurrentTile.Axial;
		var found = new HashSet<ElementTag>();
		foreach (var kvp in s.Grid.Tiles)
		{
			if (s.Grid.Distance(center, kvp.Key) > Radius)
				continue;
			ElementTag? tag = kvp.Value.ElementType switch
			{
				TileElementType.Fire => ElementTag.Fire,
				TileElementType.Frost => ElementTag.Ice,
				TileElementType.Lightning => ElementTag.Storm,
				TileElementType.Earth => ElementTag.Earth,
				_ => null
			};
			if (tag.HasValue)
				found.Add(tag.Value);
		}

		foreach (var tag in found)
			attunement.GainCharge(tag, 1);

		s.Log($"[ElementalSight] {found.Count} element(s) read from the land — +1 charge each.");
	}
}
