using System.Collections.Generic;

// ============================================================
// ShadowState.cs
//
// Purpose:        Tier 2 data model for the espionage layer — the
//                 Informant Network and the Veiled Concord. The
//                 third tenant of the lunation tick, sitting beneath
//                 the Court & Council system the way the underworld
//                 sits beneath the throne room. Pure data + derived
//                 Concord-standing math; NO simulation logic lives
//                 here (the tick that mutates it is phase E2+ and
//                 lives in CouncilTick.cs).
//
//                 TWO new structs total, per the two-structs-max
//                 rule: InformantState and ConcordContract. The
//                 espionage fields on the CouncilState container
//                 (Informants, ConcordContracts, ConcordFavor,
//                 Marked, ConcordDealings, ConcordContacted) are
//                 scalars/lists, not new struct types. Everything
//                 else is REUSE: court Exposure is the counter-intel
//                 meter for court-embedded informants; EchoEvent is
//                 the false-echo vehicle; the negotiation encounter
//                 is the Tier C broker; the corruption clock and
//                 warfront machinery are existing hooks.
// Layer:          Data
// Collaborators:  CouncilState.cs (owns the espionage fields),
//                 CourtState.cs Exposure (court-embedded burns),
//                 ConcordGenerator.cs (nodes + price list, phase E1c),
//                 CouncilTick.cs (yields/burns/contracts, phase E2+)
// See:            espionage_veiled_concord_spec_v1.md §2, §3, §7
// Tier rule:      Kassian's next timeline contains no informants and
//                 no Concord ledger. Cross-cycle carry is limited to
//                 exfiltrated-Access renown in EternalLedger (E6).
// ============================================================

/// <summary>
/// Shared string vocabulary and tuning constants for the espionage
/// layer. Strings (not enums) for Role/ContractType so authored JSON
/// (the Concord price list) references them without a mapping table —
/// consistent with CourtVocab's archetype/office handling.
/// </summary>
public static class ShadowVocab
{
    // ── Informant roles (§2c) ────────────────────────────────────────────
    /// <summary>Pure sensor. Charts tiles at range, previews echoes and the
    /// Astrologer's next corruption target. Cannot act, cannot be traced by
    /// its own noise — the safest, slowest-burning role.</summary>
    public const string RoleWatcher = "Watcher";

    /// <summary>Fence and forger. Produces sellable intel, discovers secrets,
    /// can manufacture a false echo (a fabricated EchoEvent).</summary>
    public const string RoleCutout = "Cutout";

    /// <summary>Wrecker. Degrades warfront sieges and delays corruption ticks.
    /// Action is loud: bleeds Cover fastest under counter-intelligence.</summary>
    public const string RoleSaboteur = "Saboteur";

    public static readonly string[] Roles =
    {
        RoleWatcher, RoleCutout, RoleSaboteur,
    };

    // ── Concord contract types (§3c) ─────────────────────────────────────
    public const string ContractPlantAsset = "PlantAsset";
    public const string ContractPurchaseIntel = "PurchaseIntel";
    public const string ContractSabotage = "Sabotage";
    public const string ContractTheft = "Theft";
    public const string ContractExtraction = "Extraction";
    public const string ContractAssassination = "Assassination";

    public static readonly string[] ContractTypes =
    {
        ContractPlantAsset, ContractPurchaseIntel, ContractSabotage,
        ContractTheft, ContractExtraction, ContractAssassination,
    };

    // ── Cover tuning (§12 STARTING VALUES — tune here) ───────────────────
    /// <summary>Cover 0..10. At 0 the informant is burned.</summary>
    public const int CoverMax = 10;
    public const int CoverMin = 0;

    /// <summary>Starting Cover by acquisition source. Turned-from-a-secret is
    /// the default midpoint; Concord-bought assets arrive well-embedded;
    /// coerced captives are exposed and expendable.</summary>
    public const int CoverStartTurned = 6;
    public const int CoverStartConcordBought = 9;
    public const int CoverStartCoerced = 3;

    // ── Access tuning (§2b) ──────────────────────────────────────────────
    /// <summary>Access 1..3. Gates the higher-value yields and Sabotage.</summary>
    public const int AccessMin = 1;
    public const int AccessMax = 3;

    /// <summary>Lunations in place before Access ripens by +1 (Library halves
    /// this — see campus riders, §6). Not applied here; read by the tick.</summary>
    public const int AccessRipenLunations = 3;

    // ── Marked tuning (§3d) ──────────────────────────────────────────────
    /// <summary>Marked 0..10, the shadow-world's memory of you. Decays -1 per
    /// idle lunation, exactly like court Exposure.</summary>
    public const int MarkedMax = 10;
    public const int MarkedNoticed = 3;    // a courtier may blackmail your dealings
    public const int MarkedSoldOut = 6;    // the Concord fences your movements
    public const int MarkedContracted = 9; // the Astrologer commissions against you

    // ── Marketplace tuning (§3b/§3c, phase E3 — STARTING VALUES) ─────────
    /// <summary>Concord Favor granted for fencing one known courtier secret.</summary>
    public const int FavorSellSecret = 12;

    public const int FavorCostPlantAsset = 15;
    public const int FavorCostPurchaseIntel = 10;
    public const int FavorCostTheft = 30;

    /// <summary>Marked gained when a contract completes / a sell is traced. Dirtier
    /// work leaves a longer shadow.</summary>
    public const int MarkedGainPlantAsset = 1;
    public const int MarkedGainPurchaseIntel = 1;
    public const int MarkedGainTheft = 2;
    public const int MarkedGainTracedSell = 2;

    /// <summary>Contract durations in lunations (Tier A fast, Tier B slower).</summary>
    public const int ContractDurPlantAsset = 1;
    public const int ContractDurPurchaseIntel = 1;
    public const int ContractDurTheft = 2;

    /// <summary>Undiscovered POIs a Purchase Intel contract reveals in the
    /// target kingdom on completion.</summary>
    public const int PurchaseIntelPoiReveal = 3;

    /// <summary>Trace chance (%) that a sold secret is pinned on the guild:
    /// base + per current Marked point (ruling #3 — only a traced sell bites).</summary>
    public const int SellTraceBasePercent = 20;
    public const int SellTracePerMarked = 5;

    /// <summary>Regard damage to the sold-out courtier when a sale is traced.</summary>
    public const int TracedSellRegardHit = 2;

    // ── Sabotage & the clock (§2c / §3c / §4, phase E4) ──────────────────
    public const int FavorCostSabotage = 20;
    public const int ContractDurSabotage = 1;
    public const int MarkedGainSabotage = 2;

    /// <summary>Sabotage variant, carried in ConcordContract.TargetId and passed
    /// to the Saboteur strike: break an active siege, or stall a corruption tick.</summary>
    public const string SabotageSiege = "siege";
    public const string SabotageCorruption = "corruption";

    /// <summary>Warfront.Advance points pushed back toward repel (0). A bought
    /// Concord break is decisive; an informant's own strike is smaller; passive
    /// erosion is a slow bleed each lunation.</summary>
    public const int ConcordSiegeBreak = 40;
    public const int SaboteurSiegeStrike = 25;
    public const int SaboteurSiegePassive = 6;

    /// <summary>Saboteur active strike: Cover spent and the Access it needs.</summary>
    public const int SaboteurStrikeCoverCost = 3;
    public const int SaboteurStrikeMinAccess = 2;

    // ── Cutout false echo (§2c A3, phase E4) ─────────────────────────────
    public const int ForgeEchoCoverCost = 2;
    public const int ForgeEchoMinAccess = 3;

    /// <summary>Trace chance (%) that a forged echo is exposed as a fabrication,
    /// detonating as a court Exposure spike (Scandal). Rises with Marked.</summary>
    public const int ForgeEchoTraceBasePercent = 25;
    public const int ForgeEchoExposureSpike = 3;

    // ── The shadow war (§3d threshold 9, §3e, phase E5) ──────────────────
    /// <summary>Per-lunation chance (%) the Astrologer commissions the Concord
    /// against the guild once Marked is at the Contracted-Against threshold.</summary>
    public const int AstrologerContractChance = 40;
    public const int AstrologerContractDuration = 1;

    /// <summary>The Astrologer's standing bid on an against-guild contract — the
    /// Favor the guild must BEAT to outbid and buy it back (§3e).</summary>
    public const int AstrologerBidFavor = 25;

    /// <summary>Cover each informant loses when a mass-burn contract lands.</summary>
    public const int MassBurnCover = 4;

    /// <summary>Marked booked when the guild outbids a contract — dealing with
    /// the shadows again, however defensively.</summary>
    public const int OutbidMarkedGain = 1;

    /// <summary>Against-guild contract flavors, carried in ConcordContract.TargetId
    /// ("seize:&lt;companionId&gt;" or "burn").</summary>
    public const string AgainstSeize = "seize";
    public const string AgainstBurn = "burn";

    // Court blackmail of the guild's own dealings (§3d threshold 3).
    public const int BlackmailStandingPenalty = 4;

    // Extraction (§3c, buy side).
    public const int FavorCostExtraction = 35;
    public const int ContractDurExtraction = 1;
    public const int MarkedGainExtraction = 2;

    // ── Tier C + the spine (§3c / §6, phase E6) ──────────────────────────
    // Campus building ids the espionage layer reads (snake_case, Data/Buildings).
    public const string BuildingUndercroft = "undercroft";      // the spine (to be authored)
    public const string BuildingArcaneLibrary = "arcane_library"; // ripen rider (§6)
    public const string BuildingScriptorum = "scriptorum";       // records → renown (§6)

    /// <summary>Concurrent informant cap by Undercroft tier (0 = no building).
    /// The spine's economy knob, mirroring the Embassy's envoy cap.</summary>
    public static int InformantCap(int undercroftTier) => undercroftTier switch
    {
        <= 1 => 2,
        2 => 4,
        _ => 6,
    };

    /// <summary>Concurrent guild-contract cap by Undercroft tier.</summary>
    public static int ContractCap(int undercroftTier) => undercroftTier switch
    {
        <= 1 => 1,
        2 => 2,
        _ => 3,
    };

    /// <summary>Undercroft tier required to commission Assassination (§3c Inner).</summary>
    public const int AssassinationMinUndercroft = 3;

    /// <summary>Undercroft II runs the guild's networks itself: unhandled
    /// informants get this much counter-intelligence mitigation for free (§6).</summary>
    public const int UndercroftHandlerTier = 2;
    public const int UndercroftHandlerMitigation = 6;

    /// <summary>Undercroft III shaves this much Marked off each completed guild
    /// contract — deeper tradecraft leaves a shorter shadow (§6).</summary>
    public const int UndercroftMarkedDiscountTier = 3;
    public const int UndercroftMarkedDiscount = 1;

    public const int FavorCostAssassination = 60;
    public const int ContractDurAssassination = 3;   // Tier C — the slow, dear work
    public const int MarkedGainAssassination = 3;
    public const int AssassinationExposureSpike = 5; // the court investigates its dead

    /// <summary>Max Cover a re-planted informant inherits from banked
    /// exfiltration renown (Hall of Records / §6).</summary>
    public const int ExfilRenownCoverCap = 4;

    // ── Concord standing thresholds (§3c gate) ───────────────────────────
    // Derived from lifetime completed dealings, NOT stored as a band — mirrors
    // CourtState.Band() deriving from Regard×Influence. The driver scalar is
    // CouncilState.ConcordDealings (completed contracts + traced sells).
    private const int StandingKnownAt = 1;   // first dealing: out of Unaware
    private const int StandingTrustedAt = 4; // Theft/Extraction unlocked
    private const int StandingInnerAt = 8;   // Assassination unlocked (also needs Undercroft III)

    /// <summary>Derive the Concord standing band from lifetime dealings.
    /// Never stored — always computed, so it cannot drift from its driver.</summary>
    public static ConcordStandingBand StandingBand(int dealings)
    {
        if (dealings >= StandingInnerAt)
        {
            return ConcordStandingBand.Inner;
        }
        if (dealings >= StandingTrustedAt)
        {
            return ConcordStandingBand.Trusted;
        }
        if (dealings >= StandingKnownAt)
        {
            return ConcordStandingBand.Known;
        }
        return ConcordStandingBand.Unaware;
    }

    /// <summary>The band that GATES the marketplace: making contact floors the
    /// guild at Known (the cabal will deal the moment you find the door), then
    /// lifetime dealings raise it. StandingBand alone stays the raw driver so
    /// the derived-never-stored round-trip check is unaffected.</summary>
    public static ConcordStandingBand EffectiveBand(bool contacted, int dealings)
    {
        if (!contacted)
        {
            return ConcordStandingBand.Unaware;
        }
        var b = StandingBand(dealings);
        return b < ConcordStandingBand.Known ? ConcordStandingBand.Known : b;
    }

    // ── Node generation (§3a, phase E1c) ─────────────────────────────────
    /// <summary>How many Concord nodes to scatter into a world with this many
    /// kingdoms: roughly one shadow-market per two territories, floored at 2 so
    /// even a small world has a reachable underworld, capped so nodes stay a
    /// discovery reward rather than ambient furniture.</summary>
    public static int NodeCountFor(int kingdomCount)
    {
        int n = kingdomCount / 2;
        if (n < 2) { n = 2; }
        if (n > 6) { n = 6; }
        return n;
    }

    /// <summary>The broker archetype at a Concord node (§Negotiation 4 ids;
    /// drives the Tier C broker negotiation, E6). Derived from the cycle seed
    /// and the node's tile — never stored, so it cannot drift and adds no save
    /// surface. v1 RULING (#6, deferred): all nodes broker as Opportunist
    /// (numerous, surprising hidden terms — the cabal's default face). The
    /// FNV hook below is the one-line seam to vary per node later; left inert
    /// rather than shipped as dead variety.</summary>
    public static string BrokerArchetypeFor(int seed, int x, int y)
    {
        // Deterministic seam for E6 per-node variety (one line, using the
        // existing GlyphCipher.Fnv1a32):
        //   uint h = GlyphCipher.Fnv1a32($"concord_broker:{seed}:{x}:{y}");
        //   return CourtVocab.Archetypes[h % (uint)CourtVocab.Archetypes.Length];
        // Left pinned in v1 rather than shipped as dead variety.
        return "Opportunist";
    }
}

/// <summary>
/// Derived Concord relationship band. NEVER stored — always computed from
/// lifetime dealings via <see cref="ShadowVocab.StandingBand"/>. Gates which
/// contract tiers the cabal will sell you.
/// </summary>
public enum ConcordStandingBand
{
    Unaware,  // no contact — nothing bought or sold yet
    Known,    // Plant Asset, Purchase Intel, Sabotage
    Trusted,  // + Theft, Extraction
    Inner,    // + Assassination (also gated on Undercroft III)
}

/// <summary>
/// One standing informant — a turned NPC asset, NOT a companion (the
/// distinction is the layer's spine: informants never touch the party
/// HP pool, never fill an expedition slot). Managed by one meter, Cover;
/// at Cover 0 the asset is burned and removed. Save-adjacent — round-trip
/// asserted in CouncilSaveAssert.
/// </summary>
public class InformantState
{
    public string Id = "";

    /// <summary>Kingdom the informant operates in. Always set.</summary>
    public string KingdomId = "";

    /// <summary>Non-empty = embedded inside a court, sharing that court's
    /// Exposure meter (no new counter). A court-embedded burn spikes court
    /// Exposure and the network is traced back to the guild (§2e).</summary>
    public string CourtierId = "";

    /// <summary>Non-empty = embedded in a siege. Feeds WarfrontStrongholdCleared
    /// and the Saboteur siege-degrade verb (§2c).</summary>
    public string WarfrontId = "";

    /// <summary>ShadowVocab.Roles — Watcher, Cutout, Saboteur. Determines
    /// yield type and burn risk.</summary>
    public string Role = ShadowVocab.RoleWatcher;

    /// <summary>0..10, inverse of exposure. The meter the player manages;
    /// 0 = burned. Starts by acquisition source (ShadowVocab.CoverStart*).</summary>
    public int Cover = ShadowVocab.CoverStartTurned;

    /// <summary>1..3. What the informant can reach; ripens +1 per
    /// AccessRipenLunations survived. Gates high-value yields and Sabotage.</summary>
    public int Access = ShadowVocab.AccessMin;

    /// <summary>Companion left at campus to run this network (soft sacrifice —
    /// not on expedition, not an envoy). Empty = unhandled, or handled free by
    /// Undercroft II. Reduces counter-intelligence burn.</summary>
    public string HandlerCompanionId = "";

    /// <summary>Absolute lunation index at placement; drives Access ripen.</summary>
    public int LunationPlaced = 0;
}

/// <summary>
/// One live Veiled Concord contract, ticking toward completion. Bought by
/// the guild (AgainstPlayer false) or commissioned by the Astrologer against
/// the guild at Marked 9 (AgainstPlayer true — the outbid path, §3e).
/// Save-adjacent — round-trip asserted.
/// </summary>
public class ConcordContract
{
    public string Id = "";

    /// <summary>ShadowVocab.ContractTypes.</summary>
    public string ContractType = "";

    /// <summary>Kingdom the contract operates in (siege, court, or node).</summary>
    public string TargetKingdomId = "";

    /// <summary>Target id, meaning by type: courtier (Theft/Assassination),
    /// warfront (Sabotage), companion (Extraction), POI/node (Purchase/Plant).
    /// Empty where the contract targets no specific entity.</summary>
    public string TargetId = "";

    public int LunationsRemaining = 0;

    /// <summary>Concord Favor spent to commission. On an outbid (§3e) this is
    /// the standing bid the guild must beat to buy the contract back.</summary>
    public int FavorPaid = 0;

    /// <summary>True = Astrologer-commissioned against the guild. Resolved at
    /// tick step 6 with the outbid offered before it fires.</summary>
    public bool AgainstPlayer = false;
}
