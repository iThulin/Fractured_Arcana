using Godot;
using System.Collections.Generic;

// ============================================================
// CouncilScreen.cs
//
// Purpose:        The fully-built-out Court & Council presentation
//                 screen. Opens as a global overlay from anywhere
//                 (keybind or top-bar button). Layout follows the
//                 design sketch: a KINGDOM BANNER TAB-STRIP across
//                 the top, the selected court's courtiers as ARCHED
//                 PORTRAIT frames, a compact ACTIONS row, and a
//                 full-width RUMOUR RIBBON along the bottom.
//
//                 Dispatch is NATIVE: the Actions row shows one
//                 button per mission in the catalog; pressing one
//                 opens a compact modal that runs the real flow
//                 (envoy -> target -> confirm). Recall of the active
//                 mission lives inline in the same row. The old
//                 host-wired OnActionRequested delegation is retired
//                 (see "RETIREMENT" note below).
//
//                 Action availability is contextual: live only
//                 outside combat and POI events.
//                   - Combat/negotiation: EncounterRouter
//                     .HasPendingReturn (derived).
//                   - POI event panels: EncounterLockout, set by the
//                     owning scene (one-line integrations).
//
//                 Global-dispatch guards (over the old testing UI):
//                   - On expedition, in-party companions cannot be
//                     dispatched (deploy-time HP/loadout would
//                     silently desync). Off expedition, unchanged.
//                   - A court under Expulsion freeze refuses
//                     dispatch outright (MissionFreezeLunations).
//                   - Imprisoned companions are excluded.
//                 Commit-time re-validation is COMPLETE: the modal
//                 can sit open across a lunation boundary, so
//                 ConfirmDispatch re-checks the target courtier
//                 (courtship +2 floor / petition office can lapse)
//                 and Recall re-checks the encounter lock. Target
//                 filtering is single-sourced through
//                 ValidDispatchTargets so render and commit cannot
//                 diverge.
//
//                 RETIREMENT: OnActionRequested and the four action-
//                 id consts are GONE. Any host that wired them
//                 (StrategicView) must have that wiring removed —
//                 grep for "OnActionRequested", "ActionDispatchEnvoy",
//                 "ActionPresentGifts", "ActionCourtCourtier",
//                 "ActionGatherIntel" and delete those references or
//                 the compile breaks.
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
//                 helpers), CouncilMissions.cs (catalog),
//                 CouncilLedger.cs (petition targets), CouncilQueries,
//                 CompanionRoster.cs (party removal), UITheme.cs,
//                 ArchmageRegistry.cs, EncounterRouter.cs, SaveManager.
// See:            court_council_system_v1_1.docx §3, §5, §6, §8
// ============================================================

/// <summary>Global Court &amp; Council overlay. Kingdom banner tabs select the
/// court; courtiers show as arched portraits; a rumour ribbon runs along the
/// bottom. The action row dispatches natively via a modal. Open/close via
/// <see cref="Toggle"/>.</summary>
public partial class CouncilScreen : CanvasLayer
{
    private static CouncilScreen _instance;

    /// <summary>True while the screen is on-screen.</summary>
    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);

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
        return null;
    }

    private const int PortraitColumns = 4;
    private const int RumourTail = 3;
    private const int ArchCornerRadius = 46; // top-rounded frame ≈ a gothic arch

    private int _selectedIndex = 0;
    private readonly List<string> _courtIds = new();
    private readonly List<Button> _tabButtons = new();

    // Dispatch-modal state (one flow at a time, for the current court).
    private bool _flowOpen = false;
    private string _selMissionId = null;
    private string _selCompanionId = null;
    private string _selTargetCourtierId = null;
    private Control _flowOverlay;
    private Label _flowTitle;
    private VBoxContainer _flowBody;

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
    private HFlowContainer _standingsFlow; // Step 9: archmage standings (moved off the strategic map)

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
                // A live dispatch modal swallows Escape first — close it, not the
                // whole screen.
                if (_flowOpen)
                {
                    CloseFlow();
                }
                else
                {
                    Close();
                }
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
        BuildStandingsStrip(root);

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
            btn.Pressed += () => { _selectedIndex = captured; CloseFlow(); RefreshAll(); };
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
        CloseFlow();
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
        RefreshStandings(cycle);
        RefreshFooter(cycle, court, band);
    }

    /// <summary>Step 9 (relocated from the strategic map per user ruling): the
    /// at-a-glance archmage standings — every placed archmage's signed
    /// sentiment (or disposition once resolved), faction-colored, in one
    /// wrapping strip above the footer. Full detail lives on the campus
    /// Council tab.</summary>
    private void BuildStandingsStrip(VBoxContainer root)
    {
        _standingsFlow = new HFlowContainer();
        _standingsFlow.AddThemeConstantOverride("h_separation", 18);
        _standingsFlow.AddThemeConstantOverride("v_separation", 2);
        root.AddChild(_standingsFlow);
    }

    private void RefreshStandings(CycleState cycle)
    {
        if (_standingsFlow == null) return;
        foreach (var child in _standingsFlow.GetChildren())
            child.QueueFree();

        var campaign = cycle?.Campaign;
        if (campaign == null || campaign.RegionArchmageMap.Count == 0) return;

        var header = new Label { Text = "The Archmagi:" };
        header.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        header.AddThemeColorOverride("font_color", UITheme.POINarrative);
        _standingsFlow.AddChild(header);

        foreach (var pair in campaign.RegionArchmageMap)
        {
            string id = pair.Value;
            if (string.IsNullOrEmpty(id)) continue;
            var def = ArchmageRegistry.Get(id);
            if (def == null || def.IsVillainFaction) continue;

            var disp = campaign.GetDisposition(id);
            string text;
            if (disp == ArchmageDisposition.Unknown || disp == ArchmageDisposition.Neutral)
            {
                int s = campaign.GetSentiment(id);
                text = $"{def.DisplayName} {(s > 0 ? "+" : "")}{s}";
            }
            else
            {
                text = $"{def.DisplayName} · {disp}";
            }

            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            lbl.AddThemeColorOverride("font_color", new Color(def.FactionColorHex));
            _standingsFlow.AddChild(lbl);
        }
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

    // ══════════════════════════════════════════════════════════════════════
    // Actions row — native dispatch launchers + inline recall
    // ══════════════════════════════════════════════════════════════════════

    private void RefreshActions(CycleState cycle, CourtState court)
    {
        foreach (var child in _actionBox.GetChildren())
        {
            child.QueueFree();
        }

        var save = SaveManager.ActiveSave;
        string encLock = ActionsLockReason();

        // ── A mission is already afield at this court: status + recall ───────
        var mission = CouncilQueries.MissionAt(court.KingdomId);
        if (mission != null)
        {
            var envoy = cycle.Companions.Find(c => c.Id == mission.CompanionId);
            var mdef = CouncilMissions.Get(mission.MissionType);
            _statusLabel.Text = mission.Recalled
                ? $"{envoy?.Name ?? mission.CompanionId} is travelling home " +
                  $"({mission.LunationsRemaining} lunation)."
                : $"{envoy?.Name ?? mission.CompanionId} — {mdef?.DisplayName ?? mission.MissionType}, " +
                  $"{mission.LunationsRemaining} lunation(s) left.";
            if (encLock != null)
            {
                _statusLabel.Text += $"   ·   {encLock}";
            }

            if (!mission.Recalled)
            {
                var recallBtn = new Button
                {
                    Text = "Recall Envoy",
                    CustomMinimumSize = new Vector2(0, 32),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Disabled = encLock != null,
                    TooltipText = encLock ?? "Bring the envoy home; the mission yields nothing.",
                };
                recallBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                UITheme.ApplyButtonStyle(recallBtn, isPrimary: false);
                recallBtn.Pressed += () =>
                {
                    // Re-check the lock at commit — the screen is global, so an
                    // encounter or POI panel may have opened since this rendered.
                    if (ActionsLockReason() != null)
                    {
                        RefreshAll();
                        return;
                    }
                    mission.Recalled = true;
                    mission.LunationsRemaining = 1; // travel home
                    SaveManager.Save();
                    RefreshAll();
                };
                _actionBox.AddChild(recallBtn);
            }
            return; // one mission per court — no dispatch while one is live
        }

        // ── Idle: status line, then one launcher button per mission ─────────
        if (!string.IsNullOrEmpty(court.PatronCourtierId))
        {
            var patron = court.GetCourtier(court.PatronCourtierId);
            _statusLabel.Text = patron != null
                ? $"Patron at court: {patron.DisplayName}. No envoy afield."
                : "No envoy afield here.";
        }
        else
        {
            _statusLabel.Text = "No envoy afield here.";
        }
        if (encLock != null)
        {
            _statusLabel.Text += $"   ·   {encLock}";
        }
        else if (court.MissionFreezeLunations > 0)
        {
            _statusLabel.Text += $"   ·   The court's doors are closed " +
                                 $"({court.MissionFreezeLunations} lunation(s) remain).";
        }

        var bandNow = court.Band();
        int embassyTier = CouncilQueries.EmbassyTier(save);
        bool capFull = cycle.Council.ActiveMissions.Count >= CouncilQueries.EnvoyCap(save);

        foreach (var def in CouncilMissions.All)
        {
            // Per-mission availability, in priority order. Encounter lock and the
            // Expulsion freeze dominate; then contact/standing/embassy gating; then
            // the shared envoy cap. Gold and envoy/target validity are checked in
            // the modal and re-checked at commit.
            string lockReason = encLock;
            if (lockReason == null && court.MissionFreezeLunations > 0)
            {
                lockReason = "the court is closed to the guild";
            }
            if (lockReason == null && def.RequiresContact && !court.HasContact)
            {
                lockReason = "requires contact";
            }
            if (lockReason == null && bandNow < def.MinBand)
            {
                lockReason = $"requires {def.MinBand} standing";
            }
            if (lockReason == null && embassyTier < def.RequiredEmbassyTier)
            {
                lockReason = $"requires Embassy tier {def.RequiredEmbassyTier}";
            }
            if (lockReason == null && capFull)
            {
                lockReason = "no envoys free";
            }

            string missionId = def.Id;
            var btn = new Button
            {
                Text = $"{def.DisplayName} ({def.Lunations}◐, {def.GoldCost}g)",
                CustomMinimumSize = new Vector2(0, 32),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Disabled = lockReason != null,
                TooltipText = lockReason ?? def.Blurb,
                ClipText = true,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(btn, isPrimary: lockReason == null);
            btn.Pressed += () => OpenFlow(missionId);
            _actionBox.AddChild(btn);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Dispatch modal — envoy -> (target) -> confirm, for the chosen mission
    // ══════════════════════════════════════════════════════════════════════

    private void OpenFlow(string missionId)
    {
        _selMissionId = missionId;
        _selCompanionId = null;
        _selTargetCourtierId = null;
        _flowOpen = true;
        if (_flowOverlay != null)
        {
            _flowOverlay.QueueFree();
            _flowOverlay = null;
        }
        BuildFlowOverlay();
        RefreshFlow();
    }

    private void CloseFlow()
    {
        _flowOpen = false;
        _selMissionId = null;
        _selCompanionId = null;
        _selTargetCourtierId = null;
        if (_flowOverlay != null)
        {
            _flowOverlay.QueueFree();
            _flowOverlay = null;
        }
    }

    private void BuildFlowOverlay()
    {
        var dim = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim); // added after the main panel -> renders on top
        _flowOverlay = dim;

        var shade = new ColorRect { Color = UITheme.BgOverlay };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.AddChild(shade);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -300,
            OffsetRight = 300,
            OffsetTop = -260,
            OffsetBottom = 260,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        dim.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        margin.AddChild(v);

        _flowTitle = new Label();
        _flowTitle.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _flowTitle.AddThemeColorOverride("font_color", UITheme.Gold);
        v.AddChild(_flowTitle);
        v.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        v.AddChild(scroll);

        var sm = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin, // Compatibility rule
        };
        scroll.AddChild(sm);

        _flowBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _flowBody.AddThemeConstantOverride("separation", 6);
        sm.AddChild(_flowBody);
    }

    /// <summary>Rebuild the modal body for the current selection state. The mission
    /// is fixed (chosen by the launcher button); this picks envoy and, if needed,
    /// target, then confirms.</summary>
    private void RefreshFlow()
    {
        if (_flowBody == null)
        {
            return;
        }
        foreach (var child in _flowBody.GetChildren())
        {
            child.QueueFree();
        }

        var cycle = Cycle;
        var court = SelectedCourt();
        var save = SaveManager.ActiveSave;
        var def = _selMissionId != null ? CouncilMissions.Get(_selMissionId) : null;
        if (cycle == null || court == null || save == null || def == null)
        {
            CloseFlow();
            return;
        }

        _flowTitle.Text = $"{def.DisplayName} — {CouncilTick.CourtDisplayName(cycle, court.KingdomId)}";
        AddFlowLabel(def.Blurb, UITheme.TextSecondary);

        // 1. Envoy selection. On expedition, in-party companions are NOT
        // dispatchable (deploy-time HP/loadout would desync); imprisoned and
        // already-afield companions are excluded outright.
        AddFlowLabel("Envoy:", UITheme.Gold);
        bool onExpedition = PlayerSession.IsOnExpedition;
        bool anyCompanion = false;
        foreach (var c in cycle.Companions)
        {
            if (!c.IsRecruited || c.IsPermadead)
            {
                continue;
            }
            // K2 (§5b): injured companions are out of all three demands —
            // recovering at the infirmary, not dispatchable. Same outright
            // exclusion as imprisoned/afield.
            if (c.IsInjured)
            {
                continue;
            }
            if (CouncilQueries.IsOnMission(c.Id) || CouncilQueries.IsImprisoned(c.Id))
            {
                continue;
            }
            // Cache overseers are posted afield (SupplyCacheSystem) — same
            // outright exclusion as envoys on mission.
            if (SupplyCacheSystem.IsOverseer(c.Id))
            {
                continue;
            }
            bool inParty = save.ActivePartyCompanionIds.Contains(c.Id);
            bool blocked = onExpedition && inParty;
            anyCompanion = anyCompanion || !blocked;

            string label = c.Name + (inParty ? " (in party)" : "");
            AddSelectButton(label, _selCompanionId == c.Id,
                () => { _selCompanionId = c.Id; RefreshFlow(); },
                disabled: blocked,
                tooltip: blocked ? "In the field with you — cannot be dispatched mid-expedition." : null);
        }
        if (!anyCompanion)
        {
            AddFlowLabel("  No companions free to send.", UITheme.TextDim);
        }

        // 2. Target courtier, if the mission needs one. Single-sourced through
        // ValidDispatchTargets so this render and the commit re-check can't drift.
        if (def.NeedsTargetCourtier)
        {
            bool isPetition = def.Id == CouncilMissions.PetitionMinor;
            bool isCourtship = def.Id == CouncilMissions.CourtCourtier;
            var targets = ValidDispatchTargets(court, def.Id);

            AddFlowLabel(isPetition ? "Petition of:" : (isCourtship ? "Court:" : "Recipient:"), UITheme.Gold);
            if (targets.Count == 0)
            {
                AddFlowLabel(isCourtship
                    ? "  No courtier's regard runs deep enough to court (needs +2)."
                    : "  No receptive courtier holds a favor-granting office.", UITheme.TextDim);
            }
            foreach (var c in targets)
            {
                string cid = c.Id;
                string label;
                if (isPetition)
                {
                    label = $"{c.DisplayName} — {CouncilTick.OfficeDisplay(c.Office)} " +
                            $"({CouncilLedger.OfficeToFavorType(c.Office)})";
                }
                else if (isCourtship)
                {
                    label = $"{c.DisplayName} — {CouncilTick.OfficeDisplay(c.Office)} (Regard +{c.Regard})";
                }
                else
                {
                    label = c.DisplayName;
                }
                AddSelectButton(label, _selTargetCourtierId == cid,
                    () => { _selTargetCourtierId = cid; RefreshFlow(); });
            }
        }

        // 3. Confirm / cancel.
        _flowBody.AddChild(new HSeparator());
        var confirmRow = new HBoxContainer();
        confirmRow.AddThemeConstantOverride("separation", 10);
        _flowBody.AddChild(confirmRow);

        bool ready = _selCompanionId != null &&
                     (!def.NeedsTargetCourtier || _selTargetCourtierId != null);
        bool affordable = save.Gold >= def.GoldCost;

        var confirmBtn = new Button
        {
            Text = affordable ? $"Send ({def.GoldCost}g)" : $"Need {def.GoldCost}g",
            CustomMinimumSize = new Vector2(140, 32),
            Disabled = !ready || !affordable,
        };
        confirmBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(confirmBtn, isPrimary: ready && affordable);
        confirmBtn.Pressed += () => ConfirmDispatch(save, cycle, court);
        confirmRow.AddChild(confirmBtn);

        var cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(100, 32) };
        cancelBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(cancelBtn, isPrimary: false);
        cancelBtn.Pressed += () => CloseFlow();
        confirmRow.AddChild(cancelBtn);
    }

    /// <summary>The set of courtiers a mission may target, single-sourced so
    /// render-time filtering and commit-time re-validation can never diverge.
    /// Caller ensures the mission actually needs a target.</summary>
    private static List<CourtierState> ValidDispatchTargets(CourtState court, string missionId)
    {
        if (missionId == CouncilMissions.PetitionMinor)
        {
            return CouncilLedger.PetitionTargets(court);
        }
        if (missionId == CouncilMissions.CourtCourtier)
        {
            var list = new List<CourtierState>();
            foreach (var c in court.Courtiers)
            {
                if (c.Regard >= 2 && court.PatronCourtierId != c.Id)
                {
                    list.Add(c);
                }
            }
            return list;
        }
        return court.Courtiers;
    }

    private void ConfirmDispatch(GuildSaveData save, CycleState cycle, CourtState court)
    {
        var def = CouncilMissions.Get(_selMissionId);
        if (def == null || _selCompanionId == null)
        {
            return;
        }
        // Re-validate EVERYTHING at commit — the modal is global and may have sat
        // open across a lunation boundary since it was rendered.
        if (ActionsLockReason() != null)
        {
            return;
        }
        if (court.MissionFreezeLunations > 0)
        {
            return;
        }
        if (save.Gold < def.GoldCost)
        {
            return;
        }
        if (cycle.Council.ActiveMissions.Count >= CouncilQueries.EnvoyCap(save))
        {
            return;
        }
        if (CouncilQueries.MissionAt(court.KingdomId) != null)
        {
            return; // one mission per court
        }
        if (def.RequiresContact && !court.HasContact)
        {
            return;
        }
        if (court.Band() < def.MinBand)
        {
            return;
        }
        if (CouncilQueries.EmbassyTier(save) < def.RequiredEmbassyTier)
        {
            return;
        }
        if (CouncilQueries.IsOnMission(_selCompanionId) ||
            CouncilQueries.IsImprisoned(_selCompanionId) ||
            SupplyCacheSystem.IsOverseer(_selCompanionId))
        {
            return;
        }
        if (PlayerSession.IsOnExpedition &&
            save.ActivePartyCompanionIds.Contains(_selCompanionId))
        {
            return; // in-party guard, re-checked at commit
        }
        // Target re-validation: a courtship target can fall below +2 or become the
        // patron, and a petition office can lapse, while the modal sat open. Re-
        // filter from the same source as render and require the selection present.
        if (def.NeedsTargetCourtier)
        {
            if (_selTargetCourtierId == null)
            {
                return;
            }
            bool targetStillValid = false;
            foreach (var t in ValidDispatchTargets(court, def.Id))
            {
                if (t.Id == _selTargetCourtierId)
                {
                    targetStillValid = true;
                    break;
                }
            }
            if (!targetStillValid)
            {
                return;
            }
        }

        save.Gold -= def.GoldCost;

        // Envoys leave the expedition pool: instant dispatch (v1.1 ruling).
        CompanionRoster.RemoveFromParty(_selCompanionId);

        cycle.Council.ActiveMissions.Add(new EnvoyMission
        {
            CompanionId = _selCompanionId,
            KingdomId = court.KingdomId,
            MissionType = def.Id,
            LunationsRemaining = def.Lunations,
            TargetCourtierId = _selTargetCourtierId ?? "",
            Recalled = false,
        });

        GD.Print($"[Council] Dispatched {_selCompanionId} to {court.KingdomId} " +
                 $"({def.Id}, {def.Lunations} lunation(s), {def.GoldCost}g).");

        CloseFlow();
        SaveManager.Save();
        RefreshAll();
    }

    // ── Modal-flow UI helpers ────────────────────────────────────────────

    private void AddFlowLabel(string text, Color color)
    {
        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        lbl.AddThemeColorOverride("font_color", color);
        _flowBody.AddChild(lbl);
    }

    private void AddSelectButton(string text, bool selected, System.Action onPress,
                                 bool disabled = false, string tooltip = null)
    {
        var btn = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonPressed = selected,
            Disabled = disabled,
            CustomMinimumSize = new Vector2(0, 28),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = tooltip ?? "",
            ClipText = true,
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: selected);
        btn.Pressed += () => onPress();
        _flowBody.AddChild(btn);
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
            string seat = "Seat: " + (def?.DisplayName ?? ks.ArchmageId);

            // Step 9 (moved off the strategic map): this seat's standing with
            // the guild — signed sentiment while unresolved, disposition once
            // resolved.
            var campaign = cycle.Campaign;
            if (campaign != null && def != null && !def.IsVillainFaction)
            {
                var disp = campaign.GetDisposition(ks.ArchmageId);
                if (disp == ArchmageDisposition.Unknown || disp == ArchmageDisposition.Neutral)
                {
                    int s = campaign.GetSentiment(ks.ArchmageId);
                    seat += $"   ·   sentiment {(s > 0 ? "+" : "")}{s}";
                }
                else
                {
                    seat += $"   ·   {disp}";
                }
            }
            return seat;
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
