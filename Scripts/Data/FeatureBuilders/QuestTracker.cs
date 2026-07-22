using System.Collections.Generic;

// ============================================================
// QuestTracker.cs
//
// Purpose:        Evaluate quest status/progress as a live
//                 projection over existing save state, so the
//                 Quests tab needs no separate quest-state store.
//                 Permanent (cross-cycle) quests get a durable
//                 completion stamp in EternalLedger.CompletedQuestIds
//                 so their arcs stay done after a cycle reset; cycle
//                 quests derive completion live and reset naturally.
// Layer:          System (static, stateless)
// Collaborators:  QuestDefinition/QuestLoader, GuildSaveData,
//                 EternalLedger.CompletedQuestIds, SaveManager.
//
// Counter families (quest spec §8, step 4):
//   "deed:<type>"   → Ledger.DeedCounts[type]  (cross-cycle total)
//   "flags:<prefix>"→ count of flags matching prefix across
//                     WorldFlags + MetaNarrativeFlags
//   Plus the original named counters for backwards compat.
// ============================================================

public enum QuestStatus { Locked, Active, Complete }

/// <summary>Live evaluation of quests against save state.</summary>
public static class QuestTracker
{
    public static QuestStatus StatusOf(QuestDefinition q, GuildSaveData save)
    {
        if (q == null || save == null) return QuestStatus.Locked;
        if (!UnlockMet(q, save)) return QuestStatus.Locked;
        return IsComplete(q, save) ? QuestStatus.Complete : QuestStatus.Active;
    }

    public static bool IsComplete(QuestDefinition q, GuildSaveData save)
    {
        if (q.Permanent && (save.Ledger?.CompletedQuestIds?.Contains(q.Id) ?? false))
            return true;
        return AllObjectivesDone(q, save);
    }

    public static bool ObjectiveDone(QuestObjective o, GuildSaveData save)
    {
        if (!string.IsNullOrEmpty(o.Flag)) return FlagSet(save, o.Flag);
        if (!string.IsNullOrEmpty(o.Lore)) return LoreHas(save, o.Lore);
        if (!string.IsNullOrEmpty(o.Counter)) return CountFor(o.Counter, save) >= o.CounterTarget;
        return false;
    }

    public static (int have, int need) CounterProgress(QuestObjective o, GuildSaveData save)
        => (CountFor(o.Counter, save), o.CounterTarget);

    // ── internals ────────────────────────────────────────────────────────
    private static bool AllObjectivesDone(QuestDefinition q, GuildSaveData save)
    {
        if (q.Objectives == null || q.Objectives.Count == 0) return false;
        foreach (var o in q.Objectives)
            if (!ObjectiveDone(o, save)) return false;
        return true;
    }

    private static bool UnlockMet(QuestDefinition q, GuildSaveData save)
    {
        if (!string.IsNullOrEmpty(q.RequiredLore) && !LoreHas(save, q.RequiredLore)) return false;
        if (!string.IsNullOrEmpty(q.RequiredFlag) && !FlagSet(save, q.RequiredFlag)) return false;
        if (!string.IsNullOrEmpty(q.RequiredQuest) &&
            !(save.Ledger?.CompletedQuestIds?.Contains(q.RequiredQuest) ?? false)) return false;
        return true;
    }

    private static bool FlagSet(GuildSaveData save, string flag)
        => save.HasFlag(flag)
           || (save.Ledger?.MetaNarrativeFlags?.Contains(flag) ?? false)
           || (save.CompletedEvents?.Contains(flag) ?? false);

    private static bool LoreHas(GuildSaveData save, string lore)
        => save.UnlockedLoreEntries?.Contains(lore) ?? false;

    private static int CountFor(string counter, GuildSaveData save)
    {
        if (string.IsNullOrEmpty(counter)) return 0;

        // ── Generic family: deed:<type> ─────────────────────────────────
        // Reads the cross-cycle deed tally from EternalLedger.DeedCounts.
        // Quest JSON example:  { "counter": "deed:combat_won", "counterTarget": 5 }
        if (counter.StartsWith("deed:"))
        {
            string deedType = counter.Substring(5);
            if (string.IsNullOrEmpty(deedType)) return 0;
            var deeds = save.Ledger?.DeedCounts;
            if (deeds != null && deeds.TryGetValue(deedType, out int val))
                return val;
            return 0;
        }

        // ── Generic family: flags:<prefix> ──────────────────────────────
        // Counts flags matching the prefix across BOTH flag stores
        // (timeline WorldFlags + eternal MetaNarrativeFlags), de-duped.
        // Quest JSON example:  { "counter": "flags:dossier_hint_", "counterTarget": 3 }
        if (counter.StartsWith("flags:"))
        {
            string prefix = counter.Substring(6);
            if (string.IsNullOrEmpty(prefix)) return 0;
            int n = 0;
            var wf = save.Cycle?.WorldFlags;
            if (wf != null)
                foreach (var f in wf)
                    if (f.StartsWith(prefix)) n++;
            var mf = save.Ledger?.MetaNarrativeFlags;
            if (mf != null)
                foreach (var f in mf)
                    if (f.StartsWith(prefix)) n++;
            return n;
        }

        // ── Named counters (original, backwards-compatible) ─────────────
        switch (counter)
        {
            case "outposts_secured":
            {
                int n = 0;
                var sps = save.Cycle?.World?.StagingPoints;
                if (sps != null) foreach (var sp in sps) if (sp.Source == "Secured") n++;
                return n;
            }
            case "fragments_collected":
            {
                int f = 0;
                var mf = save.Ledger?.MetaNarrativeFlags;
                if (mf != null) foreach (var s in mf)
                    if (s.StartsWith("fragment_") && s.EndsWith("_collected")) f++;
                return f;
            }
            case "lore_count":
                return save.UnlockedLoreEntries?.Count ?? 0;
            case "companions_recruited":
            {
                int c = 0;
                var comps = save.Cycle?.Companions;
                if (comps != null) foreach (var cm in comps) if (cm.IsRecruited) c++;
                return c;
            }
            default:
                return 0;
        }
    }

    /// <summary>Stamp newly-completed PERMANENT quests into the ledger and fire
    /// their one-time rewards. Call on campus load / whenever the tab refreshes.
    /// Cycle quests are never stamped, so a new timeline resets them.</summary>
    public static void SyncCompletions(GuildSaveData save)
    {
        if (save?.Ledger == null) return;
        bool dirty = false;
        foreach (var q in QuestLoader.LoadAll())
        {
            if (!q.Permanent) continue;
            if (save.Ledger.CompletedQuestIds.Contains(q.Id)) continue;
            if (!UnlockMet(q, save)) continue;
            if (!AllObjectivesDone(q, save)) continue;

            save.Ledger.CompletedQuestIds.Add(q.Id);
            if (!string.IsNullOrEmpty(q.RewardLore) && !LoreHas(save, q.RewardLore))
                save.UnlockedLoreEntries.Add(q.RewardLore);
            if (!string.IsNullOrEmpty(q.RewardFlag) &&
                !save.Ledger.MetaNarrativeFlags.Contains(q.RewardFlag))
                save.Ledger.MetaNarrativeFlags.Add(q.RewardFlag);
            dirty = true;
        }
        if (dirty) SaveManager.MarkDirty();
    }
}
