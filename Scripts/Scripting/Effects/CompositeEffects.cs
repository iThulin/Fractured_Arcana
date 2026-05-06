using System;
using System.Collections.Generic;
using Godot;

// ============================================================
// Composite effects. With Sequence, Conditional, and ForEach, 
// every card can be expressed as a tree of primitives.
// ============================================================

public sealed class SequenceEffect : EffectBase
{
    public IEffect[] Steps;

    public SequenceEffect(params IEffect[] steps) { Steps = steps; }

    public override IEnumerable<IEffect> Children => Steps;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Targets = targets, Snapshot = snap };
        ResolveWithResult(ctx);
    }

    public override EffectResult ResolveWithResult(PredicateContext ctx)
    {
        EffectResult last = new();
        foreach (var step in Steps)
        {
            if (step is EffectBase eb)
            {
                last = eb.ResolveWithResult(ctx);
                ctx.LastResult = last;
            }
            else
            {
                // Fallback for any IEffect that doesn't use EffectBase
                step.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
                last = new EffectResult();
            }
        }
        return last;
    }
}

// Branch on a predicate. Then-branch required, else-branch optional.
public sealed class ConditionalEffect : EffectBase
{
    public IPredicate If;
    public IEffect Then;
    public IEffect Else; // may be null

    public ConditionalEffect(IPredicate pred, IEffect thenEff, IEffect elseEff = null)
    {
        If = pred; Then = thenEff; Else = elseEff;
    }

    public override IEnumerable<IEffect> Children
    {
        get
        {
            yield return Then;
            if (Else != null) yield return Else;
        }
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Targets = targets, Snapshot = snap };
        ResolveWithResult(ctx);
    }

    public override EffectResult ResolveWithResult(PredicateContext ctx)
    {
        bool branch = If.Evaluate(ctx);
        s_LogBranch(ctx, branch);

        var chosen = branch ? Then : Else;
        if (chosen == null) return new EffectResult();

        if (chosen is EffectBase eb) return eb.ResolveWithResult(ctx);
        chosen.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
        return new EffectResult();
    }

    private static void s_LogBranch(PredicateContext ctx, bool taken)
    {
        ctx.Game?.Log($"[Conditional] predicate={taken} -> {(taken ? "THEN" : "ELSE")}");
    }
}

// Apply an effect once per target in the current target set.
public sealed class ForEachTargetEffect : EffectBase
{
    public IEffect PerTarget;

    public ForEachTargetEffect(IEffect per) { PerTarget = per; }

    public override IEnumerable<IEffect> Children { get { yield return PerTarget; } }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Snapshot = snap };
        foreach (var item in targets.Items)
        {
            // Wrap single target so nested effects see exactly one target.
            var single = new TargetSet();
            single.Items.Add(item);
            ctx.Targets = single;
            ctx.Caster = caster;

            if (PerTarget is EffectBase eb) eb.ResolveWithResult(ctx);
            else PerTarget.Resolve(s, caster, single, snap);
        }
    }
}

// Push targets away from caster, dealing damage per tile pushed. Useful for knockback or pull effects (just reverse the direction).
public sealed class PushDamageEffect : EffectBase
{
    public int PushTiles;
    public int DamagePerTile;

    public PushDamageEffect(int pushTiles, int damagePerTile)
    {
        PushTiles = pushTiles;
        DamagePerTile = damagePerTile;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null || s?.Grid == null) return;
        if (targets == null) return;

        var casterPos = casterUnit.CurrentTile.Axial;

        foreach (var obj in targets.Items)
        {
            var victim = ResolveTargetUnit(s, obj);
            if (victim == null || victim.CurrentTile == null) continue;

            int pushed = 0;
            for (int i = 0; i < PushTiles; i++)
            {
                var current = victim.CurrentTile.Axial;
                TileData bestTile = null;
                int bestDist = -1;

                foreach (var neighbor in s.Grid.GetNeighbors(current))
                {
                    var td = s.Grid.GetTile(neighbor);
                    if (td == null || !td.CanEnter(victim)) continue;

                    int distFromCaster = s.Grid.Distance(casterPos, neighbor);
                    if (distFromCaster > bestDist)
                    {
                        bestDist = distFromCaster;
                        bestTile = td;
                    }
                }

                if (bestTile != null)
                {
                    victim.CurrentTile.ClearOccupant(victim);
                    victim.PlaceOnTile(bestTile);
                    pushed++;
                }
                else
                {
                    s.Log($"[PushDamage] {victim.Name} hit obstacle after {pushed} tile(s).");
                    break;
                }
            }

            int totalDmg = pushed * DamagePerTile;
            if (totalDmg > 0)
            {
                victim.ApplyDamage(totalDmg);
                s.Log($"[PushDamage] {victim.Name} pushed {pushed} tile(s), takes {totalDmg} damage ({DamagePerTile}/tile).");
            }
            else
            {
                s.Log($"[PushDamage] {victim.Name} couldn't be pushed.");
            }
        }
    }
}

// ============================================================
// Change the target set for a child effect. This is how we do chaining effects like "damage, then retarget nearest enemy and damage again".
// Example JSON structure:
//   {
//     "type": "sequence",
//     "steps": [
//       { "type": "move", "tiles": 3 },          ← targets self
//       { "type": "retarget",                    ← switches to AoE
//         "targeting": { "type": "aoe", "radius": 1, "enemies_only": true },
//         "do": { "type": "move", "tiles": 1 }   ← pushes nearby enemies
//       }
//     ]
//   }
// ============================================================
public sealed class RetargetEffect : EffectBase
{
	public ITargetSelector Targeter;
	public IEffect Child;

	public RetargetEffect(ITargetSelector targeter, IEffect child)
	{
		Targeter = targeter;
		Child = child;
	}

	public override IEnumerable<IEffect> Children
	{
		get { yield return Child; }
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var ctx = new PredicateContext
		{
			Game = s,
			Caster = caster,
			Targets = targets,
			Snapshot = snap
		};
		ResolveWithResult(ctx);
	}

	public override EffectResult ResolveWithResult(PredicateContext ctx)
	{
		var originalTargets = ctx.Targets;

		// Store previous targets so chain targeters can use them
		// as the origin point ("nearest to whoever was just hit")
		ctx.Game.RetargetOrigin = originalTargets;

		TargetSet newTargets;
		if (Targeter != null && Targeter.Select(ctx.Game, ctx.Caster, out newTargets))
		{
			ctx.Game?.Log($"[Retarget] Switched to {newTargets.Items.Count} new target(s).");
			ctx.Targets = newTargets;
		}
		else
		{
			ctx.Game?.Log("[Retarget] No valid targets found. Skipping.");
			ctx.Game.RetargetOrigin = null;
			return new EffectResult();
		}

		// Execute child effect with new targets
		EffectResult result;
		if (Child is EffectBase eb)
			result = eb.ResolveWithResult(ctx);
		else
		{
			Child.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
			result = new EffectResult();
		}

		// Restore
		ctx.Targets = originalTargets;
		ctx.Game.RetargetOrigin = null;

		return result;
	}
}

public sealed class ImbuePathEffect : EffectBase
{
    public string Element;
    public int MoveTiles;
    public int ArmorPerTile;

    public ImbuePathEffect(string element, int moveTiles, int armorPerTile = 0)
    {
        Element = element;
        MoveTiles = moveTiles;
        ArmorPerTile = armorPerTile;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit == null || s?.Grid == null) return;

        TileElementType elementType = Element.ToLowerInvariant() switch
        {
            "fire"  => TileElementType.Fire,
            "ice"   => TileElementType.Frost,
            "frost" => TileElementType.Frost,
            "storm" => TileElementType.Lightning,
            "stone" => TileElementType.Earth,
            _       => TileElementType.None
        };

        int tilesImbued = 0;

        // Subscribe: imbue every tile the unit leaves
        Action<TileData> onLeave = null;
        onLeave = (leftTile) =>
        {
            if (leftTile == null) return;
            leftTile.ElementType = elementType;
            leftTile.ElementStrength = 1.0f;
            if (Element.ToLowerInvariant() == "fire")
                leftTile.IsHazardous = true;
            leftTile.TileView?.SetElement(elementType);
            tilesImbued++;
            s.Log($"[ImbuePath] {leftTile.Axial} imbued with {Element}.");
        };

        casterUnit.OnTileLeft += onLeave;

        // Grant the movement
        casterUnit.Stats.MovePoints += MoveTiles;
        s.Log($"[ImbuePath] {casterUnit.Name} gains {MoveTiles} move. Tiles left behind will be imbued with {Element}.");

        // Also imbue the starting tile
        if (casterUnit.CurrentTile != null)
        {
            casterUnit.CurrentTile.ElementType = elementType;
            casterUnit.CurrentTile.ElementStrength = 1.0f;
            casterUnit.CurrentTile.TileView?.SetElement(elementType);
            tilesImbued++;
        }

        // The callback stays active until turn ends.
        // We need to clean it up. Store a cleanup action on GameState.
        s.OnTurnEndCleanups ??= new List<Action>();
        s.OnTurnEndCleanups.Add(() =>
        {
            casterUnit.OnTileLeft -= onLeave;

            // Grant armor based on tiles imbued
            if (ArmorPerTile > 0 && tilesImbued > 0)
            {
                int totalArmor = tilesImbued * ArmorPerTile;
                casterUnit.Stats.Armor += totalArmor;
                casterUnit.RefreshHealthBar();
                s.Log($"[ImbuePath] {casterUnit.Name} gains {totalArmor} armor ({tilesImbued} tiles x {ArmorPerTile}).");
            }
        });
    }
}

public sealed class PrimordialSurgeEffect : EffectBase
{
    public int Radius;
    public int Damage;
    private static readonly TileElementType[] Elements = 
    {
        TileElementType.Fire, TileElementType.Frost, 
        TileElementType.Lightning, TileElementType.Earth
    };
    private Random _rng = new();

    public PrimordialSurgeEffect(int radius = 4, int damage = 4) { Radius = radius; Damage = damage; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null) return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return;

        var center = casterUnit.CurrentTile.Axial;

        // Imbue tiles within radius
        int imbued = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null) continue;
            if (s.Grid.Distance(center, kvp.Key) > Radius) continue;

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
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId) continue;

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
        if (s?.Grid == null) return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return;

        var center = casterUnit.CurrentTile.Axial;

        // Clear imbued tiles within radius
        int destroyed = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null) continue;
            if (s.Grid.Distance(center, kvp.Key) > Radius) continue;
            if (tile.ElementType == TileElementType.None) continue;

            tile.ElementType = TileElementType.None;
            tile.ElementStrength = 0f;
            tile.IsHazardous = false;
            tile.TileView?.SetElement(TileElementType.None);
            destroyed++;
        }

        s.Log($"[Cataclysm] Destroyed {destroyed} imbued tile(s) within {Radius} range.");

        if (destroyed == 0) return;

        // Damage enemies in radius
        int totalDmg = destroyed * DamagePerTile;
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId) continue;
            if (s.Grid.Distance(center, unit.CurrentTile.Axial) > Radius) continue;

            unit.ApplyDamage(totalDmg);
            s.Log($"[Cataclysm] {unit.Name} takes {totalDmg} damage ({destroyed} x {DamagePerTile}).");
        }

        int cardsToDraw = destroyed / TilesPerDraw;
        if (cardsToDraw > 0)
        {
            s.Draw(caster, cardsToDraw);
            s.Log($"[Cataclysm] Draw {cardsToDraw} card(s).");
        }
    }
}

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
        if (s?.Grid == null) return;
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
            if (tile == null || tile.ElementType == TileElementType.None) continue;
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
            if (unit == null || !unit.Stats.IsAlive) continue;

            int dmg = totalDmg;
            if (HalfToAllies && casterUnit != null && unit.TeamId == casterUnit.TeamId)
                dmg = totalDmg / 2;

            unit.ApplyDamage(dmg);
            s.Log($"[Ragnarok] {unit.Name} takes {dmg} damage.");
        }
    }
}

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
        if (s?.Grid == null) return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return;

        var center = casterUnit.CurrentTile.Axial;

        // Imbue all tiles within radius with random elements
        int imbued = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile == null) continue;
            if (s.Grid.Distance(center, kvp.Key) > Radius) continue;

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

public sealed class ImbueAreaEffect : EffectBase
{
    public string Element;
    public int Radius;

    public ImbueAreaEffect(string element, int radius)
    {
        Element = element;
        Radius = radius;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null) return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return;

        var center = casterUnit.CurrentTile.Axial;

        TileElementType elementType = Element.ToLowerInvariant() switch
        {
            "fire"  => TileElementType.Fire,
            "ice"   => TileElementType.Frost,
            "storm" => TileElementType.Lightning,
            "stone" => TileElementType.Earth,
            _ => TileElementType.None
        };

        int imbued = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) > Radius) continue;
            var tile = kvp.Value;
            if (tile == null) continue;

            tile.ElementType = elementType;
            tile.ElementStrength = 1.0f;
            if (elementType == TileElementType.Fire)
                tile.IsHazardous = true;
            tile.TileView?.SetElement(elementType);
            imbued++;
        }

        s.Log($"[ImbueArea] Imbued {imbued} tiles within {Radius} with {Element}.");
    }
}

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
        if (s?.Grid == null) return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return;

        // Center on the target, not the caster
        Vector2I center = casterUnit.CurrentTile.Axial;
        if (targets != null)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is Unit u && u.CurrentTile != null) { center = u.CurrentTile.Axial; break; }
                if (obj is TileData td) { center = td.Axial; break; }
                if (obj is HexTile tv) { center = tv.Axial; break; }
            }
        }

        // Find and destroy all stone tiles in radius
        int destroyed = 0;
        var destroyedTiles = new List<TileData>();

        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) > Radius) continue;
            var tile = kvp.Value;
            if (tile == null) continue;

            bool isStone = tile.TerrainType == TileTerrainType.Stone ||
                           tile.ElementType == TileElementType.Earth ||
                            (tile.IsBlocked && 
                            (tile.ObstacleKind == "rock" || 
                            tile.ObstacleKind == "stone" ||
                            tile.ObstacleKind == "boulder" ||
                            tile.ObstacleKind == "stone_pillar"));

            if (!isStone) continue;

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

        if (destroyed == 0) return;

        // For each destroyed tile, deal damage to nearest enemy
        int totalDmg = destroyed * DamagePerTile;

        // Find nearest enemy to center
        Unit nearest = null;
        int nearestDist = int.MaxValue;
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId) continue;
            int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
            if (dist < nearestDist) { nearest = unit; nearestDist = dist; }
        }

        if (nearest != null)
        {
            nearest.ApplyDamage(totalDmg);
            s.Log($"[TectonicShatter] {nearest.Name} takes {totalDmg} damage ({destroyed} x {DamagePerTile}).");
        }
    }
}

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
        if (s?.Grid == null) return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return;

        // Find center from targets or use caster
        Vector2I center = casterUnit.CurrentTile.Axial;
        if (targets != null)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is Unit u && u.CurrentTile != null) { center = u.CurrentTile.Axial; break; }
                if (obj is TileData td) { center = td.Axial; break; }
                if (obj is HexTile tv) { center = tv.Axial; break; }
            }
        }

        // Determine reshape element from highest attunement
        var element = casterUnit.HighestAttunementElement;

        TileTerrainType newTerrain = element switch
        {
            ElementTag.Fire  => TileTerrainType.Lava,
            ElementTag.Ice   => TileTerrainType.Ice,
            ElementTag.Storm => TileTerrainType.Stone,
            ElementTag.Earth => TileTerrainType.Stone,
            _ => TileTerrainType.Stone
        };

        TileElementType newElement = element switch
        {
            ElementTag.Fire  => TileElementType.Fire,
            ElementTag.Ice   => TileElementType.Frost,
            ElementTag.Storm => TileElementType.Lightning,
            ElementTag.Earth => TileElementType.Earth,
            _ => TileElementType.Earth
        };

        // Reshape all tiles in radius
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) > Radius) continue;
            var tile = kvp.Value;
            if (tile == null) continue;

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
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId) continue;

            int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
            if (dist > Radius) continue;

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
                    if (td == null || !td.CanEnter(unit)) continue;
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
                else break;
            }

            // Damage
            unit.ApplyDamage(Damage);
            s.Log($"[Terraform] {unit.Name} pushed {pushed} tile(s) to edge, takes {Damage} damage.");
        }
    }
}

public sealed class ConsumeElementTileEffect : EffectBase
{
    public string Element;
    public int Radius;
    public int Damage;

    public ConsumeElementTileEffect(string element, int radius, int damage)
    {
        Element = element;
        Radius = radius;
        Damage = damage;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null || targets == null) return;
        var casterUnit = FindCasterUnit(s, caster);

        TileElementType needed = Element.ToLowerInvariant() switch
        {
            "fire"  => TileElementType.Fire,
            "ice"   => TileElementType.Frost,
            "storm" => TileElementType.Lightning,
            "stone" => TileElementType.Earth,
            _ => TileElementType.None
        };

        // Find the target tile
        TileData targetTile = null;
        foreach (var obj in targets.Items)
        {
            if (obj is TileData td) { targetTile = td; break; }
            else if (obj is HexTile tv) { targetTile = s.Grid.GetTile(tv.Axial); break; }
            else if (obj is Unit u && u.CurrentTile != null) { targetTile = u.CurrentTile; break; }
        }

        if (targetTile == null)
        {
            s.Log($"[ConsumeTile] No target tile found.");
            return;
        }

        if (targetTile.ElementType != needed)
        {
            s.Log($"[ConsumeTile] Target tile is not {Element}. Cannot consume.");
            return;
        }

        // Consume the tile
        var center = targetTile.Axial;
        targetTile.ElementType = TileElementType.None;
        targetTile.ElementStrength = 0f;
        targetTile.IsHazardous = false;
        targetTile.TileView?.SetElement(TileElementType.None);
        s.Log($"[ConsumeTile] Consumed {Element} tile at {center}.");

        // Deal damage to enemies within radius
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId) continue;
            if (s.Grid.Distance(center, unit.CurrentTile.Axial) > Radius) continue;

            unit.ApplyDamage(Damage);
            s.Log($"[ConsumeTile] {unit.Name} takes {Damage} damage from {Element} explosion.");
        }
    }
}

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
        if (casterUnit == null) return;

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
            if (leftTile == null || s?.Grid == null) return;
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

        if (!found) { s.Log("[Maelstrom] No center found."); return; }

        s.ActiveEffects ??= new List<PersistentEffect>();
        s.ActiveEffects.Add(new MaelstromEffect(center, Radius, Damage, Turns, caster, Freezes));

        s.Log($"[Maelstrom] Created at {center}, radius {Radius}, {Turns} turns, damage {Damage}.");
    }
}


// Do nothing. Useful as a placeholder in JSON while you're sketching.
public sealed class EmptyEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) { }
}
