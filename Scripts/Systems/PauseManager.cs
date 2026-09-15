using Godot;

// ============================================================
// PauseManager.cs
//
// Purpose:        Autoload singleton that listens for ESC, opens
//                 the pause overlay on a high-layer CanvasLayer,
//                 and infers the right context (MainMenu /
//                 Overworld / InCombat) from the current scene
//                 path so the menu shows the right buttons.
// Layer:          System
// Collaborators:  PauseMenu.cs (the overlay scene),
//                 PauseButton.cs (alternate entry point)
// See:            (none)
// ============================================================

/// <summary>Process-wide autoload that owns the pause overlay lifecycle. ESC toggles open/close, scene changes auto-update <see cref="CurrentContext"/>, and the overlay is hosted on a CanvasLayer at depth 100 so it renders over any 2D camera-driven scene.</summary>
public partial class PauseManager : Node
{
    public static PauseManager Instance { get; private set; }

    [Export] public string PauseMenuScenePath = "res://Scenes/UI/PauseMenu.tscn";

    /// <summary>Scene-class tag used by <see cref="PauseMenu"/> to decide which optional buttons to show (Return to Campus vs Forfeit Combat).</summary>
    public enum PauseContext
    {
        MainMenu,
        Overworld,
        InCombat,
    }

    public PauseContext CurrentContext { get; private set; } = PauseContext.MainMenu;

    private CanvasLayer _layer;       // The CanvasLayer that hosts the pause menu.
    private PauseMenu _activeMenu;
    private PackedScene _pauseMenuScene;

    public bool IsPaused => _activeMenu != null && IsInstanceValid(_activeMenu);

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;

        _pauseMenuScene = GD.Load<PackedScene>(PauseMenuScenePath);
        if (_pauseMenuScene == null)
            GD.PrintErr($"[PauseManager] Could not load pause menu scene at: {PauseMenuScenePath}");

        // Refresh context whenever scenes change.
        GetTree().NodeAdded += OnNodeAdded;
    }

    private void OnNodeAdded(Node n)
    {
        // Nodes added and freed in the same frame (label markers, tweens) can
        // reach here already invalid; a node added during a scene swap can have
        // no tree yet on our side.
        if (n == null || !IsInstanceValid(n))
            return;
        var tree = GetTree();
        if (tree == null)
            return;
        if (n.GetParent() == tree.Root && n != this)
            CallDeferred(nameof(InferContextFromCurrentScene));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo
            && k.Keycode == Key.Escape)
        {
            if (IsPaused) ClosePauseMenu();
            else          OpenPauseMenu();

            GetViewport().SetInputAsHandled();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════════════

    public void OpenPauseMenu()
    {
        if (IsPaused) return;
        if (_pauseMenuScene == null) return;

        InferContextFromCurrentScene();

        // CanvasLayer guarantees the pause menu renders above any 2D scene
        // (including ones with Camera2Ds, like the overworld). Layer 100 is
        // well above default UI layers.
        _layer = new CanvasLayer
        {
            Layer = 100,
            ProcessMode = ProcessModeEnum.Always,
        };
        GetTree().Root.AddChild(_layer);

        _activeMenu = _pauseMenuScene.Instantiate<PauseMenu>();
        _layer.AddChild(_activeMenu);
        _activeMenu.Configure(CurrentContext);

        GetTree().Paused = true;
    }

    public void ClosePauseMenu()
    {
        if (_activeMenu != null && IsInstanceValid(_activeMenu))
        {
            _activeMenu.QueueFree();
            _activeMenu = null;
        }
        if (_layer != null && IsInstanceValid(_layer))
        {
            _layer.QueueFree();
            _layer = null;
        }
        GetTree().Paused = false;
    }

    public void SetContext(PauseContext ctx) => CurrentContext = ctx;

    private void InferContextFromCurrentScene()
    {
        var current = GetTree().CurrentScene;
        if (current == null) return;

        string path = current.SceneFilePath ?? "";
        string lower = path.ToLower();

        if (lower.Contains("/combat/") || lower.Contains("gamerunner") || lower.Contains("combatscene"))
            CurrentContext = PauseContext.InCombat;
        else if (lower.Contains("/overworld") || lower.Contains("overworldscene"))
            CurrentContext = PauseContext.Overworld;
        else
            CurrentContext = PauseContext.MainMenu;
    }
}
