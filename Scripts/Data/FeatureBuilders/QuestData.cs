using System.Collections.Generic;

// ============================================================
// QuestData.cs
//
// Purpose:        Quest/journal data model. A quest is a small
//                 authored definition whose objectives are
//                 PREDICATES over existing save state (WorldFlags,
//                 MetaNarrativeFlags, lore, counters). Status is
//                 computed live by QuestTracker — no separate
//                 quest-state to persist or keep in sync — with a
//                 permanent completion record for cross-cycle arcs.
// Layer:          Data
// Collaborators:  QuestLoader.cs (JSON), QuestTracker.cs (eval),
//                 CampusScreen.cs (Quests tab), EternalLedger.cs
//                 (CompletedQuestIds), the fragment/Convergence
//                 spine (convergence.docx).
// ============================================================

/// <summary>One objective within a quest. The first non-empty condition field
/// decides how it is satisfied: a Flag set, a Lore entry unlocked, or a named
/// Counter reaching CounterTarget.</summary>
public class QuestObjective
{
    public string Text = "";
    public string Flag = "";          // WorldFlags / MetaNarrativeFlags / CompletedEvents
    public string Lore = "";          // UnlockedLoreEntries
    public string Counter = "";       // named counter (QuestTracker.CountFor)
    public int CounterTarget = 0;
}

/// <summary>An authored quest. Loaded from Data/Quests/*.json.</summary>
public class QuestDefinition
{
    public string Id = "";
    public string Title = "";
    public string Summary = "";
    /// <summary>Grouping/lens: "Story" | "Expansion" | "Fragments".</summary>
    public string Category = "Story";
    /// <summary>True = cross-cycle arc (spine/fragments); completion is stamped
    /// permanently. False = per-timeline; status resets each cycle.</summary>
    public bool Permanent = false;

    // Visibility gate — a rumored quest stays hidden until met.
    public string RequiredLore = "";
    public string RequiredFlag = "";
    public string RequiredQuest = "";  // another quest id that must be complete

    public List<QuestObjective> Objectives = new();

    // One-time completion rewards (Permanent quests only).
    public string RewardLore = "";
    public string RewardFlag = "";
}
