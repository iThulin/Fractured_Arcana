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

    // ── W1: sliding window (claude/expedition_window_sliding_v1) ─────────
    /// <summary>Debug A/B lever: true restores the old fixed-perimeter window
    /// (no sliding). Off by default — the wall is gone; range is governed by
    /// the step/HP economy plus the W3 supply leash below.</summary>
    [Export] public bool HardWindowMode = false;

    /// <summary>Hexes of party drift from the window center before the loaded
    /// window slides to follow. Small enough that the loaded edge always stays
    /// far beyond vision range; large enough that pacing doesn't thrash.</summary>
    [Export] public int RecenterThreshold = 3;

    // ── W3: soft leash — the supply line ──────────────────────────────────
    /// <summary>Hex distance from the nearest supply anchor (this expedition's
    /// staging tile, or any Available staging point — including outposts
    /// secured mid-run) within which no leash drain applies.</summary>
    [Export] public int SupplyRange = 12;

    /// <summary>Width in hexes of each leash band beyond SupplyRange.</summary>
    [Export] public int LeashBandWidth = 3;

    /// <summary>HP-pool drain per step, per band beyond supply. Deliberately
    /// NOT reducible by HazardWard/CorruptionWard (Q3) — the leash is its own
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

    // ── S3: Retrace memory (Chronomancer) — the last committed move, so the
    // sole G1 exception can undo it. Cleared when a scene swap makes the
    // "last step" ambiguous (combat/negotiation) and after use. ─────────────
    private Vector2I _lastMoveFrom;
    private int _lastMoveStepCost;
    private bool _hasLastMove = false;

    // ── Runtime resource state (rides EncounterRouter across combat) ─────
    public int StepsRemaining { get; set; }
    public int CurrentHP { get; set; }
    public int MaxHP { get; set; }

    /// <summary>Casualty summary from the most recent §5b wipe roll, consumed
    /// by FailExpedition's banner so the human cost is visible at the moment
    /// of failure (K2 UX).</summary>
    private string _casualtyNote;
    public int GoldEarned { get; set; }
    public int SplinterEarned { get; set; }
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

    // ── Nodes ───────────────────────────────────────────────────────────
    private OverworldHexGrid _grid;
    private FogOfWarManager _fog;
    private OverworldPartyToken _party;
    private OverworldFactionManager _factionManager;
    private RoamerToken _roamer;
    private bool _roamerSpent;
    private Camera2D _camera;
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
    /// archmage's own pool ("" otherwise) — dossier attribution (spec §4).</summary>
    private string _pendingCombatArchmageId = "";
    private float _scaledDifficultyMult = 1.0f;
    private bool _ambushPending = false;
    private const int PatrolRecoverySteps = 8;
    private const int PatrolShakeSteps = 5;

    // ── UI ──────────────────────────────────────────────────────────────
    private Label _stepLabel, _hpLabel, _infoLabel, _windowLabel;
    private Button _extractButton, _returnButton, _ledgerButton;
    private bool _cameraFreeMode = false;
    private const float CameraPanSpeed = 400f;

    private const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    private Label _hoverTooltip;

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

        // Warfront intervention? The cycle carries the pending front id across the
        // deploy → combat → return round-trips, so this stays true for the whole run.
        _isWarfront = !string.IsNullOrEmpty(cycle.PendingWarfrontId);
        if (_isWarfront)
        {
            var awf = cycle.Warfronts?.Find(w => w.Id == cycle.PendingWarfrontId);
            if (awf != null && awf.HasStronghold)
            { _strongholdCol = awf.StrongholdCol; _strongholdRow = awf.StrongholdRow; }
        }

        BuildEquipmentLoadouts();

        // ── Build the window grid (WindowMode = no self-generation) ──────
        _grid = new OverworldHexGrid { Name = "WindowGrid", WindowMode = true };
        AddChild(_grid);

        _window = new WorldWindowBuilder(_world, _stagingCol, _stagingRow, WindowRadius);

        // On a combat/negotiation return the party may be far outside the base
        // disc — build the initial window around where they'll actually be
        // placed, instead of 469 tiles at staging that the restore recenter
        // would immediately free. (Fresh deploys — and HardWindowMode, where
        // the party can never leave the base disc — build at staging.)
        bool pendingReturn = router != null && router.HasPendingReturn;
        Vector2I initialCenter = (pendingReturn && !HardWindowMode)
            ? GridLocalOf(router.SavedPartyCoord)
            : _window.PartyStartLocal;
        _window.Build(_grid, initialCenter);
        _windowCenterLocal = initialCenter;
        StampCivicPois(); // S4.2: settlements/seats get their map marker

        // Fog manager (child of grid, same as before)
        _fog = new FogOfWarManager { Name = "FogOfWar" };
        _grid.AddChild(_fog);

        // Faction patrols — keyed to the staging tile's kingdom, if any.
        _factionManager = new OverworldFactionManager { Name = "FactionManager" };
        _grid.AddChild(_factionManager);
        // Patrols key off the TEMPLATE REGION (the campaign's archmage map is
        // keyed by region names like 'dustreach', not 'kingdom_N' ids).
        _factionManager.Initialize(_grid, StagingTemplateRegion(), cycle.Campaign);
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
        // Guard on ReturnSceneOverride too: a campus-pending return must never
        // be mis-consumed as an expedition return (Step 9 hardening).
        if (router != null && router.HasPendingReturn &&
            string.IsNullOrEmpty(router.ReturnSceneOverride))
        {
            RestoreFromCombat(router);
        }
        else
        {
            // K2.5: fresh expedition — everyone starts whole. (Combat returns
            // take the other branch and must NOT reset carried HP.)
            CompanionInjurySystem.ResetExpeditionHP(SaveManager.ActiveSave);
            PlayerSession.WizardExpeditionHP = -1; // K2.5 symmetry — wizard too

            // S4 (Identify) + S5 (True Names): pinned encounters are
            // expedition-scoped. Static so they survive combat round-trips
            // (the OverworldSpellEffects pattern); cleared here and on
            // every expedition-end path.
            _identifiedEncounters.Clear();
            _pinnedNegotiations.Clear();

            // Run journal: opens run_<id>.log/.csv under user://run_logs/.
            // ONLY on a fresh deploy — combat/negotiation returns take the
            // other branch and keep appending to the same run's files.
            RunEventLog.Begin(StagingTemplateRegion(),
                PlayerSession.SelectedSchool.ToString(),
                GoldEarned, SplinterEarned, CurrentHP, MaxHP, StepsRemaining);
            if (bonuses.BonusGold != 0 || bonuses.BonusHP != 0 || bonuses.BonusSteps != 0)
                LogRun("campus_bonus",
                    $"buildings: +{bonuses.BonusGold}g +{bonuses.BonusHP}maxHP +{bonuses.BonusSteps}steps");
            if (PlayerSession.DebugMode && (PlayerSession.StartWithGold || PlayerSession.StartWithSplinters))
                LogRun("debug_grant",
                    $"{(PlayerSession.StartWithGold ? "+5000g " : "")}{(PlayerSession.StartWithSplinters ? "+5000sp" : "")}".Trim());

            _party.Initialize(_grid, _fog, _window.PartyStartLocal);
            // Reveal-on-deploy: the staging tile and its vision write to World.
            WriteVisibleToWorld();
            ShowInfo(_isWarfront
                ? "Warfront — storm the besieging stronghold (marked), then extract to secure the front."
                : "Expedition deployed. Explore the region; extract before your range runs out.");

            if (PlayerSession.DebugMode && PlayerSession.NoFog)
                RevealAllFog();
        }

        // ── S2: overworld spellcasting — manager + Grimoire panel ────────
        // Fresh deploys reset the Essence pool / cast counts / beacons;
        // combat and negotiation returns keep them (they ride the save).
        _spells = new OverworldSpellManager { Name = "SpellManager" };
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

            // P3: seeing any footprint tile (charted or revealed) discovers the
            // whole shard sub-region — the vault layout then reads at distance.
            if ((hex.Fog == OverworldHex.FogState.Silhouette ||
                 hex.Fog == OverworldHex.FogState.Revealed) &&
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
            // persistent Charted corridor on the strategic map — the route
            // itself becomes a legible artifact of the expedition.
            if (hex.Fog == OverworldHex.FogState.Silhouette)
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

    /// <summary>P3: the first sighting of any footprint tile opens the whole vault
    /// layout — every footprint tile charts (reduced fog) and any loaded, still-
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
            if (_grid.Hexes.TryGetValue(local, out var h) &&
                h.Fog == OverworldHex.FogState.Hidden)
            {
                h.Fog = OverworldHex.FogState.Silhouette;
                h.RefreshVisuals();
            }
        }
        ShowInfo($"You have found {z.Name}. A shard of the Arcanum lies within its depths.");
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
        if (!PlayerSession.DebugMode || ExpeditionComplete)
            return;

        // F: mint test favors for the kingdom under the party (C3 testing).
        if (@event is InputEventKey { Pressed: true, Keycode: Key.F })
        {
            string kid = KingdomIdAt(_party.CurrentCoord);
            if (string.IsNullOrEmpty(kid))
            {
                ShowInfo("[DEBUG] No kingdom here — cannot mint test favors.");
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

        // N: [DEBUG] summon the narrative-chain proof rig without walking the map
        //    — cycles lost_traveler -> sealed_letter_delivery -> grateful_courier on
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
        // sanctum once the guardian is felled) and trigger arrival — P4 testing.
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

    /// <summary>[DEBUG] Summon the next chain encounter directly — ignores
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
        if (_party != null && _grid != null &&
            _grid.Hexes.TryGetValue(_party.CurrentCoord, out var dbgHex))
            dbgTerrain = dbgHex.Terrain;
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
                (f.StartsWith("fragment_") && f.EndsWith("_collected")));
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
        // first so hazard/corruption warnings on the same step overwrite it —
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
        if (_grid.Hexes.TryGetValue(newCoord, out var hex))
        {
            hpDrain = GetTerrainHPDrain(hex.Terrain);
            // Q3 (§4b): HazardWard reduces terrain drain, floored at 1 whenever the
            // terrain drains at all — relief is bought, immunity does not exist.
            if (hpDrain > 0)
                hpDrain = Mathf.Max(1, hpDrain - EquipmentLoadout.PartyHazardWard());
            // Edge-aware step cost: destination terrain, cheapened by a road on the
            // traveled edge, surcharged by an unbridged river ford. Read the shared
            // edge off the tile we're leaving (masks live on both sides). Q3 (§7b):
            // Pathfinder cheapens the matching terrain (floor 1 inside StepCost).
            _grid.Hexes.TryGetValue(oldCoord, out var fromHex);
            stepCost = OverworldMovementCost.StepCost(hex.Terrain, fromHex, oldCoord, newCoord,
                EquipmentLoadout.PartyPathfinder(hex.Terrain.ToString()));

            // S4.2 (user ruling 2026-07-16): a step traveled ALONG A ROAD is
            // safe going — see the drain sites below. Edge roads are the real
            // network; the vestigial Road TERRAIN tile counts too (old maps).
            roadTravel = OverworldMovementCost.EdgeHasRoad(fromHex, oldCoord, newCoord) ||
                         hex.Terrain == OverworldHex.TerrainType.Road;
        }

        // P5: inside a shard-zone footprint the party is in a contained designed
        // arena, not open wilderness. The three wilderness tolls (terrain,
        // corruption, supply leash) are suppressed below; step cost and
        // out-of-range exhaustion still apply. The lethal cost is the APPROACH to
        // the gate and the guardian fight, not attrition between gate and sanctum.
        bool inVault = InsideShardZone(newCoord);
        if (inVault && !_lastInVault)
            ShowInfo("Within the vault's bounds, the wilds' toll lifts \u2014 no terrain, corruption, or supply drain here.");
        _lastInVault = inVault;

        // S3 (Retrace): remember this move so it can be undone. Records the
        // cost actually charged (0 on the exhaustion path — HP is not refunded).
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
                LogRun("step", hex != null ? hex.Terrain.ToString() : "?",
                       stepsDelta: -stepsCharged, at: newCoord);
            }
            else
            {
                // Range exhausted: each further step costs HP. Forced extraction
                // when HP would run out is handled below.
                CurrentHP -= ExhaustionDamagePerStep;
                LogRun("exhaustion", "step beyond range",
                       hpDelta: -ExhaustionDamagePerStep, at: newCoord);
                if (CurrentHP <= 0)
                { CurrentHP = 0; FailExpedition("Stranded beyond your range."); return; }
            }

            // S4.2 (user ruling): the causeway spares you the terrain's bite —
            // a road step never pays hazard drain. (Corruption is NOT road-
            // exempt below: the creep eats roads too, and corridor-immunity
            // through corrupted ground would gut the G4 pressure.)
            if (hpDrain > 0 && inVault)
            {
                GD.Print($"[Expedition] Within the vault \u2014 {hpDrain} terrain drain suppressed.");
                hpDrain = 0;
            }
            if (hpDrain > 0 && roadTravel)
            {
                GD.Print($"[Expedition] The road spares you {hpDrain} terrain drain.");
                hpDrain = 0;
            }

            // S2: an active warding spell (Ember Ward) negates the terrain's
            // bite entirely — bounded window, not immunity (G4).
            if (hpDrain > 0 && OverworldSpellEffects.DrainSuppressed(hex.Terrain))
            {
                GD.Print($"[Spellcraft] Ward negates {hpDrain} terrain drain on {hex.Terrain}.");
                hpDrain = 0;
            }

            if (hpDrain > 0)
            {
                CurrentHP -= hpDrain;
                LogRun("terrain_drain", hex != null ? hex.Terrain.ToString() : "?",
                       hpDelta: -hpDrain, at: newCoord);
                ShowInfo($"Hazardous terrain! Lost {hpDrain} HP.");
                if (CurrentHP <= 0)
                { CurrentHP = 0; FailExpedition("Lost to the wilds."); return; }
            }

            // Corruption attrition: crossing corrupted ground bleeds you. Light at
            // the creeping edge, heavy in the convergence core — so the spreading
            // corruption is a hostile zone to route around, not stroll through.
            int corruptionDrain = CorruptionDrainAt(newCoord);
            if (corruptionDrain > 0 && inVault)
            {
                GD.Print($"[Expedition] Within the vault \u2014 {corruptionDrain} corruption drain suppressed.");
                corruptionDrain = 0;
            }
            // S2: Purifying Rite suppresses corruption attrition for its
            // window — bounded relief, never immunity (G4).
            if (corruptionDrain > 0 && OverworldSpellEffects.CorruptionSuppressed())
            {
                GD.Print($"[Spellcraft] Purifying Rite holds — {corruptionDrain} corruption drain suppressed.");
                corruptionDrain = 0;
            }
            if (corruptionDrain > 0)
            {
                // Q3 (§4b): CorruptionWard reduces the bleed, but Σ ward is CAPPED
                // at (tile corruption tier × 2) and drain never drops below 1 —
                // deep stacking is pointless past the tier you're actually walking.
                int tier = CorruptionTierAt(newCoord);
                int ward = Mathf.Min(EquipmentLoadout.PartyCorruptionWard(), tier * 2);
                corruptionDrain = Mathf.Max(1, corruptionDrain - ward);
                CurrentHP -= corruptionDrain;
                LogRun("corruption_drain", $"tier {tier}",
                       hpDelta: -corruptionDrain, at: newCoord);
                ShowInfo($"The corruption sears you! Lost {corruptionDrain} HP.");
                if (CurrentHP <= 0)
                { CurrentHP = 0; FailExpedition("Consumed by corruption."); return; }
            }

            // W3: the soft leash. Past supply range of the nearest anchor, each
            // step bleeds the pool — +1 HP per band of LeashBandWidth hexes,
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
                // S4.2 (user ruling): the road bears your supply — steps taken
                // along a road edge pay no leash drain, however far out. Leave
                // the road and the line snaps taut again. Early-game relief
                // for the lone wizard; the wilds stay priced.
                if (roadTravel)
                {
                    ShowInfo("The road bears your supply — safe going while you follow it.");
                }
                else
                {
                    int leashDrain = band * LeashDrainPerBand;
                    CurrentHP -= leashDrain;
                    LogRun("leash_drain", $"band {band}",
                           hpDelta: -leashDrain, at: newCoord);
                    ShowInfo($"Beyond your supply line ({(band > 1 ? $"band {band}" : "the fringe")}). Lost {leashDrain} HP.");
                    if (CurrentHP <= 0)
                    { CurrentHP = 0; FailExpedition("Lost beyond the supply line."); return; }
                }
            }
        }

        // W1: slide the loaded window to follow the party once it drifts far
        // enough from the current center. Fires at move START (this handler),
        // so tiles stream in while the token animates across the hex.
        if (!HardWindowMode &&
            _grid.Distance(_party.CurrentCoord, _windowCenterLocal) >= RecenterThreshold)
            RecenterWindow(_party.CurrentCoord);

        // S2: spell-effect windows tick per committed step; Arcane Ground
        // feeds the pool (+1, §5 — a terrain property); the school Attunement
        // re-applies around the new position BEFORE the discovery write so
        // its silhouettes chart in the same pass.
        OverworldSpellEffects.TickStep();
        if (_spells != null)
        {
            if (hex != null && hex.Terrain == OverworldHex.TerrainType.ArcaneGround)
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
        if (hex.Fog != OverworldHex.FogState.Revealed)
        {
            _hoverTooltip.Text = hex.Fog == OverworldHex.FogState.Silhouette
                ? "Charted — unexplored" + (_spells?.TooltipSilhouetteExtra(hex) ?? "")
                : "Unexplored";
        }
        else
        {
            string line = TerrainDisplayName(hex.Terrain);
            if (hex.POI != OverworldHex.POIType.None && !hex.POIConsumed)
                line += $"  ·  {PoiSignal.Label(hex.POI, hex.Terrain, axial)}{_spells?.TooltipPoiExtra(hex) ?? ""}" +
                        NegotiationPreread(axial, hex); // S5: True Names
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

    /// <summary>True when the mouse is over any HUD element — the Godot
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

        // S4.2 (user request): never show the tile readout through UI — the
        // Grimoire, the stat panel, buttons, and the top bar all take
        // precedence. Runs every frame, so entering/leaving UI just works.
        if (MouseIsOverUi())
        {
            _hoverTooltip.Visible = false;
            return;
        }

        // Resolve the tile under the cursor from the mouse position, every frame.
        // (Area2D MouseEntered/Exited is unreliable here; InputEvent gives no exit
        // event — so we poll, which also fixes "tooltip won't hide off-grid".)
        Vector2 mouseWorld = _grid.GetGlobalMousePosition();
        Vector2I axial = _grid.WorldToAxial(_grid.ToLocal(mouseWorld));

        if (!_grid.Hexes.TryGetValue(axial, out var hex))
        {
            _hoverTooltip.Visible = false;
            return;
        }

        // Fog gate: don't reveal terrain the player hasn't explored.
        if (hex.Fog != OverworldHex.FogState.Revealed)
        {
            _hoverTooltip.Text = hex.Fog == OverworldHex.FogState.Silhouette
                ? "Charted — unexplored" + (_spells?.TooltipSilhouetteExtra(hex) ?? "")
                : "Unexplored";
        }
        else
        {
            string line = TerrainDisplayName(hex.Terrain);
            if (hex.POI != OverworldHex.POIType.None && !hex.POIConsumed)
                line += $"  ·  {PoiSignal.Label(hex.POI, hex.Terrain, axial)}{_spells?.TooltipPoiExtra(hex) ?? ""}" +
                        NegotiationPreread(axial, hex); // S5: True Names
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
        if (!_grid.Hexes.TryGetValue(coord, out var hex))
            return;

        // S3 (Deploy Waystation): standing on a deployed waystation consumes
        // its one rest charge — quarter-heal + 3 Essence, then it breaks down
        // (marker removed; it stops being a supply anchor).
        if (_window.TryLocalToWorld(coord, out int wcol, out int wrow))
        {
            var grimWs = SaveManager.ActiveSave?.Cycle?.Grimoire;
            string wsMark = $"{wcol},{wrow}";
            if (grimWs != null && grimWs.ActiveWaystations.Remove(wsMark))
            {
                int wsHeal = MaxHP / 4;
                CurrentHP = Mathf.Min(CurrentHP + wsHeal, MaxHP);
                _spells?.AddEssence(3, "Waystation");
                _grid.GetNodeOrNull($"WaystationMarker_{wcol}_{wrow}")?.QueueFree();
                SaveManager.MarkDirty();
                ShowInfo($"The waystation serves its purpose and breaks down. Recovered {wsHeal} HP.");
                UpdateUI();
            }
        }

        // P4: shard sub-region tiles carry NO POI, so handle them BEFORE the
        // POIType early-return. Gate -> guardian; sanctum (post-clear) -> collect.
        if (TryHandleShardZone(coord))
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
                // S2: Campward (§8) — the armed charge makes this Rest heal
                // +50% and grant +2 extra Essence, then is consumed.
                bool campward = OverworldSpellEffects.ConsumeCampward();
                if (campward)
                    heal += MaxHP / 8;
                int restHpBefore = CurrentHP;
                CurrentHP = Mathf.Min(CurrentHP + heal, MaxHP);
                _spells?.AddEssence(3 + (campward ? 2 : 0), campward ? "Rest + Campward" : "Rest");
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                ConsumeWorldPoi(coord);
                // K2.5 carry (2026-07-29): a rest also mends the party's
                // carried COMBAT HP — a quarter of max each, mirroring the
                // pool heal above. Stabilized (0) companions stay down.
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
                       goldDelta: +15, splinterDelta: +restSpl,
                       hpDelta: CurrentHP - restHpBefore, at: coord);
                ShowInfo($"Rest site{(campward ? " (Campward)" : "")}. Recovered {heal} HP. " +
                         $"+{restSpl} Arcane Splinters.");
                UpdateUI();
                break;

            case OverworldHex.POIType.Narrative:
                TriggerNarrativeEncounter(hex, coord);
                break;

            case OverworldHex.POIType.Negotiation:
                TriggerNegotiationEncounter(hex, coord);
                break;

            case OverworldHex.POIType.Prison:
                // Imprisonment rescue (§8): storming the gaol is a combat. Winning
                // releases the captive — handled on combat return in
                // RestoreFromCombat via ReleaseImprisonedAt(resultHex). Routes
                // through the ordinary scout->commit path so difficulty scaling and
                // patrol attribution behave normally.
                OpenScoutReport(coord, hex);
                break;

            case OverworldHex.POIType.Outpost:
                // Full-heal checkpoint + grants a staging point (world-scale reward).
                int outHpBefore = CurrentHP;
                CurrentHP = MaxHP;
                _spells?.RestoreEssenceFull(); // S2: Outpost = full Essence (§5)
                hex.POIConsumed = true;
                hex.RefreshVisuals();
                ConsumeWorldPoi(coord);
                GrantStagingPointAt(coord);
                // K2.5 carry (2026-07-29): an outpost is a full rest for the
                // fights too — carriers mend to full; the wizard fields fresh.
                // Stabilized (0) companions stay down (HealExpeditionHP skips 0).
                CompanionInjurySystem.HealExpeditionHP(SaveManager.ActiveSave, 1.0f);
                PlayerSession.WizardExpeditionHP = -1;
                int outSpl = SplinterDropTable.RestSite();
                SplinterEarned += outSpl;
                GoldEarned += 25;
                LogRun("outpost", "secured (full heal, staging point)",
                       goldDelta: +25, splinterDelta: +outSpl,
                       hpDelta: CurrentHP - outHpBefore, at: coord);
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
        // Warfront intervention fights the region's SIEGE pool — heavy compositions,
        // Dense maps (DensityForTier) — so relieving a siege feels like one.
        var tier = _isWarfront ? EncounterTier.Siege : EncounterTier.Battle;
        _scaledDifficultyMult = DifficultyMultAt(coord);

        // S4 (Identify): an identified site fights the PINNED composition —
        // what the spell showed is what you get (G5). Otherwise roll fresh.
        EncounterDefinition encounterDef = null;
        if (_window.TryLocalToWorld(coord, out int idCol, out int idRow))
            _identifiedEncounters.TryGetValue($"{idCol},{idRow}", out encounterDef);

        // Dossier attribution defaults to none (pinned/identified fights keep
        // no attribution — accepted limit; the pin predates this pass).
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
            // the tile's corruption level before the draw — same call shape, different def.
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

        int stepCost = GetTerrainStepCost(hex.Terrain);
        _scoutPanel.Show(encounterDef, hex.Terrain.ToString(), stepCost);
    }

    private void CommitCombat(Vector2I hexCoord, EncounterDefinition encounterDef, string terrainType, string guardianKey = "")
    {
        var router = EncounterRouter.Instance;
        if (router == null)
        { GD.PrintErr("ExpeditionManager: EncounterRouter missing."); return; }

        // S3 (Retrace): a scene swap makes "the last step" ambiguous — forget it.
        _hasLastMove = false;

        LogRun("combat_start",
               $"{encounterDef.Id} (tier {encounterDef.Tier}, {encounterDef.Enemies.Count} foes)" +
               (string.IsNullOrEmpty(guardianKey) ? "" : $" [guardian:{guardianKey}]"),
               at: hexCoord);

        // Save only the RESOURCE state — the world (and thus the map) is resident.
        router.SavedStepsRemaining = StepsRemaining;
        router.SavedCurrentHP = CurrentHP;
        router.SavedGoldEarned = GoldEarned;
        router.SavedSplinterEarned = SplinterEarned;
        router.SavedEncountersWon = EncountersWon;
        router.SavedPartyCoord = _party.CurrentCoord;
        router.SavedCombatHexCoord = hexCoord;
        router.HasPendingReturn = false;
        // Reset ambush attribution — OnPatrolCapturedPlayer re-marks it AFTER
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
                if (_grid.Hexes.TryGetValue(new Vector2I(nc, nr), out var nHex))
                    neighborTerrains[k] = nHex.Terrain.ToString();
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
        if (!_grid.Hexes.TryGetValue(coord, out var hex))
            return;

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
            // witnessed — the echo fires NOW, at the moment of compulsion.
            // A Cordial resolution at the table buries it in flight
            // (OnNegotiationReturned); anything else lets it land on the
            // Chancellor and the Commanders.
            string compulsionToast = null;
            string patrolKingdom = KingdomIdAt(coord);
            if (!string.IsNullOrEmpty(patrolKingdom))
                compulsionToast = CouncilEcho.EmitDeed(SaveManager.ActiveSave?.Cycle,
                    patrolKingdom, CouncilEcho.PatrolCompelled,
                    positive: false, isMajor: false);

            ShowInfo("The compulsion takes hold — the patrol will talk instead of fight." +
                     (compulsionToast != null ? $" {compulsionToast}" : ""));
            // Dossier: a compelled parley still counts as crossing paths.
            AnnounceDossierMet(archmageId);
            TriggerPatrolNegotiation(hex, coord);
            return;
        }

        _ambushPending = true;
        ShowInfo("A patrol has intercepted you!");
        string regionId = StagingTemplateRegion();
        string terrainType = hex.Terrain.ToString();
        // The patrol BELONGS to this archmage (passed by the signal) — its forces
        // are always the archmage's own, NO chance roll. Region pool only backstops
        // an archmage that has no authored skirmish group.
        var arch = ArchmageDefById(archmageId);
        if (PlayerSession.DebugMode)
            GD.Print($"[ArchmageEncounter] patrol archmageId='{archmageId}', " +
                     $"draw={(arch != null ? arch.Id : "(region pool)")}");

        _scaledDifficultyMult = DifficultyMultAt(coord);
        // On a warfront the besieging patrols hit at siege weight too. OFF a warfront
        // the tier now says WHO caught you: an archmage's patrol was hunting you
        // (Ambush — 3 enemies, Standard map, richer purse), while an unclaimed-wilds
        // band merely blundered into you (Skirmish — 2 enemies, Sparse). This is the
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
        // tears the ToastManager out of the tree — announcing after it threw
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
    // Combat return — rebuild the SAME window; no seed/fog replay
    // ════════════════════════════════════════════════════════════════════

    private void RestoreFromCombat(EncounterRouter router)
    {
        StepsRemaining = router.SavedStepsRemaining;
        // K1 clamp (2026-07-09): MaxHP was recomputed in _Ready from the LIVE
        // roster — a companion permadying in the combat we're returning from
        // shrinks the pool, and the saved HP must not exceed the new ceiling.
        CurrentHP = Mathf.Min(router.SavedCurrentHP, MaxHP);
        GoldEarned = router.SavedGoldEarned;
        SplinterEarned = router.SavedSplinterEarned;
        EncountersWon = router.SavedEncountersWon;

        // The window was rebuilt fresh in _Ready from World; discovery is already
        // correct (it lives in World). W1: _Ready already built the initial disc
        // around this saved coord (return-aware Build) — this recenter is a
        // cheap idempotent safety net (adds/frees 0 tiles when Build did its
        // job) that also guarantees the tile exists before party placement.
        var savedLocal = GridLocalOf(router.SavedPartyCoord);
        if (!HardWindowMode)
            RecenterWindow(savedLocal);
        _party.Initialize(_grid, _fog, savedLocal);
        _lastSupplyBand = SupplyBandAt(savedLocal);
        WriteVisibleToWorld();

        var resultHex = router.SavedCombatHexCoord;

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
                _toasts?.Push("The guardian falls — the way to the fragment is open.", QuestToastKind.Progress);
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
                _toasts?.Push($"{rDef?.DisplayName ?? "The archmage"} is overthrown — their shard answers you now.",
                              QuestToastKind.Progress);
            }

            GoldEarned += router.GoldReward;
            SplinterEarned += router.SplinterReward;
            EncountersWon++;
            LogRun("combat_end",
                   $"victory{(router.SavedCombatWasPatrolAmbush ? " (patrol ambush)" : "")}" +
                   $" — encounter #{EncountersWon}",
                   goldDelta: +router.GoldReward, splinterDelta: +router.SplinterReward,
                   at: resultHex);

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
                    _toasts?.Push("The stronghold falls — the siege breaks. Extract to secure the front.",
                                  QuestToastKind.Progress);
                }
            }

            if (_grid.Hexes.TryGetValue(resultHex, out var hex))
            { hex.POIConsumed = true; hex.RefreshVisuals(); }
            ConsumeWorldPoi(resultHex);
            GrantStagingPointAt(resultHex); // securing a seat/settlement via combat can grant staging
            ShowInfo($"Victory! +{router.GoldReward} gold, +{router.SplinterReward} Splinters.");
            EmitCombatDeed(router, resultHex);

            // Sentiment: winning combat in an archmage's region shifts sentiment
            // toward the player. Killing their OWN patrol is handled separately
            // in EmitCombatDeed (negative shift there). Here: region-archmage
            // gets a positive nudge — the player is clearing threats.
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
            // the next authored weakness hint (quest spec §4 — wiring pass).
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
                            $"Dossier — {(dDef != null ? dDef.DisplayName : dossierArch)}: “{hint}”",
                            QuestToastKind.Progress);
                        foreach (var qt in QuestNotifier.NotifyNew(dBefore, dSave))
                            _toasts?.Push(qt.Text, qt.Kind);
                    }
                }
            }
            ReleaseImprisonedAt(resultHex); // if this was a prison, free the captive

            // S3 (Deathsight, Necromancer): every won combat leaves a Remnant
            // for the rest of the expedition — Bone Scout / Speak with the
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
        }
        else
        {
            // K2 (§5b): a LOST combat downs the whole fielded party (defeat
            // requires allPlayersDead in CheckCombatEnd) — one roll each at the
            // combat hex's territory tier; boss encounters roll at 40%.
            _casualtyNote = CompanionInjurySystem.ApplyWipe(SaveManager.ActiveSave,
                TerritoryTierAt(resultHex),
                bossContext: router.CurrentTier == EncounterTier.Boss,
                "defeated in combat");
            LogRun("combat_end",
                   $"DEFEAT{(string.IsNullOrEmpty(_casualtyNote) ? "" : " — " + _casualtyNote)}",
                   at: resultHex);

            if (_grid.Hexes.TryGetValue(resultHex, out var hex))
            { hex.POIConsumed = true; hex.RefreshVisuals(); }
            ConsumeWorldPoi(resultHex);

            // RULED (2026-07-09): defeat ENDS the expedition. The old path
            // subtracted router.DamageTaken (which arrived as 0) and carried on
            // — a fully dead party "respawned" at full pool. A party that lost
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
            var hex = kvp.Value;
            if (hex.IsWater || hex.Terrain == OverworldHex.TerrainType.Mountain)
                continue;
            candidates.Add(kvp.Key);
        }
        if (candidates.Count == 0)
            return;

        var spawn = candidates[(int)(GD.Randi() % (uint)candidates.Count)];
        _roamer = new RoamerToken { Name = "Roamer" };
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
                    _grid.Hexes.TryGetValue(_party.CurrentCoord, out var ph))
            ? ph.Terrain : OverworldHex.TerrainType.Grassland;
        _narrativePanel.ShowEncounter(enc, hasFlag, save?.Cycle?.SelectedSchool, GoldEarned,
            save?.Cycle?.Campaign);
        _narrativePanel.OnCompleted = (choice) => OnNarrativeCompleted(enc, choice, terr);
        ShowInfo("A caravan crosses your path.");
    }

    private static NarrativeEncounterData BuildCaravanEncounter() => new NarrativeEncounterData
    {
        Id = "roaming_caravan",
        Title = "A Caravan on the Road",
        Body = "A string of laden mules and creaking carts crests the rise \u2014 a merchant column far " +
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
                ResultText = "They trade rumor for coin \u2014 a shortcut, and a warning about what waits on it.",
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
            if (!_grid.Hexes.TryGetValue(coord, out var ghex))
                return false;
            ShowInfo($"The heart of {z.Name} is guarded. Its warden stirs.");
            LaunchGuardianCombat(z.FragmentKey, ghex.Terrain);
            return true;
        }

        if (col == z.SanctumX && row == z.SanctumY && z.GuardianCleared && !z.ShardCollected)
        {
            CollectShard(z);
            return true;
        }

        return false;
    }

    /// <summary>P4: take the shard from a cleared sanctum — permanent
    /// fragment_&lt;key&gt;_collected, convert the vault centre to a staging point
    /// (the vault becomes a forward base), bump host-kingdom influence, notify.
    /// Idempotent. Staging is added inline: a vault centre carries no POI, so the
    /// POI-gated GrantStagingPointAtWorld does not apply — this parallels its
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
        LogRun("shard_collected", $"{z.Name} ({z.FragmentKey}) — vault becomes staging point");
        _toasts?.Push($"Shard recovered: {z.Name}.", QuestToastKind.Complete);
        ShowInfo($"You take the shard from {z.Name}. Its power is yours — and the vault " +
                 "is now a staging point.");
        foreach (var qt in QuestNotifier.NotifyNew(before, save))
            _toasts?.Push(qt.Text, qt.Kind);
        UpdateUI();
    }

    /// <summary>[DEBUG] V: teleport to the nearest UNFINISHED shard vault — its GATE
    /// while the guardian stands, else its SANCTUM — and trigger arrival, so P4's
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

    private void TriggerNarrativeEncounter(OverworldHex hex, Vector2I coord)
    {
        string terrainName = hex.Terrain.ToString();
        var completedIds = SaveManager.ActiveSave?.CompletedEvents;
        var encounter = NarrativeEncounterLoader.PickRandom(_encounterPool, terrainName, completedIds, SaveManager.ActiveSave);

        hex.POIConsumed = true;
        hex.RefreshVisuals();
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
        var shownEnc = EncounterAssembler.ForDisplay(encounter, hex.Terrain, StagingTemplateRegion());
        _narrativePanel.ShowEncounter(
            shownEnc,
            hasFlag,
            gateSave?.Cycle?.SelectedSchool,
            GoldEarned,
            gateSave?.Cycle?.Campaign);
        LogRun("narrative_start", encounter.Id, at: coord);
        var loreTerrain = hex.Terrain; // S4: the drop pool is terrain-flavored
        _narrativePanel.OnCompleted = (choice) => OnNarrativeCompleted(encounter, choice, loreTerrain);
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
            LogRun("guardian_bypassed", $"{key} — trial granted unopposed");
            ShowInfo("The guardian does not stir. You pass unopposed.");
            UpdateUI();
            return;
        }
        ShowInfo("The guardian rises to bar your way!");
        CommitCombat(_party.CurrentCoord, def, terrain.ToString(), key);
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
        // Capstone escalation (user ruling 2026-07-20 — scale both, shared knob):
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
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                UpdateUI();
                return true;

            case "coerce":
                campaign.SetDisposition(archmageId, ArchmageDisposition.Coerced);
                foreach (var qt in QuestEvents.Raise(QuestEvents.ArchmageCoerced, region, archmageId))
                    _toasts?.Push(qt.Text, qt.Kind);
                _toasts?.Push($"{def?.DisplayName ?? "The archmage"} yields to the accord — for now.",
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
            _grid.Hexes.TryGetValue(_party.CurrentCoord, out var rHex))
            terrain = rHex.Terrain.ToString();
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
                ? $"{arcStatus.CompanionName} \u2014 \"{arcStatus.ArcName}\" complete."
                : $"{arcStatus.CompanionName} \u2014 \"{arcStatus.ArcName}\" advances ({arcStatus.CurrentStage}/{arcStatus.TotalStages}).",
                QuestToastKind.Progress);
            SaveManager.MarkDirty();
        }

        // S4 (§11): lore POIs are the terrain-flavored acquisition path.
        // An authored SpellReward on the chosen option grants exactly that
        // spell; otherwise a bonus roll may teach an unknown learnable from
        // the tile's flavored pool. KnownSpellIds rides CycleState, so the
        // learn persists through any save — the S4 exit criterion.
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
              $"{OverworldSpellRegistry.Get(learnedId)?.Name} — preparable at the next launch."
            : $"Encounter resolved. +{spl} Arcane Splinters.";
        if (t2.Count > 0)
            msg += " You " + string.Join(", ", t2) + ".";

        LogRun("narrative_choice",
               encounter.Id
               + (learnedId != "" ? $"; learned {learnedId}" : "")
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
    /// negotiation. Same setup as a Negotiation POI, minus POI consumption —
    /// the patrol's hex owns no POI. The patrol itself disengages via the
    /// standard post-negotiation restore path.</summary>
    private void TriggerPatrolNegotiation(OverworldHex hex, Vector2I coord)
    {
        string kingdomId = StagingTemplateRegion();
        string terrain = hex.Terrain.ToString();
        var encounter = NegotiationEncounterLoader.PickForTerrain(terrain, kingdomId);
        if (encounter == null)
        { ShowInfo("The patrol shakes off the compulsion — nothing to say."); UpdateUI(); return; }

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
        GD.Print("[Spellcraft] Beguile takes effect — the table opens a band more favorable.");
    }

    private void TriggerNegotiationEncounter(OverworldHex hex, Vector2I coord)
    {
        hex.POIConsumed = true;
        hex.RefreshVisuals();
        ConsumeWorldPoi(coord);

        // S5 (True Names): honor the pinned pre-read when one exists —
        // the archetype the attunement showed is the counterpart you meet.
        var encounter = PinnedNegotiationFor(coord, hex);
        if (encounter == null)
        { ShowInfo("A potential contact slips away."); UpdateUI(); return; }

        NegotiationContext.Clear();
        NegotiationContext.EncounterId = encounter.Id;
        NegotiationContext.HexCoordKey = $"{coord.X},{coord.Y}";
        NegotiationContext.NpcArchetype = encounter.Archetype.ToString();
        // Kingdom of the tile we're standing on — drives court-standing
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
            LogRun("negotiation_end",
                   $"deal signed: {NegotiationContext.EncounterId}" +
                   $" (rep {(NegotiationContext.ReputationDelta >= 0 ? "+" : "")}{NegotiationContext.ReputationDelta})",
                   goldDelta: GoldEarned - negGoldBefore, at: hexCoord);
            var cycle = SaveManager.ActiveSave?.Cycle;
            string kingdom = NegotiationContext.OriginKingdomId;
            bool kingdomAligned = cycle != null &&
                                  !string.IsNullOrEmpty(kingdom) &&
                                  cycle.Kingdoms.ContainsKey(kingdom);
            if (kingdomAligned)
            {
                // Kingdom-aligned: the deal echoes to the court (C4). Routed
                // on OriginKingdomId (the tile's kingdom), NOT the authored
                // FactionId — encounter JSONs carry non-kingdom faction keys,
                // so keying on FactionId here was structurally dead (Session D).
                // FactionReputation no longer stores kingdom feeling; court
                // standing is the single source of truth.
                int rep = NegotiationContext.ReputationDelta;
                if (rep != 0)
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
            // S4 (§11): a deal closed in the Cordial zone can carry tuition —
            // the social route to spells. NegotiationState grants only on
            // Cordial (see GetSpellOutcome); here we just learn and say so.
            string taught = "";
            if (!string.IsNullOrEmpty(NegotiationContext.SpellGranted))
            {
                var grimD = SaveManager.ActiveSave?.Cycle?.Grimoire;
                if (grimD != null && SpellAcquisition.Learn(grimD, NegotiationContext.SpellGranted))
                    taught = $"  They teach you {OverworldSpellRegistry.Get(NegotiationContext.SpellGranted)?.Name}.";
            }
            // S5 (§7f/§6a): a compulsion table that CLOSES CORDIALLY buries
            // the compulsion echo before it lands — same gate as tuition
            // (DealAccepted ∧ Cordial). Walking away, strained deals, and
            // collapses all let the story reach the court.
            string buried = "";
            if (NegotiationContext.FromCompulsion && NegotiationContext.ResolvedCordial &&
                CouncilEcho.CancelDeed(SaveManager.ActiveSave?.Cycle?.Council,
                    NegotiationContext.OriginKingdomId, CouncilEcho.PatrolCompelled))
                buried = "  The patrol parts on good terms — that story dies here.";

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

            // Quest event shim (step 1 spec — raise finally wired 2026-07-23):
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
                   $"no deal: {NegotiationContext.EncounterId}", at: hexCoord);
            foreach (var qt in QuestEvents.Raise(QuestEvents.NegotiationWalkaway,
                     NegotiationContext.OriginKingdomId))
                _toasts?.Push(qt.Text, qt.Kind);
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
    /// built for — walking home is the cheap way out.</summary>
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

    /// <summary>W3 emergency extraction — the party abandons the field and
    /// straggles home. Costs: +1 lunation (CycleState.PendingStraggleLunations,
    /// advanced with the full world tick by StrategicView on return) and one
    /// §5b roll per companion at the tier-2 band (15% death, Sworn −10; the
    /// rest injured 1–2 lunations). AMENDS K2.5's "no death risk outside
    /// losing fights" — this is the price of extraction beyond the line.
    /// Spoils and discoveries ARE kept: the cost is time and bodies, not loot.</summary>
    private void EmergencyExtract()
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

        OverworldSpellEffects.Clear(); // S2: timed spell windows end with the expedition
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
        RunEventLog.End("emergency_extract",
            $"straggled home, +1 lunation.{casualties}",
            GoldEarned, SplinterEarned, EncountersWon, CurrentHP, StepsRemaining,
            goldBanked: true);
        ShowInfo($"Emergency extraction. The party straggles home — a lunation will pass. " +
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
        ExpeditionComplete = true;
        PlayerSession.IsOnExpedition = false;

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        OverworldSpellEffects.Clear(); // S2: timed spell windows end with the expedition
        _identifiedEncounters.Clear(); // S4: Identify pins end with it too
        _pinnedNegotiations.Clear();   // S5: True Names pre-reads likewise

        // K2.5 ruling: extraction infirmary check — who came home broken?
        // Stabilized (downed in a won fight) → 1–2 lunations; below 25% of
        // BaseHP → 1. Resets ExpeditionHP. No death risk on extraction.
        string extractCasualties = CompanionInjurySystem.ApplyExtractionCheck(SaveManager.ActiveSave);

        BankResources(extracted: true);
        RunEventLog.End("extracted",
            $"voluntary extraction at supply anchor.{(string.IsNullOrEmpty(extractCasualties) ? "" : " " + extractCasualties)}",
            GoldEarned, SplinterEarned, EncountersWon, CurrentHP, StepsRemaining,
            goldBanked: true);
        ShowInfo($"Extracted. Gold: {GoldEarned}, Splinters: {SplinterEarned}, Encounters: {EncountersWon}." +
                 $"{(string.IsNullOrEmpty(extractCasualties) ? "" : " " + extractCasualties)}");
        ShowReturnButton();
        EmitSignal(SignalName.ExpeditionEnded, true);
    }

    private void FailExpedition(string reason, bool injuriesAlreadyRolled = false)
    {
        if (ExpeditionComplete)
            return;
        ExpeditionComplete = true;
        PlayerSession.IsOnExpedition = false;

        // K2 (§5b): the pool hit 0 — an expedition wipe. One roll per fielded
        // companion at the territory tier under the party's feet. Skipped when
        // the combat-loss return already rolled this wipe (one roll per wipe).
        if (!injuriesAlreadyRolled)
            _casualtyNote = CompanionInjurySystem.ApplyWipe(SaveManager.ActiveSave,
                TerritoryTierAt(_party?.CurrentCoord ?? Vector2I.Zero),
                bossContext: false, reason);

        // K2.5: expedition over — the wipe rolls above are the injury
        // accounting on this path; carried HP just clears.
        CompanionInjurySystem.ResetExpeditionHP(SaveManager.ActiveSave);
        OverworldSpellEffects.Clear(); // S2: timed spell windows end with the expedition
        _identifiedEncounters.Clear(); // S4: Identify pins end with it too
        _pinnedNegotiations.Clear();   // S5: True Names pre-reads likewise

        if (EncounterRouter.Instance != null)
        {
            EncounterRouter.Instance.HasSavedSeed = false;
            EncounterRouter.Instance.HasPendingReturn = false;
        }

        // Failure still banks DISCOVERY (it's in World) but forfeits unbanked gold.
        BankResources(extracted: false);
        // The casualty note makes the human cost part of the banner — WHO was
        // hurt and for how long, not just that the run died (K2 UX).
        string casualties = string.IsNullOrEmpty(_casualtyNote) ? "" : $" {_casualtyNote}";
        RunEventLog.End("failed", $"{reason}{casualties}",
            GoldEarned, SplinterEarned, EncountersWon, CurrentHP, StepsRemaining,
            goldBanked: false);
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

        // Hover tooltip — follows the mouse, names the tile under it (fog-gated).
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

        _stepLabel = MakeHudLabel();
        vbox.AddChild(_stepLabel);
        _hpLabel = MakeHudLabel();
        vbox.AddChild(_hpLabel);
        // S2: the second scarcity, read beside the first (§12).
        _essenceLabel = MakeHudLabel();
        _essenceLabel.AddThemeColorOverride("font_color", UITheme.EssenceText);
        vbox.AddChild(_essenceLabel);
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

        // W3: emergency-extraction confirm. Free extraction happens only on a
        // supply anchor; anywhere else the party straggles home at real cost.
        _emergencyConfirm = new ConfirmationDialog
        {
            Title = "Emergency Extraction",
            DialogText = "You are away from any supply anchor. The party abandons\n" +
                         "the field and straggles home overland:\n\n" +
                         "  · One full lunation passes before you reach the campus.\n" +
                         "  · Every companion risks injury — or worse — on the road.\n\n" +
                         "Spoils and discoveries are kept. Extract anyway?",
            OkButtonText = "Extract",
        };
        _emergencyConfirm.Confirmed += EmergencyExtract;
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
        // (Modal panels — scout report, narrative — are caught by the
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
        _stepLabel.Text = (PlayerSession.DebugMode && PlayerSession.UnlimitedSteps)
            ? "Range: ∞ [DEBUG]"
            : $"Range: {StepsRemaining} / {OperatingRange}";
        _stepLabel.Modulate = StepsRemaining > 5 ? Colors.White : UITheme.OverworldLowResourceWarning;

        _hpLabel.Text = $"HP: {CurrentHP} / {MaxHP}";
        _hpLabel.Modulate = CurrentHP > MaxHP / 3 ? Colors.White : UITheme.OverworldLowResourceWarning;

        // S2: the Essence pool, beside the other scarcities (§12).
        var grimoire = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (_essenceLabel != null && grimoire != null)
        {
            _essenceLabel.Text = $"Essence: {grimoire.EssenceCurrent} / {grimoire.EssenceMax}";
            _essenceLabel.Modulate = grimoire.EssenceCurrent > 2
                ? Colors.White : UITheme.OverworldLowResourceWarning;
        }

        // W3: supply readout replaces the old fixed-window explored counter
        // (the loaded set now slides and grows — a ratio over it is noise).
        int supplyDist = SupplyDistanceAt(_party.CurrentCoord);
        int supplyBand = SupplyBandAt(_party.CurrentCoord);
        _windowLabel.Text = supplyBand == 0
            ? $"Supply: in range ({supplyDist}/{SupplyRange})"
            : $"Supply: {supplyDist - SupplyRange} beyond the line (−{supplyBand * LeashDrainPerBand} HP/step)";
        _windowLabel.Modulate = supplyBand == 0 ? Colors.White : UITheme.OverworldLowResourceWarning;

        if (_grid.Hexes.TryGetValue(_party.CurrentCoord, out var cur))
            _windowLabel.Text += $"  |  {cur.Terrain}";
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
    /// on. Used only for the REGION pool — archmage groups carry their own
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

    /// <summary>K2 (§5b): territory tier (1–3) at a window-local tile — the
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

    /// <summary>Map a stored grid-local coord through the window (identity — the
    /// window rebuild uses the same staging point, so local coords are stable
    /// even across slides: the local frame is a fixed translation of world axial).</summary>
    private Vector2I GridLocalOf(Vector2I savedLocal) => savedLocal;

    // ════════════════════════════════════════════════════════════════════
    // S2: spell façade — OverworldSpellManager dispatches effects into
    // these; world mutation stays HERE (the manager owns decisions, not
    // the world — overworld_spell_system §13).
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

    /// <summary>Chart a hex disc into the world (Unseen → Charted only — G2;
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
    /// shatters to Hills; water freezes/fords to Marsh — passable but boggy,
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
    /// window slides (its hex node may unload; the mark remains — that is
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
    /// undo the last committed movement step — position restored, charged step
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

    /// <summary>Waystation marker — a small square-on-post, named by world
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

    /// <summary>Remnant marker (Deathsight) — a pale sliver on a won-combat hex.</summary>
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
    /// tokens render) — Stasis Snare's legal targets.</summary>
    public List<Vector2I> VisiblePatrolCoords()
    {
        var result = new List<Vector2I>();
        if (_factionManager == null)
            return result;
        foreach (var c in _factionManager.GetPatrolPositions())
            if (_grid.Hexes.TryGetValue(c, out var h) &&
                h.Fog != OverworldHex.FogState.Hidden)
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
    /// restart, so a quit-and-reload mid-expedition forgets pins — the
    /// next scout report re-rolls.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, EncounterDefinition>
        _identifiedEncounters = new();

    /// <summary>Identify (Arcanist, §7b): roll the encounter for a visible
    /// combat/prison POI exactly as OpenScoutReport would, PIN it so the
    /// on-hex report later shows the same forces, and display it read-only
    /// through the ScoutReportPanel's intel mode. Returns the info-line
    /// result, or null (refused — no charge, G5).</summary>
    public string SpellIdentify(Vector2I local)
    {
        if (!_grid.Hexes.TryGetValue(local, out var hex) || hex.POIConsumed ||
            (hex.POI != OverworldHex.POIType.Combat && hex.POI != OverworldHex.POIType.Prison))
            return null;
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return null;

        string key = $"{col},{row}";
        if (!_identifiedEncounters.TryGetValue(key, out var encounterDef))
        {
            string terrainType = hex.Terrain.ToString();
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

        _scoutPanel.ShowIntel(encounterDef, hex.Terrain.ToString(),
            "Identified from afar — this composition is fixed; the scout report will match.");
        return $"the weave yields their number — {encounterDef.Enemies.Count} foe(s) revealed";
    }

    // ── S5 façade additions — the world watches magic (§6a / R15) ────────

    /// <summary>S5 (R15): deterministic HP hit per cast made FROM a tier-3
    /// corrupted tile. Flat and legible — the detail card warns pre-cast;
    /// no roll. Applies to scroll casts too: exposure is about standing in
    /// the corruption while channeling, not about Essence.</summary>
    [Export] public int Tier3CastExposureHP = 4;

    /// <summary>Apply the tier-3 casting exposure if the party stands on
    /// tier-3 ground. Returns the info-line note, or null when no exposure.
    /// Can end the expedition — callers must check ExpeditionComplete.</summary>
    public string SpellTier3Exposure()
    {
        if (CorruptionTierAt(_party.CurrentCoord) < 3)
            return null;
        CurrentHP -= Tier3CastExposureHP;
        if (PlayerSession.DebugMode && PlayerSession.GodModeHP)
            CurrentHP = Mathf.Max(1, CurrentHP);
        LogRun("cast_exposure", "cast from tier-3 corrupted ground",
               hpDelta: -Tier3CastExposureHP);
        if (CurrentHP <= 0)
        {
            CurrentHP = 0;
            FailExpedition("Consumed by corruption mid-casting.");
            return null;
        }
        UpdateUI();
        return $"the corrupted ground answers the working — the party sears for {Tier3CastExposureHP} HP";
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
    /// within `radius` hexes of the party — §6a's "near a settlement
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

    private NegotiationEncounterData PinnedNegotiationFor(Vector2I local, OverworldHex hex)
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
            hex.Terrain.ToString(), StagingTemplateRegion());
        if (data != null)
            _pinnedNegotiations[key] = data.Id;
        return data;
    }

    /// <summary>Hover extra for Negotiation POIs under the True Names
    /// attunement: name the counterpart's archetype before engagement —
    /// pre-loading the token-affinity read the negotiation rewards.</summary>
    private string NegotiationPreread(Vector2I local, OverworldHex hex)
    {
        if (_spells == null || !_spells.HasAttunement("true_names"))
            return "";
        if (hex.POI != OverworldHex.POIType.Negotiation || hex.POIConsumed)
            return "";
        var data = PinnedNegotiationFor(local, hex);
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
    /// flood presses next — loaded clean tiles adjacent to corrupted ground
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
    /// when the shard returns — the simulation LOD is implicit.</summary>
    private void RecenterWindow(Vector2I centerLocal)
    {
        if (!_window.TryLocalToWorld(centerLocal, out int col, out int row))
            return;
        var (added, removed) = _window.StreamTo(_grid, col, row);
        _windowCenterLocal = centerLocal;
        if (added > 0)
            StampCivicPois(); // S4.2: newly streamed tiles may hold settlements
        StampStronghold();    // re-stamp the warfront objective if it (re)entered the window
        if (PlayerSession.DebugMode && (added > 0 || removed > 0))
            GD.Print($"[Window] Slide → ({col},{row}): +{added}/−{removed} tiles, " +
                     $"{_grid.Hexes.Count} live.");
    }

    /// <summary>S4.2 (user request): settlements and seats had no expedition-
    /// map presence — the window streamer maps only encounter-scale POIs to
    /// hex markers, so cities were visible on the strategic view and invisible
    /// underfoot. Stamp POIType.Settlement/Seat onto loaded hexes after every
    /// build/slide (idempotent; never overwrites an encounter POI; marker
    /// visibility still rides the standard fog gate in RefreshVisuals).</summary>
    /// <summary>Warfront objective: stamp the besieging stronghold as a Combat
    /// landmark on its window hex and reveal it, so the party can march from the
    /// front and storm it. Re-called on recenter (streaming rebuilds hexes from
    /// world data, which has no stronghold). No-op once the siege is broken, so it
    /// doesn't respawn. Touches only the in-window hex — never the world table.</summary>
    private void StampStronghold()
    {
        if (!_isWarfront || _strongholdCol < 0 || _grid == null || _window == null)
            return;
        var cyc = SaveManager.ActiveSave?.Cycle;
        if (cyc != null && cyc.WarfrontStrongholdCleared)
            return; // already stormed — don't put it back

        var local = _window.LocalOf(_strongholdCol, _strongholdRow);
        if (!_grid.Hexes.TryGetValue(local, out var hex))
            return; // not in the loaded window yet — a later stream will catch it

        hex.POI = OverworldHex.POIType.Combat;
        hex.IsLandmark = true;
        hex.POIConsumed = false;
        hex.RefreshVisuals();
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
            if (!_grid.Hexes.TryGetValue(local, out var hex))
                continue;
            var want = poi.Kind == PoiKind.Seat
                ? OverworldHex.POIType.Seat
                : OverworldHex.POIType.Settlement;
            if (hex.POI == OverworldHex.POIType.None)
            {
                hex.POI = want;
                hex.RefreshVisuals();
            }
        }
    }

    /// <summary>Hex distance from a grid-local coord to the NEAREST supply
    /// anchor: this expedition's staging tile, or any Available staging point
    /// (settlements, secured outposts/seats — including ones secured this
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
        // a supply anchor while it lasts — the deep-push range strategy.
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

    /// <summary>True when the party stands ON a supply anchor tile — the
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
        // S3: a standing waystation is an anchor (free extraction included —
        // it is a 5-Essence Overt cast; tuning watch noted in the docs).
        var grimA = SaveManager.ActiveSave?.Cycle?.Grimoire;
        if (grimA != null && grimA.ActiveWaystations.Contains($"{col},{row}"))
            return true;
        return false;
    }

    private void RevealAllFog()
    {
        foreach (var hex in _grid.Hexes.Values)
        {
            hex.Fog = OverworldHex.FogState.Revealed;
            hex.RefreshVisuals();
        }
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
            return "Owed by the guild — repay it, don't call it in.";
        if (f.IsMajor)
            return "Major favors cannot be called in from the field yet.";
        if (!CouncilLedger.CallableTypes.Contains(f.Type))
            return $"{f.Type} favors have no field effect yet.";
        if (ExpeditionComplete)
            return "The expedition is over.";
        if (KingdomIdAt(_party.CurrentCoord) != f.KingdomId)
            return "Must be inside the creditor's territory.";
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

    /// <summary>The four C3 call-in effects. Returns (consumed, message);
    /// a no-op outcome refuses without consuming the favor.</summary>
    private (bool ok, string msg) ExecuteCallIn(Favor f)
    {
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
                int heal = MaxHP / 4;
                CurrentHP = Mathf.Min(CurrentHP + heal, MaxHP);
                return (true, $"The Steward's supply train reaches you. Recovered {heal} HP.");
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
        }
        return (false, "That favor has no field effect yet.");
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
        // identifies it uniquely and survives any mutation of WorldData.Pois —
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
    /// around the stamp so the unlock toasts fire. Idempotent — "wilds" and
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
        // Cross-cycle combat record (deed:combat_won) — powers proven-guild
        // companion unlocks (CompanionUnlocks) and future deed-count quests.
        SaveManager.ActiveSave?.Ledger?.RecordDeed("combat_won");

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.Council == null)
            return;

        // 1. Patrol slain — offense against whoever owns the soldiers.
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

        // 3. Settlement defended — a discovered settlement of this kingdom
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
    /// is now Charted — mid-expedition world writes don't otherwise reach the
    /// already-built window.</summary>
    private void RefreshWindowSilhouettes()
    {
        foreach (var kvp in _grid.Hexes)
        {
            var hex = kvp.Value;
            if (hex.Fog != OverworldHex.FogState.Hidden)
                continue;
            if (!_window.TryLocalToWorld(kvp.Key, out int col, out int row))
                continue;
            if (!_world.TryIndex(col, row, out int idx))
                continue;
            if (_world.Tiles[idx].Discovery == TileDiscovery.Charted)
            {
                hex.Fog = OverworldHex.FogState.Silhouette;
                hex.RefreshVisuals();
            }
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
            int contribution = c.BaseHP / 2;   // floor — int division, BaseHP ≥ 0
            int bonus = c.LoyaltyPoolBonus();
            total += contribution + bonus;
            readout.Append($" + {c.Name} {contribution + bonus} (⌊{c.BaseHP}/2⌋" +
                           $"{(bonus > 0 ? $" +{bonus} {c.GetLoyaltyTier()}" : "")})");
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

        // Q3 (§4b) readout — party traversal resistance at a glance, once at deploy.
        int cw = EquipmentLoadout.PartyCorruptionWard();
        int hw = EquipmentLoadout.PartyHazardWard();
        if (cw > 0 || hw > 0)
            GD.Print($"[PartyResist] CorruptionWard {cw}, HazardWard {hw} (+ Pathfinder per-terrain).");

        // W3 readout — the supply-line terms this expedition operates under.
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

        // ALWAYS claim the return path — the router is a persistent singleton that
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
