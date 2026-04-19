using Godot;
using System;

/// <summary>
/// Phase 1 campus screen. School selection + run launch + results display.
/// Phase 2+ adds building management, companion roster, equipment, etc.
/// </summary>
public partial class CampusScreen : Control
{
    private Label _titleLabel;
    private Label _summaryLabel;
    private OptionButton _schoolPicker;
    private Label _schoolDescription;
    private CheckBox _debugCheckbox;
    private Button _startRunButton;
    private Button _quitButton;
    private VBoxContainer _layout;

    private static readonly System.Collections.Generic.Dictionary<CardSchool, string> SchoolDescriptions = new()
    {
        { CardSchool.Arcanist,     "Masters of raw magic. High damage spells and mana manipulation." },
        { CardSchool.Elementalist, "Controls terrain with fire, ice, and storm effects." },
        { CardSchool.Necromancer,  "Summons minions and drains life from enemies." },
        { CardSchool.Enchanter,    "Buffs, debuffs, and tile enchantments." },
        { CardSchool.Tinker,       "Mechanical traps, turrets, and area control." },
        { CardSchool.Generic,      "A mixed deck drawn from all schools." },
    };

    public override void _Ready()
    {
        // Make sure card database is loaded for school descriptions
        if (CardDatabase.Blueprints.Count == 0)
            CardDatabase.LoadFromCsv("res://Data/cards.csv");

        // ── Layout ──────────────────────────────────────────────────────
        _layout = new VBoxContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f,
            AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            OffsetLeft = -220, OffsetTop = -250,
            OffsetRight = 220, OffsetBottom = 250,
        };
        _layout.AddThemeConstantOverride("separation", 16);
        AddChild(_layout);

        // ── Title ───────────────────────────────────────────────────────
        _titleLabel = new Label
        {
            Text = "Guild Campus",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 36);
        _layout.AddChild(_titleLabel);

        // ── Run summary (shown after returning from a run) ──────────────
        _summaryLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _summaryLabel.AddThemeFontSizeOverride("font_size", 16);
        _layout.AddChild(_summaryLabel);

        if (RunResultData.HasResults)
        {
            string outcome = RunResultData.ReachedObjective ? "SUCCESS" : "FAILED";
            _summaryLabel.Text = $"Last Run: {outcome}\n" +
                                 $"Gold earned: {RunResultData.GoldEarned}\n" +
                                 $"Encounters won: {RunResultData.EncountersWon}\n" +
                                 $"HP remaining: {RunResultData.HPRemaining}";
            _summaryLabel.Modulate = RunResultData.ReachedObjective
                ? new Color(0.6f, 1f, 0.6f)
                : new Color(1f, 0.6f, 0.6f);
            RunResultData.Clear();
        }
        else
        {
            _summaryLabel.Text = "No expeditions yet. The wilds await.";
            _summaryLabel.Modulate = new Color(0.7f, 0.7f, 0.7f);
        }

        // ── Spacer ──────────────────────────────────────────────────────
        _layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        // ── School selection label ──────────────────────────────────────
        var schoolLabel = new Label
        {
            Text = "Choose Your Wizard School",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        schoolLabel.AddThemeFontSizeOverride("font_size", 20);
        _layout.AddChild(schoolLabel);

        // ── School picker dropdown ──────────────────────────────────────
        _schoolPicker = new OptionButton
        {
            CustomMinimumSize = new Vector2(400, 40),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };

        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
            _schoolPicker.AddItem(school.ToString(), (int)school);

        // Default to whatever was last selected (persists via PlayerSession)
        _schoolPicker.Selected = (int)PlayerSession.SelectedSchool;
        _schoolPicker.ItemSelected += OnSchoolChanged;
        _layout.AddChild(_schoolPicker);

        // ── School description ──────────────────────────────────────────
        _schoolDescription = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(400, 60),
        };
        _schoolDescription.AddThemeFontSizeOverride("font_size", 14);
        _layout.AddChild(_schoolDescription);

        UpdateSchoolDescription();

        // ── Debug checkbox ──────────────────────────────────────────────
        _debugCheckbox = new CheckBox
        {
            Text = "Debug Mode",
            ButtonPressed = PlayerSession.DebugMode,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _layout.AddChild(_debugCheckbox);

        // ── Spacer ──────────────────────────────────────────────────────
        _layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        // ── Start Run button ────────────────────────────────────────────
        _startRunButton = new Button
        {
            Text = "Begin Expedition",
            CustomMinimumSize = new Vector2(300, 50),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _startRunButton.AddThemeFontSizeOverride("font_size", 20);
        _startRunButton.Pressed += OnStartRun;
        _layout.AddChild(_startRunButton);

        // ── Quit button ─────────────────────────────────────────────────
        _quitButton = new Button
        {
            Text = "Quit",
            CustomMinimumSize = new Vector2(300, 40),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _quitButton.Pressed += () => GetTree().Quit();
        _layout.AddChild(_quitButton);
    }

    private void OnSchoolChanged(long index)
    {
        UpdateSchoolDescription();
    }

    private void UpdateSchoolDescription()
    {
        var school = (CardSchool)_schoolPicker.GetSelectedId();

        int count = 0;
        foreach (var bp in CardDatabase.Blueprints)
            if (bp.School == school) count++;

        string desc = SchoolDescriptions.TryGetValue(school, out var d) ? d : "";
        _schoolDescription.Text = $"{desc}\n{count} cards available.";
    }

    private void OnStartRun()
    {
        // Set the session before launching — GameRunner reads these
        PlayerSession.SelectedSchool = (CardSchool)_schoolPicker.GetSelectedId();
        PlayerSession.DebugMode = _debugCheckbox.ButtonPressed;

        GD.Print($"Starting run: {PlayerSession.SelectedSchool}, Debug: {PlayerSession.DebugMode}");
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/OverworldScene.tscn");
    }
}