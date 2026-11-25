using Godot;

// ============================================================
// HexGridManager.Surround.cs  (partial of HexGridManager)
//
// Purpose:        The wide surround. Instead of a thin ring of out-of-bounds
//                 tiles and then a coarse far field, the ground around the
//                 board is generated the way the board is, many rings deep,
//                 at the board's own detail near the edge. It is drawn as ONE
//                 merged mesh cut from the continuous ground surface, so it
//                 joins the board without a seam and costs one draw call.
// Layer:          System (combat terrain, visuals only, no rules)
// Collaborators:  HexGridManager.Vista.cs (generates the out-of-bounds tile
//                 data ring by ring and calls BuildSurroundMesh),
//                 GroundSurface.cs (AppendTile, SubdivFor), SurroundTile.cs
//                 (the node each surround tile gets), terrain_splat.gdshader
// See:            docs/wide_surround_v1.md
//
// Only with the continuous ground. Interiors and hard-ground maps (the castle
// deck) keep the old thin ring of full tiles.
// ============================================================

public partial class HexGridManager : Node3D
{
    [ExportGroup("Wide Surround")]

    /// <summary>Generate the ground around the board many rings deep and draw it as one mesh. Off = the old thin ring of full tiles (Vista Ring settings).</summary>
    [Export] public bool EnableWideSurround = true;

    /// <summary>How many rings of out-of-bounds ground to generate. 14 fills the camera at normal zoom. Each ring adds build time.</summary>
    [Export(PropertyHint.Range, "2,24,1")] public int SurroundRingDepth = 14;

    /// <summary>Rings nearest the board drawn at the board's own mesh detail. Rings beyond use half, which is hard to tell apart at that distance and is a quarter of the triangles.</summary>
    [Export(PropertyHint.Range, "0,24,1")] public int SurroundFineRings = 3;

    /// <summary>Grass, rock, flower and tree density on the outermost ring, as a fraction of the board's. The ring beside the board is always full density.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float SurroundOuterDensity = 0.05f;

    /// <summary>How quickly density falls from full to SurroundOuterDensity. 1 = evenly across all rings. 2 = reaches the outer value halfway out.</summary>
    [Export(PropertyHint.Range, "0.5,6,0.1")] public float SurroundDensityFalloff = 3.5f;

    /// <summary>
    /// How far thinned out-of-bounds grass is widened to keep the ground covered. 0 = tufts
    /// keep their size, so thinner grass looks sparse. 1 = each tuft grows to fully make up
    /// for the missing ones. Wider tufts cost nothing: the saving from drawing fewer stays.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float SurroundGrassFill = 0.75f;

    /// <summary>Width multiplier for a grass tuft on ground at this scatter density (1 on the board).</summary>
    private float SurroundTuftScale(float scatterDensity)
    {
        if (!SurroundActive || scatterDensity >= 0.999f)
            return 1f;

        // Covering the same ground with a fraction d of the tufts needs each about
        // 1 / sqrt(d) times wider. Capped so no tuft becomes a bush.
        float full = Mathf.Min(1f / Mathf.Sqrt(Mathf.Max(scatterDensity, 0.05f)), 2.4f);
        return Mathf.Lerp(1f, full, SurroundGrassFill);
    }

    /// <summary>True when this map uses the wide surround.</summary>
    public bool SurroundActive => EnableWideSurround && EnableVistaRing && ContinuousGroundActive;

    /// <summary>Rings of out-of-bounds tiles on this map, whichever way they are built.</summary>
    public int ActiveVistaDepth => SurroundActive ? SurroundRingDepth : VistaRingDepth;

    /// <summary>
    /// Scatter density for an out-of-bounds tile. Wide surround: full beside the board,
    /// easing down to SurroundOuterDensity. Old thin ring: the flat VistaScatterDensity.
    /// </summary>
    private float VistaDensityFor(TileData tile)
    {
        if (!SurroundActive)
            return VistaScatterDensity;

        float t = Mathf.Clamp(Mathf.Max(tile.VistaHorizon, 0f) * SurroundDensityFalloff, 0f, 1f);
        t = t * t * (3f - 2f * t);
        return Mathf.Lerp(1f, SurroundOuterDensity, t);
    }

    /// <summary>
    /// Draws every out-of-bounds tile as one mesh. Each tile contributes its patch of
    /// the continuous ground in world space. Runs after the board's heights and the
    /// splat template exist (the same point the old per-tile out-of-bounds meshes were built).
    /// </summary>
    private void BuildSurroundMesh()
    {
        var template = UseTerrainTextures ? GetTerrainMaterialTemplate() : null;
        bool splat = template != null;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (splat)
            st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);

        int count = 0;
        foreach (TileData tile in VistaTiles.Values)
        {
            if (tile.TileView == null)
                continue;

            // The stand-in node has no height logic of its own: place it at the tile top,
            // where the scatters and the water read it.
            Vector3 flat = AxialToWorld(tile.Axial);
            tile.TileView.Position = new Vector3(flat.X, tile.Height * HexTile.HeightStep, flat.Z);

            GroundSurface.AppendTile(st, this, tile, _lastWorldFloor, splat, worldSpace: true);
            count++;
        }

        if (count == 0)
            return;

        st.GenerateTangents();

        Material material = template;
        if (material == null)
        {
            material = new StandardMaterial3D
            {
                AlbedoColor = Colors.White,
                VertexColorUseAsAlbedo = true,
                VertexColorIsSrgb = true,
                Roughness = 0.85f
            };
        }

        var ground = new MeshInstance3D
        {
            Name = "SurroundGround",
            Mesh = st.Commit(),
            // The SHARED template, never a copy, so later pushes (horizon colour,
            // waterline, snow) reach it, as they reach the far field.
            MaterialOverride = material,
            // Receives shadows, casts none. Casting would redraw this whole mesh once
            // per shadow cascade (four times a frame) to shade ground nobody stands on.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        // In the out-of-bounds tile group, so ClearVistaTiles frees it with the tiles.
        ground.AddToGroup(VistaTileGroup);
        AddChild(ground);
        ground.GlobalTransform = Transform3D.Identity;   // vertices are world-space

        // Grid lines are drawn per tile in the shader and switch off on this flag. The
        // dimming itself comes per vertex from the mesh (GroundSurface.VistaFromNear).
        ground.SetInstanceShaderParameter("vista_fade", 1.0f);

        GD.Print($"[Surround] {count} out-of-bounds tile(s) in {SurroundRingDepth} ring(s), one mesh.");
    }
}
