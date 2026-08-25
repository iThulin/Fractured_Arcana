using Godot;
using System.Collections.Generic;

// ============================================================
// WorldAtlas3D.cs
//
// Purpose:        The 3D translation of the strategic view: a
//                 side-by-side PROTOTYPE for evaluating a Civ-style
//                 terrain read of the world against StrategicView's
//                 2D quad paint. Renders the SAME WorldData: one hex
//                 prism per tile via two MultiMeshes, land (tapered
//                 rim, matte) and water (seamless, glinting), with
//                 per-instance transform carrying terraced elevation
//                 as height and per-instance color carrying the
//                 active lens (graded + jittered), river/road edges as
//                 a second MultiMesh, discovered POIs / settlements /
//                 staging points / shard zones as marker meshes, and
//                 settlement names as Label3Ds. Read-only: it renders
//                 the world, it does not deploy expeditions. The
//                 strategic scene stays the functional map while the
//                 two renderers are compared.
// Layer:          UI (strategic view, 3D prototype)
// Collaborators:  WorldData.cs (the data it renders),
//                 UITheme.cs (all colors), HexCoord.cs (layout math),
//                 StrategicView.cs (the 2D renderer it mirrors),
//                 CampusAtlasPanel.cs (its host tab)
// See:            single_world_refactor_v2.docx §4.2 (the strategic
//                 renderer's cost contract: no per-tile nodes; a
//                 MultiMesh of prisms is one draw call, same as the
//                 quad version)
//
// COLOR PARITY NOTE: the lens color logic below is a deliberate,
// marked COPY of StrategicView's private color methods (TileColor,
// PoliticalLensColor, TerrainColor, KingdomColor, ...). Two renderers
// answering the same questions must not drift. If the 3D atlas is
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
/// Input is gated by <see cref="AcceptInput"/>, driven by the host container's hover state,
/// the same discipline CampusScreen uses for the campus map's camera.</summary>
public partial class WorldAtlas3D : Node3D
{
    // ── Layout (flat-top, odd-q; must match HexCoord/WorldData) ─────────────
    /// <summary>Hex circumradius in world units. Column spacing is 1.5R, row spacing
    /// is √3·R with odd columns pushed HALF A TILE toward +Z, the 3D analogue of
    /// HexCoord.OffsetRenderPosition's "odd columns nudged down".</summary>
    private const float HexR = 1.0f;
    private static readonly float ColSpacing = 1.5f * HexR;
    private static readonly float RowSpacing = Mathf.Sqrt(3f) * HexR;

    /// <summary>Yaw applied to every prism so the hex cross-section sits FLAT-TOP
    /// (a corner on +X). Godot's CylinderMesh puts a corner on +Z; +30° walks it
    /// onto +X, matching the 1.5R column spacing.</summary>
    private static readonly Basis HexYaw = new Basis(Vector3.Up, Mathf.Pi / 6f);

    // ── Heights ─────────────────────────────────────────────────────────────
    private const float VoidSlabHeight = 0.06f;   // Unseen: flat canvas slab, no silhouette leak (A6: renders as unpainted parchment)
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
    /// A plain .NET event, not a Godot signal, since the host panel is not a Node.</summary>
    public event System.Action<int, int> TilePicked;

    /// <summary>Fired when a building on the home-grounds MODEL is clicked while the camera
    /// is in city view (<see cref="_cityMode"/>). Carries the building id and
    /// its grid axial. The host (StrategicView) routes it to that building's campus panel.
    /// This is the "build in place on the world map" seam (Phase 2, true geometry merge).</summary>
    public event System.Action<string, Vector2I> HomeBuildingPicked;

    /// <summary>A bare (unbuilt, non-landmark) home-grounds hex was clicked in
    /// city view, a candidate building site. The host opens the construct card.</summary>
    public event System.Action<Vector2I> HomeGroundPicked;

    // ── Footprint preview (2026-08-13) ───────────────────────────────────

    private List<Vector2I> _footprintPreview;

    /// <summary>Tint a building's would-be footprint at an anchor on the home
    /// grounds: Gold = fits, Danger = doesn't (off-grid hexes simply don't
    /// tint; fewer gold tiles than the footprint size IS the signal). One
    /// preview at a time; call <see cref="ClearHomeFootprintPreview"/> after.</summary>
    public void PreviewHomeFootprint(string buildingId, Vector2I anchor)
    {
        ClearHomeFootprintPreview();
        if (_homeGrounds == null) return;
        var template = BuildingDatabase.GetTemplate(buildingId);
        if (template == null) return;

        var hexes = CampusGridManager.GetFootprintHexes(template, anchor, 0);
        bool fits = _homeGrounds.CanPlaceBuilding(template, anchor, 0);
        _homeGrounds.TintHexes(hexes, fits ? UITheme.Gold : UITheme.Danger);
        _footprintPreview = hexes;
    }

    public void ClearHomeFootprintPreview()
    {
        if (_footprintPreview == null || _homeGrounds == null) { _footprintPreview = null; return; }
        _homeGrounds.RestoreHexVisuals(_footprintPreview);
        _footprintPreview = null;
    }

    // ── Building focus (2026-08-13, Magos: "show off the art to come") ───

    /// <summary>How close the building-focus swoop lands. ORTHO MATH, not
    /// metres: the camera is orthographic and vertical framing = _camDist ×
    /// OrthoSizeFactor (1.5), so dist 0.67 → Size ≈ 1.0 world unit. One
    /// campus tile (0.67 across at the 1/3 grounds scale) genuinely fills
    /// the frame, with the 40° cinematic pitch this zoom implies. Deeper
    /// than the wheel floor (CamDistMinCity 1.5) on purpose: the showcase
    /// beat may go where the wheel doesn't. (History: 3.2 could pull BACK;
    /// 1.8 still framed ~4 tiles; both wrong for the same ortho reason.)</summary>
    private const float BuildingFocusDist = 1.0f;

    /// <summary>Swoop the camera down to frame one home-grounds building,
    /// the beat that plays under its floating panel. No-op outside city view.
    ///
    /// <para>Two composition rules learned the hard way (2026-08-13): the
    /// building must center in the VISIBLE area: the floating panel docks
    /// right, so the look-target shifts camera-right by ~a fifth of the
    /// horizontal framing, placing the mesh left-of-screen-centre = centre
    /// of what the player can see. And it must sit in the tilt-shift post's
    /// SHARP band (mid-frame). Off-centre framing dropped it into the
    /// miniature blur and the whole close-up read as soup.</para></summary>
    public void FocusHomeBuilding(Vector2I coord)
    {
        if (!_cityMode || _homeGrounds == null) return;
        var view = _homeGrounds.GetTileView(coord);
        if (view == null) return;

        float dist = Mathf.Min(BuildingFocusDist, _camDist);
        Vector3 right = new Vector3(Mathf.Cos(_camYaw), 0f, -Mathf.Sin(_camYaw));
        Vector3 target = view.GlobalPosition + right * (dist * OrthoSizeFactor * 0.2f);
        FlyTo(target, dist);
    }

    /// <summary>Pull back to the city framing after a focused panel closes.
    /// Only if still in city view; leaving for the world already reframes.</summary>
    public void UnfocusHomeBuilding()
    {
        if (!_cityMode) return;
        FlyTo(_cityCentre, _cityFitDist);
    }

    /// <summary>Place a building's anchor at a grounds hex (rotation 0, the
    /// city card's placement; rotated/multi-tile siting stays on the campus
    /// overlay's placement tool). On success rebuilds the grounds in place.</summary>
    public bool TryPlaceHomeBuilding(string buildingId, Vector2I coord)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || _homeGrounds == null) return false;
        if (!_homeGrounds.PlaceBuilding(buildingId, coord, 0, save.Buildings)) return false;
        SaveManager.Save();
        RefreshCityGrowth();
        return true;
    }

    /// <summary>As <see cref="HomeBuildingPicked"/>, but for a landmark hex on the grounds
    /// model.</summary>
    public event System.Action<string, Vector2I> HomeLandmarkPicked;

    /// <summary>Fired when city view is entered (true) or left (false), so the host can
    /// swap its chrome (hide the world HUD, show a "to the world map" button, etc.).</summary>
    public event System.Action<bool> CityModeChanged;

    public bool CityMode => _cityMode;

    /// <summary>True when the in-world campus grounds exist (a home tile + a campus layout
    /// have been resolved), the precondition for <see cref="EnterCityMode"/> to work.</summary>
    public bool HasCityGrounds => _homeGrounds != null;

    // ── Scene pieces ────────────────────────────────────────────────────────
    private Camera3D _camera;
    // Pass 1: land and water are SEPARATE MultiMeshes. Land keeps the top taper
    // (the grout line that makes prisms read as tiles); water drops it so the sea
    // fuses into one continuous surface instead of a hex spreadsheet, and takes a
    // low-roughness material so the sun glints on it.
    private MultiMeshInstance3D _landLayer;
    private MultiMeshInstance3D _waterLayer;
    /// <summary>Unseen tiles, the UNPAINTED CANVAS layer (art pass A6): matte
    /// parchment slabs with the same grout taper for land AND water, so no
    /// coastline silhouette can leak through the fog via mesh differences.</summary>
    private MultiMeshInstance3D _canvasLayer;
    /// <summary>flat tile index → instance index inside its layer's MultiMesh.</summary>
    private int[] _instanceIndexOf;
    /// <summary>flat tile index → which layer the instance lives in (Land/Water/Canvas).
    /// Membership is by terrain + discovery, computed in RebuildTiles; discovery only
    /// changes between SetWorld/SetRevealAll (both full rebuilds), so it is safe
    /// across recolors.</summary>
    private byte[] _tileLayer;
    private const byte LayerLand = 0, LayerWater = 1, LayerCanvas = 2;
    private MeshInstance3D _riverLayer;        // A9b: one winding ribbon mesh (RiverMesh)
    private MultiMeshInstance3D _roadLayer;
    private MultiMeshInstance3D _borderLayer;  // A7: kingdom-border ink strokes
    private readonly List<Node3D> _markers = new();   // POI/settlement/staging/etc, rebuilt together
    // Map labels that hold a constant on-screen size across zoom (their world PixelSize is retuned
    // from _camDist each camera move; see UpdateLabelScales). Their nodes live in _markers.
    private readonly List<(Label3D lbl, float fontSize, float screenFrac)> _scaledLabels = new();
    // ── The city (Phase 2, true geometry merge, /3 rep-tile) ────────────────
    // The campus grid is PERMANENTLY anchored in world space as the /3 subdivision of the
    // strategic tiles the city occupies: the grounds node is drawn at 1/3 scale, unrotated,
    // positioned on the home tile, so each city tile carries a whole 7-hex flower of
    // build-slots (ring tiles touch the tile edge exactly) with the vertex cells shared
    // three ways as bonus corners, districts ≡ strategic tiles, and "zooming into the
    // campus" is purely a camera move. No scale swap, no world culling, no separate scene.
    private CampusGridManager _homeGrounds;              // the HOME campus grounds: persistent, always present
    private CampusGridManager _cityGrounds;             // Phase 3: a visited NPC city's grounds, transient (built on enter, freed on leave)
    private WorldSettlement _activeCity;                // Phase 3: the NPC city currently entered; null = home campus
    private readonly HashSet<Vector2I> _revealedDistricts = new();   // Phase 3 explore: revealed districts in the active NPC city (projection of _cityExplore)
    private CityExploreState _cityExplore;              // Phase 3 explore: the active city's persisted district content (per-city, saved this cycle)
    private readonly List<Node3D> _cityContentMarkers = new(); // Phase 3 explore: floating content glyphs over revealed, uncleared districts (transient)
    private static readonly Color CityFogColor = new Color(0.13f, 0.14f, 0.19f);   // unexplored city district
    private readonly HashSet<Vector2I> _cityTiles = new(); // strategic (col,row) the ACTIVE city occupies

    /// <summary>A revealed district holding uncleared content was clicked in an NPC city view. The
    /// host (StrategicView) dispatches it (Service → services menu, Event → narrative panel,
    /// Fight/Story → stub), then marks it cleared and calls <see cref="RefreshCityContentMarkers"/>.</summary>
    public event System.Action<CityDistrictEntry, WorldSettlement> DistrictContentTriggered;

    /// <summary>True when the active city view is the home campus (vs. a visited NPC city). The
    /// host uses it to gate home-only affordances (annex) out of NPC cities.</summary>
    public bool ActiveCityIsHome => _activeCity == null;

    /// <summary>Display name of the visited NPC city (empty at home), for the services menu header.</summary>
    public string ActiveCityName => _activeCity != null ? SettlementDisplayName(_activeCity) : "";

    /// <summary>The visited NPC city's settlement record (null at home). K3: the
    /// services menu needs the settlement itself (KingdomId for Steward pricing
    /// and the R25 deed, IsSeat for hall quality), not just its display name.</summary>
    public WorldSettlement ActiveCity => _activeCity;

    // ── District growth (city view) ──────────────────────────────────────
    /// <summary>Dim tint for annexable preview flowers, so they read as not-yet-yours.</summary>
    private static readonly Color CityLockedPreview = new Color(0.34f, 0.36f, 0.44f, 1f);
    /// <summary>Slate grey that fills a CITY's whole footprint on the strategic map, so capitals
    /// read as regions at whole-world zoom on any lens (a lone marker was lost among the gold
    /// staging beacons). Towns keep their lens colour.</summary>
    private static readonly Color CityRegionTint = new Color(0.42f, 0.43f, 0.47f);
    /// <summary>Annex mode: the "about to buy a tile" state. Only while it's on are the annexable
    /// preview flowers shown, and only then does a city click resolve to a district to buy rather
    /// than to a building. Toggled by the host (StrategicView's annex button).</summary>
    private bool _annexMode;
    /// <summary>An annexable district was clicked in annex mode (its axial delta from the home
    /// district). The host confirms the spend and calls <see cref="RefreshCityGrowth"/>.</summary>
    public event System.Action<Vector2I> HomeDistrictPicked;
    private Vector3 _cityCentre;                          // world centre of the city footprint
    private float _cityFitDist = 8f;                      // camera distance framing the whole city
    private MultiMeshInstance3D _cityBorders;             // city PERIMETER outline, city view only
    private MultiMeshInstance3D _cityBordersInner;        // faint interior district seams, city view only
    private float _cityPlateau = 0f;                      // the city's uniform ground height (above all neighbours)
    /// <summary>Every marker standing ON a city tile (the seat block + label, the portal
    /// beacon (the gold "staging" marker on the city IS the portal building), POI orbs).
    /// Hidden while in city view: zoomed in, they'd loom over the campus they stand on.</summary>
    private readonly List<Node3D> _cityHiddenMarkers = new();
    /// <summary>True while in CITY VIEW: camera down at campus scale, grounds clickable,
    /// city-tile borders shown. Entered by selecting the city / Return-to-Campus.</summary>
    private bool _cityMode = false;
    /// <summary>Closest zoom allowed IN city view, near enough to sit among the buildings
    /// (child hexes are 1/3 world-tile scale), far below the world floor.</summary>
    private const float CamDistMinCity = 1.5f;
    private List<Vector2I> _warfronts = new();         // active warfront focus tiles (CYCLE state, injected)
    private readonly List<MultiMeshInstance3D> _decoLayers = new();  // stage 1: trees/peaks

    // ── Expedition window preview (stage 2 seed) ────────────────────────────
    // Clicking a staging point previews the deploy window: a boundary ring, the
    // window's tiles lifted in brightness, and a GHOST party token that hops to
    // any clicked tile inside, a feel test for playing expeditions on this
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

    // ── Orbit (2026-08-13, Magos: full camera controls) ─────────────────
    /// <summary>Camera yaw in radians (0 = the classic fixed-yaw framing).
    /// Middle-mouse horizontal drag turns it; persists across pans/zooms.</summary>
    private float _camYaw = 0f;
    /// <summary>Middle-mouse vertical drag sets an explicit pitch (degrees).
    /// -1 = automatic zoom-slaved pitch (the original behaviour).</summary>
    private float _pitchOverrideDeg = -1f;
    private bool _orbiting;
    private const float OrbitYawSens = 0.008f;    // rad per px
    private const float OrbitPitchSens = 0.25f;   // deg per px
    private const float PitchMinDeg = 15f, PitchMaxDeg = 82f;
    // Stage 1: min dropped 12→6. Close zoom is where exploration reads, and the
    // decoration layer + adaptive post blur make it worth going there.
    private const float CamDistMin = 6f, CamDistMax = 150f;

    /// <summary>ORTHOGRAPHIC is the strategic-map default: every tile the same size
    /// regardless of distance, so the whole world reads uniformly (the flat 2D map's
    /// legibility, kept in 3D). Perspective is the opt-in "cinematic model" diorama.</summary>
    private bool _orthographic = true;
    /// <summary>Ortho vertical extent per unit of _camDist. ≈ 2·tan(fov/2) for the
    /// default 75° FOV (2·0.767), so toggling projection barely shifts the framing.</summary>
    private const float OrthoSizeFactor = 1.5f;

    /// <summary>Fires with zoom01 (0 = closest) whenever the camera moves. The host
    /// panel drives the post shader off this so the miniature blur RELAXES as the
    /// camera comes down. A model stops reading as miniature at ground level.</summary>
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
    /// rebuild. Safe to call before _Ready; _Ready picks it up.</summary>
    public void SetWorld(WorldData world, Dictionary<string, KingdomState> kingdoms)
    {
        _world = world;
        _kingdoms = kingdoms ?? new Dictionary<string, KingdomState>();
        ComputeCityFootprint();   // before Rebuild, since tile flattening reads the city set
        if (IsInsideTree())
        {
            FrameWorld();
            Rebuild();
            BuildHomeGrounds();
        }
    }

    // ── City footprint (strategic tiles the city occupies) ──────────────────

    /// <summary>Offset (col,row) → axial, consistent with <see cref="TileOrigin"/>'s
    /// odd-columns-pushed-down layout.</summary>
    private static Vector2I OffsetToAxial(int col, int row)
        => new Vector2I(col, row - (col - (col & 1)) / 2);

    private static Vector2I AxialToOffset(int q, int r)
        => new Vector2I(q, r + (q - (q & 1)) / 2);

    /// <summary>Resolve which strategic tiles the city occupies (home + each UNLOCKED
    /// district at its strategic-axial offset) plus the derived camera framing. Districts
    /// are strategic tiles: CampusDistrict.Q/R are axial deltas from the home tile.</summary>
    private void ComputeCityFootprint()
    {
        _cityTiles.Clear();
        if (_world == null || !_world.InBounds(_world.HomeX, _world.HomeY)) return;

        var homeAx = OffsetToAxial(_world.HomeX, _world.HomeY);
        var districts = SaveManager.ActiveSave?.Ledger?.CampusMap?.Districts;
        if (districts != null)
            foreach (var d in districts)
            {
                if (d == null || !d.Unlocked) continue;
                var off = AxialToOffset(homeAx.X + d.Q, homeAx.Y + d.R);
                if (_world.InBounds(off.X, off.Y))
                    _cityTiles.Add(off);
            }
        if (_cityTiles.Count == 0)
            _cityTiles.Add(new Vector2I(_world.HomeX, _world.HomeY));

        // The city IS flattened to a plateau (2026-08-19): the home tile's natural terrain
        // height, applied to every city tile via the TileHeightAt override. RAW height here:
        // TileHeightAt consults _cityPlateau for city tiles, so computing it through the
        // override would read the previous city's value.
        _cityPlateau = TileHeight(_world.GetTile(_world.HomeX, _world.HomeY));

        // Centre + framing distance for the city-view camera. Target Y sits at the home-tile
        // height so the camera frames the city surface, not the y=0 ground plane far beneath it.
        Vector3 min = new(float.MaxValue, 0, float.MaxValue), max = new(float.MinValue, 0, float.MinValue);
        foreach (var ct in _cityTiles)
        {
            var p = TileOrigin(ct.X, ct.Y);
            min.X = Mathf.Min(min.X, p.X); min.Z = Mathf.Min(min.Z, p.Z);
            max.X = Mathf.Max(max.X, p.X); max.Z = Mathf.Max(max.Z, p.Z);
        }
        _cityCentre = (min + max) * 0.5f;
        _cityCentre.Y = _cityPlateau;
        float span = Mathf.Max(max.X - min.X, max.Z - min.Z) + 2f * HexR;
        _cityFitDist = Mathf.Clamp(span * 1.0f / OrthoSizeFactor, CamDistMinCity, CamDistMax);
    }

    /// <summary>Contour-follow height with ONE exception (2026-08-19, reversing the earlier
    /// contour-follow-everywhere choice): the ACTIVE city's strategic tiles are flattened to
    /// <see cref="_cityPlateau"/>, the centre tile's natural height. Under contour-follow
    /// the 3-way corner bonus hexes straddled terrace cliffs (never constructable-looking),
    /// building labels stacked down the hillside, and the border ring stepped. Every height
    /// consumer routes through here (tile prisms, grounds contour, borders, marker lifts),
    /// so the flatten is consistent by construction. Non-city tiles keep their own height.</summary>
    private float TileHeightAt(int col, int row)
        => _cityTiles.Contains(new Vector2I(col, row))
            ? _cityPlateau
            : TileHeight(_world.GetTile(col, row));

    /// <summary>Stage 3 (Phase 2, true geometry merge): render the guild's actual campus
    /// GROUNDS as a scaled model standing on the home tile, so the world map literally
    /// carries the city you zoom into (fits the "crafted clockwork model" art direction).
    /// Additive + visual for now: the overlay is still the entry. Rebuilt with the world;
    /// a safe no-op when there is no home tile or no campus layout yet. Scale/lift are
    /// first-pass and meant to be tuned in-engine.</summary>
    private void BuildHomeGrounds()
    {
        // True geometry merge (flower lattice): the campus grid is anchored IN WORLD SPACE
        // as the 1/3-scale unrotated subdivision of the city's strategic tiles. Node
        // transform does all the work: DistrictCentre(dq,dr) = (3dq,3dr) lands on each
        // strategic tile's centre, the district's 7-flower sits wholly inside its tile
        // (ring tiles touch the edge exactly), and vertex cells are the 3-way bonus
        // corners (verified numerically; see CampusMapSaveData.DistrictCentre). The
        // grounds sit on the city plateau and are ALWAYS present. At map zoom they're
        // simply small; city view is just the camera coming down. HexRadius 1.0 so child
        // spacing lands exactly on the sublattice.
        if (_cityMode) LeaveCityMode();
        if (_cityBorders != null) { _cityBorders.QueueFree(); _cityBorders = null; }
        if (_cityBordersInner != null) { _cityBordersInner.QueueFree(); _cityBordersInner = null; }
        if (_homeGrounds != null) { _homeGrounds.QueueFree(); _homeGrounds = null; }
        if (_world == null || !_world.InBounds(_world.HomeX, _world.HomeY)) return;

        var save = SaveManager.ActiveSave;
        var map = save?.Ledger?.CampusMap;
        if (map == null || map.Tiles.Count == 0) return;

        var grounds = new CampusGridManager
        {
            Name = "CityGrounds",
            HexTileScene3D = GD.Load<PackedScene>("res://Scenes/Combat/HexTile.tscn"),
            HexRadius = 1.0f,
            UseBlendedTerrainMesh = false,
            TileVisualShrink = 1f,     // full mesh (2026-08-19): the shrink's seams read as
                                       // chasms at the 3-way centre; the perimeter SKIRT now
                                       // covers the exterior overhang instead, so children
                                       // tessellate seamlessly again
        };
        AddChild(grounds);

        // Contour-follow: set the grid's transform AND the per-child height provider BEFORE
        // loading tiles, so both the real tiles (LoadFromSave) and the preview flowers are placed
        // on their district's strategic tile at its terrain height. The provider's world→local
        // height conversion reads the grid's global transform, so it must already be in place.
        float baseY = TileHeightAt(_world.HomeX, _world.HomeY);
        Vector3 home = TileOrigin(_world.HomeX, _world.HomeY);
        grounds.Scale = Vector3.One / 3f;
        grounds.Position = new Vector3(home.X, baseY + GroundsLift, home.Z);
        var homeCenter = new Vector2I(_world.HomeX, _world.HomeY);
        grounds.ChildTopWorldY = child => ChildDistrictTopWorldY(child, homeCenter);

        grounds.LoadFromSave(map, save.Ledger.Buildings);
        grounds.LoadLandmarks(save.HasFlag);
        _homeGrounds = grounds;
        // Picking stays analytic (CampusGridManager.TryPickRay): it goes through the node
        // transform, so the scale AND the rotation are handled exactly; Godot physics
        // mis-handles scaled colliders, which is why it isn't a ray→collider query.

        BuildCityBorders();
        // The annexable-district preview is built ON DEMAND when the player enters annex mode
        // (see SetAnnexMode), not here, so the "room to grow" flowers only appear when they're
        // actually about to buy a tile, and always reflect the current frontier.
    }

    /// <summary>Enter/leave "annex mode", the state in which the annexable-district preview is
    /// shown and a city click buys a district instead of opening a building. Rebuilds the preview
    /// to the CURRENT frontier each time it's turned on (so a freshly-annexed district's new
    /// neighbours appear). Only meaningful in city view.</summary>
    public void SetAnnexMode(bool on)
    {
        _annexMode = on && _cityMode && _homeGrounds != null;
        if (_annexMode)
        {
            _homeGrounds.BuildDistrictPreview(FrontierDistricts(), CityLockedPreview);
            _homeGrounds.SetSurroundingPreviewVisible(true);
        }
        else
        {
            _homeGrounds?.SetSurroundingPreviewVisible(false);
        }
    }

    /// <summary>The LOCKED districts adjacent to an unlocked one, the contiguous frontier the
    /// guild can annex next. Districts tessellate on a hex lattice in (dq,dr) space, so a
    /// district's neighbours are the six axial steps.</summary>
    private System.Collections.Generic.IEnumerable<Vector2I> FrontierDistricts()
    {
        var map = SaveManager.ActiveSave?.Ledger?.CampusMap;
        if (map?.Districts == null) yield break;
        var unlocked = new System.Collections.Generic.HashSet<(int, int)>();
        foreach (var d in map.Districts)
            if (d != null && d.Unlocked) unlocked.Add((d.Q, d.R));

        (int dq, int dr)[] dirs = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1) };
        var seen = new System.Collections.Generic.HashSet<(int, int)>();
        foreach (var u in unlocked)
            foreach (var (dq, dr) in dirs)
            {
                var n = (u.Item1 + dq, u.Item2 + dr);
                if (unlocked.Contains(n) || !seen.Add(n)) continue;
                yield return new Vector2I(n.Item1, n.Item2);
            }
    }

    /// <summary>Rebuild the city after a district was annexed: recompute the footprint, repaint
    /// the world tiles (the new city tile flattens/contours in), rebuild the grounds, and snap
    /// back into city view. Leaves annex mode off (BuildHomeGrounds → LeaveCityMode clears it).</summary>
    public void RefreshCityGrowth()
    {
        if (_world == null) return;
        bool wasCity = _cityMode;
        if (_cityMode) LeaveCityMode(flyOut: false);   // no swoop; BuildHomeGrounds would fly out, and we snap back
        ComputeCityFootprint();
        Rebuild();
        BuildHomeGrounds();                             // _cityMode already false, so it won't re-fly
        if (wasCity) EnterCityMode(fly: false);         // snap back into the (now larger) city
    }

    /// <summary>Small lift of the campus ground above its strategic tile's top, so the flower
    /// tiles rest on the surface rather than z-fighting the tile face.</summary>
    private const float GroundsLift = 0.02f;

    /// <summary>Contour-follow height provider for a city child tile: the WORLD top Y its tile
    /// should sit at: its DISTRICT's strategic tile terrain height, plus <see cref="GroundsLift"/>.
    /// A child's district is the nearest sublattice point (district centre child = (3dq,3dr), so
    /// round each axial component ÷3); shared corner cells round to one owner, whose height differs
    /// by at most a terrace step. <paramref name="center"/> is the city's centre strategic tile
    /// (home tile for the campus, settlement centre for an NPC city), so the same provider serves
    /// any city's grounds.</summary>
    private float ChildDistrictTopWorldY(Vector2I child, Vector2I center)
    {
        int dq = Mathf.RoundToInt(child.X / 3f);
        int dr = Mathf.RoundToInt(child.Y / 3f);
        var centerAx = OffsetToAxial(center.X, center.Y);
        var off = AxialToOffset(centerAx.X + dq, centerAx.Y + dr);
        float h = _world.InBounds(off.X, off.Y)
            ? TileHeightAt(off.X, off.Y)
            : TileHeightAt(center.X, center.Y);
        return h + GroundsLift;
    }

    /// <summary>City-view borders, reworked 2026-08-19 per playtest: the old version outlined
    /// EVERY city tile's six edges in full violet, which read as a harsh grid of purple rings.
    /// Now the city's outer PERIMETER (edges facing the world) gets the violet outline (one
    /// outline around the whole city), while interior edges shared by two city tiles become
    /// thin, low, darkened seams that still let each district be counted without shouting.
    /// Hidden on the world map (toggled by Enter/LeaveCityMode); heights sit on the plateau.</summary>
    private void BuildCityBorders()
    {
        if (_cityBorders != null) { _cityBorders.QueueFree(); _cityBorders = null; }   // safe to call repeatedly
        if (_cityBordersInner != null) { _cityBordersInner.QueueFree(); _cityBordersInner = null; }
        var outer = new List<Transform3D>();
        var inner = new List<Transform3D>();
        var seenInterior = new HashSet<Vector2I>();   // quantized edge midpoints, so shared edges draw once
        float apo = HexR * Mathf.Sqrt(3f) * 0.5f;   // flat-top apothem: edge midpoints at 60k+30°

        // City-tile centres in the ground plane, for resolving which side of an edge is city.
        var centres = new List<Vector3>();
        foreach (var ct in _cityTiles)
            centres.Add(TileOrigin(ct.X, ct.Y));

        foreach (var ct in _cityTiles)
        {
            Vector3 c = TileOrigin(ct.X, ct.Y);
            float top = TileHeightAt(ct.X, ct.Y);
            for (int k = 0; k < 6; k++)
            {
                float mid = Mathf.DegToRad(60f * k + 30f);
                var dir = new Vector3(Mathf.Cos(mid), 0f, Mathf.Sin(mid));
                var pos = new Vector3(c.X + dir.X * apo, top + 0.05f, c.Z + dir.Z * apo);
                // Box +X along the edge: the edge runs perpendicular to the radial direction.
                // Basis(Up, θ) maps +X to (cos θ, 0, -sin θ), so θ = -(mid + 90°) puts +X at
                // planar angle mid+90° in (x, z) = (cos, sin) convention.
                var basis = new Basis(Vector3.Up, -(mid + Mathf.Pi * 0.5f));

                // The tile across this edge sits one full tile-width past the midpoint.
                // If it's another city tile, the edge is interior.
                var nbr = c + dir * (2f * apo);
                bool interior = false;
                foreach (var oc in centres)
                    if (new Vector2(oc.X - nbr.X, oc.Z - nbr.Z).LengthSquared() < 0.05f * HexR * HexR)
                    { interior = true; break; }

                if (interior)
                {
                    var key = new Vector2I(Mathf.RoundToInt(pos.X * 64f), Mathf.RoundToInt(pos.Z * 64f));
                    if (seenInterior.Add(key))
                        inner.Add(new Transform3D(basis, pos));
                }
                else
                {
                    // SKIRT band (2026-08-19): the perimeter border DROPS from the plateau
                    // lip to just below the neighbouring tile's top. It frames the city
                    // like a model base and covers the child tiles' slight overhang at the
                    // exterior edge. Per-edge height is baked into the instance basis (the
                    // layer's box is unit-height), since each neighbour sits at its own
                    // terrain height.
                    int ncol = Mathf.RoundToInt(nbr.X / ColSpacing);
                    float zOff = ((ncol & 1) == 1) ? RowSpacing * 0.5f : 0f;
                    int nrow = Mathf.RoundToInt((nbr.Z - zOff) / RowSpacing);
                    float nbrTop = _world.InBounds(ncol, nrow) ? TileHeightAt(ncol, nrow) : top;
                    float bandTop = top + 0.02f;
                    float bandBot = Mathf.Min(nbrTop, top) - 0.08f;
                    float bandH = Mathf.Max(bandTop - bandBot, 0.08f);
                    var sPos = new Vector3(pos.X, (bandTop + bandBot) * 0.5f, pos.Z);
                    outer.Add(new Transform3D(basis.Scaled(new Vector3(1f, bandH, 1f)), sPos));
                }
            }
        }

        // Perimeter skirt: unit-height box (per-instance Y scale above), darker violet so
        // it reads as the city's base rather than a bright ring.
        _cityBorders = MakeEdgeLayer("CityBorders", outer,
            new Vector3(HexR * 1.02f, 1f, 0.06f), UITheme.Violet.Darkened(0.35f));
        _cityBorders.Visible = _cityMode;
        // Interior seams: same hue family (derived from UITheme.Violet, per the UI color
        // rule), darkened toward the ground and much thinner/lower than the perimeter.
        _cityBordersInner = MakeEdgeLayer("CityBordersInner", inner,
            new Vector3(HexR * 1.0f, 0.015f, 0.03f), UITheme.Violet.Darkened(0.55f));
        _cityBordersInner.Visible = _cityMode;
    }

    // ── Phase 3: visited NPC cities ──────────────────────────────────────────

    /// <summary>Descend into an NPC city: render its footprint as a /3 districted region (empty,
    /// no buildings) on a transient grounds grid, frame the camera on it, and enter city view.
    /// The home campus keeps its own persistent path, so a home settlement here just routes to
    /// <see cref="EnterCityMode"/>. Leaving (LeaveCityMode) tears the transient grounds down and
    /// restores the home footprint.</summary>
    public void EnterCityView(WorldSettlement city)
    {
        if (city == null || _world == null || _camera == null) return;
        if (city.IsGuildHome) { EnterCityMode(); return; }
        _activeCity = city;

        // Explore: fetch (or generate + persist) this city's district content. Districts start
        // fogged except the ones previously revealed (the centre seat is revealed on generation).
        var cycle = SaveManager.ActiveSave?.Cycle;
        _cityExplore = CityExploreService.GetOrGenerate(cycle, city, DistrictDeltas(city));
        _revealedDistricts.Clear();
        _revealedDistricts.Add(Vector2I.Zero);
        if (_cityExplore != null)
            foreach (var e in _cityExplore.Districts)
                if (e.Revealed) _revealedDistricts.Add(new Vector2I(e.Dq, e.Dr));

        ComputeNpcFootprint(city);
        Rebuild();   // the city set changed → the plateau flatten moves to THIS city's tiles
        BuildNpcGrounds(city);
        BuildCityBorders();
        EnterCityMode();
        RebuildCityContentMarkers();
    }

    /// <summary>The axial deltas (from the city centre) of a settlement's tiles, the district set
    /// <see cref="GenerateCityLayout"/> renders and <see cref="CityExploreService"/> assigns content
    /// to. Kept in sync with GenerateCityLayout's per-tile math.</summary>
    private List<Vector2I> DistrictDeltas(WorldSettlement city)
    {
        var list = new List<Vector2I>();
        if (city == null) return list;
        var centerAx = OffsetToAxial(city.CenterX, city.CenterY);
        foreach (var (x, y) in city.Tiles)
        {
            var ax = OffsetToAxial(x, y);
            list.Add(new Vector2I(ax.X - centerAx.X, ax.Y - centerAx.Y));
        }
        return list;
    }

    /// <summary>The district a fine-hex child belongs to (district centre child = (3dq,3dr), so
    /// round each axial component ÷3). Used by the explore fog to group flower tiles by district.</summary>
    private static Vector2I DistrictOf(Vector2I child)
        => new Vector2I(Mathf.RoundToInt(child.X / 3f), Mathf.RoundToInt(child.Y / 3f));

    /// <summary>Footprint + camera framing for a visited NPC city: its settlement tiles, centred
    /// on its centre tile. Mirrors ComputeCityFootprint's framing but reads the settlement rather
    /// than the campus districts.</summary>
    private void ComputeNpcFootprint(WorldSettlement city)
    {
        _cityTiles.Clear();
        foreach (var (x, y) in city.Tiles)
            if (_world.InBounds(x, y)) _cityTiles.Add(new Vector2I(x, y));
        if (_cityTiles.Count == 0) _cityTiles.Add(new Vector2I(city.CenterX, city.CenterY));

        // RAW height, same reason as ComputeCityFootprint: the TileHeightAt override
        // consults _cityPlateau for tiles already in the (just-refilled) city set.
        _cityPlateau = TileHeight(_world.GetTile(city.CenterX, city.CenterY));
        Vector3 min = new(float.MaxValue, 0, float.MaxValue), max = new(float.MinValue, 0, float.MinValue);
        foreach (var ct in _cityTiles)
        {
            var p = TileOrigin(ct.X, ct.Y);
            min.X = Mathf.Min(min.X, p.X); min.Z = Mathf.Min(min.Z, p.Z);
            max.X = Mathf.Max(max.X, p.X); max.Z = Mathf.Max(max.Z, p.Z);
        }
        _cityCentre = (min + max) * 0.5f;
        _cityCentre.Y = _cityPlateau;
        float span = Mathf.Max(max.X - min.X, max.Z - min.Z) + 2f * HexR;
        _cityFitDist = Mathf.Clamp(span * 1.0f / OrthoSizeFactor, CamDistMinCity, CamDistMax);
    }

    /// <summary>A transient district layout for an NPC city: each settlement tile becomes an
    /// unlocked district (axial delta from the centre), so CampusGridManager renders its /3 flowers
    /// exactly as the campus does, just with no buildings. Reuses the Locale renderer rather than
    /// forking one (per the Phase 3 spec).</summary>
    private CampusMapSaveData GenerateCityLayout(WorldSettlement city)
    {
        var map = new CampusMapSaveData { LatticeVersion = 3 };
        var centerAx = OffsetToAxial(city.CenterX, city.CenterY);
        foreach (var (x, y) in city.Tiles)
        {
            var ax = OffsetToAxial(x, y);
            map.Districts.Add(new CampusDistrict { Q = ax.X - centerAx.X, R = ax.Y - centerAx.Y, Unlocked = true });
        }
        map.RebuildTilesFromDistricts();
        return map;
    }

    /// <summary>Build the transient grounds for a visited NPC city (freed on leave). Same renderer
    /// + contour as the home campus, positioned on the settlement centre, with an empty
    /// (no-building) district layout.</summary>
    private void BuildNpcGrounds(WorldSettlement city)
    {
        if (_cityGrounds != null) { _cityGrounds.QueueFree(); _cityGrounds = null; }
        var map = GenerateCityLayout(city);
        if (map.Tiles.Count == 0) return;

        var grounds = new CampusGridManager
        {
            Name = "NpcCityGrounds",
            HexTileScene3D = GD.Load<PackedScene>("res://Scenes/Combat/HexTile.tscn"),
            HexRadius = 1.0f,
            UseBlendedTerrainMesh = false,
            TileVisualShrink = 1f,     // full mesh, matching the home grounds (skirt covers the edge)
        };
        AddChild(grounds);
        var center = new Vector2I(city.CenterX, city.CenterY);
        float baseY = TileHeightAt(center.X, center.Y);
        Vector3 c = TileOrigin(center.X, center.Y);
        grounds.Scale = Vector3.One / 3f;
        grounds.Position = new Vector3(c.X, baseY + GroundsLift, c.Z);
        grounds.ChildTopWorldY = child => ChildDistrictTopWorldY(child, center);
        grounds.LoadFromSave(map, new System.Collections.Generic.List<BuildingSaveData>());
        _cityGrounds = grounds;
        // Explore: fog everything but the revealed (centre) district; click districts to reveal.
        grounds.ApplyDistrictFog(child => _revealedDistricts.Contains(DistrictOf(child)), CityFogColor);
    }

    /// <summary>Rebuild the floating content glyphs over the active city: one marker per revealed,
    /// uncleared, non-Empty district, positioned above that district's centre child tile. Fogged,
    /// cleared, and Empty districts show nothing. Called on enter, on reveal, and after a district
    /// is cleared (via <see cref="RefreshCityContentMarkers"/>).</summary>
    private void RebuildCityContentMarkers()
    {
        foreach (var m in _cityContentMarkers)
            if (m != null && IsInstanceValid(m)) m.QueueFree();
        _cityContentMarkers.Clear();
        if (_cityGrounds == null || _cityExplore == null) return;

        foreach (var e in _cityExplore.Districts)
        {
            var district = new Vector2I(e.Dq, e.Dr);
            if (!_revealedDistricts.Contains(district)) continue;   // still fogged
            if (e.Cleared) continue;
            var type = (DistrictContentType)e.Content;
            if (type == DistrictContentType.Empty) continue;

            // District centre child = (3dq, 3dr); its tile view gives the world-space top.
            var view = _cityGrounds.GetTileView(new Vector2I(e.Dq * 3, e.Dr * 3));
            if (view == null) continue;

            var mk = MakeContentMarker(type, view.GlobalPosition);
            if (mk != null) { AddChild(mk); _cityContentMarkers.Add(mk); }
        }
    }

    /// <summary>Public hook for the host to refresh markers after clearing a district's content.</summary>
    public void RefreshCityContentMarkers() => RebuildCityContentMarkers();

    /// <summary>A billboarded glyph marking a district's content type, floating just above the
    /// district centre. NoDepthTest so it reads over the tiles. (Fixed pixel size for now, a first
    /// pass; may want the zoom-tracking scale the place-name labels use.)</summary>
    private Node3D MakeContentMarker(DistrictContentType type, Vector3 worldTop)
    {
        string glyph; Color color;
        switch (type)
        {
            case DistrictContentType.Service: glyph = "⚒"; color = new Color(1f, 0.82f, 0.25f); break;
            case DistrictContentType.Event:   glyph = "?"; color = new Color(0.42f, 0.72f, 1f); break;
            case DistrictContentType.Story:   glyph = "✦"; color = new Color(0.80f, 0.60f, 1f); break;
            case DistrictContentType.Fight:   glyph = "⚔"; color = new Color(1f, 0.40f, 0.32f); break;
            default: return null;
        }
        return new Label3D
        {
            Text = glyph,
            Modulate = color,
            OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
            OutlineSize = 12,
            FontSize = 96,
            PixelSize = 0.004f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            Position = worldTop + new Vector3(0f, 0.55f, 0f),
        };
    }

    // ── City view (camera-only) ─────────────────────────────────────────────

    /// <summary>Enter CITY VIEW: fly the camera down to campus scale over the city. The
    /// geometry never changes (the campus is already there at true scale), so this is a
    /// camera move plus state: borders on, seat marker off, closer zoom floor, grounds
    /// clicks take priority. The surrounding world tiles stay visible around the edges.</summary>
    public void EnterCityMode() => EnterCityMode(fly: true);

    private void EnterCityMode(bool fly)
    {
        if (_cityMode || _homeGrounds == null || _world == null || _camera == null) return;
        _cityMode = true;

        if (_cityBorders != null) _cityBorders.Visible = true;
        if (_cityBordersInner != null) _cityBordersInner.Visible = true;
        // A9/A7: river/road/border strokes read as litter at city zoom, so hide them.
        if (_riverLayer != null) _riverLayer.Visible = false;
        if (_roadLayer != null) _roadLayer.Visible = false;
        if (_borderLayer != null) _borderLayer.Visible = false;
        foreach (var m in _cityHiddenMarkers)
            if (m != null && IsInstanceValid(m)) m.Visible = false;
        _camera.Near = 0.02f;   // let the camera get in close without slicing tiles
        _camera.Far = 150f;     // tighter near/far span = better depth precision up close
        // Concentrate the shadow map on the city: 350 units of range at campus zoom is
        // maybe a texel per child hex, pure grain. ~60 covers the city + visible ring.
        if (_sun != null) _sun.DirectionalShadowMaxDistance = 60f;
        if (fly)
        {
            FlyTo(_cityCentre, _cityFitDist);
        }
        else
        {
            // Wheel-triggered: no tween (FlyTo would lerp _camDist too and stomp further
            // wheel input for its duration). Snap the look-target up onto the plateau so
            // the city SURFACE is framed, not the ground far beneath it.
            _camTarget.Y = _cityPlateau;
            PlaceCamera();
        }

        CityModeChanged?.Invoke(true);
    }

    /// <summary>Leave CITY VIEW: borders off, seat marker back, world zoom floor restored,
    /// camera swoops back out to the overview.</summary>
    public void LeaveCityMode() => LeaveCityMode(flyOut: true);

    private void LeaveCityMode(bool flyOut)
    {
        if (!_cityMode) return;
        _cityMode = false;
        _annexMode = false;

        _homeGrounds?.SetSurroundingPreviewVisible(false);
        if (_cityBorders != null) _cityBorders.Visible = false;
        if (_cityBordersInner != null) _cityBordersInner.Visible = false;
        if (_riverLayer != null) _riverLayer.Visible = true;
        if (_roadLayer != null) _roadLayer.Visible = true;
        if (_borderLayer != null) _borderLayer.Visible = true;
        foreach (var m in _cityHiddenMarkers)
            if (m != null && IsInstanceValid(m)) m.Visible = true;
        if (_camera != null) { _camera.Near = 0.05f; _camera.Far = 600f; }
        if (_sun != null) _sun.DirectionalShadowMaxDistance = 350f;

        // Phase 3: a visited NPC city's grounds are transient. Tear them down and restore the
        // HOME footprint + borders, so the map and a later home entry are back on the campus.
        if (_activeCity != null)
        {
            // Explore: markers are transient (rebuilt on next entry from the persisted state).
            foreach (var m in _cityContentMarkers)
                if (m != null && IsInstanceValid(m)) m.QueueFree();
            _cityContentMarkers.Clear();
            _cityExplore = null;
            if (_cityGrounds != null) { _cityGrounds.QueueFree(); _cityGrounds = null; }
            _activeCity = null;
            ComputeCityFootprint();
            Rebuild();   // restore the home footprint's flatten (the NPC city's tiles re-contour)
            BuildCityBorders();
        }

        if (flyOut)
        {
            FlyToOverview();
        }
        else
        {
            // Wheel-triggered: snap the look-target back to the ground plane (no tween;
            // see the matching note in EnterCityMode).
            _camTarget.Y = 0f;
            PlaceCamera();
        }

        CityModeChanged?.Invoke(false);
    }

    /// <summary>Continuous zoom across the city threshold: wheeling IN over the city slips
    /// into city view without a camera hijack (state flip only; the zoom keeps going,
    /// now down to the campus floor); wheeling OUT past the exit distance slips back to the
    /// world map where you are. Selecting the city / Return-to-Campus still fly.</summary>
    private void MaybeAutoCityTransition()
    {
        if (_cityMode)
        {
            if (_camDist > CityAutoExitDist)
                LeaveCityMode(flyOut: false);
        }
        else if (_homeGrounds != null && _camDist < CityAutoEnterDist)
        {
            float d = new Vector2(_camTarget.X - _cityCentre.X, _camTarget.Z - _cityCentre.Z).Length();
            if (d <= 2.5f * ColSpacing)
                EnterCityMode(fly: false);
        }
    }

    /// <summary>Wheeling in below this over the city slips into city view. Sits just above
    /// the world zoom floor (6) so the handoff is reachable by wheel.</summary>
    private const float CityAutoEnterDist = 7f;
    /// <summary>Wheeling out past this leaves city view (without it, the world map would sit
    /// unresponsive behind a mode that eats every click).</summary>
    private const float CityAutoExitDist = 24f;

    /// <summary>On a confirmed click over the home city, pick the grounds model and surface
    /// the hit building/landmark to the host. Returns true when it consumed the click (hit a
    /// grounds hex), so the caller skips the ordinary world-tile pick.</summary>
    private bool TryPickHomeGrounds(Vector2 screenPos)
    {
        if (_homeGrounds == null || _camera == null) return false;
        Vector3 origin = _camera.ProjectRayOrigin(screenPos);
        Vector3 dir = _camera.ProjectRayNormal(screenPos);

        // Phase 3 explore: in a visited NPC city, a click reveals the district under it (lifts the
        // explore fog). No buildings/annex there; consume the click so it doesn't fall through to
        // the world pick or the off-screen home campus.
        if (_activeCity != null)
        {
            if (_cityGrounds != null && _cityGrounds.TryPickRay(origin, dir, out Vector2I hit))
            {
                var district = DistrictOf(hit);
                var entry = CityExploreService.FindDistrict(_cityExplore, district);
                if (!_revealedDistricts.Contains(district))
                {
                    // First click on a fogged district: scout it (lift the fog, reveal the marker).
                    _revealedDistricts.Add(district);
                    if (entry != null) entry.Revealed = true;
                    _cityGrounds.ApplyDistrictFog(child => _revealedDistricts.Contains(DistrictOf(child)), CityFogColor);
                    RebuildCityContentMarkers();
                    SaveManager.Save();   // persist the scouted district
                }
                else if (entry != null && !entry.Cleared
                         && (DistrictContentType)entry.Content != DistrictContentType.Empty)
                {
                    // Second click on a revealed district with live content: trigger it (host dispatches).
                    DistrictContentTriggered?.Invoke(entry, _activeCity);
                }
            }
            return true;
        }

        // Annex mode: clicks buy districts, not buildings. Resolve the annexable preview flower
        // under the cursor and hand it to the host; a miss consumes the click (no building pick
        // underneath) so it doesn't fall through.
        if (_annexMode)
        {
            if (_homeGrounds.TryPickPreviewDistrict(origin, dir, out Vector2I district))
                HomeDistrictPicked?.Invoke(district);
            return true;
        }

        if (!_homeGrounds.TryPickRay(origin, dir, out Vector2I coord))
            return false;

        string buildingId = _homeGrounds.GetBuildingIdAt(coord);
        if (!string.IsNullOrEmpty(buildingId)) { HomeBuildingPicked?.Invoke(buildingId, coord); return true; }
        string landmarkId = _homeGrounds.GetLandmarkIdAt(coord);
        if (!string.IsNullOrEmpty(landmarkId)) { HomeLandmarkPicked?.Invoke(landmarkId, coord); return true; }
        // Construction (2026-08-13): a bare grounds hex is a building site.
        // The host opens the construct card for this coord ("build in place",
        // the Phase-2 promise finally kept for NEW buildings).
        HomeGroundPicked?.Invoke(coord);
        return true;   // consumed either way; never falls through to a world pick
    }

    /// <summary>Inject the active warfront focus tiles (cycle state, not in WorldData)
    /// so the map draws red conflict beacons. The host routes picks on these tiles back
    /// to its intervention dialog. Rebuilds only the marker layer.</summary>
    public void SetWarfronts(List<Vector2I> tiles)
    {
        _warfronts = tiles ?? new List<Vector2I>();
        if (_world != null && IsInsideTree())
            RebuildMarkers();
    }

    public StrategicLens Lens => _lens;

    /// <summary>Switch lens. Color-only: heights don't change, so this is a recolor
    /// pass over the existing instances, not a rebuild.</summary>
    public void SetLens(StrategicLens lens)
    {
        _lens = lens;
        RecolorTiles();
    }

    public bool RevealAll => _revealAll;

    /// <summary>Toggle the debug full-map reveal (display-only, like StrategicView's
    /// _debugReveal; saved discovery state is never touched). Heights depend on
    /// discovery (Unseen renders as a flat slab), so this is a full rebuild.</summary>
    public void SetRevealAll(bool reveal)
    {
        _revealAll = reveal;
        if (_world != null && IsInsideTree())
            Rebuild();
    }

    /// <summary>Human-readable report for a picked tile, the panel's info line.</summary>
    public string DescribeTile(int col, int row)
    {
        if (_world == null || !_world.InBounds(col, row))
            return "";
        var t = _world.GetTile(col, row);
        var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;
        if (discovery == TileDiscovery.Unseen)
            return $"({col},{row})  Unpainted. No expedition has come this far.";

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
                // A4b (exposure fix after user screenshots): the first daylight rig
                // recreated the Pass-1 failure: fill ~equal to the sun's top-face
                // contribution = milky and flat. The steeper −45° sun catches tile
                // TOPS at sin45 ≈ 0.71 (the lamp only grazed them at 0.45), so both
                // knobs must drop to hold the OLD exposure with the NEW hue:
                // target ≈ 0.97 total on flat tops at ~2.3:1 key-to-fill
                // (sun 0.96·1.0·0.71 ≈ 0.68 + fill 0.575·0.5 ≈ 0.29).
                AmbientLightColor = new Color(0.55f, 0.56f, 0.60f),
                AmbientLightEnergy = 0.5f,
            },
        });

        // A4 sun (painterly overrides the lamp, 2026-08-12): PAINTERLY DAYLIGHT
        // replaces the raking late-afternoon amber. The lamp's ~27° grazing angle
        // and heavy warm cast were the "crafted model" read; a ~45° near-neutral
        // warm sun lets the A1 terrain hues render TRUE while mountain chains
        // still throw readable shadow (higher would flatten the relief entirely;
        // this is mid-morning, not noon). Energy drops with the steeper incidence.
        // Shadows stay ON: cast shadow remains the atlas's biggest depth cue.
        _sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.97f, 0.90f, 1f),   // near-neutral warm daylight
            LightEnergy = 1.0f,   // A4b: 1.3 overexposed the steeper sun (see ambient note)
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 350f,
            // Softens the hard shadow terminator and the bright-rim speckle on
            // terraced peaks; if speckle persists, raise ShadowNormalBias next.
            ShadowBlur = 1.0f,
        };
        AddChild(_sun);
        _sun.RotationDegrees = new Vector3(-45f, -35f, 0f);
    }

    /// <summary>The atlas sun, kept so city view can tighten its shadow range. One
    /// directional map stretched over 350 units is fine at map zoom but starves the shadow
    /// of texels up close, which reads as pixel grain on shadows AND lit faces (the blur is
    /// randomized sampling, so under-resolution becomes noise).</summary>
    private DirectionalLight3D _sun;

    private void BuildCamera()
    {
        _camera = new Camera3D { Name = "AtlasCamera", Far = 600f };
        AddChild(_camera);
        FrameWorld();
    }

    /// <summary>Frame the whole map: target its center, distance from its span.</summary>
    private Vector3 WorldCenter()
        => new Vector3(_world.Width * ColSpacing * 0.5f, 0f, _world.Height * RowSpacing * 0.5f);

    /// <summary>The zoom distance that makes the whole world FILL the viewport (minimal
    /// margin), accounting for the viewport aspect: a wide world on a wide screen is
    /// width-bound, so the vertical ortho size is driven by width/aspect. Falls back to
    /// 16:9 if the viewport isn't sized yet.</summary>
    private float OverviewDist()
    {
        float w = _world.Width * ColSpacing;
        float h = _world.Height * RowSpacing;
        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        float aspect = vp.Y > 1f ? vp.X / vp.Y : 1.7778f;
        // Vertical ortho size needed to show the whole world; small pad off the border.
        float fitSize = Mathf.Max(h, w / Mathf.Max(0.1f, aspect)) * 1.04f;
        return Mathf.Clamp(fitSize / OrthoSizeFactor, CamDistMin, CamDistMax);
    }

    private void FrameWorld()
    {
        if (_world == null || _camera == null)
            return;
        _camTarget = WorldCenter();
        _camDist = OverviewDist();
        PlaceCamera();
    }

    /// <summary>Closest zoom currently allowed, nearer in city view than on the world map.</summary>
    private float MinDist() => _cityMode ? CamDistMinCity : CamDistMin;

    /// <summary>Pitch eases from steep (far, the map overview) to shallower (close,
    /// the Civ shot) as the camera comes down. Yaw is fixed: a map should not orbit.</summary>
    private void PlaceCamera()
    {
        float zoom01 = Mathf.InverseLerp(CamDistMin, CamDistMax, _camDist);
        // Pass 2: close zoom sweeps lower (40°) for the cinematic across-the-model shot.
        // (2026-08-13) Middle-mouse orbit can override the auto pitch; yaw
        // rotates the whole rig around the look target.
        float pitchDeg = _pitchOverrideDeg >= 0f
            ? _pitchOverrideDeg
            : Mathf.Lerp(40f, 68f, zoom01);
        float pitch = Mathf.DegToRad(pitchDeg);
        Vector3 offset = new Vector3(
            Mathf.Sin(_camYaw) * Mathf.Cos(pitch),
            Mathf.Sin(pitch),
            Mathf.Cos(_camYaw) * Mathf.Cos(pitch)) * _camDist;
        _camera.Position = _camTarget + offset;
        _camera.LookAt(_camTarget, Vector3.Up);
        // Projection: orthographic (strategic map) samples every tile at one scale, so
        // wheel-zoom drives the ortho Size (which tracks _camDist, keeping the framing
        // continuous across a projection toggle) rather than a perspective pull-back.
        if (_orthographic)
        {
            _camera.Projection = Camera3D.ProjectionType.Orthogonal;
            _camera.Size = Mathf.Max(1f, _camDist * OrthoSizeFactor);
        }
        else
        {
            _camera.Projection = Camera3D.ProjectionType.Perspective;
        }
        ZoomChanged?.Invoke(zoom01);
        UpdateLabelScales();   // keep map labels a constant on-screen size as the zoom changes
    }

    /// <summary>True when showing the orthographic strategic map (uniform tile scale,
    /// whole-world legibility). False = the perspective "cinematic model" diorama.</summary>
    public bool IsOrthographic => _orthographic;

    /// <summary>Flip between the orthographic strategic map and the perspective diorama.
    /// Re-places the camera immediately (and re-fires ZoomChanged so the host can retune
    /// the post pass; the miniature tilt-shift belongs to the cinematic view, not the map).</summary>
    public void SetProjection(bool orthographic)
    {
        _orthographic = orthographic;
        if (_camera != null) PlaceCamera();
    }

    // ── Input: pan / zoom / pick ────────────────────────────────────────────

    /// <summary>Keyboard camera controls (2026-08-19): Q/E orbit via rotate_left /
    /// rotate_right, WASD/arrows pan via the ui_* actions (which the project binds to
    /// both), the same bindings every other 3D scene reads through CameraController.
    /// Polled per-frame for held keys; zoom stays on the wheel. Pan runs in the camera's
    /// ground frame, same math as left-drag, and scales with zoom so a held key covers
    /// the screen in about the same time at any distance.</summary>
    private const float KeyOrbitYawSpeed = 1.75f;   // rad/s ≈ 100°/s, CameraController-comparable
    private const float KeyPanSpeedFactor = 0.55f;  // × _camDist per second of held key

    public override void _Process(double delta)
    {
        if (!AcceptInput || _camera == null)
            return;

        float turn = 0f;
        if (Input.IsActionPressed("rotate_left")) turn -= 1f;
        if (Input.IsActionPressed("rotate_right")) turn += 1f;

        float panX = 0f, panZ = 0f;   // screen-right, screen-up on the ground plane
        if (Input.IsActionPressed("ui_right")) panX += 1f;
        if (Input.IsActionPressed("ui_left")) panX -= 1f;
        if (Input.IsActionPressed("ui_up")) panZ += 1f;
        if (Input.IsActionPressed("ui_down")) panZ -= 1f;

        if (turn == 0f && panX == 0f && panZ == 0f)
            return;

        _camYaw += turn * KeyOrbitYawSpeed * (float)delta;
        if (panX != 0f || panZ != 0f)
        {
            // Same ground frame the drag pan uses, so screen-right is always right
            // regardless of orbit yaw.
            Vector3 right = new Vector3(Mathf.Cos(_camYaw), 0f, -Mathf.Sin(_camYaw));
            Vector3 fwdGround = new Vector3(-Mathf.Sin(_camYaw), 0f, -Mathf.Cos(_camYaw));
            float speed = _camDist * KeyPanSpeedFactor * (float)delta;
            _camTarget += right * (panX * speed) + fwdGround * (panZ * speed);
            ClampTarget();
        }
        PlaceCamera();   // no MaybeAutoCityTransition: pan never changes _camDist, matching drag-pan
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (!AcceptInput || _camera == null)
            return;

        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 0.9f, MinDist(), CamDistMax); PlaceCamera(); MaybeAutoCityTransition(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            { _camDist = Mathf.Clamp(_camDist * 1.1f, MinDist(), CamDistMax); PlaceCamera(); MaybeAutoCityTransition(); }
            else if (mb.ButtonIndex == MouseButton.Middle)
            {
                // (2026-08-13) Middle-drag = orbit: horizontal turns the yaw,
                // vertical adjusts the viewing angle. Double-click resets to
                // the classic framing.
                if (mb.Pressed && mb.DoubleClick) { ResetOrbit(); _orbiting = false; }
                else _orbiting = mb.Pressed;
            }
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _dragging = true; _dragMoved = false; }
                else
                {
                    if (_dragging && !_dragMoved)
                    {
                        // In city view a click first tries the grounds (a building opens its
                        // panel; a hit is consumed). On the world map it's an ordinary tile pick.
                        if (_cityMode)
                            TryPickHomeGrounds(mb.Position);
                        else
                            PickTile(mb.Position);
                    }
                    _dragging = false;
                }
            }
        }
        else if (ev is InputEventMouseMotion mmOrbit && _orbiting)
        {
            // (2026-08-13) Orbit: yaw from horizontal, pitch from vertical
            // (an explicit pitch sticks until the next big zoom-slaved fly).
            _camYaw -= mmOrbit.Relative.X * OrbitYawSens;
            float basePitch = _pitchOverrideDeg >= 0f
                ? _pitchOverrideDeg
                : Mathf.Lerp(40f, 68f, Mathf.InverseLerp(CamDistMin, CamDistMax, _camDist));
            _pitchOverrideDeg = Mathf.Clamp(
                basePitch + mmOrbit.Relative.Y * OrbitPitchSens, PitchMinDeg, PitchMaxDeg);
            PlaceCamera();
        }
        else if (ev is InputEventMouseMotion mm && _dragging)
        {
            if (mm.Relative.LengthSquared() > 1f)
                _dragMoved = true;
            float k = _camDist * 0.0012f;
            // Screen-up drags the map away from the camera; divide by sin(pitch) so a
            // vertical drag covers the same GROUND distance as a horizontal one.
            // (2026-08-13) Pan in the CAMERA's ground frame so dragging right
            // always moves the map right regardless of orbit yaw.
            float pitchSin = Mathf.Max(0.3f, (_camera.Position - _camTarget).Normalized().Y);
            Vector3 right = new Vector3(Mathf.Cos(_camYaw), 0f, -Mathf.Sin(_camYaw));
            Vector3 fwdGround = new Vector3(-Mathf.Sin(_camYaw), 0f, -Mathf.Cos(_camYaw));
            _camTarget += right * (-mm.Relative.X * k)
                        + fwdGround * (mm.Relative.Y * k / pitchSin);
            ClampTarget();
            PlaceCamera();
        }
        // macOS trackpad: pinch to zoom (a Mac trackpad never sends wheel events, so
        // wheel-only zoom is dead there) and two-finger drag to pan.
        else if (ev is InputEventMagnifyGesture mag)
        {
            _camDist = Mathf.Clamp(_camDist / mag.Factor, MinDist(), CamDistMax);
            PlaceCamera();
            MaybeAutoCityTransition();
        }
        else if (ev is InputEventPanGesture pan)
        {
            // (2026-08-13) Trackpad orbit: Alt/Option + two-finger drag turns
            // the rig (a trackpad has no middle button). Plain two-finger
            // drag pans, yaw-aware like the mouse pan.
            if (pan.AltPressed)
            {
                _camYaw -= pan.Delta.X * OrbitYawSens * 6f;   // gesture deltas are small
                float basePitch = _pitchOverrideDeg >= 0f
                    ? _pitchOverrideDeg
                    : Mathf.Lerp(40f, 68f, Mathf.InverseLerp(CamDistMin, CamDistMax, _camDist));
                _pitchOverrideDeg = Mathf.Clamp(
                    basePitch + pan.Delta.Y * OrbitPitchSens * 6f, PitchMinDeg, PitchMaxDeg);
                PlaceCamera();
                return;
            }
            float k = _camDist * 0.010f;
            float pitchSin = Mathf.Max(0.3f, (_camera.Position - _camTarget).Normalized().Y);
            Vector3 right = new Vector3(Mathf.Cos(_camYaw), 0f, -Mathf.Sin(_camYaw));
            Vector3 fwdGround = new Vector3(-Mathf.Sin(_camYaw), 0f, -Mathf.Cos(_camYaw));
            _camTarget += right * (pan.Delta.X * k)
                        + fwdGround * (-pan.Delta.Y * k / pitchSin);
            ClampTarget();
            PlaceCamera();
        }
        else if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R })
        {
            ResetOrbit();
        }
    }

    /// <summary>Reset the orbit to the classic framing: yaw 0, pitch back on
    /// its zoom-slaved automatic. R key, or double-middle-click.</summary>
    private void ResetOrbit()
    {
        _camYaw = 0f;
        _pitchOverrideDeg = -1f;
        PlaceCamera();
    }

    private void ClampTarget()
    {
        if (_world == null) return;
        _camTarget.X = Mathf.Clamp(_camTarget.X, 0f, _world.Width * ColSpacing);
        _camTarget.Z = Mathf.Clamp(_camTarget.Z, 0f, _world.Height * RowSpacing);
    }

    /// <summary>Center the camera on a tile. The strategic view calls this when it opens
    /// the deploy side-panel, so the chosen staging point sits in view beside the panel
    /// rather than wherever it happened to be (possibly behind it).</summary>
    public void FocusTile(int col, int row)
    {
        if (_camera == null || _world == null) return;
        _camTarget = TileOrigin(col, row);
        ClampTarget();
        PlaceCamera();
    }

    /// <summary>Cinematic fly-in: animate the camera FROM wherever it is (usually the
    /// whole-world overview) down INTO a region, both panning to the tile and zooming
    /// in to frame the operating window. The zoom drop also rakes the pitch (see
    /// PlaceCamera), so it reads as a dramatic swoop, not a jump. Used when a staging
    /// point is chosen for deploy.</summary>
    public void FlyToTile(int col, int row, float targetDist = 30f, float screenLeftShiftPx = 0f)
    {
        if (_camera == null || _world == null) return;
        float dist = Mathf.Clamp(targetDist, CamDistMin, CamDistMax);
        Vector3 to = TileOrigin(col, row);
        // Shift the look-target in world +X (which is screen-right, since the camera has
        // no yaw) so the tile lands screenLeftShiftPx to the LEFT of center. Used to
        // center the beacon in the map area NOT covered by the deploy drawer. Converts
        // pixels→world via the ortho size at the target distance.
        if (screenLeftShiftPx != 0f)
        {
            var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
            float size = dist * OrthoSizeFactor;                 // vertical world extent
            float worldPerPx = size / (vp.Y > 1f ? vp.Y : 1080f);
            to.X += screenLeftShiftPx * worldPerPx;
        }
        FlyTo(to, dist);
    }

    /// <summary>Reverse of the fly-in: swoop back out to the whole-world overview.
    /// The strategic view calls this when a deploy is cancelled.</summary>
    public void FlyToOverview()
    {
        if (_camera == null || _world == null) return;
        FlyTo(WorldCenter(), OverviewDist());
    }

    /// <summary>Snap the camera onto a tile at closest zoom with NO animation, so a
    /// following <see cref="FlyToOverview"/> reads as a swoop OUT from that tile.
    /// Used for the campus→world "ascend from your city" transition (Phase 2,
    /// Stage 2).</summary>
    public void SnapToTileClose(int col, int row)
    {
        if (_camera == null || _world == null) return;
        _camTarget = TileOrigin(col, row);
        _camDist = CamDistMin;
        ClampTarget();
        PlaceCamera();
    }

    private void FlyTo(Vector3 toTarget, float toDist)
    {
        Vector3 fromTarget = _camTarget;
        float fromDist = _camDist;
        var tw = CreateTween();
        tw.TweenMethod(Callable.From((float u) =>
        {
            _camTarget = fromTarget.Lerp(toTarget, u);
            _camDist = Mathf.Lerp(fromDist, toDist, u);
            ClampTarget();
            PlaceCamera();
        }), 0.0f, 1.0f, 0.8).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
    }

    /// <summary>Raycast the click onto the y=0 ground plane, then resolve the nearest
    /// tile center among the 3×3 offset neighborhood, cheap and exact enough for a
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

        // Phase 3: clicking a discovered enemy SEAT capital descends into it. Checked BEFORE staging
        // so the click isn't stolen by a staging beacon sitting on the capital. You can't deploy
        // from an enemy capital anyway, so entering it is the only sensible action there.
        if (!_cityMode && discovery != TileDiscovery.Unseen)
        {
            var s = _world.SettlementAt(col, row);
            if (s != null && s.Tier == SettlementTier.City && s.IsSeat && !s.IsGuildHome)
            {
                EnterCityView(s);
                return;
            }
        }
        if (t.IsStagingPoint && discovery != TileDiscovery.Unseen)
        {
            ShowWindowPreview(col, row);
            return;
        }
        // Selecting the HOME city on the world map zooms into it (camera-only; the campus is
        // already there at true scale). Staging wins above for the home seat so deploy stays
        // reachable from its beacon; you enter the home campus from a non-beacon city tile.
        if (!_cityMode && _cityTiles.Contains(new Vector2I(col, row)))
        {
            EnterCityMode();   // the home campus
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
        _canvasLayer?.QueueFree();

        int total = _world.Width * _world.Height;
        _instanceIndexOf = new int[total];
        _tileLayer = new byte[total];

        // Pass over the world once to split membership and count each layer.
        // Unseen tiles, land AND water alike, go to the canvas layer (art pass A6), so
        // the unpainted world is one uniform matte surface with one grout pattern
        // and nothing about the ground can leak through mesh differences.
        int waterCount = 0, canvasCount = 0;
        for (int i = 0; i < total; i++)
        {
            var d = _revealAll ? TileDiscovery.Explored : _world.Tiles[i].Discovery;
            _tileLayer[i] = d == TileDiscovery.Unseen ? LayerCanvas
                          : _world.Tiles[i].IsWater ? LayerWater
                          : LayerLand;
            if (_tileLayer[i] == LayerWater) waterCount++;
            else if (_tileLayer[i] == LayerCanvas) canvasCount++;
        }

        var landMesh = new CylinderMesh
        {
            // Slight top taper gives every LAND prism a visible rim line under flat
            // shading, the "grout" that makes 9k identical prisms read as tiles.
            TopRadius = HexR * 0.96f,
            BottomRadius = HexR * 1.0f,
            Height = 1f,               // unit height; per-instance Y scale carries elevation
            RadialSegments = 6,
            Rings = 0,
            CapBottom = false,
        };
        // A2: painterly prism shader (brush grain, striated skirts, banded toon
        // light) via the shared factory; PainterlyPrism.Enabled = false is the
        // instant fallback to the flat A4 materials.
        landMesh.Material = PainterlyPrism.TileMaterial(PainterlyPrism.Land, 0.9f);

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
        // A3-lite: animated painterly sea (world-space swell, drifting sky
        // bands, banded sun glints) on the existing prisms. The full welded
        // water plane (combat's WaterPlane port) remains a later upgrade.
        waterMesh.Material = PainterlyPrism.TileMaterial(PainterlyPrism.Water, 0.55f);

        var canvasMesh = new CylinderMesh
        {
            // Same grout taper as land: the rim lines over the parchment read as the
            // map's PENCIL UNDER-DRAWING, a faint hex grid waiting to be painted.
            TopRadius = HexR * 0.96f,
            BottomRadius = HexR * 1.0f,
            Height = 1f,
            RadialSegments = 6,
            Rings = 0,
            CapBottom = false,
        };
        // Paper-fibre grain, fully matte: no sheen may slide across the
        // unpainted world.
        canvasMesh.Material = PainterlyPrism.TileMaterial(PainterlyPrism.Canvas, 1.0f);

        var landMm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = landMesh,
            InstanceCount = total - waterCount - canvasCount,
        };
        var waterMm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = waterMesh,
            InstanceCount = waterCount,
        };
        var canvasMm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = canvasMesh,
            InstanceCount = canvasCount,
        };

        int landIdx = 0, waterIdx = 0, canvasIdx = 0;
        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            int i = row * _world.Width + col;
            var tile = _world.Tiles[i];
            float h = TileHeightAt(col, row);   // contour-follow: every tile at its own terrain height
            var basis = HexYaw * Basis.FromScale(new Vector3(1f, h, 1f));
            var origin = TileOrigin(col, row) + new Vector3(0f, h * 0.5f, 0f);
            var xf = new Transform3D(basis, origin);
            var c = TileColor(tile, col, row);
            switch (_tileLayer[i])
            {
                case LayerWater:
                    _instanceIndexOf[i] = waterIdx;
                    waterMm.SetInstanceTransform(waterIdx, xf);
                    waterMm.SetInstanceColor(waterIdx, c);
                    waterIdx++;
                    break;
                case LayerCanvas:
                    _instanceIndexOf[i] = canvasIdx;
                    canvasMm.SetInstanceTransform(canvasIdx, xf);
                    canvasMm.SetInstanceColor(canvasIdx, c);
                    canvasIdx++;
                    break;
                default:
                    _instanceIndexOf[i] = landIdx;
                    landMm.SetInstanceTransform(landIdx, xf);
                    landMm.SetInstanceColor(landIdx, c);
                    landIdx++;
                    break;
            }
        }

        _landLayer = new MultiMeshInstance3D { Name = "LandLayer", Multimesh = landMm };
        _waterLayer = new MultiMeshInstance3D { Name = "WaterLayer", Multimesh = waterMm };
        _canvasLayer = new MultiMeshInstance3D
        {
            Name = "CanvasLayer",
            Multimesh = canvasMm,
            // Canvas is the LOWEST geometry: nothing below it receives shadow, and
            // thousands of coplanar bright slabs self-shadowing under the raking sun
            // produce acne (the diagonal slash artifacts, invisible on the old dark
            // void). It still RECEIVES shadows, so the painted world rests on the
            // paper with a real cast shadow.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_landLayer);
        AddChild(_waterLayer);
        AddChild(_canvasLayer);
    }

    /// <summary>Recolor every instance in place (lens switch). Transforms untouched.</summary>
    private void RecolorTiles()
    {
        if (_landLayer?.Multimesh == null || _waterLayer?.Multimesh == null
            || _canvasLayer?.Multimesh == null || _world == null)
            return;
        for (int i = 0; i < _world.Tiles.Length; i++)
        {
            int col = i % _world.Width, row = i / _world.Width;
            var c = TileColor(_world.Tiles[i], col, row);
            LayerMultimesh(i).SetInstanceColor(_instanceIndexOf[i], c);
        }
        if (PreviewActive)
            ApplyWindowTint();
    }

    /// <summary>The MultiMesh holding a flat tile index's instance.</summary>
    private MultiMesh LayerMultimesh(int i) => _tileLayer[i] switch
    {
        LayerWater => _waterLayer.Multimesh,
        LayerCanvas => _canvasLayer.Multimesh,
        _ => _landLayer.Multimesh,
    };

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
        // overview zoom; quantizing into steps first turns noise into landforms
        // (plateaus, escarpments, stepped foothills), which is most of the Civ read.
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

    /// <summary>Rivers and roads as DRAWN STROKES (art pass A9): for each tile a
    /// thin strip runs from its CENTRE out to each active edge midpoint. The
    /// neighbour draws its own half, so paths run continuously THROUGH tiles
    /// (the model the expedition window always used, matching the 2D map)
    /// instead of as boundary dashes. Each half hugs its OWN tile's top, so
    /// lines follow the terrain and step at cliffs like ink over relief.
    /// Water tiles are skipped (no strokes across lakes); Unseen is skipped
    /// (nothing is drawn on unpainted canvas); Charted keeps its strokes,
    /// inked chart lines on the underpainting.</summary>
    private void RebuildEdges()
    {
        _riverLayer?.QueueFree();
        _roadLayer?.QueueFree();

        // A9b: rivers become WINDING RIBBONS (RiverMesh: Bézier through-curves +
        // deterministic meander, bank-shaded); roads stay straight strokes.
        var riverTiles = new List<(Vector3 center, List<Vector3> mids)>();
        var roads = new List<Transform3D>();

        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            var tile = _world.GetTile(col, row);
            if ((tile.RiverEdges | tile.RoadEdges) == 0)
                continue;
            if (tile.IsWater)
                continue;
            var discovery = _revealAll ? TileDiscovery.Explored : tile.Discovery;
            if (discovery == TileDiscovery.Unseen)
                continue;

            Vector3 center = TileOrigin(col, row);
            center.Y = TileHeightAt(col, row) + 0.02f;
            List<Vector3> riverMids = null;
            var (q, r) = HexCoord.OffsetToAxial(col, row);
            for (int i = 0; i < 6; i++)
            {
                bool riv = (tile.RiverEdges & (1 << i)) != 0;
                bool road = (tile.RoadEdges & (1 << i)) != 0;
                if (!riv && !road)
                    continue;

                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                bool inBounds = _world.InBounds(nc, nr);
                Vector3 nbr = inBounds ? TileOrigin(nc, nr)
                                       : center + DirectionVector(center, col, i);
                // CONTINUITY (user report: strokes break at height steps): the
                // shared edge midpoint sits at the AVERAGE of the two rendered
                // heights, so both tiles' halves meet at the same point and the
                // stroke SLOPES across the tile instead of jumping at the seam.
                float nbrH = inBounds ? TileHeightAt(nc, nr) : TileHeightAt(col, row);
                Vector3 edgeMid = new Vector3((center.X + nbr.X) * 0.5f,
                                              (TileHeightAt(col, row) + nbrH) * 0.5f + 0.02f,
                                              (center.Z + nbr.Z) * 0.5f);
                if (riv)
                    (riverMids ??= new List<Vector3>()).Add(edgeMid);
                if (road)
                {
                    Vector3 seg = edgeMid - center;
                    float len = seg.Length();
                    if (len < 1e-4f)
                        continue;
                    // Tilted basis: local +X runs along the (possibly sloped)
                    // segment, slightly overlength so joints at slope kinks close.
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
            Name = "RiverLayer",
            // A9c: 0.22 was invisible at map zoom; map rivers are exaggerated, not to scale.
            Mesh = RiverMesh.Build(riverTiles, 0.36f, Hex3DPalette.RiverWater, Hex3DPalette.RiverBank),
            MaterialOverride = PainterlyPrism.RiverMaterial(),
        };
        AddChild(_riverLayer);
        _roadLayer = MakeEdgeLayer("RoadLayer", roads,
            new Vector3(1f, 0.025f, 0.14f), Hex3DPalette.RoadStroke);
        // Phase-2 polish note honoured: strokes read as litter at city zoom, so they
        // are hidden in city view (Enter/LeaveCityMode toggle them).
        // A7: kingdom-border ink, a dark drawn line along every edge where two
        // differing realms (or a realm and the wilds) meet. Both sides must be
        // discovered: a border may not leak into the unpainted canvas. Interior
        // edges once via direction bits 0–2 (the mirror is the neighbour's 3–5).
        _borderLayer?.QueueFree();
        var borders = new List<Transform3D>();
        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            var t = _world.GetTile(col, row);
            if (!t.IsLand)
                continue;
            var d1 = _revealAll ? TileDiscovery.Explored : t.Discovery;
            if (d1 == TileDiscovery.Unseen)
                continue;
            var (q, r) = HexCoord.OffsetToAxial(col, row);
            for (int i = 0; i < 3; i++)
            {
                var (dq, dr) = HexCoord.AxialDirections[i];
                var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
                if (!_world.InBounds(nc, nr))
                    continue;
                var n = _world.GetTile(nc, nr);
                if (!n.IsLand)
                    continue;
                var d2 = _revealAll ? TileDiscovery.Explored : n.Discovery;
                if (d2 == TileDiscovery.Unseen)
                    continue;
                if (t.KingdomId == n.KingdomId)
                    continue;
                Vector3 a = TileOrigin(col, row);
                Vector3 b = TileOrigin(nc, nr);
                Vector3 mid = (a + b) * 0.5f;
                mid.Y = Mathf.Max(TileHeightAt(col, row), TileHeightAt(nc, nr)) + 0.012f;
                Vector3 dd = (b - a).Normalized();
                Vector3 perp = new Vector3(-dd.Z, 0f, dd.X);
                borders.Add(new Transform3D(
                    new Basis(Vector3.Up, Mathf.Atan2(-perp.Z, perp.X)), mid));
            }
        }
        _borderLayer = MakeEdgeLayer("BorderLayer", borders,
            new Vector3(HexR * 0.98f, 0.02f, 0.06f), Hex3DPalette.BorderInk);

        _riverLayer.Visible = !_cityMode;
        _roadLayer.Visible = !_cityMode;
        _borderLayer.Visible = !_cityMode;
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
        // A9: matte ink stroke, no sheen.
        mesh.Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.95f };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = xfs.Count,
        };
        for (int i = 0; i < xfs.Count; i++)
            mm.SetInstanceTransform(i, xfs[i]);
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
        _scaledLabels.Clear();        // label nodes live in _markers, just freed above
        _cityHiddenMarkers.Clear();   // its nodes live in _markers, just freed above

        // POIs: discovered only (or reveal), same rule as StrategicView's POI layer.
        // A7: a flattened DAB of paint on the map rather than a floating ball.
        var poiMesh = new SphereMesh { Radius = 0.34f, Height = 0.26f, RadialSegments = 10, Rings = 6 };
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
                Position = MarkerPos(poi.X, poi.Y, 0.16f),   // A7: sits ON the ground, a dab of paint
            }, poi.X, poi.Y);
        }

        // Settlements: one marker at the center, sized by tier, named label.
        // The guild's own seat (IsGuildHome) ALWAYS shows and reads distinctly: a
        // taller, glowing violet block labelled with the guild name. This is where
        // the campus is (Phase 2, campus-as-world-location).
        foreach (var s in _world.Settlements)
        {
            bool home = s.IsGuildHome;
            if (!home && !SettlementVisible(s)) continue;
            bool city = s.Tier == SettlementTier.City;
            Color c = home ? UITheme.Violet : (s.IsSeat ? UITheme.Gold : UITheme.ArcaneBlue);
            // A4: painted game-piece, not polished metal (Metallic 0.9 was the
            // pass-2 crafted-model read). Emission below keeps the map-readability
            // glow; loudness law unchanged.
            var mat = new StandardMaterial3D { AlbedoColor = c, Metallic = 0f, Roughness = 0.7f };
            // The city is now marked primarily by its grey FOOTPRINT REGION (see TileColor) + a large
            // name label, so the centre marker is just a modest metal accent, deliberately NOT a tall
            // pillar, which read as one of the gold staging beacons. Cities glow faintly; towns don't.
            if (home || city)
            {
                mat.EmissionEnabled = true;
                mat.Emission = c;
                mat.EmissionEnergyMultiplier = home ? 0.5f : 0.4f;
            }
            // Pass 2: settlements are METAL, worked brass pieces standing on the carving. Hue still
            // carries meaning (violet guild seat / gold enemy capital / arcane-blue lesser city).
            float side = home ? 1.35f : (city ? 1.2f : 0.65f);
            var block = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = Vector3.One * side },
                MaterialOverride = mat,
                Position = MarkerPos(s.CenterX, s.CenterY, side * 0.5f + 0.05f),
            };
            AddMarker(block);
            // Only SEAT capitals (and the home) get a name label. Labelling every town + lesser city
            // buried the map in overlapping, duplicated text ("The <Kingdom> Town" ×N per kingdom).
            // Lesser settlements are still marked by their block + (cities) their grey footprint; the
            // label stays a constant, readable on-screen size at any zoom (see UpdateLabelScales).
            if (home || s.IsSeat)
            {
                string label = home
                    ? $"{(SaveManager.ActiveSave?.Ledger?.GuildName ?? "Your Guild")} (your seat)"
                    : SettlementDisplayName(s);
                var lbl = MakeLabel(label, c, MarkerPos(s.CenterX, s.CenterY, side + 1.4f), 88, 0.028f);
                AddMarker(lbl);
                if (home)
                {
                    // Tracked so city view can hide the seat block + label. Zoomed in, they'd
                    // just occlude the campus you're standing in. Back when the camera leaves.
                    _cityHiddenMarkers.Add(block);
                    _cityHiddenMarkers.Add(lbl);
                    block.Visible = lbl.Visible = !_cityMode;
                }
            }
        }

        // Staging points: gold beacons (the launch options the strategic view deploys
        // from). Deliberately TALL and capped with a glowing orb: a staging point is a
        // single hex, which is only a few pixels at whole-world orthographic zoom, so
        // the beacon has to stand up off the map to stay visible and aimable.
        // A beacon standing ON the city represents the PORTAL BUILDING (map travel);
        // AddMarker's city-tile tracking hides it in city view like the seat block.
        foreach (var sp in _world.StagingPoints)
        {
            // A7: a planted gold STANDARD instead of the cone, the guild's launch
            // flag in the world. The glowing orb cap stays smaller but bright: it
            // is the "see me from across the map" element (loudness law).
            var banner = new MeshInstance3D
            {
                Mesh = PainterlyProps.Banner(),
                Position = MarkerPos(sp.X, sp.Y, 0.02f),
            };
            banner.SetSurfaceOverrideMaterial(PainterlyProps.BannerFlagSurface,
                FlagMaterial(UITheme.Gold, 0.7f));
            AddMarker(banner, sp.X, sp.Y);
            AddMarker(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.55f, Height = 1.1f, RadialSegments = 12, Rings = 8 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = UITheme.Gold,
                    EmissionEnabled = true, Emission = UITheme.Gold, EmissionEnergyMultiplier = 1.0f,
                },
                Position = MarkerPos(sp.X, sp.Y, 3.6f),
            }, sp.X, sp.Y);
        }

        // Shard zones: violet spikes at the gate once discovered.
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

        // The Convergence: Kassian's seat, the cycle's terminal location.
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

        // Warfronts: red conflict beacons at active fronts. CYCLE state (not
        // WorldData), injected via SetWarfronts, so they rebuild with the markers.
        // Deliberately LOUD: a tall spike, a bright floating orb, and a "War" label,
        // so an active front can't be missed on the whole-world view.
        foreach (var wf in _warfronts)
        {
            // A7: a WAR BANNER instead of the spike, scaled past the staging
            // standards so a front still can't be missed (loudness law: the orb +
            // label stay).
            var banner = new MeshInstance3D
            {
                Mesh = PainterlyProps.Banner(),
                Position = MarkerPos(wf.X, wf.Y, 0.02f),
                Scale = new Vector3(1.3f, 1.3f, 1.3f),
            };
            banner.SetSurfaceOverrideMaterial(PainterlyProps.BannerFlagSurface,
                FlagMaterial(UITheme.Danger, 1.0f));
            AddMarker(banner);
            AddMarker(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f, RadialSegments = 12, Rings = 8 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = UITheme.Danger,
                    EmissionEnabled = true, Emission = UITheme.Danger, EmissionEnergyMultiplier = 1.3f,
                },
                Position = MarkerPos(wf.X, wf.Y, 4.7f),
            });
            AddMarker(MakeLabel("⚔ War", UITheme.Danger, MarkerPos(wf.X, wf.Y, 5.9f)));
        }
    }

    /// <summary>Pennant material for a planted banner (A7): coloured, matte,
    /// two-sided, glowing enough to stay findable at whole-world zoom.</summary>
    private static StandardMaterial3D FlagMaterial(Color c, float emission)
        => new StandardMaterial3D
        {
            AlbedoColor = c,
            Roughness = 0.8f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true,
            Emission = c,
            EmissionEnergyMultiplier = emission,
        };

    // ── Stage 1: decoration layers ──────────────────────────────────────────

    private static uint HashTile(int col, int row, uint salt)
    {
        uint h = (uint)(col * 73856093) ^ (uint)(row * 19349663) ^ (salt * 83492791u);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return h;
    }
    private static float Hash01(uint h) => (h & 0xFFFFu) / 65535f;

    /// <summary>Trees on forests, cap-stones on mountain chains, snow spires on ice:
    /// the detail that makes close zoom worth visiting. Instanced (two MultiMeshes for
    /// thousands of props), deterministic per tile, and EXPLORED-ONLY: charted land
    /// knows its shape, not what stands on it, and props appearing is discovery's reward.</summary>
    private void RebuildDecorations()
    {
        foreach (var l in _decoLayers)
            l.QueueFree();
        _decoLayers.Clear();

        var broadleaf = new List<(Transform3D xf, Color c)>();
        var conifers = new List<(Transform3D xf, Color c)>();
        var peaks = new List<(Transform3D xf, Color c)>();

        for (int row = 0; row < _world.Height; row++)
        for (int col = 0; col < _world.Width; col++)
        {
            var t = _world.GetTile(col, row);
            var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;
            if (discovery != TileDiscovery.Explored || !t.IsLand)
                continue;

            float h = TileHeightAt(col, row);   // city-aware: decorations sit on the flattened plateau too
            Vector3 basePos = TileOrigin(col, row);

            if (t.Terrain == TT.Forest)
            {
                // A5: canopy blob clusters (base-origin meshes, placed AT ground
                // height), 60/40 broadleaf/conifer by hash, random yaw so the
                // cluster meshes don't read as clones.
                int n = 2 + (int)(HashTile(col, row, 1) % 2);
                for (int i = 0; i < n; i++)
                {
                    float ang = Hash01(HashTile(col, row, (uint)(7 + i))) * Mathf.Tau;
                    float rad = Hash01(HashTile(col, row, (uint)(31 + i))) * 0.55f;
                    float s = 0.55f + Hash01(HashTile(col, row, (uint)(53 + i))) * 0.55f;
                    var pos = basePos + new Vector3(Mathf.Cos(ang) * rad, h - 0.03f, Mathf.Sin(ang) * rad);
                    var yaw = new Basis(Vector3.Up, Hash01(HashTile(col, row, (uint)(97 + i))) * Mathf.Tau);
                    var xf = new Transform3D(yaw * Basis.FromScale(new Vector3(s, s, s)), pos);
                    if (HashTile(col, row, (uint)(71 + i)) % 10 < 6)
                        broadleaf.Add((xf, Jitter(new Color(0.21f, 0.35f, 0.15f), col, row + i * 977, 0.12f)));
                    else
                        conifers.Add((xf, Jitter(new Color(0.13f, 0.25f, 0.15f), col, row + i * 977, 0.10f)));
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

        _decoLayers.Add(MakeDecoLayer("BroadleafLayer", broadleaf, PainterlyProps.BroadleafCanopy(), 1.3f));
        _decoLayers.Add(MakeDecoLayer("ConiferLayer", conifers, PainterlyProps.ConiferCanopy(), 1.4f));
        _decoLayers.Add(MakeDecoLayer("PeakLayer", peaks, PainterlyProps.PeakCone(0.34f, 0.9f), 1.6f));
    }

    private MultiMeshInstance3D MakeDecoLayer(string name,
        List<(Transform3D xf, Color c)> items, Mesh mesh, float meshExtent)
    {
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
        // Style-guide scatter law: every world-space MultiMesh needs an explicit
        // CustomAabb (auto AABB on world-space instance transforms is unreliable;
        // whole layers frustum-cull as one unit on a camera turn). Latent here
        // since stage 1; fixed with the A5 refactor. Grown by mesh extent × the
        // max scale band.
        if (items.Count > 0)
        {
            Vector3 min = items[0].xf.Origin, max = min;
            for (int i = 1; i < items.Count; i++)
            {
                var o = items[i].xf.Origin;
                min = new Vector3(Mathf.Min(min.X, o.X), Mathf.Min(min.Y, o.Y), Mathf.Min(min.Z, o.Z));
                max = new Vector3(Mathf.Max(max.X, o.X), Mathf.Max(max.Y, o.Y), Mathf.Max(max.Z, o.Z));
            }
            mm.CustomAabb = new Aabb(min, max - min).Grow(meshExtent);
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
            p.Y = TileHeightAt(c2, r2) + 0.05f;
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
        p.Y = TileHeightAt(col, row);
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
            LayerMultimesh(i).SetInstanceColor(_instanceIndexOf[i], lifted);
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

    /// <summary>A readable name for a settlement's map label. Settlement generation doesn't assign
    /// names yet (WorldSettlement.Name is empty), so fall back to the owning kingdom + tier
    /// ("The Untamed Seat", "… City"), enough to identify a capital until place-name generation lands.</summary>
    private string SettlementDisplayName(WorldSettlement s)
    {
        if (!string.IsNullOrEmpty(s.Name)) return s.Name;
        string kind = s.Tier == SettlementTier.City ? (s.IsSeat ? "Seat" : "City") : "Town";
        if (_kingdoms != null && !string.IsNullOrEmpty(s.KingdomId)
            && _kingdoms.TryGetValue(s.KingdomId, out var k) && !string.IsNullOrEmpty(k.DisplayName))
            return $"{k.DisplayName} {kind}";
        return kind;
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
        p.Y = TileHeightAt(col, row) + lift;
        return p;
    }

    private void AddMarker(Node3D node)
    {
        AddChild(node);
        _markers.Add(node);
    }

    /// <summary>As <see cref="AddMarker(Node3D)"/>, but for a marker standing on a KNOWN
    /// tile: if that tile belongs to the city, the marker joins the city-hidden set (not
    /// drawn while in city view, where it would loom over the campus it stands on).</summary>
    private void AddMarker(Node3D node, int col, int row)
    {
        AddMarker(node);
        if (_cityTiles.Contains(new Vector2I(col, row)))
        {
            _cityHiddenMarkers.Add(node);
            node.Visible = !_cityMode;
        }
    }

    private Label3D MakeLabel(string text, Color color, Vector3 pos, int fontSize = 42, float screenFrac = 0.02f)
    {
        var lbl = new Label3D
        {
            Text = text,
            Position = pos,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = fontSize,
            Modulate = color,
            OutlineSize = 10,
            // Linear + mipmaps so labels don't render pixelated/aliased when minified at
            // whole-world zoom (the default nearest filter shimmered).
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        };
        // Track it so its world size follows the zoom, keeping a constant ON-SCREEN size. Without
        // this a world-sized label shimmers to a few pixels at whole-world zoom and balloons up close.
        _scaledLabels.Add((lbl, fontSize, screenFrac));
        ApplyLabelScale(lbl, fontSize, screenFrac);
        return lbl;
    }

    /// <summary>Set a label's PixelSize so it occupies a constant fraction of screen HEIGHT at the
    /// current zoom. In the orthographic map, screen fraction = worldHeight / orthoSize, with
    /// orthoSize = _camDist·OrthoSizeFactor and worldHeight = fontSize·PixelSize; solve for
    /// PixelSize. Clamped so it never collapses or explodes.</summary>
    private void ApplyLabelScale(Label3D lbl, float fontSize, float screenFrac)
    {
        float px = _camDist * OrthoSizeFactor * screenFrac / Mathf.Max(1f, fontSize);
        lbl.PixelSize = Mathf.Clamp(px, 0.001f, 0.5f);
    }

    /// <summary>Rescale every tracked map label to the current zoom (called from PlaceCamera as the
    /// camera moves). Drops freed labels lazily.</summary>
    private void UpdateLabelScales()
    {
        for (int i = _scaledLabels.Count - 1; i >= 0; i--)
        {
            var (lbl, fs, frac) = _scaledLabels[i];
            if (lbl == null || !IsInstanceValid(lbl)) { _scaledLabels.RemoveAt(i); continue; }
            ApplyLabelScale(lbl, fs, frac);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Lens colors: MARKED COPY of StrategicView's private color logic.
    // See the header note: extract to a shared TileColors class if adopted.
    // ════════════════════════════════════════════════════════════════════════

    private Color TileColor(in WorldTile t, int col, int row)
    {
        var discovery = _revealAll ? TileDiscovery.Explored : t.Discovery;
        // THE UNPAINTED WORLD (art pass A6): Unseen ground is raw canvas, paper
        // grain only (coordinate hash, zero world data), plus a noisy watercolor
        // wet-edge where the canvas borders painted ground, so the frontier reads
        // as a torn wash boundary instead of a ruled line. Exploration paints the
        // world in.
        if (discovery == TileDiscovery.Unseen)
            return Hex3DPalette.CanvasTone(col, row,
                HasPaintedNeighbor(col, row) ? Hex3DPalette.WetEdgeAmount(col, row) : 0f);
        // Charted is the UNDERPAINTING: a flat pale wash with the terrain hue
        // faintly present: known shape, not yet painted in.
        if (discovery == TileDiscovery.Charted)
            return Jitter(Hex3DPalette.Underpaint(LensBaseColor(t)), col, row, 0.02f);

        Color c = LensColor(t);
        // A1: the palette is now authored as FINAL lit-scene painterly swatches in
        // Hex3DPalette; the old Grade() compensation stack is gone. Per-tile value
        // jitter (hash of col,row, stable across recolors) breaks the flat
        // fill-tool fields, amplitude per terrain so organic biomes read as painted
        // masses; this one trick is most of why the HTML mockup read painterly.
        // Cities read as a solid slate-grey REGION over their whole footprint (any lens), so a
        // capital is obvious at whole-world zoom. Applied last so it stays a flat grey.
        if (t.SettlementIndex >= 0 && t.SettlementIndex < _world.Settlements.Count
            && _world.Settlements[t.SettlementIndex].Tier == SettlementTier.City)
            c = CityRegionTint;
        return Jitter(c, col, row, Hex3DPalette.JitterAmp(t));
    }

    /// <summary>True when any hex neighbor of an Unseen tile has been discovered:
    /// the canvas tile sits on the painted world's frontier and takes the wet-edge
    /// darkening. Carries no new information: adjacency to explored ground is
    /// already visible on the map.</summary>
    private bool HasPaintedNeighbor(int col, int row)
    {
        var (q, r) = HexCoord.OffsetToAxial(col, row);
        for (int i = 0; i < 6; i++)
        {
            var (dq, dr) = HexCoord.AxialDirections[i];
            var (nc, nr) = HexCoord.AxialToOffset(q + dq, r + dr);
            if (_world.InBounds(nc, nr)
                && _world.GetTile(nc, nr).Discovery != TileDiscovery.Unseen)
                return true;
        }
        return false;
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
