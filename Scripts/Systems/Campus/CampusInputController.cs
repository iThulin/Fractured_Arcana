using Godot;
using System.Collections.Generic;

// ============================================================
// CampusInputController.cs
//
// Purpose:        Turns mouse input over the campus 3D viewport
//                 into building selection and drag-and-drop
//                 placement. Combat has no equivalent of this —
//                 HexTile only exposes hover (MouseEntered/
//                 MouseExited), and CameraController's left-click
//                 handling is card-drop-specific
//                 (_cardDropHandler.TryDropCardOnTile()). This is
//                 new plumbing, not a reuse of anything combat-side.
// Layer:          UI
// Collaborators:  CampusGridManager.cs (grid queries, preview,
//                 placement commit), CampusScreen.cs (starts a
//                 drag from a building palette entry, receives
//                 BuildingSelected to open a future info panel)
// See:             Conversation note: "building interactable to
//                 pull up relevant sub-menus and act as the new
//                 main hud/ui bus" — BuildingSelected is the seam
//                 for that; the sub-menu system itself is not
//                 designed here, just the selection event it needs.
// ============================================================

/// <summary>Raycasts mouse input against the campus grid's HexTile colliders to drive
/// two interactions: plain click-to-select (for an eventual building info/sub-menu
/// panel) and drag-and-drop building placement (palette → live preview → commit).
/// ASSUMPTION (unverified against the actual HexTile.tscn hierarchy): each HexTile's
/// collider is a StaticBody3D that is a DIRECT CHILD of the HexTile node itself,
/// matching how HexTile.cs's own _Ready() looks it up. If the real hierarchy nests
/// it deeper, HexTileFromCollider below needs a parent-walk instead of one GetParent().</summary>
public partial class CampusInputController : Node3D
{
    [Export] public NodePath GridManagerPath;
    [Export] public NodePath CameraPath;
    [Export] public float MaxRayDistance = 200f;

    private CampusGridManager _grid;
    private Camera3D _camera;

    private bool _acceptInput = true;

    /// <summary>Same gate as CameraController.AcceptInput, same reason: the drag-
    /// preview raycast reads GetViewport().GetMousePosition() every motion event,
    /// which has the same SubViewport-local-position ambiguity when the cursor isn't
    /// actually over this viewport. Default true. Deactivating mid-drag cancels the
    /// drag outright rather than leaving it frozen and invisible until reactivated.</summary>
    public bool AcceptInput
    {
        get => _acceptInput;
        set
        {
            _acceptInput = value;
            if (!value && _draggingBuildingId != null)
                CancelDrag();
        }
    }

    // ── Drag state ────────────────────────────────────────────────────
    private string _draggingBuildingId = null;
    private Building _draggingTemplate = null;
    private int _dragRotation = 0;
    private Vector2I _lastHoveredAnchor;
    private bool _hasHoveredAnchor = false;

    [Signal] public delegate void BuildingSelectedEventHandler(string buildingId, Vector2I anchor);
    [Signal] public delegate void TileClickedEventHandler(Vector2I axial); // empty-tile click — e.g. deselect
    [Signal] public delegate void PlacementConfirmedEventHandler(string buildingId, Vector2I anchor, int rotation);
    [Signal] public delegate void PlacementCancelledEventHandler(string buildingId);

    public override void _Ready()
    {
        // Only resolve via NodePath if Configure() wasn't already called directly —
        // CampusScreen builds this whole scene in code at runtime (no .tscn), so it
        // wires _grid/_camera itself rather than relying on exported NodePaths.
        if (_grid == null)
            _grid = GetNodeOrNull<CampusGridManager>(GridManagerPath);
        if (_camera == null)
            _camera = GetNodeOrNull<Camera3D>(CameraPath);

        if (_grid == null)
            GD.PrintErr("CampusInputController: no CampusGridManager (Configure() not called and GridManagerPath did not resolve).");
        if (_camera == null)
            GD.PrintErr("CampusInputController: no Camera3D (Configure() not called and CameraPath did not resolve).");
    }

    /// <summary>Direct-wiring entry point for code-built scenes — call right after
    /// AddChild instead of authoring GridManagerPath/CameraPath in an editor scene.</summary>
    public void Configure(CampusGridManager grid, Camera3D camera)
    {
        _grid = grid;
        _camera = camera;
    }

    /// <summary>Call from CampusScreen when the player starts dragging a building
    /// palette entry (an owned, Tier > 0, not-yet-placed building) onto the map.</summary>
    public void BeginDrag(string buildingId)
    {
        _draggingTemplate = BuildingDatabase.GetTemplate(buildingId);
        if (_draggingTemplate == null)
        {
            GD.PrintErr($"CampusInputController: no template for '{buildingId}', drag not started.");
            return;
        }
        _draggingBuildingId = buildingId;
        _dragRotation = 0;
        _hasHoveredAnchor = false;
    }

    /// <summary>Cancels an in-progress drag without placing anything. Safe to call
    /// even if no drag is active.</summary>
    public void CancelDrag()
    {
        if (_draggingBuildingId == null)
            return;

        _grid?.ClearPlacementPreview();
        string id = _draggingBuildingId;
        _draggingBuildingId = null;
        _draggingTemplate = null;
        _hasHoveredAnchor = false;
        EmitSignal(SignalName.PlacementCancelled, id);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!AcceptInput)
            return;
        if (_grid == null || _camera == null)
            return;

        if (@event is InputEventMouseMotion)
        {
            if (_draggingBuildingId != null)
                UpdateDragPreview();
            return;
        }

        if (@event is InputEventMouseButton mb)
        {
            if (_draggingBuildingId != null)
            {
                // Right-click cancels an active drag.
                if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                {
                    CancelDrag();
                    return;
                }
                // Left-click release commits (or fails silently — the preview already
                // showed red if invalid, so a failed commit here isn't a surprise).
                if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                {
                    CommitDrag();
                    return;
                }
            }
            else if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                HandleClick();
            }
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (_draggingBuildingId != null && key.Keycode == Key.R)
            {
                _dragRotation = (_dragRotation + 1) % 6;
                UpdateDragPreview(); // re-show at the same hovered anchor, new rotation
            }
            else if (_draggingBuildingId != null && key.Keycode == Key.Escape)
            {
                CancelDrag();
            }
        }
    }

    private void HandleClick()
    {
        if (!TryRaycastHex(out Vector2I coord))
            return;

        string buildingId = _grid.GetBuildingIdAt(coord);
        if (!string.IsNullOrEmpty(buildingId))
            EmitSignal(SignalName.BuildingSelected, buildingId, coord);
        else
            EmitSignal(SignalName.TileClicked, coord);
    }

    private void UpdateDragPreview()
    {
        if (!TryRaycastHex(out Vector2I coord))
        {
            // Cursor left the grid entirely — clear the preview so nothing looks
            // like a stale valid/invalid ghost is still live.
            if (_hasHoveredAnchor)
            {
                _grid.ClearPlacementPreview();
                _hasHoveredAnchor = false;
            }
            return;
        }

        _lastHoveredAnchor = coord;
        _hasHoveredAnchor = true;
        _grid.ShowPlacementPreview(_draggingTemplate, coord, _dragRotation);
    }

    private void CommitDrag()
    {
        string id = _draggingBuildingId;
        _grid.ClearPlacementPreview();

        if (_hasHoveredAnchor)
        {
            // Caller (CampusScreen) owns the actual BuildingSaveData list — this
            // controller only knows the grid, not the save. It emits the confirmed
            // intent; CampusScreen calls CampusGridManager.PlaceBuilding with the
            // real save-backed list and reacts to success/failure itself.
            EmitSignal(SignalName.PlacementConfirmed, id, _lastHoveredAnchor, _dragRotation);
        }
        else
        {
            EmitSignal(SignalName.PlacementCancelled, id);
        }

        _draggingBuildingId = null;
        _draggingTemplate = null;
        _hasHoveredAnchor = false;
    }

    private bool TryRaycastHex(out Vector2I coord)
    {
        coord = default;

        var mousePos = GetViewport().GetMousePosition();
        var from = _camera.ProjectRayOrigin(mousePos);
        var to = from + _camera.ProjectRayNormal(mousePos) * MaxRayDistance;

        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        var result = spaceState.IntersectRay(query);

        if (result.Count == 0)
            return false;

        var collider = result["collider"].As<Node>();
        var hexTile = HexTileFromCollider(collider);
        if (hexTile == null)
            return false;

        coord = hexTile.Axial;
        return _grid.Tiles.ContainsKey(coord);
    }

    /// <summary>See the class-level ASSUMPTION note — this expects the collider to be
    /// a direct child of the HexTile. Change to a parent-walk loop if that's wrong.</summary>
    private static HexTile HexTileFromCollider(Node collider)
    {
        return collider?.GetParent() as HexTile;
    }
}
