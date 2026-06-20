using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Props.cs  (partial of HexGridManager)
//
// Purpose:        Data-driven terrain prop scattering, v2.
//                 Replaces centre-clumped jitter with annular
//                 placement (centre exclusion disc -> outer ring),
//                 a seeded world-space density noise field so props
//                 cluster organically ACROSS tiles, and a global
//                 spatial-hash minimum-spacing rejection so props
//                 never stack — including across tile boundaries.
//                 Repeated single-mesh props still batch into one
//                 MultiMeshInstance3D each. Falls back to the legacy
//                 SpawnTerrainProps behaviour when no manifest
//                 resolves.
// Layer:          System (generation)
// Collaborators:  TilesetManifest / TilesetRegistry (data),
//                 HexGridManager.cs (Tiles, _rng, MapSeed,
//                 HexRadius, PropParent, DensityPreset)
// Notes:          - PropEntry.Jitter is DEPRECATED and ignored;
//                   the annulus replaces it. Field kept in the
//                   schema for backward compatibility.
//                 - All randomness flows through _rng / a noise
//                   generator seeded from MapSeed: same seed,
//                   same prop layout.
//                 - SamplePropSurfaceY() is the integration hook
//                   for the blended-hex-mesh pass: today it
//                   returns the flat tile top; later it samples
//                   the blended surface so props sit on slopes.
// ============================================================

public partial class HexGridManager : Node3D
{
    /// <summary>Tileset manifest id from Data/Tilesets. Empty or not-found = legacy grass-tuft scatter.</summary>
    [Export] public string TilesetId = "default";

    [ExportGroup("Prop Scatter")]

    /// <summary>Exclusion disc around the tile centre (fraction of HexRadius). Keeps the unit's footprint clear.</summary>
    [Export(PropertyHint.Range, "0,0.6,0.02")] public float PropClearRadiusFrac = 0.32f;

    /// <summary>Outer scatter radius (fraction of HexRadius). Hex inradius is ~0.866; 0.82 stays safely inside the tile.</summary>
    [Export(PropertyHint.Range, "0.4,0.95,0.01")] public float PropSpreadRadiusFrac = 0.82f;

    /// <summary>Props flagged blocks_los are confined inside this radius so the per-tile LOS flag stays visually honest.</summary>
    [Export(PropertyHint.Range, "0,0.6,0.02")] public float PropLosMaxRadiusFrac = 0.50f;

    /// <summary>Count compensation: the annulus is ~6x the old jitter square's area and noise rejection eats spawns.</summary>
    [Export(PropertyHint.Range, "0.5,4,0.1")] public float PropCountMultiplier = 1.8f;

    /// <summary>0 = uniform density everywhere; 1 = density fully driven by the noise field (dense patches + bare gaps).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float PropDensityNoiseInfluence = 0.55f;

    /// <summary>World-space frequency of the density field. Lower = broader patches spanning several tiles.</summary>
    [Export] public float PropDensityNoiseFrequency = 0.16f;

    /// <summary>Minimum distance between any two props, map-wide (fraction of HexRadius). 0 disables the check.</summary>
    [Export(PropertyHint.Range, "0,0.6,0.02")] public float PropMinSpacingFrac = 0.16f;

    private readonly Dictionary<string, Mesh> _propMeshCache = new();

    /// <summary>
    /// Entry point — replaces the SpawnTerrainProps() call in GenerateMap.
    /// Uses the manifest if one resolves; otherwise defers to the legacy method.
    /// </summary>
    private void SpawnTerrainPropsFromManifest()
    {
        ClearTerrainProps();

        TilesetManifest manifest = null;
        if (!string.IsNullOrEmpty(TilesetId))
        {
            TilesetRegistry.EnsureLoaded();
            manifest = TilesetRegistry.Get(TilesetId);
        }

        if (manifest == null)
        {
            SpawnTerrainProps(); // legacy fallback
            return;
        }

        float densityScalar = DensityPreset switch
        {
            MapDensityPreset.Sparse => 0.5f,
            MapDensityPreset.Standard => 1.0f,
            MapDensityPreset.Dense => 1.4f,
            MapDensityPreset.Wild => 1.8f,
            _ => 1.0f
        };

        // World-space density field. Seeded from MapSeed (xor'd so it never
        // correlates with the field noise in MapField), deterministic per map.
        var densityNoise = new FastNoiseLite
        {
            Seed = unchecked(MapSeed ^ 0x5F375A86),
            Frequency = PropDensityNoiseFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth
        };

        // Spatial hash for global min-spacing. Cell size = the spacing itself,
        // so a 3x3 neighbourhood check covers every possible conflict.
        float minSpacing = PropMinSpacingFrac * HexRadius;
        float minSpacingSq = minSpacing * minSpacing;
        var occupancy = new Dictionary<Vector2I, List<Vector2>>();

        Node parent = PropParent ?? this;
        var batched = new Dictionary<string, List<Transform3D>>();

        float clearRadius = PropClearRadiusFrac * HexRadius;

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null || tile.IsBlocked)
                continue;

            if (!manifest.Terrains.TryGetValue(tile.TerrainType, out var set) || set.Props.Count == 0)
                continue;

            if (_rng.Randf() > set.Chance)
                continue;

            int count = Mathf.RoundToInt(
                _rng.RandiRange(set.CountMin, set.CountMax) * densityScalar * PropCountMultiplier);

            Vector3 basePos = tile.TileView.GlobalPosition;

            for (int i = 0; i < count; i++)
            {
                PropEntry prop = WeightedPick(set.Props);
                if (prop == null || string.IsNullOrEmpty(prop.ScenePath))
                    continue;

                // ── Annular placement ─────────────────────────────────────
                // LOS-blocking props stay near the centre so the tile-level
                // BlocksLineOfSight flag matches what the player sees.
                float spreadRadius = (prop.BlocksLos
                    ? Mathf.Min(PropLosMaxRadiusFrac, PropSpreadRadiusFrac)
                    : PropSpreadRadiusFrac) * HexRadius;

                float innerRadius = prop.BlocksLos ? 0f : clearRadius;
                if (spreadRadius <= innerRadius)
                    spreadRadius = innerRadius + 0.05f;

                // sqrt-lerp of squared radii = uniform distribution by area.
                float r = Mathf.Sqrt(Mathf.Lerp(
                    innerRadius * innerRadius, spreadRadius * spreadRadius, _rng.Randf()));
                float ang = _rng.RandfRange(0f, Mathf.Tau);

                float px = basePos.X + r * Mathf.Cos(ang);
                float pz = basePos.Z + r * Mathf.Sin(ang);
                Vector3 worldPos = new Vector3(
                    px,
                    SamplePropSurfaceY(tile, px, pz) + prop.YOffset,
                    pz);

                // ── Density noise acceptance ──────────────────────────────
                // Sampled in world space, so dense/bare patches flow across
                // tile boundaries instead of quantising per tile.
                float n01 = densityNoise.GetNoise2D(worldPos.X, worldPos.Z) * 0.5f + 0.5f;
                float acceptance = Mathf.Lerp(1f, n01, PropDensityNoiseInfluence);
                if (_rng.Randf() > acceptance)
                    continue;

                // ── Global min-spacing rejection ──────────────────────────
                if (minSpacing > 0f && !TryReservePropSlot(occupancy, worldPos, minSpacing, minSpacingSq))
                    continue;

                float rotY = _rng.RandfRange(0f, Mathf.Tau);
                float scale = _rng.RandfRange(prop.ScaleMin, prop.ScaleMax);

                if (prop.BlocksLos)
                    tile.BlocksLineOfSight = true;

                if (prop.Batch)
                {
                    if (ExtractMesh(prop.ScenePath) == null)
                        continue;

                    if (!batched.TryGetValue(prop.ScenePath, out var list))
                    {
                        list = new List<Transform3D>();
                        batched[prop.ScenePath] = list;
                    }

                    var basis = new Basis(Vector3.Up, rotY).Scaled(new Vector3(scale, scale, scale));
                    list.Add(new Transform3D(basis, worldPos));
                }
                else
                {
                    var ps = GD.Load<PackedScene>(prop.ScenePath);
                    if (ps == null)
                        continue;

                    var node = ps.Instantiate<Node3D>();
                    parent.AddChild(node);
                    node.GlobalPosition = worldPos;

                    Vector3 rot = node.RotationDegrees;
                    rot.Y = Mathf.RadToDeg(rotY);
                    node.RotationDegrees = rot;
                    node.Scale = new Vector3(scale, scale, scale);
                    node.AddToGroup("generated_prop");
                }
            }
        }

        // One MultiMeshInstance3D per distinct batched prop mesh.
        foreach (var kvp in batched)
        {
            Mesh mesh = ExtractMesh(kvp.Key);
            if (mesh == null || kvp.Value.Count == 0)
                continue;

            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = mesh,
                InstanceCount = kvp.Value.Count
            };

            for (int i = 0; i < kvp.Value.Count; i++)
                mm.SetInstanceTransform(i, kvp.Value[i]);

            var mmi = new MultiMeshInstance3D { Multimesh = mm };
            parent.AddChild(mmi);
            mmi.TopLevel = true;                       // instance transforms are world-space
            mmi.GlobalTransform = Transform3D.Identity;
            mmi.AddToGroup("generated_prop");
        }
    }

    /// <summary>
    /// Surface height for prop placement at this world XZ. In blended-mesh
    /// mode this samples the same bilinear blend the mesh quads use, so
    /// props sit correctly on slopes and terraces. Legacy mode returns the
    /// flat tile top.
    /// </summary>
    private float SamplePropSurfaceY(TileData tile, float worldX, float worldZ)
    {
        if (UseBlendedTerrainMesh && tile.TileView != null)
            return HexMeshBuilder.SampleSurfaceWorldY(
                this, tile, worldX, worldZ, TerrainSolidFactor, TerrainTerraceSteps);

        return tile.TileView != null ? tile.TileView.GlobalPosition.Y : 0f;
    }

    /// <summary>
    /// Spatial-hash min-spacing check. Returns false if another prop sits
    /// within minSpacing of pos; otherwise records pos and returns true.
    /// Cell size equals minSpacing, so checking the 3x3 neighbourhood is
    /// sufficient and the whole pass stays O(props).
    /// </summary>
    private static bool TryReservePropSlot(
        Dictionary<Vector2I, List<Vector2>> occupancy,
        Vector3 worldPos, float cellSize, float minDistSq)
    {
        var p = new Vector2(worldPos.X, worldPos.Z);
        var cell = new Vector2I(
            Mathf.FloorToInt(p.X / cellSize),
            Mathf.FloorToInt(p.Y / cellSize));

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                var key = new Vector2I(cell.X + dx, cell.Y + dy);
                if (!occupancy.TryGetValue(key, out var list))
                    continue;

                foreach (var q in list)
                {
                    if (p.DistanceSquaredTo(q) < minDistSq)
                        return false;
                }
            }
        }

        if (!occupancy.TryGetValue(cell, out var own))
        {
            own = new List<Vector2>();
            occupancy[cell] = own;
        }

        own.Add(p);
        return true;
    }

    private PropEntry WeightedPick(List<PropEntry> props)
    {
        int total = 0;
        foreach (var p in props)
            total += Mathf.Max(0, p.Weight);

        if (total <= 0)
            return props.Count > 0 ? props[_rng.RandiRange(0, props.Count - 1)] : null;

        int roll = _rng.RandiRange(1, total);
        int acc = 0;
        foreach (var p in props)
        {
            acc += Mathf.Max(0, p.Weight);
            if (roll <= acc)
                return p;
        }

        return props[props.Count - 1];
    }

    /// <summary>Loads a PackedScene and returns its first MeshInstance3D's mesh, cached per path. Null = not a single-mesh prop (use batch:false for those).</summary>
    private Mesh ExtractMesh(string scenePath)
    {
        if (_propMeshCache.TryGetValue(scenePath, out var cached))
            return cached;

        Mesh result = null;
        var ps = GD.Load<PackedScene>(scenePath);
        if (ps != null)
        {
            var inst = ps.Instantiate();
            result = FindFirstMesh(inst);
            inst.Free();
            if (result == null)
                GD.PushWarning($"[Tileset] No MeshInstance3D found in '{scenePath}'; set \"batch\": false for this prop.");
        }
        else
        {
            GD.PushWarning($"[Tileset] Could not load prop scene '{scenePath}'.");
        }

        _propMeshCache[scenePath] = result;
        return result;
    }

    private static Mesh FindFirstMesh(Node n)
    {
        if (n is MeshInstance3D mi && mi.Mesh != null)
            return mi.Mesh;

        foreach (var c in n.GetChildren())
        {
            var m = FindFirstMesh(c);
            if (m != null)
                return m;
        }

        return null;
    }
}
