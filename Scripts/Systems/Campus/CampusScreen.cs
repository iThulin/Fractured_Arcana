using Godot;
using System;
using System.Collections.Generic;
using static CampusUi;   // AddSectionHeader / MakeVBox / MakeMargins / MakeButton /
                         // MakeStubLabel / ApplyTabStyle — see CampusUi.cs

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
    private int _activeTab = 0;

    private Button[] _tabButtons;
    private Control[] _tabPanels;

    // Guild tab
    private Label _goldLabel;
    private readonly CampusGuildPanel _guildPanel = new();

    // Companions tab
    private readonly CampusCompanionsPanel _companionsPanel = new();
    private VBoxContainer _buildingContainer;

    // Campus tab (hex map)
    private CampusGridManager _campusGrid;
    private CampusInputController _campusInput;
    private CameraController _campusCameraController;
    private SubViewport _campusViewport;
    private Label _campusSelectionLabel;
    private Label _campusResourceBanner;
    private string _campusPlacingBuildingId = null; // non-null while a drag placement is in progress

    // Campus tab — building list collapse
    private VBoxContainer _buildingListSection;
    private Button _buildingListCollapseBtn;
    private bool _buildingListCollapsed = false;

    // Council tab + campus narrative host (Step 9)
    private const string CampusScenePath = "res://Scenes/Campus/CampusScene.tscn";
    private readonly CampusCouncilPanel _councilTab = new();
    private NarrativeEncounterPanel _campusNarrativePanel;
    private ToastManager _campusToasts;

    /// <summary>The one seam handed to extracted campus panels. Built in <see cref="BuildUI"/>
    /// before the tab bodies run. Nothing consumes it yet — the tab bodies are still
    /// methods on this class; each one that moves out takes this as its build argument.
    /// See docs/campus_tab_extraction_v1.md.</summary>
    private CampusContext _ctx;

    // Armory tab
    private readonly CampusArmoryPanel _armoryPanel = new();

    // Training tab
    private readonly CampusTrainingPanel _trainingPanel = new();

    // Expedition tab
    private readonly CampusExpeditionPanel _expeditionPanel = new();

    /// <summary>S4: the Scriptorium's scroll-crafting rows (Expedition tab).</summary>

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
        PlayerDeckSave.UseDebugDeck = false; // campus is the real-deck home; debug routing off
        if (SaveManager.ActiveSave == null) SaveManager.AutoLoadLast(); // boot/dev: fall back to last save
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

        string[] tabNames = { "Guild", "Companions", "Campus", "Expedition", "Armory", "Training", "Records", "Quests", "Council" };
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

        // Step 9: campus-hosted narrative panel + quest toasts (the campus half of the
        // vignette host and the audience UI).
        //
        // CONSTRUCTED here, before the tab bodies, so CampusContext can hand panels a
        // valid NarrativeHost/Toasts at build time — but ADDED to the tree further down,
        // after the tab panels, because Godot draws later siblings on top and both of
        // these must layer over the tabs. Do not collapse the two halves back together.
        _campusNarrativePanel = new NarrativeEncounterPanel { Visible = false };
        _campusToasts = new ToastManager { Name = "CampusQuestToasts" };
        _ctx = new CampusContext(this, _campusToasts, ShowCampusNarrative, RefreshAll,
                                 RefreshGoldLabel, EnterStrategicMap, BeginNextCycle,
                                 EnsureSaveSeeded);

        _guildPanel.Build((ScrollContainer)_tabPanels[0], _ctx);
        _companionsPanel.Build((ScrollContainer)_tabPanels[1], _ctx);
        BuildCampusTab((ScrollContainer)_tabPanels[2]);
        _expeditionPanel.Build((ScrollContainer)_tabPanels[3], _ctx);
        _armoryPanel.Build((ScrollContainer)_tabPanels[4], _ctx);
        _trainingPanel.Build((ScrollContainer)_tabPanels[5], _ctx);
        _recordsPanel.Build((ScrollContainer)_tabPanels[6], _ctx);
        _questsPanel.Build((ScrollContainer)_tabPanels[7], _ctx);
        _councilTab.Build((ScrollContainer)_tabPanels[8], _ctx);

        // Layered last — see the construction note above.
        AddChild(_campusNarrativePanel);
        AddChild(_campusToasts);

        GD.Print($"CampusScreen: ActiveSave={SaveManager.ActiveSave?.GuildName ?? "NULL"}, " +
                 $"Gold={SaveManager.ActiveSave?.Gold ?? -1}, " +
                 $"Runs={SaveManager.ActiveSave?.TotalRuns ?? -1}");

        if (SaveManager.ActiveSave != null && SaveManager.ActiveSlot >= 0)
        {
            _guildPanel.SelectedSlot = SaveManager.ActiveSlot;
            EnsureSaveSeeded();
            if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave.SelectedSchool, out var school))
                PlayerSession.SelectedSchool = school;
        }

        // Step 9: consume a pending campus-combat return (landmark trial or
        // archmage overthrow) BEFORE the first refresh, so every tab renders
        // the post-fight state.
        ConsumeCampusCombatReturn();

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
                _expeditionPanel.Refresh();
                break;
            case 4:
                _armoryPanel.Refresh();
                break;
            case 5:
                _trainingPanel.Refresh();
                break;
            case 6:
                _recordsPanel.Refresh();
                break;
            case 7:
                _questsPanel.Refresh();
                break;
            case 8:
                _councilTab.Refresh();
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Guild Tab
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildCampusTab(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(10);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Campus Buildings");

        var note = new Label
        {
            Text = "Construct and upgrade buildings to gain permanent bonuses across all runs. " +
                   "Built buildings must be sited on the map before their bonuses feel like a place.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.Modulate = UITheme.CampusSubtleText;
        layout.AddChild(note);
        layout.AddChild(new HSeparator());

        var splitRow = new HBoxContainer();
        splitRow.AddThemeConstantOverride("separation", 20);
        layout.AddChild(splitRow);

        // ── Left: the hex map (3D — same HexTile/TileData tech combat uses) ──
        var mapCol = MakeVBox(6);
        mapCol.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        splitRow.AddChild(mapCol);

        _campusSelectionLabel = new Label { Text = "Click a building to select it." };
        _campusSelectionLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _campusSelectionLabel.Modulate = UITheme.CampusSubtleText;
        mapCol.AddChild(_campusSelectionLabel);

        // Gold/Materials banner — local to the viewport so it's visible while focused
        // on placement, rather than only in the screen's top title bar.
        var bannerPanel = new PanelContainer();
        var bannerStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.11f, 0.09f, 0.85f),
            CornerRadiusTopLeft = UITheme.CornerRadius,
            CornerRadiusTopRight = UITheme.CornerRadius,
            CornerRadiusBottomLeft = UITheme.CornerRadius,
            CornerRadiusBottomRight = UITheme.CornerRadius,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        bannerPanel.AddThemeStyleboxOverride("panel", bannerStyle);
        _campusResourceBanner = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _campusResourceBanner.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildFontSize);
        _campusResourceBanner.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
        bannerPanel.AddChild(_campusResourceBanner);
        mapCol.AddChild(bannerPanel);

        var viewportContainer = new SubViewportContainer
        {
            Stretch = true,
            CustomMinimumSize = new Vector2(560, 560),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        mapCol.AddChild(viewportContainer);

        _campusViewport = new SubViewport
        {
            Size = new Vector2I(560, 560),
            TransparentBg = true,
        };
        viewportContainer.AddChild(_campusViewport);

        // Exact values from Battlefield.tscn's DirectionalLight3D — not a guess anymore.
        var sunBasis = new Basis(
            new Vector3(0.7071065f, 0.49999976f, -0.49999994f),
            new Vector3(0f, 0.7071065f, 0.7071067f),
            new Vector3(0.7071065f, -0.49999976f, 0.49999994f)
        );
        var sun = new DirectionalLight3D
        {
            Transform = new Transform3D(sunBasis, new Vector3(0, 10, 0)),
            LightColor = new Color(1f, 0.95f, 0.88f, 1f),
            LightEnergy = 0.34f,
            LightIndirectEnergy = 0.5f,
            ShadowEnabled = true,
        };
        _campusViewport.AddChild(sun);

        // Same environment resource combat uses, for real visual consistency rather
        // than an unlit/default look.
        var combatEnv = GD.Load<Godot.Environment>("res://Assets/Environments/Combat_Environment.tres");
        if (combatEnv != null)
            _campusViewport.AddChild(new WorldEnvironment { Environment = combatEnv });

        // Reused as-is from combat: pan/zoom/orbit rig. Its left-click handling
        // (_cardDropHandler?.TryDropCardOnTile()) no-ops safely here since no
        // CardDropHandler exists in this scene — CampusInputController owns all
        // actual click/drag behavior via _UnhandledInput, layered on top.
        _campusCameraController = new CameraController();
        var pivot = new Node3D { Name = "CameraPivot" };
        var camera3D = new Camera3D { Name = "Camera3D" };
        pivot.AddChild(camera3D);
        _campusCameraController.AddChild(pivot);
        _campusViewport.AddChild(_campusCameraController);

        _campusGrid = new CampusGridManager
        {
            // Confirmed from Battlefield.tscn: this is the exact scene HexGridManager
            // uses for HexTileScene3D, so campus tiles render identically to combat.
            HexTileScene3D = GD.Load<PackedScene>("res://Scenes/Combat/HexTile.tscn"),
            // Inherited from HexGridManager — its defaults (1f, true) don't suit the
            // campus. HexRadius must match combat's real value or tiles misalign;
            // UseBlendedTerrainMesh MUST be false or ApplyVisualToTile takes the
            // blended-mesh branch, which needs a private field this class never sets.
            HexRadius = 1.025f,
            UseBlendedTerrainMesh = false,
        };
        _campusViewport.AddChild(_campusGrid);

        _campusInput = new CampusInputController();
        // Configure BEFORE AddChild. AddChild runs _Ready synchronously, and _Ready's
        // NodePath fallback logs two errors ("no CampusGridManager" / "no Camera3D") when
        // it finds _grid/_camera still null — which they are, if Configure comes second.
        // The controller worked anyway (the next line filled them in), so this was pure
        // console noise, but it buries real errors on a screen that prints ~90 lines.
        // Configure touches no tree state, so calling it pre-parent is safe.
        _campusInput.Configure(_campusGrid, camera3D); // code-built scene — direct wiring, not NodePath
        _campusViewport.AddChild(_campusInput);
        _campusInput.BuildingSelected += OnCampusBuildingSelected;
        _campusInput.TileClicked += OnCampusTileClicked;
        _campusInput.LandmarkClicked += OnCampusLandmarkClicked;
        _campusInput.PlacementConfirmed += OnCampusPlacementConfirmed;
        _campusInput.PlacementCancelled += OnCampusPlacementCancelled;

        // Both CameraController and CampusInputController poll/read raw input that
        // Godot's normal event-consumption pipeline can't scope by screen position on
        // its own (see AcceptInput's doc comment on each) — so this viewport's own
        // hover state, tracked via the Control wrapping it, is what gates them. This
        // is what stops mouse motion / WASD from bleeding into the building list.
        viewportContainer.MouseEntered += () =>
        {
            _campusCameraController.AcceptInput = true;
            _campusInput.AcceptInput = true;
        };
        viewportContainer.MouseExited += () =>
        {
            _campusCameraController.AcceptInput = false;
            _campusInput.AcceptInput = false;
        };
        // Starts inactive — nothing has entered the viewport yet at build time.
        _campusCameraController.AcceptInput = false;
        _campusInput.AcceptInput = false;

        var cancelPlaceBtn = MakeButton("Cancel Placement", 160, 32, UITheme.CampusBuildSmallFontSize, isPrimary: false);
        cancelPlaceBtn.Pressed += () => _campusInput?.CancelDrag();
        mapCol.AddChild(cancelPlaceBtn);

        // ── Right: the existing build/upgrade list, now collapsible ─────
        var listCol = MakeVBox(6);
        listCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        splitRow.AddChild(listCol);

        _buildingListCollapseBtn = new Button
        {
            Text = "▼  Buildings",
            Alignment = HorizontalAlignment.Left,
        };
        _buildingListCollapseBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildFontSize);
        _buildingListCollapseBtn.Pressed += ToggleBuildingListCollapsed;
        listCol.AddChild(_buildingListCollapseBtn);

        _buildingListSection = MakeVBox(10);
        listCol.AddChild(_buildingListSection);

        _buildingContainer = MakeVBox(10);
        _buildingListSection.AddChild(_buildingContainer);
    }

    private void ToggleBuildingListCollapsed()
    {
        _buildingListCollapsed = !_buildingListCollapsed;
        _buildingListSection.Visible = !_buildingListCollapsed;
        _buildingListCollapseBtn.Text = _buildingListCollapsed ? "▶  Buildings" : "▼  Buildings";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Expedition Tab
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Backs <see cref="CampusContext.EnterStrategicMap"/>.</summary>
    private void EnterStrategicMap()
    {
        EnsureCycleWorld();
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
    }

    /// <summary>Backs <see cref="CampusContext.BeginNextCycle"/>. Cycle lifecycle, kept on
    /// the shell rather than moved into the Expedition panel: it archives a LoopRecord,
    /// replaces CycleState and reseeds the deck — save mutation far beyond anything that
    /// panel displays. EnsureCycleWorld's own comment marks it for a future CycleInitializer;
    /// parking both here keeps that lift to one step.</summary>
    private void BeginNextCycle(string school)
    {
        // Archive the dead cycle and create the fresh one. Option A persistence is
        // automatic: BeginNewCycle leaves the ledger untouched, resets the cycle,
        // and re-seeds a starter deck for the chosen school.
        // TODO (SCOPE — Convergence victory-gating; progression_persistence_model_v1.md §4):
        // Outcome is HARDCODED "ConvergenceDefeat" — there is no real victory/defeat
        // determination yet. Wire the actual Convergence result here so a win may lead to
        // Continue and a loss forces the reset. NOT YET SCOPED.
        SaveManager.BeginNewCycle(school, "ConvergenceDefeat");
        PlayerSession.CycleEndedByConjunction = false;
        if (Enum.TryParse<CardSchool>(school, out var cs))
            PlayerSession.SelectedSchool = cs;

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
        int seed = cycle.WorldSeed; // playtest 2026-07-23: real per-cycle seeds restored
        // int seed = -2085197503; // (dev) pin for deterministic world generation
        var g = WorldGenerator.Generate(seed, cycle.SelectedSchool);
        cycle.World = g.World;
        cycle.Kingdoms = g.Kingdoms;
        cycle.Campaign = g.Campaign;
        cycle.Council = g.Council;
        CorruptionSpread.Reset(); // new world — drop cached adjacency + pressure
        KingdomTickSimulation.Reset(); // new world — drop cached kingdom adjacency
        // Seed echo-eligible flags from permanent records (quest_hooks §5, step 6).
        // Runs after world generation so echo encounters can reference the new world.
        EchoSeeder.Seed(SaveManager.ActiveSave);
        // Roster rotation: which starters are present this rendering (2026-07-22).
        CompanionUnlocks.SeedCycleRotation(SaveManager.ActiveSave);
        SaveManager.Save();
        GD.Print($"[Campus] Generated cycle {cycle.CycleNumber} world (seed {seed}, " +
                 $"{g.Kingdoms.Count} territories, {g.World.Pois.Count} POIs, " +
                 $"{g.Council.Courts.Count} courts).");
    }


    // ═══════════════════════════════════════════════════════════════════════
    // Armory Tab
    // ═══════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════
    // Training Tab
    // ═══════════════════════════════════════════════════════════════════════
    // ── Helpers ──────────────────────────────────────────────────────────

    // ═══════════════════════════════════════════════════════════════════════
    // Debug panel
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshAll()
    {
        if (SaveManager.ActiveSave != null)
        {
            QuestTracker.SyncCompletions(SaveManager.ActiveSave);
            // Companion unlock rules (2026-07-22): evaluate on every campus
            // refresh; toast anyone the guild's record just earned.
            foreach (var name in CompanionUnlocks.Sync(SaveManager.ActiveSave))
                _campusToasts?.Push($"{name} can now be recruited.", QuestToastKind.Unlock);
        }
        PlayerSession.ClearRunState();
        BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        BuildingEffectApplier.ApplyCampusEffects(SaveManager.ActiveSave);

        _guildPanel.RefreshSlots();
        _companionsPanel.Refresh();
        RefreshBuildingList();
        _trainingPanel.Refresh();
        _armoryPanel.Refresh();
        RefreshGoldLabel();
    }

    private void RefreshBuildingList()
    {
        LoadCampusGrid();

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
                int goldCost = tierData?.GoldCost ?? 0;
                int materialsCost = tierData?.EffectiveMaterialsCost ?? 0;
                var btn = new Button
                {
                    Text = buildingSave.Tier == 0
                        ? $"Build\n{goldCost}g / {materialsCost}m"
                        : $"Upgrade\n{goldCost}g / {materialsCost}m",
                    CustomMinimumSize = new Vector2(110, 44),
                    Disabled = save.Gold < goldCost || save.BuildMaterials < materialsCost,
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

            // Built but not yet sited on the map — the map's hex slots are
            // otherwise empty for this building. Placement is separate from
            // tier progression (a building can be upgraded whether or not
            // it's sited yet), so this is its own row/button.
            if (buildingSave.Tier > 0 && !buildingSave.IsPlaced)
            {
                var placeRow = new HBoxContainer();
                placeRow.AddThemeConstantOverride("separation", 8);
                cardLayout.AddChild(placeRow);

                var placeLbl = new Label { Text = "Not yet sited on the campus map." };
                placeLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildTinyFontSize);
                placeLbl.Modulate = UITheme.CampusSubtleText;
                placeLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                placeRow.AddChild(placeLbl);

                string capturedId = buildingSave.Id;
                var placeBtn = new Button { Text = "Place on Map", CustomMinimumSize = new Vector2(110, 32) };
                placeBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
                placeBtn.Pressed += () => BeginBuildingDrag(capturedId);
                placeRow.AddChild(placeBtn);
            }

            _buildingContainer.AddChild(card);
        }
    }

    // ── Campus hex map wiring ─────────────────────────────────────────────

    /// <summary>(Re)loads the campus grid from the active save and reframes the camera
    /// on it. Safe to call repeatedly — CampusGridManager.LoadFromSave clears and
    /// rebuilds its tiles.</summary>
    private void LoadCampusGrid()
    {
        if (_campusGrid == null)
            return;
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;

        _campusGrid.LoadFromSave(save.Ledger.CampusMap, save.Ledger.Buildings);

        // Must follow LoadFromSave — LoadLandmarks reads the building occupancy that
        // LoadFromSave populates, and skips any hex a building already sits on.
        // Every RefreshAll → BuildUI path lands here, so this is also what restamps
        // landmark states after a narrative beat advances ruined → active → restored.
        _campusGrid.LoadLandmarks(save.HasFlag);

        _campusCameraController?.FrameGrid(_campusGrid.CampusGridBoundsMin, _campusGrid.CampusGridBoundsMax);
    }

    /// <summary>Wired to the "Place on Map" button. Starts a drag via
    /// CampusInputController — the player then moves the mouse over the 3D viewport
    /// (live valid/invalid preview) and clicks to drop, rather than holding the mouse
    /// button down continuously from the button press.</summary>
    private void BeginBuildingDrag(string buildingId)
    {
        _campusPlacingBuildingId = buildingId;
        _campusInput?.BeginDrag(buildingId);
        var template = BuildingDatabase.GetTemplate(buildingId);
        _campusSelectionLabel.Text = $"Move the mouse over the map and click to place {template?.Name ?? buildingId}. " +
                                      "R to rotate, right-click or Escape to cancel.";
    }

    private void OnCampusBuildingSelected(string buildingId, Vector2I anchor)
    {
        // Seam for the eventual building info sub-menu / HUD bus — not built yet,
        // this just surfaces selection for now.
        var template = BuildingDatabase.GetTemplate(buildingId);
        _campusSelectionLabel.Text = template != null
            ? $"Selected: {template.Name}"
            : $"Selected: {buildingId}";
    }

    private void OnCampusTileClicked(Vector2I axial)
    {
        // Empty-tile click while not dragging — treat as deselect.
        _campusSelectionLabel.Text = "Click a building to select it.";
    }

    private void OnCampusPlacementConfirmed(string buildingId, Vector2I anchor, int rotation)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || _campusGrid == null)
            return;

        bool placed = _campusGrid.PlaceBuilding(buildingId, anchor, rotation, save.Ledger.Buildings);
        _campusPlacingBuildingId = null;

        if (placed)
        {
            SaveManager.Save();
            _campusSelectionLabel.Text = "Click a building to select it.";
            RefreshBuildingList(); // reloads the grid too, via LoadCampusGrid at the top
        }
        else
        {
            // The live preview already showed red for this exact spot, so reaching
            // an invalid commit here means something changed between preview and
            // release (e.g. two rapid inputs) rather than a normal user mistake.
            _campusSelectionLabel.Text = "Couldn't place there. Try again.";
        }
    }

    private void OnCampusPlacementCancelled(string buildingId)
    {
        _campusPlacingBuildingId = null;
        _campusSelectionLabel.Text = "Click a building to select it.";
    }

    private void RefreshGoldLabel()
    {
        if (_goldLabel == null)
            return;
        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            _goldLabel.Text = "";
            if (_campusResourceBanner != null)
                _campusResourceBanner.Text = "";
            return;
        }
        _goldLabel.Text = $"Gold: {save.Gold}    Materials: {save.BuildMaterials}    ✦ {save.ArcaneSplinters} Splinters";
        if (_campusResourceBanner != null)
            _campusResourceBanner.Text = $"Gold: {save.Gold}     Materials: {save.BuildMaterials}";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Actions
    // ═══════════════════════════════════════════════════════════════════════

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
        if (tierData == null || save.Gold < tierData.GoldCost || save.BuildMaterials < tierData.EffectiveMaterialsCost)
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
        save.BuildMaterials -= tierData.EffectiveMaterialsCost;
        if (buildingSave.Tier == 0)
            buildingSave.CurrentIntegrity = buildingSave.MaxIntegrity; // fresh build (or rebuild after destruction) starts at full HP
        buildingSave.Tier = nextTier;

        SaveManager.Save();
        RefreshGoldLabel();
        GD.Print($"Built {buildingSave.Name} tier {nextTier}. Gold: {save.Gold}, Materials: {save.BuildMaterials}");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Seed everything a loaded save is expected to already contain: the companion
    /// roster, the building list, and the starter armory.
    ///
    /// EnsureStarterItems used to sit outside this, called only from OnSlotSelected — so a
    /// guild reached by any OTHER route got a roster and buildings but an EMPTY ARMORY. Two
    /// routes do exactly that: founding a new guild (the empty-slot branch of OnSlotSelected
    /// early-returns to NewGameScreen and never comes back through it) and SaveManager
    /// .AutoLoadLast on boot. Both land in BuildUI, which called only this method.
    ///
    /// Folded in rather than adding a second call to BuildUI: the failure mode was two
    /// seeding steps that had to be kept in sync and weren't, and a second call site would
    /// have left that shape intact. EnsureStarterItems is idempotent — it skips demo items
    /// that already exist and gates starter seeding on an empty armory — so callers may
    /// invoke this freely.</summary>
    private void EnsureSaveSeeded()
    {
        if (SaveManager.ActiveSave == null)
            return;
        CompanionRoster.EnsureRoster(SaveManager.ActiveSave);
        BuildingDatabase.EnsureBuildings(SaveManager.ActiveSave);
        EnsureStarterItems();
    }

    private void EnsureStarterItems()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;

        ItemDatabase.LoadAll();

        // Q2 (§7a) + Q3 (§4b) demo items — ensure the six exemplars exist even on
        // an ESTABLISHED armory, so they're equippable for verification without a
        // fresh save. Q2: trigger-bus (aegis/duelist/standard). Q3: overworld
        // traversal-resistance (wardstone/cinderweave/trailwarden). Runs before
        // the fresh-armory gate below.
        bool grantedDemo = false;
        foreach (var id in new[] { "aegis_charm", "duelists_brand", "standard_of_the_vigil",
                                   "wardstone_amulet", "cinderweave_cloak", "trailwardens_compass" })
        {
            if (save.Armory.OwnedItems.Exists(i => i.DefinitionId == id))
                continue;
            var demoDef = ItemDatabase.Get(id);
            if (demoDef != null)
            {
                save.Armory.AddItem(demoDef);
                grantedDemo = true;
            }
        }
        if (grantedDemo)
        {
            SaveManager.Save();
            GD.Print("[Armory] Q2/Q3 demo items granted (Aegis Charm, Duelist's Brand, Standard of the Vigil, Wardstone Amulet, Cinderweave Cloak, Trailwarden's Compass).");
        }

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

    // ApplyTabStyle moved to CampusUi 2026-08-03, unchanged. It styles the tab-bar
    // SELECTOR rather than any panel's content — which is exactly the piece the campus
    // map will not need when it becomes the second selector.

    // ═══════════════════════════════════════════════════════════════════════
    // Records Tab — the Hall of Records (negotiation doc §7b)
    // ═══════════════════════════════════════════════════════════════════════

    // Extracted 2026-08-03 — bodies now live in CampusRecordsPanel / CampusQuestsPanel.
    // The five container fields these tabs used moved with them.
    private readonly CampusRecordsPanel _recordsPanel = new();
    private readonly CampusQuestsPanel _questsPanel = new();

    // RefreshLoreSection / PrettifyLoreId removed 2026-08-03 — they were a second
    // renderer for the same codex QuestLogView.BuildLoreInto already draws, flagged as
    // a parked follow-up in session log 2026-07-18. The Quests tab now calls
    // BuildLoreInto directly, so the campus tab and the global QuestLogScreen overlay
    // render the lore codex through one code path and cannot drift.

    // ═══════════════════════════════════════════════════════════════════════
    // Quests Tab — story log + lore codex (drives the fragment/Convergence spine
    // and the expansion arcs; status computed live by QuestTracker).
    // ═══════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════
    // Council Tab (Step 9) — the archmage sentiment overview, the resolution
    // audiences, and the mentor's counsel. The campus is where the campaign's
    // central question is asked and answered.
    // ═══════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════
    // Campus vignette host + campus → combat round trip (Steps 2 + 9)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Backs <see cref="CampusContext.ShowNarrative"/>: open an encounter on the
    /// campus overlay AND wire its completion to <see cref="OnCampusNarrativeCompleted"/>,
    /// the Snapshot-Mutate-Diff-Toast pass that persists flags, gold and meta-progression.
    ///
    /// The two halves were previously copy-pasted at three call sites. Showing without
    /// wiring fails silently — the encounter renders, the player chooses, nothing is saved —
    /// so they are one method now, and panels are handed the verb rather than the panel.</summary>
    private void ShowCampusNarrative(NarrativeEncounterData enc)
    {
        var save = SaveManager.ActiveSave;
        if (enc == null || save == null || _campusNarrativePanel == null) return;

        _campusNarrativePanel.ShowEncounter(enc, save.HasFlag,
            save.Cycle?.SelectedSchool, save.Gold, save.Cycle?.Campaign);
        _campusNarrativePanel.OnCompleted = choice => OnCampusNarrativeCompleted(enc, choice);
    }

    /// <summary>Landmark clicked on the campus hex grid → show its current
    /// beat's narrative encounter on the campus host.</summary>
    private void OnCampusLandmarkClicked(string landmarkId, Vector2I coord)
    {
        var lm = CampusLandmarkRegistry.Get(landmarkId);
        var save = SaveManager.ActiveSave;
        if (lm == null || save == null || _campusNarrativePanel == null) return;

        var enc = lm.GetEncounter(save.HasFlag);
        if (enc == null)
        {
            _campusSelectionLabel.Text = $"{lm.DisplayName} — restored.";
            return;
        }

        _campusSelectionLabel.Text = lm.DisplayName;
        ShowCampusNarrative(enc);
    }

    /// <summary>Campus-side choice resolution: the Snapshot-Mutate-Diff-Toast
    /// pattern over campus state (save.Gold, flags, rewards). HPDelta and
    /// StepDelta are expedition currencies and do not apply at the campus;
    /// campus beats are authored with gold/flag costs instead.</summary>
    private void OnCampusNarrativeCompleted(NarrativeEncounterData encounter, EncounterChoice choice)
    {
        if (choice == null) return;
        var save = SaveManager.ActiveSave;
        if (save == null) return;

        // Resolution verbs (audience encounters).
        if (!string.IsNullOrEmpty(choice.ResolutionKind) &&
            !string.IsNullOrEmpty(encounter.ArchmageId) &&
            HandleCampusResolutionChoice(encounter.ArchmageId, choice.ResolutionKind))
            return;

        // A campus beat that stages a real fight (Step 9's round trip).
        if (!string.IsNullOrEmpty(choice.LaunchGuardian))
        {
            LaunchCampusCombat(BuildCampusGuardianEncounter(choice.LaunchGuardian),
                               guardianKey: choice.LaunchGuardian);
            return;
        }

        var before = QuestNotifier.Snapshot(save);

        if (choice.GoldDelta != 0)
            save.Gold = Mathf.Max(0, save.Gold + choice.GoldDelta);

        if (!string.IsNullOrEmpty(encounter.Id) &&
            !save.CompletedEvents.Contains(encounter.Id))
            save.CompletedEvents.Add(encounter.Id);

        if (choice.SetFlags != null)
            foreach (var flag in choice.SetFlags)
                save.SetFlag(flag);

        if (choice.SetMetaFlags != null && save.Ledger != null)
            foreach (var flag in choice.SetMetaFlags)
                if (!string.IsNullOrEmpty(flag) && !save.Ledger.MetaNarrativeFlags.Contains(flag))
                    save.Ledger.MetaNarrativeFlags.Add(flag);

        if (!string.IsNullOrEmpty(choice.ItemReward))
        {
            var idef = ItemDatabase.Get(choice.ItemReward);
            if (idef != null) save.Armory.AddItem(idef);
        }

        if (!string.IsNullOrEmpty(choice.CompanionUnlock))
            CompanionRoster.GrantFromEncounter(choice.CompanionUnlock);

        if (!string.IsNullOrEmpty(choice.ReputationFactionId) && choice.ReputationAmount != 0)
        {
            save.FactionReputation.TryGetValue(choice.ReputationFactionId, out int cur);
            save.FactionReputation[choice.ReputationFactionId] = cur + choice.ReputationAmount;
        }

        if (!string.IsNullOrEmpty(choice.LoreId) &&
            !save.UnlockedLoreEntries.Contains(choice.LoreId))
            save.UnlockedLoreEntries.Add(choice.LoreId);

        // Companion arc delivery (Step 9 follow-up): campus-located arc stages
        // resolve here — advance the arc and toast it.
        var arcStatus = CompanionArcTracker.TryCompleteByEncounter(encounter.Id, save);
        if (arcStatus != null)
            _campusToasts?.Push(arcStatus.IsComplete
                ? $"{arcStatus.CompanionName} — \"{arcStatus.ArcName}\" complete."
                : $"{arcStatus.CompanionName} — \"{arcStatus.ArcName}\" advances ({arcStatus.CurrentStage}/{arcStatus.TotalStages}).",
                QuestToastKind.Progress);

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();

        foreach (var qt in QuestNotifier.NotifyNew(before, save))
            _campusToasts?.Push(qt.Text, qt.Kind);

        // Landmark states may have advanced (ruined → active → restored) —
        // RefreshAll → BuildUI → LoadCampusGrid → LoadLandmarks restamps them from
        // current flags.
        RefreshAll();
    }

    /// <summary>Campus twin of ExpeditionManager.HandleResolutionChoice.</summary>
    private bool HandleCampusResolutionChoice(string archmageId, string kind)
    {
        var save = SaveManager.ActiveSave;
        var campaign = save?.Cycle?.Campaign;
        if (campaign == null) return false;
        var def = ArchmageRegistry.Get(archmageId);
        string region = campaign.GetRegionForArchmage(archmageId);

        switch (kind.ToLowerInvariant())
        {
            case "unite":
                campaign.SetDisposition(archmageId, ArchmageDisposition.Allied);
                foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageUnited, region, archmageId))
                    _campusToasts?.Push(qt.Text, qt.Kind);
                _campusToasts?.Push($"{def?.DisplayName ?? "The archmage"} stands with the guild.",
                                    QuestToastKind.Progress);
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                _councilTab.Refresh();
                return true;

            case "coerce":
                campaign.SetDisposition(archmageId, ArchmageDisposition.Coerced);
                foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageCoerced, region, archmageId))
                    _campusToasts?.Push(qt.Text, qt.Kind);
                _campusToasts?.Push($"{def?.DisplayName ?? "The archmage"} yields to the accord — for now.",
                                    QuestToastKind.Progress);
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                _councilTab.Refresh();
                return true;

            case "overthrow":
                var combat = ResolutionEncounterBuilder.BuildOverthrowCombat(
                    campaign, archmageId, save.Cycle?.SelectedSchool);
                if (combat == null)
                {
                    campaign.SetDisposition(archmageId, ArchmageDisposition.Overthrown);
                    foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageOverthrown, region, archmageId))
                        _campusToasts?.Push(qt.Text, qt.Kind);
                    SaveManager.MarkDirty();
                    SaveManager.SaveIfDirty();
                    _councilTab.Refresh();
                    return true;
                }
                LaunchCampusCombat(combat, resolutionArchmageId: archmageId);
                return true;
        }
        return false;
    }

    /// <summary>Step 9: a themed Boss composition for a campus-launched trial
    /// (mirrors ExpeditionManager.BuildGuardianEncounter for the campus host).</summary>
    private EncounterDefinition BuildCampusGuardianEncounter(string key)
    {
        string[] arch = key switch
        {
            "primal"    => new[] { "Brute", "Wizard", "Wizard" },
            "axiom"     => new[] { "Wizard", "Wizard", "Defender" },
            "moment"    => new[] { "Ranger", "Wizard", "Ranger" },
            "binding"   => new[] { "Wizard", "Wizard", "Soldier" },
            "schema"    => new[] { "Defender", "Brute", "Soldier" },
            "deathless" => new[] { "Brute", "Wizard", "Defender" },
            _           => new[] { "Brute", "Wizard", "Soldier" },
        };
        float mult = 1.6f * CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle);
        var def = new EncounterDefinition
        {
            Id = $"campus_guardian_{key}",
            DisplayName = "The Warden",
            Tier = EncounterTier.Boss,
            TerrainType = "Plains",
            DifficultyMult = mult,
        };
        foreach (var a in arch)
            if (UnitRegistry.TryResolveId(a, out var uid))
                def.Enemies.Add(new EnemySlot(uid, mult));
        return def.Enemies.Count > 0 ? def : null;
    }

    /// <summary>Step 9: launch a combat FROM the campus. Sets the router's
    /// return-scene override so the fight (and any card draft after it) routes
    /// back to the campus, then swaps to the battlefield. Attribution fields
    /// follow the expedition's reset-then-mark pattern.</summary>
    private void LaunchCampusCombat(EncounterDefinition def,
                                    string guardianKey = "",
                                    string resolutionArchmageId = "")
    {
        // The router is created lazily by ExpeditionManager; a fresh session
        // that goes campus → audience → overthrow has never deployed, so
        // ensure it here too (same pattern as EnsureEncounterRouter).
        if (EncounterRouter.Instance == null)
            GetTree().Root.AddChild(new EncounterRouter { Name = "EncounterRouter" });

        var router = EncounterRouter.Instance;
        var save = SaveManager.ActiveSave;
        if (router == null || def == null || def.Enemies.Count == 0)
        {
            // Never dead-end: resolve the launch's intent directly.
            if (!string.IsNullOrEmpty(guardianKey) && save?.Ledger != null &&
                !save.Ledger.MetaNarrativeFlags.Contains($"{guardianKey}_trial_passed"))
            {
                save.Ledger.MetaNarrativeFlags.Add($"{guardianKey}_trial_passed");
                SaveManager.MarkDirty();
            }
            if (!string.IsNullOrEmpty(resolutionArchmageId))
            {
                var fbCampaign = save?.Cycle?.Campaign;
                fbCampaign?.SetDisposition(resolutionArchmageId,
                    ArchmageDisposition.Overthrown);
                if (fbCampaign != null)
                    foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageOverthrown,
                             fbCampaign.GetRegionForArchmage(resolutionArchmageId), resolutionArchmageId))
                        _campusToasts?.Push(qt.Text, qt.Kind);
                SaveManager.MarkDirty();
            }
            SaveManager.SaveIfDirty();
            RefreshAll();
            return;
        }

        router.HasPendingReturn = false;
        router.SavedCombatWasPatrolAmbush = false;
        router.SavedCombatPatrolArchmageId = "";
        router.SavedCombatGuardianKey = guardianKey;
        router.SavedCombatArchmageId = "";
        router.SavedResolutionArchmageId = resolutionArchmageId;
        router.ReturnSceneOverride = CampusScenePath;
        router.SetCurrentTier(def.Tier);

        SaveManager.SaveIfDirty();
        EncounterContextCarrier.Set(def);
        EncounterContextCarrier.SetContext(def.TerrainType, def.Tier);
        GetTree().ChangeSceneToFile(router.CombatScenePath);
    }

    /// <summary>Step 9: consume a pending campus-combat return. Banks rewards
    /// into the save (gold/splinters are campus currencies here), applies
    /// guardian-trial and overthrow outcomes, clears the router's override so
    /// a later expedition doesn't misread the pending state, and toasts.</summary>
    private void ConsumeCampusCombatReturn()
    {
        var router = EncounterRouter.Instance;
        if (router == null || !router.HasPendingReturn ||
            router.ReturnSceneOverride != CampusScenePath)
            return;

        var save = SaveManager.ActiveSave;
        bool won = router.CombatWon;
        string guardianKey = router.SavedCombatGuardianKey;
        string resolutionId = router.SavedResolutionArchmageId;

        router.HasPendingReturn = false;
        router.ReturnSceneOverride = "";
        router.SavedCombatGuardianKey = "";
        router.SavedResolutionArchmageId = "";

        if (save == null) return;
        var before = QuestNotifier.Snapshot(save);

        if (won)
        {
            save.Gold += router.GoldReward;
            save.ArcaneSplinters += router.SplinterReward;

            if (!string.IsNullOrEmpty(guardianKey) && save.Ledger != null &&
                !save.Ledger.MetaNarrativeFlags.Contains($"{guardianKey}_trial_passed"))
            {
                save.Ledger.MetaNarrativeFlags.Add($"{guardianKey}_trial_passed");
                _campusToasts?.Push("The warden falls — the trial is passed.", QuestToastKind.Progress);
            }

            if (!string.IsNullOrEmpty(resolutionId))
            {
                var campaign = save.Cycle?.Campaign;
                var rDef = ArchmageRegistry.Get(resolutionId);
                if (campaign != null)
                {
                    campaign.SetDisposition(resolutionId, ArchmageDisposition.Overthrown);
                    string region = campaign.GetRegionForArchmage(resolutionId);
                    foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageOverthrown, region, resolutionId))
                        _campusToasts?.Push(qt.Text, qt.Kind);
                }
                _campusToasts?.Push(
                    $"{rDef?.DisplayName ?? "The archmage"} is overthrown — their shard answers you now.",
                    QuestToastKind.Progress);
            }
        }
        else
        {
            _campusToasts?.Push("Driven back — the campus holds you while you recover.", QuestToastKind.Progress);
        }

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();

        foreach (var qt in QuestNotifier.NotifyNew(before, save))
            _campusToasts?.Push(qt.Text, qt.Kind);
        // Landmark restamping happens in the RefreshAll that follows (BuildUI).
    }

    // AddSectionHeader / MakeVBox / MakeMargins / MakeButton / MakeStubLabel moved to
    // CampusUi 2026-08-03, unchanged. Reached through the file-scoped `using static
    // CampusUi;` at the top, so every call site here is byte-identical. Panels extracted
    // out of this class get the same helpers the same way — do NOT re-add private copies.
}
