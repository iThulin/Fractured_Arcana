// ============================================================
// NegotiationTuning.cs
//
// Purpose:        Single source of truth for every tunable
//                 number in the negotiation system. Anywhere
//                 else in Scripts/Systems/Negotiation/: read
//                 from here, don't hardcode. Same rule as
//                 UITheme for colors.
//                 Values marked [SIM] were set from the Monte
//                 Carlo harness (claude/negotiation_tuning_v1);
//                 the rest carry the original v2 values.
// Layer:          Data (constants only)
// Collaborators:  NegotiationState.cs (main consumer),
//                 ArchetypeBehavior (stance bags, NPC pools)
// See:            claude/negotiation_tuning_v1.md for the knob →
//                 metric map, target ranges, and sim method.
// ============================================================

/// <summary>Every tunable constant in the negotiation system, one file.
/// Each section names the metric the knobs move. `const` so values are
/// usable in switch patterns; a rebuild applies changes everywhere.</summary>
public static class NegotiationTuning
{
    // ── Token economy ─────────────────────────────────────────────────────
    // Moves: table length, score ceiling, skill gap. [SIM] With the old
    // economy (1 of each school token, no floor) the player held ~4 tokens
    // vs the NPC's 4-6 pool: 4-5★ deals were MATHEMATICALLY UNREACHABLE
    // (best-case score ≈ +1 vs the 5★ threshold of 8) and skilled play
    // scored only +0.2★ over button-mashing. Doubling school identity and
    // flooring Offering/Persuade puts skilled-median at 3★, naive at ~2★,
    // and makes the patience clock the real constraint.

    /// <summary>[SIM] Copies of each school-innate token (was 1).</summary>
    public const int SchoolTokenCount = 2;

    /// <summary>[SIM] Universal Offering floor: the exchange economy needs
    /// legs for every school, not just Tinker (was 0; Offerings saw 245
    /// plays in 6,000 simulated tables).</summary>
    public const int BaseOfferingFloor = 1;

    /// <summary>[SIM] Universal +Persuade: the generalist argument everyone
    /// can make (was 0).</summary>
    public const int UniversalPersuade = 1;

    // ── Tension zones ─────────────────────────────────────────────────────
    // Moves: how much of the meter is "safe"; Cordial close rate.
    public const int CordialMax = 3;    // tension ≤ this = Cordial
    public const int StrainedMax = 7;   // tension ≤ this = Strained; above = Hostile

    // ── Press stance modifiers (Module A) ─────────────────────────────────
    // Moves: value-of-timing. Bigger spreads = reading stances matters more.
    public const int IrritatedBackfireTension = +1;  // charm into a set jaw
    public const int WaveringEase = -1;              // extra tension ease
    public const int GuardedResent = +1;             // extra tension cost
    public const int ExpansiveEase = -1;
    public const int IntimidateWaveringPull = 2;     // fear lands on the uncertain
    public const int IntimidateWaveringTension = +2;
    public const int IntimidateGuardedTension = +1;  // and hardens the guarded

    // ── Offering ──────────────────────────────────────────────────────────
    // Moves: Offering's identity as the timing token.
    public const int OfferEagerPull = 2;   // double pull on an Eager moment
    public const int OfferEagerEase = -1;  // extra tension ease when Eager

    // ── NPC turn ──────────────────────────────────────────────────────────
    // Moves: how hard the opponent fights back; Hostile's bite.
    public const int PoiseTriggerTension = 9;  // they step back from the brink at ≥ this
    public const int HostilePullSteps = 2;     // their pulls in the Hostile zone
    public const int HardenedBonusSteps = 1;   // extra pull after Intimidate-into-Guarded

    /// <summary>[SIM 2026-07-30] Fairness floor: at table-open, NpcPatience is
    /// raised to at least Resolve+Guile+this. Below that margin the clock ends
    /// the table before skill can beat their pool. Monte Carlo put an
    /// informed bot at −13g vs a Commander at design-doc patience 4. Archetype
    /// personality survives relatively (Commanders still close fastest).</summary>
    public const int PatienceFloorOverPool = 3;

    // ── The squeeze (Module B) ────────────────────────────────────────────
    // Moves: end-of-table drama; hold-firm EV. Blink rates are SHOWN to the
    // player, so these read directly as UI numbers.
    public const int SqueezeOddsCordial = 75;
    public const int SqueezeOddsStrained = 55;
    public const int SqueezeOddsHostile = 30;
    public const int SqueezeOddsWavering = +15;
    public const int SqueezeOddsIrritated = -15;
    public const int SqueezeOddsGuarded = -10;
    public const int SqueezeOddsMin = 5;
    public const int SqueezeOddsMax = 95;
    public const int SqueezeBristleTension = +2;

    // ── Scoring & stars (§7b) ─────────────────────────────────────────────
    // Moves: reward curve. [SIM] With the new economy: skilled-median score
    // ≈ 3, naive ≈ 1, P95 ≈ 6-9. These thresholds put skilled play at 3★,
    // top-quartile at 4★, and keep 5★ genuinely rare. Revisit after human
    // playtests (bots understate real skill).
    public const float CordialGoldMult = 1.2f;
    public const float HostileGoldMult = 0.8f;
    public const int CordialRepBonus = +1;
    public const int HostileRepPenalty = -1;
    public const int ScoreCordialBonus = +2;
    public const int ScoreHostilePenalty = -3;
    public const int StarT5 = 8;
    public const int StarT4 = 5;
    public const int StarT3 = 2;
    public const int StarT2 = -2;

    // ── School signature moves (Phase 5) ─────────────────────────────────
    public const int ShowOfPowerPull = 2;      // Elementalist
    public const int ShowOfPowerTension = +1;
    public const int QuietGroveEase = 2;       // Druid: tension −this
}
