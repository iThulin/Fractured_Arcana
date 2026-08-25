using Godot;

// ============================================================
// TitleScreen.cs
//
// Purpose:        The launch screen (2026-08-21). It is the ONE screen
//                 shown before the game routes anywhere. Cold boot used to
//                 land directly in the city hub (or the campus slot
//                 picker), which read as the app dumping you mid-game.
//                 This is deliberately minimal: name, and the three
//                 verbs a boot actually has. It appears exactly once:
//                 project.godot's main_scene points here, and no in-game
//                 flow routes back (the hub remains the game's home).
// Layer:          UI
// Collaborators:  SaveManager (AnySaveExists / AutoLoadLast),
//                 PlayerSession (StartInCityOnOpen), SettingsMenu
//                 (instanced as an overlay), UITheme.
// ============================================================

public partial class TitleScreen : Control
{
    private const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    private const string CampusScenePath = "res://Scenes/Campus/CampusScene.tscn";
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
        }

        var newBtn = MakeBtn(hasSave ? "Guild Hall" : "Found a Guild", primary: !hasSave);
        newBtn.Pressed += () => GetTree().ChangeSceneToFile(CampusScenePath);
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
            // Files existed at build time but failed to load. The founding
            // room owns slot problems.
            GetTree().ChangeSceneToFile(CampusScenePath);
            return;
        }
        PlayerSession.StartInCityOnOpen = true;
        GetTree().ChangeSceneToFile(StrategicScenePath);
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
