using Godot;
using System;

public partial class HexGridManager : Node3D
{
    [Export] public PackedScene HexTileScene3D;
    [Export] public int GridWidth = 7;   // number of columns (q)
    [Export] public int GridHeight = 6;  // number of rows (r)
    [Export] public float HexRadius;

    public Vector3 GridBoundsMin { get; private set; }
    public Vector3 GridBoundsMax { get; private set; }

    public override void _Ready()
    {
        //GenerateHexGrid();
        GenerateAxialHexGrid();
        CenterCameraOverGrid();
    }

    private void GenerateHexGrid()
    {
        float hexWidth = HexRadius * 2f;
        float hexHeight = Mathf.Sqrt(3f) * HexRadius;
        float verticalSpacing = hexHeight;
        float horizontalSpacing = hexWidth * 0.75f;

        for (int q = 0; q < GridWidth; q++) // columns
        {
            for (int r = 0; r < GridHeight; r++) // rows
            {
                float x = q * horizontalSpacing;
                float z = r * verticalSpacing + (q % 2 == 1 ? verticalSpacing / 2f : 0); // offset every odd column

                var tile = HexTileScene3D.Instantiate<HexTile>();
                tile.Position = new Vector3(x, 0, z); // Flat grid on xy plane

                //GD.Print($"Placed tile at ({q}, {r}) -> Position: {tile.Position}");
                AddChild(tile);
                tile.Call("SetCoordinatesLabel", q, r);
            }
        }
        float maxX = (GridWidth - 1) * horizontalSpacing;
        float maxZ = (GridHeight - 1) * verticalSpacing + ((GridWidth % 2 == 1) ? verticalSpacing / 2f : 0);
        GridBoundsMin = new Vector3(0, 0, 0);
        GridBoundsMax = new Vector3(maxX, 0, maxZ);

    }

    private void GenerateAxialHexGrid()
    {
        for (int q = 0; q < GridWidth; q++)
        {
            for (int r = 0; r < GridHeight; r++)
            {
                // Axial (q,r) to world position for flat top

                float x = HexRadius * 3f / 2f * q;
                float z = HexRadius * Mathf.Sqrt(3f) * (r + q / 2f); //q/2 is a float

                var tile = HexTileScene3D.Instantiate<HexTile>();
                tile.Position = new Vector3(x, 0, z);


                AddChild(tile);
                tile.Call("SetCoordinatesLabel", q, r);
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
}
