using Godot;
using System.Collections.Generic;

/// <summary>
/// Manages a single exploration run: step budget, input routing,
/// POI triggering, and run completion detection.
/// Attach this to the root of your OverworldScene.
/// </summary>
public partial class RunManager : Node2D
{
    [Export] public int StepBudget = 20;
    [Export] public int ExhaustionDamagePerStep = 10;
    [Export] public int MaxHP = 100;

    // ── Runtime state (public for save/restore) ─────────────────────────
    public int StepsRemaining { get; set; }
    public int CurrentHP { get; set; }
    public int GoldEarned { get; set; }
    public int EncountersWon { get; set; }
    public bool RunComplete { get; private set; }

    // ── Node references ─────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private FogOfWarManager _fog;
    private OverworldPartyToken _party;
    private Camera2D _camera;
    private RegionDefinition _region;
    private NarrativeEncounterPanel _narrativePanel;
    private List<NarrativeEncounterData> _encounterPool;

    // ── UI ───────────────────────────────────────────────────────────────
    private Label _stepLabel;
    private Label _hpLabel;
    private Label _infoLabel;
    private Label _objectiveLabel;

    [Signal] public delegate void RunEndedEventHandler(bool reachedObjective);

    // ── Accessors for EncounterRouter ───────────────────────────────────
    public Vector2I GetPartyCoord() => _party.CurrentCoord;
    public OverworldHexGrid GetGrid() => _grid;

    public override void _Ready()
    {
        // ── Make sure the router exists FIRST so we can read saved seed/state ──
        EnsureEncounterRouter();
        var router = EncounterRouter.Instance;

        // ── Decide which seed to use for this overworld ─────────────────────
        int seed;
        if (router != null && router.HasSavedSeed)
        {
            seed = router.SavedRunSeed;
            GD.Print($"RunManager: Reusing saved seed {seed} (returning from combat).");
        }
        else
        {
            seed = (int)GD.Randi();
            GD.Print($"RunManager: New run with seed {seed}.");
        }

        // ── Load the region for this run ───────────────────────────
        string regionId = SaveManager.ActiveSave?.CurrentRegionId ?? "frontier_wilds";
        _region = RegionLoader.LoadOrDefault(regionId);

        if (_region != null)
        {
            // Sync step budget from region
            StepBudget = _region.StepBudget;
            GD.Print($"RunManager: Loaded region '{_region.DisplayName}' " +
                    $"(StepBudget={StepBudget}, POIs: {_region.CombatPOICount}/" +
                    $"{_region.RestPOICount}/{_region.NarrativePOICount}/" +
                    $"{_region.NegotiationPOICount})");
        }

        // ── Build the grid with that seed ───────────────────────────────────
        _grid = new OverworldHexGrid { Name = "HexGrid", Seed = seed };
        _grid.Region = _region;
        AddChild(_grid);

        // ── POIs use region counts (or defaults if no region) ───────────
        int combatCount    = _region?.CombatPOICount    ?? 10;
        int restCount      = _region?.RestPOICount      ?? 4;
        int narrativeCount = _region?.NarrativePOICount ?? 3;
        int negotiationCount = _region?.NegotiationPOICount ?? 2;
        POIGenerator.Generate(_grid, combatCount, restCount, narrativeCount, negotiationCount, seed);

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

        _stepLabel = MakeUILabel(new Vector2(20, 20));
        canvas.AddChild(_stepLabel);

        _hpLabel = MakeUILabel(new Vector2(20, 55));
        canvas.AddChild(_hpLabel);

        _objectiveLabel = MakeUILabel(new Vector2(20, 90));
        canvas.AddChild(_objectiveLabel);

        _infoLabel = MakeUILabel(new Vector2(20, 130));
        _infoLabel.Modulate = new Color(1f, 1f, 0.7f);
        canvas.AddChild(_infoLabel);

        // ── Initialize run state defaults (may be overwritten by restore) ───
        StepsRemaining = StepBudget;
        CurrentHP = MaxHP;
        GoldEarned = 0;
        EncountersWon = 0;
        RunComplete = false;

        // ── Apply building bonuses ──────────────────────────────────────
        var buildingBonuses = BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        MaxHP += buildingBonuses.BonusHP;
        CurrentHP = MaxHP;
        StepBudget += buildingBonuses.BonusSteps;
        StepsRemaining = StepBudget;
        GoldEarned += buildingBonuses.BonusGold;

        // Apply debug flags
        if (PlayerSession.DebugMode && PlayerSession.StartWithGold)
            GoldEarned += 500;

        // ── Restore from combat or place at entry ───────────────────────────
        if (router != null && router.HasPendingReturn)
        {
            RestoreFromCombat(router);
        }
        else
        {
            _party.Initialize(_grid, _fog, _grid.EntryCoord);
            ShowInfo("Explore the map. Reach the golden objective marker.");

            // Pre-reveal hexes from Courier Station
            if (buildingBonuses.PreRevealHexCount > 0)
                PreRevealHexes(buildingBonuses.PreRevealHexCount);

            // Apply fog after pre-reveals
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

        // Debug: reveal all fog
        if (PlayerSession.DebugMode && PlayerSession.NoFog)
            RevealAllFog();

        CenterCamera();
        UpdateUI();
    }

    /// <summary>
    /// Restore overworld state after returning from a combat encounter.
    /// </summary>
    private void RestoreFromCombat(EncounterRouter router)
    {
        GD.Print("RunManager: Restoring state from combat...");

        // Restore run state
        StepsRemaining = router.SavedStepsRemaining;
        CurrentHP = router.SavedCurrentHP;
        GoldEarned = router.SavedGoldEarned;
        EncountersWon = router.SavedEncountersWon;

        // Restore fog state
        foreach (var kvp in router.SavedFogStates)
        {
            if (_grid.Hexes.TryGetValue(kvp.Key, out var hex))
            {
                hex.Fog = kvp.Value;
            }
        }

        // Restore POI consumed state
        foreach (var kvp in router.SavedPOIConsumed)
        {
            if (_grid.Hexes.TryGetValue(kvp.Key, out var hex))
            {
                hex.POIConsumed = kvp.Value;
            }
        }

        // Refresh all hex visuals after restoring state
        foreach (var hex in _grid.Hexes.Values)
            hex.RefreshVisuals();

        // Place party at saved position
        _party.Initialize(_grid, _fog, router.SavedPartyCoord);

        // Apply results — negotiation or combat
        var resultHex = router.SavedCombatHexCoord;

        if (NegotiationContext.HasResult)
        {
            OnNegotiationReturned(resultHex);
        }
        else
        {
            // Combat result
            if (router.CombatWon)
            {
                GoldEarned += router.GoldReward;
                EncountersWon++;

                if (_grid.Hexes.TryGetValue(resultHex, out var hex))
                {
                    hex.POIConsumed = true;
                    hex.RefreshVisuals();
                }

                ShowInfo($"Victory! Earned {router.GoldReward} gold.");
            }
            else
            {
                CurrentHP -= router.DamageTaken;

                // Debug: God mode prevents death
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

    /// <summary>
    /// Make sure the EncounterRouter singleton exists in the tree.
    /// It lives on the root so it survives scene changes.
    /// </summary>
    private void EnsureEncounterRouter()
    {
        if (EncounterRouter.Instance != null) return;

        var router = new EncounterRouter { Name = "EncounterRouter" };
        router.CombatScenePath = "res://Scenes/Combat/Battlefield.tscn";
        router.OverworldScenePath = "res://Scenes/Overworld/OverworldScene.tscn"; // ← FIXED

        // Add immediately, not deferred — we need it ready for the seed check below.
        GetTree().Root.AddChild(router);
        GD.Print("RunManager: Created EncounterRouter on tree root.");
    }

 private void OnPartyMoved(Vector2I newCoord, Vector2I oldCoord)
    {
        int stepCost = 1;
        int hpDrain = 0;

        if (_grid.Hexes.TryGetValue(newCoord, out var hex))
        {
            stepCost = GetTerrainStepCost(hex.Terrain);
            hpDrain = GetTerrainHPDrain(hex.Terrain);
        }

        // Debug: unlimited steps skips all step/exhaustion logic
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
        if (RunComplete) return;
        _party.TryMoveTo(axial);
    }
    
    private void OnPartyArrived(Vector2I coord)
    {
        if (RunComplete) return;

        if (!_grid.Hexes.TryGetValue(coord, out var hex)) return;

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
            PlayerSession.ForceNextEncounterType = -1; // consume — only forces once
            GD.Print($"[Debug] Forcing encounter type: {poiType}");
        }

        switch (poiType)
        {
            case OverworldHex.POIType.Combat:
                ShowInfo("Combat encounter! (Press SPACE to fight, ESC to skip)");
                _pendingCombatHex = coord;
                break;

            case OverworldHex.POIType.Rest:
                int healAmount = MaxHP / 4;
                CurrentHP = Mathf.Min(CurrentHP + healAmount, MaxHP);
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                ShowInfo($"Rest site. Recovered {healAmount} HP.");
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
        }
    }

    private int GetTerrainStepCost(OverworldHex.TerrainType terrain)
    {
        return terrain switch
        {
            OverworldHex.TerrainType.Road         => 1,
            OverworldHex.TerrainType.Grassland     => 1,
            OverworldHex.TerrainType.ArcaneGround  => 1,
            OverworldHex.TerrainType.Forest         => 2,
            OverworldHex.TerrainType.Ruins          => 2,
            OverworldHex.TerrainType.Swamp          => 2,
            OverworldHex.TerrainType.Mountain       => 3,
            OverworldHex.TerrainType.Volcanic       => 2,
            _ => 1
        };
    }

    private int GetTerrainHPDrain(OverworldHex.TerrainType terrain)
    {
        return terrain switch
        {
            OverworldHex.TerrainType.Swamp    => 3,
            OverworldHex.TerrainType.Volcanic  => GD.Randf() < 0.3f ? 5 : 0, // 30% chance
            _ => 0
        };
    }

    private Vector2I? _pendingCombatHex = null;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed)
        {
            if (_pendingCombatHex.HasValue && key.Keycode == Key.Space)
            {
                StartRealCombat(_pendingCombatHex.Value);
                _pendingCombatHex = null;
            }
            else if (_pendingCombatHex.HasValue && key.Keycode == Key.Escape)
            {
                ShowInfo("Skipped encounter. It remains on the map.");
                _pendingCombatHex = null;
            }
        }
    }

    /// <summary>
    /// Save state and swap to the combat scene.
    /// </summary>
    private void StartRealCombat(Vector2I hexCoord)
    {
        var router = EncounterRouter.Instance;
        if (router == null)
        {
            GD.PrintErr("RunManager: EncounterRouter not found!");
            return;
        }

        ShowInfo("Entering combat...");
        router.StartCombat(this, hexCoord);
    }

    private void TriggerNarrativeEncounter(OverworldHex hex, Vector2I coord)
    {
        string terrainName = hex.Terrain.ToString();
        var completedIds = SaveManager.ActiveSave?.CompletedEvents;

        var encounter = NarrativeEncounterLoader.PickRandom(
            _encounterPool, terrainName, completedIds);

        if (encounter == null)
        {
            // Pool exhausted — fall back to a small reward
            int gold = 15 + (int)(GD.Randf() * 20);
            GoldEarned += gold;
            hex.POIConsumed = true;
            hex.RefreshVisuals();
            ShowInfo($"You find something of value here. (+{gold} gold)");
            UpdateUI();
            return;
        }

        // Mark consumed immediately so re-entering doesn't re-trigger
        hex.POIConsumed = true;
        hex.RefreshVisuals();

        _narrativePanel.ShowEncounter(encounter);
        _narrativePanel.OnCompleted = (choice) => OnNarrativeCompleted(encounter, choice);
    }

    private void OnNarrativeCompleted(NarrativeEncounterData encounter, EncounterChoice choice)
    {
        if (choice == null) return;

        if (choice.GoldDelta != 0)
            GoldEarned = Mathf.Max(0, GoldEarned + choice.GoldDelta);

        if (choice.HPDelta != 0)
        {
            CurrentHP = Mathf.Clamp(CurrentHP + choice.HPDelta, 0, MaxHP);

            // Debug: God mode prevents death
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

        // Track completed unique encounters in save data
        if (SaveManager.ActiveSave != null && !string.IsNullOrEmpty(encounter.Id))
        {
            if (!SaveManager.ActiveSave.CompletedEvents.Contains(encounter.Id))
                SaveManager.ActiveSave.CompletedEvents.Add(encounter.Id);
        }

        // Apply any flags set by the choice
        if (choice.SetFlags != null && SaveManager.ActiveSave != null)
        {
            foreach (var flag in choice.SetFlags)
            {
                if (!SaveManager.ActiveSave.CompletedEvents.Contains(flag))
                    SaveManager.ActiveSave.CompletedEvents.Add(flag);
            }
        }

        ShowInfo($"Encounter resolved.");
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

        // Store context and save overworld state
        NegotiationContext.Clear();
        NegotiationContext.EncounterId = encounter.Id;
        NegotiationContext.HexCoordKey = $"{coord.X},{coord.Y}";

        // Save overworld state the same way combat does
        var router = EncounterRouter.Instance;
        if (router != null)
        {
            router.SavedStepsRemaining = StepsRemaining;
            router.SavedCurrentHP = CurrentHP;
            router.SavedGoldEarned = GoldEarned;
            router.SavedEncountersWon = EncountersWon;
            router.SavedPartyCoord = _party.CurrentCoord;
            router.SavedCombatHexCoord = coord;     // reuse field for the triggering hex
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

            // Apply steps delta from terms if any
            // (Phase 3: iterate terms and apply each one)

            // Apply reputation to save
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

    private void EndRun(bool reachedObjective)
    {
        RunComplete = true;

        // Run is over — clear the saved seed so the next run gets a fresh map
        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        // Store results for the campus screen
        RunResultData.Set(reachedObjective, GoldEarned, EncountersWon, CurrentHP);

        // ── Update persistent save data ─────────────────────────────────
        if (SaveManager.ActiveSave != null)
        {
            var save = SaveManager.ActiveSave;
            save.TotalRuns++;
            save.TotalGoldEarned += GoldEarned;
            save.TotalEncountersWon += EncountersWon;
            save.Gold += GoldEarned;

            if (reachedObjective)
                save.RunsWon++;
            else
                save.RunsLost++;

            SaveManager.Save();
            GD.Print($"SaveManager: Run recorded. Gold: {save.Gold}, Runs: {save.TotalRuns}, Won: {save.RunsWon}");
        }

        string result = reachedObjective ? "SUCCESS" : "FAILED";
        ShowInfo($"Run {result} — Gold: {GoldEarned}, Encounters: {EncountersWon}. Press R to return to campus.");
        EmitSignal(SignalName.RunEnded, reachedObjective);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.R && RunComplete)
        {
            // Return to campus instead of reloading the overworld
            GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn");
        }
    }

    // ── Building helpers ─────────────────────────────────────────────────

    private void PreRevealHexes(int count)
    {
        // Reveal random hexes near the entry point
        var candidates = new System.Collections.Generic.List<Vector2I>();

        foreach (var kvp in _grid.Hexes)
        {
            int dist = _grid.Distance(kvp.Key, _grid.EntryCoord);
            if (dist >= 2 && dist <= 6 &&
                kvp.Value.Fog == OverworldHex.FogState.Hidden)
                candidates.Add(kvp.Key);
        }

        // Shuffle candidates
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = (int)(GD.Randi() % (uint)(i + 1));
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        int revealed = 0;
        foreach (var coord in candidates)
        {
            if (revealed >= count) break;
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

    // ── UI helpers ──────────────────────────────────────────────────────

    private void UpdateUI()
    {
        _stepLabel.Text = (PlayerSession.DebugMode && PlayerSession.UnlimitedSteps)
            ? "Steps: ∞ [DEBUG]"
            : $"Steps: {StepsRemaining} / {StepBudget}";
        _stepLabel.Modulate = StepsRemaining > 5
            ? Colors.White
            : new Color(1f, 0.4f, 0.4f);

        _hpLabel.Text = $"HP: {CurrentHP} / {MaxHP}";
        _hpLabel.Modulate = CurrentHP > MaxHP / 3
            ? Colors.White
            : new Color(1f, 0.4f, 0.4f);

        int dist = _grid.Distance(_party.CurrentCoord, _grid.ObjectiveCoord);
        _objectiveLabel.Text = $"Objective: ~{dist} hexes away";

        // Show terrain info for current hex
        if (_grid.Hexes.TryGetValue(_party.CurrentCoord, out var currentHex))
        {
            string terrainName = currentHex.Terrain.ToString();
            _objectiveLabel.Text += $"  |  Terrain: {terrainName}";
        }
    }

    private void ShowInfo(string message)
    {
        _infoLabel.Text = message;
        GD.Print($"[Run] {message}");
    }

    private void CenterCamera()
    {
        if (_camera != null)
            _camera.Position = _party.Position;
    }

    private Label MakeUILabel(Vector2 pos)
    {
        var label = new Label { Position = pos };
        label.AddThemeFontSizeOverride("font_size", 18);
        return label;
    }
}