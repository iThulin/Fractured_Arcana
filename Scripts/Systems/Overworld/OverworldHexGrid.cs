using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// OverworldHexGrid.cs
//
// Purpose:        Generates and owns the 2D overworld hex map.
//                 Procedurally lays out terrain, designates
//                 entry/objective tiles, instantiates one
//                 OverworldHex per cell, and exposes axial-coord
//                 lookup + neighbour/distance helpers. Same
//                 axial coordinate convention as HexGridManager
//                 (combat grid).
// Layer:          System
// Collaborators:  OverworldHex.cs (child tiles),
//                 OverworldField.cs (seeded terrain field),
//                 RegionDefinition.cs (palette + feature inputs),
//                 FogOfWarManager.cs, POIGenerator.cs (modify the
//                 grid post-construction)
// See:            README §3 — overworld layer
//
// Generation rewrite (substrate pass — mirrors the combat MapField
// rewrite of HexGridManager):
//   - Seeded OverworldField produces coherent elevation + moisture;
//     terrain is DERIVED from a region palette (water in lows,
//     mountains on ridges, forest in the wet band) instead of being
//     painted from hardcoded biome centres.
//   - Adjacency constraints smooth illegal terrain transitions
//     (e.g. Volcanic next to Swamp) and force Mountain/Volcanic to
//     read as contiguous ranges rather than speckle.
//   - The river now traces the DESCENDING elevation gradient from a
//     high edge to a low edge, rather than running down the middle
//     column; roads branch toward POIs.
//   - A connectivity guarantee carves a corridor when entry→objective
//     would otherwise be walled off by Water/Mountain — a failure the
//     previous generator could silently produce and never checked.
//   - All randomness draws from the seeded RNG / field, so the map
//     regenerates identically on return from combat (invariant relied
//     on by EncounterRouter + OverworldRunManager).
// ============================================================

/// <summary>2D flat-top hex grid for the overworld map. Owns the per-tile <see cref="OverworldHex"/> children and exposes axial-coord helpers. Seeded field + RNG ensure the same map regenerates on return from combat.</summary>
public partial class OverworldHexGrid : Node2D
{
    [Export] public int Seed = 0;  // 0 = random
    [Export] public int GridWidth = 15;
    [Export] public int GridHeight = 15;
    [Export] public float HexSize = 36f;

    public RegionDefinition Region { get; set; }

    // ── Runtime data ────────────────────────────────────────────────────
    private RandomNumberGenerator _rng;
    private OverworldField _field;
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

        // ── Seeded coherent field → palette classification ──────────────
        BuildField();
        GenerateTerrainFromField();

        // ── Adjacency + contiguity smoothing ────────────────────────────
        ApplyAdjacencyConstraints();
        ConsolidateRanges();

        // ── Feature layers (toggled per-region) ─────────────────────────
        if (Region == null || Region.HasRiver)
            GenerateRiver();

        if (Region == null || Region.HasRoads)
            GenerateRoads();

        if (Region != null && Region.HasMountainRange)
            GenerateMountainRange();

        // ── Place entry and objective — seeded random positions ─────────
        // Entry:     left edge column (q=1), row randomised in middle third.
        // Objective: right edge column (q=GridWidth-2), independently randomised.
        // Uses _rng which is already seeded, so positions are deterministic
        // per seed and regenerate identically on return from combat.
        int rowMin = GridHeight / 4;
        int rowMax = (3 * GridHeight) / 4;
        int rowRange = Mathf.Max(1, rowMax - rowMin); // guard against tiny grids

        int entryRow = rowMin + (int)(_rng.Randi() % (uint)rowRange);
        EntryCoord = new Vector2I(1, entryRow);
        ClearTerrainAround(EntryCoord, 1, OverworldHex.TerrainType.Grassland);

        int objRow = rowMin + (int)(_rng.Randi() % (uint)rowRange);
        ObjectiveCoord = new Vector2I(GridWidth - 2, objRow);
        if (Hexes.TryGetValue(ObjectiveCoord, out var objHex))
        {
            objHex.POI = OverworldHex.POIType.Objective;
            objHex.Terrain = OverworldHex.TerrainType.ArcaneGround;
        }
        ClearTerrainAround(ObjectiveCoord, 1, OverworldHex.TerrainType.ArcaneGround);

        // ── Guarantee the objective is reachable from entry ─────────────
        EnsureEntryToObjectiveConnectivity();

        GD.Print($"Overworld grid generated: {GridWidth}x{GridHeight}, " +
                 $"Entry={EntryCoord}, Objective={ObjectiveCoord}, Seed={Seed}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Field-based terrain generation
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildField()
    {
        _field = new OverworldField(Seed);

        var bt = Region?.BaseTerrain;
        if (bt != null)
        {
            if (bt.ElevationFrequency > 0f)
                _field.ElevationFrequency = bt.ElevationFrequency;
            if (bt.MoistureFrequency > 0f)
                _field.MoistureFrequency = bt.MoistureFrequency;
            if (bt.DetailWeight >= 0f)
                _field.DetailWeight = bt.DetailWeight;
            _field.ApplyFrequencies();
        }
    }

    /// <summary>
    /// Classifies every hex from the seeded field using the region palette, falling
    /// back to a built-in default palette when the region authors none.
    /// </summary>
    private void GenerateTerrainFromField()
    {
        var palette = (Region?.BaseTerrain != null && Region.BaseTerrain.HasPalette)
            ? Region.BaseTerrain.Palette
            : DefaultPalette();

        foreach (var kvp in Hexes)
        {
            float elevation = _field.SampleElevation01(kvp.Key);
            float moisture = _field.SampleMoisture01(kvp.Key);
            kvp.Value.Terrain = _field.ClassifyByPalette(palette, elevation, moisture);
        }
    }

    /// <summary>
    /// Sensible default palette used when a region authors no base-terrain block.
    /// Ordered specific → general; ends with an unbounded Grassland catch-all.
    /// Low ground = water, high ground = mountain, dry highs = volcanic ridges,
    /// the wet mid-band = forest, the dry mid = grassland.
    /// </summary>
    private static List<OverworldPaletteRule> DefaultPalette()
    {
        return new List<OverworldPaletteRule>
        {
            new() { TerrainName = "Water",    MaxElevation = 0.18f },
            new() { TerrainName = "Volcanic", MinElevation = 0.86f, MaxMoisture = 0.30f },
            new() { TerrainName = "Mountain", MinElevation = 0.82f },
            new() { TerrainName = "Swamp",    MaxElevation = 0.30f, MinMoisture = 0.62f },
            new() { TerrainName = "Forest",   MinMoisture = 0.52f },
            new() { TerrainName = "Grassland" } // unbounded catch-all
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Adjacency constraints — smooth illegal terrain transitions
    // ═══════════════════════════════════════════════════════════════════════

    // Pairs that should never sit edge-to-edge. When found, the "softer" tile of
    // the pair is replaced with a buffer terrain so the transition reads naturally.
    private static readonly (OverworldHex.TerrainType a, OverworldHex.TerrainType b, OverworldHex.TerrainType buffer)[] ForbiddenPairs =
    {
        // Volcanic should never bleed straight into wet terrain.
        (OverworldHex.TerrainType.Volcanic, OverworldHex.TerrainType.Swamp,  OverworldHex.TerrainType.Mountain),
        (OverworldHex.TerrainType.Volcanic, OverworldHex.TerrainType.Water,  OverworldHex.TerrainType.Mountain),
        (OverworldHex.TerrainType.Volcanic, OverworldHex.TerrainType.Forest, OverworldHex.TerrainType.Grassland),
        // Swamp shouldn't touch bare mountain — insert grassland/forest fringe.
        (OverworldHex.TerrainType.Swamp,    OverworldHex.TerrainType.Mountain, OverworldHex.TerrainType.Grassland),
    };

    /// <summary>
    /// Single pass that replaces the softer side of any forbidden adjacency with a buffer
    /// terrain. Order-independent enough for a single sweep at overworld scale; the buffer
    /// itself is always a "neutral" terrain so it can't create a new forbidden pair.
    /// </summary>
    private void ApplyAdjacencyConstraints()
    {
        // Snapshot terrain so edits within the sweep don't cascade unpredictably.
        var snapshot = new Dictionary<Vector2I, OverworldHex.TerrainType>(Hexes.Count);
        foreach (var kvp in Hexes)
            snapshot[kvp.Key] = kvp.Value.Terrain;

        foreach (var kvp in Hexes)
        {
            var coord = kvp.Key;
            var terrain = snapshot[coord];

            foreach (var neighbor in GetNeighbors(coord))
            {
                var nTerrain = snapshot[neighbor];

                foreach (var rule in ForbiddenPairs)
                {
                    // Replace whichever side of the pair is the second member (the
                    // "softer" terrain) with the buffer. Check both orderings.
                    if (terrain == rule.a && nTerrain == rule.b)
                    {
                        Hexes[neighbor].Terrain = rule.buffer;
                    }
                    else if (terrain == rule.b && nTerrain == rule.a)
                    {
                        Hexes[coord].Terrain = rule.buffer;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Removes isolated single-hex Mountain / Volcanic specks so those terrains read as
    /// contiguous ranges. A peak with no same-terrain neighbour is demoted to the most
    /// common neighbouring terrain (or Grassland as a last resort).
    /// </summary>
    private void ConsolidateRanges()
    {
        var toDemote = new List<(Vector2I coord, OverworldHex.TerrainType to)>();

        foreach (var kvp in Hexes)
        {
            var terrain = kvp.Value.Terrain;
            if (terrain != OverworldHex.TerrainType.Mountain &&
                terrain != OverworldHex.TerrainType.Volcanic)
                continue;

            var neighbors = GetNeighbors(kvp.Key);
            bool hasSameNeighbor = false;
            var counts = new Dictionary<OverworldHex.TerrainType, int>();

            foreach (var n in neighbors)
            {
                var nt = Hexes[n].Terrain;
                if (nt == terrain)
                { hasSameNeighbor = true; break; }
                counts[nt] = counts.TryGetValue(nt, out var c) ? c + 1 : 1;
            }

            if (hasSameNeighbor)
                continue;

            // Demote the speck to its most common neighbour terrain.
            var best = OverworldHex.TerrainType.Grassland;
            int bestCount = -1;
            foreach (var pair in counts)
            {
                if (pair.Value > bestCount)
                {
                    bestCount = pair.Value;
                    best = pair.Key;
                }
            }
            toDemote.Add((kvp.Key, best));
        }

        foreach (var (coord, to) in toDemote)
            Hexes[coord].Terrain = to;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // River generation — traces the descending elevation gradient
    // ═══════════════════════════════════════════════════════════════════════

    private void GenerateRiver()
    {
        if (_field == null || Hexes.Count == 0)
            return;

        // Source: the highest-elevation hex on the top or left edge.
        // Mouth: flow is driven entirely by following the descending gradient.
        Vector2I source = FindRiverSource();
        var riverHexes = new List<Vector2I>();
        var visited = new HashSet<Vector2I>();

        Vector2I current = source;
        int safety = GridWidth * GridHeight; // hard cap against loops

        while (safety-- > 0 && Hexes.ContainsKey(current) && visited.Add(current))
        {
            riverHexes.Add(current);
            Hexes[current].Terrain = OverworldHex.TerrainType.Water;

            // Occasional widening for a more natural channel (seeded).
            if ((HashCoord(current.X, current.Y) & 3) == 0)
            {
                foreach (var n in GetNeighbors(current))
                {
                    if (Hexes[n].Terrain != OverworldHex.TerrainType.Water &&
                        _rng.Randf() < 0.5f)
                    {
                        Hexes[n].Terrain = OverworldHex.TerrainType.Water;
                        riverHexes.Add(n);
                        break; // widen by at most one hex per step
                    }
                }
            }

            // Step to the lowest-elevation unvisited neighbour (downhill flow).
            Vector2I next = current;
            float lowest = _field.SampleElevation01(current);
            bool found = false;

            foreach (var n in GetNeighbors(current))
            {
                if (visited.Contains(n))
                    continue;
                float e = _field.SampleElevation01(n);
                if (e < lowest)
                {
                    lowest = e;
                    next = n;
                    found = true;
                }
            }

            // Local minimum reached (a basin / lake) — stop the river here.
            if (!found || next == current)
                break;

            current = next;
        }

        // Carve crossings (fords / bridges) so roads and the party can cross.
        int crossingCount = Region?.RiverCrossingCount ?? 2;
        if (crossingCount > 0 && riverHexes.Count > 0)
        {
            int spacing = Mathf.Max(1, riverHexes.Count / (crossingCount + 1));
            for (int i = 1; i <= crossingCount; i++)
            {
                int idx = i * spacing;
                if (idx >= riverHexes.Count)
                    break;
                Hexes[riverHexes[idx]].Terrain = OverworldHex.TerrainType.Road;
            }
        }
    }

    /// <summary>Highest-elevation hex along the top or left edge — the river's headwater.</summary>
    private Vector2I FindRiverSource()
    {
        Vector2I best = new(0, 0);
        float bestElev = -1f;

        foreach (var kvp in Hexes)
        {
            var c = kvp.Key;
            bool onEdge = c.X == 0 || c.Y == 0;
            if (!onEdge)
                continue;

            float e = _field.SampleElevation01(c);
            if (e > bestElev)
            {
                bestElev = e;
                best = c;
            }
        }

        return best;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Road generation — connects entry to objective with branches
    // ═══════════════════════════════════════════════════════════════════════

    private void GenerateRoads()
    {
        // Main road: roughly horizontal mid-map spine from entry side to objective side.
        int roadR = GridHeight / 2;

        for (int q = 0; q < GridWidth; q++)
        {
            var coord = new Vector2I(q, roadR);
            if (Hexes.TryGetValue(coord, out var hex))
            {
                // Don't overwrite water — roads use the carved crossings instead.
                if (hex.Terrain != OverworldHex.TerrainType.Water)
                    hex.Terrain = OverworldHex.TerrainType.Road;
            }

            // Slight vertical wobble, seeded.
            int hash = HashCoord(q * 13, roadR);
            if (hash % 6 == 0 && roadR > GridHeight / 2 - 2)
                roadR--;
            else if (hash % 6 == 1 && roadR < GridHeight / 2 + 2)
                roadR++;
        }

        // Branch road going north from the first third.
        int branchQ = GridWidth / 3;
        for (int r = GridHeight / 2; r >= 2; r--)
        {
            var coord = new Vector2I(branchQ, r);
            if (Hexes.TryGetValue(coord, out var hex))
            {
                if (hex.Terrain != OverworldHex.TerrainType.Water)
                    hex.Terrain = OverworldHex.TerrainType.Road;
            }

            int hash = HashCoord(branchQ, r * 11);
            if (hash % 4 == 0)
                branchQ++;
        }

        // Branch road going south from the second third.
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
            if (hash % 4 == 0)
                branchQ--;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Mountain range — optional explicit barrier (region toggle)
    // ═══════════════════════════════════════════════════════════════════════

    private void GenerateMountainRange()
    {
        // Diagonal range from upper-center toward lower-right, with periodic passes.
        int q = GridWidth / 2 + 2;
        int r = 1;

        for (int i = 0; i < 8; i++)
        {
            var coord = new Vector2I(q, r);
            if (Hexes.TryGetValue(coord, out var hex))
            {
                if (hex.Terrain != OverworldHex.TerrainType.Water &&
                    hex.Terrain != OverworldHex.TerrainType.Road)
                {
                    hex.Terrain = OverworldHex.TerrainType.Mountain;
                }
            }

            // Thicken with one seeded neighbour.
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

            // Leave a pass every ~3 hexes so the range is navigable.
            if (i % 3 == 2)
            {
                r++;
                continue;
            }

            q++;
            r++;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Connectivity guarantee
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// BFS from entry to objective across passable terrain. Water and Mountain block the
    /// path. If the objective is unreachable, carve a Grassland corridor along the
    /// straightest hex line between them so a run can always be completed. Deterministic:
    /// the carve only depends on coordinates, so it reproduces with the seed.
    /// </summary>
    private void EnsureEntryToObjectiveConnectivity()
    {
        if (IsReachable(EntryCoord, ObjectiveCoord))
            return;

        GD.Print("Overworld: objective unreachable — carving guaranteed corridor.");

        foreach (var coord in HexLine(EntryCoord, ObjectiveCoord))
        {
            if (!Hexes.TryGetValue(coord, out var hex))
                continue;
            if (coord == ObjectiveCoord) // keep the objective's ArcaneGround tile
                continue;

            if (hex.Terrain == OverworldHex.TerrainType.Water ||
                hex.Terrain == OverworldHex.TerrainType.Mountain)
            {
                hex.Terrain = OverworldHex.TerrainType.Grassland;
            }
        }
    }

    /// <summary>True when a passable path exists between two coords (Water/Mountain block).</summary>
    private bool IsReachable(Vector2I start, Vector2I goal)
    {
        if (!Hexes.ContainsKey(start) || !Hexes.ContainsKey(goal))
            return false;

        var visited = new HashSet<Vector2I> { start };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == goal)
                return true;

            foreach (var n in GetNeighbors(current))
            {
                if (visited.Contains(n))
                    continue;
                if (IsBlockingTerrain(Hexes[n].Terrain) && n != goal)
                    continue;
                visited.Add(n);
                queue.Enqueue(n);
            }
        }

        return false;
    }

    private static bool IsBlockingTerrain(OverworldHex.TerrainType t) =>
        t == OverworldHex.TerrainType.Water || t == OverworldHex.TerrainType.Mountain;

    /// <summary>Cube-rounded line of axial coords between two hexes (inclusive of both ends).</summary>
    private List<Vector2I> HexLine(Vector2I a, Vector2I b)
    {
        var result = new List<Vector2I>();
        int n = Distance(a, b);
        if (n == 0)
        {
            result.Add(a);
            return result;
        }

        // Cube coords for endpoints.
        float ax = a.X, az = a.Y, ay = -ax - az;
        float bx = b.X, bz = b.Y, by = -bx - bz;

        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            float lx = Mathf.Lerp(ax, bx, t);
            float ly = Mathf.Lerp(ay, by, t);
            float lz = Mathf.Lerp(az, bz, t);

            int rx = Mathf.RoundToInt(lx);
            int ry = Mathf.RoundToInt(ly);
            int rz = Mathf.RoundToInt(lz);

            float dx = Mathf.Abs(rx - lx);
            float dy = Mathf.Abs(ry - ly);
            float dz = Mathf.Abs(rz - lz);

            if (dx > dy && dx > dz)
                rx = -ry - rz;
            else if (dy > dz)
                ry = -rx - rz;
            else
                rz = -rx - ry;

            result.Add(new Vector2I(rx, rz));
        }

        return result;
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
