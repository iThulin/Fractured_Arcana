using Godot;
using System;

public partial class CameraController : Node3D
{
    [Export] public float MoveSpeed = 10f;
    [Export] public float ZoomSpeed = 2f;
    [Export] public float MinZoom = 5f;
    [Export] public float MaxZoom = 40f;
    [Export] public float EdgeScrollMargin = 20f;
    [Export] public float EdgeScrollSpeed = 8f;
    [Export] public float DragSpeed = 0.01f;

    [Export] public float RotationSpeed = 0.3f;
    [Export] public float MinPitch = 0f;
    [Export] public float MaxPitch = 75f;

    private float yaw = 0f;
    private float pitch = 20f;

    private Camera3D camera;
    private Node3D cameraPivot;
    private CardDropHandler cardDropHandler;
    private Vector2 lastMousePos;
    private Vector2 mouseDelta;
    private bool dragging = false;

    private Vector3 boundsMin = new Vector3(-10, 0, -10);
    private Vector3 boundsMax = new Vector3(100, 0, 100);

    private Vector3 zoomDirection = Vector3.Zero;
    private float zoomStepRemaining = 0f;
    private float zoomLerpSpeed = 10f;
    private const float MouseToKeyboardRotationRatio = 200f;

    public override void _Ready()
    {
        cameraPivot = GetNode<Node3D>("CameraPivot");
        camera = cameraPivot.GetNode<Camera3D>("Camera3D");

        cardDropHandler = GetNodeOrNull<CardDropHandler>("/root/Main Scene/CardDropHandler");

        var gridManager = GetNodeOrNull<Node3D>("/root/Main Scene/HexGridManager");
        if (gridManager is HexGridManager hexGrid)
        {
            boundsMin = hexGrid.GridBoundsMin;
            boundsMax = hexGrid.GridBoundsMax;
        }
        else
        {
            GD.PrintErr("HexGridManager not found. Using default bounds.");
        }
    }

    public override void _Input(InputEvent @event)
    {
        //GD.Print("CameraController _Input triggered");
        if (@event is InputEventMouseButton mouseEvent)
        {
            GD.Print("Left click detected");
            if (mouseEvent.ButtonIndex == MouseButton.Right)
                dragging = mouseEvent.Pressed;

            if (mouseEvent.ButtonIndex == MouseButton.WheelUp || mouseEvent.ButtonIndex == MouseButton.WheelDown)
            {
                var mousePos = GetViewport().GetMousePosition();
                var from = camera.ProjectRayOrigin(mousePos);
                var to = from + camera.ProjectRayNormal(mousePos) * 1000f;

                var spaceState = GetWorld3D().DirectSpaceState;
                var result = spaceState.IntersectRay(new PhysicsRayQueryParameters3D
                {
                    From = from,
                    To = to,
                    CollisionMask = 1 // make sure your tiles are on this layer
                });

                if (result.Count > 0)
                {
                    GD.Print("Ray hit something!");

                if (result.TryGetValue("collider", out var colliderVar) && colliderVar.VariantType == Variant.Type.Object)
                {
                    GodotObject colliderObject = colliderVar.AsGodotObject();
                    if (colliderObject is Node colliderNode)
                    {
                        // Walk up to find the HexTile ancestor
                        var tile = colliderNode.GetParentOrNull<HexTile>();
                        while (tile == null && colliderNode.GetParent() != null)
                        {
                            colliderNode = colliderNode.GetParent();
                            tile = colliderNode as HexTile;
                        }

                        if (tile != null)
                        {
                            GD.Print($"Ray hit tile at {tile.GlobalPosition}");
                            // Do your card logic here
                        }
                    }
                }
                }
                // === Debug Raycast Result Keys ===
                //GD.Print($"Ray hit result count: {result.Count}");
                foreach (var key in result.Keys)
                {
                    //GD.Print($"Raycast result key: {key}");
                }

                if (result.Count > 0)
                    zoomDirection = ((Vector3)result["position"] - camera.GlobalTransform.Origin).Normalized();
                else
                    zoomDirection = camera.GlobalTransform.Basis.Z * -1f;

                zoomStepRemaining += (mouseEvent.ButtonIndex == MouseButton.WheelUp ? 1 : -1) * ZoomSpeed;
            }

            if (mouseEvent.ButtonIndex == MouseButton.Left && !mouseEvent.Pressed)
            {
                cardDropHandler?.TryDropCardOnTile();
            }

        }

        if (@event is InputEventMouseMotion motionEvent)
            mouseDelta = motionEvent.Relative;
    }

    public override void _Process(double delta)
    {
        // Movement
        Vector3 inputDirection = Vector3.Zero;

        Vector3 forward = -cameraPivot.GlobalTransform.Basis.Z;
        forward.Y = 0;
        forward = forward.Normalized();

        Vector3 right = cameraPivot.GlobalTransform.Basis.X;
        right.Y = 0;
        right = right.Normalized();

        if (Input.IsActionPressed("ui_up")) inputDirection += forward;
        if (Input.IsActionPressed("ui_down")) inputDirection -= forward;
        if (Input.IsActionPressed("ui_right")) inputDirection += right;
        if (Input.IsActionPressed("ui_left")) inputDirection -= right;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;

        if (mousePos.X < EdgeScrollMargin) inputDirection -= right;
        if (mousePos.X > viewportSize.X - EdgeScrollMargin) inputDirection += right;
        if (mousePos.Y < EdgeScrollMargin) inputDirection += forward;
        if (mousePos.Y > viewportSize.Y - EdgeScrollMargin) inputDirection -= forward;

        if (inputDirection != Vector3.Zero)
            inputDirection = inputDirection.Normalized();

        Vector3 newPosition = Position + inputDirection * MoveSpeed * (float)delta;

        if (dragging)
        {
            Vector2 deltaMouse = GetViewport().GetMousePosition() - lastMousePos;
            Vector3 dragRight = -right * deltaMouse.X;
            Vector3 dragForward = forward * deltaMouse.Y;
            newPosition += (dragRight + dragForward) * DragSpeed;
        }

        newPosition.X = Mathf.Clamp(newPosition.X, boundsMin.X, boundsMax.X);
        newPosition.Z = Mathf.Clamp(newPosition.Z, boundsMin.Z, boundsMax.Z);
        Position = newPosition;

        // Rotation
        if (Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            yaw -= mouseDelta.X * RotationSpeed;
            pitch -= mouseDelta.Y * RotationSpeed;
            pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
            cameraPivot.RotationDegrees = new Vector3(pitch, yaw, 0);
        }
        // Keyboard camera rotation (Q/E)
        if (Input.IsActionPressed("rotate_left"))
            yaw -= RotationSpeed * MouseToKeyboardRotationRatio * (float)delta;
        if (Input.IsActionPressed("rotate_right"))
            yaw += RotationSpeed * MouseToKeyboardRotationRatio * (float)delta;


        cameraPivot.RotationDegrees = new Vector3(pitch, yaw, 0);

        // Zoom
        if (Mathf.Abs(zoomStepRemaining) > 0.01f)
        {
            float step = zoomStepRemaining * zoomLerpSpeed * (float)delta;
            Vector3 proposed = camera.GlobalPosition + zoomDirection * step;

            float distanceToPivot = (proposed - cameraPivot.GlobalPosition).Length();
            if (distanceToPivot >= MinZoom && distanceToPivot <= MaxZoom)
            {
                camera.GlobalPosition = proposed;
                zoomStepRemaining -= step / ZoomSpeed;
            }
            else
            {
                zoomStepRemaining = 0;
            }
        }

        mouseDelta = Vector2.Zero;
        lastMousePos = GetViewport().GetMousePosition();
    }
}
