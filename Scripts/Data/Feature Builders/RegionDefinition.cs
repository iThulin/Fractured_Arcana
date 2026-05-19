using System.Collections.Generic;

// ============================================================
// RegionDefinition.cs
//
// Purpose:        Overworld region data — grid dimensions, POI
//                 counts, biome zones, feature toggles (rivers,
//                 mountains, roads), difficulty multipliers.
//                 The region defines STRUCTURE; the run seed
//                 drives randomisation within that structure.
// Layer:          Data
// Collaborators:  RegionLoader.cs (JSON parser),
//                 OverworldHexGrid.cs (consumes grid + biomes),
//                 POIGenerator.cs (consumes POI counts),
//                 OverworldRunManager.cs (consumes difficulty)
// See:            README §4.2 (Adding a Region)
// ============================================================

/// <summary>All parameters defining one overworld region: grid size, biome layout, POI distribution, feature toggles, and difficulty multipliers. Defines structure only; run randomisation comes from the seed at run start.</summary>
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