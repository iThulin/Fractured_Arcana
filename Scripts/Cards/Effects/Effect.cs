using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// Effect.cs
//
// Purpose:        EffectBase abstract class plus all leaf
//                 (non-composite) effects — damage, heal, push,
//                 imbue, summon, transform, status, etc. Each
//                 leaf is paired with a registry entry in
//                 JsonCardLoader.RegisterBuiltins.
// Layer:          Effects
// Collaborators:  ScriptingInterfaces.cs (IEffect, EffectResult),
//                 JsonCardLoader.cs (RegisterBuiltins maps JSON
//                 type strings to these classes),
//                 GameState.cs, Entity.cs, Unit.cs, TileData.cs,
//                 PersistentEffect.cs (some leaf effects spawn
//                 persistent effects, e.g. AvatarTransformEffect)
// See:            README §5.4 (Effect Types — JSON contract),
//                 README §7 — "Effect Types Must Be Registered"
// ============================================================

/// <summary>
/// Abstract base for every leaf and composite effect in the project. Leaf effects
/// override <see cref="Resolve"/>; effects that need to report data back to a
/// downstream <c>ConditionalEffect</c> (lethal damage, targets hit, spawned entities)
/// also override <see cref="ResolveWithResult"/>. Provides shared helpers for
/// resolving casters and targets across the Unit/TileData/HexTile shapes the runtime
/// passes around.
/// </summary>
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
		if (s == null)
			return null;
		// PlayerA maps to PlayerUnit
		if (caster == s.PlayerA)
			return s.PlayerUnit;
		if (caster == s.PlayerB)
			return s.EnemyUnit;
		// Fallback: search UnitsInPlay by name
		foreach (var u in s.UnitsInPlay)
			if (u != null && u.Name == caster.Name)
				return u;
		return s.PlayerUnit; // last resort
	}

	// ── Shared helper: resolve any target type to a Unit ────────────
	protected static Unit ResolveTargetUnit(GameState s, object obj)
	{
		if (obj is Unit u)
			return u;
		if (obj is TileData td)
			return td.Occupant;
		if (obj is HexTile tv)
		{
			var tileData = s?.Grid?.GetTile(tv.Axial);
			return tileData?.Occupant;
		}
		return null;
	}
}

// ── Leaf effects ────────────────────────────────────────────────────────

/// <summary>Deals a flat amount of damage to every target in the target set. Also handles caster-side modifiers (empowered status, avatar aura bonus, equipment spell-damage), arcane-mark consumption, and chain bounce propagation when the caster has the "chaining" status.</summary>
public sealed class DealDamageEffect : EffectBase
{
	public int Amount;
	public DealDamageEffect(int a) { Amount = a; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		int hit = 0;
		if (targets == null)
		{ s?.Log($"[DealDamage] No targets."); return; }

		var casterUnit = FindCasterUnit(s, caster);

		// ── Bonus damage accumulation ────────────────────────────────────
		int bonus = 0;
		if (casterUnit != null && casterUnit.HasStatus("empowered"))
			bonus += 3;

		var avatarAura = s.GetActiveEffect<AvatarAuraEffect>(caster);
		if (avatarAura != null)
			bonus += avatarAura.BonusDamage;

		int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
		if (bonusSpellDmg > 0)
			s.Log($"[SpellDamage] +{bonusSpellDmg} from equipment.");

		int totalDamage = Amount + bonus + bonusSpellDmg;

		// ── Debug logging ────────────────────────────────────────────────
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

		// ── Main damage loop ─────────────────────────────────────────────
		foreach (var obj in targets.Items)
		{
			Unit victim = null;

			if (obj is Unit u)
			{
				u.ApplyDamage(totalDamage);
				s.Log($"HIT unit {u.Name}");
				hit++;
				victim = u;
			}
			else if (obj is TileData td && td.Occupant != null)
			{
				td.Occupant.ApplyDamage(totalDamage);
				s.Log($"HIT tile occupant {td.Occupant.Name} on {td.Axial}");
				hit++;
				victim = td.Occupant;
			}
			else if (obj is HexTile tileView)
			{
				var tileData = ResolveTileDataFromView(s, tileView);
				if (tileData != null && tileData.Occupant != null)
				{
					tileData.Occupant.ApplyDamage(totalDamage);
					s.Log($"HIT tile occupant {tileData.Occupant.Name} on {tileData.Axial}");
					hit++;
					victim = tileData.Occupant;
				}
			}

			// Arcane mark: separate bonus, intentionally outside totalDamage
			if (victim != null && victim.HasStatus("arcane_mark"))
			{
				victim.RemoveStatus("arcane_mark");
				int markBonus = 3;
				victim.ApplyDamage(markBonus);
				s.Log($"[ArcaneMark] {victim.Name} takes {markBonus} bonus damage. Mark consumed.");
			}
		}

		s.Log($"Resolve: Deal {totalDamage} damage to {hit} target(s). lethal={hit > 0}");

		// ── Chain bounce ─────────────────────────────────────────────────
		int chainCount = 0;
		if (casterUnit != null && casterUnit.HasStatus("chaining"))
		{
			chainCount = casterUnit.Stats.StatusEffects.ContainsKey("chaining")
				? Math.Min(casterUnit.Stats.StatusEffects["chaining"], 2)
				: 1;
		}

		if (chainCount > 0 && hit > 0)
		{
			if (s?.Grid == null)
			{ s?.Log("[Chain] No grid for chain bounce."); return; }

			var alreadyHit = new HashSet<Unit>();
			foreach (var obj in targets.Items)
			{
				var victim = ResolveTargetUnit(s, obj);
				if (victim != null)
					alreadyHit.Add(victim);
			}
			alreadyHit.Add(casterUnit);

			Unit chainOrigin = null;
			foreach (var obj in targets.Items)
			{
				var v = ResolveTargetUnit(s, obj);
				if (v != null)
				{ chainOrigin = v; break; }
			}

			for (int chain = 0; chain < chainCount; chain++)
			{
				if (chainOrigin?.CurrentTile == null)
					break;

				Unit nearest = null;
				int nearestDist = int.MaxValue;
				foreach (var unit in s.UnitsInPlay)
				{
					if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
						continue;
					if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
						continue;
					if (alreadyHit.Contains(unit))
						continue;

					int dist = s.Grid.Distance(chainOrigin.CurrentTile.Axial, unit.CurrentTile.Axial);
					if (dist <= 3 && dist < nearestDist)
					{
						nearestDist = dist;
						nearest = unit;
					}
				}

				if (nearest != null)
				{
					nearest.ApplyDamage(totalDamage);
					alreadyHit.Add(nearest);
					chainOrigin = nearest;
					s.Log($"[Chain] Bounced to {nearest.Name} for {totalDamage} damage.");
				}
				else
					break;
			}

			casterUnit.Stats.StatusEffects.Remove("chaining");
			s.Log($"[Chain] Chaining consumed.");
		}
	}

	public override EffectResult ResolveWithResult(PredicateContext ctx)
	{
		int totalDamage = 0;
		bool lethal = false;
		int hit = 0;

		if (ctx.Targets == null)
			return new EffectResult();

		var casterUnit = FindCasterUnit(ctx.Game, ctx.Caster);
		int bonus = 0;
		if (casterUnit != null && casterUnit.HasStatus("empowered"))
			bonus += 3;

		int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
		int total = Amount + bonus + bonusSpellDmg;

		foreach (var obj in ctx.Targets.Items)
		{
			Unit victim = null;
			if (obj is Unit u)
				victim = u;
			else if (obj is TileData td && td.Occupant != null)
				victim = td.Occupant;
			else if (obj is HexTile tileView)
			{
				var tileData = ResolveTileDataFromView(ctx.Game, tileView);
				if (tileData != null)
					victim = tileData.Occupant;
			}

			if (victim != null)
			{
				int hpBefore = victim.Stats.Health;
				victim.ApplyDamage(total);
				totalDamage += total;
				hit++;
				if (hpBefore > 0 && victim.Stats.Health <= 0)
					lethal = true;
			}
		}

		ctx.Game?.Log($"Resolve: Deal {total} damage to {hit} target(s). lethal={lethal}");
		return new EffectResult { DamageDealt = totalDamage, WasLethal = lethal, TargetsHit = hit };
	}

	private TileData ResolveTileDataFromView(GameState s, HexTile tileView)
	{
		if (tileView == null)
			return null;
		var grid = s?.Grid;
		if (grid == null)
		{
			s?.Log("ResolveTileDataFromView: could not find HexGridManager.");
			return null;
		}
		return grid.GetTile(tileView.Axial);
	}
}

/// <summary>Deals damage scaled by hex distance from caster to each target. Damage = clamp(distance × BonusPerTile, MinDamage, MaxDamage) + spell-damage bonus.</summary>
public sealed class DistanceDamageEffect : EffectBase
{
	public int MinDamage;
	public int MaxDamage;
	public int BonusPerTile; // damage multiplier per tile, default 1

	public DistanceDamageEffect(int minDamage = 1, int maxDamage = 99, int bonusPerTile = 1)
	{
		MinDamage = minDamage;
		MaxDamage = maxDamage;
		BonusPerTile = bonusPerTile;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null || s?.Grid == null)
			return;

		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.CurrentTile == null)
		{
			s.Log("[DistanceDamage] No caster tile found.");
			return;
		}

		int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;

		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim?.CurrentTile == null)
				continue;

			int dist = s.Grid.Distance(casterUnit.CurrentTile.Axial, victim.CurrentTile.Axial);
			int damage = Math.Clamp(dist * BonusPerTile, MinDamage, MaxDamage) + bonusSpellDmg;

			victim.ApplyDamage(damage);
			s.Log($"[DistanceDamage] {victim.Name} takes {damage} damage (dist={dist}).");
		}
	}
}

// ── AoE All Effect ──────────────────────────────────────────────────────

/// <summary>Deals damage to ALL units within radius of the caster, including allies and the caster itself. High-risk board-wipe primitive.</summary>
public sealed class AoeAllEffect : EffectBase
{
	public int Radius;
	public int Damage;

	public AoeAllEffect(int radius, int damage)
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

		int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
		int totalDamage = Damage + bonusSpellDmg;

		var center = casterUnit.CurrentTile.Axial;
		int hit = 0;

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;
			if (s.Grid.Distance(center, unit.CurrentTile.Axial) > Radius)
				continue;

			unit.ApplyDamage(totalDamage);
			s.Log($"[AoeAll] {unit.Name} takes {totalDamage} damage.");
			hit++;
		}

		s.Log($"[AoeAll] Cataclysm hit {hit} unit(s).");
	}
}

// ── Damage By Hand Size ─────────────────────────────────────────────────

/// <summary>Deals damage equal to the caster's current hand size × <see cref="Multiplier"/>. Plus the caster's spell-damage bonus. Hand of 0 deals 0 (no-op).</summary>
public sealed class DamageByHandSizeEffect : EffectBase
{
	public int Multiplier;
	public DamageByHandSizeEffect(int multiplier = 2) { Multiplier = multiplier; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;

		var casterUnit = FindCasterUnit(s, caster);
		var hand = casterUnit?.DeckData?.Hand ?? new System.Collections.Generic.List<Card>();
		int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
		int damage = hand.Count * Multiplier + bonusSpellDmg;

		if (damage <= 0)
		{
			s.Log($"[HandSizeDamage] Hand is empty, no damage dealt.");
			return;
		}

		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim == null)
				continue;
			victim.ApplyDamage(damage);
			s.Log($"[HandSizeDamage] {victim.Name} takes {damage} damage ({hand.Count} cards x {Multiplier} +{bonusSpellDmg} spell).");
		}
	}
}

/// <summary>Dual-purpose movement primitive: when targets is empty/self, grants the caster N move points; when targets contains units, pushes each target N tiles away from the caster.</summary>
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
				if (victim == null || victim.CurrentTile == null)
					continue;
				if (casterUnit == null || casterUnit.CurrentTile == null)
					continue;

				// Calculate push direction: away from caster
				var grid = s.Grid;
				if (grid == null)
				{ s.Log("[Push] No grid."); continue; }

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
						if (td == null || !td.CanEnter(victim))
							continue;

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

// ── Teleport Effect ─────────────────────────────────────────────────────

/// <summary>Instantly moves the caster to a target tile, bypassing movement points, pathing, and reaction triggers along the way. First valid empty target wins.</summary>
public sealed class TeleportEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null || s?.Grid == null)
			return;

		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.CurrentTile == null)
			return;

		foreach (var obj in targets.Items)
		{
			TileData destTile = null;

			if (obj is TileData td)
				destTile = td;
			else if (obj is HexTile tv)
				destTile = s.Grid.GetTile(tv.Axial);
			else if (obj is Unit u && u.CurrentTile != null)
				destTile = u.CurrentTile;

			if (destTile == null || destTile.Occupant != null)
				continue;

			casterUnit.CurrentTile.ClearOccupant(casterUnit);
			casterUnit.PlaceOnTile(destTile);
			s.Log($"[Teleport] {casterUnit.Name} teleported to {destTile.Axial}.");
			break;
		}
	}
}

// ── Push Effect ─────────────────────────────────────────────────────────

/// <summary>Pushes each target N tiles directly away from the caster. When a push is blocked by an obstacle, optionally deals <see cref="CollisionDamage"/> to the obstructed unit. See README §5.4 — the JSON key is `tiles` not `amount`, a common typo source.</summary>
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
		if (casterUnit?.CurrentTile == null || s?.Grid == null || targets == null)
			return;

		var casterPos = casterUnit.CurrentTile.Axial;

		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim == null || victim.CurrentTile == null)
				continue;

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
					if (td == null || !td.CanEnter(victim))
						continue;

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

// ── Pull Effect ─────────────────────────────────────────────────────────

/// <summary>
/// Pulls each target N tiles directly toward the caster. When a pull is blocked
/// by an obstacle, the unit stops at the last valid tile — no collision damage
/// since being pulled into the caster is intentional positioning, not a hazard.
/// JSON key is "tiles". See PushEffect for the inverse.
/// </summary>
public sealed class PullEffect : EffectBase
{
	public int Tiles;

	public PullEffect(int tiles)
	{
		Tiles = tiles;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.CurrentTile == null || s?.Grid == null || targets == null)
			return;

		var casterPos = casterUnit.CurrentTile.Axial;

		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim == null || victim.CurrentTile == null)
				continue;

			// Don't pull the caster toward themselves
			if (victim == casterUnit)
				continue;

			int pulled = 0;

			for (int i = 0; i < Tiles; i++)
			{
				var current = victim.CurrentTile.Axial;

				// Already adjacent to caster — nowhere closer to go
				if (s.Grid.Distance(casterPos, current) <= 1)
					break;

				TileData bestTile = null;
				int bestDist = int.MaxValue;

				foreach (var neighbor in s.Grid.GetNeighbors(current))
				{
					var td = s.Grid.GetTile(neighbor);
					if (td == null || !td.CanEnter(victim))
						continue;

					int distFromCaster = s.Grid.Distance(casterPos, neighbor);
					if (distFromCaster < bestDist)
					{
						bestDist = distFromCaster;
						bestTile = td;
					}
				}

				if (bestTile != null)
				{
					victim.CurrentTile.ClearOccupant(victim);
					victim.PlaceOnTile(bestTile);
					pulled++;
				}
				else
				{
					// Blocked — stop here, no collision
					break;
				}
			}

			s.Log($"[Pull] {victim.Name} pulled {pulled} tile(s) toward {casterUnit.Name}.");
		}
	}
}

// ── Shield / Armor Effects ──────────────────────────────────────────────

/// <summary>Grants the caster temporary shield (consumed before HP, cleared at end of turn).</summary>
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

/// <summary>Grants the caster persistent armor (reduces incoming damage, does NOT decay at end of turn).</summary>
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

/// <summary>Grants armor to each ally target. Filters out non-allies via TeamId match against the caster.</summary>
public sealed class GiveTargetArmorEffect : EffectBase
{
	public int Amount;
	public GiveTargetArmorEffect(int a) { Amount = a; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;
		var casterUnit = FindCasterUnit(s, caster);

		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null)
				continue;

			// Only buff allies
			if (casterUnit != null && unit.TeamId != casterUnit.TeamId)
				continue;

			unit.Stats.Armor += Amount;
			unit.RefreshHealthBar();
			s.Log($"[GiveTargetArmor] {unit.Name} gains {Amount} armor (now {unit.Stats.Armor}).");
		}
	}
}

// ── Armor Per Target Effect ─────────────────────────────────────────────────────────

/// <summary>
/// Grants the caster armor equal to <see cref="Amount"/> multiplied by the number
/// of units in the current TargetSet. Designed to follow a retarget step in a
/// sequence — the targets from the prior step are the units being counted.
/// JSON keys: "type": "armor_per_target", "amount": n.
/// </summary>
public sealed class ArmorPerTargetEffect : EffectBase
{
	public int Amount;

	public ArmorPerTargetEffect(int amount)
	{
		Amount = amount;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		// Fallback path — no PredicateContext available, count whatever targets are passed
		ApplyArmor(s, caster, targets);
	}

	public override EffectResult ResolveWithResult(PredicateContext ctx)
	{
		// Prefer LastRetargetedTargets so this works correctly as a sequence
		// sibling after a retarget step (e.g. pull all enemies, then armor per enemy)
		var countTargets = ctx.LastRetargetedTargets ?? ctx.Targets;
		ApplyArmor(ctx.Game, ctx.Caster, countTargets);
		return new EffectResult();
	}

	private void ApplyArmor(GameState s, Entity caster, TargetSet targets)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null)
			return;

		int targetCount = 0;
		if (targets != null)
		{
			foreach (var obj in targets.Items)
			{
				var unit = ResolveTargetUnit(s, obj);
				if (unit != null)
					targetCount++;
			}
		}

		int totalArmor = targetCount * Amount;
		if (totalArmor > 0)
		{
			casterUnit.Stats.Armor += totalArmor;
			casterUnit.RefreshHealthBar();
			s.Log($"[ArmorPerTarget] {casterUnit.Name} gains {totalArmor} armor " +
				  $"({targetCount} target(s) × {Amount}) — now {casterUnit.Stats.Armor}.");
		}
		else
		{
			s.Log($"[ArmorPerTarget] {casterUnit.Name}: no targets counted, no armor gained.");
		}
	}
}

// ── Remove Status Effect ────────────────────────────────────────────────

/// <summary>Removes status effects from each target. When <see cref="StatusName"/> is null, strips every entry in the built-in negative-status set; when set, removes only that named status.</summary>
public sealed class RemoveStatusEffect : EffectBase
{
	public string StatusName; // null = remove all negative statuses
	private static readonly HashSet<string> NegativeStatuses = new()
	{
		"burn", "frozen", "slowed", "stunned", "rooted", "poisoned", "weakened", "blinded", "bound"
	};

	public RemoveStatusEffect(string statusName = null)
	{
		StatusName = statusName;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;

		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null)
				continue;

			if (StatusName == null && !unit.CanBeFreed)
			{
				s.Log($"[RemoveStatus] {unit.Name} is bound — cannot clear statuses.");
				continue;
			}

			if (StatusName != null)
			{
				unit.RemoveStatus(StatusName);
				s.Log($"[RemoveStatus] Removed {StatusName} from {unit.Name}.");
			}
			else
			{
				foreach (var status in NegativeStatuses)
					unit.RemoveStatus(status);
				s.Log($"[RemoveStatus] Cleared all negative statuses from {unit.Name}.");
			}
		}
	}
}

// ── Draw / Mana / Heal / Self-Damage Effects ────────────────────────────

/// <summary>Draws <see cref="Count"/> cards into the caster's hand. Fires <c>GameState.OnDrawCards</c> for UI refreshes.</summary>
public sealed class DrawCardsEffect : EffectBase
{
	public int Count;
	public DrawCardsEffect(int n) { Count = n; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null)
		{
			s.Log($"[Draw] No caster unit found.");
			return;
		}

		var deckData = casterUnit.DeckData;
		if (deckData == null)
		{
			s.Log($"[Draw] {casterUnit.Name} has no DeckData.");
			return;
		}

		var drawn = deckData.Draw(Count);
		s.Log($"[Draw] {casterUnit.Name} draws {drawn.Count} card(s). Hand now: {deckData.Hand.Count}");

		s.OnDrawCards?.Invoke(casterUnit);
	}
}

/// <summary>Grants <see cref="Amount"/> mana to the caster and syncs <c>GameState.Mana</c> so the cost-check path sees the updated pool immediately.</summary>
public sealed class ManaGainEffect : EffectBase
{
	public int Amount;
	public ManaGainEffect(int a) { Amount = a; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit != null)
		{
			casterUnit.GainMana(Amount);
			// Keep GameState.Mana in sync for cost checking
			if (s.Mana.ContainsKey(caster))
				s.Mana[caster] = casterUnit.Stats.Mana;
			s.Log($"[ManaGain] {casterUnit.Name} gains {Amount} mana (now {casterUnit.Stats.Mana}/{casterUnit.Stats.MaxMana}).");
		}
	}
}

// ── Mana Per Nearby Element Effect ─────────────────────────────────────────────────────────

/// <summary>
/// Grants the caster 1 mana for each unique element type present on tiles
/// within <see cref="Radius"/> of the caster. Maximum 4 mana (one per element).
/// Designed for Worldshaper's Elemental Read — rewards building a diverse
/// elemental board state.
/// JSON keys: "type": "mana_per_nearby_element", "radius": n.
/// </summary>
public sealed class ManaPerNearbyElementEffect : EffectBase
{
	public int Radius;

	public ManaPerNearbyElementEffect(int radius = 3)
	{
		Radius = radius;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.CurrentTile == null || s?.Grid == null)
			return;

		var center = casterUnit.CurrentTile.Axial;
		var uniqueElements = new HashSet<TileElementType>();

		foreach (var kvp in s.Grid.Tiles)
		{
			var tile = kvp.Value;
			if (tile == null)
				continue;
			if (tile.ElementType == TileElementType.None)
				continue;
			if (s.Grid.Distance(center, kvp.Key) > Radius)
				continue;

			uniqueElements.Add(tile.ElementType);
		}

		int manaGained = uniqueElements.Count;
		if (manaGained == 0)
		{
			s.Log($"[ManaPerNearbyElement] {casterUnit.Name}: no elements within {Radius} — no mana gained.");
			return;
		}

		casterUnit.GainMana(manaGained);
		if (s.Mana.ContainsKey(caster))
			s.Mana[caster] = casterUnit.Stats.Mana;

		var elementNames = string.Join(", ", uniqueElements);
		s.Log($"[ManaPerNearbyElement] {casterUnit.Name} gains {manaGained} mana " +
			  $"({elementNames}) — now {casterUnit.Stats.Mana}/{casterUnit.Stats.MaxMana}.");
	}
}

/// <summary>Caster takes <see cref="Amount"/> damage. Used for life-cost spells.</summary>
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

/// <summary>Heals the caster for <see cref="Amount"/>, clamped to <c>MaxHealth</c>.</summary>
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

// ── Tile / Terrain Effects ──────────────────────────────────────────────

/// <summary>Imbues each target tile with an element. Fire tiles become hazardous. When <see cref="BonusDamage"/> > 0 and the tile is occupied by an enemy, deals additional spell-modified damage on imbuement.</summary>
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
		if (s?.Grid == null)
		{ s?.Log("[ImbueTile] No grid."); return; }

		TileElementType elementType = Element.ToLowerInvariant() switch
		{
			"fire" => TileElementType.Fire,
			"ice" => TileElementType.Frost,
			"frost" => TileElementType.Frost,
			"storm" => TileElementType.Lightning,
			"stone" => TileElementType.Earth,
			"earth" => TileElementType.Earth,
			_ => TileElementType.None
		};

		if (targets == null)
			return;

		foreach (var obj in targets.Items)
		{
			TileData tile = null;

			if (obj is TileData td)
				tile = td;
			else if (obj is HexTile tv)
				tile = s.Grid.GetTile(tv.Axial);
			else if (obj is Unit u && u.CurrentTile != null)
				tile = u.CurrentTile;

			if (tile == null)
				continue;

			tile.ElementType = elementType;
			tile.ElementStrength = 1.0f;

			if (elementType == TileElementType.Fire)
				tile.IsHazardous = true;

			// Use the existing visual system to update the tile
			tile.TileView?.SetElement(elementType);

			s.Log($"[ImbueTile] {tile.Axial} imbued with {Element} ({elementType}).");

			if (BonusDamage > 0 && tile.Occupant != null && tile.Occupant.TeamId != 0)
			{
				var casterUnit = FindCasterUnit(s, caster);
				int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
				int totalImbueDmg = BonusDamage + bonusSpellDmg;
				tile.Occupant.ApplyDamage(totalImbueDmg);
				s.Log($"[ImbueTile] {Element} deals {totalImbueDmg} to {tile.Occupant.Name}.");
			}
		}
	}
}

// ── Imbue All Tiles Random Effect ─────────────────────────────────────────────────────────

/// <summary>
/// Imbues every tile on the board with a random element. No radius restriction —
/// this is a board-wide effect. Used by Ragnarok and similar capstone cards.
/// JSON key: "type": "imbue_all_tiles_random". No parameters.
/// </summary>
public sealed class ImbueAllTilesRandomEffect : EffectBase
{
	private static readonly TileElementType[] Elements =
	{
		TileElementType.Fire, TileElementType.Frost,
		TileElementType.Lightning, TileElementType.Earth
	};

	private static readonly Random _rng = new();

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null)
			return;

		int imbued = 0;
		foreach (var kvp in s.Grid.Tiles)
		{
			var tile = kvp.Value;
			if (tile == null)
				continue;

			var element = Elements[_rng.Next(Elements.Length)];
			tile.ElementType = element;
			tile.ElementStrength = 1.0f;
			if (element == TileElementType.Fire)
				tile.IsHazardous = true;
			tile.TileView?.SetElement(element);
			imbued++;
		}

		s.Log($"[ImbueAllTilesRandom] Imbued {imbued} tiles with random elements.");
	}
}

/// <summary>Places a triggered glyph on the target tile. Glyph fires when an enemy steps on the tile and is consumed by the trigger. Optionally applies a named status on trigger. One glyph per cast; tile must be unblocked and not already glyphed.</summary>
public sealed class PlaceGlyphEffect : EffectBase
{
	public int Damage;
	public string Status;
	public int StatusDuration;

	public PlaceGlyphEffect(int damage, string status = null, int statusDuration = 1)
	{
		Damage = damage;
		Status = status;
		StatusDuration = statusDuration;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null || s?.Grid == null)
			return;

		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null)
			return;

		int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
		int totalGlyphDmg = Damage + bonusSpellDmg; // captured at placement

		foreach (var obj in targets.Items)
		{
			TileData tile = null;
			if (obj is TileData td)
				tile = td;
			else if (obj is HexTile tv)
				tile = s.Grid.GetTile(tv.Axial);

			if (tile == null || tile.IsBlocked)
				continue;
			if (tile.Glyph != null)
				continue; // tile already has a glyph

			int dmg = totalGlyphDmg; // use captured value in closure
			string status = Status;
			int dur = StatusDuration;

			tile.Glyph = new GlyphData
			{
				OwnerId = casterUnit.Name,
				OwnerTeam = casterUnit.TeamId,
				GameState = s,
				OnTrigger = (victim, state) =>
				{
					victim.ApplyDamage(dmg);
					state.Log($"[Glyph] {victim.Name} triggered glyph, takes {dmg} damage.");

					if (!string.IsNullOrEmpty(status))
					{
						victim.ApplyStatus(status, dur);
						state.Log($"[Glyph] {victim.Name} is {status} for {dur} turn(s).");
					}
				}
			};

			tile.TileView?.ShowGlyph();
			s.Log($"[Glyph] Placed glyph at {tile.Axial}.");
			break; // one glyph per cast
		}
	}
}

// ── Status / Summon / Misc Effects ──────────────────────────────────────

/// <summary>Applies a named status to each target for a given duration. The runtime does not enforce a closed status enum here — any string is accepted and the consumer is responsible for handling it.</summary>
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
		if (targets == null)
			return;
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

// ── Cleanse Debuffs Effect ─────────────────────────────────────────────────────────

/// <summary>
/// Removes all negative status effects from the caster. Debuffs are defined
/// as a hardcoded set of known negative status names. Any status not in this
/// set (e.g. buffs like "chaining") is left untouched.
/// JSON key: "type": "cleanse_debuffs". No parameters.
/// </summary>
public sealed class CleanseDebuffsEffect : EffectBase
{
	private static readonly HashSet<string> Debuffs = new()
	{
		"frozen",
		"rooted",
		"slowed",
		"stunned",
		"burn",
		"poisoned",
		"weakened",
		"blinded",
		"silenced",
		"cursed",
		"bound"
	};

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null)
			return;

		if (!casterUnit.CanBeFreed)
		{
			s.Log($"[Cleanse] {casterUnit.Name} is bound — cannot be cleansed.");
			return;
		}

		var toRemove = new List<string>();
		foreach (var status in casterUnit.Stats.StatusEffects.Keys)
		{
			if (Debuffs.Contains(status))
				toRemove.Add(status);
		}

		foreach (var status in toRemove)
		{
			casterUnit.RemoveStatus(status);
			s.Log($"[Cleanse] {casterUnit.Name}: removed {status}.");
		}

		if (toRemove.Count == 0)
			s.Log($"[Cleanse] {casterUnit.Name}: no debuffs to remove.");
		else
			s.Log($"[Cleanse] {casterUnit.Name}: cleared {toRemove.Count} debuff(s).");
	}
}

/// <summary>Spawns <see cref="Count"/> instances of a named unit kind on the player's side. Requires <c>GameState.OnSummonRequested</c> to be wired by the combat scene; without it, the effect logs an error and no-ops. Uses targeted tile when provided, otherwise falls back to the first empty neighbor of the caster.</summary>
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
				if (obj is TileData td && td.Occupant == null)
				{ spawnTile = td; break; }
				if (obj is HexTile tv)
				{
					var tileData = s.Grid?.GetTile(tv.Axial);
					if (tileData != null && tileData.Occupant == null)
					{ spawnTile = tileData; break; }
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
					if (td != null && td.Occupant == null)
					{ spawnTile = td; break; }
				}
				if (spawnTile == null)
					break;
			}
		}
	}
}

/// <summary>Strips armor from each target. <see cref="Amount"/> == 0 removes all armor; positive values cap at the target's current armor pool.</summary>
public sealed class RemoveArmorEffect : EffectBase
{
	public int Amount; // 0 = remove all armor

	public RemoveArmorEffect(int amount = 0)
	{
		Amount = amount;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;

		foreach (var obj in targets.Items)
		{
			var victim = ResolveTargetUnit(s, obj);
			if (victim == null)
				continue;

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

/// <summary>Converts each target tile to "rubble" (difficult terrain). Skips already-blocked tiles.</summary>
public sealed class CreateRubbleEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null || s?.Grid == null)
			return;

		foreach (var obj in targets.Items)
		{
			TileData tile = null;
			if (obj is TileData td)
				tile = td;
			else if (obj is HexTile tv)
				tile = s.Grid.GetTile(tv.Axial);
			else if (obj is Unit u && u.CurrentTile != null)
				tile = u.CurrentTile;

			if (tile == null || tile.IsBlocked)
				continue;

			tile.ApplyTerrainModifier("rubble");
			s.Grid.ApplyVisualToTile(tile);
			s.Log($"[Rubble] {tile.Axial} is now difficult terrain.");
		}
	}
}

/// <summary>Raises the target tile by <see cref="HeightIncrease"/> units, imbues it with Earth, applies rubble, and crushes any unit standing on it for <c>HeightIncrease × 2</c> damage.</summary>
public sealed class RaiseTerrainEffect : EffectBase
{
	public int HeightIncrease;

	public RaiseTerrainEffect(int heightIncrease = 1)
	{
		HeightIncrease = heightIncrease;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null || s?.Grid == null)
			return;

		foreach (var obj in targets.Items)
		{
			TileData tile = null;
			if (obj is TileData td)
				tile = td;
			else if (obj is HexTile tv)
				tile = s.Grid.GetTile(tv.Axial);
			else if (obj is Unit u && u.CurrentTile != null)
				tile = u.CurrentTile;

			if (tile == null)
				continue;

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

// ============================================================================
// Necromancer Effects
// ============================================================================

// ── Summon Spirit ──────────────────────────────────────────────────────────────

/// <summary>
/// Summons a spirit unit on a memorial tile. Marks the unit as IsSpirit,
/// applies spirit appearance, and consumes the memorial it rises from.
/// JSON: { "type": "summon_spirit", "unit": "Spirit", "hp": 10, "damage": 5, "speed": 1 }
/// </summary>
public sealed class SummonSpiritEffect : EffectBase
{
	public string UnitKind;
	public int HP, Damage, Speed;
	public bool OnDeathMemorial;

	public SummonSpiritEffect(string kind, int hp, int damage, int speed, bool onDeathMemorial = false)
	{
		UnitKind = kind;
		HP = hp;
		Damage = damage;
		Speed = speed;
		OnDeathMemorial = onDeathMemorial;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{
			s.Log("[SummonSpirit] No summon handler registered.");
			return;
		}

		var casterUnit = s.ActiveCasterUnit;
		int ownerTeam = casterUnit?.TeamId ?? 0;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null || !tile.HasMemorial)
			{
				s.Log("[SummonSpirit] Target tile has no memorial — cannot summon here.");
				continue;
			}

			// If the memorial tile is occupied, try to find an adjacent empty tile.
			// HACK: Caster or another unit standing on the memorial would cause the
			// summon handler to place the spirit at an undefined position (observed: (1,1)).
			TileData spawnTile = tile;
			if (tile.IsOccupied && s.Grid != null)
			{
				spawnTile = null;
				foreach (var neighborCoord in s.Grid.GetNeighbors(tile.Axial))
				{
					var neighbor = s.Grid.GetTile(neighborCoord);
					if (neighbor != null && neighbor.IsWalkable && !neighbor.IsBlocked && !neighbor.IsOccupied)
					{
						spawnTile = neighbor;
						break;
					}
				}

				if (spawnTile == null)
				{
					s.Log($"[SummonSpirit] Memorial at {tile.Axial} is occupied and no adjacent tile is free — summon blocked.");
					continue;
				}

				s.Log($"[SummonSpirit] Memorial tile {tile.Axial} occupied; placing spirit at adjacent tile {spawnTile.Axial}.");
			}

			string sourceName = tile.Memorial?.SourceName ?? "Unknown";

			var spirit = s.OnSummonRequested(UnitKind, spawnTile, ownerTeam);
			if (spirit == null)
				continue;

			spirit.IsSpirit = true;
			spirit.SummonerTeamId = ownerTeam;
			spirit.Stats.MaxHealth = HP;
			spirit.Stats.Health = HP;
			spirit.Stats.BaseSpeed = Speed;
			spirit.AttackDamage = Damage;
			spirit.OnDeathMemorial = OnDeathMemorial;
			spirit.ApplySpiritAppearance();

			s.Memorials?.ConsumeMemorial(tile);
			s.Log($"[SummonSpirit] {sourceName} answers the call as {UnitKind} at {spawnTile.Axial}.");
		}
	}
}

// ── Summon Spirit From All Memorials ──────────────────────────────────────────

/// <summary>
/// Summons a spirit from every memorial on the board simultaneously.
/// JSON: { "type": "summon_spirit_from_all_memorials", "unit": "Spirit", "hp": 10, "damage": 5, "speed": 1 }
/// Optional "hp_per_spirit": true — each spirit's HP equals number of other spirits controlled.
/// </summary>
public sealed class SummonSpiritFromAllMemorialsEffect : EffectBase
{
	public string UnitKind;
	public int BaseHP, Damage, Speed;
	public bool HpPerSpirit;
	public int AdvanceOnArrive;
	public bool InheritMemorialName;
	public int BonusDamagePerStrength;

	public SummonSpiritFromAllMemorialsEffect(string kind, int baseHp, int damage, int speed,
		bool hpPerSpirit = false, int advanceOnArrive = 0,
		bool inheritMemorialName = false, int bonusDamagePerStrength = 0)
	{
		UnitKind = kind;
		BaseHP = baseHp;
		Damage = damage;
		Speed = speed;
		HpPerSpirit = hpPerSpirit;
		AdvanceOnArrive = advanceOnArrive;
		InheritMemorialName = inheritMemorialName;
		BonusDamagePerStrength = bonusDamagePerStrength;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null || s.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		int ownerTeam = casterUnit?.TeamId ?? 0;

		var memorials = s.Memorials.GetAllMemorials();
		int existingSpirits = s.UnitsInPlay.Count(u => u != null && u.IsSpirit && u.SummonerTeamId == ownerTeam);

		foreach (var tile in memorials)
		{
			if (!tile.HasMemorial)
				continue;

			int hp = HpPerSpirit ? Math.Max(1, BaseHP + existingSpirits) : BaseHP;
			int dmg = Damage + (BonusDamagePerStrength > 0
				? tile.Memorial.StrengthValue * BonusDamagePerStrength : 0);

			string sourceName = InheritMemorialName
				? (tile.Memorial?.SourceName ?? UnitKind)
				: UnitKind;

			var spirit = s.OnSummonRequested(UnitKind, tile, ownerTeam);
			if (spirit == null)
				continue;

			spirit.IsSpirit = true;
			spirit.SummonerTeamId = ownerTeam;
			spirit.Stats.MaxHealth = hp;
			spirit.Stats.Health = hp;
			spirit.Stats.BaseSpeed = Speed;
			spirit.AttackDamage = dmg;
			spirit.ApplySpiritAppearance();

			s.Memorials.ConsumeMemorial(tile);
			existingSpirits++;

			s.Log($"[SummonFromAllMemorials] {sourceName} rises at {tile.Axial} ({hp}HP {dmg}DMG).");
		}
	}
}

// ── Create Memorial ────────────────────────────────────────────────────────────

/// <summary>
/// Creates a memorial on target tile or caster tile.
/// JSON: { "type": "create_memorial", "strength": "solid" }
/// Strength values: "faint", "solid", "strong"
/// </summary>
public sealed class CreateMemorialEffect : EffectBase
{
	public MemorialStrength Strength;

	public CreateMemorialEffect(MemorialStrength strength = MemorialStrength.Solid)
	{
		Strength = strength;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		int ownerTeam = casterUnit?.TeamId ?? 0;
		string casterName = casterUnit?.DisplayName ?? casterUnit?.Name ?? "Unknown";

		// Use target tile if available, otherwise caster tile
		TileData tile = null;
		if (targets?.Items?.Count > 0)
		{
			tile = targets.Items[0] switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};
		}
		tile ??= casterUnit?.CurrentTile;

		if (tile == null)
			return;

		s.Memorials.CreateMemorial(tile, casterName, false, Strength, ownerTeam);
		s.Log($"[CreateMemorial] {Strength} memorial at {tile.Axial}.");
	}
}

// ── Consume Memorial ───────────────────────────────────────────────────────────

/// <summary>
/// Consumes target memorial, marking it for removal at turn end.
/// JSON: { "type": "consume_memorial" }
/// </summary>
public sealed class ConsumeMemorialEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null || !tile.HasMemorial)
				continue;

			s.Memorials.ConsumeMemorial(tile);
			s.Log($"[ConsumeMemorial] Memorial released at {tile.Axial}.");
		}
	}
}

// ── Consume Memorial or Dismiss Spirit ────────────────────────────────────────

/// <summary>
/// Consumes a memorial on the target tile, or dismisses a spirit standing on it.
/// JSON: { "type": "consume_memorial_or_dismiss_spirit" }
/// </summary>
public sealed class ConsumeMemorialOrDismissSpiritEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null)
				continue;

			// Prefer spirit dismissal if a spirit occupies the tile
			if (tile.Occupant is Unit occupant && occupant.IsSpirit)
			{
				occupant.Die();
				s.Log($"[DismissSpirit] {occupant.Name} dismissed from {tile.Axial}.");
				continue;
			}

			// Fall back to consuming the memorial
			if (tile.HasMemorial && s.Memorials != null)
			{
				s.Memorials.ConsumeMemorial(tile);
				s.Log($"[ConsumeMemorial] Memorial released at {tile.Axial}.");
			}
		}
	}
}

// ── Gain Grief ─────────────────────────────────────────────────────────────────

/// <summary>
/// Adds Grief charges to the active caster's GriefAttunement.
/// JSON: { "type": "gain_grief", "amount": n }
/// </summary>
public sealed class GainGriefEffect : EffectBase
{
	public int Amount;
	public GainGriefEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is GriefAttunement grief)
		{
			grief.GainCharges(Amount);
			s.Log($"[GainGrief] +{Amount} Grief (now {grief.Charges}).");
		}
	}
}

// ── Advance All Spirits ────────────────────────────────────────────────────────

/// <summary>
/// Moves all friendly spirits toward the nearest enemy. If already adjacent, they attack.
/// JSON: { "type": "advance_all_spirits", "tiles": n, "attack_if_adjacent": true }
/// </summary>
public sealed class AdvanceAllSpiritsEffect : EffectBase
{
	public int Tiles;
	public bool AttackIfAdjacent;
	public bool GrantAttackIfReached;

	public AdvanceAllSpiritsEffect(int tiles, bool attackIfAdjacent = true, bool grantAttackIfReached = false)
	{
		Tiles = tiles;
		AttackIfAdjacent = attackIfAdjacent;
		GrantAttackIfReached = grantAttackIfReached;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null || s.Grid == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
		{
			if (spirit.CurrentTile == null)
				continue;

			// Find nearest enemy
			Unit nearestEnemy = null;
			int bestDist = int.MaxValue;
			foreach (var unit in s.UnitsInPlay)
			{
				if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
					continue;
				if (unit.TeamId == spirit.TeamId)
					continue;
				int dist = s.Grid.Distance(spirit.CurrentTile.Axial, unit.CurrentTile.Axial);
				if (dist < bestDist)
				{ bestDist = dist; nearestEnemy = unit; }
			}

			if (nearestEnemy == null)
				continue;

			if (AttackIfAdjacent && bestDist <= 1)
			{
				// Already adjacent — attack
				nearestEnemy.ApplyDamage(spirit.AttackDamage);
				s.Log($"[AdvanceSpirits] {spirit.Name} attacks {nearestEnemy.Name} for {spirit.AttackDamage}.");
			}
			else
			{
				// Move toward enemy
				spirit.Stats.MovePoints = Tiles;
				s.Log($"[AdvanceSpirits] {spirit.Name} advances {Tiles} toward {nearestEnemy.Name}.");
				// Actual pathfinding movement is handled by the movement system — we set move points here
			}
		}
	}
}

// ── Buff All Spirits ───────────────────────────────────────────────────────────

/// <summary>
/// Grants a temporary stat buff to all friendly spirits.
/// JSON: { "type": "buff_all_spirits", "stat": "damage", "amount": n, "duration": 1 }
/// Supported stats: "damage", "armor", "undying"
/// </summary>
public sealed class BuffAllSpiritsEffect : EffectBase
{
	public string Stat;
	public int Amount;
	public int Duration;

	public BuffAllSpiritsEffect(string stat, int amount, int duration = 1)
	{
		Stat = stat;
		Amount = amount;
		Duration = duration;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
		{
			switch (Stat.ToLower())
			{
				case "damage":
					spirit.AttackDamage += Amount;
					spirit.SpiritDamageBuff += Amount;
					spirit.SpiritDamageBuffTurns = Duration;
					break;
				case "armor":
					spirit.Stats.Armor += Amount;
					break;
				case "undying":
					spirit.IsUndying = true;
					spirit.UndyingTurns = Duration;
					break;
			}
		}

		s.Log($"[BuffAllSpirits] {spirits.Count} spirit(s) buffed: +{Amount} {Stat} for {Duration} turn(s).");
	}
}

// ── Mark Spirits Memorial On Kill ─────────────────────────────────────────────

/// <summary>
/// Marks all friendly spirits to create a memorial when they score a kill this turn.
/// JSON: { "type": "mark_spirits_memorial_on_kill" }
/// </summary>
public sealed class MarkSpiritsMemorialOnKillEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
			spirit.CreateMemorialOnKill = true;

		s.Log($"[MarkSpirits] {spirits.Count} spirit(s) will leave memorials on kill.");
	}
}

// ── Armor Per Memorial ─────────────────────────────────────────────────────────

/// <summary>
/// Grants the caster armor equal to AmountPer × number of memorials on the board.
/// JSON: { "type": "armor_per_memorial", "amount_per": n }
/// </summary>
public sealed class ArmorPerMemorialEffect : EffectBase
{
	public int AmountPer;
	public ArmorPerMemorialEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int count = s.Memorials.CountMemorials();
		int armor = count * AmountPer;
		if (armor > 0)
		{
			casterUnit.Stats.Armor += armor;
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[ArmorPerMemorial] {count} memorial(s) × {AmountPer} = {armor} armor.");
	}
}

// ── Armor Per Grief ────────────────────────────────────────────────────────────

/// <summary>
/// Grants the caster armor equal to AmountPer × current Grief charges.
/// JSON: { "type": "armor_per_grief", "amount_per": n }
/// </summary>
public sealed class ArmorPerGriefEffect : EffectBase
{
	public int AmountPer;
	public ArmorPerGriefEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is not GriefAttunement grief)
			return;

		int armor = grief.Charges * AmountPer;
		if (armor > 0)
		{
			casterUnit.Stats.Armor += armor;
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[ArmorPerGrief] {grief.Charges} Grief × {AmountPer} = {armor} armor.");
	}
}

// ── Heal Fraction of Damage ────────────────────────────────────────────────────

/// <summary>
/// Heals the caster for a fraction of the damage dealt by the previous step.
/// Reads damage from EffectResult context if available; otherwise reads last damage dealt from GameState.
/// JSON: { "type": "heal_fraction_of_damage", "fraction": 0.5 }
/// </summary>
public sealed class HealFractionOfDamageEffect : EffectBase
{
	public float Fraction;
	public HealFractionOfDamageEffect(float fraction) { Fraction = fraction; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int damage = s.LastDamageDealt;
		int heal = (int)(damage * Fraction);
		if (heal > 0)
		{
			casterUnit.Stats.Health = Math.Min(casterUnit.Stats.MaxHealth, casterUnit.Stats.Health + heal);
			casterUnit.RefreshHealthBar();
		}
		s.Log($"[HealFraction] Healed {heal} ({Fraction:P0} of {damage} damage).");
	}
}

// ── Gain Mana (alias) ──────────────────────────────────────────────────────────

/// <summary>
/// Alias registered as "gain_mana" — delegates to existing ManaGainEffect logic.
/// JSON: { "type": "gain_mana", "amount": n }
/// </summary>
public sealed class GainManaEffect : EffectBase
{
	public int Amount;
	public GainManaEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;
		casterUnit.GainMana(Amount);
		if (s.Mana.ContainsKey(caster))
			s.Mana[caster] = casterUnit.Stats.Mana;
		s.Log($"[GainMana] {casterUnit.Name} gains {Amount} mana (now {casterUnit.Stats.Mana}/{casterUnit.Stats.MaxMana}).");
	}
}

// ── Dirge Pulse ────────────────────────────────────────────────────────────────

/// <summary>
/// Deals damage and pushes all enemies within range of any spirit or memorial.
/// JSON: { "type": "dirge_pulse", "damage": n, "push": n }
/// </summary>
public sealed class DirgePulseEffect : EffectBase
{
	public int Damage;
	public int Push;
	public int CollisionDamage;

	public DirgePulseEffect(int damage, int push, int collisionDamage = 0)
	{
		Damage = damage;
		Push = push;
		CollisionDamage = collisionDamage;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		// Collect pulse origins — all spirit tiles and memorial tiles
		var pulseOrigins = new HashSet<Vector2I>();

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.IsSpirit || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;
			if (unit.SummonerTeamId != casterUnit.TeamId)
				continue;
			pulseOrigins.Add(unit.CurrentTile.Axial);
		}

		if (s.Memorials != null)
			foreach (var tile in s.Memorials.GetAllMemorials())
				pulseOrigins.Add(tile.Axial);

		if (pulseOrigins.Count == 0)
		{
			s.Log("[Dirge] No spirits or memorials on board — no effect.");
			return;
		}

		// Find all enemies within 2 of any origin
		var affected = new HashSet<Unit>();
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;
			if (unit.TeamId == casterUnit.TeamId)
				continue;

			foreach (var origin in pulseOrigins)
			{
				if (s.Grid.Distance(origin, unit.CurrentTile.Axial) <= 2)
				{
					affected.Add(unit);
					break;
				}
			}
		}

		foreach (var unit in affected)
		{
			unit.ApplyDamage(Damage);
			s.Log($"[Dirge] {unit.Name} takes {Damage} from the dirge.");
			// Push is handled by the movement system when push tiles > 0
		}
	}
}

// ── Hallow Tile ────────────────────────────────────────────────────────────────

/// <summary>
/// Hallows target tile — creates or upgrades a memorial to Hallowed state.
/// JSON: { "type": "hallow_tile", "duration": n, "auto_rise_range": n }
/// </summary>
public sealed class HallowTileEffect : EffectBase
{
	public int Duration;
	public int AutoRiseRange;

	public HallowTileEffect(int duration = 99, int autoRiseRange = 0)
	{
		Duration = duration;
		AutoRiseRange = autoRiseRange;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null)
				continue;

			s.Memorials.HallowTile(tile);
			s.Log($"[HallowTile] Tile {tile.Axial} hallowed.");
		}
	}
}

// ── Hallow Area ────────────────────────────────────────────────────────────────

/// <summary>
/// Hallows all tiles within radius of the caster.
/// JSON: { "type": "hallow_area", "radius": n }
/// </summary>
public sealed class HallowAreaEffect : EffectBase
{
	public int Radius;

	public HallowAreaEffect(int radius = 2)
	{
		Radius = radius;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null || s.Grid == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null)
			return;

		var center = casterUnit.CurrentTile.Axial;
		int count = 0;

		foreach (var kvp in s.Grid.Tiles)
		{
			if (s.Grid.Distance(center, kvp.Key) > Radius)
				continue;
			s.Memorials.HallowTile(kvp.Value);
			count++;
		}

		s.Log($"[HallowArea] Hallowed {count} tile(s) within radius {Radius}.");
	}
}

// ── Memorial Strike All ────────────────────────────────────────────────────────

/// <summary>
/// Each memorial on the board strikes adjacent enemies for damage.
/// JSON: { "type": "memorial_strike_all", "damage": n }
/// Optional: "push": n, "leave_memorial": true, "strikes": n, "global": false
/// </summary>
public sealed class MemorialStrikeAllEffect : EffectBase
{
	public int Damage;
	public int Push;
	public bool LeaveMemorial;
	public int Strikes;

	public MemorialStrikeAllEffect(int damage, int push = 0, bool leaveMemorial = false, int strikes = 1)
	{
		Damage = damage;
		Push = push;
		LeaveMemorial = leaveMemorial;
		Strikes = strikes;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null || s.Grid == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var memorials = s.Memorials.GetAllMemorials();
		int totalDamage = 0;

		foreach (var tile in memorials)
		{
			for (int strike = 0; strike < Strikes; strike++)
			{
				foreach (var neighbor in s.Grid.GetNeighbors(tile.Axial))
				{
					var neighborTile = s.Grid.GetTile(neighbor);
					if (neighborTile?.Occupant == null)
						continue;
					var unit = neighborTile.Occupant;
					if (unit.TeamId == casterUnit.TeamId)
						continue;

					unit.ApplyDamage(Damage);
					totalDamage += Damage;
					s.Log($"[MemorialStrike] Memorial at {tile.Axial} strikes {unit.Name} for {Damage}.");
				}
			}

			if (!LeaveMemorial)
				s.Memorials.ConsumeMemorial(tile);
		}

		s.Log($"[MemorialStrikeAll] {memorials.Count} memorial(s) fired. Total damage: {totalDamage}.");
	}
}

// ── Create Memorial Ground ─────────────────────────────────────────────────────

/// <summary>
/// Imbues target tile as Memorial Ground — summon spells here cost less.
/// JSON: { "type": "create_memorial_ground", "duration": n, "summon_discount": n }
/// </summary>
public sealed class CreateMemorialGroundEffect : EffectBase
{
	public int Duration;
	public int SummonDiscount;
	public int SpiritRegen;

	public CreateMemorialGroundEffect(int duration = 3, int summonDiscount = 2, int spiritRegen = 0)
	{
		Duration = duration;
		SummonDiscount = summonDiscount;
		SpiritRegen = spiritRegen;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		foreach (var obj in targets?.Items ?? new List<object>())
		{
			TileData tile = obj switch
			{
				TileData td => td,
				Unit u => u.CurrentTile,
				_ => null
			};

			if (tile == null)
				continue;

			// Hallow the tile and track discount via persistent effect
			s.Memorials.HallowTile(tile);
			tile.SummonDiscount = SummonDiscount;
			tile.SummonDiscountTurns = Duration;
			s.Log($"[MemorialGround] Tile {tile.Axial} is Memorial Ground (discount {SummonDiscount}, {Duration} turns).");
		}
	}
}

// ── Grief Discharge Damage ─────────────────────────────────────────────────────

/// <summary>
/// Spends all (or chosen amount of) Grief charges. Deals DamagePerGrief to all enemies per charge.
/// JSON: { "type": "grief_discharge_damage", "damage_per_grief": n }
/// Optional: "choose_amount": true, "min_spend": 1
/// </summary>
public sealed class GriefDischargeDamageEffect : EffectBase
{
	public int DamagePerGrief;

	public GriefDischargeDamageEffect(int damagePerGrief)
	{
		DamagePerGrief = damagePerGrief;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is not GriefAttunement grief)
			return;

		int charges = grief.Charges;
		if (charges <= 0)
		{
			s.Log("[GriefDischarge] No Grief to spend.");
			return;
		}

		int totalDamage = charges * DamagePerGrief;
		s.LastGriefSpent = charges;

		// Deal damage to all enemies
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.TeamId == casterUnit.TeamId)
				continue;
			unit.ApplyDamage(totalDamage);
		}

		// Reset grief
		grief.SetChargesDirectly(0);

		s.Log($"[GriefDischarge] Spent {charges} Grief — dealt {totalDamage} to all enemies.");
	}
}

// ── Apply Status To All Spirits ────────────────────────────────────────────────

/// <summary>
/// Applies a status to all friendly spirits.
/// JSON: { "type": "apply_status_to_all_spirits", "status": "undying_turn", "duration": 1 }
/// </summary>
public sealed class ApplyStatusToAllSpiritsEffect : EffectBase
{
	public string Status;
	public int Duration;
	public int ReviveHP;

	public ApplyStatusToAllSpiritsEffect(string status, int duration = 1, int reviveHP = 8)
	{
		Status = status;
		Duration = duration;
		ReviveHP = reviveHP;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		var spirits = s.UnitsInPlay
			.Where(u => u != null && u.IsSpirit && u.Stats.IsAlive && u.SummonerTeamId == casterUnit.TeamId)
			.ToList();

		foreach (var spirit in spirits)
		{
			switch (Status)
			{
				case "undying_turn":
					spirit.IsUndying = true;
					spirit.UndyingReviveHP = ReviveHP;
					spirit.UndyingTurns = Duration;
					break;
				case "undying_full_restore":
					spirit.IsUndying = true;
					spirit.UndyingFullRestore = true;
					spirit.UndyingTurns = Duration;
					break;
				case "invulnerable":
					spirit.IsInvulnerable = true;
					spirit.InvulnerableTurns = Duration;
					break;
				case "vigil":
					spirit.IsVigil = true;
					spirit.VigilTurns = Duration;
					break;
				default:
					spirit.ApplyStatus(Status, Duration);
					break;
			}
		}

		s.Log($"[StatusAllSpirits] Applied '{Status}' to {spirits.Count} spirit(s).");
	}
}

// ── Consume All Memorials Global ───────────────────────────────────────────────

/// <summary>
/// Consumes all memorials on the board. Per memorial consumed: gain mana and/or draw cards.
/// JSON: { "type": "consume_all_memorials_global", "mana_per": n, "draw_per": n }
/// </summary>
public sealed class ConsumeAllMemorialsGlobalEffect : EffectBase
{
	public int ManaPerMemorial;
	public int DrawPerMemorial;

	public ConsumeAllMemorialsGlobalEffect(int manaPerMemorial = 0, int drawPerMemorial = 0)
	{
		ManaPerMemorial = manaPerMemorial;
		DrawPerMemorial = drawPerMemorial;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		var memorials = s.Memorials.GetAllMemorials().ToList();

		foreach (var tile in memorials)
		{
			s.Memorials.ConsumeMemorial(tile);

			if (ManaPerMemorial > 0 && casterUnit != null)
			{
				casterUnit.GainMana(ManaPerMemorial);
				if (s.Mana.ContainsKey(caster))
					s.Mana[caster] = casterUnit.Stats.Mana;
			}

			if (DrawPerMemorial > 0 && casterUnit?.DeckData != null)
				casterUnit.DeckData.Draw(DrawPerMemorial);
		}

		s.Log($"[ConsumeAllMemorials] Released {memorials.Count} memorial(s). " +
			  $"+{memorials.Count * ManaPerMemorial} mana, drew {memorials.Count * DrawPerMemorial} card(s).");
	}
}

// ── Damage Per Memorial Global ─────────────────────────────────────────────────

/// <summary>
/// Deals DamagePer × memorial count to all enemies.
/// JSON: { "type": "damage_per_memorial_global", "damage_per": n }
/// </summary>
public sealed class DamagePerMemorialGlobalEffect : EffectBase
{
	public int DamagePer;
	public DamagePerMemorialGlobalEffect(int damagePer) { DamagePer = damagePer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Memorials == null)
			return;

		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
			return;

		int count = s.Memorials.CountMemorials();
		int damage = count * DamagePer;

		if (damage <= 0)
		{
			s.Log("[DamagePerMemorial] No memorials — no damage.");
			return;
		}

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.TeamId == casterUnit.TeamId)
				continue;
			unit.ApplyDamage(damage);
		}

		s.LastDamageDealt = damage;
		s.Log($"[DamagePerMemorial] {count} memorials × {DamagePer} = {damage} damage to all enemies.");
	}
}



// ============================================================================
// Arcanist Effects
// ============================================================================

/// <summary>
/// Adds <see cref="Amount"/> Charge to the caster's Grimoire. Overflow past the cap
/// is reported by the attunement (CombatManager turns it into card draw).
/// JSON: { "type": "gain_charge", "amount": n }
/// </summary>
public sealed class GainChargeEffect : EffectBase
{
	public int Amount;
	public GainChargeEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.Attunement is not ArcaneAttunement arc)
		{
			s.Log("[GainCharge] Caster has no Arcane attunement — ignored.");
			return;
		}

		int banked = arc.Add(Amount);
		s.Log($"[GainCharge] +{banked} charge (now {arc.Charge}/{ArcaneAttunement.MaxCharge}).");
	}
}

/// <summary>
/// Spends banked Charge as ammo, dealing <see cref="DamagePerCharge"/> per charge spent
/// to each target in the current set. Optionally costs the caster
/// <see cref="SelfDamagePerCharge"/> HP per charge spent (Overcharge). Spends between
/// <see cref="MinSpend"/> and <see cref="MaxSpend"/> charge (MaxSpend &lt;= 0 means "all").
/// JSON: { "type": "spend_charge_damage", "damage_per_charge": n,
///         "min_spend": 1, "max_spend": 0, "self_damage_per_charge": 0 }
/// NOTE: This deals the full per-charge total to every target. The "may split between
/// enemies" variant (Arcane Barrage) needs per-instance target selection and is handled
/// by the heavier barrage effect — see Arcanist_Design.md.
/// </summary>
public sealed class SpendChargeDamageEffect : EffectBase
{
	public int DamagePerCharge;
	public int MinSpend;
	public int MaxSpend;            // <= 0 means "spend all available"
	public int SelfDamagePerCharge;

	public SpendChargeDamageEffect(int damagePerCharge, int minSpend = 1, int maxSpend = 0, int selfDamagePerCharge = 0)
	{
		DamagePerCharge = damagePerCharge;
		MinSpend = Math.Max(0, minSpend);
		MaxSpend = maxSpend;
		SelfDamagePerCharge = Math.Max(0, selfDamagePerCharge);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.Attunement is not ArcaneAttunement arc)
		{
			s.Log("[SpendChargeDamage] Caster has no Arcane attunement — ignored.");
			return;
		}

		if (arc.Charge < MinSpend)
		{
			s.Log($"[SpendChargeDamage] Not enough charge ({arc.Charge} < {MinSpend}) — nothing spent.");
			return;
		}

		int want = MaxSpend > 0 ? Math.Min(MaxSpend, arc.Charge) : arc.Charge;
		int spent = arc.Spend(want);
		if (spent <= 0)
			return;

		int dmg = spent * DamagePerCharge;
		int hits = 0;

		if (targets != null)
		{
			foreach (var obj in targets.Items)
			{
				var unit = ResolveTargetUnit(s, obj);
				if (unit == null || !unit.Stats.IsAlive)
					continue;
				unit.ApplyDamage(dmg);
				hits++;
			}
		}

		s.LastDamageDealt = dmg;

		if (SelfDamagePerCharge > 0)
		{
			int recoil = spent * SelfDamagePerCharge;
			casterUnit.ApplyDamage(recoil);
			s.Log($"[SpendChargeDamage] Spent {spent} charge → {dmg} dmg to {hits} target(s); recoil {recoil} HP.");
		}
		else
		{
			s.Log($"[SpendChargeDamage] Spent {spent} charge → {dmg} dmg to {hits} target(s).");
		}
	}
}

/// <summary>
/// Deals <see cref="DamagePerSpell"/> per spell the Arcanist has cast this turn (read from
/// the Grimoire), with a floor of <see cref="Minimum"/>, to each target in the set.
/// The triggering card counts itself because "AbilityCast" fires when the card is pushed.
/// JSON: { "type": "damage_per_spell_cast", "amount": n, "min": m }
/// </summary>
public sealed class DamagePerSpellCastEffect : EffectBase
{
	public int DamagePerSpell;
	public int Minimum;

	public DamagePerSpellCastEffect(int damagePerSpell, int minimum = 0)
	{
		DamagePerSpell = damagePerSpell;
		Minimum = minimum;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		int count = (casterUnit?.Attunement as ArcaneAttunement)?.SpellsCastThisTurn ?? 0;

		int dmg = Math.Max(Minimum, DamagePerSpell * count);
		int hits = 0;

		if (targets != null)
		{
			foreach (var obj in targets.Items)
			{
				var unit = ResolveTargetUnit(s, obj);
				if (unit == null || !unit.Stats.IsAlive)
					continue;
				unit.ApplyDamage(dmg);
				hits++;
			}
		}

		s.LastDamageDealt = dmg;
		s.Log($"[DamagePerSpellCast] {count} spell(s) → {dmg} dmg to {hits} target(s) (min {Minimum}).");
	}
}

/// <summary>
/// Drains up to <see cref="Amount"/> mana from each target and gives the total to the caster.
/// JSON: { "type": "steal_mana", "amount": n }
/// NOTE: Reads/writes Unit.Stats.Mana directly. If your Unit exposes a dedicated
/// TrySpendMana / SetMana API, route through it instead of the raw field.
/// </summary>
public sealed class StealManaEffect : EffectBase
{
	public int Amount;
	public StealManaEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null || targets == null)
			return;

		int stolen = 0;
		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null)
				continue;

			int take = Math.Min(Amount, unit.Stats.Mana);
			if (take <= 0)
				continue;

			unit.Stats.Mana -= take;
			stolen += take;
		}

		if (stolen > 0)
		{
			casterUnit.GainMana(stolen);
			if (s.Mana.ContainsKey(caster))
				s.Mana[caster] = casterUnit.Stats.Mana;
		}

		s.Log($"[StealMana] Drained {stolen} mana to {casterUnit.Name}.");
	}
}

// ============================================================================
// Enchanter Effects
// ============================================================================

/// <summary>
/// Adds <see cref="Amount"/> Weave to the caster's working. If this reaches the cap the
/// attunement fires its Seventh Layer burst on its own.
/// JSON: { "type": "gain_weave", "amount": n }
/// </summary>
public sealed class GainWeaveEffect : EffectBase
{
	public int Amount;
	public GainWeaveEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit?.Attunement is not WeaveAttunement weave)
		{
			s.Log("[GainWeave] Caster has no Weave attunement — ignored.");
			return;
		}
		weave.Add(Amount);
		s.Log($"[GainWeave] +{Amount} Weave (now {weave.Weave}/{WeaveAttunement.MaxWeave}).");
	}
}

/// <summary>
/// Deals <see cref="DamagePer"/> per prepared glyph the caster's team has on the board,
/// floored at <see cref="Minimum"/>, to each target. Counts the existing tile.Glyph field.
/// JSON: { "type": "damage_per_glyph", "amount": n, "min": m }
/// </summary>
public sealed class DamagePerGlyphEffect : EffectBase
{
	public int DamagePer;
	public int Minimum;

	public DamagePerGlyphEffect(int damagePer, int minimum = 0)
	{
		DamagePer = damagePer;
		Minimum = minimum;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		int count = CountFriendlyGlyphs(s, casterUnit);

		int dmg = Math.Max(Minimum, DamagePer * count);
		int hits = 0;

		if (targets != null)
		{
			foreach (var obj in targets.Items)
			{
				var unit = ResolveTargetUnit(s, obj);
				if (unit == null || !unit.Stats.IsAlive)
					continue;
				unit.ApplyDamage(dmg);
				hits++;
			}
		}

		s.LastDamageDealt = dmg;
		s.Log($"[DamagePerGlyph] {count} glyph(s) → {dmg} dmg to {hits} target(s) (min {Minimum}).");
	}

	/// <summary>Counts prepared glyphs owned by the caster's team across the grid.</summary>
	internal static int CountFriendlyGlyphs(GameState s, Unit casterUnit)
	{
		if (s?.Grid?.Tiles == null)
			return 0;
		int teamId = casterUnit?.TeamId ?? 0;
		int count = 0;
		foreach (var kvp in s.Grid.Tiles)
		{
			var tile = kvp.Value;
			if (tile?.Glyph == null)
				continue;
			if (casterUnit == null || tile.Glyph.OwnerTeam == teamId)
				count++;
		}
		return count;
	}
}

// ============================================================================
// Chronomancer Effects
// ============================================================================




// ============================================================================
// Tinker Effects
// ============================================================================



// ============================================================================
// Druid Effects
// ============================================================================


// ── No-Op Effect ────────────────────────────────────────────────────────

/// <summary>Logs <see cref="Text"/> and does nothing else. Useful as a debug placeholder while authoring card data.</summary>
public sealed class NoOpEffect : EffectBase
{
	public string Text;
	public NoOpEffect(string t) { Text = t; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.Log($"[NoOp] {Text}");
	}
}
