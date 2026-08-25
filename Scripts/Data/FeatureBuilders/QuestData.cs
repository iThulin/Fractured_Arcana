using System.Collections.Generic;

// ============================================================
// QuestData.cs
//
// Purpose:        Quest/journal data model. A quest is a small
//                 authored definition whose objectives are
//                 PREDICATES over existing save state (WorldFlags,
//                 MetaNarrativeFlags, lore, counters). Status is
//                 computed live by QuestTracker (no separate
//                 quest-state to persist or keep in sync) with a
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

    /// <summary>Persistence layer: "Eternal" or "Timeline" (quest spec §2/§7).
    /// JSON may set this explicitly; when empty, derived from <see cref="Permanent"/>
    /// at load time: Permanent → Eternal, else Timeline.</summary>
    public string Layer = "";

    /// <summary>Resolve the effective layer. Honors an explicit JSON value;
    /// falls back to Permanent → "Eternal", else "Timeline".</summary>
    public string EffectiveLayer => !string.IsNullOrEmpty(Layer) ? Layer
        : Permanent ? "Eternal" : "Timeline";

    // Visibility gate: a rumored quest stays hidden until met.
    public string RequiredLore = "";
    public string RequiredFlag = "";
    public string RequiredQuest = "";  // another quest id that must be complete

    /// <summary>Counter-based visibility gate, same counter vocabulary as
    /// <see cref="QuestObjective.Counter"/> ("mastery:Necromancer",
    /// "deed:combat_won", "flags:deep_"). The quest stays Locked until the
    /// counter reaches <see cref="RequiredCounterTarget"/>. Added for the
    /// Fluency track, where the natural gate is "you have earned at least one
    /// point in this school" and no flag exists to express that. Without it
    /// all eight school quests would sit permanently visible at 0 progress.</summary>
    public string RequiredCounter = "";
    public int RequiredCounterTarget = 1;

    public List<QuestObjective> Objectives = new();

    // One-time completion rewards (Permanent quests only).
    public string RewardLore = "";
    public string RewardFlag = "";
}
