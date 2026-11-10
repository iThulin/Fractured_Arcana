using Godot;

// ============================================================
// HudManager.cs
//
// Purpose:        Autoload singleton that owns the persistent
//                 top-bar HUD: the always-present strip across the
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
//                 v2 contents (2026-08-05): left cluster is the fixed
//                 resource strip [Lunation, Gold, Splinters, Materials];
//                 the three currencies each carry a red "+N" delta while
//                 an expedition holds unbanked spoils (all three are
//                 forfeited if the run ends without extraction). Right
//                 cluster: Return to Campus (menu screens only) +
//                 Council + Quests + Menu. Extend readouts in BuildBar's
//                 left cluster, buttons in the right. Between them (S5):
//                 the first lunation's Next line, the step not yet done,
//                 which goes there when clicked (FirstLunation). While
//                 the interval walks, Standing Orders' line leads, the
//                 first lunation's yields, and the date reads as held
//                 (O1b, StandingOrders and Interval).
//                 Provinces 7f: a slot at the bar's left end where the
//                 Register places its book (TryGetRegisterSlot); buttons a
//                 screen puts before Menu (SetScreenButton, released with
//                 the screen); and on the strategic map Return to Campus
//                 is that screen's view toggle (Home city, World map), read
//                 from StrategicView.HomeToggleState.
// Layer:          System (autoload)
// Collaborators:  PauseManager.cs (Menu button -> OpenPauseMenu),
//                 CouncilScreen.cs (Council button -> Toggle),
//                 SaveManager.cs (resource readouts), UITheme.cs,
//                 FirstLunation.cs and StandingOrders.cs (the Next line)
// See:            (none)
// ============================================================

/// <summary>Process-wide autoload owning the persistent top-bar HUD. Builds a
/// CanvasLayer-hosted strip at the top of the screen with resource readouts and
/// global buttons; hides itself on the main menu and in combat.</summary>
public partial class HudManager : Node
{
    public static HudManager Instance { get; private set; }

    /// <summary>Height of the bar in pixels. Public so gameplay scenes can offset
    /// their own top-anchored UI below it. The HUD is a floating overlay and does
    /// not reserve layout space, so scene content must clear it explicitly.</summary>
    public const int BarHeight = 60;
    private const int HudLayer = 90; // below pause (100) and council (128)

    private CanvasLayer _layer;
    private Label _lunationLabel;
    private ResourceReadout _gold;
    private ResourceReadout _splinters;
    private ResourceReadout _materials;
    private ResourceReadout _supplies;
    private ResourceReadout _anchors;
    private Button _returnButton; // gated by ShouldShowReturnToCampus (menu screens only)
    private Button _nextButton;   // S5: the first lunation's Next line (RefreshNextLine)
    private double _nextRefreshIn;
    private const double NextRefreshSeconds = 0.5;

    // Provinces 7e (PR34, PR35): a screen that lays its own controls over the bar's centre (the
    // strategic map's clock) claims it. The bar's date hides and the Next line drops to a strip
    // hung under the bar's centre, in the bar's fill. Given back when the screen leaves.
    private Node _centreOwner;
    private HBoxContainer _barRow;
    private int _nextSlot = -1;
    private PanelContainer _nextStrip;

    /// <summary>Height of the Next strip under the bar, for screens that keep their content
    /// clear of it.</summary>
    public const int NextStripHeight = 28;

    // Provinces 7f (PR42): the Register's book stands in this slot at the bar's left end. The
    // glyph stays the Register's own node on its own layer; the bar only keeps the room.
    private Control _registerSlot;

    /// <summary>The Register's slot's width: its book glyph's.</summary>
    public const int RegisterSlotWidth = 44;

    // Provinces 7f (PR41): buttons a screen puts in the bar's right cluster, before Menu, by
    // key. Each goes when its screen releases it or leaves the tree.
    private sealed class ScreenButton
    {
        public Node Owner;
        public string Text;
        public string Tooltip;
        public System.Action OnPressed;
        public Button Shown;
    }

    private readonly System.Collections.Generic.Dictionary<string, ScreenButton> _screenButtons = new();
    private HBoxContainer _screenSlot;

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
        // Scene swaps add a node under the root, so re-check whether the bar
        // should be visible in the new context.
        if (n == null || !IsInstanceValid(n))
            return;
        var tree = GetTree();
        if (tree == null)
            return;
        if (n.GetParent() == tree.Root && n != this)
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
        // Mimic the campus title bar: deep fill, violet bottom border only.
        // It is the same style the campus screen used, now unified across all content.
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
        // Provinces 7f (PR42): first, the Register's slot (RegisterManager places its book here).
        _registerSlot = new Control
        {
            CustomMinimumSize = new Vector2(RegisterSlotWidth, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        row.AddChild(_registerSlot);
        _lunationLabel = MakeLabel(row, UITheme.TextSecondary);
        _gold = MakeResourceReadout(row, UITheme.Gold);
        _splinters = MakeResourceReadout(row, UITheme.ArcaneBlue);
        _materials = MakeResourceReadout(row, UITheme.TextPrimary);
        _supplies = MakeResourceReadout(row, UITheme.Success);
        _anchors = MakeResourceReadout(row, UITheme.Gold);

        // S5: the first lunation's Next line, centred between the two clusters by a spacer on
        // each side. Flat, in gold: a line to read and click, not another nav button.
        _centreLeft = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(_centreLeft);
        _nextButton = new Button { Flat = true, Visible = false, FocusMode = Control.FocusModeEnum.None };
        _nextButton.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _nextButton.AddThemeColorOverride("font_color", UITheme.Gold);
        _nextButton.AddThemeColorOverride("font_hover_color", UITheme.TextPrimary);
        _nextButton.AddThemeColorOverride("font_pressed_color", UITheme.TextPrimary);
        _nextButton.AddThemeColorOverride("font_disabled_color", UITheme.GoldDim);
        _nextButton.Pressed += GoNext;
        row.AddChild(_nextButton);
        _barRow = row;                        // provinces 7e: where the Next line returns to
        _nextSlot = _nextButton.GetIndex();

        // Spacer pushes the buttons to the right edge.
        _centreRight = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(_centreRight);

        // ── Right: global nav buttons (extend here) ──────────────────────
        // Return to Campus is a scene-warp shown on menu screens (see
        // ShouldShowReturnToCampus): the strategic map and the card/deck utility
        // screens, hidden in combat, expeditions, negotiations, and on campus.
        _returnButton = AddNavButton(row, "Return to Campus", ReturnToCampus);
        AddNavButton(row, "Council", () => CouncilScreen.Toggle(GetTree().Root));
        AddNavButton(row, "Quests", () => QuestLogScreen.Toggle(GetTree().Root));
        // Provinces 7f (PR41): a screen's own buttons sit here, before Menu (SetScreenButton).
        _screenSlot = new HBoxContainer { Visible = false };
        _screenSlot.AddThemeConstantOverride("separation", 18);
        row.AddChild(_screenSlot);
        AddNavButton(row, "Menu", () => PauseManager.Instance?.OpenPauseMenu());

        BuildNextStrip();   // provinces 7e (PR35)
        ApplyCentre();      // a screen may have claimed the centre before the bar was built
        ApplyScreenButtons();   // provinces 7f (PR41): and put its buttons in
        RefreshReadouts(force: true);
        RefreshVisibility();
    }

    /// <summary>Return to the campus. From the strategic map this DESCENDS, swooping
    /// the atlas camera into the home city before the scene change (Phase 2, Stage 2);
    /// from every other menu/utility screen it's the plain scene warp.</summary>
    private void ReturnToCampus()
    {
        // Provinces 7f (PR41): on the strategic map the button is that screen's view toggle.
        if (GetTree().CurrentScene is StrategicView sv)
        {
            sv.ToggleHomeView();
            return;
        }
        // The hub is the strategic city (2026-08-19): from utility screens this warp
        // lands in city view, not on the retired standalone campus screen.
        PlayerSession.StartInCityOnOpen = true;
        GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
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
    /// an expedition carries unbanked spoils of this currency (the amount
    /// forfeited if the run ends without extraction) and hides at 0 so the
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
        // Labels ignore the mouse by default, so opt in for the tooltip to work.
        r.Delta.MouseFilter = Control.MouseFilterEnum.Stop;
        r.Delta.TooltipText = "At risk: earned this expedition but unbanked.\nLost unless you extract.";
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
        _nextRefreshIn -= delta;
        if (_nextRefreshIn <= 0)
        {
            _nextRefreshIn = NextRefreshSeconds;
            RefreshNextLine();
            RefreshViewToggle();   // provinces 7f (PR41): the door can shut while the map is up
        }
    }

    /// <summary>The Next line was clicked: Standing Orders' step while the interval walks, else the
    /// first lunation's (O1b).</summary>
    private void GoNext()
    {
        if (StandingOrders.Current(SaveManager.ActiveSave) != null)
        {
            StandingOrders.Go(GetTree());
            return;
        }
        FirstLunation.Go(GetTree());
    }

    /// <summary>The first lunation's Next line (S5): the step not yet done, with the save's own
    /// state settled first (FirstLunation.Settle). Hidden while there is none or the bar is down,
    /// and greyed where clicking cannot go there (a fight, an expedition, a negotiation).</summary>
    private void RefreshNextLine()
    {
        if (_nextButton == null)
        {
            return;
        }
        var save = SaveManager.ActiveSave;
        if (save == null || _layer == null || !_layer.Visible)
        {
            _nextButton.Visible = false;
            SyncNextStrip();   // provinces 7e
            return;
        }
        var scene = GetTree().CurrentScene;
        FirstLunation.Settle(save, scene);
        StandingOrders.Settle(save);
        // Standing Orders O1b (D159): the interval's step leads while it walks; the first lunation's yields.
        string text = StandingOrders.NextLine(save, scene, out string tip, out bool canGo);
        if (text == null)
        {
            text = FirstLunation.NextLine(save, scene, out tip, out canGo);
        }
        _nextButton.Visible = text != null;
        SyncNextStrip();   // provinces 7e: the strip shows only with a step
        if (text == null)
        {
            return;
        }
        if (_nextButton.Text != text)
        {
            _nextButton.Text = text;
            PulseNext();   // provinces 7e (PR35): a new step pulses once
        }
        _nextButton.TooltipText = tip;
        _nextButton.Disabled = !canGo;
    }

    // ── The bar's centre (provinces 7e, PR34 and PR35) ───────────────────

    /// <summary>True while the bar is drawn. A screen that lays its own controls over the
    /// bar's centre follows it, so they hide with the bar (the campus overlay hides it).</summary>
    public bool BarVisible => _layer != null && _layer.Visible;

    /// <summary>A screen lays its own controls over the bar's centre (the strategic map's
    /// clock, on its own canvas layer just above the bar's). The bar's date hides, since the
    /// screen shows its own, and the Next line drops to a strip under the bar's centre. The
    /// screen gives the centre back with ReleaseCentre when it leaves.</summary>
    public void ClaimCentre(Node owner)
    {
        if (owner == null)
        {
            return;
        }
        _centreOwner = owner;
        ApplyCentre();
    }

    /// <summary>Give the bar's centre back: its date shows again and the Next line returns to
    /// its place in the bar. Ignored unless <paramref name="owner"/> holds the claim.</summary>
    public void ReleaseCentre(Node owner)
    {
        if (owner == null || owner != _centreOwner)
        {
            return;
        }
        _centreOwner = null;
        ApplyCentre();
    }

    private bool CentreClaimed => _centreOwner != null;

    // P7a3: the two spacers either side of the bar's centre. A screen that lays its own
    // controls over the centre reads the room between them, so it never covers a readout.
    private Control _centreLeft;
    private Control _centreRight;

    /// <summary>The room in the bar's centre, in canvas pixels: from the end of the resource
    /// readouts to the start of the buttons. False until the bar is built and laid out.</summary>
    public bool TryCentreGap(out float left, out float right)
    {
        left = 0f;
        right = 0f;
        if (!IsInstanceValid(_centreLeft) || !IsInstanceValid(_centreRight))
        {
            return false;
        }
        left = _centreLeft.GlobalPosition.X;
        right = _centreRight.GlobalPosition.X + _centreRight.Size.X;
        return right > left;
    }

    /// <summary>Put the date and the Next line where the claim says. Waits for BuildBar,
    /// which calls it again once the bar exists.</summary>
    private void ApplyCentre()
    {
        if (!IsInstanceValid(_nextButton) || !IsInstanceValid(_barRow) || !IsInstanceValid(_nextStrip))
        {
            return;
        }
        bool claimed = CentreClaimed;
        if (IsInstanceValid(_lunationLabel))
        {
            _lunationLabel.Visible = !claimed;
        }
        Node home = claimed ? (Node)_nextStrip : _barRow;
        if (_nextButton.GetParent() != home)
        {
            _nextButton.GetParent()?.RemoveChild(_nextButton);
            home.AddChild(_nextButton);
            if (!claimed && _nextSlot >= 0)
            {
                _barRow.MoveChild(_nextButton, Mathf.Min(_nextSlot, _barRow.GetChildCount() - 1));
            }
        }
        SyncNextStrip();
    }

    /// <summary>The strip shows only while a screen holds the centre and there is a step.</summary>
    private void SyncNextStrip()
    {
        if (IsInstanceValid(_nextStrip))
        {
            _nextStrip.Visible = CentreClaimed && IsInstanceValid(_nextButton) && _nextButton.Visible;
        }
    }

    /// <summary>The strip that holds the Next line while a screen has the bar's centre: hung
    /// under the bar's middle, in the bar's own fill and violet rule.</summary>
    private void BuildNextStrip()
    {
        _nextStrip = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0f,
            AnchorRight = 0.5f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetTop = BarHeight,
            Visible = false,
        };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.CampusTitleBarBg,
            BorderColor = UITheme.CampusTitleBarBorder,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 0,
            ContentMarginBottom = 2,
        };
        _nextStrip.AddThemeStyleboxOverride("panel", style);
        _layer.AddChild(_nextStrip);
    }

    /// <summary>One pulse when the Next line changes step, so a new step is seen wherever the
    /// line sits.</summary>
    private void PulseNext()
    {
        Control target = CentreClaimed && IsInstanceValid(_nextStrip) ? (Control)_nextStrip : _nextButton;
        if (!IsInstanceValid(target) || !target.IsInsideTree())
        {
            return;
        }
        target.CreateTween()
              .TweenProperty(target, "modulate", Colors.White, 0.9)
              .From(new Color(1.8f, 1.6f, 1.0f))
              .SetTrans(Tween.TransitionType.Sine)
              .SetEase(Tween.EaseType.Out);
    }

    // ── The Register's slot (provinces 7f, PR42) ─────────────────────────

    /// <summary>Where the Register's book stands while the bar shows: the slot's rectangle on
    /// screen. False while the bar is hidden (the prologue, the campus overlay) or not yet laid
    /// out, and the Register keeps its book on the left edge then.</summary>
    public bool TryGetRegisterSlot(out Rect2 rect)
    {
        rect = new Rect2();
        if (!BarVisible || !IsInstanceValid(_registerSlot) || !_registerSlot.IsVisibleInTree())
        {
            return false;
        }
        rect = _registerSlot.GetGlobalRect();
        return rect.Size.X >= 1f && rect.Size.Y >= 1f;
    }

    // ── Screen buttons and the view toggle (provinces 7f, PR41) ──────────

    /// <summary>A screen puts a button in the bar's right cluster, before Menu, under a key; the
    /// same key again replaces it. It goes when the screen calls ReleaseScreenButtons or leaves
    /// the tree. A screen built before the bar has its buttons placed once the bar exists.</summary>
    public void SetScreenButton(Node owner, string key, string text, string tooltip, System.Action onPressed)
    {
        if (owner == null || string.IsNullOrEmpty(key) || onPressed == null)
        {
            return;
        }
        if (_screenButtons.TryGetValue(key, out var old) && IsInstanceValid(old.Shown))
        {
            old.Shown.QueueFree();
        }
        _screenButtons[key] = new ScreenButton { Owner = owner, Text = text, Tooltip = tooltip, OnPressed = onPressed };
        ApplyScreenButtons();
    }

    /// <summary>Take away every button <paramref name="owner"/> put in the bar.</summary>
    public void ReleaseScreenButtons(Node owner)
    {
        if (owner == null)
        {
            return;
        }
        var gone = new System.Collections.Generic.List<string>();
        foreach (var kv in _screenButtons)
        {
            if (kv.Value.Owner == owner)
            {
                gone.Add(kv.Key);
            }
        }
        foreach (string key in gone)
        {
            var b = _screenButtons[key];
            if (IsInstanceValid(b.Shown))
            {
                b.Shown.QueueFree();
            }
            _screenButtons.Remove(key);
        }
        SyncScreenSlot();
    }

    /// <summary>Buttons whose screen left the tree without releasing them go now.</summary>
    private void ReleaseLostScreenButtons()
    {
        var lost = new System.Collections.Generic.List<Node>();
        foreach (var b in _screenButtons.Values)
        {
            if (!IsInstanceValid(b.Owner) || !b.Owner.IsInsideTree())
            {
                lost.Add(b.Owner);
            }
        }
        foreach (var owner in lost)
        {
            ReleaseScreenButtons(owner);
        }
    }

    /// <summary>Put every screen button that is not on the bar yet on it. Waits for BuildBar,
    /// which calls it again once the bar exists.</summary>
    private void ApplyScreenButtons()
    {
        if (!IsInstanceValid(_screenSlot))
        {
            return;
        }
        foreach (var b in _screenButtons.Values)
        {
            if (IsInstanceValid(b.Shown))
            {
                continue;
            }
            bool glyph = (b.Text ?? "").Length <= 2;
            var btn = new Button
            {
                Text = b.Text ?? "",
                TooltipText = b.Tooltip ?? "",
                CustomMinimumSize = new Vector2(glyph ? 44 : 96, 0),
            };
            btn.AddThemeFontSizeOverride("font_size", glyph ? UITheme.CampusBodyFontSize : UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: false);
            var press = b.OnPressed;
            btn.Pressed += () => press();
            _screenSlot.AddChild(btn);
            b.Shown = btn;
        }
        SyncScreenSlot();
    }

    private void SyncScreenSlot()
    {
        if (IsInstanceValid(_screenSlot))
        {
            _screenSlot.Visible = _screenButtons.Count > 0;
        }
    }

    /// <summary>Return to Campus, read for the screen: on the strategic map it is that screen's
    /// view toggle (Home city on the world, World map in a city), elsewhere the warp home. From
    /// RefreshVisibility, the bar's half-second tick, and the strategic map's chrome refresh.</summary>
    public void RefreshViewToggle()
    {
        if (!IsInstanceValid(_returnButton))
        {
            return;
        }
        string text = "Return to Campus", tip = "";
        bool disabled = false;
        if (GetTree()?.CurrentScene is StrategicView sv && IsInstanceValid(sv))
        {
            sv.HomeToggleState(out text, out tip, out disabled);
        }
        if (_returnButton.Text != text)
        {
            _returnButton.Text = text;
        }
        _returnButton.TooltipText = tip;
        _returnButton.Disabled = disabled;
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
        int day = 0;
        var cycle = save?.Cycle;
        if (cycle != null)
        {
            lun = cycle.Calendar.CurrentLunation;
            day = cycle.Calendar.DayOfLunation + 1;
        }
        // The day clock (2026-09-23): lunation and day share one change key. Standing Orders O1b
        // (D164): while the interval walks the moon is held, and the date reads as the map's does.
        bool held = Interval.Walking(cycle);
        int lunKey = held ? -1 : lun * 100 + day;

        // Expedition-carried spoils (2026-07-29 playtest request, extended
        // 2026-08-05 to all three currencies): a run's earnings are only
        // BANKED on extraction, so a failed run forfeits gold, splinters, AND
        // materials. Show what's riding on the current expedition as a red
        // "+N" next to each treasury total so the stake stays visible.
        var (pGold, pSplinters, pMaterials, pSupplies) = GetExpeditionPending();

        if (force || lunKey != _lastLunation)
        {
            _lunationLabel.Text = held ? Interval.DateLine : $"Moon  {lun}  ·  day {day}";
            _lastLunation = lunKey;
        }
        UpdateReadout(_gold, "Gold", save?.Gold ?? 0, pGold, force);
        UpdateReadout(_splinters, "Splinters", save?.ArcaneSplinters ?? 0, pSplinters, force);
        UpdateReadout(_materials, "Materials", save?.BuildMaterials ?? 0, pMaterials, force);
        UpdateReadout(_supplies, "Supplies", save?.Supplies ?? 0, pSupplies, force);
        UpdateReadout(_anchors, "Anchors", TimelineAnchors.Left(save), 0, force);
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

    /// <summary>Spoils earned by the active expedition but NOT yet banked, and
    /// all forfeited if the run fails. Read from the live ExpeditionManager
    /// while on the overworld (it is the expedition scene's root script), or
    /// from the EncounterRouter's saved resource state while a combat/
    /// negotiation round-trip is in flight. All-zero when no expedition is
    /// running, so the plain treasury readouts return the moment the run ends.</summary>
    private (int gold, int splinters, int materials, int supplies) GetExpeditionPending()
    {
        if (!PlayerSession.IsOnExpedition)
            return (0, 0, 0, 0);
        if (GetTree().CurrentScene is ExpeditionManager exp)
            return (exp.GoldEarned, exp.SplinterEarned, exp.MaterialEarned, exp.SuppliesEarned);
        var router = EncounterRouter.Instance;
        return router != null
            ? (router.SavedGoldEarned, router.SavedSplinterEarned,
               router.SavedMaterialEarned, router.SavedSuppliesEarned)
            : (0, 0, 0, 0);
    }

    /// <summary>Show the bar only when a save is live and we're not in combat or
    /// the main menu. Scene predicates mirror PauseManager's inference; adjust
    /// the hidden-scene list if a context should differ.</summary>
    public void RefreshVisibility()
    {
        if (_layer == null)
        {
            return;
        }
        _layer.Visible = BarShownForCurrentScene();

        // Return to Campus shows on menu/utility screens (deck editor, card upgrade,
        // library, the strategic map, etc.) but is hidden where it would abandon an
        // active activity (combat, an expedition, a negotiation) or is moot (the
        // campus itself). The pre-game menus already hide the whole bar.
        if (_returnButton != null)
        {
            _returnButton.Visible = ShouldShowReturnToCampus();
        }
        RefreshViewToggle();          // provinces 7f (PR41)
        ReleaseLostScreenButtons();   // provinces 7f (PR41): a screen that left without releasing them
        // Provinces 7e: a screen that left without giving the centre back gives it now.
        if (_centreOwner != null && (!IsInstanceValid(_centreOwner) || !_centreOwner.IsInsideTree()))
        {
            ReleaseCentre(_centreOwner);
        }
        RefreshNextLine();
    }

    /// <summary>True on menu screens where a one-click hop to campus is safe. False in
    /// combat, expeditions, and negotiations (it would abandon them) and on the campus
    /// itself (redundant). Scene matched by file path, like IsHiddenScene.</summary>
    private bool ShouldShowReturnToCampus()
    {
        var current = GetTree().CurrentScene;
        // City view IS home (2026-08-19 hub). Since provinces 7f (PR41) the button is the
        // strategic map's view toggle (Home city on the world, World map in a city), shown in
        // both views.
        if (current is StrategicView)
        {
            return true;
        }
        string lower = (current?.SceneFilePath ?? "").ToLower();
        if (lower.Contains("campus") || lower.Contains("expedition") ||
            lower.Contains("battlefield") || lower.Contains("combat") ||
            lower.Contains("negotiation"))
        {
            return false;
        }
        return true;
    }

    /// <summary>True when the bar shows over the current scene: the test RefreshVisibility
    /// applies, evaluated on demand, so a scene building its UI in the frame of the swap gets the
    /// answer the bar will settle on. CombatUI reads it to seat its top panels at the very top
    /// while the bar is hidden (the prologue's fights).</summary>
    public bool BarShownForCurrentScene() =>
        // Hidden while the campus is an in-world overlay (Stage 3), because the campus draws
        // its own top bar, so the global one would double up.
        SaveManager.ActiveSave != null && !IsHiddenScene() && !PlayerSession.CampusOverlayOpen;

    private bool IsHiddenScene()
    {
        var current = GetTree().CurrentScene;
        string lower = (current?.SceneFilePath ?? "").ToLower();
        if (lower.Contains("mainmenu") || lower.Contains("newgame") || lower.Contains("titlescreen")
            || lower.Contains("timelineend"))
        {
            return true; // pre-game menus only, since the bar spans all gameplay, combat included
        }
        // C4: the prologue comes before the guild has a purse or a campus to return to, so
        // there is no bar in the examination hall or in its fights.
        if (lower.Contains("prologue") || !string.IsNullOrEmpty(RegisterManager.PrologueMode))
            return true;
        // The standalone campus (2026-11-05) draws its own top bar, and the global bar covered
        // its buttons (To the World Map, Begin the Next Reading). Hidden there, as it already is
        // over the campus overlay.
        if (lower.EndsWith("campus/campusscene.tscn"))
            return true;
        return false;
    }
}
