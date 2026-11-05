#!/usr/bin/env python3
# Provinces 7f, a follow-up to apply_provinces_desk_7f.py (docs/provinces_v1_7f_desk_and_hud.md,
# PR38, 2026-11-04). The province names draw through a FontVariation with no base font, so they
# took the project's font without the mipmapped copy that every other map label now uses. This
# gives that FontVariation the copy (WorldAtlas3D.MapLabelFont) as its base. If the copy will
# not load, MapLabelFont returns null and the names keep the default font, as before.
#
# Edits one live file surgically: Scripts/Systems/Strategic/WorldAtlas3D.Provinces.cs. Rules:
# the starting blob checked, the anchor found exactly once, added text ASCII apart from
# box-drawing characters, no banned dash, brackets balanced, the result equal to the blob it
# was validated at, nothing written unless every step succeeds, the written file verified,
# and a second run refuses.
#
# Usage: python3 apply_provinces_names_7f.py <repo_root> [--dry-run]

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

# DROP_INS: (path in the repo, blob it must have now or None for a new file, blob it is
# written at). EDITS: (path, blob now, blob after, [(anchor, replacement)]).

DROP_INS = []

EDITS = [
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.Provinces.cs",
        "f894b5f41f5faddf27557728a8936ddaa21ea861",
        "5f678cebc397d8057fcc4e8fe9b5c832a7f01670",
        [
            (
                r"""        _nameFont ??= new FontVariation { SpacingGlyph = NameGlyphSpacing };
""",
                r"""        // Provinces 7f (PR38): the names' base font is the mipmapped copy (null keeps the default).
        _nameFont ??= new FontVariation { BaseFont = MapLabelFont(), SpacingGlyph = NameGlyphSpacing };
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
        fail("usage: " + Path(__file__).name + " <repo_root> [--dry-run]")
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
    print(f"WRITTEN: all {len(drops) + len(results)} files verified against their expected blobs.")


if __name__ == "__main__":
    main()
