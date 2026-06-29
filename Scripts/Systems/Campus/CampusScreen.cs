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
    private CheckBox _debugCheckbox;
    private PanelContainer _debugPanel;
    private OptionButton _forceEncounterDropdown;
    private Button _cardLibraryButton;
    private VBoxContainer _guildIdentityContainer;
    private VBoxContainer _guildResultContainer;

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

    // Expedition tab
    private Label _expeditionWorldStatus;

    private static readonly Dictionary<CardSchool, string> SchoolDescriptions = new()
    {
        { CardSchool.Arcanist,     "Masters of raw magic. High damage spells and mana manipulation." },
        { CardSchool.Elementalist, "Controls terrain with fire, ice, and storm effects." },
        { CardSchool.Necromancer,  "Summons minions and drains life from enemies." },
        { CardSchool.Enchanter,    "Buffs, debuffs, and tile enchantments." },
        { CardSchool.Tinker,       "Mechanical traps, turrets, and area control." },
        { CardSchool.Adept,      "Academy trained magical initiates at their finest." },
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

        var quitBtn = new Button
        {
            Text = "Quit",
            AnchorLeft = 1f,
            AnchorTop = 0.5f,
            AnchorRight = 1f,
            AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -80,
            OffsetRight = -8,
            OffsetTop = -16,
            OffsetBottom = 16,
        };
        quitBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(quitBtn, isPrimary: false);
        quitBtn.Pressed += () => GetTree().Quit();
        titleBar.AddChild(quitBtn);

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
                PlayerSession.SelectedSchool = school;
        }

        RefreshAll();
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
            case 3:
                RefreshExpeditionTab();
                break;
            case 4:
                RefreshArmoryTab();
                break;
            case 5:
                RefreshTrainingTab();
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Guild Tab
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildGuildTab(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(16);
        margins.AddChild(layout);

        // ── Guild identity — filled by RefreshGuildTab() ─────────────────
        _guildIdentityContainer = MakeVBox(0);
        layout.AddChild(_guildIdentityContainer);

        // ── Last run result — filled by RefreshGuildTab() ────────────────
        _guildResultContainer = MakeVBox(0);
        layout.AddChild(_guildResultContainer);

        // ── Save slots ───────────────────────────────────────────────────
        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Save Slots");
        _slotContainer = MakeVBox(6);
        layout.AddChild(_slotContainer);

        // ── Card management ──────────────────────────────────────────────
        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Cards");

        var cardRow = new HBoxContainer();
        cardRow.AddThemeConstantOverride("separation", 10);
        cardRow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        layout.AddChild(cardRow);

        var libBtn = MakeButton("Card Library", 160, 40, 15);
        libBtn.Pressed += () => GetTree().ChangeSceneToFile("res://Scenes/UI/CardLibrary.tscn");
        cardRow.AddChild(libBtn);

        var deckBtn = MakeButton("Manage Deck", 160, 40, 15);
        deckBtn.Pressed += () => GetTree().ChangeSceneToFile("res://Scenes/UI/DeckEditor.tscn");
        cardRow.AddChild(deckBtn);

        var upgradeBtn = MakeButton("Upgrade Cards", 160, 40, 15);
        upgradeBtn.Pressed += () => GetTree().ChangeSceneToFile("res://Scenes/UI/CardUpgradeScreen.tscn");
        cardRow.AddChild(upgradeBtn);

        // ── Debug ────────────────────────────────────────────────────────
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
                PlayerSession.StartWithSplinters = false;
                PlayerSession.SkipDeployment = false;
                PlayerSession.ForceNextEncounterType = -1;
                PlayerSession.DebugRevealStrategicMap = false;
                PlayerSession.DebugGrantStagingArmed = false;
            }
        };

        _summaryLabel = new Label { Visible = false };
        layout.AddChild(_summaryLabel);

    }

    private void RefreshGuildTab()
    {
        RefreshGuildIdentityPanel();
        RefreshGuildResultPanel();
        RefreshSlotButtons();
    }

    private void RefreshGuildIdentityPanel()
    {
        if (_guildIdentityContainer == null)
            return;
        foreach (var child in _guildIdentityContainer.GetChildren())
            child.QueueFree();

        var save = SaveManager.ActiveSave;

        var identityPanel = new PanelContainer();
        var identityStyle = new StyleBoxFlat
        {
            BgColor = UITheme.BgRaised,
            BorderColor = save != null ? UITheme.Violet : UITheme.NeutralDim,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        identityPanel.AddThemeStyleboxOverride("panel", identityStyle);
        identityPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _guildIdentityContainer.AddChild(identityPanel);

        var identityMargin = new MarginContainer();
        identityMargin.AddThemeConstantOverride("margin_left", 20);
        identityMargin.AddThemeConstantOverride("margin_right", 20);
        identityMargin.AddThemeConstantOverride("margin_top", 14);
        identityMargin.AddThemeConstantOverride("margin_bottom", 14);
        identityPanel.AddChild(identityMargin);

        var identityVBox = MakeVBox(6);
        identityMargin.AddChild(identityVBox);

        if (save == null)
        {
            var noSaveLabel = new Label
            {
                Text = "No guild selected — choose a save slot below.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            noSaveLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            noSaveLabel.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
            identityVBox.AddChild(noSaveLabel);
            return;
        }

        // Guild name + school badge
        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 12);
        identityVBox.AddChild(nameRow);

        var guildNameLabel = new Label
        {
            Text = save.GuildName,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        guildNameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSectionFontSize + 2);
        guildNameLabel.AddThemeColorOverride("font_color", UITheme.Gold);
        nameRow.AddChild(guildNameLabel);

        var schoolBadge = new Label { Text = save.SelectedSchool };
        schoolBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        schoolBadge.AddThemeColorOverride("font_color", UITheme.Violet);
        nameRow.AddChild(schoolBadge);

        // Stats row
        var statsRow = new HBoxContainer();
        statsRow.AddThemeConstantOverride("separation", 24);
        identityVBox.AddChild(statsRow);

        void AddStat(string label, string value)
        {
            var col = MakeVBox(2);
            var lbl = new Label { Text = label };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            lbl.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
            col.AddChild(lbl);
            var val = new Label { Text = value };
            val.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            val.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            col.AddChild(val);
            statsRow.AddChild(col);
        }

        AddStat("RUNS", $"{save.TotalRuns}");
        AddStat("WON", $"{save.RunsWon}");
        AddStat("LOST", $"{save.RunsLost}");
        AddStat("GOLD EARNED", $"{save.TotalGoldEarned}");
        AddStat("REGION", save.CurrentRegionId.Replace("_", " ").ToUpper());
    }

    private void RefreshGuildResultPanel()
    {
        if (_guildResultContainer == null)
            return;
        foreach (var child in _guildResultContainer.GetChildren())
            child.QueueFree();

        if (!RunResultData.HasResults)
            return;

        bool won = RunResultData.ReachedObjective;

        var resultPanel = new PanelContainer();
        var resultStyle = new StyleBoxFlat
        {
            BgColor = won
                ? new Color(0.05f, 0.18f, 0.05f, 0.9f)
                : new Color(0.18f, 0.05f, 0.05f, 0.9f),
            BorderColor = won ? UITheme.Success : UITheme.Danger,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 3,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
        };
        resultPanel.AddThemeStyleboxOverride("panel", resultStyle);
        resultPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _guildResultContainer.AddChild(resultPanel);

        var resultMargin = new MarginContainer();
        resultMargin.AddThemeConstantOverride("margin_left", 16);
        resultMargin.AddThemeConstantOverride("margin_right", 16);
        resultMargin.AddThemeConstantOverride("margin_top", 10);
        resultMargin.AddThemeConstantOverride("margin_bottom", 10);
        resultPanel.AddChild(resultMargin);

        var resultVBox = MakeVBox(6);
        resultMargin.AddChild(resultVBox);

        var resultTitle = new Label
        {
            Text = won ? "✓  Last Expedition — Success" : "✗  Last Expedition — Failed",
        };
        resultTitle.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        resultTitle.AddThemeColorOverride("font_color", won ? UITheme.Success : UITheme.Danger);
        resultVBox.AddChild(resultTitle);

        var resultRow = new HBoxContainer();
        resultRow.AddThemeConstantOverride("separation", 20);
        resultVBox.AddChild(resultRow);

        void AddResult(string label, string value)
        {
            var lbl = new Label { Text = $"{label}  {value}" };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            lbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            resultRow.AddChild(lbl);
        }

        AddResult("Gold:", $"{RunResultData.GoldEarned}");
        AddResult("Splinters:", $"{RunResultData.ArcaneSplinters}");
        AddResult("Encounters:", $"{RunResultData.EncountersWon}");
        AddResult("HP:", $"{RunResultData.HPRemaining}");

        RunResultData.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tab builders
    // ═══════════════════════════════════════════════════════════════════════

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
    // Expedition Tab
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildExpeditionTab(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 24);
        scroll.AddChild(margins);
        var layout = MakeVBox(16);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Set Out");

        var hint = new Label
        {
            Text = "The world stands as one map for this cycle. Open the strategic map " +
                   "to choose a staging point and launch a bounded expedition. Explore " +
                   "outward, secure outposts to unlock new staging grounds, and illuminate " +
                   "the world before the Grand Conjunction forces the final confrontation.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        hint.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(hint);

        layout.AddChild(new HSeparator());

        // ── World status panel ───────────────────────────────────────────
        var statusPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        statusPanel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Violet));
        layout.AddChild(statusPanel);

        var statusMargin = new MarginContainer();
        statusMargin.AddThemeConstantOverride("margin_left", 18);
        statusMargin.AddThemeConstantOverride("margin_right", 18);
        statusMargin.AddThemeConstantOverride("margin_top", 14);
        statusMargin.AddThemeConstantOverride("margin_bottom", 14);
        statusPanel.AddChild(statusMargin);

        _expeditionWorldStatus = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _expeditionWorldStatus.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _expeditionWorldStatus.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        statusMargin.AddChild(_expeditionWorldStatus);

        layout.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        // ── Launch button ────────────────────────────────────────────────
        var launchBtn = MakeButton("Open Strategic Map", 260, 52, UITheme.CampusBodyFontSize);
        launchBtn.Pressed += OnOpenStrategicMap;
        var btnRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        btnRow.AddChild(launchBtn);
        layout.AddChild(btnRow);

        RefreshExpeditionTab();
    }

    private void RefreshExpeditionTab()
    {
        if (_expeditionWorldStatus == null)
            return;

        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            _expeditionWorldStatus.Text = "No guild loaded. Select a save slot first.";
            return;
        }

        var cycle = save.Cycle;
        bool worldExists = cycle?.World != null && cycle.World.Tiles.Length > 0;

        if (!worldExists)
        {
            _expeditionWorldStatus.Text =
                $"Cycle {cycle?.CycleNumber ?? 1}: a new timeline awaits generation. " +
                "Opening the strategic map will weave the world.";
            return;
        }

        // Summarize discovery progress + staging options.
        var world = cycle.World;
        int explored = 0, charted = 0;
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            var d = world.Tiles[i].Discovery;
            if (d == TileDiscovery.Explored)
                explored++;
            else if (d == TileDiscovery.Charted)
                charted++;
        }
        float pct = world.Tiles.Length > 0 ? explored * 100f / world.Tiles.Length : 0f;
        int staging = 0;
        foreach (var sp in world.StagingPoints)
            if (sp.Available)
                staging++;
        int discoveredPois = 0;
        foreach (var p in world.Pois)
            if (p.Discovered)
                discoveredPois++;

        _expeditionWorldStatus.Text =
            $"Cycle {cycle.CycleNumber}  ·  World {world.Width}×{world.Height}\n" +
            $"Illuminated: {pct:F1}%  ({explored} tiles explored, {charted} charted)\n" +
            $"Staging points available: {staging}\n" +
            $"Points of interest discovered: {discoveredPois}";
    }

    private void OnOpenStrategicMap()
    {
        if (SaveManager.ActiveSave == null)
        {
            GD.Print("[Campus] No save loaded — cannot open strategic map.");
            return;
        }

        // If the last cycle ended at the Grand Conjunction, begin a new cycle first —
        // with school reselection (Option A: unlocked blueprints, campus, mastery, and
        // essence persist in the ledger; the deck resets to a starter).
        if (PlayerSession.CycleEndedByConjunction)
        {
            ShowNewCycleSchoolPicker();
            return;
        }

        EnsureCycleWorld();
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
    }

    /// <summary>After a Conjunction, let the player choose the next cycle's school
    /// (the same school is allowed — they keep their unlocked card pool either way,
    /// but the deck rebuilds from a starter). Then begin the new cycle and open the
    /// freshly generated world.</summary>
    private void ShowNewCycleSchoolPicker()
    {
        var layer = new CanvasLayer { Name = "NewCycleUI" };
        AddChild(layer);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.04f, 0.92f) };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        layer.AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            OffsetLeft = -280,
            OffsetRight = 280,
            OffsetTop = -200,
            OffsetBottom = 200,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        layer.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var vbox = MakeVBox(14);
        margin.AddChild(vbox);

        var title = new Label { Text = "A New Timeline" };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var body = new Label
        {
            Text = "Kassian weaves the world anew. Choose the school of this cycle. " +
                   "Everything you have learned — your card knowledge, your campus, your " +
                   "mastery — endures. Your deck begins again from its foundations.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        body.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        body.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        vbox.AddChild(body);

        vbox.AddChild(new HSeparator());

        string previousSchool = SaveManager.ActiveSave.Cycle.SelectedSchool;

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        vbox.AddChild(grid);

        foreach (CardSchool school in System.Enum.GetValues(typeof(CardSchool)))
        {
            string schoolName = school.ToString();
            bool isPrevious = schoolName == previousSchool;

            var btn = new Button
            {
                Text = isPrevious ? $"{schoolName}  (again)" : schoolName,
                CustomMinimumSize = new Vector2(230, 44),
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: isPrevious);

            string captured = schoolName;
            btn.Pressed += () => BeginNextCycle(captured, layer);
            grid.AddChild(btn);
        }
    }

    private void BeginNextCycle(string school, CanvasLayer pickerLayer)
    {
        // Archive the dead cycle and create the fresh one. Option A persistence is
        // automatic: BeginNewCycle leaves the ledger untouched, resets the cycle,
        // and re-seeds a starter deck for the chosen school.
        SaveManager.BeginNewCycle(school, "ConvergenceDefeat");
        PlayerSession.CycleEndedByConjunction = false;
        if (Enum.TryParse<CardSchool>(school, out var cs))
            PlayerSession.SelectedSchool = cs;

        pickerLayer?.QueueFree();

        // Generate the new cycle's world and open it.
        EnsureCycleWorld();
        RefreshAll();
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
    }

    /// <summary>Generate the cycle's world on first entry if it doesn't exist yet.
    /// Deterministic per cycle + slot, stored in the cycle save, generated once.
    /// (Later this moves to a dedicated CycleInitializer at cycle start.)</summary>
    private void EnsureCycleWorld()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
            return;
        if (cycle.World != null && cycle.World.Tiles.Length > 0)
            return; // already generated this cycle

        if (cycle.WorldSeed == 0)              // 0 = "not yet rolled" sentinel
        {
            var rng = new RandomNumberGenerator();
            rng.Randomize();
            cycle.WorldSeed = (int)rng.Randi();
        }
        int seed = cycle.WorldSeed;
        var g = WorldGenerator.Generate(seed, cycle.SelectedSchool);
        cycle.World = g.World;
        cycle.Kingdoms = g.Kingdoms;
        cycle.Campaign = g.Campaign;
        CorruptionSpread.Reset(); // new world — drop cached adjacency + pressure
        SaveManager.Save();
        GD.Print($"[Campus] Generated cycle {cycle.CycleNumber} world (seed {seed}, " +
                 $"{g.Kingdoms.Count} territories, {g.World.Pois.Count} POIs).");
    }


    // ═══════════════════════════════════════════════════════════════════════
    // Armory Tab
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshArmoryTab()
    {
        if (_armoryContainer == null)
            return;

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
            if (companion == null || companion.IsPermadead)
                continue;

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
        if (_trainingContainer == null)
            return;
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

        if (_selectedTrainingCompanionId == null)
            return;

        var companion = save.Companions.Find(
            c => c.Id == _selectedTrainingCompanionId);
        if (companion == null || companion.IsPermadead)
            return;

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
            if (!companion.IsRecruited || companion.IsPermadead)
                continue;
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
                if (stance == null)
                    continue;

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
            if (stance.Class != martialClass)
                continue;
            if (companion.TrainedStanceIds.Contains(stance.Id))
                continue;

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

        if (def.Stats.MaxHP != 0)
            parts.Add($"+{def.Stats.MaxHP} HP");
        if (def.Stats.MaxMana != 0)
            parts.Add($"+{def.Stats.MaxMana} Mana");
        if (def.Stats.Armor != 0)
            parts.Add($"+{def.Stats.Armor} Armor");
        if (def.Stats.BaseSpeed != 0)
            parts.Add($"+{def.Stats.BaseSpeed} Speed");
        if (def.Stats.AttackDamage != 0)
            parts.Add($"+{def.Stats.AttackDamage} Atk");
        if (def.Stats.AttackRange != 0)
            parts.Add($"+{def.Stats.AttackRange} Range");
        if (def.Stats.SpellDamage != 0)
            parts.Add($"+{def.Stats.SpellDamage} SpellDmg");

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

        grid.AddChild(MakeDebugCheck("No Fog in Expedition", PlayerSession.NoFog,
            on => PlayerSession.NoFog = on));
        grid.AddChild(MakeDebugCheck("Unlimited Steps", PlayerSession.UnlimitedSteps,
            on => PlayerSession.UnlimitedSteps = on));
        grid.AddChild(MakeDebugCheck("God Mode HP", PlayerSession.GodModeHP,
            on => PlayerSession.GodModeHP = on));
        grid.AddChild(MakeDebugCheck("Start With Gold", PlayerSession.StartWithGold,
            on => PlayerSession.StartWithGold = on));
        grid.AddChild(MakeDebugCheck("Start With Splinters", PlayerSession.StartWithSplinters,
            on => PlayerSession.StartWithSplinters = on));
        grid.AddChild(MakeDebugCheck("Skip Deployment", PlayerSession.SkipDeployment,
            on => PlayerSession.SkipDeployment = on));
        grid.AddChild(MakeDebugCheck("Reveal Strategic Map", PlayerSession.DebugRevealStrategicMap,
            on => PlayerSession.DebugRevealStrategicMap = on));
        grid.AddChild(MakeDebugCheck("Grant Staging (press G in expedition)",
            PlayerSession.DebugGrantStagingArmed,
            on => PlayerSession.DebugGrantStagingArmed = on));

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
        PlayerSession.ClearRunState();
        BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        BuildingEffectApplier.ApplyCampusEffects(SaveManager.ActiveSave);

        RefreshSlotButtons();
        RefreshCompanionList();
        RefreshBuildingList();
        RefreshTrainingTab();
        RefreshArmoryTab();
        RefreshGoldLabel();
    }

    private void RefreshSlotButtons()
    {
        if (_slotContainer == null)
            return;
        foreach (var child in _slotContainer.GetChildren())
            child.QueueFree();

        var slots = SaveManager.GetAllSlotInfo();
        foreach (var slot in slots)
        {
            bool isActive = slot.Slot == _selectedSlot;

            var card = new PanelContainer();
            card.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var cardStyle = new StyleBoxFlat
            {
                BgColor = isActive
                    ? new Color(0.10f, 0.18f, 0.10f, 0.9f)
                    : UITheme.BgRaised,
                BorderColor = isActive ? UITheme.Success : UITheme.NeutralDim,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderWidthLeft = isActive ? 3 : 1,
                BorderWidthRight = 1,
                CornerRadiusTopLeft = 5,
                CornerRadiusTopRight = 5,
                CornerRadiusBottomLeft = 5,
                CornerRadiusBottomRight = 5,
            };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var cardMargin = new MarginContainer();
            cardMargin.AddThemeConstantOverride("margin_left", 16);
            cardMargin.AddThemeConstantOverride("margin_right", 16);
            cardMargin.AddThemeConstantOverride("margin_top", 10);
            cardMargin.AddThemeConstantOverride("margin_bottom", 10);
            card.AddChild(cardMargin);

            var cardRow = new HBoxContainer();
            cardRow.AddThemeConstantOverride("separation", 16);
            cardMargin.AddChild(cardRow);

            // Left: slot info
            var infoCol = MakeVBox(4);
            infoCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cardRow.AddChild(infoCol);

            if (slot.IsEmpty)
            {
                var emptyLabel = new Label { Text = $"Slot {slot.Slot + 1}  —  Empty" };
                emptyLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
                emptyLabel.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                infoCol.AddChild(emptyLabel);

                var newGameHint = new Label { Text = "Click to start a new guild" };
                newGameHint.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                newGameHint.AddThemeColorOverride("font_color", UITheme.TextDim);
                infoCol.AddChild(newGameHint);
            }
            else
            {
                // Name + school row
                var nameRow = new HBoxContainer();
                nameRow.AddThemeConstantOverride("separation", 10);
                infoCol.AddChild(nameRow);

                var slotNum = new Label { Text = $"[{slot.Slot + 1}]" };
                slotNum.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                slotNum.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                nameRow.AddChild(slotNum);

                var nameLabel = new Label { Text = slot.GuildName };
                nameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
                nameLabel.AddThemeColorOverride("font_color",
                    isActive ? Colors.White : UITheme.TextPrimary);
                nameRow.AddChild(nameLabel);

                var schoolBadge = new Label { Text = slot.School };
                schoolBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                schoolBadge.AddThemeColorOverride("font_color", UITheme.Violet);
                nameRow.AddChild(schoolBadge);

                if (isActive)
                {
                    var activeBadge = new Label { Text = "● Active" };
                    activeBadge.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                    activeBadge.AddThemeColorOverride("font_color", UITheme.Success);
                    nameRow.AddChild(activeBadge);
                }

                // Stats row
                var statsRow = new HBoxContainer();
                statsRow.AddThemeConstantOverride("separation", 20);
                infoCol.AddChild(statsRow);

                void AddMiniStat(string label, string value)
                {
                    var lbl = new Label { Text = $"{label}  {value}" };
                    lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
                    lbl.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
                    statsRow.AddChild(lbl);
                }

                AddMiniStat("Gold:", $"{slot.Gold}");
                AddMiniStat("Runs:", $"{slot.TotalRuns}");
                if (!string.IsNullOrEmpty(slot.LastPlayed))
                    AddMiniStat("Last played:", slot.LastPlayed[..10]); // date only
            }

            // Right: action buttons
            var btnCol = MakeVBox(4);
            btnCol.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            cardRow.AddChild(btnCol);

            int capturedSlot = slot.Slot;
            bool isEmpty = slot.IsEmpty;

            var loadBtn = new Button
            {
                Text = slot.IsEmpty ? "New Game" : (isActive ? "Reload" : "Load"),
                CustomMinimumSize = new Vector2(90, 32),
            };
            loadBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(loadBtn, isPrimary: !isActive);
            loadBtn.Pressed += () => OnSlotSelected(capturedSlot, isEmpty);
            btnCol.AddChild(loadBtn);

            if (!slot.IsEmpty)
            {
                var delBtn = new Button
                {
                    Text = "Delete",
                    CustomMinimumSize = new Vector2(90, 28),
                };
                delBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                UITheme.ApplyButtonStyle(delBtn, isPrimary: false);
                delBtn.AddThemeColorOverride("font_color", UITheme.Danger);
                delBtn.Pressed += () =>
                {
                    SaveManager.DeleteSlot(capturedSlot);
                    _selectedSlot = -1;
                    RefreshAll();
                };
                btnCol.AddChild(delBtn);
            }

            _slotContainer.AddChild(card);
        }
    }

    private void RefreshCompanionList()
    {
        if (_companionContainer == null)
            return;
        foreach (var child in _companionContainer.GetChildren())
            child.QueueFree();

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
            if (!c.IsAvailable && !c.IsRecruited)
                continue;
            if (c.IsPermadead)
                continue;
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
        if (_buildingContainer == null)
            return;
        foreach (var child in _buildingContainer.GetChildren())
            child.QueueFree();

        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            _buildingContainer.AddChild(MakeStubLabel("Select a save slot to see buildings."));
            return;
        }

        foreach (var buildingSave in save.Buildings)
        {
            var template = BuildingDatabase.GetTemplate(buildingSave.Id);
            if (template == null)
                continue;

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
        if (_goldLabel == null)
            return;
        var save = SaveManager.ActiveSave;
        if (save == null)
        { _goldLabel.Text = ""; return; }
        _goldLabel.Text = $"Gold: {save.Gold}    ✦ {save.ArcaneSplinters} Splinters";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Actions
    // ═══════════════════════════════════════════════════════════════════════

    private void OnSlotSelected(int slot, bool isEmpty)
    {
        if (isEmpty)
        {
            PlayerSession.PendingNewGameSlot = slot;
            GetTree().ChangeSceneToFile("res://Scenes/UI/NewGameScreen.tscn");
            return;
        }
        else
        {
            SaveManager.Load(slot);
            if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave.SelectedSchool, out var school))
                PlayerSession.SelectedSchool = school;
        }
        _selectedSlot = slot;
        EnsureRostersAndBuildings();
        BuildingEffectApplier.ApplyCampusEffects(SaveManager.ActiveSave);
        EnsureStarterItems();
        RefreshAll();
        RefreshGoldLabel();
        RefreshArmoryTab();
        RefreshTrainingTab();
        GD.Print($"Selected slot {slot}");
    }

    private bool TryBuildOrUpgrade(string buildingId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return false;
        var template = BuildingDatabase.GetTemplate(buildingId);
        if (template == null)
            return false;

        BuildingSaveData buildingSave = null;
        foreach (var b in save.Buildings)
            if (b.Id == buildingId)
            { buildingSave = b; break; }
        if (buildingSave == null)
            return false;

        int nextTier = buildingSave.Tier + 1;
        if (nextTier > template.MaxTier)
            return false;
        var tierData = template.Tiers.Find(t => t.Tier == nextTier);
        if (tierData == null || save.Gold < tierData.GoldCost)
            return false;

        foreach (var reqId in tierData.RequiredBuildings)
        {
            bool found = false;
            foreach (var b in save.Buildings)
                if (b.Id == reqId && b.Tier > 0)
                { found = true; break; }
            if (!found)
                return false;
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

    private void EnsureRostersAndBuildings()
    {
        if (SaveManager.ActiveSave == null)
            return;
        CompanionRoster.EnsureRoster(SaveManager.ActiveSave);
        BuildingDatabase.EnsureBuildings(SaveManager.ActiveSave);
    }

    private void EnsureStarterItems()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;

        ItemDatabase.LoadAll();

        // Only seed on a fresh armory
        if (save.Armory.OwnedItems.Count > 0)
            return;

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
