using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CampusScreen.cs
//
// Purpose:        The persistent between-runs campus hub.
//                 Tabbed UI hosting guild (save slots + school
//                 picker + start-run), companions, buildings,
//                 armory, training tabs. Builds every visible
//                 widget in code (no .tscn UI), reads/writes
//                 GuildSaveData via SaveManager.
// Layer:          UI
// Collaborators:  SaveManager.cs, CompanionRoster.cs,
//                 BuildingDatabase.cs, ItemDatabase.cs,
//                 EquipmentLoadout.cs, PlayerSession.cs,
//                 UITheme.cs (extensive — every panel/button)
// See:            README §3 — Campus is the persistence layer
//                 between runs; touches almost every save field
// ============================================================

/// <summary>Persistent between-runs hub. Hosts five tabs (Guild, Companions, Buildings, Armory, Training) and the start-run button. Reads/writes the active save through <see cref="SaveManager"/>. Massive file — see the section banners inside for the tab-by-tab layout.</summary>
public partial class CampusScreen : Control
{
    private int _selectedSlot = -1;
    private int _activeTab = 0;

    private Button[] _tabButtons;
    private Control[] _tabPanels;

    // Guild tab
    private Label _goldLabel;
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
    private VBoxContainer _buildingContainer;

    // Armory tab
    private VBoxContainer _armoryContainer;
    private string _selectedArmoryUnitId = null;   // which unit we're equipping
    private string _armorySlotFilter = "All"; // "All", "Weapon", "Armor", "Trinket"

    // Training tab
    private VBoxContainer _trainingContainer;
    private string _selectedTrainingCompanionId = null;

    private static readonly Dictionary<CardSchool, string> SchoolDescriptions = new()
    {
        { CardSchool.Arcanist,     "Masters of raw magic. High damage spells and mana manipulation." },
        { CardSchool.Elementalist, "Controls terrain with fire, ice, and storm effects." },
        { CardSchool.Necromancer,  "Summons minions and drains life from enemies." },
        { CardSchool.Enchanter,    "Buffs, debuffs, and tile enchantments." },
        { CardSchool.Tinker,       "Mechanical traps, turrets, and area control." },
        { CardSchool.Generic,      "Academy trained magical initiates at their finest." },
    };

    public override void _Ready()
    {
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");
        CallDeferred(nameof(BuildUI));
    }

    private void BuildUI()
    {
        // Background
        var bg = new ColorRect { Color = UITheme.CampusBg };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Title bar
        var titleBar = new Panel();
        titleBar.SetAnchorsPreset(LayoutPreset.TopWide);
        titleBar.OffsetBottom = 60;
        var titleStyle = new StyleBoxFlat
        {
            BgColor = UITheme.CampusTitleBarBg,
            BorderColor = UITheme.CampusTitleBarBorder,
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
        titleLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        titleLbl.AddThemeColorOverride("font_color", UITheme.CampusTitleColor);
        titleBar.AddChild(titleLbl);

        // Gold label
        _goldLabel = new Label();
        _goldLabel.Name = "GoldLabel";
        _goldLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _goldLabel.VerticalAlignment = VerticalAlignment.Center;
        _goldLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _goldLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f)); // gold color
        _goldLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        _goldLabel.OffsetRight = -16; // right margin
        titleBar.AddChild(_goldLabel);

        // Tab bar
        var tabBar = new HBoxContainer();
        tabBar.SetAnchorsPreset(LayoutPreset.TopWide);
        tabBar.OffsetTop = 60;
        tabBar.OffsetBottom = 104;
        tabBar.AddThemeConstantOverride("separation", 0);
        AddChild(tabBar);

        string[] tabNames = { "Guild", "Companions", "Campus", "Expedition", "Armory", "Training" };
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
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusTabFontSize);
            ApplyTabStyle(btn, false);
            int captured = i;
            btn.Pressed += () => SelectTab(captured);
            _tabButtons[i] = btn;
            tabBar.AddChild(btn);
        }

        // Content panels
        _tabPanels = new Control[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            var panel = new ScrollContainer();
            panel.SetAnchorsPreset(LayoutPreset.FullRect);
            panel.OffsetTop = 104;
            panel.Visible = false;
            panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            panel.SizeFlagsVertical = SizeFlags.ExpandFill;

            // Slate background so WorldBase doesn't bleed through
            var panelBg = new StyleBoxFlat { BgColor = UITheme.BgBase };
            panel.AddThemeStyleboxOverride("panel", panelBg);

            AddChild(panel);
            _tabPanels[i] = panel;
        }

        BuildGuildTab((ScrollContainer)_tabPanels[0]);
        BuildCompanionsTab((ScrollContainer)_tabPanels[1]);
        BuildCampusTab((ScrollContainer)_tabPanels[2]);
        BuildExpeditionTab((ScrollContainer)_tabPanels[3]);
        BuildArmoryTab((ScrollContainer)_tabPanels[4]);
        BuildTrainingTab((ScrollContainer)_tabPanels[5]);
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
            ApplyTabStyle(_tabButtons[i], i == index);
        }

        // Refresh the newly visible tab so it always shows current data
        switch (index)
        {
            case 4: RefreshArmoryTab(); break;
            case 5: RefreshTrainingTab(); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tab builders
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
        _summaryLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        layout.AddChild(_summaryLabel);

        if (RunResultData.HasResults)
        {
            string outcome = RunResultData.ReachedObjective ? "✓ SUCCESS" : "✗ FAILED";
            _summaryLabel.Text = $"Last Run: {outcome}  |  " +
                                $"Gold: {RunResultData.GoldEarned}  |  " +
                                $"Splinters: {RunResultData.ArcaneSplinters}  |  " +
                                $"Encounters: {RunResultData.EncountersWon}  |  " +
                                $"HP: {RunResultData.HPRemaining}";
            _summaryLabel.Modulate = RunResultData.ReachedObjective
                ? UITheme.CampusRunSuccess : UITheme.CampusRunFail;
            RunResultData.Clear();
        }
        else
        {
            _summaryLabel.Text = "No expeditions yet. The wilds await.";
            _summaryLabel.Modulate = UITheme.CampusNoRunText;
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
        _schoolDescription.AddThemeFontSizeOverride("font_size", UITheme.CampusSchoolFontSize);
        _schoolDescription.Modulate = UITheme.CampusSchoolDescText;
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

        var deckEditorButton = MakeButton("Manage Deck", 280, 40, 16);
        deckEditorButton.Pressed += () =>
            GetTree().ChangeSceneToFile("res://Scenes/UI/DeckEditor.tscn");
        layout.AddChild(deckEditorButton);

        var upgradeBtn = MakeButton("Upgrade Cards", 280, 40, 16);
        upgradeBtn.Pressed += () =>
            GetTree().ChangeSceneToFile("res://Scenes/UI/CardUpgradeScreen.tscn");
        layout.AddChild(upgradeBtn);

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
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.Modulate = UITheme.CampusSubtleText;
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
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.Modulate = UITheme.CampusSubtleText;
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
        stub.AddThemeFontSizeOverride("font_size", UITheme.CampusTabFontSize);
        stub.Modulate = UITheme.CampusStubText;
        layout.AddChild(stub);
    }

    private void BuildArmoryTab(ScrollContainer scroll)
    {
        // EnsureStarterItems removed — now called from OnSlotSelected
        var outer = MakeMargins(20, 16);
        scroll.AddChild(outer);

        _armoryContainer = MakeVBox(12);
        _armoryContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        outer.AddChild(_armoryContainer);

        RefreshArmoryTab();
    }

    private void BuildTrainingTab(ScrollContainer scroll)
    {
        var outer = MakeMargins(20, 16);
        scroll.AddChild(outer);

        _trainingContainer = MakeVBox(12);
        _trainingContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        outer.AddChild(_trainingContainer);

        RefreshTrainingTab();
    }
    // ═══════════════════════════════════════════════════════════════════════
    // Armory Tab
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshArmoryTab()
    {
        if (_armoryContainer == null) return;

        foreach (Node child in _armoryContainer.GetChildren())
            child.QueueFree();

        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            _armoryContainer.AddChild(MakeStubLabel("No save loaded."));
            return;
        }

        RefreshGoldLabel();
        ItemDatabase.LoadAll();

        // ── Unit selector ────────────────────────────────────────────────
        AddSectionHeader(_armoryContainer, "Equip To");
        BuildUnitSelector(save);

        // ── Currently equipped ───────────────────────────────────────────
        if (_selectedArmoryUnitId != null)
        {
            AddSectionHeader(_armoryContainer, "Equipped");
            BuildEquippedPanel(save);
        }

        // ── Unequipped items ─────────────────────────────────────────────
        AddSectionHeader(_armoryContainer, "Armory");
        BuildUnequippedPanel(save);
    }

    // ── Unit selector row ────────────────────────────────────────────────

    private void BuildUnitSelector(GuildSaveData save)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _armoryContainer.AddChild(row);

        // Wizard button (always present)
        AddUnitSelectorButton(row, "wizard", "Wizard", UITheme.Violet);

        // Active party companions
        foreach (var companionId in save.ActivePartyCompanionIds)
        {
            var companion = save.Companions.Find(c => c.Id == companionId);
            if (companion == null || companion.IsPermadead) continue;

            AddUnitSelectorButton(row, companion.Id, companion.Name, UITheme.Success);
        }
    }

    private void AddUnitSelectorButton(HBoxContainer row, string unitId, string label, Color accentColor)
    {
        bool isSelected = _selectedArmoryUnitId == unitId;

        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            ButtonPressed = isSelected,
            CustomMinimumSize = new Vector2(120, 36),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);

        if (isSelected)
            btn.AddThemeColorOverride("font_color", accentColor);

        string captured = unitId;
        btn.Pressed += () =>
        {
            _selectedArmoryUnitId = captured;
            _armorySlotFilter = "All"; // reset filter on unit switch
            RefreshArmoryTab();
        };

        row.AddChild(btn);
    }

    // ── Equipped panel ───────────────────────────────────────────────────

    private void BuildEquippedPanel(GuildSaveData save)
    {
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", UITheme.PaddingNormal);
        grid.AddThemeConstantOverride("v_separation", UITheme.PaddingNormal);
        grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _armoryContainer.AddChild(grid);

        foreach (EquipmentSlot slot in System.Enum.GetValues(typeof(EquipmentSlot)))
        {
            var loadout = save.Armory.GetLoadout(_selectedArmoryUnitId);
            var instanceId = loadout.GetSlot(slot);
            var item = instanceId != null ? save.Armory.GetInstance(instanceId) : null;

            var card = BuildItemSlotCard(slot, item, save);
            grid.AddChild(card);
        }
    }

    private Control BuildItemSlotCard(EquipmentSlot slot, ItemInstance item, GuildSaveData save)
    {
        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(180, 90);

        var style = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,
            BorderColor = item != null ? UITheme.RarityColor(item.Rarity) : UITheme.Neutral,
            CornerRadiusTopLeft = UITheme.CornerRadius - 1,
            CornerRadiusTopRight = UITheme.CornerRadius - 1,
            CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
            CornerRadiusBottomRight = UITheme.CornerRadius - 1,
            BorderWidthTop = UITheme.BorderWidth - 1,
            BorderWidthBottom = UITheme.BorderWidth - 1,
            BorderWidthLeft = UITheme.BorderWidth - 1,
            BorderWidthRight = UITheme.BorderWidth - 1,
            ContentMarginLeft = UITheme.PaddingNormal + 2,
            ContentMarginRight = UITheme.PaddingNormal + 2,
            ContentMarginTop = UITheme.PaddingNormal,
            ContentMarginBottom = UITheme.PaddingNormal,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = MakeVBox(4);
        panel.AddChild(vbox);

        // Slot label
        var slotLbl = new Label { Text = slot.ToString().ToUpper() };
        slotLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        slotLbl.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        vbox.AddChild(slotLbl);

        if (item != null)
        {
            // Item name
            var nameLbl = new Label { Text = item.Name };
            nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            nameLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(item.Rarity));
            nameLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            vbox.AddChild(nameLbl);

            // Stats summary
            var def = ItemDatabase.Get(item.DefinitionId);
            if (def != null)
            {
                var statsLbl = new Label { Text = BuildStatSummary(def) };
                statsLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                statsLbl.AddThemeColorOverride("font_color", UITheme.TextOnLight);
                statsLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                vbox.AddChild(statsLbl);
            }

            // Unequip button
            var unequipBtn = new Button
            {
                Text = "Unequip",
                CustomMinimumSize = new Vector2(0, 24),
            };
            unequipBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            EquipmentSlot capturedSlot = slot;
            unequipBtn.Pressed += () =>
            {
                save.Armory.Unequip(_selectedArmoryUnitId, capturedSlot);
                SaveManager.Save();
                RefreshArmoryTab();
            };
            vbox.AddChild(unequipBtn);
        }
        else
        {
            var emptyLbl = new Label { Text = "— Empty —" };
            emptyLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            emptyLbl.AddThemeColorOverride("font_color", UITheme.TextDim);
            vbox.AddChild(emptyLbl);
        }

        return panel;
    }

    // ── Unequipped items list ─────────────────────────────────────────────

    private void BuildUnequippedPanel(GuildSaveData save)
    {
        var allUnequipped = save.Armory.GetUnequipped();

        if (allUnequipped.Count == 0)
        {
            _armoryContainer.AddChild(MakeStubLabel("All items are equipped."));
            return;
        }

        // ── Filter bar ────────────────────────────────────────────────
        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 4);
        _armoryContainer.AddChild(filterRow);

        foreach (var filterName in new[] { "All", "Weapon", "Armor", "Trinket" })
        {
            bool isActive = _armorySlotFilter == filterName;
            var filterBtn = new Button
            {
                Text = filterName,
                ToggleMode = true,
                ButtonPressed = isActive,
                CustomMinimumSize = new Vector2(80, 28),
            };
            filterBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            ApplyTabStyle(filterBtn, isActive);

            string captured = filterName;
            filterBtn.Pressed += () =>
            {
                _armorySlotFilter = captured;
                RefreshArmoryTab();
            };
            filterRow.AddChild(filterBtn);
        }

        // ── Filtered list ─────────────────────────────────────────────
        var filtered = _armorySlotFilter == "All"
            ? allUnequipped
            : allUnequipped.FindAll(i => i.Slot == _armorySlotFilter);

        if (filtered.Count == 0)
        {
            _armoryContainer.AddChild(MakeStubLabel($"No {_armorySlotFilter} items in armory."));
            return;
        }

        var countLbl = new Label
        {
            Text = _armorySlotFilter == "All"
                ? $"{filtered.Count} items"
                : $"{filtered.Count} {_armorySlotFilter}s",
        };
        countLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        countLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        _armoryContainer.AddChild(countLbl);

        foreach (var item in filtered)
            _armoryContainer.AddChild(BuildUnequippedItemRow(item, save));
    }

    private Control BuildUnequippedItemRow(ItemInstance item, GuildSaveData save)
    {

        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,
            BorderColor = UITheme.RarityColor(item.Rarity),
            CornerRadiusTopLeft = UITheme.CornerRadius - 1,
            CornerRadiusTopRight = UITheme.CornerRadius - 1,
            CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
            CornerRadiusBottomRight = UITheme.CornerRadius - 1,
            BorderWidthTop = UITheme.BorderWidth - 1,
            BorderWidthBottom = UITheme.BorderWidth - 1,
            BorderWidthLeft = UITheme.BorderWidth - 1,
            BorderWidthRight = UITheme.BorderWidth - 1,
            ContentMarginLeft = UITheme.PaddingNormal + 2,
            ContentMarginRight = UITheme.PaddingNormal + 2,
            ContentMarginTop = UITheme.PaddingNormal,
            ContentMarginBottom = UITheme.PaddingNormal,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(row);

        // Left: name + details
        var info = MakeVBox(2);
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(info);

        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 8);
        info.AddChild(nameRow);

        var nameLbl = new Label { Text = item.Name };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        nameLbl.AddThemeColorOverride("font_color", UITheme.RarityColor(item.Rarity));
        nameRow.AddChild(nameLbl);

        var slotBadge = new Label { Text = $"[{item.Slot}]" };
        slotBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        slotBadge.AddThemeColorOverride("font_color", UITheme.TextOnLight);
        nameRow.AddChild(slotBadge);

        var classBadge = new Label { Text = $"[{item.UnitClass}]" };
        classBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        classBadge.AddThemeColorOverride("font_color", UITheme.SuccessDim);
        nameRow.AddChild(classBadge);

        var def = ItemDatabase.Get(item.DefinitionId);
        if (def != null)
        {
            var statsLbl = new Label { Text = BuildStatSummary(def) };
            statsLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            statsLbl.AddThemeColorOverride("font_color", UITheme.TextOnLight);
            info.AddChild(statsLbl);

            if (!string.IsNullOrEmpty(def.Description))
            {
                var descLbl = new Label
                {
                    Text = def.Description,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                descLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                descLbl.AddThemeColorOverride("font_color", UITheme.TextDim);
                info.AddChild(descLbl);
            }
        }

        // Right: equip button
        if (_selectedArmoryUnitId != null && def != null)
        {
            if (System.Enum.TryParse<EquipmentSlot>(item.Slot, true, out var itemSlot))
            {
                var loadout = save.Armory.GetLoadout(_selectedArmoryUnitId);
                string currentInstanceId = loadout.GetSlot(itemSlot);

                string btnText = currentInstanceId != null ? "Swap →" : "Equip →";

                var equipBtn = new Button
                {
                    Text = btnText,
                    CustomMinimumSize = new Vector2(90, 32),
                };
                equipBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                UITheme.ApplyButtonStyle(equipBtn, isPrimary: true);

                string capturedInstId = item.InstanceId;
                equipBtn.Pressed += () =>
                {
                    // Swap: unequip current first, then equip new
                    if (currentInstanceId != null)
                        save.Armory.Unequip(_selectedArmoryUnitId, itemSlot);
                    save.Armory.Equip(_selectedArmoryUnitId, capturedInstId);
                    SaveManager.Save();
                    RefreshArmoryTab();
                };

                var btnCol = MakeVBox(4);
                btnCol.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
                row.AddChild(btnCol);
                btnCol.AddChild(equipBtn);
            }
        }

        return panel;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Training Tab
    // ═══════════════════════════════════════════════════════════════════════
    private void RefreshTrainingTab()
    {
        if (_trainingContainer == null) return;
        foreach (Node child in _trainingContainer.GetChildren())
            child.QueueFree();

        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            _trainingContainer.AddChild(MakeStubLabel("No save loaded."));
            return;
        }

        RefreshGoldLabel();

        int tgTier = save.TrainingGroundsTier;
        if (tgTier == 0)
        {
            _trainingContainer.AddChild(MakeStubLabel(
                "Build Training Grounds to unlock stance training."));
            return;
        }

        AddSectionHeader(_trainingContainer, "Stance Training");

        var note = new Label
        {
            Text = $"Training Grounds Tier {tgTier} — " +
                   $"{save.MartialStanceSlots} stance slot(s) active per companion.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        _trainingContainer.AddChild(note);

        // ── Companion selector ────────────────────────────────────────────
        AddSectionHeader(_trainingContainer, "Select Companion");
        BuildTrainingCompanionSelector(save);

        if (_selectedTrainingCompanionId == null) return;

        var companion = save.Companions.Find(
            c => c.Id == _selectedTrainingCompanionId);
        if (companion == null || companion.IsPermadead) return;

        bool isMartial = companion.UnitClass == "Fighter" ||
                         companion.UnitClass == "Ranger";
        if (!isMartial)
        {
            _trainingContainer.AddChild(MakeStubLabel(
                $"{companion.Name} is arcane — no stance training available."));
            return;
        }

        // ── Current trained stances ───────────────────────────────────────
        AddSectionHeader(_trainingContainer, $"{companion.Name}'s Trained Stances");
        BuildTrainedStanceList(companion, save);

        // ── Available stances to learn ────────────────────────────────────
        AddSectionHeader(_trainingContainer, "Available to Learn");
        BuildLearnableStanceList(companion, save);
    }

    private void BuildTrainingCompanionSelector(GuildSaveData save)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _trainingContainer.AddChild(row);

        foreach (var companion in save.Companions)
        {
            if (!companion.IsRecruited || companion.IsPermadead) continue;
            bool isMartial = companion.UnitClass == "Fighter" ||
                             companion.UnitClass == "Ranger";

            bool isSelected = _selectedTrainingCompanionId == companion.Id;
            var btn = new Button
            {
                Text = companion.Name,
                ToggleMode = true,
                ButtonPressed = isSelected,
                CustomMinimumSize = new Vector2(120, 36),
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            ApplyTabStyle(btn, isSelected);

            if (!isMartial)
                btn.Modulate = new Color(1, 1, 1, 0.5f); // dim arcane companions

            string captured = companion.Id;
            btn.Pressed += () =>
            {
                _selectedTrainingCompanionId = captured;
                RefreshTrainingTab();
            };
            row.AddChild(btn);
        }
    }

    private void BuildTrainedStanceList(Companion companion, GuildSaveData save)
    {
        int slots = save.MartialStanceSlots;

        if (companion.TrainedStanceIds.Count == 0)
        {
            _trainingContainer.AddChild(MakeStubLabel("No stances trained yet."));
        }
        else
        {
            for (int i = 0; i < companion.TrainedStanceIds.Count; i++)
            {
                bool slotActive = i < slots;
                var stance = StanceRegistry.Get(companion.TrainedStanceIds[i]);
                if (stance == null) continue;

                var row = BuildStanceRow(stance, companion, save,
                    isActive: slotActive, canForget: true);
                _trainingContainer.AddChild(row);
            }
        }

        // Show locked slots
        for (int i = companion.TrainedStanceIds.Count; i < 3; i++)
        {
            bool unlocked = i < slots;
            var slotLbl = new Label
            {
                Text = unlocked
                    ? $"Slot {i + 1}: Empty — learn a stance below"
                    : $"Slot {i + 1}: Locked (Training Grounds Tier {i + 1} required)",
            };
            slotLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            slotLbl.AddThemeColorOverride("font_color",
                unlocked ? UITheme.TextSecondary : UITheme.TextDim);
            _trainingContainer.AddChild(slotLbl);
        }
    }

    private void BuildLearnableStanceList(Companion companion, GuildSaveData save)
    {
        // Stances this companion can learn based on their class
        // that they haven't learned yet
        var martialClass = companion.UnitClass == "Fighter"
            ? MartialClass.Fighter : MartialClass.Ranger;

        bool anyLearnable = false;
        foreach (var stance in StanceRegistry.All.Values)
        {
            if (stance.Class != martialClass) continue;
            if (companion.TrainedStanceIds.Contains(stance.Id)) continue;

            anyLearnable = true;
            bool canLearn = companion.TrainedStanceIds.Count < save.MartialStanceSlots;

            // Training cost: 50g per stance (could be data-driven later)
            int cost = 50;
            bool canAfford = save.Gold >= cost;

            var row = BuildLearnStanceRow(stance, companion, save,
                cost, canLearn, canAfford);
            _trainingContainer.AddChild(row);
        }

        if (!anyLearnable)
            _trainingContainer.AddChild(MakeStubLabel(
                $"{companion.Name} has learned all available stances."));
    }

    private Control BuildStanceRow(StanceDefinition stance, Companion companion,
        GuildSaveData save, bool isActive, bool canForget)
    {
        var panel = new PanelContainer();
        var style = UITheme.MakePanelStyle(
            isActive ? UITheme.BgRaised : UITheme.BgBase,
            isActive ? UITheme.Violet : UITheme.Neutral);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(row);

        var info = MakeVBox(2);
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(info);

        var nameLbl = new Label { Text = stance.DisplayName };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        nameLbl.AddThemeColorOverride("font_color",
            isActive ? UITheme.TextPrimary : UITheme.TextDim);
        info.AddChild(nameLbl);

        var descLbl = new Label
        {
            Text = stance.Description,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        descLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        descLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        info.AddChild(descLbl);

        if (!isActive)
        {
            var inactiveLbl = new Label { Text = "Inactive — upgrade Training Grounds" };
            inactiveLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            inactiveLbl.AddThemeColorOverride("font_color", UITheme.Warning);
            info.AddChild(inactiveLbl);
        }

        if (canForget)
        {
            var forgetBtn = new Button
            {
                Text = "Forget",
                CustomMinimumSize = new Vector2(70, 28),
            };
            forgetBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(forgetBtn, isPrimary: false);

            string stanceId = stance.Id;
            forgetBtn.Pressed += () =>
            {
                companion.TrainedStanceIds.Remove(stanceId);
                SaveManager.Save();
                RefreshTrainingTab();
            };
            row.AddChild(forgetBtn);
        }

        return panel;
    }

    private Control BuildLearnStanceRow(StanceDefinition stance, Companion companion,
        GuildSaveData save, int cost, bool canLearn, bool canAfford)
    {
        var panel = new PanelContainer();
        var style = UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Neutral);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(row);

        var info = MakeVBox(2);
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(info);

        var nameLbl = new Label { Text = stance.DisplayName };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        nameLbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        info.AddChild(nameLbl);

        var descLbl = new Label
        {
            Text = stance.Description,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        descLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        descLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        info.AddChild(descLbl);

        var learnBtn = new Button
        {
            Text = $"Train ({cost}g)",
            CustomMinimumSize = new Vector2(90, 32),
            Disabled = !canLearn || !canAfford,
        };
        learnBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(learnBtn, isPrimary: canLearn && canAfford);

        if (!canLearn)
        {
            var reasonLbl = new Label { Text = "No open slots" };
            reasonLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            reasonLbl.AddThemeColorOverride("font_color", UITheme.TextDim);
            info.AddChild(reasonLbl);
        }
        else if (!canAfford)
        {
            var reasonLbl = new Label { Text = $"Need {cost}g" };
            reasonLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            reasonLbl.AddThemeColorOverride("font_color", UITheme.Danger);
            info.AddChild(reasonLbl);
        }

        string stanceId = stance.Id;
        learnBtn.Pressed += () =>
        {
            save.Gold -= cost;
            companion.TrainedStanceIds.Add(stanceId);
            SaveManager.Save();
            RefreshTrainingTab();
            RefreshAll(); // update gold display
        };
        row.AddChild(learnBtn);

        return panel;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string BuildStatSummary(ItemDefinition def)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (def.Stats.MaxHP != 0) parts.Add($"+{def.Stats.MaxHP} HP");
        if (def.Stats.MaxMana != 0) parts.Add($"+{def.Stats.MaxMana} Mana");
        if (def.Stats.Armor != 0) parts.Add($"+{def.Stats.Armor} Armor");
        if (def.Stats.BaseSpeed != 0) parts.Add($"+{def.Stats.BaseSpeed} Speed");
        if (def.Stats.AttackDamage != 0) parts.Add($"+{def.Stats.AttackDamage} Atk");
        if (def.Stats.AttackRange != 0) parts.Add($"+{def.Stats.AttackRange} Range");
        if (def.Stats.SpellDamage != 0) parts.Add($"+{def.Stats.SpellDamage} SpellDmg");

        if (def.Passive != "None" && !string.IsNullOrEmpty(def.Passive))
            parts.Add(PassiveLabel(def.Passive, def.PassiveValue));

        return parts.Count > 0 ? string.Join("  ·  ", parts) : "No bonuses";
    }

    private string PassiveLabel(string passive, int value) => passive switch
    {
        "StormSpellCostReduction" => $"Storm spells cost -{value} mana",
        "FireSpellBonusDamage" => $"Fire spells +{value} dmg",
        "StartCombatWithShield" => $"Start with {value} shield",
        "RestoreManaOnTurnStart" => $"Restore {value} mana/turn",
        "FirstCardCostReduction" => $"First card costs -{value} mana",
        "AttackAppliesBleed" => "Attacks apply bleed",
        "BonusDamageAboveHalfHP" => $"+{value} atk above 50% HP",
        "DamageReductionPerHit" => $"Take -{value} dmg per hit",
        _ => passive,
    };

    // ═══════════════════════════════════════════════════════════════════════
    // Debug panel
    // ═══════════════════════════════════════════════════════════════════════

    private PanelContainer BuildDebugPanel()
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.DebugPanelBg,
            BorderColor = UITheme.DebugPanelBorder,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            ContentMarginLeft = UITheme.PaddingNormal + 4,
            ContentMarginRight = UITheme.PaddingNormal + 4,
            ContentMarginTop = UITheme.PaddingNormal,
            ContentMarginBottom = UITheme.PaddingNormal,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 20);
        grid.AddThemeConstantOverride("v_separation", 6);
        panel.AddChild(grid);

        CheckBox MakeDebugCheck(string label, bool current, Action<bool> onChange)
        {
            var cb = new CheckBox { Text = label, ButtonPressed = current };
            cb.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            cb.Toggled += (on) => onChange(on);
            return cb;
        }

        grid.AddChild(MakeDebugCheck("No Fog of War", PlayerSession.NoFog,
            on => PlayerSession.NoFog = on));
        grid.AddChild(MakeDebugCheck("Unlimited Steps", PlayerSession.UnlimitedSteps,
            on => PlayerSession.UnlimitedSteps = on));
        grid.AddChild(MakeDebugCheck("God Mode HP", PlayerSession.GodModeHP,
            on => PlayerSession.GodModeHP = on));
        grid.AddChild(MakeDebugCheck("Start With Gold", PlayerSession.StartWithGold,
            on => PlayerSession.StartWithGold = on));
        grid.AddChild(MakeDebugCheck("Skip Deployment", PlayerSession.SkipDeployment,
            on => PlayerSession.SkipDeployment = on));

        var forceLabel = new Label { Text = "Force Next POI:" };
        forceLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        grid.AddChild(forceLabel);

        _forceEncounterDropdown = new OptionButton { CustomMinimumSize = new Vector2(140, 28) };
        _forceEncounterDropdown.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _forceEncounterDropdown.AddItem("None (normal)", -1);
        _forceEncounterDropdown.AddItem("Combat", (int)OverworldHex.POIType.Combat);
        _forceEncounterDropdown.AddItem("Rest", (int)OverworldHex.POIType.Rest);
        _forceEncounterDropdown.AddItem("Narrative", (int)OverworldHex.POIType.Narrative);
        _forceEncounterDropdown.AddItem("Negotiation", (int)OverworldHex.POIType.Negotiation);
        _forceEncounterDropdown.Selected = 0;
        _forceEncounterDropdown.ItemSelected += (idx) =>
            PlayerSession.ForceNextEncounterType =
                _forceEncounterDropdown.GetItemId((int)idx);
        grid.AddChild(_forceEncounterDropdown);

        return panel;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Refresh methods
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshAll()
    {
        var bonuses = BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        PlayerSession.ApplyUnlockedFeatures(bonuses.UnlockedFeatures);

        RefreshSlotButtons();
        RefreshCompanionList();
        RefreshBuildingList();
        RefreshTrainingTab();
        RefreshArmoryTab();
        RefreshGoldLabel();
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
            UITheme.ApplyButtonStyle(loadBtn, isPrimary: false);

            if (slot.Slot == _selectedSlot)
                loadBtn.Modulate = UITheme.CampusSlotSelected;

            int capturedSlot = slot.Slot;
            bool isEmpty = slot.IsEmpty;
            loadBtn.Pressed += () => OnSlotSelected(capturedSlot, isEmpty);
            hbox.AddChild(loadBtn);

            if (!slot.IsEmpty)
            {
                var delBtn = new Button { Text = "✕", CustomMinimumSize = new Vector2(36, 36) };
                UITheme.ApplyButtonStyle(delBtn, isPrimary: false);
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
        partyHeader.AddThemeFontSizeOverride("font_size", UITheme.CampusStubFontSize);
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
                BgColor = UITheme.CompanionCardBg,
                BorderColor = c.IsRecruited ? UITheme.CompanionCardBorderActive : UITheme.CompanionCardBorderInactive,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                CornerRadiusTopLeft = UITheme.CornerRadius - 1,
                CornerRadiusTopRight = UITheme.CornerRadius - 1,
                CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
                CornerRadiusBottomRight = UITheme.CornerRadius - 1,
                ContentMarginLeft = UITheme.PaddingNormal + 2,
                ContentMarginRight = UITheme.PaddingNormal + 2,
                ContentMarginTop = UITheme.PaddingNormal,
                ContentMarginBottom = UITheme.PaddingNormal,
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            card.AddChild(row);

            var info = MakeVBox(2);
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            bool inParty = save.ActivePartyCompanionIds.Contains(c.Id);
            string badge = c.IsRecruited ? (inParty ? "  [PARTY]" : "  [ROSTER]") : $"  [{c.RecruitmentCost}g]";

            var nameLabel = new Label { Text = $"{c.Name}{badge}" };
            nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusNameFontSize);
            nameLabel.AddThemeColorOverride("font_color", UITheme.TextPrimary); // ← add this
            info.AddChild(nameLabel);

            var subLabel = new Label { Text = $"{c.School}  ·  {c.PersonalityTrait}  ·  Loyalty: {c.Loyalty}" };
            subLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            subLabel.Modulate = UITheme.CompanionSubText;
            info.AddChild(subLabel);
            row.AddChild(info);

            string capturedId = c.Id;
            var btn = new Button { CustomMinimumSize = new Vector2(120, 32) };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);

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
                BgColor = UITheme.BuildingCardBg,
                BorderColor = buildingSave.Tier > 0 ? UITheme.BuildingCardBorderBuilt : UITheme.BuildingCardBorderEmpty,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                CornerRadiusTopLeft = UITheme.CornerRadius - 1,
                CornerRadiusTopRight = UITheme.CornerRadius - 1,
                CornerRadiusBottomLeft = UITheme.CornerRadius - 1,
                CornerRadiusBottomRight = UITheme.CornerRadius - 1,
                ContentMarginLeft = UITheme.PaddingNormal + 4,
                ContentMarginRight = UITheme.PaddingNormal + 4,
                ContentMarginTop = UITheme.PaddingNormal + 2,
                ContentMarginBottom = UITheme.PaddingNormal + 2,
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var cardLayout = MakeVBox(4);
            card.AddChild(cardLayout);

            var headerRow = new HBoxContainer();
            headerRow.AddThemeConstantOverride("separation", 12);
            cardLayout.AddChild(headerRow);

            var nameCol = MakeVBox(2);
            nameCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            string tierText = buildingSave.Tier == 0 ? "Not Built" : $"Tier {buildingSave.Tier} / {template.MaxTier}";
            var nameLabel = new Label { Text = $"{buildingSave.Name}  [{tierText}]" };
            nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildFontSize);
            nameLabel.AddThemeColorOverride("font_color", UITheme.TextPrimary); // ← add this
            nameCol.AddChild(nameLabel);

            var catLabel = new Label
            {
                Text = template.Category + (string.IsNullOrEmpty(template.SchoolAffinity) ? "" : $"  ·  {template.SchoolAffinity}")
            };
            catLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildTinyFontSize);
            catLabel.Modulate = UITheme.BuildingCategoryText;
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
                btn.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
                string capturedId = buildingSave.Id;
                btn.Pressed += () => { if (TryBuildOrUpgrade(capturedId)) RefreshAll(); };
                headerRow.AddChild(btn);
            }
            else
            {
                var maxLabel = new Label { Text = "MAX" };
                maxLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
                maxLabel.AddThemeColorOverride("font_color", UITheme.BuildingMaxText);
                headerRow.AddChild(maxLabel);
            }

            if (buildingSave.Tier > 0)
            {
                var cur = template.Tiers.Find(t => t.Tier == buildingSave.Tier);
                if (cur != null)
                {
                    var lbl = new Label { Text = $"Active: {cur.Description}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
                    lbl.AddThemeColorOverride("font_color", UITheme.BuildingActiveText);
                    cardLayout.AddChild(lbl);
                }
            }

            if (nextTier <= template.MaxTier)
            {
                var next = template.Tiers.Find(t => t.Tier == nextTier);
                if (next != null)
                {
                    var lbl = new Label { Text = $"Next: {next.Description}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                    lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildTinyFontSize);
                    lbl.AddThemeColorOverride("font_color", UITheme.BuildingNextText);
                    cardLayout.AddChild(lbl);
                }
            }

            _buildingContainer.AddChild(card);
        }
    }

    private void RefreshGoldLabel()
    {
        if (_goldLabel == null) return;
        var save = SaveManager.ActiveSave;
        if (save == null) { _goldLabel.Text = ""; return; }
        _goldLabel.Text = $"Gold: {save.Gold}    ✦ {save.ArcaneSplinters} Splinters";
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
        EnsureStarterItems();
        RefreshAll();
        RefreshGoldLabel();
        RefreshArmoryTab();
        RefreshTrainingTab();
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
        RefreshGoldLabel();
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

    private void EnsureStarterItems()
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return;

        ItemDatabase.LoadAll();

        // Only seed on a fresh armory
        if (save.Armory.OwnedItems.Count > 0) return;

        // Give one of each starter item
        var starterIds = new[]
        {
            "apprentices_focus", "travellers_robe", "mana_crystal",
            "stormcaller_staff", "warding_cloak", "spell_focus",
            "iron_sword", "leather_jerkin", "warriors_sigil",
            "hunters_bow", "chain_hauberk", "scouts_leathers",
        };

        foreach (var id in starterIds)
        {
            var def = ItemDatabase.Get(id);
            if (def != null)
                save.Armory.AddItem(def);
        }

        SaveManager.Save();
        GD.Print($"[Armory] Seeded {save.Armory.OwnedItems.Count} starter items.");
    }

    private void ApplyTabStyle(Button btn, bool isActive)
    {
        // Flat style — no rounded corners, continuous bar appearance
        var normal = new StyleBoxFlat
        {
            BgColor = isActive ? UITheme.ButtonPrimary : UITheme.BgDeep,
            BorderColor = isActive ? UITheme.Violet : UITheme.NeutralDim,
            BorderWidthBottom = isActive ? 2 : 0,
            BorderWidthTop = 0,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
            // No corner radius — square tabs
        };
        var hover = new StyleBoxFlat
        {
            BgColor = isActive ? UITheme.ButtonPrimaryHover : UITheme.BgBase,
            BorderColor = UITheme.Violet,
            BorderWidthBottom = 2,
            BorderWidthTop = 0,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
        };

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", normal);
        btn.AddThemeStyleboxOverride("focus", normal);
        btn.AddThemeColorOverride("font_color",
            isActive ? UITheme.TextPrimary : UITheme.TextSecondary);
    }

    private void AddSectionHeader(VBoxContainer parent, string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize);
        label.AddThemeColorOverride("font_color", UITheme.CampusSectionColor);
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

    private Button MakeButton(string text, float minWidth, float minHeight, int fontSize,
        bool isPrimary = true)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(minWidth, minHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary);
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
        label.AddThemeFontSizeOverride("font_size", UITheme.CampusStubFontSize);
        label.Modulate = UITheme.CampusStubText;
        return label;
    }
}
