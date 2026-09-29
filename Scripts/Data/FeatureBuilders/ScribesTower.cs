using Godot;
using System.Collections.Generic;

// ============================================================
// ScribesTower.cs
//
// Purpose:        The Scribe's Tower (spec D7; campus_building_
//                 upgrades_design_v1 §4): the guild's one venue for
//                 scrolls, spellglass and wand work. Gates and
//                 transactions live here; CampusScribePanel draws them.
//                   T1  scribe overworld scrolls; seal Common glass.
//                   T2  seal Uncommon glass; a field party's wands
//                       recharge at staging anchors and the castle
//                       (FieldMarch.BankHere), not only at home.
//                   T3  doctrine, chartered at the Grand Hall:
//                       Glasswright  Rares sealable, and a glass keeps
//                                    the sealed card's upgrades.
//                       Wandwright   rebind a wand to any aimable
//                                    Common/Uncommon the guild knows.
//                 Without the Tower nothing is scribed or sealed.
//                 Wands at home still recharge (armory upkeep), and
//                 glass already made still works.
// Layer:          Data / Feature builder
// Collaborators:  Charters.cs (doctrine gate), ItemInstance
//                 (BoundCardId, BoundTopTier, BoundBotTier, Charges),
//                 CardUpgradeApplier (upgraded halves),
//                 CombatManager.ItemCanAim (what an item can aim),
//                 SpellAcquisition.ScrollGoldCost (scroll price),
//                 CampusScribePanel.cs (UI), FieldMarch.cs (wands afield).
// Rulings:        2026-09-29, Magos: the Tower takes over sealing and
//                 scribing (design ruling 3).
// ============================================================

/// <summary>The Scribe's Tower's gates and verbs. Stateless.</summary>
public static class ScribesTower
{
    public const string Id = "scribes_tower";
    public const string Glasswright = "glasswright";
    public const string Wandwright = "wandwright";

    /// <summary>Gold to rebind a wand (Wandwright). A starting value.</summary>
    public const int RebindGold = 100;

    // ── Gates ────────────────────────────────────────────────────────────

    /// <summary>The Tower's tier, 0 when unbuilt or unsited.</summary>
    public static int Tier(GuildSaveData save)
    {
        if (save?.Buildings == null)
        {
            return 0;
        }
        foreach (var b in save.Buildings)
        {
            if (b.Id == Id)
            {
                return b.IsFunctional ? b.Tier : 0;
            }
        }
        return 0;
    }

    public static bool CanScribeScrolls(GuildSaveData save) => Tier(save) >= 1;

    public static bool GlasswrightActive(GuildSaveData save) => Charters.IsActive(save, Id, Glasswright);

    public static bool WandwrightActive(GuildSaveData save) => Charters.IsActive(save, Id, Wandwright);

    /// <summary>Tier 2: a field party's wands recharge at staging anchors and the
    /// parked castle, not only at home.</summary>
    public static bool RechargesAfield(GuildSaveData save) => Tier(save) >= 2;

    /// <summary>Can glass of this rarity be sealed now? Legendaries never.</summary>
    public static bool CanSealRarity(GuildSaveData save, CardRarity rarity)
    {
        int tier = Tier(save);
        // Blighted (campus corruption, §11c): the Tower seals any rarity below
        // Legendary, in cracked glass (see Cracked below).
        if (tier >= 1 && IsCracking(save))
        {
            return rarity != CardRarity.Legendary;
        }
        return rarity switch
        {
            CardRarity.Common => tier >= 1,
            CardRarity.Uncommon => tier >= 2,
            CardRarity.Rare => GlasswrightActive(save),
            _ => false,
        };
    }

    /// <summary>Damage cracked glass deals its caster when it shatters.</summary>
    public const int CrackedGlassBite = 3;

    /// <summary>The Tower stands on blighted ground: its glass comes out cracked.</summary>
    public static bool IsCracking(GuildSaveData save) => CampusBlight.IsBlighted(save, Id);

    /// <summary>What sealing costs this guild now: half, in cracked glass.</summary>
    public static int SealCost(GuildSaveData save, CardRarity rarity)
        => IsCracking(save) ? SealGold(rarity) / 2 : SealGold(rarity);

    /// <summary>Gold to seal one glass, by the card's rarity.</summary>
    public static int SealGold(CardRarity rarity) => rarity switch
    {
        CardRarity.Common => 60,
        CardRarity.Uncommon => 110,
        _ => 180,
    };

    // ── Spellglass ───────────────────────────────────────────────────────

    /// <summary>Cards the guild could seal if the Tower allowed every rarity:
    /// known (unlocked), below Legendary, with a top half items can aim. The
    /// panel greys the ones the Tower's tier does not reach.</summary>
    public static List<CardBlueprint> SealCandidates(GuildSaveData save)
    {
        var list = new List<CardBlueprint>();
        if (save?.UnlockedCardBlueprintIds == null)
        {
            return list;
        }
        foreach (var id in save.UnlockedCardBlueprintIds)
        {
            var bp = CardDatabase.Blueprints.Find(b => b.Id == id);
            if (bp == null || bp.Prebuilt?.TopHalf == null || bp.Rarity == CardRarity.Legendary)
            {
                continue;
            }
            if (!CombatManager.ItemCanAim(bp.Prebuilt.TopHalf.Targeting))
            {
                continue;
            }
            list.Add(bp);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Prebuilt.TopHalf.Name, b.Prebuilt.TopHalf.Name));
        return list;
    }

    /// <summary>The upgrade tiers of the guild's best owned copy of a card
    /// (highest total), or (0, 0) when none is upgraded or owned.</summary>
    public static (int top, int bot) BestOwnedTiers(GuildSaveData save, string blueprintId)
    {
        int bestTop = 0;
        int bestBot = 0;
        var cards = save?.PlayerDeck?.Cards;
        if (cards == null)
        {
            return (0, 0);
        }
        foreach (var c in cards)
        {
            if (c == null || c.BlueprintId != blueprintId)
            {
                continue;
            }
            if (c.TopTier + c.BotTier > bestTop + bestBot)
            {
                bestTop = c.TopTier;
                bestBot = c.BotTier;
            }
        }
        return (bestTop, bestBot);
    }

    /// <summary>The half a glass sealed now would cast: upgraded when the
    /// Glasswright holds the charter, printed otherwise.</summary>
    public static CardHalf SealPreview(GuildSaveData save, CardBlueprint bp)
    {
        if (bp == null)
        {
            return null;
        }
        if (GlasswrightActive(save))
        {
            var (top, bot) = BestOwnedTiers(save, bp.Id);
            if (top > 0 || bot > 0)
            {
                var upgraded = CardUpgradeApplier.Apply(bp.Id, top, bot);
                if (upgraded?.TopHalf != null)
                {
                    return upgraded.TopHalf;
                }
            }
        }
        return bp.Prebuilt?.TopHalf;
    }

    /// <summary>Why this card cannot be sealed now, or null when it can.</summary>
    public static string CannotSealReason(GuildSaveData save, CardBlueprint bp)
    {
        if (save == null || bp == null)
        {
            return "Nothing to seal.";
        }
        if (Tier(save) < 1)
        {
            return "The Scribe's Tower is not built.";
        }
        if (!CanSealRarity(save, bp.Rarity))
        {
            return bp.Rarity switch
            {
                CardRarity.Uncommon => "Uncommon glass needs the Tower's second tier.",
                CardRarity.Rare => "Rare glass needs the Glasswright chartered at the Grand Hall.",
                _ => "This card cannot be sealed.",
            };
        }
        if (save.Gold < SealCost(save, bp.Rarity))
        {
            return $"Sealing costs {SealCost(save, bp.Rarity)} gold.";
        }
        return null;
    }

    /// <summary>Seal one card into a new glass. Returns the report line, or null.</summary>
    public static string TrySeal(GuildSaveData save, string blueprintId)
    {
        var bp = CardDatabase.Blueprints.Find(b => b.Id == blueprintId);
        if (CannotSealReason(save, bp) != null)
        {
            return null;
        }
        var glassDef = ItemDatabase.Get("spellglass");
        if (glassDef == null)
        {
            GD.PushWarning("[ScribesTower] No spellglass item definition (Data/Items/spellglass.json).");
            return null;
        }

        int cost = SealCost(save, bp.Rarity);
        save.Gold -= cost;
        var inst = ItemInstance.FromDefinition(glassDef);
        inst.BoundCardId = bp.Id;
        inst.Cracked = IsCracking(save);
        var half = bp.Prebuilt.TopHalf;
        if (GlasswrightActive(save))
        {
            var (top, bot) = BestOwnedTiers(save, bp.Id);
            inst.BoundTopTier = top;
            inst.BoundBotTier = bot;
            half = SealPreview(save, bp) ?? half;
        }
        inst.Name = inst.Cracked ? $"Cracked Spellglass: {half.Name}" : $"Spellglass: {half.Name}";
        save.Armory.AddItem(inst);
        SaveManager.MarkDirty();
        GD.Print($"[ScribesTower] Sealed '{bp.Id}' ({inst.BoundTopTier}/{inst.BoundBotTier}) for {cost}g"
                 + (inst.Cracked ? ", cracked." : "."));
        return inst.Cracked
            ? $"{half.Name} is sealed in cracked glass for {cost} gold. It bites whoever breaks it ({CrackedGlassBite} damage)."
            : $"{half.Name} is sealed in glass for {cost} gold. It waits in the Armory.";
    }

    // ── Overworld scrolls ────────────────────────────────────────────────

    /// <summary>Spells the guild can put on a scroll: the school's innates plus
    /// every learned spell, never an Attunement, never Emulate.</summary>
    public static List<OverworldSpellDefinition> Scribable(GuildSaveData save)
    {
        var list = new List<OverworldSpellDefinition>();
        var grim = save?.Cycle?.Grimoire;
        if (grim == null)
        {
            return list;
        }
        OverworldSpellRegistry.EnsureLoaded();
        void AddDef(OverworldSpellDefinition d)
        {
            if (d != null && !d.IsAttunement && d.EffectKey != "emulate" && !list.Contains(d))
            {
                list.Add(d);
            }
        }
        foreach (var innate in OverworldSpellRegistry.InnatesFor(save.Cycle.SelectedSchool))
        {
            AddDef(innate);
        }
        foreach (var id in grim.KnownSpellIds)
        {
            AddDef(OverworldSpellRegistry.Get(id));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>Scribe one scroll. Returns the report line, or null.</summary>
    public static string TryScribe(GuildSaveData save, OverworldSpellDefinition def)
    {
        var grim = save?.Cycle?.Grimoire;
        if (grim == null || def == null || !CanScribeScrolls(save))
        {
            return null;
        }
        int cost = SpellAcquisition.ScrollGoldCost(def);
        if (save.Gold < cost)
        {
            return null;
        }
        save.Gold -= cost;
        grim.ScrollInventory[def.Id] = grim.ScrollInventory.TryGetValue(def.Id, out int n) ? n + 1 : 1;
        SaveManager.MarkDirty();
        GD.Print($"[ScribesTower] Scribed '{def.Id}' for {cost}g (held ×{grim.ScrollInventory[def.Id]}).");
        return $"A scroll of {def.Name} is scribed for {cost} gold.";
    }

    // ── Wands (Wandwright) ───────────────────────────────────────────────

    /// <summary>Cards a Wandwright may bind into a wand: known, Common or
    /// Uncommon, with a top half items can aim.</summary>
    public static List<CardBlueprint> RebindCandidates(GuildSaveData save)
    {
        var list = new List<CardBlueprint>();
        foreach (var bp in SealCandidates(save))
        {
            if (bp.Rarity == CardRarity.Common || bp.Rarity == CardRarity.Uncommon)
            {
                list.Add(bp);
            }
        }
        return list;
    }

    /// <summary>Why this wand cannot be rebound to this card, or null.</summary>
    public static string CannotRebindReason(GuildSaveData save, ItemInstance wand, string blueprintId)
    {
        if (save == null || wand == null)
        {
            return "Nothing to rebind.";
        }
        var def = ItemDatabase.Get(wand.DefinitionId);
        if (def == null || !def.IsWand)
        {
            return "Only a wand can be rebound.";
        }
        if (!WandwrightActive(save))
        {
            return "Rebinding needs the Wandwright chartered at the Grand Hall.";
        }
        if (wand.EffectiveBoundCardId(def) == blueprintId)
        {
            return "The wand already holds that spell.";
        }
        if (!RebindCandidates(save).Exists(b => b.Id == blueprintId))
        {
            return "That spell cannot be bound into a wand.";
        }
        if (save.Gold < RebindGold)
        {
            return $"Rebinding costs {RebindGold} gold.";
        }
        return null;
    }

    /// <summary>Rebind a wand. Charges are left as they are: the rite changes the
    /// spell, not the wood. Returns the report line, or null.</summary>
    public static string TryRebind(GuildSaveData save, ItemInstance wand, string blueprintId)
    {
        if (CannotRebindReason(save, wand, blueprintId) != null)
        {
            return null;
        }
        var bp = CardDatabase.Blueprints.Find(b => b.Id == blueprintId);
        save.Gold -= RebindGold;
        wand.BoundCardId = blueprintId;
        SaveManager.MarkDirty();
        string spell = bp?.Prebuilt?.TopHalf?.Name ?? blueprintId;
        GD.Print($"[ScribesTower] {wand.Name} rebound to '{blueprintId}' for {RebindGold}g.");
        return $"{wand.Name} now casts {spell}.";
    }
}
