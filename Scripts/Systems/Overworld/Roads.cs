using Godot;
using System.Collections.Generic;

// ============================================================
// Roads.cs
//
// Purpose:        Connects settlements with roads. Builds a minimum
//                 spanning forest over the settlements (Kruskal on
//                 pairwise least-cost distances, so each landmass's
//                 settlements connect and ocean-separated ones don't),
//                 then traces each link with A* over a terrain cost
//                 field and stamps the ROAD onto the EDGES it crosses.
//
//                 Roads are EDGES (WorldTile.RoadEdges), same 6-bit /
//                 both-sides convention as rivers. They never overwrite
//                 terrain, so a road runs through a city over its real
//                 biome. A road edge that coincides with a river edge IS
//                 a bridge (tile.BridgeEdges = RoadEdges & RiverEdges,
//                 derived) — fast to cross, no ford penalty.
//
//                 Links are stamped shortest-first and each A* reads the
//                 CURRENT road edges, so later roads merge onto earlier
//                 ones into trunk roads and converge on the same river
//                 crossings instead of fording fresh everywhere.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (RoadEdges out; RiverEdges +
//                 SettlementIndex + Terrain in), Settlements (must run
//                 first), Hydrology (river edges), HexCoord, WorldGenerator.
// Notes:          Runs AFTER Settlements, BEFORE ScatterPois. Deterministic.
//                 Roads are unrendered until the edge render pass — the
//                 [Roads] print is the only feedback until then.
// ============================================================

public static class Roads
{
    /// <summary>Extra cost to cross a river edge without a bridge. Higher = roads
    /// detour harder to minimise crossings (concentrating them at bridges).</summary>
    public static float RiverCrossingPenalty = 6f;

    /// <summary>Cost of moving ALONG an existing road edge. Below the cheapest
    /// terrain so later links snap onto built roads and form trunks.</summary>
    public static float RoadStepCost = 1f;

    public static void Generate(WorldData world)
    {
        int w = world.Width;

        var centers = new List<int>();
        foreach (var s in world.Settlements)
            centers.Add(s.CenterY * w + s.CenterX);

        int m = centers.Count;
        if (m < 2)
        {
            GD.Print($"[Roads] {m} settlement(s) — nothing to connect.");
            return;
        }

        // ── Pairwise least-cost distances (pre-road) ─────────────────────
        var dist = new float[m][];
        for (int i = 0; i < m; i++)
            dist[i] = DijkstraToCenters(world, centers[i], centers);

        // ── Kruskal MST over reachable pairs ─────────────────────────────
        var edges = new List<(float wgt, int a, int b)>();
        for (int i = 0; i < m; i++)
            for (int j = i + 1; j < m; j++)
            {
                float d = dist[i][j];
                if (!float.IsInfinity(d))
                    edges.Add((d, i, j));
            }
        edges.Sort((x, y) => x.wgt.CompareTo(y.wgt));

        var uf = new int[m];
        for (int i = 0; i < m; i++) uf[i] = i;
        int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }

        // ── Stamp links shortest-first (incremental → roads merge) ───────
        int built = 0, roadEdges = 0, bridges = 0;
        foreach (var (wgt, a, b) in edges)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb)
                continue;
            uf[ra] = rb;

            var path = AStar(world, centers[a], centers[b]);
            if (path == null)
                continue;

            StampRoad(world, path, ref roadEdges, ref bridges);
            built++;
        }

        GD.Print($"[Roads] {built} links over {m} settlements: " +
                 $"{roadEdges} road edges, {bridges} bridges.");
    }

    // ── Stamping (edges, both sides) ─────────────────────────────────────

    private static void StampRoad(WorldData world, List<int> path,
                                  ref int roadEdges, ref int bridges)
    {
        for (int k = 0; k < path.Count - 1; k++)
        {
            int a = path[k], b = path[k + 1];
            int d = DirBetween(world, a, b);
            if (d < 0)
                continue;

            var ta = world.Tiles[a];
            if ((ta.RoadEdges & (byte)(1 << d)) != 0)
                continue;                       // already a road edge — reused, not recounted

            bool bridge = (ta.RiverEdges & (byte)(1 << d)) != 0;   // road crossing a river
            int opp = (d + 3) % 6;

            ta.RoadEdges |= (byte)(1 << d);
            world.Tiles[a] = ta;
            var tb = world.Tiles[b];
            tb.RoadEdges |= (byte)(1 << opp);
            world.Tiles[b] = tb;

            roadEdges++;
            if (bridge)
                bridges++;
        }
    }

    // ── Cost model ───────────────────────────────────────────────────────

    private static bool Traversable(WorldData world, int idx) => world.Tiles[idx].IsLand;

    /// <summary>Per-destination-cell cost for OFF-road steps. Settlements are cheap
    /// so paths hub through towns; mountains costly but finite, so roads cross ranges
    /// only at the cheapest saddle. Roads themselves are edges, not terrain — their
    /// cheapness lives in StepCost, not here.</summary>
    private static float TerrainCost(WorldTile t)
    {
        if (t.SettlementIndex >= 0)
            return 1f;
        return t.Terrain switch
        {
            OverworldHex.TerrainType.Coast => 2f,
            OverworldHex.TerrainType.Grassland => 2f,
            OverworldHex.TerrainType.ArcaneGround => 3f,
            OverworldHex.TerrainType.Ruins => 3f,
            OverworldHex.TerrainType.Forest => 4f,
            OverworldHex.TerrainType.Hills => 5f,
            OverworldHex.TerrainType.Swamp => 7f,
            OverworldHex.TerrainType.Volcanic => 8f,
            OverworldHex.TerrainType.Mountain => 14f,
            _ => 4f,
        };
    }

    /// <summary>Cost of stepping from u into neighbour v across direction d. Moving
    /// along an existing road edge is cheap (reuse → trunks). Crossing a river edge
    /// adds the ford penalty unless that edge is already a road (= a bridge).</summary>
    private static float StepCost(WorldData world, int u, int v, int d)
    {
        var tu = world.Tiles[u];
        bool road = (tu.RoadEdges & (byte)(1 << d)) != 0;
        bool river = (tu.RiverEdges & (byte)(1 << d)) != 0;

        float c = road ? RoadStepCost : TerrainCost(world.Tiles[v]);
        if (river && !road)
            c += RiverCrossingPenalty;   // unbridged ford; bridge = road on a river edge
        return c;
    }

    // ── Search ───────────────────────────────────────────────────────────

    private static float[] DijkstraToCenters(WorldData world, int source, List<int> centers)
    {
        int w = world.Width, h = world.Height, n = w * h;
        var d = new float[n];
        for (int i = 0; i < n; i++) d[i] = float.PositiveInfinity;
        d[source] = 0f;

        var pq = new PriorityQueue<int, float>();
        pq.Enqueue(source, 0f);

        while (pq.Count > 0)
        {
            int u = pq.Dequeue();
            int ux = u % w, uy = u / w;
            float du = d[u];

            for (int dir = 0; dir < 6; dir++)
            {
                if (!NeighborByDir(ux, uy, dir, w, h, out int nx, out int ny))
                    continue;
                int v = ny * w + nx;
                if (!Traversable(world, v))
                    continue;
                float nd = du + StepCost(world, u, v, dir);
                if (nd < d[v])
                {
                    d[v] = nd;
                    pq.Enqueue(v, nd);
                }
            }
        }

        var res = new float[centers.Count];
        for (int k = 0; k < centers.Count; k++)
            res[k] = d[centers[k]];
        return res;
    }

    private static List<int> AStar(WorldData world, int start, int goal)
    {
        int w = world.Width, h = world.Height, n = w * h;
        int gx = goal % w, gy = goal / w;

        var g = new float[n];
        for (int i = 0; i < n; i++) g[i] = float.PositiveInfinity;
        var came = new int[n];
        for (int i = 0; i < n; i++) came[i] = -1;

        g[start] = 0f;
        var pq = new PriorityQueue<int, float>();
        pq.Enqueue(start, Heuristic(start % w, start / w, gx, gy));

        while (pq.Count > 0)
        {
            int u = pq.Dequeue();
            if (u == goal)
                return Reconstruct(came, start, goal);

            int ux = u % w, uy = u / w;
            float gu = g[u];

            for (int dir = 0; dir < 6; dir++)
            {
                if (!NeighborByDir(ux, uy, dir, w, h, out int nx, out int ny))
                    continue;
                int v = ny * w + nx;
                if (!Traversable(world, v))
                    continue;
                float ng = gu + StepCost(world, u, v, dir);
                if (ng < g[v])
                {
                    g[v] = ng;
                    came[v] = u;
                    pq.Enqueue(v, ng + Heuristic(nx, ny, gx, gy));
                }
            }
        }
        return null;
    }

    private static List<int> Reconstruct(int[] came, int start, int goal)
    {
        var path = new List<int>();
        int c = goal;
        while (c != -1)
        {
            path.Add(c);
            if (c == start)
                break;
            c = came[c];
        }
        path.Reverse();
        return path;
    }

    private static float Heuristic(int x, int y, int gx, int gy)
        => HexCoord.OffsetDistance(x, y, gx, gy);

    // ── Hex helpers ──────────────────────────────────────────────────────

    private static bool NeighborByDir(int col, int row, int d, int w, int h,
                                      out int nCol, out int nRow)
    {
        var (q, r) = HexCoord.OffsetToAxial(col, row);
        var (dq, dr) = HexCoord.AxialDirections[d];
        (nCol, nRow) = HexCoord.AxialToOffset(q + dq, r + dr);
        return nCol >= 0 && nRow >= 0 && nCol < w && nRow < h;
    }

    private static int DirBetween(WorldData world, int a, int b)
    {
        int w = world.Width, h = world.Height;
        int ax = a % w, ay = a / w;
        for (int d = 0; d < 6; d++)
            if (NeighborByDir(ax, ay, d, w, h, out int nx, out int ny) && ny * w + nx == b)
                return d;
        return -1;
    }
}
