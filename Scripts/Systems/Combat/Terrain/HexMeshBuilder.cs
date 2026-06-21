using Godot;
using System;

// ============================================================
// HexMeshBuilder.cs  (v8 — cliff foot welded via neighbour sampler)
//
// Purpose:        Generates the per-tile blended terrain mesh.
//
//                 v8 — CLIFF FOOT WELD:
//                 The cliff wall's bottom rim no longer reconstructs
//                 the lower neighbour's rim noise from THIS tile's
//                 side (the old CornerIndexOnNeighbour mapping was
//                 approximate and left a vertical gap, most visible
//                 on water edges). Instead the foot queries the
//                 neighbour's OWN surface sampler
//                 (SampleSurfaceWorldY) at the exact shared world
//                 points — the same math that places the neighbour's
//                 visible top — so the foot lands on it by
//                 construction. CornerIndexOnNeighbour is removed.
//
//                 v7 carried forward — PER-TERRAIN NOISE (amplitude
//                 + frequency): each terrain type has its own noise
//                 amp/freq, so water stays near-flat, stone is
//                 jagged, grass rolls, arcane swells smoothly.
//
//                 WATERTIGHT SEAM RULE: a surface point's
//                 displacement is noise(worldXZ, freq) * amp, and at
//                 any SHARED boundary point both/all touching tiles
//                 must produce the IDENTICAL value. They do, because:
//                   - INTERIOR points use the tile's OWN terrain
//                     (amp, freq).
//                   - MID points (shared by 2 tiles) blend the two
//                     terrains' params: (amp,freq) = mean of the two,
//                     then sample ONCE. Both tiles compute the same
//                     mean -> agree.
//                   - CORNER points (shared by up to 3 tiles) blend
//                     params over the corner's BLEND COMPONENT (the
//                     same membership the height blend uses, so a
//                     corner across a cliff doesn't pull in the wrong
//                     terrain). Symmetric over the component -> every
//                     tile computes the same mean -> agree.
//
//                 v6 carried forward — seamless no-falloff noise,
//                 subdivided fan + radially-subdivided bridge band,
//                 cliff tops/bottoms welded to noised rims.
//                 v4 carried forward — edge classification, corner
//                 blend-components (now water/land aware via
//                 BlendConnected), splat data.
//
// REQUIRES: st.GenerateTangents() after GenerateNormals() (in Build).
// Reads per-terrain exports on HexGridManager (see NoiseParams).
// ============================================================

public static class HexMeshBuilder
{
    private const float SkirtFloorDarkening = 0.5f;
    private const float CliffFaceDarkening = 0.7f;
    private const float FlatEpsilon = 0.01f;

    // One noise generator PER frequency in use this build, cached so we don't
    // rebuild the field every sample. Frequency varies per terrain now, so we
    // key the cache by frequency.
    private static readonly System.Collections.Generic.Dictionary<float, FastNoiseLite> _noiseByFreq = new();

    private static FastNoiseLite NoiseForFreq(float frequency)
    {
        // Quantise the key so tiny float drift doesn't explode the cache.
        float key = Mathf.Round(frequency * 1000f) / 1000f;
        if (!_noiseByFreq.TryGetValue(key, out var n))
        {
            n = new FastNoiseLite
            {
                Seed = 1337,
                Frequency = key,
                NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
                FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
                FractalOctaves = 3
            };
            _noiseByFreq[key] = n;
        }
        return n;
    }

    /// <summary>Raw positional noise in ~[-1,1] at a world XZ for a given frequency.</summary>
    public static float SurfaceNoise(float worldX, float worldZ, float frequency)
        => NoiseForFreq(frequency).GetNoise2D(worldX, worldZ);

    // ── Per-terrain parameters ────────────────────────────────────────────────

    /// <summary>(amplitude, frequency) for a terrain type, from HexGridManager exports.</summary>
    private static (float amp, float freq) NoiseParams(HexGridManager grid, TileTerrainType terrain)
    {
        return terrain switch
        {
            TileTerrainType.Grass => (grid.GrassNoiseAmp, grid.GrassNoiseFreq),
            TileTerrainType.Forest => (grid.ForestNoiseAmp, grid.ForestNoiseFreq),
            TileTerrainType.Stone => (grid.StoneNoiseAmp, grid.StoneNoiseFreq),
            TileTerrainType.Water => (grid.WaterNoiseAmp, grid.WaterNoiseFreq),
            TileTerrainType.Ice => (grid.IceNoiseAmp, grid.IceNoiseFreq),
            TileTerrainType.Lava => (grid.LavaNoiseAmp, grid.LavaNoiseFreq),
            TileTerrainType.Arcane => (grid.ArcaneNoiseAmp, grid.ArcaneNoiseFreq),
            _ => (grid.GrassNoiseAmp, grid.GrassNoiseFreq)
        };
    }

    private static float Displace(float amp, float freq, float worldX, float worldZ)
    {
        if (amp <= 0f)
            return 0f;
        return SurfaceNoise(worldX, worldZ, freq) * amp;
    }

    /// <summary>Interior displacement using the tile's OWN terrain params.</summary>
    private static float NoiseOwn(HexGridManager grid, TileData tile, float worldX, float worldZ)
    {
        var (amp, freq) = NoiseParams(grid, tile.TerrainType);
        return Displace(amp, freq, worldX, worldZ);
    }

    /// <summary>Mid-point displacement: mean params of the two edge terrains, sampled once.</summary>
    private static float NoiseBlend2(HexGridManager grid, TileTerrainType a, TileTerrainType b, float worldX, float worldZ)
    {
        var (aA, aF) = NoiseParams(grid, a);
        var (bA, bF) = NoiseParams(grid, b);
        return Displace((aA + bA) * 0.5f, (aF + bF) * 0.5f, worldX, worldZ);
    }

    /// <summary>
    /// Corner displacement: mean params over the corner's BLEND COMPONENT
    /// (same membership the height blend uses). Symmetric over the component, so
    /// every tile touching this corner computes the identical value -> watertight.
    /// </summary>
    private static float NoiseCorner(HexGridManager grid, TileData tile, int cornerIndex, float worldX, float worldZ)
    {
        var (m0, m1, m2, tA, tB) = CornerComponent(grid, tile, cornerIndex, 0);

        float sumAmp = 0f, sumFreq = 0f;
        int n = 0;
        if (m0)
        { var (a, f) = NoiseParams(grid, tile.TerrainType); sumAmp += a; sumFreq += f; n++; }
        if (m1)
        { var (a, f) = NoiseParams(grid, tA.TerrainType); sumAmp += a; sumFreq += f; n++; }
        if (m2)
        { var (a, f) = NoiseParams(grid, tB.TerrainType); sumAmp += a; sumFreq += f; n++; }

        if (n == 0)
        {
            var (a, f) = NoiseParams(grid, tile.TerrainType);
            return Displace(a, f, worldX, worldZ);
        }

        return Displace(sumAmp / n, sumFreq / n, worldX, worldZ);
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
        int subdiv = Math.Max(1, grid.TerrainNoiseSubdiv);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (splatMode)
            st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);

        float ownIdx = (int)tile.TerrainType;
        Color ownColor = TerrainColor(grid, tile.TerrainType);
        Color ownWeights = new Color(1f, 0f, 0f, 0f);
        Color ownIndices = new Color(ownIdx, ownIdx, ownIdx, ownIdx);

        var cornerXZ = new Vector2[6];
        var midXZ = new Vector2[6];
        for (int e = 0; e < 6; e++)
        {
            Vector2 cA = Corner(e, radius);
            Vector2 cB = Corner((e + 1) % 6, radius);
            cornerXZ[e] = centerXZ + cA;
            midXZ[e] = centerXZ + (cA + cB) * 0.5f;
        }

        // Corner heights: component-mean base + corner-blended positional noise.
        var cornerY = new float[6];
        var cornerColor = new Color[6];
        for (int i = 0; i < 6; i++)
        {
            float baseY = CornerWorldY(grid, tile, i) - ownTop;
            cornerY[i] = baseY + NoiseCorner(grid, tile, i, cornerXZ[i].X, cornerXZ[i].Y);
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
            // Water never blends into land: the water tile stays flat at its own
            // surface to the boundary; the land neighbour drops/terraces to it.
            bool waterEdge = nE != null &&
                ((tile.TerrainType == TileTerrainType.Water) != (nE.TerrainType == TileTerrainType.Water));

            isCliff[e] = nE != null &&
                (Math.Abs(tile.Height - nE.Height) > threshold || waterEdge);

            bool blends = nE != null && !isCliff[e];

            float midBase = blends
                ? ((tile.Height + nE.Height) * 0.5f) * HexTile.HeightStep - ownTop
                : 0f;
            // Mid noise blends THIS tile's terrain with the edge neighbour's.
            float midNoise = blends
                ? NoiseBlend2(grid, tile.TerrainType, nE.TerrainType, midXZ[e].X, midXZ[e].Y)
                : 0f;
            midY[e] = midBase + midNoise;

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

        // ── 1) Inner hexagon: subdivided, OWN-terrain seamless noise ──────
        st.SetSmoothGroup(0);
        Color innerAttr = splatMode ? ownWeights : ownColor;

        for (int e = 0; e < 6; e++)
        {
            Vector2 cA = Corner(e, radius) * solidFactor;
            Vector2 cB = Corner((e + 1) % 6, radius) * solidFactor;
            Vector2 m = (cA + cB) * 0.5f;

            AddNoisyFanTri(st, Vector3.Zero, cA, m, subdiv,
                centerXZ, splatMode, innerAttr, ownIndices, grid, tile);
            AddNoisyFanTri(st, Vector3.Zero, m, cB, subdiv,
                centerXZ, splatMode, innerAttr, ownIndices, grid, tile);
        }

        // ── 2) Bridge strips ──────────────────────────────────────────────
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
                cA, cornerY[e], attrA, mid, midY[e], attrM, inA, grid, tile, subdiv);
            AddBridgeHalf(st, centerXZ, splatMode, idx, solidFactor, terraceSteps,
                mid, midY[e], attrM, cB, cornerY[e2], attrB, inA, grid, tile, subdiv);
        }

        // ── 3) Cliff walls: top = own noised rim, bottom = lower tile's
        //       ACTUAL surface (queried from its own sampler) ──────────────
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

            // Weld the wall foot to the LOWER tile's ACTUAL surface at these world
            // points by querying its own surface sampler — the same math that
            // places the neighbour's visible top, so the foot lands on it exactly
            // (no reconstruction, no gap). Local Y = neighbour world Y − ownTop.
            float botYA = SampleSurfaceWorldY(grid, nbr[e], cornerXZ[e].X, cornerXZ[e].Y, solidFactor, terraceSteps) - ownTop;
            float botYM = SampleSurfaceWorldY(grid, nbr[e], midXZ[e].X, midXZ[e].Y, solidFactor, terraceSteps) - ownTop;
            float botYB = SampleSurfaceWorldY(grid, nbr[e], cornerXZ[e2].X, cornerXZ[e2].Y, solidFactor, terraceSteps) - ownTop;

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

        // ── 4) Map-edge skirts: top = noised rim, bottom = world floor ─────
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

            // Skirt flag: in splat mode the weights' alpha is free, so mark these
            // boundary faces with A = 1 (interior faces leave A = 0). The shader
            // reads it to paint skirts a flat boundary colour and skip cliff/grid.
            if (splatMode)
            {
                topA.A = 1f;
                topB.A = 1f;
                topM.A = 1f;
            }

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
        st.GenerateTangents();
        return st.Commit();
    }

    /// <summary>
    /// World-space surface Y at (worldX, worldZ) on <paramref name="tile"/>.
    /// Mirrors v7 per-terrain noise so props ride the surface. Inside the solid
    /// inner hex uses own-terrain noise; the bridge band interpolates own->blended.
    /// Also queried by the cliff-foot weld (section 3) to land a wall's bottom on
    /// the lower neighbour's actual surface.
    /// </summary>
    public static float SampleSurfaceWorldY(
        HexGridManager grid, TileData tile,
        float worldX, float worldZ, float solidFactor, int terraceSteps)
    {
        float ownTop = tile.Height * HexTile.HeightStep;
        if (tile.TileView == null)
            return ownTop;

        float radius = grid.HexRadius;
        Vector2 originXZ = new Vector2(tile.TileView.GlobalPosition.X, tile.TileView.GlobalPosition.Z);
        Vector2 p = new Vector2(worldX - originXZ.X, worldZ - originXZ.Y);

        float ownNoise = NoiseOwn(grid, tile, worldX, worldZ);

        if (p.LengthSquared() < 0.0001f)
            return ownTop + ownNoise;

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
            return ownTop + ownNoise;

        rho = Mathf.Min(rho, 1f);
        float s = b / rho;
        float t = (rho - solidFactor) / (1f - solidFactor);

        var nE = grid.GetTile(tile.Axial + HexDirection.All[(6 - e) % 6]);
        bool waterEdge = nE != null &&
            ((tile.TerrainType == TileTerrainType.Water) != (nE.TerrainType == TileTerrainType.Water));
        bool blends = nE != null
            && Math.Abs(tile.Height - nE.Height) <= grid.CliffHeightThreshold
            && !waterEdge;

        Vector2 cornerEworld = originXZ + cE;
        Vector2 cornerFworld = originXZ + cF;
        Vector2 midWorld = originXZ + (cE + cF) * 0.5f;

        float yE = (CornerWorldY(grid, tile, e) - ownTop) + NoiseCorner(grid, tile, e, cornerEworld.X, cornerEworld.Y);
        float yF = (CornerWorldY(grid, tile, e2) - ownTop) + NoiseCorner(grid, tile, e2, cornerFworld.X, cornerFworld.Y);
        float midBase = blends
            ? ((tile.Height + nE.Height) * 0.5f) * HexTile.HeightStep - ownTop
            : 0f;
        float yM = midBase + (blends
            ? NoiseBlend2(grid, tile.TerrainType, nE.TerrainType, midWorld.X, midWorld.Y)
            : 0f);

        float edgeY = s < 0.5f
            ? Mathf.Lerp(yE, yM, s * 2f)
            : Mathf.Lerp(yM, yF, (s - 0.5f) * 2f);

        // Match the radially-noised band: own noise at the query point, plus the
        // rim deviation ramped across the band (noise at the rim point removed so
        // t = 0 meets the fan and t = 1 meets the rim).
        Vector2 rimXZ = originXZ + cE.Lerp(cF, s);
        float rimNoise = NoiseOwn(grid, tile, rimXZ.X, rimXZ.Y);
        float smoothBandY = ownTop + ownNoise + (edgeY - rimNoise) * t;

        if (terraceSteps <= 0 || Mathf.Abs(edgeY) < FlatEpsilon)
            return smoothBandY;

        int hStepSpan = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(edgeY) / HexTile.HeightStep));

        // Match AddBridgeHalf: gentle 1-step edges are smooth ramps, not terraces.
        const int TerraceMinSpan = 2;
        if (hStepSpan < TerraceMinSpan)
            return smoothBandY;

        int terraces = hStepSpan * terraceSteps;
        int steps = terraces * 2 + 1;
        int k = Mathf.Clamp((int)(t * steps), 0, steps - 1);
        float frac = t * steps - k;
        float v0 = ((k + 1) / 2) / (float)(terraces + 1);
        float v1 = ((k + 2) / 2) / (float)(terraces + 1);
        return ownTop + edgeY * Mathf.Lerp(v0, v1, frac);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Subdivided fan triangle — OWN terrain noise (no falloff)
    // ────────────────────────────────────────────────────────────────────────

    private static void AddNoisyFanTri(
        SurfaceTool st, Vector3 centre, Vector2 bA, Vector2 bB, int n,
        Vector2 centerXZ, bool splat, Color attr, Color idx,
        HexGridManager grid, TileData tile)
    {
        Vector2 c2 = new Vector2(centre.X, centre.Z);

        Vector3 V(int i, int j)
        {
            float fi = i / (float)n;
            float fj = j / (float)n;
            float wc = 1f - fi - fj;
            Vector2 xz = c2 * wc + bA * fi + bB * fj;
            float y = NoiseOwn(grid, tile, centerXZ.X + xz.X, centerXZ.Y + xz.Y);
            return new Vector3(xz.X, y, xz.Y);
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n - i; j++)
            {
                AddVert(st, V(i, j), attr, centerXZ, splat, idx);
                AddVert(st, V(i + 1, j), attr, centerXZ, splat, idx);
                AddVert(st, V(i, j + 1), attr, centerXZ, splat, idx);

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
    // Corner components (water/land aware via BlendConnected)
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

    /// <summary>True when two tiles share a smooth blended surface rather than meet at a cliff: both present, within the height threshold, and not a water/land straddle.</summary>
    private static bool BlendConnected(TileData x, TileData y, int threshold)
    {
        if (x == null || y == null)
            return false;
        if ((x.TerrainType == TileTerrainType.Water) != (y.TerrainType == TileTerrainType.Water))
            return false;
        return Math.Abs(x.Height - y.Height) <= threshold;
    }

    private static (bool m0, bool m1, bool m2, TileData tA, TileData tB)
        CornerComponent(HexGridManager grid, TileData tile, int cornerIndex, int startSlot)
    {
        int threshold = grid.CliffHeightThreshold;

        var tA = grid.GetTile(tile.Axial + HexDirection.All[(7 - cornerIndex) % 6]);
        var tB = grid.GetTile(tile.Axial + HexDirection.All[(6 - cornerIndex) % 6]);

        bool p1 = tA != null, p2 = tB != null;

        // Height-AND-terrain aware: a water/land pair is a barrier, never a blend,
        // so it must not join a corner component — same predicate the per-edge
        // waterEdge rule uses, so edges and corners agree.
        bool a01 = BlendConnected(tile, tA, threshold);
        bool a02 = BlendConnected(tile, tB, threshold);
        bool a12 = BlendConnected(tA, tB, threshold);

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

    private static Vector2 Corner(int i, float radius)
    {
        float a = Mathf.DegToRad(60f * i);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
    }

    /// <summary>
    /// One half of a bridge strip, subdivided along its length into `lengthDiv`
    /// columns. Inner-ring points carry the tile's OWN-terrain noise (matching the
    /// fan's outer ring); outer-rim points use the passed (already blended-noised)
    /// boundary heights yA/yB. The smooth path also subdivides across the band's
    /// WIDTH so it follows the noise field instead of a single planar span.
    /// </summary>
    private static void AddBridgeHalf(SurfaceTool st, Vector2 centerXZ, bool splat, Color idx,
        float solidFactor, int terraceSteps,
        Vector2 outerA, float yA, Color attrA,
        Vector2 outerB, float yB, Color attrB,
        Color innerAttr, HexGridManager grid, TileData tile, int lengthDiv)
    {
        int n = Math.Max(1, lengthDiv);

        Vector2 innerA = outerA * solidFactor;
        Vector2 innerB = outerB * solidFactor;

        // Per-column rings. Inner ring carries the tile's OWN-terrain noise so it
        // welds to the subdivided fan's outer edge; outer rim carries the already
        // blended-noised boundary heights yA/yB (shared with the neighbour).
        var innerPos = new Vector3[n + 1];
        var outerPos = new Vector3[n + 1];
        var outerAttrCol = new Color[n + 1];
        for (int c = 0; c <= n; c++)
        {
            float u = c / (float)n;
            Vector2 inXZ = innerA.Lerp(innerB, u);
            Vector2 outXZ = outerA.Lerp(outerB, u);
            float inY = NoiseOwn(grid, tile, centerXZ.X + inXZ.X, centerXZ.Y + inXZ.Y);
            float outY = Mathf.Lerp(yA, yB, u);
            innerPos[c] = new Vector3(inXZ.X, inY, inXZ.Y);
            outerPos[c] = new Vector3(outXZ.X, outY, outXZ.Y);
            outerAttrCol[c] = attrA.Lerp(attrB, u);
        }

        // Terrace classification is constant across the half (depends on yA/yB).
        bool flat = Mathf.Abs(yA) < FlatEpsilon && Mathf.Abs(yB) < FlatEpsilon;
        int hStepSpan = flat ? 0 : Mathf.Max(1, Mathf.RoundToInt(
            Mathf.Max(Mathf.Abs(yA), Mathf.Abs(yB)) / HexTile.HeightStep));
        const int TerraceMinSpan = 2;
        bool terrace = terraceSteps > 0 && hStepSpan >= TerraceMinSpan;

        if (!terrace)
        {
            // Subdivide across the band's WIDTH too, so it follows the noise field
            // like the fan instead of a single planar span (the solid-factor ring).
            // Inner row welds to the fan (pure own noise); outer row hits the shared
            // rim exactly (the noise term cancels at tr = 1).
            int radial = Math.Max(2, Mathf.RoundToInt(n * (1f - solidFactor) / Mathf.Max(0.01f, solidFactor)));

            Vector3 BandVert(int c, int r)
            {
                float u = c / (float)n;
                float tr = r / (float)radial;
                Vector2 inXZ = innerA.Lerp(innerB, u);
                Vector2 outXZ = outerA.Lerp(outerB, u);
                Vector2 xz = inXZ.Lerp(outXZ, tr);
                float rimY = Mathf.Lerp(yA, yB, u);
                float dev = rimY - NoiseOwn(grid, tile, centerXZ.X + outXZ.X, centerXZ.Y + outXZ.Y);
                float y = NoiseOwn(grid, tile, centerXZ.X + xz.X, centerXZ.Y + xz.Y) + dev * tr;
                return new Vector3(xz.X, y, xz.Y);
            }

            Color BandAttr(int c, int r)
            {
                float u = c / (float)n;
                float tr = r / (float)radial;
                return innerAttr.Lerp(attrA.Lerp(attrB, u), tr);
            }

            for (int c = 0; c < n; c++)
            {
                for (int r = 0; r < radial; r++)
                {
                    AddQuad(st, centerXZ, splat, idx,
                        BandVert(c, r), BandAttr(c, r),
                        BandVert(c + 1, r), BandAttr(c + 1, r),
                        BandVert(c, r + 1), BandAttr(c, r + 1),
                        BandVert(c + 1, r + 1), BandAttr(c + 1, r + 1));
                }
            }
            return;
        }

        // Terraced: per column, march inner->outer in tread/riser steps.
        // t = run (XZ inner->outer), v = stepped rise (Y inner->outer). t=0 welds to
        // the fan, t=1 welds to the rim, so the band stays watertight.
        int terraces = hStepSpan * terraceSteps;
        int steps = terraces * 2 + 1;

        for (int c = 0; c < n; c++)
        {
            Vector3 iA = innerPos[c], oA = outerPos[c];
            Vector3 iB = innerPos[c + 1], oB = outerPos[c + 1];
            Color oaA = outerAttrCol[c], oaB = outerAttrCol[c + 1];

            for (int k = 0; k < steps; k++)
            {
                float t0 = (float)k / steps;
                float t1 = (float)(k + 1) / steps;
                float v0 = ((k + 1) / 2) / (float)(terraces + 1);
                float v1 = ((k + 2) / 2) / (float)(terraces + 1);

                Vector3 lowA = TerracePoint(iA, oA, t0, v0);
                Vector3 highA = TerracePoint(iA, oA, t1, v1);
                Vector3 lowB = TerracePoint(iB, oB, t0, v0);
                Vector3 highB = TerracePoint(iB, oB, t1, v1);

                AddQuad(st, centerXZ, splat, idx,
                    lowA, innerAttr.Lerp(oaA, t0),
                    lowB, innerAttr.Lerp(oaB, t0),
                    highA, innerAttr.Lerp(oaA, t1),
                    highB, innerAttr.Lerp(oaB, t1));
            }
        }
    }

    /// <summary>Terrace sample: XZ lerps inner->outer by run t; Y lerps inner->outer by stepped rise v.</summary>
    private static Vector3 TerracePoint(Vector3 inner, Vector3 outer, float t, float v)
        => new Vector3(
            Mathf.Lerp(inner.X, outer.X, t),
            Mathf.Lerp(inner.Y, outer.Y, v),
            Mathf.Lerp(inner.Z, outer.Z, t));

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
