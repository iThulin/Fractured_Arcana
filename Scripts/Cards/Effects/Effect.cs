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


internal static class InterfaceHelpers
{
	/// <summary>
	/// Status names treated as debuffs. Anything on a unit NOT in this set is counted as a "buff".
	/// </summary>
	internal static readonly HashSet<string> Debuffs = new()
	{
		"frozen", "rooted", "slowed", "stunned", "burn", "poisoned", "weakened",
		"blinded", "silenced", "cursed", "bound", "named", "mana_taxed", "geas", "hexed"
	};

	internal static TileData ResolveTile(GameState s, object obj)
	{
		if (obj is TileData td)
			return td;
		if (obj is HexTile hv && s?.Grid != null)
			return s.Grid.GetTile(hv.Axial);
		if (obj is Unit u)
			return u.CurrentTile;
		return null;
	}

	internal static IEnumerable<TileData> FriendlyGlyphTiles(GameState s, int team)
	{
		if (s?.Grid?.Tiles == null)
			yield break;
		foreach (var t in s.Grid.Tiles.Values)
			if (t?.Glyph != null && t.Glyph.OwnerTeam == team)
				yield return t;
	}

	internal static TileData NearestFriendlyGlyph(GameState s, int team, Vector2I from)
	{
		TileData best = null;
		int bestD = int.MaxValue;
		foreach (var t in FriendlyGlyphTiles(s, team))
		{
			int d = s.Grid.Distance(from, t.Axial);
			if (d < bestD)
			{ bestD = d; best = t; }
		}
		return best;
	}

	/// <summary>
	/// Places a standard enemy-enter glyph (damage + optional status) on a tile, mirroring PlaceGlyphEffect, and feeds the Weave attunement.
	/// </summary>
	internal static bool PlaceEnterGlyph(GameState s, Unit caster, TileData tile, int damage, string status, int statusDuration, bool reusable)
	{
		if (tile == null || tile.IsBlocked || tile.Glyph != null)
			return false;

		int dmg = damage + (caster?.BonusSpellDamage ?? 0);
		string st = status;
		int dur = statusDuration;
		bool reuse = reusable;

		tile.Glyph = new GlyphData
		{
			OwnerId = caster?.Name ?? "Enchanter",
			OwnerTeam = caster?.TeamId ?? 0,
			GameState = s,
			OnTrigger = (victim, state) =>
			{
				if (dmg > 0)
					victim.ApplyDamage(dmg);
				if (!string.IsNullOrEmpty(st))
					victim.ApplyStatus(st, dur);
				state.Log($"[Glyph] {victim.Name} triggers glyph: {dmg} dmg" + (st != null ? $", {st} {dur}t" : ""));
				// Reusable glyphs are re-armed by re-placing; Unit.PlaceOnTile clears on trigger.
				// Full reusable/duration handling needs the GlyphManager tick (see writeup).
			}
		};
		tile.TileView?.ShowGlyph();

		if (caster?.Attunement is WeaveAttunement w)
			w.OnGlyphPrepared();
		return true;
	}
}

/// <summary>Shared helpers for the Druid growth effects — center resolution and radius iteration, mirroring TerraformEffect's target handling.</summary>
internal static class GrowthEffectUtil
{
	/// <summary>Center axial from the first tile/unit in the target set, else the caster's tile.</summary>
	public static bool TryGetCenter(Unit casterUnit, TargetSet targets, out Vector2I center)
	{
		center = default;

		if (targets != null)
		{
			foreach (object obj in targets.Items)
			{
				switch (obj)
				{
					case TileData td:
						center = td.Axial;
						return true;
					case HexTile tv:
						center = tv.Axial;
						return true;
					case Unit u when u.CurrentTile != null:
						center = u.CurrentTile.Axial;
						return true;
				}
			}
		}

		if (casterUnit?.CurrentTile != null)
		{
			center = casterUnit.CurrentTile.Axial;
			return true;
		}
		return false;
	}

	public static IEnumerable<TileData> TilesInRadius(GameState s, Vector2I center, int radius)
	{
		foreach (KeyValuePair<Vector2I, TileData> kvp in s.Grid.Tiles)
		{
			if (s.Grid.Distance(center, kvp.Key) > radius)
				continue;
			if (kvp.Value != null)
				yield return kvp.Value;
		}
	}
}

// ════════════════════════════════════════════════════════════════
// ALMANAC — the scheduled-spell queue
// ════════════════════════════════════════════════════════════════

/// <summary>
/// One entry in the Chronomancer's Almanac (scheduled-spell queue).
/// Stored on <c>GameState.Almanac</c>. Ticked each player turn;
/// when <see cref="TurnsRemaining"/> reaches 0, the entry fires.
/// </summary>
public class AlmanacEntry
{
	/// <summary>Turns until this entry resolves. Decremented each player turn.</summary>
	public int TurnsRemaining;

	/// <summary>The effect to resolve when the entry fires.</summary>
	public IEffect Child;

	/// <summary>The original caster entity.</summary>
	public Entity Caster;

	/// <summary>The targets at scheduling time (snapshotted).</summary>
	public TargetSet Targets;

	/// <summary>The effect snapshot at scheduling time.</summary>
	public EffectSnapshot Snapshot;

	/// <summary>Display name shown in the turn-track UI (optional).</summary>
	public string Label;

	public bool IsReady => TurnsRemaining <= 0;

	/// <summary>Decrement the counter. Call once per player turn.</summary>
	public void Tick() => TurnsRemaining = Math.Max(0, TurnsRemaining - 1);
}

// ════════════════════════════════════════════════════════════════
// EFFECTS — leaf effects that do things. Each effect class is paired
// ════════════════════════════════════════════════════════════════

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

		// ── EffectSnapshot multiplier (EchoLast / RewindLast scaling) ───────────
		if (snap != null && Math.Abs(snap.DamageMultiplier - 1.0f) > 0.001f)
		{
			totalDamage = (int)Math.Round(totalDamage * snap.DamageMultiplier);
			s.Log($"[DamageMultiplier] Applied {snap.DamageMultiplier}x → {totalDamage}.");
		}

		// ── TemporalDecayField spell scaling bonus ───────────────────────────────
		var decayField = s.GetActiveEffect<TemporalDecayFieldPersistentEffect>(caster);
		if (decayField != null && decayField.CurrentScalingBonus > 0)
		{
			totalDamage += decayField.CurrentScalingBonus;
			s.Log($"[TemporalDecay] +{decayField.CurrentScalingBonus} scaling → {totalDamage}.");
		}

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


/// <summary>
/// Return N cards from discard to hand (most recent first), then optionally draw. 
/// JSON: { "type":"return_from_discard","count":n,"draw":m }
/// </summary>
public sealed class ReturnFromDiscardEffect : EffectBase
{
	public int Count, DrawN;
	public ReturnFromDiscardEffect(int count, int draw) { Count = count; DrawN = draw; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var d = FindCasterUnit(s, caster)?.DeckData;
		if (d == null)
			return;
		int n = Math.Min(Count, d.DiscardPile.Count), returned = 0;
		for (int i = 0; i < n; i++)
		{
			var c = d.DiscardPile[d.DiscardPile.Count - 1];
			d.DiscardPile.RemoveAt(d.DiscardPile.Count - 1);
			d.Hand.Add(c);
			returned++;
		}
		if (DrawN > 0)
			d.Draw(DrawN);
		s.Log($"[ReturnFromDiscard] returned {returned}, drew {DrawN}.");
	}
}

/// <summary>
/// Deal damage to each target, then gain Charge equal to the buffs on the (first) target, floored at min. 
/// JSON: { "type":"gain_charge_per_buff","min":n }
/// </summary>
public sealed class GainChargePerBuffEffect : EffectBase
{
	public int Minimum;
	public GainChargePerBuffEffect(int minimum) { Minimum = minimum; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var caster2 = FindCasterUnit(s, caster);
		int buffs = 0;
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{
				var u = ResolveTargetUnit(s, o);
				if (u?.Stats?.StatusEffects == null)
					continue;
				buffs = Math.Max(buffs, u.Stats.StatusEffects.Keys.Count(k => !InterfaceHelpers.Debuffs.Contains(k)));
			}
		int gain = Math.Max(Minimum, buffs);
		if (caster2?.Attunement is ArcaneAttunement a)
		{ a.Add(gain); s.Log($"[GainChargePerBuff] +{gain} charge."); }
	}
}

/// <summary>
/// Gain Charge scaled by keyword count. Card-context keyword introspection is pending; grants `multiplier` (min 1) as a stable stand-in. 
/// JSON: { "type":"gain_charge_per_keyword","multiplier":n }
/// </summary>
public sealed class GainChargePerKeywordEffect : EffectBase
{
	public int Multiplier;
	public GainChargePerKeywordEffect(int multiplier) { Multiplier = Math.Max(1, multiplier); }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var u = FindCasterUnit(s, caster);
		if (u?.Attunement is ArcaneAttunement a)
		{
			a.Add(Multiplier);
			s.Log($"[GainChargePerKeyword] +{Multiplier} charge (flat stand-in until card keyword context is threaded).");
		}
	}
}

/// <summary>
/// Grant armor/shield per spell cast this turn (read from ArcaneAttunement), capped at max. Auto-move portion pending a movement helper.
/// JSON: { "type":"move_per_spell_cast","max":n,"armor_per":n,"shield_per":n }
/// </summary>
public sealed class MovePerSpellCastEffect : EffectBase
{
	public int Max, ArmorPer, ShieldPer;
	public MovePerSpellCastEffect(int max, int armorPer, int shieldPer) { Max = max; ArmorPer = armorPer; ShieldPer = shieldPer; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var u = FindCasterUnit(s, caster);
		if (u == null)
			return;
		int spells = (u.Attunement is ArcaneAttunement a) ? a.SpellsCastThisTurn : 0;
		int n = Math.Min(Max, spells);
		if (ArmorPer > 0)
			u.Stats.Armor += ArmorPer * n;
		if (ShieldPer > 0)
			u.Stats.Shield += ShieldPer * n;
		u.RefreshHealthBar();
		s.Log($"[MovePerSpellCast] {spells} spells → +{ArmorPer * n} armor, +{ShieldPer * n} shield. (movement step pending move helper)");
	}
}

/// <summary>
/// Spend charge, deal flat damage; if lethal, mark the target exiled.
/// JSON: { "type":"disintegrate","damage":n,"charge_cost":n,"exile_on_lethal":bool }
/// </summary>
public sealed class DisintegrateEffect : EffectBase
{
	public int Damage, ChargeCost; public bool ExileOnLethal;
	public DisintegrateEffect(int damage, int chargeCost, bool exile) { Damage = damage; ChargeCost = chargeCost; ExileOnLethal = exile; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		if (c?.Attunement is ArcaneAttunement a)
		{
			if (a.Charge < ChargeCost)
			{ s.Log($"[Disintegrate] not enough charge ({a.Charge}/{ChargeCost})."); return; }
			a.SetChargesDirectly(a.Charge - ChargeCost);
		}
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null)
				continue;
			if (ExileOnLethal && u.Stats.Health + u.Stats.Shield + u.Stats.Armor <= Damage)
				u.ApplyStatus("exiled", 99); // necromancer resurrect should check "exiled"
			u.ApplyDamage(Damage);
			s.Log($"[Disintegrate] {Damage} to {u.Name}" + (ExileOnLethal ? " (exile on lethal)" : ""));
		}
	}
}

/// <summary>
/// Summons an Arcane Construct adjacent to the caster or on the targeted tile.
/// Constructs are autonomous units (HP/ATK/Speed from JSON) that persist until
/// killed or their duration expires. Duration is stored as a "construct" status
/// whose countdown needs a per-turn status hook (standard status processing).
/// JSON: { "type": "create_arcane_construct", "unit": "ArcaneConstruct",
///         "hp": n, "damage": n, "speed": n, "duration": n }
/// </summary>
public sealed class CreateArcaneConstructEffect : EffectBase
{
	public string UnitKind;
	public int HP, Damage, Speed, Duration;

	public CreateArcaneConstructEffect(string kind, int hp, int damage, int speed, int duration)
	{
		UnitKind = kind;
		HP = hp;
		Damage = damage;
		Speed = speed;
		Duration = duration;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{ s.Log("[CreateConstruct] No summon handler — cannot spawn."); return; }

		var casterUnit = FindCasterUnit(s, caster);
		int team = casterUnit?.TeamId ?? 0;
		int bonusDmg = casterUnit?.BonusSpellDamage ?? 0;

		var spawnTile = FindSpawnTile(s, casterUnit, targets);
		if (spawnTile == null)
		{ s.Log("[CreateConstruct] No valid spawn tile."); return; }

		var construct = s.OnSummonRequested(UnitKind, spawnTile, team);
		if (construct == null)
		{ s.Log("[CreateConstruct] Summon handler returned null."); return; }

		construct.Stats.MaxHealth = HP;
		construct.Stats.Health = HP;
		construct.Stats.BaseSpeed = Speed;
		construct.AttackDamage = Damage + bonusDmg;

		// Duration tracked as a status; status-system per-turn processing decrements it.
		// When "construct" reaches 0, the unit AI / status handler should kill the unit.
		if (Duration > 0)
			construct.ApplyStatus("construct", Duration);

		s.UnitsInPlay?.Add(construct);
		s.Log($"[CreateConstruct] {UnitKind} at {spawnTile.Axial} — {HP}HP / {construct.AttackDamage}ATK.");
	}

	private static TileData FindSpawnTile(GameState s, Unit caster, TargetSet targets)
	{
		// Prefer explicit target tile
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{
				var t = InterfaceHelpers.ResolveTile(s, o);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
					return t;
			}
		// Fall back to first empty neighbour of the caster
		if (caster?.CurrentTile != null && s.Grid != null)
			foreach (var coord in s.Grid.GetNeighbors(caster.CurrentTile.Axial))
			{
				var t = s.Grid.GetTile(coord);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
					return t;
			}
		return null;
	}
}

/// <summary>
/// Summons a Living Spell — a unit that embodies a spell and auto-casts it each
/// turn against the nearest enemy. The auto-cast AI lives on the unit side (not
/// in this effect); the effect handles the summoning and initial stats.
/// JSON: { "type": "summon_living_spell", "unit": "LivingSpell",
///         "hp": n, "damage": n, "duration": n }
/// </summary>
public sealed class SummonLivingSpellEffect : EffectBase
{
	public string UnitKind;
	public int HP, Damage, Duration;

	public SummonLivingSpellEffect(string kind, int hp, int damage, int duration)
	{
		UnitKind = kind;
		HP = hp;
		Damage = damage;
		Duration = duration;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{ s.Log("[SummonLivingSpell] No summon handler."); return; }

		var casterUnit = FindCasterUnit(s, caster);
		int team = casterUnit?.TeamId ?? 0;
		int bonusDmg = casterUnit?.BonusSpellDamage ?? 0;

		TileData spawnTile = null;
		if (casterUnit?.CurrentTile != null && s.Grid != null)
			foreach (var coord in s.Grid.GetNeighbors(casterUnit.CurrentTile.Axial))
			{
				var t = s.Grid.GetTile(coord);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
				{ spawnTile = t; break; }
			}

		if (spawnTile == null)
		{ s.Log("[SummonLivingSpell] No spawn tile."); return; }

		var spell = s.OnSummonRequested(UnitKind, spawnTile, team);
		if (spell == null)
			return;

		spell.Stats.MaxHealth = HP;
		spell.Stats.Health = HP;
		spell.AttackDamage = Damage + bonusDmg;

		if (Duration > 0)
			spell.ApplyStatus("living_spell", Duration);

		s.UnitsInPlay?.Add(spell);
		s.Log($"[SummonLivingSpell] {UnitKind} manifested at {spawnTile.Axial} ({HP}HP / {spell.AttackDamage}ATK). Auto-cast AI needs unit-side integration.");
	}
}

/// <summary>
/// Queues a spell modifier effect that will apply to the next N spells cast by the caster.
/// The modifier grants flat bonus damage, extra draw, and/or a status effect on hit.
/// </summary>
public sealed class QueueNextSpellModifierLeafEffect : EffectBase
{
	public int BonusDamage, ExtraDraw, AppliesTo, StatusDuration;
	public string GrantStatus;

	public QueueNextSpellModifierLeafEffect(int bd, int ed, int at, string gs, int sd)
	{
		BonusDamage = bd;
		ExtraDraw = ed;
		AppliesTo = Math.Max(1, at);
		GrantStatus = gs;
		StatusDuration = sd;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new QueuedSpellModifier(
			BonusDamage, ExtraDraw, AppliesTo, GrantStatus, StatusDuration, caster, unit));
		s.Log($"[QueueNextSpell] Queued +{BonusDamage} dmg / draw {ExtraDraw} on next {AppliesTo} spell(s).");
	}
}

public sealed class ChargeCostModifierLeafEffect : EffectBase
{
	public int ChargePerMana, Turns;
	public ChargeCostModifierLeafEffect(int cpm, int turns) { ChargePerMana = cpm; Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ChargeCostModifierAura(ChargePerMana, Turns, caster, unit));
		s.Log($"[ChargeCostModifier] Spells cost charge instead of mana for {Turns} turn(s).");
	}
}

public sealed class OmniscienceLeafEffect : EffectBase
{
	public int Turns, ExileOnExpire;
	public OmniscienceLeafEffect(int turns, int exile) { Turns = turns; ExileOnExpire = exile; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<OmniscienceEffect>(caster))
			return;
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new OmniscienceEffect(Turns, ExileOnExpire, caster, unit));
		s.Log($"[Omniscience] All spells free for {Turns} turn(s). {ExileOnExpire} exiled on expire.");
	}
}

public sealed class ArcaneApotheosisLeafEffect : EffectBase
{
	public int ChargePerSpell;
	public ArcaneApotheosisLeafEffect(int cps) { ChargePerSpell = Math.Max(1, cps); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<ArcaneApotheosisAura>(caster))
			return; // idempotent
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ArcaneApotheosisAura(ChargePerSpell, caster, unit));
		s.Log("[ArcaneApotheosis] Permanent: every spell you cast generates charge.");
	}
}

public sealed class BindCardLeafEffect : EffectBase
{
	public int Turns;
	public BindCardLeafEffect(int turns) { Turns = Math.Max(1, turns); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		if (unit?.DeckData == null)
			return;

		// Bind the first card in hand (card-selection UI is a future feature)
		var hand = unit.DeckData.Hand;
		if (hand.Count == 0)
		{ s.Log("[BindCard] Hand is empty — nothing to bind."); return; }

		var card = hand[0];
		hand.RemoveAt(0);

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new BoundCardAura(card, Turns, caster, unit));
		s.Log($"[BindCard] '{card.CardName}' bound for {Turns} turns; auto-casts each turn start.");
	}
}

public sealed class ReplicateLastSpellLeafEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ReplicateSpellAura(caster, unit));
		s.Log("[ReplicateLastSpell] Your next spell will echo once.");
	}
}



// ─────────────────────────────────────────────────────────────────────────────
//  CAST DECK TOP
//  Immediately resolve the top card of the deck against the current targets.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CastDeckTopEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		if (unit?.DeckData == null)
			return;

		var pile = unit.DeckData.DrawPile;
		if (pile.Count == 0 && unit.DeckData.DiscardPile.Count > 0)
			unit.DeckData.Reshuffle();
		if (pile.Count == 0)
		{ s.Log("[CastDeckTop] Deck is empty."); return; }

		var card = pile[0];
		pile.RemoveAt(0);
		if (card.TopHalf?.Effects == null || card.TopHalf.Effects.Length == 0)
		{
			unit.DeckData.DiscardPile.Add(card);
			s.Log($"[CastDeckTop] Top card '{card.CardName}' has no effects.");
			return;
		}

		s.Log($"[CastDeckTop] Auto-casting '{card.CardName}' from deck top.");
		foreach (var eff in card.TopHalf.Effects)
			eff.Resolve(s, caster, targets, snap);

		unit.DeckData.DiscardPile.Add(card);
	}
}

public sealed class ConvergenceLeafEffect : EffectBase
{
	public int Damage, Range, Turns;
	public ConvergenceLeafEffect(int dmg, int range, int turns) { Damage = dmg; Range = range; Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ConvergenceAura(Damage, Range, Turns, caster, unit));
		s.Log($"[Convergence] Each spell pulses {Damage} dmg to nearest enemy for {Turns} turn(s).");
	}
}

// ─────────────────────────────────────────────────────────────────────────────
//  PERFECT CARD
//  Permanently enhance a card's power. Full per-card selection needs a
//  card-choice UI step that is a separate feature; this grants a permanent
//  spell-damage boost to the caster for this combat as a stand-in.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PerfectCardEffect : EffectBase
{
	public int BonusDamage, Draw;
	public PerfectCardEffect(int bd, int draw) { BonusDamage = bd; Draw = draw; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		if (unit == null)
			return;
		if (BonusDamage > 0)
			unit.BonusSpellDamage += BonusDamage;
		if (Draw > 0)
			unit.DeckData?.Draw(Draw);
		s.Log($"[PerfectCard] +{BonusDamage} permanent spell damage this combat, drew {Draw}. (per-card selection pending UI)");
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

/// <summary>
/// Place enemy-enter glyphs on every tile within radius of the (first) target tile. 
/// JSON: { "type":"prepare_glyph_area","damage":n,"radius":n,"empty_only":bool }
/// </summary>
public sealed class PrepareGlyphAreaEffect : EffectBase
{
	public int Damage, Radius, StatusDuration; public string Status; public bool EmptyOnly, Reusable;
	public PrepareGlyphAreaEffect(int damage, int radius, string status, int statusDuration, bool emptyOnly, bool reusable)
	{ Damage = damage; Radius = radius; Status = status; StatusDuration = statusDuration; EmptyOnly = emptyOnly; Reusable = reusable; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		TileData center = null;
		if (targets?.Items != null && targets.Items.Count > 0)
			center = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		center ??= c?.CurrentTile;
		if (center == null || s?.Grid?.Tiles == null)
			return;
		int placed = 0;
		foreach (var t in s.Grid.Tiles.Values)
		{
			if (s.Grid.Distance(center.Axial, t.Axial) > Radius)
				continue;
			if (EmptyOnly && t.Occupant != null)
				continue;
			if (InterfaceHelpers.PlaceEnterGlyph(s, c, t, Damage, Status, StatusDuration, Reusable))
				placed++;
		}
		s.Log($"[PrepareGlyphArea] placed {placed} glyph(s) in radius {Radius}.");
	}
}

/// <summary>
/// Relocate the target enemy onto the nearest friendly glyph (Unit.PlaceOnTile fires the glyph). 
/// Simplified from directional push; refine once hex-step helpers are confirmed.
/// JSON: { "type":"push_to_glyph","tiles":n } / "pull_to_glyph"
/// </summary>
public sealed class MoveToGlyphEffect : EffectBase
{
	private readonly string _label;
	public MoveToGlyphEffect(string label) { _label = label; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		int team = c?.TeamId ?? 0;
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null)
				continue;
			var glyph = InterfaceHelpers.NearestFriendlyGlyph(s, team, u.CurrentTile?.Axial ?? default);
			if (glyph != null && glyph.Occupant == null)
			{ u.PlaceOnTile(glyph); s.Log($"[{_label}] moved {u.Name} onto a glyph."); }
			else
				s.Log($"[{_label}] no reachable friendly glyph for {u.Name}.");
		}
	}
}

/// <summary>
/// Remove up to Count buff statuses from each target; if Steal, the caster gains them. 
/// JSON: { "type":"dispel","count":n,"steal":bool }
/// </summary>
public sealed class DispelEffect : EffectBase
{
	public int Count; public bool Steal;
	public DispelEffect(int count, bool steal) { Count = count; Steal = steal; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u?.Stats?.StatusEffects == null)
				continue;
			var buffs = u.Stats.StatusEffects.Where(kv => !InterfaceHelpers.Debuffs.Contains(kv.Key))
											 .Select(kv => (kv.Key, kv.Value)).Take(Count).ToList();
			foreach (var (name, dur) in buffs)
			{
				u.RemoveStatus(name);
				if (Steal && c != null)
					c.ApplyStatus(name, dur);
			}
			s.Log($"[Dispel] removed {buffs.Count} buff(s) from {u.Name}" + (Steal ? " (stolen)." : "."));
		}
	}
}

/// <summary>
/// Swap the positions of two targeted units. 
/// JSON: { "type":"swap_units" }
/// </summary>
public sealed class SwapUnitsEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var units = new List<Unit>();
		if (targets?.Items != null)
			foreach (var o in targets.Items)
			{ var u = ResolveTargetUnit(s, o); if (u != null) units.Add(u); }
		if (units.Count < 2)
		{ s.Log("[SwapUnits] need two units."); return; }
		var a = units[0];
		var b = units[1];
		var ta = a.CurrentTile;
		var tb = b.CurrentTile;
		if (ta == null || tb == null)
			return;
		a.PlaceOnTile(tb);
		b.PlaceOnTile(ta);
		s.Log($"[SwapUnits] swapped {a.Name} and {b.Name}.");
	}
}

/// <summary>
/// Apply a status to each target (used for geas / mana_tithe — the on-move and on-cast hooks live in the status system). 
/// JSON: { "type":"geas",... } / "mana_tithe"
/// </summary>
public sealed class StatusApplyEffect : EffectBase
{
	private readonly string _status; private readonly int _duration; private readonly string _note;
	public StatusApplyEffect(string status, int duration, string note) { _status = status; _duration = duration; _note = note; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets?.Items == null)
			return;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null)
				continue;
			u.ApplyStatus(_status, _duration);
			s.Log($"[{_status}] applied to {u.Name} {_duration}t. {_note}");
		}
	}
}

/// <summary>
/// Prepares glyph(s). Backs three JSON types: "prepare_glyph" (one or `count` tiles),
/// "prepare_glyph_area" (every tile in `radius`), and "cascade_glyph" (an enter glyph with
/// `spread`). All glyph properties are read from JSON and written onto the GlyphData.
/// </summary>
public sealed class PrepareGlyphEffect : EffectBase
{
	public GlyphTrigger Trigger = GlyphTrigger.Enter;
	public int Damage, StatusDuration = 1, Duration = -1, Radius, Count = 1, CascadeSpread;
	public string Status;
	public bool Reusable, Invisible, EmptyOnly, AtOrigin, Area;
	public int AllyArmor, AllyShield, AllyDamage, AllyMana;
	public int OwnerDraw, OwnerMana, OwnerWeave, OwnerHeal;

	private void Configure(GlyphData g)
	{
		g.Trigger = Trigger;
		g.Damage = Damage;
		g.Status = Status;
		g.StatusDuration = StatusDuration;
		g.DurationTurns = Duration;
		g.Reusable = Reusable;
		g.Invisible = Invisible;
		g.Radius = Radius;
		g.CascadeSpread = CascadeSpread;
		g.AllyArmor = AllyArmor;
		g.AllyShield = AllyShield;
		g.AllyDamage = AllyDamage;
		g.AllyMana = AllyMana;
		g.OwnerDraw = OwnerDraw;
		g.OwnerMana = OwnerMana;
		g.OwnerWeave = OwnerWeave;
		g.OwnerHeal = OwnerHeal;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null)
		{ s?.Log("[PrepareGlyph] no GlyphManager on GameState."); return; }

		int placed = 0;
		if (Area)
		{
			TileData center = (targets?.Items?.Count > 0 ? InterfaceHelpers.ResolveTile(s, targets.Items[0]) : null) ?? owner?.CurrentTile;
			if (center == null || s.Grid?.Tiles == null)
				return;
			foreach (var t in s.Grid.Tiles.Values)
			{
				if (s.Grid.Distance(center.Axial, t.Axial) > Radius)
					continue;
				if (EmptyOnly && t.Occupant != null)
					continue;
				if (s.Glyphs.Prepare(t, owner, Configure) != null)
					placed++;
			}
		}
		else
		{
			if (AtOrigin && owner?.CurrentTile != null)
			{
				if (s.Glyphs.Prepare(owner.CurrentTile, owner, Configure) != null)
					placed++;
			}
			else if (targets?.Items != null)
			{
				foreach (var o in targets.Items)
				{
					if (placed >= Count)
						break;
					var tile = InterfaceHelpers.ResolveTile(s, o);
					if (tile != null && s.Glyphs.Prepare(tile, owner, Configure) != null)
						placed++;
				}
			}
		}
		s.Log($"[PrepareGlyph] placed {placed} glyph(s) [{Trigger}].");
	}
}

/// <summary>Link up to N friendly glyphs so triggering one triggers the group. { "type":"link_glyphs","count":n,"cumulative_bonus":n }</summary>
public sealed class LinkGlyphsEffect : EffectBase
{
	public int Count, CumulativeBonus;
	public LinkGlyphsEffect(int count, int bonus) { Count = count; CumulativeBonus = bonus; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		int id = s.Glyphs.Link(owner.TeamId, Count, CumulativeBonus);
		s.Log($"[LinkGlyphs] linked up to {Count} glyph(s) (id {id}).");
	}
}

/// <summary>Re-arm consumed friendly glyphs; optional empower. { "type":"rearm_glyphs","empower":n }</summary>
public sealed class RearmGlyphsEffect : EffectBase
{
	public int Empower;
	public RearmGlyphsEffect(int empower) { Empower = empower; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		int n = s.Glyphs.Rearm(owner.TeamId, Empower);
		s.Log($"[RearmGlyphs] re-armed {n} glyph(s)" + (Empower > 0 ? $" (+{Empower} dmg)." : "."));
	}
}

/// <summary>Fire all friendly glyphs at once. { "type":"trigger_all_glyphs","bonus_per_other":n,"consume":bool }</summary>
public sealed class TriggerAllGlyphsEffect : EffectBase
{
	public int BonusPerOther; public bool Consume;
	public TriggerAllGlyphsEffect(int bonus, bool consume) { BonusPerOther = bonus; Consume = consume; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null)
			return;
		s.Glyphs.TriggerAll(s, owner.TeamId, BonusPerOther, Consume);
	}
}

/// <summary>Swap two glyph tiles. { "type":"swap_glyphs" }</summary>
public sealed class SwapGlyphsEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var tiles = targets.Items.Select(o => InterfaceHelpers.ResolveTile(s, o)).Where(t => t != null).ToList();
		if (tiles.Count < 2)
		{ s.Log("[SwapGlyphs] need two tiles."); return; }
		s.Glyphs.Swap(tiles[0], tiles[1]);
		s.Log("[SwapGlyphs] swapped two glyph tiles.");
	}
}

/// <summary>Teleport caster onto the nearest friendly glyph. { "type":"teleport_to_glyph","trigger_on_arrive":bool }</summary>
public sealed class TeleportToGlyphEffect : EffectBase
{
	public bool TriggerOnArrive;
	public TeleportToGlyphEffect(bool trigger) { TriggerOnArrive = trigger; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var c = FindCasterUnit(s, caster);
		if (s?.Glyphs == null || c == null)
			return;
		var tile = s.Glyphs.NearestFriendly(c.TeamId, c.CurrentTile?.Axial ?? default);
		if (tile == null)
		{ s.Log("[TeleportToGlyph] no friendly glyph."); return; }
		c.PlaceOnTile(tile);
		s.Log($"[TeleportToGlyph] {c.Name} teleported to a glyph.");
		if (TriggerOnArrive && tile.Glyph != null)
		{
			tile.Glyph.Fire(c, s);                 // caster is friendly → ally payload / payoffs
			s.Glyphs.OnGlyphFired(s, tile, c);
			if (!tile.Glyph.Reusable)
				s.Glyphs.Remove(tile);
		}
	}
}

/// <summary>Permanent reusable ally-buff tiles (Sovereign Pillars). Enemy-adjacent aura is logged as pending. { "type":"enchant_pillar","count":n,"ally_all_stats":n,... }</summary>
public sealed class EnchantPillarEffect : EffectBase
{
	public int Count, AllyAll, EnemyDamageReduction; public string AuraStatus;
	public EnchantPillarEffect(int count, int allyAll, int enemyDr, string aura)
	{ Count = count; AllyAll = allyAll; EnemyDamageReduction = enemyDr; AuraStatus = aura; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || owner == null || targets?.Items == null)
			return;
		int placed = 0;
		foreach (var o in targets.Items)
		{
			if (placed >= Count)
				break;
			var tile = InterfaceHelpers.ResolveTile(s, o);
			if (tile == null)
				continue;
			if (s.Glyphs.Prepare(tile, owner, g =>
			{
				g.Trigger = GlyphTrigger.AllyEnter;
				g.Reusable = true;
				g.DurationTurns = -1;
				g.AllyArmor = AllyAll;
				g.AllyDamage = AllyAll;
				g.AllyShield = AllyAll;
			}) != null)
				placed++;
		}
		s.Log($"[EnchantPillar] raised {placed} permanent pillar(s). (enemy-adjacent aura pending per-turn aura hook)");
	}
}

/// <summary>A glyph that reflects the next spell on a unit standing on it. Placement works; reflection resolution needs a hook in the cast/targeting pipeline. { "type":"reflect_ward","triggers":n }</summary>
public sealed class ReflectWardEffect : EffectBase
{
	public int Triggers, Radius;
	public ReflectWardEffect(int triggers, int radius) { Triggers = triggers; Radius = radius; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var tile = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		if (tile == null)
			return;
		s.Glyphs.Prepare(tile, owner, g => { g.Trigger = GlyphTrigger.Manual; g.Status = "reflect"; g.StatusDuration = Triggers; g.DurationTurns = 3; });
		s.Log("[ReflectWard] placed. (spell-reflection resolution needs a cast-pipeline hook — see writeup)");
	}
}

/// <summary>A glyph that doubles the next spell cast while standing on it. Placement works; the cast-twice resolution needs the cast pipeline. { "type":"spell_anchor","casts":n }</summary>
public sealed class SpellAnchorEffect : EffectBase
{
	public int Casts;
	public SpellAnchorEffect(int casts) { Casts = casts; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null || targets?.Items == null)
			return;
		var tile = InterfaceHelpers.ResolveTile(s, targets.Items[0]);
		if (tile == null)
			return;
		s.Glyphs.Prepare(tile, owner, g => { g.Trigger = GlyphTrigger.SelfStand; g.Status = "anchor"; g.StatusDuration = Casts; g.DurationTurns = 3; });
		s.Log("[SpellAnchor] placed. (cast-twice resolution needs the cast pipeline — see writeup)");
	}
}

/// <summary>
/// Applies "dominated" status to each target enemy and spawns a DominateAura
/// to enforce the forced-attack each turn.
/// JSON: { "type": "dominate", "turns": n }
/// </summary>
public sealed class DominateEffect : EffectBase
{
	public int Turns;
	public DominateEffect(int turns) { Turns = Math.Max(1, turns); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (targets?.Items == null)
			return;

		bool dominated = false;
		foreach (var o in targets.Items)
		{
			var u = ResolveTargetUnit(s, o);
			if (u == null || u.TeamId == casterUnit?.TeamId)
				continue;
			u.ApplyStatus("dominated", Turns);
			s.Log($"[Dominate] {u.Name} is dominated for {Turns} turn(s).");
			dominated = true;
		}

		if (dominated && !s.HasActiveEffect<DominateAura>(caster))
		{
			s.ActiveEffects ??= new List<PersistentEffect>();
			s.ActiveEffects.Add(new DominateAura(Turns, caster, casterUnit));
		}
	}
}

/// <summary>
/// Summons a phantom duplicate of the caster with HpFraction of the caster's
/// max HP. The illusion unit carries an "illusion" status; apply one-hit-break
/// behaviour in Unit.ApplyDamage by checking HasStatus("illusion") and calling
/// Die() if any damage lands — that is a unit-side hook this effect cannot set.
/// JSON: { "type": "summon_illusion", "hp_fraction": 0.5, "duration": n }
/// </summary>
public sealed class SummonIllusionEffect : EffectBase
{
	public float HpFraction;
	public int Duration;

	public SummonIllusionEffect(float hpFrac, int dur)
	{
		HpFraction = Math.Clamp(hpFrac, 0.1f, 1f);
		Duration = Math.Max(1, dur);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{ s.Log("[SummonIllusion] No summon handler."); return; }

		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null)
			return;

		TileData spawnTile = null;
		if (casterUnit.CurrentTile != null && s.Grid != null)
			foreach (var coord in s.Grid.GetNeighbors(casterUnit.CurrentTile.Axial))
			{
				var t = s.Grid.GetTile(coord);
				if (t != null && !t.IsBlocked && !t.IsOccupied)
				{ spawnTile = t; break; }
			}

		if (spawnTile == null)
		{ s.Log("[SummonIllusion] No spawn tile."); return; }

		var illusion = s.OnSummonRequested("Illusion", spawnTile, casterUnit.TeamId);
		if (illusion == null)
			return;

		illusion.Stats.MaxHealth = Math.Max(1, (int)(casterUnit.Stats.MaxHealth * HpFraction));
		illusion.Stats.Health = illusion.Stats.MaxHealth;
		illusion.AttackDamage = casterUnit.AttackDamage;
		illusion.ApplyStatus("illusion", Duration);

		s.UnitsInPlay?.Add(illusion);
		s.Log($"[SummonIllusion] Phantom at {spawnTile.Axial} ({illusion.Stats.MaxHealth}HP). One-hit-break: add to Unit.ApplyDamage.");
	}
}

/// <summary>
/// Spawns a GrandDesignPersistentEffect. Glyph doubling is enforced in GlyphData.Fire
/// — add the 7-line check shown in the integration note above.
/// JSON: { "type": "grand_design_passive", "turns": n }
/// </summary>
public sealed class GrandDesignPassiveLeafEffect : EffectBase
{
	public int Turns;
	public GrandDesignPassiveLeafEffect(int turns) { Turns = Math.Max(1, turns); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<GrandDesignPersistentEffect>(caster))
			return;
		var unit = FindCasterUnit(s, caster);
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new GrandDesignPersistentEffect(Turns, caster, unit));
		s.Log($"[GrandDesign] Glyphs doubled for {Turns} turn(s). (add check to GlyphData.Fire — see note)");
	}
}

/// <summary>
/// Creates a persistent damage zone centred on the caster's current position.
/// JSON: { "type": "absolute_territory", "radius": n, "damage_per_turn": n, "turns": n }
/// </summary>
public sealed class AbsoluteTerritoryLeafEffect : EffectBase
{
	public int Radius, DamagePerTurn, Turns;

	public AbsoluteTerritoryLeafEffect(int r, int dpt, int t)
	{
		Radius = r;
		DamagePerTurn = dpt;
		Turns = t;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var unit = FindCasterUnit(s, caster);
		var center = unit?.CurrentTile?.Axial ?? default;
		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new AbsoluteTerritoryZone(center, Radius, DamagePerTurn, Turns, caster, unit));
		s.Log($"[AbsoluteTerritory] Zone r={Radius} / {DamagePerTurn}dpt / {Turns} turns centred on {center}.");
	}
}


// ============================================================================
// Chronomancer Effects
// ============================================================================

/// <summary>
/// Adds Foresight charges to the active caster's FateAttunement.
/// JSON: { "type": "gain_foresight", "amount": n }
/// </summary>
public sealed class GainForesightEffect : EffectBase
{
	public int Amount;
	public GainForesightEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is FateAttunement fate)
		{
			fate.GainCharges(Amount);
			s.Log($"[GainForesight] +{Amount} Foresight (now {fate.Charges}).");
		}
		else
		{
			s.Log("[GainForesight] No FateAttunement on caster — no-op.");
		}
	}
}

/// <summary>
/// Draws <see cref="Keep"/> cards to hand. Full card-selection UI is future
/// work — currently draws the top Keep cards without player choice.
/// Also grants +1 Foresight. Discount is stored for future UI implementation.
/// JSON: { "type": "scry", "look": n, "keep": n, "discount": n }
/// </summary>
public sealed class ScryEffect : EffectBase
{
	public int Look;
	public int Keep;
	public int Discount;

	public ScryEffect(int look, int keep, int discount)
	{
		Look = look;
		Keep = keep;
		Discount = discount;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null)
		{ s.Log("[Scry] No active caster — no-op."); return; }

		if (casterUnit.Attunement is FateAttunement fate)
		{
			fate.GainCharges(1);
			s.Log($"[Scry] Foresight +1 (now {fate.Charges}).");
		}

		if (casterUnit.DeckData != null)
		{
			casterUnit.DeckData.Draw(Keep);
			s.OnDrawCards?.Invoke(casterUnit);
			s.Log($"[Scry] Drew {Keep} card(s). (Look={Look}, Discount={Discount} pending UI)");
		}
	}
}

/// <summary>
/// Leaf that spawns a DelayedDamageEffect on GameState.ActiveEffects.
/// JSON: { "type": "delayed_damage", "amount": n, "turns": n }
/// </summary>
public sealed class DelayedDamageLeafEffect : EffectBase
{
	public int DamagePerTick;
	public int Ticks;

	public DelayedDamageLeafEffect(int damagePerTick, int ticks)
	{
		DamagePerTick = damagePerTick;
		Ticks = ticks;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null || s?.Grid == null)
		{ s?.Log("[DelayedDamage] No targets or grid."); return; }

		s.ActiveEffects ??= new List<PersistentEffect>();

		foreach (var obj in targets.Items)
		{
			Vector2I coord;
			if (obj is TileData td)
				coord = td.Axial;
			else if (obj is Unit u && u.CurrentTile != null)
				coord = u.CurrentTile.Axial;
			else
				continue;

			s.ActiveEffects.Add(new DelayedDamageEffect(coord, DamagePerTick, Ticks, caster));
			s.Log($"[DelayedDamage] Scheduled {DamagePerTick}×{Ticks} ticks at {coord}.");
		}
	}
}

/// <summary>
/// Ticks once per turn. Deals DamagePerTick to whatever enemy stands on
/// TargetCoord. Stored in GameState.ActiveEffects.
/// </summary>
public class DelayedDamageEffect : PersistentEffect
{
	public Vector2I TargetCoord;
	public int DamagePerTick;

	public DelayedDamageEffect(Vector2I coord, int damagePerTick, int ticks, Entity owner)
	{
		TargetCoord = coord;
		DamagePerTick = damagePerTick;
		TurnsRemaining = ticks;
		Owner = owner;
		// Entity can't be cast to Unit directly — match by name instead
		// _ownerTeamId is set externally via SetOwnerTeam if needed,
		// or resolved at tick time:
	}

	// Remove _ownerTeamId entirely and resolve team at tick time:
	public override void Tick(GameState s)
	{
		if (s?.Grid != null)
		{
			var tile = s.Grid.GetTile(TargetCoord);
			if (tile?.Occupant is Unit target && target.Stats.IsAlive)
			{
				// Find owner's team by name-matching against UnitsInPlay
				var ownerUnit = s.UnitsInPlay?.Find(u => u != null && u.Name == Owner?.Name);
				if (ownerUnit == null || target.TeamId != ownerUnit.TeamId)
				{
					target.ApplyDamage(DamagePerTick);
					s.Log($"[DelayedDamage] {target.Name} takes {DamagePerTick} at {TargetCoord}. " +
						  $"{TurnsRemaining - 1} tick(s) left.");
				}
			}
			else
			{
				s.Log($"[DelayedDamage] No valid target at {TargetCoord} — tick skipped.");
			}
		}
		TurnsRemaining--;
	}
}

/// <summary>
/// Reveals enemy intent (currently logs HP/status to console and grants
/// Foresight). Full HUD reveal is future UI work.
/// JSON: { "type": "peek_intent" }
/// </summary>
public sealed class PeekIntentEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;

		if (casterUnit?.Attunement is FateAttunement fate)
		{
			fate.GainCharges(1);
			s.Log($"[PeekIntent] Foresight +1 (now {fate.Charges}).");
		}

		if (s?.UnitsInPlay == null)
			return;

		s.Log("[PeekIntent] Enemy intel:");
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive)
				continue;
			if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
				continue;

			var statuses = unit.Stats.StatusEffects.Count > 0
				? string.Join(", ", unit.Stats.StatusEffects.Keys)
				: "none";
			s.Log($"  {unit.Name}: {unit.Stats.Health}/{unit.Stats.MaxHealth}HP " +
				  $"| Archetype={unit.EnemyArchetype} | Status=[{statuses}]");
		}
	}
}

/// <summary>
/// Grants a named stat boost for <see cref="Turns"/> turns.
/// Movement: adds directly to MovePoints this turn; registers a cleanup
///   callback on GameState.OnTurnEndCleanups for multi-turn restoration.
/// Action: adds to CurrentActionPoints immediately.
/// Damage: adds to BonusSpellDamage; registers cleanup.
/// JSON: { "type": "temp_buff", "stat": "movement"|"action"|"damage", "amount": n, "turns": n }
/// </summary>
public sealed class TempBuffEffect : EffectBase
{
	public string Stat;
	public int Amount;
	public int Turns;

	public TempBuffEffect(string stat, int amount, int turns)
	{
		Stat = stat;
		Amount = amount;
		Turns = turns;
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

			switch (Stat)
			{
				case "movement":
					unit.Stats.MovePoints += Amount;
					unit.RefreshHealthBar();
					s.Log($"[TempBuff] {unit.Name} +{Amount} movement (now {unit.Stats.MovePoints}).");

					if (Turns == 1)
					{
						// Single-turn: clean up at end of this turn
						int captured = Amount;
						var capturedUnit = unit;
						s.OnTurnEndCleanups ??= new List<Action>();
						s.OnTurnEndCleanups.Add(() =>
						{
							capturedUnit.Stats.MovePoints =
								Math.Max(0, capturedUnit.Stats.MovePoints - captured);
							capturedUnit.RefreshHealthBar();
						});
					}
					else
					{
						// Multi-turn: use a PersistentEffect for proper duration tracking
						s.ActiveEffects ??= new List<PersistentEffect>();
						s.ActiveEffects.Add(new MovementBuffEffect(unit, Amount, Turns));
					}
					break;

				case "action":
					unit.CurrentActionPoints += Amount;
					s.Log($"[TempBuff] {unit.Name} +{Amount} action(s) (now {unit.CurrentActionPoints}).");
					// Actions don't roll over — no cleanup needed (they're consumed or lost at turn end)
					break;

				case "damage":
					unit.BonusSpellDamage += Amount;
					s.Log($"[TempBuff] {unit.Name} +{Amount} spell damage (now {unit.BonusSpellDamage}).");

					int capturedDmg = Amount;
					var capturedDmgUnit = unit;
					s.OnTurnEndCleanups ??= new List<Action>();
					for (int i = 0; i < Turns; i++)
					{
						s.OnTurnEndCleanups.Add(() =>
						{
							capturedDmgUnit.BonusSpellDamage =
								Math.Max(0, capturedDmgUnit.BonusSpellDamage - capturedDmg);
						});
					}
					break;

				default:
					s.Log($"[TempBuff] Unknown stat '{Stat}' — no-op.");
					break;
			}
		}
	}
}

/// <summary>
/// Modifies mana costs.
///   "self_next"  — your next spell costs Amount less (consumed after one spell).
///   "enemy"      — enemy spells cost Amount more this round.
///
/// Reads/writes fields added to GameState:
///   GameState.NextSpellCostReduction (int)
///   GameState.EnemySpellCostIncrease (int)
///
/// TryCastWithTargets applies NextSpellCostReduction as an additional
/// manaDiscount. See ChronomancerWiringComplete.md §3.
/// JSON: { "type": "cost_modify", "amount": n, "scope": "self_next"|"enemy" }
/// </summary>
public sealed class CostModifyEffect : EffectBase
{
	public int Amount;
	public string Scope;

	public CostModifyEffect(int amount, string scope)
	{
		Amount = amount;
		Scope = scope;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		switch (Scope)
		{
			case "self_next":
				s.NextSpellCostReduction = Math.Max(s.NextSpellCostReduction, Amount);
				s.Log($"[CostModify] Next spell costs {Amount} less.");
				break;

			case "enemy":
				s.EnemySpellCostIncrease += Amount;
				s.Log($"[CostModify] Enemy spells cost +{Amount} this round.");
				// Reset in StartEnemyTurn — see wiring doc §7.
				break;

			default:
				s.Log($"[CostModify] Unknown scope '{Scope}' — no-op.");
				break;
		}
	}
}

/// <summary>
/// Adds <see cref="Turns"/> postponed-turn tokens to each target enemy.
/// In RunEnemyTurn, before ActEnemyUnit: if PostponedTurns > 0, skip
/// the unit and decrement. See wiring doc §5.
/// JSON: { "type": "postpone", "turns": n }
/// </summary>
public sealed class PostponeEffect : EffectBase
{
	public int Turns;
	public PostponeEffect(int turns) { Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;

		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null || unit.IsPlayerControlled)
				continue;

			unit.PostponedTurns += Turns;
			unit.ApplyStatus("delayed", Turns);
			s.Log($"[Postpone] {unit.Name} postponed {Turns} turn(s) (total: {unit.PostponedTurns}).");
		}
	}
}

/// <summary>
/// All living enemies skip their next <see cref="Turns"/> turns.
/// Uses the same PostponedTurns mechanism as PostponeEffect but applies
/// to every enemy simultaneously.
/// JSON: { "type": "skip_enemy_turn", "turns": n }
/// </summary>
public sealed class SkipEnemyTurnEffect : EffectBase
{
	public int Turns;
	public SkipEnemyTurnEffect(int turns) { Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.IsPlayerControlled)
				continue;
			if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
				continue;

			unit.PostponedTurns += Turns;
			unit.ApplyStatus("delayed", Turns);
			s.Log($"[SkipEnemyTurn] {unit.Name} skips {Turns} turn(s).");
		}
	}
}

/// <summary>
/// Schedules a child effect to resolve at the end of the player's turn
/// <see cref="Turns"/> rounds from now. Stored in GameState.Almanac.
/// The Almanac is ticked in StartPlayerTurn — see wiring doc §6.
/// JSON: { "type": "schedule", "turns": n, "do": { ...effect... } }
/// </summary>
public sealed class ScheduleLeafEffect : EffectBase
{
	public int Turns;
	public IEffect Child;

	public ScheduleLeafEffect(int turns, IEffect child)
	{
		Turns = turns;
		Child = child;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.Almanac ??= new List<AlmanacEntry>();
		s.Almanac.Add(new AlmanacEntry
		{
			TurnsRemaining = Math.Max(0, Turns),
			Child = Child,
			Caster = caster,
			Targets = targets,
			Snapshot = snap,
			Label = Child?.GetType().Name ?? "Scheduled"
		});
		s.Log($"[Schedule] Effect scheduled for {Turns} turn(s) from now.");
	}
}

/// <summary>
/// Moves the soonest Almanac entry one turn closer to firing.
/// If it was already at 1 (fires next turn), it fires immediately.
/// JSON: { "type": "advance" }
/// </summary>
public sealed class AdvanceEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.Almanac == null || s.Almanac.Count == 0)
		{
			s.Log("[Advance] No scheduled entries to advance.");
			return;
		}

		// Find the soonest entry belonging to the caster
		var entry = s.Almanac
			.Where(e => e.Caster == caster)
			.OrderBy(e => e.TurnsRemaining)
			.FirstOrDefault()
			?? s.Almanac.OrderBy(e => e.TurnsRemaining).First();

		entry.TurnsRemaining = Math.Max(0, entry.TurnsRemaining - 1);

		if (entry.IsReady)
		{
			s.Log($"[Advance] Entry reached 0 — firing immediately.");
			entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
			s.Almanac.Remove(entry);
		}
		else
		{
			s.Log($"[Advance] Entry moved to {entry.TurnsRemaining} turn(s) away.");
		}
	}
}

/// <summary>
/// Resolves the soonest caster Almanac entry immediately.
/// Spends 1 Foresight if the caster has a FateAttunement.
/// JSON: { "type": "fast_forward" }
/// </summary>
public sealed class FastForwardEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.Almanac == null || s.Almanac.Count == 0)
		{
			s.Log("[FastForward] No scheduled entries.");
			return;
		}

		var entry = s.Almanac
			.Where(e => e.Caster == caster)
			.OrderBy(e => e.TurnsRemaining)
			.FirstOrDefault()
			?? s.Almanac.OrderBy(e => e.TurnsRemaining).First();

		s.Log($"[FastForward] Firing scheduled entry immediately.");
		entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
		s.Almanac.Remove(entry);

		// Spend 1 Foresight
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is FateAttunement fate)
			fate.SpendCharges(1);
	}
}

/// <summary>
/// Marks the caster's current tile as an anchor for <see cref="Turns"/> turns.
/// Stores the coord on <c>Unit.AnchorCoord</c> and <c>Unit.AnchorTurnsRemaining</c>.
/// The anchor expires in <c>StartPlayerTurn</c> — see wiring doc §8.
/// JSON: { "type": "set_anchor", "turns": n }
/// </summary>
public sealed class SetAnchorEffect : EffectBase
{
	public int Turns;
	public SetAnchorEffect(int turns) { Turns = turns; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.CurrentTile == null)
		{
			s.Log("[SetAnchor] Caster has no tile — no-op.");
			return;
		}

		casterUnit.AnchorCoord = casterUnit.CurrentTile.Axial;
		casterUnit.AnchorTurnsRemaining = Turns;
		s.Log($"[SetAnchor] Anchor set at {casterUnit.AnchorCoord} for {Turns} turn(s).");
	}
}

/// <summary>
/// Teleports the caster to their stored anchor tile.
/// No-ops if no anchor is set or the anchor tile is occupied.
/// JSON: { "type": "teleport_to_anchor" }
/// </summary>
public sealed class TeleportToAnchorEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null || casterUnit.AnchorCoord == null)
		{
			s.Log("[TeleportToAnchor] No anchor set — no-op.");
			return;
		}

		var tile = s.Grid?.GetTile(casterUnit.AnchorCoord.Value);
		if (tile == null || !tile.CanEnter(casterUnit))
		{
			s.Log("[TeleportToAnchor] Anchor tile unavailable.");
			return;
		}

		casterUnit.CurrentTile?.ClearOccupant(casterUnit);
		casterUnit.PlaceOnTile(tile);
		s.Log($"[TeleportToAnchor] {casterUnit.Name} snapped back to {tile.Axial}.");
	}
}

/// <summary>
/// Registers up to <see cref="Count"/> tiles near the target as Phase tiles on
/// GameState.PhaseTiles. The caster may teleport between them for free once per
/// turn. The actual teleport is handled by <see cref="TeleportToPhaseTileEffect"/>.
/// JSON: { "type": "create_phase_tiles", "count": n, "turns": n }
/// </summary>
public sealed class CreatePhaseTilesEffect : EffectBase
{
	public int Count;
	public int Turns;

	public CreatePhaseTilesEffect(int count, int turns)
	{
		Count = count;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null)
			return;

		s.PhaseTiles ??= new List<Vector2I>();
		s.PhaseTileTurnsRemaining = Turns;

		// Use the target tiles, or fall back to nearby empty tiles
		int added = 0;
		if (targets?.Items != null)
		{
			foreach (var obj in targets.Items)
			{
				if (added >= Count)
					break;
				Vector2I coord;
				if (obj is TileData td)
					coord = td.Axial;
				else if (obj is Unit u && u.CurrentTile != null)
					coord = u.CurrentTile.Axial;
				else
					continue;

				if (!s.PhaseTiles.Contains(coord))
				{
					s.PhaseTiles.Add(coord);
					added++;
					s.Log($"[PhaseTiles] Added tile {coord}.");
				}
			}
		}

		s.Log($"[PhaseTiles] Network of {s.PhaseTiles.Count} tile(s) active for {Turns} turn(s).");
	}
}

/// <summary>
/// Teleports the caster to a phase tile.
/// Used by the Temporal Anchor and Phase Anchor snap-back mechanics.
/// JSON: { "type": "teleport_to_phase_tile" }
/// </summary>
public sealed class TeleportToPhaseTileEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit == null || s.PhaseTiles == null || s.PhaseTiles.Count == 0)
		{
			s.Log("[PhaseTile] No phase tiles registered — no-op.");
			return;
		}

		// Find the closest phase tile that isn't the caster's current position
		Vector2I? best = null;
		int bestDist = int.MaxValue;
		foreach (var coord in s.PhaseTiles)
		{
			if (casterUnit.CurrentTile != null && coord == casterUnit.CurrentTile.Axial)
				continue;
			int dist = s.Grid?.Distance(casterUnit.CurrentTile?.Axial ?? coord, coord) ?? 0;
			if (dist < bestDist)
			{ bestDist = dist; best = coord; }
		}

		if (best == null)
		{ s.Log("[PhaseTile] No valid destination."); return; }

		var tile = s.Grid.GetTile(best.Value);
		if (tile == null || !tile.CanEnter(casterUnit))
		{ s.Log("[PhaseTile] Destination blocked."); return; }

		casterUnit.CurrentTile?.ClearOccupant(casterUnit);
		casterUnit.PlaceOnTile(tile);
		s.Log($"[PhaseTile] {casterUnit.Name} teleported to {tile.Axial}.");
	}
}

/// <summary>
/// Directly re-resolves all effects of <c>GameState.LastResolvedItem</c>
/// using the original targets. <see cref="ValueMult"/> is stored on the
/// snapshot — DealDamageEffect reads <c>EffectSnapshot.DamageMultiplier</c>
/// and applies it. See wiring doc §9 for the DealDamageEffect hook.
/// JSON: { "type": "echo_last", "value_mult": f }
/// </summary>
public sealed class EchoLastEffect : EffectBase
{
	public float ValueMult;
	public EchoLastEffect(float valueMult) { ValueMult = valueMult; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var last = s.LastResolvedItem;
		if (last == null)
		{
			s.Log("[EchoLast] No last resolved item — no-op.");
			return;
		}

		// Build a modified snapshot with the value multiplier
		var echoSnap = new EffectSnapshot
		{
			DamageMultiplier = ValueMult,
			// Copy any other fields from the original snapshot as needed
		};

		s.Log($"[EchoLast] Echoing '{last.Ability?.Name}' at {ValueMult * 100f}% value.");
		foreach (var eff in last.Ability.Effects)
			eff.Resolve(s, last.Caster, last.Targets, echoSnap);
	}
}

/// <summary>
/// Re-resolves <c>GameState.LastResolvedItem</c>. When <see cref="Retarget"/>
/// is true, uses the new targets passed into this effect instead of the
/// original. Spends 1 Foresight.
/// JSON: { "type": "rewind_last", "retarget": bool }
/// </summary>
public sealed class RewindLastEffect : EffectBase
{
	public bool Retarget;
	public RewindLastEffect(bool retarget) { Retarget = retarget; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var last = s.LastResolvedItem;
		if (last == null)
		{
			s.Log("[RewindLast] Nothing to rewind — no-op.");
			return;
		}

		var resolveTargets = Retarget ? targets : last.Targets;
		s.Log($"[RewindLast] Rewinding '{last.Ability?.Name}'" +
			  (Retarget ? " with new targets." : " with original targets."));

		foreach (var eff in last.Ability.Effects)
			eff.Resolve(s, last.Caster, resolveTargets, last.Snapshot);

		// Spend 1 Foresight
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is FateAttunement fate)
			fate.SpendCharges(1);
	}
}

/// <summary>
/// Reverses the order of all items on GameStack. The item that would have
/// resolved last now resolves first. Requires GameStack.Reverse() — see wiring doc §10.
/// JSON: { "type": "reverse_stack" }
/// </summary>
public sealed class ReverseStackEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.StackCount() == 0)
		{
			s.Log("[ReverseStack] Stack is empty — no-op.");
			return;
		}

		s.Stack.Reverse();
		s.Log($"[ReverseStack] Stack reversed ({s.StackCount()} item(s)).");
	}
}

/// <summary>
/// Retargets the top enemy spell on the stack.
///   "random_enemy" — picks a random enemy other than the original target.
///   "chosen"       — uses the targets passed into this effect (costs 1 Foresight).
///   "decoy"        — redirects to the nearest live decoy.
///
/// Requires GameStack.PeekTop() — see wiring doc §11.
/// JSON: { "type": "redirect", "to": "random_enemy"|"chosen"|"decoy" }
/// </summary>
public sealed class RedirectEffect : EffectBase
{
	public string To;
	private static readonly Random _rng = new();

	public RedirectEffect(string to) { To = to; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var top = s.Stack.PeekTop();
		if (top == null)
		{
			s.Log("[Redirect] Stack is empty — no-op.");
			return;
		}

		var casterUnit = s.ActiveCasterUnit;

		Unit newTarget = null;

		switch (To)
		{
			case "random_enemy":
				{
					var originalItems = top.Targets?.Items ?? new List<object>();
					var candidates = s.UnitsInPlay
						.Where(u => u != null && u.Stats.IsAlive
								 && (casterUnit == null || u.TeamId != casterUnit.TeamId)
								 && !originalItems.Contains(u))
						.ToList();
					if (candidates.Count == 0)
						candidates = s.UnitsInPlay.Where(u => u != null && u.Stats.IsAlive
								 && (casterUnit == null || u.TeamId != casterUnit.TeamId)).ToList();
					if (candidates.Count > 0)
						newTarget = candidates[_rng.Next(candidates.Count)];
					break;
				}

			case "chosen":
				{
					newTarget = targets?.Items?.OfType<Unit>().FirstOrDefault();
					// Spend 1 Foresight for chosen redirect
					if (casterUnit?.Attunement is FateAttunement fate)
						fate.SpendCharges(1);
					break;
				}

			case "decoy":
				{
					newTarget = s.UnitsInPlay.FirstOrDefault(u => u != null && u.IsDecoy && u.Stats.IsAlive);
					break;
				}
		}

		if (newTarget == null)
		{
			s.Log($"[Redirect] No valid redirect target found (to={To}).");
			return;
		}

		top.Targets = new TargetSet { Items = new List<object> { newTarget } };
		s.Log($"[Redirect] Spell '{top.Ability?.Name}' redirected to {newTarget.Name}.");
	}
}

/// <summary>
/// Redirects an enemy's current movement/charge target.
/// Sets <c>Unit.RedirectedChargeTile</c> on the target unit.
/// CombatManager's ActSoldier/ActBrute reads this field and moves there
/// instead of toward the player. See wiring doc §12.
/// JSON: { "type": "redirect_charge", "to": "chosen" }
/// </summary>
public sealed class RedirectChargeEffect : EffectBase
{
	public string To;
	public RedirectChargeEffect(string to) { To = to; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		// Find the target enemy unit
		var enemy = targets?.Items?.OfType<Unit>()
			.FirstOrDefault(u => u != null && !u.IsPlayerControlled);

		if (enemy == null)
		{ s.Log("[RedirectCharge] No enemy target — no-op."); return; }

		// Find redirect destination: target tile or random walkable tile
		TileData dest = null;
		switch (To)
		{
			case "chosen":
				dest = targets?.Items?.OfType<TileData>().FirstOrDefault()
					?? targets?.Items?.Skip(1).OfType<Unit>().FirstOrDefault()?.CurrentTile;
				break;
		}

		if (dest != null && dest.CanEnter(enemy))
		{
			enemy.RedirectedChargeTile = dest.Axial;
			s.Log($"[RedirectCharge] {enemy.Name} charge redirected to {dest.Axial}.");
		}
		else
		{
			s.Log("[RedirectCharge] No valid charge destination — no-op.");
		}
	}
}

/// <summary>
/// Sets <c>GameState.RedirectAllTurnsRemaining</c> to <see cref="Turns"/>.
/// CombatManager.FindNearestPlayerUnit checks this flag and returns a random
/// other enemy unit instead of the nearest player unit. See wiring doc §13.
/// JSON: { "type": "redirect_all", "to": "random_enemy"|"chosen", "turns": n }
/// </summary>
public sealed class RedirectAllEffect : EffectBase
{
	public string To;
	public int Turns;

	public RedirectAllEffect(string to, int turns)
	{
		To = to;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.RedirectAllTurnsRemaining = Math.Max(s.RedirectAllTurnsRemaining, Turns);
		s.Log($"[RedirectAll] All enemy actions redirected for {Turns} turn(s).");
	}
}

/// <summary>
/// Adds an <see cref="ExtraTurnPersistentEffect"/> to GameState.ActiveEffects.
/// The Chronomancer gets a second turn each round with <see cref="Mana"/> mana
/// and draws <see cref="Draw"/> cards. See wiring doc §14 for the CombatManager hook.
/// JSON: { "type": "extra_turn", "mana": n, "draw": n }
/// </summary>
public sealed class ExtraTurnLeafEffect : EffectBase
{
	public int Mana;
	public int Draw;

	public ExtraTurnLeafEffect(int mana, int draw)
	{
		Mana = mana;
		Draw = draw;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		// Only one extra-turn effect at a time
		if (s.HasActiveEffect<ExtraTurnPersistentEffect>(caster))
		{
			s.Log("[ExtraTurn] Already active — no-op.");
			return;
		}

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new ExtraTurnPersistentEffect(Mana, Draw, caster));
		s.Log($"[ExtraTurn] Granted: {Mana} mana, draw {Draw} per extra turn.");
	}
}

/// <summary>
/// Spawns a decoy unit on the target tile using <c>GameState.OnSummonRequested</c>.
/// The decoy has <see cref="HP"/> health and expires after <see cref="Turns"/> turns
/// (a DelayedDamage effect kills it). Enemy units within a Redirect Aura's radius
/// must target the decoy instead of player units.
/// JSON: { "type": "summon_decoy", "hp": n, "turns": n }
/// </summary>
public sealed class SummonDecoyLeafEffect : EffectBase
{
	public int HP;
	public int Turns;

	public SummonDecoyLeafEffect(int hp, int turns)
	{
		HP = hp;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.OnSummonRequested == null)
		{
			s.Log("[SummonDecoy] OnSummonRequested not registered — no-op.");
			return;
		}

		TileData tile = null;
		if (targets?.Items != null)
		{
			foreach (var obj in targets.Items)
			{
				if (obj is TileData td)
				{ tile = td; break; }
				if (obj is Unit u && u.CurrentTile != null)
				{ tile = u.CurrentTile; break; }
			}
		}

		// Fall back to an empty tile adjacent to the caster
		if (tile == null && s.ActiveCasterUnit?.CurrentTile != null && s.Grid != null)
		{
			var casterTile = s.ActiveCasterUnit.CurrentTile;
			foreach (var kvp in s.Grid.Tiles)
			{
				if (s.Grid.Distance(casterTile.Axial, kvp.Key) == 1
					&& kvp.Value != null
					&& !kvp.Value.IsOccupied
					&& kvp.Value.IsWalkable)
				{
					tile = kvp.Value;
					break;
				}
			}
		}

		if (tile == null)
		{ s.Log("[SummonDecoy] No valid tile — no-op."); return; }

		var casterUnit = s.ActiveCasterUnit;
		int teamId = casterUnit?.TeamId ?? 0;

		// Pass HP via a special summon kind; CombatManager.RegisterSummonHandler
		// must handle "decoy" — see wiring doc §15.
		var decoy = s.OnSummonRequested("decoy", tile, teamId);
		if (decoy != null)
		{
			decoy.Stats.MaxHealth = HP;
			decoy.Stats.Health = HP;
			decoy.IsDecoy = true;

			// Schedule the decoy's death after Turns turns
			var killEntry = new AlmanacEntry
			{
				TurnsRemaining = Turns,
				Child = new LethalDamageEffect(decoy),
				Caster = caster,
				Targets = new TargetSet { Items = new List<object> { decoy } },
				Label = "Decoy Expire"
			};
			s.Almanac ??= new List<AlmanacEntry>();
			s.Almanac.Add(killEntry);

			s.Log($"[SummonDecoy] Spawned {HP}HP decoy at {tile.Axial}. Expires in {Turns} turns.");
		}
	}
}

/// <summary>
/// Kills a specific unit — used internally to expire decoys.
/// Not registered in the JSON registry.
/// </summary>
public sealed class LethalDamageEffect : EffectBase
{
	private readonly Unit _target;
	public LethalDamageEffect(Unit target) { _target = target; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (_target != null && Godot.GodotObject.IsInstanceValid(_target) && _target.Stats.IsAlive)
		{
			_target.ApplyDamage(_target.Stats.Health);
			s.Log($"[LethalDamage] Decoy {_target.Name} expired.");
		}
	}
}

/// <summary>
/// Spawns a <see cref="RedirectAuraPersistentEffect"/> on GameState.ActiveEffects.
/// While active, enemy single-target actions within Radius must target the
/// nearest live decoy unit instead of player units.
/// JSON: { "type": "redirect_aura", "radius": n, "turns": n }
/// </summary>
public sealed class RedirectAuraLeafEffect : EffectBase
{
	public int Radius;
	public int Turns;

	public RedirectAuraLeafEffect(int radius, int turns)
	{
		Radius = radius;
		Turns = turns;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;
		var center = casterUnit?.CurrentTile?.Axial ?? new Vector2I(0, 0);

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new RedirectAuraPersistentEffect(Radius, Turns, caster, center));
		s.Log($"[RedirectAura] Active within {Radius} for {Turns} turns.");
	}
}

/// <summary>
/// Spawns a <see cref="TemporalDecayFieldPersistentEffect"/> on GameState.ActiveEffects.
/// JSON: { "type": "temporal_decay_field", "damage": n, "scaling": n }
/// </summary>
public sealed class TemporalDecayFieldLeafEffect : EffectBase
{
	public int Damage;
	public int Scaling;

	public TemporalDecayFieldLeafEffect(int damage, int scaling)
	{
		Damage = damage;
		Scaling = scaling;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		// Only one decay field at a time
		if (s.GetActiveEffect<TemporalDecayFieldPersistentEffect>(caster) != null)
		{
			s.Log("[TemporalDecayField] Already active — no-op.");
			return;
		}

		var casterUnit = s.ActiveCasterUnit;
		bool enemiesOnly = false; // default: everyone decays. Upgrade to enemies-only handled by card upgrade.

		var effect = new TemporalDecayFieldPersistentEffect(Damage, Scaling, caster, enemiesOnly);
		effect.SetOwnerTeamId(casterUnit?.TeamId ?? 0);

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(effect);
		s.Log($"[TemporalDecayField] Board-wide decay: {Damage}/turn, scaling +{Scaling}/turn.");
	}
}

/// <summary>
/// Spawns an <see cref="EventControlPersistentEffect"/> that at the start of each
/// player turn automatically fast-forwards the nearest Almanac entry (or rewinds
/// the last resolved spell if no Almanac entries exist).
///
/// The "player chooses which event" UI will be a future upgrade. For now,
/// the logic auto-resolves the most beneficial action deterministically.
/// JSON: { "type": "event_control" }
/// </summary>
public sealed class EventControlLeafEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.HasActiveEffect<EventControlPersistentEffect>(caster))
		{
			s.Log("[EventControl] Already active — no-op.");
			return;
		}

		s.ActiveEffects ??= new List<PersistentEffect>();
		s.ActiveEffects.Add(new EventControlPersistentEffect(caster));
		s.Log("[EventControl] Permanent event control granted.");
	}
}

/// <summary>
/// Permanent PersistentEffect. Each player turn: if the Almanac has entries,
/// fast-forward the soonest one. Otherwise, echo the last resolved spell.
/// </summary>
public class EventControlPersistentEffect : PersistentEffect
{
	public int EventsPerTurn = 1;

	public EventControlPersistentEffect(Entity owner)
	{
		TurnsRemaining = 999;
		Owner = owner;
	}

	public override void Tick(GameState s)
	{
		for (int i = 0; i < EventsPerTurn; i++)
		{
			if (s.Almanac != null && s.Almanac.Count > 0)
			{
				var entry = s.Almanac.OrderBy(e => e.TurnsRemaining).First();
				s.Log($"[EventControl] Fast-forwarding '{entry.Label}'.");
				entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
				s.Almanac.Remove(entry);
			}
			else if (s.LastResolvedItem != null)
			{
				s.Log($"[EventControl] Echoing last spell '{s.LastResolvedItem.Ability?.Name}'.");
				foreach (var eff in s.LastResolvedItem.Ability.Effects)
					eff.Resolve(s, s.LastResolvedItem.Caster,
								s.LastResolvedItem.Targets, s.LastResolvedItem.Snapshot);
			}
		}
		// Never expire — permanent for the fight
	}
}

// ============================================================================
// Tinker Effects
// ============================================================================



// ============================================================================
// Druid Effects
// ============================================================================


// ── Seed Growth ──────────────────────────────────────────────────────────

/// <summary>
/// Plant living terrain at a stage on the target tile (and ring, if radius &gt; 0).
/// Terrain affinity is enforced inside GrowthManager.Seed (never takes root on fire).
/// Raises the caster's Wilding once per cast — not per tile — so a wide seed does not
/// spike straight into a Riot.
/// JSON: { "type": "seed_growth", "stage": 1, "radius": 0, "wilding": 1 }
/// </summary>
public sealed class SeedGrowthEffect : EffectBase
{
	public int Stage;
	public int Radius;
	public int Wilding;

	public SeedGrowthEffect(int stage, int radius, int wilding)
	{
		Stage = stage;
		Radius = radius;
		Wilding = wilding;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null)
			return;
		if (s.Growth == null)
		{ s.Log("[SeedGrowth] GameState.Growth is null — wire the GrowthManager."); return; }

		Unit casterUnit = FindCasterUnit(s, caster);
		if (!GrowthEffectUtil.TryGetCenter(casterUnit, targets, out Vector2I center))
			return;

		int seeded = 0;
		foreach (TileData tile in GrowthEffectUtil.TilesInRadius(s, center, Radius))
		{
			int before = tile.GrowthStage;
			s.Growth.Seed(tile, Stage, casterUnit, raiseWilding: false);
			if (tile.GrowthStage > before)
				seeded++;
		}

		if (Wilding > 0 && casterUnit?.Attunement is WildingAttunement w)
			w.GainCharges(Wilding);

		s.Log($"[SeedGrowth] Seeded {seeded} tile(s) at stage {Stage} (r{Radius}).");
	}
}

// ── Advance Growth ───────────────────────────────────────────────────────

/// <summary>
/// Force every living tile in radius up one stage, overriding the natural clock.
/// GrowthManager.AdvanceTile raises Wilding per advance, so a big advance can be the
/// thing that tips you into a Riot — intended.
/// JSON: { "type": "advance_growth", "radius": 1 }
/// </summary>
public sealed class AdvanceGrowthEffect : EffectBase
{
	public int Radius;
	public AdvanceGrowthEffect(int radius) { Radius = radius; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null || s.Growth == null)
			return;

		Unit casterUnit = FindCasterUnit(s, caster);
		if (!GrowthEffectUtil.TryGetCenter(casterUnit, targets, out Vector2I center))
			return;

		int advanced = 0;
		foreach (TileData tile in GrowthEffectUtil.TilesInRadius(s, center, Radius))
		{
			if (tile.GrowthStage <= 0)
				continue;
			int before = tile.GrowthStage;
			s.Growth.AdvanceTile(tile);
			if (tile.GrowthStage > before)
				advanced++;
		}

		s.Log($"[AdvanceGrowth] Advanced {advanced} living tile(s) within {Radius}.");
	}
}

// ── Spread Growth ────────────────────────────────────────────────────────

/// <summary>
/// Force an immediate spread tick from every Thicket+ source in radius (the active
/// version of the end-of-enemy-turn engine).
/// JSON: { "type": "spread_growth", "radius": 2 }
/// </summary>
public sealed class SpreadGrowthEffect : EffectBase
{
	public int Radius;
	public SpreadGrowthEffect(int radius) { Radius = radius; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null || s.Growth == null)
			return;

		Unit casterUnit = FindCasterUnit(s, caster);
		if (!GrowthEffectUtil.TryGetCenter(casterUnit, targets, out Vector2I center))
			return;

		var sources = new List<TileData>();
		foreach (TileData tile in GrowthEffectUtil.TilesInRadius(s, center, Radius))
			if (tile.GrowthStage >= GrowthManager.StageThicket)
				sources.Add(tile);

		foreach (TileData src in sources)
			s.Growth.SpreadFrom(src);

		s.Log($"[SpreadGrowth] Forced spread from {sources.Count} source tile(s).");
	}
}

// ── Entangle ─────────────────────────────────────────────────────────────

/// <summary>
/// Root every enemy standing on living ground within radius. Rooting funnels through
/// GrowthManager.RootUnit -> the injected status handler, so there is exactly one place
/// the "rooted" status API is wired.
/// JSON: { "type": "entangle", "radius": 2, "duration": 1 }
/// </summary>
public sealed class EntangleEffect : EffectBase
{
	public int Radius;
	public int Duration;

	public EntangleEffect(int radius, int duration)
	{
		Radius = radius;
		Duration = duration;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null || s.Growth == null)
			return;

		Unit casterUnit = FindCasterUnit(s, caster);
		if (!GrowthEffectUtil.TryGetCenter(casterUnit, targets, out Vector2I center))
			return;

		int rooted = 0;
		foreach (Unit unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;
			if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
				continue;   // enemies only
			if (unit.CurrentTile.GrowthStage <= 0)
				continue;                         // must be on living ground
			if (s.Grid.Distance(center, unit.CurrentTile.Axial) > Radius)
				continue;

			s.Growth.RootUnit(unit, Duration);
			rooted++;
		}

		s.Log($"[Entangle] Rooted {rooted} enemy(ies) for {Duration} on living ground.");
	}
}

// ── Harvest Growth ───────────────────────────────────────────────────────

/// <summary>
/// The panic button: clear living tiles in radius for heal/draw scaled by how many were
/// consumed, leaving fertile carcass ground behind (the spent grove enriches the soil).
/// Deliberately worse value than letting growth mature — composes the already-registered
/// HealEffect / DrawCardsEffect rather than reimplementing those APIs.
/// JSON: { "type": "harvest_growth", "radius": 2, "heal_per": 3, "draw_per": 0 }
/// </summary>
public sealed class HarvestGrowthEffect : EffectBase
{
	public int Radius;
	public int HealPer;
	public int DrawPer;

	public HarvestGrowthEffect(int radius, int healPer, int drawPer)
	{
		Radius = radius;
		HealPer = healPer;
		DrawPer = drawPer;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null || s.Growth == null)
			return;

		Unit casterUnit = FindCasterUnit(s, caster);
		if (!GrowthEffectUtil.TryGetCenter(casterUnit, targets, out Vector2I center))
			return;

		var living = new List<TileData>();
		foreach (TileData tile in GrowthEffectUtil.TilesInRadius(s, center, Radius))
			if (tile.GrowthStage > 0)
				living.Add(tile);

		int harvested = 0;
		foreach (TileData tile in living)
			if (s.Growth.Harvest(tile) > 0)
				harvested++;

		if (harvested == 0)
		{ s.Log("[HarvestGrowth] No living tiles to harvest."); return; }

		// Compose confirmed leaf effects so we don't reimplement heal/draw internals.
		// NOTE: HealEffect is assumed to heal the caster (matches the self-targeted "heal" usage).
		if (HealPer > 0)
			new HealEffect(HealPer * harvested).Resolve(s, caster, targets, snap);
		if (DrawPer > 0)
			new DrawCardsEffect(DrawPer * harvested).Resolve(s, caster, targets, snap);

		s.Log($"[HarvestGrowth] Harvested {harvested} tile(s): +{HealPer * harvested} heal, +{DrawPer * harvested} draw.");
	}
}

// ── Thornlash ────────────────────────────────────────────────────────────

/// <summary>
/// Damage each targeted enemy, scaled by the growth stage of the tile they stand on —
/// the more mature the ground beneath them, the more it hurts. Rewards a board you have
/// patiently grown rather than spent.
/// JSON: { "type": "thornlash", "damage": 3, "per_stage": 2 }
/// </summary>
public sealed class ThornlashEffect : EffectBase
{
	public int Damage;
	public int PerStage;

	public ThornlashEffect(int damage, int perStage)
	{
		Damage = damage;
		PerStage = perStage;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets == null)
			return;

		foreach (object obj in targets.Items)
		{
			Unit target = obj switch
			{
				Unit u => u,
				TileData td => td.Occupant as Unit,
				HexTile tv => s.Grid?.GetTile(tv.Axial)?.Occupant as Unit,
				_ => null
			};
			if (target == null || !target.Stats.IsAlive || target.CurrentTile == null)
				continue;

			int stage = target.CurrentTile.GrowthStage;
			int dmg = Damage + PerStage * stage;
			target.ApplyDamage(dmg);
			s.Log($"[Thornlash] {target.Name} takes {dmg} ({Damage} + {PerStage}x{stage} growth).");
		}
	}
}

// ── Gain Wilding ─────────────────────────────────────────────────────────

/// <summary>
/// Add Wilding charges to the active caster's WildingAttunement. Mirrors GainGriefEffect.
/// JSON: { "type": "gain_wilding", "amount": n }
/// </summary>
public sealed class GainWildingEffect : EffectBase
{
	public int Amount;
	public GainWildingEffect(int amount) { Amount = amount; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		Unit casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is WildingAttunement w)
		{
			w.GainCharges(Amount);
			s.Log($"[GainWilding] +{Amount} Wilding (now {w.Charges}).");
		}
	}
}

// ── Summon Wildlife ──────────────────────────────────────────────────────

/// <summary>
/// Request a wildlife summon at the target tile. "auto" lets the host terrain pick from
/// its bestiary pool (see growth_profiles.json + bestiary.json). Routes through
/// GrowthManager.SummonWildlifeAt -> the injected wildlife spawner, so it obeys the
/// "never AddChild inside an effect" rule. Cast-time gating (Old Growth present) is the
/// "old_growth_tile" requires entry, not this effect.
/// JSON: { "type": "summon_wildlife", "unit": "auto" }
/// </summary>
public sealed class SummonWildlifeEffect : EffectBase
{
	public string Unit;
	public SummonWildlifeEffect(string unit) { Unit = unit; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s?.Grid == null || s.Growth == null)
		{ s?.Log("[SummonWildlife] No GrowthManager wired."); return; }

		Unit casterUnit = FindCasterUnit(s, caster);
		if (!GrowthEffectUtil.TryGetCenter(casterUnit, targets, out Vector2I center))
			return;

		TileData tile = s.Grid.GetTile(center);
		if (tile == null)
			return;

		s.Growth.SummonWildlifeAt(tile, Unit);
		s.Log($"[SummonWildlife] Requested '{Unit}' at {center}.");
	}
}


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
