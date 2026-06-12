using Godot;
using System;

// ============================================================
// HexMeshBuilder.cs  (v4 — cliffs)
//
// Purpose:        Generates the per-tile blended terrain mesh.
//                 Each tile owns its full hexagon footprint: a
//                 flat inner hex at the tile's own height, and six
//                 bridge strips out to the shared boundaries, with
//                 every boundary edge split at its midpoint.
//
//                 v4 — EDGE CLASSIFICATION:
//                 - BLEND edge (|Δheight| ≤ grid.CliffHeightThreshold):
//                   midpoint averages the two edge tiles; smooth
//                   (or terraced) transition, as in v3.
//                 - CLIFF edge (|Δheight| > threshold): both tiles
//                   stay flat at their OWN height to the boundary,
//                   and the HIGHER tile builds a vertical wall
//                   sealing the gap down to the lower tile's rim.
//                 Corner heights average over the BLEND-CONNECTED
//                 COMPONENT of tiles at that corner (pairwise
//                 |Δh| ≤ threshold, transitively closed) instead of
//                 all tiles blindly — tiles across a cliff stop
//                 pulling each other's corners. The component rule
//                 is symmetric, so blend seams remain watertight,
//                 and the wall's top/bottom profiles match both
//                 rims exactly (both are computed from the same
//                 height data, available to either tile).
//                 Where a cliff edge reaches a corner whose tiles
//                 chain together through blends (e.g. 0–2–4 with
//                 threshold 2), the wall tapers to zero height
//                 there — a cliff dying into a ramp. Intended.
//
//                 The same grid.CliffHeightThreshold must drive the
//                 movement rule (StepAllowed in HexGridManager) so
//                 the visual and the gameplay never disagree.
//
//                 TWO ATTRIBUTE MODES:
//                 - Vertex-colour mode (splatMode = false): COLOR
//                   carries blended terrain albedo.
//                 - Splat mode (splatMode = true): CUSTOM0 carries
//                   four terrain layer indices (CONSTANT across
//                   every triangle) and COLOR carries blend weights.
//                   Slot layout per edge: [own, edge neighbour,
//                   corner-e third tile, corner-(e+1) third tile].
//                   Corner weights distribute over the corner's
//                   blend component; cliff boundaries don't blend.
//                   Cliff walls inherit the rim attributes with
//                   vertical continuity — in splat mode the
//                   triplanar shader textures the face without
//                   stretching and darkens it by normal.
//
// Layer:          System (generation helper)
// Collaborators:  HexGridManager (grid, CliffHeightThreshold,
//                 terrain materials/textures), HexTile (HeightStep,
//                 SetGeneratedMesh), HexDirection, UITheme,
//                 TerrainTextureLibrary / terrain_splat.gdshader,
//                 HexGridManager.Props (surface sampler)
//
// Geometry conventions (verified against MovementZoneRenderer):
//   - Flat-top hexes. Corner i at angle 60°·i from +X, CCW.
//   - Edge e spans corners e and e+1, faces neighbour direction
//     d = (6 - e) % 6 (the EdgeForDir reflection).
//   - Corner i is shared with neighbour directions (7 - i) % 6
//     (slot "A") and (6 - i) % 6 (slot "B").
//   - Godot front faces wind clockwise; up-facing surfaces are CCW
//     in the (X, Z) math plane. Walls/skirts face outward.
//   - FLAT SHADING on purpose: per-tile meshes cannot average
//     normals with neighbouring meshes.
// ============================================================

public static class HexMeshBuilder
{
    /// <summary>Vertex-colour multiplier at the bottom of map-edge skirts (vertex-colour mode only).</summary>
    private const float SkirtFloorDarkening = 0.5f;

    /// <summary>Vertex-colour multiplier at the bottom of cliff walls (vertex-colour mode only; splat mode darkens by normal in-shader).</summary>
    private const float CliffFaceDarkening = 0.7f;

    /// <summary>Height delta below which a bridge half is built as a single quad even when terracing is on.</summary>
    private const float FlatEpsilon = 0.01f;

    // ────────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the blended/cliff mesh for one tile, in HexTile-local space
    /// (origin at the tile's top-surface centre; inner hexagon at local Y = 0).
    /// Reads grid.CliffHeightThreshold for edge classification.
    /// </summary>
    public static ArrayMesh Build(
        HexGridManager grid, TileData tile,
        float worldFloor, float solidFactor, int terraceSteps, bool splatMode = false)
    {
        if (tile.TileView == null)
            return null;

        float radius = grid.HexRadius;
        float ownTop = tile.Height * HexTile.HeightStep;
        float floorLocal = worldFloor - ownTop;
        Vector2 centerXZ = new Vector2(tile.TileView.GlobalPosition.X, tile.TileView.GlobalPosition.Z);
        int threshold = grid.CliffHeightThreshold;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetSmoothGroup(uint.MaxValue); // flat shading — see header
        if (splatMode)
            st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);

        // ── Shared blend inputs ───────────────────────────────────────────
        float ownIdx = (int)tile.TerrainType;
        Color ownColor = TerrainColor(grid, tile.TerrainType);
        Color ownWeights = new Color(1f, 0f, 0f, 0f);
        Color ownIndices = new Color(ownIdx, ownIdx, ownIdx, ownIdx);

        // Per-corner: component-averaged height (own's component) + colour.
        var cornerY = new float[6];
        var cornerColor = new Color[6];
        for (int i = 0; i < 6; i++)
        {
            cornerY[i] = CornerWorldY(grid, tile, i) - ownTop;
            if (!splatMode)
                cornerColor[i] = CornerColor(grid, tile, i);
        }

        // Per-edge: neighbour, classification, midpoint data, splat data.
        var nbr = new TileData[6];
        var isCliff = new bool[6];
        var midY = new float[6];
        var midColor = new Color[6];
        var edgeIndices = new Color[6];
        var edgeWeightsA = new Color[6];   // splat weights at corner e (own's component)
        var edgeWeightsB = new Color[6];   // splat weights at corner e+1 (own's component)
        var edgeWeightsM = new Color[6];   // splat weights at the edge midpoint

        for (int e = 0; e < 6; e++)
        {
            var nE = grid.GetTile(tile.Axial + HexDirection.All[(6 - e) % 6]);
            nbr[e] = nE;
            isCliff[e] = nE != null && Math.Abs(tile.Height - nE.Height) > threshold;

            bool blends = nE != null && !isCliff[e];
            midY[e] = blends
                ? ((tile.Height + nE.Height) * 0.5f) * HexTile.HeightStep - ownTop
                : 0f;

            if (splatMode)
            {
                (edgeIndices[e], edgeWeightsA[e], edgeWeightsB[e]) = SplatForEdge(grid, tile, e);
                edgeWeightsM[e] = blends ? new Color(0.5f, 0.5f, 0f, 0f) : ownWeights;
            }
            else
            {
                if (blends)
                {
                    Color mc = (ownColor + TerrainColor(grid, nE.TerrainType)) / 2f;
                    mc.A = 1f;
                    midColor[e] = mc;
                }
                else
                {
                    midColor[e] = ownColor;
                }
            }
        }

        // ── 1) Inner hexagon fan: 12 boundary points (corner, mid, corner…)
        Color innerAttr = splatMode ? ownWeights : ownColor;
        for (int e = 0; e < 6; e++)
        {
            Vector2 cA = Corner(e, radius) * solidFactor;
            Vector2 cB = Corner((e + 1) % 6, radius) * solidFactor;
            Vector2 m = (cA + cB) * 0.5f;

            AddVert(st, Vector3.Zero, innerAttr, centerXZ, splatMode, ownIndices);
            AddVert(st, new Vector3(cA.X, 0f, cA.Y), innerAttr, centerXZ, splatMode, ownIndices);
            AddVert(st, new Vector3(m.X, 0f, m.Y), innerAttr, centerXZ, splatMode, ownIndices);

            AddVert(st, Vector3.Zero, innerAttr, centerXZ, splatMode, ownIndices);
            AddVert(st, new Vector3(m.X, 0f, m.Y), innerAttr, centerXZ, splatMode, ownIndices);
            AddVert(st, new Vector3(cB.X, 0f, cB.Y), innerAttr, centerXZ, splatMode, ownIndices);
        }

        // ── 2) Bridge strips: two halves per edge (corner→mid, mid→corner)
        for (int e = 0; e < 6; e++)
        {
            int e2 = (e + 1) % 6;
            Vector2 cA = Corner(e, radius);
            Vector2 cB = Corner(e2, radius);
            Vector2 mid = (cA + cB) * 0.5f;

            Color idx = splatMode ? edgeIndices[e] : ownIndices;
            Color inA = splatMode ? ownWeights : ownColor;
            Color attrA = splatMode ? edgeWeightsA[e] : cornerColor[e];
            Color attrB = splatMode ? edgeWeightsB[e] : cornerColor[e2];
            Color attrM = splatMode ? edgeWeightsM[e] : midColor[e];

            AddBridgeHalf(st, centerXZ, splatMode, idx, solidFactor, terraceSteps,
                cA, cornerY[e], attrA, mid, midY[e], attrM, inA);
            AddBridgeHalf(st, centerXZ, splatMode, idx, solidFactor, terraceSteps,
                mid, midY[e], attrM, cB, cornerY[e2], attrB, inA);
        }

        // ── 3) Cliff walls: the HIGHER tile seals the gap down to the
        //       lower tile's rim profile ──────────────────────────────────
        for (int e = 0; e < 6; e++)
        {
            if (!isCliff[e] || tile.Height <= nbr[e].Height)
                continue;

            int e2 = (e + 1) % 6;
            Vector2 cA = Corner(e, radius);
            Vector2 cB = Corner(e2, radius);
            Vector2 mid = (cA + cB) * 0.5f;

            // Wall top = own rim profile along this edge.
            float topYA = cornerY[e];
            float topYM = midY[e];           // 0 for cliff edges
            float topYB = cornerY[e2];

            // Wall bottom = the LOWER tile's rim profile, computed from the
            // same corner-component data (neighbour is slot B at corner e,
            // slot A at corner e+1 — see geometry conventions).
            float botYA = CornerComponentMeanWorldY(grid, tile, e, 2) - ownTop;
            float botYM = nbr[e].Height * HexTile.HeightStep - ownTop;
            float botYB = CornerComponentMeanWorldY(grid, tile, e2, 1) - ownTop;

            Color idx = splatMode ? edgeIndices[e] : ownIndices;
            Color attrA = splatMode ? edgeWeightsA[e] : cornerColor[e];
            Color attrB = splatMode ? edgeWeightsB[e] : cornerColor[e2];
            Color attrM = splatMode ? edgeWeightsM[e] : midColor[e];

            Color botA = attrA, botM = attrM, botB = attrB;
            if (!splatMode)
            {
                botA = attrA * CliffFaceDarkening;
                botA.A = 1f;
                botM = attrM * CliffFaceDarkening;
                botM.A = 1f;
                botB = attrB * CliffFaceDarkening;
                botB.A = 1f;
            }

            AddWallQuad(st, centerXZ, splatMode, idx,
                cA, topYA, attrA, mid, topYM, attrM,
                botYA, botYM, botA, botM);
            AddWallQuad(st, centerXZ, splatMode, idx,
                mid, topYM, attrM, cB, topYB, attrB,
                botYM, botYB, botM, botB);
        }

        // ── 4) Map-edge skirts down to the floor ─────────────────────────
        for (int e = 0; e < 6; e++)
        {
            if (nbr[e] != null)
                continue;

            int e2 = (e + 1) % 6;
            Vector2 cA = Corner(e, radius);
            Vector2 cB = Corner(e2, radius);
            Vector2 mid = (cA + cB) * 0.5f;

            Color idx = splatMode ? edgeIndices[e] : ownIndices;
            Color topA = splatMode ? edgeWeightsA[e] : cornerColor[e];
            Color topB = splatMode ? edgeWeightsB[e] : cornerColor[e2];
            Color topM = splatMode ? edgeWeightsM[e] : midColor[e];

            Color botA = topA, botM = topM, botB = topB;
            if (!splatMode)
            {
                botA = topA * SkirtFloorDarkening;
                botA.A = 1f;
                botM = topM * SkirtFloorDarkening;
                botM.A = 1f;
                botB = topB * SkirtFloorDarkening;
                botB.A = 1f;
            }

            AddWallQuad(st, centerXZ, splatMode, idx,
                cA, cornerY[e], topA, mid, midY[e], topM,
                floorLocal, floorLocal, botA, botM);
            AddWallQuad(st, centerXZ, splatMode, idx,
                mid, midY[e], topM, cB, cornerY[e2], topB,
                floorLocal, floorLocal, botM, botB);
        }

        st.GenerateNormals();
        return st.Commit();
    }

    /// <summary>
    /// World-space surface Y of the terrain at (worldX, worldZ), assuming the
    /// point lies on <paramref name="tile"/>. Mirrors the v4 geometry: cliff
    /// edges stay flat at the tile's own height; blend edges run piecewise
    /// corner→mid→corner with component-averaged corners. Used by prop
    /// scattering. Reads grid.CliffHeightThreshold.
    /// </summary>
    public static float SampleSurfaceWorldY(
        HexGridManager grid, TileData tile,
        float worldX, float worldZ, float solidFactor, int terraceSteps)
    {
        float ownTop = tile.Height * HexTile.HeightStep;
        if (tile.TileView == null)
            return ownTop;

        float radius = grid.HexRadius;
        Vector2 p = new Vector2(
            worldX - tile.TileView.GlobalPosition.X,
            worldZ - tile.TileView.GlobalPosition.Z);

        if (p.LengthSquared() < 0.0001f)
            return ownTop;

        float angDeg = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(p.Y, p.X)), 360f);
        int e = Mathf.Clamp((int)(angDeg / 60f), 0, 5);
        int e2 = (e + 1) % 6;

        Vector2 cE = Corner(e, radius);
        Vector2 cF = Corner(e2, radius);

        float det = cE.X * cF.Y - cF.X * cE.Y;
        float a = (p.X * cF.Y - cF.X * p.Y) / det;
        float b = (cE.X * p.Y - p.X * cE.Y) / det;

        float rho = a + b;
        if (rho <= solidFactor)
            return ownTop;

        rho = Mathf.Min(rho, 1f);
        float s = b / rho;
        float t = (rho - solidFactor) / (1f - solidFactor);

        float yE = CornerWorldY(grid, tile, e) - ownTop;
        float yF = CornerWorldY(grid, tile, e2) - ownTop;

        var nE = grid.GetTile(tile.Axial + HexDirection.All[(6 - e) % 6]);
        bool blends = nE != null && Math.Abs(tile.Height - nE.Height) <= grid.CliffHeightThreshold;
        float yM = blends
            ? ((tile.Height + nE.Height) * 0.5f) * HexTile.HeightStep - ownTop
            : 0f;

        float edgeY = s < 0.5f
            ? Mathf.Lerp(yE, yM, s * 2f)
            : Mathf.Lerp(yM, yF, (s - 0.5f) * 2f);

        if (terraceSteps <= 0 || Mathf.Abs(edgeY) < FlatEpsilon)
            return ownTop + edgeY * t;

        int steps = terraceSteps * 2 + 1;
        int k = Mathf.Clamp((int)(t * steps), 0, steps - 1);
        float frac = t * steps - k;
        float v0 = ((k + 1) / 2) / (float)(terraceSteps + 1);
        float v1 = ((k + 2) / 2) / (float)(terraceSteps + 1);
        return ownTop + edgeY * Mathf.Lerp(v0, v1, frac);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Corner components — the symmetric blend-connectivity rule.
    // Tiles at corner i: slot 0 = own, slot 1 = dir (7-i)%6, slot 2 = dir (6-i)%6.
    // Two tiles are blend-connected when |Δheight| ≤ threshold; components are
    // the transitive closure. Any two tiles sharing a blend edge land in the
    // same component → identical corner values → watertight blend seams.
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>World-space Y of corner <paramref name="cornerIndex"/> as this tile's mesh uses it: mean height of the blend component containing the tile.</summary>
    public static float CornerWorldY(HexGridManager grid, TileData tile, int cornerIndex)
        => CornerComponentMeanWorldY(grid, tile, cornerIndex, 0);

    /// <summary>Mean corner height (world Y) of the blend component containing the tile in <paramref name="startSlot"/> (0 = own, 1 = slot-A neighbour, 2 = slot-B neighbour).</summary>
    public static float CornerComponentMeanWorldY(HexGridManager grid, TileData tile, int cornerIndex, int startSlot)
    {
        var (m0, m1, m2, tA, tB) = CornerComponent(grid, tile, cornerIndex, startSlot);

        float sum = 0f;
        int n = 0;
        if (m0)
        { sum += tile.Height; n++; }
        if (m1)
        { sum += tA.Height; n++; }
        if (m2)
        { sum += tB.Height; n++; }

        if (n == 0)
            return tile.Height * HexTile.HeightStep; // start tile absent — defensive

        return (sum / n) * HexTile.HeightStep;
    }

    /// <summary>Blended terrain colour at a corner: mean over the blend component containing this tile.</summary>
    public static Color CornerColor(HexGridManager grid, TileData tile, int cornerIndex)
    {
        var (m0, m1, m2, tA, tB) = CornerComponent(grid, tile, cornerIndex, 0);

        Color sum = new Color(0f, 0f, 0f, 0f);
        int n = 0;
        if (m0)
        { sum += TerrainColor(grid, tile.TerrainType); n++; }
        if (m1)
        { sum += TerrainColor(grid, tA.TerrainType); n++; }
        if (m2)
        { sum += TerrainColor(grid, tB.TerrainType); n++; }

        if (n == 0)
            return TerrainColor(grid, tile.TerrainType);

        Color c = sum / n;
        c.A = 1f;
        return c;
    }

    /// <summary>
    /// Membership of the blend component containing the tile in startSlot.
    /// Closure over ≤3 nodes via two relaxation passes.
    /// </summary>
    private static (bool m0, bool m1, bool m2, TileData tA, TileData tB)
        CornerComponent(HexGridManager grid, TileData tile, int cornerIndex, int startSlot)
    {
        int threshold = grid.CliffHeightThreshold;

        var tA = grid.GetTile(tile.Axial + HexDirection.All[(7 - cornerIndex) % 6]);
        var tB = grid.GetTile(tile.Axial + HexDirection.All[(6 - cornerIndex) % 6]);

        int h0 = tile.Height;
        bool p1 = tA != null, p2 = tB != null;
        int h1 = p1 ? tA.Height : 0;
        int h2 = p2 ? tB.Height : 0;

        bool a01 = p1 && Math.Abs(h0 - h1) <= threshold;
        bool a02 = p2 && Math.Abs(h0 - h2) <= threshold;
        bool a12 = p1 && p2 && Math.Abs(h1 - h2) <= threshold;

        bool m0 = startSlot == 0;
        bool m1 = startSlot == 1 && p1;
        bool m2 = startSlot == 2 && p2;

        for (int pass = 0; pass < 2; pass++)
        {
            if (m0)
            { m1 |= a01; m2 |= a02; }
            if (m1)
            { m0 |= a01; m2 |= a12; }
            if (m2)
            { m0 |= a02; m1 |= a12; }
        }

        return (m0, m1 && p1, m2 && p2, tA, tB);
    }

    /// <summary>
    /// Splat data for one edge. Index slots: [own, edge-e neighbour,
    /// corner-e third tile, corner-(e+1) third tile]. Corner weights
    /// distribute equally over the corner's blend component (containing own),
    /// matching the geometry's corner heights, so texture blends track the
    /// surface exactly and stop at cliffs. ALL vertices of the edge's bridge
    /// halves, walls, and skirts share the index vector — per-triangle index
    /// constancy keeps GPU interpolation valid.
    /// </summary>
    private static (Color indices, Color weightsCornerA, Color weightsCornerB)
        SplatForEdge(HexGridManager grid, TileData tile, int e)
    {
        float ownIdx = (int)tile.TerrainType;

        var nE = grid.GetTile(tile.Axial + HexDirection.All[(6 - e) % 6]);     // edge neighbour
        var nA = grid.GetTile(tile.Axial + HexDirection.All[(7 - e) % 6]);     // third tile at corner e
        var nB = grid.GetTile(tile.Axial + HexDirection.All[(5 - e) % 6]);     // third tile at corner e+1

        var indices = new Color(
            ownIdx,
            nE != null ? (int)nE.TerrainType : ownIdx,
            nA != null ? (int)nA.TerrainType : ownIdx,
            nB != null ? (int)nB.TerrainType : ownIdx);

        // Corner e: corner-slot A = third tile (nA), corner-slot B = nE.
        var (c0, cThird, cNe, _, _) = CornerComponent(grid, tile, e, 0);
        int cntA = (c0 ? 1 : 0) + (cThird ? 1 : 0) + (cNe ? 1 : 0);
        float wA = 1f / Math.Max(1, cntA);
        var weightsCornerA = new Color(c0 ? wA : 0f, cNe ? wA : 0f, cThird ? wA : 0f, 0f);

        // Corner e+1: corner-slot A = nE, corner-slot B = third tile (nB).
        var (d0, dNe, dThird, _, _) = CornerComponent(grid, tile, (e + 1) % 6, 0);
        int cntB = (d0 ? 1 : 0) + (dNe ? 1 : 0) + (dThird ? 1 : 0);
        float wB = 1f / Math.Max(1, cntB);
        var weightsCornerB = new Color(d0 ? wB : 0f, dNe ? wB : 0f, 0f, dThird ? wB : 0f);

        return (indices, weightsCornerA, weightsCornerB);
    }

    /// <summary>
    /// Canonical terrain colour: the exported terrain material's albedo when
    /// one is assigned, otherwise the UITheme.CombatTile* token — the same
    /// source the legacy flat-colour path uses. No local palette.
    /// </summary>
    public static Color TerrainColor(HexGridManager grid, TileTerrainType terrain)
    {
        Material m = terrain switch
        {
            TileTerrainType.Grass => grid.GrassMaterial,
            TileTerrainType.Forest => grid.ForestMaterial,
            TileTerrainType.Stone => grid.StoneMaterial,
            TileTerrainType.Water => grid.WaterMaterial,
            TileTerrainType.Arcane => grid.ArcaneMaterial,
            TileTerrainType.Ice => grid.IceMaterial,
            TileTerrainType.Lava => grid.LavaMaterial,
            _ => null
        };

        if (m is StandardMaterial3D std)
            return std.AlbedoColor;

        return terrain switch
        {
            TileTerrainType.Grass => UITheme.CombatTileGrass,
            TileTerrainType.Forest => UITheme.CombatTileForest,
            TileTerrainType.Stone => UITheme.CombatTileStone,
            TileTerrainType.Water => UITheme.CombatTileWater,
            TileTerrainType.Lava => UITheme.CombatTileLava,
            TileTerrainType.Arcane => UITheme.CombatTileArcane,
            TileTerrainType.Ice => UITheme.CombatTileIce,
            _ => Colors.White
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // Internals
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>XZ offset from hex centre to corner i (flat-top, corner 0 at +X, CCW).</summary>
    private static Vector2 Corner(int i, float radius)
    {
        float a = Mathf.DegToRad(60f * i);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
    }

    /// <summary>
    /// One half of a bridge strip: boundary point A (outerA, yA) to boundary
    /// point B (outerB, yB), inner-ring points at outerA·solid / outerB·solid
    /// (local Y = 0). Terraced when requested and the half changes height.
    /// </summary>
    private static void AddBridgeHalf(SurfaceTool st, Vector2 centerXZ, bool splat, Color idx,
        float solidFactor, int terraceSteps,
        Vector2 outerA, float yA, Color attrA,
        Vector2 outerB, float yB, Color attrB,
        Color innerAttr)
    {
        Vector2 innerA = outerA * solidFactor;
        Vector2 innerB = outerB * solidFactor;

        bool flat = Mathf.Abs(yA) < FlatEpsilon && Mathf.Abs(yB) < FlatEpsilon;

        if (terraceSteps <= 0 || flat)
        {
            AddQuad(st, centerXZ, splat, idx,
                new Vector3(innerA.X, 0f, innerA.Y), innerAttr,
                new Vector3(innerB.X, 0f, innerB.Y), innerAttr,
                new Vector3(outerA.X, yA, outerA.Y), attrA,
                new Vector3(outerB.X, yB, outerB.Y), attrB);
            return;
        }

        int steps = terraceSteps * 2 + 1;
        for (int k = 0; k < steps; k++)
        {
            float t0 = (float)k / steps;
            float t1 = (float)(k + 1) / steps;
            float v0 = ((k + 1) / 2) / (float)(terraceSteps + 1);
            float v1 = ((k + 2) / 2) / (float)(terraceSteps + 1);

            Vector2 a0 = innerA.Lerp(outerA, t0);
            Vector2 a1 = innerA.Lerp(outerA, t1);
            Vector2 b0 = innerB.Lerp(outerB, t0);
            Vector2 b1 = innerB.Lerp(outerB, t1);

            AddQuad(st, centerXZ, splat, idx,
                new Vector3(a0.X, yA * v0, a0.Y), innerAttr.Lerp(attrA, t0),
                new Vector3(b0.X, yB * v0, b0.Y), innerAttr.Lerp(attrB, t0),
                new Vector3(a1.X, yA * v1, a1.Y), innerAttr.Lerp(attrA, t1),
                new Vector3(b1.X, yB * v1, b1.Y), innerAttr.Lerp(attrB, t1));
        }
    }

    /// <summary>
    /// One vertical wall quad along boundary segment A→B with per-column top
    /// AND bottom heights. Used for cliff faces (bottom = lower tile's rim)
    /// and map-edge skirts (bottom = world floor). Faces outward; winding CW
    /// viewed from outside: (topA, botA, botB), (topA, botB, topB).
    /// </summary>
    private static void AddWallQuad(SurfaceTool st, Vector2 centerXZ, bool splat, Color idx,
        Vector2 a, float topYA, Color topAttrA,
        Vector2 b, float topYB, Color topAttrB,
        float botYA, float botYB,
        Color botAttrA, Color botAttrB)
    {
        AddQuad(st, centerXZ, splat, idx,
            new Vector3(a.X, topYA, a.Y), topAttrA,
            new Vector3(b.X, topYB, b.Y), topAttrB,
            new Vector3(a.X, botYA, a.Y), botAttrA,
            new Vector3(b.X, botYB, b.Y), botAttrB);
    }

    /// <summary>
    /// One quad as two triangles: (A0, A1, B1) and (A0, B1, B0).
    /// Top-surface quads: A0/B0 = inner edge, A1/B1 = outer edge (CCW in the
    /// X/Z math plane = Godot front-face up). Walls: A0=topA, A1=botA,
    /// B1=botB, B0=topB (clockwise viewed from outside).
    /// </summary>
    private static void AddQuad(SurfaceTool st, Vector2 centerXZ, bool splat, Color custom,
        Vector3 a0, Color ca0,
        Vector3 b0, Color cb0,
        Vector3 a1, Color ca1,
        Vector3 b1, Color cb1)
    {
        AddVert(st, a0, ca0, centerXZ, splat, custom);
        AddVert(st, a1, ca1, centerXZ, splat, custom);
        AddVert(st, b1, cb1, centerXZ, splat, custom);

        AddVert(st, a0, ca0, centerXZ, splat, custom);
        AddVert(st, b1, cb1, centerXZ, splat, custom);
        AddVert(st, b0, cb0, centerXZ, splat, custom);
    }

    /// <summary>Adds one vertex. COLOR = albedo (vertex-colour mode) or splat weights (splat mode); CUSTOM0 = layer indices in splat mode. World-planar UV kept for compatibility (the splat shader samples by world position).</summary>
    private static void AddVert(SurfaceTool st, Vector3 local, Color color, Vector2 centerXZ, bool splat, Color custom)
    {
        st.SetColor(color);
        if (splat)
            st.SetCustom(0, custom);
        st.SetUV(new Vector2((centerXZ.X + local.X) * 0.25f, (centerXZ.Y + local.Z) * 0.25f));
        st.AddVertex(local);
    }
}
