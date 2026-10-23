"""camera_lens_slice1 fix 1 (2026-10-06): review findings on the applied slice.

Run AFTER apply_camera_lens.py:  python apply_camera_lens_fix1.py "<project root>"

  1. GrassLodController: blade LOD measured in reference-lens distance (it was
     culling grass the lens-compensated shader still draws).
  2. Far field off = containment off (the vista alone pinned the camera).
     Containment ceiling search is monotone-safe under the zoom tilt.
  3. Water in the far field sinks under the backdrop plane (was a grass plain).
  4. Outline: 4 samples per edge (hits the midpoint kink); tucked strip takes a
     3-tap min so it cannot slice up through a cliff between outline tiles.
  5. Sun shadow max distance scales with the lens (cached base, no compounding).
  6. Campus untouched: zoom tilt + horizon guard only on a lens-managed rig.
  7. terrain_splat: the far field always counts as above the waterline.
  8. FrameUnits sets the pivot pitch for the new zoom (no one-frame pop).
  9. Shader inspector grouping; two comments corrected.

Every edit asserts exactly one match; a file is written only when all of its
edits matched. HexGridManager.FarField.cs is replaced only if it is still the
exact file the first script installed (md5 check), so a hand edit is never lost.
"""
import hashlib
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


# ─────────────────────────────────────────────────────────────────────────────
# CameraController.cs
# ─────────────────────────────────────────────────────────────────────────────
CAM = "Scripts/UI/CameraController.cs"
edit(CAM, [
(
'''    private bool _containWarned = false;
''',
'''    private bool _containWarned = false;
    private bool _lensManaged = false;   // SetLensFov was called: zoom tilt + horizon guard apply
'''),
(
'''    /// <summary>Sets the Camera3D lens (degrees along its keep-aspect axis) and rescales the
''',
'''    /// <summary>The factor every distance-tuned effect (grass fade and LOD, canopy fade,
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
'''),
(
'''        float zoomRef = CurrentZoomRef;   // measured under the old lens
        _camera.Fov = fovDeg;
''',
'''        float zoomRef = CurrentZoomRef;   // measured under the old lens
        _camera.Fov = fovDeg;
        _lensManaged = true;
'''),
(
'''    private float EffectivePitch(float zoomRef)
    {
        float shallow = Mathf.Min(MaxPitch, -30f);
''',
'''    private float EffectivePitch(float zoomRef)
    {
        // Only a lens-managed rig (combat, through SetLensFov) gets the tilt and the
        // guard. The campus and every other user keep exactly the old pitch behaviour.
        if (!_lensManaged)
            return Mathf.Max(_pitch, -89f);

        float shallow = Mathf.Min(MaxPitch, -30f);
'''),
(
'''        float lo = MinZoom;
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
''',
'''        // The zoom tilt makes the footprint non-monotonic in zoom (a steeper pitch can
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
'''),
(
'''        _camera.Position = new Vector3(0f, 0f, zoom * FovZoomScale);
''',
'''        _camera.Position = new Vector3(0f, 0f, zoom * FovZoomScale);
        _pivot.RotationDegrees = new Vector3(EffectivePitch(zoom), _yaw, 0f);
'''),
])

# ─────────────────────────────────────────────────────────────────────────────
# GrassLodController.cs
# ─────────────────────────────────────────────────────────────────────────────
LOD = "Scripts/Systems/Combat/Terrain/GrassLodController.cs"
edit(LOD, [
(
'''// LOD curve (distances are camera -> closest point of the chunk AABB):
''',
'''// LOD curve (distances are camera -> closest point of the chunk AABB,
// divided by CameraController.LensDistanceScale so they track the shader
// fade under any lens; camera_lens_slice1):
'''),
(
'''        Vector3 camPos = cam.GlobalPosition;

        float span = Mathf.Max(0.001f, EndDistance - FullDistance);
''',
'''        Vector3 camPos = cam.GlobalPosition;
        // Lens compensation: FullDistance/EndDistance are tuned for a 75 degree lens,
        // like the shader fade they must stay behind. A narrower lens sits the camera
        // farther back for the same framing, so measure in reference-lens terms.
        float lens = CameraController.LensDistanceScale(cam);

        float span = Mathf.Max(0.001f, EndDistance - FullDistance);
'''),
(
'''            float d = camPos.DistanceTo(nearest);
''',
'''            float d = camPos.DistanceTo(nearest) / lens;
'''),
])

# ─────────────────────────────────────────────────────────────────────────────
# Shaders
# ─────────────────────────────────────────────────────────────────────────────
edit("Assets/Shaders/terrain_splat.gdshader", [
(
'''\tfloat above_water = smoothstep(grid_fade_below_y - grid_fade_span, grid_fade_below_y, v_world_pos.y);
''',
'''\t// The far field always counts as dry land (its water sinks under the backdrop plane).
\tfloat above_water = max(smoothstep(grid_fade_below_y - grid_fade_span, grid_fade_below_y, v_world_pos.y), far_field);
'''),
])

for shader in ("Assets/Shaders/painterly_grass.gdshader", "Assets/Shaders/painterly_canopy.gdshader"):
    edit(shader, [
    (
'''uniform bool lens_compensate_distance = true;
''',
'''group_uniforms;
uniform bool lens_compensate_distance = true;
'''),
    ])

# ─────────────────────────────────────────────────────────────────────────────
# HexGridManager.cs comments
# ─────────────────────────────────────────────────────────────────────────────
edit("Scripts/Systems/Combat/Terrain/HexGridManager.cs", [
(
'''        // Atmosphere BEFORE tile visuals (camera_lens_slice1). Tiles duplicate the
        // splat template in SetGeneratedMesh, so the horizon_color that ApplyHorizon
        // pushes must already be on the template, or every tile melts toward the
        // PREVIOUS fight's fog colour. ApplyHorizon only needs _lastWorldFloor, which
        // ApplyTileHeights has just computed.
''',
'''        // Atmosphere BEFORE the vista meshes (camera_lens_slice1). Tiles duplicate the
        // splat template in SetGeneratedMesh, so the horizon_color that ApplyHorizon
        // pushes must already be on the template, or every VISTA tile melts toward the
        // PREVIOUS fight's fog colour. (Playable tiles duplicate earlier, inside
        // ApplyTileHeights, but never read horizon_color.) ApplyHorizon only needs
        // _lastWorldFloor, which ApplyTileHeights has just computed.
'''),
(
'''        // How much world exists to look at: the far field, or the vista when the far
        // field is off. FrameGrid cleared the previous map's extent.
''',
'''        // How much world exists to look at: the far field (radius 0 turns containment
        // off when no far field was built). FrameGrid cleared the previous map's extent.
'''),
])

# ─────────────────────────────────────────────────────────────────────────────
# HexGridManager.FarField.cs (Claude-authored drop-in): replace only if untouched
# ─────────────────────────────────────────────────────────────────────────────
FF = "Scripts/Systems/Combat/Terrain/HexGridManager.FarField.cs"
dst = os.path.join(root, FF)
FIRST_MD5 = "a09556cee9c9fff0d1187530f918b007"   # as installed by apply_camera_lens.py (first version)
FIX1_MD5 = "97a01cfe6d1efdb7226701502141a8e8"    # fix-1 version
LATER_MD5 = ("bedef37b639e2ee75a5e4b8eec358161",)  # later versions this folder may hold (fix 2)
with open(dst, "rb") as f:
    got = hashlib.md5(f.read()).hexdigest()
if got == FIX1_MD5 or got in LATER_MD5:
    print("ok", FF, "(already the fix-1 drop-in or newer)")
else:
    assert got == FIRST_MD5, f"{FF} changed since the first install (md5 {got}); not overwriting"
    shutil.copyfile(os.path.join(here, "HexGridManager.FarField.cs"), dst)
    print("ok", FF, "(replaced with fix-1 drop-in)")

print("camera_lens_slice1 fix 1 applied.")
