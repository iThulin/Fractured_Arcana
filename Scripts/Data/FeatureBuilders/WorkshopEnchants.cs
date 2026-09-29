using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// WorkshopEnchants.cs
//
// Purpose:        Q5, the Enchanter's Workshop machinery: the
//                 handcrafted enchant catalog (v1 rule: no
//                 procedural affixes; every line here is
//                 authored), the enchant verb, the blight roll
//                 (§7d), and Cleanse at tier 3 (R23). Q5
//                 STARTING VALUES throughout: v1 §6–7's numeric
//                 content could not be located (same situation
//                 as K4; fresh-authored under the empirical
//                 pillar).
// Layer:          Data (FeatureBuilders)
// Collaborators:  ItemInstance (the enchant slot + blight
//                 fields), EquipmentLoadout.BuildForRun (the ONE
//                 resolution seam; enchants ride the same
//                 accumulator as innates), CampusWorkshopPanel
//                 (UI), ExpeditionManager (blight roll at the
//                 corrupted-ground drop site),
//                 CouncilQueries.BuildingTier (tier gate).
// Rules held:     one enchant slot; slot identity; two-effect
//                 ceiling by construction (innate + 1 enchant);
//                 sealed slots refuse enchants until Cleansed;
//                 re-enchanting overwrites (mutation through the
//                 sole mutation venue) at full price.
// ============================================================

/// <summary>One authored enchant line. Key vocabulary is shared with the
/// loadout resolver: "stat_*" keys land on ResolvedLoadout bonus fields;
/// overworld keys (corruption_ward / hazard_ward / pathfinder) land on the
/// party-summed fields; trigger keys (shield_self / regen_aura / apply_bleed)
/// ride the Q2 trigger bus. No new effect keys, no new handlers.</summary>
public class EnchantDef
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public int MinTier = 1;          // Workshop tier required
    public string AllowedSlot = "Any"; // "Any" / "Weapon" / "Armor" / "Trinket"
    public string Key = "";
    public int Value = 0;
    public string Param = "";
    public string Trigger = "";      // "" = not a trigger-bus effect
    public int GoldCost = 60;
}

/// <summary>The Workshop's verbs and tables. Stateless; all state lives on
/// ItemInstance (save) and the building tier (save).</summary>
public static class WorkshopEnchants
{
    // ── Doctrines (campus_building_upgrades_design_v1 §4, 2026-09-29) ─────
    public const string BuildingId = "enchanters_workshop";
    public const string UnbindingFloor = "unbinding_floor";
    public const string AttunementForge = "attunement_forge";

    /// <summary>Gold to rebind a staff's Innate card (Attunement Forge). A starting value.</summary>
    public const int AttuneGold = 120;

    /// <summary>Cleanse is the Unbinding Floor's verb, not tier 3's: a Workshop
    /// chartered to the Attunement Forge does not cleanse.</summary>
    public static bool CanCleanse(GuildSaveData save) => Charters.IsActive(save, BuildingId, UnbindingFloor);

    public static bool CanAttune(GuildSaveData save) => Charters.IsActive(save, BuildingId, AttunementForge);

    // ── Cleanse pricing (R23: tier 3) ────────────────────────────────────
    public const int CleanseGold = 150;
    public const int CleanseSplinters = 25;

    /// <summary>Blight chance (percent) for drops won on corrupted ground
    /// (corruption tier ≥ 2 at the combat hex).</summary>
    public const int BlightChancePct = 35;

    // ── The catalog (authored, tier-gated) ───────────────────────────────

    public static readonly List<EnchantDef> Catalog = new()
    {
        // Tier 1: stat lines
        new EnchantDef { Id = "keen_edge", Name = "Keen Edge",
            Description = "+1 attack damage.", MinTier = 1, AllowedSlot = "Weapon",
            Key = "stat_attackdamage", Value = 1, GoldCost = 60 },
        new EnchantDef { Id = "hardened", Name = "Hardened",
            Description = "+1 armor.", MinTier = 1, AllowedSlot = "Armor",
            Key = "stat_armor", Value = 1, GoldCost = 60 },
        new EnchantDef { Id = "vital_thread", Name = "Vital Thread",
            Description = "+3 max HP.", MinTier = 1, AllowedSlot = "Any",
            Key = "stat_maxhp", Value = 3, GoldCost = 60 },
        new EnchantDef { Id = "deep_well", Name = "Deep Well",
            Description = "+1 max mana.", MinTier = 1, AllowedSlot = "Trinket",
            Key = "stat_maxmana", Value = 1, GoldCost = 80 },

        // Tier 2: scripted effects (live keys only)
        new EnchantDef { Id = "warding_script", Name = "Warding Script",
            Description = "Reduces corruption attrition by 2 per corrupted step.",
            MinTier = 2, AllowedSlot = "Any",
            Key = "corruption_ward", Value = 2, GoldCost = 120 },
        new EnchantDef { Id = "waymark_forest", Name = "Waymark: Forest",
            Description = "-1 step cost crossing Forest (never below 1).",
            MinTier = 2, AllowedSlot = "Trinket",
            Key = "pathfinder", Value = 1, Param = "Forest", GoldCost = 120 },
        new EnchantDef { Id = "aegis_script", Name = "Aegis Script",
            Description = "Gain 3 shield at the start of combat.",
            MinTier = 2, AllowedSlot = "Armor",
            Key = "shield_self", Value = 3, Trigger = "onSpawn", GoldCost = 140 },
        new EnchantDef { Id = "mending_verse", Name = "Mending Verse",
            Description = "Allies within 1 tile recover 1 HP at the start of each of your turns.",
            MinTier = 2, AllowedSlot = "Trinket",
            Key = "regen_aura", Value = 1, Trigger = "aura", GoldCost = 140 },
    };

    // ── Blight drawbacks (§7d: authored, never rolled procedurally) ─────

    public static readonly (string key, int value, string text)[] Drawbacks =
    {
        ("blight_maxhp",  3, "the wearer's flesh pays for it (-3 max HP)"),
        ("blight_armor",  1, "it eats at what protects (-1 armor)"),
        ("blight_speed",  1, "it drags like wet rope (-1 speed)"),
        ("blight_damage", 1, "it dulls the striking hand (-1 attack damage)"),
    };

    // ═════════════════════════════════════════════════════════════════════
    // Verbs
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Enchants available for an item at the current Workshop tier
    /// (slot identity + tier gate; sealed slots return empty).</summary>
    public static List<EnchantDef> AvailableFor(ItemInstance item, int workshopTier)
    {
        if (item == null || item.EnchantSealed) return new List<EnchantDef>();
        // Consumables have no enchant slot. The "Any" slot lines must not
        // offer themselves to a potion.
        if (item.Slot == "Consumable") return new List<EnchantDef>();
        return Catalog
            .Where(e => e.MinTier <= workshopTier
                        && (e.AllowedSlot == "Any" || e.AllowedSlot == item.Slot))
            .ToList();
    }

    /// <summary>Write an enchant onto the item's one slot (overwrites any
    /// existing enchant; the Workshop is the sole mutation venue, and it
    /// charges every time). Returns the result line or null (sealed /
    /// unknown / wrong slot / tier / gold).</summary>
    public static string TryEnchant(ItemInstance item, string enchantId, int workshopTier)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || item == null || item.EnchantSealed) return null;
        if (IsOverrun(save)) return null;   // campus corruption: it has stopped working

        var e = Catalog.FirstOrDefault(x => x.Id == enchantId);
        if (e == null || e.MinTier > workshopTier) return null;
        if (e.AllowedSlot != "Any" && e.AllowedSlot != item.Slot) return null;
        int cost = EnchantCost(save, e);
        if (save.Gold < cost) return null;

        save.Gold -= cost;
        item.EnchantKey = e.Key;
        item.EnchantValue = e.Value;
        item.EnchantParam = e.Param;
        item.EnchantTrigger = e.Trigger;

        // A blighted Workshop (campus corruption, §11c) writes cheap, and the
        // blight writes with it: the item takes a drawback and its slot seals,
        // exactly as a blighted drop does, but with no innate bump to keep.
        string tail = "";
        if (IsTainting(save) && !item.IsBlighted)
        {
            var (key, value, text) = Drawbacks[DrawbackIndexFor(item)];
            item.DrawbackKey = key;
            item.DrawbackValue = value;
            item.EnchantSealed = true;
            if (!item.Name.StartsWith("Blighted "))
                item.Name = $"Blighted {item.Name}";
            tail = $" The blight writes with it: {text}. The slot is sealed.";
        }
        SaveManager.Save();
        GD.Print($"[Workshop] {item.Name} enchanted: {e.Name} ({cost}g).{tail}");
        return $"{e.Name} written onto {item.Name}.{tail}";
    }

    /// <summary>The Workshop stands on blighted ground (campus corruption, §11c).</summary>
    public static bool IsTainting(GuildSaveData save)
        => CampusBlight.IsBlighted(save, BuildingId) && !IsOverrun(save);

    /// <summary>The Workshop stands on overrun ground and has stopped working.</summary>
    public static bool IsOverrun(GuildSaveData save) => CampusBlight.Level(save, BuildingId) >= CampusBlight.MaxLevel;

    /// <summary>What an enchant costs this guild now: half on blighted ground.</summary>
    public static int EnchantCost(GuildSaveData save, EnchantDef e)
        => e == null ? 0 : IsTainting(save) ? e.GoldCost / 2 : e.GoldCost;

    /// <summary>Which authored drawback the blight writes onto this item. Keyed on
    /// the instance id, so it is fixed per item and a reload cannot reroll it.</summary>
    private static int DrawbackIndexFor(ItemInstance item)
    {
        int h = 17;
        foreach (char ch in item.InstanceId ?? "")
            h = unchecked(h * 31 + ch);
        return (int)((uint)h % (uint)Drawbacks.Length);
    }

    /// <summary>R23, as the Unbinding Floor doctrine (2026-09-29): strip the
    /// drawback, unseal the slot, and DOUBLE the blight's innate bump (the
    /// doctrine's payoff: blighted spoils are worth hunting). Gold + splinters.
    /// Returns the result line or null.</summary>
    public static string TryCleanse(ItemInstance item)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || item == null || !item.IsBlighted) return null;
        if (!CanCleanse(save)) return null;
        if (save.Gold < CleanseGold || save.ArcaneSplinters < CleanseSplinters) return null;

        save.Gold -= CleanseGold;
        save.ArcaneSplinters -= CleanseSplinters;
        item.DrawbackKey = "";
        item.DrawbackValue = 0;
        item.EnchantSealed = false;
        item.BlightBonus *= 2;
        if (item.Name.StartsWith("Blighted "))
            item.Name = item.Name.Substring("Blighted ".Length);
        SaveManager.Save();
        GD.Print($"[Workshop] {item.Name} cleansed ({CleanseGold}g + {CleanseSplinters} splinters).");
        return $"{item.Name} is cleansed: the drawback lifts, the slot unseals, " +
               "and what the corruption improved, it keeps twice over.";
    }

    /// <summary>Cards a staff may be attuned to: every distinct card in the deck
    /// collection, by blueprint id, in name order.</summary>
    public static List<CardBlueprint> AttuneCandidates(GuildSaveData save)
    {
        var list = new List<CardBlueprint>();
        var cards = save?.PlayerDeck?.Cards;
        if (cards == null) return list;
        foreach (var c in cards)
        {
            if (c == null || list.Exists(b => b.Id == c.BlueprintId)) continue;
            var bp = CardDatabase.Blueprints.Find(b => b.Id == c.BlueprintId);
            if (bp != null) list.Add(bp);
        }
        list.Sort((a, b) => string.CompareOrdinal(CardDatabase.GetDisplayName(a), CardDatabase.GetDisplayName(b)));
        return list;
    }

    /// <summary>Why this staff cannot be attuned to this card, or null.</summary>
    public static string CannotAttuneReason(GuildSaveData save, ItemInstance staff, string blueprintId)
    {
        if (save == null || staff == null) return "Nothing to attune.";
        var def = ItemDatabase.Get(staff.DefinitionId);
        if (def == null || !def.IsStaff) return "Only a staff can be attuned.";
        if (!CanAttune(save)) return "Attuning needs the Attunement Forge chartered at the Grand Hall.";
        if (staff.EffectiveBoundCardId(def) == blueprintId) return "The staff already opens with that card.";
        if (!AttuneCandidates(save).Exists(b => b.Id == blueprintId)) return "That card is not in your deck collection.";
        if (save.Gold < AttuneGold) return $"Attuning costs {AttuneGold} gold.";
        return null;
    }

    /// <summary>Attunement Forge: rebind a staff's Innate card (M7). Returns the
    /// result line or null.</summary>
    public static string TryAttune(GuildSaveData save, ItemInstance staff, string blueprintId)
    {
        if (CannotAttuneReason(save, staff, blueprintId) != null) return null;
        var bp = CardDatabase.Blueprints.Find(b => b.Id == blueprintId);
        save.Gold -= AttuneGold;
        staff.BoundCardId = blueprintId;
        SaveManager.Save();
        GD.Print($"[Workshop] {staff.Name} attuned to '{blueprintId}' ({AttuneGold}g).");
        return $"{staff.Name} now opens every fight with {CardDatabase.GetDisplayName(bp)}.";
    }

    // ═════════════════════════════════════════════════════════════════════
    // Blight roll (§7d), called at the corrupted-ground drop site
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Maybe blight a fresh drop: authored drawback, sealed slot,
    /// +1 above-floor innate. Mutates and returns the instance.</summary>
    public static ItemInstance MaybeBlight(ItemInstance item, RandomNumberGenerator rng)
    {
        if (item == null || rng.RandiRange(1, 100) > BlightChancePct) return item;
        // Consumables never blight: a one-use item has no slot to seal and
        // no worn drawback to carry; a "Blighted Draught" is a different
        // design (poison mechanics) this deliberately isn't.
        if (item.Slot == "Consumable") return item;

        var (key, value, _) = Drawbacks[rng.RandiRange(0, Drawbacks.Length - 1)];
        item.DrawbackKey = key;
        item.DrawbackValue = value;
        item.EnchantSealed = true;
        item.BlightBonus = 1;
        item.Name = $"Blighted {item.Name}";
        GD.Print($"[Blight] {item.Name}: {DrawbackText(key)}. Slot sealed.");
        return item;
    }

    public static string DrawbackText(string key)
    {
        foreach (var (k, _, text) in Drawbacks)
            if (k == key) return text;
        return "an ill weight on it";
    }
}
