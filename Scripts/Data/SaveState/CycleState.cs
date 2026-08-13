using System.Collections.Generic;

// ============================================================
// CycleState.cs
//
// Purpose:        Tier 2 of the three-tier save schema — every
//                 piece of state scoped to ONE timeline (one
//                 cycle / one loop). Dies and is replaced
//                 wholesale when a cycle ends, by any outcome.
//                 Serialized to user://saves/slot_N_cycle.json.
// Layer:          Data
// Collaborators:  GuildSaveData.cs (in-memory envelope + shims),
//                 EternalLedger.cs (tier 3 sibling),
//                 SaveManager.cs (dual-file IO),
//                 CalendarState.cs (owned field),
//                 CampaignState.cs (owned field)
// See:            open_world_refactor_v1.docx §10 — Save Schema
// Tier rule:      If Kassian's next timeline would not contain
//                 it, it belongs here. If the loom remembers it,
//                 it belongs in EternalLedger.
// ============================================================

/// <summary>
/// All state for the current timeline. One cycle = one school,
/// one generated world, one corruption arc. Created fresh by
/// <see cref="SaveManager.NewGame"/> and
/// <see cref="SaveManager.BeginNewCycle"/>; never migrated across
/// cycles — replaced.
/// </summary>
public class CycleState
{
    // ── Meta ────────────────────────────────────────────────────────────
    /// <summary>Schema version of the cycle file. Must match SaveManager.CURRENT_VERSION.</summary>
    public int SaveVersion = SaveManager.CURRENT_VERSION;

    /// <summary>1-based index of this cycle within the guild's loop history.</summary>
    public int CycleNumber = 1;

    /// <summary>1-based year within THIS timeline. A fresh timeline starts at 1;
    /// SaveManager.ContinueCampaign increments it when the player holds the timeline
    /// past the Grand Conjunction instead of letting it unmake. See
    /// claude/progression_persistence_model_v1.md.</summary>
    public int CampaignYear = 1;

    // ── The astrological calendar ────────────────────────────────────────
    /// <summary>Phase / lunation / conjunction clock for this timeline.</summary>
    public CalendarState Calendar = new();

    // ── Campaign state ───────────────────────────────────────────────────
    /// <summary>
    /// Archmage placements, dispositions, corruption levels, mentor state.
    /// Generated once at cycle start from the cycle seed.
    /// (Corruption ticking is re-keyed to lunation boundaries in Phase 1;
    /// the legacy GlobalStepCount fields remain valid until then.)
    /// </summary>
    public CampaignState Campaign = new();

    /// <summary>The finale's progress for this timeline (schema v102). Phase −1 on
    /// every cycle that has not opened the Anchorhold, which is all of them until
    /// every seat is resolved. Dies with the timeline like the rest of this block —
    /// what survives a Convergence is the LoopRecord and the permanent flags.
    /// See docs/convergence_finale_spec_v1.md §2.</summary>
    public ConvergenceState Convergence = new();

    // ── Strategic world (the generated timeline) ─────────────────────────
    /// <summary>
    /// The authoritative Civ-scale world for this cycle — terrain, territories,
    /// corruption, discovery, POIs, staging points. Generated once at cycle start
    /// by WorldGenerator; read by the strategic view and the expedition window.
    /// Replaces the retired node-graph StrategicMapData.
    /// </summary>
    public WorldData World = new();
    public int WorldSeed;

    /// <summary>
    /// Dynamic per-territory state (faction control, stance, tier, stability,
    /// influence), keyed by kingdom id. Corruption is single-sourced in
    /// Campaign.CorruptionLevels; tile-level corruption lives in World.
    /// </summary>
    public Dictionary<string, KingdomState> Kingdoms = new();

    // ── Court & Council (timeline politics) ──────────────────────────────
    /// <summary>
    /// Courts, courtiers, the favor ledger, envoy missions, and echoes in
    /// flight. Generated at cycle start beside Kingdoms by CourtGenerator;
    /// dies with the timeline — cross-cycle renown lives in EternalLedger.
    /// See court_council_system_v1_1.docx §12.
    /// </summary>
    public CouncilState Council = new();

    // ── Wizard (one cycle, one school) ───────────────────────────────────
    public string SelectedSchool = "Elementalist";
    public string WizardName = "Wizard";

    // ── World position ───────────────────────────────────────────────────
    /// <summary>
    /// Region/kingdom the player is currently operating in. Becomes a
    /// kingdom id when the strategic map lands in Phase 1.
    /// </summary>
    public string CurrentRegionId = "frontier_wilds";

    // ── Difficulty (stamped from the founding scenario each cycle) ───────
    /// <summary>Enemy stat multiplier for this timeline, copied from the guild's
    /// founding scenario at world generation. 1.0 = baseline. Consumed at combat
    /// spawn (CombatManager) — pre-feature/legacy cycles default to 1.0.</summary>
    public float EnemyDifficultyMult = 1f;

    /// <summary>Corruption tile/territory spread-rate multiplier for this timeline,
    /// copied from the founding scenario. 1.0 = baseline. Consumed by
    /// CorruptionSpread.Tick — legacy cycles default to 1.0.</summary>
    public float CorruptionSpreadMult = 1f;

    // ── Timeline economy ─────────────────────────────────────────────────
    public int Gold = 0;

    /// <summary>Building construction/upgrade cost's second resource, alongside Gold —
    /// standard ratio is 3 Materials : 1 Gold (BuildingTier.EffectiveMaterialsCost).
    /// Same tier as Gold by design (dies with the timeline) — no gathering system
    /// exists yet, so NewGameScreen currently grants a flat starting stock as a
    /// placeholder.</summary>
    public int BuildMaterials = 0;

    /// <summary>
    /// Card upgrade currency. In-cycle power, so it dies with the timeline.
    /// </summary>
    public int ArcaneSplinters = 0;

    /// <summary>Guild supply stores (docs/supply_cache_spec_v1) — harvested each
    /// lunation from guild-controlled supply caches (SupplyCacheSystem.Tick,
    /// banked directly: strategic income is never expedition-carried), and
    /// bargained in negotiation deals (DealTerm.SuppliesDelta; deal GAINS ride
    /// with the expedition as SuppliesEarned and bank on extraction). Same tier
    /// as Gold — dies with the timeline.</summary>
    public int Supplies = 0;

    /// <summary>One-shot flag for the supply-cache fog migration (v1 seeded
    /// caches Discovered; v1.1 hides them until earned — see
    /// SupplyCacheSystem.MigrateFog). True on fresh v1.1+ seeds.</summary>
    public bool SupplyCacheFogApplied = false;

    // ── W3: emergency-extraction debt ────────────────────────────────────
    /// <summary>Lunations the party owes for straggling home from an emergency
    /// extraction (claude/expedition_window_sliding_v1 §2.3). Set by
    /// ExpeditionManager.EmergencyExtract; consumed — advanced on the calendar
    /// WITH the full per-lunation world tick — by StrategicView on the next
    /// return to the strategic map. Serialized so quitting between extraction
    /// and return can't dodge the time cost.</summary>
    public int PendingStraggleLunations = 0;

    /// <summary>Human-readable "since your last sortie" strategic news — siege /
    /// warfront outcomes appended by KingdomTickSimulation on each lunation tick.
    /// Rendered by StrategicView's HUD on return to the map, then cleared at the
    /// top of the next Deploy (the player has read them). Serialized so the news
    /// survives the deploy → expedition → return scene round-trip and a quit.</summary>
    public List<string> PendingSiegeReports = new();

    /// <summary>Active warfronts — the visible, intervenable form of a siege.
    /// Opened / advanced / resolved by KingdomTickSimulation on the lunation tick;
    /// rendered and deployed-into by StrategicView.</summary>
    public List<Warfront> Warfronts = new();

    /// <summary>Phase 3 explore: per-city district content + reveal/clear progress
    /// for NPC cities visited this cycle. Keyed by CityExploreState.CityId. Written
    /// by CityExploreService / WorldAtlas3D as the player scouts a city; read on
    /// re-entering the same city. Cycle-scoped (the world reseeds each cycle).
    /// Additive save field — no SaveManager version bump.</summary>
    public List<CityExploreState> CityExplore = new();

    /// <summary>K3 hiring halls: per-city candidate stock, lazily refreshed each
    /// lunation when the hall is opened (HiringHallService). Keyed by the same
    /// CityExploreService.CityId convention as CityExplore. Cycle-scoped (the
    /// world reseeds each cycle). Additive save field — no version bump.</summary>
    public List<HiringHallState> HiringHalls = new();

    /// <summary>Q4 city markets: per-city item stock, same lifecycle and key
    /// convention as HiringHalls (CityMarketService). Additive — no bump.</summary>
    public List<CityMarketState> CityMarkets = new();

    /// <summary>Warfront the player is currently deployed to intervene in (empty =
    /// none). Set when an intervention deploy launches; consumed by StrategicView
    /// on return, applying the expedition outcome to the front. Serialized so the
    /// intervention survives the deploy → expedition → return round-trip.</summary>
    public string PendingWarfrontId = "";

    /// <summary>Which side the pending intervention is fighting for.</summary>
    public WarfrontSide PendingWarfrontSide = WarfrontSide.Defend;

    /// <summary>Set by ExpeditionManager once the besieging stronghold is broken
    /// (a combat won during a warfront-intervention run). The intervention counts
    /// as a success only if the stronghold was cleared AND the party extracted.
    /// Read + reset by StrategicView on return. Serialized so it survives the
    /// combat-scene round-trips within the expedition.</summary>
    public bool WarfrontStrongholdCleared = false;

    // ── Run stats (this cycle only) ──────────────────────────────────────
    // Lifetime totals are derived from EternalLedger.LoopHistory + this.
    public int TotalRuns = 0;
    public int RunsWon = 0;
    public int RunsLost = 0;
    public int TotalGoldEarned = 0;
    public int TotalEncountersWon = 0;

    // ── Companions (timeline people) ─────────────────────────────────────
    // Roster is tier 2 — they are inhabitants of this timeline. Their arc
    // MILESTONES anchor into EternalLedger.RenownAnchors / MetaFlags so the
    // loom remembers them even when the timeline forgets.
    public List<Companion> Companions = new();
    public List<string> ActivePartyCompanionIds = new();
    public int MaxPartySize = 2;

    // ── Equipment armory ─────────────────────────────────────────────────
    /// <summary>
    /// Items are timeline loot — they die with the cycle. (Future option:
    /// named relics that can be essence-anchored into the ledger.)
    /// </summary>
    public ArmoryData Armory = new();

    // ── Constructed deck (in-cycle power) ────────────────────────────────
    /// <summary>
    /// Owned card copies, upgrades, grafts, and the active-deck loadout.
    /// Rebuilt each cycle from the unlocked pool (which lives in the
    /// ledger). Seeded by StarterDeckLoader at cycle start.
    /// </summary>
    public PlayerDeckSave PlayerDeck = new();

    /// <summary>Minimum cards that must remain in the deck.</summary>
    public int MinDeckSize = 10;

    /// <summary>
    /// Which Regalia the player chose to bring into THIS cycle. Ownership is
    /// permanent and lives on EternalLedger.RegaliaBlueprintIds; this is only
    /// the loadout decision, so it is wiped with the timeline and re-made at
    /// the start of every cycle. Bounded by RegaliaService.MaxCarry (K), which
    /// scales with shards collected. See docs/progression_card_acquisition_v1.md §6c.
    /// </summary>
    public List<string> CarriedRegaliaIds = new();

    /// <summary>
    /// Cards minted from the Arcane Library this cycle. The per-cycle cap is what
    /// stops minting from trivialising the draft and flattening the reseed: you can
    /// rebuild your core, not your whole deck. Resets with the timeline, because the
    /// budget is a timeline resource — the KNOWLEDGE that makes minting possible is
    /// what persists. See docs/progression_card_acquisition_v1_2.md.
    /// </summary>
    public int MintsThisCycle = 0;

    // ── Overworld magic (S1) ─────────────────────────────────────────────
    /// <summary>Known/prepared overworld spells, Essence pool, scrolls,
    /// beacons. Spell knowledge is timeline knowledge — dies with the cycle.
    /// See overworld_spell_system_v1_1 §5/§13 and GrimoireState.cs.</summary>
    public GrimoireState Grimoire = new();

    // ── Faction reputation (the timeline forgets) ────────────────────────
    public Dictionary<string, int> FactionReputation = new();

    // ── Narrative state (timeline-scoped) ────────────────────────────────
    /// <summary>
    /// One-shot narrative events completed in THIS timeline. Cross-loop
    /// story lives in EternalLedger.MetaNarrativeFlags instead.
    /// </summary>
    public List<string> CompletedEvents = new();

    /// <summary>Story/chain flags set by encounter choices in THIS timeline
    /// (EncounterChoice.SetFlags routes here). Kept separate from
    /// CompletedEvents so an encounter id can never collide with a flag and
    /// wrongly filter the pool. Read by choice gating (RequiredFlag).
    /// Cross-loop story still lives in EternalLedger.MetaNarrativeFlags.</summary>
    public HashSet<string> WorldFlags = new();

    /// <summary>True if the given timeline flag is set (null/empty = false).</summary>
    public bool HasFlag(string flag) =>
        !string.IsNullOrEmpty(flag) && WorldFlags.Contains(flag);

    /// <summary>Set a timeline flag (idempotent). Returns true if newly added.</summary>
    public bool SetFlag(string flag) =>
        !string.IsNullOrEmpty(flag) && WorldFlags.Add(flag);

    // ── Phase 3+ stubs (carried over; all timeline-scoped) ───────────────
    public string CharterAlignment = "";
    public int SeasonalThreatLevel = 0;
    public Dictionary<string, int> FragmentProgress = new();
}
