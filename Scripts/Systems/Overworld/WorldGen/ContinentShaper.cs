using Godot;
using System.Collections.Generic;

// ============================================================
// ContinentShaper.cs
//
// Purpose:        Seed-rolled continental landmass shaping for the
//                 world generator. Multiplies the raw OverworldField
//                 elevation by a continental mask (Pangaea /
//                 Continents / Archipelago), then SOLVES sea level by
//                 quantile so the land fraction is stable across seeds
//                 (no magic elevation threshold). Returns shaped
//                 per-tile land elevation in [0,1] plus an ocean mask.
//                 Pure / deterministic from the world seed.
//
//                 A low-frequency COASTLINE WARP displaces the mask and
//                 taper coordinates so continents aren't perfect circles
//                 and the ocean frame (esp. the N/S border) is an
//                 irregular coastline rather than a clean band. A hard
//                 ocean frame on the outermost tiles keeps the warp from
//                 clipping land against the literal map edge.
// Layer:          System (generation helper)
// Collaborators:  OverworldField (raw elevation), WorldGenerator
//                 (FillTerrain consumes this), WorldData.
// ============================================================

/// <summary>Macro landmass topology. Seed-rolled by default; overridable for debug.</summary>
public enum ContinentStyle
{
    Pangaea,
    Continents,
    Archipelago,
}

/// <summary>Result of continental shaping: shaped land elevation + ocean mask.</summary>
public sealed class ContinentShape
{
    /// <summary>Row-major (y*W+x). LAND elevation renormalized to [0,1] above sea
    /// level; OCEAN tiles are 0 (they're identified by IsOcean, classified as Water).</summary>
    public float[] Elevation;

    /// <summary>Row-major. True = ocean (below the solved sea level).</summary>
    public bool[] IsOcean;

    /// <summary>Row-major. True = a Water tile NOT connected to the world ocean
    /// (an enclosed basin) — becomes a Lake instead of sea.</summary>
    public bool[] IsEnclosed;

    public ContinentStyle Style;
    public float LandFraction;
}

/// <summary>Builds a continental mask and solves sea level by quantile.</summary>
public static class ContinentShaper
{
    // ── Tuning ───────────────────────────────────────────────────────────

    /// <summary>Coastline warp strength (normalized space). Higher = more peninsulas,
    /// bays and offshore islands; too high fragments a Pangaea. ~0.10–0.18.</summary>
    public static float CoastWarp = 0.15f;

    /// <summary>Coastline warp frequency. Lower = broader, smoother lobes.</summary>
    public static float CoastWarpFrequency = 0.025f;

    /// <summary>Beyond this |per-axis normalized| coord, tiles are forced ocean — a
    /// hard frame so the warp can't push land onto the literal map edge.</summary>
    public static float OceanFrameMargin = 0.96f;

    /// <summary>Target land fraction per style; sea level is solved to hit this.</summary>
    private static float TargetLand(ContinentStyle s) => s switch
    {
        ContinentStyle.Pangaea => 0.60f,
        ContinentStyle.Continents => 0.50f,
        ContinentStyle.Archipelago => 0.34f,
        _ => 0.50f,
    };

    /// <summary>Roll a style deterministically from the world seed. Continents
    /// common; Pangaea and Archipelago rarer.</summary>
    public static ContinentStyle RollStyle(int seed)
    {
        uint h = (uint)seed * 2654435761u;
        int b = (int)(h % 100u);
        if (b < 25)
            return ContinentStyle.Pangaea;      // 25%
        if (b < 80)
            return ContinentStyle.Continents;   // 55%
        return ContinentStyle.Archipelago;      // 20%
    }

    public static ContinentShape Build(OverworldField field, int width, int height,
                                       int seed, ContinentStyle style)
    {
        int n = width * height;
        var shaped = new float[n];

        // Aspect-correct normalization: map the LARGER dimension to [-1,1] so the
        // world isn't stretched and the mask's continents/falloffs stay circular.
        float half = Mathf.Max(width, height) * 0.5f;
        float cx = (width - 1) * 0.5f;
        float cy = (height - 1) * 0.5f;

        var centers = PlaceCenters(style, seed, out float sigma);

        // Coastline warp: two low-freq channels displace the shape/taper coords so
        // nothing comes out a perfect circle or a flat band.
        var warpX = new FastNoiseLite
        {
            Seed = seed + 4242,
            Frequency = CoastWarpFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 2,
        };
        var warpY = new FastNoiseLite
        {
            Seed = seed + 9119,
            Frequency = CoastWarpFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 2,
        };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                float raw = field.SampleElevation01(new Vector2I(x, y));

                float du = warpX.GetNoise2D(x, y) * CoastWarp;
                float dv = warpY.GetNoise2D(x, y) * CoastWarp;

                // Aspect-correct coords for the mask, warped so coastlines wobble.
                float u = (x - cx) / half + du;
                float v = (y - cy) / half + dv;

                // Per-axis coords for the taper (each axis reaches ±1 at its own edge),
                // warped so the ocean frame is an irregular coastline, not a flat band.
                float un = (x - cx) / (width * 0.5f) + du;
                float vn = (y - cy) / (height * 0.5f) + dv;
                float edge = EdgeTaper(un, vn);

                // Hard ocean frame on the literal outermost tiles so the warp can't
                // push land off the map edge (which reads as clipped).
                float unRaw = (x - cx) / (width * 0.5f);
                float vnRaw = (y - cy) / (height * 0.5f);
                if (Mathf.Abs(unRaw) > OceanFrameMargin || Mathf.Abs(vnRaw) > OceanFrameMargin)
                    edge = 0f;

                float m;
                if (style == ContinentStyle.Pangaea)
                {
                    float r = Mathf.Sqrt(u * u + v * v);
                    m = 1f - Mathf.SmoothStep(0.15f, 1.05f, r); // one broad central plateau
                }
                else
                {
                    float best = 0f;
                    foreach (var c in centers)
                    {
                        float dcu = u - c.X, dcv = v - c.Y;
                        float d2 = dcu * dcu + dcv * dcv;
                        float g = Mathf.Exp(-d2 / (2f * sigma * sigma));
                        if (g > best)
                            best = g;
                    }
                    m = best;
                }

                // Field texture carves internal variation; the mask carves continents.
                shaped[i] = raw * Mathf.Clamp(m * edge, 0f, 1f);
            }
        }

        // Solve sea level as the (1 - targetLand) quantile of shaped elevation.
        float target = TargetLand(style);
        float seaLevel = Quantile(shaped, 1f - target);

        var isOcean = new bool[n];
        var outE = new float[n];
        float span = Mathf.Max(1e-4f, 1f - seaLevel);
        int land = 0;

        for (int i = 0; i < n; i++)
        {
            bool ocean = shaped[i] < seaLevel;
            isOcean[i] = ocean;
            // Land renormalized to [0,1] so downstream mountain/hill bands and
            // hydrology keep full dynamic range; ocean stored as 0 (Water by flag).
            outE[i] = ocean ? 0f : Mathf.Clamp((shaped[i] - seaLevel) / span, 0f, 1f);
            if (!ocean)
                land++;
        }

        var isEnclosed = FloodEnclosed(isOcean, width, height);

        return new ContinentShape
        {
            Elevation = outE,
            IsOcean = isOcean,
            IsEnclosed = isEnclosed,
            Style = style,
            LandFraction = (float)land / n,
        };
    }

    /// <summary>BFS ocean inward from the map border (the true outer sea). Any Water
    /// tile the flood never reaches is an enclosed basin → flagged for Lake.</summary>
    private static bool[] FloodEnclosed(bool[] isOcean, int w, int h)
    {
        int n = w * h;
        var reached = new bool[n];
        var queue = new Queue<int>();

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool border = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                int i = y * w + x;
                if (border && isOcean[i] && !reached[i])
                { reached[i] = true; queue.Enqueue(i); }
            }

        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            foreach (var (nx, ny) in HexCoord.Neighbors(c % w, c / w, w, h))
            {
                int ni = ny * w + nx;
                if (isOcean[ni] && !reached[ni])
                { reached[ni] = true; queue.Enqueue(ni); }
            }
        }

        var enclosed = new bool[n];
        for (int i = 0; i < n; i++)
            enclosed[i] = isOcean[i] && !reached[i];
        return enclosed;
    }

    private static float EdgeTaper(float u, float v)
    {
        float tu = 1f - Mathf.SmoothStep(0.72f, 1.0f, Mathf.Abs(u));
        float tv = 1f - Mathf.SmoothStep(0.72f, 1.0f, Mathf.Abs(v));
        return tu * tv;
    }

    private static List<Vector2> PlaceCenters(ContinentStyle style, int seed, out float sigma)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)seed ^ 0x9E3779B97F4A7C15ul };

        int count;
        float minSep;
        if (style == ContinentStyle.Continents)
        {
            // Tighter gaussians, farther apart, and at least 3 centres so a
            // "Continents" roll reliably yields multiple landmasses with ocean
            // channels between them instead of one fused blob.
            count = 3 + (int)(rng.Randi() % 3u);   // 3..5
            sigma = 0.28f;
            minSep = 0.68f;
        }
        else // Archipelago (also the centers source for any non-Pangaea style)
        {
            count = 6 + (int)(rng.Randi() % 7u);   // 6..12
            sigma = 0.20f;
            minSep = 0.28f;
        }

        var centers = new List<Vector2>();
        int guard = 0;
        while (centers.Count < count && guard++ < 600)
        {
            // Interior only, so the edge taper still rings every continent in ocean.
            var c = new Vector2(rng.RandfRange(-0.6f, 0.6f), rng.RandfRange(-0.6f, 0.6f));
            bool ok = true;
            foreach (var e in centers)
                if (c.DistanceTo(e) < minSep)
                { ok = false; break; }
            if (ok)
                centers.Add(c);
        }
        return centers;
    }

    private static float Quantile(float[] values, float q)
    {
        var copy = (float[])values.Clone();
        System.Array.Sort(copy);
        int idx = Mathf.Clamp(Mathf.RoundToInt(q * (copy.Length - 1)), 0, copy.Length - 1);
        return copy[idx];
    }
}
