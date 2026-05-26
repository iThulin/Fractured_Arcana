using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardUpgradeScreen.cs
//
// Purpose:        Dedicated upgrade screen. Shows the player's
//                 owned cards, the current tier, upgrade cost,
//                 and a before/after preview using CardUi.
//                 Upgrade button spends ArcaneSplinters and
//                 bumps OwnedCard top/bot tiers in the save.
//                 Gated by Scriptorum tier.
// Layer:          UI
// Collaborators:  CardUpgradeApplier.cs (preview + apply),
//                 PlayerDeckService.cs (OwnedCard lookup),
//                 SaveManager.cs (ActiveSave + Save),
//                 GuildSaveData / OwnedCard,
//                 CardDatabase.cs (blueprint lookup),
//                 CardUi.cs (before/after display),
//                 UITheme.cs
// ============================================================

public partial class CardUpgradeScreen : Control
{
    [Export] public PackedScene CardUIScene;
    [Export] public string ReturnScenePath = "res://Scenes/Campus/CampusScene.tscn";

    private const int MAX_TIER = 4;
    private bool _bypassCastRequirement = false;

    public static class CardUpgradeCosts
    {
        public static readonly int[] HalfTierCost = { 0, 15, 20, 30, 45 };
        public const int SharedUpgradeCost = 15;
    }

    // ── Layout ────────────────────────────────────────────────────────
    private VBoxContainer _cardList;
    private Label _splinterLabel;
    private Label _selectedCardLabel;
    private Label _gateLabel;
    private Control _beforeZone;
    private Control _afterZone;
    private CardUi _beforeCard;
    private CardUi _afterCard;

    // Stable reference to right panel content — never freed between refreshes
    private VBoxContainer _rightContent = null;

    // ── State ─────────────────────────────────────────────────────────
    private OwnedCard _selectedOwned = null;
    private bool _buildPending = false;

    public override void _Ready()
    {
        GD.Print("[UpgradeScreen] _Ready fired");
        _buildPending = true;
    }

    public override void _Process(double delta)
    {
        if (_buildPending)
        {
            _buildPending = false;
            SetProcess(false);
            GD.Print("[UpgradeScreen] BuildUI starting via _Process");
            BuildUI();
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Build
    // ═════════════════════════════════════════════════════════════════

    public void BuildUI()
    {
        try
        {
            GD.Print("[UpgradeScreen] BuildUI try block entered");

            var bg = new ColorRect { Color = UITheme.CampusBg };
            bg.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(bg);

            BuildTopBar();

            var body = new HBoxContainer();
            body.SetAnchorsPreset(LayoutPreset.FullRect);
            body.OffsetTop = 60;
            body.AddThemeConstantOverride("separation", 0);
            AddChild(body);

            // ── LEFT — card list ──────────────────────────────────────────
            var leftShell = new PanelContainer();
            leftShell.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            leftShell.SizeFlagsVertical = SizeFlags.ExpandFill;
            leftShell.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = UITheme.BgBase,
                BorderColor = UITheme.Violet,
                BorderWidthRight = 1,
            });
            body.AddChild(leftShell);

            var leftVBox = MakeVBox(0);
            leftVBox.SizeFlagsVertical = SizeFlags.ExpandFill;
            leftShell.AddChild(leftVBox);

            leftVBox.AddChild(BuildHeader("Your Cards", UITheme.Violet));

            var leftScroll = new ScrollContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            leftVBox.AddChild(leftScroll);
            _cardList = MakeVBox(5);
            MakeInnerMargin(leftScroll).AddChild(_cardList);

            // ── RIGHT — upgrade panel ─────────────────────────────────────
            var rightPanel = new PanelContainer();
            rightPanel.CustomMinimumSize = new Vector2(520, 0);
            rightPanel.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            rightPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
            rightPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = UITheme.BgDeep,
            });
            body.AddChild(rightPanel);

            var rightMargin = new MarginContainer();
            rightMargin.AddThemeConstantOverride("margin_left", 24);
            rightMargin.AddThemeConstantOverride("margin_right", 24);
            rightMargin.AddThemeConstantOverride("margin_top", 20);
            rightMargin.AddThemeConstantOverride("margin_bottom", 20);
            rightMargin.SizeFlagsVertical = SizeFlags.ExpandFill;
            rightPanel.AddChild(rightMargin);

            _rightContent = MakeVBox(16);
            _rightContent.SizeFlagsVertical = SizeFlags.ExpandFill;
            rightMargin.AddChild(_rightContent);

            // Permanent static nodes — never freed
            _selectedCardLabel = new Label
            {
                Text = "Select a card to upgrade",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _selectedCardLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
            _selectedCardLabel.AddThemeColorOverride("font_color", UITheme.Gold);
            _rightContent.AddChild(_selectedCardLabel);

            _gateLabel = new Label
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                Visible = false,
            };
            _gateLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            _gateLabel.AddThemeColorOverride("font_color", UITheme.Warning);
            _rightContent.AddChild(_gateLabel);

            // Before / After preview zones — permanent, content is swapped
            var previewRow = new HBoxContainer();
            previewRow.AddThemeConstantOverride("separation", 20);
            previewRow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            _rightContent.AddChild(previewRow);

            _beforeZone = BuildPreviewZone(previewRow, "Current");
            _afterZone = BuildPreviewZone(previewRow, "After Upgrade");

            if (PlayerSession.DebugMode)
            {
                var bypassBtn = new Button
                {
                    Text = "Toggle Cast Bypass [DEV]",
                    CustomMinimumSize = new Vector2(220, 36),
                    SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
                };
                bypassBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                UITheme.ApplyButtonStyle(bypassBtn, isPrimary: false);
                bypassBtn.Modulate = UITheme.DebugPanelBorder;
                bypassBtn.Pressed += () =>
                {
                    _bypassCastRequirement = !_bypassCastRequirement;
                    bypassBtn.Text = _bypassCastRequirement
                        ? "Cast Bypass ON [DEV]"
                        : "Toggle Cast Bypass [DEV]";
                    RefreshUpgradePanel(SaveManager.ActiveSave);
                };
                _rightContent.AddChild(bypassBtn);
            }

            Refresh();
            GD.Print("[UpgradeScreen] BuildUI completed successfully");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UpgradeScreen] BuildUI EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            GD.PrintErr(ex.StackTrace);
        }
    }

    private void BuildTopBar()
    {
        var bar = new Panel();
        bar.SetAnchorsPreset(LayoutPreset.TopWide);
        bar.OffsetBottom = 60;
        bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = UITheme.CampusTitleBarBg,
            BorderColor = UITheme.CampusTitleBarBorder,
            BorderWidthBottom = 2,
        });
        AddChild(bar);

        var back = new Button { Text = "← Back", CustomMinimumSize = new Vector2(90, 36) };
        back.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        back.SetAnchorsPreset(LayoutPreset.CenterLeft);
        back.OffsetLeft = 16; back.OffsetRight = 106;
        back.OffsetTop = -18; back.OffsetBottom = 18;
        UITheme.ApplyButtonStyle(back, isPrimary: false);
        back.Pressed += () => GetTree().ChangeSceneToFile(ReturnScenePath);
        bar.AddChild(back);

        var title = new Label
        {
            Text = "Upgrade Cards",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetAnchorsPreset(LayoutPreset.FullRect);
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        title.AddThemeColorOverride("font_color", UITheme.CampusTitleColor);
        bar.AddChild(title);

        _splinterLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _splinterLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        _splinterLabel.OffsetRight = -16;
        _splinterLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _splinterLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
        bar.AddChild(_splinterLabel);
    }

    private Control BuildPreviewZone(HBoxContainer parent, string label)
    {
        var col = MakeVBox(8);
        col.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        parent.AddChild(col);

        var hdr = new Label { Text = label, HorizontalAlignment = HorizontalAlignment.Center };
        hdr.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        hdr.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        col.AddChild(hdr);

        float scale = 0.78f;
        float w = UITheme.LibraryCardWidth * scale;
        float h = UITheme.LibraryCardHeight * scale;

        var zone = new Control
        {
            CustomMinimumSize = new Vector2(w, h),
            ClipContents = true,
        };
        var zoneBg = new ColorRect
        {
            Color = new Color(0.06f, 0.06f, 0.10f, 1f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        zoneBg.SetAnchorsPreset(LayoutPreset.FullRect);
        zone.AddChild(zoneBg);

        var hint = new Label
        {
            Text = "—",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "Hint",
        };
        hint.SetAnchorsPreset(LayoutPreset.FullRect);
        hint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        hint.AddThemeColorOverride("font_color", UITheme.TextDim);
        zone.AddChild(hint);

        col.AddChild(zone);
        return zone;
    }

    private PanelContainer BuildHeader(string title, Color accent)
    {
        var hdr = new PanelContainer();
        hdr.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(accent.R, accent.G, accent.B, 0.10f),
            BorderColor = accent,
            BorderWidthBottom = 1,
        });
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 14); m.AddThemeConstantOverride("margin_right", 14);
        m.AddThemeConstantOverride("margin_top", 8); m.AddThemeConstantOverride("margin_bottom", 8);
        hdr.AddChild(m);
        var lbl = new Label { Text = title };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        lbl.AddThemeColorOverride("font_color", UITheme.CampusTitleColor);
        m.AddChild(lbl);
        return hdr;
    }

    // ═════════════════════════════════════════════════════════════════
    // Refresh
    // ═════════════════════════════════════════════════════════════════

    private void Refresh()
    {
        var save = SaveManager.ActiveSave;
        if (_splinterLabel != null)
            _splinterLabel.Text = save != null ? $"✦ {save.ArcaneSplinters} Splinters" : "";

        RefreshCardList(save);
        RefreshUpgradePanel(save);
    }

    private void RefreshSelectionHighlight()
    {
        var save = SaveManager.ActiveSave;
        RefreshCardList(save);
    }

    private void RefreshCardList(GuildSaveData save)
    {
        foreach (Node n in _cardList.GetChildren()) n.QueueFree();

        if (save?.PlayerDeck?.Cards == null)
        {
            _cardList.AddChild(MakeStub("No save loaded."));
            return;
        }

        var grouped = new Dictionary<string, List<OwnedCard>>();
        foreach (var c in save.PlayerDeck.Cards)
        {
            if (!grouped.ContainsKey(c.BlueprintId)) grouped[c.BlueprintId] = new List<OwnedCard>();
            grouped[c.BlueprintId].Add(c);
        }

        bool any = false;
        foreach (var kvp in grouped.OrderBy(k => k.Key))
        {
            var copies = kvp.Value.OrderBy(c => c.PointsSpent).ToList();
            var display = copies[0];
            var bp = CardDatabase.Blueprints.Find(b =>
                string.Equals(b.Id, display.BlueprintId, StringComparison.OrdinalIgnoreCase));

            string displayName = CardDatabase.GetDisplayName(bp, display);
            bool maxed = display.IsMaxed;
            bool isSelected = _selectedOwned?.InstanceId == display.InstanceId;

            var row = new PanelContainer();
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.CustomMinimumSize = new Vector2(0, 52);
            row.MouseFilter = MouseFilterEnum.Stop;

            var normalStyle = new StyleBoxFlat
            {
                BgColor = isSelected
                    ? new Color(UITheme.Violet.R, UITheme.Violet.G, UITheme.Violet.B, 0.18f)
                    : UITheme.SurfaceLight,
                BorderColor = isSelected ? UITheme.Violet
                                : maxed ? UITheme.Gold
                                : UITheme.RarityColor(bp?.Rarity.ToString() ?? "Common"),
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
            row.AddThemeStyleboxOverride("panel", normalStyle);

            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);
            hbox.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(hbox);

            var info = MakeVBox(2);
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            info.MouseFilter = MouseFilterEnum.Ignore;
            hbox.AddChild(info);

            var nameLbl = new Label { Text = displayName };
            nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            nameLbl.AddThemeColorOverride("font_color",
                UITheme.RarityColor(bp?.Rarity.ToString() ?? "Common"));
            nameLbl.MouseFilter = MouseFilterEnum.Ignore;
            info.AddChild(nameLbl);

            string tierText = maxed
                ? $"{TierLabel(display.TopTier)}/{TierLabel(display.BotTier)}  ★ MAX"
                : $"Top: {TierLabel(display.TopTier)}  Bot: {TierLabel(display.BotTier)}" +
                  $"  ({display.PointsRemaining} pts remaining)";
            var tierLbl = new Label { Text = tierText };
            tierLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            tierLbl.AddThemeColorOverride("font_color",
                maxed ? UITheme.Gold : UITheme.TextSecondary);
            tierLbl.MouseFilter = MouseFilterEnum.Ignore;
            info.AddChild(tierLbl);

            if (copies.Count > 1)
            {
                var countBadge = new Label { Text = $"×{copies.Count}" };
                countBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                countBadge.AddThemeColorOverride("font_color", UITheme.TextDim);
                countBadge.MouseFilter = MouseFilterEnum.Ignore;
                hbox.AddChild(countBadge);
            }

            var capturedOwned = display;
            row.GuiInput += (e) =>
            {
                if (e is InputEventMouseButton mb && !mb.Pressed &&
                    mb.ButtonIndex == MouseButton.Left)
                {
                    _selectedOwned = capturedOwned;
                    RefreshSelectionHighlight();
                    RefreshUpgradePanel(SaveManager.ActiveSave);
                    if (_splinterLabel != null)
                        _splinterLabel.Text = $"✦ {SaveManager.ActiveSave?.ArcaneSplinters ?? 0} Splinters";
                }
            };

            _cardList.AddChild(row);
            any = true;
        }

        if (!any) _cardList.AddChild(MakeStub("No cards owned yet."));
    }

    private void RefreshUpgradePanel(GuildSaveData save)
    {
        if (_rightContent == null) return;

        // Update splinter label
        if (_splinterLabel != null && save != null)
            _splinterLabel.Text = $"✦ {save.ArcaneSplinters} Splinters";

        if (_selectedOwned == null)
        {
            SetPreviewEmpty();
            if (_selectedCardLabel != null)
                _selectedCardLabel.Text = "Select a card to upgrade";
            ClearDynamicContent();
            return;
        }

        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, _selectedOwned.BlueprintId,
                StringComparison.OrdinalIgnoreCase));

        if (_selectedCardLabel != null)
            _selectedCardLabel.Text = CardDatabase.GetDisplayName(bp, _selectedOwned);

        // Scriptorum gate
        int maxUpgradeStage = 0;
        if (PlayerSession.HasFeature("card_upgrade_stage_3")) maxUpgradeStage = 3;
        else if (PlayerSession.HasFeature("card_upgrade_stage_2")) maxUpgradeStage = 2;
        else if (PlayerSession.HasFeature("card_upgrade_stage_1")) maxUpgradeStage = 1;

        bool gated = maxUpgradeStage == 0;

        if (_gateLabel != null)
        {
            _gateLabel.Visible = gated;
            _gateLabel.Text = "Requires a Scriptorum to refine your spells.";
        }

        // Clear only the dynamic section — permanent nodes (_selectedCardLabel,
        // _gateLabel, preview zones, bypass button) are preserved
        ClearDynamicContent();

        bool isGeneric = bp?.School == CardSchool.Generic;
        bool baseUpgraded = _selectedOwned.IsBaseUpgraded;
        int pointsSpent = _selectedOwned.PointsSpent;
        int splinters = save?.ArcaneSplinters ?? 0;

        // ── Shared 1/1 upgrade ───────────────────────────────────────────
        if (!baseUpgraded)
        {
            bool castOk = _bypassCastRequirement ||
                CardMasteryThresholds.CanSpendNextPoint(
                    _selectedOwned.CastCount, pointsSpent);
            int castsNeeded = CardMasteryThresholds.CastsUntilNextPoint(
                _selectedOwned.CastCount, pointsSpent);
            bool canAfford = splinters >= CardUpgradeCosts.SharedUpgradeCost;

            string desc = CardUpgradeApplier.GetSharedUpgradeDescription(
                _selectedOwned.BlueprintId);

            AddDynamic(MakeDescLabel(string.IsNullOrEmpty(desc)
                ? "Refine both halves of this card."
                : desc));

            AddDynamic(MakeCostLabel(
                CardUpgradeCosts.SharedUpgradeCost, castOk,
                castsNeeded, canAfford, _bypassCastRequirement,
                _selectedOwned.CastCount));

            AddDynamic(MakeUpgradeButton(
                "Refine Both Halves →",
                !canAfford || gated || !castOk,
                () => OnSharedUpgradePressed(save)));

            ShowPreview(_selectedOwned.BlueprintId, 0, 0, 1, 1);
            return;
        }

        // Generic cards stop at 1/1
        if (isGeneric)
        {
            AddDynamic(MakeDescLabel("Generic cards cannot be further refined."));
            ShowPreview(_selectedOwned.BlueprintId,
                _selectedOwned.TopTier, _selectedOwned.BotTier, -1, -1);
            return;
        }

        // ── Independent half upgrade tracks ──────────────────────────────
        int pointsRemaining = _selectedOwned.PointsRemaining;

        var tracksRow = new HBoxContainer();
        tracksRow.AddThemeConstantOverride("separation", 16);
        tracksRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddDynamic(tracksRow);

        tracksRow.AddChild(BuildHalfTrack(
            save, bp, isTop: true, pointsRemaining, maxUpgradeStage, gated));

        var div = new ColorRect
        {
            Color = new Color(1, 1, 1, 0.08f),
            CustomMinimumSize = new Vector2(1, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        tracksRow.AddChild(div);

        tracksRow.AddChild(BuildHalfTrack(
            save, bp, isTop: false, pointsRemaining, maxUpgradeStage, gated));

        var pointsLabel = new Label
        {
            Text = $"Upgrade points remaining: {pointsRemaining} / 5",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        pointsLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        pointsLabel.AddThemeColorOverride("font_color",
            pointsRemaining > 0 ? UITheme.TextSecondary : UITheme.Danger);
        AddDynamic(pointsLabel);

        ShowPreview(_selectedOwned.BlueprintId,
            _selectedOwned.TopTier, _selectedOwned.BotTier, -1, -1);
    }

    // Tracks which children are dynamic so we can clear only them
    private readonly List<Node> _dynamicNodes = new();

    private void AddDynamic(Node node)
    {
        _rightContent.AddChild(node);
        _dynamicNodes.Add(node);
    }

    private void ClearDynamicContent()
    {
        foreach (var node in _dynamicNodes)
        {
            if (node != null && IsInstanceValid(node))
                node.QueueFree();
        }
        _dynamicNodes.Clear();
    }

    private Control BuildHalfTrack(GuildSaveData save, CardBlueprint bp,
        bool isTop, int pointsRemaining, int maxUpgradeStage, bool gated)
    {
        var col = new VBoxContainer();
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddThemeConstantOverride("separation", 8);

        int currentTier = isTop ? _selectedOwned.TopTier : _selectedOwned.BotTier;
        int nextTier = currentTier + 1;
        bool maxed = currentTier >= 4;
        bool noPoints = pointsRemaining <= 0;
        bool stageLocked = nextTier > maxUpgradeStage;

        var halfLabel = new Label
        {
            Text = isTop ? "▲ Top Half" : "▼ Bottom Half",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        halfLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        halfLabel.AddThemeColorOverride("font_color",
            isTop ? UITheme.ElementFire : UITheme.ElementIce);
        col.AddChild(halfLabel);

        var tierLabel = new Label
        {
            Text = TierLabel(currentTier),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tierLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        tierLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        col.AddChild(tierLabel);

        float previewScale = 0.55f;
        float cw = UITheme.LibraryCardWidth * previewScale;
        float ch = UITheme.LibraryCardHeight * previewScale;

        var previewCard = CardUpgradeApplier.Apply(
            _selectedOwned.BlueprintId, _selectedOwned.TopTier, _selectedOwned.BotTier);
        if (previewCard != null && CardUIScene != null)
        {
            var wrapper = new Control
            {
                CustomMinimumSize = new Vector2(cw, ch),
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
                ClipContents = true,
            };
            col.AddChild(wrapper);
            var cardUi = CardUIScene.Instantiate<CardUi>();
            wrapper.AddChild(cardUi);
            cardUi.SetCard(previewCard.TopHalf, previewCard.BottomHalf);
            cardUi.OffsetRight = UITheme.LibraryCardWidth;
            cardUi.OffsetBottom = UITheme.LibraryCardHeight;
            cardUi.Scale = new Vector2(previewScale, previewScale);
            cardUi.PivotOffset = Vector2.Zero;
            cardUi.Position = Vector2.Zero;
            cardUi.Rotation = 0f;
            cardUi.SetProcess(false);
            DisableMouseRecursive(cardUi);
            var capturedUi = cardUi;
            bool capturedIsTop = isTop;
            GetTree().CreateTimer(0.0).Timeout += () =>
            {
                if (IsInstanceValid(capturedUi))
                {
                    capturedUi.SetStaticDisplay(previewScale);
                    capturedUi.SetHalfHighlight(capturedIsTop, !capturedIsTop);
                }
            };
        }

        if (maxed)
        {
            var maxLabel = new Label
            {
                Text = "★ Transcendent",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            maxLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            maxLabel.AddThemeColorOverride("font_color", UITheme.Gold);
            col.AddChild(maxLabel);
            return col;
        }

        string desc = isTop
            ? CardUpgradeApplier.GetTopUpgradeDescription(
                _selectedOwned.BlueprintId, nextTier)
            : CardUpgradeApplier.GetBotUpgradeDescription(
                _selectedOwned.BlueprintId, nextTier);

        if (!string.IsNullOrEmpty(desc))
        {
            var descLabel = new Label
            {
                Text = desc,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            descLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            descLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            col.AddChild(descLabel);
        }

        int cost = CardUpgradeCosts.HalfTierCost[Mathf.Min(nextTier,
            CardUpgradeCosts.HalfTierCost.Length - 1)];
        bool castOk = _bypassCastRequirement ||
            CardMasteryThresholds.CanSpendNextPoint(
                _selectedOwned.CastCount, _selectedOwned.PointsSpent);
        int castsNeeded = CardMasteryThresholds.CastsUntilNextPoint(
            _selectedOwned.CastCount, _selectedOwned.PointsSpent);
        bool canAfford = (save?.ArcaneSplinters ?? 0) >= cost;

        col.AddChild(MakeCostLabel(cost, castOk, castsNeeded,
            canAfford, _bypassCastRequirement, _selectedOwned.CastCount));

        bool disabled = !canAfford || gated || stageLocked || !castOk || noPoints;
        string btnText = noPoints
            ? "No points remaining"
            : stageLocked
                ? $"Requires Scriptorum Tier {nextTier}"
                : $"Upgrade {(isTop ? "Top" : "Bottom")} → {TierLabel(nextTier)}";

        col.AddChild(MakeUpgradeButton(btnText, disabled, () =>
            OnHalfUpgradePressed(save, isTop)));

        return col;
    }

    // ═════════════════════════════════════════════════════════════════
    // Upgrade actions
    // ═════════════════════════════════════════════════════════════════

    private void OnSharedUpgradePressed(GuildSaveData save)
    {
        if (save == null || _selectedOwned == null) return;
        if (_selectedOwned.IsBaseUpgraded) return;

        int cost = CardUpgradeCosts.SharedUpgradeCost;
        if (save.ArcaneSplinters < cost) return;

        save.ArcaneSplinters -= cost;
        _selectedOwned.TopTier = 1;
        _selectedOwned.BotTier = 1;
        _selectedOwned.PointsSpent = 1;

        SaveManager.Save();
        GD.Print($"[CardUpgrade] Shared upgrade: '{_selectedOwned.BlueprintId}' → 1/1");
        Refresh();
    }

    private void OnHalfUpgradePressed(GuildSaveData save, bool isTop)
    {
        if (save == null || _selectedOwned == null) return;
        if (!_selectedOwned.IsBaseUpgraded) return;
        if (_selectedOwned.PointsRemaining <= 0) return;

        int currentTier = isTop ? _selectedOwned.TopTier : _selectedOwned.BotTier;
        int nextTier = currentTier + 1;
        if (nextTier > 4) return;

        int cost = CardUpgradeCosts.HalfTierCost[
            Mathf.Min(nextTier, CardUpgradeCosts.HalfTierCost.Length - 1)];
        if (save.ArcaneSplinters < cost) return;

        save.ArcaneSplinters -= cost;

        if (isTop) _selectedOwned.TopTier = nextTier;
        else _selectedOwned.BotTier = nextTier;
        _selectedOwned.PointsSpent++;

        SaveManager.Save();
        GD.Print($"[CardUpgrade] {(isTop ? "Top" : "Bot")} upgraded: " +
                 $"'{_selectedOwned.BlueprintId}' → {_selectedOwned.TopTier}/{_selectedOwned.BotTier}");
        Refresh();
    }

    // ─────────────────────────────────────────────────────────────────
    // Tier label
    // ─────────────────────────────────────────────────────────────────

    private static string TierLabel(int tier) => tier switch
    {
        0 => "Base",
        1 => "Refined",
        2 => "Specialized",
        3 => "Mastered",
        4 => "Transcendent",
        _ => $"Tier {tier}"
    };

    // ═════════════════════════════════════════════════════════════════
    // Preview
    // ═════════════════════════════════════════════════════════════════

    private void ShowPreview(string blueprintId,
        int currentTopTier, int currentBotTier,
        int nextTopTier, int nextBotTier)
    {
        ShowCardInZone(_beforeZone, ref _beforeCard,
            blueprintId, currentTopTier, currentBotTier);

        if (nextTopTier >= 0 && nextBotTier >= 0)
            ShowCardInZone(_afterZone, ref _afterCard,
                blueprintId, nextTopTier, nextBotTier);
        else
            ClearZone(_afterZone, ref _afterCard, "★ MAX");
    }

    private void ShowCardInZone(Control zone, ref CardUi cardUi,
        string blueprintId, int topTier, int botTier)
    {
        cardUi?.QueueFree();
        cardUi = null;

        var hint = zone.GetNodeOrNull<Label>("Hint");
        if (hint != null) hint.Visible = false;

        if (CardUIScene == null) return;

        var card = CardUpgradeApplier.Apply(blueprintId, topTier, botTier);
        if (card == null) return;

        var newCardUi = CardUIScene.Instantiate<CardUi>();
        zone.AddChild(newCardUi);
        newCardUi.SetCard(card.TopHalf, card.BottomHalf);
        cardUi = newCardUi;
        cardUi.SetProcess(false);
        DisableMouseRecursive(cardUi);

        var capturedCard = newCardUi;
        int capturedTop = topTier;
        int capturedBot = botTier;
        GetTree().CreateTimer(0.0).Timeout += () =>
        {
            if (IsInstanceValid(capturedCard))
            {
                capturedCard.SetStaticDisplay(0.78f);
                bool topIsHigher = capturedTop > capturedBot;
                bool botIsHigher = capturedBot > capturedTop;
                capturedCard.SetHalfHighlight(topIsHigher, botIsHigher);
            }
        };
    }

    private void ClearZone(Control zone, ref CardUi cardUi, string hintText)
    {
        if (cardUi != null && IsInstanceValid(cardUi))
        {
            cardUi.QueueFree();
            cardUi = null;
        }
        var hint = zone.GetNodeOrNull<Label>("Hint");
        if (hint != null) { hint.Text = hintText; hint.Visible = true; }
    }

    private void SetPreviewEmpty()
    {
        ClearZone(_beforeZone, ref _beforeCard, "—");
        ClearZone(_afterZone, ref _afterCard, "—");
    }

    // ═════════════════════════════════════════════════════════════════
    // Small UI helpers
    // ═════════════════════════════════════════════════════════════════

    private Label MakeDescLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        label.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        return label;
    }

    private Label MakeCostLabel(int cost, bool castOk, int castsNeeded,
        bool canAfford, bool bypass, int castCount)
    {
        string castStatus = bypass
            ? "✓ [DEV] bypass"
            : castOk
                ? $"✓ {castCount} casts"
                : $"{castsNeeded} more casts needed";

        var label = new Label
        {
            Text = castOk
                ? $"Cost: {cost} ✦  ({castStatus})"
                : castStatus,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        label.AddThemeColorOverride("font_color",
            castOk && canAfford ? new Color(0.6f, 0.85f, 1f) : UITheme.Danger);
        return label;
    }

    private Button MakeUpgradeButton(string text, bool disabled, Action onPress)
    {
        var btn = new Button
        {
            Text = text,
            Disabled = disabled,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(180, 36),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: !disabled);
        if (!disabled) btn.Pressed += onPress;
        return btn;
    }

    private MarginContainer MakeInnerMargin(ScrollContainer s)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 12); m.AddThemeConstantOverride("margin_right", 12);
        m.AddThemeConstantOverride("margin_top", 8); m.AddThemeConstantOverride("margin_bottom", 8);
        m.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        m.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        s.AddChild(m);
        return m;
    }

    private VBoxContainer MakeVBox(int sep)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return v;
    }

    private Label MakeStub(string t)
    {
        var l = new Label
        {
            Text = t,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusStubFontSize);
        l.Modulate = UITheme.CampusStubText;
        return l;
    }

    private static void DisableMouseRecursive(Control root)
    {
        root.MouseFilter = MouseFilterEnum.Ignore;
        foreach (var child in root.GetChildren())
            if (child is Control c)
                DisableMouseRecursive(c);
    }
}
