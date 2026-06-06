using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// AttunementResolver.cs
//
// Purpose:        Applies elemental attunement threshold bonuses
//                 after a spell resolves. Reads the spell's
//                 element tags, increments matching counters on
//                 the caster's ElementalAttunement, and fires
//                 tier 1/2/3/4 bonuses when crossed.
// Layer:          System
// Collaborators:  ElementalAttunement.cs (state container),
//                 Unit.cs (caster ref), GameState.cs,
//                 RulesManager.cs (calls this post-resolve)
// See:            README §6 — Elemental Attunement
// ============================================================

/// <summary>Static resolver invoked after every spell resolution. Updates the caster's elemental attunement counters and applies tier-crossing bonuses (1: bonus damage; 2: auto-imbue; 3: enhanced effect; 4: burst AoE then reset).</summary>
public static class AttunementResolver
{
	/// <summary>
	/// Call after a spell resolves. Applies attunement threshold bonuses.
	/// castingUnit is the wizard who cast the spell.
	/// </summary>
	public static List<string> ApplyThresholdEffects(
		ElementalAttunement attunement,
		string[] tags,
		GameState state,
		Unit castingUnit,
		TargetSet targets)
	{
		var log = new List<string>();
		if (attunement == null || tags == null || tags.Length == 0) return log;

		foreach (var tagStr in tags)
		{
			if (!ElementalAttunement.TryParseTag(tagStr, out var element)) continue;

			// ── Threshold 1+: Bonus damage ──────────────────────────
			int bonusDmg = attunement.GetBonusDamage(element);
			if (bonusDmg > 0 && targets != null)
			{
				foreach (var obj in targets.Items)
				{
					Unit victim = ResolveUnit(state, obj);
					if (victim != null && victim.TeamId != castingUnit.TeamId)
					{
						victim.ApplyDamage(bonusDmg);
						log.Add($"[Attunement] {element} +{bonusDmg} bonus damage to {victim.Name}");
					}
				}
			}

			// ── Threshold 2+: Auto-imbue ────────────────────────────
			if (attunement.ShouldAutoImbue(element) && targets != null)
			{
				TileElementType tileElement = MapToTileElement(element);
				foreach (var obj in targets.Items)
				{
					TileData tile = ResolveTile(state, obj);
					if (tile != null && tile.ElementType != tileElement)
					{
						tile.ElementType = tileElement;
						tile.ElementStrength = 1.0f;
						if (element == ElementTag.Fire) tile.IsHazardous = true;
						tile.TileView?.SetElement(tileElement);
						log.Add($"[Attunement] Auto-imbued {tile.Axial} with {element}");
					}
				}
			}

			// ── Threshold 3+: Enhanced effect ───────────────────────
			if (attunement.ShouldEnhance(element) && targets != null)
			{
				log.AddRange(ApplyEnhancedEffect(element, state, castingUnit, targets));
			}
		}

		return log;
	}

	/// <summary>
	/// Resolve burst effects when attunement hits 4.
	/// </summary>
	public static List<string> ResolveBurst(
		ElementTag element,
		GameState state,
		Unit castingUnit)
	{
		var log = new List<string>();
		if (castingUnit?.CurrentTile == null || state?.Grid == null) return log;

		var center = castingUnit.CurrentTile.Axial;

		switch (element)
		{
			case ElementTag.Fire:
				log.Add("FIRE BURST: Nova!");
				foreach (var unit in state.UnitsInPlay)
				{
					if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
					if (unit.TeamId == castingUnit.TeamId) continue;
					if (state.Grid.Distance(center, unit.CurrentTile.Axial) <= 99)
					{
						unit.ApplyDamage(6);
						log.Add($"  Fire Nova hits {unit.Name} for 6 damage");
					}
				}
				break;

			case ElementTag.Ice:
				log.Add("ICE BURST: Freeze Wave!");
				foreach (var unit in state.UnitsInPlay)
				{
					if (unit == null || !unit.Stats.IsAlive) continue;
					if (unit.TeamId == castingUnit.TeamId) continue;
					unit.ApplyStatus("frozen", 1);
					log.Add($"  Freeze Wave freezes {unit.Name}");
				}
				break;

			case ElementTag.Storm:
				log.Add("STORM BURST: Lightning Strike!");
				Unit nearest = null;
				int nearestDist = int.MaxValue;
				foreach (var unit in state.UnitsInPlay)
				{
					if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
					if (unit.TeamId == castingUnit.TeamId) continue;
					int dist = state.Grid.Distance(center, unit.CurrentTile.Axial);
					if (dist < nearestDist) { nearest = unit; nearestDist = dist; }
				}
				if (nearest != null)
				{
					nearest.ApplyDamage(8);
					log.Add($"  Lightning strikes {nearest.Name} for 8 damage");
					// Chain to 1 adjacent enemy
					foreach (var unit in state.UnitsInPlay)
					{
						if (unit == null || !unit.Stats.IsAlive || unit == nearest || unit.CurrentTile == null) continue;
						if (unit.TeamId == castingUnit.TeamId) continue;
						if (state.Grid.Distance(nearest.CurrentTile.Axial, unit.CurrentTile.Axial) <= 2)
						{
							unit.ApplyDamage(4);
							log.Add($"  Lightning chains to {unit.Name} for 4 damage");
							break;
						}
					}
				}
				break;

			case ElementTag.Earth:
				log.Add("EARTH BURST: Quake!");
				foreach (var unit in state.UnitsInPlay)
				{
					if (unit == null || !unit.Stats.IsAlive) continue;
					if (unit.TeamId == castingUnit.TeamId) continue;
					unit.Stats.MovePoints = Math.Max(0, unit.Stats.MovePoints - 2);
					log.Add($"  Quake slows {unit.Name} (-2 movement)");
				}
				castingUnit.Stats.Armor += 6;
				castingUnit.RefreshHealthBar();
				log.Add($"  Quake grants {castingUnit.Name} 6 armor");
				break;
		}

		return log;
	}

	// ── Enhanced effects (threshold 3) ──────────────────────────────

	private static List<string> ApplyEnhancedEffect(
		ElementTag element, GameState state, Unit castingUnit, TargetSet targets)
	{
		var log = new List<string>();

		foreach (var obj in targets.Items)
		{
			Unit victim = ResolveUnit(state, obj);
			if (victim == null) continue;

			switch (element)
			{
				case ElementTag.Fire:
					if (victim.TeamId != castingUnit.TeamId)
					{
						victim.ApplyStatus("burning", 2);
						log.Add($"[Enhanced] {victim.Name} is burning for 2 turns");
					}
					break;
				case ElementTag.Ice:
					if (victim.TeamId != castingUnit.TeamId)
					{
						victim.ApplyStatus("slowed", 1);
						log.Add($"[Enhanced] {victim.Name} is slowed");
					}
					break;
				case ElementTag.Storm:
					if (victim.TeamId != castingUnit.TeamId && victim.CurrentTile != null && state.Grid != null)
					{
						foreach (var unit in state.UnitsInPlay)
						{
							if (unit == null || !unit.Stats.IsAlive || unit == victim || unit.CurrentTile == null) continue;
							if (unit.TeamId == castingUnit.TeamId) continue;
							if (state.Grid.Distance(victim.CurrentTile.Axial, unit.CurrentTile.Axial) <= 1)
							{
								unit.ApplyDamage(3);
								log.Add($"[Enhanced] Storm chains 3 damage to {unit.Name}");
								break;
							}
						}
					}
					break;
				case ElementTag.Earth:
					castingUnit.Stats.Armor += 3;
					castingUnit.RefreshHealthBar();
					log.Add($"[Enhanced] Earth grants {castingUnit.Name} 3 armor");
					return log; // Only apply once per cast, not per target
			}
		}

		return log;
	}

	// ── Helpers ─────────────────────────────────────────────────────

	private static TileElementType MapToTileElement(ElementTag element) => element switch
	{
		ElementTag.Fire  => TileElementType.Fire,
		ElementTag.Ice   => TileElementType.Frost,
		ElementTag.Storm => TileElementType.Lightning,
		ElementTag.Earth => TileElementType.Earth,
		_ => TileElementType.None
	};

	private static Unit ResolveUnit(GameState s, object obj)
	{
		if (obj is Unit u) return u;
		if (obj is TileData td) return td.Occupant;
		if (obj is HexTile tv) return s?.Grid?.GetTile(tv.Axial)?.Occupant;
		return null;
	}

	private static TileData ResolveTile(GameState s, object obj)
	{
		if (obj is TileData td) return td;
		if (obj is HexTile tv) return s?.Grid?.GetTile(tv.Axial);
		if (obj is Unit u) return u.CurrentTile;
		return null;
	}
}
