using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// Effect.cs
//
// Purpose:        EffectBase abstract class plus the CORE leaf
//                 (non-composite) effects shared by all schools —
//                 damage, heal, push, imbue, summon, status, etc.
//                 School-specific effects live in their own files
//                 (<School>Effects.cs). Each leaf is paired with a
//                 registry entry in CardScriptRegistry.
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
	// 2026-07-09: ActiveCasterUnit is authoritative when set — with per-unit
	// decks, PlayerA can be ANY party caster (a companion), not the main
	// character. This mirrors TargetingHelpers.FindCasterUnit; the old
	// PlayerA→PlayerUnit mapping made every companion spell resolve centered
	// on the main character. Resolver.ResolveTop pins ActiveCasterUnit from
	// StackItem.CasterUnit so stack-deferred casts (Reaction responses in
	// trigger windows) resolve against the right unit too.
	protected static Unit FindCasterUnit(GameState s, Entity caster)
	{
		if (s == null)
			return null;
		if (s.ActiveCasterUnit != null && caster == s.PlayerA)
			return s.ActiveCasterUnit;
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

		// NOTE (2026-08-01): a caster ENTITY is deliberately NOT resolved here.
		// Self-targeted casts put one in the target set, but so do `aoe` and
		// `global` ones (CombatManager's SelectAreaTarget / SelectGlobalTarget
		// cases both `Items.Add(Me)`), and six halves carry a direct damage or
		// status leaf under an aoe targeter. Mapping the token to the caster in
		// this shared helper would turn "does nothing" into "hits the caster",
		// which is strictly worse. The self-cast mapping is done locally by the
		// two effects that need it — see ApplyStatusEffect and ImbueTileEffect.
		return null;
	}

	/// <summary>True when <paramref name="targets"/> is the two-step aim shape —
	/// [victim Unit, aim TileData] from a unit_then_* targeter (2026-07-29). The tile
	/// in that shape is DIRECTION METADATA, not a second target: a damage or status
	/// leaf in the same sequence as an aimed push must not also hit whoever happens
	/// to stand on the tile the player used to point. Unit-facing leaf loops call
	/// this and skip Items[1]. Tile-consuming effects (imbue, glyph placement) do
	/// not — for them the tile may genuinely be the payload.</summary>
	protected static bool IsAimShape(TargetSet targets)
		=> targets?.Items != null
		   && targets.Items.Count == 2
		   && targets.Items[0] is Unit
		   && targets.Items[1] is TileData;
}

// ── Leaf effects ────────────────────────────────────────────────────────

/// <summary>Deals a flat amount of damage to every target in the target set. Also handles caster-side modifiers (empowered status, avatar aura bonus, equipment spell-damage), arcane-mark consumption, and chain bounce propagation when the caster has the "chaining" status.</summary>
public sealed class DealDamageEffect : EffectBase
{
	public int Amount;
	/// <summary>Arcane-mark consumption bonus — shared by Resolve and the R22 preview.</summary>
	public const int ArcaneMarkBonus = 3;
	public DealDamageEffect(int a) { Amount = a; }

	/// <summary>R22 damage preview: the caster-side damage computation, extracted
	/// so Resolve and the drag preview run the SAME code — never a parallel
	/// formula, so the preview structurally cannot drift. Pure (mutates nothing).
	/// <paramref name="log"/>=false silences the s.Log lines for preview calls.</summary>
	public int ComputeTotalDamage(GameState s, Unit casterUnit, Entity caster, EffectSnapshot snap, bool log = true)
	{
		// ── Bonus damage accumulation ────────────────────────────────────
		int bonus = 0;
		if (casterUnit != null && casterUnit.HasStatus("empowered"))
			bonus += 3;

		var avatarAura = s.GetActiveEffect<AvatarAuraEffect>(caster);
		if (avatarAura != null)
			bonus += avatarAura.BonusDamage;

		int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
		if (bonusSpellDmg > 0 && log)
			s.Log($"[SpellDamage] +{bonusSpellDmg} from equipment.");

		int totalDamage = Amount + bonus + bonusSpellDmg;

		// ── EffectSnapshot multiplier (EchoLast / RewindLast scaling) ───────────
		if (snap != null && Math.Abs(snap.DamageMultiplier - 1.0f) > 0.001f)
		{
			totalDamage = (int)Math.Round(totalDamage * snap.DamageMultiplier);
			if (log)
				s.Log($"[DamageMultiplier] Applied {snap.DamageMultiplier}x → {totalDamage}.");
		}

		// ── TemporalDecayField spell scaling bonus ───────────────────────────────
		var decayField = s.GetActiveEffect<TemporalDecayFieldPersistentEffect>(caster);
		if (decayField != null && decayField.CurrentScalingBonus > 0)
		{
			totalDamage += decayField.CurrentScalingBonus;
			if (log)
				s.Log($"[TemporalDecay] +{decayField.CurrentScalingBonus} scaling → {totalDamage}.");
		}
		return totalDamage;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		int hit = 0;
		if (targets == null)
		{ s?.Log($"[DealDamage] No targets."); return; }

		var casterUnit = FindCasterUnit(s, caster);
		int totalDamage = ComputeTotalDamage(s, casterUnit, caster, snap);

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
		bool aimShape = IsAimShape(targets);   // [victim, aim-tile]: the tile is not a target
		foreach (var obj in targets.Items)
		{
			Unit victim = null;

			if (aimShape && obj is TileData)
				continue;

			if (obj is Unit u)
			{
				u.ApplyDamage(totalDamage, s.ActiveCasterUnit);
				s.Log($"HIT unit {u.Name}");
				hit++;
				victim = u;
			}
			// Occupant captured BEFORE ApplyDamage: a lethal hit clears
			// tile.Occupant during death cleanup, so re-reads after the damage
			// NRE'd on kills and left victim null (2026-07-09 sweep).
			else if (obj is TileData td && td.Occupant != null)
			{
				victim = td.Occupant;
				victim.ApplyDamage(totalDamage, s.ActiveCasterUnit);
				s.Log($"HIT tile occupant {victim.Name} on {td.Axial}");
				hit++;
			}
			else if (obj is HexTile tileView)
			{
				var tileData = ResolveTileDataFromView(s, tileView);
				if (tileData != null && tileData.Occupant != null)
				{
					victim = tileData.Occupant;
					victim.ApplyDamage(totalDamage, s.ActiveCasterUnit);
					s.Log($"HIT tile occupant {victim.Name} on {tileData.Axial}");
					hit++;
				}
			}

			// Arcane mark: separate bonus, intentionally outside totalDamage.
			// (Constant shared with the R22 preview so it can't drift.)
			if (victim != null && victim.HasStatus("arcane_mark"))
			{
				victim.RemoveStatus("arcane_mark");
				victim.ApplyDamage(ArcaneMarkBonus, s.ActiveCasterUnit);
				s.Log($"[ArcaneMark] {victim.Name} takes {ArcaneMarkBonus} bonus damage. Mark consumed.");
			}
		}

		// Record for follow-up steps (heal_fraction_of_damage, grief_per_damage, ...).
		// Total across all targets — "the damage this step dealt".
		if (hit > 0)
			s.LastDamageDealt = totalDamage * hit;

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

		if (hit > 0)
			s.LastDamageDealt = totalDamage * hit;

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

/// <summary>Dual-purpose movement primitive: when targets is empty/self, grants the
/// caster +N move range for the turn (the movespeed currency — mobility only, spent
/// through the unit's own AP-gated moves and honored by EffectiveMovement); when
/// targets contains units, pushes each target N tiles away from the caster.</summary>
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
			// Self-movement — grant movespeed (per-turn move-range bonus). Previously
			// this wrote Stats.MovePoints, which no movement code read, so every
			// self-Dash card was inert. BonusMoveRange is read by EffectiveMovement.
			if (casterUnit != null)
			{
				casterUnit.Stats.BonusMoveRange += Tiles;
				s.Log($"[Dash] {casterUnit.Name} gains +{Tiles} move range this turn (now +{casterUnit.Stats.BonusMoveRange}).");
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

// ── Aimed displacement (2026-07-28) ─────────────────────────────────────
//
// PushEffect/PullEffect DERIVE their direction from the caster's position, which is
// why no card could ever say "in a direction you choose". These two read the
// direction (or the destination) the player picked, from the second slot of a
// two-step TargetSet — see SelectTwoStepTarget.
//
// TargetSet convention: Items[0] = victim Unit, Items[1] = chosen TileData.
// CombatManager guarantees the order and validates both picks before casting, so
// these do NOT re-validate reachability; they only re-check that the world still
// looks the way it did, because a Reaction may have moved things in between.

/// <summary>Shoves a unit <see cref="Tiles"/> steps along the axis from the unit to
/// a player-chosen ADJACENT tile. The chosen tile is the AIM, not the landing spot:
/// the shove walks that axis and stops on the first tile it cannot enter, exactly as
/// the derived-direction push does, so a wall still stops it and
/// <see cref="CollisionDamage"/> still applies.
/// JSON: { "type": "push_aimed", "tiles": n, "collision_damage": n }
/// with targeting { "type": "unit_then_direction", ... }</summary>
public sealed class PushAimedEffect : EffectBase
{
	public int Tiles;
	public int CollisionDamage;

	/// <summary>Flat damage dealt to the victim after the shove (2026-07-29). Exists
	/// so "push 2 and deal 3" cards (Gust) can be ONE aimed effect: authored as
	/// [push_aimed, damage] in a sequence, the damage leaf would also resolve against
	/// the aim TILE's occupant — Items[1] is a TileData and ResolveTargetUnit happily
	/// returns whoever stands there. Folding the damage in here keeps it on the
	/// victim alone.</summary>
	public int Damage;

	public PushAimedEffect(int tiles, int collisionDamage = 0, int damage = 0)
	{
		Tiles = tiles;
		CollisionDamage = collisionDamage;
		Damage = damage;
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (!TwoStep.Read(s, targets, "PushAimed", out var victim, out var aim))
			return;

		var from = victim.CurrentTile.Axial;
		var dir = aim.Axial - from;
		if (dir == Vector2I.Zero)
		{ s.Log("[PushAimed] the aim tile is the unit's own tile — no direction."); return; }

		var ctx = new MoveContext(s.Grid);
		int pushed = 0;
		bool collided = false;
		Unit collisionUnit = null;
		for (int i = 0; i < Tiles; i++)
		{
			if (ctx.HaltForced || ctx.ForcedTilesRemaining <= 0)
				break;
			var next = s.Grid.GetTile(victim.CurrentTile.Axial + dir);
			bool uphill = next != null && next.Height - victim.CurrentTile.Height >= 2;
			if (next == null || !next.CanEnter(victim) || uphill)
			{
				collided = true;
				if (next != null && !uphill && next.IsOccupied
					&& next.Occupant != null && next.Occupant.Stats.IsAlive
					&& next.Occupant != victim)
					collisionUnit = next.Occupant;
				break;
			}
			ctx.ForcedTilesRemaining--;
			victim.PlaceOnTile(next, MovementKind.Forced, ctx);
			pushed++;
			if (ctx.HaltForced) // Stone Anchors caught it, or the cap hit
				break;
		}

		s.Log($"[PushAimed] {victim.Name} shoved {pushed} tile(s)" +
			  (collided ? " — blocked." : "."));

		if (collided && CollisionDamage > 0)
		{
			victim.ApplyDamage(CollisionDamage);
			if (collisionUnit != null && collisionUnit.Stats.IsAlive)
			{
				// Mutual collision (spec §4.1) + chain shove depth-1 (§4.2) along the aim axis.
				collisionUnit.ApplyDamage(CollisionDamage);
				if (!ctx.HaltForced && ctx.ForcedTilesRemaining > 0
					&& collisionUnit.CurrentTile != null)
				{
					var chainNext = s.Grid.GetTile(collisionUnit.CurrentTile.Axial + dir);
					if (chainNext != null && chainNext.CanEnter(collisionUnit)
						&& chainNext.Height - collisionUnit.CurrentTile.Height < 2)
					{
						ctx.ForcedTilesRemaining--;
						collisionUnit.PlaceOnTile(chainNext, MovementKind.Forced, ctx);
						s.Log($"[PushAimed] chain — {collisionUnit.Name} shoved 1 tile further.");
					}
				}
			}
		}
		if (Damage > 0)
			victim.ApplyDamage(Damage);
	}
}

/// <summary>Relocates a unit to a player-chosen tile. "Move a construct you control
/// up to 3 tiles." Uses PlaceOnTile, NOT TryMoveTo: this is being moved, not walking
/// — it spends no AP, ignores move range (the targeter already bounded it), and by
/// design does NOT fire Unit.OnMoved, so an enemy binding_geas cannot tax a
/// displacement the player's own card performed.
/// JSON: { "type": "move_to_tile" } with targeting { "type": "unit_then_tile", ... }</summary>
public sealed class MoveToTileEffect : EffectBase
{
	public bool EndsTurn;

	public MoveToTileEffect(bool endsTurn = false) { EndsTurn = endsTurn; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (!TwoStep.Read(s, targets, "MoveToTile", out var victim, out var dest))
			return;

		if (dest.Occupant != null && dest.Occupant != victim)
		{ s.Log($"[MoveToTile] {dest.Axial} was taken before this resolved — no move."); return; }
		if (!dest.CanEnter(victim))
		{ s.Log($"[MoveToTile] {victim.Name} can no longer enter {dest.Axial} — no move."); return; }

		victim.PlaceOnTile(dest);
		s.Log($"[MoveToTile] {victim.Name} relocated to ({dest.Axial.X}, {dest.Axial.Y}).");

		if (EndsTurn)
		{
			victim.CurrentActionPoints = 0;
			victim.Stats.HasActed = true;
		}
	}
}

/// <summary>Shared reader for the two-step TargetSet convention. One place, so the
/// [victim, tile] ordering cannot drift between effects — and every failure says
/// which half was missing rather than returning silently, because "the card did
/// nothing" and "the targeting is wired wrong" must not look identical from outside
/// (U3c lesson 3).</summary>
internal static class TwoStep
{
	public static bool Read(GameState s, TargetSet targets, string tag,
							out Unit victim, out TileData tile)
	{
		victim = null; tile = null;
		if (s?.Grid == null)
		{ s?.Log($"[{tag}] no grid."); return false; }
		if (targets?.Items == null || targets.Items.Count < 2)
		{ s.Log($"[{tag}] needs a [unit, tile] TargetSet — got {targets?.Items?.Count ?? 0} item(s). " +
				"Is the card authored with a unit_then_* targeter?"); return false; }

		victim = targets.Items[0] as Unit;
		tile = targets.Items[1] as TileData;
		if (victim == null || !GodotObject.IsInstanceValid(victim) || !victim.Stats.IsAlive)
		{ s.Log($"[{tag}] the chosen unit is gone."); victim = null; return false; }
		if (victim.CurrentTile == null)
		{ s.Log($"[{tag}] {victim.Name} is not on the board."); victim = null; return false; }
		if (tile == null)
		{ s.Log($"[{tag}] the chosen tile did not survive to resolution."); return false; }
		return true;
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

			// Per-victim resolution scope (§2.2): each unit gets its own 10-tile
			// force budget and once-per-tile reaction guard.
			var ctx = new MoveContext(s.Grid);
			int pushed = 0;
			bool collided = false;
			Unit collisionUnit = null;

			for (int i = 0; i < Tiles; i++)
			{
				if (ctx.HaltForced || ctx.ForcedTilesRemaining <= 0)
					break;

				var current = victim.CurrentTile.Axial;
				int currentDist = s.Grid.Distance(casterPos, current);
				int fromHeight = victim.CurrentTile.Height;

				TileData bestTile = null;
				int bestDist = -1;
				Unit outwardBlocker = null;
				int blockerDist = -1;

				foreach (var neighbor in s.Grid.GetNeighbors(current))
				{
					var td = s.Grid.GetTile(neighbor);
					if (td == null)
						continue;

					int distFromCaster = s.Grid.Distance(casterPos, neighbor);
					if (distFromCaster <= currentDist)
						continue; // only tiles farther from the caster

					// Force-moving uphill by ≥2 is illegal (spec §4.3): a cliff, not a lane.
					bool uphillIllegal = td.Height - fromHeight >= 2;

					if (td.CanEnter(victim) && !uphillIllegal)
					{
						if (distFromCaster > bestDist)
						{
							bestDist = distFromCaster;
							bestTile = td;
						}
					}
					else if (!uphillIllegal && td.IsOccupied
							 && td.Occupant != null && td.Occupant.Stats.IsAlive
							 && td.Occupant != victim)
					{
						// A living unit blocks the outward path — a collision candidate.
						if (distFromCaster > blockerDist)
						{
							blockerDist = distFromCaster;
							outwardBlocker = td.Occupant;
						}
					}
				}

				if (bestTile != null)
				{
					ctx.ForcedTilesRemaining--;
					victim.PlaceOnTile(bestTile, MovementKind.Forced, ctx);
					pushed++;
					if (ctx.HaltForced) // Stone Anchors caught it, or the cap hit
						break;
				}
				else
				{
					collided = true;
					collisionUnit = outwardBlocker;
					break;
				}
			}

			if (collided && CollisionDamage > 0)
			{
				victim.ApplyDamage(CollisionDamage);
				if (collisionUnit != null && collisionUnit.Stats.IsAlive)
				{
					// Mutual collision (spec §4.1): the unit slammed into takes it too.
					collisionUnit.ApplyDamage(CollisionDamage);
					// Chain shove depth-1 (spec §4.2): pass 1 tile of push to the occupant.
					if (!ctx.HaltForced && ctx.ForcedTilesRemaining > 0)
						ChainShoveOne(s, casterPos, collisionUnit, ctx);
				}
				s.Log($"[Push] {victim.Name} pushed {pushed} tile(s), collided for {CollisionDamage} damage!");
			}
			else
			{
				s.Log($"[Push] {victim.Name} pushed {pushed} tile(s).");
			}
		}
	}

	/// <summary>Chain shove (tile_interaction_spec §4.2), depth 1: shove a unit one
	/// tile directly outward from the caster. Shares the MoveContext so the
	/// occupant's own entry verbs / slide fire, but never triggers a further chain.</summary>
	private static void ChainShoveOne(GameState s, Vector2I casterPos, Unit occ, MoveContext ctx)
	{
		if (occ?.CurrentTile == null)
			return;

		var current = occ.CurrentTile.Axial;
		int currentDist = s.Grid.Distance(casterPos, current);
		int fromHeight = occ.CurrentTile.Height;

		TileData bestTile = null;
		int bestDist = -1;
		foreach (var neighbor in s.Grid.GetNeighbors(current))
		{
			var td = s.Grid.GetTile(neighbor);
			if (td == null || !td.CanEnter(occ))
				continue;
			int distFromCaster = s.Grid.Distance(casterPos, neighbor);
			if (distFromCaster <= currentDist)
				continue;
			if (td.Height - fromHeight >= 2)
				continue; // uphill illegal
			if (distFromCaster > bestDist)
			{
				bestDist = distFromCaster;
				bestTile = td;
			}
		}

		if (bestTile != null)
		{
			ctx.ForcedTilesRemaining--;
			occ.PlaceOnTile(bestTile, MovementKind.Forced, ctx);
			s.Log($"[Push] chain — {occ.Name} shoved 1 tile further.");
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

			// Pull is positioning, not a hazard: suppress falling and keep the
			// no-collision asymmetry (spec §4). Verbs (Fire Sears, Frost Slides,
			// Stone Anchors) still apply on the forced entry.
			var ctx = new MoveContext(s.Grid) { SuppressFalling = true };
			int pulled = 0;

			for (int i = 0; i < Tiles; i++)
			{
				if (ctx.HaltForced || ctx.ForcedTilesRemaining <= 0)
					break;

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
					ctx.ForcedTilesRemaining--;
					victim.PlaceOnTile(bestTile, MovementKind.Forced, ctx);
					pulled++;
					if (ctx.HaltForced) // Stone Anchors caught it, or the cap hit
						break;
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
			else if (obj is Entity ent)
			{
				// "Imbue YOUR tile" (2026-08-01). A self-targeted cast puts the
				// caster ENTITY in the target set, not a Unit — Entity is a bare
				// `{ string Name }` token — so this loop used to `continue` past it
				// and the effect silently did nothing. Earth Anchor granted its armor
				// and imbued no ground; the card looked half-working, not broken.
				//
				// Handled HERE rather than in the shared ResolveTargetUnit because
				// `aoe` and `global` targeters put the same token in their sets, and
				// six halves carry a direct damage leaf under one. Widening the shared
				// helper would turn those from "does nothing" into "hits the caster".
				tile = FindCasterUnit(s, ent)?.CurrentTile;
			}

			if (tile == null)
				continue;

			// R22 sim gate: the preview must not really imbue the tile — but the
			// imbue's immediate tick damage below still runs (ApplyDamage is
			// itself gated, so the tick lands in the sim ledger).
			if (!CombatSim.Active)
			{
				tile.ElementType = elementType;
				tile.ElementStrength = 1.0f;

				if (elementType == TileElementType.Fire)
					tile.IsHazardous = true;

				// Use the existing visual system to update the tile
				tile.TileView?.SetElement(elementType);
			}

			s.Log($"[ImbueTile] {tile.Axial} imbued with {Element} ({elementType}).");

			// Capture the occupant ONCE: if the imbue damage is lethal, death
			// cleanup clears tile.Occupant between ApplyDamage and the log line
			// (NRE, 2026-07-09 — surfaced once drop-on-unit made direct casts
			// at enemies routine).
			var victim = tile.Occupant;
			if (BonusDamage > 0 && victim != null && victim.TeamId != 0)
			{
				var casterUnit = FindCasterUnit(s, caster);
				int bonusSpellDmg = casterUnit?.BonusSpellDamage ?? 0;
				int totalImbueDmg = BonusDamage + bonusSpellDmg;
				victim.ApplyDamage(totalImbueDmg);
				s.Log($"[ImbueTile] {Element} deals {totalImbueDmg} to {victim.Name}.");
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
		bool aimShape = IsAimShape(targets);   // [victim, aim-tile]: the tile is not a target
		foreach (var obj in targets.Items)
		{
			if (aimShape && obj is TileData)
				continue;
			// A self-targeted cast hands us the caster ENTITY rather than a Unit
			// (see the note in ResolveTargetUnit). Mapped here and not there, because
			// aoe/global casts pass the same token to leaves that would then hit the
			// caster. "You are Rooted" is unambiguous about who it means.
			var victim = obj is Entity ent ? FindCasterUnit(s, ent) : ResolveTargetUnit(s, obj);
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

	/// <summary>Flat stat bumps applied to each spawned unit (2026-07-29). Exists so
	/// upgrade tiers can patch a summon card's OUTPUT — the unit stats live in the
	/// summon handler's registry, which card-JSON field patches cannot reach, so
	/// "the turret arrives sturdier" needs a knob on the card side. This is the knob
	/// the new Tinker T1 tiers patch.</summary>
	public int HpBonus;
	public int DamageBonus;

	public SummonEffect(string kind, int count, int hpBonus = 0, int damageBonus = 0)
	{ UnitKind = kind; Count = count; HpBonus = hpBonus; DamageBonus = damageBonus; }

	/// <summary>
	/// Summons that CHANGE THE GROUND they arrive on. A stone pillar is a chunk of
	/// rock erupting through the turf: the tile under it is stone afterwards, it
	/// should read that way, and it should be consumable by anything that eats Earth
	/// tiles.
	///
	/// Keyed on the summon KIND rather than written into each card, because it is a
	/// property of the thing and not of the spell that made it. Three cards summon a
	/// stone pillar today (Boulder Hurl, Ember Bolt, Frost Lance) and all three should
	/// behave identically without three JSON edits to keep in sync.
	/// </summary>
	private static readonly Dictionary<string, TileElementType> SpawnImbues = new()
	{
		["stone_pillar"] = TileElementType.Earth,
	};

	/// <summary>
	/// Applies <see cref="SpawnImbues"/> for a freshly spawned summon. Mirrors
	/// ImbueTileEffect's write exactly — element, strength, TileView refresh — so a
	/// tile imbued this way is indistinguishable from one imbued by a spell.
	/// </summary>
	private static void ImbueForSummon(GameState s, string kind, TileData tile)
	{
		if (s == null || tile == null || kind == null) return;
		if (!SpawnImbues.TryGetValue(kind, out var element)) return;

		// Same sim gate as ImbueTileEffect: a damage PREVIEW must not really change
		// the board.
		if (CombatSim.Active) return;

		tile.ElementType = element;
		tile.ElementStrength = 1.0f;
		tile.TileView?.SetElement(element);

		s.Log($"[Summon] {tile.Axial} imbued with {element} by {kind}.");
	}

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
				if (HpBonus > 0)
				{
					spawned.Stats.MaxHealth += HpBonus;
					spawned.Stats.Health += HpBonus;
				}
				if (DamageBonus > 0)
					spawned.AttackDamage += DamageBonus;
				if (HpBonus > 0 || DamageBonus > 0)
					spawned.RefreshHealthBar();
				s.UnitsInPlay.Add(spawned);
				s.Log($"[Summon] Spawned {UnitKind} at {spawnTile.Axial}" +
					  (HpBonus > 0 || DamageBonus > 0 ? $" (+{HpBonus}HP/+{DamageBonus}DMG)." : "."));

				ImbueForSummon(s, UnitKind, spawnTile);
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

			// Push any unit on the tile (ground rising under them).
			// Captured once — lethal crush clears tile.Occupant mid-block.
			var crushed = tile.Occupant;
			if (crushed != null)
			{
				crushed.ApplyDamage(HeightIncrease * 2);
				s.Log($"[RaiseTerrain] {crushed.Name} crushed by rising ground for {HeightIncrease * 2} damage.");
			}

			s.Log($"[RaiseTerrain] {tile.Axial} raised by {HeightIncrease} (now height {tile.Height}), imbued with earth, rubble created.");
		}
	}
}

// ── Aftershock ──────────────────────────────────────────────────────────

/// <summary>
/// Grants the caster mana per enemy killed since the start of the player's turn.
/// Reads GameState.EnemiesKilledThisTurn (fed by HandleUnitDeath).
/// JSON: { "type": "mana_per_kill", "amount_per": 1 }
/// </summary>
public sealed class ManaPerKillEffect : EffectBase
{
	public int AmountPer;
	public ManaPerKillEffect(int amountPer) { AmountPer = amountPer; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = FindCasterUnit(s, caster);
		if (casterUnit == null || s == null)
			return;

		int kills = s.EnemiesKilledThisTurn;
		int mana = kills * AmountPer;
		if (mana > 0)
		{
			casterUnit.GainMana(mana);
			if (s.Mana.ContainsKey(caster))
				s.Mana[caster] = casterUnit.Stats.Mana;
		}
		s.Log($"[ManaPerKill] {kills} kill(s) × {AmountPer} = {mana} mana.");
	}
}

// ── Counterspell ────────────────────────────────────────────────────────

/// <summary>
/// Negates each targeted enemy's next action outright: when its turn comes, the
/// intent is cleared and the action is lost (no reschedule). Consumed in
/// RunEnemyTurn before the postpone check.
/// JSON: { "type": "negate_action" }
/// </summary>
public sealed class NegateActionEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (targets?.Items == null)
			return;

		foreach (var obj in targets.Items)
		{
			var unit = ResolveTargetUnit(s, obj);
			if (unit == null || unit.IsPlayerControlled || !unit.Stats.IsAlive)
				continue;

			unit.NegateNextAction = true;
			s.ActionsNegatedThisTurn++;
			s.Log($"[Negate] {unit.Name}'s next action is countered.");
		}
	}
}

// ── Riposte ─────────────────────────────────────────────────────────────

/// <summary>
/// Arms the caster with retaliation: until the start of their next turn, any
/// attacker that hits them takes the given damage back. Consumed per hit in
/// PerformAttack / PerformRangedAttack.
/// JSON: { "type": "retaliate", "damage": 4 }
/// </summary>
public sealed class RetaliateEffect : EffectBase
{
	public int Damage;
	public RetaliateEffect(int damage) { Damage = damage; }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s?.ActiveCasterUnit ?? FindCasterUnit(s, caster);
		if (casterUnit == null)
			return;

		casterUnit.RetaliateDamage = Math.Max(casterUnit.RetaliateDamage, Damage);
		s.Log($"[Riposte] {casterUnit.Name} will strike back for {Damage} this round.");
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
