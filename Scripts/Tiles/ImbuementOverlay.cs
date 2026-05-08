using Godot;
using System.Collections.Generic;

public partial class ImbuementOverlay : Node3D
{
    [Export] public MeshInstance3D AuraMesh;
    [Export] public MeshInstance3D GlyphMesh;

    // How tall the aura column is (rises from tile top).
    [Export] public float AuraHeight = 0.9f;

    // How high above the tile top the glyph hovers, and how far it bobs.
    [Export] public float GlyphBaseHeight = 0.55f;
    [Export] public float GlyphBobAmount  = 0.08f;
    [Export] public float GlyphBobSpeed   = 1.4f;

    // If true (default), at startup we measure the parent HexTile's actual
    // mesh top and reposition the aura/glyph accordingly. Set false in the
    // inspector to use the scene's hardcoded transforms instead.
    [Export] public bool AutoFitToTile = true;

    // Cached after auto-fit: the local-space Y of the parent tile's top surface.
    private float _tileTopY = 0.0f;

    private ShaderMaterial _auraMaterial;
    private ShaderMaterial _glyphMaterial;
    private TileElementType _current = TileElementType.None;
    private float _timeOffset;

    // Element → tint color (used by both shaders).
    private static readonly Dictionary<TileElementType, Color> ElementTints = new()
    {
        { TileElementType.Fire,      new Color(1.00f, 0.45f, 0.10f, 1.0f) },
        { TileElementType.Frost,     new Color(0.65f, 0.90f, 1.00f, 1.0f) },
        { TileElementType.Lightning, new Color(0.90f, 0.80f, 1.00f, 1.0f) },
        { TileElementType.Earth,     new Color(0.75f, 0.55f, 0.25f, 1.0f) },
        { TileElementType.Water,     new Color(0.30f, 0.60f, 0.95f, 1.0f) },
        { TileElementType.Air,       new Color(0.85f, 0.95f, 0.85f, 1.0f) },
        { TileElementType.Arcane,    new Color(0.85f, 0.40f, 1.00f, 1.0f) },
        { TileElementType.Shadow,    new Color(0.50f, 0.20f, 0.55f, 1.0f) },
    };

    // Element → integer ID for the shader switch.
    private static readonly Dictionary<TileElementType, int> ElementIds = new()
    {
        { TileElementType.Fire,      0 },
        { TileElementType.Frost,     1 },
        { TileElementType.Lightning, 2 },
        { TileElementType.Earth,     3 },
        { TileElementType.Water,     4 },
        { TileElementType.Air,       5 },
        { TileElementType.Arcane,    6 },
        { TileElementType.Shadow,    7 },
    };

    public override void _Ready()
    {
        if (AuraMesh == null)  AuraMesh  = GetNodeOrNull<MeshInstance3D>("Aura");
        if (GlyphMesh == null) GlyphMesh = GetNodeOrNull<MeshInstance3D>("Glyph");

        // Duplicate shader materials so each tile can have its own tint/id.
        if (AuraMesh != null)
        {
            var src = AuraMesh.GetActiveMaterial(0) as ShaderMaterial;
            if (src != null)
            {
                _auraMaterial = (ShaderMaterial)src.Duplicate();
                AuraMesh.SetSurfaceOverrideMaterial(0, _auraMaterial);
            }
        }

        if (GlyphMesh != null)
        {
            var src = GlyphMesh.GetActiveMaterial(0) as ShaderMaterial;
            if (src != null)
            {
                _glyphMaterial = (ShaderMaterial)src.Duplicate();
                GlyphMesh.SetSurfaceOverrideMaterial(0, _glyphMaterial);
            }
        }

        // Slight per-tile time offset so adjacent tiles don't bob in lockstep.
        _timeOffset = (float)GD.RandRange(0.0, Mathf.Tau);

        if (AutoFitToTile)
            FitToParentTile();

        Visible = false;
    }

    /// <summary>
    /// Measures the parent HexTile's mesh AABB to find the tile's actual top
    /// surface, then positions the aura column to sit on it and the glyph
    /// to hover at its base height above it. This makes the overlay robust
    /// to changes in tile geometry (Hex_mesh.tres scale, blocker variants,
    /// height adjustments via SetHeight, etc).
    /// </summary>
    private void FitToParentTile()
    {
        var parent = GetParent();
        if (parent == null) return;

        var hexMesh = parent.GetNodeOrNull<MeshInstance3D>("HexMesh");
        if (hexMesh == null || hexMesh.Mesh == null) return;

        // AABB is in the mesh's local space; we need it in the HexTile's
        // local space (which is the same coordinate space as our own
        // Position, since we're a sibling of HexMesh under HexTile).
        var aabb = hexMesh.Mesh.GetAabb();

        // Apply the HexMesh node's transform to the AABB to get it in
        // HexTile-local space. We only care about the top Y.
        var t = hexMesh.Transform;
        // Top of AABB in mesh-local: aabb.Position.Y + aabb.Size.Y
        // Transformed by HexMesh's local transform: scale and offset Y.
        float meshTopLocal = aabb.Position.Y + aabb.Size.Y;
        // HexMesh's transform basis Y scale + origin Y:
        float scaleY = t.Basis.Y.Length();
        float offsetY = t.Origin.Y;
        _tileTopY = meshTopLocal * scaleY + offsetY;

        // Position the aura: cylinder origin = tile top + half its height,
        // so its bottom is flush with the tile top.
        if (AuraMesh != null)
        {
            var pos = AuraMesh.Position;
            pos.Y = _tileTopY + AuraHeight * 0.5f;
            AuraMesh.Position = pos;

            // Also rescale the cylinder Y to match our AuraHeight export
            // (the scene file has a fixed-height mesh; export-driven scale
            // lets you tune in the inspector).
            var s = AuraMesh.Scale;
            // The mesh as authored is height=0.9. We compute scale relative
            // to that author-time height so the export reads as world units.
            const float AuthoredHeight = 0.9f;
            s.Y = AuraHeight / AuthoredHeight;
            AuraMesh.Scale = s;
        }

        // Glyph base position (script also drives the bob in _Process).
        if (GlyphMesh != null)
        {
            var pos = GlyphMesh.Position;
            pos.Y = _tileTopY + GlyphBaseHeight;
            GlyphMesh.Position = pos;
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible || GlyphMesh == null) return;

        // Bob the glyph up and down for the floating-magic feel.
        float t = (float)Time.GetTicksMsec() / 1000f + _timeOffset;
        float baseY = AutoFitToTile ? _tileTopY + GlyphBaseHeight : GlyphMesh.Position.Y;
        // (When AutoFitToTile=false, we just use the scene's authored Y
        // and bob around it; for that case we don't have _tileTopY cached,
        // so we keep the current Y as the bob anchor.)

        var pos = GlyphMesh.Position;
        if (AutoFitToTile)
            pos.Y = _tileTopY + GlyphBaseHeight + Mathf.Sin(t * GlyphBobSpeed) * GlyphBobAmount;
        else
            pos.Y = baseY + Mathf.Sin(t * GlyphBobSpeed) * GlyphBobAmount;
        GlyphMesh.Position = pos;
    }

    public void SetElement(TileElementType element)
    {
        _current = element;

        if (element == TileElementType.None)
        {
            Visible = false;
            return;
        }

        Color tint = ElementTints.TryGetValue(element, out var c)
            ? c
            : new Color(1, 1, 1, 1);
        int id = ElementIds.TryGetValue(element, out var i) ? i : 0;

        _auraMaterial?.SetShaderParameter("tint_color", tint);
        _auraMaterial?.SetShaderParameter("element_id", id);

        _glyphMaterial?.SetShaderParameter("tint_color", tint);
        _glyphMaterial?.SetShaderParameter("element_id", id);

        Visible = true;
    }

    public TileElementType CurrentElement => _current;
}