using Godot;

// ============================================================
// HexGridManager.Ground.cs  (partial of HexGridManager)
//
// Purpose:        Switch and tuning for the continuous ground (GroundSurface.cs).
//                 When active, every tile mesh is a patch of one smooth
//                 surface: no seams between tiles and no vertical wall
//                 faces at height drops. When off, the old per-tile builder
//                 (fan, bridge band, cliff walls) runs unchanged.
// Layer:          System (combat terrain, visuals only, no rules)
// Collaborators:  GroundSurface.cs, HexMeshBuilder.cs (hands over at the top
//                 of Build and SampleSurfaceWorldY)
// See:            docs/continuous_ground_v1.md
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Continuous Ground")]

    /// <summary>Build the ground as one continuous surface. Off = the old per-tile mesh with cliff walls.</summary>
    [Export] public bool UseContinuousGround = true;

    /// <summary>Rows of triangles from a tile's centre to its edge. Higher = smoother slopes and sharper cliffs, more vertices.</summary>
    [Export(PropertyHint.Range, "2,12,1")] public int GroundSubdiv = 6;

    /// <summary>
    /// How far a tile's pull on the ground reaches, as a fraction of the distance
    /// between tile centres. Higher = broader, softer slopes and a smaller level
    /// patch at each centre (the level patch is 1 minus this).
    /// </summary>
    [Export(PropertyHint.Range, "0.6,0.86,0.01")] public float GroundBlendRadius = 0.78f;

    /// <summary>Distance from a tile's centre (same units) inside which its pull is at full strength. Higher = slopes pushed toward the edges.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float GroundCoreRadius = 0.25f;

    /// <summary>
    /// How hard a drop above CliffHeightThreshold is squeezed into a steep face at
    /// the hex edge. 0 = cliffs are just long slopes. 5 = a clear rock face.
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.5")] public float GroundCliffSharpness = 5f;

    /// <summary>How tightly each terrain's texture keeps to its own tile. 1 = wide soft blends, 4 = tight to the hex edge.</summary>
    [Export(PropertyHint.Range, "1,6,0.25")] public float GroundTextureSharpness = 2.5f;

    /// <summary>
    /// True when this map's ground is the continuous surface. Interiors and recipes
    /// marked "hard_ground" (the castle deck) keep the old builder: their floors and
    /// steps are built things with hard edges.
    /// </summary>
    public bool ContinuousGroundActive =>
        UseBlendedTerrainMesh && UseContinuousGround && !InteriorActive && _activeRecipe?.HardGround != true;
}
