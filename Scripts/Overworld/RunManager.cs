using Godot;

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
        // ── Build the scene tree ────────────────────────────────────────

        // Grid
        _grid = new OverworldHexGrid { Name = "HexGrid" };
        AddChild(_grid);

        // Place encounters
        POIGenerator.Generate(_grid, combatCount: 10, restCount: 4);

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

        // ── Ensure EncounterRouter exists (persistent across scenes) ────
        EnsureEncounterRouter();

        // ── UI Layer ────────────────────────────────────────────────────
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

        // ── Initialize run state ────────────────────────────────────────
        StepsRemaining = StepBudget;
        CurrentHP = MaxHP;
        GoldEarned = 0;
        EncountersWon = 0;
        RunComplete = false;

        // ── Check if we're returning from combat ────────────────────────
        var router = EncounterRouter.Instance;
        if (router != null && router.HasPendingReturn)
        {
            RestoreFromCombat(router);
        }
        else
        {
            // Fresh run — place party at entry
            _party.Initialize(_grid, _fog, _grid.EntryCoord);
            ShowInfo("Explore the map. Reach the golden objective marker.");
        }

        // ── Wire signals ────────────────────────────────────────────────
        _grid.HexClicked += OnHexClicked;
        _party.PartyMoved += OnPartyMoved;
        _party.PartyArrived += OnPartyArrived;

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

        // Apply combat results
        var combatHex = router.SavedCombatHexCoord;
        if (router.CombatWon)
        {
            GoldEarned += router.GoldReward;
            EncountersWon++;

            // Mark the combat POI as consumed
            if (_grid.Hexes.TryGetValue(combatHex, out var hex))
            {
                hex.POIConsumed = true;
                hex.RefreshVisuals();
            }

            ShowInfo($"Victory! Earned {router.GoldReward} gold.");
        }
        else
        {
            CurrentHP -= router.DamageTaken;

            // Mark consumed even on defeat
            if (_grid.Hexes.TryGetValue(combatHex, out var hex))
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

        // Clear the pending return flag
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
        router.OverworldScenePath = "res://Scenes/Overworld/CampusScene.tscn";

        // Defer the add since the tree is busy during _Ready()
        GetTree().Root.CallDeferred("add_child", router);
        GD.Print("RunManager: Created EncounterRouter on tree root (deferred).");
    }

    private void OnHexClicked(Vector2I axial)
    {
        if (RunComplete) return;
        _party.TryMoveTo(axial);
    }

    private void OnPartyMoved(Vector2I newCoord, Vector2I oldCoord)
    {
        // Get terrain cost for the hex we just moved INTO
        int stepCost = 1;
        int hpDrain = 0;

        if (_grid.Hexes.TryGetValue(newCoord, out var hex))
        {
            stepCost = GetTerrainStepCost(hex.Terrain);
            hpDrain = GetTerrainHPDrain(hex.Terrain);
        }

        if (StepsRemaining > 0)
        {
            StepsRemaining = Mathf.Max(0, StepsRemaining - stepCost);
        }
        else
        {
            // Exhaustion
            CurrentHP -= ExhaustionDamagePerStep;
            if (CurrentHP <= 0)
            {
                CurrentHP = 0;
                EndRun(false);
                return;
            }
        }

        // Terrain HP drain (swamp, volcanic)
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

        CenterCamera();
        UpdateUI();
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

        switch (hex.POI)
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
                // Phase 1 stub — simple text event with a small reward
                string[] events = {
                    "You find ancient runes carved into a standing stone. (+20 gold)",
                    "A wandering scholar shares a fragment of forgotten lore. (+15 gold)",
                    "Remnants of a battlefield. Among the debris, something glints. (+25 gold)",
                    "A shrine to a forgotten god. You feel a moment of peace. (+10 HP)",
                    "Carved into the cave wall: a map fragment showing nearby terrain. (+30 gold)",
                };
                string chosen = events[(int)(GD.Randi() % (uint)events.Length)];
                
                if (chosen.Contains("+10 HP"))
                {
                    CurrentHP = Mathf.Min(CurrentHP + 10, MaxHP);
                }
                else
                {
                    int gold = 15 + (int)(GD.Randf() * 15);
                    GoldEarned += gold;
                }
                
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                ShowInfo(chosen);
                UpdateUI();
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

    private void EndRun(bool reachedObjective)
    {
        RunComplete = true;

        // Store results for the campus screen
        RunResultData.Set(reachedObjective, GoldEarned, EncountersWon, CurrentHP);

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

    // ── UI helpers ──────────────────────────────────────────────────────

    private void UpdateUI()
    {
        _stepLabel.Text = $"Steps: {StepsRemaining} / {StepBudget}";
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