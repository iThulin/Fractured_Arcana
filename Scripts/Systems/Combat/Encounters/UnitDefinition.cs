using Godot;
using System.Collections.Generic;

// ============================================================
// UnitDefinition.cs  (U1 · U2)
//
// Purpose:        Data-driven stat block + metadata for one combat
//                 unit, loaded from Data/Units/*.json by UnitRegistry.
//                 One JSON file per unit; the id is the spawn currency.
//
//                 U2: BehaviorKey is now the AI dispatcher (the
//                 EnemyArchetype enum and its EnemyArchetypeData facade
//                 are deleted); BehaviorTags compose movement/targeting/
//                 damage modifiers around the base routine — this is the
//                 deferred Druid-wildlife behaviour-tag dispatcher,
//                 generalized to all factions (units doc §4a).
// Layer:          Data
// Collaborators:  UnitRegistry.cs (loads + caches),
//                 CombatManager.cs (spawn),
//                 CombatManager.EnemyIntents.cs (dispatch + tag hooks).
// See:            build_order_v3 §4 (U2) · archmage_unique_units §3–4a
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

    /// <summary>AI behaviour key — which planning routine drives the unit.
    /// Dispatched by CombatManager.EnemyIntents.PlanIntent (string → handler map).
    /// Current catalog: melee_advance, melee_target_highest_hp, hold_until_near,
    /// ranged_kite, ranged_charge, melee_hunt_wounded (the units doc's 'stalker').
    /// Unknown keys log once and fall back to melee_advance.</summary>
    public string BehaviorKey = "";

    /// <summary>Composable modifiers around the base routine (units doc §4a):
    /// pack, bulwark, charge, scout, immobile. A tag never replaces the routine;
    /// it modifies target choice, movement, or timing. Additive schema change —
    /// U1 JSONs without the field deserialize to an empty list.</summary>
    public List<string> BehaviorTags = new();

    /// <summary>Case-insensitive tag membership test.</summary>
    public bool HasTag(string tag)
    {
        foreach (var t in BehaviorTags)
            if (string.Equals(t, tag, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public float ColorR = 1.0f;
    public float ColorG = 0.25f;
    public float ColorB = 0.25f;

    /// <summary>Body colour reconstructed from the RGB components.</summary>
    public Color BodyColor => new Color(ColorR, ColorG, ColorB);
}
