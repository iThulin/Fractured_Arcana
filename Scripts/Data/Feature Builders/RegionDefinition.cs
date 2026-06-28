using System.Collections.Generic;

// ============================================================
// RegionDefinition.cs
//
// Purpose:        Overworld region data — grid dimensions, POI
//                 counts, a field-based base-terrain palette
//                 (elevation/moisture rules), feature toggles
//                 (rivers, mountains, roads), difficulty tier,
//                 and gold/reward multipliers. The region defines
//                 STRUCTURE; the run seed drives randomisation.
// Layer:          Data
// Collaborators:  RegionLoader.cs (JSON parser),
//                 OverworldHexGrid.cs (consumes grid + palette),
//                 OverworldField.cs (consumes base terrain),
//                 POIGenerator.cs (consumes POI counts),
//                 OverworldRunManager.cs (consumes difficulty)
// See:            README §4.2 (Adding a Region)
// ============================================================

/// <summary>All parameters defining one overworld region.</summary>
public class RegionDefinition
{
    // ── Identity ────────────────────────────────────────────────────────
    public string Id = "";
    public string DisplayName = "";
    public string Description = "";
    public string SchoolAffinity = "";      // Elementalist, Necromancer, etc.
    public string Atmosphere = "";          // Hostile, Neutral, Friendly

    // ── Difficulty tier ──────────────────────────────────────────────────
    /// <summary>
    /// Base difficulty tier (1 = early, 2 = mid, 3 = late). Used by
    /// OverworldRunManager to scale EnemyDifficultyMult at runtime based on
    /// how many regions the player has already completed. The JSON value is the
    /// floor; each completed region adds a small multiplier on top.
    /// </summary>
    public int BaseDifficultyTier = 1;

    /// <summary>
    /// Display string shown in region-select UI (e.g. "The Borderlands",
    /// "The Interior", "The Reaches"). Authored per region; not derived
    /// from BaseDifficultyTier so names can be flavourful.
    /// </summary>
    public string DisplayTier = "The Borderlands";

    // ── Grid dimensions ─────────────────────────────────────────────────
    public int GridWidth = 15;
    public int GridHeight = 15;
    public int StepBudget = 22;

    // ── POI distribution ────────────────────────────────────────────────
    public int CombatPOICount = 10;
    public int RestPOICount = 4;
    public int OutpostPOICount = 0;
    public int NarrativePOICount = 3;
    public int NegotiationPOICount = 0;

    // ── Feature toggles ──────────────────────────────────────────────────
    public bool HasRiver = true;
    public bool HasMountainRange = false;
    public bool HasRoads = true;
    public int RiverCrossingCount = 2;

    // ── Base terrain palette ─────────────────────────────────────────────
    public OverworldBaseTerrain BaseTerrain = new();
    public RegionAffinity Affinity = new();

    // ── Difficulty multipliers ────────────────────────────────────────────
    public float EnemyDifficultyMult = 1.0f;
    public float GoldRewardMult = 1.0f;

    /// <summary>Multiplier on how many cities/towns this region grows when it owns a
    /// territory. 1.0 = baseline; >1 = denser/more urban (the Tinker city-states),
    /// <1 = emptier wilds. Read by Settlements.Generate. Default 1.0 leaves every
    /// existing region unchanged.</summary>
    public float SettlementDensity = 1.0f;
}

/// <summary>Field tuning + terrain palette for a region.</summary>
public class OverworldBaseTerrain
{
    public float ElevationFrequency = 0f;
    public float MoistureFrequency = 0f;
    public float DetailWeight = -1f;
    public List<OverworldPaletteRule> Palette = new();
    public bool HasPalette => Palette != null && Palette.Count > 0;
}

/// <summary>One elevation/moisture classification rule. First matching rule wins.</summary>
public class OverworldPaletteRule
{
    public string TerrainName = "Grassland";
    public float? MaxElevation;
    public float? MinElevation;
    public float? MaxMoisture;
    public float? MinMoisture;

    public OverworldHex.TerrainType Terrain =>
        System.Enum.TryParse<OverworldHex.TerrainType>(TerrainName, ignoreCase: true, out var t)
            ? t : OverworldHex.TerrainType.Grassland;
}

/// <summary>How a region wants to be placed on the world map. RegionMatcher scores
/// each territory's measured natural profile against these. Every axis defaults to a
/// no-constraint value so an unauthored region matches anywhere (and ends up as
/// leftover/fallback rather than forced onto a poor fit).</summary>
public class RegionAffinity
{
    public string Temperature = "any";      // cold | temperate | warm | any
    public string Moisture = "any";         // arid | dry | temperate | wet | any
    public string Elevation = "any";        // low | mid | high | any
    public bool Coastal = false;            // true → wants ocean coastline
    public string DominantTerrain = "None"; // Swamp | Forest | Volcanic | None
}
