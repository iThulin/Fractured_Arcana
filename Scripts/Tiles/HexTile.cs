using Godot;
using System;

public partial class HexTile : Node3D
{
    // Configurable properties
    [Export] public Color HoverColor = UITheme.TileHover;
    [Export] public bool ShowDebugInfo = true;

    // Optional override; if not set, the default overlay scene is loaded by path.
    [Export] public PackedScene ImbuementOverlayScene;

    private const string DefaultOverlayScenePath = "res://Scenes/Combat/ImbuementOverlay.tscn";

    // Cached nodes and materials
    private MeshInstance3D meshInstance;
    private StandardMaterial3D material;
    private Label3D coordLabel;
    private Label3D _glyphLabel;
    private Color baseColor;

    private ImbuementOverlay imbuementOverlay;

    public Vector2I Axial { get; set; }

    // Highlighting states
    private bool _isHighlighted = false;
    private Color _preHighlightColor;
    private bool deploymentHighlighted = false;
    private bool moveHighlighted = false;
    private bool targetHighlighted = false;
    private bool rangeHighlighted = false;
    private bool rangeBorderHighlighted = false;
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

    public void SetHeight(int height)
    {
        var pos = Position;
        pos.Y = height * 0.5f; // scale factor (tune this)
        Position = pos;

        //GD.Print($"SetHeight on tile {Axial}: height={height}, new pos={Position}");
    }

    public void SetCoordinatesLabel(int q, int r)
    {
        Axial = new Vector2I(q, r);
        coordLabel.Text = $"({q}, {r})";
    }

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

    public void ClearGlyph()
    {
        if (_glyphLabel != null)
            _glyphLabel.Visible = false;
    }

    public TileElementType CurrentElement =>
        imbuementOverlay?.CurrentElement ?? TileElementType.None;

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

    public void SetDeploymentHighlight(bool enabled)
    {
        deploymentHighlighted = enabled;
        RefreshVisualState();
    }

    public void SetMoveHighlightColored(Color color)
    {
        // Apply the color directly to whatever mesh/material drives move highlight
        // Use AlbedoColor override — same pattern as existing tile highlighting
        if (_moveMesh != null)
            _moveMesh.GetSurfaceOverrideMaterial(0)?.Set("albedo_color", color);

        SetMoveHighlight(true); // keep the existing visibility logic
    }

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

    public void SetDragHoverHighlight(bool on)
    {
        if (material == null) return;
        if (on)
            material.AlbedoColor = DragHoverColor;
        else
            RefreshVisualState(); // restore base/range/target state
    }

    public void RefreshVisualState()
    {
        if (material == null) return;
        if (_isHighlighted) return;

        Color finalColor = baseColor;
        if (deploymentHighlighted) finalColor = finalColor.Lerp(UITheme.TileDeployHighlight, 0.45f);
        if (moveHighlighted) finalColor = finalColor.Lerp(UITheme.TileMoveHighlight, 0.45f);
        material.AlbedoColor = finalColor;
    }

}
