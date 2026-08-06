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

    /// <summary>Elemental strike rider (tile_interaction_spec). Enum-name element
    /// string ("fire" / "frost" / "lightning" / "earth" / "arcane"); the unit's
    /// landed attacks imbue the struck tile with it. Empty (default) = no rider.
    /// Parsed via MapRecipe.ParseElement, so card aliases (storm/ice/stone) do NOT
    /// work here — use the enum names.</summary>
    public string ImbueOnHit = "";

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

    /// <summary>U3a — ordered intent script. Each entry names a PLANNER from the
    /// same catalog BehaviorKey draws on, so a script is written in the vocabulary
    /// the AI already speaks. EMPTY (every pre-U3a JSON) = the unit plans from
    /// BehaviorKey on every activation, exactly as before. Additive schema change.
    ///
    /// This is the identity axis the roster was missing: six behaviour keys vary
    /// only WHO a unit walks at, which is invisible at decision time and answered
    /// the same way every time. A unit whose beat 3 differs from its beat 1 is a
    /// memory. See enemy_identity_spec_v1 §4.</summary>
    public List<string> IntentCycle = new();

    /// <summary>True (default) = the cycle repeats forever — rotations.
    /// False = it runs ONCE and then falls through to BehaviorKey — openings
    /// (a wind-up, an entrance, a one-time transformation).</summary>
    public bool CycleLoops = true;

    /// <summary>Triggered abilities (units doc §5, U3). Hard cap two per unit —
    /// the item system's two-effect legibility ceiling applied to enemies.
    /// Additive schema change: JSONs without the field deserialize empty.</summary>
    public List<UnitAbilityDef> Abilities = new();

    /// <summary>V2 (units doc §3): "line", "elite", "boss", or "summon". Drives
    /// roster role markers, nameplate policy, and (later) reward-roll bonuses.
    /// Missing = "line" — every pre-V2 JSON is a line unit.</summary>
    public string Role = "line";

    /// <summary>Owning archmage id ("conductor") or "" for generics/debug.
    /// Drives faction tinting in the roster and the §11 tint legend.</summary>
    public string FactionId = "";

    /// <summary>One sentence for the scout report and the inspect panel (§3).</summary>
    public string IntelDescription = "";

    /// <summary>Signature spell key for channel->release casters (behaviorKey
    /// ranged_charge). "" = default blast (damage + slowed rider). Values —
    /// ember/chrono/grave/thorn/mind/arclance/geas/forge — swap the release rider
    /// per wizard school in CombatManager.EnemyIntents.ApplyCasterRider. Additive:
    /// JSONs without the field deserialize to "".</summary>
    public string CasterSpell = "";

    public bool IsElite => string.Equals(Role, "elite", System.StringComparison.OrdinalIgnoreCase);
    public bool IsBoss  => string.Equals(Role, "boss",  System.StringComparison.OrdinalIgnoreCase);

    public float ColorR = 1.0f;
    public float ColorG = 0.25f;
    public float ColorB = 0.25f;

    /// <summary>Body colour reconstructed from the RGB components.</summary>
    public Color BodyColor => new Color(ColorR, ColorG, ColorB);
}

/// <summary>One triggered ability on a UnitDefinition (units doc §5): a data KEY
/// dispatched by the handler map in CombatManager.Triggers, a TRIGGER from the
/// bounded taxonomy (onSpawn/onDeath/onAllyDeath/onAttack/onStruck/onTurnEnd/
/// everyNRounds — auras are states, not events, and do not use this), a display
/// name for stack/log lines, and free-form string params (parsed by the handler).</summary>
public class UnitAbilityDef
{
    public string Key = "";
    public string Trigger = "";
    public string Name = "";
    public string IntelDescription = "";
    public Dictionary<string, string> Params = new();

    public int GetIntParam(string key, int fallback)
        => Params.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    public string GetStringParam(string key, string fallback)
        => Params.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
}
