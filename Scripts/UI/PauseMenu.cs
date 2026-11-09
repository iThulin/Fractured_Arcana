using Godot;

// ============================================================
// PauseMenu.cs
//
// Purpose:        Pause overlay controller. Hosts Resume, Settings,
//                 Card Library, Reaction Chart, Manage Deck and
//                 Upgrade Cards, Return to Campus, Forfeit Combat
//                 (Yield, in a checklist fight), Return to Title and
//                 Quit, under a line naming the guild in play. It
//                 also manages an inline sub-overlay slot (Settings,
//                 the Card Library, the deck editor, the upgrade
//                 screen) so opening them doesn't unload the
//                 underlying scene. The two card screens and the
//                 guild's line came from the retired Guild screen on
//                 2026-11-05 (docs/guild_screen_retirement_v1.md).
// Layer:          UI
// Collaborators:  PauseManager.cs (opens/closes this),
//                 SettingsMenu.cs, CardLibraryUi.cs (inline
//                 overlay children), EncounterRouter.cs
//                 (forfeit combat path)
// See:            README §8. CallDeferred discipline applies
//                 to children added at runtime
// ============================================================

/// <summary>The pause overlay scene. Configures which buttons are visible based on the current <see cref="PauseManager.PauseContext"/> (overworld vs in-combat), and hosts inline sub-overlays for Settings and Card Library so the underlying scene isn't unloaded.</summary>
public partial class PauseMenu : Control
{
    /// <summary>Sentinel value passed to inline sub-overlays' ReturnScenePath so they QueueFree themselves on back instead of triggering a scene change.</summary>
    public const string InlineSentinel = "__INLINE__";

    [Export] public string SettingsScenePath = "res://Scenes/UI/SettingsMenu.tscn";
    [Export] public string CardLibraryScenePath = "res://Scenes/UI/CardLibrary.tscn";
    [Export] public string MainMenuScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    [Export] public string OverworldScenePath = "res://Scenes/Overworld/ExpeditionScene.tscn";

    private Button _resumeButton;
    private Button _settingsButton;
    private Button _cardLibraryButton;
    private Button _returnToCampusButton;
    private Button _forfeitCombatButton;
    private Button _quitButton;
    private Button _returnToTitleButton;
    private const string TitleScenePath = "res://Scenes/UI/TitleScreen.tscn";
    private Button _manageDeckButton;
    private Button _upgradeCardsButton;
    private Label _guildLine;
    private const string DeckEditorScenePath = "res://Scenes/UI/DeckEditor.tscn";
    private const string CardUpgradeScenePath = "res://Scenes/UI/CardUpgradeScreen.tscn";

    private Control _menuContainer;
    private Control _overlayHost;
    private ColorRect _backdrop;

    private Node _currentInlinePanel;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;

        _backdrop = GetNode<ColorRect>("Backdrop");
        _menuContainer = GetNode<Control>("MenuContainer");
        _overlayHost = GetNode<Control>("OverlayHost");

        _resumeButton = GetNode<Button>("MenuContainer/Margin/VBox/ResumeButton");
        _settingsButton = GetNode<Button>("MenuContainer/Margin/VBox/SettingsButton");
        _cardLibraryButton = GetNode<Button>("MenuContainer/Margin/VBox/CardLibraryButton");
        _returnToCampusButton = GetNode<Button>("MenuContainer/Margin/VBox/ReturnToCampusButton");
        _forfeitCombatButton = GetNode<Button>("MenuContainer/Margin/VBox/ForfeitCombatButton");
        _quitButton = GetNode<Button>("MenuContainer/Margin/VBox/QuitButton");

        // Reaction chart (class_identity_elementalist_v1 §2a item 4). Duplicated from
        // the Card Library button BEFORE any signal is wired, so it carries the
        // scene's styling and none of that button's handlers.
        var chartButton = (Button)_cardLibraryButton.Duplicate();
        chartButton.Name = "ReactionChartButton";
        chartButton.Text = "Reaction Chart";
        var menuColumn = _cardLibraryButton.GetParent();
        menuColumn.AddChild(chartButton);
        menuColumn.MoveChild(chartButton, _cardLibraryButton.GetIndex() + 1);
        chartButton.Pressed += () => ReactionChart.ShowDialog(this);

        // Manage Deck and Upgrade Cards (Guild screen part B1, 2026-11-05): the card
        // buttons the Guild screen carried, opened inline like the Card Library.
        // Duplicated the same way, just below the chart.
        _manageDeckButton = (Button)_cardLibraryButton.Duplicate();
        _manageDeckButton.Name = "ManageDeckButton";
        _manageDeckButton.Text = "Manage Deck";
        menuColumn.AddChild(_manageDeckButton);
        menuColumn.MoveChild(_manageDeckButton, chartButton.GetIndex() + 1);
        _manageDeckButton.Pressed += OnManageDeckPressed;
        _upgradeCardsButton = (Button)_cardLibraryButton.Duplicate();
        _upgradeCardsButton.Name = "UpgradeCardsButton";
        _upgradeCardsButton.Text = "Upgrade Cards";
        menuColumn.AddChild(_upgradeCardsButton);
        menuColumn.MoveChild(_upgradeCardsButton, _manageDeckButton.GetIndex() + 1);
        _upgradeCardsButton.Pressed += OnUpgradeCardsPressed;

        // The guild in play, one line under the title (B4): its name, the reading, its seat.
        // Wraps inside the panel's column instead of widening it.
        var menuTitle = GetNode<Control>("MenuContainer/Margin/VBox/Title");
        _guildLine = new Label
        {
            Name = "GuildLine",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(312, 0),
            Visible = false,
        };
        _guildLine.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _guildLine.AddThemeColorOverride("font_color", UITheme.GoldDim);
        menuColumn.AddChild(_guildLine);
        menuColumn.MoveChild(_guildLine, menuTitle.GetIndex() + 1);

        // Return to Title (2026-11-05): Continue and the save slots live on the title
        // screen now, so the Menu leads back to it. Duplicated from Quit before any signal
        // is wired, like the reaction chart, and placed just above it.
        _returnToTitleButton = (Button)_quitButton.Duplicate();
        _returnToTitleButton.Name = "ReturnToTitleButton";
        _returnToTitleButton.Text = "Return to Title";
        var quitColumn = _quitButton.GetParent();
        quitColumn.AddChild(_returnToTitleButton);
        quitColumn.MoveChild(_returnToTitleButton, _quitButton.GetIndex());
        _returnToTitleButton.Pressed += OnReturnToTitlePressed;

        _resumeButton.Pressed += OnResumePressed;
        _settingsButton.Pressed += OnSettingsPressed;
        _cardLibraryButton.Pressed += OnCardLibraryPressed;
        _returnToCampusButton.Pressed += OnReturnToCampusPressed;
        _forfeitCombatButton.Pressed += OnForfeitCombatPressed;
        _quitButton.Pressed += OnQuitPressed;

        _backdrop.GuiInput += OnBackdropInput;

        RaiseMenuContainer();
    }

    /// <summary>Sets which buttons are visible based on the pause context (overworld shows Return to Campus, combat shows Forfeit Combat). Always called by <see cref="PauseManager"/> before showing the menu.</summary>
    public void Configure(PauseManager.PauseContext ctx)
    {
        bool inOverworld = ctx == PauseManager.PauseContext.Overworld;
        bool inCombat = ctx == PauseManager.PauseContext.InCombat;

        _cardLibraryButton.Visible = true;
        _returnToCampusButton.Visible = inOverworld;
        _forfeitCombatButton.Visible = inCombat;
        // A checklist fight (the trial, a practical) has no round cap, so its way out is a
        // yield, which the director reads as the bell, not the floor (ruled 2026-10-27).
        _forfeitCombatButton.Text = inCombat && InChecklistFight() ? "Yield" : "Forfeit Combat";
        _quitButton.Visible = true;
        // Return to Title: on the strategic map and the screens around it. Never in a fight
        // or mid-expedition, which have their own ways out, nor on the title itself.
        var scene = GetTree()?.CurrentScene;
        bool aroundMap = !inCombat && scene is not TitleScreen
            && (scene is StrategicView || ctx == PauseManager.PauseContext.MainMenu);
        _returnToTitleButton.Visible = aroundMap;
        // Manage Deck and Upgrade Cards (B1, GB2): where Return to Title shows, with a guild
        // loaded, but not while founding one, in the prologue, or over the same screen.
        bool cardScreens = aroundMap && SaveManager.ActiveSave != null && scene is not NewGameScreen
            && string.IsNullOrEmpty(RegisterManager.PrologueMode);
        _manageDeckButton.Visible = cardScreens && scene is not DeckEditorUi;
        _upgradeCardsButton.Visible = cardScreens && scene is not CardUpgradeScreen;
        // The guild's line (B4), anywhere a guild is in play.
        string guild = scene is TitleScreen || scene is NewGameScreen ? "" : GuildLine(SaveManager.ActiveSave);
        _guildLine.Text = guild;
        _guildLine.Visible = guild.Length > 0;
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
            // Cast directly. ReturnScenePath is a public field, not a property,
            // so reflection via GetProperty() returns null and silently does nothing.
            if (inst is CardLibraryUi lib)
                lib.ReturnScenePath = InlineSentinel;
        });
    }

    /// <summary>The deck editor over the Menu (B1), as the Card Library opens: its Back closes
    /// it and the Menu comes back.</summary>
    private void OnManageDeckPressed()
    {
        OpenInlineOverlay(DeckEditorScenePath, before: inst =>
        {
            if (inst is DeckEditorUi deck)
                deck.ReturnScenePath = InlineSentinel;
        });
    }

    /// <summary>The upgrade screen over the Menu (B1). It keeps its own Scriptorum gate.</summary>
    private void OnUpgradeCardsPressed()
    {
        OpenInlineOverlay(CardUpgradeScenePath, before: inst =>
        {
            if (inst is CardUpgradeScreen upgrade)
                upgrade.ReturnScenePath = InlineSentinel;
        });
    }

    /// <summary>The guild in play in one line (B4): its name, the reading, and its seat (the
    /// home city and its realm). The Grand Hall's identity block it replaces also had a stats
    /// row; gold is on the HUD, and gold and runs are on the Load Game card.</summary>
    private static string GuildLine(GuildSaveData save)
    {
        if (save == null)
            return "";
        string line = save.GuildName;
        int reading = save.Cycle?.CycleNumber ?? 0;
        if (reading > 0)
            line += $"  \u00b7  Reading {reading}";
        string seat = SeatName(save);
        if (seat.Length > 0)
            line += $"  \u00b7  {seat}";
        return line;
    }

    /// <summary>The home city and its realm, as the Grand Hall's seat line read them; "" before
    /// the reading's world exists.</summary>
    private static string SeatName(GuildSaveData save)
    {
        var world = save?.Cycle?.World;
        if (world == null || world.Tiles.Length == 0 || !world.InBounds(world.HomeX, world.HomeY))
            return "";
        var home = world.SettlementAt(world.HomeX, world.HomeY);
        string city = home != null && !string.IsNullOrEmpty(home.Name) ? home.Name : "";
        string region = "";
        string kid = world.GetTile(world.HomeX, world.HomeY).KingdomId;
        if (!string.IsNullOrEmpty(kid) && save.Cycle.Kingdoms != null &&
            save.Cycle.Kingdoms.TryGetValue(kid, out var ks))
            region = !string.IsNullOrEmpty(ks.DisplayName) ? ks.DisplayName : ks.TemplateRegionId;
        if (city.Length > 0 && !string.IsNullOrEmpty(region))
            return $"{city}, {region}";
        return city.Length > 0 ? city : region ?? "";
    }

    private void OnReturnToCampusPressed()
    {
        PauseManager.Instance?.ClosePauseMenu();
        // The hub is the strategic city (2026-08-19): abandoning an expedition for
        // home lands in city view, not on the retired standalone campus screen.
        PlayerSession.StartInCityOnOpen = true;
        GetTree().ChangeSceneToFile(MainMenuScenePath);
    }

    private void OnForfeitCombatPressed()
    {
        PauseManager.Instance?.ClosePauseMenu();

        // Debug-launched fights have no overworld run to return to, so go to
        // campus (resetting the debug flags) instead of the blank-screen
        // overworld exit.
        if (PlayerSession.DebugCombat)
        {
            CombatDebugLauncher.ReturnToCampus(this);
            return;
        }

        if (EncounterRouter.Instance != null)
        {
            if (InChecklistFight())
            {
                EncounterRouter.Instance.ScriptedFightYielded = true;   // the bell, not the floor
                GD.Print("[PauseMenu] The candidate yields the fight.");
            }
            EncounterRouter.Instance.OnCombatFinished(playerWon: false);
        }
        else
        {
            GetTree().ChangeSceneToFile(
                EncounterRouter.Instance?.OverworldScenePath
                ?? OverworldScenePath);
        }
    }

    /// <summary>A scripted fight with a checklist (CombatManager.Tutorial.cs): the trial or a
    /// practical. It has no round cap, so its forfeit is a yield.</summary>
    private static bool InChecklistFight()
    {
        var router = EncounterRouter.Instance;
        return router != null && router.SuppressRewards && !string.IsNullOrEmpty(router.ScriptedFightId)
            && PrologueDirector.StepsFor(router.ScriptedFightId) != null;
    }

    private void OnQuitPressed() => GetTree().Quit();

    /// <summary>Save, close the Menu and go to the title screen, where Continue and the save
    /// slots are. The campus overlay's flags go too, or the next visit to the map would think
    /// the overlay still open and keep the HUD's bar hidden.</summary>
    private void OnReturnToTitlePressed()
    {
        SaveManager.SaveIfDirty();
        CampusScreen.OverlayLeaveHandler = null;
        PlayerSession.CampusOverlayOpen = false;
        PauseManager.Instance?.ClosePauseMenu();
        GetTree().ChangeSceneToFile(TitleScenePath);
    }

    private void OnBackdropInput(InputEvent @event)
    {
        if (_currentInlinePanel != null)
            return;
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            OnResumePressed();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Inline overlay management
    // ════════════════════════════════════════════════════════════════════════

    private void OpenInlineOverlay(string scenePath, System.Action<Node> before = null)
    {
        if (_currentInlinePanel != null)
            return;

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
        if (!IsInstanceValid(_overlayHost))
            return;
        MoveChild(_overlayHost, GetChildCount() - 1);
    }

    private void RestoreMenuContainer()
    {
        if (!IsInstanceValid(_menuContainer))
            return;
        _menuContainer.Visible = true;
        MoveChild(_menuContainer, GetChildCount() - 1);
    }

    private void RaiseMenuContainer()
    {
        if (_menuContainer != null && IsInstanceValid(_menuContainer))
            MoveChild(_menuContainer, GetChildCount() - 1);
    }
}
