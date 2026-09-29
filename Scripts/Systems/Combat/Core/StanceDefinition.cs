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
/// The one event that earns Edge for a stance (Martial Maneuvers & Edge spec v1 §5).
/// Resolved in EdgeRules.cs.
/// </summary>
public enum EdgeTrigger
{
    None,
    FirstHitEachTurn,      // Aggressive: first hit each turn
    WhenStruck,            // Defensive: struck by an enemy (once per enemy activation)
    TurnStart,             // Reckless: every turn start
    TurnStartBelowHalfHp,  // Berserk: turn start while below 50% HP
    AdjacentAllyAttacked,  // Guardian: an adjacent ally is attacked (once per enemy activation)
    TurnEndNoMove,         // Aimed: end turn without having moved
    Momentum,              // Skirmish: action differs from the previous one (max 2 per turn)
    PerExtraTargetHit,     // Volley: per extra enemy hit by one action
    HitUndamagedTarget,    // Ambush: hit an enemy at full HP
    HitStatusedTarget,     // Duelist, Suppression: hit a target carrying EdgeTriggerStatus
    AllyHitsMarkedTarget,  // Marked: any other ally hits a Marked target
    TurnEndCounteredIntents, // Vigilant (M5): +1 per visible enemy intent this unit counters at turn end, max 2
    Openings,              // Opportunist (M5): hits place Opening tokens on the target; maneuvers against it spend them as Edge
}

/// <summary>
/// The stance's rider on maneuvers (Martial Maneuvers & Edge spec v1 §5, "Rider
/// on maneuvers" column). Resolved in CombatManager.Maneuvers.cs. One per stance.
/// </summary>
public enum StanceManeuverRider
{
    None,
    PushPlusOne,            // Aggressive: maneuver pushes go one tile further
    GainShield,             // Defensive: gain 1 shield after a maneuver
    EdgeDebt,               // Reckless: may spend Edge it lacks for 2 self-damage per point (not built)
    DamageVsVulnerable,     // Duelist: +1 maneuver damage against Vulnerable targets
    DamageBelowHalf,        // Berserk: +1 maneuver damage while below 50% HP
    GuardianReactions,      // Guardian: reactions cover adjacent allies (M4)
    RangePlusOne,           // Aimed: ranged maneuvers reach one further
    StatusDurationPlusOne,  // Suppression: root and suppress from maneuvers last one turn longer
    LinePlusOne,            // Volley: line maneuvers are one tile longer
    SelfDisplacePlusOne,    // Skirmish: self-displacement maneuvers go one hex further (none at launch)
    FirstManeuverCheaper,   // Ambush: the first maneuver each combat costs 1 Edge less
    CheaperVsMarked,        // Marked: maneuvers against Marked targets cost 1 Edge less
    IgnoreOnePoise,         // Vigilant (M5): stagger maneuvers ignore 1 Poise
    DamageVsMostOpenings,   // Opportunist (M5): +1 maneuver damage against the enemy with the most Openings
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

    // ── Edge (spec v1 §5) ─────────────────────────────────────────────────
    public EdgeTrigger EdgeTrigger = EdgeTrigger.None;
    public int EdgeTriggerValue = 1;         // Edge granted per trigger
    public string EdgeTriggerStatus = null;  // for HitStatusedTarget
    public int EdgeStartBonus = 0;           // added to starting Edge (Ambush)
    public StanceManeuverRider ManeuverRider = StanceManeuverRider.None;

    /// <summary>Spec v1 §5: Opportunist is "Either" class. Class stays the nominal
    /// bucket; FitsClass is what the Training tab and the hiring roll read.</summary>
    public bool EitherClass = false;
    public bool FitsClass(MartialClass c) => c != MartialClass.None && (EitherClass || Class == c);

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
            Description = "+1 attack damage. On hit: push target 1 tile. -1 armor. Edge: +1 on your first hit each turn.",
            Class = MartialClass.Fighter,
            PassiveArmorPenalty = 1,
            AttackDamageBonus = 1,
            AttackPushTiles = 1,
            EdgeTrigger = EdgeTrigger.FirstHitEachTurn,
            ManeuverRider = StanceManeuverRider.PushPlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "defensive",
            DisplayName = "Defensive",
            Description = "+3 armor. On hit: gain 2 shield. -1 attack damage. Edge: +1 when struck (once per enemy action).",
            Class = MartialClass.Fighter,
            PassiveArmorBonus = 3,
            AttackDamageBonus = -1,
            OnHitSelfShieldGain = 2,
            EdgeTrigger = EdgeTrigger.WhenStruck,
            ManeuverRider = StanceManeuverRider.GainShield,
        });

        Add(new StanceDefinition
        {
            Id = "reckless",
            DisplayName = "Reckless",
            Description = "Attack hits all adjacent enemies. Take 2 damage after attacking. -2 armor. Edge: +2 at the start of your turn.",
            Class = MartialClass.Fighter,
            PassiveArmorPenalty = 2,
            OnHitSelfDamage = 2,
            SpecialTag = StanceSpecialTag.AoeAdjacent,
            EdgeTrigger = EdgeTrigger.TurnStart,
            EdgeTriggerValue = 2,
            ManeuverRider = StanceManeuverRider.EdgeDebt,
        });

        Add(new StanceDefinition
        {
            Id = "duelist",
            DisplayName = "Duelist",
            Description = "+1 attack range. On hit: apply Vulnerable for 1 turn (target takes +2 damage). Edge: +1 when you hit a Vulnerable target.",
            Class = MartialClass.Fighter,
            AttackRangeBonus = 1,
            OnHitStatusName = "vulnerable",
            OnHitStatusDuration = 1,
            EdgeTrigger = EdgeTrigger.HitStatusedTarget,
            EdgeTriggerStatus = "vulnerable",
            ManeuverRider = StanceManeuverRider.DamageVsVulnerable,
        });

        Add(new StanceDefinition
        {
            Id = "berserk",
            DisplayName = "Berserk",
            Description = "Gain +1 damage per 5 HP missing (max +5). Edge: +1 at turn start while below half HP.",
            Class = MartialClass.Fighter,
            SpecialTag = StanceSpecialTag.BerserkScaling,
            SpecialTagValue = 5, // cap
            EdgeTrigger = EdgeTrigger.TurnStartBelowHalfHp,
            ManeuverRider = StanceManeuverRider.DamageBelowHalf,
        });

        Add(new StanceDefinition
        {
            Id = "guardian",
            DisplayName = "Guardian",
            Description = "Adjacent allies gain +2 armor. Attack taunts: enemies target you next turn. Edge: +1 when an adjacent ally is attacked.",
            Class = MartialClass.Fighter,
            SpecialTag = StanceSpecialTag.GuardianAura,
            SpecialTagValue = 2, // armor granted to allies
            OnHitStatusName = "taunted",
            OnHitStatusDuration = 1,
            EdgeTrigger = EdgeTrigger.AdjacentAllyAttacked,
            ManeuverRider = StanceManeuverRider.GuardianReactions,
        });

        // ── Ranger stances ────────────────────────────────────────────────

        Add(new StanceDefinition
        {
            Id = "aimed",
            DisplayName = "Aimed",
            Description = "+3 damage if you haven't moved this turn. Ignores armor. Edge: +1 if you end your turn without moving.",
            Class = MartialClass.Ranger,
            AttackDamageBonus = 3,
            AttackIgnoresArmor = true,
            SpecialTag = StanceSpecialTag.AimedRequiresNoMove,
            EdgeTrigger = EdgeTrigger.TurnEndNoMove,
            ManeuverRider = StanceManeuverRider.RangePlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "suppression",
            DisplayName = "Suppression",
            Description = "On hit: target loses 1 move point next turn. Edge: +1 when you hit a Suppressed target.",
            Class = MartialClass.Ranger,
            OnHitStatusName = "suppressed",
            OnHitStatusDuration = 1,
            EdgeTrigger = EdgeTrigger.HitStatusedTarget,
            EdgeTriggerStatus = "suppressed",
            ManeuverRider = StanceManeuverRider.StatusDurationPlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "volley",
            DisplayName = "Volley",
            Description = "Attack hits all enemies in a line. -1 damage per target after the first. Edge: +1 per extra enemy hit.",
            Class = MartialClass.Ranger,
            SpecialTag = StanceSpecialTag.LinePiercing,
            EdgeTrigger = EdgeTrigger.PerExtraTargetHit,
            ManeuverRider = StanceManeuverRider.LinePlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "skirmish",
            DisplayName = "Skirmish",
            Description = "+1 speed. After attacking, move 1 tile for free. Edge: +1 when your action differs from your last (max 2 per turn).",
            Class = MartialClass.Ranger,
            PassiveSpeedBonus = 1,
            SpecialTag = StanceSpecialTag.SkirmishDash,
            SpecialTagValue = 1, // free move tiles after attack
            EdgeTrigger = EdgeTrigger.Momentum,
            ManeuverRider = StanceManeuverRider.SelfDisplacePlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "ambush",
            DisplayName = "Ambush",
            Description = "First attack this combat deals +50% damage. Edge: start combat with 3; +1 when you hit an enemy at full HP.",
            Class = MartialClass.Ranger,
            SpecialTag = StanceSpecialTag.AmbushFirstStrike,
            EdgeTrigger = EdgeTrigger.HitUndamagedTarget,
            EdgeStartBonus = 2,
            ManeuverRider = StanceManeuverRider.FirstManeuverCheaper,
        });

        Add(new StanceDefinition
        {
            Id = "marked",
            DisplayName = "Marked",
            Description = "On hit: apply Marked. Next ally attack on that target deals +3 damage. Edge: +1 when an ally hits a Marked target.",
            Class = MartialClass.Ranger,
            OnHitStatusName = "marked",
            OnHitStatusDuration = 2,
            SpecialTag = StanceSpecialTag.MarkedTarget,
            SpecialTagValue = 3, // bonus damage for next ally hit
            EdgeTrigger = EdgeTrigger.AllyHitsMarkedTarget,
            ManeuverRider = StanceManeuverRider.CheaperVsMarked,
        });

        // ── M5 stances (Martial Maneuvers & Edge spec v1 §5) ─────────────

        Add(new StanceDefinition
        {
            Id = "vigilant",
            DisplayName = "Vigilant",
            Description = "No passive. Edge: +1 at turn end per visible enemy intent you stand ready to counter (beside a channeler, beside an attacker aimed at an ally, or in a charger's path), max 2. Stagger maneuvers ignore 1 Poise.",
            Class = MartialClass.Fighter,
            EdgeTrigger = EdgeTrigger.TurnEndCounteredIntents,
            ManeuverRider = StanceManeuverRider.IgnoreOnePoise,
        });

        Add(new StanceDefinition
        {
            Id = "opportunist",
            DisplayName = "Opportunist",
            Description = "No passive. Openings: each hit places an Opening on the target; your maneuvers against it spend Openings as Edge. Maneuvers against the enemy carrying the most Openings deal +1 damage.",
            Class = MartialClass.Fighter,
            EitherClass = true,
            EdgeTrigger = EdgeTrigger.Openings,
            ManeuverRider = StanceManeuverRider.DamageVsMostOpenings,
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
            Description = "Signature. +1 damage. On hit: apply Vulnerable for 1 turn. Edge: +1 when you hit a Vulnerable target.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            AttackDamageBonus = 1,
            OnHitStatusName = "vulnerable",
            OnHitStatusDuration = 1,
            EdgeTrigger = EdgeTrigger.HitStatusedTarget,
            EdgeTriggerStatus = "vulnerable",
            ManeuverRider = StanceManeuverRider.DamageVsVulnerable,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_loyal",
            DisplayName = "Oathwall ✦",
            Description = "Signature. +2 armor. Adjacent allies gain +3 armor. Edge: +1 when an adjacent ally is attacked.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            PassiveArmorBonus = 2,
            SpecialTag = StanceSpecialTag.GuardianAura,
            SpecialTagValue = 3,
            EdgeTrigger = EdgeTrigger.AdjacentAllyAttacked,
            ManeuverRider = StanceManeuverRider.GuardianReactions,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_curious",
            DisplayName = "Openings ✦",
            Description = "Signature. +1 damage. Attacks ignore armor. Edge: +1 on your first hit each turn.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            AttackDamageBonus = 1,
            AttackIgnoresArmor = true,
            EdgeTrigger = EdgeTrigger.FirstHitEachTurn,
            ManeuverRider = StanceManeuverRider.PushPlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_stoic",
            DisplayName = "Immovable ✦",
            Description = "Signature. +5 armor. On hit: gain 2 shield. Edge: +1 when struck (once per enemy action).",
            Class = MartialClass.Fighter,
            IsSignature = true,
            PassiveArmorBonus = 5,
            OnHitSelfShieldGain = 2,
            EdgeTrigger = EdgeTrigger.WhenStruck,
            ManeuverRider = StanceManeuverRider.GainShield,
        });

        Add(new StanceDefinition
        {
            Id = "sig_fighter_reckless",
            DisplayName = "Avalanche ✦",
            Description = "Signature. Attack hits all adjacent enemies at +2 damage. " +
                          "Take 2 damage after attacking. -1 armor. Edge: +2 at the start of your turn.",
            Class = MartialClass.Fighter,
            IsSignature = true,
            AttackDamageBonus = 2,
            PassiveArmorPenalty = 1,
            OnHitSelfDamage = 2,
            SpecialTag = StanceSpecialTag.AoeAdjacent,
            EdgeTrigger = EdgeTrigger.TurnStart,
            EdgeTriggerValue = 2,
            ManeuverRider = StanceManeuverRider.EdgeDebt,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_cunning",
            DisplayName = "Killing Angle ✦",
            Description = "Signature. +1 damage. On hit: apply Marked. The next ally attack " +
                          "on that target deals +4 damage. Edge: +1 when an ally hits a Marked target.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackDamageBonus = 1,
            OnHitStatusName = "marked",
            OnHitStatusDuration = 2,
            SpecialTag = StanceSpecialTag.MarkedTarget,
            SpecialTagValue = 4,
            EdgeTrigger = EdgeTrigger.AllyHitsMarkedTarget,
            ManeuverRider = StanceManeuverRider.CheaperVsMarked,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_loyal",
            DisplayName = "Warding Volley ✦",
            Description = "Signature. Attack hits all enemies in a line. On hit: target " +
                          "loses 1 move point next turn. Edge: +1 per extra enemy hit.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            OnHitStatusName = "suppressed",
            OnHitStatusDuration = 1,
            SpecialTag = StanceSpecialTag.LinePiercing,
            EdgeTrigger = EdgeTrigger.PerExtraTargetHit,
            ManeuverRider = StanceManeuverRider.LinePlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_curious",
            DisplayName = "Read the Wind ✦",
            Description = "Signature. +4 damage if you haven't moved this turn. Ignores armor. Edge: +1 if you end your turn without moving.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackDamageBonus = 4,
            AttackIgnoresArmor = true,
            SpecialTag = StanceSpecialTag.AimedRequiresNoMove,
            EdgeTrigger = EdgeTrigger.TurnEndNoMove,
            ManeuverRider = StanceManeuverRider.RangePlusOne,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_stoic",
            DisplayName = "Patient Shot ✦",
            Description = "Signature. +1 range. First attack this combat deals +50% damage. Edge: start combat with 3; +1 when you hit an enemy at full HP.",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackRangeBonus = 1,
            SpecialTag = StanceSpecialTag.AmbushFirstStrike,
            EdgeTrigger = EdgeTrigger.HitUndamagedTarget,
            EdgeStartBonus = 2,
            ManeuverRider = StanceManeuverRider.FirstManeuverCheaper,
        });

        Add(new StanceDefinition
        {
            Id = "sig_ranger_reckless",
            DisplayName = "Storm of Shafts ✦",
            Description = "Signature. +1 damage, +1 speed. After attacking, move up to " +
                          "3 tiles for free. Edge: +1 when your action differs from your last (max 2 per turn).",
            Class = MartialClass.Ranger,
            IsSignature = true,
            AttackDamageBonus = 1,
            PassiveSpeedBonus = 1,
            SpecialTag = StanceSpecialTag.SkirmishDash,
            SpecialTagValue = 3,
            EdgeTrigger = EdgeTrigger.Momentum,
            ManeuverRider = StanceManeuverRider.SelfDisplacePlusOne,
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
