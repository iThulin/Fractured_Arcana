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
//                 every IEffect that touches tiles
// See:            README §3 — combat hex grid is the spatial
//                 substrate for every other combat system
// ============================================================

/// <summary>3D combat hex grid manager. Generates the grid procedurally, exposes axial-coord helpers (GetTile, GetNeighbors, Distance), and drives the highlight state machine across every <see cref="HexTile"/>. Each Tile is paired 1:1 with a <see cref="TileData"/> that holds the game-state side of the grid.</summary>
public partial class HexGridManager : Node3D
{


    [ExportGroup("Grid Generation")]
    [Export] public int GridWidth = 7;
    [Export] public int GridHeight = 6;
    [Export] public float HexRadius = 1f;

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

    // Tile Materials

    [ExportGroup("Tile Materials")]
    [Export] public Material GrassMaterial;
    [Export] public Material ForestMaterial;
    [Export] public Material StoneMaterial;
    [Export] public Material WaterMaterial;
    [Export] public Material ArcaneMaterial;
    [Export] public Material IceMaterial;
    [Export] public Material LavaMaterial;

    // Prop import
    [ExportGroup("Tile Props")]
    [Export] public PackedScene GrassTuftScene;
    [Export] public PackedScene GrassTuftSceneAlt;
    [Export] public Node3D PropParent;

    // Runtime data structures
    public List<SpawnZone> SpawnZones { get; private set; } = new();
    public List<SpawnSlot> SpawnSlots { get; private set; } = new();
    public Vector3 GridBoundsMin { get; private set; }
    public Vector3 GridBoundsMax { get; private set; }
    private readonly HashSet<Vector2I> ReservedTiles = new();

    public readonly Dictionary<Vector2I, TileData> Tiles = new();

    // Enums

    public enum MapTheme
    {
        ArcaneMeadow,
        FrozenBasin,
        VolcanicScar,
        OvergrownRuins
    }

    public enum MapLayoutType
    {
        CentralClash,
        SplitLanes,
        RingCourtyard
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
        GenerateMap();
        CallDeferred(nameof(CenterCameraOverGrid));
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

        return keys[(int)(GD.Randi() % (uint)keys.Count)];
    }

    private Vector2I GetRandomCentralCoord()
    {
        var candidates = new List<Vector2I>();
        Vector2I approxCenter = new Vector2I(GridWidth / 2, GridHeight / 2);

        foreach (var coord in Tiles.Keys)
        {
            if (Distance(coord, approxCenter) <= 3)
                candidates.Add(coord);
        }

        if (candidates.Count == 0)
            return GetRandomCoord();

        return candidates[(int)(GD.Randi() % (uint)candidates.Count)];
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

        return candidates[(int)(GD.Randi() % (uint)candidates.Count)];
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
        //Create base grid and data structures
        GenerateBaseGrid();
        ClearReservedTiles();

        if (RandomizeLayout)
        {
            var values = Enum.GetValues<MapLayoutType>();
            LayoutType = values[(int)(GD.Randi() % (uint)values.Length)];
        }

        // Build layout skeleton and spawn zones
        ApplyDensityPreset();
        DetermineLayoutAnchors();
        ApplyThemeBaseTerrain();
        GenerateLayoutSkeleton();
        GenerateSpawnPlan();
        ApplyThemeToLayout();

        // Add height variation and smooth it out to create a more natural look
        AddTerrainHeightVariation();
        SmoothTileHeights();

        EnsureReservedTilesArePlayable();
        EnsureConnectivityBetweenSpawns();

        // Apply visuals after all generation steps are done to minimize redundant updates
        ApplyTileHeights();
        ApplyTileVisuals();

        // Add props and obstacles after visuals so they appear on top of the tiles
        SpawnObstacleVisuals();
        SpawnTerrainProps();
        RefreshAllTileLabels();

    }

    private void GenerateBaseGrid()
    {
        Tiles.Clear();

        bool first = true;
        Vector3 min = Vector3.Zero;
        Vector3 max = Vector3.Zero;

        for (int q = 0; q < GridWidth; q++)
        {
            for (int r = 0; r < GridHeight; r++)
            {
                var coord = new Vector2I(q, r);
                var worldPos = AxialToWorld(coord);

                var tileNode = HexTileScene3D.Instantiate<HexTile>();
                tileNode.Position = worldPos;
                tileNode.Axial = coord;
                AddChild(tileNode);

                tileNode.SetCoordinatesLabel(q, r);

                var tileData = new TileData
                {
                    Axial = coord,
                    TileView = tileNode,
                    IsWalkable = true,
                    IsBlocked = false,
                    //ElementId = 0
                };

                Tiles[coord] = tileData;
                tileNode.RefreshLabel(tileData);

                var p = tileNode.GlobalPosition;
                if (first)
                {
                    min = p;
                    max = p;
                    first = false;
                }
                else
                {
                    min = new Vector3(Mathf.Min(min.X, p.X), 0, Mathf.Min(min.Z, p.Z));
                    max = new Vector3(Mathf.Max(max.X, p.X), 0, Mathf.Max(max.Z, p.Z));
                }
            }
        }

        GridBoundsMin = min;
        GridBoundsMax = max;
    }

    private void GenerateLayoutSkeleton()
    {
        switch (LayoutType)
        {
            case MapLayoutType.CentralClash:
                GenerateCentralClashLayout();
                break;

            case MapLayoutType.SplitLanes:
                GenerateSplitLanesLayout();
                break;

            case MapLayoutType.RingCourtyard:
                GenerateRingCourtyardLayout();
                break;
        }
    }

    private void ApplyTileHeights()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.TileView?.SetHeight(tile.Height);
        }
    }

    private void AddTerrainHeightVariation()
    {
        foreach (var tile in Tiles.Values)
        {
            if (IsReserved(tile.Axial))
                continue;

            float chanceMultiplier = Mathf.Lerp(0.3f, 1.5f, HeightVariation);
            int heightStep = HeightVariation < 0.35f ? 1 : 2;

            switch (tile.TerrainType)
            {
                case TileTerrainType.Grass:
                    if (GD.Randf() < 0.20f * chanceMultiplier)
                        tile.Height += 1;
                    break;

                case TileTerrainType.Forest:
                    if (GD.Randf() < 0.30f * chanceMultiplier)
                        tile.Height += heightStep;
                    break;

                case TileTerrainType.Stone:
                    if (GD.Randf() < 0.35f * chanceMultiplier)
                        tile.Height += heightStep;
                    break;

                case TileTerrainType.Water:
                    if (GD.Randf() < 0.35f * chanceMultiplier)
                        tile.Height -= heightStep;
                    break;

                case TileTerrainType.Lava:
                    if (GD.Randf() < 0.25f * chanceMultiplier)
                        tile.Height -= heightStep;
                    break;

                case TileTerrainType.Ice:
                    if (GD.Randf() < 0.20f * chanceMultiplier)
                        tile.Height += 1;
                    break;

                case TileTerrainType.Arcane:
                    if (GD.Randf() < 0.25f * chanceMultiplier)
                        tile.Height += heightStep;
                    break;
            }
        }
    }

    private void SmoothTileHeights()
    {
        var newHeights = new Dictionary<Vector2I, int>();

        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            int total = tile.Height;
            int count = 1;

            foreach (var neighbor in GetNeighbors(coord))
            {
                if (Tiles.TryGetValue(neighbor, out var n))
                {
                    total += n.Height;
                    count++;
                }
            }

            newHeights[coord] = Mathf.RoundToInt(total / (float)count);
        }

        foreach (var kvp in newHeights)
        {
            Tiles[kvp.Key].Height = kvp.Value;
        }
    }

    private void ResetTileHeights()
    {
        foreach (var tile in Tiles.Values)
            tile.Height = 0;
    }

    private void ResetTileStateForGeneration()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.TerrainType = TileTerrainType.Grass;
            tile.ElementType = TileElementType.None;
            tile.ElementStrength = 0f;
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.BlocksLineOfSight = false;
            tile.IsHazardous = false;
            tile.MoveCost = 1;
            tile.ObstacleKind = "";
            tile.Height = 0;
        }
    }

    private void ClearObstacleVisuals()
    {
        Node parent = ObstacleParent ?? this;

        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup("generated_obstacle"))
                child.QueueFree();
        }
    }

    private void GenerateSpawnPlan()
    {
        SpawnZones.Clear();

        Vector2I playerAnchor = FindSpawnAnchor(SpawnSide.Player);
        Vector2I enemyAnchor = FindSpawnAnchor(SpawnSide.Enemy);

        GD.Print($"[SpawnPlan] Player anchor: {playerAnchor}, Enemy anchor: {enemyAnchor}");

        var playerZone = BuildSpawnZone(playerAnchor, SpawnSide.Player, 0, PlayerSpawnCount);
        var enemyZone = BuildSpawnZone(enemyAnchor, SpawnSide.Enemy, 1, EnemySpawnCount);

        GD.Print($"[SpawnPlan] Player zone tiles: {playerZone.Tiles.Count}, Enemy zone tiles: {enemyZone.Tiles.Count}");

        SpawnZones.Add(playerZone);
        SpawnZones.Add(enemyZone);

        ReserveSpawnZones();
        BuildSpawnSlotsFromZones();

        GD.Print($"[SpawnPlan] Total spawn slots: {SpawnSlots.Count}");
    }

    private void DetermineLayoutAnchors()
    {
        PlayerLayoutAnchor = new Vector2I(1, GridHeight / 2);
        EnemyLayoutAnchor = new Vector2I(GridWidth - 2, GridHeight / 2);

        switch (LayoutType)
        {
            case MapLayoutType.CentralClash:
                PlayerLayoutAnchor = new Vector2I(1, GridHeight / 2);
                EnemyLayoutAnchor = new Vector2I(GridWidth - 2, GridHeight / 2);
                break;

            case MapLayoutType.SplitLanes:
                PlayerLayoutAnchor = new Vector2I(1, GridHeight / 2);
                EnemyLayoutAnchor = new Vector2I(GridWidth - 2, GridHeight / 2);
                break;

            case MapLayoutType.RingCourtyard:
                PlayerLayoutAnchor = new Vector2I(1, GridHeight / 2);
                EnemyLayoutAnchor = new Vector2I(GridWidth - 2, GridHeight / 2);
                break;
        }
    }

    // Tile Visuals

    private void SpawnObstacleVisuals()
    {
        ClearObstacleVisuals();

        foreach (var kvp in Tiles)
        {
            TileData tile = kvp.Value;

            if (string.IsNullOrEmpty(tile.ObstacleKind))
                continue;

            PackedScene scene = null;

            switch (tile.ObstacleKind)
            {
                case "rock":
                    scene = RockObstacleScene;
                    break;
                case "crystal":
                    scene = CrystalObstacleScene;
                    break;
            }

            if (scene == null || tile.TileView == null)
                continue;

            var obstacle = scene.Instantiate<Node3D>();

            if (ObstacleParent != null)
            {
                ObstacleParent.AddChild(obstacle);
                obstacle.GlobalPosition = tile.TileView.GlobalPosition + new Vector3(0f, 0.5f, 0f);
            }
            else
            {
                AddChild(obstacle);
                obstacle.Position = tile.TileView.Position + new Vector3(0f, 0.5f, 0f);
            }

            obstacle.AddToGroup("generated_obstacle");
        }
    }

    public void ApplyVisualToTile(TileData tile)
    {
        if (tile.TileView == null)
            return;

        Material terrainMaterial = null;
        Color color = Colors.White;

        color = tile.TerrainType switch
        {
            TileTerrainType.Grass => UITheme.CombatTileGrass,
            TileTerrainType.Forest => UITheme.CombatTileForest,
            TileTerrainType.Stone => UITheme.CombatTileStone,
            TileTerrainType.Water => UITheme.CombatTileWater,
            TileTerrainType.Lava => UITheme.CombatTileLava,
            TileTerrainType.Arcane => UITheme.CombatTileArcane,
            TileTerrainType.Ice => UITheme.CombatTileIce,
            _ => Colors.White
        };

        if (terrainMaterial != null)
            tile.TileView.SetMaterial(terrainMaterial);

        bool inPlayerSpawn = IsTileInSpawnSide(tile.Axial, SpawnSide.Player);
        bool inEnemySpawn = IsTileInSpawnSide(tile.Axial, SpawnSide.Enemy);

        if (inPlayerSpawn)
            color = color.Lerp(UITheme.SpawnTintPlayer, UITheme.SpawnTintStrength);

        if (inEnemySpawn)
            color = color.Lerp(UITheme.SpawnTintEnemy, UITheme.SpawnTintStrength);

        tile.TileView.SetBaseColor(color);
        tile.TileView.SetElement(tile.ElementType);
    }

    private void ApplyTileVisuals()
    {
        foreach (var kvp in Tiles)
        {
            TileData tile = kvp.Value;
            if (tile.TileView == null)
                continue;

            ApplyVisualToTile(tile);
        }
    }

    private void SetAllTilesToTerrain(TileTerrainType terrain)
    {
        foreach (var tile in Tiles.Values)
        {
            tile.TerrainType = terrain;

            switch (terrain)
            {
                case TileTerrainType.Grass:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    tile.Height = 0;
                    break;

                case TileTerrainType.Water:
                    tile.IsWalkable = false;
                    tile.IsBlocked = false;
                    tile.MoveCost = 999;
                    tile.Height = Math.Min(tile.Height, -1);
                    break;

                case TileTerrainType.Forest:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 2;
                    tile.Height = Math.Max(tile.Height, 1);
                    break;

                case TileTerrainType.Stone:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    tile.Height = Math.Max(tile.Height, 1);
                    break;

                case TileTerrainType.Ice:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                case TileTerrainType.Lava:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 2;
                    tile.IsHazardous = true;
                    tile.Height = Math.Min(tile.Height, -1);
                    break;

                case TileTerrainType.Arcane:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;
            }
        }
    }


    private void RefreshAllTileLabels()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.TileView?.RefreshLabel(tile);
        }
    }

    private void ApplyThemeToLayout()
    {
        switch (Theme)
        {
            case MapTheme.ArcaneMeadow:
                ApplyArcaneMeadowTheme();
                break;

            case MapTheme.FrozenBasin:
                ApplyFrozenBasinTheme();
                break;

            case MapTheme.VolcanicScar:
                ApplyVolcanicScarTheme();
                break;

            case MapTheme.OvergrownRuins:
                ApplyOvergrownRuinsTheme();
                break;
        }
    }

    // Tile Props

    private void SpawnTerrainProps()
    {
        ClearTerrainProps();

        foreach (var tile in Tiles.Values)
        {
            if (tile.TileView == null)
                continue;

            if (tile.IsBlocked)
                continue;

            if (tile.TerrainType == TileTerrainType.Grass)
            {
                SpawnGrassOnTile(tile, 0.65f, 1, 3);
            }
            else if (tile.TerrainType == TileTerrainType.Forest)
            {
                SpawnGrassOnTile(tile, 0.9f, 2, 4);
            }
        }
    }

    private void ClearTerrainProps()
    {
        Node parent = PropParent ?? this;

        foreach (Node child in parent.GetChildren())
        {
            if (child.IsInGroup("generated_prop"))
                child.QueueFree();
        }
    }

    private void SpawnGrassOnTile(TileData tile, float spawnChance, int minCount, int maxCount)
    {
        if (GD.Randf() > spawnChance)
            return;

        int count = minCount + (int)(GD.Randi() % (uint)(maxCount - minCount + 1));

        for (int i = 0; i < count; i++)
        {
            PackedScene scene = GrassTuftScene;

            if (GrassTuftSceneAlt != null && GD.Randf() < 0.35f)
                scene = GrassTuftSceneAlt;

            if (scene == null)
                continue;

            var tuft = scene.Instantiate<Node3D>();

            Node parent = PropParent ?? this;
            parent.AddChild(tuft);

            Vector3 basePos = tile.TileView.GlobalPosition;

            float xOffset = (float)GD.RandRange(-0.35f, 0.35f);
            float zOffset = (float)GD.RandRange(-0.35f, 0.35f);

            tuft.GlobalPosition = basePos + new Vector3(xOffset, 0.05f, zOffset);

            Vector3 rot = tuft.RotationDegrees;
            rot.Y = (float)GD.RandRange(0f, 360f);
            tuft.RotationDegrees = rot;

            float scale = (float)GD.RandRange(0.85f, 1.2f);
            tuft.Scale = new Vector3(scale, scale, scale);

            tuft.AddToGroup("generated_prop");
        }
    }


    // Paint Terrain and Features

    private void PaintTerrainPatch(Vector2I center, TileTerrainType terrain, int radius, float edgeChance = 0.75f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            bool apply = true;

            if (dist == radius)
                apply = GD.Randf() < edgeChance;

            if (!apply)
                continue;

            tile.TerrainType = terrain;

            switch (terrain)
            {
                case TileTerrainType.Water:
                    tile.IsWalkable = false;
                    tile.IsBlocked = false;
                    tile.MoveCost = 999;
                    break;

                case TileTerrainType.Forest:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 2;
                    break;

                case TileTerrainType.Stone:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                case TileTerrainType.Ice:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                case TileTerrainType.Lava:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 2;
                    tile.IsHazardous = true;
                    break;

                case TileTerrainType.Arcane:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;

                default:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;
            }
        }
    }

    private void PaintElementPatch(Vector2I center, TileElementType element, int radius, float strength = 1.0f, float edgeChance = 0.75f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            bool apply = true;

            if (dist == radius)
                apply = GD.Randf() < edgeChance;

            if (!apply)
                continue;

            tile.ElementType = element;
            tile.ElementStrength = Mathf.Clamp(strength - (dist * 0.2f), 0.2f, 1.0f);

            if (element == TileElementType.Fire)
                tile.IsHazardous = true;
        }
    }

    private void PaintHeightPatch(Vector2I center, int radius, int peakHeight)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            int height = Math.Max(0, peakHeight - dist);

            tile.Height = Math.Max(tile.Height, height);
        }
    }

    private void PaintHeightHill(Vector2I center, int radius, int peakHeight)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            int appliedHeight = Math.Max(0, peakHeight - dist);
            tile.Height = Math.Max(tile.Height, appliedHeight);
        }
    }

    private void PaintHeightBasin(Vector2I center, int radius, int depth)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            int depression = Math.Max(0, depth - dist);
            tile.Height -= depression;
        }
    }

    private void PaintHeightRidge(Vector2I start, Vector2I direction, int length, int ridgeHeight)
    {
        Vector2I current = start;

        for (int i = 0; i < length; i++)
        {
            if (!Tiles.TryGetValue(current, out var tile))
                break;

            if (!IsReserved(current))
                tile.Height = Math.Max(tile.Height, ridgeHeight);

            foreach (var neighbor in GetNeighbors(current))
            {
                if (!Tiles.TryGetValue(neighbor, out var neighborTile))
                    continue;

                if (IsReserved(neighbor))
                    continue;

                neighborTile.Height = Math.Max(neighborTile.Height, ridgeHeight - 1);
            }

            current += direction;
        }
    }

    private void PaintLinearFeature(Vector2I start, Vector2I direction, int length, Action<TileData> applyToTile, float branchChance = 0.0f)
    {
        Vector2I current = start;

        for (int i = 0; i < length; i++)
        {
            if (Tiles.TryGetValue(current, out var tile) && !IsReserved(current))
            {
                applyToTile(tile);
            }

            if (GD.Randf() < branchChance)
            {
                var neighbors = GetNeighbors(current);
                if (neighbors.Count > 0)
                {
                    var branch = neighbors[(int)(GD.Randi() % (uint)neighbors.Count)];
                    if (Tiles.TryGetValue(branch, out var branchTile) && !IsReserved(branch))
                        applyToTile(branchTile);
                }
            }

            current += direction;

            if (!Tiles.ContainsKey(current))
                break;
        }
    }

    private void PaintRingFeature(Vector2I center, int radius, Action<TileData> applyToTile, float edgeChance = 1.0f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist != radius)
                continue;

            if (GD.Randf() > edgeChance)
                continue;

            applyToTile(tile);
        }
    }

    private void PaintFilledRadius(Vector2I center, int radius, Action<TileData> applyToTile, float edgeChance = 1.0f)
    {
        foreach (var kvp in Tiles)
        {
            Vector2I coord = kvp.Key;
            TileData tile = kvp.Value;

            if (IsReserved(coord))
                continue;

            int dist = Distance(center, coord);
            if (dist > radius)
                continue;

            if (dist == radius && GD.Randf() > edgeChance)
                continue;

            applyToTile(tile);
        }
    }

    private void PaintObstacleBand(Vector2I start, Vector2I direction, int length, string obstacleKind, float chance = 0.7f)
    {
        Vector2I current = start;

        for (int i = 0; i < length; i++)
        {
            if (!Tiles.TryGetValue(current, out var tile))
                break;

            if (!IsReserved(current) && GD.Randf() < chance)
            {
                tile.IsBlocked = true;
                tile.IsWalkable = false;
                tile.BlocksLineOfSight = true;
                tile.ObstacleKind = obstacleKind;
            }

            current += direction;
        }
    }

    private void PaintObstacleCluster(Vector2I start, string obstacleKind, int targetSize)
    {
        if (!Tiles.TryGetValue(start, out var startTile))
            return;

        if (startTile.TerrainType == TileTerrainType.Water)
            return;

        if (IsReserved(start))
            return;

        var frontier = new List<Vector2I> { start };
        var visited = new HashSet<Vector2I> { start };

        int placed = 0;

        while (frontier.Count > 0 && placed < targetSize)
        {
            int index = (int)(GD.Randi() % (uint)frontier.Count);
            Vector2I current = frontier[index];
            frontier.RemoveAt(index);

            if (!Tiles.TryGetValue(current, out var tile))
                continue;

            if (IsReserved(current))
                continue;

            if (tile.TerrainType == TileTerrainType.Water)
                continue;

            if (tile.IsOccupied)
                continue;

            tile.IsBlocked = true;
            tile.IsWalkable = false;
            tile.BlocksLineOfSight = true;
            tile.ObstacleKind = obstacleKind;
            placed++;

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                visited.Add(neighbor);

                if (GD.Randf() < 0.75f)
                    frontier.Add(neighbor);
            }
        }
    }

    // Reservation System

    private void ClearReservedTiles()
    {
        ReservedTiles.Clear();
    }

    private void ReserveTile(Vector2I coord)
    {
        if (Tiles.ContainsKey(coord))
            ReservedTiles.Add(coord);
    }

    private void ReserveRadius(Vector2I center, int radius)
    {
        foreach (var coord in Tiles.Keys)
        {
            if (Distance(center, coord) <= radius)
                ReservedTiles.Add(coord);
        }
    }

    private bool IsReserved(Vector2I coord)
    {
        return ReservedTiles.Contains(coord);
    }

    private void EnsureReservedTilesArePlayable()
    {
        foreach (var coord in ReservedTiles)
        {
            if (!Tiles.TryGetValue(coord, out var tile))
                continue;

            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.BlocksLineOfSight = false;
            tile.IsHazardous = false;
            tile.MoveCost = 1;
            tile.ObstacleKind = "";
        }
    }

    private HashSet<Vector2I> GetReachableTiles(Vector2I start)
    {
        var visited = new HashSet<Vector2I>();

        if (!Tiles.TryGetValue(start, out var startTile))
            return visited;

        if (!startTile.IsWalkable || startTile.IsBlocked)
            return visited;

        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                if (!Tiles.TryGetValue(neighbor, out var tile))
                    continue;

                if (!tile.IsWalkable || tile.IsBlocked)
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return visited;
    }

    private void EnsureConnectivity(Vector2I start, Vector2I goal)
    {
        var reachable = GetReachableTiles(start);
        if (reachable.Contains(goal))
            return;

        GD.Print("No valid path found between spawn points. Carving path...");

        Vector2I current = start;

        while (current != goal)
        {
            if (Tiles.TryGetValue(current, out var tile))
            {
                tile.TerrainType = TileTerrainType.Grass;
                tile.ElementType = TileElementType.None;
                tile.ElementStrength = 0f;
                tile.IsWalkable = true;
                tile.IsBlocked = false;
                tile.BlocksLineOfSight = false;
                tile.IsHazardous = false;
                tile.MoveCost = 1;
                tile.ObstacleKind = "";
            }

            int dq = goal.X - current.X;
            int dr = goal.Y - current.Y;

            Vector2I step = current;

            if (Math.Abs(dq) > Math.Abs(dr))
                step = new Vector2I(current.X + Math.Sign(dq), current.Y);
            else if (dr != 0)
                step = new Vector2I(current.X, current.Y + Math.Sign(dr));

            if (step == current)
                break;

            current = step;
        }

        if (Tiles.TryGetValue(goal, out var goalTile))
        {
            goalTile.ElementStrength = 0f;
            goalTile.IsWalkable = true;
            goalTile.IsBlocked = false;
            goalTile.BlocksLineOfSight = false;
            goalTile.IsHazardous = false;
            goalTile.MoveCost = 1;
            goalTile.ObstacleKind = "";
        }
    }

    // Player and Enemy Spawns

    private List<Vector2I> GetSideCandidates(SpawnSide side)
    {
        var result = new List<Vector2I>();
        Vector2I anchor = side == SpawnSide.Player ? PlayerLayoutAnchor : EnemyLayoutAnchor;

        foreach (var coord in Tiles.Keys)
        {
            // Stay roughly on the correct half
            if (side == SpawnSide.Player && coord.X > GridWidth / 2)
                continue;

            if (side == SpawnSide.Enemy && coord.X < GridWidth / 2)
                continue;

            // Prefer tiles near the layout anchor
            if (Distance(coord, anchor) <= 3)
                result.Add(coord);
        }

        return result;
    }

    private Vector2I FindSpawnAnchor(SpawnSide side)
    {
        if (UseDebugSpawnOverrides)
            return side == SpawnSide.Player ? DebugPlayerAnchor : DebugEnemyAnchor;

        Vector2I targetAnchor = side == SpawnSide.Player ? PlayerLayoutAnchor : EnemyLayoutAnchor;
        int requiredSlots = side == SpawnSide.Player ? PlayerSpawnCount : EnemySpawnCount;

        var candidates = GetSideCandidates(side);

        Vector2I bestCoord = Vector2I.Zero;
        int bestScore = int.MinValue;
        bool foundAny = false;

        foreach (var coord in candidates)
        {
            if (!IsValidSpawnTile(coord))
                continue;

            int localCapacity = CountNearbySpawnableTiles(coord, requiredSlots, 3);
            if (localCapacity <= 0)
                continue;

            int distToAnchor = Distance(coord, targetAnchor);

            // higher is better
            int score = 0;

            // prefer being close to layout anchor
            score -= distToAnchor * 10;

            // strongly prefer enough room for whole team
            score += localCapacity * 25;

            // bonus if it fully supports the team
            if (localCapacity >= requiredSlots)
                score += 100;

            if (!foundAny || score > bestScore)
            {
                bestScore = score;
                bestCoord = coord;
                foundAny = true;
            }
        }

        if (foundAny)
            return bestCoord;

        // fallback
        if (candidates.Count > 0)
            return candidates[0];

        // After the existing fallback:
        if (candidates.Count > 0)
            return candidates[0];

        // Nuclear fallback — scan entire correct half for ANY walkable tile
        GD.PrintErr($"[SpawnPlan] No spawn anchor found for {side} — using emergency fallback.");
        foreach (var coord in Tiles.Keys)
        {
            if (side == SpawnSide.Player && coord.X > GridWidth / 2) continue;
            if (side == SpawnSide.Enemy && coord.X < GridWidth / 2) continue;
            if (IsValidSpawnTile(coord)) return coord;
        }

        GD.PrintErr($"[SpawnPlan] CRITICAL: No valid spawn tile found for {side}.");
        return Vector2I.Zero;
    }

    private SpawnZone BuildSpawnZone(Vector2I anchor, SpawnSide side, int teamId, int requiredSlots)
    {
        var zone = new SpawnZone
        {
            Anchor = anchor,
            Side = side,
            TeamId = teamId
        };

        var visited = new HashSet<Vector2I>();
        var queue = new Queue<Vector2I>();

        queue.Enqueue(anchor);
        visited.Add(anchor);

        while (queue.Count > 0 && zone.Tiles.Count < requiredSlots)
        {
            var current = queue.Dequeue();

            if (Tiles.TryGetValue(current, out var tile))
            {
                if (tile.IsWalkable && !tile.IsBlocked)
                    zone.Tiles.Add(current);
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return zone;
    }

    private void BuildSpawnSlotsFromZones()
    {
        SpawnSlots.Clear();

        foreach (var zone in SpawnZones)
        {
            foreach (var coord in zone.Tiles)
            {
                SpawnSlots.Add(new SpawnSlot
                {
                    Coord = coord,
                    Side = zone.Side,
                    TeamId = zone.TeamId,
                    IsOccupied = false
                });
            }
        }
    }

    private void ReserveSpawnZones()
    {
        ClearReservedTiles();

        foreach (var zone in SpawnZones)
        {
            foreach (var coord in zone.Tiles)
                ReservedTiles.Add(coord);
        }
    }

    private void EnsureConnectivityBetweenSpawns()
    {
        if (SpawnZones.Count < 2)
            return;

        var playerZone = SpawnZones.Find(z => z.Side == SpawnSide.Player);
        var enemyZone = SpawnZones.Find(z => z.Side == SpawnSide.Enemy);

        if (playerZone == null || enemyZone == null)
        {
            GD.PrintErr("Missing spawn zones for connectivity.");
            return;
        }

        // Primary connection (anchor → anchor)
        EnsureConnectivity(playerZone.Anchor, enemyZone.Anchor);

        // Optional: reinforce connectivity with extra paths
        if (playerZone.Tiles.Count > 0 && enemyZone.Tiles.Count > 0)
        {
            var p = playerZone.Tiles[(int)(GD.Randi() % (uint)playerZone.Tiles.Count)];
            var e = enemyZone.Tiles[(int)(GD.Randi() % (uint)enemyZone.Tiles.Count)];

            EnsureConnectivity(p, e);
        }
    }

    private bool IsTileInSpawnSide(Vector2I coord, SpawnSide side)
    {
        foreach (var zone in SpawnZones)
        {
            if (zone.Side == side && zone.Tiles.Contains(coord))
                return true;
        }

        return false;
    }

    private bool IsValidSpawnTile(Vector2I coord)
    {
        if (!Tiles.TryGetValue(coord, out var tile))
            return false;

        if (!tile.IsWalkable || tile.IsBlocked)
            return false;

        if (tile.TerrainType == TileTerrainType.Water)
            return false;

        return true;
    }

    private int CountNearbySpawnableTiles(Vector2I start, int maxCount, int maxDistance = 3)
    {
        if (!IsValidSpawnTile(start))
            return 0;

        var visited = new HashSet<Vector2I>();
        var queue = new Queue<Vector2I>();
        int count = 0;

        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0 && count < maxCount)
        {
            var current = queue.Dequeue();

            if (IsValidSpawnTile(current))
                count++;

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor))
                    continue;

                if (Distance(start, neighbor) > maxDistance)
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return count;
    }

    public SpawnSlot ClaimNextSpawnSlot(SpawnSide side)
    {
        foreach (var slot in SpawnSlots)
        {
            if (slot.Side == side && !slot.IsOccupied)
            {
                slot.IsOccupied = true;
                return slot;
            }
        }

        return null;
    }

    public TileData GetTileAtSpawnSlot(SpawnSlot slot)
    {
        if (slot == null)
            return null;

        return GetTile(slot.Coord);
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

    private int GetTerrainPatchCount(int minCount, int maxCount)
    {
        return Mathf.RoundToInt(Mathf.Lerp(minCount, maxCount, TerrainDensity));
    }

    private float GetEdgeChance()
    {
        // Low roughness = smoother edge fill
        // High roughness = more broken edges
        return Mathf.Lerp(0.95f, 0.55f, TerrainRoughness);
    }

    private int GetObstacleClusterCount(int minCount, int maxCount)
    {
        return Mathf.RoundToInt(Mathf.Lerp(minCount, maxCount, ObstacleDensity));
    }

    private int GetObstacleClusterSize(int minSize, int maxSize)
    {
        return Mathf.RoundToInt(Mathf.Lerp(minSize, maxSize, ObstacleDensity));
    }

    private int GetPatchRadius(int minRadius, int maxRadius)
    {
        // Low roughness = larger patches
        // High roughness = smaller patches
        return Mathf.RoundToInt(Mathf.Lerp(maxRadius, minRadius, TerrainRoughness));
    }

    private void CarveLane(Vector2I start, Vector2I goal, int width = 0)
    {
        Vector2I current = start;

        while (current != goal)
        {
            ClearTileForLane(current, width);

            int dq = goal.X - current.X;
            int dr = goal.Y - current.Y;

            Vector2I step = current;

            if (Math.Abs(dq) > Math.Abs(dr))
                step = new Vector2I(current.X + Math.Sign(dq), current.Y);
            else if (dr != 0)
                step = new Vector2I(current.X, current.Y + Math.Sign(dr));

            if (step == current)
                break;

            current = step;
        }

        ClearTileForLane(goal, width);
    }

    private void ClearTileObstacleState(TileData tile)
    {
        tile.IsBlocked = false;
        tile.BlocksLineOfSight = false;
        tile.ObstacleKind = "";
    }

    private void ClearTileForLane(Vector2I center, int width)
    {
        foreach (var coord in Tiles.Keys)
        {
            if (Distance(center, coord) > width)
                continue;

            if (!Tiles.TryGetValue(coord, out var tile))
                continue;

            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.BlocksLineOfSight = false;
            tile.ObstacleKind = "";
            tile.MoveCost = 1;
        }
    }

    private void GenerateBasin()
    {
        Vector2I center = GetRandomCoord();

        foreach (var kvp in Tiles)
        {
            int dist = Distance(center, kvp.Key);
            if (dist <= 2)
            {
                kvp.Value.Height -= (2 - dist);
            }
        }
    }

    private void GenerateHill()
    {
        PaintHeightPatch(GetRandomCoord(), 2, 2);
    }

    private void GenerateRidge()
    {
        Vector2I start = GetRandomCoord();
        Vector2I dir = HexDirs[(int)(GD.Randi() % (uint)HexDirs.Length)];

        Vector2I current = start;

        for (int i = 0; i < 5; i++)
        {
            if (Tiles.TryGetValue(current, out var tile))
            {
                tile.Height += 2;

                foreach (var neighbor in GetNeighbors(current))
                {
                    if (Tiles.TryGetValue(neighbor, out var n))
                        n.Height += 1;
                }
            }

            current += dir;
            if (!Tiles.ContainsKey(current))
                break;
        }
    }

    // Map Skeletons

    private void GenerateCentralClashLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);

        // Raised central hill / contest point
        PaintHeightHill(center, 2, 2);

        // Main open route
        CarveLane(PlayerLayoutAnchor, center, 1);
        CarveLane(EnemyLayoutAnchor, center, 1);

        // Cover near the center, but not full wall
        PaintObstacleCluster(GetRandomNearbyCoord(center, 2), "rock", 3);
        PaintObstacleCluster(GetRandomNearbyCoord(center, 2), "rock", 2);

        // Flank patches
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 1, 0.8f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 1, 0.8f);
    }

    private void GenerateSplitLanesLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);

        // Create a central blocker band to split traffic
        Vector2I dir = HexDirs[2];
        PaintObstacleBand(center, dir, 4, "rock", 0.8f);

        // Carve left and right lanes around it
        CarveLane(PlayerLayoutAnchor, new Vector2I(center.X - 1, center.Y - 1), 1);
        CarveLane(new Vector2I(center.X - 1, center.Y - 1), EnemyLayoutAnchor, 1);

        CarveLane(PlayerLayoutAnchor, new Vector2I(center.X + 1, center.Y + 1), 1);
        CarveLane(new Vector2I(center.X + 1, center.Y + 1), EnemyLayoutAnchor, 1);

        // Add some height on the band
        PaintHeightRidge(center, dir, 4, 2);
    }

    private void GenerateRingCourtyardLayout()
    {
        Vector2I center = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);

        // Central raised courtyard
        PaintFilledRadius(center, 1, tile =>
        {
            tile.TerrainType = TileTerrainType.Grass;
            tile.Height = Math.Max(tile.Height, 1);
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.MoveCost = 1;
        });

        // Outer ring with broken walls
        PaintRingFeature(center, 2, tile =>
        {
            tile.TerrainType = TileTerrainType.Stone;
            tile.Height = Math.Max(tile.Height, 2);

            if (GD.Randf() < 0.65f)
            {
                tile.IsBlocked = true;
                tile.IsWalkable = false;
                tile.BlocksLineOfSight = true;
                tile.ObstacleKind = "rock";
            }
        }, 0.85f);

        // Ensure entrances
        CarveLane(PlayerLayoutAnchor, center, 1);
        CarveLane(EnemyLayoutAnchor, center, 1);
    }

    private void ApplyDensityPreset()
    {
        if (DensityControlMode != DensityMode.Preset)
            return;

        switch (DensityPreset)
        {
            case MapDensityPreset.Sparse:
                TerrainDensity = 0.25f;
                TerrainRoughness = 0.25f;
                ObstacleDensity = 0.2f;
                break;

            case MapDensityPreset.Standard:
                TerrainDensity = 0.5f;
                TerrainRoughness = 0.5f;
                ObstacleDensity = 0.4f;
                break;

            case MapDensityPreset.Dense:
                TerrainDensity = 0.75f;
                TerrainRoughness = 0.6f;
                ObstacleDensity = 0.65f;
                break;

            case MapDensityPreset.Wild:
                TerrainDensity = 0.9f;
                TerrainRoughness = 0.9f;
                ObstacleDensity = 0.75f;
                break;
        }
    }

    // Themes

    private void ApplyThemeBaseTerrain()
    {
        switch (Theme)
        {
            case MapTheme.ArcaneMeadow:
                SetAllTilesToTerrain(TileTerrainType.Grass);
                break;

            case MapTheme.FrozenBasin:
                SetAllTilesToTerrain(TileTerrainType.Ice);
                break;

            case MapTheme.VolcanicScar:
                SetAllTilesToTerrain(TileTerrainType.Stone);
                break;

            case MapTheme.OvergrownRuins:
                SetAllTilesToTerrain(TileTerrainType.Forest);
                break;
        }
    }

    private void ApplyArcaneMeadowTheme()
    {
        int forestPatches = GetTerrainPatchCount(1, 4);
        int waterPatches = GetTerrainPatchCount(0, 2);

        for (int i = 0; i < forestPatches; i++)
            PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, GetPatchRadius(1, 3), GetEdgeChance());

        for (int i = 0; i < waterPatches; i++)
            PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, GetPatchRadius(1, 2), GetEdgeChance());

        PaintElementPatch(GetRandomCentralCoord(), TileElementType.Arcane, GetPatchRadius(1, 2), 1.0f, GetEdgeChance());

        if (GD.Randf() < Mathf.Lerp(0.2f, 0.8f, ObstacleDensity))
            PaintObstacleCluster(GetRandomCentralCoord(), "crystal", GetObstacleClusterSize(2, 4));
    }

    private void ApplyFrozenBasinTheme()
    {
        PaintTerrainPatch(GetRandomCentralCoord(), TileTerrainType.Ice, 3, 0.95f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Ice, 2, 0.85f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, 2, 0.75f);

        PaintElementPatch(GetRandomCentralCoord(), TileElementType.Frost, 2, 1.0f, 0.9f);
        PaintElementPatch(GetRandomCoord(), TileElementType.Frost, 1, 0.7f, 0.75f);
    }

    private void ApplyVolcanicScarTheme()
    {
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 3, 0.9f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 2, 0.85f);

        Vector2I start = GetRandomCentralCoord();
        Vector2I dir = HexDirs[(int)(GD.Randi() % (uint)HexDirs.Length)];

        PaintLinearFeature(start, dir, 5, tile =>
        {
            MakeLava(tile);
            tile.Height -= 1;
        }, 0.2f);

        PaintElementPatch(start, TileElementType.Fire, 2, 1.0f, 0.8f);
    }

    private void ApplyOvergrownRuinsTheme()
    {
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 3, 0.9f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 2, 0.85f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 2, 0.75f);

        if (GD.Randf() < 0.5f)
            PaintElementPatch(GetRandomCentralCoord(), TileElementType.Arcane, 1, 0.8f, 0.7f);
    }

    private void MakeLava(TileData tile)
    {
        ClearTileObstacleState(tile);
        tile.TerrainType = TileTerrainType.Lava;
        tile.IsWalkable = true;
        tile.IsBlocked = false;
        tile.MoveCost = 2;
        tile.IsHazardous = true;
        tile.ElementType = TileElementType.Fire;
        tile.ElementStrength = 1.0f;
    }

    private void MakeArcaneGround(TileData tile)
    {
        ClearTileObstacleState(tile);
        tile.TerrainType = TileTerrainType.Arcane;
        tile.IsWalkable = true;
        tile.IsBlocked = false;
        tile.MoveCost = 1;
        tile.ElementType = TileElementType.Arcane;
        tile.ElementStrength = 1.0f;
    }

    private void MakeIce(TileData tile)
    {
        ClearTileObstacleState(tile);
        tile.TerrainType = TileTerrainType.Ice;
        tile.IsWalkable = true;
        tile.IsBlocked = false;
        tile.MoveCost = 1;
        tile.ElementType = TileElementType.Frost;
        tile.ElementStrength = 1.0f;
    }

    private void MakeRockObstacle(TileData tile)
    {
        tile.IsBlocked = true;
        tile.IsWalkable = false;
        tile.BlocksLineOfSight = true;
        tile.ObstacleKind = "rock";
    }

    private void MakeCrystalObstacle(TileData tile)
    {
        tile.IsBlocked = true;
        tile.IsWalkable = false;
        tile.BlocksLineOfSight = true;
        tile.ObstacleKind = "crystal";
    }

    private void GenerateArcaneMeadowFeature()
    {
        Vector2I center = GetRandomCentralCoord();

        PaintHeightHill(center, 2, 2);

        PaintFilledRadius(center, 2, tile =>
        {
            MakeArcaneGround(tile);
        }, 0.85f);

        PaintRingFeature(center, 2, tile =>
        {
            if (GD.Randf() < 0.4f)
                MakeCrystalObstacle(tile);
        }, 0.7f);
    }

    private void GenerateFrozenBasinFeature()
    {
        Vector2I center = GetRandomCentralCoord();

        PaintHeightBasin(center, 2, 2);

        PaintFilledRadius(center, 2, tile =>
        {
            MakeIce(tile);
        }, 0.9f);

        PaintRingFeature(center, 2, tile =>
        {
            if (GD.Randf() < 0.35f)
                MakeRockObstacle(tile);
        }, 0.75f);
    }

    private void GenerateVolcanicScarFeature()
    {
        Vector2I start = GetRandomCoord();
        Vector2I dir = HexDirs[(int)(GD.Randi() % (uint)HexDirs.Length)];

        PaintHeightRidge(start, dir, 5, 2);

        PaintLinearFeature(start, dir, 5, tile =>
        {
            MakeLava(tile);
            tile.Height -= 1; // cut a lava trench through the ridge
        }, 0.25f);
    }

    private void GenerateOvergrownRuinsFeature()
    {
        Vector2I center = GetRandomCentralCoord();

        PaintRingFeature(center, 2, tile =>
        {
            tile.Height = Math.Max(tile.Height, 2);
            tile.TerrainType = TileTerrainType.Stone;
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.MoveCost = 1;

            if (GD.Randf() < 0.7f)
                MakeRockObstacle(tile);
        }, 0.75f);

        PaintFilledRadius(center, 1, tile =>
        {
            tile.TerrainType = TileTerrainType.Grass;
            tile.IsWalkable = true;
            tile.IsBlocked = false;
            tile.MoveCost = 1;
            tile.Height = Math.Max(tile.Height, 1);

            if (GD.Randf() < 0.5f)
            {
                tile.ElementType = TileElementType.Arcane;
                tile.ElementStrength = 0.8f;
            }
        }, 1.0f);
    }

    // Pathfinding
    public HashSet<Vector2I> GetReachableTiles(Unit unit)
    {
        var result = new HashSet<Vector2I>();
        if (unit == null || unit.CurrentTile == null) return result;

        var start = unit.CurrentTile.Axial;
        int maxMove = unit.Stats.MovePoints;

        var frontier = new PriorityQueue<Vector2I, int>();
        var bestCost = new Dictionary<Vector2I, int>();

        frontier.Enqueue(start, 0);
        bestCost[start] = 0;

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            int costSoFar = bestCost[current];

            foreach (var neighbor in GetNeighbors(current))
            {
                if (!Tiles.TryGetValue(neighbor, out var tile)) continue;
                if (!tile.IsWalkable || tile.IsBlocked) continue;
                if (tile.IsOccupied && neighbor != start) continue;

                int stepCost = Mathf.Max(1, tile.MoveCost);
                int newCost = costSoFar + stepCost;

                if (newCost > maxMove) continue;

                if (bestCost.TryGetValue(neighbor, out int old) && old <= newCost) continue;

                bestCost[neighbor] = newCost;
                frontier.Enqueue(neighbor, newCost);

                if (neighbor != start)
                    result.Add(neighbor);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a dictionary of reachable tile coords → movement cost to reach them.
    /// Used to drive cost-coloured highlighting.
    /// </summary>
    public Dictionary<Vector2I, int> GetReachableTilesWithCost(Unit unit)
    {
        var result = new Dictionary<Vector2I, int>();
        if (unit?.CurrentTile == null) return result;

        var start = unit.CurrentTile.Axial;
        int maxMove = unit.Stats.MovePoints;

        // Priority queue: (coord, costSoFar) ordered by lowest cost first
        var frontier = new PriorityQueue<Vector2I, int>();
        var bestCost = new Dictionary<Vector2I, int>();

        frontier.Enqueue(start, 0);
        bestCost[start] = 0;

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            int costSoFar = bestCost[current];

            foreach (var neighbor in GetNeighbors(current))
            {
                if (!Tiles.TryGetValue(neighbor, out var tile)) continue;
                if (!tile.IsWalkable || tile.IsBlocked) continue;
                if (tile.IsOccupied && neighbor != start) continue;

                int stepCost = Mathf.Max(1, tile.MoveCost);
                int newCost = costSoFar + stepCost;

                if (newCost > maxMove) continue;

                if (bestCost.TryGetValue(neighbor, out int old) && old <= newCost) continue;

                bestCost[neighbor] = newCost;
                frontier.Enqueue(neighbor, newCost);

                if (neighbor != start)
                    result[neighbor] = newCost;
            }
        }

        return result;
    }
}