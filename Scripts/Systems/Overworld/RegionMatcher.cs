using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// RegionMatcher.cs
//
// Purpose:        Assigns each territory (kingdom) the authored region
//                 whose climate/terrain identity best fits that
//                 territory's NATURAL profile — so the icy region lands
//                 on the cold highland, the swamp region on the wet
//                 lowland, the volcanic region on the volcanic ground,
//                 instead of a blind shuffle. This is identity-by-match:
//                 terrain is never repainted; we pick the region that
//                 already suits the ground.
//
//                 Runs at WorldGenerator step 4 — AFTER territories +
//                 hydrology + uplift, BEFORE ClassifyHighlands / Climate.
//                 So it measures base terrain, elevation, moisture, ocean-
//                 adjacency, and a latitude/elevation TEMPERATURE PROXY
//                 (Climate hasn't run yet). Mountain/Snow/Desert don't
//                 exist at this point, which is why elevation stands in
//                 for "mountainous".
//
//                 Pool logic: the_convergence is reserved (Kassian);
//                 frontier_wilds is the leftover fallback; the remaining
//                 "specific" regions are matched 1:1 to best-fit
//                 territories by a deterministic global-greedy assignment.
// Layer:          System (generation helper)
// Collaborators:  RegionLoader / RegionDefinition (+ RegionAffinity),
//                 WorldData / WorldTile, HexCoord, WorldGenerator (caller).
// Notes:          Deterministic for a given world (pure function of tile
//                 data + region files; greedy order is fully sorted).
// ============================================================

public static class RegionMatcher
{
    private const string CONVERGENCE_ID = "the_convergence";
    private const string FALLBACK_ID = "frontier_wilds";

    // Axis weights. DominantTerrain is the loudest "obviously that region" signal;
    // temperature/coastal are distinctive; moisture/elevation are softer.
    private const float WTemp = 1.0f;
    private const float WMoisture = 0.8f;
    private const float WElevation = 0.8f;
    private const float WCoastal = 1.0f;
    private const float WDominant = 1.2f;

    private const float LapseRate = 0.60f;   // temperature falloff with elevation (mirrors Climate)

    // Axis target values, in the realized range of TERRITORY MEANS (not raw tile
    // extremes — means cluster toward the middle). Only ordering matters for the
    // match ranking; magnitudes set cross-axis balance. Tune freely.
    private static readonly Dictionary<string, float> TempTarget =
        new() { { "cold", 0.30f }, { "temperate", 0.55f }, { "warm", 0.78f } };
    private static readonly Dictionary<string, float> MoistTarget =
        new() { { "arid", 0.28f }, { "dry", 0.40f }, { "temperate", 0.52f }, { "wet", 0.68f } };
    private static readonly Dictionary<string, float> ElevTarget =
        new() { { "low", 0.38f }, { "mid", 0.50f }, { "high", 0.64f } };

    public struct Profile
    {
        public float Temp, Moisture, Elevation, CoastFrac;
        public float SwampFrac, ForestFrac, VolcanicFrac;
        public int LandCount;
    }

    /// <summary>Returns kingdomId → regionId for every kingdom, with the convergence
    /// kingdom fixed to the_convergence and any unmatched kingdoms set to the fallback.</summary>
    public static Dictionary<string, string> Match(
        WorldData world, List<string> kingdomIds, string convergenceKingdom)
    {
        var result = new Dictionary<string, string>();

        var profiles = MeasureProfiles(world, kingdomIds);

        var regions = RegionLoader.LoadAll();
        regions.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var specific = regions
            .Where(r => r.Id != CONVERGENCE_ID && r.Id != FALLBACK_ID)
            .ToList();

        var realKingdoms = kingdomIds.Where(k => k != convergenceKingdom).ToList();
        realKingdoms.Sort(string.CompareOrdinal);

        if (!string.IsNullOrEmpty(convergenceKingdom))
            result[convergenceKingdom] = CONVERGENCE_ID;

        // Score every (kingdom, specific-region) pair, then assign globally cheapest-
        // first, each kingdom and region used once. Fully sorted → deterministic.
        var pairs = new List<(float score, string k, string r)>();
        foreach (var k in realKingdoms)
            foreach (var reg in specific)
                pairs.Add((Score(profiles[k], reg), k, reg.Id));

        pairs.Sort((a, b) =>
            a.score != b.score ? a.score.CompareTo(b.score)
            : a.k != b.k ? string.CompareOrdinal(a.k, b.k)
            : string.CompareOrdinal(a.r, b.r));

        var takenK = new HashSet<string>();
        var takenR = new HashSet<string>();
        foreach (var (score, k, reg) in pairs)
        {
            if (takenK.Contains(k) || takenR.Contains(reg))
                continue;
            result[k] = reg;
            takenK.Add(k);
            takenR.Add(reg);
            if (takenK.Count == realKingdoms.Count)
                break;
        }

        // Leftover kingdoms (more kingdoms than specific regions) → fallback.
        foreach (var k in realKingdoms)
            if (!result.ContainsKey(k))
                result[k] = FALLBACK_ID;

        foreach (var k in realKingdoms)
        {
            var pr = profiles[k];
            GD.Print($"[RegionMatch] {k} -> {result[k]}  " +
                     $"(T{pr.Temp:F2} M{pr.Moisture:F2} E{pr.Elevation:F2} " +
                     $"coast{pr.CoastFrac:F2} sw{pr.SwampFrac:F2} fo{pr.ForestFrac:F2} vo{pr.VolcanicFrac:F2})");
        }

        return result;
    }

    private static Dictionary<string, Profile> MeasureProfiles(WorldData world, List<string> kingdomIds)
    {
        int w = world.Width, h = world.Height;
        var t = new Dictionary<string, double>();
        var m = new Dictionary<string, double>();
        var e = new Dictionary<string, double>();
        var coast = new Dictionary<string, int>();
        var sw = new Dictionary<string, int>();
        var fo = new Dictionary<string, int>();
        var vo = new Dictionary<string, int>();
        var n = new Dictionary<string, int>();
        foreach (var id in kingdomIds)
        { t[id] = 0; m[id] = 0; e[id] = 0; coast[id] = 0; sw[id] = 0; fo[id] = 0; vo[id] = 0; n[id] = 0; }

        float halfH = h * 0.5f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var tile = world.Tiles[i];
                if (!tile.IsLand)
                    continue;
                string id = tile.KingdomId;
                if (string.IsNullOrEmpty(id) || !n.ContainsKey(id))
                    continue;

                float latNorm = Mathf.Abs(y - halfH) / halfH;          // 0 equator .. 1 pole
                float temp = (1f - latNorm) - tile.Elevation * LapseRate;
                temp = Mathf.Clamp((temp + 0.6f) / 1.6f, 0f, 1f);       // → ~[0,1]

                bool isCoast = false;
                foreach (var (nx, ny) in HexCoord.Neighbors(x, y, w, h))
                    if (world.Tiles[ny * w + nx].Terrain == OverworldHex.TerrainType.Water)
                    { isCoast = true; break; }

                t[id] += temp;
                m[id] += tile.Moisture;
                e[id] += tile.Elevation;
                if (isCoast)
                    coast[id]++;
                if (tile.Terrain == OverworldHex.TerrainType.Swamp)
                    sw[id]++;
                if (tile.Terrain == OverworldHex.TerrainType.Forest)
                    fo[id]++;
                if (tile.Terrain == OverworldHex.TerrainType.Volcanic)
                    vo[id]++;
                n[id]++;
            }
        }

        var res = new Dictionary<string, Profile>();
        foreach (var id in kingdomIds)
        {
            int cnt = Mathf.Max(1, n[id]);
            res[id] = new Profile
            {
                Temp = (float)(t[id] / cnt),
                Moisture = (float)(m[id] / cnt),
                Elevation = (float)(e[id] / cnt),
                CoastFrac = (float)coast[id] / cnt,
                SwampFrac = (float)sw[id] / cnt,
                ForestFrac = (float)fo[id] / cnt,
                VolcanicFrac = (float)vo[id] / cnt,
                LandCount = n[id],
            };
        }
        return res;
    }

    private static float Score(Profile p, RegionDefinition reg)
    {
        var aff = reg.Affinity ?? new RegionAffinity();
        float c = 0f;

        c += WTemp * AxisCost(aff.Temperature, TempTarget, p.Temp);
        c += WMoisture * AxisCost(aff.Moisture, MoistTarget, p.Moisture);
        c += WElevation * AxisCost(aff.Elevation, ElevTarget, p.Elevation);

        if (aff.Coastal)
            c += WCoastal * (1f - p.CoastFrac);

        switch (aff.DominantTerrain)
        {
            case "Swamp":
                c += WDominant * (1f - p.SwampFrac);
                break;
            case "Forest":
                c += WDominant * (1f - p.ForestFrac);
                break;
            case "Volcanic":
                c += WDominant * (1f - p.VolcanicFrac);
                break;
        }

        return c;
    }

    private static float AxisCost(string val, Dictionary<string, float> targets, float measured)
    {
        if (string.IsNullOrEmpty(val) || val == "any")
            return 0f;
        if (!targets.TryGetValue(val, out float tgt))
            return 0f;
        return Mathf.Abs(tgt - measured);
    }
}
