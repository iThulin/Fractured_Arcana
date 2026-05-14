using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Target Selectors
//
// All shape-based selectors (line, cone, ring, area, adjacent)
// share a common pattern:
//   1. Find the caster unit and origin
//   2. Determine an aim point (from RetargetOrigin, or fallback)
//   3. Build a set of coordinates from that shape
//   4. Optionally filter to units on those tiles, with team filter
//
// Helpers in HexDirection and TargetingHelpers handle the boilerplate.
// ============================================================

// ── Single-target selectors ─────────────────────────────────────

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

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return false;

        var center = casterUnit.CurrentTile.Axial;
        Unit best = null;
        int bestDist = int.MaxValue;

        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (unit == casterUnit) continue;
            if (!TargetingHelpers.PassesTeamFilter(unit, casterUnit, enemyOnly, friendlyOnly)) continue;

            int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
            if (dist > range) continue;

            if (dist < bestDist) { bestDist = dist; best = unit; }
        }

        if (best == null) return false;
        targets.Items.Add(best);
        return true;
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

        // Prefer the player's clicked tile if available
        if (TargetingHelpers.TryGetAim(s, out var aim))
        {
            var aimedTile = s.Grid.GetTile(aim);
            if (aimedTile != null)
            {
                targets.Items.Add(aimedTile);
                return true;
            }
        }

        // Fallback: caster's own tile
        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return false;
        targets.Items.Add(casterUnit.CurrentTile);
        return true;
    }
}

// Selects an empty (unoccupied) tile within range
public sealed class SelectEmptyTileTarget : ITargetSelector
{
    public int Range;
    public SelectEmptyTileTarget(int range) { Range = range; }

    public bool Select(GameState s, Entity caster, out TargetSet result)
    {
        result = new TargetSet();
        var casterUnit = s.ActiveCasterUnit;
        if (casterUnit?.CurrentTile == null) return false;

        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(casterUnit.CurrentTile.Axial, kvp.Key) <= Range
                && kvp.Value.Occupant == null
                && !kvp.Value.IsBlocked)
            {
                result.Items.Add(kvp.Value);
            }
        }

        return result.Items.Count > 0;
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

// ── Shape selectors ─────────────────────────────────────────────

public sealed class SelectAreaTarget : ITargetSelector
{
    public int Radius;
    public bool EnemiesOnly;
    public bool IncludeTiles;

    public SelectAreaTarget(int r, bool enemiesOnly, bool includeTiles)
    {
        Radius = r;
        EnemiesOnly = enemiesOnly;
        IncludeTiles = includeTiles;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null) return false;

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return false;

        // Aim point: player's dropped tile, or caster's tile as fallback
        Vector2I center;
        if (!TargetingHelpers.TryGetAim(s, out center))
            center = casterUnit.CurrentTile.Axial;

        // Collect tiles in radius
        if (IncludeTiles)
        {
            foreach (var kvp in s.Grid.Tiles)
            {
                if (s.Grid.Distance(center, kvp.Key) <= Radius)
                    targets.Items.Add(kvp.Value);
            }
        }

        // Collect units in radius
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (!TargetingHelpers.PassesTeamFilter(unit, casterUnit, EnemiesOnly)) continue;

            int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
            if (dist <= Radius) targets.Items.Add(unit);
        }

        return true;
    }
}

public sealed class SelectRingTarget : ITargetSelector
{
    public int Radius;
    public bool IncludeTiles;

    public SelectRingTarget(int radius = 2, bool includeTiles = true)
    {
        Radius = radius;
        IncludeTiles = includeTiles;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null) return false;

        // Center: aim point if available, else caster
        Vector2I center;
        if (!TargetingHelpers.TryGetAim(s, out center))
        {
            var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
            if (casterUnit?.CurrentTile == null) return false;
            center = casterUnit.CurrentTile.Axial;
        }

        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) != Radius) continue;

            if (IncludeTiles)
                targets.Items.Add(kvp.Value);
            else if (kvp.Value.Occupant != null && kvp.Value.Occupant.Stats.IsAlive)
                targets.Items.Add(kvp.Value.Occupant);
        }

        return targets.Items.Count > 0;
    }
}

public sealed class SelectLineTarget : ITargetSelector
{
    public int Length;
    public bool EnemiesOnly;
    public bool IncludeTiles;

    public SelectLineTarget(int length = 2, bool enemiesOnly = true, bool includeTiles = false)
    {
        Length = length;
        EnemiesOnly = enemiesOnly;
        IncludeTiles = includeTiles;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null) return false;

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return false;

        var origin = casterUnit.CurrentTile.Axial;

        // Aim from RetargetOrigin, fall back to nearest enemy
        if (!TargetingHelpers.TryGetAim(s, out var aim))
            aim = TargetingHelpers.FindNearestEnemyCoord(s, casterUnit, origin);
        if (aim == origin) return false;

        int dirIdx = HexDirection.Pick(origin, aim, Length);

        for (int step = 1; step <= Length; step++)
        {
            var coord = origin + HexDirection.All[dirIdx] * step;
            var tile = s.Grid.GetTile(coord);
            if (tile == null) continue;

            if (IncludeTiles) targets.Items.Add(tile);

            if (tile.Occupant != null && tile.Occupant.Stats.IsAlive)
            {
                if (!TargetingHelpers.PassesTeamFilter(tile.Occupant, casterUnit, EnemiesOnly)) continue;
                if (!IncludeTiles) targets.Items.Add(tile.Occupant);
            }
        }
        return true;
    }
}

public sealed class SelectConeTarget : ITargetSelector
{
    public int Range;
    public bool EnemiesOnly;
    public bool IncludeTiles;

    public SelectConeTarget(int range = 3, bool enemiesOnly = true, bool includeTiles = false)
    {
        Range = range;
        EnemiesOnly = enemiesOnly;
        IncludeTiles = includeTiles;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null || s.UnitsInPlay == null) return false;

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return false;

        var origin = casterUnit.CurrentTile.Axial;

        if (!TargetingHelpers.TryGetAim(s, out var aim))
            aim = TargetingHelpers.FindNearestEnemyCoord(s, casterUnit, origin);
        if (aim == origin) return false;

        int dirIdx = HexDirection.Pick(origin, aim, Range);
        var coneTiles = BuildConeCoords(origin, dirIdx, Range);

        if (IncludeTiles)
        {
            foreach (var coord in coneTiles)
            {
                var tile = s.Grid.GetTile(coord);
                if (tile != null) targets.Items.Add(tile);
            }
        }

        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (unit == casterUnit) continue;
            if (!TargetingHelpers.PassesTeamFilter(unit, casterUnit, EnemiesOnly)) continue;

            if (coneTiles.Contains(unit.CurrentTile.Axial))
                targets.Items.Add(unit);
        }

        s.Log($"[ConeTarget] Direction {dirIdx}, range {Range}: found {targets.Items.Count} target(s).");
        return true;
    }

    /// <summary>
    /// Build the hex coords forming a cone of the given range, expanding by 1 each step.
    ///   Step 1: just the forward hex
    ///   Step 2: forward + 1 hex on each side
    ///   Step N: forward + (N-1) hexes on each side
    /// </summary>
    private static HashSet<Vector2I> BuildConeCoords(Vector2I origin, int dirIdx, int range)
    {
        var coords = new HashSet<Vector2I>();
        var forward = HexDirection.All[dirIdx];
        var leftDir = HexDirection.All[(dirIdx + 5) % 6];
        var rightDir = HexDirection.All[(dirIdx + 1) % 6];

        for (int step = 1; step <= range; step++)
        {
            var center = origin + forward * step;
            int spread = step - 1;

            for (int side = -spread; side <= spread; side++)
            {
                Vector2I tile = side == 0
                    ? center
                    : (side < 0 ? center + leftDir * (-side) : center + rightDir * side);
                coords.Add(tile);
            }
        }
        return coords;
    }
}

public sealed class SelectAdjacentToTarget : ITargetSelector
{
    public bool IncludeTiles;

    public SelectAdjacentToTarget(bool includeTiles = true)
    {
        IncludeTiles = includeTiles;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null) return false;

        if (!TargetingHelpers.TryGetAim(s, out var center)) return false;

        foreach (var neighbor in s.Grid.GetNeighbors(center))
        {
            var tile = s.Grid.GetTile(neighbor);
            if (tile == null) continue;

            if (IncludeTiles)
                targets.Items.Add(tile);
            else if (tile.Occupant != null && tile.Occupant.Stats.IsAlive)
                targets.Items.Add(tile.Occupant);
        }

        return targets.Items.Count > 0;
    }
}

public sealed class SelectNearestToTarget : ITargetSelector
{
    public int Range;

    public SelectNearestToTarget(int range = 3) { Range = range; }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null || s.UnitsInPlay == null) return false;

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit == null) return false;

        // Build exclusion set: don't chain to caster or anyone already hit
        var excluded = new HashSet<Unit> { casterUnit };
        Vector2I origin;

        if (s.RetargetOrigin != null && s.RetargetOrigin.Items.Count > 0)
        {
            // Use previous target as the chain origin
            var prevTarget = TargetingHelpers.ResolveUnit(s, s.RetargetOrigin.Items[0]);
            if (prevTarget?.CurrentTile != null)
            {
                origin = prevTarget.CurrentTile.Axial;
                excluded.Add(prevTarget);
            }
            else
            {
                origin = casterUnit.CurrentTile?.Axial ?? default;
            }

            // Exclude all previously-hit units so chains don't repeat
            foreach (var obj in s.RetargetOrigin.Items)
            {
                var u = TargetingHelpers.ResolveUnit(s, obj);
                if (u != null) excluded.Add(u);
            }
        }
        else
        {
            if (casterUnit.CurrentTile == null) return false;
            origin = casterUnit.CurrentTile.Axial;
        }

        Unit nearest = null;
        int nearestDist = int.MaxValue;

        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (unit.TeamId == casterUnit.TeamId) continue;
            if (excluded.Contains(unit)) continue;

            int dist = s.Grid.Distance(origin, unit.CurrentTile.Axial);
            if (dist > Range) continue;
            if (dist < nearestDist) { nearestDist = dist; nearest = unit; }
        }

        if (nearest == null) return false;
        targets.Items.Add(nearest);
        return true;
    }
}

public sealed class SelectElementTileTarget : ITargetSelector
{
    public string Element;
    public int Range;

    public SelectElementTileTarget(string element, int range = 6)
    {
        Element = element;
        Range = range;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null) return false;

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return false;

        var center = casterUnit.CurrentTile.Axial;

        TileElementType needed = Element.ToLowerInvariant() switch
        {
            "fire" => TileElementType.Fire,
            "ice" => TileElementType.Frost,
            "storm" => TileElementType.Lightning,
            "stone" => TileElementType.Earth,
            _ => TileElementType.None
        };

        // Prefer the player's clicked tile if it matches the required element
        if (TargetingHelpers.TryGetAim(s, out var aim))
        {
            var aimedTile = s.Grid.GetTile(aim);
            if (aimedTile?.ElementType == needed)
            {
                targets.Items.Add(aimedTile);
                return true;
            }
        }

        // Fallback: nearest matching tile in range
        TileData best = null;
        int bestDist = int.MaxValue;

        foreach (var kvp in s.Grid.Tiles)
        {
            var tile = kvp.Value;
            if (tile?.ElementType != needed) continue;
            int dist = s.Grid.Distance(center, kvp.Key);
            if (dist > Range) continue;
            if (dist < bestDist) { bestDist = dist; best = tile; }
        }

        if (best == null) return false;
        targets.Items.Add(best);
        return true;
    }
}