using System.Collections.Generic;

// ============================================================
// CouncilState.cs
//
// Purpose:        Tier 2 data model for the Court & Council
//                 system — courts, courtiers, the favor ledger,
//                 envoy missions, echoes in flight, and queued
//                 interjections. Owned by CycleState (dies with
//                 the timeline; courtiers regenerate each cycle
//                 from the cycle seed). Pure data + derived
//                 standing math; no simulation logic lives here.
//                 The lunation TICK that mutates this state is
//                 build phase C2+ and depends on the Phase 2
//                 world-sim tick.
// Layer:          Data
// Collaborators:  CycleState.cs (owns the Council field),
//                 CourtGenerator.cs (creates + seeds),
//                 KingdomState.cs (kingdom identity, sibling
//                 strategic state), CampaignState.cs (archmage
//                 disposition truth — Seat state is NOT
//                 duplicated here)
// See:            court_council_system_v1_1.docx §3, §4, §12
// Tier rule:      Kassian's next timeline contains none of these
//                 people. Cross-cycle renown annotations belong
//                 in EternalLedger (Hall of Records, phase C6).
// ============================================================

/// <summary>
/// Derived court standing band. NEVER stored — always computed from
/// courtier Regard × Influence via <see cref="CourtState.Band"/>.
/// Gates missions, asks, and the archmage disposition pipeline.
/// </summary>
public enum CourtStandingBand
{
    Unknown,   // no contact yet — only Attend Court may be dispatched
    Hostile,   // score <= -6 — envoys refused, patrol pressure
    Received,  // -5 .. +2   — standard access
    Welcome,   // +3 .. +8   — Petition + intrigue unlock; Coerce path opens
    Favored,   // +9 .. +15  — passage purchasable; Expose the Agent
    Trusted,   // >= +16     — Broker the Compact; free intelligence
}

/// <summary>
/// Shared string vocabulary for the court layer. Strings (not enums)
/// so Archetype ids can match the negotiation system's archetype ids
/// verbatim and authored JSON (name pools, secrets, missions) can
/// reference them without mapping tables.
/// </summary>
public static class CourtVocab
{
    // IMPORTANT: these MUST match the negotiation system's NPC archetype
    // ids exactly — the court layer reuses its matchup semantics. If the
    // negotiation registry uses different casing, fix it HERE, once.
    public static readonly string[] Archetypes =
    {
        "Merchant", "Commander", "Scholar", "Idealist", "Opportunist", "Survivor",
    };

    public const string OfficeChancellor = "Chancellor";
    public const string OfficeMarshal = "Marshal";
    public const string OfficeSpymaster = "Spymaster";
    public const string OfficeCourtWizard = "CourtWizard";
    public const string OfficeSteward = "Steward";
    public const string OfficeFavorite = "Favorite";

    public static readonly string[] Offices =
    {
        OfficeChancellor, OfficeMarshal, OfficeSpymaster,
        OfficeCourtWizard, OfficeSteward, OfficeFavorite,
    };

    // Favor types (§4a). Office determines what a courtier's favor can do.
    public const string FavorMilitary = "Military";
    public const string FavorEconomic = "Economic";
    public const string FavorArcane = "Arcane";
    public const string FavorPolitical = "Political";
    public const string FavorPassage = "Passage";
    public const string FavorIntelligence = "Intelligence";
}

/// <summary>
/// One named courtier at one court. The atomic unit the entire court
/// game moves. Regard is personal stance toward the guild; Influence
/// is weight at court; both are mutated by missions, echoes, and
/// intrigue from phase C2 onward.
/// </summary>
public class CourtierState
{
    public string Id = "";
    public string DisplayName = "";

    /// <summary>Negotiation-system archetype id. Drives gift preferences,
    /// mission-roll matchups, and echo routing (§7).</summary>
    public string Archetype = "";

    /// <summary>Court office (CourtVocab.Offices). Determines what this
    /// courtier's favors can do. One per office per court.</summary>
    public string Office = "";

    /// <summary>Personal stance toward the guild, -3 .. +3.</summary>
    public int Regard = 0;

    /// <summary>Weight at court, 1 .. 3. Moves through intrigue.</summary>
    public int Influence = 2;

    /// <summary>Secret table id. Hidden until discovered via Gather
    /// Intelligence or a Spymaster favor (SecretKnown flips true).</summary>
    public string SecretId = "";
    public bool SecretKnown = false;

    /// <summary>The Astrologer's rival agent (§9). Never true at
    /// generation — manifests during play at kingdom corruption 2.</summary>
    public bool IsCorruptedAgent = false;
}

/// <summary>
/// One kingdom's court. The Seat (archmage or regent) is NOT stored
/// here — archmage disposition truth stays single-sourced in
/// CampaignState, mirroring the corruption single-sourcing rule in
/// KingdomState.
/// </summary>
public class CourtState
{
    public string KingdomId = "";

    /// <summary>True where no archmage was placed. Regent courts cannot
    /// produce alliance/shard outcomes but a Trusted regent court
    /// contributes a minor final-battle asset (v1.1 ruling).</summary>
    public bool IsRegentCourt = false;

    /// <summary>Display name of the mundane regent for regent courts.
    /// Empty for archmage courts (seat name comes from the archmage).</summary>
    public string RegentName = "";

    public List<CourtierState> Courtiers = new();

    /// <summary>Intrigue heat 0..10. Decays -1 per idle lunation.
    /// Thresholds 4 / 7 / 10 = Scandal / Expulsion / Imprisonment.</summary>
    public int Exposure = 0;

    /// <summary>Courted Patron (one per court). Empty until a Court a
    /// Courtier mission completes its Patron Oath (phase C5/C6).</summary>
    public string PatronCourtierId = "";

    /// <summary>False until any mission completes here — the Unknown band.</summary>
    public bool HasContact = false;

    /// <summary>Expulsion lockout. While > 0 the court refuses missions
    /// and the standing band is capped at Received.</summary>
    public int MissionFreezeLunations = 0;

    // ── Derived standing (never serialized as truth) ─────────────────────

    /// <summary>Σ (Regard × Influence) over all courtiers.</summary>
    public int StandingScore()
    {
        int score = 0;
        foreach (var c in Courtiers)
        {
            score += c.Regard * c.Influence;
        }
        return score;
    }

    /// <summary>Standing band per §3b, honoring the Unknown flag and the
    /// expulsion cap (band never exceeds Received while frozen).</summary>
    public CourtStandingBand Band()
    {
        if (!HasContact)
        {
            return CourtStandingBand.Unknown;
        }

        int score = StandingScore();
        CourtStandingBand band;
        if (score <= -6)
        {
            band = CourtStandingBand.Hostile;
        }
        else if (score <= 2)
        {
            band = CourtStandingBand.Received;
        }
        else if (score <= 8)
        {
            band = CourtStandingBand.Welcome;
        }
        else if (score <= 15)
        {
            band = CourtStandingBand.Favored;
        }
        else
        {
            band = CourtStandingBand.Trusted;
        }

        if (MissionFreezeLunations > 0 && band > CourtStandingBand.Received)
        {
            band = CourtStandingBand.Received;
        }
        return band;
    }

    public CourtierState GetCourtier(string courtierId)
    {
        foreach (var c in Courtiers)
        {
            if (c.Id == courtierId)
            {
                return c;
            }
        }
        return null;
    }
}

/// <summary>
/// One discrete favor in the bidirectional ledger (§4). Owed favors
/// the guild fails to repay decay Regard from lunation
/// LunationMinted + 3 onward (phase C3).
/// </summary>
public class Favor
{
    public string Id = "";

    /// <summary>true = a courtier owes the guild; false = the guild owes
    /// the courtier (hospitality, easy negotiation branches, the Black
    /// Market Dealer's future-favor hidden terms land here).</summary>
    public bool OwedToGuild = true;

    public string KingdomId = "";
    public string CourtierId = "";

    /// <summary>CourtVocab favor type — Military, Economic, Arcane,
    /// Political, Passage, Intelligence.</summary>
    public string Type = "";

    public bool IsMajor = false;

    /// <summary>Ledger attribution line shown to the player — every favor
    /// must be traceable to its cause.</summary>
    public string SourceDescription = "";

    /// <summary>Absolute lunation index at minting; drives obligation decay.</summary>
    public int LunationMinted = 0;
}

/// <summary>One dispatched envoy mission. The companion referenced is
/// removed from the active party pool while this exists (phase C2).</summary>
public class EnvoyMission
{
    public string CompanionId = "";
    public string KingdomId = "";
    public string MissionType = "";
    public int LunationsRemaining = 0;

    /// <summary>Target for gifts, courtship, rumor. Empty where the
    /// mission targets the court as a whole.</summary>
    public string TargetCourtierId = "";

    /// <summary>True once recalled — the envoy is 1 lunation from home
    /// and the mission yields nothing.</summary>
    public bool Recalled = false;
}

/// <summary>A deed's echo traveling toward a court (§7). Lands and
/// applies Regard on the lunation tick when LandsOnLunation is reached.</summary>
public class EchoEvent
{
    public string KingdomId = "";

    /// <summary>Deed tag — drives archetype routing and the Herald's
    /// Report attribution line.</summary>
    public string DeedTag = "";

    /// <summary>+1 or -1.</summary>
    public int Valence = 1;

    public bool IsMajor = false;

    /// <summary>Absolute lunation index at which the echo lands.</summary>
    public int LandsOnLunation = 0;

    /// <summary>Set by a Political favor call-in — the Chancellor buries
    /// the story before it lands.</summary>
    public bool Cancelled = false;
}

/// <summary>A queued Tier B interjection — a single-scene choice card.
/// Presented at extraction, at campus, or at the lunation summary;
/// never mid-combat.</summary>
public class InterjectionEvent
{
    public string Id = "";
    public string KingdomId = "";
    public string CompanionId = "";

    /// <summary>Authored event id from the interjection table (phase C2).</summary>
    public string EventId = "";

    /// <summary>Absolute lunation index at which it was raised.</summary>
    public int LunationRaised = 0;
}

/// <summary>
/// Root container for the entire court layer, owned by CycleState.
/// Everything here is timeline-scoped and regenerates each cycle.
/// </summary>
public class CouncilState
{
    /// <summary>Courts keyed by kingdom id. The convergence territory has
    /// no court; every other kingdom has exactly one.</summary>
    public Dictionary<string, CourtState> Courts = new();

    /// <summary>The bidirectional favor ledger (§4). Cleared at cycle end.</summary>
    public List<Favor> Ledger = new();

    /// <summary>Live envoy missions, ticked at lunation boundaries.</summary>
    public List<EnvoyMission> ActiveMissions = new();

    /// <summary>Echoes traveling toward courts (§7).</summary>
    public List<EchoEvent> EchoesInFlight = new();

    /// <summary>FIFO queue of Tier B interjections awaiting presentation.
    /// List rather than Queue for clean System.Text.Json round-trips;
    /// consume from index 0.</summary>
    public List<InterjectionEvent> PendingInterjections = new();
}
