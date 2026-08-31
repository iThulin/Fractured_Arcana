using Godot;
using System.Collections.Generic;

// ============================================================
// ExpeditionManager.cs
//
// Purpose:        Top-level controller for ONE bounded expedition
//                 onto the persistent world. Replaces the region-
//                 generation lifecycle of OverworldRunManager with
//                 the single-world model:
//                   DEPLOY:   build a radius-R window of WorldData
//                             around the chosen staging point.
//                   OPERATE:  move / fight / negotiate inside the
//                             window; reveal tiles, which write
//                             straight back into Cycle.World.
//                   EXTRACT:  voluntary or range-exhausted; bank
//                             discoveries + new staging points,
//                             save, return to the strategic view.
//                 The world is authoritative and resident in
//                 CycleState.World, so there is NO seed reproduction
//                 and NO fog save/restore. Combat round-trips just
//                 rebuild the same window from the same world.
// Layer:          System
// Collaborators:  WorldWindowBuilder.cs (builds the window),
//                 OverworldHexGrid.cs (WindowMode container),
//                 OverworldPartyToken / FogOfWarManager /
//                 OverworldFactionManager (unchanged interaction),
//                 EncounterRouter.cs (combat resource round-trip),
//                 PlayerSession (staging point handoff),
//                 SaveManager.ActiveSave.Cycle.World (the world)
// See:            single_world_refactor_v2.docx §4.1, §6 (lifecycle)
// ============================================================

/// <summary>Controls one expedition: deploy a window from a staging point,
/// operate inside it, extract by writing discovery back to the persistent world.</summary>
public partial class ExpeditionManager : Node2D
{
    [Export] public int WindowRadius = 12;
    // Fuel skin (Mobile Fortress §3): OperatingRange IS MaxFuel and the per-tile
    // burn IS OverworldMovementCost.StepCost. The entire step economy carries over
    // untouched, relabeled. The serialized/code names stay "steps" (§10: renaming
    // serialized fields is not worth a migration); only the UI says Fuel/Furnace.
    [Export] public int OperatingRange = 40;   // MaxFuel budget for one sortie (fuel-in-disguise)
    [Export] public int ExhaustionDamagePerStep = 10;

    // ── Fuel refueling tuning (Mobile Fortress §3.2 / §13) ───────────────
    /// <summary>Fuel restored on resting at a refuge (§14.4 APPROVED). Watch note
    /// (§3.2): cut to 3 or 0 if routes stop feeling finite. Doubled by the Druid
    /// Verdant Ark quirk in F3.</summary>
    [Export] public int RestRefuel = 5;
    /// <summary>Fuel restored the first time a supply cache is scouted/collected
    /// (§3.2). Gated to first discovery so a persistent cache can't be milked.</summary>
    [Export] public int CacheRefuel = 8;

    // ── W1: sliding window (claude/expedition_window_sliding_v1) ─────────
    /// <summary>Debug A/B lever: true restores the old fixed-perimeter window
    /// (no sliding). Off by default. The wall is gone; range is governed by
    /// the step/HP economy plus the W3 supply leash below.</summary>
    [Export] public bool HardWindowMode = false;

    /// <summary>Hexes of party drift from the window center before the loaded
    /// window slides to follow. Small enough that the loaded edge always stays
    /// far beyond vision range; large enough that pacing doesn't thrash.</summary>
    [Export] public int RecenterThreshold = 3;

    // ── W3: soft leash (the supply line) ──────────────────────────────────
    /// <summary>Hex distance from the nearest supply anchor (this expedition's
    /// staging tile, or any Available staging point, including outposts
    /// secured mid-run) within which no leash drain applies.</summary>
    [Export] public int SupplyRange = 12;

    /// <summary>Width in hexes of each leash band beyond SupplyRange.</summary>
    [Export] public int LeashBandWidth = 3;

    /// <summary>HP-pool drain per step, per band beyond supply. Deliberately
    /// NOT reducible by HazardWard/CorruptionWard (Q3). The leash is its own
    /// attrition axis; the deferred §7b Provisioner family is its future
    /// mitigation. Wards reducing it would trivialize the leash exactly the
    /// way the hard wall trivialized Pathfinder.</summary>
    [Export] public int LeashDrainPerBand = 1;

    /// <summary>Maximum leash bands (drain caps at LeashBandCap × LeashDrainPerBand).</summary>
    [Export] public int LeashBandCap = 3;

    /// <summary>Grid-local coord the loaded window is currently centered on.</summary>
    private Vector2I _windowCenterLocal = Vector2I.Zero;

    /// <summary>Supply band after the last step (0 = in supply). Lets band
    /// crossings announce themselves once instead of every step.</summary>
    private int _lastSupplyBand = 0;

    /// <summary>P5: whether the last step landed inside a shard-zone footprint.
    /// Lets the vault-sanctuary relief announce itself once on entry instead of
    /// every step within the footprint.</summary>
    private bool _lastInVault = false;

    /// <summary>Two-step confirm for emergency extraction (W3 ruling).</summary>
    private ConfirmationDialog _emergencyConfirm;

    // ── S2: overworld spellcasting (overworld_spell_system_v1_1) ─────────
    private OverworldSpellManager _spells;
    private GrimoirePanel _grimoirePanel;
    private Label _essenceLabel;

    // ── S3: Retrace memory (Chronomancer): the last committed move, so the
    // sole G1 exception can undo it. Cleared when a scene swap makes the
    // "last step" ambiguous (combat/negotiation) and after use. ─────────────
    private Vector2I _lastMoveFrom;
    private int _lastMoveStepCost;
    private bool _hasLastMove = false;

    // ── Runtime resource state (rides EncounterRouter across combat) ─────
    /// <summary>Current fuel (fuel-in-disguise; serialized name kept per §10).</summary>
    public int StepsRemaining { get; set; }
    /// <summary>Fuel tank capacity this sortie = OperatingRange + campus BonusSteps,
    /// recomputed deterministically each Deploy (so it survives combat round-trips
    /// without riding the router). Refuel clamps to this; the negotiation overrun
    /// above it is left honest, exactly as before.</summary>
    public int MaxFuel { get; set; }

    /// <summary>The active sortie's castle (Mobile Fortress §4), keyed by the founding
    /// school. Its movement signature is pushed into OverworldMovementCost; its quirks
    /// are read at the relevant sites (MaxFuel, rest refuel, corruption/weather drain,
    /// scry). Set every Deploy.</summary>
    private CastleTypeDef _castle;

    /// <summary>This sortie's crew effects (Mobile Fortress §5), computed at deploy
    /// from the active party's station assignment. Helm/Furnace/Lens are applied to
    /// fuel/MaxFuel/scry; Wardroom (ambush delay) is read by F6; Quartermaster (loot)
    /// later. Recomputed every deploy.</summary>
    private CrewEffects _crew = CrewEffects.None;
    private System.Collections.Generic.Dictionary<CrewStation, Companion> _crewAssign = new();
    public int CurrentHP { get; set; }
    public int MaxHP { get; set; }

    // Mobile Fortress §2.1: Hull IS the sortie pool (this pool never took combat
    // damage: router.DamageTaken has arrived as 0 since the K2.5 carried-HP
    // system landed; combat runs on per-companion HP, §7.3). Overworld attrition
    // drains Hull; Hull-0 is a forced RECALL (damaged, never lost), not a loss.
    // Aliases only. CurrentHP/MaxHP stay the serialized backing (§10: no rename).
    public int Hull    { get => CurrentHP; set => CurrentHP = value; }
    public int MaxHull { get => MaxHP;     set => MaxHP = value; }

    /// <summary>Casualty summary from the most recent §5b wipe roll, consumed
    /// by FailExpedition's banner so the human cost is visible at the moment
    /// of failure (K2 UX).</summary>
    private string _casualtyNote;
    public int GoldEarned { get; set; }
    public int SplinterEarned { get; set; }
    /// <summary>Build Materials gathered on this run: banked only on
    /// extraction, forfeited on failure (same stake rules as gold/splinters).
    /// No encounter grants materials yet (the gathering system is unbuilt);
    /// the channel exists so the top-bar pending readout and banking are
    /// already correct the day something does.</summary>
    public int MaterialEarned { get; set; }
    /// <summary>Supplies GAINED by negotiation deals on this run: banked only on
    /// extraction, forfeited on failure (the 2026-08-05 "all spoils are losable"
    /// ruling). Deal terms that COST supplies deduct from the treasury at once
    /// (you pledge from stores, but gains ride home with the party). See
    /// OnNegotiationReturned. Strategic cache income never touches this: it
    /// banks directly in SupplyCacheSystem.Tick.</summary>
    public int SuppliesEarned { get; set; }
    public int EncountersWon { get; set; }
    public bool ExpeditionComplete { get; private set; }

    // ── World + window ──────────────────────────────────────────────────
    private WorldData _world;
    private WorldWindowBuilder _window;
    private int _stagingCol, _stagingRow;

    /// <summary>True when this expedition is a warfront intervention (the cycle has a
    /// PendingWarfrontId). Forces siege-tier combat and enables the "break the siege"
    /// stronghold objective. Read from the cycle so it survives combat round-trips.</summary>
    private bool _isWarfront;

    /// <summary>World coord of the besieging stronghold (the warfront objective),
    /// or (-1,-1) if none. Stamped as a Combat landmark in the window and re-stamped
    /// on recenter; clearing it (winning combat on this tile) breaks the siege.</summary>
    private int _strongholdCol = -1, _strongholdRow = -1;

    /// <summary>The two provinces the active warfront is fought over. Empty on an
    /// ordinary expedition. Drives the contested-ground tint (PaintContestedGround),
    /// the answer to "which region is this war actually about", which the map
    /// could not previously show.</summary>
    private string _warfrontDefenderKid = "", _warfrontAggressorKid = "";

    /// <summary>World coord of the warfront's focus tile (the contested border hex the
    /// party deploys onto). With the stronghold, the two poles of the war zone.</summary>
    private int _warfrontFrontCol = -1, _warfrontFrontRow = -1;

    /// <summary>How far from the front or the stronghold the ground still reads as
    /// contested. Tinting whole PROVINCES was the first cut and it was wrong: a kingdom
    /// is enormous, so the whole visible map went red and the tint stopped meaning
    /// anything. The war zone is the corridor two armies actually fight over. Front and
    /// stronghold sit 2-3 apart, so 4 covers the corridor and a hex of shoulder.</summary>
    private const int WarZoneRadius = 4;

    // ── Nodes ───────────────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private FogOfWarManager _fog;

    /// <summary>Step 2 (convergence spec): the window's gameplay overlay (effective
    /// POI, consumed, objective/landmark, contested) as plain data. The authority;
    /// hex nodes mirror it through SetOverlay. Gameplay never reads hex.POI again.</summary>
    private readonly WindowOverlayModel _overlay = new();
    private OverworldPartyToken _party;
    private OverworldFactionManager _factionManager;
    private RoamerToken _roamer;
    private bool _roamerSpent;
    private Camera2D _camera;

    // ── [DEBUG] 3D expedition-window overlay (Stage-2 live wiring) ────────
    // Toggled with M in DebugMode: renders THIS run's window in 3D from the live
    // fog/overlay/world models, and its clicks drive the REAL _party.TryMoveTo, so
    // walking here charges cost, reveals fog, and triggers POIs exactly like the 2D
    // map. The viewport is parented into the HUD canvas UNDER the panels (via
    // MoveChild to index 0), so encounter panels (scout/narrative/negotiation) draw
    // OVER the 3D naturally: no auto-close guessing, no panel occlusion.
    private SubViewportContainer _window3DContainer;
    private ExpeditionWindow3D _window3D;
    private Button _view3DButton;   // persistent HUD 2D/3D view toggle (Stage 3)

    private NarrativeEncounterPanel _narrativePanel;
    private ToastManager _toasts;
    private ScoutReportPanel _scoutPanel;
    private LedgerPanel _ledgerPanel;
    private List<NarrativeEncounterData> _encounterPool;

    // ── Pending combat (scout panel) ────────────────────────────────────
    private Vector2I? _pendingCombatHexCoord = null;
    private EncounterDefinition _pendingEncounter = null;
    private string _pendingTerrain = null;
    /// <summary>Owner archmage when the pending scout-panel combat drew from an
    /// archmage's own pool ("" otherwise): dossier attribution (spec §4).</summary>
    private string _pendingCombatArchmageId = "";
    private float _scaledDifficultyMult = 1.0f;
    private bool _ambushPending = false;
    private const int PatrolRecoverySteps = 8;
    private const int PatrolShakeSteps = 5;

    // ── UI ──────────────────────────────────────────────────────────────
    private Label _stepLabel, _hpLabel, _infoLabel, _windowLabel;
    private ProgressBar _fuelGauge;   // Mobile Fortress §3.1: furnace dial
    private Label _weatherLabel;      // Mobile Fortress weather (W1): field readout
    private WeatherType _lastWeatherAtParty = WeatherType.Clear;

    /// <summary>Persistent objective line at the top of the expedition HUD. Before
    /// 2026-08-06 a warfront's objective was stated ONCE, in the deploy ShowInfo, and
    /// then scrolled away, so a player could win fights at the front all sortie and
    /// have no way to learn that none of them were the objective, or what taking it
    /// would actually buy. Refreshed every UpdateUI; hidden on ordinary expeditions.</summary>
    private Label _objectiveLabel;
    private Button _extractButton, _returnButton, _ledgerButton;
    private bool _cameraFreeMode = false;
    private const float CameraPanSpeed = 400f;

    private const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    private Label _hoverTooltip;
    private HSeparator _objectiveSeparator;

    // ── Autosave throttle ───────────────────────────────────────────────
    // The cycle file holds the whole world array (~2MB+), so per-move saves
    // stutter. Autosave at most once per interval; checkpoints save directly.
    private const double AutosaveIntervalSec = 3.0;
    private double _lastAutosaveMsec = 0;

    [Signal] public delegate void ExpeditionEndedEventHandler(bool extracted);

    // ── Accessors for EncounterRouter ───────────────────────────────────
    public Vector2I GetPartyCoord() => _party.CurrentCoord;
    public OverworldHexGrid GetGrid() => _grid;

    public override void _Ready()
    {
        EnsureEncounterRouter();
        var router = EncounterRouter.Instance;

        // ── World comes from the resident cycle ──────────────────────────
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            GD.PrintErr("ExpeditionManager: no active cycle. Cannot deploy.");
            return;
        }
        _world = cycle.World;

        // ── Staging point + radius from the deploy handoff ───────────────
        _stagingCol = PlayerSession.ExpeditionStagingCol;
        _stagingRow = PlayerSession.ExpeditionStagingRow;
        if (PlayerSession.ExpeditionWindowRadius > 0)
            WindowRadius = PlayerSession.ExpeditionWindowRadius;

        // Warfront intervention? The cycle carries the pending front id across the
        // deploy → combat → return round-trips, so this stays true for the whole run.
        _isWarfront = !string.IsNullOrEmpty(cycle.PendingWarfrontId);
        if (_isWarfront)
        {
            var awf = cycle.Warfronts?.Find(w => w.Id == cycle.PendingWarfrontId);
            if (awf != null && awf.HasStronghold)
            { _strongholdCol = awf.StrongholdCol; _strongholdRow = awf.StrongholdRow; }
            if (awf != null)
            {
                _warfrontDefenderKid = awf.DefenderKingdomId ?? "";
                _warfrontAggressorKid = awf.AggressorKingdomId ?? "";
                if (awf.HasFocus)
                { _warfrontFrontCol = awf.FocusCol; _warfrontFrontRow = awf.FocusRow; }
            }
        }

        BuildEquipmentLoadouts();

        // ── Build the window grid (WindowMode = no self-generation) ──────
        _grid = new OverworldHexGrid { Name = "WindowGrid", WindowMode = true };
        AddChild(_grid);

        _window = new WorldWindowBuilder(_world, _stagingCol, _stagingRow, WindowRadius);

        // On a combat/negotiation return the party may be far outside the base
        // disc. Build the initial window around where they'll actually be
        // placed, instead of 469 tiles at staging that the restore recenter
        // would immediately free. (Fresh deploys, and HardWindowMode, where
        // the party can never leave the base disc, build at staging.)
        bool pendingReturn = router != null && router.HasPendingReturn;
        Vector2I initialCenter = (pendingReturn && !HardWindowMode)
            ? GridLocalOf(router.SavedPartyCoord)
            : _window.PartyStartLocal;
        _window.Build(_grid, initialCenter);
        _windowCenterLocal = initialCenter;
        // Step 2: seed the overlay model from the freshly built window (hexes carry
        // the world-mapped POIs); from here on the model is the authority and the
        // stamps below write through the SetOverlay seam.
        SyncOverlayFromWindow();
        StampCivicPois(); // S4.2: settlements/seats get their map marker

        // Fog manager (child of grid, same as before)
        _fog = new FogOfWarManager { Name = "FogOfWar" };
        _grid.AddChild(_fog);
        // Step 1: seed the fog MODEL from the freshly built window. Hexes arrive
        // carrying FogFromDiscovery, and from here on the model is the authority.
        _fog.SyncFromWindow();
        // Step 2: the landmark-lure scan reads the overlay model, not node POIs.
        _fog.Overlay = _overlay;

        // Faction patrols, keyed to the staging tile's kingdom, if any.
        _factionManager = new OverworldFactionManager { Name = "FactionManager" };
        // Step 4: spawn filters + every patrol token read DATA through these,
        // the same seams the manager itself gates on. Wired BEFORE Initialize.
        _factionManager.TileQuery = local => TryTileAt(local, out var ft) ? ft : (WorldTile?)null;
        _factionManager.FogQuery = local => _fog.FogAt(local);
        _factionManager.PoiQuery = local => _overlay.OverlayAt(local).Poi;
        _grid.AddChild(_factionManager);
        // Patrols key off the TEMPLATE REGION (the campaign's archmage map is
        // keyed by region names like 'dustreach', not 'kingdom_N' ids).
        _factionManager.Initialize(_grid, StagingTemplateRegion(), cycle.Campaign);
        _factionManager.PatrolCapturedPlayer += OnPatrolCapturedPlayer;

        // Party token
        _party = new OverworldPartyToken { Name = "PartyToken" };
        // Step 4: movement legality + cost preview read the WORLD through the
        // manager's seams, the same source OnPartyMoved charges from.
        _party.TileQuery = local => TryTileAt(local, out var pt) ? pt : (WorldTile?)null;
        _party.IsBlocked = local => !_grid.Hexes.ContainsKey(local)
            || (TryTileAt(local, out var bt) && bt.IsWater);
        _grid.AddChild(_party);

        // Camera
        _camera = new Camera2D
        {
            Name = "ExpeditionCamera",
            Zoom = new Vector2(1.2f, 1.2f),
            PositionSmoothingEnabled = true,
            PositionSmoothingSpeed = 5f,
        };
        AddChild(_camera);
        _camera.CallDeferred("make_current");

        BuildHud();

        // ── Resource state ───────────────────────────────────────────────
        MaxHP = ComputePartyBaseHP();
        CurrentHP = MaxHP;
        StepsRemaining = OperatingRange;
        GoldEarned = 0;
        SplinterEarned = 0;
        MaterialEarned = 0;
        SuppliesEarned = 0;
        EncountersWon = 0;
        ExpeditionComplete = false;

        PlayerSession.ClearRunState();
        var bonuses = BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        BuildingEffectApplier.ApplyCampusEffects(SaveManager.ActiveSave);
        MaxHP += bonuses.BonusHP;
        CurrentHP = MaxHP;
        StepsRemaining += bonuses.BonusSteps;
        // §4 castle: the school's chassis. Configure its movement signature (static
        // ambient read by StepCost) and fold its MaxFuel quirk into the tank. Set
        // every deploy, stateless and deterministic from the school.
        _castle = CastleTypes.For(PlayerSession.SelectedSchool);
        OverworldMovementCost.CastleCheapTerrains = _castle.CheapTerrains;
        OverworldMovementCost.CastleTerrainDiscount = _castle.TerrainDiscount;
        OverworldMovementCost.CastleExtraRoadDiscount = _castle.ExtraRoadDiscount;
        OverworldMovementCost.CastleWaiveFord = _castle.WaiveFord;
        MaxFuel = OperatingRange + bonuses.BonusSteps + _castle.BonusMaxFuel;   // fuel tank capacity (§3.1/§4)

        // §5 crew: the active party mans the stations. Auto-assign, compute the
        // effects, and apply Helm (fuel burn), Furnace (MaxFuel) now; Lens (scry)
        // folds into VisionModifiers below; Wardroom/Quartermaster are stored on
        // _crew for F6 / the loot pass. Recomputed every deploy from the roster.
        _crewAssign = CrewStations.AutoAssign(ActivePartyCompanions());
        _crew = CrewStations.Compute(_crewAssign);
        OverworldMovementCost.CrewFuelMultiplier = _crew.FuelBurnMultiplier;
        MaxFuel += _crew.BonusMaxFuel;

        GoldEarned += bonuses.BonusGold;

        PlayerSession.IsOnExpedition = true;
        if (PlayerSession.DebugMode && PlayerSession.StartWithGold)
            GoldEarned += 5000;
        if (PlayerSession.DebugMode && PlayerSession.StartWithSplinters)
            SplinterEarned += 5000;

        // ── Place party / restore from combat ────────────────────────────
        // Guard on ReturnSceneOverride too: a campus-pending return must never
        // be mis-consumed as an expedition return (Step 9 hardening).
        if (router != null && router.HasPendingReturn &&
            string.IsNullOrEmpty(router.ReturnSceneOverride))
        {
            RestoreFromCombat(router);
        }
        else
        {
            // K2.5: fresh expedition: everyone starts whole. (Combat returns
            // take the other branch and must NOT reset carried HP.)
            CompanionInjurySystem.ResetExpeditionHP(SaveManager.ActiveSave);
            PlayerSession.WizardExpeditionHP = -1; // K2.5 symmetry, wizard too

            // S4 (Identify) + S5 (True Names): pinned encounters are
            // expedition-scoped. Static so they survive combat round-trips
            // (the OverworldSpellEffects pattern); cleared here and on
            // every expedition-end path.
            _identifiedEncounters.Clear();
            _pinnedNegotiations.Clear();

            // Run journal: opens run_<id>.log/.csv under user://run_logs/.
            // ONLY on a fresh deploy. Combat/negotiation returns take the
            // other branch and keep appending to the same run's files.
            RunEventLog.Begin(StagingTemplateRegion(),
                PlayerSession.SelectedSchool.ToString(),
                GoldEarned, SplinterEarned, CurrentHP, MaxHP, StepsRemaining);
            // §4 castle: name it in the log, and seed the Chronomancer flat-move
            // counter (fresh deploy only, so it survives combat round-trips).
            LogRun("castle", $"{_castle.Name}: {_castle.Quirk}");
            PlayerSession.ChronoFlatMovesLeft = _castle.ChronoFlatMoves;
            // §5 crew: log the station assignment + the effects it yields.
            LogRun("crew", $"{CrewSummary()} → " +
                   $"burn ×{_crew.FuelBurnMultiplier:0.00}, +{_crew.BonusMaxFuel} fuel, +{_crew.BonusScry} scry");
            if (bonuses.BonusGold != 0 || bonuses.BonusHP != 0 || bonuses.BonusSteps != 0)
                LogRun("campus_bonus",
                    $"buildings: +{bonuses.BonusGold}g +{bonuses.BonusHP}maxHP +{bonuses.BonusSteps}fuel");
            if (PlayerSession.DebugMode && (PlayerSession.StartWithGold || PlayerSession.StartWithSplinters))
                LogRun("debug_grant",
                    $"{(PlayerSession.StartWithGold ? "+5000g " : "")}{(PlayerSession.StartWithSplinters ? "+5000sp" : "")}".Trim());

            _party.Initialize(_grid, _fog, _window.PartyStartLocal);
            // Reveal-on-deploy: the staging tile and its vision write to World.
            WriteVisibleToWorld();
            // On a warfront the objective banner states all of this permanently and
            // with more precision (side, stakes, distance), so a second line here
            // just said the same thing twice, two inches apart.
            if (!_isWarfront)
                ShowInfo("The castle strides out. Explore the region; recall before the fuel runs out.");

            if (PlayerSession.DebugMode && PlayerSession.NoFog)
                RevealAllFog();
        }

        // ── Mobile Fortress weather field (W1) ───────────────────────────
        // Bind the field to this window every deploy (so a combat-return
        // re-points the terrain sampler at the new instance while keeping the
        // fronts it left with); reseed fronts only on a fresh deploy. Season is
        // the lunation mod 4. Party is placed by both branches above, so the
        // readout baseline reads a valid tile.
        bool weatherFresh = !(router != null && router.HasPendingReturn &&
                              string.IsNullOrEmpty(router.ReturnSceneOverride));
        int wSeason = (SaveManager.ActiveSave?.Cycle?.Calendar?.CurrentLunation ?? 0) % 4;
        WeatherSystem.Configure(_grid.Hexes.Keys, TerrainAt, wSeason);
        if (weatherFresh)
        {
            ulong wSeed = ((ulong)GD.Randi() << 32) ^ GD.Randi();
            WeatherSystem.Seed(wSeed);
            LogRun("weather_seed", WeatherSummary());
        }
        _lastWeatherAtParty = WeatherSystem.WeatherAt(_party.CurrentCoord);
        VisionModifiers.ScryBonus = WeatherCatalog.Def(_lastWeatherAtParty).ScryDelta
                                    + (_castle?.BonusScry ?? 0) + _crew.BonusScry;   // W2 + §4 Arcanist + §5 Lens

        // ── S2: overworld spellcasting (manager + Grimoire panel) ────────
        // Fresh deploys reset the Essence pool / cast counts / beacons;
        // combat and negotiation returns keep them (they ride the save).
        _spells = new OverworldSpellManager { Name = "SpellManager" };
        // Step 4b: spell rules read the same seams the manager gates on; fog
        // writes (attunement senses) go through the model, so a later
        // UpdateVision can't stomp a sense's silhouette.
        _spells.TileQuery = local => TryTileAt(local, out var st) ? st : (WorldTile?)null;
        _spells.FogQuery = local => _fog.FogAt(local);
        _spells.OverlayQuery = local => _overlay.OverlayAt(local);
        _spells.FogWrite = (local, state) => _fog.SetFog(local, state);
        _spells.StrideLockQuery = () => _striding;   // §3.4: seal the Grimoire mid-stride
        AddChild(_spells);
        _spells.Initialize(this, _grid, cycle.Grimoire, freshDeploy: !pendingReturn);
        _spells.ApplyAttunement(_party.CurrentCoord);
        WriteVisibleToWorld(); // attunement silhouettes chart immediately

        _grimoirePanel = new GrimoirePanel { Name = "GrimoirePanel" };
        GetHudCanvas().AddChild(_grimoirePanel);
        _grimoirePanel.Initialize(_spells);
        _uiHoverBlockers.Add(_grimoirePanel); // S4.2: no tile hover through the Grimoire

        // Narrative panel + pool (keyed to the staging kingdom)
        _narrativePanel = new NarrativeEncounterPanel { Visible = false };
        GetHudCanvas().AddChild(_narrativePanel);

        _toasts = new ToastManager { Name = "QuestToasts" };
        GetHudCanvas().AddChild(_toasts);
        _uiHoverBlockers.Add(_narrativePanel);

        // Favor ledger panel (C3): read-only ledger + the call-in action.
        _ledgerPanel = new LedgerPanel { Name = "LedgerPanel" };
        GetHudCanvas().AddChild(_ledgerPanel);
        _uiHoverBlockers.Add(_ledgerPanel);
        _ledgerPanel.GetIneligibilityReason = CallInIneligibility;
        _ledgerPanel.OnCallIn = OnLedgerCallIn;
        _encounterPool = NarrativeEncounterLoader.LoadForRegion(StagingTemplateRegion());

        // Wire signals
        _grid.HexClicked += OnHexClicked;
        _grid.HexHovered += OnHexHovered;
        _grid.HexUnhovered += OnHexUnhovered;
        _party.PartyMoved += OnPartyMoved;
        _party.PartyArrived += OnPartyArrived;

        SpawnRoamer();
        StampStronghold(); // warfront objective: place + reveal the besieging stronghold
        PaintContestedGround();   // and show WHICH ground the war is over
        ApplyScryingReveals(bonuses);   // Scrying Chambers run-start intel (scrying_chambers_spec_v1)

        CenterCamera();
        UpdateUI();

        // Stage 3: honour the player's view preference. Launch straight into the 3D
        // expedition view when it's set. The flag persists across deploys and combat
        // returns (static PlayerSession scratchpad), so once you switch to 3D every
        // subsequent run comes up in 3D until you switch back. The 2D map still runs
        // underneath; the overlay renders it and drives the real move logic.
        UpdateView3DButton();
        if (PlayerSession.ExpeditionView3D && _window3D == null)
            OpenWindow3D();
    }

    // ════════════════════════════════════════════════════════════════════
    // Discovery write-back: the heart of the single-world model
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Push every currently-revealed window tile into Cycle.World as
    /// Explored, and mark any revealed POIs discovered. Called after each move
    /// (and on deploy). Cheap: only flips tiles that changed. Marks the save
    /// dirty so the periodic SaveIfDirty flush persists it.</summary>
    private void WriteVisibleToWorld()
    {
        bool changed = false;

        // STEP 1 (convergence spec): iterate the FOG MODEL, not the scene nodes.
        // The persistent world's Discovery now derives from plain data. The last
        // render-state scrape in the write-back path is gone. Same entries, same
        // ratchet: the model mirrors the loaded window 1:1.
        foreach (var kvp in _fog.Model.All)
        {
            var local = kvp.Key;
            var fog = kvp.Value;

            // P3: seeing any footprint tile (charted or revealed) discovers the
            // whole shard sub-region. The vault layout then reads at distance.
            if ((fog == OverworldHex.FogState.Silhouette ||
                 fog == OverworldHex.FogState.Revealed) &&
                _window.TryLocalToWorld(local, out int zc, out int zr))
            {
                var sz = _world.ShardZoneAt(zc, zr);
                if (sz != null && !sz.Discovered)
                {
                    RevealShardZone(sz);
                    changed = true;
                }
            }

            // W4 (§5 keystone extension): silhouette = terrain-only knowledge =
            // Charted. As the sliding window travels, its vision fringe leaves a
            // persistent Charted corridor on the strategic map. The route
            // itself becomes a legible artifact of the expedition.
            if (fog == OverworldHex.FogState.Silhouette)
            {
                if (_window.TryLocalToWorld(local, out int scol, out int srow) &&
                    _world.TryIndex(scol, srow, out int sidx) &&
                    _world.Tiles[sidx].Discovery == TileDiscovery.Unseen)
                {
                    _world.Tiles[sidx].Discovery = TileDiscovery.Charted;
                    changed = true;
                }
                continue;
            }

            if (fog != OverworldHex.FogState.Revealed)
                continue;
            if (!_window.TryLocalToWorld(local, out int col, out int row))
                continue;

            // Tile discovery → Explored.
            if (_world.TryIndex(col, row, out int idx))
            {
                if (_world.Tiles[idx].Discovery != TileDiscovery.Explored)
                {
                    _world.Tiles[idx].Discovery = TileDiscovery.Explored;
                    changed = true;
                }
            }

            // POI discovery → discovered (shows on the strategic map).
            var poi = _world.PoiAt(col, row);
            if (poi != null && !poi.Discovered)
            {
                poi.Discovered = true;
                changed = true;

                // Settlements grant staging the moment they're DISCOVERED: a
                // friendly hub, no fight needed. (Outposts/seats still grant on
                // being secured, via OnPartyArrived/GrantStagingPointAt.)
                if (poi.Kind == PoiKind.Settlement && poi.GrantsStaging)
                    GrantStagingPointAt(local);
            }
        }

        if (changed)
            SaveManager.MarkDirty();
    }

    /// <summary>P3: the first sighting of any footprint tile opens the whole vault
    /// layout: every footprint tile charts (reduced fog) and any loaded, still-
    /// hidden footprint hex silhouettes immediately. Interaction + collection are
    /// later phases; this is discovery only.</summary>
    private void RevealShardZone(ShardZone z)
    {
        z.Discovered = true;
        foreach (var (x, y) in z.Tiles)
        {
            if (_world.TryIndex(x, y, out int idx) &&
                _world.Tiles[idx].Discovery == TileDiscovery.Unseen)
                _world.Tiles[idx].Discovery = TileDiscovery.Charted;

            var local = _window.LocalOf(x, y);
            // Step 1: through the fog seam (model + mirror). SetFog no-ops on
            // unloaded coords, same as the old TryGetValue guard.
            if (_fog != null && _fog.FogAt(local) == OverworldHex.FogState.Hidden)
                _fog.SetFog(local, OverworldHex.FogState.Silhouette);
        }
        ShowInfo($"You have found {z.Name}. A shard of the Arcanum lies within its depths.");
    }

    // ── Step 3: the tile query seam ──────────────────────────────────────

    /// <summary>THE tile query: window-local coord → the WORLD's tile. Step 3 of
    /// the convergence spec: terrain/water/edge questions are answered by WorldData
    /// (which SpellForcePath et al. already treat as the terrain authority), never
    /// by render nodes. False off-world; callers that need "is loaded" semantics
    /// keep an explicit _grid.Hexes.ContainsKey guard alongside.</summary>
    private bool TryTileAt(Vector2I local, out WorldTile tile)
    {
        tile = default;
        if (_window == null || _world == null)
            return false;
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return false;
        if (!_world.TryIndex(col, row, out int idx))
            return false;
        tile = _world.Tiles[idx];
        return true;
    }

    /// <summary>Terrain at a window-local coord from the world; Grassland (the
    /// neutral cost row) off-world. Callers that must distinguish guard first.</summary>
    private OverworldHex.TerrainType TerrainAt(Vector2I local)
        => TryTileAt(local, out var t) ? t.Terrain : OverworldHex.TerrainType.Grassland;

    // ── Step 2: the overlay seam ─────────────────────────────────────────

    /// <summary>Write a tile's overlay: model first, node mirror + redraw second.
    /// No-op for unloaded coords, matching every pre-Step-2 write pattern, all of
    /// which guarded on Hexes.TryGetValue. (Persistent POI truth still goes through
    /// ConsumeWorldPoi / WorldPoi.Discovered, unchanged.)</summary>
    private void SetOverlay(Vector2I coord, in TileOverlay o)
    {
        if (_grid == null || !_grid.Hexes.TryGetValue(coord, out var hex))
            return;
        _overlay.Set(coord, o);
        hex.POI = o.Poi;
        hex.POIConsumed = o.Consumed;
        hex.IsObjective = o.Objective;
        hex.IsLandmark = o.Landmark;
        hex.Contested = o.Contested;
        hex.RefreshVisuals();
    }

    /// <summary>Mark a tile's window POI consumed (model + mirror). The companion
    /// world-side write stays the callers' ConsumeWorldPoi, as before.</summary>
    private void ConsumeOverlayPoi(Vector2I coord)
    {
        var o = _overlay.OverlayAt(coord);
        o.Consumed = true;
        SetOverlay(coord, o);
    }

    /// <summary>Re-mirror the overlay model to the loaded window (the Step-1
    /// SyncFromWindow pattern). Called after window Build and every StreamTo slide,
    /// BEFORE the stamps re-run (they then write through the seam). Node→model is
    /// lossless because every mid-run write goes through SetOverlay.</summary>
    private void SyncOverlayFromWindow()
    {
        _overlay.Clear();
        foreach (var kvp in _grid.Hexes)
            _overlay.Set(kvp.Key, new TileOverlay
            {
                Poi = kvp.Value.POI,
                Consumed = kvp.Value.POIConsumed,
                Objective = kvp.Value.IsObjective,
                Landmark = kvp.Value.IsLandmark,
                Contested = kvp.Value.Contested,
            });
    }

    /// <summary>Flush a dirty save at most once per AutosaveIntervalSec. Keeps the
    /// large cycle file from being written every move. Real checkpoints (combat
    /// entry, outpost secured, extract) bypass this and save directly.</summary>
    private void ThrottledAutosave()
    {
        double now = Time.GetTicksMsec();
        if (now - _lastAutosaveMsec < AutosaveIntervalSec * 1000.0)
            return;
        _lastAutosaveMsec = now;
        SaveManager.SaveIfDirty();
    }

    /// <summary>Mark a world POI consumed (resolved) so it isn't re-offered.</summary>
    private void ConsumeWorldPoi(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return;
        var poi = _world.PoiAt(col, row);
        if (poi != null && !poi.Consumed)
        {
            poi.Consumed = true;
            SaveManager.MarkDirty();
        }
    }

/// <summary>Securing a staging-granting POI adds a new launch point to the
    /// world. Called when such a POI is resolved.</summary>
    /// <summary>Influence granted to the host kingdom when a site is secured
    /// (the strategic Reach-lens ratchet).</summary>
    private const int StagingInfluenceGain = 20;

    private void GrantStagingPointAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return;
        GrantStagingPointAtWorld(col, row);
    }

    /// <summary>World-coordinate core of the staging grant, so remote reveals
    /// (Spymaster chart packets, court intelligence) can grant staging for
    /// settlements discovered outside the current window.</summary>
    private void GrantStagingPointAtWorld(int col, int row)
    {
        var poi = _world.PoiAt(col, row);
        if (poi == null || !poi.GrantsStaging)
            return;

        // Already a staging point? Skip.
        foreach (var sp in _world.StagingPoints)
            if (sp.X == col && sp.Y == row)
                return;

        var questBefore = QuestNotifier.Snapshot(SaveManager.ActiveSave);

        string name = poi.Kind switch
        {
            PoiKind.Outpost => "Outpost",
            PoiKind.Settlement => "Settlement",
            PoiKind.Seat => "Secured Seat",
            _ => "Staging Point",
        };
        _world.StagingPoints.Add(new StagingPoint
        {
            X = col,
            Y = row,
            Name = name,
            Source = "Secured",
            Available = true,
        });
        if (_world.TryIndex(col, row, out int idx))
        {
            _world.Tiles[idx].IsStagingPoint = true;

            // Reach ratchet: securing a site grows guild influence over the host
            // kingdom, so the strategic Reach lens changes because you played.
            string kid = _world.Tiles[idx].KingdomId;
            var kingdoms = SaveManager.ActiveSave?.Cycle?.Kingdoms;
            if (!string.IsNullOrEmpty(kid) && kingdoms != null &&
                kingdoms.TryGetValue(kid, out var ks))
                ks.PlayerInfluence = Mathf.Min(100, ks.PlayerInfluence + StagingInfluenceGain);
        }

        SaveManager.MarkDirty();
        RunEventLog.Event("staging_point", $"{name} secured", 0, 0, 0, 0,
                          GoldEarned, SplinterEarned, CurrentHP, StepsRemaining, $"{col},{row}");
        ShowInfo($"New staging point secured: {name}. Future expeditions can launch from here.");
        foreach (var qt in QuestNotifier.NotifyNew(questBefore, SaveManager.ActiveSave))
            _toasts?.Push(qt.Text, qt.Kind);
    }

    // ════════════════════════════════════════════════════════════════════
    // Debug / dev-mode helpers
    // ════════════════════════════════════════════════════════════════════

    public override void _UnhandledInput(InputEvent @event)
    {
        // ── View toggle (Stage 3): a REAL, non-debug feature, so it's handled
        //    BEFORE the debug gate. M flips this run between the 2D map and the
        //    3D expedition view. Esc is deliberately NOT handled here (it is
        //    reserved for the pause menu), so use M (or the "Switch to 2D"
        //    button) to leave the 3D view.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.M } && !ExpeditionComplete)
        {
            OnView3DTogglePressed();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!PlayerSession.DebugMode || ExpeditionComplete)
            return;

        // F: mint test favors for the kingdom under the party (C3 testing).
        if (@event is InputEventKey { Pressed: true, Keycode: Key.F })
        {
            string kid = KingdomIdAt(_party.CurrentCoord);
            if (string.IsNullOrEmpty(kid))
            {
                ShowInfo("[DEBUG] No kingdom here, so no test favors can be minted.");
            }
            else
            {
                CouncilLedger.DebugMintTestFavors(SaveManager.ActiveSave.Cycle, kid);
                SaveManager.SaveIfDirty();
                ShowInfo($"[DEBUG] Test favors minted for '{kid}'.");
                _ledgerPanel?.RefreshRows();
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        // E: dump echoes in flight (C4 verification).
        if (@event is InputEventKey { Pressed: true, Keycode: Key.E })
        {
            CouncilDebug.DumpEchoes();
            ShowInfo("[DEBUG] Echo flight dumped to Output.");
            GetViewport().SetInputAsHandled();
            return;
        }

        // R: dump court Regard for the kingdom underfoot (all courts in wilds).
        if (@event is InputEventKey { Pressed: true, Keycode: Key.R })
        {
            string rkid = KingdomIdAt(_party.CurrentCoord);
            CouncilDebug.DumpRegard(string.IsNullOrEmpty(rkid) ? null : rkid);
            ShowInfo("[DEBUG] Court Regard dumped to Output.");
            GetViewport().SetInputAsHandled();
            return;
        }

        // C: paint world corruption on the party tile + its six neighbours
        //    (Session C setup). C = 30 (minor band), Shift+C = 60 (major band),
        //    Ctrl+C = 0 (clear).
        if (@event is InputEventKey { Pressed: true, Keycode: Key.C } cKey)
        {
            byte value = cKey.CtrlPressed ? (byte)0 : (cKey.ShiftPressed ? (byte)60 : (byte)30);
            int painted = DebugPaintCorruption(_party.CurrentCoord, value);
            ShowInfo(painted > 0
                ? $"[DEBUG] Painted corruption {value} on {painted} tile(s)."
                : "[DEBUG] Could not paint corruption here.");
            GetViewport().SetInputAsHandled();
            return;
        }

        // N: [DEBUG] summon the narrative-chain proof rig without walking the map.
        //    Cycles lost_traveler -> sealed_letter_delivery -> grateful_courier on
        //    repeat presses. Shift+N clears the chain's flags + completed ids so the
        //    ungated "before" state can be re-tested. Bypasses POI scarcity/patrols but
        //    runs the REAL resolve path, so flags actually set and gates react live.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.N } nKey)
        {
            if (nKey.ShiftPressed) DebugResetNarrativeChain();
            else DebugSummonNextChainEncounter();
            GetViewport().SetInputAsHandled();
            return;
        }

        // K: [DEBUG] force the roaming-caravan opportunity (living-map test).
        if (@event is InputEventKey { Pressed: true, Keycode: Key.K })
        {
            TriggerRoamerEncounter();
            GetViewport().SetInputAsHandled();
            return;
        }

        // V: [DEBUG] teleport to the nearest unfinished shard vault (gate, or its
        // sanctum once the guardian is felled) and trigger arrival (P4 testing).
        if (@event is InputEventKey { Pressed: true, Keycode: Key.V })
        {
            DebugTeleportToVault();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!PlayerSession.DebugGrantStagingArmed)
            return;
        if (@event is InputEventKey { Pressed: true, Keycode: Key.G })
        {
            DebugGrantStagingHere();
            GetViewport().SetInputAsHandled();
        }
    }

    // ── 3D expedition-window view (Stage 3) ─────────────────────────────
    //    A full-screen 3D render of THIS run's window, built from the live
    //    fog/overlay/world models; clicking an adjacent tile calls the REAL
    //    _party.TryMoveTo, so a walk here charges cost, reveals fog, and fires POIs
    //    exactly as the 2D map does. The decoupled models render AND drive the run
    //    in 3D. The 2D map keeps running underneath (the overlay sits at HUD index 0,
    //    under every panel), so encounters resolve normally over it. Toggled by the
    //    persistent HUD button (or M / Esc); the choice is remembered in
    //    PlayerSession.ExpeditionView3D so the next deploy launches into the same view.

    /// <summary>The single entry point for flipping the view: toggle the overlay,
    /// persist the choice as the session preference, and refresh the button label.
    /// The HUD button, the M key, and Esc all route through here so all three stay
    /// in lockstep with the actual overlay state.</summary>
    private void OnView3DTogglePressed()
    {
        ToggleWindow3D();
        PlayerSession.ExpeditionView3D = _window3D != null;
        UpdateView3DButton();
    }

    /// <summary>Keep the persistent HUD toggle's label honest about what a press does.</summary>
    private void UpdateView3DButton()
    {
        if (_view3DButton != null)
            _view3DButton.Text = _window3D != null ? "Switch to 2D" : "Switch to 3D";
    }

    private void ToggleWindow3D()
    {
        if (_window3D != null) CloseWindow3D();
        else OpenWindow3D();
    }

    private void OpenWindow3D()
    {
        if (_grid == null || _party == null || _window == null || _world == null
            || GetHudCanvas() == null)
            return;

        // Parent the 3D view into the HUD canvas, then MoveChild to index 0 so it sits
        // UNDER every existing + future HUD panel: encounter panels draw over it, no
        // occlusion, no auto-close needed. Full-rect + MouseFilter.Stop, so it blocks
        // the 2D map's Area2D picking underneath while open.
        _window3DContainer = new SubViewportContainer { Stretch = true, Name = "Window3DView" };
        _window3DContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        GetHudCanvas().AddChild(_window3DContainer);
        GetHudCanvas().MoveChild(_window3DContainer, 0);

        var vp = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X };
        _window3DContainer.AddChild(vp);

        // Instance the ExpeditionWindow3D SCENE (not `new`) so its Inspector-tuned
        // export values (chamber, table, companions, camera) drive the live view;
        // the same scene is F6-previewable. Force run-mode flags after instancing.
        // Falls back to a code-built node if the scene can't be loaded.
        var winScene = GD.Load<PackedScene>("res://Scenes/Overworld/ExpeditionWindow3D.tscn");
        _window3D = winScene != null ? winScene.Instantiate<ExpeditionWindow3D>() : new ExpeditionWindow3D();
        _window3D.Standalone = false;
        _window3D.SelfDrive = false;
        _window3D.MoveRequested += OnWindow3DMove;
        _window3D.TileHovered += OnWindow3DHover;
        _window3D.TileUnhovered += OnWindow3DUnhover;
        vp.AddChild(_window3D);
        _window3D.AcceptInput = true;
        _window3DContainer.MouseEntered += () => { if (_window3D != null) _window3D.AcceptInput = true; };
        _window3DContainer.MouseExited += () => { if (_window3D != null) _window3D.AcceptInput = false; };

        // Refresh after each real move: PartyMoved (fog+pos), PartyArrived (POI state).
        _party.PartyMoved += OnWindow3DPartyMoved;
        _party.PartyArrived += OnWindow3DPartyArrived;

        FeedWindow3D(frameCamera: true);
        UpdateView3DButton();
        ShowInfo("3D expedition view. Click an adjacent tile to walk. \"Switch to 2D\" or M returns.");
    }

    private void CloseWindow3D()
    {
        if (_party != null)
        {
            _party.PartyMoved -= OnWindow3DPartyMoved;
            _party.PartyArrived -= OnWindow3DPartyArrived;
        }
        _window3DContainer?.QueueFree();
        _window3DContainer = null;
        _window3D = null;
        UpdateView3DButton();
    }

    private void OnWindow3DPartyMoved(Vector2I n, Vector2I o)
    {
        if (_window3D != null) FeedWindow3D(frameCamera: false);
    }

    private void OnWindow3DPartyArrived(Vector2I c)
    {
        // No auto-close: panels draw OVER the 3D (it's under them in the HUD canvas),
        // so encounters resolve normally while the 3D view keeps tracking the run.
        if (_window3D != null) FeedWindow3D(frameCamera: false);
    }

    /// <summary>Project the live grid-LOCAL fog/overlay models into WORLD-offset-keyed
    /// copies (the space <see cref="ExpeditionWindow3D"/> renders in) and hand them to
    /// the view. Cheap dictionary copies over the loaded window (~500 tiles).</summary>
    private void FeedWindow3D(bool frameCamera)
    {
        if (_window3D == null || _grid == null || _window == null || _world == null)
            return;

        var fogW = new ExpeditionFogModel();
        var ovW = new WindowOverlayModel();
        foreach (var kvp in _grid.Hexes)
        {
            if (!_window.TryLocalToWorld(kvp.Key, out int wc, out int wr))
                continue;
            var wcoord = new Vector2I(wc, wr);
            fogW.Set(wcoord, _fog.FogAt(kvp.Key));
            ovW.Set(wcoord, _overlay.OverlayAt(kvp.Key));
        }

        Vector2I worldCenter = _window.TryLocalToWorld(_windowCenterLocal, out int cc, out int cr)
            ? new Vector2I(cc, cr) : Vector2I.Zero;
        Vector2I worldParty = _window.TryLocalToWorld(_party.CurrentCoord, out int pc, out int pr)
            ? new Vector2I(pc, pr) : worldCenter;

        _window3D.SetWindow(_world, fogW, ovW, worldCenter, worldParty, frameCamera);

        // Moving entities so ambushers are visible in 3D like the 2D tokens: enemy patrols (red)
        // and the roamer (amber). Patrol/roamer coords are grid-local → world-offset for the view.
        var entities = new List<(Vector2I, Color)>();
        if (_factionManager != null)
            foreach (var pLocal in _factionManager.GetPatrolPositions())
                if (_window.TryLocalToWorld(pLocal, out int px, out int py))
                    entities.Add((new Vector2I(px, py), UITheme.Danger));
        if (_roamer != null && !_roamerSpent
            && _window.TryLocalToWorld(_roamer.CurrentCoord, out int rx, out int ry))
            entities.Add((new Vector2I(rx, ry), UITheme.POINarrative));
        _window3D.SetEntities(entities);
    }

    /// <summary>A 3D-overlay click: translate the world coord back to grid-local and run
    /// the REAL move. TryMoveTo enforces adjacency/water and no-ops if illegal, so a
    /// coordinate mismatch is harmless. It simply doesn't move.</summary>
    private void OnWindow3DMove(Vector2I worldCoord)
    {
        // A click mid-stride is the one order accepted while marching: cancel (§3.4).
        if (_striding) { CancelStride(); return; }

        var local = _window.LocalOf(worldCoord.X, worldCoord.Y);
        if (_party == null) return;

        // Adjacent → a single ordinary step. Distant → a stride order (§3.4).
        if (_grid.GetNeighbors(_party.CurrentCoord).Contains(local))
            _party.TryMoveTo(local);
        else
            BeginStride(local);
    }

    /// <summary>3D-view hover → drive the same tile tooltip the 2D grid drives (world→local, then
    /// the existing OnHexHovered/OnHexUnhovered path), AND preview the stride path to that tile.</summary>
    private void OnWindow3DHover(Vector2I worldCoord)
    {
        var local = _window.LocalOf(worldCoord.X, worldCoord.Y);
        OnHexHovered(local);
        ShowStridePreview(local);
    }

    private void OnWindow3DUnhover()
    {
        if (_hoveredCoord.HasValue)
            OnHexUnhovered(_hoveredCoord.Value);
        _window3D?.ClearStridePath();
    }

    // ── Stride orders (§3.4): plan + preview (F8a) ───────────────────────────
    /// <summary>POI path-weight penalty (§3.4): a stride routes AROUND known
    /// encounters rather than into them; the goal tile is exempt so a POI can be
    /// ordered as a destination normally.</summary>
    private const int StridePoiPenalty = 6;
    private Vector2I _strideGoal;

    // Stride execution (F8b) + exploratory march (F8d)
    private bool _striding;
    private bool _strideHasMoved;   // don't halt on the tile we STARTED on (e.g. a staging outpost)
    private int _strideConsecutive; // uninterrupted stride steps taken; drives momentum (§3.4)
    private Vector2I _strideLastTile;  // the tile we came from (blind march no-backtrack)
    private int _strideBestDist;       // best hex distance to the goal achieved so far
    private int _strideStuck;          // blind steps without improving _strideBestDist
    private Button _haltButton;
    private const float StrideStepSeconds = 0.25f;   // pacing per tile (watchable)

    /// <summary>An ENCOUNTER POI stops a stride; a benign anchor/service (outpost,
    /// seat, settlement, rest, cache) does not: the castle may deploy on or stride
    /// past those without the march refusing to start.</summary>
    private static bool IsEncounterPoi(OverworldHex.POIType p)
        => p == OverworldHex.POIType.Combat || p == OverworldHex.POIType.Narrative
        || p == OverworldHex.POIType.Negotiation || p == OverworldHex.POIType.Prison
        || p == OverworldHex.POIType.Objective;

    /// <summary>Can a stride traverse or stop on this local tile? In the loaded
    /// window, not water, and not Hidden fog (the lens cannot command unscried
    /// ground). Silhouette IS orderable (planned at a pessimistic flat cost).</summary>
    private bool StrideOrderable(Vector2I local)
        => _grid != null && _grid.Hexes.ContainsKey(local)
           && !(TryTileAt(local, out var t) && t.IsWater)
           && _fog.FogAt(local) != OverworldHex.FogState.Hidden;

    /// <summary>Fuel to step from `from` into `to` for the planner AND the estimate.
    /// This is the SAME cost the live move charges (G1), plus the Silhouette
    /// pessimistic cost and the POI routing penalty. Weather surcharge rides inside
    /// StepCost.</summary>
    private int StrideEdgeCost(Vector2I from, Vector2I to)
    {
        int cost;
        if (_fog.FogAt(to) == OverworldHex.FogState.Silhouette)
        {
            cost = 2; // terrain unknown under a silhouette, so plan pessimistically
        }
        else
        {
            var terr = TerrainAt(to);
            WorldTile? fromTile = TryTileAt(from, out var ft) ? ft : (WorldTile?)null;
            cost = OverworldMovementCost.StepCost(terr, fromTile, from, to,
                       EquipmentLoadout.PartyPathfinder(terr.ToString()));
        }
        var ov = _overlay.OverlayAt(to);
        if (to != _strideGoal && ov.Poi != OverworldHex.POIType.None && !ov.Consumed)
            cost += StridePoiPenalty;
        return cost;
    }

    /// <summary>Plan the stride path from the castle to a local goal, or null if the
    /// goal is unscried/water/unreachable. Path excludes the current tile, ends on
    /// the goal.</summary>
    private System.Collections.Generic.List<Vector2I> PlanStride(Vector2I goalLocal)
    {
        if (_party == null || _grid == null)
            return null;
        _strideGoal = goalLocal;
        return StridePlanner.Plan(
            _party.CurrentCoord, goalLocal,
            local => _grid.GetNeighbors(local),
            StrideOrderable,
            StrideEdgeCost,
            local => _grid.Distance(local, goalLocal));
    }

    /// <summary>Hover preview: draw the ribbon + total-fuel estimate to the hovered
    /// tile. Clears when the tile is the castle's own or unreachable.</summary>
    private void ShowStridePreview(Vector2I goalLocal)
    {
        if (_window3D == null)
            return;
        if (_striding)   // no hover previews while the castle is already marching
        { _window3D.ClearStridePath(); return; }
        if (_party == null || goalLocal == _party.CurrentCoord)
        { _window3D.ClearStridePath(); return; }
        if (!_grid.Hexes.ContainsKey(goalLocal) || (TryTileAt(goalLocal, out var gt) && gt.IsWater))
        { _window3D.ClearStridePath(); return; }

        var path = PlanStride(goalLocal);
        if (path != null && path.Count > 0)
        {
            // Charted route: solid ribbon + total-fuel estimate.
            var worldPath = new System.Collections.Generic.List<Vector2I>(path.Count);
            foreach (var l in path)
                if (_window.TryLocalToWorld(l, out int wx, out int wy))
                    worldPath.Add(new Vector2I(wx, wy));
            int fuel = StridePlanner.FuelEstimate(_party.CurrentCoord, path, StrideEdgeCost);
            _window3D.ShowStridePath(worldPath, fuel, exploratory: false);
            return;
        }

        // Fog goal: no charted route, so show the bearing into the unknown (a dashed
        // line from the castle to the target). The march will path there blind.
        var seg = new System.Collections.Generic.List<Vector2I>();
        if (_window.TryLocalToWorld(_party.CurrentCoord, out int sx, out int sy)) seg.Add(new Vector2I(sx, sy));
        if (_window.TryLocalToWorld(goalLocal, out int gx, out int gy)) seg.Add(new Vector2I(gx, gy));
        if (seg.Count == 2)
            _window3D.ShowStridePath(seg, -1, exploratory: true);
        else
            _window3D.ClearStridePath();
    }

    // ── Stride orders (§3.4): execution (F8b) + exploratory march (F8d) ──────
    /// <summary>Begin a march to a distant tile, KNOWN or in the fog. The castle
    /// paths across scried ground toward it, then presses on blind toward the
    /// bearing (revealing as it goes), marching one tile at a time through the REAL
    /// per-step move (fuel/Hull/vision/patrols/ambush all fire per step), paced
    /// ~0.25 s and haltable. The next tile is chosen fresh each step, so newly
    /// revealed ground re-routes the march automatically.</summary>
    private void BeginStride(Vector2I goalLocal)
    {
        if (_striding || ExpeditionComplete || _party == null)
            return;
        if (goalLocal == _party.CurrentCoord)
            return;
        // Orderable as a destination: in the loaded window and not open water. Fog
        // IS allowed. That is the exploratory march (spec §3.4 revisited): you can
        // command the fortress toward the unknown, not only across charted ground.
        if (!_grid.Hexes.ContainsKey(goalLocal) || (TryTileAt(goalLocal, out var gt) && gt.IsWater))
        { ShowInfo("The castle cannot march there."); return; }

        _strideGoal = goalLocal;
        _striding = true;
        _strideHasMoved = false;
        _strideConsecutive = 0;
        _strideLastTile = new Vector2I(int.MinValue, int.MinValue);
        _strideBestDist = _grid.Distance(_party.CurrentCoord, goalLocal);
        _strideStuck = 0;
        _window3D?.ClearStridePath();
        SetHaltButton(true);
        _grimoirePanel?.Refresh();   // §3.4: grey the sealed Grimoire
        StrideStep();   // first step now; subsequent steps self-schedule
    }

    /// <summary>One beat of the march: wait for the pawn to finish its last hop,
    /// run the halt checks, choose the next tile (known-ground routing, else a blind
    /// step toward the bearing), commit it, and schedule the following.</summary>
    private void StrideStep()
    {
        if (!_striding)
            return;

        // Interruptions from the world (combat launched, ambush, run ended).
        if (ExpeditionComplete || _ambushPending)
        { EndStride(null); return; }

        // Wait for the previous hop's animation to land before issuing the next.
        if (_party.IsMoving)
        { ScheduleStrideTick(0.05f); return; }

        // Arrived at the ordered tile.
        if (_party.CurrentCoord == _strideGoal)
        { EndStride("Arrived."); return; }

        // An ENCOUNTER opened on a tile we ARRIVED on (scout report, narrative,
        // negotiation, …) stops the march so the player decides. Only checked once
        // the castle has actually moved, so deploying on a staging outpost (a benign
        // POI) does not refuse the first step.
        if (_strideHasMoved)
        {
            var ovHere = _overlay.OverlayAt(_party.CurrentCoord);
            if (IsEncounterPoi(ovHere.Poi) && !ovHere.Consumed)
            { EndStride("The castle halts as something ahead demands your attention."); return; }
        }

        // Safety halt: don't grind the Hull down to nothing on a long order.
        if (MaxHull > 0 && Hull <= Mathf.CeilToInt(MaxHull * 0.25f))
        { EndStride("The castle halts to spare its Hull."); return; }

        // Choose the next tile: known-ground routing first, else a blind step toward
        // the bearing (the exploratory march). Dead-end / lost-in-fog halts here.
        if (!TryNextStrideTile(out var next))
        { EndStride("The castle can find no way onward."); return; }

        // Fuel gate: a stride never spends Hull to press on. It halts when the
        // furnace cannot cover the next tile (§3.4). Momentum discounts the gate
        // identically to the charge so the two never disagree.
        int nextCost = StrideEdgeCost(_party.CurrentCoord, next);
        if (_strideConsecutive >= 3)
            nextCost = Mathf.Max(1, nextCost - 1);
        if (StepsRemaining < nextCost)
        { EndStride("The castle halts, out of fuel."); return; }

        // A KNOWN encounter on the next tile (not the goal) stops the march before
        // the castle walks into it. (Fog tiles are unknown, and that is the risk.)
        var ovNext = _overlay.OverlayAt(next);
        if (next != _strideGoal && IsEncounterPoi(ovNext.Poi) && !ovNext.Consumed)
        { EndStride("The castle halts as the way ahead is no longer clear."); return; }

        var from = _party.CurrentCoord;
        if (!_party.TryMoveTo(next))
        { EndStride("The castle halts; the way is blocked."); return; }
        _strideLastTile = from;
        _strideHasMoved = true;
        _strideConsecutive++;   // this step is now behind us; momentum builds toward step 4

        // Bound a blind march that wanders: if several steps pass without getting
        // any closer than our best-yet distance, the bearing is lost. Halt.
        int d = _grid.Distance(_party.CurrentCoord, _strideGoal);
        if (d < _strideBestDist) { _strideBestDist = d; _strideStuck = 0; }
        else if (++_strideStuck > 5) { EndStride("The castle loses the bearing in the fog."); return; }

        ScheduleStrideTick(StrideStepSeconds);
    }

    /// <summary>Pick the next tile of the march. First tries known-ground A* to the
    /// goal (routes around charted POIs/hazards); if the goal is in fog / unreachable
    /// by charted ground, takes a blind step toward the bearing: the passable
    /// neighbour that most reduces hex distance to the goal, not backtracking. Falls
    /// back to any passable neighbour (incl. backtrack) before giving up.</summary>
    private bool TryNextStrideTile(out Vector2I next)
    {
        next = default;

        var path = PlanStride(_strideGoal);
        if (path != null && path.Count > 0)
        { next = path[0]; return true; }

        int bestDist = int.MaxValue; bool found = false; Vector2I best = default;
        foreach (var nb in _grid.GetNeighbors(_party.CurrentCoord))
        {
            if (!BlindStridePassable(nb, allowBacktrack: false)) continue;
            int d = _grid.Distance(nb, _strideGoal);
            if (d < bestDist) { bestDist = d; best = nb; found = true; }
        }
        if (!found)
            foreach (var nb in _grid.GetNeighbors(_party.CurrentCoord))
            {
                if (!BlindStridePassable(nb, allowBacktrack: true)) continue;
                int d = _grid.Distance(nb, _strideGoal);
                if (d < bestDist) { bestDist = d; best = nb; found = true; }
            }

        next = best;
        return found;
    }

    /// <summary>A tile the blind march may step onto: loaded, not water, and (unless
    /// allowed) not the tile we just came from. Fog is fine, and that is the point.</summary>
    private bool BlindStridePassable(Vector2I nb, bool allowBacktrack)
        => _grid.Hexes.ContainsKey(nb)
           && !(TryTileAt(nb, out var t) && t.IsWater)
           && (allowBacktrack || nb != _strideLastTile);

    private void ScheduleStrideTick(float delay)
    {
        if (!_striding)
            return;
        GetTree().CreateTimer(delay).Timeout += StrideStep;
    }

    /// <summary>Player cancelled the march (Halt button or a map click).</summary>
    private void CancelStride() => EndStride("The castle holds.");

    private void EndStride(string note)
    {
        if (!_striding)
            return;
        _striding = false;
        _strideConsecutive = 0;
        SetHaltButton(false);
        _window3D?.ClearStridePath();
        _grimoirePanel?.Refresh();   // §3.4: the Grimoire unseals on halt
        if (!string.IsNullOrEmpty(note) && !ExpeditionComplete)
            ShowInfo(note);
    }

    private void SetHaltButton(bool show)
    {
        if (_haltButton != null)
            _haltButton.Visible = show;
    }

    // ── [DEBUG] Narrative-chain proof rig (2026-07-18) ───────────────────
    //    Verifies the encounter gate-wiring from the keyboard when POIs are too
    //    scarce to reach on foot. Active only in DebugMode (via _UnhandledInput).
    private static readonly string[] _debugChainIds =
        { "lost_traveler", "sealed_letter_delivery", "grateful_courier",
          "armory_cache", "wilds_companion", "free_charter_envoy", "vault_inscription",
          "assembled_wayside", "primal_seek", "primal_trial", "primal_recover",
          "axiom_seek", "axiom_trial", "axiom_recover", "moment_seek", "moment_trial", "moment_recover",
          "binding_seek", "binding_trial", "binding_recover", "schema_seek", "schema_trial", "schema_recover",
          "deathless_seek", "deathless_trial", "deathless_recover",
          "axiom_discovery", "moment_discovery", "binding_discovery", "schema_discovery", "deathless_discovery" };
    private int _debugChainIdx;

    /// <summary>[DEBUG] Summon the next chain encounter directly: ignores
    /// terrain/completed filters, but shows it with the REAL gating context and
    /// resolves through the REAL OnNarrativeCompleted so flags actually set.</summary>
    private void DebugSummonNextChainEncounter()
    {
        if (_encounterPool == null || _encounterPool.Count == 0)
        { ShowInfo("[DEBUG] Encounter pool is empty."); return; }

        string id = _debugChainIds[_debugChainIdx % _debugChainIds.Length];
        _debugChainIdx++;

        NarrativeEncounterData enc = null;
        foreach (var e in _encounterPool)
            if (e.Id == id) { enc = e; break; }
        if (enc == null)
        { ShowInfo($"[DEBUG] Encounter '{id}' not found in pool."); return; }

        var save = SaveManager.ActiveSave;
        System.Func<string, bool> hasFlag = null;
        if (save != null) hasFlag = save.HasFlag;
        var dbgTerrain = OverworldHex.TerrainType.Grassland;
        if (_party != null && _grid != null && _grid.Hexes.ContainsKey(_party.CurrentCoord))
            dbgTerrain = TerrainAt(_party.CurrentCoord);   // Step 3: world read
        var shownDbg = EncounterAssembler.ForDisplay(enc, dbgTerrain, StagingTemplateRegion());
        _narrativePanel.ShowEncounter(shownDbg, hasFlag, save?.Cycle?.SelectedSchool, GoldEarned,
            save?.Cycle?.Campaign);
        _narrativePanel.OnCompleted =
            (choice) => OnNarrativeCompleted(enc, choice, dbgTerrain);

        ShowInfo($"[DEBUG] Summoned '{id}'. Press N for the next link, Shift+N to reset.");
    }

    /// <summary>[DEBUG] Clear the letter-chain flags and one-shot completed ids so
    /// the ungated "before" state can be tested again.</summary>
    private void DebugResetNarrativeChain()
    {
        var save = SaveManager.ActiveSave;
        if (save == null) { ShowInfo("[DEBUG] No active save."); return; }

        foreach (var f in new[] { "carrying_sealed_letter", "helped_traveler", "letter_delivered" })
            save.WorldFlags.Remove(f);
        foreach (var id in _debugChainIds)
            save.CompletedEvents.Remove(id);

        // Undo the Tranche 2 demo grants so the reward verbs can re-fire cleanly.
        var demoC = save.Companions.Find(c => c.Id == "bram_thistlewade");
        if (demoC != null) demoC.IsRecruited = false;
        save.FactionReputation.Remove("free_charter");

        // Fragment arcs: clear ALL permanent milestones, quest stamps, and
        // discovery/recovery lore so every arc re-runs from scratch.
        if (save.Ledger != null)
        {
            save.Ledger.MetaNarrativeFlags.RemoveAll(f =>
                f.EndsWith("_rumor") || f.EndsWith("_location_known") ||
                f.EndsWith("_trial_passed") ||
                (f.StartsWith("fragment_") && f.EndsWith("_collected")) ||
                // ProgressionSweep's "already paid" stamps must clear with the
                // milestones they were paid for, or a re-collected fragment
                // awards nothing while MaxCarry silently drops.
                f.StartsWith("prog_paid_"));
            save.Ledger.CompletedQuestIds.RemoveAll(id => id.StartsWith("q_"));
        }
        save.UnlockedLoreEntries.RemoveAll(l =>
            l.EndsWith("_rumor_lore") || l.EndsWith("_recovered_lore") ||
            l == "sunken_concord_fate" || l == "the_primal_shard_recovered");

        _debugChainIdx = 0;
        SaveManager.MarkDirty();
        ShowInfo("[DEBUG] Narrative chain + Tranche 2 demos reset.");
    }

    private void DebugGrantStagingHere()
    {
        var local = _party.CurrentCoord;
        if (!_window.TryLocalToWorld(local, out int col, out int row))
        {
            ShowInfo("[DEBUG] Can't resolve current tile to world.");
            return;
        }
        foreach (var sp in _world.StagingPoints)
            if (sp.X == col && sp.Y == row)
            { ShowInfo("[DEBUG] Already a staging point here."); return; }

        _world.StagingPoints.Add(new StagingPoint
        {
            X = col,
            Y = row,
            Name = "Debug Staging",
            Source = "Debug",
            Available = true,
        });
        if (_world.TryIndex(col, row, out int idx))
            _world.Tiles[idx].IsStagingPoint = true;

        string kid = _world.GetTile(col, row).KingdomId ?? "";
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        ShowInfo($"[DEBUG] Staging granted at ({col},{row}), kingdom '{kid}'.");
        GD.Print($"[DEBUG] Granted staging at ({col},{row}), kingdom '{kid}'.");
    }

    /// <summary>Debug: set world corruption on the party's tile and its six
    /// neighbours (skipping water). Writes the same field EmitCombatDeed and
    /// CorruptionDrainAt read. Note: CorruptionSpread's flood only raises, so
    /// a Ctrl+C clear may be re-raised toward the kingdom's territory level at
    /// the next boundary. Returns the number of tiles painted.</summary>
    private int DebugPaintCorruption(Vector2I local, byte value)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return 0;

        int painted = 0;
        void Paint(int x, int y)
        {
            if (!_world.TryIndex(x, y, out int idx))
                return;
            if (_world.Tiles[idx].IsWater)
                return;
            _world.Tiles[idx].Corruption = value;
            painted++;
        }

        Paint(col, row);
        foreach (var (nx, ny) in HexCoord.Neighbors(col, row, _world.Width, _world.Height))
            Paint(nx, ny);

        if (painted > 0)
            SaveManager.MarkDirty();
        GD.Print($"[DEBUG] Corruption {value} painted on {painted} tile(s) around world ({col},{row}).");
        return painted;
    }

    // ════════════════════════════════════════════════════════════════════
    // Movement / POI handlers (lifted from OverworldRunManager, de-objectived)
    // ════════════════════════════════════════════════════════════════════

private void OnPartyMoved(Vector2I newCoord, Vector2I oldCoord)
    {
        // Border-cross feedback: name the territory being entered. Fired
        // first so hazard/corruption warnings on the same step overwrite it:
        // damage outranks geography.
        string fromKingdom = KingdomIdAt(oldCoord);
        string toKingdom = KingdomIdAt(newCoord);
        if (toKingdom != fromKingdom)
        {
            ShowInfo(string.IsNullOrEmpty(toKingdom)
                ? "You cross into unclaimed wilds."
                : $"You cross into the territory of {KingdomDisplayName(toKingdom)}.");
        }

        int stepCost = 1, hpDrain = 0;
        bool roadTravel = false;
        // Step 3: terrain and edge masks come from the WORLD tile, not the render
        // node. Loaded-guard kept (a move only lands on loaded ground anyway).
        bool destKnown = false;
        var destTerrain = OverworldHex.TerrainType.Grassland;
        if (_grid.Hexes.ContainsKey(newCoord) && TryTileAt(newCoord, out var destTile))
        {
            destKnown = true;
            destTerrain = destTile.Terrain;
            hpDrain = GetTerrainHPDrain(destTerrain);
            // Q3 (§4b): HazardWard reduces terrain drain, floored at 1 whenever the
            // terrain drains at all: relief is bought, immunity does not exist.
            if (hpDrain > 0)
                hpDrain = Mathf.Max(1, hpDrain - EquipmentLoadout.PartyHazardWard());
            // Edge-aware step cost: destination terrain, cheapened by a road on the
            // traveled edge, surcharged by an unbridged river ford. Read the shared
            // edge off the tile we're leaving (masks live on both sides). Q3 (§7b):
            // Pathfinder cheapens the matching terrain (floor 1 inside StepCost).
            WorldTile? fromTile = TryTileAt(oldCoord, out var ft) ? ft : (WorldTile?)null;
            stepCost = OverworldMovementCost.StepCost(destTerrain, fromTile, oldCoord, newCoord,
                EquipmentLoadout.PartyPathfinder(destTerrain.ToString()));

            // S4.2 (user ruling 2026-07-16): a step traveled ALONG A ROAD is
            // safe going (see the drain sites below). Edge roads are the real
            // network; the vestigial Road TERRAIN tile counts too (old maps).
            roadTravel = OverworldMovementCost.EdgeHasRoad(fromTile, oldCoord, newCoord) ||
                         destTerrain == OverworldHex.TerrainType.Road;
        }

        // P5: inside a shard-zone footprint the party is in a contained designed
        // arena, not open wilderness. The three wilderness tolls (terrain,
        // corruption, supply leash) are suppressed below; step cost and
        // out-of-range exhaustion still apply. The lethal cost is the APPROACH to
        // the gate and the guardian fight, not attrition between gate and sanctum.
        bool inVault = InsideShardZone(newCoord);
        if (inVault && !_lastInVault)
            ShowInfo("Within the vault's bounds, the wilds' toll lifts: no terrain, corruption, or supply drain here.");
        _lastInVault = inVault;

        // §4 Chronomancer flat + §3.4 momentum. These are temporally disjoint (the
        // flat covers the sortie's first 3 moves; momentum begins at stride step 4),
        // so the flat takes priority and momentum only applies when the flat did not:
        // "the cheaper of the two, never both" (§3.4). The flat OVERRIDES terrain +
        // edge to a flat burn; momentum shaves 1 off the finalised cost.
        if (PlayerSession.ChronoFlatMovesLeft > 0)
        {
            stepCost = Mathf.Max(1, _castle?.ChronoFlatCost ?? 1);
            PlayerSession.ChronoFlatMovesLeft--;
        }
        else if (_striding && _strideConsecutive >= 3)
        {
            stepCost = Mathf.Max(1, stepCost - 1);
        }

        // S3 (Retrace): remember this move so it can be undone. Records the
        // cost actually charged (0 on the exhaustion path; HP is not refunded).
        _lastMoveFrom = oldCoord;
        _lastMoveStepCost = (!(PlayerSession.DebugMode && PlayerSession.UnlimitedSteps) &&
                             StepsRemaining > 0) ? Mathf.Min(StepsRemaining, stepCost) : 0;
        _hasLastMove = true;

        if (!(PlayerSession.DebugMode && PlayerSession.UnlimitedSteps))
        {
            if (StepsRemaining > 0)
            {
                int stepsCharged = Mathf.Min(StepsRemaining, stepCost);
                StepsRemaining = Mathf.Max(0, StepsRemaining - stepCost);
                LogRun("step", destKnown ? destTerrain.ToString() : "?",
                       stepsDelta: -stepsCharged, at: newCoord);
            }
            else
            {
                // Fuel spent: striding on with a dry furnace grinds the Hull.
                // Hull-0 is a forced RECALL, not a loss (§2.1: damaged never lost).
                Hull -= ExhaustionDamagePerStep;
                LogRun("exhaustion", "stride beyond fuel",
                       hpDelta: -ExhaustionDamagePerStep, at: newCoord);
                if (Hull <= 0)
                { Hull = 0; EmergencyExtract("The furnace runs dry and the hull gives out, forcing a recall."); return; }
            }

            // S4.2 (user ruling): the causeway spares you the terrain's bite:
            // a road step never pays hazard drain. (Corruption is NOT road-
            // exempt below: the creep eats roads too, and corridor-immunity
            // through corrupted ground would gut the G4 pressure.)
            if (hpDrain > 0 && inVault)
            {
                GD.Print($"[Expedition] Within the vault: {hpDrain} terrain drain suppressed.");
                hpDrain = 0;
            }
            if (hpDrain > 0 && roadTravel)
            {
                GD.Print($"[Expedition] The road spares you {hpDrain} terrain drain.");
                hpDrain = 0;
            }

            // S2: an active warding spell (Ember Ward) negates the terrain's
            // bite entirely: bounded window, not immunity (G4).
            if (hpDrain > 0 && OverworldSpellEffects.DrainSuppressed(destTerrain))
            {
                GD.Print($"[Spellcraft] Ward negates {hpDrain} terrain drain on {destTerrain}.");
                hpDrain = 0;
            }

            if (hpDrain > 0)
            {
                Hull -= hpDrain;
                LogRun("terrain_drain", destKnown ? destTerrain.ToString() : "?",
                       hpDelta: -hpDrain, at: newCoord);
                ShowInfo($"Hazardous terrain! The castle takes {hpDrain} Hull damage.");
                if (Hull <= 0)
                { Hull = 0; EmergencyExtract("The wilds batter the castle to breaking, forcing a recall."); return; }
            }

            // Corruption attrition: crossing corrupted ground bleeds you. Light at
            // the creeping edge, heavy in the convergence core, so the spreading
            // corruption is a hostile zone to route around, not stroll through.
            int corruptionDrain = CorruptionDrainAt(newCoord);
            if (corruptionDrain > 0 && inVault)
            {
                GD.Print($"[Expedition] Within the vault: {corruptionDrain} corruption drain suppressed.");
                corruptionDrain = 0;
            }
            // S2: Purifying Rite suppresses corruption attrition for its
            // window: bounded relief, never immunity (G4).
            if (corruptionDrain > 0 && OverworldSpellEffects.CorruptionSuppressed())
            {
                GD.Print($"[Spellcraft] Purifying Rite holds: {corruptionDrain} corruption drain suppressed.");
                corruptionDrain = 0;
            }
            if (corruptionDrain > 0)
            {
                // Q3 (§4b): CorruptionWard reduces the bleed, but Σ ward is CAPPED
                // at (tile corruption tier × 2) and drain never drops below 1;
                // deep stacking is pointless past the tier you're actually walking.
                int tier = CorruptionTierAt(newCoord);
                int ward = Mathf.Min(EquipmentLoadout.PartyCorruptionWard(), tier * 2);
                corruptionDrain = Mathf.Max(1, corruptionDrain - ward);
                // §4 Necromancer (Ossuary Ambulant): corruption Hull drain halved.
                if (_castle != null && _castle.CorruptionDrainMultiplier != 1f)
                    corruptionDrain = Mathf.Max(1, Mathf.RoundToInt(corruptionDrain * _castle.CorruptionDrainMultiplier));
                Hull -= corruptionDrain;
                LogRun("corruption_drain", $"tier {tier}",
                       hpDelta: -corruptionDrain, at: newCoord);
                ShowInfo($"The corruption sears the castle! {corruptionDrain} Hull lost.");
                if (Hull <= 0)
                { Hull = 0; EmergencyExtract("Corruption eats through the hull, forcing a recall."); return; }
            }

            // W3: the soft leash. Past supply range of the nearest anchor, each
            // step bleeds the pool: +1 HP per band of LeashBandWidth hexes,
            // capped. NOT ward-reducible (see the export's doc comment); the
            // supply line is priced in pool HP the wards can't buy back.
            int band = inVault ? 0 : SupplyBandAt(newCoord);
            if (band != _lastSupplyBand)
            {
                if (band > 0 && _lastSupplyBand == 0)
                    ShowInfo("You pass beyond your supply line. Each step out here drains the party.");
                else if (band == 0 && _lastSupplyBand > 0)
                    ShowInfo("You are back within your supply line.");
                _lastSupplyBand = band;
            }
            if (band > 0)
            {
                // S4.2 (user ruling): the road bears your supply: steps taken
                // along a road edge pay no leash drain, however far out. Leave
                // the road and the line snaps taut again. Early-game relief
                // for the lone wizard; the wilds stay priced.
                if (roadTravel)
                {
                    ShowInfo("The road bears your supply, so the going stays safe while you follow it.");
                }
                else
                {
                    int leashDrain = band * LeashDrainPerBand;
                    Hull -= leashDrain;
                    LogRun("leash_drain", $"band {band}",
                           hpDelta: -leashDrain, at: newCoord);
                    ShowInfo($"Beyond your supply line ({(band > 1 ? $"band {band}" : "the fringe")}). The castle strains and loses {leashDrain} Hull.");
                    if (Hull <= 0)
                    { Hull = 0; EmergencyExtract("Cut off beyond the supply line, the castle is forced to limp home."); return; }
                }
            }

            // Mobile Fortress weather (W2): the front over this tile grinds the
            // Hull, stacking on terrain/corruption. Suppressed inside a vault
            // sanctuary (like the other tolls). Cinderhold is immune; Storm
            // Anchors will halve it (F5). Hull-0 is a forced recall, not a loss.
            var wDef = WeatherCatalog.Def(WeatherSystem.WeatherAt(newCoord));
            int weatherHull = wDef.IsWeatherHullDrain ? wDef.HullPerTile : 0;
            if (weatherHull > 0 && inVault)
                weatherHull = 0;
            if (weatherHull > 0 && _castle != null && _castle.WeatherHullImmune)
                weatherHull = 0; // §4 Cinderhold (Elementalist): immune to weather Hull drain
            if (weatherHull > 0)
            {
                Hull -= weatherHull;
                LogRun("weather_drain", wDef.Name, hpDelta: -weatherHull, at: newCoord);
                ShowInfo($"{wDef.Name} batters the castle. {weatherHull} Hull lost.");
                if (Hull <= 0)
                { Hull = 0; EmergencyExtract($"{wDef.Name} breaks the hull, forcing a recall."); return; }
            }
        }

        // W1: slide the loaded window to follow the party once it drifts far
        // enough from the current center. Fires at move START (this handler),
        // so tiles stream in while the token animates across the hex.
        if (!HardWindowMode &&
            _grid.Distance(_party.CurrentCoord, _windowCenterLocal) >= RecenterThreshold)
            RecenterWindow(_party.CurrentCoord);

        // Mobile Fortress weather (W1): fronts drift one wind-step per committed
        // stride, then announce a change in the weather standing over the castle.
        // (Effects on fuel/Hull/scry/combat arrive in W2+; W1 is the field only.)
        WeatherSystem.Advect();
        var wNow = WeatherSystem.WeatherAt(_party.CurrentCoord);
        // W2 weather scry penalty + §4 Arcanist + §5 crew Lens Room, summed into the
        // shared modifier both reveal paths read.
        VisionModifiers.ScryBonus = WeatherCatalog.Def(wNow).ScryDelta + (_castle?.BonusScry ?? 0) + _crew.BonusScry;
        if (wNow != _lastWeatherAtParty)
        {
            _lastWeatherAtParty = wNow;
            var wd = WeatherCatalog.Def(wNow);
            LogRun("weather", wd.Name, at: newCoord);
            ShowInfo(wNow == WeatherType.Clear
                ? "The skies clear over the castle."
                : $"{wd.Name} closes over the castle.");
        }

        // S2: spell-effect windows tick per committed step; Arcane Ground
        // feeds the pool (+1, §5, a terrain property); the school Attunement
        // re-applies around the new position BEFORE the discovery write so
        // its silhouettes chart in the same pass.
        OverworldSpellEffects.TickStep();
        if (_spells != null)
        {
            if (destKnown && destTerrain == OverworldHex.TerrainType.ArcaneGround)
                _spells.AddEssence(1, "Arcane Ground");
            _spells.ApplyAttunement(_party.CurrentCoord);
        }

        // Reveal-on-move writes straight into World.
        WriteVisibleToWorld();

        // Patrols tick once per step.
        if (_factionManager != null && !ExpeditionComplete)
            _factionManager.Tick(_party.CurrentCoord);

        // Living map: the roaming caravan wanders once per step and offers a
        // one-time opportunity when it crosses the party's path.
        if (_roamer != null && !_roamerSpent && !ExpeditionComplete &&
            GodotObject.IsInstanceValid(_roamer))
        {
            bool contact = _roamer.IsOnSameHex(_party.CurrentCoord);
            if (!contact) { _roamer.Tick(); contact = _roamer.IsOnSameHex(_party.CurrentCoord); }
            if (contact) TriggerRoamerEncounter();
        }

        // Durability flush: THROTTLED. The cycle file is large (the whole world
        // array), so saving every move stutters. Autosave at most once every few
        // seconds; real checkpoints (combat entry, outpost, extract) save directly.
        ThrottledAutosave();

        // Range warning + auto-extract offer.
        if (StepsRemaining == 0 && !ExpeditionComplete)
            ShowInfo("Fuel spent. Recall now, or press on at the cost of HP.");

        CenterCamera();
        UpdateUI();
    }

    private void OnHexClicked(Vector2I axial)
    {
        if (ExpeditionComplete)
            return;
        // S2: an active spell-targeting session consumes grid clicks first.
        if (_spells != null && _spells.HandleHexClicked(axial))
            return;
        _party.TryMoveTo(axial);
    }
    private Vector2I? _hoveredCoord = null;

    private void OnHexHovered(Vector2I axial)
    {
        _hoveredCoord = axial;
        if (_hoverTooltip == null || !_grid.Hexes.TryGetValue(axial, out var hex))
            return;

        // Fog gate: don't reveal terrain the player hasn't explored.
        // Step 1: gate reads the fog MODEL, not the render node.
        var fogHere = _fog.FogAt(axial);
        if (fogHere != OverworldHex.FogState.Revealed)
        {
            _hoverTooltip.Text = fogHere == OverworldHex.FogState.Silhouette
                ? "Charted, unexplored" + (_spells?.TooltipSilhouetteExtra(axial, hex) ?? "")
                : "Unexplored";
        }
        else
        {
            string line = TerrainDisplayName(TerrainAt(axial));   // Step 3: world read
            // Step 2: POI gate + label read the overlay model, not the node.
            var ovTip = _overlay.OverlayAt(axial);
            if (ovTip.Poi != OverworldHex.POIType.None && !ovTip.Consumed)
                line += $"  ·  {PoiSignal.Label(ovTip.Poi, TerrainAt(axial), axial)}{_spells?.TooltipPoiExtra(axial, hex) ?? ""}" +
                        NegotiationPreread(axial); // S5: True Names
            // City tile: name the settlement whose greyed footprint this tile belongs to, so the
            // player can tell they're standing in a city (not just on its terrain).
            if (_window.TryLocalToWorld(axial, out int ccol, out int crow))
            {
                var cityTip = _world.SettlementAt(ccol, crow);
                if (cityTip != null && cityTip.Tier == SettlementTier.City)
                    line += $"  ·  {CitySettlementName(cityTip)}";
            }
            // Corruption readout if the underlying world tile is corrupted.
            if (_window.TryLocalToWorld(axial, out int col, out int row) &&
                _world.TryIndex(col, row, out int idx) && _world.Tiles[idx].Corruption >= 20)
                line += $"  ·  corrupted ({_world.Tiles[idx].Corruption})";
            _hoverTooltip.Text = line;
        }

        _hoverTooltip.Visible = true;
        PositionTooltip();
    }

    private void OnHexUnhovered(Vector2I axial)
    {
        // Only clear if we're leaving the tile we're actually showing (enter/exit
        // can interleave as the mouse crosses a shared edge).
        if (_hoveredCoord == axial)
        {
            _hoveredCoord = null;
            if (_hoverTooltip != null)
                _hoverTooltip.Visible = false;
        }
    }

    // ── S4.2: tile hover must yield to UI ────────────────────────────────

    /// <summary>UI surfaces the tile tooltip must never print through.
    /// Registered at build time; rect-tested as a fallback for surfaces
    /// whose mouse filter is Ignore (labels, label-only panels).</summary>
    private readonly List<Control> _uiHoverBlockers = new();

    /// <summary>True when the mouse is over any HUD element: the Godot
    /// hovered-control query first (honors mouse filters: buttons, panels,
    /// the Grimoire), then rect tests for Ignore-filtered surfaces, then
    /// the global top bar strip.</summary>
    private bool MouseIsOverUi()
    {
        var hovered = GetViewport()?.GuiGetHoveredControl();
        if (hovered != null && hovered.IsVisibleInTree())
            return true;

        var mouse = GetViewport().GetMousePosition();
        if (mouse.Y <= HudManager.BarHeight) // the global top bar strip
            return true;
        foreach (var c in _uiHoverBlockers)
            if (c != null && GodotObject.IsInstanceValid(c) && c.IsVisibleInTree() &&
                c.GetGlobalRect().HasPoint(mouse))
                return true;
        return false;
    }

    private void PositionTooltip()
    {
        if (_hoverTooltip == null || _grid == null)
            return;

        // S4.2 (user request): never show the tile readout through UI. The
        // Grimoire, the stat panel, buttons, and the top bar all take
        // precedence. Runs every frame, so entering/leaving UI just works.
        if (MouseIsOverUi())
        {
            _hoverTooltip.Visible = false;
            return;
        }

        // Resolve the tile under the cursor from the mouse position, every frame.
        // (Area2D MouseEntered/Exited is unreliable here; InputEvent gives no exit
        // event, so we poll, which also fixes "tooltip won't hide off-grid".)
        Vector2 mouseWorld = _grid.GetGlobalMousePosition();
        Vector2I axial = _grid.WorldToAxial(_grid.ToLocal(mouseWorld));

        if (!_grid.Hexes.TryGetValue(axial, out var hex))
        {
            _hoverTooltip.Visible = false;
            return;
        }

        // Fog gate: don't reveal terrain the player hasn't explored.
        // Step 1: gate reads the fog MODEL, not the render node.
        var fogPolled = _fog.FogAt(axial);
        if (fogPolled != OverworldHex.FogState.Revealed)
        {
            _hoverTooltip.Text = fogPolled == OverworldHex.FogState.Silhouette
                ? "Charted, unexplored" + (_spells?.TooltipSilhouetteExtra(axial, hex) ?? "")
                : "Unexplored";
        }
        else
        {
            string line = TerrainDisplayName(TerrainAt(axial));   // Step 3: world read
            // Step 2: POI gate + label read the overlay model, not the node.
            var ovTip = _overlay.OverlayAt(axial);
            if (ovTip.Poi != OverworldHex.POIType.None && !ovTip.Consumed)
                line += $"  ·  {PoiSignal.Label(ovTip.Poi, TerrainAt(axial), axial)}{_spells?.TooltipPoiExtra(axial, hex) ?? ""}" +
                        NegotiationPreread(axial); // S5: True Names
            if (_window.TryLocalToWorld(axial, out int col, out int row) &&
                _world.TryIndex(col, row, out int idx) && _world.Tiles[idx].Corruption >= 20)
                line += $"  ·  corrupted ({_world.Tiles[idx].Corruption})";
            _hoverTooltip.Text = line;
        }

        _hoverTooltip.Visible = true;
        _hoverTooltip.Position = _hudCanvas.GetViewport().GetMousePosition() + new Vector2(16, 12);
    }

    private void OnPartyArrived(Vector2I coord)
    {
        if (ExpeditionComplete || _ambushPending)
            return;
        if (!_grid.Hexes.ContainsKey(coord))
            return;

        // S3 (Deploy Waystation): standing on a deployed waystation consumes
        // its one rest charge: quarter-heal + 3 Essence, then it breaks down
        // (marker removed; it stops being a supply anchor).
        if (_window.TryLocalToWorld(coord, out int wcol, out int wrow))
        {
            var grimWs = SaveManager.ActiveSave?.Cycle?.Grimoire;
            string wsMark = $"{wcol},{wrow}";
            if (grimWs != null && grimWs.ActiveWaystations.Remove(wsMark))
            {
                // Ruling (turnaround-only Hull repair): a waystation restores
                // Essence, not Hull. Hull mends only at the between-sortie dock.
                _spells?.AddEssence(3, "Waystation");
                _grid.GetNodeOrNull($"WaystationMarker_{wcol}_{wrow}")?.QueueFree();
                SaveManager.MarkDirty();
                ShowInfo("The waystation serves its purpose and breaks down. Essence restored.");
                UpdateUI();
            }
        }

        // P4: shard sub-region tiles carry NO POI, so handle them BEFORE the
        // POIType early-return. Gate -> guardian; sanctum (post-clear) -> collect.
        if (TryHandleShardZone(coord))
            return;

        // Step 2: the arrival gate reads the overlay model, including the
        // stronghold, which exists ONLY as a stamp and is now data, not scenery.
        var ovArrived = _overlay.OverlayAt(coord);
        if (ovArrived.Poi == OverworldHex.POIType.None || ovArrived.Consumed)
            return;

        var poiType = ovArrived.Poi;
        if (PlayerSession.DebugMode && PlayerSession.ForceNextEncounterType >= 0)
        {
            poiType = (OverworldHex.POIType)PlayerSession.ForceNextEncounterType;
            PlayerSession.ForceNextEncounterType = -1;
        }

        switch (poiType)
        {
            case OverworldHex.POIType.Combat:
                OpenScoutReport(coord);
                break;

            case OverworldHex.POIType.Rest:
                // Ruling (turnaround-only Hull repair): a refuge no longer mends the
                // castle's Hull. It restores Essence, tops the furnace up, and mends
                // the crew's carried COMBAT HP (a separate economy). Hull waits for
                // the dock. S2 Campward (§8) now grants only the +2 extra Essence.
                bool campward = OverworldSpellEffects.ConsumeCampward();
                _spells?.AddEssence(3 + (campward ? 2 : 0), campward ? "Rest + Campward" : "Rest");
                ConsumeOverlayPoi(coord);
                ConsumeWorldPoi(coord);
                // K2.5: a rest still mends the crew's carried COMBAT HP (quarter of
                // max each). This is combat HP, not Hull, and untouched by the ruling.
                CompanionInjurySystem.HealExpeditionHP(SaveManager.ActiveSave, 0.25f);
                if (PlayerSession.WizardExpeditionHP >= 0)
                    PlayerSession.WizardExpeditionHP = Mathf.Min(
                        PlayerSession.WizardExpeditionMaxHP,
                        PlayerSession.WizardExpeditionHP +
                        Mathf.Max(1, PlayerSession.WizardExpeditionMaxHP / 4));
                int restSpl = SplinterDropTable.RestSite();
                SplinterEarned += restSpl;
                GoldEarned += 15;
                LogRun("rest_site", campward ? "rest (Campward)" : "rest",
                       goldDelta: +15, splinterDelta: +restSpl, at: coord);
                // Mobile Fortress §3.2: a refuge tops the furnace up (+RestRefuel).
                // §4 Druid (Verdant Ark) doubles rest-site refuel.
                int restFuelBefore = StepsRemaining;
                Refuel(RestRefuel * (_castle?.RestRefuelMultiplier ?? 1), "rest site", coord);
                int restFuelGain = StepsRemaining - restFuelBefore;
                ShowInfo($"Rest site{(campward ? " (Campward)" : "")}. Essence restored." +
                         $" +{restSpl} Arcane Splinters." +
                         (restFuelGain > 0 ? $" +{restFuelGain} fuel." : ""));
                UpdateUI();
                break;

            case OverworldHex.POIType.Narrative:
                TriggerNarrativeEncounter(coord);
                break;

            case OverworldHex.POIType.Negotiation:
                TriggerNegotiationEncounter(coord);
                break;

            case OverworldHex.POIType.Prison:
                // Imprisonment rescue (§8): storming the gaol is a combat. Winning
                // releases the captive, handled on combat return in
                // RestoreFromCombat via ReleaseImprisonedAt(resultHex). Routes
                // through the ordinary scout->commit path so difficulty scaling and
                // patrol attribution behave normally.
                OpenScoutReport(coord);
                break;

            case OverworldHex.POIType.Outpost:
                // Secured checkpoint + a staging point (world-scale reward). Ruling
                // (turnaround-only Hull repair): an outpost refuels fully and grants
                // full Essence, but does NOT mend the castle's Hull in the field.
                // That waits for the between-sortie dock. Crew combat HP still mends.
                _spells?.RestoreEssenceFull(); // S2: Outpost = full Essence (§5)
                ConsumeOverlayPoi(coord);
                ConsumeWorldPoi(coord);
                GrantStagingPointAt(coord);
                // K2.5 carry (2026-07-29): an outpost is a full rest for the fights.
                // Carriers mend to full; the wizard fields fresh. This is combat HP,
                // not Hull. Stabilized (0) companions stay down.
                CompanionInjurySystem.HealExpeditionHP(SaveManager.ActiveSave, 1.0f);
                PlayerSession.WizardExpeditionHP = -1;
                int outSpl = SplinterDropTable.RestSite();
                SplinterEarned += outSpl;
                GoldEarned += 25;
                LogRun("outpost", "secured (refuel, staging point)",
                       goldDelta: +25, splinterDelta: +outSpl, at: coord);
                // Mobile Fortress §3.2: a secured outpost refuels the castle fully.
                Refuel(0, "outpost (full)", coord, full: true);
                SaveManager.SaveIfDirty(); // checkpoint
                ShowInfo($"Outpost secured and refueled. +{outSpl} Arcane Splinters.");
                UpdateUI();
                break;

            case OverworldHex.POIType.SupplyCache:
                // Reconnaissance, not an encounter (supply_cache spec v1.1):
                // standing at the depot confirms it (the strategic marker and
                // its dialog unlock) and the banner says who's harvesting it.
                // Never consumed; the crate stays a landmark of the window.
                if (_window.TryLocalToWorld(coord, out int scCol, out int scRow))
                {
                    var scPoi = _world.PoiAt(scCol, scRow);
                    var scCycle = SaveManager.ActiveSave?.Cycle;
                    // Kind guard: debug ForceNextEncounterType can force this
                    // case onto a tile holding some OTHER POI; don't discover
                    // or misreport it as a cache.
                    if (scPoi != null && scPoi.Kind == PoiKind.SupplyCache && scCycle != null)
                    {
                        // Mobile Fortress §3.2: a cache refuels +CacheRefuel on
                        // COLLECTION. A cache here is a persistent strategic node
                        // (never consumed), so "collection" = the first scout, the
                        // one-time discovery event, which keeps it un-milkable.
                        int cacheFuelGain = 0;
                        if (!scPoi.Discovered)
                        {
                            scPoi.Discovered = true;
                            SaveManager.MarkDirty();
                            int cacheFuelBefore = StepsRemaining;
                            Refuel(CacheRefuel, "supply cache", coord);
                            cacheFuelGain = StepsRemaining - cacheFuelBefore;
                        }
                        string scCtrl = SupplyCacheSystem.ControllerDisplay(
                            scCycle, SupplyCacheSystem.ControllerOf(scPoi));
                        ShowInfo($"Supply cache harvested by {scCtrl} " +
                                 $"(+{SupplyCacheSystem.YieldOf(scPoi)} supplies/lunation). " +
                                 (cacheFuelGain > 0 ? $"+{cacheFuelGain} fuel drawn from the stores. " : "") +
                                 "Sieges are laid from the strategic map.");
                        LogRun("supply_cache", $"scouted the cache; held by {scCtrl}", at: coord);
                        UpdateUI();
                    }
                }
                break;

            case OverworldHex.POIType.Seat:
            case OverworldHex.POIType.Settlement:
                // Ruling (§3.2): reaching the guild's OWN seat is a home dock, the
                // one in-field place Hull mends, because the seat IS home: full Hull
                // repair, full refuel, full Essence, crew mended. (Enemy capitals /
                // lesser cities fall through to their services menu below.)
                if (_window.TryLocalToWorld(coord, out int seatCol, out int seatRow)
                    && _world.SettlementAt(seatCol, seatRow) is { IsGuildHome: true })
                {
                    Hull = MaxHull;
                    _spells?.RestoreEssenceFull();
                    Refuel(0, "home seat (full)", coord, full: true);
                    CompanionInjurySystem.HealExpeditionHP(SaveManager.ActiveSave, 1.0f);
                    PlayerSession.WizardExpeditionHP = -1;
                    LogRun("home_seat", "docked at the seat (full repair + refuel)", at: coord);
                    ShowInfo("The castle docks at your seat. Hull fully repaired and refueled.");
                    UpdateUI();
                    break;
                }
                // Phase 3: reaching a CITY centre on foot (a gold seat capital or a blue lesser city)
                // opens its services menu, the same shell as the strategic-map city view. Gated to
                // Tier==City inside OpenCityServices, so towns fall through. Persistent (never
                // consumed), so you can revisit it.
                OpenCityServices(coord);
                break;
        }
    }

    // ── Phase 3: enemy-capital services on the expedition map ────────────────

    private CityServicesHost _cityServices;

    /// <summary>Walked onto an enemy capital's seat tile: open its services menu over the
    /// expedition (reuses <see cref="CityServicesHost"/>). Skips the guild's OWN seat. The city
    /// name comes from its kingdom, matching the strategic-map labels.</summary>
    private void OpenCityServices(Vector2I coord)
    {
        if (_cityServices != null || _window == null || _world == null)
            return;
        if (!_window.TryLocalToWorld(coord, out int wcol, out int wrow))
            return;
        var s = _world.SettlementAt(wcol, wrow);
        // Cities only (seats + lesser cities; their footprint is the grey region); never your own.
        if (s == null || s.Tier != SettlementTier.City || s.IsGuildHome)
            return;

        if (_window3D != null) _window3D.AcceptInput = false;   // the menu owns the screen
        _cityServices = CityServicesHost.Create(CitySettlementName(s), s, CloseCityServices);
        AddChild(_cityServices);
    }

    /// <summary>Readable name for a city on the tooltip/menu. Settlement generation assigns no name,
    /// so fall back to the owning kingdom + tier ("The Untamed Seat", "… City").</summary>
    private string CitySettlementName(WorldSettlement s)
    {
        if (s == null) return "City";
        if (!string.IsNullOrEmpty(s.Name)) return s.Name;
        string kind = s.IsSeat ? "Seat" : "City";
        return !string.IsNullOrEmpty(s.KingdomId) ? $"{KingdomDisplayName(s.KingdomId)} {kind}" : kind;
    }

    /// <summary>Services menu closed: drop the reference and hand input back to the expedition.
    /// The host frees itself.</summary>
    private void CloseCityServices()
    {
        if (_cityServices == null) return;
        _cityServices = null;
        if (_window3D != null) _window3D.AcceptInput = true;
    }

    // ════════════════════════════════════════════════════════════════════
    // Combat routing (verbatim from OverworldRunManager, world-sourced)
    // ════════════════════════════════════════════════════════════════════

    private void OpenScoutReport(Vector2I coord)
    {
        string terrainType = TerrainAt(coord).ToString();   // Step 3: world read
        string regionId = StagingTemplateRegion();
        // Warfront intervention fights the region's SIEGE pool: heavy compositions,
        // Dense maps (DensityForTier), so relieving a siege feels like one.
        var tier = _isWarfront ? EncounterTier.Siege : EncounterTier.Battle;
        _scaledDifficultyMult = DifficultyMultAt(coord);

        // S4 (Identify): an identified site fights the PINNED composition:
        // what the spell showed is what you get (G5). Otherwise roll fresh.
        EncounterDefinition encounterDef = null;
        if (_window.TryLocalToWorld(coord, out int idCol, out int idRow))
            _identifiedEncounters.TryGetValue($"{idCol},{idRow}", out encounterDef);

        // Dossier attribution defaults to none (pinned/identified fights keep
        // no attribution. Accepted limit; the pin predates this pass).
        _pendingCombatArchmageId = "";

        if (encounterDef == null)
        {
            var arch = RollArchmageAt(coord);   // resident archmage rolls for its own forces
            if (PlayerSession.DebugMode)
                GD.Print($"[ArchmageEncounter] POI tile kingdom-archmage='{KingdomArchmageAt(coord)}', " +
                         $"draw={(arch != null ? arch.Id : "(region pool)")}");

            // 2c: archmage groups own their authored difficulty (mult 1.0). Region-tier
            // scaling applies only to the generic region-pool fallback.
            // SEAM: a future corrupted-archmage variant would swap `arch` here based on
            // the tile's corruption level before the draw: same call shape, different def.
            var archDef =
                arch != null
                    ? EncounterPoolLoader.PickFromArchmage(arch, regionId, tier, terrainType, CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle))
                    : null;
            encounterDef = archDef
                ?? EncounterPoolLoader.Pick(regionId, tier, terrainType, _scaledDifficultyMult);
            // Dossier: only when the archmage pool ACTUALLY supplied the
            // composition are these the archmage's own forces. Seeing them
            // opens the dossier even if the player then retreats.
            _pendingCombatArchmageId = archDef != null ? arch.Id : "";
            if (archDef != null)
                AnnounceDossierMet(arch.Id);
        }
        _pendingCombatHexCoord = coord;
        _pendingEncounter = encounterDef;
        _pendingTerrain = terrainType;

        _scoutPanel.OnEngage = () =>
        {
            if (_pendingCombatHexCoord.HasValue && _pendingEncounter != null)
            {
                CommitCombat(_pendingCombatHexCoord.Value, _pendingEncounter, _pendingTerrain);
                // Mark AFTER CommitCombat (which resets the field): whose
                // forces these are, for the dossier hook on a win.
                EncounterRouter.Instance.SavedCombatArchmageId = _pendingCombatArchmageId;
            }
            _pendingCombatHexCoord = null;
            _pendingEncounter = null;
            _pendingTerrain = null;
        };
        _scoutPanel.OnRetreat = () =>
        {
            ShowInfo("You fall back. The encounter remains.");
            _pendingCombatHexCoord = null;
            _pendingEncounter = null;
            _pendingTerrain = null;
        };

        int stepCost = GetTerrainStepCost(TerrainAt(coord));
        _scoutPanel.Show(encounterDef, TerrainAt(coord).ToString(), stepCost);
    }

    private void CommitCombat(Vector2I hexCoord, EncounterDefinition encounterDef, string terrainType, string guardianKey = "")
    {
        var router = EncounterRouter.Instance;
        if (router == null)
        { GD.PrintErr("ExpeditionManager: EncounterRouter missing."); return; }

        // S3 (Retrace): a scene swap makes "the last step" ambiguous: forget it.
        _hasLastMove = false;

        LogRun("combat_start",
               $"{encounterDef.Id} (tier {encounterDef.Tier}, {encounterDef.Enemies.Count} foes)" +
               (string.IsNullOrEmpty(guardianKey) ? "" : $" [guardian:{guardianKey}]"),
               at: hexCoord);

        // Save only the RESOURCE state: the world (and thus the map) is resident.
        router.SavedStepsRemaining = StepsRemaining;
        router.SavedCurrentHP = CurrentHP;
        router.SavedGoldEarned = GoldEarned;
        router.SavedSplinterEarned = SplinterEarned;
        router.SavedMaterialEarned = MaterialEarned;
        router.SavedSuppliesEarned = SuppliesEarned;
        router.SavedEncountersWon = EncountersWon;
        router.SavedPartyCoord = _party.CurrentCoord;
        router.SavedCombatHexCoord = hexCoord;
        // W3: carry the weather over the combat tile into the fight; the
        // battlefield injects a matching weather_tick hazard (§combat).
        router.SavedWeather = WeatherSystem.WeatherAt(hexCoord);
        // §3.4: record whether the castle was mid-stride at combat launch. Only an
        // ambush interrupts a march (a normal fight cancels the stride first), so
        // this is true exactly for a stride-interrupting ambush. F6 adds +1 round
        // to the wizard's teleport delay from it.
        router.SavedStrideAmbush = _striding;
        router.HasPendingReturn = false;
        // Reset ambush attribution: OnPatrolCapturedPlayer re-marks it AFTER
        // this call for genuine patrol fights. Without this reset, the flag
        // from a previous ambush survives on the scene-persistent router and
        // every later ordinary win re-emits patrol_slain.
        router.SavedCombatWasPatrolAmbush = false;
        router.SavedCombatPatrolArchmageId = "";
        router.SavedCombatGuardianKey = guardianKey;
        router.SavedCombatArchmageId = "";
        router.SavedResolutionArchmageId = ""; // Step 9: set AFTER this call by resolution launchers
        router.ReturnSceneOverride = "";       // expedition launches always return to the overworld

        if (_factionManager != null)
        {
            router.SavedPatrolPositions = _factionManager.GetPatrolPositions();
            router.SavedPatrolCooldowns = _factionManager.GetPatrolCooldowns();
            router.SavedPatrolArchmageId = _factionManager.GetArchmageId();
        }

        // Persist discovery so far before leaving the scene.
        SaveManager.SaveIfDirty();

        // Vista world-adjacency (combat_environments §5): capture what surrounds
        // this fight on the overworld, per hex direction. HexCoord.AxialDirections
        // matches the combat grid's HexDirs order 1:1, so index k here becomes
        // vista side k in HexGridManager.VistaTerrainBias directly.
        string[] neighborTerrains = null;
        if (_grid != null)
        {
            neighborTerrains = new string[6];
            var (q, r) = HexCoord.OffsetToAxial(hexCoord.X, hexCoord.Y);
            for (int k = 0; k < HexCoord.AxialDirections.Length; k++)
            {
                var (dq, dr) = HexCoord.AxialDirections[k];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                // Step 3: vista from the WORLD tile (loaded-guard preserved).
                var nCoord = new Vector2I(nc, nr);
                if (_grid.Hexes.ContainsKey(nCoord) && TryTileAt(nCoord, out var nTile))
                    neighborTerrains[k] = nTile.Terrain.ToString();
            }
        }

        EncounterContextCarrier.Set(encounterDef);
        EncounterContextCarrier.SetContext(terrainType, encounterDef.Tier, neighborTerrains);
        router.SetCurrentTier(encounterDef.Tier);

        ShowInfo("Entering combat...");
        GetTree().ChangeSceneToFile(router.CombatScenePath);
    }

    private void OnPatrolCapturedPlayer(Vector2I coord, string archmageId)
    {
        if (ExpeditionComplete || _ambushPending)
            return;
        if (!_grid.Hexes.ContainsKey(coord))
            return;
        // Debug: let patrols pass without forcing combat, so the map can be walked freely (e.g. to
        // reach a distant enemy capital while testing). Player-initiated combat is unaffected.
        if (PlayerSession.DebugNoAmbush)
        {
            if (PlayerSession.DebugMode)
                GD.Print($"[Debug] Ambush suppressed at {coord} (No Enemy Ambushes).");
            return;
        }

        // S3 (Parley Compulsion, Enchanter): an armed compulsion converts this
        // interception into a negotiation instead of an ambush. Once per
        // expedition (the cast carries the cap); the outcome writes stance and
        // echoes exactly as any negotiation.
        var grim = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grim != null && grim.ParleyArmed)
        {
            grim.ParleyArmed = false;
            SaveManager.MarkDirty();

            // S5 (§6a row 3): compelling the kingdom's own patrol is
            // witnessed: the echo fires NOW, at the moment of compulsion.
            // A Cordial resolution at the table buries it in flight
            // (OnNegotiationReturned); anything else lets it land on the
            // Chancellor and the Commanders.
            string compulsionToast = null;
            string patrolKingdom = KingdomIdAt(coord);
            if (!string.IsNullOrEmpty(patrolKingdom))
                compulsionToast = CouncilEcho.EmitDeed(SaveManager.ActiveSave?.Cycle,
                    patrolKingdom, CouncilEcho.PatrolCompelled,
                    positive: false, isMajor: false);

            ShowInfo("The compulsion takes hold, and the patrol will talk instead of fight." +
                     (compulsionToast != null ? $" {compulsionToast}" : ""));
            // Dossier: a compelled parley still counts as crossing paths.
            AnnounceDossierMet(archmageId);
            TriggerPatrolNegotiation(coord);
            return;
        }

        // Scrying Chambers T3 Portent (scrying_chambers_spec_v1 §2): the party foresaw this
        // interception. Spend the once-per-run portent to slip the first Ambush. The patrol
        // passes without forcing combat. Player-armed Parley (above) takes precedence: a
        // deliberate cast must not be pre-empted by passive foresight.
        if (PlayerSession.ScryingPortentAvailable)
        {
            PlayerSession.ScryingPortentAvailable = false;
            ShowInfo("The scrying held true, and you foresee the patrol and slip past unseen.");
            return;
        }

        _ambushPending = true;
        ShowInfo("A patrol has intercepted you!");
        string regionId = StagingTemplateRegion();
        string terrainType = TerrainAt(coord).ToString();   // Step 3: world read
        // The patrol BELONGS to this archmage (passed by the signal): its forces
        // are always the archmage's own, NO chance roll. Region pool only backstops
        // an archmage that has no authored skirmish group.
        var arch = ArchmageDefById(archmageId);
        if (PlayerSession.DebugMode)
            GD.Print($"[ArchmageEncounter] patrol archmageId='{archmageId}', " +
                     $"draw={(arch != null ? arch.Id : "(region pool)")}");

        _scaledDifficultyMult = DifficultyMultAt(coord);
        // On a warfront the besieging patrols hit at siege weight too. OFF a warfront
        // the tier now says WHO caught you: an archmage's patrol was hunting you
        // (Ambush: 3 enemies, Standard map, richer purse), while an unclaimed-wilds
        // band merely blundered into you (Skirmish: 2 enemies, Sparse). This is the
        // ONLY consumer of EncounterTier.Ambush; before it, every authored ambush
        // composition in every region and archmage pool was unreachable data.
        var patrolTier = _isWarfront
            ? EncounterTier.Siege
            : (arch != null ? EncounterTier.Ambush : EncounterTier.Skirmish);
        var encounterDef =
            (arch != null
                ? EncounterPoolLoader.PickFromArchmage(arch, regionId, patrolTier, terrainType, CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle))
                : null)
            ?? EncounterPoolLoader.Pick(regionId, patrolTier, terrainType, _scaledDifficultyMult);
        // Dossier: being intercepted by an archmage's patrol is crossing paths
        // with their forces ("wilds" is filtered inside the service). Fired
        // BEFORE CommitCombat (2026-07-29): CommitCombat changes scene, which
        // tears the ToastManager out of the tree; announcing after it threw
        // an NRE in ToastManager.Push (GetTree() on a detached node). The
        // dossier record persists either way; only the toast needed the tree.
        AnnounceDossierMet(archmageId);
        CommitCombat(coord, encounterDef, terrainType);
        // Mark AFTER CommitCombat (which resets the flag): this combat is a
        // patrol ambush, and whose soldiers they are (C4 deed emission).
        EncounterRouter.Instance.SavedCombatWasPatrolAmbush = true;
        EncounterRouter.Instance.SavedCombatPatrolArchmageId = archmageId;
    }

    // ════════════════════════════════════════════════════════════════════
    // Combat return: rebuild the SAME window; no seed/fog replay
    // ════════════════════════════════════════════════════════════════════

    private void RestoreFromCombat(EncounterRouter router)
    {
        StepsRemaining = router.SavedStepsRemaining;
        // K1 clamp (2026-07-09): MaxHP was recomputed in _Ready from the LIVE
        // roster: a companion permadying in the combat we're returning from
        // shrinks the pool, and the saved HP must not exceed the new ceiling.
        CurrentHP = Mathf.Min(router.SavedCurrentHP, MaxHP);
        GoldEarned = router.SavedGoldEarned;
        SplinterEarned = router.SavedSplinterEarned;
        MaterialEarned = router.SavedMaterialEarned;
        SuppliesEarned = router.SavedSuppliesEarned;
        EncountersWon = router.SavedEncountersWon;

        // The window was rebuilt fresh in _Ready from World; discovery is already
        // correct (it lives in World). W1: _Ready already built the initial disc
        // around this saved coord (return-aware Build); this recenter is a
        // cheap idempotent safety net (adds/frees 0 tiles when Build did its
        // job) that also guarantees the tile exists before party placement.
        var savedLocal = GridLocalOf(router.SavedPartyCoord);
        if (!HardWindowMode)
            RecenterWindow(savedLocal);
        _party.Initialize(_grid, _fog, savedLocal);
        _lastSupplyBand = SupplyBandAt(savedLocal);
        WriteVisibleToWorld();

        var resultHex = router.SavedCombatHexCoord;

        // Spoils card (2026-08-13): victory rewards collect here and show as
        // ONE card instead of scattering into toasts. Defeat has no card;
        // FailExpedition's banner owns that beat.
        var spoils = new List<(string, Color)>();

        if (NegotiationContext.HasResult)
        {
            OnNegotiationReturned(resultHex);
        }
        else if (router.CombatWon)
        {
            // Fragment guardian felled → the trial is passed (permanent ledger flag).
            if (!string.IsNullOrEmpty(router.SavedCombatGuardianKey))
            {
                string gk = router.SavedCombatGuardianKey;
                router.SavedCombatGuardianKey = "";
                var gsave = SaveManager.ActiveSave;
                if (gsave?.Ledger != null)
                {
                    var gBefore = QuestNotifier.Snapshot(gsave);
                    string gflag = $"{gk}_trial_passed";
                    if (!gsave.Ledger.MetaNarrativeFlags.Contains(gflag))
                    {
                        gsave.Ledger.MetaNarrativeFlags.Add(gflag);
                        SaveManager.MarkDirty();
                    }
                    // P4: mirror the pass onto the matching shard zone so its
                    // sanctum opens for collection.
                    if (_world?.ShardZones != null)
                        foreach (var sz in _world.ShardZones)
                            if (sz.FragmentKey == gk) { sz.GuardianCleared = true; break; }
                    foreach (var qt in QuestNotifier.NotifyNew(gBefore, gsave))
                        _toasts?.Push(qt.Text, qt.Kind);
                }
                _toasts?.Push("The guardian falls. The way to the fragment is open.", QuestToastKind.Progress);
                spoils.Add(("The guardian falls. The way to the fragment is open.", UITheme.Violet));
            }

            // Step 9: archmage resolution boss felled → Overthrown.
            if (!string.IsNullOrEmpty(router.SavedResolutionArchmageId))
            {
                string rid = router.SavedResolutionArchmageId;
                router.SavedResolutionArchmageId = "";
                var rCampaign = SaveManager.ActiveSave?.Cycle?.Campaign;
                var rDef = ArchmageRegistry.Get(rid);
                if (rCampaign != null)
                {
                    rCampaign.SetDisposition(rid, ArchmageDisposition.Overthrown);
                    string rRegion = rCampaign.GetRegionForArchmage(rid);
                    foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageOverthrown, rRegion, rid))
                        _toasts?.Push(qt.Text, qt.Kind);
                    SaveManager.MarkDirty();
                }
                _toasts?.Push($"{rDef?.DisplayName ?? "The archmage"} is overthrown. Their shard answers you now.",
                              QuestToastKind.Progress);
                // Q4.2 (§7c): Overthrow drops the archmage's authored relic.
                string relicLine = ArchmageRelics.TryGrant(rid, "torn from the fallen seat");
                if (relicLine != null)
                {
                    _toasts?.Push(relicLine, QuestToastKind.Progress);
                    spoils.Add((relicLine, UITheme.RarityColor("Legendary")));
                }
            }

            GoldEarned += router.GoldReward;
            SplinterEarned += router.SplinterReward;
            EncountersWon++;
            LogRun("combat_end",
                   $"victory{(router.SavedCombatWasPatrolAmbush ? " (patrol ambush)" : "")}" +
                   $", encounter #{EncountersWon}",
                   goldDelta: +router.GoldReward, splinterDelta: +router.SplinterReward,
                   at: resultHex);
            if (router.GoldReward > 0 || router.SplinterReward > 0)
                spoils.Add(($"+{router.GoldReward} gold   ·   +{router.SplinterReward} Arcane Splinters",
                            UITheme.Gold));

            // Q4.4 (§7c): combat pays in things. Tier-keyed drop roll: the
            // primary item faucet (encounter choices were the only one before
            // this). Siege rolls twice; Siege/Boss skip the chance gate.
            var lootSave = SaveManager.ActiveSave;
            if (lootSave != null)
            {
                // Q5 (§7d): drops won on corrupted ground (tier ≥ 2 at the
                // combat hex) may come back Blighted: better innate, authored
                // drawback, enchant slot sealed until Cleansed.
                bool blightGround = CorruptionTierAt(resultHex) >= 2;
                var blightRng = new RandomNumberGenerator();
                blightRng.Randomize();

                foreach (var lootDef in CombatLootTable.Roll(
                    TerritoryTierAt(resultHex), router.CurrentTier))
                {
                    var lootInst = ItemInstance.FromDefinition(lootDef);
                    if (blightGround)
                        WorkshopEnchants.MaybeBlight(lootInst, blightRng);
                    lootSave.Armory.AddItem(lootInst);
                    LogRun("item_drop", lootInst.Name, at: resultHex);
                    spoils.Add(($"{lootInst.Name}  ·  {lootInst.Rarity}" +
                                (lootInst.IsBlighted ? "  ·  BLIGHTED" : ""),
                                UITheme.RarityColor(lootInst.Rarity)));
                }
                SaveManager.MarkDirty();
            }

            // Warfront objective: storming the besieging STRONGHOLD breaks the siege.
            // Only a win on the stronghold tile counts (if one was sited); if none
            // could be placed, fall back to any won fight so the objective is never
            // impossible. Extract after this and the intervention succeeds on return.
            if (_isWarfront)
            {
                bool noStronghold = _strongholdCol < 0;
                bool atStronghold = !noStronghold
                    && _window.TryLocalToWorld(resultHex, out int wsCol, out int wsRow)
                    && wsCol == _strongholdCol && wsRow == _strongholdRow;
                var wfCycle = SaveManager.ActiveSave?.Cycle;
                if (wfCycle != null && !wfCycle.WarfrontStrongholdCleared && (atStronghold || noStronghold))
                {
                    wfCycle.WarfrontStrongholdCleared = true;
                    SaveManager.MarkDirty();
                    _toasts?.Push("The stronghold falls. The siege breaks. Extract to secure the front.",
                                  QuestToastKind.Progress);
                }
            }

            ConsumeOverlayPoi(resultHex);   // Step 2: unloaded-guard built into the seam
            ConsumeWorldPoi(resultHex);
            GrantStagingPointAt(resultHex); // securing a seat/settlement via combat can grant staging
            ShowInfo($"Victory! +{router.GoldReward} gold, +{router.SplinterReward} Splinters.");
            EmitCombatDeed(router, resultHex);

            // Sentiment: winning combat in an archmage's region shifts sentiment
            // toward the player. Killing their OWN patrol is handled separately
            // in EmitCombatDeed (negative shift there). Here: region-archmage
            // gets a positive nudge: the player is clearing threats.
            {
                var sentCampaign = SaveManager.ActiveSave?.Cycle?.Campaign;
                if (sentCampaign != null)
                {
                    string sentRegion = StagingTemplateRegion();
                    string sentArch = sentCampaign.GetArchmageForRegion(sentRegion);
                    if (!string.IsNullOrEmpty(sentArch))
                        sentCampaign.ShiftSentiment(sentArch, +5);
                }
            }

            // Dossier: a field victory over an archmage's own forces reveals
            // the next authored weakness hint (quest spec §4, wiring pass).
            {
                string dossierArch = router.SavedCombatWasPatrolAmbush
                    ? router.SavedCombatPatrolArchmageId
                    : router.SavedCombatArchmageId;
                router.SavedCombatArchmageId = "";
                if (!string.IsNullOrEmpty(dossierArch) && dossierArch != "wilds")
                {
                    var dSave = SaveManager.ActiveSave;
                    var dBefore = QuestNotifier.Snapshot(dSave);
                    string hint = DossierService.RevealNextHint(dossierArch);
                    if (hint != null)
                    {
                        var dDef = ArchmageDefById(dossierArch);
                        _toasts?.Push(
                            $"Dossier ({(dDef != null ? dDef.DisplayName : dossierArch)}): “{hint}”",
                            QuestToastKind.Progress);
                        foreach (var qt in QuestNotifier.NotifyNew(dBefore, dSave))
                            _toasts?.Push(qt.Text, qt.Kind);
                    }
                }
            }
            ReleaseImprisonedAt(resultHex); // if this was a prison, free the captive

            // S3 (Deathsight, Necromancer): every won combat leaves a Remnant
            // for the rest of the expedition: Bone Scout / Speak with the
            // Fallen cast from these. Recorded school-agnostically (cheap);
            // markers draw only when a necromancer can use them.
            if (_window.TryLocalToWorld(resultHex, out int rcol, out int rrow))
            {
                var grimR = SaveManager.ActiveSave?.Cycle?.Grimoire;
                string mark = $"{rcol},{rrow}";
                if (grimR != null && !grimR.ActiveRemnants.Contains(mark))
                {
                    grimR.ActiveRemnants.Add(mark);
                    SaveManager.MarkDirty();
                }

                // S4 (Identify): the pinned composition served its purpose.
                _identifiedEncounters.Remove(mark);
            }

            // The spoils card: everything the win paid, one modal read. Its
            // own backdrop gates map input until Continue; no host state to
            // re-enable on close.
            CombatSummaryPanel.Show(this, spoils, null);
        }
        else
        {
            // K2 (§5b): a LOST combat downs the whole fielded party (defeat
            // requires allPlayersDead in CheckCombatEnd): one roll each at the
            // combat hex's territory tier; boss encounters roll at 40%.
            _casualtyNote = CompanionInjurySystem.ApplyWipe(SaveManager.ActiveSave,
                TerritoryTierAt(resultHex),
                bossContext: router.CurrentTier == EncounterTier.Boss,
                "defeated in combat");
            LogRun("combat_end",
                   $"DEFEAT{(string.IsNullOrEmpty(_casualtyNote) ? "" : ": " + _casualtyNote)}",
                   at: resultHex);

            ConsumeOverlayPoi(resultHex);   // Step 2: unloaded-guard built into the seam
            ConsumeWorldPoi(resultHex);

            // RULED (2026-07-09): defeat ENDS the expedition. The old path
            // subtracted router.DamageTaken (which arrived as 0) and carried on
            // (a fully dead party "respawned" at full pool). A party that lost
            // everyone does not keep exploring. GodModeHP (debug) is the only
            // escape: the run survives at 1 HP.
            if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
            {
                CurrentHP = Mathf.Max(1, CurrentHP - router.DamageTaken);
                ShowInfo("Defeated... (GodMode: the expedition staggers on.)");
            }
            else
            {
                CurrentHP = 0;
                FailExpedition("Your party was defeated in the field.", injuriesAlreadyRolled: true);
                return;
            }
        }

        router.HasPendingReturn = false;
        // Stale-attribution hygiene: a LOST resolution fight must not leave the
        // archmage id armed on the scene-persistent router (the win branch
        // clears it when it applies Overthrown).
        router.SavedResolutionArchmageId = "";

        if (_factionManager != null && router.SavedPatrolPositions.Count > 0)
        {
            _factionManager.RestorePatrolPositions(router.SavedPatrolPositions);
            _factionManager.RestorePatrolCooldowns(router.SavedPatrolCooldowns);
            _factionManager.DisengagePatrolsAt(router.SavedCombatHexCoord,
                router.CombatWon ? PatrolRecoverySteps : PatrolShakeSteps);
            router.SavedPatrolPositions.Clear();
            router.SavedPatrolCooldowns.Clear();
        }

        SaveManager.SaveIfDirty();
    }

    // ════════════════════════════════════════════════════════════════════
    // Narrative / Negotiation (lifted; world-sourced ids)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Living-map (discovery_loop_spec Layer E): spawn one non-hostile
    /// roaming caravan a few hexes off, so something other than patrols moves and
    /// the map can generate a moment you didn't author. Once per expedition.</summary>
    private void SpawnRoamer()
    {
        if ((_roamer != null && GodotObject.IsInstanceValid(_roamer)) || _roamerSpent)
            return;
        if (_grid == null || _party == null)
            return;

        var start = _party.CurrentCoord;
        var candidates = new System.Collections.Generic.List<Vector2I>();
        foreach (var kvp in _grid.Hexes)
        {
            int d = _grid.Distance(start, kvp.Key);
            if (d < 6 || d > 12)
                continue;
            // Step 3: spawn-site terrain from the world tile.
            if (!TryTileAt(kvp.Key, out var rt) || rt.IsWater ||
                rt.Terrain == OverworldHex.TerrainType.Mountain)
                continue;
            candidates.Add(kvp.Key);
        }
        if (candidates.Count == 0)
            return;

        var spawn = candidates[(int)(GD.Randi() % (uint)candidates.Count)];
        _roamer = new RoamerToken { Name = "Roamer" };
        // Step 4: same query wiring as the patrols.
        _roamer.TileQuery = local => TryTileAt(local, out var rt2) ? rt2 : (WorldTile?)null;
        _roamer.FogQuery = local => _fog.FogAt(local);
        _grid.AddChild(_roamer);
        _roamer.Initialize(_grid, spawn, (int)GD.Randi());
        GD.Print($"[Roamer] Caravan spawned at {spawn} (dist {_grid.Distance(start, spawn)} from party).");
    }

    /// <summary>Contact with the roaming caravan: a one-time opportunity encounter.
    /// Despawns the caravan afterward so it does not re-offer.</summary>
    private void TriggerRoamerEncounter()
    {
        _roamerSpent = true;
        if (_roamer != null && GodotObject.IsInstanceValid(_roamer))
            _roamer.QueueFree();
        _roamer = null;

        var enc = BuildCaravanEncounter();
        var save = SaveManager.ActiveSave;
        System.Func<string, bool> hasFlag = null;
        if (save != null) hasFlag = save.HasFlag;
        var terr = (_party != null && _grid != null &&
                    _grid.Hexes.ContainsKey(_party.CurrentCoord))
            ? TerrainAt(_party.CurrentCoord) : OverworldHex.TerrainType.Grassland;
        _narrativePanel.ShowEncounter(enc, hasFlag, save?.Cycle?.SelectedSchool, GoldEarned,
            save?.Cycle?.Campaign);
        _narrativePanel.OnCompleted = (choice) => OnNarrativeCompleted(enc, choice, terr);
        ShowInfo("A caravan crosses your path.");
    }

    private static NarrativeEncounterData BuildCaravanEncounter() => new NarrativeEncounterData
    {
        Id = "roaming_caravan",
        Title = "A Caravan on the Road",
        Body = "A string of laden mules and creaking carts crests the rise: a merchant column far " +
               "from any road you'd expect. The lead driver raises an open hand. Not a threat; an offer.",
        Choices = new System.Collections.Generic.List<EncounterChoice>
        {
            new EncounterChoice { Label = "Trade for supplies (20 gold)",
                ResultText = "Dried rations and clean water change hands. Your party travels easier.",
                GoldDelta = -20, HPDelta = 20, RequiredGold = 20 },
            new EncounterChoice { Label = "Buy a warding cloak (30 gold)",
                ResultText = "The driver produces a travel-worn but sound warding cloak for the armory.",
                GoldDelta = -30, ItemReward = "warding_cloak", RequiredGold = 30 },
            new EncounterChoice { Label = "Buy word of the road ahead (5 gold)",
                ResultText = "They trade rumor for coin: a shortcut, and a warning about what waits on it.",
                GoldDelta = -5, StepDelta = 4, RequiredGold = 5 },
            new EncounterChoice { Label = "Wave them on",
                ResultText = "The column rolls past and is gone. The road feels emptier after." },
        },
    };

    /// <summary>P4: standing on a shard sub-region tile. GATE (guardian not yet
    /// felled) -> launch the guardian Boss (fragment key doubles as guardian key,
    /// so the combat-return handler stamps &lt;key&gt;_trial_passed + GuardianCleared).
    /// SANCTUM (guardian felled, shard not taken) -> collect the shard. Returns true
    /// when the tile was a shard-zone trigger.</summary>
    private bool TryHandleShardZone(Vector2I coord)
    {
        if (_world?.ShardZones == null)
            return false;
        if (!_window.TryLocalToWorld(coord, out int col, out int row))
            return false;
        var z = _world.ShardZoneAt(col, row);
        if (z == null)
            return false;

        if (col == z.GateX && row == z.GateY && !z.GuardianCleared)
        {
            if (!_grid.Hexes.ContainsKey(coord))
                return false;
            ShowInfo($"The heart of {z.Name} is guarded. Its warden stirs.");
            LaunchGuardianCombat(z.FragmentKey, TerrainAt(coord));
            return true;
        }

        if (col == z.SanctumX && row == z.SanctumY && z.GuardianCleared && !z.ShardCollected)
        {
            CollectShard(z);
            return true;
        }

        return false;
    }

    /// <summary>P4: take the shard from a cleared sanctum: permanent
    /// fragment_&lt;key&gt;_collected, convert the vault centre to a staging point
    /// (the vault becomes a forward base), bump host-kingdom influence, notify.
    /// Idempotent. Staging is added inline: a vault centre carries no POI, so the
    /// POI-gated GrantStagingPointAtWorld does not apply; this parallels its
    /// core.</summary>
    private void CollectShard(ShardZone z)
    {
        if (z.ShardCollected)
            return;
        z.ShardCollected = true;

        var save = SaveManager.ActiveSave;
        var before = QuestNotifier.Snapshot(save);
        string flag = $"fragment_{z.FragmentKey}_collected";
        if (save?.Ledger != null && !save.Ledger.MetaNarrativeFlags.Contains(flag))
            save.Ledger.MetaNarrativeFlags.Add(flag);

        bool already = false;
        foreach (var sp in _world.StagingPoints)
            if (sp.X == z.CenterX && sp.Y == z.CenterY) { already = true; break; }
        if (!already)
        {
            _world.StagingPoints.Add(new StagingPoint
            {
                X = z.CenterX,
                Y = z.CenterY,
                Name = z.Name,
                Source = "Shard",
                Available = true,
            });
            if (_world.TryIndex(z.CenterX, z.CenterY, out int cidx))
            {
                _world.Tiles[cidx].IsStagingPoint = true;
                string kid = _world.Tiles[cidx].KingdomId;
                var kingdoms = SaveManager.ActiveSave?.Cycle?.Kingdoms;
                if (!string.IsNullOrEmpty(kid) && kingdoms != null &&
                    kingdoms.TryGetValue(kid, out var ks))
                    ks.PlayerInfluence = Mathf.Min(100, ks.PlayerInfluence + StagingInfluenceGain);
            }
        }

        SaveManager.MarkDirty();
        LogRun("shard_collected", $"{z.Name} ({z.FragmentKey}); vault becomes staging point");
        _toasts?.Push($"Shard recovered: {z.Name}.", QuestToastKind.Complete);
        ShowInfo($"You take the shard from {z.Name}. Its power is yours, and the vault " +
                 "is now a staging point.");
        foreach (var qt in QuestNotifier.NotifyNew(before, save))
            _toasts?.Push(qt.Text, qt.Kind);
        UpdateUI();
    }

    /// <summary>[DEBUG] V: teleport to the nearest UNFINISHED shard vault (its GATE
    /// while the guardian stands, else its SANCTUM) and trigger arrival, so P4's
    /// gate/guardian/collect flow is testable without surviving the walk in.</summary>
    private void DebugTeleportToVault()
    {
        if (_world?.ShardZones == null || _world.ShardZones.Count == 0)
        { ShowInfo("[DEBUG] No shard zones in this world."); return; }

        if (!_window.TryLocalToWorld(_party.CurrentCoord, out int pc, out int pr))
        { pc = _window.StagingCol; pr = _window.StagingRow; }

        ShardZone best = null;
        int bestX = 0, bestY = 0, bestD = int.MaxValue;
        foreach (var z in _world.ShardZones)
        {
            int tx, ty;
            if (!z.GuardianCleared) { tx = z.GateX; ty = z.GateY; }
            else if (!z.ShardCollected) { tx = z.SanctumX; ty = z.SanctumY; }
            else continue;
            int d = _world.HexDistance(pc, pr, tx, ty);
            if (d < bestD) { bestD = d; best = z; bestX = tx; bestY = ty; }
        }
        if (best == null)
        { ShowInfo("[DEBUG] All shard vaults are complete."); return; }

        var local = _window.LocalOf(bestX, bestY);
        RecenterWindow(local);
        _party.Initialize(_grid, _fog, local);
        WriteVisibleToWorld();
        string what = !best.GuardianCleared ? "gate" : "sanctum";
        ShowInfo($"[DEBUG] Teleported to {best.Name} {what} ({bestX},{bestY}).");
        OnPartyArrived(local);
    }

    private void TriggerNarrativeEncounter(Vector2I coord)
    {
        string terrainName = TerrainAt(coord).ToString();   // Step 3: world read
        var completedIds = SaveManager.ActiveSave?.CompletedEvents;

        // K3 (§5a): a rescue POI wears a Narrative marker in-window, but the
        // world-side kind survives; route it to a found-person encounter.
        // Falls through to the normal pool when no one is left to find
        // (a rescue site must never dead-end into nothing).
        NarrativeEncounterData encounter = null;
        if (_window.TryLocalToWorld(coord, out int wcol, out int wrow) &&
            _world.PoiAt(wcol, wrow)?.Kind == PoiKind.Companion)
        {
            encounter = BuildRescueEncounter();
        }

        encounter ??= NarrativeEncounterLoader.PickRandom(_encounterPool, terrainName, completedIds, SaveManager.ActiveSave);

        ConsumeOverlayPoi(coord);   // Step 2
        ConsumeWorldPoi(coord);

        if (encounter == null)
        {
            int gold = 15 + (int)(GD.Randf() * 20);
            GoldEarned += gold;
            LogRun("gold_find", "unmarked cache (narrative pool empty)",
                   goldDelta: +gold, at: coord);
            ShowInfo($"You find something of value here. (+{gold} gold)");
            UpdateUI();
            return;
        }
        var gateSave = SaveManager.ActiveSave;
        System.Func<string, bool> hasFlag = null;
        if (gateSave != null) hasFlag = gateSave.HasFlag;

        // T3 gates (2026-08-13): the Armory and the fielded party are keys.
        System.Func<string, bool> hasItem = null;
        System.Func<string, bool> hasCompanion = null;
        if (gateSave != null)
        {
            hasItem = id =>
            {
                foreach (var inst in gateSave.Armory.OwnedItems)
                    if (inst.DefinitionId == id) return true;
                return false;
            };
            hasCompanion = id =>
            {
                foreach (var c in CompanionRoster.GetActiveParty())
                    if (c.Id == id) return true;
                return false;
            };
        }

        var shownEnc = EncounterAssembler.ForDisplay(encounter, TerrainAt(coord), StagingTemplateRegion());
        _narrativePanel.ShowEncounter(
            shownEnc,
            hasFlag,
            gateSave?.Cycle?.SelectedSchool,
            GoldEarned,
            gateSave?.Cycle?.Campaign,
            hasItem,
            hasCompanion);
        LogRun("narrative_start", encounter.Id, at: coord);
        var loreTerrain = TerrainAt(coord); // S4: the drop pool is terrain-flavored
        _narrativePanel.OnCompleted = (choice) => OnNarrativeCompleted(encounter, choice, loreTerrain);
    }

    /// <summary>K3 (§5a): synthesize the found-person encounter for a rescue POI.
    /// Eligible: authored, not recruited, not dead, and NOT IsAvailable. The
    /// available ones are the hiring halls' pool; rescues find the people no
    /// hall will ever list. Complementary by construction, so a rescue never
    /// duplicates a hall offer. Returns null when no one is left to find
    /// (caller falls through to the normal narrative pool). The grant rides
    /// the existing CompanionUnlock → GrantFromEncounter path: no gold, per
    /// spec ("found people"). NOTE (logged deviation): the spec's "arrives
    /// with a live arc, ArcStage > 0" is deferred to K4: ArcStage is derived
    /// state owned by CompanionArcTracker's flag sync, and forcing it here
    /// would desync the tracker (single-source discipline).</summary>
    private NarrativeEncounterData BuildRescueEncounter()
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return null;

        var pool = new List<Companion>();
        foreach (var c in save.Companions)
            if (!c.IsRecruited && !c.IsPermadead && !c.IsAvailable)
                pool.Add(c);
        if (pool.Count == 0) return null;

        var found = pool[(int)(GD.Randf() * pool.Count) % pool.Count];

        return new NarrativeEncounterData
        {
            Id = $"rescue_{found.Id}",
            Title = "A Found Person",
            Body = $"Half-hidden from the road: a makeshift camp, cold ashes, and someone " +
                   $"who has been out here too long. {found.Name}. " +
                   $"{found.Backstory} They watch you decide what you are before " +
                   $"they decide what they'll be.",
            Choices = new List<EncounterChoice>
            {
                new EncounterChoice
                {
                    Label = $"Take {found.Name} in",
                    ResultText = $"{found.Name} gathers what little there is to gather. " +
                                 "Found, not bought, and they will remember which.",
                    CompanionUnlock = found.Id,
                },
                new EncounterChoice
                {
                    Label = "Leave them to their road",
                    ResultText = "You mark the camp on the map and move on. " +
                                 "Some debts are cheaper never taken on.",
                },
            },
        };
    }

    /// <summary>T3 intel verb (2026-08-13): reveal the N nearest hidden,
    /// unconsumed POI hexes as landmark BEACONS (IsLandmark set BEFORE the
    /// fog write; the 08-08 lesson: the redraw catches the styling only in
    /// that order). Distance from the party's current hex; ties break by
    /// iteration order. Returns how many were actually revealed (the window
    /// may hold fewer hidden POIs than asked).</summary>
    private int RevealNearestPois(int count)
    {
        if (_grid == null || _fog == null || _party == null || count <= 0)
            return 0;

        var candidates = new List<(int dist, Vector2I coord, OverworldHex hex)>();
        var from = _party.CurrentCoord;
        foreach (var kvp in _grid.Hexes)
        {
            var hex = kvp.Value;
            if (hex.POI == OverworldHex.POIType.None || hex.POIConsumed)
                continue;
            if (hex.Fog == OverworldHex.FogState.Revealed)
                continue;   // already known
            candidates.Add((HexCoord.OffsetDistance(from.X, from.Y, kvp.Key.X, kvp.Key.Y),
                            kvp.Key, hex));
        }
        candidates.Sort((a, b) => a.dist - b.dist);

        int revealed = 0;
        foreach (var (_, coord, hex) in candidates)
        {
            if (revealed >= count) break;
            hex.IsLandmark = true;          // beacon styling, BEFORE the fog write
            _fog.RevealHex(coord);
            revealed++;
        }
        return revealed;
    }

    /// <summary>Scrying Chambers run-start intelligence (scrying_chambers_spec_v1 §2).
    /// T1+: chart the nearest hidden POIs as beacons. T2+: chart a radius around the run
    /// objective so its site is known from turn one. T3: arm the once-per-run Ambush
    /// Portent. Pure reuse of existing primitives (RevealNearestPois, SpellChartHexRadius,
    /// the fog): no new reveal machinery. Called after the grid, fog, and party are all
    /// placed (post-StampStronghold), because RevealNearestPois reads _party.CurrentCoord.</summary>
    private void ApplyScryingReveals(BuildingEffectApplier.RunBonuses bonuses)
    {
        if (bonuses.RevealPoiCount > 0)
        {
            int marked = RevealNearestPois(bonuses.RevealPoiCount);
            if (marked > 0)
                ShowInfo($"Scrying: {marked} site{(marked == 1 ? "" : "s")} charted.");
        }

        // Chart around the run objective. Not every run has a discrete objective hex
        // (open explorations do not); when none is flagged this simply does nothing.
        if (bonuses.ChartObjectiveRadius > 0 && _grid != null)
        {
            foreach (var kvp in _grid.Hexes)
            {
                if (!kvp.Value.IsObjective) continue;
                SpellChartHexRadius(kvp.Key.X, kvp.Key.Y, bonuses.ChartObjectiveRadius);
                break;   // a run has one objective; chart the first and stop
            }
        }

        if (bonuses.ScryingPortent)
            PlayerSession.ScryingPortentAvailable = true;
    }

    /// <summary>Difficulty multiplier applied to fragment-guardian boss units.</summary>
    private const float GuardianDifficultyMult = 1.6f;

    /// <summary>Launch a fragment-guardian Boss combat. Winning sets
    /// &lt;key&gt;_trial_passed (handled on combat return). Falls back to granting
    /// the pass directly if combat can't be staged, so the arc never dead-ends.</summary>
    private void LaunchGuardianCombat(string key, OverworldHex.TerrainType terrain)
    {
        var def = BuildGuardianEncounter(key, terrain);
        var router = EncounterRouter.Instance;
        if (router == null || def == null || def.Enemies.Count == 0)
        {
            var save = SaveManager.ActiveSave;
            if (save?.Ledger != null && !save.Ledger.MetaNarrativeFlags.Contains($"{key}_trial_passed"))
            {
                save.Ledger.MetaNarrativeFlags.Add($"{key}_trial_passed");
                SaveManager.MarkDirty();
            }
            LogRun("guardian_bypassed", $"{key}: trial granted unopposed");
            ShowInfo("The guardian does not stir. You pass unopposed.");
            UpdateUI();
            return;
        }
        ShowInfo("The guardian rises to bar your way!");
        CommitCombat(_party.CurrentCoord, def, terrain.ToString(), key);
    }

    /// <summary>negotiation_system.docx Resolution Check, the escalation branch:
    /// "tension is at 10 and the NPC archetype is aggressive (Commander, some
    /// Opportunists), triggering combat." Specced since v1 and never wired: a
    /// collapsed table just closed.
    ///
    /// The composition is drawn from the SAME region and archmage pools an
    /// ordinary Battle-tier POI draws from, so an escalation is a real regional
    /// engagement that inherits archmage forces on archmage-held ground, rather
    /// than a bespoke one-off roster. Battle rather than Ambush tier on purpose:
    /// you are standing across a table from them and both sides watched this
    /// coming.
    ///
    /// <paramref name="hexCoord"/> is WINDOW-LOCAL. It comes from
    /// EncounterRouter.SavedCombatHexCoord, which TriggerNegotiationEncounter set
    /// from its own local coord, and which GridLocalOf maps by identity. That is
    /// the space TerrainAt, RollArchmageAt, DifficultyMultAt and CommitCombat all
    /// expect, so no conversion happens here.</summary>
    private void LaunchNegotiationEscalation(Vector2I hexCoord, string escalatedFrom)
    {
        string regionId = StagingTemplateRegion();
        string terrainType = TerrainAt(hexCoord).ToString();

        var arch = RollArchmageAt(hexCoord);
        var archDef = arch != null
            ? EncounterPoolLoader.PickFromArchmage(
                  arch, regionId, EncounterTier.Battle, terrainType,
                  CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle))
            : null;
        var def = archDef
            ?? EncounterPoolLoader.Pick(regionId, EncounterTier.Battle,
                                        terrainType, DifficultyMultAt(hexCoord));

        // Fail CLOSED, never silently. If no composition can be produced the table
        // ends as an ordinary collapse. The outcome is already recorded either
        // way, so nothing is lost but the fight.
        if (def == null || def.Enemies.Count == 0)
        {
            LogRun("negotiation_escalation_bypassed",
                   $"{escalatedFrom} (no {regionId}/Battle composition)", at: hexCoord);
            ShowInfo("The table breaks up badly. Nothing comes of it this time.");
            return;
        }

        // Their own forces showing up IS meeting them (same rule as the ordinary
        // POI path: seeing the composition opens the dossier).
        if (archDef != null)
            AnnounceDossierMet(arch.Id);

        LogRun("negotiation_escalated", $"{escalatedFrom} → {def.Id}", at: hexCoord);
        ShowInfo("The table breaks. Steel comes out.");
        CommitCombat(hexCoord, def, terrainType);
    }

    /// <summary>A themed Boss-tier composition per fragment (archetypes resolved
    /// through UnitRegistry; scaled by GuardianDifficultyMult).</summary>
    private EncounterDefinition BuildGuardianEncounter(string key, OverworldHex.TerrainType terrain)
    {
        string[] arch = key switch
        {
            "primal"    => new[] { "Brute", "Wizard", "Wizard" },
            "axiom"     => new[] { "Wizard", "Wizard", "Defender" },
            "moment"    => new[] { "Ranger", "Wizard", "Ranger" },
            "binding"   => new[] { "Wizard", "Wizard", "Soldier" },
            "schema"    => new[] { "Defender", "Brute", "Soldier" },
            "deathless" => new[] { "Brute", "Wizard", "Defender" },
            _           => new[] { "Brute", "Wizard", "Soldier" },
        };
        // Capstone escalation (user ruling 2026-07-20: scale both, shared knob):
        // the shard guardian hardens with the timeline's threat like the rest of the
        // world. Authored 1.6 base x the per-year threat scalar (x1.0 at Year 1).
        float mult = GuardianDifficultyMult *
                     CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle);
        var def = new EncounterDefinition
        {
            Id = $"guardian_{key}",
            DisplayName = "The Warden",
            Tier = EncounterTier.Boss,
            RegionId = StagingTemplateRegion(),
            TerrainType = terrain.ToString(),
            DifficultyMult = mult,
        };
        foreach (var a in arch)
            if (UnitRegistry.TryResolveId(a, out var uid))
                def.Enemies.Add(new EnemySlot(uid, mult));
        return def;
    }

    /// <summary>Step 9: apply a resolution verb chosen in an audience
    /// encounter. Returns true when the verb was consumed (unite/coerce
    /// resolved, or the overthrow boss launched); false for unknown kinds so
    /// the caller falls through to ordinary choice processing.</summary>
    private bool HandleResolutionChoice(string archmageId, string kind)
    {
        var campaign = SaveManager.ActiveSave?.Cycle?.Campaign;
        if (campaign == null) return false;
        var def = ArchmageRegistry.Get(archmageId);
        string region = campaign.GetRegionForArchmage(archmageId);

        switch (kind.ToLowerInvariant())
        {
            case "unite":
                campaign.SetDisposition(archmageId, ArchmageDisposition.Allied);
                foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageUnited, region, archmageId))
                    _toasts?.Push(qt.Text, qt.Kind);
                _toasts?.Push($"{def?.DisplayName ?? "The archmage"} stands with the guild.",
                              QuestToastKind.Progress);
                // K5 (§5a): the united school seconds one adept.
                string unitedAdept = RecruitmentSources.OnArchmageUnited(archmageId);
                if (unitedAdept != null) _toasts?.Push(unitedAdept, QuestToastKind.Progress);
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                UpdateUI();
                return true;

            case "coerce":
                campaign.SetDisposition(archmageId, ArchmageDisposition.Coerced);
                foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageCoerced, region, archmageId))
                    _toasts?.Push(qt.Text, qt.Kind);
                _toasts?.Push($"{def?.DisplayName ?? "The archmage"} yields to the accord, for now.",
                              QuestToastKind.Progress);
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                UpdateUI();
                return true;

            case "overthrow":
                LaunchResolutionCombat(archmageId);
                return true;
        }
        return false;
    }

    /// <summary>Step 9: launch the archmage resolution boss fight. Falls back
    /// to resolving directly if combat can't be staged, so the resolution arc
    /// never dead-ends (the guardian-fallback pattern).</summary>
    private void LaunchResolutionCombat(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        var campaign = save?.Cycle?.Campaign;
        var def = ResolutionEncounterBuilder.BuildOverthrowCombat(
            campaign, archmageId, save?.Cycle?.SelectedSchool);
        var router = EncounterRouter.Instance;
        if (router == null || def == null)
        {
            if (campaign != null)
            {
                campaign.SetDisposition(archmageId, ArchmageDisposition.Overthrown);
                foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageOverthrown,
                         campaign.GetRegionForArchmage(archmageId), archmageId))
                    _toasts?.Push(qt.Text, qt.Kind);
            }
            SaveManager.MarkDirty();
            ShowInfo("The seat falls without a fight. The shard is yours.");
            UpdateUI();
            return;
        }
        ShowInfo("The archmage rises to meet you!");
        string terrain = "Plains";
        if (_grid != null && _party != null &&
            _grid.Hexes.ContainsKey(_party.CurrentCoord))
            terrain = TerrainAt(_party.CurrentCoord).ToString();   // Step 3
        CommitCombat(_party.CurrentCoord, def, terrain);
        router.SavedResolutionArchmageId = archmageId; // after CommitCombat, per the patrol pattern
    }

    private void OnNarrativeCompleted(NarrativeEncounterData encounter, EncounterChoice choice,
                                      OverworldHex.TerrainType terrain)
    {
        if (choice == null)
            return;

        // Step 9: resolution verbs on an audience encounter resolve the
        // archmage in place (unite/coerce) or launch the boss (overthrow).
        // Unrecognized kinds (withdraw) fall through to normal processing.
        if (!string.IsNullOrEmpty(choice.ResolutionKind) &&
            !string.IsNullOrEmpty(encounter.ArchmageId) &&
            HandleResolutionChoice(encounter.ArchmageId, choice.ResolutionKind))
        {
            LogRun("archmage_resolution", $"{encounter.ArchmageId}: {choice.ResolutionKind}");
            return;
        }

        if (!string.IsNullOrEmpty(choice.LaunchGuardian))
        {
            LaunchGuardianCombat(choice.LaunchGuardian, terrain);
            return;
        }

        var questBefore = QuestNotifier.Snapshot(SaveManager.ActiveSave);
        int nGoldBefore = GoldEarned, nHpBefore = CurrentHP, nStepsBefore = StepsRemaining;

        if (choice.GoldDelta != 0)
            GoldEarned = Mathf.Max(0, GoldEarned + choice.GoldDelta);
        if (choice.HPDelta != 0)
        {
            Hull = Mathf.Clamp(Hull + choice.HPDelta, 0, MaxHull);
            if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
                Hull = Mathf.Max(1, Hull);
            if (Hull <= 0)
            { EmergencyExtract("A fateful choice cripples the castle, forcing a recall."); return; }
        }
        if (choice.StepDelta != 0)
            StepsRemaining = Mathf.Max(0, StepsRemaining + choice.StepDelta);

        int spl = SplinterDropTable.Narrative();
        SplinterEarned += spl;

        // T3 (2026-08-13): the intel verb: information as a first-class
        // reward. Reveals the N nearest hidden POIs as beacons.
        if (choice.RevealPois > 0)
        {
            int marked = RevealNearestPois(choice.RevealPois);
            if (marked > 0)
                ShowInfo($"Intel: {marked} site{(marked == 1 ? "" : "s")} marked on the map.");
        }

        if (SaveManager.ActiveSave != null && !string.IsNullOrEmpty(encounter.Id))
            if (!SaveManager.ActiveSave.CompletedEvents.Contains(encounter.Id))
                SaveManager.ActiveSave.CompletedEvents.Add(encounter.Id);

        if (choice.SetFlags != null && SaveManager.ActiveSave != null)
        {
            bool anyNewFlag = false;
            foreach (var flag in choice.SetFlags)
                anyNewFlag |= SaveManager.ActiveSave.SetFlag(flag);
            if (anyNewFlag) SaveManager.MarkDirty();
        }

        // Permanent story flags (fragment-arc milestones) ride the ledger so
        // they survive a cycle reset. Read by quests + choice gating (HasFlag).
        if (choice.SetMetaFlags != null && SaveManager.ActiveSave?.Ledger != null)
        {
            bool anyMeta = false;
            var meta = SaveManager.ActiveSave.Ledger.MetaNarrativeFlags;
            foreach (var flag in choice.SetMetaFlags)
                if (!string.IsNullOrEmpty(flag) && !meta.Contains(flag))
                { meta.Add(flag); anyMeta = true; }
            if (anyMeta) SaveManager.MarkDirty();
        }

        // Companion arc delivery (Step 9 follow-up): if this encounter was the
        // companion's current arc stage, advance the arc and toast it.
        var arcStatus = CompanionArcTracker.TryCompleteByEncounter(encounter.Id, SaveManager.ActiveSave);
        if (arcStatus != null)
        {
            _toasts?.Push(arcStatus.IsComplete
                ? $"{arcStatus.CompanionName}: \"{arcStatus.ArcName}\" complete."
                : $"{arcStatus.CompanionName}: \"{arcStatus.ArcName}\" advances ({arcStatus.CurrentStage}/{arcStatus.TotalStages}).",
                QuestToastKind.Progress);
            SaveManager.MarkDirty();
        }

        // S4 (§11): lore POIs are the terrain-flavored acquisition path.
        // An authored SpellReward on the chosen option grants exactly that
        // spell; otherwise a bonus roll may teach an unknown learnable from
        // the tile's flavored pool. KnownSpellIds rides CycleState, so the
        // learn persists through any save (the S4 exit criterion).
        string learnedId = "";
        var grimL = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grimL != null)
        {
            if (!string.IsNullOrEmpty(choice.SpellReward))
            {
                if (SpellAcquisition.Learn(grimL, choice.SpellReward))
                    learnedId = choice.SpellReward;
            }
            else if (GD.Randf() < SpellAcquisition.NarrativeDropChance)
            {
                string roll = SpellAcquisition.RollUnknownLearnable(grimL, terrain);
                if (roll != "" && SpellAcquisition.Learn(grimL, roll))
                    learnedId = roll;
            }
        }

        // ── Explore→named codices (§8): the card analogue of the spell block
        // above. An authored CardReward discovers exactly that blueprint; a
        // CardCodex choice with no named reward rolls an unknown in-school Rare.
        // Discovery is permanent (rides the ledger), so a card found in the field
        // is known across every timeline, the same knowledge/power split. ──
        string discoveredCard = "";
        var cardSave = SaveManager.ActiveSave;
        if (cardSave?.Ledger != null)
        {
            if (!string.IsNullOrEmpty(choice.CardReward))
                discoveredCard = CardAcquisition.Discover(cardSave, choice.CardReward);
            else if (choice.CardCodex)
            {
                string roll = CardAcquisition.RollUnknownInSchoolRare(
                    cardSave, cardSave.Cycle?.SelectedSchool);
                if (!string.IsNullOrEmpty(roll))
                    discoveredCard = CardAcquisition.Discover(cardSave, roll);
            }
        }

        // ── Tranche 2 reward verbs: item / companion / reputation / lore ──
        var t2 = new System.Collections.Generic.List<string>();
        var t2save = SaveManager.ActiveSave;
        if (t2save != null)
        {
            if (!string.IsNullOrEmpty(choice.ItemReward))
            {
                var def = ItemDatabase.Get(choice.ItemReward);
                if (def != null)
                {
                    t2save.Armory.AddItem(def);
                    SaveManager.MarkDirty();
                    t2.Add($"gain the {def.Name}");
                }
                else GD.PrintErr($"[Encounter] ItemReward '{choice.ItemReward}' not in ItemDatabase.");
            }

            if (!string.IsNullOrEmpty(choice.CompanionUnlock))
            {
                string joined = CompanionRoster.GrantFromEncounter(choice.CompanionUnlock);
                if (joined != null) t2.Add($"are joined by {joined}");
            }

            if (!string.IsNullOrEmpty(choice.ReputationFactionId) && choice.ReputationAmount != 0)
            {
                var rep = t2save.FactionReputation;
                rep.TryGetValue(choice.ReputationFactionId, out int cur);
                rep[choice.ReputationFactionId] = cur + choice.ReputationAmount;
                SaveManager.MarkDirty();
                t2.Add($"gain {(choice.ReputationAmount >= 0 ? "+" : "")}{choice.ReputationAmount} " +
                       $"standing with {choice.ReputationFactionId.Replace('_', ' ')}");
            }

            if (!string.IsNullOrEmpty(choice.LoreId) &&
                !t2save.UnlockedLoreEntries.Contains(choice.LoreId))
            {
                t2save.UnlockedLoreEntries.Add(choice.LoreId);
                SaveManager.MarkDirty();
                t2.Add("uncover a truth for the Hall of Records");
            }
        }

        string msg = learnedId != ""
            ? $"Encounter resolved. +{spl} Arcane Splinters. The site yields the secret of " +
              $"{OverworldSpellRegistry.Get(learnedId)?.Name}, preparable at the next launch."
            : $"Encounter resolved. +{spl} Arcane Splinters.";
        if (t2.Count > 0)
            msg += " You " + string.Join(", ", t2) + ".";
        if (discoveredCard != "")
            msg += $" A codex here yields the {discoveredCard}, now in your card library, " +
                   $"draftable and scribable.";

        LogRun("narrative_choice",
               encounter.Id
               + (learnedId != "" ? $"; learned {learnedId}" : "")
               + (discoveredCard != "" ? $"; discovered card {discoveredCard}" : "")
               + (t2.Count > 0 ? "; " + string.Join("; ", t2) : ""),
               goldDelta: GoldEarned - nGoldBefore,
               splinterDelta: spl,
               hpDelta: CurrentHP - nHpBefore,
               stepsDelta: StepsRemaining - nStepsBefore);

        ShowInfo(msg);

        foreach (var qt in QuestNotifier.NotifyNew(questBefore, SaveManager.ActiveSave))
            _toasts?.Push(qt.Text, qt.Kind);

        UpdateUI();
    }

    /// <summary>S3 (Parley Compulsion): a patrol interception converted into a
    /// negotiation. Same setup as a Negotiation POI, minus POI consumption:
    /// the patrol's hex owns no POI. The patrol itself disengages via the
    /// standard post-negotiation restore path.</summary>
    private void TriggerPatrolNegotiation(Vector2I coord)
    {
        string kingdomId = StagingTemplateRegion();
        string terrain = TerrainAt(coord).ToString();   // Step 3: world read
        var encounter = NegotiationEncounterLoader.PickForTerrain(terrain, kingdomId);
        if (encounter == null)
        { ShowInfo("The patrol shakes off the compulsion and has nothing to say."); UpdateUI(); return; }

        NegotiationContext.Clear();
        NegotiationContext.EncounterId = encounter.Id;
        NegotiationContext.HexCoordKey = $"{coord.X},{coord.Y}";
        NegotiationContext.NpcArchetype = encounter.Archetype.ToString();
        NegotiationContext.OriginKingdomId = KingdomIdAt(coord);
        NegotiationContext.FromCompulsion = true; // S5: sole caller is the Parley path
        ConsumeBeguileIfArmed();

        var router = EncounterRouter.Instance;
        if (router != null)
        {
            router.SavedStepsRemaining = StepsRemaining;
            router.SavedCurrentHP = CurrentHP;
            router.SavedGoldEarned = GoldEarned;
            router.SavedSplinterEarned = SplinterEarned;
            router.SavedMaterialEarned = MaterialEarned;
            router.SavedSuppliesEarned = SuppliesEarned;
            router.SavedEncountersWon = EncountersWon;
            router.SavedPartyCoord = _party.CurrentCoord;
            router.SavedCombatHexCoord = coord;
            router.SavedCombatWasPatrolAmbush = false;
            router.SavedCombatPatrolArchmageId = "";
            router.HasPendingReturn = true;
            if (_factionManager != null)
            {
                router.SavedPatrolPositions = _factionManager.GetPatrolPositions();
                router.SavedPatrolCooldowns = _factionManager.GetPatrolCooldowns();
                router.SavedPatrolArchmageId = _factionManager.GetArchmageId();
            }
        }
        _hasLastMove = false; // S3 (Retrace): scene swap forgets the last step
        SaveManager.SaveIfDirty();
        LogRun("negotiation_start",
               $"{encounter.Id} ({encounter.Archetype}) [patrol parley]", at: coord);
        ShowInfo($"Negotiation: {encounter.Title}");
        GetTree().ChangeSceneToFile("res://Scenes/Negotiation/NegotiationScene.tscn");
    }

    /// <summary>S3 (Beguile): consume an armed charm into the tension shift
    /// the negotiation layer applies on open. One band ≈ 2 tension.</summary>
    private void ConsumeBeguileIfArmed()
    {
        var grim = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grim == null || !grim.BeguileArmed)
            return;
        grim.BeguileArmed = false;
        NegotiationContext.TensionShift = 2;
        SaveManager.MarkDirty();
        GD.Print("[Spellcraft] Beguile takes effect: the table opens a band more favorable.");
    }

    private void TriggerNegotiationEncounter(Vector2I coord)
    {
        ConsumeOverlayPoi(coord);   // Step 2
        ConsumeWorldPoi(coord);

        // S5 (True Names): honor the pinned pre-read when one exists:
        // the archetype the attunement showed is the counterpart you meet.
        var encounter = PinnedNegotiationFor(coord);
        if (encounter == null)
        { ShowInfo("A potential contact slips away."); UpdateUI(); return; }

        NegotiationContext.Clear();
        NegotiationContext.EncounterId = encounter.Id;
        NegotiationContext.HexCoordKey = $"{coord.X},{coord.Y}";
        NegotiationContext.NpcArchetype = encounter.Archetype.ToString();
        // Kingdom of the tile we're standing on: drives court-standing
        // starting tension and the deal-deed echo route. "" for wilds.
        NegotiationContext.OriginKingdomId = KingdomIdAt(coord);
        ConsumeBeguileIfArmed(); // S3

        var router = EncounterRouter.Instance;
        if (router != null)
        {
            router.SavedStepsRemaining = StepsRemaining;
            router.SavedCurrentHP = CurrentHP;
            router.SavedGoldEarned = GoldEarned;
            router.SavedSplinterEarned = SplinterEarned;
            router.SavedMaterialEarned = MaterialEarned;
            router.SavedSuppliesEarned = SuppliesEarned;
            router.SavedEncountersWon = EncountersWon;
            router.SavedPartyCoord = _party.CurrentCoord;
            router.SavedCombatHexCoord = coord;
            router.SavedCombatWasPatrolAmbush = false;
            router.SavedCombatPatrolArchmageId = "";
            router.HasPendingReturn = true;
        }
        _hasLastMove = false; // S3 (Retrace): scene swap forgets the last step
        SaveManager.SaveIfDirty();
        LogRun("negotiation_start", $"{encounter.Id} ({encounter.Archetype})", at: coord);
        ShowInfo($"Negotiation: {encounter.Title}");
        GetTree().ChangeSceneToFile("res://Scenes/Negotiation/NegotiationScene.tscn");
    }

    private void OnNegotiationReturned(Vector2I hexCoord)
    {
        if (NegotiationContext.DealAccepted)
        {
            int negGoldBefore = GoldEarned;
            GoldEarned = Mathf.Max(0, GoldEarned + NegotiationContext.GoldDelta);

            // Supplies bargained at the table (docs/supply_cache_spec_v1): GAINS
            // ride home with the party (SuppliesEarned, at risk until extraction);
            // COSTS come out of the treasury immediately: you pledged from
            // stores, and the treasury can't go below empty.
            if (NegotiationContext.SuppliesDelta > 0)
            {
                SuppliesEarned += NegotiationContext.SuppliesDelta;
            }
            else if (NegotiationContext.SuppliesDelta < 0)
            {
                var supSave = SaveManager.ActiveSave;
                if (supSave != null)
                {
                    supSave.Supplies = Mathf.Max(0, supSave.Supplies + NegotiationContext.SuppliesDelta);
                    SaveManager.MarkDirty();
                }
            }
            // Steps bargained at the table (safe passage, a guide, an opened
            // gate) pay in expedition range, applied to the live budget on
            // return, same shape as NarrativeChoice.StepDelta, floored at 0.
            // May exceed OperatingRange, exactly like the pre-expedition
            // BonusSteps path; the range label shows the overrun honestly.
            if (NegotiationContext.StepsDelta != 0)
                StepsRemaining = Mathf.Max(0, StepsRemaining + NegotiationContext.StepsDelta);

            LogRun("negotiation_end",
                   $"deal signed: {NegotiationContext.EncounterId}" +
                   $" (rep {(NegotiationContext.ReputationDelta >= 0 ? "+" : "")}{NegotiationContext.ReputationDelta})",
                   goldDelta: GoldEarned - negGoldBefore, at: hexCoord);
            var cycle = SaveManager.ActiveSave?.Cycle;
            string kingdom = NegotiationContext.OriginKingdomId;
            bool kingdomAligned = cycle != null &&
                                  !string.IsNullOrEmpty(kingdom) &&
                                  cycle.Kingdoms.ContainsKey(kingdom);

            // Supply-lines intel: the signed charts reveal every cache in the
            // origin kingdom on the strategic map (supply_cache spec v1.1).
            if (NegotiationContext.RevealSupplyCaches && kingdomAligned)
            {
                int marked = SupplyCacheSystem.RevealCachesInKingdom(cycle, kingdom);
                if (marked > 0)
                {
                    ShowInfo($"Their quartermaster marks {marked} supply " +
                             $"cache{(marked == 1 ? "" : "s")} on your map.");
                    // A charted tile inside the live window should silhouette
                    // now, not after the next stream (same as the Spymaster
                    // packet's refresh).
                    RefreshWindowSilhouettes();
                }
            }
            if (kingdomAligned)
            {
                // Kingdom-aligned: the deal echoes to the court (C4). Routed
                // on OriginKingdomId (the tile's kingdom), NOT the authored
                // FactionId: encounter JSONs carry non-kingdom faction keys,
                // so keying on FactionId here was structurally dead (Session D).
                // FactionReputation no longer stores kingdom feeling; court
                // standing is the single source of truth.
                int rep = NegotiationContext.ReputationDelta;
                // §6a (Q4 ruling): when the court's own voice sat at the
                // table (a courtier of the counterpart's archetype), Regard
                // moved there and then; the deal-deed echo is REPLACED, not
                // doubled. NegotiationManager.SettleRegardAtTable sets this.
                if (rep != 0 && !NegotiationContext.RegardSettledAtTable)
                {
                    string tag = (rep > 0 ? CouncilEcho.DealFair : CouncilEcho.DealExploit)
                                 + ":" + NegotiationContext.NpcArchetype;
                    string toast = CouncilEcho.EmitDeed(cycle, kingdom, tag, rep > 0, isMajor: false);
                    if (toast != null)
                        ShowInfo(toast);
                }
            }
            else if (SaveManager.ActiveSave != null)
            {
                // Non-kingdom faction (wilds, convergence, faction-specific
                // NPC): FactionReputation keeps its job, keyed by the
                // encounter's authored FactionId.
                string f = NegotiationContext.FactionId;
                if (!string.IsNullOrEmpty(f))
                {
                    var repDict = SaveManager.ActiveSave.FactionReputation;
                    repDict[f] = repDict.TryGetValue(f, out int cur)
                        ? cur + NegotiationContext.ReputationDelta
                        : NegotiationContext.ReputationDelta;
                }
            }
            // S4 (§11): a deal closed in the Cordial zone can carry tuition,
            // the social route to spells. NegotiationState grants only on
            // Cordial (see GetSpellOutcome); here we just learn and say so.
            string taught = "";
            if (!string.IsNullOrEmpty(NegotiationContext.SpellGranted))
            {
                var grimD = SaveManager.ActiveSave?.Cycle?.Grimoire;
                if (grimD != null && SpellAcquisition.Learn(grimD, NegotiationContext.SpellGranted))
                {
                    var taughtDef = OverworldSpellRegistry.Get(NegotiationContext.SpellGranted);
                    taught = $"  They teach you {taughtDef?.Name}.";

                    // TUITION → SchoolMastery (design doc §4). Being *taught* by
                    // someone of a school makes you marginally more fluent in it,
                    // and this is the only acquisition route that pays: finding a
                    // working in a ruin or hearing it from the dead is discovery,
                    // not instruction, so neither of those award anything.
                    //
                    // Unfarmable by construction: SpellAcquisition.Learn returns
                    // false for anything already known, and spell knowledge is now
                    // PERMANENT (SpellKnowledgeService), so each spell can pay
                    // tuition exactly once, ever, across every timeline.
                    //
                    // "General" spells belong to no school and pay nothing; the
                    // TryParse is what filters them, since General is not a
                    // CardSchool member.
                    // System.Enum fully qualified: this file's usings are only
                    // Godot and System.Collections.Generic, and adding `using
                    // System;` to a 4,000-line file risks new ambiguities.
                    if (taughtDef != null &&
                        System.Enum.TryParse<CardSchool>(taughtDef.School, true, out var taughtSchool))
                    {
                        SchoolMasteryService.Award(SaveManager.ActiveSave,
                            taughtSchool.ToString(), SchoolMasteryService.PointsTuition,
                            $"tuition: taught '{taughtDef.Id}' at a cordial table");

                        // Card tuition (§8, "Negotiate → tuition"): a teacher of YOUR
                        // OWN school also imparts a technique: an unknown in-school
                        // Rare enters the pool. Off-school teachers pay only the spell
                        // + SchoolMastery above (§2a: off-school pays access, not
                        // cards), so this is gated to same-school instruction. Bounded
                        // by the spell-teaching beat (each spell teaches once ever), so
                        // it cannot be farmed as a standalone card faucet.
                        string curSchool = SaveManager.ActiveSave?.Cycle?.SelectedSchool ?? "";
                        if (System.Enum.TryParse<CardSchool>(curSchool, true, out var curParsed) &&
                            curParsed == taughtSchool)
                        {
                            string rareId = CardAcquisition.RollUnknownInSchoolRare(
                                SaveManager.ActiveSave, curSchool);
                            if (!string.IsNullOrEmpty(rareId))
                            {
                                string cardName = CardAcquisition.Discover(
                                    SaveManager.ActiveSave, rareId);
                                if (!string.IsNullOrEmpty(cardName))
                                    taught += $"  They show you the {cardName}, too.";
                            }
                        }
                    }
                }
            }
            // S5 (§7f/§6a): a compulsion table that CLOSES CORDIALLY buries
            // the compulsion echo before it lands: same gate as tuition
            // (DealAccepted ∧ Cordial). Walking away, strained deals, and
            // collapses all let the story reach the court.
            string buried = "";
            if (NegotiationContext.FromCompulsion && NegotiationContext.ResolvedCordial &&
                CouncilEcho.CancelDeed(SaveManager.ActiveSave?.Cycle?.Council,
                    NegotiationContext.OriginKingdomId, CouncilEcho.PatrolCompelled))
                buried = "  The patrol parts on good terms. That story dies here.";

            // Sentiment: a kingdom-aligned deal shifts the region's archmage.
            // Fair deal (positive rep) = favor; exploitative = disfavor.
            if (kingdomAligned)
            {
                var sentCampaign = SaveManager.ActiveSave?.Cycle?.Campaign;
                if (sentCampaign != null)
                {
                    var kState = cycle.Kingdoms[kingdom];
                    string sentArch = sentCampaign.GetArchmageForRegion(kState.TemplateRegionId);
                    if (!string.IsNullOrEmpty(sentArch))
                    {
                        int sentDelta = NegotiationContext.ReputationDelta > 0 ? +5
                                      : NegotiationContext.ReputationDelta < 0 ? -5 : 0;
                        if (sentDelta != 0)
                            sentCampaign.ShiftSentiment(sentArch, sentDelta);
                    }
                }
            }

            // Quest event shim (step 1 spec; raise finally wired 2026-07-23):
            // qe_negotiation_deal (+kingdom variant) for quest gating and the
            // Seraphine unlock.
            foreach (var qt in QuestEvents.Raise(QuestEvents.NegotiationDeal,
                     kingdomAligned ? kingdom : null))
                _toasts?.Push(qt.Text, qt.Kind);

            ShowInfo($"Deal struck. Gold: {(NegotiationContext.GoldDelta >= 0 ? "+" : "")}{NegotiationContext.GoldDelta}{taught}{buried}");
        }
        else
        {
            LogRun("negotiation_end",
                   $"no deal: {NegotiationContext.EncounterId}"
                   + (NegotiationContext.Escalated ? " (escalating)" : ""), at: hexCoord);
            foreach (var qt in QuestEvents.Raise(QuestEvents.NegotiationWalkaway,
                     NegotiationContext.OriginKingdomId))
                _toasts?.Push(qt.Text, qt.Kind);

            // Resolution Check (negotiation_system.docx): a table that hit maximum
            // tension against an aggressive counterpart becomes a fight. Read the
            // flag and the encounter id BEFORE Clear(): CommitCombat only queues
            // the scene change (Godot defers it to end of frame), so everything
            // below this point still executes.
            if (NegotiationContext.Escalated)
            {
                string escalatedFrom = NegotiationContext.EncounterId;
                NegotiationContext.Clear();
                LaunchNegotiationEscalation(hexCoord, escalatedFrom);
                UpdateUI();
                return;
            }
            ShowInfo("No deal reached.");
        }
        NegotiationContext.Clear();
        UpdateUI();
    }

    // ════════════════════════════════════════════════════════════════════
    // Extraction / failure
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Extract-button router (W3 ruling): free extraction only while
    /// standing ON a supply anchor; anywhere else offers the emergency path
    /// behind a confirm. The return leg is the tension the step budget was
    /// built for: walking home is the cheap way out.</summary>
    private void OnExtractPressed()
    {
        if (ExpeditionComplete)
            return;
        if (OnSupplyAnchor())
        {
            Extract();
            return;
        }
        _emergencyConfirm?.PopupCentered();
    }

    /// <summary>W3 emergency extraction: the party abandons the field and
    /// straggles home. Costs: +1 lunation (CycleState.PendingStraggleLunations,
    /// advanced with the full world tick by StrategicView on return) and one
    /// §5b roll per companion at the tier-2 band (15% death, Sworn −10; the
    /// rest injured 1–2 lunations). AMENDS K2.5's "no death risk outside
    /// losing fights": this is the price of extraction beyond the line.
    /// Spoils and discoveries ARE kept: the cost is time and bodies, not loot.</summary>
    private void EmergencyExtract(string reason = null)
    {
        if (ExpeditionComplete)
            return;
        if (_striding) EndStride(null);   // a run-end cancels any march
        ExpeditionComplete = true;
        PlayerSession.IsOnExpedition = false;

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        OverworldSpellEffects.Clear(); // S2: timed spell windows end with the expedition
        WeatherSystem.Reset();         // W1: weather fronts end with the expedition
        VisionModifiers.Reset();       // W2: clear the weather scry penalty
        _identifiedEncounters.Clear(); // S4: Identify pins end with it too
        _pinnedNegotiations.Clear();   // S5: True Names pre-reads likewise

        _casualtyNote = CompanionInjurySystem.ApplyWipe(SaveManager.ActiveSave,
            territoryTier: 2, bossContext: false, "emergency extraction");
        CompanionInjurySystem.ResetExpeditionHP(SaveManager.ActiveSave);

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle != null)
            cycle.PendingStraggleLunations += 1;

        BankResources(extracted: true);
        string casualties = string.IsNullOrEmpty(_casualtyNote) ? "" : $" {_casualtyNote}";
        string emReason = string.IsNullOrEmpty(reason) ? "Emergency recall." : reason;
        RunEventLog.End("emergency_extract",
            $"{emReason} straggled home, +1 lunation.{casualties}",
            GoldEarned, SplinterEarned, EncountersWon, CurrentHP, StepsRemaining,
            goldBanked: true, materials: MaterialEarned, supplies: SuppliesEarned);
        // §2.2 turnaround: the recall IS the refuel/restock/unload/repair, the
        // narration for the existing lunation cadence (no new timer).
        ShowInfo($"{emReason} The castle limps home to refuel, restock, unload, and repair, and a lunation will pass. " +
                 $"Gold: {GoldEarned}, Splinters: {SplinterEarned}.{casualties}");
        _casualtyNote = null;
        ShowReturnButton();
        EmitSignal(SignalName.ExpeditionEnded, true);
    }

    /// <summary>Voluntary or range-forced extraction: bank everything, save,
    /// return to the strategic view. Discoveries are already in World.</summary>
    private void Extract()
    {
        if (ExpeditionComplete)
            return;
        if (_striding) EndStride(null);   // a run-end cancels any march
        ExpeditionComplete = true;
        PlayerSession.IsOnExpedition = false;

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        OverworldSpellEffects.Clear(); // S2: timed spell windows end with the expedition
        WeatherSystem.Reset();         // W1: weather fronts end with the expedition
        VisionModifiers.Reset();       // W2: clear the weather scry penalty
        _identifiedEncounters.Clear(); // S4: Identify pins end with it too
        _pinnedNegotiations.Clear();   // S5: True Names pre-reads likewise

        // K4: loyalty homecoming (+1 fielded, +2 heroism for the stabilized)
        // BEFORE the extraction check: ApplyExtractionCheck resets
        // ExpeditionHP, which is the heroism evidence. Then the Cunning
        // Finder's Fee lands before banking.
        LoyaltyEvents.OnExtraction(SaveManager.ActiveSave);
        GoldEarned += CompanionPerks.ExtractionGold(SaveManager.ActiveSave);

        // K2.5 ruling: extraction infirmary check: who came home broken?
        // Stabilized (downed in a won fight) → 1–2 lunations; below 25% of
        // BaseHP → 1. Resets ExpeditionHP. No death risk on extraction.
        string extractCasualties = CompanionInjurySystem.ApplyExtractionCheck(SaveManager.ActiveSave);

        BankResources(extracted: true);
        RunEventLog.End("extracted",
            $"voluntary extraction at supply anchor.{(string.IsNullOrEmpty(extractCasualties) ? "" : " " + extractCasualties)}",
            GoldEarned, SplinterEarned, EncountersWon, CurrentHP, StepsRemaining,
            goldBanked: true, materials: MaterialEarned, supplies: SuppliesEarned);
        ShowInfo($"The castle docks to refuel, restock, unload, and make repairs. " +
                 $"Gold: {GoldEarned}, Splinters: {SplinterEarned}" +
                 $"{(SuppliesEarned != 0 ? $", Supplies: {SuppliesEarned}" : "")}" +
                 $"{(MaterialEarned != 0 ? $", Materials: {MaterialEarned}" : "")}" +
                 $", Encounters: {EncountersWon}." +
                 $"{(string.IsNullOrEmpty(extractCasualties) ? "" : " " + extractCasualties)}");
        ShowReturnButton();
        EmitSignal(SignalName.ExpeditionEnded, true);
    }

    private void FailExpedition(string reason, bool injuriesAlreadyRolled = false)
    {
        if (ExpeditionComplete)
            return;
        if (_striding) EndStride(null);   // a run-end cancels any march
        ExpeditionComplete = true;
        PlayerSession.IsOnExpedition = false;

        // K2 (§5b): the pool hit 0, an expedition wipe. One roll per fielded
        // companion at the territory tier under the party's feet. Skipped when
        // the combat-loss return already rolled this wipe (one roll per wipe).
        if (!injuriesAlreadyRolled)
            _casualtyNote = CompanionInjurySystem.ApplyWipe(SaveManager.ActiveSave,
                TerritoryTierAt(_party?.CurrentCoord ?? Vector2I.Zero),
                bossContext: false, reason);

        // K2.5: expedition over. The wipe rolls above are the injury
        // accounting on this path; carried HP just clears.
        CompanionInjurySystem.ResetExpeditionHP(SaveManager.ActiveSave);
        OverworldSpellEffects.Clear(); // S2: timed spell windows end with the expedition
        WeatherSystem.Reset();         // W1: weather fronts end with the expedition
        VisionModifiers.Reset();       // W2: clear the weather scry penalty
        _identifiedEncounters.Clear(); // S4: Identify pins end with it too
        _pinnedNegotiations.Clear();   // S5: True Names pre-reads likewise

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        // Failure still banks DISCOVERY (it's in World) but forfeits unbanked gold.
        BankResources(extracted: false);
        // The casualty note makes the human cost part of the banner: WHO was
        // hurt and for how long, not just that the run died (K2 UX).
        string casualties = string.IsNullOrEmpty(_casualtyNote) ? "" : $" {_casualtyNote}";
        RunEventLog.End("failed", $"{reason}{casualties}",
            GoldEarned, SplinterEarned, EncountersWon, CurrentHP, StepsRemaining,
            goldBanked: false, materials: MaterialEarned, supplies: SuppliesEarned);
        ShowInfo($"Expedition failed: {reason} Discoveries retained; unbanked spoils lost.{casualties}");
        _casualtyNote = null;
        ShowReturnButton();
        EmitSignal(SignalName.ExpeditionEnded, false);
    }

    /// <summary>Write expedition results into the cycle save. Discovery is already
    /// resident in World; this handles the economy + stats.</summary>
    private void BankResources(bool extracted)
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;

        save.TotalRuns++;
        save.TotalEncountersWon += EncountersWon;
        save.TotalGoldEarned += GoldEarned;

        if (extracted)
        {
            save.Gold += GoldEarned;
            save.ArcaneSplinters += SplinterEarned;
            save.BuildMaterials += MaterialEarned;
            save.Supplies += SuppliesEarned;
            save.RunsWon++;
        }
        else
        {
            // Failure forfeits ALL unbanked spoils: gold, splinters, and
            // materials (2026-08-05 ruling, made alongside the top-bar at-risk
            // readouts: the bar shows all three as losable, so they must be).
            // Map discoveries are the only thing retained. Previously splinters
            // survived failure ("discoveries retained, spoils lost"); splinters
            // are now spoils, not discoveries.
            save.RunsLost++;
        }

        RunResultData.Set(extracted, GoldEarned, EncountersWon, CurrentHP, SplinterEarned);
        SaveManager.Save();
    }

    // ════════════════════════════════════════════════════════════════════
    // HUD
    // ════════════════════════════════════════════════════════════════════

    private CanvasLayer _hudCanvas;
    private CanvasLayer GetHudCanvas() => _hudCanvas;

    private void BuildHud()
    {
        _hudCanvas = new CanvasLayer { Name = "UI" };
        AddChild(_hudCanvas);

        var hudPanel = new PanelContainer
        {
            OffsetLeft = 12,
            OffsetTop = 12 + HudManager.BarHeight, // clear the global top bar
            OffsetRight = 300,
            OffsetBottom = 12,
        };
        var hudStyle = new StyleBoxFlat
        {
            BgColor = UITheme.OverworldHudBg,
            BorderColor = UITheme.OverworldHudBorder,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        hudPanel.AddThemeStyleboxOverride("panel", hudStyle);
        _hudCanvas.AddChild(hudPanel);
        _uiHoverBlockers.Add(hudPanel); // S4.2: stat cluster blocks tile hover

        // Hover tooltip: follows the mouse, names the tile under it (fog-gated).
        _hoverTooltip = new Label { Visible = false, ZIndex = 100 };
        _hoverTooltip.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        _hoverTooltip.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        _hoverTooltip.AddThemeColorOverride("font_outline_color", UITheme.WorldDeep);
        _hoverTooltip.AddThemeConstantOverride("outline_size", 5);
        _hudCanvas.AddChild(_hoverTooltip);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        hudPanel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        margin.AddChild(vbox);

        _objectiveLabel = MakeHudLabel();
        _objectiveLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _objectiveLabel.Visible = false;
        vbox.AddChild(_objectiveLabel);
        _objectiveSeparator = new HSeparator { Visible = false };
        vbox.AddChild(_objectiveSeparator);

        _stepLabel = MakeHudLabel();
        vbox.AddChild(_stepLabel);
        // Mobile Fortress §3.1: the fuel gauge rendered as the castle's furnace
        // dial, a thin ember-lit bar under the readout. Denominator is MaxFuel.
        _fuelGauge = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 6),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        var fuelBg = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.08f, 0.05f),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        var fuelFill = new StyleBoxFlat
        {
            BgColor = new Color(0.95f, 0.55f, 0.15f), // furnace ember
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        _fuelGauge.AddThemeStyleboxOverride("background", fuelBg);
        _fuelGauge.AddThemeStyleboxOverride("fill", fuelFill);
        vbox.AddChild(_fuelGauge);
        _hpLabel = MakeHudLabel();
        vbox.AddChild(_hpLabel);
        // S2: the second scarcity, read beside the first (§12).
        _essenceLabel = MakeHudLabel();
        _essenceLabel.AddThemeColorOverride("font_color", UITheme.EssenceText);
        vbox.AddChild(_essenceLabel);
        // Mobile Fortress weather (W1): the front standing over the castle.
        _weatherLabel = MakeHudLabel();
        vbox.AddChild(_weatherLabel);
        vbox.AddChild(new HSeparator());
        _windowLabel = MakeHudLabel();
        vbox.AddChild(_windowLabel);
        vbox.AddChild(new HSeparator());
        _infoLabel = MakeHudLabel();
        _infoLabel.Modulate = UITheme.OverworldInfoLabelTint;
        _infoLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_infoLabel);

        // Extract button. W3: free extraction only ON a supply anchor; anywhere
        // else routes through the emergency-extraction confirm (OnExtractPressed).
        _extractButton = new Button
        {
            Text = "Extract",
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetLeft = -150,
            OffsetRight = -12,
            OffsetTop = 12 + HudManager.BarHeight, // clear the global top bar
            OffsetBottom = 52 + HudManager.BarHeight,
        };
        _extractButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(_extractButton, isPrimary: true);

        _extractButton.Pressed += OnExtractPressed;
        _hudCanvas.AddChild(_extractButton);

        // §3.4 Stride: a Halt button appears (top-centre) only while marching.
        _haltButton = new Button
        {
            Text = "■ Halt",
            Visible = false,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetLeft = -72, OffsetRight = 72,
            OffsetTop = 12 + HudManager.BarHeight,
            OffsetBottom = 52 + HudManager.BarHeight,
        };
        _haltButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(_haltButton, isPrimary: true);
        _haltButton.Pressed += CancelStride;
        _hudCanvas.AddChild(_haltButton);

        // W3: emergency-extraction confirm. Free extraction happens only on a
        // supply anchor; anywhere else the party straggles home at real cost.
        _emergencyConfirm = new ConfirmationDialog
        {
            Title = "Emergency Extraction",
            DialogText = "You are away from any supply anchor. The party abandons\n" +
                         "the field and straggles home overland:\n\n" +
                         "  · One full lunation passes before you reach the campus.\n" +
                         "  · Every companion risks injury, or worse, on the road.\n\n" +
                         "Spoils and discoveries are kept. Extract anyway?",
            OkButtonText = "Extract",
        };
        _emergencyConfirm.Confirmed += () => EmergencyExtract();
        _hudCanvas.AddChild(_emergencyConfirm);

        // Ledger button (C3), stacked under Extract.
        _ledgerButton = new Button
        {
            Text = "Ledger",
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetLeft = -150,
            OffsetRight = -12,
            OffsetTop = 60 + HudManager.BarHeight, // clear the global top bar
            OffsetBottom = 100 + HudManager.BarHeight,
        };
        _ledgerButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(_ledgerButton, isPrimary: false);
        _ledgerButton.Pressed += () => _ledgerPanel?.Toggle();
        _hudCanvas.AddChild(_ledgerButton);

        // 3D view toggle (Stage 3), stacked under Ledger. A real player-facing
        // control: flips this run between the 2D map and the 3D expedition view and
        // remembers the choice (PlayerSession.ExpeditionView3D), so the next deploy
        // launches into the same view. M / Esc mirror it. Label is set by
        // UpdateView3DButton to reflect what a press will do.
        _view3DButton = new Button
        {
            Text = "Switch to 3D",
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetLeft = -150,
            OffsetRight = -12,
            OffsetTop = 108 + HudManager.BarHeight,  // third row under Extract / Ledger
            OffsetBottom = 148 + HudManager.BarHeight,
        };
        _view3DButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(_view3DButton, isPrimary: false);
        _view3DButton.Pressed += OnView3DTogglePressed;
        _hudCanvas.AddChild(_view3DButton);

        // Scout panel.
        _scoutPanel = new ScoutReportPanel { Name = "ScoutPanel" };
        _hudCanvas.AddChild(_scoutPanel);

        // Return button (hidden until expedition ends).
        _returnButton = new Button
        {
            Text = "Return to Strategic Map",
            Visible = false,
            AnchorLeft = 0.5f,
            AnchorTop = 0.82f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.82f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -150,
            OffsetRight = 150,
            OffsetTop = -26,
            OffsetBottom = 26,
        };
        _returnButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(_returnButton, isPrimary: true);
        _returnButton.Pressed += () => GetTree().ChangeSceneToFile(StrategicScenePath);
        _hudCanvas.AddChild(_returnButton);

        // S4.2: every clickable HUD surface blocks the tile hover readout.
        // (Modal panels (scout report, narrative) are caught by the
        // hovered-control query; listing them too costs nothing.)
        _uiHoverBlockers.Add(_extractButton);
        _uiHoverBlockers.Add(_ledgerButton);
        _uiHoverBlockers.Add(_returnButton);
        _uiHoverBlockers.Add(_scoutPanel);
        _uiHoverBlockers.Add(_infoLabel);
    }

    private void ShowReturnButton()
    {
        if (_extractButton != null)
            _extractButton.Visible = false;
        if (_ledgerButton != null)
            _ledgerButton.Visible = false;
        if (_ledgerPanel != null)
            _ledgerPanel.Close();
        if (_returnButton != null)
            _returnButton.Visible = true;
    }

    /// <summary>Keeps the objective banner honest about (a) what the objective IS,
    /// (b) whether it has been met, and (c) what meeting it buys: the three things
    /// the warfront intervention silently decided on return
    /// (KingdomTickSimulation.ApplyIntervention). Success there requires BOTH
    /// ReachedObjective AND WarfrontStrongholdCleared, so an ordinary won fight at
    /// the front advances nothing; this line is what says so.</summary>
    /// <summary>How far the marked stronghold is from the party, as a clause to
    /// append to the objective banner. Distance only, no compass: the gold star
    /// answers WHICH mark, this answers HOW FAR, and a bearing derived from axial
    /// deltas would be guessing at the layout's orientation.</summary>
    private string StrongholdBearing()
    {
        if (_strongholdCol < 0 || _window == null || _grid == null || _party == null)
            return "";
        var local = _window.LocalOf(_strongholdCol, _strongholdRow);
        int d = _grid.Distance(_party.CurrentCoord, local);
        if (d <= 0)
            return " (you are on it)";
        return $", {d} hex{(d == 1 ? "" : "es")} out";
    }

    private void RefreshObjectiveBanner()
    {
        if (_objectiveLabel == null)
            return;
        if (!_isWarfront)
        {
            _objectiveLabel.Visible = false;
            if (_objectiveSeparator != null) _objectiveSeparator.Visible = false;
            return;
        }

        var cyc = SaveManager.ActiveSave?.Cycle;
        bool cleared = cyc?.WarfrontStrongholdCleared ?? false;
        string stake = (cyc?.PendingWarfrontSide ?? WarfrontSide.Defend) switch
        {
            WarfrontSide.Seize => "Seize: the guild's banner over the province.",
            WarfrontSide.Aid   => "Aid: drive the invasion home.",
            _                  => "Defend: push the invasion back.",
        };

        _objectiveLabel.Visible = true;
        if (_objectiveSeparator != null) _objectiveSeparator.Visible = true;
        _objectiveLabel.Text = cleared
            ? $"⚔ WARFRONT · {stake}\nThe stronghold has fallen. EXTRACT to secure it."
            : _strongholdCol >= 0
                ? $"⚔ WARFRONT · {stake}\nStorm the gold-starred stronghold{StrongholdBearing()}. Other fights here win you nothing."
                : $"⚔ WARFRONT · {stake}\nWin a fight at the front, then extract.";
        _objectiveLabel.Modulate = cleared ? Colors.White : UITheme.OverworldLowResourceWarning;
    }

    private Label MakeHudLabel()
    {
        var l = new Label { AutowrapMode = TextServer.AutowrapMode.Off };
        l.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        return l;
    }

    // ════════════════════════════════════════════════════════════════════
    // Process / camera / UI
    // ════════════════════════════════════════════════════════════════════

    public override void _Process(double delta)
    {
        if (ExpeditionComplete || _camera == null)
            return;
        HandleCameraPan((float)delta);
        PositionTooltip();
    }

    private void HandleCameraPan(float delta)
    {
        var dir = Vector2.Zero;
        if (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D))
            dir.X += 1f;
        if (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A))
            dir.X -= 1f;
        if (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S))
            dir.Y += 1f;
        if (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W))
            dir.Y -= 1f;
        if (dir != Vector2.Zero)
        {
            _cameraFreeMode = true;
            _camera.Position += dir.Normalized() * CameraPanSpeed * delta / _camera.Zoom.X;
        }
    }

    private void CenterCamera()
    {
        if (_camera != null)
        { _camera.Position = _party.Position; _cameraFreeMode = false; }
    }

    private void UpdateUI()
    {
        RefreshObjectiveBanner();

        bool unlimitedFuel = PlayerSession.DebugMode && PlayerSession.UnlimitedSteps;
        _stepLabel.Text = unlimitedFuel
            ? "Fuel: ∞ [DEBUG]"
            : $"Fuel: {StepsRemaining} / {MaxFuel}";
        _stepLabel.Modulate = StepsRemaining > 5 ? Colors.White : UITheme.OverworldLowResourceWarning;

        // Furnace dial: fill = fuel/MaxFuel (clamped; a negotiation overrun reads full).
        if (_fuelGauge != null)
        {
            double frac = (unlimitedFuel || MaxFuel <= 0) ? 1.0 : (double)StepsRemaining / MaxFuel;
            _fuelGauge.Value = frac < 0.0 ? 0.0 : (frac > 1.0 ? 1.0 : frac);
        }

        _hpLabel.Text = $"Hull: {Hull} / {MaxHull}";
        _hpLabel.Modulate = Hull > MaxHull / 3 ? Colors.White : UITheme.OverworldLowResourceWarning;

        // Mobile Fortress weather (W1): the front over the castle. Severe
        // fronts (severity ≥ 3) read in the warning tint; milder ones stay plain.
        if (_weatherLabel != null && _party != null)
        {
            var wt = WeatherSystem.Active ? WeatherSystem.WeatherAt(_party.CurrentCoord) : WeatherType.Clear;
            var wd = WeatherCatalog.Def(wt);
            // W2: name the front + its per-tile toll so the tradeoff is legible.
            var fx = new System.Collections.Generic.List<string>();
            if (wd.FuelPerTile != 0) fx.Add($"+{wd.FuelPerTile} fuel");
            bool cinderImmune = _castle != null && _castle.WeatherHullImmune;
            if (wd.HullPerTile != 0) fx.Add(cinderImmune ? "Hull immune" : $"-{wd.HullPerTile} Hull");
            if (wd.ScryDelta != 0) fx.Add($"scry {wd.ScryDelta}");
            string suffix = fx.Count > 0 ? $"  ({string.Join(", ", fx)})" : "";
            _weatherLabel.Text = $"Weather: {wd.Glyph} {wd.Name}{suffix}";
            _weatherLabel.Modulate = wd.Severity >= 3 ? UITheme.OverworldLowResourceWarning : Colors.White;
        }

        // S2: the Essence pool, beside the other scarcities (§12).
        var grimoire = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (_essenceLabel != null && grimoire != null)
        {
            _essenceLabel.Text = $"Essence: {grimoire.EssenceCurrent} / {grimoire.EssenceMax}";
            _essenceLabel.Modulate = grimoire.EssenceCurrent > 2
                ? Colors.White : UITheme.OverworldLowResourceWarning;
        }

        // W3: supply readout replaces the old fixed-window explored counter
        // (the loaded set now slides and grows; a ratio over it is noise).
        int supplyDist = SupplyDistanceAt(_party.CurrentCoord);
        int supplyBand = SupplyBandAt(_party.CurrentCoord);
        _windowLabel.Text = supplyBand == 0
            ? $"Supply: in range ({supplyDist}/{SupplyRange})"
            : $"Supply: {supplyDist - SupplyRange} beyond the line (−{supplyBand * LeashDrainPerBand} HP/step)";
        _windowLabel.Modulate = supplyBand == 0 ? Colors.White : UITheme.OverworldLowResourceWarning;

        if (_grid.Hexes.TryGetValue(_party.CurrentCoord, out var cur))
            _windowLabel.Text += $"  |  {TerrainAt(_party.CurrentCoord)}";   // Step 3
        string curKingdom = KingdomIdAt(_party.CurrentCoord);
        _windowLabel.Text += $"  |  {(string.IsNullOrEmpty(curKingdom) ? "Unclaimed" : KingdomDisplayName(curKingdom))}";

        // W3: the button tells the truth about which extraction you'd get.
        if (_extractButton != null && !ExpeditionComplete)
            _extractButton.Text = OnSupplyAnchor() ? "Extract" : "Emergency Extract";

        // S2: affordability / surcharge / active-effect readout.
        _grimoirePanel?.Refresh();
    }

    private void ShowInfo(string message)
    {
        _infoLabel.Text = message;
        GD.Print($"[Expedition] {message}");
    }

    /// <summary>RunEventLog bridge: stamps the event with the current resource
    /// totals and the party's WORLD coordinate (stable across windows). All
    /// expedition-side run logging funnels through here.</summary>
    /// <summary>The active party's companions, resolved from ids (§5 crew source).</summary>
    private System.Collections.Generic.List<Companion> ActivePartyCompanions()
    {
        var list = new System.Collections.Generic.List<Companion>();
        var save = SaveManager.ActiveSave;
        if (save?.ActivePartyCompanionIds == null || save.Companions == null)
            return list;
        foreach (var id in save.ActivePartyCompanionIds)
        {
            var c = save.Companions.Find(x => x.Id == id);
            if (c != null) list.Add(c);
        }
        return list;
    }

    /// <summary>One-line summary of the crew station assignment, for the run log.</summary>
    private string CrewSummary()
    {
        if (_crewAssign == null || _crewAssign.Count == 0)
            return "no crew";
        var parts = new System.Collections.Generic.List<string>();
        foreach (var kv in _crewAssign)
            parts.Add($"{CrewStations.StationName(kv.Key)}={kv.Value.Name}");
        return string.Join(", ", parts);
    }

    /// <summary>One-line summary of the seeded weather fronts, for the run log.</summary>
    private string WeatherSummary()
    {
        var counts = new System.Collections.Generic.Dictionary<WeatherType, int>();
        foreach (var f in WeatherSystem.Fronts)
        {
            counts.TryGetValue(f.Type, out int c);
            counts[f.Type] = c + 1;
        }
        if (counts.Count == 0)
            return "clear";
        var parts = new System.Collections.Generic.List<string>();
        foreach (var kv in counts)
            parts.Add($"{kv.Value}× {WeatherCatalog.Name(kv.Key)}");
        return $"fronts: {string.Join(", ", parts)}";
    }

    /// <summary>Field refueling (§3.2). Adds fuel, clamped to MaxFuel (never
    /// exceeds the tank), logs the gain as a fuel line, and refreshes the HUD.
    /// A pre-existing negotiation overrun above MaxFuel is left untouched. This
    /// only tops the tank up, it never trims. Pass a negative/zero amount and it
    /// no-ops. `full: true` fills to MaxFuel regardless of `amount`.</summary>
    private void Refuel(int amount, string source, Vector2I? at = null, bool full = false)
    {
        int before = StepsRemaining;
        int target = full ? MaxFuel : StepsRemaining + Mathf.Max(0, amount);
        // Only ever raise toward MaxFuel; never lower an existing overrun.
        StepsRemaining = Mathf.Max(before, Mathf.Min(MaxFuel, target));
        int gained = StepsRemaining - before;
        if (gained > 0)
            LogRun("refuel", source, stepsDelta: gained, at: at);
    }

    private void LogRun(string type, string detail,
                        int goldDelta = 0, int splinterDelta = 0,
                        int hpDelta = 0, int stepsDelta = 0, Vector2I? at = null)
    {
        string coord = "";
        Vector2I? local = at ?? (_party != null ? _party.CurrentCoord : (Vector2I?)null);
        if (local.HasValue && _window != null &&
            _window.TryLocalToWorld(local.Value, out int wc, out int wr))
            coord = $"{wc},{wr}";
        RunEventLog.Event(type, detail, goldDelta, splinterDelta, hpDelta, stepsDelta,
                          GoldEarned, SplinterEarned, CurrentHP, StepsRemaining, coord);
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private string StagingKingdom()
        => _world.GetTile(_stagingCol, _stagingRow).KingdomId ?? "frontier_wilds";

    /// <summary>The content template region for the staging kingdom: the real
    /// region name (e.g. "frontier_wilds") that encounter/narrative pools are
    /// filed under, NOT the "kingdom_N" id. Resolves via the kingdom's
    /// TemplateRegionId set at world generation; falls back to the borderlands.</summary>
    private string StagingTemplateRegion()
    {
        string kid = StagingKingdom();
        if (_world != null && SaveManager.ActiveSave?.Cycle?.Kingdoms != null &&
            SaveManager.ActiveSave.Cycle.Kingdoms.TryGetValue(kid, out var ks) &&
            !string.IsNullOrEmpty(ks.TemplateRegionId))
        {
            return ks.TemplateRegionId;
        }
        return "frontier_wilds";
    }

    // ── Archmage faction encounters ─────────────────────────────────────

    /// <summary>The non-villain archmage definition for an id, or null.</summary>
    private ArchmageDefinition ArchmageDefById(string archmageId)
    {
        if (string.IsNullOrEmpty(archmageId))
            return null;
        var def = ArchmageRegistry.Get(archmageId);
        return (def == null || def.IsVillainFaction) ? null : def;
    }

    /// <summary>Archmage controlling the kingdom that owns the given window-local
    /// tile, or "" if none. Per-tile (NOT staging-keyed) so a border-straddling
    /// window fights whoever actually holds the ground you're standing on.</summary>
    private string KingdomArchmageAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return "";
        string kid = _world.GetTile(col, row).KingdomId ?? "";
        if (!string.IsNullOrEmpty(kid) &&
            SaveManager.ActiveSave?.Cycle?.Kingdoms != null &&
            SaveManager.ActiveSave.Cycle.Kingdoms.TryGetValue(kid, out var ks))
            return ks.ArchmageId ?? "";
        return "";
    }

    /// <summary>Roll the resident archmage's ArchmageFactionChance for an ordinary
    /// combat POI. Returns the archmage to draw from, or null to use the region pool.</summary>
    private ArchmageDefinition RollArchmageAt(Vector2I local)
    {
        var def = ArchmageDefById(KingdomArchmageAt(local));
        if (def == null)
            return null;
        return GD.Randf() < def.ArchmageFactionChance ? def : null;
    }

    /// <summary>Combined enemy difficulty multiplier for a window-local tile:
    /// the tile's kingdom's region-template EnemyDifficultyMult × a positional
    /// factor from the kingdom's Tier (1→1.0, 2→1.25, 3→1.5). Per-tile (NOT
    /// staging-keyed) so a border-straddling window scales to the ground you're
    /// on. Used only for the REGION pool; archmage groups carry their own
    /// authored difficulty (see OpenScoutReport / OnPatrolCapturedPlayer).</summary>
    private float DifficultyMultAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return 1.0f;
        string kid = _world.GetTile(col, row).KingdomId ?? "";
        if (string.IsNullOrEmpty(kid) ||
            SaveManager.ActiveSave?.Cycle?.Kingdoms == null ||
            !SaveManager.ActiveSave.Cycle.Kingdoms.TryGetValue(kid, out var ks))
            return 1.0f;

        float regionMult = RegionLoader.LoadOrDefault(ks.TemplateRegionId)?.EnemyDifficultyMult ?? 1.0f;
        float tierFactor = ks.Tier switch
        {
            <= 1 => 1.0f,
            2 => 1.25f,
            _ => 1.5f,   // tier 3+
        };
        // Continue-campaign escalation: the timeline's accumulated threat hardens
        // every region encounter (progression_persistence_model_v1.md §6). 1.0 in
        // a fresh Year-1 timeline; +ThreatDifficultyStep per continued year.
        float threatMult = CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle);
        return regionMult * tierFactor * threatMult;
    }

    /// <summary>K2 (§5b): territory tier (1–3) at a window-local tile: the
    /// injury/death roll severity. Same kingdom lookup as DifficultyMultAt;
    /// unclaimed ground rolls at tier 1.</summary>
    private int TerritoryTierAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return 1;
        string kid = _world.GetTile(col, row).KingdomId ?? "";
        if (string.IsNullOrEmpty(kid) ||
            SaveManager.ActiveSave?.Cycle?.Kingdoms == null ||
            !SaveManager.ActiveSave.Cycle.Kingdoms.TryGetValue(kid, out var ks))
            return 1;
        return Mathf.Clamp(ks.Tier, 1, 3);
    }

    /// <summary>Map a stored grid-local coord through the window (identity: the
    /// window rebuild uses the same staging point, so local coords are stable
    /// even across slides: the local frame is a fixed translation of world axial).</summary>
    private Vector2I GridLocalOf(Vector2I savedLocal) => savedLocal;

    // ════════════════════════════════════════════════════════════════════
    // S2: spell façade: OverworldSpellManager dispatches effects into
    // these; world mutation stays HERE (the manager owns decisions, not
    // the world; overworld_spell_system §13).
    // ════════════════════════════════════════════════════════════════════

    public Vector2I PartyLocal => _party?.CurrentCoord ?? Vector2I.Zero;
    public WorldData WorldRef => _world;
    public WorldWindowBuilder WindowRef => _window;

    public void SpellInfo(string message) => ShowInfo(message);
    public void SpellRefreshHud() => UpdateUI();

    public int SpellCorruptionTierAtParty()
        => _party != null ? CorruptionTierAt(_party.CurrentCoord) : 0;

    public string SpellKingdomAtParty()
        => _party != null ? KingdomIdAt(_party.CurrentCoord) : "";

    /// <summary>Heal the party pool (Mending Cant, Minor Working).</summary>
    public void SpellHealParty(int amount)
    {
        CurrentHP = Mathf.Min(CurrentHP + Mathf.Max(0, amount), MaxHP);
        UpdateUI();
    }

    /// <summary>Chart a hex disc into the world (Unseen → Charted only, G2;
    /// never touches Charted/Explored). Optional terrain filter (Tremorsense).
    /// Returns tiles charted; refreshes window silhouettes when > 0.</summary>
    public int SpellChartHexRadius(int col, int row, int radius,
        System.Collections.Generic.List<OverworldHex.TerrainType> terrainFilter = null)
    {
        int charted = 0;
        foreach (var (c, r) in _world.Disc(col, row, radius))
        {
            if (!_world.TryIndex(c, r, out int idx))
                continue;
            if (terrainFilter != null && !terrainFilter.Contains(_world.Tiles[idx].Terrain))
                continue;
            if (_world.Tiles[idx].Discovery == TileDiscovery.Unseen)
            {
                _world.Tiles[idx].Discovery = TileDiscovery.Charted;
                charted++;
            }
        }
        if (charted > 0)
        {
            SaveManager.MarkDirty();
            RefreshWindowSilhouettes();
        }
        return charted;
    }

    /// <summary>Force Path (Elementalist): open one impassable hex. Mountain
    /// shatters to Hills; water freezes/fords to Marsh: passable but boggy,
    /// the "may carry a hazard" clause priced as Marsh's HP drain. Writes the
    /// WORLD tile: a physically opened passage persists for the cycle.</summary>
    public bool SpellForcePath(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return false;
        if (!_world.TryIndex(col, row, out int idx))
            return false;

        var t = _world.Tiles[idx].Terrain;
        OverworldHex.TerrainType opened;
        if (t == OverworldHex.TerrainType.Mountain)
            opened = OverworldHex.TerrainType.Hills;
        else if (TerrainClass.IsWater(t))
            opened = OverworldHex.TerrainType.Marsh;
        else
            return false;

        _world.Tiles[idx].Terrain = opened;
        if (_grid.Hexes.TryGetValue(local, out var hexNode))
        {
            hexNode.Terrain = opened;
            hexNode.RefreshVisuals();
        }
        SaveManager.MarkDirty();
        return true;
    }

    /// <summary>Draw a Wayfarer's Beacon marker at a grid-local coord. The
    /// marker is a direct grid child at a fixed position, so it survives
    /// window slides (its hex node may unload; the mark remains. That is
    /// the point of a beacon). Persistence lives in GrimoireState.</summary>
    public void SpellDrawBeaconMarker(Vector2I local)
    {
        var marker = new Node2D { Name = "BeaconMarker", ZIndex = 6 };
        var body = new Polygon2D
        {
            Polygon = new[]
            {
                new Vector2(0, -12), new Vector2(8, 0),
                new Vector2(0, 12), new Vector2(-8, 0),
            },
            Color = UITheme.BeaconMark,
        };
        var outline = new Polygon2D
        {
            Polygon = new[]
            {
                new Vector2(0, -15), new Vector2(10.5f, 0),
                new Vector2(0, 15), new Vector2(-10.5f, 0),
            },
            Color = new Color(0f, 0f, 0f, 0.7f),
            ZIndex = -1,
        };
        marker.AddChild(outline);
        marker.AddChild(body);
        marker.Position = _grid.AxialToWorld(local);
        _grid.AddChild(marker);
    }

    // ── S3 façade additions ──────────────────────────────────────────────

    /// <summary>Retrace (Chronomancer, THE sole G1 exception, once/expedition):
    /// undo the last committed movement step: position restored, charged step
    /// cost refunded. HP drains are NOT refunded (time reclaims the ground,
    /// not the toll). False when there is no step to undo.</summary>
    /// <summary>True when a last step exists to undo (Grimoire gating).</summary>
    public bool CanRetrace => _hasLastMove;

    public bool SpellRetrace()
    {
        if (!_hasLastMove || _party == null)
            return false;
        _hasLastMove = false;
        StepsRemaining += _lastMoveStepCost;
        _party.Initialize(_grid, _fog, _lastMoveFrom);
        if (!HardWindowMode &&
            _grid.Distance(_party.CurrentCoord, _windowCenterLocal) >= RecenterThreshold)
            RecenterWindow(_party.CurrentCoord);
        UpdateUI();
        return true;
    }

    /// <summary>Deploy Waystation (Tinker): a one-use pocket rest on the
    /// current hex, and a supply anchor while it stands (W-track ruling #2).
    /// Expires with the expedition; never persists as a staging point.</summary>
    public bool SpellDeployWaystation()
    {
        if (!_window.TryLocalToWorld(_party.CurrentCoord, out int col, out int row))
            return false;
        var grim = SaveManager.ActiveSave?.Cycle?.Grimoire;
        string mark = $"{col},{row}";
        if (grim == null || grim.ActiveWaystations.Contains(mark))
            return false;
        grim.ActiveWaystations.Add(mark);
        SaveManager.MarkDirty();
        SpellDrawWaystationMarker(_party.CurrentCoord, col, row);
        UpdateUI(); // supply readout may change immediately
        return true;
    }

    /// <summary>Waystation marker: a small square-on-post, named by world
    /// coord so consumption can find and free it.</summary>
    public void SpellDrawWaystationMarker(Vector2I local, int col, int row)
    {
        var marker = new Node2D { Name = $"WaystationMarker_{col}_{row}", ZIndex = 6 };
        marker.AddChild(new Polygon2D
        {
            Polygon = new[] { new Vector2(-9, -9), new Vector2(9, -9),
                              new Vector2(9, 9), new Vector2(-9, 9) },
            Color = new Color(0f, 0f, 0f, 0.7f),
        });
        marker.AddChild(new Polygon2D
        {
            Polygon = new[] { new Vector2(-6.5f, -6.5f), new Vector2(6.5f, -6.5f),
                              new Vector2(6.5f, 6.5f), new Vector2(-6.5f, 6.5f) },
            Color = UITheme.ArcaneBlue,
        });
        marker.Position = _grid.AxialToWorld(local);
        _grid.AddChild(marker);
    }

    /// <summary>Remnant marker (Deathsight): a pale sliver on a won-combat hex.</summary>
    public void SpellDrawRemnantMarker(Vector2I local)
    {
        var marker = new Node2D { Name = "RemnantMarker", ZIndex = 6 };
        marker.AddChild(new Polygon2D
        {
            Polygon = new[] { new Vector2(0, -10), new Vector2(5, 4),
                              new Vector2(-5, 4) },
            Color = new Color(0.85f, 0.88f, 0.80f, 0.85f),
        });
        marker.Position = _grid.AxialToWorld(local);
        _grid.AddChild(marker);
    }

    /// <summary>Stasis Snare: freeze the patrol on a grid-local coord.</summary>
    public bool SpellStunPatrolAt(Vector2I local, int steps)
        => _factionManager?.TryStunPatrolAt(local, steps) != null;

    /// <summary>Coords of patrols whose tiles are currently visible (their
    /// tokens render): Stasis Snare's legal targets.</summary>
    public List<Vector2I> VisiblePatrolCoords()
    {
        var result = new List<Vector2I>();
        if (_factionManager == null)
            return result;
        foreach (var c in _factionManager.GetPatrolPositions())
            // Step 1: model read. Unloaded coords answer Hidden, which also
            // covers the old "hex must exist" half of the check.
            if (_fog.FogAt(c) != OverworldHex.FogState.Hidden)
                result.Add(c);
        return result;
    }

    /// <summary>Speak with the Fallen: chart the ground under every patrol
    /// (radius 1) so their ghosted tokens surface. Returns patrols exposed.</summary>
    public int SpellChartPatrolPositions()
    {
        if (_factionManager == null)
            return 0;
        int exposed = 0;
        foreach (var c in _factionManager.GetPatrolPositions())
        {
            if (_window.TryLocalToWorld(c, out int col, out int row))
            {
                SpellChartHexRadius(col, row, 1);
                exposed++;
            }
        }
        RefreshWindowSilhouettes();
        return exposed;
    }

    /// <summary>Compass bearing + distance from the party to a world coord.</summary>
    public string SpellBearingTo(int col, int row, string label)
    {
        if (!_window.TryLocalToWorld(_party.CurrentCoord, out int pc, out int pr))
            return "";
        int dist = _world.HexDistance(pc, pr, col, row);
        int dx = col - pc, dy = row - pr;
        string ns = dy < 0 ? "north" : dy > 0 ? "south" : "";
        string ew = dx < 0 ? "west" : dx > 0 ? "east" : "";
        string dir = (ns + (ns != "" && ew != "" ? "-" : "") + ew);
        if (dir == "") dir = "here";
        return $"{label}: {dist} hexes {dir}";
    }

    /// <summary>Attuned Recall: bearings to the staging tile and the nearest
    /// Available staging point that isn't the staging tile.</summary>
    public string SpellRecallBearings()
    {
        string home = SpellBearingTo(_stagingCol, _stagingRow, "Staging point");
        StagingPoint nearest = null;
        int bestD = int.MaxValue;
        if (_window.TryLocalToWorld(_party.CurrentCoord, out int pc, out int pr))
            foreach (var sp in _world.StagingPoints)
            {
                if (!sp.Available || (sp.X == _stagingCol && sp.Y == _stagingRow))
                    continue;
                int d = _world.HexDistance(pc, pr, sp.X, sp.Y);
                if (d < bestD) { bestD = d; nearest = sp; }
            }
        return nearest == null
            ? home
            : home + "  ·  " + SpellBearingTo(nearest.X, nearest.Y, nearest.Name);
    }

    // ── S4 façade additions ──────────────────────────────────────────────

    /// <summary>S4 (Identify): rolled encounter compositions pinned by
    /// Identify, keyed by world "col,row". Static so pins survive the
    /// combat scene swap (the OverworldSpellEffects pattern); cleared on
    /// fresh deploy and every expedition-end path. Known limit (accepted,
    /// logged in the verification doc): statics do not survive a full app
    /// restart, so a quit-and-reload mid-expedition forgets pins: the
    /// next scout report re-rolls.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, EncounterDefinition>
        _identifiedEncounters = new();

    /// <summary>Identify (Arcanist, §7b): roll the encounter for a visible
    /// combat/prison POI exactly as OpenScoutReport would, PIN it so the
    /// on-hex report later shows the same forces, and display it read-only
    /// through the ScoutReportPanel's intel mode. Returns the info-line
    /// result, or null (refused; no charge, G5).</summary>
    public string SpellIdentify(Vector2I local)
    {
        // Step 2: gate on the overlay model (hex still fetched for terrain below).
        var ovIdent = _overlay.OverlayAt(local);
        if (!_grid.Hexes.ContainsKey(local) || ovIdent.Consumed ||
            (ovIdent.Poi != OverworldHex.POIType.Combat && ovIdent.Poi != OverworldHex.POIType.Prison))
            return null;
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return null;

        string key = $"{col},{row}";
        if (!_identifiedEncounters.TryGetValue(key, out var encounterDef))
        {
            string terrainType = TerrainAt(local).ToString();   // Step 3: world read
            string regionId = StagingTemplateRegion();
            var arch = RollArchmageAt(local); // same draw shape as OpenScoutReport
            encounterDef =
                (arch != null
                    ? EncounterPoolLoader.PickFromArchmage(arch, regionId, EncounterTier.Battle, terrainType, CampaignEscalation.CombatDifficultyMult(SaveManager.ActiveSave?.Cycle))
                    : null)
                ?? EncounterPoolLoader.Pick(regionId, EncounterTier.Battle, terrainType, DifficultyMultAt(local));
            if (encounterDef == null)
                return null;
            _identifiedEncounters[key] = encounterDef;
        }

        _scoutPanel.ShowIntel(encounterDef, TerrainAt(local).ToString(),
            "Identified from afar. This composition is fixed; the scout report will match.");
        return $"the weave yields their number: {encounterDef.Enemies.Count} foe(s) revealed";
    }

    // ── S5 façade additions: the world watches magic (§6a / R15) ────────

    /// <summary>S5 (R15): deterministic HP hit per cast made FROM a tier-3
    /// corrupted tile. Flat and legible: the detail card warns pre-cast;
    /// no roll. Applies to scroll casts too: exposure is about standing in
    /// the corruption while channeling, not about Essence.</summary>
    [Export] public int Tier3CastExposureHP = 4;

    /// <summary>Apply the tier-3 casting exposure if the party stands on
    /// tier-3 ground. Returns the info-line note, or null when no exposure.
    /// Can end the expedition: callers must check ExpeditionComplete.</summary>
    public string SpellTier3Exposure()
    {
        if (CorruptionTierAt(_party.CurrentCoord) < 3)
            return null;
        Hull -= Tier3CastExposureHP;
        if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
            Hull = Mathf.Max(1, Hull);
        LogRun("cast_exposure", "cast from tier-3 corrupted ground",
               hpDelta: -Tier3CastExposureHP);
        if (Hull <= 0)
        {
            Hull = 0;
            EmergencyExtract("The channeling shatters the hull, forcing a recall.");
            return null;
        }
        UpdateUI();
        return $"the corrupted ground answers the working: the party sears for {Tier3CastExposureHP} HP";
    }

    /// <summary>S5 (§6a): emit the witnessed-cast deed for an Overt/Grand
    /// spell resolved in a kingdom's territory. Only the §6a rows echo:
    /// necromantic casting (−, Court Wizard/Idealist) and warding worked
    /// near the kingdom's own settlement or seat (+, same route). Other
    /// Overt casts are witnessed but not yet deeds (v1 table). Returns the
    /// deed toast, or null.</summary>
    public string SpellEmitWitnessEcho(OverworldSpellDefinition def, string kingdomId)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || def == null)
            return null;
        bool grand = def.Magnitude == "Grand";

        if (def.School == "Necromancer")
            return CouncilEcho.EmitDeed(cycle, kingdomId,
                CouncilEcho.SpellcraftTransgression, positive: false, isMajor: grand);

        if (def.Category == "Warding" && CivicPoiNear(kingdomId, radius: 2))
            return CouncilEcho.EmitDeed(cycle, kingdomId,
                CouncilEcho.SpellcraftAid, positive: true, isMajor: grand);

        return null;
    }

    /// <summary>True when a Settlement/Seat POI of the given kingdom lies
    /// within `radius` hexes of the party: §6a's "near a settlement
    /// (benefiting inhabitants)" test.</summary>
    private bool CivicPoiNear(string kingdomId, int radius)
    {
        if (!_window.TryLocalToWorld(_party.CurrentCoord, out int pc, out int pr))
            return false;
        foreach (var poi in _world.Pois)
        {
            if ((poi.Kind == PoiKind.Settlement || poi.Kind == PoiKind.Seat) &&
                poi.KingdomId == kingdomId &&
                _world.HexDistance(pc, pr, poi.X, poi.Y) <= radius)
                return true;
        }
        return false;
    }

    /// <summary>S5 (True Names §7f): pinned negotiation encounters, world
    /// "col,row" → encounter id. Created on a True-Names pre-read hover or
    /// at engagement; TriggerNegotiationEncounter consumes the pin so the
    /// archetype you read is the counterpart you meet (G5). Same static
    /// lifecycle as the Identify pins.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string>
        _pinnedNegotiations = new();

    private NegotiationEncounterData PinnedNegotiationFor(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return null;
        string key = $"{col},{row}";
        if (_pinnedNegotiations.TryGetValue(key, out string id))
        {
            var cached = NegotiationEncounterLoader.Load(id);
            if (cached != null)
                return cached;
        }
        var data = NegotiationEncounterLoader.PickForTerrain(
            TerrainAt(local).ToString(), StagingTemplateRegion());   // Step 3
        if (data != null)
            _pinnedNegotiations[key] = data.Id;
        return data;
    }

    /// <summary>Hover extra for Negotiation POIs under the True Names
    /// attunement: name the counterpart's archetype before engagement,
    /// pre-loading the token-affinity read the negotiation rewards.</summary>
    private string NegotiationPreread(Vector2I local)
    {
        if (_spells == null || !_spells.HasAttunement("true_names"))
            return "";
        var ovPre = _overlay.OverlayAt(local);   // Step 2
        if (ovPre.Poi != OverworldHex.POIType.Negotiation || ovPre.Consumed)
            return "";
        var data = PinnedNegotiationFor(local);
        return data == null ? "" : $"  ·  a {data.Archetype} holds this table";
    }

    /// <summary>Nearest undiscovered POI's bearing (Speak with the Fallen).</summary>
    public string SpellNearestUndiscoveredPoiBearing()
    {
        if (!_window.TryLocalToWorld(_party.CurrentCoord, out int pc, out int pr))
            return "";
        WorldPoi best = null;
        int bestD = int.MaxValue;
        foreach (var poi in _world.Pois)
        {
            if (poi.Discovered || poi.Consumed)
                continue;
            int d = _world.HexDistance(pc, pr, poi.X, poi.Y);
            if (d < bestD) { bestD = d; best = poi; }
        }
        return best == null ? "" : SpellBearingTo(best.X, best.Y, "Something undiscovered lies");
    }

    private readonly List<Node2D> _auspiceMarks = new();

    /// <summary>Auspice (Chronomancer): preview where the corruption's tile
    /// flood presses next: loaded clean tiles adjacent to corrupted ground
    /// (heuristic over CorruptionSpread's outward flood; the exact tick also
    /// moves kingdom pressure, which this does not simulate). Marks fade at
    /// the next Auspice or expedition end. Returns tiles flagged.</summary>
    public int SpellAuspicePreview()
    {
        foreach (var m in _auspiceMarks)
            if (GodotObject.IsInstanceValid(m))
                m.QueueFree();
        _auspiceMarks.Clear();

        int flagged = 0;
        foreach (var kvp in _grid.Hexes)
        {
            if (!_window.TryLocalToWorld(kvp.Key, out int col, out int row) ||
                !_world.TryIndex(col, row, out int idx))
                continue;
            if (_world.Tiles[idx].Corruption >= 30)
                continue;
            bool threatened = false;
            foreach (var (nc, nr) in HexCoord.Neighbors(col, row, _world.Width, _world.Height))
                if (_world.TryIndex(nc, nr, out int nidx) && _world.Tiles[nidx].Corruption >= 30)
                { threatened = true; break; }
            if (!threatened)
                continue;

            var m = new Node2D { Name = "AuspiceMark", ZIndex = 5 };
            m.AddChild(new Polygon2D
            {
                Polygon = OverworldHex.MakeHexPoints(OverworldHex.GetHexSize() * 0.45f),
                Color = new Color(0.55f, 0.20f, 0.65f, 0.35f),
            });
            m.Position = _grid.AxialToWorld(kvp.Key);
            _grid.AddChild(m);
            _auspiceMarks.Add(m);
            flagged++;
        }
        return flagged;
    }

    // ════════════════════════════════════════════════════════════════════
    // W1: sliding window · W3: supply line
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Slide the loaded window so it is centered on a grid-local
    /// coord: stream in tiles entering the load radius, free tiles beyond the
    /// unload radius. Patrols whose tiles unload freeze in place automatically
    /// (their passability/visibility checks fail on missing hexes) and resume
    /// when the shard returns: the simulation LOD is implicit.</summary>
    private void RecenterWindow(Vector2I centerLocal)
    {
        if (!_window.TryLocalToWorld(centerLocal, out int col, out int row))
            return;
        var (added, removed) = _window.StreamTo(_grid, col, row);
        // Step 1: re-mirror the fog model to the slid window (streamed-in hexes
        // carry FogFromDiscovery; streamed-out coords drop from the model).
        _fog?.SyncFromWindow();
        // Step 2: same re-mirror for the overlay: the stamps below then rewrite
        // their marks through the SetOverlay seam.
        SyncOverlayFromWindow();
        _windowCenterLocal = centerLocal;
        if (added > 0)
            StampCivicPois(); // S4.2: newly streamed tiles may hold settlements
        StampStronghold();    // re-stamp the warfront objective if it (re)entered the window
        PaintContestedGround();   // newly streamed fringe joins (or leaves) the war zone
        if (PlayerSession.DebugMode && (added > 0 || removed > 0))
            GD.Print($"[Window] Slide → ({col},{row}): +{added}/−{removed} tiles, " +
                     $"{_grid.Hexes.Count} live.");
    }

    /// <summary>S4.2 (user request): settlements and seats had no expedition-
    /// map presence: the window streamer maps only encounter-scale POIs to
    /// hex markers, so cities were visible on the strategic view and invisible
    /// underfoot. Stamp POIType.Settlement/Seat onto loaded hexes after every
    /// build/slide (idempotent; never overwrites an encounter POI; marker
    /// visibility still rides the standard fog gate in RefreshVisuals).</summary>
    /// <summary>Warfront objective: stamp the besieging stronghold as a Combat
    /// landmark on its window hex and reveal it, so the party can march from the
    /// front and storm it. Re-called on recenter (streaming rebuilds hexes from
    /// world data, which has no stronghold). No-op once the siege is broken, so it
    /// doesn't respawn. Touches only the in-window hex, never the world table.</summary>
    /// <summary>Tint every window tile belonging to either province of the active
    /// warfront. Cheap by construction: it only calls RefreshVisuals on tiles whose
    /// contested state actually CHANGED, so a window slide repaints the newly
    /// streamed fringe rather than all ~500 live hexes. A no-op (after the first
    /// pass) on ordinary expeditions, where both kingdom ids are empty.</summary>
    private void PaintContestedGround()
    {
        if (_grid == null || _window == null || _world == null)
            return;
        bool anyFront = (!string.IsNullOrEmpty(_warfrontDefenderKid)
                      || !string.IsNullOrEmpty(_warfrontAggressorKid))
                     && (_warfrontFrontCol >= 0 || _strongholdCol >= 0);

        foreach (var kv in _grid.Hexes)
        {
            bool contested = false;
            if (anyFront && _window.TryLocalToWorld(kv.Key, out int col, out int row))
            {
                string kid = _world.GetTile(col, row).KingdomId ?? "";
                if (kid.Length > 0
                    && (kid == _warfrontDefenderKid || kid == _warfrontAggressorKid))
                {
                    // Mathf, not Math: this file's usings are Godot and
                    // System.Collections.Generic only.
                    int dFront = _warfrontFrontCol >= 0
                        ? _world.HexDistance(col, row, _warfrontFrontCol, _warfrontFrontRow)
                        : int.MaxValue;
                    int dHold = _strongholdCol >= 0
                        ? _world.HexDistance(col, row, _strongholdCol, _strongholdRow)
                        : int.MaxValue;
                    contested = Mathf.Min(dFront, dHold) <= WarZoneRadius;
                }
            }
            // Step 2: contested lives on the overlay model; SetOverlay mirrors +
            // redraws, preserving the only-repaint-on-change economy.
            var ovWar = _overlay.OverlayAt(kv.Key);
            if (kv.Value == null || ovWar.Contested == contested)
                continue;
            ovWar.Contested = contested;
            SetOverlay(kv.Key, ovWar);
        }
    }

    private void StampStronghold()
    {
        if (!_isWarfront || _strongholdCol < 0 || _grid == null || _window == null)
            return;
        var cyc = SaveManager.ActiveSave?.Cycle;
        if (cyc != null && cyc.WarfrontStrongholdCleared)
            return; // already stormed; don't put it back

        var local = _window.LocalOf(_strongholdCol, _strongholdRow);
        if (!_grid.Hexes.ContainsKey(local))
            return; // not in the loaded window yet; a later stream will catch it

        // Step 2: the stronghold stamp is DATA now. Before this, the warfront
        // objective existed only as node properties inside the 2D scene.
        var ovHold = _overlay.OverlayAt(local);
        ovHold.Poi = OverworldHex.POIType.Combat; // entry must still route to a fight
        ovHold.Objective = true;                  // ...but it draws as the gold objective star
        ovHold.Landmark = true;
        ovHold.Consumed = false;
        SetOverlay(local, ovHold);
        _fog?.RevealHex(local);
    }

    private void StampCivicPois()
    {
        if (_world?.Pois == null || _grid == null)
            return;
        foreach (var poi in _world.Pois)
        {
            if (poi.Kind != PoiKind.Settlement && poi.Kind != PoiKind.Seat)
                continue;
            var local = _window.LocalOf(poi.X, poi.Y);
            if (!_grid.Hexes.ContainsKey(local))
                continue;
            var want = poi.Kind == PoiKind.Seat
                ? OverworldHex.POIType.Seat
                : OverworldHex.POIType.Settlement;
            // Step 2: stamp through the overlay seam (never overwrites an
            // encounter POI, as before).
            var ovCivic = _overlay.OverlayAt(local);
            if (ovCivic.Poi == OverworldHex.POIType.None)
            {
                ovCivic.Poi = want;
                SetOverlay(local, ovCivic);
            }
        }

        // A SEAT capital's centre is loaded as an Outpost POI (WorldWindowBuilder maps
        // PoiKind.Seat -> POIType.Outpost, the old "seat is a rest/staging stop" behaviour). For
        // Phase 3 an enemy capital is a SERVICES stop, so OVERRIDE its centre to POIType.Seat: this
        // draws the gold seat marker AND routes arrival to the services menu instead of the outpost
        // full-heal-and-consume. Only the seat's own centre tile is touched; lesser cities and real
        // outposts are left alone. Runs after SyncOverlayFromWindow, so it wins over the loader map.
        if (_world.Settlements != null)
            foreach (var st in _world.Settlements)
            {
                if (st == null || !st.IsSeat || st.Tier != SettlementTier.City)
                    continue;
                var localC = _window.LocalOf(st.CenterX, st.CenterY);
                if (!_grid.Hexes.ContainsKey(localC))
                    continue;
                var ovC = _overlay.OverlayAt(localC);
                if (ovC.Poi == OverworldHex.POIType.None || ovC.Poi == OverworldHex.POIType.Outpost)
                {
                    if (PlayerSession.DebugMode)
                        GD.Print($"[CityServices] Stamped seat '{CitySettlementName(st)}' as POIType.Seat " +
                                 $"at local {localC} (world {st.CenterX},{st.CenterY}); was {ovC.Poi}.");
                    ovC.Poi = OverworldHex.POIType.Seat;
                    ovC.Consumed = false;   // undo any earlier outpost consume
                    SetOverlay(localC, ovC);
                }
            }
    }

    /// <summary>Hex distance from a grid-local coord to the NEAREST supply
    /// anchor: this expedition's staging tile, or any Available staging point
    /// (settlements, secured outposts/seats, including ones secured this
    /// run, which extend the line as you push).</summary>
    private int SupplyDistanceAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return 0;

        int best = _world.HexDistance(col, row, _stagingCol, _stagingRow);
        foreach (var sp in _world.StagingPoints)
        {
            if (!sp.Available)
                continue;
            int d = _world.HexDistance(col, row, sp.X, sp.Y);
            if (d < best)
                best = d;
        }

        // S3 (Deploy Waystation + W-track ruling #2): a standing waystation is
        // a supply anchor while it lasts: the deep-push range strategy.
        var grimW = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grimW != null)
            foreach (var mark in grimW.ActiveWaystations)
                if (TryParseMark(mark, out int wc, out int wr))
                {
                    int d = _world.HexDistance(col, row, wc, wr);
                    if (d < best)
                        best = d;
                }
        return best;
    }

    /// <summary>Parse a "col,row" world mark (beacons/remnants/waystations).</summary>
    private static bool TryParseMark(string mark, out int col, out int row)
    {
        col = row = -1;
        var parts = mark.Split(',');
        return parts.Length == 2 &&
               int.TryParse(parts[0], out col) && int.TryParse(parts[1], out row);
    }

    /// <summary>Leash band at a grid-local coord: 0 within SupplyRange of the
    /// nearest anchor, then 1 per LeashBandWidth hexes beyond, capped at
    /// LeashBandCap. Drain per step = band × LeashDrainPerBand.</summary>
    private int SupplyBandAt(Vector2I local)
    {
        int over = SupplyDistanceAt(local) - SupplyRange;
        if (over <= 0)
            return 0;
        return Mathf.Min(LeashBandCap, 1 + (over - 1) / Mathf.Max(1, LeashBandWidth));
    }

    /// <summary>P5: true when <paramref name="local"/> maps to a tile inside a
    /// shard sub-region footprint. Inside a vault the terrain, corruption, and
    /// supply-leash drains are all suppressed (a contained designed arena, not
    /// wilderness); step cost and out-of-range exhaustion still apply.</summary>
    private bool InsideShardZone(Vector2I local)
    {
        if (_world == null || !_window.TryLocalToWorld(local, out int col, out int row))
            return false;
        return _world.ShardZoneAt(col, row) != null;
    }

    /// <summary>True when the party stands ON a supply anchor tile: the
    /// staging tile or any Available staging point. Free extraction is only
    /// offered here (W3 ruling); anywhere else is an emergency extraction.</summary>
    private bool OnSupplyAnchor()
    {
        if (_party == null ||
            !_window.TryLocalToWorld(_party.CurrentCoord, out int col, out int row))
            return false;
        if (col == _stagingCol && row == _stagingRow)
            return true;
        foreach (var sp in _world.StagingPoints)
            if (sp.Available && sp.X == col && sp.Y == row)
                return true;
        // S3: a standing waystation is an anchor (free extraction included;
        // it is a 5-Essence Overt cast; tuning watch noted in the docs).
        var grimA = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grimA != null && grimA.ActiveWaystations.Contains($"{col},{row}"))
            return true;
        return false;
    }

    private void RevealAllFog()
    {
        // Step 1: writes go through the fog seam (model + node mirror + redraw).
        foreach (var coord in new List<Vector2I>(_grid.Hexes.Keys))
            _fog.SetFog(coord, OverworldHex.FogState.Revealed);
        WriteVisibleToWorld();
    }

    private int GetTerrainStepCost(OverworldHex.TerrainType terrain)
        => OverworldMovementCost.TerrainStep(terrain);

    private int GetTerrainHPDrain(OverworldHex.TerrainType terrain)
        => OverworldMovementCost.TerrainHPDrain(terrain);

    /// <summary>HP lost crossing a corrupted tile, by its world corruption (0–100).
    /// Below 30 is harmless (the faint edge); it ramps to ~10 at the core. This
    /// makes the corrupted third of the late-cycle map genuinely dangerous to cross.</summary>
    private int CorruptionDrainAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return 0;
        if (!_world.TryIndex(col, row, out int idx))
            return 0;
        int corruption = _world.Tiles[idx].Corruption;
        if (corruption < 30)
            return 0;
        // 30 → ~2, 100 → ~10, linear.
        return Mathf.Clamp(2 + (corruption - 30) * 8 / 70, 2, 10);
    }

    /// <summary>Q3 (§4b): corruption TIER (1–3) of a tile, for the CorruptionWard
    /// cap (tier × 2). Banded off the 0–100 world corruption; 0 below the 30 harm
    /// threshold. Tier 1 (30–59) is fully wardable at the edge; tier 3 (90+)
    /// always stings past any realistic ward.</summary>
    private int CorruptionTierAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return 0;
        if (!_world.TryIndex(col, row, out int idx))
            return 0;
        int c = _world.Tiles[idx].Corruption;
        if (c < 30) return 0;
        if (c < 60) return 1;
        if (c < 90) return 2;
        return 3;
    }

    // ════════════════════════════════════════════════════════════════════
    // Favor call-ins (Court & Council C3, §4a)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Steps of patrol suppression a Passage (safe conduct) favor buys.</summary>
    private const int SafeConductSteps = 25;

    /// <summary>KingdomId of the world tile under a window-local coord, or "".</summary>
    private string KingdomIdAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return "";
        return _world.GetTile(col, row).KingdomId ?? "";
    }

    /// <summary>Human-readable kingdom name via the court layer's resolver;
    /// falls back to the raw id.</summary>
    private string KingdomDisplayName(string kingdomId)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || string.IsNullOrEmpty(kingdomId))
            return kingdomId ?? "";
        return CouncilTick.CourtDisplayName(cycle, kingdomId);
    }

    /// <summary>Null if the favor is callable right now; else the reason it
    /// isn't. Ineligible calls never consume the favor.</summary>
    private string CallInIneligibility(Favor f)
    {
        if (f == null)
            return "No favor.";
        if (!f.OwedToGuild)
            return "Owed by the guild. Repay it, don't call it in.";
        // Q4.3: Major favors ARE callable now: the courtier's own gift
        // (item, or the Arcane retainer). They skip the minor-effect type
        // gates below; territory + expedition checks still apply.
        if (!f.IsMajor && !CouncilLedger.CallableTypes.Contains(f.Type))
            return $"{f.Type} favors have no field effect yet.";
        if (ExpeditionComplete)
            return "The expedition is over.";
        if (KingdomIdAt(_party.CurrentCoord) != f.KingdomId)
            return "Must be inside the creditor's territory.";
        if (f.IsMajor)
            return null; // Major redemptions have no further preconditions
        if (f.Type == "Military" &&
            (_factionManager == null || !_factionManager.HasStandablePatrol()))
            return "No patrols in the field to stand down.";
        if (f.Type == "Economic" && CurrentHP >= MaxHP)
            return "The party is at full strength.";
        if (f.Type == "Political" &&
            !CouncilEcho.HasCancellableNegative(SaveManager.ActiveSave?.Cycle?.Council, f.KingdomId))
            return "No ill word is travelling toward this court.";
        return null;
    }

    /// <summary>Panel callback: validate, execute, consume, checkpoint.</summary>
    private void OnLedgerCallIn(Favor f)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null)
            return;

        string reason = CallInIneligibility(f);
        if (reason != null)
        {
            ShowInfo(reason);
            return;
        }

        var (ok, msg) = ExecuteCallIn(f);
        ShowInfo(msg);
        if (ok)
        {
            CouncilLedger.Consume(council, f);
            SaveManager.SaveIfDirty(); // favor consumption is a checkpoint
        }
        _ledgerPanel.RefreshRows();
        UpdateUI();
    }

    /// <summary>The C3 call-in effects (+ Q4.3 Major redemptions). Returns
    /// (consumed, message); a no-op outcome refuses without consuming.</summary>
    private (bool ok, string msg) ExecuteCallIn(Favor f)
    {
        // Q4.3 (§7c): a Major favor is the courtier's own gift: an item
        // flavored by their office ("the Marshal's own sword is a gift with a
        // story and a watcher"). Arcane Majors stay the retainer (K5).
        if (f.IsMajor && f.Type != "Arcane")
        {
            var gift = RollCourtierGift(f);
            if (gift == null)
                return (false, "The court has nothing worthy to send.");
            SaveManager.ActiveSave?.Armory.AddItem(gift);
            SaveManager.MarkDirty();
            return (true, $"A courier arrives under seal: {gift.Name} ({gift.Rarity}), " +
                          "a gift with a story, and a watcher.");
        }

        switch (f.Type)
        {
            case "Military":
            {
                string routed = _factionManager?.StandDownNearestPatrol(_party.CurrentCoord);
                if (routed == null)
                    return (false, "No patrols in the field to stand down.");
                return (true, "The Marshal's word arrives: a patrol withdraws for the rest of this expedition.");
            }
            case "Economic":
            {
                // Ruling (turnaround-only Hull repair): the Steward's supply train
                // brings FUEL, not hull plate, so it can't mend the castle in the field.
                int before = StepsRemaining;
                Refuel(MaxFuel / 4, "Steward supply train");
                int got = StepsRemaining - before;
                return got > 0
                    ? (true, $"The Steward's supply train reaches you. +{got} fuel.")
                    : (true, "The Steward's supply train reaches you, but the furnace is already full.");
            }
            case "Intelligence":
            {
                if (!TryChartPacket(f.KingdomId, out string summary))
                    return (false, "Nothing new to chart here.");
                return (true, summary);
            }
            case "Passage":
            {
                _factionManager?.SuppressAllPatrols(SafeConductSteps);
                return (true, $"Papers of safe conduct: patrols will not trouble you for {SafeConductSteps} steps.");
            }
            case "Political":
            {
                var council = SaveManager.ActiveSave?.Cycle?.Council;
                string buried = council != null
                    ? CouncilEcho.CancelWorstNegative(council, f.KingdomId)
                    : null;
                if (buried == null)
                    return (false, "No ill word is travelling toward this court.");
                return (true, $"The Chancellor's quiet work: the tale of {buried} will never reach the court.");
            }
            case "Arcane":
            {
                // K5 (§5a): the Arcane MAJOR favor is the retainer redemption:
                // the Court Wizard's own person, sent to settle the debt.
                // (Scope ruling: spec offered any Major favor; Arcane was the
                // one empty effect slot, so no existing effect is overloaded.)
                if (!f.IsMajor)
                    return (false, "A minor arcane favor buys no one's service.");
                string joined = RecruitmentSources.RedeemRetainer(f);
                if (joined == null)
                    return (false, "The court's debt cannot be paid right now.");
                return (true, joined);
            }
        }
        return (false, "That favor has no field effect yet.");
    }

    /// <summary>Q4.3: pick the Major-favor gift: slot flavored by the favor
    /// type (Military → Weapon, Passage → Armor, the rest → Trinket), Rare
    /// preferred, Uncommon fallback, never Legendary (Auction House rule).</summary>
    private ItemDefinition RollCourtierGift(Favor f)
    {
        string slot = f.Type switch
        {
            "Military" => "Weapon",
            "Passage" => "Armor",
            _ => "Trinket",
        };

        var all = ItemDatabase.GetAll();
        if (all == null || all.Count == 0) return null;

        var rng = new RandomNumberGenerator();
        rng.Randomize();

        foreach (string rarity in new[] { "Rare", "Uncommon" })
        {
            var band = new List<ItemDefinition>();
            foreach (var d in all)
                if (d.Rarity == rarity && d.Slot == slot)
                    band.Add(d);
            if (band.Count > 0)
                return band[rng.RandiRange(0, band.Count - 1)];
        }
        // No item of that slot below Legendary: fall back to any Rare.
        var any = new List<ItemDefinition>();
        foreach (var d in all)
            if (d.Rarity == "Rare")
                any.Add(d);
        return any.Count > 0 ? any[rng.RandiRange(0, any.Count - 1)] : null;
    }

    /// <summary>If the resolved tile is a Prison holding a guild envoy, free
    /// them: remove the ImprisonedEnvoy record and return the companion to the
    /// recruited pool (AddToParty-eligible again via the derived guard). Keyed
    /// by matching the world POI index, so only the correct captive is freed.</summary>
    private void ReleaseImprisonedAt(Vector2I resultHex)
    {
        var council = SaveManager.ActiveSave?.Cycle?.Council;
        if (council == null || council.Imprisoned.Count == 0)
            return;
        if (!_window.TryLocalToWorld(resultHex, out int col, out int row))
            return;

        // Match the resolved tile against the gaol's stored world coordinates.
        // Each runtime prison sits on its own unoccupied tile, so (col,row)
        // identifies it uniquely and survives any mutation of WorldData.Pois,
        // unlike the list index this used to key on.
        ImprisonedEnvoy freed = null;
        foreach (var e in council.Imprisoned)
        {
            if (e.PrisonX == col && e.PrisonY == row)
            { freed = e; break; }
        }
        if (freed == null)
            return;

        council.Imprisoned.Remove(freed);
        var envoy = SaveManager.ActiveSave.Companions.Find(c => c.Id == freed.CompanionId);
        string name = envoy?.Name ?? freed.CompanionId;
        SaveManager.MarkDirty();
        ShowInfo($"{name} is freed from the gaol and returns to the guild's ranks.");
    }

    /// <summary>Emit at most ONE echo for a won combat (C4 §7a), priority:
    /// patrol-slain (negative, major, routed to the patrol's OWNER kingdom)
    /// > corruption-cleansed (positive; major at world corruption >= 60)
    /// > settlement-defended (positive, within 4 of a friendly settlement).
    /// Wilds patrols and courtless kingdoms emit nothing.</summary>
    /// <summary>Dossier: open an archmage's dossier the first time their forces
    /// are encountered (seen, fought, or parleyed with), diffing quest state
    /// around the stamp so the unlock toasts fire. Idempotent; "wilds" and
    /// unknown ids are filtered inside DossierService.</summary>
    private void AnnounceDossierMet(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return;
        var before = QuestNotifier.Snapshot(save);
        if (!DossierService.EnsureMet(archmageId)) return;
        var def = ArchmageDefById(archmageId);
        _toasts?.Push($"Dossier opened: {(def != null ? def.DisplayName : archmageId)}.",
                      QuestToastKind.Unlock);
        foreach (var qt in QuestNotifier.NotifyNew(before, save))
            _toasts?.Push(qt.Text, qt.Kind);
    }

    private void EmitCombatDeed(EncounterRouter router, Vector2I resultHex)
    {
        // Cross-cycle combat record (deed:combat_won): powers proven-guild
        // companion unlocks (CompanionUnlocks) and future deed-count quests.
        SaveManager.ActiveSave?.Ledger?.RecordDeed("combat_won");

        // Marginalia (marginalia_spec_v1 R2/R5): commit the won fight's family
        // kill tally (victory-gated exactly like combat_won) and toast any
        // movement. The unlock itself is settled by ProgressionSweep on the
        // next save; the completion toast is the promise it keeps.
        if (router.SavedCombatFamilyKills != null && router.SavedCombatFamilyKills.Count > 0)
        {
            var advanced = MarginaliaService.CommitKills(
                SaveManager.ActiveSave, router.SavedCombatFamilyKills);
            router.SavedCombatFamilyKills = new Dictionary<string, int>();

            foreach (var adv in advanced)
            {
                if (adv.CompletedNow)
                    _toasts?.Push(
                        $"Marginalia complete: {adv.FactionName}, " +
                        $"{(string.IsNullOrEmpty(adv.CardName) ? "entry settled" : adv.CardName + " unlocked")}.",
                        QuestToastKind.Unlock);
                else if (adv.Threshold > 0)
                    _toasts?.Push(
                        $"Marginalia: {adv.FactionName}, {adv.Kills}/{adv.Threshold} defeated.",
                        QuestToastKind.Progress);
            }
        }

        // The deed writes above mutate the permanent ledger directly; without
        // this, a return with no OTHER dirtying mutation (a re-fought hex, no
        // quest movement) reaches SaveIfDirty clean and the kills, plus the
        // completion the toast just promised, never touch disk.
        SaveManager.MarkDirty();

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.Council == null)
            return;

        // 1. Patrol slain: offense against whoever owns the soldiers.
        if (router.SavedCombatWasPatrolAmbush &&
            !string.IsNullOrEmpty(router.SavedCombatPatrolArchmageId) &&
            router.SavedCombatPatrolArchmageId != "wilds")
        {
            // Sentiment: killing an archmage's patrol is a direct affront
            cycle.Campaign?.ShiftSentiment(router.SavedCombatPatrolArchmageId, -10);

            foreach (var kvp in cycle.Kingdoms)
            {
                if (kvp.Value.ArchmageId == router.SavedCombatPatrolArchmageId)
                {
                    string t = CouncilEcho.EmitDeed(cycle, kvp.Key,
                        CouncilEcho.PatrolSlain, positive: false, isMajor: true);
                    if (t != null)
                        ShowInfo(t);
                    return;
                }
            }
            return; // archmage owns no kingdom (shouldn't happen); no echo
        }

        string kid = KingdomIdAt(resultHex);
        if (string.IsNullOrEmpty(kid))
            return;

        // 2. Corruption cleansed on the fought tile.
        if (_window.TryLocalToWorld(resultHex, out int col, out int row) &&
            _world.TryIndex(col, row, out int idx) &&
            _world.Tiles[idx].Corruption >= 30)
        {
            bool major = _world.Tiles[idx].Corruption >= 60;
            // Sentiment: fighting corruption directly helps the region's archmage
            if (cycle.Campaign != null && cycle.Kingdoms.TryGetValue(kid, out var clnKs))
            {
                string clnArch = cycle.Campaign.GetArchmageForRegion(clnKs.TemplateRegionId);
                if (!string.IsNullOrEmpty(clnArch))
                    cycle.Campaign.ShiftSentiment(clnArch, major ? +8 : +4);
            }
            string t = CouncilEcho.EmitDeed(cycle, kid,
                CouncilEcho.CorruptionCleansed, positive: true, isMajor: major);
            if (t != null)
                ShowInfo(t);
            return;
        }

        // 3. Settlement defended: a discovered settlement of this kingdom
        // within 4. Square-radius check on world offset coords approximates
        // hex distance (error <= 1 class at this radius); swap in a proper
        // offset->cube distance if the world exposes one.
        foreach (var poi in _world.Pois)
        {
            if (poi.Kind != PoiKind.Settlement || poi.KingdomId != kid || !poi.Discovered)
                continue;
            if (System.Math.Max(System.Math.Abs(poi.X - col), System.Math.Abs(poi.Y - row)) <= 4)
            {
                string t = CouncilEcho.EmitDeed(cycle, kid,
                    CouncilEcho.SettlementDefended, positive: true, isMajor: false);
                if (t != null)
                    ShowInfo(t);
                return;
            }
        }
    }

    /// <summary>Spymaster chart packet: reveal one undiscovered POI in the
    /// kingdom and chart radius 3 around it (same Unseen -> Charted write
    /// path as CouncilTick's Gather Intelligence); if the kingdom holds no
    /// undiscovered POIs, chart radius 3 around the party instead.</summary>
    private bool TryChartPacket(string kingdomId, out string summary)
    {
        summary = "";
        int charted = 0;
        string revealedKind = null;

        foreach (var poi in _world.Pois)
        {
            if (poi.KingdomId != kingdomId || poi.Discovered)
                continue;
            poi.Discovered = true;
            revealedKind = poi.Kind switch
            {
                PoiKind.Combat => "hostile encampment",
                PoiKind.Rest => "refuge",
                PoiKind.Narrative => "curious site",
                PoiKind.Negotiation => "meeting place",
                PoiKind.Outpost => "outpost",
                PoiKind.Settlement => "settlement",
                PoiKind.Seat => "seat of power",
                PoiKind.SupplyCache => "supply cache",  // spymaster = an intel channel
                _ => "site",
            };
            charted = ChartRadius(poi.X, poi.Y, 3);
            // Remote settlement discovery must still grant staging (the
            // WriteVisibleToWorld grant only fires on the un->discovered flip).
            if (poi.Kind == PoiKind.Settlement && poi.GrantsStaging)
                GrantStagingPointAtWorld(poi.X, poi.Y);
            break;
        }

        if (revealedKind == null)
        {
            if (_window.TryLocalToWorld(_party.CurrentCoord, out int pc, out int pr))
                charted = ChartRadius(pc, pr, 3);
        }

        if (charted == 0 && revealedKind == null)
            return false;

        SaveManager.MarkDirty();
        RefreshWindowSilhouettes();
        summary = revealedKind != null
            ? (charted > 0
                ? $"The Spymaster's packet arrives: a {revealedKind} is revealed; {charted} tiles charted."
                : $"The Spymaster's packet arrives: a {revealedKind} is revealed on already-charted ground.")
            : $"The Spymaster's packet arrives: {charted} tiles charted around your position.";
        return true;
    }

    /// <summary>Chart Unseen tiles in a square radius (never downgrades
    /// Charted/Explored). Returns the count charted.</summary>
    private int ChartRadius(int cx, int cy, int radius)
    {
        int charted = 0;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (!_world.TryIndex(cx + dx, cy + dy, out int idx))
                    continue;
                if (_world.Tiles[idx].Discovery == TileDiscovery.Unseen)
                {
                    _world.Tiles[idx].Discovery = TileDiscovery.Charted;
                    charted++;
                }
            }
        }
        return charted;
    }

    /// <summary>Lift Hidden window hexes to Silhouette where their world tile
    /// is now Charted. Mid-expedition world writes don't otherwise reach the
    /// already-built window.</summary>
    private void RefreshWindowSilhouettes()
    {
        // Step 1: reads and writes through the fog seam.
        foreach (var coord in new List<Vector2I>(_grid.Hexes.Keys))
        {
            if (_fog.FogAt(coord) != OverworldHex.FogState.Hidden)
                continue;
            if (!_window.TryLocalToWorld(coord, out int col, out int row))
                continue;
            if (!_world.TryIndex(col, row, out int idx))
                continue;
            if (_world.Tiles[idx].Discovery == TileDiscovery.Charted)
                _fog.SetFog(coord, OverworldHex.FogState.Silhouette);
        }
    }

    /// <summary>K1 (companion_item_systems v2.1 §4a): PartyPool = 20 (wizard
    /// base) + Σ per-companion floor(BaseHP/2) + loyalty bonus (Devoted +2,
    /// Sworn +4). Replaces the old full-BaseHP sum. Reads only serialized
    /// fields (BaseHP, Loyalty, roster ids) → deterministic across save/load.
    /// Prints the per-companion breakdown at launch (§10 K1 "pool readout").</summary>
    private int ComputePartyBaseHP()
    {
        const int WizardBaseHP = 20;
        int total = WizardBaseHP;
        var save = SaveManager.ActiveSave;
        if (save == null)
            return total;

        var readout = new System.Text.StringBuilder($"[PartyPool] wizard {WizardBaseHP}");
        foreach (var id in save.ActivePartyCompanionIds)
        {
            // K2: injured companions aren't fielded → no pool contribution.
            var c = save.Companions.Find(c => c.Id == id && c.IsRecruited && !c.IsPermadead && !c.IsInjured);
            if (c == null)
                continue;
            int contribution = c.BaseHP / 2;   // floor: int division, BaseHP ≥ 0
            int bonus = c.LoyaltyPoolBonus();
            int perk = CompanionPerks.PoolBonus(c);   // K4: Trusted Loyal
            total += contribution + bonus + perk;
            readout.Append($" + {c.Name} {contribution + bonus + perk} (⌊{c.BaseHP}/2⌋" +
                           $"{(bonus > 0 ? $" +{bonus} {c.GetLoyaltyTier()}" : "")}" +
                           $"{(perk > 0 ? $" +{perk} Loyal perk" : "")})");
        }
        readout.Append($" = {total}");
        GD.Print(readout.ToString());
        return total;
    }

    private void BuildEquipmentLoadouts()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        EquipmentLoadout.BuildForRun(save.Armory, "wizard",
            save.ActivePartyCompanionIds ?? new List<string>());

        // Q3 (§4b) readout: party traversal resistance at a glance, once at deploy.
        int cw = EquipmentLoadout.PartyCorruptionWard();
        int hw = EquipmentLoadout.PartyHazardWard();
        if (cw > 0 || hw > 0)
            GD.Print($"[PartyResist] CorruptionWard {cw}, HazardWard {hw} (+ Pathfinder per-terrain).");

        // W3 readout: the supply-line terms this expedition operates under.
        GD.Print($"[PartyResist] Supply range {SupplyRange} from the nearest anchor; beyond it " +
                 $"+{LeashDrainPerBand} HP/step per {LeashBandWidth} hexes (cap {LeashBandCap} bands). " +
                 "Wards do not apply to leash drain. " +
                 "S4.2: steps along road edges pay no leash or terrain drain (corruption still applies).");
    }

    private void EnsureEncounterRouter()
    {
        if (EncounterRouter.Instance == null)
        {
            var router = new EncounterRouter { Name = "EncounterRouter" };
            GetTree().Root.AddChild(router);
        }

        // ALWAYS claim the return path: the router is a persistent singleton that
        // survives scene changes, so if the retired OverworldRunManager (or a prior
        // session) created it pointing at the old OverworldScene, combat would
        // return THERE instead of the expedition window. Set it every _Ready.
        EncounterRouter.Instance.CombatScenePath = "res://Scenes/Combat/Battlefield.tscn";
        EncounterRouter.Instance.OverworldScenePath = "res://Scenes/Overworld/ExpeditionScene.tscn";
    }

    private static string TerrainDisplayName(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.ArcaneGround => "Arcane Ground",
        _ => t.ToString(),
    };
}
