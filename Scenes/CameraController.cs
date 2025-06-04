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
    [Export] public float MinPitch = -30f;
    [Export] public float MaxPitch = 60f;

    private float yaw = 0f;
    private float pitch = 20f;

    private Camera3D camera;
    private Vector2 lastMousePos;
    private Vector2 mouseDelta;
    private bool dragging = false;

    private Vector3 boundsMin = new Vector3(-10, 0, -10);
    private Vector3 boundsMax = new Vector3(100, 0, 100);

    public override void _Ready()
    {
        camera = GetNode<Camera3D>("Camera3D");

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

            if (mouseEvent.ButtonIndex == MouseButton.WheelUp && camera != null)
                camera.Position -= camera.Basis.Z.Normalized() * ZoomSpeed;
            if (mouseEvent.ButtonIndex == MouseButton.WheelDown && camera != null)
                camera.Position += camera.Basis.Z.Normalized() * ZoomSpeed;

            float zoomDistance = camera.Position.Length();
            if (zoomDistance < MinZoom)
                camera.Position = camera.Position.Normalized() * MinZoom;
            else if (zoomDistance > MaxZoom)
                camera.Position = camera.Position.Normalized() * MaxZoom;
        }

        if (@event is InputEventMouseMotion motionEvent)
        {
            mouseDelta = motionEvent.Relative;
        }
    }

    public override void _Process(double delta)
    {
        Vector3 inputDirection = Vector3.Zero;

        if (Input.IsActionPressed("ui_right")) inputDirection.X += 1;
        if (Input.IsActionPressed("ui_left"))  inputDirection.X -= 1;
        if (Input.IsActionPressed("ui_down"))  inputDirection.Z += 1;
        if (Input.IsActionPressed("ui_up"))    inputDirection.Z -= 1;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;

        if (mousePos.X < EdgeScrollMargin) inputDirection.X -= 1;
        if (mousePos.X > viewportSize.X - EdgeScrollMargin) inputDirection.X += 1;
        if (mousePos.Y < EdgeScrollMargin) inputDirection.Z -= 1;
        if (mousePos.Y > viewportSize.Y - EdgeScrollMargin) inputDirection.Z += 1;

        if (inputDirection != Vector3.Zero)
            inputDirection = inputDirection.Normalized();

        Vector3 newPosition = Position + inputDirection * MoveSpeed * (float)delta;

        if (dragging)
        {
            Vector2 deltaMouse = GetViewport().GetMousePosition() - lastMousePos;
            Vector3 dragMove = new Vector3(-deltaMouse.X, 0, -deltaMouse.Y) * 0.1f;
            newPosition += dragMove * DragSpeed;
        }

        newPosition.X = Mathf.Clamp(newPosition.X, boundsMin.X, boundsMax.X);
        newPosition.Z = Mathf.Clamp(newPosition.Z, boundsMin.Z, boundsMax.Z);
        Position = newPosition;

        if (Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            yaw -= mouseDelta.X * RotationSpeed;
            pitch -= mouseDelta.Y * RotationSpeed;
            pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
            RotationDegrees = new Vector3(pitch, yaw, 0);
        }

        mouseDelta = Vector2.Zero;
        lastMousePos = GetViewport().GetMousePosition();
    }
}
