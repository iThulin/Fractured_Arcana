#!/usr/bin/env python3
# Provinces, slice PV2a (docs/provinces_v1.md section 7b, 2026-11-03): the guild's
# influence held per province. Province gains Influence and Held; KingdomState
# gains PlayerInfluenceDerived; a kingdom's PlayerInfluence is derived from its
# provinces; the lunation moves influence (presence, decay, held neighbours, the
# hold lines); today's writers land on provinces; the Reach lens reads provinces;
# the place card, a report, a debug deed and asserts.
#
# Drops in three files kept beside this script (ProvinceInfluence.cs, new; and
# Province.cs and ProvinceSaveAssert.cs, which replace the PV1 versions) and edits
# seven live files surgically. Rules: every starting blob checked (a new file must
# not exist yet), every anchor found exactly once, added text ASCII apart from
# box-drawing characters, no banned dash, brackets balanced per edit, every result
# equal to the blob it was validated at, nothing written unless every step
# succeeds, every written file verified, and a second run refuses.
#
# Usage: python3 apply_provinces_pv2a.py <repo_root> [--dry-run]

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
    ("Scripts/Systems/Strategic/ProvinceInfluence.cs", None, "8941d5ef92ea2e6aeeda110e8839302ca06842ae"),
    ("Scripts/Systems/Overworld/WorldGen/Province.cs", "364870b330dbdd026fb68eca11f063de78bbdf2f", "25d3425b7ce78c0f5decb719d829db315f75cc15"),
    ("Scripts/Systems/Verification/ProvinceSaveAssert.cs", "b783f5deaf1c9372ea54899d154b1dc3c7db49d0", "ac13cb781a1c49a98e90d55d16a29ae97b44f6d9"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/KingdomState.cs",
        "4fcd2cc6b9b7dbaf46154198f5744abf9f12b417",
        "0c90e77390db17b8f55d7a220299be6c0e3fbac7",
        [
            (
                r"""// Purpose:        Per-province dynamic strategic state, the
//                 §3.2 state block. One per generated province,
""",
                r"""// Purpose:        Per-kingdom dynamic strategic state, the
//                 §3.2 state block. One per generated kingdom,
""",
            ),
            (
                r"""// Province identity: for Phase 1, a province's id EQUALS its
// source template's region id (each template is used at most
// once). TemplateRegionId is kept distinct so a future cycle can
// instance one template into several provinces without touching
// callers.
""",
                r"""// Kingdom identity: a kingdom's id EQUALS its source template's
// region id (each template is used at most once). TemplateRegionId
// is kept distinct so a future cycle can instance one template into
// several kingdoms without touching callers. The Phase 1 documents
// called a kingdom a "province"; since docs/provinces_v1.md a
// province is a region of a kingdom (Province.cs), about six to a
// kingdom, and the guild's influence is held on provinces
// (ProvinceInfluence.cs).
""",
            ),
            (
                r"""/// Dynamic strategic state for one province. Topology (grid
""",
                r"""/// Dynamic strategic state for one kingdom. Topology (grid
""",
            ),
            (
                r"""    /// <summary>Province id. Phase 1: equals the source template's region id.</summary>
""",
                r"""    /// <summary>Kingdom id: equals the source template's region id.</summary>
""",
            ),
            (
                r"""    /// <summary>Source template this province generated from (terrain gen reads this).</summary>
""",
                r"""    /// <summary>Source template this kingdom generated from (terrain gen reads this).</summary>
""",
            ),
            (
                r"""    /// <summary>1""" + EN_DASH + r"""3 difficulty tier, derived at generation from distance to
    /// the player's start. Replaces the deleted completedRegions ramp as the
    /// per-province difficulty floor.</summary>
""",
                r"""    /// <summary>Difficulty tier, 1 to 3, derived at generation from distance to
    /// the player's start. Replaces the deleted completedRegions ramp as the
    /// per-kingdom difficulty floor.</summary>
""",
            ),
            (
                r"""    /// <summary>Zone-of-influence value 0""" + EN_DASH + r"""100. Raised by outposts, resolved
    /// landmarks, Allied stance, archmage resolution. Painted on the map.</summary>
    public int PlayerInfluence = 0;
""",
                r"""    /// <summary>The guild's influence over the kingdom, 0 to 100. Since provinces
    /// carry influence (docs/provinces_v1.md section 7b) it is derived: the mean of
    /// the guild's influence in the kingdom's provinces, weighted by their tiles,
    /// kept current by ProvinceInfluence. Read it freely. To change it, act on a
    /// province (ProvinceInfluence.Add, AddNear, SetKingdom); a write made here
    /// anyway is spread over the kingdom's provinces at the next sync.</summary>
    public int PlayerInfluence = 0;

    /// <summary>The value ProvinceInfluence last derived into PlayerInfluence. A
    /// difference between the two is a write made outside the province layer, or a
    /// save from before provinces carried influence, and the next sync spreads it
    /// over the kingdom's provinces. 0 on such a save, so every province then starts
    /// at its kingdom's value.</summary>
    public int PlayerInfluenceDerived = 0;
""",
            ),
            (
                r"""    /// <summary>Accumulated tension per neighboring province id. At threshold a
""",
                r"""    /// <summary>Accumulated tension per neighboring kingdom id. At threshold a
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/KingdomTickSimulation.cs",
        "4e3973e88649f09c3c7d64b958121450d010f80a",
        "143dd1bea9237d4676800dfd9e1bfc01e312df59",
        [
            (
                r"""//                WorldData (adjacency + border tiles + staging points),
//                StrategicView (calls Tick + ApplyIntervention; renders
""",
                r"""//                WorldData (adjacency + border tiles + staging points),
//                ProvinceInfluence (the guild's influence is held per
//                province since docs/provinces_v1.md PV2a; falls,
//                seizures and interventions write through it),
//                StrategicView (calls Tick + ApplyIntervention; renders
""",
            ),
            (
                r"""    /// <summary>PlayerInfluence at/above which losing a province reads as "yours".</summary>
""",
                r"""    /// <summary>PlayerInfluence at/above which a fallen kingdom reads as "yours". So does
    /// a kingdom in which the guild held any province (docs/provinces_v1.md section 7b).</summary>
""",
            ),
            (
                r"""            if (def.PlayerInfluence >= InfluenceStabilizeThreshold)
                adv -= WarfrontInfluenceRelief;
""",
                r"""            // The guild's influence relieves a front where it stands (provinces PV2a):
            // in the front's own province, at its focus, or across the kingdom.
            if (Math.Max(def.PlayerInfluence, ProvinceInfluence.InfluenceAt(cycle.World, wf.FocusCol, wf.FocusRow))
                >= InfluenceStabilizeThreshold)
                adv -= WarfrontInfluenceRelief;
""",
            ),
            (
                r"""        bool wasPlayers = def.PlayerInfluence >= PlayerHoldingInfluence
                          || HoldsStagingPoint(cycle.World, defenderKid);
""",
                r"""        bool wasPlayers = def.PlayerInfluence >= PlayerHoldingInfluence
                          || ProvinceInfluence.HoldsAny(cycle.World, defenderKid)
                          || HoldsStagingPoint(cycle.World, defenderKid);
""",
            ),
            (
                r"""        def.PlayerInfluence = 0;
""",
                r"""        // The guild's influence goes with the kingdom, province by province (PV2a).
        ProvinceInfluence.SetKingdom(cycle, defenderKid, 0);
""",
            ),
            (
                r"""        def.PlayerInfluence = 100;
""",
                r"""        // Every province of the kingdom passes to the guild with it, until fronts and
        // seizures work province by province (docs/provinces_v1.md, PV3).
        ProvinceInfluence.SetKingdom(cycle, defenderKid, 100);
""",
            ),
            (
                r"""                    def.PlayerInfluence = Mathf.Min(100, def.PlayerInfluence + 15);
""",
                r"""                    // The front's own province gains (provinces PV2a).
                    ProvinceInfluence.AddNear(cycle, wf.DefenderKingdomId, wf.FocusCol, wf.FocusRow, 15);
""",
            ),
            (
                r"""                    def.PlayerInfluence = Mathf.Min(100, def.PlayerInfluence + 20);
""",
                r"""                    // The front's own province gains (provinces PV2a).
                    ProvinceInfluence.AddNear(cycle, wf.DefenderKingdomId, wf.FocusCol, wf.FocusRow, 20);
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs",
        "91b108797cf1e2ff7350291694921140ed2aa85a",
        "2ee0f8cd3a8483f67662bd1317a4b3b4dae73a24",
        [
            (
                r"""    /// <summary>Influence granted to the host kingdom when a site is secured
    /// (the strategic Reach-lens ratchet).</summary>
""",
                r"""    /// <summary>Influence granted in a site's province when the site is secured or a
    /// shard vault taken (the strategic Reach-lens ratchet; provinces PV2a).</summary>
""",
            ),
            (
                r"""            // Reach ratchet: securing a site grows guild influence over the host
            // kingdom, so the strategic Reach lens changes because you played.
            string kid = _world.Tiles[idx].KingdomId;
            var kingdoms = SaveManager.ActiveSave?.Cycle?.Kingdoms;
            if (!string.IsNullOrEmpty(kid) && kingdoms != null &&
                kingdoms.TryGetValue(kid, out var ks))
                ks.PlayerInfluence = Mathf.Min(100, ks.PlayerInfluence + StagingInfluenceGain);
""",
                r"""            // Reach ratchet: securing a site grows the guild's influence in its
            // province (docs/provinces_v1.md section 7b; the host kingdom's influence
            // is derived from its provinces), so the strategic Reach lens changes
            // because you played. A province that crosses the hold line says so.
            string heldNews = ProvinceInfluence.Add(SaveManager.ActiveSave?.Cycle, col, row, StagingInfluenceGain);
            if (!string.IsNullOrEmpty(heldNews))
                _toasts?.Push(heldNews, QuestToastKind.Complete);
""",
            ),
            (
                r"""                string kid = _world.Tiles[cidx].KingdomId;
                var kingdoms = SaveManager.ActiveSave?.Cycle?.Kingdoms;
                if (!string.IsNullOrEmpty(kid) && kingdoms != null &&
                    kingdoms.TryGetValue(kid, out var ks))
                    ks.PlayerInfluence = Mathf.Min(100, ks.PlayerInfluence + StagingInfluenceGain);
""",
                r"""                // The vault's province gains, as a secured site's does (provinces PV2a).
                string vaultNews = ProvinceInfluence.Add(SaveManager.ActiveSave?.Cycle, z.CenterX, z.CenterY, StagingInfluenceGain);
                if (!string.IsNullOrEmpty(vaultNews))
                    _toasts?.Push(vaultNews, QuestToastKind.Complete);
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "7c8fc361e8202c445a1a1a50caa224b01017049a",
        "13bb0443309c6815b7c9c5e1859cf8ce009d3743",
        [
            (
                r"""        if (placeProvince != null && !string.IsNullOrEmpty(placeProvince.Name))
            kingdomLabel = $"{placeProvince.Name}, {kingdomLabel}";
""",
                r"""        if (placeProvince != null && !string.IsNullOrEmpty(placeProvince.Name))
            kingdomLabel = $"{placeProvince.Name}, {kingdomLabel}";
        // PV2a: the guild's influence in the province, and whether it holds it.
        string placeStanding = placeProvince != null ? "\n" + ProvinceInfluence.StandingLine(placeProvince) : "";
""",
            ),
            (
                r"""                       + (sp.Available ? ", stages and supplies your forces." : ", overrun: it neither stages nor supplies you until retaken."),
""",
                r"""                       + (sp.Available ? ", stages and supplies your forces." : ", overrun: it neither stages nor supplies you until retaken.")
                       + placeStanding,
""",
            ),
            (
                r"""        CorruptionSpread.Tick(cycle.World, cycle.Campaign, cycle.Kingdoms);
""",
                r"""        CorruptionSpread.Tick(cycle.World, cycle.Campaign, cycle.Kingdoms);
        // Provinces (docs/provinces_v1.md section 7b, PV2a): the castle's presence
        // raises the guild's influence in its province and the ones beside it, it
        // fades where the castle is not, and provinces cross into or out of the
        // guild's hold. Before the kingdom tick, which reads each kingdom's influence
        // (now derived from its provinces) for stability and fronts.
        foreach (var provinceLine in ProvinceInfluence.Tick(cycle))
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(provinceLine);
        }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campaign/CycleInitializer.cs",
        "e673bf44dea2c8dfad7b92bf9240a0dec05b2679",
        "ffe0dab72bc766e16515d5314046e1d4dc63affb",
        [
            (
                r"""            if (ProvinceBuilder.EnsureBuilt(cycle.World))
            {
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
            }
            return; // already generated this cycle
""",
                r"""            bool provincesBuilt = ProvinceBuilder.EnsureBuilt(cycle.World);
            // PV2a (docs/provinces_v1.md section 7b): the guild's influence lives on
            // provinces. A kingdom value written outside them, or saved before they
            // carried influence, is spread over the kingdom's provinces now.
            bool influenceSynced = ProvinceInfluence.Sync(cycle);
            if (provincesBuilt || influenceSynced)
            {
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
            }
            return; // already generated this cycle
""",
            ),
            (
                r"""        CorruptionSpread.Reset(); // new world, so drop cached adjacency + pressure
""",
                r"""        // Provinces PV2a: the start kingdom's influence is spread over its provinces,
        // and every kingdom's value derived from them.
        ProvinceInfluence.Sync(cycle);
        CorruptionSpread.Reset(); // new world, so drop cached adjacency + pressure
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.cs",
        "f053a59d65ba8b2c7f595c0580e8d22e2d26c3f3",
        "7f1591f75a5e21adacba8329182af8f944356ca6",
        [
            (
                r"""        if (_lens == StrategicLens.Political)
            c = ProvinceShade(c, col, row);
""",
                r"""        if (_lens == StrategicLens.Political)
            c = ProvinceShade(c, col, row);
        // PV2a (section 7b): the Reach lens reads the guild's influence per province.
        else if (_lens == StrategicLens.Reach)
            c = ProvinceReach(c, t, col, row);
""",
            ),
            (
                r"""    private Color CorruptionLensColor(in WorldTile t)
""",
                r"""    // ── Provinces on the Reach lens (docs/provinces_v1.md section 7b, PV2a) ──
    /// <summary>How much gold a held province's edge tiles take.</summary>
    private const float HeldEdgeGold = 0.65f;

    /// <summary>The Reach lens per province: the realm's standing colour, filled from
    /// the void in proportion to the guild's influence in the province, so a province
    /// worked to 40 reads apart from the untouched ones around it. A held province's
    /// edge tiles (those with a neighbour outside it) draw in gold. Water, and land in
    /// no province, keep the kingdom read.</summary>
    private Color ProvinceReach(Color c, in WorldTile t, int col, int row)
    {
        if (t.IsWater)
            return c;
        int pi = _world.ProvinceIndexAt(col, row);
        if (pi < 0)
            return c;
        var province = _world.Provinces[pi];
        var stance = KingdomStance.Neutral;
        var cyc = SaveManager.ActiveSave?.Cycle;
        if (cyc != null && !string.IsNullOrEmpty(province.KingdomId))
            stance = CouncilQueries.StanceFor(cyc, province.KingdomId);
        Color voidDim = new Color(0.10f, 0.10f, 0.13f);
        Color fill = voidDim.Lerp(StanceColor(stance), Mathf.Clamp(province.Influence, 0, 100) / 100f);
        if (province.Held && OnProvinceEdge(col, row, pi))
            fill = fill.Lerp(UITheme.Gold, HeldEdgeGold);
        return fill;
    }

    /// <summary>True when any neighbour of the tile lies outside its province: another
    /// province, water, or off the map.</summary>
    private bool OnProvinceEdge(int col, int row, int pi)
    {
        var (q, r) = HexCoord.OffsetToAxial(col, row);
        for (int i = 0; i < 6; i++)
        {
            var (dq, dr) = HexCoord.AxialDirections[i];
            var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
            if (_world.ProvinceIndexAt(nc, nr) != pi)
                return true;
        }
        return false;
    }

    private Color CorruptionLensColor(in WorldTile t)
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campus/CampusGuildPanel.cs",
        "08e21c829488dd19641cee5622861f8bfbad5015",
        "4965395470df00d7233aca073dbb032b94088a3f",
        [
            (
                r"""            ProvinceSaveAssert.AssertAll(); // provinces PV1: the partition and its save
""",
                r"""            ProvinceSaveAssert.AssertAll(); // provinces PV1 and PV2a: the partition, influence, their save
""",
            ),
            (
                r"""        }, "Print the active world's provinces: sizes, seats, how often borders follow rivers and ridges, each kingdom, and the checks.");
""",
                r"""        }, "Print the active world's provinces: sizes, seats, how often borders follow rivers and ridges, each kingdom, and the checks.");
        Act(verify, "Province Influence", () =>
        {
            GD.Print(ProvinceInfluence.Report(SaveManager.ActiveSave?.Cycle));
        }, "Provinces PV2a: print each kingdom's influence and each province's, its hold, and where the castle stands.");
        Act(verify, "Province Deed +20", () =>
        {
            var deedCycle = SaveManager.ActiveSave?.Cycle;
            GD.Print(ProvinceInfluence.DebugDeed(deedCycle, 20));
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
        }, "Provinces PV2a: +20 guild influence in the castle's province, as a deed gives, then saves. From 40, one press holds it.");
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
        fail("usage: apply_provinces_pv2a.py <repo_root> [--dry-run]")
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
    print("WRITTEN: all ten files verified against their expected blobs.")


if __name__ == "__main__":
    main()
