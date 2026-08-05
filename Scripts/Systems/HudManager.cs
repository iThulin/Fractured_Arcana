using Godot;

// ============================================================
// HudManager.cs
//
// Purpose:        Autoload singleton that owns the persistent
//                 top-bar HUD — the always-present strip across the
//                 top of gameplay screens carrying key resource
//                 readouts (Lunation, Gold, Splinters, Materials) and global nav buttons
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
//                 v2 contents (2026-08-05) — left cluster is the fixed
//                 resource strip [Lunation, Gold, Splinters, Materials];
//                 the three currencies each carry a red "+N" delta while
//                 an expedition holds unbanked spoils (all three are
//                 forfeited if the run ends without extraction). Right
//                 cluster: Return to Campus (menu screens only) +
//                 Council + Quests + Menu. Extend readouts in BuildBar's
//                 left cluster, buttons in the right.
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
    private Label _lunationLabel;
    private ResourceReadout _gold;
    private ResourceReadout _splinters;
    private ResourceReadout _materials;
    private Button _returnButton; // gated by ShouldShowReturnToCampus (menu screens only)

    private int _lastLunation = int.MinValue;

    /// <summary>One currency readout on the bar: base "Name N" label plus a
    /// red "+N" delta shown while an expedition carries unbanked spoils.
    /// Change-guard values live here so _Process refreshes stay cheap.</summary>
    private sealed class ResourceReadout
    {
        public Label Base;
        public Label Delta;
        public int LastValue = int.MinValue;
        public int LastPending = int.MinValue;
    }

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
        // Fixed order [Lunation, Gold, Splinters, Materials] (2026-08-05 ask).
        _lunationLabel = MakeLabel(row, UITheme.TextSecondary);
        _gold = MakeResourceReadout(row, UITheme.Gold);
        _splinters = MakeResourceReadout(row, UITheme.ArcaneBlue);
        _materials = MakeResourceReadout(row, UITheme.TextPrimary);

        // Spacer pushes the buttons to the right edge.
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        // ── Right: global nav buttons (extend here) ──────────────────────
        // Return to Campus is a scene-warp shown on menu screens (see
        // ShouldShowReturnToCampus): the strategic map and the card/deck utility
        // screens, hidden in combat, expeditions, negotiations, and on campus.
        _returnButton = AddNavButton(row, "Return to Campus",
            () => GetTree().ChangeSceneToFile("res://Scenes/Campus/CampusScene.tscn"));
        AddNavButton(row, "Council", () => CouncilScreen.Toggle(GetTree().Root));
        AddNavButton(row, "Quests", () => QuestLogScreen.Toggle(GetTree().Root));
        AddNavButton(row, "Menu", () => PauseManager.Instance?.OpenPauseMenu());

        RefreshReadouts(force: true);
        RefreshVisibility();
    }

    private Label MakeLabel(HBoxContainer row, Color color)
    {
        var l = new Label { VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        l.AddThemeColorOverride("font_color", color);
        row.AddChild(l);
        return l;
    }

    /// <summary>Base label + tight red delta label. The delta shows "+N" while
    /// an expedition carries unbanked spoils of this currency — the amount
    /// forfeited if the run ends without extraction — and hides at 0 so the
    /// bar reads clean outside expeditions.</summary>
    private ResourceReadout MakeResourceReadout(HBoxContainer row, Color color)
    {
        var cluster = new HBoxContainer();
        cluster.AddThemeConstantOverride("separation", 5);
        row.AddChild(cluster);

        var r = new ResourceReadout
        {
            Base = MakeLabel(cluster, color),
            Delta = MakeLabel(cluster, UITheme.Danger),
        };
        r.Delta.Visible = false;
        // Labels ignore the mouse by default — opt in so the tooltip works.
        r.Delta.MouseFilter = Control.MouseFilterEnum.Stop;
        r.Delta.TooltipText = "At risk: earned this expedition but unbanked —\nlost unless you extract.";
        return r;
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
        if (_gold == null)
        {
            return;
        }
        var save = SaveManager.ActiveSave;
        int lun = 0;
        var cycle = save?.Cycle;
        if (cycle != null)
        {
            lun = cycle.Calendar.CurrentLunation;
        }

        // Expedition-carried spoils (2026-07-29 playtest request, extended
        // 2026-08-05 to all three currencies): a run's earnings are only
        // BANKED on extraction — a failed run forfeits gold, splinters, AND
        // materials. Show what's riding on the current expedition as a red
        // "+N" next to each treasury total so the stake stays visible.
        var (pGold, pSplinters, pMaterials) = GetExpeditionPending();

        if (force || lun != _lastLunation)
        {
            _lunationLabel.Text = $"Lunation  {lun}";
            _lastLunation = lun;
        }
        UpdateReadout(_gold, "Gold", save?.Gold ?? 0, pGold, force);
        UpdateReadout(_splinters, "Splinters", save?.ArcaneSplinters ?? 0, pSplinters, force);
        UpdateReadout(_materials, "Materials", save?.BuildMaterials ?? 0, pMaterials, force);
    }

    private static void UpdateReadout(ResourceReadout r, string name,
                                      int value, int pending, bool force)
    {
        if (!force && value == r.LastValue && pending == r.LastPending)
        {
            return;
        }
        r.Base.Text = $"{name}  {value}";
        r.Delta.Text = pending > 0 ? $"+{pending}" : "";
        r.Delta.Visible = pending > 0;
        r.LastValue = value;
        r.LastPending = pending;
    }

    /// <summary>Spoils earned by the active expedition but NOT yet banked —
    /// all forfeited if the run fails. Read from the live ExpeditionManager
    /// while on the overworld (it is the expedition scene's root script), or
    /// from the EncounterRouter's saved resource state while a combat/
    /// negotiation round-trip is in flight. All-zero when no expedition is
    /// running, so the plain treasury readouts return the moment the run ends.</summary>
    private (int gold, int splinters, int materials) GetExpeditionPending()
    {
        if (!PlayerSession.IsOnExpedition)
            return (0, 0, 0);
        if (GetTree().CurrentScene is ExpeditionManager exp)
            return (exp.GoldEarned, exp.SplinterEarned, exp.MaterialEarned);
        var router = EncounterRouter.Instance;
        return router != null
            ? (router.SavedGoldEarned, router.SavedSplinterEarned, router.SavedMaterialEarned)
            : (0, 0, 0);
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

        // Return to Campus shows on menu/utility screens (deck editor, card upgrade,
        // library, the strategic map, etc.) but is hidden where it would abandon an
        // active activity — combat, an expedition, a negotiation — or is moot (the
        // campus itself). The pre-game menus already hide the whole bar.
        if (_returnButton != null)
        {
            _returnButton.Visible = ShouldShowReturnToCampus();
        }
    }

    /// <summary>True on menu screens where a one-click hop to campus is safe. False in
    /// combat, expeditions, and negotiations (it would abandon them) and on the campus
    /// itself (redundant). Scene matched by file path, like IsHiddenScene.</summary>
    private bool ShouldShowReturnToCampus()
    {
        var current = GetTree().CurrentScene;
        string lower = (current?.SceneFilePath ?? "").ToLower();
        if (lower.Contains("campus") || lower.Contains("expedition") ||
            lower.Contains("battlefield") || lower.Contains("combat") ||
            lower.Contains("negotiation"))
        {
            return false;
        }
        return true;
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
