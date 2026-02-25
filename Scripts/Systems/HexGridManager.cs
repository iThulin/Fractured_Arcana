using Godot;
using System;
using System.Collections.Generic;

public partial class HexGridManager : Node3D
{
    [Export] public PackedScene HexTileScene3D;
    [Export] public int GridWidth = 7;   // number of columns (q)
    [Export] public int GridHeight = 6;  // number of rows (r)
    [Export] public float HexRadius = 1f;

    public Vector3 GridBoundsMin { get; private set; }
    public Vector3 GridBoundsMax { get; private set; }

    public readonly Dictionary<Vector2I, HexTile> Tiles = new();

    public override void _Ready()
    {
        GenerateAxialHexGrid();
        CenterCameraOverGrid();
    }

    public HexTile GetTile(Vector2I axial) => Tiles.TryGetValue(axial, out var t) ? t : null;

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
                // Axial (q,r) to world position for flat top
                float x = HexRadius * 1.5f * q;
                float z = HexRadius * Mathf.Sqrt(3f) * (r + q / 2f);

                var tile = HexTileScene3D.Instantiate<HexTile>();
                tile.Position = new Vector3(x, 0, z);

                tile.Axial = new Vector2I(q, r);   // add this property to HexTile
                AddChild(tile);

                Tiles[tile.Axial] = tile;
                tile.Call("SetCoordinatesLabel", q, r);

                if (first)
                    {
                        min = tile.GlobalPosition;
                        max = tile.GlobalPosition;
                        first = false;
                    }
                else
                    {
                        var p = tile.GlobalPosition;
                        min = new Vector3(Mathf.Min(min.X, p.X), 0, Mathf.Min(min.Z, p.Z));
                        max = new Vector3(Mathf.Max(max.X, p.X), 0, Mathf.Max(max.Z, p.Z));
                    }
            }
        }

        float maxX = HexRadius * 3f / 2f * GridWidth - 1;
        float maxZ = HexRadius * Mathf.Sqrt(3f) * (GridHeight + (GridWidth - 1) / 2f);

        GridBoundsMin = new Vector3(0, 0, 0);
        GridBoundsMax = new Vector3(maxX, 0, maxZ);
    }
        private void CenterCameraOverGrid()
    {
        // Calculate grid dimensions in world units
        float hexWidth = HexRadius * 2f;
        float hexHeight = Mathf.Sqrt(3f) * HexRadius;
        float horizontalSpacing = hexWidth * 0.75f;
        float verticalSpacing = hexHeight;

        // World dimensions
        float worldWidth = horizontalSpacing * (GridWidth - 1);
        float worldHeight = verticalSpacing * (GridHeight - 1);

        // Center of grid in world space
        Vector3 center = new Vector3(worldWidth / 2f, 0, worldHeight / 2f);

        // Find the camera in the scene
        var camera = GetNodeOrNull<Camera3D>("../CameraController/Camera3D");
        if (camera != null)
        {
            // Position the camera above and back, looking down at the center
            float distance = Mathf.Max(worldWidth, worldHeight);
            camera.GlobalPosition = center + new Vector3(0, distance, distance * 0.8f);
            camera.LookAt(center, Vector3.Up);
        }
    }

        public int Distance(HexTile a, HexTile b)
    {
        var aq = a.Axial.X; var ar = a.Axial.Y;
        var bq = b.Axial.X; var br = b.Axial.Y;

        int ax = aq, az = ar, ay = -ax - az;
        int bx = bq, bz = br, by = -bx - bz;

        return (Math.Abs(ax - bx) + Math.Abs(ay - by) + Math.Abs(az - bz)) / 2;
    }
}
