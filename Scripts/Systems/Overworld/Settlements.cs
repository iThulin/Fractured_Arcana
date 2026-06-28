using Godot;
using System.Collections.Generic;

// ============================================================
// Settlements.cs
//
// Purpose:        Grows City and Town AREAS across the world. A
//                 settlement is a contiguous region of tiles, NOT a
//                 POI — tiles keep their biome and carry a back-ref
//                 (WorldTile.SettlementIndex) into WorldData.Settlements.
//                 POIs are studded into these areas later by
//                 WorldGenerator.ScatterPois (cities dense, towns
//                 sparse); this pass only decides WHERE the cities and
//                 towns are and how big.
//
//                 Each non-convergence kingdom gets a primary City grown
//                 from its archmage SEAT (so every kingdom has at least
//                 one staging city — no staging gaps), an optional second
//                 City if the territory is large, and Towns scaled by
//                 territory size, placed by a suitability score (fresh
//                 water + flat ground) with spacing.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (SettlementIndex + Settlements
//                 table), Hydrology (river/lake data feeds suitability),
//                 WorldGenerator (caller; ScatterPois studs the result),
//                 HexCoord (neighbours / distance).
// Notes:          Runs AFTER terrain/hydrology/highlands and territory
//                 assignment, BEFORE ScatterPois. Deterministic from the
//                 world RNG.
// ============================================================

public static class Settlements
{
    // ── Tuning ───────────────────────────────────────────────────────────

    /// <summary>Target tile count for a City (grown; may fall short if hemmed in
    /// by water/mountain/other settlements).</summary>
    public static int CityTargetSize = 9;

    /// <summary>A kingdom larger than this (land tiles) earns a second City.</summary>
    public static int ExtraCityKingdomTiles = 650;

    /// <summary>Roughly one Town per this many kingdom land tiles.</summary>
    public static int TownPerKingdomTiles = 200;
    public static int MinTowns = 1, MaxTowns = 6;

    /// <summary>Town size range (1 = a single hamlet tile).</summary>
    public static int TownMinSize = 1, TownMaxSize = 3;

    /// <summary>Min hex distance between settlement centres, so cities/towns don't
    /// pile on top of each other.</summary>
    public static int SettlementSpacing = 4;

    // ── Entry ────────────────────────────────────────────────────────────

    public static void Generate(WorldData world,
        Dictionary<string, KingdomState> kingdoms,
        List<(int x, int y)> capitals, List<string> kingdomIds,
        string convergenceKingdom, RandomNumberGenerator rng)
    {
        // Per-kingdom land tile lists (one scan).
        var kingdomTiles = new Dictionary<string, List<(int x, int y)>>();
        foreach (var id in kingdomIds)
            kingdomTiles[id] = new List<(int x, int y)>();

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var t = world.GetTile(x, y);
                if (!string.IsNullOrEmpty(t.KingdomId) && kingdomTiles.ContainsKey(t.KingdomId))
                    kingdomTiles[t.KingdomId].Add((x, y));
            }
        }

        var centers = new List<(int x, int y)>();   // all settlement centres (spacing)

        for (int k = 0; k < capitals.Count; k++)
        {
            string id = kingdomIds[k];
            if (id == convergenceKingdom)
                continue;                            // Kassian's seat — no city (endgame zone)
            var tiles = kingdomTiles[id];
            if (tiles.Count == 0)
                continue;

            // Primary city anchored on the archmage seat.
            var cap = capitals[k];
            GrowSettlement(world, cap, CityTargetSize, id, SettlementTier.City, isSeat: true);
            centers.Add(cap);

            // Optional second city for large territories.
            if (tiles.Count > ExtraCityKingdomTiles &&
                TryPickCenter(world, tiles, centers, rng, out var c2))
            {
                GrowSettlement(world, c2, CityTargetSize, id, SettlementTier.City, isSeat: false);
                centers.Add(c2);
            }

            // Towns scaled by territory size.
            int townCount = Mathf.Clamp(tiles.Count / TownPerKingdomTiles, MinTowns, MaxTowns);
            for (int t = 0; t < townCount; t++)
            {
                if (!TryPickCenter(world, tiles, centers, rng, out var tc))
                    break;
                int size = TownMinSize + (int)(rng.Randi() % (uint)(TownMaxSize - TownMinSize + 1));
                GrowSettlement(world, tc, size, id, SettlementTier.Town, isSeat: false);
                centers.Add(tc);
            }
        }

        GD.Print($"[Settlements] {world.Settlements.Count} settlements " +
                 $"({CountTier(world, SettlementTier.City)} cities, " +
                 $"{CountTier(world, SettlementTier.Town)} towns).");
    }

    // ── Growth ───────────────────────────────────────────────────────────

    /// <summary>Force-claims the centre (even if it's a mountain seat), then
    /// frontier-grows toward low/flat ground in the same kingdom up to targetSize.</summary>
    private static void GrowSettlement(WorldData world, (int x, int y) center, int targetSize,
        string kingdomId, SettlementTier tier, bool isSeat)
    {
        int idx = world.Settlements.Count;
        var s = new WorldSettlement
        {
            Tier = tier,
            CenterX = center.x,
            CenterY = center.y,
            KingdomId = kingdomId,
            GrantsStaging = tier == SettlementTier.City,
            IsSeat = isSeat,
        };

        Claim(world, center.x, center.y, idx, s);    // centre always claimed

        var frontier = new List<(int x, int y)>();
        AddNeighbors(world, center, kingdomId, frontier);

        while (s.Tiles.Count < targetSize && frontier.Count > 0)
        {
            // Score each valid frontier tile: COMPACTNESS dominates (how many of its
            // neighbours are already ours — fill gaps, don't extrude), flatness is a
            // weak tie-breaker. Prune invalid tiles in the same downward scan; select
            // by VALUE so RemoveAt can't invalidate the choice.
            (int x, int y) pick = default;
            float bestScore = float.NegativeInfinity;
            bool found = false;

            for (int i = frontier.Count - 1; i >= 0; i--)
            {
                var (fx, fy) = frontier[i];
                if (!CanSettle(world, fx, fy, kingdomId))
                {
                    frontier.RemoveAt(i);
                    continue;
                }

                int ownNeighbors = 0;
                foreach (var (nx, ny) in HexCoord.Neighbors(fx, fy, world.Width, world.Height))
                    if (world.GetTile(nx, ny).SettlementIndex == idx)
                        ownNeighbors++;

                // Compactness is the driver (0..6); flatness only breaks ties.
                float score = ownNeighbors + (1f - world.GetTile(fx, fy).Elevation) * 0.25f;
                if (score > bestScore)
                {
                    bestScore = score;
                    pick = (fx, fy);
                    found = true;
                }
            }
            if (!found)
                break;

            frontier.Remove(pick);   // remove by value — position-independent
            Claim(world, pick.x, pick.y, idx, s);
            AddNeighbors(world, pick, kingdomId, frontier);
        }

        world.Settlements.Add(s);
    }

    private static void Claim(WorldData world, int x, int y, int settlementIndex, WorldSettlement s)
    {
        int i = y * world.Width + x;
        var t = world.Tiles[i];
        t.SettlementIndex = settlementIndex;
        world.Tiles[i] = t;
        s.Tiles.Add((x, y));
    }

    private static void AddNeighbors(WorldData world, (int x, int y) c, string kingdomId,
        List<(int x, int y)> frontier)
    {
        foreach (var (nc, nr) in HexCoord.Neighbors(c.x, c.y, world.Width, world.Height))
            if (CanSettle(world, nc, nr, kingdomId))
                frontier.Add((nc, nr));   // dups are filtered at pick-time
    }

    /// <summary>A tile a settlement may spread onto: land, not a mountain peak, not
    /// already claimed, in the same kingdom. (The centre bypasses this so a seat on
    /// a peak still anchors its city.)</summary>
    private static bool CanSettle(WorldData world, int x, int y, string kingdomId)
    {
        if (!world.InBounds(x, y))
            return false;
        var t = world.GetTile(x, y);
        if (!t.IsLand)
            return false;
        if (t.Terrain == OverworldHex.TerrainType.Mountain)
            return false;
        if (t.SettlementIndex >= 0)
            return false;
        return t.KingdomId == kingdomId;
    }

    // ── Centre selection (towns / extra cities) ──────────────────────────

    /// <summary>Picks the highest-suitability settle-able tile in the kingdom that
    /// is at least SettlementSpacing from every existing centre. Returns false if
    /// none qualifies (small/crowded kingdom).</summary>
    private static bool TryPickCenter(WorldData world, List<(int x, int y)> tiles,
        List<(int x, int y)> centers, RandomNumberGenerator rng, out (int x, int y) center)
    {
        center = default;
        float bestScore = float.NegativeInfinity;
        bool found = false;

        foreach (var (x, y) in tiles)
        {
            if (!CanSettle(world, x, y, world.GetTile(x, y).KingdomId))
                continue;
            if (TooClose(centers, x, y))
                continue;

            float score = Suitability(world, x, y)
                          + (rng.Randf() * 0.25f);   // tiny jitter to break ties / vary seeds
            if (score > bestScore)
            { bestScore = score; center = (x, y); found = true; }
        }
        return found;
    }

    /// <summary>Higher = better townsite: flat, on/near fresh water or coast, not
    /// swamp, mild penalty for hills.</summary>
    private static float Suitability(WorldData world, int x, int y)
    {
        var t = world.GetTile(x, y);
        float s = (1f - t.Elevation);                 // flatter is better

        if (t.RiverEdges != 0)
            s += 1.5f;                                // riverside

        bool nearWater = false;
        foreach (var (nc, nr) in HexCoord.Neighbors(x, y, world.Width, world.Height))
        {
            var nt = world.GetTile(nc, nr);
            if (nt.IsLake || nt.IsCoast || nt.IsOcean)
            { nearWater = true; break; }
        }
        if (nearWater)
            s += 0.8f;

        if (t.Terrain == OverworldHex.TerrainType.Swamp)
            s -= 1.0f;
        if (t.Terrain == OverworldHex.TerrainType.Hills)
            s -= 0.3f;

        return s;
    }

    private static bool TooClose(List<(int x, int y)> centers, int x, int y)
    {
        foreach (var (cx, cy) in centers)
            if (HexCoord.OffsetDistance(cx, cy, x, y) < SettlementSpacing)
                return true;
        return false;
    }

    private static int CountTier(WorldData world, SettlementTier tier)
    {
        int c = 0;
        foreach (var s in world.Settlements)
            if (s.Tier == tier)
                c++;
        return c;
    }
}
