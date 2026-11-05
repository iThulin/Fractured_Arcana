#!/usr/bin/env python3
# Provinces, slice PV2b-2: favors as deeds (docs/provinces_v1_7g_deeds_and_courts.md, section
# 17; rulings PR16 and PR44, 2026-11-05). One call-in, two uses: a favor owed to the guild
# still buys its field effect, or it is spent as its deed in its court's realm. A Political
# favor is a charter, +20 (major +35) in the province the party stands in; an Intelligence
# favor makes every site there Known; a Military favor gives +15 in the courtier's estate
# and moves the invasion of the realm 10 back, never far enough to decide it; an Economic
# favor gives +10 in the estate. Arcane and Passage favors wait for PV4.
#
# Drops in two new files kept beside this script (FavorDeeds.cs and
# ExpeditionManager.FavorDeeds.cs) and edits three live files surgically
# (ProvinceInfluence.cs, ExpeditionManager.cs and ProvinceSaveAssert.cs).
#
# Rules: every starting blob checked (a new file must not exist yet), every anchor found
# exactly once, added text ASCII apart from box-drawing characters, no banned dash,
# brackets balanced per edit, every result equal to the blob it was validated at, nothing
# written unless every step succeeds, every written file verified, and a second run
# refuses.
#
# Usage: python3 apply_provinces_pv2b2.py <repo_root> [--dry-run]

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
    ("Scripts/Systems/Strategic/FavorDeeds.cs", None, "0b1af8fbe6f30c3994e8d323ba1a280590fc325b"),
    ("Scripts/Systems/Overworld/Expedition/ExpeditionManager.FavorDeeds.cs", None, "335799686ab05b4f70ab97fd8c4e14edbe330987"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/ProvinceInfluence.cs",
        "f024a8982dfaf09c7c9016bae7f184e9c5b9621e",
        "2b4450533b0693ff28aa32d187fdc05c990fc3a9",
        [
            (
                r"""    public static string Deed(CycleState cycle, int x, int y, int amount)
    {
        if (Interval.Walking(cycle))
            return "";
        return Add(cycle, x, y, amount);
    }
""",
                r"""    public static string Deed(CycleState cycle, int x, int y, int amount)
    {
        if (Interval.Walking(cycle))
            return "";
        return Add(cycle, x, y, amount);
    }

    /// <summary>A deed in a province named by its index (slice PV2b-2: a favor spent as its
    /// deed lands in a courtier's estate, which may lie anywhere in the realm). As Deed: the
    /// court weighs it, and nothing moves while the interval walks or in a world without
    /// provinces. Returns the news as Add does.</summary>
    public static string DeedIn(CycleState cycle, int index, int amount)
    {
        if (cycle?.World == null || !Active(cycle) || Interval.Walking(cycle))
            return "";
        return AddToProvince(cycle, index, amount);
    }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs",
        "d3d5fcf9d4f350588f83708aa1f4ec521a23b4ed",
        "472d2fd3143c4d70ca6ec06af828345aaa424517",
        [
            (
                r"""        _ledgerPanel.GetIneligibilityReason = CallInIneligibility;
""",
                r"""        // Provinces PV2b-2 (PR44): the button shows while either use of the favor can be had.
        _ledgerPanel.GetIneligibilityReason = CallInRowIneligibility;
""",
            ),
            (
                r"""    /// <summary>Panel callback: validate, execute, consume, checkpoint.</summary>
    private void OnLedgerCallIn(Favor f)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null)
            return;

        string reason = CallInIneligibility(f);
""",
                r"""    /// <summary>Panel callback. A favor with a deed opens the choice of its two uses, the
    /// field effect or the deed (provinces PV2b-2, PR44, ExpeditionManager.FavorDeeds.cs);
    /// any other goes straight to its field effect.</summary>
    private void OnLedgerCallIn(Favor f)
    {
        if (SaveManager.ActiveSave?.Cycle?.Council == null)
            return;
        if (FavorDeeds.HasDeed(f))
        {
            OpenCallInChoice(f);
            return;
        }
        CallInFieldEffect(f);
    }

    /// <summary>The field effect: validate, execute, consume, checkpoint.</summary>
    private void CallInFieldEffect(Favor f)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null)
            return;

        string reason = CallInIneligibility(f);
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Verification/ProvinceSaveAssert.cs",
        "d13e30133871fb0f07e5188dd54b7e603802c70b",
        "ae8d5e13ee9fadeb513ca63f3c4070361aac36eb",
        [
            (
                r"""        ok &= AssertBetrayal(sb);
""",
                r"""        ok &= AssertBetrayal(sb);
        ok &= AssertFavorDeeds(sb);
""",
            ),
            (
                r"""        sb.AppendLine(ok ? "  Betrayal: PASS" : "  Betrayal: FAIL");
        return ok;
    }
""",
                r"""        sb.AppendLine(ok ? "  Betrayal: PASS" : "  Betrayal: FAIL");
        return ok;
    }

    /// <summary>Favors as deeds (PR16, PR44): a charter of 20 (a major one 35) in the province
    /// the party stands in, in the creditor's realm only; a Military favor's 15 in the
    /// courtier's estate, with the invasion of the realm 10 back but never to the line that
    /// decides it; an Economic favor's 10 in the estate; an Intelligence favor makes every
    /// site in the province Discovered and Known, and has nothing to spend on once they are;
    /// Arcane and Passage favors have no deed, a favor owed by the guild is never one, and no
    /// favor is spent as a deed while the interval walks.</summary>
    private static bool AssertFavorDeeds(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        ProvinceInfluence.Sync(cycle);   // gives the courtiers their estates
        var west = cycle.Council.Courts[West];
        int capital = world.ProvinceIndexAt(6, 15);
        var courtier = west.Courtiers.FirstOrDefault(c => !string.IsNullOrEmpty(c.EstateProvinceId));
        int estate = courtier == null ? -1 : provinces.FindIndex(p => p.Id == courtier.EstateProvinceId);
        bool ok = Check(sb, "the favor deeds are 20 (major 35), 15 with the front 10, and 10",
            FavorDeeds.CharterGain == 20 && FavorDeeds.CharterMajorGain == 35 && FavorDeeds.MarshalGain == 15
            && FavorDeeds.MarshalFrontPush == 10 && FavorDeeds.StewardGain == 10);
        if (capital < 0 || estate < 0)
        {
            sb.AppendLine("  Favor deeds: FAIL (the scratch world's capital or a courtier's estate is missing)");
            return false;
        }
        Favor Owed(string type, bool major = false) => new Favor
        {
            Id = "favor_" + type, OwedToGuild = true, KingdomId = West, CourtierId = courtier.Id,
            Type = type, IsMajor = major,
        };

        ok &= Check(sb, "Arcane and Passage favors have no deed, and a favor owed by the guild is never one",
            !FavorDeeds.HasDeed(Owed("Arcane")) && !FavorDeeds.HasDeed(Owed("Passage"))
            && !FavorDeeds.HasDeed(new Favor { OwedToGuild = false, Type = "Political", KingdomId = West })
            && FavorDeeds.HasDeed(Owed("Political")) && FavorDeeds.HasDeed(Owed("Intelligence")));

        // A charter, where the party stands.
        provinces[capital].Influence = 0;
        provinces[capital].Held = false;
        ok &= Check(sb, "a charter can be spent standing in the realm, and not standing in another",
            FavorDeeds.Ineligibility(cycle, Owed("Political"), 6, 15) == null
            && FavorDeeds.Ineligibility(cycle, Owed("Political"), 30, 15) != null);
        FavorDeeds.Spend(cycle, Owed("Political"), 6, 15);
        int charter = provinces[capital].Influence;
        provinces[capital].Influence = 0;
        FavorDeeds.Spend(cycle, Owed("Political", major: true), 6, 15);
        ok &= Check(sb, $"a charter gives 20 in the province the party stands in, a major one 35 ({charter}, {provinces[capital].Influence})",
            charter == 20 && provinces[capital].Influence == 35);

        // The Marshal and the Steward, in the estate.
        cycle.Warfronts = new List<Warfront> { Front("wf_favor") };
        provinces[estate].Influence = 0;
        provinces[estate].Held = false;
        FavorDeeds.Spend(cycle, Owed("Military"), 6, 15);
        ok &= Check(sb, $"a Military favor gives 15 in the courtier's estate ({provinces[estate].Influence}) and the invasion falls back 10 ({cycle.Warfronts[0].Advance})",
            provinces[estate].Influence == 15 && cycle.Warfronts[0].Advance == 40);
        cycle.Warfronts[0].Advance = 4;
        FavorDeeds.Spend(cycle, Owed("Military"), 6, 15);
        ok &= Check(sb, "but never to the line that decides it",
            cycle.Warfronts[0].Advance == FavorDeeds.FrontFloor && !cycle.Warfronts[0].Closed);
        provinces[estate].Influence = 0;
        FavorDeeds.Spend(cycle, Owed("Economic"), 6, 15);
        ok &= Check(sb, "an Economic favor gives 10 in the estate", provinces[estate].Influence == 10);

        // The Spymaster's dossier: two sites in the capital province, one of them unfound.
        var spots = new List<(int x, int y)>();
        for (int y = 0; y < world.Height && spots.Count < 2; y++)
        {
            for (int x = 0; x < world.Width && spots.Count < 2; x++)
            {
                if (!world.GetTile(x, y).IsWater && world.ProvinceIndexAt(x, y) == capital)
                    spots.Add((x, y));
            }
        }
        int first = world.Pois.Count;
        world.Pois.Add(new WorldPoi { X = spots[0].x, Y = spots[0].y, Kind = PoiKind.Combat, KingdomId = West });
        world.Pois.Add(new WorldPoi { X = spots[1].x, Y = spots[1].y, Kind = PoiKind.Rest, KingdomId = West, Discovered = true });
        var learned = FavorDeeds.Spend(cycle, Owed("Intelligence"), 6, 15);
        ok &= Check(sb, "an Intelligence favor makes every site in the province Discovered and Known, and names the newly found",
            world.Pois[first].Discovered && world.Pois[first].Known && world.Pois[first + 1].Known
            && learned.Revealed.Contains(world.Pois[first]) && !learned.Revealed.Contains(world.Pois[first + 1]));
        ok &= Check(sb, "and once they are, it has nothing to spend on",
            FavorDeeds.Ineligibility(cycle, Owed("Intelligence"), 6, 15) != null);

        // The interval.
        cycle.Interval.Active = true;
        ok &= Check(sb, "while the interval walks no favor is spent as a deed",
            FavorDeeds.Ineligibility(cycle, Owed("Political"), 6, 15) != null);
        cycle.Interval.Active = false;

        sb.AppendLine(ok ? "  Favor deeds: PASS" : "  Favor deeds: FAIL");
        return ok;
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
