using Godot;

// ============================================================
// UnitDefinition.cs  (U1)
//
// Purpose:        Data-driven stat block + metadata for one combat
//                 unit, loaded from Data/Units/*.json by UnitRegistry.
//                 Replaces the hardcoded switches in EnemyArchetypeData
//                 (now a thin facade over the registry). One JSON file
//                 per unit; the id is the spawn currency going forward.
//
//                 BehaviorKey is carried but NOT dispatched on in U1 —
//                 AI still runs through the EnemyArchetype enum via the
//                 facade. U2 makes BehaviorKey a first-class dispatcher
//                 and removes the enum.
// Layer:          Data
// Collaborators:  UnitRegistry.cs (loads + caches), EnemyArchetype.cs
//                 (facade reads these), CombatManager.cs (spawn).
// See:            build_order_v3 §4 (U1)
// ============================================================

/// <summary>One unit's stateless stat block. Plain public fields so
/// System.Text.Json (CamelCase, IncludeFields) carries it cleanly. Body
/// colour is stored as RGB components because Godot.Color is not JSON-native;
/// <see cref="BodyColor"/> reconstructs it.</summary>
public class UnitDefinition
{
    public string Id = "";
    public string ThreatLabel = "";

    public int MaxHealth = 20;
    public int BaseSpeed = 2;
    public int Armor = 0;
    public int AttackRange = 1;
    public int AttackDamage = 5;
    public int PreferredDistance = 1;

    /// <summary>AI behaviour key. In U1 this is descriptive only (the enum still
    /// drives RulesManager); U2 dispatches on it and deletes the enum.</summary>
    public string BehaviorKey = "";

    public float ColorR = 1.0f;
    public float ColorG = 0.25f;
    public float ColorB = 0.25f;

    /// <summary>Body colour reconstructed from the RGB components.</summary>
    public Color BodyColor => new Color(ColorR, ColorG, ColorB);
}
