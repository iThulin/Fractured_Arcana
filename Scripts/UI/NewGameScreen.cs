using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// NewGameScreen.cs
//
// Purpose:        One-time character creation screen. Guild name
//                 + wizard school selection. On confirm, creates
//                 GuildSaveData, seeds the starter deck, returns
//                 to campus.
// Layer:          UI
// Collaborators:  SaveManager.cs, StarterDeckLoader.cs,
//                 PlayerSession.cs, CardLoaderV2.cs,
//                 SchoolColors.cs, UITheme.cs
// ============================================================

public partial class NewGameScreen : Control
{
    // ── Overlay callbacks (when hosted inside CampusScreen) ─────────────
    public Action<CardSchool, string> OnComplete;
    public Action OnCancel;

    // ── School data ──────────────────────────────────────────────────────
    private static readonly Dictionary<CardSchool, (string desc, string flavor, string symbol, string identity)> SchoolData = new()
    {
        { CardSchool.Adept, (
            "Graduated top of their cohort at the Aldric Academy, which prepares a person extremely well for everything except what actually happens next. Draws from all six schools without deep attunement to any — broad, adaptable, and carrying more theoretical knowledge than field experience. The perfect starting point for a wizard who hasn't yet decided what kind of wizard they intend to be.",
            "The academy taught me the name of every spell. The world is teaching me when to use them.",
            "✧", "The Graduate"
        )},
        { CardSchool.Elementalist, (
            "Channels raw elemental force — fire, ice, lightning, and earth — building attunement with each cast. As charges accumulate, spells transform: a simple bolt becomes a chain, a barrier becomes an avalanche. The Elementalist brute-forces the map, smashes obstacles, and fights whatever they find. Aggressive, direct, and devastating at full attunement.",
            "The Conclave tried to contain the storm. They built walls of iron and doctrine. I watched both burn.",
            "✦", "The Pathbreaker"
        )},
        { CardSchool.Druid, (
            "Reads the land the way other wizards read books — as something with opinions, with memory, with preferences about what happens on it. Shapes terrain to punish enemies who stand in the wrong place, binds animal companions into combat with a word, and heals companions not through arcane theory but through the same instinct that closes a wound on a living tree. The land does not fight for the Druid. It simply stops cooperating with everyone else.",
            "I did not move the river. I reminded it where it wanted to go.",
            "᛫", "The Shaper"
        )},
        { CardSchool.Necromancer, (
            "Moves between the living and the dead with the ease of someone who has done it a thousand times. Heals allies by drawing on the energy of fallen enemies, shields companions with spirit-light, and reads the echo of every ruin to learn what waits inside. Not a commander of the dead — a confidant. They come because, in their experience, it is worth showing up when they are called.",
            "Sugar, everyone ends up on my side of the bar eventually. I just make sure they have a good time getting there.",
            "☽", "The Conductor"
        )},
        { CardSchool.Tinker, (
            "Builds what others cannot yet imagine and deploys it before they finish asking whether it is possible. Turrets, arc emitters, pressure traps, and scouting drones — every engagement is an engineering problem, and every engineering problem has already been solved on paper. The Tinker arrives at the battlefield having already won it. The enemies simply have not received the report yet.",
            "The mathematics have been correct since Tuesday. I am waiting for the rest of the world to catch up.",
            "⚙", "The Engineer"
        )},
        { CardSchool.Enchanter, (
            "Layers enchantments with the patience of someone who finds complexity restful. Each glyph references the last, each binding tightens the one beneath it — by the fourth layer most wizards have stopped following, and by the seventh they have quietly left the room. The Enchanter does not prevent enemies from acting. They ensure that every action an enemy takes pulls another thread, and that all threads lead to the same place.",
            "You are welcome to try to unpick it. Most people only attempt that once.",
            "⬡", "The Namer"
        )},
        { CardSchool.Arcanist, (
            "Studies the deep rules of magic itself — not spells, but the laws that govern them. Arcanists amplify, redirect, and replicate effects, turning a single card into a cascade. They see hidden details in every POI they reveal, and argue more effectively because they understand the logical structure of every situation. Demanding to master; nearly unmatched at peak play.",
            "There are no miracles. Only observers who lacked the patience to understand what they were watching.",
            "◈", "The Analyst"
        )},
        { CardSchool.Chronomancer, (
            "Reads the celestial record and repositions events until the stars align with the outcome already written. Does not bend time — reads it. Delays enemy actions by holding them in a moment not yet resolved, echoes spells that the heavens say should have landed twice, and charts the overworld ahead by consulting what the sky has already declared. Deeply technical. Cosmically patient. Correct far more often than anyone is comfortable with.",
            "The outcome was written before your grandfather drew his first breath. I am not here to change it. I am here to make sure you are standing in the right place when it arrives.",
            "◎", "The Astrologer"
        )},
    };

    // ── Node refs ────────────────────────────────────────────────────────
    private LineEdit _guildNameInput;
    private Label _errorLabel;
    private CardSchool _selectedSchool = CardSchool.Elementalist;
    private int _targetSlot = -1;

    // Detail panel refs
    private Panel _detailPanel;
    private Panel _modelFrame;
    private Label _modelSymbol;
    private Label _modelPlaceholderLabel;
    private Label _detailIdentity;
    private Label _detailName;
    private Panel _detailDivider;
    private Label _detailDesc;
    private Label _detailCardCount;
    private Label _detailFlavor;

    // School card refs
    private readonly List<(CardSchool school, Panel card, Label name, Label symbol)> _schoolCards = new();

    public override void _Ready()
    {
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");
        _targetSlot = PlayerSession.PendingNewGameSlot;
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;

        // ── Background ──────────────────────────────────────────────────
        var bg = new ColorRect { Color = new Color(0.05f, 0.04f, 0.08f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // ── Root layout ──────────────────────────────────────────────────
        var outer = new MarginContainer();
        outer.SetAnchorsPreset(LayoutPreset.FullRect);
        outer.AddThemeConstantOverride("margin_left", 80);
        outer.AddThemeConstantOverride("margin_right", 80);
        outer.AddThemeConstantOverride("margin_top", 48);
        outer.AddThemeConstantOverride("margin_bottom", 36);
        AddChild(outer);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 20);
        outer.AddChild(root);

        BuildHeader(root);

        // ── Main two-column body ─────────────────────────────────────────
        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 24);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(body);

        BuildSchoolList(body);   // left: school picker
        BuildDetailPanel(body);  // right: model frame + info

        BuildBottomBar(root);

        UpdateAll();
    }

    // ════════════════════════════════════════════════════════════════════
    // Header
    // ════════════════════════════════════════════════════════════════════

    private void BuildHeader(VBoxContainer parent)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        parent.AddChild(vbox);

        var title = new Label { Text = "FOUND A GUILD" };
        title.AddThemeFontSizeOverride("font_size", 34);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(title);

        var sub = new Label
        {
            Text = "Your school shapes your deck, your campus buildings, and how you move through the world. This choice is permanent.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        sub.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        sub.AddThemeColorOverride("font_color", new Color(0.50f, 0.50f, 0.60f));
        vbox.AddChild(sub);

        // Thin violet rule
        var rule = new Panel { CustomMinimumSize = new Vector2(0, 1) };
        rule.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var ruleStyle = new StyleBoxFlat
        {
            BgColor = new Color(UITheme.Violet.R, UITheme.Violet.G, UITheme.Violet.B, 0.35f)
        };
        rule.AddThemeStyleboxOverride("panel", ruleStyle);
        vbox.AddChild(rule);
    }

    // ════════════════════════════════════════════════════════════════════
    // School list — left column
    // ════════════════════════════════════════════════════════════════════

    private void BuildSchoolList(HBoxContainer parent)
    {
        var col = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(280, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        col.AddThemeConstantOverride("separation", 5);
        parent.AddChild(col);

        var hdr = new Label { Text = "WIZARD SCHOOL" };
        hdr.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize - 1);
        hdr.AddThemeColorOverride("font_color", new Color(0.38f, 0.38f, 0.48f));
        col.AddChild(hdr);

        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
            col.AddChild(BuildSchoolCard(school));
    }

    private Control BuildSchoolCard(CardSchool school)
    {
        bool sel = school == _selectedSchool;
        var accent = SchoolColors.GetBorderColor(school);
        var (_, _, symbol, identity) = SchoolData.TryGetValue(school, out var d)
            ? d : ("", "", "✦", "");

        var card = new Panel { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.CustomMinimumSize = new Vector2(0, 48);
        ApplySchoolCardStyle(card, sel, accent);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 0);
        margin.AddThemeConstantOverride("margin_bottom", 0);
        card.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        margin.AddChild(row);

        var symLbl = new Label
        {
            Text = symbol,
            CustomMinimumSize = new Vector2(22, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        symLbl.AddThemeFontSizeOverride("font_size", 17);
        symLbl.AddThemeColorOverride("font_color",
            sel ? accent : new Color(0.30f, 0.30f, 0.40f));
        row.AddChild(symLbl);

        // Center name + identity vertically within the card height
        var nameWrapper = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        nameWrapper.AddThemeConstantOverride("separation", 0);
        nameWrapper.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(nameWrapper);

        var nameLbl = new Label { Text = school.ToString() };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLbl.AddThemeColorOverride("font_color",
            sel ? Colors.White : new Color(0.55f, 0.55f, 0.65f));
        nameWrapper.AddChild(nameLbl);

        var identityLbl = new Label { Text = identity };
        identityLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        identityLbl.AddThemeColorOverride("font_color",
            sel ? new Color(accent.R, accent.G, accent.B, 0.85f) : new Color(0.32f, 0.32f, 0.42f));
        nameWrapper.AddChild(identityLbl);

        var countLbl = new Label
        {
            Text = $"{CountCardsForSchool(school)}",
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        countLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize - 1);
        countLbl.AddThemeColorOverride("font_color",
            sel ? accent : new Color(0.30f, 0.30f, 0.40f));
        row.AddChild(countLbl);

        _schoolCards.Add((school, card, nameLbl, symLbl));

        // Click overlay
        var btn = new Button
        {
            AnchorRight = 1f,
            AnchorBottom = 1f,
            Flat = true,
            FocusMode = FocusModeEnum.None,
        };
        CardSchool captured = school;
        btn.Pressed += () => { _selectedSchool = captured; UpdateAll(); };
        card.AddChild(btn);

        return card;
    }

    private static void ApplySchoolCardStyle(Panel card, bool sel, Color accent)
    {
        var style = new StyleBoxFlat
        {
            BgColor = sel
                ? new Color(accent.R * 0.12f, accent.G * 0.12f, accent.B * 0.12f, 1f)
                : new Color(0.07f, 0.06f, 0.10f),
            BorderColor = sel ? accent : new Color(0.16f, 0.15f, 0.22f),
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
    }

    // ════════════════════════════════════════════════════════════════════
    // Detail panel — right column
    // Model frame on top, school info below
    // ════════════════════════════════════════════════════════════════════

    private void BuildDetailPanel(HBoxContainer parent)
    {
        // Right section: model frame (left ~45%) + info panel (right ~55%)
        // Both fill full column height side by side.
        var rightRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rightRow.AddThemeConstantOverride("separation", 12);
        parent.AddChild(rightRow);

        // ── Model frame (left half) ──────────────────────────────────────
        _modelFrame = new Panel
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rightRow.AddChild(_modelFrame);

        var modelMargin = new MarginContainer();
        modelMargin.SetAnchorsPreset(LayoutPreset.FullRect);
        modelMargin.AddThemeConstantOverride("margin_left", 20);
        modelMargin.AddThemeConstantOverride("margin_right", 20);
        modelMargin.AddThemeConstantOverride("margin_top", 20);
        modelMargin.AddThemeConstantOverride("margin_bottom", 20);
        _modelFrame.AddChild(modelMargin);

        var modelContent = new VBoxContainer();
        modelContent.AddThemeConstantOverride("separation", 10);
        modelMargin.AddChild(modelContent);

        modelContent.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        _modelSymbol = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "✦",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _modelSymbol.AddThemeFontSizeOverride("font_size", 72);
        modelContent.AddChild(_modelSymbol);

        _modelPlaceholderLabel = new Label
        {
            Text = "MODEL PREVIEW",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _modelPlaceholderLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _modelPlaceholderLabel.AddThemeColorOverride("font_color", new Color(0.28f, 0.28f, 0.38f));
        modelContent.AddChild(_modelPlaceholderLabel);

        modelContent.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        // ── Info panel (right half) ──────────────────────────────────────
        _detailPanel = new Panel
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rightRow.AddChild(_detailPanel);

        var infoMargin = new MarginContainer();
        infoMargin.SetAnchorsPreset(LayoutPreset.FullRect);
        infoMargin.AddThemeConstantOverride("margin_left", 24);
        infoMargin.AddThemeConstantOverride("margin_right", 24);
        infoMargin.AddThemeConstantOverride("margin_top", 24);
        infoMargin.AddThemeConstantOverride("margin_bottom", 24);
        _detailPanel.AddChild(infoMargin);

        var infoVBox = new VBoxContainer();
        infoVBox.AddThemeConstantOverride("separation", 10);
        infoMargin.AddChild(infoVBox);

        // Identity tag
        _detailIdentity = new Label();
        _detailIdentity.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        infoVBox.AddChild(_detailIdentity);

        // School name
        _detailName = new Label();
        _detailName.AddThemeFontSizeOverride("font_size", 26);
        _detailName.AddThemeColorOverride("font_color", Colors.White);
        infoVBox.AddChild(_detailName);

        // Accent divider
        _detailDivider = new Panel { CustomMinimumSize = new Vector2(48, 2) };
        _detailDivider.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        infoVBox.AddChild(_detailDivider);

        // Description
        _detailDesc = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _detailDesc.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _detailDesc.AddThemeColorOverride("font_color", new Color(0.72f, 0.72f, 0.82f));
        infoVBox.AddChild(_detailDesc);

        // Card count
        _detailCardCount = new Label();
        _detailCardCount.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        infoVBox.AddChild(_detailCardCount);

        // Push flavor to bottom
        infoVBox.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        var flavorRule = new Panel { CustomMinimumSize = new Vector2(0, 1) };
        flavorRule.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var frStyle = new StyleBoxFlat { BgColor = new Color(0.20f, 0.20f, 0.28f) };
        flavorRule.AddThemeStyleboxOverride("panel", frStyle);
        infoVBox.AddChild(flavorRule);

        _detailFlavor = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _detailFlavor.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _detailFlavor.AddThemeColorOverride("font_color", new Color(0.36f, 0.36f, 0.46f));
        infoVBox.AddChild(_detailFlavor);
    }

    // ════════════════════════════════════════════════════════════════════
    // Bottom bar — guild name + buttons
    // ════════════════════════════════════════════════════════════════════

    private void BuildBottomBar(VBoxContainer parent)
    {
        var rule = new Panel { CustomMinimumSize = new Vector2(0, 1) };
        rule.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var rStyle = new StyleBoxFlat { BgColor = new Color(0.18f, 0.17f, 0.26f) };
        rule.AddThemeStyleboxOverride("panel", rStyle);
        parent.AddChild(rule);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 20);
        parent.AddChild(bar);

        // Guild name
        var nameSection = new VBoxContainer();
        nameSection.AddThemeConstantOverride("separation", 6);
        nameSection.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bar.AddChild(nameSection);

        var nameHdr = new Label { Text = "GUILD NAME" };
        nameHdr.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize - 1);
        nameHdr.AddThemeColorOverride("font_color", new Color(0.38f, 0.38f, 0.48f));
        nameSection.AddChild(nameHdr);

        _guildNameInput = new LineEdit
        {
            PlaceholderText = "Name your guild...",
            MaxLength = 32,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _guildNameInput.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _guildNameInput.AddThemeColorOverride("font_color", Colors.White);
        _guildNameInput.AddThemeColorOverride("font_placeholder_color", new Color(0.32f, 0.32f, 0.42f));

        var inputNormal = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.05f, 0.10f),
            BorderColor = new Color(0.24f, 0.23f, 0.34f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        var inputFocus = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.06f, 0.12f),
            BorderColor = UITheme.Violet,
            BorderWidthTop = 1,
            BorderWidthBottom = 2,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        _guildNameInput.AddThemeStyleboxOverride("normal", inputNormal);
        _guildNameInput.AddThemeStyleboxOverride("focus", inputFocus);
        nameSection.AddChild(_guildNameInput);

        _errorLabel = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _errorLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _errorLabel.AddThemeColorOverride("font_color", UITheme.Danger);
        nameSection.AddChild(_errorLabel);

        // Buttons
        var btnCol = new VBoxContainer();
        btnCol.AddThemeConstantOverride("separation", 6);
        btnCol.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        bar.AddChild(btnCol);

        var confirmBtn = new Button
        {
            Text = "Found the Guild",
            CustomMinimumSize = new Vector2(200, 44),
        };
        confirmBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        StylePrimaryButton(confirmBtn);
        confirmBtn.Pressed += OnConfirmPressed;
        btnCol.AddChild(confirmBtn);

        var backBtn = new Button
        {
            Text = "← Back",
            CustomMinimumSize = new Vector2(200, 32),
        };
        backBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        StyleGhostButton(backBtn);
        backBtn.Pressed += () =>
        {
            if (OnCancel != null) OnCancel.Invoke();
            else GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
        };
        btnCol.AddChild(backBtn);
    }

    // ════════════════════════════════════════════════════════════════════
    // Update — called whenever selected school changes
    // ════════════════════════════════════════════════════════════════════

    private void UpdateAll()
    {
        UpdateSchoolCards();
        UpdateDetailPanel();
    }

    private void UpdateSchoolCards()
    {
        foreach (var (school, card, nameLbl, symLbl) in _schoolCards)
        {
            bool sel = school == _selectedSchool;
            var accent = SchoolColors.GetBorderColor(school);
            ApplySchoolCardStyle(card, sel, accent);
            nameLbl.AddThemeColorOverride("font_color",
                sel ? Colors.White : new Color(0.55f, 0.55f, 0.65f));
            symLbl.AddThemeColorOverride("font_color",
                sel ? accent : new Color(0.30f, 0.30f, 0.40f));
        }
    }

    private void UpdateDetailPanel()
    {
        if (_detailName == null) return;

        var accent = SchoolColors.GetBorderColor(_selectedSchool);
        var dark = SchoolColors.GetDarkColor(_selectedSchool);
        var (desc, flavor, symbol, identity) = SchoolData.TryGetValue(_selectedSchool, out var d)
            ? d : ("", "", "✦", "");

        // Model frame
        var modelFrameStyle = new StyleBoxFlat
        {
            BgColor = new Color(dark.R * 0.6f, dark.G * 0.6f, dark.B * 0.6f, 0.35f),
            BorderColor = new Color(accent.R, accent.G, accent.B, 0.5f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
        };
        _modelFrame?.AddThemeStyleboxOverride("panel", modelFrameStyle);

        _modelSymbol.Text = symbol;
        _modelSymbol.AddThemeColorOverride("font_color", accent);

        // Info panel
        var infoPanelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.06f, 0.10f),
            BorderColor = new Color(accent.R, accent.G, accent.B, 0.4f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        _detailPanel?.AddThemeStyleboxOverride("panel", infoPanelStyle);

        _detailIdentity.Text = identity.ToUpper();
        _detailIdentity.AddThemeColorOverride("font_color", new Color(accent.R, accent.G, accent.B, 0.7f));

        _detailName.Text = _selectedSchool.ToString();

        var divStyle = new StyleBoxFlat { BgColor = accent };
        _detailDivider?.AddThemeStyleboxOverride("panel", divStyle);

        _detailDesc.Text = desc;

        int count = CountCardsForSchool(_selectedSchool);
        _detailCardCount.Text = $"{count} cards in starting pool";
        _detailCardCount.AddThemeColorOverride("font_color", new Color(accent.R, accent.G, accent.B, 0.6f));

        _detailFlavor.Text = flavor;
    }

    // ════════════════════════════════════════════════════════════════════
    // Confirm
    // ════════════════════════════════════════════════════════════════════

    private void OnConfirmPressed()
    {
        string guildName = _guildNameInput?.Text.Trim() ?? "";

        if (string.IsNullOrEmpty(guildName))
        {
            ShowError("Please enter a guild name.");
            return;
        }
        if (_targetSlot < 0)
        {
            ShowError("No save slot selected. Return to campus and try again.");
            return;
        }

        PlayerSession.SelectedSchool = _selectedSchool;

        if (OnComplete != null) { OnComplete.Invoke(_selectedSchool, guildName); return; }

        var save = SaveManager.NewGame(_targetSlot, guildName, _selectedSchool.ToString());
        if (save == null) { ShowError("Failed to create save. Please try again."); return; }

        GD.Print($"[NewGame] Guild '{guildName}' founded as {_selectedSchool} in slot {_targetSlot}");
        GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
    }

    private void ShowError(string msg)
    {
        if (_errorLabel == null) return;
        _errorLabel.Text = msg;
        _errorLabel.Visible = true;
    }

    // ════════════════════════════════════════════════════════════════════
    // Button styles
    // ════════════════════════════════════════════════════════════════════

    private static void StylePrimaryButton(Button btn)
    {
        var normal = new StyleBoxFlat
        {
            BgColor = UITheme.ButtonPrimary,
            BorderColor = UITheme.Violet,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        var hover = new StyleBoxFlat
        {
            BgColor = UITheme.ButtonPrimaryHover,
            BorderColor = UITheme.Violet,
            BorderWidthTop = 1,
            BorderWidthBottom = 2,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeColorOverride("font_color", Colors.White);
    }

    private static void StyleGhostButton(Button btn)
    {
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = new Color(0.22f, 0.22f, 0.32f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        var hover = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.10f, 0.16f),
            BorderColor = new Color(0.32f, 0.32f, 0.45f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeColorOverride("font_color", new Color(0.42f, 0.42f, 0.52f));
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private static int CountCardsForSchool(CardSchool school)
    {
        int n = 0;
        foreach (var bp in CardDatabase.Blueprints)
            if (bp.School == school) n++;
        return n;
    }
}
