using System.Collections.Generic;

// ============================================================
// EncounterDefinition.cs
//
// Purpose:        Per-combat data model — encounter tier, enemy
//                 composition list, source terrain/region tags.
//                 Created by EncounterRouter from region pool
//                 data before scene swap; read by CombatManager
//                 at spawn time.
// Layer:          Data
// Collaborators:  EncounterRouter.cs (creator),
//                 EncounterPoolLoader.cs (data source),
//                 CombatManager.cs (consumer),
//                 EnemyArchetype.cs (composition refs)
// See:            README §3 — combat dispatch pipeline
// ============================================================

/// <summary>Difficulty tier of a combat encounter. Drives spawn count, enemy archetype mix, and reward scaling. Maps to overworld POI sub-types.</summary>
public enum EncounterTier
{
    Skirmish,  // 2 enemies, easiest
    Battle,    // 3 enemies, standard
    Siege,     // 4–5 enemies, hard
    Ambush,    // 3 enemies, surprise (future: grants enemies a free action)
    Boss,      // 1–3 enemies, very hard (future)
}

/// <summary>
/// A single enemy slot in an encounter composition.
/// </summary>
public struct EnemySlot
{
    public EnemyArchetype Archetype;

    /// <summary>
    /// Optional stat multiplier from the region's enemyDifficultyMult.
    /// 1.0 = base stats. Applied at spawn time.
    /// </summary>
    public float DifficultyMult;

    public EnemySlot(EnemyArchetype archetype, float difficultyMult = 1.0f)
    {
        Archetype = archetype;
        DifficultyMult = difficultyMult;
    }
}

/// <summary>
/// Full definition of a combat encounter passed from EncounterRouter
/// to CombatManager via EncounterContext.
/// </summary>
public class EncounterDefinition
{
    public string Id = "";     // e.g. "frontier_wilds_skirmish_forest"
    public string DisplayName = "";
    public EncounterTier Tier = EncounterTier.Battle;
    public List<EnemySlot> Enemies = new();

    // Overworld context — used by CombatManager for map theme selection later
    public string RegionId = "";
    public string TerrainType = "";   // OverworldHex.TerrainType name
    public float DifficultyMult = 1.0f;
}
