using System.Collections.Generic;

// ============================================================
// CycleState.cs
//
// Purpose:        Tier 2 of the three-tier save schema: every
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
// See:            open_world_refactor_v1.docx §10, Save Schema
// Tier rule:      If Kassian's next timeline would not contain
//                 it, it belongs here. If the loom remembers it,
//                 it belongs in EternalLedger.
// ============================================================

/// <summary>
/// All state for the current timeline. One cycle = one school,
/// one generated world, one corruption arc. Created fresh by
/// <see cref="SaveManager.NewGame"/> and
/// <see cref="SaveManager.BeginNewCycle"/>; never migrated across
/// cycles, only replaced.
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
    /// every seat is resolved. Dies with the timeline like the rest of this block;
    /// what survives a Convergence is the LoopRecord and the permanent flags.
    /// See docs/convergence_finale_spec_v1.md §2.</summary>
    public ConvergenceState Convergence = new();

    // ── Strategic world (the generated timeline) ─────────────────────────
    /// <summary>
    /// The authoritative Civ-scale world for this cycle: terrain, territories,
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
    /// dies with the timeline; cross-cycle renown lives in EternalLedger.
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
    /// spawn (CombatManager); pre-feature/legacy cycles default to 1.0.</summary>
    public float EnemyDifficultyMult = 1f;

    /// <summary>Corruption tile/territory spread-rate multiplier for this timeline,
    /// copied from the founding scenario. 1.0 = baseline. Consumed by
    /// CorruptionSpread.Tick; legacy cycles default to 1.0.</summary>
    public float CorruptionSpreadMult = 1f;

    // ── Timeline economy ─────────────────────────────────────────────────
    public int Gold = 0;

    /// <summary>Building construction/upgrade cost's second resource, alongside Gold.
    /// Standard ratio is 3 Materials : 1 Gold (BuildingTier.EffectiveMaterialsCost).
    /// Same tier as Gold by design (dies with the timeline). No gathering system
    /// exists yet, so NewGameScreen currently grants a flat starting stock as a
    /// placeholder.</summary>
    public int BuildMaterials = 0;

    /// <summary>
    /// Card upgrade currency. In-cycle power, so it dies with the timeline.
    /// </summary>
    public int ArcaneSplinters = 0;

    /// <summary>Guild supply stores (docs/supply_cache_spec_v1), harvested each
    /// lunation from guild-controlled supply caches (SupplyCacheSystem.Tick,
    /// banked directly: strategic income is never expedition-carried), and
    /// bargained in negotiation deals (DealTerm.SuppliesDelta; deal GAINS ride
    /// with the expedition as SuppliesEarned and bank on extraction). Same tier
    /// as Gold; dies with the timeline.</summary>
    public int Supplies = 0;

    /// <summary>One-shot flag for the supply-cache fog migration (v1 seeded
    /// caches Discovered; v1.1 hides them until earned, see
    /// SupplyCacheSystem.MigrateFog). True on fresh v1.1+ seeds.</summary>
    public bool SupplyCacheFogApplied = false;

    // ── W3: emergency-extraction debt ────────────────────────────────────
    /// <summary>Lunations the party owes for straggling home from an emergency
    /// extraction (claude/expedition_window_sliding_v1 §2.3). Set by
    /// ExpeditionManager.EmergencyExtract; consumed (advanced on the calendar
    /// WITH the full per-lunation world tick) by StrategicView on the next
    /// return to the strategic map. Serialized so quitting between extraction
    /// and return can't dodge the time cost.</summary>
    public int PendingStraggleLunations = 0;

    /// <summary>Human-readable "since your last sortie" strategic news: siege /
    /// warfront outcomes appended by KingdomTickSimulation on each lunation tick.
    /// Rendered by StrategicView's HUD on return to the map, then cleared at the
    /// top of the next Deploy (the player has read them). Serialized so the news
    /// survives the deploy → expedition → return scene round-trip and a quit.</summary>
    public List<string> PendingSiegeReports = new();

    /// <summary>Active warfronts, the visible, intervenable form of a siege.
    /// Opened / advanced / resolved by KingdomTickSimulation on the lunation tick;
    /// rendered and deployed-into by StrategicView.</summary>
    public List<Warfront> Warfronts = new();

    /// <summary>Phase 3 explore: per-city district content + reveal/clear progress
    /// for NPC cities visited this cycle. Keyed by CityExploreState.CityId. Written
    /// by CityExploreService / WorldAtlas3D as the player scouts a city; read on
    /// re-entering the same city. Cycle-scoped (the world reseeds each cycle).
    /// Additive save field, no SaveManager version bump.</summary>
    public List<CityExploreState> CityExplore = new();

    /// <summary>K3 hiring halls: per-city candidate stock, lazily refreshed each
    /// lunation when the hall is opened (HiringHallService). Keyed by the same
    /// CityExploreService.CityId convention as CityExplore. Cycle-scoped (the
    /// world reseeds each cycle). Additive save field, no version bump.</summary>
    public List<HiringHallState> HiringHalls = new();

    /// <summary>Q4 city markets: per-city item stock, same lifecycle and key
    /// convention as HiringHalls (CityMarketService). Additive, no bump.</summary>
    public List<CityMarketState> CityMarkets = new();

    /// <summary>Deploy-flow streamline (2026-08-21): the staging point the last
    /// sortie launched from ("x,y"), so clicking the Gatehouse reopens the deploy
    /// drawer there directly. Empty = never deployed (falls back to Home Camp).
    /// Additive save field: no version bump.</summary>
    public string LastDeployStagingKey = "";

    /// <summary>Deploy-flow streamline: consumable kinds (ItemDefinition ids) the
    /// player has UNCHECKED on the deploy drawer's loadout. Default empty = carry
    /// everything (today's behaviour); exclusions persist across sorties this
    /// cycle. Additive, no bump.</summary>
    public List<string> ExcludedConsumableIds = new();

    /// <summary>Phase 3 contracts boards: per-city posted contracts (the Quests
    /// service), same CityId + lazy-refresh convention as CityMarkets. Additive
    /// save field: no version bump.</summary>
    public List<CityContractBoardState> CityContractBoards = new();

    /// <summary>Phase 3 explore: a district FIGHT launched from a visited city
    /// round-trips through the combat scene; these record WHICH district so the
    /// strategic scene's return consume (StrategicView.ConsumeDistrictFightReturn)
    /// can clear it on victory and land the player back inside the same city.
    /// CityId follows the CityExploreService.CityId convention; empty = no fight
    /// pending. Serialized so the round-trip survives a mid-combat save; a stale
    /// pending id with no router return (mid-combat reload) is dropped harmlessly:
    /// the district simply stays live. Additive save fields, no version bump.</summary>
    public string PendingCityFightCityId = "";
    public int PendingCityFightDq = 0;
    public int PendingCityFightDr = 0;

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
    // Roster is tier 2: they are inhabitants of this timeline. Their arc
    // MILESTONES anchor into EternalLedger.RenownAnchors / MetaFlags so the
    // loom remembers them even when the timeline forgets.
    public List<Companion> Companions = new();
    public List<string> ActivePartyCompanionIds = new();
    public int MaxPartySize = 2;

    // ── Expedition v2: the split forces (2026-09-21 ruling) ──────────────
    // The castle parks and becomes a mobile anchor; a separate field party
    // travels to known anchors (cities, shard zones, built waypoints, the
    // parked castle) and dives the POIs around them. Every field below is
    // additive with a safe default, so pre-feature saves load clean and are
    // backfilled lazily by ExpeditionAnchors.BackfillPostings. No SaveManager
    // version bump. See docs/expedition_bastion_and_field_party_handoff_v1.md.

    /// <summary>Where the fortress is parked on the world, or -1,-1 before it
    /// has taken the field this cycle. A parked castle is a supply anchor and
    /// a field-party destination, which is what gives parking its weight.
    /// Distinct from LastDeployStagingKey, which records where the last sortie
    /// LAUNCHED from rather than where the castle now stands.</summary>
    public int CastleX = -1;
    public int CastleY = -1;

    /// <summary>True while the fortress is parked in the field rather than at
    /// dock. Ruled 2026-09-21: running the furnace dry parks the castle where it
    /// stands, it becomes a waypoint there, and it refuels on the next sortie.
    /// A parked castle also owns a StagingPoint with Source "Castle", which is
    /// what makes the whole existing deploy, marker and supply-anchor machinery
    /// treat it as a launch point without a parallel system.</summary>
    public bool CastleParked = false;

    /// <summary>Lunation the castle parked on, for the run log and the HUD.</summary>
    public int CastleParkedLunation = 0;

    /// <summary>Lunations of resupply still owed before the fortress can sortie
    /// again. Set on parking from how badly the hull was damaged: a pristine
    /// castle is ready next lunation, a battered one sits while work crews
    /// teleport in and rebuild it.
    ///
    /// <para>This is the cost of pushing past a dry furnace, and the reason the
    /// player cannot simply force-march the castle wherever they like. While it
    /// is above zero the castle is EXPOSED: its staging point is unavailable, the
    /// crews are in the open, and the resupply is a target.</para></summary>
    public int CastleRepairLunations = 0;

    /// <summary>True while work crews are teleporting in to refuel, restock,
    /// repair and carry the hold home. The vulnerable window.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CastleExposed => CastleParked && CastleRepairLunations > 0;

    /// <summary>Kingdom whose soldiers fell on the camp and are owed a tactical
    /// castle defence, or empty. The assault's strategic cost is applied the
    /// moment it lands, so leaving this unanswered sets the player back rather
    /// than letting them off; this only records that a FIGHT is still owed.
    /// See CastleThreats.</summary>
    public string PendingCastleAssaultKingdomId = "";

    /// <summary>True between launching a castle defence and landing back on the
    /// strategic map. Distinguishes "a fight is owed" from "a fight is being
    /// fought", so a mid-combat reload cannot be mistaken for a resolved one.
    /// Serialized for exactly that reason.</summary>
    public bool CastleDefenseLaunched = false;

    /// <summary>Fuel in the furnace between sorties. Needed because a march is
    /// ordered from the strategic map, where no ExpeditionManager exists to own
    /// StepsRemaining. Written when the castle parks, refilled when the resupply
    /// completes, and spent by CastleMarch.</summary>
    public int CastleFuel = 0;

    /// <summary>Tank capacity as of the last sortie, including castle type, crew
    /// and module bonuses. Stored rather than recomputed so the strategic map can
    /// show a truthful gauge without standing up the whole expedition rig.</summary>
    public int CastleMaxFuel = 0;

    /// <summary>Spoils the fortress carries but has not banked. Filled by
    /// parking, emptied by a recall (and later by a field party courier run).</summary>
    public CastleHold CastleHold = new();

    /// <summary>Field parties operating away from the castle. Iterated by the
    /// turn loop from day one even while MaxFieldParties is 1, so unlocking
    /// another party is a data change rather than a refactor.</summary>
    public List<FieldParty> FieldParties = new();

    /// <summary>How many field parties may be fielded at once. Baseline 1;
    /// growth is a campus upgrade (which building grants it is still open,
    /// so nothing writes this yet).</summary>
    public int MaxFieldParties = 1;

    /// <summary>Built waypoints: the consumable access the castle conjures
    /// over a discovered POI. Permanent anchors are NOT stored here; they come
    /// from World.StagingPoints and World.ShardZones through
    /// ExpeditionAnchors.All.</summary>
    public List<Waypoint> Waypoints = new();

    /// <summary>This lunation's expedition budget: one castle move plus K
    /// field dives (ruling 2026-09-21). Keeps map reveal on the 12-lunation
    /// clock while holding POI throughput near a radius-12 sortie's.</summary>
    public ExpeditionTurnState ExpeditionTurn = new();

    // ── Equipment armory ─────────────────────────────────────────────────
    /// <summary>
    /// Items are timeline loot; they die with the cycle. (Future option:
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

    // ── Faction signature spells (handoff section 4.6) ──────────────────
    /// <summary>The faction library: a bounded, curated set of blueprints
    /// drawn from the collection, carrying POOL-LEVEL upgrade tiers so an
    /// upgrade propagates to every wizard referencing the spell. Deliberately
    /// a third tier rather than an alias of PlayerDeck: per-copy OwnedCard
    /// tiers cannot express propagation, and aliasing would make every card
    /// added for an ally bloat the player's own draws. See SignaturePool.cs
    /// and the pressure test, finding 4.</summary>
    public SignaturePoolState SignaturePool = new();

    /// <summary>Signature slots the player has granted to allied wizards, one
    /// entry per companion who has any. Bounded by slot count and a rarity
    /// point budget (ruling 3, 2026-09-21).</summary>
    public List<WizardSignatureSlots> SignatureGrants = new();

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
    /// budget is a timeline resource; the KNOWLEDGE that makes minting possible is
    /// what persists. See docs/progression_card_acquisition_v1_2.md.
    /// </summary>
    public int MintsThisCycle = 0;

    // ── Overworld magic (S1) ─────────────────────────────────────────────
    /// <summary>Known/prepared overworld spells, Essence pool, scrolls,
    /// beacons. Spell knowledge is timeline knowledge; it dies with the cycle.
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
