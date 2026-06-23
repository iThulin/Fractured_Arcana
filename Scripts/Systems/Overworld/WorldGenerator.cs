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
//                 WorldData.cs (output), CycleState.cs (stores)
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
        public int Width = 96;
        public int Height = 96;
        public int KingdomCount = 10;     // territories partitioned across the surface
        public float WaterLevel = 0.30f;  // elevation below this is unwalkable water (avoid as capitals/POIs)
        public int PoiPerKingdom = 8;     // rough POI density per territory
        public int PreDiscoveredPois = 3; // POIs visible from the start, near the staging point
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
        FillTerrain(world, seed);

        // ── 2. Territory partition (capitals → graph Voronoi) ────────────
        var capitals = PlaceCapitals(world, p, rng);   // one per kingdom
        var kingdomIds = AssignTerritories(world, capitals);

        // Convergence: the capital farthest from the player's start capital.
        // Start capital = capitals[0]; convergence = farthest capital cell.
        var start = capitals[0];
        var convergence = capitals
            .Skip(1)
            .OrderByDescending(c => Cheb(c, start))
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
            int d = Cheb(kvp.c, start);
            distOf[kvp.id] = d;
            if (d > maxDist) maxDist = d;
        }
        foreach (var id in kingdomIds)
            tierOf[id] = DistanceToTier(distOf[id], maxDist);

        // ── 4. Archmage placement via the existing CampaignGenerator ─────
        // Territories become PlaceableRegions with distance-derived tiers.
        // Exclude the convergence territory from placement (Kassian's seat).
        string convergenceKingdom = kingdomIds[capitals.FindIndex(c => c.x == convergence.x && c.y == convergence.y)];
        var placeables = new List<PlaceableRegion>();
        foreach (var id in kingdomIds)
        {
            if (id == convergenceKingdom) continue;
            placeables.Add(new PlaceableRegion { Id = id, Tier = tierOf[id] });
        }
        var campaign = CampaignGenerator.Generate(seed, playerSchool, placeables);

        // ── 5. KingdomState per territory ────────────────────────────────
        var kingdoms = new Dictionary<string, KingdomState>();
        for (int i = 0; i < capitals.Count; i++)
        {
            string id = kingdomIds[i];
            bool isStart = (i == 0);
            bool isConvergence = (id == convergenceKingdom);

            kingdoms[id] = new KingdomState
            {
                RegionId = id,
                TemplateRegionId = id,
                DisplayName = id,
                ControllingFactionId = isConvergence ? "" : factionOf[id],
                Stance = isStart ? KingdomStance.Friendly : KingdomStance.Neutral,
                Tier = isConvergence ? 3 : tierOf[id],
                Stability = 50,
                PlayerInfluence = isStart ? 25 : 0,
                ArchmageId = campaign.GetArchmageForRegion(id),
            };
        }

        // ── 6. Corruption gradient toward the convergence seat ───────────
        ApplyCorruptionGradient(world, kingdoms, campaign, convergence);

        // ── 7. POIs (mostly undiscovered) + kingdom seats ────────────────
        ScatterPois(world, kingdoms, convergenceKingdom, capitals, kingdomIds, p, rng);

        // ── 8. Starting staging point + a few pre-discovered POIs ────────
        SeedStaging(world, start, p);

        GD.Print($"[WorldGenerator] World {p.Width}x{p.Height} seed={seed}: " +
                 $"{kingdoms.Count} territories, convergence='{convergenceKingdom}' " +
                 $"at ({convergence.x},{convergence.y}), " +
                 $"{world.Pois.Count} POIs, {world.StagingPoints.Count} staging point(s).");

        return new GeneratedWorldData { World = world, Kingdoms = kingdoms, Campaign = campaign };
    }

    // ── 1. Terrain ───────────────────────────────────────────────────────
    private static void FillTerrain(WorldData world, int seed)
    {
        var field = new OverworldField(seed);
        // Lower frequencies than a 15x15 region so biomes read as continental
        // bands rather than noise at world scale.
        field.ElevationFrequency = 0.018f;
        field.MoistureFrequency = 0.013f;
        field.ApplyFrequencies();

        var palette = DefaultWorldPalette();

        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var axial = new Vector2I(x, y);
                float e = field.SampleElevation01(axial);
                float m = field.SampleMoisture01(axial);

                world.Tiles[y * world.Width + x] = new WorldTile
                {
                    Terrain = field.ClassifyByPalette(palette, e, m),
                    Elevation = e,
                    Moisture = m,
                    KingdomId = "",
                    Corruption = 0,
                    Discovery = TileDiscovery.Unseen,
                    PoiIndex = -1,
                    IsStagingPoint = false,
                };
            }
        }
    }

    private static List<OverworldPaletteRule> DefaultWorldPalette() => new()
    {
        new() { TerrainName = "Water",    MaxElevation = 0.30f },
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
                if (t != OverworldHex.TerrainType.Water)
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
                    minD = Mathf.Min(minD, Cheb(cand, cap));
                if (minD > bestMin) { bestMin = minD; best = cand; }
            }
            capitals.Add(best);
        }
        return capitals;
    }

    /// <summary>Assigns every land tile to its nearest capital's kingdom
    /// (Chebyshev). Water stays wilderness. Returns the per-capital kingdom ids
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
                if (world.Tiles[idx].Terrain == OverworldHex.TerrainType.Water)
                    continue;

                int nearest = 0, bestD = int.MaxValue;
                for (int i = 0; i < capitals.Count; i++)
                {
                    int d = Cheb((x, y), capitals[i]);
                    if (d < bestD) { bestD = d; nearest = i; }
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
    private static void ApplyCorruptionGradient(WorldData world,
        Dictionary<string, KingdomState> kingdoms, CampaignState campaign,
        (int x, int y) convergence)
    {
        // Tile-level: a soft bloom around the seat, capped at 1 so nothing
        // auto-falls at generation. Kingdom-level corruption (CampaignState)
        // gets the nearest ring pre-warmed to 1.
        int bloom = Mathf.Max(world.Width, world.Height) / 8;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int d = Cheb((x, y), convergence);
                if (d <= bloom)
                {
                    int idx = y * world.Width + x;
                    if (world.Tiles[idx].Terrain != OverworldHex.TerrainType.Water)
                        world.Tiles[idx].Corruption = 1;
                }
            }
        }

        // Pre-warm the kingdom whose capital is nearest the seat (excluding the
        // seat's own territory, which has no archmage).
        foreach (var kvp in kingdoms)
        {
            if (string.IsNullOrEmpty(kvp.Value.ArchmageId)) continue;
            // Cheap proxy: if any of this kingdom's region appears in campaign and
            // it's a tier-3 territory, give it baseline corruption 1.
            if (kvp.Value.Tier >= 3)
                campaign.CorruptionLevels[kvp.Key] = 1;
        }
    }

    // ── 7. POIs ──────────────────────────────────────────────────────────
    private static void ScatterPois(WorldData world,
        Dictionary<string, KingdomState> kingdoms, string convergenceKingdom,
        List<(int x, int y)> capitals, List<string> kingdomIds,
        Params p, RandomNumberGenerator rng)
    {
        // Kingdom seats first: each archmage-bearing kingdom's capital becomes a Seat POI.
        for (int i = 0; i < capitals.Count; i++)
        {
            string id = kingdomIds[i];
            if (id == convergenceKingdom) continue;
            if (!kingdoms.TryGetValue(id, out var ks) || string.IsNullOrEmpty(ks.ArchmageId))
                continue;

            AddPoi(world, capitals[i].x, capitals[i].y, PoiKind.Seat, id, grantsStaging: true);
        }

        // Scatter ordinary POIs per kingdom on walkable, non-seat tiles.
        PoiKind[] kinds =
        {
            PoiKind.Combat, PoiKind.Combat, PoiKind.Rest,
            PoiKind.Narrative, PoiKind.Negotiation, PoiKind.Outpost,
        };
        foreach (var id in kingdomIds)
        {
            if (id == convergenceKingdom) continue;
            var tiles = TilesOfKingdom(world, id);
            if (tiles.Count == 0) continue;

            int count = p.PoiPerKingdom;
            for (int n = 0; n < count; n++)
            {
                var (x, y) = tiles[(int)(rng.Randi() % (uint)tiles.Count)];
                if (world.GetTile(x, y).PoiIndex >= 0) continue; // already a POI here
                PoiKind kind = kinds[(int)(rng.Randi() % (uint)kinds.Length)];
                bool staging = kind == PoiKind.Outpost; // outposts become staging points when secured
                AddPoi(world, x, y, kind, id, grantsStaging: staging);
            }
        }
    }

    private static void AddPoi(WorldData world, int x, int y, PoiKind kind,
                               string kingdomId, bool grantsStaging)
    {
        int poiIndex = world.Pois.Count;
        world.Pois.Add(new WorldPoi
        {
            X = x, Y = y, Kind = kind, KingdomId = kingdomId,
            Discovered = false, GrantsStaging = grantsStaging,
        });
        int idx = y * world.Width + x;
        var t = world.Tiles[idx];
        t.PoiIndex = poiIndex;
        world.Tiles[idx] = t;
    }

    // ── 8. Staging ───────────────────────────────────────────────────────
    private static void SeedStaging(WorldData world, (int x, int y) start, Params p)
    {
        var t = world.GetTile(start.x, start.y);
        t.IsStagingPoint = true;
        t.Discovery = TileDiscovery.Explored; // the start is known
        world.SetTile(start.x, start.y, t);

        world.StagingPoints.Add(new StagingPoint
        {
            X = start.x, Y = start.y, Name = "Home Camp", Source = "Start", Available = true,
        });

        // Pre-discover the nearest few POIs so the first strategic view has
        // something to aim at.
        var nearest = world.Pois
            .Select((poi, i) => (poi, i, d: Cheb((poi.X, poi.Y), start)))
            .OrderBy(t2 => t2.d)
            .Take(p.PreDiscoveredPois);
        foreach (var (poi, _, _) in nearest)
            poi.Discovered = true;
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

    private static int Cheb((int x, int y) a, (int x, int y) b)
        => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));

    private static int DistanceToTier(int dist, int maxDist)
    {
        if (maxDist <= 0) return 1;
        float t = (float)dist / maxDist;
        if (t < 0.34f) return 1;
        if (t < 0.67f) return 2;
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
