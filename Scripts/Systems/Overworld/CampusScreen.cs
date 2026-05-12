using Godot;
using System;
using System.Collections.Generic;

public partial class CampusScreen : Control
{
    private int _selectedSlot = -1;
    private int _activeTab = 0;

    // Tab buttons
    private Button[] _tabButtons;

    // Tab content panels (one per tab, shown/hidden)
    private Control[] _tabPanels;

    // Guild tab nodes
    private VBoxContainer _slotContainer;
    private Label _summaryLabel;
    private OptionButton _schoolPicker;
    private Label _schoolDescription;
    private CheckBox _debugCheckbox;
    private PanelContainer _debugPanel;
    private OptionButton _forceEncounterDropdown;
    private Button _startRunButton;
    private Button _cardLibraryButton;

    // Companions tab
    private VBoxContainer _companionContainer;

    // Campus tab
    private VBoxContainer _buildingContainer;

    private static readonly Dictionary<CardSchool, string> SchoolDescriptions = new()
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
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");

        // Fill the viewport
        SetAnchorsPreset(LayoutPreset.FullRect);

        // Background
        var bg = new ColorRect { Color = new Color(0.09f, 0.08f, 0.13f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Title bar — fixed 60px at top
        var titleBar = new Panel();
        titleBar.SetAnchorsPreset(LayoutPreset.TopWide);
        titleBar.OffsetBottom = 60;
        var titleStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.11f, 0.20f),
            BorderColor = new Color(0.35f, 0.28f, 0.55f),
            BorderWidthBottom = 2,
        };
        titleBar.AddThemeStyleboxOverride("panel", titleStyle);
        AddChild(titleBar);

        var titleLbl = new Label
        {
            Text = "Guild Campus",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleLbl.SetAnchorsPreset(LayoutPreset.FullRect);
        titleLbl.AddThemeFontSizeOverride("font_size", 30);
        titleLbl.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.6f));
        titleBar.AddChild(titleLbl);

        // Tab button bar — fixed 44px below title
        var tabBar = new HBoxContainer();
        tabBar.SetAnchorsPreset(LayoutPreset.TopWide);
        tabBar.OffsetTop = 60;
        tabBar.OffsetBottom = 104;
        tabBar.AddThemeConstantOverride("separation", 0);
        AddChild(tabBar);

        string[] tabNames = { "Guild", "Companions", "Campus", "Expedition" };
        _tabButtons = new Button[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            var btn = new Button
            {
                Text = tabNames[i],
                ToggleMode = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 44),
            };
            btn.AddThemeFontSizeOverride("font_size", 16);
            int captured = i;
            btn.Pressed += () => SelectTab(captured);
            _tabButtons[i] = btn;
            tabBar.AddChild(btn);
        }

        // Content area — fills from below tab bar to bottom
        _tabPanels = new Control[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            var panel = new ScrollContainer();
            panel.SetAnchorsPreset(LayoutPreset.FullRect);
            panel.OffsetTop = 104;
            panel.Visible = false;
            panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            panel.SizeFlagsVertical = SizeFlags.ExpandFill;
            AddChild(panel);
            _tabPanels[i] = panel;
        }

        // Build tab content
        BuildGuildTab((ScrollContainer)_tabPanels[0]);
        BuildCompanionsTab((ScrollContainer)_tabPanels[1]);
        BuildCampusTab((ScrollContainer)_tabPanels[2]);
        BuildExpeditionTab((ScrollContainer)_tabPanels[3]);

        // Auto-select active slot
        GD.Print($"CampusScreen: ActiveSave={SaveManager.ActiveSave?.GuildName ?? "NULL"}, " +
                 $"Gold={SaveManager.ActiveSave?.Gold ?? -1}, " +
                 $"Runs={SaveManager.ActiveSave?.TotalRuns ?? -1}");

        if (SaveManager.ActiveSave != null && SaveManager.ActiveSlot >= 0)
        {
            _selectedSlot = SaveManager.ActiveSlot;
            EnsureRostersAndBuildings();
            if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave.SelectedSchool, out var school))
            {
                _schoolPicker.Selected = (int)school;
                UpdateSchoolDescription();
            }
        }

        RefreshAll();
        UpdateStartButton();
        SelectTab(0);
    }

    private void SelectTab(int index)
    {
        _activeTab = index;
        for (int i = 0; i < _tabPanels.Length; i++)
        {
            _tabPanels[i].Visible = (i == index);
            _tabButtons[i].ButtonPressed = (i == index);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tab content builders
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildGuildTab(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(14);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Save Slots");
        _slotContainer = MakeVBox(8);
        layout.AddChild(_slotContainer);
        layout.AddChild(new HSeparator());

        _summaryLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _summaryLabel.AddThemeFontSizeOverride("font_size", 15);
        layout.AddChild(_summaryLabel);

        if (RunResultData.HasResults)
        {
            string outcome = RunResultData.ReachedObjective ? "✓ SUCCESS" : "✗ FAILED";
            _summaryLabel.Text = $"Last Run: {outcome}  |  " +
                                 $"Gold: {RunResultData.GoldEarned}  |  " +
                                 $"Encounters: {RunResultData.EncountersWon}  |  " +
                                 $"HP: {RunResultData.HPRemaining}";
            _summaryLabel.Modulate = RunResultData.ReachedObjective
                ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f);
            RunResultData.Clear();
        }
        else
        {
            _summaryLabel.Text = "No expeditions yet. The wilds await.";
            _summaryLabel.Modulate = new Color(0.6f, 0.6f, 0.6f);
        }

        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Wizard School");

        _schoolPicker = new OptionButton
        {
            CustomMinimumSize = new Vector2(320, 40),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        foreach (CardSchool school in Enum.GetValues(typeof(CardSchool)))
            _schoolPicker.AddItem(school.ToString(), (int)school);
        _schoolPicker.Selected = (int)PlayerSession.SelectedSchool;
        _schoolPicker.ItemSelected += (_) => UpdateSchoolDescription();
        layout.AddChild(_schoolPicker);

        _schoolDescription = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 48),
        };
        _schoolDescription.AddThemeFontSizeOverride("font_size", 13);
        _schoolDescription.Modulate = new Color(0.8f, 0.8f, 0.85f);
        layout.AddChild(_schoolDescription);
        UpdateSchoolDescription();

        layout.AddChild(new HSeparator());

        _debugCheckbox = new CheckBox
        {
            Text = "Debug Mode",
            ButtonPressed = PlayerSession.DebugMode,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        layout.AddChild(_debugCheckbox);

        _debugPanel = BuildDebugPanel();
        _debugPanel.Visible = PlayerSession.DebugMode;
        layout.AddChild(_debugPanel);

        _debugCheckbox.Toggled += (on) =>
        {
            PlayerSession.DebugMode = on;
            _debugPanel.Visible = on;
            if (!on)
            {
                PlayerSession.NoFog = false;
                PlayerSession.UnlimitedSteps = false;
                PlayerSession.GodModeHP = false;
                PlayerSession.StartWithGold = false;
                PlayerSession.SkipDeployment = false;
                PlayerSession.ForceNextEncounterType = -1;
            }
        };

        layout.AddChild(new HSeparator());

        _startRunButton = MakeButton("Begin Expedition", 280, 52, 20);
        _startRunButton.Pressed += OnStartRun;
        layout.AddChild(_startRunButton);

        _cardLibraryButton = MakeButton("Card Library", 280, 40, 16);
        _cardLibraryButton.Pressed += () =>
            GetTree().ChangeSceneToFile("res://Scenes/UI/CardLibrary.tscn");
        layout.AddChild(_cardLibraryButton);

        var quitBtn = MakeButton("Quit", 280, 36, 15);
        quitBtn.Pressed += () => GetTree().Quit();
        layout.AddChild(quitBtn);
    }

    private void BuildCompanionsTab(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Companion Roster");

        var note = new Label
        {
            Text = "Recruit companions to bring on expeditions. Active party members " +
                   "contribute cards to your deck and tokens to negotiations.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", 13);
        note.Modulate = new Color(0.7f, 0.7f, 0.75f);
        layout.AddChild(note);
        layout.AddChild(new HSeparator());

        _companionContainer = MakeVBox(8);
        layout.AddChild(_companionContainer);
    }

    private void BuildCampusTab(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Campus Buildings");

        var note = new Label
        {
            Text = "Construct and upgrade buildings to gain permanent bonuses across all runs.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", 13);
        note.Modulate = new Color(0.7f, 0.7f, 0.75f);
        layout.AddChild(note);
        layout.AddChild(new HSeparator());

        _buildingContainer = MakeVBox(10);
        layout.AddChild(_buildingContainer);
    }

    private void BuildExpeditionTab(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(14);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Choose Destination");

        var stub = new Label
        {
            Text = "Region selection coming in Phase 3.\n\nCurrently exploring: Frontier Wilds.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stub.AddThemeFontSizeOverride("font_size", 16);
        stub.Modulate = new Color(0.6f, 0.6f, 0.65f);
        layout.AddChild(stub);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Debug panel
    // ═══════════════════════════════════════════════════════════════════════

    private PanelContainer BuildDebugPanel()
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.15f, 0.10f, 0.10f),
            BorderColor = new Color(0.6f, 0.3f, 0.3f),
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        panel.AddThemeStyleboxOverride("stylebox", style);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 20);
        grid.AddThemeConstantOverride("v_separation", 6);
        panel.AddChild(grid);

        CheckBox MakeDebugCheck(string label, bool current, Action<bool> onChange)
        {
            var cb = new CheckBox { Text = label, ButtonPressed = current };
            cb.AddThemeFontSizeOverride("font_size", 13);
            cb.Toggled += (on) => onChange(on);
            return cb;
        }

        grid.AddChild(MakeDebugCheck("No Fog of War",   PlayerSession.NoFog,
            on => PlayerSession.NoFog = on));
        grid.AddChild(MakeDebugCheck("Unlimited Steps", PlayerSession.UnlimitedSteps,
            on => PlayerSession.UnlimitedSteps = on));
        grid.AddChild(MakeDebugCheck("God Mode HP",     PlayerSession.GodModeHP,
            on => PlayerSession.GodModeHP = on));
        grid.AddChild(MakeDebugCheck("Start With Gold", PlayerSession.StartWithGold,
            on => PlayerSession.StartWithGold = on));
        grid.AddChild(MakeDebugCheck("Skip Deployment", PlayerSession.SkipDeployment,
            on => PlayerSession.SkipDeployment = on));

        var forceLabel = new Label { Text = "Force Next POI:" };
        forceLabel.AddThemeFontSizeOverride("font_size", 13);
        grid.AddChild(forceLabel);

        _forceEncounterDropdown = new OptionButton { CustomMinimumSize = new Vector2(140, 28) };
        _forceEncounterDropdown.AddThemeFontSizeOverride("font_size", 13);
        _forceEncounterDropdown.AddItem("None (normal)", -1);
        _forceEncounterDropdown.AddItem("Combat",        (int)OverworldHex.POIType.Combat);
        _forceEncounterDropdown.AddItem("Rest",          (int)OverworldHex.POIType.Rest);
        _forceEncounterDropdown.AddItem("Narrative",     (int)OverworldHex.POIType.Narrative);
        _forceEncounterDropdown.AddItem("Negotiation",   (int)OverworldHex.POIType.Negotiation);
        _forceEncounterDropdown.Selected = 0;
        _forceEncounterDropdown.ItemSelected += (idx) =>
            PlayerSession.ForceNextEncounterType =
                _forceEncounterDropdown.GetItemId((int)idx);
        grid.AddChild(_forceEncounterDropdown);

        return panel;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Refresh methods (unchanged from before)
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshAll()
    {
        RefreshSlotButtons();
        RefreshCompanionList();
        RefreshBuildingList();
    }

    private void RefreshSlotButtons()
    {
        if (_slotContainer == null) return;
        foreach (var child in _slotContainer.GetChildren()) child.QueueFree();

        var slots = SaveManager.GetAllSlotInfo();
        foreach (var slot in slots)
        {
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 10);

            string label = slot.IsEmpty
                ? $"Slot {slot.Slot + 1}: Empty"
                : $"Slot {slot.Slot + 1}: {slot.GuildName} ({slot.School})" +
                  $"  |  Gold: {slot.Gold}  |  Runs: {slot.TotalRuns}";

            var loadBtn = new Button
            {
                Text = slot.IsEmpty ? $"New Game (Slot {slot.Slot + 1})" : label,
                CustomMinimumSize = new Vector2(360, 36),
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            };
            if (slot.Slot == _selectedSlot)
                loadBtn.Modulate = new Color(0.7f, 1f, 0.7f);

            int capturedSlot = slot.Slot;
            bool isEmpty = slot.IsEmpty;
            loadBtn.Pressed += () => OnSlotSelected(capturedSlot, isEmpty);
            hbox.AddChild(loadBtn);

            if (!slot.IsEmpty)
            {
                var delBtn = new Button { Text = "✕", CustomMinimumSize = new Vector2(36, 36) };
                delBtn.Pressed += () =>
                {
                    SaveManager.DeleteSlot(capturedSlot);
                    _selectedSlot = -1;
                    RefreshAll();
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
        foreach (var child in _companionContainer.GetChildren()) child.QueueFree();

        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            _companionContainer.AddChild(MakeStubLabel("Select a save slot to see companions."));
            return;
        }

        var partyHeader = new Label
        {
            Text = $"Active party: {save.ActivePartyCompanionIds.Count} / {save.MaxPartySize}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        partyHeader.AddThemeFontSizeOverride("font_size", 14);
        _companionContainer.AddChild(partyHeader);

        bool anyShown = false;
        foreach (var c in save.Companions)
        {
            if (!c.IsAvailable && !c.IsRecruited) continue;
            if (c.IsPermadead) continue;
            anyShown = true;

            var card = new PanelContainer();
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.12f, 0.11f, 0.18f),
                BorderColor = c.IsRecruited ? new Color(0.4f, 0.7f, 0.4f) : new Color(0.35f, 0.35f, 0.45f),
                BorderWidthTop = 1, BorderWidthBottom = 1,
                BorderWidthLeft = 1, BorderWidthRight = 1,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
                ContentMarginLeft = 10, ContentMarginRight = 10,
                ContentMarginTop = 8, ContentMarginBottom = 8,
            };
            card.AddThemeStyleboxOverride("stylebox", cardStyle);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            card.AddChild(row);

            var info = MakeVBox(2);
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            bool inParty = save.ActivePartyCompanionIds.Contains(c.Id);
            string badge = c.IsRecruited ? (inParty ? "  [PARTY]" : "  [ROSTER]") : $"  [{c.RecruitmentCost}g]";

            var nameLabel = new Label { Text = $"{c.Name}{badge}" };
            nameLabel.AddThemeFontSizeOverride("font_size", 15);
            info.AddChild(nameLabel);

            var subLabel = new Label { Text = $"{c.School}  ·  {c.PersonalityTrait}  ·  Loyalty: {c.Loyalty}" };
            subLabel.AddThemeFontSizeOverride("font_size", 12);
            subLabel.Modulate = new Color(0.7f, 0.7f, 0.75f);
            info.AddChild(subLabel);
            row.AddChild(info);

            string capturedId = c.Id;
            var btn = new Button { CustomMinimumSize = new Vector2(120, 32) };
            btn.AddThemeFontSizeOverride("font_size", 13);

            if (!c.IsRecruited)
            {
                btn.Text = $"Recruit ({c.RecruitmentCost}g)";
                btn.Disabled = save.Gold < c.RecruitmentCost;
                btn.Pressed += () => { if (CompanionRoster.TryRecruit(capturedId)) RefreshAll(); };
            }
            else if (inParty)
            {
                btn.Text = "Remove";
                btn.Pressed += () => { CompanionRoster.RemoveFromParty(capturedId); RefreshCompanionList(); };
            }
            else
            {
                btn.Text = "Add to Party";
                btn.Disabled = save.ActivePartyCompanionIds.Count >= save.MaxPartySize;
                btn.Pressed += () => { if (CompanionRoster.TryAddToParty(capturedId)) RefreshCompanionList(); };
            }
            row.AddChild(btn);
            _companionContainer.AddChild(card);
        }

        if (!anyShown)
            _companionContainer.AddChild(MakeStubLabel("No companions available yet."));
    }

    private void RefreshBuildingList()
    {
        if (_buildingContainer == null) return;
        foreach (var child in _buildingContainer.GetChildren()) child.QueueFree();

        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            _buildingContainer.AddChild(MakeStubLabel("Select a save slot to see buildings."));
            return;
        }

        foreach (var buildingSave in save.Buildings)
        {
            var template = BuildingDatabase.GetTemplate(buildingSave.Id);
            if (template == null) continue;

            var card = new PanelContainer();
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.11f, 0.10f, 0.17f),
                BorderColor = buildingSave.Tier > 0 ? new Color(0.55f, 0.45f, 0.75f) : new Color(0.30f, 0.30f, 0.40f),
                BorderWidthTop = 1, BorderWidthBottom = 1,
                BorderWidthLeft = 1, BorderWidthRight = 1,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
                ContentMarginLeft = 12, ContentMarginRight = 12,
                ContentMarginTop = 10, ContentMarginBottom = 10,
            };
            card.AddThemeStyleboxOverride("stylebox", cardStyle);

            var cardLayout = MakeVBox(4);
            card.AddChild(cardLayout);

            var headerRow = new HBoxContainer();
            headerRow.AddThemeConstantOverride("separation", 12);
            cardLayout.AddChild(headerRow);

            var nameCol = MakeVBox(2);
            nameCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            string tierText = buildingSave.Tier == 0 ? "Not Built" : $"Tier {buildingSave.Tier} / {template.MaxTier}";
            var nameLabel = new Label { Text = $"{buildingSave.Name}  [{tierText}]" };
            nameLabel.AddThemeFontSizeOverride("font_size", 16);
            nameCol.AddChild(nameLabel);

            var catLabel = new Label
            {
                Text = template.Category + (string.IsNullOrEmpty(template.SchoolAffinity) ? "" : $"  ·  {template.SchoolAffinity}")
            };
            catLabel.AddThemeFontSizeOverride("font_size", 12);
            catLabel.Modulate = new Color(0.6f, 0.6f, 0.7f);
            nameCol.AddChild(catLabel);
            headerRow.AddChild(nameCol);

            int nextTier = buildingSave.Tier + 1;
            if (nextTier <= template.MaxTier)
            {
                var tierData = template.Tiers.Find(t => t.Tier == nextTier);
                int cost = tierData?.GoldCost ?? 0;
                var btn = new Button
                {
                    Text = buildingSave.Tier == 0 ? $"Build\n{cost}g" : $"Upgrade\n{cost}g",
                    CustomMinimumSize = new Vector2(90, 44),
                    Disabled = save.Gold < cost,
                };
                btn.AddThemeFontSizeOverride("font_size", 13);
                string capturedId = buildingSave.Id;
                btn.Pressed += () => { if (TryBuildOrUpgrade(capturedId)) RefreshAll(); };
                headerRow.AddChild(btn);
            }
            else
            {
                var maxLabel = new Label { Text = "MAX" };
                maxLabel.AddThemeFontSizeOverride("font_size", 13);
                maxLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.9f, 0.5f));
                headerRow.AddChild(maxLabel);
            }

            if (buildingSave.Tier > 0)
            {
                var cur = template.Tiers.Find(t => t.Tier == buildingSave.Tier);
                if (cur != null)
                {
                    var lbl = new Label { Text = $"Active: {cur.Description}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    lbl.AddThemeFontSizeOverride("font_size", 13);
                    lbl.AddThemeColorOverride("font_color", new Color(0.6f, 0.9f, 0.6f));
                    cardLayout.AddChild(lbl);
                }
            }

            if (nextTier <= template.MaxTier)
            {
                var next = template.Tiers.Find(t => t.Tier == nextTier);
                if (next != null)
                {
                    var lbl = new Label { Text = $"Next: {next.Description}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    lbl.AddThemeFontSizeOverride("font_size", 12);
                    lbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.75f));
                    cardLayout.AddChild(lbl);
                }
            }

            _buildingContainer.AddChild(card);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Actions
    // ═══════════════════════════════════════════════════════════════════════

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
        EnsureRostersAndBuildings();
        RefreshAll();
        UpdateStartButton();
        GD.Print($"Selected slot {slot}");
    }

    private void OnStartRun()
    {
        if (_selectedSlot < 0) return;
        PlayerSession.SelectedSchool = (CardSchool)_schoolPicker.GetSelectedId();
        PlayerSession.DebugMode = _debugCheckbox.ButtonPressed;
        if (SaveManager.ActiveSave != null)
        {
            SaveManager.ActiveSave.SelectedSchool = PlayerSession.SelectedSchool.ToString();
            SaveManager.Save();
        }
        GD.Print($"Starting run: {PlayerSession.SelectedSchool}, Debug: {PlayerSession.DebugMode}");
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/OverworldScene.tscn");
    }

    private bool TryBuildOrUpgrade(string buildingId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return false;
        var template = BuildingDatabase.GetTemplate(buildingId);
        if (template == null) return false;

        BuildingSaveData buildingSave = null;
        foreach (var b in save.Buildings)
            if (b.Id == buildingId) { buildingSave = b; break; }
        if (buildingSave == null) return false;

        int nextTier = buildingSave.Tier + 1;
        if (nextTier > template.MaxTier) return false;
        var tierData = template.Tiers.Find(t => t.Tier == nextTier);
        if (tierData == null || save.Gold < tierData.GoldCost) return false;

        foreach (var reqId in tierData.RequiredBuildings)
        {
            bool found = false;
            foreach (var b in save.Buildings)
                if (b.Id == reqId && b.Tier > 0) { found = true; break; }
            if (!found) return false;
        }

        save.Gold -= tierData.GoldCost;
        buildingSave.Tier = nextTier;
        SaveManager.Save();
        GD.Print($"Built {buildingSave.Name} tier {nextTier}. Gold: {save.Gold}");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private void UpdateSchoolDescription()
    {
        if (_schoolPicker == null || _schoolDescription == null) return;
        var school = (CardSchool)_schoolPicker.GetSelectedId();
        int count = 0;
        foreach (var bp in CardDatabase.Blueprints)
            if (bp.School == school) count++;
        string desc = SchoolDescriptions.TryGetValue(school, out var d) ? d : "";
        _schoolDescription.Text = $"{desc}\n{count} cards available.";
    }

    private void UpdateStartButton()
    {
        if (_startRunButton == null) return;
        _startRunButton.Disabled = _selectedSlot < 0;
        _startRunButton.Text = _selectedSlot >= 0 ? "Begin Expedition" : "Select a save slot first";
    }

    private void EnsureRostersAndBuildings()
    {
        if (SaveManager.ActiveSave == null) return;
        CompanionRoster.EnsureRoster(SaveManager.ActiveSave);
        BuildingDatabase.EnsureBuildings(SaveManager.ActiveSave);
    }

    private void AddSectionHeader(VBoxContainer parent, string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 20);
        label.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.6f));
        parent.AddChild(label);
    }

    private VBoxContainer MakeVBox(int separation)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", separation);
        return v;
    }

    private MarginContainer MakeMargins(int horizontal, int vertical)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", horizontal);
        m.AddThemeConstantOverride("margin_right", horizontal);
        m.AddThemeConstantOverride("margin_top", vertical);
        m.AddThemeConstantOverride("margin_bottom", vertical);
        m.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        m.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        return m;
    }

    private Button MakeButton(string text, float minWidth, float minHeight, int fontSize)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(minWidth, minHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        return btn;
    }

    private Label MakeStubLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.Modulate = new Color(0.6f, 0.6f, 0.65f);
        return label;
    }
}