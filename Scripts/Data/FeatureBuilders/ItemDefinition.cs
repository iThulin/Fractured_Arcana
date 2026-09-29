using System.Collections.Generic;

// ============================================================
// ItemDefinition.cs
//
// Purpose:        Item system data model. ItemDefinition is the
//                 blueprint loaded from JSON; ItemInstance is the
//                 runtime owned-by-armory copy; UnitLoadout is
//                 the three equipped slots on one unit;
//                 ItemPassiveTag enumerates data-driven passives.
// Layer:          Data
// Collaborators:  ItemDatabase.cs (blueprint registry),
//                 CompanionDefinition.cs (each Companion has a
//                 UnitLoadout), Unit.cs (combat-side equipped
//                 items), GuildSaveData.cs (persists instances)
// See:            README §3 (Architecture, item pipeline)
// ============================================================

/// <summary>The three equipment slots every unit (wizard or companion) has.</summary>
public enum EquipmentSlot
{
    Weapon,
    Armor,
    Trinket,
}

/// <summary>Martial Maneuvers & Edge spec v1 §6: the CLASS of a Weapon-slot item
/// grants maneuvers (R1: the item itself stays passive). Launch classes carry
/// maneuvers in Data/Maneuvers; the rest are named now so items can be authored
/// against them and gain their maneuvers post-launch (§6b).</summary>
public enum WeaponClass
{
    None,
    SwordShield,
    Polearm,
    Bow,
    Crossbow,
    Hammer,
    TwoHander,
    PairedBlades,
    Sling,
}

/// <summary>
/// Which unit class this item is designed for.
/// "Any" means it can be equipped by either.
/// </summary>
public enum ItemUnitClass
{
    Any,
    Wizard,
    Martial,
}

/// <summary>
/// Data-driven passive behaviours. CombatManager and Unit check these at the
/// appropriate moment. Add new values here as needed; no other code changes
/// required until you want to implement the behaviour itself.
/// </summary>
public enum ItemPassiveTag
{
    None,

    // ── Wizard weapon passives ───────────────────────────────────────────
    // RETIRED 2026-08-13 (never had consumers; cards carry SCHOOL, not
    // element; these were designed against a taxonomy that doesn't exist).
    // Kept so old save/JSON strings still parse; do not author new items
    // with them. Use the School* pair below.
    StormSpellCostReduction,    // retired, use SchoolSpellCostReduction
    FireSpellBonusDamage,       // retired, use SchoolSpellDamage

    // ── Wizard armor passives ────────────────────────────────────────────
    StartCombatWithShield,      // Gain N shield at combat start

    // ── Wizard trinket passives ──────────────────────────────────────────
    RestoreManaOnTurnStart,     // Restore N mana at the start of each turn
    FirstCardCostReduction,     // First card each turn costs N less mana

    // ── Martial weapon passives ──────────────────────────────────────────
    // RETIRED 2026-08-13: superseded by the trigger-bus key apply_bleed
    // (same behavior, one dispatcher). Kept for parse safety only.
    AttackAppliesBleed,         // retired, use apply_bleed / onAttack

    // ── Martial trinket passives (implemented 2026-08-13) ────────────────
    BonusDamageAboveHalfHP,     // +N attack damage when HP > 50% (ResolveMartialAttack)
    DamageReductionPerHit,      // Take N less damage from each hit, floor 1 (Unit.ApplyDamage)

    // ── School-keyed spell passives (2026-08-13, replace Fire/Storm) ─────
    // PassiveParam = CardSchool name ("Elementalist", …); empty = ALL schools.
    SchoolSpellDamage,          // +N damage on spells of the keyed school (cast pin)
    SchoolSpellCostReduction,   // keyed school's cards cost N less mana
}

/// <summary>
/// Flat stat modifiers applied to a unit when an item is equipped.
/// All fields default to 0 (no change).
/// </summary>
public class ItemStatModifiers
{
    public int MaxHP = 0;
    public int MaxMana = 0;
    public int Armor = 0;
    public int BaseSpeed = 0;
    public int AttackDamage = 0;    // martial units only
    public int AttackRange = 0;    // martial units only
    public int SpellDamage = 0;    // wizard units only: flat bonus to all spell damage
}

/// <summary>
/// Blueprint for an item. Loaded from Data/Items/*.json and cached by ItemDatabase.
/// Never mutated at runtime.
/// </summary>
public class ItemDefinition
{
    // ── Identity ─────────────────────────────────────────────────────────
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string Rarity = "Common";   // Common, Uncommon, Rare, Legendary
    public string Slot = "Trinket";  // "Weapon", "Armor", "Trinket"
    public string UnitClass = "Any";      // "Any", "Wizard", "Martial"
    /// <summary>Weapon slot only (spec v1 §8). JSON "weaponClass"; a WeaponClass
    /// name. Default None: a levy's unclassed blade, or a wizard focus. A martial
    /// fielding a None weapon has the basic attack and no maneuvers (R8, D4).</summary>
    public string WeaponClass = "None";
    [System.Text.Json.Serialization.JsonIgnore] public global::WeaponClass WeaponClassValue =>
        System.Enum.TryParse<global::WeaponClass>(WeaponClass, ignoreCase: true, out var w) ? w : global::WeaponClass.None;

    // ── Stat modifiers ────────────────────────────────────────────────────
    public ItemStatModifiers Stats = new();

    // ── Passive behaviour ─────────────────────────────────────────────────
    // One item can have at most one passive tag for now.
    // PassiveValue is the magnitude (e.g. "+2 damage" → PassiveValue = 2).
    public string Passive = "None";    // maps to ItemPassiveTag enum name
    public int PassiveValue = 0;

    // ── Overworld passive parameter (Q3, §7b) ─────────────────────────────
    // Extra arg for keyed overworld passives that need one. Currently only
    // Pathfinder uses it: PassiveParam names the terrain it cheapens (e.g.
    // "Swamp", matching OverworldHex.TerrainType.ToString()). Empty otherwise.
    public string PassiveParam = "";

    // ── Trigger-bus passive (Q2, §7a) ─────────────────────────────────────
    // When Trigger != "none", `Passive` is read as the effect KEY (lowercase,
    // e.g. "apply_bleed") and PassiveValue as its magnitude; the legacy
    // ItemPassiveTag enum path is skipped for that item (ParsePassive returns
    // None for keys not in the enum, so the two systems never double-fire).
    //   Trigger ∈ { "none", "onSpawn", "onAttack", "aura" }
    public string Trigger = "none";

    // ── Economy ───────────────────────────────────────────────────────────
    public int GoldValue = 50;   // base sell/buy price

    // ── Consumables (2026-08-13: v1's "actives are scrolls", finally built) ──
    // Slot = "Consumable": unequippable BY CONSTRUCTION (Equip's
    // EquipmentSlot enum parse fails), so nothing in the loadout pipeline
    // ever sees one. Used from the combat Scrolls button; consuming removes
    // the instance from the Armory. One consumable per unit per turn.
    /// <summary>"heal" | "shield" | "mana" | "ap". "" = not a consumable.</summary>
    public string ConsumeEffect = "";
    public int ConsumeValue = 0;

    /// <summary>"potion" (default) | "scroll". Two RULES, not two flavors:
    /// a potion is the UNIT's resource (drunk by the selected unit, one per
    /// unit per turn, body effects, the ward cannot drink); a scroll is the
    /// PARTY's resource (an arcane reading, one per player turn total,
    /// stacks with a potion on the same unit, and CAN target the ward,
    /// the protect-mission tool).
    /// <para>2026-09-29 (D6: scrolls are overworld only, spellglass is the combat
    /// spell): players see the "scroll" kind as a TABLET. The key stays "scroll"
    /// so item JSON and saves need no migration; only the words changed.</para></summary>
    public string ConsumeKind = "potion";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConsumable => !string.IsNullOrEmpty(ConsumeEffect);

    // ── Active items (Martial Maneuvers & Edge spec v1 §11, M6) ─────────────
    /// <summary>"wand" (Trinket slot, charges per expedition, binds a combat card),
    /// "spellglass" (Belt, single use, binds a combat card per INSTANCE), or "" for
    /// everything else. R1b: every active item is charge-, stock- or deck-limited.</summary>
    public string ActiveKind = "";
    /// <summary>Card blueprint id a wand casts. Spellglass leaves this empty and
    /// binds per instance (ItemInstance.BoundCardId), because one glass is one cast
    /// of one spell the guild sealed.</summary>
    public string BoundCardId = "";
    /// <summary>Wands: charges per expedition (spec: start 3, refilled at campus).</summary>
    public int MaxCharges = 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWand => string.Equals(ActiveKind, "wand", System.StringComparison.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSpellglass => string.Equals(ActiveKind, "spellglass", System.StringComparison.OrdinalIgnoreCase);
    /// <summary>M7: a Weapon-slot wizard focus that adds one Innate copy of its
    /// bound card to the holder's deck (spec §11b). Deck variance is its limit.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStaff => string.Equals(ActiveKind, "staff", System.StringComparison.OrdinalIgnoreCase);
    /// <summary>Belt items (R9): anything in the Consumable pseudo-slot: potions,
    /// scrolls, whetstones and spellglass alike. Two per unit, assigned on the
    /// Forces screen or the Armory tab.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBeltItem => string.Equals(Slot, "Consumable", System.StringComparison.OrdinalIgnoreCase);
}

/// <summary>Q2: a triggered ability an item grants its wearer. Carried on the
/// resolved loadout and copied onto Unit at spawn; fired by CombatManager
/// through the shared trigger dispatcher (BuildTriggeredEffect).</summary>
public class ItemAbility
{
    public string Key = "";       // effect key, e.g. "apply_bleed" / "shield_self" / "regen_aura"
    public string Trigger = "";   // "onSpawn" | "onAttack" | "aura"
    public int Value = 0;         // magnitude (from ItemDefinition.PassiveValue)
    public string SourceName = ""; // item name, for log grammar ([Item] Key: effect)
}

/// <summary>
/// A runtime instance of an item. Owned by ArmoryData or a unit's loadout.
/// Identical to the definition for now, but the instance is the right
/// abstraction for future durability, upgrades, or procedural rolls.
/// </summary>
public class ItemInstance
{
    public string DefinitionId = "";    // key into ItemDatabase
    public string InstanceId = "";    // unique per-instance GUID (set on creation)

    // Cached for fast access; mirrors the definition at creation time.
    // If you add item upgrades, store deltas here rather than mutating the def.
    public string Name = "";
    public string Slot = "Trinket";
    public string UnitClass = "Any";
    public string Rarity = "Common";
    public int GoldValue = 50;

    // ── Q5: the enchant slot (v1 rules: ONE slot, Workshop is the sole
    // mutation venue, handcrafted scripts only). Additive save fields: old
    // instances deserialize with an empty, unsealed slot. ────────────────
    /// <summary>WorkshopEnchants catalog id. "" = empty slot.</summary>
    public string EnchantKey = "";
    public int EnchantValue = 0;
    public string EnchantParam = "";
    public string EnchantTrigger = "";

    /// <summary>Blighted items arrive with the slot SEALED (§7d): no enchant
    /// until Cleansed at Workshop tier 3.</summary>
    public bool EnchantSealed = false;

    // ── Q5: blight (§7d), authored drawback, never rolled ────────────────
    /// <summary>WorkshopEnchants drawback id. "" = not blighted.</summary>
    public string DrawbackKey = "";
    public int DrawbackValue = 0;

    /// <summary>The above-floor innate bump a blighted drop carries (+N to the
    /// definition's PassiveValue in loadout resolution). SURVIVES Cleanse:
    /// what the corruption improved, it keeps; only the drawback and the seal
    /// are removed.</summary>
    public int BlightBonus = 0;

    // ── M6 active items. Additive save fields: old instances deserialize with
    // Charges -1 (not a charged item) and an empty binding. ───────────────────
    /// <summary>Wand charges left this expedition. -1 = this item has no charges.</summary>
    public int Charges = -1;
    /// <summary>Spellglass: the card blueprint id sealed in this glass. Wands leave
    /// it empty and read their definition's BoundCardId.</summary>
    public string BoundCardId = "";

    /// <summary>Glasswright (Scribe's Tower doctrine): the upgrade tiers the sealed
    /// card carried when it went into the glass. 0/0 casts the printed card.
    /// Additive save fields; older glass reads as printed.</summary>
    public int BoundTopTier = 0;
    public int BoundBotTier = 0;

    /// <summary>Spellglass sealed at a blighted Scribe's Tower (campus corruption,
    /// §11c): cheaper, any rarity, and it bites its caster when it shatters
    /// (ScribesTower.CrackedGlassBite). Additive save field; older glass is whole.</summary>
    public bool Cracked = false;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBlighted => !string.IsNullOrEmpty(DrawbackKey);

    /// <summary>The card this instance casts, if any: the instance binding first
    /// (spellglass), else the definition's (wand).</summary>
    public string EffectiveBoundCardId(ItemDefinition def)
        => !string.IsNullOrEmpty(BoundCardId) ? BoundCardId : (def?.BoundCardId ?? "");

    public static ItemInstance FromDefinition(ItemDefinition def)
    {
        return new ItemInstance
        {
            DefinitionId = def.Id,
            InstanceId = System.Guid.NewGuid().ToString(),
            Name = def.Name,
            Slot = def.Slot,
            UnitClass = def.UnitClass,
            Rarity = def.Rarity,
            GoldValue = def.GoldValue,
            Charges = def.MaxCharges > 0 ? def.MaxCharges : -1,
        };
    }
}

/// <summary>
/// The three equipped item slots for one unit.
/// Null = slot is empty.
/// Stored in EquipmentLoadout keyed by unit/companion ID.
/// </summary>
public class UnitLoadout
{
    public string WeaponInstanceId = null;
    public string ArmorInstanceId = null;
    public string TrinketInstanceId = null;

    /// <summary>R9 (spec v1 §11): the Belt, two slots of consumables or spellglass
    /// per unit. Additive save field; old loadouts deserialize with an empty belt.
    /// Kept as a list rather than two named slots so the enum loops over
    /// EquipmentSlot stay untouched.</summary>
    public List<string> BeltInstanceIds = new();
    public const int BeltSlots = 2;

    /// <summary>Proving Grounds (Training Grounds doctrine, design §12): a second
    /// weapon the martial carries and changes to mid-fight for 1 AP. Null when
    /// none. Additive save field; outside the EquipmentSlot enum on purpose, so
    /// the per-slot loops (stat totals, the three slot cards) never count it.</summary>
    public string SecondWeaponInstanceId = null;

    public string GetSlot(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.Weapon => WeaponInstanceId,
        EquipmentSlot.Armor => ArmorInstanceId,
        EquipmentSlot.Trinket => TrinketInstanceId,
        _ => null,
    };

    public void SetSlot(EquipmentSlot slot, string instanceId)
    {
        switch (slot)
        {
            case EquipmentSlot.Weapon: WeaponInstanceId = instanceId; break;
            case EquipmentSlot.Armor: ArmorInstanceId = instanceId; break;
            case EquipmentSlot.Trinket: TrinketInstanceId = instanceId; break;
        }
    }

    public void ClearSlot(EquipmentSlot slot) => SetSlot(slot, null);
}
