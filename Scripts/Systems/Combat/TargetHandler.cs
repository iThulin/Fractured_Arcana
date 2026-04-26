using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Target Selectors — PHASE 2 UPDATE
//
// SelectUnitTarget and SelectTileTarget now query the real grid
// and return actual Unit/TileData objects. SelectAreaTarget was
// already updated. These are used as fallbacks when TryCast is
// called without pre-selected targets (enemy AI, chain effects).
//
// For player card-drops, OnCardDroppedOnTile bypasses these via
// TryCastWithTargets — but these need to work for everything else.
// ============================================================

public sealed class SelectUnitTarget : ITargetSelector
{
	public bool enemyOnly;
	public int range;
	public bool los;
	public bool friendlyOnly;

	public SelectUnitTarget(bool enemyOnly = true, int range = 4, bool los = true, bool friendlyOnly = false)
	{
		this.enemyOnly = enemyOnly;
		this.range = range;
		this.los = los;
		this.friendlyOnly = friendlyOnly;
	}

	public bool Select(GameState s, Entity caster, out TargetSet targets)
	{
		targets = new TargetSet();

		if (s?.Grid == null || s.UnitsInPlay == null) return false;

		// Find the caster's unit to measure range from
		Unit casterUnit = null;
		if (caster == s.PlayerA) casterUnit = s.PlayerUnit;
		else if (caster == s.PlayerB) casterUnit = s.EnemyUnit;
		else
		{
			foreach (var u in s.UnitsInPlay)
				if (u != null && u.Name == caster?.Name) { casterUnit = u; break; }
		}

		if (casterUnit?.CurrentTile == null) return false;

		var center = casterUnit.CurrentTile.Axial;

		// Find the best target: closest valid unit
		Unit bestTarget = null;
		int bestDist = int.MaxValue;

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;

			// Skip self
			if (unit == casterUnit) continue;

			// Filter by team
			if (enemyOnly && unit.TeamId == casterUnit.TeamId) continue;
			if (friendlyOnly && unit.TeamId != casterUnit.TeamId) continue;

			int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);

			// Range check
			if (dist > range) continue;

			// Pick closest valid target
			if (dist < bestDist)
			{
				bestDist = dist;
				bestTarget = unit;
			}
		}

		if (bestTarget != null)
		{
			targets.Items.Add(bestTarget);
			return true;
		}

		return false;
	}
}

public sealed class SelectTileTarget : ITargetSelector
{
	public int range;

	public SelectTileTarget(int r = 4) { range = r; }

	public bool Select(GameState s, Entity caster, out TargetSet targets)
	{
		targets = new TargetSet();

		if (s?.Grid == null) return false;

		// Find caster position
		Unit casterUnit = null;
		if (caster == s.PlayerA) casterUnit = s.PlayerUnit;
		else if (caster == s.PlayerB) casterUnit = s.EnemyUnit;

		if (casterUnit?.CurrentTile == null) return false;

		// Default: select the caster's own tile
		// (For player-initiated casts, this gets overridden by
		// TryCastWithTargets with the actual dropped-on tile)
		targets.Items.Add(casterUnit.CurrentTile);
		return true;
	}
}

public sealed class SelectAreaTarget : ITargetSelector
{
	public int Radius;
	public bool EnemiesOnly;
	public bool Tiles;

	public SelectAreaTarget(int r, bool enemiesOnly, bool tiles)
	{
		Radius = r;
		EnemiesOnly = enemiesOnly;
		Tiles = tiles;
	}

	public bool Select(GameState s, Entity caster, out TargetSet targets)
	{
		targets = new TargetSet();

		if (s?.Grid == null) return false;

		// Find the center from caster's position
		Unit casterUnit = null;
		if (caster == s.PlayerA) casterUnit = s.PlayerUnit;
		else if (caster == s.PlayerB) casterUnit = s.EnemyUnit;
		else
		{
			foreach (var u in s.UnitsInPlay)
				if (u != null && u.Name == caster?.Name) { casterUnit = u; break; }
		}

		if (casterUnit?.CurrentTile == null) return false;

		var center = casterUnit.CurrentTile.Axial;

		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
				continue;

			// Skip allies if enemies_only
			if (EnemiesOnly && unit.TeamId == casterUnit.TeamId)
				continue;

			int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
			if (dist <= Radius)
				targets.Items.Add(unit);
		}

		return true;
	}
}

public sealed class SelectSelfTarget : ITargetSelector
{
	public bool Select(GameState s, Entity caster, out TargetSet targets)
	{
		targets = new TargetSet();
		targets.Items.Add(caster);
		return true;
	}
}

public sealed class SelectGlobalTarget : ITargetSelector
{
	public bool Select(GameState s, Entity caster, out TargetSet targets)
	{
		targets = new TargetSet();
		return true;
	}
}

public sealed class SelectByTagTarget : ITargetSelector
{
	public string tag;
	public bool enemyOnly;

	public SelectByTagTarget(string tag, bool enemyOnly = false)
	{
		this.tag = tag;
		this.enemyOnly = enemyOnly;
	}

	public bool Select(GameState s, Entity caster, out TargetSet targets)
	{
		targets = new TargetSet();
		targets.Items.Add(tag);
		return true;
	}
}
