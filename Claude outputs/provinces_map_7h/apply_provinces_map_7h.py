#!/usr/bin/env python3
# The map fixes shelved at the end of 7f (docs/provinces_v1_7h_map_fixes.md, section 7h of
# the provinces spec, 2026-11-05). 1: the Register's margin notes fit their region. 2: the
# sea's glints off on the atlas. 3: a seat's name kept inside the frame on screen, the
# camera's turn included. 4: seat names at 2 percent of the screen, the pieces' size. 5: no
# "Seat" after a realm's name. 6: the pieces' names hidden at the whole-world view. 7: the
# selection panel names a force's place by province and realm, the coordinates in its
# tooltip. 8: folded, Destinations is a chip in the roster's row.
#
# Drops in two new files kept beside this script (WorldAtlas3D.Names.cs and
# StrategicView.Places.cs) and edits five live files surgically (WorldAtlas3D.cs,
# WorldAtlas3D.Clarity.cs, RegisterManager.cs, StrategicView.cs and
# StrategicView.Dispatch.cs).
#
# Rules: every starting blob checked (a new file must not exist yet), every anchor found
# exactly once, added text ASCII apart from box-drawing characters, no banned dash,
# brackets balanced per edit, every result equal to the blob it was validated at, nothing
# written unless every step succeeds, every written file verified, and a second run
# refuses.
#
# Usage: python3 apply_provinces_map_7h.py <repo_root> [--dry-run]

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
    ("Scripts/Systems/Strategic/WorldAtlas3D.Names.cs", None, "ecf16d621386d9ae3d4925631809a77daed0e0a2"),
    ("Scripts/Systems/Strategic/StrategicView.Places.cs", None, "aab3573883583c7dab7c714b804a4ccb0196a225"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.cs",
        "afebf38f874d3c5172ec3f7b715f0fd3088d2e09",
        "50e139cdc06371d7748c42b759ebc36ee7229f00",
        [
            (
                r"""        _scaledLabels.Clear();        // label nodes live in _markers, just freed above
""",
                r"""        _scaledLabels.Clear();        // label nodes live in _markers, just freed above
        ForgetFramedLabels();         // provinces 7h: the framed and the hidden names went with them
""",
            ),
            (
                r"""                    : SettlementDisplayName(s);
                var lbl = StackedLabel(new Vector2I(s.CenterX, s.CenterY), label, c,
                                       MarkerPos(s.CenterX, s.CenterY, side + 1.4f), 88, 0.028f);
""",
                r"""                    : SeatMapName(s);   // provinces 7h (fix 5): the realm's name, without "Seat"
                // Provinces 7h (fix 4): 2 percent of the screen's height, the pieces' size.
                var lbl = StackedLabel(new Vector2I(s.CenterX, s.CenterY), label, c,
                                       MarkerPos(s.CenterX, s.CenterY, side + 1.4f), SeatLabelFont, SeatLabelFrac);
""",
            ),
            (
                r"""                    _labelStack[seatTile] = (stacked.anchor, stacked.frac + 0.028f * 0.5f);
                AddMarker(lbl);
""",
                r"""                    _labelStack[seatTile] = (stacked.anchor, stacked.frac + SeatLabelFrac * 0.5f);
                AddMarker(lbl);
                FrameLabel(lbl);   // provinces 7h (fix 3): kept inside the frame on screen
""",
            ),
            (
                r"""        if (stackFrac > 0f && _camera != null)
        {
            lbl.Position = anchor + _camera.GlobalTransform.Basis.Y * (orthoSize * stackFrac);
        }
    }
""",
                r"""        if (stackFrac > 0f && _camera != null)
        {
            lbl.Position = anchor + _camera.GlobalTransform.Basis.Y * (orthoSize * stackFrac);
        }
        // Provinces 7h (fix 3): a framed name is placed again, inside the frame's inner edge.
        if (_framedLabels.Count > 0 && _framedLabels.Contains(lbl))
        {
            KeepInsideFrame(lbl, anchor, stackFrac, orthoSize);
        }
    }
""",
            ),
            (
                r"""        UpdateLabelScales();   // keep map labels a constant on-screen size as the zoom changes
""",
                r"""        UpdateLabelScales();   // keep map labels a constant on-screen size as the zoom changes
        ApplyPieceNameZoom();  // provinces 7h (fix 6): the pieces' names step aside at the whole-world view
""",
            ),
            (
                r"""        // LAST, so the pieces draw over the places they are standing on.
        BuildPieceMarkers();
""",
                r"""        // LAST, so the pieces draw over the places they are standing on.
        BuildPieceMarkers();
        ApplyPieceNameZoom();   // provinces 7h (fix 6): new names follow the zoom at once
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.Clarity.cs",
        "3fbb7e9433b47ddc3ad8949297c77fc560033993",
        "3950ddd849e93c2a55664f0f9b7131ab027c21e4",
        [
            (
                r"""    /// <summary>The glints' strength up close: the water shader's own default.</summary>
    private const float SeaGlintStrength = 0.35f;
""",
                r"""    /// <summary>The glints' strength up close. Off since provinces 7h (fix 2): magnified,
    /// the glint's step-cut noise read as hard-edged blotches. The ramp above is kept, so a
    /// softer glint can come back by raising this (the water shader's own default is 0.35).</summary>
    private const float SeaGlintStrength = 0f;
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Register/RegisterManager.cs",
        "f03867dea07c97b305995834dcdcdef5d272f762",
        "a30187d7cbefd327a7ce833c534b10a6de655e64",
        [
            (
                r"""    private void PlaceAll()
    {
        _historyText.CustomMinimumSize = new Vector2(SlipWidth + 12f, Mathf.Min(520f, ViewportSize.Y * 0.5f));
        PlaceDock();
    }
""",
                r"""    private void PlaceAll()
    {
        // Provinces 7h (fix 1): the notes' list is sized to its area by FitHistoryTo, in PlaceDock.
        PlaceDock();
    }
""",
            ),
            (
                r"""        PlaceIn(_slip, area, growUp, vp);
        PlaceIn(_history, area, growUp, vp);
""",
                r"""        FitHistoryTo(area);
        PlaceIn(_slip, area, growUp, vp);
        PlaceIn(_history, area, growUp, vp);
""",
            ),
            (
                r"""        var at = new Vector2(Mathf.Round(area.Position.X), Mathf.Round(y));
        if (panel.Position != at)
            panel.Position = at;
    }
""",
                r"""        var at = new Vector2(Mathf.Round(area.Position.X), Mathf.Round(y));
        if (panel.Position != at)
            panel.Position = at;
    }

    /// <summary>The margin notes' list at its tallest, as before provinces 7h.</summary>
    private const float HistoryListCap = 520f;
    /// <summary>The list at its shortest, a few notes, however small the area.</summary>
    private const float HistoryListFloor = 120f;

    /// <summary>Provinces 7h (fix 1): the notes' list takes what the area leaves after the
    /// panel's header, footer and padding, so the panel ends inside it. With the list alone
    /// 520 tall, the panel ran 59 design pixels past the strategic map's 520 pixel region.
    /// Every frame, from PlaceDock; it changes the list only when the answer changes.</summary>
    private void FitHistoryTo(Rect2 area)
    {
        if (_history == null || _historyText == null)
            return;
        float cap = Mathf.Min(HistoryListCap, ViewportSize.Y * 0.5f);
        float chrome = Mathf.Max(0f, _history.GetCombinedMinimumSize().Y - _historyText.GetCombinedMinimumSize().Y);
        float list = Mathf.Round(Mathf.Clamp(area.Size.Y - chrome, Mathf.Min(HistoryListFloor, cap), cap));
        var want = new Vector2(SlipWidth + 12f, list);
        if (_historyText.CustomMinimumSize == want)
            return;
        _historyText.CustomMinimumSize = want;
        _history.ResetSize();
    }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "b9e051f48efad89982c3a111a7599eb1fab19579",
        "2c2d1bf121fc10513b785a325f1da9a4709784df",
        [
            (
                r"""    /// <summary>What the castle is doing, in one line. Leads with the thing that
    /// BLOCKS an order when there is one, because that is what the player is
    /// about to run into.</summary>
    private string CastleStatusLine(CycleState cycle)
    {
        string at = cycle.World != null && cycle.World.InBounds(cycle.CastleX, cycle.CastleY)
            ? $"({cycle.CastleX},{cycle.CastleY})"
            : "nowhere yet";

        if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            return $"{at}  ·  under attack";
        }
        if (cycle.CastleRepairLunations > 0)
        {
            return $"{at}  ·  resupply {WorldClock.ResupplyDaysLeft(cycle)} day(s)";
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            return $"{at}  ·  in the field, {cycle.CastleSortie.StepsRemaining}/{cycle.CastleSortie.MaxFuel} fuel";
        }
        if (WorldClock.CastleBusy(cycle))
        {
            return $"{at}  ·  returning, {cycle.CastleBusyUntilDay - WorldClock.Now(cycle)} day(s)";
        }
        if (!cycle.CastleParked)
        {
            return $"{at}  ·  out on a sortie";
        }
        // No standing order. Named rather than left blank, because "nothing is
        // assigned here" is the thing the player needs to notice before they
        // spend a lunation.
        return $"{at}  ·  fuel {cycle.CastleFuel}/{cycle.CastleMaxFuel}  ·  awaiting orders";
    }

    /// <summary>What a party is doing, in one line.</summary>
    private string PartyStatusLine(CycleState cycle, FieldParty p)
    {
        if (p.X < 0 || p.Y < 0)
        {
            return "not in the field";
        }
        string at = $"({p.X},{p.Y})";

        if (p.Sortie != null && p.Sortie.IsLive())
        {
            return $"({p.Sortie.X},{p.Sortie.Y})  ·  in the field, {p.Sortie.StepsRemaining}/{p.Sortie.MaxFuel} rations";
        }
        if (p.State == FieldPartyState.Travelling)
        {
            return $"{at}  \u2192 ({p.DestX},{p.DestY})  ·  {p.TravelPhasesRemaining} tile(s) to go";
        }
        if (p.State == FieldPartyState.Working)
        {
            return $"{at}  ·  {FieldPostings.Describe(cycle, p)}";
        }
        if (WorldClock.PartyBusy(cycle, p))
        {
            return $"{at}  ·  returning, {p.BusyUntilDay - WorldClock.Now(cycle)} day(s)";
        }
        if (FieldPostings.IsDetachment(p))
        {
            // A detachment with no job is waiting to be picked up. It is not
            // "awaiting orders": it cannot take any.
            return $"{at}  ·  {p.MemberCompanionIds?.Count ?? 0} posted  ·  awaiting collection";
        }

        int strength = p.MemberCompanionIds?.Count ?? 0;
        string who = strength == 0 ? "the wizard alone" : $"{strength} afoot";
        string carrying = p.Carrying != null && !p.Carrying.IsEmpty ? "  ·  carrying spoils" : "";
        return $"{at}  ·  {who}{carrying}  ·  awaiting orders";
    }

""",
                r"""    /// <summary>What the castle is doing, in one line. Leads with the thing that
    /// BLOCKS an order when there is one, because that is what the player is
    /// about to run into. Where it stands is CastlePlace's: the selection panel
    /// gives the place a line of its own (provinces 7h, fix 7).</summary>
    private string CastleStatusLine(CycleState cycle)
    {
        if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            return "under attack";
        }
        if (cycle.CastleRepairLunations > 0)
        {
            return $"resupply {WorldClock.ResupplyDaysLeft(cycle)} day(s)";
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            return $"in the field, {cycle.CastleSortie.StepsRemaining}/{cycle.CastleSortie.MaxFuel} fuel";
        }
        if (WorldClock.CastleBusy(cycle))
        {
            return $"returning, {cycle.CastleBusyUntilDay - WorldClock.Now(cycle)} day(s)";
        }
        if (!cycle.CastleParked)
        {
            return "out on a sortie";
        }
        // No standing order. Named rather than left blank, because "nothing is
        // assigned here" is the thing the player needs to notice before they
        // spend a lunation.
        return $"fuel {cycle.CastleFuel}/{cycle.CastleMaxFuel}  ·  awaiting orders";
    }

    /// <summary>What a party is doing, in one line. Where it stands is
    /// PartyPlace's (provinces 7h, fix 7).</summary>
    private string PartyStatusLine(CycleState cycle, FieldParty p)
    {
        if (p.X < 0 || p.Y < 0)
        {
            return "not in the field";
        }
        if (p.Sortie != null && p.Sortie.IsLive())
        {
            return $"in the field, {p.Sortie.StepsRemaining}/{p.Sortie.MaxFuel} rations";
        }
        if (p.State == FieldPartyState.Travelling)
        {
            string dest = PlaceName(cycle, p.DestX, p.DestY, withRealm: false);
            return $"\u2192 {dest}  ·  {p.TravelPhasesRemaining} tile(s) to go";
        }
        if (p.State == FieldPartyState.Working)
        {
            return FieldPostings.Describe(cycle, p);
        }
        if (WorldClock.PartyBusy(cycle, p))
        {
            return $"returning, {p.BusyUntilDay - WorldClock.Now(cycle)} day(s)";
        }
        if (FieldPostings.IsDetachment(p))
        {
            // A detachment with no job is waiting to be picked up. It is not
            // "awaiting orders": it cannot take any.
            return $"{p.MemberCompanionIds?.Count ?? 0} posted  ·  awaiting collection";
        }

        int strength = p.MemberCompanionIds?.Count ?? 0;
        string who = strength == 0 ? "the wizard alone" : $"{strength} afoot";
        string carrying = p.Carrying != null && !p.Carrying.IsEmpty ? "  ·  carrying spoils" : "";
        return $"{who}{carrying}  ·  awaiting orders";
    }

""",
            ),
            (
                r"""        AddForceRow("\u25A0  Castle", CastleStatusLine(cycle), UITheme.ArcaneBlue,
""",
                r"""        AddForceRow("\u25A0  Castle", ForceLine(CastlePlace(cycle), CastleStatusLine(cycle)), UITheme.ArcaneBlue,
""",
            ),
            (
                r"""                AddForceRow($"\u25B2  {p.Name}", PartyStatusLine(cycle, p), UITheme.Gold,
""",
                r"""                AddForceRow($"\u25B2  {p.Name}", ForceLine(PartyPlace(cycle, p), PartyStatusLine(cycle, p)), UITheme.Gold,
""",
            ),
            (
                r"""                    AddForceRow($"      \u25B8  {det.Name}", PartyStatusLine(cycle, det), UITheme.Gold,
""",
                r"""                    AddForceRow($"      \u25B8  {det.Name}", ForceLine(PartyPlace(cycle, det), PartyStatusLine(cycle, det)), UITheme.Gold,
""",
            ),
            (
                r"""        chipRow.AddChild(BuildMusterChip());""",
                r"""        chipRow.AddChild(BuildDestinationsChip());   // provinces 7h (fix 8): the folded Destinations card
        chipRow.AddChild(BuildMusterChip());""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.Dispatch.cs",
        "f2bbcdbaa1eb6a777c0ccfc19e3932961e1a1c8e",
        "afa9a7b67c56dc19a19b8ff4a1bc9c1be705d9ed",
        [
            (
                r"""        ob.AddChild(_ordersTitle);
        _ordersStatus = new Label { ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
""",
                r"""        ob.AddChild(_ordersTitle);
        // Provinces 7h (fix 7): where the force stands, by province and realm, on a line of
        // its own; the tile's coordinates are its tooltip. A label shows a tooltip only when
        // it takes the mouse, so both lines here do.
        _ordersPlace = new Label
        {
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _ordersPlace.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _ordersPlace.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        ob.AddChild(_ordersPlace);
        _ordersStatus = new Label
        {
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
""",
            ),
            (
                r"""        _ordersStatus.TooltipText = _ordersStatus.Text;
""",
                r"""        _ordersStatus.TooltipText = _ordersStatus.Text;
        RefreshOrdersPlace(cycle, party);   // provinces 7h (fix 7)
""",
            ),
            (
                r"""        if (!show)
        {
            _dispatchPanel.Visible = false;
            return;
        }
""",
                r"""        if (!show)
        {
            _dispatchAvailable = false;   // provinces 7h (fix 8): hides the card and its chip
            ApplyDispatchCollapse();
            return;
        }
""",
            ),
            (
                r"""        // placeholder holding a slab of screen.
        _dispatchPanel.Visible = reachable > 0;
""",
                r"""        // placeholder holding a slab of screen. ApplyDispatchCollapse, below, shows the card
        // or, folded, its chip in the roster's row (provinces 7h, fix 8).
        _dispatchAvailable = reachable > 0;
        _dispatchReachable = reachable;
""",
            ),
            (
                r"""        if (_dispatchScroll != null)
        {
            _dispatchScroll.Visible = !_dispatchCollapsed;
        }
    }
""",
                r"""        if (_dispatchScroll != null)
        {
            _dispatchScroll.Visible = !_dispatchCollapsed;
        }
        // Provinces 7h (fix 8): folded, the card leaves the corner and a chip in the roster's
        // row stands for it. The chip opens it; the card's own button folds it again.
        if (_dispatchPanel != null)
        {
            _dispatchPanel.Visible = _dispatchAvailable && !_dispatchCollapsed;
        }
        RefreshDestinationsChip();
    }
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
