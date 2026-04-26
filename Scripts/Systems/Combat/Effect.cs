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

		s.Log($"[DealDamageEffect] resolving for Amount={Amount}");
		if (targets == null)
		{
			s.Log("targets == null");
			return;
		}

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
				u.ApplyDamage(Amount);
				s.Log($"HIT unit {u.Name}");
				hit++;
			}
			else if (obj is TileData td && td.Occupant != null)
			{
				td.Occupant.ApplyDamage(Amount);
				s.Log($"HIT tile occupant {td.Occupant.Name} on {td.Axial}");
				hit++;
			}
			else if (obj is HexTile tileView)
			{
				var tileData = ResolveTileDataFromView(s, tileView);
				if (tileData != null && tileData.Occupant != null)
				{
					tileData.Occupant.ApplyDamage(Amount);
					s.Log($"HIT tile occupant {tileData.Occupant.Name} on {tileData.Axial}");
					hit++;
				}
			}
		}

		s.Log($"Resolve: Deal {Amount} damage to {hit} target(s).");
	}

	public override EffectResult ResolveWithResult(PredicateContext ctx)
	{
		int totalDamage = 0;
		bool lethal = false;
		int hit = 0;

		if (ctx.Targets == null) return new EffectResult();

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
				victim.ApplyDamage(Amount);
				totalDamage += Amount;
				hit++;
				if (hpBefore > 0 && victim.Stats.Health <= 0) lethal = true;
			}
		}

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
			// Self movement, grant additional movement points
			if ( casterUnit != null)
			{
				casterUnit.Stats.MovePoints += Tiles;
				s.Log($"[Dash] {casterUnit.Name} gains {Tiles} move points (now {casterUnit.Stats.MovePoints}).");
			}
			else
			{
				s.Log($"[Dash] Move {Tiles} tile(s). (caster unit not found)");
			}
		}
		else
		{
			// Push Effect applied to targets
			foreach (var obj in targets.Items)
			{
				var victim = ResolveTargetUnit(s, obj);
				if (victim != null)
				{
					// PUSH STUB. Needs to integrate with the pathfinder.
					s.Log($"[Push] {victim.Name} pushed {Tiles} tile(s).");
				}
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

// ── Imbue Tile Effect ───────────────────────────────────────────
// Phase 2 stub: logs the imbue and deals minor bonus damage if
// an enemy is on the tile. Full elemental terrain system comes later.
public sealed class ImbueTileEffect : EffectBase
{
	public string Element;  // "fire", "ice", "storm", "stone"
	public int BonusDamage; // Optional damage to occupant
	public ImbueTileEffect(string element, int bonusDamage = 0)
	{
		Element = element;
		BonusDamage = bonusDamage;
	}
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.Log($"[ImbueTile] Imbuing tile(s) with {Element}.");
		if (BonusDamage > 0 && targets != null)
		{
			foreach (var obj in targets.Items)
			{
				var victim = ResolveTargetUnit(s, obj);
				if (victim != null && victim.TeamId != 0) // Only damage enemies
				{
					victim.ApplyDamage(BonusDamage);
					s.Log($"[ImbueTile] {Element} deals {BonusDamage} to {victim.Name}.");
				}
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
// Phase 2: logs summon. Full summon instantiation needs scene
// references which must come from GameRunner.
public sealed class SummonEffect : EffectBase
{
	public string UnitKind;
	public int Count;
	public SummonEffect(string kind, int count) { UnitKind = kind; Count = count; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		s.Log($"[Summon] Summon {Count}x {UnitKind}. (Scene spawn not yet wired)");
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
