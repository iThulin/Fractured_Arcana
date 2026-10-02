import sys, re, os
root = sys.argv[1]

def edit(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print("ok", rel)

# 1. Ledger: once flags
edit('Scripts/Data/SaveState/EternalLedger.cs', [(
'''    public List<string> MetaNarrativeFlags = new();
''',
'''    public List<string> MetaNarrativeFlags = new();

    /// <summary>
    /// The Register's once flags (docs/the_register_v1.md §4): ids of explanations
    /// already shown. Eternal, so an explanation never repeats across cycles.
    /// Null on pre-feature saves; RegisterManager backfills on first write.
    /// </summary>
    public List<string> RegisterSeen = new();
''')])

# 2. Settings: two toggles
edit('Scripts/Systems/SettingsManager.cs', [
(
'''    public float MasterVolume { get; private set; } = 1.0f;
''',
'''    public float MasterVolume { get; private set; } = 1.0f;

    /// <summary>The Register's remarks and asides (comment + flavor barks).</summary>
    public bool RegisterCommentary { get; private set; } = true;
    /// <summary>The Register's one-time onboarding explanations.</summary>
    public bool RegisterExplanations { get; private set; } = true;
'''),
(
'''    // ── Apply helpers ────────────────────────────────────────────────────────
''',
'''    public void SetRegisterCommentary(bool on)
    {
        RegisterCommentary = on;
        SaveSettings();
    }

    public void SetRegisterExplanations(bool on)
    {
        RegisterExplanations = on;
        SaveSettings();
    }

    // ── Apply helpers ────────────────────────────────────────────────────────
'''),
(
'''        MasterVolume = (float)cfg.GetValue("audio", "master_volume", 1.0f);
''',
'''        MasterVolume = (float)cfg.GetValue("audio", "master_volume", 1.0f);
        RegisterCommentary = (bool)cfg.GetValue("register", "commentary", true);
        RegisterExplanations = (bool)cfg.GetValue("register", "explanations", true);
'''),
(
'''        cfg.SetValue("audio", "master_volume", MasterVolume);
''',
'''        cfg.SetValue("audio", "master_volume", MasterVolume);
        cfg.SetValue("register", "commentary", RegisterCommentary);
        cfg.SetValue("register", "explanations", RegisterExplanations);
'''),
])

# 3. Settings menu rows
edit('Scripts/UI/SettingsMenu.cs', [
(
'''    private Button       _backButton;
''',
'''    private Button       _backButton;
    private CheckBox     _registerCommentaryCheck;
    private CheckBox     _registerExplainCheck;
'''),
(
'''        PopulateResolutionDropdown();
        PopulateWindowModeDropdown();
        ReadCurrentValues();
''',
'''        BuildRegisterRows();
        PopulateResolutionDropdown();
        PopulateWindowModeDropdown();
        ReadCurrentValues();
'''),
(
'''    private void PopulateResolutionDropdown()
''',
'''    // ════════════════════════════════════════════════════════════════════════
    //  The Register's toggles (built in code from the scene's own rows)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Adds a "Register" section with the two toggles from the_register_v1 §4.
    /// Built by duplicating the scene's Audio header and VSync row, so the new rows
    /// inherit the scene's styling without a .tscn edit.</summary>
    private void BuildRegisterRows()
    {
        var vsyncRow = _vsyncCheck?.GetParent() as Control;
        var settings = vsyncRow?.GetParent() as Control;
        if (vsyncRow == null || settings == null)
        {
            GD.PrintErr("[SettingsMenu] VSync row not found, so the Register toggles are not shown.");
            return;
        }

        if (FindChild("AudioSpacer", true) is Control spacer)
            settings.AddChild(spacer.Duplicate());
        if (FindChild("AudioHeader", true) is Label headerTemplate)
        {
            var header = (Label)headerTemplate.Duplicate();
            header.Name = "RegisterHeader";
            header.Text = "Register";
            settings.AddChild(header);
        }

        _registerCommentaryCheck = AddCheckRow(settings, vsyncRow, "RegisterCommentary", "Commentary");
        _registerExplainCheck = AddCheckRow(settings, vsyncRow, "RegisterExplanations", "Explanations");
    }

    private static CheckBox AddCheckRow(Control settings, Control template, string id, string text)
    {
        var row = (Control)template.Duplicate();
        row.Name = id + "Row";
        settings.AddChild(row);

        CheckBox check = null;
        foreach (var child in row.GetChildren())
        {
            if (child is CheckBox c)
            {
                check = c;
                c.Name = id + "Check";
            }
            else if (child is Label l)
            {
                l.Name = id + "Label";
                l.Text = text;
            }
        }
        return check;
    }

    private void PopulateResolutionDropdown()
'''),
(
'''        if (_volumeSlider  != null) _volumeSlider.Value       = sm.MasterVolume;
''',
'''        if (_volumeSlider  != null) _volumeSlider.Value       = sm.MasterVolume;
        if (_registerCommentaryCheck != null) _registerCommentaryCheck.ButtonPressed = sm.RegisterCommentary;
        if (_registerExplainCheck    != null) _registerExplainCheck.ButtonPressed    = sm.RegisterExplanations;
'''),
(
'''        if (_backButton    != null) _backButton.Pressed         += OnBackPressed;
''',
'''        if (_backButton    != null) _backButton.Pressed         += OnBackPressed;
        if (_registerCommentaryCheck != null)
            _registerCommentaryCheck.Toggled += on => SettingsManager.Instance?.SetRegisterCommentary(on);
        if (_registerExplainCheck != null)
            _registerExplainCheck.Toggled += on => SettingsManager.Instance?.SetRegisterExplanations(on);
'''),
])

# 4. Autoload
edit('project.godot', [(
'''SettingsManager="*res://Scripts/Systems/SettingsManager.cs"
''',
'''SettingsManager="*res://Scripts/Systems/SettingsManager.cs"
RegisterManager="*res://Scripts/Systems/Register/RegisterManager.cs"
''')])

# 5. Combat hooks
edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
(
'''        currentPhase = CombatPhase.PlayerTurn;
        enemyPhaseRunning = false;
        _endTurnConfirmPending = false;   // a new turn never inherits last turn's warning
''',
'''        currentPhase = CombatPhase.PlayerTurn;
        enemyPhaseRunning = false;
        _endTurnConfirmPending = false;   // a new turn never inherits last turn's warning

        // The Register (the_register_v1 §4-5): a new round opens the remark budget,
        // and the combat basics explain themselves the first time (once flags).
        RegisterManager.NoteCombatTurn(roundNumber);
        RegisterManager.Fire("combat.basics.hand");
        RegisterManager.Fire("combat.basics.halves");
        RegisterManager.Fire("combat.basics.move");
'''),
(
'''        combatUI?.AppendActionLog("Victory!");
''',
'''        combatUI?.AppendActionLog("Victory!");
        RegisterManager.Fire("combat.victory");
'''),
(
'''        _deathsThisCombat++;   // map_pressure_v2: first_blood and the like read this
''',
'''        _deathsThisCombat++;   // map_pressure_v2: first_blood and the like read this
        if (unit.IsPlayerControlled && !string.IsNullOrEmpty(unit.CompanionId))
            RegisterManager.Fire("combat.ally_down");
'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.Triggers.cs', [(
'''        _priorityWindowOpen = true;
        _priorityPassed = false;
''',
'''        _priorityWindowOpen = true;
        _priorityPassed = false;
        RegisterManager.Fire("combat.reflex_window");
''')])

edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [(
'''            UpdateIntentDisplay(enemy);
        }

        LogMarkerLegend();
        RefreshThreatTiles();
''',
'''            UpdateIntentDisplay(enemy);
        }

        LogMarkerLegend();
        RefreshThreatTiles();

        foreach (var enemy in enemyUnits)
        {
            if (IsValidActor(enemy) && enemy.CurrentIntent?.TargetTile != null)
            {
                RegisterManager.Fire("combat.tile_intent");
                break;
            }
        }
''')])

edit('Scripts/Systems/Combat/Core/ElementReactions.cs', [(
'''        Say($"[Reaction] {DisplayName(existing, incoming)} at {tile.Axial}: {DisplayName(reaction)}. {Describe(reaction)}");
''',
'''        Say($"[Reaction] {DisplayName(existing, incoming)} at {tile.Axial}: {DisplayName(reaction)}. {Describe(reaction)}");
        RegisterManager.Fire("reaction." + reaction.ToString().ToLowerInvariant());
''')])

# Attunement burst: two sites, same call
p = os.path.join(root, 'Scripts/Systems/Combat/Attunement/ElementalAttunement.cs')
s = open(p, encoding='utf-8').read()
pat = re.compile(r'^([ \t]*)OnBurstTriggered\?\.Invoke\(element\);\n', re.M)
hits = pat.findall(s)
assert len(hits) == 2, f"burst sites: {len(hits)}"
s = pat.sub(lambda m: m.group(0) + m.group(1) + 'if (!CombatSim.Active) RegisterManager.Fire("elementalist.burst");\n', s)
open(p, 'w', encoding='utf-8').write(s)
print("ok ElementalAttunement.cs")

# 6. Strategic hooks
edit('Scripts/Systems/Strategic/StrategicView.cs', [
(
'''            RefreshPieceChrome();
            ShowStrategicNotice("Posted", line);
''',
'''            RefreshPieceChrome();
            ShowStrategicNotice("Posted", line);
            RegisterManager.Fire("strategic.first_posting");
'''),
(
'''    private void RunLunationTick(CycleState cycle)
    {
''',
'''    private void RunLunationTick(CycleState cycle)
    {
        RegisterManager.Fire("strategic.first_lunation");
'''),
])

edit('Scripts/Systems/Strategic/StrategicView.Dispatch.cs', [(
'''        _atlas3D?.HighlightDispatch(null);
        RefreshPieceChrome();
        if (takeField)
''',
'''        _atlas3D?.HighlightDispatch(null);
        RefreshPieceChrome();
        RegisterManager.Fire("strategic.first_waystone");
        if (takeField)
''')])

edit('Scripts/Systems/Campaign/CouncilScreen.cs', [(
'''        _instance = new CouncilScreen { Name = "CouncilScreen", Layer = 128 };
        host.AddChild(_instance);
''',
'''        _instance = new CouncilScreen { Name = "CouncilScreen", Layer = 128 };
        host.AddChild(_instance);
        RegisterManager.Fire("strategic.first_council");
''')])

edit('Scripts/Systems/Negotiation/NegotiationManager.cs', [(
'''        BuildUI();
        InitializeNegotiation();
    }
''',
'''        BuildUI();
        InitializeNegotiation();
        RegisterManager.Fire("strategic.first_negotiation");
    }
''')])

edit('Scripts/Data/SaveState/SaveManager.cs', [(
'''        SeedDeckForSchool(ActiveSave, school);

        Save();
''',
'''        SeedDeckForSchool(ActiveSave, school);

        RegisterManager.Fire("cycle.first_unmaking");   // shown on the campus that follows
        Save();
''')])

edit('Scripts/Systems/Campus/CampusScreen.cs', [(
'''    public override void _Ready()
    {
        PlayerDeckSave.UseDebugDeck = false; // campus is the real-deck home; debug routing off
''',
'''    public override void _Ready()
    {
        RegisterManager.Fire("campus.visit");
        PlayerDeckSave.UseDebugDeck = false; // campus is the real-deck home; debug routing off
''')])
