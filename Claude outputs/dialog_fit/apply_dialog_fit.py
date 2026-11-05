#!/usr/bin/env python3
# Dialogs that keep their buttons on the screen (2026-11-05). "Take the field" grew taller
# than the screen and lost its Go button: a dialog sizes itself from its contents, measured
# a wrapping label while it was still narrow, grew to fit and never shrank back. The same
# trap sat in "Post here", the scrying table and the scry desk's message card.
#
# Drops in one new file kept beside this script (Scripts/UI/DialogFit.cs: Wrap gives a
# wrapping label a real width, PopupFixed pops a dialog at a fixed size) and edits three live
# files surgically (StrategicView.cs, ExpeditionManager.cs, ExpeditionManager.ScryDesk.cs).
#
# Rules: every starting blob checked (a new file must not exist yet), every anchor found
# exactly once, added text ASCII apart from box-drawing characters, no banned dash,
# brackets balanced per edit, every result equal to the blob it was validated at, nothing
# written unless every step succeeds, every written file verified, and a second run
# refuses.
#
# Usage: python3 apply_dialog_fit.py <repo_root> [--dry-run]

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

DROP_INS = [
    ("Scripts/UI/DialogFit.cs", None, "cfd9c21923c55936a797c626c536840d8a2ec155"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "707a338c535437a032bb95f93e005157dd3dc3c9",
        "b9e051f48efad89982c3a111a7599eb1fab19579",
        [
            (
                r"""                 + $"The dive costs about {WorldClock.DaysPerRation:0.##} day per ration eaten, charged when they return.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
""",
                r"""                 + $"The dive costs about {WorldClock.DaysPerRation:0.##} day per ration eaten, charged when they return.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(DialogFit.TextWidth, 0f),
""",
            ),
            (
                r"""                 + "that watch the roads.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
""",
                r"""                 + "that watch the roads.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(DialogFit.TextWidth, 0f),
""",
            ),
            (
                r"""            PlayerSession.AudienceKingdomId = "";
            dlg.QueueFree();
        };
        AddChild(dlg);
        dlg.PopupCentered();
""",
                r"""            PlayerSession.AudienceKingdomId = "";
            dlg.QueueFree();
        };
        // A fixed size. Sizing itself, the dialog could measure its wrapping
        // labels too narrow, grow past the bottom of the screen and take the
        // Go button with it (DialogFit). The companion list scrolls instead.
        DialogFit.PopupFixed(this, dlg, new Vector2I(560, 520));
""",
            ),
            (
                r"""        var detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
""",
                r"""        var detail = DialogFit.Wrap(new Label());
""",
            ),
            (
                r"""        var vocLine = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
""",
                r"""        var vocLine = DialogFit.Wrap(new Label());
""",
            ),
            (
                r"""                  + "A detachment does not move: collect it by standing with it, or recall it through a waystone.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
""",
                r"""                  + "A detachment does not move: collect it by standing with it, or recall it through a waystone.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(DialogFit.TextWidth, 0f),
""",
            ),
            (
                r"""        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void OnCollectPressed()""",
                r"""        dlg.Canceled += () => dlg.QueueFree();
        // A fixed size, so the buttons stay on the screen (DialogFit).
        DialogFit.PopupFixed(this, dlg, new Vector2I(560, 640));
    }

    private void OnCollectPressed()""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs",
        "2b18da456b77ed4227d53116dd2c9d14788f1501",
        "d3d5fcf9d4f350588f83708aa1f4ec521a23b4ed",
        [
            (
                r"""                 + "one holds where it stands.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
""",
                r"""                 + "one holds where it stands.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(DialogFit.TextWidth, 0f),
""",
            ),
            (
                r"""        var statusLbl = new Label { Text = status, AutowrapMode = TextServer.AutowrapMode.WordSmart };
""",
                r"""        // A real width to be measured at, or the dialog sizes itself from a
        // label one character wide and runs off the screen (DialogFit).
        var statusLbl = DialogFit.Wrap(new Label { Text = status }, DialogFit.RowTextWidth);
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionManager.ScryDesk.cs",
        "583badd15476d2b561f2ccc0739d4db915c2b4d1",
        "40a08458ce0d0182a530ee777b835f680d0fb605",
        [
            (
                r"""        var title = new Label { Text = msg.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
""",
                r"""        // Both labels get a real width to be measured at, or the card sizes
        // itself from text one character wide and runs off the screen with its
        // button (DialogFit). The card still fits its height to the message.
        var title = DialogFit.Wrap(new Label { Text = msg.Title });
""",
            ),
            (
                r"""        var body = new Label { Text = msg.Body, AutowrapMode = TextServer.AutowrapMode.WordSmart };
""",
                r"""        var body = DialogFit.Wrap(new Label { Text = msg.Body });
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
