using Godot;
using System;

public partial class HexTile : Node3D
{
    [Export] public float Radius = 1f;
    [Export] public Color HoverColor = new Color(1.0f, 0.9f, 0.4f);

    private MeshInstance3D meshInstance;
    private StandardMaterial3D material;
    private Label3D coordLabel;
    private Color baseColor;

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

        GD.Print($"Hovering tile at: {GlobalPosition}");
    }

    private void OnMouseExited()
    {
        if (material != null)
            material.AlbedoColor = baseColor;
    }

    public void SetCoordinatesLabel(int q, int r)
    {
        //GD.Print($"Set Coords: {q}, {r}");
        coordLabel.Text = $"({q}, {r})";
    }


} 