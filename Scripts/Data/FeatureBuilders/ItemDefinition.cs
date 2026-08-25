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
    /// the protect-mission tool).</summary>
    public string ConsumeKind = "potion";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConsumable => !string.IsNullOrEmpty(ConsumeEffect);
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

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBlighted => !string.IsNullOrEmpty(DrawbackKey);

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
