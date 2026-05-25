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
    private Control       _previewZone;   // fixed bottom-right panel
    private CardUi        _previewCard;   // live CardUi inside preview zone
    private Label         _previewHint;
    private Label         _activeDeckCountLabel;
    private Label         _stashCountLabel;
    private Label         _dustLabel;

    // Drop highlight overlays (full-column tint when drag is in flight)
    private ColorRect _activeDropHighlight;
    private ColorRect _stashDropHighlight;

    // ── Filter state ─────────────────────────────────────────────────────
    private string _stashSearch = "";

    // ── Drag state ────────────────────────────────────────────────────────
    private DeckRowControl _draggedRow     = null;
    private bool           _isDragging     = false;
    private bool           _dragFromActive = false;
    private Label          _dragGhost      = null;  // follows cursor

    // ── Preview size ──────────────────────────────────────────────────────
    // The preview zone is this tall (px). Card is scaled to fit inside it.
    private const float PreviewZoneHeight = 380f;
    private const float RightColumnWidth  = 300f;

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

        // ── Body: left column + right column ─────────────────────────────
        var body = new HBoxContainer();
        body.SetAnchorsPreset(LayoutPreset.FullRect);
        body.OffsetTop = 60;
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
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        leftOuter.AddChild(leftScroll);
        _activeList = MakeVBox(4);
        MakeInnerMargin(leftScroll).AddChild(_activeList);

        // ── RIGHT: Stash + Preview ────────────────────────────────────────
        // A VBoxContainer that holds:  [stash column shell]  [preview zone]
        var rightCol = new VBoxContainer();
        rightCol.CustomMinimumSize   = new Vector2(RightColumnWidth, 0);
        rightCol.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        rightCol.SizeFlagsVertical   = SizeFlags.ExpandFill;
        rightCol.AddThemeConstantOverride("separation", 0);
        body.AddChild(rightCol);

        // ── Preview zone (Upper right, fixed height) ──────────────────────
        _previewZone = new Control
        {
            CustomMinimumSize   = new Vector2(RightColumnWidth, PreviewZoneHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ShrinkBegin,
            ClipContents        = true,
        };
        var previewBg = new StyleBoxFlat
        {
            BgColor             = new Color(0.04f, 0.04f, 0.08f, 1f),
            BorderColor         = UITheme.NeutralDim,
            BorderWidthBottom   = 1,
            BorderWidthLeft     = 1,
        };
        var previewPanel = new PanelContainer();
        previewPanel.SetAnchorsPreset(LayoutPreset.FullRect);
        previewPanel.AddThemeStyleboxOverride("panel", previewBg);
        previewPanel.MouseFilter = MouseFilterEnum.Ignore;
        _previewZone.AddChild(previewPanel);

        _previewHint = new Label
        {
            Text                = "Hover a card\nto preview",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Name                = "HintLabel",
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
            PlaceholderText     = "Search…",
            CustomMinimumSize   = new Vector2(0, 30),
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
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        stashOuter.AddChild(stashScroll);
        _stashList = MakeVBox(4);
        MakeInnerMargin(stashScroll).AddChild(_stashList);


        // ── Drag ghost label ──────────────────────────────────────────────
        _dragGhost = new Label
        {
            Text        = "",
            Visible     = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex      = 1000,
        };
        _dragGhost.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _dragGhost.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        var ghostStyle = new StyleBoxFlat
        {
            BgColor        = new Color(0.08f, 0.08f, 0.18f, 0.95f),
            BorderColor    = UITheme.Violet,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 5, ContentMarginBottom = 5,
        };
        _dragGhost.AddThemeStyleboxOverride("normal", ghostStyle);
        AddChild(_dragGhost);

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
            BgColor           = UITheme.CampusTitleBarBg,
            BorderColor       = UITheme.CampusTitleBarBorder,
            BorderWidthBottom = 2,
        });
        AddChild(topBar);

        var backBtn = new Button
        {
            Text              = "← Back",
            CustomMinimumSize = new Vector2(90, 36),
        };
        backBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        backBtn.SetAnchorsPreset(LayoutPreset.CenterLeft);
        backBtn.OffsetLeft = 16; backBtn.OffsetRight  = 106;
        backBtn.OffsetTop  = -18; backBtn.OffsetBottom = 18;
        UITheme.ApplyButtonStyle(backBtn, isPrimary: false);
        backBtn.Pressed += () => GetTree().ChangeSceneToFile(ReturnScenePath);
        topBar.AddChild(backBtn);

        var titleLbl = new Label
        {
            Text                = "Manage Deck",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        titleLbl.SetAnchorsPreset(LayoutPreset.FullRect);
        titleLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        titleLbl.AddThemeColorOverride("font_color", UITheme.CampusTitleColor);
        topBar.AddChild(titleLbl);

        _dustLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
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
            shell.SizeFlagsVertical   = SizeFlags.ExpandFill;
        }
        shell.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor      = bgColor,
            BorderColor  = accentColor,
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
            BgColor           = new Color(accentColor.R, accentColor.G, accentColor.B, 0.10f),
            BorderColor       = accentColor,
            BorderWidthBottom = 1,
        });
        var hdrMargin = new MarginContainer();
        hdrMargin.AddThemeConstantOverride("margin_left",   14);
        hdrMargin.AddThemeConstantOverride("margin_right",  14);
        hdrMargin.AddThemeConstantOverride("margin_top",     8);
        hdrMargin.AddThemeConstantOverride("margin_bottom",  8);
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
            _dustLabel.Text = save != null ? $"✦ {save.ArcaneSplinters} Splinters" : "";

        if (save?.PlayerDeck == null)
        {
            Clear(_activeList); _activeList?.AddChild(MakeStub("No save loaded."));
            Clear(_stashList);  _stashList?.AddChild(MakeStub("No save loaded."));
            return;
        }

        RefreshActive(save);
        RefreshStash(save);
    }

    private void RefreshActive(GuildSaveData save)
    {
        Clear(_activeList);
        var activeIds = save.PlayerDeck.ActiveDeckInstanceIds ?? new List<string>();
        int count     = activeIds.Count;
        bool tooFew   = count < PlayerDeckSave.MinDeckSize;

        if (_activeDeckCountLabel != null)
        {
            _activeDeckCountLabel.Text = $"{count} / {PlayerDeckSave.MaxDeckSize}";
            _activeDeckCountLabel.AddThemeColorOverride("font_color",
                tooFew                              ? UITheme.Danger  :
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
            .ToList();

        foreach (var group in GroupByBlueprint(cards))
            _activeList.AddChild(BuildRow(group, save, isActive: true));
    }

    private void RefreshStash(GuildSaveData save)
    {
        Clear(_stashList);
        var activeSet = new HashSet<string>(
            save.PlayerDeck.ActiveDeckInstanceIds ?? new List<string>());
        var stashed = (save.PlayerDeck.Cards ?? new List<OwnedCard>())
                        .Where(c => !activeSet.Contains(c.InstanceId))
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

        foreach (var group in GroupByBlueprint(stashed))
            _stashList.AddChild(BuildRow(group, save, isActive: false));
    }

    private List<List<OwnedCard>> GroupByBlueprint(List<OwnedCard> cards)
    {
        var dict = new Dictionary<string, List<OwnedCard>>();
        foreach (var c in cards)
        {
            if (!dict.ContainsKey(c.BlueprintId)) dict[c.BlueprintId] = new List<OwnedCard>();
            dict[c.BlueprintId].Add(c);
        }
        return dict
            .OrderByDescending(kv => kv.Value[0].IsStarter)
            .ThenBy(kv => kv.Key)
            .Select(kv => kv.Value)
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Row
    // ─────────────────────────────────────────────────────────────────────

    private Control BuildRow(List<OwnedCard> copies, GuildSaveData save, bool isActive)
    {
        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, copies[0].BlueprintId, StringComparison.OrdinalIgnoreCase));

        string displayName = CardDatabase.GetDisplayName(bp);
        string botName = bp?.Prebuilt?.BottomHalf?.Name ?? "";
        int    topMana = bp?.Prebuilt?.TopHalf?.ManaCost  ?? 0;
        int    botMana = bp?.Prebuilt?.BottomHalf?.ManaCost ?? 0;
        var    rarity  = bp?.Rarity ?? CardRarity.Common;
        var    school  = bp?.School ?? CardSchool.Generic;
        Color  accent  = SchoolColors.GetBorderColor(school);
        Color  dark    = SchoolColors.GetDarkColor(school);

        var row = new DeckRowControl
        {
            BlueprintId    = copies[0].BlueprintId,
            Copies         = copies,
            IsActive       = isActive,
            DisplayTopName = displayName,
        };
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.CustomMinimumSize   = new Vector2(0, 46);
        row.MouseFilter         = MouseFilterEnum.Stop;

        // Styles
        var normalStyle = new StyleBoxFlat
        {
            BgColor          = UITheme.SurfaceLight,
            BorderColor      = accent,
            BorderWidthLeft  = 3,
            BorderWidthTop   = 1, BorderWidthBottom = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft    = UITheme.CornerRadius - 1,
            CornerRadiusTopRight   = UITheme.CornerRadius - 1,
            CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
            CornerRadiusBottomRight = UITheme.CornerRadius - 1,
            ContentMarginLeft = 10, ContentMarginRight  = 8,
            ContentMarginTop  =  7, ContentMarginBottom = 7,
        };
        var hoverStyle = normalStyle.Duplicate() as StyleBoxFlat;
        hoverStyle.BgColor = new Color(
            Mathf.Min(UITheme.SurfaceLight.R * 1.3f, 1f),
            Mathf.Min(UITheme.SurfaceLight.G * 1.3f, 1f),
            Mathf.Min(UITheme.SurfaceLight.B * 1.45f, 1f), 1f);

        row.AddThemeStyleboxOverride("panel", normalStyle);
        row.NormalStyle = normalStyle;
        row.HoverStyle  = hoverStyle;

        // Inner HBox
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 6);
        hbox.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(hbox);

        // Mana pip
        var pip = new Label { Text = topMana.ToString() };
        pip.CustomMinimumSize       = new Vector2(24, 24);
        pip.HorizontalAlignment     = HorizontalAlignment.Center;
        pip.VerticalAlignment       = VerticalAlignment.Center;
        pip.MouseFilter             = MouseFilterEnum.Ignore;
        pip.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize + 1);
        pip.AddThemeColorOverride("font_color", Colors.White);
        var pipStyle = new StyleBoxFlat { BgColor = dark };
        pipStyle.SetCornerRadiusAll(12);
        pip.AddThemeStyleboxOverride("normal", pipStyle);
        hbox.AddChild(pip);

        // Names
        var names = new VBoxContainer();
        names.AddThemeConstantOverride("separation", 1);
        names.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        names.MouseFilter         = MouseFilterEnum.Ignore;
        hbox.AddChild(names);

        var topLbl = new Label { Text = displayName };
        topLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        topLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(rarity.ToString()));
        topLbl.MouseFilter = MouseFilterEnum.Ignore;
        names.AddChild(topLbl);

        if (!string.IsNullOrEmpty(botName))
        {
            var botLbl = new Label { Text = $"{botName}  {botMana}◆" };
            botLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            botLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            botLbl.MouseFilter = MouseFilterEnum.Ignore;
            names.AddChild(botLbl);
        }

        // Badges
        var badges = new VBoxContainer();
        badges.AddThemeConstantOverride("separation", 2);
        badges.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        badges.MouseFilter         = MouseFilterEnum.Ignore;
        hbox.AddChild(badges);

        if (copies.Count > 1)
            badges.AddChild(MakeBadge($"×{copies.Count}", UITheme.TextSecondary));
        int maxTier = copies.Max(c => c.UpgradeTier);
        if (maxTier > 0)
            badges.AddChild(MakeBadge(maxTier == 1 ? "+" : "++", UITheme.Success));
        if (copies.Any(c => c.IsStarter))
            badges.AddChild(MakeBadge("⚑", UITheme.TextDim));
        int graftCount = copies.SelectMany(c => c.Grafts ?? new List<string>())
                               .Distinct().Count();
        if (graftCount > 0)
            badges.AddChild(MakeBadge($"✦{graftCount}", new Color(0.6f, 0.85f, 1f)));

        // Arrow button
        bool allStarters = copies.All(c => c.IsStarter);
        bool deckAtMin   = (save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0)
                            <= PlayerDeckSave.MinDeckSize;
        bool deckFull    = (save.PlayerDeck.ActiveDeckInstanceIds?.Count ?? 0)
                            >= PlayerDeckSave.MaxDeckSize;

        var btn = new Button
        {
            Text              = isActive ? "→" : "←",
            Disabled          = isActive ? (allStarters || deckAtMin) : deckFull,
            CustomMinimumSize = new Vector2(28, 28),
            FocusMode         = FocusModeEnum.None,
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: !isActive);
        btn.MouseFilter = MouseFilterEnum.Stop;
        var capturedCopies = copies;
        btn.Pressed += () =>
        {
            MoveCard(save, capturedCopies, isActive);
            Refresh();
        };
        hbox.AddChild(btn);

        // ── Hover → preview ───────────────────────────────────────────────
        row.MouseEntered += () =>
        {
            GD.Print($"[DeckEditor] Row MouseEntered — {displayName}");
            ShowPreview(bp);
        };
        row.MouseExited  += () =>
        {
            if (!_isDragging) HidePreview();
        };

        // ── Drag ──────────────────────────────────────────────────────────
        row.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                    BeginDrag(row, isActive, displayName);
                else if (_isDragging)
                    FinishDrag(save);
            }
        };

        return row;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Preview
    // ─────────────────────────────────────────────────────────────────────

    private void ShowPreview(CardBlueprint bp)
    {
        if (_previewZone == null || CardUIScene == null || bp?.Prebuilt == null)
        {
            if (_previewHint != null) _previewHint.Visible = true;
            return;
        }

        if (_previewHint != null) _previewHint.Visible = false;

        if (_previewCard != null && IsInstanceValid(_previewCard))
        {
            _previewCard.QueueFree();
            _previewCard = null;
        }

        _previewCard = CardUIScene.Instantiate<CardUi>();
        _previewZone.AddChild(_previewCard);
        _previewCard.SetCard(bp.Prebuilt.TopHalf, bp.Prebuilt.BottomHalf);

        // Bypass the draw-in animation — force visible at rest position
        _previewCard.Modulate  = Colors.White;
        _previewCard.Position  = Vector2.Zero;
        _previewCard.Rotation  = 0f;
        _previewCard.Scale     = Vector2.One;
        _previewCard.SetProcess(false); // disable breathe animation

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

        _previewCard.Scale    = new Vector2(scale, scale);
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
        _isDragging     = true;
        _draggedRow     = row;
        _dragFromActive = fromActive;
        _dragGhost.Text    = displayName;
        _dragGhost.Visible = true;
        _dragGhost.GlobalPosition =
            GetViewport().GetMousePosition() + new Vector2(14, -10);

        if (_activeDropHighlight != null)
            _activeDropHighlight.Visible = !fromActive; // light up opposite column
        if (_stashDropHighlight  != null)
            _stashDropHighlight.Visible  = fromActive;
    }

    private void FinishDrag(GuildSaveData save)
    {
        _isDragging        = false;
        _dragGhost.Visible = false;
        if (_activeDropHighlight != null) _activeDropHighlight.Visible = false;
        if (_stashDropHighlight  != null) _stashDropHighlight.Visible  = false;

        if (_draggedRow == null) return;

        var mouse      = GetViewport().GetMousePosition();
        bool overActive = _activeList != null &&
                          _activeList.GetGlobalRect().GrowIndividual(0, 40, 0, 0)
                                     .HasPoint(mouse);
        bool overStash  = _stashList  != null &&
                          _stashList.GetGlobalRect().GrowIndividual(0, 40, 0, 0)
                                    .HasPoint(mouse);

        if (_dragFromActive && overStash)
            MoveCard(save, _draggedRow.Copies, isActive: true);
        else if (!_dragFromActive && overActive)
            MoveCard(save, _draggedRow.Copies, isActive: false);

        _draggedRow = null;
        SaveManager.Save();
        Refresh();
    }

    private void MoveCard(GuildSaveData save, List<OwnedCard> copies, bool isActive)
    {
        if (isActive)
        {
            var toMove = copies.FirstOrDefault(c => !c.IsStarter);
            if (toMove != null) PlayerDeckService.UnslotCard(save, toMove.InstanceId);
        }
        else
        {
            var toMove = copies.FirstOrDefault();
            if (toMove != null) PlayerDeckService.SlotCard(save, toMove.InstanceId);
        }
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
    // Layout helpers
    // ─────────────────────────────────────────────────────────────────────

    private MarginContainer MakeInnerMargin(ScrollContainer scroll)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left",   12);
        m.AddThemeConstantOverride("margin_right",  12);
        m.AddThemeConstantOverride("margin_top",     8);
        m.AddThemeConstantOverride("margin_bottom",  8);
        m.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        m.SizeFlagsVertical   = SizeFlags.ShrinkBegin;
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
            Color        = new Color(accent.R, accent.G, accent.B, 0.15f),
            MouseFilter  = MouseFilterEnum.Ignore,
            Visible      = false,
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
            Text                = t,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode        = TextServer.AutowrapMode.WordSmart,
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusStubFontSize);
        l.Modulate = UITheme.CampusStubText;
        return l;
    }

    private Label MakeInfoLabel(string t, Color col)
    {
        var l = new Label
        {
            Text                = t,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode        = TextServer.AutowrapMode.WordSmart,
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        l.AddThemeColorOverride("font_color", col);
        return l;
    }

    private Label MakeBadge(string text, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        l.AddThemeColorOverride("font_color", color);
        l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DeckRowControl — PanelContainer that carries card metadata for the drag
// system and handles its own hover style swap via Notification.
// ─────────────────────────────────────────────────────────────────────────────
public partial class DeckRowControl : PanelContainer
{
    public string          BlueprintId;
    public List<OwnedCard> Copies;
    public bool            IsActive;
    public string          DisplayTopName;
    public StyleBoxFlat    NormalStyle;
    public StyleBoxFlat    HoverStyle;

    public override void _Notification(int what)
    {
        if (what == NotificationMouseEnter && HoverStyle != null)
            AddThemeStyleboxOverride("panel", HoverStyle);
        else if (what == NotificationMouseExit && NormalStyle != null)
            AddThemeStyleboxOverride("panel", NormalStyle);
    }
}
