#!/usr/bin/env python3
# Resolve from Camp A/B switch (castle-role review, test 2, 2026-11-03).
#
# Surgical edits over the live Mac checkout (uncommitted O3b, O3c and L4b1
# included). Every file must match its expected starting blob and every anchor
# must occur exactly once, or nothing is written. A second run refuses, because
# the blobs no longer match.
#
# Usage: python3 apply_resolve_from_camp_ab.py <repo_root> [--dry-run]

import difflib
import hashlib
import sys
from pathlib import Path


def blob(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


EDITS = [
    (
        "Scripts/Data/SaveState/PlayerSession.cs",
        "adfb255d99267b340684627ff1a2d4b547a09ce4",
        [
            (
                "    public static bool DebugClassicExploration = false;\n",
                """\
    public static bool DebugClassicExploration = false;

    /// <summary>Debug A/B switch for test 2 of the castle-role review (2026-11-03,
    /// claude/session_log_2026-11-03_resolve_from_camp.md). When true, a field
    /// party's march runs to its end at once. Every step is the same real move
    /// (rations, Hull, weather, patrols, ambushes and every trouble halt) with no
    /// pacing between steps, and the march does not stop for a sighting. The
    /// question it answers: is there a decision in the walk, or only time? The
    /// castle's own sorties walk as before. Not saved.</summary>
    public static bool DebugResolveFromCamp = false;
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campus/CampusGuildPanel.cs",
        "9ecb1a9f79270ea36d2736c9f113dc7f949e6cf3",
        [
            (
                "        expedition.AddChild(_forceEncounterDropdown);\n"
                "        expedition.AddChild(new Control());\n",
                """\
        expedition.AddChild(_forceEncounterDropdown);
        // Castle-role review, test 2 (2026-11-03). Added after the dropdown so the
        // caption and the dropdown keep their row in the three-column grid.
        Check(expedition, "Resolve from Camp (A/B: the party's walk is instant)", PlayerSession.DebugResolveFromCamp,
            on => PlayerSession.DebugResolveFromCamp = on);
        expedition.AddChild(new Control());
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/OverworldPartyToken.cs",
        "d5c31fda8768cc33ba358e6b0a1e9d8f7987de6b",
        [
            (
                "    private float _moveSpeed = 300f; // pixels per second\n",
                """\
    private float _moveSpeed = 300f; // pixels per second

    /// <summary>Scales the walk animation. 1 is the normal pace. The Resolve from
    /// Camp A/B switch (2026-11-03) raises it while a field party marches, so each
    /// hop lands on the next frame. Only the animation changes: the move, its cost
    /// and its signals are the same, and they fire in the same order.</summary>
    public float SpeedScale { get; set; } = 1f;

""",
            ),
            (
                "            float step = _moveSpeed * (float)delta;\n",
                "            float step = _moveSpeed * SpeedScale * (float)delta;\n",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs",
        "0df6433edbd7c2cd98090e4d66b188129a7f2d8e",
        [
            (
                '                LogRun("field_party", $"{_fieldPartyId}: {ActiveRunCompanionIds().Count} afoot, "\n'
                '                                    + $"{MaxFuel} rations, window radius {WindowRadius}");\n',
                '                LogRun("field_party", $"{_fieldPartyId}: {ActiveRunCompanionIds().Count} afoot, "\n'
                '                                    + $"{MaxFuel} rations, window radius {WindowRadius}");\n'
                """\
                // Castle-role review, test 2 (2026-11-03): name the arm in the journal, so a
                // dive walked with the Resolve from Camp switch reads as one afterwards.
                if (PlayerSession.DebugResolveFromCamp)
                {
                    LogRun("ab_resolve_camp", "on: the walk is instant and stops for no sighting");
                }
""",
            ),
            (
                "    private const float StrideStepSeconds = 0.25f;   // pacing per tile (watchable)\n",
                """\
    private const float StrideStepSeconds = 0.25f;   // pacing per tile (watchable)

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
            ),
            (
                "        { ScheduleStrideTick(0.05f); return; }\n",
                "        { ScheduleStrideTick(InstantWalk ? 0f : 0.05f); return; }\n",
            ),
            (
                "        else if (_party.CurrentCoord == _strideGoal)\n"
                '        { EndStride("The castle reaches the tile you ordered.", "Arrived"); return; }\n',
                "        else if (_party.CurrentCoord == _strideGoal)\n"
                '        { EndStride("The castle reaches the tile you ordered.", "Arrived"); return; }\n'
                """\

        // Castle-role review, test 2 (2026-11-03): under InstantWalk the march never stops
        // for a sighting, so it stops when it lands on a place on the way. That place's
        // encounter has just opened, and the party must not walk on out from under it.
        if (InstantWalk && _strideHasMoved)
        {
            var ovOnTheWay = _overlay.OverlayAt(_party.CurrentCoord);
            if (IsEncounterPoi(ovOnTheWay.Poi) && !ovOnTheWay.Consumed)
            {
                EndStride("The party comes upon a place on the way.", "Something here", HaltTone.Choice);
                return;
            }
        }
""",
            ),
            (
                "        HashSet<Vector2I> seenBefore = PlayerSession.DebugClassicExploration ? null : VisiblePoiTiles();\n",
                """\
        // Under InstantWalk the march does not stop for a sighting, so nothing is noted,
        // and the pawn's hop lands on the next frame.
        HashSet<Vector2I> seenBefore = (PlayerSession.DebugClassicExploration || InstantWalk) ? null : VisiblePoiTiles();
        _party.SpeedScale = InstantWalk ? InstantWalkSpeedScale : 1f;
""",
            ),
            (
                "        ScheduleStrideTick(StrideStepSeconds);\n",
                "        ScheduleStrideTick(InstantWalk ? 0f : StrideStepSeconds);\n",
            ),
            (
                "        _strideSightNote = null;   // a pending sighting dies with the order\n",
                """\
        _strideSightNote = null;   // a pending sighting dies with the order
        if (_party != null)
        {
            _party.SpeedScale = 1f;   // a step taken after the march walks at the normal pace
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
        fail("usage: apply_resolve_from_camp_ab.py <repo_root> [--dry-run]")
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
            if "—" in new or "–" in new or "\\u2014" in new:
                fail(f"{rel}: replacement carries a dash the house style bans")
            for o, c in PAIRS:
                d_open = new.count(o) - anchor.count(o)
                d_close = new.count(c) - anchor.count(c)
                if d_open != d_close:
                    fail(f"{rel}: replacement unbalances {o}{c} ({d_open} vs {d_close})")
            text = text.replace(anchor, new, 1)
        out = text.encode("utf-8")
        results.append((rel, path, got, blob(out), original, text, out))

    for rel, path, before, after, original, text, out in results:
        added = sum(1 for l in difflib.ndiff(original.splitlines(), text.splitlines()) if l.startswith("+ "))
        removed = sum(1 for l in difflib.ndiff(original.splitlines(), text.splitlines()) if l.startswith("- "))
        print(f"{rel}: {before[:7]} -> {after[:7]}  (+{added} -{removed})")
        if dry:
            for line in difflib.unified_diff(original.splitlines(), text.splitlines(),
                                             fromfile="a/" + rel, tofile="b/" + rel, lineterm="", n=1):
                print(line)

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
