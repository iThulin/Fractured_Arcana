using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// MapField.cs
//
// Purpose:        Seeded, coherent terrain field for combat map
//                 generation. Samples per-axial elevation and
//                 moisture from layered FastNoiseLite, then derives
//                 integer height steps and theme-aware terrain type
//                 from those two scalars. This is the substrate that
//                 replaces independent per-tile height/terrain rolls
//                 in HexGridManager — coherent noise + correlation
//                 instead of salt-and-pepper randomness.
// Layer:          System (generation helper)
// Collaborators:  HexGridManager.Generation (samples this per tile),
//                 TileData (TileTerrainType), HexGridManager.MapTheme
// Notes:          Pure / deterministic. No GD.Randf calls — same seed
//                 always yields the same field. Safe to construct,
//                 sample, and discard during a single generation pass.
// ============================================================

/// <summary>
/// Deterministic elevation + moisture field for one combat map. Given a seed,
/// produces coherent terrain: <see cref="ElevationToHeightStep"/> drives raised
/// ground and basins, and <see cref="ClassifyTerrain"/> maps (elevation, moisture)
/// to a <see cref="TileTerrainType"/> appropriate for the active theme. Water pools
/// in low ground, stone caps ridges, forest fills the wet mid-band, etc.
/// </summary>
public sealed class MapField
{
    private readonly FastNoiseLite _elevation;
    private readonly FastNoiseLite _moisture;
    private readonly FastNoiseLite _detail;

    // ── Tuning (override after construction if needed) ──────────────────────

    /// <summary>Lower = broader landmasses; higher = busier terrain. Scaled to hex count.</summary>
    public float ElevationFrequency = 0.14f;

    /// <summary>Moisture varies more slowly than elevation by default, so biomes read as bands.</summary>
    public float MoistureFrequency = 0.10f;

    /// <summary>High-frequency break-up layered onto elevation so slopes aren't glassy.</summary>
    public float DetailFrequency = 0.40f;

    /// <summary>How much the detail layer perturbs base elevation (0..1).</summary>
    public float DetailWeight = 0.18f;

    /// <summary>Highest integer height step the field will assign (ridges/peaks).</summary>
    public int MaxHeightStep = 3;

    /// <summary>Lowest integer height step (basins, water beds). Negative dips below baseline.</summary>
    public int MinHeightStep = -2;

    public MapField(int seed)
    {
        _elevation = MakeNoise(seed, ElevationFrequency, FastNoiseLite.NoiseTypeEnum.SimplexSmooth, octaves: 4);
        _moisture = MakeNoise(seed + 1013, MoistureFrequency, FastNoiseLite.NoiseTypeEnum.SimplexSmooth, octaves: 3);
        _detail = MakeNoise(seed + 7919, DetailFrequency, FastNoiseLite.NoiseTypeEnum.Simplex, octaves: 2);
    }

    // ── Sampling ────────────────────────────────────────────────────────────

    /// <summary>Coherent elevation in [0,1] at the given axial coord (1.0 = peak, 0.0 = lowest).</summary>
    public float SampleElevation01(Vector2I axial)
    {
        Vector2 p = AxialToPlane(axial);

        float baseE = Norm01(_elevation.GetNoise2D(p.X, p.Y));
        float detailE = Norm01(_detail.GetNoise2D(p.X, p.Y));

        float e = Mathf.Lerp(baseE, detailE, DetailWeight);
        return Mathf.Clamp(e, 0f, 1f);
    }

    /// <summary>Coherent moisture in [0,1] at the given axial coord (1.0 = wettest, 0.0 = driest).</summary>
    public float SampleMoisture01(Vector2I axial)
    {
        Vector2 p = AxialToPlane(axial);
        return Mathf.Clamp(Norm01(_moisture.GetNoise2D(p.X, p.Y)), 0f, 1f);
    }

    // ── Derivation ──────────────────────────────────────────────────────────

    /// <summary>Maps a normalized elevation to an integer height step in [MinHeightStep, MaxHeightStep].</summary>
    public int ElevationToHeightStep(float elevation01)
    {
        return Mathf.RoundToInt(Mathf.Lerp(MinHeightStep, MaxHeightStep, Mathf.Clamp(elevation01, 0f, 1f)));
    }

    /// <summary>
    /// Theme-aware terrain classification from elevation + moisture. Thresholds are tuned
    /// per theme so each map reads as its own place: low ground becomes water/lava,
    /// high ground becomes stone, the wet mid-band becomes forest, etc.
    /// </summary>
    public TileTerrainType ClassifyTerrain(HexGridManager.MapTheme theme, float elevation01, float moisture01)
    {
        switch (theme)
        {
            case HexGridManager.MapTheme.ArcaneMeadow:
                if (elevation01 < 0.18f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.80f)
                    return TileTerrainType.Stone;
                if (moisture01 > 0.60f)
                    return TileTerrainType.Forest;
                return TileTerrainType.Grass;

            case HexGridManager.MapTheme.FrozenBasin:
                if (elevation01 < 0.16f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.82f)
                    return TileTerrainType.Stone;
                return TileTerrainType.Ice;

            case HexGridManager.MapTheme.VolcanicScar:
                if (elevation01 < 0.20f)
                    return TileTerrainType.Lava;
                if (moisture01 < 0.25f && elevation01 < 0.45f)
                    return TileTerrainType.Lava;
                return TileTerrainType.Stone;

            case HexGridManager.MapTheme.OvergrownRuins:
                if (elevation01 < 0.16f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.82f)
                    return TileTerrainType.Stone;
                if (moisture01 > 0.45f)
                    return TileTerrainType.Forest;
                return TileTerrainType.Grass;

            case HexGridManager.MapTheme.VerdantWoods:
                if (elevation01 < 0.16f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.82f)
                    return TileTerrainType.Stone;
                if (moisture01 > 0.35f)
                    return TileTerrainType.Forest;
                return TileTerrainType.Grass;

            case HexGridManager.MapTheme.Wetlands:
                if (elevation01 < 0.30f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.80f)
                    return TileTerrainType.Stone;
                if (moisture01 > 0.45f)
                    return TileTerrainType.Forest;
                return TileTerrainType.Grass;

            case HexGridManager.MapTheme.HighlandCrags:
                if (elevation01 < 0.22f)
                    return TileTerrainType.Grass;
                return TileTerrainType.Stone;

            case HexGridManager.MapTheme.RiverValley:
                if (elevation01 < 0.14f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.80f)
                    return TileTerrainType.Stone;
                if (moisture01 > 0.50f)
                    return TileTerrainType.Forest;
                return TileTerrainType.Grass;

            case HexGridManager.MapTheme.Heathland:
                if (elevation01 < 0.14f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.85f)
                    return TileTerrainType.Stone;
                if (moisture01 > 0.60f)
                    return TileTerrainType.Forest;
                return TileTerrainType.Grass;

            case HexGridManager.MapTheme.CoastalShallows:
                if (elevation01 < 0.30f)
                    return TileTerrainType.Water;
                if (elevation01 > 0.85f)
                    return TileTerrainType.Stone;
                if (moisture01 > 0.55f)
                    return TileTerrainType.Forest;
                return TileTerrainType.Grass;

            default:
                return TileTerrainType.Grass;
        }
    }

    // ── MapField integration ─────────────────────────────────────────────────

    /// <summary>Data-driven terrain classification: first palette rule whose elevation/moisture bounds all pass wins.</summary>
    public TileTerrainType ClassifyByPalette(List<PaletteRule> rules, float elevation01, float moisture01)
    {
        foreach (var r in rules)
        {
            if (r.MaxElevation.HasValue && elevation01 >= r.MaxElevation.Value)
                continue;
            if (r.MinElevation.HasValue && elevation01 <= r.MinElevation.Value)
                continue;
            if (r.MaxMoisture.HasValue && moisture01 >= r.MaxMoisture.Value)
                continue;
            if (r.MinMoisture.HasValue && moisture01 <= r.MinMoisture.Value)
                continue;
            return r.Terrain;
        }
        return TileTerrainType.Grass;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static FastNoiseLite MakeNoise(int seed, float frequency, FastNoiseLite.NoiseTypeEnum type, int octaves)
    {
        var noise = new FastNoiseLite
        {
            Seed = seed,
            Frequency = frequency,
            NoiseType = type,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = octaves
        };
        return noise;
    }

    /// <summary>
    /// Converts an axial (q, r) coord to an unskewed plane coordinate for noise sampling.
    /// Raw axial coords introduce diagonal bias; shearing q by half of r rounds the field out.
    /// </summary>
    private static Vector2 AxialToPlane(Vector2I axial)
    {
        return new Vector2(axial.X + axial.Y * 0.5f, axial.Y);
    }

    /// <summary>Remaps FastNoiseLite output (~[-1,1]) to [0,1].</summary>
    private static float Norm01(float n)
    {
        return (n + 1f) * 0.5f;
    }
}
