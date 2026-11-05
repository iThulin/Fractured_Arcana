using Godot;

// ============================================================
// StrategicView.Corners.cs: the HUD in the corners, the atlas at full pixels (2026-11-04)
//
// Purpose:        Section 7f of the provinces spec
//                 (docs/provinces_v1_7f_desk_and_hud.md).
//                 PR40: the command column became the corners. The force's
//                 cards stand in the bottom-left corner and grow up: the
//                 destinations (folded until asked for), the roster as a
//                 row of chips with Muster at its end, then the orders of
//                 the force picked. The lenses take the bottom-right. The
//                 top-left is the Register's region, and nothing else goes
//                 there.
//                 PR41: "?" lives in the global bar as a screen button, the
//                 Forces screen opens from the roster's Muster chip, and the
//                 bar's Return to Campus is this screen's view toggle (Home
//                 city on the world, World map in a city), read from
//                 HomeToggleState.
//                 PR37: the atlas renders at the window's own pixels. Its
//                 SubViewportContainer is sized in window pixels and scaled
//                 down by the same factor, so the 3D draws one pixel to one
//                 pixel instead of at the 1920 by 1080 design size stretched.
// Layer:          UI (StrategicView partial)
// Collaborators:  StrategicView.Dispatch.cs (EnsureCommandColumn calls
//                 BuildCorners; TickTopBar calls TickAtlasView),
//                 StrategicView.cs (BuildAtlas3D calls SizeAtlasView;
//                 BuildForcesPanel takes the Muster chip; BuildHud claims
//                 the Register's region and the help button), HudManager
//                 (the screen button, the view toggle), RegisterManager
//                 (the region), WorldAtlas3D (RenderScale)
// See:            docs/provinces_v1_7f_desk_and_hud.md
// ============================================================

public partial class StrategicView
{
    // ── The corners (PR40) ───────────────────────────────────────────────

    /// <summary>The bottom-left stack's width: the selection panel and the roster's row.</summary>
    private const int CornerWidth = 440;

    /// <summary>The lens card's width in the bottom-right corner.</summary>
    private const int LensCornerWidth = 392;

    /// <summary>The Register's region on this screen: top left, under the bar. The history
    /// panel (up to 520 tall) opens in it as well as the slips.</summary>
    private const float RegisterRegionWidth = 440f;
    private const float RegisterRegionHeight = 520f;

    /// <summary>The lens card's corner: anchored bottom right, growing up and left.</summary>
    private VBoxContainer _lensCorner;

    /// <summary>From EnsureCommandColumn: the two corner stacks. _commandColumn keeps its name
    /// and is now the bottom-left stack, so every card that joined the column joins it.</summary>
    private void BuildCorners()
    {
        _commandColumn = new VBoxContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 1f,
            AnchorRight = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 16,
            OffsetRight = 16 + CornerWidth,
            OffsetTop = -16,
            OffsetBottom = -16,
            GrowHorizontal = Control.GrowDirection.End,
            GrowVertical = Control.GrowDirection.Begin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _commandColumn.AddThemeConstantOverride("separation", CardGap);
        _hud.AddChild(_commandColumn);

        _lensCorner = new VBoxContainer
        {
            AnchorLeft = 1f,
            AnchorTop = 1f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = -16 - LensCornerWidth,
            OffsetRight = -16,
            OffsetTop = -16,
            OffsetBottom = -16,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Begin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _lensCorner.AddThemeConstantOverride("separation", CardGap);
        _hud.AddChild(_lensCorner);
    }

    /// <summary>The roster row's last chip: the Forces screen, where every force is listed.
    /// Outside the chips' own flow, so it stays when the chips hide in city view: "where is
    /// everyone" has an answer in every mode.</summary>
    private Button BuildMusterChip()
    {
        var btn = new Button
        {
            Text = "\u2637  Muster",
            TooltipText = "The Forces screen: every force, and where it is.",
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.Pressed += OpenForcesScreen;
        return btn;
    }

    // ── The bar's view toggle (PR41) ─────────────────────────────────────

    /// <summary>The global bar's view toggle, pressed: down into the home city from the
    /// world, up to the world from a city. The same path Escape and the old column button
    /// took, door rules included.</summary>
    public void ToggleHomeView() => OnViewTogglePressed();

    /// <summary>What the bar's view toggle reads and whether it can be pressed: the rules the
    /// column's "To the home city" button followed.</summary>
    public void HomeToggleState(out string text, out string tooltip, out bool disabled)
    {
        bool cityNow = _atlas3D?.CityMode ?? false;
        bool home = _atlas3D?.ActiveCityIsHome ?? true;
        bool doorShut = Interval.DoorShut(SaveManager.ActiveSave?.Cycle);   // Standing Orders O1
        bool shutToPlayer = doorShut && !PlayerSession.DebugMode;          // Debug Mode reaches the panels
        text = cityNow ? "World map" : "Home city";
        disabled = !cityNow && (_atlas3D == null || !_atlas3D.HasCityGrounds || shutToPlayer);
        tooltip = cityNow
            ? (home ? "Up to the world. Escape does the same." : "Leave this city. Escape does the same.")
            : (shutToPlayer ? Interval.DoorLine
               : doorShut ? "Debug Mode: the campus's panels, over the map. The home stays shut."
               : "Down into the guild's own city.");
    }

    // ── The Register's region (PR42) ─────────────────────────────────────

    /// <summary>From BuildHud: the Register's slips and margin notes open top left, under the
    /// bar, where this screen keeps nothing else. Released in _ExitTree (and by the Register
    /// itself once this view leaves the tree).</summary>
    private void ClaimRegisterRegion()
    {
        RegisterManager.ClaimRegion(this,
            new Rect2(16f, HudManager.BarHeight + 16f, RegisterRegionWidth, RegisterRegionHeight),
            growUp: false);
    }

    // ── The atlas at the window's own pixels (PR37) ──────────────────────

    /// <summary>The fallback in one place: false renders the atlas at the design size,
    /// stretched, as before 7f.</summary>
    private static readonly bool AtlasNativeResolution = true;

    private SubViewportContainer _atlasView;
    private SubViewport _atlasViewport;
    private Vector2I _atlasViewWindow;
    private Vector2 _atlasViewUi;

    /// <summary>Size the atlas's container in window pixels and scale it back down to the
    /// design size: with Stretch on, the SubViewport takes the container's own size, so the
    /// 3D renders at the window's pixels. Pointer events reach the SubViewport through the
    /// container's transform, so they arrive in those pixels too; the atlas hears the factor
    /// as its RenderScale. Below the design size the factor holds at 1 (as before). MSAA
    /// drops to 2x from a factor of 1.5, to give back part of the cost.</summary>
    private void SizeAtlasView()
    {
        if (_atlasView == null || !IsInstanceValid(_atlasView))
            return;
        Vector2 ui = GetViewport().GetVisibleRect().Size;
        Vector2I window = GetTree().Root.Size;
        _atlasViewUi = ui;
        _atlasViewWindow = window;
        if (ui.X < 1f || ui.Y < 1f)
            return;
        float scale = 1f;
        if (AtlasNativeResolution && window.Y > 0)
            scale = Mathf.Clamp(window.Y / ui.Y, 1f, 4f);
        _atlasView.AnchorLeft = 0f;
        _atlasView.AnchorTop = 0f;
        _atlasView.AnchorRight = 0f;
        _atlasView.AnchorBottom = 0f;
        _atlasView.OffsetLeft = 0f;
        _atlasView.OffsetTop = 0f;
        _atlasView.OffsetRight = Mathf.Round(ui.X * scale);
        _atlasView.OffsetBottom = Mathf.Round(ui.Y * scale);
        _atlasView.Scale = new Vector2(1f / scale, 1f / scale);
        if (_atlasViewport != null && IsInstanceValid(_atlasViewport))
            _atlasViewport.Msaa3D = scale >= 1.5f ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Msaa4X;
        if (_atlas3D != null)
            _atlas3D.RenderScale = scale;
    }

    /// <summary>From TickTopBar: a new window size, or a new UI size, sizes the atlas again.</summary>
    private void TickAtlasView()
    {
        if (_atlasView == null)
            return;
        if (GetTree().Root.Size != _atlasViewWindow || GetViewport().GetVisibleRect().Size != _atlasViewUi)
            SizeAtlasView();
    }
}
