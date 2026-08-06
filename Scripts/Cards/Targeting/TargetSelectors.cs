using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// TargetSelectors.cs
//
// Purpose:        All ITargetSelector implementations. Each
//                 class corresponds to one registered targeter
//                 type ("self", "unit", "tile", "aoe", "cone",
//                 "ring", "line", "by_tag", "nearest_to_target",
//                 "adjacent_to_target", "element_tile",
//                 "empty_tile", "none").
// Layer:          Targeting
// Collaborators:  ScriptingInterfaces.cs (ITargetSelector,
//                 TargetSet), TargetingHelpers.cs (shared
//                 utilities), HexDirection.cs (direction math),
//                 JsonCardLoader.cs (RegisterBuiltins maps JSON
//                 type strings to these classes),
//                 GameState.cs (Grid, UnitsInPlay,
//                 ActiveCasterUnit, RetargetOrigin)
// See:            README §5.3 (Targeting Types)
// ============================================================
//
// All shape-based selectors share a common pattern:
//   1. Find the caster unit and origin
//   2. Determine an aim point (from RetargetOrigin, or fallback)
//   3. Build a set of coordinates from that shape
//   4. Optionally filter to units on those tiles, with team filter
// Helpers in HexDirection and TargetingHelpers handle steps 1, 2, and 4.

// ── Two-step (interactive) selectors ────────────────────────────────────
//
// Post-cast player choice, part 1 (2026-07-28). Cards whose text says "in a
// direction you choose" or "move it up to N tiles" need TWO picks, not one.
//
// The ruling that shapes these: both picks happen at CAST time, not at
// resolution. The destination is knowable before the card reaches the stack, and
// this engine's stack is MTG-derived — targets are chosen when the spell is cast
// and are public information from that moment. Three things fall out of that,
// all of them wanted:
//   · a Reaction can respond to WHERE the push is going, not just that it happened
//   · the R22 drag preview can model the outcome, which it structurally cannot do
//     for a choice made after resolution has begun
//   · nothing in IEffect, Resolver or the stack changes — this is an input-layer
//     feature wearing a targeter's clothes
//
// Choices whose INFORMATION does not exist until resolution — scry's "look at the
// top 3" — are a genuinely different problem and do NOT belong here.
//
// TargetSet convention for both: Items[0] is the victim Unit, Items[1] is the
// chosen TileData. Effects read them positionally; CombatManager guarantees the
// order and validates both picks before the cast is attempted.

/// <summary>Base for targeters the DRAG PATH drives interactively. Select() is
/// deliberately a hard failure: these can only be filled by CombatManager's
/// two-step input flow, and an AI or scripted cast reaching here means a card was
/// authored with an interactive targeter and then handed to a non-interactive
/// caster. Failing loudly beats silently targeting nothing.</summary>
public abstract class SelectTwoStepTarget : ITargetSelector
{
    public bool enemyOnly;
    public bool friendlyOnly;
    public bool constructsOnly;
    public int range;

    protected SelectTwoStepTarget(bool enemyOnly, int range,
                                  bool friendlyOnly = false, bool constructsOnly = false)
    {
        this.enemyOnly = enemyOnly;
        this.friendlyOnly = friendlyOnly;
        this.constructsOnly = constructsOnly;
        this.range = range;
    }

    /// <summary>Player-facing prompt for the second pick, shown in the action log.</summary>
    public abstract string StepTwoPrompt { get; }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = null;
        GD.PrintErr($"[Targeting] {GetType().Name} is interactive — it cannot be resolved " +
                    "by a non-interactive cast (AI, scripted, or Rules.TryCast). " +
                    "Cards using it must be cast by the player.");
        return false;
    }
}

/// <summary>Pick a unit, then a DESTINATION TILE within <see cref="destRange"/> of
/// that unit. "Move a construct you control up to 3 tiles."</summary>
public sealed class SelectUnitThenTileTarget : SelectTwoStepTarget
{
    public int destRange;

    public SelectUnitThenTileTarget(bool enemyOnly = true, int range = 4, int destRange = 2,
                                    bool friendlyOnly = false, bool constructsOnly = false)
        : base(enemyOnly, range, friendlyOnly, constructsOnly)
    {
        this.destRange = destRange;
    }

    public override string StepTwoPrompt => "Click a destination tile.";
}

/// <summary>Pick a unit, then an ADJACENT tile of that unit naming the direction to
/// shove it. "Push an enemy 2 tiles in a direction you choose." The adjacent tile
/// is the AIM, not the destination — the effect walks <c>tiles</c> steps along that
/// axis and stops on the first blocker, exactly as the derived-direction push does.</summary>
public sealed class SelectUnitThenDirectionTarget : SelectTwoStepTarget
{
    public SelectUnitThenDirectionTarget(bool enemyOnly = true, int range = 4,
                                        bool friendlyOnly = false, bool constructsOnly = false)
        : base(enemyOnly, range, friendlyOnly, constructsOnly) { }

    public override string StepTwoPrompt => "Click a tile beside it to aim the shove.";
}

// ── Single-target selectors ─────────────────────────────────────────────

/// <summary>Picks the single nearest unit to the caster within <see cref="range"/>, optionally filtered to enemies or friendlies. LOS flag is plumbed through but not yet enforced.</summary>
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

/// <summary>Picks a single tile — the player's clicked aim point if available, otherwise the caster's own tile.</summary>
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

/// <summary>Collects every unoccupied, unblocked tile within <see cref="Range"/> of the caster. Used as the candidate set for summon-style spells.</summary>
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

/// <summary>Targets the caster itself. Used by self-buff and transform effects.</summary>
public sealed class SelectSelfTarget : ITargetSelector
{
    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        targets.Items.Add(caster);
        return true;
    }
}

/// <summary>Targets nothing in particular — returns an empty target set that's still considered a successful selection. Used by global effects (board-wipes) that operate on world state directly rather than a target list.</summary>
public sealed class SelectGlobalTarget : ITargetSelector
{
    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        return true;
    }
}

/// <summary>Packages a tag string into the target set so downstream effects can read it. Does NOT iterate units — that's the effect's responsibility.</summary>
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

// ── Shape selectors ─────────────────────────────────────────────────────

/// <summary>Collects every tile and unit within <see cref="Radius"/> hexes of the aim point (or caster if no aim). When <see cref="IncludeTiles"/> is false, only units are returned. Team filter applies to units.</summary>
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

/// <summary>Collects every tile (or its occupant) at exact distance == <see cref="Radius"/> from the aim point. The hollow donut shape; useful for "blast zone surrounds a safe center" effects.</summary>
public sealed class SelectRingTarget : ITargetSelector
{
    public int Radius;
    public bool IncludeTiles;
    public bool EnemiesOnly;

    public SelectRingTarget(int radius = 2, bool includeTiles = true, bool enemiesOnly = false)
    {
        Radius = radius;
        IncludeTiles = includeTiles;
        EnemiesOnly = enemiesOnly;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null) return false;

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);

        // Center: aim point if available, else caster
        Vector2I center;
        if (!TargetingHelpers.TryGetAim(s, out center))
        {
            if (casterUnit?.CurrentTile == null) return false;
            center = casterUnit.CurrentTile.Axial;
        }

        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) != Radius) continue;

            // Hostile rings collect enemy occupants only — never tiles, never
            // the caster's own side (Spores self-poison, PT7 2026-07-12).
            if (EnemiesOnly)
            {
                var occ = kvp.Value.Occupant;
                if (occ != null && occ.Stats.IsAlive
                    && TargetingHelpers.PassesTeamFilter(occ, casterUnit, true))
                    targets.Items.Add(occ);
                continue;
            }

            if (IncludeTiles)
                targets.Items.Add(kvp.Value);
            else if (kvp.Value.Occupant != null && kvp.Value.Occupant.Stats.IsAlive)
                targets.Items.Add(kvp.Value.Occupant);
        }

        return targets.Items.Count > 0;
    }
}

/// <summary>Projects a straight line from the caster toward the aim point for up to <see cref="Length"/> tiles. Picks direction by axial-snap when the aim lies exactly on an axis, otherwise nearest direction.</summary>
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

/// <summary>Expanding-cone shape: step 1 is the single forward hex, each subsequent step widens by one hex on each side. Picks direction by axial-snap when possible.</summary>
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

/// <summary>Collects the six neighbour tiles (or their occupants) of the aim point. Used by splash and "everyone nearby" effects.</summary>
public sealed class SelectAdjacentToTarget : ITargetSelector
{
    public bool IncludeTiles;
    public bool EnemiesOnly;

    public SelectAdjacentToTarget(bool includeTiles = true, bool enemiesOnly = false)
    {
        IncludeTiles = includeTiles;
        EnemiesOnly = enemiesOnly;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null) return false;

        if (!TargetingHelpers.TryGetAim(s, out var center)) return false;

        // Only resolved when a team filter is requested; harmless otherwise.
        var casterUnit = EnemiesOnly ? TargetingHelpers.FindCasterUnit(s, caster) : null;

        foreach (var neighbor in s.Grid.GetNeighbors(center))
        {
            var tile = s.Grid.GetTile(neighbor);
            if (tile == null) continue;

            if (IncludeTiles)
                targets.Items.Add(tile);
            else if (tile.Occupant != null && tile.Occupant.Stats.IsAlive
                     && (!EnemiesOnly
                         || TargetingHelpers.PassesTeamFilter(tile.Occupant, casterUnit, true)))
                targets.Items.Add(tile.Occupant);
        }

        return targets.Items.Count > 0;
    }
}

/// <summary>Picks the single nearest enemy to the previous target (used by chain-bounce patterns). Excludes the caster and any unit already hit earlier in the chain.</summary>
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

/// <summary>Picks a single tile of a named element. Prefers the player's aim point when it matches, otherwise falls back to the nearest matching tile within range.</summary>
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

/// <summary>
/// Selects the memorial tile nearest to the caster. Used by upgrade tiers that
/// summon "at the nearest other memorial" — by resolution time the primary
/// memorial has been consumed, so the nearest remaining one IS the other.
/// JSON: { "type": "nearest_memorial" }
/// </summary>
public sealed class SelectNearestMemorialTarget : ITargetSelector
{
    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();
        if (s?.Grid == null || s.Memorials == null) return false;

        var casterUnit = TargetingHelpers.FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null) return false;

        var center = casterUnit.CurrentTile.Axial;
        TileData best = null;
        int bestDist = int.MaxValue;
        foreach (var tile in s.Memorials.GetAllMemorials())
        {
            int dist = s.Grid.Distance(center, tile.Axial);
            if (dist < bestDist) { bestDist = dist; best = tile; }
        }

        if (best == null) return false;
        targets.Items.Add(best);
        return true;
    }
}
