using System.Collections.Generic;

// ============================================================
// EternalLedger.cs
//
// Purpose:        Tier 3 of the three-tier save schema — the
//                 loom. Everything that exists outside the
//                 timelines: the deed ledger and school mastery,
//                 anchored essence, the campus (Eiran's draft),
//                 the beacon, loop history, renown anchors,
//                 meta-narrative flags, and unlocked knowledge.
//                 The ONLY permanent-loss vector in the game —
//                 SaveManager writes it atomically with a .bak.
//                 Serialized to user://saves/slot_N_ledger.json.
// Layer:          Data
// Collaborators:  GuildSaveData.cs (in-memory envelope + shims),
//                 CycleState.cs (tier 2 sibling),
//                 SaveManager.cs (atomic dual-file IO),
//                 DeedLedgerService (Phase 3 — income hooks),
//                 AssaultDirector (Phase 4 — beacon reader)
// See:            open_world_refactor_v1.docx §10 — Save Schema
// Tier rule:      If the loom remembers it, it lives here.
//                 Nothing in this file may grant raw combat
//                 power — breadth and knowledge only.
// ============================================================

/// <summary>Per-school mastery progress. The progression spine.</summary>
public class SchoolMasteryTrack
{
    /// <summary>Accumulated mastery points (deed-driven, outcome-blind).</summary>
    public int Points = 0;

    /// <summary>Milestone ids reached on this track (gates scar work, exotic builds, lore).</summary>
    public List<string> MilestoneIds = new();
}

/// <summary>
/// One completed (or failed, or abandoned) cycle, summarized for
/// the loop history. Kassian's adaptive behavior reads these.
/// </summary>
public class LoopRecord
{
    public int CycleNumber = 0;
    public string School = "";

    /// <summary>"Victory", "ConvergenceDefeat", "CorruptionLoss", or "Abandoned".</summary>
    public string Outcome = "";

    /// <summary>Convergence resolution when victorious: "Restoration", "Harness", "Synthesis", or "".</summary>
    public string ResolutionPath = "";

    public int LunationsElapsed = 0;
    public int RunsCompleted = 0;
    public int EssenceEarned = 0;

    /// <summary>archmageId → final disposition string, for memory threads and adaptation.</summary>
    public Dictionary<string, string> FinalDispositions = new();
}

/// <summary>
/// One anchored relationship milestone — recognition without memory.
/// Manifests in later cycles as starting offsets and memory threads.
/// </summary>
public class RenownAnchor
{
    /// <summary>Archmage, faction, or companion id the anchor refers to.</summary>
    public string SubjectId = "";

    /// <summary>What was anchored: "Allied", "ArcComplete", "FiveStarDeal", etc.</summary>
    public string MilestoneId = "";

    /// <summary>Cycle in which the milestone was anchored.</summary>
    public int CycleAnchored = 0;
}

/// <summary>
/// The eternal ledger. Created once per guild slot; survives every
/// cycle reset; the campus, the economy, and the meta-narrative all
/// live here. Carries the guild identity (the guild, like the campus,
/// exists outside the timelines).
/// </summary>
public class EternalLedger
{
    // ── Meta (the guild is eternal) ──────────────────────────────────────
    public int SaveVersion = SaveManager.CURRENT_VERSION;
    public string GuildName = "New Guild";
    public string CreatedAt = "";
    public string LastPlayedAt = "";

    // ── Anchored essence (the economy) ───────────────────────────────────
    /// <summary>Current spendable balance.</summary>
    public int EssenceBalance = 0;

    /// <summary>Lifetime earned, all cycles. Display + beacon input.</summary>
    public int LifetimeEssenceEarned = 0;

    // ── The deed ledger ──────────────────────────────────────────────────
    /// <summary>
    /// deedType → count, accumulated across all cycles, outcome-blind.
    /// Individual deed events deposit essence at the moment they occur
    /// (DeedLedgerService, Phase 3); this dictionary is the aggregate
    /// record that milestones and achievements read.
    /// </summary>
    public Dictionary<string, int> DeedCounts = new();

    /// <summary>school name → mastery track. The "approach to all magic" record.</summary>
    public Dictionary<string, SchoolMasteryTrack> SchoolMastery = new();

    // ── The campus (Eiran's draft) ───────────────────────────────────────
    /// <summary>
    /// Campus buildings. Phase 0: the existing flat tier list, relocated
    /// here because the campus exists outside time. Phase 3 expands this
    /// into the spatial model (districts, foundation tiles, scars,
    /// integrity, work orders) — additively, on this same object.
    /// </summary>
    public List<BuildingSaveData> Buildings = new();

    // ── The beacon (Phase 4 reader) ──────────────────────────────────────
    /// <summary>Kassian's perception of total anchored essence.</summary>
    public float BeaconValue = 0f;

    /// <summary>How many beacon thresholds have been crossed (eclipse pacing).</summary>
    public int BeaconThresholdsCrossed = 0;

    // ── Loop history ─────────────────────────────────────────────────────
    public List<LoopRecord> LoopHistory = new();

    // ── Renown and meta-narrative ────────────────────────────────────────
    public List<RenownAnchor> RenownAnchors = new();

    /// <summary>Cross-loop story flags. Placement-agnostic by design rule.</summary>
    public List<string> MetaNarrativeFlags = new();

    // ── Knowledge (breadth, never power) ─────────────────────────────────
    /// <summary>
    /// Card blueprints the player has discovered, across all timelines.
    /// Knowing a card is knowledge; owning a copy is tier-2 power.
    /// </summary>
    public List<string> UnlockedCardBlueprintIds = new();

    public List<string> UnlockedLoreEntries = new();

    // ── The honored dead ─────────────────────────────────────────────────
    /// <summary>
    /// Every unit death, every timeline. The loom remembers the dead even
    /// when their timelines no longer exist — the Ossuary (a campus
    /// building, outside time) draws on all of them. Append-only.
    /// </summary>
    public List<HonoredDeadRecord> HonoredDead = new();

    // ── Convenience (not serialized) ─────────────────────────────────────
    /// <summary>Total cycles recorded (completed by any outcome).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int CyclesCompleted => LoopHistory.Count;

    /// <summary>Returns the mastery track for a school, creating it if absent.</summary>
    public SchoolMasteryTrack GetMastery(string school)
    {
        if (!SchoolMastery.TryGetValue(school, out var track))
        {
            track = new SchoolMasteryTrack();
            SchoolMastery[school] = track;
        }
        return track;
    }

    /// <summary>Increment a deed count and return the new total.</summary>
    public int RecordDeed(string deedType, int count = 1)
    {
        DeedCounts.TryGetValue(deedType, out int current);
        current += count;
        DeedCounts[deedType] = current;
        return current;
    }
}
