using Godot;
using System;

// ============================================================
// HexMeshBuilder.cs  (v5 — subdivided noise tops)
//
// Purpose:        Generates the per-tile blended terrain mesh.
//                 Each tile owns its full hexagon footprint: a
//                 SUBDIVIDED inner hex at the tile's own height
//                 (now displaced by world-space surface noise for
//                 rolling micro-relief), and six bridge strips out
//                 to the shared boundaries, with every boundary
//                 edge split at its midpoint.
//
//                 v5 — INNER-HEX SUBDIVISION + NOISE:
//                 - Each of the six fan triangles (centre, boundary
//                   point A, boundary point B) is subdivided into a
//                   uniform barycentric triangle grid of resolution
//                   grid.TerrainNoiseSubdiv.
//                 - Interior vertices are displaced in Y by
//                   SurfaceNoise(worldX, worldZ) * amplitude, scaled
//                   by a falloff that is 1 at the centre and 0 along
//                   the boundary edge. Boundary points (corners,
//                   mids, and the straight edge between them) stay
//                   EXACTLY flat at their analytic height, so seams
//                   with bridges/neighbours remain watertight.
//                 - Noise is a pure function of world XZ, so it is
//                   continuous across tiles wherever it is nonzero.
//                 - SampleSurfaceWorldY applies the SAME noise with
//                   the SAME falloff, so props sit on the bumps.
//
//                 v4 carried forward — EDGE CLASSIFICATION (cliffs),
//                 corner blend-components, splat data. Sections 2-4
//                 (bridges, cliff walls, map-edge skirts) are
//                 unchanged from v4.
//
// Layer:          System (generation helper)
// Collaborators:  HexGridManager (grid, CliffHeightThreshold,
//                 TerrainNoise* knobs, terrain materials/textures),
//                 HexTile (HeightStep, SetGeneratedMesh),
//                 HexDirection, UITheme, TerrainTextureLibrary /
//                 terrain_splat.gdshader, HexGridManager.Props
//
// Geometry conventions (verified against MovementZoneRenderer):
//   - Flat-top hexes. Corner i at angle 60°·i from +X, CCW.
//   - Edge e spans corners e and e+1, faces neighbour direction
//     d = (6 - e) % 6 (the EdgeForDir reflection).
//   - Corner i is shared with neighbour directions (7 - i) % 6
//     and (6 - i) % 6.
//   - Godot front faces wind clockwise; up-facing surfaces are CCW
//     in the (X, Z) math plane. Walls/skirts face outward.
//   - SMOOTH shading on the top surface (inner fan + bridges),
//     FLAT shading on walls/skirts — see Build().
//   - st.GenerateTangents() is REQUIRED after GenerateNormals()
//     so the splat shader's NORMAL_MAP path works.
// ============================================================

public static class HexMeshBuilder
{
    /// <summary>Vertex-colour multiplier at the bottom of map-edge skirts (vertex-colour mode only).</summary>
    private const float SkirtFloorDarkening = 0.5f;

    /// <summary>Vertex-colour multiplier at the bottom of cliff walls (vertex-colour mode only; splat mode darkens by normal in-shader).</summary>
    private const float CliffFaceDarkening = 0.7f;

    /// <summary>Height delta below which a bridge half is built as a single quad even when terracing is on.</summary>
    private const float FlatEpsilon = 0.01f;

    // Shared noise generator for top-surface displacement. Static so the mesh
    // builder and the prop sampler evaluate IDENTICAL values for the same world
    // XZ — if these ever diverge, props float above / sink below the surface.
    private static FastNoiseLite _surfaceNoise;
    private static float _surfaceNoiseFreq = -1f;

    /// <summary>
    /// World-space surface displacement in [-1, 1] at (worldX, worldZ). Pure
    /// function of position, so continuous across tiles. Caller scales by
    /// amplitude and a per-vertex falloff. Lazily (re)built when frequency
    /// changes.
    /// </summary>
    public static float SurfaceNoise(float worldX, float worldZ, float frequency)
    {
        if (_surfaceNoise == null || Math.Abs(_surfaceNoiseFreq - frequency) > 0.0001f)
        {
            _surfaceNoise = new FastNoiseLite
            {
                // Fixed seed: the displacement field is the same every run, so a
                // given map seed still reproduces exactly (heights are deterministic
                // and the noise is positional, not random per-build).
                Seed = 1337,
                Frequency = frequency,
                NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
                FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
                FractalOctaves = 3
            };
            _surfaceNoiseFreq = frequency;
        }

        return _surfaceNoise.GetNoise2D(worldX, worldZ);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────────

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

        float noiseAmp = grid.TerrainNoiseAmplitude;
        float noiseFreq = grid.TerrainNoiseFrequency;
        int subdiv = Math.Max(1, grid.TerrainNoiseSubdiv);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (splatMode)
            st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);

        // ── Shared blend inputs ───────────────────────────────────────────
        float ownIdx = (int)tile.TerrainType;
        Color ownColor = TerrainColor(grid, tile.TerrainType);
        Color ownWeights = new Color(1f, 0f, 0f, 0f);
        Color ownIndices = new Color(ownIdx, ownIdx, ownIdx, ownIdx);

        var cornerY = new float[6];
        var cornerColor = new Color[6];
        for (int i = 0; i < 6; i++)
        {
            cornerY[i] = CornerWorldY(grid, tile, i) - ownTop;
            if (!splatMode)
                cornerColor[i] = CornerColor(grid, tile, i);
        }

        var nbr = new TileData[6];
        var isCliff = new bool[6];
        var midY = new float[6];
        var midColor = new Color[6];
        var edgeIndices = new Color[6];
        var edgeWeightsA = new Color[6];
        var edgeWeightsB = new Color[6];
        var edgeWeightsM = new Color[6];

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

        // ── 1) Inner hexagon: SUBDIVIDED + NOISE-DISPLACED ────────────────
        // Smooth shading so the noisy surface reads as rolling terrain.
        st.SetSmoothGroup(0);
        Color innerAttr = splatMode ? ownWeights : ownColor;

        // The inner hex boundary is the 12-gon of (corner, mid, corner, ...) at
        // radius*solidFactor. Build six fan triangles (centre, A, B) where
        // A/B walk corner→mid and mid→corner, and subdivide each barycentrically.
        for (int e = 0; e < 6; e++)
        {
            Vector2 cA = Corner(e, radius) * solidFactor;
            Vector2 cB = Corner((e + 1) % 6, radius) * solidFactor;
            Vector2 m = (cA + cB) * 0.5f;

            // Two fan triangles per edge: (centre, cA, m) and (centre, m, cB).
            // Boundary points cA, m, cB carry their analytic height (flat, 0 here
            // for the inner ring which sits at local Y=0). Centre carries noise.
            AddNoisyFanTri(st, Vector3.Zero, cA, m, subdiv,
                centerXZ, splatMode, innerAttr, ownIndices,
                noiseAmp, noiseFreq, solidFactor, radius);
            AddNoisyFanTri(st, Vector3.Zero, m, cB, subdiv,
                centerXZ, splatMode, innerAttr, ownIndices,
                noiseAmp, noiseFreq, solidFactor, radius);
        }

        // ── 2) Bridge strips: two halves per edge (UNCHANGED) ─────────────
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

        // ── 3) Cliff walls (UNCHANGED, flat-shaded) ───────────────────────
        st.SetSmoothGroup(uint.MaxValue);
        for (int e = 0; e < 6; e++)
        {
            if (!isCliff[e] || tile.Height <= nbr[e].Height)
                continue;

            int e2 = (e + 1) % 6;
            Vector2 cA = Corner(e, radius);
            Vector2 cB = Corner(e2, radius);
            Vector2 mid = (cA + cB) * 0.5f;

            float topYA = cornerY[e];
            float topYM = midY[e];
            float topYB = cornerY[e2];

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

        // ── 4) Map-edge skirts (UNCHANGED, flat-shaded) ───────────────────
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
        st.GenerateTangents();   // required for the splat shader's NORMAL_MAP path
        return st.Commit();
    }

    /// <summary>
    /// World-space surface Y of the terrain at (worldX, worldZ), assuming the
    /// point lies on <paramref name="tile"/>. Mirrors v4 edge geometry AND the
    /// v5 inner-surface noise (same noise + same boundary falloff), so props
    /// sit on the displaced surface. Reads grid.CliffHeightThreshold and the
    /// grid.TerrainNoise* knobs.
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
            return ownTop + InteriorNoiseOffset(grid, worldX, worldZ, 0f, solidFactor);

        float angDeg = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(p.Y, p.X)), 360f);
        int e = Mathf.Clamp((int)(angDeg / 60f), 0, 5);
        int e2 = (e + 1) % 6;

        Vector2 cE = Corner(e, radius);
        Vector2 cF = Corner(e2, radius);

        float det = cE.X * cF.Y - cF.X * cE.Y;
        float a = (p.X * cF.Y - cF.X * p.Y) / det;
        float b = (cE.X * p.Y - p.X * cE.Y) / det;

        float rho = a + b;

        // Inside the solid inner hex: flat analytic height + interior noise,
        // with the SAME radial falloff the mesh uses (1 at centre → 0 at rim).
        if (rho <= solidFactor)
        {
            float rNorm = rho / Mathf.Max(0.0001f, solidFactor); // 0 centre .. 1 rim
            return ownTop + InteriorNoiseOffset(grid, worldX, worldZ, rNorm, solidFactor);
        }

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

    /// <summary>Interior noise displacement (world units) at normalized radius rNorm (0 centre .. 1 boundary). Falloff = (1 - rNorm) so it is exactly 0 at the rim, matching the flat boundary ring.</summary>
    private static float InteriorNoiseOffset(HexGridManager grid, float worldX, float worldZ, float rNorm, float solidFactor)
    {
        float amp = grid.TerrainNoiseAmplitude;
        if (amp <= 0f)
            return 0f;

        float falloff = Mathf.Clamp(1f - rNorm, 0f, 1f);
        // Smoothstep the falloff so the transition to the flat rim is gentle.
        falloff = falloff * falloff * (3f - 2f * falloff);
        return SurfaceNoise(worldX, worldZ, grid.TerrainNoiseFrequency) * amp * falloff;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Subdivided fan triangle with noise
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Subdivides triangle (centre0, boundaryA, boundaryB) into a barycentric
    /// grid of resolution `n` and emits it. The two boundary vertices sit on
    /// the inner-hex rim (local Y = 0, no noise — keeps the seam with bridges
    /// flat). The centre and all interior vertices are displaced by interior
    /// noise with a falloff keyed to barycentric distance from the boundary
    /// edge, so displacement is full at the centre and 0 along the rim edge.
    /// </summary>
    private static void AddNoisyFanTri(
        SurfaceTool st, Vector3 centre, Vector2 bA, Vector2 bB, int n,
        Vector2 centerXZ, bool splat, Color attr, Color idx,
        float amp, float freq, float solidFactor, float radius)
    {
        Vector2 c2 = new Vector2(centre.X, centre.Z);

        Vector3 V(int i, int j)
        {
            float fi = i / (float)n;
            float fj = j / (float)n;
            float wc = 1f - fi - fj;
            Vector2 xz = c2 * wc + bA * fi + bB * fj;

            float y = 0f;
            if (amp > 0f && wc > 0.0001f)
            {
                float fall = wc * wc * (3f - 2f * wc);
                y = SurfaceNoise(centerXZ.X + xz.X, centerXZ.Y + xz.Y, freq) * amp * fall;
            }
            return new Vector3(xz.X, y, xz.Y);
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n - i; j++)
            {
                // Upright triangle — winding flipped to match the fan (CCW in XZ).
                AddVert(st, V(i, j), attr, centerXZ, splat, idx);
                AddVert(st, V(i + 1, j), attr, centerXZ, splat, idx);
                AddVert(st, V(i, j + 1), attr, centerXZ, splat, idx);

                // Inverted triangle — same flip.
                if (i + j < n - 1)
                {
                    AddVert(st, V(i + 1, j), attr, centerXZ, splat, idx);
                    AddVert(st, V(i + 1, j + 1), attr, centerXZ, splat, idx);
                    AddVert(st, V(i, j + 1), attr, centerXZ, splat, idx);
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Corner components (UNCHANGED from v4)
    // ────────────────────────────────────────────────────────────────────────

    public static float CornerWorldY(HexGridManager grid, TileData tile, int cornerIndex)
        => CornerComponentMeanWorldY(grid, tile, cornerIndex, 0);

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
            return tile.Height * HexTile.HeightStep;

        return (sum / n) * HexTile.HeightStep;
    }

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

    private static (Color indices, Color weightsCornerA, Color weightsCornerB)
        SplatForEdge(HexGridManager grid, TileData tile, int e)
    {
        float ownIdx = (int)tile.TerrainType;

        var nE = grid.GetTile(tile.Axial + HexDirection.All[(6 - e) % 6]);
        var nA = grid.GetTile(tile.Axial + HexDirection.All[(7 - e) % 6]);
        var nB = grid.GetTile(tile.Axial + HexDirection.All[(5 - e) % 6]);

        var indices = new Color(
            ownIdx,
            nE != null ? (int)nE.TerrainType : ownIdx,
            nA != null ? (int)nA.TerrainType : ownIdx,
            nB != null ? (int)nB.TerrainType : ownIdx);

        var (c0, cThird, cNe, _, _) = CornerComponent(grid, tile, e, 0);
        int cntA = (c0 ? 1 : 0) + (cThird ? 1 : 0) + (cNe ? 1 : 0);
        float wA = 1f / Math.Max(1, cntA);
        var weightsCornerA = new Color(c0 ? wA : 0f, cNe ? wA : 0f, cThird ? wA : 0f, 0f);

        var (d0, dNe, dThird, _, _) = CornerComponent(grid, tile, (e + 1) % 6, 0);
        int cntB = (d0 ? 1 : 0) + (dNe ? 1 : 0) + (dThird ? 1 : 0);
        float wB = 1f / Math.Max(1, cntB);
        var weightsCornerB = new Color(d0 ? wB : 0f, dNe ? wB : 0f, 0f, dThird ? wB : 0f);

        return (indices, weightsCornerA, weightsCornerB);
    }

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
    // Internals (UNCHANGED from v4)
    // ────────────────────────────────────────────────────────────────────────

    private static Vector2 Corner(int i, float radius)
    {
        float a = Mathf.DegToRad(60f * i);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
    }

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

    private static void AddVert(SurfaceTool st, Vector3 local, Color color, Vector2 centerXZ, bool splat, Color custom)
    {
        st.SetColor(color);
        if (splat)
            st.SetCustom(0, custom);
        st.SetUV(new Vector2((centerXZ.X + local.X) * 0.25f, (centerXZ.Y + local.Z) * 0.25f));
        st.AddVertex(local);
    }
}
