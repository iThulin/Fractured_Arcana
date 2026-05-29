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
            "Aldric Academy graduates know the proper name of every spell, the eight classical schools, the seventeen permitted variant pronunciations, and the precise angle at which to hold the wand during a formal casting examination. None of this has ever been useful. An Adept knows a little of everything and is constantly in the process of learning that \"a little of everything\" is what people say when they mean \"not enough of anything yet.\" They will become something, probably.",
            "The academy taught me the name of every spell. The world is teaching me when to use them.",
            "✧", "Wenna Aldric"
        )},
        { CardSchool.Elementalist, (
            "The four elements are older than language and have been arguing for longer than that. The Elementalist is what happens when one person agrees to host the argument. Fire wants to spread. Ice wants to hold. Storm wants to move. Earth wants to stay. They reach no conclusion through him, but they reach it loudly, and the landscape around him tends to reflect whichever of them was making the better point that morning. He is not winning a fight. He is the place a much older fight is presently occurring.",
            "They told me the storm could be contained. I asked who told them.",
            "✦", "Joren Kall, who answered the storm"
        )},
        { CardSchool.Druid, (
            "The Druid sleeps where she falls and eats what is in front of her. She has been doing this for a long time. The land does what she asks. It does so not because she commands it, and not because it serves her, but because what she asks is generally what the land was going to do anyway. In the slow vocabulary of root and weather and bone, she has learned to ask only for what is already coming. She does not heal so much as decide which wounds are permitted to close. The animals that follow her are not pets. They are present for the same reason the carrion birds are present, which is that something is about to happen and they would like to be there when it does.",
            "The river goes where it wants. I happen to know where that is.",
            "᛫", "Hess, who does not intervene"
        )},
        { CardSchool.Necromancer, (
            "She runs a bar. The bar is also a funeral home, depending on what you need. Most of her regulars are dead, which she finds restful — they listen better, drink less, and never ask her to weigh in on whose fault it was. She heals the living by asking the dead to chip in, and they do, because she is the kind of person you want to be on good terms with on both sides of the door. Spirit-light. Ruin-echoes. The usual.",
            "He still owes me eighty silver. I'll get it eventually. Patience is sort of my whole thing.",
            "☽", "Ondria Vell, Hostess of the Long Table"
        )},
        { CardSchool.Tinker, (
            "A turret is just a question with a satisfying answer. The Tinker has many questions. They arrive at every fight with a small cart, three projects in various states of completion, and a notebook full of diagrams that other wizards describe as \"concerning\" and engineers describe as \"not yet, but close.\" Half the devices work. The other half are research. The distinction matters less than you'd think.",
            "That wasn't supposed to do that. Write it down.",
            "⚙", "Master Bram Korro"
        )},
        { CardSchool.Enchanter, (
            "An Enchanter does not stop you from doing things. An Enchanter makes sure that whatever you do, the next thing you have to do is worse. There are seven layers in a properly built binding. Other wizards have, at various academic conferences, suggested that four is sufficient and seven is excessive. The Enchanter, present in the room, did not respond. The paper was withdrawn.",
            "I'm not finished. Sit.",
            "⬡", "Cael Morn, of the Seventh Layer"
        )},
        { CardSchool.Arcanist, (
           "The Arcanist does not cast spells so much as revise them. He keeps a tome. The tome is not a spellbook in any sense another wizard would recognize; it is closer to a workshop, with the spells of every school laid out on the bench in pieces, partially disassembled, annotated in three languages and a personal shorthand that he has never explained. When another wizard casts something near him, he sees what it is doing and, more importantly, what it is failing to do — the constraint someone built into it for safety, the redundancy nobody bothered to remove, the second verse the original author forgot was there. A fire spell passes through the room and he lets it pass; a moment later, in a way nobody can quite trace, it is still burning. A binding goes around an enemy and holds tighter than it should, because the Arcanist has quietly removed the clause the original caster put in to make it humane.",
           "There are no miracles. Only observers who lacked the patience to understand what they were watching.",
            "◈", "Master Aurel Pendry"
        )},
        { CardSchool.Chronomancer, (
            "The Chronomancer is not bending time. He will correct you on this. What he is doing is reading — the sky writes the outcome of every event before the event happens, and he has spent a long time learning the script. He knows who wins. He knows who loses. He knows, with some precision, the moment at which each of you stopped having a real choice. He has been generous with this knowledge, in places. He has been less generous in others. The galaxy turns, and he turns with it, and somewhere in that turning he made a decision he is not, yet, willing to discuss.",
            "I am not changing what happens. I am changing where you are standing when it does. The distinction will matter to you, later.",
            "◎", "Kassian Vor-Aleth, who read the Sky"
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
