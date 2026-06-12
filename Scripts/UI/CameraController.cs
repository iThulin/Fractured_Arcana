using Godot;

// ============================================================
// CameraController.cs
//
// Purpose:        Top-down combat camera rig — handles pan, zoom,
//                 and orbit on a pivot/camera pair, plus left-click
//                 drop-on-tile forwarding to CardDropHandler.
//
//                 Terrain-aware pass:
//                 - FrameGrid now expects HEIGHT-INCLUSIVE bounds
//                   (HexGridManager.RecomputeGridBounds) and centres
//                   the rig at the bounds midpoint including Y.
//                 - DYNAMIC ZOOM FLOOR: every frame the minimum zoom
//                   is whatever distance keeps the camera's world Y
//                   above boundsMax.Y + MinCameraClearance at the
//                   current pitch. No pan/zoom/orbit combination can
//                   put the eye inside or below the terrain.
//                 - Pitch ceiling tightened (-20°) so the floor stays
//                   finite and the horizon stays unreachable.
//                 - GLIDE-TO-ACTIVE FOCUS: FocusOn(node) glides the
//                   rig to a unit and tracks it while it moves. Any
//                   pan input (keys / edge scroll / right-drag)
//                   cancels the glide; F re-centres on the last
//                   focused unit. Deliberately NOT a hard follow —
//                   a leashed camera fights free card targeting.
// Layer:          UI
// Collaborators:  CardDropHandler.cs (forwards left-click drops),
//                 HexGridManager.cs (GenerateMap → FrameGrid with
//                 height-aware bounds), CombatManager (FocusOn at
//                 turn handoff), Camera3D / Node3D pivot in .tscn.
// See:            README §8 (make_current via CallDeferred)
// ============================================================

/// <summary>
/// Combat camera controller. Owns a pivot Node3D + Camera3D pair: panning moves
/// the controller, rotation/orbit drives the pivot, zoom slides the camera along
/// its local Z. All motion is smoothed via lerps toward target values. A dynamic
/// zoom floor keeps the camera above the terrain at all times, and FocusOn()
/// provides cancellable glide-to-unit framing for turn handoffs.
/// </summary>
public partial class CameraController : Node3D
{
    // ── Tuning ───────────────────────────────────────────────────────────────
    /// <summary>Maximum pan speed in world units per second.</summary>
    [Export] public float MoveSpeed = 12f;
    /// <summary>Lerp rate used to ease the rig toward its desired pan target. Higher = snappier.</summary>
    [Export] public float MoveLerpSpeed = 10f;
    /// <summary>Distance the zoom target moves per scroll wheel tick.</summary>
    [Export] public float ZoomSpeed = 4f;
    /// <summary>Lerp rate used to ease zoom toward its target. Higher = snappier.</summary>
    [Export] public float ZoomLerpSpeed = 8f;
    /// <summary>Closest the camera can get to the pivot (static minimum — the terrain-aware floor can only raise this, never lower it).</summary>
    [Export] public float MinZoom = 3f;
    /// <summary>Farthest the camera can pull out (maximum zoom).</summary>
    [Export] public float MaxZoom = 30f;
    /// <summary>Distance from screen edge (in pixels) within which edge-scroll pan activates.</summary>
    [Export] public float EdgeScrollMargin = 20f;
    /// <summary>Speed multiplier for right-click drag pan.</summary>
    [Export] public float DragSpeed = 0.3f;
    /// <summary>Mouse-rotation sensitivity. Also used for keyboard orbit (scaled by MouseToKeyboardRotationRatio).</summary>
    [Export] public float RotationSpeed = 0.3f;
    /// <summary>Steepest the camera can tilt (looking straight down is -90).</summary>
    [Export] public float MinPitch = -75f;
    /// <summary>Shallowest the camera can tilt. Kept ≤ -20° so the terrain-aware
    /// zoom floor stays finite and the camera can never reach the horizon.</summary>
    [Export] public float MaxPitch = -20f;
    /// <summary>Extra slack (world units) added to the clamp bounds so the camera can drift slightly past the arena edge.</summary>
    [Export] public float BoundsPad = 2f;
    /// <summary>Vertical clearance (world units) the camera keeps above the tallest tile top.</summary>
    [Export] public float MinCameraClearance = 1.5f;
    /// <summary>Lerp rate of the glide toward a focused unit. Lower than MoveLerpSpeed so focus feels like a deliberate camera move, not a snap.</summary>
    [Export] public float FocusLerpSpeed = 5f;

    // ── State ────────────────────────────────────────────────────────────────
    private float _yaw = -45f;
    private float _pitch = -35f;

    private Camera3D _camera;
    private Node3D _pivot;
    private CardDropHandler _cardDropHandler;

    private Vector2 _lastMousePos;
    private Vector2 _mouseDelta;
    private bool _dragging = false;

    private Vector3 _boundsMin = new Vector3(-10, 0, -10);
    private Vector3 _boundsMax = new Vector3(100, 0, 100);

    // Smooth zoom: we track a target Z distance and lerp toward it
    private float _zoomTarget;

    // Smooth pan: lerp the controller position toward a desired position
    private Vector3 _desiredPosition;

    // Glide-to-active focus
    private bool _focusing = false;
    private Node3D _focusNode;       // live target while it exists (tracks movement)
    private Node3D _lastFocusNode;   // recenter target for the F key
    private Vector3 _focusPoint;     // static fallback target

    private const float MouseToKeyboardRotationRatio = 200f;

    // ── Init ─────────────────────────────────────────────────────────────────
    public override void _Ready()
    {
        EnsureCameraNodes();
        _cardDropHandler = GetParent()?.GetNodeOrNull<CardDropHandler>("../CardDropHandler")
            ?? GetNodeOrNull<CardDropHandler>("/root/Main Scene/CardDropHandler");

        if (_pivot != null)
            _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);

        _desiredPosition = Position;
        _zoomTarget = _camera?.Position.Z ?? 20f;
    }

    // ── FrameGrid ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Frames the whole arena. Pass HEIGHT-INCLUSIVE bounds
    /// (HexGridManager.RecomputeGridBounds) — the rig centres at the bounds
    /// midpoint including Y, and the start zoom respects the terrain-aware
    /// safety floor so the camera can never spawn inside the mesh.
    /// </summary>
    public void FrameGrid(Vector3 min, Vector3 max)
    {
        if (!EnsureCameraNodes())
            return;

        _camera.CallDeferred("make_current"); // README §8 — never set Current directly
        _boundsMin = min;
        _boundsMax = max;

        ClearFocus();
        _lastFocusNode = null;

        Vector3 center = (min + max) * 0.5f;
        Vector3 size = max - min;
        float boardSpan = Mathf.Max(size.X, size.Z);

        // Reset rig
        Position = center;
        _desiredPosition = center;
        _pivot.Position = Vector3.Zero;
        _camera.RotationDegrees = Vector3.Zero;

        _yaw = -45f;
        _pitch = -35f;
        _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);

        // Start close (0.35 × span is the intended feel) but never closer than
        // the terrain-aware floor allows at the starting pitch.
        float startZoom = Mathf.Clamp(
            Mathf.Max(boardSpan * 0.35f, MinSafeZoom()),
            MinZoom, MaxZoom);
        _camera.Position = new Vector3(0f, 0f, startZoom);
        _zoomTarget = startZoom;

        GD.Print($"FrameGrid center: {center}  span: {boardSpan:0.0}  startZoom: {startZoom:0.0}  maxY: {max.Y:0.0}");
    }

    // ── Focus (glide-to-active) ──────────────────────────────────────────────
    /// <summary>
    /// Glides the rig to a unit (or any Node3D) and tracks it while it moves.
    /// Cancelled instantly by any pan input; F re-centres on the last target.
    /// Call at turn handoff for BOTH player and enemy units.
    /// </summary>
    public void FocusOn(Node3D node)
    {
        if (node == null)
            return;
        _focusNode = node;
        _lastFocusNode = node;
        _focusPoint = node.GlobalPosition;
        _focusing = true;
    }

    /// <summary>Glides the rig to a static world point (no tracking).</summary>
    public void FocusOn(Vector3 worldPoint)
    {
        _focusNode = null;
        _focusPoint = worldPoint;
        _focusing = true;
    }

    /// <summary>Stops any active glide; the rig stays where it is.</summary>
    public void ClearFocus()
    {
        _focusing = false;
        _focusNode = null;
    }

    // ── Input ────────────────────────────────────────────────────────────────
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Right)
                _dragging = mb.Pressed;

            if (mb.ButtonIndex == MouseButton.WheelUp)
                _zoomTarget = Mathf.Clamp(_zoomTarget - ZoomSpeed, MinZoom, MaxZoom);

            if (mb.ButtonIndex == MouseButton.WheelDown)
                _zoomTarget = Mathf.Clamp(_zoomTarget + ZoomSpeed, MinZoom, MaxZoom);

            if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                _cardDropHandler?.TryDropCardOnTile();
        }

        if (@event is InputEventMouseMotion motion)
            _mouseDelta = motion.Relative;

        // Recenter on the active unit. Direct key check on purpose — no
        // InputMap action required. Remap here if F conflicts with anything.
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.F)
        {
            if (_lastFocusNode != null && IsInstanceValid(_lastFocusNode))
                FocusOn(_lastFocusNode);
        }
    }

    // ── Process ──────────────────────────────────────────────────────────────
    public override void _Process(double delta)
    {
        if (!EnsureCameraNodes())
            return;

        float dt = (float)delta;

        HandlePan(dt);
        HandleRotation(dt);
        HandleZoom(dt);

        _mouseDelta = Vector2.Zero;
        _lastMousePos = GetViewport().GetMousePosition();
    }

    // ── Pan ───────────────────────────────────────────────────────────────────
    private void HandlePan(float delta)
    {
        Vector3 forward = -_pivot.GlobalTransform.Basis.Z;
        forward.Y = 0;
        forward = forward.Normalized();

        Vector3 right = _pivot.GlobalTransform.Basis.X;
        right.Y = 0;
        right = right.Normalized();

        Vector3 inputDir = Vector3.Zero;

        // Keyboard
        if (Input.IsActionPressed("ui_up"))
            inputDir += forward;
        if (Input.IsActionPressed("ui_down"))
            inputDir -= forward;
        if (Input.IsActionPressed("ui_right"))
            inputDir += right;
        if (Input.IsActionPressed("ui_left"))
            inputDir -= right;

        // Edge scroll — only when mouse is not near the bottom (card hand area)
        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        bool inCardArea = mousePos.Y > viewportSize.Y * 0.75f;

        if (!inCardArea)
        {
            if (mousePos.X < EdgeScrollMargin)
                inputDir -= right;
            if (mousePos.X > viewportSize.X - EdgeScrollMargin)
                inputDir += right;
            if (mousePos.Y < EdgeScrollMargin)
                inputDir += forward;
            if (mousePos.Y > viewportSize.Y - EdgeScrollMargin)
                inputDir -= forward;
        }

        bool userPanning = inputDir != Vector3.Zero || _dragging;

        // Any pan input cancels an active glide — the player always wins.
        if (userPanning)
            _focusing = false;

        if (_focusing)
        {
            // Track the focus node while it exists (it moves during its turn);
            // fall back to the captured point if it was freed.
            if (_focusNode != null && IsInstanceValid(_focusNode))
                _focusPoint = _focusNode.GlobalPosition;
            else
                _focusNode = null;

            _desiredPosition = new Vector3(
                _focusPoint.X,
                Mathf.Clamp(_focusPoint.Y, _boundsMin.Y, _boundsMax.Y),
                _focusPoint.Z);
        }
        else
        {
            if (inputDir != Vector3.Zero)
                inputDir = inputDir.Normalized();

            _desiredPosition += inputDir * MoveSpeed * delta;

            // Right-click drag pan
            if (_dragging)
            {
                Vector2 dm = GetViewport().GetMousePosition() - _lastMousePos;
                _desiredPosition += (-right * dm.X
                                   + forward * dm.Y) * DragSpeed * delta;
            }
        }

        // Clamp to arena bounds
        _desiredPosition.X = Mathf.Clamp(_desiredPosition.X,
            _boundsMin.X - BoundsPad, _boundsMax.X + BoundsPad);
        _desiredPosition.Z = Mathf.Clamp(_desiredPosition.Z,
            _boundsMin.Z - BoundsPad, _boundsMax.Z + BoundsPad);

        // Smooth lerp toward desired (focus uses its own, gentler rate)
        float lerpRate = _focusing ? FocusLerpSpeed : MoveLerpSpeed;
        Position = Position.Lerp(_desiredPosition, lerpRate * delta);
    }

    // ── Rotation ─────────────────────────────────────────────────────────────
    private void HandleRotation(float delta)
    {
        if (Input.IsActionPressed("rotate_left"))
            _yaw -= RotationSpeed * MouseToKeyboardRotationRatio * delta;
        if (Input.IsActionPressed("rotate_right"))
            _yaw += RotationSpeed * MouseToKeyboardRotationRatio * delta;

        if (Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            _yaw -= _mouseDelta.X * RotationSpeed;
            _pitch -= _mouseDelta.Y * RotationSpeed;
            _pitch = Mathf.Clamp(_pitch, MinPitch, MaxPitch);
        }

        _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────
    private void HandleZoom(float delta)
    {
        // Terrain-aware floor: at the current pitch, this is the closest zoom
        // that keeps the camera's world Y above the tallest tile + clearance.
        float minSafe = MinSafeZoom();
        _zoomTarget = Mathf.Clamp(_zoomTarget, minSafe, MaxZoom);

        Vector3 camPos = _camera.Position;
        camPos.Z = Mathf.Lerp(camPos.Z, _zoomTarget, ZoomLerpSpeed * delta);
        // Hard floor on the actual value too — a pitch change mid-lerp must
        // never leave the eye inside the terrain for even one frame.
        camPos.Z = Mathf.Max(camPos.Z, minSafe);
        _camera.Position = camPos;
    }

    /// <summary>
    /// Minimum zoom distance that keeps the camera above boundsMax.Y +
    /// MinCameraClearance at the current pitch: camera world Y rises by
    /// zoom · sin(|pitch|) above the rig. Shallower pitch → larger floor.
    /// </summary>
    private float MinSafeZoom()
    {
        float sin = Mathf.Sin(Mathf.DegToRad(Mathf.Abs(_pitch)));
        if (sin < 0.05f)
            return MaxZoom; // defensive — MaxPitch should keep us far from here

        float requiredRise = _boundsMax.Y + MinCameraClearance - Position.Y;
        if (requiredRise <= 0f)
            return MinZoom;

        return Mathf.Max(MinZoom, requiredRise / sin);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private bool EnsureCameraNodes()
    {
        if (_pivot == null)
            _pivot = GetNodeOrNull<Node3D>("CameraPivot");

        if (_camera == null && _pivot != null)
            _camera = _pivot.GetNodeOrNull<Camera3D>("Camera3D");

        if (_pivot == null || _camera == null)
            return false;
        return true;
    }

    /// <summary>
    /// Position the camera centered on fromCenter, rotated to face toward towardPoint.
    /// Call after FrameGrid so bounds are already set.
    /// </summary>
    public void FaceToward(Vector3 fromCenter, Vector3 towardPoint)
    {
        if (!EnsureCameraNodes())
            return;

        ClearFocus();

        // Pan to player spawn center
        Position = fromCenter;
        _desiredPosition = fromCenter;

        // Compute yaw so camera faces from player zone toward enemy zone
        Vector3 dir = (towardPoint - fromCenter).Normalized();
        // atan2 of the XZ direction gives us the horizontal angle
        float targetYaw = Mathf.RadToDeg(Mathf.Atan2(dir.X, dir.Z));
        // Camera looks "into" the scene so we add 180 to flip it
        _yaw = targetYaw + 180f;

        _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);
    }
}
