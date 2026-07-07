using Godot;
using System.Collections.Generic;

// ============================================================
// CouncilScreen.cs
//
// Purpose:        The fully-built-out Court & Council presentation
//                 screen — successor to CouncilPanel's testing UI.
//                 Opens as a global overlay from anywhere (keybind
//                 or top-bar button). Layout follows the design
//                 sketch: a KINGDOM BANNER TAB-STRIP across the top,
//                 the selected court's courtiers as ARCHED PORTRAIT
//                 frames, a compact ACTIONS row, and a full-width
//                 RUMOUR RIBBON along the bottom.
//
//                 v1 scope: VIEW surface. Because it can open over
//                 combat/expeditions, Actions are live only outside
//                 combat/POI events (ActionsLockReason) and dispatch
//                 through a host-wired OnActionRequested hook.
//
//                 Placeholders (flagged, not stubs):
//                   - Portrait arches are top-rounded initial tiles
//                     until a real portrait asset pipeline exists;
//                     final frame geometry is a designer pass.
//                   - Rumour flavour pool is deterministic filler
//                     shown only when a court has no real report
//                     lines yet.
// Layer:          UI (global overlay)
// Collaborators:  CouncilState.cs, CouncilTick.cs (name/office
//                 helpers), CouncilQueries, UITheme.cs,
//                 ArchmageRegistry.cs, EncounterRouter.cs, SaveManager.
// See:            court_council_system_v1_1.docx §3, §6, §8
// ============================================================

/// <summary>Global Court &amp; Council overlay. Kingdom banner tabs select the
/// court; courtiers show as arched portraits; a rumour ribbon runs along the
/// bottom. Open/close via <see cref="Toggle"/>.</summary>
public partial class CouncilScreen : CanvasLayer
{
    private static CouncilScreen _instance;

    /// <summary>True while the screen is on-screen.</summary>
    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);

    /// <summary>Host hook for the Actions column: (kingdomId, actionId). The
    /// strategic view (and any other safe host) wires this to the real dispatch
    /// flow. Availability is decided by ActionsLockReason(), not by whether the
    /// hook is wired — buttons are disabled with a reason during encounters.</summary>
    public static System.Action<string, string> OnActionRequested;

    /// <summary>Set true by scenes while a POI event panel is open (narrative
    /// card, scout report) — the one encounter state a global overlay can't
    /// derive. Combat/negotiation and expedition state are derived directly.</summary>
    public static bool EncounterLockout = false;

    /// <summary>Actions are live outside combat and POI events. Returns null
    /// when available, else the human-readable reason they're locked.</summary>
    private static string ActionsLockReason()
    {
        if (EncounterRouter.Instance != null && EncounterRouter.Instance.HasPendingReturn)
        {
            return "The council waits — you are mid-encounter.";
        }
        if (EncounterLockout)
        {
            return "The council waits — resolve the event before you.";
        }
        if (OnActionRequested == null)
        {
            return "Council actions are taken from the strategic view.";
        }
        return null;
    }

    // Action ids the buttons emit (stable strings; host maps them to real flow).
    public const string ActionDispatchEnvoy = "dispatch_envoy";
    public const string ActionPresentGifts = "present_gifts";
    public const string ActionCourtCourtier = "court_courtier";
    public const string ActionGatherIntel = "gather_intelligence";

    private const int PortraitColumns = 4;
    private const int RumourTail = 3;
    private const int ArchCornerRadius = 46; // top-rounded frame ≈ a gothic arch

    private int _selectedIndex = 0;
    private readonly List<string> _courtIds = new();
    private readonly List<Button> _tabButtons = new();

    // Header widgets.
    private Label _kingdomNameLabel;
    private Label _seatLabel;
    private Label _bandLabel;
    private Label _posLabel;

    // Body widgets.
    private HBoxContainer _tabStrip;
    private GridContainer _courtierGrid;
    private VBoxContainer _rumourBox;
    private HBoxContainer _actionBox;
    private Label _statusLabel;
    private Label _footerLabel;

    // ══════════════════════════════════════════════════════════════════════
    // Open / close
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Open if closed, close if open. Pass any node in the tree as host.</summary>
    public static void Toggle(Node host)
    {
        if (IsOpen)
        {
            _instance.QueueFree();
            _instance = null;
            return;
        }
        if (host == null)
        {
            return;
        }
        _instance = new CouncilScreen { Name = "CouncilScreen", Layer = 128 };
        host.AddChild(_instance);
    }

    public static void Close()
    {
        if (IsOpen)
        {
            _instance.QueueFree();
            _instance = null;
        }
    }

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUI));
    }

    public override void _ExitTree()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key)
        {
            return;
        }
        switch (key.Keycode)
        {
            case Key.Escape:
                Close();
                GetViewport().SetInputAsHandled();
                break;
            case Key.Left:
                Step(-1);
                GetViewport().SetInputAsHandled();
                break;
            case Key.Right:
                Step(1);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Build
    // ══════════════════════════════════════════════════════════════════════

    private void BuildUI()
    {
        var backdrop = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);
        var shade = new ColorRect { Color = UITheme.BgOverlay };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backdrop.AddChild(shade);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -560,
            OffsetRight = 560,
            OffsetTop = -360,
            OffsetBottom = 360,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        backdrop.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        margin.AddChild(root);

        BuildHeader(root);
        BuildKingdomTabs(root);
        root.AddChild(new HSeparator());
        BuildCourtArea(root);
        BuildActionsRow(root);
        BuildRumourRibbon(root);

        _footerLabel = new Label();
        _footerLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _footerLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        root.AddChild(_footerLabel);

        ResolveCourtIds();
        PopulateTabs();
        RefreshAll();
    }

    private void BuildHeader(VBoxContainer root)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        root.AddChild(row);

        var title = new Label { Text = "The Council" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        row.AddChild(title);

        row.AddChild(new VSeparator());

        var switcher = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        switcher.AddThemeConstantOverride("separation", 0);
        row.AddChild(switcher);

        _kingdomNameLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _kingdomNameLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _kingdomNameLabel.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        switcher.AddChild(_kingdomNameLabel);

        _seatLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _seatLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _seatLabel.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        switcher.AddChild(_seatLabel);

        var rightBox = new VBoxContainer();
        rightBox.AddThemeConstantOverride("separation", 0);
        row.AddChild(rightBox);

        _bandLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _bandLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        rightBox.AddChild(_bandLabel);

        _posLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _posLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _posLabel.AddThemeColorOverride("font_color", UITheme.TextDim);
        rightBox.AddChild(_posLabel);

        var close = new Button { Text = "Close  [Esc]", CustomMinimumSize = new Vector2(110, 34) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        row.AddChild(close);
    }

    /// <summary>The kingdom banner strip — one tab per court, across the top.
    /// Populated in PopulateTabs once the court ids are resolved.</summary>
    private void BuildKingdomTabs(VBoxContainer root)
    {
        _tabStrip = new HBoxContainer();
        _tabStrip.AddThemeConstantOverride("separation", 4);
        root.AddChild(_tabStrip);
    }

    private void PopulateTabs()
    {
        foreach (var b in _tabButtons)
        {
            b.QueueFree();
        }
        _tabButtons.Clear();

        var cycle = Cycle;
        if (cycle == null)
        {
            return;
        }
        for (int i = 0; i < _courtIds.Count; i++)
        {
            string kid = _courtIds[i];
            var btn = new Button
            {
                Text = CouncilTick.CourtDisplayName(cycle, kid),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                ClipText = true,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            int captured = i;
            btn.Pressed += () => { _selectedIndex = captured; RefreshAll(); };
            _tabStrip.AddChild(btn);
            _tabButtons.Add(btn);
        }
    }

    private void BuildCourtArea(VBoxContainer root)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(scroll);

        var scrollMargin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin, // Compatibility rule
        };
        scrollMargin.AddThemeConstantOverride("margin_right", 8);
        scroll.AddChild(scrollMargin);

        _courtierGrid = new GridContainer
        {
            Columns = PortraitColumns,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _courtierGrid.AddThemeConstantOverride("h_separation", 12);
        _courtierGrid.AddThemeConstantOverride("v_separation", 12);
        scrollMargin.AddChild(_courtierGrid);
    }

    private void BuildActionsRow(VBoxContainer root)
    {
        _statusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _statusLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _statusLabel.AddThemeColorOverride("font_color", UITheme.Success);
        root.AddChild(_statusLabel);

        _actionBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _actionBox.AddThemeConstantOverride("separation", 6);
        root.AddChild(_actionBox);
    }

    /// <summary>The rumour ribbon — a full-width banner along the bottom carrying
    /// this kingdom's Herald lines (or deterministic flavour when none yet).</summary>
    private void BuildRumourRibbon(VBoxContainer root)
    {
        var ribbon = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ribbon.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Violet));
        root.AddChild(ribbon);

        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 12);
        m.AddThemeConstantOverride("margin_right", 12);
        m.AddThemeConstantOverride("margin_top", 6);
        m.AddThemeConstantOverride("margin_bottom", 6);
        ribbon.AddChild(m);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        m.AddChild(v);

        var header = new Label { Text = "Rumours & Word" };
        header.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        header.AddThemeColorOverride("font_color", UITheme.Violet);
        v.AddChild(header);

        _rumourBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rumourBox.AddThemeConstantOverride("separation", 2);
        v.AddChild(_rumourBox);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Data resolution
    // ══════════════════════════════════════════════════════════════════════

    private CycleState Cycle => SaveManager.ActiveSave?.Cycle;

    private void ResolveCourtIds()
    {
        _courtIds.Clear();
        var council = Cycle?.Council;
        if (council == null)
        {
            return;
        }
        foreach (var id in council.Courts.Keys)
        {
            _courtIds.Add(id);
        }
        _courtIds.Sort(System.StringComparer.Ordinal);

        int firstContacted = -1;
        for (int i = 0; i < _courtIds.Count; i++)
        {
            if (council.Courts[_courtIds[i]].HasContact)
            {
                firstContacted = i;
                break;
            }
        }
        _selectedIndex = Mathf.Clamp(firstContacted >= 0 ? firstContacted : 0,
            0, Mathf.Max(0, _courtIds.Count - 1));
    }

    private void Step(int dir)
    {
        if (_courtIds.Count == 0)
        {
            return;
        }
        _selectedIndex = (_selectedIndex + dir + _courtIds.Count) % _courtIds.Count;
        RefreshAll();
    }

    private CourtState SelectedCourt()
    {
        var council = Cycle?.Council;
        if (council == null || _courtIds.Count == 0)
        {
            return null;
        }
        return council.Courts.TryGetValue(_courtIds[_selectedIndex], out var c) ? c : null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Refresh
    // ══════════════════════════════════════════════════════════════════════

    private void RefreshAll()
    {
        var cycle = Cycle;
        var court = SelectedCourt();
        if (cycle == null || court == null)
        {
            _kingdomNameLabel.Text = "No courts exist in this timeline.";
            return;
        }

        var band = court.Band();
        _kingdomNameLabel.Text = CouncilTick.CourtDisplayName(cycle, court.KingdomId);
        _seatLabel.Text = SeatDisplay(cycle, court);
        _bandLabel.Text = band.ToString();
        _bandLabel.AddThemeColorOverride("font_color", BandColor(band));
        _posLabel.Text = $"Court {_selectedIndex + 1} / {_courtIds.Count}";

        RefreshTabs();
        RefreshCourtiers(court);
        RefreshRumours(court);
        RefreshActions(cycle, court);
        RefreshFooter(cycle, court, band);
    }

    private void RefreshTabs()
    {
        for (int i = 0; i < _tabButtons.Count; i++)
        {
            UITheme.ApplyButtonStyle(_tabButtons[i], isPrimary: i == _selectedIndex);
        }
    }

    private void RefreshCourtiers(CourtState court)
    {
        foreach (var child in _courtierGrid.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var c in court.Courtiers)
        {
            _courtierGrid.AddChild(BuildArchCard(court, c));
        }
    }

    /// <summary>One courtier as an arched portrait frame (top-rounded panel).</summary>
    private Control BuildArchCard(CourtState court, CourtierState c)
    {
        bool contacted = court.HasContact;
        bool agent = contacted && c.IsCorruptedAgent;

        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 196),
        };
        var borderColor = agent ? UITheme.Danger
            : contacted ? RegardColor(c.Regard) : UITheme.TextDim;
        card.AddThemeStyleboxOverride("panel", MakeArchStyle(UITheme.BgRaised, borderColor));

        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 10);
        m.AddThemeConstantOverride("margin_right", 10);
        m.AddThemeConstantOverride("margin_top", 10);
        m.AddThemeConstantOverride("margin_bottom", 8);
        card.AddChild(m);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        m.AddChild(v);

        var portraitRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        v.AddChild(portraitRow);
        portraitRow.AddChild(BuildPortrait(contacted, agent, c));

        if (!contacted)
        {
            AddCentered(v, "A figure of the court", UITheme.TextDim, UITheme.CampusSmallFontSize);
            AddCentered(v, CouncilTick.OfficeDisplay(c.Office), UITheme.TextDim, UITheme.CampusTinyFontSize);
            return card;
        }

        AddCentered(v, c.DisplayName, UITheme.TextPrimary, UITheme.CampusSmallFontSize);
        AddCentered(v, $"{CouncilTick.OfficeDisplay(c.Office)}  ·  {c.Archetype}",
            UITheme.TextSecondary, UITheme.CampusTinyFontSize);

        var meters = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        meters.AddThemeConstantOverride("separation", 10);
        v.AddChild(meters);
        meters.AddChild(BuildRegardMeter(c.Regard));
        meters.AddChild(BuildInfluencePips(c.Influence));

        string badge = null;
        if (agent)
        {
            badge = "the court whispers against you";
        }
        else if (c.SecretKnown)
        {
            badge = "a secret is known to the guild";
        }
        else if (court.PatronCourtierId == c.Id)
        {
            badge = "sworn patron of the guild";
        }
        if (badge != null)
        {
            AddCentered(v, badge, agent ? UITheme.Danger : UITheme.Gold, UITheme.CampusTinyFontSize);
        }

        return card;
    }

    private Control BuildPortrait(bool contacted, bool agent, CourtierState c)
    {
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(64, 72) };
        var accent = agent ? UITheme.Danger : contacted ? UITheme.Gold : UITheme.TextDim;
        frame.AddThemeStyleboxOverride("panel", MakeArchStyle(UITheme.BgBase, accent));

        var label = new Label
        {
            Text = contacted ? Initials(c.DisplayName) : "?",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        label.AddThemeColorOverride("font_color", contacted ? UITheme.TextSecondary : UITheme.TextDim);
        frame.AddChild(label);
        return frame;
    }

    /// <summary>A top-rounded, square-bottomed panel style — the arch shape.
    /// Placeholder until real portrait-frame art; the corner radius is a knob a
    /// designer pass can tune.</summary>
    private static StyleBoxFlat MakeArchStyle(Color bg, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            CornerRadiusTopLeft = ArchCornerRadius,
            CornerRadiusTopRight = ArchCornerRadius,
            CornerRadiusBottomLeft = 0,
            CornerRadiusBottomRight = 0,
        };
    }

    /// <summary>Seven-cell Regard meter, centre cell = 0.</summary>
    private Control BuildRegardMeter(int regard)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        for (int i = 0; i < 7; i++)
        {
            int offset = i - 3; // −3..+3
            Color color;
            if (offset == 0)
            {
                color = UITheme.TextSecondary;
            }
            else if (regard > 0 && offset > 0 && offset <= regard)
            {
                color = UITheme.Success;
            }
            else if (regard < 0 && offset < 0 && offset >= regard)
            {
                color = UITheme.Danger;
            }
            else
            {
                color = UITheme.TextDim;
            }
            box.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(10, 8) });
        }
        return box;
    }

    private Control BuildInfluencePips(int influence)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        for (int i = 0; i < 3; i++)
        {
            box.AddChild(new ColorRect
            {
                Color = i < influence ? UITheme.Gold : UITheme.TextDim,
                CustomMinimumSize = new Vector2(10, 8),
            });
        }
        return box;
    }

    private void RefreshRumours(CourtState court)
    {
        foreach (var child in _rumourBox.GetChildren())
        {
            child.QueueFree();
        }

        var lines = new List<string>();
        var reports = Cycle?.Council?.Reports;
        if (reports != null)
        {
            foreach (var r in reports)
            {
                if (r.KingdomId == court.KingdomId)
                {
                    lines.Add($"L{r.Lunation}  {r.Text}");
                }
            }
        }

        if (lines.Count == 0)
        {
            foreach (var f in PlaceholderRumours(court))
            {
                AddRumourLine(f, UITheme.TextDim);
            }
            return;
        }

        int start = Mathf.Max(0, lines.Count - RumourTail);
        for (int i = start; i < lines.Count; i++)
        {
            AddRumourLine(lines[i], UITheme.TextSecondary);
        }
    }

    private void AddRumourLine(string text, Color color)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        l.AddThemeColorOverride("font_color", color);
        _rumourBox.AddChild(l);
    }

    private void RefreshActions(CycleState cycle, CourtState court)
    {
        foreach (var child in _actionBox.GetChildren())
        {
            child.QueueFree();
        }

        var mission = CouncilQueries.MissionAt(court.KingdomId);
        if (mission != null)
        {
            var envoy = cycle.Companions.Find(c => c.Id == mission.CompanionId);
            var def = CouncilMissions.Get(mission.MissionType);
            _statusLabel.Text = mission.Recalled
                ? $"{envoy?.Name ?? mission.CompanionId} is travelling home."
                : $"{envoy?.Name ?? mission.CompanionId} — {def?.DisplayName ?? mission.MissionType}, " +
                  $"{mission.LunationsRemaining} lunation(s) left.";
        }
        else if (!string.IsNullOrEmpty(court.PatronCourtierId))
        {
            var patron = court.GetCourtier(court.PatronCourtierId);
            _statusLabel.Text = patron != null
                ? $"Patron at court: {patron.DisplayName}."
                : "No envoy afield here.";
        }
        else
        {
            _statusLabel.Text = "No envoy afield here.";
        }

        string reason = ActionsLockReason();
        _statusLabel.Text += reason != null ? $"   ·   {reason}" : "";

        AddActionButton(court, "Dispatch Envoy", ActionDispatchEnvoy,
            "Send a companion to hold the guild's presence at this court.");
        AddActionButton(court, "Present Gifts", ActionPresentGifts,
            "A gift matched to a courtier's tastes can warm a cold room.");
        AddActionButton(court, "Court a Courtier", ActionCourtCourtier,
            "Cultivate a receptive power into a sworn patron.");
        AddActionButton(court, "Gather Intelligence", ActionGatherIntel,
            "Work the shadows — chart the ground, uncover a secret. Raises exposure.");
    }

    private void AddActionButton(CourtState court, string label, string actionId, string flavour)
    {
        string lockReason = ActionsLockReason();
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(0, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Disabled = lockReason != null,
            TooltipText = lockReason ?? flavour,
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: lockReason == null);
        string kid = court.KingdomId;
        btn.Pressed += () => OnActionRequested?.Invoke(kid, actionId);
        _actionBox.AddChild(btn);
    }

    private void RefreshFooter(CycleState cycle, CourtState court, CourtStandingBand band)
    {
        var save = SaveManager.ActiveSave;
        int cap = CouncilQueries.EnvoyCap(save);
        int active = cycle.Council.ActiveMissions.Count;
        string exposure = court.Exposure > 0 ? $"Exposure {court.Exposure}/10" : "Exposure clear";
        _footerLabel.Text =
            $"Standing: {band} ({court.StandingScore()})    ·    {exposure}    ·    " +
            $"Envoys afield: {active}/{cap}    ·    Gold: {save.Gold}";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    private void AddCentered(VBoxContainer parent, string text, Color color, int fontSize)
    {
        var l = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        l.AddThemeFontSizeOverride("font_size", fontSize);
        l.AddThemeColorOverride("font_color", color);
        parent.AddChild(l);
    }

    private static string Initials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "?";
        }
        var parts = displayName.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return parts[0].Substring(0, 1).ToUpperInvariant();
        }
        return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
    }

    private static string SeatDisplay(CycleState cycle, CourtState court)
    {
        if (court.IsRegentCourt)
        {
            return string.IsNullOrEmpty(court.RegentName) ? "Regent court" : $"Regent: {court.RegentName}";
        }
        if (cycle.Kingdoms.TryGetValue(court.KingdomId, out var ks) &&
            !string.IsNullOrEmpty(ks.ArchmageId))
        {
            var def = ArchmageRegistry.Get(ks.ArchmageId);
            return "Seat: " + (def?.DisplayName ?? ks.ArchmageId);
        }
        return "Seat: unknown";
    }

    private static Color BandColor(CourtStandingBand band) => band switch
    {
        CourtStandingBand.Trusted => UITheme.Gold,
        CourtStandingBand.Favored => UITheme.Success,
        CourtStandingBand.Welcome => UITheme.Violet,
        CourtStandingBand.Received => UITheme.TextSecondary,
        CourtStandingBand.Hostile => UITheme.Danger,
        _ => UITheme.TextDim, // Unknown
    };

    private static Color RegardColor(int regard)
    {
        if (regard > 0)
        {
            return UITheme.Success;
        }
        if (regard < 0)
        {
            return UITheme.Danger;
        }
        return UITheme.TextSecondary;
    }

    /// <summary>Deterministic placeholder flavour, shown only when a court has no
    /// real Herald lines yet. Seeded off the kingdom id so it's stable per court.</summary>
    private static List<string> PlaceholderRumours(CourtState court)
    {
        string[] pool =
        {
            "The court speaks little of the guild — as yet.",
            "Servants trade gossip in the colonnades; none of it names you.",
            "A minor lord wonders aloud what the guild wants here.",
            "The halls are quiet. Word of your deeds has not yet arrived.",
            "Couriers come and go. None carry your name.",
            "Talk turns to harvests and taxes, not to sorcerers.",
        };
        int seed = 0;
        foreach (char ch in court.KingdomId)
        {
            seed = (seed * 31 + ch) & 0x7fffffff;
        }
        var lines = new List<string>();
        int count = court.HasContact ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            lines.Add(pool[(seed + i * 7) % pool.Length]);
        }
        return lines;
    }
}
