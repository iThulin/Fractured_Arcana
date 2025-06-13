using Godot;
using System;

public partial class CardDropHandler : Node3D
{
    private Camera3D camera;

    public override void _Ready()
    {
        var cameraNode = GetViewport().GetCamera3D();
        if (cameraNode != null)
            camera = cameraNode;
        else
            GD.PrintErr("Camera3D not found for CardDropHandler!");
    }

    public void TryDropCardOnTile()
    {
        if (!DragPayloadManager.IsDragging || camera == null) return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState.IntersectRay(new PhysicsRayQueryParameters3D
        {
            From = from,
            To = to,
            CollisionMask = 1 // Ensure this matches your tile's collision layer
        });

        GD.Print($"IsDragging: {DragPayloadManager.IsDragging}");

        if (result.TryGetValue("collider", out var colliderVar))
        {
            Node colliderNode = colliderVar.As<Node>();
            if (colliderNode != null)
            {
                var hexTile = GetParentHexTile(colliderNode);
                if (hexTile != null)
                {
                    var card = DragPayloadManager.DraggedCard;
                    bool isTop = DragPayloadManager.IsTopHalf;

                    if (card != null)
                    {
                        GD.Print($"Card dropped on tile at {hexTile.GlobalPosition} — Playing {(isTop ? card.TopCardData.CardName : card.BottomCardData.CardName)}");
                        if (isTop)
                            card.EmitSignal(CardUi.SignalName.TopCardSelected, card.TopCardData);
                        else
                            card.EmitSignal(CardUi.SignalName.BottomCardSelected, card.BottomCardData);

                        DragPayloadManager.IsDragging = false;
                    }
                }
            }
        }
    }

    private HexTile GetParentHexTile(Node node)
    {
        while (node != null && node is not HexTile)
        {
            node = node.GetParent();
        }
        return node as HexTile;
    }
}
