using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Generates and manages the overworld hex exploration map.
/// 2D flat-top hex grid using axial coordinates, same convention as HexGridManager.
/// </summary>
public partial class OverworldHexGrid : Node2D
{
    [Export] public int GridWidth = 15;
    [Export] public int GridHeight = 15;

    // ── Runtime data ────────────────────────────────────────────────────
    public Dictionary<Vector2I, OverworldHex> Hexes { get; private set; } = new();
    public Vector2I EntryCoord { get; private set; }
    public Vector2I ObjectiveCoord { get; private set; }

    // ── Hex spacing (flat-top) ──────────────────────────────────────────
    private float _hexSize;
    private float _hexWidth;   // horizontal distance between hex centers
    private float _hexHeight;  // vertical distance between hex centers

    // ── Signals ─────────────────────────────────────────────────────────
    [Signal] public delegate void HexClickedEventHandler(Vector2I axial);

    public override void _Ready()
    {
        _hexSize = OverworldHex.GetHexSize();
        // Flat-top hex spacing:
        //   horizontal center-to-center = 1.5 * size
        //   vertical center-to-center   = sqrt(3) * size
        _hexWidth = _hexSize * 1.5f;
        _hexHeight = _hexSize * Mathf.Sqrt(3f);

        GenerateGrid();
    }

    /// <summary>
    /// Builds the hex grid, assigns terrain, places entry + objective.
    /// </summary>
    public void GenerateGrid()
    {
        // Clear any previous grid
        foreach (var hex in Hexes.Values)
            hex.QueueFree();
        Hexes.Clear();

        // Generate hex tiles
        for (int q = 0; q < GridWidth; q++)
        {
            for (int r = 0; r < GridHeight; r++)
            {
                var axial = new Vector2I(q, r);
                var hex = new OverworldHex();
                hex.Axial = axial;
                hex.Position = AxialToWorld(axial);

                // Assign random terrain (placeholder — replace with region definitions later)
                hex.Terrain = GetPlaceholderTerrain(q, r);

                // Everything starts hidden
                hex.Fog = OverworldHex.FogState.Hidden;

                hex.HexClicked += OnHexClicked;
                AddChild(hex);
                Hexes[axial] = hex;
            }
        }

        // Entry point: bottom-left area
        EntryCoord = new Vector2I(0, GridHeight / 2);

        // Objective: top-right area (visible through fog as a landmark)
        ObjectiveCoord = new Vector2I(GridWidth - 2, GridHeight / 2);
        if (Hexes.TryGetValue(ObjectiveCoord, out var objHex))
        {
            objHex.POI = OverworldHex.POIType.Objective;
            objHex.Terrain = OverworldHex.TerrainType.ArcaneGround;
        }

        GD.Print($"Overworld grid generated: {GridWidth}x{GridHeight}, " +
                 $"Entry={EntryCoord}, Objective={ObjectiveCoord}");
    }

    // ── Coordinate math (same convention as HexGridManager) ─────────────

    /// <summary>
    /// Convert axial hex coordinate to 2D world position (flat-top layout).
    /// Mirrors HexGridManager.AxialToWorld but returns Vector2 for 2D.
    /// </summary>
    public Vector2 AxialToWorld(Vector2I axial)
    {
        float x = _hexSize * 1.5f * axial.X;
        float y = _hexSize * Mathf.Sqrt(3f) * (axial.Y + axial.X / 2f);
        return new Vector2(x, y);
    }

    /// <summary>
    /// Convert a 2D world position back to the nearest axial coordinate.
    /// Useful for mouse picking if you ever need it.
    /// </summary>
    public Vector2I WorldToAxial(Vector2 world)
    {
        float q = (2f / 3f * world.X) / _hexSize;
        float r = (-1f / 3f * world.X + Mathf.Sqrt(3f) / 3f * world.Y) / _hexSize;
        return AxialRound(q, r);
    }

    private Vector2I AxialRound(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int rs = Mathf.RoundToInt(s);

        float qDiff = Mathf.Abs(rq - q);
        float rDiff = Mathf.Abs(rr - r);
        float sDiff = Mathf.Abs(rs - s);

        if (qDiff > rDiff && qDiff > sDiff)
            rq = -rr - rs;
        else if (rDiff > sDiff)
            rr = -rq - rs;

        return new Vector2I(rq, rr);
    }

    // ── Neighbor / distance helpers ─────────────────────────────────────

    public static readonly Vector2I[] HexDirs =
    {
        new Vector2I(1, 0),
        new Vector2I(1, -1),
        new Vector2I(0, -1),
        new Vector2I(-1, 0),
        new Vector2I(-1, 1),
        new Vector2I(0, 1)
    };

    public List<Vector2I> GetNeighbors(Vector2I axial)
    {
        var result = new List<Vector2I>();
        foreach (var dir in HexDirs)
        {
            var neighbor = axial + dir;
            if (Hexes.ContainsKey(neighbor))
                result.Add(neighbor);
        }
        return result;
    }

    /// <summary>
    /// Hex distance (same formula as HexGridManager.Distance).
    /// </summary>
    public int Distance(Vector2I a, Vector2I b)
    {
        int ax = a.X, az = a.Y, ay = -ax - az;
        int bx = b.X, bz = b.Y, by = -bx - bz;
        return (Math.Abs(ax - bx) + Math.Abs(ay - by) + Math.Abs(az - bz)) / 2;
    }

    /// <summary>
    /// Get all hex coords within a radius of a center coord.
    /// Used for vision, AOE, etc.
    /// </summary>
    public List<Vector2I> GetHexesInRange(Vector2I center, int range)
    {
        var result = new List<Vector2I>();
        foreach (var kvp in Hexes)
        {
            if (Distance(center, kvp.Key) <= range)
                result.Add(kvp.Key);
        }
        return result;
    }

    // ── Placeholder terrain (replace with region definitions in Phase 2) ─

    private OverworldHex.TerrainType GetPlaceholderTerrain(int q, int r)
    {
        // Simple noise-like distribution for testing.
        // Creates a roughly natural-looking terrain mix.
        int hash = HashCoord(q, r);

        // Roads along the horizontal middle band
        if (r >= GridHeight / 2 - 1 && r <= GridHeight / 2 + 1 && hash % 5 < 2)
            return OverworldHex.TerrainType.Road;

        return (hash % 10) switch
        {
            0 or 1 or 2 or 3 => OverworldHex.TerrainType.Grassland,
            4 or 5           => OverworldHex.TerrainType.Forest,
            6                => OverworldHex.TerrainType.Ruins,
            7                => OverworldHex.TerrainType.Mountain,
            8                => OverworldHex.TerrainType.ArcaneGround,
            9                => OverworldHex.TerrainType.Swamp,
            _                => OverworldHex.TerrainType.Grassland
        };
    }

    /// <summary>
    /// Simple deterministic hash for pseudo-random terrain placement.
    /// Same seed = same map, which is useful for testing.
    /// </summary>
    private int HashCoord(int q, int r)
    {
        int h = q * 374761393 + r * 668265263;
        h = (h ^ (h >> 13)) * 1274126177;
        return Math.Abs(h);
    }

    private void OnHexClicked(Vector2I axial)
    {
        EmitSignal(SignalName.HexClicked, axial);
    }
}