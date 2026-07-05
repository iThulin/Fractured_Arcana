using System.Collections.Generic;

// ============================================================
// CompanionDefinition.cs
//
// Purpose:        Companion data model. Holds identity, school,
//                 recruitment/loyalty/arc state, unit class
//                 (Arcane / Fighter / Ranger / None levy),
//                 base combat stats, trained stances, and the
//                 cards contributed to the active wizard's deck.
//                 Persists across runs in GuildSaveData.
// Layer:          Data
// Collaborators:  CompanionLoader.cs (JSON parser),
//                 CompanionRoster.cs (collection wrapper),
//                 GuildSaveData.cs (persistence),
//                 StanceDefinition.cs (ActiveStance), Unit.cs
// See:            README §4.5 (Adding a Companion)
// ============================================================

/// <summary>One companion's full data: identity, recruitment status, arc progression, unit class, base combat stats, trained stances, and run-scoped runtime state. Companions persist across runs; arc and loyalty progression lives here.</summary>
public class Companion
{
    // ── Identity ────────────────────────────────────────────────────────
    public string Id = "";              // unique key, e.g. "elara_stormcaller"
    public string Name = "";
    public string School = "Elementalist";
    public string PersonalityTrait = ""; // flavor: "Reckless", "Stoic", "Curious"
    public string Backstory = "";

    // ── State ───────────────────────────────────────────────────────────
    public bool IsRecruited = false;
    public bool IsAvailable = false;     // is recruitment unlocked?
    public bool IsPermadead = false;
    public int Loyalty = 50;             // 0-100
    public int ArcStage = 0;             // 0 = not started, 1-3 in progress, 4 complete

    // ── Unit class ───────────────────────────────────────────────────────
    // "Arcane" = wizard-type (card-based, has mana)
    // "Fighter" = melee martial
    // "Ranger" = ranged martial
    // "None" = unclassed levy (default for new companions)
    public string UnitClass = "None";

    // ── Combat stats (base values at levy tier) ──────────────────────────
    // Wizards: these are ignored — wizard stats come from PlayerSession/school
    // Martials: these are the starting levy stats, boosted by Training Grounds
    public int BaseHP = 12;
    public int BaseSpeed = 2;
    public int BaseArmor = 0;
    public int BaseAttackDamage = 3;
    public int BaseAttackRange = 1;
    public int BaseMana = 0;   // always 0 for martials

    // ── Martial progression (saved per companion) ────────────────────────
    // Which stances this companion has been trained in at the campus.
    // Populated by the Training tab — not from JSON templates.
    [System.Text.Json.Serialization.JsonPropertyName("availableStanceIds")]
    public List<string> TrainedStanceIds = new();

    // AP pool — set by Training Grounds tier at run start
    // Stored here so the UI can show it between runs
    public int BaseActionPoints = 3; // levy default

    // ── Runtime state (not in JSON — set during combat) ──────────────────
    // These are not serialized; they're rebuilt each combat from save data.
    [System.Text.Json.Serialization.JsonIgnore]
    public StanceDefinition ActiveStance = null;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAttackedThisCombat = false; // for Ambush tracking

    // ── Combat contribution ─────────────────────────────────────────────
    // Cards this companion adds to the active wizard's deck during combat.
    // Phase 2: simple list. Phase 3+: arc rewards add unique cards.
    public List<string> ContributedCardIds = new();

    // ── Recruitment ──────────────────────────────────────────────────────
    public int RecruitmentCost = 100;    // gold
    public string UnlockCondition = "";  // human-readable; gameplay logic in Phase 3
}