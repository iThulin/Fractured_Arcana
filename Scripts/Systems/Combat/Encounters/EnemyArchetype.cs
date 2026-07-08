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

/// <summary>Thin facade over <see cref="UnitRegistry"/>: the stats now live in
/// Data/Units/generic_*.json (loaded by the registry), not in this file. Kept for
/// parity during U1 so every existing call site — CombatManager spawn paths, Unit,
/// RulesManager, intel labels — is unchanged while the data moves to JSON. U2
/// deletes the EnemyArchetype enum and reads UnitDefinition directly.</summary>
public static class EnemyArchetypeData
{
    public static int GetMaxHealth(EnemyArchetype a) => UnitRegistry.ForArchetype(a).MaxHealth;
    public static int GetBaseSpeed(EnemyArchetype a) => UnitRegistry.ForArchetype(a).BaseSpeed;
    public static int GetArmor(EnemyArchetype a) => UnitRegistry.ForArchetype(a).Armor;
    public static int GetAttackRange(EnemyArchetype a) => UnitRegistry.ForArchetype(a).AttackRange;
    public static int GetAttackDamage(EnemyArchetype a) => UnitRegistry.ForArchetype(a).AttackDamage;
    public static int GetPreferredDistance(EnemyArchetype a) => UnitRegistry.ForArchetype(a).PreferredDistance;
    public static string GetThreatLabel(EnemyArchetype a) => UnitRegistry.ForArchetype(a).ThreatLabel;
    public static Color GetBodyColor(EnemyArchetype a) => UnitRegistry.ForArchetype(a).BodyColor;
}
