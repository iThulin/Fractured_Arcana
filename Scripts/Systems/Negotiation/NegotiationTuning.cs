// ============================================================
// NegotiationTuning.cs
//
// Purpose:        Single source of truth for every tunable
//                 number in negotiation v3 (the Ledger). Anywhere
//                 else in Scripts/Systems/Negotiation/: read
//                 from here, don't hardcode. Same rule as
//                 UITheme for colors.
// Layer:          Data (constants only)
// Collaborators:  NegotiationState.cs (main consumer),
//                 ArchetypeBehavior (archetype tables)
// See:            docs/negotiation_ledger_spec_v1.md §2–§7
// ============================================================

/// <summary>Every tunable constant in the negotiation system, one file.
/// Each section names the metric the knobs move. All values are STARTING
/// values (spec closing note): the par-relative star ladder is designed so
/// mis-tuned authoring degrades gracefully instead of making stars
/// unreachable.</summary>
public static class NegotiationTuning
{
    // ── The parley deck (v3.1, docs/negotiation_parley_deck_spec_v1.md) ──
    /// <summary>Cards in hand at table-open and the cap after each draw.</summary>
    public const int HandSize = 4;
    /// <summary>Cards drawn at the end of every turn (no reshuffle: the deck is the clock).</summary>
    public const int DrawPerTurn = 1;
    /// <summary>Embassy II: open with this many cards instead of HandSize.</summary>
    public const int EmbassyOpeningHand = 5;
    /// <summary>Credit at or below which they sit Guarded (arms folded).</summary>
    public const int PostureGuardedCredit = 1;
    /// <summary>Credit at or above which they lean in (Eager).</summary>
    public const int PostureEagerCredit = 5;
    /// <summary>Bluff: the agreed package must be worth at least this much
    /// to them (npc valuation) for a threatened walk to shake a slip loose.</summary>
    public const int BluffThreshold = 4;
    /// <summary>Bluff that fails costs this much goodwill.</summary>
    public const int BluffFailGoodwillCost = 1;
    /// <summary>The Nth hard-toned card at one table sets a Grievance.</summary>
    public const int HardCardGrievanceCount = 2;
    /// <summary>A warm-toned card on a Warm table gives this once per table.</summary>
    public const int WarmToneBonusGoodwill = 1;
    /// <summary>Sweeten: how much a card adds to one of your slips' worth to them.</summary>
    public const int SweetenNpcValueMax = 5;
    /// <summary>v3.2 sweeps: how many slips a Reach=Some card touches (random pick).</summary>
    public const int SweepSomeCount = 2;
    /// <summary>Bundle: at most this many of their slips in one category-ask.</summary>
    public const int BundleMaxSlips = 3;

    // ── v3.3 The Contested Table (docs/negotiation_contested_table_spec_v3_3.md) ──
    /// <summary>v3.5: the track runs −TrackMax (theirs) … 0 (contested) … +TrackMax (yours).</summary>
    public const int TrackMax = 2;
    /// <summary>A Claim card locks their terms at or past this position (wide claims: one step less).</summary>
    public const int ClaimAtPosition = 1;
    /// <summary>A Concede card gives your terms at or below this position (wide: one step more).</summary>
    public const int ConcedeAtPosition = -1;
    /// <summary>Settlement: your terms at or below this are taken; their terms at or above SettleSweepAt are swept in.</summary>
    public const int SettleTakeAt = -1;
    public const int SettleSweepAt = 1;
    /// <summary>Claim credit bonus by position: 0 at theirs / leaning theirs, 1 at contested / leaning yours, 2 at yours.</summary>
    public const int ClaimBonusMax = 2;
    /// <summary>Handshake sweep: their terms at "yours" sign while Credit + this ≥ their valuation.</summary>
    public const int SettleSweepBonus = 2;
    /// <summary>Conceding a term they had pulled toward them: extra goodwill.</summary>
    public const int ConcedePulledGoodwill = 1;
    /// <summary>Terms they pull back per turn (their top want).</summary>
    public const int TheirPullPerTurn = 1;

    // ── Legacy token economy (kept for BuildingDefinition / telemetry keys) ─
    public const int SchoolTokenCount = 2;
    public const int BaseOfferingFloor = 1;
    public const int UniversalPersuade = 1;

    // ── Goodwill (spec §2b/§2c) ───────────────────────────────────────────
    // Moves: how much free Credit a table opens with; how often Warm closes.
    public const int GoodwillMin = 0;
    public const int GoodwillMax = 10;
    /// <summary>Neutral opening goodwill before faction/archetype modifiers.</summary>
    public const int BaseGoodwill = 3;
    /// <summary>Goodwill ≥ this = Warm.</summary>
    public const int WarmGoodwill = 7;
    /// <summary>Goodwill ≤ this = Cold.</summary>
    public const int ColdGoodwill = 2;
    /// <summary>Faction standing → goodwill: Allied +2, Friendly +1,
    /// Unfriendly −1, Hostile −2 (applied in NegotiationState.Initialize).</summary>
    public const int StandingAlliedBonus = 2;
    public const int StandingFriendlyBonus = 1;
    public const int StandingUnfriendlyPenalty = 1;
    public const int StandingHostilePenalty = 2;
    /// <summary>Court standing pre-load (Trusted) bonus.</summary>
    public const int CourtTrustedBonus = 3;

    // ── Reactions (spec §4a–§4c) ──────────────────────────────────────────
    // Moves: how forgiving a near-miss Ask is; how much a wild Ask costs.
    /// <summary>Deficit at or below which they counter-propose instead of refusing.</summary>
    public const int CounterWindow = 2;
    /// <summary>Deficit at or above which a refusal is an insult (−2 goodwill).</summary>
    public const int InsultDeficit = 4;
    public const int RefuseGoodwillCost = 1;
    public const int InsultGoodwillCost = 2;
    /// <summary>Extra patience a refusal burns while the mood is Cold.</summary>
    public const int ColdRefusePatienceExtra = 1;
    /// <summary>Conceding a clause they value at or above this is "generous": +1 goodwill.</summary>
    public const int GenerousThreshold = 3;
    public const int GenerousGoodwillGain = 1;

    // ── Beats (spec §4d) ──────────────────────────────────────────────────
    /// <summary>Patience refunded when the player concedes the final-warning clause.</summary>
    public const int FinalDemandPatienceRefund = 2;

    // ── The squeeze (spec §4e) ────────────────────────────────────────────
    // Fires only behind a Grievance. Odds SHOWN to the player.
    public const int HoldOddsBase = 35;
    public const int HoldOddsPerGoodwill = 8;      // × (goodwill − HoldOddsPivot)
    public const int HoldOddsPivot = 3;
    public const int HoldOddsMin = 20;
    public const int HoldOddsMax = 80;
    public const int BristleGoodwillCost = 2;
    public const int BristlePatienceCost = 1;

    // ── Press (Intimidate) ───────────────────────────────────────────────
    // Goodwill cost lives in ArchetypeBehavior.PressGoodwillCost (archetype-
    // flavoured). A press that drives goodwill to GoodwillMin collapses the table.

    // ── Warm close (spec §2c) ─────────────────────────────────────────────
    public const int WarmRepBonus = +1;

    // ── Scoring & stars (spec §7) ─────────────────────────────────────────
    // Stars are measured against PAR: the brute-forced best package at
    // starting goodwill with no tokens. Fractions of par per star.
    public const float StarT4Fraction = 0.75f;
    public const float StarT3Fraction = 0.50f;
    public const float StarT2Fraction = 0.25f;
    /// <summary>Brute-force cap: tables with more open clauses than this
    /// compute par over the first MaxParClauses by authored order (2^12 = 4096
    /// subsets is cheap; 2^20 is not).</summary>
    public const int MaxParClauses = 12;

    // ── School signature moves (spec §5d) ────────────────────────────────
    public const int ShowOfPowerCreditBonus = 3;   // Elementalist: next Ask
    public const int QuietGroveGoodwill = 2;       // Druid
    public const int CommuneReveals = 2;           // Necromancer: top-N valuations
    public const int FabricateNpcValue = 3;        // Tinker: the device's worth to anyone
    public const int FabricateValue = 1;           // ...and to you

    // ── Offering / Demonstration clauses (spec §3) ───────────────────────
    public const int OfferingGold = 40;            // the purse you set down
    public const int OfferingValue = 2;            // what the purse is worth to you
    public const int TwistValue = 2;               // Opportunist mid-table rider, worth to you
    public const int TwistNpcValue = 3;            // ...and to her

    // ── Building hooks ───────────────────────────────────────────────────
    public const int CourierTier1Reveals = 1;      // valuations revealed at table-open
    public const int CourierTier2Reveals = 2;
    public const int CourierTier3InsightBonus = 1;
    public const int WarRoomReveals = 1;
}
