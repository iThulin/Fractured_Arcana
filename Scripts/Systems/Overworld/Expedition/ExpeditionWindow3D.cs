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
    private static readonly Basis HexYaw = new Basis(Vector3.Up, Mathf.Pi / 6f);

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
    private MultiMeshInstance3D _landLayer, _waterLayer;
    private MultiMeshInstance3D _canvasLayer;   // Hidden fog = unpainted canvas (art pass A6)
    private MeshInstance3D _riverLayer;        // A9b: one winding ribbon mesh (RiverMesh)
    private MultiMeshInstance3D _roadLayer;
    private readonly List<Node3D> _decor = new();
    private readonly List<Node3D> _markers = new();
    private readonly List<Node3D> _entities = new();   // moving entities: enemy patrols + roamer
    private readonly List<Node3D> _moveHints = new();
    private Node3D _pawn;

    private Vector3 _camTarget = Vector3.Zero;
    private float _camDist = 26f;
    private float _camYaw = 0f;   // camera orbit yaw (Q/E rotate); 0 = looking down +Z as before
    private const float CamDistMin = 8f, CamDistMax = 60f;
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
        _ => OverworldHex.POIType.Combat,
    };

    /// <summary>Radius reveal around the party (mirrors FogOfWarManager.UpdateVision):
    /// within VisionRadius → Revealed, one ring beyond & still Hidden → Silhouette.</summary>
    private void UpdateVision()
    {
        foreach (var c in _windowTiles)
        {
            int d = HexCoord.OffsetDistance(c.X, c.Y, _party.X, _party.Y);
            var cur = _fog.FogAt(c);
            if (d <= VisionRadius) _fog.Set(c, Fog.Revealed);
            else if (d <= VisionRadius + 1 && cur == Fog.Hidden) _fog.Set(c, Fog.Silhouette);
        }
    }

    // ── Environment + camera ────────────────────────────────────────────────

    private void BuildEnvironment()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
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
            },
        });
        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.97f, 0.90f, 1f),
            LightEnergy = 1.0f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 120f,
            ShadowBlur = 1.0f,
        };
        AddChild(sun);
        sun.RotationDegrees = new Vector3(-45f, -40f, 0f);
    }

    private Vector3 TileOrigin(int col, int row)
        => new Vector3(col * ColSpacing, 0f,
                       row * RowSpacing + (((col & 1) == 1) ? RowSpacing * 0.5f : 0f));

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
        float zoom01 = Mathf.InverseLerp(CamDistMin, CamDistMax, _camDist);
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
        if (!AcceptInput || _camera == null) return;
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 0.9f, CamDistMin, CamDistMax); PlaceCamera(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 1.1f, CamDistMin, CamDistMax); PlaceCamera(); }
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
        // Only an adjacent, non-water tile is a legal step (party rule: water blocks; everything
        // else walkable, including into adjacent fog to explore it). Same gate the 2D token uses.
        if (HexCoord.OffsetDistance(best.X, best.Y, _party.X, _party.Y) != 1) return;
        if (_world.GetTile(best.X, best.Y).IsWater) return;
        if (SelfDrive)
            MoveParty(best);          // harness: move ourselves
        else
            MoveRequested?.Invoke(best);   // live: the host drives the real run
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

    private void RebuildTiles()
    {
        _landLayer?.QueueFree();
        _waterLayer?.QueueFree();
        _canvasLayer?.QueueFree();

        var land = new List<(Transform3D xf, Color c)>();
        var water = new List<(Transform3D xf, Color c)>();
        var canvas = new List<(Transform3D xf, Color c)>();
        foreach (var c in _windowTiles)
        {
            var fog = _fog.FogAt(c);
            var t = _world.GetTile(c.X, c.Y);
            // Unexplored (Hidden) tiles render as a FLAT slab of UNPAINTED CANVAS
            // (art pass A6) — the run's frontier is a live painting edge; walking
            // paints the world in. Their terrain HEIGHT is not revealed (flat), so
            // the fog doesn't leak elevation, and land + water share one canvas
            // layer so no coastline leaks through mesh differences either.
            // Explored/charted tiles render normally.
            bool hidden = fog == Fog.Hidden;
            float h = hidden ? FogSlabHeight : TileHeight(c);
            var xf = new Transform3D(HexYaw * Basis.FromScale(new Vector3(1f, h, 1f)),
                                     TileOrigin(c.X, c.Y) + new Vector3(0f, h * 0.5f, 0f));
            if (hidden)
            {
                float edge = HasPaintedNeighbor(c) ? Hex3DPalette.WetEdgeAmount(c.X, c.Y) : 0f;
                canvas.Add((xf, Hex3DPalette.CanvasTone(c.X, c.Y, edge)));
            }
            else if (t.IsWater) water.Add((xf, TileColor(t, c, fog)));
            else land.Add((xf, TileColor(t, c, fog)));
        }

        // Terrain break-up stage 1 (window-only): subdivided tile tops on a
        // thinner grout, rolled by the shader's top_undulation — the ground
        // undulates ACROSS tiles while height steps stay crisp for navigation.
        _landLayer = MakeTileLayer("WinLand", land, taper: 0.985f, roughness: 0.9f,
                                   prismMode: PainterlyPrism.Land,
                                   customMesh: PainterlyProps.HexTileMesh(0.985f));
        // Close-zoom identity (user: "everything is too similar"): the atlas
        // grain settings are too broad to read at walking distance — finer,
        // slightly stronger brushwork, plus the undulation, window only.
        if (_landLayer.Multimesh.Mesh is ArrayMesh lam
            && lam.SurfaceGetMaterial(0) is ShaderMaterial landSm)
        {
            landSm.SetShaderParameter("grain_scale", 1.8f);
            landSm.SetShaderParameter("grain_strength", 0.11f);
            landSm.SetShaderParameter("top_undulation", 0.06f);
            landSm.SetShaderParameter("undulation_scale", 0.5f);
        }
        _waterLayer = MakeTileLayer("WinWater", water, taper: 1.0f, roughness: 0.55f,
                                    prismMode: PainterlyPrism.Water);
        // No shadow casting on canvas: it is the lowest geometry, and coplanar
        // bright slabs self-shadowing under a low sun produce acne (seen on the
        // strategic map).
        _canvasLayer = MakeTileLayer("WinCanvas", canvas, taper: 0.96f, roughness: 1.0f,
                                     prismMode: PainterlyPrism.Canvas);
        _canvasLayer.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
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

    private MultiMeshInstance3D MakeTileLayer(string name, List<(Transform3D xf, Color c)> items,
                                              float taper, float roughness, int prismMode,
                                              Mesh customMesh = null)
    {
        Mesh mesh = customMesh ?? new CylinderMesh
        {
            TopRadius = HexR * taper, BottomRadius = HexR, Height = 1f,
            RadialSegments = 6, Rings = 0, CapBottom = false,
        };
        // A2/A3-lite: shared painterly prism shader (same factory as the atlas —
        // the two views must not drift); roughness is the fallback material's.
        // The material lives on the mesh either way.
        if (mesh is PrimitiveMesh pm)
            pm.Material = PainterlyPrism.TileMaterial(prismMode, roughness);
        else if (mesh is ArrayMesh am)
            am.SurfaceSetMaterial(0, PainterlyPrism.TileMaterial(prismMode, roughness));
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = mesh, InstanceCount = items.Count,
        };
        for (int i = 0; i < items.Count; i++)
        { mm.SetInstanceTransform(i, items[i].xf); mm.SetInstanceColor(i, items[i].c); }
        var node = new MultiMeshInstance3D { Name = name, Multimesh = mm };
        AddChild(node);
        return node;
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
        // A9b: rivers become WINDING RIBBONS (RiverMesh — same builder as the
        // atlas); roads stay straight strokes.
        var riverTiles = new List<(Vector3 center, List<Vector3> mids)>();
        var roads = new List<Transform3D>();

        foreach (var c in _windowTiles)
        {
            if (_fog.FogAt(c) == Fog.Hidden) continue;
            var tile = _world.GetTile(c.X, c.Y);
            if (tile.IsWater) continue;                          // no rivers/roads over open water
            if ((tile.RiverEdges | tile.RoadEdges) == 0) continue;

            Vector3 center = TileOrigin(c.X, c.Y);
            center.Y = TileHeight(c) + 0.03f;   // hug the ground — drawn water, not a floating strip
            List<Vector3> riverMids = null;
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
                // dives into the fog) — halves meet, strokes slope, no seam jumps.
                float nbrH = inBounds ? RenderedTileHeight(new Vector2I(nc, nr)) : TileHeight(c);
                Vector3 edgeMid = new Vector3((center.X + nbr.X) * 0.5f,
                                              (TileHeight(c) + nbrH) * 0.5f + 0.03f,
                                              (center.Z + nbr.Z) * 0.5f);
                if (riv)
                    (riverMids ??= new List<Vector3>()).Add(edgeMid);
                if (road)
                {
                    Vector3 seg = edgeMid - center;
                    float len = seg.Length();
                    if (len < 1e-4f) continue;
                    // Tilted basis: local +X along the sloped segment, slightly
                    // overlength so joints at slope kinks close.
                    Vector3 ax = seg / len;
                    Vector3 az = ax.Cross(Vector3.Up);
                    az = az.LengthSquared() > 1e-6f ? az.Normalized() : Vector3.Forward;
                    Vector3 ay = az.Cross(ax).Normalized();
                    roads.Add(new Transform3D(new Basis(ax * (len * 1.05f), ay, az),
                                              (center + edgeMid) * 0.5f));
                }
            }
            if (riverMids != null)
                riverTiles.Add((center, riverMids));
        }

        _riverLayer = new MeshInstance3D
        {
            Name = "WinRivers",
            // A9c: widened with the atlas (window is closer, so slightly narrower).
            Mesh = RiverMesh.Build(riverTiles, 0.30f, Hex3DPalette.RiverWater, Hex3DPalette.RiverBank),
            MaterialOverride = PainterlyPrism.RiverMaterial(),
        };
        AddChild(_riverLayer);
        _roadLayer = MakeEdgeLayer("WinRoads", roads, new Vector3(1f, 0.03f, 0.15f), Hex3DPalette.RoadStroke);
    }

    /// <summary>A tile's height as it actually RENDERS: hidden tiles sit at the
    /// canvas slab (FogSlabHeight), not TileHeight's internal void value — used
    /// for edge-midpoint averaging so strokes meet what is really drawn.</summary>
    private float RenderedTileHeight(Vector2I c)
        => _fog.FogAt(c) == Fog.Hidden ? FogSlabHeight : TileHeight(c);

    /// <summary>Approx render-space direction to a border tile's missing neighbour.</summary>
    private static Vector3 EdgeDir(int i)
    {
        var (dq, dr) = HexCoord.AxialDirections[i];
        return new Vector3(dq * ColSpacing, 0f, (dr + dq * 0.5f) * RowSpacing);
    }

    private MultiMeshInstance3D MakeEdgeLayer(string name, List<Transform3D> xfs, Vector3 size, Color color)
    {
        var mesh = new BoxMesh { Size = size };
        // A9: matte ink stroke, no sheen.
        mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.95f };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh, InstanceCount = xfs.Count,
        };
        for (int i = 0; i < xfs.Count; i++) mm.SetInstanceTransform(i, xfs[i]);
        // Scatter law: explicit CustomAabb (was latent here too).
        if (xfs.Count > 0)
        {
            Vector3 min = xfs[0].Origin, max = min;
            for (int i = 1; i < xfs.Count; i++)
            {
                var o = xfs[i].Origin;
                min = new Vector3(Mathf.Min(min.X, o.X), Mathf.Min(min.Y, o.Y), Mathf.Min(min.Z, o.Z));
                max = new Vector3(Mathf.Max(max.X, o.X), Mathf.Max(max.Y, o.Y), Mathf.Max(max.Z, o.Z));
            }
            mm.CustomAabb = new Aabb(min, max - min).Grow(HexR);
        }
        var node = new MultiMeshInstance3D { Name = name, Multimesh = mm };
        AddChild(node);
        return node;
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
            float h = TileHeight(c);
            var basePos = TileOrigin(c.X, c.Y);
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
                    var pos = basePos + new Vector3(Mathf.Cos(a) * rad, h - 0.03f, Mathf.Sin(a) * rad);
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
                    var pos = basePos + new Vector3(Mathf.Cos(a) * rad, h - 0.02f, Mathf.Sin(a) * rad);
                    var yaw = new Basis(Vector3.Up, H01(Hash(c, (uint)(79 + i))) * Mathf.Tau);
                    broadleaf.Add((new Transform3D(yaw * Basis.FromScale(new Vector3(s, s * 0.8f, s)), pos),
                                   Jitter(new Color(0.44f, 0.45f, 0.24f), c, 0.14f)));
                }
            }
            else if ((t.Terrain == TT.Mountain || t.Terrain == TT.Volcanic) && Hash(c, 2) % 10 < 5)
            {
                float s = 0.7f + H01(Hash(c, 11)) * 0.6f;
                peaks.Add((new Transform3D(Basis.FromScale(new Vector3(s, s, s)),
                          basePos + new Vector3(0f, h + 0.9f * s * 0.5f - 0.04f, 0f)),
                          t.Terrain == TT.Volcanic ? new Color(0.30f, 0.22f, 0.20f) : new Color(0.55f, 0.52f, 0.48f)));
            }
            else if (t.Terrain == TT.Snow && Hash(c, 3) % 10 < 5)
            {
                float s = 0.6f + H01(Hash(c, 13)) * 0.5f;
                peaks.Add((new Transform3D(Basis.FromScale(new Vector3(s, s, s)),
                          basePos + new Vector3(0f, h + 0.9f * s * 0.5f - 0.04f, 0f)),
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

            var pos = TileOrigin(nc, nr); pos.Y = TileHeight(coord) + 0.06f;
            var disc = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = HexR * 0.7f, BottomRadius = HexR * 0.7f, Height = 0.05f, RadialSegments = 6, Rings = 0 },
                MaterialOverride = new StandardMaterial3D
                { AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.5f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                  EmissionEnabled = true, Emission = tint, EmissionEnergyMultiplier = 0.25f },
                Position = pos,
            };
            AddChild(disc); _moveHints.Add(disc);

            var label = new Label3D
            {
                Text = cost.ToString(), Position = pos + new Vector3(0f, 0.4f, 0f),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                FontSize = 40, PixelSize = 0.012f, Modulate = Colors.White, OutlineSize = 8,
            };
            AddChild(label); _moveHints.Add(label);
        }
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
