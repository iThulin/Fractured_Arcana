using System.Collections.Generic;

// ============================================================
// DossierService.cs
//
// Purpose:        The archmage dossier layer: quests-as-knowledge
//                 (docs/quest_system_narrative_spec_v1.md §4).
//                 Stamps permanent EternalLedger metaflags as the
//                 player crosses paths with each archmage's forces
//                 and defeats them in the field, revealing the
//                 authored WeaknessHints one victory at a time.
//                 Pure flag arithmetic over existing save state:
//                 the dossier quests in Data/Quests/dossiers.json
//                 gate/tick on these flags and QuestLogView renders
//                 the revealed hint text. Nothing else persists.
//                 The Astrologer's dossier advances from the
//                 corruption clock instead (CorruptionSpread):
//                 he has no patrols; his forces are the weather.
// Layer:          System (static, stateless)
// Collaborators:  ArchmageRegistry (hint text), EternalLedger
//                 (MetaNarrativeFlags), ExpeditionManager (field
//                 hooks), CorruptionSpread (Astrologer hooks),
//                 QuestLogView (rendering), QuestNotifier (toasts
//                 are diffed by the CALLER around these stamps).
// See:            docs/quest_system_narrative_spec_v1.md §4
// ============================================================

/// <summary>Stamps and queries the permanent archmage-dossier flags:
/// dossier_&lt;id&gt;_met and dossier_&lt;id&gt;_hint_N (1-based).</summary>
public static class DossierService
{
    public static string MetFlag(string archmageId) => $"dossier_{archmageId}_met";
    public static string HintFlag(string archmageId, int n) => $"dossier_{archmageId}_hint_{n}";

    /// <summary>True once the player's dossier on this archmage is open.</summary>
    public static bool IsMet(GuildSaveData save, string archmageId)
        => save?.Ledger?.MetaNarrativeFlags?.Contains(MetFlag(archmageId)) ?? false;

    /// <summary>How many weakness hints stand revealed (0..authored count).</summary>
    public static int HintsRevealed(GuildSaveData save, string archmageId)
    {
        var def = ArchmageRegistry.Get(archmageId);
        var flags = save?.Ledger?.MetaNarrativeFlags;
        if (def == null || flags == null) return 0;
        int n = 0;
        for (int i = 1; i <= def.WeaknessHints.Count; i++)
            if (flags.Contains(HintFlag(archmageId, i))) n++;
        return n;
    }

    /// <summary>Open the dossier if it isn't open yet. Returns true when THIS
    /// call newly opened it (caller may toast). Idempotent. Ignores empty ids,
    /// the generic "wilds" pursuer, and ids with no authored definition.</summary>
    public static bool EnsureMet(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        if (save?.Ledger == null || string.IsNullOrEmpty(archmageId)) return false;
        if (archmageId == "wilds" || ArchmageRegistry.Get(archmageId) == null) return false;
        string flag = MetFlag(archmageId);
        if (save.Ledger.MetaNarrativeFlags.Contains(flag)) return false;
        save.Ledger.MetaNarrativeFlags.Add(flag);
        SaveManager.MarkDirty();
        return true;
    }

    /// <summary>Open the dossier (if needed) and reveal the next unrevealed
    /// weakness hint. Returns the hint text revealed by THIS call, or null when
    /// the dossier was already full (or the id is unknown/ignored). Field
    /// victories call this via ExpeditionManager; the corruption clock calls it
    /// for the Astrologer when an archmage falls.</summary>
    public static string RevealNextHint(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        var def = ArchmageRegistry.Get(archmageId);
        if (save?.Ledger == null || def == null ||
            string.IsNullOrEmpty(archmageId) || archmageId == "wilds")
            return null;
        EnsureMet(archmageId);
        for (int i = 1; i <= def.WeaknessHints.Count; i++)
        {
            string flag = HintFlag(archmageId, i);
            if (save.Ledger.MetaNarrativeFlags.Contains(flag)) continue;
            save.Ledger.MetaNarrativeFlags.Add(flag);
            SaveManager.MarkDirty();
            return def.WeaknessHints[i - 1];
        }
        return null; // dossier already full
    }
}
