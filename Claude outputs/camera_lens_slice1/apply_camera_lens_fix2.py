"""camera_lens_slice1 fix 2 (2026-10-06): second review pass on fix 1.

Run AFTER apply_camera_lens_fix1.py:  python apply_camera_lens_fix2.py "<project root>"

  1. CameraController: the horizon guard (and tilt) also apply whenever a world
     extent is set. With CameraLensFov = 0 and the far field on, a 75 degree lens
     at -35 has sky in frame, the footprint is infinite, and containment would
     pin the rig. Campus behaviour is still untouched (it sets neither).
  2. FarField: the tucked-strip min only acts across a real jump (more than one
     height step), so ordinary rim edges keep their shallow dip.
  3. Docs and the containment warning text brought up to date.

Exactly-one-match asserts, all-or-nothing per file, md5-guarded drop-in.
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


CAM = "Scripts/UI/CameraController.cs"
edit(CAM, [
(
'''        // Only a lens-managed rig (combat, through SetLensFov) gets the tilt and the
        // guard. The campus and every other user keep exactly the old pitch behaviour.
        if (!_lensManaged)
            return Mathf.Max(_pitch, -89f);
''',
'''        // Only a lens-managed rig (combat, through SetLensFov) or one with a world
        // extent gets the tilt and the guard. The guard must hold whenever containment
        // is on: sky in frame makes the footprint infinite and would pin the rig. The
        // campus does neither, so it keeps exactly the old pitch behaviour.
        if (!_lensManaged && !_hasWorldExtent)
            return Mathf.Max(_pitch, -89f);
'''),
(
'''    /// Negative disables the guard.</summary>
''',
'''    /// Negative disables the guard. Applies once SetLensFov or SetWorldExtent has been
    /// called (combat does both); the campus calls neither.</summary>
'''),
(
'''    /// comes back on zoom-in. Set it equal to MaxPitch to disable the tilt.</summary>
''',
'''    /// comes back on zoom-in. Set it equal to MaxPitch to disable the tilt. Applies once
    /// SetLensFov or SetWorldExtent has been called, like the horizon guard.</summary>
'''),
(
'''Enable HexGridManager.EnableFarField or widen the vista.");
''',
'''Raise HexGridManager.FarFieldRadius.");
'''),
])

FF = "Scripts/Systems/Combat/Terrain/HexGridManager.FarField.cs"
dst = os.path.join(root, FF)
FIX1_MD5 = "97a01cfe6d1efdb7226701502141a8e8"   # installed by fix 1
FIX2_MD5 = "bedef37b639e2ee75a5e4b8eec358161"   # this folder's copy (fix-2 version)
with open(dst, "rb") as f:
    got = hashlib.md5(f.read()).hexdigest()
if got == FIX2_MD5:
    print("ok", FF, "(already the fix-2 drop-in)")
else:
    assert got == FIX1_MD5, f"{FF} changed since fix 1 (md5 {got}); not overwriting"
    shutil.copyfile(os.path.join(here, "HexGridManager.FarField.cs"), dst)
    print("ok", FF, "(replaced with fix-2 drop-in)")

print("camera_lens_slice1 fix 2 applied.")
