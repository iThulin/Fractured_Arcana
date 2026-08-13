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
    public CouncilState Council = new();
}

/// <summary>Builds a complete Civ-scale world from a seed. Headless.</summary>
public static class WorldGenerator
{
    private const string CONVERGENCE_ID = "the_convergence";
    private static int RegionTierOf(string regionId)
        => RegionLoader.LoadOrDefault(regionId)?.BaseDifficultyTier ?? 1;

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

        // ── Founding-scenario levers (defaults reproduce shipping behaviour) ──
        // Fractional start-capital hint (0..1 of Width/Height); < 0 = legacy
        // interior-third random. Both must be >= 0 to take effect.
        public float StartHintX = -1f;
        public float StartHintY = -1f;
        // One knob, two coupled effects: scales where the Convergence lands (as a
        // fraction of the max capital distance) AND the tier-ramp steepness.
        // 1.0 = shipping; < 1 nearer + steeper; > 1 farther-clamped + gentler.
        public float ConvergenceDistanceBias = 1f;
        // Bootstrap staging outposts seeded near home (1..3). 2 = shipping.
        public int StartingOutposts = 2;
        // Seeded PlayerInfluence at the home kingdom. 25 = shipping.
        public int StartInfluence = 25;

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

        var start = capitals[0];
        // Convergence: the capital whose distance from the start is closest to
        // (bias × the max capital distance). bias 1.0 => the farthest capital
        // (shipping behaviour, since abs-diff is minimised at the max); bias < 1
        // => a nearer capital, shortening the ramp.
        int maxCapDist = capitals.Skip(1).Max(c => Dist(c, start));
        int targetConvDist = Mathf.RoundToInt(
            Mathf.Clamp(p.ConvergenceDistanceBias, 0.1f, 4f) * maxCapDist);
        var convergence = capitals
            .Skip(1)
            .OrderBy(c => Mathf.Abs(Dist(c, start) - targetConvDist))
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
            tierOf[id] = DistanceToTier(distOf[id], maxDist, p.ConvergenceDistanceBias);

        // ── 4. Assign each kingdom a REAL region, then place archmagi ────
        // Each kingdom becomes an instance of one of the authored regions
        // (hollow_mire, glacial_threshold, …). This unifies the two region
        // concepts: a kingdom IS a region, so its archmage, encounters,
        // terrain palette and flavor all flow from one assignment. The
        // convergence territory is Kassian's seat → always "the_convergence".
        string convergenceKingdom = kingdomIds[capitals.FindIndex(c => c.x == convergence.x && c.y == convergence.y)];

        // Match each territory to the region whose natural climate/terrain it best
        // fits (geography-driven identity), instead of a blind shuffle. The convergence
        // stays Kassian's seat; leftover territories fall back to frontier_wilds.
        var kingdomRegion = RegionMatcher.Match(world, kingdomIds, convergenceKingdom);

        // Feed the REAL region ids (not kingdom_N) to the campaign generator,
        // so archmagi are placed onto real regions. Tier carries through.
        var placeables = new List<PlaceableRegion>();
        foreach (var id in kingdomIds)
        {
            if (id == convergenceKingdom)
                continue;
            placeables.Add(new PlaceableRegion { Id = kingdomRegion[id], Tier = RegionTierOf(kingdomRegion[id]) });
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
                DisplayName = RegionLoader.LoadOrDefault(region)?.DisplayName ?? id,
                ControllingFactionId = isConvergence ? "" : factionOf[id],
                Tier = isConvergence ? 3 : RegionTierOf(region),
                Stability = 50,
                PlayerInfluence = isStart ? p.StartInfluence : 0,
                ArchmageId = archmageId,
            };
            GD.Print($"[WorldGen] {id} -> region '{region}'" +
                     (string.IsNullOrEmpty(archmageId) ? " (no archmage)" : $" (archmage {archmageId})"));
        }

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

        // ── 6b. Shard sub-regions: one vault footprint per fragment, near a
        // non-convergence seat. BEFORE ScatterPois so wilderness POIs avoid the
        // vault tiles (WildTilesOfKingdom excludes ShardZoneIndex >= 0).
        ShardZones.Generate(world, capitals, kingdomIds, convergenceKingdom, rng);

        // ── 7. POIs (mostly undiscovered) + kingdom seats ────────────────
        ScatterPois(world, kingdoms, convergenceKingdom, capitals, kingdomIds, p, rng);

    // ── 8. Starting staging point + a few pre-discovered POIs ────────
        SeedStaging(world, start, p, rng);

        // ── 9. Courts (Court & Council phase C1) ─────────────────────────
        // Own per-kingdom RNGs (seed ^ FNV1a(kingdomId)) — deliberately
        // does NOT consume from this method's rng, so existing world
        // output is bit-identical with or without court generation.
        var council = CourtGenerator.Generate(seed, kingdoms, convergenceKingdom);

        GD.Print($"[WorldGenerator] World {p.Width}x{p.Height} seed={seed}: " +
                 $"{kingdoms.Count} territories, convergence='{convergenceKingdom}' " +
                 $"at ({convergence.x},{convergence.y}), " +
                 $"{world.Pois.Count} POIs, {world.StagingPoints.Count} staging point(s).");

        return new GeneratedWorldData { World = world, Kingdoms = kingdoms, Campaign = campaign, Council = council };
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
                    ShardZoneIndex = -1,
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
        // First capital = the guild's start. A founding scenario may hint a
        // fractional position (StartHintX/Y in 0..1); snap to the nearest land
        // tile. Otherwise a seeded random land tile in the interior third (so the
        // start isn't jammed in a corner) — the legacy path, byte-identical.
        if (p.StartHintX >= 0f && p.StartHintY >= 0f && land.Count > 0)
        {
            int hx = Mathf.Clamp(Mathf.RoundToInt(p.StartHintX * (world.Width - 1)), 0, world.Width - 1);
            int hy = Mathf.Clamp(Mathf.RoundToInt(p.StartHintY * (world.Height - 1)), 0, world.Height - 1);
            (int x, int y) best = land[0];
            int bestD = int.MaxValue;
            foreach (var c in land)
            {
                int d = Dist(c, (hx, hy));
                if (d < bestD) { bestD = d; best = c; }
            }
            capitals.Add(best);
        }
        else
        {
            var interior = land.Where(c =>
                c.x > world.Width / 5 && c.x < 4 * world.Width / 5 &&
                c.y > world.Height / 5 && c.y < 4 * world.Height / 5).ToList();
            var pool = interior.Count > 0 ? interior : land;
            capitals.Add(pool[(int)(rng.Randi() % (uint)pool.Count)]);
        }

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

    // ── Territory cost-flood tuning ──────────────────────────────────────
    // Borders follow terrain: lowlands are cheap (fronts sprawl), elevation above
    // the lowland line ramps cost steeply (fronts stall on ridges), river edges
    // cost extra (fronts meet on rivers), water is impassable (coasts are borders).
    private const float TerritoryElevationWeight = 12f;
    private const float TerritoryLowlandLevel = 0.50f;
    private const float TerritoryRiverWeight = 5f;

    /// <summary>Partitions land among the capitals by a cost-weighted multi-source
    /// flood (a generalized Voronoi: unit cost would reproduce the old nearest-
    /// capital result). Cost is cheap on lowland, steep climbing into highland, and
    /// penalized crossing rivers; water blocks expansion. Borders therefore fall on
    /// ridgelines, rivers, and coasts. Orphaned land with no land-path to any capital
    /// (isolated islands) falls back to nearest-by-hex-distance so every land tile is
    /// still owned. Deterministic for a given world. Returns kingdom ids in capital
    /// order.</summary>
    private static List<string> AssignTerritories(WorldData world, List<(int x, int y)> capitals)
    {
        int w = world.Width, h = world.Height, n = w * h;

        var ids = new List<string>();
        for (int i = 0; i < capitals.Count; i++)
            ids.Add($"kingdom_{i}");

        var owner = new int[n];
        var cost = new float[n];
        var done = new bool[n];
        for (int i = 0; i < n; i++)
        {
            owner[i] = -1;
            cost[i] = float.MaxValue;
        }

        var pq = new PriorityQueue<int, float>();
        for (int i = 0; i < capitals.Count; i++)
        {
            int idx = capitals[i].y * w + capitals[i].x;
            owner[idx] = i;
            cost[idx] = 0f;
            pq.Enqueue(idx, 0f);
        }

        while (pq.Count > 0)
        {
            int cur = pq.Dequeue();
            if (done[cur])
                continue;
            done[cur] = true;

            int cx = cur % w, cy = cur / w;
            foreach (var (nx, ny) in HexCoord.Neighbors(cx, cy, w, h))
            {
                int ni = ny * w + nx;
                if (done[ni] || world.Tiles[ni].IsWater)
                    continue;

                float nc = cost[cur] + EnterCost(world, cx, cy, nx, ny);
                if (nc < cost[ni])
                {
                    cost[ni] = nc;
                    owner[ni] = owner[cur];
                    pq.Enqueue(ni, nc);
                }
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (world.Tiles[idx].IsWater)
                    continue;

                int o = owner[idx];
                if (o < 0)
                {
                    // Unreachable island: fall back to nearest capital by hex distance
                    // so no land tile is left ownerless (downstream assumes ownership).
                    int best = 0, bestD = int.MaxValue;
                    for (int i = 0; i < capitals.Count; i++)
                    {
                        int d = Dist((x, y), capitals[i]);
                        if (d < bestD)
                        { bestD = d; best = i; }
                    }
                    o = best;
                }
                world.Tiles[idx].KingdomId = ids[o];
            }
        }

        return ids;
    }

    /// <summary>Cost to expand into (toX,toY) from an adjacent owned tile: a unit step
    /// plus a steep ramp for elevation above the lowland line, plus a river-ford
    /// penalty if the crossed edge carries a river.</summary>
    private static float EnterCost(WorldData world, int fromX, int fromY, int toX, int toY)
    {
        var to = world.Tiles[toY * world.Width + toX];
        float elevExcess = Mathf.Max(0f, to.Elevation - TerritoryLowlandLevel);
        float c = 1f + TerritoryElevationWeight * elevExcess;

        int d = TerritoryEdgeDir(fromX, fromY, toX, toY);
        if (d >= 0)
        {
            var from = world.Tiles[fromY * world.Width + fromX];
            if ((from.RiverEdges & (1 << d)) != 0)
                c += TerritoryRiverWeight;
        }
        return c;
    }

    /// <summary>Direction index (0..5, AxialDirections order) from one offset tile to
    /// an adjacent one, matching the river-edge bit convention. -1 if not adjacent.</summary>
    private static int TerritoryEdgeDir(int xa, int ya, int xb, int yb)
    {
        var (qa, ra) = HexCoord.OffsetToAxial(xa, ya);
        var (qb, rb) = HexCoord.OffsetToAxial(xb, yb);
        int dq = qb - qa, dr = rb - ra;
        for (int d = 0; d < 6; d++)
        {
            var (adq, adr) = HexCoord.AxialDirections[d];
            if (adq == dq && adr == dr)
                return d;
        }
        return -1;
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
            // The convergence: Kassian's seat and the final objective. A distinct POI
            // kind so it reads as the endgame target, not a normal capital. Fog-gated like
            // any seat, so it stays hidden until the player reaches it (dramatic reveal).
            // grantsStaging stays FALSE — it's an objective marker, not a deploy point;
            // the assault on it is a later phase and will hook here.
            int convIdx = kingdomIds.IndexOf(convergenceKingdom);
            if (convIdx >= 0)
                AddPoi(world, capitals[convIdx].x, capitals[convIdx].y,
                       PoiKind.Convergence, convergenceKingdom, grantsStaging: false);
            string id = kingdomIds[i];
            if (id == convergenceKingdom)
                continue;
            if (!kingdoms.TryGetValue(id, out var ks))
                continue;
            // Every kingdom has a capital/seat — the archmage is the *ruler*, not a
            // precondition for the seat existing. Archmage-less kingdoms are real
            // polities with a minor/unnamed ruler; they still get a seat + label.
            bool hasArchmage = !string.IsNullOrEmpty(ks.ArchmageId);
            AddPoi(world, capitals[i].x, capitals[i].y, PoiKind.Seat, id, grantsStaging: hasArchmage);
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

            // K3 (companion_item_systems v2.1 §5a): rescue POIs — found people.
            // At most ONE per kingdom, 35% chance, wilderness only. Rare by
            // design: rescues are the richest recruits narratively, so they
            // must not read as farm nodes. In-window the POI presents as a
            // Narrative site; ExpeditionManager routes it to a rescue
            // encounter when one is warranted.
            if (rng.Randi() % 100 < 35)
            {
                int rAttempts = 0;
                while (rAttempts < 12)
                {
                    rAttempts++;
                    var (x, y) = tiles[(int)(rng.Randi() % (uint)tiles.Count)];
                    if (world.GetTile(x, y).PoiIndex >= 0) continue;
                    if (TooClose(placedList, x, y, 2)) continue;
                    AddPoi(world, x, y, PoiKind.Companion, id, grantsStaging: false);
                    placedList.Add((x, y));
                    break;
                }
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
                if (t.KingdomId == id && t.SettlementIndex < 0 && t.ShardZoneIndex < 0)
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

    /// <summary>Runtime POI creation for systems outside worldgen (the council tick
    /// siting an Imprisonment gaol). Anchors on the kingdom's Seat, then places the
    /// POI on the nearest kingdom-owned, non-settlement, unoccupied land tile
    /// (WildTilesOfKingdom is land-only — water carries no KingdomId). Marks the POI
    /// discovered and reveals its tile so the marker shows and the rescue is
    /// reachable, and RETURNS the new POI index for the caller to back-reference.
    /// Returns -1 if the kingdom has no seat or no free wild tile.</summary>
    public static int SiteRuntimePoi(WorldData world, PoiKind kind, string kingdomId)
    {
        if (world == null || string.IsNullOrEmpty(kingdomId))
            return -1;

        int capX = -1, capY = -1;
        foreach (var poi in world.Pois)
        {
            if (poi.Kind == PoiKind.Seat && poi.KingdomId == kingdomId)
            { capX = poi.X; capY = poi.Y; break; }
        }
        if (capX < 0)
            return -1; // no seat (convergence, or malformed) — cannot anchor

        int bestX = -1, bestY = -1, bestDist = int.MaxValue;
        foreach (var (x, y) in WildTilesOfKingdom(world, kingdomId))
        {
            if (world.GetTile(x, y).PoiIndex >= 0)
                continue; // occupied
            int d = HexCoord.OffsetDistance(capX, capY, x, y);
            if (d < bestDist)
            { bestDist = d; bestX = x; bestY = y; }
        }
        if (bestX < 0)
            return -1; // no free wild tile in the kingdom

        int index = world.Pois.Count;
        AddPoi(world, bestX, bestY, kind, kingdomId, grantsStaging: false);
        world.Pois[index].Discovered = true; // WorldPoi is a class — mutate in place

        var t = world.GetTile(bestX, bestY);
        if (t.Discovery == TileDiscovery.Unseen)
        {
            t.Discovery = TileDiscovery.Charted;
            world.SetTile(bestX, bestY, t);
        }
        return index;
    }

    // ── 8. Staging ───────────────────────────────────────────────────────
    private static void SeedStaging(WorldData world, (int x, int y) start, Params p,
                                    RandomNumberGenerator rng)
    {
        // Phase 2: the guild's home is a real place in the world — the start capital
        // (capitals[0]), whose seat city hosts the campus. Record the coordinate and
        // flag the settlement so renderers + the campus can locate it.
        world.HomeX = start.x;
        world.HomeY = start.y;
        var homeSettlement = world.SettlementAt(start.x, start.y);
        if (homeSettlement != null)
            homeSettlement.IsGuildHome = true;

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
        // The Distant (foreign) outpost is the anti-softlock guarantee — always
        // seeded (StartingOutposts >= 1). The near Frontier outpost is convenience,
        // seeded at >= 2 (shipping). An extra Waystation is seeded at >= 3.
        int outposts = Mathf.Clamp(p.StartingOutposts, 1, 3);
        if (outposts >= 2)
            // Near outpost: inside the first window, home kingdom is fine — it bootstraps the loop.
            SeedBootstrapOutpost(world, start, minD: 10, maxD: 12, rng, "Frontier Outpost", foreignTo: null);
        // Distant outpost: MUST be in a different kingdom, so its window reaches foreign ground.
        // This is the anti-softlock guarantee — without it every staging point can stay home.
        SeedBootstrapOutpost(world, start, minD: 13, maxD: 18, rng, "Distant Outpost", foreignTo: startKingdom);
        if (outposts >= 3)
            SeedBootstrapOutpost(world, start, minD: 8, maxD: 14, rng, "Waystation Outpost", foreignTo: null);

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
                if (tile.ShardZoneIndex >= 0)
                    continue; // never bootstrap a staging outpost inside a shard vault
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

    /// <summary>Per-cycle world seed from a founding base seed. Cycle 1 uses the
    /// base seed VERBATIM (the curated map the scenario was validated/balanced on);
    /// later cycles mix in the cycle number so each timeline differs while the
    /// founding difficulty levers stay constant. Deterministic.</summary>
    public static int DeriveCycleSeed(int baseSeed, int cycleNumber)
    {
        if (cycleNumber <= 1)
            return baseSeed;
        unchecked
        {
            uint h = (uint)baseSeed * 2654435761u;
            h ^= (uint)cycleNumber + 0x9E3779B9u + (h << 6) + (h >> 2);
            return (int)h;
        }
    }

    private static int DistanceToTier(int dist, int maxDist, float bias = 1f)
    {
        if (maxDist <= 0)
            return 1;
        float t = (float)dist / maxDist;
        // bias > 1 stretches the ramp (more of the map stays tier 1); bias < 1
        // compresses it (tier 3 reached sooner). bias 1.0 leaves t unchanged.
        if (bias > 0f)
            t /= bias;
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
