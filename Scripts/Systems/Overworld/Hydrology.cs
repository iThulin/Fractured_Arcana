using Godot;
using System.Collections.Generic;

// ============================================================
// Hydrology.cs
//
// Purpose:        Derives inland Lakes and river EDGES from the final
//                 (post-uplift) elevation field. One pass, deterministic,
//                 no RNG. Runs AFTER MountainShaper.RaiseElevation and
//                 BEFORE territory partition, so:
//                   - lakes exist before AssignTerritories (and are never
//                     owned, because that site skips IsWater),
//                   - rivers exist before road routing (a later phase),
//                   - rivers drain around the mountains, not through them.
//
//                 Pipeline:
//                   1. Priority-flood (Barnes 2014) seeded from ocean +
//                      map border. Produces a "spill" surface with no
//                      internal sinks and a drainage tree (each tile's
//                      receiver is its downhill parent on the filled
//                      surface).
//                   2. Flag flooded tiles (spill risen meaningfully above
//                      terrain). A rough elevation field produces thousands
//                      of one-tile micro-pits, so:
//                   3. Min-size filter: keep only flooded tiles that belong
//                      to a connected basin of at least MinLakeTiles. This
//                      is what stops single-tile "confetti" lakes — real
//                      basins are multi-tile, micro-pits are isolated.
//                   4. Flow accumulation down the drainage tree (leaves
//                      first, O(n)).
//                   5. River edges where accumulation crosses the threshold,
//                      stamped on BOTH tiles via the i <-> (i+3)%6 opposite-
//                      edge pairing. River origins are gated off the
//                      will-be-lake set so rivers don't start inside a lake.
//                   6. Lakes classified LAST, so steps 4-5 still see the
//                      basin tiles as land.
//
//                 River model is flow-edges (the edge a tile drains
//                 across), NOT the vertex/corner model. Crossing cost is
//                 per-edge and exact; geometric bank-to-bank purity is a
//                 later corner-flow refactor if ever wanted.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (Elevation in, Terrain + RiverEdges
//                 out), HexCoord (neighbour + direction math),
//                 MountainShaper (must run first), WorldGenerator (caller).
// ============================================================

public static class Hydrology
{
    // ── Tuning ───────────────────────────────────────────────────────────

    /// <summary>A basin must flood at least this far above its terrain before a
    /// tile is a lake candidate. Low on purpose — the size filter, not depth,
    /// is what removes spurious water.</summary>
    public static float LakeMinDepth = 0.015f;

    /// <summary>A flooded basin must span at least this many connected tiles to
    /// become a Lake. This is the knob that kills single-tile "confetti" lakes
    /// from a noisy elevation field. Raise for fewer/larger lakes; lower (toward
    /// 1) to reintroduce small ponds.</summary>
    public static int MinLakeTiles = 6;

    /// <summary>Upstream flow (contributing tile count) a tile must carry before
    /// the edge to its receiver is drawn as a river. Higher = sparser, fewer but
    /// larger trunk rivers. Tuned for a 96x96 world.</summary>
    public static int RiverMinFlow = 80;

    // ── Entry ────────────────────────────────────────────────────────────

    public static void Apply(WorldData world)
    {
        int w = world.Width, h = world.Height, n = w * h;

        var spill = new float[n];
        var visited = new bool[n];
        var receiver = new int[n];       // downhill parent index, -1 for roots
        var recvDir = new byte[n];       // direction index from tile -> receiver
        var popped = new List<int>(n);   // dequeue order (outward from ocean)

        // ── 1. Priority-flood ────────────────────────────────────────────
        var pq = new PriorityQueue<int, float>();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                bool isBorder = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                // Ocean and the map border are the drains: water leaves here.
                if (world.Tiles[i].IsOcean || isBorder)
                {
                    spill[i] = world.Tiles[i].Elevation;
                    visited[i] = true;
                    receiver[i] = -1;
                    pq.Enqueue(i, spill[i]);
                }
            }
        }

        while (pq.Count > 0)
        {
            int c = pq.Dequeue();
            popped.Add(c);
            int cx = c % w, cy = c / w;

            for (int d = 0; d < 6; d++)
            {
                if (!NeighborByDir(cx, cy, d, w, h, out int nx, out int ny))
                    continue;
                int ni = ny * w + nx;
                if (visited[ni])
                    continue;

                visited[ni] = true;
                // Filled surface: a cell can be no lower than its own terrain,
                // and no lower than the lowest barrier on its path to the drain.
                spill[ni] = Mathf.Max(spill[c], world.Tiles[ni].Elevation);
                receiver[ni] = c;
                recvDir[ni] = (byte)((d + 3) % 6);   // direction from ni back to c
                pq.Enqueue(ni, spill[ni]);
            }
        }

        // ── 2. Flag flooded tiles ────────────────────────────────────────
        var flooded = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!world.Tiles[i].IsLand)
                continue;
            if (spill[i] - world.Tiles[i].Elevation > LakeMinDepth)
                flooded[i] = true;
        }

        // ── 3. Min-size filter (kills single-tile confetti lakes) ─────────
        int lakeCount = FilterSmallLakes(world, flooded);

        // ── 4. Flow accumulation (leaves first via reversed pop order) ────
        var acc = new int[n];
        for (int i = 0; i < n; i++)
            acc[i] = world.Tiles[i].IsLand ? 1 : 0;   // terrain unchanged yet, so basin tiles still count

        for (int k = popped.Count - 1; k >= 0; k--)
        {
            int c = popped[k];
            int r = receiver[c];
            if (r >= 0)
                acc[r] += acc[c];
        }

        // ── 5. River edges ───────────────────────────────────────────────
        int riverEdges = 0;
        for (int i = 0; i < n; i++)
        {
            // Source must be flowing land: not a will-be-lake, not ocean. A river
            // may still END in a lake/ocean (its receiver) — that isn't gated.
            if (flooded[i] || world.Tiles[i].IsOcean)
                continue;
            if (acc[i] < RiverMinFlow)
                continue;
            int r = receiver[i];
            if (r < 0)
                continue;

            int d = recvDir[i];                 // edge from i -> receiver
            int opp = (d + 3) % 6;              // same physical edge, other side

            var ti = world.Tiles[i];
            ti.RiverEdges |= (byte)(1 << d);
            world.Tiles[i] = ti;

            var tr = world.Tiles[r];
            tr.RiverEdges |= (byte)(1 << opp);
            world.Tiles[r] = tr;

            riverEdges++;
        }

        // ── 6. Classify lakes LAST ───────────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            if (!flooded[i])
                continue;
            var t = world.Tiles[i];
            t.Terrain = OverworldHex.TerrainType.Lake;
            world.Tiles[i] = t;
        }

        GD.Print($"[Hydrology] {lakeCount} lake tiles, {riverEdges} river edges " +
                 $"(LakeMinDepth={LakeMinDepth}, MinLakeTiles={MinLakeTiles}, RiverMinFlow={RiverMinFlow}).");
    }

    // ── Connected-component size filter ───────────────────────────────────

    /// <summary>Clears flooded[] for any basin smaller than MinLakeTiles (so it
    /// reverts to its original land terrain), and returns the count of tiles that
    /// survive as lakes. Components are 6-neighbour connected over flooded tiles.</summary>
    private static int FilterSmallLakes(WorldData world, bool[] flooded)
    {
        int w = world.Width, h = world.Height, n = w * h;
        var seen = new bool[n];
        var component = new List<int>();
        var queue = new Queue<int>();
        int kept = 0;

        for (int start = 0; start < n; start++)
        {
            if (!flooded[start] || seen[start])
                continue;

            component.Clear();
            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;

            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                component.Add(c);
                int cx = c % w, cy = c / w;

                for (int d = 0; d < 6; d++)
                {
                    if (!NeighborByDir(cx, cy, d, w, h, out int nx, out int ny))
                        continue;
                    int ni = ny * w + nx;
                    if (flooded[ni] && !seen[ni])
                    {
                        seen[ni] = true;
                        queue.Enqueue(ni);
                    }
                }
            }

            if (component.Count < MinLakeTiles)
            {
                foreach (int idx in component)
                    flooded[idx] = false;   // too small — revert to land
            }
            else
            {
                kept += component.Count;
            }
        }

        return kept;
    }

    // ── Internals ────────────────────────────────────────────────────────

    /// <summary>Offset neighbour in axial direction d, bounds-checked. Mirrors
    /// HexCoord's offset/axial convention so river edges line up with the same
    /// hex topology distance/disc queries use.</summary>
    private static bool NeighborByDir(int col, int row, int d, int w, int h,
                                      out int nCol, out int nRow)
    {
        var (q, r) = HexCoord.OffsetToAxial(col, row);
        var (dq, dr) = HexCoord.AxialDirections[d];
        (nCol, nRow) = HexCoord.AxialToOffset(q + dq, r + dr);
        return nCol >= 0 && nRow >= 0 && nCol < w && nRow < h;
    }
}
