using Godot;
using System;

public partial class CameraController : Node3D
{
    [Export] public float MoveSpeed = 10f;
    [Export] public float ZoomSpeed = 2f;
    [Export] public float MinZoom = 5f;
    [Export] public float MaxZoom = 40f;
    [Export] public float EdgeScrollMargin = 20f;
    [Export] public float DragSpeed = 0.01f;

    [Export] public float RotationSpeed = 0.3f;
    [Export] public float MinPitch = -80f;
    [Export] public float MaxPitch = -10f;
    [Export] public float pad = 4f;

    private float yaw = -45f;
    private float pitch = -35f;

    private Camera3D camera;
    private Node3D cameraPivot;
    private CardDropHandler cardDropHandler;
    private Vector2 lastMousePos;
    private Vector2 mouseDelta;
    private bool dragging = false;

    private Vector3 boundsMin = new Vector3(-10, 0, -10);
    private Vector3 boundsMax = new Vector3(100, 0, 100);

    private float zoomStepRemaining = 0f;
    private float zoomLerpSpeed = 10f;
    private const float MouseToKeyboardRotationRatio = 200f;

    public override void _Ready()
    {
        EnsureCameraNodes();
        cardDropHandler = GetNodeOrNull<CardDropHandler>("/root/Main Scene/CardDropHandler");

        if (cameraPivot != null)
            cameraPivot.RotationDegrees = new Vector3(pitch, yaw, 0f);
    }

    public void FrameGrid(Vector3 min, Vector3 max)
    {
        if (!EnsureCameraNodes())
            return;

        camera.Current = true;

        boundsMin = min;
        boundsMax = max;

        Vector3 center = (min + max) * 0.5f;
        Vector3 size = max - min;
        float boardSpan = Mathf.Max(size.X, size.Z);

        // Reset rig local transforms
        Position = center;
        cameraPivot.Position = Vector3.Zero;
        cameraPivot.RotationDegrees = Vector3.Zero;
        camera.Position = Vector3.Zero;
        camera.RotationDegrees = Vector3.Zero;

        yaw = -45f;
        pitch = -35f;
        cameraPivot.RotationDegrees = new Vector3(pitch, yaw, 0f);

        float zoomDistance = Mathf.Clamp(boardSpan * 0.9f, MinZoom, MaxZoom);
        camera.Position = new Vector3(0f, 0f, zoomDistance);

        zoomStepRemaining = 0f;

        GD.Print($"FrameGrid center: {center}");
        GD.Print($"Controller pos: {Position}");
        GD.Print($"Pivot rot: {cameraPivot.RotationDegrees}");
        GD.Print($"Camera local pos: {camera.Position}");
        GD.Print($"Camera global pos: {camera.GlobalPosition}");
        GD.Print($"Camera current: {camera.Current}");
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseEvent)
        {
            if (mouseEvent.ButtonIndex == MouseButton.Right)
                dragging = mouseEvent.Pressed;

            if (mouseEvent.ButtonIndex == MouseButton.WheelUp || mouseEvent.ButtonIndex == MouseButton.WheelDown)
            {
                zoomStepRemaining += (mouseEvent.ButtonIndex == MouseButton.WheelUp ? -1 : 1) * ZoomSpeed;
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

        newPosition.X = Mathf.Clamp(newPosition.X, boundsMin.X - pad, boundsMax.X + pad);
        newPosition.Z = Mathf.Clamp(newPosition.Z, boundsMin.Z - pad, boundsMax.Z + pad);
        Position = newPosition;

        if (Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            yaw -= mouseDelta.X * RotationSpeed;
            pitch -= mouseDelta.Y * RotationSpeed;
            pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
        }

        if (Input.IsActionPressed("rotate_left"))
            yaw -= RotationSpeed * MouseToKeyboardRotationRatio * (float)delta;
        if (Input.IsActionPressed("rotate_right"))
            yaw += RotationSpeed * MouseToKeyboardRotationRatio * (float)delta;

        cameraPivot.RotationDegrees = new Vector3(pitch, yaw, 0f);

        if (Mathf.Abs(zoomStepRemaining) > 0.01f)
        {
            float step = zoomStepRemaining * zoomLerpSpeed * (float)delta;

            Vector3 localPos = camera.Position;
            localPos.Z = Mathf.Clamp(localPos.Z + step, MinZoom, MaxZoom);
            camera.Position = localPos;

            zoomStepRemaining -= step / ZoomSpeed;
        }

        mouseDelta = Vector2.Zero;
        lastMousePos = GetViewport().GetMousePosition();
    }

    private bool EnsureCameraNodes()
    {
        if (cameraPivot == null)
            cameraPivot = GetNodeOrNull<Node3D>("CameraPivot");

        if (camera == null && cameraPivot != null)
            camera = cameraPivot.GetNodeOrNull<Camera3D>("Camera3D");

        if (cameraPivot == null)
        {
            GD.PrintErr("CameraController: CameraPivot not found.");
            return false;
        }

        if (camera == null)
        {
            GD.PrintErr("CameraController: Camera3D not found under CameraPivot.");
            return false;
        }

        return true;
    }
}