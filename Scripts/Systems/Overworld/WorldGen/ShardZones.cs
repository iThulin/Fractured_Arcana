using Godot;
using System.Collections.Generic;

// ============================================================
// ShardZones.cs
//
// Purpose:        Sites the six shard acquisition SUB-REGIONS, one
//                 per fragment (axiom/binding/deathless/moment/
//                 schema/primal), near a non-convergence archmage
//                 seat ("the archmage guards the shard"). A zone is
//                 a contiguous tile FOOTPRINT (WorldTile.ShardZoneIndex
//                 back-ref into WorldData.ShardZones), the same AREA
//                 mechanism settlements use, but its OWN system, not
//                 a SettlementTier. This pass only decides WHERE the
//                 vaults are, how big, and which tile is the guardian
//                 GATE vs the inner SANCTUM. Reduced-fog + step
//                 behaviour, discovery, and the guardian/shard
//                 interaction are layered on later phases.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (ShardZoneIndex + ShardZones
//                 table), WorldGenerator (calls after settlements,
//                 before ScatterPois), HexCoord (neighbours/distance).
// See:            claude/shard_zone_refactor_plan_v1.md (P2)
// ============================================================

public static class ShardZones
{
    /// <summary>The six fragments. One sub-region each, round-robin over the
    /// non-convergence seats (wraps if fewer kingdoms than fragments).</summary>
    public static readonly string[] FragmentKeys =
        { "axiom", "binding", "deathless", "moment", "schema", "primal" };

    private static readonly Dictionary<string, string> ZoneNames = new()
    {
        { "axiom",     "The Infinite Athenaeum" },
        { "binding",   "The Bound Vault" },
        { "deathless", "The Deathless Reliquary" },
        { "moment",    "The Stilled Hour" },
        { "schema",    "The Pattern Sanctum" },
        { "primal",    "The Primal Heart" },
    };

    // ── Tuning ───────────────────────────────────────────────────────────
    /// <summary>Tiles per vault cluster.</summary>
    public static int FootprintSize = 7;
    /// <summary>Sited near the seat, but off the city itself.</summary>
    public static int SeatMinDist = 3;
    public static int SeatMaxDist = 7;

    // ── Entry ────────────────────────────────────────────────────────────
    public static void Generate(WorldData world,
        List<(int x, int y)> capitals, List<string> kingdomIds,
        string convergenceKingdom, RandomNumberGenerator rng)
    {
        var seats = new List<(string id, int x, int y)>();
        for (int k = 0; k < capitals.Count; k++)
        {
            string id = kingdomIds[k];
            if (id == convergenceKingdom)
                continue;
            seats.Add((id, capitals[k].x, capitals[k].y));
        }
        if (seats.Count == 0)
        {
            GD.PushWarning("[ShardZones] No non-convergence seats. No shard zones sited.");
            return;
        }

        int sited = 0;
        for (int f = 0; f < FragmentKeys.Length; f++)
        {
            // Try the round-robin seat first, then every other seat, so all six
            // fragments get a zone even when a small kingdom offers no anchor.
            bool placed = false;
            for (int a = 0; a < seats.Count && !placed; a++)
            {
                var (kid, sx, sy) = seats[(f + a) % seats.Count];
                placed = TrySiteZone(world, FragmentKeys[f], kid, sx, sy, rng);
            }
            if (placed)
                sited++;
            else
                GD.PushWarning($"[ShardZones] Could not site '{FragmentKeys[f]}' on any seat.");
        }

        GD.Print($"[ShardZones] Sited {sited}/{FragmentKeys.Length} shard sub-regions " +
                 $"across {seats.Count} seat(s).");
    }

    // ── Siting ───────────────────────────────────────────────────────────
    private static bool TrySiteZone(WorldData world, string fragKey, string kingdomId,
        int seatX, int seatY, RandomNumberGenerator rng)
    {
        // 1. Anchor: best kingdom-owned land tile in the [min,max] ring around the
        //    seat, off the city, off other zones. Bias toward wilder/corrupt ground.
        (int x, int y) anchor = default;
        float best = float.NegativeInfinity;
        bool found = false;
        for (int y = 0; y < world.Height; y++)
            for (int x = 0; x < world.Width; x++)
            {
                if (!CanClaim(world, x, y, kingdomId))
                    continue;
                int d = HexCoord.OffsetDistance(seatX, seatY, x, y);
                if (d < SeatMinDist || d > SeatMaxDist)
                    continue;
                float score = world.GetTile(x, y).Corruption * 0.5f + rng.Randf();
                if (score > best)
                { best = score; anchor = (x, y); found = true; }
            }
        if (!found)
            return false;

        // 2. Grow a compact footprint from the anchor.
        var zone = new ShardZone
        {
            FragmentKey = fragKey,
            KingdomId = kingdomId,
            Name = ZoneNames.TryGetValue(fragKey, out var nm) ? nm : "Shard Vault",
            CenterX = anchor.x,
            CenterY = anchor.y,
        };
        int idx = world.ShardZones.Count;
        Claim(world, anchor.x, anchor.y, idx, zone);

        var frontier = new List<(int x, int y)>();
        AddNeighbors(world, anchor, kingdomId, frontier);
        while (zone.Tiles.Count < FootprintSize && frontier.Count > 0)
        {
            (int x, int y) pick = default;
            float bestScore = float.NegativeInfinity;
            bool ok = false;
            for (int i = frontier.Count - 1; i >= 0; i--)
            {
                var (fx, fy) = frontier[i];
                if (!CanClaim(world, fx, fy, kingdomId))
                { frontier.RemoveAt(i); continue; }
                int own = 0;
                foreach (var (nx, ny) in HexCoord.Neighbors(fx, fy, world.Width, world.Height))
                    if (world.GetTile(nx, ny).ShardZoneIndex == idx)
                        own++;
                if (own > bestScore)
                { bestScore = own; pick = (fx, fy); ok = true; }
            }
            if (!ok)
                break;
            frontier.Remove(pick);
            Claim(world, pick.x, pick.y, idx, zone);
            AddNeighbors(world, pick, kingdomId, frontier);
        }

        // 3. Gate = footprint tile nearest the seat (the approach); Sanctum = the
        //    footprint tile farthest from the gate (the deepest). Both fall back to
        //    the anchor for a degenerate 1-tile zone.
        var gate = anchor;
        int gd = int.MaxValue;
        foreach (var (x, y) in zone.Tiles)
        {
            int d = HexCoord.OffsetDistance(seatX, seatY, x, y);
            if (d < gd) { gd = d; gate = (x, y); }
        }
        var sanctum = gate;
        int sd = -1;
        foreach (var (x, y) in zone.Tiles)
        {
            int d = HexCoord.OffsetDistance(gate.x, gate.y, x, y);
            if (d > sd) { sd = d; sanctum = (x, y); }
        }
        zone.GateX = gate.x; zone.GateY = gate.y;
        zone.SanctumX = sanctum.x; zone.SanctumY = sanctum.y;

        world.ShardZones.Add(zone);
        GD.Print($"[ShardZones] '{fragKey}' -> {zone.Tiles.Count} tiles in '{kingdomId}', " +
                 $"gate ({gate.x},{gate.y}) sanctum ({sanctum.x},{sanctum.y}).");
        return true;
    }

    // ── Claim helpers (mirror Settlements, but keyed on ShardZoneIndex) ───
    private static bool CanClaim(WorldData world, int x, int y, string kingdomId)
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
        if (t.ShardZoneIndex >= 0)
            return false;
        return t.KingdomId == kingdomId;
    }

    private static void Claim(WorldData world, int x, int y, int zoneIndex, ShardZone z)
    {
        int i = y * world.Width + x;
        var t = world.Tiles[i];
        t.ShardZoneIndex = zoneIndex;
        world.Tiles[i] = t;
        z.Tiles.Add((x, y));
    }

    private static void AddNeighbors(WorldData world, (int x, int y) c, string kingdomId,
        List<(int x, int y)> frontier)
    {
        foreach (var (nx, ny) in HexCoord.Neighbors(c.x, c.y, world.Width, world.Height))
            if (CanClaim(world, nx, ny, kingdomId))
                frontier.Add((nx, ny));
    }
}
