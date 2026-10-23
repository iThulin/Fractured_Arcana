"""camera_lens_slice1 fix 4 (2026-10-06): opening view keeps the party above the hand.

Run AFTER apply_camera_lens_fix3.py:  python apply_camera_lens_fix4.py "<project root>"

With a fixed OpeningPartyBias, a wide party-to-enemy separation (25 m and more)
pushed the party down behind the card hand. The lead from the party toward the
enemies is now capped at 0.2 x the opening camera distance, which pins the party
at roughly a third of the way up the screen whatever the separation (enemies stay
on screen up to about 30 m away on a radius-5 map).
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


edit("Scripts/UI/CameraController.cs", [
(
'''        Vector3 center = pc.Lerp(ec, Mathf.Clamp(OpeningPartyBias, 0f, 1f));
''',
'''        // Lead from the party toward the enemies, but never so far that the party drops
        // behind the card hand: the lead is capped at a fixed share of the opening camera
        // distance, which pins the party's screen height whatever the separation.
        Vector3 toFoes = ec - pc;
        float sep = new Vector2(toFoes.X, toFoes.Z).Length();
        float lead = Mathf.Min(sep * Mathf.Clamp(OpeningPartyBias, 0f, 1f), 0.2f * _openingZoomCap * FovZoomScale);
        Vector3 center = sep > 0.001f ? pc + toFoes * (lead / sep) : pc;
'''),
])

print("camera_lens_slice1 fix 4 applied.")
