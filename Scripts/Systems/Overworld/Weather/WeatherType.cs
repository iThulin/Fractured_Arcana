using System.Collections.Generic;

// ============================================================
// WeatherType.cs
//
// Purpose:        The overworld weather catalog for the Mobile Fortress
//                 reframe. Weather is a moving-front field over the
//                 expedition window (see WeatherSystem): each front is
//                 one of these types, and each type carries the per-tile
//                 magnitudes the sortie reads.
//
//                 W1 defines the vocabulary and the numbers; the numbers
//                 are consumed later:
//                   FuelPerTile  -> W2 (added inside OverworldMovementCost.StepCost)
//                   HullPerTile  -> W2 (overworld Hull drain, stacks on terrain)
//                   ScryDelta    -> W2 (reveal/scry radius)
//                   CombatHazard -> W3 (MapEventDef injected when a fight
//                                       starts under this weather)
//                   Particle     -> W4 (3D expedition-view VFX)
//                 Severity orders fronts (the worst covering front wins a
//                 tile) and orders transitions (a front drifts toward an
//                 adjacent severity, not a random jump).
// Layer:          Data (pure catalog, no nodes)
// Collaborators:  WeatherSystem (the field), ExpeditionManager (readout),
//                 OverworldMovementCost (W2 fuel), EncounterRouter (W3).
// Notes:          Starting values are starting values (house discipline);
//                 tune freely. Cinderhold immunity + Storm Anchors act on
//                 HullPerTile in W2.
// ============================================================

public enum WeatherType
{
    Clear,
    Rain,
    Fog,
    Gale,
    Storm,
    Blizzard,
    Ashfall,
}

/// <summary>One weather state's per-tile magnitudes and presentation keys.</summary>
public sealed class WeatherDef
{
    public WeatherType Type;
    public string Name = "";
    /// <summary>0 = Clear (benign); higher = worse. Orders the "worst covering
    /// front wins the tile" resolution and the drift-to-adjacent transitions.</summary>
    public int Severity;
    public int FuelPerTile;      // W2: extra fuel burned entering a tile under this front
    public int HullPerTile;      // W2: Hull lost entering a tile under this front
    public int ScryDelta;        // W2: added to reveal/scry radius (negative shrinks)
    /// <summary>W3: the battlefield weather_tick param this front injects into a
    /// fight ("storm" = lightning, "snow" = ice, "rain" = rising water), or ""
    /// for no combat hazard. Reuses the existing weather_tick map-event kind.</summary>
    public string CombatHazard = "";
    public string Glyph = "";    // HUD readout marker
    public string Particle = ""; // W4: VFX style key
    /// <summary>Whether HullPerTile counts as "weather" Hull drain for the
    /// Cinderhold immunity / Storm Anchors reduction (§4/§6). All non-Clear
    /// drains do; kept explicit so a future non-weather front could opt out.</summary>
    public bool IsWeatherHullDrain = true;
}

/// <summary>The weather table + biome/season roll weights. Static catalog,
/// mirroring how the rest of the overworld config lives in code.</summary>
public static class WeatherCatalog
{
    // ── Tuning: field shape (WeatherSystem reads these) ──────────────────
    public static int   FrontCount        = 3;     // simultaneous fronts in a window
    public static float FrontRadiusTiles   = 5.0f;  // front coverage radius (render-space tiles)
    public static float FrontSpeedTiles    = 0.6f;  // drift per committed stride
    public static float FrontRadiusJitter  = 2.0f;  // ± radius spread per front

    // ── The catalog ──────────────────────────────────────────────────────
    public static readonly Dictionary<WeatherType, WeatherDef> Table = new()
    {
        [WeatherType.Clear] = new WeatherDef
        { Type = WeatherType.Clear, Name = "Clear", Severity = 0,
          FuelPerTile = 0, HullPerTile = 0, ScryDelta = 0,
          CombatHazard = "", Glyph = "☀", Particle = "", IsWeatherHullDrain = false },

        [WeatherType.Rain] = new WeatherDef
        { Type = WeatherType.Rain, Name = "Rain", Severity = 1,
          FuelPerTile = 1, HullPerTile = 0, ScryDelta = 0,
          CombatHazard = "rain", Glyph = "☔", Particle = "rain" },

        [WeatherType.Fog] = new WeatherDef
        { Type = WeatherType.Fog, Name = "Fog", Severity = 1,
          FuelPerTile = 0, HullPerTile = 0, ScryDelta = -2,
          CombatHazard = "", Glyph = "☁", Particle = "fog" },

        [WeatherType.Gale] = new WeatherDef
        { Type = WeatherType.Gale, Name = "Gale", Severity = 2,
          FuelPerTile = 1, HullPerTile = 0, ScryDelta = -1,
          CombatHazard = "", Glyph = "≈", Particle = "gale" },

        [WeatherType.Storm] = new WeatherDef
        { Type = WeatherType.Storm, Name = "Storm", Severity = 3,
          FuelPerTile = 1, HullPerTile = 2, ScryDelta = -1,
          CombatHazard = "storm", Glyph = "⛈", Particle = "storm" },

        [WeatherType.Blizzard] = new WeatherDef
        { Type = WeatherType.Blizzard, Name = "Blizzard", Severity = 3,
          FuelPerTile = 1, HullPerTile = 2, ScryDelta = -2,
          CombatHazard = "snow", Glyph = "❄", Particle = "snow" },

        [WeatherType.Ashfall] = new WeatherDef
        { Type = WeatherType.Ashfall, Name = "Ashfall", Severity = 3,
          FuelPerTile = 0, HullPerTile = 2, ScryDelta = -1,
          CombatHazard = "storm", Glyph = "♨", Particle = "ash" },
    };

    public static WeatherDef Def(WeatherType t)
        => Table.TryGetValue(t, out var d) ? d : Table[WeatherType.Clear];

    public static string Name(WeatherType t) => Def(t).Name;
    public static int Severity(WeatherType t) => Def(t).Severity;

    // ── Biome + season roll ──────────────────────────────────────────────

    /// <summary>Weighted candidate weathers for a front sitting over this
    /// terrain, biased by season. Clear always carries weight so any region
    /// gets fair-weather fronts too. `season` is 0..3 (see WeatherSystem):
    /// 0 spring, 1 summer, 2 autumn, 3 winter — winter pushes Blizzard, summer
    /// pushes Storm/Ashfall. Returns (type, weight) pairs.</summary>
    public static List<(WeatherType type, int weight)> BiomeWeights(
        OverworldHex.TerrainType terrain, int season)
    {
        var w = new List<(WeatherType, int)> { (WeatherType.Clear, 4) };

        switch (terrain)
        {
            case OverworldHex.TerrainType.Tundra:
            case OverworldHex.TerrainType.Snow:
                w.Add((WeatherType.Blizzard, 4));
                w.Add((WeatherType.Fog, 2));
                w.Add((WeatherType.Gale, 2));
                break;
            case OverworldHex.TerrainType.Volcanic:
                w.Add((WeatherType.Ashfall, 5));
                w.Add((WeatherType.Gale, 2));
                break;
            case OverworldHex.TerrainType.Swamp:
            case OverworldHex.TerrainType.Marsh:
                w.Add((WeatherType.Fog, 4));
                w.Add((WeatherType.Rain, 3));
                w.Add((WeatherType.Storm, 2));
                break;
            case OverworldHex.TerrainType.Desert:
                w.Add((WeatherType.Gale, 4));
                w.Add((WeatherType.Ashfall, 1));
                break;
            case OverworldHex.TerrainType.Forest:
                w.Add((WeatherType.Rain, 3));
                w.Add((WeatherType.Fog, 2));
                w.Add((WeatherType.Storm, 2));
                break;
            case OverworldHex.TerrainType.Mountain:
            case OverworldHex.TerrainType.Hills:
                w.Add((WeatherType.Gale, 3));
                w.Add((WeatherType.Storm, 2));
                w.Add((WeatherType.Blizzard, 2));
                break;
            default: // Grassland, Coast, ArcaneGround, Ruins, Road
                w.Add((WeatherType.Rain, 3));
                w.Add((WeatherType.Storm, 2));
                w.Add((WeatherType.Gale, 1));
                break;
        }

        // Season bias (additive to whatever the biome already offered).
        switch (season)
        {
            case 1: // summer: convective violence
                Bump(w, WeatherType.Storm, 2);
                Bump(w, WeatherType.Ashfall, 1);
                break;
            case 3: // winter: cold fronts
                Bump(w, WeatherType.Blizzard, 3);
                Bump(w, WeatherType.Fog, 1);
                break;
            case 0: // spring: wet
                Bump(w, WeatherType.Rain, 2);
                break;
            case 2: // autumn: wind + fog
                Bump(w, WeatherType.Gale, 1);
                Bump(w, WeatherType.Fog, 1);
                break;
        }

        return w;
    }

    private static void Bump(List<(WeatherType type, int weight)> w, WeatherType t, int add)
    {
        for (int i = 0; i < w.Count; i++)
            if (w[i].type == t) { w[i] = (t, w[i].weight + add); return; }
        w.Add((t, add));
    }
}
