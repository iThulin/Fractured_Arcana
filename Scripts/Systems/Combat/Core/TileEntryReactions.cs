using Godot;
using System.Collections.Generic;

// ============================================================
// TileEntryReactions.cs
//
// Purpose:        The tile-entry keystone from tile_interaction_spec
//                 (§2–§4). Defines MovementKind, the per-resolution
//                 MoveContext (loop guards), and the element/terrain
//                 verbs that fire when a unit ENTERS a tile: Fire
//                 Sears, Storm Conducts, Stone Anchors, Growth Grasps,
//                 Memorial Stirs, plus falling damage and the Frost
//                 slide continuation.
// Layer:          Combat core
// Collaborators:  Unit.PlaceOnTile (the single choke point that calls
//                 these), HexGridManager (neighbors), TileData
//                 (ElementType / Height / GrowthStage / Memorial),
//                 the push/pull/slide effects (own the MoveContext).
// See:            docs/tile_interaction_spec_v1.md §2, §3, §4
// ============================================================
//
// Ruleset frozen for v1 (see the spec's §10 "Open Rulings", resolved
// 2026-08-06):
//   - Fire Sears on ANY entry (Walked / Forced / Teleport) + standing
//     (standing is the pre-existing IsHazardous tick, untouched here).
//   - Frost Slides on Walked and Forced entry (not Teleport).
//   - Storm Conducts, Stone Anchors, Growth Grasps, Memorial Stirs are
//     Forced-only. The Walked/Forced distinction still governs them.
//   - Chain shove is depth-1; ElementStrength intensity is parked.
//   - Deprecated elements (Water/Arcane/Shadow) and reserved Air have
//     no verb by design.

/// <summary>How a unit arrived on a tile. The Walked/Forced split is the
/// design spine (spec §2.1): Forced-only verbs are the payoff for shoving an
/// enemy, Teleport crosses no intervening tiles.</summary>
public enum MovementKind
{
    Walked,
    Forced,
    Teleport
}

/// <summary>Per-resolution movement scope. One instance is created by each
/// forced-move effect (or walk commit) and threaded through every
/// <see cref="Unit.PlaceOnTile"/> call in that resolution, so the §2.2 loop
/// guards (the 10-tile force cap, the once-per-tile reaction guard, and the
/// Stone/cap halt signal) hold across slides, chains, and multi-step pushes.
/// A null context (bare teleport / summon placement) means "single entry, no
/// guards, no slide", and every reaction still fires exactly once.</summary>
public sealed class MoveContext
{
    /// <summary>Grid handle for adjacency (Storm Conducts) and slide lookahead.</summary>
    public readonly HexGridManager Grid;

    /// <summary>§2.2 hard cap: a single resolution may force-move a unit at most
    /// 10 tiles total (slides and chains included). Decremented per forced step.</summary>
    public int ForcedTilesRemaining = 10;

    /// <summary>Set by Stone Anchors, by hitting the tile cap, or by any effect
    /// that wants the current shove to stop. Movers break their loop on it.</summary>
    public bool HaltForced = false;

    /// <summary>Pull sets this: being pulled is positioning, not a hazard, so it
    /// deals no falling damage (mirrors pull's existing no-collision asymmetry).</summary>
    public bool SuppressFalling = false;

    private HashSet<(ulong unit, Vector2I tile)> _reacted;

    public MoveContext(HexGridManager grid) { Grid = grid; }

    /// <summary>Records that <paramref name="u"/> has reacted to <paramref name="t"/>
    /// this resolution. Returns true the FIRST time (caller should run reactions),
    /// false on repeats (skip, which prevents glyph↔push↔glyph ping-pong, spec §2.2).</summary>
    public bool MarkReacted(Unit u, TileData t)
    {
        _reacted ??= new HashSet<(ulong, Vector2I)>();
        return _reacted.Add((u.GetInstanceId(), t.Axial));
    }
}

/// <summary>The tile-entry verbs. Called by <see cref="Unit.PlaceOnTile"/> in a
/// fixed order (element verb → glyph → statuses, spec §2.2). Stateless; all
/// per-resolution memory lives on the passed <see cref="MoveContext"/>.</summary>
public static class TileEntryReactions
{
    /// <summary>Element / terrain verbs plus falling damage. Runs once per
    /// (unit, tile) per resolution, BEFORE glyph and status reactions.</summary>
    public static void ApplyElementVerbs(
        Unit unit, TileData tile, TileData previousTile, MovementKind kind, MoveContext ctx)
    {
        if (unit == null || tile == null || !unit.Stats.IsAlive)
            return;

        // Colossus form absorbs its tile through its own PlaceOnTile branch, so it
        // must not ALSO be seared/conducted by the same element it is eating.
        if (unit.HasStatus("colossus_absorb"))
            return;

        bool forced = kind == MovementKind.Forced;

        // ── Falling (spec §4.3): force-moved off a height drop ≥2 → 3 per step. ──
        // Pull suppresses this (positioning, not a hazard). Uphill shoves are made
        // illegal mover-side, so a positive drop here is always a fall.
        if (forced && previousTile != null && !(ctx?.SuppressFalling ?? false))
        {
            int drop = previousTile.Height - tile.Height;
            if (drop >= 2)
            {
                unit.ApplyDamage(3 * drop);
                if (!unit.Stats.IsAlive)
                    return;
            }
        }

        // ── Element verbs, keyed on the imbued ElementType layer (NOT TerrainType). ──
        switch (tile.ElementType)
        {
            case TileElementType.Fire:
                // Sears: 2 on any entry (ruling 10.2, so walking sears too).
                unit.ApplyDamage(2);
                break;

            case TileElementType.Lightning: // Storm
                // Conducts (Forced only): 2 to the enterer and every adjacent unit.
                if (forced)
                {
                    unit.ApplyDamage(2);
                    if (unit.Stats.IsAlive && ctx?.Grid != null)
                    {
                        foreach (var nb in ctx.Grid.GetNeighbors(tile.Axial))
                        {
                            var occ = ctx.Grid.GetTile(nb)?.Occupant;
                            if (occ != null && occ != unit && occ.Stats.IsAlive)
                                occ.ApplyDamage(2);
                        }
                    }
                }
                break;

            case TileElementType.Earth: // Stone
                // Anchors (Forced only): the shove stops here, no crush damage.
                if (forced && ctx != null)
                    ctx.HaltForced = true;
                break;
        }

        if (!unit.Stats.IsAlive)
            return;

        // ── Growth ≥2 (Thicket) Grasps (Forced only): Root the owner's foes 1 turn. ──
        if (forced && tile.GrowthStage >= 2 && tile.GrowthOwner != null
            && tile.GrowthOwner.TeamId != unit.TeamId)
        {
            unit.ApplyStatus("rooted", 1);
        }

        // ── Memorial Stirs (Forced only): 3 to an enterer hostile to the memorial's
        //    owner. The memorial is NOT consumed. ──
        if (forced && tile.HasMemorial && tile.Memorial.OwnerTeamId != unit.TeamId)
        {
            unit.ApplyDamage(3);
        }
    }

    /// <summary>Write an element onto a tile at RUNTIME (combat), updating data and
    /// visual together. Used by the enemy imbue-on-hit rider and any effect that
    /// needs a one-call imbue. Map GENERATION writes ElementType directly and lets
    /// the visual pass follow, so it does not use this. No-op for None.</summary>
    public static void ImbueTile(TileData tile, TileElementType element, float strength = 1f)
    {
        if (tile == null || element == TileElementType.None)
            return;
        tile.ElementType = element;
        tile.ElementStrength = strength;
        if (element == TileElementType.Fire)
            tile.IsHazardous = true;   // matches PaintElementPatch / imbue_tile
        tile.TileView?.SetElement(element);
    }

    /// <summary>Frost slide continuation (spec §3 "Slides"). After a tile's
    /// reactions resolve, a unit standing on Frost is carried one further tile in
    /// its direction of travel; recursion repeats the carry while it stays on
    /// Frost. Slides are Forced for all downstream triggers and obey the §2.2
    /// cap / halt. Teleports never slide (they cross no intervening tiles).</summary>
    public static void TrySlide(
        Unit unit, TileData tile, TileData previousTile, MovementKind kind, MoveContext ctx)
    {
        if (ctx?.Grid == null || kind == MovementKind.Teleport)
            return;
        if (tile.ElementType != TileElementType.Frost)
            return;
        if (ctx.HaltForced || ctx.ForcedTilesRemaining <= 0)
            return;
        if (previousTile == null || unit.CurrentTile != tile || !unit.Stats.IsAlive)
            return;

        // Direction of travel = the step just taken; only a single hex step yields
        // a slide vector (a multi-tile teleport delta is not a direction).
        var dir = tile.Axial - previousTile.Axial;
        if (dir == Vector2I.Zero
            || HexGridManager.AxialDistance(previousTile.Axial, tile.Axial) != 1)
            return;

        var next = ctx.Grid.GetTile(tile.Axial + dir);
        if (next == null || !next.CanEnter(unit))
            return; // wall / edge / occupant stops the slide (normal collision rules)

        ctx.ForcedTilesRemaining--;
        if (ctx.ForcedTilesRemaining <= 0)
            ctx.HaltForced = true;

        unit.PlaceOnTile(next, MovementKind.Forced, ctx);
    }
}
