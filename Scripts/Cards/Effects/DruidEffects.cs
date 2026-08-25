using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// DruidEffects.cs
//
// Purpose:        Druid school effects: growth stages, wilding, and wildlife.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase, core leaves),
//                 PersistentEffect.cs (PersistentEffect base),
//                 CardScriptRegistry.Druid.cs (registration)
// Notes:          Extracted from Effect.cs / CompositeEffects.cs /
//                 PersistentEffect.cs. Pure move, no behavior change.
// ============================================================

/// <summary>Shared helpers for the Druid growth effects: center resolution and radius iteration, mirroring TerraformEffect's target handling.</summary>
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

/// <summary>
/// Plant living terrain at a stage on the target tile (and ring, if radius &gt; 0).
/// Terrain affinity is enforced inside GrowthManager.Seed (never takes root on fire).
/// Raises the caster's Wilding once per cast, not per tile, so a wide seed does not
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
		{ s.Log("[SeedGrowth] GameState.Growth is null. Wire the GrowthManager."); return; }

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

/// <summary>
/// Force every living tile in radius up one stage, overriding the natural clock.
/// GrowthManager.AdvanceTile raises Wilding per advance, so a big advance can be the
/// thing that tips you into a Riot. That is intended.
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

/// <summary>
/// The panic button: clear living tiles in radius for heal/draw scaled by how many were
/// consumed, leaving fertile carcass ground behind (the spent grove enriches the soil).
/// Deliberately worse value than letting growth mature. Composes the already-registered
/// HealEffect / DrawCardsEffect rather than reimplementing those APIs.
/// JSON: { "type": "harvest_growth", "radius": 2, "heal_per": 3, "draw_per": 0 }
/// </summary>
public sealed class HarvestGrowthEffect : EffectBase
{
	public int Radius;
	public int HealPer;
	public int DrawPer;

	/// <summary>Deadfall (audit §6.2.6, 2026-07-29): mana per harvested tile, capped
	/// by <see cref="ManaCap"/>. Ramp that costs board: the Druid-flavored home for
	/// acceleration after Vein Tap's nerf.</summary>
	public int ManaPer;
	public int ManaCap;

	public HarvestGrowthEffect(int radius, int healPer, int drawPer, int manaPer = 0, int manaCap = 99)
	{
		Radius = radius;
		HealPer = healPer;
		DrawPer = drawPer;
		ManaPer = manaPer;
		ManaCap = manaCap;
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
		int manaGained = 0;
		if (ManaPer > 0)
		{
			manaGained = Math.Min(ManaPer * harvested, ManaCap);
			new GainManaEffect(manaGained).Resolve(s, caster, targets, snap);
		}

		s.Log($"[HarvestGrowth] Harvested {harvested} tile(s): +{HealPer * harvested} heal, " +
			  $"+{DrawPer * harvested} draw" + (manaGained > 0 ? $", +{manaGained} mana." : "."));
	}
}

/// <summary>
/// Damage each targeted enemy, scaled by the growth stage of the tile they stand on.
/// The more mature the ground beneath them, the more it hurts. Rewards a board you have
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
