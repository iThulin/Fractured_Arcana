using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.WaterPlane.cs  (partial of HexGridManager)
//
// Purpose:        Builds the single painterly water surface — one welded
//                 MeshInstance3D covering every Water-classified tile —
//                 and bakes the per-vertex data the shader needs:
//                   COLOR.r = shore distance (0 at land edge .. 1 open water)
//                   COLOR.g = depth (0 shallow .. 1 at WaterMaxDepthWorld)
//                   COLOR.a = spill distance across the shore skirt
//                              (0 over water tiles, 1 at the skirt's far edge)
//                   COLOR.b = reserved for the S1.4 flow (packed angle)
//                 This is the ONE sanctioned transparent surface (style
//                 guide §2.3 / art plan S1). Single mesh → no MultiMesh
//                 sort hazard. Rebuilt on map regen like the scatter fields.
// Layer:          System (visuals)
// Collaborators:  HexGridManager.cs (Tiles, AxialToWorld, HexRadius, regen
//                 path — SpawnWaterPlane() is called with the other fields),
//                 HexMeshBuilder.SampleSurfaceWorldY (depth bake),
//                 WindNoise.cs (procedural noise fallback),
//                 painterly_water.gdshader
// Notes:          Spawn AFTER ApplyTileHeights()/ApplyTileVisuals() — the
//                 depth bake samples the final blended terrain surface.
//                 Material resolution mirrors the grass: explicit material
//                 export wins, else shader + WindNoise.CreateSeamless().
// ============================================================

public partial class HexGridManager : Node3D
{
    // ── Tuning ──────────────────────────────────────────────────────────────

    /// <summary>Master toggle for the painterly water plane.</summary>
    [ExportGroup("Water Shader")]
    [Export] public bool EnableWaterPlane = true;

    /// <summary>Surface height above the deepest bed for LANDLESS bodies only (open sea). Any body touching land takes its level from the land instead (see WaterShoreLip).</summary>
    [Export(PropertyHint.Range, "0.1,1.0,0.05")] public float WaterFillDepth = 0.4f;

    /// <summary>How far the surface sits below the LOWEST adjacent bank top. Small (~0.06) = water at grade, spilling into the neighbors' noise dips — the marsh look; large = recessed pond in a visible basin.</summary>
    [Export(PropertyHint.Range, "0.0,0.4,0.01")] public float WaterShoreLip = 0.06f;

    /// <summary>Water thickness (world units) that maps to depth 1.0 in the baked COLOR.g channel.</summary>
    [Export(PropertyHint.Range, "0.2,4.0,0.1")] public float WaterMaxDepthWorld = 1.5f;

    /// <summary>World-unit span over which baked shore distance goes 0 → 1 (the shader's foam bands live inside this).</summary>
    [Export(PropertyHint.Range, "0.3,4.0,0.1")] public float WaterShoreRange = 1.6f;

    /// <summary>Hex rings searched around each water tile for land when baking shore distance. 3 covers WaterShoreRange at default HexRadius.</summary>
    [Export(PropertyHint.Range, "1,6,1")] public int WaterShoreSearchRings = 3;

    /// <summary>Ramped beaches: water/land seams blend instead of cliffing (rule mirrored inside HexMeshBuilder), and the plane extends one tile under the bank so the waterline is the organic terrain∩surface intersection, not a hex edge.</summary>
    [Export] public bool BeachBlendWaterShores = true;

    /// <summary>How strongly water tiles pull shared shore corners down in the height weld (1 = plain average). Keeps the hex boundary ring below the waterline so the shoreline crosses on the noisy inner ramp — cures spiky corner wedges and z-flicker at shore corners.</summary>
    [Export(PropertyHint.Range, "1.0,4.0,0.1")] public float WaterShoreCornerSink = 2.0f;

    /// <summary>Explicit water material. When set it wins outright; assign its water_noise slot yourself (materials don't auto-inject noise — style guide §8).</summary>
    [Export] public Material WaterMaterial;

    /// <summary>Water shader used when no explicit material is set. Falls back to WaterShaderPath.</summary>
    [Export] public Shader WaterShader;

    /// <summary>Load path for the water shader when neither export above is assigned.</summary>
    [Export] public string WaterShaderPath = "res://Assets/Shaders/painterly_water.gdshader";

    /// <summary>Optional: the map's sun. When set, its forward vector is pushed into the shader's sun_direction so sparkle glints align with the real light.</summary>
    [Export] public DirectionalLight3D WaterSunLight;

    private const string WaterPlaneGroup = "water_plane";
    private ShaderMaterial _waterMaterialCache;

    // ── Public entry points (called from GenerateMap with the other fields) ──

    /// <summary>Removes any existing water plane. Safe to call when none exists.</summary>
    public void ClearWaterPlane()
    {
        Node parent = PropParent ?? this;
        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup(WaterPlaneGroup))
                child.QueueFree();
        }
    }

    /// <summary>
    /// Builds the water plane over all Water-classified tiles. Call after
    /// ApplyTileHeights()/ApplyTileVisuals() — the depth bake samples the
    /// final blended terrain surface under each vertex.
    /// </summary>
    public void SpawnWaterPlane()
    {
        ClearWaterPlane();

        if (!EnableWaterPlane)
            return;

        List<TileData> waterTiles = CollectAllWaterTiles();

        if (waterTiles.Count == 0)
        {
            GD.Print("[WaterPlane] No Water-classified tiles on this map — nothing to build.");
            return;
        }

        Material mat = ResolveWaterMaterial();
        if (mat == null)
            return;

        // Per-BODY waterline: flood-fill connected water tiles, then set each
        // body's surface from its own bed and its lowest adjacent land lip.
        // A single global waterline floats above any land that happens to sit
        // low (Grassland at Height -1 next to a pond) — learned the hard way.
        Dictionary<Vector2I, float> surfaceYByTile = ComputeBodyWaterlines(waterTiles);

        // Shore skirt: with ramped beaches the plane extends one tile under
        // every adjacent land tile. The buried part is depth-hidden; the part
        // where the ramp dips below the surface becomes visible water, so the
        // waterline is drawn by the terrain intersection — noisy and organic,
        // never a hex silhouette.
        var skirt = new Dictionary<TileData, float>();
        if (BeachBlendWaterShores)
        {
            foreach (var tile in waterTiles)
            {
                float y = surfaceYByTile[tile.Axial];
                foreach (var dir in HexDirs)
                {
                    var nbr = GetTileOrVista(tile.Axial + dir);
                    if (nbr != null &&
                        nbr.TerrainType != TileTerrainType.Water &&
                        !skirt.ContainsKey(nbr))
                        skirt[nbr] = y;
                }
            }
        }

        GD.Print($"[WaterPlane] Building water surface over {waterTiles.Count} tiles (+{skirt.Count} shore-skirt tiles).");

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        foreach (var tile in waterTiles)
            AppendWaterHex(st, tile, surfaceYByTile[tile.Axial], isSkirt: false);
        foreach (var kv in skirt)
            AppendWaterHex(st, kv.Key, kv.Value, isSkirt: true);

        st.Index();
        st.GenerateTangents();
        var mesh = st.Commit();

        var mi = new MeshInstance3D
        {
            Name = "WaterPlane",
            Mesh = mesh,
            MaterialOverride = mat,
            ExtraCullMargin = 0.5f, // ripple displacement headroom
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        mi.AddToGroup(WaterPlaneGroup);

        Node parent = PropParent ?? this;
        parent.AddChild(mi);
        mi.GlobalTransform = Transform3D.Identity; // vertices are world-space

        if (WaterSunLight != null && mat is ShaderMaterial sm)
            sm.SetShaderParameter("sun_direction", -WaterSunLight.GlobalTransform.Basis.Z);
    }

    // ── Material resolution (mirrors ResolvePainterlyGrassMaterial) ─────────

    private Material ResolveWaterMaterial()
    {
        if (WaterMaterial != null)
            return WaterMaterial;

        if (_waterMaterialCache != null)
            return _waterMaterialCache;

        Shader shader = WaterShader;
        if (shader == null && !string.IsNullOrEmpty(WaterShaderPath))
            shader = GD.Load<Shader>(WaterShaderPath);

        if (shader == null)
        {
            GD.PushWarning($"[HexGridManager] Painterly water shader not found " +
                $"(assign WaterShader, or place it at '{WaterShaderPath}'). Water plane skipped.");
            return null;
        }

        var sm = new ShaderMaterial { Shader = shader };
        // Denser noise than the grass gust texture: the water samples it for
        // ripple/clouds/foam/sparkle at world scale, not for long gust streaks.
        sm.SetShaderParameter("water_noise", WindNoise.CreateSeamless(512, 0.008f, 24680));
        _waterMaterialCache = sm;
        return sm;
    }

    // ── Basin dig (generation-time) ─────────────────────────────────────────

    /// <summary>
    /// Guarantees every connected water body sits in a REAL basin: all its
    /// tiles are dug at least one height step below the lowest land tile
    /// adjacent to the body. Without this, wetlands-style maps (banks at the
    /// same height as the bed) have no basin to fill and the surface floats
    /// as a raised film above the surrounding ground. Bodies that are already
    /// legal (typical land-0 / water-−1 ponds) are untouched.
    /// Call from the generate sequence AFTER GenerateVistaRing (vista water
    /// joins the same bodies) and BEFORE ApplyTileHeights.
    /// </summary>
    public void DigWaterBasins()
    {
        if (!EnableWaterPlane)
            return;

        List<TileData> waterTiles = CollectAllWaterTiles();

        if (waterTiles.Count == 0)
            return;

        int dug = 0;
        foreach (var body in CollectWaterBodies(waterTiles))
        {
            int adjLandMin = int.MaxValue;
            foreach (var t in body)
            {
                foreach (var dir in HexDirs)
                {
                    var nbr = GetTileOrVista(t.Axial + dir);
                    if (nbr != null && nbr.TerrainType != TileTerrainType.Water)
                        adjLandMin = Math.Min(adjLandMin, nbr.Height);
                }
            }

            if (adjLandMin == int.MaxValue)
                continue; // landless body (open sea) — nothing to dig against

            int maxAllowed = adjLandMin - 1;
            foreach (var t in body)
            {
                if (t.Height > maxAllowed)
                {
                    t.Height = maxAllowed;
                    dug++;
                }
            }
        }

        if (dug > 0)
            GD.Print($"[WaterPlane] Dug {dug} water tile(s) below their banks so basins exist.");
    }

    /// <summary>
    /// All water tiles the surface must cover: playable AND vista-ring water.
    /// On Coast/Water maps the sea continues past the playable boundary
    /// (S1 item 6) — leaving vista water on the flat splat draws a hard seam
    /// through the ocean.
    /// </summary>
    private List<TileData> CollectAllWaterTiles()
    {
        var waterTiles = new List<TileData>();
        foreach (var tile in Tiles.Values)
        {
            if (tile.TerrainType == TileTerrainType.Water)
                waterTiles.Add(tile);
        }
        foreach (var tile in VistaTiles.Values)
        {
            if (tile.TerrainType == TileTerrainType.Water)
                waterTiles.Add(tile);
        }
        return waterTiles;
    }

    /// <summary>Flood-fills the water tiles into connected bodies (shared by the dig and the waterline solve).</summary>
    private List<List<TileData>> CollectWaterBodies(List<TileData> waterTiles)
    {
        var bodies = new List<List<TileData>>();
        var visited = new HashSet<Vector2I>();
        var queue = new Queue<TileData>();

        foreach (var seed in waterTiles)
        {
            if (!visited.Add(seed.Axial))
                continue;

            var body = new List<TileData>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                body.Add(t);

                foreach (var dir in HexDirs)
                {
                    var coord = t.Axial + dir;
                    var nbr = GetTileOrVista(coord);
                    if (nbr != null &&
                        nbr.TerrainType == TileTerrainType.Water &&
                        visited.Add(coord))
                        queue.Enqueue(nbr);
                }
            }

            bodies.Add(body);
        }

        return bodies;
    }

    // ── Waterline solve ─────────────────────────────────────────────────────

    /// <summary>
    /// Flood-fills water tiles into connected bodies and computes one flat
    /// surface Y per body:
    ///   preferred = deepest bed top + WaterFillDepth
    ///   capped    = lowest adjacent land top − WaterShoreLip (never floods a bank)
    ///   floored   = shallowest bed top + 0.05 (never sinks under its own bed)
    /// Ponds, lakes and seas each get their own level, so low-lying land next
    /// to one pond can't drag another pond's surface around.
    /// </summary>
    private Dictionary<Vector2I, float> ComputeBodyWaterlines(List<TileData> waterTiles)
    {
        var result = new Dictionary<Vector2I, float>();

        foreach (var body in CollectWaterBodies(waterTiles))
        {
            float bedMaxTop = float.MinValue;   // shallowest bed in the body
            float bedMinTop = float.MaxValue;   // deepest bed in the body
            float adjLandMinTop = float.MaxValue;

            foreach (var t in body)
            {
                float bedTop = t.Height * HexTile.HeightStep;
                bedMaxTop = Mathf.Max(bedMaxTop, bedTop);
                bedMinTop = Mathf.Min(bedMinTop, bedTop);

                foreach (var dir in HexDirs)
                {
                    var nbr = GetTileOrVista(t.Axial + dir);
                    if (nbr != null && nbr.TerrainType != TileTerrainType.Water)
                        adjLandMinTop = Mathf.Min(adjLandMinTop, nbr.Height * HexTile.HeightStep);
                }
            }

            // Bodies with land take their level FROM the land: a small lip below
            // the lowest bank, so water reads at grade and spills into the
            // neighbors' noise dips (the user-directed marsh look). FillDepth
            // only governs landless bodies (open sea).
            float surfaceY = adjLandMinTop != float.MaxValue
                ? adjLandMinTop - WaterShoreLip
                : bedMinTop + WaterFillDepth;
            // Safety floor only — DigWaterBasins guarantees room below the lip.
            surfaceY = Mathf.Max(surfaceY, bedMaxTop + 0.05f);

            foreach (var t in body)
                result[t.Axial] = surfaceY;
        }

        return result;
    }

    // ── Mesh construction ───────────────────────────────────────────────────

    /// <summary>
    /// Appends one water tile as a subdivided hex fan: each of the six
    /// center–corner–corner wedges is split into four sub-triangles, giving
    /// the vertex shader enough resolution for the painterly ripple.
    /// </summary>
    private void AppendWaterHex(SurfaceTool st, TileData tile, float waterY, bool isSkirt)
    {
        Vector3 c = AxialToWorld(tile.Axial);
        c.Y = waterY;

        // Skirt fans bake a spill-distance channel (COLOR.a): 0 at the water
        // boundary, 1 toward the skirt's far side, so the shader can feather
        // spilled water out instead of cutting it at the skirt's outer hex edge.
        List<Vector2> waterCenters = null;
        if (isSkirt)
        {
            waterCenters = new List<Vector2>(6);
            foreach (var dir in HexDirs)
            {
                var nbr = GetTileOrVista(tile.Axial + dir);
                if (nbr != null && nbr.TerrainType == TileTerrainType.Water)
                {
                    Vector3 wc = AxialToWorld(nbr.Axial);
                    waterCenters.Add(new Vector2(wc.X, wc.Z));
                }
            }
        }

        // Flat-top corner ring — same 60°·i convention as HexMeshBuilder.Corner.
        var corners = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float ang = Mathf.DegToRad(60f * i);
            corners[i] = new Vector3(
                c.X + HexRadius * Mathf.Cos(ang),
                waterY,
                c.Z + HexRadius * Mathf.Sin(ang));
        }

        for (int i = 0; i < 6; i++)
        {
            Vector3 a = corners[i];
            Vector3 b = corners[(i + 1) % 6];
            Vector3 mca = (c + a) * 0.5f;
            Vector3 mcb = (c + b) * 0.5f;
            Vector3 mab = (a + b) * 0.5f;

            AddWaterTri(st, tile, waterCenters, c, mca, mcb);
            AddWaterTri(st, tile, waterCenters, mca, a, mab);
            AddWaterTri(st, tile, waterCenters, mca, mab, mcb);
            AddWaterTri(st, tile, waterCenters, mcb, mab, b);
        }
    }

    private void AddWaterTri(SurfaceTool st, TileData tile, List<Vector2> waterCenters, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        // GODOT WINDING GOTCHA: front faces are CLOCKWISE seen from the front
        // (opposite of the OpenGL right-hand convention). The corner ring is
        // authored at 60°·i in XZ, so the natural p0→p1→p2 order is what reads
        // clockwise from +Y. Swapping any two vertices makes the whole plane
        // visible only from BELOW — it builds, logs, and renders to the fish.
        AddWaterVertex(st, tile, waterCenters, p0);
        AddWaterVertex(st, tile, waterCenters, p1);
        AddWaterVertex(st, tile, waterCenters, p2);
    }

    private void AddWaterVertex(SurfaceTool st, TileData tile, List<Vector2> waterCenters, Vector3 pos)
    {
        float shore = BakeShoreDistance(tile, pos);
        float depth = BakeDepth(tile, pos, pos.Y);

        float spill = 0f;
        if (waterCenters != null && waterCenters.Count > 0)
        {
            float inradius = HexRadius * Mathf.Sqrt(3f) * 0.5f;
            float best = float.MaxValue;
            foreach (var wc in waterCenters)
            {
                float d = new Vector2(pos.X - wc.X, pos.Z - wc.Y).Length() - inradius;
                if (d < best)
                    best = d;
            }
            spill = Mathf.Clamp(best / (HexRadius * 1.7f), 0f, 1f);
        }

        st.SetColor(new Color(shore, depth, 0f, spill));
        st.SetNormal(Vector3.Up);
        st.SetUV(new Vector2(pos.X, pos.Z) * 0.1f);
        st.AddVertex(pos);
    }

    // ── Per-vertex bakes ────────────────────────────────────────────────────

    /// <summary>
    /// Smooth world-space distance from a vertex to the nearest land edge,
    /// normalized by WaterShoreRange. Searched over the axial neighborhood so
    /// the value curves continuously across tile boundaries (per-tile BFS
    /// would band the foam along hex edges).
    /// </summary>
    private float BakeShoreDistance(TileData tile, Vector3 pos)
    {
        // Approximate distance to land as (distance to land tile center) minus
        // the hex inradius — error is well under the shader's foam wobble.
        float inradius = HexRadius * Mathf.Sqrt(3f) * 0.5f;

        float best = float.MaxValue;
        int r = WaterShoreSearchRings;

        for (int dq = -r; dq <= r; dq++)
        {
            for (int dr = Math.Max(-r, -dq - r); dr <= Math.Min(r, -dq + r); dr++)
            {
                var coord = tile.Axial + new Vector2I(dq, dr);
                var nbr = GetTileOrVista(coord);
                if (nbr == null)
                    continue; // truly off-map = open water, not shore
                if (nbr.TerrainType == TileTerrainType.Water)
                    continue;

                Vector3 lc = AxialToWorld(coord);
                float d = new Vector2(pos.X - lc.X, pos.Z - lc.Z).Length() - inradius;
                if (d < best)
                    best = d;
            }
        }

        if (best == float.MaxValue)
            return 1f;

        return Mathf.Clamp(best / WaterShoreRange, 0f, 1f);
    }

    /// <summary>
    /// Baked depth 0..1: the submerged blended terrain surface under this
    /// vertex vs the water surface. Uses the same analytic sample as the
    /// grass/props so the depth tint agrees with the visible lakebed.
    /// </summary>
    private float BakeDepth(TileData tile, Vector3 pos, float waterY)
    {
        float bedY;
        if (UseBlendedTerrainMesh && tile.TileView != null)
        {
            bedY = HexMeshBuilder.SampleSurfaceWorldY(
                this, tile, pos.X, pos.Z, TerrainSolidFactor, TerrainTerraceSteps);
        }
        else
        {
            bedY = tile.Height * HexTile.HeightStep;
        }

        float thickness = Mathf.Max(waterY - bedY, 0f);
        return Mathf.Clamp(thickness / WaterMaxDepthWorld, 0f, 1f);
    }
}
