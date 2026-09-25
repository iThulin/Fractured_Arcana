using Godot;
using System.Collections.Generic;

// ============================================================
// StrategicView.cs
//
// Purpose:        The cheap whole-world renderer (Phase 1b). Paints
//                 one quad per WorldTile via a single MultiMesh:
//                 ~9,216 instances, one draw call, zero Area2D /
//                 Label / per-tile nodes. Per-tile color encodes
//                 discovery first (Unseen = void), then faction
//                 tint over terrain, then corruption wash.
//                 Discovered POIs draw as markers in a second
//                 MultiMesh. Camera frames the world with zoom +
//                 drag-pan. Recolor is live: MarkTileDiscovered /
//                 MarkPoiDiscovered recolor single instances so the
//                 map fills in as expeditions reveal it (Phase 1c).
// Layer:          UI (strategic view)
// Collaborators:  WorldData.cs (the data it paints),
//                 UITheme.cs (all colors, incl. FactionColor),
//                 WorldGenerator.cs (standalone self-generation),
//                 OverworldHex.TerrainType (shared terrain colors)
// See:            single_world_refactor_v2.docx §4.2, §4.3
//
// Standalone: with no world supplied, _Ready generates one from a
// fixed seed so the scene renders in complete isolation (open the
// scene, press F6). SetWorld(world) injects the real cycle world.
// ============================================================

/// <summary>Map lenses. Each colors the strategic map to answer a different
/// question. Political = faction control + terrain texture + corruption (the
/// combined overview); Terrain = raw region terrain; Corruption = a spread
/// heat map.</summary>
public enum StrategicLens
{
    Political,
    Terrain,
    Corruption,
    Reach,
}

public partial class StrategicView : Node2D
{
    [Export] public float TilePx = 10f;          // world-space size of one tile quad
    [Export] public int StandaloneSeed = 12345;  // used only when self-generating
    [Export] public string StandaloneSchool = "Elementalist";

    /// <summary>When true, _Ready generates a throwaway world for isolated testing.
    /// When false (the real strategic scene), it reads SaveManager.ActiveSave.Cycle.World
    /// and enables staging-point deploy.</summary>
    [Export] public bool Standalone = true;

    // For standalone testing: reveal the whole world so colors are visible
    // without running expeditions. Leave false to see true discovery (mostly void).
    [Export] public bool RevealAllForTesting = true;

    /// <summary>Operating range / window radius handed to the expedition on deploy.</summary>
    [Export] public int DeployWindowRadius = 12;

    /// <summary>Window radius for a FIELD PARTY expedition.
    ///
    /// <para>WAS 6, on a model where the party made small "dives" around an
    /// anchor it teleported to. Corrected 2026-09-22: the field party is the
    /// expedition now. It gets the same window the single token always had,
    /// because it does everything that token did. Kept as a separate export
    /// rather than folded into DeployWindowRadius so the two forces can still be
    /// tuned apart if play wants that.</para></summary>
    [Export] public int FieldDeployWindowRadius = 12;

    /// <summary>Which force the pending deploy sends. Reset to Castle every time
    /// the drawer opens, so a cancelled field send cannot leak into the next
    /// ordinary sortie.</summary>
    private ExpeditionRunKind _pendingRunKind = ExpeditionRunKind.Castle;

    // Deploy cost is one whole lunation (see Deploy()): the moon turns once per
    // expedition and every deploy begins on the new moon (The Veiled). The world
    // ticks exactly once per deploy, so LunationsPerCycle (CalendarState) is the
    // sole expedition-count pacing knob: ~LunationsPerCycle deploys per cycle.

    /// <summary>This scene. Doubles as the EncounterRouter return-override key for
    /// the Convergence fight, the way CampusScreen uses its own path. That is what
    /// tells ConsumeConvergenceReturn a returning combat was the finale's and not an
    /// expedition's.</summary>
    private const string StrategicScenePath = "res://Scenes/Overworld/StrategicScene.tscn";
    private const string CampusScenePath = "res://Scenes/Campus/CampusScene.tscn";

    private WorldData _world;
    private System.Collections.Generic.Dictionary<string, KingdomState> _kingdoms = new();
    private Node2D _labelLayer;
    private const float ArchmageNameZoomThreshold = 1.4f; // ruler line appears past this zoom
    private bool _debugReveal = false;   // debug full-map view (non-destructive)
    private StrategicLens _lens = StrategicLens.Terrain;  // active map lens (default: Terrain)
    private MultiMeshInstance2D _tileLayer;
    private MultiMeshInstance2D _poiLayer;
    private Node2D _shardZoneLayer;
    private MultiMeshInstance2D _settlementLayer;
    private Node2D _edgeLayer;
    private Node2D _borderLayer;
    private Camera2D _camera;

    // ── 3D strategic map (renderer swap) ─────────────────────────────────
    // The real scene renders the world via WorldAtlas3D (orthographic strategic
    // view) instead of the 2D MultiMesh layers. ONLY the render + tile picking
    // move to 3D; every controller flow (deploy, the lunation tick, the
    // Convergence finale, the _Ready lifecycle mutators, all dialogs and the HUD)
    // is unchanged. Standalone/debug keeps the 2D path. When _atlas3D is non-null
    // the 2D layers are never built, so their recolor paths short-circuit.
    private CanvasLayer _atlas3DLayer;
    private WorldAtlas3D _atlas3D;
    private Node _campusOverlay;   // Stage 3: campus hosted in-scene as an overlay (no scene swap)
    private HomeBuildingPanelHost _floatingPanel;   // a single building's panel floated over the LIVE city view
    // City-view chrome (2026-08-19 rework): the leave/annex/services buttons live in ONE
    // right-docked VBox on the strategic HUD together with the calendar readout and the
    // frontier news, so the global top bar, the buttons and the panels stack cleanly
    // without manual offset arithmetic. Buttons toggle Visible per city state; the whole
    // stack rebuilds with the HUD.
    private VBoxContainer _rightHudStack;
    // The "to the world map" button that lived in the verb stack is gone
    // (2026-09-24): navigation is _viewToggleBtn at the head of the FORCES
    // roster, and Escape. See OnViewTogglePressed.
    private Button _viewToggleBtn;
    private Button _sortieBtn;             // 2026-09-24: the castle sorties from where it stands
    private Button _annexButton;           // "annex a district" toggle (kept so we can reset it after a purchase)
    private Button _buildModeBtn;          // "build" toggle: arms bare-ground clicks to open the construct card
    private Button _marchModeBtn;          // Expedition v2: arms map clicks as a MOVE order
    private bool _marchMode;               // mirrors _marchModeBtn

    /// <summary>Which piece takes orders. Ruled 2026-09-22: the castle and the
    /// field party are independent pieces, each with its own marker and its own
    /// orders, so every world-map verb has to know which one it is talking to.
    ///
    /// <para>ONE selector driving ONE armed-move verb, rather than a March
    /// button and a separate Send Party button. The question "where should this
    /// go" is the same question for both pieces; what differs is what the answer
    /// costs, and that belongs in the refusal and the confirmation, not in two
    /// parallel controls that can be armed at once.</para></summary>
    /// <summary>Which force takes orders: "" for the castle, otherwise a
    /// FieldParty.Id.
    ///
    /// <para>WAS a bool, _partySelected, on the assumption that there would only
    /// ever be two pieces. MaxFieldParties is a campus upgrade and the turn loop
    /// has iterated the party list since the schema landed, so a boolean was
    /// always going to have to become this. Asked for by name on 2026-09-23:
    /// "select and give commands to any party or castle you control".</para></summary>
    private string _selectedPieceId = "";

    /// <summary>The roster panel: every force, where it is, what it can do.
    /// Rebuilt whenever anything moves, because a stale roster is worse than
    /// none: it invites an order for a piece that is no longer there.</summary>
    private PanelContainer _forcesPanel;
    private VBoxContainer _forcesRows;
    private Button _turnMoonBtn;
    private Button _partyDeployBtn;
    private Button _bankFurnaceBtn;    // 2026-09-23: park a frozen castle from the map
    private Button _stopWorkBtn;       // 2026-09-23: recall a working party
    private Button _postBtn;           // 2026-09-24: Field Party v1, the posting sheet
    private Button _collectBtn;        // 2026-09-24: fold a detachment back in, standing with it
    private Button _recallBtn;         // 2026-09-24: fold a detachment back in through a waystone
    private Button _interveneBtn;      // 2026-09-24: storm a depot or intervene at a front, from the tile
    private Button _audienceBtn;       // 2026-09-24: open a table with the court whose city the party stands in
    private Button _musterBtn;         // 2026-09-24: the Forces screen, where everyone is

    /// <summary>True when the castle is the piece taking orders.</summary>
    private bool CastleSelected => string.IsNullOrEmpty(_selectedPieceId);

    /// <summary>The selected field party, or null when the castle has the
    /// orders or the named party has gone.</summary>
    private FieldParty SelectedParty()
    {
        if (CastleSelected)
        {
            return null;
        }
        var parties = SaveManager.ActiveSave?.Cycle?.FieldParties;
        if (parties == null)
        {
            return null;
        }
        foreach (var p in parties)
        {
            if (p != null && p.Id == _selectedPieceId)
            {
                return p;
            }
        }
        return null;
    }
    private bool _buildMode;               // mirrors _buildModeBtn; gates OnHomeGroundPicked
    private Button _cityServicesBtn;       // "City Services" reopen button, NPC city view only
    private Label _hintLabel;              // bottom context hint; text swaps per view mode
    private CanvasLayer _helpLayer;        // the "first steps" orientation card
    private Button _helpBtn;               // reopens the help card any time
    private static bool _helpAutoShown;    // once per app session: auto-open on first home-city entry
    private CityServicesHost _cityServices;   // Phase 3: a visited enemy capital's services menu (auto-opened)
    private NarrativeEncounterPanel _cityNarrativePanel;  // Phase 3 explore: hosts a district EVENT over the city view
    private CanvasLayer _cityExploreLayer;    // Phase 3 explore: hosts the narrative panel + toasts above the atlas
    private ToastManager _cityExploreToasts;  // Phase 3 explore: stub messages for Fight/Story districts
    private WorldSettlement _reenterNpcCity;  // Phase 3 fights: land back inside this city after a combat round-trip
    private string _reenterToast = "";        // outcome toast shown once the city view is back up
    private QuestToastKind _reenterToastKind = QuestToastKind.Progress;
    private System.Collections.Generic.List<string> _reenterContractToasts;  // contract completions from the fight
    private bool _suppressServicesOnce;       // skip the services auto-open for one city entry (fight return)
    private const int DistrictAnnexCost = 250;   // placeholder gold cost to annex a district; tune in playtest
    [Export] public bool Use3DStrategicMap = true;

    // Camera control
    private bool _dragging;
    private Vector2 _dragLast;
    private float _zoom = 1f;
    private const float ZoomMin = 0.25f, ZoomMax = 4f, ZoomStep = 1.15f;

    // Index bookkeeping for live recolor.
    private readonly Dictionary<int, int> _poiInstanceOfPoi = new(); // poiIndex → poi MultiMesh instance

    // ── Standalone continent-style debug selector ────────────────────────
    private ContinentStyle? _standaloneStyle = null;   // null = seed-rolled
    private int _standaloneSeed;
    private CanvasLayer _debugControls;
    private Label _debugInfoLabel;

    public override void _Ready()
    {
        // Expedition v2: the run is over. ExpeditionRunKind is a handoff static
        // that survives the scene change, and CompanionRoster.RunRosterIds reads
        // it to decide whose names answer "who is on the expedition". Leaving it
        // on Field here would make the campus and this map show the field party
        // as the active party for the rest of the session. Field is an
        // expedition-scene state and nothing else.
        PlayerSession.ExpeditionRunKind = ExpeditionRunKind.Castle;
        PlayerSession.ExpeditionFieldPartyId = "";
        // An audience is a scene argument for the NEXT field deploy only. The
        // strategic map coming up means no deploy consumed it: drop it.
        PlayerSession.AudienceEncounterId = "";
        PlayerSession.AudienceKingdomId = "";

        if (_world == null)
        {
            if (Standalone)
            {
                // Isolated testing: generate a throwaway world so the scene renders alone.
                _standaloneSeed = StandaloneSeed;
                GenerateStandaloneWorld();
            }
            else
            {
                // THE HUB (2026-08-19): this scene is the game's main screen, so its
                // boot must be self-sufficient. The jobs CampusScreen._Ready used to
                // do on the way in happen here. Cards are already primed by the
                // GameBootstrap autoload; the save is autoloaded, seeded, and its
                // cycle world generated on demand. A cold boot with NO save routes to
                // the campus scene, which is now purely the slot picker / founding room.
                if (SaveManager.ActiveSave == null)
                {
                    SaveManager.AutoLoadLast();
                    if (SaveManager.ActiveSave != null)
                        PlayerSession.StartInCityOnOpen = true;   // cold boot lands in the city
                }
                if (SaveManager.ActiveSave == null)
                {
                    CallDeferred(nameof(RouteToCampusFallback));
                    return;
                }
                CycleInitializer.EnsureSaveSeeded();
                CycleInitializer.EnsureCycleWorld();

                // Real strategic scene: read the resident cycle world.
                var cycle = SaveManager.ActiveSave?.Cycle;
                if (cycle == null || cycle.World == null || cycle.World.Tiles.Length == 0)
                {
                    GD.PrintErr("StrategicView: no active cycle/world after init. Routing to campus.");
                    CallDeferred(nameof(RouteToCampusFallback));
                    return;
                }
                _world = cycle.World;
                _kingdoms = cycle.Kingdoms;
                _debugReveal = PlayerSession.DebugMode && PlayerSession.DebugRevealStrategicMap;

                // W3: pay the emergency-extraction debt BEFORE anything renders.
                // The straggle lunations advance the calendar and tick the
                // world, and the HUD/markers must show the post-tick state.
                ProcessPendingStraggle(cycle);

                // A returned warfront-intervention expedition applies its outcome
                // to the front here, before anything renders, so markers, control
                // colours, and the frontier report reflect the post-intervention
                // state the moment the map comes up.
                ResolveReturnedWarfrontIntervention(cycle);

                // A returning Convergence combat resolves the finale before
                // anything renders, so the outcome beat lands on the map the
                // player left rather than after a frame of normal play.
                ConsumeConvergenceReturn(cycle);

                // A returning district FIGHT (Phase 3 explore) resolves here the
                // same way: bank rewards, clear the district on victory, and queue
                // the re-entry so BuildAtlas3D lands back inside the fought city.
                ConsumeDistrictFightReturn(cycle);

                // Mid-finale reload: once the Anchorhold is open, the Convergence
                // is the only thing left in this timeline (spec §2). Route back to
                // the gate rather than letting the player wander the map.
                if (cycle.Convergence != null && cycle.Convergence.InProgress)
                    CallDeferred(nameof(ShowConjunction));

                // Supply caches: idempotent seed so pre-feature saves (and fresh
                // worlds) get their per-kingdom caches before markers render.
                SupplyCacheSystem.EnsureSeeded(cycle);

                // Expedition v2: idempotent, same role as the cache seed above.
                // BackfillPostings reconstructs the castle crew on a save written
                // before the force split (ActivePartyCompanionIds WAS the crew);
                // it no-ops once any posting exists. BeginLunation rolls the
                // one-move-plus-K-dives budget, and runs AFTER ProcessPendingStraggle
                // so a straggled lunation is already on the calendar.
                ExpeditionAnchors.EnsureCastleSited(cycle);
                ExpeditionAnchors.BackfillPostings(cycle);
                ExpeditionAnchors.ReconcilePostings(cycle);
                // After the castle: the party starts where the fortress is, so
                // it needs a sited castle to copy. Without this the party sits at
                // (-1,-1), PartyIsAt is false everywhere, and the drawer offers
                // travel to the tile they are already standing on.
                ExpeditionAnchors.EnsureFieldPartySited(cycle);
                // Waystones that spent their last charge on the dive we just
                // came back from. Closed HERE, on the way in, rather than at the
                // moment the charge was spent: see PruneSpentWaypoints.
                ExpeditionAnchors.PruneSpentWaypoints(cycle);
                // Sortie slots marked Active with nothing resumable in them
                // (saves from the slot-ordering bug). Cleared on the way in so
                // the roster never says "in the field, 0/0 fuel" about a force
                // standing in the dock.
                ExpeditionAnchors.PruneDeadSorties(cycle);
                cycle.ExpeditionTurn ??= new ExpeditionTurnState();
                cycle.ExpeditionTurn.BeginLunation(cycle.Calendar?.CurrentLunation ?? 1);

                // Expedition v2: a castle defence resolves BEFORE anything else
                // reads the camp, then any still-owed assault asks to be fought.
                // Order matters: resolving first means the offer below never
                // re-asks about a fight that just happened.
                ConsumeCastleDefenseReturn(cycle);
                if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
                    CallDeferred(nameof(OfferPendingCastleDefense));
            }
        }

        // The 2D Camera2D is only for the 2D render path. In 3D-real mode we skip
        // it. WorldAtlas3D owns its own camera, and _UnhandledInput/FrameCamera
        // both early-return on a null _camera, so all 2D pan/zoom auto-disables.
        if (Standalone || !Use3DStrategicMap)
            BuildCamera();
        CallDeferred(nameof(BuildRender));
    }

    /// <summary>W3 (claude/expedition_window_sliding_v1 §2.3): an emergency
    /// extraction sends the party home overland. CycleState.PendingStraggleLunations
    /// holds the debt. Advance the calendar one full lunation per owed unit,
    /// running the SAME per-lunation world tick a deploy-crossed boundary runs
    /// (council → corruption → infirmary), then save. If the lost time tips the
    /// cycle into the Grand Conjunction, the conjunction beat plays on top of
    /// the rendered map exactly as it would from a deploy.</summary>
    private void ProcessPendingStraggle(CycleState cycle)
    {
        int owed = cycle.PendingStraggleLunations;
        if (owed <= 0)
            return;
        cycle.PendingStraggleLunations = 0;

        for (int i = 0; i < owed; i++)
        {
            if (!cycle.Calendar.AdvanceLunation())
                break; // conjunction already reached, no further time to spend
            GD.Print($"[Calendar] The party straggles home. A lunation passes " +
                     $"(Lunation {cycle.Calendar.CurrentLunation} · {cycle.Calendar.CurrentMoonName}).");
            RunLunationTick(cycle);
        }

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();

        if (cycle.Calendar.ConjunctionReached)
        {
            GD.Print("[Calendar] The Grand Conjunction has come. The cycle ends.");
            CallDeferred(nameof(ShowConjunction));
        }
    }

    /// <summary>If the player just returned from a warfront intervention, apply the
    /// expedition's outcome to that front. Success = the party extracted alive (held
    /// or took the field); defeat swings the bar against the chosen side. Consumes
    /// the pending marker on the cycle and the RunResultData scratchpad.</summary>
    private void ResolveReturnedWarfrontIntervention(CycleState cycle)
    {
        if (cycle == null || string.IsNullOrEmpty(cycle.PendingWarfrontId))
            return;
        if (!RunResultData.HasResults)
        {
            // No expedition result to read (map reopened without a sortie). Leave the
            // pending marker so the intervention resolves on the real return.
            return;
        }

        // Success = broke the besieging stronghold AND extracted alive. Fleeing
        // without breaking the siege, or dying at the front, is a failed intervention.
        bool success = RunResultData.ReachedObjective && cycle.WarfrontStrongholdCleared;
        KingdomTickSimulation.ApplyIntervention(
            cycle, cycle.PendingWarfrontId, cycle.PendingWarfrontSide, success, FactionDisplay);

        cycle.PendingWarfrontId = "";
        cycle.WarfrontStrongholdCleared = false;
        RunResultData.Clear();
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
    }

    /// <summary>Deferred no-save / no-cycle escape hatch from _Ready: the campus scene
    /// is the slot picker and founding room, so a boot with nothing to show lands there.
    /// Deferred because changing scenes inside _Ready tears the tree mid-build.</summary>
    private void RouteToCampusFallback() => GetTree().ChangeSceneToFile(CampusScenePath);

    /// <summary>Inject the real cycle world (campus integration path).</summary>
    public void SetWorld(WorldData world, System.Collections.Generic.Dictionary<string, KingdomState> kingdoms = null)
    {
        _world = world;
        _kingdoms = kingdoms ?? new System.Collections.Generic.Dictionary<string, KingdomState>();
        if (IsInsideTree())
            CallDeferred(nameof(BuildRender));
    }

    // ── Render construction ──────────────────────────────────────────────
    private void BuildRender()
    {
        if (_world == null)
            return;

        // 3D-real path: render via WorldAtlas3D and route its tile picks into the
        // existing staging/supply dialogs. The HUD (calendar clock, siege news,
        // lens bar, hint) is a renderer-agnostic CanvasLayer and carries over as-is.
        // Every 2D map/marker layer is skipped. The "2D system" retires here.
        if (Use3DStrategicMap && !Standalone)
        {
            BuildAtlas3D();
            BuildHud();
            return;
        }

        BuildTileLayer();
        BuildSettlementLayer();
        BuildBorderLayer();
        BuildEdgeLayer();
        BuildPoiLayer();
        BuildShardZoneMarkers();
        if (!Standalone)
        {
            BuildStagingMarkers();
            BuildSupplyMarkers();
            BuildWarfrontMarkers();
            BuildHud();
        }
        if (Standalone)
            BuildDebugControls();
        FrameCamera();
        BuildLabelLayer();   // last: needs the framed _zoom for correct counter-scale
    }

    // ── 3D strategic map host + pick routing ─────────────────────────────
    /// <summary>Host WorldAtlas3D full-screen (in a CanvasLayer below the HUD) and
    /// render the resident world. Its `TilePicked` drives the same deploy/supply
    /// flows the 2D Area2D markers used to. WorldAtlas3D already draws staging
    /// beacons, POIs, shard zones, settlements and the Convergence marker, so the
    /// visual carries over; warfront markers are a later pass.</summary>
    private void BuildAtlas3D()
    {
        _atlas3DLayer?.QueueFree();
        _atlas3DLayer = new CanvasLayer { Name = "Atlas3DLayer", Layer = 0 };
        AddChild(_atlas3DLayer);

        var container = new SubViewportContainer { Stretch = true, Name = "Atlas3DView" };
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _atlas3DLayer.AddChild(container);

        var vp = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X };
        container.AddChild(vp);

        _atlas3D = new WorldAtlas3D();
        _atlas3D.TilePicked += OnAtlas3DTilePicked;
        // True geometry merge (Phase 2): the campus grounds render as a model on the home
        // tile; when zoomed in, clicking a building on it opens that building's campus panel
        // in place, without the full-screen scene swap.
        _atlas3D.HomeBuildingPicked += OnHomeBuildingPicked;
        _atlas3D.HomeLandmarkPicked += OnHomeLandmarkPicked;
        _atlas3D.HomeDistrictPicked += OnHomeDistrictPicked;
        _atlas3D.HomeGroundPicked += OnHomeGroundPicked;
        _atlas3D.CityModeChanged += OnCityModeChanged;
        _atlas3D.DistrictContentTriggered += OnDistrictContentTriggered;
        _atlas3D.DistrictScouted += OnDistrictScouted;
        vp.AddChild(_atlas3D);            // _Ready builds the camera (no world yet)
        // Discovery gates the strategic map exactly as the 2D view did: normal play
        // shows only charted/explored ground (unseen = void), debug reveals all.
        // WorldAtlas3D DEFAULTS to revealed (it's a comparison prototype), so set
        // this explicitly, and before SetWorld, so the first render is already right.
        _atlas3D.SetRevealAll(_debugReveal);
        _atlas3D.SetWorld(_world, _kingdoms);   // now render the resident world
        _atlas3D.SetLens(_lens);

        // Warfronts are cycle state (not WorldData), so hand the active front tiles to
        // the map for its red conflict beacons. Picks on them route back below.
        var warTiles = new System.Collections.Generic.List<Vector2I>();
        var atlasCycle = SaveManager.ActiveSave?.Cycle;
        if (atlasCycle?.Warfronts != null)
            foreach (var wf in atlasCycle.Warfronts)
                if (!wf.Closed && wf.HasFocus)
                    warTiles.Add(new Vector2I(wf.FocusCol, wf.FocusRow));
        _atlas3D.SetWarfronts(warTiles);

        // Expedition v2: both pieces on the board from the first frame. Without
        // this the party has coordinates in the save that nothing draws, which
        // is the state the whole 2026-09-22 ruling exists to end.
        RefreshPieceChrome();

        _atlas3D.AcceptInput = true;      // full-screen map, always live

        // Phase 3 explore: a district fight round-trip lands back INSIDE the city it
        // left, with its outcome toast. Checked before the hub landing so the fight
        // return wins over StartInCityOnOpen. Services are suppressed for this one
        // entry (the player left a fight, not a shop queue); the City Services
        // button still reopens them.
        if (_reenterNpcCity != null)
        {
            var backCity = _reenterNpcCity;
            _reenterNpcCity = null;
            PlayerSession.StartInCityOnOpen = false;
            PlayerSession.ZoomFromHomeOnOpen = false;
            _suppressServicesOnce = true;
            _atlas3D.SnapToTileClose(backCity.CenterX, backCity.CenterY);
            _atlas3D.EnterCityView(backCity);
            if (!string.IsNullOrEmpty(_reenterToast))
            {
                EnsureCityExploreToasts();
                _cityExploreToasts?.Push(_reenterToast, _reenterToastKind);
                _reenterToast = "";
            }
            PushContractToasts(_reenterContractToasts);
            _reenterContractToasts = null;
        }
        // The hub landing (2026-08-19): open IN CITY VIEW, the game's main screen.
        // One-shot; set by cold boot, guild founding, and utility-screen returns.
        // Checked before ZoomFromHomeOnOpen so the city landing wins if both are set.
        else if (PlayerSession.StartInCityOnOpen)
        {
            PlayerSession.StartInCityOnOpen = false;
            PlayerSession.ZoomFromHomeOnOpen = false;
            if (_world.InBounds(_world.HomeX, _world.HomeY) && _atlas3D.HasCityGrounds)
            {
                _atlas3D.SnapToTileClose(_world.HomeX, _world.HomeY);
                _atlas3D.EnterCityMode();
            }
        }
        // Stage 2 (Phase 2): arriving from the campus, start framed on the home city
        // at closest zoom and swoop OUT to the overview: the "ascend from your city
        // into the world". One-shot flag, cleared on use; only the campus→world
        // transition sets it, so expedition returns etc. are unaffected.
        else if (PlayerSession.ZoomFromHomeOnOpen)
        {
            PlayerSession.ZoomFromHomeOnOpen = false;
            if (_world.InBounds(_world.HomeX, _world.HomeY))
            {
                _atlas3D.SnapToTileClose(_world.HomeX, _world.HomeY);
                _atlas3D.FlyToOverview();
            }
        }
    }

    /// <summary>A 3D map click resolved to (col,row): route it to the same handler
    /// the 2D marker used. Staging beacon → deploy; supply cache → cache dialog.
    /// (Warfront routing lands in a later pass.) A click on ordinary ground is a
    /// no-op; WorldAtlas3D still updates its own inspect readout.</summary>
    /// <summary>How many hexes from a staging point a click may land and still deploy
    /// there. A staging hex is only a few pixels at whole-world zoom, so demanding an
    /// exact hit is unreasonable: snap to the nearest staging point within this radius.</summary>
    private const int StagingClickTolerance = 3;

    /// <summary>Arm or disarm march mode. Refuses to arm when the castle cannot
    /// march at all, and says why, rather than letting the player arm a mode that
    /// will reject every tile they click.</summary>
    // ── The forces roster (2026-09-23) ───────────────────────────────────
    //    Asked for by name: "a selector on this screen that allow you to select
    //    and give commands to any party or castle you control in the world".
    //
    //    The piece selector this replaces was a two-state button in the
    //    RIGHT-docked chrome stack, which the deploy drawer covers completely
    //    the moment it opens. So the control existed and could not be seen at
    //    the exact moment the player was thinking about where to send a force.
    //    It also could not name a second party, and MaxFieldParties is a campus
    //    upgrade.
    //
    //    LEFT side, under the lens row, because that is the one band of chrome
    //    nothing else docks into and the drawer cannot reach.

    /// <summary>Build the roster shell once. Rows are rebuilt on every refresh;
    /// the panel is not, so the player does not watch it flicker.</summary>
    private void BuildForcesPanel()
    {
        // EXPLICIT rect, not offsets-plus-minimum-size.
        //
        // The lens panel above gets away with two offsets and lets its content
        // push the other two, and I copied that. It did not render. Rather than
        // keep reasoning about when Godot clamps a top-level Control to its
        // combined minimum size, the panel now states its own rectangle: a
        // control that declares where it is cannot be zero-sized by a layout
        // rule I have mispredicted twice.
        const int ForcesWidth = 268;
        const int ForcesTop = 58;
        // 2026-09-24: was 300, which fits the castle and one party. With a
        // second party (Grand Hall III) and detachments under each, the roster
        // needs the room, and nothing else docks on the left below it.
        const int ForcesHeight = 440;
        _forcesPanel = new PanelContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 16,
            OffsetRight = 16 + ForcesWidth,
            OffsetTop = ForcesTop + HudManager.BarHeight,           // under the lens row
            OffsetBottom = ForcesTop + ForcesHeight + HudManager.BarHeight,
            CustomMinimumSize = new Vector2(ForcesWidth, 0),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _forcesPanel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.CampusTitleBarBorder));
        _hud.AddChild(_forcesPanel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _forcesPanel.AddChild(margin);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        margin.AddChild(col);

        // Navigation lives HERE, at the head of the roster, not in the verb
        // stack (2026-09-24). "Where am I looking" and "who do I command" are
        // the two things the player needs in every mode, and the city/world
        // toggle had been one of fourteen same-sized buttons on the right.
        _viewToggleBtn = new Button
        {
            Text = "\u25C8  To the world map",
            CustomMinimumSize = new Vector2(248, 34),
        };
        _viewToggleBtn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        UITheme.ApplyButtonStyle(_viewToggleBtn, isPrimary: true);
        _viewToggleBtn.TooltipText = "Escape does the same.";
        _viewToggleBtn.Pressed += OnViewTogglePressed;
        col.AddChild(_viewToggleBtn);

        _forcesRows = new VBoxContainer();
        _forcesRows.AddThemeConstantOverride("separation", 4);
        col.AddChild(_forcesRows);

        // Docked to the ROSTER, not the chrome stack. The question "have I given
        // everyone their orders" and the button that ends the lunation are the
        // same thought, and putting them on opposite sides of the screen is how
        // a player turns the moon with a force still standing idle.
        _turnMoonBtn = new Button
        {
            Text = "\u263D  Pass time",
            CustomMinimumSize = new Vector2(248, 34),
        };
        _turnMoonBtn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        UITheme.ApplyButtonStyle(_turnMoonBtn, isPrimary: false);
        _turnMoonBtn.TooltipText =
            "Let the days pass until the next thing that needs you: a party arriving, work "
          + "finishing, a force coming free, or the new moon, when the world moves. "
          + "Stops at every one of them.";
        _turnMoonBtn.Pressed += OnPassTimePressed;
        col.AddChild(_turnMoonBtn);

        // Fill itself. BuildLensButtons runs LATE in BuildHud, after the
        // RefreshPieceChrome that would otherwise have populated this, so the
        // roster would open empty and only appear once something else moved.
        //
        // That is the same ordering bug fixed a day earlier for the chrome
        // buttons, reintroduced by putting this builder in a method that runs
        // later. The band-aid is another refresh call at the new end; the fix is
        // a builder that does not depend on when it is called.
        RebuildForcesPanel();
    }

    /// <summary>Redraw every force. One row per piece: what it is, where it is,
    /// and what it is doing, because "can I give this an order right now" is the
    /// question the panel exists to answer and a name alone does not answer
    /// it.</summary>
    private void RebuildForcesPanel()
    {
        if (_forcesRows == null)
        {
            return;
        }

        foreach (var child in _forcesRows.GetChildren())
        {
            _forcesRows.RemoveChild(child);
            child.QueueFree();
        }

        // Hidden in city view: these are world-map verbs and the city owns its
        // own clicks. Visibility only; the rows are built either way.
        //
        // The early return that used to sit here is what the probe caught: the
        // panel was built during city mode, returned before adding a single row,
        // and had nothing to show even once it was made visible again.
        // Visible in every mode since 2026-09-24: it carries the city/world
        // toggle, and the roster is worth seeing from the city too. The rows'
        // verbs still refuse in city view through RefreshPieceChrome.
        if (_forcesPanel != null)
        {
            _forcesPanel.Visible = true;
        }

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            return;
        }

        var title = new Label { Text = "FORCES" };
        title.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 4);
        title.AddThemeColorOverride("font_color", UITheme.TextDim);
        _forcesRows.AddChild(title);

        AddForceRow("\u25A0  Castle", CastleStatusLine(cycle), UITheme.ArcaneBlue,
                    "", CastleSelected);

        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null || FieldPostings.IsDetachment(p))
                {
                    continue;
                }
                AddForceRow($"\u25B2  {p.Name}", PartyStatusLine(cycle, p), UITheme.Gold,
                            p.Id, _selectedPieceId == p.Id);
                // Detachments sit under their parent, indented, so the roster
                // reads as "this party, and the pieces of it left behind".
                foreach (var det in FieldPostings.DetachmentsOf(cycle, p))
                {
                    AddForceRow($"      \u25B8  {det.Name}", PartyStatusLine(cycle, det), UITheme.Gold,
                                det.Id, _selectedPieceId == det.Id);
                }
            }
        }
    }

    /// <summary>One selectable force. The accent bar down the left is the
    /// selection tell, matching the ring the piece wears on the map, so the
    /// panel and the board agree without the player having to check.</summary>
    private void AddForceRow(string name, string status, Color accent,
                             string pieceId, bool selected)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(248, 40),
            ToggleMode = false,
            ButtonPressed = false,
        };
        var style = UITheme.MakePanelStyle(
            selected ? UITheme.BgCard : UITheme.BgBase,
            selected ? accent : UITheme.NeutralDim);
        style.BorderWidthLeft = selected ? 4 : 1;
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeStyleboxOverride("hover", style);
        btn.AddThemeStyleboxOverride("pressed", style);
        btn.Pressed += () => SelectPiece(pieceId);
        _forcesRows.AddChild(btn);

        // The label rides INSIDE the button rather than being its Text, so the
        // name and the status line can carry different sizes and colours. A
        // Button's own Text is a single run and cannot.
        var box = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 12,
            OffsetTop = 3,
            OffsetRight = -6,
            OffsetBottom = -3,
        };
        box.AddThemeConstantOverride("separation", 0);
        btn.AddChild(box);

        var nameLbl = new Label { Text = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        nameLbl.AddThemeColorOverride("font_color", selected ? accent : UITheme.TextPrimary);
        box.AddChild(nameLbl);

        var statusLbl = new Label { Text = status, MouseFilter = Control.MouseFilterEnum.Ignore };
        statusLbl.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        statusLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(statusLbl);
    }

    /// <summary>What the castle is doing, in one line. Leads with the thing that
    /// BLOCKS an order when there is one, because that is what the player is
    /// about to run into.</summary>
    private string CastleStatusLine(CycleState cycle)
    {
        string at = cycle.World != null && cycle.World.InBounds(cycle.CastleX, cycle.CastleY)
            ? $"({cycle.CastleX},{cycle.CastleY})"
            : "nowhere yet";

        if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            return $"{at}  ·  under attack";
        }
        if (cycle.CastleRepairLunations > 0)
        {
            return $"{at}  ·  resupply {cycle.CastleRepairLunations} lunation(s)";
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            return $"{at}  ·  in the field, {cycle.CastleSortie.StepsRemaining}/{cycle.CastleSortie.MaxFuel} fuel";
        }
        if (WorldClock.CastleBusy(cycle))
        {
            return $"{at}  ·  returning, {cycle.CastleBusyUntilDay - WorldClock.Now(cycle)} day(s)";
        }
        if (!cycle.CastleParked)
        {
            return $"{at}  ·  out on a sortie";
        }
        // No standing order. Named rather than left blank, because "nothing is
        // assigned here" is the thing the player needs to notice before they
        // spend a lunation.
        return $"{at}  ·  fuel {cycle.CastleFuel}/{cycle.CastleMaxFuel}  ·  awaiting orders";
    }

    /// <summary>What a party is doing, in one line.</summary>
    private string PartyStatusLine(CycleState cycle, FieldParty p)
    {
        if (p.X < 0 || p.Y < 0)
        {
            return "not in the field";
        }
        string at = $"({p.X},{p.Y})";

        if (p.Sortie != null && p.Sortie.IsLive())
        {
            return $"({p.Sortie.X},{p.Sortie.Y})  ·  in the field, {p.Sortie.StepsRemaining}/{p.Sortie.MaxFuel} rations";
        }
        if (p.State == FieldPartyState.Travelling)
        {
            return $"{at}  \u2192 ({p.DestX},{p.DestY})  ·  {p.TravelPhasesRemaining} tile(s) to go";
        }
        if (p.State == FieldPartyState.Working)
        {
            return $"{at}  ·  {FieldPostings.Describe(cycle, p)}";
        }
        if (WorldClock.PartyBusy(cycle, p))
        {
            return $"{at}  ·  returning, {p.BusyUntilDay - WorldClock.Now(cycle)} day(s)";
        }
        if (FieldPostings.IsDetachment(p))
        {
            // A detachment with no job is waiting to be picked up. It is not
            // "awaiting orders": it cannot take any.
            return $"{at}  ·  {p.MemberCompanionIds?.Count ?? 0} posted  ·  awaiting collection";
        }

        int strength = p.MemberCompanionIds?.Count ?? 0;
        string who = strength == 0 ? "the wizard alone" : $"{strength} afoot";
        string carrying = p.Carrying != null && !p.Carrying.IsEmpty ? "  ·  carrying spoils" : "";
        return $"{at}  ·  {who}{carrying}  ·  awaiting orders";
    }

    /// <summary>Turn the moon: advance the calendar one lunation and run the
    /// world tick. Extracted from Deploy so the castle sortie and the explicit
    /// "let the moon turn" action cannot drift apart.
    ///
    /// <para>Returns false when the cycle ended, in which case the caller must
    /// stop: ShowConjunction has already taken over the screen.</para></summary>
    /// <summary>Let up to <paramref name="maxDays"/> pass, one day at a time,
    /// stopping at the FIRST day on which anything completes (ruled 2026-09-23:
    /// stop at every event; a quieter rider that skips the harmless ones comes
    /// later). A day that is a new moon runs the per-lunation world tick and is
    /// always a stop, because the tick is where the kingdoms and the threats
    /// move. Reloads the scene at the end, as the moon turn did, because the
    /// map is rebuilt rather than patched.</summary>
    private void PassTime(CycleState cycle, int maxDays)
    {
        var cal = cycle?.Calendar;
        if (cal == null || maxDays <= 0)
        {
            return;
        }
        cycle.PendingSiegeReports?.Clear();

        var events = new System.Collections.Generic.List<string>();
        int passed = 0;

        // Where every party stood before the first day, then after each day
        // (2026-09-25): the record the atlas plays back before the reload,
        // so a march is watched rather than found finished.
        var paths = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Vector2I>>();
        void RecordTiles()
        {
            if (cycle.FieldParties == null)
            {
                return;
            }
            foreach (var p in cycle.FieldParties)
            {
                if (p == null || p.X < 0 || p.Y < 0)
                {
                    continue;
                }
                if (!paths.TryGetValue(p.Id, out var path))
                {
                    path = new System.Collections.Generic.List<Vector2I>();
                    paths[p.Id] = path;
                }
                var tile = new Vector2I(p.X, p.Y);
                if (path.Count == 0 || path[path.Count - 1] != tile)
                {
                    path.Add(tile);
                }
            }
        }
        RecordTiles();

        for (int i = 0; i < maxDays; i++)
        {
            bool newMoon = cal.AdvanceDay();
            passed++;
            WorldClock.StepDay(cycle, events);
            RecordTiles();
            if (newMoon)
            {
                GD.Print($"[Calendar] The moon turns. Lunation {cal.CurrentLunation} " +
                         $"of {cal.LunationsPerCycle}: {cal.CurrentMoonName} " +
                         $"({cal.CurrentMoonSchool} ascendant).");
                RunLunationTick(cycle);
                events.Insert(0, $"The new moon: {cal.CurrentMoonName}, {cal.CurrentMoonSchool} ascendant.");
                break;
            }
            if (events.Count > 0)
            {
                break;
            }
        }
        GD.Print($"[Calendar] {passed} day(s) pass. Lunation {cal.CurrentLunation}, day {cal.DayOfLunation + 1}.");
        SaveManager.MarkDirty();

        if (cal.ConjunctionReached)
        {
            GD.Print("[Calendar] The Grand Conjunction has come. The cycle ends.");
            _deployUi?.QueueFree();
            _deployUi = null;
            _pendingStaging = null;
            SaveManager.SaveIfDirty();
            ShowConjunction();
            return;
        }

        if (events.Count > 0)
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add($"{passed} day(s) pass. " + string.Join(" ", events));
        }
        SaveManager.SaveIfDirty();

        // Walk the pieces along what the days did to them, THEN rebuild. The
        // state is already advanced and saved; the playback is presentation,
        // and a click during it would be an order on a map that is about to
        // be redrawn, so input is off until the reload.
        if (_atlas3D != null)
        {
            _atlas3D.AcceptInput = false;
            _atlas3D.PlayMarches(paths, () =>
            {
                if (IsInsideTree())
                {
                    GetTree().ReloadCurrentScene();
                }
            });
            return;
        }
        GetTree().ReloadCurrentScene();
    }

    private bool AdvanceMoon(CycleState cycle)
    {
        bool crossedLunation = cycle.Calendar.AdvanceLunation();
        SaveManager.MarkDirty();

        if (crossedLunation)
        {
            GD.Print($"[Calendar] The moon turns. Lunation {cycle.Calendar.CurrentLunation} " +
                     $"of {cycle.Calendar.LunationsPerCycle}: {cycle.Calendar.CurrentMoonName} " +
                     $"({cycle.Calendar.CurrentMoonSchool} ascendant).");
            RunLunationTick(cycle);
        }

        if (cycle.Calendar.ConjunctionReached)
        {
            GD.Print("[Calendar] The Grand Conjunction has come. The cycle ends.");
            _deployUi?.QueueFree();
            _deployUi = null;
            _pendingStaging = null;
            ShowConjunction();
            return false;
        }

        SaveManager.SaveIfDirty();
        return true;
    }

    /// <summary>Let the moon turn without a castle sortie.
    ///
    /// <para>This exists because the world tick ran ONLY inside Deploy, so every
    /// slow process in the game was hostage to the castle sortieing: a field
    /// party ordered to march would not take a single step until the fortress
    /// deployed, and the resupply clock, the threat rolls and the interception
    /// rolls were all gated the same way. Ordering a march and then discovering
    /// that nothing happens because the OTHER force has not acted is not a
    /// decision, it is a puzzle about the implementation.</para>
    ///
    /// <para>Deliberately NOT free of consequence: turning the moon spends a
    /// lunation of a twelve-lunation cycle, runs corruption, kingdom drift,
    /// sieges and every threat roll. It is the "I am waiting" move, and waiting
    /// costs the same clock everything else does.</para></summary>
    private void OnPassTimePressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.Calendar == null)
        {
            return;
        }

        int now = cycle.Calendar.AbsoluteDay;
        int nextDay = WorldClock.NextEventDay(cycle, out string what);
        int moonIn = cycle.Calendar.DaysToNewMoon;
        bool stopsAtEvent = nextDay > 0 && nextDay - now < moonIn;
        int days = stopsAtEvent ? nextDay - now : moonIn;
        string until = stopsAtEvent ? what : "the new moon";

        string idle = IdleForcesLine(cycle);
        var dlg = new ConfirmationDialog
        {
            Title = "Pass time",
            OkButtonText = $"Pass {days} day(s)",
            DialogText =
                $"Lunation {cycle.Calendar.CurrentLunation}, day {cycle.Calendar.DayOfLunation + 1}. "
                + $"{days} day(s) pass, until {until}.\n\n"
                + WhatResolvesLine(cycle)
                + (string.IsNullOrEmpty(idle)
                    ? ""
                    : $"\n\nNothing is ordered for: {idle}. Their days pass unused."),
        };
        dlg.Confirmed += () =>
        {
            dlg.QueueFree();
            PassTime(cycle, days);
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>Forces with nothing to do this lunation, named. The warning is
    /// the whole point of the control: the failure it prevents is turning the
    /// moon having forgotten to give somebody an order, which costs a twelfth of
    /// the cycle and is invisible after the fact.</summary>
    private string IdleForcesLine(CycleState cycle)
    {
        var idle = new System.Collections.Generic.List<string>();

        bool castleBusy = !cycle.CastleParked
                          || cycle.CastleRepairLunations > 0
                          || WorldClock.CastleBusy(cycle)
                          || !string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId);
        if (!castleBusy)
        {
            idle.Add("the castle");
        }

        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p != null && p.X >= 0 && p.State == FieldPartyState.AtAnchor
                    && !WorldClock.PartyBusy(cycle, p) && !FieldPostings.IsDetachment(p))
                {
                    idle.Add(p.Name);
                }
            }
        }
        return string.Join(", ", idle);
    }

    /// <summary>What the turn will actually resolve, so the player is agreeing
    /// to something specific rather than to a word.</summary>
    private string WhatResolvesLine(CycleState cycle)
    {
        var lines = new System.Collections.Generic.List<string>();

        int now = WorldClock.Now(cycle);
        if (cycle.CastleParked && cycle.CastleRepairLunations > 0)
        {
            int days = cycle.CastleRepairLunations * CalendarState.DaysPerLunation - cycle.CastleRepairDayAccum;
            lines.Add($"The castle's resupply finishes in {days} day(s).");
        }
        if (WorldClock.CastleBusy(cycle))
        {
            lines.Add($"The castle is free again in {cycle.CastleBusyUntilDay - now} day(s).");
        }
        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null)
                {
                    continue;
                }
                if (p.State == FieldPartyState.Travelling)
                {
                    int days = p.TravelPhasesRemaining * FieldMarch.DaysPerTile - p.TravelDayAccum;
                    lines.Add($"{p.Name} arrives at ({p.DestX},{p.DestY}) in {days} day(s).");
                }
                else if (p.State == FieldPartyState.Working)
                {
                    int cost = FieldPostings.SuppliesFor(p.WorkKind);
                    string pay = cost > 0 ? $" ({cost} supplies at the new moon)" : "";
                    lines.Add(p.WorkDaysTotal > 0
                        ? $"{p.Name} finish {FieldPostings.DescribeVerb(p.WorkKind)} in {p.WorkDaysLeft} day(s){pay}."
                        : $"{p.Name} {FieldPostings.DescribeVerb(p.WorkKind)} at ({p.X},{p.Y}){pay}.");
                }
                else if (WorldClock.PartyBusy(cycle, p))
                {
                    lines.Add($"{p.Name} are free again in {p.BusyUntilDay - now} day(s).");
                }
            }
        }
        lines.Add($"The new moon is {cycle.Calendar.DaysToNewMoon} day(s) off: corruption spreads, the kingdoms move, "
                + "and anything watching your camps and roads takes its chance.");
        return string.Join("\n", lines);
    }

    /// <summary>Give the orders to a named force. Disarms the move verb on the
    /// way through: an armed order belongs to the piece it was armed for, and
    /// silently repointing it is how a player marches the fortress somewhere
    /// they meant to send three people on foot.</summary>
    private void SelectPiece(string pieceId)
    {
        _selectedPieceId = pieceId ?? "";
        if (_marchModeBtn != null)
        {
            _marchModeBtn.ButtonPressed = false;
        }
        _marchMode = false;
        RefreshPieceChrome();
    }

    /// <summary>Keep the roster, the move button and the map agreeing. Called
    /// after anything that moves a piece or changes whose turn it is to act.</summary>
    private void RefreshPieceChrome()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        ExpeditionAnchors.EnsurePrimaryParty(cycle);

        // A selection can go stale: a named party could be gone from the save
        // between cycles. Fall back to the castle rather than commanding a
        // force that is not there.
        if (!CastleSelected && SelectedParty() == null)
        {
            _selectedPieceId = "";
        }
        var party = SelectedParty();

        bool cityNow = _atlas3D?.CityMode ?? false;

        if (_marchModeBtn != null)
        {
            _marchModeBtn.Text = CastleSelected ? "\u2691  March castle" : "\u2691  Move party";
            // Visibility lives HERE, not at build time, so it is re-decided every
            // time anything changes rather than frozen at whatever the map
            // happened to be showing when the HUD was constructed.
            _marchModeBtn.Visible = !cityNow;
            if (cityNow)
            {
                _marchModeBtn.ButtonPressed = false;
                _marchMode = false;
            }
        }

        if (_viewToggleBtn != null)
        {
            bool home = _atlas3D?.ActiveCityIsHome ?? true;
            _viewToggleBtn.Text = cityNow
                ? "\u25C8  To the world map"
                : "\u25C8  To the home city";
            _viewToggleBtn.Disabled = !cityNow && (_atlas3D == null || !_atlas3D.HasCityGrounds);
            _viewToggleBtn.TooltipText = cityNow
                ? (home ? "Up to the world. Escape does the same." : "Leave this city. Escape does the same.")
                : "Down into the guild's own city.";
        }

        if (_sortieBtn != null)
        {
            _sortieBtn.Visible = CastleSelected && !cityNow;
            if (_sortieBtn.Visible)
            {
                bool can = CanSortie(cycle, out string sortieWhy);
                _sortieBtn.Disabled = !can;
                _sortieBtn.TooltipText = can
                    ? "The castle strides out from where it stands. Fuel is the budget; the days it "
                      + $"costs are charged on return at about {WorldClock.DaysPerFuel:0.##} a fuel."
                    : sortieWhy;
            }
        }

        // A detachment is a posted piece: it takes no orders but Stop work,
        // Collect and Recall. Hide the move verb for it rather than letting an
        // armed click be refused tile by tile.
        bool isDetachment = party != null && FieldPostings.IsDetachment(party);
        if (_marchModeBtn != null && isDetachment)
        {
            _marchModeBtn.Visible = false;
            _marchModeBtn.ButtonPressed = false;
            _marchMode = false;
        }

        if (_partyDeployBtn != null)
        {
            bool canField = party != null
                            && !isDetachment
                            && party.State == FieldPartyState.AtAnchor
                            && party.X >= 0 && party.Y >= 0
                            && (_atlas3D == null || !_atlas3D.CityMode);
            _partyDeployBtn.Visible = canField;
            if (_stopWorkBtn != null)
            {
                _stopWorkBtn.Visible = party != null && party.State == FieldPartyState.Working && !cityNow;
            }
            bool freeNow = canField && !WorldClock.PartyBusy(cycle, party);
            if (_postBtn != null)
            {
                var options = freeNow ? FieldPostings.OptionsAt(cycle, party) : null;
                _postBtn.Visible = options != null && options.Count > 0;
                if (_postBtn.Visible)
                {
                    _postBtn.TooltipText = options.Count == 1
                        ? options[0].Title
                        : $"{options.Count} postings are possible here.";
                }
            }
            if (_collectBtn != null || _recallBtn != null)
            {
                var (collectable, recallable) = DetachmentVerbsFor(cycle, party);
                if (_collectBtn != null)
                {
                    _collectBtn.Visible = collectable != null && !cityNow;
                    if (collectable != null)
                    {
                        _collectBtn.Text = $"\u21A9  Collect {collectable.Name}";
                    }
                }
                if (_recallBtn != null)
                {
                    _recallBtn.Visible = recallable != null && !cityNow;
                    if (recallable != null)
                    {
                        _recallBtn.Text = $"\u21A9  Recall {recallable.Name} through the waystone";
                    }
                }
            }
            if (_interveneBtn != null)
            {
                string verb = freeNow ? InterventionVerbAt(cycle, party) : null;
                _interveneBtn.Visible = verb != null;
                if (verb != null)
                {
                    _interveneBtn.Text = verb;
                }
            }
            if (_audienceBtn != null)
            {
                // Called unconditionally: an out parameter behind a
                // short-circuited && is unassigned on the false path (CS0165).
                bool courtOk = AudienceCourtAt(cycle, party, out _, out string courtWhy);
                bool court = freeNow && courtOk;
                bool here = freeNow && !court && !string.IsNullOrEmpty(courtWhy) && !courtWhy.StartsWith("No court");
                _audienceBtn.Visible = court || here;
                _audienceBtn.Disabled = !court;
                _audienceBtn.TooltipText = court
                    ? "Open a table with this court. A field dive: the party takes the field here and the "
                      + "table opens before their first step."
                    : courtWhy;
            }
            if (_bankFurnaceBtn != null)
            {
                _bankFurnaceBtn.Visible = CastleSelected && !cityNow
                                          && cycle?.CastleSortie != null && cycle.CastleSortie.IsLive();
            }
            if (canField)
            {
                bool busy = WorldClock.PartyBusy(cycle, party);
                int freeIn = busy ? party.BusyUntilDay - WorldClock.Now(cycle) : 0;
                _partyDeployBtn.Disabled = busy;
                _partyDeployBtn.Text = busy
                    ? $"\u25B6  Take the field (free in {freeIn} day(s))"
                    : "\u25B6  Take the field";
                _partyDeployBtn.TooltipText = busy
                    ? $"{party.Name} are still out on their last dive. Pass time; they are free on day {party.BusyUntilDay - WorldClock.Now(cycle)} from now."
                    : $"Send {party.Name} into the field at ({party.X},{party.Y}). The dive costs days in "
                      + $"proportion to the rations it eats, about {WorldClock.DaysPerRation:0.##} a ration, "
                      + "charged when they come back.";
            }
        }

        if (_turnMoonBtn != null && cycle?.Calendar != null)
        {
            int nextDay = WorldClock.NextEventDay(cycle, out string what);
            int moonIn = cycle.Calendar.DaysToNewMoon;
            int now = cycle.Calendar.AbsoluteDay;
            if (nextDay > 0 && nextDay - now < moonIn)
            {
                _turnMoonBtn.Text = $"\u263D  Pass time: {nextDay - now} day(s), {what}";
            }
            else
            {
                _turnMoonBtn.Text = $"\u263D  Pass time: {moonIn} day(s) to the new moon";
            }
        }

        RebuildForcesPanel();

        if (_atlas3D != null && cycle != null)
        {
            Vector2I? castleAt = cycle.World != null && cycle.World.InBounds(cycle.CastleX, cycle.CastleY)
                ? new Vector2I(cycle.CastleX, cycle.CastleY)
                : null;

            var partyTiles = new System.Collections.Generic.List<Vector2I>();
            var partyNames = new System.Collections.Generic.List<string>();
            var partyDests = new System.Collections.Generic.List<Vector2I?>();
            var partyBanners = new System.Collections.Generic.List<string>();
            var partyFigures = new System.Collections.Generic.List<System.Collections.Generic.List<(Color tint, bool injured)>>();
            var partyIds = new System.Collections.Generic.List<string>();
            int selectedIndex = -1;
            if (cycle.FieldParties != null)
            {
                foreach (var p in cycle.FieldParties)
                {
                    if (p == null || p.X < 0 || p.Y < 0)
                    {
                        continue;
                    }
                    if (p.Id == _selectedPieceId)
                    {
                        selectedIndex = partyTiles.Count;
                    }
                    partyTiles.Add(new Vector2I(p.X, p.Y));
                    partyNames.Add(p.Name);
                    partyDests.Add(p.State == FieldPartyState.Travelling && p.DestX >= 0 && p.DestY >= 0
                        ? new Vector2I(p.DestX, p.DestY)
                        : null);
                    // A detachment draws as a banner with a glyph for its job.
                    partyBanners.Add(FieldPostings.IsDetachment(p) ? BannerGlyph(p) : "");
                    // And the people with it, one figure each. A party out on a
                    // sortie is drawn where its sortie stands, but its members
                    // are still its members.
                    partyFigures.Add(CompanionWhereabouts.FigureTints(cycle, p.MemberCompanionIds));
                    partyIds.Add(p.Id);
                }
            }
            var crewIds = new System.Collections.Generic.List<string>();
            if (cycle.Companions != null)
            {
                foreach (var c in cycle.Companions)
                {
                    if (c != null && c.IsRecruited && !c.IsPermadead && c.Posting == CompanionPosting.Crew)
                    {
                        crewIds.Add(c.Id);
                    }
                }
            }
            _atlas3D.SetPieces(castleAt, partyTiles, partyNames, partyDests,
                               CastleSelected ? -1 : selectedIndex, partyBanners,
                               partyFigures, CompanionWhereabouts.FigureTints(cycle, crewIds),
                               partyIds);
        }
    }

    /// <summary>The letter a detachment's banner carries. Letters, not
    /// pictographs: the map's fonts have every letter and not every glyph, and
    /// the roster row beside it spells the job out in full.</summary>
    private static string BannerGlyph(FieldParty det)
    {
        string kind = det?.State == FieldPartyState.Working ? det.WorkKind : "";
        return kind switch
        {
            FieldPostings.Garrison => "[G]",
            FieldPostings.Survey => "[S]",
            FieldPostings.HoldLine => "[H]",
            FieldPostings.Siege => "[B]",
            FieldPostings.Envoy => "[E]",
            _ => "[-]",
        };
    }

    private void OnMarchModeToggled(bool pressed)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;

        // The party's version of the same verb. It walks rather than relocating,
        // so its refusals are different, but the click that follows means the
        // same thing and goes through the same mode.
        if (!CastleSelected)
        {
            var fp = SelectedParty();
            if (pressed && !FieldMarch.CanOrderAtAll(cycle, fp, out string partyWhy))
            {
                ShowStrategicNotice("The party cannot move", partyWhy);
                if (_marchModeBtn != null) _marchModeBtn.ButtonPressed = false;
                _marchMode = false;
                return;
            }
            _marchMode = pressed;
            if (pressed)
            {
                if (_buildModeBtn != null) _buildModeBtn.ButtonPressed = false;
                if (_annexButton != null) _annexButton.ButtonPressed = false;
                ShowStrategicNotice("Move the field party",
                    $"Click charted ground to send them there on foot: {FieldMarch.DaysPerTile} "
                    + "day(s) a tile, and the road is watched. Click the castle, a "
                    + "raised waystone or the dock to step through instantly instead.");
            }
            return;
        }

        if (pressed && !CastleMarch.CanMarchAtAll(cycle, out string why))
        {
            ShowStrategicNotice("The castle cannot march", why);
            if (_marchModeBtn != null) _marchModeBtn.ButtonPressed = false;
            _marchMode = false;
            return;
        }

        _marchMode = pressed;
        // One ground-click mode at a time.
        if (pressed)
        {
            if (_buildModeBtn != null) _buildModeBtn.ButtonPressed = false;
            if (_annexButton != null) _annexButton.ButtonPressed = false;
            int range = cycle != null ? CastleMarch.RangeInTiles(cycle) : 0;
            ShowStrategicNotice("March",
                $"Click charted ground to send the fortress there. Range {range} tile(s) " +
                $"on the fuel in the furnace, at {CastleMarch.FuelPerTile} per tile.");
        }
    }

    /// <summary>A one-shot informational dialog. Used where a refusal has to be
    /// read before the player wastes a click.</summary>
    private void ShowStrategicNotice(string title, string body)
    {
        var dlg = new AcceptDialog { Title = title, DialogText = body };
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void OnAtlas3DTilePicked(int col, int row)
    {
        if (_world == null)
            return;

        // City view owns clicks: when this pick just DESCENDED into a settlement
        // (HandlePick enters city mode, then TilePicked still fires), none of the
        // world-map verbs below may run. Most visibly the staging snap, which would
        // pop a deploy window over the freshly entered city (any settlement footprint
        // tile sits within StagingClickTolerance of its own beacon).
        if (_atlas3D != null && _atlas3D.CityMode)
            return;

        // March mode owns the click once the city has had its say: it is an
        // explicit armed verb, so it takes priority over the passive map
        // behaviours below, but never over descending into a settlement.
        if (_marchMode)
        {
            if (!CastleSelected)
            {
                TryMovePartyTo(col, row);
            }
            else
            {
                TryMarchTo(col, row);
            }
            return;
        }

        // Deploy drawer open: the map is live for RETARGETING only. A click near
        // another staging beacon moves the drawer there; caches/warfronts wait
        // until the drawer closes (no dialog stacking over the launch screen).
        if (_deployUi != null)
        {
            // 2026-09-24: the confirm belongs to the castle's own tile, so
            // there is nothing to retarget to. The map stays live to look at;
            // clicks do nothing until the confirm closes.
            return;
        }

        // Supply cache first, on the EXACT tile (caches are denser than staging, so no
        // snap: an exact click opens the cache; a near click falls through to the
        // staging snap below, which is the map's primary action).
        for (int i = 0; i < _world.Pois.Count; i++)
        {
            var poi = _world.Pois[i];
            if (poi.Kind == PoiKind.SupplyCache && poi.X == col && poi.Y == row
                && (poi.Discovered || _debugReveal))
            {
                ShowSupplyCacheDialog(i);
                return;
            }
        }

        // Warfront (non-cache-siege) → the intervention dialog. Cache sieges sit ON a
        // cache tile and are handled by the supply-cache branch above; other fronts
        // route here on a near-exact hit (before the wider staging snap, so a front
        // next to a staging point isn't swallowed by it).
        var wcycle = SaveManager.ActiveSave?.Cycle;
        if (wcycle?.Warfronts != null)
            foreach (var wf in wcycle.Warfronts)
                if (!wf.Closed && wf.HasFocus && !wf.IsCacheSiege
                    && HexCoord.OffsetDistance(col, row, wf.FocusCol, wf.FocusRow) <= 1)
                {
                    ShowWarfrontIntervene(wf);
                    return;
                }

        // Staging: snap to the nearest AVAILABLE staging point within tolerance, so a
        // near-miss on a tiny beacon still deploys instead of doing nothing.
        StagingPoint nearest = null;
        int nearestD = int.MaxValue;
        if (_world.StagingPoints != null)
            foreach (var sp in _world.StagingPoints)
            {
                if (!sp.Available) continue;
                int d = HexCoord.OffsetDistance(col, row, sp.X, sp.Y);
                if (d < nearestD) { nearestD = d; nearest = sp; }
            }
        if (nearest != null && nearestD <= StagingClickTolerance)
            OnStagingClicked(nearest);
    }

    /// <summary>Stage 2 (Phase 2), the "descend into your city" transition: swoop the
    /// atlas camera into the home city, then change to the campus scene once the fly
    /// completes. Returns false when there's no atlas/home to fly to, so the caller
    /// (HudManager's Return-to-Campus) falls back to the plain scene warp.</summary>
    /// <summary>True while the atlas is in city view (home or NPC). Read by HudManager
    /// to hide its Return-to-Campus warp; the button is moot (home) or a dead click
    /// (NPC city, where EnterCityMode early-returns) while a city fills the screen.</summary>
    public bool InCityView => _atlas3D?.CityMode ?? false;

    public bool TryDescendToCampus()
    {
        if (_atlas3D == null || !_atlas3D.HasCityGrounds)
            return false;
        // True geometry merge: the campus is permanently in the world at true scale, so
        // "descending" is nothing but the camera flying down; EnterCityMode does the swoop.
        _atlas3D.EnterCityMode();
        return true;
    }

    /// <summary>Stage 3 (Phase 2): host the campus as an in-world overlay instead of a
    /// scene swap. The strategic scene stays alive underneath (atlas hidden + input off);
    /// the campus draws its own chrome, so the global HUD hides too. Leaving is wired back
    /// through <see cref="CampusScreen.OverlayLeaveHandler"/> → <see cref="HideCampusOverlay"/>.</summary>
    private void ShowCampusOverlay() => ShowCampusOverlay(CampusPanelId.Campus);

    private void ShowCampusOverlay(CampusPanelId initial)
    {
        if (_campusOverlay != null) return;   // grounds are covered while it's up; can't re-enter
        var scene = GD.Load<PackedScene>(CampusScenePath);
        if (scene == null)
        {
            GetTree().ChangeSceneToFile(CampusScenePath);   // fallback: classic swap
            return;
        }

        // Which panel the overlay lands on: the grounds MAP for a plain descend, or the
        // clicked building's panel when entered by clicking the world-map grounds model.
        CampusScreen.InitialPanel = initial;

        var layer = new CanvasLayer { Name = "CampusOverlayLayer", Layer = 50 };
        layer.AddChild(scene.Instantiate());

        // The global HUD is hidden while the overlay is up, so the host supplies the
        // "leave for the world" affordance itself. Added AFTER the campus so it draws on
        // top within the layer.
        var leaveBtn = new Button { Text = "↑  To the World Map" };
        leaveBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        leaveBtn.OffsetLeft = -244;
        leaveBtn.OffsetTop = 14;
        leaveBtn.OffsetRight = -14;
        leaveBtn.OffsetBottom = 48;
        leaveBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(leaveBtn, isPrimary: true);
        leaveBtn.Pressed += HideCampusOverlay;
        layer.AddChild(leaveBtn);

        AddChild(layer);
        _campusOverlay = layer;

        _atlas3D.AcceptInput = false;
        if (_atlas3DLayer != null) _atlas3DLayer.Visible = false;
        PlayerSession.CampusOverlayOpen = true;
        CampusScreen.OverlayLeaveHandler = HideCampusOverlay;
        HudManager.Instance?.RefreshVisibility();
    }

    /// <summary>Tear down the campus overlay and return to the world: reveal the atlas and
    /// swoop back out (the "ascend"). Invoked via CampusScreen.OverlayLeaveHandler.</summary>
    private void HideCampusOverlay()
    {
        if (_campusOverlay == null) return;
        _campusOverlay.QueueFree();
        _campusOverlay = null;

        CampusScreen.OverlayLeaveHandler = null;
        PlayerSession.CampusOverlayOpen = false;
        if (_atlas3DLayer != null) _atlas3DLayer.Visible = true;
        HudManager.Instance?.RefreshVisibility();

        if (_atlas3D != null)
        {
            _atlas3D.AcceptInput = true;
            // The overlay's only exits mean "to the world" (its leave button, or a deploy),
            // so leaving it also leaves city view when that's where it was opened from.
            // (A building menu that RETURNS to the city is the floating-panel increment.)
            if (_atlas3D.CityMode) _atlas3D.LeaveCityMode();
            else _atlas3D.FlyToOverview();
        }
    }

    /// <summary>True geometry merge (Phase 2): a building in CITY VIEW was clicked. Route it
    /// as the campus does: a building that hosts a panel opens that panel; one that hosts a
    /// separate screen (deck editor, card library, upgrade) changes scene; an inert building
    /// does nothing.
    ///
    /// <para>"Build in place" finish: a panel the strategic scene can host on its own
    /// (<see cref="HomeBuildingPanelHost.CanFloat"/>) FLOATS over the live city. The world
    /// stays visible behind it and closing returns to the city, not the world. The
    /// lifecycle-heavy panels (Expedition / Quests / Council) still need the full CampusScene
    /// overlay, so they fall back to it until that machinery is generalized (Phase 3).</para></summary>
    private void OnHomeBuildingPicked(string buildingId, Vector2I coord)
    {
        var dest = CampusLocationRegistry.ForBuilding(buildingId);
        if (!dest.IsValid)
        {
            // No system panel. Still open the host for its tier/upgrade
            // strip (2026-08-13: the city view's upgrade path; previously
            // these buildings were mute).
            ShowFloatingPanel(null, buildingId);
            _atlas3D?.FocusHomeBuilding(coord);
            return;
        }
        if (dest.Panel.HasValue)
        {
            // Deploy-flow streamline (2026-08-21): the Gatehouse no longer opens
            // the Expedition tab. Clicking it IS the deploy order: fly out of
            // the city and open the deploy drawer on the last-used staging
            // point. The old Expedition overlay remains only as the fallback
            // for lifecycle moments (new-cycle school pick, ungenerated world).
            if (dest.Panel.Value == CampusPanelId.Expedition)
            {
                BeginDeployFlow();
                return;
            }
            if (HomeBuildingPanelHost.CanFloat(dest.Panel.Value))
            {
                ShowFloatingPanel(dest.Panel.Value, buildingId);
                _atlas3D?.FocusHomeBuilding(coord);   // the showcase swoop (2026-08-13)
            }
            else
                ShowCampusOverlay(dest.Panel.Value);   // lifecycle-heavy panel: full overlay for now
        }
        else if (!string.IsNullOrEmpty(dest.ScenePath))
        {
            // Entered from the city, so the utility screen's return warp should land
            // back in the city, consumed by BuildAtlas3D when this scene reloads.
            PlayerSession.StartInCityOnOpen = true;
            GetTree().ChangeSceneToFile(dest.ScenePath);
        }
    }

    /// <summary>Float a single campus panel over the LIVE city view. Unlike
    /// <see cref="ShowCampusOverlay"/> this leaves the atlas/world VISIBLE (only its input is
    /// gated) and closes back to the city rather than the world: the finish that lets a
    /// building's menu open in place. One panel at a time.</summary>
    private void ShowFloatingPanel(CampusPanelId? panel, string buildingId, string titleOverride = null)
    {
        // (2026-08-13) Panel SWAP: picking another building replaces the open
        // card instead of being swallowed. Atlas input stays ENABLED. The
        // host's catcher now covers only the card, so grounds clicks (and the
        // camera) work beside it; that's the point.
        if (_floatingPanel != null)
        {
            var old = _floatingPanel;
            _floatingPanel = null;   // null first: Close() fires HideFloatingPanel
            old.Close();
        }
        string title = titleOverride ?? (BuildingDatabase.GetTemplate(buildingId)?.Name ?? "");
        _floatingPanel = HomeBuildingPanelHost.Create(this, panel, title, HideFloatingPanel,
            buildingId, enc => ShowFloatedPanelNarrative(enc),
            onBuildingChanged: () => _atlas3D?.RefreshCityGrowth());
        AddChild(_floatingPanel);
    }

    /// <summary>Tear down the floated panel and hand control back to the CITY view (not the
    /// world): drop our reference and re-enable atlas input. The host frees itself. Invoked as
    /// the host's close callback.</summary>
    private void HideFloatingPanel()
    {
        if (_floatingPanel == null) return;
        _floatingPanel = null;
        // Atlas input was never disabled for floated panels (2026-08-13 swap
        // behavior); nothing to re-enable. Deliberately does NOT leave city
        // view: the panel closes back into the city, pulling the camera
        // back up from the building-focus swoop.
        _atlas3D?.UnfocusHomeBuilding();
    }

    /// <summary>A landmark hex in city view was clicked (2026-08-13: no longer
    /// a no-op). Landmark restoration beats are PURE narrative (verified: no
    /// LaunchGuardian/ResolutionKind in CampusLandmarkData), so the
    /// session-one floated-narrative host carries them completely: beat
    /// shows over the live city, outcome through the shared applier, flags
    /// advance the ruined → active → restored chain. A fully restored
    /// landmark toasts instead of opening nothing.</summary>
    private void OnHomeLandmarkPicked(string landmarkId, Vector2I coord)
    {
        var lm = CampusLandmarkRegistry.Get(landmarkId);
        var save = SaveManager.ActiveSave;
        if (lm == null || save == null) return;

        var enc = lm.GetEncounter(save.HasFlag);
        if (enc == null)
        {
            // Restoration complete. If this landmark is now a door (restored Observatory →
            // Hall of Records), open it in place like a building; otherwise acknowledge and stop.
            var dest = CampusLocationRegistry.ForLandmark(landmarkId);
            if (dest.Panel.HasValue && HomeBuildingPanelHost.CanFloat(dest.Panel.Value))
            {
                ShowFloatingPanel(dest.Panel.Value, buildingId: "", titleOverride: lm.DisplayName);
                _atlas3D?.FocusHomeBuilding(coord);
                return;
            }
            EnsureCityExploreToasts();
            _cityExploreToasts?.Push($"{lm.DisplayName} restored.", QuestToastKind.Complete);
            return;
        }
        // Restoration advances change the grounds themselves (ruined → active
        // → restored stamps), so rebuild the 3D grounds after the beat applies.
        ShowFloatedPanelNarrative(enc, onApplied: () => _atlas3D?.RefreshCityGrowth());
    }

    // ── Construction from the city view (2026-08-13) ─────────────────────

    private CanvasLayer _constructCard;

    /// <summary>The Build toggle (2026-08-19, playtest): construction is ARMED, like annex
    /// mode, instead of ambushing from any bare-ground click. The card "jumped out of
    /// nowhere" with no instruction. Arming disarms annex (and vice versa), announces
    /// itself with a toast so the player knows what a click will now do, and disarming
    /// closes any open construct card.</summary>
    private void OnBuildModeToggled(bool pressed)
    {
        _buildMode = pressed;
        if (pressed)
        {
            if (_annexButton != null) _annexButton.ButtonPressed = false;
            EnsureCityExploreToasts();
            _cityExploreToasts?.Push("Build mode: click open ground to choose what to raise there.",
                                     QuestToastKind.Progress);
        }
        else
        {
            CloseConstructCard();
        }
    }

    /// <summary>A bare home-grounds hex was clicked: open the construct card,
    /// the unbuilt ledger with tier-1 costs, buildable in place at that hex.
    /// This closes the Phase-2 gap where NEW buildings could only be raised
    /// through the full-screen campus overlay.</summary>
    private void OnHomeGroundPicked(Vector2I coord)
    {
        if (!_buildMode) return;   // construction is armed via the Build toggle, like annex
        if (_constructCard != null) return;
        // Swap symmetry: clicking bare ground while a building panel floats
        // closes the panel and opens the construct card in its place.
        if (_floatingPanel != null)
        {
            var old = _floatingPanel;
            _floatingPanel = null;
            old.Close();
        }
        var save = SaveManager.ActiveSave;
        if (save == null) return;

        var unbuilt = CampusConstruction.Unbuilt(save);

        _constructCard = new CanvasLayer { Name = "ConstructCard", Layer = 50 };
        AddChild(_constructCard);
        if (_atlas3D != null) _atlas3D.AcceptInput = false;

        var catcher = new Control();
        catcher.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        catcher.MouseFilter = Control.MouseFilterEnum.Stop;
        // Click-outside-to-close (2026-08-19): the card itself stops input, so any
        // press reaching the catcher is beside the card: dismiss, like a popup.
        catcher.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton b && b.Pressed && b.ButtonIndex == MouseButton.Left)
                CloseConstructCard();
        };
        _constructCard.AddChild(catcher);

        var card = new PanelContainer();
        card.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        card.OffsetLeft = -520;
        // Below the global top bar (2026-08-19): the HUD CanvasLayer (90) draws over
        // this card's layer (50), so a full-height card had its title + Close button
        // buried under the bar: "no way to close the menu".
        card.OffsetTop = HudManager.BarHeight;
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = UITheme.BgBase });
        catcher.AddChild(card);

        var margins = new MarginContainer();
        margins.AddThemeConstantOverride("margin_left", 18);
        margins.AddThemeConstantOverride("margin_right", 18);
        margins.AddThemeConstantOverride("margin_top", 14);
        margins.AddThemeConstantOverride("margin_bottom", 14);
        card.AddChild(margins);

        var scroll = new ScrollContainer
        { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        margins.AddChild(scroll);

        var vbox = new VBoxContainer
        { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(vbox);

        var header = new HBoxContainer();
        vbox.AddChild(header);
        var title = new Label
        {
            Text = "Raise a Building",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        header.AddChild(title);
        var closeBtn = new Button { Text = "✕  Close" };
        UITheme.ApplyButtonStyle(closeBtn, isPrimary: false);
        closeBtn.Pressed += CloseConstructCard;
        header.AddChild(closeBtn);

        var sub = new Label
        {
            Text = $"Gold: {save.Gold}   Materials: {save.BuildMaterials}   Siting at the chosen ground.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        sub.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        sub.AddThemeColorOverride("font_color", UITheme.TextDim);
        vbox.AddChild(sub);

        if (unbuilt.Count == 0)
        {
            var none = new Label { Text = "Every building in the ledger already stands." };
            none.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            vbox.AddChild(none);
        }

        foreach (var (bs, template) in unbuilt)
        {
            var tier1 = template.Tiers.Find(t => t.Tier == 1);
            if (tier1 == null) continue;
            string reason = CampusConstruction.CannotBuildReason(save, bs.Id);

            var row = new PanelContainer();
            row.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = UITheme.BgCard,
                ContentMarginLeft = 12, ContentMarginRight = 12,
                ContentMarginTop = 8, ContentMarginBottom = 8,
            });
            vbox.AddChild(row);

            // Footprint preview (2026-08-13): hovering a row paints the
            // building's would-be footprint on the grounds at the chosen
            // anchor: gold fits, red doesn't. Siting will matter more once
            // adjacency bonuses land; this is the read-before-you-build.
            string hoverId = bs.Id;
            row.MouseEntered += () => _atlas3D?.PreviewHomeFootprint(hoverId, coord);
            row.MouseExited += () => _atlas3D?.ClearHomeFootprintPreview();

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 4);
            row.AddChild(col);

            var name = new Label
            { Text = $"{template.Name}   ·   footprint {template.Footprint.Count} hex{(template.Footprint.Count == 1 ? "" : "es")}" };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            col.AddChild(name);

            var desc = new Label { Text = template.Description };
            desc.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            desc.AddThemeColorOverride("font_color", UITheme.TextDim);
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            col.AddChild(desc);

            var buildBtn = new Button
            {
                Text = reason == null
                    ? $"Build ({tier1.GoldCost}g + {tier1.EffectiveMaterialsCost} materials)"
                    : reason,
                Disabled = reason != null,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            };
            buildBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            UITheme.ApplyButtonStyle(buildBtn, isPrimary: reason == null);
            string capturedId = bs.Id;
            buildBtn.Pressed += () => OnConstructPicked(capturedId, coord);
            col.AddChild(buildBtn);
        }
    }

    /// <summary>Place FIRST, purchase second: placement writes only siting
    /// fields and is revertible; a paid-but-unplaceable building would strand
    /// gold. If the anchor doesn't fit (multi-tile footprint on a cramped
    /// hex), the campus overlay's placement tool (with rotation) remains
    /// the fallback.</summary>
    private void OnConstructPicked(string buildingId, Vector2I coord)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return;

        EnsureCityExploreToasts();

        // PURCHASE FIRST: PlaceBuilding refuses Tier 0 buildings (the campus
        // flow's contract: buy, then site). A failed siting refunds the tier,
        // so gold is never stranded. (2026-08-13 fix: the original
        // place-first ordering failed silently for every unbuilt building.)
        if (!CampusConstruction.TryBuildOrUpgrade(save, buildingId))
        {
            _cityExploreToasts?.Push("The coin came up short. Construction cancelled.",
                                     QuestToastKind.Progress);
            return;
        }

        if (_atlas3D == null || !_atlas3D.TryPlaceHomeBuilding(buildingId, coord))
        {
            CampusConstruction.RefundTier(save, buildingId);
            _cityExploreToasts?.Push("It doesn't fit there. Pick more open ground, or site it " +
                                     "from the campus placement tool (it can rotate).",
                                     QuestToastKind.Progress);
            return;
        }

        var template = BuildingDatabase.GetTemplate(buildingId);
        _cityExploreToasts?.Push($"{template?.Name ?? buildingId} rises on the campus.",
                                 QuestToastKind.Complete);
        CloseConstructCard();
    }

    private void CloseConstructCard()
    {
        if (_constructCard == null) return;
        _atlas3D?.ClearHomeFootprintPreview();
        _constructCard.QueueFree();
        _constructCard = null;
        if (_atlas3D != null) _atlas3D.AcceptInput = true;
    }

    /// <summary>City view entered/left: show/hide the host buttons (leave-to-world,
    /// annex-a-district, city services) that only make sense in city view. The global
    /// top-bar HUD stays VISIBLE in city view (2026-08-19: the city is the hub screen
    /// and its readouts/menus belong there); RefreshVisibility still runs so the bar's
    /// Return-to-Campus button can hide itself while we're already home.</summary>
    private void OnCityModeChanged(bool on)
    {
        HudManager.Instance?.RefreshVisibility();
        bool home = _atlas3D?.ActiveCityIsHome ?? true;
        // Annexing/building are home-campus affordances only; hide them in an NPC city (Phase 3).
        if (_annexButton != null) _annexButton.Visible = on && home;
        if ((!on || !home) && _annexButton != null)
            _annexButton.ButtonPressed = false;   // drop annex mode on exit or in an NPC city
        if (_buildModeBtn != null) _buildModeBtn.Visible = on && home;
        if ((!on || !home) && _buildModeBtn != null)
            _buildModeBtn.ButtonPressed = false;  // disarm build mode too (Toggled(false) closes the card)

        // Phase 3 services (enemy capital only): show the "City Services" button in an NPC city view
        // and auto-open the menu on entry; hide/close both when leaving the city.
        bool npc = on && !home;
        if (_cityServicesBtn != null) _cityServicesBtn.Visible = npc;
        if (npc && !_suppressServicesOnce) ShowCityServices();
        else if (!npc && _cityServices != null) _cityServices.Close();
        if (npc) _suppressServicesOnce = false;   // one-shot (fight return lands on the city, not the menu)

        RefreshHint();

        // The world-map forces: the roster, the move verb and Take the field.
        //
        // BUG (found 2026-09-23 from a [Forces] probe reading
        // "rows=0 visible=False"): every one of these had its visibility decided
        // ONCE, in BuildHud, from `_atlas3D.CityMode`. The map opens in the home
        // city, so that read was TRUE, so all of them were built hidden and
        // nothing ever showed them again. This handler updated four other
        // controls on the same signal and simply did not know about them.
        //
        // The March control had been invisible for the same reason since the day
        // it was added, which is why it was only ever reachable from the deploy
        // drawer.
        RefreshPieceChrome();

        // First home-city entry this session: open the orientation card once, unbidden;
        // the hub otherwise drops a new player in with zero instruction.
        if (on && home && !_helpAutoShown)
        {
            _helpAutoShown = true;
            ShowHelpCard();
        }
    }

    /// <summary>Bottom hint line, per view mode. One line, always current: the "click a
    /// gold beacon" advice only appears where beacons exist.</summary>
    private void RefreshHint()
    {
        if (_hintLabel == null) return;
        bool city = _atlas3D?.CityMode ?? false;
        bool home = _atlas3D?.ActiveCityIsHome ?? true;
        _hintLabel.Text = !city
            ? "Click a gold beacon to deploy an expedition  ·  Q/E to rotate  ·  zoom into your city to return home."
            : home
                ? "Click a building to open it  ·  ⚒ Build raises new buildings  ·  Q/E to rotate  ·  ↑ To the World Map to send expeditions."
                : "Click a fogged district to scout it  ·  click a revealed marker to enter it.";
    }

    /// <summary>The "first steps" orientation card (2026-08-19): one screen that explains
    /// the hub loop. Auto-opens once per session on first home-city entry; reopenable any
    /// time from the "? How this works" button. Dim catcher blocks map input while open.</summary>
    private void ShowHelpCard()
    {
        if (_helpLayer != null) return;
        _helpLayer = new CanvasLayer { Name = "HubHelpLayer", Layer = 95 };   // above HUD (90), below pause (100)
        AddChild(_helpLayer);

        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.55f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.MouseFilter = Control.MouseFilterEnum.Stop;
        _helpLayer.AddChild(dim);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            CustomMinimumSize = new Vector2(680, 0),
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Gold));
        _helpLayer.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        var title = new Label { Text = "Your Guild Seat" };
        title.AddThemeFontSizeOverride("font_size", UITheme.CampusTitleFontSize);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(title);

        void HelpLine(string text)
        {
            var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            l.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            l.AddThemeColorOverride("font_color", UITheme.TextPrimary);
            vbox.AddChild(l);
        }

        HelpLine("•  Every building is a working room of the guild. Click one to open it.");
        HelpLine("•  The Grand Hall keeps the guild ledger and your save slots.");
        HelpLine("•  The Gatehouse Yard mounts expeditions.");
        HelpLine("•  ⚒ Build raises new buildings on open ground.");
        HelpLine("•  ＋ Annex a district buys a neighbouring tile when the guild outgrows its walls.");
        HelpLine("•  ↑ To the World Map ascends to the strategic view. Click a gold staging " +
                 "beacon there to deploy an expedition.");
        HelpLine("•  Each expedition costs one lunation of the calendar. When the calendar " +
                 "runs out, the cycle ends at the Grand Conjunction.");
        HelpLine("•  Camera: WASD or left-drag pans · wheel zooms · Q/E or middle-drag rotates.");

        var okBtn = new Button
        {
            Text = "Understood",
            CustomMinimumSize = new Vector2(200, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        okBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        UITheme.ApplyButtonStyle(okBtn, isPrimary: true);
        okBtn.Pressed += CloseHelpCard;
        vbox.AddChild(okBtn);
    }

    private void CloseHelpCard()
    {
        if (_helpLayer == null) return;
        _helpLayer.QueueFree();
        _helpLayer = null;
    }

    /// <summary>Open the visited capital's services menu (Phase 3). Gates atlas input while it's up
    /// (the menu owns the screen); one at a time.</summary>
    private void ShowCityServices()
    {
        if (_cityServices != null || (_atlas3D?.ActiveCityIsHome ?? true)) return;
        if (_atlas3D != null) _atlas3D.AcceptInput = false;
        _cityServices = CityServicesHost.Create(
            _atlas3D?.ActiveCityName ?? "", _atlas3D?.ActiveCity, HideCityServices);
        AddChild(_cityServices);
    }

    /// <summary>The services menu closed: drop our reference and re-enable atlas input, staying in
    /// the CITY view (the player leaves explicitly via "To the World Map", and can reopen services
    /// with the City Services button). The host frees itself. NOT leaving here is deliberate: leaving
    /// dropped the player onto the world map at the capital's staging tile, which popped the deploy
    /// window right after closing the menu.</summary>
    private void HideCityServices()
    {
        if (_cityServices == null) return;
        _cityServices = null;
        if (_atlas3D != null) _atlas3D.AcceptInput = true;
    }

    // ── Phase 3 explore: district content dispatch ────────────────────────

    /// <summary>A revealed district's content was clicked in a visited NPC city. Route by type:
    /// Service reopens the capital's services menu; Event runs a narrative encounter over the city;
    /// Fight and Story are stubbed (their real routing, combat + story beats, is the next
    /// increment). Non-service content is marked cleared once resolved.</summary>
    private void OnDistrictContentTriggered(CityDistrictEntry entry, WorldSettlement city)
    {
        if (entry == null) return;
        switch ((DistrictContentType)entry.Content)
        {
            case DistrictContentType.Service:
                ShowCityServices();   // reopenable, never cleared
                break;
            case DistrictContentType.Event:
                TriggerCityEvent(entry, city);
                break;
            case DistrictContentType.Fight:
                LaunchDistrictFight(entry, city);
                break;
            case DistrictContentType.Story:
                TriggerCityStory(entry, city);
                break;
        }
    }

    /// <summary>Run a narrative EVENT for a district: pick from the city's region pool (generic
    /// pool always included) and show it on the city-hosted panel. On completion, apply the choice's
    /// gold/flag outcomes to the guild save (mirrors the campus, non-expedition path) and clear the
    /// district. Empty pool → a small gold cache so the click never dead-ends.</summary>
    private void TriggerCityEvent(CityDistrictEntry entry, WorldSettlement city)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) { ClearDistrict(entry); return; }

        var pool = NarrativeEncounterLoader.LoadForRegion(city?.KingdomId ?? "");
        var enc = NarrativeEncounterLoader.PickRandom(pool, "", save.CompletedEvents, save);
        if (enc == null)
        {
            int gold = 15 + (int)(GD.Randf() * 20f);
            save.Gold += gold;
            EnsureCityExploreToasts();
            _cityExploreToasts?.Push($"A hidden cache in the district. (+{gold} gold)", QuestToastKind.Complete);
            ClearDistrict(entry);
            return;
        }

        EnsureCityNarrativePanel();
        if (_atlas3D != null) _atlas3D.AcceptInput = false;   // panel owns the screen
        _cityNarrativePanel.Visible = true;
        _cityNarrativePanel.ShowEncounter(enc, save.HasFlag,
            save.Cycle?.SelectedSchool, save.Gold, save.Cycle?.Campaign,
            hasItem: id => save.Armory.OwnedItems.Exists(i => i.DefinitionId == id),
            hasCompanion: id => CompanionRoster.GetActiveParty().Exists(c => c.Id == id));
        _cityNarrativePanel.OnCompleted = choice => OnCityEventCompleted(enc, choice, entry, city);
    }

    /// <summary>Apply a city event's chosen outcome to the guild save (gold, flags, meta-flags,
    /// completed-event marker), then clear the district. HP/steps deltas don't apply outside an
    /// expedition; item/companion rewards are deferred to a later increment.</summary>
    private void OnCityEventCompleted(NarrativeEncounterData enc, EncounterChoice choice,
        CityDistrictEntry entry, WorldSettlement city)
    {
        if (_cityNarrativePanel != null) _cityNarrativePanel.Visible = false;
        if (_atlas3D != null) _atlas3D.AcceptInput = true;
        ApplyNarrativeOutcome(enc, choice);
        ClearDistrict(entry);
        // Phase 3 contracts: a resolved district event advances "aid" contracts.
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle != null && city != null)
            PushContractToasts(CityContractService.NoteAid(cycle, city.KingdomId));
    }

    /// <summary>Run a STORY beat for a district: pick from the authored city-vignette
    /// pool (Data/Encounters/city_stories.json, one-shot ids and flag-gated chains) and
    /// show it on the same city-hosted panel the Events use. Outcomes route through
    /// ApplyNarrativeOutcome like any narrative choice. Exhausted pool → a closing
    /// line rather than a dead click, and the district clears.</summary>
    private void TriggerCityStory(CityDistrictEntry entry, WorldSettlement city)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) { ClearDistrict(entry); return; }

        var pool = NarrativeEncounterLoader.LoadCityStories();
        var enc = NarrativeEncounterLoader.PickRandom(pool, "", save.CompletedEvents, save);
        if (enc == null)
        {
            EnsureCityExploreToasts();
            _cityExploreToasts?.Push("The quarter's stories have all found their endings, for this cycle.",
                                     QuestToastKind.Progress);
            ClearDistrict(entry);
            return;
        }

        EnsureCityNarrativePanel();
        if (_atlas3D != null) _atlas3D.AcceptInput = false;   // panel owns the screen
        _cityNarrativePanel.Visible = true;
        _cityNarrativePanel.ShowEncounter(enc, save.HasFlag,
            save.Cycle?.SelectedSchool, save.Gold, save.Cycle?.Campaign,
            hasItem: id => save.Armory.OwnedItems.Exists(i => i.DefinitionId == id),
            hasCompanion: id => CompanionRoster.GetActiveParty().Exists(c => c.Id == id));
        // Story beats are not "aid": no contract credit. Otherwise the same
        // completion path as an Event (apply outcome, clear the district).
        _cityNarrativePanel.OnCompleted = choice =>
        {
            if (_cityNarrativePanel != null) _cityNarrativePanel.Visible = false;
            if (_atlas3D != null) _atlas3D.AcceptInput = true;
            ApplyNarrativeOutcome(enc, choice);
            ClearDistrict(entry);
        };
    }

    /// <summary>Launch a district FIGHT (Phase 3 explore): compose an encounter from
    /// the owning kingdom's region pool at the kingdom's difficulty, record which
    /// district is being fought in the cycle save (survives the scene swap), and
    /// round-trip through the combat scene via the proven strategic pattern
    /// (OpenAnchorhold / CampusScreen.LaunchCampusCombat): return override → carrier
    /// → scene change. ConsumeDistrictFightReturn picks it up on the way back.</summary>
    private void LaunchDistrictFight(CityDistrictEntry entry, WorldSettlement city)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (entry == null || city == null || cycle == null || _world == null)
            return;

        // Composition source: the kingdom's template region pool, at the same
        // difficulty an expedition fight there would roll (DifficultyMultAt's math:
        // region mult × kingdom-tier factor × timeline threat). Seats field Battle
        // weight; ordinary settlements Skirmish.
        string regionId = "";
        float mult = 1.0f;
        if (_kingdoms != null && !string.IsNullOrEmpty(city.KingdomId)
            && _kingdoms.TryGetValue(city.KingdomId, out var ks))
        {
            regionId = string.IsNullOrEmpty(ks.TemplateRegionId) ? ks.RegionId : ks.TemplateRegionId;
            float regionMult = RegionLoader.LoadOrDefault(ks.TemplateRegionId)?.EnemyDifficultyMult ?? 1.0f;
            float tierFactor = ks.Tier switch
            {
                <= 1 => 1.0f,
                2 => 1.25f,
                _ => 1.5f,   // tier 3+
            };
            mult = regionMult * tierFactor * CampaignEscalation.CombatDifficultyMult(cycle);
        }
        var tier = city.IsSeat ? EncounterTier.Battle : EncounterTier.Skirmish;
        string terrain = _world.InBounds(city.CenterX, city.CenterY)
            ? _world.GetTile(city.CenterX, city.CenterY).Terrain.ToString()
            : "Plains";

        var def = EncounterPoolLoader.Pick(regionId, tier, terrain, mult);
        if (def == null || def.Enemies.Count == 0)
        {
            // Never dead-end the click: no resolvable roster → the enclave scatters.
            int gold = 20 + (int)(GD.Randf() * 20f);
            var s = SaveManager.ActiveSave;
            if (s != null) s.Gold += gold;
            EnsureCityExploreToasts();
            _cityExploreToasts?.Push($"The enclave scatters before you arrive. (+{gold} gold)",
                                     QuestToastKind.Complete);
            ClearDistrict(entry);
            PushContractToasts(CityContractService.NotePurge(cycle, city.KingdomId));
            return;
        }

        if (EncounterRouter.Instance == null)
            GetTree().Root.AddChild(new EncounterRouter { Name = "EncounterRouter" });
        var router = EncounterRouter.Instance;
        if (router == null)
            return;

        cycle.PendingCityFightCityId = CityExploreService.CityId(city);
        cycle.PendingCityFightDq = entry.Dq;
        cycle.PendingCityFightDr = entry.Dr;
        SaveManager.MarkDirty();

        router.HasPendingReturn = false;
        router.SavedCombatWasPatrolAmbush = false;
        router.SavedCombatPatrolArchmageId = "";
        router.SavedCombatGuardianKey = "";
        router.SavedCombatArchmageId = "";
        router.SavedResolutionArchmageId = "";
        router.ReturnSceneOverride = StrategicScenePath;
        router.SetCurrentTier(def.Tier);

        SaveManager.SaveIfDirty();
        EncounterContextCarrier.Set(def);
        EncounterContextCarrier.SetContext(def.TerrainType, def.Tier);
        SceneTransition.Go(GetTree(), router.CombatScenePath, "To Arms",
            string.IsNullOrEmpty(def.DisplayName) ? "The district is contested." : def.DisplayName);
    }

    /// <summary>Pick up a returning district fight (Phase 3 explore). Keyed on the
    /// cycle's pending-city-fight record AND the router's return override being this
    /// scene. A stale pending record with no router return (mid-combat reload) is
    /// dropped and the district stays live. Victory banks gold/splinters, rolls the
    /// Q4.4 combat loot faucet at the kingdom's territory tier, and clears the
    /// district; either way the player lands back inside the fought city
    /// (BuildAtlas3D consumes _reenterNpcCity) with an outcome toast.</summary>
    // ══════════════════════════════════════════════════════════════════
    // Castle defence (Expedition v2): the fight a kingdom assault owes
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Soldiers are in the camp and the player has to answer for it.
    /// The assault's strategic cost landed when it happened (CastleThreats), so
    /// this is not "do you want to be attacked", it is "do you stand or do you
    /// burn the camp and go". Withdrawing is a real option with a real price,
    /// which is what keeps a losing fight from being mandatory.</summary>
    private void OfferPendingCastleDefense()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId)
            || cycle.CastleDefenseLaunched)
        {
            return;
        }

        string kingdomId = cycle.PendingCastleAssaultKingdomId;
        string who = FactionDisplay(kingdomId);

        var dialog = new ConfirmationDialog
        {
            Title = "The camp is attacked",
            OkButtonText = "Hold the camp",
            CancelButtonText = "Withdraw",
            DialogText =
                $"Soldiers of {who} are among the tents and the waystone is still open.\n\n" +
                "Hold the camp: the crew forms up at the Heart and you fight for the work " +
                "already done. Winning puts the crews back on it.\n\n" +
                "Withdraw: the castle breaks camp under fire. Nothing is lost that is not " +
                "already lost, but the resupply slips another lunation.",
        };
        dialog.Confirmed += () => { dialog.QueueFree(); LaunchCastleDefense(kingdomId); };

        // Withdrawing costs a lunation, so it hangs off the BUTTON rather than
        // the Canceled signal. Escape and the close box also emit Canceled, and
        // a stray keypress must not spend a lunation. Dismissing therefore just
        // defers: the assault stays owed and is offered again on the next map
        // load, which is the harmless reading of "the player did not answer".
        dialog.GetCancelButton().Pressed += () => WithdrawFromCastleAssault(kingdomId);
        dialog.Canceled += () => dialog.QueueFree();

        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>Break camp under fire. The assault already took its pound of
    /// flesh; this adds the cost of leaving work half done.</summary>
    private void WithdrawFromCastleAssault(string kingdomId)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            return;
        }
        cycle.PendingCastleAssaultKingdomId = "";
        cycle.CastleDefenseLaunched = false;
        cycle.CastleRepairLunations = Mathf.Min(
            cycle.CastleRepairLunations + 1, CastleThreats.MaxTotalRepairLunations);
        cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
        cycle.PendingSiegeReports.Add(
            $"The camp is struck under fire and {FactionDisplay(kingdomId)} is left holding the ground. " +
            $"The resupply stands at {cycle.CastleRepairLunations} lunation(s).");
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        // The report rides in PendingSiegeReports and is read by the HUD on the
        // next build. Not forcing one here: BuildHud is not written to be
        // re-entered and this fires from inside a dialog callback.
    }

    /// <summary>Send the player into the castle defence. Reuses the shipped
    /// ambush fight wholesale: CastleDefenseCompiler builds the map for this
    /// terrain and the installed modules, attaches the protect objective with the
    /// Castle Heart, and the wizard arrives by waystone. Nothing here is a second
    /// defence system, which was the whole point of routing through it.</summary>
    private void LaunchCastleDefense(string kingdomId)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || _world == null)
        {
            return;
        }

        if (EncounterRouter.Instance == null)
            GetTree().Root.AddChild(new EncounterRouter { Name = "EncounterRouter" });
        var router = EncounterRouter.Instance;
        if (router == null)
        {
            return;
        }

        string terrainType = _world.InBounds(cycle.CastleX, cycle.CastleY)
            ? _world.GetTile(cycle.CastleX, cycle.CastleY).Terrain.ToString()
            : OverworldHex.TerrainType.Grassland.ToString();

        string regionId = "frontier_wilds";
        if (_kingdoms != null && _kingdoms.TryGetValue(kingdomId, out var ks)
            && !string.IsNullOrEmpty(ks.RegionId))
        {
            regionId = ks.RegionId;
        }

        // A kingdom sends soldiers, not opportunists, so this is a Siege-tier
        // pull rather than the Ambush pool a wandering patrol draws from.
        var def = EncounterPoolLoader.Pick(regionId, EncounterTier.Siege, terrainType,
                                           CampaignEscalation.CombatDifficultyMult(cycle));
        if (def == null)
        {
            GD.PrintErr("[CastleDefense] No encounter could be built; the assault is written off.");
            WithdrawFromCastleAssault(kingdomId);
            return;
        }

        CastleDefenseCompiler.Arm(def, terrainType,
            (ulong)(cycle.CastleX * 7919 + cycle.CastleY * 104729 + 31));

        cycle.CastleDefenseLaunched = true;
        SaveManager.MarkDirty();

        router.HasPendingReturn = false;
        router.SavedCombatWasPatrolAmbush = true;   // the defence presentation
        router.SavedCombatPatrolArchmageId = "";
        router.SavedCombatGuardianKey = "";
        router.SavedCombatArchmageId = "";
        router.SavedResolutionArchmageId = "";
        router.ReturnSceneOverride = StrategicScenePath;
        router.SetCurrentTier(def.Tier);

        SaveManager.SaveIfDirty();
        EncounterContextCarrier.Set(def);
        EncounterContextCarrier.SetContext(def.TerrainType, def.Tier);
        SceneTransition.Go(GetTree(), router.CombatScenePath, "To Arms",
            $"{FactionDisplay(kingdomId)} is in the camp.");
    }

    /// <summary>Pick up a returning castle defence. Same discipline as the
    /// district fight: keyed on the cycle's in-progress marker AND the router's
    /// return override being this scene, so a mid-combat reload leaves the fight
    /// owed rather than silently resolving it.</summary>
    private void ConsumeCastleDefenseReturn(CycleState cycle)
    {
        if (cycle == null || !cycle.CastleDefenseLaunched)
        {
            return;
        }

        var router = EncounterRouter.Instance;
        if (router == null || !router.HasPendingReturn ||
            router.ReturnSceneOverride != StrategicScenePath)
        {
            return;   // the fight never resolved; it stays owed
        }

        bool won = router.CombatWon;
        router.HasPendingReturn = false;
        router.ReturnSceneOverride = "";

        string who = FactionDisplay(cycle.PendingCastleAssaultKingdomId);
        cycle.PendingCastleAssaultKingdomId = "";
        cycle.CastleDefenseLaunched = false;
        cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();

        if (won)
        {
            // The crews go straight back to work, so the setback the assault
            // bought is handed back. Floored at one: the fight still cost a beat.
            int before = cycle.CastleRepairLunations;
            cycle.CastleRepairLunations = Mathf.Max(
                1, cycle.CastleRepairLunations - CastleThreats.AssaultRepairSetback);
            cycle.PendingSiegeReports.Add(
                $"The Heart holds. {who} is thrown out of the camp and the crews are back on the " +
                $"waystone: the resupply falls from {before} to {cycle.CastleRepairLunations} lunation(s).");
        }
        else
        {
            cycle.CastleRepairLunations = Mathf.Min(
                cycle.CastleRepairLunations + CastleThreats.AssaultRepairSetback,
                CastleThreats.MaxTotalRepairLunations);
            cycle.PendingSiegeReports.Add(
                $"The camp is overrun. {who} burns what the crews had rebuilt: the resupply stands " +
                $"at {cycle.CastleRepairLunations} lunation(s).");
        }

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
    }

    private void ConsumeDistrictFightReturn(CycleState cycle)
    {
        if (cycle == null || string.IsNullOrEmpty(cycle.PendingCityFightCityId))
            return;

        string cityId = cycle.PendingCityFightCityId;
        var district = new Vector2I(cycle.PendingCityFightDq, cycle.PendingCityFightDr);
        cycle.PendingCityFightCityId = "";
        cycle.PendingCityFightDq = 0;
        cycle.PendingCityFightDr = 0;
        SaveManager.MarkDirty();

        var router = EncounterRouter.Instance;
        if (router == null || !router.HasPendingReturn ||
            router.ReturnSceneOverride != StrategicScenePath)
            return;   // stale record: the fight never resolved, district stays live

        bool won = router.CombatWon;
        router.HasPendingReturn = false;
        router.ReturnSceneOverride = "";

        var save = SaveManager.ActiveSave;
        if (save == null)
            return;

        // Resolve the city record (CityId is stable within the cycle).
        WorldSettlement city = null;
        if (_world?.Settlements != null)
            foreach (var s in _world.Settlements)
                if (CityExploreService.CityId(s) == cityId) { city = s; break; }

        if (won)
        {
            save.Gold += router.GoldReward;
            save.ArcaneSplinters += router.SplinterReward;

            // Q4.4 loot faucet, tiered by the owning kingdom (expedition parity).
            // City ground is not corrupted, so no blight roll.
            int terrTier = 1;
            if (city != null && _kingdoms != null && !string.IsNullOrEmpty(city.KingdomId)
                && _kingdoms.TryGetValue(city.KingdomId, out var ks))
                terrTier = ks.Tier;
            int items = 0;
            foreach (var lootDef in CombatLootTable.Roll(terrTier, router.CurrentTier))
            {
                save.Armory.AddItem(ItemInstance.FromDefinition(lootDef));
                items++;
            }

            var st = CityExploreService.Get(cycle, cityId);
            var entry = st != null ? CityExploreService.FindDistrict(st, district) : null;
            if (entry != null) entry.Cleared = true;

            _reenterToast = $"The enclave is broken. (+{router.GoldReward} gold, +{router.SplinterReward} splinters"
                          + (items > 0 ? $", {items} item{(items == 1 ? "" : "s")})" : ")");
            _reenterToastKind = QuestToastKind.Complete;

            // Phase 3 contracts: an enclave defeated advances "purge" contracts in
            // the owning kingdom; completions toast after the landing (below).
            if (city != null)
                _reenterContractToasts = CityContractService.NotePurge(cycle, city.KingdomId);
        }
        else
        {
            _reenterToast = "Driven back. The enclave holds its district.";
            _reenterToastKind = QuestToastKind.Progress;
        }

        _reenterNpcCity = city;   // BuildAtlas3D lands back in the city (null-safe there)
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
    }

    /// <summary>The non-expedition narrative outcome, shared by city district
    /// events and floated campus panels (2026-08-13 CampusScreen extraction,
    /// session one). Mirrors CampusScreen.OnCampusNarrativeCompleted's save
    /// mutations, including the item/companion/reputation/lore/arc verbs the
    /// city path had deferred. HP/steps don't apply off-expedition;
    /// ResolutionKind/LaunchGuardian encounters must NOT route here (the
    /// Council panel stays on the campus overlay until session two).</summary>
    private void ApplyNarrativeOutcome(NarrativeEncounterData enc, EncounterChoice choice)
    {
        var save = SaveManager.ActiveSave;
        if (choice == null || save == null) return;

        if (choice.GoldDelta != 0)
            save.Gold = Mathf.Max(0, save.Gold + choice.GoldDelta);

        if (enc != null && !string.IsNullOrEmpty(enc.Id)
            && !save.CompletedEvents.Contains(enc.Id))
            save.CompletedEvents.Add(enc.Id);

        if (choice.SetFlags != null)
            foreach (var flag in choice.SetFlags)
                if (!string.IsNullOrEmpty(flag)) save.SetFlag(flag);

        if (choice.SetMetaFlags != null && save.Ledger != null)
            foreach (var flag in choice.SetMetaFlags)
                if (!string.IsNullOrEmpty(flag) && !save.Ledger.MetaNarrativeFlags.Contains(flag))
                    save.Ledger.MetaNarrativeFlags.Add(flag);

        if (!string.IsNullOrEmpty(choice.ItemReward))
        {
            var idef = ItemDatabase.Get(choice.ItemReward);
            if (idef != null) save.Armory.AddItem(idef);
        }

        if (!string.IsNullOrEmpty(choice.CompanionUnlock))
            CompanionRoster.GrantFromEncounter(choice.CompanionUnlock);

        if (!string.IsNullOrEmpty(choice.ReputationFactionId) && choice.ReputationAmount != 0)
        {
            save.FactionReputation.TryGetValue(choice.ReputationFactionId, out int cur);
            save.FactionReputation[choice.ReputationFactionId] = cur + choice.ReputationAmount;
        }

        if (!string.IsNullOrEmpty(choice.LoreId) &&
            !save.UnlockedLoreEntries.Contains(choice.LoreId))
            save.UnlockedLoreEntries.Add(choice.LoreId);

        var arcStatus = CompanionArcTracker.TryCompleteByEncounter(enc?.Id, save);
        if (arcStatus != null)
        {
            EnsureCityExploreToasts();
            _cityExploreToasts?.Push(arcStatus.IsComplete
                ? $"{arcStatus.CompanionName}: \"{arcStatus.ArcName}\" complete."
                : $"{arcStatus.CompanionName}: \"{arcStatus.ArcName}\" advances ({arcStatus.CurrentStage}/{arcStatus.TotalStages}).",
                QuestToastKind.Progress);
        }

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
    }

    /// <summary>Session-one extraction: a floated campus panel's narrative
    /// (today: the Quests panel) hosts on the city narrative layer, outcome
    /// through the shared applier, then the panel refreshes.</summary>
    private void ShowFloatedPanelNarrative(NarrativeEncounterData enc,
        System.Action onApplied = null)
    {
        var save = SaveManager.ActiveSave;
        if (enc == null || save == null) return;
        EnsureCityNarrativePanel();
        if (_atlas3D != null) _atlas3D.AcceptInput = false;
        _cityNarrativePanel.Visible = true;
        _cityNarrativePanel.ShowEncounter(enc, save.HasFlag,
            save.Cycle?.SelectedSchool, save.Gold, save.Cycle?.Campaign,
            hasItem: id => save.Armory.OwnedItems.Exists(i => i.DefinitionId == id),
            hasCompanion: id => CompanionRoster.GetActiveParty().Exists(c => c.Id == id));
        _cityNarrativePanel.OnCompleted = choice =>
        {
            if (_cityNarrativePanel != null) _cityNarrativePanel.Visible = false;
            if (_atlas3D != null) _atlas3D.AcceptInput = true;
            ApplyNarrativeOutcome(enc, choice);
            _floatingPanel?.RefreshHostedPanel();
            onApplied?.Invoke();
        };
    }

    /// <summary>Mark a district's content consumed, refresh the atlas markers, and persist.</summary>
    /// <summary>Phase 3 contracts: a district was scouted, so advance any accepted
    /// "scout" contracts in the owning kingdom and toast completions.</summary>
    private void OnDistrictScouted(WorldSettlement city)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || city == null) return;
        PushContractToasts(CityContractService.NoteScout(cycle, city.KingdomId));
    }

    /// <summary>Toast contract-completion lines from a CityContractService.Note* call.</summary>
    private void PushContractToasts(System.Collections.Generic.List<string> lines)
    {
        if (lines == null || lines.Count == 0) return;
        EnsureCityExploreToasts();
        foreach (var line in lines)
            _cityExploreToasts?.Push(line, QuestToastKind.Complete);
    }

    private void ClearDistrict(CityDistrictEntry entry)
    {
        if (entry != null) entry.Cleared = true;
        _atlas3D?.RefreshCityContentMarkers();
        SaveManager.Save();
    }

    /// <summary>Lazily build the city-explore host layer (narrative panel + toasts), above the atlas.</summary>
    private void EnsureCityExploreLayer()
    {
        if (_cityExploreLayer != null) return;
        _cityExploreLayer = new CanvasLayer { Name = "CityExploreLayer", Layer = 45 };
        AddChild(_cityExploreLayer);
    }

    private void EnsureCityNarrativePanel()
    {
        if (_cityNarrativePanel != null) return;
        EnsureCityExploreLayer();
        _cityNarrativePanel = new NarrativeEncounterPanel { Name = "CityEventPanel", Visible = false };
        _cityExploreLayer.AddChild(_cityNarrativePanel);
    }

    private void EnsureCityExploreToasts()
    {
        if (_cityExploreToasts != null) return;
        EnsureCityExploreLayer();
        _cityExploreToasts = new ToastManager { Name = "CityExploreToasts" };
        _cityExploreLayer.AddChild(_cityExploreToasts);
    }

    /// <summary>The "Annex a district" toggle (city view only). While pressed, the atlas shows the
    /// annexable preview flowers and a click buys one; unpressed hides them. Sits just under the
    /// leave-to-world button.</summary>
    // EnsureAnnexButton / EnsureCityLeaveButton / EnsureCityServicesButton removed
    // (2026-08-19): the three city-chrome buttons are built with the HUD's right-docked
    // stack in BuildHud, so they layout WITH the calendar instead of over it.

    /// <summary>An annexable district was clicked in annex mode. Confirm the placeholder gold
    /// spend, then unlock the district, persist, and rebuild the city around the new tile.</summary>
    private void OnHomeDistrictPicked(Vector2I district)
    {
        var save = SaveManager.ActiveSave;
        var map = save?.Ledger?.CampusMap;
        if (save == null || map == null) return;

        var dialog = new ConfirmationDialog
        {
            Title = "Annex district",
            DialogText = save.Gold >= DistrictAnnexCost
                ? $"Annex this district for {DistrictAnnexCost} gold?\nTreasury: {save.Gold} gold."
                : $"Not enough gold to annex: costs {DistrictAnnexCost}, treasury has {save.Gold}.",
        };
        // Only allow the buy when affordable; otherwise the dialog is informational.
        dialog.GetOkButton().Disabled = save.Gold < DistrictAnnexCost;
        dialog.Confirmed += () =>
        {
            if (save.Gold < DistrictAnnexCost) return;
            save.Gold -= DistrictAnnexCost;
            map.UnlockDistrict(district.X, district.Y);
            SaveManager.Save();
            _atlas3D?.RefreshCityGrowth();          // rebuild + snap back into city (clears annex mode)
            if (_annexButton != null) _annexButton.ButtonPressed = false;
        };
        dialog.Canceled += () => dialog.QueueFree();
        dialog.Confirmed += () => dialog.QueueFree();
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>One button in the right-docked HUD stack. Visibility is state-driven
    /// (OnCityModeChanged), so every button initializes from the CURRENT city state;
    /// the HUD can be (re)built before or after city mode engages.</summary>
    private Button MakeCityChromeButton(string text, bool primary, bool toggle = false)
    {
        var btn = new Button
        {
            Text = text,
            ToggleMode = toggle,
            CustomMinimumSize = new Vector2(230, 34),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        UITheme.ApplyButtonStyle(btn, isPrimary: primary);
        _rightHudStack.AddChild(btn);
        return btn;
    }

    public override void _ExitTree()
    {
        // Never leave a dangling overlay-leave callback pointing at a freed view, and if
        // we're torn down while the overlay was up (e.g. a new-cycle scene swap from
        // inside it), clear the flag so the next scene's HUD isn't wrongly hidden.
        // (City mode no longer touches the flag; the HUD stays visible in the city.)
        if (CampusScreen.OverlayLeaveHandler == HideCampusOverlay)
            CampusScreen.OverlayLeaveHandler = null;
        if (_campusOverlay != null)
            PlayerSession.CampusOverlayOpen = false;
    }

    /// <summary>Persistent strategic-map HUD: a free exit back to campus. Returning
    /// costs nothing: the world, discoveries, and staging points already live in
    /// the saved cycle, so leaving and reopening the map changes nothing.</summary>
    private void BuildHud()
    {
        _hud?.QueueFree();
        _hud = new CanvasLayer { Name = "StrategicHud" };
        AddChild(_hud);

        // Return-to-Campus and Council now live on the global top bar
        // (HudManager); removed from this per-screen HUD to avoid duplicate
        // buttons on the strategic map. The top bar's Return-to-Campus is gated
        // to this scene, so exit-to-campus availability is unchanged.

        // ── Right-docked stack (2026-08-19): city buttons + calendar + news ──
        // One VBox under the global top bar so nothing overlaps by offset math.
        // Order: [To the World Map] [Annex] [City Services] → calendar → news.
        _rightHudStack = new VBoxContainer
        {
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.End,
            OffsetLeft = -260,
            OffsetRight = -16,
            OffsetTop = 8 + HudManager.BarHeight,   // clear the global top bar
        };
        _rightHudStack.AddThemeConstantOverride("separation", 8);
        _hud.AddChild(_rightHudStack);

        bool cityNow = _atlas3D?.CityMode ?? false;
        bool homeNow = _atlas3D?.ActiveCityIsHome ?? true;
        _annexButton = MakeCityChromeButton("＋  Annex a district", primary: false, toggle: true);
        _annexButton.Visible = cityNow && homeNow;
        _annexButton.Toggled += pressed =>
        {
            _atlas3D?.SetAnnexMode(pressed);
            // One ground-click mode at a time: arming annex disarms build.
            if (pressed && _buildModeBtn != null) _buildModeBtn.ButtonPressed = false;
        };
        _buildModeBtn = MakeCityChromeButton("⚒  Build", primary: false, toggle: true);
        _buildModeBtn.Visible = cityNow && homeNow;
        _buildModeBtn.Toggled += OnBuildModeToggled;
        _cityServicesBtn = MakeCityChromeButton("City Services", primary: true);
        _cityServicesBtn.Visible = cityNow && !homeNow;
        _cityServicesBtn.Pressed += ShowCityServices;
        // Expedition v2: the March control was unusable as first built. It only
        // appeared on the deploy drawer for an EXISTING staging point, and a fresh
        // cycle has exactly one, the tile the castle is already standing on. So
        // there was nowhere to march to. Repositioning the fortress is precisely
        // about going somewhere that is NOT already a launch point, so marching
        // targets any charted ground: arm this, then click the tile.
        _marchModeBtn = MakeCityChromeButton("\u2691  March", primary: false, toggle: true);
        // Visibility is NOT set here. RefreshPieceChrome owns it and re-decides
        // it on every city-mode change; setting it once from `cityNow` at build
        // time is exactly the bug that kept this button hidden all session.
        _marchModeBtn.Toggled += OnMarchModeToggled;

        // Sortie (2026-09-24): the castle is a piece and launches from where it
        // stands, exactly as the party takes the field where it stands. This
        // replaces the staging-point drawer as the way to launch the castle;
        // the drawer's readouts moved into the confirm this opens.
        _sortieBtn = MakeCityChromeButton("\u25B6  Sortie", primary: true);
        _sortieBtn.Visible = false;
        _sortieBtn.Pressed += OnSortiePressed;

        // The two-state piece toggle that used to live here is gone. It was in
        // the RIGHT-docked stack, which the deploy drawer covers entirely, and it
        // could only ever name two forces. Replaced by the FORCES roster on the
        // left (BuildForcesPanel), which lists every piece and can name any
        // number of parties.

        // Take the field, from wherever the party is standing.
        //
        // NOT on the deploy drawer any more. The drawer belongs to a STAGING
        // POINT, and tying the party's expedition to one was the last piece of
        // the model where the party could only act at anchors. It is its own
        // piece: it takes the field where it is.
        _partyDeployBtn = MakeCityChromeButton("\u25B6  Take the field", primary: true);
        _partyDeployBtn.Visible = false;
        _partyDeployBtn.Pressed += OnPartyTakeTheField;

        // Bank the furnace: a frozen castle makes camp where it stands without
        // the stone being aimed at it. See ExpeditionAnchors.BankFrozenCastle.
        _bankFurnaceBtn = MakeCityChromeButton("\u2302  Bank the furnace", primary: false);
        _bankFurnaceBtn.Visible = false;
        _bankFurnaceBtn.Pressed += OnBankFurnacePressed;

        // Field work (skeleton). Stop is real; the debug starter stands in for
        // the zones until they exist.
        _stopWorkBtn = MakeCityChromeButton("\u25A0  Stop work", primary: false);
        _stopWorkBtn.Visible = false;
        _stopWorkBtn.Pressed += OnStopWorkPressed;

        // Field Party v1 (2026-09-24, docs/field_party_task_force_spec_v1).
        // The debug "Survey here" stand-in is retired: postings are real now.
        // Visibility of all four lives in RefreshPieceChrome, never here.
        _postBtn = MakeCityChromeButton("\u2691  Post here", primary: false);
        _postBtn.Visible = false;
        _postBtn.Pressed += OnPostPressed;
        _collectBtn = MakeCityChromeButton("\u21A9  Collect detachment", primary: false);
        _collectBtn.Visible = false;
        _collectBtn.Pressed += OnCollectPressed;
        _recallBtn = MakeCityChromeButton("\u21A9  Recall through the waystone", primary: false);
        _recallBtn.Visible = false;
        _recallBtn.Pressed += OnRecallPressed;
        _interveneBtn = MakeCityChromeButton("\u2694  Intervene", primary: true);
        _interveneBtn.Visible = false;
        _interveneBtn.Pressed += OnInterveneHerePressed;
        _audienceBtn = MakeCityChromeButton("\u2696  Seek audience", primary: false);
        _audienceBtn.Visible = false;
        _audienceBtn.Pressed += OnSeekAudiencePressed;
        // The muster board is never gated: "where is everyone" is a question
        // with an answer in every mode, city view included.
        _musterBtn = MakeCityChromeButton("\u2637  Forces", primary: false);
        _musterBtn.Pressed += OpenForcesScreen;

        _helpBtn = MakeCityChromeButton("?  How this works", primary: false);
        _helpBtn.Pressed += ShowHelpCard;   // visible in every mode; orientation is never gated

        // Stack order (2026-09-24): the selected piece's verbs first, then the
        // city's own three, then the fixed footer. Moved rather than rebuilt
        // in place, so each button's construction stays beside its handler.
        foreach (var tail in new Button[] { _annexButton, _buildModeBtn, _cityServicesBtn, _musterBtn, _helpBtn })
        {
            if (tail != null && tail.GetParent() == _rightHudStack)
            {
                _rightHudStack.MoveChild(tail, -1);
            }
        }

        // The chrome is built AFTER BuildAtlas3D, so the RefreshPieceChrome call
        // in there ran with every one of these buttons still null and could only
        // set the map. Run it again now that the controls exist, or the selector
        // opens reading "Castle" by luck rather than by state and the Take the
        // field button never appears.
        RefreshPieceChrome();

        // ── Calendar readout: the doomsday clock, top-right ──────────────
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle != null)
        {
            var cal = cycle.Calendar;
            var calPanel = new PanelContainer();
            calPanel.AddThemeStyleboxOverride("panel",
                UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Gold));
            _rightHudStack.AddChild(calPanel);

            var calMargin = new MarginContainer();
            calMargin.AddThemeConstantOverride("margin_left", 14);
            calMargin.AddThemeConstantOverride("margin_right", 14);
            calMargin.AddThemeConstantOverride("margin_top", 8);
            calMargin.AddThemeConstantOverride("margin_bottom", 8);
            calPanel.AddChild(calMargin);

            var calVbox = new VBoxContainer();
            calVbox.AddThemeConstantOverride("separation", 2);
            calMargin.AddChild(calVbox);

            // Continue-campaign legibility (progression doc §9): which year of this
            // timeline, and how hard the world has grown, so the player can read the
            // escalation and time the bank before a push turns unwinnable.
            int campaignYear = cycle.CampaignYear;
            if (campaignYear > 1)
            {
                int foePct = Mathf.RoundToInt(
                    cycle.SeasonalThreatLevel * CampaignEscalation.ThreatDifficultyStep * 100f);
                var yearLbl = new Label
                {
                    Text = $"⚠ Year {campaignYear}  ·  the world hardens (+{foePct}% foes)",
                };
                yearLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
                yearLbl.AddThemeColorOverride("font_color", UITheme.Danger);
                calVbox.AddChild(yearLbl);
            }
            else
            {
                var yearLbl = new Label { Text = "Year 1  ·  a pristine timeline" };
                yearLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
                yearLbl.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.55f));
                calVbox.AddChild(yearLbl);
            }

            var phaseLbl = new Label
            {
                Text = $"Lunation {cal.CurrentLunation} / {cal.LunationsPerCycle}  ·  {cal.CurrentMoonName}"
                     + $"  ·  day {cal.DayOfLunation + 1}",
            };
            phaseLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize);
            phaseLbl.AddThemeColorOverride("font_color", UITheme.Gold);
            calVbox.AddChild(phaseLbl);

            int lunationsLeft = cal.LunationsRemaining;
            var remainLbl = new Label
            {
                Text = lunationsLeft <= 2
                    ? $"⚠ {lunationsLeft} lunation(s) until the Conjunction"
                    : $"{lunationsLeft} lunations until the Conjunction",
            };
            remainLbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            remainLbl.AddThemeColorOverride("font_color",
                lunationsLeft <= 2 ? UITheme.Danger : new Color(1f, 1f, 1f, 0.6f));
            calVbox.AddChild(remainLbl);
        }

        // ── Word from the frontier: this-lunation siege outcomes ─────────
        // KingdomTickSimulation queued these on the tick; surface them so a
        // fallen province is not just a silent recolour on the map. Cleared at
        // the top of the next Deploy (see Deploy()).
        var siegeReports = cycle?.PendingSiegeReports;
        if (siegeReports != null && siegeReports.Count > 0)
        {
            var newsPanel = new PanelContainer();
            newsPanel.AddThemeStyleboxOverride("panel",
                UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Danger));
            _rightHudStack.AddChild(newsPanel);

            var newsMargin = new MarginContainer();
            newsMargin.AddThemeConstantOverride("margin_left", 14);
            newsMargin.AddThemeConstantOverride("margin_right", 14);
            newsMargin.AddThemeConstantOverride("margin_top", 8);
            newsMargin.AddThemeConstantOverride("margin_bottom", 8);
            newsPanel.AddChild(newsMargin);

            var newsVbox = new VBoxContainer();
            newsVbox.AddThemeConstantOverride("separation", 3);
            newsMargin.AddChild(newsVbox);

            var newsTitle = new Label { Text = "Word from the frontier" };
            newsTitle.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
            newsTitle.AddThemeColorOverride("font_color", UITheme.Danger);
            newsVbox.AddChild(newsTitle);

            // Show the most recent handful so a heavy lunation can't overflow.
            int shown = 0;
            for (int i = siegeReports.Count - 1; i >= 0 && shown < 5; i--, shown++)
            {
                var line = new Label
                {
                    Text = siegeReports[i],
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                line.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
                line.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.85f, 0.95f));
                newsVbox.AddChild(line);
            }
            if (siegeReports.Count > 5)
            {
                var more = new Label { Text = $"…and {siegeReports.Count - 5} more." };
                more.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
                more.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.5f));
                newsVbox.AddChild(more);
            }
        }

        // A short legend so the player knows what they're looking at. Context-sensitive
        // (2026-08-19): the fixed "gold beacon" line read as broken advice once the city
        // became the landing view; RefreshHint swaps the text per mode.
        _hintLabel = new Label
        {
            AnchorLeft = 0.5f,
            AnchorTop = 1f,
            AnchorRight = 0.5f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Both,
            OffsetTop = -34,
            OffsetBottom = -10,
        };
        _hintLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        _hintLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.5f));
        _hintLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _hud.AddChild(_hintLabel);
        RefreshHint();

        BuildLensButtons();
        // The archmage standings strip was moved into CouncilScreen (user
        // ruling 2026-07-22). The strategic map stays clean; open the Council
        // from the top bar for standings.
    }

    // ── Map lens toggles ─────────────────────────────────────────────────
    private readonly System.Collections.Generic.List<Button> _lensButtons = new();

    private void BuildLensButtons()
    {
        _lensButtons.Clear();

        // Directly under the global top bar, top-left, WRAPPED in a raised panel
        // (2026-08-19): the bare row's text and inactive buttons faded into whatever
        // terrain happened to sit behind them.
        var lensPanel = new PanelContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 16,
            OffsetTop = 8 + HudManager.BarHeight,   // clear the global top bar
        };
        lensPanel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.CampusTitleBarBorder));
        _hud.AddChild(lensPanel);

        var lensMargin = new MarginContainer();
        lensMargin.AddThemeConstantOverride("margin_left", 10);
        lensMargin.AddThemeConstantOverride("margin_right", 10);
        lensMargin.AddThemeConstantOverride("margin_top", 6);
        lensMargin.AddThemeConstantOverride("margin_bottom", 6);
        lensPanel.AddChild(lensMargin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        lensMargin.AddChild(row);

        var lbl = new Label { Text = "View:" };
        lbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        lbl.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.6f));
        lbl.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(lbl);

        AddLensButton(row, "Political", StrategicLens.Political);
        AddLensButton(row, "Terrain", StrategicLens.Terrain);
        AddLensButton(row, "Corruption", StrategicLens.Corruption);
        AddLensButton(row, "Reach", StrategicLens.Reach);

        UpdateLensButtons();

        // The forces roster docks directly beneath this row. Built here so the
        // two left-hand panels are laid out together and their offsets cannot
        // drift apart in separate methods.
        BuildForcesPanel();
    }

    private void AddLensButton(HBoxContainer row, string text, StrategicLens lens)
    {
        var btn = new Button { Text = text, ToggleMode = true };
        btn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        UITheme.ApplyButtonStyle(btn, isPrimary: false);
        btn.Pressed += () => SetLens(lens);
        btn.SetMeta("lens", (int)lens);
        row.AddChild(btn);
        _lensButtons.Add(btn);
    }

    private void UpdateLensButtons()
    {
        foreach (var btn in _lensButtons)
        {
            if (!IsInstanceValid(btn))
                continue;
            bool active = (int)btn.GetMeta("lens") == (int)_lens;
            btn.ButtonPressed = active;
            btn.Modulate = active ? Colors.White : new Color(1f, 1f, 1f, 0.55f);
        }
    }

    private void BuildTileLayer()
    {
        _tileLayer?.QueueFree();

        var quad = MakeQuadMesh(TilePx);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            // GL Compatibility can collapse per-instance COLOR when only one of
            // UseColors / UseCustomData is set. Enabling both keeps the color
            // buffer live on the Compatibility renderer.
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = _world.Width * _world.Height,
        };

        for (int y = 0; y < _world.Height; y++)
        {
            for (int x = 0; x < _world.Width; x++)
            {
                int i = y * _world.Width + x;
                mm.SetInstanceTransform2D(i,
                    new Transform2D(0f, HexCoord.OffsetRenderPosition(x, y, TilePx)));
                mm.SetInstanceColor(i, TileColor(_world.Tiles[i]));
                mm.SetInstanceCustomData(i, Colors.White); // keep custom-data buffer non-zero
            }
        }

        _tileLayer = new MultiMeshInstance2D { Name = "TileLayer", Multimesh = mm };
        AddChild(_tileLayer);
    }

    private void BuildPoiLayer()
    {
        _poiLayer?.QueueFree();
        _poiInstanceOfPoi.Clear();

        // Count discovered POIs first (MultiMesh needs a fixed instance count).
        // Debug reveal shows every POI regardless of discovery.
        var visible = new List<int>();
        for (int i = 0; i < _world.Pois.Count; i++)
            if (_debugReveal || _world.Pois[i].Discovered)
                visible.Add(i);

        // Markers are diamonds, a bit larger than a tile so they read when zoomed out.
        var marker = MakeDiamondMesh(TilePx * 1.4f);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = true,
            Mesh = marker,
            InstanceCount = Mathf.Max(1, visible.Count),
        };

        if (visible.Count == 0)
        {
            // Keep a valid (invisible) instance so the MultiMesh is well-formed.
            mm.SetInstanceTransform2D(0, new Transform2D(0f, new Vector2(-9999, -9999)));
            mm.SetInstanceColor(0, new Color(0, 0, 0, 0));
            mm.SetInstanceCustomData(0, Colors.White);
        }
        else
        {
            for (int n = 0; n < visible.Count; n++)
            {
                int poiIndex = visible[n];
                var poi = _world.Pois[poiIndex];
                Vector2 pos = HexCoord.OffsetRenderPosition(poi.X, poi.Y, TilePx)
                              + new Vector2(TilePx * 0.5f, TilePx * 0.5f);
                mm.SetInstanceTransform2D(n, new Transform2D(0f, pos));
                mm.SetInstanceColor(n, PoiColor(poi.Kind));
                mm.SetInstanceCustomData(n, Colors.White);
                _poiInstanceOfPoi[poiIndex] = n;
            }
        }

        _poiLayer = new MultiMeshInstance2D { Name = "PoiLayer", Multimesh = mm };
        _poiLayer.ZIndex = 1;
        AddChild(_poiLayer);
    }

    /// <summary>River/road overlay for the strategic zoom. Each tile draws a half-
    /// segment from its centre toward each river/road edge's shared boundary; the two
    /// tiles' halves meet, tracing the network as a route (a center-path simplification;
    /// the window draws true hex edges). Respects fog. Rivers blue, roads tan; a road
    /// over a river draws second, reading as a crossing.</summary>
    private void BuildEdgeLayer()
    {
        _edgeLayer?.QueueFree();
        _edgeLayer = new Node2D { Name = "EdgeLayer" };   // over settlement tint, under POIs (z=1)
        AddChild(_edgeLayer);
        if (_world == null)
            return;

        float half = TilePx * 0.5f;
        var center = new Vector2(half, half);
        float riverW = Mathf.Max(1f, TilePx * 0.20f);
        float roadW = Mathf.Max(1f, TilePx * 0.13f);
        float springW = Mathf.Max(1f, TilePx * 0.11f);

        for (int y = 0; y < _world.Height; y++)
        {
            for (int x = 0; x < _world.Width; x++)
            {
                var t = _world.GetTile(x, y);
                if (t.RiverEdges == 0 && t.RoadEdges == 0 && t.SpringEdges == 0)
                    continue;
                if (t.IsWater)
                    continue;   // never originate a line in water; kills the ocean overshoot

                var disc = _debugReveal ? TileDiscovery.Explored : t.Discovery;
                if (disc == TileDiscovery.Unseen)
                    continue;

                Vector2 c = HexCoord.OffsetRenderPosition(x, y, TilePx) + center;
                var (q, r) = HexCoord.OffsetToAxial(x, y);

                for (int d = 0; d < 6; d++)
                {
                    bool spring = (t.SpringEdges & (1 << d)) != 0;
                    bool river = (t.RiverEdges & (1 << d)) != 0;
                    bool road = (t.RoadEdges & (1 << d)) != 0;
                    if (!spring && !river && !road)
                        continue;

                    var (dq, dr) = HexCoord.AxialDirections[d];
                    var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                    if (!_world.InBounds(nc, nr))
                        continue;

                    Vector2 nCenter = HexCoord.OffsetRenderPosition(nc, nr, TilePx) + center;
                    Vector2 dir = nCenter - c;
                    float dist = dir.Length();
                    // Clamp to half a tile so the segment stays inside this tile's
                    // footprint; two tiles' halves still meet near the shared edge.
                    Vector2 end = c + dir / dist * Mathf.Min(dist * 0.5f, TilePx * 0.5f);

                    if (spring && !river)
                        AddEdgeSegment(c, end, springW, UITheme.TerrainLake);   // thin, lighter blue
                    if (river)
                        AddEdgeSegment(c, end, riverW, UITheme.TerrainWater);
                    if (road)
                        AddEdgeSegment(c, end, roadW, UITheme.TerrainRoad);
                }
            }
        }
    }

    /// <summary>Kingdom boundaries, Political lens only. Instead of stroking edges
    /// (which exposes the hex stairstep and drifts on the square-quad renderer), this
    /// TINTS the boundary tiles (a tile whose neighbour is a different kingdom, ocean,
    /// or off-map) with a dark band, using the SAME quad transform as the tile layer
    /// so it lands exactly on grid. Mirrors BuildSettlementLayer's rim technique.</summary>
    private void BuildBorderLayer()
    {
        _borderLayer?.QueueFree();
        if (_world == null || _lens != StrategicLens.Political)
        {
            _borderLayer = new Node2D { Name = "BorderLayer" }; // empty placeholder so the ref is valid
            AddChild(_borderLayer);
            return;
        }

        var rim = new List<(int x, int y)>();
        for (int y = 0; y < _world.Height; y++)
        {
            for (int x = 0; x < _world.Width; x++)
            {
                var t = _world.GetTile(x, y);
                if (!t.IsLand || string.IsNullOrEmpty(t.KingdomId))
                    continue;

                var disc = _debugReveal ? TileDiscovery.Explored : t.Discovery;
                if (disc == TileDiscovery.Unseen)
                    continue;

                var (q, r) = HexCoord.OffsetToAxial(x, y);
                bool onBorder = false;
                for (int d = 0; d < 6; d++)
                {
                    var (dq, dr) = HexCoord.AxialDirections[d];
                    var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                    if (!_world.InBounds(nc, nr))
                    { onBorder = true; break; }
                    var nt = _world.GetTile(nc, nr);
                    if (nt.IsWater || nt.KingdomId != t.KingdomId)
                    { onBorder = true; break; }
                }
                if (onBorder)
                    rim.Add((x, y));
            }
        }

        if (rim.Count == 0)
        {
            _borderLayer = new Node2D { Name = "BorderLayer" };
            AddChild(_borderLayer);
            return;
        }

        var quad = MakeQuadMesh(TilePx);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = rim.Count,
        };
        for (int n = 0; n < rim.Count; n++)
        {
            var (x, y) = rim[n];
            mm.SetInstanceTransform2D(n,
                new Transform2D(0f, HexCoord.OffsetRenderPosition(x, y, TilePx)));
            mm.SetInstanceColor(n, UITheme.KingdomBorder);
            mm.SetInstanceCustomData(n, Colors.White);
        }

        _borderLayer = new MultiMeshInstance2D
        {
            Name = "BorderLayer",
            Multimesh = mm,
            ZIndex = 0, // above tiles, below POIs; same band as settlement rim
        };
        AddChild(_borderLayer);
    }

    private void AddEdgeSegment(Vector2 a, Vector2 b, float width, Color color)
    {
        _edgeLayer.AddChild(new Line2D
        {
            Points = new[] { a, b },
            Width = width,
            DefaultColor = color,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
        });
    }

    /// <summary>Tints the boundary tiles of each settlement (a one-tile rim) so a
    /// city/town's extent reads without hiding the terrain underneath. A tile is on
    /// the rim if any hex neighbour belongs to a different settlement (or none), or
    /// if it sits on the map edge. Respects fog: Unseen tiles aren't rimmed. Cities
    /// gold, towns bronze. Aligns to the tile layer exactly (same transform/mesh).</summary>
    private void BuildSettlementLayer()
    {
        _settlementLayer?.QueueFree();
        if (_world == null || _world.Settlements.Count == 0)
            return;

        var fill = new List<(int x, int y, SettlementTier tier)>();
        for (int i = 0; i < _world.Settlements.Count; i++)
        {
            var s = _world.Settlements[i];
            foreach (var (tx, ty) in s.Tiles)
            {
                var disc = _debugReveal ? TileDiscovery.Explored : _world.GetTile(tx, ty).Discovery;
                if (disc == TileDiscovery.Unseen)
                    continue;
                fill.Add((tx, ty, s.Tier));
            }
        }
        if (fill.Count == 0)
            return;

        var quad = MakeQuadMesh(TilePx);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = fill.Count,
        };
        for (int n = 0; n < fill.Count; n++)
        {
            var (x, y, tier) = fill[n];
            mm.SetInstanceTransform2D(n,
                new Transform2D(0f, HexCoord.OffsetRenderPosition(x, y, TilePx)));
            mm.SetInstanceColor(n, tier == SettlementTier.City
                ? UITheme.SettlementCityBorder
                : UITheme.SettlementTownBorder);
            mm.SetInstanceCustomData(n, Colors.White);
        }

        _settlementLayer = new MultiMeshInstance2D
        {
            Name = "SettlementLayer",
            Multimesh = mm,
            ZIndex = 0,   // above tiles (added later in tree), below POIs (z=1)
        };
        AddChild(_settlementLayer);
    }

    /// <summary>Per-kingdom name labels, anchored at each kingdom's seat (capital).
    /// Political lens only; the ruler line is zoom-gated so the far-out view stays
    /// readable. Drawn above POIs with a dark backing pill for legibility over the
    /// faction wash.</summary>
    private void BuildLabelLayer()
    {
        _labelLayer?.QueueFree();
        _labelLayer = new Node2D { Name = "LabelLayer", ZIndex = 3 }; // above POIs (z=1), staging (z=2)
        AddChild(_labelLayer);

        if (_world == null || _lens != StrategicLens.Political)
            return;

        bool showRuler = _zoom >= ArchmageNameZoomThreshold;

        // Anchor each kingdom's label at its Seat POI (the capital/seat city centre).
        foreach (var poi in _world.Pois)
        {
            if (poi.Kind != PoiKind.Seat && poi.Kind != PoiKind.Convergence)
                continue;

            var disc = _debugReveal ? TileDiscovery.Explored : _world.GetTile(poi.X, poi.Y).Discovery;
            if (disc == TileDiscovery.Unseen)
                continue; // don't name kingdoms the player hasn't found

            if (string.IsNullOrEmpty(poi.KingdomId) ||
                !_kingdoms.TryGetValue(poi.KingdomId, out var ks))
                continue;

            string place = string.IsNullOrEmpty(ks.DisplayName) ? poi.KingdomId : ks.DisplayName;
            string ruler = null;
            if (showRuler && !string.IsNullOrEmpty(ks.ArchmageId))
                ruler = ArchmageRegistry.Get(ks.ArchmageId)?.DisplayName;

            Vector2 at = HexCoord.OffsetRenderPosition(poi.X, poi.Y, TilePx)
                         + new Vector2(TilePx * 0.5f, TilePx * 0.5f);
            AddKingdomLabel(at, place, ruler, ominous: poi.Kind == PoiKind.Convergence);
        }
    }

    private void AddKingdomLabel(Vector2 center, string place, string ruler, bool ominous = false)
    {
        float inv = _zoom > 0.001f ? 1f / _zoom : 1f;

        var holder = new Node2D
        {
            Position = center,
            Scale = new Vector2(inv, inv),
        };
        _labelLayer.AddChild(holder);

        var plate = new PanelContainer();
        var plateStyle = UITheme.MakePanelStyle(
            new Color(UITheme.BgBase.R, UITheme.BgBase.G, UITheme.BgBase.B, 0.78f),
            ominous ? UITheme.StrategicCorruption : UITheme.Violet);
        plateStyle.ContentMarginLeft = plateStyle.ContentMarginRight = 8;
        plateStyle.ContentMarginTop = plateStyle.ContentMarginBottom = 3;
        plate.AddThemeStyleboxOverride("panel", plateStyle);
        holder.AddChild(plate);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        plate.AddChild(vbox);

        var nameLbl = new Label
        {
            Text = Spaced(place.ToUpperInvariant()),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.StrategicLabelFontSize);
        nameLbl.AddThemeColorOverride("font_color",
            ominous ? new Color(0.95f, 0.55f, 0.62f) : UITheme.Gold);
        nameLbl.AddThemeColorOverride("font_outline_color", UITheme.WorldDeep);
        nameLbl.AddThemeConstantOverride("outline_size", 4);
        vbox.AddChild(nameLbl);

        if (!string.IsNullOrEmpty(ruler))
        {
            var rulerLbl = new Label
            {
                Text = ruler,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            rulerLbl.AddThemeFontSizeOverride("font_size", UITheme.StrategicLabelFontSize - 5);
            rulerLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            rulerLbl.AddThemeColorOverride("font_outline_color", UITheme.WorldDeep);
            rulerLbl.AddThemeConstantOverride("outline_size", 3);
            vbox.AddChild(rulerLbl);
        }

        CallDeferred(nameof(RecenterLabelPlate), plate);
    }

    /// <summary>Re-position a label plate so it's horizontally centred on, and sitting
    /// just above, its holder's origin (the seat). Deferred because a Control's size
    /// isn't known until after it lays out.</summary>
    private void RecenterLabelPlate(PanelContainer plate)
    {
        if (!IsInstanceValid(plate))
            return;
        Vector2 size = plate.Size;
        // Centre horizontally; lift the plate up so its bottom edge clears the seat
        // diamond (which is ~TilePx*1.4 tall, drawn at the anchor).
        plate.Position = new Vector2(-size.X * 0.5f, -size.Y - TilePx * 1.1f);
    }

    /// <summary>Insert thin spaces between characters for a letter-spaced, map-label
    /// feel (Godot Labels have no native tracking control).</summary>
    private static string Spaced(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        // U+2009 THIN SPACE between each character; a hair of tracking, not a full gap.
        return string.Join("\u2009", s.ToCharArray());
    }

    // ── Color logic ──────────────────────────────────────────────────────
    private Color TileColor(WorldTile t)
    {
        // Debug full-map reveal: treat every tile as Explored for DISPLAY only;
        // the saved discovery state is never touched. Lets corruption + the whole
        // world be inspected during testing.
        var discovery = _debugReveal ? TileDiscovery.Explored : t.Discovery;

        // Discovery first: unexplored is void (all lenses respect fog).
        if (discovery == TileDiscovery.Unseen)
            return UITheme.StrategicUnseen;

        // Charted-but-unexplored: dim hint of the active lens's read.
        if (discovery == TileDiscovery.Charted)
        {
            Color hint = LensBaseColor(t);
            return hint.Lerp(UITheme.StrategicCharted, 0.55f);
        }

        // Explored: the active lens decides how the tile reads.
        return LensColor(t);
    }

    /// <summary>The fully-saturated color for a tile under the active lens
    /// (explored tiles). Each lens answers a different question about the tile.</summary>
    private Color LensColor(WorldTile t)
    {
        switch (_lens)
        {
            case StrategicLens.Terrain:
                return TerrainLensColor(t);
            case StrategicLens.Corruption:
                return CorruptionLensColor(t);
            case StrategicLens.Reach:
                return ReachLensColor(t);
            default:
                return PoliticalLensColor(t);
        }
    }

    /// <summary>The base (un-dimmed) color used for the charted-tile hint, per lens.</summary>
    private Color LensBaseColor(WorldTile t)
    {
        switch (_lens)
        {
            case StrategicLens.Terrain:
                return TerrainColorOf(t);
            case StrategicLens.Corruption:
                return CorruptionLensColor(t);
            case StrategicLens.Reach:
                return ReachLensColor(t);
            default:
                bool ownedLand = t.IsLand && !string.IsNullOrEmpty(t.KingdomId);
                return ownedLand ? KingdomColor(t.KingdomId) : TerrainColorOf(t);
        }
    }

    // ── Political lens (default): faction control + terrain luminance + corruption wash ──
    private Color PoliticalLensColor(WorldTile t)
    {
        bool isLand = t.IsLand;
        Color c;
        if (isLand && !string.IsNullOrEmpty(t.KingdomId))
        {
            Color bloc = KingdomColor(t.KingdomId);
            float lum = TerrainLuminance(t.Terrain);
            c = new Color(
                Mathf.Clamp(bloc.R * lum, 0f, 1f),
                Mathf.Clamp(bloc.G * lum, 0f, 1f),
                Mathf.Clamp(bloc.B * lum, 0f, 1f),
                1f);
        }
        else
        {
            c = TerrainColorOf(t);
        }
        if (t.Corruption > 0)
        {
            // Political lens: corruption is a STAIN over the kingdom color, not a
            // recolor: the territory's identity must survive underneath. Capped low
            // and darkened (vs the loud Corruption-lens red) so a heavily corrupted
            // kingdom reads as "this kingdom, corrupted," not "the red kingdom."
            float k = Mathf.Clamp(t.Corruption / 100f, 0f, 1f) * 0.35f;
            c = c.Lerp(UITheme.StrategicCorruptionWash, k);
        }
        return c;
    }

    // ── Terrain lens: pure region terrain, no faction tint. Shows the
    //    per-region terrain identity (the whole point of terrain-per-region). ──
    private Color TerrainLensColor(WorldTile t) => TerrainColorOf(t);

    /// <summary>Terrain color for a whole tile, with ocean shaded shallow→deep by
    /// distance from shore instead of one flat blue. Use this wherever a tile's
    /// terrain color is wanted; TerrainColor(TerrainType) stays for type-only lookups.</summary>
    private static Color TerrainColorOf(WorldTile t)
        => t.Terrain == OverworldHex.TerrainType.Water
            ? UITheme.OceanColor(t.OceanDepth)
            : TerrainColor(t.Terrain);

    // ── Corruption lens: a heat map. Clean land reads cool/neutral, corruption
    //    ramps through warning to full corruption color. Makes the spread legible. ──
    private Color CorruptionLensColor(WorldTile t)
    {
        if (t.IsWater)
            return UITheme.TerrainWater.Darkened(0.3f);
        float k = Mathf.Clamp(t.Corruption / 100f, 0f, 1f);
        // Cool clean -> hot corrupted, via a two-stop ramp for readability.
        Color clean = new Color(0.18f, 0.26f, 0.22f);          // dim green-grey
        Color mid = new Color(0.65f, 0.45f, 0.15f);            // amber
        Color hot = UITheme.StrategicCorruption;               // full corruption
        return k < 0.5f
            ? clean.Lerp(mid, k / 0.5f)
            : mid.Lerp(hot, (k - 0.5f) / 0.5f);
    }

    // ── Reach lens: the guild's footprint. Each territory's STANDING colour
    //    (Hostile red -> Neutral slate -> Allied green) fills in from the void
    //    in proportion to PlayerInfluence (0-100), so reach you've built reads
    //    bright and reach you haven't stays dark. Secured staging points also
    //    render as gold beacons on top (BuildStagingMarkers). ──
    private Color ReachLensColor(WorldTile t)
    {
        if (t.IsWater)
            return UITheme.TerrainWater.Darkened(0.55f);

        int influence = 0;
        var stance = KingdomStance.Neutral;
        if (!string.IsNullOrEmpty(t.KingdomId))
        {
            if (_kingdoms != null && _kingdoms.TryGetValue(t.KingdomId, out var ks))
                influence = Mathf.Clamp(ks.PlayerInfluence, 0, 100);
            var cyc = SaveManager.ActiveSave?.Cycle;
            if (cyc != null)
                stance = CouncilQueries.StanceFor(cyc, t.KingdomId);
        }

        Color voidDim = new Color(0.10f, 0.10f, 0.13f);
        float f = influence / 100f;
        return voidDim.Lerp(StanceColor(stance), f);
    }

    /// <summary>Standing -> colour ramp for the Reach lens.</summary>
    private static Color StanceColor(KingdomStance s) => s switch
    {
        KingdomStance.Hostile    => new Color(0.75f, 0.24f, 0.22f),
        KingdomStance.Unfriendly => new Color(0.80f, 0.46f, 0.22f),
        KingdomStance.Neutral    => new Color(0.48f, 0.50f, 0.55f),
        KingdomStance.Friendly   => new Color(0.28f, 0.62f, 0.60f),
        KingdomStance.Allied     => new Color(0.30f, 0.72f, 0.40f),
        _                        => new Color(0.48f, 0.50f, 0.55f),
    };

    private static Color TerrainColor(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Grassland => UITheme.TerrainGrassland,
        OverworldHex.TerrainType.Forest => UITheme.TerrainForest,
        OverworldHex.TerrainType.Road => UITheme.TerrainRoad,
        OverworldHex.TerrainType.Ruins => UITheme.TerrainRuins,
        OverworldHex.TerrainType.Mountain => UITheme.TerrainMountain,
        OverworldHex.TerrainType.Swamp => UITheme.TerrainSwamp,
        OverworldHex.TerrainType.ArcaneGround => UITheme.TerrainArcaneGround,
        OverworldHex.TerrainType.Volcanic => UITheme.TerrainVolcanic,
        OverworldHex.TerrainType.Water => UITheme.TerrainWater,
        OverworldHex.TerrainType.Hills => UITheme.TerrainHills,
        OverworldHex.TerrainType.Coast => UITheme.TerrainCoast,
        OverworldHex.TerrainType.Lake => UITheme.TerrainLake,
        OverworldHex.TerrainType.Desert => UITheme.TerrainDesert,
        OverworldHex.TerrainType.Tundra => UITheme.TerrainTundra,
        OverworldHex.TerrainType.Snow => UITheme.TerrainSnow,
        OverworldHex.TerrainType.Marsh => UITheme.TerrainMarsh,
        _ => UITheme.Neutral,
    };

    /// <summary>Brightness multiplier per terrain so terrain reads as texture
    /// WITHIN a faction-colored territory without overriding the faction hue.</summary>
    private static float TerrainLuminance(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Grassland => 1.10f,
        OverworldHex.TerrainType.Road => 1.15f,
        OverworldHex.TerrainType.ArcaneGround => 1.05f,
        OverworldHex.TerrainType.Ruins => 0.95f,
        OverworldHex.TerrainType.Forest => 0.78f,
        OverworldHex.TerrainType.Swamp => 0.72f,
        OverworldHex.TerrainType.Mountain => 0.88f,
        OverworldHex.TerrainType.Volcanic => 0.85f,
        OverworldHex.TerrainType.Hills => 0.95f,
        OverworldHex.TerrainType.Coast => 1.12f,
        OverworldHex.TerrainType.Desert => 0.80f,
        OverworldHex.TerrainType.Tundra => 0.62f,
        OverworldHex.TerrainType.Snow => 0.95f,
        OverworldHex.TerrainType.Marsh => 0.40f,
        _ => 1.0f,
    };

    /// <summary>Resolve a kingdom id to its controlling faction's color.
    /// Tiles store a KINGDOM id (e.g. "kingdom_3"), not a faction id, so we
    /// look up the kingdom's ControllingFactionId first. Falls back to a
    /// per-kingdom distinct hue if the kingdom or faction is missing, so the
    /// map never collapses to one color even with incomplete data.</summary>
    private Color FactionColorForKingdom(string kingdomId)
    {
        if (_kingdoms != null && _kingdoms.TryGetValue(kingdomId, out var ks)
            && !string.IsNullOrEmpty(ks.ControllingFactionId))
        {
            return UITheme.FactionColor(ks.ControllingFactionId);
        }
        // Fallback: derive a stable distinct hue from the kingdom id so
        // unowned/seat territories still read as separate blocs.
        return FallbackHue(kingdomId);
    }

    private static Color FallbackHue(string id)
    {
        if (string.IsNullOrEmpty(id))
            return UITheme.Neutral;
        // Hash the id to a hue; fixed saturation/value for legibility.
        uint h = 2166136261u;
        foreach (char ch in id)
        { h ^= ch; h *= 16777619u; }
        float hue = (h % 360u) / 360f;
        return Color.FromHsv(hue, 0.45f, 0.70f);
    }

    private static Color PoiColor(PoiKind kind) => kind switch
    {
        PoiKind.Combat => UITheme.POICombat,
        PoiKind.Rest => UITheme.POIRest,
        PoiKind.Narrative => UITheme.POINarrative,
        PoiKind.Negotiation => UITheme.POINegotiation,
        PoiKind.Outpost => UITheme.POIOutpost,
        PoiKind.Seat => UITheme.Gold,          // archmage seats: gold
        PoiKind.Settlement => UITheme.ArcaneBlue,
        PoiKind.Convergence => UITheme.POIConvergence,
        PoiKind.SupplyCache => UITheme.Success,   // provisions read green; the
                                                  // marker layer carries the owner color
        _ => UITheme.TextPrimary,
    };

    /// <summary>A stable, visually distinct fill color per kingdom, keyed off the
    /// kingdom INDEX so the ten territories spread evenly around the hue wheel and
    /// adjacent ids never collide. This is the political-lens unit: one kingdom =
    /// one color = one bordered bloc, regardless of which faction controls it
    /// (faction is a separate layer; coloring by it merges distinct territories).</summary>
    private Color KingdomColor(string kingdomId)
    {
        if (string.IsNullOrEmpty(kingdomId))
            return UITheme.Neutral;

        int idx = -1;
        int us = kingdomId.LastIndexOf('_');
        if (us >= 0 && us + 1 < kingdomId.Length)
            int.TryParse(kingdomId.Substring(us + 1), out idx);
        if (idx < 0)
        {
            uint hsh = 2166136261u;
            foreach (char ch in kingdomId)
            { hsh ^= ch; hsh *= 16777619u; }
            idx = (int)(hsh % (uint)UITheme.KingdomPalette.Length);
        }

        return UITheme.KingdomPalette[idx % UITheme.KingdomPalette.Length];
    }

    // ── Live recolor (Phase 1c hooks) ────────────────────────────────────

    /// <summary>Recolor one tile after its discovery/corruption changed.</summary>
    public void MarkTileDirty(int x, int y)
    {
        if (_tileLayer?.Multimesh == null || !_world.InBounds(x, y))
            return;
        int i = y * _world.Width + x;
        _tileLayer.Multimesh.SetInstanceColor(i, TileColor(_world.Tiles[i]));
    }

    /// <summary>A POI just became discovered: rebuild the POI layer (its
    /// instance count changed). Cheap relative to the tile layer.</summary>
    public void RefreshPois() { BuildPoiLayer(); BuildShardZoneMarkers(); }

    /// <summary>Switch the active map lens and recolor every tile. Cheap: only
    /// rewrites instance colors, no rebuild.</summary>
    public void SetLens(StrategicLens lens)
    {
        if (_lens == lens)
            return;
        _lens = lens;
        // 3D mode: the lens is a recolor on WorldAtlas3D; the 2D layers don't exist,
        // so skip their recolor/border/label passes (they'd no-op or null-ref).
        if (_atlas3D != null)
        {
            _atlas3D.SetLens(lens);
            UpdateLensButtons();
            return;
        }
        RecolorAllTiles();
        BuildBorderLayer();
        BuildLabelLayer();
        UpdateLensButtons();
    }

    private void RecolorAllTiles()
    {
        if (_tileLayer?.Multimesh == null || _world == null)
            return;
        var mm = _tileLayer.Multimesh;
        for (int i = 0; i < _world.Tiles.Length; i++)
            mm.SetInstanceColor(i, TileColor(_world.Tiles[i]));
    }

    // ── Camera ───────────────────────────────────────────────────────────
    private void BuildCamera()
    {
        _camera = new Camera2D { Name = "StrategicCamera" };
        AddChild(_camera);
        _camera.CallDeferred("make_current");
    }

    private void FrameCamera()
    {
        if (_camera == null || _world == null)
            return;
        float w = _world.Width * TilePx;
        float h = _world.Height * TilePx;
        _camera.Position = new Vector2(w * 0.5f, h * 0.5f);

        // Fit the world to the viewport with a margin.
        var vp = GetViewportRect().Size;
        float fit = Mathf.Min(vp.X / w, vp.Y / h) * 0.9f;
        _zoom = Mathf.Clamp(fit, ZoomMin, ZoomMax);
        _camera.Zoom = new Vector2(_zoom, _zoom);
    }

    public override void _Process(double delta)
    {
        // The debug "reveal strategic map" checkbox lives in the Guild panel, which now floats over
        // this LIVE view (Phase 2), so toggling it must re-apply to the running atlas; otherwise
        // the reveal (terrain + enemy city markers/regions) only took effect on a scene reload.
        bool reveal = PlayerSession.DebugMode && PlayerSession.DebugRevealStrategicMap;
        if (reveal != _debugReveal)
        {
            _debugReveal = reveal;
            _atlas3D?.SetRevealAll(reveal);   // full Rebuild: re-reveals tiles, settlements, grey regions
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // Escape: up from the city (2026-09-24). Only when nothing modal is up,
        // so a dialog's own Escape still means "cancel that".
        if (e.IsActionPressed("ui_cancel") && _atlas3D != null && _atlas3D.CityMode
            && _deployUi == null && _warfrontUi == null && _supplyUi == null)
        {
            _atlas3D.LeaveCityMode();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_camera == null)
            return;

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                ApplyZoom(ZoomStep);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                ApplyZoom(1f / ZoomStep);
            else if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Middle)
            {
                _dragging = mb.Pressed;
                _dragLast = mb.Position;
            }
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            // Pan opposite the drag, scaled by zoom.
            _camera.Position -= (mm.Position - _dragLast) / _camera.Zoom;
            _dragLast = mm.Position;
        }
    }

    private void ApplyZoom(float factor)
    {
        _zoom = Mathf.Clamp(_zoom * factor, ZoomMin, ZoomMax);
        _camera.Zoom = new Vector2(_zoom, _zoom);
        BuildLabelLayer(); // re-evaluate the ruler-line zoom gate
    }

    // ── Meshes ───────────────────────────────────────────────────────────
    private static QuadMesh MakeQuadMesh(float size)
        => new QuadMesh { Size = new Vector2(size, size) };

    /// <summary>A small diamond (rotated square) for POI markers, built as an
    /// ArrayMesh so it reads distinctly from the square tiles.</summary>
    private static ArrayMesh MakeDiamondMesh(float size)
    {
        float h = size * 0.5f;
        var verts = new Vector3[]
        {
            new(0, -h, 0), new(h, 0, 0), new(-h, 0, 0),
            new(h, 0, 0), new(0, h, 0), new(-h, 0, 0),
        };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ── Standalone helper ────────────────────────────────────────────────
    private void RevealAll()
    {
        for (int i = 0; i < _world.Tiles.Length; i++)
            _world.Tiles[i].Discovery = TileDiscovery.Explored;
        foreach (var poi in _world.Pois)
            poi.Discovered = true;
    }

    // ── Standalone continent-style selector (debug only) ─────────────────

    /// <summary>(Re)generate the disposable standalone world from the current
    /// debug seed + style override. Never touches a save; Standalone only.</summary>
    private void GenerateStandaloneWorld()
    {
        var p = new WorldGenerator.Params { ContinentStyleOverride = _standaloneStyle };
        var g = WorldGenerator.Generate(_standaloneSeed, StandaloneSchool, p);
        _world = g.World;
        _kingdoms = g.Kingdoms;
        if (RevealAllForTesting)
            RevealAll();
    }

    /// <summary>Regenerate + repaint the data layers in place. Leaves the debug
    /// panel and camera node alone so the OptionButton selection is preserved.</summary>
    private void RegenerateStandalone()
    {
        GenerateStandaloneWorld();
        BuildTileLayer();
        BuildSettlementLayer();
        BuildBorderLayer();
        BuildEdgeLayer();
        BuildPoiLayer();
        FrameCamera();
        BuildLabelLayer();   // match BuildRender: framed zoom first, then labels
        UpdateDebugInfo();
    }

    private void BuildDebugControls()
    {
        _debugControls?.QueueFree();
        _debugControls = new CanvasLayer { Name = "StandaloneDebugControls" };
        AddChild(_debugControls);

        var panel = new PanelContainer
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetLeft = 16,
            OffsetTop = 16,
            OffsetRight = 300,
            OffsetBottom = 224,
        };
        panel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Violet));
        _debugControls.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(vbox);

        var title = new Label { Text = "Continent (debug)" };
        title.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(title);

        var opt = new OptionButton();
        opt.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        opt.AddItem("Seed-rolled", 0);
        opt.AddItem("Pangaea", 1);
        opt.AddItem("Continents", 2);
        opt.AddItem("Archipelago", 3);
        opt.Select(_standaloneStyle switch
        {
            ContinentStyle.Pangaea => 1,
            ContinentStyle.Continents => 2,
            ContinentStyle.Archipelago => 3,
            _ => 0,
        });
        opt.ItemSelected += OnStyleSelected;
        vbox.AddChild(opt);

        var rerollBtn = new Button { Text = "Reroll seed" };
        rerollBtn.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        UITheme.ApplyButtonStyle(rerollBtn, isPrimary: false);
        rerollBtn.Pressed += () =>
        {
            _standaloneSeed = (int)GD.Randi();
            RegenerateStandalone();
        };
        vbox.AddChild(rerollBtn);

        _debugInfoLabel = new Label();
        _debugInfoLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
        _debugInfoLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.6f));
        vbox.AddChild(_debugInfoLabel);

        // ── Lens toggles (debug parity with the real strategic HUD) ──────
        vbox.AddChild(new HSeparator());

        var viewLabel = new Label { Text = "View" };
        viewLabel.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 2);
        viewLabel.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(viewLabel);

        _lensButtons.Clear();
        var lensRow = new HBoxContainer();
        lensRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(lensRow);

        AddLensButton(lensRow, "Political", StrategicLens.Political);
        AddLensButton(lensRow, "Terrain", StrategicLens.Terrain);
        AddLensButton(lensRow, "Corruption", StrategicLens.Corruption);
        AddLensButton(lensRow, "Reach", StrategicLens.Reach);
        UpdateLensButtons();

        UpdateDebugInfo();
    }

    private void OnStyleSelected(long idx)
    {
        _standaloneStyle = idx switch
        {
            1 => ContinentStyle.Pangaea,
            2 => ContinentStyle.Continents,
            3 => ContinentStyle.Archipelago,
            _ => (ContinentStyle?)null,
        };
        RegenerateStandalone();
    }

    private void UpdateDebugInfo()
    {
        if (_debugInfoLabel == null || _world == null)
            return;
        string rolled = string.IsNullOrEmpty(_world.ContinentStyle) ? "?" : _world.ContinentStyle;
        _debugInfoLabel.Text = $"seed {_standaloneSeed} · {rolled}";
    }

    // ════════════════════════════════════════════════════════════════════
    // Staging-point deploy (real mode only)
    // ════════════════════════════════════════════════════════════════════

    private Node2D _stagingLayer;
    private Node2D _warfrontLayer;
    private CanvasLayer _deployUi;
    private const float SidebarWidth = 420f;   // deploy drawer width
    private CanvasLayer _warfrontUi;
    private CanvasLayer _hud;
    private StagingPoint _pendingStaging;

    /// <summary>One clickable marker per available staging point. Staging points
    /// are few, so a handful of Area2D markers is cheap (unlike per-tile nodes).</summary>
    /// <summary>P3: a distinct arcane beacon on each DISCOVERED shard sub-region,
    /// drawn at the vault centre. Not clickable: a vault is reached by expedition,
    /// not by deploy. Reads apart from staging (gold) and POIs (flat diamonds) via a
    /// violet octagon + arcane-blue diamond core; dims once the shard is collected.</summary>
    private void BuildShardZoneMarkers()
    {
        _shardZoneLayer?.QueueFree();
        _shardZoneLayer = new Node2D { Name = "ShardZoneMarkers", ZIndex = 2 };
        AddChild(_shardZoneLayer);

        if (_world?.ShardZones == null)
            return;

        foreach (var z in _world.ShardZones)
        {
            if (!_debugReveal && !z.Discovered)
                continue;

            var center = HexCoord.OffsetRenderPosition(z.CenterX, z.CenterY, TilePx)
                         + new Vector2(TilePx * 0.5f, TilePx * 0.5f);
            var marker = new Node2D { Position = center };

            var ring = new Polygon2D
            {
                Polygon = MakeRing(TilePx * 1.7f),
                Color = z.ShardCollected ? UITheme.VioletDark : UITheme.Violet,
            };
            marker.AddChild(ring);

            float c = TilePx * 0.9f;
            var core = new Polygon2D
            {
                Polygon = new[]
                {
                    new Vector2(0, -c), new Vector2(c, 0),
                    new Vector2(0, c), new Vector2(-c, 0),
                },
                Color = z.ShardCollected ? UITheme.TextSecondary : UITheme.ArcaneBlue,
            };
            marker.AddChild(core);

            _shardZoneLayer.AddChild(marker);
        }
    }

    private void BuildStagingMarkers()
    {
        _stagingLayer?.QueueFree();
        _stagingLayer = new Node2D { Name = "StagingMarkers", ZIndex = 2 };
        AddChild(_stagingLayer);

        foreach (var sp in _world.StagingPoints)
        {
            if (!sp.Available)
                continue;

            var center = HexCoord.OffsetRenderPosition(sp.X, sp.Y, TilePx)
                         + new Vector2(TilePx * 0.5f, TilePx * 0.5f);

            // Visual: a ringed beacon so it stands out from POI diamonds.
            var marker = new Node2D { Position = center };

            var ring = new Polygon2D
            {
                Polygon = MakeRing(TilePx * 1.6f),
                Color = UITheme.Gold,
            };
            marker.AddChild(ring);

            var core = new Polygon2D
            {
                Polygon = MakeRing(TilePx * 0.7f),
                Color = UITheme.TextPrimary,
            };
            marker.AddChild(core);

            // Clickable area sized to the ring.
            var area = new Area2D();
            var shape = new CollisionShape2D
            {
                Shape = new CircleShape2D { Radius = TilePx * 1.8f },
            };
            area.AddChild(shape);
            var captured = sp;
            area.InputEvent += (viewport, evt, idx) =>
            {
                if (evt is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    OnStagingClicked(captured);
            };
            marker.AddChild(area);

            _stagingLayer.AddChild(marker);
        }
    }

    private void OnStagingClicked(StagingPoint sp)
    {
        // 2026-09-24: a beacon opens the launch confirm ONLY when the castle
        // stands on it, which is the same thing the Sortie verb does. Every
        // other beacon is a place: see ShowPlaceCard.
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle != null && cycle.CastleX == sp.X && cycle.CastleY == sp.Y && CanSortie(cycle, out _))
        {
            OnSortiePressed();
            return;
        }
        ShowPlaceCard(sp);
    }

    /// <summary>Deploy-flow streamline (2026-08-21): clicking the Gatehouse goes
    /// STRAIGHT to the launch drawer. Fly out of the city, open deploy on the
    /// last-used staging point (Home Camp on a fresh cycle). The old chain
    /// (Expedition tab → "Open Strategic Map" → find beacon → click) collapses
    /// to one click plus an optional beacon retarget. Lifecycle moments the
    /// Expedition tab still owns (post-Conjunction school pick, a world not yet
    /// woven, no staging at all) fall back to the overlay as before.</summary>
    private void BeginDeployFlow()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (PlayerSession.CycleEndedByConjunction || cycle?.World == null ||
            cycle.World.Tiles.Length == 0 || _world == null)
        {
            ShowCampusOverlay(CampusPanelId.Expedition);
            return;
        }

        // 2026-09-24: the Gatehouse no longer opens a drawer on a remembered
        // staging point. The castle launches from where it stands, so the
        // Gatehouse does the one thing that is always right: up to the world
        // with the castle selected, its Sortie verb lit if it can go.
        _atlas3D?.LeaveCityMode();
        SelectPiece("");
        if (_world.InBounds(cycle.CastleX, cycle.CastleY))
        {
            _atlas3D?.FlyToTile(cycle.CastleX, cycle.CastleY, 30f, 0f);
        }
    }

    // ── Supply caches: markers + control/overseer/siege dialog ──────────────

    private Node2D _supplyLayer;
    private CanvasLayer _supplyUi;

    /// <summary>One marker per discovered supply cache: a square "crate" in the
    /// CONTROLLER's color (guild green, else the holding faction's color) with a
    /// yield tag, the at-a-glance "who is harvesting this, and how hard"
    /// indication. Clicking opens the cache dialog. An active siege additionally
    /// shows the standard red warfront marker on the same tile (Z above this).</summary>
    private void BuildSupplyMarkers()
    {
        _supplyLayer?.QueueFree();
        _supplyLayer = new Node2D { Name = "SupplyMarkers", ZIndex = 2 };
        AddChild(_supplyLayer);

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.World == null)
            return;

        for (int i = 0; i < _world.Pois.Count; i++)
        {
            var poi = _world.Pois[i];
            if (poi.Kind != PoiKind.SupplyCache || (!poi.Discovered && !_debugReveal))
                continue;

            string ctrl = SupplyCacheSystem.ControllerOf(poi);
            Color ctrlColor = CacheControllerColor(cycle, ctrl);

            var center = HexCoord.OffsetRenderPosition(poi.X, poi.Y, TilePx)
                         + new Vector2(TilePx * 0.5f, TilePx * 0.5f);
            var marker = new Node2D { Position = center };

            float c = TilePx * 1.1f;
            marker.AddChild(new Polygon2D
            {
                Polygon = new[]
                {
                    new Vector2(-c, -c), new Vector2(c, -c),
                    new Vector2(c, c), new Vector2(-c, c),
                },
                Color = ctrlColor,
            });
            float k = TilePx * 0.55f;
            marker.AddChild(new Polygon2D
            {
                Polygon = new[]
                {
                    new Vector2(-k, -k), new Vector2(k, -k),
                    new Vector2(k, k), new Vector2(-k, k),
                },
                Color = UITheme.BgDeep,
            });

            // Harvest tag: the per-lunation draw, in the controller's color;
            // overseer-boosted caches visibly pay more.
            var lbl = new Label
            {
                Text = $"+{SupplyCacheSystem.YieldOf(poi)}",
                Position = new Vector2(-TilePx * 0.9f, -TilePx * 3.0f),
            };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
            lbl.AddThemeColorOverride("font_color", ctrlColor);
            marker.AddChild(lbl);

            var area = new Area2D();
            area.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = TilePx * 1.5f } });
            int captured = i;
            area.InputEvent += (viewport, evt, idx) =>
            {
                if (evt is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    ShowSupplyCacheDialog(captured);
            };
            marker.AddChild(area);

            _supplyLayer.AddChild(marker);
        }
    }

    private Color CacheControllerColor(CycleState cycle, string controllerId)
    {
        if (controllerId == KingdomTickSimulation.GuildFactionId)
            return UITheme.Success;
        if (cycle.Kingdoms != null && cycle.Kingdoms.TryGetValue(controllerId, out var k)
            && !string.IsNullOrEmpty(k.ControllingFactionId))
            return UITheme.FactionColor(k.ControllingFactionId);
        return UITheme.Neutral;
    }

    /// <summary>The cache dialog: who harvests it, what it pays, who watches it,
    /// plus the contextual action (view the siege / manage the overseer / lay
    /// siege). Mirrors ShowWarfrontIntervene's panel idiom.</summary>
    private void ShowSupplyCacheDialog(int poiIndex)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.World == null || poiIndex < 0 || poiIndex >= _world.Pois.Count)
            return;
        var poi = _world.Pois[poiIndex];
        string ctrl = SupplyCacheSystem.ControllerOf(poi);
        bool guildOwned = ctrl == KingdomTickSimulation.GuildFactionId;
        var siege = SupplyCacheSystem.SiegeFor(cycle, poiIndex);

        _supplyUi?.QueueFree();
        _supplyUi = new CanvasLayer { Name = "SupplyCacheUI" };
        AddChild(_supplyUi);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.02f, 0.72f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _supplyUi.AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -280, OffsetRight = 280, OffsetTop = -200, OffsetBottom = 200,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgRaised,
            guildOwned ? UITheme.Success : UITheme.Gold));
        _supplyUi.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        var title = new Label
        {
            Text = $"Supply Cache: {SupplyCacheSystem.HostName(cycle, poi)}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        title.AddThemeColorOverride("font_color", guildOwned ? UITheme.Success : UITheme.Gold);
        vbox.AddChild(title);
        vbox.AddChild(new HSeparator());

        AddDeployStat(vbox, "Harvested by",
            SupplyCacheSystem.ControllerDisplay(cycle, ctrl) + (guildOwned ? " (you)" : ""));
        AddDeployStat(vbox, "Yield", $"+{SupplyCacheSystem.YieldOf(poi)} supplies / lunation");
        if (!guildOwned && cycle.Kingdoms.TryGetValue(ctrl, out var ck))
            AddDeployStat(vbox, "Their stock", $"{ck.SupplyStock} / 100");
        if (guildOwned)
        {
            var ov = cycle.Companions?.Find(x => x.Id == poi.OverseerCompanionId);
            AddDeployStat(vbox, "Overseer", ov != null
                ? $"{ov.Name}  (+{SupplyCacheSystem.OverseerYieldBonus} yield, harder to besiege)"
                : "none (assign one to boost the draw)");
        }
        if (siege != null)
            AddDeployStat(vbox, "Under siege", $"{siege.AggressorName}, {siege.Advance}/100");

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        buttons.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttons);

        void ActionButton(string text, bool primary, System.Action onPressed)
        {
            var btn = new Button { Text = text, CustomMinimumSize = new Vector2(150, 40) };
            UITheme.ApplyButtonStyle(btn, isPrimary: primary);
            btn.Pressed += () => onPressed();
            buttons.AddChild(btn);
        }

        if (siege != null)
        {
            // The fight is the warfront's; hand over to the standard dialog.
            ActionButton("Go to the siege", true, () =>
            {
                CloseSupplyUi();
                ShowWarfrontIntervene(siege);
            });
        }
        else if (guildOwned)
        {
            // Field Party v1 (2026-09-24, ruled): overseers are no longer
            // assigned from the map by fiat. A field party garrisons the depot
            // and its first member oversees it. The picker (ShowOverseerPicker)
            // is left in place but unreached.
            var hint = new Label
            {
                Text = string.IsNullOrEmpty(poi.OverseerCompanionId)
                    ? "Post a field party here (Garrison) to oversee it."
                    : "Overseen by the garrison posted here.",
            };
            hint.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            hint.AddThemeColorOverride("font_color", UITheme.TextSecondary);
            buttons.AddChild(hint);
        }
        else
        {
            ActionButton("Storm the depot", true, () => CommitCacheSiege(poiIndex));
        }

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(110, 40) };
        UITheme.ApplyButtonStyle(close, isPrimary: false);
        close.Pressed += CloseSupplyUi;
        buttons.AddChild(close);
    }

    private void CloseSupplyUi()
    {
        _supplyUi?.QueueFree();
        _supplyUi = null;
    }

    /// <summary>Companion picker for the overseer posting: recruited, healthy,
    /// home, and uncommitted (not in the party, not an envoy, not already an
    /// overseer). The stake is stated on the button row: they are wounded if
    /// the cache falls.</summary>
    private void ShowOverseerPicker(int poiIndex)
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null)
            return;

        CloseSupplyUi();
        _supplyUi = new CanvasLayer { Name = "SupplyCacheUI" };
        AddChild(_supplyUi);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.02f, 0.72f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _supplyUi.AddChild(backdrop);

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both, GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -260, OffsetRight = 260, OffsetTop = -190, OffsetBottom = 190,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgRaised, UITheme.Success));
        _supplyUi.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 22);
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        var title = new Label
        {
            Text = "Post an Overseer",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        title.AddThemeColorOverride("font_color", UITheme.Success);
        vbox.AddChild(title);

        var warn = new Label
        {
            Text = $"+{SupplyCacheSystem.OverseerYieldBonus} supplies per lunation and a stiffer " +
                   "defence, but if the cache falls, they are wounded in the rout. " +
                   "Posted companions can't join the party or run envoy missions.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        warn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        warn.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        vbox.AddChild(warn);
        vbox.AddChild(new HSeparator());

        bool any = false;
        foreach (var c in cycle.Companions)
        {
            if (!SupplyCacheSystem.OverseerEligible(save, c))
                continue;
            any = true;
            var btn = new Button
            {
                Text = $"{c.Name}  ({c.School})",
                CustomMinimumSize = new Vector2(0, 36),
            };
            UITheme.ApplyButtonStyle(btn, isPrimary: false);
            string cid = c.Id;
            btn.Pressed += () =>
            {
                SupplyCacheSystem.AssignOverseer(SaveManager.ActiveSave, poiIndex, cid);
                CloseSupplyUi();
                BuildSupplyMarkers();
            };
            vbox.AddChild(btn);
        }
        if (!any)
        {
            var none = new Label
            {
                Text = "No one is free: everyone is in the party, afield, recovering, or already posted.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            none.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            none.AddThemeColorOverride("font_color", UITheme.TextDim);
            vbox.AddChild(none);
        }

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var cancel = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(110, 40) };
        UITheme.ApplyButtonStyle(cancel, isPrimary: false);
        cancel.Pressed += CloseSupplyUi;
        vbox.AddChild(cancel);
    }

    /// <summary>Lay siege to a cache the guild doesn't hold: open (or join) the
    /// cache warfront with the guild as aggressor and deploy into it at once,
    /// side Seize: one successful expedition flips the cache
    /// (SupplyCacheSystem.ApplyCacheIntervention). Costs a lunation like every
    /// deploy; a failed sortie collapses the siege.</summary>
    private void CommitCacheSiege(int poiIndex)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.World == null || poiIndex < 0 || poiIndex >= _world.Pois.Count)
            return;
        var poi = _world.Pois[poiIndex];

        CloseSupplyUi();

        // Field Party v1 (2026-09-24): the castle no longer fights for depots.
        // The party standing on the cache storms it; the run is a field dive
        // with the Seize intervention pending, and the return path
        // (ResolveReturnedWarfrontIntervention) is unchanged.
        var stormer = PartyStandingAt(cycle, poi.X, poi.Y, out string why);
        if (stormer == null)
        {
            ShowStrategicNotice("Send the field party", why);
            return;
        }
        SelectPiece(stormer.Id);

        var wf = SupplyCacheSystem.OpenPlayerSiege(cycle, poiIndex);
        cycle.PendingWarfrontId = wf.Id;
        cycle.PendingWarfrontSide = WarfrontSide.Seize;
        cycle.WarfrontStrongholdCleared = false;

        OpenFieldPartyPicker(new StagingPoint
        {
            X = poi.X,
            Y = poi.Y,
            Name = $"the supply cache in {SupplyCacheSystem.HostName(cycle, poi)}",
            Source = "FieldParty",
            Available = true,
        });
    }

    /// <summary>The free, unposted party standing on a tile, or null with the
    /// reason. The verbs that used to deploy the castle to a fight now need a
    /// party to be there first, and the refusal says so.</summary>
    private FieldParty PartyStandingAt(CycleState cycle, int x, int y, out string why)
    {
        why = $"No field party stands at ({x},{y}). Walk one there first; the castle does not fight for this.";
        if (cycle?.FieldParties == null)
        {
            return null;
        }
        var selected = SelectedParty();
        if (selected != null && !FieldPostings.IsDetachment(selected) && selected.X == x && selected.Y == y)
        {
            if (selected.State != FieldPartyState.AtAnchor || WorldClock.PartyBusy(cycle, selected))
            {
                why = $"{selected.Name} are not standing free. Stop their work or wait for them.";
                return null;
            }
            return selected;
        }
        foreach (var p in cycle.FieldParties)
        {
            if (p != null && !FieldPostings.IsDetachment(p) && p.X == x && p.Y == y
                && p.State == FieldPartyState.AtAnchor && !WorldClock.PartyBusy(cycle, p))
            {
                return p;
            }
        }
        return null;
    }

    // ── Warfronts: markers + three-sided intervention ────────────────────────

    /// <summary>Render a clickable crossed-front marker for each open warfront at
    /// its border tile, the deploy target for intervention.</summary>
    private void BuildWarfrontMarkers()
    {
        _warfrontLayer?.QueueFree();
        _warfrontLayer = new Node2D { Name = "WarfrontMarkers", ZIndex = 3 };
        AddChild(_warfrontLayer);

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle?.Warfronts == null)
            return;

        foreach (var wf in cycle.Warfronts)
        {
            if (wf.Closed || !wf.HasFocus)
                continue;

            var center = HexCoord.OffsetRenderPosition(wf.FocusCol, wf.FocusRow, TilePx)
                         + new Vector2(TilePx * 0.5f, TilePx * 0.5f);
            var marker = new Node2D { Position = center };

            // A red diamond ring reads as conflict, distinct from gold staging beacons.
            var ring = new Polygon2D { Polygon = MakeRing(TilePx * 1.7f), Color = UITheme.Danger };
            marker.AddChild(ring);
            var core = new Polygon2D { Polygon = MakeRing(TilePx * 0.8f), Color = UITheme.TextPrimary };
            marker.AddChild(core);

            // Advance bar as a tiny label above the marker. Cache sieges sit ON
            // the cache tile, whose supply marker already carries a "+N" tag at
            // -3.0 tiles; lift the siege label clear of it.
            var lbl = new Label
            {
                Text = $"⚔ {wf.Advance}%",
                Position = new Vector2(-TilePx * 1.6f,
                    -TilePx * (wf.IsCacheSiege ? 4.4f : 3.2f)),
            };
            lbl.AddThemeFontSizeOverride("font_size", UITheme.OverworldUIFontSize - 3);
            lbl.AddThemeColorOverride("font_color", UITheme.Danger);
            marker.AddChild(lbl);

            // Cache sieges get NO clickable area: the supply marker underneath
            // owns the click (its dialog routes to "Go to the siege"). Two
            // overlapping Area2Ds would both receive the click (Godot picking
            // doesn't stop at the topmost) and stack two modal backdrops.
            if (!wf.IsCacheSiege)
            {
                var area = new Area2D();
                area.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = TilePx * 1.9f } });
                var captured = wf;
                area.InputEvent += (viewport, evt, idx) =>
                {
                    if (evt is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                        ShowWarfrontIntervene(captured);
                };
                marker.AddChild(area);
            }

            _warfrontLayer.AddChild(marker);
        }
    }

    /// <summary>The three-sided intervention dialog. Each side sets the pending
    /// intervention on the cycle and deploys an expedition to the front tile (reusing
    /// Deploy(), so it costs a lunation like any sortie); the outcome is applied on
    /// return in ResolveReturnedWarfrontIntervention.</summary>
    private void ShowWarfrontIntervene(Warfront wf)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || !wf.HasFocus)
            return;

        _warfrontUi?.QueueFree();
        _warfrontUi = new CanvasLayer { Name = "WarfrontUI" };
        AddChild(_warfrontUi);

        // Same 3D-native flow as deploy: fly the camera into the front (centered in the
        // map area left of the drawer), no dimming backdrop, and present the choice as a
        // right-edge drawer that slides out.
        _atlas3D?.FlyToTile(wf.FocusCol, wf.FocusRow, 30f, SidebarWidth * 0.5f);

        var guard = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        guard.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _warfrontUi.AddChild(guard);

        var panel = new PanelContainer
        {
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetTop = HudManager.BarHeight,
            OffsetBottom = 0,
            OffsetLeft = 0,
            OffsetRight = SidebarWidth,   // start off-screen; the tween below slides it in
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Danger));
        _warfrontUi.AddChild(panel);

        var pop = CreateTween();
        pop.SetParallel(true);
        pop.TweenProperty(panel, "offset_left", -SidebarWidth, 0.34f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        pop.TweenProperty(panel, "offset_right", 0f, 0.34f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 22);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        var title = new Label { Text = $"Warfront: {wf.DefenderName}" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        title.AddThemeColorOverride("font_color", UITheme.Danger);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);
        vbox.AddChild(new HSeparator());

        AddDeployStat(vbox, "Aggressor", wf.AggressorName);
        AddDeployStat(vbox, "Defender", wf.DefenderName);
        AddDeployStat(vbox, "Advance", $"{wf.Advance}/100  (falls at 100, repelled at 0)");
        AddDeployStat(vbox, "Front", $"({wf.FocusCol}, {wf.FocusRow})");
        AddDeployStat(vbox, "Cost", "a field dive: days in proportion to the rations it eats");

        var help = new Label
        {
            Text = "The field party standing at the front takes a side and fights. Extract alive " +
                   "to swing the war; a defeat swings it against you. To hold a front over " +
                   "moons instead, post a detachment there.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        help.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        help.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        vbox.AddChild(help);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        // Stacked full-width in the drawer (a centered 4-button row doesn't fit 420px).
        var buttons = new VBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(buttons);

        void SideButton(string text, WarfrontSide side, Color color)
        {
            var btn = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44) };
            btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            UITheme.ApplyButtonStyle(btn, isPrimary: side == WarfrontSide.Defend);
            btn.AddThemeColorOverride("font_color", color);
            btn.Pressed += () => CommitWarfrontIntervention(wf, side);
            buttons.AddChild(btn);
        }

        SideButton("Defend", WarfrontSide.Defend, UITheme.Success);
        SideButton("Seize", WarfrontSide.Seize, UITheme.Gold);
        SideButton("Aid attacker", WarfrontSide.Aid, UITheme.Danger);

        buttons.AddChild(new HSeparator());

        var cancel = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(0, 40) };
        cancel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        UITheme.ApplyButtonStyle(cancel, isPrimary: false);
        cancel.Pressed += () =>
        {
            var ui = _warfrontUi;
            _warfrontUi = null;
            CloseDrawer(panel, ui);   // slide back + swoop to overview, matching deploy
        };
        buttons.AddChild(cancel);
    }

    private void CommitWarfrontIntervention(Warfront wf, WarfrontSide side)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || !wf.HasFocus)
            return;

        _warfrontUi?.QueueFree();
        _warfrontUi = null;

        // Field Party v1 (2026-09-24): the party at the front intervenes, as a
        // field dive, not the castle as a sortie. The pending fields and the
        // return path are exactly as before.
        var fighter = PartyStandingAt(cycle, wf.FocusCol, wf.FocusRow, out string why);
        if (fighter == null)
        {
            ShowStrategicNotice("Send the field party", why);
            return;
        }
        SelectPiece(fighter.Id);

        // Record which front + side so the outcome applies on return.
        cycle.PendingWarfrontId = wf.Id;
        cycle.PendingWarfrontSide = side;
        cycle.WarfrontStrongholdCleared = false; // fresh objective for this intervention

        OpenFieldPartyPicker(new StagingPoint
        {
            X = wf.FocusCol,
            Y = wf.FocusRow,
            Name = $"the front at {wf.DefenderName}",
            Source = "FieldParty",
            Available = true,
        });
    }

    /// <summary>Close the deploy drawer: slide it back off the right edge (reverse of the
    /// pop-out), then free it, and swoop the camera back to the overview. Detaches
    /// _deployUi up front and frees the captured layer in the callback, so a rapid new
    /// deploy started mid-slide is never the one that gets freed.</summary>
    /// <summary>Slide a right-edge drawer back off-screen, then free its layer, and swoop
    /// the camera back to the overview. Shared by the deploy and warfront drawers. The
    /// caller detaches its own field (so a rapid re-open is never the layer that's freed)
    /// and passes the captured layer + panel here.</summary>
    private void CloseDrawer(PanelContainer panel, CanvasLayer ui)
    {
        _atlas3D?.FlyToOverview();
        if (ui == null || panel == null || !IsInstanceValid(panel))
        {
            ui?.QueueFree();
            return;
        }
        var slide = CreateTween();
        slide.TweenProperty(panel, "offset_left", 0f, 0.26f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        slide.Parallel().TweenProperty(panel, "offset_right", SidebarWidth, 0.26f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        slide.Chain().TweenCallback(Callable.From(() => ui.QueueFree()));
    }

    private void CloseDeploy(PanelContainer panel)
    {
        var ui = _deployUi;
        _deployUi = null;
        _pendingStaging = null;
        if (_atlas3D != null) _atlas3D.SuppressCityEntry = false;
        CloseDrawer(panel, ui);
    }

    /// <summary>The launch drawer's consumable loadout: one checkbox row per owned
    /// consumable KIND (grouped like the combat popup), checked = carried. Unchecked
    /// kinds go into CycleState.ExcludedConsumableIds and are filtered from the
    /// combat consumable list. No slot cap in v1: the mechanic is opt-out, so the
    /// default sortie is identical to before this UI existed.</summary>
    private void BuildConsumableLoadout(VBoxContainer vbox, GuildSaveData save)
    {
        var cycle = save?.Cycle;
        if (cycle == null || save.Armory?.OwnedItems == null)
            return;

        var byDef = new System.Collections.Generic.Dictionary<string, (string name, int count)>();
        foreach (var inst in save.Armory.OwnedItems)
        {
            var d = ItemDatabase.Get(inst.DefinitionId);
            if (d == null || !d.IsConsumable) continue;
            byDef[d.Id] = byDef.TryGetValue(d.Id, out var cur)
                ? (d.Name, cur.count + 1) : (d.Name, 1);
        }
        if (byDef.Count == 0)
            return;

        var header = new Label { Text = "Provisions" };
        header.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        header.AddThemeColorOverride("font_color", UITheme.Gold);
        vbox.AddChild(header);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, Mathf.Min(30 * byDef.Count, 130)),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        vbox.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);

        foreach (var kv in byDef)
        {
            string defId = kv.Key;
            var check = new CheckBox
            {
                Text = $"{kv.Value.name}  ×{kv.Value.count}",
                ButtonPressed = !cycle.ExcludedConsumableIds.Contains(defId),
            };
            check.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            check.Toggled += on =>
            {
                if (on) cycle.ExcludedConsumableIds.Remove(defId);
                else if (!cycle.ExcludedConsumableIds.Contains(defId))
                    cycle.ExcludedConsumableIds.Add(defId);
                SaveManager.MarkDirty();
            };
            list.AddChild(check);
        }
    }

    private void ShowDeployConfirm(StagingPoint sp)
    {
        _deployUi?.QueueFree();
        _deployUi = new CanvasLayer { Name = "DeployUI" };
        AddChild(_deployUi);
        // Map stays live under the drawer, but a settlement click must not
        // descend into city view underneath it (cleared on close/launch).
        if (_atlas3D != null) _atlas3D.SuppressCityEntry = true;

        // 3D-native deploy: no dimming backdrop; the map stays visible with this
        // staging point's deploy footprint (WorldAtlas3D's window preview) highlighted
        // in-world. A cinematic fly-in swoops the camera down into the region; the
        // half-drawer-width shift centers the beacon in the map area LEFT of the drawer.
        _atlas3D?.FlyToTile(sp.X, sp.Y, 30f, SidebarWidth * 0.5f);

        // Deploy-flow streamline (2026-08-21): the map stays LIVE under the
        // drawer. Pan/zoom to read the region, and clicking another staging
        // beacon RETARGETS this drawer (OnAtlas3DTilePicked filters to staging-
        // only while the drawer is up, so caches/warfronts can't stack dialogs
        // over it). The old full-rect Stop guard is gone; the drawer panel
        // itself still swallows clicks over its own rect.

        // Full-height sidebar flush to the right edge (below the global top bar) that
        // SLIDES OUT from off-screen: a drawer opening, not a modal popping in. The
        // width is constant through the slide, so the content never reflows. This is
        // the "launch screen": the manifest + spell prep, read against the region the
        // camera flew into on the left.
        var panel = new PanelContainer
        {
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Begin,
            OffsetTop = HudManager.BarHeight,
            OffsetBottom = 0,
            // Start fully off-screen to the right; the tween below slides it in.
            OffsetLeft = 0,
            OffsetRight = SidebarWidth,
        };
        panel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Violet));
        _deployUi.AddChild(panel);

        var pop = CreateTween();
        pop.SetParallel(true);
        pop.TweenProperty(panel, "offset_left", -SidebarWidth, 0.34f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        pop.TweenProperty(panel, "offset_right", 0f, 0.34f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 22);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        // A fresh drawer is a fresh question. Without this, opening the drawer
        // after cancelling a field send would leave the Deploy button sending
        // the field party.
        _pendingRunKind = ExpeditionRunKind.Castle;

        var title = new Label { Text = $"Sortie from {sp.Name}" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        // Context: kingdom + terrain + range.
        var tile = _world.GetTile(sp.X, sp.Y);
        string kingdomLabel = string.IsNullOrEmpty(tile.KingdomId)
            ? "Wilderness"
            : (_kingdoms.TryGetValue(tile.KingdomId, out var ks) && !string.IsNullOrEmpty(ks.ControllingFactionId)
                ? FactionDisplay(ks.ControllingFactionId)
                : tile.KingdomId);

        AddDeployStat(vbox, "Location", $"({sp.X}, {sp.Y}) · {tile.Terrain}");
        AddDeployStat(vbox, "Territory", kingdomLabel);
        AddDeployStat(vbox, "Operating range", $"~{DeployWindowRadius * 2} tiles across");

        // Time cost: every deploy spends one whole lunation of the doomsday
        // clock. Surface it here: it is the most expensive thing the player
        // spends, and it was previously invisible until the debug log.
        var depCycle = SaveManager.ActiveSave?.Cycle;
        if (depCycle != null)
        {
            var depCal = depCycle.Calendar;
            bool fieldDrawer = _pendingRunKind == ExpeditionRunKind.Field;
            float perUnit = fieldDrawer ? WorldClock.DaysPerRation : WorldClock.DaysPerFuel;
            AddDeployStat(vbox, "Time cost",
                $"{perUnit:0.##} day per {(fieldDrawer ? "ration" : "fuel")}, charged on return  "
                + $"·  day {depCal.DayOfLunation + 1} of {CalendarState.DaysPerLunation}, "
                + $"{depCal.DaysToNewMoon} to the new moon");
        }

        // K2 (§5b): party manifest: who actually deploys, who's in the
        // infirmary. Without this the injury system was invisible outside
        // the log and a missing companion in the first fight was a surprise.
        var deploySave = SaveManager.ActiveSave;
        if (deploySave != null)
        {
            var fielded = new System.Collections.Generic.List<string>();
            var infirmary = new System.Collections.Generic.List<string>();
            foreach (var cid in deploySave.ActivePartyCompanionIds)
            {
                var comp = deploySave.Companions.Find(x => x.Id == cid && x.IsRecruited && !x.IsPermadead);
                if (comp == null)
                    continue;
                if (comp.IsInjured)
                    infirmary.Add($"{comp.Name} ({comp.InjuredLunationsRemaining} lun.)");
                else
                    fielded.Add(comp.Name);
            }
            AddDeployStat(vbox, "Party",
                fielded.Count > 0 ? "Wizard + " + string.Join(", ", fielded) : "The wizard, alone");
            if (infirmary.Count > 0)
            {
                var injWarn = new Label
                {
                    Text = $"✚ Infirmary: {string.Join(", ", infirmary)} (recovering, will not deploy).",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                injWarn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
                injWarn.AddThemeColorOverride("font_color", UITheme.Danger);
                vbox.AddChild(injWarn);
            }
        }

        // Corruption warning if the staging tile is corrupted.
        if (tile.Corruption >= 20)
        {
            string sev = tile.Corruption >= 60 ? "Heavy" : "Spreading";
            var warn = new Label { Text = $"⚠ {sev} corruption here ({tile.Corruption}/100)." };
            warn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            warn.AddThemeColorOverride("font_color", UITheme.Danger);
            vbox.AddChild(warn);
        }

        // S4: Grimoire preparation, the §4a prepared slots, chosen at launch.
        BuildGrimoirePrep(vbox, deploySave);

        // Deploy-flow streamline (2026-08-21): the consumable LOADOUT lives on the
        // launch screen. Check/uncheck what the party carries. Default all-carried
        // (today's behaviour); exclusions persist this cycle.
        BuildConsumableLoadout(vbox, deploySave);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        buttons.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(buttons);

        var cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(120, 40) };
        UITheme.ApplyButtonStyle(cancelBtn, isPrimary: false);
        cancelBtn.Pressed += () => CloseDeploy(panel);
        buttons.AddChild(cancelBtn);

        var deployBtn = new Button { Text = "Sortie", CustomMinimumSize = new Vector2(120, 40) };
        UITheme.ApplyButtonStyle(deployBtn, isPrimary: true);
        deployBtn.Pressed += () => { _pendingRunKind = ExpeditionRunKind.Castle; Deploy(); };
        buttons.AddChild(deployBtn);

        // Expedition v2: the OTHER force. Same question ("go here"), different
        // answer, so it sits on the same drawer as Deploy and March rather than
        // hiding behind a mode the player has to find. Offered at any staging
        // point, because a field party travels to KNOWN anchors and a staging
        // point is exactly that.
        // The field party used to have a three-state control here: dive if it
        // was standing on this anchor, order an abstract journey if it was not,
        // greyed out while that journey ran. All three are gone.
        //
        // The drawer belongs to a STAGING POINT, and the party is not a thing
        // that happens at staging points any more. It is a piece on the map with
        // its own marker, its own move order and its own "Take the field"
        // button, and it acts wherever it is standing. Leaving a party control
        // here would have been a second, worse way to command it.

        // The March button that sat beside Deploy is gone (2026-09-24): the
        // castle marches from the map (the March verb, or a beacon's place
        // card), and this confirm is the SORTIE only, opened from where the
        // castle stands.
    }

    /// <summary>Choose who goes, then send them.
    ///
    /// <para>The picker is the half of the force split that the data layer could
    /// not supply: Companion.Posting has been in the schema since the first
    /// increment and nothing in the game could WRITE it, so the field party was
    /// permanently empty and the entire second force was inert.</para>
    ///
    /// <para>Postings are written straight onto the companions and reconciled,
    /// because Companion.Posting is the single source of truth and
    /// FieldParty.MemberCompanionIds is only the ordered view. Writing the view
    /// and hoping is how the two drift.</para>
    ///
    /// <para>Crew members appear in the list and can be taken. That is the
    /// interesting decision in the whole feature: every station a companion
    /// vacates costs the fortress its crew effect (fuel burn, tank, scry, ambush
    /// warning), so sending a strong party makes the next castle move worse.
    /// Hiding crew from the list would have removed the tradeoff and left two
    /// unrelated rosters.</para></summary>
    private void OpenFieldPartyPicker(StagingPoint sp)
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null || sp == null)
        {
            return;
        }

        ExpeditionAnchors.EnsurePrimaryParty(cycle);
        ExpeditionAnchors.ReconcilePostings(cycle);

        // The SELECTED party, not the primary one (Field Party v1, 2026-09-24):
        // with two parties, writing everyone to field_1 would send the wrong
        // people and empty the other party.
        string targetPartyId = SelectedParty() != null && !FieldPostings.IsDetachment(SelectedParty())
            ? SelectedParty().Id
            : ExpeditionAnchors.PrimaryFieldPartyId;
        int partyCap = Mathf.Max(1, cycle.MaxPartySize);

        var dlg = new ConfirmationDialog
        {
            Title = "Take the field",
            OkButtonText = "Go",
            CancelButtonText = "Cancel",
            MinSize = new Vector2I(520, 460),
        };

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        dlg.AddChild(box);

        var intro = new Label
        {
            Text = $"They take the field at {sp.Name} ({sp.X},{sp.Y}).\n"
                 + $"The dive costs about {WorldClock.DaysPerRation:0.##} day per ration eaten, charged when they return.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        intro.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        intro.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(intro);
        box.AddChild(new HSeparator());

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        box.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(rows);

        var boxes = new System.Collections.Generic.List<(CheckBox box, Companion who)>();
        int eligible = 0;
        if (cycle.Companions != null)
        {
            foreach (var c in cycle.Companions)
            {
                if (c == null || !c.IsRecruited || c.IsPermadead)
                {
                    continue;
                }
                eligible++;

                // Injured companions are listed and DISABLED rather than hidden,
                // so "where is Elara" has an answer on the screen that asks the
                // question instead of in a different panel. So are companions
                // posted with a detachment: they are somewhere on the map, and
                // the roster row for that detachment is where to get them back.
                bool postedElsewhere = c.Posting == CompanionPosting.Field
                                       && !string.IsNullOrEmpty(c.FieldPartyId)
                                       && c.FieldPartyId != targetPartyId
                                       && (cycle.FieldParties?.Exists(fp => fp != null && fp.Id == c.FieldPartyId && FieldPostings.IsDetachment(fp)) ?? false);
                string suffix = c.IsInjured
                    ? $"  (injured, {c.InjuredLunationsRemaining} lunation(s))"
                    : postedElsewhere ? "  (posted with a detachment)"
                    : c.Posting == CompanionPosting.Crew ? "  (castle crew)" : "";

                string voc = Vocations.Describe(c);
                var cb = new CheckBox
                {
                    Text = c.Name + (string.IsNullOrEmpty(voc) ? "" : $"  ·  {voc}") + suffix,
                    ButtonPressed = c.Posting == CompanionPosting.Field && !postedElsewhere,
                    Disabled = c.IsInjured || postedElsewhere,
                };
                cb.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
                rows.AddChild(cb);
                boxes.Add((cb, c));
            }
        }

        if (eligible == 0)
        {
            var none = new Label { Text = "No companions are recruited yet. The wizard goes alone." };
            none.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            none.AddThemeColorOverride("font_color", UITheme.TextDim);
            rows.AddChild(none);
        }

        var warn = new Label
        {
            Text = "Anyone you take off the castle crew stops manning their station, and the "
                 + "fortress loses that effect on its next move. Numbers also deter the patrols "
                 + "that watch the roads.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        warn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        warn.AddThemeColorOverride("font_color", UITheme.Warning);
        box.AddChild(warn);

        dlg.Confirmed += () =>
        {
            // The party is capped at MaxPartySize (Grand Hall). The picker had
            // no cap before this and the Field Party v1 spec gives it one; the
            // refusal names the number rather than silently dropping the last
            // few ticked boxes.
            int ticked = 0;
            foreach (var (cb, who) in boxes)
            {
                if (!who.IsInjured && !cb.Disabled && cb.ButtonPressed)
                {
                    ticked++;
                }
            }
            if (ticked > partyCap)
            {
                ShowStrategicNotice("Too many for one party",
                    $"A party holds {partyCap}. Untick {ticked - partyCap}, or grow the Grand Hall.");
                return;
            }
            foreach (var (cb, who) in boxes)
            {
                if (who.IsInjured || cb.Disabled)
                {
                    continue;   // the control was disabled; do not let it decide
                }
                if (cb.ButtonPressed)
                {
                    who.Posting = CompanionPosting.Field;
                    who.FieldPartyId = targetPartyId;
                }
                else if (who.Posting == CompanionPosting.Field)
                {
                    // Taken OFF the party goes back to the CAMPUS, not to the
                    // crew: the crew is a separate decision made elsewhere, and
                    // silently promoting someone into a station would change the
                    // fortress's characteristics without the player asking.
                    who.Posting = CompanionPosting.Campus;
                    who.FieldPartyId = "";
                }
            }
            ExpeditionAnchors.ReconcilePostings(cycle);
            SaveManager.MarkDirty();

            _pendingStaging = sp;
            _pendingRunKind = ExpeditionRunKind.Field;
            // An audience is spent when the party actually goes, not when
            // the verb was pressed: a cancelled picker costs the court nothing.
            if (!string.IsNullOrEmpty(PlayerSession.AudienceEncounterId)
                && !string.IsNullOrEmpty(PlayerSession.AudienceKingdomId))
            {
                cycle.LastAudienceLunation ??= new System.Collections.Generic.Dictionary<string, int>();
                cycle.LastAudienceLunation[PlayerSession.AudienceKingdomId] = cycle.Calendar?.CurrentLunation ?? 0;
            }
            dlg.QueueFree();
            Deploy();
        };
        dlg.Canceled += () =>
        {
            // A cancelled audience must not ride along into the next dive.
            PlayerSession.AudienceEncounterId = "";
            PlayerSession.AudienceKingdomId = "";
            dlg.QueueFree();
        };
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>The party takes the field where it stands. Synthesises a
    /// staging point on its own tile and runs the shipped deploy path, exactly
    /// as the warfront intervention does: the expedition scene is driven by a
    /// coordinate and a radius, and it has never cared whether a real launch
    /// point sits under it.
    ///
    /// <para>This is what makes the party a PIECE. It does not need permission
    /// from the anchor list to act; it acts where it is.</para></summary>
    private void OnPartyTakeTheField()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        // The SELECTED force, not "the primary one". With one party those are
        // the same object and the distinction costs nothing; with two, reading
        // the primary would send the wrong people.
        var party = SelectedParty();
        if (cycle == null || party == null || party.X < 0 || party.Y < 0)
        {
            return;
        }
        if (party.State != FieldPartyState.AtAnchor)
        {
            ShowStrategicNotice("The party is on the road",
                "They cannot take the field while they are walking. Wait for them to arrive.");
            return;
        }

        var here = ExpeditionAnchors.FindAt(cycle, party.X, party.Y);
        OpenFieldPartyPicker(new StagingPoint
        {
            X = party.X,
            Y = party.Y,
            Name = here != null && !string.IsNullOrEmpty(here.Name) ? here.Name : "the field",
            Source = "FieldParty",
            Available = true,
        });
    }

    /// <summary>Move-mode click with the PARTY selected. One click, two verbs:
    /// a waystone tile steps them through instantly, anything else is a march on
    /// foot. The tile decides, not a second mode, because "go there" is one
    /// intention and making the player pick the travel method first is asking
    /// them to know the answer before they have looked at the map.</summary>
    // ── Bank the furnace / field work verbs (2026-09-23) ──────────────────

    private void OnBankFurnacePressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var slot = cycle?.CastleSortie;
        if (slot == null || !slot.IsLive())
        {
            return;
        }
        int repair = ExpeditionAnchors.RepairLunationsFor(slot.CurrentHP, slot.MaxHP, 1, 4);
        var dlg = new ConfirmationDialog
        {
            Title = "Bank the furnace",
            OkButtonText = "Make camp",
            DialogText =
                $"The castle holds at ({slot.X},{slot.Y}) with {slot.StepsRemaining}/{slot.MaxFuel} fuel and "
                + $"{slot.CurrentHP}/{slot.MaxHP} Hull.\n\n"
                + "Shut the furnace down there and make camp. The sortie ends; what it earned rides in the "
                + "hold until a party carries it home. The castle becomes a waypoint, burns nothing while it "
                + $"waits, and is exposed for {repair} lunation(s) of resupply.",
        };
        dlg.Confirmed += () =>
        {
            dlg.QueueFree();
            string name = CastleTypes.For(PlayerSession.SelectedSchool)?.Name ?? "The castle";
            string line = ExpeditionAnchors.BankFrozenCastle(cycle, name, 1, 4);
            if (string.IsNullOrEmpty(line))
            {
                return;
            }
            var save = SaveManager.ActiveSave;
            if (save != null)
            {
                save.TotalRuns++;   // the sortie counts, as the scene's park counts it
            }
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
            RefreshPieceChrome();
            ShowStrategicNotice("The furnace is banked", line);
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void OnStopWorkPressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        // Through FieldPostings, not FieldWork: an overseer has to step down
        // when the garrison that posted them stands down.
        string line = FieldPostings.Stop(cycle, party);
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        RefreshPieceChrome();
        ShowStrategicNotice("Work stopped", line);
    }

    // ── Field Party v1: postings and detachments (2026-09-24) ─────────────
    // docs/field_party_task_force_spec_v1. The debug "Survey here" stand-in
    // that lived here is gone: postings are real and the sheet below is how
    // they start.

    /// <summary>Which detachment the Collect and Recall verbs would act on for
    /// the selected piece: a party names the first of its detachments that
    /// qualifies; a selected detachment names itself.</summary>
    private (FieldParty collectable, FieldParty recallable) DetachmentVerbsFor(CycleState cycle, FieldParty party)
    {
        if (cycle == null || party == null)
        {
            return (null, null);
        }
        if (FieldPostings.IsDetachment(party))
        {
            var parent = cycle.FieldParties?.Find(p => p != null && p.Id == party.ParentId);
            bool c = FieldPostings.CanCollect(cycle, parent, party, out _);
            bool r = !c && FieldPostings.CanRecall(cycle, parent, party, out _);
            return (c ? party : null, r ? party : null);
        }
        FieldParty collect = null, recall = null;
        foreach (var det in FieldPostings.DetachmentsOf(cycle, party))
        {
            if (collect == null && FieldPostings.CanCollect(cycle, party, det, out _))
            {
                collect = det;
            }
            else if (recall == null && FieldPostings.CanRecall(cycle, party, det, out _))
            {
                recall = det;
            }
        }
        return (collect, recall);
    }

    /// <summary>The fight a free party could start where it stands: storming
    /// a depot it does not hold, or intervening at a kingdom front. Null when
    /// there is none, which hides the verb.</summary>
    private string InterventionVerbAt(CycleState cycle, FieldParty party)
    {
        var world = cycle?.World;
        if (world == null || party == null || !world.InBounds(party.X, party.Y))
        {
            return null;
        }
        int poiIndex = world.GetTile(party.X, party.Y).PoiIndex;
        if (poiIndex >= 0 && poiIndex < world.Pois.Count)
        {
            var poi = world.Pois[poiIndex];
            if (poi.Kind == PoiKind.SupplyCache && poi.Discovered
                && SupplyCacheSystem.ControllerOf(poi) != SupplyCacheSystem.GuildId)
            {
                return "\u2694  Storm the depot";
            }
        }
        if (cycle.Warfronts != null)
        {
            foreach (var wf in cycle.Warfronts)
            {
                if (wf != null && !wf.Closed && !wf.IsCacheSiege && wf.HasFocus
                    && wf.FocusCol == party.X && wf.FocusRow == party.Y)
                {
                    return "\u2694  Intervene at the front";
                }
            }
        }
        return null;
    }

    private void OnInterveneHerePressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        var world = cycle?.World;
        if (world == null || party == null || !world.InBounds(party.X, party.Y))
        {
            return;
        }
        int poiIndex = world.GetTile(party.X, party.Y).PoiIndex;
        if (poiIndex >= 0 && poiIndex < world.Pois.Count && world.Pois[poiIndex].Kind == PoiKind.SupplyCache)
        {
            CommitCacheSiege(poiIndex);
            return;
        }
        var front = cycle.Warfronts?.Find(w => w != null && !w.Closed && !w.IsCacheSiege && w.HasFocus
                                              && w.FocusCol == party.X && w.FocusRow == party.Y);
        if (front != null)
        {
            ShowWarfrontIntervene(front);
        }
    }

    // ── Navigation and the castle's sortie (2026-09-24) ──────────────────

    /// <summary>City to world, world to home city. One control, two states,
    /// mirrored by Escape.</summary>
    private void OnViewTogglePressed()
    {
        if (_atlas3D == null)
        {
            return;
        }
        if (_atlas3D.CityMode)
        {
            _atlas3D.LeaveCityMode();
            return;
        }
        if (_world != null && _atlas3D.HasCityGrounds && _world.InBounds(_world.HomeX, _world.HomeY))
        {
            _atlas3D.FlyToTile(_world.HomeX, _world.HomeY, 30f, 0f);
            _atlas3D.EnterCityMode();
        }
    }

    /// <summary>Can the castle stride out from where it stands, and if not why.
    /// The same preconditions a march has, minus the fuel-per-tile check: a
    /// sortie refuels at deploy.</summary>
    private static bool CanSortie(CycleState cycle, out string why)
    {
        why = "";
        if (cycle?.World == null)
        {
            why = "There is no world.";
            return false;
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            why = "The castle is already in the field. Aim the stone at it.";
            return false;
        }
        if (!cycle.CastleParked)
        {
            why = "The castle is out. Recall or make camp first.";
            return false;
        }
        if (cycle.CastleRepairLunations > 0)
        {
            why = $"The crews are still at work: {cycle.CastleRepairLunations} lunation(s) of resupply left.";
            return false;
        }
        if (WorldClock.CastleBusy(cycle))
        {
            why = $"The castle is still coming in from its last sortie: {cycle.CastleBusyUntilDay - WorldClock.Now(cycle)} day(s).";
            return false;
        }
        if (!string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            why = "There are soldiers in the camp. Answer them before you move.";
            return false;
        }
        if (!cycle.World.InBounds(cycle.CastleX, cycle.CastleY))
        {
            why = "The castle is not sited.";
            return false;
        }
        return true;
    }

    /// <summary>The staging point the castle launches from: whatever stages
    /// on its own tile (its park, the dock, a secured outpost it stopped on),
    /// or a synthesised one, because the expedition scene only ever needed a
    /// coordinate and a radius.</summary>
    private StagingPoint CastleLaunchPoint(CycleState cycle)
    {
        var here = _world?.StagingPoints?.Find(s => s != null && s.X == cycle.CastleX && s.Y == cycle.CastleY);
        if (here != null)
        {
            return here;
        }
        var anchor = ExpeditionAnchors.FindAt(cycle, cycle.CastleX, cycle.CastleY);
        return new StagingPoint
        {
            X = cycle.CastleX,
            Y = cycle.CastleY,
            Name = anchor != null && !string.IsNullOrEmpty(anchor.Name) ? anchor.Name : "the castle",
            Source = "Castle",
            Available = true,
        };
    }

    private void OnSortiePressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            return;
        }
        if (!CanSortie(cycle, out string why))
        {
            ShowStrategicNotice("The castle cannot sortie", why);
            return;
        }
        SelectPiece("");
        var sp = CastleLaunchPoint(cycle);
        _pendingStaging = sp;
        ShowDeployConfirm(sp);
    }

    /// <summary>A staging beacon is a PLACE now, not a launch dialog: what it is,
    /// whose ground, and the two ways a force can be sent there. The castle
    /// launches from where it stands (Sortie), never from a beacon it is not
    /// standing on.</summary>
    private void ShowPlaceCard(StagingPoint sp)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null || _world == null || !_world.InBounds(sp.X, sp.Y))
        {
            return;
        }
        var tile = _world.GetTile(sp.X, sp.Y);
        string kingdomLabel = string.IsNullOrEmpty(tile.KingdomId)
            ? "Wilderness"
            : (_kingdoms.TryGetValue(tile.KingdomId, out var ks) && !string.IsNullOrEmpty(ks.ControllingFactionId)
                ? FactionDisplay(ks.ControllingFactionId)
                : tile.KingdomId);
        string kind = sp.Source switch
        {
            "Start" => "the guild's dock",
            "Castle" => "the castle's camp",
            "Waypoint" => "a raised waystone",
            "Shard" => "a shard zone's heart",
            "Secured" => "a secured outpost",
            _ => "a staging point",
        };

        var dlg = new AcceptDialog
        {
            Title = string.IsNullOrEmpty(sp.Name) ? "Staging point" : sp.Name,
            OkButtonText = "Close",
            DialogText = $"({sp.X},{sp.Y})  ·  {tile.Terrain}  ·  {kingdomLabel}\n{kind}"
                       + (sp.Available ? ", stages and supplies your forces." : ", overrun: it neither stages nor supplies you until retaken."),
        };

        // March the castle here.
        if (cycle.CastleParked && !(cycle.CastleX == sp.X && cycle.CastleY == sp.Y))
        {
            bool canMarch = CastleMarch.CanMarchTo(cycle, sp.X, sp.Y, out string marchWhy);
            int cost = canMarch ? CastleMarch.CostTo(cycle, sp.X, sp.Y) : 0;
            var b = dlg.AddButton(canMarch ? $"March the castle here ({cost} fuel)" : "March the castle here", true, "march");
            b.Disabled = !canMarch;
            b.TooltipText = canMarch ? "" : marchWhy;
        }
        // Move the selected party here.
        var party = SelectedParty();
        if (party != null && !FieldPostings.IsDetachment(party) && !(party.X == sp.X && party.Y == sp.Y))
        {
            bool canGo = FieldMarch.CanTeleportTo(cycle, party, sp.X, sp.Y, out _)
                         || FieldMarch.CanMarchTo(cycle, party, sp.X, sp.Y, out _);
            FieldMarch.CanMarchTo(cycle, party, sp.X, sp.Y, out string goWhy);
            var b = dlg.AddButton($"Move {party.Name} here", true, "party");
            b.Disabled = !canGo;
            b.TooltipText = canGo ? "" : goWhy;
        }
        dlg.CustomAction += action =>
        {
            dlg.QueueFree();
            if (action == "march")
            {
                SelectPiece("");
                ExecuteCastleMarch(sp.X, sp.Y, null);
            }
            else if (action == "party")
            {
                TryMovePartyTo(sp.X, sp.Y);
            }
        };
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>The Forces screen (2026-09-24): every force, its members,
    /// their stats and equipment, and the moves between campus, crew and a
    /// free party. Opens on the selected piece. The muster drawer that stood
    /// here for an afternoon is retired into it: one screen for one question.</summary>
    private void OpenForcesScreen()
    {
        string initial = CastleSelected ? "castle" : _selectedPieceId;
        ForcesScreen.Open(this,
            onChanged: () =>
            {
                RefreshPieceChrome();
                BuildSupplyMarkers();
            },
            onClose: RefreshPieceChrome,
            initialForceId: initial);
    }

    /// <summary>Is there a court to be received by where the party stands?
    /// A kingdom's seat always receives; another city only once the court
    /// knows the guild. Once per court per moon. The reason is written for
    /// the tooltip, and begins with "No court" only when the tile is simply
    /// not a city, which is the one case the verb hides rather than greys.</summary>
    private bool AudienceCourtAt(CycleState cycle, FieldParty party, out string kingdomId, out string why)
    {
        kingdomId = "";
        why = "No court here.";
        var world = cycle?.World;
        if (world == null || party == null || !world.InBounds(party.X, party.Y))
        {
            return false;
        }
        var settlement = world.SettlementAt(party.X, party.Y);
        if (settlement == null || settlement.Tier != SettlementTier.City || string.IsNullOrEmpty(settlement.KingdomId))
        {
            return false;
        }
        kingdomId = settlement.KingdomId;
        if (cycle.Council?.Courts == null || !cycle.Council.Courts.TryGetValue(kingdomId, out var court))
        {
            why = "This city answers to no court.";
            return false;
        }
        if (!settlement.IsSeat && !court.HasContact)
        {
            why = "This court does not know the guild. Attend court at their seat first, or send an envoy.";
            return false;
        }
        if (court.MissionFreezeLunations > 0)
        {
            why = $"The court has expelled the guild's people; {court.MissionFreezeLunations} moon(s) until they receive anyone.";
            return false;
        }
        int lun = cycle.Calendar?.CurrentLunation ?? 0;
        if (cycle.LastAudienceLunation != null
            && cycle.LastAudienceLunation.TryGetValue(kingdomId, out int last) && last == lun)
        {
            why = "This court has already received you this moon.";
            return false;
        }
        return true;
    }

    /// <summary>Seek audience: pick a table from the region's negotiation pool
    /// and send the party into the field with it pending. The expedition scene
    /// opens the table before the first step (ExpeditionManager.OpenAudienceDeferred),
    /// so every reward a table pays (gold, supplies, charted ground, revealed
    /// places, safe conduct, tuition, the court echo) lands through the one
    /// path that already pays them.</summary>
    private void OnSeekAudiencePressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        if (cycle == null || party == null)
        {
            return;
        }
        if (!AudienceCourtAt(cycle, party, out string kid, out string why))
        {
            ShowStrategicNotice("No audience", why);
            return;
        }
        string region = cycle.Kingdoms != null && cycle.Kingdoms.TryGetValue(kid, out var ks) ? ks.TemplateRegionId : "";
        var encounter = NegotiationEncounterLoader.PickForTerrain("", region);
        if (encounter == null)
        {
            ShowStrategicNotice("No audience", "No one at this court will sit with you.");
            return;
        }
        PlayerSession.AudienceEncounterId = encounter.Id;
        PlayerSession.AudienceKingdomId = kid;
        GD.Print($"[StrategicView] Audience sought at {kid}: {encounter.Id}.");

        var here = ExpeditionAnchors.FindAt(cycle, party.X, party.Y);
        OpenFieldPartyPicker(new StagingPoint
        {
            X = party.X,
            Y = party.Y,
            Name = here != null && !string.IsNullOrEmpty(here.Name) ? here.Name : "the court",
            Source = "FieldParty",
            Available = true,
        });
    }

    private void OnPostPressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        if (cycle == null || party == null)
        {
            return;
        }
        var options = FieldPostings.OptionsAt(cycle, party);
        if (options.Count == 0)
        {
            ShowStrategicNotice("Nothing to post here", "No outpost, depot, shard zone or front stands on this tile.");
            return;
        }
        OpenPostingSheet(cycle, party, options);
    }

    /// <summary>The posting sheet: which job, and who stays. Every member is
    /// checked by default; unchecking some makes a detachment of the rest.
    /// Every row is a sentence, because a posting is a decision and not a form.</summary>
    private void OpenPostingSheet(CycleState cycle, FieldParty party, System.Collections.Generic.List<FieldPostings.Option> options)
    {
        var dlg = new ConfirmationDialog
        {
            Title = "Post here",
            OkButtonText = "Post them",
            CancelButtonText = "Cancel",
            MinSize = new Vector2I(560, 520),
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        dlg.AddChild(box);

        var picker = new OptionButton();
        foreach (var o in options)
        {
            picker.AddItem(o.Title);
        }
        picker.Selected = 0;
        box.AddChild(picker);

        var detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        detail.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        detail.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        box.AddChild(detail);

        void ShowDetail(int i)
        {
            var o = options[Mathf.Clamp(i, 0, options.Count - 1)];
            string span = o.Days > 0 ? $"{o.Days} day(s)." : "Until recalled.";
            detail.Text = $"{o.Detail}\n{o.SuppliesPerMoon} supplies a moon, paid at the new moon; "
                        + $"unpaid, they stall and do no work. {span}";
        }
        ShowDetail(0);
        picker.ItemSelected += idx => ShowDetail((int)idx);

        box.AddChild(new HSeparator());
        var who = new Label { Text = "Who stays:" };
        who.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        box.AddChild(who);

        var boxes = new System.Collections.Generic.List<(CheckBox cb, string id)>();
        var members = party.MemberCompanionIds ?? new System.Collections.Generic.List<string>();
        foreach (var id in members)
        {
            var c = cycle.Companions?.Find(x => x != null && x.Id == id);
            if (c == null)
            {
                continue;
            }
            var cb = new CheckBox
            {
                Text = c.Name + (c.IsInjured ? $"  (injured, {c.InjuredLunationsRemaining} lunation(s))" : ""),
                ButtonPressed = true,
            };
            cb.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            box.AddChild(cb);
            boxes.Add((cb, id));
        }

        // What the chosen people bring (Vocations, 2026-09-25). Re-read on
        // every tick and every posting change, so unticking the Scout shows
        // the survey losing its extra ring before the player commits.
        var vocLine = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        vocLine.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        vocLine.AddThemeColorOverride("font_color", UITheme.Gold);
        box.AddChild(vocLine);
        void RefreshVocLine()
        {
            var chosenIds = new System.Collections.Generic.List<string>();
            foreach (var (cb, id) in boxes)
            {
                if (cb.ButtonPressed)
                {
                    chosenIds.Add(id);
                }
            }
            var o = options[Mathf.Clamp(picker.Selected, 0, options.Count - 1)];
            string line = Vocations.SheetLine(cycle, party, o.Kind, chosenIds);
            vocLine.Text = string.IsNullOrEmpty(line) ? "Nobody chosen has a vocation for this." : line;
        }
        RefreshVocLine();
        picker.ItemSelected += _ => RefreshVocLine();
        foreach (var (cb, _) in boxes)
        {
            cb.Toggled += _ => RefreshVocLine();
        }

        var note = new Label
        {
            Text = members.Count <= 1
                ? "The whole party posts itself; there is nobody to split off."
                : "Leave some unchecked and they carry on as the party; the checked ones stay as a detachment. "
                  + "A detachment does not move: collect it by standing with it, or recall it through a waystone.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        note.AddThemeColorOverride("font_color", UITheme.TextDim);
        box.AddChild(note);

        dlg.Confirmed += () =>
        {
            var chosen = new System.Collections.Generic.List<string>();
            foreach (var (cb, id) in boxes)
            {
                if (cb.ButtonPressed)
                {
                    chosen.Add(id);
                }
            }
            var opt = options[Mathf.Clamp(picker.Selected, 0, options.Count - 1)];
            dlg.QueueFree();
            string line = FieldPostings.Begin(cycle, party, opt, chosen, out string why);
            if (line == null)
            {
                ShowStrategicNotice("Not posted", why);
                return;
            }
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(line);
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
            RefreshPieceChrome();
            ShowStrategicNotice("Posted", line);
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void OnCollectPressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        var (det, _) = DetachmentVerbsFor(cycle, party);
        if (cycle == null || det == null)
        {
            return;
        }
        var parent = FieldPostings.IsDetachment(det)
            ? cycle.FieldParties?.Find(p => p != null && p.Id == det.ParentId)
            : null;
        if (parent == null)
        {
            return;
        }
        string line = FieldPostings.Collect(cycle, parent, det);
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        SelectPiece(parent.Id);
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        RefreshPieceChrome();
        ShowStrategicNotice("Collected", line);
    }

    private void OnRecallPressed()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        var (_, det) = DetachmentVerbsFor(cycle, party);
        if (cycle == null || det == null)
        {
            return;
        }
        var parent = cycle.FieldParties?.Find(p => p != null && p.Id == det.ParentId);
        if (parent == null)
        {
            return;
        }
        string line = FieldPostings.Collect(cycle, parent, det);
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        SelectPiece(parent.Id);
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        RefreshPieceChrome();
        ShowStrategicNotice("Through the waystone", line.Replace("take", "recall") );
    }

    private void TryMovePartyTo(int col, int row)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var party = SelectedParty();
        if (cycle == null || party == null)
        {
            return;
        }

        if (FieldMarch.CanTeleportTo(cycle, party, col, row, out _))
        {
            string jump = FieldMarch.Teleport(cycle, party, col, row);
            if (!string.IsNullOrEmpty(jump))
            {
                cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
                cycle.PendingSiegeReports.Add(jump);
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                RefreshPieceChrome();
                ShowStrategicNotice("Through the waystone", jump);
            }
            return;
        }

        if (!FieldMarch.CanMarchTo(cycle, party, col, row, out string why))
        {
            ShowStrategicNotice("The party cannot go there", why);
            return;
        }

        int tiles = cycle.World.HexDistance(party.X, party.Y, col, row);
        int marchDays = FieldMarch.DaysTo(cycle, party, col, row);
        bool fromCastlePark = cycle.CastleParked
                              && party.X == cycle.CastleX && party.Y == cycle.CastleY;
        bool holdWaiting = fromCastlePark && cycle.CastleHold != null && !cycle.CastleHold.IsEmpty;

        var dlg = new ConfirmationDialog
        {
            Title = "Move the field party",
            OkButtonText = "March",
            DialogText =
                $"Send them from ({party.X},{party.Y}) to ({col},{row}).\n\n"
                + $"{tiles} tile(s) on foot, {marchDays} day(s) at {FieldMarch.DaysPerTile} a tile. "
                + "They walk as the days pass, and they cannot take the field while they are walking.\n\n"
                + "The road is watched. Patrols take a share of whatever they carry and cost them "
                + "time; in hostile ground, soldiers turn them back outright."
                + (holdWaiting
                    ? $"\n\nThey will carry the castle's hold ({cycle.CastleHold}) and bank it on arrival."
                    : ""),
        };
        dlg.Confirmed += () =>
        {
            string line = FieldMarch.Order(cycle, party, col, row);
            dlg.QueueFree();
            if (string.IsNullOrEmpty(line))
            {
                return;
            }
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(line);
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
            if (_marchModeBtn != null) _marchModeBtn.ButtonPressed = false;
            _marchMode = false;
            RefreshPieceChrome();
            ShowStrategicNotice("The party sets out", line);
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>March-mode click: confirm the cost, then go. Refusals are shown
    /// rather than swallowed, because the player armed a verb and clicked, and a
    /// silent no-op reads as a broken control.</summary>
    private void TryMarchTo(int col, int row)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            return;
        }

        if (!CastleMarch.CanMarchTo(cycle, col, row, out string why))
        {
            ShowStrategicNotice("The castle cannot march there", why);
            return;
        }

        int cost = CastleMarch.CostTo(cycle, col, row);
        int tiles = cycle.World.HexDistance(cycle.CastleX, cycle.CastleY, col, row);
        var dlg = new ConfirmationDialog
        {
            Title = "March the fortress",
            OkButtonText = "March",
            DialogText =
                $"Send the castle {tiles} tile(s) to ({col},{row}).\n\n" +
                $"Fuel: {cost} of {cycle.CastleFuel}.\n" +
                $"On arrival the crews make camp for {CastleMarch.ArrivalCampLunations} lunation(s), " +
                "and the castle is exposed until they finish.",
        };
        dlg.Confirmed += () => { dlg.QueueFree(); ExecuteCastleMarch(col, row, null); };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>Order the march and report it. The camp at the far end is the
    /// time cost, so nothing advances the calendar here. Shared by the deploy
    /// drawer's March button and march mode, so the two cannot diverge.</summary>
    private void ExecuteCastleMarch(int col, int row, PanelContainer panel)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
        {
            return;
        }

        string name = CastleTypes.For(PlayerSession.SelectedSchool)?.Name ?? "The castle";
        int fromX = cycle.CastleX, fromY = cycle.CastleY;
        string line = CastleMarch.Execute(cycle, col, row, name);
        if (string.IsNullOrEmpty(line))
        {
            return;   // refused; the caller already said why
        }

        cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
        cycle.PendingSiegeReports.Add(line);
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();

        if (panel != null)
        {
            CloseDeploy(panel);
        }

        // The world moved: markers, beacons and the castle's own staging point
        // all changed, so the map is rebuilt rather than patched in four places.
        // First, the fortress is seen to go (2026-09-25): its piece rides the
        // same straight corridor the march charted, then the map rebuilds.
        if (_atlas3D != null && _world != null && _world.InBounds(fromX, fromY))
        {
            var path = CorridorTiles(fromX, fromY, col, row);
            _atlas3D.AcceptInput = false;
            _atlas3D.PlayMarches(
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Vector2I>>
                {
                    [WorldAtlas3D.CastlePathKey] = path,
                },
                () =>
                {
                    if (IsInsideTree())
                    {
                        GetTree().ReloadCurrentScene();
                    }
                });
            return;
        }
        GetTree().ReloadCurrentScene();
    }

    /// <summary>The tiles of a straight march, the same axial lerp
    /// CastleMarch.ChartCorridor walks, deduplicated. Presentation only.</summary>
    private System.Collections.Generic.List<Vector2I> CorridorTiles(int fromX, int fromY, int toX, int toY)
    {
        var tiles = new System.Collections.Generic.List<Vector2I>();
        int steps = _world.HexDistance(fromX, fromY, toX, toY);
        var (aq, ar) = HexCoord.OffsetToAxial(fromX, fromY);
        var (bq, br) = HexCoord.OffsetToAxial(toX, toY);
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 1f : (float)i / steps;
            int q = Mathf.RoundToInt(Mathf.Lerp(aq, bq, t));
            int r = Mathf.RoundToInt(Mathf.Lerp(ar, br, t));
            var (c, rr) = HexCoord.AxialToOffset(q, r);
            var tile = new Vector2I(c, rr);
            if (tiles.Count == 0 || tiles[tiles.Count - 1] != tile)
            {
                tiles.Add(tile);
            }
        }
        return tiles;
    }

    // ════════════════════════════════════════════════════════════════════
    // S4: Grimoire preparation (overworld_spell_system §4a / §14-S4)
    // The deploy dialog is the launch screen, so slot selection lives here:
    // base 2 prepared slots, 3 for the Adept (Versatility's "+1 slot rides
    // the S4 prep UI"). Innates and companion-granted spells occupy no
    // slots and are listed read-only; scrolls show as a satchel summary.
    // ════════════════════════════════════════════════════════════════════

    private Label _prepHeader;
    private HFlowContainer _prepFlow;

    private void BuildGrimoirePrep(VBoxContainer vbox, GuildSaveData deploySave)
    {
        var cycle = deploySave?.Cycle;
        var grim = cycle?.Grimoire;
        if (grim == null)
            return;
        OverworldSpellRegistry.EnsureLoaded();

        vbox.AddChild(new HSeparator());

        string school = cycle.SelectedSchool;
        int slots = 2 + (school == "Adept" ? 1 : 0); // §4a: base 2; Adept 3 (Versatility)

        // Sanitize the persisted loadout: prepared ⊆ known, count ≤ slots.
        // (Covers unlearned ids from older saves and Adept→other cycles.)
        grim.PreparedSpellIds.RemoveAll(id => !grim.KnownSpellIds.Contains(id));
        while (grim.PreparedSpellIds.Count > slots)
            grim.PreparedSpellIds.RemoveAt(grim.PreparedSpellIds.Count - 1);

        // Innates: always prepared, no slots.
        var innateNames = new System.Collections.Generic.List<string>();
        foreach (var innate in OverworldSpellRegistry.InnatesFor(school))
            innateNames.Add(innate.Name);
        if (innateNames.Count > 0)
            AddDeployStat(vbox, "Innate", string.Join(" · ", innateNames));

        // Companion-granted: schools of fielded companions, off-caster tax
        // noted (waived for the Adept, §7h).
        var grantedSchools = new System.Collections.Generic.List<string>();
        foreach (var cid in deploySave.ActivePartyCompanionIds)
        {
            var comp = deploySave.Companions.Find(x => x.Id == cid && x.IsRecruited &&
                                                       !x.IsPermadead && !x.IsInjured);
            if (comp != null && !string.IsNullOrEmpty(comp.School) &&
                comp.School != school && !grantedSchools.Contains(comp.School))
                grantedSchools.Add(comp.School);
        }
        if (grantedSchools.Count > 0)
            AddDeployStat(vbox, "Companion-granted",
                $"{string.Join(" · ", grantedSchools)} innates " +
                (school == "Adept" ? "(no tax: Adept)" : "(+1✦ off-school)"));

        // Prepared slots: toggle the loadout from the known list.
        _prepHeader = new Label();
        _prepHeader.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        _prepHeader.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        vbox.AddChild(_prepHeader);

        if (grim.KnownSpellIds.Count == 0)
        {
            _prepHeader.Text = $"Prepared (0/{slots}). No spells learned yet. " +
                               "Lore sites, cordial deals, and the dead all teach.";
        }
        else
        {
            _prepFlow = new HFlowContainer();
            _prepFlow.AddThemeConstantOverride("h_separation", 6);
            _prepFlow.AddThemeConstantOverride("v_separation", 4);
            vbox.AddChild(_prepFlow);
            RebuildPrepButtons(grim, slots);
        }

        // Scroll satchel: read-only summary; scribing lives at the campus.
        if (grim.ScrollInventory.Count > 0)
        {
            var scrollParts = new System.Collections.Generic.List<string>();
            foreach (var kvp in grim.ScrollInventory)
                if (kvp.Value > 0 && OverworldSpellRegistry.Get(kvp.Key) != null)
                    scrollParts.Add($"{OverworldSpellRegistry.Get(kvp.Key).Name} ×{kvp.Value}");
            if (scrollParts.Count > 0)
                AddDeployStat(vbox, "Scrolls", string.Join(" · ", scrollParts));
        }
    }

    private void RebuildPrepButtons(GrimoireState grim, int slots)
    {
        _prepHeader.Text = $"Prepared ({grim.PreparedSpellIds.Count}/{slots}). Click to toggle:";
        foreach (var child in _prepFlow.GetChildren())
            child.QueueFree();

        // Stable order: by display name.
        var known = new System.Collections.Generic.List<OverworldSpellDefinition>();
        foreach (var id in grim.KnownSpellIds)
        {
            var def = OverworldSpellRegistry.Get(id);
            if (def != null && !def.IsAttunement)
                known.Add(def);
        }
        known.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        foreach (var def in known)
        {
            bool prepared = grim.PreparedSpellIds.Contains(def.Id);
            var btn = new Button
            {
                Text = $"{def.Name} · {def.EssenceCost}✦",
                ToggleMode = true,
                ButtonPressed = prepared,
                TooltipText = def.Description,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
            UITheme.ApplyButtonStyle(btn, isPrimary: prepared);

            string id = def.Id; // capture per-iteration
            btn.Toggled += pressed =>
            {
                if (pressed)
                {
                    if (grim.PreparedSpellIds.Count >= slots)
                    {
                        btn.SetPressedNoSignal(false); // slots full, refuse
                        return;
                    }
                    if (!grim.PreparedSpellIds.Contains(id))
                        grim.PreparedSpellIds.Add(id);
                }
                else
                {
                    grim.PreparedSpellIds.Remove(id);
                }
                SaveManager.MarkDirty(); // Deploy() flushes via SaveIfDirty
                RebuildPrepButtons(grim, slots);
            };
            _prepFlow.AddChild(btn);
        }
    }

    private void AddDeployStat(VBoxContainer parent, string label, string value)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var l = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        l.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        l.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        row.AddChild(l);
        var v = new Label { Text = value };
        v.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        v.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        row.AddChild(v);
        parent.AddChild(row);
    }

    private void Deploy()
    {
        if (_pendingStaging == null)
            return;

        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
            return;

        // Last sortie's frontier news has now been read (it was shown on the HUD
        // when the player returned to the map). Clear it before this lunation's
        // tick appends fresh reports.
        cycle.PendingSiegeReports?.Clear();

        // Deploy-flow streamline: remember the launch point so the next Gatehouse
        // click reopens the drawer here directly.
        cycle.LastDeployStagingKey = $"{_pendingStaging.X},{_pendingStaging.Y}";

        // Expedition v2 (ruled 2026-09-21): a lunation buys ONE castle move plus
        // K field dives. So a FIELD dive must not turn the moon, or three dives
        // would burn a quarter of the cycle and the split would be a downgrade.
        // It spends a dive from this lunation's budget instead, and the world
        // does not tick.
        bool fieldDeploy = _pendingRunKind == ExpeditionRunKind.Field;
        if (fieldDeploy)
        {
            // The day clock (2026-09-23): a dive is not budgeted, it is timed.
            // The days it costs are charged when it ends; until then the party
            // is busy and cannot dive again.
            var divingParty = SelectedParty()
                              ?? cycle.FieldParties?.Find(p => p != null && p.Id == ExpeditionAnchors.PrimaryFieldPartyId);
            if (divingParty != null && WorldClock.PartyBusy(cycle, divingParty))
            {
                ShowStrategicNotice("The party is still out",
                    $"{divingParty.Name} are {divingParty.BusyUntilDay - WorldClock.Now(cycle)} day(s) from being free. Pass time.");
                return;
            }

            // A built waypoint is the consumable kind of anchor, and diving from
            // it is the only thing that consumes one. Permanent anchors ignore
            // this; SpendWaypointCharge finds nothing and says so by returning
            // false, which is not an error.
            ExpeditionAnchors.SpendWaypointCharge(cycle, _pendingStaging.X, _pendingStaging.Y);

            PlayerSession.ExpeditionRunKind = ExpeditionRunKind.Field;
            PlayerSession.ExpeditionFieldPartyId = divingParty?.Id ?? ExpeditionAnchors.PrimaryFieldPartyId;
            PlayerSession.ExpeditionStagingCol = _pendingStaging.X;
            PlayerSession.ExpeditionStagingRow = _pendingStaging.Y;
            PlayerSession.ExpeditionWindowRadius = FieldDeployWindowRadius;

            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();

            GD.Print($"[StrategicView] Field party dives from '{_pendingStaging.Name}' "
                     + $"({_pendingStaging.X},{_pendingStaging.Y}) on day {WorldClock.Now(cycle)}.");

            GetTree().ChangeSceneToFile("res://Scenes/Overworld/ExpeditionScene.tscn");
            return;
        }

        // A castle sortie. Say so explicitly rather than relying on the static
        // still holding Castle: it survives scene changes, and a field send that
        // was cancelled halfway must not make this run a field run.
        PlayerSession.ExpeditionRunKind = ExpeditionRunKind.Castle;
        PlayerSession.ExpeditionFieldPartyId = "";

        // ── Time advances on deploy: one expedition costs one whole lunation. ──
        // AdvanceLunation snaps the calendar to the next new moon, so every
        // expedition begins on The Veiled and the living world ticks exactly
        // once per deploy. The Conjunction remains a real deadline
        // (~LunationsPerCycle deploys per cycle).
        //
        // SEAM (Phase 4): eclipses land on a specific (lunation, phase). Under
        // whole-lunation deploys the calendar only ever rests on phase 0, so an
        // eclipse-interception model must key off the lunation itself (or
        // temporarily restore phase-stepping for the deploy that would cross a
        // scheduled eclipse).
        // The day clock (2026-09-23): a sortie no longer turns the moon on the
        // way out. Its days are charged when it ends, in proportion to the fuel
        // it burned, and the world moves when the player passes time across a
        // new moon. The castle is gated on being free, not on a budget.
        if (WorldClock.CastleBusy(cycle))
        {
            ShowStrategicNotice("The castle is still out",
                $"The castle is {cycle.CastleBusyUntilDay - WorldClock.Now(cycle)} day(s) from being free. Pass time.");
            return;
        }
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();

        PlayerSession.ExpeditionStagingCol = _pendingStaging.X;
        PlayerSession.ExpeditionStagingRow = _pendingStaging.Y;
        PlayerSession.ExpeditionWindowRadius = DeployWindowRadius;

        GD.Print($"[StrategicView] Deploying expedition from " +
                 $"'{_pendingStaging.Name}' ({_pendingStaging.X},{_pendingStaging.Y}). " +
                 $"Lunation {cycle.Calendar.CurrentLunation} / {cycle.Calendar.LunationsPerCycle} " +
                 $"· {cycle.Calendar.CurrentMoonName}.");

        GetTree().ChangeSceneToFile("res://Scenes/Overworld/ExpeditionScene.tscn");
    }

    /// <summary>The per-lunation world tick, in canonical order: the single
    /// place a crossed lunation boundary advances the living world. Called by
    /// Deploy (every deploy = one lunation) and by ProcessPendingStraggle
    /// (emergency-extraction debt). Order (§13): Council resolves BEFORE
    /// corruption spreads (envoy residency must read missions still live when the
    /// moon turned); kingdom drift + sieges run AFTER corruption (they are the
    /// political consequence of the tide, and read this lunation's corruption);
    /// K2 (§5b/R24): infirmary recovery last.</summary>
    private void RunLunationTick(CycleState cycle)
    {
        // Expedition v2: while the waystone is open the camp is a target. The
        // roll happens BEFORE the resupply ticks down, so a lunation the castle
        // was attacked in is a lunation of work lost, not work banked.
        var castleThreat = CastleThreats.RollForLunation(cycle);
        if (castleThreat.Happened)
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(castleThreat.Report);
            GD.Print($"[CastleThreats] {castleThreat.Kind}: {castleThreat.Report}");
        }

        // The resupply used to tick here. Under the day clock (2026-09-23) it
        // counts days from the camp in WorldClock.StepDay, so a camp made on
        // day 3 is not done at the next new moon along with one made on day 27.

        // Expedition v2: a force left IN THE FIELD while the table looked
        // elsewhere is not safe. Until this call it was: CastleThreats gates on
        // parked, FieldThreats on travelling, and a frozen sortie is neither.
        // Its news is written as a sending, and the stone flares with it the
        // next time the wizard sits down.
        string frozenReport = SortieThreats.RollForLunation(cycle);
        if (!string.IsNullOrEmpty(frozenReport))
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(frozenReport);
        }

        // Expedition v2: the road is not free. Rolled BEFORE the journey
        // advances, so a party turned back does not also step toward the place
        // it was just turned back from.
        string interceptReport = FieldThreats.RollForLunation(cycle);
        if (!string.IsNullOrEmpty(interceptReport))
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(interceptReport);
        }

        // Marches and field work step by the DAY now (WorldClock.StepDay), not
        // here. The interception roll above still fires at the new moon against
        // tiles left, which is the same exposure it always measured.

        // Field Party v1 (2026-09-24): postings pay, are rolled against, and
        // work, BEFORE the kingdom and cache ticks below so a held line or a
        // maintained siege lands in the moon it was paid for. Then the ground
        // nobody is holding rolls to be lost.
        string postingReport = FieldPostings.TickLunation(cycle);
        if (!string.IsNullOrEmpty(postingReport))
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(postingReport);
        }
        string overrunReport = PostingThreats.RollOverruns(cycle);
        if (!string.IsNullOrEmpty(overrunReport))
        {
            cycle.PendingSiegeReports ??= new System.Collections.Generic.List<string>();
            cycle.PendingSiegeReports.Add(overrunReport);
        }

        CouncilTick.Tick(cycle);
        CorruptionSpread.Tick(cycle.World, cycle.Campaign, cycle.Kingdoms);
        // Supply envy runs BEFORE the kingdom tick so cache imbalances feed the
        // same border-pressure boil-over that opens warfronts (wars erupt over
        // supply access); the harvest runs AFTER it so a province that just
        // fell pays its new master. See docs/supply_cache_spec_v1.
        SupplyCacheSystem.TickPressure(cycle);
        KingdomTickSimulation.Tick(cycle, FactionDisplay);
        SupplyCacheSystem.Tick(cycle, FactionDisplay);
        CompanionInjurySystem.TickRecovery(SaveManager.ActiveSave);
        // Q4.2: a united archmage's relic arrives when the unite moon returns.
        ArchmageRelics.TickUniteAnniversaries(cycle);
        // §8 pity-timer: advance Library research commissions; a completed one
        // unlocks its named card into the permanent draft pool. This is the ONE
        // per-lunation call site; calling it twice would double the countdown.
        CardCommissionService.TickLunation(SaveManager.ActiveSave);
    }

    /// <summary>The Grand Conjunction has arrived. For now the cycle simply ends;
    /// no final encounter yet (miniboss + campus assault are a later phase). Show a
    /// beat, then return the player to campus, where the next cycle is begun on
    /// re-entry to the strategic map (school reselection happens there).</summary>
    private void ShowConjunction()
    {
        var panelLayer = new CanvasLayer { Name = "ConjunctionUI" };
        AddChild(panelLayer);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.04f, 0.92f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        panelLayer.AddChild(backdrop);

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
            OffsetTop = -210,
            OffsetBottom = 210,
        };
        panel.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Gold));
        panelLayer.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        margin.AddChild(vbox);

        // ── The gate (R-F2 hybrid; convergence_finale_spec_v1 §3) ────────
        // Three states, and only the middle one is what shipped before v102:
        //   in-finale  → the Anchorhold was opened and abandoned; resume it.
        //   unresolved → today's press-your-luck choice, verbatim.
        //   resolved   → the Conjunction IS the Convergence.
        var gateCycle = SaveManager.ActiveSave?.Cycle;
        var gateConv = gateCycle?.Convergence;
        var gateCampaign = gateCycle?.Campaign;
        bool resolved = gateCampaign?.AllArchmagiResolved() ?? false;
        bool inFinale = gateConv?.InProgress ?? false;

        // CampaignState.FinalBattleUnlocked has had a definition since the campaign
        // layer shipped and ZERO writers. This is its first one.
        if (resolved && gateCampaign != null && !gateCampaign.FinalBattleUnlocked)
        {
            gateCampaign.FinalBattleUnlocked = true;
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
            GD.Print("[Convergence] Every seat is resolved. The Anchorhold can open.");
        }

        var title = new Label
        {
            Text = (resolved || inFinale) ? "The Convergence" : "The Grand Conjunction",
        };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        string gateBody = inFinale
            ? "You left the Anchorhold open. It is still open. Nothing else in this timeline is going to happen until you walk back through it."
            : resolved
                ? "The sky has finished reading itself. Every seat is answered: allied, bought, emptied, or lost. The script has one passage left, and it converges on a second that was never written. The Anchorhold can open its door exactly once. He will be through it before it closes. So will you."
                : "The moons align and the timeline strains toward its close. You can let it unmake: return to the campus, keep everything you have learned, and begin a new world. Or refuse the reset and hold this timeline into another year. Perfect it, and all you have built endures; but the world hardens, the corruption deepens, and a defeat now unmakes it all.";

        var body = new Label
        {
            Text = gateBody,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        body.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        body.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        vbox.AddChild(body);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        Button GateButton(string text, bool primary, System.Action onPressed)
        {
            var b = new Button
            {
                Text = text,
                CustomMinimumSize = new Vector2(300, primary ? 48 : 44),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            UITheme.ApplyButtonStyle(b, isPrimary: primary);
            b.Pressed += () => onPressed();
            return b;
        }

        void LetItUnmake()
        {
            // The permanent layer endures; the campus begins a fresh gen-1 timeline
            // (school chosen there). BeginNextCycle now reads Convergence.Outcome,
            // so declining the open door archives as "Abandoned", not a defeat.
            PlayerSession.CycleEndedByConjunction = true;
            SaveManager.SaveIfDirty();
            GetTree().ChangeSceneToFile(CampusScenePath);
        }

        if (inFinale)
        {
            vbox.AddChild(GateButton("Return to the Convergence", true, OpenAnchorhold));
        }
        else if (!resolved)
        {
            // CANONICAL press-your-luck choice (progression_persistence_model_v1 §4),
            // unchanged: while seats remain unresolved the Conjunction is still a pure
            // DEADLINE, and Continue is still earned by surviving to it.
            int nextYear = (gateCycle?.CampaignYear ?? 1) + 1;
            vbox.AddChild(GateButton($"Perfect the Timeline  (Year {nextYear})", true, () =>
            {
                // Keep the world, advance the year, harden it; then reload the
                // strategic scene, whose _Ready re-reads the preserved+escalated
                // Cycle.World. No campus round-trip and no reset.
                SaveManager.ContinueCampaign();
                PlayerSession.CycleEndedByConjunction = false;
                GetTree().ChangeSceneToFile(StrategicScenePath);
            }));
            vbox.AddChild(GateButton("Let It Unmake  (Return to Campus)", false, LetItUnmake));
        }
        else
        {
            vbox.AddChild(GateButton("Open the Anchorhold", true, OpenAnchorhold));
            vbox.AddChild(GateButton("Let It Unmake  (Return to Campus)", false, LetItUnmake));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // The Convergence, I1: gate, placeholder encounter, outcome routing
    // (docs/convergence_finale_spec_v1.md §3, §13 step 1)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Enter or resume the finale.
    ///
    /// <para>I1 ships the PLACEHOLDER form the spec asks for: one authored Siege
    /// fight, then victory/defeat routing. There is no director, no five phases and
    /// no path choice yet; those are I2 onward. What matters is that at the end of
    /// this method the campaign can be WON, which it could not be for the first
    /// hundred thousand lines of this project.</para>
    ///
    /// <para>Routes through the proven campus round-trip pattern
    /// (CampusScreen.LaunchCampusCombat): set the router's return override, hand the
    /// encounter to the carrier, swap scenes. ConsumeConvergenceReturn picks it up
    /// on the way back.</para></summary>
    private void OpenAnchorhold()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        if (cycle == null)
            return;

        if (cycle.Convergence.Phase < 1)
            cycle.Convergence.Phase = 1;
        cycle.Convergence.Outcome = "";
        SaveManager.MarkDirty();

        var def = BuildAnchorholdPlaceholder();
        if (EncounterRouter.Instance == null)
            GetTree().Root.AddChild(new EncounterRouter { Name = "EncounterRouter" });
        var router = EncounterRouter.Instance;

        if (def == null || def.Enemies.Count == 0 || router == null)
        {
            // Never dead-end the player at the one door the whole campaign points
            // at. If the roster cannot resolve, award the finale rather than
            // stranding them on a map with nothing left to do.
            GD.PrintErr("[Convergence] Anchorhold roster failed to resolve; " +
                        "awarding the victory rather than stranding the player.");
            ResolveConvergence(true);
            return;
        }

        router.HasPendingReturn = false;
        router.SavedCombatWasPatrolAmbush = false;
        router.SavedCombatPatrolArchmageId = "";
        router.SavedCombatGuardianKey = "";
        router.SavedCombatArchmageId = "";
        router.SavedResolutionArchmageId = "";
        router.ReturnSceneOverride = StrategicScenePath;
        router.SetCurrentTier(def.Tier);

        SaveManager.SaveIfDirty();
        EncounterContextCarrier.Set(def);
        EncounterContextCarrier.SetContext(def.TerrainType, def.Tier);
        GD.Print("[Convergence] The Anchorhold opens. The Fracture begins.");
        SceneTransition.Go(GetTree(), router.CombatScenePath, "The Anchorhold Opens",
            "The Fracture begins.", 1.0f);
    }

    /// <summary>The Fracture, placeholder form. Real content (waves, the Anchor ward
    /// unit, the mirror beat, per-path Thresholds) arrives with I2/I4 and the
    /// ConvergenceEncounterBuilder reading Data/Encounters/convergence.json; this
    /// builds the roster in code the way BuildCampusGuardianEncounter does, so I1
    /// depends on no new data files.
    ///
    /// <para><b>R-F1: the wall is fixed.</b> CampaignEscalation.CombatDifficultyMult
    /// is deliberately NOT applied: waiting extra years must not harden the finale,
    /// because corruption pressure is already the price of waiting. All variance is
    /// player-side, and lands with the prep ledger in I3.</para></summary>
    private static EncounterDefinition BuildAnchorholdPlaceholder()
    {
        const float FractureMult = 2.0f;   // spec §5 base for the Fracture
        string[] roster =
        {
            "astrologer_foregone",
            "astrologer_sealkeeper", "astrologer_sealkeeper",
            "astrologer_read_ahead", "astrologer_read_ahead",
        };

        var def = new EncounterDefinition
        {
            Id = "fracture_hall",
            DisplayName = "The Fracture",
            Tier = EncounterTier.Siege,
            TerrainType = "Plains",
            DifficultyMult = FractureMult,
        };
        foreach (var a in roster)
            if (UnitRegistry.TryResolveId(a, out var uid))
                def.Enemies.Add(new EnemySlot(uid, FractureMult));
        return def.Enemies.Count > 0 ? def : null;
    }

    /// <summary>Pick up a returning Convergence combat. Keyed on the router's return
    /// override being THIS scene, exactly as CampusScreen keys on its own path. An
    /// expedition combat returns with an empty override and is untouched here.</summary>
    private void ConsumeConvergenceReturn(CycleState cycle)
    {
        var router = EncounterRouter.Instance;
        if (router == null || !router.HasPendingReturn ||
            router.ReturnSceneOverride != StrategicScenePath)
            return;
        if (cycle?.Convergence == null || !cycle.Convergence.InProgress)
            return;

        bool won = router.CombatWon;
        router.HasPendingReturn = false;
        router.ReturnSceneOverride = "";
        ResolveConvergence(won);
    }

    /// <summary>Write the finale's outcome and show the beat. This is where the three
    /// TODOs the codebase carried for weeks actually resolve: Convergence.Outcome,
    /// CampaignState.CampaignComplete and CampaignState.CampaignOutcome all get their
    /// first writers, and CampusScreen.BeginNextCycle reads the first of them instead
    /// of hardcoding a defeat.</summary>
    private void ResolveConvergence(bool won)
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle?.Convergence == null)
            return;

        var conv = cycle.Convergence;
        conv.Outcome = won ? "Victory" : "Defeat";
        conv.PhaseResults[conv.Phase < 1 ? 1 : conv.Phase] = won ? "won" : "lost";

        if (cycle.Campaign != null)
        {
            cycle.Campaign.CampaignComplete = true;
            cycle.Campaign.CampaignOutcome = won ? "Victory" : "Defeat";
        }

        if (won && save?.Ledger != null)
        {
            // Permanent: survives the unmake, unlike everything else here.
            if (!save.Ledger.MetaNarrativeFlags.Contains("convergence_won"))
                save.Ledger.MetaNarrativeFlags.Add("convergence_won");
            string pathFlag = string.IsNullOrEmpty(conv.Path) ? "" : $"convergence_won_{conv.Path}";
            if (pathFlag.Length > 0 && !save.Ledger.MetaNarrativeFlags.Contains(pathFlag))
                save.Ledger.MetaNarrativeFlags.Add(pathFlag);
        }

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        GD.Print($"[Convergence] Resolved: {conv.Outcome}.");
        CallDeferred(nameof(ShowConvergenceOutcome));
    }

    /// <summary>The victory / defeat beat. Zero-arg and state-reading on purpose:
    /// it is reached through CallDeferred, matching ShowConjunction's proven shape.
    /// Copy per spec §11; the three per-path conferral variants arrive with I6.</summary>
    private void ShowConvergenceOutcome()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var conv = cycle?.Convergence;
        if (conv == null || !conv.Resolved)
            return;
        bool won = conv.Outcome == "Victory";

        var panelLayer = new CanvasLayer { Name = "ConvergenceOutcomeUI" };
        AddChild(panelLayer);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.0f, 0.04f, 0.94f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        panelLayer.AddChild(backdrop);

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
            OffsetTop = -200,
            OffsetBottom = 200,
        };
        panel.AddThemeStyleboxOverride("panel",
            UITheme.MakePanelStyle(UITheme.BgBase, won ? UITheme.Gold : UITheme.Danger));
        panelLayer.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        margin.AddChild(vbox);

        var title = new Label { Text = won ? "The Conferral Completes" : "The Anchor Takes You Back" };
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        title.AddThemeColorOverride("font_color", won ? UITheme.Gold : UITheme.Danger);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        var body = new Label
        {
            Text = won
                ? "The Long Second closes, and this time it closes on your terms. The hall lets go of the moment it has been holding since before you were a wizard. Whatever else is true of this timeline, it ended where it was always going to end, and you were the one still standing in the room."
                : "The anchor takes you back before the blow lands. The timeline closes over the Convergence like water. He does not gloat; he schedules. Somewhere behind your eyes, the Long Second holds, and everything carried inside it holds with you.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        body.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        body.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        vbox.AddChild(body);

        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        Button OutcomeButton(string text, bool primary, System.Action onPressed)
        {
            var b = new Button
            {
                Text = text,
                CustomMinimumSize = new Vector2(300, primary ? 48 : 44),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            UITheme.ApplyButtonStyle(b, isPrimary: primary);
            b.Pressed += () => onPressed();
            return b;
        }

        void ToCampus()
        {
            PlayerSession.CycleEndedByConjunction = true;
            SaveManager.SaveIfDirty();
            GetTree().ChangeSceneToFile(CampusScenePath);
        }

        if (won)
        {
            // R-F2: post-resolution, Continue is offered ONLY here, on a victory.
            int nextYear = (cycle?.CampaignYear ?? 1) + 1;
            vbox.AddChild(OutcomeButton($"Perfect the Timeline  (Year {nextYear})", true, () =>
            {
                SaveManager.ContinueCampaign();
                PlayerSession.CycleEndedByConjunction = false;
                GetTree().ChangeSceneToFile(StrategicScenePath);
            }));
            vbox.AddChild(OutcomeButton("Let It Rest", false, ToCampus));
        }
        else
        {
            // No retry inside the timeline: the wall is fixed, and the answer to a
            // defeat is another timeline with more permanent power (spec §3).
            vbox.AddChild(OutcomeButton("Return to the Campus", true, ToCampus));
        }
    }

    private string FactionDisplay(string factionId)
    {
        if (factionId == KingdomTickSimulation.GuildFactionId)
            return "the Guild";
        var def = FactionRegistry.Get(factionId);
        return def != null ? def.DisplayName : factionId;
    }

    /// <summary>A simple filled ring (octagon approximation) for staging markers.</summary>
    private static Vector2[] MakeRing(float radius)
    {
        const int n = 8;
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Tau * i / n;
            pts[i] = new Vector2(radius * Mathf.Cos(a), radius * Mathf.Sin(a));
        }
        return pts;
    }
}
