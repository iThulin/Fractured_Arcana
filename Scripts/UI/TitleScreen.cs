using Godot;

// ============================================================
// TitleScreen.cs
//
// Purpose:        The launch screen (2026-08-21). It is the ONE screen
//                 shown before the game routes anywhere. Cold boot used to
//                 land directly in the city hub (or the campus slot
//                 picker), which read as the app dumping you mid-game.
//                 This is deliberately minimal: name, and the verbs a
//                 boot actually has. project.godot's main_scene points
//                 here, and since 2026-11-05 the Menu's Return to Title
//                 leads back. Load Game opens the save slots over it
//                 (they lived in the Guild tab, whose "Guild Hall" door
//                 here had no way back out).
// Layer:          UI
// Collaborators:  SaveManager (AnySaveExists / AutoLoadLast),
//                 PlayerSession (StartInCityOnOpen), SettingsMenu
//                 (instanced as an overlay), SaveSlotsPanel (Load
//                 Game), UITheme.
// ============================================================

public partial class TitleScreen : Control
{
    private const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    // The city places its camera and lays its ground over its first frames. The curtain
    // stays down long enough that neither is seen.
    private const float StrategicSettleSeconds = 0.9f;
    private const string SettingsScenePath = "res://Scenes/UI/SettingsMenu.tscn";

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        var bg = new ColorRect { Color = UITheme.WorldDeep };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var vbox = new VBoxContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        vbox.AddThemeConstantOverride("separation", 14);
        AddChild(vbox);

        var title = new Label
        {
            Text = "FRACTURED ARCANA",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 64);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(title);

        var sub = new Label
        {
            Text = "The sky has finished reading itself.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sub.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        sub.AddThemeColorOverride("font_color", UITheme.TextDim);
        vbox.AddChild(sub);

        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 28) });

        bool hasSave = SaveManager.AnySaveExists();

        Button MakeBtn(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                CustomMinimumSize = new Vector2(280, 52),
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            };
            b.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
            UITheme.ApplyButtonStyle(b, isPrimary: primary);
            vbox.AddChild(b);
            return b;
        }

        if (hasSave)
        {
            var cont = MakeBtn("Continue", primary: true);
            cont.Pressed += OnContinue;
            cont.GrabFocus();   // Enter on boot resumes the guild

            var loadBtn = MakeBtn("Load Game", primary: false);
            loadBtn.Pressed += () => OpenSlots("");
        }

        var newBtn = MakeBtn(hasSave ? "New Game" : "Found a Guild", primary: !hasSave);
        newBtn.Pressed += OnNewGame;
        if (!hasSave) newBtn.GrabFocus();

        var settingsBtn = MakeBtn("Settings", primary: false);
        settingsBtn.Pressed += OpenSettingsOverlay;

        var quitBtn = MakeBtn("Quit", primary: false);
        quitBtn.Pressed += () => GetTree().Quit();

        // Footer: unobtrusive corner line, not part of the centered stack.
        var footer = new Label
        {
            Text = "an eternal guild across unmade timelines",
            AnchorLeft = 0.5f, AnchorTop = 1f, AnchorRight = 0.5f, AnchorBottom = 1f,
            GrowHorizontal = GrowDirection.Both,
            OffsetTop = -44,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        footer.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        footer.AddThemeColorOverride("font_color", UITheme.TextDim);
        AddChild(footer);
    }

    /// <summary>Resume the most recent guild and land in the city hub. This is the same
    /// route the old cold boot took, now behind an explicit choice.</summary>
    private void OnContinue()
    {
        if (!SaveManager.AutoLoadLast())
        {
            // Files existed at build time but failed to load: the slot list says so and
            // offers the others (the founding room's job before 2026-11-05).
            OpenSlots("The last guild could not be loaded. Pick another, or found a new one.");
            return;
        }
        PlayerSession.StartInCityOnOpen = true;
        SceneTransition.Quiet(GetTree(), StrategicScenePath, StrategicSettleSeconds);
    }

    /// <summary>A new guild in the first empty slot, founded on the New Game screen. With
    /// every slot taken, the slot list opens so one can be cleared first.</summary>
    private void OnNewGame()
    {
        int empty = SaveSlotsPanel.FirstEmptySlot();
        if (empty < 0)
        {
            OpenSlots("Every slot holds a guild. Delete one to found another.");
            return;
        }
        SaveSlotsPanel.StartNewGame(GetTree(), empty);
    }

    /// <summary>The save slots over the title, with a line saying why when there is one.</summary>
    private void OpenSlots(string note)
    {
        if (FindChild("SaveSlots", false, false) != null)
            return;
        AddChild(new SaveSlotsPanel { Name = "SaveSlots", Note = note });
    }

    /// <summary>Settings as an OVERLAY child (empty ReturnScenePath → its Back
    /// button QueueFrees it), so the title stays underneath.</summary>
    private void OpenSettingsOverlay()
    {
        var scene = GD.Load<PackedScene>(SettingsScenePath);
        if (scene == null) return;
        var menu = scene.Instantiate();
        if (menu is SettingsMenu sm) sm.ReturnScenePath = "";
        AddChild(menu);
    }
}
