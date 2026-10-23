"""camera_lens_slice1 fix 3 (2026-10-06): playtest feedback.

Run AFTER apply_camera_lens_fix2.py:  python apply_camera_lens_fix3.py "<project root>"

  1. Grass and trees missing: lens_k used the SIGNED PROJECTION_MATRIX[1][1].
     Godot's Forward+ renderer folds a Y flip into that matrix, so the value is
     negative, lens_k clamped to 0.05, and the far fade treated every blade and
     canopy as 20x farther away (fully dithered out). Use its magnitude.
  2. Pieces tiny at the standard zoom: the opening view (FrameUnits) fitted every
     unit, which on a spread deployment is max zoom. It now centres toward the
     party (OpeningPartyBias, 0.25) and never pulls out past OpeningZoomSpanFactor
     (0.4) of the board span, so the fight opens close and the board runs off the
     screen edges. Enemies beyond the cap sit up-screen along the facing.

Exactly-one-match asserts, all-or-nothing per file.
"""
import os
import sys

root = sys.argv[1]


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


for shader in ("Assets/Shaders/painterly_grass.gdshader", "Assets/Shaders/painterly_canopy.gdshader"):
    edit(shader, [
    (
'''    // proj[1][1] = 1 / tan(vertical half-FOV) for a perspective camera; 0.767327 is
    // tan(37.5 deg), the reference half-angle. proj[3][3] is 1 for orthographic
    // passes (directional shadow maps), which keep the raw distance.
    if (!lens_compensate_distance || proj[3][3] > 0.5)
        return 1.0;
    return max(proj[1][1] * 0.767327, 0.05);
''',
'''    // |proj[1][1]| = 1 / tan(vertical half-FOV) for a perspective camera; 0.767327 is
    // tan(37.5 deg), the reference half-angle. The MAGNITUDE matters: Godot's Forward+
    // renderer folds a Y flip into PROJECTION_MATRIX, so [1][1] is negative there (the
    // signed value clamped to 0.05 and faded every blade as if it were 20x farther
    // away). proj[3][3] is 1 for orthographic passes (directional shadow maps), which
    // keep the raw distance.
    if (!lens_compensate_distance || proj[3][3] > 0.5)
        return 1.0;
    return max(abs(proj[1][1]) * 0.767327, 0.05);
'''),
    ])

CAM = "Scripts/UI/CameraController.cs"
edit(CAM, [
(
'''    [Export] public float WorldContainmentMargin = 4f;
''',
'''    [Export] public float WorldContainmentMargin = 4f;
    /// <summary>The opening view (FrameUnits) never pulls out past this fraction of the
    /// board span (reference zoom). Fitting every unit on a spread deployment meant max
    /// zoom and tiny units; enemies beyond the cap sit up-screen along the facing.</summary>
    [Export(PropertyHint.Range, "0.15,1,0.05")] public float OpeningZoomSpanFactor = 0.4f;
    /// <summary>Where the opening view centres on the party-to-enemy line: 0 = on the
    /// party, 0.5 = the midpoint (the old framing). Low values keep the party above the
    /// card hand while the camera still faces the enemies.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.05")] public float OpeningPartyBias = 0.25f;
'''),
(
'''    private bool _lensManaged = false;   // SetLensFov was called: zoom tilt + horizon guard apply
''',
'''    private bool _lensManaged = false;   // SetLensFov was called: zoom tilt + horizon guard apply
    private float _openingZoomCap = 12f;  // FrameGrid: OpeningZoomSpanFactor x board span (reference units)
'''),
(
'''        _maxZoomDynamic = Mathf.Clamp(boardSpan * MaxZoomSpanFactor, MinZoom + 4f, MaxZoom);
''',
'''        _maxZoomDynamic = Mathf.Clamp(boardSpan * MaxZoomSpanFactor, MinZoom + 4f, MaxZoom);
        _openingZoomCap = Mathf.Clamp(boardSpan * OpeningZoomSpanFactor, MinZoom, _maxZoomDynamic);
'''),
(
'''    /// <summary>Opening view: face from the party toward the enemies, centred between
    /// them, zoomed out until every point fits (within the map's zoom ceiling).</summary>
''',
'''    /// <summary>Opening view: face from the party toward the enemies, centred on the line
    /// between them at OpeningPartyBias, zoomed out until every point fits but never past
    /// the opening cap (OpeningZoomSpanFactor of the board span).</summary>
'''),
(
'''        Vector3 center = pc.Lerp(ec, 0.5f);
''',
'''        Vector3 center = pc.Lerp(ec, Mathf.Clamp(OpeningPartyBias, 0f, 1f));
'''),
(
'''        float zoom = Mathf.Clamp(Mathf.Max(extent * 1.9f + 3f, MinSafeZoom()), MinZoom, _maxZoomDynamic);
''',
'''        // Fit everyone, capped at the opening zoom (safety floor still wins).
        float fit = Mathf.Max(extent * 1.9f + 3f, MinSafeZoom());
        float zoom = Mathf.Clamp(Mathf.Min(fit, Mathf.Max(_openingZoomCap, MinSafeZoom())), MinZoom, _maxZoomDynamic);
'''),
])

print("camera_lens_slice1 fix 3 applied.")
