using System.Collections.Generic;

// ============================================================
// CompanionArcData.cs
//
// Purpose:        JSON-driven companion arc definitions. Each
//                 companion has a multi-stage arc (3–4 stages),
//                 optional remembrance branches for returning
//                 players, and Hall anchoring metadata. Maximally
//                 data-driven: stages, encounters, flags, and
//                 gating are all authored in JSON, not code.
// Layer:          Data
// Collaborators:  CompanionArcLoader.cs (JSON parser),
//                 CompanionArcTracker.cs (stage derivation),
//                 Companion.cs (runtime state),
//                 CompanionRoster.cs (party management),
//                 QuestLogView.cs (mission prompts)
// See:            quest_hooks_compendium_v1.md §3
// ============================================================

/// <summary>One stage of a companion's arc — an encounter gated by flags,
/// with completion writing new flags. Loaded from Data/Companions/Arcs/*.json.</summary>
public class CompanionArcStage
{
    /// <summary>Stage number (1-based, matching ArcStage on Companion).</summary>
    public int Stage = 0;

    /// <summary>Display title for the quest log ("Prove the Campus Exists").</summary>
    public string Title = "";

    /// <summary>Brief summary shown in the quest log when this stage is active.</summary>
    public string Summary = "";

    /// <summary>Encounter id (Data/Encounters) for this stage's narrative beat.</summary>
    public string EncounterId = "";

    /// <summary>When true, the companion must be in the active expedition party
    /// for this stage's encounter to appear. When false, the encounter can
    /// trigger anywhere (campus, region POI, etc.).</summary>
    public bool RequiresParty = false;

    /// <summary>Where this stage triggers: "expedition" (overworld encounter pool),
    /// "campus" (campus landmark interaction), or "any" (either).</summary>
    public string Location = "expedition";

    /// <summary>Timeline flag set on completion (e.g. "arc_serren_1"). Gates
    /// the next stage and is read by CompanionArcTracker to derive ArcStage.</summary>
    public string CompletionFlag = "";

    /// <summary>Eternal flag set on completion (e.g. "remember_serren_1"). Persists
    /// across cycles for remembrance branches on future re-runs.</summary>
    public string RemembranceFlag = "";

    /// <summary>Alternative encounter id used when the remembrance flag from a
    /// PRIOR cycle is already set — the foreknowledge variant. Empty = no
    /// remembrance branch for this stage (use the normal encounter).</summary>
    public string RemembranceEncounterId = "";

    /// <summary>Optional flag that must be set BEFORE this stage is available
    /// (beyond the implicit "previous stage complete" gate). Used for stages
    /// that require external progress (e.g. a fragment collected).</summary>
    public string RequiredFlag = "";

    /// <summary>Optional meta-flags set on completion (beyond RemembranceFlag).
    /// Used for stages that unlock cross-system effects.</summary>
    public List<string> SetMetaFlags = new();

    /// <summary>Loyalty delta applied to the companion on stage completion.
    /// Positive = trust gained, negative = tension. 0 = no change.</summary>
    public int LoyaltyDelta = 0;
}

/// <summary>Complete arc definition for one companion. Loaded from JSON;
/// the arc tracker reads this + save flags to derive current stage and
/// surface quest-log entries.</summary>
public class CompanionArcData
{
    // ── Identity ────────────────────────────────────────────────────────
    /// <summary>Must match Companion.Id in the companion roster.</summary>
    public string CompanionId = "";

    /// <summary>Display name for the arc in the quest log ("The One Who Wasn't There").</summary>
    public string ArcName = "";

    /// <summary>One-line arc summary for the quest log header.</summary>
    public string ArcSummary = "";

    // ── Recruitment ─────────────────────────────────────────────────────
    /// <summary>Encounter id for the recruitment event. Empty = companion is
    /// available from cycle start (e.g. the Examiner).</summary>
    public string RecruitEncounterId = "";

    /// <summary>Flag set when the companion is recruited. The arc tracker uses
    /// this to know the arc has begun.</summary>
    public string RecruitFlag = "";

    /// <summary>Where the recruitment encounter surfaces: "expedition", "campus",
    /// or "any".</summary>
    public string RecruitLocation = "expedition";

    /// <summary>Optional flag required before the recruitment encounter appears.
    /// Used for companions gated on story progress (e.g. Mother Ashwell needs
    /// echo_standing_debt_eligible).</summary>
    public string RecruitRequiredFlag = "";

    // ── Arc stages ──────────────────────────────────────────────────────
    /// <summary>Ordered list of arc stages (1-based). The tracker advances
    /// ArcStage when it detects the completion flag for the current stage.</summary>
    public List<CompanionArcStage> Stages = new();

    // ── Hall anchoring (quest spec §5c) ─────────────────────────────────
    /// <summary>Whether this companion can be anchored in the Remembrancer's Hall.
    /// False for the Examiner ("already inside the Second").</summary>
    public bool HallEligible = true;

    /// <summary>Text shown when the Hall option is greyed out (e.g. "They never left.").
    /// Only used when HallEligible is false.</summary>
    public string HallBlockedText = "";

    /// <summary>Eternal flag set when this companion is anchored in the Hall.
    /// Persists across all future cycles.</summary>
    public string HallAnchorFlag = "";

    // ── Arc completion ──────────────────────────────────────────────────
    /// <summary>Flag set when all stages are complete. Used by quest gating
    /// and the Convergence staging.</summary>
    public string ArcCompleteFlag = "";

    /// <summary>Eternal flag set on arc completion (for cross-cycle tracking).</summary>
    public string ArcCompleteMetaFlag = "";
}
