using Godot;
using System.Collections.Generic;

// ============================================================
// OverworldField.cs
//
// Purpose:        Seeded, coherent terrain field for OVERWORLD map
//                 generation. The structural twin of MapField (combat),
//                 sampling per-axial elevation and moisture from layered
//                 FastNoiseLite, then classifying (elevation, moisture)
//                 into an OverworldHex.TerrainType via a region palette.
//                 This replaces the hardcoded-biome-centre generator in
//                 OverworldHexGrid with coherent, seed-reproducible
//                 terrain at any map size.
// Layer:          System (generation helper)
// Collaborators:  OverworldHexGrid (samples this per hex),
//                 OverworldHex (TerrainType), RegionDefinition
//                 (OverworldBaseTerrain + OverworldPaletteRule)
// Notes:          Pure / deterministic. No GD.Randf calls — the same seed
//                 always yields the same field. Mirrors MapField's
//                 AxialToPlane deskew and Norm01 remap so the overworld
//                 and combat layers read terrain the same way.
// ============================================================

/// <summary>
/// Deterministic elevation + moisture field for one overworld region. Given a seed,
/// produces coherent terrain: water pools in lows, mountains/volcanic cap ridges,
/// forest fills the wet mid-band, etc. <see cref="ClassifyByPalette"/> turns the two
/// scalars into an <see cref="OverworldHex.TerrainType"/> using region-authored rules;
/// <see cref="SampleElevation01"/> additionally drives the gradient-traced river.
/// </summary>
public sealed class OverworldField
{
    private readonly FastNoiseLite _elevation;
    private readonly FastNoiseLite _moisture;
    private readonly FastNoiseLite _detail;

    // ── Tuning (override after construction; region values applied by the grid) ──

    /// <summary>Lower = broader regions; higher = busier terrain.</summary>
    public float ElevationFrequency = 0.11f;

    /// <summary>Moisture varies more slowly than elevation so biomes read as bands.</summary>
    public float MoistureFrequency = 0.08f;

    /// <summary>High-frequency break-up layered onto elevation so coastlines aren't glassy.</summary>
    public float DetailFrequency = 0.38f;

    /// <summary>How much the detail layer perturbs base elevation (0..1).</summary>
    public float DetailWeight = 0.16f;

    public OverworldField(int seed)
    {
        _elevation = MakeNoise(seed, ElevationFrequency, FastNoiseLite.NoiseTypeEnum.SimplexSmooth, octaves: 4);
        _moisture = MakeNoise(seed + 1013, MoistureFrequency, FastNoiseLite.NoiseTypeEnum.SimplexSmooth, octaves: 3);
        _detail = MakeNoise(seed + 7919, DetailFrequency, FastNoiseLite.NoiseTypeEnum.Simplex, octaves: 2);
    }

    /// <summary>
    /// Re-applies frequency tuning to the underlying noise generators. Call after
    /// overriding the public frequency fields (e.g. from a region's base-terrain block)
    /// so the change actually reaches FastNoiseLite.
    /// </summary>
    public void ApplyFrequencies()
    {
        _elevation.Frequency = ElevationFrequency;
        _moisture.Frequency = MoistureFrequency;
        _detail.Frequency = DetailFrequency;
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

    // ── Classification ────────────────────────────────────────────────────────

    /// <summary>
    /// Data-driven terrain classification: the first palette rule whose elevation
    /// and moisture bounds all pass wins. Mirrors MapField.ClassifyByPalette so the
    /// overworld and combat layers share the same authoring mental model. Returns
    /// Grassland if no rule matches (palettes should end with an unbounded catch-all).
    /// </summary>
    public OverworldHex.TerrainType ClassifyByPalette(List<OverworldPaletteRule> rules, float elevation01, float moisture01)
    {
        if (rules == null)
            return OverworldHex.TerrainType.Grassland;

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

        return OverworldHex.TerrainType.Grassland;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static FastNoiseLite MakeNoise(int seed, float frequency, FastNoiseLite.NoiseTypeEnum type, int octaves)
    {
        return new FastNoiseLite
        {
            Seed = seed,
            Frequency = frequency,
            NoiseType = type,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = octaves
        };
    }

    /// <summary>
    /// Converts an axial (q, r) coord to an unskewed plane coordinate for noise sampling.
    /// Raw axial coords introduce diagonal bias; shearing q by half of r rounds the field
    /// out. Identical to MapField.AxialToPlane so both layers sample coherently.
    /// </summary>
    private static Vector2 AxialToPlane(Vector2I axial)
    {
        return new Vector2(axial.X + axial.Y * 0.5f, axial.Y);
    }

    /// <summary>Remaps FastNoiseLite output (~[-1,1]) to [0,1].</summary>
    private static float Norm01(float n) => (n + 1f) * 0.5f;
}
