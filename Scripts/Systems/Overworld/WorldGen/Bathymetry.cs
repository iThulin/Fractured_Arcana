using Godot;
using System.Collections.Generic;

// ============================================================
// Bathymetry.cs
//
// Purpose:        Assigns each OCEAN tile an OceanDepth used to shade the
//                 sea shallow→deep. The base value is a multi-source BFS
//                 distance from the coastline, but it is then PERTURBED by
//                 two noise fields so the shelf isn't a uniform halo:
//
//                   • Shelf noise (low freq) SCALES the distance, so some
//                     coasts have a broad gentle shelf reaching far out and
//                     others drop off steeply right at the shore.
//                   • Mottle noise (higher freq) adds a few depth-units of
//                     per-tile jitter — this ragged the shelf→deep boundary
//                     (hiding the halo transition) and keeps the open ocean
//                     from reading as one flat colour. It also dithers the
//                     integer depth steps, dissolving concentric banding.
//
//                 Water stays the SAME terrain type — this is metadata, not
//                 a new biome, so land/water classification is untouched.
//                 Lakes (enclosed, not ocean) are left at depth 0.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (OceanDepth out), HexCoord,
//                 FastNoiseLite, WorldGenerator (caller, passes seed),
//                 StrategicView / OverworldHex (depth-shaded rendering),
//                 UITheme.OceanColor.
// Notes:          Runs LATE, after all terrain passes finalize land/water.
//                 Deterministic for a given seed (noise is seeded).
// ============================================================

public static class Bathymetry
{
    // ── Tuning ───────────────────────────────────────────────────────────

    /// <summary>Low frequency → coherent shelf-width zones spanning ~25-30 tiles,
    /// so one coastline can have several stretches of differing shelf character.</summary>
    private const float ShelfNoiseFreq = 0.025f;

    /// <summary>How strongly the shelf noise widens/narrows the shelf. 0.65 → the
    /// effective distance is scaled into roughly [0.35×, 1.65×]: wide gentle shelf
    /// at one extreme, steep drop-off at the other.</summary>
    private const float ShelfWidthAmp = 0.9f;

    /// <summary>Higher frequency → per-tile-ish texture that ragged the shelf edge
    /// and mottles the deep (≈8-tile lobes, reads as bays/points, not pixel snow).</summary>
    private const float MottleNoiseFreq = 0.12f;

    /// <summary>Depth-units of jitter added by the mottle noise.</summary>
    private const float MottleAmp = 4.5f;

    // ── Pass ─────────────────────────────────────────────────────────────

    public static void Apply(WorldData world, int seed)
    {
        int w = world.Width, h = world.Height, n = w * h;
        var dist = new int[n];           // 0 = land / unset
        var queue = new Queue<int>();

        // Seed: ocean tiles touching land are distance 1 (the shoreline).
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (world.Tiles[i].Terrain != OverworldHex.TerrainType.Water)
                    continue;

                bool nearLand = false;
                foreach (var (nx, ny) in HexCoord.Neighbors(x, y, w, h))
                    if (world.Tiles[ny * w + nx].IsLand)
                    { nearLand = true; break; }

                if (nearLand)
                { dist[i] = 1; queue.Enqueue(i); }
            }
        }

        // BFS outward through ocean: each step is one tile farther from shore.
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            foreach (var (nx, ny) in HexCoord.Neighbors(c % w, c / w, w, h))
            {
                int ni = ny * w + nx;
                if (world.Tiles[ni].Terrain == OverworldHex.TerrainType.Water && dist[ni] == 0)
                {
                    dist[ni] = dist[c] + 1;
                    queue.Enqueue(ni);
                }
            }
        }

        // Perturb distance into a stored depth.
        var shelfNoise = new FastNoiseLite
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = ShelfNoiseFreq,
        };
        var mottleNoise = new FastNoiseLite
        {
            Seed = seed + 1337,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = MottleNoiseFreq,
        };

        int maxD = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (world.Tiles[i].Terrain != OverworldHex.TerrainType.Water)
                    continue;

                int raw = dist[i];

                float shelf = shelfNoise.GetNoise2D(x, y);    // [-1,1]
                float mott = mottleNoise.GetNoise2D(x, y);    // [-1,1]

                // Vary shelf width by scaling distance, then ragged the result.
                float factor = 1f + shelf * ShelfWidthAmp;    // ~[0.35, 1.65]
                float eff = raw * factor + mott * MottleAmp;

                int d = Mathf.Clamp(Mathf.RoundToInt(eff), 0, 255);

                var t = world.Tiles[i];
                t.OceanDepth = (byte)d;
                world.Tiles[i] = t;
                if (d > maxD)
                    maxD = d;
            }
        }

        GD.Print($"[Bathymetry] ocean depth assigned (perturbed, max {maxD}).");
    }
}
