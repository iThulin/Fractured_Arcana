using Godot;
using System;

public partial class HexTile : Node3D
{
    // Configurable properties
    [Export] public Color HoverColor = new Color(1.0f, 0.9f, 0.4f);
    [Export] public bool ShowDebugInfo = true;

    // Optional override; if not set, the default overlay scene is loaded by path.
    [Export] public PackedScene ImbuementOverlayScene;

    private const string DefaultOverlayScenePath = "res://Scenes/Combat/ImbuementOverlay.tscn";

    // Cached nodes and materials
    private MeshInstance3D meshInstance;
    private StandardMaterial3D material;
    private Label3D coordLabel;
    private Color baseColor;

    private ImbuementOverlay imbuementOverlay;

    public Vector2I Axial { get; set; }

    // Highlighting states
    private bool _isHighlighted = false;
    private Color _preHighlightColor;
    private bool deploymentHighlighted = false;
    private Color deploymentColor = new Color(0.2f, 1.0f, 0.2f, 1f);
    private bool moveHighlighted = false;
    private Color moveHighlightColor = new Color(0.2f, 0.6f, 1.0f, 1f);
    private bool targetHighlighted = false;
    private Color targetHighlightColor = new Color(1.0f, 0.4f, 0.4f, 1f); 
    private bool rangeHighlighted = false;
    private bool rangeBorderHighlighted = false;
    private Color rangeColor = new Color(1.0f, 0.7f, 0.3f, 1f);
    private Color rangeBorderColor = new Color(1.0f, 0.5f, 0.1f, 1f);

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
        // Restore to either the highlight override or base visual state
        if (_isHighlighted)
        {
            if (rangeBorderHighlighted)
                material.AlbedoColor = rangeBorderColor;
            else if (rangeHighlighted)
                material.AlbedoColor = rangeColor;
            else if (targetHighlighted)
                material.AlbedoColor = targetHighlightColor;
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

    public void SetMoveHighlight(bool enabled)
    {
        moveHighlighted = enabled;
        RefreshVisualState();
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
            material.AlbedoColor = targetHighlightColor;
    }

    public void SetRangeHighlight(bool interior, bool border)
    {
        rangeHighlighted = interior;
        rangeBorderHighlighted = border;

        if ((interior || border) && !_isHighlighted)
        {
            // Save current color before overriding
            _preHighlightColor = material.AlbedoColor;
            _isHighlighted = true;
        }
        else if (!interior && !border && _isHighlighted)
        {
            // Restore saved color
            _isHighlighted = false;
            material.AlbedoColor = _preHighlightColor;
            return;
        }

        if (material == null) return;

        if (border)
            material.AlbedoColor = rangeBorderColor;
        else if (interior)
            material.AlbedoColor = rangeColor;
    }

    public void RefreshVisualState()
    {
        if (material == null) return;

        // If highlighted, don't lerp — the override color is already set directly
        if (_isHighlighted) return;

        Color finalColor = baseColor;
        if (deploymentHighlighted) finalColor = finalColor.Lerp(deploymentColor, 0.45f);
        if (moveHighlighted) finalColor = finalColor.Lerp(moveHighlightColor, 0.45f);
        material.AlbedoColor = finalColor;
        // Note: element visuals are NOT applied here.
        // They live on the ImbuementOverlay child node and update
        // independently via SetElement().
    }

}
