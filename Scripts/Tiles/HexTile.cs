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
        if (material != null)
            material.AlbedoColor = HoverColor;

        //GD.Print($"Hovering tile at: {GlobalPosition}");
    }

    private void OnMouseExited()
    {
        if (material != null)
            material.AlbedoColor = baseColor;
    }

    public void SetCoordinatesLabel(int q, int r)
    {
        Axial = new Vector2I(q, r);
        coordLabel.Text = $"({q}, {r})";
    }

        public void SetBaseColor(Color color)
    {
        baseColor = color;
        if (material != null)
            material.AlbedoColor = color;
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
            $"Block: {blocked}";
    }

} 