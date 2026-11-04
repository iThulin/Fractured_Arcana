#!/usr/bin/env python3
# Provinces, slice PV1 (docs/provinces_v1.md, 2026-11-03): provinces as data,
# built at generation and for legacy worlds, drawn on the Political lens, named
# on the place card, reported and asserted from the guild panel. No gameplay
# change.
#
# Surgical edits to six live files. The three new files (Province.cs,
# ProvinceBuilder.cs, ProvinceSaveAssert.cs) are dropped in beside this script's
# run and only checked here. Rules: every starting blob checked, every anchor
# found exactly once, added text ASCII apart from box-drawing characters, no
# banned dash, brackets balanced per edit, nothing written unless every edit
# succeeds, every written blob verified, and a second run refuses.
#
# Usage: python3 apply_provinces_pv1.py <repo_root> [--dry-run]

import collections
import difflib
import hashlib
import sys
from pathlib import Path


def blob(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


PAIRS = [("(", ")"), ("[", "]"), ("{", "}")]

NEW_FILES = [
    "Scripts/Systems/Overworld/WorldGen/Province.cs",
    "Scripts/Systems/Overworld/WorldGen/ProvinceBuilder.cs",
    "Scripts/Systems/Verification/ProvinceSaveAssert.cs",
]

EDITS = [
    (
        "Scripts/Systems/Overworld/WorldGen/WorldData.cs",
        "7537704b863ee0d0843ae365f10ec947c888e5d9",
        [
            (
                """\
//                 KingdomState.cs (territories reference tiles)
""",
                """\
//                 KingdomState.cs (territories reference tiles),
//                 ProvinceBuilder.cs (Provinces, ProvinceMap)
""",
            ),
            (
                """\
    /// <summary>The rolled continental topology, for save/debug.</summary>
    public string ContinentStyle = "";
""",
                """\
    /// <summary>The rolled continental topology, for save/debug.</summary>
    public string ContinentStyle = "";

    /// <summary>The world's provinces (docs/provinces_v1.md): each kingdom's land cut
    /// into about six by ProvinceBuilder, at generation (step 5i) or, for a world saved
    /// before provinces, the first time the cycle opens. Frozen once built: never
    /// reordered or shortened, so a province's position here is the value ProvinceMap
    /// stores for its tiles. Save data outside the world names a province by its Id,
    /// never by this position. Additive: empty on a legacy world.</summary>
    public List<Province> Provinces = new();

    /// <summary>Each tile's province, one byte per tile in Tiles' row-major order: the
    /// province's position in Provinces, or NoProvince for water. A byte array, so the
    /// save carries the whole map as one short base64 string rather than a field on
    /// every tile. Null on a world saved before provinces existed.</summary>
    public byte[] ProvinceMap;

    /// <summary>ProvinceMap's value for a tile in no province (water).</summary>
    public const byte NoProvince = 255;
""",
            ),
            (
                """\
        int zi = GetTile(x, y).ShardZoneIndex;
        return (zi >= 0 && zi < ShardZones.Count) ? ShardZones[zi] : null;
    }
""",
                """\
        int zi = GetTile(x, y).ShardZoneIndex;
        return (zi >= 0 && zi < ShardZones.Count) ? ShardZones[zi] : null;
    }

    // ── Provinces (docs/provinces_v1.md) ─────────────────────────────────
    /// <summary>True once the world has its provinces. False on a world saved before
    /// them until ProvinceBuilder.EnsureBuilt runs. A method, not a property, so the
    /// save serializer never writes it.</summary>
    public bool HasProvinces()
        => ProvinceMap != null && Tiles != null && ProvinceMap.Length == Tiles.Length
           && Provinces != null && Provinces.Count > 0;

    /// <summary>Position in Provinces of the province holding a tile, or -1 for water,
    /// out of bounds, land opened from water since the build, or a world without
    /// provinces.</summary>
    public int ProvinceIndexAt(int x, int y)
    {
        if (!InBounds(x, y) || !HasProvinces())
            return -1;
        int p = ProvinceMap[y * Width + x];
        return (p != NoProvince && p < Provinces.Count) ? p : -1;
    }

    /// <summary>The province holding a tile, or null where ProvinceIndexAt gives -1.</summary>
    public Province ProvinceAt(int x, int y)
    {
        int p = ProvinceIndexAt(x, y);
        return p >= 0 ? Provinces[p] : null;
    }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/WorldGen/WorldGenerator.cs",
        "20f73815555642b733ee6a8336e087c912f8b9d2",
        [
            (
                """\
        // road edges), regardless of the per-kingdom town cap.
        Settlements.AddJunctionTowns(world);
""",
                """\
        // road edges), regardless of the per-kingdom town cap.
        Settlements.AddJunctionTowns(world);

        // ── 5i. Provinces (docs/provinces_v1.md): each kingdom's land cut into about
        // six, seeded on its seat, cities and towns, with borders on rivers and ridges.
        // Draws no RNG, so every later step's output is unchanged by it.
        ProvinceBuilder.Build(world);
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campaign/CycleInitializer.cs",
        "d091744d841a04df9db3b3a41dc6a1b7793937c1",
        [
            (
                """\
        if (cycle.World != null && cycle.World.Tiles.Length > 0)
            return; // already generated this cycle
""",
                """\
        if (cycle.World != null && cycle.World.Tiles.Length > 0)
        {
            // Provinces (docs/provinces_v1.md, PV1): a world saved before provinces
            // existed gets them now. The build reads only what generation fixed
            // (territories, elevation, rivers, settlements), so the world gets the
            // partition a fresh generation of it would have made. Saved at once, so
            // it is built once.
            if (ProvinceBuilder.EnsureBuilt(cycle.World))
            {
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
            }
            return; // already generated this cycle
        }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.cs",
        "9168e39f3f94387ff93fdaac306b430080984e85",
        [
            (
                """\
        Color c = LensColor(t);
        // A1: the palette is now authored as FINAL lit-scene painterly swatches in
""",
                """\
        Color c = LensColor(t);
        // Provinces (docs/provinces_v1.md, PV1): the Political lens shows the partition.
        if (_lens == StrategicLens.Political)
            c = ProvinceShade(c, col, row);
        // A1: the palette is now authored as FINAL lit-scene painterly swatches in
""",
            ),
            (
                """\
    private Color CorruptionLensColor(in WorldTile t)
""",
                """\
    // ── Provinces on the Political lens (docs/provinces_v1.md, PV1) ─────────
    /// <summary>Brightness of the four province tones: provinces of one kingdom that
    /// share a border take different tones, so they read apart without a new hue.</summary>
    private static readonly float[] ProvinceToneScale = { 1.00f, 0.90f, 1.08f, 0.82f };

    /// <summary>How much darker a province-border tile draws.</summary>
    private const float ProvinceBorderShade = 0.30f;

    /// <summary>Shade a Political-lens color by its tile's province: the province's
    /// tone, and darker where the province meets a lower-numbered province of the same
    /// kingdom, so each border inside a kingdom draws as one line of tiles. A border
    /// between kingdoms already reads as a change of hue and is left alone. A world
    /// without provinces draws as before.</summary>
    private Color ProvinceShade(Color c, int col, int row)
    {
        int pi = _world.ProvinceIndexAt(col, row);
        if (pi < 0)
            return c;
        var province = _world.Provinces[pi];
        float k = ProvinceToneScale[province.Tone & 3];
        var (q, r) = HexCoord.OffsetToAxial(col, row);
        for (int i = 0; i < 6; i++)
        {
            var (dq, dr) = HexCoord.AxialDirections[i];
            var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
            int ni = _world.ProvinceIndexAt(nc, nr);
            if (ni >= 0 && ni < pi && _world.Provinces[ni].KingdomId == province.KingdomId)
            {
                k *= 1f - ProvinceBorderShade;
                break;
            }
        }
        return new Color(
            Mathf.Clamp(c.R * k, 0f, 1f),
            Mathf.Clamp(c.G * k, 0f, 1f),
            Mathf.Clamp(c.B * k, 0f, 1f),
            c.A);
    }

    private Color CorruptionLensColor(in WorldTile t)
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "b333776e2392c7cf33e3e40d0ac4a0dc0f102d1f",
        [
            (
                """\
        string kind = sp.Source switch
        {
            "Start" => "the guild's dock",
""",
                """\
        // Provinces (docs/provinces_v1.md, PV1): the province's name comes before the realm's.
        var placeProvince = _world.ProvinceAt(sp.X, sp.Y);
        if (placeProvince != null && !string.IsNullOrEmpty(placeProvince.Name))
            kingdomLabel = $"{placeProvince.Name}, {kingdomLabel}";
        string kind = sp.Source switch
        {
            "Start" => "the guild's dock",
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campus/CampusGuildPanel.cs",
        "cd8b7c9014d434e70a25f5f9ec22b51bb4100e23",
        [
            (
                """\
            IntervalSaveAssert.AssertAll(); // Standing Orders O1: the interval
        });
""",
                """\
            IntervalSaveAssert.AssertAll(); // Standing Orders O1: the interval
            ProvinceSaveAssert.AssertAll(); // provinces PV1: the partition and its save
        });
        Act(verify, "Province Report", () =>
        {
            var provinceWorld = SaveManager.ActiveSave?.Cycle?.World;
            GD.Print(ProvinceBuilder.Report(provinceWorld));
        }, "Print the active world's provinces: sizes, seats, how often borders follow rivers and ridges, each kingdom, and the checks.");
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
    bad = [c for c, k in extra.items() if k > 0 and not (0x2500 <= ord(c) <= 0x257F)]
    return bad


def check_new_file(root: Path, rel: str) -> None:
    path = root / rel
    if not path.exists():
        fail(f"new file missing: {rel} (drop it in first)")
    text = path.read_bytes().decode("utf-8")
    if "\r" in text:
        fail(f"{rel} has CR characters")
    bad = sorted({c for c in text if ord(c) > 127 and not (0x2500 <= ord(c) <= 0x257F)})
    if bad:
        fail(f"{rel} carries characters outside ASCII and box drawing: {bad!r}")
    if "—" in text or "\\u2014" in text or "–" in text:
        fail(f"{rel} carries a dash the house style bans")


def main() -> None:
    if len(sys.argv) < 2:
        fail("usage: apply_provinces_pv1.py <repo_root> [--dry-run]")
    root = Path(sys.argv[1])
    dry = "--dry-run" in sys.argv[2:]
    for rel in NEW_FILES:
        check_new_file(root, rel)
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
            bad = added_chars_ok(anchor, new)
            if bad:
                fail(f"{rel}: replacement adds characters outside ASCII and box drawing: {bad!r}")
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
        diff = list(difflib.unified_diff(original.splitlines(), text.splitlines(),
                                         fromfile="a/" + rel, tofile="b/" + rel, lineterm="", n=0))
        added = sum(1 for l in diff if l.startswith("+") and not l.startswith("+++"))
        removed = sum(1 for l in diff if l.startswith("-") and not l.startswith("---"))
        print(f"{rel}: {before[:7]} -> {after[:7]}  (+{added} -{removed})")
    for rel in NEW_FILES:
        print(f"{rel}: new, {blob((root / rel).read_bytes())[:7]}")

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
