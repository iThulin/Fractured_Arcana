using System;
using System.Collections.Generic;

// ============================================================
// Base class for all effects. This is the ONLY EffectBase in the
// project — the scripting system extends it rather than replacing it.
//
// Leaf effects (DealDamage, Move, etc.) inherit from this and
// override Resolve. They may ALSO override ResolveWithResult if
// they want to report data back to downstream conditionals
// (e.g. DealDamageEffect reports WasLethal).
// ============================================================

public abstract class EffectBase : IEffect
{
	protected string[] _tags = Array.Empty<string>();
	public string[] Tags => _tags;

	public IEffect WithTag(string t)
	{
		_tags = new[] { t };
		return this;
	}

	// Default: leaf effect, no children. Composite effects override.
	public virtual IEnumerable<IEffect> Children => Array.Empty<IEffect>();

	// Old entry point — kept for compatibility with your stack code
	// (RulesManager still calls this through the IEffect interface).
	public abstract void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap);

	// New entry point. Default wraps the old Resolve so legacy
	// effects keep working without needing to override.
	// Effects that want to report data (lethal damage, targets hit,
	// spawned entities) should override this.
	public virtual EffectResult ResolveWithResult(PredicateContext ctx)
	{
		Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
		return new EffectResult();
	}

	// ── Shared helper: find the caster's Unit in the game ───────────
	protected static Unit FindCasterUnit(GameState s, Entity caster)
	{
		if (s == null) return null;
		// PlayerA maps to PlayerUnit
		if (caster == s.PlayerA) return s.PlayerUnit;
		if (caster == s.PlayerB) return s.EnemyUnit;
		// Fallback: search UnitsInPlay by name
		foreach (var u in s.UnitsInPlay)
			if (u != null && u.Name == caster.Name) return u;
		return s.PlayerUnit; // last resort
	}

	// ── Shared helper: resolve any target type to a Unit ────────────
	protected static Unit ResolveTargetUnit(GameState s, object obj)
	{
		if (obj is Unit u) return u;
		if (obj is TileData td) return td.Occupant;
		if (obj is HexTile tv)
		{
			var tileData = s?.Grid?.GetTile(tv.Axial);
			return tileData?.Occupant;
		}
		return null;
	}
}

// ============================================================
// Leaf effects
// ============================================================

public sealed class DealDamageEffect : EffectBase
{
	public int Amount;
	public DealDamageEffect(int a) { Amount = a; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		int hit = 0;
		if (targets == null) { s?.Log($"[DealDamage] No targets."); return; }

		// Check if caster has persistent buffs
		var casterUnit = FindCasterUnit(s, caster);
		int bonus = 0;
		if (casterUnit != null && casterUnit.HasStatus("empowered"))
			bonus = 3;

		var avatarAura = s.GetActiveEffect<AvatarAuraEffect>(caster);
		if (avatarAura != null)
			bonus += avatarAura.BonusDamage;

		s.Log($"targets.Items.Count={targets.Items.Count}");

		foreach (var obj in targets.Items)
		{
			s.Log($"  item: {(obj == null ? "null" : obj.GetType().Name)}");

			if (obj is Unit u)
				s.Log($"    -> Unit: {u.Name} HP {u.Stats.Health}/{u.Stats.MaxHealth}");

			if (obj is TileData td)
				s.Log($"    -> TileData: {td.Axial} occupant={(td.Occupant != null ? td.Occupant.Name : "null")}");

			if (obj is HexTile tile)
				s.Log($"    -> TileView: {tile.Axial}");
		}

		foreach (var obj in targets.Items)
		{
			if (obj is Unit u)
			{
				u.ApplyDamage(Amount + bonus);
				s.Log($"HIT unit {u.Name}");
				hit++;
			}
			else if (obj is TileData td && td.Occupant != null)
			{
				td.Occupant.ApplyDamage(Amount + bonus);
				s.Log($"HIT tile occupant {td.Occupant.Name} on {td.Axial}");
				hit++;
			}
			else if (obj is HexTile tileView)
			{
				var tileData = ResolveTileDataFromView(s, tileView);
				if (tileData != null && tileData.Occupant != null)
				{
					tileData.Occupant.ApplyDamage(Amount + bonus);
					s.Log($"HIT tile occupant {tileData.Occupant.Name} on {tileData.Axial}");
					hit++;
				}
			}
		}

		s.Log($"Resolve: Deal {Amount} damage to {hit} target(s).");

		// After the main damage loop, add chain bounces:
		int chainCount = 0;
		if (casterUnit != null && casterUnit.HasStatus("chaining"))
		{
			// Get the chaining level (1 or 2)
			chainCount = casterUnit.Stats.StatusEffects.ContainsKey("chaining")
				? Math.Min(casterUnit.Stats.StatusEffects["chaining"], 2)
				: 1;
		}

		if (chainCount > 0 && hit > 0)
		{
			if (s?.Grid == null) { s?.Log("[Chain] No grid for chain bounce."); return; }

			// Build exclusion set from already-hit units
			var alreadyHit = new HashSet<Unit>();
			foreach (var obj in targets.Items)
			{
				var victim = ResolveTargetUnit(s, obj);
				if (victim != null) alreadyHit.Add(victim);
			}
			alreadyHit.Add(casterUnit);

			// Find the last hit unit as origin for chain
			Unit chainOrigin = null;
			foreach (var obj in targets.Items)
			{
				var v = ResolveTargetUnit(s, obj);
				if (v != null) { chainOrigin = v; break; }
			}

			for (int chain = 0; chain < chainCount; chain++)
			{
				if (chainOrigin?.CurrentTile == null) break;

				// Find nearest enemy to chainOrigin not already hit
				Unit nearest = null;
				int nearestDist = int.MaxValue;
				foreach (var unit in s.UnitsInPlay)
				{
					if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
					if (casterUnit != null && unit.TeamId == casterUnit.TeamId) continue;
					if (alreadyHit.Contains(unit)) continue;

					int dist = s.Grid.Distance(chainOrigin.CurrentTile.Axial, unit.CurrentTile.Axial);
					if (dist <= 3 && dist < nearestDist)
					{
						nearestDist = dist;
						nearest = unit;
					}
				}

				if (nearest != null)
				{
					nearest.ApplyDamage(Amount + bonus);
					alreadyHit.Add(nearest);
					chainOrigin = nearest;
					s.Log($"[Chain] Bounced to {nearest.Name} for {Amount + bonus} damage.");
				}
				else break;
			}

			// Consume chaining after use
			casterUnit.Stats.StatusEffects.Remove("chaining");
			s.Log($"[Chain] Chaining consumed.");
		}
	}

	public override EffectResult ResolveWithResult(PredicateContext ctx)
	{
		int totalDamage = 0;
		bool lethal = false;
		int hit = 0;

		if (ctx.Targets == null) return new EffectResult();

		// Check empowered
		var casterUnit = FindCasterUnit(ctx.Game, ctx.Caster);
		int bonus = 0;
		if (casterUnit != null && casterUnit.HasStatus("empowered"))
			bonus = 3;

		foreach (var obj in ctx.Targets.Items)
		{
			Unit victim = null;
			if (obj is Unit u) victim = u;
			else if (obj is TileData td && td.Occupant != null) victim = td.Occupant;
			else if (obj is HexTile tileView)
			{
				var tileData = ResolveTileDataFromView(ctx.Game, tileView);
				if (tileData != null) victim = tileData.Occupant;
			}

			if (victim != null)
			{
				int hpBefore = victim.Stats.Health;
				victim.ApplyDamage(Amount + bonus);
				totalDamage += Amount + bonus;
				hit++;
				if (hpBefore > 0 && victim.Stats.Health <= 0) lethal = true;
			}
		}

		if (bonus > 0)
			ctx.Game?.Log($"Resolve: Deal {Amount}+{bonus} (empowered) damage to {hit} target(s). lethal={lethal}");
		else
			ctx.Game?.Log($"Resolve: Deal {Amount} damage to {hit} target(s). lethal={lethal}");

		return new EffectResult { DamageDealt = totalDamage, WasLethal = lethal, TargetsHit = hit };
	}

	private TileData ResolveTileDataFromView(GameState s, HexTile tileView)
	{
		if (tileView == null) return null;
		var grid = s?.Grid;
		if (grid == null)
		{
			s?.Log("ResolveTileDataFromView: could not find HexGridManager.");
			return null;
		}
		return grid.GetTile(tileView.Axial);
	}
}

public sealed class DashEffect : EffectBase
{
    public int Tiles;
    public DashEffect(int t) { Tiles = t; }
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);

        if (targets == null || targets.Items.Count == 0 ||
            (targets.Items.Count == 1 && targets.Items[0] is Entity))
        {
            // Self-movement — grant move points
            if (casterUnit != null)
            {
                casterUnit.Stats.MovePoints += Tiles;
                s.Log($"[Dash] {casterUnit.Name} gains {Tiles} move points (now {casterUnit.Stats.MovePoints}).");
            }
        }
        else
        {
            // Push — find the victim and try to move them away from caster
            foreach (var obj in targets.Items)
            {
                var victim = ResolveTargetUnit(s, obj);
                if (victim == null || victim.CurrentTile == null) continue;
                if (casterUnit == null || casterUnit.CurrentTile == null) continue;

                // Calculate push direction: away from caster
                var grid = s.Grid;
                if (grid == null) { s.Log("[Push] No grid."); continue; }

                var from = victim.CurrentTile.Axial;
                var casterPos = casterUnit.CurrentTile.Axial;

                // Push tile by tile away from caster
                int pushed = 0;
                for (int i = 0; i < Tiles; i++)
                {
                    var current = victim.CurrentTile.Axial;
                    var dir = current - casterPos;

                    // Normalize to one hex step — pick the neighbor furthest from caster
                    TileData bestTile = null;
                    int bestDist = -1;

                    foreach (var neighbor in grid.GetNeighborCoords(current))
                    {
                        var td = grid.GetTile(neighbor);
                        if (td == null || !td.CanEnter(victim)) continue;

                        int distFromCaster = grid.Distance(casterPos, neighbor);
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
                        // Hit a wall or edge — could add collision damage here
                        s.Log($"[Push] {victim.Name} hit an obstacle after {pushed} tile(s).");
                        break;
                    }
                }
                s.Log($"[Push] {victim.Name} pushed {pushed} tile(s) away.");
            }
        }
    }
}

// ── Push Effect ───────────────────────────────────────────
public sealed class PushEffect : EffectBase
{
    public int Tiles;
    public int CollisionDamage;

    public PushEffect(int tiles, int collisionDamage = 0)
    {
        Tiles = tiles;
        CollisionDamage = collisionDamage;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null || s?.Grid == null || targets == null) return;

        var casterPos = casterUnit.CurrentTile.Axial;

        foreach (var obj in targets.Items)
        {
            var victim = ResolveTargetUnit(s, obj);
            if (victim == null || victim.CurrentTile == null) continue;

            int pushed = 0;
            bool collided = false;

            for (int i = 0; i < Tiles; i++)
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
                    collided = true;
                    break;
                }
            }

            if (collided && CollisionDamage > 0)
            {
                victim.ApplyDamage(CollisionDamage);
                s.Log($"[Push] {victim.Name} pushed {pushed} tile(s), collided for {CollisionDamage} damage!");
            }
            else
            {
                s.Log($"[Push] {victim.Name} pushed {pushed} tile(s).");
            }
        }
    }
}

// ── Shield / Armor Effect ───────────────────────────────────────

public sealed class GiveShieldEffect : EffectBase
{
	public int Shield;
	public GiveShieldEffect(int v) { Shield = v; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit != null)
		{
			// Shield is a temporary buffer that goes away at end of turn.
			casterUnit.Stats.Shield += Shield;
			casterUnit.RefreshHealthBar();
			s.Log($"[GiveShield] {casterUnit.Name} gains {Shield} shield (now {casterUnit.Stats.Shield}).");
		}
		else
		{
			s.Log($"[GiveShield] Gain {Shield} shield. (caster unit not found)");
		}
	}
}

public sealed class GiveArmorEffect : EffectBase
{
	public int Armor;
	public GiveArmorEffect(int v) { Armor = v; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit != null)
		{
			// Apply as armor (persistent defense).
			casterUnit.Stats.Armor += Armor;
			casterUnit.RefreshHealthBar();
			s.Log($"[GiveArmor] {casterUnit.Name} gains {Armor} armor (now {casterUnit.Stats.Armor}).");
		}
		else
		{
			s.Log($"[GiveArmor] Gain {Armor} armor. (caster unit not found)");
		}
	}
}

public sealed class GiveTargetArmorEffect : EffectBase
{
    public int Amount;
    public GiveTargetArmorEffect(int a) { Amount = a; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets == null) return;
        var casterUnit = FindCasterUnit(s, caster);

        foreach (var obj in targets.Items)
        {
            var unit = ResolveTargetUnit(s, obj);
            if (unit == null) continue;

            // Only buff allies
            if (casterUnit != null && unit.TeamId != casterUnit.TeamId) continue;

            unit.Stats.Armor += Amount;
            unit.RefreshHealthBar();
            s.Log($"[GiveTargetArmor] {unit.Name} gains {Amount} armor (now {unit.Stats.Armor}).");
        }
    }
}

// ── Draw Cards Effect ───────────────────────────────────────────

public sealed class DrawCardsEffect : EffectBase
{
	public int Count;
	public DrawCardsEffect(int n) { Count = n; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.Draw(caster, Count);
		s.Log($"[Draw]: {caster.Name} Draws {Count} cards.");
	}
}

// ── Mana Gain Effect ────────────────────────────────────────────

public sealed class ManaGainEffect : EffectBase
{
	public int Amount;
	public ManaGainEffect(int a) { Amount = a; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.Mana.ContainsKey(caster))
		{
			s.Mana[caster] += Amount;
			s.Log($"[ManaGain] {caster.Name} gains {Amount} mana (now {s.Mana[caster]}).");
		}

		// Also sync to the actual Unit so the health bar updates
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit != null)
		{
			casterUnit.GainMana(Amount);
		}
	}
}

// ── Self-Damage Effect ──────────────────────────────────────────

public sealed class SelfDamageEffect : EffectBase
{
	public int Amount;
	public SelfDamageEffect(int a) { Amount = a; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit != null)
		{
			casterUnit.ApplyDamage(Amount);
			s.Log($"[SelfDamage] {casterUnit.Name} takes {Amount} damage.");
		}
	}
}

// ── Heal Effect ─────────────────────────────────────────────────

public sealed class HealEffect : EffectBase
{
	public int Amount;
	public HealEffect(int a) { Amount = a; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit != null)
		{
			int before = casterUnit.Stats.Health;
			casterUnit.Stats.Health = Math.Min(casterUnit.Stats.MaxHealth,
				casterUnit.Stats.Health + Amount);
			int healed = casterUnit.Stats.Health - before;
			casterUnit.RefreshHealthBar();
			s.Log($"[Heal] {casterUnit.Name} heals {healed} HP (now {casterUnit.Stats.Health}/{casterUnit.Stats.MaxHealth}).");
		}
	}
}

// ── Imbue Tile Effect ─────────────────────────────────────────
public sealed class ImbueTileEffect : EffectBase
{
    public string Element;
    public int BonusDamage;
    public ImbueTileEffect(string element, int bonusDamage = 0)
    {
        Element = element;
        BonusDamage = bonusDamage;
    }
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null) { s?.Log("[ImbueTile] No grid."); return; }

		TileElementType elementType = Element.ToLowerInvariant() switch
		{
			"fire"  => TileElementType.Fire,
			"ice"   => TileElementType.Frost,
			"frost" => TileElementType.Frost,
			"storm" => TileElementType.Lightning,
			"stone" => TileElementType.Earth,
			"earth" => TileElementType.Earth,
			_       => TileElementType.None
		};

		if (targets == null) return;

		foreach (var obj in targets.Items)
		{
			TileData tile = null;

			if (obj is TileData td) tile = td;
			else if (obj is HexTile tv) tile = s.Grid.GetTile(tv.Axial);
			else if (obj is Unit u && u.CurrentTile != null) tile = u.CurrentTile;

			if (tile == null) continue;

			tile.ElementType = elementType;
			tile.ElementStrength = 1.0f;

			if (elementType == TileElementType.Fire)
				tile.IsHazardous = true;

			// Use the existing visual system to update the tile
			tile.TileView?.SetElement(elementType);

			s.Log($"[ImbueTile] {tile.Axial} imbued with {Element} ({elementType}).");

			if (BonusDamage > 0 && tile.Occupant != null && tile.Occupant.TeamId != 0)
			{
				tile.Occupant.ApplyDamage(BonusDamage);
				s.Log($"[ImbueTile] {Element} deals {BonusDamage} to {tile.Occupant.Name}.");
			}
		}
	}
}

// ── Apply Status Effect ─────────────────────────────────────────

public sealed class ApplyStatusEffect : EffectBase
{
	public string StatusName; // "frozen", "slowed", "burning", etc.
	public int Duration;
	public ApplyStatusEffect(string name, int duration = 1)
	{
		StatusName = name;
		Duration = duration;
	}
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null) return;
		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim != null)
			{
				victim.ApplyStatus(StatusName, Duration);
				s.Log($"[Status] {victim.Name} is {StatusName} for {Duration} turn(s).");
			}
		}
	}
}

// ── Summon Effect ───────────────────────────────────────────────
public sealed class SummonEffect : EffectBase
{
    public string UnitKind;
    public int Count;
    public SummonEffect(string kind, int count) { UnitKind = kind; Count = count; }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s.OnSummonRequested == null)
        {
            s.Log($"[Summon] No summon handler registered. Cannot spawn {Count}x {UnitKind}.");
            return;
        }

        var casterUnit = FindCasterUnit(s, caster);
        int casterTeam = casterUnit?.TeamId ?? 0;

        // Find the target tile to spawn on
        TileData spawnTile = null;
        if (targets != null)
        {
            foreach (var obj in targets.Items)
            {
                if (obj is TileData td && td.Occupant == null) { spawnTile = td; break; }
                if (obj is HexTile tv)
                {
                    var tileData = s.Grid?.GetTile(tv.Axial);
                    if (tileData != null && tileData.Occupant == null) { spawnTile = tileData; break; }
                }
            }
        }

        // Fallback: find empty adjacent tile to caster
        if (spawnTile == null && casterUnit?.CurrentTile != null && s.Grid != null)
        {
            foreach (var neighbor in s.Grid.GetNeighbors(casterUnit.CurrentTile.Axial))
            {
                var td = s.Grid.GetTile(neighbor);
                if (td != null && td.Occupant == null)
                {
                    spawnTile = td;
                    break;
                }
            }
        }

        if (spawnTile == null)
        {
            s.Log($"[Summon] No valid tile to spawn {UnitKind}.");
            return;
        }

        for (int i = 0; i < Count; i++)
        {
            var spawned = s.OnSummonRequested(UnitKind, spawnTile, casterTeam);
            if (spawned != null)
            {
                s.UnitsInPlay.Add(spawned);
                s.Log($"[Summon] Spawned {UnitKind} at {spawnTile.Axial}.");
            }
            else
            {
                s.Log($"[Summon] Failed to spawn {UnitKind}.");
            }

            // For multiple summons, find next empty tile
            if (i < Count - 1 && casterUnit?.CurrentTile != null)
            {
                spawnTile = null;
                foreach (var neighbor in s.Grid.GetNeighbors(casterUnit.CurrentTile.Axial))
                {
                    var td = s.Grid.GetTile(neighbor);
                    if (td != null && td.Occupant == null) { spawnTile = td; break; }
                }
                if (spawnTile == null) break;
            }
        }
    }
}

public sealed class RemoveArmorEffect : EffectBase
{
    public int Amount; // 0 = remove all armor

    public RemoveArmorEffect(int amount = 0)
    {
        Amount = amount;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets == null) return;

        foreach (var obj in targets.Items)
        {
            var victim = ResolveTargetUnit(s, obj);
            if (victim == null) continue;

            int removed;
            if (Amount <= 0)
            {
                removed = victim.Stats.Armor;
                victim.Stats.Armor = 0;
            }
            else
            {
                removed = Math.Min(victim.Stats.Armor, Amount);
                victim.Stats.Armor -= removed;
            }

            if (removed > 0)
            {
                victim.RefreshHealthBar();
                s.Log($"[RemoveArmor] {victim.Name} loses {removed} armor (now {victim.Stats.Armor}).");
            }
        }
    }
}

public sealed class CreateRubbleEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets == null || s?.Grid == null) return;

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td) tile = td;
            else if (obj is HexTile tv) tile = s.Grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null) tile = u.CurrentTile;

            if (tile == null || tile.IsBlocked) continue;

            tile.ApplyTerrainModifier("rubble");
            s.Grid.ApplyVisualToTile(tile);
            s.Log($"[Rubble] {tile.Axial} is now difficult terrain.");
        }
    }
}

public sealed class RaiseTerrainEffect : EffectBase
{
    public int HeightIncrease;

    public RaiseTerrainEffect(int heightIncrease = 1)
    {
        HeightIncrease = heightIncrease;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (targets == null || s?.Grid == null) return;

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td) tile = td;
            else if (obj is HexTile tv) tile = s.Grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null) tile = u.CurrentTile;

            if (tile == null) continue;

            // Raise height
            tile.Height += HeightIncrease;
            tile.TileView?.SetHeight(tile.Height);

            // Imbue with earth and create rubble
            tile.ElementType = TileElementType.Earth;
            tile.ElementStrength = 1.0f;
            tile.ApplyTerrainModifier("rubble");
            s.Grid.ApplyVisualToTile(tile);

            // Push any unit on the tile (ground rising under them)
            if (tile.Occupant != null)
            {
                tile.Occupant.ApplyDamage(HeightIncrease * 2);
                s.Log($"[RaiseTerrain] {tile.Occupant.Name} crushed by rising ground for {HeightIncrease * 2} damage.");
            }

            s.Log($"[RaiseTerrain] {tile.Axial} raised by {HeightIncrease} (now height {tile.Height}), imbued with earth, rubble created.");
        }
    }
}

// ── No-Op Effect ────────────────────────────────────────────────
public sealed class NoOpEffect : EffectBase
{
	public string Text;
	public NoOpEffect(string t) { Text = t; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.Log($"[NoOp] {Text}");
	}
}
