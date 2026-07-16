using Godot;
using System.Collections.Generic;

// ============================================================
// SpellAcquisition.cs  (S4, 2026-07-16)
//
// Purpose:        The acquisition layer for overworld spells
//                 (overworld_spell_system_v1_1 §11) — the single
//                 owner of "how does the guild come to KNOW a
//                 spell." Three in-expedition routes plus the
//                 scroll price formula:
//                 - Lore POIs: authored SpellReward on a choice,
//                   or a bonus roll against a terrain-flavored
//                   pool (forge-country leans Tinker, graves lean
//                   Communion — mirrored here as terrain → ids).
//                 - Negotiation: a "tuition" DealTerm injected at
//                   table-open; granted only when the deal closes
//                   in the Cordial zone (§11 "Cordial deals").
//                 - Speak with the Fallen: an occasional whisper
//                   from the dead (S3 wrap-up's lore-drop note).
//                 Learnables = every registry definition that is
//                 neither innate nor Attunement: the 8 school
//                 exemplars + the 4 Generals (un-seeded in S4 —
//                 Generals are ACQUIRED now, not given).
// Layer:          System (static; stateless — all state lives on
//                 GrimoireState)
// Collaborators:  GrimoireState.cs (KnownSpellIds),
//                 OverworldSpellRegistry.cs (definitions),
//                 ExpeditionManager.cs (lore/negotiation grants),
//                 NegotiationManager.cs (tuition-term injection),
//                 CampusScreen.cs (scroll pricing)
// See:            overworld_spell_system_v1_1.docx §8a, §11
// ============================================================

/// <summary>Acquisition rules for overworld spells: terrain-flavored drop
/// pools, drop chances, the negotiation tuition pick, and scroll pricing.
/// All knobs are constants here — one file to tune.</summary>
public static class SpellAcquisition
{
    // ── Tuning knobs ─────────────────────────────────────────────────────

    /// <summary>Bonus-roll chance that a resolved Narrative (lore) POI with no
    /// authored SpellReward teaches an unknown learnable.</summary>
    public const float NarrativeDropChance = 0.30f;

    /// <summary>Chance a Speak-with-the-Fallen cast also yields a working.</summary>
    public const float SpeakFallenDropChance = 0.20f;

    /// <summary>Tuition-offer chance at table-open: Merchants and Scholars
    /// trade in knowledge; everyone else only sometimes thinks to offer.</summary>
    public const float DealOfferChanceKeen = 0.75f;
    public const float DealOfferChanceOther = 0.35f;

    /// <summary>Scroll price (§8a): gold = max(floor, perEssence × base cost).
    /// Scrolls bypass the Essence economy entirely, so this price is THE
    /// balance lever — a scroll library must never substitute for the pool.</summary>
    public const int ScrollCostPerEssence = 25;
    public const int ScrollCostFloor = 30;

    // ── Pools ────────────────────────────────────────────────────────────

    /// <summary>Terrain-flavored learnable ids (§11: "forge sites lean
    /// Tinker-adjacent learnables, grave sites lean Communion"). Order is
    /// irrelevant — the roll picks uniformly among the UNKNOWN entries.</summary>
    private static List<string> FlavoredIds(OverworldHex.TerrainType terrain) => terrain switch
    {
        OverworldHex.TerrainType.Volcanic => new() { "ember_ward" },
        OverworldHex.TerrainType.ArcaneGround => new() { "ley_tap" },
        OverworldHex.TerrainType.Forest or
        OverworldHex.TerrainType.Swamp or
        OverworldHex.TerrainType.Marsh => new() { "thornwall" },
        OverworldHex.TerrainType.Ruins => new() { "pallid_bargain" },
        OverworldHex.TerrainType.Snow or
        OverworldHex.TerrainType.Tundra => new() { "stasis_snare" },
        OverworldHex.TerrainType.Mountain or
        OverworldHex.TerrainType.Hills => new() { "fulminant_charge", "attuned_recall" },
        OverworldHex.TerrainType.Grassland or
        OverworldHex.TerrainType.Road => new() { "beguile" },
        _ => new(),
    };

    /// <summary>All learnable definitions the guild does not yet know.</summary>
    public static List<OverworldSpellDefinition> UnknownLearnables(GrimoireState grimoire)
    {
        var result = new List<OverworldSpellDefinition>();
        foreach (var def in OverworldSpellRegistry.Learnables())
            if (!grimoire.KnownSpellIds.Contains(def.Id))
                result.Add(def);
        return result;
    }

    // ── Rolls ────────────────────────────────────────────────────────────

    /// <summary>Roll one unknown learnable, preferring the terrain-flavored
    /// pool; falls back to any unknown learnable (Generals included). Returns
    /// "" when the guild already knows everything.</summary>
    public static string RollUnknownLearnable(GrimoireState grimoire,
                                              OverworldHex.TerrainType terrain)
    {
        var flavored = new List<string>();
        foreach (var id in FlavoredIds(terrain))
            if (!grimoire.KnownSpellIds.Contains(id) && OverworldSpellRegistry.Get(id) != null)
                flavored.Add(id);
        if (flavored.Count > 0)
            return flavored[(int)(GD.Randi() % (uint)flavored.Count)];

        var any = UnknownLearnables(grimoire);
        return any.Count == 0 ? "" : any[(int)(GD.Randi() % (uint)any.Count)].Id;
    }

    /// <summary>The tuition pick for a negotiation table: any unknown
    /// learnable, uniformly. "" when nothing remains to teach.</summary>
    public static string PickNegotiationSpell(GrimoireState grimoire)
    {
        var any = UnknownLearnables(grimoire);
        return any.Count == 0 ? "" : any[(int)(GD.Randi() % (uint)any.Count)].Id;
    }

    // ── Granting ─────────────────────────────────────────────────────────

    /// <summary>Add a spell to the guild's known list. Returns true only when
    /// newly learned (false: null/unknown id or already known). Marks the
    /// save dirty — KnownSpellIds rides CycleState, so a mid-expedition
    /// learn persists through any save (the S4 exit criterion).</summary>
    public static bool Learn(GrimoireState grimoire, string spellId)
    {
        if (grimoire == null || string.IsNullOrEmpty(spellId) ||
            OverworldSpellRegistry.Get(spellId) == null ||
            grimoire.KnownSpellIds.Contains(spellId))
            return false;
        grimoire.KnownSpellIds.Add(spellId);
        SaveManager.MarkDirty();
        GD.Print($"[Grimoire] Learned '{spellId}' " +
                 $"({grimoire.KnownSpellIds.Count} known this cycle).");
        return true;
    }

    // ── Scrolls (§8a) ────────────────────────────────────────────────────

    /// <summary>Gold price to scribe one scroll of a spell.</summary>
    public static int ScrollGoldCost(OverworldSpellDefinition def)
        => Mathf.Max(ScrollCostFloor, ScrollCostPerEssence * def.EssenceCost);
}
