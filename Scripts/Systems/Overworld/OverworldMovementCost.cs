using Godot;

// ============================================================
// OverworldMovementCost.cs
//
// Purpose:        Single source of truth for overworld movement cost.
//                 Both the controller that CHARGES the move
//                 (ExpeditionManager.OnPartyMoved) and the UI that
//                 PREVIEWS it (OverworldPartyToken.HighlightMoveOptions)
//                 call these methods, so the highlighted number always
//                 equals the cost actually paid — they can't diverge.
//
//                 Step cost = destination terrain cost, then a road on
//                 the travelled edge cheapens it (floored at 1) and an
//                 unbridged river adds a ford penalty. A bridge is a road
//                 over a river (road && river on the same edge): it takes
//                 the road discount and skips the ford. Edges are read off
//                 the tile being LEFT; the 6-bit mask is mirrored onto both
//                 tiles, so either side carries the shared edge.
// Layer:          System (shared helper)
// Collaborators:  OverworldHex (RoadEdges/RiverEdges + Terrain),
//                 HexCoord (AxialDirections), ExpeditionManager,
//                 OverworldPartyToken.
// ============================================================

public static class OverworldMovementCost
{
    // ── Tuning (the single place these magnitudes live) ──────────────────

    /// <summary>Subtracted from a step that travels along a road edge. Cost is
    /// floored at 1, so a road never makes a move free — just the cheapest.</summary>
    public static int RoadDiscount = 1;

    /// <summary>Added to a step that crosses an unbridged river edge (a ford).</summary>
    public static int FordPenalty = 2;

    // ── Terrain tables ───────────────────────────────────────────────────

    /// <summary>Base step cost of entering a tile of this terrain. Mirrors the
    /// relative ordering of the generation cost field so roads prefer the same
    /// ground the player finds cheap. Water/Lake are never entered (blocked in
    /// OverworldPartyToken.TryMoveTo); Road is vestigial (roads are edges now).</summary>
    public static int TerrainStep(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Grassland => 1,
        OverworldHex.TerrainType.ArcaneGround => 1,
        OverworldHex.TerrainType.Coast => 1,
        OverworldHex.TerrainType.Forest => 2,
        OverworldHex.TerrainType.Ruins => 2,
        OverworldHex.TerrainType.Hills => 2,
        OverworldHex.TerrainType.Desert => 2,
        OverworldHex.TerrainType.Tundra => 2,
        OverworldHex.TerrainType.Swamp => 3,
        OverworldHex.TerrainType.Marsh => 3,
        OverworldHex.TerrainType.Volcanic => 3,
        OverworldHex.TerrainType.Mountain => 4,
        OverworldHex.TerrainType.Snow => 4,
        _ => 1,
    };

    /// <summary>HP lost on entering hazardous terrain. Rivers cost STEPS not HP
    /// (a routing obstacle, not a hazard), so they don't appear here.</summary>
    public static int TerrainHPDrain(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Swamp => 3,
        OverworldHex.TerrainType.Marsh => 2,
        OverworldHex.TerrainType.Snow => 2,
        OverworldHex.TerrainType.Volcanic => GD.Randf() < 0.3f ? 5 : 0,
        _ => 0,
    };

    // ── Edge-adjusted step cost ──────────────────────────────────────────

    /// <summary>Full step cost for moving from `fromHex` across the shared edge into
    /// the destination terrain. fromHex may be null (e.g. window fringe) — then only
    /// terrain cost applies.</summary>
    public static int StepCost(OverworldHex.TerrainType destTerrain,
                               OverworldHex fromHex, Vector2I from, Vector2I to)
    {
        int cost = TerrainStep(destTerrain);

        int d = EdgeDirection(from, to);
        if (d >= 0 && fromHex != null)
        {
            int bit = 1 << d;
            bool road = (fromHex.RoadEdges & bit) != 0;
            bool river = (fromHex.RiverEdges & bit) != 0;
            bool bridge = road && river;   // road over a river

            if (road)
                cost -= RoadDiscount;       // includes bridges (a bridge is a road)
            if (river && !bridge)
                cost += FordPenalty;        // unbridged ford
        }

        return Mathf.Max(1, cost);
    }

    /// <summary>True if a road runs along the edge from `from` to `to`.</summary>
    public static bool EdgeHasRoad(OverworldHex fromHex, Vector2I from, Vector2I to)
    {
        int d = EdgeDirection(from, to);
        return d >= 0 && fromHex != null && (fromHex.RoadEdges & (1 << d)) != 0;
    }

    /// <summary>True if an UNBRIDGED river runs along the edge (a ford). A bridge —
    /// a road on the same edge — is not a ford.</summary>
    public static bool EdgeHasUnbridgedRiver(OverworldHex fromHex, Vector2I from, Vector2I to)
    {
        int d = EdgeDirection(from, to);
        if (d < 0 || fromHex == null)
            return false;
        int bit = 1 << d;
        return (fromHex.RiverEdges & bit) != 0 && (fromHex.RoadEdges & bit) == 0;
    }

    // ── Hex direction ────────────────────────────────────────────────────

    /// <summary>Index 0..5 of the AxialDirections step from `from` to adjacent `to`,
    /// matching the bit convention the edge masks were stamped with. -1 if `to` isn't
    /// an axial neighbour of `from`.</summary>
    public static int EdgeDirection(Vector2I from, Vector2I to)
    {
        int dq = to.X - from.X;
        int dr = to.Y - from.Y;
        for (int d = 0; d < 6; d++)
        {
            var (adq, adr) = HexCoord.AxialDirections[d];
            if (adq == dq && adr == dr)
                return d;
        }
        return -1;
    }
}
