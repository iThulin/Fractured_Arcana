using Godot;
using System.Collections.Generic;

// ============================================================
// BuildingMeshLibrary.cs
//
// Purpose:        Building meshes on campus tiles (2026-08-13).
//                 Convention-driven: one packed scene per building
//                 at res://Scenes/Campus/Buildings/{buildingId}.tscn
//                 whose root is a Node3D with child nodes named
//                 "T1", "T2", "T3": one mesh group per tier,
//                 grander as they rise. The library instantiates
//                 the scene, shows the highest Tn <= current tier,
//                 hides the rest, and applies footprint rotation.
//                 NO scene on disk → returns null and the tile
//                 keeps today's tint + label rendering, so buildings
//                 can gain meshes one at a time.
// Layer:          System (campus rendering)
// Collaborators:  CampusGridManager.StampBuilding (sole caller),
//                 Scenes/Campus/Buildings/*.tscn (authored art).
//
// AUTHORING CONTRACT (Blender → glb → inherit into the tscn):
//   - Origin at the hex CENTRE, ground plane at Y = 0, +Y up.
//   - Scale: one hex has RADIUS 1.0 world unit (flat-to-flat
//     ≈ 1.732). Author to fit inside r ≈ 0.9 so neighbours
//     never kiss. The city-grounds 1/3 scale is inherited from
//     the grid transform; author at FULL combat-tile scale.
//   - Rotation 0 faces +Z; footprint rotation turns the whole
//     scene in 60° steps around Y.
//   - Multi-tile buildings: the scene sits on the ANCHOR hex;
//     sprawl across the footprint is the author's freedom.
// ============================================================

/// <summary>Loads and instantiates per-building mesh scenes by convention,
/// with per-tier visibility. Null-safe throughout: missing scenes and
/// missing tier nodes degrade to "less art", never to errors.</summary>
public static class BuildingMeshLibrary
{
    private const string ScenePathFmt = "res://Scenes/Campus/Buildings/{0}.tscn";

    /// <summary>Node name given to instances so re-stamps can find and
    /// replace them.</summary>
    public const string InstanceName = "BuildingMesh";

    private static readonly Dictionary<string, PackedScene> _cache = new();
    private static readonly HashSet<string> _known_missing = new();

    /// <summary>Instantiate the building's mesh scene at the given tier and
    /// footprint rotation, or null when no scene is authored yet.</summary>
    public static Node3D TryInstantiate(string buildingId, int tier, int rotation)
    {
        var scene = GetScene(buildingId);
        if (scene == null) return null;

        if (scene.Instantiate() is not Node3D root)
        {
            GD.PrintErr($"[BuildingMesh] {buildingId}.tscn root is not a Node3D. Ignored.");
            return null;
        }

        root.Name = InstanceName;
        root.RotationDegrees = new Vector3(0, -60f * rotation, 0);
        ApplyTier(root, tier);
        return root;
    }

    /// <summary>Show the highest "Tn" child with n &lt;= tier; hide the other
    /// tier groups. A scene missing the exact tier (e.g. only T1 authored so
    /// far) shows the best available: art debt reads as "not yet grander",
    /// never as an empty tile.</summary>
    public static void ApplyTier(Node3D root, int tier)
    {
        Node3D best = null;
        int bestN = 0;
        for (int n = 1; n <= 3; n++)
        {
            if (root.GetNodeOrNull<Node3D>($"T{n}") is not Node3D tn)
                continue;
            tn.Visible = false;
            if (n <= Mathf.Max(tier, 1) && n > bestN) { best = tn; bestN = n; }
        }
        if (best != null) best.Visible = true;
        else GD.PrintErr($"[BuildingMesh] '{root.Name}' scene has no T1/T2/T3 children.");
    }

    private static PackedScene GetScene(string buildingId)
    {
        if (string.IsNullOrEmpty(buildingId) || _known_missing.Contains(buildingId))
            return null;
        if (_cache.TryGetValue(buildingId, out var cached))
            return cached;

        string path = string.Format(ScenePathFmt, buildingId);
        if (!ResourceLoader.Exists(path))
        {
            _known_missing.Add(buildingId);   // remember, don't re-probe every stamp
            return null;
        }
        var scene = ResourceLoader.Load<PackedScene>(path);
        if (scene != null) _cache[buildingId] = scene;
        return scene;
    }
}
