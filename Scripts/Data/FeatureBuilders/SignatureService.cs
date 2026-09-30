using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SignatureService.cs
//
// Purpose:        Signature slots, live (campus_building_upgrades_design_v1
//                 §16; SignaturePool.cs holds the data). The player grants up
//                 to 3 of the guild's own cards to each ARCANE companion,
//                 within a rarity point budget (8). A granted card is added
//                 to that companion's combat deck on top of its school's
//                 starter deck (ruled 2026-09-29: add, not replace).
//
//                 The pool (SignaturePoolState, cap 24) is kept as the set of
//                 blueprints granted to anyone: granting adds the blueprint,
//                 revoking the last grant of it removes it. So the pool's
//                 bound is real but never the first thing a player hits.
//
//                 Dissemination (Scriptorum T3 doctrine): granted cards carry
//                 the tiers of the guild's best owned copy (so the Scriptorum's
//                 study reaches every allied wizard), and the budget rises by 3.
//                 Without it they are fielded at their printed tiers.
// Layer:          Data / rules
// Collaborators:  SignaturePool.cs (WizardSignatureSlots, SignaturePoolState),
//                 CycleState (SignaturePool, SignatureGrants), LoreHalls
//                 (Dissemination gate), ScribesTower.BestOwnedTiers,
//                 CardUpgradeApplier, CombatManager.InitializeUnitDecks,
//                 ForcesScreen (the grant UI).
// ============================================================

/// <summary>Signature slot rules. Stateless: everything lives on the cycle.</summary>
public static class SignatureService
{
    /// <summary>Budget Dissemination adds to every grant.</summary>
    public const int DisseminationBudgetBonus = 3;

    public static bool DisseminationActive(GuildSaveData save)
        => Charters.IsActive(save, LoreHalls.ScriptorumId, LoreHalls.Dissemination);

    /// <summary>The companion's grant record, or null when none exists yet.</summary>
    public static WizardSignatureSlots Find(GuildSaveData save, string companionId)
    {
        var list = save?.Cycle?.SignatureGrants;
        if (list == null || string.IsNullOrEmpty(companionId))
        {
            return null;
        }
        foreach (var g in list)
        {
            if (g != null && g.CompanionId == companionId)
            {
                return g;
            }
        }
        return null;
    }

    private static WizardSignatureSlots FindOrCreate(GuildSaveData save, string companionId)
    {
        var g = Find(save, companionId);
        if (g != null)
        {
            return g;
        }
        save.Cycle.SignatureGrants ??= new List<WizardSignatureSlots>();
        g = new WizardSignatureSlots { CompanionId = companionId };
        save.Cycle.SignatureGrants.Add(g);
        return g;
    }

    public static List<string> Slotted(GuildSaveData save, string companionId)
        => Find(save, companionId)?.SlottedBlueprintIds ?? new List<string>();

    public static int Slots(GuildSaveData save, string companionId)
        => Find(save, companionId)?.Slots ?? WizardSignatureSlots.DefaultSlots;

    /// <summary>The grant's budget with the doctrine applied.</summary>
    public static int Budget(GuildSaveData save, string companionId)
        => (Find(save, companionId)?.RarityBudget ?? WizardSignatureSlots.DefaultRarityBudget)
           + (DisseminationActive(save) ? DisseminationBudgetBonus : 0);

    public static int PointsSpent(GuildSaveData save, string companionId)
        => Find(save, companionId)?.PointsSpent() ?? 0;

    public static int PointsFor(string blueprintId)
    {
        var bp = Blueprint(blueprintId);
        return WizardSignatureSlots.PointsFor(bp?.Rarity ?? CardRarity.Common);
    }

    /// <summary>Cards the guild could grant: every distinct blueprint in its real
    /// owned collection (never the debug deck), by name. Regalia are one of a
    /// kind and stay with the wizard.</summary>
    public static List<CardBlueprint> Candidates(GuildSaveData save)
    {
        var list = new List<CardBlueprint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cards = save?.PlayerDeck?.RealCards;
        if (cards == null)
        {
            return list;
        }
        foreach (var c in cards)
        {
            if (c == null || c.IsRegalia || string.IsNullOrEmpty(c.BlueprintId) || !seen.Add(c.BlueprintId))
            {
                continue;
            }
            var bp = Blueprint(c.BlueprintId);
            if (bp != null)
            {
                list.Add(bp);
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(Name(a), Name(b)));
        return list;
    }

    /// <summary>Why this card cannot be granted to this companion now, or null.</summary>
    public static string CannotGrantReason(GuildSaveData save, Companion c, string blueprintId)
    {
        if (save?.Cycle == null || c == null)
        {
            return "No save.";
        }
        if (c.UnitClass != "Arcane")
        {
            return "Only arcane companions cast the guild's spells.";
        }
        if (string.IsNullOrEmpty(blueprintId) || Blueprint(blueprintId) == null)
        {
            return "Choose a card.";
        }
        if (!Owns(save, blueprintId))
        {
            return "The guild does not own this card.";
        }
        var slotted = Slotted(save, c.Id);
        if (slotted.Contains(blueprintId))
        {
            return "Already granted to them.";
        }
        if (slotted.Count >= Slots(save, c.Id))
        {
            return $"No free slot ({slotted.Count} of {Slots(save, c.Id)} used).";
        }
        int after = PointsSpent(save, c.Id) + PointsFor(blueprintId);
        if (after > Budget(save, c.Id))
        {
            return $"Over budget: {after} of {Budget(save, c.Id)} points.";
        }
        var pool = save.Cycle.SignaturePool;
        if (pool != null && !pool.Contains(blueprintId) && pool.IsFull)
        {
            return $"The signature library is full ({pool.Count} of {pool.MaxPoolSize}). Revoke a grant elsewhere first.";
        }
        return null;
    }

    public static bool TryGrant(GuildSaveData save, Companion c, string blueprintId)
    {
        if (CannotGrantReason(save, c, blueprintId) != null)
        {
            return false;
        }
        save.Cycle.SignaturePool ??= new SignaturePoolState();
        var pool = save.Cycle.SignaturePool;
        pool.Spells ??= new List<SignatureSpell>();
        if (!pool.Contains(blueprintId))
        {
            pool.Spells.Add(new SignatureSpell
            {
                BlueprintId = blueprintId,
                AddedLunation = save.Cycle.Calendar?.CurrentLunation ?? 1,
            });
        }
        var g = FindOrCreate(save, c.Id);
        g.SlottedBlueprintIds ??= new List<string>();
        g.SlottedBlueprintIds.Add(blueprintId);
        SaveManager.MarkDirty();
        GD.Print($"[Signature] {c.Name} carries '{blueprintId}' ({g.PointsSpent()}/{Budget(save, c.Id)} pts).");
        return true;
    }

    public static bool Revoke(GuildSaveData save, string companionId, string blueprintId)
    {
        var g = Find(save, companionId);
        if (g?.SlottedBlueprintIds == null || !g.SlottedBlueprintIds.Remove(blueprintId))
        {
            return false;
        }
        // The library holds what is granted to anyone: drop an orphaned entry.
        bool stillGranted = false;
        foreach (var other in save.Cycle.SignatureGrants ?? new List<WizardSignatureSlots>())
        {
            if (other?.SlottedBlueprintIds != null && other.SlottedBlueprintIds.Contains(blueprintId))
            {
                stillGranted = true;
                break;
            }
        }
        if (!stillGranted)
        {
            save.Cycle.SignaturePool?.Spells?.RemoveAll(s => s != null && s.BlueprintId == blueprintId);
        }
        SaveManager.MarkDirty();
        return true;
    }

    /// <summary>The granted cards as combat cards for this companion: printed, or
    /// at the guild's best owned tiers under Dissemination. Unknown ids are skipped.</summary>
    public static List<Card> BuildCards(GuildSaveData save, string companionId)
    {
        var cards = new List<Card>();
        bool spread = DisseminationActive(save);
        foreach (var id in Slotted(save, companionId))
        {
            var bp = Blueprint(id);
            if (bp == null || IdleReason(save, companionId, id) != null)
            {
                continue;
            }
            Card card = null;
            if (spread)
            {
                var (top, bot) = ScribesTower.BestOwnedTiers(save, bp.Id);
                if (top > 0 || bot > 0)
                {
                    card = CardUpgradeApplier.Apply(bp.Id, top, bot);
                }
            }
            card ??= CardDatabase.Instantiate(bp);
            if (card != null)
            {
                cards.Add(card);
            }
        }
        return cards;
    }

    /// <summary>The guild still owns a non-Regalia copy of this blueprint.</summary>
    public static bool Owns(GuildSaveData save, string blueprintId)
    {
        var cards = save?.PlayerDeck?.RealCards;
        if (cards == null)
        {
            return false;
        }
        foreach (var c in cards)
        {
            if (c != null && !c.IsRegalia && string.Equals(c.BlueprintId, blueprintId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Why a slotted card will not take the field, or null when it will.
    /// Grants are checked again at every fight: a card the guild has since
    /// disenchanted or transmuted stays home, and so does whatever no longer
    /// fits the budget (Dissemination lapsed), last granted first.</summary>
    public static string IdleReason(GuildSaveData save, string companionId, string blueprintId)
    {
        if (!Owns(save, blueprintId))
        {
            return "The guild no longer owns this card.";
        }
        int budget = Budget(save, companionId), running = 0;
        foreach (var id in Slotted(save, companionId))
        {
            if (!Owns(save, id))
            {
                continue;
            }
            running += PointsFor(id);
            if (id == blueprintId)
            {
                return running > budget ? $"Over budget ({running} of {budget} points): it stays home." : null;
            }
        }
        return null;
    }

    public static string Name(CardBlueprint bp) => bp?.Prebuilt?.TopHalf?.Name ?? bp?.Id ?? "";

    private static CardBlueprint Blueprint(string id)
        => CardDatabase.Blueprints.Find(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
}
