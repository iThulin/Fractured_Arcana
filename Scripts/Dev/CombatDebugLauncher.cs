using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatDebugLauncher.cs  (dev tooling)
//
// Purpose:        A configurable combat test-launcher so encounters
//                 can be exercised without walking the overworld loop.
//                 Pick the player's school, the encounter tier, the map
//                 terrain, a per-archetype enemy count, and a difficulty
//                 multiplier; "Launch Combat" builds an EncounterDefinition,
//                 sets the same EncounterContextCarrier the overworld router
//                 uses, and swaps to Battlefield.tscn. Doubles as the U1
//                 "one encounter per tier reads identically" harness.
// Layer:          UI (dev overlay)
// Collaborators:  EncounterContextCarrier / EncounterDefinition (combat
//                 input), UnitRegistry (enemy labels), PlayerSession
//                 (school / debug flags), UITheme.
// See:            build_order_v3 §4 (Phase B). Dev-only; not shipped UI.
// ============================================================

/// <summary>Dev overlay that assembles an EncounterDefinition from dropdowns and
/// launches Battlefield.tscn. Open/close via <see cref="Toggle"/>; Esc closes.</summary>
public partial class CombatDebugLauncher : CanvasLayer
{
    private const string BattlefieldScene = "res://Scenes/Combat/Battlefield.tscn";
    private const string CampusScene = "res://Scenes/Overworld/StrategicScene.tscn";   // hub swap 2026-08-19

    private static CombatDebugLauncher _instance;
    public static bool IsOpen => _instance != null && IsInstanceValid(_instance);

    private OptionButton _schoolOpt;
    private OptionButton _tierOpt;
    private OptionButton _mapOpt;
    private OptionButton _vistaOpt;  // debug vista border terrain (same as map = no bias)
    private SpinBox _diffSpin;
    private CheckBox _skipDeployChk;
    private CheckBox _stopOnTriggersChk;
    private CheckBox _wavesChk;
    private CheckBox _surviveChk;
    private CheckBox _protectChk;   // O3
    private OptionButton _mapEventKindOpt;
    private OptionButton _mapEventElemOpt;
    private CheckBox _noHazardCapChk;
    private Label _status;
    private readonly Dictionary<string, SpinBox> _enemySpins = new();  // unit id → count (U2: registry-driven)
    private readonly List<(CheckBox chk, Companion comp)> _allyChecks = new();

    public static void Toggle(Node host)
    {
        if (IsOpen) { _instance.QueueFree(); _instance = null; return; }
        if (host == null) return;
        _instance = new CombatDebugLauncher { Name = "CombatDebugLauncher", Layer = 200 };
        host.AddChild(_instance);
    }

    public static void Close()
    {
        if (IsOpen) { _instance.QueueFree(); _instance = null; }
    }

    /// <summary>Return from a debug-launched fight to the campus, clearing the debug
    /// flags so a later real encounter routes normally. Called by combat-end wiring
    /// and by the pause menu's Forfeit when PlayerSession.DebugCombat is set.</summary>
    public static void ReturnToCampus(Node ctx)
    {
        PlayerSession.DebugCombat = false;
        PlayerSession.DebugMapEventKind = null;
        PlayerSession.DebugDisableHazardCap = false;
        PlayerSession.DebugMapObjects = null;
        PlayerSession.DebugMode = false;
        PlayerSession.SkipDeployment = false;
        PlayerDeckSave.UseDebugDeck = false;
        CompanionRoster.DebugPartyOverride = null;
        CompanionLoader.ClearCache();
        ctx?.GetTree()?.ChangeSceneToFile(CampusScene);
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

        var title = new Label { Text = "Combat Debug Launcher" };
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

        _schoolOpt = AddEnumDropdown(form, "Player school:", Enum.GetValues(typeof(CardSchool)),
            Convert.ToInt32(PlayerSession.SelectedSchool));
        _tierOpt = AddEnumDropdown(form, "Tier:", Enum.GetValues(typeof(EncounterTier)),
            (int)EncounterTier.Battle);
        // Show the recipe each overworld terrain resolves to, so "Mountain"
        // reads as "Mountain → highland_crags" (the debug picker uses overworld
        // terrain names; the recipe files use their own names, and this bridges them).
        TerrainRecipeMap.EnsureLoaded();
        _mapOpt = AddEnumDropdown(form, "Map / terrain:", Enum.GetValues(typeof(OverworldHex.TerrainType)),
            (int)OverworldHex.TerrainType.Grassland,
            v => $"{v} → {TerrainRecipeMap.Resolve((OverworldHex.TerrainType)v)}");
        // Debug stand-in for overworld adjacency: pretends ALL six neighbouring
        // world hexes are this terrain, so the vista ring leans toward it.
        // Same as the map terrain = no bias (pure field continuation).
        _vistaOpt = AddEnumDropdown(form, "Vista border:", Enum.GetValues(typeof(OverworldHex.TerrainType)),
            (int)OverworldHex.TerrainType.Grassland);
        // E6: force a specific battlefield archetype, or "(from terrain)" for the map dropdown's default.
        var recipeItems = new string[BattlefieldRecipes.Length + 7];
        recipeItems[0] = "(from terrain)";
        for (int ri = 0; ri < BattlefieldRecipes.Length; ri++)
            recipeItems[ri + 1] = BattlefieldRecipes[ri];
        recipeItems[BattlefieldRecipes.Length + 1] = CompiledGateLabel;          // city siege: gate attack
        recipeItems[BattlefieldRecipes.Length + 2] = CompiledGateDefenseLabel;   // city siege: hold_zone defense
        recipeItems[BattlefieldRecipes.Length + 3] = CompiledBreachLabel;        // city siege: breach attack
        recipeItems[BattlefieldRecipes.Length + 4] = CompiledDockDefenseLabel;   // city siege: quay defense
        recipeItems[BattlefieldRecipes.Length + 5] = CompiledPortalDefenseLabel; // city siege: rift defense
        recipeItems[BattlefieldRecipes.Length + 6] = CastleDefenseLabel;         // mobile fortress: defend the castle
        _forceRecipeOpt = AddStringDropdown(form, "Force battlefield:", recipeItems);
        _diffSpin = AddSpin(form, "Difficulty ×:", 0.5, 3.0, 0.25, 1.0);

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Battlefield test injectors (new mechanics):");

        // E4 map events: inject a synthetic scheduled event onto whatever map is
        // launched (bf_cauldron already ships one; this lets you watch the ring /
        // spread / patch on any terrain). Fires round 2, then every 2 rounds.
        _mapEventKindOpt = AddStringDropdown(form, "Map event:",
            new[] { "(none)", "advance_hazard_ring", "spread_element", "imbue_patch", "collapse_tiles", "raise_tiles", "lower_tiles", "spawn_object", "weather_tick" });
        _mapEventElemOpt = AddStringDropdown(form, "  event element:",
            new[] { "fire", "frost", "lightning", "earth", "arcane" });

        _noHazardCapChk = new CheckBox { Text = "Disable hazard cap (see the uncapped map)" };
        _noHazardCapChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_noHazardCapChk);

        // E3: spawn neutral map objects near the arena centre for isolated testing
        // (independent of any map_object ops the launched recipe carries).
        var mapObjNote = new Label
        {
            Text = "Spawn map objects near centre (E3 test):",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        mapObjNote.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        mapObjNote.AddThemeColorOverride("font_color", UITheme.TextDim);
        form.AddChild(mapObjNote);
        foreach (string kind in MapObjectKinds)
        {
            string moLabel = MapObjectCatalog.TryGet(kind, out var moSpec)
                ? $"  {moSpec.Label} (HP {moSpec.Hp}):"
                : $"  {kind}:";
            _mapObjectSpins[kind] = AddSpin(form, moLabel, 0, 4, 1, 0);
        }

        // Expedition patterns: spawn a premade enemy composition straight from a
        // region's encounterPools (the same tables the overworld draws from). Pick a
        // region, pick one of its compositions, "Apply" clears the roster below and
        // fills it with that pattern's enemies.
        var patternNote = new Label
        {
            Text = "Spawn a premade expedition pattern into the roster (region -> pattern -> Apply):",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        patternNote.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        patternNote.AddThemeColorOverride("font_color", UITheme.TextDim);
        form.AddChild(patternNote);

        foreach (var r in RegionLoader.LoadAll())
            _regions.Add(r);
        var regionNames = new string[_regions.Count];
        for (int i = 0; i < _regions.Count; i++)
            regionNames[i] = _regions[i].DisplayName;
        _patternRegionOpt = AddStringDropdown(form, "Pattern region:",
            regionNames.Length > 0 ? regionNames : new[] { "(no regions)" });
        _patternRegionOpt.ItemSelected += idx => RebuildPatternDropdown((int)idx);

        _patternOpt = AddStringDropdown(form, "Pattern:", new[] { "(select a region)" });
        RebuildPatternDropdown(0);

        var applyPatternBtn = new Button
        {
            Text = "Apply pattern -> roster",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        applyPatternBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(applyPatternBtn, isPrimary: false);
        applyPatternBtn.Pressed += ApplySelectedPattern;
        form.AddChild(applyPatternBtn);

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Player deck & cards:");
        var deckNote = new Label
        {
            Text = "Debug combat uses a SEPARATE scratch deck, not your real one. \"Edit Debug Deck\" opens the editor on that scratch deck (seeded from the selected class's starter the first time). Upgrades apply to owned cards and are shared.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        deckNote.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        deckNote.AddThemeColorOverride("font_color", UITheme.TextDim);
        form.AddChild(deckNote);
        var deckRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        deckRow.AddThemeConstantOverride("separation", 6);
        deckRow.AddChild(MakeDebugDeckEditButton());
        deckRow.AddChild(MakeNavButton("Upgrade Cards", "res://Scenes/UI/CardUpgradeScreen.tscn"));
        deckRow.AddChild(MakeNavButton("Card Library", "res://Scenes/UI/CardLibrary.tscn"));
        form.AddChild(deckRow);
        var resetDeckBtn = new Button
        {
            Text = "Reset Debug Deck (reseed selected class starter)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        resetDeckBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(resetDeckBtn, isPrimary: false);
        resetDeckBtn.Pressed += ResetDebugDeck;
        form.AddChild(resetDeckBtn);

        form.AddChild(new HSeparator());
        AddSectionLabel(form, "Enemies (count of each):");
        // U2: registry-driven roster. Every Data/Units/*.json shows up here
        // automatically, which is exactly the harness the U2 exit criterion
        // ("a debug encounter fielding tagged units") needs.
        BuildEnemyRoster(form);
        form.AddChild(new HSeparator());

        AddSectionLabel(form, "Allies (bring companions with real cards + stats):");
        foreach (var comp in CompanionLoader.LoadAll())
        {
            var chk = new CheckBox { Text = $"  {comp.Name} ({comp.School})" };
            // The helmsman is the crew every real fight has; field him by default so
            // a castle defence launched from here has someone to hold the walls.
            chk.ButtonPressed = comp.Id == CompanionRoster.StartingDriverId;
            chk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            form.AddChild(chk);
            _allyChecks.Add((chk, comp));
        }
        form.AddChild(new HSeparator());

        _skipDeployChk = new CheckBox { Text = "Skip deployment (jump straight in)", ButtonPressed = true };
        _skipDeployChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_skipDeployChk);

        _stopOnTriggersChk = new CheckBox
        {
            Text = "Stop on enemy triggers (U3 stack windows, even w/o Reactions)",
            ButtonPressed = PlayerSession.DebugStopOnTriggers,
        };
        _stopOnTriggersChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_stopOnTriggersChk);

        // ── O-track levers (docs/combat_objectives_spec_v1.md) ──────────────
        // Same design rule as the strategic debug harness: a lever sets the
        // state the SHIPPED path already reads, so a forced run exercises the
        // same code an authored encounter would. These write a real
        // CombatObjectiveDef / ReinforcementWave onto the definition, and nothing
        // here reimplements the runtime.
        _wavesChk = new CheckBox
        {
            Text = "Reinforcement waves (rounds 3 and 5, mirroring the roster)",
        };
        _wavesChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_wavesChk);

        _surviveChk = new CheckBox { Text = "Objective: survive 6 rounds" };
        _surviveChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_surviveChk);

        // O3 (2026-08-13): protect test. Spawns the Anchor as the ward.
        _protectChk = new CheckBox { Text = "Objective: protect the Anchor (ward)" };
        _protectChk.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        form.AddChild(_protectChk);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        _status.AddThemeColorOverride("font_color", UITheme.TextDim);
        form.AddChild(_status);

        root.AddChild(new HSeparator());
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 10);
        root.AddChild(btnRow);

        var launch = new Button { Text = "Launch Combat", CustomMinimumSize = new Vector2(170, 38) };
        UITheme.ApplyButtonStyle(launch, isPrimary: true);
        launch.Pressed += OnLaunch;
        btnRow.AddChild(launch);

        var close = new Button { Text = "Close  [Esc]", CustomMinimumSize = new Vector2(120, 38) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += Close;
        btnRow.AddChild(close);
    }

    private void OnLaunch()
    {
        var tier = (EncounterTier)_tierOpt.GetSelectedId();
        var terrain = ((OverworldHex.TerrainType)_mapOpt.GetSelectedId()).ToString();
        float diff = (float)_diffSpin.Value;

        var def = new EncounterDefinition
        {
            Id = "debug_launch",
            DisplayName = "Debug Encounter",
            Tier = tier,
            RegionId = "debug",
            TerrainType = terrain,
            DifficultyMult = diff,
        };

        int total = 0;
        foreach (var kvp in _enemySpins)
        {
            int n = (int)kvp.Value.Value;
            for (int i = 0; i < n; i++)
            {
                def.Enemies.Add(new EnemySlot(kvp.Key, diff));
            }
            total += n;
        }

        if (total == 0)
        {
            _status.Text = "Add at least one enemy before launching.";
            _status.AddThemeColorOverride("font_color", UITheme.Danger);
            return;
        }

        // O-track: attach the debug objective/waves AFTER the roster exists, so
        // a wave can mirror what was actually selected rather than hardcoding
        // unit ids that may not be in this build.
        if (_surviveChk != null && _surviveChk.ButtonPressed)
        {
            def.Objective = new CombatObjectiveDef
            {
                Kind = CombatObjectiveDef.KindSurvive,
                Rounds = 6,
                Description = "Hold the ground",
            };
        }

        // O3: protect wins over survive when both are ticked (one objective
        // per fight, because the def has a single slot).
        if (_protectChk != null && _protectChk.ButtonPressed)
        {
            def.Objective = new CombatObjectiveDef
            {
                Kind = CombatObjectiveDef.KindProtect,
                WardUnitId = "anchor_moment",
                Description = "Protect the Anchor",
            };
        }

        if (_wavesChk != null && _wavesChk.ButtonPressed)
        {
            string waveUnit = def.Enemies[0].UnitId;
            def.Waves.Add(new ReinforcementWave
            {
                Round = 3,
                Announce = "The first wave arrives.",
                Enemies = { new EnemySlot(waveUnit, diff) },
            });
            def.Waves.Add(new ReinforcementWave
            {
                Round = 5,
                Announce = "The second wave arrives.",
                Enemies = { new EnemySlot(waveUnit, diff), new EnemySlot(waveUnit, diff) },
            });
        }

        var party = new List<Companion>();
        foreach (var (chk, comp) in _allyChecks)
        {
            if (chk.ButtonPressed) party.Add(comp);
        }
        CompanionRoster.DebugPartyOverride = party.Count > 0 ? party : null;

        PlayerSession.SelectedSchool = (CardSchool)_schoolOpt.GetSelectedId();
        PlayerSession.DebugCombat = true;
        PlayerSession.DebugMode = true;
        PlayerSession.SkipDeployment = _skipDeployChk.ButtonPressed;
        PlayerSession.DebugStopOnTriggers = _stopOnTriggersChk.ButtonPressed;
        SeedDebugDeckIfEmpty((CardSchool)_schoolOpt.GetSelectedId());
        PlayerDeckSave.UseDebugDeck = true;

        // Debug vista adjacency: if the vista-border terrain differs from the map
        // terrain, pretend all six overworld neighbours are that terrain.
        var vistaTerrain = ((OverworldHex.TerrainType)_vistaOpt.GetSelectedId()).ToString();
        string[] neighborTerrains = null;
        if (vistaTerrain != terrain)
        {
            neighborTerrains = new string[6];
            for (int k = 0; k < 6; k++)
                neighborTerrains[k] = vistaTerrain;
        }

        // Battlefield injectors -> PlayerSession (read by HexGridManager). Cleared
        // on ReturnToCampus so a later real fight is unaffected.
        string mevKind = _mapEventKindOpt.GetItemText(_mapEventKindOpt.Selected);
        PlayerSession.DebugMapEventKind = mevKind == "(none)" ? null : mevKind;
        PlayerSession.DebugMapEventElement = _mapEventElemOpt.GetItemText(_mapEventElemOpt.Selected);
        PlayerSession.DebugDisableHazardCap = _noHazardCapChk.ButtonPressed;

        var debugObjs = new List<string>();
        foreach (var kvp in _mapObjectSpins)
            for (int i = 0; i < (int)kvp.Value.Value; i++)
                debugObjs.Add(kvp.Key);
        PlayerSession.DebugMapObjects = debugObjs.Count > 0 ? debugObjs : null;

        int recipeSel = _forceRecipeOpt.Selected;
        if (recipeSel > 0 && recipeSel <= BattlefieldRecipes.Length)
            def.MapRecipe = BattlefieldRecipes[recipeSel - 1];
        else if (recipeSel == BattlefieldRecipes.Length + 1 && !TryForceCompiledGate(def))
            return;   // compile failed; reason already in the status label
        else if (recipeSel == BattlefieldRecipes.Length + 2 && !TryForceCompiledGate(def, defending: true))
            return;   // compile failed; reason already in the status label
        else if (recipeSel == BattlefieldRecipes.Length + 3 && !TryForceCompiledGate(def, vectorKind: "breach"))
            return;   // compile failed; reason already in the status label
        else if (recipeSel == BattlefieldRecipes.Length + 4 && !TryForceCompiledGate(def, defending: true, vectorKind: "dock"))
            return;   // compile failed; reason already in the status label
        else if (recipeSel == BattlefieldRecipes.Length + 5 && !TryForceCompiledGate(def, defending: true, vectorKind: "portal"))
            return;   // compile failed; reason already in the status label
        else if (recipeSel == BattlefieldRecipes.Length + 6
                 && !CastleDefenseCompiler.Arm(def, terrain.ToString(), CompiledGateSeed))
        {
            _status.Text = "castle defense: compiler emitted unparseable JSON (see log).";
            return;
        }

        EncounterContextCarrier.Set(def);
        EncounterContextCarrier.SetContext(terrain, tier, neighborTerrains);

        GD.Print($"[CombatDebug] Launch: {total} enemy(ies), tier={tier}, terrain={terrain}, " +
                 $"vistaBorder={vistaTerrain}, diff={diff}, school={PlayerSession.SelectedSchool}, " +
                 $"skipDeploy={_skipDeployChk.ButtonPressed}.");

        _instance = null; // scene swap frees us
        GetTree().ChangeSceneToFile(BattlefieldScene);
    }

    // ── UI helpers ────────────────────────────────────────────────────────

    /// <summary>Dropdown of plain strings (id = index). Used for the debug map-event
    /// kind/element pickers, which don't map to a game enum.</summary>
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

    private OptionButton AddEnumDropdown(VBoxContainer form, string label, Array values, int selectedId,
        Func<object, string> labelFn = null)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(MakeLabel(label, 150));

        var opt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var v in values)
        {
            opt.AddItem(labelFn != null ? labelFn(v) : v.ToString(), Convert.ToInt32(v));
        }
        for (int i = 0; i < opt.ItemCount; i++)
        {
            if (opt.GetItemId(i) == selectedId) { opt.Selected = i; break; }
        }
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

    /// <summary>A button that jumps to one of the existing deck/card scenes. Those
    /// screens edit the persistent save, which is exactly what debug combat draws,
    /// so no separate card picker is needed. They return to campus on exit; reopen
    /// the launcher from there. ChangeSceneToFile frees this overlay with the scene.</summary>
    private Button MakeNavButton(string text, string scenePath)
    {
        var b = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        b.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(b, isPrimary: false);
        b.Pressed += () => GetTree().ChangeSceneToFile(scenePath);
        return b;
    }

    /// <summary>Opens the existing Deck Editor pointed at the DEBUG deck: seeds it from
    /// the real deck on first use, flips UseDebugDeck so the editor edits the scratch
    /// list, then navigates. Campus._Ready flips it back on return.</summary>
    private Button MakeDebugDeckEditButton()
    {
        var b = new Button { Text = "Edit Debug Deck", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        b.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(b, isPrimary: false);
        b.Pressed += () =>
        {
            SeedDebugDeckIfEmpty((CardSchool)_schoolOpt.GetSelectedId());
            PlayerDeckSave.UseDebugDeck = true;
            GetTree().ChangeSceneToFile("res://Scenes/UI/DeckEditor.tscn");
        };
        return b;
    }

    private static void SeedDebugDeckIfEmpty(CardSchool school)
    {
        // Default the scratch deck to the selected class's starter, appended to the
        // shared owned collection, never touching the real deck. No-op once it has cards.
        StarterDeckLoader.SeedDebugStarterDeck(SaveManager.ActiveSave, school);
    }

    /// <summary>Clear the scratch deck and reseed it from the currently-selected class's
    /// starter. Also drops the owned copies that belonged only to the old debug deck
    /// (never the real deck's cards) so repeated resets don't bloat the collection.</summary>
    private void ResetDebugDeck()
    {
        var save = SaveManager.ActiveSave;
        var pd = save?.PlayerDeck;
        if (pd == null)
        {
            return;
        }

        // Full reset of the scratch collection (separate from the real one), so there
        // is no accumulation across resets.
        pd.DebugCards = new List<OwnedCard>();
        pd.DebugDeckInstanceIds = new List<string>();

        // One-time cleanup: earlier builds appended debug starter copies into the REAL
        // collection, leaving orphaned starter duplicates in the stash. Drop starter
        // cards not slotted in the real deck; the real deck itself is untouched.
        var inRealDeck = new HashSet<string>(pd.RealActiveDeckInstanceIds);
        int before = pd.RealCards.Count;
        pd.RealCards.RemoveAll(c => c.IsStarter && !inRealDeck.Contains(c.InstanceId));
        int purged = before - pd.RealCards.Count;

        var school = (CardSchool)_schoolOpt.GetSelectedId();
        StarterDeckLoader.SeedDebugStarterDeck(save, school);

        if (_status != null)
        {
            _status.Text = $"Debug deck reset to {school} starter ({pd.DebugDeckInstanceIds.Count} cards)" +
                (purged > 0 ? $"; purged {purged} orphaned starter duplicate(s) from the real stash." : ".");
            _status.AddThemeColorOverride("font_color", UITheme.Success);
        }
    }

    /// <summary>Verify the deck split serializes safely: the real deck still uses the
    /// legacy "activeDeckInstanceIds" key (old saves keep their deck), the debug deck is
    /// separate, and both survive a round-trip through the real save options.</summary>
    public static bool AssertDeckSplit()
    {
        var src = new PlayerDeckSave
        {
            RealActiveDeckInstanceIds = new List<string> { "real_1", "real_2" },
            DebugDeckInstanceIds = new List<string> { "dbg_1" },
        };
        string json = System.Text.Json.JsonSerializer.Serialize(src, SaveManager.JsonOptions);
        var rt = System.Text.Json.JsonSerializer.Deserialize<PlayerDeckSave>(json, SaveManager.JsonOptions);
        bool ok = json.Contains("activeDeckInstanceIds")
                  && !json.Contains("realActiveDeckInstanceIds")
                  && json.Contains("\"cards\":") && !json.Contains("\"realCards\":")
                  && rt != null && rt.RealActiveDeckInstanceIds.Count == 2
                  && rt.DebugDeckInstanceIds.Count == 1
                  && rt.RealActiveDeckInstanceIds[0] == "real_1";
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PlayerDeckSave>(
            "{\"activeDeckInstanceIds\":[\"old_a\",\"old_b\",\"old_c\"]}", SaveManager.JsonOptions);
        bool compat = legacy != null && legacy.RealActiveDeckInstanceIds.Count == 3
                      && legacy.DebugDeckInstanceIds.Count == 0;
        GD.Print($"[DeckSplit] round-trip {(ok ? "OK" : "FAIL")}, legacy-compat {(compat ? "OK" : "FAIL")}.");
        if (!(ok && compat)) GD.PushError("[DeckSplit] Assertion FAILED. Deck save split is unsafe.");
        return ok && compat;
    }

    private static int DefaultCount(string unitId) => unitId switch
    {
        "generic_soldier" => 1,
        "generic_ranger" => 1,
        "generic_wizard" => 1,
        _ => 0,
    };
}
