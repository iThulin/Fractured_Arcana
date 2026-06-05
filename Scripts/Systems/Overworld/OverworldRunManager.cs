using Godot;
using System.Collections.Generic;

// ============================================================
// OverworldRunManager.cs
//
// Purpose:        Top-level controller for one exploration run.
//                 Owns step budget, HP, gold, encounters won;
//                 wires party-token movement to fog updates and
//                 POI triggering; routes encounters to
//                 EncounterRouter; detects run-end conditions.
// Layer:          System
// Collaborators:  OverworldHexGrid.cs, FogOfWarManager.cs,
//                 OverworldPartyToken.cs, RegionLoader.cs,
//                 NarrativeEncounterPanel.cs / Loader.cs,
//                 ScoutReportPanel.cs (combat pre-flight),
//                 EncounterRouter.cs (combat dispatch),
//                 RunResultData.cs (writes results on end)
// See:            README §3 — top of the overworld layer
// ============================================================

/// <summary>Top-level controller for one exploration run. Owns step budget, HP, gold, and encounter counters; routes POI triggers to the appropriate sub-system (combat / negotiation / narrative panel); writes <see cref="RunResultData"/> on run end.</summary>
public partial class OverworldRunManager : Node2D
{
    [Export] public int StepBudget = 20;
    [Export] public int ExhaustionDamagePerStep = 10;
    [Export] public int MaxHP = 100;

    // ── Runtime state (public for save/restore) ─────────────────────────
    public int StepsRemaining { get; set; }
    public int CurrentHP { get; set; }
    public int GoldEarned { get; set; }
    public int SplinterEarned { get; set; }
    public int EncountersWon { get; set; }
    public bool RunComplete { get; private set; }

    // ── Node references ─────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private FogOfWarManager _fog;
    private OverworldPartyToken _party;
    private Camera2D _camera;
    private RegionDefinition _region;
    private NarrativeEncounterPanel _narrativePanel;
    private ScoutReportPanel _scoutPanel;
    private List<NarrativeEncounterData> _encounterPool;

    // ── Pending combat state ─────────────────────────────────────────────
    // Set when the scout panel is open; cleared on engage or retreat.
    private Vector2I? _pendingCombatHexCoord = null;
    private EncounterDefinition _pendingEncounter = null;
    private string _pendingTerrain = null;
    private float _scaledDifficultyMult = 1.0f;

    // ── UI ───────────────────────────────────────────────────────────────
    private Label _stepLabel;
    private Label _hpLabel;
    private Label _infoLabel;
    private Label _objectiveLabel;
    private Button _returnButton;

    // ── Camera pan state ─────────────────────────────────────────────────
    // When the player pans manually, we detach the camera from the party.
    // It reattaches on the next party move.
    private bool _cameraFreeMode = false;
    private const float CameraPanSpeed = 400f; // pixels per second

    [Signal] public delegate void RunEndedEventHandler(bool reachedObjective);

    // ── Accessors for EncounterRouter ───────────────────────────────────
    public Vector2I GetPartyCoord() => _party.CurrentCoord;
    public OverworldHexGrid GetGrid() => _grid;
    public string GetRegionId() => _region?.Id ?? "frontier_wilds";
    public RegionDefinition GetRegion() => _region;

    public override void _Ready()
    {
        // ── Make sure the router exists FIRST so we can read saved seed/state ──
        EnsureEncounterRouter();
        var router = EncounterRouter.Instance;

        // ── Decide which seed to use for this overworld ─────────────────────
        // Priority: (1) returning from combat — use router's saved seed
        //           (2) returning to a visited region — use region memory seed
        //           (3) fresh visit — generate a new random seed
        int seed;
        if (router != null && router.HasSavedSeed)
        {
            seed = router.SavedRunSeed;
            GD.Print($"RunManager: Reusing saved seed {seed} (returning from combat).");
        }
        else
        {
            // Check if this region has been visited before
            string earlyRegionId = SaveManager.ActiveSave?.CurrentRegionId ?? "frontier_wilds";
            int savedSeed = RegionMemoryService.GetSavedSeed(earlyRegionId);
            if (savedSeed != 0)
            {
                seed = savedSeed;
                GD.Print($"RunManager: Using saved region seed {seed} (return visit to '{earlyRegionId}').");
            }
            else
            {
                seed = (int)GD.Randi();
                GD.Print($"RunManager: New run with seed {seed}.");
            }
        }

        // ── Build equipment loadouts for the run ─────────────────────
        BuildEquipmentLoadouts();

        // ── Load the region for this run ───────────────────────────
        string regionId = SaveManager.ActiveSave?.CurrentRegionId ?? "frontier_wilds";
        _region = RegionLoader.LoadOrDefault(regionId);

        if (_region != null)
        {
            StepBudget = _region.StepBudget;

            // ── Scale difficulty by progression ─────────────────────────────
            // Count how many regions the player has cleared so far (not counting
            // the current one — it's the challenge, not the reward).
            int completedRegions = 0;
            if (SaveManager.ActiveSave != null)
            {
                foreach (var mem in SaveManager.ActiveSave.RegionMemory.Values)
                {
                    if (mem.ObjectiveReached && mem.RegionId != regionId)
                        completedRegions++;
                }
            }

            float progression = completedRegions * 0.05f * _region.BaseDifficultyTier;
            _scaledDifficultyMult = _region.EnemyDifficultyMult * (1f + progression);

            GD.Print($"[Difficulty] Region='{_region.DisplayName}' " +
                     $"Tier={_region.BaseDifficultyTier} " +
                     $"BaseMult={_region.EnemyDifficultyMult:F2} " +
                     $"Completed={completedRegions} " +
                     $"ScaledMult={_scaledDifficultyMult:F2}");
        }

        // ── Build the grid with that seed ───────────────────────────────────
        _grid = new OverworldHexGrid { Name = "HexGrid", Seed = seed };
        _grid.Region = _region;
        AddChild(_grid);

        // ── POIs use region counts (or defaults if no region) ───────────
        int combatCount = _region?.CombatPOICount ?? 10;
        int restCount = _region?.RestPOICount ?? 4;
        int narrativeCount = _region?.NarrativePOICount ?? 3;
        int negotiationCount = _region?.NegotiationPOICount ?? 2;
        int outpostCount = _region?.OutpostPOICount ?? 0;
        POIGenerator.Generate(_grid, combatCount, restCount, narrativeCount,
                              negotiationCount, seed, outpostCount);

        // Stash the seed on the router
        if (router != null)
        {
            router.SavedRunSeed = seed;
            router.HasSavedSeed = true;
        }

        // Fog manager (child of grid)
        _fog = new FogOfWarManager { Name = "FogOfWar" };
        _grid.AddChild(_fog);

        // Party token
        _party = new OverworldPartyToken { Name = "PartyToken" };
        _grid.AddChild(_party);

        // Camera
        _camera = new Camera2D
        {
            Name = "RunCamera",
            Zoom = new Vector2(1.2f, 1.2f),
            PositionSmoothingEnabled = true,
            PositionSmoothingSpeed = 5f
        };
        AddChild(_camera);
        _camera.CallDeferred("make_current");

        // ── UI Layer ────────────────────────────────────────────────────────
        var canvas = new CanvasLayer { Name = "UI" };
        AddChild(canvas);

        // ── HUD panel — dark background keeps labels readable over any terrain ──
        var hudPanel = new PanelContainer
        {
            // Top-left, fixed width, auto-height
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 12,
            OffsetTop = 12,
            OffsetRight = 300,
            OffsetBottom = 12, // height driven by content
        };
        var hudStyle = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.55f),
            BorderColor = new Color(1f, 1f, 1f, 0.08f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        hudPanel.AddThemeStyleboxOverride("panel", hudStyle);
        canvas.AddChild(hudPanel);

        var hudVBox = new VBoxContainer();
        hudVBox.AddThemeConstantOverride("separation", 4);
        // Inner padding
        var hudMargin = new MarginContainer();
        hudMargin.AddThemeConstantOverride("margin_left", 12);
        hudMargin.AddThemeConstantOverride("margin_right", 12);
        hudMargin.AddThemeConstantOverride("margin_top", 10);
        hudMargin.AddThemeConstantOverride("margin_bottom", 10);
        hudMargin.AddChild(hudVBox);
        hudPanel.AddChild(hudMargin);

        _stepLabel = MakeHUDLabel();
        hudVBox.AddChild(_stepLabel);

        _hpLabel = MakeHUDLabel();
        hudVBox.AddChild(_hpLabel);

        hudVBox.AddChild(new HSeparator());

        _objectiveLabel = MakeHUDLabel();
        hudVBox.AddChild(_objectiveLabel);

        hudVBox.AddChild(new HSeparator());

        _infoLabel = MakeHUDLabel();
        _infoLabel.Modulate = UITheme.OverworldInfoLabelTint;
        _infoLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hudVBox.AddChild(_infoLabel);

        // ── Pan hint label (bottom-left, outside panel) ──────────────────
        var panHint = new Label
        {
            Text = "WASD / Arrow Keys — pan map",
            Position = new Vector2(12, 0),
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetTop = -30,
            OffsetBottom = -6,
        };
        panHint.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        panHint.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.4f));
        canvas.AddChild(panHint);

        // ── Scout report panel ──────────────────────────────────────────────
        _scoutPanel = new ScoutReportPanel { Name = "ScoutPanel" };
        canvas.AddChild(_scoutPanel);

        // ── Return-to-campus button (hidden until run ends) ─────────────────
        _returnButton = new Button
        {
            Text = "Return to Campus",
            Visible = false,
            // Centered horizontally, anchored near the bottom of the screen
            AnchorLeft = 0.5f,
            AnchorTop = 0.82f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.82f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -130,
            OffsetRight = 130,
            OffsetTop = -26,
            OffsetBottom = 26,
        };
        _returnButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        _returnButton.AddThemeColorOverride("font_color", UITheme.NarrativeTitleColor);
        _returnButton.Pressed += () =>
            GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
        canvas.AddChild(_returnButton);

        // ── Initialize run state defaults (may be overwritten by restore) ───
        StepsRemaining = StepBudget;
        MaxHP = ComputePartyBaseHP();
        CurrentHP = MaxHP;
        GoldEarned = 0;
        SplinterEarned = 0;
        EncountersWon = 0;
        RunComplete = false;

        // ── Apply building bonuses ──────────────────────────────────────
        PlayerSession.ClearRunState();
        var buildingBonuses = BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        BuildingEffectApplier.ApplyCampusEffects(SaveManager.ActiveSave);
        MaxHP += buildingBonuses.BonusHP;
        CurrentHP = MaxHP;
        StepBudget += buildingBonuses.BonusSteps;
        StepsRemaining = StepBudget;
        GoldEarned += buildingBonuses.BonusGold;

        PlayerSession.IsOnExpedition = true;

        // Apply debug flags
        if (PlayerSession.DebugMode && PlayerSession.StartWithGold)
            GoldEarned += 5000;
        if (PlayerSession.DebugMode && PlayerSession.StartWithSplinters)
            SplinterEarned += 5000;

        // ── Restore from combat or place at entry ───────────────────────────
        if (router != null && router.HasPendingReturn)
        {
            RestoreFromCombat(router);
        }
        else
        {
            _party.Initialize(_grid, _fog, _grid.EntryCoord);

            // Restore fog/POI state from region memory if this is a return visit
            string initRegionId = _region?.Id ?? SaveManager.ActiveSave?.CurrentRegionId ?? "frontier_wilds";
            bool isReturnVisit = RegionMemoryService.Restore(initRegionId, _grid);

            if (isReturnVisit)
                ShowInfo("You return to familiar ground. Unexplored territory awaits.");
            else
                ShowInfo("Explore the map. Find and reach the objective.");

            if (buildingBonuses.PreRevealHexCount > 0)
                PreRevealHexes(buildingBonuses.PreRevealHexCount);

            if (PlayerSession.DebugMode && PlayerSession.NoFog)
                RevealAllFog();
        }

        // ── Narrative encounter panel ───────────────────────────────────
        _narrativePanel = new NarrativeEncounterPanel { Visible = false };
        canvas.AddChild(_narrativePanel);

        // Load encounter pool for this region
        _encounterPool = NarrativeEncounterLoader.LoadForRegion(regionId);
        GD.Print($"RunManager: Loaded {_encounterPool.Count} narrative encounters " +
                 $"for region '{regionId}'.");

        // ── Wire signals ────────────────────────────────────────────────────
        _grid.HexClicked += OnHexClicked;
        _party.PartyMoved += OnPartyMoved;
        _party.PartyArrived += OnPartyArrived;

        if (PlayerSession.DebugMode && PlayerSession.NoFog)
            RevealAllFog();

        CenterCamera();
        UpdateUI();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Party HP derivation
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Derives overworld MaxHP from the wizard's base HP (20, matching
    /// CombatManager) plus each active recruited companion's BaseHP.
    /// Building bonuses are applied on top of this in _Ready.
    /// </summary>
    private int ComputePartyBaseHP()
    {
        const int WizardBaseHP = 20;
        int total = WizardBaseHP;

        var save = SaveManager.ActiveSave;
        if (save == null)
            return total;

        foreach (var companionId in save.ActivePartyCompanionIds)
        {
            var companion = save.Companions.Find(
                c => c.Id == companionId && c.IsRecruited && !c.IsPermadead);
            if (companion != null)
                total += companion.BaseHP;
        }

        return total;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Combat routing
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called when the party steps onto a Combat POI. Pre-builds the
    /// EncounterDefinition so the scout panel has full data, then shows
    /// the panel. The party has already paid the step cost to arrive here.
    /// </summary>
    private void OpenScoutReport(Vector2I coord, OverworldHex hex)
    {
        string terrainType = hex.Terrain.ToString();
        string regionId = _region?.Id ?? "frontier_wilds";
        float diffMult = _scaledDifficultyMult;
        var tier = EncounterTier.Battle; // expand when POI stores sub-type

        var encounterDef = EncounterPoolLoader.Pick(regionId, tier, terrainType, diffMult);

        _pendingCombatHexCoord = coord;
        _pendingEncounter = encounterDef;
        _pendingTerrain = terrainType;

        _scoutPanel.OnEngage = () =>
        {
            if (_pendingCombatHexCoord.HasValue && _pendingEncounter != null)
                CommitCombat(_pendingCombatHexCoord.Value, _pendingEncounter, _pendingTerrain);
            _pendingCombatHexCoord = null;
            _pendingEncounter = null;
            _pendingTerrain = null;               // ← add
        };

        _scoutPanel.OnRetreat = () =>
        {
            ShowInfo("You fall back. The encounter remains.");
            _pendingCombatHexCoord = null;
            _pendingEncounter = null;
            _pendingTerrain = null;               // ← add
        };

        int stepCost = GetTerrainStepCost(hex.Terrain);
        _scoutPanel.Show(encounterDef, hex.Terrain.ToString(), stepCost);
    }

    /// <summary>
    /// Saves all overworld state to the router and swaps to the combat
    /// scene, using the pre-built EncounterDefinition from the scout panel.
    /// Bypasses EncounterRouter.StartCombat() to avoid a second Pick() call.
    /// </summary>
    private void CommitCombat(Vector2I hexCoord, EncounterDefinition encounterDef, string terrainType)
    {
        var router = EncounterRouter.Instance;
        if (router == null)
        {
            GD.PrintErr("RunManager: EncounterRouter not found!");
            return;
        }

        // ── Save overworld state ────────────────────────────────────────
        router.SavedStepsRemaining = StepsRemaining;
        router.SavedCurrentHP = CurrentHP;
        router.SavedGoldEarned = GoldEarned;
        router.SavedSplinterEarned = SplinterEarned;
        router.SavedEncountersWon = EncountersWon;
        router.SavedPartyCoord = _party.CurrentCoord;
        router.SavedCombatHexCoord = hexCoord;

        router.SavedFogStates.Clear();
        router.SavedPOIConsumed.Clear();
        foreach (var kvp in _grid.Hexes)
        {
            router.SavedFogStates[kvp.Key] = kvp.Value.Fog;
            router.SavedPOIConsumed[kvp.Key] = kvp.Value.POIConsumed;
        }

        router.HasPendingReturn = false;
        router.HasSavedSeed = true;
        router.SavedRunSeed = _grid.Seed;

        // ── Hand off the pre-built definition, terrain, and tier ────────
        EncounterContextCarrier.Set(encounterDef);
        EncounterContextCarrier.SetContext(terrainType, encounterDef.Tier);
        router.SetCurrentTier(encounterDef.Tier);

        GD.Print($"RunManager: Committing combat — {encounterDef.DisplayName} " +
                 $"({encounterDef.Tier}, {encounterDef.Enemies.Count} enemies) at {hexCoord}");

        ShowInfo("Entering combat...");
        GetTree().ChangeSceneToFile(router.CombatScenePath);
    }

    // ════════════════════════════════════════════════════════════════════════
    // State restore
    // ════════════════════════════════════════════════════════════════════════

    private void RestoreFromCombat(EncounterRouter router)
    {
        GD.Print("RunManager: Restoring state from combat...");

        StepsRemaining = router.SavedStepsRemaining;
        CurrentHP = router.SavedCurrentHP;
        GoldEarned = router.SavedGoldEarned;
        SplinterEarned = router.SavedSplinterEarned;
        EncountersWon = router.SavedEncountersWon;

        foreach (var kvp in router.SavedFogStates)
            if (_grid.Hexes.TryGetValue(kvp.Key, out var h))
                h.Fog = kvp.Value;

        foreach (var kvp in router.SavedPOIConsumed)
            if (_grid.Hexes.TryGetValue(kvp.Key, out var h))
                h.POIConsumed = kvp.Value;

        foreach (var hex in _grid.Hexes.Values)
            hex.RefreshVisuals();

        _party.Initialize(_grid, _fog, router.SavedPartyCoord);

        var resultHex = router.SavedCombatHexCoord;

        if (NegotiationContext.HasResult)
        {
            OnNegotiationReturned(resultHex);
        }
        else
        {
            if (router.CombatWon)
            {
                GoldEarned += router.GoldReward;
                SplinterEarned += router.SplinterReward;
                EncountersWon++;

                if (_grid.Hexes.TryGetValue(resultHex, out var hex))
                {
                    hex.POIConsumed = true;
                    hex.RefreshVisuals();
                }

                ShowInfo($"Victory! Earned {router.GoldReward} gold, " +
                         $"{router.SplinterReward} Arcane Splinters.");
            }
            else
            {
                CurrentHP -= router.DamageTaken;

                if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
                    CurrentHP = Mathf.Max(1, CurrentHP);

                if (_grid.Hexes.TryGetValue(resultHex, out var hex))
                {
                    hex.POIConsumed = true;
                    hex.RefreshVisuals();
                }

                if (CurrentHP <= 0)
                {
                    CurrentHP = 0;
                    ShowInfo("Defeated! Run over.");
                    EndRun(false);
                    UpdateUI();
                    return;
                }

                ShowInfo($"Defeated... Lost {router.DamageTaken} HP.");
            }
        }

        router.HasPendingReturn = false;

        GD.Print($"RunManager: Restored. Party at {router.SavedPartyCoord}, " +
                 $"Steps: {StepsRemaining}, HP: {CurrentHP}, Gold: {GoldEarned}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Setup helpers
    // ════════════════════════════════════════════════════════════════════════

    private void EnsureEncounterRouter()
    {
        if (EncounterRouter.Instance != null)
            return;

        var router = new EncounterRouter { Name = "EncounterRouter" };
        router.CombatScenePath = "res://Scenes/Combat/Battlefield.tscn";
        router.OverworldScenePath = "res://Scenes/Overworld/OverworldScene.tscn";

        GetTree().Root.AddChild(router);
        GD.Print("RunManager: Created EncounterRouter on tree root.");
    }

    private void BuildEquipmentLoadouts()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;

        var companionIds = save.ActivePartyCompanionIds ?? new List<string>();
        EquipmentLoadout.BuildForRun(save.Armory, "wizard", companionIds);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Movement / POI handlers
    // ════════════════════════════════════════════════════════════════════════

    private void OnPartyMoved(Vector2I newCoord, Vector2I oldCoord)
    {
        int stepCost = 1;
        int hpDrain = 0;

        if (_grid.Hexes.TryGetValue(newCoord, out var hex))
        {
            stepCost = GetTerrainStepCost(hex.Terrain);
            hpDrain = GetTerrainHPDrain(hex.Terrain);
        }

        if (!(PlayerSession.DebugMode && PlayerSession.UnlimitedSteps))
        {
            if (StepsRemaining > 0)
            {
                StepsRemaining = Mathf.Max(0, StepsRemaining - stepCost);
            }
            else
            {
                CurrentHP -= ExhaustionDamagePerStep;
                if (CurrentHP <= 0)
                {
                    CurrentHP = 0;
                    EndRun(false);
                    return;
                }
            }

            if (hpDrain > 0)
            {
                CurrentHP -= hpDrain;
                ShowInfo($"Hazardous terrain! Lost {hpDrain} HP.");
                if (CurrentHP <= 0)
                {
                    CurrentHP = 0;
                    EndRun(false);
                    return;
                }
            }
        }

        CenterCamera();
        UpdateUI();
    }

    private void OnHexClicked(Vector2I axial)
    {
        if (RunComplete)
            return;
        _party.TryMoveTo(axial);
    }

    private void OnPartyArrived(Vector2I coord)
    {
        if (RunComplete)
            return;

        if (!_grid.Hexes.TryGetValue(coord, out var hex))
            return;

        if (hex.POI == OverworldHex.POIType.None || hex.POIConsumed)
        {
            if (StepsRemaining <= 5 && StepsRemaining > 0)
                ShowInfo($"Low on steps! {StepsRemaining} remaining.");
            return;
        }

        // Debug: force a specific encounter type
        var poiType = hex.POI;
        if (PlayerSession.DebugMode && PlayerSession.ForceNextEncounterType >= 0)
        {
            poiType = (OverworldHex.POIType)PlayerSession.ForceNextEncounterType;
            PlayerSession.ForceNextEncounterType = -1;
            GD.Print($"[Debug] Forcing encounter type: {poiType}");
        }

        switch (poiType)
        {
            case OverworldHex.POIType.Combat:
                OpenScoutReport(coord, hex);
                break;

            case OverworldHex.POIType.Rest:
                int healAmount = MaxHP / 4;
                CurrentHP = Mathf.Min(CurrentHP + healAmount, MaxHP);
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                int restSplinters = SplinterDropTable.RestSite();
                SplinterEarned += restSplinters;
                ShowInfo($"Rest site. Recovered {healAmount} HP. +{restSplinters} Arcane Splinters.");
                GoldEarned += 15;
                UpdateUI();
                break;

            case OverworldHex.POIType.Objective:
                ShowInfo("Objective reached! Run complete.");
                GoldEarned += 100;
                EndRun(true);
                break;

            case OverworldHex.POIType.Narrative:
                TriggerNarrativeEncounter(hex, coord);
                break;

            case OverworldHex.POIType.Negotiation:
                TriggerNegotiationEncounter(hex, coord);
                break;

            case OverworldHex.POIType.Outpost:
                // Full-heal checkpoint — heavier than Rest's quarter-heal.
                CurrentHP = MaxHP;
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                int outpostSplinters = SplinterDropTable.RestSite();
                SplinterEarned += outpostSplinters;
                GoldEarned += 25;
                // Checkpoint: persist current map + position immediately so the
                // outpost survives an app close mid-run. RegionMemoryService already
                // knows how to round-trip fog/POI/seed.
                if (SaveManager.ActiveSave != null)
                {
                    string outpostRegionId = _region?.Id
                        ?? SaveManager.ActiveSave.CurrentRegionId ?? "frontier_wilds";
                    RegionMemoryService.Save(outpostRegionId, _grid,
                        _party.CurrentCoord, objectiveReached: false);
                    SaveManager.Save();
                }
                ShowInfo($"Outpost secured. Fully rested. +{outpostSplinters} Arcane Splinters. Checkpoint saved.");
                UpdateUI();
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Terrain helpers
    // ════════════════════════════════════════════════════════════════════════

    private int GetTerrainStepCost(OverworldHex.TerrainType terrain)
    {
        return terrain switch
        {
            OverworldHex.TerrainType.Road => 1,
            OverworldHex.TerrainType.Grassland => 1,
            OverworldHex.TerrainType.ArcaneGround => 1,
            OverworldHex.TerrainType.Forest => 2,
            OverworldHex.TerrainType.Ruins => 2,
            OverworldHex.TerrainType.Swamp => 2,
            OverworldHex.TerrainType.Mountain => 3,
            OverworldHex.TerrainType.Volcanic => 2,
            _ => 1
        };
    }

    private int GetTerrainHPDrain(OverworldHex.TerrainType terrain)
    {
        return terrain switch
        {
            OverworldHex.TerrainType.Swamp => 3,
            OverworldHex.TerrainType.Volcanic => GD.Randf() < 0.3f ? 5 : 0,
            _ => 0
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Narrative / Negotiation encounter handlers (unchanged)
    // ════════════════════════════════════════════════════════════════════════

    private void TriggerNarrativeEncounter(OverworldHex hex, Vector2I coord)
    {
        string terrainName = hex.Terrain.ToString();
        var completedIds = SaveManager.ActiveSave?.CompletedEvents;

        var encounter = NarrativeEncounterLoader.PickRandom(
            _encounterPool, terrainName, completedIds);

        if (encounter == null)
        {
            int gold = 15 + (int)(GD.Randf() * 20);
            GoldEarned += gold;
            hex.POIConsumed = true;
            hex.RefreshVisuals();
            ShowInfo($"You find something of value here. (+{gold} gold)");
            UpdateUI();
            return;
        }

        hex.POIConsumed = true;
        hex.RefreshVisuals();

        _narrativePanel.ShowEncounter(encounter);
        _narrativePanel.OnCompleted = (choice) => OnNarrativeCompleted(encounter, choice);
    }

    private void OnNarrativeCompleted(NarrativeEncounterData encounter, EncounterChoice choice)
    {
        if (choice == null)
            return;

        if (choice.GoldDelta != 0)
            GoldEarned = Mathf.Max(0, GoldEarned + choice.GoldDelta);

        if (choice.HPDelta != 0)
        {
            CurrentHP = Mathf.Clamp(CurrentHP + choice.HPDelta, 0, MaxHP);

            if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
                CurrentHP = Mathf.Max(1, CurrentHP);

            if (CurrentHP <= 0)
            {
                EndRun(false);
                return;
            }
        }

        if (choice.StepDelta != 0)
            StepsRemaining = Mathf.Max(0, StepsRemaining + choice.StepDelta);

        int narrativeSplinters = SplinterDropTable.Narrative();
        SplinterEarned += narrativeSplinters;

        if (SaveManager.ActiveSave != null && !string.IsNullOrEmpty(encounter.Id))
        {
            if (!SaveManager.ActiveSave.CompletedEvents.Contains(encounter.Id))
                SaveManager.ActiveSave.CompletedEvents.Add(encounter.Id);
        }

        if (choice.SetFlags != null && SaveManager.ActiveSave != null)
        {
            foreach (var flag in choice.SetFlags)
            {
                if (!SaveManager.ActiveSave.CompletedEvents.Contains(flag))
                    SaveManager.ActiveSave.CompletedEvents.Add(flag);
            }
        }

        ShowInfo($"Encounter resolved. +{narrativeSplinters} Arcane Splinters.");
        UpdateUI();
    }

    private void TriggerNegotiationEncounter(OverworldHex hex, Vector2I coord)
    {
        hex.POIConsumed = true;
        hex.RefreshVisuals();

        string regionId = _region?.Id ?? "frontier_wilds";
        string terrain = hex.Terrain.ToString();

        var encounter = NegotiationEncounterLoader.PickForTerrain(terrain, regionId);
        if (encounter == null)
        {
            ShowInfo("A potential contact slips away before you can speak.");
            UpdateUI();
            return;
        }

        NegotiationContext.Clear();
        NegotiationContext.EncounterId = encounter.Id;
        NegotiationContext.HexCoordKey = $"{coord.X},{coord.Y}";

        var router = EncounterRouter.Instance;
        if (router != null)
        {
            router.SavedStepsRemaining = StepsRemaining;
            router.SavedCurrentHP = CurrentHP;
            router.SavedGoldEarned = GoldEarned;
            router.SavedSplinterEarned = SplinterEarned;
            router.SavedEncountersWon = EncountersWon;
            router.SavedPartyCoord = _party.CurrentCoord;
            router.SavedCombatHexCoord = coord;
            router.HasPendingReturn = true;
            router.HasSavedSeed = true;
            router.SavedRunSeed = _grid.Seed;

            foreach (var kvp in _grid.Hexes)
            {
                router.SavedFogStates[kvp.Key] = kvp.Value.Fog;
                router.SavedPOIConsumed[kvp.Key] = kvp.Value.POIConsumed;
            }
        }

        ShowInfo($"Negotiation: {encounter.Title}");
        GetTree().ChangeSceneToFile("res://Scenes/Negotiation/NegotiationScene.tscn");
    }

    private void OnNegotiationReturned(Vector2I hexCoord)
    {
        if (NegotiationContext.DealAccepted)
        {
            GoldEarned += NegotiationContext.GoldDelta;
            GoldEarned = Mathf.Max(0, GoldEarned);

            if (SaveManager.ActiveSave != null && !string.IsNullOrEmpty(NegotiationContext.FactionId))
            {
                var rep = SaveManager.ActiveSave.FactionReputation;
                string faction = NegotiationContext.FactionId;
                rep[faction] = rep.TryGetValue(faction, out int current)
                    ? current + NegotiationContext.ReputationDelta
                    : NegotiationContext.ReputationDelta;
            }

            ShowInfo($"Deal struck. Gold: {(NegotiationContext.GoldDelta >= 0 ? "+" : "")}" +
                     $"{NegotiationContext.GoldDelta}");
        }
        else
        {
            ShowInfo("No deal reached.");
        }

        NegotiationContext.Clear();
        UpdateUI();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Run end
    // ════════════════════════════════════════════════════════════════════════

    public override void _Process(double delta)
    {
        if (RunComplete || _camera == null)
            return;
        HandleCameraPan((float)delta);
    }

    private void HandleCameraPan(float delta)
    {
        var dir = Vector2.Zero;

        if (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D))
            dir.X += 1f;
        if (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A))
            dir.X -= 1f;
        if (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S))
            dir.Y += 1f;
        if (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W))
            dir.Y -= 1f;

        if (dir != Vector2.Zero)
        {
            _cameraFreeMode = true;
            _camera.Position += dir.Normalized() * CameraPanSpeed * delta / _camera.Zoom.X;
        }
    }

    private void EndRun(bool reachedObjective)
    {
        RunComplete = true;
        PlayerSession.IsOnExpedition = false;

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        RunResultData.Set(reachedObjective, GoldEarned,
                          EncountersWon, CurrentHP, SplinterEarned);

        if (SaveManager.ActiveSave != null)
        {
            var save = SaveManager.ActiveSave;
            save.TotalRuns++;
            save.TotalGoldEarned += GoldEarned;
            save.TotalEncountersWon += EncountersWon;
            save.Gold += GoldEarned;
            save.ArcaneSplinters += SplinterEarned;

            if (reachedObjective)
                save.RunsWon++;
            else
                save.RunsLost++;

            // Persist region map state (fog, POIs, seed, objective status)
            string endRegionId = _region?.Id ?? save.CurrentRegionId ?? "frontier_wilds";
            RegionMemoryService.Save(endRegionId, _grid, _party.CurrentCoord, reachedObjective);

            SaveManager.Save();
        }

        string result = reachedObjective ? "SUCCESS" : "FAILED";
        ShowInfo($"Run {result} — Gold: {GoldEarned}, " +
                 $"Splinters: {SplinterEarned}, Encounters: {EncountersWon}.");

        // Show the clickable return button
        if (_returnButton != null)
            _returnButton.Visible = true;

        EmitSignal(SignalName.RunEnded, reachedObjective);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Building helpers
    // ════════════════════════════════════════════════════════════════════════

    private void PreRevealHexes(int count)
    {
        var candidates = new System.Collections.Generic.List<Vector2I>();

        foreach (var kvp in _grid.Hexes)
        {
            int dist = _grid.Distance(kvp.Key, _grid.EntryCoord);
            if (dist >= 2 && dist <= 6 &&
                kvp.Value.Fog == OverworldHex.FogState.Hidden)
                candidates.Add(kvp.Key);
        }

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = (int)(GD.Randi() % (uint)(i + 1));
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        int revealed = 0;
        foreach (var coord in candidates)
        {
            if (revealed >= count)
                break;
            if (_grid.Hexes.TryGetValue(coord, out var hex))
            {
                hex.Fog = OverworldHex.FogState.Revealed;
                hex.RefreshVisuals();
                revealed++;
            }
        }

        if (revealed > 0)
            GD.Print($"[Buildings] Pre-revealed {revealed} hexes (Courier Station).");
    }

    private void RevealAllFog()
    {
        foreach (var hex in _grid.Hexes.Values)
        {
            hex.Fog = OverworldHex.FogState.Revealed;
            hex.RefreshVisuals();
        }
        GD.Print("[Debug] All fog revealed.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // UI helpers
    // ════════════════════════════════════════════════════════════════════════

    private void UpdateUI()
    {
        _stepLabel.Text = (PlayerSession.DebugMode && PlayerSession.UnlimitedSteps)
            ? "Steps: ∞ [DEBUG]"
            : $"Steps: {StepsRemaining} / {StepBudget}";
        _stepLabel.Modulate = StepsRemaining > 5
            ? Colors.White
            : UITheme.OverworldLowResourceWarning;

        _hpLabel.Text = $"HP: {CurrentHP} / {MaxHP}";
        _hpLabel.Modulate = CurrentHP > MaxHP / 3
            ? Colors.White
            : UITheme.OverworldLowResourceWarning;

        int dist = _grid.Distance(_party.CurrentCoord, _grid.ObjectiveCoord);
        _objectiveLabel.Text = $"Objective: ~{dist} hexes away";

        if (_grid.Hexes.TryGetValue(_party.CurrentCoord, out var currentHex))
            _objectiveLabel.Text += $"  |  Terrain: {currentHex.Terrain}";
    }

    private void ShowInfo(string message)
    {
        _infoLabel.Text = message;
        GD.Print($"[Run] {message}");
    }

    private void CenterCamera()
    {
        if (_camera != null)
        {
            _camera.Position = _party.Position;
            _cameraFreeMode = false;
        }
    }

    /// <summary>Label for inside the HUD panel — no position needed, VBox handles layout.</summary>
    private Label MakeHUDLabel()
    {
        var label = new Label { AutowrapMode = TextServer.AutowrapMode.Off };
        label.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        return label;
    }
}
