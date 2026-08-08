using Godot;
using System.Collections.Generic;

// ============================================================
// WorldAtlas3D.cs
//
// Purpose:        The 3D translation of the strategic view — a
//                 side-by-side PROTOTYPE for evaluating a Civ-style
//                 terrain read of the world against StrategicView's
//                 2D quad paint. Renders the SAME WorldData: one hex
//                 prism per tile via two MultiMeshes — land (tapered
//                 rim, matte) and water (seamless, glinting) — with
//                 per-instance transform carrying terraced elevation
//                 as height and per-instance color carrying the
//                 active lens (graded + jittered), river/road edges as
//                 a second MultiMesh, discovered POIs / settlements /
//                 staging points / shard zones as marker meshes, and
//                 settlement names as Label3Ds. Read-only: it renders
//                 the world, it does not deploy expeditions — the
//                 strategic scene stays the functional map while the
//                 two renderers are compared.
// Layer:          UI (strategic view, 3D prototype)
// Collaborators:  WorldData.cs (the data it renders),
//                 UITheme.cs (all colors), HexCoord.cs (layout math),
//                 StrategicView.cs (the 2D renderer it mirrors),
//                 CampusAtlasPanel.cs (its host tab)
// See:            single_world_refactor_v2.docx §4.2 (the strategic
//                 renderer's cost contract — no per-tile nodes; a
//                 MultiMesh of prisms is one draw call, same as the
//                 quad version)
//
// COLOR PARITY NOTE: the lens color logic below is a deliberate,
// marked COPY of StrategicView's private color methods (TileColor,
// PoliticalLensColor, TerrainColor, KingdomColor, ...). Two renderers
// answering the same questions must not drift — if the 3D atlas is
// adopted past the comparison stage, extract that logic from
// StrategicView into a shared static TileColors class and delete the
// copy here (the QuestLogView precedent: one renderer, many hosts).
// Kept as a copy FOR NOW so the comparison leaves StrategicView
// byte-identical.
// ============================================================

using TT = OverworldHex.TerrainType;

/// <summary>Renders a <see cref="WorldData"/> as low-relief 3D hex terrain inside its own
/// SubViewport. Pure renderer + camera: pan (left-drag), zoom (wheel, pitch eases with
/// distance), click (no drag) reports the picked tile through <see cref="TilePicked"/>.
/// Input is gated by <see cref="AcceptInput"/>, driven by the host container's hover state —
/// the same discipline CampusScreen uses for the campus map's camera.</summary>
public partial class WorldAtlas3D : Node3D
{
    // ── Layout (flat-top, odd-q — must match HexCoord/WorldData) ────────────
    /// <summary>Hex circumradius in world units. Column spacing is 1.5R, row spacing
    /// is √3·R with odd columns pushed HALF A TILE toward +Z — the 3D analogue of
    /// HexCoord.OffsetRenderPosition's "odd columns nudged down".</summary>
    private const float HexR = 1.0f;
    private static readonly float ColSpacing = 1.5f * HexR;
    private static readonly float RowSpacing = Mathf.Sqrt(3f) * HexR;

    /// <summary>Yaw applied to every prism so the hex cross-section sits FLAT-TOP
    /// (a corner on +X). Godot's CylinderMesh puts a corner on +Z; +30° walks it
    /// onto +X, matching the 1.5R column spacing.</summary>
    private static readonly Basis HexYaw = new Basis(Vector3.Up, Mathf.Pi / 6f);

    // ── Heights ─────────────────────────────────────────────────────────────
    private const float VoidSlabHeight = 0.06f;   // Unseen: flat dark slab, no silhouette leak
    private const float OceanHeight = 0.08f;
    private const float LakeHeight = 0.12f;

    // ── State ───────────────────────────────────────────────────────────────
    private WorldData _world;
    private Dictionary<string, KingdomState> _kingdoms = new();
    private bool _revealAll = true;
    private StrategicLens _lens = StrategicLens.Terrain;

    /// <summary>Gates ALL input. The host flips this on container MouseEntered/Exited so
    /// wheel/drag never bleed into the atlas while the pointer is over other campus UI.</summary>
    public bool AcceptInput = false;

    /// <summary>Fired on a click that wasn't a drag, with the picked OFFSET coords.
    /// A plain .NET event, not a Godot signal — the host panel is not a Node.</summary>
    public event System.Action<int, int> TilePicked;

    // ── Scene pieces ────────────────────────────────────────────────────────
    private Camera3D _camera;
    // Pass 1: land and water are SEPARATE MultiMeshes. Land keeps the top taper
    // (the grout line that makes prisms read as tiles); water drops it so the sea
    // fuses into one continuous surface instead of a hex spreadsheet, and takes a
    // low-roughness material so the sun glints on it.
    private MultiMeshInstance3D _landLayer;
    private MultiMeshInstance3D _waterLayer;
    /// <summary>flat tile index → instance index inside its layer's MultiMesh.</summary>
    private int[] _instanceIndexOf;
    /// <summary>flat tile index → true when the instance lives in the water layer.
    /// Membership is by terrain (IsWater), fixed per world — safe across recolors.</summary>
    private bool[] _isWaterInstance;
    private MultiMeshInstance3D _riverLayer;
    private MultiMeshInstance3D _roadLayer;
    private readonly List<Node3D> _markers = new();   // POI/settlement/staging/etc, rebuilt together
    private readonly List<MultiMeshInstance3D> _decoLayers = new();  // stage 1: trees/peaks

    // ── Expedition window preview (stage 2 seed) ────────────────────────────
    // Clicking a staging point previews the deploy window: a boundary ring, the
    // window's tiles lifted in brightness, and a GHOST party token that hops to
    // any clicked tile inside — a feel test for playing expeditions on this
    // surface. Pure WorldData: radius matches StrategicView.DeployWindowRadius's
    // default; nothing here touches ExpeditionManager or run state.
    private const int WindowRadius = 12;
    private int _previewCol = -1, _previewRow = -1;
    private Node3D _ghost;
    private MultiMeshInstance3D _previewRing;
    private List<(int col, int row)> _previewTiles;

    public bool PreviewActive => _previewCol >= 0;

    // Camera rig state (position derived, not stored on nodes).
    private Vector3 _camTarget = Vector3.Zero;
    private float _camDist = 80f;
    // Stage 1: min dropped 12→6 — close zoom is where exploration reads, and the
    // decoration layer + adaptive post blur make it worth going there.
    private const float CamDistMin = 6f, CamDistMax = 150f;

    /// <summary>Fires with zoom01 (0 = closest) whenever the camera moves. The host
    /// panel drives the post shader off this so the miniature blur RELAXES as the
    /// camera comes down — a model stops reading as miniature at ground level.</summary>
    public event System.Action<float> ZoomChanged;
    private bool _dragging = false;
    private bool _dragMoved = false;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildCamera();
        if (_world != null)
            Rebuild();
    }

    // ── Public surface ──────────────────────────────────────────────────────

    /// <summary>Inject the world (and kingdoms, for the political/reach lenses) and
    /// rebuild. Safe to call before _Ready — _Ready picks it up.</summary>
    public void SetWorld(WorldData world, Dictionary<string, KingdomState> kingdoms)
    {
        _world = world;
        _kingdoms = kingdoms ?? new Dictionary<string, KingdomState>();
        if (IsInsideTree())
        {
            FrameWorld();
            Rebuild();
        }
    }

    public StrategicLens Lens => _lens;

    /// <summary>Switch lens. Color-only — heights don't change, so this is a recolor
    /// pass over the existing instances, not a rebuild.</summary>
    public void SetLens(StrategicLens lens)
    {
        _lens = lens;
        RecolorTiles();
    }

    public bool RevealAll => _revealAll;

    /// <summary>Toggle the debug full-map reveal (display-only, like StrategicView's
    /// _debugReveal — saved discovery state is never touched). Heights depend on
    /// discovery (Unseen renders as a flat slab), so this is a full rebuild.</summary>
    public void SetRevealAll(bool reveal)
    {
        _revealAll = reveal;
        if (_world != null && IsInsideTree())
            Rebuild();
    }

    /// <summary>Human-readable report for a picked tile — the panel's info line.</summary>
    public string DescribeTile(int col, int row)
    {
        if (_world == null || !_world.InBounds(col, row))
            return "";
        var t = _world.GetTile(col, row);
        var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;
        if (discovery == TileDiscovery.Unseen)
            return $"({col},{row})  Unseen — no expedition has come this far.";

        var parts = new List<string> { $"({col},{row})  {t.Terrain}" };
        if (t.IsOcean) parts.Add($"depth {t.OceanDepth}");
        if (!string.IsNullOrEmpty(t.KingdomId)) parts.Add(t.KingdomId);
        if (t.Corruption > 0) parts.Add($"corruption {t.Corruption}");
        if (discovery == TileDiscovery.Charted) parts.Add("charted");

        var settlement = _world.SettlementAt(col, row);
        if (settlement != null) parts.Add($"{settlement.Tier}: {settlement.Name}");
        var zone = _world.ShardZoneAt(col, row);
        if (zone != null) parts.Add($"shard zone: {zone.Name}");
        var poi = _world.PoiAt(col, row);
        if (poi != null && (poi.Discovered || _revealAll)) parts.Add($"POI: {poi.Kind}");
        if (t.IsStagingPoint) parts.Add("staging point");
        return string.Join("  ·  ", parts);
    }

    // ── Environment + camera ────────────────────────────────────────────────

    private void BuildEnvironment()
    {
        // Code-built environment rather than the combat .tres: the atlas frames a whole
        // continent, and combat's fog/tonemap distances are tuned for a 15-hex arena.
        // Pass 1 lighting: the first build ran ambient ~equal to the sun and the whole
        // map came out milky and flat. Relief is carried by the key-to-fill RATIO, so
        // ambient drops to a dim violet floor and the sun carries the image.
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = UITheme.WorldDeep,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                // Pass 1b: 0.35 energy read as underexposed once shadows + the color
                // grade stacked on top — three darkening knobs multiplied. Ambient is
                // the FILL: bright enough that shadowed ground stays readable, still
                // below the sun so relief keeps its ratio.
                AmbientLightColor = new Color(0.44f, 0.43f, 0.54f),
                AmbientLightEnergy = 0.55f,
            },
        });

        // Pass 2 sun: RAKING amber light instead of the campus's neutral 45° —
        // the "crafted model under a lamp" read comes from long shadows sliding
        // across the relief, not overhead illumination. ~27° elevation; energy
        // raised to compensate for grazing incidence on tile tops. Shadows ON
        // (pass 1): one directional map over a handful of MultiMesh draws is
        // cheap even in Compatibility, and at this angle mountain chains throw
        // long blades of shadow across the lowlands — the biggest depth cue
        // this view has. Max distance covers the zoomed-out camera.
        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.87f, 0.68f, 1f),   // late-afternoon amber
            LightEnergy = 1.8f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 350f,
            // Softens the hard shadow terminator and the bright-rim speckle on
            // terraced peaks; if speckle persists, raise ShadowNormalBias next.
            ShadowBlur = 1.0f,
        };
        AddChild(sun);
        sun.RotationDegrees = new Vector3(-27f, -35f, 0f);
    }

    private void BuildCamera()
    {
        _camera = new Camera3D { Name = "AtlasCamera", Far = 600f };
        AddChild(_camera);
        FrameWorld();
    }

    /// <summary>Frame the whole map: target its center, distance from its span.</summary>
    private void FrameWorld()
    {
        if (_world == null || _camera == null)
            return;
        float w = _world.Width * ColSpacing;
        float h = _world.Height * RowSpacing;
        _camTarget = new Vector3(w * 0.5f, 0f, h * 0.5f);
        _camDist = Mathf.Clamp(Mathf.Max(w, h) * 0.62f, CamDistMin, CamDistMax);
        PlaceCamera();
    }

    /// <summary>Pitch eases from steep (far — the map overview) to shallower (close —
    /// the Civ shot) as the camera comes down. Yaw is fixed: a map should not orbit.</summary>
    private void PlaceCamera()
    {
        float zoom01 = Mathf.InverseLerp(CamDistMin, CamDistMax, _camDist);
        // Pass 2: close zoom sweeps lower (40°) for the cinematic across-the-model shot.
        float pitch = Mathf.DegToRad(Mathf.Lerp(40f, 68f, zoom01));
        Vector3 offset = new Vector3(0f, Mathf.Sin(pitch), Mathf.Cos(pitch)) * _camDist;
        _camera.Position = _camTarget + offset;
        _camera.LookAt(_camTarget, Vector3.Up);
        ZoomChanged?.Invoke(zoom01);
    }

    // ── Input: pan / zoom / pick ────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent ev)
    {
        if (!AcceptInput || _camera == null)
            return;

        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 0.9f, CamDistMin, CamDistMax); PlaceCamera(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 1.1f, CamDistMin, CamDistMax); PlaceCamera(); }
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _dragging = true; _dragMoved = false; }
                else
                {
                    if (_dragging && !_dragMoved)
                        PickTile(mb.Position);
                    _dragging = false;
                }
            }
        }
        else if (ev is InputEventMouseMotion mm && _dragging)
        {
            if (mm.Relative.LengthSquared() > 1f)
                _dragMoved = true;
            float k = _camDist * 0.0012f;
            // Screen-up drags the map away from the camera; divide by sin(pitch) so a
            // vertical drag covers the same GROUND distance as a horizontal one.
            float pitchSin = Mathf.Max(0.3f, (_camera.Position - _camTarget).Normalized().Y);
            _camTarget += new Vector3(-mm.Relative.X * k, 0f, -mm.Relative.Y * k / pitchSin);
            ClampTarget();
            PlaceCamera();
        }
    }

    private void ClampTarget()
    {
        if (_world == null) return;
        _camTarget.X = Mathf.Clamp(_camTarget.X, 0f, _world.Width * ColSpacing);
        _camTarget.Z = Mathf.Clamp(_camTarget.Z, 0f, _world.Height * RowSpacing);
    }

    /// <summary>Raycast the click onto the y=0 ground plane, then resolve the nearest
    /// tile center among the 3×3 offset neighborhood — cheap and exact enough for a
    /// hex field (the true cell is always one of the candidates).</summary>
    private void PickTile(Vector2 screenPos)
    {
        if (_world == null) return;
        Vector3 origin = _camera.ProjectRayOrigin(screenPos);
        Vector3 dir = _camera.ProjectRayNormal(screenPos);
        if (Mathf.Abs(dir.Y) < 0.0001f) return;
        float t = -origin.Y / dir.Y;
        if (t < 0) return;
        Vector3 hit = origin + dir * t;

        int col0 = Mathf.RoundToInt(hit.X / ColSpacing);
        int row0 = Mathf.RoundToInt(hit.Z / RowSpacing);
        int bestCol = -1, bestRow = -1;
        float bestD = float.MaxValue;
        for (int dc = -1; dc <= 1; dc++)
        for (int dr = -1; dr <= 1; dr++)
        {
            int c = col0 + dc, r = row0 + dr;
            if (!_world.InBounds(c, r)) continue;
            Vector3 p = TileOrigin(c, r);
            float d = new Vector2(p.X - hit.X, p.Z - hit.Z).LengthSquared();
            if (d < bestD) { bestD = d; bestCol = c; bestRow = r; }
        }
        if (bestCol >= 0)
        {
            HandlePick(bestCol, bestRow);
            TilePicked?.Invoke(bestCol, bestRow);
        }
    }

    /// <summary>Preview routing: staging point → open a window preview there; inside
    /// an open window → hop the ghost; outside it → dismiss. Info reporting still
    /// happens via TilePicked regardless.</summary>
    private void HandlePick(int col, int row)
    {
        var t = _world.GetTile(col, row);
        var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;

        if (t.IsStagingPoint && discovery != TileDiscovery.Unseen)
        {
            ShowWindowPreview(col, row);
            return;
        }
        if (!PreviewActive)
            return;
        if (HexCoord.OffsetDistance(col, row, _previewCol, _previewRow) <= WindowRadius
            && t.IsLand)
        {
            MoveGhost(col, row);
        }
        else
        {
            ClearWindowPreview();
        }
    }

    // ── Build: tiles ────────────────────────────────────────────────────────

    /// <summary>Ground-plane position of a tile's center (y = 0).</summary>
    private Vector3 TileOrigin(int col, int row)
        => new Vector3(
            col * ColSpacing,
            0f,
            row * RowSpacing + (((col & 1) == 1) ? RowSpacing * 0.5f : 0f));

    private void Rebuild()
    {
        RebuildTiles();
        RebuildEdges();
        RebuildMarkers();
        RebuildDecorations();
        if (PreviewActive)
            ApplyWindowTint();   // fresh multimesh colors wiped the preview lift
    }

    private void RebuildTiles()
    {
        _landLayer?.QueueFree();
        _waterLayer?.QueueFree();

        int total = _world.Width * _world.Height;
        _instanceIndexOf = new int[total];
        _isWaterInstance = new bool[total];

        // Pass over the world once to split membership and count each layer.
        int waterCount = 0;
        for (int i = 0; i < total; i++)
        {
            _isWaterInstance[i] = _world.Tiles[i].IsWater;
            if (_isWaterInstance[i]) waterCount++;
        }

        var landMesh = new CylinderMesh
        {
            // Slight top taper gives every LAND prism a visible rim line under flat
            // shading — the "grout" that makes 9k identical prisms read as tiles.
            TopRadius = HexR * 0.96f,
            BottomRadius = HexR * 1.0f,
            Height = 1f,               // unit height; per-instance Y scale carries elevation
            RadialSegments = 6,
            Rings = 0,
            CapBottom = false,
        };
        landMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            // Pass 2: satin, not matte — under the raking sun a slight sheen lets
            // light SLIDE across the carving as the camera pans, which is most of
            // the "crafted object" material read.
            Roughness = 0.65f,
        };

        var waterMesh = new CylinderMesh
        {
            // NO taper on water: adjacent prisms meet exactly and the sea reads as one
            // surface, not a grid. Low roughness so the sun lays a glint across it.
            TopRadius = HexR * 1.0f,
            BottomRadius = HexR * 1.0f,
            Height = 1f,
            RadialSegments = 6,
            Rings = 0,
            CapBottom = false,
        };
        waterMesh.Material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            // Pass 2: dark lacquer — near-mirror so the amber sun lays a hard
            // glint line across the sea.
            Roughness = 0.15f,
        };

        var landMm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = landMesh,
            InstanceCount = total - waterCount,
        };
        var waterMm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = waterMesh,
            InstanceCount = waterCount,
        };

        int landIdx = 0, waterIdx = 0;
        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            int i = row * _world.Width + col;
            var tile = _world.Tiles[i];
            float h = TileHeight(tile);
            var basis = HexYaw * Basis.FromScale(new Vector3(1f, h, 1f));
            var origin = TileOrigin(col, row) + new Vector3(0f, h * 0.5f, 0f);
            var xf = new Transform3D(basis, origin);
            var c = TileColor(tile, col, row);
            if (_isWaterInstance[i])
            {
                _instanceIndexOf[i] = waterIdx;
                waterMm.SetInstanceTransform(waterIdx, xf);
                waterMm.SetInstanceColor(waterIdx, c);
                waterIdx++;
            }
            else
            {
                _instanceIndexOf[i] = landIdx;
                landMm.SetInstanceTransform(landIdx, xf);
                landMm.SetInstanceColor(landIdx, c);
                landIdx++;
            }
        }

        _landLayer = new MultiMeshInstance3D { Name = "LandLayer", Multimesh = landMm };
        _waterLayer = new MultiMeshInstance3D { Name = "WaterLayer", Multimesh = waterMm };
        AddChild(_landLayer);
        AddChild(_waterLayer);
    }

    /// <summary>Recolor every instance in place (lens switch). Transforms untouched.</summary>
    private void RecolorTiles()
    {
        if (_landLayer?.Multimesh == null || _waterLayer?.Multimesh == null || _world == null)
            return;
        for (int i = 0; i < _world.Tiles.Length; i++)
        {
            int col = i % _world.Width, row = i / _world.Width;
            var c = TileColor(_world.Tiles[i], col, row);
            if (_isWaterInstance[i])
                _waterLayer.Multimesh.SetInstanceColor(_instanceIndexOf[i], c);
            else
                _landLayer.Multimesh.SetInstanceColor(_instanceIndexOf[i], c);
        }
        if (PreviewActive)
            ApplyWindowTint();
    }

    /// <summary>THE 3D translation: discovery and terrain decide a tile's height.
    /// Unseen is a flat void slab so the continent's silhouette can't leak through
    /// fog; Charted keeps its height (the spec says shape is known) at a dim color;
    /// water sits low; land rises with the stored Elevation field plus a terrain
    /// bump so mountain chains read as chains.</summary>
    private float TileHeight(in WorldTile t)
    {
        var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;
        if (discovery == TileDiscovery.Unseen)
            return VoidSlabHeight;
        if (t.IsOcean)
            return OceanHeight;
        if (t.IsLake)
            return LakeHeight;

        // Pass 1: TERRACED and exaggerated. Raw elevation at ~1.3× was invisible at
        // overview zoom; quantizing into steps first turns noise into landforms —
        // plateaus, escarpments, stepped foothills — which is most of the Civ read.
        float terraced = Mathf.Round(Mathf.Clamp(t.Elevation, 0f, 1f) * TerraceSteps) / TerraceSteps;
        float h = 0.22f + terraced * 2.6f;
        switch (t.Terrain)
        {
            case TT.Mountain: h += 1.20f; break;
            case TT.Volcanic: h += 0.90f; break;
            case TT.Snow:     h += 0.60f; break;
            case TT.Hills:    h += 0.50f; break;
            case TT.Swamp:
            case TT.Marsh:    h = Mathf.Min(h, 0.30f); break;
            case TT.Coast:    h = Mathf.Min(h, 0.26f); break;
        }
        return h;
    }

    /// <summary>Elevation quantization steps for the terrace look. More steps =
    /// smoother slopes; fewer = bolder plateaus. 5 is the pass-1 pick.</summary>
    private const float TerraceSteps = 5f;

    // ── Build: river/road edges ─────────────────────────────────────────────

    /// <summary>Rivers and roads are EDGES in WorldData (6-bit masks, set on both
    /// sides). Each interior edge is drawn once: a tile draws its bits 0–2, whose
    /// mirrors are the neighbor's bits 3–5. Thin flat boxes laid along the shared
    /// edge at the taller tile's top.</summary>
    private void RebuildEdges()
    {
        _riverLayer?.QueueFree();
        _roadLayer?.QueueFree();

        var rivers = new List<Transform3D>();
        var roads = new List<Transform3D>();

        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            var tile = _world.GetTile(col, row);
            if ((tile.RiverEdges | tile.RoadEdges) == 0)
                continue;
            var discovery = _revealAll ? TileDiscovery.Explored : tile.Discovery;
            if (discovery == TileDiscovery.Unseen)
                continue;

            var (q, r) = HexCoord.OffsetToAxial(col, row);
            for (int i = 0; i < 6; i++)
            {
                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                bool neighborIn = _world.InBounds(nc, nr);
                // Ownership rule: draw bits 0–2; draw 3–5 only when the mirror side
                // doesn't exist to draw them.
                if (i >= 3 && neighborIn)
                    continue;

                Vector3 a = TileOrigin(col, row);
                Vector3 b = neighborIn ? TileOrigin(nc, nr)
                                       : a + DirectionVector(a, col, i);
                float hA = TileHeight(tile);
                float hB = neighborIn ? TileHeight(_world.GetTile(nc, nr)) : hA;
                Vector3 mid = (a + b) * 0.5f;
                mid.Y = Mathf.Max(hA, hB) + 0.03f;

                // The edge runs perpendicular to the center→neighbor line.
                Vector3 d = (b - a).Normalized();
                Vector3 perp = new Vector3(-d.Z, 0f, d.X);
                float yaw = Mathf.Atan2(-perp.Z, perp.X);
                var basis = new Basis(Vector3.Up, yaw);

                var xf = new Transform3D(basis, mid);
                if ((tile.RiverEdges & (1 << i)) != 0) rivers.Add(xf);
                if ((tile.RoadEdges & (1 << i)) != 0) roads.Add(xf);
            }
        }

        _riverLayer = MakeEdgeLayer("RiverLayer", rivers,
            new Vector3(HexR * 0.95f, 0.05f, 0.22f), UITheme.TerrainWaterShallow);
        _roadLayer = MakeEdgeLayer("RoadLayer", roads,
            new Vector3(HexR * 0.95f, 0.05f, 0.16f), UITheme.TerrainRoad.Lightened(0.15f));
    }

    /// <summary>Fallback direction for a border edge with no in-bounds neighbor:
    /// approximate using the axial direction in render space.</summary>
    private Vector3 DirectionVector(Vector3 from, int col, int i)
    {
        var (dq, dr) = HexCoord.AxialDirections[i];
        // Convert one axial step to render-space by round-tripping through offset
        // from an even reference column (exact per-step shape varies by parity;
        // border edges are rare enough that approximate is fine for a prototype).
        float x = dq * ColSpacing;
        float z = (dr + dq * 0.5f) * RowSpacing;
        return new Vector3(x, 0f, z);
    }

    private MultiMeshInstance3D MakeEdgeLayer(string name, List<Transform3D> xfs,
        Vector3 size, Color color)
    {
        var mesh = new BoxMesh { Size = size };
        mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.8f };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = xfs.Count,
        };
        for (int i = 0; i < xfs.Count; i++)
            mm.SetInstanceTransform(i, xfs[i]);
        var layer = new MultiMeshInstance3D { Name = name, Multimesh = mm };
        AddChild(layer);
        return layer;
    }

    // ── Build: markers + labels ─────────────────────────────────────────────

    private void RebuildMarkers()
    {
        foreach (var m in _markers)
            m.QueueFree();
        _markers.Clear();

        // POIs — discovered only (or reveal), same rule as StrategicView's POI layer.
        var poiMesh = new SphereMesh { Radius = 0.30f, Height = 0.60f, RadialSegments = 10, Rings = 6 };
        foreach (var poi in _world.Pois)
        {
            if (!poi.Discovered && !_revealAll) continue;
            var c = PoiColor(poi.Kind);
            AddMarker(new MeshInstance3D
            {
                Mesh = poiMesh,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = c,
                    EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 0.35f,
                },
                Position = MarkerPos(poi.X, poi.Y, 0.4f),
            });
        }

        // Settlements — one marker at the center, sized by tier, named label.
        foreach (var s in _world.Settlements)
        {
            if (!SettlementVisible(s)) continue;
            bool city = s.Tier == SettlementTier.City;
            Color c = s.IsSeat ? UITheme.Gold : UITheme.ArcaneBlue;
            float side = city ? 1.15f : 0.65f;
            AddMarker(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(side, side, side) },
                // Pass 2: settlements are METAL — worked pieces standing on the
                // carving, the way the reference model's cities are brass. Hue
                // still carries meaning (gold seat / arcane-blue settlement).
                MaterialOverride = new StandardMaterial3D
                { AlbedoColor = c, Metallic = 0.9f, Roughness = 0.35f },
                Position = MarkerPos(s.CenterX, s.CenterY, side * 0.5f + 0.05f),
            });
            AddMarker(MakeLabel(s.Name, c, MarkerPos(s.CenterX, s.CenterY, side + 1.1f)));
        }

        // Staging points — gold beacons (the launch options the strategic view deploys from).
        foreach (var sp in _world.StagingPoints)
        {
            AddMarker(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.32f, Height = 1.6f, RadialSegments = 8, Rings = 0 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = UITheme.Gold,
                    Metallic = 0.85f, Roughness = 0.3f,   // pass 2: brass beacons
                    EmissionEnabled = true, Emission = UITheme.Gold, EmissionEnergyMultiplier = 0.5f,
                },
                Position = MarkerPos(sp.X, sp.Y, 0.85f),
            });
        }

        // Shard zones — violet spikes at the gate once discovered.
        foreach (var z in _world.ShardZones)
        {
            if (!z.Discovered && !_revealAll) continue;
            AddMarker(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.35f, Height = 1.8f, RadialSegments = 6, Rings = 0 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = UITheme.Violet,
                    EmissionEnabled = true, Emission = UITheme.Violet, EmissionEnergyMultiplier = 0.6f,
                },
                Position = MarkerPos(z.GateX, z.GateY, 0.95f),
            });
        }

        // The Convergence — Kassian's seat, the cycle's terminal location.
        if (_world.ConvergenceX >= 0)
        {
            AddMarker(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.6f, Height = 3.5f, RadialSegments = 6, Rings = 0 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = UITheme.POIConvergence,
                    EmissionEnabled = true, Emission = UITheme.POIConvergence, EmissionEnergyMultiplier = 0.9f,
                },
                Position = MarkerPos(_world.ConvergenceX, _world.ConvergenceY, 1.8f),
            });
            AddMarker(MakeLabel("The Convergence", UITheme.POIConvergence,
                MarkerPos(_world.ConvergenceX, _world.ConvergenceY, 4.6f)));
        }
    }

    // ── Stage 1: decoration layers ──────────────────────────────────────────

    private static uint HashTile(int col, int row, uint salt)
    {
        uint h = (uint)(col * 73856093) ^ (uint)(row * 19349663) ^ (salt * 83492791u);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return h;
    }
    private static float Hash01(uint h) => (h & 0xFFFFu) / 65535f;

    /// <summary>Trees on forests, cap-stones on mountain chains, snow spires on ice —
    /// the detail that makes close zoom worth visiting. Instanced (two MultiMeshes for
    /// thousands of props), deterministic per tile, and EXPLORED-ONLY: charted land
    /// knows its shape, not what stands on it — props appearing is discovery's reward.</summary>
    private void RebuildDecorations()
    {
        foreach (var l in _decoLayers)
            l.QueueFree();
        _decoLayers.Clear();

        var trees = new List<(Transform3D xf, Color c)>();
        var peaks = new List<(Transform3D xf, Color c)>();

        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            var t = _world.GetTile(col, row);
            var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;
            if (discovery != TileDiscovery.Explored || !t.IsLand)
                continue;

            float h = TileHeight(t);
            Vector3 basePos = TileOrigin(col, row);

            if (t.Terrain == TT.Forest)
            {
                int n = 2 + (int)(HashTile(col, row, 1) % 2);
                for (int i = 0; i < n; i++)
                {
                    float ang = Hash01(HashTile(col, row, (uint)(7 + i))) * Mathf.Tau;
                    float rad = Hash01(HashTile(col, row, (uint)(31 + i))) * 0.55f;
                    float s = 0.55f + Hash01(HashTile(col, row, (uint)(53 + i))) * 0.55f;
                    var pos = basePos + new Vector3(
                        Mathf.Cos(ang) * rad,
                        h + 0.8f * s * 0.5f - 0.04f,
                        Mathf.Sin(ang) * rad);
                    trees.Add((
                        new Transform3D(Basis.FromScale(new Vector3(s, s, s)), pos),
                        Jitter(new Color(0.16f, 0.30f, 0.14f), col, row + i * 977, 0.10f)));
                }
            }
            else if (t.Terrain == TT.Mountain || t.Terrain == TT.Volcanic)
            {
                if (HashTile(col, row, 2) % 10 < 4)
                {
                    float s = 0.7f + Hash01(HashTile(col, row, 11)) * 0.6f;
                    var pos = basePos + new Vector3(0f, h + 0.9f * s * 0.5f - 0.04f, 0f);
                    var c = t.Terrain == TT.Volcanic
                        ? new Color(0.30f, 0.22f, 0.20f)
                        : new Color(0.55f, 0.52f, 0.48f);
                    peaks.Add((new Transform3D(Basis.FromScale(new Vector3(s, s, s)), pos),
                        Jitter(c, col, row, 0.08f)));
                }
            }
            else if (t.Terrain == TT.Snow)
            {
                if (HashTile(col, row, 3) % 10 < 4)
                {
                    float s = 0.6f + Hash01(HashTile(col, row, 13)) * 0.5f;
                    var pos = basePos + new Vector3(0f, h + 0.9f * s * 0.5f - 0.04f, 0f);
                    peaks.Add((new Transform3D(Basis.FromScale(new Vector3(s, s, s)), pos),
                        new Color(0.92f, 0.94f, 0.97f)));
                }
            }
        }

        _decoLayers.Add(MakeDecoLayer("TreeLayer", trees, 0.26f, 0.8f));
        _decoLayers.Add(MakeDecoLayer("PeakLayer", peaks, 0.34f, 0.9f));
    }

    private MultiMeshInstance3D MakeDecoLayer(string name,
        List<(Transform3D xf, Color c)> items, float baseRadius, float height)
    {
        var mesh = new CylinderMesh
        {
            TopRadius = 0f, BottomRadius = baseRadius, Height = height,
            RadialSegments = 5, Rings = 0,
        };
        mesh.Material = new StandardMaterial3D
        { VertexColorUseAsAlbedo = true, Roughness = 0.85f };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = items.Count,
        };
        for (int i = 0; i < items.Count; i++)
        {
            mm.SetInstanceTransform(i, items[i].xf);
            mm.SetInstanceColor(i, items[i].c);
        }
        var layer = new MultiMeshInstance3D { Name = name, Multimesh = mm };
        AddChild(layer);
        return layer;
    }

    // ── Expedition window preview ───────────────────────────────────────────

    private void ShowWindowPreview(int col, int row)
    {
        ClearWindowPreview();
        _previewCol = col;
        _previewRow = row;
        _previewTiles = _world.Disc(col, row, WindowRadius);
        ApplyWindowTint();

        // Boundary ring: flat violet discs on every tile at exactly the window edge.
        var edge = new List<Transform3D>();
        foreach (var (c2, r2) in _previewTiles)
        {
            if (HexCoord.OffsetDistance(c2, r2, col, row) != WindowRadius)
                continue;
            var p = TileOrigin(c2, r2);
            p.Y = TileHeight(_world.GetTile(c2, r2)) + 0.05f;
            edge.Add(new Transform3D(Basis.Identity, p));
        }
        var ringMesh = new CylinderMesh
        { TopRadius = 0.42f, BottomRadius = 0.42f, Height = 0.08f, RadialSegments = 6, Rings = 0 };
        ringMesh.Material = new StandardMaterial3D
        {
            AlbedoColor = UITheme.Violet,
            EmissionEnabled = true, Emission = UITheme.Violet, EmissionEnergyMultiplier = 0.6f,
        };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = ringMesh,
            InstanceCount = edge.Count,
        };
        for (int i = 0; i < edge.Count; i++)
            mm.SetInstanceTransform(i, edge[i]);
        _previewRing = new MultiMeshInstance3D { Name = "PreviewRing", Multimesh = mm };
        AddChild(_previewRing);

        // Ghost party token: a pawn the player can walk around the window.
        _ghost = new Node3D { Name = "GhostParty" };
        var body = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.30f, Height = 0.55f, RadialSegments = 8, Rings = 0 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.90f, 0.86f, 0.98f),
                EmissionEnabled = true, Emission = UITheme.Violet, EmissionEnergyMultiplier = 0.35f,
            },
            Position = new Vector3(0f, 0.28f, 0f),
        };
        var head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.16f, Height = 0.32f, RadialSegments = 10, Rings = 6 },
            MaterialOverride = new StandardMaterial3D
            { AlbedoColor = new Color(0.90f, 0.86f, 0.98f) },
            Position = new Vector3(0f, 0.66f, 0f),
        };
        var lamp = new OmniLight3D
        { LightColor = new Color(0.75f, 0.6f, 1f), LightEnergy = 0.8f, OmniRange = 6f,
          Position = new Vector3(0f, 1.2f, 0f) };
        _ghost.AddChild(body);
        _ghost.AddChild(head);
        _ghost.AddChild(lamp);
        _ghost.Position = GhostPos(col, row);
        AddChild(_ghost);
    }

    private Vector3 GhostPos(int col, int row)
    {
        var p = TileOrigin(col, row);
        p.Y = TileHeight(_world.GetTile(col, row));
        return p;
    }

    private void MoveGhost(int col, int row)
    {
        if (_ghost == null)
            return;
        var tween = CreateTween();
        tween.TweenProperty(_ghost, "position", GhostPos(col, row), 0.22)
             .SetTrans(Tween.TransitionType.Sine)
             .SetEase(Tween.EaseType.InOut);
    }

    /// <summary>Lift the window's tiles above the surrounding map so the deploy
    /// footprint reads as a lit table under the piece.</summary>
    private void ApplyWindowTint()
    {
        if (_previewTiles == null)
            return;
        foreach (var (c2, r2) in _previewTiles)
        {
            int i = r2 * _world.Width + c2;
            var lifted = TileColor(_world.Tiles[i], c2, r2).Lightened(0.18f);
            if (_isWaterInstance[i])
                _waterLayer.Multimesh.SetInstanceColor(_instanceIndexOf[i], lifted);
            else
                _landLayer.Multimesh.SetInstanceColor(_instanceIndexOf[i], lifted);
        }
    }

    private void ClearWindowPreview()
    {
        bool wasActive = PreviewActive;
        _previewRing?.QueueFree(); _previewRing = null;
        _ghost?.QueueFree(); _ghost = null;
        _previewCol = _previewRow = -1;
        _previewTiles = null;
        if (wasActive)
            RecolorTiles();   // drop the lift back to normal colors
    }

    private bool SettlementVisible(WorldSettlement s)
    {
        if (_revealAll) return true;
        foreach (var (x, y) in s.Tiles)
            if (_world.InBounds(x, y) && _world.GetTile(x, y).Discovery != TileDiscovery.Unseen)
                return true;
        return false;
    }

    private Vector3 MarkerPos(int col, int row, float lift)
    {
        var p = TileOrigin(col, row);
        p.Y = TileHeight(_world.GetTile(col, row)) + lift;
        return p;
    }

    private void AddMarker(Node3D node)
    {
        AddChild(node);
        _markers.Add(node);
    }

    private Label3D MakeLabel(string text, Color color, Vector3 pos)
        => new Label3D
        {
            Text = text,
            Position = pos,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 42,
            PixelSize = 0.02f,
            Modulate = color,
            OutlineSize = 10,
        };

    // ════════════════════════════════════════════════════════════════════════
    // Lens colors — MARKED COPY of StrategicView's private color logic.
    // See the header note: extract to a shared TileColors class if adopted.
    // ════════════════════════════════════════════════════════════════════════

    private Color TileColor(in WorldTile t, int col, int row)
    {
        var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;
        // Unseen stays a UNIFORM void — jittering fog would sparkle and read as data.
        if (discovery == TileDiscovery.Unseen)
            return UITheme.StrategicUnseen;
        if (discovery == TileDiscovery.Charted)
            return Jitter(LensBaseColor(t).Lerp(UITheme.StrategicCharted, 0.55f), col, row, 0.02f);

        Color c = LensColor(t);
        // Pass 1 grading, LOCAL to the 3D view (UITheme untouched — the 2D map keeps
        // its tuning): the palette was authored for unlit quads; under a lit scene it
        // washes out, so saturate up and darken slightly. Then per-tile value jitter
        // (hash of col,row — stable across recolors) breaks the flat fill-tool fields;
        // this one trick is most of why the HTML mockup read painterly.
        if (t.IsLand)
            c = Hex3DPalette.Grade(c);
        return Jitter(c, col, row, t.IsWater ? 0.02f : 0.04f);
    }

    /// <summary>Deterministic per-tile brightness wobble, ±amp around 1.0.</summary>
    private static Color Jitter(Color c, int col, int row, float amp)
    {
        uint h = (uint)(col * 73856093) ^ (uint)(row * 19349663);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        float k = 1f + (((h & 1023u) / 1023f) - 0.5f) * 2f * amp;
        return new Color(
            Mathf.Clamp(c.R * k, 0f, 1f),
            Mathf.Clamp(c.G * k, 0f, 1f),
            Mathf.Clamp(c.B * k, 0f, 1f),
            c.A);
    }

    private Color LensColor(in WorldTile t)
    {
        switch (_lens)
        {
            case StrategicLens.Terrain: return Hex3DPalette.TerrainColorOf(t);
            case StrategicLens.Corruption: return CorruptionLensColor(t);
            case StrategicLens.Reach: return ReachLensColor(t);
            default: return PoliticalLensColor(t);
        }
    }

    private Color LensBaseColor(in WorldTile t)
    {
        switch (_lens)
        {
            case StrategicLens.Terrain: return Hex3DPalette.TerrainColorOf(t);
            case StrategicLens.Corruption: return CorruptionLensColor(t);
            case StrategicLens.Reach: return ReachLensColor(t);
            default:
                bool ownedLand = t.IsLand && !string.IsNullOrEmpty(t.KingdomId);
                return ownedLand ? KingdomColor(t.KingdomId) : Hex3DPalette.TerrainColorOf(t);
        }
    }

    private Color PoliticalLensColor(in WorldTile t)
    {
        Color c;
        if (t.IsLand && !string.IsNullOrEmpty(t.KingdomId))
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
            c = Hex3DPalette.TerrainColorOf(t);
        }
        if (t.Corruption > 0)
        {
            float k = Mathf.Clamp(t.Corruption / 100f, 0f, 1f) * 0.35f;
            c = c.Lerp(UITheme.StrategicCorruptionWash, k);
        }
        return c;
    }

    private Color CorruptionLensColor(in WorldTile t)
    {
        if (t.IsWater)
            return UITheme.TerrainWater.Darkened(0.3f);
        float k = Mathf.Clamp(t.Corruption / 100f, 0f, 1f);
        Color clean = new Color(0.18f, 0.26f, 0.22f);
        Color mid = new Color(0.65f, 0.45f, 0.15f);
        Color hot = UITheme.StrategicCorruption;
        return k < 0.5f ? clean.Lerp(mid, k / 0.5f) : mid.Lerp(hot, (k - 0.5f) / 0.5f);
    }

    private Color ReachLensColor(in WorldTile t)
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
        return voidDim.Lerp(StanceColor(stance), influence / 100f);
    }

    private static Color StanceColor(KingdomStance s) => s switch
    {
        KingdomStance.Hostile    => new Color(0.75f, 0.24f, 0.22f),
        KingdomStance.Unfriendly => new Color(0.80f, 0.46f, 0.22f),
        KingdomStance.Neutral    => new Color(0.48f, 0.50f, 0.55f),
        KingdomStance.Friendly   => new Color(0.28f, 0.62f, 0.60f),
        KingdomStance.Allied     => new Color(0.30f, 0.72f, 0.40f),
        _                        => new Color(0.48f, 0.50f, 0.55f),
    };

    private static float TerrainLuminance(TT t) => t switch
    {
        TT.Grassland => 1.10f,
        TT.Road => 1.15f,
        TT.ArcaneGround => 1.05f,
        TT.Ruins => 0.95f,
        TT.Forest => 0.78f,
        TT.Swamp => 0.72f,
        TT.Mountain => 0.88f,
        TT.Volcanic => 0.85f,
        TT.Hills => 0.95f,
        TT.Coast => 1.12f,
        TT.Desert => 0.80f,
        TT.Tundra => 0.62f,
        TT.Snow => 0.95f,
        TT.Marsh => 0.40f,
        _ => 1.0f,
    };

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

    private static Color PoiColor(PoiKind kind) => kind switch
    {
        PoiKind.Combat => UITheme.POICombat,
        PoiKind.Rest => UITheme.POIRest,
        PoiKind.Narrative => UITheme.POINarrative,
        PoiKind.Negotiation => UITheme.POINegotiation,
        PoiKind.Outpost => UITheme.POIOutpost,
        PoiKind.Seat => UITheme.Gold,
        PoiKind.Settlement => UITheme.ArcaneBlue,
        PoiKind.Convergence => UITheme.POIConvergence,
        PoiKind.SupplyCache => UITheme.Success,
        _ => UITheme.TextPrimary,
    };
}
