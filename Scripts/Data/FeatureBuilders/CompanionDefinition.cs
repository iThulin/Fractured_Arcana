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

    // ── Injury (K2, §5b) ─────────────────────────────────────────────────
    // Lunations left in the infirmary. >0 = excluded from all three demands
    // (expedition, court dispatch; recovery IS the third). Serialized;
    // round-trip asserted in CompanionInjurySystem.
    public int InjuredLunationsRemaining = 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInjured => InjuredLunationsRemaining > 0;

    // ── Expedition HP (K2.5 ruling, 2026-07-09) ──────────────────────────
    // Combat HP persists BETWEEN fights within one expedition: unit HP is
    // the fights, the party pool is the journey. -1 = fresh/full. 0 = downed
    // in a WON fight: stabilized, cannot field again this expedition.
    // Set on combat end, consumed at spawn, resolved by the extraction
    // infirmary check (below 25% of BaseHP → recovery time), reset at
    // expedition launch/end. Serialized for mid-expedition saves.
    public int ExpeditionHP = -1;

    // ── Unit class ───────────────────────────────────────────────────────
    // "Arcane" = wizard-type (card-based, has mana)
    // "Fighter" = melee martial
    // "Ranger" = ranged martial
    // "None" = unclassed levy (default for new companions)
    public string UnitClass = "None";

    // ── Combat stats (base values at levy tier) ──────────────────────────
    // Wizards: these are ignored; wizard stats come from PlayerSession/school
    // Martials: these are the starting levy stats, boosted by Training Grounds
    public int BaseHP = 12;
    public int BaseSpeed = 2;
    public int BaseArmor = 0;
    public int BaseAttackDamage = 3;
    public int BaseAttackRange = 1;
    public int BaseMana = 0;   // always 0 for martials

    // ── Martial progression (saved per companion) ────────────────────────
    // Which stances this companion has been trained in at the campus.
    // Populated by the Training tab, not from JSON templates.
    [System.Text.Json.Serialization.JsonPropertyName("availableStanceIds")]
    public List<string> TrainedStanceIds = new();

    // AP pool, set by Training Grounds tier at run start
    // Stored here so the UI can show it between runs
    public int BaseActionPoints = 3; // levy default

    // ── Signature override (K4, v1's signatureId hook) ───────────────────
    // Authored companions may name a bespoke signature stance id here (JSON
    // "signatureStanceId"). Empty = the Class × Trait matrix id. The GRANT
    // is always derived (StanceRegistry.EligibleSignature); this field only
    // redirects WHICH signature, never whether one is fielded.
    public string SignatureStanceId = "";

    // ── Runtime state (not in JSON, set during combat) ───────────────────
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

    // ── Loyalty tiers (K1, 2026-07-09) ───────────────────────────────────
    // Bands per companion_item_systems v1 (locked, carried in v2.1 §2):
    // Wary 0–24 / Hired 25–49 / Trusted 50–74 / Devoted / Sworn.
    // ASSUMPTION (flagged in docs/k1_verification.md): the docs never pin the
    // Devoted/Sworn split numerically; 75–89 / 90–100 are K1 starting values.
    // Tune HERE; K4 (Trusted perks, Sworn signatures) and §6 envoy fitness
    // must read tiers through this same helper, never re-derive.
    public const int TrustedThreshold = 50;
    public const int DevotedThreshold = 75;
    public const int SwornThreshold = 90;

    public LoyaltyTier GetLoyaltyTier() => TierOfValue(Loyalty);

    /// <summary>The ONE place a loyalty value becomes a tier (K4: extracted so
    /// LoyaltyEvents' before/after tier reporting reads through the same
    /// thresholds instead of re-deriving them).</summary>
    public static LoyaltyTier TierOfValue(int loyalty) =>
        loyalty >= SwornThreshold ? LoyaltyTier.Sworn
        : loyalty >= DevotedThreshold ? LoyaltyTier.Devoted
        : loyalty >= TrustedThreshold ? LoyaltyTier.Trusted
        : loyalty >= 25 ? LoyaltyTier.Hired
        : LoyaltyTier.Wary;

    /// <summary>§4a pool-HP loyalty bonus: Devoted +2, Sworn +4, "the personal
    /// ceiling made literal." All other tiers contribute no bonus.</summary>
    public int LoyaltyPoolBonus() => GetLoyaltyTier() switch
    {
        LoyaltyTier.Sworn => 4,
        LoyaltyTier.Devoted => 2,
        _ => 0,
    };
}

/// <summary>Companion loyalty bands. See Companion.GetLoyaltyTier for the
/// thresholds and the K1 assumption on the Devoted/Sworn split.</summary>
public enum LoyaltyTier { Wary, Hired, Trusted, Devoted, Sworn }