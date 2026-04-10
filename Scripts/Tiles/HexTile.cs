using Godot;
using System;

public partial class HexTile : Node3D
{
    [Export] public Color HoverColor = new Color(1.0f, 0.9f, 0.4f);
    [Export] public bool ShowDebugInfo = true;

    private MeshInstance3D meshInstance;
    private StandardMaterial3D material;
    private Label3D coordLabel;
    private Color baseColor;

    public Vector2I Axial { get; set; }

    private bool deploymentHighlighted = false;
    private Color deploymentColor = new Color(0.2f, 1.0f, 0.2f, 1f);

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
    }

    private void OnMouseEntered()
    {
        if (material == null)
            return;

        Color c = deploymentHighlighted
            ? baseColor.Lerp(deploymentColor, 0.45f).Lerp(HoverColor, 0.5f)
            : HoverColor;

        material.AlbedoColor = c;
    }

    private void OnMouseExited()
    {
        RefreshVisualState();
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

        GD.Print($"SetHeight on tile {Axial}: height={height}, new pos={Position}");
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

    public void RefreshVisualState()
    {
        if (material == null)
            return;

        if (deploymentHighlighted)
            material.AlbedoColor = baseColor.Lerp(deploymentColor, 0.45f);
        else
            material.AlbedoColor = baseColor;
    }

} 