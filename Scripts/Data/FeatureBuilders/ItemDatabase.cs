using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// ============================================================
// ItemDatabase.cs
//
// Purpose:        Three related classes that together implement
//                 the item / armory pipeline:
//                 - ItemDatabase: loads ItemDefinitions from
//                   Data/Items/*.json and caches them.
//                 - ArmoryData: per-save inventory of owned
//                   ItemInstances and per-unit loadouts.
//                 - EquipmentLoadout: static context populated
//                   at campus before a run; read by combat at
//                   spawn time to apply stat deltas + passives.
// Layer:          Loader (ItemDatabase) / Data (ArmoryData,
//                 ResolvedLoadout) / System (EquipmentLoadout)
// Collaborators:  ItemDefinition.cs, GuildSaveData.cs (Armory),
//                 CampusScreen.cs (calls BuildForRun),
//                 CombatManager.cs / Unit.cs (read Resolved
//                 loadouts at spawn)
// See:            README §3 (Architecture — item pipeline)
// ============================================================

/// <summary>Process-wide loader and registry for item blueprints. Loads lazily on first <see cref="Get"/> call; cache cleared by re-invoking <see cref="LoadAll"/> after manual reset.</summary>
public static class ItemDatabase
{
    private const string ITEMS_DIR = "res://Data/Items/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    private static Dictionary<string, ItemDefinition> _cache = new();
    private static bool _loaded = false;

    public static void LoadAll()
    {
        if (_loaded) return;
        _loaded = true;
        _cache.Clear();

        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(ITEMS_DIR)))
        {
            GD.PrintErr($"ItemDatabase: No items directory at {ITEMS_DIR}");
            return;
        }

        using var dir = DirAccess.Open(ITEMS_DIR);
        if (dir == null) return;

        dir.ListDirBegin();
        string filename = dir.GetNext();
        while (filename != "")
        {
            if (!dir.CurrentIsDir() && filename.EndsWith(".json"))
            {
                LoadFile($"{ITEMS_DIR}{filename}");
            }
            filename = dir.GetNext();
        }
        dir.ListDirEnd();

        GD.Print($"ItemDatabase: Loaded {_cache.Count} items.");
    }

    private static void LoadFile(string path)
    {
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return;

            var def = JsonSerializer.Deserialize<ItemDefinition>(file.GetAsText(), JsonOptions);
            if (def == null || string.IsNullOrEmpty(def.Id)) return;

            _cache[def.Id] = def;
        }
        catch (Exception e)
        {
            GD.PrintErr($"ItemDatabase: Error loading {path}: {e.Message}");
        }
    }

    public static ItemDefinition Get(string id)
    {
        if (!_loaded) LoadAll();
        return _cache.TryGetValue(id, out var def) ? def : null;
    }

    public static List<ItemDefinition> GetAll()
    {
        if (!_loaded) LoadAll();
        return _cache.Values.ToList();
    }

    /// <summary>
    /// Parse ItemPassiveTag from the definition's Passive string.
    /// Returns ItemPassiveTag.None if not recognized.
    /// </summary>
    public static ItemPassiveTag ParsePassive(ItemDefinition def)
    {
        if (def == null || string.IsNullOrEmpty(def.Passive)) return ItemPassiveTag.None;
        if (Enum.TryParse<ItemPassiveTag>(def.Passive, ignoreCase: true, out var tag))
            return tag;
        return ItemPassiveTag.None;
    }
}


// ══════════════════════════════════════════════════════════════════════════════
// ArmoryData
// Lives on GuildSaveData. Persists all owned item instances across runs.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// All items owned by the guild. Serialized as part of GuildSaveData.
/// Add this as a field: public ArmoryData Armory = new();
/// </summary>
public class ArmoryData
{
    /// <summary>All item instances the guild currently owns.</summary>
    public List<ItemInstance> OwnedItems = new();

    /// <summary>
    /// Per-unit loadout assignments. Key = unit/companion ID (or "wizard" for the player wizard).
    /// Value = which instance IDs are in each slot.
    /// </summary>
    public Dictionary<string, UnitLoadout> Loadouts = new();

    // ── Helpers ───────────────────────────────────────────────────────────

    public void AddItem(ItemInstance item)
    {
        OwnedItems.Add(item);
    }

    public void AddItem(ItemDefinition def)
    {
        OwnedItems.Add(ItemInstance.FromDefinition(def));
    }

    public bool RemoveItem(string instanceId)
    {
        // Unequip from any slot first
        foreach (var loadout in Loadouts.Values)
        {
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                if (loadout.GetSlot(slot) == instanceId)
                    loadout.ClearSlot(slot);
            }
        }
        int removed = OwnedItems.RemoveAll(i => i.InstanceId == instanceId);
        return removed > 0;
    }

    public ItemInstance GetInstance(string instanceId)
        => OwnedItems.FirstOrDefault(i => i.InstanceId == instanceId);

    public UnitLoadout GetLoadout(string unitId)
    {
        if (!Loadouts.TryGetValue(unitId, out var loadout))
        {
            loadout = new UnitLoadout();
            Loadouts[unitId] = loadout;
        }
        return loadout;
    }

    /// <summary>
    /// Equip an item to a unit's slot. Unequips whatever was there before
    /// (returns it to the armory — it stays in OwnedItems).
    /// Returns false if the item isn't in the armory or the slot doesn't match.
    /// </summary>
    public bool Equip(string unitId, string instanceId)
    {
        var item = GetInstance(instanceId);
        if (item == null) return false;

        if (!Enum.TryParse<EquipmentSlot>(item.Slot, ignoreCase: true, out var slot))
            return false;

        var loadout = GetLoadout(unitId);
        loadout.SetSlot(slot, instanceId);
        return true;
    }

    public void Unequip(string unitId, EquipmentSlot slot)
        => GetLoadout(unitId).ClearSlot(slot);

    /// <summary>
    /// Return all items currently equipped on a unit as (slot, instance) pairs.
    /// </summary>
    public List<(EquipmentSlot slot, ItemInstance item)> GetEquipped(string unitId)
    {
        var result = new List<(EquipmentSlot, ItemInstance)>();
        var loadout = GetLoadout(unitId);

        foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
        {
            var id = loadout.GetSlot(slot);
            if (id == null) continue;
            var item = GetInstance(id);
            if (item != null)
                result.Add((slot, item));
        }
        return result;
    }

    /// <summary>
    /// All items in the armory that are NOT currently equipped by anyone.
    /// </summary>
    public List<ItemInstance> GetUnequipped()
    {
        var equipped = new HashSet<string>();
        foreach (var loadout in Loadouts.Values)
            foreach (EquipmentSlot slot in Enum.GetValues(typeof(EquipmentSlot)))
            {
                var id = loadout.GetSlot(slot);
                if (id != null) equipped.Add(id);
            }
        return OwnedItems.Where(i => !equipped.Contains(i.InstanceId)).ToList();
    }
}


// ══════════════════════════════════════════════════════════════════════════════
// EquipmentLoadout (static context, like PlayerSession / NegotiationContext)
// Set at campus before departure. Read by CombatManager at spawn time.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Resolved item data for one unit, ready to apply to Unit stats at spawn.
/// CombatManager reads this — no ItemDatabase lookups needed in combat.
/// </summary>
public class ResolvedLoadout
{
    public string UnitId = "";

    // Aggregated stat deltas from all three slots
    public int BonusMaxHP = 0;
    public int BonusMaxMana = 0;
    public int BonusArmor = 0;
    public int BonusBaseSpeed = 0;
    public int BonusAttackDamage = 0;
    public int BonusAttackRange = 0;
    public int BonusSpellDamage = 0;

    // All passive tags active on this unit (one per equipped item max)
    public List<(ItemPassiveTag tag, int value)> Passives = new();
}

/// <summary>
/// Static context carrier for equipment loadouts.
/// CampusScreen populates this before a run starts.
/// CombatManager reads it when spawning player units.
/// </summary>
public static class EquipmentLoadout
{
    // Key = unit ID ("wizard", or companion ID like "elara_stormcaller")
    private static Dictionary<string, ResolvedLoadout> _loadouts = new();

    public static void Clear() => _loadouts.Clear();

    public static bool HasLoadout(string unitId) => _loadouts.ContainsKey(unitId);

    public static ResolvedLoadout Get(string unitId)
        => _loadouts.TryGetValue(unitId, out var l) ? l : null;

    /// <summary>
    /// Build resolved loadouts from ArmoryData for the current party
    /// (wizard + active companions). Call this at run start before
    /// transitioning to the overworld.
    /// </summary>
    public static void BuildForRun(ArmoryData armory, string wizardId, List<string> companionIds)
    {
        Clear();
        ItemDatabase.LoadAll();

        var allUnitIds = new List<string> { wizardId };
        allUnitIds.AddRange(companionIds);

        foreach (var unitId in allUnitIds)
        {
            var resolved = new ResolvedLoadout { UnitId = unitId };
            var equipped = armory.GetEquipped(unitId);

            foreach (var (slot, instance) in equipped)
            {
                var def = ItemDatabase.Get(instance.DefinitionId);
                if (def == null) continue;

                // Accumulate stat modifiers
                resolved.BonusMaxHP += def.Stats.MaxHP;
                resolved.BonusMaxMana += def.Stats.MaxMana;
                resolved.BonusArmor += def.Stats.Armor;
                resolved.BonusBaseSpeed += def.Stats.BaseSpeed;
                resolved.BonusAttackDamage += def.Stats.AttackDamage;
                resolved.BonusAttackRange += def.Stats.AttackRange;
                resolved.BonusSpellDamage += def.Stats.SpellDamage;

                // Collect passive tag
                var tag = ItemDatabase.ParsePassive(def);
                if (tag != ItemPassiveTag.None)
                    resolved.Passives.Add((tag, def.PassiveValue));
            }

            _loadouts[unitId] = resolved;
        }

        GD.Print($"EquipmentLoadout: Built loadouts for {_loadouts.Count} unit(s).");
    }
}
