using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Generates and manages the overworld hex exploration map.
/// 2D flat-top hex grid using axial coordinates, same convention as HexGridManager.
/// </summary>
public partial class OverworldHexGrid : Node2D
{
    [Export] public int Seed = 0;  // 0 = random
    [Export] public int GridWidth = 15;
    [Export] public int GridHeight = 15;
    [Export] public float HexSize = 36f;

    public RegionDefinition Region { get; set; }

    // ── Runtime data ────────────────────────────────────────────────────
    private RandomNumberGenerator _rng;
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
        _rng = new RandomNumberGenerator();
        if (Seed != 0)
            _rng.Seed = (ulong)Seed;
        else
        {
            _rng.Randomize();
            Seed = (int)_rng.Randi();
        }

        // Apply region dimensions if a region is set
        if (Region != null)
        {
            GridWidth = Region.GridWidth;
            GridHeight = Region.GridHeight;
        }

        _hexSize = HexSize;
        _hexWidth = _hexSize * 1.5f;
        _hexHeight = _hexSize * Mathf.Sqrt(3f);

        GenerateGrid();
    }

    /// <summary>
    /// Builds the hex grid, assigns terrain, places entry + objective.
    /// </summary>
    public void GenerateGrid()
    {
        GD.Print($"GenerateGrid called from:\n{System.Environment.StackTrace}");
        foreach (var hex in Hexes.Values)
            hex.QueueFree();
        Hexes.Clear();

        // Generate hex tiles with no terrain yet
        for (int q = 0; q < GridWidth; q++)
        {
            for (int r = 0; r < GridHeight; r++)
            {
                var axial = new Vector2I(q, r);
                var hex = new OverworldHex();
                hex.Axial = axial;
                hex.Position = AxialToWorld(axial);
                hex.Fog = OverworldHex.FogState.Hidden;

                hex.HexClicked += OnHexClicked;
                AddChild(hex);
                Hexes[axial] = hex;
            }
        }

        // ── Generate terrain with biomes ────────────────────────────────
        GenerateBiomes();

        if (Region == null || Region.HasRiver)
            GenerateRiver();

        if (Region == null || Region.HasRoads)
            GenerateRoads();

        if (Region != null && Region.HasMountainRange)
            GenerateMountainRange();

        // ── Place entry and objective ───────────────────────────────────
        EntryCoord = new Vector2I(1, GridHeight / 2);
        // Make sure entry area is walkable
        ClearTerrainAround(EntryCoord, 1, OverworldHex.TerrainType.Grassland);

        ObjectiveCoord = new Vector2I(GridWidth - 2, GridHeight / 2);
        if (Hexes.TryGetValue(ObjectiveCoord, out var objHex))
        {
            objHex.POI = OverworldHex.POIType.Objective;
            objHex.Terrain = OverworldHex.TerrainType.ArcaneGround;
        }
        ClearTerrainAround(ObjectiveCoord, 1, OverworldHex.TerrainType.ArcaneGround);

        GD.Print($"Overworld grid generated: {GridWidth}x{GridHeight}, " +
                 $"Entry={EntryCoord}, Objective={ObjectiveCoord}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Biome generation
    // ═══════════════════════════════════════════════════════════════════════

    private struct BiomeSeed
    {
        public Vector2I Center;
        public OverworldHex.TerrainType Primary;
        public OverworldHex.TerrainType Secondary;
        public int Radius;
    }

        private void GenerateBiomes()
    {
        // Start everything as grassland
        foreach (var hex in Hexes.Values)
            hex.Terrain = OverworldHex.TerrainType.Grassland;

        // Build biome list — from region if set, otherwise the hardcoded defaults
        var biomes = (Region != null && Region.Biomes != null && Region.Biomes.Count > 0)
            ? BuildBiomesFromRegion()
            : BuildDefaultBiomes();

        foreach (var biome in biomes)
            PaintBiome(biome);
    }

    private List<BiomeSeed> BuildBiomesFromRegion()
    {
        var biomes = new List<BiomeSeed>();
        foreach (var bd in Region.Biomes)
        {
            biomes.Add(new BiomeSeed
            {
                Center = new Vector2I(bd.CenterQ, bd.CenterR),
                Primary = ParseTerrain(bd.PrimaryTerrain),
                Secondary = ParseTerrain(bd.SecondaryTerrain),
                Radius = bd.Radius
            });
        }
        return biomes;
    }

    private List<BiomeSeed> BuildDefaultBiomes()
    {
        // The same hardcoded biomes you have now, just extracted into a method.
        var biomes = new List<BiomeSeed>();

        biomes.Add(new BiomeSeed
        {
            Center = new Vector2I(3, 3),
            Primary = OverworldHex.TerrainType.Forest,
            Secondary = OverworldHex.TerrainType.Swamp,
            Radius = 4
        });
        biomes.Add(new BiomeSeed
        {
            Center = new Vector2I(8, 2),
            Primary = OverworldHex.TerrainType.Ruins,
            Secondary = OverworldHex.TerrainType.ArcaneGround,
            Radius = 3
        });
        biomes.Add(new BiomeSeed
        {
            Center = new Vector2I(12, 11),
            Primary = OverworldHex.TerrainType.Volcanic,
            Secondary = OverworldHex.TerrainType.Mountain,
            Radius = 3
        });
        biomes.Add(new BiomeSeed
        {
            Center = new Vector2I(3, 11),
            Primary = OverworldHex.TerrainType.Swamp,
            Secondary = OverworldHex.TerrainType.Forest,
            Radius = 3
        });
        biomes.Add(new BiomeSeed
        {
            Center = new Vector2I(12, 5),
            Primary = OverworldHex.TerrainType.ArcaneGround,
            Secondary = OverworldHex.TerrainType.Ruins,
            Radius = 3
        });
        biomes.Add(new BiomeSeed
        {
            Center = new Vector2I(11, 1),
            Primary = OverworldHex.TerrainType.Mountain,
            Secondary = OverworldHex.TerrainType.Grassland,
            Radius = 3
        });

        return biomes;
    }

    private OverworldHex.TerrainType ParseTerrain(string name)
    {
        return Enum.TryParse<OverworldHex.TerrainType>(name, out var result)
            ? result : OverworldHex.TerrainType.Grassland;
    }

    private void PaintBiome(BiomeSeed biome)
    {
        foreach (var kvp in Hexes)
        {
            int dist = Distance(kvp.Key, biome.Center);
            if (dist > biome.Radius + 1) continue;

            var hex = kvp.Value;
            int hash = HashCoord(kvp.Key.X, kvp.Key.Y);

            if (dist <= biome.Radius - 1)
            {
                // Core — solid primary terrain
                hex.Terrain = biome.Primary;
            }
            else if (dist == biome.Radius)
            {
                // Edge — mix of primary and secondary
                hex.Terrain = (hash % 3 < 2) ? biome.Primary : biome.Secondary;
            }
            else if (dist == biome.Radius + 1)
            {
                // Fringe — mostly stays as-is, occasional secondary bleed
                if (hash % 5 == 0)
                    hex.Terrain = biome.Secondary;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // River generation — snakes vertically through the map
    // ═══════════════════════════════════════════════════════════════════════

    private void GenerateRiver()
    {
        // River flows from top to bottom, snaking left and right
        int q = GridWidth / 2; // start in the middle
        var riverHexes = new List<Vector2I>();

        for (int r = 0; r < GridHeight; r++)
        {
            var coord = new Vector2I(q, r);
            if (Hexes.ContainsKey(coord))
            {
                riverHexes.Add(coord);
                Hexes[coord].Terrain = OverworldHex.TerrainType.Water;

                // Widen the river occasionally
                int hash = HashCoord(q, r);
                if (hash % 4 == 0)
                {
                    var wide = new Vector2I(q - 1, r);
                    if (Hexes.ContainsKey(wide))
                    {
                        riverHexes.Add(wide);
                        Hexes[wide].Terrain = OverworldHex.TerrainType.Water;
                    }
                }
            }

            // Snake left or right
            int drift = HashCoord(q + r * 7, r) % 5;
            if (drift == 0 && q > 2) q--;
            else if (drift == 1 && q < GridWidth - 3) q++;
            // else stay straight
        }

        // Create 2-3 crossing points (bridges / fords)
        int crossingSpacing = GridHeight / 4;
        for (int i = 1; i <= 3; i++)
        {
            int crossingR = i * crossingSpacing;
            if (crossingR >= GridHeight) continue;

            // Find the river hex at this row
            foreach (var rc in riverHexes)
            {
                if (rc.Y == crossingR)
                {
                    Hexes[rc].Terrain = OverworldHex.TerrainType.Road;
                    break;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Road generation — connects entry to objective with branches
    // ═══════════════════════════════════════════════════════════════════════

    private void GenerateRoads()
    {
        // Main road: roughly horizontal from entry area to objective area
        int roadR = GridHeight / 2;

        for (int q = 0; q < GridWidth; q++)
        {
            var coord = new Vector2I(q, roadR);
            if (Hexes.TryGetValue(coord, out var hex))
            {
                // Don't overwrite water (road uses the bridge crossings)
                if (hex.Terrain != OverworldHex.TerrainType.Water)
                    hex.Terrain = OverworldHex.TerrainType.Road;
            }

            // Slight vertical wobble
            int hash = HashCoord(q * 13, roadR);
            if (hash % 6 == 0 && roadR > GridHeight / 2 - 2) roadR--;
            else if (hash % 6 == 1 && roadR < GridHeight / 2 + 2) roadR++;
        }

        // Branch road going north from the middle
        int branchQ = GridWidth / 3;
        for (int r = GridHeight / 2; r >= 2; r--)
        {
            var coord = new Vector2I(branchQ, r);
            if (Hexes.TryGetValue(coord, out var hex))
            {
                if (hex.Terrain != OverworldHex.TerrainType.Water)
                    hex.Terrain = OverworldHex.TerrainType.Road;
            }

            // Drift
            int hash = HashCoord(branchQ, r * 11);
            if (hash % 4 == 0) branchQ++;
        }

        // Branch road going south from 2/3 mark
        branchQ = (GridWidth * 2) / 3;
        for (int r = GridHeight / 2; r < GridHeight - 1; r++)
        {
            var coord = new Vector2I(branchQ, r);
            if (Hexes.TryGetValue(coord, out var hex))
            {
                if (hex.Terrain != OverworldHex.TerrainType.Water)
                    hex.Terrain = OverworldHex.TerrainType.Road;
            }

            int hash = HashCoord(branchQ, r * 13);
            if (hash % 4 == 0) branchQ--;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Mountain range — creates a natural barrier to navigate around
    // ═══════════════════════════════════════════════════════════════════════

    private void GenerateMountainRange()
    {
        // Diagonal range from upper-center to lower-right
        // Creates a wall the player has to route around or pay 3 steps to cross
        int q = GridWidth / 2 + 2;
        int r = 1;

        for (int i = 0; i < 8; i++)
        {
            var coord = new Vector2I(q, r);
            if (Hexes.TryGetValue(coord, out var hex))
            {
                // Don't overwrite water or roads
                if (hex.Terrain != OverworldHex.TerrainType.Water &&
                    hex.Terrain != OverworldHex.TerrainType.Road)
                {
                    hex.Terrain = OverworldHex.TerrainType.Mountain;
                }
            }

            // Also paint one neighbor for thickness
            foreach (var dir in HexDirs)
            {
                var neighbor = coord + dir;
                int hash = HashCoord(neighbor.X, neighbor.Y);
                if (hash % 3 == 0 && Hexes.TryGetValue(neighbor, out var nhex))
                {
                    if (nhex.Terrain != OverworldHex.TerrainType.Water &&
                        nhex.Terrain != OverworldHex.TerrainType.Road)
                    {
                        nhex.Terrain = OverworldHex.TerrainType.Mountain;
                    }
                }
            }

            // Leave a pass every ~3 hexes
            if (i % 3 == 2)
            {
                // Skip painting this hex — creates a gap
                r++;
                continue;
            }

            // Move diagonally
            q++;
            r++;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Clear terrain around a coordinate to ensure it's walkable.
    /// Used for entry and objective areas.
    /// </summary>
    private void ClearTerrainAround(Vector2I center, int radius, OverworldHex.TerrainType terrain)
    {
        foreach (var coord in GetHexesInRange(center, radius))
        {
            if (Hexes.TryGetValue(coord, out var hex))
            {
                hex.Terrain = terrain;
            }
        }
    }

    private int HashCoord(int q, int r)
    {
        int h = q * 374761393 + r * 668265263 + Seed * 2147483647;
        h = (h ^ (h >> 13)) * 1274126177;
        return Math.Abs(h);
    }

    private void OnHexClicked(Vector2I axial)
    {
        EmitSignal(SignalName.HexClicked, axial);
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
}