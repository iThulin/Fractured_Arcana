"""camera_lens_slice1 (2026-10-06): combat lens, horizon guard, zoom tilt,
world containment, far field, lens-compensated fades and fog, and the stale
horizon_color fix.

Usage (from anywhere):  python apply_camera_lens.py "<project root>"

Every edit asserts EXACTLY one match before it is applied, and a file is only
written once all of its edits matched. Re-running on a patched tree fails the
asserts and changes nothing. The new partial HexGridManager.FarField.cs is
copied from this folder and must not already exist.
"""
import os
import shutil
import sys

root = sys.argv[1]
here = os.path.dirname(os.path.abspath(__file__))


def edit(rel, pairs):
    p = os.path.join(root, rel)
    with open(p, encoding="utf-8", newline="") as f:
        s = f.read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)
    print("ok", rel)


def insert_before(rel, anchor, block):
    """Insert block immediately before the single occurrence of anchor."""
    edit(rel, [(anchor, block + anchor)])


# ─────────────────────────────────────────────────────────────────────────────
# 1. CameraController.cs
# ─────────────────────────────────────────────────────────────────────────────
CAM = "Scripts/UI/CameraController.cs"

edit(CAM, [
# 1a. header
(
'''//                   focused unit. Deliberately NOT a hard follow:
//                   a leashed camera fights free card targeting.
''',
'''//                   focused unit. Deliberately NOT a hard follow:
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
'''),
# 1b. exports
(
'''    [Export] public float FocusLerpSpeed = 5f;
''',
'''    [Export] public float FocusLerpSpeed = 5f;

    // ── Lens + world (camera_lens_slice1) ───────────────────────────────────
    /// <summary>The lens every zoom number in this class is authored for (degrees, along
    /// the Camera3D's keep-aspect axis). The real lens lives on the Camera3D (combat sets
    /// it through SetLensFov). FovZoomScale converts, so a lens change never changes the
    /// framing. 75 is Godot's default lens, the one all existing tuning was done under.</summary>
    [Export] public float ReferenceFov = 75f;
    /// <summary>Degrees the TOP edge of the view is kept below the horizon. A pitch
    /// shallower than half the vertical FOV shows sky, which no terrain can fill.
    /// Negative disables the guard.</summary>
    [Export] public float HorizonMarginDeg = 6f;
    /// <summary>Shallowest pitch allowed at full zoom-out. Zoomed in, the player may tilt
    /// as low as MaxPitch; pulling out tilts the view toward this. The player's own pitch
    /// comes back on zoom-in. Set it equal to MaxPitch to disable the tilt.</summary>
    [Export] public float ZoomedOutMaxPitch = -45f;
    /// <summary>Point in the zoom range (0 = closest, 1 = farthest) where the zoom tilt
    /// begins. Below it the player's pitch is untouched.</summary>
    [Export(PropertyHint.Range, "0,0.95,0.05")] public float ZoomTiltStart = 0.4f;
    /// <summary>Keep the view footprint inside the terrain disc given to SetWorldExtent.
    /// Combat sets one; the campus never does, so this is inert there.</summary>
    [Export] public bool EnforceWorldContainment = true;
    /// <summary>Metres of slack kept between the view footprint and the edge of the world.</summary>
    [Export] public float WorldContainmentMargin = 4f;
'''),
# 1c. state
(
'''    private const float MouseToKeyboardRotationRatio = 200f;
''',
'''    private const float MouseToKeyboardRotationRatio = 200f;

    // World containment (SetWorldExtent): the disc of terrain that exists.
    private bool _hasWorldExtent = false;
    private Vector2 _worldCenterXZ;
    private float _worldRadius;
    private float _worldFloorY;
    private bool _containWarned = false;
'''),
# 1d. _Ready: the scene's camera distance is converted into reference units
(
'''        _zoomTarget = _camera?.Position.Z ?? 20f;
''',
'''        _zoomTarget = (_camera?.Position.Z ?? 20f) / FovZoomScale;
'''),
# 1e. FrameGrid: a new map clears the old map's world extent
(
'''        _camera.CallDeferred("make_current"); // README §8: never set Current directly
        _boundsMin = min;
        _boundsMax = max;
''',
'''        _camera.CallDeferred("make_current"); // README §8: never set Current directly
        _boundsMin = min;
        _boundsMax = max;
        _hasWorldExtent = false; // the caller re-sends it after framing (HexGridManager does)
'''),
# 1f. FrameGrid: lens-scaled start distance, effective pitch at the start zoom
(
'''        _camera.Position = new Vector3(0f, 0f, startZoom);
        _zoomTarget = startZoom;

        GD.Print($"FrameGrid center: {center}  span: {boardSpan:0.0}  startZoom: {startZoom:0.0}  maxY: {max.Y:0.0}");
''',
'''        _camera.Position = new Vector3(0f, 0f, startZoom * FovZoomScale);
        _zoomTarget = startZoom;
        _pivot.RotationDegrees = new Vector3(EffectivePitch(startZoom), _yaw, 0f);

        GD.Print($"FrameGrid center: {center}  span: {boardSpan:0.0}  startZoom: {startZoom:0.0}  maxY: {max.Y:0.0}  lens: {_camera.Fov:0}deg x{FovZoomScale:0.00}");
'''),
# 1g. _Process: hard containment after this frame's pan/rotation/zoom
(
'''        HandlePan(dt);
        HandleRotation(dt);
        HandleZoom(dt);
''',
'''        HandlePan(dt);
        HandleRotation(dt);
        HandleZoom(dt);
        ApplyWorldContainment();
'''),
# 1h. HandlePan: containment clamp on the desired position
(
'''        _desiredPosition.Z = Mathf.Clamp(_desiredPosition.Z,
            _boundsMin.Z + insetZ, _boundsMax.Z - insetZ);
''',
'''        _desiredPosition.Z = Mathf.Clamp(_desiredPosition.Z,
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
'''),
# 1i. HandleRotation: the pivot gets the effective pitch
(
'''        _pitch = Mathf.Clamp(_pitch, MinPitch, Mathf.Min(MaxPitch, -30f));

        _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);
''',
'''        _pitch = Mathf.Clamp(_pitch, MinPitch, Mathf.Min(MaxPitch, -30f));

        // _pitch stays the player's preference; the pivot gets it after the zoom
        // tilt and the horizon guard (EffectivePitch).
        _pivot.RotationDegrees = new Vector3(EffectivePitch(CurrentZoomRef), _yaw, 0f);
'''),
# 1j. HandleZoom: containment ceiling + lens scale on the real distance
(
'''        float minSafe = MinSafeZoom();
        float ceiling = Mathf.Max(_maxZoomDynamic, minSafe);
        _zoomTarget = Mathf.Clamp(_zoomTarget, minSafe, ceiling);

        Vector3 camPos = _camera.Position;
        camPos.Z = Mathf.Lerp(camPos.Z, _zoomTarget, ZoomLerpSpeed * delta);
        // Hard floor on the actual value too: a pitch change mid-lerp must
        // never leave the eye inside the terrain for even one frame.
        camPos.Z = Mathf.Max(camPos.Z, minSafe);
        _camera.Position = camPos;
''',
'''        // World containment caps the ceiling too (pivot at the disc centre; the pan
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
'''),
# 1k. MinSafeZoom returns reference units
(
'''        return Mathf.Max(MinZoom, requiredRise / sin);
''',
'''        // requiredRise / sin is a REAL distance; zoom values are reference units.
        return Mathf.Max(MinZoom, requiredRise / sin / FovZoomScale);
'''),
# 1l. FrameUnits: lens-scaled distance
(
'''        _camera.Position = new Vector3(0f, 0f, zoom);
''',
'''        _camera.Position = new Vector3(0f, 0f, zoom * FovZoomScale);
'''),
# 1m. FaceToward: effective pitch
(
'''        _yaw = targetYaw + 180f;

        _pivot.RotationDegrees = new Vector3(_pitch, _yaw, 0f);
''',
'''        _yaw = targetYaw + 180f;

        _pivot.RotationDegrees = new Vector3(EffectivePitch(CurrentZoomRef), _yaw, 0f);
'''),
])

CAM_HELPERS = '''    // ── Lens + world containment (camera_lens_slice1) ─────────────────────────
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

    /// <summary>Sets the Camera3D lens (degrees along its keep-aspect axis) and rescales the
    /// current distance so the view keeps its framing. Safe at any time.</summary>
    public void SetLensFov(float fovDeg)
    {
        if (!EnsureCameraNodes() || fovDeg <= 1f || fovDeg >= 170f)
            return;

        float zoomRef = CurrentZoomRef;   // measured under the old lens
        _camera.Fov = fovDeg;
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

        float lo = MinZoom;
        float hi = _maxZoomDynamic;
        if (Fits(hi))
            return hi;
        if (!Fits(lo))
        {
            WarnContainmentOnce(lo);
            return lo;
        }

        for (int i = 0; i < 20; i++)
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
            + $"{ceiling:0.0} of {_maxZoomDynamic:0.0}. Enable HexGridManager.EnableFarField or widen the vista.");
    }

'''

# Insert before the Helpers banner (prefix match; the banner line is unique).
insert_before(CAM, "    // ── Helpers ─", CAM_HELPERS)

# ─────────────────────────────────────────────────────────────────────────────
# 2. HexGridManager.cs: atmosphere before tile visuals, lens, far field, extent
# ─────────────────────────────────────────────────────────────────────────────
HGM = "Scripts/Systems/Combat/Terrain/HexGridManager.cs"

edit(HGM, [
(
'''        ApplyTileHeights();
        ApplyTileVisuals();
        BuildVistaMeshes();
        BuildArenaBoundary();

        if (_activeRecipe?.Atmosphere != null)
            ApplyRecipeAtmosphere(_activeRecipe.Atmosphere);
        else
            ApplyThemeAtmosphere();

        SpawnObstacleVisuals();
''',
'''        ApplyTileHeights();

        // Atmosphere BEFORE tile visuals (camera_lens_slice1). Tiles duplicate the
        // splat template in SetGeneratedMesh, so the horizon_color that ApplyHorizon
        // pushes must already be on the template, or every tile melts toward the
        // PREVIOUS fight's fog colour. ApplyHorizon only needs _lastWorldFloor, which
        // ApplyTileHeights has just computed.
        if (_activeRecipe?.Atmosphere != null)
            ApplyRecipeAtmosphere(_activeRecipe.Atmosphere);
        else
            ApplyThemeAtmosphere();
        ApplyCameraLens();        // combat lens + fog compensation (FarField partial)

        ApplyTileVisuals();
        BuildVistaMeshes();
        BuildArenaBoundary();
        BuildFarField(field);     // landscape beyond the vista (FarField partial)

        SpawnObstacleVisuals();
'''),
(
'''        controller.FrameGrid(GridBoundsMin, GridBoundsMax);
''',
'''        controller.FrameGrid(GridBoundsMin, GridBoundsMax);
        // How much world exists to look at: the far field, or the vista when the far
        // field is off. FrameGrid cleared the previous map's extent.
        controller.SetWorldExtent(WorldExtentCenter, WorldExtentRadius, WorldFloorY);
'''),
])

# ─────────────────────────────────────────────────────────────────────────────
# 3. terrain_splat.gdshader: far-field distance melt
# ─────────────────────────────────────────────────────────────────────────────
SPLAT = "Assets/Shaders/terrain_splat.gdshader"

edit(SPLAT, [
(
'''uniform float horizon_skirt_strength : hint_range(0.0, 1.0) = 0.85;
''',
'''uniform float horizon_skirt_strength : hint_range(0.0, 1.0) = 0.85;
/** PER-INSTANCE flag, 1.0 on the far-field mesh only (HexGridManager.FarField.cs). Enables the distance melt below. Hex tiles never set it. */
instance uniform float far_field : hint_range(0.0, 1.0) = 0.0;
/** World XZ the far-field melt is measured from (the board centre). Set from code. */
uniform vec2 far_field_center = vec2(0.0, 0.0);
/** Distance from the board centre where the far-field melt begins. Set from code. */
uniform float far_melt_start = 45.0;
/** Distance where the far-field melt reaches far_melt_strength. Set from code. */
uniform float far_melt_end = 230.0;
/** Strongest far-field melt toward horizon_color. Set from code. */
uniform float far_melt_strength : hint_range(0.0, 1.0) = 0.8;
'''),
(
'''\t\talbedo = mix(albedo, horizon_color, melt);
\t}
''',
'''\t\talbedo = mix(albedo, horizon_color, melt);
\t}

\t// Far-field distance melt (camera_lens_slice1). Only the far-field mesh sets
\t// far_field. The distant ground dissolves toward the horizon colour by its
\t// distance from the board, however thin the scene fog is tuned.
\tif (far_field > 0.001) {
\t\tfloat fd = length(v_world_pos.xz - far_field_center);
\t\tfloat fmelt = smoothstep(far_melt_start, far_melt_end, fd) * far_melt_strength * far_field;
\t\talbedo = mix(albedo, horizon_color, fmelt);
\t}
'''),
])

# ─────────────────────────────────────────────────────────────────────────────
# 4. painterly_grass / painterly_canopy: lens-compensated far distances
# ─────────────────────────────────────────────────────────────────────────────
LENS_HELPER = '''// Lens compensation (camera_lens_slice1, 2026-10-06). The far-fade and depth-tint
// distances in this shader were tuned under a 75 degree lens. A narrower lens frames
// the same board from farther back, so a blade of the same SCREEN size sits at a
// larger true distance. lens_k rescales those thresholds by exactly the camera's
// zoom scale. The near fade keeps true distance: it is about the lens touching blades.
uniform bool lens_compensate_distance = true;

float lens_k(mat4 proj)
{
    // proj[1][1] = 1 / tan(vertical half-FOV) for a perspective camera; 0.767327 is
    // tan(37.5 deg), the reference half-angle. proj[3][3] is 1 for orthographic
    // passes (directional shadow maps), which keep the raw distance.
    if (!lens_compensate_distance || proj[3][3] > 0.5)
        return 1.0;
    return max(proj[1][1] * 0.767327, 0.05);
}

'''

for shader in ("Assets/Shaders/painterly_grass.gdshader", "Assets/Shaders/painterly_canopy.gdshader"):
    edit(shader, [
    (
'''void vertex()
''',
LENS_HELPER + '''void vertex()
'''),
    (
'''        float dt = smoothstep(depth_start, depth_end, v_view_dist) * depth_strength;
''',
'''        float dt = smoothstep(depth_start, depth_end, v_view_dist / lens_k(PROJECTION_MATRIX)) * depth_strength;
'''),
    (
'''        fade = 1.0 - smoothstep(fade_start, fade_end, v_view_dist);
''',
'''        fade = 1.0 - smoothstep(fade_start, fade_end, v_view_dist / lens_k(PROJECTION_MATRIX));
'''),
    ])

# ─────────────────────────────────────────────────────────────────────────────
# 5. New partial: HexGridManager.FarField.cs
# ─────────────────────────────────────────────────────────────────────────────
FF = "Scripts/Systems/Combat/Terrain/HexGridManager.FarField.cs"
dst = os.path.join(root, FF)
assert not os.path.exists(dst), f"{FF} already exists; refusing to overwrite"
shutil.copyfile(os.path.join(here, "HexGridManager.FarField.cs"), dst)
print("ok", FF, "(new)")

print("camera_lens_slice1 applied.")
