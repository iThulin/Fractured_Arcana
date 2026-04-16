using Godot;
using System;

/// <summary>
/// Manages transitions between the overworld and combat encounters.
/// Uses additive scene loading: the overworld stays in the tree (hidden),
/// the combat scene loads on top, and results are passed back when combat ends.
/// </summary>
public partial class EncounterRouter : Node
{
    // Path to your existing combat scene — adjust this to match your project
    [Export] public string CombatScenePath = "res://Scenes/CombatScene.tscn";

    private Node _overworldRoot;
    private Node _combatInstance;
    private EncounterContext _currentContext;
    private Action<EncounterContext> _onComplete;

    // Track combat completion state to handle delayed cleanup
    private bool _combatFinished = false;
    private bool _playerWon = false;

    /// <summary>
    /// Call this to start a combat encounter.
    /// The overworld will be hidden and the combat scene loaded.
    /// When combat ends, onComplete fires with the filled EncounterContext.
    /// </summary>
    public void StartCombat(Node overworldRoot, EncounterContext context, 
                            Action<EncounterContext> onComplete)
    {
        _overworldRoot = overworldRoot;
        _currentContext = context;
        _onComplete = onComplete;
        _combatFinished = false;

        // Hide the overworld (don't free it — we want to come back)
        _overworldRoot.ProcessMode = ProcessModeEnum.Disabled;
        SetOverworldVisibility(false);

        // Load the combat scene
        var combatScene = GD.Load<PackedScene>(CombatScenePath);
        if (combatScene == null)
        {
            GD.PrintErr($"EncounterRouter: Could not load combat scene at {CombatScenePath}");
            RestoreOverworld(false);
            return;
        }

        _combatInstance = combatScene.Instantiate();
        GetTree().Root.AddChild(_combatInstance);

        // Find the GameRunner in the combat scene and wire up the completion signal
        var gameRunner = FindGameRunner(_combatInstance);
        if (gameRunner != null)
        {
            gameRunner.CombatCompleted += OnCombatCompleted;
            GD.Print("EncounterRouter: Combat scene loaded, GameRunner connected.");
        }
        else
        {
            GD.PrintErr("EncounterRouter: GameRunner not found in combat scene! " +
                        "Combat will work but results won't route back.");
        }
    }

    private void OnCombatCompleted(bool playerWon)
    {
        GD.Print($"EncounterRouter: Combat completed. Won: {playerWon}");
        _combatFinished = true;
        _playerWon = playerWon;

        // Brief delay so the player can see the Victory/Defeat UI
        // before we yank them back to the overworld
        GetTree().CreateTimer(2.0f).Timeout += () => CleanupAndReturn();
    }

    private void CleanupAndReturn()
    {
        // Fill in results
        _currentContext.PlayerWon = _playerWon;
        _currentContext.GoldReward = _playerWon ? 30 + (int)(GD.Randf() * 50) : 0;
        _currentContext.DamageTaken = _playerWon ? (int)(GD.Randf() * 20) : 30;

        // Remove combat scene
        if (_combatInstance != null)
        {
            _combatInstance.QueueFree();
            _combatInstance = null;
        }

        // Restore overworld
        RestoreOverworld(_playerWon);
    }

    private void RestoreOverworld(bool playerWon)
    {
        if (_overworldRoot != null)
        {
            _overworldRoot.ProcessMode = ProcessModeEnum.Inherit;
            SetOverworldVisibility(true);
        }

        // Fire the callback
        _onComplete?.Invoke(_currentContext);
        _onComplete = null;
        _currentContext = null;
    }

    private void SetOverworldVisibility(bool visible)
    {
        // Hide/show all CanvasItems in the overworld tree
        if (_overworldRoot is CanvasItem ci)
            ci.Visible = visible;

        foreach (var child in _overworldRoot.GetChildren())
        {
            if (child is CanvasItem childCi)
                childCi.Visible = visible;
        }
    }

    /// <summary>
    /// Recursively search for a GameRunner node in the combat scene tree.
    /// Your combat scene might have it at different nesting levels.
    /// </summary>
    private GameRunner FindGameRunner(Node root)
    {
        if (root is GameRunner gr) return gr;

        foreach (var child in root.GetChildren())
        {
            var found = FindGameRunner(child);
            if (found != null) return found;
        }
        return null;
    }
}