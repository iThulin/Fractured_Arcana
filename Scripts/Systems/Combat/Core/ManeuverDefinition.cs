using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

// ============================================================
// ManeuverDefinition.cs
//
// Purpose:        Data for one martial maneuver (Martial Maneuvers
//                 & Edge spec v1 §6, §8). A weapon CLASS grants its
//                 maneuvers (R1); the item stays passive. Loaded from
//                 Data/Maneuvers/*.json by ManeuverRegistry. Never
//                 mutated at runtime.
// Layer:          Data
// Collaborators:  ManeuverRegistry.cs (loading, validation),
//                 CombatManager.Maneuvers.cs (arming, targeting,
//                 resolution), ItemDefinition.cs (WeaponClass),
//                 TechniqueTray.cs (display)
//
// Effect keys are strings resolved in CombatManager.Maneuvers.cs, the
// same shape as the trigger bus's DispatchKey switch. The validator
// rejects a key nothing resolves, so a typo in JSON fails at load,
// not at the moment a player clicks the card.
// ============================================================

/// <summary>Where the maneuver may land. Line means "one of the six axis
/// lines from the user, up to Length tiles"; the click picks the axis.</summary>
public enum ManeuverShape
{
    Self,
    Adjacent,
    Range,
    Line,
}

/// <summary>One effect line on a maneuver. Value and Param are read by the
/// key that needs them; the rest ignore them.</summary>
public class ManeuverEffectDef
{
    /// <summary>damage | push | stagger | root | ignore_armor | ignore_chitin |
    /// finisher_scale. Validated against ManeuverRegistry.KnownEffectKeys.</summary>
    public string Key = "";
    /// <summary>damage: delta from ATK (-1 = "ATK - 1"). push: tiles. root: turns.
    /// finisher_scale: bonus damage per Edge spent. Others: unused.</summary>
    public int Value = 0;
    /// <summary>Optional. push: "collision" damage rides in CollisionDamage instead.</summary>
    public string Param = "";
    /// <summary>push only: authored collision damage dealt to both bodies when
    /// the pushed unit hits something. 0 uses ForcedMove's momentum rule.</summary>
    public int CollisionDamage = 0;
}

public class ManeuverDefinition
{
    // ── Identity ─────────────────────────────────────────────────────────
    public string Id = "";
    public string DisplayName = "";
    /// <summary>Card text. Written to the card text style guide's grammar (D11).</summary>
    public string Text = "";
    /// <summary>Breaker | Anchor | Executioner | Hunter (spec §2). Display only.</summary>
    public string Role = "";

    // ── Access ───────────────────────────────────────────────────────────
    /// <summary>WeaponClass name as JSON writes it ("SwordShield"). Parsed lazily.</summary>
    public string WeaponClass = "None";

    // ── Cost ─────────────────────────────────────────────────────────────
    public int ApCost = 1;
    /// <summary>Edge spent. Ignored when DrainAll is set.</summary>
    public int EdgeCost = 1;
    /// <summary>Finisher: spend every point of Edge, at least MinEdge.</summary>
    public bool DrainAll = false;
    public int MinEdge = 0;

    // ── Shape ────────────────────────────────────────────────────────────
    /// <summary>Self | Adjacent | Range | Line, as JSON writes it.</summary>
    public string Shape = "Adjacent";
    /// <summary>Range shape: maximum distance. Line shape: length.</summary>
    public int Reach = 1;
    /// <summary>Line shape only: whether friendly units on the line are struck.
    /// Piercing Bolt hits "every unit"; Reach Thrust spares allies.</summary>
    public bool HitsAllies = false;
    /// <summary>Heavy Bolt: refused when the unit has moved this turn.</summary>
    public bool RequiresUnmoved = false;

    // ── Effects ──────────────────────────────────────────────────────────
    public List<ManeuverEffectDef> Effects = new();

    // ── Reactions and finishers ──────────────────────────────────────────
    /// <summary>Armed on the player's turn (Self shape, its own AP and Edge),
    /// fires in the enemy phase (spec §7). Reach is the zone radius. Resolved in
    /// CombatManager.Reactions.cs.</summary>
    public bool IsReaction = false;
    /// <summary>enemy_move_end (Brace) | enemy_enter_zone (Set Against Charge).
    /// Validated against ManeuverRegistry.KnownReactionTriggers.</summary>
    public string ReactionTrigger = "";
    public bool IsFinisher = false;

    /// <summary>Per-maneuver VFX tag for the presenter (D12). "" = the placeholder swing.</summary>
    public string VfxTag = "";

    // ── Derived ──────────────────────────────────────────────────────────
    // global:: because the string field above shadows the enum's name inside this class.
    [JsonIgnore] public global::WeaponClass WeaponClassValue =>
        Enum.TryParse<global::WeaponClass>(WeaponClass, ignoreCase: true, out var w) ? w : global::WeaponClass.None;

    [JsonIgnore] public ManeuverShape ShapeValue =>
        Enum.TryParse<ManeuverShape>(Shape, ignoreCase: true, out var s) ? s : ManeuverShape.Adjacent;

    public bool HasEffect(string key)
    {
        foreach (var e in Effects)
            if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public ManeuverEffectDef Effect(string key)
    {
        foreach (var e in Effects)
            if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    /// <summary>"1 AP · 2 Edge", "2 AP · all Edge (min 2)".</summary>
    [JsonIgnore] public string CostLabel =>
        DrainAll ? $"{ApCost} AP · all Edge (min {MinEdge})" : $"{ApCost} AP · {EdgeCost} Edge";
}
