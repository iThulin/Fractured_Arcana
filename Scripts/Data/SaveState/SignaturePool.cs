using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

// ============================================================
// SignaturePool.cs
//
// Purpose:        The faction's signature spells: the shared library every
//                 wizard in the guild draws from, and the per-wizard slot
//                 grants the player spends to shape an ally's deck.
//
//                 THE SHAPE, AND WHY IT IS NOT AN ALIAS OF THE DECK.
//                 The handoff asked for the player's combat deck to simply
//                 BE the pool. That collides with the shipped save schema
//                 in three places:
//                   1. Upgrades live per owned copy (OwnedCard.TopTier /
//                      BotTier / Grafts, keyed by InstanceId). "Upgrading a
//                      signature spell propagates to every wizard" has no
//                      referent when the player owns two copies of one
//                      blueprint at different tiers. Propagation needs an
//                      upgrade record at POOL scope, which is what
//                      SignatureSpell carries below.
//                   2. PlayerDeckSave caps the active deck at MaxDeckSize
//                      20 and floors it at MinDeckSize 10. Aliasing would
//                      mean every card added so an ally can slot it also
//                      bloats the player's own draws. A pool wants breadth;
//                      a combat deck wants tightness. Opposed pressures.
//                   3. Lending would be undefined: by blueprint (both draw
//                      it, the ally's copy has no upgrade identity) or by
//                      instance (the player loses it from their deck)?
//
//                 So: three tiers, not two.
//                   collection  (OwnedCard, already exists)
//                     -> signature pool  (bounded, THIS file)
//                       -> player's active deck (ActiveDeckInstanceIds,
//                          already exists, unchanged)
//                 Bounding the pool is the whole point: an unbounded pool
//                 is just the collection under a new name, and granting a
//                 slot out of it costs the player nothing.
//
//                 Pure data plus arithmetic helpers. No behaviour, no
//                 nodes. Additive into CycleState (the deck is already
//                 cycle-scoped), no SaveManager version bump.
// Layer:          Data
// Collaborators:  CycleState.cs (stores it), GuildSaveData.cs
//                 (PlayerDeckSave / OwnedCard, the collection tier),
//                 CardDatabase.cs (CardBlueprint.Rarity),
//                 FieldExpeditionSaveAssert.cs (round-trip proof)
// See:            docs/expedition_bastion_pressure_test_2026-09-21.md,
//                 finding 4; handoff section 4.6
// ============================================================

/// <summary>One spell in the faction library. The upgrade tiers here are the
/// POOL-level record: this is what makes an upgrade propagate to every wizard
/// referencing the blueprint, which per-instance OwnedCard tiers cannot do.
/// The player's own owned copies keep their own tiers for their own deck;
/// these are the tiers an ALLY gets when the spell is slotted to them.</summary>
public class SignatureSpell
{
    /// <summary>CardBlueprint.Id, "school:TopName|BotName". Decks reference
    /// the pool by id, never by copy.</summary>
    public string BlueprintId = "";

    /// <summary>0 = base, 1 = Refined, 2 = Mastered, 3 = Ascended. Same ladder
    /// as OwnedCard so the Scriptorum upgrade path can write either.</summary>
    public int TopTier = 0;

    public int BotTier = 0;

    /// <summary>Ruling 2 (2026-09-21): remote casting is a FLAG on a signature
    /// spell, not a separate pool. Damage spells never carry it. Note that the
    /// cast itself should spend Essence through the existing overworld spell
    /// system (GrimoireState / OverworldSpellManager) rather than inventing a
    /// second budget. See the pressure test, finding 6.</summary>
    public bool CastableRemotely = false;

    /// <summary>Lunation the spell entered the pool, for the library log.</summary>
    public int AddedLunation = 0;
}

/// <summary>The faction library: a bounded, explicitly curated set of
/// blueprints drawn from the collection. Every wizard's deck references
/// entries here by id.</summary>
public class SignaturePoolState
{
    public List<SignatureSpell> Spells = new();

    /// <summary>Hard cap on pool size. Bounding is what makes granting a slot
    /// a real choice. Upgradeable later through the campus; starting value
    /// sits above MaxDeckSize 20 so the player's own deck can always be drawn
    /// entirely from the pool without the pool feeling like a straitjacket.</summary>
    public int MaxPoolSize = DefaultMaxPoolSize;

    public const int DefaultMaxPoolSize = 24;

    [JsonIgnore]
    public int Count => Spells != null ? Spells.Count : 0;

    [JsonIgnore]
    public bool IsFull => Count >= MaxPoolSize;

    public SignatureSpell Find(string blueprintId)
    {
        if (Spells == null || string.IsNullOrEmpty(blueprintId))
        {
            return null;
        }
        foreach (var s in Spells)
        {
            if (s != null && s.BlueprintId == blueprintId)
            {
                return s;
            }
        }
        return null;
    }

    public bool Contains(string blueprintId) => Find(blueprintId) != null;
}

/// <summary>The signature slots the player has granted to ONE allied wizard.
/// Ruling 3 (2026-09-21): a rarity POINT BUDGET, not per-rarity caps, so a
/// companion cannot be built as five legendaries.
///
/// Worth knowing before tuning: at Slots 3 and Budget 8, with rare costing 3,
/// a realistic grant is two rares and two commons, so roughly two or three
/// cards of player authorship in a deck whose floor is MinDeckSize 10. That
/// is about a quarter of an ally's deck. If the player's tinkering is meant
/// to be felt in every fight, these are the two numbers to raise. See the
/// pressure test, finding 5.</summary>
public class WizardSignatureSlots
{
    /// <summary>Companion id this grant belongs to.</summary>
    public string CompanionId = "";

    /// <summary>Blueprint ids slotted, each of which must exist in the pool.</summary>
    public List<string> SlottedBlueprintIds = new();

    /// <summary>How many spells may be slotted at all. Upgradeable through
    /// campus or companion progression.</summary>
    public int Slots = DefaultSlots;

    /// <summary>Total rarity points the slotted spells may cost.</summary>
    public int RarityBudget = DefaultRarityBudget;

    public const int DefaultSlots = 3;
    public const int DefaultRarityBudget = 8;

    /// <summary>Rarity point cost. Starting values from ruling 3. The ONE place
    /// a rarity becomes a cost, so the editor readout and the validator can
    /// never disagree.</summary>
    public static int PointsFor(CardRarity rarity) => rarity switch
    {
        CardRarity.Legendary => 5,
        CardRarity.Rare => 3,
        CardRarity.Uncommon => 2,
        _ => 1,
    };

    /// <summary>Points the current grant spends, resolved through CardDatabase.
    /// Unknown blueprints count as Common rather than throwing, so a save that
    /// references a retired card degrades instead of breaking the editor.</summary>
    public int PointsSpent()
    {
        if (SlottedBlueprintIds == null)
        {
            return 0;
        }
        int total = 0;
        foreach (string id in SlottedBlueprintIds)
        {
            var bp = FindBlueprint(id);
            total += bp != null ? PointsFor(bp.Rarity) : 1;
        }
        return total;
    }

    /// <summary>True if the given blueprint could be added without breaking
    /// either the slot count or the point budget.</summary>
    public bool CanSlot(string blueprintId, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(blueprintId))
        {
            reason = "No spell chosen.";
            return false;
        }
        if (SlottedBlueprintIds != null && SlottedBlueprintIds.Contains(blueprintId))
        {
            reason = "Already granted to this wizard.";
            return false;
        }
        if (SlottedBlueprintIds != null && SlottedBlueprintIds.Count >= Slots)
        {
            reason = $"No free slot ({SlottedBlueprintIds.Count} of {Slots} used).";
            return false;
        }
        var bp = FindBlueprint(blueprintId);
        int cost = bp != null ? PointsFor(bp.Rarity) : 1;
        int after = PointsSpent() + cost;
        if (after > RarityBudget)
        {
            reason = $"Over budget: {after} of {RarityBudget} points.";
            return false;
        }
        return true;
    }

    /// <summary>Blueprint lookup, the same idiom RegaliaService uses. Kept
    /// private here rather than added to CardDatabase so this file introduces
    /// no new public surface.</summary>
    private static CardBlueprint FindBlueprint(string id) =>
        CardDatabase.Blueprints.Find(b =>
            string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
}
