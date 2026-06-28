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
//
//                 AddJunctionTowns (called AFTER Roads) drops an extra
//                 Town at every road convergence — a tile with roads on
//                 3+ edges — regardless of the per-kingdom town cap.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (SettlementIndex + Settlements
//                 table), Hydrology (river/lake data feeds suitability),
//                 Roads (junction towns read RoadEdges), WorldGenerator,
//                 HexCoord (neighbours / distance).
// ============================================================

public static class Settlements
{
    // ── Tuning ───────────────────────────────────────────────────────────

    public static int CityTargetSize = 9;
    public static int ExtraCityKingdomTiles = 650;
    public static int TownPerKingdomTiles = 200;
    public static int MinTowns = 1, MaxTowns = 6;
    public static int TownMinSize = 1, TownMaxSize = 3;
    public static int SettlementSpacing = 4;

    /// <summary>Size of a town grown at a road junction.</summary>
    public static int JunctionTownSize = 2;
    /// <summary>Junction towns ignore the per-kingdom cap but still keep this much
    /// distance from existing settlements, so they don't pile onto a city.</summary>
    public static int JunctionMinSpacing = 2;

    // ── Entry ────────────────────────────────────────────────────────────

    public static void Generate(WorldData world,
        Dictionary<string, KingdomState> kingdoms,
        List<(int x, int y)> capitals, List<string> kingdomIds,
        string convergenceKingdom, RandomNumberGenerator rng)
    {
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

        var centers = new List<(int x, int y)>();

        for (int k = 0; k < capitals.Count; k++)
        {
            string id = kingdomIds[k];
            if (id == convergenceKingdom)
                continue;
            var tiles = kingdomTiles[id];
            if (tiles.Count == 0)
                continue;

            var cap = capitals[k];
            GrowSettlement(world, cap, CityTargetSize, id, SettlementTier.City, isSeat: true);
            centers.Add(cap);

            if (tiles.Count > ExtraCityKingdomTiles &&
                TryPickCenter(world, tiles, centers, rng, out var c2))
            {
                GrowSettlement(world, c2, CityTargetSize, id, SettlementTier.City, isSeat: false);
                centers.Add(c2);
            }

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

    // ── Junction towns (called after Roads) ──────────────────────────────

    /// <summary>Drops a Town at every road junction — a tile carrying roads on 3+
    /// edges, i.e. where road paths converge — ignoring the per-kingdom town cap but
    /// keeping JunctionMinSpacing from existing settlements.</summary>
    public static void AddJunctionTowns(WorldData world)
    {
        var centers = new List<(int x, int y)>();
        foreach (var s in world.Settlements)
            centers.Add((s.CenterX, s.CenterY));

        var junctions = new List<(int x, int y)>();
        for (int y = 0; y < world.Height; y++)
            for (int x = 0; x < world.Width; x++)
            {
                var t = world.GetTile(x, y);
                if (t.SettlementIndex >= 0 || string.IsNullOrEmpty(t.KingdomId))
                    continue;
                if (PopCount(t.RoadEdges) >= 3)
                    junctions.Add((x, y));
            }

        int added = 0;
        foreach (var (x, y) in junctions)
        {
            if (TooClose(centers, x, y, JunctionMinSpacing))
                continue;
            string id = world.GetTile(x, y).KingdomId;
            GrowSettlement(world, (x, y), JunctionTownSize, id, SettlementTier.Town, isSeat: false);
            centers.Add((x, y));
            added++;
        }

        GD.Print($"[Settlements] +{added} junction towns at road convergences.");
    }

    // ── Growth ───────────────────────────────────────────────────────────

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

        Claim(world, center.x, center.y, idx, s);

        var frontier = new List<(int x, int y)>();
        AddNeighbors(world, center, kingdomId, frontier);

        while (s.Tiles.Count < targetSize && frontier.Count > 0)
        {
            // Compactness drives the shape (fill gaps, don't extrude); flatness is a
            // weak tie-breaker. Prune invalid tiles in the downward scan; select by
            // VALUE so RemoveAt can't invalidate the choice.
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

            frontier.Remove(pick);
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
                frontier.Add((nc, nr));
    }

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

    // ── Centre selection ─────────────────────────────────────────────────

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

            float score = Suitability(world, x, y) + (rng.Randf() * 0.25f);
            if (score > bestScore)
            { bestScore = score; center = (x, y); found = true; }
        }
        return found;
    }

    private static float Suitability(WorldData world, int x, int y)
    {
        var t = world.GetTile(x, y);
        float s = (1f - t.Elevation);

        if (t.RiverEdges != 0)
            s += 1.5f;

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
        => TooClose(centers, x, y, SettlementSpacing);

    private static bool TooClose(List<(int x, int y)> centers, int x, int y, int spacing)
    {
        foreach (var (cx, cy) in centers)
            if (HexCoord.OffsetDistance(cx, cy, x, y) < spacing)
                return true;
        return false;
    }

    private static int PopCount(byte b)
    {
        int c = 0;
        while (b != 0)
        { c += b & 1; b >>= 1; }
        return c;
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
