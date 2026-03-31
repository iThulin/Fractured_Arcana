using Godot;
using System;
using System.Collections.Generic;

public partial class HexGridManager : Node3D
{
    [Export] public PackedScene HexTileScene3D;
    [Export] public int GridWidth = 7;
    [Export] public int GridHeight = 6;
    [Export] public float HexRadius = 1f;

    [Export] public PackedScene RockObstacleScene;
    [Export] public PackedScene CrystalObstacleScene;
    [Export] public Node3D ObstacleParent;

    // Temporary Spawn conditions
    [Export] public Vector2I PlayerSpawnCoord = new Vector2I(1, 1);
    [Export] public Vector2I EnemySpawnCoord = new Vector2I(4, 2);
    [Export] public int ReservedSpawnRadius = 1;
    [Export] public MapTheme Theme = MapTheme.ArcaneMeadow;

    public Vector3 GridBoundsMin { get; private set; }
    public Vector3 GridBoundsMax { get; private set; }
    private readonly HashSet<Vector2I> ReservedTiles = new();

    public bool BlocksMovementByHeight = false;

    public readonly Dictionary<Vector2I, TileData> Tiles = new();

    public enum MapTheme
    {
        ArcaneMeadow,
        FrozenBasin,
        VolcanicScar,
        OvergrownRuins
    }

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

    public void GenerateMap()
    {
        GenerateBaseGrid();

        ReserveRadius(PlayerSpawnCoord, ReservedSpawnRadius);
        ReserveRadius(EnemySpawnCoord, ReservedSpawnRadius);
        ClearReservedTiles();

        GenerateTheme();
        //GenerateThemeFeatures();

        EnsureReservedTilesArePlayable();
        EnsureConnectivity(PlayerSpawnCoord, EnemySpawnCoord);

        ApplyTileVisuals();
        ApplyTileHeights();
        RefreshAllTileLabels();
        SpawnObstacleVisuals();

        
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

    private void GenerateTheme()
    {
        switch (Theme)
        {
            case MapTheme.ArcaneMeadow:
                GenerateArcaneMeadow();
                GenerateArcaneMeadowFeature();
                break;

            case MapTheme.FrozenBasin:
                GenerateFrozenBasin();
                GenerateFrozenBasinFeature();
                break;

            case MapTheme.VolcanicScar:
                GenerateVolcanicScar();
                GenerateVolcanicScarFeature();
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

    private void AssignTerrain()
    {
        SetAllTilesToTerrain(TileTerrainType.Grass);

        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, 2, 0.65f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 2, 0.8f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 2, 0.75f);

        if (GD.Randf() < 0.5f)
            PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 1, 0.8f);
    }

    private void AssignElements()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.ElementType = TileElementType.None;
            tile.ElementStrength = 0f;
            tile.IsHazardous = false;
        }

        PaintElementPatch(GetRandomCoord(), TileElementType.Fire, 1, 1.0f, 0.7f);
        PaintElementPatch(GetRandomCoord(), TileElementType.Arcane, 2, 0.9f, 0.75f);

        if (GD.Randf() < 0.5f)
            PaintElementPatch(GetRandomCoord(), TileElementType.Frost, 1, 0.9f, 0.7f);
    }

    private void GenerateObstacles()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.ObstacleKind = "";
            tile.IsBlocked = false;
            tile.BlocksLineOfSight = false;
        }

        PaintObstacleCluster(GetRandomCoord(), "rock", 3);
        PaintObstacleCluster(GetRandomCoord(), "rock", 4);

        if (GD.Randf() < 0.5f)
            PaintObstacleCluster(GetRandomCoord(), "crystal", 3);
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

    private static readonly Vector2I[] HexDirs =
    {
        new Vector2I(1, 0),
        new Vector2I(1, -1),
        new Vector2I(0, -1),
        new Vector2I(-1, 0),
        new Vector2I(-1, 1),
        new Vector2I(0, 1)
    };

    private List<Vector2I> GetNeighbors(Vector2I coord)
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

    private void ApplyVisualToTile(TileData tile)
    {
        Color color = Colors.White;

        switch (tile.TerrainType)
        {
            case TileTerrainType.Grass:
                color = new Color(0.45f, 0.75f, 0.45f);
                break;
            case TileTerrainType.Water:
                color = new Color(0.2f, 0.45f, 0.85f);
                break;
            case TileTerrainType.Lava:
                color = new Color(0.9f, 0.3f, 0.1f);
                break;
            case TileTerrainType.Forest:
                color = new Color(0.2f, 0.5f, 0.2f);
                break;
            case TileTerrainType.Stone:
                color = new Color(0.5f, 0.5f, 0.55f);
                break;
            case TileTerrainType.Arcane:
                color = new Color(0.55f, 0.25f, 0.8f);
                break;
            case TileTerrainType.Ice:
                color = new Color(0.7f, 0.9f, 1.0f);
                break;
        }

        // element overlay tint
        switch (tile.ElementType)
        {
            case TileElementType.Fire:
                color = color.Lerp(new Color(1f, 0.3f, 0.1f), 0.4f);
                break;
            case TileElementType.Arcane:
                color = color.Lerp(new Color(0.7f, 0.2f, 1f), 0.4f);
                break;
            case TileElementType.Frost:
                color = color.Lerp(new Color(0.8f, 0.95f, 1f), 0.4f);
                break;
        }

        tile.TileView.SetBaseColor(color);
    }

    private void RefreshAllTileLabels()
    {
        foreach (var tile in Tiles.Values)
        {
            tile.TileView?.RefreshLabel(tile);
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
                    break;

                case TileTerrainType.Arcane:
                    tile.IsWalkable = true;
                    tile.IsBlocked = false;
                    tile.MoveCost = 1;
                    break;
            }
        }
    }

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

    private Vector2I GetRandomCoord()
    {
        var keys = new List<Vector2I>(Tiles.Keys);
        if (keys.Count == 0)
            return Vector2I.Zero;

        return keys[(int)(GD.Randi() % (uint)keys.Count)];
    }

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
            goalTile.TerrainType = TileTerrainType.Grass;
            goalTile.ElementType = TileElementType.None;
            goalTile.ElementStrength = 0f;
            goalTile.IsWalkable = true;
            goalTile.IsBlocked = false;
            goalTile.BlocksLineOfSight = false;
            goalTile.IsHazardous = false;
            goalTile.MoveCost = 1;
            goalTile.ObstacleKind = "";
        }
    }

    private void ClearTileObstacleState(TileData tile)
    {
        tile.IsBlocked = false;
        tile.BlocksLineOfSight = false;
        tile.ObstacleKind = "";
    }

    // Themes
    private void GenerateArcaneMeadow()
    {
        SetAllTilesToTerrain(TileTerrainType.Grass);

        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 2, 0.8f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 1, 0.8f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, 2, 0.65f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 1, 0.7f);

        PaintElementPatch(GetRandomCoord(), TileElementType.Arcane, 2, 1.0f, 0.75f);
        PaintElementPatch(GetRandomCoord(), TileElementType.Frost, 1, 0.8f, 0.7f);

        PaintObstacleCluster(GetRandomCoord(), "rock", 3);

        if (GD.Randf() < 0.7f)
            PaintObstacleCluster(GetRandomCoord(), "crystal", 3);
    }

    private void GenerateFrozenBasin()
    {
        SetAllTilesToTerrain(TileTerrainType.Ice);

        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, 2, 0.7f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 2, 0.75f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Ice, 2, 0.9f);

        PaintElementPatch(GetRandomCoord(), TileElementType.Frost, 2, 1.0f, 0.8f);
        PaintElementPatch(GetRandomCoord(), TileElementType.Frost, 1, 0.9f, 0.75f);

        PaintObstacleCluster(GetRandomCoord(), "rock", 3);

        if (GD.Randf() < 0.5f)
            PaintObstacleCluster(GetRandomCoord(), "crystal", 2);
    }

    private void GenerateVolcanicScar()
    {
        SetAllTilesToTerrain(TileTerrainType.Stone);

        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Lava, 2, 0.75f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Stone, 2, 0.9f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 1, 0.5f);

        PaintElementPatch(GetRandomCoord(), TileElementType.Fire, 2, 1.0f, 0.8f);
        PaintElementPatch(GetRandomCoord(), TileElementType.Fire, 1, 0.9f, 0.75f);

        PaintObstacleCluster(GetRandomCoord(), "rock", 4);
        PaintObstacleCluster(GetRandomCoord(), "rock", 3);

        if (GD.Randf() < 0.4f)
            PaintObstacleCluster(GetRandomCoord(), "crystal", 2);
    }

    private void GenerateOvergrownRuins()
    {
        SetAllTilesToTerrain(TileTerrainType.Stone);

        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 2, 0.8f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Forest, 2, 0.75f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Grass, 2, 0.8f);
        PaintTerrainPatch(GetRandomCoord(), TileTerrainType.Water, 1, 0.65f);

        PaintElementPatch(GetRandomCoord(), TileElementType.Arcane, 1, 0.9f, 0.7f);
        PaintElementPatch(GetRandomCoord(), TileElementType.Frost, 1, 0.8f, 0.7f);

        PaintObstacleCluster(GetRandomCoord(), "rock", 4);
        PaintObstacleCluster(GetRandomCoord(), "rock", 3);

        if (GD.Randf() < 0.6f)
            PaintObstacleCluster(GetRandomCoord(), "crystal", 3);
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

    public int Distance(Vector2I a, Vector2I b)
    {
        int ax = a.X, az = a.Y, ay = -ax - az;
        int bx = b.X, bz = b.Y, by = -bx - bz;

        return (Math.Abs(ax - bx) + Math.Abs(ay - by) + Math.Abs(az - bz)) / 2;
    }

    public int Distance(HexTile a, HexTile b) => Distance(a.Axial, b.Axial);
    public int Distance(TileData a, TileData b) => Distance(a.Axial, b.Axial);
}