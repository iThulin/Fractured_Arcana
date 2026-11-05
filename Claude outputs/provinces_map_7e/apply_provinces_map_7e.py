#!/usr/bin/env python3
# The strategic map after 7d (docs/provinces_v1_7e_map_polish.md, section 7e of the
# provinces spec, 2026-11-04): the sea stops shadowing itself; the whole-world view fits
# the world into the screen the panels leave free, with the camera's pitch counted, over a
# sheet of paper instead of a dark void; settlements get map marks that keep their size;
# map names get a halo scaled to their glyphs; the clock moves into the global bar's
# centre, and the Next line drops to a strip under it.
#
# Drops in two new files kept beside this script (WorldAtlas3D.Frame.cs and
# WorldAtlas3D.Marks.cs) and edits five live files surgically (WorldAtlas3D.cs,
# WorldAtlas3D.Provinces.cs, HudManager.cs, StrategicView.cs, StrategicView.Dispatch.cs).
# Rules: every starting blob checked (a new file must not exist yet), every anchor found
# exactly once, added text ASCII apart from box-drawing characters, no banned dash,
# brackets balanced per edit, every result equal to the blob it was validated at, nothing
# written unless every step succeeds, every written file verified, and a second run
# refuses.
#
# Usage: python3 apply_provinces_map_7e.py <repo_root> [--dry-run]

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
    ("Scripts/Systems/Strategic/WorldAtlas3D.Frame.cs", None, "f2788790dea20a8e9992d32574c8c1b98644da5f"),
    ("Scripts/Systems/Strategic/WorldAtlas3D.Marks.cs", None, "d9a5af3b9f4fb94b56674e50f56750085844cf79"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.cs",
        "6429483bb29f606d576ed6db879a3d979df58107",
        "64166fbe2616c4fe78749fe544f21474933fc1bf",
        [
            (
                r"""        _waterLayer = new MultiMeshInstance3D { Name = "WaterLayer", Multimesh = waterMm };
""",
                r"""        // Provinces 7e (PR30): the sea casts no shadow. Thousands of coplanar water slabs
        // shadowing themselves printed diagonal panes across it, the canvas layer's old
        // artifact (see its note below). Land still casts onto the sea, so coasts keep theirs.
        _waterLayer = new MultiMeshInstance3D
        {
            Name = "WaterLayer",
            Multimesh = waterMm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
""",
            ),
            (
                r"""        AddChild(_waterLayer);
        AddChild(_canvasLayer);
""",
                r"""        AddChild(_waterLayer);
        AddChild(_canvasLayer);
        RebuildPaperSheet();   // provinces 7e (PR31): the paper past the map's edge
""",
            ),
            (
                r"""                BackgroundColor = UITheme.WorldDeep,
""",
                r"""                // Provinces 7e (PR31): paper, as the sheet under the world, for whatever
                // lies past the far plane (WorldAtlas3D.Frame.cs).
                BackgroundColor = PaperVoid,
""",
            ),
            (
                r"""    /// <summary>The zoom distance that makes the whole world FILL the viewport (minimal
    /// margin), accounting for the viewport aspect: a wide world on a wide screen is
    /// width-bound, so the vertical ortho size is driven by width/aspect. Falls back to
    /// 16:9 if the viewport isn't sized yet.</summary>
    private float OverviewDist()
    {
        float w = _world.Width * ColSpacing;
        float h = _world.Height * RowSpacing;
        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        float aspect = vp.Y > 1f ? vp.X / vp.Y : 1.7778f;
        // Vertical ortho size needed to show the whole world; small pad off the border.
        float fitSize = Mathf.Max(h, w / Mathf.Max(0.1f, aspect)) * 1.04f;
        return Mathf.Clamp(fitSize / OrthoSizeFactor, CamDistMin, CamDistMax);
    }
""",
                r"""    /// <summary>The zoom distance that makes the whole world fill the screen the host's
    /// panels leave free (provinces 7e, PR31: FitWorldDist in WorldAtlas3D.Frame.cs),
    /// accounting for the viewport aspect and for the pitch, which shortens the world's
    /// depth on screen. Falls back to 16:9 if the viewport isn't sized yet.</summary>
    private float OverviewDist()
    {
        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        float aspect = vp.Y > 1f ? vp.X / vp.Y : 1.7778f;
        return FitWorldDist(aspect);
    }
""",
            ),
            (
                r"""        float pitchDeg = _pitchOverrideDeg >= 0f
            ? _pitchOverrideDeg
            : Mathf.Lerp(40f, 68f, zoom01);
""",
                r"""        float pitchDeg = PitchDegFor(_camDist);   // provinces 7e: the rule the whole-world fit counts too
""",
            ),
            (
                r"""            _camera.Projection = Camera3D.ProjectionType.Perspective;
        }
        ZoomChanged?.Invoke(zoom01);
""",
                r"""            _camera.Projection = Camera3D.ProjectionType.Perspective;
        }
        ApplyViewCentre();     // provinces 7e (PR31): the target shows in the middle of the free screen
        ZoomChanged?.Invoke(zoom01);
""",
            ),
            (
                r"""        UpdateProvinceNameFade(zoom01);   // provinces 7d: names show only when the camera is close
""",
                r"""        UpdateProvinceNameFade(zoom01);   // provinces 7d: names show only when the camera is close
        UpdateSettlementMarks();          // provinces 7e (PR32): the marks keep their width and fade close in
""",
            ),
            (
                r"""        _cityHiddenMarkers.Clear();   // its nodes live in _markers, just freed above
""",
                r"""        _cityHiddenMarkers.Clear();   // its nodes live in _markers, just freed above
        _settlementMarks.Clear();     // provinces 7e: so do the settlement marks'
""",
            ),
            (
                r"""                var lbl = StackedLabel(new Vector2I(s.CenterX, s.CenterY), label, c,
                                       MarkerPos(s.CenterX, s.CenterY, side + 1.4f), 88, 0.028f);
""",
                r"""                var lbl = StackedLabel(new Vector2I(s.CenterX, s.CenterY), label, c,
                                       MarkerPos(s.CenterX, s.CenterY, side + 1.4f), 88, 0.028f);
                // Provinces 7e (PR32): the name stands above its seat's mark, and labels stacked
                // after it on this tile (the pieces) start half a line higher to clear it.
                lbl.VerticalAlignment = VerticalAlignment.Bottom;
                var seatTile = new Vector2I(s.CenterX, s.CenterY);
                if (_labelStack.TryGetValue(seatTile, out var stacked))
                    _labelStack[seatTile] = (stacked.anchor, stacked.frac + 0.028f * 0.5f);
""",
            ),
            (
                r"""                    block.Visible = lbl.Visible = !_cityMode;
                }
            }
        }
""",
                r"""                    block.Visible = lbl.Visible = !_cityMode;
                }
            }
            // Provinces 7e (PR32): the settlement's map mark, after its block and its name so
            // the block's visibility is settled first.
            AddSettlementMark(s.CenterX, s.CenterY, home, s.IsSeat, city, block);
        }
""",
            ),
            (
                r"""            Modulate = color,
            OutlineSize = 10,
""",
                r"""            Modulate = color,
            // Provinces 7e (PR33): a halo about 30 percent of the glyph, in near-black ink, so
            // a name reads over snow and over gold. Labels with a style of their own (the
            // province names) set theirs after.
            OutlineSize = LabelHaloSize(fontSize),
            OutlineModulate = LabelHaloInk,
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.Provinces.cs",
        "838ff2cb82121c686bce18bc0ce47ca69e4b1954",
        "f894b5f41f5faddf27557728a8936ddaa21ea861",
        [
            (
                r"""                _provinceInkLayer.MaterialOverride = _provinceInkMaterial;
""",
                r"""                _provinceInkLayer.MaterialOverride = _provinceInkMaterial;
                _provinceInkMaterial.RenderPriority = ProvinceInkPriority;   // provinces 7e: drawn before the settlement marks
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/HudManager.cs",
        "5748ab8ddf0b14beb19d592cccc95a09ebde4f78",
        "91d92ff7f3c6e5772545bdab98fcc0ea8baaccdd",
        [
            (
                r"""    private const double NextRefreshSeconds = 0.5;
""",
                r"""    private const double NextRefreshSeconds = 0.5;

    // Provinces 7e (PR34, PR35): a screen that lays its own controls over the bar's centre (the
    // strategic map's clock) claims it. The bar's date hides and the Next line drops to a strip
    // hung under the bar's centre, in the bar's fill. Given back when the screen leaves.
    private Node _centreOwner;
    private HBoxContainer _barRow;
    private int _nextSlot = -1;
    private PanelContainer _nextStrip;

    /// <summary>Height of the Next strip under the bar, for screens that keep their content
    /// clear of it.</summary>
    public const int NextStripHeight = 28;
""",
            ),
            (
                r"""        _nextButton.Pressed += GoNext;
        row.AddChild(_nextButton);
""",
                r"""        _nextButton.Pressed += GoNext;
        row.AddChild(_nextButton);
        _barRow = row;                        // provinces 7e: where the Next line returns to
        _nextSlot = _nextButton.GetIndex();
""",
            ),
            (
                r"""        RefreshReadouts(force: true);
        RefreshVisibility();
    }
""",
                r"""        BuildNextStrip();   // provinces 7e (PR35)
        ApplyCentre();      // a screen may have claimed the centre before the bar was built
        RefreshReadouts(force: true);
        RefreshVisibility();
    }
""",
            ),
            (
                r"""            _nextButton.Visible = false;
            return;
""",
                r"""            _nextButton.Visible = false;
            SyncNextStrip();   // provinces 7e
            return;
""",
            ),
            (
                r"""        _nextButton.Visible = text != null;
""",
                r"""        _nextButton.Visible = text != null;
        SyncNextStrip();   // provinces 7e: the strip shows only with a step
""",
            ),
            (
                r"""        if (_nextButton.Text != text)
        {
            _nextButton.Text = text;
""",
                r"""        if (_nextButton.Text != text)
        {
            _nextButton.Text = text;
            PulseNext();   // provinces 7e (PR35): a new step pulses once
""",
            ),
            (
                r"""            _returnButton.Visible = ShouldShowReturnToCampus();
        }
        RefreshNextLine();
""",
                r"""            _returnButton.Visible = ShouldShowReturnToCampus();
        }
        // Provinces 7e: a screen that left without giving the centre back gives it now.
        if (_centreOwner != null && (!IsInstanceValid(_centreOwner) || !_centreOwner.IsInsideTree()))
        {
            ReleaseCentre(_centreOwner);
        }
        RefreshNextLine();
""",
            ),
            (
                r"""    /// <summary>Update the resource labels when their values change. Cheap
""",
                r"""    // ── The bar's centre (provinces 7e, PR34 and PR35) ───────────────────

    /// <summary>True while the bar is drawn. A screen that lays its own controls over the
    /// bar's centre follows it, so they hide with the bar (the campus overlay hides it).</summary>
    public bool BarVisible => _layer != null && _layer.Visible;

    /// <summary>A screen lays its own controls over the bar's centre (the strategic map's
    /// clock, on its own canvas layer just above the bar's). The bar's date hides, since the
    /// screen shows its own, and the Next line drops to a strip under the bar's centre. The
    /// screen gives the centre back with ReleaseCentre when it leaves.</summary>
    public void ClaimCentre(Node owner)
    {
        if (owner == null)
        {
            return;
        }
        _centreOwner = owner;
        ApplyCentre();
    }

    /// <summary>Give the bar's centre back: its date shows again and the Next line returns to
    /// its place in the bar. Ignored unless <paramref name="owner"/> holds the claim.</summary>
    public void ReleaseCentre(Node owner)
    {
        if (owner == null || owner != _centreOwner)
        {
            return;
        }
        _centreOwner = null;
        ApplyCentre();
    }

    private bool CentreClaimed => _centreOwner != null;

    /// <summary>Put the date and the Next line where the claim says. Waits for BuildBar,
    /// which calls it again once the bar exists.</summary>
    private void ApplyCentre()
    {
        if (!IsInstanceValid(_nextButton) || !IsInstanceValid(_barRow) || !IsInstanceValid(_nextStrip))
        {
            return;
        }
        bool claimed = CentreClaimed;
        if (IsInstanceValid(_lunationLabel))
        {
            _lunationLabel.Visible = !claimed;
        }
        Node home = claimed ? (Node)_nextStrip : _barRow;
        if (_nextButton.GetParent() != home)
        {
            _nextButton.GetParent()?.RemoveChild(_nextButton);
            home.AddChild(_nextButton);
            if (!claimed && _nextSlot >= 0)
            {
                _barRow.MoveChild(_nextButton, Mathf.Min(_nextSlot, _barRow.GetChildCount() - 1));
            }
        }
        SyncNextStrip();
    }

    /// <summary>The strip shows only while a screen holds the centre and there is a step.</summary>
    private void SyncNextStrip()
    {
        if (IsInstanceValid(_nextStrip))
        {
            _nextStrip.Visible = CentreClaimed && IsInstanceValid(_nextButton) && _nextButton.Visible;
        }
    }

    /// <summary>The strip that holds the Next line while a screen has the bar's centre: hung
    /// under the bar's middle, in the bar's own fill and violet rule.</summary>
    private void BuildNextStrip()
    {
        _nextStrip = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0f,
            AnchorRight = 0.5f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetTop = BarHeight,
            Visible = false,
        };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.CampusTitleBarBg,
            BorderColor = UITheme.CampusTitleBarBorder,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 0,
            ContentMarginBottom = 2,
        };
        _nextStrip.AddThemeStyleboxOverride("panel", style);
        _layer.AddChild(_nextStrip);
    }

    /// <summary>One pulse when the Next line changes step, so a new step is seen wherever the
    /// line sits.</summary>
    private void PulseNext()
    {
        Control target = CentreClaimed && IsInstanceValid(_nextStrip) ? (Control)_nextStrip : _nextButton;
        if (!IsInstanceValid(target) || !target.IsInsideTree())
        {
            return;
        }
        target.CreateTween()
              .TweenProperty(target, "modulate", Colors.White, 0.9)
              .From(new Color(1.8f, 1.6f, 1.0f))
              .SetTrans(Tween.TransitionType.Sine)
              .SetEase(Tween.EaseType.Out);
    }

    /// <summary>Update the resource labels when their values change. Cheap
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "341dfab7cd6d329b697e3c691210d4a31b2d388e",
        "dec72fcdbddcc1203730961dd068ee3130c16b96",
        [
            (
                r"""        _atlas3D.SetRevealAll(_debugReveal);
        _atlas3D.SetWorld(_world, _kingdoms);   // now render the resident world
""",
                r"""        _atlas3D.SetRevealAll(_debugReveal);
        UpdateAtlasInsets();   // provinces 7e (PR31): the panels' share of the screen, before the first framing
        _atlas3D.SetWorld(_world, _kingdoms);   // now render the resident world
""",
            ),
            (
                r"""        TickProvinceHover(delta);   // provinces 7c: the province readout
""",
                r"""        TickProvinceHover(delta);   // provinces 7c: the province readout
        TickTopBar();               // provinces 7e: the clock follows the bar; the insets follow the UI's size
""",
            ),
            (
                r"""        if (_campusOverlay != null)
            PlayerSession.CampusOverlayOpen = false;
    }
""",
                r"""        if (_campusOverlay != null)
            PlayerSession.CampusOverlayOpen = false;
        // Provinces 7e (PR34): give the global bar its centre back.
        var hud = HudManager.Instance;
        if (hud != null && IsInstanceValid(hud))
            hud.ReleaseCentre(this);
    }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.Dispatch.cs",
        "a76c5ae58ce4fb080a8ff55c173da462ac449130",
        "2887f9ef136c54365baf7a88a4852db017f95a8e",
        [
            (
                r"""    private Button _passOneDayBtn;
""",
                r"""    private Button _passOneDayBtn;
    private CanvasLayer _timeBarLayer;

    /// <summary>The clock's canvas layer (provinces 7e, PR34): just above the global bar
    /// (HudManager, 90), under the help layer (95) and the pause menu (100).</summary>
    private const int TimeBarLayer = 91;
""",
            ),
            (
                r"""        if (_hud == null || _turnMoonBtn == null || _timeBar != null)
        {
            return;
        }
        _timeBar = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetTop = HudManager.BarHeight + 8,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _timeBar.AddThemeStyleboxOverride("panel", CardStyle());
        _hud.AddChild(_timeBar);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", CardPadX + 2);
        margin.AddThemeConstantOverride("margin_right", CardPadX - 4);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _timeBar.AddChild(margin);
""",
                r"""        if (_hud == null || _turnMoonBtn == null)
        {
            return;
        }
        // Provinces 7e (PR34): the clock sits in the global bar's centre, on a canvas layer of
        // its own just above the bar's, so it is freed with this scene. The bar steps its
        // centre aside: its own date hides and its Next line drops below it (HudManager).
        // A second build (the HUD rebuilt) replaces the first.
        if (_timeBarLayer != null && IsInstanceValid(_timeBarLayer))
        {
            _timeBarLayer.QueueFree();
        }
        _timeBarLayer = new CanvasLayer { Name = "TimeBarLayer", Layer = TimeBarLayer };
        AddChild(_timeBarLayer);
        _timeBar = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetTop = 4,
            OffsetBottom = HudManager.BarHeight - 4,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _timeBar.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        _timeBarLayer.AddChild(_timeBar);
        HudManager.Instance?.ClaimCentre(this);

        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _timeBar.AddChild(margin);
""",
            ),
            (
                r"""    /// <summary>The date, the year, the next stop and the button's count.
""",
                r"""    // ── The free screen (provinces 7e, PR31) ─────────────────────────────

    /// <summary>The UI size the atlas last heard the panels' shares for.</summary>
    private Vector2 _atlasInsetsFor = Vector2.Zero;

    /// <summary>Tell the atlas how much of the screen the panels cover, so its whole-world
    /// view fits the world into the rest and its view centres there: the command column on
    /// the left, the global bar with the Next strip on top, the hint line at the foot. From
    /// BuildAtlas3D before the first framing, and again when the UI's size changes.</summary>
    private void UpdateAtlasInsets()
    {
        if (_atlas3D == null)
        {
            return;
        }
        Vector2 ui = GetViewport().GetVisibleRect().Size;
        if (ui.X < 1f || ui.Y < 1f)
        {
            return;
        }
        _atlasInsetsFor = ui;
        _atlas3D.SetViewInsets(
            (16f + CommandWidth + 16f) / ui.X,
            (HudManager.BarHeight + 8f + HudManager.NextStripHeight) / ui.Y,
            16f / ui.X,
            44f / ui.Y);
    }

    /// <summary>From _Process: the clock shows only while the global bar does (the campus
    /// overlay hides the bar), and the atlas hears of a new UI size.</summary>
    private void TickTopBar()
    {
        if (_timeBarLayer != null && IsInstanceValid(_timeBarLayer))
        {
            var hud = HudManager.Instance;
            _timeBarLayer.Visible = hud == null || !IsInstanceValid(hud) || hud.BarVisible;
        }
        if (_atlas3D != null && GetViewport().GetVisibleRect().Size != _atlasInsetsFor)
        {
            UpdateAtlasInsets();
        }
    }

    /// <summary>The date, the year, the next stop and the button's count.
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
        fail("usage: apply_provinces_map_7e.py <repo_root> [--dry-run]")
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
