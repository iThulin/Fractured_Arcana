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
// See:            README §3 (Architecture — item pipeline)
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
/// appropriate moment. Add new values here as needed — no other code changes
/// required until you want to implement the behaviour itself.
/// </summary>
public enum ItemPassiveTag
{
    None,

    // ── Wizard weapon passives ───────────────────────────────────────────
    StormSpellCostReduction,    // Storm spells cost 1 less mana
    FireSpellBonusDamage,       // Fire spells deal +N damage (N from PassiveValue)

    // ── Wizard armor passives ────────────────────────────────────────────
    StartCombatWithShield,      // Gain N shield at combat start

    // ── Wizard trinket passives ──────────────────────────────────────────
    RestoreManaOnTurnStart,     // Restore N mana at the start of each turn
    FirstCardCostReduction,     // First card each turn costs N less mana

    // ── Martial weapon passives ──────────────────────────────────────────
    AttackAppliesBleed,         // Melee attacks apply bleed (1 turn)

    // ── Martial trinket passives ─────────────────────────────────────────
    BonusDamageAboveHalfHP,     // +N attack damage when HP > 50%
    DamageReductionPerHit,      // Take N less damage from each hit (flat reduction)
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
    public int SpellDamage = 0;    // wizard units only — flat bonus to all spell damage
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

    // ── Economy ───────────────────────────────────────────────────────────
    public int GoldValue = 50;   // base sell/buy price
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

    // Cached for fast access — mirrors the definition at creation time.
    // If you add item upgrades, store deltas here rather than mutating the def.
    public string Name = "";
    public string Slot = "Trinket";
    public string UnitClass = "Any";
    public string Rarity = "Common";
    public int GoldValue = 50;

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
