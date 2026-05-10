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
    private Button _cardLibraryButton;
    private Button _quitButton;
    private VBoxContainer _layout;

    private int _selectedSlot = -1;
    private VBoxContainer _slotContainer;
    private VBoxContainer _companionContainer;

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
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");

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

        // ── Save slot selection ─────────────────────────────────────────
        var slotLabel = new Label
        {
            Text = "Save Slots",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        slotLabel.AddThemeFontSizeOverride("font_size", 20);
        _layout.AddChild(slotLabel);

        _slotContainer = new VBoxContainer();
        _slotContainer.AddThemeConstantOverride("separation", 8);
        _layout.AddChild(_slotContainer);

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

        // ── Companions ──────────────────────────────────────────────────
        _layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        var companionLabel = new Label
        {
            Text = "Companions",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        companionLabel.AddThemeFontSizeOverride("font_size", 20);
        _layout.AddChild(companionLabel);

        _companionContainer = new VBoxContainer();
        _companionContainer.AddThemeConstantOverride("separation", 6);
        _layout.AddChild(_companionContainer);

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

        // ── Card Library button ─────────────────────────────────────────
        _cardLibraryButton = new Button
        {
            Text = "Card Library",
            CustomMinimumSize = new Vector2(300, 40),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _cardLibraryButton.AddThemeFontSizeOverride("font_size", 18);
        _cardLibraryButton.Pressed += OnOpenCardLibrary;
        _layout.AddChild(_cardLibraryButton);

        // ── Quit button ─────────────────────────────────────────────────
        _quitButton = new Button
        {
            Text = "Quit",
            CustomMinimumSize = new Vector2(300, 40),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        _quitButton.Pressed += () => GetTree().Quit();
        _layout.AddChild(_quitButton);

                // Debug check
        GD.Print($"CampusScreen: ActiveSave={SaveManager.ActiveSave?.GuildName ?? "NULL"}, " +
                 $"Gold={SaveManager.ActiveSave?.Gold ?? -1}, " +
                 $"Runs={SaveManager.ActiveSave?.TotalRuns ?? -1}");

        // Auto-select active slot if returning from a run
        if (SaveManager.ActiveSave != null && SaveManager.ActiveSlot >= 0)
        {
            _selectedSlot = SaveManager.ActiveSlot;
            CompanionRoster.EnsureRoster(SaveManager.ActiveSave);

            if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave.SelectedSchool, out var school))
            {
                _schoolPicker.Selected = (int)school;
                UpdateSchoolDescription();
            }
        }

        RefreshSlotButtons();
        RefreshCompanionList();
        UpdateStartButton();
        
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
        if (_selectedSlot < 0) return;

        PlayerSession.SelectedSchool = (CardSchool)_schoolPicker.GetSelectedId();
        PlayerSession.DebugMode = _debugCheckbox.ButtonPressed;

        // Update save with current school
        if (SaveManager.ActiveSave != null)
        {
            SaveManager.ActiveSave.SelectedSchool = PlayerSession.SelectedSchool.ToString();
            SaveManager.Save();
        }

        GD.Print($"Starting run: {PlayerSession.SelectedSchool}, Debug: {PlayerSession.DebugMode}");
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/OverworldScene.tscn");
    }

    private void OnOpenCardLibrary()
    {
        GetTree().ChangeSceneToFile("res://Scenes/UI/CardLibrary.tscn");
    }

    private void RefreshSlotButtons()
    {
        // Clear existing buttons
        foreach (var child in _slotContainer.GetChildren())
            child.QueueFree();

        var slots = SaveManager.GetAllSlotInfo();
        foreach (var slot in slots)
        {
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 10);

            string label;
            if (slot.IsEmpty)
            {
                label = $"Slot {slot.Slot + 1}: Empty";
            }
            else
            {
                label = $"Slot {slot.Slot + 1}: {slot.GuildName} ({slot.School}) " +
                        $"| Gold: {slot.Gold} | Runs: {slot.TotalRuns}";
            }

            var loadBtn = new Button
            {
                Text = slot.IsEmpty ? $"New Game (Slot {slot.Slot + 1})" : label,
                CustomMinimumSize = new Vector2(350, 36),
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            };
            int capturedSlot = slot.Slot; // capture for lambda
            bool isEmpty = slot.IsEmpty;
            loadBtn.Pressed += () => OnSlotSelected(capturedSlot, isEmpty);
            hbox.AddChild(loadBtn);

            // Delete button for non-empty slots
            if (!slot.IsEmpty)
            {
                var delBtn = new Button
                {
                    Text = "X",
                    CustomMinimumSize = new Vector2(36, 36),
                };
                delBtn.Pressed += () =>
                {
                    SaveManager.DeleteSlot(capturedSlot);
                    _selectedSlot = -1;
                    RefreshSlotButtons();
                    UpdateStartButton();
                };
                hbox.AddChild(delBtn);
            }

            _slotContainer.AddChild(hbox);
        }
    }

    private void RefreshCompanionList()
    {
        if (_companionContainer == null) return;

        foreach (var child in _companionContainer.GetChildren())
            child.QueueFree();

        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            var stub = new Label
            {
                Text = "Select a save slot to see companions.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stub.Modulate = new Color(0.7f, 0.7f, 0.7f);
            _companionContainer.AddChild(stub);
            return;
        }

        // Header — current party size
        var partyLabel = new Label
        {
            Text = $"Active party: {save.ActivePartyCompanionIds.Count} / {save.MaxPartySize}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        partyLabel.AddThemeFontSizeOverride("font_size", 14);
        _companionContainer.AddChild(partyLabel);

        bool anyShown = false;
        foreach (var c in save.Companions)
        {
            if (!c.IsAvailable && !c.IsRecruited) continue;
            if (c.IsPermadead) continue;
            anyShown = true;

            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);
            hbox.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;

            string status;
            if (c.IsRecruited)
            {
                bool inParty = save.ActivePartyCompanionIds.Contains(c.Id);
                status = inParty ? "[PARTY]" : "[ROSTER]";
            }
            else
            {
                status = $"[{c.RecruitmentCost}g]";
            }

            var info = new Label
            {
                Text = $"{c.Name} ({c.School}) {status}",
                CustomMinimumSize = new Vector2(280, 28),
            };
            hbox.AddChild(info);

            string capturedId = c.Id;
            var btn = new Button { CustomMinimumSize = new Vector2(110, 28) };

            if (!c.IsRecruited)
            {
                btn.Text = "Recruit";
                btn.Disabled = save.Gold < c.RecruitmentCost;
                btn.Pressed += () =>
                {
                    if (CompanionRoster.TryRecruit(capturedId))
                    {
                        RefreshSlotButtons();   // gold changed
                        RefreshCompanionList();
                    }
                };
            }
            else if (save.ActivePartyCompanionIds.Contains(c.Id))
            {
                btn.Text = "Remove";
                btn.Pressed += () =>
                {
                    CompanionRoster.RemoveFromParty(capturedId);
                    RefreshCompanionList();
                };
            }
            else
            {
                btn.Text = "Add to Party";
                btn.Disabled = save.ActivePartyCompanionIds.Count >= save.MaxPartySize;
                btn.Pressed += () =>
                {
                    if (CompanionRoster.TryAddToParty(capturedId))
                        RefreshCompanionList();
                };
            }
            hbox.AddChild(btn);

            _companionContainer.AddChild(hbox);
        }

        if (!anyShown)
        {
            var stub = new Label
            {
                Text = "No companions available yet.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stub.Modulate = new Color(0.7f, 0.7f, 0.7f);
            _companionContainer.AddChild(stub);
        }
    }

    private void OnSlotSelected(int slot, bool isEmpty)
    {
        if (isEmpty)
        {
            var school = (CardSchool)_schoolPicker.GetSelectedId();
            SaveManager.NewGame(slot, $"Guild of {school}");
            SaveManager.ActiveSave.SelectedSchool = school.ToString();
            SaveManager.Save();
        }
        else
        {
            SaveManager.Load(slot);
            if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave.SelectedSchool, out var school))
            {
                _schoolPicker.Selected = (int)school;
                UpdateSchoolDescription();
            }
        }

        _selectedSlot = slot;

        // Make sure the loaded/new save has all current companion templates
        if (SaveManager.ActiveSave != null)
            CompanionRoster.EnsureRoster(SaveManager.ActiveSave);

        RefreshSlotButtons();
        RefreshCompanionList();
        UpdateStartButton();
        GD.Print($"Selected slot {slot}");
    }

    private void UpdateStartButton()
    {
        _startRunButton.Disabled = _selectedSlot < 0;
        _startRunButton.Text = _selectedSlot >= 0
            ? "Begin Expedition"
            : "Select a save slot first";
    }

}
