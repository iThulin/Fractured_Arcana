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
    private Vector2 lastMousePos;
    private Vector2 mouseDelta;
    private bool dragging = false;

    private Vector3 boundsMin = new Vector3(-10, 0, -10);
    private Vector3 boundsMax = new Vector3(100, 0, 100);
    private float targetZoomDistance;
    private float zoomLerpSpeed = 10f; // You can adjust for speed

    public override void _Ready()
    {
        cameraPivot = GetNode<Node3D>("CameraPivot");
        camera = cameraPivot.GetNode<Camera3D>("Camera3D");

        targetZoomDistance = camera.Position.Length();

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
        if (@event is InputEventMouseButton mouseEvent)
        {
            if (mouseEvent.ButtonIndex == MouseButton.Right)
                dragging = mouseEvent.Pressed;

            // Set target zoom distance
            if (mouseEvent.ButtonIndex == MouseButton.WheelUp)
                targetZoomDistance -= ZoomSpeed;
            if (mouseEvent.ButtonIndex == MouseButton.WheelDown)
                targetZoomDistance += ZoomSpeed;

            // Clamp zoom distance
            targetZoomDistance = Mathf.Clamp(targetZoomDistance, MinZoom, MaxZoom);
        }

        if (@event is InputEventMouseMotion motionEvent)
            mouseDelta = motionEvent.Relative;
    }

    public override void _Process(double delta)
{
    Vector3 inputDirection = Vector3.Zero;

    // Read camera-facing directions
    Vector3 forward = -cameraPivot.GlobalTransform.Basis.Z;
    forward.Y = 0; // flatten
    forward = forward.Normalized();

    Vector3 right = cameraPivot.GlobalTransform.Basis.X;
    right.Y = 0;
    right = right.Normalized();

    // WASD / Arrow keys
    if (Input.IsActionPressed("ui_up"))    inputDirection += forward;
    if (Input.IsActionPressed("ui_down"))  inputDirection -= forward;
    if (Input.IsActionPressed("ui_right")) inputDirection += right;
    if (Input.IsActionPressed("ui_left"))  inputDirection -= right;

    // Edge scroll (relative)
    Vector2 mousePos = GetViewport().GetMousePosition();
    Vector2 viewportSize = GetViewport().GetVisibleRect().Size;

    if (mousePos.X < EdgeScrollMargin) inputDirection -= right;
    if (mousePos.X > viewportSize.X - EdgeScrollMargin) inputDirection += right;
    if (mousePos.Y < EdgeScrollMargin) inputDirection += forward;
    if (mousePos.Y > viewportSize.Y - EdgeScrollMargin) inputDirection -= forward;

    // Normalize to prevent diagonal boost
    if (inputDirection != Vector3.Zero)
        inputDirection = inputDirection.Normalized();

    Vector3 newPosition = Position + inputDirection * MoveSpeed * (float)delta;

    // Right-click drag
    if (dragging)
    {
        Vector2 deltaMouse = GetViewport().GetMousePosition() - lastMousePos;
        Vector3 dragRight = -right * deltaMouse.X;
        Vector3 dragForward = forward * deltaMouse.Y;
        newPosition += (dragRight + dragForward) * DragSpeed;
    }

    // Clamp camera bounds
    newPosition.X = Mathf.Clamp(newPosition.X, boundsMin.X, boundsMax.X);
    newPosition.Z = Mathf.Clamp(newPosition.Z, boundsMin.Z, boundsMax.Z);
    Position = newPosition;

    // Middle mouse rotate
    if (Input.IsMouseButtonPressed(MouseButton.Middle))
    {
        yaw -= mouseDelta.X * RotationSpeed;
        pitch -= mouseDelta.Y * RotationSpeed;
        pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
        cameraPivot.RotationDegrees = new Vector3(pitch, yaw, 0);
    }

    // Smooth zoom toward targetZoomDistance
    Vector3 zoomDirection = camera.GlobalTransform.Basis.Z.Normalized();
    float currentDistance = camera.Position.Length();
    float zoomDelta = targetZoomDistance - currentDistance;

    if (Mathf.Abs(zoomDelta) > 0.01f)
    {
        float zoomStep = zoomDelta * zoomLerpSpeed * (float)delta;
        camera.Position += zoomDirection * zoomStep;
    }

    mouseDelta = Vector2.Zero;
    lastMousePos = GetViewport().GetMousePosition();
}

}
