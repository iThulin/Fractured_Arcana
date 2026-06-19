using Godot;

// ============================================================
//  WindNoise.cs
//
//  Purpose:   Optional factory for the seamless tiling noise the
//             painterly_grass shader samples in world XZ for its
//             wind gusts. You do NOT need this if you build the
//             NoiseTexture2D in the inspector (Seamless = On) and
//             drag it into the material's "wind_noise" slot — that
//             is the zero-code path. This exists for procedural
//             setup (e.g. theme-driven wind from ThemeAtmosphere).
//  Layer:     Terrain / utility
//  See:       painterly_grass.gdshader (wind_noise uniform)
// ============================================================

/// <summary>
/// Builds a seamless, tiling Perlin noise texture suitable for the
/// painterly grass wind shader. The texture generates asynchronously;
/// the grass simply won't sway for a frame or two after creation.
/// </summary>
public static class WindNoise
{
    public static NoiseTexture2D CreateSeamless(int size = 256, float frequency = 0.015f, int seed = 12345)
    {
        var fnl = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = frequency,
            Seed = seed,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 3
        };

        return new NoiseTexture2D
        {
            Width = size,
            Height = size,
            Seamless = true,
            GenerateMipmaps = false,
            Noise = fnl
        };
    }

    /// <summary>
    /// Assigns a freshly built wind-noise texture onto a ShaderMaterial's
    /// "wind_noise" parameter. Pass the material from your grass mesh/surface.
    /// </summary>
    public static void Apply(ShaderMaterial material, int size = 256, float frequency = 0.015f, int seed = 12345)
    {
        if (material == null)
        {
            GD.PushError("WindNoise.Apply: material is null.");
            return;
        }

        material.SetShaderParameter("wind_noise", CreateSeamless(size, frequency, seed));
    }
}
