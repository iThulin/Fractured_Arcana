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
// See:            README §3 (Architecture, item pipeline)
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
    /// (returns it to the armory; it stays in OwnedItems).
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
/// CombatManager reads this, so no ItemDatabase lookups are needed in combat.
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
    // Param added 2026-08-13 for the school-keyed passives (empty for the rest).
    public List<(ItemPassiveTag tag, int value, string param)> Passives = new();

    // Q2 (§7a): trigger-bus abilities from equipped items, dispatched on the
    // shared handler map in combat, NOT via the ItemPassiveTag switch above.
    public List<ItemAbility> Abilities = new();

    // Q3 (§4b/§7b): overworld traversal-resistance passives, summed across the
    // party and read during expedition traversal (attrition + step cost). These
    // are inert in combat: not a trigger, not an ItemPassiveTag.
    public int CorruptionWard = 0;
    public int HazardWard = 0;
    // Pathfinder step-cost reduction, keyed by terrain name
    // (OverworldHex.TerrainType.ToString(), e.g. "Swamp").
    public Dictionary<string, int> Pathfinder = new();
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

    // ── Q3 (§4b): party-summed traversal resistance ──────────────────────
    // Every equipped item on every party member contributes; the sum is what
    // expedition traversal reads. Absent loadouts contribute 0. §4b stacking:
    // resistances add across the party (the cap/floor is applied at the call
    // site in ExpeditionManager, not here).

    public static int PartyCorruptionWard()
    {
        int sum = 0;
        foreach (var l in _loadouts.Values) sum += l.CorruptionWard;
        return sum;
    }

    public static int PartyHazardWard()
    {
        int sum = 0;
        foreach (var l in _loadouts.Values) sum += l.HazardWard;
        return sum;
    }

    /// <summary>Σ Pathfinder step-cost reduction for `terrain` across the whole
    /// party. `terrain` is OverworldHex.TerrainType.ToString() (e.g. "Swamp").</summary>
    public static int PartyPathfinder(string terrain)
    {
        int sum = 0;
        foreach (var l in _loadouts.Values)
            if (l.Pathfinder.TryGetValue(terrain, out int r)) sum += r;
        return sum;
    }

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

                // Q5 (§7d): a blighted drop's above-floor innate: +N to the
                // definition's PassiveValue, wherever that value lands below.
                // Survives Cleanse by design.
                int pv = def.PassiveValue + instance.BlightBonus;

                // Q5 (§7d): blight drawbacks are authored stat penalties. Land
                // as negative bonuses so every consumer (combat spawn, pool,
                // readouts) prices the blight without new plumbing.
                if (instance.IsBlighted)
                {
                    switch (instance.DrawbackKey)
                    {
                        case "blight_maxhp":  resolved.BonusMaxHP        -= instance.DrawbackValue; break;
                        case "blight_armor":  resolved.BonusArmor        -= instance.DrawbackValue; break;
                        case "blight_speed":  resolved.BonusBaseSpeed    -= instance.DrawbackValue; break;
                        case "blight_damage": resolved.BonusAttackDamage -= instance.DrawbackValue; break;
                    }
                }

                // Q5: the enchant slot rides the SAME accumulator as innates.
                // One resolution seam, no parallel system (§7a's whole point).
                if (!string.IsNullOrEmpty(instance.EnchantKey))
                    AccumulateEffect(resolved, instance.EnchantKey, instance.EnchantValue,
                                     instance.EnchantParam, instance.EnchantTrigger,
                                     $"{def.Name} (enchant)");

                // Q3 (§4b/§7b): overworld traversal-resistance passives route into
                // dedicated party-summed fields, consumed during expedition
                // traversal. NOT a combat trigger, NOT an ItemPassiveTag. Checked
                // BEFORE the Q2 trigger block and the enum path so an overworld
                // item is completely inert in combat.
                string ovKey = (def.Passive ?? "").ToLowerInvariant();
                if (ovKey == "corruption_ward") { resolved.CorruptionWard += pv; continue; }
                if (ovKey == "hazard_ward")     { resolved.HazardWard    += pv; continue; }
                if (ovKey == "pathfinder")
                {
                    string terrain = def.PassiveParam ?? "";
                    resolved.Pathfinder.TryGetValue(terrain, out int cur);
                    resolved.Pathfinder[terrain] = cur + pv;
                    continue;
                }

                // Q2 (§7a): a trigger-bus item routes through the shared
                // dispatcher, NOT the enum path. Its `Passive` string is the
                // effect key; ParsePassive returns None for it, so the two
                // systems never both fire the same item.
                if (!string.IsNullOrEmpty(def.Trigger) &&
                    !string.Equals(def.Trigger, "none", System.StringComparison.OrdinalIgnoreCase))
                {
                    resolved.Abilities.Add(new ItemAbility
                    {
                        Key = def.Passive,
                        Trigger = def.Trigger,
                        Value = pv,
                        SourceName = def.Name,
                    });
                    continue;   // do NOT also add it to the enum Passives list
                }

                // Collect passive tag (legacy enum path for unmigrated items)
                var tag = ItemDatabase.ParsePassive(def);
                if (tag != ItemPassiveTag.None)
                    resolved.Passives.Add((tag, pv, def.PassiveParam ?? ""));
            }

            _loadouts[unitId] = resolved;
        }

        GD.Print($"EquipmentLoadout: Built loadouts for {_loadouts.Count} unit(s).");
    }

    /// <summary>Q5: route one effect line (an enchant) into the resolved
    /// loadout, using the same key vocabulary as definitions: "stat_*" →
    /// bonus fields, overworld keys → party-summed fields, trigger keys →
    /// the Q2 bus. Unknown keys are inert (a typo'd enchant does nothing,
    /// it doesn't crash a run).</summary>
    private static void AccumulateEffect(ResolvedLoadout resolved, string key,
        int value, string param, string trigger, string sourceName)
    {
        switch ((key ?? "").ToLowerInvariant())
        {
            case "stat_maxhp":        resolved.BonusMaxHP += value; return;
            case "stat_maxmana":      resolved.BonusMaxMana += value; return;
            case "stat_armor":        resolved.BonusArmor += value; return;
            case "stat_speed":        resolved.BonusBaseSpeed += value; return;
            case "stat_attackdamage": resolved.BonusAttackDamage += value; return;
            case "stat_attackrange":  resolved.BonusAttackRange += value; return;
            case "stat_spelldamage":  resolved.BonusSpellDamage += value; return;

            case "corruption_ward":   resolved.CorruptionWard += value; return;
            case "hazard_ward":       resolved.HazardWard += value; return;
            case "pathfinder":
            {
                string terrain = param ?? "";
                resolved.Pathfinder.TryGetValue(terrain, out int cur);
                resolved.Pathfinder[terrain] = cur + value;
                return;
            }
        }

        if (!string.IsNullOrEmpty(trigger) &&
            !string.Equals(trigger, "none", System.StringComparison.OrdinalIgnoreCase))
        {
            resolved.Abilities.Add(new ItemAbility
            {
                Key = key,
                Trigger = trigger,
                Value = value,
                SourceName = sourceName,
            });
        }
    }
}
