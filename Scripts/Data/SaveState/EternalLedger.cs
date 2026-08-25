using System.Collections.Generic;

// ============================================================
// EternalLedger.cs
//
// Purpose:        Tier 3 of the three-tier save schema: the
//                 loom. Everything that exists outside the
//                 timelines: the deed ledger and school mastery,
//                 anchored essence, the campus (Eiran's draft),
//                 the beacon, loop history, renown anchors,
//                 meta-narrative flags, and unlocked knowledge.
//                 The ONLY permanent-loss vector in the game.
//                 SaveManager writes it atomically with a .bak.
//                 Serialized to user://saves/slot_N_ledger.json.
// Layer:          Data
// Collaborators:  GuildSaveData.cs (in-memory envelope + shims),
//                 CycleState.cs (tier 2 sibling),
//                 SaveManager.cs (atomic dual-file IO),
//                 DeedLedgerService (Phase 3, income hooks),
//                 AssaultDirector (Phase 4, beacon reader)
// See:            open_world_refactor_v1.docx §10 (Save Schema)
// Tier rule:      If the loom remembers it, it lives here.
//                 The loom MAY grant permanent, raw combat
//                 power: the campus is an intentional power-
//                 expansion engine (roguelite meta-progression).
//                 Superseded the old "breadth/knowledge only"
//                 rule. See claude/progression_persistence_model_v1.md.
// ============================================================

/// <summary>
/// Per-BLUEPRINT card mastery. Permanent, and deliberately so.
///
/// Cast counts and upgrade tiers used to live only on <see cref="OwnedCard"/>,
/// which hangs off CycleState.PlayerDeck and is destroyed every cycle. That meant
/// a player re-earned the right to upgrade cards they had already mastered twice,
/// and every tier they had ever bought evaporated with the timeline. Knowing a
/// card well is knowledge, and it belongs in the loom
/// (progression_persistence_model_v1 §2).
///
/// The per-copy OwnedCard.CastCount still exists and still increments; this is the
/// authoritative record that survives, and the one the upgrade gate reads.
/// </summary>
/// <summary>
/// An in-flight Library research commission: the deterministic DISCOVERY verb
/// (progression_card_acquisition_v1 §8, "Library research → the pity timer").
/// The player names a locked blueprint and pays gold up front; the card is
/// unlocked once <see cref="LunationsRemaining"/> ticks to zero. Distinct from
/// minting, which COPIES an already-discovered card for splinters instantly.
///
/// Permanent by design: a commission lives on the ledger, so it keeps counting
/// down across a cycle reseed (the calendar resets to lunation 1 each cycle, so
/// the remaining count is stored absolutely, not as a due-lunation).
/// </summary>
public class CardCommission
{
    /// <summary>Blueprint id the research will unlock on completion.</summary>
    public string BlueprintId = "";

    /// <summary>Lunations still to elapse. Decremented once per lunation tick;
    /// the card unlocks when this reaches 0. Stored as a remaining count rather
    /// than an absolute due-lunation because the calendar resets every cycle.</summary>
    public int LunationsRemaining = 0;

    /// <summary>Gold charged when the commission was placed. Audit/display only.
    /// The payment already happened; this is never refunded on settlement.</summary>
    public int GoldPaid = 0;
}

public class CardMasteryRecord
{
    /// <summary>Lifetime casts of this blueprint, all copies, all timelines.</summary>
    public int Casts = 0;

    /// <summary>Highest top-half tier ever reached on any copy.</summary>
    public int BestTopTier = 0;

    /// <summary>Highest bottom-half tier ever reached on any copy.</summary>
    public int BestBotTier = 0;

    /// <summary>Upgrade points spent to reach that best state. The mint reproduces it.</summary>
    public int BestPointsSpent = 0;
}

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
/// One anchored relationship milestone: recognition without memory.
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
/// A timeline quest that was still active (unlocked but incomplete)
/// when the cycle unmaked. Archived to <see cref="EternalLedger.UnfinishedBusiness"/>
/// so the quest log can render the cost of every reset (spec §7).
/// </summary>
public class UnfinishedQuestRecord
{
    public string QuestId = "";
    public string Title = "";
    public string Summary = "";

    /// <summary>How many objectives were completed before the unmake.</summary>
    public int ObjectivesDone = 0;

    /// <summary>Total objective count on the quest.</summary>
    public int ObjectivesTotal = 0;

    /// <summary>Which cycle this quest was abandoned in.</summary>
    public int CycleNumber = 0;

    /// <summary>Campaign year within that cycle (1 = first timeline).</summary>
    public int CampaignYear = 1;

    /// <summary>School the player was running when the cycle ended.</summary>
    public string School = "";
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

    // ── Founding scenario (the guild's difficulty, chosen once) ──────────
    /// <summary>The founding scenario (a curated seed plus difficulty levers)
    /// chosen when the guild was founded. GUILD-LEVEL: re-applied to every cycle's
    /// world generation (spec §3.3), so difficulty is a property of the guild, not
    /// of one timeline. Null on pre-feature saves; SaveManager backfills the
    /// Standard default on load, and EnsureCycleWorld coalesces null → default too.
    /// Presentation fields (name/blurb) ride along so the guild can display what it
    /// was founded on.</summary>
    public StartScenario FoundingScenario = null;

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
    /// integrity, work orders), additively, on this same object.
    /// </summary>
    public List<BuildingSaveData> Buildings = new();

    /// <summary>
    /// The campus hex map's ground layout (cosmetic dressing + buildable
    /// slots). See CampusMapSaveData.cs. Building PLACEMENT lives on each
    /// BuildingSaveData (Q/R/IsPlaced), not here; this is ground only.
    /// First slice of the "spatial model" called out above. Districts,
    /// scars, integrity, and work orders are still open. When they land,
    /// they belong either as new fields on CampusTileSaveData (per-tile:
    /// scar, integrity) or as new top-level fields here (districts,
    /// work-order queue), added the same additive way Buildings was.
    /// </summary>
    public CampusMapSaveData CampusMap = new();

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

    /// <summary>
    /// In-flight Library research commissions (the §8 pity-timer). Each names a
    /// locked blueprint and a remaining lunation count; on completion the id is
    /// moved into <see cref="UnlockedCardBlueprintIds"/> and the entry removed.
    /// Settled by CardCommissionService on the lunation tick. Permanent so a
    /// commission survives the cycle reseed and keeps counting down.
    /// </summary>
    public List<CardCommission> CardCommissions = new();

    // ── Regalia (the ONE reseed exception) ───────────────────────────────
    /// <summary>
    /// Named artifacts owned permanently, granted at milestones (fragments,
    /// archmage confrontations, companion arc capstones), never drafted.
    /// Legendary draft weight is 0; these are the only route to a Legendary.
    ///
    /// Regalia are the single sanctioned exception to the deck reseed: up to
    /// K of them (RegaliaService.MaxCarry) ride into a fresh cycle alongside
    /// the 10-card starter. The fiction pays for it: the fragments are
    /// trans-temporal, so a card cut from one was never in the timeline that
    /// resets (narrative_frame_intro_finale_v1 R5).
    ///
    /// AMENDS progression_persistence_model_v1.md §5 ("run deck → starter"),
    /// which now reads "→ starter, plus up to K Regalia". User-authorized
    /// 2026-08-04. See docs/progression_card_acquisition_v1.md §6.
    /// Ownership lives here; the per-cycle SELECTION lives on
    /// CycleState.CarriedRegaliaIds and is wiped with the timeline.
    /// </summary>
    public List<string> RegaliaBlueprintIds = new();

    /// <summary>
    /// blueprintId → lifetime casts and best tiers reached. Permanent: the upgrade
    /// gate reads this, so mastery is never re-ground after a reseed, and minting
    /// reproduces a card at the tier you have already paid for.
    /// See docs/progression_card_acquisition_v1_2.md.
    /// </summary>
    public Dictionary<string, CardMasteryRecord> CardMastery = new();

    /// <summary>
    /// Every overworld spell the guild has ever learned, across all timelines.
    ///
    /// GrimoireState.KnownSpellIds is the per-cycle working list and stays
    /// cycle-scoped (as do prepared slots, scrolls, and Essence, which are
    /// loadout and resource, not knowledge). This is the permanent record the
    /// working list is re-seeded from at cycle start.
    ///
    /// AMENDS overworld_spell_system_v1_1 §5/§13 and the CycleState comment
    /// "spell knowledge is timeline knowledge that dies with the cycle", which put
    /// knowledge on the wrong side of the two-layer law. Lore and card blueprints
    /// were already permanent for exactly this reason; spells were the outlier.
    /// User-ruled 2026-08-04.
    /// </summary>
    public List<string> KnownSpellIds = new();

    public List<string> UnlockedLoreEntries = new();

    /// <summary>Quest ids permanently completed (cross-cycle arcs: the fragment
    /// spine, the Convergence). Cycle-scoped quests are NOT recorded here; they
    /// derive completion live and reset with the timeline. Stamped by
    /// QuestTracker.SyncCompletions.</summary>
    public List<string> CompletedQuestIds = new();

    // ── Unfinished business (quest spec §7) ───────────────────────────────
    /// <summary>
    /// Timeline quests that were active (unlocked, not complete) when a cycle
    /// unmaked. Append-only: each unmake adds its live timeline quests here
    /// as a permanent record of what was left behind. The quest log renders
    /// these in a collapsible "Unfinished Business" section under the main
    /// groups, and emotionally load-bearing (the cost of every reset, itemized).
    /// </summary>
    public List<UnfinishedQuestRecord> UnfinishedBusiness = new();

    // ── The honored dead ─────────────────────────────────────────────────
    /// <summary>
    /// Every unit death, every timeline. The loom remembers the dead even
    /// when their timelines no longer exist. The Ossuary (a campus
    /// building, outside time) draws on all of them. Append-only.
    /// </summary>
    public List<HonoredDeadRecord> HonoredDead = new();

    // ── The Hall of Records (deal ledger) ────────────────────────────────
    /// <summary>
    /// Every negotiation resolution, every timeline: signed, spurned, or
    /// collapsed (negotiation doc §7b). Append-only, like the honored dead:
    /// the loom remembers the deals even when their timelines are gone.
    /// Written by NegotiationManager; read by the campus Records tab.
    /// Record only. Grants no power.
    /// </summary>
    public List<DealRecord> DealRecords = new();

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

    /// <summary>Returns the mastery record for a blueprint, creating it if absent.</summary>
    public CardMasteryRecord GetCardMastery(string blueprintId)
    {
        CardMastery ??= new Dictionary<string, CardMasteryRecord>();
        if (!CardMastery.TryGetValue(blueprintId, out var rec))
        {
            rec = new CardMasteryRecord();
            CardMastery[blueprintId] = rec;
        }
        return rec;
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
