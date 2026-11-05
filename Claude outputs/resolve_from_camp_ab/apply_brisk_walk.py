#!/usr/bin/env python3
# Resolve from Camp A/B, revision 1 (2026-11-03, 13:56 ruling: "we still need a
# movement animation, otherwise the whole game starts to feel like a spreadsheet").
#
# The switch's walk goes from instant to brisk: about twice the normal pace, the
# 3D pawn's glide matched to it, no pause between steps, still no stop for a
# sighting. Applies over apply_resolve_from_camp_ab.py's results. Same rules:
# every starting blob checked, every anchor exactly once, nothing written unless
# every edit succeeds, and a second run refuses.
#
# Usage: python3 apply_brisk_walk.py <repo_root> [--dry-run]

import difflib
import hashlib
import sys
from pathlib import Path

# The dashes the house style bans, built from code points so this file carries none.
EM_DASH = chr(0x2014)
EN_DASH = chr(0x2013)
ESCAPED_EM_DASH = "\\" + "u2014"


def blob(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


EDITS = [
    (
        "Scripts/Data/SaveState/PlayerSession.cs",
        "48f1e2a1f0801c418f391b082472eca2b0886209",
        [
            (
                """\
    /// claude/session_log_2026-11-03_resolve_from_camp.md). When true, a field
    /// party's march runs to its end at once. Every step is the same real move
    /// (rations, Hull, weather, patrols, ambushes and every trouble halt) with no
    /// pacing between steps, and the march does not stop for a sighting. The
    /// question it answers: is there a decision in the walk, or only time? The
    /// castle's own sorties walk as before. Not saved.</summary>
""",
                """\
    /// claude/session_log_2026-11-03_resolve_from_camp.md). When true, a field
    /// party's march walks briskly to its end, about twice the normal pace with no
    /// pause between steps, and it does not stop for a sighting. Every step is the
    /// same real move (rations, Hull, weather, patrols, ambushes and every trouble
    /// halt). The question it answers: is there a decision in the walk, or only
    /// time? The walk stays visible, because without movement the game reads as a
    /// spreadsheet. The castle's own sorties walk as before. Not saved.</summary>
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campus/CampusGuildPanel.cs",
        "0d0b622cc0b74186b8570eb75c97effd0901b546",
        [
            (
                '        Check(expedition, "Resolve from Camp (A/B: the party\'s walk is instant)", PlayerSession.DebugResolveFromCamp,\n',
                '        Check(expedition, "Resolve from Camp (A/B: a brisk walk that never stops to look)", PlayerSession.DebugResolveFromCamp,\n',
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/OverworldPartyToken.cs",
        "6efaddb9bc10e6040b6322c777ef2ad9f8b4a4ba",
        [
            (
                """\
    /// Camp A/B switch (2026-11-03) raises it while a field party marches, so each
    /// hop lands on the next frame. Only the animation changes: the move, its cost
    /// and its signals are the same, and they fire in the same order.</summary>
""",
                """\
    /// Camp A/B switch (2026-11-03) raises it while a field party marches, so the
    /// march walks briskly. Only the animation changes: the move, its cost and its
    /// signals are the same, and they fire in the same order.</summary>
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionWindow3D.cs",
        "59c5c839211320b5a9eb5b9da6c9805c28425aaf",
        [
            (
                "    private Node3D _pawn;\n",
                """\
    private Node3D _pawn;

    /// <summary>How long the pawn takes to glide one tile in a host-driven run. The
    /// host shortens it while the Resolve from Camp A/B switch walks the party
    /// briskly (2026-11-03), so the glide keeps pace with the march.</summary>
    public float PawnHopSeconds = 0.18f;
""",
            ),
            (
                '            tw.TweenProperty(_pawn, "position", target, 0.18)\n',
                '            tw.TweenProperty(_pawn, "position", target, PawnHopSeconds)\n',
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs",
        "f9bac2a479a91d2590aa04fdb09ec309372a4ed5",
        [
            (
                '                    LogRun("ab_resolve_camp", "on: the walk is instant and stops for no sighting");\n',
                '                    LogRun("ab_resolve_camp", "on: a brisk walk that stops for no sighting");\n',
            ),
            (
                """\
    /// <summary>Castle-role review, test 2 (2026-11-03): with the Resolve from Camp
    /// A/B switch on, a field party's march runs to its end at once. Each step is
    /// the same real move (rations, Hull, weather, patrols, ambushes, every trouble
    /// halt). Only the pacing goes, with the stop for a sighting. The castle's own
    /// sorties walk as before.</summary>
    private bool InstantWalk => _fieldRun && PlayerSession.DebugResolveFromCamp;

    /// <summary>How much faster the pawn's hop animates under InstantWalk: enough
    /// that every hop lands on the next frame.</summary>
    private const float InstantWalkSpeedScale = 1000f;
""",
                """\
    /// <summary>Castle-role review, test 2 (2026-11-03): with the Resolve from Camp
    /// A/B switch on, a field party's march walks briskly to its end and never stops
    /// for a sighting. Each step is the same real move (rations, Hull, weather,
    /// patrols, ambushes, every trouble halt). The walk stays visible, because
    /// without movement the game reads as a spreadsheet (ruled 2026-11-03). The
    /// castle's own sorties walk as before.</summary>
    private bool BriskWalk => _fieldRun && PlayerSession.DebugResolveFromCamp;

    /// <summary>The brisk pace: the 2D hop runs twice as fast and the 3D pawn's
    /// glide is matched to it, with no pause between steps. About nine tiles a
    /// second, against about four at the normal pace.</summary>
    private const float BriskWalkSpeedScale = 2f;
    private const float BriskHopSeconds = 0.1f;

    /// <summary>The 3D pawn's glide at the normal pace (ExpeditionWindow3D's default).</summary>
    private const float NormalHopSeconds = 0.18f;
""",
            ),
            (
                "        { ScheduleStrideTick(InstantWalk ? 0f : 0.05f); return; }\n",
                "        { ScheduleStrideTick(BriskWalk ? 0f : 0.05f); return; }\n",
            ),
            (
                """\
        // Castle-role review, test 2 (2026-11-03): under InstantWalk the march never stops
        // for a sighting, so it stops when it lands on a place on the way. That place's
        // encounter has just opened, and the party must not walk on out from under it.
        if (InstantWalk && _strideHasMoved)
""",
                """\
        // Castle-role review, test 2 (2026-11-03): under BriskWalk the march never stops
        // for a sighting, so it stops when it lands on a place on the way. That place's
        // encounter has just opened, and the party must not walk on out from under it.
        if (BriskWalk && _strideHasMoved)
""",
            ),
            (
                """\
        // Under InstantWalk the march does not stop for a sighting, so nothing is noted,
        // and the pawn's hop lands on the next frame.
        HashSet<Vector2I> seenBefore = (PlayerSession.DebugClassicExploration || InstantWalk) ? null : VisiblePoiTiles();
        _party.SpeedScale = InstantWalk ? InstantWalkSpeedScale : 1f;
""",
                """\
        // Under BriskWalk the march does not stop for a sighting, so nothing is noted,
        // and both pawns walk at the brisk pace.
        HashSet<Vector2I> seenBefore = (PlayerSession.DebugClassicExploration || BriskWalk) ? null : VisiblePoiTiles();
        _party.SpeedScale = BriskWalk ? BriskWalkSpeedScale : 1f;
        if (_window3D != null)
        {
            _window3D.PawnHopSeconds = BriskWalk ? BriskHopSeconds : NormalHopSeconds;
        }
""",
            ),
            (
                "        ScheduleStrideTick(InstantWalk ? 0f : StrideStepSeconds);\n",
                "        ScheduleStrideTick(BriskWalk ? 0f : StrideStepSeconds);\n",
            ),
            (
                """\
        if (_party != null)
        {
            _party.SpeedScale = 1f;   // a step taken after the march walks at the normal pace
        }
""",
                """\
        if (_party != null)
        {
            _party.SpeedScale = 1f;   // a step taken after the march walks at the normal pace
        }
        if (_window3D != null)
        {
            _window3D.PawnHopSeconds = NormalHopSeconds;
        }
""",
            ),
        ],
    ),
]

PAIRS = [("{", "}"), ("(", ")"), ("[", "]")]


def fail(msg: str) -> None:
    print("REFUSED: " + msg)
    sys.exit(1)


def main() -> None:
    if len(sys.argv) < 2:
        fail("usage: apply_brisk_walk.py <repo_root> [--dry-run]")
    root = Path(sys.argv[1])
    dry = "--dry-run" in sys.argv[2:]
    results = []
    for rel, expected, edits in EDITS:
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
            if not new.isascii():
                fail(f"{rel}: replacement is not ASCII")
            if EM_DASH in new or EN_DASH in new or ESCAPED_EM_DASH in new:
                fail(f"{rel}: replacement carries a dash the house style bans")
            for o, c in PAIRS:
                d_open = new.count(o) - anchor.count(o)
                d_close = new.count(c) - anchor.count(c)
                if d_open != d_close:
                    fail(f"{rel}: replacement unbalances {o}{c} ({d_open} vs {d_close})")
            text = text.replace(anchor, new, 1)
        if "InstantWalk" in text:
            fail(f"{rel}: an InstantWalk reference survived the rename")
        out = text.encode("utf-8")
        results.append((rel, path, got, blob(out), original, text, out))

    for rel, path, before, after, original, text, out in results:
        diff = list(difflib.unified_diff(original.splitlines(), text.splitlines(),
                                         fromfile="a/" + rel, tofile="b/" + rel, lineterm="", n=0))
        added = sum(1 for l in diff if l.startswith("+") and not l.startswith("+++"))
        removed = sum(1 for l in diff if l.startswith("-") and not l.startswith("---"))
        print(f"{rel}: {before[:7]} -> {after[:7]}  (+{added} -{removed})")

    if dry:
        print("DRY RUN: nothing written.")
        return

    for rel, path, before, after, original, text, out in results:
        with open(path, "wb") as fh:
            fh.write(out)
    for rel, path, before, after, original, text, out in results:
        check = blob(path.read_bytes())
        if check != after:
            fail(f"{rel}: wrote {check}, expected {after}")
    print("WRITTEN: all files verified against their expected blobs.")


if __name__ == "__main__":
    main()
