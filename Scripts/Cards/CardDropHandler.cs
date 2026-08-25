using Godot;

// ============================================================
// CardDropHandler.cs
//
// Purpose:        Bridges the 2D card-drag UI with the 3D hex
//                 grid. Each frame raycasts under the mouse
//                 while a drag is active, tracks the hovered
//                 HexTile, and emits signals on drag-start,
//                 drag-end, and successful drop.
// Layer:          UI
// Collaborators:  CardUi.cs (the card being dragged),
//                 DragPayloadManager.cs (drag state singleton),
//                 HexTile.cs (hover highlight, axial coord),
//                 CombatManager.cs / RulesManager.cs (consumers
//                 of the CardDroppedOnTile signal)
// See:            README §3 (Architecture, input flow)
// ============================================================

/// <summary>3D-space drag/drop bridge. Watches <see cref="DragPayloadManager"/> for an active drag, raycasts under the mouse each frame to find the hovered <see cref="HexTile"/>, and emits signals consumers can subscribe to (drag-start, drag-end, drop-on-tile).</summary>
public partial class CardDropHandler : Node3D
{
    private Camera3D camera;

    /// <summary>The HexTile under the mouse cursor while a drag is active, or null when no tile is hovered.</summary>
    public HexTile CurrentHoveredTile { get; private set; }

    // Tracks last-frame drag state so we can detect transitions
    private bool _wasDragging = false;

    /// <summary>Emitted when the player drops a card half onto a valid HexTile. <paramref name="isTop"/> identifies which half is being played.</summary>
    [Signal]
    public delegate void CardDroppedOnTileEventHandler(CardUi cardUi, bool isTop, HexTile tile);

    /// <summary>Emitted on the frame a drag begins. Consumers typically light up valid drop tiles in response.</summary>
    [Signal]
    public delegate void CardDragStartedEventHandler(CardUi cardUi, bool isTop);

    /// <summary>Emitted when a drag ends without a successful drop (cancelled or out-of-bounds).</summary>
    [Signal]
    public delegate void CardDragEndedEventHandler();

    /// <summary>R22 damage preview: emitted whenever the hovered tile CHANGES
    /// during a drag (including to no-tile, where consumers receive null-equivalent
    /// via a separate DragHoverCleared). Lets CombatManager refresh the
    /// predicted-damage readout without polling.</summary>
    [Signal]
    public delegate void DragHoverChangedEventHandler(HexTile tile);

    /// <summary>R22: hovered tile became nothing (off-grid) during a drag.</summary>
    [Signal]
    public delegate void DragHoverClearedEventHandler();

    public override void _Ready()
    {
        camera = GetViewport().GetCamera3D();
        if (camera == null)
            GD.PrintErr("Camera3D not found for CardDropHandler!");
    }

    public override void _Process(double delta)
    {
        bool isDragging = DragPayloadManager.IsDragging;

        // Detect drag start
        if (isDragging && !_wasDragging)
        {
            var cardUi = DragPayloadManager.DraggedCard;
            bool isTop = DragPayloadManager.IsTopHalf;
            if (cardUi != null)
                EmitSignal(SignalName.CardDragStarted, cardUi, isTop);
        }
        // Detect drag end (without a drop, i.e. cancelled)
        else if (!isDragging && _wasDragging)
        {
            EmitSignal(SignalName.CardDragEnded);
        }

        _wasDragging = isDragging;

        if (!isDragging || camera == null)
        {
            if (CurrentHoveredTile != null) ClearHoverHighlight();
            return;
        }

        var newTile = RaycastToHexTile();

        if (newTile != CurrentHoveredTile)
        {
            ClearHoverHighlight();
            CurrentHoveredTile = newTile;
            newTile?.SetDragHoverHighlight(true);

            // R22 damage preview: notify on every hover-tile change.
            if (newTile != null)
                EmitSignal(SignalName.DragHoverChanged, newTile);
            else
                EmitSignal(SignalName.DragHoverCleared);
        }
    }

    /// <summary>Called by the UI when the player releases the mouse mid-drag. Resolves the currently-hovered tile (if any) and emits <see cref="CardDroppedOnTile"/>. Always clears drag state and snaps the card back; if the drop was valid, the consumer is responsible for animating the card to discard.</summary>
    public void TryDropCardOnTile()
    {
        if (!DragPayloadManager.IsDragging) return;

        var cardUi = DragPayloadManager.DraggedCard;
        bool isTop = DragPayloadManager.IsTopHalf;
        var tile = CurrentHoveredTile;

        // Always reset drag state at the end of an attempt
        DragPayloadManager.IsDragging = false;
        ClearHoverHighlight();

        // Fire the cast attempt only if a valid tile was hovered
        if (tile != null && cardUi != null)
        {
            var halfName = (isTop ? cardUi.TopHalf : cardUi.BottomHalf)?.Name ?? "(null half)";
            GD.Print($"Card dropped on tile {tile.Axial}. Playing {halfName}");
            EmitSignal(SignalName.CardDroppedOnTile, cardUi, isTop, tile);
        }

        // Always snap the card back visually. If the cast succeeded, the deck
        // manager will animate it to the discard pile from there.
        cardUi?.EndDrag();
    }

    private void ClearHoverHighlight()
    {
        CurrentHoveredTile?.SetDragHoverHighlight(false);
        CurrentHoveredTile = null;
    }

    private HexTile RaycastToHexTile()
    {
        if (camera == null) return null;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState.IntersectRay(new PhysicsRayQueryParameters3D
        {
            From = from,
            To = to,
            CollisionMask = 1
        });

        if (!result.TryGetValue("collider", out var colliderVar)) return null;
        return GetParentHexTile(colliderVar.As<Node>());
    }

    private HexTile GetParentHexTile(Node node)
    {
        while (node != null)
        {
            if (node is HexTile tile)
                return tile;

            // Fix (2026-07-09): dropping a card ON A UNIT resolves to the tile
            // the unit stands on. Unit collision bodies share ray layer 1 with
            // tiles, so a drop over an enemy's bounding box used to swallow the
            // ray, resolve no tile, and silently eat the cast. The player had
            // to hit the sliver of tile around the model. Drag-hover highlight
            // gets the same fix for free (this resolver feeds both).
            if (node is Unit unit)
                return unit.CurrentTile?.TileView;

            node = node.GetParent();
        }
        return null;
    }
}