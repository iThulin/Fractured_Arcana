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

    // ── Scene ───────────────────────────────────────────────────────────────
    private Camera3D _camera;
    private MultiMeshInstance3D _landLayer, _waterLayer;
    private readonly List<Node3D> _decor = new();
    private readonly List<Node3D> _markers = new();
    private readonly List<Node3D> _moveHints = new();
    private Node3D _pawn;

    private Vector3 _camTarget = Vector3.Zero;
    private float _camDist = 26f;
    private const float CamDistMin = 8f, CamDistMax = 60f;
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
                AmbientLightColor = new Color(0.44f, 0.43f, 0.54f),
                AmbientLightEnergy = 0.6f,
            },
        });
        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.9f, 0.74f, 1f),
            LightEnergy = 1.7f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 120f,
            ShadowBlur = 1.0f,
        };
        AddChild(sun);
        sun.RotationDegrees = new Vector3(-32f, -40f, 0f);
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
        _camera.Position = _camTarget + new Vector3(0f, Mathf.Sin(pitch), Mathf.Cos(pitch)) * _camDist;
        _camera.LookAt(_camTarget, Vector3.Up);
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
        else if (ev is InputEventMouseMotion mm && _dragging)
        {
            if (mm.Relative.LengthSquared() > 1f) _dragMoved = true;
            float k = _camDist * 0.0016f;
            float pitchSin = Mathf.Max(0.3f, (_camera.Position - _camTarget).Normalized().Y);
            _camTarget += new Vector3(-mm.Relative.X * k, 0f, -mm.Relative.Y * k / pitchSin);
            PlaceCamera();
        }
    }

    private void PickAndMove(Vector2 screenPos)
    {
        if (_camera == null) return;

        // SCREEN-SPACE pick that respects tile HEIGHT: project each RENDERED tile's
        // TOP to the screen and take the nearest to the click. A y=0 ground raycast
        // (the old way) lands PAST a raised tile onto the hex behind it — the source
        // of "the pawn moves to the wrong hex," worst on hills/mountains at this
        // shallow angle. Hidden tiles aren't drawn, so they're not click targets.
        Vector2I best = default;
        float bestD = float.MaxValue;
        bool found = false;
        foreach (var c in _windowTiles)
        {
            if (_fog.FogAt(c) == Fog.Hidden)
                continue;
            Vector3 top = TileOrigin(c.X, c.Y);
            top.Y = TileHeight(c);
            if (_camera.IsPositionBehind(top))
                continue;
            float d = _camera.UnprojectPosition(top).DistanceSquaredTo(screenPos);
            if (d < bestD) { bestD = d; best = c; found = true; }
        }
        if (!found) return;

        // Only an adjacent, non-water tile is a legal step (party rule: water blocks;
        // everything else walkable). Same gate the 2D token uses.
        if (HexCoord.OffsetDistance(best.X, best.Y, _party.X, _party.Y) != 1) return;
        if (_world.GetTile(best.X, best.Y).IsWater) return;
        if (SelfDrive)
            MoveParty(best);          // harness: move ourselves
        else
            MoveRequested?.Invoke(best);   // live: the host drives the real run
    }

    private void MoveParty(Vector2I coord)
    {
        _party = coord;
        UpdateVision();
        // Recolor + re-decorate + re-mark for the new fog, then re-hint moves.
        RebuildTiles();
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

        var land = new List<(Transform3D xf, Color c)>();
        var water = new List<(Transform3D xf, Color c)>();
        foreach (var c in _windowTiles)
        {
            var fog = _fog.FogAt(c);
            // "Lantern in the dark": Hidden tiles are NOT drawn at all — the window
            // is a lit island of known ground floating in void that grows as you
            // walk, rather than a fully-visible dim disc. (Also cheaper: only
            // charted+explored tiles instantiate.) This still prevents silhouette
            // leak — you simply see nothing where you haven't been.
            if (fog == Fog.Hidden) continue;
            var t = _world.GetTile(c.X, c.Y);
            float h = TileHeight(c);
            var xf = new Transform3D(HexYaw * Basis.FromScale(new Vector3(1f, h, 1f)),
                                     TileOrigin(c.X, c.Y) + new Vector3(0f, h * 0.5f, 0f));
            var col = TileColor(t, c, fog);
            if (t.IsWater) water.Add((xf, col));
            else land.Add((xf, col));
        }

        _landLayer = MakeTileLayer("WinLand", land, taper: 0.96f, roughness: 0.65f);
        _waterLayer = MakeTileLayer("WinWater", water, taper: 1.0f, roughness: 0.15f);
    }

    private MultiMeshInstance3D MakeTileLayer(string name, List<(Transform3D xf, Color c)> items,
                                              float taper, float roughness)
    {
        var mesh = new CylinderMesh
        {
            TopRadius = HexR * taper, BottomRadius = HexR, Height = 1f,
            RadialSegments = 6, Rings = 0, CapBottom = false,
        };
        mesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = roughness };
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
        return h;
    }

    // ── Decorations (revealed land only) ─────────────────────────────────────

    private void RebuildDecorations()
    {
        foreach (var d in _decor) d.QueueFree();
        _decor.Clear();
        var trees = new List<(Transform3D, Color)>();
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
                int n = 2 + (int)(Hash(c, 1) % 2);
                for (int i = 0; i < n; i++)
                {
                    float a = H01(Hash(c, (uint)(7 + i))) * Mathf.Tau;
                    float rad = H01(Hash(c, (uint)(31 + i))) * 0.55f;
                    float s = 0.55f + H01(Hash(c, (uint)(53 + i))) * 0.55f;
                    var pos = basePos + new Vector3(Mathf.Cos(a) * rad, h + 0.8f * s * 0.5f - 0.04f, Mathf.Sin(a) * rad);
                    trees.Add((new Transform3D(Basis.FromScale(new Vector3(s, s, s)), pos),
                              new Color(0.16f, 0.30f, 0.14f)));
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
        _decor.Add(MakeDecoLayer("WinTrees", trees, 0.26f, 0.8f));
        _decor.Add(MakeDecoLayer("WinPeaks", peaks, 0.34f, 0.9f));
    }

    private MultiMeshInstance3D MakeDecoLayer(string name, List<(Transform3D, Color)> items, float br, float ht)
    {
        var mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = br, Height = ht, RadialSegments = 5, Rings = 0 };
        mesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = mesh, InstanceCount = items.Count,
        };
        for (int i = 0; i < items.Count; i++)
        { mm.SetInstanceTransform(i, items[i].Item1); mm.SetInstanceColor(i, items[i].Item2); }
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
            var pos = TileOrigin(c.X, c.Y); pos.Y = TileHeight(c) + 0.55f;
            _markers.Add(AddChildReturn(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.26f, Height = 0.52f, RadialSegments = 10, Rings = 6 },
                MaterialOverride = new StandardMaterial3D
                { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0.4f },
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

    private Color TileColor(in WorldTile t, Vector2I c, Fog fog)
    {
        // Base terrain/ocean colour + land grade come from the shared Hex3DPalette
        // (identical for the Atlas view). Fog handling and per-tile jitter are
        // view-local: the window's jitter uses a salted &0xFFFF hash, distinct from
        // the Atlas's &1023 noise, so they stay here.
        Color baseCol = Hex3DPalette.TerrainColorOf(t);
        if (fog == Fog.Hidden) return UITheme.StrategicUnseen;
        if (fog == Fog.Silhouette) return baseCol.Lerp(UITheme.StrategicCharted, 0.55f);
        if (t.IsLand) baseCol = Hex3DPalette.Grade(baseCol);
        return Jitter(baseCol, c, t.IsWater ? 0.02f : 0.04f);
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
