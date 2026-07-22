using System.Collections.Generic;

// ============================================================
// NarrativeEncounterData.cs
//
// Purpose:        Narrative encounter model — title, body text,
//                 terrain/region filter tags, list of player
//                 choices each with their own outcome deltas
//                 (gold, HP, steps) and Phase-3 gating fields.
// Layer:          Data
// Collaborators:  NarrativeEncounterLoader.cs (JSON parser),
//                 NarrativeEncounterPanel.cs (UI display),
//                 EncounterRouter.cs
// See:            README §4.3 (Adding a Narrative Encounter)
// ============================================================

/// <summary>One narrative encounter: title, body text, optional terrain/region filters, and the list of player choices. Loaded from Data/Encounters/*.json.</summary>
public class NarrativeEncounterData
{
    // ── Identity ────────────────────────────────────────────────────────
    public string Id = "";
    public string Title = "";
    public string Body = "";

    // ── Context filters ──────────────────────────────────────────────────
    // Empty list = matches any. Non-empty = only matches listed values.
    public List<string> TerrainTags = new();
    public List<string> RegionTags = new();

    // ── Encounter-level visibility gate (Step 5, quest spec §8) ─────────
    /// <summary>When non-empty, this encounter is only eligible for selection
    /// when the flag is set (checked via GuildSaveData.HasFlag, which reads
    /// BOTH timeline WorldFlags and permanent MetaNarrativeFlags). Used by
    /// ripple encounters to gate on qe_* trigger flags from the event shim,
    /// and by echo encounters to gate on echo_*_eligible seeder flags.
    /// Choice-level RequiredFlag still applies independently.</summary>
    public string RequiredFlag = "";

    // ── Resolution encounters (Step 9, quest_hooks_compendium §7) ───────
    /// <summary>When non-empty, this encounter is a RESOLUTION audience with
    /// the named archmage. Choices carrying a ResolutionKind are gated against
    /// CampaignState.ResolutionOptions(ArchmageId) by the panel (unite/coerce
    /// shown-but-disabled with the reason when out of reach; overthrow always
    /// pressable).</summary>
    public string ArchmageId = "";

    // ── Choices ──────────────────────────────────────────────────────────
    public List<EncounterChoice> Choices = new();
}

/// <summary>
/// A single choice option within a narrative encounter.
/// </summary>
public class EncounterChoice
{
    public string Label = "";
    public string ResultText = "";

    // Outcomes (positive or negative)
    public int GoldDelta = 0;
    public int HPDelta = 0;
    public int StepDelta = 0;

    /// <summary>S4 (overworld_spell_system §11): an overworld spell id this
    /// choice teaches — the authored half of the lore-POI acquisition path
    /// (the other half is ExpeditionManager's terrain-flavored bonus roll,
    /// which only runs when this is empty). Already-known spells no-op.</summary>
    public string SpellReward = "";

    // Phase 3+ tracking
    public List<string> SetFlags = new();

    /// <summary>Permanent (cross-cycle) story flags this choice sets — written to
    /// EternalLedger.MetaNarrativeFlags, not the timeline-scoped WorldFlags. Used
    /// for fragment-arc milestones and other progress that must survive a cycle
    /// reset (convergence.docx). Read by quest objectives and choice gating.</summary>
    public List<string> SetMetaFlags = new();

    /// <summary>Fragment key: when set, choosing this option launches a Boss-tier
    /// guardian combat instead of resolving normally; winning sets
    /// &lt;key&gt;_trial_passed (the fragment-setpiece climax).</summary>
    public string LaunchGuardian = "";

    // Phase 3+ gating
    public string RequiredFlag = "";
    public string RequiredSchool = "";
    public int RequiredGold = 0;

    /// <summary>Step 9: "unite" | "coerce" | "overthrow" (empty for ordinary
    /// choices). Only meaningful on encounters with a non-empty ArchmageId.
    /// Unite/Coerce resolve the archmage's disposition directly; Overthrow
    /// launches the archmage boss combat (disposition set on the win).</summary>
    public string ResolutionKind = "";

    // ── Tranche 2 reward verbs (encounter_outcome_expansion §Tranche 2) ──
    // Levers that create lasting attachment. All default-empty/0 so every
    // pre-existing encounter JSON still deserializes unchanged.
    /// <summary>Item id (Data/Items) granted to the guild armory.</summary>
    public string ItemReward = "";
    /// <summary>Companion id (Data/Companions) recruited for this timeline.</summary>
    public string CompanionUnlock = "";
    /// <summary>Faction id whose reputation this choice shifts (with ReputationAmount).</summary>
    public string ReputationFactionId = "";
    /// <summary>Signed reputation delta applied to ReputationFactionId.</summary>
    public int ReputationAmount = 0;
    /// <summary>Lore id recorded permanently in the Hall of Records (Records tab).</summary>
    public string LoreId = "";
}