using Godot;

public partial class CardDropHandler : Node3D
{
    private Camera3D camera;

    [Signal]
    public delegate void CardDroppedOnTileEventHandler(CardUi cardUi, bool isTop, HexTile tile);

    public override void _Ready()
    {
        camera = GetViewport().GetCamera3D();
        if (camera == null)
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
            CollisionMask = 1
        });

        if (!result.TryGetValue("collider", out var colliderVar)) return;

        Node colliderNode = colliderVar.As<Node>();
        if (colliderNode == null) return;

        var hexTile = GetParentHexTile(colliderNode);
        if (hexTile == null) return;

        var cardUi = DragPayloadManager.DraggedCard;
        bool isTop = DragPayloadManager.IsTopHalf;
        if (cardUi == null) return;

        var halfName = (isTop ? cardUi.TopHalf : cardUi.BottomHalf)?.Name ?? "(null half)";
        GD.Print($"Card dropped on tile {hexTile.Axial} — Playing {halfName}");

        EmitSignal(SignalName.CardDroppedOnTile, cardUi, isTop, hexTile);

        DragPayloadManager.IsDragging = false;
    }

    private HexTile GetParentHexTile(Node node)
    {
        while (node != null && node is not HexTile)
            node = node.GetParent();
        return node as HexTile;
    }
}