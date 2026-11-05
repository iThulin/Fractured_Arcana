#!/usr/bin/env python3
# The desk, the frame, a sharp map, a lighter HUD (docs/provinces_v1_7f_desk_and_hud.md,
# section 7f of the provinces spec, 2026-11-04). Slice 7f-a: the sea's sky band off and its
# glints by zoom, the atlas rendered at the window's own pixels, the names' opaque halo and
# mipmapped font, a wooden desk and frame where 7e's paper was. Slice 7f-b: the command
# column becomes the corners (the roster's chips and the selection panel bottom left, the
# lenses bottom right), "?" and the Home city / World map toggle in the global bar, Muster on
# the roster, a readable hint. Slice 7f-c: the Register's book in the bar, and a region of its
# own on each screen that claims one (the strategic map, combat).
#
# Drops in four files kept beside this script (WorldAtlas3D.Frame.cs and WorldAtlas3D.Marks.cs
# replace 7e's; WorldAtlas3D.Clarity.cs and StrategicView.Corners.cs are new) and edits eight
# live files surgically (WorldAtlas3D.cs, WorldAtlas3D.Dispatch.cs, StrategicView.cs,
# StrategicView.Dispatch.cs, HudManager.cs, RegisterManager.cs, CombatUI.cs and
# Data/Register/barks.json). Rules: every starting blob checked (a new file must not exist
# yet), every anchor found exactly once, added text ASCII apart from box-drawing characters,
# no banned dash, brackets balanced per edit, every result equal to the blob it was validated
# at, nothing written unless every step succeeds, every written file verified, and a second
# run refuses.
#
# Usage: python3 apply_provinces_desk_7f.py <repo_root> [--dry-run]

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
    ("Scripts/Systems/Strategic/WorldAtlas3D.Frame.cs", "f2788790dea20a8e9992d32574c8c1b98644da5f", "53010d71e5b890b8d3f11fe0c762c50e77917f38"),
    ("Scripts/Systems/Strategic/WorldAtlas3D.Marks.cs", "d9a5af3b9f4fb94b56674e50f56750085844cf79", "0f862f40b9c1e7deec5b5e88b4eb21ca2c21adb4"),
    ("Scripts/Systems/Strategic/WorldAtlas3D.Clarity.cs", None, "3fbb7e9433b47ddc3ad8949297c77fc560033993"),
    ("Scripts/Systems/Strategic/StrategicView.Corners.cs", None, "96a26d416adbd39474c89a2237f14a51dc995fd5"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.cs",
        "64166fbe2616c4fe78749fe544f21474933fc1bf",
        "afebf38f874d3c5172ec3f7b715f0fd3088d2e09",
        [
            (
                r"""                // Provinces 7e (PR31): paper, as the sheet under the world, for whatever
                // lies past the far plane (WorldAtlas3D.Frame.cs).
                BackgroundColor = PaperVoid,
""",
                r"""                // Provinces 7f (PR39): the desk's darkest wood, for whatever lies past the
                // far plane (WorldAtlas3D.Frame.cs).
                BackgroundColor = DeskVoid,
""",
            ),
            (
                r"""        UpdateSettlementMarks();          // provinces 7e (PR32): the marks keep their width and fade close in
""",
                r"""        UpdateSettlementMarks();          // provinces 7e (PR32): the marks keep their width and fade close in
        UpdateSeaByZoom(zoom01);          // provinces 7f (PR36): the sea's glints only close in
""",
            ),
            (
                r"""            _camYaw -= mmOrbit.Relative.X * OrbitYawSens;
""",
                r"""            // Provinces 7f (PR37): the pointer moves in render pixels; the rates are per design pixel.
            _camYaw -= mmOrbit.Relative.X / RenderScale * OrbitYawSens;
""",
            ),
            (
                r"""                basePitch + mmOrbit.Relative.Y * OrbitPitchSens, PitchMinDeg, PitchMaxDeg);
""",
                r"""                basePitch + mmOrbit.Relative.Y / RenderScale * OrbitPitchSens, PitchMinDeg, PitchMaxDeg);
""",
            ),
            (
                r"""            if (mm.Relative.LengthSquared() > 1f)
                _dragMoved = true;
            float k = _camDist * 0.0012f;
""",
                r"""            if (mm.Relative.LengthSquared() > RenderScale * RenderScale)
                _dragMoved = true;
            // Provinces 7f (PR37): the pointer moves in render pixels, RenderScale of them to a
            // design pixel, so the pan divides by it to keep its pace under the cursor.
            float k = _camDist * 0.0012f / RenderScale;
""",
            ),
            (
                r"""            float worldPerPx = size / (vp.Y > 1f ? vp.Y : 1080f);
""",
                r"""            // Provinces 7f (PR37): vp is in render pixels and the shift in design pixels.
            float worldPerPx = size * RenderScale / (vp.Y > 1f ? vp.Y : 1080f);
""",
            ),
            (
                r"""        waterMesh.Material = PainterlyPrism.TileMaterial(PainterlyPrism.Water, 0.55f);
""",
                r"""        waterMesh.Material = PainterlyPrism.TileMaterial(PainterlyPrism.Water, 0.55f);
        AdoptSeaMaterial(waterMesh.Material);   // provinces 7f (PR36): no sky band; glints by zoom
""",
            ),
            (
                r"""        RebuildPaperSheet();   // provinces 7e (PR31): the paper past the map's edge
""",
                r"""        RebuildDeskAndFrame();   // provinces 7f (PR39): the desk and the frame, where 7e's paper was
""",
            ),
            (
                r"""            NoDepthTest = true,
            FontSize = fontSize,
            Modulate = color,
""",
                r"""            NoDepthTest = true,
            FontSize = fontSize,
            Font = MapLabelFont(),   // provinces 7f (PR38): the project's font with mipmaps (or null: the default)
            Modulate = color,
""",
            ),
            (
                r"""            return Jitter(Hex3DPalette.Underpaint(LensBaseColor(t)), col, row, 0.02f);
""",
                r"""            return Jitter(Hex3DPalette.Underpaint(LensBaseColor(t)), col, row, t.IsWater ? 0f : 0.02f);
""",
            ),
            (
                r"""        return Jitter(c, col, row, Hex3DPalette.JitterAmp(t));
""",
                r"""        // Provinces 7f (PR36): the sea reads as one surface, so no per-tile wobble on it.
        return Jitter(c, col, row, t.IsWater ? 0f : Hex3DPalette.JitterAmp(t));
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/WorldAtlas3D.Dispatch.cs",
        "195e745a8e718f7dd7aeb7337c99ea3cc9e451bf",
        "70761378781f420f806aa8e9881d56f3ff4b3cad",
        [
            (
                r"""        float vpH = GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
        if (vpH < 1f)
        {
            vpH = 1080f;
        }
""",
                r"""        float vpH = GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
        if (vpH < 1f)
        {
            vpH = 1080f;
        }
        // Provinces 7f (PR37): the screen counted in design pixels, as the radii and the floors
        // below are; the pointer and the projections arrive in render pixels.
        vpH /= RenderScale;
""",
            ),
            (
                r"""            Vector2 d = screenPos - at;
""",
                r"""            Vector2 d = (screenPos - at) / RenderScale;
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.cs",
        "dec72fcdbddcc1203730961dd068ee3130c16b96",
        "707a338c535437a032bb95f93e005157dd3dc3c9",
        [
            (
                r"""    // The "to the world map" button that lived in the verb stack is gone
    // (2026-09-24): navigation is _viewToggleBtn at the head of the FORCES
    // roster, and Escape. See OnViewTogglePressed.
    private Button _viewToggleBtn;
""",
                r"""    // The "to the world map" button that lived in the verb stack is gone
    // (2026-09-24). Since provinces 7f (PR41) the city/world toggle is the
    // global bar's (HudManager reads HomeToggleState), and Escape. See
    // OnViewTogglePressed.
""",
            ),
            (
                r"""    private VBoxContainer _forcesRows;
""",
                r"""    private HFlowContainer _forcesRows;   // provinces 7f (PR40): the roster's chips
""",
            ),
            (
                r"""    private Button _audienceBtn;       // 2026-09-24: open a table with the court whose city the party stands in
    private Button _musterBtn;         // 2026-09-24: the Forces screen, where everyone is
""",
                r"""    private Button _audienceBtn;       // 2026-09-24: open a table with the court whose city the party stands in
""",
            ),
            (
                r"""    private CanvasLayer _helpLayer;        // the "first steps" orientation card
    private Button _helpBtn;               // reopens the help card any time
""",
                r"""    private CanvasLayer _helpLayer;        // the "first steps" orientation card
""",
            ),
            (
                r"""        vp.AddChild(_atlas3D);            // _Ready builds the camera (no world yet)
""",
                r"""        vp.AddChild(_atlas3D);            // _Ready builds the camera (no world yet)
        // Provinces 7f (PR37): render at the window's own pixels (StrategicView.Corners).
        _atlasView = container;
        _atlasViewport = vp;
        SizeAtlasView();
""",
            ),
            (
                r"""        _forcesPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        _forcesPanel.AddThemeStyleboxOverride("panel", CardStyle());
        _commandColumn.AddChild(_forcesPanel);
        // Orders and Destinations follow it down the column (StrategicView.Dispatch).
        BuildDispatchRail();

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", CardPadX);
        margin.AddThemeConstantOverride("margin_right", CardPadX);
        margin.AddThemeConstantOverride("margin_top", CardPadY);
        margin.AddThemeConstantOverride("margin_bottom", CardPadY);
        _forcesPanel.AddChild(margin);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        margin.AddChild(col);

        // Navigation lives HERE, at the head of the roster, not in the verb
        // stack (2026-09-24). "Where am I looking" and "who do I command" are
        // the two things the player needs in every mode, and the city/world
        // toggle had been one of fourteen same-sized buttons on the right.
        _viewToggleBtn = new Button
        {
            Text = "\u25C8  To the world map",
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _viewToggleBtn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        UITheme.ApplyButtonStyle(_viewToggleBtn, isPrimary: true);
        _viewToggleBtn.TooltipText = "Escape does the same.";
        _viewToggleBtn.Pressed += OnViewTogglePressed;
        col.AddChild(_viewToggleBtn);

        _forcesRows = new VBoxContainer();
        _forcesRows.AddThemeConstantOverride("separation", 4);
        col.AddChild(_forcesRows);
""",
                r"""        // Provinces 7f (PR40): the roster is a row of chips in the bottom-left corner, over
        // the selection panel. No card behind it, and nothing in it but the chips takes the
        // pointer, so the map stays clickable between them.
        _forcesPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _forcesPanel.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        _commandColumn.AddChild(_forcesPanel);
        // Orders and Destinations join the corner after it (StrategicView.Dispatch).
        BuildDispatchRail();

        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _forcesPanel.AddChild(margin);

        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 6);
        margin.AddChild(col);

        // Navigation (2026-09-24) stood here, at the head of the roster. Since provinces 7f
        // (PR41) the city/world toggle is the global bar's (HomeToggleState).
        var chipRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        chipRow.AddThemeConstantOverride("separation", 8);
        col.AddChild(chipRow);

        _forcesRows = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _forcesRows.AddThemeConstantOverride("h_separation", 6);
        _forcesRows.AddThemeConstantOverride("v_separation", 6);
        chipRow.AddChild(_forcesRows);
        chipRow.AddChild(BuildMusterChip());   // the Forces screen, at the row's end (StrategicView.Corners)
""",
            ),
            (
                r"""        var title = new Label { Text = "FORCES" };
        title.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 4);
        title.AddThemeColorOverride("font_color", UITheme.TextDim);
        _forcesRows.AddChild(title);

""",
                r"""        // Provinces 7f (PR40): no heading; the chips say what they are.

""",
            ),
            (
                r"""    /// <summary>One selectable force. The accent bar down the left is the
    /// selection tell, matching the ring the piece wears on the map, so the
    /// panel and the board agree without the player having to check.</summary>
    private void AddForceRow(string name, string status, Color accent,
                             string pieceId, bool selected)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ToggleMode = false,
            ButtonPressed = false,
        };
        var style = UITheme.MakePanelStyle(
            selected ? UITheme.BgCard : UITheme.BgBase,
            selected ? accent : UITheme.NeutralDim);
        style.BorderWidthLeft = selected ? 4 : 1;
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeStyleboxOverride("hover", style);
        btn.AddThemeStyleboxOverride("pressed", style);
        btn.Pressed += () => FocusForce(pieceId);   // select, fly, show the reach
        _forcesRows.AddChild(btn);

        // The label rides INSIDE the button rather than being its Text, so the
        // name and the status line can carry different sizes and colours. A
        // Button's own Text is a single run and cannot.
        var box = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 12,
            OffsetTop = 3,
            OffsetRight = -6,
            OffsetBottom = -3,
        };
        box.AddThemeConstantOverride("separation", 0);
        btn.AddChild(box);

        var nameLbl = new Label { Text = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        nameLbl.AddThemeColorOverride("font_color", selected ? accent : UITheme.TextPrimary);
        box.AddChild(nameLbl);

        var statusLbl = new Label
        {
            Text = status,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        btn.TooltipText = status;
        statusLbl.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        statusLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(statusLbl);
    }
""",
                r"""    /// <summary>One selectable force, as a chip in the roster's row (provinces 7f, PR40): its
    /// name, coloured and edged in its accent while selected, matching the ring the piece
    /// wears on the map, so the row and the board agree. What it is doing is the chip's
    /// tooltip; the selection panel under the row says it in full.</summary>
    private void AddForceRow(string name, string status, Color accent,
                             string pieceId, bool selected)
    {
        var btn = new Button
        {
            Text = name.Trim(),
            TooltipText = status,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
        };
        var style = UITheme.MakePanelStyle(
            selected ? UITheme.BgCard : UITheme.BgBase,
            selected ? accent : UITheme.NeutralDim);
        style.SetBorderWidthAll(selected ? 2 : 1);
        style.ContentMarginLeft = 12;
        style.ContentMarginRight = 12;
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeStyleboxOverride("hover", style);
        btn.AddThemeStyleboxOverride("pressed", style);
        btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
        Color ink = selected ? accent : UITheme.TextPrimary;
        btn.AddThemeColorOverride("font_color", ink);
        btn.AddThemeColorOverride("font_hover_color", ink);
        btn.AddThemeColorOverride("font_pressed_color", ink);
        btn.Pressed += () => FocusForce(pieceId);   // select, fly, show the reach
        _forcesRows.AddChild(btn);
    }
""",
            ),
            (
                r"""        if (_viewToggleBtn != null)
        {
            bool home = _atlas3D?.ActiveCityIsHome ?? true;
            _viewToggleBtn.Text = cityNow
                ? "\u25C8  To the world map"
                : "\u25C8  To the home city";
            bool doorShut = Interval.DoorShut(SaveManager.ActiveSave?.Cycle);   // Standing Orders O1
            bool shutToPlayer = doorShut && !PlayerSession.DebugMode;          // Debug Mode reaches the panels
            _viewToggleBtn.Disabled = !cityNow && (_atlas3D == null || !_atlas3D.HasCityGrounds || shutToPlayer);
            _viewToggleBtn.TooltipText = cityNow
                ? (home ? "Up to the world. Escape does the same." : "Leave this city. Escape does the same.")
                : (shutToPlayer ? Interval.DoorLine
                   : doorShut ? "Debug Mode: the campus's panels, over the map. The home stays shut."
                   : "Down into the guild's own city.");
        }
""",
                r"""        // Provinces 7f (PR41): the city/world toggle is the global bar's now. It reads
        // HomeToggleState (StrategicView.Corners) and hears of a change here.
        HudManager.Instance?.RefreshViewToggle();
""",
            ),
            (
                r"""            ? "Pick a force in FORCES to give it orders""",
                r"""            ? "Pick a force at the lower left to give it orders""",
            ),
            (
                r"""                 "city, or press \u25C8 To the home city at the head of FORCES. Escape goes back up.");""",
                r"""                 "city, or press Home city in the top bar. Escape goes back up.");""",
            ),
            (
                r"""        HelpLine("•  FORCES lists the castle and your parties. Click one to give it orders.");
""",
                r"""        HelpLine("•  The row at the lower left lists the castle and your parties. Click one, or its " +
                 "piece on the map, to give it orders. Muster opens the Forces screen.");
""",
            ),
            (
                r"""        // The muster board is never gated: "where is everyone" is a question
        // with an answer in every mode, city view included.
        _musterBtn = MakeCityChromeButton("\u2637  Forces", primary: false);
        _musterBtn.Pressed += OpenForcesScreen;

        _helpBtn = MakeCityChromeButton("?  How this works", primary: false);
        _helpBtn.Pressed += ShowHelpCard;   // visible in every mode; orientation is never gated

        // Stack order (2026-09-24): the selected piece's verbs first, then the
        // city's own three, then the fixed footer. Moved rather than rebuilt
        // in place, so each button's construction stays beside its handler.
        foreach (var tail in new Button[] { _annexButton, _buildModeBtn, _cityServicesBtn, _musterBtn, _helpBtn })
""",
                r"""        // Provinces 7f (PR41): the muster board opens from the roster's Muster chip
        // (BuildMusterChip), still in every mode, city view included. "How this works"
        // is a screen button in the global bar; orientation is never gated.
        HudManager.Instance?.SetScreenButton(this, "help", "?", "How this works", ShowHelpCard);
        // Provinces 7f (PR42): the Register speaks top left, where this screen keeps nothing else.
        ClaimRegisterRegion();

        // Stack order (2026-09-24): the selected piece's verbs first, then the
        // city's own three. Moved rather than rebuilt in place, so each button's
        // construction stays beside its handler.
        foreach (var tail in new Button[] { _annexButton, _buildModeBtn, _cityServicesBtn })
""",
            ),
            (
                r"""        _hintLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        _hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.5f));
""",
                r"""        _hintLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        // Provinces 7f (PR43): brighter, with a dark outline, so it reads over the desk and the map.
        _hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.75f));
        _hintLabel.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.035f, 0.025f, 1f));
        _hintLabel.AddThemeConstantOverride("outline_size", 4);
""",
            ),
            (
                r"""        _commandColumn.AddChild(lensPanel);
""",
                r"""        // Provinces 7f (PR40): the lenses' card takes the bottom-right corner
        // (StrategicView.Corners), the four lenses sharing its width.
        _lensCorner.AddChild(lensPanel);
""",
            ),
            (
                r"""        // Provinces 7e (PR34): give the global bar its centre back.
        var hud = HudManager.Instance;
        if (hud != null && IsInstanceValid(hud))
            hud.ReleaseCentre(this);
""",
                r"""        // Provinces 7e (PR34): give the global bar its centre back. 7f (PR41, PR42): and its
        // screen buttons, and the Register its region.
        var hud = HudManager.Instance;
        if (hud != null && IsInstanceValid(hud))
        {
            hud.ReleaseCentre(this);
            hud.ReleaseScreenButtons(this);
        }
        RegisterManager.ReleaseRegion(this);
""",
            ),
            (
                r"""        PlayerSession.CampusOverlayOpen = true;
        CampusScreen.OverlayLeaveHandler = HideCampusOverlay;
""",
                r"""        PlayerSession.CampusOverlayOpen = true;
        RegisterManager.ReleaseRegion(this);   // provinces 7f (PR42): the campus keeps its own layout
        CampusScreen.OverlayLeaveHandler = HideCampusOverlay;
""",
            ),
            (
                r"""        CampusScreen.OverlayLeaveHandler = null;
        PlayerSession.CampusOverlayOpen = false;
""",
                r"""        CampusScreen.OverlayLeaveHandler = null;
        PlayerSession.CampusOverlayOpen = false;
        ClaimRegisterRegion();   // provinces 7f (PR42): back on the map, the Register's region again
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.Dispatch.cs",
        "2887f9ef136c54365baf7a88a4852db017f95a8e",
        "f2bbcdbaa1eb6a777c0ccfc19e3932961e1a1c8e",
        [
            (
                r"""    /// <summary>Width of the left column, cards included.</summary>
    private const int CommandWidth = 320;
""",
                r"""    //    Provinces 7f (PR40): the column became the corners. _commandColumn is
    //    now the bottom-left stack, growing up: Destinations (folded until asked
    //    for), the roster's chips with Muster, then Orders as the selection
    //    panel. The lenses take the bottom-right (StrategicView.Corners).

    /// <summary>The old column's width, cards included; the orders card's notes still wrap
    /// to it.</summary>
    private const int CommandWidth = 320;
""",
            ),
            (
                r"""    private VBoxContainer _ordersBox;
""",
                r"""    private GridContainer _ordersBox;   // provinces 7f (PR40): the verbs in two columns
""",
            ),
            (
                r"""    private bool _dispatchCollapsed;
""",
                r"""    private bool _dispatchCollapsed = true;   // provinces 7f (PR40): folded until asked for
""",
            ),
            (
                r"""    /// <summary>The column itself: a transparent stack, so the map between and
    /// below the cards still takes clicks. Built on first use by whichever card
    /// asks first (the lens row, in practice).</summary>
    private void EnsureCommandColumn()
    {
        if (_commandColumn != null || _hud == null)
        {
            return;
        }
        _commandColumn = new VBoxContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 16,
            OffsetRight = 16 + CommandWidth,
            OffsetTop = 8 + HudManager.BarHeight,
            OffsetBottom = -16,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _commandColumn.AddThemeConstantOverride("separation", CardGap);
        _hud.AddChild(_commandColumn);
    }
""",
                r"""    /// <summary>The corners: transparent stacks, so the map between and around the cards
    /// still takes clicks. Built on first use by whichever card asks first (the lens row, in
    /// practice). Since provinces 7f (PR40) _commandColumn is the bottom-left stack and the
    /// lenses have a corner of their own (StrategicView.Corners, BuildCorners).</summary>
    private void EnsureCommandColumn()
    {
        if (_commandColumn != null || _hud == null)
        {
            return;
        }
        BuildCorners();
    }
""",
            ),
            (
                r"""        _ordersBox = new VBoxContainer();
        _ordersBox.AddThemeConstantOverride("separation", 6);
""",
                r"""        _ordersBox = new GridContainer { Columns = 2 };   // provinces 7f (PR40)
        _ordersBox.AddThemeConstantOverride("h_separation", 6);
        _ordersBox.AddThemeConstantOverride("v_separation", 6);
""",
            ),
            (
                r"""        _commandColumn.AddChild(_dispatchPanel);
""",
                r"""        _commandColumn.AddChild(_dispatchPanel);
        // Provinces 7f (PR40): first in the bottom-left stack, so it opens upward and the
        // chips and the orders under it never move.
        _commandColumn.MoveChild(_dispatchPanel, 0);
""",
            ),
            (
                r"""            b.GetParent()?.RemoveChild(b);
            b.CustomMinimumSize = new Vector2(0, 36);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
""",
                r"""            b.GetParent()?.RemoveChild(b);
            b.CustomMinimumSize = new Vector2(0, 36);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            // Provinces 7f (PR40): two to a row, so a long name ends in an ellipsis rather
            // than widening the corner.
            b.ClipText = true;
            b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
""",
            ),
            (
                r"""    /// <summary>Tell the atlas how much of the screen the panels cover, so its whole-world
    /// view fits the world into the rest and its view centres there: the command column on
    /// the left, the global bar with the Next strip on top, the hint line at the foot. From
    /// BuildAtlas3D before the first framing, and again when the UI's size changes.</summary>
""",
                r"""    /// <summary>Tell the atlas how much of the screen the panels cover, so its whole-world
    /// view fits the world into the rest and its view centres there: the global bar with the
    /// Next strip on top, the hint line at the foot, a margin at the sides. Since provinces 7f
    /// (PR40) no column: the corner blocks sit over the framed world's corners, which are sea.
    /// From BuildAtlas3D before the first framing, and again when the UI's size changes.</summary>
""",
            ),
            (
                r"""            (16f + CommandWidth + 16f) / ui.X,
""",
                r"""            16f / ui.X,
""",
            ),
            (
                r"""        if (_atlas3D != null && GetViewport().GetVisibleRect().Size != _atlasInsetsFor)
        {
            UpdateAtlasInsets();
        }
    }
""",
                r"""        if (_atlas3D != null && GetViewport().GetVisibleRect().Size != _atlasInsetsFor)
        {
            UpdateAtlasInsets();
        }
        TickAtlasView();   // provinces 7f (PR37): the atlas follows the window's pixels
    }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/HudManager.cs",
        "91d92ff7f3c6e5772545bdab98fcc0ea8baaccdd",
        "c03034f5a66a5845323a98867ea90d5af65a940c",
        [
            (
                r"""//                 (O1b, StandingOrders and Interval).
// Layer:          System (autoload)
""",
                r"""//                 (O1b, StandingOrders and Interval).
//                 Provinces 7f: a slot at the bar's left end where the
//                 Register places its book (TryGetRegisterSlot); buttons a
//                 screen puts before Menu (SetScreenButton, released with
//                 the screen); and on the strategic map Return to Campus
//                 is that screen's view toggle (Home city, World map), read
//                 from StrategicView.HomeToggleState.
// Layer:          System (autoload)
""",
            ),
            (
                r"""    public const int NextStripHeight = 28;
""",
                r"""    public const int NextStripHeight = 28;

    // Provinces 7f (PR42): the Register's book stands in this slot at the bar's left end. The
    // glyph stays the Register's own node on its own layer; the bar only keeps the room.
    private Control _registerSlot;

    /// <summary>The Register's slot's width: its book glyph's.</summary>
    public const int RegisterSlotWidth = 44;

    // Provinces 7f (PR41): buttons a screen puts in the bar's right cluster, before Menu, by
    // key. Each goes when its screen releases it or leaves the tree.
    private sealed class ScreenButton
    {
        public Node Owner;
        public string Text;
        public string Tooltip;
        public System.Action OnPressed;
        public Button Shown;
    }

    private readonly System.Collections.Generic.Dictionary<string, ScreenButton> _screenButtons = new();
    private HBoxContainer _screenSlot;
""",
            ),
            (
                r"""        // ── Left: resource readouts (extend here) ────────────────────────
        // Fixed order [Lunation, Gold, Splinters, Materials] (2026-08-05 ask).
        _lunationLabel = MakeLabel(row, UITheme.TextSecondary);
""",
                r"""        // ── Left: resource readouts (extend here) ────────────────────────
        // Fixed order [Lunation, Gold, Splinters, Materials] (2026-08-05 ask).
        // Provinces 7f (PR42): first, the Register's slot (RegisterManager places its book here).
        _registerSlot = new Control
        {
            CustomMinimumSize = new Vector2(RegisterSlotWidth, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(_registerSlot);
        _lunationLabel = MakeLabel(row, UITheme.TextSecondary);
""",
            ),
            (
                r"""        AddNavButton(row, "Quests", () => QuestLogScreen.Toggle(GetTree().Root));
        AddNavButton(row, "Menu", () => PauseManager.Instance?.OpenPauseMenu());

        BuildNextStrip();   // provinces 7e (PR35)
        ApplyCentre();      // a screen may have claimed the centre before the bar was built
""",
                r"""        AddNavButton(row, "Quests", () => QuestLogScreen.Toggle(GetTree().Root));
        // Provinces 7f (PR41): a screen's own buttons sit here, before Menu (SetScreenButton).
        _screenSlot = new HBoxContainer { Visible = false };
        _screenSlot.AddThemeConstantOverride("separation", 18);
        row.AddChild(_screenSlot);
        AddNavButton(row, "Menu", () => PauseManager.Instance?.OpenPauseMenu());

        BuildNextStrip();   // provinces 7e (PR35)
        ApplyCentre();      // a screen may have claimed the centre before the bar was built
        ApplyScreenButtons();   // provinces 7f (PR41): and put its buttons in
""",
            ),
            (
                r"""    private void ReturnToCampus()
    {
        if (GetTree().CurrentScene is StrategicView sv && sv.TryDescendToCampus())
            return;
""",
                r"""    private void ReturnToCampus()
    {
        // Provinces 7f (PR41): on the strategic map the button is that screen's view toggle.
        if (GetTree().CurrentScene is StrategicView sv)
        {
            sv.ToggleHomeView();
            return;
        }
""",
            ),
            (
                r"""        if (_nextRefreshIn <= 0)
        {
            _nextRefreshIn = NextRefreshSeconds;
            RefreshNextLine();
        }
""",
                r"""        if (_nextRefreshIn <= 0)
        {
            _nextRefreshIn = NextRefreshSeconds;
            RefreshNextLine();
            RefreshViewToggle();   // provinces 7f (PR41): the door can shut while the map is up
        }
""",
            ),
            (
                r"""            _returnButton.Visible = ShouldShowReturnToCampus();
        }
""",
                r"""            _returnButton.Visible = ShouldShowReturnToCampus();
        }
        RefreshViewToggle();          // provinces 7f (PR41)
        ReleaseLostScreenButtons();   // provinces 7f (PR41): a screen that left without releasing them
""",
            ),
            (
                r"""        // City view IS home (2026-08-19 hub): the warp is moot there, and the bar now
        // stays visible in city view, so gate the button on the atlas state directly.
        if (current is StrategicView sv && sv.InCityView)
        {
            return false;
        }
""",
                r"""        // City view IS home (2026-08-19 hub). Since provinces 7f (PR41) the button is the
        // strategic map's view toggle (Home city on the world, World map in a city), shown in
        // both views.
        if (current is StrategicView)
        {
            return true;
        }
""",
            ),
            (
                r"""    /// <summary>Update the resource labels when their values change. Cheap
""",
                r"""    // ── The Register's slot (provinces 7f, PR42) ─────────────────────────

    /// <summary>Where the Register's book stands while the bar shows: the slot's rectangle on
    /// screen. False while the bar is hidden (the prologue, the campus overlay) or not yet laid
    /// out, and the Register keeps its book on the left edge then.</summary>
    public bool TryGetRegisterSlot(out Rect2 rect)
    {
        rect = new Rect2();
        if (!BarVisible || !IsInstanceValid(_registerSlot) || !_registerSlot.IsVisibleInTree())
        {
            return false;
        }
        rect = _registerSlot.GetGlobalRect();
        return rect.Size.X >= 1f && rect.Size.Y >= 1f;
    }

    // ── Screen buttons and the view toggle (provinces 7f, PR41) ──────────

    /// <summary>A screen puts a button in the bar's right cluster, before Menu, under a key; the
    /// same key again replaces it. It goes when the screen calls ReleaseScreenButtons or leaves
    /// the tree. A screen built before the bar has its buttons placed once the bar exists.</summary>
    public void SetScreenButton(Node owner, string key, string text, string tooltip, System.Action onPressed)
    {
        if (owner == null || string.IsNullOrEmpty(key) || onPressed == null)
        {
            return;
        }
        if (_screenButtons.TryGetValue(key, out var old) && IsInstanceValid(old.Shown))
        {
            old.Shown.QueueFree();
        }
        _screenButtons[key] = new ScreenButton { Owner = owner, Text = text, Tooltip = tooltip, OnPressed = onPressed };
        ApplyScreenButtons();
    }

    /// <summary>Take away every button <paramref name="owner"/> put in the bar.</summary>
    public void ReleaseScreenButtons(Node owner)
    {
        if (owner == null)
        {
            return;
        }
        var gone = new System.Collections.Generic.List<string>();
        foreach (var kv in _screenButtons)
        {
            if (kv.Value.Owner == owner)
            {
                gone.Add(kv.Key);
            }
        }
        foreach (string key in gone)
        {
            var b = _screenButtons[key];
            if (IsInstanceValid(b.Shown))
            {
                b.Shown.QueueFree();
            }
            _screenButtons.Remove(key);
        }
        SyncScreenSlot();
    }

    /// <summary>Buttons whose screen left the tree without releasing them go now.</summary>
    private void ReleaseLostScreenButtons()
    {
        var lost = new System.Collections.Generic.List<Node>();
        foreach (var b in _screenButtons.Values)
        {
            if (!IsInstanceValid(b.Owner) || !b.Owner.IsInsideTree())
            {
                lost.Add(b.Owner);
            }
        }
        foreach (var owner in lost)
        {
            ReleaseScreenButtons(owner);
        }
    }

    /// <summary>Put every screen button that is not on the bar yet on it. Waits for BuildBar,
    /// which calls it again once the bar exists.</summary>
    private void ApplyScreenButtons()
    {
        if (!IsInstanceValid(_screenSlot))
        {
            return;
        }
        foreach (var b in _screenButtons.Values)
        {
            if (IsInstanceValid(b.Shown))
            {
                continue;
            }
            bool glyph = (b.Text ?? "").Length <= 2;
            var btn = new Button
            {
                Text = b.Text ?? "",
                TooltipText = b.Tooltip ?? "",
                CustomMinimumSize = new Vector2(glyph ? 44 : 96, 0),
            };
            btn.AddThemeFontSizeOverride("font_size", glyph ? UITheme.CampusBodyFontSize : UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: false);
            var press = b.OnPressed;
            btn.Pressed += () => press();
            _screenSlot.AddChild(btn);
            b.Shown = btn;
        }
        SyncScreenSlot();
    }

    private void SyncScreenSlot()
    {
        if (IsInstanceValid(_screenSlot))
        {
            _screenSlot.Visible = _screenButtons.Count > 0;
        }
    }

    /// <summary>Return to Campus, read for the screen: on the strategic map it is that screen's
    /// view toggle (Home city on the world, World map in a city), elsewhere the warp home. From
    /// RefreshVisibility, the bar's half-second tick, and the strategic map's chrome refresh.</summary>
    public void RefreshViewToggle()
    {
        if (!IsInstanceValid(_returnButton))
        {
            return;
        }
        string text = "Return to Campus", tip = "";
        bool disabled = false;
        if (GetTree()?.CurrentScene is StrategicView sv && IsInstanceValid(sv))
        {
            sv.HomeToggleState(out text, out tip, out disabled);
        }
        if (_returnButton.Text != text)
        {
            _returnButton.Text = text;
        }
        _returnButton.TooltipText = tip;
        _returnButton.Disabled = disabled;
    }

    /// <summary>Update the resource labels when their values change. Cheap
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Register/RegisterManager.cs",
        "3216561b84a85a1038f989554aba4b750a6565a7",
        "f03867dea07c97b305995834dcdcdef5d272f762",
        [
            (
                r"""//                 Fire() is safe from anywhere: with no instance
//                 (headless asserts, tools) it does nothing.
// Layer:          System (autoload)
""",
                r"""//                 Fire() is safe from anywhere: with no instance
//                 (headless asserts, tools) it does nothing.
//
//                 Provinces 7f (PR42): the book stands in the global
//                 bar's slot while the bar shows, else on the left edge.
//                 A screen may claim a region (ClaimRegion); the slip and
//                 the margin notes then open only inside it. Nothing new
//                 is built: the same nodes are only placed elsewhere.
// Layer:          System (autoload)
""",
            ),
            (
                r"""    private const float DockTopFraction = 0.36f;   // of the viewport height
""",
                r"""    private const float DockTopFraction = 0.36f;   // of the viewport height

    // Provinces 7f (PR42): the region a screen claimed (ClaimRegion), in the UI's pixels.
    private Node _regionOwner;
    private Rect2 _region;
    private bool _regionGrowsUp;
""",
            ),
            (
                r"""    public static bool HasSeen(string key) => Instance != null && Instance.IsSeen(key);
""",
                r"""    public static bool HasSeen(string key) => Instance != null && Instance.IsSeen(key);

    /// <summary>A screen gives the Register a region of its own (provinces 7f, PR42), in the UI's
    /// pixels: the slip and the margin notes open only inside it, from its top (or, with
    /// <paramref name="growUp"/>, from its foot). The newest claim wins. It ends with
    /// ReleaseRegion, or when the owner leaves the tree.</summary>
    public static void ClaimRegion(Node owner, Rect2 region, bool growUp)
    {
        if (Instance == null || owner == null)
            return;
        Instance._regionOwner = owner;
        Instance._region = region;
        Instance._regionGrowsUp = growUp;
    }

    /// <summary>Give the region back. Ignored unless <paramref name="owner"/> holds it.</summary>
    public static void ReleaseRegion(Node owner)
    {
        if (Instance == null || owner == null || Instance._regionOwner != owner)
            return;
        Instance._regionOwner = null;
    }
""",
            ),
            (
                r"""        if (hidden)
            return;

        // A timed remark runs out: fade, then hide (FinishFade).
""",
                r"""        if (hidden)
            return;
        PlaceDock();   // provinces 7f (PR42): the book in the bar, the slip in its region

        // A timed remark runs out: fade, then hide (FinishFade).
""",
            ),
            (
                r"""    private void PlaceAll()
    {
        float top = Mathf.Round(ViewportSize.Y * DockTopFraction);
        _book.Position = new Vector2(DockLeft, top);
        var beside = new Vector2(DockLeft + RegisterBookGlyph.GlyphSize + 10f, top);
        _slip.Position = beside;
        _history.Position = beside;
        _historyText.CustomMinimumSize = new Vector2(SlipWidth + 12f, Mathf.Min(520f, ViewportSize.Y * 0.5f));
    }
""",
                r"""    private void PlaceAll()
    {
        _historyText.CustomMinimumSize = new Vector2(SlipWidth + 12f, Mathf.Min(520f, ViewportSize.Y * 0.5f));
        PlaceDock();
    }

    /// <summary>Provinces 7f (PR42), every frame while shown: the book in the global bar's slot
    /// while the bar shows, else on the left edge a third down (as before); the slip and the
    /// margin notes in the current screen's region, else on the left edge at that height
    /// (beside the book when the book is there). Their sizes follow their text, so this follows
    /// them rather than placing once.</summary>
    private void PlaceDock()
    {
        Vector2 vp = ViewportSize;
        float glyph = RegisterBookGlyph.GlyphSize;
        float dockTop = Mathf.Round(vp.Y * DockTopFraction);
        Vector2 book;
        bool inBar = false;
        var hud = HudManager.Instance;
        if (hud != null && IsInstanceValid(hud) && hud.TryGetRegisterSlot(out Rect2 slot))
        {
            book = (slot.Position + (slot.Size - new Vector2(glyph, glyph)) * 0.5f).Round();
            inBar = true;
        }
        else
            book = new Vector2(DockLeft, dockTop);
        if (_book.Position != book)
            _book.Position = book;

        Rect2 area;
        bool growUp;
        if (RegionLive())
        {
            area = _region;
            growUp = _regionGrowsUp;
        }
        else
        {
            float x = inBar ? DockLeft : DockLeft + glyph + 10f;
            area = new Rect2(x, dockTop, SlipWidth + 40f, Mathf.Max(0f, vp.Y - dockTop - 16f));
            growUp = false;
        }
        PlaceIn(_slip, area, growUp, vp);
        PlaceIn(_history, area, growUp, vp);
    }

    /// <summary>The newest claim, while its owner is still in the tree.</summary>
    private bool RegionLive()
    {
        if (_regionOwner == null)
            return false;
        if (!IsInstanceValid(_regionOwner) || !_regionOwner.IsInsideTree())
        {
            _regionOwner = null;
            return false;
        }
        return true;
    }

    /// <summary>One shown panel at the region's top, or at its foot when it grows up, kept on
    /// screen.</summary>
    private static void PlaceIn(Control panel, Rect2 area, bool growUp, Vector2 vp)
    {
        if (panel == null || !panel.Visible)
            return;
        float h = panel.Size.Y;
        float y = growUp ? area.End.Y - h : area.Position.Y;
        y = Mathf.Clamp(y, 8f, Mathf.Max(8f, vp.Y - h - 8f));
        var at = new Vector2(Mathf.Round(area.Position.X), Mathf.Round(y));
        if (panel.Position != at)
            panel.Position = at;
    }
""",
            ),
        ],
    ),
    (
        "Scripts/UI/CombatUI.cs",
        "8c0fc94bbd1163e6fdf4dfb0d99ceef321f8ee64",
        "b15d420ccade93aa09ed7da20bde4e436042c149",
        [
            (
                r"""	private void BuildBottomLeft()
	{
		var block = new VBoxContainer
""",
                r"""	// Provinces 7f (PR42): the bottom-left block (the log ticker) is the Register's floor in a
	// fight: its slips and margin notes open above the block and grow up, clear of the left
	// panel and the hand.
	private Control _registerFloor;

	/// <summary>Tell the Register where it may speak: from under the top inset down to just
	/// above the bottom-left block, from the block's left edge. Again whenever the block
	/// moves or grows. The claim ends with this UI: the Register drops an owner that has
	/// left the tree.</summary>
	private void ClaimRegisterRegion()
	{
		if (_registerFloor == null || !IsInstanceValid(_registerFloor))
			return;
		Rect2 block = _registerFloor.GetGlobalRect();
		float top = TopInset + 8f;
		float bottom = block.Position.Y - 8f;
		RegisterManager.ClaimRegion(this,
			new Rect2(block.Position.X, top, 420f, Mathf.Max(0f, bottom - top)), growUp: true);
	}

	private void BuildBottomLeft()
	{
		var block = new VBoxContainer
""",
            ),
            (
                r"""		block.AddThemeConstantOverride("separation", 6);
		AddChild(block);

		// (The party chips moved to the top-center strip on 2026-10-26; see BuildPartyStrip.)
""",
                r"""		block.AddThemeConstantOverride("separation", 6);
		AddChild(block);
		// Provinces 7f (PR42): the Register speaks above this block (ClaimRegisterRegion).
		_registerFloor = block;
		block.ItemRectChanged += ClaimRegisterRegion;

		// (The party chips moved to the top-center strip on 2026-10-26; see BuildPartyStrip.)
""",
            ),
        ],
    ),
    (
        "Data/Register/barks.json",
        "acc386f028793f730396713d073a35223890a93d",
        "f7f776754b9f19ad1bb3660b784fb355eb7c5996",
        [
            (
                r"""pick one in FORCES to give it orders.""",
                r"""pick one on the map, or in the row at the lower left, to give it orders.""",
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
