using Godot;

// ============================================================
// HudManager.cs
//
// Purpose:        Autoload singleton that owns the persistent
//                 top-bar HUD — the always-present strip across the
//                 top of gameplay screens carrying key resource
//                 readouts (Gold, Lunation) and global nav buttons
//                 (Return to Campus, Council, Menu). Mirrors PauseManager's
//                 pattern:
//                 a CanvasLayer hosted on the tree root, context-
//                 aware visibility inferred from the current scene.
//
//                 Render order: the bar sits at layer 90, BELOW the
//                 pause overlay (100) and the council overlay (128),
//                 so those cover it (and block its clicks via their
//                 own backdrops) whenever they're open.
//
//                 v1 contents — Gold + Lunation readouts, Return to
//                 Campus (strategic-map only) + Council + Menu buttons —
//                 and deliberately easy to extend (add readouts in
//                 BuildBar's left cluster, buttons in the right).
// Layer:          System (autoload)
// Collaborators:  PauseManager.cs (Menu button -> OpenPauseMenu),
//                 CouncilScreen.cs (Council button -> Toggle),
//                 SaveManager.cs (resource readouts), UITheme.cs
// See:            (none)
// ============================================================

/// <summary>Process-wide autoload owning the persistent top-bar HUD. Builds a
/// CanvasLayer-hosted strip at the top of the screen with resource readouts and
/// global buttons; hides itself on the main menu and in combat.</summary>
public partial class HudManager : Node
{
    public static HudManager Instance { get; private set; }

    /// <summary>Height of the bar in pixels. Public so gameplay scenes can offset
    /// their own top-anchored UI below it — the HUD is a floating overlay and does
    /// not reserve layout space, so scene content must clear it explicitly.</summary>
    public const int BarHeight = 60;
    private const int HudLayer = 90; // below pause (100) and council (128)

    private CanvasLayer _layer;
    private Label _goldLabel;
    private Label _lunationLabel;
    private Button _returnButton; // gated: strategic map only (see RefreshVisibility)

    private int _lastGold = int.MinValue;
    private int _lastLunation = int.MinValue;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        GetTree().NodeAdded += OnNodeAdded;
        CallDeferred(nameof(BuildBar));
    }

    private void OnNodeAdded(Node n)
    {
        // Scene swaps add a node under the root — re-check whether the bar
        // should be visible in the new context.
        if (n.GetParent() == GetTree().Root && n != this)
        {
            CallDeferred(nameof(RefreshVisibility));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Build
    // ════════════════════════════════════════════════════════════════════════

    private void BuildBar()
    {
        _layer = new CanvasLayer { Layer = HudLayer };
        GetTree().Root.AddChild(_layer);

        var bar = new PanelContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            OffsetBottom = BarHeight,
        };
        // Mimic the campus title bar: deep fill, violet bottom border only —
        // the same style the campus screen used, now unified across all content.
        var barStyle = new StyleBoxFlat
        {
            BgColor = UITheme.CampusTitleBarBg,
            BorderColor = UITheme.CampusTitleBarBorder,
            BorderWidthBottom = 2,
        };
        bar.AddThemeStyleboxOverride("panel", barStyle);
        _layer.AddChild(bar);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_bottom", 4);
        bar.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        margin.AddChild(row);

        // ── Left: resource readouts (extend here) ────────────────────────
        _goldLabel = MakeReadout(row, UITheme.Gold);
        _lunationLabel = MakeReadout(row, UITheme.TextSecondary);

        // Spacer pushes the buttons to the right edge.
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        // ── Right: global nav buttons (extend here) ──────────────────────
        // Return to Campus is a scene-warp, so it is gated to the strategic map
        // in RefreshVisibility — it must never offer an exit out of combat,
        // expeditions, or negotiation, nor sit redundantly on the campus itself.
        _returnButton = AddNavButton(row, "Return to Campus",
            () => GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn"));
        AddNavButton(row, "Council", () => CouncilScreen.Toggle(GetTree().Root));
        AddNavButton(row, "Menu", () => PauseManager.Instance?.OpenPauseMenu());

        RefreshReadouts(force: true);
        RefreshVisibility();
    }

    private Label MakeReadout(HBoxContainer row, Color color)
    {
        var l = new Label { VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        l.AddThemeColorOverride("font_color", color);
        row.AddChild(l);
        return l;
    }

    private Button AddNavButton(HBoxContainer row, string text, System.Action onPressed)
    {
        var btn = new Button { Text = text, CustomMinimumSize = new Vector2(96, 0) };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.Pressed += () => onPressed();
        row.AddChild(btn);
        return btn;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Refresh
    // ════════════════════════════════════════════════════════════════════════

    public override void _Process(double delta)
    {
        RefreshReadouts(force: false);
    }

    /// <summary>Update the resource labels when their values change. Cheap
    /// change-guard so it's safe to run every frame; call with force after a
    /// known mutation to update immediately.</summary>
    public void RefreshReadouts(bool force)
    {
        if (_goldLabel == null)
        {
            return;
        }
        var save = SaveManager.ActiveSave;
        int gold = save?.Gold ?? 0;
        int lun = 0;
        var cycle = save?.Cycle;
        if (cycle != null)
        {
            lun = cycle.Calendar.CurrentLunation;
        }

        if (force || gold != _lastGold)
        {
            _goldLabel.Text = $"Gold  {gold}";
            _lastGold = gold;
        }
        if (force || lun != _lastLunation)
        {
            _lunationLabel.Text = $"Lunation  {lun}";
            _lastLunation = lun;
        }
    }

    /// <summary>Show the bar only when a save is live and we're not in combat or
    /// the main menu. Scene predicates mirror PauseManager's inference; adjust
    /// the hidden-scene list if a context should differ.</summary>
    private void RefreshVisibility()
    {
        if (_layer == null)
        {
            return;
        }
        _layer.Visible = SaveManager.ActiveSave != null && !IsHiddenScene();

        // Return to Campus only makes sense on the strategic map. Gating it here
        // (rather than always-on) keeps it from warping the player out of combat,
        // an expedition, or a negotiation, and off the campus where it's moot.
        if (_returnButton != null)
        {
            _returnButton.Visible = IsStrategicScene();
        }
    }

    private bool IsStrategicScene()
    {
        var current = GetTree().CurrentScene;
        string lower = (current?.SceneFilePath ?? "").ToLower();
        return lower.Contains("strategicscene");
    }

    private bool IsHiddenScene()
    {
        var current = GetTree().CurrentScene;
        string lower = (current?.SceneFilePath ?? "").ToLower();
        if (lower.Contains("mainmenu") || lower.Contains("newgame") || lower.Contains("titlescreen"))
        {
            return true; // pre-game menus only — the bar spans all gameplay, combat included
        }
        return false;
    }
}
