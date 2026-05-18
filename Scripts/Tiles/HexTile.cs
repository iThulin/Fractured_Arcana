using Godot;
using System;

// ============================================================
// HexTile.cs
//
// Purpose:        Visual Node3D for one hex tile on the combat
//                 grid. Renders the mesh, handles hover/highlight
//                 colour blending, manages the imbuement overlay
//                 and glyph indicator, and shows the debug coord
//                 label. Pure visual layer — game state lives on
//                 the paired TileData.
// Layer:          Tiles
// Collaborators:  TileData.cs (1:1 data sibling, via TileView),
//                 ImbuementOverlay.cs (child scene),
//                 UITheme.cs (highlight colours),
//                 HexGridManager.cs (instantiates and positions tiles)
// See:            README §8 — CallDeferred rules apply to glyph
//                 child addition (see ShowGlyph)
// ============================================================

/// <summary>
/// Visual Node3D for one hex tile. Handles mesh duplication for per-tile material,
/// hover colour blending, the layered highlight state machine
/// (deployment / movement / range / target / drag), and ownership of the
/// <see cref="ImbuementOverlay"/> child plus the optional glyph label. All highlight
/// colours come from <see cref="UITheme"/>.
/// </summary>
public partial class HexTile : Node3D
{
    /// <summary>Colour blended onto the tile when the mouse is over it. Defaults to the central UITheme value but is overridable in the inspector for special tiles.</summary>
    [Export] public Color HoverColor = UITheme.TileHover;

    /// <summary>When true, the coord/terrain label is shown in 3D space above the tile (debug only).</summary>
    [Export] public bool ShowDebugInfo = true;

    /// <summary>Optional override for the imbuement overlay scene. If unset, the default at <see cref="DefaultOverlayScenePath"/> is loaded.</summary>
    [Export] public PackedScene ImbuementOverlayScene;

    private const string DefaultOverlayScenePath = "res://Scenes/Combat/ImbuementOverlay.tscn";

    // Cached nodes and materials
    private MeshInstance3D meshInstance;
    private StandardMaterial3D material;
    private Label3D coordLabel;
    private Label3D _glyphLabel;
    private Color baseColor;

    private ImbuementOverlay imbuementOverlay;

    /// <summary>Axial (q, r) coordinate identifying this tile's grid position.</summary>
    public Vector2I Axial { get; set; }

    // Highlighting states
    private bool _isHighlighted = false;
    private Color _preHighlightColor;
    private bool deploymentHighlighted = false;
    private bool moveHighlighted = false;
    private Color _moveHighlightColor = UITheme.TileMoveHighlight; // default
    private bool targetHighlighted = false;
    private bool rangeHighlighted = false;
    private bool rangeBorderHighlighted = false;
    /// <summary>Colour used when a draggable card is hovered over this tile during targeting.</summary>
    [Export] public Color DragHoverColor = UITheme.TileDragHover;

    public override void _Ready()
    {
        meshInstance = GetNode<MeshInstance3D>("HexMesh");
        coordLabel = GetNode<Label3D>("CoordLabel");

        // Get the material and cache base color
        var sharedMaterial = meshInstance.GetActiveMaterial(0) as StandardMaterial3D;
        if (sharedMaterial != null)
        {
            material = (StandardMaterial3D)sharedMaterial.Duplicate();
            meshInstance.SetSurfaceOverrideMaterial(0, material);
            baseColor = material.AlbedoColor;
        }

        var area = GetNode<StaticBody3D>("StaticBody3D");
        area.MouseEntered += OnMouseEntered;
        area.MouseExited += OnMouseExited;

        EnsureImbuementOverlay();
    }

    private void EnsureImbuementOverlay()
    {
        // Already a child? Use it.
        imbuementOverlay = GetNodeOrNull<ImbuementOverlay>("ImbuementOverlay");
        if (imbuementOverlay != null)
            return;

        var scene = ImbuementOverlayScene
                    ?? GD.Load<PackedScene>(DefaultOverlayScenePath);
        if (scene == null)
        {
            GD.PushWarning($"HexTile {Axial}: ImbuementOverlay scene not found.");
            return;
        }

        imbuementOverlay = scene.Instantiate<ImbuementOverlay>();
        imbuementOverlay.Name = "ImbuementOverlay";
        AddChild(imbuementOverlay);
    }

    private void OnMouseEntered()
    {
        if (material == null) return;
        // Blend hover on top of current color (highlight override or base)
        Color c = material.AlbedoColor;
        c = c.Lerp(HoverColor, 0.5f);
        material.AlbedoColor = c;
    }

    private void OnMouseExited()
    {
        if (_isHighlighted)
        {
            if (rangeBorderHighlighted)
                material.AlbedoColor = UITheme.TileRangeBorder;
            else if (rangeHighlighted)
                material.AlbedoColor = UITheme.TileRangeInterior;
            else if (targetHighlighted)
                material.AlbedoColor = UITheme.TileTargetHighlight;
        }
        else
        {
            RefreshVisualState();
        }
    }

    /// <summary>Replaces the tile's material with a per-tile duplicate (so AlbedoColor changes don't bleed to siblings). Pass a StandardMaterial3D for the standard hover/highlight path; other material types disable the colour blending features.</summary>
    public void SetMaterial(Material newMaterial)
    {
        if (meshInstance == null || newMaterial == null)
            return;

        if (newMaterial is StandardMaterial3D stdMat)
        {
            material = (StandardMaterial3D)stdMat.Duplicate();
            meshInstance.SetSurfaceOverrideMaterial(0, material);
            baseColor = material.AlbedoColor;
        }
        else
        {
            meshInstance.SetSurfaceOverrideMaterial(0, newMaterial);
            material = null;
        }
    }

    /// <summary>Lifts the tile vertically by an integer step count. The Y offset multiplier (currently 0.5 units per step) is tuned visually.</summary>
    public void SetHeight(int height)
    {
        var pos = Position;
        pos.Y = height * 0.5f; // scale factor (tune this)
        Position = pos;

        //GD.Print($"SetHeight on tile {Axial}: height={height}, new pos={Position}");
    }

    /// <summary>Sets both the <see cref="Axial"/> coordinate and the visible debug label.</summary>
    public void SetCoordinatesLabel(int q, int r)
    {
        Axial = new Vector2I(q, r);
        coordLabel.Text = $"({q}, {r})";
    }

    /// <summary>Sets the tile's resting AlbedoColor (under no hover/highlight). Triggers an immediate visual refresh.</summary>
    public void SetBaseColor(Color color)
    {
        baseColor = color;
        RefreshVisualState();
    }

    /// <summary>
    /// Sets the imbuement element shown above this tile. Pass
    /// <see cref="TileElementType.None"/> to hide the overlay.
    /// </summary>
    public void SetElement(TileElementType element)
    {
        if (imbuementOverlay == null)
            EnsureImbuementOverlay();

        imbuementOverlay?.SetElement(element);
    }

    /// <summary>Lazily creates a billboarded glyph label above the tile and makes it visible. Uses <c>CallDeferred("add_child", ...)</c> to comply with the Godot 4.6 cross-platform safety rule (see README §8).</summary>
    public void ShowGlyph()
    {
        if (_glyphLabel == null)
        {
            _glyphLabel = new Label3D();
            _glyphLabel.Text = "✦";
            _glyphLabel.FontSize = UITheme.Label3DGlyph;
            _glyphLabel.Modulate = UITheme.TileGlyph;
            _glyphLabel.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            _glyphLabel.Position = new Vector3(0, 0.6f, 0);
            _glyphLabel.Name = "GlyphIndicator";
            CallDeferred("add_child", _glyphLabel);
        }
        else
        {
            _glyphLabel.Visible = true;
        }
    }

    /// <summary>Hides the glyph indicator without destroying it. Cheap to re-show via <see cref="ShowGlyph"/>.</summary>
    public void ClearGlyph()
    {
        if (_glyphLabel != null)
            _glyphLabel.Visible = false;
    }

    /// <summary>Current elemental imbuement displayed by the overlay child, or <see cref="TileElementType.None"/> if no overlay is present.</summary>
    public TileElementType CurrentElement =>
        imbuementOverlay?.CurrentElement ?? TileElementType.None;

    /// <summary>Rebuilds the debug coord label text from the paired <see cref="TileData"/>. No-op when <see cref="ShowDebugInfo"/> is false.</summary>
    public void RefreshLabel(TileData tileData)
    {
        if (coordLabel == null || tileData == null)
            return;

        if (!ShowDebugInfo)
        {
            coordLabel.Text = "";
            return;
        }

        string terrain = tileData.TerrainType.ToString();
        string element = tileData.ElementType.ToString();

        if (tileData.ElementType == TileElementType.None)
            element = "-";

        string blocked = tileData.IsBlocked ? "Yes" : "No";

        coordLabel.Text =
            $"({tileData.Axial.X}, {tileData.Axial.Y})\n" +
            $"Type: {terrain}\n" +
            $"Imbue: {element}\n" +
            $"Block: {blocked}\n" +
            $"H: {tileData.Height}";
    }

    /// <summary>Toggles the soft deployment-zone tint blended onto the resting colour.</summary>
    public void SetDeploymentHighlight(bool enabled)
    {
        deploymentHighlighted = enabled;
        RefreshVisualState();
    }

    /// <summary>Toggles the movement highlight. When enabled, the movement highlight colour is blended on top of the base/deployment colour. Use <see cref="SetMoveHighlightColored"/> to set a custom colour for this highlight (e.g. to distinguish player vs ally vs dash reachability).</summary>
    public void SetMoveHighlight(bool enabled)
    {
        if (!enabled)
            _moveHighlightColor = UITheme.TileMoveHighlight; // reset to default on clear
        moveHighlighted = enabled;
        RefreshVisualState();
    }

    /// <summary>Sets a custom colour for the movement highlight overlay, then enables it. Used to distinguish player vs ally vs reachable-via-dash highlights at the gameplay level.</summary>
    public void SetMoveHighlightColored(Color color)
    {
        if (material == null) return;
        _moveHighlightColor = color;
        moveHighlighted = true;
        RefreshVisualState();
    }

    /// <summary>Toggles the targeting highlight (used when a card is being aimed at this tile). Saves and restores the prior colour so the highlight is non-destructive.</summary>
    public void SetTargetHighlight(bool enabled)
    {
        targetHighlighted = enabled;

        if (enabled && !_isHighlighted)
        {
            _preHighlightColor = material.AlbedoColor;
            _isHighlighted = true;
        }
        else if (!enabled && _isHighlighted)
        {
            _isHighlighted = false;
            material.AlbedoColor = _preHighlightColor;
            return;
        }

        if (enabled)
            material.AlbedoColor = UITheme.TileTargetHighlight;
    }

    /// <summary>Toggles the range-preview highlight. Pass <paramref name="border"/> true for the edge of the area, <paramref name="interior"/> true for tiles inside the area. Both false clears the highlight.</summary>
    public void SetRangeHighlight(bool interior, bool border)
    {
        rangeHighlighted = interior;
        rangeBorderHighlighted = border;

        if ((interior || border) && !_isHighlighted)
        {
            _preHighlightColor = material.AlbedoColor;
            _isHighlighted = true;
        }
        else if (!interior && !border && _isHighlighted)
        {
            _isHighlighted = false;
            material.AlbedoColor = _preHighlightColor;
            return;
        }

        if (material == null) return;

        if (border)
            material.AlbedoColor = UITheme.TileRangeBorder;
        else if (interior)
            material.AlbedoColor = UITheme.TileRangeInterior;
    }

    /// <summary>Applies the drag-hover colour when a card is being dragged over this tile. Restores the prior state when <paramref name="on"/> is false.</summary>
    public void SetDragHoverHighlight(bool on)
    {
        if (material == null) return;
        if (on)
            material.AlbedoColor = DragHoverColor;
        else
            RefreshVisualState(); // restore base/range/target state
    }

    /// <summary>Recomputes the current AlbedoColor from the layered highlight flags (base → deployment → move). No-op while a target/range highlight is active — those override.</summary>
    public void RefreshVisualState()
    {
        if (material == null) return;
        if (_isHighlighted) return;

        Color finalColor = baseColor;
        if (deploymentHighlighted) finalColor = finalColor.Lerp(UITheme.TileDeployHighlight, 0.45f);
        if (moveHighlighted) finalColor = finalColor.Lerp(_moveHighlightColor, 0.45f);
        material.AlbedoColor = finalColor;
    }

}
