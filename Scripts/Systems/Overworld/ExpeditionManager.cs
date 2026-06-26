using Godot;
using System.Collections.Generic;

// ============================================================
// ExpeditionManager.cs
//
// Purpose:        Top-level controller for ONE bounded expedition
//                 onto the persistent world. Replaces the region-
//                 generation lifecycle of OverworldRunManager with
//                 the single-world model:
//                   DEPLOY  — build a radius-R window of WorldData
//                             around the chosen staging point.
//                   OPERATE — move / fight / negotiate inside the
//                             window; reveal tiles, which write
//                             straight back into Cycle.World.
//                   EXTRACT — voluntary or range-exhausted; bank
//                             discoveries + new staging points,
//                             save, return to the strategic view.
//                 The world is authoritative and resident in
//                 CycleState.World, so there is NO seed reproduction
//                 and NO fog save/restore — combat round-trips just
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
    [Export] public int OperatingRange = 40;   // step budget for one sortie (crosses a window + probes onward)
    [Export] public int ExhaustionDamagePerStep = 10;

    // ── Runtime resource state (rides EncounterRouter across combat) ─────
    public int StepsRemaining { get; set; }
    public int CurrentHP { get; set; }
    public int MaxHP { get; set; }
    public int GoldEarned { get; set; }
    public int SplinterEarned { get; set; }
    public int EncountersWon { get; set; }
    public bool ExpeditionComplete { get; private set; }

    // ── World + window ──────────────────────────────────────────────────
    private WorldData _world;
    private WorldWindowBuilder _window;
    private int _stagingCol, _stagingRow;

    // ── Nodes ───────────────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private FogOfWarManager _fog;
    private OverworldPartyToken _party;
    private OverworldFactionManager _factionManager;
    private Camera2D _camera;
    private NarrativeEncounterPanel _narrativePanel;
    private ScoutReportPanel _scoutPanel;
    private List<NarrativeEncounterData> _encounterPool;

    // ── Pending combat (scout panel) ────────────────────────────────────
    private Vector2I? _pendingCombatHexCoord = null;
    private EncounterDefinition _pendingEncounter = null;
    private string _pendingTerrain = null;
    private float _scaledDifficultyMult = 1.0f;
    private bool _ambushPending = false;
    private const int PatrolRecoverySteps = 8;
    private const int PatrolShakeSteps = 5;

    // ── UI ──────────────────────────────────────────────────────────────
    private Label _stepLabel, _hpLabel, _infoLabel, _windowLabel;
    private Button _extractButton, _returnButton;
    private bool _cameraFreeMode = false;
    private const float CameraPanSpeed = 400f;

    private const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";

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
            GD.PrintErr("ExpeditionManager: no active cycle — cannot deploy.");
            return;
        }
        _world = cycle.World;

        // ── Staging point + radius from the deploy handoff ───────────────
        _stagingCol = PlayerSession.ExpeditionStagingCol;
        _stagingRow = PlayerSession.ExpeditionStagingRow;
        if (PlayerSession.ExpeditionWindowRadius > 0)
            WindowRadius = PlayerSession.ExpeditionWindowRadius;

        BuildEquipmentLoadouts();

        // ── Build the window grid (WindowMode = no self-generation) ──────
        _grid = new OverworldHexGrid { Name = "WindowGrid", WindowMode = true };
        AddChild(_grid);

        _window = new WorldWindowBuilder(_world, _stagingCol, _stagingRow, WindowRadius);
        _window.Build(_grid);

        // Fog manager (child of grid, same as before)
        _fog = new FogOfWarManager { Name = "FogOfWar" };
        _grid.AddChild(_fog);

        // Faction patrols — keyed to the staging tile's kingdom, if any.
        _factionManager = new OverworldFactionManager { Name = "FactionManager" };
        _grid.AddChild(_factionManager);
        string stagingKingdom = _world.GetTile(_stagingCol, _stagingRow).KingdomId ?? "";
        _factionManager.Initialize(_grid, stagingKingdom, cycle.Campaign);
        _factionManager.PatrolCapturedPlayer += OnPatrolCapturedPlayer;

        // Party token
        _party = new OverworldPartyToken { Name = "PartyToken" };
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
        EncountersWon = 0;
        ExpeditionComplete = false;

        PlayerSession.ClearRunState();
        var bonuses = BuildingEffectApplier.CalculateRunBonuses(SaveManager.ActiveSave);
        BuildingEffectApplier.ApplyCampusEffects(SaveManager.ActiveSave);
        MaxHP += bonuses.BonusHP;
        CurrentHP = MaxHP;
        StepsRemaining += bonuses.BonusSteps;
        GoldEarned += bonuses.BonusGold;

        PlayerSession.IsOnExpedition = true;
        if (PlayerSession.DebugMode && PlayerSession.StartWithGold)
            GoldEarned += 5000;
        if (PlayerSession.DebugMode && PlayerSession.StartWithSplinters)
            SplinterEarned += 5000;

        // ── Place party / restore from combat ────────────────────────────
        if (router != null && router.HasPendingReturn)
        {
            RestoreFromCombat(router);
        }
        else
        {
            _party.Initialize(_grid, _fog, _window.PartyStartLocal);
            // Reveal-on-deploy: the staging tile and its vision write to World.
            WriteVisibleToWorld();
            ShowInfo("Expedition deployed. Explore the region; extract before your range runs out.");

            if (PlayerSession.DebugMode && PlayerSession.NoFog)
                RevealAllFog();
        }

        // Narrative panel + pool (keyed to the staging kingdom)
        _narrativePanel = new NarrativeEncounterPanel { Visible = false };
        GetHudCanvas().AddChild(_narrativePanel);
        _encounterPool = NarrativeEncounterLoader.LoadForRegion(StagingTemplateRegion());

        // Wire signals
        _grid.HexClicked += OnHexClicked;
        _party.PartyMoved += OnPartyMoved;
        _party.PartyArrived += OnPartyArrived;

        CenterCamera();
        UpdateUI();
    }

    // ════════════════════════════════════════════════════════════════════
    // Discovery write-back — the heart of the single-world model
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Push every currently-revealed window tile into Cycle.World as
    /// Explored, and mark any revealed POIs discovered. Called after each move
    /// (and on deploy). Cheap: only flips tiles that changed. Marks the save
    /// dirty so the periodic SaveIfDirty flush persists it.</summary>
    private void WriteVisibleToWorld()
    {
        bool changed = false;

        foreach (var kvp in _grid.Hexes)
        {
            var local = kvp.Key;
            var hex = kvp.Value;
            if (hex.Fog != OverworldHex.FogState.Revealed)
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

                // Settlements grant staging the moment they're DISCOVERED — a
                // friendly hub, no fight needed. (Outposts/seats still grant on
                // being secured, via OnPartyArrived/GrantStagingPointAt.)
                if (poi.Kind == PoiKind.Settlement && poi.GrantsStaging)
                    GrantStagingPointAt(local);
            }
        }

        if (changed)
            SaveManager.MarkDirty();
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
    private void GrantStagingPointAt(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return;
        var poi = _world.PoiAt(col, row);
        if (poi == null || !poi.GrantsStaging)
            return;

        // Already a staging point? Skip.
        foreach (var sp in _world.StagingPoints)
            if (sp.X == col && sp.Y == row)
                return;

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
            _world.Tiles[idx].IsStagingPoint = true;

        SaveManager.MarkDirty();
        ShowInfo($"New staging point secured: {name}. Future expeditions can launch from here.");
    }

    // ════════════════════════════════════════════════════════════════════
    // Movement / POI handlers (lifted from OverworldRunManager, de-objectived)
    // ════════════════════════════════════════════════════════════════════

    private void OnPartyMoved(Vector2I newCoord, Vector2I oldCoord)
    {
        int stepCost = 1, hpDrain = 0;
        if (_grid.Hexes.TryGetValue(newCoord, out var hex))
        {
            stepCost = GetTerrainStepCost(hex.Terrain);
            hpDrain = GetTerrainHPDrain(hex.Terrain);
        }

        if (!(PlayerSession.DebugMode && PlayerSession.UnlimitedSteps))
        {
            if (StepsRemaining > 0)
                StepsRemaining = Mathf.Max(0, StepsRemaining - stepCost);
            else
            {
                // Range exhausted: each further step costs HP. Forced extraction
                // when HP would run out is handled below.
                CurrentHP -= ExhaustionDamagePerStep;
                if (CurrentHP <= 0)
                { CurrentHP = 0; FailExpedition("Stranded beyond your range."); return; }
            }

            if (hpDrain > 0)
            {
                CurrentHP -= hpDrain;
                ShowInfo($"Hazardous terrain! Lost {hpDrain} HP.");
                if (CurrentHP <= 0)
                { CurrentHP = 0; FailExpedition("Lost to the wilds."); return; }
            }

            // Corruption attrition: crossing corrupted ground bleeds you. Light at
            // the creeping edge, heavy in the convergence core — so the spreading
            // corruption is a hostile zone to route around, not stroll through.
            int corruptionDrain = CorruptionDrainAt(newCoord);
            if (corruptionDrain > 0)
            {
                CurrentHP -= corruptionDrain;
                ShowInfo($"The corruption sears you! Lost {corruptionDrain} HP.");
                if (CurrentHP <= 0)
                { CurrentHP = 0; FailExpedition("Consumed by corruption."); return; }
            }
        }

        // Reveal-on-move writes straight into World.
        WriteVisibleToWorld();

        // Patrols tick once per step.
        if (_factionManager != null && !ExpeditionComplete)
            _factionManager.Tick(_party.CurrentCoord);

        // Durability flush — THROTTLED. The cycle file is large (the whole world
        // array), so saving every move stutters. Autosave at most once every few
        // seconds; real checkpoints (combat entry, outpost, extract) save directly.
        ThrottledAutosave();

        // Range warning + auto-extract offer.
        if (StepsRemaining == 0 && !ExpeditionComplete)
            ShowInfo("Operating range spent. Extract now, or press on at the cost of HP.");

        CenterCamera();
        UpdateUI();
    }

    private void OnHexClicked(Vector2I axial)
    {
        if (ExpeditionComplete)
            return;
        _party.TryMoveTo(axial);
    }

    private void OnPartyArrived(Vector2I coord)
    {
        if (ExpeditionComplete || _ambushPending)
            return;
        if (!_grid.Hexes.TryGetValue(coord, out var hex))
            return;
        if (hex.POI == OverworldHex.POIType.None || hex.POIConsumed)
            return;

        var poiType = hex.POI;
        if (PlayerSession.DebugMode && PlayerSession.ForceNextEncounterType >= 0)
        {
            poiType = (OverworldHex.POIType)PlayerSession.ForceNextEncounterType;
            PlayerSession.ForceNextEncounterType = -1;
        }

        switch (poiType)
        {
            case OverworldHex.POIType.Combat:
                OpenScoutReport(coord, hex);
                break;

            case OverworldHex.POIType.Rest:
                int heal = MaxHP / 4;
                CurrentHP = Mathf.Min(CurrentHP + heal, MaxHP);
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                ConsumeWorldPoi(coord);
                int restSpl = SplinterDropTable.RestSite();
                SplinterEarned += restSpl;
                GoldEarned += 15;
                ShowInfo($"Rest site. Recovered {heal} HP. +{restSpl} Arcane Splinters.");
                UpdateUI();
                break;

            case OverworldHex.POIType.Narrative:
                TriggerNarrativeEncounter(hex, coord);
                break;

            case OverworldHex.POIType.Negotiation:
                TriggerNegotiationEncounter(hex, coord);
                break;

            case OverworldHex.POIType.Outpost:
                // Full-heal checkpoint + grants a staging point (world-scale reward).
                CurrentHP = MaxHP;
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                ConsumeWorldPoi(coord);
                GrantStagingPointAt(coord);
                int outSpl = SplinterDropTable.RestSite();
                SplinterEarned += outSpl;
                GoldEarned += 25;
                SaveManager.SaveIfDirty(); // checkpoint
                ShowInfo($"Outpost secured. Fully rested. +{outSpl} Arcane Splinters.");
                UpdateUI();
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Combat routing (verbatim from OverworldRunManager, world-sourced)
    // ════════════════════════════════════════════════════════════════════

    private void OpenScoutReport(Vector2I coord, OverworldHex hex)
    {
        string terrainType = hex.Terrain.ToString();
        string regionId = StagingTemplateRegion();
        var tier = EncounterTier.Battle;
        var encounterDef = EncounterPoolLoader.Pick(regionId, tier, terrainType, _scaledDifficultyMult);

        _pendingCombatHexCoord = coord;
        _pendingEncounter = encounterDef;
        _pendingTerrain = terrainType;

        _scoutPanel.OnEngage = () =>
        {
            if (_pendingCombatHexCoord.HasValue && _pendingEncounter != null)
                CommitCombat(_pendingCombatHexCoord.Value, _pendingEncounter, _pendingTerrain);
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

        int stepCost = GetTerrainStepCost(hex.Terrain);
        _scoutPanel.Show(encounterDef, hex.Terrain.ToString(), stepCost);
    }

    private void CommitCombat(Vector2I hexCoord, EncounterDefinition encounterDef, string terrainType)
    {
        var router = EncounterRouter.Instance;
        if (router == null)
        { GD.PrintErr("ExpeditionManager: EncounterRouter missing."); return; }

        // Save only the RESOURCE state — the world (and thus the map) is resident.
        router.SavedStepsRemaining = StepsRemaining;
        router.SavedCurrentHP = CurrentHP;
        router.SavedGoldEarned = GoldEarned;
        router.SavedSplinterEarned = SplinterEarned;
        router.SavedEncountersWon = EncountersWon;
        router.SavedPartyCoord = _party.CurrentCoord;
        router.SavedCombatHexCoord = hexCoord;
        router.HasPendingReturn = false;

        if (_factionManager != null)
        {
            router.SavedPatrolPositions = _factionManager.GetPatrolPositions();
            router.SavedPatrolCooldowns = _factionManager.GetPatrolCooldowns();
            router.SavedPatrolArchmageId = _factionManager.GetArchmageId();
        }

        // Persist discovery so far before leaving the scene.
        SaveManager.SaveIfDirty();

        EncounterContextCarrier.Set(encounterDef);
        EncounterContextCarrier.SetContext(terrainType, encounterDef.Tier);
        router.SetCurrentTier(encounterDef.Tier);

        ShowInfo("Entering combat...");
        GetTree().ChangeSceneToFile(router.CombatScenePath);
    }

    private void OnPatrolCapturedPlayer(Vector2I coord, string archmageId)
    {
        if (ExpeditionComplete || _ambushPending)
            return;
        if (!_grid.Hexes.TryGetValue(coord, out var hex))
            return;

        _ambushPending = true;
        ShowInfo("A patrol has intercepted you!");
        var encounterDef = EncounterPoolLoader.Pick(
            StagingTemplateRegion(), EncounterTier.Skirmish, hex.Terrain.ToString(), _scaledDifficultyMult);
        CommitCombat(coord, encounterDef, hex.Terrain.ToString());
    }

    // ════════════════════════════════════════════════════════════════════
    // Combat return — rebuild the SAME window; no seed/fog replay
    // ════════════════════════════════════════════════════════════════════

    private void RestoreFromCombat(EncounterRouter router)
    {
        StepsRemaining = router.SavedStepsRemaining;
        CurrentHP = router.SavedCurrentHP;
        GoldEarned = router.SavedGoldEarned;
        SplinterEarned = router.SavedSplinterEarned;
        EncountersWon = router.SavedEncountersWon;

        // The window was rebuilt fresh in _Ready from World; discovery is already
        // correct (it lives in World). Just place the party and re-reveal vision.
        _party.Initialize(_grid, _fog, GridLocalOf(router.SavedPartyCoord));
        WriteVisibleToWorld();

        var resultHex = router.SavedCombatHexCoord;

        if (NegotiationContext.HasResult)
        {
            OnNegotiationReturned(resultHex);
        }
        else if (router.CombatWon)
        {
            GoldEarned += router.GoldReward;
            SplinterEarned += router.SplinterReward;
            EncountersWon++;
            if (_grid.Hexes.TryGetValue(resultHex, out var hex))
            { hex.POIConsumed = true; hex.RefreshVisuals(); }
            ConsumeWorldPoi(resultHex);
            GrantStagingPointAt(resultHex); // securing a seat/settlement via combat can grant staging
            ShowInfo($"Victory! +{router.GoldReward} gold, +{router.SplinterReward} Splinters.");
        }
        else
        {
            CurrentHP -= router.DamageTaken;
            if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
                CurrentHP = Mathf.Max(1, CurrentHP);
            if (_grid.Hexes.TryGetValue(resultHex, out var hex))
            { hex.POIConsumed = true; hex.RefreshVisuals(); }
            ConsumeWorldPoi(resultHex);
            if (CurrentHP <= 0)
            { CurrentHP = 0; FailExpedition("Defeated in the field."); return; }
            ShowInfo($"Defeated... Lost {router.DamageTaken} HP.");
        }

        router.HasPendingReturn = false;

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

    private void TriggerNarrativeEncounter(OverworldHex hex, Vector2I coord)
    {
        string terrainName = hex.Terrain.ToString();
        var completedIds = SaveManager.ActiveSave?.CompletedEvents;
        var encounter = NarrativeEncounterLoader.PickRandom(_encounterPool, terrainName, completedIds);

        hex.POIConsumed = true;
        hex.RefreshVisuals();
        ConsumeWorldPoi(coord);

        if (encounter == null)
        {
            int gold = 15 + (int)(GD.Randf() * 20);
            GoldEarned += gold;
            ShowInfo($"You find something of value here. (+{gold} gold)");
            UpdateUI();
            return;
        }
        _narrativePanel.ShowEncounter(encounter);
        _narrativePanel.OnCompleted = (choice) => OnNarrativeCompleted(encounter, choice);
    }

    private void OnNarrativeCompleted(NarrativeEncounterData encounter, EncounterChoice choice)
    {
        if (choice == null)
            return;
        if (choice.GoldDelta != 0)
            GoldEarned = Mathf.Max(0, GoldEarned + choice.GoldDelta);
        if (choice.HPDelta != 0)
        {
            CurrentHP = Mathf.Clamp(CurrentHP + choice.HPDelta, 0, MaxHP);
            if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
                CurrentHP = Mathf.Max(1, CurrentHP);
            if (CurrentHP <= 0)
            { FailExpedition("Lost to a fateful choice."); return; }
        }
        if (choice.StepDelta != 0)
            StepsRemaining = Mathf.Max(0, StepsRemaining + choice.StepDelta);

        int spl = SplinterDropTable.Narrative();
        SplinterEarned += spl;

        if (SaveManager.ActiveSave != null && !string.IsNullOrEmpty(encounter.Id))
            if (!SaveManager.ActiveSave.CompletedEvents.Contains(encounter.Id))
                SaveManager.ActiveSave.CompletedEvents.Add(encounter.Id);

        if (choice.SetFlags != null && SaveManager.ActiveSave != null)
            foreach (var flag in choice.SetFlags)
                if (!SaveManager.ActiveSave.CompletedEvents.Contains(flag))
                    SaveManager.ActiveSave.CompletedEvents.Add(flag);

        ShowInfo($"Encounter resolved. +{spl} Arcane Splinters.");
        UpdateUI();
    }

    private void TriggerNegotiationEncounter(OverworldHex hex, Vector2I coord)
    {
        hex.POIConsumed = true;
        hex.RefreshVisuals();
        ConsumeWorldPoi(coord);

        string kingdomId = StagingTemplateRegion();
        string terrain = hex.Terrain.ToString();
        var encounter = NegotiationEncounterLoader.PickForTerrain(terrain, kingdomId);
        if (encounter == null)
        { ShowInfo("A potential contact slips away."); UpdateUI(); return; }

        NegotiationContext.Clear();
        NegotiationContext.EncounterId = encounter.Id;
        NegotiationContext.HexCoordKey = $"{coord.X},{coord.Y}";

        var router = EncounterRouter.Instance;
        if (router != null)
        {
            router.SavedStepsRemaining = StepsRemaining;
            router.SavedCurrentHP = CurrentHP;
            router.SavedGoldEarned = GoldEarned;
            router.SavedSplinterEarned = SplinterEarned;
            router.SavedEncountersWon = EncountersWon;
            router.SavedPartyCoord = _party.CurrentCoord;
            router.SavedCombatHexCoord = coord;
            router.HasPendingReturn = true;
        }
        SaveManager.SaveIfDirty();
        ShowInfo($"Negotiation: {encounter.Title}");
        GetTree().ChangeSceneToFile("res://Scenes/Negotiation/NegotiationScene.tscn");
    }

    private void OnNegotiationReturned(Vector2I hexCoord)
    {
        if (NegotiationContext.DealAccepted)
        {
            GoldEarned = Mathf.Max(0, GoldEarned + NegotiationContext.GoldDelta);
            if (SaveManager.ActiveSave != null && !string.IsNullOrEmpty(NegotiationContext.FactionId))
            {
                var rep = SaveManager.ActiveSave.FactionReputation;
                string f = NegotiationContext.FactionId;
                rep[f] = rep.TryGetValue(f, out int cur) ? cur + NegotiationContext.ReputationDelta
                                                          : NegotiationContext.ReputationDelta;
            }
            ShowInfo($"Deal struck. Gold: {(NegotiationContext.GoldDelta >= 0 ? "+" : "")}{NegotiationContext.GoldDelta}");
        }
        else
            ShowInfo("No deal reached.");
        NegotiationContext.Clear();
        UpdateUI();
    }

    // ════════════════════════════════════════════════════════════════════
    // Extraction / failure
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Voluntary or range-forced extraction: bank everything, save,
    /// return to the strategic view. Discoveries are already in World.</summary>
    private void Extract()
    {
        if (ExpeditionComplete)
            return;
        ExpeditionComplete = true;
        PlayerSession.IsOnExpedition = false;

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        BankResources(extracted: true);
        ShowInfo($"Extracted. Gold: {GoldEarned}, Splinters: {SplinterEarned}, Encounters: {EncountersWon}.");
        ShowReturnButton();
        EmitSignal(SignalName.ExpeditionEnded, true);
    }

    private void FailExpedition(string reason)
    {
        if (ExpeditionComplete)
            return;
        ExpeditionComplete = true;
        PlayerSession.IsOnExpedition = false;

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        // Failure still banks DISCOVERY (it's in World) but forfeits unbanked gold.
        BankResources(extracted: false);
        ShowInfo($"Expedition failed: {reason} Discoveries retained; unbanked spoils lost.");
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
            save.RunsWon++;
        }
        else
        {
            // Failure: keep a fraction, or nothing — design knob. Keep splinters,
            // lose loose gold, to match "discoveries retained, spoils lost."
            save.ArcaneSplinters += SplinterEarned;
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
            OffsetTop = 12,
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

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        hudPanel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        margin.AddChild(vbox);

        _stepLabel = MakeHudLabel();
        vbox.AddChild(_stepLabel);
        _hpLabel = MakeHudLabel();
        vbox.AddChild(_hpLabel);
        vbox.AddChild(new HSeparator());
        _windowLabel = MakeHudLabel();
        vbox.AddChild(_windowLabel);
        vbox.AddChild(new HSeparator());
        _infoLabel = MakeHudLabel();
        _infoLabel.Modulate = UITheme.OverworldInfoLabelTint;
        _infoLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_infoLabel);

        // Extract button (always available — voluntary extraction).
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
            OffsetTop = 12,
            OffsetBottom = 52,
        };
        _extractButton.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
        UITheme.ApplyButtonStyle(_extractButton, isPrimary: true);
        _extractButton.Pressed += Extract;
        _hudCanvas.AddChild(_extractButton);

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
    }

    private void ShowReturnButton()
    {
        if (_extractButton != null)
            _extractButton.Visible = false;
        if (_returnButton != null)
            _returnButton.Visible = true;
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
        _stepLabel.Text = (PlayerSession.DebugMode && PlayerSession.UnlimitedSteps)
            ? "Range: ∞ [DEBUG]"
            : $"Range: {StepsRemaining} / {OperatingRange}";
        _stepLabel.Modulate = StepsRemaining > 5 ? Colors.White : UITheme.OverworldLowResourceWarning;

        _hpLabel.Text = $"HP: {CurrentHP} / {MaxHP}";
        _hpLabel.Modulate = CurrentHP > MaxHP / 3 ? Colors.White : UITheme.OverworldLowResourceWarning;

        int explored = 0;
        foreach (var h in _grid.Hexes.Values)
            if (h.Fog == OverworldHex.FogState.Revealed)
                explored++;
        _windowLabel.Text = $"Window explored: {explored} / {_grid.Hexes.Count}";

        if (_grid.Hexes.TryGetValue(_party.CurrentCoord, out var cur))
            _windowLabel.Text += $"  |  {cur.Terrain}";
    }

    private void ShowInfo(string message)
    {
        _infoLabel.Text = message;
        GD.Print($"[Expedition] {message}");
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private string StagingKingdom()
        => _world.GetTile(_stagingCol, _stagingRow).KingdomId ?? "frontier_wilds";

    /// <summary>The content template region for the staging kingdom — the real
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

    /// <summary>Map a stored grid-local coord through the window (identity — the
    /// window rebuild uses the same staging point, so local coords are stable).</summary>
    private Vector2I GridLocalOf(Vector2I savedLocal) => savedLocal;

    private void RevealAllFog()
    {
        foreach (var hex in _grid.Hexes.Values)
        {
            hex.Fog = OverworldHex.FogState.Revealed;
            hex.RefreshVisuals();
        }
        WriteVisibleToWorld();
    }

    private int GetTerrainStepCost(OverworldHex.TerrainType terrain) => terrain switch
    {
        OverworldHex.TerrainType.Road => 1,
        OverworldHex.TerrainType.Grassland => 1,
        OverworldHex.TerrainType.ArcaneGround => 1,
        OverworldHex.TerrainType.Forest => 2,
        OverworldHex.TerrainType.Ruins => 2,
        OverworldHex.TerrainType.Swamp => 2,
        OverworldHex.TerrainType.Mountain => 3,
        OverworldHex.TerrainType.Volcanic => 2,
        _ => 1,
    };

    private int GetTerrainHPDrain(OverworldHex.TerrainType terrain) => terrain switch
    {
        OverworldHex.TerrainType.Swamp => 3,
        OverworldHex.TerrainType.Volcanic => GD.Randf() < 0.3f ? 5 : 0,
        _ => 0,
    };

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

    private int ComputePartyBaseHP()
    {
        const int WizardBaseHP = 20;
        int total = WizardBaseHP;
        var save = SaveManager.ActiveSave;
        if (save == null)
            return total;
        foreach (var id in save.ActivePartyCompanionIds)
        {
            var c = save.Companions.Find(c => c.Id == id && c.IsRecruited && !c.IsPermadead);
            if (c != null)
                total += c.BaseHP;
        }
        return total;
    }

    private void BuildEquipmentLoadouts()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        EquipmentLoadout.BuildForRun(save.Armory, "wizard",
            save.ActivePartyCompanionIds ?? new List<string>());
    }

    private void EnsureEncounterRouter()
    {
        if (EncounterRouter.Instance == null)
        {
            var router = new EncounterRouter { Name = "EncounterRouter" };
            GetTree().Root.AddChild(router);
        }

        // ALWAYS claim the return path — the router is a persistent singleton that
        // survives scene changes, so if the retired OverworldRunManager (or a prior
        // session) created it pointing at the old OverworldScene, combat would
        // return THERE instead of the expedition window. Set it every _Ready.
        EncounterRouter.Instance.CombatScenePath = "res://Scenes/Combat/Battlefield.tscn";
        EncounterRouter.Instance.OverworldScenePath = "res://Scenes/Overworld/ExpeditionScene.tscn";
    }
}
