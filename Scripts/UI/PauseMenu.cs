using Godot;

/// <summary>
/// Pause menu overlay controller.
/// </summary>
public partial class PauseMenu : Control
{
    public const string InlineSentinel = "__INLINE__";

    [Export] public string SettingsScenePath    = "res://Scenes/UI/SettingsMenu.tscn";
    [Export] public string CardLibraryScenePath = "res://Scenes/UI/CardLibrary.tscn";
    [Export] public string MainMenuScenePath    = "res://Scenes/Campus/CampusScene.tscn";
    [Export] public string OverworldScenePath   = "res://Scenes/Overworld/OverworldScene.tscn";

    private Button _resumeButton;
    private Button _settingsButton;
    private Button _cardLibraryButton;
    private Button _returnToCampusButton;
    private Button _forfeitCombatButton;
    private Button _quitButton;

    private Control _menuContainer;
    private Control _overlayHost;
    private ColorRect _backdrop;

    private Node _currentInlinePanel;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;

        _backdrop      = GetNode<ColorRect>("Backdrop");
        _menuContainer = GetNode<Control>("MenuContainer");
        _overlayHost   = GetNode<Control>("OverlayHost");

        _resumeButton         = GetNode<Button>("MenuContainer/Margin/VBox/ResumeButton");
        _settingsButton       = GetNode<Button>("MenuContainer/Margin/VBox/SettingsButton");
        _cardLibraryButton    = GetNode<Button>("MenuContainer/Margin/VBox/CardLibraryButton");
        _returnToCampusButton = GetNode<Button>("MenuContainer/Margin/VBox/ReturnToCampusButton");
        _forfeitCombatButton  = GetNode<Button>("MenuContainer/Margin/VBox/ForfeitCombatButton");
        _quitButton           = GetNode<Button>("MenuContainer/Margin/VBox/QuitButton");

        _resumeButton.Pressed         += OnResumePressed;
        _settingsButton.Pressed       += OnSettingsPressed;
        _cardLibraryButton.Pressed    += OnCardLibraryPressed;
        _returnToCampusButton.Pressed += OnReturnToCampusPressed;
        _forfeitCombatButton.Pressed  += OnForfeitCombatPressed;
        _quitButton.Pressed           += OnQuitPressed;

        _backdrop.GuiInput += OnBackdropInput;

        RaiseMenuContainer();
    }

    public void Configure(PauseManager.PauseContext ctx)
    {
        bool inOverworld = ctx == PauseManager.PauseContext.Overworld;
        bool inCombat    = ctx == PauseManager.PauseContext.InCombat;

        _cardLibraryButton.Visible    = true;
        _returnToCampusButton.Visible = inOverworld;
        _forfeitCombatButton.Visible  = inCombat;
        _quitButton.Visible           = true;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Button handlers
    // ════════════════════════════════════════════════════════════════════════

    private void OnResumePressed() => PauseManager.Instance?.ClosePauseMenu();

    private void OnSettingsPressed()
    {
        OpenInlineOverlay(SettingsScenePath, before: inst =>
        {
            if (inst is SettingsMenu sm)
                sm.ReturnScenePath = "";
        });
    }

    private void OnCardLibraryPressed()
    {
        OpenInlineOverlay(CardLibraryScenePath, before: inst =>
        {
            // Cast directly — ReturnScenePath is a public field, not a property,
            // so reflection via GetProperty() returns null and silently does nothing.
            if (inst is CardLibraryUi lib)
                lib.ReturnScenePath = InlineSentinel;
        });
    }

    private void OnReturnToCampusPressed()
    {
        PauseManager.Instance?.ClosePauseMenu();
        GetTree().ChangeSceneToFile(MainMenuScenePath);
    }

    private void OnForfeitCombatPressed()
    {
        if (EncounterRouter.Instance != null)
        {
            PauseManager.Instance?.ClosePauseMenu();
            EncounterRouter.Instance.OnCombatFinished(playerWon: false);
        }
        else
        {
            PauseManager.Instance?.ClosePauseMenu();
            GetTree().ChangeSceneToFile(OverworldScenePath);
        }
    }

    private void OnQuitPressed() => GetTree().Quit();

    private void OnBackdropInput(InputEvent @event)
    {
        if (_currentInlinePanel != null) return;
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            OnResumePressed();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Inline overlay management
    // ════════════════════════════════════════════════════════════════════════

    private void OpenInlineOverlay(string scenePath, System.Action<Node> before = null)
    {
        if (_currentInlinePanel != null) return;

        var packed = GD.Load<PackedScene>(scenePath);
        if (packed == null)
        {
            GD.PrintErr($"[PauseMenu] Could not load: {scenePath}");
            return;
        }

        var inst = packed.Instantiate();
        _currentInlinePanel = inst;

        // Configure BEFORE adding to the tree so _Ready sees the right values.
        before?.Invoke(inst);

        if (inst is Control c)
        {
            c.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            c.ProcessMode = ProcessModeEnum.Always;
        }

        _overlayHost.AddChild(inst);   // _Ready fires here

        _menuContainer.Visible = false;
        CallDeferred(nameof(MoveOverlayHostToFront));

        inst.TreeExited += OnInlinePanelClosed;
    }

    private void OnInlinePanelClosed()
    {
        _currentInlinePanel = null;
        CallDeferred(nameof(RestoreMenuContainer));
    }

    private void MoveOverlayHostToFront()
    {
        if (!IsInstanceValid(_overlayHost)) return;
        MoveChild(_overlayHost, GetChildCount() - 1);
    }

    private void RestoreMenuContainer()
    {
        if (!IsInstanceValid(_menuContainer)) return;
        _menuContainer.Visible = true;
        MoveChild(_menuContainer, GetChildCount() - 1);
    }

    private void RaiseMenuContainer()
    {
        if (_menuContainer != null && IsInstanceValid(_menuContainer))
            MoveChild(_menuContainer, GetChildCount() - 1);
    }
}
