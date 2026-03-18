using Godot;
using System;
using System.Collections.Generic;

public partial class HexGridManager : Node3D
{
    [Export] public PackedScene HexTileScene3D;
    [Export] public int GridWidth = 7;
    [Export] public int GridHeight = 6;
    [Export] public float HexRadius = 1f;

    public Vector3 GridBoundsMin { get; private set; }
    public Vector3 GridBoundsMax { get; private set; }

    public readonly Dictionary<Vector2I, TileData> Tiles = new();

    public override void _Ready()
    {
        GenerateAxialHexGrid();
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

    private void GenerateAxialHexGrid()
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
                    ElementId = 0
                };

                Tiles[coord] = tileData;

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