using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// WorldGenerator.cs
//
// Purpose:        Seeded, headless generator for one cycle's
//                 Civ-scale world. Produces a WorldData (tile
//                 array + POIs + staging) and the paired
//                 KingdomState dictionary + CampaignState, by:
//                   1. sampling OverworldField across the whole
//                      surface for coherent terrain,
//                   2. partitioning the surface into contiguous
//                      territories (graph-Voronoi from seeded
//                      capitals) with faction + tier + corruption,
//                   3. handing the territories to the existing
//                      CampaignGenerator for archmage placement
//                      (betrayal weight + co-conspirator intact),
//                   4. scattering POIs (mostly undiscovered) and
//                      seeding the first staging point.
//                 No Godot nodes are instantiated — this runs and
//                 verifies headless (Phase 1a).
// Layer:          System
// Collaborators:  OverworldField.cs (terrain noise),
//                 RegionLoader.cs (palette presets),
//                 CampaignGenerator.cs (archmage seam),
//                 FactionRegistry.cs, KingdomState.cs,
//                 WorldData.cs (output), CycleState.cs (stores),
//                 TerrainClass.cs (land/water predicates)
// See:            single_world_refactor_v2.docx §3.2, §8 (Phase 1a)
// ============================================================

/// <summary>Everything one generated world needs, returned together.</summary>
public class GeneratedWorldData
{
    public WorldData World = new();
    public Dictionary<string, KingdomState> Kingdoms = new();
    public CampaignState Campaign = new();
}

/// <summary>Builds a complete Civ-scale world from a seed. Headless.</summary>
public static class WorldGenerator
{
    private const string CONVERGENCE_ID = "the_convergence";

    // Defaults; overridable via the parameter object.
    public class Params
    {
        public int Width = 158;
        public int Height = 96;
        public int KingdomCount = 10;     // territories partitioned across the surface
        public float WaterLevel = 0.30f;  // elevation below this is unwalkable water (avoid as capitals/POIs)
        public int PoiPerKingdom = 12;
        public int PreDiscoveredPois = 3; // POIs visible from the start, near the staging point
        public ContinentStyle? ContinentStyleOverride = null; // null = roll the continent style from the seed; set to force one (debug).
        public float CityStudFraction = 0.55f;   // fraction of a city's tiles that get a POI
        public float TownStudFraction = 0.50f;
        public int WildPoiPerKingdom = 5;         // thinned wilderness scatter (was PoiPerKingdom=12)

    }

    public static GeneratedWorldData Generate(int seed, string playerSchool, Params p = null)
    {
        p ??= new Params();
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };

        var world = new WorldData
        {
            Width = p.Width,
            Height = p.Height,
            Tiles = new WorldTile[p.Width * p.Height],
        };

        // ── 1. Terrain across the whole surface ──────────────────────────
        FillTerrain(world, seed, p.ContinentStyleOverride);

        // ── 1b. Orogenic uplift into the elevation field. Runs before
        // hydrology + territories so ranges are present when rivers drain and
        // capitals place. Terrain for the high bands is stamped post-reclassify.
        MountainShaper.RaiseElevation(world, seed);

        // ── 1c. Hydrology: depression-fill the uplifted surface into inland
        // Lakes, then trace flow accumulation into river EDGES. Before territories
        // so lakes aren't owned and rivers exist for later road routing.
        Hydrology.Apply(world);

        // ── 2. Territory partition (capitals → graph Voronoi) ────────────
        var capitals = PlaceCapitals(world, p, rng);   // one per kingdom
        var kingdomIds = AssignTerritories(world, capitals);

        // Convergence: the capital farthest from the player's start capital.
        // Start capital = capitals[0]; convergence = farthest capital cell.
        var start = capitals[0];
        var convergence = capitals
            .Skip(1)
            .OrderByDescending(c => Dist(c, start))
            .First();
        world.ConvergenceX = convergence.x;
        world.ConvergenceY = convergence.y;

        // ── 3. Tiers by distance from start; factions per territory ──────
        var tierOf = new Dictionary<string, int>();
        var factionOf = AssignFactions(capitals, kingdomIds, rng);
        int maxDist = 1;
        var distOf = new Dictionary<string, int>();
        foreach (var kvp in capitals.Select((c, i) => (id: kingdomIds[i], c)))
        {
            int d = Dist(kvp.c, start);
            distOf[kvp.id] = d;
            if (d > maxDist)
                maxDist = d;
        }
        foreach (var id in kingdomIds)
            tierOf[id] = DistanceToTier(distOf[id], maxDist);

        // ── 4. Assign each kingdom a REAL region, then place archmagi ────
        // Each kingdom becomes an instance of one of the authored regions
        // (hollow_mire, glacial_threshold, …). This unifies the two region
        // concepts: a kingdom IS a region, so its archmage, encounters,
        // terrain palette and flavor all flow from one assignment. The
        // convergence territory is Kassian's seat → always "the_convergence".
        string convergenceKingdom = kingdomIds[capitals.FindIndex(c => c.x == convergence.x && c.y == convergence.y)];

        // Real region pool, excluding the convergence (reserved for Kassian).
        var allRegions = RegionLoader.LoadAll();
        allRegions.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var realRegionPool = allRegions
            .Where(r => r.Id != "the_convergence")
            .Select(r => r.Id)
            .ToList();
        // Deterministic shuffle so assignment is stable per seed.
        ShuffleDeterministic(realRegionPool, rng);

        // kingdom_N -> real region id (convergence handled separately).
        var kingdomRegion = new Dictionary<string, string>();
        int regionCursor = 0;
        foreach (var id in kingdomIds)
        {
            if (id == convergenceKingdom)
            {
                kingdomRegion[id] = "the_convergence";
                continue;
            }
            string region = realRegionPool.Count > 0
                ? realRegionPool[regionCursor % realRegionPool.Count]
                : "frontier_wilds";
            kingdomRegion[id] = region;
            regionCursor++;
        }

        // Feed the REAL region ids (not kingdom_N) to the campaign generator,
        // so archmagi are placed onto real regions. Tier carries through.
        var placeables = new List<PlaceableRegion>();
        foreach (var id in kingdomIds)
        {
            if (id == convergenceKingdom)
                continue;
            placeables.Add(new PlaceableRegion { Id = kingdomRegion[id], Tier = tierOf[id] });
        }
        var campaign = CampaignGenerator.Generate(seed, playerSchool, placeables);

        // ── 5. KingdomState per territory ────────────────────────────────
        var kingdoms = new Dictionary<string, KingdomState>();
        for (int i = 0; i < capitals.Count; i++)
        {
            string id = kingdomIds[i];
            bool isStart = (i == 0);
            bool isConvergence = (id == convergenceKingdom);
            string region = kingdomRegion[id];
            // Archmage is now looked up by the REAL region id the campaign used.
            string archmageId = isConvergence ? "" : campaign.GetArchmageForRegion(region);

            kingdoms[id] = new KingdomState
            {
                RegionId = id,
                TemplateRegionId = region,
                DisplayName = id,
                ControllingFactionId = isConvergence ? "" : factionOf[id],
                Stance = isStart ? KingdomStance.Friendly : KingdomStance.Neutral,
                Tier = isConvergence ? 3 : tierOf[id],
                Stability = 50,
                PlayerInfluence = isStart ? 25 : 0,
                ArchmageId = archmageId,
            };
            GD.Print($"[WorldGen] {id} -> region '{region}'" +
                     (string.IsNullOrEmpty(archmageId) ? " (no archmage)" : $" (archmage {archmageId})"));
        }

        // ── 5b. Per-region terrain: reclassify each land tile through its
        // kingdom's region palette, reusing the SHARED continuous field's
        // elevation/moisture (already stored per tile). The field stays global
        // so terrain flows seamlessly across borders; only the *interpretation*
        // varies by region, so a glacial kingdom reads icy/mountainous and a
        // verdant one reads wet/forested without hard seams. Water is preserved
        // so coastlines + territory assignment stay stable.
        ReclassifyTerrainPerRegion(world, kingdoms);

        // ── 5c. Stamp Hills/Mountain from the final (uplifted) elevation,
        // AFTER the per-region repaint so the mountain structure is globally
        // coherent. Lowlands keep their regional identity; biome Volcanic is
        // preserved.
        MountainShaper.ClassifyHighlands(world);

        // ── 5d. Climate: latitude − elevation lapse → a late terrain override.
        // Desert (hot+dry), Tundra (cold), Snow (very cold or tall/cold peaks).
        // After the region repaint + highlands so it wins; before settlements so
        // towns see the final biome.
        Climate.Apply(world, seed);

        // ── 5e. Bathymetry: ocean depth-from-shore, for shallow→deep shading and
        // (later) ship navigation. After all terrain passes settle land/water.
        Bathymetry.Apply(world, seed);

        // ── 5f. Settlements: grow City/Town AREAS (cities on the seats, towns by
        // suitability). Areas only — ScatterPois studs them with POIs next.
        Settlements.Generate(world, kingdoms, capitals, kingdomIds, convergenceKingdom, rng);

        // ── 5g. Roads: MST over each landmass's settlements, stamped as Road on
        // wilderness tiles with bridges where they ford rivers. Before ScatterPois
        // so POIs can land on waystations; after Settlements so the nodes exist.
        Roads.Generate(world);

        // ── 5h. Road-junction towns: a settlement at every road convergence (3+
        // road edges), regardless of the per-kingdom town cap.
        Settlements.AddJunctionTowns(world);

        // ── 6. Corruption gradient toward the convergence seat ───────────
        ApplyCorruptionGradient(world, kingdoms, campaign, convergence);

        // ── 7. POIs (mostly undiscovered) + kingdom seats ────────────────
        ScatterPois(world, kingdoms, convergenceKingdom, capitals, kingdomIds, p, rng);

        // ── 8. Starting staging point + a few pre-discovered POIs ────────
        SeedStaging(world, start, p, rng);

        GD.Print($"[WorldGenerator] World {p.Width}x{p.Height} seed={seed}: " +
                 $"{kingdoms.Count} territories, convergence='{convergenceKingdom}' " +
                 $"at ({convergence.x},{convergence.y}), " +
                 $"{world.Pois.Count} POIs, {world.StagingPoints.Count} staging point(s).");

        return new GeneratedWorldData { World = world, Kingdoms = kingdoms, Campaign = campaign };
    }

    // ── 1. Terrain ───────────────────────────────────────────────────────
    private static void FillTerrain(WorldData world, int seed, ContinentStyle? styleOverride)
    {
        var field = new OverworldField(seed);
        // Lower frequencies than a 15x15 region so biomes read as continental
        // bands rather than noise at world scale.
        field.ElevationFrequency = 0.018f;
        field.MoistureFrequency = 0.013f;
        field.ApplyFrequencies();

        var style = styleOverride ?? ContinentShaper.RollStyle(seed);
        var shape = ContinentShaper.Build(field, world.Width, world.Height, seed, style);
        world.ContinentStyle = style.ToString();

        // LAND-only palette: ocean is decided by the continent mask, not by an
        // elevation threshold, so the Water rule is dropped here.
        var landPalette = LandOnlyWorldPalette();

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int i = y * world.Width + x;
                float e = shape.Elevation[i];
                float m = field.SampleMoisture01(new Vector2I(x, y));

                var terrain = shape.IsOcean[i]
                    ? (shape.IsEnclosed[i] ? OverworldHex.TerrainType.Lake
                                           : OverworldHex.TerrainType.Water)
                    : field.ClassifyByPalette(landPalette, e, m);

                world.Tiles[i] = new WorldTile
                {
                    Terrain = terrain,
                    Elevation = e,
                    Moisture = m,
                    KingdomId = "",
                    Corruption = 0,
                    Discovery = TileDiscovery.Unseen,
                    PoiIndex = -1,
                    IsStagingPoint = false,
                    SettlementIndex = -1,
                };
            }
        }

        GD.Print($"[WorldGen] Continent style={style}, land fraction={shape.LandFraction:P0}.");
    }

    /// <summary>The default world palette minus its Water rule. Ocean is set from
    /// the continent mask; this classifies LAND only, on land elevation in [0,1].</summary>
    private static List<OverworldPaletteRule> LandOnlyWorldPalette() => new()
    {
        new() { TerrainName = "Volcanic", MinElevation = 0.88f, MaxMoisture = 0.28f },
        new() { TerrainName = "Mountain", MinElevation = 0.84f },
        new() { TerrainName = "Swamp",    MaxElevation = 0.40f, MinMoisture = 0.66f },
        new() { TerrainName = "Forest",   MinMoisture = 0.55f },
        new() { TerrainName = "Grassland" },
    };

    /// <summary>Reclassify each LAND tile's terrain through its kingdom's region
    /// palette, reusing the shared field's stored elevation/moisture. Water tiles
    /// are left untouched so coastlines and territory stay stable. Kingdoms whose
    /// region has no palette (or that fail to load) keep the default-classified
    /// terrain. This is what gives each region its visual identity without
    /// reintroducing noise seams — the field is shared, only classification varies.</summary>
    private static void ReclassifyTerrainPerRegion(WorldData world,
        Dictionary<string, KingdomState> kingdoms)
    {
        // Cache each kingdom's LAND palette once (water rules stripped so we never
        // turn a land tile into water under a different palette).
        var landPaletteByKingdom = new Dictionary<string, List<OverworldPaletteRule>>();
        foreach (var kvp in kingdoms)
        {
            string regionId = kvp.Value.TemplateRegionId;
            if (string.IsNullOrEmpty(regionId))
                continue;

            var def = RegionLoader.LoadOrDefault(regionId);
            if (def == null || def.BaseTerrain == null || !def.BaseTerrain.HasPalette)
                continue;

            var landRules = def.BaseTerrain.Palette
                .Where(r => !TerrainClass.IsWater(r.Terrain))
                .ToList();
            if (landRules.Count == 0)
                continue;

            // Guarantee a catch-all so every land tile classifies to something.
            if (!landRules.Any(r => r.MaxElevation == null && r.MinElevation == null &&
                                    r.MaxMoisture == null && r.MinMoisture == null))
            {
                landRules.Add(new OverworldPaletteRule { TerrainName = "Grassland" });
            }
            landPaletteByKingdom[kvp.Key] = landRules;
        }

        if (landPaletteByKingdom.Count == 0)
            return; // no region palettes available — keep default terrain

        // A throwaway field instance only to reuse ClassifyByPalette (pure function
        // of the rules + e/m; no sampling here since e/m are already stored).
        var classifier = new OverworldField(0);

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int idx = y * world.Width + x;
                var tile = world.Tiles[idx];

                // Preserve water AND coast — coastlines and territory depend on it.
                if (tile.IsWater || tile.IsCoast)
                    continue;
                if (string.IsNullOrEmpty(tile.KingdomId))
                    continue;
                if (!landPaletteByKingdom.TryGetValue(tile.KingdomId, out var rules))
                    continue;

                var newTerrain = classifier.ClassifyByPalette(rules, tile.Elevation, tile.Moisture);
                if (TerrainClass.IsWater(newTerrain))
                    continue; // never introduce water mid-territory
                tile.Terrain = newTerrain;
                world.Tiles[idx] = tile;
            }
        }

        GD.Print($"[WorldGen] Reclassified terrain for {landPaletteByKingdom.Count} region palette(s).");
    }

    private static List<OverworldPaletteRule> DefaultWorldPalette() => new()
    {
        // Water threshold lowered (0.30 -> 0.18) to compensate for the field's
        // redistribution, which spreads the low end and would otherwise flood
        // ~37% of the map. 0.18 restores ~19% water (close to the pre-redistribution
        // layout) so continents stay intact for territory/POI placement.
        new() { TerrainName = "Water",    MaxElevation = 0.18f },
        new() { TerrainName = "Volcanic", MinElevation = 0.88f, MaxMoisture = 0.28f },
        new() { TerrainName = "Mountain", MinElevation = 0.84f },
        new() { TerrainName = "Swamp",    MaxElevation = 0.40f, MinMoisture = 0.66f },
        new() { TerrainName = "Forest",   MinMoisture = 0.55f },
        new() { TerrainName = "Grassland" },
    };

    // ── 2. Capitals + territory partition ────────────────────────────────
    private static List<(int x, int y)> PlaceCapitals(WorldData world, Params p,
                                                      RandomNumberGenerator rng)
    {
        // Farthest-point sampling over walkable land so capitals spread out.
        var land = new List<(int x, int y)>();
        for (int y = 0; y < world.Height; y++)
            for (int x = 0; x < world.Width; x++)
            {
                var t = world.GetTile(x, y).Terrain;
                if (TerrainClass.IsLand(t))
                    land.Add((x, y));
            }

        var capitals = new List<(int x, int y)>();
        // First capital: a seeded random land tile in the interior third
        // (so the start isn't jammed in a corner).
        var interior = land.Where(c =>
            c.x > world.Width / 5 && c.x < 4 * world.Width / 5 &&
            c.y > world.Height / 5 && c.y < 4 * world.Height / 5).ToList();
        var pool = interior.Count > 0 ? interior : land;
        capitals.Add(pool[(int)(rng.Randi() % (uint)pool.Count)]);

        while (capitals.Count < p.KingdomCount && capitals.Count < land.Count)
        {
            (int x, int y) best = land[0];
            int bestMin = -1;
            // Sample a subset for speed at world scale rather than scanning all land each time.
            int samples = Mathf.Min(land.Count, 1200);
            for (int s = 0; s < samples; s++)
            {
                var cand = land[(int)(rng.Randi() % (uint)land.Count)];
                int minD = int.MaxValue;
                foreach (var cap in capitals)
                    minD = Mathf.Min(minD, Dist(cand, cap));
                if (minD > bestMin)
                { bestMin = minD; best = cand; }
            }
            capitals.Add(best);
        }
        return capitals;
    }

    /// <summary>Assigns every land tile to its nearest capital's kingdom
    /// (hex). Water stays wilderness. Returns the per-capital kingdom ids
    /// in capital order.</summary>
    private static List<string> AssignTerritories(WorldData world, List<(int x, int y)> capitals)
    {
        var ids = new List<string>();
        for (int i = 0; i < capitals.Count; i++)
            ids.Add($"kingdom_{i}");

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int idx = y * world.Width + x;
                if (world.Tiles[idx].IsWater)
                    continue;

                int nearest = 0, bestD = int.MaxValue;
                for (int i = 0; i < capitals.Count; i++)
                {
                    int d = Dist((x, y), capitals[i]);
                    if (d < bestD)
                    { bestD = d; nearest = i; }
                }
                world.Tiles[idx].KingdomId = ids[nearest];
            }
        }
        return ids;
    }

    // ── 3. Factions ──────────────────────────────────────────────────────
    private static Dictionary<string, string> AssignFactions(
        List<(int x, int y)> capitals, List<string> kingdomIds,
        RandomNumberGenerator rng)
    {
        var factions = FactionRegistry.All;
        var shuffled = ShuffleList(factions, rng);
        var result = new Dictionary<string, string>();
        // Round-robin factions across territories; coherent already because
        // territories are contiguous regions and neighbors share a capital cluster.
        for (int i = 0; i < kingdomIds.Count; i++)
            result[kingdomIds[i]] = shuffled[i % shuffled.Count].Id;
        return result;
    }

    // ── 6. Corruption gradient ───────────────────────────────────────────
    // ── Region template assignment ───────────────────────────────────────

    /// <summary>In-place Fisher–Yates shuffle using the world RNG, so kingdom→
    /// region assignment is deterministic per seed.</summary>
    private static void ShuffleDeterministic<T>(List<T> list, RandomNumberGenerator rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.Randi() % (uint)(i + 1));
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static void ApplyCorruptionGradient(WorldData world,
        Dictionary<string, KingdomState> kingdoms, CampaignState campaign,
        (int x, int y) convergence)
    {
        // Tile-level: a corruption bloom around the seat on the 0–100 scale,
        // falling off with distance. The convergence core starts heavily
        // corrupted; this is the source the per-lunation spread radiates from.
        int bloom = Mathf.Max(world.Width, world.Height) / 6;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int d = Dist((x, y), convergence);
                if (d <= bloom)
                {
                    int idx = y * world.Width + x;
                    if (world.Tiles[idx].IsLand)
                    {
                        // 100 at the seat, falling to ~20 at the bloom edge.
                        float t = 1f - (float)d / bloom;
                        int corruption = Mathf.RoundToInt(Mathf.Lerp(20f, 100f, t));
                        world.Tiles[idx].Corruption = (byte)Mathf.Clamp(corruption, 0, 100);
                    }
                }
            }
        }

        // Pre-warm the kingdom whose capital is nearest the seat (excluding the
        // seat's own territory) on the 0–3 territory scale.
        foreach (var kvp in kingdoms)
        {
            if (string.IsNullOrEmpty(kvp.Value.ArchmageId))
                continue;
            if (kvp.Value.Tier >= 3)
                campaign.CorruptionLevels[kvp.Value.TemplateRegionId] = 1;
        }
    }

    // ── 7. POIs ──────────────────────────────────────────────────────────
    private static void ScatterPois(WorldData world,
        Dictionary<string, KingdomState> kingdoms, string convergenceKingdom,
        List<(int x, int y)> capitals, List<string> kingdomIds,
        Params p, RandomNumberGenerator rng)
    {
        // 1. Archmage seats — each sits at its kingdom's primary (seat) city centre,
        //    and is that city's staging POI.
        for (int i = 0; i < capitals.Count; i++)
        {
            string id = kingdomIds[i];
            if (id == convergenceKingdom)
                continue;
            if (!kingdoms.TryGetValue(id, out var ks) || string.IsNullOrEmpty(ks.ArchmageId))
                continue;
            AddPoi(world, capitals[i].x, capitals[i].y, PoiKind.Seat, id, grantsStaging: true);
        }

        // 2. Stud settlements: cities dense + civilized, towns sparse. Non-seat
        //    cities get a Settlement staging POI at centre (seat cities use the Seat).
        PoiKind[] cityKinds = { PoiKind.Rest, PoiKind.Rest, PoiKind.Negotiation, PoiKind.Narrative, PoiKind.Combat };
        PoiKind[] townKinds = { PoiKind.Rest, PoiKind.Negotiation, PoiKind.Combat };

        foreach (var s in world.Settlements)
        {
            bool isCity = s.Tier == SettlementTier.City;

            if (isCity && !s.IsSeat && world.GetTile(s.CenterX, s.CenterY).PoiIndex < 0)
                AddPoi(world, s.CenterX, s.CenterY, PoiKind.Settlement, s.KingdomId, grantsStaging: true);

            var kinds = isCity ? cityKinds : townKinds;
            float frac = isCity ? p.CityStudFraction : p.TownStudFraction;
            int target = Mathf.RoundToInt(s.Tiles.Count * frac);
            if (isCity)
                target = Mathf.Max(target, 2);

            int placed = 0, attempts = 0, maxAttempts = s.Tiles.Count * 4 + 8;
            while (placed < target && attempts < maxAttempts)
            {
                attempts++;
                var (x, y) = s.Tiles[(int)(rng.Randi() % (uint)s.Tiles.Count)];
                if (world.GetTile(x, y).PoiIndex >= 0)
                    continue;   // occupied (centre seat/staging, or already studded)
                var kind = kinds[(int)(rng.Randi() % (uint)kinds.Length)];
                AddPoi(world, x, y, kind, s.KingdomId, grantsStaging: false);
                placed++;
            }
        }

        // 3. Wilderness scatter on non-settlement tiles: thinner, martial. Outposts
        //    still grant staging so the exploration loop can bootstrap from the wild.
        PoiKind[] wildKinds = { PoiKind.Combat, PoiKind.Combat, PoiKind.Combat, PoiKind.Outpost, PoiKind.Rest };

        foreach (var id in kingdomIds)
        {
            if (id == convergenceKingdom)
                continue;
            var tiles = WildTilesOfKingdom(world, id);
            if (tiles.Count == 0)
                continue;

            int count = p.WildPoiPerKingdom;
            var placedList = new List<(int x, int y)>();
            int attempts = 0, maxAttempts = count * 12;

            while (placedList.Count < count && attempts < maxAttempts)
            {
                attempts++;
                var (x, y) = tiles[(int)(rng.Randi() % (uint)tiles.Count)];
                if (world.GetTile(x, y).PoiIndex >= 0)
                    continue;
                if (TooClose(placedList, x, y, 2))
                    continue;

                PoiKind kind = wildKinds[(int)(rng.Randi() % (uint)wildKinds.Length)];
                bool staging = kind == PoiKind.Outpost;
                AddPoi(world, x, y, kind, id, grantsStaging: staging);
                placedList.Add((x, y));
            }
        }
    }

    /// <summary>Kingdom land tiles NOT inside any settlement (the wilderness).</summary>
    private static List<(int x, int y)> WildTilesOfKingdom(WorldData world, string id)
    {
        var result = new List<(int x, int y)>();
        for (int y = 0; y < world.Height; y++)
            for (int x = 0; x < world.Width; x++)
            {
                var t = world.GetTile(x, y);
                if (t.KingdomId == id && t.SettlementIndex < 0)
                    result.Add((x, y));
            }
        return result;
    }

    /// <summary>True if (x,y) is within minDist hexes of any already-placed POI.
    /// Keeps POIs from clumping so windows read as populated, not piled.</summary>
    private static bool TooClose(List<(int x, int y)> placed, int x, int y, int minDist)
    {
        foreach (var (px, py) in placed)
            if (HexCoord.OffsetDistance(px, py, x, y) < minDist)
                return true;
        return false;
    }

    private static void AddPoi(WorldData world, int x, int y, PoiKind kind,
                               string kingdomId, bool grantsStaging)
    {
        int poiIndex = world.Pois.Count;
        world.Pois.Add(new WorldPoi
        {
            X = x,
            Y = y,
            Kind = kind,
            KingdomId = kingdomId,
            Discovered = false,
            GrantsStaging = grantsStaging,
        });
        int idx = y * world.Width + x;
        var t = world.Tiles[idx];
        t.PoiIndex = poiIndex;
        world.Tiles[idx] = t;
    }

    // ── 8. Staging ───────────────────────────────────────────────────────
    private static void SeedStaging(WorldData world, (int x, int y) start, Params p,
                                    RandomNumberGenerator rng)
    {
        var t = world.GetTile(start.x, start.y);
        t.IsStagingPoint = true;
        t.Discovery = TileDiscovery.Explored; // the start is known
        world.SetTile(start.x, start.y, t);

        world.StagingPoints.Add(new StagingPoint
        {
            X = start.x,
            Y = start.y,
            Name = "Home Camp",
            Source = "Start",
            Available = true,
        });

        string startKingdom = world.GetTile(start.x, start.y).KingdomId ?? "";
        // Near outpost: inside the first window, home kingdom is fine — it bootstraps the loop.
        SeedBootstrapOutpost(world, start, minD: 10, maxD: 12, rng, "Frontier Outpost", foreignTo: null);
        // Distant outpost: MUST be in a different kingdom, so its window reaches foreign ground.
        // This is the anti-softlock guarantee — without it every staging point can stay home.
        SeedBootstrapOutpost(world, start, minD: 13, maxD: 18, rng, "Distant Outpost", foreignTo: startKingdom);

        // Pre-discover the nearest few ordinary POIs too, so the first strategic
        // view has texture beyond the guaranteed outposts.
        var nearest = world.Pois
            .Where(poi => !poi.Discovered)
            .Select((poi, i) => (poi, d: Dist((poi.X, poi.Y), start)))
            .OrderBy(t2 => t2.d)
            .Take(p.PreDiscoveredPois);
        foreach (var (poi, _) in nearest)
            poi.Discovered = true;
    }

    /// <summary>Force a discovered, staging-granting Outpost POI onto a walkable
    /// land tile within [minD, maxD] hex distance of the start. Guarantees the
    /// exploration loop can bootstrap a second staging point.</summary>
    private static void SeedBootstrapOutpost(WorldData world, (int x, int y) start,
                                                 int minD, int maxD,
                                                 RandomNumberGenerator rng, string name,
                                                 string foreignTo)
    {
        var candidates = new List<(int x, int y)>();
        var foreignCandidates = new List<(int x, int y)>();
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int d = Dist((x, y), start);
                if (d < minD || d > maxD)
                    continue;
                var tile = world.GetTile(x, y);
                if (tile.IsWater)
                    continue;
                if (tile.PoiIndex >= 0)
                    continue;
                if (tile.IsStagingPoint)
                    continue;
                candidates.Add((x, y));
                if (!string.IsNullOrEmpty(foreignTo) &&
                    !string.IsNullOrEmpty(tile.KingdomId) &&
                    tile.KingdomId != foreignTo)
                    foreignCandidates.Add((x, y));
            }
        }

        // Prefer a foreign-kingdom site when one is required and available.
        var pickList = (foreignTo != null && foreignCandidates.Count > 0)
            ? foreignCandidates
            : candidates;

        if (foreignTo != null && foreignCandidates.Count == 0)
            GD.PushWarning($"[WorldGenerator] No FOREIGN bootstrap site for '{name}' in ring " +
                           $"[{minD},{maxD}] — falling back to home kingdom; softlock risk.");

        if (pickList.Count == 0)
        {
            GD.PushWarning($"[WorldGenerator] No bootstrap-outpost site in ring " +
                           $"[{minD},{maxD}] — staging may not bootstrap.");
            return;
        }

        var (ox, oy) = pickList[(int)(rng.Randi() % (uint)pickList.Count)];
        int poiIndex = world.Pois.Count;
        world.Pois.Add(new WorldPoi
        {
            X = ox,
            Y = oy,
            Kind = PoiKind.Outpost,
            KingdomId = world.GetTile(ox, oy).KingdomId ?? "",
            Discovered = true,
            GrantsStaging = true,
        });
        int idx = oy * world.Width + ox;
        var ot = world.Tiles[idx];
        ot.PoiIndex = poiIndex;
        world.Tiles[idx] = ot;

        GD.Print($"[WorldGenerator] Bootstrap outpost '{name}' at ({ox},{oy}), " +
                 $"hex distance {Dist((ox, oy), start)} from start, kingdom '{world.GetTile(ox, oy).KingdomId}'.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    private static List<(int x, int y)> TilesOfKingdom(WorldData world, string id)
    {
        var result = new List<(int x, int y)>();
        for (int y = 0; y < world.Height; y++)
            for (int x = 0; x < world.Width; x++)
                if (world.GetTile(x, y).KingdomId == id)
                    result.Add((x, y));
        return result;
    }

    // World coords (x,y) ARE offset (col,row). Distance is hexagonal — the
    // world is a Civ-6-style rectangular hex map (flat-top, odd-q).
    private static int Dist((int x, int y) a, (int x, int y) b)
        => HexCoord.OffsetDistance(a.x, a.y, b.x, b.y);

    private static int DistanceToTier(int dist, int maxDist)
    {
        if (maxDist <= 0)
            return 1;
        float t = (float)dist / maxDist;
        if (t < 0.34f)
            return 1;
        if (t < 0.67f)
            return 2;
        return 3;
    }

    private static List<T> ShuffleList<T>(List<T> list, RandomNumberGenerator rng)
    {
        var result = new List<T>(list);
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.Randi() % (uint)(i + 1));
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }
}
