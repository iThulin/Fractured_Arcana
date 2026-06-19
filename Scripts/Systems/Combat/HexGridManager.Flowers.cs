using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Flowers.cs  (partial of HexGridManager)
//
// Scattered flower props over grass/forest tiles — MultiMesh layer(s)
// on top of the painterly grass carpet, mirroring the Blender reference
// where modelled flowers ride the same scatter surface as the grass.
//
// VARIETY: a single MultiMesh can hold only one mesh, so a pool of
// distinct flower meshes (single blooms AND pre-modelled 2/3/5-flower
// clusters) is supported by assigning each scatter point a random
// variant (optionally weighted) and emitting one MultiMesh per variant.
//
// Shares the grass clump-noise field (same seed + frequency) so flowers
// bloom where the grass is dense, and samples the same surface height
// (SampleGrassSurfaceY) so they sit on the blended terrain like the grass.
//
// Per-instance colour (MultiMesh UseColors) tints each placed instance.
// NOTE: a cluster mesh is ONE instance, so it receives ONE palette colour
// across all its blooms — bake per-bloom colour into the cluster mesh's
// vertex colours and turn UseFlowerColorVariation OFF if you want varied
// colours within a single cluster asset.
//
// INTEGRATION: one line at the tail of GenerateMap(), after
//   SpawnPainterlyGrass();
//       SpawnFlowerProps();
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Flower Props")]
    [Export] public bool EnableFlowerProps = true;

    /// <summary>Pool of flower mesh variants (single blooms and/or multi-flower clusters). Each scatter point picks one at random. If empty, falls back to FlowerMesh, then to a procedural placeholder.</summary>
    [Export] public Mesh[] FlowerMeshes;

    /// <summary>Optional relative spawn weights, parallel to FlowerMeshes. Higher = picked more often. Leave empty for equal odds. Weight dense cluster meshes LOWER since each places several blooms at once.</summary>
    [Export] public float[] FlowerMeshWeights;

    /// <summary>Single-mesh fallback, used only when FlowerMeshes is empty.</summary>
    [Export] public Mesh FlowerMesh;

    /// <summary>Material for the flowers. REQUIRED — if null, flowers are skipped (never rendered material-less). Assign painterly_flower.tres (sway + clean palette colours).</summary>
    [Export] public Material FlowerMaterial;

    [Export(PropertyHint.Range, "0,40,1")] public int FlowersPerTile = 6;
    [Export(PropertyHint.Range, "0.05,2.0,0.05")] public float FlowerScale = 0.5f;
    [Export(PropertyHint.Range, "0,0.8,0.05")] public float FlowerScaleJitter = 0.25f;

    /// <summary>0 = flowers scatter evenly; 1 = flowers appear only where the grass clump field is dense. Uses the SAME noise as grass clumping, so blooms land in the grass masses.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float FlowerClumpBias = 0.6f;

    /// <summary>Lift each flower slightly along +Y so it sits on the grass rather than buried in it.</summary>
    [Export(PropertyHint.Range, "0,0.3,0.01")] public float FlowerSurfaceOffset = 0.03f;

    /// <summary>Random tilt (radians) so flowers don't all stand perfectly upright.</summary>
    [Export(PropertyHint.Range, "0,0.6,0.02")] public float FlowerTiltJitter = 0.18f;

    [Export] public bool FlowerOnForest = true;

    /// <summary>Per-instance palette tint via MultiMesh instance colour. The material must read instance/vertex colour (painterly_flower does). Turn OFF for cluster meshes whose per-bloom colours are baked into the mesh.</summary>
    [Export] public bool UseFlowerColorVariation = true;

    /// <summary>Palette sampled per instance when UseFlowerColorVariation is on.</summary>
    [Export]
    public Color[] FlowerPalette =
    {
        new Color(0.97f, 0.84f, 0.30f), // warm yellow
        new Color(0.72f, 0.55f, 0.85f), // soft violet
        new Color(0.96f, 0.96f, 0.92f)  // off-white
    };

    private const string FlowerPropGroup = "flower_props";

    public void SpawnFlowerProps()
    {
        ClearFlowerProps();

        if (!EnableFlowerProps)
            return;

        if (FlowerMaterial == null)
        {
            GD.PushWarning("[HexGridManager] FlowerMaterial unassigned — flowers skipped. " +
                "Assign painterly_flower.tres (or any lit material that reads instance colour).");
            return;
        }

        // ── Resolve the variant pool (skip null entries, align weights) ──
        var variantMeshes = new List<Mesh>();
        var variantWeights = new List<float>();
        if (FlowerMeshes != null && FlowerMeshes.Length > 0)
        {
            for (int i = 0; i < FlowerMeshes.Length; i++)
            {
                if (FlowerMeshes[i] == null)
                    continue;
                variantMeshes.Add(FlowerMeshes[i]);
                float w = (FlowerMeshWeights != null && i < FlowerMeshWeights.Length)
                    ? Mathf.Max(0f, FlowerMeshWeights[i])
                    : 1f;
                variantWeights.Add(w);
            }
        }
        if (variantMeshes.Count == 0)
        {
            variantMeshes.Add(FlowerMesh ?? BuildProceduralFlowerMesh());
            variantWeights.Add(1f);
        }

        int variantCount = variantMeshes.Count;
        float totalWeight = 0f;
        foreach (float w in variantWeights)
            totalWeight += w;

        float densityScalar = DensityPreset switch
        {
            MapDensityPreset.Sparse => 0.5f,
            MapDensityPreset.Standard => 1.0f,
            MapDensityPreset.Dense => 1.4f,
            MapDensityPreset.Wild => 1.8f,
            _ => 1.0f
        };

        var rng = new RandomNumberGenerator { Seed = (ulong)(MapSeed ^ 0x46_4C_57_52) }; // "FLWR"

        var clumpNoise = new FastNoiseLite
        {
            Seed = unchecked(MapSeed ^ 0x13577531),
            Frequency = GrassClumpFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth
        };

        var nbrDir = new Vector2[6];
        for (int k = 0; k < 6; k++)
        {
            Vector3 d = AxialToWorld(HexDirs[k]);
            nbrDir[k] = new Vector2(d.X, d.Z).Normalized();
        }
        Vector3 d0 = AxialToWorld(HexDirs[0]);
        float apothem = 0.5f * new Vector2(d0.X, d0.Z).Length();

        bool IsFlowerSurface(TileData t) =>
            t.TerrainType == TileTerrainType.Grass ||
            (FlowerOnForest && t.TerrainType == TileTerrainType.Forest);

        // Per-variant buckets so each distinct mesh becomes its own MultiMesh.
        bool useColors = UseFlowerColorVariation;
        bool paletteOk = FlowerPalette != null && FlowerPalette.Length > 0;
        var tfBuckets = new List<Transform3D>[variantCount];
        var colBuckets = useColors ? new List<Color>[variantCount] : null;
        for (int v = 0; v < variantCount; v++)
        {
            tfBuckets[v] = new List<Transform3D>();
            if (colBuckets != null)
                colBuckets[v] = new List<Color>();
        }

        int PickVariant()
        {
            if (variantCount == 1)
                return 0;
            if (totalWeight <= 0f)
                return rng.RandiRange(0, variantCount - 1);
            float r = rng.RandfRange(0f, totalWeight);
            float acc = 0f;
            for (int v = 0; v < variantCount; v++)
            {
                acc += variantWeights[v];
                if (r <= acc)
                    return v;
            }
            return variantCount - 1;
        }

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null || tile.IsBlocked || !IsFlowerSurface(tile))
                continue;

            int count = Mathf.Max(0, Mathf.RoundToInt(FlowersPerTile * densityScalar));
            Vector3 top = tile.TileView.GlobalPosition;

            for (int i = 0; i < count; i++)
            {
                Vector2 p;
                int guard = 0;
                do
                {
                    float ang = rng.RandfRange(0f, Mathf.Tau);
                    float radc = HexRadius * Mathf.Sqrt(rng.Randf());
                    p = new Vector2(Mathf.Cos(ang) * radc, Mathf.Sin(ang) * radc);
                    guard++;
                }
                while (!InsideHex(p, nbrDir, apothem) && guard < 8);
                if (!InsideHex(p, nbrDir, apothem))
                    continue;

                float wx = top.X + p.X;
                float wz = top.Z + p.Y;

                if (FlowerClumpBias > 0f)
                {
                    float cn = clumpNoise.GetNoise2D(wx, wz) * 0.5f + 0.5f;
                    float accept = Mathf.Lerp(1f, cn, FlowerClumpBias);
                    if (rng.Randf() > accept)
                        continue;
                }

                float sy = SampleGrassSurfaceY(tile, wx, wz) + FlowerSurfaceOffset;
                var pos = new Vector3(wx, sy, wz);

                float yaw = rng.RandfRange(0f, Mathf.Tau);
                float tiltA = rng.RandfRange(0f, FlowerTiltJitter);
                float tiltDir = rng.RandfRange(0f, Mathf.Tau);
                float s = Mathf.Max(0.02f, FlowerScale * (1f + rng.RandfRange(-FlowerScaleJitter, FlowerScaleJitter)));

                var basis = new Basis(Vector3.Up, yaw);
                var tiltAxis = new Vector3(Mathf.Cos(tiltDir), 0f, Mathf.Sin(tiltDir));
                basis = new Basis(tiltAxis, tiltA) * basis;
                basis = basis.Scaled(new Vector3(s, s, s));

                int variant = PickVariant();
                tfBuckets[variant].Add(new Transform3D(basis, pos));

                if (colBuckets != null)
                {
                    Color c = paletteOk
                        ? FlowerPalette[rng.RandiRange(0, FlowerPalette.Length - 1)]
                        : Colors.White;
                    colBuckets[variant].Add(c);
                }
            }
        }

        Node parent = PropParent ?? this;
        for (int v = 0; v < variantCount; v++)
        {
            var tf = tfBuckets[v];
            if (tf.Count == 0)
                continue;

            var col = colBuckets?[v];
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = col != null,
                Mesh = variantMeshes[v],
                InstanceCount = tf.Count
            };

            for (int i = 0; i < tf.Count; i++)
            {
                mm.SetInstanceTransform(i, tf[i]);
                if (col != null)
                    mm.SetInstanceColor(i, col[i]);
            }

            var mmi = new MultiMeshInstance3D
            {
                Name = $"FlowerPropField_{v}",
                Multimesh = mm,
                MaterialOverride = FlowerMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            mmi.AddToGroup(FlowerPropGroup);
            parent.AddChild(mmi);
            mmi.GlobalTransform = Transform3D.Identity;
        }
    }

    private static bool InsideHex(Vector2 p, Vector2[] nbrDir, float apothem)
    {
        for (int k = 0; k < 6; k++)
        {
            if (p.Dot(nbrDir[k]) > apothem)
                return false;
        }
        return true;
    }

    private void ClearFlowerProps()
    {
        Node parent = PropParent ?? this;
        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup(FlowerPropGroup))
                child.QueueFree();
        }
    }

    // Placeholder rosette — used only if no FlowerMeshes and no FlowerMesh are set.
    private static Mesh BuildProceduralFlowerMesh()
    {
        const int petals = 5;
        const float height = 0.35f;
        const float baseHalf = 0.02f;
        const float tipHalf = 0.07f;
        const float lean = 0.10f;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int i = 0; i < petals; i++)
        {
            float a = Mathf.Tau * i / petals;
            var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var right = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));

            Vector3 bl = -right * baseHalf;
            Vector3 br = right * baseHalf;
            Vector3 tl = -right * tipHalf + Vector3.Up * height + outward * lean;
            Vector3 tr = right * tipHalf + Vector3.Up * height + outward * lean;

            st.SetUV(new Vector2(0f, 0f));
            st.AddVertex(bl);
            st.SetUV(new Vector2(1f, 0f));
            st.AddVertex(br);
            st.SetUV(new Vector2(1f, 1f));
            st.AddVertex(tr);

            st.SetUV(new Vector2(0f, 0f));
            st.AddVertex(bl);
            st.SetUV(new Vector2(1f, 1f));
            st.AddVertex(tr);
            st.SetUV(new Vector2(0f, 1f));
            st.AddVertex(tl);
        }

        st.GenerateNormals();
        st.GenerateTangents();
        return st.Commit();
    }
}
