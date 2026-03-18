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

    public Vector3 GridBoundsMin { get; private set; }
    public Vector3 GridBoundsMax { get; private set; }

    public readonly Dictionary<Vector2I, TileData> Tiles = new();

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
        AssignTerrain();
        AssignElements();
        GenerateObstacles();
        ApplyTileVisuals();
        SpawnObstacleVisuals();
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

    private void AssignTerrain()
    {
        foreach (var kvp in Tiles)
        {
            TileData tile = kvp.Value;
            float roll = GD.Randf();

            if (roll < 0.10f)
            {
                tile.TerrainType = TileTerrainType.Water;
                tile.IsWalkable = false;
                tile.MoveCost = 999;
            }
            else if (roll < 0.20f)
            {
                tile.TerrainType = TileTerrainType.Forest;
                tile.MoveCost = 2;
            }
            else if (roll < 0.28f)
            {
                tile.TerrainType = TileTerrainType.Stone;
            }
            else
            {
                tile.TerrainType = TileTerrainType.Grass;
                tile.MoveCost = 1;
            }
        }
    }

    private void AssignElements()
    {
        foreach (var kvp in Tiles)
        {
            TileData tile = kvp.Value;

            float roll = GD.Randf();

            if (roll < 0.08f)
            {
                tile.ElementType = TileElementType.Fire;
                tile.ElementStrength = 1.0f;
                tile.IsHazardous = true;
            }
            else if (roll < 0.14f)
            {
                tile.ElementType = TileElementType.Arcane;
                tile.ElementStrength = 0.8f;
            }
            else if (roll < 0.20f)
            {
                tile.ElementType = TileElementType.Frost;
                tile.ElementStrength = 0.9f;
            }
            else
            {
                tile.ElementType = TileElementType.None;
                tile.ElementStrength = 0f;
            }
        }
    }

    private void GenerateObstacles()
    {
        foreach (var kvp in Tiles)
        {
            TileData tile = kvp.Value;

            if (tile.TerrainType == TileTerrainType.Water)
                continue;

            if (GD.Randf() < 0.12f)
            {
                tile.IsBlocked = true;
                tile.IsWalkable = false;
                tile.BlocksLineOfSight = true;
                tile.ObstacleKind = "rock";
            }
        }
    }

    private void SpawnObstacleVisuals()
    {
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