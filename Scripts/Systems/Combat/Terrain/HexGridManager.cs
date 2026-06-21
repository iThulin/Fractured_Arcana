using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.cs
//
// Purpose:        The combat hex grid. Node3D parent that owns
//                 every HexTile + TileData pair, exposes axial
//                 coord lookup / neighbours / distance helpers,
//                 manages tile highlight state machines (move /
//                 range / target / deployment), and applies
//                 visual updates after terrain modifier changes.
//                 Axial coordinate convention shared with
//                 OverworldHexGrid (2D).
// Layer:          System
// Collaborators:  HexTile.cs / TileData.cs (children),
//                 Unit.cs (PlaceOnTile / ClearOccupant),
//                 CombatManager.cs (spawns + queries),
//                 MapField.cs (seeded terrain/height field),
//                 every IEffect that touches tiles
// See:            README §3 — combat hex grid is the spatial
//                 substrate for every other combat system
//
// Generation rewrite (substrate pass):
//   - Seeded RandomNumberGenerator (_rng) drives ALL generation
//     randomness; same MapSeed → same map (matches OverworldHexGrid).
//   - MapField produces coherent elevation + moisture; terrain and
//     height are DERIVED from it and correlated (water in lows,
//     stone on ridges, forest in the wet mid-band).
//   - Removed AddTerrainHeightVariation (salt-and-pepper noise) and
//     SmoothTileHeights (it averaged hand-placed features back to
//     flat). The field is already smooth by construction.
//   - Layout skeletons + theme accents now layer ADDITIVELY on the
//     field instead of flood-filling over it.
//   - Theme landmark: the previously-unused Generate*Feature methods
//     are now wired as a central set-piece per theme.
//   - ApplyVisualToTile now assigns the exported terrain Material
//     when one is set (was dead code); falls back to flat colour
//     exactly as before when materials are unassigned.
//   - Optional, null-safe theme atmosphere (sun + WorldEnvironment).
// ============================================================

/// <summary>3D combat hex grid manager. Generates the grid procedurally, exposes axial-coord helpers (GetTile, GetNeighbors, Distance), and drives the highlight state machine across every <see cref="HexTile"/>. Each Tile is paired 1:1 with a <see cref="TileData"/> that holds the game-state side of the grid.</summary>
public partial class HexGridManager : Node3D
{


    [ExportGroup("Grid Generation")]
    [Export] public int GridWidth = 7;
    [Export] public int GridHeight = 6;
    [Export] public float HexRadius = 1f;
    [Export] public MapShape Shape = MapShape.Rectangle;
    [Export] public int MapRadius = 4;                                  // hexagon / triangle / blob size
    [Export(PropertyHint.Range, "0,1,0.05")] public float BlobErosion = 0.5f;

    /// <summary>Deterministic map seed. 0 = randomise on generation (and write the chosen seed back here).</summary>
    [Export] public int MapSeed = 0;
    /// <summary>Map recipe id from Data/Maps. When set and found, the recipe drives shape/terrain/features/atmosphere and overrides the enum Theme/Layout. Empty = use enum path.</summary>
    [Export] public string MapRecipeId = "";

    // Spawn conditions

    [ExportGroup("Spawn Settings")]
    [Export] public int SpawnZonePadding = 1;
    [Export] public int ReservedSpawnRadius = 1;
    [Export] public int PlayerSpawnCount = 3;
    [Export] public int EnemySpawnCount = 3;

    [ExportGroup("Gameplay Settings")]
    public bool BlocksMovementByHeight = false;

    // Debug and testing
    [ExportGroup("Debug Settings")]
    [Export] public bool UseDebugSpawnOverrides = false;
    [Export] public Vector2I DebugPlayerAnchor = new Vector2I(1, 1);
    [Export] public Vector2I DebugEnemyAnchor = new Vector2I(4, 2);
    private Vector2I PlayerLayoutAnchor;
    private Vector2I EnemyLayoutAnchor;

    // Map generation parameters
    [ExportGroup("Map Generation")]
    [Export] public MapLayoutType LayoutType = MapLayoutType.CentralClash;
    [Export] public MapTheme Theme = MapTheme.ArcaneMeadow;
    [Export] public bool RandomizeLayout = false;

    // Density controls

    [ExportSubgroup("Map Presets")]
    [Export] public DensityMode DensityControlMode = DensityMode.Preset;
    [Export] public MapDensityPreset DensityPreset = MapDensityPreset.Standard;

    // Manual density controls (used if DensityControlMode is set to Manual)

    [ExportSubgroup("Manual Terrain Settings")]
    [Export(PropertyHint.Range, "0,1,0.05")] public float TerrainDensity = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float TerrainRoughness = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float ObstacleDensity = 0.4f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float HeightVariation = 0.5f;

    [ExportGroup("Tile Settings")]
    [Export] public PackedScene HexTileScene3D;
    [Export] public PackedScene RockObstacleScene;
    [Export] public PackedScene CrystalObstacleScene;
    [Export] public Node3D ObstacleParent;

    [ExportGroup("Blended Terrain Mesh")]
    [Export] public bool UseBlendedTerrainMesh = true;
    [Export(PropertyHint.Range, "0.55,0.9,0.01")] public float TerrainSolidFactor = 0.75f;
    [Export(PropertyHint.Range, "0,4,1")] public int TerrainTerraceSteps = 0;
    /// <summary>World-unit height of the rolling micro-relief on tile tops. 0 = flat (old behaviour). Start ~0.15.</summary>
    [Export(PropertyHint.Range, "0,0.6,0.01")] public float TerrainNoiseAmplitude = 0.15f;

    /// <summary>Spatial frequency of the top-surface noise. Higher = tighter, busier bumps. Start ~0.6.</summary>
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float TerrainNoiseFrequency = 0.6f;

    /// <summary>Inner-hex subdivision. 1 = old flat fan, 3-4 = rolling relief. Higher = smoother bumps but more verts per tile.</summary>
    [Export(PropertyHint.Range, "1,6,1")] public int TerrainNoiseSubdiv = 4;

    /// <summary>
    /// Height-step difference above which an edge renders as a sheer cliff
    /// AND (when BlockMovementAtCliffs) movement across it is blocked. One number
    /// drives both, so the visual and the rules can never disagree.
    /// </summary>
    [Export(PropertyHint.Range, "1,5,1")] public int CliffHeightThreshold = 2;

    /// <summary>
    /// Block unit movement across cliff edges. Leave on — rendering
    /// impassable-looking cliffs that units can walk up is a readability lie.
    /// </summary>
    [Export] public bool BlockMovementAtCliffs = true;

    private float _lastWorldFloor = -1.0f;

    [ExportGroup("Terrain Textures")]
    /// <summary>Use the splat shader with per-terrain textures. Off = vertex-colour blending (Route A look).</summary>
    [Export] public bool UseTerrainTextures = true;
    [Export] public Texture2D GrassTexture;
    [Export] public Texture2D GrassNormal;
    [Export] public Texture2D ForestTexture;
    [Export] public Texture2D ForestNormal;
    [Export] public Texture2D StoneTexture;
    [Export] public Texture2D StoneNormal;
    [Export] public Texture2D WaterTexture;
    [Export] public Texture2D WaterNormal;
    [Export] public Texture2D IceTexture;
    [Export] public Texture2D IceNormal;
    [Export] public Texture2D LavaTexture;
    [Export] public Texture2D LavaNormal;
    [Export] public Texture2D ArcaneTexture;
    [Export] public Texture2D ArcaneNormal;
    /// <summary>All source textures are normalised to this square size when packed.</summary>
    [Export] public int TerrainTextureSize = 512;
    /// <summary>World units → UV. Lower = bigger texture features.</summary>
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float TerrainTextureScale = 0.22f;

    /// <summary>Optional authored terrain material. When set, it's used as the splat
    /// template (so pastel/ground-detail uniforms are editable + persistent in a .tres)
    /// instead of the code-built one. Its terrain_textures/normals/scale are still set
    /// from the exports below, so you don't hand-wire the texture arrays.</summary>
    [Export] public ShaderMaterial TerrainMaterialOverride;

    private ShaderMaterial _terrainMaterialTemplate;

    [ExportSubgroup("Per-Terrain Noise")]
    [Export] public float GrassNoiseAmp = 0.18f;
    [Export] public float GrassNoiseFreq = 0.55f;

    [Export] public float ForestNoiseAmp = 0.22f;
    [Export] public float ForestNoiseFreq = 0.7f;

    [Export] public float StoneNoiseAmp = 0.34f;
    [Export] public float StoneNoiseFreq = 0.40f;

    [Export] public float WaterNoiseAmp = 0.03f;
    [Export] public float WaterNoiseFreq = 0.4f;

    [Export] public float IceNoiseAmp = 0.06f;
    [Export] public float IceNoiseFreq = 0.5f;

    [Export] public float LavaNoiseAmp = 0.15f;
    [Export] public float LavaNoiseFreq = 0.9f;

    [Export] public float ArcaneNoiseAmp = 0.20f;
    [Export] public float ArcaneNoiseFreq = 0.30f;

    // Prop import
    [ExportGroup("Tile Props")]
    [Export] public PackedScene GrassTuftScene;
    [Export] public PackedScene GrassTuftSceneAlt;
    [Export] public Node3D PropParent;

    // Theme atmosphere (all optional; null = no-op, zero regression)
    [ExportGroup("Theme Atmosphere")]
    [Export] public DirectionalLight3D ThemeSun;
    [Export] public WorldEnvironment ThemeWorldEnvironment;
    [Export] public PackedScene LandmarkScene;

    // Runtime data structures
    public List<SpawnZone> SpawnZones { get; private set; } = new();
    public List<SpawnSlot> SpawnSlots { get; private set; } = new();
    public Vector3 GridBoundsMin { get; private set; }
    public Vector3 GridBoundsMax { get; private set; }
    private readonly HashSet<Vector2I> ReservedTiles = new();

    public readonly Dictionary<Vector2I, TileData> Tiles = new();

    // Seeded RNG for all generation randomness. Initialised at the top of GenerateMap.
    private RandomNumberGenerator _rng = new();

    private Vector2I _centerCoord;
    private float _layoutMinX;
    private float _layoutMaxX;

    // Enums

    public enum MapTheme
    {
        ArcaneMeadow,
        FrozenBasin,
        VolcanicScar,
        OvergrownRuins,
        VerdantWoods,
        Wetlands,
        HighlandCrags,
        RiverValley,
        Heathland,
        CoastalShallows
    }

    public enum MapLayoutType
    {
        CentralClash,
        SplitLanes,
        RingCourtyard,
        OpenField,
        Chokepoint,
        HighGround,
        ScatteredCover
    }

    public enum SpawnSide
    {
        Player,
        Enemy,
        Neutral
    }

    public class SpawnSlot
    {
        public Vector2I Coord;
        public SpawnSide Side;
        public int TeamId;
        public bool IsOccupied;
    }

    public class SpawnZone
    {
        public SpawnSide Side;
        public int TeamId;
        public Vector2I Anchor;
        public List<Vector2I> Tiles = new();
    }

    public enum DensityMode
    {
        Preset,
        Manual
    }

    public enum MapDensityPreset
    {
        Sparse,
        Standard,
        Dense,
        Wild
    }

    // Structures

    public override void _Ready()
    {
        // Generation is normally driven by CombatManager, which sets the recipe /
        // density / seed first, then calls GenerateMap(). As a fallback for opening
        // a grid-only scene on its own, self-generate after the frame settles — but
        // only if nothing has already generated the grid.
        CallDeferred(nameof(AutoGenerateIfEmpty));
    }

    private void AutoGenerateIfEmpty()
    {
        if (Tiles.Count > 0)
            return;

        GenerateMap();
    }

    public TileData GetTile(Vector2I axial) =>
        Tiles.TryGetValue(axial, out var t) ? t : null;

    public HexTile GetTileView(Vector2I axial) =>
        Tiles.TryGetValue(axial, out var t) ? t.TileView : null;

    public Vector3 AxialToWorld(Vector2I coord)
    {
        int q = coord.X;
        int r = coord.Y;

        float x = HexRadius * 1.5f * q;
        float z = HexRadius * Mathf.Sqrt(3f) * (r + q / 2f);

        return new Vector3(x, 0f, z);
    }

    private static readonly Vector2I[] HexDirs =
    {
        new Vector2I(1, 0),
        new Vector2I(1, -1),
        new Vector2I(0, -1),
        new Vector2I(-1, 0),
        new Vector2I(-1, 1),
        new Vector2I(0, 1)
    };

    public List<Vector2I> GetNeighbors(Vector2I coord)
    {
        var result = new List<Vector2I>();

        foreach (var dir in HexDirs)
        {
            var next = coord + dir;
            if (Tiles.ContainsKey(next))
                result.Add(next);
        }

        return result;
    }

    private Vector2I GetRandomCoord()
    {
        var keys = new List<Vector2I>(Tiles.Keys);
        if (keys.Count == 0)
            return Vector2I.Zero;

        return keys[_rng.RandiRange(0, keys.Count - 1)];
    }

    private Vector2I GetRandomNearbyCoord(Vector2I center, int radius)
    {
        var candidates = new List<Vector2I>();

        foreach (var coord in Tiles.Keys)
        {
            if (Distance(center, coord) <= radius && !IsReserved(coord))
                candidates.Add(coord);
        }

        if (candidates.Count == 0)
            return center;

        return candidates[_rng.RandiRange(0, candidates.Count - 1)];
    }

    private Vector2I GetMidpoint(Vector2I a, Vector2I b)
    {
        return new Vector2I((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }

    public int Distance(Vector2I a, Vector2I b)
    {
        int ax = a.X, az = a.Y, ay = -ax - az;
        int bx = b.X, bz = b.Y, by = -bx - bz;

        return (Math.Abs(ax - bx) + Math.Abs(ay - by) + Math.Abs(az - bz)) / 2;
    }

    public int Distance(HexTile a, HexTile b) => Distance(a.Axial, b.Axial);

    public int Distance(TileData a, TileData b) => Distance(a.Axial, b.Axial);

    private void CenterCameraOverGrid()
    {
        var controller = GetNodeOrNull<CameraController>("../CameraController");
        if (controller == null)
        {
            GD.PrintErr("CameraController not found at ../CameraController");
            return;
        }

        controller.FrameGrid(GridBoundsMin, GridBoundsMax);

        Vector3 center = (GridBoundsMin + GridBoundsMax) * 0.5f;
        GD.Print($"Grid center: {center}");
    }

    // Map Generation

    public void GenerateMap()
    {
        InitRng();
        ResolveRecipe();          // may override Shape/size from the recipe; null = enum path
        GenerateBaseGrid();
        ClearReservedTiles();

        if (RandomizeLayout && _activeRecipe == null)
        {
            var values = Enum.GetValues<MapLayoutType>();
            LayoutType = values[_rng.RandiRange(0, values.Length - 1)];
        }

        ApplyDensityPreset();
        DetermineLayoutAnchors();

        MapField field = _activeRecipe != null ? BuildFieldFromRecipe(_activeRecipe) : BuildField();
        ApplyFieldTerrainAndHeight(field);

        // Skeleton phase (pre-spawn): recipe skeleton features, or the enum layout.
        if (_activeRecipe != null)
            RunRecipeFeatures(_activeRecipe, "skeleton");
        else
            GenerateLayoutSkeleton();

        GenerateSpawnPlan();

        // Accent phase (post-spawn): recipe accent features, or enum theme + landmark.
        if (_activeRecipe != null)
        {
            RunRecipeFeatures(_activeRecipe, "accent");
        }
        else
        {
            ApplyThemeToLayout();
            PlaceThemeLandmark();
        }

        EnsureReservedTilesArePlayable();
        EnsureConnectivityBetweenSpawns();

        ApplyTileHeights();
        ApplyTileVisuals();

        if (_activeRecipe?.Atmosphere != null)
            ApplyRecipeAtmosphere(_activeRecipe.Atmosphere);
        else
            ApplyThemeAtmosphere();

        SpawnObstacleVisuals();
        //SpawnTerrainPropsFromManifest();
        SpawnPainterlyGrass();
        SpawnFlowerProps();
        SpawnRockProps();
        RefreshAllTileLabels();

        RecomputeGridBounds();
        CenterCameraOverGrid();
    }

    private void InitRng()
    {
        _rng = new RandomNumberGenerator();

        if (MapSeed != 0)
        {
            _rng.Seed = (ulong)MapSeed;
        }
        else
        {
            _rng.Randomize();
            MapSeed = (int)_rng.Randi(); // record the chosen seed so the map is reproducible
        }
    }

    /// <summary>Recomputes GridBoundsMin/Max from actual tile-top positions,
    /// INCLUDING height. Must run after ApplyTileHeights — GenerateBaseGrid's
    /// initial bounds are captured before heights exist and sit at Y = 0.</summary>
    private void RecomputeGridBounds()
    {
        bool first = true;
        Vector3 min = Vector3.Zero, max = Vector3.Zero;

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null)
                continue;

            var p = tile.TileView.GlobalPosition; // tile origin = top surface
            if (first)
            {
                min = p;
                max = p;
                first = false;
            }
            else
            {
                min = new Vector3(Mathf.Min(min.X, p.X), Mathf.Min(min.Y, p.Y), Mathf.Min(min.Z, p.Z));
                max = new Vector3(Mathf.Max(max.X, p.X), Mathf.Max(max.Y, p.Y), Mathf.Max(max.Z, p.Z));
            }
        }

        // Pad XZ by one hex so border tiles sit fully inside the framed volume.
        min -= new Vector3(HexRadius, 0f, HexRadius);
        max += new Vector3(HexRadius, 0f, HexRadius);

        GridBoundsMin = min;
        GridBoundsMax = max;
    }

    private Vector2I NearestTileTo(Vector3 target)
    {
        Vector2I best = Vector2I.Zero;
        float bestDist = float.MaxValue;
        bool found = false;

        foreach (var c in Tiles.Keys)
        {
            float d = (AxialToWorld(c) - target).LengthSquared();
            if (!found || d < bestDist)
            {
                bestDist = d;
                best = c;
                found = true;
            }
        }

        return best;
    }

    private Vector2I GetRandomCentralCoord()
    {
        var candidates = new List<Vector2I>();

        foreach (var coord in Tiles.Keys)
        {
            if (Distance(coord, _centerCoord) <= 3)
                candidates.Add(coord);
        }

        if (candidates.Count == 0)
            return GetRandomCoord();

        return candidates[_rng.RandiRange(0, candidates.Count - 1)];
    }

    // Terrain helpers

    public List<Vector2I> GetNeighborCoords(Vector2I coord)
    {
        // Axial hex directions
        var dirs = new Vector2I[]
        {
            new(1, 0), new(1, -1), new(0, -1),
            new(-1, 0), new(-1, 1), new(0, 1)
        };

        var result = new List<Vector2I>();
        foreach (var d in dirs)
            result.Add(coord + d);
        return result;
    }
}
