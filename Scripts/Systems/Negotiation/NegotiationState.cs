using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// NegotiationState.cs
//
// Purpose:        Pure state machine for one negotiation
//                 encounter, v3 "The Ledger": clauses that
//                 change hands, a goodwill/credit ledger with
//                 hidden NPC valuations, reactions instead of
//                 NPC turns, two demand beats, a grievance-gated
//                 closing squeeze, par-relative stars.
//                 v3.1 (the Parley Deck): the player's leverage
//                 is a hand of drawn cards (ParleyDeck.cs), one
//                 playable per turn before the table action;
//                 their beats are face-down cards on their side;
//                 valuations are learned as bands, exact only
//                 from school moves. Decoupled from UI; consumed
//                 by NegotiationManager.
// Layer:          Data
// Collaborators:  NegotiationEncounterData (input),
//                 NpcArchetype.cs (Clause + archetype tables),
//                 NegotiationBarks.cs (reaction lines),
//                 NegotiationTuning.cs (constants),
//                 NegotiationManager.cs (drives this)
// See:            docs/negotiation_ledger_spec_v1.md,
//                 docs/negotiation_parley_deck_spec_v1.md
// ============================================================

/// <summary>Reading tier of a log entry. Dialogue: the player's spoken
/// lines. NpcLine: the counterpart's spoken lines (the portrait voices
/// these). Scene: stage direction. Detail: the arithmetic (credit, patience
/// stamps), hidden unless the player asks for it.</summary>
public enum NegotiationLogKind { Dialogue, Scene, Detail, NpcLine }

/// <summary>What just happened at the table, for the UI's effects and the
/// portrait's expression. One per player verb (plus beats and closes).</summary>
public enum ReactionKind
{
    Agree, Counter, Refuse,          // to an Ask
    Concede,                         // the player gave something
    Probe, Argue, Charm, Press, Offer, Demonstrate, CallIn, Wait,
    Beat,                            // a demand beat fired
    Reveal,                          // valuations became known (school move / building)
    SchoolMove,
    Card,                            // a parley card was played (ClauseIds = targets, Band = (int)effect)
    Pull,                            // v3.3: terms moved toward you (ClauseIds = moved)
    TheirPull,                       // v3.3: they moved terms toward them (ClauseIds = moved)
    Settle,                          // v3.3: handshake settlement (ClauseIds = taken + swept)
    Flip,                            // one of their cards turned face-up
    Bluff,                           // a bluff landed (Band 1) or failed (Band 0)
    Draw,                            // cards drawn (Band = count)
    SqueezeOpen, SqueezeBlink, SqueezeBristle,
    Sign, Walk, Leave, Collapse,
}

/// <summary>Portrait expression: three states, driven by the last reaction.</summary>
public enum NpcExpression { Neutral, Pleased, Displeased }

/// <summary>One reaction event, consumed by the manager for FX + portrait.</summary>
public class NegotiationReaction
{
    public ReactionKind Kind;
    /// <summary>Clauses the reaction touched (target slip(s) for FX).</summary>
    public List<string> ClauseIds = new();
    /// <summary>Band for barks: agree/concede = value band, refuse = deficit band.</summary>
    public int Band;
    /// <summary>Goodwill before/after, for the scale animation.</summary>
    public int GoodwillFrom, GoodwillTo;
}

/// <summary>A counter-proposal awaiting the player's answer (spec §4b).</summary>
public class CounterProposal
{
    public Clause Ask;      // what the player asked for
    public Clause Demand;   // what they want added
    public Clause Rider;    // the Ask's rider, if any and still open
    public List<Clause> Extras = new();   // v3.2 Bundle: the other slips of the category asked for
    public List<Clause> ExtraRiders = new();
    public string Line;     // what they said, verbatim (the modal repeats it)
}

/// <summary>The NPC's last demand at the handshake (spec §4e).</summary>
public class SqueezeOffer
{
    public Clause Target;
    public int OddsPercent;   // chance they blink if you hold firm; SHOWN
    public string Cause;      // the grievance, in plain words
}

/// <summary>State machine for one in-progress negotiation. Holds the
/// clauses, goodwill, patience, the parley deck and hand, their face-down
/// cards, grievance and resolution state.
/// UI-agnostic; <see cref="NegotiationManager"/> wraps this and renders.</summary>
public class NegotiationState
{
    // ── Encounter data ──────────────────────────────────────────────────
    public NegotiationEncounterData Data { get; private set; }
    public List<Clause> Clauses => Data.Clauses;
    public CardSchool School { get; private set; } = CardSchool.Adept;

    // ── The ledger (spec §2b) ───────────────────────────────────────────
    public int Goodwill { get; private set; } = NegotiationTuning.BaseGoodwill;
    public NegotiationMood Mood =>
        Goodwill >= NegotiationTuning.WarmGoodwill ? NegotiationMood.Warm
      : Goodwill <= NegotiationTuning.ColdGoodwill ? NegotiationMood.Cold
      : NegotiationMood.Even;

    /// <summary>Their private running balance: goodwill + what you've given
    /// them − what you've taken, in THEIR valuation. Never negative.</summary>
    public int Credit =>
        Goodwill
        + Clauses.Where(c => c.IsAgreed && c.Side == ClauseSide.Yours).Sum(c => c.NpcValue)
        - Clauses.Where(c => c.IsAgreed && c.Side == ClauseSide.Theirs && !c.Beguiled).Sum(c => c.NpcValue);

    /// <summary>How they sit (spec v3.1 §5): the only face the credit shows.</summary>
    public Posture Posture => ParleyTells.PostureFor(Credit);

    // ── Patience / turns ─────────────────────────────────────────────────
    public int Patience { get; private set; }
    public int BasePatience { get; private set; }
    public int TurnNumber { get; private set; } = 0;

    // ── Grievance (spec §2e) ─────────────────────────────────────────────
    /// <summary>null = no grievance; otherwise the cause in plain words.</summary>
    public string Grievance { get; private set; } = null;
    public bool HasGrievance => Grievance != null;

    // ── The parley deck (v3.1) ───────────────────────────────────────────
    public List<ParleyCard> Deck { get; private set; } = new();
    public List<ParleyCard> Hand { get; private set; } = new();
    public List<ParleyCard> Discard { get; private set; } = new();
    /// <summary>v3.4: every card play IS the turn. Kept (always false) for
    /// the snapshot and any caller that still reads it.</summary>
    public bool CardPlayedThisTurn { get; private set; } = false;
    /// <summary>v3.4: the three cards always in hand — school move, shake, walk.</summary>
    public List<ParleyCard> FixedCards { get; private set; } = new();
    /// <summary>The last card played this table (Recall's target).</summary>
    public ParleyCard LastPlayed { get; private set; } = null;
    /// <summary>Telemetry: plays counted under the legacy token key.</summary>
    public Dictionary<LeverageToken, int> PlayedCounts { get; private set; } = new();
    /// <summary>Cards on THEIR side: the beats and the squeeze, face-down
    /// until they fire or a Flip turns them.</summary>
    public List<NpcCard> NpcCards { get; private set; } = new();
    private int _nextInstanceId = 1000;
    private int _hardCardsPlayed = 0;
    private bool _warmToneBonusUsed = false;
    private bool _forceArmed = false;
    private bool _bundleArmed = false;
    /// <summary>Set by a Bundle card: the next Ask names two slips.</summary>
    public bool BundleArmed => _bundleArmed;
    public bool ForceArmed => _forceArmed;

    // ── Pending interaction ──────────────────────────────────────────────
    public CounterProposal PendingCounter { get; private set; } = null;

    // ── Resolution ───────────────────────────────────────────────────────
    public bool IsResolved { get; private set; } = false;
    public bool DealAccepted { get; private set; } = false;
    public bool PlayerWalkedAway { get; private set; } = false;
    private bool _collapsed = false;
    private bool _theyLeft = false;
    /// <summary>The table ended because goodwill hit zero (the v3 collapse):
    /// keys the DealRecord outcome and the escalate-to-combat branch.</summary>
    public bool Collapsed => IsResolved && !DealAccepted && !PlayerWalkedAway && _collapsed;
    public bool TheyLeft => IsResolved && !DealAccepted && !PlayerWalkedAway && _theyLeft;
    /// <summary>Warm at the moment of signing (the S4/S5 "Cordial" gate).</summary>
    public bool SignedWarm { get; private set; } = false;

    // ── Squeeze / school move / telemetry ────────────────────────────────
    public bool SqueezeSpent { get; private set; } = false;
    public bool SqueezeWasOffered { get; private set; } = false;
    public bool SqueezeWasHeld { get; private set; } = false;
    public bool SqueezeDidBlink { get; private set; } = false;
    public bool SchoolMoveUsed { get; private set; } = false;
    private bool _showOfPowerArmed = false;
    private TableSnapshot _rewindPoint = null;

    // ── Beats ────────────────────────────────────────────────────────────
    private bool _midBeatFired = false;
    private bool _finalBeatFired = false;
    private bool _finalArmed = false;      // next non-conceding turn ends the table
    private string _finalTargetId = "";
    private string _namedWantId = "";      // the clause they named at a beat (UI tag)
    private bool _warmRevealFired = false;
    private bool _waiting = false;         // Patience token: next turn is free

    // ── Scoring ──────────────────────────────────────────────────────────
    /// <summary>Best surplus reachable at starting goodwill with no tokens
    /// (spec §7). Computed once at table-open.</summary>
    public int Par { get; private set; } = 0;

    // ── Expression / log / events ────────────────────────────────────────
    public NpcExpression Expression { get; private set; } = NpcExpression.Neutral;
    public List<string> Log { get; private set; } = new();
    public event Action<int, int> OnGoodwillChanged;   // old, new
    public event Action<string, NegotiationLogKind> OnLogEntry;
    public event Action<NegotiationReaction> OnReaction;
    public event Action OnResolved;

    /// <summary>Percent roll, 0–99. Replaceable for headless tests.</summary>
    public Func<int> RollPercent = () => (int)(GD.Randi() % 100u);

    private bool _firstTableGuidance = false;

    // ═══════════════════════════════════════════════════════════════════
    // Init
    // ═══════════════════════════════════════════════════════════════════

    public void Initialize(NegotiationEncounterData data, CardSchool wizardSchool,
                           List<Companion> party, int factionReputation = 0,
                           LeverageToken patronToken = LeverageToken.Connections,
                           int patronTokenCount = 0, int openingHand = -1)
    {
        Data = data;
        School = wizardSchool;
        BasePatience = data.ResolvedPatience;
        Patience = BasePatience;

        // Goodwill: authored baseline (or the neutral 3) + standing + archetype.
        int baseline = data.StartingGoodwill >= 0 ? data.StartingGoodwill : NegotiationTuning.BaseGoodwill;
        int standing = factionReputation switch
        {
            >= 2 => NegotiationTuning.StandingAlliedBonus,
            >= 1 => NegotiationTuning.StandingFriendlyBonus,
            <= -2 => -NegotiationTuning.StandingHostilePenalty,   // must precede <= -1
            <= -1 => -NegotiationTuning.StandingUnfriendlyPenalty,
            _ => 0,
        };
        Goodwill = Mathf.Clamp(baseline + standing + ArchetypeBehavior.StartGoodwillMod(data.Archetype),
                               NegotiationTuning.GoodwillMin, NegotiationTuning.GoodwillMax);

        // Resolve every clause's hidden valuation and reset runtime state.
        foreach (var c in Clauses)
        {
            if (c.NpcValue < 0)
                c.NpcValue = ArchetypeBehavior.DefaultNpcValue(data.Archetype, c.Kind);
            c.NpcValue = Mathf.Clamp(c.NpcValue, 0, 5);
            c.Value = Mathf.Clamp(c.Value, 0, 6);
            c.State = ClauseState.Open;
            c.Revealed = false;
            c.BandKnown = false;
            c.Argued = false;
            c.Beguiled = false;
            c.Position = c.Side == ClauseSide.Theirs ? -NegotiationTuning.TrackMax : +NegotiationTuning.TrackMax;
            c.PulledByThem = false;
        }
        // A rider pointing at nothing is authored noise; drop it quietly.
        foreach (var c in Clauses)
            if (c.HasRider && !Clauses.Any(r => r.Id == c.Rider && r.Side == ClauseSide.Yours))
                c.Rider = "";

        BuildDeck(wizardSchool, party, patronToken, patronTokenCount,
                  openingHand > 0 ? openingHand : NegotiationTuning.HandSize);
        FixedCards = new List<ParleyCard>
        {
            ParleyFixedCards.School(SchoolMoveName(wizardSchool)),
            ParleyFixedCards.Shake(),
            ParleyFixedCards.Walk(),
        };
        BuildNpcCards();

        // Grievance at table-open (spec §2e): creed clash or bad standing.
        if (data.ResolvedDistrusts.Contains(wizardSchool.ToString()))
            Grievance = $"{data.NpcName} distrusts {wizardSchool}s";
        else if (factionReputation <= -1)
            Grievance = "bad standing with " + (string.IsNullOrEmpty(data.FactionId) ? "their people" : data.FactionId);

        Par = ComputePar();
        RefreshNpcCards();

        AddLog($"Negotiation begins. {data.NpcName} lays the table.");
        AddLog(data.OpeningText, NegotiationLogKind.NpcLine);
        AddLog($"— goodwill {Goodwill} · patience {Patience} · par +{Par}", NegotiationLogKind.Detail);

        _firstTableGuidance = SaveManager.ActiveSave?.Ledger?.DeedCounts != null
            && !SaveManager.ActiveSave.Ledger.DeedCounts.ContainsKey("negotiation_resolved");
        if (_firstTableGuidance)
            AddLog("Your guild training surfaces: what they hold, you ask for; what you hold, " +
                   "you give. Give what they want and you don't, to take what you want and they don't.");
    }

    private void BuildDeck(CardSchool school, List<Companion> party,
                           LeverageToken patronToken, int patronCount, int openingHand)
    {
        Deck = ParleyDeckBuilder.Build(school, party, patronToken, patronCount, out var provenance);
        Hand.Clear();
        Discard.Clear();
        _nextInstanceId = 1000;
        ParleyDeckBuilder.Shuffle(Deck, n => (int)(GD.Randi() % (uint)Math.Max(1, n)));
        AddLog("Your parley deck: " + string.Join(", ", provenance), NegotiationLogKind.Detail);
        if (patronCount > 0)
            AddLog("A patron at court backs you: a card of theirs is in your deck.", NegotiationLogKind.Detail);
        DrawCards(openingHand, silent: true);
    }

    /// <summary>Their side of the table: the mid beat, the final warning,
    /// and (once a grievance exists) the squeeze — as face-down cards.</summary>
    private void BuildNpcCards()
    {
        NpcCards.Clear();
        NpcCards.Add(new NpcCard { Kind = NpcCardKind.Mid, Name = ParleyCardLibrary.NpcCardName(Data.Archetype, NpcCardKind.Mid) });
        NpcCards.Add(new NpcCard { Kind = NpcCardKind.Final, Name = ParleyCardLibrary.NpcCardName(Data.Archetype, NpcCardKind.Final) });
    }

    /// <summary>The squeeze card appears the moment a grievance exists and
    /// leaves when it is spent. Face-up cards keep their named target fresh.</summary>
    private void RefreshNpcCards()
    {
        bool hasSqueezeCard = NpcCards.Any(n => n.Kind == NpcCardKind.Squeeze);
        if (HasGrievance && !SqueezeSpent && !hasSqueezeCard)
            NpcCards.Add(new NpcCard { Kind = NpcCardKind.Squeeze, Name = ParleyCardLibrary.NpcCardName(Data.Archetype, NpcCardKind.Squeeze), FaceUp = true });
        foreach (var n in NpcCards)
        {
            if (n.Played || !n.FaceUp) continue;
            n.TargetClauseId = PredictNpcCardTarget(n) ?? "";
        }
    }

    /// <summary>What a card of theirs would name if it fired now.</summary>
    private string PredictNpcCardTarget(NpcCard n)
    {
        switch (n.Kind)
        {
            case NpcCardKind.Mid:
                if (Data.Archetype == NpcArchetypeType.Opportunist)
                    return OpenTheirs.Where(c => !c.HasRider).OrderByDescending(c => c.Value).FirstOrDefault()?.Id;
                return TopWant()?.Id;
            case NpcCardKind.Final: return TopWant()?.Id;
            case NpcCardKind.Squeeze: return PredictSqueezeTarget()?.Id;
        }
        return null;
    }

    /// <summary>Draw up to <paramref name="n"/> cards, never past HandSize
    /// (the opening hand may exceed it once: Embassy II).</summary>
    private int DrawCards(int n, bool silent = false, bool allowOverfill = false)
    {
        int drawn = 0;
        while (drawn < n && Deck.Count > 0 && (allowOverfill || Hand.Count < NegotiationTuning.HandSize || silent))
        {
            var c = Deck[0];
            Deck.RemoveAt(0);
            Hand.Add(c);
            drawn++;
        }
        if (!silent && drawn > 0)
        {
            AddLog(drawn == 1 ? $"You draw: {Hand[^1].Name}." : $"You draw {drawn}.", NegotiationLogKind.Detail);
            Emit(ReactionKind.Draw, drawn);
        }
        return drawn;
    }

    /// <summary>Debug / building bridge: put a specific card straight into
    /// the hand (Courier III, the dossier seam, the debug launcher).</summary>
    public ParleyCard AddCardToHand(string cardId, string why = null)
    {
        var c = ParleyDeckBuilder.Instance(cardId, _nextInstanceId++);
        if (c == null) return null;
        Hand.Add(c);
        if (!string.IsNullOrEmpty(why)) AddLog($"  · {why}: {c.Name} joins your hand.", NegotiationLogKind.Detail);
        return c;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Queries
    // ═══════════════════════════════════════════════════════════════════

    public IEnumerable<Clause> OpenTheirs => Clauses.Where(c => c.IsOpen && c.Side == ClauseSide.Theirs);
    public IEnumerable<Clause> OpenYours => Clauses.Where(c => c.IsOpen && c.Side == ClauseSide.Yours);
    public IEnumerable<Clause> Agreed => Clauses.Where(c => c.IsAgreed);
    public Clause Find(string id) => Clauses.FirstOrDefault(c => c.Id == id);

    /// <summary>The yours-side clause they named at the mid or final beat,
    /// while it is still open. Null otherwise. The UI tags the slip.</summary>
    public Clause NamedWant
    {
        get
        {
            var c = string.IsNullOrEmpty(_namedWantId) ? null : Find(_namedWantId);
            return c != null && c.IsOpen ? c : null;
        }
    }

    /// <summary>Credit an Ask is judged against right now (Show of Power
    /// adds its bonus to the next Ask only).</summary>
    public int AskRoom => Credit + (_showOfPowerArmed ? NegotiationTuning.ShowOfPowerCreditBonus : 0);

    /// <summary>What the Ask verb would do to <paramref name="c"/> right now,
    /// with no side effects: the same arithmetic as <see cref="Ask"/>.
    /// The UI shows this only for clauses whose valuation is revealed.</summary>
    public ReactionKind PredictAsk(Clause c)
    {
        if (c == null) return ReactionKind.Refuse;
        if (_forceArmed) return ReactionKind.Agree;
        Clause rider = OpenRiderOf(c);
        var extras = BundleCompanions(c);
        int d = c.NpcValue + extras.Sum(e => e.NpcValue) - AskRoom - ClaimBonus(c) - extras.Sum(ClaimBonus) - (rider?.NpcValue ?? 0)
              - extras.Select(OpenRiderOf).Where(r => r != null && r != rider).Sum(r => r.NpcValue);
        if (d <= 0) return ReactionKind.Agree;
        if (d <= NegotiationTuning.CounterWindow && PickCounterDemand(d, rider) != null) return ReactionKind.Counter;
        return ReactionKind.Refuse;
    }

    /// <summary>Can this clause be Asked for right now? (Open, theirs, and
    /// if it is Warm-sealed, the mood is Warm.)</summary>
    public bool CanAsk(Clause c) =>
        !IsResolved && PendingCounter == null && c != null && c.IsOpen &&
        c.Side == ClauseSide.Theirs && (!c.RequiresWarm || Mood == NegotiationMood.Warm);

    public bool CanConcede(Clause c) =>
        !IsResolved && PendingCounter == null && c != null && c.IsOpen && c.Side == ClauseSide.Yours;

    /// <summary>Can the card be played right now? (v3.2: no targets — a sweep
    /// needs at least one slip it could touch.)</summary>
    public bool CanPlay(ParleyCard card)
    {
        if (IsResolved || PendingCounter != null || card == null) return false;
        if (card.IsFixed)
            return card.Effect switch
            {
                ParleyEffect.Shake => CanShake,
                ParleyEffect.Walk => true,
                ParleyEffect.SchoolMove => CanUseSchoolMove(),
                _ => false,
            };
        if (!Hand.Contains(card)) return false;
        return card.Effect switch
        {
            ParleyEffect.Claim => SweepCandidates(card).Any(),
            ParleyEffect.Concede => SweepCandidates(card).Any(),
            ParleyEffect.Read => SweepCandidates(card).Any(),
            ParleyEffect.ReadTop => OpenYours.Any(c => !c.AnyKnown),
            ParleyEffect.Pull => SweepCandidates(card).Any(),
            ParleyEffect.Sweeten => SweepCandidates(card).Any(),
            ParleyEffect.Force => OpenTheirs.Any() && !_forceArmed,
            ParleyEffect.Bundle => OpenTheirs.Count() >= 2 && !_bundleArmed,
            ParleyEffect.Hold => !_waiting,
            ParleyEffect.Flip => NpcCards.Any(n => !n.Played && !n.FaceUp),
            ParleyEffect.Bluff => OpenTheirs.Any(),
            ParleyEffect.Draw => Deck.Count > 0,
            ParleyEffect.Recall => LastPlayed != null && LastPlayed.Effect != ParleyEffect.Recall && Discard.Contains(LastPlayed),
            _ => true,
        };
    }

    /// <summary>Would this sweep touch <paramref name="c"/> if its category were chosen?</summary>
    private bool SweepEligible(ParleyCard card, Clause c)
    {
        if (c == null || !c.IsOpen) return false;
        return card.Effect switch
        {
            ParleyEffect.Read => !c.Revealed,
            ParleyEffect.Pull => c.Position < NegotiationTuning.TrackMax || OpenRiderOf(c) != null,
            ParleyEffect.Sweeten => c.Side == ClauseSide.Yours && c.NpcValue < NegotiationTuning.SweetenNpcValueMax,
            ParleyEffect.Claim => CanAsk(c),
            ParleyEffect.Concede => CanConcede(c),
            _ => false,
        };
    }

    /// <summary>The category a card will act on: fixed on the card, or (Auto)
    /// the one with the most eligible slips right now, ties by enum order.</summary>
    public ClauseCategory? ResolveCategory(ParleyCard card)
    {
        var fixedCat = ClauseCategories.Fixed(card.Category);
        if (fixedCat.HasValue) return fixedCat;
        ClauseCategory? best = null; int bestScore = 0;
        foreach (ClauseCategory cat in Enum.GetValues(typeof(ClauseCategory)))
        {
            var inCat = Clauses.Where(c => SweepEligible(card, c) && ClauseCategories.Of(c.Kind) == cat).ToList();
            if (inCat.Count == 0) continue;
            // Claim prefers where you've pulled the most; Concede where they've pulled the most.
            int score = card.Effect switch
            {
                ParleyEffect.Claim => 1 + inCat.Sum(c => c.Position + NegotiationTuning.TrackMax) * 10 + inCat.Count,
                ParleyEffect.Concede => 1 + inCat.Sum(c => NegotiationTuning.TrackMax - c.Position) * 10 + inCat.Count,
                _ => inCat.Count,
            };
            if (score > bestScore) { bestScore = score; best = cat; }
        }
        return best;
    }

    /// <summary>Every slip the card could touch in its resolved category.</summary>
    public IEnumerable<Clause> SweepCandidates(ParleyCard card)
    {
        var cat = ResolveCategory(card);
        if (cat == null) return Enumerable.Empty<Clause>();
        return Clauses.Where(c => SweepEligible(card, c) && ClauseCategories.Of(c.Kind) == cat.Value);
    }

    /// <summary>All of the candidates, or a random SweepSomeCount of them.</summary>
    private List<Clause> PickSweep(ParleyCard card)
    {
        var cands = SweepCandidates(card).ToList();
        if (card.Reach == ParleyReach.All || cands.Count <= NegotiationTuning.SweepSomeCount) return cands;
        ParleyDeckBuilder.Shuffle(cands, n => (int)(GD.Randi() % (uint)Math.Max(1, n)));
        return cands.Take(NegotiationTuning.SweepSomeCount).ToList();
    }

    /// <summary>What a Claim card locks: their terms in its category at
    /// position ≥ threshold (yours; contested too when Amount ≥ 2). If none,
    /// the single nearest term (highest position, then cheapest to them).</summary>
    public List<Clause> ClaimTargets(ParleyCard card)
    {
        var cands = SweepCandidates(card).ToList();
        int threshold = NegotiationTuning.ClaimAtPosition - (card.Amount >= 2 ? 1 : 0);
        var at = cands.Where(c => c.Position >= threshold).Take(NegotiationTuning.BundleMaxSlips).ToList();
        if (at.Count > 0) return at;
        var nearest = cands.OrderByDescending(c => c.Position).ThenBy(c => c.NpcValue).FirstOrDefault();
        return nearest != null ? new List<Clause> { nearest } : new List<Clause>();
    }

    /// <summary>What a Concede card gives: your terms in its category at
    /// position ≤ threshold (theirs; contested too when Amount ≥ 2). If none,
    /// the one they want most.</summary>
    public List<Clause> ConcedeTargets(ParleyCard card)
    {
        var cands = SweepCandidates(card).ToList();
        int threshold = NegotiationTuning.ConcedeAtPosition + (card.Amount >= 2 ? 1 : 0);
        var at = cands.Where(c => c.Position <= threshold).ToList();
        if (at.Count > 0) return at;
        var top = cands.OrderByDescending(c => c.NpcValue).FirstOrDefault();
        return top != null ? new List<Clause> { top } : new List<Clause>();
    }

    /// <summary>The slips a Bundle-ask would take alongside <paramref name="c"/>:
    /// the other open, askable slips of its category, up to BundleMaxSlips − 1.</summary>
    public List<Clause> BundleCompanions(Clause c)
    {
        if (c == null || !_bundleArmed) return new List<Clause>();
        var cat = ClauseCategories.Of(c.Kind);
        return OpenTheirs.Where(o => o != c && CanAsk(o) && ClauseCategories.Of(o.Kind) == cat)
                         .Take(NegotiationTuning.BundleMaxSlips - 1).ToList();
    }

    public bool IsRider(Clause c) => c != null && Clauses.Any(p => p.Rider == c.Id && p.IsOpen);

    /// <summary>Debug hook (NegotiationDebugLauncher): arm a grievance from
    /// outside so the closing squeeze can be exercised on any table.</summary>
    public void ForceGrievance(string cause)
    {
        if (Grievance == null) Grievance = cause;
        RefreshNpcCards();
    }

    /// <summary>The open yours-side clause they value most (ties: authored order).</summary>
    public Clause TopWant()
    {
        Clause best = null;
        foreach (var c in OpenYours)
            if (best == null || c.NpcValue > best.NpcValue) best = c;
        return best;
    }

    /// <summary>Surplus of the package as it stands (spec §7).</summary>
    public int Surplus() =>
        Clauses.Where(c => c.IsAgreed && c.Side == ClauseSide.Theirs).Sum(c => c.Value)
      - Clauses.Where(c => c.IsAgreed && c.Side == ClauseSide.Yours).Sum(c => c.Value);

    public int ProjectStars() => StarsFor(Surplus());
    public int GetStars() => DealAccepted ? StarsFor(Surplus()) : 0;
    public int GetDealScore() => DealAccepted ? Surplus() : 0;

    private int StarsFor(int s)
    {
        if (s <= 0) return 1;
        if (Par <= 0) return 5;
        float f = (float)s / Par;
        return f >= 1f ? 5
             : f >= NegotiationTuning.StarT4Fraction ? 4
             : f >= NegotiationTuning.StarT3Fraction ? 3
             : f >= NegotiationTuning.StarT2Fraction ? 2 : 1;
    }

    /// <summary>Brute-force the best feasible package at starting goodwill
    /// (riders enforced, Warm-sealed clauses excluded). ≤ 2^12 subsets.</summary>
    private int ComputePar()
    {
        var pool = Clauses.Where(c => c.IsOpen && !c.RequiresWarm).Take(NegotiationTuning.MaxParClauses).ToList();
        var theirs = pool.Where(c => c.Side == ClauseSide.Theirs).ToList();
        var yours = pool.Where(c => c.Side == ClauseSide.Yours).ToList();
        int best = 0;
        for (int tm = 0; tm < (1 << theirs.Count); tm++)
        {
            int takenNpc = 0, takenVal = 0;
            var forced = new HashSet<string>();
            for (int i = 0; i < theirs.Count; i++)
            {
                if ((tm >> i & 1) == 0) continue;
                takenNpc += theirs[i].NpcValue;
                takenVal += theirs[i].Value;
                if (theirs[i].HasRider) forced.Add(theirs[i].Rider);
            }
            for (int ym = 0; ym < (1 << yours.Count); ym++)
            {
                int givenNpc = 0, givenVal = 0;
                bool ok = true;
                foreach (var f in forced)
                {
                    int idx = yours.FindIndex(y => y.Id == f);
                    if (idx < 0 || (ym >> idx & 1) == 0) { ok = false; break; }
                }
                if (!ok) continue;
                for (int i = 0; i < yours.Count; i++)
                {
                    if ((ym >> i & 1) == 0) continue;
                    givenNpc += yours[i].NpcValue;
                    givenVal += yours[i].Value;
                }
                if (Goodwill + givenNpc - takenNpc < 0) continue;
                int s = takenVal - givenVal;
                if (s > best) best = s;
            }
        }
        return best;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Player verbs (spec §3)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Ask for one of their clauses. Returns the reaction kind
    /// (Agree / Counter / Refuse) or null if the ask was illegal. On Counter,
    /// <see cref="PendingCounter"/> is set and must be answered.</summary>
    public ReactionKind? Ask(Clause c, List<Clause> extras = null)
    {
        if (!CanAsk(c)) return null;
        extras = (extras ?? new List<Clause>()).Where(e => e != null && e != c && CanAsk(e)).Distinct().ToList();
        if (!FinalGate(null)) return ReactionKind.Leave;
        CaptureRewindPoint();
        Clause rider = OpenRiderOf(c);
        var extraRiders = extras.Select(OpenRiderOf).Where(r => r != null && r != rider).Distinct().ToList();
        int bonus = _showOfPowerArmed ? NegotiationTuning.ShowOfPowerCreditBonus : 0;
        _showOfPowerArmed = false;
        bool forced = _forceArmed;
        _forceArmed = false;
        _bundleArmed = false;
        int need = c.NpcValue + extras.Sum(e => e.NpcValue);
        int posBonus = ClaimBonus(c) + extras.Sum(ClaimBonus);
        int d = need - (Credit + bonus + posBonus) - (rider?.NpcValue ?? 0) - extraRiders.Sum(r => r.NpcValue);
        var allAsked = new List<Clause> { c }; allAsked.AddRange(extras);
        var allRiders = new List<Clause>(); if (rider != null) allRiders.Add(rider); allRiders.AddRange(extraRiders);
        string askName = extras.Count == 0 ? ShortName(c) : $"{ShortName(c)} and the {string.Join(" and the ", extras.Select(ShortName))}";
        var ids = allAsked.Concat(allRiders).Select(x => x.Id).ToArray();

        AddLog(extras.Count == 0 ? NegotiationBarks.PlayerAsk(ShortName(c)) : $"“The {askName}. Together.”", NegotiationLogKind.Dialogue);

        if (forced)
        {
            // A Force card: the ask goes through, and they hold it against you.
            AddLog(NegotiationBarks.PlayerPress(ShortName(c)), NegotiationLogKind.Dialogue);
            AgreeClause(c, rider);
            foreach (var e in extras) AgreeClause(e, OpenRiderOf(e));
            int from = Goodwill;
            if (Grievance == null) Grievance = "you forced their hand at this table";
            SetExpression(NpcExpression.Displeased);
            AddLog(NegotiationBarks.PressBark(Data.Archetype), NegotiationLogKind.NpcLine);
            Emit(ReactionKind.Press, 0, from, ids);
            ApplyGoodwill(-ArchetypeBehavior.PressGoodwillCost(Data.Archetype), "force");
            RefreshNpcCards();
            if (IsResolved) return ReactionKind.Press;
            SpendTurn();
            return ReactionKind.Press;
        }

        if (d <= 0)
        {
            AgreeClause(c, rider);
            foreach (var e in extras) AgreeClause(e, OpenRiderOf(e));
            SetExpression(NpcExpression.Pleased);
            AddLog(NegotiationBarks.AgreeBark(Data.Archetype, ValueBand(need)), NegotiationLogKind.NpcLine);
            Emit(ReactionKind.Agree, ValueBand(need), ids);
            SpendTurn();
            CheckWarmReveal();
            return ReactionKind.Agree;
        }

        if (d <= NegotiationTuning.CounterWindow)
        {
            var demand = PickCounterDemand(d, rider);
            if (demand != null && !extraRiders.Contains(demand))
            {
                string line = NegotiationBarks.CounterBark(Data.Archetype, askName, ShortName(demand));
                PendingCounter = new CounterProposal
                {
                    Ask = c, Demand = demand, Rider = rider, Line = line,
                    Extras = extras, ExtraRiders = extraRiders,
                };
                SetExpression(NpcExpression.Neutral);
                AddLog(line, NegotiationLogKind.NpcLine);
                Emit(ReactionKind.Counter, d, new[] { c.Id, demand.Id }.Concat(extras.Select(e => e.Id)).ToArray());
                SpendTurn();   // the exchange cost a beat either way
                return ReactionKind.Counter;
            }
        }

        RefuseClause(c, d);
        if (!IsResolved) foreach (var e in extras) { e.State = ClauseState.Refused; e.Position = -NegotiationTuning.TrackMax; }
        SpendTurn();
        return ReactionKind.Refuse;
    }

    /// <summary>Answer a pending counter-proposal: both clauses (and the
    /// rider) agree; a generous demand still warms them.</summary>
    public void AcceptCounter()
    {
        var pc = PendingCounter;
        if (pc == null || IsResolved) return;
        PendingCounter = null;
        AgreeClause(pc.Ask, pc.Rider);
        foreach (var e in pc.Extras) AgreeClause(e, OpenRiderOf(e));
        pc.Demand.State = ClauseState.Agreed;
        pc.Demand.Revealed = true;
        if (pc.Demand.NpcValue >= NegotiationTuning.GenerousThreshold)
            ApplyGoodwill(NegotiationTuning.GenerousGoodwillGain, "generous");
        SetExpression(NpcExpression.Pleased);
        AddLog(NegotiationBarks.PlayerAcceptCounter(ShortName(pc.Ask), ShortName(pc.Demand)),
               NegotiationLogKind.Dialogue);
        AddLog(NegotiationBarks.AgreeBark(Data.Archetype, 1), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Agree, 1, new[] { pc.Ask.Id, pc.Demand.Id, pc.Rider?.Id }.Concat(pc.Extras.Select(e => e.Id)).Concat(pc.ExtraRiders.Select(r => r.Id)).ToArray());
        CheckWarmReveal();
        StampDetail();
    }

    /// <summary>Decline a counter: the Ask'd clause stays open (not refused),
    /// no goodwill change. The turn was already spent.</summary>
    public void DeclineCounter()
    {
        var pc = PendingCounter;
        if (pc == null || IsResolved) return;
        PendingCounter = null;
        SetExpression(NpcExpression.Neutral);
        AddLog(NegotiationBarks.PlayerDeclineCounter(), NegotiationLogKind.Dialogue);
        AddLog(NegotiationBarks.DeclineBark(Data.Archetype), NegotiationLogKind.NpcLine);
        StampDetail();
    }

    /// <summary>Give one of your clauses. Agreed is agreed (spec §1.2).</summary>
    public bool Concede(Clause c)
    {
        if (!CanConcede(c)) return false;
        if (!FinalGate(c)) return false;
        CaptureRewindPoint();
        c.State = ClauseState.Agreed;
        c.Revealed = true;
        AddLog(NegotiationBarks.PlayerConcede(ShortName(c)), NegotiationLogKind.Dialogue);
        int band = ConcedeBand(c.NpcValue);
        if (c.NpcValue >= NegotiationTuning.GenerousThreshold)
            ApplyGoodwill(NegotiationTuning.GenerousGoodwillGain, "generous");
        if (c.PulledByThem && !IsResolved)
            ApplyGoodwill(NegotiationTuning.ConcedePulledGoodwill, "you saw what it meant to them");
        SetExpression(c.NpcValue >= 2 ? NpcExpression.Pleased : NpcExpression.Neutral);
        AddLog(NegotiationBarks.ConcedeBark(Data.Archetype, band), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Concede, band, c.Id);
        if (_finalArmed && c.Id == _finalTargetId)
        {
            _finalArmed = false;
            _finalTargetId = "";
            Patience += NegotiationTuning.FinalDemandPatienceRefund;
            AddLog(NegotiationBarks.FinalSatisfied(Data.Archetype), NegotiationLogKind.NpcLine);
        }
        SpendTurn();
        CheckWarmReveal();
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cards (spec v3.1 §2b, §2c, §6): free, one per turn, before the action
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Play a card from the hand. v3.2: no targets — a sweep picks
    /// its category (fixed on the card, or the busiest) and touches all or
    /// some of it. Returns false when the play is illegal. Playing never
    /// spends patience; the table action that follows does. Tone lands first
    /// (§2c), then the effect.</summary>
    public bool PlayCard(ParleyCard card)
    {
        if (!CanPlay(card)) return false;
        if (!FinalGate(null)) return false;
        CaptureRewindPoint();

        if (card.IsFixed)
        {
            switch (card.Effect)
            {
                case ParleyEffect.Walk: WalkAway(); return true;
                case ParleyEffect.SchoolMove: return UseSchoolMove();
                case ParleyEffect.Shake: return false;   // the manager runs the preview, then BeginShake
            }
        }

        Hand.Remove(card);
        if (card.Once) Discard.Add(card); else Deck.Add(card);
        LastPlayed = card;
        if (card.AsToken.HasValue)
            PlayedCounts[card.AsToken.Value] = PlayedCounts.GetValueOrDefault(card.AsToken.Value) + 1;

        string line = card.Line.Replace("{school}", School.ToString());
        if (!string.IsNullOrEmpty(line))
            AddLog(line.StartsWith("“") ? line : $"({line})", NegotiationLogKind.Dialogue);

        if (!ApplyTone(card)) return true;   // an Idealist walked out on a hard card

        // Claim and Concede are the table actions themselves; they spend the turn inside.
        if (card.Effect == ParleyEffect.Claim)
        {
            var targets = ClaimTargets(card);
            if (targets.Count == 0) { AddLog("There is nothing of theirs left to claim there."); StampDetail(); return true; }
            Ask(targets[0], targets.Skip(1).ToList());
            RefreshNpcCards();
            return true;
        }
        if (card.Effect == ParleyEffect.Concede)
        {
            var targets = ConcedeTargets(card);
            if (targets.Count == 0) { AddLog("There is nothing of yours left to give there."); StampDetail(); return true; }
            ConcedeMany(targets);
            RefreshNpcCards();
            return true;
        }

        switch (card.Effect)
        {
            case ParleyEffect.Read: EffectRead(card); break;
            case ParleyEffect.ReadTop: EffectReadTop(); break;
            case ParleyEffect.Pull: EffectPull(card); break;
            case ParleyEffect.Warm: EffectWarm(card); break;
            case ParleyEffect.Force: EffectForce(); break;
            case ParleyEffect.Purse: EffectPurse(card); break;
            case ParleyEffect.Display: EffectDisplay(card); break;
            case ParleyEffect.Hold: EffectHold(); break;
            case ParleyEffect.Flip: EffectFlip(); break;
            case ParleyEffect.Bundle: EffectBundle(); break;
            case ParleyEffect.Sweeten: EffectSweeten(card); break;
            case ParleyEffect.Bluff: EffectBluff(); break;
            case ParleyEffect.Draw: DrawCards(card.Amount); break;
            case ParleyEffect.Recall: EffectRecall(); break;
        }
        RefreshNpcCards();
        // v3.4: the card play is the turn (free cards excepted).
        if (!IsResolved && !card.IsFree) SpendTurn();
        else if (!IsResolved) StampDetail();
        return true;
    }

    /// <summary>Concede several terms as one turn (a Concede card).</summary>
    private void ConcedeMany(List<Clause> targets)
    {
        if (!FinalGate(targets.FirstOrDefault(t => t.Id == _finalTargetId) ?? targets[0])) return;
        CaptureRewindPoint();
        int totalNpc = 0;
        var ids = new List<string>();
        foreach (var c in targets)
        {
            if (!CanConcede(c)) continue;
            c.State = ClauseState.Agreed;
            c.Revealed = true;
            totalNpc += c.NpcValue;
            ids.Add(c.Id);
            if (c.PulledByThem) ApplyGoodwill(NegotiationTuning.ConcedePulledGoodwill, "you saw what it meant to them");
            if (IsResolved) return;
            if (_finalArmed && c.Id == _finalTargetId)
            {
                _finalArmed = false;
                _finalTargetId = "";
                Patience += NegotiationTuning.FinalDemandPatienceRefund;
                AddLog(NegotiationBarks.FinalSatisfied(Data.Archetype), NegotiationLogKind.NpcLine);
            }
        }
        if (ids.Count == 0) return;
        AddLog(ids.Count == 1
            ? NegotiationBarks.PlayerConcede(ShortName(Find(ids[0])))
            : $"“Take the {string.Join(" and the ", ids.Select(i => ShortName(Find(i))))}. They're yours.”", NegotiationLogKind.Dialogue);
        if (totalNpc >= NegotiationTuning.GenerousThreshold)
            ApplyGoodwill(NegotiationTuning.GenerousGoodwillGain, "generous");
        if (IsResolved) return;
        int band = ConcedeBand(Math.Min(5, totalNpc));
        SetExpression(totalNpc >= 2 ? NpcExpression.Pleased : NpcExpression.Neutral);
        AddLog(NegotiationBarks.ConcedeBark(Data.Archetype, band), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Concede, band, ids.ToArray());
        SpendTurn();
        CheckWarmReveal();
    }

    /// <summary>§2c. Returns false if the table ended (Idealist walkout).</summary>
    private bool ApplyTone(ParleyCard card)
    {
        switch (card.Tone)
        {
            case ParleyTone.Warm:
                SetExpression(NpcExpression.Pleased);
                if (Mood == NegotiationMood.Warm && !_warmToneBonusUsed)
                {
                    _warmToneBonusUsed = true;
                    ApplyGoodwill(NegotiationTuning.WarmToneBonusGoodwill, "warm word on a warm table");
                }
                return true;
            case ParleyTone.Hard:
                _hardCardsPlayed++;
                SetExpression(NpcExpression.Displeased);
                if (Data.Archetype == NpcArchetypeType.Idealist)
                {
                    AddLog(NegotiationBarks.IdealistWalkout(), NegotiationLogKind.NpcLine);
                    AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"", NegotiationLogKind.NpcLine);
                    Emit(ReactionKind.Leave, 0);
                    _theyLeft = true;
                    Resolve(false, false);
                    return false;
                }
                if (_hardCardsPlayed >= NegotiationTuning.HardCardGrievanceCount && Grievance == null)
                {
                    Grievance = "you leaned on them at this table";
                    AddLog("They have stopped smiling. Whatever you sign now, they will want one more thing.", NegotiationLogKind.Scene);
                }
                return true;
            default:
                SetExpression(NpcExpression.Neutral);
                return true;
        }
    }

    private void EffectRead(ParleyCard card)
    {
        var hit = PickSweep(card);
        foreach (var c in hit) c.BandKnown = true;
        // A read slip's open rider comes with it.
        foreach (var c in hit.ToList())
        {
            var r = OpenRiderOf(c);
            if (r != null && !r.Revealed && !hit.Contains(r)) { r.BandKnown = true; hit.Add(r); }
        }
        if (hit.Count == 0) { AddLog("Nothing new to read there."); return; }
        var cat = ResolveCategory(card);
        AddLog($"You read the {(cat.HasValue ? ClauseCategories.Word(cat.Value) : "table")}: " +
               string.Join("; ", hit.Select(c => $"the {ShortName(c)} is {ParleyTells.Band(c.NpcValue)} to them")) + ".");
        Emit(ReactionKind.Probe, 0, hit.Select(c => c.Id).ToArray());
    }

    private void EffectReadTop()
    {
        var y = TopWant();
        if (y == null) { AddLog("There is nothing of yours left for them to want."); return; }
        y.BandKnown = true;
        AddLog($"What they want most of yours: the {ShortName(y)} — {ParleyTells.Band(y.NpcValue)} to them.");
        Emit(ReactionKind.Reveal, 0, y.Id);
    }

    private void EffectPull(ParleyCard card)
    {
        var hit = PickSweep(card);
        if (hit.Count == 0) { AddLog("There is nothing left to pull there."); return; }
        var struck = new List<Clause>();
        foreach (var c in hit)
        {
            c.Position = Math.Min(NegotiationTuning.TrackMax, c.Position + 1);
            c.Argued = true;
            var r = OpenRiderOf(c);
            if (r != null)
            {
                r.State = ClauseState.Struck;
                c.Rider = "";
                struck.Add(r);
            }
        }
        var cat = ResolveCategory(card);
        AddLog($"You pull the {(cat.HasValue ? ClauseCategories.Word(cat.Value) : "table")} your way: " +
               string.Join(", ", hit.Select(c => $"the {ShortName(c)} ({PositionWord(c)})")) +
               (struck.Count > 0 ? $". The {string.Join(", ", struck.Select(ShortName))} come{(struck.Count == 1 ? "s" : "")} off." : "."));
        AddLog(NegotiationBarks.ArgueBark(Data.Archetype), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Pull, 0, hit.Concat(struck).Select(c => c.Id).ToArray());
    }

    public static string PositionWord(Clause c) =>
        c.Position <= -NegotiationTuning.TrackMax ? "theirs"
      : c.Position < 0 ? "leaning theirs"
      : c.Position == 0 ? "contested"
      : c.Position < NegotiationTuning.TrackMax ? "leaning yours"
      : "yours";

    /// <summary>Claim credit bonus from position: 0 / 0 / 1 / 1 / 2 across the five steps.</summary>
    public static int ClaimBonus(Clause c) =>
        Math.Clamp((c.Position + NegotiationTuning.TrackMax) / 2, 0, NegotiationTuning.ClaimBonusMax);

    /// <summary>Their pull (spec v3.3 §2): one open term a step toward them —
    /// their top want among yours, else the term of theirs you've pulled
    /// that they value most. Spoken, and the marker slides.</summary>
    private void TheirPull(int count)
    {
        var moved = new List<Clause>();
        for (int i = 0; i < count; i++)
        {
            var target = OpenYours.Where(c => c.Position > -NegotiationTuning.TrackMax && !moved.Contains(c)).OrderByDescending(c => c.NpcValue).FirstOrDefault()
                      ?? OpenTheirs.Where(c => c.Position > -NegotiationTuning.TrackMax && !moved.Contains(c)).OrderByDescending(c => c.NpcValue).FirstOrDefault();
            if (target == null) break;
            target.Position = Math.Max(-NegotiationTuning.TrackMax, target.Position - 1);
            target.PulledByThem = true;
            moved.Add(target);
        }
        if (moved.Count == 0) return;
        var first = moved[0];
        AddLog(first.Side == ClauseSide.Yours
            ? $"“I'll want the {ShortName(first)} back in this.”"
            : $"“The {ShortName(first)} stays with me, for now.”", NegotiationLogKind.NpcLine);
        Emit(ReactionKind.TheirPull, 0, moved.Select(c => c.Id).ToArray());
    }

    /// <summary>Mid beat pull: their whole top category, one step toward them.</summary>
    private void TheirCategoryPull(ClauseCategory cat)
    {
        var moved = Clauses.Where(c => c.IsOpen && c.Position > -NegotiationTuning.TrackMax && ClauseCategories.Of(c.Kind) == cat).ToList();
        foreach (var c in moved) { c.Position = Math.Max(-NegotiationTuning.TrackMax, c.Position - 1); c.PulledByThem = true; }
        if (moved.Count > 0) Emit(ReactionKind.TheirPull, 1, moved.Select(c => c.Id).ToArray());
    }

    private void EffectWarm(ParleyCard card)
    {
        int shape = ArchetypeBehavior.CharmGain(Data.Archetype);
        int g = shape <= 0 ? 0 : card.Amount + (shape - 1);
        int from = Goodwill;
        if (g > 0) ApplyGoodwill(g, card.Name.ToLowerInvariant());
        SetExpression(g > 0 ? NpcExpression.Pleased : NpcExpression.Displeased);
        AddLog(NegotiationBarks.CharmBark(Data.Archetype, g > 0), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Charm, g, from);
        CheckWarmReveal();
    }

    private void EffectForce()
    {
        _forceArmed = true;
        AddLog("Your next ask will not be a question.", NegotiationLogKind.Scene);
        Emit(ReactionKind.Card, (int)ParleyEffect.Force);
    }

    private void EffectPurse(ParleyCard card)
    {
        int gold = card.Amount >= 10 ? card.Amount : NegotiationTuning.OfferingGold;
        var c = new Clause
        {
            Id = $"purse_{TurnNumber}_{Clauses.Count}",
            Text = $"A purse of {gold} gold",
            ShortName = "purse",
            Side = ClauseSide.Yours, Kind = ClauseKind.Gold,
            Value = gold >= NegotiationTuning.OfferingGold ? NegotiationTuning.OfferingValue : 1,
            NpcValue = ArchetypeBehavior.DefaultNpcValue(Data.Archetype, ClauseKind.Gold),
            GoldDelta = -gold,
            Ephemeral = true,
        };
        Clauses.Add(c);
        AddLog(NegotiationBarks.PlayerOffer(), NegotiationLogKind.Dialogue);
        Emit(ReactionKind.Offer, 0, c.Id);
    }

    private void EffectDisplay(ParleyCard card)
    {
        bool device = card.Amount >= 3;   // Tinker's Fabricate: a device, worth 3 to anyone
        var c = device
            ? new Clause
            {
                Id = $"fabricated_{Clauses.Count}",
                Text = "A fabricated device", ShortName = "device",
                Side = ClauseSide.Yours, Kind = ClauseKind.Goods,
                Value = NegotiationTuning.FabricateValue, NpcValue = NegotiationTuning.FabricateNpcValue,
                Ephemeral = true, Revealed = true,
            }
            : new Clause
            {
                Id = $"demo_{TurnNumber}_{Clauses.Count}",
                Text = $"A display of {School} magic",
                ShortName = "display",
                Side = ClauseSide.Yours, Kind = ClauseKind.Honor,
                Value = 0,
                NpcValue = Mathf.Clamp(ArchetypeBehavior.DemonstrationValue(Data.Archetype, School) + (card.Amount - 1), 0, 5),
                Ephemeral = true,
            };
        Clauses.Add(c);
        AddLog(NegotiationBarks.PlayerDemonstrate(School), NegotiationLogKind.Dialogue);
        Emit(ReactionKind.Demonstrate, c.NpcValue, c.Id);
    }

    private void EffectHold()
    {
        _waiting = true;
        AddLog(NegotiationBarks.PlayerWait(), NegotiationLogKind.Dialogue);
        Emit(ReactionKind.Wait, 0);
    }

    private void EffectFlip()
    {
        var n = NpcCards.FirstOrDefault(x => !x.Played && !x.FaceUp);
        if (n == null) { AddLog("Their cards are all face-up already."); return; }
        n.FaceUp = true;
        n.TargetClauseId = PredictNpcCardTarget(n) ?? "";
        var t = Find(n.TargetClauseId);
        AddLog(t != null
            ? $"You turn their {n.Name}: it names the {ShortName(t)}."
            : $"You turn their {n.Name}.");
        Emit(ReactionKind.Flip, (int)n.Kind, t?.Id);
    }

    private void EffectBundle()
    {
        _bundleArmed = true;
        AddLog("Your next ask takes everything they hold of that kind, judged as one.", NegotiationLogKind.Scene);
        Emit(ReactionKind.Card, (int)ParleyEffect.Bundle);
    }

    private void EffectSweeten(ParleyCard card)
    {
        var hit = PickSweep(card);
        if (hit.Count == 0) { AddLog("They could not want your side of it more than they already do."); return; }
        foreach (var c in hit)
            c.NpcValue = Mathf.Min(NegotiationTuning.SweetenNpcValueMax, c.NpcValue + card.Amount);
        var cat = ResolveCategory(card);
        AddLog($"Your {(cat.HasValue ? ClauseCategories.Word(cat.Value) : "offer")} looks better to them than it did: " +
               string.Join(", ", hit.Select(c => $"the {ShortName(c)}")) + ".");
        Emit(ReactionKind.Card, (int)ParleyEffect.Sweeten, hit.Select(c => c.Id).ToArray());
    }

    private void EffectBluff()
    {
        int worthToThem = Clauses.Where(c => c.IsAgreed && c.Side == ClauseSide.Yours).Sum(c => c.NpcValue);
        var cheapest = OpenTheirs.Where(c => !c.RequiresWarm).OrderBy(c => c.NpcValue).ThenBy(c => c.Value).FirstOrDefault();
        if (worthToThem >= NegotiationTuning.BluffThreshold && cheapest != null)
        {
            AgreeClause(cheapest, OpenRiderOf(cheapest));
            SetExpression(NpcExpression.Displeased);
            AddLog($"They glance at what is already on the ledger. “Fine. The {ShortName(cheapest)}. Sit down.”", NegotiationLogKind.NpcLine);
            Emit(ReactionKind.Bluff, 1, cheapest.Id);
        }
        else
        {
            int from = Goodwill;
            SetExpression(NpcExpression.Displeased);
            AddLog(NegotiationBarks.DeclineBark(Data.Archetype), NegotiationLogKind.NpcLine);
            Emit(ReactionKind.Bluff, 0, from);
            ApplyGoodwill(-NegotiationTuning.BluffFailGoodwillCost, "a bluff that didn't land");
        }
    }

    private void EffectRecall()
    {
        var prev = Discard.LastOrDefault(c => c.Effect != ParleyEffect.Recall);
        if (prev == null) { AddLog("There is nothing to take back."); return; }
        Discard.Remove(prev);
        Hand.Add(prev);
        AddLog($"You take back {prev.Name}.", NegotiationLogKind.Detail);
        Emit(ReactionKind.Draw, 1);
    }

    public bool IsWaiting => _waiting;

    // ═══════════════════════════════════════════════════════════════════
    // Closing (spec §4e)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The squeeze a handshake offered NOW would draw, or null if
    /// they would sign as written. Single source of truth for BeginShake
    /// and the UI's mark on the portrait.</summary>
    public Clause PredictSqueezeTarget()
    {
        if (IsResolved || !HasGrievance || SqueezeSpent) return null;
        return TopWant();
    }

    public bool CanShake => !IsResolved && PendingCounter == null &&
        (Agreed.Any() || OpenYours.Any(c => c.Position <= NegotiationTuning.SettleTakeAt) || OpenTheirs.Any(c => c.Position >= NegotiationTuning.SettleSweepAt));

    /// <summary>Begin closing. Returns the squeeze, or null when they sign
    /// (in which case the table is ALREADY resolved on return).</summary>
    public SqueezeOffer BeginShake()
    {
        if (!CanShake) return null;
        var target = PredictSqueezeTarget();
        if (target == null)
        {
            Sign();
            return null;
        }
        int odds = Mathf.Clamp(
            NegotiationTuning.HoldOddsBase + NegotiationTuning.HoldOddsPerGoodwill * (Goodwill - NegotiationTuning.HoldOddsPivot),
            NegotiationTuning.HoldOddsMin, NegotiationTuning.HoldOddsMax);
        SqueezeWasOffered = true;
        MarkNpcCardPlayed(NpcCardKind.Squeeze);
        SetExpression(NpcExpression.Neutral);
        AddLog(NegotiationBarks.SqueezeOpen(Data.Archetype, School, ShortName(target)), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.SqueezeOpen, 0, target.Id);
        return new SqueezeOffer { Target = target, OddsPercent = odds, Cause = Grievance };
    }

    public void ResolveSqueezeConcede(SqueezeOffer offer)
    {
        if (IsResolved || offer == null) return;
        offer.Target.State = ClauseState.Agreed;
        offer.Target.Revealed = true;
        SqueezeSpent = true;
        AddLog(NegotiationBarks.PlayerConcede(ShortName(offer.Target)), NegotiationLogKind.Dialogue);
        AddLog("You let them have it. The ink dries.");
        Sign();
    }

    /// <summary>Hold firm: the shown odds roll. Blink → signs as written.
    /// Bristle → goodwill and patience drop and the table continues; there
    /// is no second squeeze.</summary>
    public bool ResolveSqueezeHoldFirm(SqueezeOffer offer)
    {
        if (IsResolved || offer == null) return false;
        SqueezeSpent = true;
        SqueezeWasHeld = true;
        if (RollPercent() < offer.OddsPercent)
        {
            SqueezeDidBlink = true;
            SetExpression(NpcExpression.Pleased);
            AddLog(NegotiationBarks.SqueezeBlink(Data.Archetype), NegotiationLogKind.NpcLine);
            Emit(ReactionKind.SqueezeBlink, 0, offer.Target.Id);
            Sign();
            return true;
        }
        SetExpression(NpcExpression.Displeased);
        AddLog(NegotiationBarks.SqueezeBristle(Data.Archetype), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.SqueezeBristle, 0, Goodwill, offer.Target.Id);
        ApplyGoodwill(-NegotiationTuning.BristleGoodwillCost, "bristle");
        if (IsResolved) return false;
        Patience -= NegotiationTuning.BristlePatienceCost;
        if (Patience <= 0) { Leave(); return false; }
        AddLog("The handshake failed. The talk goes on.");
        StampDetail();
        return false;
    }

    public void ResolveSqueezeWithdraw()
    {
        if (IsResolved) return;
        AddLog("You withdraw your hand. Not yet.");
    }

    /// <summary>What the handshake would settle right now (spec v3.3 §3):
    /// your terms at theirs are taken; their terms at yours are swept in,
    /// cheapest first, while credit (plus the sweep bonus) covers them.</summary>
    public (List<Clause> taken, List<Clause> swept, List<Clause> fallback) PreviewSettlement()
    {
        var taken = OpenYours.Where(c => c.Position <= NegotiationTuning.SettleTakeAt).ToList();
        int credit = Credit + taken.Sum(c => c.NpcValue);
        var swept = new List<Clause>();
        var fallback = new List<Clause>();
        foreach (var c in OpenTheirs.Where(c => c.Position >= NegotiationTuning.SettleSweepAt && !c.RequiresWarm).OrderBy(c => c.NpcValue).ThenByDescending(c => c.Value))
        {
            var r = OpenRiderOf(c);
            int cost = c.NpcValue - (r?.NpcValue ?? 0);
            if (credit + NegotiationTuning.SettleSweepBonus >= cost) { swept.Add(c); credit -= cost; }
            else fallback.Add(c);
        }
        return (taken, swept, fallback);
    }

    private void Settle()
    {
        var (taken, swept, _) = PreviewSettlement();
        foreach (var c in taken) { c.State = ClauseState.Agreed; c.Revealed = true; }
        foreach (var c in swept) AgreeClause(c, OpenRiderOf(c));
        if (taken.Count > 0)
            AddLog("They take what you let slip: " + string.Join(", ", taken.Select(c => $"the {ShortName(c)}")) + ".");
        if (swept.Count > 0)
            AddLog("What you pulled your way comes with you: " + string.Join(", ", swept.Select(c => $"the {ShortName(c)}")) + ".");
        if (taken.Count + swept.Count > 0)
            Emit(ReactionKind.Settle, 0, taken.Concat(swept).Select(c => c.Id).ToArray());
    }

    private void Sign()
    {
        if (IsResolved) return;
        Settle();
        SignedWarm = Mood == NegotiationMood.Warm;
        // Warm-sealed clauses that were agreed while Warm lapse if the mood
        // cooled before the handshake; the receipt says so.
        foreach (var c in Clauses.Where(c => c.IsAgreed && c.RequiresWarm))
            if (!SignedWarm) c.State = ClauseState.Refused;
        if (SignedWarm)
        {
            var sweet = ArchetypeBehavior.SweetenerClause(Data.Archetype);
            Clauses.Add(sweet);
            AddLog($"Warm close: they add {sweet.Text.ToLowerInvariant()}, and word of it will reach their people kindly.");
        }
        AddLog($"{Data.NpcName}: \"{Data.DialogueAccept}\"", NegotiationLogKind.NpcLine);
        SetExpression(NpcExpression.Pleased);
        Emit(ReactionKind.Sign, GetStarsProjected());
        Resolve(true, false);
    }

    private int GetStarsProjected() => StarsFor(Surplus());

    public void WalkAway()
    {
        if (IsResolved) return;
        PendingCounter = null;
        AddLog("You step away from the table. Nothing signs.");
        AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"", NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Walk, 0);
        Resolve(false, true);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NPC behaviour internals (spec §4)
    // ═══════════════════════════════════════════════════════════════════

    private Clause OpenRiderOf(Clause c)
    {
        if (c == null || !c.HasRider) return null;
        var r = Find(c.Rider);
        return r != null && r.IsOpen && r.Side == ClauseSide.Yours ? r : null;
    }

    private void AgreeClause(Clause c, Clause rider)
    {
        c.State = ClauseState.Agreed;
        c.Revealed = true;
        if (rider != null)
        {
            rider.State = ClauseState.Agreed;
            rider.Revealed = true;
            AddLog($"It comes with the {ShortName(rider)}, which you accept.");
        }
    }

    private void RefuseClause(Clause c, int deficit)
    {
        c.State = ClauseState.Refused;
        c.Position = -NegotiationTuning.TrackMax;
        bool insult = deficit >= NegotiationTuning.InsultDeficit;
        int from = Goodwill;
        SetExpression(NpcExpression.Displeased);
        AddLog(NegotiationBarks.RefuseBark(Data.Archetype, DeficitBand(deficit)), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Refuse, DeficitBand(deficit), from, c.Id);
        if (Mood == NegotiationMood.Cold)
        {
            Patience -= NegotiationTuning.ColdRefusePatienceExtra;
            AddLog("Cold: the refusal costs an extra beat of their patience.");
        }
        ApplyGoodwill(-(insult ? NegotiationTuning.InsultGoodwillCost : NegotiationTuning.RefuseGoodwillCost),
                      insult ? "insult" : "refusal");
    }

    private Clause PickCounterDemand(int deficit, Clause excludedRider)
    {
        var cands = OpenYours.Where(y => y != excludedRider).ToList();
        if (cands.Count == 0) return null;
        if (Data.ResolvedCounterStyle == CounterStyle.Greedy)
            return cands.OrderByDescending(y => y.NpcValue).First();
        var ok = cands.Where(y => y.NpcValue >= deficit).OrderBy(y => y.NpcValue).ToList();
        return ok.Count > 0 ? ok[0] : null;
    }

    /// <summary>Everything after a turn-costing verb: the clock, the beats,
    /// the walkout. A held clock (Patience token) skips all of it once.</summary>
    private void SpendTurn()
    {
        if (IsResolved) return;
        TurnNumber++;
        CardPlayedThisTurn = false;
        _bundleArmed = false;
        if (_waiting)
        {
            _waiting = false;
            AddLog("(Your patience holds the clock.)", NegotiationLogKind.Detail);
            DrawCards(NegotiationTuning.DrawPerTurn);
            RefreshNpcCards();
            StampDetail();
            return;
        }
        Patience--;
        if (Patience <= 0) { Leave(); return; }
        bool midNow = !_midBeatFired && Patience == BasePatience / 2;
        if (midNow)
        {
            _midBeatFired = true;
            MarkNpcCardPlayed(NpcCardKind.Mid);
            MidBeat();
        }
        else if (!IsResolved)
        {
            TheirPull(NegotiationTuning.TheirPullPerTurn);
        }
        if (!_finalBeatFired && Patience == 1)
        {
            _finalBeatFired = true;
            MarkNpcCardPlayed(NpcCardKind.Final);
            var y = TopWant();
            if (y != null)
            {
                _finalArmed = true;
                _finalTargetId = y.Id;
                _namedWantId = y.Id;
                SetExpression(NpcExpression.Displeased);
                AddLog(NegotiationBarks.FinalDemand(Data.Archetype, ShortName(y)), NegotiationLogKind.NpcLine);
                Emit(ReactionKind.Beat, 1, y.Id);
            }
        }
        DrawCards(NegotiationTuning.DrawPerTurn);
        RefreshNpcCards();
        StampDetail();
    }

    private void MarkNpcCardPlayed(NpcCardKind kind)
    {
        var n = NpcCards.FirstOrDefault(x => x.Kind == kind && !x.Played);
        if (n == null) return;
        n.Played = true;
        n.FaceUp = true;
    }

    private void MidBeat()
    {
        if (Data.Archetype == NpcArchetypeType.Opportunist)
        {
            // The visible twist: a rider appears on their most valuable open
            // clause. Announced, on the table, and strikeable with Persuade.
            var target = OpenTheirs.Where(c => !c.HasRider).OrderByDescending(c => c.Value).FirstOrDefault();
            if (target != null)
            {
                var r = new Clause
                {
                    Id = $"twist_{target.Id}",
                    Text = $"A finder's cut on the {ShortName(target)}",
                    ShortName = "finder's cut",
                    Side = ClauseSide.Yours, Kind = ClauseKind.Favor,
                    Value = NegotiationTuning.TwistValue, NpcValue = NegotiationTuning.TwistNpcValue,
                    ReputationDelta = 0, Ephemeral = true, Revealed = true,
                };
                Clauses.Add(r);
                target.Rider = r.Id;
                SetExpression(NpcExpression.Neutral);
                AddLog(NegotiationBarks.OpportunistTwist(ShortName(target)), NegotiationLogKind.NpcLine);
                Emit(ReactionKind.Beat, 0, target.Id, r.Id);
                return;
            }
        }
        var y = TopWant();
        if (y == null) return;
        _namedWantId = y.Id;
        AddLog(NegotiationBarks.MidDemand(Data.Archetype, ShortName(y)), NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Beat, 0, y.Id);
        // v3.3: the mid beat pulls their whole top category a step toward them.
        TheirCategoryPull(ClauseCategories.Of(y.Kind));
    }

    /// <summary>After the final warning, the only move that keeps them at
    /// the table is conceding the named clause. Returns false (and ends the
    /// table) when the attempted verb is anything else.</summary>
    private bool FinalGate(Clause conceding)
    {
        if (!_finalArmed || _waiting) return true;
        if (conceding != null && conceding.Id == _finalTargetId) return true;
        Leave();
        return false;
    }

    private void Leave()
    {
        if (IsResolved) return;
        PendingCounter = null;
        _theyLeft = true;
        SetExpression(NpcExpression.Displeased);
        AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"", NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Leave, 0);
        Resolve(false, false);
    }

    private void Collapse()
    {
        if (IsResolved) return;
        PendingCounter = null;
        _collapsed = true;
        SetExpression(NpcExpression.Displeased);
        AddLog(NegotiationBarks.CollapseLine(Data.Archetype), NegotiationLogKind.NpcLine);
        AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"", NegotiationLogKind.NpcLine);
        Emit(ReactionKind.Collapse, 0);
        Resolve(false, false);
    }

    private void CheckWarmReveal()
    {
        if (_warmRevealFired || IsResolved || Mood != NegotiationMood.Warm) return;
        _warmRevealFired = true;
        var y = TopWant();
        if (y != null)
        {
            y.BandKnown = true;
            AddLog(NegotiationBarks.WarmReveal(Data.Archetype, ShortName(y)), NegotiationLogKind.NpcLine);
            Emit(ReactionKind.Reveal, 0, y.Id);
        }
        if (!string.IsNullOrEmpty(Data.Confidence))
            AddLog(Data.Confidence, NegotiationLogKind.Scene);
    }

    /// <summary>Goodwill delta with clamp, event, and the zero-collapse rule:
    /// a table whose goodwill is driven to nothing ends (and escalates where
    /// the counterpart escalates).</summary>
    private void ApplyGoodwill(int delta, string why)
    {
        if (delta == 0) return;
        int old = Goodwill;
        Goodwill = Mathf.Clamp(Goodwill + delta, NegotiationTuning.GoodwillMin, NegotiationTuning.GoodwillMax);
        if (Goodwill != old)
        {
            OnGoodwillChanged?.Invoke(old, Goodwill);
            AddLog($"Goodwill {(delta > 0 ? "rises" : "falls")} ({old} → {Goodwill}, {why}).", NegotiationLogKind.Detail);
        }
        if (delta < 0 && Goodwill <= NegotiationTuning.GoodwillMin)
            Collapse();
    }

    private void SetExpression(NpcExpression e) => Expression = e;

    private void Emit(ReactionKind kind, int band, params string[] clauseIds)
        => Emit(kind, band, Goodwill, clauseIds);

    private void Emit(ReactionKind kind, int band, int from, params string[] clauseIds)
    {
        var r = new NegotiationReaction { Kind = kind, Band = band, GoodwillFrom = from, GoodwillTo = Goodwill };
        foreach (var id in clauseIds)
            if (!string.IsNullOrEmpty(id)) r.ClauseIds.Add(id);
        OnReaction?.Invoke(r);
    }

    private void StampDetail() =>
        AddLog($"— turn {TurnNumber} · goodwill {Goodwill} · credit {Credit} · patience {Patience}",
               NegotiationLogKind.Detail);

    /// <summary>Value band for barks: 0 (0–1), 1 (2–3), 2 (4–5).</summary>
    public static int ValueBand(int v) => v <= 1 ? 0 : v <= 3 ? 1 : 2;
    /// <summary>Deficit band for refusals: 0 (1), 1 (2–3), 2 (4+).</summary>
    public static int DeficitBand(int d) => d <= 1 ? 0 : d <= 3 ? 1 : 2;
    /// <summary>Concede band: 0 shrug, 1 nod, 2 worth something, 3 eyes light.</summary>
    public static int ConcedeBand(int v) => v == 0 ? 0 : v == 1 ? 1 : v <= 3 ? 2 : 3;

    // ═══════════════════════════════════════════════════════════════════
    // School signature moves (spec §5d)
    // ═══════════════════════════════════════════════════════════════════

    private class TableSnapshot
    {
        public int Goodwill, Patience, TurnNumber;
        public string Grievance;
        public bool MidFired, FinalFired, FinalArmed, WarmRevealFired, Waiting, ShowOfPower, SqueezeSpent;
        public string FinalTargetId;
        public string NamedWantId;
        public List<ParleyCard> Deck, Hand, Discard;
        public ParleyCard LastPlayed;
        public bool CardPlayed, ForceArmed, BundleArmed, WarmToneUsed;
        public int HardCards;
        public List<(NpcCardKind Kind, bool FaceUp, bool Played, string Target)> NpcRows;
        public List<(string Id, ClauseState State, bool Revealed, bool BandKnown, bool Argued, bool Beguiled, int NpcValue, string Rider)> ClauseRows;
        public List<(string Id, int Position, bool Pulled)> Positions;
        public List<Clause> Ephemerals;
    }

    private void CaptureRewindPoint()
    {
        if (School != CardSchool.Chronomancer) return;
        _rewindPoint = new TableSnapshot
        {
            Goodwill = Goodwill, Patience = Patience, TurnNumber = TurnNumber, Grievance = Grievance,
            MidFired = _midBeatFired, FinalFired = _finalBeatFired, FinalArmed = _finalArmed,
            WarmRevealFired = _warmRevealFired, Waiting = _waiting, ShowOfPower = _showOfPowerArmed,
            SqueezeSpent = SqueezeSpent, FinalTargetId = _finalTargetId, NamedWantId = _namedWantId,
            Deck = new List<ParleyCard>(Deck), Hand = new List<ParleyCard>(Hand), Discard = new List<ParleyCard>(Discard),
            LastPlayed = LastPlayed, CardPlayed = CardPlayedThisTurn, ForceArmed = _forceArmed,
            BundleArmed = _bundleArmed, WarmToneUsed = _warmToneBonusUsed, HardCards = _hardCardsPlayed,
            NpcRows = NpcCards.Select(n => (n.Kind, n.FaceUp, n.Played, n.TargetClauseId)).ToList(),
            ClauseRows = Clauses.Select(c => (c.Id, c.State, c.Revealed, c.BandKnown, c.Argued, c.Beguiled, c.NpcValue, c.Rider)).ToList(),
            Positions = Clauses.Select(c => (c.Id, c.Position, c.PulledByThem)).ToList(),
            Ephemerals = Clauses.Where(c => c.Ephemeral).ToList(),
        };
    }

    private void RestoreRewindPoint()
    {
        var s = _rewindPoint;
        _rewindPoint = null;
        int oldGw = Goodwill;
        Goodwill = s.Goodwill; Patience = s.Patience; TurnNumber = s.TurnNumber; Grievance = s.Grievance;
        _midBeatFired = s.MidFired; _finalBeatFired = s.FinalFired; _finalArmed = s.FinalArmed;
        _warmRevealFired = s.WarmRevealFired; _waiting = s.Waiting; _showOfPowerArmed = s.ShowOfPower;
        SqueezeSpent = s.SqueezeSpent; _finalTargetId = s.FinalTargetId; _namedWantId = s.NamedWantId;
        Deck = new List<ParleyCard>(s.Deck); Hand = new List<ParleyCard>(s.Hand); Discard = new List<ParleyCard>(s.Discard);
        LastPlayed = s.LastPlayed; CardPlayedThisTurn = s.CardPlayed; _forceArmed = s.ForceArmed;
        _bundleArmed = s.BundleArmed; _warmToneBonusUsed = s.WarmToneUsed; _hardCardsPlayed = s.HardCards;
        foreach (var row in s.NpcRows)
        {
            var n = NpcCards.FirstOrDefault(x => x.Kind == row.Kind);
            if (n == null) continue;
            n.FaceUp = row.FaceUp; n.Played = row.Played; n.TargetClauseId = row.Target;
        }
        if (Grievance == null) NpcCards.RemoveAll(n => n.Kind == NpcCardKind.Squeeze && !n.Played);
        PendingCounter = null;
        // Ephemeral clauses added since the snapshot vanish; the rest restore.
        Clauses.RemoveAll(c => c.Ephemeral && !s.Ephemerals.Contains(c));
        foreach (var row in s.ClauseRows)
        {
            var c = Find(row.Id);
            if (c == null) continue;
            c.State = row.State; c.Revealed = row.Revealed; c.BandKnown = row.BandKnown; c.Argued = row.Argued;
            c.Beguiled = row.Beguiled; c.NpcValue = row.NpcValue; c.Rider = row.Rider;
        }
        foreach (var row in s.Positions)
        {
            var c = Find(row.Id);
            if (c != null) { c.Position = row.Position; c.PulledByThem = row.Pulled; }
        }
        if (oldGw != Goodwill) OnGoodwillChanged?.Invoke(oldGw, Goodwill);
    }

    public static string SchoolMoveName(CardSchool s) => s switch
    {
        CardSchool.Adept => "Improvise",
        CardSchool.Elementalist => "Show of Power",
        CardSchool.Druid => "Quiet Grove",
        CardSchool.Necromancer => "Commune",
        CardSchool.Tinker => "Fabricate",
        CardSchool.Enchanter => "Beguiling Weave",
        CardSchool.Arcanist => "Omniscient Read",
        CardSchool.Chronomancer => "Rewind",
        _ => "Signature Move",
    };

    public string SchoolMoveDescription() => School switch
    {
        CardSchool.Adept => "Reach for what comes to hand: draw two cards.",
        CardSchool.Elementalist => $"Your next ask is weighed with {NegotiationTuning.ShowOfPowerCreditBonus} extra credit. Uses your turn.",
        CardSchool.Druid => $"The room calms: goodwill +{NegotiationTuning.QuietGroveGoodwill}.",
        CardSchool.Necromancer => "The dead know what they want: learn their two highest valuations.",
        CardSchool.Tinker => "A fabricated device joins your side of the table, worth 3 to anyone.",
        CardSchool.Enchanter => "They forget what one agreed clause cost them. Uses your turn.",
        CardSchool.Arcanist => "Know every valuation on the table.",
        CardSchool.Chronomancer => "Unwind your last action as if it never happened.",
        _ => "",
    };

    public bool CanUseSchoolMove()
    {
        if (IsResolved || SchoolMoveUsed || PendingCounter != null) return false;
        return School switch
        {
            CardSchool.Chronomancer => _rewindPoint != null,
            CardSchool.Enchanter => Clauses.Any(c => c.IsAgreed && c.Side == ClauseSide.Theirs && !c.Beguiled),
            CardSchool.Adept => Deck.Count > 0,
            _ => true,
        };
    }

    /// <summary>Use the school's once-per-table signature move. Free unless
    /// noted. The parameter is a v3 leftover (Adept picked a token); ignored.</summary>
    public bool UseSchoolMove(LeverageToken chosenToken = LeverageToken.Charm)
    {
        if (!CanUseSchoolMove()) return false;
        if (School is CardSchool.Elementalist or CardSchool.Enchanter && !FinalGate(null)) return false;
        AddLog(NegotiationBarks.SchoolMoveLine(School));
        switch (School)
        {
            case CardSchool.Chronomancer:
                SchoolMoveUsed = true;
                RestoreRewindPoint();
                AddLog("The moment repeats. The table is as you left it.");
                Emit(ReactionKind.SchoolMove, 0);
                StampDetail();
                return true;

            case CardSchool.Necromancer:
            {
                SchoolMoveUsed = true;
                var top = Clauses.Where(c => c.IsOpen).OrderByDescending(c => c.NpcValue)
                                 .Take(NegotiationTuning.CommuneReveals).ToList();
                foreach (var c in top) c.Revealed = true;
                AddLog("  · The dead whisper: " + string.Join("; ", top.Select(c => $"the {ShortName(c)} is worth {c.NpcValue} to them")) + ".");
                Emit(ReactionKind.Reveal, 0, top.Select(c => c.Id).ToArray());
                return true;
            }

            case CardSchool.Enchanter:
            {
                CaptureRewindPoint();
                SchoolMoveUsed = true;
                var c = Clauses.Where(x => x.IsAgreed && x.Side == ClauseSide.Theirs && !x.Beguiled)
                               .OrderByDescending(x => x.NpcValue).First();
                c.Beguiled = true;
                AddLog($"  · They no longer remember what the {ShortName(c)} cost them.");
                Emit(ReactionKind.SchoolMove, 0, c.Id);
                SpendTurn();
                return true;
            }

            case CardSchool.Arcanist:
            {
                SchoolMoveUsed = true;
                var all = Clauses.Where(c => c.IsOpen && !c.Revealed).ToList();
                foreach (var c in Clauses) c.Revealed = true;
                AddLog("  · Every valuation on the table is known to you.");
                Emit(ReactionKind.Reveal, 0, all.Select(c => c.Id).ToArray());
                return true;
            }

            case CardSchool.Druid:
            {
                SchoolMoveUsed = true;
                int from = Goodwill;
                ApplyGoodwill(NegotiationTuning.QuietGroveGoodwill, "quiet grove");
                Emit(ReactionKind.SchoolMove, 0, from);
                CheckWarmReveal();
                return true;
            }

            case CardSchool.Tinker:
            {
                SchoolMoveUsed = true;
                var c = new Clause
                {
                    Id = $"fabricated_{Clauses.Count}",
                    Text = "A fabricated device", ShortName = "device",
                    Side = ClauseSide.Yours, Kind = ClauseKind.Goods,
                    Value = NegotiationTuning.FabricateValue, NpcValue = NegotiationTuning.FabricateNpcValue,
                    Ephemeral = true, Revealed = true,
                };
                Clauses.Add(c);
                Emit(ReactionKind.SchoolMove, 0, c.Id);
                return true;
            }

            case CardSchool.Adept:
                SchoolMoveUsed = true;
                DrawCards(2, allowOverfill: true);
                Emit(ReactionKind.SchoolMove, 0);
                return true;

            case CardSchool.Elementalist:
                CaptureRewindPoint();
                SchoolMoveUsed = true;
                _showOfPowerArmed = true;
                AddLog("  · Your next ask carries the weight of what they just saw.");
                Emit(ReactionKind.SchoolMove, 0);
                SpendTurn();
                return true;

            default:
                return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Building / chronicle hooks
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Courier Station dossier, applied once at table-open. Tier 1:
    /// one valuation known. Tier 2: two. Tier 3: +1 Insight as well.</summary>
    public void ApplyCourierDossier(int tier)
    {
        if (tier <= 0 || IsResolved) return;
        AddLog("A courier dossier reached you ahead of this meeting.");
        int n = tier >= 2 ? NegotiationTuning.CourierTier2Reveals : NegotiationTuning.CourierTier1Reveals;
        RevealTopWants(n, "  · The dossier notes what they want most");
        if (tier >= 3)
        {
            for (int i = 0; i < NegotiationTuning.CourierTier3InsightBonus; i++)
                AddCardToHand(ParleyCardLibrary.TokenCardId(LeverageToken.Insight), "Spy-network briefing");
        }
    }

    /// <summary>War Room: one valuation known at table-open.</summary>
    public void ApplyWarRoom()
    {
        if (IsResolved) return;
        RevealTopWants(NegotiationTuning.WarRoomReveals, "  · The War Room's briefing names their price");
    }

    /// <summary>§6b: the last table with this counterpart in THIS life walks
    /// back in the door. The goodwill shift is applied by the manager
    /// pre-init; this logs the line.</summary>
    public void ApplyContinuity(string lastOutcome, int lastStars)
    {
        if (IsResolved || string.IsNullOrEmpty(lastOutcome)) return;
        var kind = lastOutcome switch
        {
            "Signed" => lastStars >= 4 ? NegotiationContinuityKind.WarmReturn : NegotiationContinuityKind.CoolReturn,
            "WalkedAway" => NegotiationContinuityKind.WalkedBefore,
            "Collapsed" => NegotiationContinuityKind.CollapsedBefore,
            _ => NegotiationContinuityKind.TimedOutBefore,
        };
        AddLog(NegotiationBarks.ContinuityLine(Data.Archetype, kind), NegotiationLogKind.NpcLine);
    }

    /// <summary>§6c: cross-timeline familiarity. Each prior life's table at
    /// this counterpart reveals one more of their valuations (unbounded,
    /// idempotent with the dossier).</summary>
    public void ApplyChronicleFamiliarity(int priorTables, bool sawCollapse)
    {
        if (priorTables <= 0 || IsResolved) return;
        AddLog(priorTables == 1
            ? "You have sat at this table before, in a life they do not remember."
            : $"You have sat at this table in {priorTables} other lives. They remember none of them.");
        RevealTopWants(priorTables, "  · You remember what they wanted");
        if (sawCollapse)
            AddLog($"  · You remember exactly how this goes wrong: \"{Data.DialogueWalkaway}\"");
    }

    /// <summary>§6c: a completed archmage dossier arms one extra argument.</summary>
    public void ApplyDossierSeam()
    {
        if (IsResolved) return;
        AddLog("You know the master this table answers to, down to the flaws. The argument half writes itself.");
        AddCardToHand(ParleyCardLibrary.TokenCardId(LeverageToken.Persuade), "Completed dossier");
    }

    private void RevealTopWants(int n, string prefix)
    {
        var picks = Clauses.Where(c => c.IsOpen && !c.AnyKnown && c.Side == ClauseSide.Yours)
                           .OrderByDescending(c => c.NpcValue).Take(n).ToList();
        if (picks.Count == 0)
            picks = Clauses.Where(c => c.IsOpen && !c.AnyKnown).OrderByDescending(c => c.NpcValue).Take(n).ToList();
        if (picks.Count == 0) return;
        foreach (var c in picks) c.BandKnown = true;
        AddLog(prefix + ": " + string.Join("; ", picks.Select(c => $"the {ShortName(c)} ({ParleyTells.Band(c.NpcValue)} to them)")) + ".");
        Emit(ReactionKind.Reveal, 0, picks.Select(c => c.Id).ToArray());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Outcomes (payload totals over AGREED clauses)
    // ═══════════════════════════════════════════════════════════════════

    private IEnumerable<Clause> Signed => DealAccepted ? Agreed : Enumerable.Empty<Clause>();

    public int GetGoldOutcome() => Signed.Sum(c => c.GoldDelta);
    public int ProjectGold() => Agreed.Sum(c => c.GoldDelta);
    public int GetSuppliesOutcome() => Signed.Sum(c => c.SuppliesDelta);
    public int ProjectSupplies() => Agreed.Sum(c => c.SuppliesDelta);
    public int GetFuelOutcome() => Signed.Sum(c => c.FuelDelta);
    public int ProjectFuel() => Agreed.Sum(c => c.FuelDelta);
    public int GetReputationOutcome() =>
        DealAccepted ? Agreed.Sum(c => c.ReputationDelta) + (SignedWarm ? NegotiationTuning.WarmRepBonus : 0) : 0;
    public int ProjectReputation() =>
        Agreed.Sum(c => c.ReputationDelta) + (Mood == NegotiationMood.Warm ? NegotiationTuning.WarmRepBonus : 0);
    public bool GetSupplyIntelOutcome() => Signed.Any(c => c.RevealsSupplyCaches);
    public string GetSpellOutcome() => Signed.FirstOrDefault(c => !string.IsNullOrEmpty(c.SpellId))?.SpellId ?? "";
    public bool HasSpellClauseOnTable() => Clauses.Any(c => (c.IsOpen || c.IsAgreed) && !string.IsNullOrEmpty(c.SpellId));
    public List<string> GetLoreOutcome() => Signed.Where(c => !string.IsNullOrEmpty(c.LoreUnlock)).Select(c => c.LoreUnlock).Distinct().ToList();
    public int GetChartRadiusOutcome() => Signed.Select(c => c.ChartRadius).DefaultIfEmpty(0).Max();
    public List<string> GetRevealPoiKindsOutcome() => Signed.SelectMany(c => c.RevealPoiKinds).Distinct().ToList();
    public bool GetAnchorOutcome() => Signed.Any(c => c.SupplyAnchorHere);
    public int GetSafeConductOutcome() => Signed.Select(c => c.SafeConductSteps).DefaultIfEmpty(0).Max();

    // ═══════════════════════════════════════════════════════════════════
    // Plumbing
    // ═══════════════════════════════════════════════════════════════════

    private void Resolve(bool dealAccepted, bool playerWalked)
    {
        IsResolved = true;
        DealAccepted = dealAccepted;
        PlayerWalkedAway = playerWalked;
        PendingCounter = null;
        OnResolved?.Invoke();
    }

    private void AddLog(string message, NegotiationLogKind kind = NegotiationLogKind.Scene)
    {
        if (string.IsNullOrEmpty(message)) return;
        Log.Add(message);
        OnLogEntry?.Invoke(message, kind);
    }

    /// <summary>Display handle for a clause in barks and on the receipt.
    /// Prefers the authored ShortName; falls back to the first few words.</summary>
    public static string ShortName(Clause c)
    {
        if (c == null) return "";
        if (!string.IsNullOrEmpty(c.ShortName)) return c.ShortName;
        if (string.IsNullOrEmpty(c.Text)) return c.Id;
        var words = c.Text.Split(' ');
        int n = Mathf.Min(4, words.Length);
        string s = string.Join(" ", words.Take(n)).TrimEnd('.', ',', ';', ':');
        return words.Length > n ? s + "…" : s;
    }
}
