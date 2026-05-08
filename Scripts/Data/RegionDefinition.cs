using System.Collections.Generic;

/// <summary>
/// Defines all parameters for an overworld region.
/// Loaded from Data/Regions/*.json and consumed by OverworldHexGrid + POIGenerator + RunManager.
/// 
/// Region defines the STRUCTURE (which biomes, which features, how many POIs).
/// The run seed determines the RANDOMIZATION within that structure.
/// </summary>
public class RegionDefinition
{
    // ── Identity ────────────────────────────────────────────────────────
    public string Id = "";
    public string DisplayName = "";
    public string Description = "";
    public string SchoolAffinity = "";      // Elementalist, Necromancer, etc.
    public string Atmosphere = "";          // Hostile, Neutral, Friendly

    // ── Grid dimensions ─────────────────────────────────────────────────
    public int GridWidth = 15;
    public int GridHeight = 15;
    public int StepBudget = 22;

    // ── POI distribution ────────────────────────────────────────────────
    public int CombatPOICount = 10;
    public int RestPOICount = 4;
    public int NarrativePOICount = 3;
    public int NegotiationPOICount = 0;     // Phase 2.5

    // ── Feature toggles ──────────────────────────────────────────────────
    public bool HasRiver = true;
    public bool HasMountainRange = false;
    public bool HasRoads = true;
    public int RiverCrossingCount = 2;

    // ── Biome list ───────────────────────────────────────────────────────
    // Empty list = use the default (hardcoded) biome layout.
    // Non-empty = replaces the default.
    public List<BiomeDefinition> Biomes = new();

    // ── Difficulty modifiers ─────────────────────────────────────────────
    public float EnemyDifficultyMult = 1.0f;
    public float GoldRewardMult = 1.0f;
}

/// <summary>
/// A zone of concentrated terrain within a region.
/// Painted outward from center with falloff to secondary terrain.
/// </summary>
public class BiomeDefinition
{
    public string Name = "";
    public int CenterQ = 0;
    public int CenterR = 0;
    public int Radius = 3;
    public string PrimaryTerrain = "Grassland";
    public string SecondaryTerrain = "Grassland";
}