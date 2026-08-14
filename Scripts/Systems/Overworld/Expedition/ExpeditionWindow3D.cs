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
    private GeometryInstance3D _landLayer;      // welded ArrayMesh (stage 2) or MultiMesh fallback
    private MultiMeshInstance3D _waterLayer;
    private GeometryInstance3D _canvasLayer;    // Hidden fog = unpainted canvas (welded sheet or MultiMesh fallback)
    private MeshInstance3D _riverLayer;         // A9b: one winding ribbon mesh (RiverMesh)
    private MeshInstance3D _roadLayer;          // stage 2: ground-following ribbon too
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
        PoiKind.Companion => OverworldHex.POIType.Narrative, // K3 rescue sites
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
        var landCoords = new List<Vector2I>();
        var water = new List<(Transform3D xf, Color c)>();
        var canvas = new List<(Transform3D xf, Color c)>();
        var canvasCoords = new List<Vector2I>();
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
                canvasCoords.Add(c);
                if (!UseWeldedTerrain)
                {
                    float edge = HasPaintedNeighbor(c) ? Hex3DPalette.WetEdgeAmount(c.X, c.Y) : 0f;
                    canvas.Add((xf, Hex3DPalette.CanvasTone(c.X, c.Y, edge)));
                }
            }
            else if (t.IsWater) water.Add((xf, TileColor(t, c, fog)));
            else
            {
                landCoords.Add(c);
                if (!UseWeldedTerrain)
                    land.Add((xf, TileColor(t, c, fog)));
            }
        }

        // The painting must end ON PAPER, not on void: extend the canvas a few
        // rings past the loaded window so the walkable disc sits on parchment
        // (the hex-scalloped land edge then reads as the painted area's edge on
        // the sheet — same metaphor as the strategic map). Not pickable: PickTile
        // iterates _windowTiles only.
        var known = new HashSet<Vector2I>(_windowTiles);
        var frontier = new List<Vector2I>(_windowTiles);
        for (int ring = 0; ring < 3; ring++)
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
                    if (known.Contains(nco) || !_world.InBounds(nc, nr))
                        continue;
                    known.Add(nco);
                    next.Add(nco);
                    canvasCoords.Add(nco);
                    if (!UseWeldedTerrain)
                    {
                        var nxf = new Transform3D(HexYaw * Basis.FromScale(new Vector3(1f, FogSlabHeight, 1f)),
                                                  TileOrigin(nc, nr) + new Vector3(0f, FogSlabHeight * 0.5f, 0f));
                        canvas.Add((nxf, Hex3DPalette.CanvasTone(nc, nr)));
                    }
                }
            }
            frontier = next;
        }

        if (UseWeldedTerrain)
        {
            // Terrain break-up stage 2 (user ruling: "the full welded look"):
            // one merged ArrayMesh — corner-averaged welds fuse tiles into
            // continuous ground below the cliff threshold, undulation is BAKED
            // into the vertices (so C# knows the true surface and the stroke
            // ribbons can follow it — the stage-1 shader displacement was
            // invisible to placement, which is why rivers/roads clipped).
            _landLayer = BuildWeldedLand(landCoords);
        }
        else
        {
            // Stage-1 fallback: subdivided tops rolled by the shader.
            _landLayer = MakeTileLayer("WinLand", land, taper: 0.985f, roughness: 0.9f,
                                       prismMode: PainterlyPrism.Land,
                                       customMesh: PainterlyProps.HexTileMesh(0.985f));
            if (((MultiMeshInstance3D)_landLayer).Multimesh.Mesh is ArrayMesh lam
                && lam.SurfaceGetMaterial(0) is ShaderMaterial landSm)
            {
                landSm.SetShaderParameter("grain_scale", 1.8f);
                landSm.SetShaderParameter("grain_strength", 0.11f);
                landSm.SetShaderParameter("top_undulation", 0.06f);
                landSm.SetShaderParameter("undulation_scale", 0.5f);
            }
        }
        _waterLayer = MakeTileLayer("WinWater", water, taper: 1.0f, roughness: 0.55f,
                                    prismMode: PainterlyPrism.Water);
        if (UseWeldedTerrain)
        {
            // Stage 2d (user: the parchment prisms don't match the welded ground):
            // the canvas becomes ONE seamless flat sheet — same corner-centroid
            // construction as the welded land, no grout, paper grain from the
            // shader. Where the sheet meets lower painted ground it walls down.
            _canvasLayer = BuildCanvasSheet(canvasCoords);
        }
        else
        {
            _canvasLayer = MakeTileLayer("WinCanvas", canvas, taper: 0.96f, roughness: 1.0f,
                                         prismMode: PainterlyPrism.Canvas);
        }
        // No shadow casting on canvas: it is the lowest geometry, and coplanar
        // bright slabs self-shadowing under a low sun produce acne (seen on the
        // strategic map).
        _canvasLayer.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
    }

    /// <summary>The unpainted world as one welded flat sheet at the canvas slab
    /// height: per tile a simple 6-triangle fan to corner centroids (shared XZ ⇒
    /// seamless paper), per-tile CanvasTone with the wet edge where the painting
    /// borders it, edge walls where the sheet ends or overhangs lower painted
    /// ground.</summary>
    private MeshInstance3D BuildCanvasSheet(List<Vector2I> coords)
    {
        var set = new HashSet<Vector2I>(coords);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var c in coords)
        {
            Vector3 o = TileOrigin(c.X, c.Y);
            o.Y = FogSlabHeight;
            float edge = HasPaintedNeighbor(c) ? Hex3DPalette.WetEdgeAmount(c.X, c.Y) : 0f;
            Color col = Hex3DPalette.CanvasTone(c.X, c.Y, edge);
            var (q, r) = HexCoord.OffsetToAxial(c.X, c.Y);
            var nOrigin = new Vector3[6];
            var nInSheet = new bool[6];
            var nCoord = new Vector2I[6];
            for (int i = 0; i < 6; i++)
            {
                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                nCoord[i] = new Vector2I(nc, nr);
                nOrigin[i] = _world.InBounds(nc, nr) ? TileOrigin(nc, nr) : o + EdgeDir(i);
                nOrigin[i].Y = FogSlabHeight;
                nInSheet[i] = set.Contains(nCoord[i]);
            }
            var corners = new Vector3[6];
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                corners[i] = (o + nOrigin[i] + nOrigin[j]) / 3f;
                corners[i].Y = FogSlabHeight;
            }
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                TopTri(st, o, col, corners[i], col, corners[j], col);
            }
            // Edge walls where the sheet is not continued by more canvas.
            for (int i = 0; i < 6; i++)
            {
                if (nInSheet[i])
                    continue;
                float floor;
                var nco = nCoord[i];
                if (!_world.InBounds(nco.X, nco.Y))
                    floor = FogSlabHeight - 0.5f;                 // world edge skirt
                else if (_fog.FogAt(nco) == Fog.Hidden)
                    floor = FogSlabHeight - 0.5f;                 // beyond the margin
                else
                {
                    var nt = _world.GetTile(nco.X, nco.Y);
                    float nTop = nt.IsOcean ? 0.08f : nt.IsLake ? 0.12f : TileHeight(nco);
                    if (nTop >= FogSlabHeight - 0.01f)
                        continue;                                  // painted side is higher; it walls to us
                    floor = nTop;
                }
                Vector3 a = corners[(i + 5) % 6];
                Vector3 b = corners[i];
                WallQuad(st, a, b, floor, floor, col, o);
            }
        }
        var mesh = st.Commit();
        var node = new MeshInstance3D
        {
            Name = "WinCanvasSheet",
            Mesh = mesh,
            MaterialOverride = PainterlyPrism.TileMaterial(PainterlyPrism.Canvas, 1.0f),
        };
        AddChild(node);
        return node;
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
        // Stage 2: BOTH rivers and roads are ground-following ribbons — every
        // vertex re-heighted by SampleGround (the same function that built the
        // welded terrain), so strokes lie ON the surface. This is the fix for
        // the reported clipping: placement now sees the real ground.
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
            System.Func<Vector3, float> ground =
                UseWeldedTerrain ? (p => SampleGround(cc, p)) : (System.Func<Vector3, float>)null;
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
                {
                    (riverMids ??= new List<Vector3>()).Add(edgeMid);
                    // Stage 2d — WATERFALL connector: across an UNWELDED cliff
                    // edge the two halves now end at different heights (each
                    // samples its own fan), which read as the river running into
                    // the wall. The higher side drops a short steep ribbon to the
                    // lower side's surface.
                    if (UseWeldedTerrain && inBounds)
                    {
                        var nbrCo = new Vector2I(nc, nr);
                        if (_surf.ContainsKey(nbrCo))
                        {
                            float selfY = SampleGround(cc, edgeMid);
                            float nbrY = SampleGround(nbrCo, edgeMid);
                            if (selfY - nbrY > 0.15f)
                            {
                                Vector3 toNbr = new Vector3(nbr.X - center.X, 0f, nbr.Z - center.Z).Normalized() * 0.14f;
                                var top = new Vector3(edgeMid.X - toNbr.X * 0.3f, selfY + 0.05f, edgeMid.Z - toNbr.Z * 0.3f);
                                var plunge = new Vector3(edgeMid.X + toNbr.X, nbrY + 0.05f, edgeMid.Z + toNbr.Z);
                                riverTiles.Add((top, new List<Vector3> { plunge }, null));
                            }
                        }
                    }
                }
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

    // ── Welded terrain (stage 2 — user ruling: "the full welded look") ──────
    // The combat map's model at window scale: hex corners shared by up to three
    // tiles average their heights (and colours) when the spread stays inside the
    // weld threshold — tiles fuse into continuous rolling ground; bigger steps
    // stay crisp cliffs with real walls. Undulation is baked into the VERTICES
    // (not the shader), so SampleGround can hand the exact surface to the
    // river/road ribbons. Canvas (Hidden) slabs stay a separate hard-edged
    // MultiMesh layer — the unpainted world is flat paper, never welded.

    /// <summary>Kill-switch back to the stage-1 prism look.</summary>
    private static readonly bool UseWeldedTerrain = true;
    /// <summary>Max height difference that still fuses into a slope. 0.50 welds
    /// up to two compressed terrace steps (isolated low pockets become steep
    /// DELLS, not sheer-walled sinkholes — the user's "holes"); only 3+ step
    /// drops remain true cliffs, which makes the remaining cliffs meaningful.</summary>
    private const float WeldThreshold = 0.50f;
    private const float UndulationAmp = 0.06f;

    private struct TileSurf
    {
        public Vector3 Center;         // welded base heights, pre-undulation
        public Vector3[] Corners;      // 6, corner i = centroid of self + nbr i + nbr i+1
        public Vector3[] EdgeMids;     // 6
        public Color CenterCol;
        public Color[] CornerCols;
        public Color[] EdgeCols;
        public bool[] EdgeWelded;      // edge i fused with neighbour i (no wall)
        public Vector2I[] Nbrs;
    }

    private readonly Dictionary<Vector2I, TileSurf> _surf = new();

    private MeshInstance3D BuildWeldedLand(List<Vector2I> coords)
    {
        _surf.Clear();
        var landSet = new HashSet<Vector2I>(coords);

        // Pass 1 — per-tile welded surface data (heights + colours, pre-undulation).
        foreach (var c in coords)
        {
            var t = _world.GetTile(c.X, c.Y);
            var fog = _fog.FogAt(c);
            float h0 = TileHeight(c);
            Color col0 = TileColor(t, c, fog);
            Vector3 o = TileOrigin(c.X, c.Y);
            var (q, r) = HexCoord.OffsetToAxial(c.X, c.Y);

            bool city0 = IsCityTile(c);
            var nOrigin = new Vector3[6];
            var nIsLand = new bool[6];
            var nH = new float[6];
            var nCol = new Color[6];
            var nCity = new bool[6];
            var s = new TileSurf
            {
                Corners = new Vector3[6], EdgeMids = new Vector3[6],
                CornerCols = new Color[6], EdgeCols = new Color[6],
                EdgeWelded = new bool[6], Nbrs = new Vector2I[6],
                Center = new Vector3(o.X, h0, o.Z), CenterCol = col0,
            };
            for (int i = 0; i < 6; i++)
            {
                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                var nco = new Vector2I(nc, nr);
                s.Nbrs[i] = nco;
                nOrigin[i] = _world.InBounds(nc, nr) ? TileOrigin(nc, nr) : o + EdgeDir(i);
                nIsLand[i] = landSet.Contains(nco);
                if (nIsLand[i])
                {
                    nH[i] = TileHeight(nco);
                    nCol[i] = TileColor(_world.GetTile(nc, nr), nco, _fog.FogAt(nco));
                    nCity[i] = IsCityTile(nco);
                }
            }
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                // Corner i = centroid of the three tile centres meeting there —
                // orientation-proof, no assumptions about axial direction order.
                // Stage 2d city rule: HEIGHTS weld across the city boundary, but
                // COLOURS average only among participants on the same side of it —
                // a settlement footprint is a built thing with a hard edge, not a
                // biome gradient.
                Vector3 cp = (o + nOrigin[i] + nOrigin[j]) / 3f;
                WeldCorner(h0, col0, city0,
                           nIsLand[i], nH[i], nCol[i], nCity[i],
                           nIsLand[j], nH[j], nCol[j], nCity[j],
                           out float ch, out Color cc);
                s.Corners[i] = new Vector3(cp.X, ch, cp.Z);
                s.CornerCols[i] = cc;

                Vector3 ep = (o + nOrigin[i]) * 0.5f;
                bool weld = nIsLand[i] && Mathf.Abs(nH[i] - h0) <= WeldThreshold;
                s.EdgeWelded[i] = weld;
                s.EdgeMids[i] = new Vector3(ep.X, weld ? (h0 + nH[i]) * 0.5f : h0, ep.Z);
                s.EdgeCols[i] = (weld && nCity[i] == city0) ? col0.Lerp(nCol[i], 0.5f) : col0;
            }
            _surf[c] = s;
        }

        // Pass 2 — emit tops + cliff/shore walls. Winding is settled per-triangle
        // by the RH-normal sign test (Godot front faces are CW), so no assumption
        // about boundary orientation can break it.
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var c in coords)
        {
            var s = _surf[c];
            Vector3 ctr = Undulate(s.Center);
            for (int j = 0; j < 12; j++)
            {
                int k = (j + 1) % 12;
                TopTri(st, ctr, s.CenterCol,
                       Undulate(Bnd(s, j)), BndCol(s, j),
                       Undulate(Bnd(s, k)), BndCol(s, k));
            }
            // Walls per unwelded edge: our (corner i-1, edgeMid i, corner i) rim
            // down to whatever is really below on the other side — PER VERTEX
            // (a flat shelf left triangular slivers where the neighbour's rim
            // varies corner to corner; matching XZ floors close them exactly).
            for (int i = 0; i < 6; i++)
            {
                if (s.EdgeWelded[i])
                    continue;
                Vector3 a = Undulate(s.Corners[(i + 5) % 6]);
                Vector3 m = Undulate(s.EdgeMids[i]);
                Vector3 b = Undulate(s.Corners[i]);
                float fa = WallFloorAt(s, i, a);
                float fm = WallFloorAt(s, i, m);
                float fb = WallFloorAt(s, i, b);
                if (a.Y <= fa + 0.01f && m.Y <= fm + 0.01f && b.Y <= fb + 0.01f)
                    continue;   // we are the lower side; the neighbour walls down to us
                Color wc = s.CenterCol;
                WallQuad(st, a, m, fa, fm, wc, s.Center);
                WallQuad(st, m, b, fm, fb, wc, s.Center);
            }
        }
        var mesh = st.Commit();

        var mat = PainterlyPrism.TileMaterial(PainterlyPrism.Land, 0.9f);
        if (mat is ShaderMaterial sm)
        {
            // Close-zoom brushwork (undulation is baked — the shader knob stays 0).
            sm.SetShaderParameter("grain_scale", 1.8f);
            sm.SetShaderParameter("grain_strength", 0.11f);
            // Welded cliffs are painted BANKS, not voids: the full skirt darkening
            // + stripes + shadow pushed them near-black (the "torn hole" read).
            sm.SetShaderParameter("skirt_darken", 0.14f);
            sm.SetShaderParameter("stripe_strength", 0.10f);
        }
        var node = new MeshInstance3D { Name = "WinLandWelded", Mesh = mesh, MaterialOverride = mat };
        AddChild(node);
        return node;
    }

    /// <summary>Corner weld by connected components over pairwise height diffs ≤
    /// threshold — symmetric (every participant computes the same subset), so
    /// welded corners are crack-free by construction. Colours additionally
    /// average only among participants on the same side of a city boundary
    /// (heights still weld — the ground is continuous; the paint is not).</summary>
    private static void WeldCorner(float h0, Color c0, bool city0,
                                   bool aIn, float ha, Color ca, bool aCity,
                                   bool bIn, float hb, Color cb, bool bCity,
                                   out float h, out Color col)
    {
        bool sa = aIn && Mathf.Abs(ha - h0) <= WeldThreshold;
        bool sb = bIn && Mathf.Abs(hb - h0) <= WeldThreshold;
        bool ab = aIn && bIn && Mathf.Abs(ha - hb) <= WeldThreshold;
        bool useA = aIn && (sa || (sb && ab));
        bool useB = bIn && (sb || (sa && ab));
        int n = 1;
        h = h0;
        if (useA) { h += ha; n++; }
        if (useB) { h += hb; n++; }
        h /= n;
        int cn = 1;
        float cr = c0.R, cg = c0.G, cbl = c0.B;
        if (useA && aCity == city0) { cr += ca.R; cg += ca.G; cbl += ca.B; cn++; }
        if (useB && bCity == city0) { cr += cb.R; cg += cb.G; cbl += cb.B; cn++; }
        col = new Color(cr / cn, cg / cn, cbl / cn, 1f);
    }

    private bool IsCityTile(Vector2I c)
    {
        var t = _world.GetTile(c.X, c.Y);
        return t.SettlementIndex >= 0 && _world != null
            && t.SettlementIndex < _world.Settlements.Count
            && _world.Settlements[t.SettlementIndex].Tier == SettlementTier.City;
    }

    /// <summary>What an unwelded edge's wall descends to AT a given rim vertex:
    /// neighbour land at its matching rim vertex (same XZ ⇒ the two sides share
    /// wall edges exactly, no slivers), hidden ground at the canvas slab, water
    /// at its pool top, out-of-window at a skirt below us.</summary>
    private float WallFloorAt(in TileSurf s, int i, Vector3 rimVert)
    {
        var nco = s.Nbrs[i];
        if (_surf.TryGetValue(nco, out var ns))
        {
            float best = float.MaxValue;
            float y = ns.Center.Y;
            for (int j = 0; j < 12; j++)
            {
                Vector3 v = Bnd(ns, j);
                float d = (v.X - rimVert.X) * (v.X - rimVert.X) + (v.Z - rimVert.Z) * (v.Z - rimVert.Z);
                if (d < best) { best = d; y = v.Y; }
            }
            return y + Undulation(rimVert.X, rimVert.Z);
        }
        if (!_world.InBounds(nco.X, nco.Y))
            return s.Center.Y - 0.6f;   // window/map boundary skirt
        if (_fog.FogAt(nco) == Fog.Hidden)
            return FogSlabHeight;
        var nt = _world.GetTile(nco.X, nco.Y);
        if (nt.IsOcean) return 0.08f;
        if (nt.IsLake) return 0.12f;
        return s.Center.Y - 0.6f;
    }

    private Vector3 Undulate(Vector3 p) => new Vector3(p.X, p.Y + Undulation(p.X, p.Z), p.Z);

    private static Vector3 Bnd(in TileSurf s, int j)
        => (j & 1) == 0 ? s.EdgeMids[(j >> 1)] : s.Corners[(j >> 1)];

    private static Color BndCol(in TileSurf s, int j)
        => (j & 1) == 0 ? s.EdgeCols[(j >> 1)] : s.CornerCols[(j >> 1)];

    /// <summary>Up-facing triangle under the CW front-face rule: emit so the
    /// RH-normal points DOWN (checked numerically — orientation-proof).</summary>
    private static void TopTri(SurfaceTool st, Vector3 a, Color ca, Vector3 b, Color cb, Vector3 c, Color cc)
    {
        float crossY = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z);
        if (crossY > 0f)
        { (b, c) = (c, b); (cb, cc) = (cc, cb); }
        st.SetColor(ca); st.SetNormal(Vector3.Up); st.AddVertex(a);
        st.SetColor(cb); st.SetNormal(Vector3.Up); st.AddVertex(b);
        st.SetColor(cc); st.SetNormal(Vector3.Up); st.AddVertex(c);
    }

    /// <summary>Outward-facing wall quad from rim segment a→b down to per-vertex
    /// floors. Winding settled numerically: RH-normal must point INWARD (toward
    /// the tile centre) for the face to render outward under the CW rule.</summary>
    private static void WallQuad(SurfaceTool st, Vector3 a, Vector3 b, float aFloorY, float bFloorY, Color col, Vector3 centre)
    {
        Vector3 a2 = new Vector3(a.X, aFloorY, a.Z);
        Vector3 b2 = new Vector3(b.X, bFloorY, b.Z);
        Vector3 n = (b - a).Cross(a2 - a);
        Vector3 outward = new Vector3((a.X + b.X) * 0.5f - centre.X, 0f, (a.Z + b.Z) * 0.5f - centre.Z);
        bool swap = n.Dot(outward) > 0f;   // RH-normal outward would render inward — swap
        Vector3 t0 = swap ? b : a, t1 = swap ? a : b;
        Vector3 b0 = swap ? b2 : a2, b1 = swap ? a2 : b2;
        Vector3 wn = swap ? -n : n;
        wn = wn.LengthSquared() > 1e-8f ? -wn.Normalized() : Vector3.Up;   // face normal points outward
        st.SetColor(col); st.SetNormal(wn); st.AddVertex(t0);
        st.SetColor(col); st.SetNormal(wn); st.AddVertex(t1);
        st.SetColor(col); st.SetNormal(wn); st.AddVertex(b1);
        st.SetColor(col); st.SetNormal(wn); st.AddVertex(t0);
        st.SetColor(col); st.SetNormal(wn); st.AddVertex(b1);
        st.SetColor(col); st.SetNormal(wn); st.AddVertex(b0);
    }

    /// <summary>The true rendered ground height at a world point inside (or just
    /// beside) a tile — welded fan interpolation + baked undulation. This is the
    /// single source of truth the stroke ribbons follow.</summary>
    private float SampleGround(Vector2I tile, Vector3 p)
    {
        if (!_surf.TryGetValue(tile, out var s))
            return RenderedTileHeight(tile) + Undulation(p.X, p.Z);
        if (TryFan(s, p, out float inY, out float bestY))
            return inY + Undulation(p.X, p.Z);
        // Cross-tile (stage 2d, river-clip fix): bank vertices near a welded edge
        // can land in the NEIGHBOUR's fan — extrapolating our own edge plane there
        // read as clipping. Ask the neighbours for the true surface first.
        for (int i = 0; i < 6; i++)
        {
            if (_surf.TryGetValue(s.Nbrs[i], out var ns) && TryFan(ns, p, out float ny, out _))
                return ny + Undulation(p.X, p.Z);
        }
        return bestY + Undulation(p.X, p.Z);
    }

    /// <summary>Fan interpolation for one tile: true when p lies inside; bestY is
    /// the nearest-triangle extrapolation for the fallback path.</summary>
    private static bool TryFan(in TileSurf s, Vector3 p, out float y, out float bestY)
    {
        float bestErr = float.MaxValue;
        bestY = s.Center.Y;
        for (int j = 0; j < 12; j++)
        {
            int k = (j + 1) % 12;
            if (BaryY(s.Center, Bnd(s, j), Bnd(s, k), p, out float ty, out float err))
            { y = ty; return true; }
            if (err < bestErr) { bestErr = err; bestY = ty; }
        }
        y = bestY;
        return false;
    }

    /// <summary>2D barycentric plane interpolation in XZ; err is how far outside
    /// the triangle the point sits (≤ small epsilon counts as inside).</summary>
    private static bool BaryY(Vector3 a, Vector3 b, Vector3 c, Vector3 p, out float y, out float err)
    {
        float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (Mathf.Abs(d) < 1e-6f) { y = a.Y; err = float.MaxValue; return false; }
        float l0 = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Z - c.Z)) / d;
        float l1 = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Z - c.Z)) / d;
        float l2 = 1f - l0 - l1;
        y = l0 * a.Y + l1 * b.Y + l2 * c.Y;
        err = -Mathf.Min(l0, Mathf.Min(l1, l2));
        return err <= 0.02f;
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
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);
        float a = UHash(xi, zi), b = UHash(xi + 1, zi);
        float c = UHash(xi, zi + 1), d = UHash(xi + 1, zi + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
    }

    private static float Undulation(float wx, float wz)
        => ((UNoise(wx * 0.5f, wz * 0.5f) + UNoise(wx * 1.2f + 31.7f, wz * 1.2f) * 0.5f) - 0.75f) * UndulationAmp;

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
            float h = TileHeight(c);
            var basePos = TileOrigin(c.X, c.Y);
            // Stage 2: props must stand on the WELDED ground, not the flat tile
            // height (deviation up to ~±0.2 would float or bury them).
            float GroundAt(Vector3 p) => UseWeldedTerrain ? SampleGround(c, p) : h;
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
