using Godot;

// ============================================================
// CameraController.cs
//
// Purpose:        Top-down combat camera rig. Handles pan, zoom,
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
//                   focused unit. Deliberately NOT a hard follow:
//                   a leashed camera fights free card targeting.
//
//                 Lens + world pass (camera_lens_slice1, 2026-10-06):
//                 - LENS-NORMALISED ZOOM: every zoom number in this
//                   class (MinZoom, MaxZoom, ZoomSpeed, _zoomTarget,
//                   the FrameGrid and FrameUnits framings) is in
//                   REFERENCE units, authored for ReferenceFov (75).
//                   The real camera distance is that value times
//                   FovZoomScale = tan(ref/2) / tan(fov/2), so a
//                   narrower lens keeps the framing it was tuned for.
//                 - HORIZON GUARD: the top of the view is kept at
//                   least HorizonMarginDeg below the horizon. A pitch
//                   shallower than half the vertical FOV puts sky in
//                   frame, and no amount of terrain can fill sky.
//                 - ZOOM TILT: the shallowest allowed pitch steepens
//                   toward ZoomedOutMaxPitch as the camera pulls out.
//                   _pitch stays the player's preference and comes
//                   back on zoom-in (EffectivePitch).
//                 - WORLD CONTAINMENT: after SetWorldExtent, the four
//                   frustum-corner ground hits must stay inside the
//                   terrain disc. Pan is clamped first, zoom second,
//                   and the actual rig is hard-clamped every frame.
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
    /// <summary>Zoom per unit of two-finger scroll, as a fraction of ZoomSpeed. Negate to flip the direction.</summary>
    [Export] public float TrackpadScrollZoom = 0.35f;
    /// <summary>Closest the camera can get to the pivot (static minimum; the terrain-aware floor can only raise this, never lower it).</summary>
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
    /// <summary>Shallowest the camera can tilt. -30° keeps the terrain-aware zoom
    /// floor finite AND keeps near-level views off the table: at -20° the camera
    /// could sit barely above the tallest tile and stare across the board at the
    /// rim skirts edge-on, which reads as "under the world".</summary>
    [Export] public float MaxPitch = -30f;
    /// <summary>How far INSIDE the playable bounds the rig is kept (world units).
    /// A positive inset stops the camera from parking on the arena edge and
    /// staring past the vista rim. The surround is dressing, not a destination.
    /// The old outward slack (BoundsPad) is gone for the same reason.</summary>
    [Export] public float PanEdgeInset = 2.5f;
    /// <summary>Maximum zoom as a fraction of the board span, resolved per map in
    /// FrameGrid (never above MaxZoom). Keeps the pull-out ceiling proportional to
    /// the arena so small maps can't be viewed from orbit. The vista ring reads
    /// as landscape at mid range and as a floating island from a satellite.</summary>
    [Export] public float MaxZoomSpanFactor = 0.85f;
    /// <summary>Vertical clearance (world units) the camera keeps above the tallest tile top.</summary>
    [Export] public float MinCameraClearance = 1.5f;
    /// <summary>Lerp rate of the glide toward a focused unit. Lower than MoveLerpSpeed so focus feels like a deliberate camera move, not a snap.</summary>
    [Export] public float FocusLerpSpeed = 5f;

    // ── Lens + world (camera_lens_slice1) ───────────────────────────────────
    /// <summary>The lens every zoom number in this class is authored for (degrees, along
    /// the Camera3D's keep-aspect axis). The real lens lives on the Camera3D (combat sets
    /// it through SetLensFov). FovZoomScale converts, so a lens change never changes the
    /// framing. 75 is Godot's default lens, the one all existing tuning was done under.</summary>
    [Export] public float ReferenceFov = 75f;
    /// <summary>Degrees the TOP edge of the view is kept below the horizon. A pitch
    /// shallower than half the vertical FOV shows sky, which no terrain can fill.
    /// Negative disables the guard. Applies once SetLensFov or SetWorldExtent has been
    /// called (combat does both); the campus calls neither.</summary>
    [Export] public float HorizonMarginDeg = 6f;
    /// <summary>Shallowest pitch allowed at full zoom-out. Zoomed in, the player may tilt
    /// as low as MaxPitch; pulling out tilts the view toward this. The player's own pitch
    /// comes back on zoom-in. Set it equal to MaxPitch to disable the tilt. Applies once
    /// SetLensFov or SetWorldExtent has been called, like the horizon guard.</summary>
    [Export] public float ZoomedOutMaxPitch = -45f;
    /// <summary>Point in the zoom range (0 = closest, 1 = farthest) where the zoom tilt
    /// begins. Below it the player's pitch is untouched.</summary>
    [Export(PropertyHint.Range, "0,0.95,0.05")] public float ZoomTiltStart = 0.4f;
    /// <summary>Keep the view footprint inside the terrain disc given to SetWorldExtent.
    /// Combat sets one; the campus never does, so this is inert there.</summary>
    [Export] public bool EnforceWorldContainment = true;
    /// <summary>Metres of slack kept between the view footprint and the edge of the world.</summary>
    [Export] public float WorldContainmentMargin = 4f;
    /// <summary>The opening view (FrameUnits) never pulls out past this fraction of the
    /// board span (reference zoom). Fitting every unit on a spread deployment meant max
    /// zoom and tiny units; enemies beyond the cap sit up-screen along the facing.</summary>
    [Export(PropertyHint.Range, "0.15,1,0.05")] public float OpeningZoomSpanFactor = 0.4f;
    /// <summary>Where the opening view centres on the party-to-enemy line: 0 = on the
    /// party, 0.5 = the midpoint (the old framing). Low values keep the party above the
    /// card hand while the camera still faces the enemies.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.05")] public float OpeningPartyBias = 0.25f;

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

    // Per-map zoom ceiling: min(MaxZoom, boardSpan × MaxZoomSpanFactor), set by
    // FrameGrid. All zoom clamps use this, never raw MaxZoom.
    private float _maxZoomDynamic = 30f;

    // Smooth pan: lerp the controller position toward a desired position
    private Vector3 _desiredPosition;

    // Glide-to-active focus
    private bool _focusing = false;
    private Node3D _focusNode;       // live target while it exists (tracks movement)
    private Node3D _lastFocusNode;   // recenter target for the F key
    private Vector3 _focusPoint;     // static fallback target

    private const float MouseToKeyboardRotationRatio = 200f;

    // World containment (SetWorldExtent): the disc of terrain that exists.
    private bool _hasWorldExtent = false;
    private Vector2 _worldCenterXZ;
    private float _worldRadius;
    private float _worldFloorY;
    private bool _containWarned = false;
    private bool _lensManaged = false;   // SetLensFov was called: zoom tilt + horizon guard apply
    private float _openingZoomCap = 12f;  // FrameGrid: OpeningZoomSpanFactor x board span (reference units)

    // ── Input gate ───────────────────────────────────────────────────────────
    private bool _acceptInput = true;

    /// <summary>Gate for all input this controller reacts to. Default true; existing
    /// combat usage never touches this and is completely unchanged. Set false when
    /// this rig's viewport isn't the "active" one (e.g. mouse hovering a sibling UI
    /// panel in a SubViewport-embedded context like the campus screen). Necessary
    /// because HandlePan/HandleRotation/HandleZoom read Input.IsActionPressed as raw
    /// polls every frame in _Process, and _Input reads mouse motion/buttons directly.
    /// Neither goes through Godot's normal event-consumption pipeline, so nothing
    /// about a Control's screen position can scope them on its own.</summary>
    public bool AcceptInput
    {
        get => _acceptInput;
        set
        {
            _acceptInput = value;
            if (!value)
            {
                // Defensive: don't let a right-drag or a pending motion delta get
                // stuck mid-gesture across an activation boundary (e.g. the mouse-up
                // that would have cleared _dragging happens while gated off).
                _dragging = false;
                _mouseDelta = Vector2.Zero;
            }
        }
    }

    // ── Init ─────────────────────────────────────────────────────────────────
    public override void _Ready()
    {
        EnsureCameraNodes();
        _cardDropHandler = GetParent()?.GetNodeOrNull<CardDropHandler>("../CardDropHandler")
            ?? GetNodeOrNull<CardDropHandler>("/root/Main Scene/CardDropHandler");

        if (_pivot != null)
            _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);

        _desiredPosition = Position;
        _zoomTarget = (_camera?.Position.Z ?? 20f) / FovZoomScale;
    }

    // ── FrameGrid ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Frames the whole arena. Pass HEIGHT-INCLUSIVE bounds
    /// (HexGridManager.RecomputeGridBounds). The rig centres at the bounds
    /// midpoint including Y, and the start zoom respects the terrain-aware
    /// safety floor so the camera can never spawn inside the mesh.
    /// </summary>
    /// <param name="pitch">Starting camera pitch. Defaults to the combat board's -35°, a
    /// low diorama angle that reads well for a 2-zone arena. The CAMPUS passes a steeper
    /// value: on a radius-5 disc, -35° foreshortens the far half so hard that its hex labels
    /// are unreadable, and there is no tactical reason for a management map to sit low.</param>
    /// <param name="yaw">Starting camera yaw. Defaults to -45°.</param>
    public void FrameGrid(Vector3 min, Vector3 max, float pitch = -35f, float yaw = -45f)
    {
        if (!EnsureCameraNodes())
            return;

        _camera.CallDeferred("make_current"); // README §8: never set Current directly
        _boundsMin = min;
        _boundsMax = max;
        _hasWorldExtent = false; // the caller re-sends it after framing (HexGridManager does)

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

        _yaw = yaw;
        _pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
        _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);

        // Per-map zoom ceiling: proportional to the arena, capped by MaxZoom.
        _maxZoomDynamic = Mathf.Clamp(boardSpan * MaxZoomSpanFactor, MinZoom + 4f, MaxZoom);
        _openingZoomCap = Mathf.Clamp(boardSpan * OpeningZoomSpanFactor, MinZoom, _maxZoomDynamic);

        // Start close (0.35 × span is the intended feel) but never closer than
        // the terrain-aware floor allows at the starting pitch.
        float startZoom = Mathf.Clamp(
            Mathf.Max(boardSpan * 0.35f, MinSafeZoom()),
            MinZoom, _maxZoomDynamic);
        _camera.Position = new Vector3(0f, 0f, startZoom * FovZoomScale);
        _zoomTarget = startZoom;
        _pivot.RotationDegrees = new Vector3(EffectivePitch(startZoom), _yaw, 0f);

        GD.Print($"FrameGrid center: {center}  span: {boardSpan:0.0}  startZoom: {startZoom:0.0}  maxY: {max.Y:0.0}  lens: {_camera.Fov:0}deg x{FovZoomScale:0.00}");
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
        if (!AcceptInput)
            return;

        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Right)
                _dragging = mb.Pressed;

            if (mb.ButtonIndex == MouseButton.WheelUp)
                _zoomTarget = Mathf.Clamp(_zoomTarget - ZoomSpeed, MinZoom, _maxZoomDynamic);

            if (mb.ButtonIndex == MouseButton.WheelDown)
                _zoomTarget = Mathf.Clamp(_zoomTarget + ZoomSpeed, MinZoom, _maxZoomDynamic);

            if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                _cardDropHandler?.TryDropCardOnTile();
        }

        // macOS trackpad: pinch zooms, and a two-finger vertical scroll zooms like the
        // wheel. A Mac trackpad sends gesture events, never WheelUp/WheelDown.
        if (@event is InputEventMagnifyGesture mag && mag.Factor > 0.01f)
            _zoomTarget = Mathf.Clamp(_zoomTarget / mag.Factor, MinZoom, _maxZoomDynamic);
        else if (@event is InputEventPanGesture pan && !pan.AltPressed)
            _zoomTarget = Mathf.Clamp(_zoomTarget + pan.Delta.Y * ZoomSpeed * TrackpadScrollZoom,
                                      MinZoom, _maxZoomDynamic);

        if (@event is InputEventMouseMotion motion)
            _mouseDelta = motion.Relative;

        // Recenter on the active unit. Direct key check on purpose, no
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
        if (!AcceptInput)
            return;
        if (!EnsureCameraNodes())
            return;

        float dt = (float)delta;

        HandlePan(dt);
        HandleRotation(dt);
        HandleZoom(dt);
        ApplyWorldContainment();

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

        // Edge scroll: only when mouse is not near the bottom (card hand area)
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

        // Any pan input cancels an active glide. The player always wins.
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

        // Clamp INSIDE the arena bounds (inset, not outward slack): the rig stays
        // over playable space, so the vista ring is always background, never floor.
        // Degenerate guard: tiny maps clamp to their own centre.
        float insetX = Mathf.Min(PanEdgeInset, (_boundsMax.X - _boundsMin.X) * 0.5f);
        float insetZ = Mathf.Min(PanEdgeInset, (_boundsMax.Z - _boundsMin.Z) * 0.5f);
        _desiredPosition.X = Mathf.Clamp(_desiredPosition.X,
            _boundsMin.X + insetX, _boundsMax.X - insetX);
        _desiredPosition.Z = Mathf.Clamp(_desiredPosition.Z,
            _boundsMin.Z + insetZ, _boundsMax.Z - insetZ);

        // World containment: the pivot may only stand where the whole view footprint
        // fits the terrain disc. Sized at the larger of the target and current zoom,
        // so a zoom-out already in flight is covered before it lands.
        if (ContainmentActive)
        {
            float zoomRef = Mathf.Max(_zoomTarget, CurrentZoomRef);
            float allowed = _worldRadius - WorldContainmentMargin
                - FootprintRadius(zoomRef * FovZoomScale, EffectivePitch(zoomRef));
            _desiredPosition = ClampToWorldDisc(_desiredPosition, allowed);
        }

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
        }

        // Clamp UNCONDITIONALLY (not just inside the drag branch) so a stale
        // scene-stored MaxPitch or any external pitch write is re-leashed every
        // frame. A near-level pitch makes the safety zoom floor explode.
        _pitch = Mathf.Clamp(_pitch, MinPitch, Mathf.Min(MaxPitch, -30f));

        // _pitch stays the player's preference; the pivot gets it after the zoom
        // tilt and the horizon guard (EffectivePitch).
        _pivot.RotationDegrees = new Vector3(EffectivePitch(CurrentZoomRef), _yaw, 0f);
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────
    private void HandleZoom(float delta)
    {
        // Terrain-aware floor: at the current pitch, this is the closest zoom
        // that keeps the camera's world Y above the tallest tile + clearance.
        // SAFETY WINS over the aesthetic ceiling: at shallow pitch the floor can
        // exceed _maxZoomDynamic, and C#'s Math.Clamp THROWS when min > max
        // (unlike GDScript's clamp), so the ceiling must yield to the floor.
        // World containment caps the ceiling too (pivot at the disc centre; the pan
        // clamp pulls the pivot inward from there). Safety still wins over both.
        float minSafe = MinSafeZoom();
        float ceiling = Mathf.Max(Mathf.Min(_maxZoomDynamic, ContainedZoomCeiling()), minSafe);
        _zoomTarget = Mathf.Clamp(_zoomTarget, minSafe, ceiling);

        // _zoomTarget and minSafe are reference units; the camera moves in real ones.
        float scale = FovZoomScale;
        Vector3 camPos = _camera.Position;
        camPos.Z = Mathf.Lerp(camPos.Z, _zoomTarget * scale, ZoomLerpSpeed * delta);
        // Hard floor on the actual value too: a pitch change mid-lerp must
        // never leave the eye inside the terrain for even one frame.
        camPos.Z = Mathf.Max(camPos.Z, minSafe * scale);
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
            return _maxZoomDynamic; // defensive; MaxPitch should keep us far from here

        float requiredRise = _boundsMax.Y + MinCameraClearance - Position.Y;
        if (requiredRise <= 0f)
            return MinZoom;

        // requiredRise / sin is a REAL distance; zoom values are reference units.
        return Mathf.Max(MinZoom, requiredRise / sin / FovZoomScale);
    }

    // ── Lens + world containment (camera_lens_slice1) ─────────────────────────
    /// <summary>Real camera distance per reference unit of zoom: tan(ReferenceFov / 2) /
    /// tan(fov / 2) along the camera's keep-aspect axis. 1 at the reference lens (campus,
    /// anything that never calls SetLensFov); about 2.11 for the 40 degree combat lens.</summary>
    public float FovZoomScale
    {
        get
        {
            if (!EnsureCameraNodes() || _camera.Projection != Camera3D.ProjectionType.Perspective)
                return 1f;
            float refHalf = Mathf.DegToRad(Mathf.Clamp(ReferenceFov, 1f, 170f) * 0.5f);
            float half = Mathf.DegToRad(Mathf.Clamp(_camera.Fov, 1f, 170f) * 0.5f);
            return Mathf.Tan(refHalf) / Mathf.Tan(half);
        }
    }

    /// <summary>The factor every distance-tuned effect (grass fade and LOD, canopy fade,
    /// depth tints) divides camera distance by, so it behaves the same under any lens:
    /// tan(37.5 deg) / tan(vertical half-FOV). Matches lens_k in painterly_grass and
    /// painterly_canopy exactly (they read PROJECTION_MATRIX[1][1]). 1 for the 75 degree
    /// reference lens and for orthographic cameras.</summary>
    public static float LensDistanceScale(Camera3D cam)
    {
        if (cam == null || cam.Projection != Camera3D.ProjectionType.Perspective)
            return 1f;

        float halfV = Mathf.DegToRad(Mathf.Clamp(cam.Fov, 1f, 170f) * 0.5f);
        if (cam.KeepAspect == Camera3D.KeepAspectEnum.Width)
        {
            Vector2 size = cam.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
            float aspect = size.Y > 1f ? size.X / size.Y : 16f / 9f;
            halfV = Mathf.Atan(Mathf.Tan(halfV) / aspect);
        }
        return Mathf.Max(0.767327f / Mathf.Tan(halfV), 0.05f);
    }

    /// <summary>Sets the Camera3D lens (degrees along its keep-aspect axis) and rescales the
    /// current distance so the view keeps its framing. Safe at any time.</summary>
    public void SetLensFov(float fovDeg)
    {
        if (!EnsureCameraNodes() || fovDeg <= 1f || fovDeg >= 170f)
            return;

        float zoomRef = CurrentZoomRef;   // measured under the old lens
        _camera.Fov = fovDeg;
        _lensManaged = true;
        Vector3 camPos = _camera.Position;
        camPos.Z = zoomRef * FovZoomScale;
        _camera.Position = camPos;
    }

    /// <summary>How much world exists to look at: a disc of terrain (centre XZ, radius)
    /// whose lowest surface is floorY. While set, the view footprint is kept inside it.
    /// FrameGrid clears it, so call this AFTER FrameGrid (CenterCameraOverGrid does).</summary>
    public void SetWorldExtent(Vector3 center, float radius, float floorY)
    {
        _worldCenterXZ = new Vector2(center.X, center.Z);
        _worldRadius = Mathf.Max(radius, 0f);
        _worldFloorY = floorY;
        _hasWorldExtent = _worldRadius > 0.5f;
        _containWarned = false;
    }

    private bool ContainmentActive => EnforceWorldContainment && _hasWorldExtent
        && EnsureCameraNodes() && _camera.Projection == Camera3D.ProjectionType.Perspective;

    /// <summary>The camera's current distance in reference units.</summary>
    private float CurrentZoomRef => _camera.Position.Z / FovZoomScale;

    /// <summary>Half-FOV tangents (horizontal, vertical) for the live viewport aspect.</summary>
    private Vector2 HalfFovTangents()
    {
        float t = Mathf.Tan(Mathf.DegToRad(Mathf.Clamp(_camera.Fov, 1f, 170f) * 0.5f));
        Vector2 size = GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
        float aspect = size.Y > 1f ? size.X / size.Y : 16f / 9f;
        return _camera.KeepAspect == Camera3D.KeepAspectEnum.Width
            ? new Vector2(t, t / aspect)
            : new Vector2(t * aspect, t);
    }

    /// <summary>The pitch the pivot actually uses at a given zoom (reference units): the
    /// player's _pitch, steepened by the zoom tilt and then by the horizon guard.</summary>
    private float EffectivePitch(float zoomRef)
    {
        // Only a lens-managed rig (combat, through SetLensFov) or one with a world
        // extent gets the tilt and the guard. The guard must hold whenever containment
        // is on: sky in frame makes the footprint infinite and would pin the rig. The
        // campus does neither, so it keeps exactly the old pitch behaviour.
        if (!_lensManaged && !_hasWorldExtent)
            return Mathf.Max(_pitch, -89f);

        float shallow = Mathf.Min(MaxPitch, -30f);
        float p = _pitch;

        float span = _maxZoomDynamic - MinZoom;
        if (ZoomedOutMaxPitch < shallow && span > 0.01f)
        {
            float t = Mathf.Clamp((zoomRef - MinZoom) / span, 0f, 1f);
            float u = Mathf.Clamp((t - ZoomTiltStart) / Mathf.Max(1f - ZoomTiltStart, 0.01f), 0f, 1f);
            u = u * u * (3f - 2f * u);
            p = Mathf.Min(p, Mathf.Lerp(shallow, ZoomedOutMaxPitch, u));
        }

        if (HorizonMarginDeg >= 0f && EnsureCameraNodes()
            && _camera.Projection == Camera3D.ProjectionType.Perspective)
        {
            float halfV = Mathf.RadToDeg(Mathf.Atan(HalfFovTangents().Y));
            p = Mathf.Min(p, -(halfV + HorizonMarginDeg));
        }

        return Mathf.Max(p, -89f);
    }

    /// <summary>Largest horizontal distance from the pivot to any of the four frustum-corner
    /// ground hits (on the plane y = world floor) for a camera <paramref name="dist"/> REAL
    /// units from the pivot at <paramref name="pitchDeg"/>. Yaw-independent, because yaw
    /// spins the footprint about the pivot. Infinity when a corner ray misses the ground,
    /// which means sky is in frame.</summary>
    private float FootprintRadius(float dist, float pitchDeg)
    {
        Vector2 tan = HalfFovTangents();
        float a = Mathf.DegToRad(pitchDeg);   // negative = looking down
        float sinA = Mathf.Sin(a);
        float cosA = Mathf.Cos(a);
        float height = Mathf.Max(Position.Y - _worldFloorY, 0f) - dist * sinA;
        float back = dist * cosA;              // the camera sits behind the pivot on local +Z

        float best = 0f;
        for (int i = 0; i < 4; i++)
        {
            float x = (i & 1) == 0 ? -tan.X : tan.X;
            float y = (i & 2) == 0 ? -tan.Y : tan.Y;
            // Camera-local ray (x, y, -1), pitched about X.
            float wy = y * cosA + sinA;
            float wz = y * sinA - cosA;
            if (wy > -0.0001f)
                return float.PositiveInfinity;
            float t = height / -wy;
            float px = t * x;
            float pz = back + t * wz;
            best = Mathf.Max(best, Mathf.Sqrt(px * px + pz * pz));
        }
        return best;
    }

    /// <summary>Largest reference zoom whose footprint fits the world disc with the pivot
    /// at the disc centre (the pan clamp then pulls the pivot inward as needed). MaxValue
    /// when containment is off.</summary>
    private float ContainedZoomCeiling()
    {
        if (!ContainmentActive)
            return float.MaxValue;

        float limit = _worldRadius - WorldContainmentMargin;
        float scale = FovZoomScale;
        bool Fits(float z) => FootprintRadius(z * scale, EffectivePitch(z)) <= limit;

        // The zoom tilt makes the footprint non-monotonic in zoom (a steeper pitch can
        // shrink it while the distance grows it), so a plain bisection could skip a band
        // that does not fit. March up from MinZoom to the FIRST zoom that does not fit,
        // then bisect only inside that last step: every zoom below the result fits.
        float lo = MinZoom;
        if (!Fits(lo))
        {
            WarnContainmentOnce(lo);
            return lo;
        }

        const int Steps = 32;
        float stepSize = (_maxZoomDynamic - MinZoom) / Steps;
        if (stepSize <= 0f)
            return lo;

        float hi = -1f;
        for (int s = 1; s <= Steps; s++)
        {
            float z = MinZoom + stepSize * s;
            if (!Fits(z))
            {
                hi = z;
                break;
            }
            lo = z;
        }
        if (hi < 0f)
            return _maxZoomDynamic;   // the whole range fits

        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Fits(mid))
                lo = mid;
            else
                hi = mid;
        }

        if (lo < _maxZoomDynamic * 0.5f)
            WarnContainmentOnce(lo);
        return lo;
    }

    /// <summary>Hard guarantee on the ACTUAL rig for this frame's real zoom and pitch (the
    /// desired-position clamp in HandlePan can trail a pitch change by a frame). Moves the
    /// rig only when the footprint would cross the world edge, so it never fights normal
    /// motion.</summary>
    private void ApplyWorldContainment()
    {
        if (!ContainmentActive)
            return;

        float allowed = _worldRadius - WorldContainmentMargin
            - FootprintRadius(_camera.Position.Z, _pivot.RotationDegrees.X);
        Position = ClampToWorldDisc(Position, allowed);
    }

    private Vector3 ClampToWorldDisc(Vector3 p, float allowed)
    {
        allowed = Mathf.Max(allowed, 0f);
        var d = new Vector2(p.X - _worldCenterXZ.X, p.Z - _worldCenterXZ.Y);
        float len = d.Length();
        if (len <= allowed || len < 0.0001f)
            return p;
        d *= allowed / len;
        return new Vector3(_worldCenterXZ.X + d.X, p.Y, _worldCenterXZ.Y + d.Y);
    }

    private void WarnContainmentOnce(float ceiling)
    {
        if (_containWarned)
            return;
        _containWarned = true;
        GD.PushWarning($"[Camera] World extent (radius {_worldRadius:0.0}) is small for this lens; zoom ceiling held at "
            + $"{ceiling:0.0} of {_maxZoomDynamic:0.0}. Raise HexGridManager.FarFieldRadius.");
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
    /// <summary>Opening view: face from the party toward the enemies, centred on the line
    /// between them at OpeningPartyBias, zoomed out until every point fits but never past
    /// the opening cap (OpeningZoomSpanFactor of the board span).</summary>
    public void FrameUnits(System.Collections.Generic.IList<Vector3> party,
                           System.Collections.Generic.IList<Vector3> enemies)
    {
        if (!EnsureCameraNodes() || party == null || enemies == null || party.Count == 0 || enemies.Count == 0)
            return;

        Vector3 pc = Vector3.Zero, ec = Vector3.Zero;
        foreach (var p in party) pc += p;
        foreach (var e in enemies) ec += e;
        pc /= party.Count;
        ec /= enemies.Count;
        // Lead from the party toward the enemies, but never so far that the party drops
        // behind the card hand: the lead is capped at a fixed share of the opening camera
        // distance, which pins the party's screen height whatever the separation.
        Vector3 toFoes = ec - pc;
        float sep = new Vector2(toFoes.X, toFoes.Z).Length();
        float lead = Mathf.Min(sep * Mathf.Clamp(OpeningPartyBias, 0f, 1f), 0.2f * _openingZoomCap * FovZoomScale);
        Vector3 center = sep > 0.001f ? pc + toFoes * (lead / sep) : pc;

        float extent = 0f;
        foreach (var p in party) extent = Mathf.Max(extent, new Vector2(p.X - center.X, p.Z - center.Z).Length());
        foreach (var e in enemies) extent = Mathf.Max(extent, new Vector2(e.X - center.X, e.Z - center.Z).Length());

        FaceToward(center, center + (ec - pc));
        // Fit everyone, capped at the opening zoom (safety floor still wins).
        float fit = Mathf.Max(extent * 1.9f + 3f, MinSafeZoom());
        float zoom = Mathf.Clamp(Mathf.Min(fit, Mathf.Max(_openingZoomCap, MinSafeZoom())), MinZoom, _maxZoomDynamic);
        _zoomTarget = zoom;
        _camera.Position = new Vector3(0f, 0f, zoom * FovZoomScale);
        _pivot.RotationDegrees = new Vector3(EffectivePitch(zoom), _yaw, 0f);
        GD.Print($"[Camera] Opening view: center {center}, extent {extent:0.0}, zoom {zoom:0.0}.");
    }

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

        _pivot.RotationDegrees = new Vector3(EffectivePitch(CurrentZoomRef), _yaw, 0f);
    }
}
