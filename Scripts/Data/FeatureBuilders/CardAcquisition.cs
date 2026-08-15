using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CardAcquisition.cs
//
// Purpose:        Explore→named codices — the field-discovery verb from
//                 progression_card_acquisition_v1 §8:
//
//                   "Replace [the dead Hidden Vault legendary jackpot] with
//                    SPECIFIC cards attached to SPECIFIC places, surfaced
//                    through PoiSignal: 'a cold that shouldn't be here' three
//                    hexes north, and it is the Frostward Codex, and it is
//                    ALWAYS the Frostward Codex. A destination beats a jackpot,
//                    and it converts the intel layer into a card-acquisition
//                    layer for free."
//
//                 The exact card analogue of SpellAcquisition: an authored
//                 CardReward discovers that named blueprint; a CardCodex choice
//                 with no named reward rolls an unknown in-school Rare. Both
//                 write to the permanent unlock pool, so a card found in the
//                 field is a card owned across every timeline.
//
//                 DISCOVERY, not ownership: this unlocks the blueprint for the
//                 draft and the mint. Getting a physical copy is still the
//                 draft, or a splinter mint at the Library. This is the same
//                 knowledge/power split the whole system rides.
//
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.UnlockedCardBlueprintIds (the payoff),
//                 CardDatabase (blueprint lookup + rarity),
//                 MarginaliaService (excluded — its own verb owns those),
//                 ExpeditionManager.OnNarrativeCompleted (the call site)
// See:            docs/progression_card_acquisition_v1.md §8;
//                 SpellAcquisition.cs (the mirrored pattern)
// ============================================================

public static class CardAcquisition
{
    /// <summary>
    /// Discover a card: add its blueprint to the permanent unlock pool. Returns
    /// the card's display name when NEWLY discovered, or "" when it was a no-op
    /// (null/unknown id, a Legendary, a Marginalia card, or already known).
    /// Marks the save dirty so a mid-expedition discovery persists.
    ///
    /// Exclusions mirror the pity-timer's (CardCommissionService): Legendaries
    /// are Regalia and are never handed out as breadth; Marginalia cards have
    /// their own acquisition verb (defeat the faction) and must not be reachable
    /// through a second path.
    /// </summary>
    public static string Discover(GuildSaveData save, string blueprintId)
    {
        if (save?.Ledger == null || string.IsNullOrEmpty(blueprintId))
            return "";

        var bp = CardDatabase.GetByName(blueprintId);
        if (bp == null)
        {
            GD.PrintErr($"[CardAcquisition] CardReward '{blueprintId}' not in CardDatabase — skipped.");
            return "";
        }
        if (bp.Rarity == CardRarity.Legendary) return "";      // Regalia only
        if (MarginaliaService.IsMarginaliaCard(bp.Id)) return ""; // earned in the field, not found

        save.Ledger.UnlockedCardBlueprintIds ??= new List<string>();
        bool already = save.Ledger.UnlockedCardBlueprintIds.Any(id =>
            string.Equals(id, bp.Id, StringComparison.OrdinalIgnoreCase));
        if (already) return "";

        save.Ledger.UnlockedCardBlueprintIds.Add(bp.Id);
        SaveManager.MarkDirty();
        GD.Print($"[CardAcquisition] Discovered '{bp.Id}' — it enters the draft pool " +
                 $"and can be scribed at the Arcane Library.");
        return CardDatabase.GetDisplayName(bp);
    }

    /// <summary>
    /// Roll one unknown Rare of a NAMED school. Excludes Legendaries and
    /// Marginalia cards. Returns the blueprint id, or "" when that school's Rares
    /// are all already known. Does NOT unlock — the caller passes the result to
    /// <see cref="Discover"/>, mirroring the spell path. Shared by the codex
    /// (current school, §2a in-school breadth) and espionage theft (the target
    /// court's school, the sanctioned off-school exception).
    /// </summary>
    public static string RollUnknownRareOfSchool(GuildSaveData save, string schoolName)
    {
        if (save?.Ledger == null) return "";
        if (!Enum.TryParse<CardSchool>(schoolName, ignoreCase: true, out var school))
            return "";

        var known = new HashSet<string>(
            save.Ledger.UnlockedCardBlueprintIds ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var pool = CardDatabase.Blueprints
            .Where(b => b.School == school
                     && b.Rarity == CardRarity.Rare
                     && !known.Contains(b.Id)
                     && !MarginaliaService.IsMarginaliaCard(b.Id))
            .ToList();

        if (pool.Count == 0) return "";
        return pool[(int)(GD.Randi() % (uint)pool.Count)].Id;
    }

    /// <summary>The codex use case: roll an unknown Rare of the school currently
    /// being played (§2a — found breadth pays the school you are on). Thin alias
    /// over <see cref="RollUnknownRareOfSchool"/>.</summary>
    public static string RollUnknownInSchoolRare(GuildSaveData save, string schoolName)
        => RollUnknownRareOfSchool(save, schoolName);
}
