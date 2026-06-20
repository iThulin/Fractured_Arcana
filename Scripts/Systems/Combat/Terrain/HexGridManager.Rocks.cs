using Godot;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Rocks.cs  (partial of HexGridManager)
//
// Scattered rock props over grass/forest tiles — a MultiMesh layer like the
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

    /// <summary>Pool of rock mesh variants. Each scatter point picks one (optionally weighted). REQUIRED — rocks are skipped if empty.</summary>
    [Export] public Mesh[] RockMeshes;

    /// <summary>Optional relative spawn weights, parallel to RockMeshes. Leave empty for equal odds.</summary>
    [Export] public float[] RockMeshWeights;

    /// <summary>Material for the rocks. REQUIRED — assign painterly_rock.tres. Skipped if null (never rendered material-less).</summary>
    [Export] public Material RockMaterial;

    [Export(PropertyHint.Range, "0,12,1")] public int RocksPerTile = 2;
    [Export(PropertyHint.Range, "0.05,3.0,0.05")] public float RockScale = 0.4f;
    [Export(PropertyHint.Range, "0,0.8,0.05")] public float RockScaleJitter = 0.4f;

    /// <summary>0 = scatter evenly; 1 = rocks appear only in SPARSE-grass (bare) areas. Inverse of the grass/flower clumping — uses the same noise field.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float RockBareBias = 0.6f;

    /// <summary>Push rocks DOWN into the terrain so they sit embedded rather than perched on a flat base.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float RockSinkDepth = 0.05f;

    /// <summary>Random tilt (radians) so rocks don't all sit perfectly level.</summary>
    [Export(PropertyHint.Range, "0,0.8,0.02")] public float RockTiltJitter = 0.30f;

    [Export] public bool RockOnForest = true;

    /// <summary>Per-instance tint via custom data, read by painterly_rock (use_instance_tint). Turn OFF for one uniform rock colour.</summary>
    [Export] public bool UseRockColorVariation = true;

    /// <summary>Tints sampled per rock when UseRockColorVariation is on. Keep them near-white — they MULTIPLY the shader's rock_base, so they nudge tone (cool/warm/light/dark), not recolour.</summary>
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
            GD.PushWarning("[HexGridManager] RockMaterial unassigned — rocks skipped. Assign painterly_rock.tres.");
            return;
        }

        // ── Variant pool (skip nulls, align weights) ──
        var variantMeshes = new List<Mesh>();
        var variantWeights = new List<float>();
        if (RockMeshes != null)
        {
            for (int i = 0; i < RockMeshes.Length; i++)
            {
                if (RockMeshes[i] == null)
                    continue;
                variantMeshes.Add(RockMeshes[i]);
                float wgt = (RockMeshWeights != null && i < RockMeshWeights.Length)
                    ? Mathf.Max(0f, RockMeshWeights[i])
                    : 1f;
                variantWeights.Add(wgt);
            }
        }
        if (variantMeshes.Count == 0)
        {
            GD.PushWarning("[HexGridManager] RockMeshes empty — rocks skipped. Assign at least one rock mesh.");
            return;
        }

        int variantCount = variantMeshes.Count;
        float totalWeight = 0f;
        foreach (float wgt in variantWeights)
            totalWeight += wgt;

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
            (RockOnForest && t.TerrainType == TileTerrainType.Forest);

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
            if (tile.TileView == null || tile.IsBlocked || !IsRockSurface(tile))
                continue;

            int count = Mathf.Max(0, Mathf.RoundToInt(RocksPerTile * densityScalar));
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

                // Bare bias: prefer LOW clump noise (sparse grass), the inverse of flowers.
                if (RockBareBias > 0f)
                {
                    float cn = clumpNoise.GetNoise2D(wx, wz) * 0.5f + 0.5f; // 0..1
                    float accept = Mathf.Lerp(1f, 1f - cn, RockBareBias);
                    if (rng.Randf() > accept)
                        continue;
                }

                float sy = SampleGrassSurfaceY(tile, wx, wz) - RockSinkDepth;
                var pos = new Vector3(wx, sy, wz);

                float yaw = rng.RandfRange(0f, Mathf.Tau);
                float tiltA = rng.RandfRange(0f, RockTiltJitter);
                float tiltDir = rng.RandfRange(0f, Mathf.Tau);
                float s = Mathf.Max(0.02f, RockScale * (1f + rng.RandfRange(-RockScaleJitter, RockScaleJitter)));

                var basis = new Basis(Vector3.Up, yaw);
                var tiltAxis = new Vector3(Mathf.Cos(tiltDir), 0f, Mathf.Sin(tiltDir));
                basis = new Basis(tiltAxis, tiltA) * basis;
                basis = basis.Scaled(new Vector3(s, s, s));

                int variant = PickVariant();
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

            var mmi = new MultiMeshInstance3D
            {
                Name = $"RockPropField_{v}",
                Multimesh = mm,
                MaterialOverride = RockMaterial,
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
