using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Rocks.cs  (partial of HexGridManager)
//
// Scattered rock props over grass/forest tiles: a MultiMesh layer like the
// flowers, but INVERTED: rocks seek BARE ground (where the grass clumping is
// sparse) instead of the dense grass masses. No wind (rocks don't sway). Rocks
// cast shadows and sink slightly into the terrain so they sit embedded.
//
// Reuses helpers from HexGridManager.Flowers.cs (same partial class):
//   InsideHex(...), and the shared scatter members (SampleGrassSurfaceY,
//   GrassClumpFrequency, MapSeed, AxialToWorld, HexDirs, HexRadius, Tiles,
//   PropParent, DensityPreset / MapDensityPreset).
//
// INTEGRATION: one line at the tail of GenerateMap(), after the flowers:
//       SpawnRockProps();
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Rock Props")]
    [Export] public bool EnableRockProps = true;

    /// <summary>Pool of rock mesh variants. Each scatter point picks one (optionally weighted). REQUIRED: rocks are skipped if empty.</summary>
    [Export] public Mesh[] RockMeshes;

    /// <summary>Optional relative spawn weights, parallel to RockMeshes. Leave empty for equal odds.</summary>
    [Export] public float[] RockMeshWeights;

    /// <summary>Separate LARGE-boulder pool used ONLY on stone/mountain tiles. Drop big rounded rock-face meshes here; grass keeps the small scree in RockMeshes. Empty = mountains reuse RockMeshes.</summary>
    [Export] public Mesh[] MountainRockMeshes;

    /// <summary>Optional weights parallel to MountainRockMeshes.</summary>
    [Export] public float[] MountainRockMeshWeights;

    /// <summary>Material for the rocks. REQUIRED: assign painterly_rock.tres. Skipped if null (never rendered material-less).</summary>
    [Export] public Material RockMaterial;

    /// <summary>Optional SEPARATE material for the mountain/stone boulder pool. Mountains want a warm dry rock palette; meadow scree wants the cool mossy one. Null = mountains reuse RockMaterial.</summary>
    [Export] public Material MountainRockMaterial;

    [Export(PropertyHint.Range, "0,12,1")] public int RocksPerTile = 2;
    [Export(PropertyHint.Range, "0.05,3.0,0.05")] public float RockScale = 0.4f;
    [Export(PropertyHint.Range, "0,0.8,0.05")] public float RockScaleJitter = 0.4f;

    /// <summary>0 = scatter evenly; 1 = rocks appear only in SPARSE-grass (bare) areas. Inverse of the grass/flower clumping, and it uses the same noise field.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float RockBareBias = 0.6f;

    /// <summary>Push rocks DOWN into the terrain so they sit embedded rather than perched on a flat base.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float RockSinkDepth = 0.05f;

    /// <summary>Random tilt (radians) so rocks don't all sit perfectly level.</summary>
    [Export(PropertyHint.Range, "0,0.8,0.02")] public float RockTiltJitter = 0.30f;

    [Export] public bool RockOnForest = true;

    /// <summary>Scatter boulders/scree on STONE tiles too (mountains). Off = old grass-only behaviour.</summary>
    [Export] public bool RockOnStone = true;

    /// <summary>Rock COUNT multiplier on stone tiles, since mountains want more scree/boulders than meadows. Applied on top of RocksPerTile.</summary>
    [Export(PropertyHint.Range, "1,8,0.5")] public float RockStoneDensityMult = 2.0f;

    /// <summary>Rock SCALE multiplier on stone tiles, for bigger, protruding boulders on rock faces.</summary>
    [Export(PropertyHint.Range, "1,4,0.1")] public float RockStoneScaleMult = 1.7f;

    /// <summary>Per-instance tint via custom data, read by painterly_rock (use_instance_tint). Turn OFF for one uniform rock colour.</summary>
    [Export] public bool UseRockColorVariation = true;

    /// <summary>Tints sampled per rock when UseRockColorVariation is on. Keep them near-white. They MULTIPLY the shader's rock_base, so they nudge tone (cool/warm/light/dark), not recolour.</summary>
    [Export]
    public Color[] RockPalette =
    {
        new Color(1.00f, 1.00f, 1.00f), // neutral
        new Color(0.92f, 0.90f, 0.86f), // warm/light
        new Color(0.82f, 0.84f, 0.88f), // cool
        new Color(0.78f, 0.76f, 0.74f)  // darker
    };

    private const string RockPropGroup = "rock_props";

    public void SpawnRockProps()
    {
        ClearRockProps();

        if (!EnableRockProps)
            return;

        if (RockMaterial == null)
        {
            GD.PushWarning("[HexGridManager] RockMaterial unassigned, so rocks were skipped. Assign painterly_rock.tres.");
            return;
        }

        // ── Variant pools (skip nulls, align weights) ──
        // TWO pools sharing one variant list: the grass scree pool (RockMeshes)
        // and the mountain boulder pool (MountainRockMeshes). variantIsMountain
        // flags which is which; grassIdx/mountainIdx let PickVariant draw from
        // the right pool per tile. Stone tiles draw mountain boulders when that
        // pool is non-empty, else fall back to the scree pool.
        var variantMeshes = new List<Mesh>();
        var variantWeights = new List<float>();
        var variantIsMountain = new List<bool>();

        void AddPool(Mesh[] meshes, float[] weights, bool mountain)
        {
            if (meshes == null)
                return;
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] == null)
                    continue;
                variantMeshes.Add(meshes[i]);
                variantWeights.Add((weights != null && i < weights.Length) ? Mathf.Max(0f, weights[i]) : 1f);
                variantIsMountain.Add(mountain);
            }
        }
        AddPool(RockMeshes, RockMeshWeights, false);
        AddPool(MountainRockMeshes, MountainRockMeshWeights, true);

        if (variantMeshes.Count == 0)
        {
            GD.PushWarning("[HexGridManager] RockMeshes empty, so rocks were skipped. Assign at least one rock mesh.");
            return;
        }

        // Per-pool index lists + weight sums for weighted picking within a pool.
        var grassIdx = new List<int>();
        var mtnIdx = new List<int>();
        float grassWsum = 0f, mtnWsum = 0f;
        for (int v = 0; v < variantMeshes.Count; v++)
        {
            if (variantIsMountain[v]) { mtnIdx.Add(v); mtnWsum += variantWeights[v]; }
            else { grassIdx.Add(v); grassWsum += variantWeights[v]; }
        }

        int variantCount = variantMeshes.Count;

        float densityScalar = DensityPreset switch
        {
            MapDensityPreset.Sparse => 0.5f,
            MapDensityPreset.Standard => 1.0f,
            MapDensityPreset.Dense => 1.4f,
            MapDensityPreset.Wild => 1.8f,
            _ => 1.0f
        };

        var rng = new RandomNumberGenerator { Seed = (ulong)(MapSeed ^ 0x52_4F_43_4B) }; // "ROCK"

        // Same clump field as grass/flowers, so "bare" matches where grass thins.
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

        bool IsRockSurface(TileData t) =>
            t.TerrainType == TileTerrainType.Grass ||
            (RockOnForest && t.TerrainType == TileTerrainType.Forest) ||
            (RockOnStone && t.TerrainType == TileTerrainType.Stone);

        bool useColors = UseRockColorVariation;
        bool paletteOk = RockPalette != null && RockPalette.Length > 0;
        var tfBuckets = new List<Transform3D>[variantCount];
        var colBuckets = useColors ? new List<Color>[variantCount] : null;
        for (int v = 0; v < variantCount; v++)
        {
            tfBuckets[v] = new List<Transform3D>();
            if (colBuckets != null)
                colBuckets[v] = new List<Color>();
        }

        // Weighted pick WITHIN a pool. Stone tiles with a non-empty mountain
        // pool draw big boulders; everything else draws scree.
        int PickVariant(bool mountain)
        {
            var idx = (mountain && mtnIdx.Count > 0) ? mtnIdx : grassIdx;
            float wsum = (mountain && mtnIdx.Count > 0) ? mtnWsum : grassWsum;
            if (idx.Count == 1 || wsum <= 0f)
                return idx[rng.RandiRange(0, idx.Count - 1)];
            float r = rng.RandfRange(0f, wsum);
            float acc = 0f;
            foreach (int v in idx)
            {
                acc += variantWeights[v];
                if (r <= acc)
                    return v;
            }
            return idx[idx.Count - 1];
        }

        // Playable tiles at full density, vista tiles at VistaScatterDensity
        // (see HexGridManager.Vista.cs).
        foreach (var (tile, scatterDensity) in ScatterTiles())
        {
            if (tile.TileView == null || tile.IsBlocked || !IsRockSurface(tile))
                continue;

            bool isStone = tile.TerrainType == TileTerrainType.Stone;
            float stoneCountMult = isStone ? RockStoneDensityMult : 1f;
            int count = Mathf.Max(0, Mathf.RoundToInt(RocksPerTile * densityScalar * scatterDensity * stoneCountMult));
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

                // Bare bias: prefer LOW clump noise so rocks CLUSTER into scree
                // fields instead of an even carpet. Stone uses HALF strength, so
                // mountains read rocky but still clumped, with bare rock faces
                // showing between the scree, not a solid blanket.
                float effBias = isStone ? RockBareBias * 0.5f : RockBareBias;
                if (effBias > 0f)
                {
                    float cn = clumpNoise.GetNoise2D(wx, wz) * 0.5f + 0.5f; // 0..1
                    float accept = Mathf.Lerp(1f, 1f - cn, effBias);
                    if (rng.Randf() > accept)
                        continue;
                }

                float sy = SampleGrassSurfaceY(tile, wx, wz) - RockSinkDepth;
                var pos = new Vector3(wx, sy, wz);

                float yaw = rng.RandfRange(0f, Mathf.Tau);
                float tiltA = rng.RandfRange(0f, RockTiltJitter);
                float tiltDir = rng.RandfRange(0f, Mathf.Tau);
                float stoneScaleMult = isStone ? RockStoneScaleMult : 1f;
                float s = Mathf.Max(0.02f, RockScale * stoneScaleMult * (1f + rng.RandfRange(-RockScaleJitter, RockScaleJitter)));

                var basis = new Basis(Vector3.Up, yaw);
                var tiltAxis = new Vector3(Mathf.Cos(tiltDir), 0f, Mathf.Sin(tiltDir));
                basis = new Basis(tiltAxis, tiltA) * basis;
                basis = basis.Scaled(new Vector3(s, s, s));

                int variant = PickVariant(isStone);
                tfBuckets[variant].Add(new Transform3D(basis, pos));

                if (colBuckets != null)
                {
                    Color c = paletteOk
                        ? RockPalette[rng.RandiRange(0, RockPalette.Length - 1)]
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
                UseCustomData = col != null,
                Mesh = variantMeshes[v],
                InstanceCount = tf.Count
            };

            for (int i = 0; i < tf.Count; i++)
            {
                mm.SetInstanceTransform(i, tf[i]);
                if (col != null)
                    mm.SetInstanceCustomData(i, col[i]); // tint -> INSTANCE_CUSTOM
            }

            // --- Explicit visibility AABB ---
            // Same reasoning as the grass/canopy fields: Godot's auto-computed
            // MultiMesh AABB is unreliable for world-space scattered instances.
            // The whole field frustum-culls as a single unit on a small camera
            // turn, so the rock layer vanishes in one pop at the screen edge.
            // Build bounds from the actual instance origins and grow by mesh
            // extent and the scale band (rocks don't sway, so no wind margin).
            Vector3 mn = tf[0].Origin;
            Vector3 mx = mn;
            for (int i = 0; i < tf.Count; i++)
            {
                Vector3 o = tf[i].Origin;
                mn.X = Mathf.Min(mn.X, o.X);
                mn.Y = Mathf.Min(mn.Y, o.Y);
                mn.Z = Mathf.Min(mn.Z, o.Z);
                mx.X = Mathf.Max(mx.X, o.X);
                mx.Y = Mathf.Max(mx.Y, o.Y);
                mx.Z = Mathf.Max(mx.Z, o.Z);
            }
            float meshExtent = variantMeshes[v].GetAabb().Size.Length();
            // Account for the larger stone boulders so their AABB isn't too tight (culling pop).
            float maxScaleMult = RockOnStone ? Mathf.Max(1f, RockStoneScaleMult) : 1f;
            float grow = Mathf.Max(2.0f, meshExtent * RockScale * maxScaleMult * (1f + RockScaleJitter) + 1.0f);
            mm.CustomAabb = new Aabb(mn, mx - mn).Grow(grow);

            var mmi = new MultiMeshInstance3D
            {
                Name = $"RockPropField_{v}",
                Multimesh = mm,
                // Mountain boulders get their own material when one is assigned,
                // so stone tiles can run a warm dry palette while grass scree
                // keeps the cool mossy one. Same shader, different .tres.
                MaterialOverride = (variantIsMountain[v] && MountainRockMaterial != null)
                    ? MountainRockMaterial
                    : RockMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On // rocks cast shadows
            };
            mmi.AddToGroup(RockPropGroup);
            parent.AddChild(mmi);
            mmi.GlobalTransform = Transform3D.Identity;
        }
    }

    private void ClearRockProps()
    {
        Node parent = PropParent ?? this;
        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup(RockPropGroup))
                child.QueueFree();
        }
    }
}
