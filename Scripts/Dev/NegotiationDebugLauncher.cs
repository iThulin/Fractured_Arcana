using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// NegotiationDebugLauncher.cs  (dev tooling)
//
// Purpose:        Modal launcher for a standalone negotiation
//                 table, the diplomacy twin of
//                 CombatDebugLauncher. Pick an encounter (every
//                 Data/Negotiations/*.json), the player school,
//                 the faction standing, and optional overrides
//                 (goodwill, patience, forced grievance, extra
//                 tokens, Beguile, building tiers, show every
//                 valuation), bring companions, and launch the
//                 NegotiationScene directly from the hub. The
//                 table returns here on close. Nothing a debug
//                 table does is written to the Hall of Records.
// Layer:          Dev / UI
// Collaborators:  NegotiationManager.cs (reads NegotiationDebug
//                 at table-open; routes ReturnToOverworld here),
//                 NegotiationContext.cs (launch input),
//                 CampusGuildPanel.cs (the "Diplomacy Debug"
//                 button), PlayerSession.DebugNegotiation
// See:            docs/negotiation_ledger_spec_v1.md
// ============================================================

/// <summary>Static overrides a debug-launched table applies at open. Read by
/// NegotiationManager only while PlayerSession.DebugNegotiation is set;
/// cleared on return so a later real table is untouched.</summary>
public static class NegotiationDebug
{
    /// <summary>Faction standing override (−2 Hostile … +2 Allied); null = the save's.</summary>
    public static int? StandingOverride = null;
    /// <summary>Opening goodwill override before archetype modifier; null = authored.</summary>
    public static int? GoodwillOverride = null;
    /// <summary>Patience override; null = authored / archetype default.</summary>
    public static int? PatienceOverride = null;
    /// <summary>Force a grievance regardless of school/standing (tests the squeeze).</summary>
    public static bool ForceGrievance = false;
    /// <summary>Extra leverage tokens added after the normal pool is built.</summary>
    public static Dictionary<LeverageToken, int> ExtraTokens = new();
    /// <summary>Every valuation is shown from the first frame (study mode).</summary>
    public static bool RevealAll = false;
    /// <summary>Portrait voice on/off.</summary>
    public static bool Voice = true;
    /// <summary>Building tier overrides (−1 = use the save's buildings).</summary>
    public static int CourierTier = -1;
    public static int EmbassyTier = -1;
    public static int WarRoomTier = -1;
    /// <summary>Show the "Table details" arithmetic in the log by default.</summary>
    public static bool ShowDetails = true;

    public static void Clear()
    {
        StandingOverride = null;
        GoodwillOverride = null;
        PatienceOverride = null;
        ForceGrievance = false;
        ExtraTokens = new();
        RevealAll = false;
        Voice = true;
        CourierTier = EmbassyTier = WarRoomTier = -1;
        ShowDetails = true;
    }
}

/// <summary>Diplomacy Debug Launcher: the negotiation twin of
/// <see cref="CombatDebugLauncher"/>, same shell, same helpers, same
/// return-to-hub contract.</summary>
public partial class NegotiationDebugLauncher : CanvasLayer
{
    private const string NegotiationScene = "res://Scenes/Negotiation/NegotiationScene.tscn";
    private const string HubScene = "res://Scenes/Overworld/StrategicScene.tscn";
    private const string EncounterDir = "res://Data/Negotiations/";

    private static NegotiationDebugLauncher _instance;
    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);

    private OptionButton _encounterOpt;
    private OptionButton _schoolOpt;
    private OptionButton _standingOpt;
    private SpinBox _goodwillSpin;
    private SpinBox _patienceSpin;
    private CheckBox _grievanceChk;
    private CheckBox _beguileChk;
    private CheckBox _revealChk;
    private CheckBox _voiceChk;
    private CheckBox _detailsChk;
    private SpinBox _courierSpin, _embassySpin, _warRoomSpin;
    private readonly Dictionary<LeverageToken, SpinBox> _tokenSpins = new();
    private readonly List<(CheckBox chk, Companion comp)> _allyChecks = new();
    private readonly List<string> _encounterIds = new();
    private Label _status;
    private Label _encounterInfo;

    public static void Toggle(Node host)
    {
        if (IsOpen) { _instance.QueueFree(); _instance = null; return; }
        if (host == null) return;
        _instance = new NegotiationDebugLauncher { Name = "NegotiationDebugLauncher", Layer = 200 };
        host.AddChild(_instance);
    }

    public static void Close()
    {
        if (IsOpen) { _instance.QueueFree(); _instance = null; }
    }

    /// <summary>Return from a debug-launched table to the hub, clearing the debug
    /// state so a later real table routes and records normally. Called by
    /// NegotiationManager.ReturnToOverworld when PlayerSession.DebugNegotiation is set.</summary>
    public static void ReturnToCampus(Node ctx)
    {
        PlayerSession.DebugNegotiation = false;
        NegotiationDebug.Clear();
        NegotiationContext.Clear();
        CompanionRoster.DebugPartyOverride = null;
        CompanionLoader.ClearCache();
        SceneTransition.Go(ctx?.GetTree(), HubScene, "Table closed", "Returning to the hub.");
    }

    public override void _Ready() => CallDeferred(nameof(BuildUI));
    public override void _ExitTree() { if (_instance == this) _instance = null; }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

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
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -280, OffsetRight = 280, OffsetTop = -330, OffsetBottom = 330,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        backdrop.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        var title = new Label { Text = "Diplomacy Debug Launcher" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        root.AddChild(title);
        root.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(scroll);
        var sm = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        scroll.AddChild(sm);
        var form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        form.AddThemeConstantOverride("separation", 8);
        sm.AddChild(form);

        // ── The table ────────────────────────────────────────────────────
        AddSectionLabel(form, "The table:");
        LoadEncounterIds();
        var names = new string[_encounterIds.Count];
        for (int i = 0; i < _encounterIds.Count; i++)
        {
            var d = NegotiationEncounterLoader.Load(_encounterIds[i]);
            names[i] = d == null ? _encounterIds[i] : $"{d.NpcName} — {d.Archetype}  ({_encounterIds[i]})";
        }
        _encounterOpt = AddStringDropdown(form, "Encounter:", names.Length > 0 ? names : new[] { "(no tables found)" });
        _encounterOpt.ItemSelected += _ => RefreshEncounterInfo();

        _encounterInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _encounterInfo.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _encounterInfo.AddThemeColorOverride("font_color", UITheme.TextDim);
        form.AddChild(_encounterInfo);

        _schoolOpt = AddEnumDropdown(form, "Player school:", Enum.GetValues(typeof(CardSchool)),
            Convert.ToInt32(PlayerSession.SelectedSchool));
        _standingOpt = AddStringDropdown(form, "Faction standing:",
            new[] { "(from save)", "Hostile (−2)", "Unfriendly (−1)", "Neutral (0)", "Friendly (+1)", "Allied (+2)" });

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Ledger overrides (−1 = as authored):");
        _goodwillSpin = AddSpin(form, "Opening goodwill:", -1, 10, 1, -1);
        _patienceSpin = AddSpin(form, "Patience:", -1, 20, 1, -1);

        _grievanceChk = new CheckBox { Text = "Force a grievance (the squeeze fires at the handshake)" };
        _grievanceChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_grievanceChk);

        _beguileChk = new CheckBox { Text = "Beguile armed (+2 goodwill at open, S3)" };
        _beguileChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_beguileChk);

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Extra leverage (added to the normal pool):");
        foreach (LeverageToken t in Enum.GetValues(typeof(LeverageToken)))
            _tokenSpins[t] = AddSpin(form, $"  {t}:", 0, 6, 1, 0);

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Building hooks (−1 = from the save):");
        _courierSpin = AddSpin(form, "Courier Station tier:", -1, 3, 1, -1);
        _embassySpin = AddSpin(form, "Embassy tier:", -1, 3, 1, -1);
        _warRoomSpin = AddSpin(form, "War Room tier:", -1, 3, 1, -1);

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Allies (companions add leverage by trait):");
        foreach (var comp in CompanionLoader.LoadAll())
        {
            var chk = new CheckBox { Text = $"  {comp.Name} ({comp.School}, {comp.PersonalityTrait})" };
            chk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            form.AddChild(chk);
            _allyChecks.Add((chk, comp));
        }

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Study aids:");
        _revealChk = new CheckBox { Text = "Show every valuation from the first frame (◉ on every slip)" };
        _revealChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_revealChk);
        _detailsChk = new CheckBox { Text = "Table details on (goodwill / credit / patience stamps in the log)", ButtonPressed = true };
        _detailsChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_detailsChk);
        _voiceChk = new CheckBox { Text = "Portrait voice", ButtonPressed = NegotiationDebug.Voice };
        _voiceChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_voiceChk);

        var note = new Label
        {
            Text = "A debug table is not recorded in the Hall of Records, moves no reputation, and " +
                   "applies no rewards. Rerun tools/verify_negotiations.py after editing a JSON.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        note.AddThemeColorOverride("font_color", UITheme.TextDim);
        form.AddChild(note);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _status.AddThemeColorOverride("font_color", UITheme.TextDim);
        form.AddChild(_status);

        root.AddChild(new HSeparator());
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 10);
        root.AddChild(btnRow);

        var launch = new Button { Text = "Sit at the Table", CustomMinimumSize = new Vector2(170, 38) };
        UITheme.ApplyButtonStyle(launch, isPrimary: true);
        launch.Pressed += OnLaunch;
        btnRow.AddChild(launch);

        var close = new Button { Text = "Close  [Esc]", CustomMinimumSize = new Vector2(120, 38) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        btnRow.AddChild(close);

        RefreshEncounterInfo();
    }

    private void LoadEncounterIds()
    {
        _encounterIds.Clear();
        var dir = DirAccess.Open(EncounterDir);
        if (dir == null) return;
        dir.ListDirBegin();
        for (string f = dir.GetNext(); !string.IsNullOrEmpty(f); f = dir.GetNext())
        {
            if (dir.CurrentIsDir()) continue;
            // Exported builds list "x.json.remap" / "x.json.import" beside the file.
            if (f.EndsWith(".json")) _encounterIds.Add(f[..^5]);
            else if (f.EndsWith(".json.remap")) _encounterIds.Add(f[..^11]);
        }
        dir.ListDirEnd();
        _encounterIds.Sort(StringComparer.Ordinal);
    }

    private void RefreshEncounterInfo()
    {
        if (_encounterInfo == null || _encounterIds.Count == 0) return;
        int i = Mathf.Clamp(_encounterOpt.Selected, 0, _encounterIds.Count - 1);
        var d = NegotiationEncounterLoader.Load(_encounterIds[i]);
        if (d == null) { _encounterInfo.Text = "(failed to load)"; return; }
        int theirs = 0, yours = 0;
        foreach (var c in d.Clauses) { if (c.Side == ClauseSide.Theirs) theirs++; else yours++; }
        var distrusts = d.ResolvedDistrusts;
        _encounterInfo.Text =
            $"{d.Title} · faction {d.FactionId} · patience {d.ResolvedPatience} · counters {d.ResolvedCounterStyle} · " +
            $"{theirs} to ask for, {yours} to give · distrusts: {(distrusts.Count == 0 ? "nobody" : string.Join(", ", distrusts))}";
    }

    private void OnLaunch()
    {
        if (_encounterIds.Count == 0)
        {
            _status.Text = "No encounter files under Data/Negotiations.";
            _status.AddThemeColorOverride("font_color", UITheme.Danger);
            return;
        }
        int i = Mathf.Clamp(_encounterOpt.Selected, 0, _encounterIds.Count - 1);
        string id = _encounterIds[i];
        var data = NegotiationEncounterLoader.Load(id);
        if (data == null)
        {
            _status.Text = $"'{id}' failed to load; see the log.";
            _status.AddThemeColorOverride("font_color", UITheme.Danger);
            return;
        }

        NegotiationDebug.Clear();
        NegotiationDebug.StandingOverride = _standingOpt.Selected switch
        {
            1 => -2, 2 => -1, 3 => 0, 4 => 1, 5 => 2, _ => null,
        };
        NegotiationDebug.GoodwillOverride = _goodwillSpin.Value >= 0 ? (int)_goodwillSpin.Value : null;
        NegotiationDebug.PatienceOverride = _patienceSpin.Value >= 0 ? (int)_patienceSpin.Value : null;
        NegotiationDebug.ForceGrievance = _grievanceChk.ButtonPressed;
        NegotiationDebug.RevealAll = _revealChk.ButtonPressed;
        NegotiationDebug.Voice = _voiceChk.ButtonPressed;
        NegotiationDebug.ShowDetails = _detailsChk.ButtonPressed;
        NegotiationDebug.CourierTier = (int)_courierSpin.Value;
        NegotiationDebug.EmbassyTier = (int)_embassySpin.Value;
        NegotiationDebug.WarRoomTier = (int)_warRoomSpin.Value;
        foreach (var kvp in _tokenSpins)
            if ((int)kvp.Value.Value > 0) NegotiationDebug.ExtraTokens[kvp.Key] = (int)kvp.Value.Value;

        var party = new List<Companion>();
        foreach (var (chk, comp) in _allyChecks)
            if (chk.ButtonPressed) party.Add(comp);
        CompanionRoster.DebugPartyOverride = party.Count > 0 ? party : null;

        PlayerSession.SelectedSchool = (CardSchool)_schoolOpt.GetSelectedId();
        PlayerSession.DebugNegotiation = true;
        PlayerSession.DebugMode = true;

        NegotiationContext.Clear();
        NegotiationContext.EncounterId = id;
        NegotiationContext.HexCoordKey = "";
        NegotiationContext.NpcArchetype = data.Archetype.ToString();
        NegotiationContext.OriginKingdomId = "";   // wilds: no court, no echo
        NegotiationContext.TensionShift = _beguileChk.ButtonPressed ? 2 : 0;

        GD.Print($"[NegotiationDebug] Launch: {id} ({data.Archetype}), school={PlayerSession.SelectedSchool}, " +
                 $"standing={(NegotiationDebug.StandingOverride?.ToString() ?? "save")}, " +
                 $"goodwill={(NegotiationDebug.GoodwillOverride?.ToString() ?? "authored")}, " +
                 $"patience={(NegotiationDebug.PatienceOverride?.ToString() ?? "authored")}, " +
                 $"grievance={NegotiationDebug.ForceGrievance}, reveal={NegotiationDebug.RevealAll}, " +
                 $"allies={party.Count}.");

        _instance = null;   // scene swap frees us
        SceneTransition.Go(GetTree(), NegotiationScene, "To the Table", "Debug negotiation.");
    }

    // ── UI helpers (same shapes as CombatDebugLauncher) ───────────────────

    private OptionButton AddStringDropdown(VBoxContainer form, string label, string[] items)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(MakeLabel(label, 150));
        var opt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (int i = 0; i < items.Length; i++)
            opt.AddItem(items[i], i);
        opt.Selected = 0;
        row.AddChild(opt);
        form.AddChild(row);
        return opt;
    }

    private OptionButton AddEnumDropdown(VBoxContainer form, string label, Array values, int selectedId)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(MakeLabel(label, 150));
        var opt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var v in values)
            opt.AddItem(v.ToString(), Convert.ToInt32(v));
        for (int i = 0; i < opt.ItemCount; i++)
            if (opt.GetItemId(i) == selectedId) { opt.Selected = i; break; }
        row.AddChild(opt);
        form.AddChild(row);
        return opt;
    }

    private SpinBox AddSpin(VBoxContainer form, string label, double min, double max, double step, double val)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(MakeLabel(label, 150));
        var spin = new SpinBox
        {
            MinValue = min, MaxValue = max, Step = step, Value = val,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        row.AddChild(spin);
        form.AddChild(row);
        return spin;
    }

    private Label MakeLabel(string text, int minWidth)
    {
        var l = new Label { Text = text, CustomMinimumSize = new Vector2(minWidth, 0) };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        l.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        return l;
    }

    private void AddSectionLabel(VBoxContainer form, string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        l.AddThemeColorOverride("font_color", UITheme.Gold);
        form.AddChild(l);
    }
}
