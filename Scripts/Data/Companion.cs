using System.Collections.Generic;

/// <summary>
/// Runtime + save-state data for a single companion.
/// Companions persist across runs; their state lives in GuildSaveData.
/// </summary>
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

    // ── Combat contribution ─────────────────────────────────────────────
    // Cards this companion adds to the active wizard's deck during combat.
    // Phase 2: simple list. Phase 3+: arc rewards add unique cards.
    public List<string> ContributedCardIds = new();

    // ── Recruitment ──────────────────────────────────────────────────────
    public int RecruitmentCost = 100;    // gold
    public string UnlockCondition = "";  // human-readable; gameplay logic in Phase 3
}