using System.Collections.Generic;

// ============================================================
// StanceDefinition.cs
//
// Purpose:        Martial-companion stance system. Stances are
//                 the martial alternative to cards. Each stance
//                 persists between turns until manually switched
//                 (once per turn). Passives apply at turn start;
//                 attack modifiers apply when the unit attacks.
// Layer:          Data
// Collaborators:  CompanionDefinition.cs (trained stances list),
//                 Unit.cs (ActiveStance), RulesManager.cs (drives
//                 stance effects), CampusScreen.cs (Training tab)
// See:            README §6, Martial system
// ============================================================

/// <summary>Martial class identifying which stance bucket a stance belongs to. Wizards do not use stances; "None" is the unclassed levy default.</summary>
public enum MartialClass
{
    None,       // unclassed levy, no stances
    Fighter,    // melee, high HP/armor
    Ranger,     // ranged, high speed/mobility
}

/// <summary>
/// Special behaviours that require custom code in the attack resolver.
/// Most stances use flat stat modifiers; these need extra handling.
/// </summary>
public enum StanceSpecialTag
{
    None,
    AoeAdjacent,        // Reckless: hits all adjacent enemies
    LinePiercing,       // Volley: hits all enemies in a line
    BerserkScaling,     // damage scales with missing HP
    SkirmishDash,       // Skirmish: free move after attack
    AmbushFirstStrike,  // double damage on first attack of combat
    MarkedTarget,       // apply Marked status; next ally hit deals bonus damage
    Taunt,              // redirect enemy AI to this unit next turn
    GuardianAura,       // adjacent allies gain armor while stance is active
    AimedRequiresNoMove,// Aimed: bonus only applies if unit hasn't moved
}

/// <summary>
/// Definition of a single stance. Loaded from StanceRegistry at startup.
/// Never mutated at runtime.
/// </summary>
public class StanceDefinition
{
    // ── Identity ─────────────────────────────────────────────────────────
    public string Id = "";
    public string DisplayName = "";
    public string Description = "";
    public MartialClass Class = MartialClass.Fighter;

    // ── Passive modifiers (applied at turn start while stance is active) ─
    public int PassiveArmorBonus = 0;   // + to unit's effective armor
    public int PassiveSpeedBonus = 0;   // + to move points this turn
    public int PassiveDamageBonus = 0;   // + to all attacks this turn
    public int PassiveArmorPenalty = 0;  // - to armor (Reckless, Aggressive)

    // ── Attack modifiers ─────────────────────────────────────────────────
    public int AttackDamageBonus = 0;   // extra damage on this attack
    public int AttackRangeBonus = 0;   // extra range on this attack
    public bool AttackIgnoresArmor = false; // Aimed: pierce armor entirely
    public int AttackPushTiles = 0;   // push target N tiles on hit

    // ── On-hit effects ────────────────────────────────────────────────────
    public string OnHitStatusName = null;  // status to apply to target
    public int OnHitStatusDuration = 1;
    public int OnHitSelfShieldGain = 0;     // Defensive: gain shield on hit
    public int OnHitSelfDamage = 0;     // Reckless: take damage after hit

    // ── Special behaviour ─────────────────────────────────────────────────
    public StanceSpecialTag SpecialTag = StanceSpecialTag.None;
    public int SpecialTagValue = 0;  // magnitude for scaling specials

    // ── Signature (K4, v2.1 §2 carried from v1) ──────────────────────────
    /// <summary>An ArcStage-4 signature stance: never trained, never listed by
    /// the campus Training tab, granted at spawn by StanceRegistry.
    /// EligibleSignature when the arc is complete and loyalty is above Wary.</summary>
    public bool IsSignature = false;
}

/// <summary>
/// Static registry of all stances. Both Fighter and Ranger stances live here.
/// Companions reference stances by Id in their JSON.
/// </summary>
public static class StanceRegistry
{
    private static Dictionary<string, StanceDefinition> _stances;

    public static Dictionary<string, StanceDefinition> All
    {
        get
        {
            if (_stances == null) Build();
            return _stances;
        }
    }

    public static StanceDefinition Get(string id)
    {
        if (All.TryGetValue(id, out var s)) return s;
        Godot.GD.PrintErr($"StanceRegistry: Unknown stance '{id}'");
        return null;
    }

    private static void Build()
    {
        _stances = new Dictionary<string, StanceDefinition>();

        // ── Fighter stances ───────────────────────────────────────────────

        Add(new StanceDefinition
        {
            Id = "aggressive",
            DisplayName = "Aggressive",
            Description = "+2 attack damage. On hit: push target 1 tile. -1 armor.",
            Class = MartialClass.Fighter,
            PassiveArmorPenalty = 1,
            AttackDamageBonus = 2,
            AttackPushTiles = 1,
        });

        Add(new StanceDefinition
        {
            Id = "defensive",
            DisplayName = "Defensive",
            Description = "+4 armor. On hit: gain 2 shield. -1 attack damage.",
            Class = MartialClass.Fighter,
            PassiveArmorBonus = 4,
            AttackDamageBonus = -1,
            OnHitSelfShieldGain = 2,
        });

        Add(new StanceDefinition
        {
            Id = "reckless",
            DisplayName = "Reckless",
            Description = "Attack hits all adjacent enemies. Take 2 damage after attacking. -2 armor.",
            Class = MartialClass.Fighter,
            PassiveArmorPenalty = 2,
            OnHitSelfDamage = 2,
            SpecialTag = StanceSpecialTag.AoeAdjacent,
        });

        Add(new StanceDefinition
        {
            Id = "duelist",
            DisplayName = "Duelist",
            Description = "+1 attack range. On hit: apply Vulnerable for 1 turn (target takes +2 damage).",
            Class = MartialClass.Fighter,
            AttackRangeBonus = 1,
            OnHitStatusName = "vulnerable",
            OnHitStatusDuration = 1,
        });

        Add(new StanceDefinition
        {
            Id = "berserk",
            DisplayName = "Berserk",
            Description = "Gain +1 damage per 5 HP missing (max +5).",
            Class = MartialClass.Fighter,
            SpecialTag = StanceSpecialTag.BerserkScaling,
            SpecialTagValue = 5, // cap
        });

        Add(new StanceDefinition
        {
            Id = "guardian",
            DisplayName = "Guardian",
            Description = "Adjacent allies gain +2 armor. Attack taunts: enemies target you next turn.",
            Class = MartialClass.Fighter,
            SpecialTag = StanceSpecialTag.GuardianAura,
            SpecialTagValue = 2, // armor granted to allies
            OnHitStatusName = "taunted",
            OnHitStatusDuration = 1,
        });

        // ── Ranger stances ────────────────────────────────────────────────

        Add(new StanceDefinition
        {
            Id = "aimed",
            DisplayName = "Aimed",
            Description = "+3 damage if you haven't moved this turn. Ignores armor.",
            Class = MartialClass.Ranger,
            AttackDamageBonus = 3,
            AttackIgnoresArmor = true,
            SpecialTag = StanceSpecialTag.AimedRequiresNoMove,
        });

        Add(new StanceDefinition
        {
            Id = "suppression",
            DisplayName = "Suppression",
            Description = "On hit: target loses 1 move point next turn.",
            Class = MartialClass.Ranger,
            OnHitStatusName = "suppressed",
            OnHitStatusDuration = 1,
        });

        Add(new StanceDefinition
        {
            Id = "volley",
            DisplayName = "Volley",
            Description = "Attack hits all enemies in a line. -1 damage per target after the first.",
            Class = MartialClass.Ranger,
            SpecialTag = StanceSpecialTag.LinePiercing,
        });

        Add(new StanceDefinition
        {
            Id = "skirmish",
            DisplayName = "Skirmish",
            Description = "+1 speed. After attacking, move up to 2 tiles for free.",
            Class = MartialClass.Ranger,
            PassiveSpeedBonus = 1,
            SpecialTag = StanceSpecialTag.SkirmishDash,
            SpecialTagValue = 2, // free move tiles after attack
        });

        Add(new StanceDefinition
        {
            Id = "ambush",
            DisplayName = "Ambush",
            Description = "First attack this combat deals double damage.",
            Class = MartialClass.Ranger,
            SpecialTag = StanceSpecialTag.AmbushFirstStrike,
        });

        Add(new StanceDefinition
        {
            Id = "marked",
            DisplayName = "Marked",
            Description = "On hit: apply Marked. Next ally attack on that target deals +3 damage.",
            Class = MartialClass.Ranger,
            OnHitStatusName = "marked",
            OnHitStatusDuration = 2,
            SpecialTag = StanceSpecialTag.MarkedTarget,
            SpecialTagValue = 3, // bonus damage for next ally hit
        });

        // ── Signature stances (K4) ────────────────────────────────────────
        // One per Class × Trait cell: elevated versions of the base kit with
        // a personality-shaped identity. FRESH-AUTHORED K4 STARTING VALUES
        // (the v1 signature matrix could not be located). Granted at spawn
        // via EligibleSignature, never trained, never in TrainedStanceIds.
        // Authored companions may override via Companion.SignatureStanceId.

        Add(new StanceDefinition
        {
            Id = "sig_fighter_cunning",
            DisplayName = "Feintwork ✦",
            Description = "Signature. +1 damage. On hit: apply Vulnerable for 1 turn.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            AttackDamageBonus = 1,
            OnHitStatusName = "vulnerable",
            OnHitStatusDuration = 1,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_loyal",
            DisplayName = "Oathwall ✦",
            Description = "Signature. +2 armor. Adjacent allies gain +3 armor.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            PassiveArmorBonus = 2,
            SpecialTag = StanceSpecialTag.GuardianAura,
            SpecialTagValue = 3,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_curious",
            DisplayName = "Openings ✦",
            Description = "Signature. +1 damage. Attacks ignore armor.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            AttackDamageBonus = 1,
            AttackIgnoresArmor = true,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_stoic",
            DisplayName = "Immovable ✦",
            Description = "Signature. +5 armor. On hit: gain 2 shield.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            PassiveArmorBonus = 5,
            OnHitSelfShieldGain = 2,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_reckless",
            DisplayName = "Avalanche ✦",
            Description = "Signature. Attack hits all adjacent enemies at +2 damage. " +
                          "Take 2 damage after attacking. -1 armor.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            AttackDamageBonus = 2,
            PassiveArmorPenalty = 1,
            OnHitSelfDamage = 2,
            SpecialTag = StanceSpecialTag.AoeAdjacent,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_cunning",
            DisplayName = "Killing Angle ✦",
            Description = "Signature. +1 damage. On hit: apply Marked. The next ally attack " +
                          "on that target deals +4 damage.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackDamageBonus = 1,
            OnHitStatusName = "marked",
            OnHitStatusDuration = 2,
            SpecialTag = StanceSpecialTag.MarkedTarget,
            SpecialTagValue = 4,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_loyal",
            DisplayName = "Warding Volley ✦",
            Description = "Signature. Attack hits all enemies in a line. On hit: target " +
                          "loses 1 move point next turn.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            OnHitStatusName = "suppressed",
            OnHitStatusDuration = 1,
            SpecialTag = StanceSpecialTag.LinePiercing,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_curious",
            DisplayName = "Read the Wind ✦",
            Description = "Signature. +4 damage if you haven't moved this turn. Ignores armor.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackDamageBonus = 4,
            AttackIgnoresArmor = true,
            SpecialTag = StanceSpecialTag.AimedRequiresNoMove,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_stoic",
            DisplayName = "Patient Shot ✦",
            Description = "Signature. +1 range. First attack this combat deals double damage.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackRangeBonus = 1,
            SpecialTag = StanceSpecialTag.AmbushFirstStrike,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_reckless",
            DisplayName = "Storm of Shafts ✦",
            Description = "Signature. +1 damage, +1 speed. After attacking, move up to " +
                          "3 tiles for free.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackDamageBonus = 1,
            PassiveSpeedBonus = 1,
            SpecialTag = StanceSpecialTag.SkirmishDash,
            SpecialTagValue = 3,
        });
    }

    private static void Add(StanceDefinition s) => _stances[s.Id] = s;

    // ═════════════════════════════════════════════════════════════════════
    // K4: signature grant (derived, single-source)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>The Class × Trait matrix id for a martial companion, or the
    /// authored override when set. Null for wizards/levies (arcane signatures
    /// are DEFERRED pending card schema: v1 lock, carried in v2.1 §2).</summary>
    public static string SignatureIdFor(Companion c)
    {
        if (c == null) return null;
        if (!string.IsNullOrEmpty(c.SignatureStanceId)) return c.SignatureStanceId;
        string cls = c.UnitClass switch
        {
            "Fighter" => "fighter",
            "Ranger" => "ranger",
            _ => null,
        };
        if (cls == null || string.IsNullOrEmpty(c.PersonalityTrait)) return null;
        return $"sig_{cls}_{c.PersonalityTrait.ToLower()}";
    }

    /// <summary>The signature stance this companion fields RIGHT NOW, or null.
    /// Rules (v1, locked): arc complete (ArcStage 4) and not Wary, since a signature
    /// is personal, and the Wary don't give you their best. Destroyed on
    /// permadeath by construction: this is derived state, and the dead never
    /// spawn. Never stored in TrainedStanceIds.</summary>
    public static StanceDefinition EligibleSignature(Companion c)
    {
        if (c == null || c.IsPermadead) return null;
        if (c.ArcStage < 4) return null;
        if (c.GetLoyaltyTier() == LoyaltyTier.Wary) return null;
        string id = SignatureIdFor(c);
        if (id == null) return null;
        var s = Get(id);
        if (s == null || !s.IsSignature) return null;
        return s;
    }
}
