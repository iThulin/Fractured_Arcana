#!/usr/bin/env python3
# Provinces, the province readout (docs/provinces_v1.md section 7c, 2026-11-03):
# pointing at painted land on the strategic map shows the province there, the
# guild's influence against the hold line, and what the next lunation does to it;
# the Reach lens gets a key under the lens buttons.
#
# Drops in four files kept beside this script (WorldAtlas3D.Hover.cs and
# StrategicView.ProvinceHover.cs, new; ProvinceInfluence.cs and
# ProvinceSaveAssert.cs, which replace the PV2a versions) and edits two live files
# surgically (StrategicView.cs, WorldAtlas3D.cs). Rules: every starting blob checked
# (a new file must not exist yet), every anchor found exactly once, added text ASCII
# apart from box-drawing characters, no banned dash, brackets balanced per edit,
# every result equal to the blob it was validated at, nothing written unless every
# step succeeds, every written file verified, and a second run refuses.
#
# Usage: python3 apply_province_readout_7c.py <repo_root> [--dry-run]

import collections
import difflib
import hashlib
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# The dashes the house style bans, built from code points so this file carries none.
EM_DASH = chr(0x2014)
EN_DASH = chr(0x2013)
ESCAPED_EM_DASH = "\\" + "u2014"


def blob(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


PAIRS = [("(", ")"), ("[", "]"), ("{", "}")]

# (path in the repo, blob it must have now or None for a new file, blob it is written at)
DROP_INS = [
    ("Scripts/Systems/Strategic/WorldAtlas3D.Hover.cs", None, "df33bf492f2ec92863056cf0df7ef45d171c1c02"),
    ("Scripts/Systems/Strategic/StrategicView.ProvinceHover.cs", None, "7106b00f5949130fd0e6c6e34a3cffb388251539"),
    ("Scripts/Systems/Strategic/ProvinceInfluence.cs", "8941d5ef92ea2e6aeeda110e8839302ca06842ae", "f0c5a8f953aae376fd47b363f963751d69fcabed"),
    ("Scripts/Systems/Verification/ProvinceSaveAssert.cs", "ac13cb781a1c49a98e90d55d16a29ae97b44f6d9", "a44eca949279fdc8d905623f4590aeae72c577b8"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.cs",
        "7f1591f75a5e21adacba8329182af8f944356ca6",
        "6da524d069f9e864eec681f426a8a809973a3c14",
        [
            (
                r"""        // macOS trackpad: pinch to zoom (a Mac trackpad never sends wheel events, so
""",
                r"""        // The tile under the pointer, for the strategic view's province readout
        // (docs/provinces_v1.md section 7c). Drags and orbits are taken above.
        else if (ev is InputEventMouseMotion hoverMotion)
            UpdateHover(hoverMotion.Position);
        // macOS trackpad: pinch to zoom (a Mac trackpad never sends wheel events, so
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "13bb0443309c6815b7c9c5e1859cf8ce009d3743",
        "db22b78a0dd58d38c0593409f1be8377c5bbc99d",
        [
            (
                r"""        var container = new SubViewportContainer { Stretch = true, Name = "Atlas3DView" };
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
""",
                r"""        var container = new SubViewportContainer { Stretch = true, Name = "Atlas3DView" };
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        container.MouseExited += OnAtlasPointerLeft;   // provinces 7c: the province readout
""",
            ),
            (
                r"""        _atlas3D.MapDoubleClicked += OnMapDoubleClicked;   // 2026-09-29: deselect and pull back
""",
                r"""        _atlas3D.MapDoubleClicked += OnMapDoubleClicked;   // 2026-09-29: deselect and pull back
        _atlas3D.TileHovered += OnAtlasTileHovered;   // provinces 7c: the province readout
""",
            ),
            (
                r"""        lensMargin.AddChild(row);

        AddLensButton(row, "Political", StrategicLens.Political);
        AddLensButton(row, "Terrain", StrategicLens.Terrain);
        AddLensButton(row, "Corruption", StrategicLens.Corruption);
        AddLensButton(row, "Reach", StrategicLens.Reach);
""",
                r"""        // Provinces 7c: the lens row, and under it the Reach lens's key.
        var lensStack = new VBoxContainer();
        lensStack.AddThemeConstantOverride("separation", 6);
        lensMargin.AddChild(lensStack);
        lensStack.AddChild(row);

        AddLensButton(row, "Political", StrategicLens.Political);
        AddLensButton(row, "Terrain", StrategicLens.Terrain);
        AddLensButton(row, "Corruption", StrategicLens.Corruption);
        AddLensButton(row, "Reach", StrategicLens.Reach);
        lensStack.AddChild(BuildReachKey());
""",
            ),
            (
                r"""            btn.Modulate = active ? Colors.White : new Color(1f, 1f, 1f, 0.55f);
        }
    }
""",
                r"""            btn.Modulate = active ? Colors.White : new Color(1f, 1f, 1f, 0.55f);
        }
        UpdateReachKey();   // provinces 7c: the Reach lens's key
    }
""",
            ),
            (
                r"""        PollChronicle(delta);
""",
                r"""        TickProvinceHover(delta);   // provinces 7c: the province readout
        PollChronicle(delta);
""",
            ),
        ],
    ),
]


def fail(msg: str) -> None:
    print("REFUSED: " + msg)
    sys.exit(1)


def added_chars_ok(anchor: str, new: str):
    """Characters the edit adds must be ASCII or box-drawing (U+2500..U+257F)."""
    extra = collections.Counter(c for c in new if ord(c) > 127)
    extra.subtract(collections.Counter(c for c in anchor if ord(c) > 127))
    return [c for c, k in extra.items() if k > 0 and not (0x2500 <= ord(c) <= 0x257F)]


def check_text(name: str, text: str) -> None:
    if "\r" in text:
        fail(f"{name} has CR characters")
    bad = sorted({c for c in text if ord(c) > 127 and not (0x2500 <= ord(c) <= 0x257F)})
    if bad:
        fail(f"{name} carries characters outside ASCII and box drawing: {bad!r}")
    if EM_DASH in text or ESCAPED_EM_DASH in text or EN_DASH in text:
        fail(f"{name} carries a dash the house style bans")


def main() -> None:
    if len(sys.argv) < 2:
        fail("usage: apply_province_readout_7c.py <repo_root> [--dry-run]")
    root = Path(sys.argv[1])
    dry = "--dry-run" in sys.argv[2:]

    drops = []
    for rel, before, after in DROP_INS:
        target = root / rel
        if before is None:
            if target.exists():
                fail(f"{rel} already exists (blob {blob(target.read_bytes())})")
            got = None
        else:
            if not target.exists():
                fail(f"{rel} is missing")
            got = blob(target.read_bytes())
            if got != before:
                fail(f"{rel} is at blob {got}, expected {before}")
        source = HERE / Path(rel).name
        if not source.exists():
            fail(f"{source.name} is missing beside this script")
        data = source.read_bytes()
        check_text(source.name, data.decode("utf-8"))
        if blob(data) != after:
            fail(f"{source.name} is at blob {blob(data)}, expected {after}")
        drops.append((rel, target, data, got, after))

    results = []
    for rel, expected, expected_after, edits in EDITS:
        path = root / rel
        data = path.read_bytes()
        got = blob(data)
        if got != expected:
            fail(f"{rel} is at blob {got}, expected {expected}")
        text = data.decode("utf-8")
        if "\r" in text:
            fail(f"{rel} has CR characters; this script writes LF only")
        original = text
        for anchor, new in edits:
            count = text.count(anchor)
            if count != 1:
                fail(f"{rel}: anchor found {count} times: {anchor.splitlines()[0]!r}")
            bad = added_chars_ok(anchor, new)
            if bad:
                fail(f"{rel}: replacement adds characters outside ASCII and box drawing: {bad!r}")
            if EM_DASH in new or EN_DASH in new or ESCAPED_EM_DASH in new:
                fail(f"{rel}: replacement carries a dash the house style bans")
            for o, c in PAIRS:
                d_open = new.count(o) - anchor.count(o)
                d_close = new.count(c) - anchor.count(c)
                if d_open != d_close:
                    fail(f"{rel}: replacement unbalances {o}{c} ({d_open} vs {d_close})")
            text = text.replace(anchor, new, 1)
        out = text.encode("utf-8")
        if blob(out) != expected_after:
            fail(f"{rel}: the edited file is at blob {blob(out)}, not the {expected_after} it was validated at")
        results.append((rel, path, got, expected_after, original, text, out))

    for rel, target, data, before, after in drops:
        print(f"{rel}: {'new' if before is None else before[:7]} -> {after[:7]}")
    for rel, path, before, after, original, text, out in results:
        diff = list(difflib.unified_diff(original.splitlines(), text.splitlines(),
                                         fromfile="a/" + rel, tofile="b/" + rel, lineterm="", n=0))
        added = sum(1 for l in diff if l.startswith("+") and not l.startswith("+++"))
        removed = sum(1 for l in diff if l.startswith("-") and not l.startswith("---"))
        print(f"{rel}: {before[:7]} -> {after[:7]}  (+{added} -{removed})")

    if dry:
        print("DRY RUN: nothing written.")
        return

    for rel, target, data, before, after in drops:
        target.parent.mkdir(parents=True, exist_ok=True)
        with open(target, "wb") as fh:
            fh.write(data)
    for rel, path, before, after, original, text, out in results:
        with open(path, "wb") as fh:
            fh.write(out)
    for rel, target, data, before, after in drops:
        if blob(target.read_bytes()) != after:
            fail(f"{rel}: did not verify after writing")
    for rel, path, before, after, original, text, out in results:
        if blob(path.read_bytes()) != after:
            fail(f"{rel}: did not verify after writing")
    print("WRITTEN: all six files verified against their expected blobs.")


if __name__ == "__main__":
    main()
