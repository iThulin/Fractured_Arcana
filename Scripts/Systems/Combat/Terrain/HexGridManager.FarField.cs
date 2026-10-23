using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// HexGridManager.FarField.cs  (partial of HexGridManager)
//
// FAR FIELD: one continuous, cheap terrain mesh from the outer edge
// of the vista ring out to FarFieldRadius, so the combat camera can
// never reach the end of the world. The vista ring stays the DETAIL
// band (hex tiles, scatter, treeline). The far field is the landscape
// beyond it. Added in camera_lens_slice1 (2026-10-06).
//
// Why it exists: a 40 degree lens at the default framing sees ground
// about 44 m from the board centre at the top of the frame, and more
// when zoomed out. Four vista rings cover about 22 m. Hex rings cost a
// node, a mesh and scatter per tile, so they cannot be the answer for
// the last 300 m. One mesh can.
//
//   - Stitched, not overlapped. The inner loop IS the vista's outer
//     outline: every tile edge whose neighbour is void, chained by
//     shared corners (integer corner keys, so the chain is exact) and
//     sampled on the tiles' own surfaces. Each inner-loop point is
//     tucked FarFieldTuck inside the tile and FarFieldDrop below its
//     surface, so there is no crack to see through and nothing can
//     poke up through the tiles.
//   - Heights. The edge height relaxes into the map field's own
//     continuation over FarFieldSeamBlend metres (the same MapField the
//     arena and vista sample, interpolated on the hex lattice so it is
//     continuous), then hands over to a broad macro relief so distant
//     ground does not alias, then eases down to the backdrop plane at
//     the rim.
//   - Material. The SHARED terrain splat template itself, never a
//     duplicate, so every later template push (horizon_color, the
//     waterline) reaches it. Instance uniforms: vista_fade = 1 and
//     vista_horizon = 1 (it matches the outermost vista ring at the
//     seam) and far_field = 1 (the distance melt in terrain_splat).
//   - Terrain. Three splat layers chosen from the far field's own
//     terrain mix (rim tiles near the seam, the field classification
//     plus the world-adjacency bias farther out). Water is never
//     painted out here (no water plane covers the far field): water
//     points sink under the backdrop plane and read as haze.
//   - Never in Tiles or VistaTiles. No collision. Casts no shadow.
//
// Space: like BuildArenaBoundary and the scatters, this works in
// GlobalPosition terms and assumes HexGridManager sits at the world
// origin (it does in Battlefield.tscn and CharacterStaging.tscn).
//
// Camera hand-off: WorldExtentCenter / WorldExtentRadius / WorldFloorY
// are read by CenterCameraOverGrid and passed to
// CameraController.SetWorldExtent. ApplyCameraLens sets the combat lens
// and compensates the scene fog for it.
//
// INTEGRATION (GenerateMap, see camera_lens_slice1 apply script):
//     ApplyCameraLens();        // right after the atmosphere block
//     BuildFarField(field);     // right after BuildArenaBoundary()
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Far Field")]

    /** Build the continuous landscape beyond the vista ring. Off = the vista edge meets the haze plane, as before, and camera containment is off (the vista alone is too small to contain the camera in). */
    [Export] public bool EnableFarField = true;

    /** How far (world units from the board centre) the far field reaches. The combat camera treats this as the edge of the world. */
    [Export(PropertyHint.Range, "80,800,10")] public float FarFieldRadius = 320f;

    /** Width of the first loop out from the vista edge. Small keeps the seam following the tile-edge relief. */
    [Export(PropertyHint.Range, "0.2,2,0.05")] public float FarFieldFirstStep = 0.5f;

    /** Each loop outward is this much wider than the one inside it (geometric spacing: dense at the seam, sparse at the rim). */
    [Export(PropertyHint.Range, "1.05,1.5,0.01")] public float FarFieldStepGrowth = 1.2f;

    /** Metres over which the vista edge height relaxes into the map field's own continuation. */
    [Export(PropertyHint.Range, "2,80,1")] public float FarFieldSeamBlend = 16f;

    /** Height of the broad, slow relief the far ground settles into away from the board. */
    [Export(PropertyHint.Range, "0,20,0.5")] public float FarFieldHillAmplitude = 3f;

    /** Wavelength (world units) of that broad relief. */
    [Export(PropertyHint.Range, "20,400,5")] public float FarFieldHillWavelength = 90f;

    /** Distance from the board centre where the far ground starts melting toward the horizon colour. */
    [Export(PropertyHint.Range, "0,400,5")] public float FarFieldMeltStart = 45f;

    /** Distance from the board centre where that melt reaches FarFieldMeltStrength. */
    [Export(PropertyHint.Range, "10,800,5")] public float FarFieldMeltEnd = 230f;

    /** Strength of the far-field melt toward the horizon colour (0 = leave it all to the scene fog). */
    [Export(PropertyHint.Range, "0,1,0.05")] public float FarFieldMeltStrength = 0.8f;

    /** Vertical lens (degrees) of the combat camera, applied on every map generation. Zoom framing is unchanged by construction (CameraController.FovZoomScale). Narrower = flatter perspective and a bigger-feeling world. 0 leaves the camera's own lens alone. */
    [Export(PropertyHint.Range, "0,90,1")] public float CameraLensFov = 40f;

    /// <summary>Centre of the world disc the camera may look at (board centre, Y = 0).</summary>
    public Vector3 WorldExtentCenter => new Vector3(_farFieldCenter.X, 0f, _farFieldCenter.Y);

    /// <summary>Radius of terrain the camera may look across: the far field when it was
    /// built, otherwise 0, which turns containment off (the vista alone is too small to
    /// contain the camera in). Read by CenterCameraOverGrid.</summary>
    public float WorldExtentRadius { get; private set; }

    /// <summary>Lowest terrain surface (arena, vista or far field). The camera's
    /// footprint test uses it as the ground plane, which is the conservative choice.</summary>
    public float WorldFloorY { get; private set; }

    private const string FarFieldGroup = "far_field";
    private const int FarFieldEdgeSubdiv = 4;     // samples per outline edge; EVEN, so one lands on the edge midpoint kink
    private const float FarFieldTuck = 0.15f;     // inner loop sits this far inside the tile edge
    private const float FarFieldDrop = 0.08f;     // and this far below the tile surface

    private Vector2 _farFieldCenter;
    private float _baseShadowMaxDistance = -1f;   // ThemeSun's authored value, cached on the first lens apply

    /// <summary>One point of the vista's outer outline.</summary>
    private struct OutlineSample
    {
        public Vector2 XZ;             // on the tile edge
        public float Y;                // tile surface height at XZ
        public Vector2 TuckXZ;         // XZ moved FarFieldTuck toward the tile centre
        public float TuckY;            // safely under the tile surface there
        public TileTerrainType Terrain;
    }

    // ── Lens ─────────────────────────────────────────────────────────────────

    /// <summary>Sets the combat lens on the camera rig, then compensates the two
    /// distance-authored scene settings for it. A narrower lens frames the same board
    /// from FovZoomScale (k) times farther back. Fog density divides by k, so the board
    /// stays as clear as it was tuned (the far field gets its own distance melt in
    /// terrain_splat, so the horizon still dissolves). The sun's shadow max distance
    /// multiplies by k, which keeps the same shadow texels per screen pixel and keeps
    /// the vista inside the shadowed range. Call ONCE per atmosphere application, right
    /// after the density was written (GenerateMap does). The shadow base is cached, so
    /// repeat calls never compound.</summary>
    private void ApplyCameraLens()
    {
        var controller = FindCameraController();
        if (controller == null)
            return;

        if (CameraLensFov > 0f)
            controller.SetLensFov(CameraLensFov);

        float k = controller.FovZoomScale;
        if (k > 1.001f && ThemeWorldEnvironment?.Environment is Godot.Environment env)
            env.FogDensity /= k;

        if (ThemeSun != null)
        {
            if (_baseShadowMaxDistance < 0f)
                _baseShadowMaxDistance = ThemeSun.DirectionalShadowMaxDistance;
            ThemeSun.DirectionalShadowMaxDistance = _baseShadowMaxDistance * Mathf.Max(k, 1f);
        }
    }

    private CameraController FindCameraController()
    {
        return GetNodeOrNull<CameraController>("../CameraController")
            ?? GetTree()?.Root.FindChild("CameraController", recursive: true, owned: false) as CameraController;
    }

    // ── Build ────────────────────────────────────────────────────────────────

    /// <summary>Builds the far-field mesh. Runs after BuildVistaMeshes (tile heights and
    /// surfaces are final) and after the atmosphere (the template's horizon_color is this
    /// map's). Always refreshes WorldExtentRadius / WorldFloorY, far field or not.</summary>
    private void BuildFarField(MapField field)
    {
        ClearFarField();

        Vector3 c3 = (GridBoundsMin + GridBoundsMax) * 0.5f;
        _farFieldCenter = new Vector2(c3.X, c3.Z);

        var outline = BuildCoverageOutline(out float inscribed);
        // 0 = no containment. Only a built far field is big enough to contain the
        // camera in: the vista alone (16 to 22 m) would pin the rig to the board centre
        // at minimum zoom. Without the far field the camera behaves as it always did.
        WorldExtentRadius = 0f;
        WorldFloorY = LowestCoverageSurfaceY();

        if (!EnableFarField || field == null || outline == null || outline.Count < 12)
            return;

        // A cliff between two neighbouring outline tiles would make the tucked strip run
        // from the high tile's level down to the low one's, slicing a shard up through
        // the cliff wall. Across a real jump (more than one height step) the higher
        // point drops to its lower neighbour, so the strip stays under both tiles.
        // Ordinary edges keep their own height (a general min would deepen the rim dip).
        {
            int m = outline.Count;
            var lowered = new float[m];
            for (int i = 0; i < m; i++)
            {
                float own = outline[i].TuckY;
                float nb = Mathf.Min(outline[(i + m - 1) % m].TuckY, outline[(i + 1) % m].TuckY);
                lowered[i] = nb < own - HexTile.HeightStep ? nb : own;
            }
            for (int i = 0; i < m; i++)
            {
                var o = outline[i];
                o.TuckY = lowered[i];
                outline[i] = o;
            }
        }

        var template = UseTerrainTextures ? GetTerrainMaterialTemplate() : null;
        if (template == null)
        {
            GD.PushWarning("[FarField] No terrain splat template (UseTerrainTextures off or shader missing). Far field skipped.");
            return;
        }

        float radius = Mathf.Max(FarFieldRadius, inscribed + 40f);

        // Loop offsets in metres beyond the outline. Offset 0 is the tucked inner
        // loop. The rest grow geometrically until even the innermost outline point
        // reaches the rim; the last loop is snapped onto the rim circle.
        var offsets = new List<float> { 0f };
        float step = Mathf.Max(FarFieldFirstStep, 0.05f);
        float growth = Mathf.Max(FarFieldStepGrowth, 1.01f);
        float s = 0f;
        while (inscribed + s < radius && offsets.Count < 200)
        {
            s += step;
            offsets.Add(s);
            step *= growth;
        }

        int n = outline.Count;
        int loops = offsets.Count;
        var pos = new Vector3[n * loops];
        var terr = new TileTerrainType[n * loops];

        float rimMean = 0f;
        foreach (var o in outline)
            rimMean += o.Y;
        rimMean /= n;

        // The backdrop plane (GroundMesh) sits at _lastWorldFloor - 0.05 (ApplyHorizon).
        // The far field stays above it and eases onto it at the rim.
        float plane = _lastWorldFloor - 0.05f;
        float floorClamp = plane + 0.05f;

        var macro = new FastNoiseLite
        {
            Seed = MapSeed ^ 0x2C1B3C6D,
            Frequency = 1f / Mathf.Max(FarFieldHillWavelength, 1f),
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 3
        };
        var elevCache = new Dictionary<Vector2I, float>();
        var palette = _activeRecipe?.BaseTerrain?.Palette;
        float seam = Mathf.Max(FarFieldSeamBlend, 0.5f);
        float minY = float.MaxValue;

        for (int k = 0; k < loops; k++)
        {
            for (int i = 0; i < n; i++)
            {
                var o = outline[i];
                int vi = k * n + i;

                if (k == 0)
                {
                    pos[vi] = new Vector3(o.TuckXZ.X, o.TuckY, o.TuckXZ.Y);
                    terr[vi] = o.Terrain;
                    minY = Mathf.Min(minY, o.TuckY);
                    continue;
                }

                // Radial from the board centre. The outline is star-shaped about the
                // centre (a hex dilation of the board), so radial rays never re-enter
                // the tiles and neighbouring rays never cross.
                Vector2 u = o.XZ - _farFieldCenter;
                float baseR = u.Length();
                u = baseR > 0.0001f ? u / baseR : Vector2.Right;
                float r = k == loops - 1 ? radius : Mathf.Min(baseR + offsets[k], radius);
                float sOut = Mathf.Max(r - baseR, 0f);
                Vector2 p = _farFieldCenter + u * r;

                float yCont = Mathf.Max(FieldContinuationY(field, p, elevCache), floorClamp);
                float yNear = Mathf.Lerp(o.Y, yCont, SmoothStep01(sOut / seam));
                float yMacro = Mathf.Max(rimMean + macro.GetNoise2D(p.X, p.Y) * FarFieldHillAmplitude, floorClamp);
                float y = Mathf.Lerp(yNear, yMacro, SmoothStep01((sOut - seam) / (seam * 3f)));
                y = Mathf.Lerp(y, plane + 0.02f, SmoothStep01((r - radius * 0.88f) / (radius * 0.12f)));

                // A ray from a deep notch in a ragged (blob) outline can pass back over a
                // protruding tile. Such a vertex goes UNDER that tile's surface, so the
                // far field can never show on top of the vista.
                var over = GetTileOrVista(NearestAxial(p));
                if (over?.TileView != null)
                    y = Mathf.Min(y, SampleGrassSurfaceY(over, p.X, p.Y) - FarFieldDrop);

                // Water is never painted out here (no water plane covers the far field).
                // Sink it under the backdrop plane instead, so a lake or sea side reads as
                // haze, the way it did before the far field, not as a grass plain.
                var ter = sOut < seam * 0.5f ? o.Terrain : FarFieldTerrainAt(field, p, palette);
                if (ter == TileTerrainType.Water)
                    y = Mathf.Min(y, plane - 0.25f);

                pos[vi] = new Vector3(p.X, y, p.Y);
                terr[vi] = ter;
                minY = Mathf.Min(minY, y);
            }
        }

        // Three splat layers, the most common non-water terrains out here. Indices
        // are constant across the mesh, so interpolating the per-vertex weights
        // blends textures correctly (interpolating INDICES would not).
        var slots = terr.Where(t => t != TileTerrainType.Water)
                        .GroupBy(t => t)
                        .OrderByDescending(g => g.Count())
                        .Select(g => g.Key)
                        .Take(3)
                        .ToList();
        if (slots.Count == 0)
            slots.Add(TileTerrainType.Grass);
        while (slots.Count < 3)
            slots.Add(slots[0]);

        var custom = new Color((int)slots[0], (int)slots[1], (int)slots[2], (int)slots[0]);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);

        for (int vi = 0; vi < pos.Length; vi++)
        {
            int slot = slots.IndexOf(terr[vi]);
            if (slot < 0)
                slot = 0;
            // RGB = layer weights. Alpha stays 0: the splat shader reads COLOR.a as the
            // skirt flag and as a fourth weight, and this surface is all top face.
            st.SetColor(new Color(slot == 0 ? 1f : 0f, slot == 1 ? 1f : 0f, slot == 2 ? 1f : 0f, 0f));
            st.SetCustom(0, custom);
            st.SetUV(new Vector2(pos[vi].X * 0.25f, pos[vi].Z * 0.25f));   // UVs only feed GenerateTangents
            st.AddVertex(ToLocal(pos[vi]));
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

        st.GenerateNormals();
        st.GenerateTangents();
        var mesh = st.Commit();

        // Far-field melt parameters live on the shared template (tiles never set the
        // far_field instance flag, so they ignore them).
        template.SetShaderParameter("far_field_center", _farFieldCenter);
        template.SetShaderParameter("far_melt_start", FarFieldMeltStart);
        template.SetShaderParameter("far_melt_end", Mathf.Max(FarFieldMeltEnd, FarFieldMeltStart + 1f));
        template.SetShaderParameter("far_melt_strength", FarFieldMeltStrength);

        var mi = new MeshInstance3D
        {
            Name = "FarField",
            Mesh = mesh,
            MaterialOverride = template,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        mi.AddToGroup(FarFieldGroup);
        AddChild(mi);
        mi.SetInstanceShaderParameter("vista_fade", 1.0f);
        mi.SetInstanceShaderParameter("vista_horizon", 1.0f);
        mi.SetInstanceShaderParameter("far_field", 1.0f);

        // The camera may look anywhere inside the far field (its rim eases into the
        // backdrop plane, so leave that last stretch out).
        WorldExtentRadius = radius * 0.95f;
        WorldFloorY = Mathf.Min(WorldFloorY, minY);

        GD.Print($"[FarField] {n} rim samples x {loops} loops, inscribed {inscribed:0.0}, radius {radius:0}, " +
                 $"layers {slots[0]}/{slots[1]}/{slots[2]}, floor {WorldFloorY:0.00}.");
    }

    private void ClearFarField()
    {
        foreach (Node child in GetChildren())
        {
            if (child.IsInGroup(FarFieldGroup))
                child.QueueFree();
        }
    }

    /// <summary>Adds a triangle wound so its front face points UP. Godot's front faces are
    /// clockwise seen from the front, which for ground means (b - a) x (c - a) points down
    /// (the same order HexMeshBuilder's top fan uses). Swaps b and c when it does not.</summary>
    private static void AddUpTriangle(SurfaceTool st, Vector3[] pos, int a, int b, int c)
    {
        Vector3 n = (pos[b] - pos[a]).Cross(pos[c] - pos[a]);
        if (n.LengthSquared() < 1e-10f)
            return; // degenerate (rim loops can collapse onto the circle)
        if (n.Y > 0f)
            (b, c) = (c, b);
        st.AddIndex(a);
        st.AddIndex(b);
        st.AddIndex(c);
    }

    // ── Outline ──────────────────────────────────────────────────────────────

    private IEnumerable<TileData> CoverageTiles()
    {
        foreach (var t in Tiles.Values)
            yield return t;
        foreach (var t in VistaTiles.Values)
            yield return t;
    }

    /// <summary>The outer outline of playable + vista tiles: every tile edge whose neighbour
    /// is void, each oriented corner e to corner e+1 (counter-clockwise about its tile in
    /// the XZ math plane), chained by shared corners into one closed loop that runs the
    /// same way about the board, and sampled FarFieldEdgeSubdiv times per edge on the
    /// tiles' own surfaces. Starts from the edge farthest from the centre, so with any
    /// inner loops (holes) present the outer one is the one returned. Null when no closed
    /// loop exists. <paramref name="inscribed"/> = distance from the board centre to the
    /// nearest outline point, i.e. the radius of the largest centred disc the tiles cover.</summary>
    private List<OutlineSample> BuildCoverageOutline(out float inscribed)
    {
        inscribed = 0f;
        var byStart = new Dictionary<Vector2I, (TileData tile, int e)>();
        (TileData tile, int e) far = (null, 0);
        float farDist = -1f;
        int pinches = 0;

        foreach (var tile in CoverageTiles())
        {
            if (tile.TileView == null)
                continue;

            for (int d = 0; d < 6; d++)
            {
                if (GetTileOrVista(tile.Axial + HexDirs[d]) != null)
                    continue;

                // Neighbour HexDirs[d] shares edge (6 - d) % 6 (BuildArenaBoundary's rule).
                int e = (6 - d) % 6;
                if (!byStart.TryAdd(CornerKey(tile.Axial, e), (tile, e)))
                {
                    pinches++;
                    continue;
                }

                float dist = (EdgeMidXZ(tile, e) - _farFieldCenter).Length();
                if (dist > farDist)
                {
                    farDist = dist;
                    far = (tile, e);
                }
            }
        }

        if (far.tile == null)
            return null;

        var samples = new List<OutlineSample>();
        var cur = far;
        Vector2I startKey = CornerKey(cur.tile.Axial, cur.e);
        bool closed = false;

        for (int guard = 0; guard <= byStart.Count; guard++)
        {
            AppendEdgeSamples(cur.tile, cur.e, samples);

            Vector2I next = CornerKey(cur.tile.Axial, (cur.e + 1) % 6);
            if (next == startKey)
            {
                closed = true;
                break;
            }
            if (!byStart.TryGetValue(next, out cur))
                break;
        }

        if (!closed)
        {
            GD.PushWarning("[FarField] The vista outline does not close into one loop. Far field skipped.");
            return null;
        }

        if (pinches > 0)
            GD.Print($"[FarField] Outline has {pinches} pinch corner(s); the loop follows the first edge found at each.");

        inscribed = float.MaxValue;
        foreach (var o in samples)
            inscribed = Mathf.Min(inscribed, (o.XZ - _farFieldCenter).Length());
        return samples;
    }

    private void AppendEdgeSamples(TileData tile, int e, List<OutlineSample> samples)
    {
        Vector2 c = TileCenterXZ(tile);
        Vector2 a = c + HexCornerXZ(e);
        Vector2 b = c + HexCornerXZ((e + 1) % 6);

        for (int k = 0; k < FarFieldEdgeSubdiv; k++)
        {
            Vector2 p = a.Lerp(b, k / (float)FarFieldEdgeSubdiv);
            Vector2 tuck = p + (c - p).Normalized() * FarFieldTuck;
            float y = SampleGrassSurfaceY(tile, p.X, p.Y);
            float tuckY = Mathf.Min(y, SampleGrassSurfaceY(tile, tuck.X, tuck.Y)) - FarFieldDrop;

            samples.Add(new OutlineSample
            {
                XZ = p,
                Y = y,
                TuckXZ = tuck,
                TuckY = tuckY,
                Terrain = tile.TerrainType
            });
        }
    }

    /// <summary>Exact integer key for corner <paramref name="corner"/> of the hex at
    /// <paramref name="axial"/>: the sum of the three hexes that meet there (3x their
    /// centroid). Every tile touching the corner computes the same key, so chaining by it
    /// has no float tolerance to get wrong. Corner c is shared with neighbours
    /// (6 - c) % 6 and (7 - c) % 6, the same pair HexMeshBuilder uses.</summary>
    private static Vector2I CornerKey(Vector2I axial, int corner)
    {
        return axial * 3 + HexDirs[(6 - corner) % 6] + HexDirs[(7 - corner) % 6];
    }

    private Vector2 HexCornerXZ(int corner)
    {
        float a = Mathf.DegToRad(60f * corner);
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * HexRadius;
    }

    private static Vector2 TileCenterXZ(TileData tile)
    {
        Vector3 g = tile.TileView.GlobalPosition;
        return new Vector2(g.X, g.Z);
    }

    private Vector2 EdgeMidXZ(TileData tile, int e)
    {
        return TileCenterXZ(tile) + (HexCornerXZ(e) + HexCornerXZ((e + 1) % 6)) * 0.5f;
    }

    private float LowestCoverageSurfaceY()
    {
        float lo = float.MaxValue;
        foreach (var t in CoverageTiles())
        {
            if (t.TileView != null)
                lo = Mathf.Min(lo, t.TileView.GlobalPosition.Y);
        }
        // Headroom for the surface relief below a tile's origin.
        return lo == float.MaxValue ? _lastWorldFloor : lo - 0.6f;
    }

    // ── Field sampling ───────────────────────────────────────────────────────

    /// <summary>The map field's own height at an arbitrary world point: the elevation is
    /// sampled at the three lattice hexes around the point and blended barycentrically
    /// (hex centres form a triangular lattice), then mapped through the field's height
    /// range WITHOUT rounding, so the continuation is smooth rather than terraced.</summary>
    private float FieldContinuationY(MapField field, Vector2 worldXZ, Dictionary<Vector2I, float> cache)
    {
        // Inverse of AxialToWorld: x = 1.5 R q, z = sqrt(3) R (r + q / 2).
        float q = worldXZ.X / (1.5f * HexRadius);
        float r = worldXZ.Y / (Mathf.Sqrt(3f) * HexRadius) - q * 0.5f;
        int q0 = Mathf.FloorToInt(q);
        int r0 = Mathf.FloorToInt(r);
        float fq = q - q0;
        float fr = r - r0;

        float elev;
        if (fq + fr < 1f)
            elev = E(q0, r0) * (1f - fq - fr) + E(q0 + 1, r0) * fq + E(q0, r0 + 1) * fr;
        else
            elev = E(q0 + 1, r0 + 1) * (fq + fr - 1f) + E(q0, r0 + 1) * (1f - fq) + E(q0 + 1, r0) * (1f - fr);

        float steps = Mathf.Lerp(field.MinHeightStep, field.MaxHeightStep, Mathf.Clamp(elev, 0f, 1f));
        return steps * HexTile.HeightStep;

        float E(int aq, int ar)
        {
            var key = new Vector2I(aq, ar);
            if (!cache.TryGetValue(key, out float v))
            {
                v = field.SampleElevation01(key);
                cache[key] = v;
            }
            return v;
        }
    }

    /// <summary>Terrain for a far-field point: the world-adjacency bias for its side when
    /// one is set (the vista's outer ring already leans 90% that way), otherwise the same
    /// classification the arena and vista use.</summary>
    private TileTerrainType FarFieldTerrainAt(MapField field, Vector2 worldXZ, List<PaletteRule> palette)
    {
        Vector2I coord = NearestAxial(worldXZ);

        if (VistaTerrainBias.Count > 0 && VistaTerrainBias.TryGetValue(DominantDirection(coord), out var biased))
            return biased;

        float elevation = field.SampleElevation01(coord);
        float moisture = field.SampleMoisture01(coord);
        return palette != null
            ? field.ClassifyByPalette(palette, elevation, moisture)
            : field.ClassifyTerrain(Theme, elevation, moisture);
    }

    /// <summary>World XZ to the nearest axial coord (cube rounding).</summary>
    private Vector2I NearestAxial(Vector2 worldXZ)
    {
        float q = worldXZ.X / (1.5f * HexRadius);
        float r = worldXZ.Y / (Mathf.Sqrt(3f) * HexRadius) - q * 0.5f;
        float y = -q - r;

        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int ry = Mathf.RoundToInt(y);

        float dq = Mathf.Abs(rq - q);
        float dr = Mathf.Abs(rr - r);
        float dy = Mathf.Abs(ry - y);

        if (dq > dr && dq > dy)
            rq = -rr - ry;
        else if (dr > dy)
            rr = -rq - ry;

        return new Vector2I(rq, rr);
    }

    private static float SmoothStep01(float x)
    {
        x = Mathf.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
