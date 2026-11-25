using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.FarSea.cs  (partial of HexGridManager)
//
// Purpose:        Two repairs to the landscape beyond the out-of-bounds tiles
//                 (the far field, HexGridManager.FarField.cs):
//                 1. Terrain out there is chosen from a SMOOTH field that
//                    widens with distance, so it can no longer alias into
//                    streaks radiating from the board.
//                 2. Water that reaches the edge of the tiles carries on to
//                    the horizon as one sea surface at the same level.
// Layer:          System (combat terrain, visuals only, no rules)
// Collaborators:  HexGridManager.FarField.cs (calls FarFieldTerrainSmooth while
//                 it builds), HexGridManager.WaterPlane.cs (calls BuildFarSea
//                 at the end of SpawnWaterPlane, so a mid-fight flood rebuilds
//                 the sea too), painterly_water.gdshader (same vertex layout
//                 as the tile water)
// See:            docs/far_field_sea_v1.md
//
// Why the streaks happened: the far field is a fan of rays from the board.
// Along a ray the samples get far apart quickly (each loop is wider than the
// last), but the terrain was looked up per hex, a pattern only a few metres
// across. Sampled that coarsely it came out as noise, and because neighbouring
// rays are close together, the noise smeared along the rays.
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Far Field")]

    /// <summary>Carry water that reaches the edge of the tiles on to the horizon. Off = the old behaviour (water stops at the last tile).</summary>
    [Export] public bool EnableFarSea = true;

    /// <summary>Extra height of distant high ground (where the far terrain is at the top of its range), so stone country shows as hills on the horizon. 0 = off.</summary>
    [Export(PropertyHint.Range, "0,30,0.5")] public float FarFieldPeakHeight = 6f;

    private const float FarSeaFirstStep = 1.0f;
    private const float FarSeaStepGrowth = 1.25f;
    private const float FarSeaDeepAfter = 10f;    // metres out from the tiles where the sea reads fully deep

    /// <summary>
    /// Terrain for a far-field point from a smooth field. Near the tiles it is the map's
    /// own elevation and moisture, interpolated between hexes. With distance (farT 0 to
    /// 1) it hands over to two broad noises, so the pattern is always wider than the
    /// gap between samples. Also returns the elevation it used, 0..1.
    /// </summary>
    private TileTerrainType FarFieldTerrainSmooth(MapField field, Vector2 worldXZ, List<PaletteRule> palette,
        FastNoiseLite broadElevation, FastNoiseLite broadMoisture,
        Dictionary<Vector2I, float> elevationCache, Dictionary<Vector2I, float> moistureCache,
        float farT, out float elevation)
    {
        float nearE = FieldSmooth01(field, worldXZ, elevationCache, moisture: false);
        float nearM = FieldSmooth01(field, worldXZ, moistureCache, moisture: true);
        float farE = Mathf.Clamp(0.5f + broadElevation.GetNoise2D(worldXZ.X, worldXZ.Y) * 0.9f, 0f, 1f);
        float farM = Mathf.Clamp(0.5f + broadMoisture.GetNoise2D(worldXZ.X, worldXZ.Y) * 0.9f, 0f, 1f);

        elevation = Mathf.Lerp(nearE, farE, farT);
        float wetness = Mathf.Lerp(nearM, farM, farT);

        // A side of the board that borders a named terrain in the world keeps it.
        if (VistaTerrainBias.Count > 0
            && VistaTerrainBias.TryGetValue(DominantDirection(NearestAxial(worldXZ)), out TileTerrainType biased))
        {
            return biased;
        }

        return palette != null
            ? field.ClassifyByPalette(palette, elevation, wetness)
            : field.ClassifyTerrain(Theme, elevation, wetness);
    }

    /// <summary>
    /// The map field's elevation or moisture (0..1) at any world point: sampled at the
    /// three hexes around the point and blended, so it is continuous instead of
    /// stepping at hex edges. Same lattice maths as FieldContinuationY.
    /// </summary>
    private float FieldSmooth01(MapField field, Vector2 worldXZ, Dictionary<Vector2I, float> cache, bool moisture)
    {
        float q = worldXZ.X / (1.5f * HexRadius);
        float r = worldXZ.Y / (Mathf.Sqrt(3f) * HexRadius) - q * 0.5f;
        int q0 = Mathf.FloorToInt(q);
        int r0 = Mathf.FloorToInt(r);
        float fq = q - q0;
        float fr = r - r0;

        if (fq + fr < 1f)
            return V(q0, r0) * (1f - fq - fr) + V(q0 + 1, r0) * fq + V(q0, r0 + 1) * fr;
        return V(q0 + 1, r0 + 1) * (fq + fr - 1f) + V(q0, r0 + 1) * (1f - fq) + V(q0 + 1, r0) * (1f - fr);

        float V(int aq, int ar)
        {
            var key = new Vector2I(aq, ar);
            if (!cache.TryGetValue(key, out float v))
            {
                v = moisture ? field.SampleMoisture01(key) : field.SampleElevation01(key);
                cache[key] = v;
            }
            return v;
        }
    }

    /// <summary>
    /// The sea beyond the tiles. Built only when a body of water reaches the outer
    /// edge of the out-of-bounds tiles. One flat sheet at that body's level, from the
    /// tile edge out to the far-field rim, drawn with the tile water's own material.
    /// It needs no shoreline of its own: wherever the far ground rises above the sheet
    /// it hides it, and wherever the ground dips below, there is water.
    /// </summary>
    private void BuildFarSea(Dictionary<Vector2I, float> surfaceYByTile, Material waterMaterial)
    {
        if (!EnableFarField || !EnableFarSea || InteriorActive || waterMaterial == null || surfaceYByTile == null)
            return;

        List<OutlineSample> outline = BuildCoverageOutline(out float inscribed);
        if (outline == null || outline.Count < 12)
            return;

        // The level of the water that touches the edge. Bodies can sit at different
        // levels. The lowest wins, so the sheet never floats above a shore.
        float seaY = float.MaxValue;
        int wetEdge = 0;
        foreach (OutlineSample o in outline)
        {
            if (o.Terrain != TileTerrainType.Water)
                continue;
            if (surfaceYByTile.TryGetValue(NearestAxial(o.TuckXZ), out float level))
            {
                seaY = Mathf.Min(seaY, level);
                wetEdge++;
            }
        }
        if (wetEdge == 0)
            return;

        float radius = Mathf.Max(FarFieldRadius, inscribed + 40f);
        float maxDepth = Mathf.Max(_waterProfile?.MaxDepthWorld ?? WaterMaxDepthWorld, 0.1f);

        var offsets = new List<float> { 0f };
        float step = FarSeaFirstStep;
        float reach = 0f;
        while (inscribed + reach < radius && offsets.Count < 120)
        {
            reach += step;
            offsets.Add(reach);
            step *= FarSeaStepGrowth;
        }

        int n = outline.Count;
        int loops = offsets.Count;
        var pos = new Vector3[n * loops];
        var shore = new float[n * loops];
        var depth = new float[n * loops];

        for (int k = 0; k < loops; k++)
        {
            for (int i = 0; i < n; i++)
            {
                OutlineSample o = outline[i];
                int vi = k * n + i;

                Vector2 u = o.XZ - _farFieldCenter;
                float baseR = u.Length();
                u = baseR > 0.0001f ? u / baseR : Vector2.Right;
                float r = k == 0 ? baseR : (k == loops - 1 ? radius : Mathf.Min(baseR + offsets[k], radius));
                float outBy = Mathf.Max(r - baseR, 0f);
                Vector2 p = _farFieldCenter + u * r;

                bool wetStart = o.Terrain == TileTerrainType.Water;
                float ramp = SmoothStep01(outBy / FarSeaDeepAfter);

                // Beside a land tile the sheet starts under the ground and rises to
                // the sea level a few metres out, so no water shows along a low bank.
                float y = wetStart
                    ? seaY
                    : Mathf.Lerp(Mathf.Min(seaY, o.TuckY - 0.15f), seaY, SmoothStep01(outBy / 3f));

                // Match the tile water at the shared edge (its depth there), then
                // deepen to open sea.
                float startDepth = wetStart ? Mathf.Clamp((seaY - o.Y) / maxDepth, 0f, 1f) : 0f;

                pos[vi] = new Vector3(p.X, y, p.Y);
                depth[vi] = Mathf.Lerp(startDepth, 1f, ramp);
                shore[vi] = Mathf.Lerp(wetStart ? 1f : 0f, 1f, ramp);
            }
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int vi = 0; vi < pos.Length; vi++)
        {
            // Same layout as the tile water: COLOR r = shore distance, g = depth,
            // a = spill (0 = over water). UV2 = the level and depth it rises from.
            st.SetColor(new Color(shore[vi], depth[vi], 0f, 0f));
            st.SetUV2(new Vector2(pos[vi].Y, depth[vi]));
            st.SetNormal(Vector3.Up);
            st.SetUV(new Vector2(pos[vi].X, pos[vi].Z) * 0.1f);
            st.AddVertex(pos[vi]);
        }

        for (int k = 0; k < loops - 1; k++)
        {
            for (int i = 0; i < n; i++)
            {
                int i2 = (i + 1) % n;
                int a = k * n + i;
                int b = (k + 1) * n + i;
                int c = k * n + i2;
                int d = (k + 1) * n + i2;
                AddUpTriangle(st, pos, a, b, c);
                AddUpTriangle(st, pos, b, d, c);
            }
        }

        st.GenerateTangents();

        var sea = new MeshInstance3D
        {
            Name = "FarSea",
            Mesh = st.Commit(),
            MaterialOverride = waterMaterial,
            ExtraCullMargin = 0.5f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        // Same group as the tile water, so ClearWaterPlane removes both together.
        sea.AddToGroup(WaterPlaneGroup);

        Node parent = PropParent ?? this;
        parent.AddChild(sea);
        sea.GlobalTransform = Transform3D.Identity;   // vertices are world-space

        // The water used to dissolve into the horizon colour just past the tiles,
        // because nothing lay beyond them. Now it runs on, so the dissolve moves out.
        if (waterMaterial is ShaderMaterial waterSm)
            waterSm.SetShaderParameter("horizon_melt_end", Mathf.Max(FarFieldMeltEnd * 0.6f, 60f));

        GD.Print($"[FarSea] Sea at {seaY:0.00} from {wetEdge} wet edge sample(s), {n} x {loops} loops out to {radius:0}.");
    }
}
