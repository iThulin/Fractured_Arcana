using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// DeckEditorUi.cs
//
// Purpose:        Hearthstone-style deck editor.
//
//  Layout:
//  ┌────────────────────────┬──────────────────┐
//  │  Active Deck  (left)   │  Stash  (right)  │
//  │  scrollable list       │  scrollable list │
//  │                        ├──────────────────┤
//  │                        │  Card Preview    │
//  │                        │  (fixed bottom)  │
//  └────────────────────────┴──────────────────┘
//
//  Hovering any row in either column shows a full CardUi
//  in the bottom-right preview zone.
//  Dragging a row and releasing over the opposite column
//  moves the card (slot ↔ unslot). Arrow buttons provide
//  a click-to-move fallback.
//
// Layer:          UI
// Collaborators:  PlayerDeckService.cs, SaveManager.cs,
//                 GuildSaveData / PlayerDeckSave / OwnedCard,
//                 CardDatabase.cs, CardUi.cs (preview),
//                 UITheme.cs, SchoolColors.cs
// ============================================================

public partial class DeckEditorUi : Control
{
    [Export] public PackedScene CardUIScene;
    [Export] public string ReturnScenePath = "res://Scenes/Campus/CampusScene.tscn";

    // ── Layout nodes ─────────────────────────────────────────────────────
    private VBoxContainer _activeList;
    private VBoxContainer _stashList;
    private Control _previewZone;   // fixed bottom-right panel
    private CardUi _previewCard;   // live CardUi inside preview zone
    private Label _previewHint;
    private Label _activeDeckCountLabel;
    private Label _stashCountLabel;
    private Label _dustLabel;

    // Drop highlight overlays (full-column tint when drag is in flight)
    private ColorRect _activeDropHighlight;
    private ColorRect _stashDropHighlight;

    // ── Filter state ─────────────────────────────────────────────────────
    private string _stashSearch = "";
    // Declare once near the top of BuildRow, after the sibling count block:

    // ── Drag state ────────────────────────────────────────────────────────
    private DeckRowControl _draggedRow = null;
    private bool _isDragging = false;
    private bool _dragFromActive = false;
    private Label _dragGhost = null;  // follows cursor

    // ── Preview size ──────────────────────────────────────────────────────
    // The preview zone is this tall (px). Card is scaled to fit inside it.
    private const float PreviewZoneHeight = 380f;
    private const float RightColumnWidth = 300f;
    private int _bodyTopOffset = 60;

    // ─────────────────────────────────────────────────────────────────────
    // Boot
    // ─────────────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Build
    // ─────────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        // Background
        var bg = new ColorRect { Color = UITheme.CampusBg };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        BuildTopBar();

        if (PlayerSession.IsOnExpedition)
        {
            var banner = new PanelContainer();
            banner.SetAnchorsPreset(LayoutPreset.TopWide);
            banner.OffsetTop = 60;
            banner.OffsetBottom = 88;
            banner.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(UITheme.Warning.R, UITheme.Warning.G,
                                    UITheme.Warning.B, 0.15f),
                BorderColor = UITheme.Warning,
                BorderWidthBottom = 1,
            });
            var bannerLabel = new Label
            {
                Text = "You are on an expedition — deck editing is locked until you return to campus.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            bannerLabel.SetAnchorsPreset(LayoutPreset.FullRect);
            bannerLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            bannerLabel.AddThemeColorOverride("font_color", UITheme.Warning);
            banner.AddChild(bannerLabel);
            AddChild(banner);

            // Push the body down to make room for the banner
            // (body is added after this block, so adjust its OffsetTop)
            // Store the extra offset for the body HBoxContainer:
            _bodyTopOffset = 88;
        }

        // ── Body: left column + right column ─────────────────────────────
        var body = new HBoxContainer();
        body.SetAnchorsPreset(LayoutPreset.FullRect);
        body.OffsetTop = _bodyTopOffset;
        body.AddThemeConstantOverride("separation", 0);
        AddChild(body);

        // ── LEFT: Active Deck ─────────────────────────────────────────────
        var leftOuter = BuildColumnShell(body,
            UITheme.BgBase, UITheme.Violet,
            "Active Deck", ref _activeDeckCountLabel,
            expandFill: true);

        _activeDropHighlight = AddDropHighlight(leftOuter, UITheme.Violet);

        var leftScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        leftOuter.AddChild(leftScroll);
        _activeList = MakeVBox(4);
        MakeInnerMargin(leftScroll).AddChild(_activeList);

        // ── RIGHT: Stash + Preview ────────────────────────────────────────
        // A VBoxContainer that holds:  [stash column shell]  [preview zone]
        var rightCol = new VBoxContainer();
        rightCol.CustomMinimumSize = new Vector2(RightColumnWidth, 0);
        rightCol.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        rightCol.SizeFlagsVertical = SizeFlags.ExpandFill;
        rightCol.AddThemeConstantOverride("separation", 0);
        body.AddChild(rightCol);

        // ── Preview zone (Upper right, fixed height) ──────────────────────
        _previewZone = new Control
        {
            CustomMinimumSize = new Vector2(RightColumnWidth, PreviewZoneHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
            ClipContents = true,
        };
        var previewBg = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.04f, 0.08f, 1f),
            BorderColor = UITheme.NeutralDim,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
        };
        var previewPanel = new PanelContainer();
        previewPanel.SetAnchorsPreset(LayoutPreset.FullRect);
        previewPanel.AddThemeStyleboxOverride("panel", previewBg);
        previewPanel.MouseFilter = MouseFilterEnum.Ignore;
        _previewZone.AddChild(previewPanel);

        _previewHint = new Label
        {
            Text = "Hover a card\nto preview",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "HintLabel",
        };
        _previewHint.SetAnchorsPreset(LayoutPreset.FullRect);
        _previewHint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _previewHint.AddThemeColorOverride("font_color", UITheme.TextDim);
        _previewZone.AddChild(_previewHint);

        rightCol.AddChild(_previewZone);

        // Stash list (Lower right)
        var stashOuter = BuildColumnShell(rightCol,
            UITheme.BgDeep, UITheme.NeutralDim,
            "Stash", ref _stashCountLabel,
            expandFill: true,
            parentIsVBox: true);

        _stashDropHighlight = AddDropHighlight(stashOuter, UITheme.NeutralDim);

        // Search bar inside stash column
        var searchBar = new LineEdit
        {
            PlaceholderText = "Search…",
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        searchBar.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        searchBar.TextChanged += (t) =>
        {
            _stashSearch = t;
            RefreshStash(SaveManager.ActiveSave);
        };
        stashOuter.AddChild(searchBar);

        var stashScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        stashOuter.AddChild(stashScroll);
        _stashList = MakeVBox(4);
        MakeInnerMargin(stashScroll).AddChild(_stashList);


        // ── Drag ghost label ──────────────────────────────────────────────
        _dragGhost = new Label
        {
            Text = "",
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 1000,
        };
        _dragGhost.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _dragGhost.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        var ghostStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.18f, 0.95f),
            BorderColor = UITheme.Violet,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 5,
            ContentMarginBottom = 5,
        };
        _dragGhost.AddThemeStyleboxOverride("normal", ghostStyle);
        AddChild(_dragGhost);


        GD.Print($"[DeckEditor] card_disenchant feature active: {PlayerSession.HasFeature("card_disenchant")}");
        GD.Print($"[DeckEditor] dissolution_chamber tier: {SaveManager.ActiveSave?.Buildings?.Find(b => b.Id == "dissolution_chamber")?.Tier ?? -1}");

        // Ensure building features are active for this screen
        // (CampusScreen.RefreshAll does this too, but deck editor
        // can be opened independently)
        PlayerSession.ClearRunState();
        BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        BuildingEffectApplier.ApplyCampusEffects(SaveManager.ActiveSave);
        Refresh();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Top bar
    // ─────────────────────────────────────────────────────────────────────

    private void BuildTopBar()
    {
        var topBar = new Panel();
        topBar.SetAnchorsPreset(LayoutPreset.TopWide);
        topBar.OffsetBottom = 60;
        topBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.CampusTitleBarBg,
            BorderColor = UITheme.CampusTitleBarBorder,
            BorderWidthBottom = 2,
        });
        AddChild(topBar);

        var backBtn = new Button
        {
            Text = "← Back",
            CustomMinimumSize = new Vector2(90, 36),
        };
        backBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        backBtn.SetAnchorsPreset(LayoutPreset.CenterLeft);
        backBtn.OffsetLeft = 16; backBtn.OffsetRight = 106;
        backBtn.OffsetTop = -18; backBtn.OffsetBottom = 18;
        UITheme.ApplyButtonStyle(backBtn, isPrimary: false);
        backBtn.Pressed += () => GetTree().ChangeSceneToFile(ReturnScenePath);
        topBar.AddChild(backBtn);

        var titleLbl = new Label
        {
            Text = "Manage Deck",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLbl.SetAnchorsPreset(LayoutPreset.FullRect);
        titleLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        titleLbl.AddThemeColorOverride("font_color", UITheme.CampusTitleColor);
        topBar.AddChild(titleLbl);

        _dustLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dustLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        _dustLabel.OffsetRight = -16;
        _dustLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _dustLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
        topBar.AddChild(_dustLabel);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Column shell builder
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a PanelContainer shell with a header strip and returns the
    /// inner VBoxContainer that callers add content into.
    /// <paramref name="parent"/> can be an HBoxContainer or VBoxContainer.
    /// </summary>
    private VBoxContainer BuildColumnShell(
        Container parent,
        Color bgColor, Color accentColor,
        string title, ref Label countLabel,
        bool expandFill,
        bool parentIsVBox = false)
    {
        var shell = new PanelContainer();
        if (expandFill)
        {
            shell.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            shell.SizeFlagsVertical = SizeFlags.ExpandFill;
        }
        shell.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = bgColor,
            BorderColor = accentColor,
            BorderWidthRight = 1,
        });
        parent.AddChild(shell);

        var vbox = MakeVBox(0);
        vbox.SizeFlagsVertical = SizeFlags.ExpandFill;
        shell.AddChild(vbox);

        // Header strip
        var hdrPanel = new PanelContainer();
        hdrPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(accentColor.R, accentColor.G, accentColor.B, 0.10f),
            BorderColor = accentColor,
            BorderWidthBottom = 1,
        });
        var hdrMargin = new MarginContainer();
        hdrMargin.AddThemeConstantOverride("margin_left", 14);
        hdrMargin.AddThemeConstantOverride("margin_right", 14);
        hdrMargin.AddThemeConstantOverride("margin_top", 8);
        hdrMargin.AddThemeConstantOverride("margin_bottom", 8);
        hdrPanel.AddChild(hdrMargin);

        var hdrRow = new HBoxContainer();
        hdrRow.AddThemeConstantOverride("separation", 8);
        hdrMargin.AddChild(hdrRow);

        var titleLbl = new Label { Text = title };
        titleLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        titleLbl.AddThemeColorOverride("font_color", UITheme.CampusTitleColor);
        titleLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        hdrRow.AddChild(titleLbl);

        var cntLbl = new Label();
        cntLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        cntLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        hdrRow.AddChild(cntLbl);
        countLabel = cntLbl;

        vbox.AddChild(hdrPanel);
        return vbox;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Refresh
    // ─────────────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var save = SaveManager.ActiveSave;
        if (_dustLabel != null)
            _dustLabel.Text = save != null
                ? $"✦ {save.ArcaneSplinters} Splinters   ◈ {save.Gold} Gold"
                : "";

        if (save?.PlayerDeck == null)
        {
            Clear(_activeList); _activeList?.AddChild(MakeStub("No save loaded."));
            Clear(_stashList); _stashList?.AddChild(MakeStub("No save loaded."));
            return;
        }

        RefreshActive(save);
        RefreshStash(save);
    }

    private void RefreshActive(GuildSaveData save)
    {
        Clear(_activeList);
        var activeIds = save.PlayerDeck.ActiveDeckInstanceIds ?? new List<string>();
        int count = activeIds.Count;
        bool tooFew = count < PlayerDeckSave.MinDeckSize;

        if (_activeDeckCountLabel != null)
        {
            _activeDeckCountLabel.Text = $"{count} / {PlayerDeckSave.MaxDeckSize}";
            _activeDeckCountLabel.AddThemeColorOverride("font_color",
                tooFew ? UITheme.Danger :
                count == PlayerDeckSave.MaxDeckSize ? UITheme.Warning :
                                                      UITheme.Success);
        }

        if (tooFew)
            _activeList.AddChild(MakeInfoLabel(
                $"Need {PlayerDeckSave.MinDeckSize - count} more card(s) to run.",
                UITheme.Danger));

        var cards = activeIds
            .Select(id => save.PlayerDeck.Cards?.Find(c => c.InstanceId == id))
            .Where(c => c != null)
            .OrderBy(c => c.BlueprintId)
            .ThenByDescending(c => c.PointsSpent)
            .ToList();

        foreach (var owned in cards)
            _activeList.AddChild(BuildRow(owned, save, isActive: true));
    }

    private void RefreshStash(GuildSaveData save)
    {
        Clear(_stashList);
        var activeSet = new HashSet<string>(
            save.PlayerDeck.ActiveDeckInstanceIds ?? new List<string>());
        var stashed = (save.PlayerDeck.Cards ?? new List<OwnedCard>())
                        .Where(c => !activeSet.Contains(c.InstanceId))
                        .OrderBy(c => c.BlueprintId)
                        .ThenByDescending(c => c.PointsSpent)
                        .ToList();

        if (_stashCountLabel != null)
            _stashCountLabel.Text = stashed.Count.ToString();

        if (!string.IsNullOrEmpty(_stashSearch))
            stashed = stashed.Where(c => c.BlueprintId.Contains(
                _stashSearch, StringComparison.OrdinalIgnoreCase)).ToList();

        if (stashed.Count == 0)
        {
            _stashList.AddChild(MakeStub(string.IsNullOrEmpty(_stashSearch)
                ? "Stash is empty." : "No cards match."));
            return;
        }

        foreach (var owned in stashed)
            _stashList.AddChild(BuildRow(owned, save, isActive: false));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Row
    // ─────────────────────────────────────────────────────────────────────

    private Control BuildRow(OwnedCard owned, GuildSaveData save, bool isActive)
    {
        bool onExpedition = PlayerSession.IsOnExpedition;

        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, owned.BlueprintId, StringComparison.OrdinalIgnoreCase));

        string topHalfName = bp?.Prebuilt?.TopHalf?.Name ?? owned.BlueprintId;
        string botHalfName = bp?.Prebuilt?.BottomHalf?.Name ?? "";
        int topMana = bp?.Prebuilt?.TopHalf?.ManaCost ?? 0;
        int botMana = bp?.Prebuilt?.BottomHalf?.ManaCost ?? 0;
        var rarity = bp?.Rarity ?? CardRarity.Common;
        var school = bp?.School ?? CardSchool.Adept;
        Color accent = SchoolColors.GetBorderColor(school);
        Color dark = SchoolColors.GetDarkColor(school);

        // How many sibling copies of this blueprint exist in the same zone?
        // Used only for the disambiguation badge — no longer used for grouping.
        var activeSet = new HashSet<string>(
            save.PlayerDeck.ActiveDeckInstanceIds ?? new List<string>());
        int siblingCount = isActive
            ? (save.PlayerDeck.ActiveDeckInstanceIds ?? new List<string>())
                .Count(id => save.PlayerDeck.Cards?
                    .Find(c => c.InstanceId == id)?.BlueprintId == owned.BlueprintId)
            : (save.PlayerDeck.Cards ?? new List<OwnedCard>())
                .Count(c => !activeSet.Contains(c.InstanceId) && c.BlueprintId == owned.BlueprintId);

        var row = new DeckRowControl
        {
            BlueprintId = owned.BlueprintId,
            Copies = new List<OwnedCard> { owned },   // single-instance list kept for drag compat
            IsActive = isActive,
            DisplayTopName = topHalfName,
            DisplayBotName = botHalfName,
        };
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.CustomMinimumSize = new Vector2(0, 46);
        row.MouseFilter = MouseFilterEnum.Stop;

        var normalStyle = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,
            BorderColor = accent,
            BorderWidthLeft = 3,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = UITheme.CornerRadius - 1,
            CornerRadiusTopRight = UITheme.CornerRadius - 1,
            CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
            CornerRadiusBottomRight = UITheme.CornerRadius - 1,
            ContentMarginLeft = 10,
            ContentMarginRight = 8,
            ContentMarginTop = 7,
            ContentMarginBottom = 7,
        };
        var hoverStyle = normalStyle.Duplicate() as StyleBoxFlat;
        hoverStyle.BgColor = new Color(
            Mathf.Min(UITheme.SurfaceLight.R * 1.3f, 1f),
            Mathf.Min(UITheme.SurfaceLight.G * 1.3f, 1f),
            Mathf.Min(UITheme.SurfaceLight.B * 1.45f, 1f), 1f);

        row.AddThemeStyleboxOverride("panel", normalStyle);
        row.NormalStyle = normalStyle;
        row.HoverStyle = hoverStyle;

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 6);
        hbox.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(hbox);

        // Class label — fixed width
        var classLbl = new Label { Text = school.ToString() };
        classLbl.CustomMinimumSize = new Vector2(150, 0);
        classLbl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        classLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        classLbl.AddThemeColorOverride("font_color", accent);
        classLbl.MouseFilter = MouseFilterEnum.Ignore;
        classLbl.VerticalAlignment = VerticalAlignment.Center;
        classLbl.ClipContents = true;
        classLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        hbox.AddChild(classLbl);

        var div1 = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.20f),
            CustomMinimumSize = new Vector2(1, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        hbox.AddChild(div1);

        // Top half block
        var topBlock = new HBoxContainer();
        topBlock.CustomMinimumSize = new Vector2(340, 0);
        topBlock.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        topBlock.AddThemeConstantOverride("separation", 4);
        topBlock.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(topBlock);

        topBlock.AddChild(CardRowHelpers.MakePip(topMana.ToString(), dark));
        var topLbl = new Label { Text = topHalfName };
        topLbl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        topLbl.CustomMinimumSize = new Vector2(150, 0);
        topLbl.ClipContents = true;
        topLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        topLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        topLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(rarity.ToString()));
        topLbl.MouseFilter = MouseFilterEnum.Ignore;
        topLbl.AutowrapMode = TextServer.AutowrapMode.Off;
        topBlock.AddChild(topLbl);
        CardRowHelpers.AddElementTags(topBlock, bp?.Prebuilt?.TopHalf);

        // Divider between halves
        if (!string.IsNullOrEmpty(botHalfName))
        {
            var div = new ColorRect
            {
                Color = new Color(0, 0, 0, 0.20f),
                CustomMinimumSize = new Vector2(1, 0),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            div.AddThemeConstantOverride("margin_left", 4);
            div.AddThemeConstantOverride("margin_right", 4);
            hbox.AddChild(div);

            var botBlock = new HBoxContainer();
            botBlock.CustomMinimumSize = new Vector2(340, 0);
            botBlock.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            botBlock.AddThemeConstantOverride("separation", 4);
            botBlock.MouseFilter = MouseFilterEnum.Ignore;
            hbox.AddChild(botBlock);

            botBlock.AddChild(CardRowHelpers.MakePip(botMana.ToString(), dark));
            var botLbl = new Label { Text = botHalfName };
            botLbl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            botLbl.CustomMinimumSize = new Vector2(110, 0);
            botLbl.ClipContents = true;
            botLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            botLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            botLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(rarity.ToString()));
            botLbl.MouseFilter = MouseFilterEnum.Ignore;
            botLbl.AutowrapMode = TextServer.AutowrapMode.Off;
            botBlock.AddChild(botLbl);
            CardRowHelpers.AddElementTags(botBlock, bp?.Prebuilt?.BottomHalf);
        }

        var div2 = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.20f),
            CustomMinimumSize = new Vector2(1, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        hbox.AddChild(div2);

        // Spacer — pushes everything after it to the right
        var spacer = new Control();
        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        spacer.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(spacer);

        // Badges
        var badges = new HBoxContainer();
        badges.AddThemeConstantOverride("separation", 4);
        badges.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        badges.MouseFilter = MouseFilterEnum.Ignore;
        hbox.AddChild(badges);

        if (owned.PointsSpent > 0)
            badges.AddChild(MakeBadge(TierBadge(owned.PointsSpent), new Color(0.65f, 0.50f, 0.10f)));

        // Disenchant button (gated by feature flag, before arrow button)
        bool canDisenchant = PlayerSession.HasFeature("card_disenchant")
                             && !onExpedition;
        bool atFloor = (save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0) <= save.MinDeckSize;
        bool disenchantBlocked = isActive && atFloor;

        if (canDisenchant && !owned.IsStarter)
        {
            int yield = DisenchantValues.GetYield(owned);
            var disBtn = new Button
            {
                Text = $"✕ +{yield}✦",
                Disabled = disenchantBlocked,
                CustomMinimumSize = new Vector2(64, 28),
                FocusMode = FocusModeEnum.None,
                TooltipText = disenchantBlocked
                    ? $"Cannot disenchant — deck at minimum size ({save.MinDeckSize})"
                    : $"Disenchant for {yield} Arcane Splinters",
            };
            disBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(disBtn, isPrimary: false);
            disBtn.Modulate = new Color(1f, 0.55f, 0.45f);
            disBtn.MouseFilter = MouseFilterEnum.Stop;
            var capturedOwned = owned;
            disBtn.Pressed += () => OnDisenchantPressed(capturedOwned, save);
            hbox.AddChild(disBtn);
        }

        // Arrow button (slot / unslot)
        // ── Arrow button (slot / unslot) ──────────────────────────────
        bool deckAtMin = (save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0)
                            <= PlayerDeckSave.MinDeckSize;
        bool deckFull = (save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0)
                            >= PlayerDeckSave.MaxDeckSize;

        // Slotting costs gold; unslotting is free
        int slotCost = PlayerSession.CardSlotCost;
        bool canAffordSlot = (save.Gold >= slotCost);

        string btnText;
        bool btnDisabled;

        if (onExpedition)
        {
            btnText = "🔒";
            btnDisabled = true;
        }
        else if (isActive)
        {
            btnText = "→";
            btnDisabled = deckAtMin;
        }
        else
        {
            btnText = slotCost > 0 ? $"← {slotCost}◈" : "←";
            btnDisabled = deckFull || !canAffordSlot;
        }

        var btn = new Button
        {
            Text = btnText,
            Disabled = btnDisabled,
            CustomMinimumSize = new Vector2(slotCost > 0 && !isActive ? 56 : 28, 28),
            FocusMode = FocusModeEnum.None,
            TooltipText = onExpedition ? "Return to campus to edit your deck."
                        : !isActive && !canAffordSlot ? $"Costs {slotCost} gold to slot."
                        : "",
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: !isActive && !btnDisabled);
        btn.MouseFilter = MouseFilterEnum.Stop;
        var capturedOwnedBtn = owned;
        btn.Pressed += () =>
        {
            MoveCard(save, capturedOwnedBtn, isActive);
            Refresh();
        };
        hbox.AddChild(btn);

        var capturedBp = bp;
        var capturedOwnedPreview = owned;
        row.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                ShowPreview(capturedBp, capturedOwnedPreview);
        };

        // Drag
        row.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                    BeginDrag(row, isActive, topHalfName);
                else if (_isDragging)
                    FinishDrag(save);
            }
        };

        return row;
    }

    private static string TierBadge(int pointsSpent) => pointsSpent switch
    {
        1 => "Inscribed",
        2 => "Refined",
        3 => "Attuned",
        4 => "Mastered",
        5 => "Transcendent",
        _ => "Upgraded"
    };


    // ─────────────────────────────────────────────────────────────────────
    // Preview
    // ─────────────────────────────────────────────────────────────────────

    private void ShowPreview(CardBlueprint bp, OwnedCard owned)
    {
        if (_previewZone == null || CardUIScene == null || bp == null) return;
        if (_previewHint != null) _previewHint.Visible = false;

        if (_previewCard != null && IsInstanceValid(_previewCard))
        {
            _previewCard.QueueFree();
            _previewCard = null;
        }

        // Use the upgraded version if available, fall back to blueprint prebuilt
        Card card = null;
        if (owned != null && owned.PointsSpent > 0)
            card = CardUpgradeApplier.Apply(owned.BlueprintId, owned.TopTier, owned.BotTier);
        card ??= bp.Prebuilt;

        if (card == null) return;

        _previewCard = CardUIScene.Instantiate<CardUi>();
        _previewZone.AddChild(_previewCard);
        _previewCard.SetCard(card.TopHalf, card.BottomHalf);
        _previewCard.Modulate = Colors.White;
        _previewCard.Position = Vector2.Zero;
        _previewCard.Rotation = 0f;
        _previewCard.Scale = Vector2.One;
        _previewCard.SetProcess(false);

        CallDeferred(nameof(LayoutPreviewCard));
    }

    private void LayoutPreviewCard()
    {
        if (_previewCard == null || !IsInstanceValid(_previewCard)) return;

        // Use the constant width if Size hasn't been computed yet
        float zoneW = _previewZone.Size.X > 10f ? _previewZone.Size.X : RightColumnWidth;
        float zoneH = _previewZone.Size.Y > 10f ? _previewZone.Size.Y : PreviewZoneHeight;
        float cardW = UITheme.LibraryCardWidth;
        float cardH = UITheme.LibraryCardHeight;

        float scale = Mathf.Min(
            (zoneW - 16f) / cardW,
            (zoneH - 16f) / cardH);
        scale = Mathf.Clamp(scale, 0.4f, 1.1f);

        _previewCard.Scale = new Vector2(scale, scale);
        _previewCard.Position = new Vector2(
            (zoneW - cardW * scale) * 0.5f,
            (zoneH - cardH * scale) * 0.5f);
    }

    private void HidePreview()
    {
        if (_previewCard != null && IsInstanceValid(_previewCard))
        {
            _previewCard.QueueFree();
            _previewCard = null;
        }
        if (_previewHint != null) _previewHint.Visible = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Drag
    // ─────────────────────────────────────────────────────────────────────

    private void BeginDrag(DeckRowControl row, bool fromActive, string displayName)
    {
        _isDragging = true;
        _draggedRow = row;
        _dragFromActive = fromActive;
        _dragGhost.Text = displayName;
        _dragGhost.Visible = true;
        _dragGhost.GlobalPosition =
            GetViewport().GetMousePosition() + new Vector2(14, -10);

        if (_activeDropHighlight != null)
            _activeDropHighlight.Visible = !fromActive; // light up opposite column
        if (_stashDropHighlight != null)
            _stashDropHighlight.Visible = fromActive;
    }

    private void FinishDrag(GuildSaveData save)
    {
        _isDragging = false;
        _dragGhost.Visible = false;
        if (_activeDropHighlight != null) _activeDropHighlight.Visible = false;
        if (_stashDropHighlight != null) _stashDropHighlight.Visible = false;

        if (_draggedRow == null) return;

        var mouse = GetViewport().GetMousePosition();
        bool overActive = _activeList != null &&
                          _activeList.GetGlobalRect().GrowIndividual(0, 40, 0, 0)
                                     .HasPoint(mouse);
        bool overStash = _stashList != null &&
                          _stashList.GetGlobalRect().GrowIndividual(0, 40, 0, 0)
                                    .HasPoint(mouse);

        var owned = _draggedRow.Copies.FirstOrDefault();
        if (owned != null)
        {
            if (_dragFromActive && overStash)
                MoveCard(save, owned, isActive: true);
            else if (!_dragFromActive && overActive)
                MoveCard(save, owned, isActive: false);
        }

        _draggedRow = null;
        Refresh();
    }

    private void MoveCard(GuildSaveData save, OwnedCard owned, bool isActive)
    {
        if (isActive)
        {
            // Unslotting — free, starters included
            PlayerDeckService.UnslotCard(save, owned.InstanceId);
        }
        else
        {
            // Slotting — costs gold
            int cost = PlayerSession.CardSlotCost;
            if (save.Gold < cost)
            {
                GD.Print($"[DeckEditor] Cannot slot — need {cost} gold, have {save.Gold}.");
                return;
            }
            save.Gold -= cost;
            PlayerDeckService.SlotCard(save, owned.InstanceId);
            GD.Print($"[DeckEditor] Slotted '{owned.BlueprintId}'. " +
                     $"Cost: {cost}g. Remaining gold: {save.Gold}.");
        }
        SaveManager.Save();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Global input — mouse-up cancels drag anywhere on screen
    // ─────────────────────────────────────────────────────────────────────

    public override void _Input(InputEvent @event)
    {
        if (_isDragging && @event is InputEventMouseButton mb
            && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
        {
            FinishDrag(SaveManager.ActiveSave);
        }

        if (_isDragging && @event is InputEventMouseMotion motion && _dragGhost != null)
        {
            _dragGhost.GlobalPosition = motion.GlobalPosition + new Vector2(14, -10);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Global input — mouse-up cancels drag anywhere on screen
    // ─────────────────────────────────────────────────────────────────────
    private void OnDisenchantPressed(OwnedCard owned, GuildSaveData save)
    {
        if (save == null || owned == null) return;
        if (owned.IsStarter) return;

        // Double-check floor — button should already be disabled but belt-and-suspenders
        bool isActive = save.PlayerDeck.ActiveDeckInstanceIds?.Contains(owned.InstanceId) ?? false;
        if (isActive && (save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0) <= save.MinDeckSize)
        {
            GD.Print("[Disenchant] Blocked — deck at minimum size.");
            return;
        }

        int yield = DisenchantValues.GetYield(owned);

        // Remove from active deck if slotted
        save.PlayerDeck.ActiveDeckInstanceIds?.Remove(owned.InstanceId);
        // Remove from owned cards entirely
        save.PlayerDeck.Cards?.Remove(owned);

        save.ArcaneSplinters += yield;

        SaveManager.Save();
        GD.Print($"[Disenchant] '{owned.BlueprintId}' removed. +{yield} splinters. " +
                 $"Total: {save.ArcaneSplinters}");

        Refresh();
    }
    // ─────────────────────────────────────────────────────────────────────
    // Layout helpers
    // ─────────────────────────────────────────────────────────────────────

    private MarginContainer MakeInnerMargin(ScrollContainer scroll)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 12);
        m.AddThemeConstantOverride("margin_right", 12);
        m.AddThemeConstantOverride("margin_top", 8);
        m.AddThemeConstantOverride("margin_bottom", 8);
        m.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        m.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        scroll.AddChild(m);
        return m;
    }

    private VBoxContainer MakeVBox(int sep)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return v;
    }

    private ColorRect AddDropHighlight(VBoxContainer parent, Color accent)
    {
        // We need to overlay the shell's PanelContainer parent, not the VBox.
        // The PanelContainer is the VBox's parent — walk up one level.
        var shell = parent.GetParent() as PanelContainer;
        if (shell == null) return null;

        var highlight = new ColorRect
        {
            Color = new Color(accent.R, accent.G, accent.B, 0.15f),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        highlight.SetAnchorsPreset(LayoutPreset.FullRect);
        shell.AddChild(highlight);
        return highlight;
    }

    private void Clear(VBoxContainer c)
    {
        if (c == null) return;
        foreach (Node n in c.GetChildren()) n.QueueFree();
    }

    private Label MakeStub(string t)
    {
        var l = new Label
        {
            Text = t,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusStubFontSize);
        l.Modulate = UITheme.CampusStubText;
        return l;
    }

    private Label MakeInfoLabel(string t, Color col)
    {
        var l = new Label
        {
            Text = t,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        l.AddThemeColorOverride("font_color", col);
        return l;
    }

    private Label MakeBadge(string text, Color color)
    {
        var l = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 16),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize - 1);
        l.AddThemeColorOverride("font_color", Colors.White);
        var style = new StyleBoxFlat { BgColor = new Color(color.R, color.G, color.B) };
        style.SetCornerRadiusAll(3);
        style.ContentMarginLeft = 5;
        style.ContentMarginRight = 5;
        style.ContentMarginTop = 2;
        style.ContentMarginBottom = 2;
        l.AddThemeStyleboxOverride("normal", style);
        return l;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DeckRowControl — PanelContainer that carries card metadata for the drag
// system and handles its own hover style swap via Notification.
// ─────────────────────────────────────────────────────────────────────────────
public partial class DeckRowControl : PanelContainer
{
    public string BlueprintId;
    public List<OwnedCard> Copies;
    public bool IsActive;
    public string DisplayTopName;
    public string DisplayBotName;
    public StyleBoxFlat NormalStyle;
    public StyleBoxFlat HoverStyle;

    public override void _Notification(int what)
    {
        if (what == NotificationMouseEnter && HoverStyle != null)
            AddThemeStyleboxOverride("panel", HoverStyle);
        else if (what == NotificationMouseExit && NormalStyle != null)
            AddThemeStyleboxOverride("panel", NormalStyle);
    }
}
