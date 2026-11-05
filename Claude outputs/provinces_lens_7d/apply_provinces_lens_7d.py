#!/usr/bin/env python3
# Provinces on every lens (docs/provinces_v1_7d_lenses.md, section 7d of the provinces
# spec, 2026-11-03): province ink on every lens, a least width on screen for every ink,
# the Political lens's tones without the darker border tile, the Reach lens in bands
# over a quiet map, province names and numbers when zoomed in, a rim on the readout's
# province, a key per lens and the realm's stance in the readout.
#
# Drops in four files kept beside this script (WorldAtlas3D.Provinces.cs, new;
# StrategicView.ProvinceHover.cs, ProvinceInfluence.cs and ProvinceSaveAssert.cs, which
# replace the 7c versions) and edits two live files surgically (WorldAtlas3D.cs,
# StrategicView.cs). Rules: every starting blob checked (a new file must not exist yet),
# every anchor found exactly once, added text ASCII apart from box-drawing characters,
# no banned dash, brackets balanced per edit, every result equal to the blob it was
# validated at, nothing written unless every step succeeds, every written file
# verified, and a second run refuses.
#
# Usage: python3 apply_provinces_lens_7d.py <repo_root> [--dry-run]

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
    ("Scripts/Systems/Strategic/WorldAtlas3D.Provinces.cs", None, "838ff2cb82121c686bce18bc0ce47ca69e4b1954"),
    ("Scripts/Systems/Strategic/StrategicView.ProvinceHover.cs", "7106b00f5949130fd0e6c6e34a3cffb388251539", "0d88ebddd429de03a034e3b4e9849bf4612ef1d1"),
    ("Scripts/Systems/Strategic/ProvinceInfluence.cs", "f0c5a8f953aae376fd47b363f963751d69fcabed", "d8a6941b5f516d65e042607ee8462b71ec7dcea5"),
    ("Scripts/Systems/Verification/ProvinceSaveAssert.cs", "a44eca949279fdc8d905623f4590aeae72c577b8", "dc55e3c7bb763a933a2a23954d574de58110cb19"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.cs",
        "6da524d069f9e864eec681f426a8a809973a3c14",
        "6429483bb29f606d576ed6db879a3d979df58107",
        [
            (
                r"""        _borderLayer = MakeEdgeLayer("BorderLayer", borders,
            new Vector3(HexR * 0.98f, 0.02f, 0.06f), Hex3DPalette.BorderInk);
""",
                r"""        _borderLayer = MakeEdgeLayer("BorderLayer", borders,
            new Vector3(HexR * 0.98f, 0.02f, RealmInkWidth), Hex3DPalette.BorderInk);
        _borderLayer.MaterialOverride = RealmInkMaterial();   // provinces 7d: a least width on screen
        RebuildProvinceInk();                                 // provinces 7d: province borders on every lens
""",
            ),
            (
                r"""        if (_borderLayer != null) _borderLayer.Visible = false;
""",
                r"""        if (_borderLayer != null) _borderLayer.Visible = false;
        SetProvinceLayersForCity(true);   // provinces 7d: province ink, rim and names
""",
            ),
            (
                r"""        if (_borderLayer != null) _borderLayer.Visible = true;
""",
                r"""        if (_borderLayer != null) _borderLayer.Visible = true;
        SetProvinceLayersForCity(false);   // provinces 7d
""",
            ),
            (
                r"""        UpdateLabelScales();   // keep map labels a constant on-screen size as the zoom changes
""",
                r"""        UpdateLabelScales();   // keep map labels a constant on-screen size as the zoom changes
        UpdateInkWidths();     // provinces 7d: every ink keeps a least width on screen
        UpdateProvinceNameFade(zoom01);   // provinces 7d: names show only when the camera is close
""",
            ),
            (
                r"""        // LAST, so the pieces draw over the places they are standing on.
        BuildPieceMarkers();
""",
                r"""        // Provinces 7d: the names, before the pieces and the dispatch marks.
        AddProvinceNames();

        // LAST, so the pieces draw over the places they are standing on.
        BuildPieceMarkers();
""",
            ),
            (
                r"""        ApplyRangeOverlay();   // a force's reach, if one is lit (2026-09-29)
""",
                r"""        RefreshProvinceNames();   // provinces 7d: the numbers follow the lens and the influence
        ApplyRangeOverlay();   // a force's reach, if one is lit (2026-09-29)
""",
            ),
            (
                r"""    // ── Provinces on the Political lens (docs/provinces_v1.md, PV1) ─────────
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

""",
                r"""    // ── Provinces on the Political lens (docs/provinces_v1.md, PV1) ─────────
    /// <summary>Brightness of the four province tones: provinces of one kingdom that
    /// share a border take different tones, so they read apart without a new hue.</summary>
    private static readonly float[] ProvinceToneScale = { 1.00f, 0.90f, 1.08f, 0.82f };

    /// <summary>Shade a Political-lens color by its tile's province tone. The borders
    /// between provinces are drawn as ink on every lens (section 7d,
    /// WorldAtlas3D.Provinces.cs), so no tile is darkened for them. A world without
    /// provinces draws as before.</summary>
    private Color ProvinceShade(Color c, int col, int row)
    {
        int pi = _world.ProvinceIndexAt(col, row);
        if (pi < 0)
            return c;
        float k = ProvinceToneScale[_world.Provinces[pi].Tone & 3];
        return new Color(
            Mathf.Clamp(c.R * k, 0f, 1f),
            Mathf.Clamp(c.G * k, 0f, 1f),
            Mathf.Clamp(c.B * k, 0f, 1f),
            c.A);
    }

""",
            ),
            (
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

""",
                r"""    // ── Provinces on the Reach lens (docs/provinces_v1.md section 7b, PV2a) ──
    /// <summary>The Reach lens per province (section 7d): over the quiet base that
    /// ReachLensColor draws, the guild's blue in steps keyed to the rules, read from
    /// ProvinceInfluence.BandOf. Growing (1 to 39) washes lightly, deeper with the
    /// number; Ready (40 or more, not held) washes strongly; a held province is gold, and
    /// one held but below 60 keeps the strong wash with its edge tiles gold. Water, and
    /// land in no province, keep the base.</summary>
    private Color ProvinceReach(Color c, in WorldTile t, int col, int row)
    {
        if (t.IsWater)
            return c;
        int pi = _world.ProvinceIndexAt(col, row);
        if (pi < 0)
            return c;
        var province = _world.Provinces[pi];
        switch (ProvinceInfluence.BandOf(province))
        {
            case ProvinceInfluence.Band.Held:
                return c.Lerp(UITheme.Gold, ReachGold);
            case ProvinceInfluence.Band.HeldSlipping:
                Color slipping = c.Lerp(UITheme.ArcaneBlue, ReachStrong);
                return OnProvinceEdge(col, row, pi) ? slipping.Lerp(UITheme.Gold, ReachEdgeGold) : slipping;
            case ProvinceInfluence.Band.Ready:
                return c.Lerp(UITheme.ArcaneBlue, ReachStrong);
            case ProvinceInfluence.Band.Growing:
                float grown = Mathf.Clamp((province.Influence - 1) / (float)Mathf.Max(1, ProvinceInfluence.PassiveCap - 2), 0f, 1f);
                return c.Lerp(UITheme.ArcaneBlue, Mathf.Lerp(ReachGrowFrom, ReachGrowTo, grown));
            default:
                return c;
        }
    }

""",
            ),
            (
                r"""    private Color ReachLensColor(in WorldTile t)
    {
        if (t.IsWater)
            return UITheme.TerrainWater.Darkened(0.55f);
        int influence = 0;
        var stance = KingdomStance.Neutral;
        if (!string.IsNullOrEmpty(t.KingdomId))
        {
            if (_kingdoms != null && _kingdoms.TryGetValue(t.KingdomId, out var ks))
                influence = Mathf.Clamp(ks.PlayerInfluence, 0, 100);
            var cyc = SaveManager.ActiveSave?.Cycle;
            if (cyc != null)
                stance = CouncilQueries.StanceFor(cyc, t.KingdomId);
        }
        Color voidDim = new Color(0.10f, 0.10f, 0.13f);
        return voidDim.Lerp(StanceColor(stance), influence / 100f);
    }
""",
                r"""    /// <summary>The Reach lens's ground (section 7d): the Political colour quieted, its
    /// saturation cut and moved toward the canvas, so untouched land reads as known
    /// ground and the guild's blue and gold read on any realm. Water keeps its Political
    /// colour. The court's stance left this lens for the province readout.</summary>
    private Color ReachLensColor(in WorldTile t)
    {
        Color c = PoliticalLensColor(t);
        if (t.IsWater)
            return c;
        c.ToHsv(out float hue, out float sat, out float val);
        return Color.FromHsv(hue, sat * ReachBaseSaturation, val, 1f).Lerp(UITheme.CanvasUnseen, ReachBaseToCanvas);
    }
""",
            ),
            (
                r"""
    private static Color StanceColor(KingdomStance s) => s switch
    {
        KingdomStance.Hostile    => new Color(0.75f, 0.24f, 0.22f),
        KingdomStance.Unfriendly => new Color(0.80f, 0.46f, 0.22f),
        KingdomStance.Neutral    => new Color(0.48f, 0.50f, 0.55f),
        KingdomStance.Friendly   => new Color(0.28f, 0.62f, 0.60f),
        KingdomStance.Allied     => new Color(0.30f, 0.72f, 0.40f),
        _                        => new Color(0.48f, 0.50f, 0.55f),
    };
""",
                r"""""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "db22b78a0dd58d38c0593409f1be8377c5bbc99d",
        "341dfab7cd6d329b697e3c691210d4a31b2d388e",
        [
            (
                r"""        lensStack.AddChild(BuildReachKey());
""",
                r"""        lensStack.AddChild(BuildLensKey());   // provinces 7d: the lens key
""",
            ),
            (
                r"""        UpdateReachKey();   // provinces 7c: the Reach lens's key
""",
                r"""        UpdateLensKey();    // provinces 7d: the lens key
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
        fail("usage: apply_provinces_lens_7d.py <repo_root> [--dry-run]")
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
