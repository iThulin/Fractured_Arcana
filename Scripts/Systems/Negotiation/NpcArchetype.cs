using System.Collections.Generic;

// ============================================================
// NpcArchetype.cs
//
// Purpose:        Negotiation v3 ("The Ledger") data model:
//                 LeverageToken, NpcArchetypeType, NegotiationMood,
//                 Clause (+ ClauseSide/Kind/State), the legacy
//                 v2 DealTerm shape (parsed only so old JSON
//                 still loads through the migration shim),
//                 NegotiationEncounterData, and the archetype
//                 tables (ArchetypeBehavior).
// Layer:          Data
// Collaborators:  NegotiationState.cs (consumer),
//                 NegotiationManager.cs (UI),
//                 NegotiationBarks.cs (reaction lines),
//                 NegotiationEncounterLoader.cs (parser + shim)
// See:            docs/negotiation_ledger_spec_v1.md
// ============================================================
//
// v3 notes: stances, the NPC Resolve/Guile/Poise pool, term
// positions/weights, Hostile seals and the tension meter are gone.
// A clause is a concrete thing that changes hands; the only hidden
// state on the table is how much the NPC cares about each clause.

/// <summary>Token types the player can spend during a negotiation.
/// Sources (school innates, companions, buildings) are unchanged from v2;
/// what each token DOES is v3 (spec §3).</summary>
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

/// <summary>NPC archetype: valuation profile, patience, counter style,
/// creed clashes, sweetener.</summary>
public enum NpcArchetypeType
{
    Merchant,
    Commander,
    Scholar,
    Opportunist,
    Idealist,
    Survivor
}

/// <summary>The relationship temperature, derived from Goodwill only
/// (spec §2c). Warm is the v3 meaning of "Cordial" for every integration
/// that used to read the tension zone.</summary>
public enum NegotiationMood { Cold, Even, Warm }

/// <summary>Who holds the clause at table-open. Theirs = you may Ask for
/// it; Yours = you may Concede it.</summary>
public enum ClauseSide { Theirs, Yours }

/// <summary>Authoring convenience: drives the archetype default valuation
/// (ArchetypeBehavior.DefaultNpcValue) and the reaction barks.</summary>
public enum ClauseKind { Gold, Goods, Lore, Service, Passage, Honor, Favor, Obligation, Spell }

/// <summary>Runtime state of a clause. Refused = they flat-refused it and it
/// cannot be Asked again this table (spec §13 ruling 2). Struck = removed by
/// Persuade (a rider argued off its parent).</summary>
public enum ClauseState { Open, Agreed, Struck, Refused }

/// <summary>How the NPC picks the concession they name in a counter-proposal
/// (spec §4b). Fair: the smallest of your open clauses that covers the
/// deficit. Greedy: the one they value most.</summary>
public enum CounterStyle { Fair, Greedy }

/// <summary>One concrete thing on the table. Every field the player can see
/// is visible from the first frame; <see cref="NpcValue"/> is the only
/// hidden number (spec §2a).</summary>
public class Clause
{
    public string Id = "";

    /// <summary>What it is, in plain words. Shown on the slip.</summary>
    public string Text = "";

    /// <summary>Authored display handle for barks and the receipt: a lowercase
    /// noun phrase, one to three words, no leading article, no trailing
    /// punctuation; must read inside "the {clause}" frames. Empty = derived
    /// fallback (NegotiationState.ShortName), which the validator flags.</summary>
    public string ShortName = "";

    public ClauseSide Side = ClauseSide.Theirs;
    public ClauseKind Kind = ClauseKind.Gold;

    /// <summary>What it is worth to YOU (1–6). Authored; always shown.</summary>
    public int Value = 1;

    /// <summary>What it is worth to THEM (0–5). Hidden until probed, revealed
    /// by their reactions, or shown by a school move. −1 = use the archetype
    /// default for <see cref="Kind"/>.</summary>
    public int NpcValue = -1;

    /// <summary>Id of a <c>yours</c>-side clause that must be in the package if
    /// this one is: "the introduction comes with a favor owed." Surfaces the
    /// moment you Ask for this clause, never after signing (spec §1.3).</summary>
    public string Rider = "";

    /// <summary>Only signable if the mood is Warm at the handshake. The S4
    /// tuition clause and Cordial-only extras use this.</summary>
    public bool RequiresWarm = false;

    // ── Payload: the reward verbs (spec §2f) ─────────────────────────────
    public int GoldDelta = 0;
    public int ReputationDelta = 0;
    public string FactionId = "";
    public string LoreUnlock = "";

    /// <summary>Expedition fuel ± this run (was StepsDelta; the legacy JSON key
    /// "stepsDelta" still parses through the shim).</summary>
    public int FuelDelta = 0;

    /// <summary>Supplies moved (docs/supply_cache_spec_v1). Gains ride the
    /// expedition at risk; costs deduct on return.</summary>
    public int SuppliesDelta = 0;

    /// <summary>Supply-lines intel: every cache in the origin kingdom is
    /// revealed on return. Injected as the "supply_lines_intel" clause.</summary>
    public bool RevealsSupplyCaches = false;

    /// <summary>S4: overworld spell taught. Authored, or injected as
    /// "spell_tuition" (always RequiresWarm).</summary>
    public string SpellId = "";

    /// <summary>Chart: writes Charted on a hex disc of this radius around the
    /// negotiation's hex (Unseen → Charted only). 0 = none.</summary>
    public int ChartRadius = 0;

    /// <summary>Reveal: discover one undiscovered POI of each listed kind in
    /// the origin kingdom (PoiKind names, e.g. "Narrative", "SupplyCache"),
    /// charting radius 3 around each, the same path the Spymaster packet uses.</summary>
    public List<string> RevealPoiKinds = new();

    /// <summary>Anchor: the negotiation's hex becomes a supply anchor for the
    /// rest of this expedition (extends the leash; free extraction there).</summary>
    public bool SupplyAnchorHere = false;

    /// <summary>Safe conduct: every patrol stands down for this many party
    /// steps (the Passage favor mechanism). 0 = none.</summary>
    public int SafeConductSteps = 0;

    // ── Runtime (never serialized) ───────────────────────────────────────
    [System.Text.Json.Serialization.JsonIgnore] public ClauseState State = ClauseState.Open;
    /// <summary>The player knows this clause's NpcValue EXACTLY (a school
    /// move, or the clause is agreed). v3.1: most reads give a band instead.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Revealed = false;
    /// <summary>The player knows the BAND of this clause's NpcValue
    /// (cheap / fair / dear) — a Read card, an Argue, a courier line.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool BandKnown = false;
    /// <summary>Anything is known about their valuation (band or exact).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool AnyKnown => Revealed || BandKnown;
    /// <summary>v3.3 Contested Table: where the open term sits on the track.
    /// −1 theirs · 0 contested · +1 yours ("yours" = ends in your hands).
    /// Meaningless once locked (agreed / refused / struck).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int Position = 0;
    /// <summary>They pulled this term toward them at least once this table
    /// (a concession of it is worth an extra goodwill).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool PulledByThem = false;
    /// <summary>Persuade has already been spent on it (once per clause).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Argued = false;
    /// <summary>Enchanter's Beguiling Weave: its NpcValue no longer counts against Credit.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Beguiled = false;
    /// <summary>True for clauses added during play (purse, demonstration, twist).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Ephemeral = false;

    [System.Text.Json.Serialization.JsonIgnore] public bool IsOpen => State == ClauseState.Open;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsAgreed => State == ClauseState.Agreed;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasRider => !string.IsNullOrEmpty(Rider);
    [System.Text.Json.Serialization.JsonIgnore] public bool HasPayload =>
        GoldDelta != 0 || ReputationDelta != 0 || FuelDelta != 0 || SuppliesDelta != 0 ||
        RevealsSupplyCaches || !string.IsNullOrEmpty(SpellId) || !string.IsNullOrEmpty(LoreUnlock) ||
        ChartRadius > 0 || RevealPoiKinds.Count > 0 || SupplyAnchorHere || SafeConductSteps > 0;
}

/// <summary>The v2 term shape, parsed ONLY so pre-v3 encounter JSON keeps
/// loading. NegotiationEncounterLoader.MigrateLegacy turns these into
/// Clauses (spec §10) and logs a warning so the file gets rewritten.</summary>
public class LegacyDealTerm
{
    public string Id = "";
    public string Description = "";
    public string ShortName = "";
    public string RumorText = "";
    public bool FavorPlayer = true;
    public bool IsHidden = false;
    public int GoldDelta = 0;
    public int ReputationDelta = 0;
    public string FactionId = "";
    public string LoreUnlock = "";
    public int StepsDelta = 0;
    public int SuppliesDelta = 0;
    public bool RevealsSupplyCaches = false;
    public string SpellId = "";
    public int StartingPosition = -99;
    public int Weight = 0;
}

/// <summary>Full definition of a negotiation encounter loaded from JSON.</summary>
public class NegotiationEncounterData
{
    public string Id = "";
    public string Title = "";
    public string OpeningText = "";

    /// <summary>Optional late-campaign opening (negotiation_narrative_spec_v1
    /// §6d): used once CycleState.CampaignYear >= 2.</summary>
    public string OpeningTextLate = "";
    public string DialogueWalkawayLate = "";

    public string NpcName = "";
    public NpcArchetypeType Archetype = NpcArchetypeType.Merchant;
    public string FactionId = "";

    /// <summary>Authored patience; −1 = archetype default (spec §5b).</summary>
    public int BasePatience = -1;

    /// <summary>Authored goodwill before faction/archetype modifiers. The
    /// neutral baseline is NegotiationTuning.BaseGoodwill (3).</summary>
    public int StartingGoodwill = -1;

    /// <summary>"fair" / "greedy"; empty = archetype default. JSON key
    /// "counterStyle" (the field is named apart from the enum).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("counterStyle")]
    public string CounterStyleId = "";

    /// <summary>Schools this counterpart distrusts (creed clash → Grievance →
    /// the closing squeeze can fire). null = archetype default; an empty
    /// list = nobody.</summary>
    public List<string> DistrustsSchools = null;

    /// <summary>v3 clauses.</summary>
    public List<Clause> Clauses = new();

    /// <summary>Optional: something true about the counterpart that they
    /// only say once the table turns Warm (spec §2c, the Warm reveal). The
    /// v2 "rumour" hidden terms that were narrative truths rather than
    /// obligations live here now.</summary>
    public string Confidence = "";

    /// <summary>v2 terms, parsed for the migration shim only. Empty in
    /// rewritten files.</summary>
    public List<LegacyDealTerm> Terms = new();

    /// <summary>Whether a collapse at this table becomes a fight. null = the
    /// archetype default resolved by <see cref="Escalates"/>.</summary>
    public bool? EscalatesToCombat = null;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Escalates =>
        EscalatesToCombat ?? (Archetype == NpcArchetypeType.Commander);

    // Legacy per-zone lines are still parsed (old files) but v3 only speaks
    // Walkaway and Accept; Warm/Cold flavour comes from the bark tables.
    public string DialogueCordial = "";
    public string DialogueStrained = "";
    public string DialogueHostile = "";
    public string DialogueWalkaway = "";
    public string DialogueAccept = "";

    // Legacy v2 fields, parsed and ignored.
    public int StartingTension = 4;
    public int NpcResolve = -1;
    public int NpcGuile = -1;
    public int NpcPoise = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    public CounterStyle ResolvedCounterStyle =>
        CounterStyleId?.ToLowerInvariant() == "fair" ? CounterStyle.Fair
      : CounterStyleId?.ToLowerInvariant() == "greedy" ? CounterStyle.Greedy
      : ArchetypeBehavior.DefaultCounterStyle(Archetype);

    [System.Text.Json.Serialization.JsonIgnore]
    public int ResolvedPatience =>
        BasePatience > 0 ? BasePatience : ArchetypeBehavior.DefaultPatience(Archetype);

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ResolvedDistrusts =>
        DistrustsSchools ?? ArchetypeBehavior.DefaultDistrusts(Archetype);
}

/// <summary>Archetype tables (spec §5). Every number here is a starting
/// value for playtesting; the par-relative star ladder keeps mis-tuned
/// authoring from making stars unreachable.</summary>
public static class ArchetypeBehavior
{
    /// <summary>§5a default npcValue by (archetype, kind).</summary>
    public static int DefaultNpcValue(NpcArchetypeType a, ClauseKind k)
    {
        // Column order: gold, goods, lore, service, passage, honor, favor, obligation, spell
        int[] row = a switch
        {
            NpcArchetypeType.Merchant    => new[] { 4, 3, 1, 2, 2, 1, 3, 3, 1 },
            NpcArchetypeType.Commander   => new[] { 2, 2, 1, 4, 3, 3, 2, 4, 1 },
            NpcArchetypeType.Scholar     => new[] { 1, 1, 4, 2, 1, 2, 2, 2, 4 },
            NpcArchetypeType.Opportunist => new[] { 3, 3, 2, 2, 2, 0, 4, 3, 2 },
            NpcArchetypeType.Idealist    => new[] { 1, 1, 2, 3, 2, 4, 2, 1, 2 },
            NpcArchetypeType.Survivor    => new[] { 4, 4, 1, 3, 3, 2, 1, 2, 1 },
            _                            => new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2 },
        };
        int i = (int)k;
        return i >= 0 && i < row.Length ? row[i] : 2;
    }

    /// <summary>§5c value of a Demonstration clause by (archetype × school).
    /// A Demonstration costs the player 0 value, so a 3–4 here is nearly
    /// free Credit: the school-fit payoff.</summary>
    public static int DemonstrationValue(NpcArchetypeType a, CardSchool s)
    {
        // Column order: Adept, Elementalist, Druid, Necromancer, Tinker, Enchanter, Arcanist, Chronomancer
        int[] row = a switch
        {
            NpcArchetypeType.Merchant    => new[] { 1, 2, 1, 0, 3, 2, 1, 2 },
            NpcArchetypeType.Commander   => new[] { 1, 4, 1, 2, 3, 0, 1, 1 },
            NpcArchetypeType.Scholar     => new[] { 2, 1, 2, 1, 3, 1, 4, 4 },
            NpcArchetypeType.Opportunist => new[] { 1, 2, 1, 2, 3, 3, 1, 2 },
            NpcArchetypeType.Idealist    => new[] { 2, 1, 4, 0, 1, 1, 1, 2 },
            NpcArchetypeType.Survivor    => new[] { 1, 3, 4, 0, 3, 1, 1, 1 },
            _                            => new[] { 1, 1, 1, 1, 1, 1, 1, 1 },
        };
        int i = s switch
        {
            CardSchool.Adept => 0,
            CardSchool.Elementalist => 1,
            CardSchool.Druid => 2,
            CardSchool.Necromancer => 3,
            CardSchool.Tinker => 4,
            CardSchool.Enchanter => 5,
            CardSchool.Arcanist => 6,
            CardSchool.Chronomancer => 7,
            _ => 0,
        };
        return row[i];
    }

    /// <summary>§5b default patience: the number of turn-costing actions
    /// before they leave.</summary>
    public static int DefaultPatience(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => 8,
        NpcArchetypeType.Commander   => 6,
        NpcArchetypeType.Scholar     => 10,
        NpcArchetypeType.Opportunist => 6,
        NpcArchetypeType.Idealist    => 7,
        NpcArchetypeType.Survivor    => 5,
        _                            => 8,
    };

    /// <summary>§5b starting-goodwill modifier.</summary>
    public static int StartGoodwillMod(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Opportunist => -1,
        NpcArchetypeType.Idealist    => +1,
        NpcArchetypeType.Survivor    => +2,
        _                            => 0,
    };

    public static CounterStyle DefaultCounterStyle(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => CounterStyle.Greedy,
        NpcArchetypeType.Opportunist => CounterStyle.Greedy,
        _                            => CounterStyle.Fair,
    };

    private static readonly string[] None = System.Array.Empty<string>();
    private static readonly string[] DistrustNecro = { "Necromancer" };
    private static readonly string[] DistrustNecroEnch = { "Necromancer", "Enchanter" };

    /// <summary>§5b default creed clashes (the Grievance gate).</summary>
    public static IReadOnlyList<string> DefaultDistrusts(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Commander => DistrustNecroEnch,
        NpcArchetypeType.Scholar   => DistrustNecro,
        NpcArchetypeType.Idealist  => DistrustNecroEnch,
        NpcArchetypeType.Survivor  => DistrustNecro,
        _                          => None,
    };

    /// <summary>The sweetener a Warm close adds (spec §2c): a real clause with
    /// a real payload, archetype-flavoured, agreed at signing and applied
    /// through the normal reward verbs. Nothing here needs a "next table"
    /// memory the game doesn't have.</summary>
    public static Clause SweetenerClause(NpcArchetypeType a)
    {
        var c = new Clause
        {
            Id = "sweetener", Side = ClauseSide.Theirs, Value = 1, NpcValue = 0,
            State = ClauseState.Agreed, Revealed = true, Ephemeral = true,
        };
        switch (a)
        {
            case NpcArchetypeType.Merchant:
                c.Text = "A purse for the road"; c.ShortName = "road purse";
                c.Kind = ClauseKind.Gold; c.GoldDelta = 15; break;
            case NpcArchetypeType.Commander:
                c.Text = "An escort to the next ridge"; c.ShortName = "escort";
                c.Kind = ClauseKind.Passage; c.FuelDelta = 3; break;
            case NpcArchetypeType.Scholar:
                c.Text = "Their annotated map of the district"; c.ShortName = "annotated map";
                c.Kind = ClauseKind.Lore; c.ChartRadius = 4; break;
            case NpcArchetypeType.Opportunist:
                c.Text = "A word in the right ear: the patrols look away"; c.ShortName = "quiet word";
                c.Kind = ClauseKind.Passage; c.SafeConductSteps = 10; break;
            case NpcArchetypeType.Idealist:
                c.Text = "Provisions from the common store"; c.ShortName = "provisions";
                c.Kind = ClauseKind.Goods; c.SuppliesDelta = 4; break;
            case NpcArchetypeType.Survivor:
                c.Text = "Fuel siphoned from a dead wagon"; c.ShortName = "siphoned fuel";
                c.Kind = ClauseKind.Goods; c.FuelDelta = 2; break;
            default:
                c.Text = "A small kindness"; c.ShortName = "kindness";
                c.Kind = ClauseKind.Gold; c.GoldDelta = 10; break;
        }
        return c;
    }

    /// <summary>Goodwill a Charm token buys (spec §3): +2 on the warm-hearted,
    /// nothing on the ones who read flattery as weakness.</summary>
    public static int CharmGain(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Idealist  => 2,
        NpcArchetypeType.Survivor  => 2,
        NpcArchetypeType.Commander => 0,
        NpcArchetypeType.Scholar   => 0,
        _                          => 1,
    };

    /// <summary>Goodwill a Press (Intimidate) costs. Commanders respect it
    /// (−1); everyone else resents it (−2). Idealists walk out instead.</summary>
    public static int PressGoodwillCost(NpcArchetypeType a) =>
        a == NpcArchetypeType.Commander ? 1 : 2;

    /// <summary>How far one Argue (Persuade) lowers their valuation of a
    /// clause: 2 against the reasoners, 1 elsewhere.</summary>
    public static int ArgueStrength(NpcArchetypeType a) =>
        a is NpcArchetypeType.Scholar or NpcArchetypeType.Idealist ? 2 : 1;

}
