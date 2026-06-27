using Godot;

// ============================================================
// MountainShaper.cs
//
// Purpose:        Orogenic uplift for the world generator, in two
//                 passes that run at different points in the pipeline:
//
//                 RaiseElevation(world, seed) — adds ridged-noise
//                   uplift, masked to a few orogenic BELTS, into each
//                   land tile's stored Elevation. Runs EARLY (after
//                   ContinentShaper / FillTerrain, before hydrology +
//                   territories) so rivers drain around the ranges and
//                   capitals place on the final surface. Ridged noise
//                   gives linear ridgelines (ranges), not the blobby
//                   highs raw FBm produces; the belt mask makes them
//                   cluster into a few regions instead of carpeting
//                   the map.
//
//                 ClassifyHighlands(world) — re-derives Hills / Mountain
//                   from the FINAL (uplifted) elevation, run LATE (after
//                   ReclassifyTerrainPerRegion) so the mountain structure
//                   is globally coherent and isn't repainted by per-region
//                   palettes. Lowlands keep their regional identity; only
//                   the high bands are stamped here. Region-authored
//                   Volcanic (a biome, not an elevation artifact) is left
//                   intact and never synthesized from elevation.
// Layer:          System (generation helper)
// Collaborators:  WorldData / WorldTile (mutated in place),
//                 WorldGenerator (calls both passes),
//                 OverworldField (sampling deskew matched here),
//                 hydrology (next phase — reads the raised elevation).
// Notes:          Pure / deterministic from the world seed.
// ============================================================

public static class MountainShaper
{
    // ── Uplift tuning ────────────────────────────────────────────────────

    /// <summary>Ridge detail frequency. Lower = longer, broader ranges.</summary>
    public static float RidgeFrequency = 0.045f;

    /// <summary>Belt-mask frequency. Lower = fewer, larger orogenic regions.</summary>
    public static float BeltFrequency = 0.015f;

    /// <summary>Belt-mask smoothstep edges: below Lo the belt adds nothing, above
    /// Hi it's full strength. The gap between them sets how sharply ranges begin,
    /// and the floor (Lo) is what keeps mountains clustered instead of everywhere.</summary>
    public static float BeltThresholdLo = 0.55f;
    public static float BeltThresholdHi = 0.80f;

    /// <summary>Max elevation added at a ridge centre inside a full belt.</summary>
    public static float UpliftStrength = 0.55f;

    // ── Highland bands (re-derived from the final, uplifted elevation) ────

    /// <summary>Land at or above this becomes Hills (unless it's higher → Mountain).</summary>
    public static float HillsFloor = 0.62f;

    /// <summary>Land at or above this becomes Mountain.</summary>
    public static float MountainFloor = 0.82f;

    // ── Pass 1: uplift into the stored elevation field (EARLY) ────────────

    public static void RaiseElevation(WorldData world, int seed)
    {
        var ridge = new FastNoiseLite
        {
            Seed = seed + 4391,
            Frequency = RidgeFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Ridged,
            FractalOctaves = 4,
        };
        var belt = new FastNoiseLite
        {
            Seed = seed + 90113,
            Frequency = BeltFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 2,
        };

        int raised = 0;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int i = y * world.Width + x;
                var tile = world.Tiles[i];
                if (!tile.IsLand)
                    continue;

                // Deskew identically to OverworldField so the uplift field lines
                // up with the continent field's visual grid rather than shearing
                // diagonally against the hex layout.
                float px = x + y * 0.5f;
                float py = y;

                float r = Norm01(ridge.GetNoise2D(px, py));   // ridged: ~1 along ridge lines
                float b = Norm01(belt.GetNoise2D(px, py));
                float beltMask = Mathf.SmoothStep(BeltThresholdLo, BeltThresholdHi, b);

                float uplift = r * beltMask * UpliftStrength;
                if (uplift <= 0f)
                    continue;

                tile.Elevation = Mathf.Clamp(tile.Elevation + uplift, 0f, 1f);
                world.Tiles[i] = tile;
                raised++;
            }
        }

        GD.Print($"[MountainShaper] Uplift applied to {raised} land tiles (ridged × belt mask).");
    }

    // ── Pass 2: stamp Hills / Mountain from final elevation (LATE) ────────

    public static void ClassifyHighlands(WorldData world)
    {
        int hills = 0, mountains = 0;
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            var tile = world.Tiles[i];
            if (!tile.IsLand)
                continue;

            // Region-authored Volcanic is a biome, not an elevation feature —
            // leave it so volcanic regions keep their identity at any height.
            if (tile.Terrain == OverworldHex.TerrainType.Volcanic)
                continue;

            float e = tile.Elevation;
            if (e < HillsFloor)
                continue;   // lowland — keep whatever the region palette assigned

            if (e >= MountainFloor)
            { tile.Terrain = OverworldHex.TerrainType.Mountain; mountains++; }
            else
            { tile.Terrain = OverworldHex.TerrainType.Hills; hills++; }

            world.Tiles[i] = tile;
        }

        GD.Print($"[MountainShaper] Highlands stamped: {hills} hills, {mountains} mountain.");
    }

    // ── Internals ────────────────────────────────────────────────────────

    /// <summary>Remaps FastNoiseLite output (~[-1,1]) to [0,1].</summary>
    private static float Norm01(float n) => (n + 1f) * 0.5f;
}
