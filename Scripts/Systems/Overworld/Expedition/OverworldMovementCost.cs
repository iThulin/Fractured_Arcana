using Godot;

// ============================================================
// OverworldMovementCost.cs
//
// Purpose:        Single source of truth for overworld movement cost.
//                 Both the controller that CHARGES the move
//                 (ExpeditionManager.OnPartyMoved) and the UI that
//                 PREVIEWS it (OverworldPartyToken.HighlightMoveOptions)
//                 call these methods, so the highlighted number always
//                 equals the cost actually paid; they can't diverge.
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
    /// floored at 1, so a road never makes a move free, just the cheapest.</summary>
    public static int RoadDiscount = 1;

    /// <summary>Added to a step that crosses an unbridged river edge (a ford).</summary>
    public static int FordPenalty = 2;

    // ── Castle movement signature (Mobile Fortress §4) ───────────────────
    // Static ambient set once per sortie by ExpeditionManager from the active
    // CastleTypeDef, read inside StepCost so the preview and the charge apply
    // the identical modifier (mirrors the OverworldSpellEffects pattern). These
    // are all STATELESS per-edge modifiers. The Chronomancer's stateful
    // first-3-flat quirk is handled at the charge site, NOT here.
    public static System.Collections.Generic.HashSet<OverworldHex.TerrainType> CastleCheapTerrains = new();
    public static int CastleTerrainDiscount = 0;   // subtracted for a CheapTerrains destination
    public static int CastleExtraRoadDiscount = 0; // added to RoadDiscount (Tinker)
    public static bool CastleWaiveFord = false;    // Enchanter

    /// <summary>Crew Helm station (§5): multiplies the finished per-tile burn
    /// (1.0 = none, 0.9 = −10%). Static ambient set at deploy, applied inside
    /// StepCost so the preview and the charge agree (G1). Floored at 1.</summary>
    public static float CrewFuelMultiplier = 1f;

    /// <summary>Clear the castle signature (fresh deploy resets it before configuring).</summary>
    public static void ResetCastle()
    {
        CastleCheapTerrains = new System.Collections.Generic.HashSet<OverworldHex.TerrainType>();
        CastleTerrainDiscount = 0;
        CastleExtraRoadDiscount = 0;
        CastleWaiveFord = false;
        CrewFuelMultiplier = 1f;
    }

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
    /// the destination terrain. fromHex may be null (e.g. window fringe); then only
    /// terrain cost applies. `pathfinderReduction` (Q3 §7b) subtracts from the
    /// terrain cost for a matching terrain; the final Max(1,…) keeps the floor-1
    /// rule so Pathfinder never makes a move free ("relief is bought, immunity
    /// does not exist"). Both callers (charge + preview) pass the same value so
    /// the highlighted cost can't diverge from the cost paid.</summary>
    public static int StepCost(OverworldHex.TerrainType destTerrain,
                               OverworldHex fromHex, Vector2I from, Vector2I to,
                               int pathfinderReduction = 0)
    {
        int cost = TerrainStep(destTerrain) - Mathf.Max(0, pathfinderReduction);

        // S2: active traversal spells (Verdant Passage) cap the terrain
        // component. Applied HERE, the single source of truth, so the
        // charge path and the preview path cannot diverge (G1: reduction
        // within a bounded window, never a refund).
        cost = OverworldSpellEffects.AdjustTerrainStep(destTerrain, cost);

        // §4 castle movement signature: the school's chassis strides its home
        // terrain cheaper (e.g. Verdant Ark on Forest/Swamp).
        if (CastleTerrainDiscount > 0 && CastleCheapTerrains.Contains(destTerrain))
            cost -= CastleTerrainDiscount;

        int d = EdgeDirection(from, to);
        if (d >= 0 && fromHex != null)
        {
            int bit = 1 << d;
            bool road = (fromHex.RoadEdges & bit) != 0;
            bool river = (fromHex.RiverEdges & bit) != 0;
            bool bridge = road && river;   // road over a river

            if (road)
                cost -= RoadDiscount + CastleExtraRoadDiscount; // §4 Gearspire doubles the road discount
            if (river && !bridge && !CastleWaiveFord)           // §4 Lantern Keep waives the ford
                cost += FordPenalty;
        }

        // Weather (W2): a front over the destination raises the burn. Applied
        // HERE, in the single source of truth, so the preview ribbon and the
        // charge cannot diverge (G1). WeatherAt returns Clear (0) when inactive.
        cost += WeatherCatalog.Def(WeatherSystem.WeatherAt(to)).FuelPerTile;

        // §5 crew Helm: shave the finished burn (preview and charge alike).
        if (CrewFuelMultiplier < 1f)
            cost = Mathf.RoundToInt(cost * CrewFuelMultiplier);

        return Mathf.Max(1, cost);
    }

    // ── Step 3 (convergence spec): WorldTile overloads ───────────────────
    // Same math, fed by the world's tile instead of a render node. WorldTile
    // carries the identical both-sides edge masks. The node overloads remain
    // for OverworldPartyToken's preview until Step 4 turns tokens into views.

    /// <summary>WorldTile overload of the node StepCost. Null fromTile =
    /// off-world/fringe: terrain cost only, matching the null-node case.</summary>
    public static int StepCost(OverworldHex.TerrainType destTerrain,
                               WorldTile? fromTile, Vector2I from, Vector2I to,
                               int pathfinderReduction = 0)
    {
        int cost = TerrainStep(destTerrain) - Mathf.Max(0, pathfinderReduction);
        cost = OverworldSpellEffects.AdjustTerrainStep(destTerrain, cost);

        // §4 castle movement signature (same as the node overload).
        if (CastleTerrainDiscount > 0 && CastleCheapTerrains.Contains(destTerrain))
            cost -= CastleTerrainDiscount;

        int d = EdgeDirection(from, to);
        if (d >= 0 && fromTile.HasValue)
        {
            int bit = 1 << d;
            bool road = (fromTile.Value.RoadEdges & bit) != 0;
            bool river = (fromTile.Value.RiverEdges & bit) != 0;
            bool bridge = road && river;   // road over a river

            if (road)
                cost -= RoadDiscount + CastleExtraRoadDiscount; // §4 Gearspire
            if (river && !bridge && !CastleWaiveFord)           // §4 Lantern Keep
                cost += FordPenalty;
        }

        // Weather (W2): same front surcharge as the node overload. Preview
        // and charge read the identical WeatherAt(to), so they cannot diverge.
        cost += WeatherCatalog.Def(WeatherSystem.WeatherAt(to)).FuelPerTile;

        // §5 crew Helm: shave the finished burn (preview and charge alike).
        if (CrewFuelMultiplier < 1f)
            cost = Mathf.RoundToInt(cost * CrewFuelMultiplier);

        return Mathf.Max(1, cost);
    }

    /// <summary>WorldTile overload of the node EdgeHasRoad.</summary>
    public static bool EdgeHasRoad(WorldTile? fromTile, Vector2I from, Vector2I to)
    {
        int d = EdgeDirection(from, to);
        return d >= 0 && fromTile.HasValue && (fromTile.Value.RoadEdges & (1 << d)) != 0;
    }

    /// <summary>WorldTile overload of the node EdgeHasUnbridgedRiver.</summary>
    public static bool EdgeHasUnbridgedRiver(WorldTile? fromTile, Vector2I from, Vector2I to)
    {
        int d = EdgeDirection(from, to);
        if (d < 0 || !fromTile.HasValue)
            return false;
        int bit = 1 << d;
        return (fromTile.Value.RiverEdges & bit) != 0 && (fromTile.Value.RoadEdges & bit) == 0;
    }

    /// <summary>True if a road runs along the edge from `from` to `to`.</summary>
    public static bool EdgeHasRoad(OverworldHex fromHex, Vector2I from, Vector2I to)
    {
        int d = EdgeDirection(from, to);
        return d >= 0 && fromHex != null && (fromHex.RoadEdges & (1 << d)) != 0;
    }

    /// <summary>True if an UNBRIDGED river runs along the edge (a ford). A bridge
    /// (a road on the same edge) is not a ford.</summary>
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
