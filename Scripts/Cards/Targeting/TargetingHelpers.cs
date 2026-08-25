using Godot;

// ============================================================
// TargetingHelpers.cs
//
// Purpose:        Shared utilities for ITargetSelector
//                 implementations: caster lookup, target-object
//                 to coord/unit resolution, aim-point detection,
//                 and the standard team filter. Centralised so
//                 each selector only writes its unique shape
//                 logic, not the bookkeeping.
// Layer:          Targeting
// Collaborators:  TargetSelectors.cs (every selector calls in
//                 here), HexDirection.cs (paired helpers),
//                 GameState.cs (ActiveCasterUnit,
//                 RetargetOrigin), Unit.cs, TileData.cs
// See:            README §5.3 (Targeting Types)
// ============================================================

/// <summary>Static utilities used by every <see cref="ITargetSelector"/> implementation in <see cref="TargetSelectors"/>. Centralises bookkeeping (resolve a target reference to its coord/unit, find the caster, locate the player's aim point) so selectors stay focused on shape and filter logic.</summary>
public static class TargetingHelpers
{
    /// <summary>
    /// Find which Unit is acting as the caster, given the abstract Entity reference.
    /// Returns null if no matching unit exists or is alive.
    /// </summary>
    public static Unit FindCasterUnit(GameState s, Entity caster)
    {
        if (s == null || caster == null) return null;

        // ActiveCasterUnit is the authoritative source when set.
        // It correctly tracks which player unit is acting each cast.
        if (s.ActiveCasterUnit != null) return s.ActiveCasterUnit;

        if (caster == s.PlayerA) return s.PlayerUnit;
        if (caster == s.PlayerB) return s.EnemyUnit;

        if (s.UnitsInPlay != null)
        {
            foreach (var u in s.UnitsInPlay)
                if (u != null && u.Name == caster.Name) return u;
        }
        return null;
    }

    /// <summary>Resolve any target object to its axial coordinate.</summary>
    public static Vector2I? ResolveCoord(GameState s, object obj)
    {
        if (obj is Unit u && u.CurrentTile != null) return u.CurrentTile.Axial;
        if (obj is TileData td) return td.Axial;
        if (obj is HexTile tv) return tv.Axial;
        return null;
    }

    /// <summary>Resolve any target object to a Unit if there is one.</summary>
    public static Unit ResolveUnit(GameState s, object obj)
    {
        if (obj is Unit u) return u;
        if (obj is TileData td) return td.Occupant;
        if (obj is HexTile tv) return s?.Grid?.GetTile(tv.Axial)?.Occupant;
        return null;
    }

    /// <summary>
    /// Try to read the aim point from RetargetOrigin. This is set by:
    ///   - The player's drag-drop on a tile (GameRunner stashes it before casting)
    ///   - RetargetEffect (when chaining sub-effects through different targeters)
    /// Returns true if an aim was found.
    /// </summary>
    public static bool TryGetAim(GameState s, out Vector2I aim)
    {
        aim = default;
        if (s?.RetargetOrigin?.Items == null || s.RetargetOrigin.Items.Count == 0)
            return false;
        var coord = ResolveCoord(s, s.RetargetOrigin.Items[0]);
        if (coord.HasValue) { aim = coord.Value; return true; }
        return false;
    }

    /// <summary>
    /// Find the axial coord of the nearest enemy to origin, ignoring LOS and range.
    /// Used as a fallback aim when no explicit aim point is available.
    /// Returns origin itself if no enemy exists.
    /// </summary>
    public static Vector2I FindNearestEnemyCoord(GameState s, Unit caster, Vector2I origin)
    {
        if (s?.UnitsInPlay == null || caster == null) return origin;

        Vector2I best = origin;
        int bestDist = int.MaxValue;

        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
            if (unit.TeamId == caster.TeamId) continue;

            int dist = s.Grid.Distance(origin, unit.CurrentTile.Axial);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = unit.CurrentTile.Axial;
            }
        }
        return best;
    }

    /// <summary>
    /// Standard team filter for shape targeters. Returns true if this unit
    /// passes the enemies/friendlies-only filter relative to caster.
    /// </summary>
    public static bool PassesTeamFilter(Unit unit, Unit caster, bool enemiesOnly, bool friendliesOnly = false)
    {
        if (unit == null || caster == null) return false;
        if (enemiesOnly && unit.TeamId == caster.TeamId) return false;
        if (friendliesOnly && unit.TeamId != caster.TeamId) return false;
        return true;
    }
}