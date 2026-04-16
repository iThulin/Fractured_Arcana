using Godot;

/// <summary>
/// Manages a single exploration run: step budget, input routing,
/// POI triggering, and run completion detection.
/// Attach this to the root of your OverworldScene.
/// </summary>
public partial class RunManager : Node2D
{
    [Export] public int StepBudget = 30;
    [Export] public int ExhaustionDamagePerStep = 10;
    [Export] public int MaxHP = 100;

    // ── Runtime state ───────────────────────────────────────────────────
    public int StepsRemaining { get; private set; }
    public int CurrentHP { get; private set; }
    public int GoldEarned { get; private set; }
    public int EncountersWon { get; private set; }
    public bool RunComplete { get; private set; }

    // ── Node references ─────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private FogOfWarManager _fog;
    private OverworldPartyToken _party;
    private Camera2D _camera;
	private EncounterRouter _encounterRouter;

    // ── UI ───────────────────────────────────────────────────────────────
    private Label _stepLabel;
    private Label _hpLabel;
    private Label _infoLabel;
    private Label _objectiveLabel;

    [Signal] public delegate void RunEndedEventHandler(bool reachedObjective);
    [Signal] public delegate void CombatTriggeredEventHandler(Vector2I hexCoord);

    public override void _Ready()
    {
        // ── Build the scene tree ────────────────────────────────────────
        // Grid
        _grid = new OverworldHexGrid { Name = "HexGrid" };
        AddChild(_grid);

		// Place encounters
		POIGenerator.Generate(_grid, combatCount: 7, restCount: 3);

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

		// Encounter router 
        _encounterRouter = new EncounterRouter { Name = "EncounterRouter" };
        _encounterRouter.CombatScenePath = "res://Scenes/Combat/Battlefield.tscn";
        AddChild(_encounterRouter);

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

        // ── Initialize run ──────────────────────────────────────────────
        StepsRemaining = StepBudget;
        CurrentHP = MaxHP;
        GoldEarned = 0;
        EncountersWon = 0;
        RunComplete = false;

        _party.Initialize(_grid, _fog, _grid.EntryCoord);
        CenterCamera();

        // ── Wire signals ────────────────────────────────────────────────
        _grid.HexClicked += OnHexClicked;
        _party.PartyMoved += OnPartyMoved;
        _party.PartyArrived += OnPartyArrived;

        UpdateUI();
        ShowInfo("Explore the map. Reach the golden objective marker.");
    }

    private void OnHexClicked(Vector2I axial)
    {
        if (RunComplete) return;

        _party.TryMoveTo(axial);
    }

    private void OnPartyMoved(Vector2I newCoord, Vector2I oldCoord)
    {
        // Spend a step (all terrain costs 1 in Phase 1)
        if (StepsRemaining > 0)
        {
            StepsRemaining--;
        }
        else
        {
            // Exhaustion — take damage for moving past the budget
            CurrentHP -= ExhaustionDamagePerStep;
            if (CurrentHP <= 0)
            {
                CurrentHP = 0;
                EndRun(false);
                return;
            }
        }

        // Camera follow
        CenterCamera();
        UpdateUI();
    }

    private void OnPartyArrived(Vector2I coord)
    {
        if (RunComplete) return;

        // Check what's on this hex
        if (!_grid.Hexes.TryGetValue(coord, out var hex)) return;

        if (hex.POI == OverworldHex.POIType.None || hex.POIConsumed) 
        {
            // Empty hex or already consumed — check for step budget warning
            if (StepsRemaining <= 5 && StepsRemaining > 0)
                ShowInfo($"Low on steps! {StepsRemaining} remaining.");
            return;
        }

        switch (hex.POI)
        {
            case OverworldHex.POIType.Combat:
                ShowInfo("Combat encounter! (Press SPACE to fight, ESC to skip for now)");
                // Store the pending combat hex — we'll resolve it on keypress
                _pendingCombatHex = coord;
                break;

            case OverworldHex.POIType.Rest:
                // Simple rest: heal and mark consumed
                int healAmount = MaxHP / 4;
                CurrentHP = Mathf.Min(CurrentHP + healAmount, MaxHP);
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                ShowInfo($"Rest site. Recovered {healAmount} HP.");
                GoldEarned += 15; // small gold bonus for discovery
                UpdateUI();
                break;

            case OverworldHex.POIType.Objective:
                ShowInfo("Objective reached! Run complete.");
                GoldEarned += 100;
                EndRun(true);
                break;
        }
    }

    private Vector2I? _pendingCombatHex = null;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed)
        {
            // Combat confirmation
            if (_pendingCombatHex.HasValue && key.Keycode == Key.Space)
            {
                ResolveCombat(_pendingCombatHex.Value);
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
    /// Phase 1 combat: simulated coin-flip. 
    /// Phase 2 will swap this for EncounterRouter → GameRunner.
    /// </summary>
    private void ResolveCombat(Vector2I hexCoord)
    {
        if (!_grid.Hexes.TryGetValue(hexCoord, out var hex)) return;

        var context = new EncounterContext
        {
            SourcePOI = hex.POI,
            SourceTerrain = hex.Terrain,
            EnemyCount = 3,
            PlayerCount = 2
        };

        ShowInfo("Entering combat...");

        _encounterRouter.StartCombat(this, context, (result) =>
        {
            // This runs after combat ends and we're back on the overworld
            OnCombatReturned(hexCoord, result);
        });
    }

	private void OnCombatReturned(Vector2I hexCoord, EncounterContext result)
    {
        if (!_grid.Hexes.TryGetValue(hexCoord, out var hex)) return;

        if (result.PlayerWon)
        {
            GoldEarned += result.GoldReward;
            EncountersWon++;
            hex.POIConsumed = true;
            hex.RefreshVisuals();
            ShowInfo($"Victory! Earned {result.GoldReward} gold.");
        }
        else
        {
            CurrentHP -= result.DamageTaken;
            hex.POIConsumed = true;
            hex.RefreshVisuals();

            if (CurrentHP <= 0)
            {
                CurrentHP = 0;
                ShowInfo("Defeated! Run over.");
                EndRun(false);
                UpdateUI();
                return;
            }
            ShowInfo($"Defeated... Lost {result.DamageTaken} HP.");
        }

        UpdateUI();
    }

    private void EndRun(bool reachedObjective)
    {
        RunComplete = true;
        string result = reachedObjective ? "SUCCESS" : "FAILED";
        ShowInfo($"Run {result} — Gold: {GoldEarned}, Encounters won: {EncountersWon}. Press R to return.");
        EmitSignal(SignalName.RunEnded, reachedObjective);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.R && RunComplete)
        {
            // Return to campus (Phase 1: just reload the scene to start a new run)
            GetTree().ReloadCurrentScene();
        }
    }

    // ── UI helpers ──────────────────────────────────────────────────────

    private void UpdateUI()
    {
        string stepColor = StepsRemaining > 5 ? "white" : "red";
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