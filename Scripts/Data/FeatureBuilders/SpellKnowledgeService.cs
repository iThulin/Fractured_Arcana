using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SpellKnowledgeService.cs
//
// Purpose:        Makes overworld-spell knowledge permanent.
//
//                 GrimoireState.KnownSpellIds is cycle-scoped, and S4
//                 removed the starting seed — so a fresh timeline knew
//                 NOTHING and the player re-learned every working from
//                 scratch, every cycle, forever. That put knowledge on the
//                 timeline layer while lore and card blueprints sat on the
//                 permanent one, which is a straight contradiction of the
//                 two-layer law (progression_persistence_model_v1 §2).
//
//                 Fix, deliberately minimal: the LEDGER is the permanent
//                 record; the Grimoire's list stays the per-cycle working
//                 copy and is re-seeded from the ledger at cycle start. All
//                 ~22 existing read sites keep working untouched, because
//                 they still read the working copy — it is simply no longer
//                 empty.
//
//                 What stays cycle-scoped, correctly: PreparedSpellIds
//                 (a loadout), ScrollInventory (consumable stock), Essence,
//                 per-expedition cast caps, beacons. Knowledge persists;
//                 preparation and resources do not.
//
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.KnownSpellIds (the record),
//                 GrimoireState.KnownSpellIds (the working copy),
//                 SpellAcquisition.Learn (the single grant funnel),
//                 SaveManager.Load / SeedDeckForSchool (sync points)
// See:            docs/progression_card_acquisition_v1_2.md
//
// AMENDS overworld_spell_system_v1_1 §5/§13 and the CycleState comment
// "Spell knowledge is timeline knowledge — dies with the cycle."
// User-ruled 2026-08-04.
// ============================================================

/// <summary>
/// Keeps the permanent spell record and the per-cycle working list in agreement.
/// Both directions are unions, never replacements, so no path through this file
/// can lose a spell the player has learned.
/// </summary>
public static class SpellKnowledgeService
{
    /// <summary>
    /// Learn a spell permanently AND make it usable this cycle. This is what
    /// SpellAcquisition's grant funnel should call instead of adding straight to
    /// the Grimoire. Returns false if it was already known.
    /// </summary>
    public static bool Learn(GuildSaveData save, string spellId)
    {
        if (save?.Ledger == null || string.IsNullOrWhiteSpace(spellId)) return false;

        save.Ledger.KnownSpellIds ??= new List<string>();

        bool isNew = !save.Ledger.KnownSpellIds.Contains(spellId);
        if (isNew)
        {
            save.Ledger.KnownSpellIds.Add(spellId);
            GD.Print($"[SpellKnowledge] '{spellId}' learned — permanent, {save.Ledger.KnownSpellIds.Count} known.");
        }

        var grim = save.Cycle?.Grimoire;
        if (grim != null)
        {
            grim.KnownSpellIds ??= new List<string>();
            if (!grim.KnownSpellIds.Contains(spellId))
                grim.KnownSpellIds.Add(spellId);
        }

        return isNew;
    }

    /// <summary>
    /// Reconcile the two lists in both directions, then return how many spells the
    /// cycle gained.
    ///
    /// Cycle → ledger runs FIRST and is the grandfather clause: a save made before
    /// this system carries its spells only on the cycle file, and they must be
    /// captured before anything else touches the lists. Ledger → cycle is the
    /// re-seed that makes a fresh timeline start knowing what the guild knows.
    ///
    /// Idempotent, and safe to call on every load and every new cycle.
    /// </summary>
    public static int Sync(GuildSaveData save)
    {
        if (save?.Ledger == null) return 0;

        save.Ledger.KnownSpellIds ??= new List<string>();

        var grim = save.Cycle?.Grimoire;
        if (grim == null) return 0;
        grim.KnownSpellIds ??= new List<string>();

        // 1. Grandfather: anything the cycle knows, the loom now knows.
        int absorbed = 0;
        foreach (var id in grim.KnownSpellIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (save.Ledger.KnownSpellIds.Contains(id)) continue;
            save.Ledger.KnownSpellIds.Add(id);
            absorbed++;
        }

        // 2. Re-seed: anything the loom knows, this cycle can cast.
        int seeded = 0;
        foreach (var id in save.Ledger.KnownSpellIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (grim.KnownSpellIds.Contains(id)) continue;
            grim.KnownSpellIds.Add(id);
            seeded++;
        }

        if (absorbed > 0 || seeded > 0)
            GD.Print($"[SpellKnowledge] Sync: absorbed {absorbed} into the loom, " +
                     $"seeded {seeded} into cycle {save.Cycle?.CycleNumber ?? 0}. " +
                     $"{save.Ledger.KnownSpellIds.Count} known permanently.");

        return seeded;
    }

    /// <summary>Does the guild know this spell, permanently? Read for UI and gating.</summary>
    public static bool Knows(GuildSaveData save, string spellId) =>
        !string.IsNullOrWhiteSpace(spellId) &&
        (save?.Ledger?.KnownSpellIds?.Contains(spellId) ?? false);
}
