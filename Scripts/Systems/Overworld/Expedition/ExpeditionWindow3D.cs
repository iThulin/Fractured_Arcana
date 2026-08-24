using Godot;
using System.Collections.Generic;

// ============================================================
// ExpeditionWindow3D.cs
//
// Purpose:        Stage-2 PROTOTYPE — the expedition window rendered
//                 in 3D. The payoff of the convergence refactor: a
//                 renderer that takes the SAME data the live run uses
//                 (WorldData + ExpeditionFogModel + WindowOverlayModel
//                 + a party coord) and draws a bounded window (the
//                 ~469-hex disc) with fog-of-war, a party pawn,
//                 fog-gated POI markers, and click-to-move with real
//                 movement-cost feedback. Because every expedition
//                 RULE now reads through those models (Steps 1–4b),
//                 this view can drive an actual run's logic without
//                 the 2D OverworldHexGrid scene.
//
//                 Standalone: with no data injected, _Ready generates
//                 a world (StrategicView's seed), fakes a fog reveal
//                 around a start tile, and lets you walk the window —
//                 so the LOOK and FEEL can be judged before wiring
//                 deploy. SetWindow(...) injects the live run's data.
// Layer:          UI (expedition view, 3D prototype)
// Collaborators:  WorldData / ExpeditionFogModel / WindowOverlayModel
//                 (the data it renders — the run's authorities),
//                 OverworldMovementCost (WorldTile? overloads, Step 3),
//                 HexCoord (disc + neighbours), UITheme (colors),
//                 WorldAtlas3D (shares the render vocabulary),
//                 CampusWindowPanel (its host test tab)
// See:            docs/atlas_expedition_convergence_v1.md (Stage 2)
//
// COLOR NOTE: the terrain base-colour / ocean-dissolve / grade helpers
// now live in the shared Hex3DPalette (used by WorldAtlas3D too), so a
// terrain re-tune touches ONE place and both 3D views follow. Kept local
// here: fog handling, per-tile Jitter (this view salts a &0xFFFF hash —
// distinct noise from the Atlas), TileHeight, decorations, and PoiColor
// (POIType, vs the Atlas's PoiKind). The 2D StrategicView still carries
// its own copy — it renders unlit quads and retires only IF the 3D atlas
// can be scaled to serve as the strategic view.
// ============================================================

using TT = OverworldHex.TerrainType;
using Fog = OverworldHex.FogState;

/// <summary>Renders one expedition window in 3D inside its own SubViewport. Fog-aware:
/// Hidden tiles are a dark void slab, Silhouette shows terrain shape dimmed with no
/// contents, Revealed is full color with decorations + POI markers. Click an adjacent
/// non-water tile to walk the party (reveals vision as it goes); move options show the
/// true step cost via <see cref="OverworldMovementCost"/> — the same numbers the live
/// run charges. Coordinates are world OFFSET coords for the standalone harness (the
/// window is a disc of the world); the live wiring maps local→world in the manager.</summary>
public partial class ExpeditionWindow3D : Node3D
{
    // ── Layout (flat-top, odd-q — matches WorldAtlas3D / HexCoord) ──────────
    private const float HexR = 1.0f;
    private const float FogSlabHeight = 0.25f;   // flat height for unexplored (Hidden) fog tiles
    private static readonly float ColSpacing = 1.5f * HexR;
    private static readonly float RowSpacing = Mathf.Sqrt(3f) * HexR;

    private const float VoidSlabHeight = 0.06f;
    private const float TerraceSteps = 5f;

    // ── Config ──────────────────────────────────────────────────────────────
    [Export] public bool Standalone = true;
    [Export] public int StandaloneSeed = 12345;
    [Export] public string StandaloneSchool = "Elementalist";
    [Export] public int WindowRadius = 12;
    [Export] public int VisionRadius = 2;   // reveal radius as the party moves

    // ── Data (the run's authorities) ────────────────────────────────────────
    private WorldData _world;
    private ExpeditionFogModel _fog;
    private WindowOverlayModel _overlay;
    private Vector2I _center;   // window center, offset coords
    private Vector2I _party;    // party position, offset coords
    private List<Vector2I> _windowTiles = new();

    public bool AcceptInput = false;

    /// <summary>Standalone self-drives its own party on click (the harness). When a
    /// LIVE run hosts this, the host sets SelfDrive=false and listens to
    /// <see cref="MoveRequested"/> so the REAL ExpeditionManager.TryMoveTo advances
    /// the run (charging cost, revealing fog, triggering POIs) — then the host
    /// re-feeds the updated models via SetWindow.</summary>
    public bool SelfDrive = true;

    /// <summary>Fired when the party moves, with the new coord — the host updates
    /// its readout. A plain event (the host may not be a Node).</summary>
    public event System.Action<Vector2I> PartyMoved;

    /// <summary>Fired (live mode only) when the player clicks a legal adjacent tile —
    /// the host translates it to a run move. The coord is in the renderer's space
    /// (world-offset for standalone; the host feeds/reads the same space).</summary>
    public event System.Action<Vector2I> MoveRequested;

    /// <summary>Fired as the mouse moves over a tile (world-offset coord), so the host can drive the
    /// tile tooltip the 2D grid drives via HexHovered. <see cref="TileUnhovered"/> fires when the
    /// cursor leaves all tiles.</summary>
    public event System.Action<Vector2I> TileHovered;
    public event System.Action TileUnhovered;

    // ── Scene ───────────────────────────────────────────────────────────────
    private Camera3D _camera;
    private GeometryInstance3D _landLayer;      // welded ArrayMesh (stage 2) or MultiMesh fallback
    private MultiMeshInstance3D _waterLayer;
    private GeometryInstance3D _canvasLayer;    // Hidden fog = unpainted canvas (welded sheet or MultiMesh fallback)
    private Node3D _mistLayer;                  // 2026-08-21 rev 2: volumetric mist stack (deck + 2 wisp sheets)
    private readonly List<ShaderMaterial> _mistMats = new();   // deck, wispA, wispB — restyled live by ApplySurround
    private MeshInstance3D _riverLayer;         // A9b: one winding ribbon mesh (RiverMesh)
    private MeshInstance3D _roadLayer;          // stage 2: ground-following ribbon too
    private readonly List<Node3D> _decor = new();
    private readonly List<Node3D> _markers = new();
    private readonly List<Node3D> _entities = new();   // moving entities: enemy patrols + roamer
    private readonly List<Node3D> _moveHints = new();
    private readonly List<Node3D> _stridePath = new();   // §3.4 stride-order preview ribbon
    private Node3D _pawn;

    private Vector3 _camTarget = Vector3.Zero;
    private float _camDist = 26f;
    private float _camYaw = 0f;   // camera orbit yaw (Q/E rotate); 0 = looking down +Z as before
    // Max zoom is the Inspector-editable MaxZoom export (default 36, was a const
    // 60) so pulling out frames the projection + table + figures instead of
    // shrinking the map to a dot in a black chamber.
    private const float CamDistMin = 8f;
    private const float CamRotateSpeed = 1.8f;   // rad/s for Q/E
    private const float CamPanSpeed = 1.1f;      // WASD pan speed as a fraction of zoom distance per second
    // Deadzone "leash": how far (as a fraction of the current zoom distance, so it
    // stays screen-relative) the party may drift from the camera focus before the
    // camera eases to follow. Inside this radius the world holds still and the pawn
    // walks; past it the camera trails just enough to keep the pawn in frame.
    private const float CamLeashFactor = 0.42f;
    private bool _dragging, _dragMoved;

    public override void _Ready()
    {
        BuildEnvironment();
        _camera = new Camera3D { Name = "WindowCamera", Far = 400f };
        AddChild(_camera);

        if (_world == null && Standalone)
            GenerateStandalone();
        if (_world != null)
            RebuildAll(frameCamera: true);
        // Standalone preview (F6 in the editor): drive the camera so the scene
        // can be inspected/tuned. In-game the host sets AcceptInput on hover.
        if (Standalone)
            AcceptInput = true;
    }

    /// <summary>Inject the live run's data and render. The thesis in one call: the same
    /// models the 2D run drives, handed to a 3D view.</summary>
    public void SetWindow(WorldData world, ExpeditionFogModel fog, WindowOverlayModel overlay,
                          Vector2I center, Vector2I party, bool frameCamera = true)
    {
        _world = world; _fog = fog; _overlay = overlay;
        _center = center; _party = party;
        ComputeWindowTiles();
        if (IsInsideTree())
            RebuildAll(frameCamera);
    }

    /// <summary>Fill _windowTiles (Vector2I) from the disc around _center. WorldData.Disc
    /// returns (col,row) tuples; the renderer works in Vector2I throughout.</summary>
    private void ComputeWindowTiles()
    {
        _windowTiles = new List<Vector2I>();
        foreach (var (c, r) in _world.Disc(_center.X, _center.Y, WindowRadius))
            _windowTiles.Add(new Vector2I(c, r));
    }

    public string DescribeParty()
    {
        if (_world == null) return "";
        var t = _world.GetTile(_party.X, _party.Y);
        var f = _fog.FogAt(_party);
        var ov = _overlay.OverlayAt(_party);
        var parts = new List<string> { $"party ({_party.X},{_party.Y})", t.Terrain.ToString(), f.ToString() };
        if (ov.Poi != OverworldHex.POIType.None) parts.Add($"POI: {ov.Poi}{(ov.Consumed ? " (used)" : "")}");
        int revealed = 0; foreach (var c in _windowTiles) if (_fog.FogAt(c) == Fog.Revealed) revealed++;
        parts.Add($"{revealed}/{_windowTiles.Count} revealed");
        return string.Join("  ·  ", parts);
    }

    // ── Standalone harness ──────────────────────────────────────────────────

    private void GenerateStandalone()
    {
        var gen = WorldGenerator.Generate(StandaloneSeed, StandaloneSchool);
        _world = gen.World;

        // Start at a staging point if the world has one, else the first land tile
        // near the middle — a plausible deploy origin for the feel test.
        _center = PickStart();
        _party = _center;

        _fog = new ExpeditionFogModel();
        _overlay = new WindowOverlayModel();
        ComputeWindowTiles();

        // Seed overlay POIs from the world's POI table for tiles in the disc.
        foreach (var c in _windowTiles)
        {
            var poi = _world.PoiAt(c.X, c.Y);
            if (poi != null)
                _overlay.Set(c, new TileOverlay { Poi = MapKind(poi.Kind), Consumed = poi.Consumed });
        }
        // All window tiles start Hidden; the opening vision reveal fills the core.
        foreach (var c in _windowTiles) _fog.Set(c, Fog.Hidden);
        UpdateVision();
    }

    private Vector2I PickStart()
    {
        if (_world.StagingPoints != null)
            foreach (var sp in _world.StagingPoints)
                if (_world.InBounds(sp.X, sp.Y) && _world.GetTile(sp.X, sp.Y).IsLand)
                    return new Vector2I(sp.X, sp.Y);
        // Fallback: spiral out from the world centre for the first land tile.
        int cx = _world.Width / 2, cy = _world.Height / 2;
        for (int r = 0; r < Mathf.Max(_world.Width, _world.Height); r++)
            foreach (var (c, rr) in HexCoord.Disc(cx, cy, r, _world.Width, _world.Height))
                if (_world.GetTile(c, rr).IsLand)
                    return new Vector2I(c, rr);
        return new Vector2I(cx, cy);
    }

    /// <summary>PoiKind → the window POIType the overlay carries, mirroring the
    /// live StampCivicPois/window mapping closely enough for the harness.</summary>
    private static OverworldHex.POIType MapKind(PoiKind k) => k switch
    {
        PoiKind.Combat => OverworldHex.POIType.Combat,
        PoiKind.Rest => OverworldHex.POIType.Rest,
        PoiKind.Narrative => OverworldHex.POIType.Narrative,
        PoiKind.Negotiation => OverworldHex.POIType.Negotiation,
        PoiKind.Outpost => OverworldHex.POIType.Outpost,
        PoiKind.Seat => OverworldHex.POIType.Seat,
        PoiKind.Settlement => OverworldHex.POIType.Settlement,
        PoiKind.SupplyCache => OverworldHex.POIType.SupplyCache,
        PoiKind.Companion => OverworldHex.POIType.Narrative, // K3 rescue sites
        _ => OverworldHex.POIType.Combat,
    };

    /// <summary>Radius reveal around the party (mirrors FogOfWarManager.UpdateVision):
    /// within VisionRadius → Revealed, one ring beyond & still Hidden → Silhouette.</summary>
    private void UpdateVision()
    {
        // Shared scry modifier (weather penalty, Arcanist/Lens/Farseeing bonuses),
        // floored at 1 so the adjacent ring the castle can step into is always
        // scried — weather blinds the far lens, never the immediate one.
        int vr = Mathf.Max(1, VisionRadius + VisionModifiers.ScryBonus);
        foreach (var c in _windowTiles)
        {
            int d = HexCoord.OffsetDistance(c.X, c.Y, _party.X, _party.Y);
            var cur = _fog.FogAt(c);
            if (d <= vr) _fog.Set(c, Fog.Revealed);
            else if (d <= vr + 1 && cur == Fog.Hidden) _fog.Set(c, Fog.Silhouette);
        }
    }

    // ── Environment + camera ────────────────────────────────────────────────

    private void BuildEnvironment()
    {
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = UITheme.WorldDeep,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            // A4b: same daylight rig as the strategic atlas — the two 3D views
            // must light the shared A1 palette identically or the colours
            // drift apart. Exposure per the atlas's A4b note: ≈0.97 total on
            // flat tops at ~2.3:1 key-to-fill.
            AmbientLightColor = new Color(0.55f, 0.56f, 0.60f),
            AmbientLightEnergy = 0.5f,
        };
        _baseAmbient = _env.AmbientLightColor;   // W4: weather tint lerps from this
        _haveBaseAmbient = true;
        AddChild(new WorldEnvironment { Environment = _env });
        ApplySurround();
        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.97f, 0.90f, 1f),
            LightEnergy = 1.0f,
            ShadowEnabled = true,
            // SHADOW-MAP HYGIENE (the "lines everywhere + spotted shadows"
            // fix). The old rig projected acne artifacts across every tile:
            // - MaxDistance 120 over a ~50-unit scene wasted >half the depth
            //   range → coarse texels → their grid stamped as regularly
            //   spaced straight stripes on the gently sloped tops (the toon
            //   light() multiplies raw ATTENUATION at full albedo contrast,
            //   so acne shows far harder than under PBR).
            // - Blur 3 spread the PCF sampling noise into a visible stipple
            //   ("spotted shadows") instead of hiding it.
            // - Orthogonal single-split: uniform texel density, no split
            //   seams, max precision for a small fixed-size scene.
            DirectionalShadowMaxDistance = 45f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            ShadowBlur = 1.0f,
            ShadowBias = 0.3f,
            ShadowNormalBias = 3.0f,
        };
        AddChild(sun);
        sun.RotationDegrees = new Vector3(-45f, -40f, 0f);
        _sun = sun;
    }

    // ── Surround / unexplored look (three switchable styles, cycle with B) ───
    // The world used to be a hard-edged pale slab floating in black. Each style
    // reconfigures FOUR things together so it reads as one intentional look:
    // the background colour, distance fog (dissolves the mesh edge so there's
    // no floating rectangle), the unexplored-tile tone (so unknown ground is
    // obvious), and the rim colour the ground fades toward. Cycled live so the
    // look can be judged in-scene; the mesh recolours + refogs on each switch.
    public enum SurroundStyle { Haze, Desk, Vignette }
    // Default is the dark chamber (Vignette) — the scrying-table setting. B still
    // cycles to Haze/Desk for comparison.
    private SurroundStyle _surround = SurroundStyle.Vignette;
    private Godot.Environment _env;

    // ── W4: weather VFX (per-front particle emitters + ambient tint) ─────────
    private Node3D _weatherLayer;
    private CpuParticles3D[] _weatherEmitters;
    private MeshInstance3D[] _weatherClouds;   // visible moving cloud mass per front
    private double _wxAccum;                 // throttle accumulator
    private Color _baseAmbient;              // captured in BuildEnvironment
    private bool _haveBaseAmbient;
    private const float WeatherEmitHeight = 5.0f;   // precip fall height above the tile tops
    private const float WeatherCloudHeight = 3.2f;  // cloud-layer height above the tile tops
    /// <summary>Colour the ground fades toward at its rim (set per style) — the
    /// heightmap lerps edge vertices to this so the boundary dissolves.</summary>
    private Color _surroundEdge = new Color(0.92f, 0.89f, 0.82f);

    // ── Scrying table rig (Phase 1: glowing projection over a table, watched by
    //    stand-in companions in a dark chamber; free camera keeps the table as a
    //    peripheral frame). The map disc is the "scried projection" hovering just
    //    above a round table; a ring of stylised figures stands around it. ──────
    private Node3D _scryRig;
    private float _mapCenterX, _mapCenterZ, _mapDiscR;   // set by BuildHeightmapSurface

    // ── Inspector tuning (Path A) ────────────────────────────────────────────
    // Editable in the Godot Inspector on the ExpeditionWindow3D node; grouped so
    // the scrying-view knobs are all in one place. Tweak, re-run (F6 to preview
    // standalone), see the change — no code edits.
    [ExportGroup("Scrying Chamber")]
    [Export] public Color ChamberBackground = new Color(0.035f, 0.045f, 0.065f);
    // Playtest 2026-08-21 ("too dark in general"): the map readability knobs are
    // the CHAMBER's, not the shared daylight rig — the base sun/ambient stay at
    // the A4b atlas-parity values so the two 3D views keep lighting the A1
    // palette identically. Fog density down (dark distance fog was eating the
    // projection), chamber ambient up.
    [Export] public float ChamberFogDensity = 0.032f;
    [Export] public float ChamberAmbientEnergy = 0.62f;
    /// <summary>Arcane glow shared by the projection rim, the chamber light, and
    /// the figures' under-light, so the scene reads as one magical source.</summary>
    [Export] public Color ArcaneGlow = new Color(0.42f, 0.68f, 0.92f);

    [ExportGroup("Scrying Table")]
    [Export] public Color TableColor = new Color(0.20f, 0.16f, 0.14f);
    [Export] public Color FloorColor = new Color(0.11f, 0.11f, 0.14f);
    [Export] public float TableTopY = -0.35f;   // just below the map ⇒ projection floats
    [Export] public float FloorY = -3.2f;       // chamber floor the figures stand on
    [Export] public float ProjectionRimEnergy = 3.4f;
    [Export] public float GlowEnergy = 3.6f;   // was 3.2 — part of the 2026-08-21 brightness pass

    [ExportGroup("Companions")]
    [Export] public int CompanionCount = 5;
    [Export] public float CompanionHeight = 3.2f;
    [Export] public float CompanionRingMargin = 3.0f;   // ring radius = map radius + this
    [Export] public Color CompanionRobe = new Color(0.10f, 0.10f, 0.13f);

    [ExportGroup("Camera")]
    [Export] public float MaxZoom = 36f;

    private void CycleSurround()
    {
        _surround = (SurroundStyle)(((int)_surround + 1) % 3);
        GD.Print($"[ExpeditionWindow3D] Surround style: {_surround}");
        ApplySurround();
        if (_world != null) RebuildTiles();   // unexplored tone + rim fade depend on style
    }

    /// <summary>Configure the environment (background + fog) and the rim-fade
    /// colour for the active style. The unexplored TONE is applied in
    /// BuildFieldData via <see cref="StyleUnexplored"/>.</summary>
    private void ApplySurround()
    {
        if (_env == null) return;
        switch (_surround)
        {
            case SurroundStyle.Haze:
                // Warm morning mist: ground dissolves into a soft sky, unexplored
                // reads as pale fog the painted world emerges from.
                _surroundEdge = new Color(0.91f, 0.88f, 0.81f);
                _env.BackgroundColor = new Color(0.90f, 0.88f, 0.83f);
                _env.AmbientLightColor = new Color(0.60f, 0.61f, 0.63f);
                _env.AmbientLightEnergy = 0.55f;
                _env.FogEnabled = true;
                _env.FogLightColor = new Color(0.92f, 0.89f, 0.83f);
                _env.FogLightEnergy = 1.0f;
                _env.FogDensity = 0.035f;
                break;
            case SurroundStyle.Desk:
                // Cartographer's artifact: the map sits on a dark warm surface,
                // its edges fading into the desk; unexplored is blank parchment.
                _surroundEdge = new Color(0.20f, 0.16f, 0.12f);
                _env.BackgroundColor = new Color(0.16f, 0.13f, 0.10f);
                _env.AmbientLightColor = new Color(0.52f, 0.52f, 0.54f);
                _env.AmbientLightEnergy = 0.5f;
                _env.FogEnabled = true;
                _env.FogLightColor = new Color(0.18f, 0.145f, 0.11f);
                _env.FogLightEnergy = 1.0f;
                _env.FogDensity = 0.05f;
                break;
            case SurroundStyle.Vignette:
                // Scrying chamber: a dark room the projection glows in. Driven by
                // the Inspector-editable Chamber exports. The map rim fades to a
                // faint arcane tone (not black), so the edge reads as the
                // projection's light spilling into the dark.
                _surroundEdge = new Color(0.10f, 0.16f, 0.22f);
                _env.BackgroundColor = ChamberBackground;
                _env.AmbientLightColor = new Color(0.42f, 0.47f, 0.58f);
                _env.AmbientLightEnergy = ChamberAmbientEnergy;
                _env.FogEnabled = true;
                _env.FogLightColor = ChamberBackground.Darkened(0.2f);
                _env.FogLightEnergy = 1.0f;
                _env.FogDensity = ChamberFogDensity;
                break;
        }
        ApplyMistStyle();   // the mist must match the active look (B-cycle safe)
    }

    /// <summary>Restyle an unexplored (canvas) tile's tone so unknown ground is
    /// obvious and matches the active surround.</summary>
    private Color StyleUnexplored(Color canvas) => _surround switch
    {
        // Pale mist — lift toward white so the painted world pops against it.
        SurroundStyle.Haze => canvas.Lerp(new Color(0.95f, 0.94f, 0.91f), 0.45f),
        // Flat blank parchment — warmer/greyer than painted ground.
        SurroundStyle.Desk => canvas.Lerp(new Color(0.80f, 0.74f, 0.62f), 0.5f),
        // Sink toward shadow so unexplored recedes.
        SurroundStyle.Vignette => canvas.Darkened(0.5f),
        _ => canvas,
    };

    private DirectionalLight3D _sun;
    private int _debugViz;

    /// <summary>V-key diagnostic: cycles isolation modes so the layer painting
    /// an artifact identifies itself live — no more inferring from screenshots.
    /// 0 all on · 1 sun shadows OFF · 2 toon banding OFF (smooth light) ·
    /// 3 grain OFF · 4 colour map OFF (vertex colours) · 5 UNSHADED albedo.
    /// The mode at which the artifact vanishes names its source.</summary>
    private void ApplyDebugViz()
    {
        string[] names = { "all on (normal)", "sun shadows OFF", "toon banding OFF (smooth light)",
                           "grain OFF", "color map OFF (vertex colours)", "UNSHADED (flat albedo)",
                           "CRACK TEST (uniform grey — surviving lines = mesh gaps/MSAA)" };
        GD.Print($"[ExpeditionWindow3D] V debug {_debugViz}: {names[_debugViz]}");
        if (_sun != null)
            _sun.ShadowEnabled = _debugViz != 1;
        if (_landLayer is MeshInstance3D mi && mi.MaterialOverride is ShaderMaterial m)
        {
            m.SetShaderParameter("debug_mode", (_debugViz == 2 || _debugViz == 5 || _debugViz == 6) ? _debugViz : 0);
            // grain is OFF on the heightmap (0); mode 3 keeps it off, every other
            // mode restores that same 0 — cycling back never reintroduces grain.
            m.SetShaderParameter("grain_strength", 0f);
        }
    }

    private Vector3 TileOrigin(int col, int row)
        => new Vector3(col * ColSpacing, 0f,
                       row * RowSpacing + (((col & 1) == 1) ? RowSpacing * 0.5f : 0f));

    // ── W4: weather 3D visuals ───────────────────────────────────────────────
    // Per-front CpuParticles3D emitters follow the moving fronts (W1), styled by
    // type (W1/W2), plus a subtle ambient tint when the castle sits under a front.
    // The field lives in local render space (HexCoord.OffsetRenderPosition, unit
    // spacing); TileOrigin scales that same odd-q grid by ColSpacing/RowSpacing,
    // so a front centre maps to 3D by the same scale — no tile lookup needed.

    private Vector3 FrontToWorld(Vector2 center, float height)
        => new Vector3(center.X * ColSpacing, height, center.Y * RowSpacing);

    private void EnsureWeatherEmitters()
    {
        if (_weatherLayer != null && GodotObject.IsInstanceValid(_weatherLayer))
            return;
        _weatherLayer = new Node3D { Name = "WeatherVfx" };
        AddChild(_weatherLayer);
        int n = Mathf.Max(1, WeatherCatalog.FrontCount);
        _weatherEmitters = new CpuParticles3D[n];
        _weatherClouds = new MeshInstance3D[n];
        for (int i = 0; i < n; i++)
        {
            // Precipitation emitter — quads big enough to read at map distance.
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlbedoColor = new Color(1f, 1f, 1f, 0.8f),
                VertexColorUseAsAlbedo = false,
            };
            var quad = new QuadMesh { Size = new Vector2(0.35f, 0.35f), Material = mat };
            var p = new CpuParticles3D
            {
                Name = $"WxFront{i}",
                Mesh = quad,
                Emitting = false,
                Amount = 90,
                Lifetime = 1.2f,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(3f, 0.3f, 3f),
                Gravity = new Vector3(0f, -18f, 0f),
                Direction = new Vector3(0f, -1f, 0f),
                Spread = 6f,
            };
            _weatherLayer.AddChild(p);
            _weatherEmitters[i] = p;

            // Cloud mass — a flat translucent plane hovering over the front, the
            // clearly-visible "storm cloud" that drifts across the map. Horizontal
            // (PlaneMesh lies in XZ), two-sided, no billboard so it stays a layer.
            var cmat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(0.3f, 0.3f, 0.35f, 0.0f),
            };
            var plane = new PlaneMesh { Size = new Vector2(2f, 2f), Material = cmat };
            var cloud = new MeshInstance3D { Name = $"WxCloud{i}", Mesh = plane, Visible = false };
            _weatherLayer.AddChild(cloud);
            _weatherClouds[i] = cloud;
        }
    }

    /// <summary>Sync the front emitters + cloud planes to the live field and tint
    /// the ambient. Throttled — the field only advances on a committed stride, so
    /// ~6 Hz is ample and keeps the per-frame cost negligible.</summary>
    private void UpdateWeatherVfx(float dt)
    {
        _wxAccum += dt;
        if (_wxAccum < 0.16)
            return;
        _wxAccum = 0;

        EnsureWeatherEmitters();
        var fronts = WeatherSystem.Fronts;
        int shown = WeatherSystem.Active ? fronts.Count : 0;

        for (int i = 0; i < _weatherEmitters.Length; i++)
        {
            var p = _weatherEmitters[i];
            var cloud = _weatherClouds[i];
            if (i >= shown || fronts[i].Type == WeatherType.Clear)
            {
                if (p.Emitting) p.Emitting = false;
                if (cloud.Visible) cloud.Visible = false;
                continue;
            }
            var f = fronts[i];
            float rx = Mathf.Max(1.5f, f.Radius * ColSpacing);

            // Precipitation.
            p.Position = FrontToWorld(f.Center, WeatherEmitHeight);
            p.EmissionBoxExtents = new Vector3(rx, 0.3f, rx);
            StyleWeatherEmitter(p, f.Type, rx);
            if (!p.Emitting) p.Emitting = true;

            // Cloud mass, sized to the front and coloured by type.
            cloud.Position = FrontToWorld(f.Center, WeatherCloudHeight);
            cloud.Scale = new Vector3(rx, 1f, rx);   // PlaneMesh half-size 1 → radius rx
            StyleWeatherCloud(cloud, f.Type);
            if (!cloud.Visible) cloud.Visible = true;
        }

        // Ambient tint: nudge toward the front's colour by severity when the
        // castle sits under weather. Reversible (lerps from the captured base).
        if (_haveBaseAmbient && _env != null && WeatherSystem.Active)
        {
            var wt = WeatherSystem.WeatherAt(_party);
            var wd = WeatherCatalog.Def(wt);
            float k = Mathf.Clamp(wd.Severity * 0.06f, 0f, 0.22f);
            _env.AmbientLightColor = _baseAmbient.Lerp(WeatherTint(wt), k);
        }
        else if (_haveBaseAmbient && _env != null)
        {
            _env.AmbientLightColor = _baseAmbient;
        }
    }

    private static Color WeatherTint(WeatherType t) => t switch
    {
        WeatherType.Storm    => new Color(0.30f, 0.34f, 0.45f),
        WeatherType.Blizzard => new Color(0.72f, 0.78f, 0.88f),
        WeatherType.Ashfall  => new Color(0.34f, 0.30f, 0.28f),
        WeatherType.Rain     => new Color(0.40f, 0.46f, 0.56f),
        WeatherType.Fog      => new Color(0.60f, 0.62f, 0.66f),
        WeatherType.Gale     => new Color(0.52f, 0.56f, 0.58f),
        _                    => new Color(0.55f, 0.56f, 0.60f),
    };

    private void StyleWeatherEmitter(CpuParticles3D p, WeatherType t, float rx)
    {
        // Amount scales with the covered area (bigger fronts, more particles).
        int baseAmt = t switch
        {
            WeatherType.Storm    => 120,
            WeatherType.Rain     => 80,
            WeatherType.Blizzard => 70,
            WeatherType.Ashfall  => 55,
            WeatherType.Gale     => 40,
            WeatherType.Fog      => 40,
            _                    => 0,
        };
        p.Amount = Mathf.Max(8, (int)(baseAmt * Mathf.Clamp(rx / (5f * ColSpacing), 0.5f, 2f)));

        Color col;
        switch (t)
        {
            case WeatherType.Storm:
                col = new Color(0.72f, 0.80f, 1.0f, 0.85f);
                p.Lifetime = 0.7f; p.Gravity = new Vector3(0f, -26f, 0f);
                p.InitialVelocityMin = 10f; p.InitialVelocityMax = 16f;
                p.ScaleAmountMin = 1.4f; p.ScaleAmountMax = 2.4f; p.Spread = 4f;
                break;
            case WeatherType.Rain:
                col = new Color(0.64f, 0.74f, 1.0f, 0.7f);
                p.Lifetime = 0.9f; p.Gravity = new Vector3(0f, -20f, 0f);
                p.InitialVelocityMin = 6f; p.InitialVelocityMax = 10f;
                p.ScaleAmountMin = 1.1f; p.ScaleAmountMax = 1.9f; p.Spread = 5f;
                break;
            case WeatherType.Blizzard:
                col = new Color(0.95f, 0.97f, 1.0f, 0.85f);
                p.Lifetime = 3.0f; p.Gravity = new Vector3(0f, -2.2f, 0f);
                p.InitialVelocityMin = 0.5f; p.InitialVelocityMax = 1.5f;
                p.ScaleAmountMin = 0.8f; p.ScaleAmountMax = 1.4f; p.Spread = 30f;
                break;
            case WeatherType.Ashfall:
                col = new Color(0.32f, 0.30f, 0.30f, 0.75f);
                p.Lifetime = 2.6f; p.Gravity = new Vector3(0f, -2.8f, 0f);
                p.InitialVelocityMin = 0.4f; p.InitialVelocityMax = 1.2f;
                p.ScaleAmountMin = 0.7f; p.ScaleAmountMax = 1.2f; p.Spread = 25f;
                break;
            case WeatherType.Gale:
                col = new Color(0.80f, 0.84f, 0.88f, 0.5f);
                p.Lifetime = 1.0f; p.Gravity = new Vector3(0f, -1.0f, 0f);
                p.Direction = new Vector3(1f, -0.15f, 0.3f);
                p.InitialVelocityMin = 10f; p.InitialVelocityMax = 18f;
                p.ScaleAmountMin = 0.5f; p.ScaleAmountMax = 0.9f; p.Spread = 12f;
                break;
            case WeatherType.Fog:
            default:
                col = new Color(0.85f, 0.87f, 0.90f, 0.32f);
                p.Lifetime = 4.0f; p.Gravity = new Vector3(0f, 0.1f, 0f);
                p.InitialVelocityMin = 0.1f; p.InitialVelocityMax = 0.5f;
                p.ScaleAmountMin = 3.0f; p.ScaleAmountMax = 5.0f; p.Spread = 40f;
                break;
        }
        p.Color = col;
        if (p.Mesh is QuadMesh qm && qm.Material is StandardMaterial3D sm)
            sm.AlbedoColor = col;
    }

    /// <summary>Colour + opacity of a front's cloud plane by type. Storm reads
    /// dark and heavy; blizzard/fog pale; ashfall dim brown; gale faint.</summary>
    private void StyleWeatherCloud(MeshInstance3D cloud, WeatherType t)
    {
        Color c = t switch
        {
            WeatherType.Storm    => new Color(0.16f, 0.18f, 0.26f, 0.62f),
            WeatherType.Blizzard => new Color(0.88f, 0.91f, 0.97f, 0.52f),
            WeatherType.Ashfall  => new Color(0.24f, 0.21f, 0.20f, 0.58f),
            WeatherType.Rain     => new Color(0.34f, 0.39f, 0.47f, 0.44f),
            WeatherType.Gale     => new Color(0.55f, 0.58f, 0.62f, 0.24f),
            WeatherType.Fog      => new Color(0.82f, 0.84f, 0.88f, 0.44f),
            _                    => new Color(0.5f, 0.5f, 0.5f, 0f),
        };
        if (cloud.Mesh is PlaneMesh pm && pm.Material is StandardMaterial3D sm)
            sm.AlbedoColor = c;
    }

    private void FrameCamera()
    {
        // Start close on the party — an expedition is walked, not surveyed. The
        // revealed bubble (~7 tiles across) should fill the frame, not float in it.
        _camTarget = TileOrigin(_party.X, _party.Y);
        _camDist = 13f;
        PlaceCamera();
    }

    private void PlaceCamera()
    {
        float zoom01 = Mathf.InverseLerp(CamDistMin, MaxZoom, _camDist);
        float pitch = Mathf.DegToRad(Mathf.Lerp(38f, 60f, zoom01));
        // Base orbit offset (behind + above), yawed around the focus so Q/E rotate the view.
        float cp = Mathf.Cos(pitch), sp = Mathf.Sin(pitch);
        float cy = Mathf.Cos(_camYaw), sy = Mathf.Sin(_camYaw);
        Vector3 offset = new Vector3(cp * sy, sp, cp * cy) * _camDist;
        _camera.Position = _camTarget + offset;
        _camera.LookAt(_camTarget, Vector3.Up);
    }

    public override void _Process(double delta)
    {
        UpdateWeatherVfx((float)delta);   // W4: weather VFX animate regardless of input focus
        if (!AcceptInput || _camera == null) return;
        float dt = (float)delta;
        bool moved = false;

        // Q/E rotate the camera around its focus.
        if (Input.IsKeyPressed(Key.Q)) { _camYaw -= CamRotateSpeed * dt; moved = true; }
        if (Input.IsKeyPressed(Key.E)) { _camYaw += CamRotateSpeed * dt; moved = true; }

        // WASD pan the focus across the ground, relative to the current facing (yaw).
        float cy = Mathf.Cos(_camYaw), sy = Mathf.Sin(_camYaw);
        Vector2 fwd = new Vector2(-sy, -cy);     // into the screen, on the ground
        Vector2 right = new Vector2(cy, -sy);
        Vector2 pan = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W)) pan += fwd;
        if (Input.IsKeyPressed(Key.S)) pan -= fwd;
        if (Input.IsKeyPressed(Key.D)) pan += right;
        if (Input.IsKeyPressed(Key.A)) pan -= right;
        if (pan != Vector2.Zero)
        {
            pan = pan.Normalized() * (CamPanSpeed * _camDist * dt);
            _camTarget += new Vector3(pan.X, 0f, pan.Y);
            moved = true;
        }

        if (moved) PlaceCamera();
    }

    /// <summary>Lazy deadzone follow. If the party sits within CamLeashFactor·zoom of
    /// the camera focus, do nothing — the world stays put and the pawn walks across it
    /// (the treadmill fix). Only once a step carries the party past that leash does the
    /// camera ease, and only far enough to bring the pawn back to the leash edge — never
    /// a full recenter. Tweens _camTarget via a method callback (it's a plain field, not
    /// a Godot property) so PlaceCamera runs each interpolation step.</summary>
    private void LeashCameraToParty()
    {
        if (_camera == null) return;
        Vector3 pawnGround = TileOrigin(_party.X, _party.Y);   // frame on ground, ignore height
        var offset = new Vector2(pawnGround.X - _camTarget.X, pawnGround.Z - _camTarget.Z);
        float dist = offset.Length();
        float leash = _camDist * CamLeashFactor;
        if (dist <= leash) return;   // inside the deadzone — hold the world still

        Vector2 pull = offset.Normalized() * (dist - leash);
        Vector3 to = _camTarget + new Vector3(pull.X, 0f, pull.Y);
        var tw = CreateTween();
        tw.TweenMethod(Callable.From((Vector3 v) => { _camTarget = v; PlaceCamera(); }),
                       _camTarget, to, 0.18)
          .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
    }

    // ── Input ────────────────────────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent ev)
    {
        // V debug-viz cycler works whenever the view exists (before the
        // AcceptInput gate) — it's a diagnostic, not a game input. (F8 was a
        // mistake: it's the editor's Stop-project shortcut and killed the run.)
        if (ev is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.V)
        {
            _debugViz = (_debugViz + 1) % 7;
            ApplyDebugViz();
            return;
        }
        // B cycles the surround style (Haze → Desk → Vignette) live.
        if (ev is InputEventKey bkey && bkey.Pressed && !bkey.Echo && bkey.Keycode == Key.B)
        {
            CycleSurround();
            return;
        }
        if (!AcceptInput || _camera == null) return;
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 0.9f, CamDistMin, MaxZoom); PlaceCamera(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 1.1f, CamDistMin, MaxZoom); PlaceCamera(); }
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _dragging = true; _dragMoved = false; }
                else { if (_dragging && !_dragMoved) PickAndMove(mb.Position); _dragging = false; }
            }
        }
        else if (ev is InputEventMouseMotion mm)
        {
            if (_dragging)
            {
                if (mm.Relative.LengthSquared() > 1f) _dragMoved = true;
                float k = _camDist * 0.0016f;
                float pitchSin = Mathf.Max(0.3f, (_camera.Position - _camTarget).Normalized().Y);
                _camTarget += new Vector3(-mm.Relative.X * k, 0f, -mm.Relative.Y * k / pitchSin);
                PlaceCamera();
            }
            else if (TryPickTile(mm.Position, out var hov))
                TileHovered?.Invoke(hov);
            else
                TileUnhovered?.Invoke();
        }
    }

    private void PickAndMove(Vector2 screenPos)
    {
        if (!TryPickTile(screenPos, out var best))
            return;
        if (_world.GetTile(best.X, best.Y).IsWater) return;   // water always blocks

        bool adjacent = HexCoord.OffsetDistance(best.X, best.Y, _party.X, _party.Y) == 1;
        if (SelfDrive)
        {
            // Harness self-move is single-step only (no host to plan a stride).
            if (adjacent) MoveParty(best);
            return;
        }
        // Live: report the clicked tile to the host — adjacent is a single step,
        // a distant tile is a stride order (§3.4). The host validates reach/fog.
        MoveRequested?.Invoke(best);
    }

    /// <summary>RAY pick that respects tile HEIGHT: intersect the click ray with EACH tile's own top
    /// plane (y = its RENDERED height — flat for fog) and keep the tile the ray actually crosses (hit
    /// within its hex), NEAREST to the camera. Picks the tile you're looking at even when tiles are
    /// raised; the old nearest-projected-CENTRE pick was ambiguous between adjacent tiles at this
    /// shallow angle and moved the pawn the wrong way. Used by both click-to-move and hover.</summary>
    private bool TryPickTile(Vector2 screenPos, out Vector2I tile)
    {
        tile = default;
        if (_camera == null) return false;
        Vector3 origin = _camera.ProjectRayOrigin(screenPos);
        Vector3 dir = _camera.ProjectRayNormal(screenPos);
        if (Mathf.Abs(dir.Y) < 1e-5f) return false;

        float bestT = float.MaxValue;
        bool found = false;
        float reachSq = (HexR * 0.95f) * (HexR * 0.95f);
        foreach (var c in _windowTiles)
        {
            float h = _fog.FogAt(c) == Fog.Hidden ? FogSlabHeight : TileHeight(c);
            float t = (h - origin.Y) / dir.Y;
            if (t < 0f)
                continue;
            Vector3 hit = origin + dir * t;
            Vector3 ctr = TileOrigin(c.X, c.Y);
            if (new Vector2(ctr.X - hit.X, ctr.Z - hit.Z).LengthSquared() > reachSq)
                continue;                              // click isn't over this tile's top
            if (t < bestT) { bestT = t; tile = c; found = true; }   // nearest tile to camera wins
        }
        return found;
    }

    private void MoveParty(Vector2I coord)
    {
        _party = coord;
        UpdateVision();
        // Recolor + re-decorate + re-mark for the new fog, then re-hint moves.
        RebuildTiles();
        RebuildEdges();
        RebuildDecorations();
        RebuildMarkers();
        RebuildMoveHints();
        var dest = TileOrigin(coord.X, coord.Y);
        dest.Y = TileHeight(coord);
        if (_pawn != null)
        {
            var tw = CreateTween();
            tw.TweenProperty(_pawn, "position", dest, 0.18)
              .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        }
        // No hard recenter (that was the treadmill) — just a lazy leash: the camera
        // eases only if this step carried the party past the deadzone, keeping the
        // pawn from wandering out of frame while the world otherwise holds still.
        LeashCameraToParty();
        PartyMoved?.Invoke(coord);
    }

    // ── Build ────────────────────────────────────────────────────────────────

    private void RebuildAll(bool frameCamera)
    {
        RebuildTiles();
        BuildScryingRig();   // frames the map (its centre/radius were set by RebuildTiles)
        RebuildEdges();
        RebuildDecorations();
        RebuildMarkers();
        // First feed (frameCamera) snaps the pawn into place; a refresh (the live
        // host re-feeding after a run move) glides it, so a driven run walks across
        // the world exactly like the standalone harness does — no teleport.
        BuildPawn(animate: !frameCamera);
        RebuildMoveHints();
        // Frame hard on the FIRST feed; on a refresh (a host-driven run move) apply
        // the lazy leash instead of a recenter — the camera trails only when the pawn
        // would otherwise leave frame, so the world holds still step to step (the
        // treadmill fix) but the party can't be walked off-screen.
        if (frameCamera) FrameCamera();
        else LeashCameraToParty();
    }

    // ── Unified heightmap surface (2026-08-16 rewrite) ───────────────────────
    // The ground is now ONE continuous mesh. Height and colour come from SMOOTH
    // FIELDS: at any world point, each nearby hex's fog-aware rendered height
    // and colour are blended by an overlapping smooth kernel (a partition of
    // unity). The fields are C1-continuous and have ZERO relationship to tile
    // topology, so there is no cell, seam, facet, or crack for a line to live
    // in — the entire class of tile-lattice artifacts is gone by construction.
    // Hexes stay pure gameplay data (picking, fog, movement, POIs unchanged).
    // Replaced the welded-fan land + canvas sheet + water prisms.

    /// <summary>Per-hex fog-aware rendered height + colour, the field's sample
    /// points. Rebuilt each RebuildTiles.</summary>
    private readonly Dictionary<Vector2I, (float h, Color col)> _field = new();
    private Vector2 _fieldMin, _fieldMax;
    /// <summary>Kernel radius in WORLD units. Must exceed the hex spacing
    /// (~1.5–1.73) so kernels overlap and the blended field is smooth. Larger =
    /// softer washes and gentler slopes; smaller = crisper terrain identity.</summary>
    private const float FieldKernelRadius = 2.4f;

    private void RebuildTiles()
    {
        _landLayer?.QueueFree();
        _waterLayer?.QueueFree();
        _canvasLayer?.QueueFree();
        _mistLayer?.QueueFree();
        _waterLayer = null;
        _canvasLayer = null;
        _mistLayer = null;

        BuildFieldData();
        _landLayer = BuildHeightmapSurface();
        RebuildMistLayer();

        // Keep the active V-key diagnostic mode across rebuilds.
        if (_debugViz != 0)
            ApplyDebugViz();
    }

    /// <summary>Populate <see cref="_field"/> with every window tile plus a few
    /// margin rings (so the disc sits on paper and the kernel has support past
    /// the edge), each carrying its fog-aware RENDERED height and colour —
    /// exactly what the old per-tile path drew, now as field samples.</summary>
    private void BuildFieldData()
    {
        _field.Clear();
        void Add(Vector2I c)
        {
            if (_field.ContainsKey(c)) return;
            bool hidden = !_world.InBounds(c.X, c.Y) || _fog.FogAt(c) == Fog.Hidden;
            if (hidden)
            {
                float edge = HasPaintedNeighbor(c) ? Hex3DPalette.WetEdgeAmount(c.X, c.Y) : 0f;
                _field[c] = (FogSlabHeight, StyleUnexplored(Hex3DPalette.CanvasTone(c.X, c.Y, edge)));
            }
            else
            {
                var f = _fog.FogAt(c);
                _field[c] = (RenderedTileHeight(c), TileColor(_world.GetTile(c.X, c.Y), c, f));
            }
        }

        foreach (var c in _windowTiles) Add(c);
        var frontier = new List<Vector2I>(_windowTiles);
        for (int ring = 0; ring < 4; ring++)
        {
            var next = new List<Vector2I>();
            foreach (var c in frontier)
            {
                var (q, r) = HexCoord.OffsetToAxial(c.X, c.Y);
                for (int i = 0; i < 6; i++)
                {
                    var (dq, dr) = HexCoord.AxialDirections[i];
                    var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                    var nco = new Vector2I(nc, nr);
                    if (_field.ContainsKey(nco)) continue;
                    Add(nco);
                    next.Add(nco);
                }
            }
            frontier = next;
        }

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var c in _field.Keys)
        {
            var o = TileOrigin(c.X, c.Y);
            minX = Mathf.Min(minX, o.X); maxX = Mathf.Max(maxX, o.X);
            minZ = Mathf.Min(minZ, o.Z); maxZ = Mathf.Max(maxZ, o.Z);
        }
        _fieldMin = new Vector2(minX, minZ);
        _fieldMax = new Vector2(maxX, maxZ);
    }

    /// <summary>Smooth blended field at a world point: kernel-weighted average of
    /// nearby hexes' heights and colours (partition of unity ⇒ C1-smooth). Out
    /// param carries the colour; return value is the height PRE-undulation.</summary>
    private float SampleField(float wx, float wz, out Color col)
    {
        var (cc, cr) = WorldToOffset(wx, wz);
        var (q0, r0) = HexCoord.OffsetToAxial(cc, cr);
        float wsum = 0f, hsum = 0f, rr = 0f, gg = 0f, bb = 0f;
        float R = FieldKernelRadius, R2 = R * R;
        for (int dq = -2; dq <= 2; dq++)
            for (int dr = -2; dr <= 2; dr++)
            {
                var (nc, nr) = HexCoord.AxialToOffset(q0 + dq, r0 + dr);
                if (!_field.TryGetValue(new Vector2I(nc, nr), out var d)) continue;
                var o = TileOrigin(nc, nr);
                float dx = wx - o.X, dz = wz - o.Z;
                float dist2 = dx * dx + dz * dz;
                if (dist2 >= R2) continue;
                float tt = Mathf.Sqrt(dist2) / R;                 // 0 at centre, 1 at radius
                float w = 1f - tt * tt * (3f - 2f * tt);          // smoothstep-down, C1
                wsum += w; hsum += w * d.h;
                rr += w * d.col.R; gg += w * d.col.G; bb += w * d.col.B;
            }
        if (wsum <= 1e-6f) { col = UITheme.CanvasUnseen; return FogSlabHeight; }
        col = new Color(rr / wsum, gg / wsum, bb / wsum, 1f);
        return hsum / wsum;
    }

    private float SampleFieldHeight(float wx, float wz)
        => SampleField(wx, wz, out _) + Undulation(wx, wz);

    /// <summary>Build the single continuous ground mesh over the field bounds:
    /// a regular XZ grid, each vertex placed at the smooth field height with a
    /// grid-derived smooth normal and the smooth field colour. Dense grid + a
    /// smooth field ⇒ Gouraud vertex colour interpolates smoothly (no mosaic),
    /// grid-difference normals vary smoothly (no facets).</summary>
    private MeshInstance3D BuildHeightmapSurface()
    {
        const float vertsPerUnit = 2.5f;
        float wSpan = _fieldMax.X - _fieldMin.X, hSpan = _fieldMax.Y - _fieldMin.Y;
        int nx = Mathf.Max(2, Mathf.CeilToInt(wSpan * vertsPerUnit));
        int nz = Mathf.Max(2, Mathf.CeilToInt(hSpan * vertsPerUnit));
        int stride = nx + 1;
        var pos = new Vector3[stride * (nz + 1)];
        var col = new Color[stride * (nz + 1)];
        var outside = new bool[stride * (nz + 1)];
        // Disc clip + rim fade (the "floating rectangle" fix). The field bounds
        // are a RECTANGLE, which drew a hard slab with cut corners. Clip the
        // mesh to the largest inscribed circle and fade the last few units of
        // ground toward the surround colour, so the world is a soft-edged island
        // that dissolves into the fog instead of a slab with a hard edge.
        float cx = (_fieldMin.X + _fieldMax.X) * 0.5f, cz = (_fieldMin.Y + _fieldMax.Y) * 0.5f;
        float discR = 0.5f * Mathf.Min(wSpan, hSpan);
        _mapCenterX = cx; _mapCenterZ = cz; _mapDiscR = discR;   // the scrying rig frames this
        const float rimFade = 7f;                       // world units of colour fade
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                float wx = _fieldMin.X + wSpan * i / nx;
                float wz = _fieldMin.Y + hSpan * j / nz;
                int idx = j * stride + i;
                float rad = Mathf.Sqrt((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz));
                // Rim CLAMP, not clip (2026-08-21 rev 9 — the final sawtooth
                // fix). Dropping whole quads outside the disc left a staircase
                // OUTLINE that survived every cover-up (sink, mist alpha) and
                // kept silhouetting through the translucent rim band. Instead,
                // vertices beyond the disc are pulled RADIALLY onto the circle:
                // the mesh boundary IS the circle now — there is no sawtooth to
                // hide. Outer quads compress to thin slivers along the arc
                // (zero-area ones render nothing); `outside` stays false so no
                // quad is ever dropped.
                if (rad > discR)
                {
                    float k = discR / rad;
                    wx = cx + (wx - cx) * k;
                    wz = cz + (wz - cz) * k;
                    rad = discR;
                }
                float baseH = SampleField(wx, wz, out var c);   // one sample: height + colour
                if (rad > discR - rimFade)
                {
                    float t = Mathf.Clamp((rad - (discR - rimFade)) / rimFade, 0f, 1f);
                    c = c.Lerp(_surroundEdge, t * t * (3f - 2f * t));   // smoothstep to surround
                }
                // Rim sink (rev 7): the outermost band dives below the mist
                // deck to exactly TableTopY, so the (now circular) boundary
                // meets the barrel base under the fog, out of sight.
                const float sinkBand = 2.0f;
                if (rad > discR - sinkBand)
                {
                    float s = Mathf.Clamp((rad - (discR - sinkBand)) / sinkBand, 0f, 1f);
                    s = s * s * (3f - 2f * s);
                    baseH = Mathf.Lerp(baseH, FogSlabHeight - 0.6f, s);
                }
                pos[idx] = new Vector3(wx, baseH + Undulation(wx, wz), wz);
                col[idx] = c;
            }

        // Grid-difference normals (cheap, smooth) — central where possible.
        float dx0 = wSpan / nx, dz0 = hSpan / nz;
        var nrm = new Vector3[pos.Length];
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                int L = j * stride + Mathf.Max(i - 1, 0);
                int Rr = j * stride + Mathf.Min(i + 1, nx);
                int D = Mathf.Max(j - 1, 0) * stride + i;
                int U = Mathf.Min(j + 1, nz) * stride + i;
                float gx = (pos[Rr].Y - pos[L].Y) / ((Mathf.Min(i + 1, nx) - Mathf.Max(i - 1, 0)) * dx0);
                float gz = (pos[U].Y - pos[D].Y) / ((Mathf.Min(j + 1, nz) - Mathf.Max(j - 1, 0)) * dz0);
                nrm[j * stride + i] = new Vector3(-gx, 1f, -gz).Normalized();
            }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void AddTri(int a, int b, int c)
        {
            // Up-facing under Godot's CW front-face rule (numeric, orientation-proof).
            float crossY = (pos[b].Z - pos[a].Z) * (pos[c].X - pos[a].X)
                         - (pos[b].X - pos[a].X) * (pos[c].Z - pos[a].Z);
            if (crossY > 0f) (b, c) = (c, b);
            st.SetColor(col[a]); st.SetNormal(nrm[a]); st.AddVertex(pos[a]);
            st.SetColor(col[b]); st.SetNormal(nrm[b]); st.AddVertex(pos[b]);
            st.SetColor(col[c]); st.SetNormal(nrm[c]); st.AddVertex(pos[c]);
        }
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                if (outside[a] && outside[b] && outside[c] && outside[d]) continue;   // beyond the disc
                AddTri(a, c, b);
                AddTri(b, c, d);
            }

        var mat = PainterlyPrism.TileMaterial(PainterlyPrism.Land, 0.9f);
        if (mat is ShaderMaterial sm)
        {
            sm.SetShaderParameter("use_color_map", false);   // colour is smooth vertex colour
            sm.SetShaderParameter("top_undulation", 0f);     // undulation is baked into vertices
            // grain OFF (0, was 0.07) — CONFIRMED cause of the lines. The fbm2
            // brush grain is high-frequency world-space noise; across the large
            // ground at distance/grazing angles it undersamples and ALIASES into
            // regular moiré lines. How badly depends on resolution/GPU/MSAA,
            // which is why it showed on this desktop but not the laptop. The
            // painterly read is carried by the toon light + colour, not grain;
            // if fine surface texture is wanted back, re-add it camera-distance-
            // faded so it only appears close up where it can be sampled.
            sm.SetShaderParameter("grain_strength", 0f);
            sm.SetShaderParameter("skirt_darken", 0.10f);
            sm.SetShaderParameter("stripe_strength", 0.06f);
            sm.SetShaderParameter("toon_softness", 0.26f);
        }
        var node = new MeshInstance3D { Name = "WinHeightmap", Mesh = st.Commit(), MaterialOverride = mat };
        AddChild(node);
        return node;
    }

    // ── Scrying table rig ────────────────────────────────────────────────────
    private const uint RigLayer = 2;   // render layer for table/figures (bit 1)

    /// <summary>Build the scrying-chamber frame around the map: a round table the
    /// projection hovers over, a chamber floor, a glowing projection rim, an
    /// arcane light, and a ring of stand-in companions. Positioned at the current
    /// map centre so it travels with a sliding window. The arcane light is
    /// cull-masked to <see cref="RigLayer"/> so it lights only the rig — the map's
    /// tuned colours are untouched. Nothing here is pickable (picking is pure math
    /// over the tile grid, not physics), so it's purely a visual frame.</summary>
    private void BuildScryingRig()
    {
        _scryRig?.QueueFree();
        _scryRig = new Node3D { Name = "ScryingRig" };
        AddChild(_scryRig);

        float R = _mapDiscR;
        Vector3 c = new Vector3(_mapCenterX, 0f, _mapCenterZ);
        float tableTopY = TableTopY;   // just below the map's lowest ground ⇒ projection floats
        float floorY = FloorY;         // chamber floor the figures stand on

        void Add(Node3D n) { if (n is VisualInstance3D vi) vi.Layers = RigLayer; _scryRig.AddChild(n); }

        // Chamber floor — a broad dark disc so the figures aren't standing in void.
        Add(new MeshInstance3D
        {
            Name = "ChamberFloor",
            Mesh = new CylinderMesh { TopRadius = R + 16f, BottomRadius = R + 16f, Height = 0.4f, RadialSegments = 48 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = FloorColor, Roughness = 1f },
            Position = c + new Vector3(0f, floorY - 0.2f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // The scrying table — a round pedestal/basin the projection sits over.
        Add(new MeshInstance3D
        {
            Name = "ScryTable",
            Mesh = new CylinderMesh { TopRadius = R + 1.2f, BottomRadius = R + 2.2f, Height = tableTopY - floorY, RadialSegments = 48 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = TableColor, Roughness = 0.85f },
            Position = c + new Vector3(0f, (tableTopY + floorY) * 0.5f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // The LENS FRAME (2026-08-21 playtest: "contained in the frame"). The
        // rim used to be a flat annulus at table level, far below the mist —
        // the fog looked like it was spilling over an open plate. Now the
        // frame is a vessel: a dark barrel wall rises from the table top to
        // just above the mist deck's crests, and the glowing annulus sits on
        // its lip, so the mist reads as held INSIDE the scrying lens.
        float rimY = FogSlabHeight + 0.12f + MistDeckAmp + 0.35f;   // just above the mist tops
        Add(new MeshInstance3D
        {
            Name = "LensBarrel",
            Mesh = new CylinderMesh
            {
                TopRadius = R + 0.55f, BottomRadius = R + 0.75f,
                Height = rimY - tableTopY, RadialSegments = 64,
                CapTop = false, CapBottom = false,
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = TableColor.Darkened(0.15f), Roughness = 0.8f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // inner wall visible from above
            },
            Position = c + new Vector3(0f, (rimY + tableTopY) * 0.5f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        // Glowing lip on the barrel's top edge.
        Add(MakeRing(c + new Vector3(0f, rimY, 0f), R + 0.30f, R + 0.85f, ArcaneGlow, ProjectionRimEnergy));

        // Arcane light from the projection — lights ONLY the rig (cull mask), so
        // the map colours stay as tuned. No shadows (avoids the acne we fixed).
        // Energy/range up so it pools visible light on the table + floor around
        // the projection rather than leaving a black void when zoomed out.
        Add(new OmniLight3D
        {
            Name = "ScryGlow",
            LightColor = ArcaneGlow, LightEnergy = GlowEnergy, OmniRange = R * 2.8f,
            ShadowEnabled = false, LightCullMask = RigLayer,
            Position = c + new Vector3(0f, tableTopY + 2.4f, 0f),
        });

        // Stand-in companions around the rim, faces turned to the map.
        int n = Mathf.Max(1, CompanionCount);
        float ringR = R + CompanionRingMargin;
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.Tau * (i + 0.5f) / n;
            var basePos = c + new Vector3(Mathf.Cos(a) * ringR, floorY, Mathf.Sin(a) * ringR);
            Add(MakeStandIn(basePos, c));
        }
    }

    /// <summary>Flat emissive annulus (ring) in the XZ plane — orientation-proof
    /// (built directly), used for the glowing projection rim.</summary>
    private MeshInstance3D MakeRing(Vector3 centre, float inner, float outer, Color glow, float energy)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int seg = 72;
        for (int i = 0; i < seg; i++)
        {
            float a0 = Mathf.Tau * i / seg, a1 = Mathf.Tau * (i + 1) / seg;
            Vector3 i0 = centre + new Vector3(Mathf.Cos(a0) * inner, 0f, Mathf.Sin(a0) * inner);
            Vector3 i1 = centre + new Vector3(Mathf.Cos(a1) * inner, 0f, Mathf.Sin(a1) * inner);
            Vector3 o0 = centre + new Vector3(Mathf.Cos(a0) * outer, 0f, Mathf.Sin(a0) * outer);
            Vector3 o1 = centre + new Vector3(Mathf.Cos(a1) * outer, 0f, Mathf.Sin(a1) * outer);
            foreach (var v in new[] { i0, o0, o1, i0, o1, i1 })
            { st.SetNormal(Vector3.Up); st.AddVertex(v); }
        }
        return new MeshInstance3D
        {
            Name = "ProjectionRim",
            Mesh = st.Commit(),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = glow, EmissionEnabled = true, Emission = glow, EmissionEnergyMultiplier = energy,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>One stylised stand-in companion: a robed body + head, dark with a
    /// faint arcane under-light, turned to face the map centre. Real companion
    /// models replace these in a later pass.</summary>
    private Node3D MakeStandIn(Vector3 basePos, Vector3 lookCentre)
    {
        float h = CompanionHeight;   // head clears the table rim to peer at the map
        var fig = new Node3D { Position = basePos };
        var robe = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.62f, Height = h * 0.8f, RadialSegments = 8 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = CompanionRobe, Roughness = 1f,
                EmissionEnabled = true, Emission = ArcaneGlow, EmissionEnergyMultiplier = 0.10f,
            },
            Position = new Vector3(0f, h * 0.4f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = RigLayer,
        };
        var head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.26f, Height = 0.52f, RadialSegments = 10, Rings = 6 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.14f, 0.13f, 0.16f), Roughness = 1f },
            Position = new Vector3(0f, h * 0.8f + 0.18f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = RigLayer,
        };
        fig.AddChild(robe); fig.AddChild(head);
        // Yaw to face the map centre (compute directly — no in-tree LookAt needed).
        Vector3 dir = new Vector3(lookCentre.X - basePos.X, 0f, lookCentre.Z - basePos.Z);
        if (dir.LengthSquared() > 1e-4f)
            fig.Rotation = new Vector3(0f, Mathf.Atan2(dir.X, dir.Z), 0f);
        return fig;
    }

    /// <summary>True when any hex neighbor of a Hidden tile is itself not Hidden —
    /// the canvas tile borders painted/underpainted ground and takes the wet-edge
    /// darkening (see Hex3DPalette.CanvasTone). Absent coords read Hidden, matching
    /// the fog model's contract, so the window boundary stays clean canvas.</summary>
    private bool HasPaintedNeighbor(Vector2I c)
    {
        // Window coords are WORLD OFFSET (col,row) — neighbor steps must round-trip
        // through axial (offset steps are column-parity-dependent), same as the
        // adjacency walks elsewhere in this file.
        var (q, r) = HexCoord.OffsetToAxial(c.X, c.Y);
        for (int i = 0; i < 6; i++)
        {
            var (dq, dr) = HexCoord.AxialDirections[i];
            var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
            if (_fog.FogAt(new Vector2I(nc, nr)) != Fog.Hidden)
                return true;
        }
        return false;
    }

    /// <summary>WINDOW-ONLY height compression (user ruling 2026-08-12: full
    /// strategic terracing is busy and hard to navigate at walking zoom). The
    /// strategic map's relief is INFORMATION at survey distance; here the
    /// information is adjacency and walkability, so the variable part of the
    /// height is compressed — relief survives as gentle steps, cliffs stop
    /// occluding the tiles behind them. 1.0 restores the strategic profile.</summary>
    private const float HeightScale = 0.45f;

    private float TileHeight(Vector2I c)
    {
        var t = _world.GetTile(c.X, c.Y);
        if (_fog.FogAt(c) == Fog.Hidden) return VoidSlabHeight;
        if (t.IsOcean) return 0.08f;
        if (t.IsLake) return 0.12f;
        float terraced = Mathf.Round(Mathf.Clamp(t.Elevation, 0f, 1f) * TerraceSteps) / TerraceSteps;
        float h = 0.22f + terraced * 2.6f;
        switch (t.Terrain)
        {
            case TT.Mountain: h += 1.2f; break;
            case TT.Volcanic: h += 0.9f; break;
            case TT.Snow: h += 0.6f; break;
            case TT.Hills: h += 0.5f; break;
            case TT.Swamp: case TT.Marsh: h = Mathf.Min(h, 0.30f); break;
            case TT.Coast: h = Mathf.Min(h, 0.26f); break;
        }
        return 0.22f + (h - 0.22f) * HeightScale;
    }

    // ── Volumetric mist over undiscovered ground (2026-08-21, rev 2) ─────────
    // Playtest rev 2: the single translucent sheet read as a flat wash and let
    // the unexplored terrain colour bleed through. Now a three-layer stack that
    // fakes a volume convincingly on any renderer:
    //  1. DECK — a subdivided plane vertex-DISPLACED by curl-bent scrolling
    //     noise (real lumpy geometry, finite-difference normals, manual lambert
    //     shading) that goes NEAR-OPAQUE over fully hidden ground, so the
    //     canvas colour underneath is actually gone;
    //  2/3. WISPS — two light translucent sheets above at different scales,
    //     speeds and directions; their parallax against the moving deck is
    //     what sells the volume. All alpha comes from the blurred hidden-mask
    //     (world-space lookup), so mist sits only over undiscovered ground and
    //     thins to wisps at the frontier. Silhouette-ring tiles stay unmisted.
    private const float MistDeckHeight = FogSlabHeight + 0.12f;
    private const float MistDeckAmp = 0.95f;      // vertex displacement ceiling
    private const int MistMaskRes = 112;
    private const int MistDeckSubdiv = 100;

    private const string MistDeckShaderCode = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_back, shadows_disabled;

uniform sampler2D hidden_mask : filter_linear, repeat_disable;
uniform sampler2D noise_tex : filter_linear, repeat_enable;
uniform vec4 mist_color : source_color = vec4(0.52, 0.55, 0.72, 1.0);
uniform float density : hint_range(0.0, 1.0) = 0.96;
uniform float amp = 0.95;
uniform float swirl = 0.4;
uniform float speed = 0.03;
uniform vec2 mask_min;
uniform vec2 mask_size;
uniform vec2 disc_center;
uniform float disc_radius = 1e6;
uniform float rim_fade = 6.0;
uniform vec2 wind = vec2(1.0, 0.35);
uniform float rim_reach = 0.5;

varying float v_cloud;
varying vec3 v_normal;
varying vec3 v_world;

// Rolling ADVECTION, not churn: every octave drifts the SAME heading at a
// different rate (in-cloud parallax), and the curl field itself evolves
// slowly. The old counter-scrolling octaves had zero net motion — pure
// in-place churn, which reads as boiling liquid, not weather.
float cloud(vec2 p, float t) {
    vec2 drift = normalize(wind) * t;
    float na = textureLod(noise_tex, p * 0.9 - drift * 0.35 + vec2(0.0, t * 0.05), 0.0).r;
    float nb = textureLod(noise_tex, p * 1.3 - drift * 0.5 + vec2(t * 0.04, 0.0), 0.0).r;
    vec2 bend = vec2(na - 0.5, nb - 0.5) * swirl;
    float body = textureLod(noise_tex, p * 0.5 + bend - drift, 0.0).r;
    float wisp = textureLod(noise_tex, p * 1.5 + bend * 1.4 - drift * 1.4, 0.0).r;
    return clamp(body * 0.75 + wisp * 0.45, 0.0, 1.0);
}

void vertex() {
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec2 muv = (wp.xz - mask_min) / mask_size;
    float m = textureLod(hidden_mask, muv, 0.0).r;
    float t = TIME * speed;
    // Broader features than rev 2 (0.13 → 0.09): banks and shelves, not bubbles.
    vec2 p = wp.xz * 0.09;
    float c = cloud(p, t);
    // Finite-difference normal from the same field (dx in world units).
    float e = 0.5;
    float cx = cloud(p + vec2(e * 0.09, 0.0), t);
    float cz = cloud(p + vec2(0.0, e * 0.09), t);
    // Displacement is full-height to the wall (rev 6): the barrel lip sits
    // at deck base + FULL amp + 0.35, so rolling cloud clears the frame.
    // Rim/mask ALPHA moved to the FRAGMENT stage (rev 8): computed per-
    // vertex they were interpolated across the 100² grid's triangles, which
    // quantized the circular fade into a sawtooth at the rim (the arrowed
    // jagged seam). Per-pixel, the edge is a true circle.
    VERTEX.y += c * amp * m;
    v_cloud = c;
    v_world = wp;
    v_normal = normalize(vec3((c - cx) * amp * m / e, 1.0, (c - cz) * amp * m / e));
}

void fragment() {
    // Manual lambert off the displaced surface: crests catch light, hollows
    // sink — the shading is what makes the deck read as a body, not a sheet.
    float lit = clamp(dot(normalize(v_normal), normalize(vec3(0.45, 0.75, 0.35))), 0.0, 1.0);
    ALBEDO = mist_color.rgb * (0.52 + 0.50 * lit) + vec3(v_cloud * 0.04);
    // Near-opaque over fully hidden ground (the underlying colour must GO);
    // the frontier fade rides the blurred mask, the disc clip the rim —
    // both PER-PIXEL (rev 8), so neither edge can alias against the grid.
    float m = texture(hidden_mask, (v_world.xz - mask_min) / mask_size).r;
    float dRim = distance(v_world.xz, disc_center);
    float arim = 1.0 - smoothstep(disc_radius - 1.2, disc_radius + rim_reach, dRim);
    ALPHA = smoothstep(0.04, 0.45, m) * density * arim;
}";

    private const string MistWispShaderCode = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, shadows_disabled;

uniform sampler2D hidden_mask : filter_linear, repeat_disable;
uniform sampler2D noise_tex : filter_linear, repeat_enable;
uniform vec4 mist_color : source_color = vec4(0.58, 0.61, 0.78, 1.0);
uniform float density : hint_range(0.0, 1.0) = 0.4;
uniform float scale = 0.2;
uniform float swirl = 0.35;
uniform float speed = 0.045;
uniform vec2 mask_min;
uniform vec2 mask_size;
uniform vec2 disc_center;
uniform float disc_radius = 1e6;
uniform float rim_fade = 6.0;
uniform vec2 wind = vec2(1.0, 0.35);
uniform float rim_reach = 0.5;

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 muv = (world_pos.xz - mask_min) / mask_size;
    float m = texture(hidden_mask, muv).r;
    if (m < 0.02) {
        ALPHA = 0.0;
    } else {
        vec2 uv = world_pos.xz * scale;
        float t = TIME * speed;
        // Same one-heading advection as the deck (see MistDeckShaderCode).
        vec2 drift = normalize(wind) * t;
        float na = texture(noise_tex, uv * 0.9 - drift * 0.35 + vec2(0.0, t * 0.05)).r;
        float nb = texture(noise_tex, uv * 1.3 - drift * 0.5 + vec2(t * 0.04, 0.0)).r;
        vec2 bend = vec2(na - 0.5, nb - 0.5) * swirl;
        float body = texture(noise_tex, uv * 0.55 + bend - drift).r;
        float wisp = texture(noise_tex, uv * 1.8 + bend * 1.4 - drift * 1.4).r;
        // Threshold into puffs (not a wash) so each sheet reads as drifting
        // cloud matter with gaps the deck shows through — the parallax cue.
        float puff = smoothstep(0.42, 0.75, body * 0.7 + wisp * 0.45);
        float edge = smoothstep(0.05, 0.55, m);
        // Alpha runs to rim_reach past the disc (the barrel wall), matching the
        // deck — see the deck shader's jagged-seam note.
        float rim = 1.0 - smoothstep(disc_radius - 1.2, disc_radius + rim_reach, distance(world_pos.xz, disc_center));
        ALPHA = puff * density * edge * rim;
        ALBEDO = mist_color.rgb + vec3(wisp * 0.05);
    }
}";

    /// <summary>Build the three-layer mist stack over the current field bounds:
    /// bake the blurred hidden-mask from the fog model, then the displaced deck
    /// plus two wisp sheets. Rebuilt with the tiles (fog changes every reveal).
    /// Style tint/density applied via <see cref="ApplyMistStyle"/>.</summary>
    private void RebuildMistLayer()
    {
        _mistMats.Clear();
        float w = _fieldMax.X - _fieldMin.X, h = _fieldMax.Y - _fieldMin.Y;
        if (w <= 0f || h <= 0f) return;

        // Bake the hidden mask: 1 = undiscovered (or off-world), 0 = painted.
        var raw = new float[MistMaskRes * MistMaskRes];
        for (int j = 0; j < MistMaskRes; j++)
        {
            for (int i = 0; i < MistMaskRes; i++)
            {
                float wx = _fieldMin.X + (i + 0.5f) / MistMaskRes * w;
                float wz = _fieldMin.Y + (j + 0.5f) / MistMaskRes * h;
                var (c, r) = WorldToOffset(wx, wz);
                bool hidden = !_world.InBounds(c, r) ||
                              _fog.FogAt(new Vector2I(c, r)) == Fog.Hidden;
                raw[j * MistMaskRes + i] = hidden ? 1f : 0f;
            }
        }
        // One 3×3 box pass: softens the tile-quantised edge so the shaders'
        // smoothsteps have a gradient to ride (wisps at the frontier).
        var img = Image.CreateEmpty(MistMaskRes, MistMaskRes, false, Image.Format.L8);
        for (int j = 0; j < MistMaskRes; j++)
        {
            for (int i = 0; i < MistMaskRes; i++)
            {
                float sum = 0f; int n = 0;
                for (int dj = -1; dj <= 1; dj++)
                {
                    for (int di = -1; di <= 1; di++)
                    {
                        int x = i + di, y = j + dj;
                        if (x < 0 || y < 0 || x >= MistMaskRes || y >= MistMaskRes) continue;
                        sum += raw[y * MistMaskRes + x]; n++;
                    }
                }
                float v = sum / n;
                img.SetPixel(i, j, new Color(v, v, v));
            }
        }
        var mask = ImageTexture.CreateFromImage(img);

        var noise = new NoiseTexture2D
        {
            Noise = new FastNoiseLite
            {
                NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
                Frequency = 0.02f,
                FractalOctaves = 3,
            },
            Seamless = true,
            Width = 256,
            Height = 256,
        };

        _mistLayer = new Node3D { Name = "MistStack" };
        AddChild(_mistLayer);

        ShaderMaterial MakeMat(string code)
        {
            var mat = new ShaderMaterial { Shader = new Shader { Code = code } };
            mat.SetShaderParameter("hidden_mask", mask);
            mat.SetShaderParameter("noise_tex", noise);
            mat.SetShaderParameter("mask_min", new Vector2(_fieldMin.X, _fieldMin.Y));
            mat.SetShaderParameter("mask_size", new Vector2(w, h));
            // Scrying-disc clip: BuildHeightmapSurface ran just before us and
            // stamped the projection's centre/radius — the mist stack must be
            // the same round island the land is (rim band matches its 7u fade).
            mat.SetShaderParameter("disc_center", new Vector2(_mapCenterX, _mapCenterZ));
            mat.SetShaderParameter("disc_radius", _mapDiscR);
            mat.SetShaderParameter("rim_fade", 6.0f);
            // Alpha reach past the disc edge: keep just inside the barrel's
            // inner wall (R + 0.55 at the top) so mist laps the vessel and
            // buries the land mesh's whole-quad clip staircase.
            mat.SetShaderParameter("rim_reach", 0.5f);
            _mistMats.Add(mat);
            return mat;
        }
        void AddPlane(string name, ShaderMaterial mat, float y, int subdiv)
        {
            _mistLayer.AddChild(new MeshInstance3D
            {
                Name = name,
                Mesh = new PlaneMesh
                {
                    Size = new Vector2(w, h),
                    SubdivideWidth = subdiv,
                    SubdivideDepth = subdiv,
                },
                MaterialOverride = mat,
                Position = new Vector3((_fieldMin.X + _fieldMax.X) * 0.5f, y,
                                       (_fieldMin.Y + _fieldMax.Y) * 0.5f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        // Deck: the displaced, near-opaque body of the mist.
        var deck = MakeMat(MistDeckShaderCode);
        deck.SetShaderParameter("amp", MistDeckAmp);
        AddPlane("MistDeck", deck, MistDeckHeight, MistDeckSubdiv);

        // Wisps: two translucent drift sheets. Same general WIND HEADING as the
        // deck (opposite directions read as churn — the "bubbling" complaint),
        // but different rates and a small angular spread, so the layers slide
        // over each other like cloud decks in shear (the parallax volume cue).
        var wispA = MakeMat(MistWispShaderCode);
        wispA.SetShaderParameter("scale", 0.20f);
        wispA.SetShaderParameter("speed", 0.045f);
        AddPlane("MistWispA", wispA, MistDeckHeight + MistDeckAmp * 0.75f, 0);

        var wispB = MakeMat(MistWispShaderCode);
        wispB.SetShaderParameter("scale", 0.30f);
        wispB.SetShaderParameter("speed", 0.06f);
        wispB.SetShaderParameter("wind", new Vector2(0.8f, 0.6f));   // ~20° off the deck's heading
        AddPlane("MistWispB", wispB, MistDeckHeight + MistDeckAmp * 1.15f, 0);

        ApplyMistStyle();
    }

    /// <summary>Tint + thickness of the mist per surround style, applied at build
    /// and again when B cycles the surround (the mist must read as part of each
    /// look, not pasted over it). Layer order in <see cref="_mistMats"/> is
    /// deck, wispA, wispB.</summary>
    private void ApplyMistStyle()
    {
        if (_mistMats.Count == 0) return;
        var (col, deckDen) = _surround switch
        {
            SurroundStyle.Haze => (new Color(0.93f, 0.92f, 0.88f), 0.93f),
            SurroundStyle.Desk => (new Color(0.85f, 0.79f, 0.68f), 0.92f),
            _ => (new Color(0.50f, 0.53f, 0.70f), 0.96f),   // Vignette chamber
        };
        for (int i = 0; i < _mistMats.Count; i++)
        {
            // Upper sheets run lighter and thinner than the deck.
            var layerCol = i == 0 ? col : col.Lightened(0.08f * i);
            float den = i == 0 ? deckDen : (i == 1 ? 0.40f : 0.28f);
            _mistMats[i].SetShaderParameter("mist_color", layerCol);
            _mistMats[i].SetShaderParameter("density", den);
        }
    }


    // ── Rivers & roads (edge masks) ──────────────────────────────────────────

    /// <summary>Rivers and roads are 6-bit EDGE masks on WorldTile. For each tile draw a strip from
    /// its CENTRE out to each active edge midpoint — the neighbour draws its own half, so the two
    /// meet and the path runs continuously THROUGH the tiles (matching the 2D map) rather than as
    /// dashes on the boundaries. Per-instance X scale carries each segment's length. Fogged tiles
    /// and open water are skipped (no river drawn across a lake).</summary>
    private void RebuildEdges()
    {
        _riverLayer?.QueueFree();
        _roadLayer?.QueueFree();
        // Rivers and roads are ground-following ribbons — every vertex re-heighted
        // by SampleGround (the unified heightmap field), so strokes lie ON the
        // surface.
        var riverTiles = new List<(Vector3 center, List<Vector3> mids, System.Func<Vector3, float> ground)>();
        var roadTiles = new List<(Vector3 center, List<Vector3> mids, System.Func<Vector3, float> ground)>();

        foreach (var c in _windowTiles)
        {
            if (_fog.FogAt(c) == Fog.Hidden) continue;
            var tile = _world.GetTile(c.X, c.Y);
            if (tile.IsWater) continue;                          // no rivers/roads over open water
            if ((tile.RiverEdges | tile.RoadEdges) == 0) continue;

            Vector3 center = TileOrigin(c.X, c.Y);
            center.Y = TileHeight(c) + 0.03f;
            var cc = c;   // closure copy
            System.Func<Vector3, float> ground = p => SampleGround(cc, p);
            List<Vector3> riverMids = null;
            List<Vector3> roadMids = null;
            var (q, r) = HexCoord.OffsetToAxial(c.X, c.Y);
            for (int i = 0; i < 6; i++)
            {
                bool riv = (tile.RiverEdges & (1 << i)) != 0;
                bool road = (tile.RoadEdges & (1 << i)) != 0;
                if (!riv && !road) continue;

                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                bool inBounds = _world.InBounds(nc, nr);
                Vector3 nbr = inBounds ? TileOrigin(nc, nr) : center + EdgeDir(i);
                // CONTINUITY: edge midpoint at the AVERAGE of the two rendered
                // heights (hidden neighbours count at the canvas slab, so a stroke
                // dives into the fog). With welded terrain the sampler overrides
                // these Y values anyway; they remain the non-welded fallback.
                float nbrH = inBounds ? RenderedTileHeight(new Vector2I(nc, nr)) : TileHeight(c);
                Vector3 edgeMid = new Vector3((center.X + nbr.X) * 0.5f,
                                              (TileHeight(c) + nbrH) * 0.5f + 0.03f,
                                              (center.Z + nbr.Z) * 0.5f);
                if (riv)
                    (riverMids ??= new List<Vector3>()).Add(edgeMid);
                if (road)
                    (roadMids ??= new List<Vector3>()).Add(edgeMid);
            }
            if (riverMids != null)
                riverTiles.Add((center, riverMids, ground));
            if (roadMids != null)
                roadTiles.Add((center, roadMids, ground));
        }

        _riverLayer = new MeshInstance3D
        {
            Name = "WinRivers",
            // Stage 2d: lift raised — covers inter-sample pokes on fan creases.
            Mesh = RiverMesh.Build(riverTiles, 0.30f, Hex3DPalette.RiverWater, Hex3DPalette.RiverBank,
                                   lift: 0.06f, meanderScale: 1f),
            MaterialOverride = PainterlyPrism.RiverMaterial(),
        };
        AddChild(_riverLayer);
        _roadLayer = new MeshInstance3D
        {
            Name = "WinRoads",
            // Roads: narrow, barely-winding, matte earth — same ground-following
            // builder so they hug the welded terrain exactly like the rivers.
            Mesh = RiverMesh.Build(roadTiles, 0.15f, Hex3DPalette.RoadStroke,
                                   Hex3DPalette.RoadStroke.Darkened(0.2f),
                                   lift: 0.055f, meanderScale: 0.3f),
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.95f,
            },
        };
        AddChild(_roadLayer);
    }

    /// <summary>A tile's height as it actually RENDERS: hidden tiles sit at the
    /// canvas slab (FogSlabHeight), not TileHeight's internal void value — used
    /// for edge-midpoint averaging so strokes meet what is really drawn.</summary>
    private float RenderedTileHeight(Vector2I c)
        => _fog.FogAt(c) == Fog.Hidden ? FogSlabHeight : TileHeight(c);

    // ── Baked undulation amplitude (world-space height roll) ────────────────
    // 0 (was 0.06): the fine world-locked height roll. Its normals, resolved by
    // the toon-banded directional light, paint a soft ~2-unit quilt across the
    // ground — the renderer-dependent "lines" (visible on Forward+/this GPU,
    // hidden on the laptop). Killed while we confirm the diagnosis; a smoother
    // reintroduction (shaded flat, or fewer harder bands) can follow.
    private const float UndulationAmp = 0f;

    /// <summary>World XZ → offset tile coord, via fractional axial + cube
    /// rounding (inverse of <see cref="TileOrigin"/>: x = q·1.5, z = √3·(r + q/2)).</summary>
    private static (int col, int row) WorldToOffset(float wx, float wz)
    {
        float qf = wx / ColSpacing;
        float rf = wz / RowSpacing - qf * 0.5f;
        float yf = -qf - rf;
        int rq = Mathf.RoundToInt(qf), ry = Mathf.RoundToInt(yf), rr = Mathf.RoundToInt(rf);
        float dq = Mathf.Abs(rq - qf), dy = Mathf.Abs(ry - yf), dr = Mathf.Abs(rr - rf);
        if (dq > dy && dq > dr) rq = -ry - rr;
        else if (dr > dy) rr = -rq - ry;
        return HexCoord.AxialToOffset(rq, rr);
    }

    /// <summary>The true rendered ground height at a world point inside (or just
    /// beside) a tile — welded fan interpolation + baked undulation. This is the
    /// single source of truth the stroke ribbons follow.</summary>
    private float SampleGround(Vector2I tile, Vector3 p)
    {
        // Unified-heightmap rewrite: the true surface is now the smooth field,
        // so rivers/roads/props read it directly (tile arg kept for signature).
        // Falls back to the flat rendered tile height before the field is built.
        if (_field.Count == 0)
            return RenderedTileHeight(tile) + Undulation(p.X, p.Z);
        return SampleFieldHeight(p.X, p.Z);
    }

    // Deterministic CPU value noise for the baked undulation (window-local — the
    // shader's top_undulation stays 0 here; geometry carries the roll).
    private static float UHash(int x, int z)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ 0xA511E9B3u;
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return (h & 0xFFFFu) / 65535f;
    }

    private static float UNoise(float x, float z)
    {
        int xi = Mathf.FloorToInt(x), zi = Mathf.FloorToInt(z);
        float fx = x - xi, fz = z - zi;
        // Quintic fade (C2), not smoothstep (C1): smoothstep's curvature snaps
        // at every noise-cell boundary, the analytic-gradient normals inherit
        // the kink, and the lit ground shows a straight crease along every
        // lattice line. Quintic removes the second-derivative discontinuity.
        fx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        fz = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);
        float a = UHash(xi, zi), b = UHash(xi + 1, zi);
        float c = UHash(xi, zi + 1), d = UHash(xi + 1, zi + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
    }

    private static float Undulation(float wx, float wz)
    {
        // ROTATED octave domains. Value noise lives on an integer lattice; with
        // both octaves axis-aligned, their cell boundaries (2.0 and 0.83 world
        // units) stamp a RECTANGULAR, REGULARLY SPACED grid of shading creases
        // across the ground — the reported "straight lines on the tiles".
        // Rotating each octave (18° / 49°) off the world axes and off each
        // other breaks the aligned grid into unstructured rolling. Pure
        // function of world XZ as before — welds, SampleGround, and the stroke
        // ribbons all stay consistent by construction.
        const float c1 = 0.9511f, s1 = 0.3090f;   // 18°
        const float c2 = 0.6561f, s2 = 0.7547f;   // 49°
        float x1 = (wx * c1 - wz * s1) * 0.5f, z1 = (wx * s1 + wz * c1) * 0.5f;
        float x2 = (wx * c2 - wz * s2) * 1.2f + 31.7f, z2 = (wx * s2 + wz * c2) * 1.2f;
        return ((UNoise(x1, z1) + UNoise(x2, z2) * 0.5f) - 0.75f) * UndulationAmp;
    }

    /// <summary>Approx render-space direction to a border tile's missing neighbour.</summary>
    private static Vector3 EdgeDir(int i)
    {
        var (dq, dr) = HexCoord.AxialDirections[i];
        return new Vector3(dq * ColSpacing, 0f, (dr + dq * 0.5f) * RowSpacing);
    }

    // ── Decorations (revealed land only) ─────────────────────────────────────

    private void RebuildDecorations()
    {
        foreach (var d in _decor) d.QueueFree();
        _decor.Clear();
        var broadleaf = new List<(Transform3D, Color)>();
        var conifers = new List<(Transform3D, Color)>();
        var peaks = new List<(Transform3D, Color)>();
        foreach (var c in _windowTiles)
        {
            if (_fog.FogAt(c) != Fog.Revealed) continue;
            var t = _world.GetTile(c.X, c.Y);
            if (t.IsWater) continue;
            var basePos = TileOrigin(c.X, c.Y);
            // Props stand on the smooth heightmap surface, not the flat tile height.
            float GroundAt(Vector3 p) => SampleGround(c, p);
            if (t.Terrain == TT.Forest)
            {
                // A5: canopy blob clusters (base-origin — placed AT ground height),
                // 60/40 broadleaf/conifer by hash, random yaw against clone read.
                int n = 2 + (int)(Hash(c, 1) % 2);
                for (int i = 0; i < n; i++)
                {
                    float a = H01(Hash(c, (uint)(7 + i))) * Mathf.Tau;
                    float rad = H01(Hash(c, (uint)(31 + i))) * 0.55f;
                    float s = 0.55f + H01(Hash(c, (uint)(53 + i))) * 0.55f;
                    var pos = basePos + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);
                    pos.Y = GroundAt(pos) - 0.03f;
                    var yaw = new Basis(Vector3.Up, H01(Hash(c, (uint)(97 + i))) * Mathf.Tau);
                    var xf = new Transform3D(yaw * Basis.FromScale(new Vector3(s, s, s)), pos);
                    if (Hash(c, (uint)(71 + i)) % 10 < 6)
                        broadleaf.Add((xf, Jitter(new Color(0.21f, 0.35f, 0.15f), c, 0.12f)));
                    else
                        conifers.Add((xf, Jitter(new Color(0.13f, 0.25f, 0.15f), c, 0.10f)));
                }
            }
            else if (t.Terrain == TT.Hills && Hash(c, 4) % 10 < 4)
            {
                // Shrub clumps on hills (window-only): breaks the big gold fields
                // at walking zoom — the atlas reads hills fine at survey distance.
                int n = 1 + (int)(Hash(c, 5) % 2);
                for (int i = 0; i < n; i++)
                {
                    float a = H01(Hash(c, (uint)(61 + i))) * Mathf.Tau;
                    float rad = 0.15f + H01(Hash(c, (uint)(67 + i))) * 0.45f;
                    float s = 0.24f + H01(Hash(c, (uint)(73 + i))) * 0.14f;
                    var pos = basePos + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);
                    pos.Y = GroundAt(pos) - 0.02f;
                    var yaw = new Basis(Vector3.Up, H01(Hash(c, (uint)(79 + i))) * Mathf.Tau);
                    broadleaf.Add((new Transform3D(yaw * Basis.FromScale(new Vector3(s, s * 0.8f, s)), pos),
                                   Jitter(new Color(0.44f, 0.45f, 0.24f), c, 0.14f)));
                }
            }
            else if ((t.Terrain == TT.Mountain || t.Terrain == TT.Volcanic) && Hash(c, 2) % 10 < 5)
            {
                float s = 0.7f + H01(Hash(c, 11)) * 0.6f;
                peaks.Add((new Transform3D(Basis.FromScale(new Vector3(s, s, s)),
                          basePos + new Vector3(0f, GroundAt(basePos) + 0.9f * s * 0.5f - 0.04f, 0f)),
                          t.Terrain == TT.Volcanic ? new Color(0.30f, 0.22f, 0.20f) : new Color(0.55f, 0.52f, 0.48f)));
            }
            else if (t.Terrain == TT.Snow && Hash(c, 3) % 10 < 5)
            {
                float s = 0.6f + H01(Hash(c, 13)) * 0.5f;
                peaks.Add((new Transform3D(Basis.FromScale(new Vector3(s, s, s)),
                          basePos + new Vector3(0f, GroundAt(basePos) + 0.9f * s * 0.5f - 0.04f, 0f)),
                          new Color(0.92f, 0.94f, 0.97f)));
            }
        }
        _decor.Add(MakeDecoLayer("WinBroadleaf", broadleaf, PainterlyProps.BroadleafCanopy(), 1.3f));
        _decor.Add(MakeDecoLayer("WinConifers", conifers, PainterlyProps.ConiferCanopy(), 1.4f));
        _decor.Add(MakeDecoLayer("WinPeaks", peaks, PainterlyProps.PeakCone(0.34f, 0.9f), 1.6f));
    }

    private MultiMeshInstance3D MakeDecoLayer(string name, List<(Transform3D, Color)> items, Mesh mesh, float meshExtent)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = mesh, InstanceCount = items.Count,
        };
        for (int i = 0; i < items.Count; i++)
        { mm.SetInstanceTransform(i, items[i].Item1); mm.SetInstanceColor(i, items[i].Item2); }
        // Style-guide scatter law: explicit CustomAabb over instance origins
        // (auto AABB on world-space transforms frustum-culls the layer as one
        // unit). Latent since stage 1; fixed with the A5 refactor.
        if (items.Count > 0)
        {
            Vector3 min = items[0].Item1.Origin, max = min;
            for (int i = 1; i < items.Count; i++)
            {
                var o = items[i].Item1.Origin;
                min = new Vector3(Mathf.Min(min.X, o.X), Mathf.Min(min.Y, o.Y), Mathf.Min(min.Z, o.Z));
                max = new Vector3(Mathf.Max(max.X, o.X), Mathf.Max(max.Y, o.Y), Mathf.Max(max.Z, o.Z));
            }
            mm.CustomAabb = new Aabb(min, max - min).Grow(meshExtent);
        }
        var node = new MultiMeshInstance3D { Name = name, Multimesh = mm };
        AddChild(node);
        return node;
    }

    // ── POI markers (revealed, unconsumed) ───────────────────────────────────

    private void RebuildMarkers()
    {
        foreach (var m in _markers) m.QueueFree();
        _markers.Clear();
        foreach (var c in _windowTiles)
        {
            if (_fog.FogAt(c) != Fog.Revealed) continue;   // silhouette hides contents (G2)
            var ov = _overlay.OverlayAt(c);
            if (ov.Poi == OverworldHex.POIType.None || ov.Consumed) continue;
            var col = PoiColor(ov.Poi);
            var pos = TileOrigin(c.X, c.Y); pos.Y = TileHeight(c) + 0.9f;
            _markers.Add(AddChildReturn(new MeshInstance3D
            {
                // A7: flattened paint-dab, matching the atlas's POI language.
                Mesh = new SphereMesh { Radius = 0.32f, Height = 0.24f, RadialSegments = 10, Rings = 6 },
                // NoDepthTest so a marker is never hidden behind a taller tile at this shallow angle —
                // the "markers don't reliably appear" fix. It reads through terrain like a map pin.
                MaterialOverride = new StandardMaterial3D
                { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0.4f, NoDepthTest = true },
                Position = pos,
            }));
        }
    }

    // ── Party pawn ────────────────────────────────────────────────────────────

    private void BuildPawn(bool animate = false)
    {
        // Refresh path (animate): the pawn already exists — glide it to the new
        // party tile instead of freeing and re-spawning it, so a host-driven run
        // reads as a walk, not a blink. Matches MoveParty's standalone tween.
        if (animate && _pawn != null)
        {
            var target = TileOrigin(_party.X, _party.Y);
            target.Y = TileHeight(_party);
            var tw = CreateTween();
            tw.TweenProperty(_pawn, "position", target, 0.18)
              .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            return;
        }
        _pawn?.QueueFree();
        _pawn = new Node3D { Name = "PartyPawn" };
        var body = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.28f, Height = 0.55f, RadialSegments = 8, Rings = 0 },
            MaterialOverride = new StandardMaterial3D
            { AlbedoColor = new Color(0.92f, 0.9f, 0.98f), EmissionEnabled = true, Emission = UITheme.Violet, EmissionEnergyMultiplier = 0.3f },
            Position = new Vector3(0f, 0.28f, 0f),
        };
        var head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f, RadialSegments = 10, Rings = 6 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.9f, 0.98f) },
            Position = new Vector3(0f, 0.64f, 0f),
        };
        var lamp = new OmniLight3D { LightColor = new Color(0.8f, 0.68f, 1f), LightEnergy = 1.1f, OmniRange = 7f, Position = new Vector3(0f, 1.3f, 0f) };
        _pawn.AddChild(body); _pawn.AddChild(head); _pawn.AddChild(lamp);
        var p = TileOrigin(_party.X, _party.Y); p.Y = TileHeight(_party);
        _pawn.Position = p;
        AddChild(_pawn);
    }

    // ── Move-option hints (adjacent, walkable, with true cost) ───────────────

    private void RebuildMoveHints()
    {
        foreach (var h in _moveHints) h.QueueFree();
        _moveHints.Clear();
        ClearStridePath();   // a moved/streamed window invalidates the old ribbon



        var fromTile = _world.GetTile(_party.X, _party.Y);
        var (pq, pr) = HexCoord.OffsetToAxial(_party.X, _party.Y);
        foreach (var (dq, dr) in HexCoord.AxialDirections)
        {
            var (nc, nr) = HexCoord.AxialToOffset(pq + dq, pr + dr);
            var coord = new Vector2I(nc, nr);
            if (!InWindow(coord)) continue;
            var t = _world.GetTile(nc, nr);
            if (t.IsWater) continue;   // party rule: only water blocks

            // TRUE cost via the shared cost fn (the WorldTile overload from Step 3) —
            // the same number the live run charges. Colour signals road/ford like 2D.
            int pathfinder = EquipmentLoadout.PartyPathfinder(t.Terrain.ToString());
            int cost = OverworldMovementCost.StepCost(t.Terrain, fromTile, _party, coord, pathfinder);
            bool road = OverworldMovementCost.EdgeHasRoad(fromTile, _party, coord);
            bool ford = OverworldMovementCost.EdgeHasUnbridgedRiver(fromTile, _party, coord);
            Color tint = road ? UITheme.MoveHighlightCheap
                       : ford ? UITheme.MoveHighlightExpensive
                       : cost <= 1 ? UITheme.MoveHighlightCheap
                       : cost == 2 ? UITheme.MoveHighlightModerate
                       : UITheme.MoveHighlightExpensive;

            // Playtest 2026-08-21 redesign: the old translucent hex-disc fills
            // implied a tile lattice the smooth heightmap no longer shows, and
            // their 30%-alpha colours vanished into same-hue terrain (amber on
            // savanna). Now: a crisp UNSHADED ring — reads as UI, not ground —
            // over a near-black underlay disc that guarantees contrast on any
            // terrain colour, in any chamber lighting. Circles, not hexagons:
            // the ground has no cells to echo.
            // Float the hint a touch higher and render it with NoDepthTest so the
            // undulating terrain can never poke through the flat ring — it reads as
            // a UI pin over the map (the same treatment the POI markers use), not a
            // decal welded to a sloped tile. (Was clipping into terrain constantly.)
            var pos = TileOrigin(nc, nr); pos.Y = TileHeight(coord) + 0.18f;

            var under = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = HexR * 0.62f, BottomRadius = HexR * 0.62f,
                                          Height = 0.02f, RadialSegments = 32, Rings = 0 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.02f, 0.02f, 0.05f, 0.45f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    NoDepthTest = true,
                },
                Position = pos - new Vector3(0f, 0.03f, 0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(under); _moveHints.Add(under);

            var ring = new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = HexR * 0.46f, OuterRadius = HexR * 0.58f,
                                       Rings = 32, RingSegments = 6 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.95f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    NoDepthTest = true,
                },
                Position = pos,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(ring); _moveHints.Add(ring);

            var label = new Label3D
            {
                Text = cost.ToString(), Position = pos + new Vector3(0f, 0.4f, 0f),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                FontSize = 40, PixelSize = 0.012f,
                Modulate = Colors.White, OutlineSize = 12,
                OutlineModulate = new Color(0.02f, 0.02f, 0.05f, 1f),
            };
            AddChild(label); _moveHints.Add(label);
        }
    }

    // ── §3.4 Stride-order path preview ────────────────────────────────────────
    // The manager plans the path (over the real fuel cost) and hands us the tiles
    // in world-offset coords plus the total fuel estimate; we draw a dotted ribbon
    // of NoDepthTest pins along it with the estimate floating at the goal. Purely a
    // preview — clicking still runs through the manager (execution is F8b).

    /// <summary>Draw the stride ribbon to a goal. Charted route (`exploratory` =
    /// false): `worldPath` is the tiles after the castle ending on the goal, `fuel`
    /// the total estimate, drawn as solid pins + a "~N fuel" label. Exploratory
    /// (`exploratory` = true): `worldPath` is just [castle, goal] and the line is
    /// drawn dashed toward the unknown with a "March into the unknown" label.</summary>
    public void ShowStridePath(List<Vector2I> worldPath, int fuel, bool exploratory = false)
    {
        ClearStridePath();
        if (worldPath == null || worldPath.Count == 0)
            return;

        Vector2I g;
        if (exploratory && worldPath.Count == 2)
        {
            // Dashed bearing: sample points along the straight castle→goal segment.
            var a = worldPath[0]; var b = worldPath[1];
            Vector3 pa = TileOrigin(a.X, a.Y); pa.Y = TileHeight(a) + 0.3f;
            Vector3 pb = TileOrigin(b.X, b.Y); pb.Y = TileHeight(b) + 0.3f;
            int dots = Mathf.Clamp((int)(pa.DistanceTo(pb) / (ColSpacing * 0.6f)), 3, 40);
            for (int i = 1; i <= dots; i++)
            {
                float f = (float)i / (dots + 1);
                if (i % 2 == 0) continue;                 // gaps → dashed
                var p = pa.Lerp(pb, f);
                var dash = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 0.1f, Height = 0.2f, RadialSegments = 8, Rings = 4 },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.75f, 0.85f, 1f, 0.85f),
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        NoDepthTest = true,
                    },
                    Position = p,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(dash); _stridePath.Add(dash);
            }
            g = b;
        }
        else
        {
            for (int i = 0; i < worldPath.Count; i++)
            {
                var c = worldPath[i];
                bool goal = i == worldPath.Count - 1;
                var pos = TileOrigin(c.X, c.Y); pos.Y = TileHeight(c) + 0.25f;
                var dot = new MeshInstance3D
                {
                    Mesh = new SphereMesh
                    {
                        Radius = goal ? 0.22f : 0.13f, Height = goal ? 0.44f : 0.26f,
                        RadialSegments = 10, Rings = 5,
                    },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = goal ? new Color(1f, 0.78f, 0.32f, 0.98f)
                                           : new Color(1f, 0.90f, 0.55f, 0.9f),
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        NoDepthTest = true,
                    },
                    Position = pos,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(dot); _stridePath.Add(dot);
            }
            g = worldPath[worldPath.Count - 1];
        }

        var gp = TileOrigin(g.X, g.Y); gp.Y = TileHeight(g) + 0.75f;
        var lbl = new Label3D
        {
            Text = exploratory ? "March into the unknown" : $"~{fuel} fuel",
            Position = gp,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            FontSize = exploratory ? 28 : 34, PixelSize = 0.012f,
            Modulate = exploratory ? new Color(0.78f, 0.88f, 1f) : new Color(1f, 0.86f, 0.5f),
            OutlineSize = 12, OutlineModulate = new Color(0.02f, 0.02f, 0.05f, 1f),
        };
        AddChild(lbl); _stridePath.Add(lbl);
    }

    public void ClearStridePath()
    {
        foreach (var n in _stridePath)
            if (GodotObject.IsInstanceValid(n)) n.QueueFree();
        _stridePath.Clear();
    }

    private bool InWindow(Vector2I c)
        => HexCoord.OffsetDistance(c.X, c.Y, _center.X, _center.Y) <= WindowRadius
           && _world.InBounds(c.X, c.Y);

    /// <summary>Render moving entities (enemy patrols + the roamer) as emissive spheres above their
    /// tiles, so ambushers are visible in the 3D view the way they are in 2D. Rebuilt each call
    /// (a handful of nodes); skipped on Hidden tiles so enemies don't show through fog. Coords are
    /// in the renderer's world-offset space, like everything else the host feeds.</summary>
    public void SetEntities(System.Collections.Generic.IEnumerable<(Vector2I tile, Color color)> entities)
    {
        foreach (var e in _entities) e?.QueueFree();
        _entities.Clear();
        if (entities == null || _fog == null) return;
        foreach (var (tile, color) in entities)
        {
            if (_fog.FogAt(tile) == Fog.Hidden) continue;
            var pos = TileOrigin(tile.X, tile.Y);
            pos.Y = TileHeight(tile) + 0.75f;
            // A downward CONE (pin) — distinct from the POI spheres, so moving units read as units.
            _entities.Add(AddChildReturn(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.34f, BottomRadius = 0f, Height = 0.72f, RadialSegments = 8, Rings = 0 },
                MaterialOverride = new StandardMaterial3D
                { AlbedoColor = color, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = 0.6f, NoDepthTest = true },
                Position = pos,
            }));
        }
    }

    private Node3D AddChildReturn(Node3D n) { AddChild(n); return n; }

    // ════════════════════════════════════════════════════════════════════════
    // Fog-aware color — marked copy of WorldAtlas3D's helpers (see header)
    // ════════════════════════════════════════════════════════════════════════

    private static uint Hash(Vector2I c, uint salt)
    {
        uint h = (uint)(c.X * 73856093) ^ (uint)(c.Y * 19349663) ^ (salt * 83492791u);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16; return h;
    }
    private static float H01(uint h) => (h & 0xFFFFu) / 65535f;

    /// <summary>Slate grey filling a CITY's whole footprint (matches the strategic map's
    /// CityRegionTint), so a capital's outskirts read on the expedition — the gold Seat marker
    /// at its centre is how you interact with the capital itself.</summary>
    private static readonly Color CityRegionTint = new Color(0.42f, 0.43f, 0.47f);

    private Color TileColor(in WorldTile t, Vector2I c, Fog fog)
    {
        // Base terrain/ocean colour + land grade come from the shared Hex3DPalette
        // (identical for the Atlas view). Fog handling and per-tile jitter are
        // view-local: the window's jitter uses a salted &0xFFFF hash, distinct from
        // the Atlas's &1023 noise, so they stay here.
        Color baseCol = Hex3DPalette.TerrainColorOf(t);
        // Hidden is normally routed to the canvas layer in RebuildTiles; this guard
        // is a safety net for any other caller. Silhouette is the UNDERPAINTING —
        // a flat pale wash, terrain hue faintly present (art pass A6; shared with
        // the strategic map via Hex3DPalette so the discovery language can't drift).
        if (fog == Fog.Hidden) return Hex3DPalette.CanvasTone(c.X, c.Y);
        if (fog == Fog.Silhouette) return Hex3DPalette.Underpaint(baseCol);
        // A1: the swatches are final lit-scene painterly colours — the old
        // Grade() + ×1.35 saturation compensation stack is deleted. Both 3D views
        // now light the SAME colours under the SAME daylight rig (A4); if the
        // window still reads flatter than the atlas after that, retune with
        // screenshots rather than reintroducing a per-view grade.
        // City footprint reads as a grey region (revealed tiles only — fog handled above).
        if (t.SettlementIndex >= 0 && _world != null && t.SettlementIndex < _world.Settlements.Count
            && _world.Settlements[t.SettlementIndex].Tier == SettlementTier.City)
            baseCol = CityRegionTint;
        return Jitter(baseCol, c, Hex3DPalette.JitterAmp(t));
    }

    private static Color Jitter(Color c, Vector2I co, float amp)
    {
        float k = 1f + (H01(Hash(co, 999)) - 0.5f) * 2f * amp;
        return new Color(Mathf.Clamp(c.R * k, 0f, 1f), Mathf.Clamp(c.G * k, 0f, 1f), Mathf.Clamp(c.B * k, 0f, 1f), c.A);
    }

    private static Color PoiColor(OverworldHex.POIType k) => k switch
    {
        OverworldHex.POIType.Combat => UITheme.POICombat,
        OverworldHex.POIType.Rest => UITheme.POIRest,
        OverworldHex.POIType.Narrative => UITheme.POINarrative,
        OverworldHex.POIType.Negotiation => UITheme.POINegotiation,
        OverworldHex.POIType.Outpost => UITheme.POIOutpost,
        OverworldHex.POIType.Seat => UITheme.Gold,
        OverworldHex.POIType.Settlement => UITheme.ArcaneBlue,
        OverworldHex.POIType.SupplyCache => UITheme.Success,
        _ => UITheme.TextPrimary,
    };
}
