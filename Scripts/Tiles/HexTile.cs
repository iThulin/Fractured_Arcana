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

        var staticArea = GetNode<Area3D>("Area3D");
        staticArea.Connect("input_event", new Callable(this, nameof(OnAreaInputEvent)));
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
        GD.Print($"Set Coords: {q}, {r}");
        coordLabel.Text = $"({q}, {r})";
    }

    private void OnAreaInputEvent(Node camera, InputEvent @event, Vector3 position, Vector3 normal, int shapeIdx)
    {
        //GD.Print("Funct trigger");
        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.Left)
        {
            
            GD.Print("if @event");
            if (DragPayloadManager.IsDragging)
            {
                GD.Print("if dragging");

                var card = DragPayloadManager.DraggedCard;
                bool isTop = DragPayloadManager.IsTopHalf;

                if (card != null)
                {
                    GD.Print($"Card dropped on tile at {GlobalPosition} — Playing {(isTop ? "TOP" : "BOTTOM")} spell.");

                    if (isTop)
                        card.EmitSignal(CardUi.SignalName.TopCardSelected, card.TopCardData);
                    else
                        card.EmitSignal(CardUi.SignalName.BottomCardSelected, card.BottomCardData);

                    DragPayloadManager.IsDragging = false; // reset here too
                }
            }
        }
    }
} 