using System;
using System.Collections.Generic;

// ============================================================
// CastleHops.cs
//
// Purpose:        The castle's march on the province graph (docs/
//                 provinces_v1_9_bastion.md, slice PV4; P4, M1). Where
//                 the world has provinces, a march is a chain of hops
//                 from province to province along the cheapest route,
//                 animated from seat to seat. A hop costs HopBase fuel,
//                 more to enter rough ground, more where the border can
//                 only be crossed by fording a river or climbing a ridge,
//                 less where a road crosses it (PR67, PR68). The castle
//                 crosses where it is cheapest, so a border with one dry,
//                 low edge has a pass. The chassis signatures carry over
//                 (PR69): cheap ground, the ford waived, the road doubled,
//                 the hourglass's first hop flat. A march inside the
//                 castle's own province costs InProvinceCost. A world
//                 without provinces, or ground outside every province,
//                 keeps the old flat march (CastleMarch).
// Layer:          System (strategic)
// Collaborators:  CastleMarch.cs (CostTo, CanMarchTo and Execute read the
//                 plan), CastleManeuvers.cs (Force Path, Verdant
//                 Passage), CastleTypeDef.cs (the chassis), Province.cs
//                 (Neighbors, Character, the seat), WorldData.cs (rivers,
//                 roads, elevation), StrategicView.Bastion.cs (the route
//                 in the readout, the confirm and the animation),
//                 ProvinceSaveAssert.Bastion.cs
// See:            docs/provinces_v1_9_bastion.md sections 3 and 4
// ============================================================

/// <summary>One hop of a march: the province entered and what it costs.</summary>
public readonly struct MarchHop
{
    /// <summary>The province entered (its index).</summary>
    public readonly int Province;

    /// <summary>Fuel for the hop, never below 1.</summary>
    public readonly int Cost;

    /// <summary>The ground's part of the cost (negative for the chassis's cheap ground).</summary>
    public readonly int Entry;

    /// <summary>The border's part: a ford, a ridge, a road (negative).</summary>
    public readonly int Crossing;

    /// <summary>Why it costs what it costs, for the confirm: "woodland +1, a ford +3".</summary>
    public readonly string Why;

    public MarchHop(int province, int cost, int entry, int crossing, string why)
    {
        Province = province;
        Cost = cost;
        Entry = entry;
        Crossing = crossing;
        Why = why ?? "";
    }
}

/// <summary>A march on the province graph: the hops, the fuel, and the waypoints the
/// castle is seen to pass (its tile, the seats between, the destination).</summary>
public sealed class MarchPlan
{
    public int FromProvince = -1;
    public int ToProvince = -1;

    /// <summary>False when no land route of provinces reaches the destination.</summary>
    public bool Reachable = true;

    /// <summary>Fuel for the whole march.</summary>
    public int Fuel;

    public readonly List<MarchHop> Hops = new();
    public readonly List<(int X, int Y)> Waypoints = new();
}

/// <summary>Hop costs and routes for the castle's march (PV4).</summary>
public static class CastleHops
{
    // ── Tuning (section 4, PR67 and PR68) ────────────────────────────────

    /// <summary>Fuel for a hop into lowland across a border with a dry, low crossing.</summary>
    public const int HopBase = 8;

    /// <summary>More where every crossing of the border fords a river (no bridge, no dry edge).</summary>
    public const int FordCost = 3;

    /// <summary>More where every crossing of the border climbs high ground (the hills line or above).</summary>
    public const int RidgeCost = 3;

    /// <summary>Less where a road crosses the border (the Tinker's chassis doubles it).</summary>
    public const int RoadDiscount = 1;

    /// <summary>A march inside the castle's own province.</summary>
    public const int InProvinceCost = 2;

    /// <summary>The ground's surcharge on a hop into a province of that character.</summary>
    public static int EntryCost(ProvinceCharacter character) => character switch
    {
        ProvinceCharacter.Woodland => 1,
        ProvinceCharacter.Highland => 1,
        ProvinceCharacter.Arid => 1,
        ProvinceCharacter.Ruined => 1,
        ProvinceCharacter.Wetland => 2,
        ProvinceCharacter.Volcanic => 2,
        ProvinceCharacter.Mountain => 3,
        ProvinceCharacter.Frozen => 3,
        _ => 0,
    };

    /// <summary>The last march's plan, set by CastleMarch.Execute so the view can animate the
    /// route the castle was charged for. Presentation only; never saved.</summary>
    public static MarchPlan LastPlan;

    // ══════════════════════════════════════════════════════════════════════
    // The chassis
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The castle's chassis: the cycle's school, else the session's.</summary>
    public static CastleTypeDef Chassis(CycleState cycle)
    {
        if (cycle != null && Enum.TryParse<CardSchool>(cycle.SelectedSchool ?? "", true, out var school))
            return CastleTypes.For(school);
        return CastleTypes.For(PlayerSession.SelectedSchool);
    }

    /// <summary>True when the chassis strides cheaply over ground of this character.</summary>
    public static bool IsCheap(CastleTypeDef chassis, ProvinceCharacter character)
    {
        if (chassis?.CheapTerrains == null || chassis.TerrainDiscount <= 0)
            return false;
        foreach (var terrain in chassis.CheapTerrains)
        {
            if (GroundOf(terrain) == character)
                return true;
        }
        return false;
    }

    /// <summary>The ground a terrain counts as, as ProvinceBuilder reads a province's character.</summary>
    private static ProvinceCharacter GroundOf(OverworldHex.TerrainType terrain) => terrain switch
    {
        OverworldHex.TerrainType.Forest => ProvinceCharacter.Woodland,
        OverworldHex.TerrainType.Hills => ProvinceCharacter.Highland,
        OverworldHex.TerrainType.Mountain => ProvinceCharacter.Mountain,
        OverworldHex.TerrainType.Swamp => ProvinceCharacter.Wetland,
        OverworldHex.TerrainType.Marsh => ProvinceCharacter.Wetland,
        OverworldHex.TerrainType.Desert => ProvinceCharacter.Arid,
        OverworldHex.TerrainType.Volcanic => ProvinceCharacter.Volcanic,
        OverworldHex.TerrainType.Tundra => ProvinceCharacter.Frozen,
        OverworldHex.TerrainType.Snow => ProvinceCharacter.Frozen,
        OverworldHex.TerrainType.ArcaneGround => ProvinceCharacter.Arcane,
        OverworldHex.TerrainType.Ruins => ProvinceCharacter.Ruined,
        _ => ProvinceCharacter.Lowland,
    };

    // ══════════════════════════════════════════════════════════════════════
    // Borders
    // ══════════════════════════════════════════════════════════════════════

    // A border's crossings, read once per world: for each pair of provinces, a bit for each
    // kind of edge they share (1 << kind, kind = river 1 | ridge 2 | road 4).
    private static WorldData _bordersWorld;
    private static Dictionary<long, int> _borders;

    /// <summary>Forget the cached borders: call after changing rivers, roads or the province
    /// map of a world already read.</summary>
    public static void ResetBorders()
    {
        _bordersWorld = null;
        _borders = null;
    }

    private static long Key(int a, int b)
        => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static Dictionary<long, int> Borders(WorldData world)
    {
        if (ReferenceEquals(world, _bordersWorld) && _borders != null)
            return _borders;
        var map = new Dictionary<long, int>();
        int w = world.Width, h = world.Height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int a = world.ProvinceIndexAt(x, y);
                if (a < 0)
                    continue;
                var tile = world.Tiles[y * w + x];
                var (q, r) = HexCoord.OffsetToAxial(x, y);
                for (int d = 0; d < 6; d++)
                {
                    var (dq, dr) = HexCoord.AxialDirections[d];
                    var (nx, ny) = HexCoord.AxialToOffset(q + dq, r + dr);
                    if (!world.InBounds(nx, ny))
                        continue;
                    int b = world.ProvinceIndexAt(nx, ny);
                    if (b < 0 || b == a)
                        continue;
                    var other = world.Tiles[ny * w + nx];
                    bool road = (tile.RoadEdges & (1 << d)) != 0;
                    bool river = (tile.RiverEdges & (1 << d)) != 0 && !road;   // a road over a river is a bridge
                    bool ridge = Math.Max(tile.Elevation, other.Elevation) >= MountainShaper.HillsFloor;
                    int kind = (river ? 1 : 0) | (ridge ? 2 : 0) | (road ? 4 : 0);
                    long key = Key(a, b);
                    map.TryGetValue(key, out int seen);
                    map[key] = seen | (1 << kind);
                }
            }
        }
        _bordersWorld = world;
        _borders = map;
        return map;
    }

    /// <summary>The border's best crossing for this chassis: the cheapest kind of edge the two
    /// provinces share, and that kind.</summary>
    private static int Crossing(int kinds, CastleTypeDef chassis, bool forcePath, out int bestKind)
    {
        int best = int.MaxValue;
        bestKind = 0;
        for (int kind = 0; kind < 8; kind++)
        {
            if ((kinds & (1 << kind)) == 0)
                continue;
            int c = 0;
            if ((kind & 1) != 0 && !chassis.WaiveFord && !forcePath)
                c += FordCost;
            if ((kind & 2) != 0 && !forcePath)
                c += RidgeCost;
            if ((kind & 4) != 0)
                c -= RoadDiscount + chassis.ExtraRoadDiscount;
            if (c < best)
            {
                best = c;
                bestKind = kind;
            }
        }
        return best == int.MaxValue ? 0 : best;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Hops and routes
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>One hop from province <paramref name="from"/> into its neighbour
    /// <paramref name="to"/>. The first hop of a march is flat for the hourglass's chassis.</summary>
    public static MarchHop Hop(CycleState cycle, int from, int to, CastleTypeDef chassis, bool first,
                               bool forcePath, bool verdant)
    {
        var world = cycle.World;
        var dest = world.Provinces[to];
        if (first && chassis.ChronoFlatMoves > 0)
            return new MarchHop(to, HopBase, 0, 0, "the hourglass's first hop is flat");
        int entry = EntryCost(dest.Character);
        if (verdant && (dest.Character == ProvinceCharacter.Woodland || dest.Character == ProvinceCharacter.Wetland))
            entry = 0;
        int kinds = Borders(world).TryGetValue(Key(from, to), out int seen) ? seen : 1;
        int crossing = Crossing(kinds, chassis, forcePath, out int kind);
        bool cheap = IsCheap(chassis, dest.Character);
        int ground = cheap ? -chassis.TerrainDiscount : entry;
        int cut = KeeperHunt.SupplyCut(cycle, to);   // provinces PV6: roads the Keeper has fouled
        int cost = Math.Max(1, HopBase + ground + crossing + cut);

        var why = new List<string>();
        string groundName = dest.Character.ToString().ToLowerInvariant();
        if (cheap)
            why.Add($"{groundName}, the chassis's ground -{chassis.TerrainDiscount}");
        else if (entry > 0)
            why.Add($"{groundName} +{entry}");
        if ((kind & 1) != 0 && !chassis.WaiveFord && !forcePath)
            why.Add($"a ford +{FordCost}");
        if ((kind & 2) != 0 && !forcePath)
            why.Add($"a ridge +{RidgeCost}");
        if ((kind & 4) != 0)
            why.Add($"a road -{RoadDiscount + chassis.ExtraRoadDiscount}");
        if (cut > 0)
            why.Add($"the Keeper's fouled roads +{cut}");
        return new MarchHop(to, cost, ground, crossing, string.Join(", ", why));
    }

    /// <summary>The march from the castle's tile to (x, y), or null where the old flat march
    /// applies: a world without provinces, or either end outside every province.</summary>
    public static MarchPlan Plan(CycleState cycle, int toX, int toY)
        => cycle == null ? null : Plan(cycle, cycle.CastleX, cycle.CastleY, toX, toY, Chassis(cycle));

    /// <summary>The march from (fromX, fromY) to (toX, toY) for a chassis, with the maneuvers
    /// armed now (Force Path, Verdant Passage). The route is the cheapest; ties go to fewer
    /// hops, then to the lower province indices in order.</summary>
    public static MarchPlan Plan(CycleState cycle, int fromX, int fromY, int toX, int toY, CastleTypeDef chassis)
    {
        var world = cycle?.World;
        if (world == null || !world.HasProvinces() || chassis == null)
            return null;
        int from = world.ProvinceIndexAt(fromX, fromY);
        int to = world.ProvinceIndexAt(toX, toY);
        if (from < 0 || to < 0)
            return null;
        var plan = new MarchPlan { FromProvince = from, ToProvince = to };
        plan.Waypoints.Add((fromX, fromY));
        if (from == to)
        {
            plan.Fuel = InProvinceCost;
            plan.Waypoints.Add((toX, toY));
            return plan;
        }
        bool forcePath = CastleManeuvers.ForcePathArmed(cycle);
        bool verdant = CastleManeuvers.VerdantActive(cycle);
        var route = Route(cycle, from, to, chassis, forcePath, verdant);
        if (route == null)
        {
            plan.Reachable = false;
            return plan;
        }
        int prev = from;
        for (int i = 0; i < route.Count; i++)
        {
            var hop = Hop(cycle, prev, route[i], chassis, i == 0, forcePath, verdant);
            plan.Hops.Add(hop);
            plan.Fuel += hop.Cost;
            if (i < route.Count - 1)
            {
                var seat = world.Provinces[route[i]];
                plan.Waypoints.Add((seat.SeatX, seat.SeatY));
            }
            prev = route[i];
        }
        plan.Waypoints.Add((toX, toY));
        return plan;
    }

    /// <summary>The cheapest route of provinces from one to another, the start left out; null
    /// when none reaches it. Dijkstra over the province graph, its ties broken by fewer hops,
    /// then by the route's indices in order, so the same world always marches the same way.</summary>
    private static List<int> Route(CycleState cycle, int from, int to, CastleTypeDef chassis, bool forcePath, bool verdant)
    {
        var provinces = cycle.World.Provinces;
        int n = provinces.Count;
        var cost = new int[n];
        var hops = new int[n];
        var path = new List<int>[n];
        var done = new bool[n];
        for (int i = 0; i < n; i++)
            cost[i] = int.MaxValue;
        cost[from] = 0;
        path[from] = new List<int>();
        while (true)
        {
            int u = -1;
            for (int i = 0; i < n; i++)
            {
                if (done[i] || cost[i] == int.MaxValue)
                    continue;
                if (u < 0 || Less(cost[i], hops[i], path[i], cost[u], hops[u], path[u]))
                    u = i;
            }
            if (u < 0)
                return null;
            if (u == to)
                return path[u];
            done[u] = true;
            if (provinces[u] == null)
                continue;
            foreach (int v in provinces[u].Neighbors)
            {
                if (v < 0 || v >= n || done[v] || provinces[v] == null)
                    continue;
                int c = cost[u] + Hop(cycle, u, v, chassis, hops[u] == 0, forcePath, verdant).Cost;
                var candidate = new List<int>(path[u]) { v };
                if (cost[v] == int.MaxValue || Less(c, hops[u] + 1, candidate, cost[v], hops[v], path[v]))
                {
                    cost[v] = c;
                    hops[v] = hops[u] + 1;
                    path[v] = candidate;
                }
            }
        }
    }

    private static bool Less(int c1, int h1, List<int> p1, int c2, int h2, List<int> p2)
    {
        if (c1 != c2)
            return c1 < c2;
        if (h1 != h2)
            return h1 < h2;
        int m = Math.Min(p1.Count, p2.Count);
        for (int i = 0; i < m; i++)
        {
            if (p1[i] != p2[i])
                return p1[i] < p2[i];
        }
        return p1.Count < p2.Count;
    }

    /// <summary>Hops between two provinces on the graph, whatever they cost; -1 when none.</summary>
    public static int HopsBetween(WorldData world, int from, int to)
    {
        if (world?.Provinces == null || from < 0 || to < 0 || from >= world.Provinces.Count || to >= world.Provinces.Count)
            return -1;
        var dist = new int[world.Provinces.Count];
        for (int i = 0; i < dist.Length; i++)
            dist[i] = -1;
        dist[from] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            if (u == to)
                return dist[u];
            foreach (int v in world.Provinces[u].Neighbors)
            {
                if (v < 0 || v >= dist.Length || dist[v] >= 0)
                    continue;
                dist[v] = dist[u] + 1;
                queue.Enqueue(v);
            }
        }
        return -1;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Where the castle stands
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The province the castle stands in, or -1.</summary>
    public static int CastleProvince(CycleState cycle)
        => cycle?.World == null || !cycle.World.HasProvinces() ? -1 : cycle.World.ProvinceIndexAt(cycle.CastleX, cycle.CastleY);

    /// <summary>Where the castle makes camp in a province it leaps or sends a forward camp to:
    /// the land tile nearest the seat with no settlement, place or staging on it (lower index
    /// on a tie), else the seat.</summary>
    public static (int X, int Y) CampTile(WorldData world, Province province)
    {
        if (world == null || province == null)
            return (-1, -1);
        int best = -1, bestDist = int.MaxValue;
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            int x = i % world.Width, y = i / world.Width;
            if (world.ProvinceIndexAt(x, y) != province.Index)
                continue;
            var tile = world.Tiles[i];
            if (tile.IsWater || tile.SettlementIndex >= 0 || tile.PoiIndex >= 0 || tile.IsStagingPoint)
                continue;
            int dist = world.HexDistance(x, y, province.SeatX, province.SeatY);
            if (dist < bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }
        return best < 0 ? (province.SeatX, province.SeatY) : (best % world.Width, best / world.Width);
    }

    /// <summary>Write Charted along the plan's route, leg by leg, as the flat march charts its
    /// corridor.</summary>
    public static void ChartRoute(WorldData world, MarchPlan plan)
    {
        if (world == null || plan == null)
            return;
        for (int i = 1; i < plan.Waypoints.Count; i++)
        {
            var (ax, ay) = plan.Waypoints[i - 1];
            var (bx, by) = plan.Waypoints[i];
            CastleMarch.ChartCorridor(world, ax, ay, bx, by);
        }
    }

    /// <summary>The route in words for the confirm: one line a hop.</summary>
    public static string Describe(WorldData world, MarchPlan plan)
    {
        if (world == null || plan == null)
            return "";
        if (plan.Hops.Count == 0)
            return $"Within {ProvinceControl.Name(world.Provinces[plan.ToProvince])}: {plan.Fuel} fuel.";
        var lines = new List<string>();
        foreach (var hop in plan.Hops)
        {
            string name = ProvinceControl.Name(world.Provinces[hop.Province]);
            lines.Add(hop.Why.Length > 0 ? $"  {name}: {hop.Cost} ({hop.Why})" : $"  {name}: {hop.Cost}");
        }
        return string.Join("\n", lines);
    }
}
