using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Props.cs  (partial of HexGridManager)
//
// Purpose:        Data-driven terrain prop scattering. Reads a tileset
//                 manifest (per-terrain weighted prop kit), rolls props
//                 per tile with density-scaled counts and seeded jitter,
//                 and batches repeated single-mesh props into one
//                 MultiMeshInstance3D each to keep draw calls sane.
//                 Falls back to the legacy SpawnTerrainProps grass-tuft
//                 behaviour when no manifest resolves.
// Layer:          System (generation)
// Collaborators:  TilesetManifest / TilesetRegistry (data),
//                 HexGridManager.cs (Tiles, _rng, PropParent, DensityPreset)
// ============================================================

public partial class HexGridManager : Node3D
{
    /// <summary>Tileset manifest id from Data/Tilesets. Empty or not-found = legacy grass-tuft scatter.</summary>
    [Export] public string TilesetId = "default";

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

        Node parent = PropParent ?? this;
        var batched = new Dictionary<string, List<Transform3D>>();

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null || tile.IsBlocked)
                continue;

            if (!manifest.Terrains.TryGetValue(tile.TerrainType, out var set) || set.Props.Count == 0)
                continue;

            if (_rng.Randf() > set.Chance)
                continue;

            int count = Mathf.RoundToInt(_rng.RandiRange(set.CountMin, set.CountMax) * densityScalar);
            Vector3 basePos = tile.TileView.GlobalPosition;

            for (int i = 0; i < count; i++)
            {
                PropEntry prop = WeightedPick(set.Props);
                if (prop == null || string.IsNullOrEmpty(prop.ScenePath))
                    continue;

                Vector3 offset = new Vector3(
                    _rng.RandfRange(-prop.Jitter, prop.Jitter),
                    prop.YOffset,
                    _rng.RandfRange(-prop.Jitter, prop.Jitter));
                Vector3 worldPos = basePos + offset;

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
