using System.Collections.Generic;

// ============================================================
// NpcArchetype.cs
//
// Purpose:        Negotiation enums + small data classes:
//                 LeverageToken, NpcArchetypeType, TensionZone,
//                 NpcStance, NpcResource, DealTerm,
//                 NegotiationEncounterData. The full negotiation
//                 system's data model lives here.
// Layer:          Data
// Collaborators:  NegotiationState.cs (consumer),
//                 NegotiationManager.cs (UI),
//                 NegotiationBarks.cs (spoken-move lines),
//                 NegotiationEncounterLoader.cs (parser)
// See:            README §6 on Negotiation;
//                 negotiation_redesign_v1.md (v2 core loop)
// ============================================================

/// <summary>Token types the player can spend during a negotiation. Mapped to player actions ("Charm the merchant", "Intimidate the commander") and matched against the NPC archetype's preferred-token profile.</summary>
public enum LeverageToken
{
    Charm,
    Intimidate,
    Persuade,
    Insight,
    Connections,
    Patience,
    Offering,
    Demonstration
}

/// <summary>
/// NPC archetype determines behavior, patience, and tension responses.
/// </summary>
public enum NpcArchetypeType
{
    Merchant,
    Commander,
    Scholar,
    Opportunist,
    Idealist,
    Survivor
}

/// <summary>
/// Tension zones per the design doc.
/// </summary>
public enum TensionZone { Cordial, Strained, Hostile }

/// <summary>v2 (Module A): the NPC's per-round mood. Shown via the portrait
/// slot + a one-line tell; modifies how every token lands, so the same token
/// is worth different amounts at different moments. Rolled each round from a
/// zone-weighted bag; the NEXT stance is pre-rolled so Insight's mood-read
/// is honest.</summary>
public enum NpcStance
{
    Eager,      // wants what you have; Offerings land double
    Guarded,    // giving nothing away; pressure resented, gifts pocketed coldly
    Wavering,   // uncertain; social pressure lands best here
    Irritated,  // one wrong word from bristling; charm backfires
    Expansive   // Cordial-only warmth; everything social lands softer
}

/// <summary>v2: the NPC's own leverage pool. They spend against you every
/// turn. Resolve pulls terms back (fed by your Offerings: tokens literally
/// cross the table), Guile makes demands / stirs tension, Poise steps them
/// back from the brink at 9-10 tension.</summary>
public enum NpcResource { Resolve, Guile, Poise }

/// <summary>
/// A single deal term on the table.
/// </summary>
public class DealTerm
{
    public string Id = "";
    public string Description = "";
    public bool FavorPlayer = true;    // true = good for player, false = costs something
    public bool IsHidden = false;       // revealed by Insight tokens
    public bool IsAccepted = false;

    // Outcomes when accepted
    public int GoldDelta = 0;
    public int ReputationDelta = 0;
    public string FactionId = "";
    public string LoreUnlock = "";
    public int StepsDelta = 0;

    /// <summary>Supplies this clause moves (docs/supply_cache_spec_v1).
    /// Positive pays the guild, negative pledges from its stores. Settled on
    /// return like gold (ExpeditionManager.OnNegotiationReturned): gains ride
    /// the expedition at risk; costs deduct from the treasury immediately,
    /// floored at 0. No zone multiplier, because provisions are physical goods and the
    /// crates don't multiply because the talk went well. Authorable in JSON as
    /// "suppliesDelta"; weighted in DeriveWeight so the NPC AI values supply
    /// clauses (unlike the dead StepsDelta/LoreUnlock precedent).</summary>
    public int SuppliesDelta = 0;

    /// <summary>Supply-lines intel (supply_cache spec v1.1): when this term is
    /// part of a signed deal (and not fully conceded away), every supply cache
    /// in the negotiation's origin kingdom is revealed on the strategic map.
    /// Diplomacy as a discovery channel. Injected dynamically at table-open
    /// ("supply_lines_intel"); settled in ExpeditionManager.OnNegotiationReturned
    /// via NegotiationContext.RevealSupplyCaches.</summary>
    public bool RevealsSupplyCaches = false;

    /// <summary>S4 (overworld_spell_system §11): an overworld spell id this
    /// term teaches. Granted ONLY when the deal closes in the Cordial zone
    /// (NegotiationState.GetSpellOutcome). "Cordial deals" are the social
    /// route to spells, and the payoff for managing tension. Authorable in
    /// encounter JSON; also injected dynamically at table-open as the
    /// "spell_tuition" term (NegotiationManager).</summary>
    public string SpellId = "";

    // ── v2 term board (negotiation_redesign_v1 §3a) ──────────────────────
    /// <summary>Authorable starting slider position, −2 (strongly favors the
    /// NPC) … +2 (strongly favors you). Old JSONs omit it; the state machine
    /// then defaults every term to −1 (their opening offer shortchanges you /
    /// their demand is near full force).</summary>
    public int StartingPosition = UNAUTHORED;
    public const int UNAUTHORED = -99;

    /// <summary>Authorable scoring weight (how much this clause matters).
    /// 0 = derive from the magnitude of the term's deltas at init.</summary>
    public int Weight = 0;

    /// <summary>Live slider position during play. Runtime only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Position = 0;

    /// <summary>Runtime: sealed while the table is Hostile (the NPC guards
    /// their most-conceded clause). Cleared when tension cools.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Locked = false;

    /// <summary>How much of this term the player walks away with, from the
    /// final slider position: favorable terms pay more as they're pulled
    /// toward you; unfavorable terms cost less. ∈ {0, .25, .5, .75, 1}.</summary>
    public float PlayerFraction()
    {
        float pulled = (Position + 2) / 4f;          // 0 at −2 … 1 at +2
        return FavorPlayer ? pulled : 1f - pulled;   // unfavorable: pulled = defanged
    }
}

/// <summary>
/// Full definition of a negotiation encounter loaded from JSON.
/// </summary>
public class NegotiationEncounterData
{
    public string Id = "";
    public string Title = "";
    public string OpeningText = "";     // NPC's opening statement
    public string NpcName = "";
    public NpcArchetypeType Archetype = NpcArchetypeType.Merchant;
    public string FactionId = "";

    // Starting tension (0 = use faction reputation, else override)
    public int StartingTension = 4;
    public int BasePatience = 8;

    // Terms on the table
    public List<DealTerm> Terms = new();

    // ── v2: the NPC's own pool (negotiation_redesign_v1 §3b). −1 = use the
    // archetype default from ArchetypeBehavior.DefaultNpcPool. ─────────────
    public int NpcResolve = -1;
    public int NpcGuile = -1;
    public int NpcPoise = -1;

    /// <summary>negotiation_system.docx, Resolution Check: "Escalation: tension
    /// is at 10 and the NPC archetype is aggressive (Commander, some
    /// Opportunists), triggering combat." That branch was specced and never
    /// implemented. A table at maximum tension simply closed as "Collapsed".
    ///
    /// null (the JSON omits the key) = use the archetype default resolved by
    /// <see cref="Escalates"/>. true / false = an explicit per-table override.
    /// Every pre-existing encounter JSON deserializes unchanged.</summary>
    public bool? EscalatesToCombat = null;

    /// <summary>Whether a collapse at this table becomes a fight. The spec names
    /// Commanders outright and only "some Opportunists", so the default escalates
    /// Commanders and nobody else: an Opportunist table has to opt in explicitly
    /// rather than surprising the player with steel. Never escalates a Merchant,
    /// Scholar, Idealist or Survivor by default: those tables ending badly is a
    /// door closing, not a weapon coming out.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Escalates =>
        EscalatesToCombat ?? (Archetype == NpcArchetypeType.Commander);

    // NPC dialogue lines per situation
    public string DialogueCordial = "";
    public string DialogueStrained = "";
    public string DialogueHostile = "";
    public string DialogueWalkaway = "";
    public string DialogueAccept = "";
}

/// <summary>
/// Archetype behavior rules: how NPCs respond to each token type.
/// </summary>
public static class ArchetypeBehavior
{
    /// <summary>
    /// Base tension delta when the player plays a token against this archetype
    /// (before the stance modifier; see NegotiationState).
    /// Negative = toward Cordial, positive = toward Hostile.
    /// </summary>
    public static int GetTensionDelta(NpcArchetypeType archetype, LeverageToken token)
    {
        return (archetype, token) switch
        {
            // Charm
            (NpcArchetypeType.Idealist,  LeverageToken.Charm) => -2,
            (NpcArchetypeType.Commander, LeverageToken.Charm) => 0,
            (_,                          LeverageToken.Charm) => -1,

            // Intimidate
            (NpcArchetypeType.Idealist,  LeverageToken.Intimidate) => 10,
            (NpcArchetypeType.Commander, LeverageToken.Intimidate) => 1,
            (NpcArchetypeType.Scholar,   LeverageToken.Intimidate) => 3,
            (_,                          LeverageToken.Intimidate) => 2,

            // Persuade
            (NpcArchetypeType.Scholar,    LeverageToken.Persuade) => -2,
            (NpcArchetypeType.Opportunist,LeverageToken.Persuade) => 0,
            (_,                           LeverageToken.Persuade) => -1,

            // Insight: no tension effect
            (_, LeverageToken.Insight)  => 0,

            // Connections
            (_, LeverageToken.Connections) => -1,

            // Patience: no tension effect
            (_, LeverageToken.Patience) => 0,

            // Offering
            (NpcArchetypeType.Merchant, LeverageToken.Offering) => -2,
            (_,                         LeverageToken.Offering) => -1,

            // Demonstration
            (NpcArchetypeType.Commander,  LeverageToken.Demonstration) => -1,
            (NpcArchetypeType.Scholar,    LeverageToken.Demonstration) => -1,
            (NpcArchetypeType.Idealist,   LeverageToken.Demonstration) =>  1,
            (_,                           LeverageToken.Demonstration) =>  0,

            // Fallback
            _ => 0
        };
    }

    /// <summary>v2: archetype defaults for the NPC's own pool. Authorable
    /// per-encounter via NegotiationEncounterData.NpcResolve/Guile/Poise.</summary>
    public static (int resolve, int guile, int poise) DefaultNpcPool(NpcArchetypeType a)
    {
        return a switch
        {
            NpcArchetypeType.Merchant    => (2, 2, 1),
            NpcArchetypeType.Commander   => (3, 1, 1),
            NpcArchetypeType.Scholar     => (1, 2, 2),
            NpcArchetypeType.Opportunist => (2, 3, 0),
            NpcArchetypeType.Idealist    => (1, 1, 2),
            NpcArchetypeType.Survivor    => (2, 1, 2),
            _                            => (2, 2, 1),
        };
    }

    /// <summary>v2: what the NPC's Resolve is CALLED at this table. Pure
    /// display flavor over the same mechanic ("Greed" for a merchant is
    /// "Duty" for a commander).</summary>
    public static string ResolveDisplayName(NpcArchetypeType a)
    {
        return a switch
        {
            NpcArchetypeType.Merchant    => "Greed",
            NpcArchetypeType.Commander   => "Duty",
            NpcArchetypeType.Scholar     => "Rigor",
            NpcArchetypeType.Opportunist => "Angle",
            NpcArchetypeType.Idealist    => "Conviction",
            NpcArchetypeType.Survivor    => "Wariness",
            _                            => "Resolve",
        };
    }

    /// <summary>v2 (Module A): roll a stance from a zone-weighted bag.
    /// Expansive only appears in Cordial; Irritated dominates Hostile.
    /// `roll` is an externally supplied random uint (GD.Randi) so this
    /// stays deterministic under test.</summary>
    public static NpcStance RollStance(TensionZone zone, uint roll)
    {
        NpcStance[] bag = zone switch
        {
            TensionZone.Cordial => new[]
            {
                NpcStance.Expansive, NpcStance.Expansive, NpcStance.Eager,
                NpcStance.Wavering,  NpcStance.Wavering,  NpcStance.Guarded
            },
            TensionZone.Hostile => new[]
            {
                NpcStance.Irritated, NpcStance.Irritated, NpcStance.Guarded,
                NpcStance.Guarded,   NpcStance.Eager,     NpcStance.Wavering
            },
            _ => new[]
            {
                NpcStance.Eager,     NpcStance.Eager,    NpcStance.Guarded,
                NpcStance.Guarded,   NpcStance.Wavering, NpcStance.Irritated
            },
        };
        return bag[roll % (uint)bag.Length];
    }

    /// <summary>v2: token the NPC gifts the player during a Cordial hold
    /// (their goodwill move), archetype-flavored.</summary>
    public static LeverageToken GiftTokenFor(NpcArchetypeType a)
    {
        return a switch
        {
            NpcArchetypeType.Merchant    => LeverageToken.Offering,
            NpcArchetypeType.Commander   => LeverageToken.Demonstration,
            NpcArchetypeType.Scholar     => LeverageToken.Insight,
            NpcArchetypeType.Opportunist => LeverageToken.Connections,
            NpcArchetypeType.Idealist    => LeverageToken.Charm,
            NpcArchetypeType.Survivor    => LeverageToken.Patience,
            _                            => LeverageToken.Connections,
        };
    }

    /// <summary>
    /// Returns a description of what happens when this token is played.
    /// (Legacy flat table, kept for chips mode; spoken-move lines live in
    /// NegotiationBarks.)
    /// </summary>
    public static string GetTokenEffect(NpcArchetypeType archetype, LeverageToken token)
    {
        return (archetype, token) switch
        {
            (NpcArchetypeType.Idealist, LeverageToken.Intimidate) =>
                "The Idealist is deeply offended. They're walking away.",
            (NpcArchetypeType.Commander, LeverageToken.Charm) =>
                "The Commander is unmoved. Flattery doesn't impress them.",
            (NpcArchetypeType.Merchant, LeverageToken.Offering) =>
                "The Merchant's eyes light up. A tangible offer. This is something they can work with.",
            (NpcArchetypeType.Scholar, LeverageToken.Persuade) =>
                "A well-reasoned argument. The Scholar leans forward, genuinely engaged.",
            (NpcArchetypeType.Commander, LeverageToken.Intimidate) =>
                "The Commander meets your gaze steadily. They respect directness.",
            (_, LeverageToken.Charm) => "You apply social grace. The mood softens slightly.",
            (_, LeverageToken.Intimidate) => "You make your position clear. Tension rises.",
            (_, LeverageToken.Persuade) => "You present your argument carefully.",
            (_, LeverageToken.Insight) => "You probe for hidden information.",
            (_, LeverageToken.Connections) => "You invoke a mutual connection.",
            (_, LeverageToken.Patience) => "You hold your ground without pressing.",
            (_, LeverageToken.Offering) => "You place something of value on the table.",
            (_, LeverageToken.Demonstration) => "You demonstrate your capabilities.",
            _ => "You make your move."
        };
    }

    /// <summary>
    /// What does the NPC say on their response turn, by zone?
    /// </summary>
    public static string GetNpcResponse(
        NpcArchetypeType archetype, TensionZone zone, NegotiationEncounterData data)
    {
        return zone switch
        {
            TensionZone.Cordial  => data.DialogueCordial,
            TensionZone.Hostile  => data.DialogueHostile,
            _                    => data.DialogueStrained
        };
    }
}
