using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// NewGameScreen.cs
//
// Purpose:        One-time character creation screen shown when
//                 starting a new guild. Lets the player enter a
//                 guild name and pick a wizard school. On confirm,
//                 creates the GuildSaveData, seeds the starter
//                 deck, and returns to the campus.
//
//                 School is a permanent choice — it determines
//                 the starter deck, school building unlocks, and
//                 (in future) faction dispositions. It cannot be
//                 changed after creation.
//
// Layer:          UI
// Collaborators:  SaveManager.cs (NewGame),
//                 StarterDeckLoader.cs (SeedStarterDeck),
//                 PlayerSession.cs (SelectedSchool, PendingSlot),
//                 CardLoaderV2.cs (lazy-load on entry),
//                 UITheme.cs
// ============================================================

public partial class NewGameScreen : Control
{
    // ── School data ──────────────────────────────────────────────────────
    private static readonly Dictionary<CardSchool, (string desc, string flavor)> SchoolData = new()
    {
        { CardSchool.Elementalist, (
            "Controls terrain with fire, ice, and storm effects. Builds elemental attunement to unlock powerful combo chains.",
            "\"The world is not made of stone and wood — it is made of fire waiting to breathe.\""
        )},
        { CardSchool.Arcanist, (
            "Masters of raw magical force. High-damage spells and mana manipulation. Rewards aggressive play.",
            "\"Power is not learned. It is taken.\""
        )},
        { CardSchool.Necromancer, (
            "Summons undead minions and drains life from enemies. Attrition-focused — grows stronger as enemies fall.",
            "\"Every ending is a beginning for those who know how to listen.\""
        )},
        { CardSchool.Enchanter, (
            "Buffs, debuffs, and tile enchantments. Controls the battlefield through persistent effects and status manipulation.",
            "\"The cleverest weapon is one your enemy never notices.\""
        )},
        { CardSchool.Tinker, (
            "Mechanical traps, turrets, and area control. Prepares the battlefield before enemies arrive.",
            "\"Given enough time and copper wire, anything is possible.\""
        )},
        { CardSchool.Generic, (
            "A mixed deck drawn from all schools. Good for learning the game; lacks the synergy of a focused school.",
            "\"The academy teaches everything. Masters forget most of it.\""
        )},
    };

    // ── Nodes ────────────────────────────────────────────────────────────
    private LineEdit _guildNameInput;
    private Label _selectedSchoolName;
    private Label _selectedSchoolDesc;
    private Label _selectedSchoolFlavor;
    private Label _cardCountLabel;
    private Button _confirmButton;
    private Label _errorLabel;

    private CardSchool _selectedSchool = CardSchool.Elementalist;
    private int _targetSlot = -1;

    public override void _Ready()
    {
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");
        _targetSlot = PlayerSession.PendingNewGameSlot;
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        // ── Full-screen dark background ───────────────────────────────────
        var bg = new ColorRect { Color = UITheme.BgDeep };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // ── Centered content column ───────────────────────────────────────
        var outer = new MarginContainer();
        outer.SetAnchorsPreset(LayoutPreset.FullRect);
        outer.AddThemeConstantOverride("margin_left", 60);
        outer.AddThemeConstantOverride("margin_right", 60);
        outer.AddThemeConstantOverride("margin_top", 40);
        outer.AddThemeConstantOverride("margin_bottom", 40);
        AddChild(outer);

        var mainHBox = new HBoxContainer();
        mainHBox.AddThemeConstantOverride("separation", 32);
        outer.AddChild(mainHBox);

        // ── LEFT: school selection cards ──────────────────────────────────
        var leftCol = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(300, 0),
        };
        leftCol.AddThemeConstantOverride("separation", 10);
        mainHBox.AddChild(leftCol);

        var schoolHeader = new Label { Text = "CHOOSE YOUR SCHOOL" };
        schoolHeader.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        schoolHeader.AddThemeColorOverride("font_color", UITheme.Gold);
        leftCol.AddChild(schoolHeader);

        var schoolSubtitle = new Label
        {
            Text = "This determines your starting deck and shapes your playstyle.\nCannot be changed after creation.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        schoolSubtitle.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        schoolSubtitle.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        leftCol.AddChild(schoolSubtitle);

        leftCol.AddChild(new HSeparator());

        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
            leftCol.AddChild(BuildSchoolCard(school));

        // ── RIGHT: detail + guild name + confirm ──────────────────────────
        var rightCol = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        rightCol.AddThemeConstantOverride("separation", 16);
        mainHBox.AddChild(rightCol);

        // School detail panel
        var detailPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var detailStyle = new StyleBoxFlat
        {
            BgColor = UITheme.BgRaised,
            BorderColor = UITheme.Violet,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        detailPanel.AddThemeStyleboxOverride("panel", detailStyle);
        rightCol.AddChild(detailPanel);

        var detailMargin = new MarginContainer();
        detailMargin.AddThemeConstantOverride("margin_left", 20);
        detailMargin.AddThemeConstantOverride("margin_right", 20);
        detailMargin.AddThemeConstantOverride("margin_top", 16);
        detailMargin.AddThemeConstantOverride("margin_bottom", 16);
        detailPanel.AddChild(detailMargin);

        var detailVBox = new VBoxContainer();
        detailVBox.AddThemeConstantOverride("separation", 10);
        detailMargin.AddChild(detailVBox);

        _selectedSchoolName = new Label();
        _selectedSchoolName.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize + 2);
        _selectedSchoolName.AddThemeColorOverride("font_color", UITheme.Gold);
        detailVBox.AddChild(_selectedSchoolName);

        _selectedSchoolDesc = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _selectedSchoolDesc.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _selectedSchoolDesc.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        detailVBox.AddChild(_selectedSchoolDesc);

        _cardCountLabel = new Label();
        _cardCountLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _cardCountLabel.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        detailVBox.AddChild(_cardCountLabel);

        detailVBox.AddChild(new HSeparator());

        _selectedSchoolFlavor = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _selectedSchoolFlavor.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _selectedSchoolFlavor.AddThemeColorOverride("font_color", UITheme.TextDim);
        detailVBox.AddChild(_selectedSchoolFlavor);

        // Guild name input
        rightCol.AddChild(new HSeparator());

        var nameHeader = new Label { Text = "GUILD NAME" };
        nameHeader.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        nameHeader.AddThemeColorOverride("font_color", UITheme.Gold);
        rightCol.AddChild(nameHeader);

        _guildNameInput = new LineEdit
        {
            PlaceholderText = "Enter guild name...",
            MaxLength = 32,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _guildNameInput.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        rightCol.AddChild(_guildNameInput);

        // Error label (hidden until needed)
        _errorLabel = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _errorLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _errorLabel.AddThemeColorOverride("font_color", UITheme.Danger);
        rightCol.AddChild(_errorLabel);

        // Spacer
        rightCol.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        // Confirm button
        _confirmButton = new Button
        {
            Text = "Found the Guild",
            CustomMinimumSize = new Vector2(0, 52),
        };
        _confirmButton.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        UITheme.ApplyButtonStyle(_confirmButton, isPrimary: true);
        _confirmButton.Pressed += OnConfirmPressed;
        rightCol.AddChild(_confirmButton);

        // Back button (small, secondary)
        var backBtn = new Button
        {
            Text = "← Back",
            CustomMinimumSize = new Vector2(0, 36),
        };
        backBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(backBtn, isPrimary: false);
        backBtn.Pressed += () =>
            GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
        rightCol.AddChild(backBtn);

        // Initial selection
        UpdateDetailPanel();
    }

    // ════════════════════════════════════════════════════════════════════
    // School card builder
    // ════════════════════════════════════════════════════════════════════

    private Control BuildSchoolCard(CardSchool school)
    {
        bool isSelected = school == _selectedSchool;
        int cardCount = CountCardsForSchool(school);

        var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };

        var style = new StyleBoxFlat
        {
            BgColor = isSelected ? UITheme.BgRaised : UITheme.BgBase,
            BorderColor = isSelected ? UITheme.Violet : UITheme.NeutralDim,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = isSelected ? 3 : 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        card.AddThemeStyleboxOverride("panel", style);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        card.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        margin.AddChild(row);

        var nameLabel = new Label
        {
            Text = school.ToString(),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLabel.AddThemeColorOverride("font_color",
            isSelected ? Colors.White : UITheme.TextSecondary);
        row.AddChild(nameLabel);

        var countLabel = new Label { Text = $"{cardCount} cards" };
        countLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        countLabel.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        row.AddChild(countLabel);

        // Register for highlight updates
        _schoolCards.Add((school, card, nameLabel));

        // Invisible click overlay
        var btn = new Button
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            Flat = true,
            FocusMode = FocusModeEnum.None,
        };
        CardSchool captured = school;
        btn.Pressed += () =>
        {
            _selectedSchool = captured;
            RebuildSchoolCards();
            UpdateDetailPanel();
        };
        card.AddChild(btn);

        return card;
    }

    private void RebuildSchoolCards()
    {
        // Find the parent (leftCol) by going up from the container
        // Simpler: just rebuild all children of each school card's panel
        // by iterating the leftCol children — but we don't have a ref.
        // Instead, flag each card by name so we can update border only.
        // Simplest reliable approach: reload the whole screen region.
        // Given it's creation-time only, just reload the scene.
        // Even simpler: store leftCol as a field and rebuild on click.
        // We stored cards directly in leftCol — store the ref instead.
        // For now: track cards by tag, update their StyleBoxFlat.

        // Since we don't have a leftCol ref here, use a different pattern:
        // The cards are direct children of the Control tree. Walk the tree.
        // Actually cleanest: just change scene to self, which re-runs _Ready.
        // But that loses guild name input. Instead store _schoolCards as a list.

        // The reliable fix: rebuild the detail panel only (which we do below)
        // and handle card highlighting by iterating _schoolCardButtons.
        // We'll update just the borders via stored references.

        foreach (var (school, card, nameLabel) in _schoolCards)
        {
            bool sel = school == _selectedSchool;
            var style = new StyleBoxFlat
            {
                BgColor = sel ? UITheme.BgRaised : UITheme.BgBase,
                BorderColor = sel ? UITheme.Violet : UITheme.NeutralDim,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = sel ? 3 : 1,
                BorderWidthRight = 1,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
            };
            card.AddThemeStyleboxOverride("panel", style);
            nameLabel.AddThemeColorOverride("font_color",
                sel ? Colors.White : UITheme.TextSecondary);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Detail panel update
    // ════════════════════════════════════════════════════════════════════

    private void UpdateDetailPanel()
    {
        if (_selectedSchoolName == null) return;

        var (desc, flavor) = SchoolData.TryGetValue(_selectedSchool, out var data)
            ? data : ("No description available.", "");

        _selectedSchoolName.Text = _selectedSchool.ToString();
        _selectedSchoolDesc.Text = desc;
        _selectedSchoolFlavor.Text = flavor;

        int count = CountCardsForSchool(_selectedSchool);
        _cardCountLabel.Text = $"{count} cards available in this school";
    }

    // ════════════════════════════════════════════════════════════════════
    // Confirm
    // ════════════════════════════════════════════════════════════════════

    private void OnConfirmPressed()
    {
        string guildName = _guildNameInput.Text.Trim();

        if (string.IsNullOrEmpty(guildName))
        {
            ShowError("Please enter a guild name.");
            return;
        }

        if (_targetSlot < 0)
        {
            ShowError("No save slot selected. Return to the campus and try again.");
            return;
        }

        PlayerSession.SelectedSchool = _selectedSchool;

        var save = SaveManager.NewGame(_targetSlot, guildName, _selectedSchool.ToString());
        if (save == null)
        {
            ShowError("Failed to create save. Please try again.");
            return;
        }

        GD.Print($"[NewGame] Guild '{guildName}' created as {_selectedSchool} in slot {_targetSlot}");
        GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
    }

    private void ShowError(string message)
    {
        if (_errorLabel == null) return;
        _errorLabel.Text = message;
        _errorLabel.Visible = true;
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private static int CountCardsForSchool(CardSchool school)
    {
        int count = 0;
        foreach (var bp in CardDatabase.Blueprints)
            if (bp.School == school) count++;
        return count;
    }

    // Track school cards for highlight updates — populated in BuildUI
    // using a parallel list since we need card panel + name label refs.
    private readonly List<(CardSchool school, PanelContainer card, Label nameLabel)>
        _schoolCards = new();
}
