using Godot;

// ============================================================
// StrategicView.ProvinceHover.cs  (partial of StrategicView)
//
// Purpose:        The province readout (docs/provinces_v1.md section
//                 7c, 2026-11-03). Pointing at painted land on the
//                 strategic map shows, beside the pointer, the province
//                 there: its name; its ground and realm; the realm's
//                 stance toward the guild; the guild's influence on a
//                 meter marked at 40 (where the castle's presence stops)
//                 and 60 (held); whether the guild holds it; and what the
//                 next lunation will do to it, and why. While it shows,
//                 the atlas draws a rim around the province (section 7d).
//                 It shows on every lens. It hides over unpainted ground
//                 (unless the debug reveal is on), water and land outside
//                 every province, in city view, while a panel has the
//                 map, and while a mouse button is held. Also the lens
//                 key, a line under the lens buttons for the Political
//                 and Reach lenses. The panel copies the look of
//                 TooltipManager's tile tooltip but is its own: it sits
//                 under the strategic view's dialogs and the pause menu,
//                 and it updates in place.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.Hover.cs (TileHovered, ClearHover),
//                 WorldAtlas3D.Provinces.cs (HighlightProvince),
//                 ProvinceInfluence.cs (InfluenceLine, NextLunationLine,
//                 the lines the key names), CouncilTick.cs (StanceFor),
//                 StrategicView.cs (BuildAtlas3D wires the events, the
//                 lens card adds the key, UpdateLensButtons and _Process
//                 call in here, StanceColor), TooltipManager.cs (the look
//                 it copies)
// See:            docs/provinces_v1.md section 7c;
//                 docs/provinces_v1_7d_lenses.md
// ============================================================

public partial class StrategicView
{
    /// <summary>Screen distance from the pointer to the readout.</summary>
    private const float ProvinceHoverOffset = 18f;

    /// <summary>Width the readout's lines wrap at.</summary>
    private const float ProvinceHoverWidth = 280f;

    /// <summary>How often a readout that is showing re-reads its province, so a deed or a
    /// lunation that lands while the pointer rests shows at once.</summary>
    private const double ProvinceHoverRefreshSeconds = 0.25;

    private CanvasLayer _provinceHoverLayer;
    private PanelContainer _provinceHoverPanel;
    private Label _provinceHoverTitle;
    private Label _provinceHoverKind;
    private Label _provinceHoverStance;
    private ProgressBar _provinceHoverMeter;
    private StyleBoxFlat _provinceHoverFill;
    private Label _provinceHoverInfluence;
    private Label _provinceHoverNext;
    private Label _lensKey;

    /// <summary>The province the readout shows, or -1 while it is hidden.</summary>
    private int _provinceHoverIndex = -1;

    private double _provinceHoverWait;

    // ── Events ───────────────────────────────────────────────────────────

    /// <summary>The atlas reports a new tile under the pointer, or (-1, -1) for none.</summary>
    private void OnAtlasTileHovered(int col, int row)
    {
        int index = HoveredProvince(col, row);
        if (index == _provinceHoverIndex)
            return;
        _provinceHoverIndex = index;
        RefreshProvinceHover();
    }

    /// <summary>The pointer left the map, for a panel, a dialog or the window's edge.</summary>
    private void OnAtlasPointerLeft()
    {
        _atlas3D?.ClearHover();
        HideProvinceHover();
    }

    /// <summary>Per frame, from _Process. While the readout shows: hidden when the map takes
    /// no input, shows a city, or has a mouse button held (a drag or an orbit); otherwise
    /// kept beside the pointer and re-read a few times a second.</summary>
    private void TickProvinceHover(double delta)
    {
        if (_provinceHoverPanel == null || !IsInstanceValid(_provinceHoverPanel) || !_provinceHoverPanel.Visible)
            return;
        if (_atlas3D == null || !_atlas3D.AcceptInput || _atlas3D.CityMode
            || Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            _atlas3D?.ClearHover();
            HideProvinceHover();
            return;
        }
        _provinceHoverWait -= delta;
        if (_provinceHoverWait <= 0)
            RefreshProvinceHover();
        else
            PlaceProvinceHover();
    }

    // ── The readout ──────────────────────────────────────────────────────

    /// <summary>The province holding a tile, or -1 for water, land outside every province,
    /// unpainted ground (unless the debug reveal is on), or a world without provinces.</summary>
    private int HoveredProvince(int col, int row)
    {
        if (_world == null || !_world.InBounds(col, row) || !_world.HasProvinces())
            return -1;
        var tile = _world.GetTile(col, row);
        if (tile.IsWater || (!_debugReveal && tile.Discovery == TileDiscovery.Unseen))
            return -1;
        return _world.ProvinceIndexAt(col, row);
    }

    private void HideProvinceHover()
    {
        _provinceHoverIndex = -1;
        if (_atlas3D != null && IsInstanceValid(_atlas3D))
            _atlas3D.HighlightProvince(-1);
        if (_provinceHoverPanel != null && IsInstanceValid(_provinceHoverPanel))
            _provinceHoverPanel.Visible = false;
    }

    /// <summary>Fill the readout from the province under the pointer, or hide it.</summary>
    private void RefreshProvinceHover()
    {
        _provinceHoverWait = ProvinceHoverRefreshSeconds;
        var cycle = SaveManager.ActiveSave?.Cycle;
        var world = cycle?.World;
        if (_provinceHoverIndex < 0 || world == null || !world.HasProvinces()
            || _provinceHoverIndex >= world.Provinces.Count)
        {
            HideProvinceHover();
            return;
        }
        EnsureProvinceHover();
        var province = world.Provinces[_provinceHoverIndex];
        _provinceHoverTitle.Text = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
        _provinceHoverKind.Text = ProvinceKindLine(province);
        RefreshProvinceStance(cycle, province);
        _provinceHoverMeter.Value = province.Influence;
        _provinceHoverFill.BgColor = province.Held ? UITheme.Gold : UITheme.ArcaneBlue;
        _provinceHoverInfluence.Text = ProvinceInfluence.InfluenceLine(province);
        _provinceHoverInfluence.AddThemeColorOverride("font_color", province.Held ? UITheme.Gold : UITheme.TextPrimary);
        _provinceHoverNext.Text = ProvinceInfluence.NextLunationLine(cycle, _provinceHoverIndex);
        _provinceHoverPanel.Visible = true;
        _atlas3D?.HighlightProvince(_provinceHoverIndex);   // provinces 7d: the rim
        PlaceProvinceHover();
    }

    /// <summary>Beside the pointer, below and to the right, flipped to the other side at
    /// the window's right or bottom edge.</summary>
    private void PlaceProvinceHover()
    {
        _provinceHoverPanel.ResetSize();
        Vector2 view = GetViewport().GetVisibleRect().Size;
        Vector2 mouse = GetViewport().GetMousePosition();
        Vector2 size = _provinceHoverPanel.Size;
        float x = mouse.X + ProvinceHoverOffset;
        float y = mouse.Y + ProvinceHoverOffset;
        if (x + size.X > view.X)
            x = mouse.X - ProvinceHoverOffset - size.X;
        if (y + size.Y > view.Y)
            y = mouse.Y - ProvinceHoverOffset - size.Y;
        _provinceHoverPanel.Position = new Vector2(Mathf.Max(0f, x), Mathf.Max(0f, y));
    }

    /// <summary>"A woodland province of House Veyra.", or "..., its capital." for the
    /// capital. The realm is named as the place card names it: the faction that rules
    /// the kingdom.</summary>
    private string ProvinceKindLine(Province province)
    {
        string ground = province.Character.ToString().ToLowerInvariant();
        string article = "aeiou".IndexOf(ground[0]) >= 0 ? "An" : "A";
        return $"{article} {ground} province of {ProvinceRealm(province)}" + (province.IsCapital ? ", its capital." : ".");
    }

    /// <summary>The province's realm as the place card names it: the faction that rules the
    /// kingdom, or the kingdom's id when no faction is recorded.</summary>
    private string ProvinceRealm(Province province)
    {
        if (string.IsNullOrEmpty(province.KingdomId))
            return "no kingdom";
        if (_kingdoms != null && _kingdoms.TryGetValue(province.KingdomId, out var ks) && ks != null
            && !string.IsNullOrEmpty(ks.ControllingFactionId))
            return FactionDisplay(ks.ControllingFactionId);
        return province.KingdomId;
    }

    /// <summary>The realm's stance toward the guild, in its stance colour (section 7d: the
    /// stance left the Reach lens for this line). "The guild rules this realm." after a
    /// seizure; nothing for land in no kingdom.</summary>
    private void RefreshProvinceStance(CycleState cycle, Province province)
    {
        if (string.IsNullOrEmpty(province.KingdomId))
        {
            _provinceHoverStance.Visible = false;
            return;
        }
        _provinceHoverStance.Visible = true;
        if (_kingdoms != null && _kingdoms.TryGetValue(province.KingdomId, out var ks) && ks != null
            && ks.ControllingFactionId == KingdomTickSimulation.GuildFactionId)
        {
            _provinceHoverStance.Text = "The guild rules this realm.";
            _provinceHoverStance.AddThemeColorOverride("font_color", UITheme.Gold);
            return;
        }
        var stance = CouncilQueries.StanceFor(cycle, province.KingdomId);
        _provinceHoverStance.Text = $"Stance of {ProvinceRealm(province)} toward the guild: {stance}.";
        _provinceHoverStance.AddThemeColorOverride("font_color", StanceColor(stance));
    }

    /// <summary>Build the readout once, hidden, on a canvas layer just above the HUD.</summary>
    private void EnsureProvinceHover()
    {
        if (_provinceHoverPanel != null && IsInstanceValid(_provinceHoverPanel))
            return;
        _provinceHoverLayer = new CanvasLayer { Name = "ProvinceHoverLayer", Layer = 2 };
        AddChild(_provinceHoverLayer);

        // TooltipManager's tile tooltip look: near-black, a violet rim, a soft shadow.
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.06f, 0.10f, 0.97f),
            BorderColor = UITheme.Violet,
            ShadowColor = new Color(0f, 0f, 0f, 0.8f),
            ShadowSize = 4,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 10,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(UITheme.CornerRadius);

        _provinceHoverPanel = new PanelContainer
        {
            Name = "ProvinceReadout",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _provinceHoverPanel.AddThemeStyleboxOverride("panel", style);
        _provinceHoverLayer.AddChild(_provinceHoverPanel);

        var stack = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(ProvinceHoverWidth, 0f),
        };
        stack.AddThemeConstantOverride("separation", 4);
        _provinceHoverPanel.AddChild(stack);

        _provinceHoverTitle = HoverLabel(UITheme.CampusBodyFontSize + 1, UITheme.Gold);
        stack.AddChild(_provinceHoverTitle);
        _provinceHoverKind = HoverLabel(UITheme.CampusTinyFontSize, UITheme.TextSecondary);
        stack.AddChild(_provinceHoverKind);
        _provinceHoverStance = HoverLabel(UITheme.CampusTinyFontSize, UITheme.TextSecondary);
        stack.AddChild(_provinceHoverStance);

        _provinceHoverMeter = new ProgressBar
        {
            MinValue = 0,
            MaxValue = ProvinceInfluence.MaxInfluence,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0f, 8f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var meterBack = new StyleBoxFlat { BgColor = UITheme.NeutralDim };
        meterBack.SetCornerRadiusAll(3);
        _provinceHoverFill = new StyleBoxFlat { BgColor = UITheme.ArcaneBlue };
        _provinceHoverFill.SetCornerRadiusAll(3);
        _provinceHoverMeter.AddThemeStyleboxOverride("background", meterBack);
        _provinceHoverMeter.AddThemeStyleboxOverride("fill", _provinceHoverFill);
        // Where the castle's presence stops, and where the province is held.
        AddMeterMark(_provinceHoverMeter, ProvinceInfluence.PassiveCap, new Color(1f, 1f, 1f, 0.45f), 1f);
        AddMeterMark(_provinceHoverMeter, ProvinceInfluence.HoldLine, UITheme.TextPrimary, 2f);
        stack.AddChild(_provinceHoverMeter);

        _provinceHoverInfluence = HoverLabel(UITheme.CampusTinyFontSize, UITheme.TextPrimary);
        stack.AddChild(_provinceHoverInfluence);
        _provinceHoverNext = HoverLabel(UITheme.CampusTinyFontSize, UITheme.TextSecondary);
        stack.AddChild(_provinceHoverNext);
    }

    private static Label HoverLabel(int fontSize, Color color)
    {
        var label = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>A thin upright mark across the meter at a value, standing a little proud of
    /// it above and below.</summary>
    private static void AddMeterMark(ProgressBar meter, int value, Color color, float width)
    {
        float at = value / (float)ProvinceInfluence.MaxInfluence;
        var mark = new ColorRect
        {
            Color = color,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = at,
            AnchorRight = at,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = -width * 0.5f,
            OffsetRight = width * 0.5f,
            OffsetTop = -2f,
            OffsetBottom = 2f,
        };
        meter.AddChild(mark);
    }

    // ── The lens key ─────────────────────────────────────────────────────

    /// <summary>The lens key: a line under the lens buttons for the Political and Reach
    /// lenses (section 7d). The lens card adds it; UpdateLensButtons refreshes it.</summary>
    private Label BuildLensKey()
    {
        _lensKey = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _lensKey.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _lensKey.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        UpdateLensKey();
        return _lensKey;
    }

    private void UpdateLensKey()
    {
        if (_lensKey == null || !IsInstanceValid(_lensKey))
            return;
        string text = LensKeyText(_lens);
        _lensKey.Text = text;
        _lensKey.Visible = text.Length > 0;
    }

    /// <summary>The key's line for a lens, or "" for a lens without one. The numbers are
    /// the rules' own, so a retune changes the key with them.</summary>
    private static string LensKeyText(StrategicLens lens)
    {
        switch (lens)
        {
            case StrategicLens.Political:
                return "Political: heavy ink is a realm's border, light dashes a province's. "
                     + "Point at a province for its numbers.";
            case StrategicLens.Reach:
                return $"Reach: blue is the guild's influence, stepping up at {ProvinceInfluence.PassiveCap}, "
                     + "as far as presence goes. Gold is held. A gold edge on blue is held but below "
                     + $"{ProvinceInfluence.HoldLine}, and lost below {ProvinceInfluence.LoseLine}. "
                     + "Point at a province for its numbers.";
            default:
                return "";
        }
    }
}
