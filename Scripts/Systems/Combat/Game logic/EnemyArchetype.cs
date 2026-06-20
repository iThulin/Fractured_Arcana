using Godot;

// ============================================================
// EnemyArchetype.cs
//
// Purpose:        Enemy archetype enum + paired stateless data
//                 helpers (max HP, speed, armor, attack damage,
//                 body colour, AI behaviour key). Everything else
//                 in the combat system keys off this enum.
// Layer:          Data
// Collaborators:  CombatManager.cs (spawns from this),
//                 EncounterPoolLoader.cs (parses archetype names),
//                 Unit.cs (consumes stat block at spawn),
//                 RulesManager.cs (drives AI by archetype)
// See:            README §3 — combat dispatch
// ============================================================

/// <summary>The five enemy archetypes. Each drives distinct stats and AI behaviour. Extending this enum requires adding stat-block entries in <see cref="EnemyArchetypeData"/>.</summary>
public enum EnemyArchetype
{
    Soldier,   // Baseline melee. Move toward nearest, attack adjacent.
    Brute,     // Slow, high-HP melee. Targets the highest-HP player unit.
    Defender,  // Armoured. Holds position until a player unit is within 2 tiles.
    Ranger,    // Ranged attacker. Maintains distance 2–3, attacks without closing.
    Wizard,    // Ranged. Charges every other turn, then deals high damage + applies debuff.
}

/// <summary>
/// Stateless stat block and metadata for each archetype.
/// CombatManager reads this to populate PendingEnemySpawn and to drive ActEnemyUnit.
/// </summary>
public static class EnemyArchetypeData
{
    public static int GetMaxHealth(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Soldier => 20,
        EnemyArchetype.Brute => 40,
        EnemyArchetype.Defender => 25,
        EnemyArchetype.Ranger => 15,
        EnemyArchetype.Wizard => 12,
        _ => 20,
    };

    public static int GetBaseSpeed(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Soldier => 2,
        EnemyArchetype.Brute => 1,
        EnemyArchetype.Defender => 1,
        EnemyArchetype.Ranger => 2,
        EnemyArchetype.Wizard => 1,
        _ => 2,
    };

    public static int GetArmor(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Defender => 5,
        _ => 0,
    };

    /// <summary>Attack range in tiles. 1 = melee only.</summary>
    public static int GetAttackRange(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Ranger => 3,
        EnemyArchetype.Wizard => 5,
        _ => 1,
    };

    /// <summary>Base damage per attack. Wizard damage is for its charged shot.</summary>
    public static int GetAttackDamage(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Soldier => 5,
        EnemyArchetype.Brute => 8,
        EnemyArchetype.Defender => 4,
        EnemyArchetype.Ranger => 4,
        EnemyArchetype.Wizard => 9,   // charged shot
        _ => 5,
    };

    /// <summary>
    /// Preferred engagement distance. Ranger/Wizard try to stay AT this distance.
    /// Melee archetypes return 1 (they want to be adjacent).
    /// </summary>
    public static int GetPreferredDistance(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Ranger => 3,
        EnemyArchetype.Wizard => 4,
        _ => 1,
    };

    public static string GetThreatLabel(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Soldier => "Soldier",
        EnemyArchetype.Brute => "Brute",
        EnemyArchetype.Defender => "Defender",
        EnemyArchetype.Ranger => "Ranger",
        EnemyArchetype.Wizard => "Wizard",
        _ => "Unknown",
    };

    public static Color GetBodyColor(EnemyArchetype a) => a switch
    {
        EnemyArchetype.Soldier => new Color(1.0f, 0.25f, 0.25f),  // red
        EnemyArchetype.Brute => new Color(0.8f, 0.2f, 0.9f),   // purple
        EnemyArchetype.Defender => new Color(0.2f, 0.5f, 0.9f),   // blue
        EnemyArchetype.Ranger => new Color(0.2f, 0.8f, 0.3f),   // green
        EnemyArchetype.Wizard => new Color(0.9f, 0.9f, 0.1f),   // yellow
        _ => new Color(1.0f, 0.25f, 0.25f),
    };
}
