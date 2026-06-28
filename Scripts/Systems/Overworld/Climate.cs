using Godot;

// ============================================================
// Climate.cs
//
// Purpose:        Temperature-driven biome override. Computes a per-tile
//                 temperature from latitude (warm at the vertical centre,
//                 cold toward the poles) minus an elevation lapse rate
//                 plus low-frequency noise, then overrides the natural
//                 land biomes at the extremes:
//                   Desert — hot AND dry,
//                   Tundra — cold lowland,
//                   Snow   — very cold land, OR a mountain above the
//                            snowline (tall peaks ice over anywhere) OR
//                            any cold-latitude mountain.
//
//                 Runs LATE (after the per-region repaint + highland
//                 stamping) so it wins, and BEFORE settlements so towns
//                 see the final biome. Touches only the natural biomes
//                 (Grassland/Forest/Swamp/Hills/Mountain); Marsh, Volcanic,
//                 Coast, ArcaneGround, Ruins and all water are preserved.
//
//                 Polar OCEAN is left as ocean in this version (freezing it
//                 to walkable land would break the owned-land invariant);
//                 the polar "freeze" is the snow/tundra land fringe plus
//                 mountain caps. A water-class sea-ice type is a later add.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (Terrain out; Elevation + Moisture
//                 in), WorldGenerator (caller), MountainShaper (mountains
//                 must be stamped first).
// Notes:          Deterministic. Temperature is computed inline, not stored
//                 — add a WorldTile.Temperature field if a later system needs it.
// ============================================================

public static class Climate
{
    // ── Tuning ───────────────────────────────────────────────────────────

    /// <summary>How much temperature drops per unit elevation. Higher = colder
    /// highlands.</summary>
    public static float LapseRate = 0.45f;

    /// <summary>Low-frequency temperature jitter so biome bands aren't perfectly
    /// latitudinal.</summary>
    public static float NoiseAmp = 0.06f;
    public static float NoiseFrequency = 0.03f;

    /// <summary>Land at/below this temperature is Snow; at/below TundraTemp is Tundra.</summary>
    public static float SnowTemp = 0.18f;
    public static float TundraTemp = 0.32f;

    /// <summary>Land at/above DesertTemp AND drier than DesertMaxMoisture is Desert.</summary>
    public static float DesertTemp = 0.68f;
    public static float DesertMaxMoisture = 0.35f;

    /// <summary>A Mountain at/above this elevation gets a permanent Snow cap at ANY
    /// latitude (orographic snow on the tallest peaks). Cold-latitude mountains also
    /// cap via the temperature rule below.</summary>
    public static float SnowlineElevation = 0.93f;

    // ── Entry ────────────────────────────────────────────────────────────

    public static void Apply(WorldData world, int seed)
    {
        var noise = new FastNoiseLite
        {
            Seed = seed + 7717,
            Frequency = NoiseFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 2,
        };

        float cy = (world.Height - 1) * 0.5f;
        int desert = 0, tundra = 0, snow = 0;

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int i = y * world.Width + x;
                var tile = world.Tiles[i];
                if (!ClimateAffects(tile.Terrain))
                    continue;

                float e = tile.Elevation;
                float m = tile.Moisture;

                // Latitude: 0 at the equator (vertical centre), 1 at the poles.
                float lat = Mathf.Abs((y - cy) / cy);

                // Deskew the noise the same way the other passes do.
                float nval = noise.GetNoise2D(x + y * 0.5f, y);

                float temp = (1f - lat) - e * LapseRate + nval * NoiseAmp;

                OverworldHex.TerrainType result;

                if (tile.Terrain == OverworldHex.TerrainType.Mountain)
                {
                    // Tall peaks ice over anywhere; cold mountains ice over too.
                    if (e >= SnowlineElevation || temp < TundraTemp)
                    { result = OverworldHex.TerrainType.Snow; snow++; }
                    else
                        continue;   // stays rocky Mountain
                }
                else
                {
                    if (temp < SnowTemp)
                    { result = OverworldHex.TerrainType.Snow; snow++; }
                    else if (temp < TundraTemp)
                    { result = OverworldHex.TerrainType.Tundra; tundra++; }
                    else if (temp > DesertTemp && m < DesertMaxMoisture)
                    { result = OverworldHex.TerrainType.Desert; desert++; }
                    else
                        continue;   // temperate — keep the region biome
                }

                tile.Terrain = result;
                world.Tiles[i] = tile;
            }
        }

        GD.Print($"[Climate] {desert} desert, {tundra} tundra, {snow} snow " +
                 $"(LapseRate={LapseRate}, snowline={SnowlineElevation}).");
    }

    /// <summary>Only natural land biomes are climate-driven. Marsh (hydrology),
    /// Volcanic (region biome), Coast (shoreline), ArcaneGround/Ruins (authored
    /// features) and all water are preserved.</summary>
    private static bool ClimateAffects(OverworldHex.TerrainType t) => t switch
    {
        OverworldHex.TerrainType.Grassland => true,
        OverworldHex.TerrainType.Forest => true,
        OverworldHex.TerrainType.Swamp => true,
        OverworldHex.TerrainType.Hills => true,
        OverworldHex.TerrainType.Mountain => true,
        _ => false,
    };
}
