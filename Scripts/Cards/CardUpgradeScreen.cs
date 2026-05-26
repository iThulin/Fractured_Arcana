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
//                 bumps OwnedCard.UpgradeTier in the save.
//                 Gated by Training Grounds tier.
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

    // Max upgrade tier
    private const int MAX_TIER = 4;
    private bool _bypassCastRequirement = false; // DEV flag to bypass cast count requirement for testing

    // Upgrade costs per tier (tunable in JSON eventually)
    // Tier 1: ~5 battles, Tier 2: ~10, Tier 3: ~20
    private static readonly int[] TierCosts = { 0, 25, 60, 120, 250 };

    // Tier labels
    private static readonly string[] TierLabels =
        { "Base", "Refined", "Specialized", "Mastered", "Transcendent" };

    // Upgrade branches
    private List<(string branch, string description)> _availableBranches = new();
    private string _pendingBranch = null; // chosen but not yet confirmed
    private Control _branchSelectionPanel = null;

    // ── Layout ────────────────────────────────────────────────────────
    private VBoxContainer _cardList;
    private Label _splinterLabel;
    private Label _selectedCardLabel;
    private Label _upgradeDescLabel;
    private Control _beforeZone;
    private Control _afterZone;
    private CardUi _beforeCard;
    private CardUi _afterCard;
    private Button _upgradeButton;
    private Label _upgradeCostLabel;
    private Label _gateLabel;

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

            // ── Body: list (left) + preview panel (right) ─────────────────
            var body = new HBoxContainer();
            body.SetAnchorsPreset(LayoutPreset.FullRect);
            body.OffsetTop = 60;
            body.AddThemeConstantOverride("separation", 0);
            AddChild(body);

            // LEFT — card list
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

            // List header
            var listHeader = BuildHeader("Your Cards", UITheme.Violet);
            leftVBox.AddChild(listHeader);

            var leftScroll = new ScrollContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            leftVBox.AddChild(leftScroll);
            _cardList = MakeVBox(5);
            MakeInnerMargin(leftScroll).AddChild(_cardList);

            // RIGHT — upgrade panel
            var rightPanel = new PanelContainer();
            rightPanel.CustomMinimumSize = new Vector2(520, 0);
            rightPanel.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            rightPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
            rightPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = UITheme.BgDeep,
            });
            body.AddChild(rightPanel);

            var rightVBox = MakeVBox(16);
            rightVBox.SizeFlagsVertical = SizeFlags.ExpandFill;
            rightPanel.AddChild(rightVBox);

            var rightMargin = new MarginContainer();
            rightMargin.AddThemeConstantOverride("margin_left", 24);
            rightMargin.AddThemeConstantOverride("margin_right", 24);
            rightMargin.AddThemeConstantOverride("margin_top", 20);
            rightMargin.AddThemeConstantOverride("margin_bottom", 20);
            rightMargin.SizeFlagsVertical = SizeFlags.ExpandFill;
            rightPanel.AddChild(rightMargin);

            var rightContent = MakeVBox(16);
            rightContent.SizeFlagsVertical = SizeFlags.ExpandFill;
            rightMargin.AddChild(rightContent);

            // Selected card name
            _selectedCardLabel = new Label
            {
                Text = "Select a card to upgrade",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _selectedCardLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
            _selectedCardLabel.AddThemeColorOverride("font_color", UITheme.Gold);
            rightContent.AddChild(_selectedCardLabel);

            // Gate label (shown when Training Grounds insufficient)
            _gateLabel = new Label
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                Visible = false,
            };
            _gateLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            _gateLabel.AddThemeColorOverride("font_color", UITheme.Warning);
            rightContent.AddChild(_gateLabel);

            // Before / After card previews side by side
            var previewRow = new HBoxContainer();
            previewRow.AddThemeConstantOverride("separation", 20);
            previewRow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            rightContent.AddChild(previewRow);

            _beforeZone = BuildPreviewZone(previewRow, "Current");
            _afterZone = BuildPreviewZone(previewRow, "After Upgrade");

            // Upgrade description
            _upgradeDescLabel = new Label
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            _upgradeDescLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            _upgradeDescLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            rightContent.AddChild(_upgradeDescLabel);

            // Cost label
            _upgradeCostLabel = new Label
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _upgradeCostLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            _upgradeCostLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
            rightContent.AddChild(_upgradeCostLabel);

            // Upgrade button
            _upgradeButton = new Button
            {
                Text = "Upgrade",
                CustomMinimumSize = new Vector2(220, 50),
                Disabled = true,
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            };
            _upgradeButton.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            UITheme.ApplyButtonStyle(_upgradeButton, isPrimary: true);
            _upgradeButton.Pressed += OnUpgradePressed;
            rightContent.AddChild(_upgradeButton);

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
                rightContent.AddChild(bypassBtn);
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

    private void BuildBranchSelectionPanel(VBoxContainer parent)
    {
        // Remove existing panel if present
        _branchSelectionPanel?.QueueFree();
        _branchSelectionPanel = null;

        if (_selectedOwned == null) return;

        int nextTier = _selectedOwned.UpgradeTier + 1;
        _availableBranches = CardUpgradeApplier.GetBranchOptions(
            _selectedOwned.BlueprintId, nextTier);

        // If only one branch or no branching, nothing to show
        if (_availableBranches.Count <= 1) return;

        var panel = new PanelContainer();
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(UITheme.Violet.R, UITheme.Violet.G, UITheme.Violet.B, 0.08f),
            BorderColor = UITheme.Violet,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 12,
            ContentMarginBottom = 12,
        });
        _branchSelectionPanel = panel;
        parent.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        var header = new Label
        {
            Text = "Choose a specialization path:",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        header.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        header.AddThemeColorOverride("font_color", UITheme.Violet);
        vbox.AddChild(header);

        var branchRow = new HBoxContainer();
        branchRow.AddThemeConstantOverride("separation", 12);
        branchRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(branchRow);

        foreach (var (branch, description) in _availableBranches)
        {
            var branchCard = BuildBranchOptionCard(branch, description);
            branchRow.AddChild(branchCard);
        }
    }

    private Control BuildBranchOptionCard(string branch, string description)
    {
        bool isSelected = _pendingBranch == branch;

        var panel = new PanelContainer();
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel.MouseFilter = MouseFilterEnum.Stop;
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = isSelected
                ? new Color(UITheme.Violet.R, UITheme.Violet.G, UITheme.Violet.B, 0.25f)
                : UITheme.BgRaised,
            BorderColor = isSelected ? UITheme.Violet : UITheme.Neutral,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            CornerRadiusTopLeft = UITheme.CornerRadius,
            CornerRadiusTopRight = UITheme.CornerRadius,
            CornerRadiusBottomLeft = UITheme.CornerRadius,
            CornerRadiusBottomRight = UITheme.CornerRadius,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        });

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        vbox.MouseFilter = MouseFilterEnum.Ignore;
        panel.AddChild(vbox);

        // Branch name
        var nameLabel = new Label
        {
            Text = branch,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLabel.AddThemeColorOverride("font_color",
            isSelected ? UITheme.Violet : UITheme.TextPrimary);
        vbox.AddChild(nameLabel);

        // Preview of what the branch does — show the upgraded card halves
        var previewZone = new Control
        {
            CustomMinimumSize = new Vector2(UITheme.LibraryCardWidth * 0.65f,
                                            UITheme.LibraryCardHeight * 0.65f),
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        var zoneBg = new ColorRect
        {
            Color = new Color(0.06f, 0.06f, 0.10f, 1f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        zoneBg.SetAnchorsPreset(LayoutPreset.FullRect);
        previewZone.AddChild(zoneBg);
        vbox.AddChild(previewZone);

        // Spawn a card preview for this branch
        if (CardUIScene != null)
        {
            int nextTier = _selectedOwned.UpgradeTier + 1;
            var branchCard = CardUpgradeApplier.Apply(
                _selectedOwned.BlueprintId, nextTier, branch);
            if (branchCard != null)
            {
                var cardUi = CardUIScene.Instantiate<CardUi>();
                previewZone.AddChild(cardUi);
                cardUi.SetCard(branchCard.TopHalf, branchCard.BottomHalf);
                var capturedCard = cardUi;
                GetTree().CreateTimer(0.0).Timeout += () =>
                {
                    if (IsInstanceValid(capturedCard))
                        capturedCard.SetStaticDisplay(0.65f);
                };
            }
        }

        // Description
        var descLabel = new Label
        {
            Text = description,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        descLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        descLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        vbox.AddChild(descLabel);

        // Select button
        var selectBtn = new Button
        {
            Text = isSelected ? "✓ Selected" : "Choose",
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(100, 32),
        };
        selectBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(selectBtn, isPrimary: !isSelected);

        string capturedBranch = branch;
        selectBtn.Pressed += () =>
        {
            _pendingBranch = capturedBranch;
            // Rebuild branch panel to update selection state
            var save = SaveManager.ActiveSave;
            RefreshUpgradePanel(save);
        };
        vbox.AddChild(selectBtn);

        return panel;
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
        // Walk existing rows and update their border color based on _selectedOwned
        foreach (Node child in _cardList.GetChildren())
        {
            if (child is not PanelContainer row) continue;
            // We can't easily identify which row maps to which card without
            // storing the mapping, so just rebuild the list minimally
        }
        // Simplest correct approach: rebuild the list but DON'T touch the preview zones
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

        // Group by blueprint, deduplicated display
        var grouped = new Dictionary<string, List<OwnedCard>>();
        foreach (var c in save.PlayerDeck.Cards)
        {
            if (!grouped.ContainsKey(c.BlueprintId)) grouped[c.BlueprintId] = new List<OwnedCard>();
            grouped[c.BlueprintId].Add(c);
        }

        bool any = false;
        foreach (var kvp in grouped.OrderBy(k => k.Key))
        {
            // Show the copy with the lowest tier first (upgrade that one)
            var copies = kvp.Value.OrderBy(c => c.UpgradeTier).ToList();
            var display = copies[0]; // lowest-tier copy to upgrade
            var bp = CardDatabase.Blueprints.Find(b =>
                string.Equals(b.Id, display.BlueprintId, StringComparison.OrdinalIgnoreCase));

            string displayName = CardDatabase.GetDisplayName(bp, display);
            int curTier = display.UpgradeTier;
            bool maxed = curTier >= MAX_TIER;

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
                ? $"{TierLabels[curTier]}  ★ MAX"
                : $"{TierLabels[curTier]}  →  {TierLabels[curTier + 1]}  " +
                  $"({TierCosts[curTier + 1]} ✦)";
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
                    _pendingBranch = null;
                    // Don't call Refresh() — only update the right panel and highlight
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
        GD.Print($"[UpgradeScreen] RefreshUpgradePanel _selectedOwned null={_selectedOwned == null}");
        if (_selectedOwned == null)
        {
            SetPreviewEmpty();
            _branchSelectionPanel?.QueueFree();
            _branchSelectionPanel = null;
            if (_upgradeButton != null) _upgradeButton.Disabled = true;
            if (_upgradeCostLabel != null) _upgradeCostLabel.Text = "";
            if (_upgradeDescLabel != null) _upgradeDescLabel.Text = "";
            if (_selectedCardLabel != null)
                _selectedCardLabel.Text = "Select a card to upgrade";
            return;
        }

        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, _selectedOwned.BlueprintId,
                StringComparison.OrdinalIgnoreCase));

        string displayName = CardDatabase.GetDisplayName(bp, _selectedOwned);
        int curTier = _selectedOwned.UpgradeTier;
        int nextTier = curTier + 1;
        bool maxed = curTier >= MAX_TIER;

        if (_selectedCardLabel != null)
            _selectedCardLabel.Text = displayName;

        // Scriptorum gate
        int maxUpgradeStage = 0;
        if (PlayerSession.HasFeature("card_upgrade_stage_3")) maxUpgradeStage = 3;
        else if (PlayerSession.HasFeature("card_upgrade_stage_2")) maxUpgradeStage = 2;
        else if (PlayerSession.HasFeature("card_upgrade_stage_1")) maxUpgradeStage = 1;

        bool gated = maxUpgradeStage == 0;
        bool stageLocked = !maxed && nextTier > maxUpgradeStage;

        if (_gateLabel != null)
        {
            _gateLabel.Visible = gated || stageLocked;
            _gateLabel.Text = gated
                ? "Requires a Scriptorum to refine your spells."
                : $"Requires Scriptorum (Tier {nextTier}) to unlock {TierLabels[nextTier]} refinements.";
        }

        if (maxed)
        {
            _branchSelectionPanel?.QueueFree();
            _branchSelectionPanel = null;
            if (_upgradeDescLabel != null) _upgradeDescLabel.Text = "This card is fully mastered.";
            if (_upgradeCostLabel != null) _upgradeCostLabel.Text = "";
            if (_upgradeButton != null) { _upgradeButton.Text = "Already Mastered"; _upgradeButton.Disabled = true; }
            ShowPreview(_selectedOwned.BlueprintId, curTier, -1, _selectedOwned.ChosenBranch);
            return;
        }

        bool castUnlocked = _bypassCastRequirement ||
            CardMasteryThresholds.IsStageUnlocked(_selectedOwned.CastCount, nextTier);
        int castsNeeded = CardMasteryThresholds.CastsToNextStage(
            _selectedOwned.CastCount, curTier);

        int cost = TierCosts[nextTier];
        int splinters = save?.ArcaneSplinters ?? 0;
        bool canAfford = splinters >= cost;

        // ── Branch selection ──────────────────────────────────────────────────
        // Always rebuild to stay in sync with current selection state
        _branchSelectionPanel?.QueueFree();
        _branchSelectionPanel = null;

        bool isBranchingTier = false;
        if (!gated && !stageLocked)
        {
            var branches = CardUpgradeApplier.GetBranchOptions(
                _selectedOwned.BlueprintId, nextTier);
            isBranchingTier = branches.Count > 1;

            if (isBranchingTier)
            {
                // Find rightContent — the parent VBoxContainer of the upgrade button
                var rightContent = _upgradeButton?.GetParent() as VBoxContainer;
                if (rightContent != null)
                    BuildBranchSelectionPanel(rightContent);
            }
        }

        bool branchRequired = isBranchingTier && string.IsNullOrEmpty(_pendingBranch);

        // ── Upgrade description ───────────────────────────────────────────────
        // Use pending branch for description when at a branching tier
        string activeBranch = isBranchingTier
            ? _pendingBranch
            : _selectedOwned.ChosenBranch;

        string desc = CardUpgradeApplier.GetUpgradeDescription(
            _selectedOwned.BlueprintId, nextTier, activeBranch);

        if (_upgradeDescLabel != null)
            _upgradeDescLabel.Text = branchRequired
                ? "Choose a specialization path above."
                : string.IsNullOrEmpty(desc)
                    ? $"Upgrade to {TierLabels[nextTier]}"
                    : desc;

        // ── Cost label ────────────────────────────────────────────────────────
        if (_upgradeCostLabel != null)
        {
            string castStatus = _bypassCastRequirement
                ? "✓ [DEV] cast bypass"
                : castUnlocked
                    ? $"✓ {_selectedOwned.CastCount} casts"
                    : $"{castsNeeded} more casts needed";

            _upgradeCostLabel.Text = castUnlocked
                ? $"Cost: {cost} ✦  ({castStatus})"
                : castStatus;
        }

        _upgradeCostLabel?.AddThemeColorOverride("font_color",
            castUnlocked && canAfford ? new Color(0.6f, 0.85f, 1f) : UITheme.Danger);

        // ── Upgrade button ────────────────────────────────────────────────────
        if (_upgradeButton != null)
        {
            _upgradeButton.Text = branchRequired
                ? "Choose a path first"
                : $"Upgrade → {TierLabels[nextTier]}";
            _upgradeButton.Disabled = !canAfford || gated || stageLocked
                || !castUnlocked || branchRequired;
        }

        // ── Preview ───────────────────────────────────────────────────────────
        // After panel uses the active branch so preview reflects selection
        ShowPreview(_selectedOwned.BlueprintId, curTier, nextTier, activeBranch);
    }

    // ═════════════════════════════════════════════════════════════════
    // Preview
    // ═════════════════════════════════════════════════════════════════

    private void ShowPreview(string blueprintId, int currentTier, int nextTier,
        string branch = null)
    {
        ShowCardInZone(_beforeZone, ref _beforeCard, blueprintId,
            currentTier, _selectedOwned?.ChosenBranch);
        if (nextTier >= 0)
            ShowCardInZone(_afterZone, ref _afterCard, blueprintId, nextTier, branch);
        else
            ClearZone(_afterZone, ref _afterCard, "★ MAX");
    }

    private void ShowCardInZone(Control zone, ref CardUi cardUi,
       string blueprintId, int tier, string branch = null)
    {
        if (cardUi != null && IsInstanceValid(cardUi))
        {
            cardUi.QueueFree();
            cardUi = null;
        }

        var hint = zone.GetNodeOrNull<Label>("Hint");
        if (hint != null) hint.Visible = false;

        if (CardUIScene == null) return;

        var card = CardUpgradeApplier.Apply(blueprintId, tier, branch);
        if (card == null) return;

        var newCardUi = CardUIScene.Instantiate<CardUi>();
        zone.AddChild(newCardUi);
        newCardUi.SetCard(card.TopHalf, card.BottomHalf);
        cardUi = newCardUi;

        var capturedCard = newCardUi;
        GetTree().CreateTimer(0.0).Timeout += () =>
        {
            if (IsInstanceValid(capturedCard))
                capturedCard.SetStaticDisplay(0.78f);
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
    // Upgrade action
    // ═════════════════════════════════════════════════════════════════

    private void OnUpgradePressed()
    {
        var save = SaveManager.ActiveSave;
        if (save == null || _selectedOwned == null) return;

        int nextTier = _selectedOwned.UpgradeTier + 1;
        if (nextTier > MAX_TIER) return;

        int cost = TierCosts[nextTier];
        if (save.ArcaneSplinters < cost)
        {
            GD.PrintErr("[CardUpgrade] Not enough splinters.");
            return;
        }

        // If this is a branching tier, lock in the chosen branch
        var branches = CardUpgradeApplier.GetBranchOptions(
            _selectedOwned.BlueprintId, nextTier);
        if (branches.Count > 1)
        {
            if (string.IsNullOrEmpty(_pendingBranch))
            {
                GD.PrintErr("[CardUpgrade] Branch required but none chosen.");
                return;
            }
            _selectedOwned.ChosenBranch = _pendingBranch;
            GD.Print($"[CardUpgrade] Branch locked: {_pendingBranch}");
        }

        save.ArcaneSplinters -= cost;
        _selectedOwned.UpgradeTier = nextTier;
        _pendingBranch = null; // clear pending after commit

        SaveManager.Save();
        GD.Print($"[CardUpgrade] Upgraded '{_selectedOwned.BlueprintId}' " +
                 $"to tier {nextTier} (branch: {_selectedOwned.ChosenBranch ?? "none"}). " +
                 $"Splinters remaining: {save.ArcaneSplinters}");

        Refresh();
    }

    // ═════════════════════════════════════════════════════════════════
    // Upgrade description from JSON
    // ═════════════════════════════════════════════════════════════════

    private string GetUpgradeDescription(string blueprintId, int tier)
    {
        // Quick pass — find the card JSON and read the description for this tier
        // without fully applying the upgrade
        var bp = CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, blueprintId, StringComparison.OrdinalIgnoreCase));
        if (bp == null) return "";

        // CardUpgradeApplier exposes a description-only helper
        return CardUpgradeApplier.GetUpgradeDescription(blueprintId, tier);
    }

    // ═════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════

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
}
