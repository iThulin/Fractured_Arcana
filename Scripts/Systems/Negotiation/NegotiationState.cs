using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// NegotiationState.cs
//
// Purpose:        Pure state machine for one negotiation
//                 encounter. v2 core loop: term board (sliders),
//                 two-sided token economy, stances (Module A),
//                 closing squeeze (Module B), tension as risk
//                 governor. Decoupled from UI; consumed by
//                 NegotiationManager.
// Layer:          Data
// Collaborators:  NegotiationEncounterData (input),
//                 NpcArchetype.cs (enums + archetype tables),
//                 NegotiationBarks.cs (NPC-turn bark content),
//                 NegotiationManager.cs (drives this)
// See:            README §6 (Negotiation);
//                 negotiation_redesign_v1.md §3 (core), §6 (map)
// ============================================================
//
// v2 rewrite notes (Phase 0 bug fixes folded in):
//   - Patience is ONE ledger (TokenPool). The PatienceUsed /
//     MaxPatience shadow counter is gone, along with the
//     "everyone gets 2" floor. Patience is earned (Chronomancer,
//     Stoic companions, buildings) and playing it does what the
//     design doc always said: the NPC's patience clock does NOT
//     tick, the NPC does NOT act, and their mood rerolls. The
//     free Pass() is the unpaid version (clock ticks, NPC acts).
//   - Chronomancer/Necromancer school tokens fixed (Chronomancer
//     previously fell to default and got 1 Persuade).
//   - Insight can no longer hurt you: revealed terms join the
//     board and can be pulled/defanged, instead of silently
//     summing into the payout.

/// <summary>Reading tier of a log entry. The manager styles and filters by
/// this. Dialogue: lines spoken at the table, theirs and yours, the primary
/// reading layer. Scene: stage direction and narration. Detail: the sim
/// readout (clause slides, tension math, turn stamps), hidden unless the
/// player turns on table details.</summary>
public enum NegotiationLogKind { Dialogue, Scene, Detail }

/// <summary>What the NPC will do on their next turn: the priority ladder's
/// verdict at the current board. Computed by
/// <see cref="NegotiationState.PredictNpcAction"/> and consumed by BOTH
/// NpcTurn and the UI (intent line, clause-card threat markers), so the
/// tell can never lie.</summary>
public enum NpcMoveKind { Poise, Pull, Rework, Threat, Gift, Hold }

/// <summary>How the NPC's CURRENT stance receives a token: the rack's
/// timing glyph (✓ / · / ✗). Computed by
/// <see cref="NegotiationState.TimingFor"/> from the same stance rules
/// PlayPress/PlayOffering apply, so the glyph can never lie.</summary>
public enum TokenTiming { Favorable, Indifferent, Poor }

/// <summary>One clause slide within the current exchange: who moved it,
/// from which notch to which. The manager draws these as move markers and
/// ghost trails on the clause cards.</summary>
public class NegotiationTermMove
{
    public string TermId = "";
    public int From;
    public int To;
    public bool ByPlayer;
}

/// <summary>State machine for one in-progress negotiation. Holds the live
/// tension meter, the term board, both token pools (yours and the NPC's),
/// the stance, and resolution state. UI-agnostic; <see cref="NegotiationManager"/>
/// wraps this and renders.</summary>
public class NegotiationState
{
    // ── Encounter data ──────────────────────────────────────────────────
    public NegotiationEncounterData Data { get; private set; }

    // ── Tension meter (1-10) ────────────────────────────────────────────
    public int Tension { get; private set; } = 4;
    public const int TensionMin = 1;
    public const int TensionMax = 10;

    public TensionZone Zone => Tension switch
    {
        <= NegotiationTuning.CordialMax => TensionZone.Cordial,
        <= NegotiationTuning.StrainedMax => TensionZone.Strained,
        _ => TensionZone.Hostile
    };

    // ── Player token pool ────────────────────────────────────────────────
    public Dictionary<LeverageToken, int> TokenPool { get; private set; } = new();

    // ── NPC pool (v2: they spend too) ────────────────────────────────────
    public Dictionary<NpcResource, int> NpcPool { get; private set; } = new();
    public string ResolveName => ArchetypeBehavior.ResolveDisplayName(Data.Archetype);
    public string GuileName => ArchetypeBehavior.GuileDisplayName(Data.Archetype);
    public string PoiseName => ArchetypeBehavior.PoiseDisplayName(Data.Archetype);

    // ── Stance (Module A) ────────────────────────────────────────────────
    public NpcStance Stance { get; private set; } = NpcStance.Guarded;
    /// <summary>True after an Insight mood-read, until the round turns.</summary>
    public bool NextStanceKnown { get; private set; } = false;
    private NpcStance _nextStance = NpcStance.Guarded;
    public NpcStance PeekNextStance() => _nextStance;   // UI shows only if NextStanceKnown

    // ── Patience / turns ─────────────────────────────────────────────────
    public int NpcPatience { get; private set; }
    public int TurnNumber { get; private set; } = 0;

    // ── Deal terms ───────────────────────────────────────────────────────
    public List<DealTerm> Terms => Data.Terms;
    public List<DealTerm> RevealedTerms =>
        Terms.Where(t => !t.IsHidden || t.IsAccepted).ToList();
    /// <summary>Terms a Press/Offer may currently target.</summary>
    public List<DealTerm> PullableTerms() =>
        Terms.Where(t => !t.IsHidden && !t.Locked && t.Position < 2).ToList();

    // ── Resolution ───────────────────────────────────────────────────────
    public bool IsResolved { get; private set; } = false;
    public bool DealAccepted { get; private set; } = false;
    public bool PlayerWalkedAway { get; private set; } = false;

    // ── Squeeze (Module B) ───────────────────────────────────────────────
    public bool SqueezeSpent { get; private set; } = false;

    // ── School signature move (Phase 5) ──────────────────────────────────
    public CardSchool School { get; private set; } = CardSchool.Adept;
    public bool SchoolMoveUsed { get; private set; } = false;
    private bool _omniscient = false;         // Arcanist: next stance stays known
    private bool _freeOfferingArmed = false;  // Tinker: next Offering doesn't feed Resolve
    private TableSnapshot _rewindPoint = null; // Chronomancer: the last exchange

    // ── Telemetry (read by NegotiationTelemetry at resolution) ───────────
    public Dictionary<LeverageToken, int> PlayedCounts { get; private set; } = new();
    public bool SqueezeWasOffered { get; private set; } = false;
    public bool SqueezeWasHeld { get; private set; } = false;
    public bool SqueezeDidBlink { get; private set; } = false;

    // ── Log ──────────────────────────────────────────────────────────────
    public List<string> Log { get; private set; } = new();

    // ── Last exchange (move markers) ─────────────────────────────────────
    /// <summary>Every clause slide since the player last acted: the UI's
    /// move markers. Cleared at the top of each player action, so it always
    /// answers "what just changed, and who changed it?"</summary>
    public List<NegotiationTermMove> LastExchange { get; } = new();

    // ── Events ───────────────────────────────────────────────────────────
    public event Action<int, int> OnTensionChanged;   // oldTension, newTension
    public event Action<string, NegotiationLogKind> OnLogEntry;
    public event Action OnResolved;
    public event Action OnStanceChanged;              // portrait hook (Phase 4 art)

    // ── Internal flags ────────────────────────────────────────────────────
    private bool _npcHardened = false;   // Intimidate-into-Guarded: next NPC pull +1
    private bool _giftGiven = false;     // one goodwill gift per table
    private bool _resolveEmptyAnnounced; // "their Greed is spent" fired once
    private bool _guileEmptyAnnounced;   // guile-exhausted line fired once
    private bool _smallPrintHinted = false;  // §4c tip-of-the-hand fired once
    private bool _squeezeSeedBarked = false; // §5c closing-demand seed fired once
    private bool _grudgeArmed = false;       // §6b: collapsed history opens on Guile
    private bool _firstTableGuidance = false; // §4e mentor lines, first table ever
    private bool _stanceGuidanceGiven = false;
    private bool _shakeGuidanceGiven = false;

    // ── Init ─────────────────────────────────────────────────────────────

    public void Initialize(NegotiationEncounterData data, CardSchool wizardSchool,
                           List<Companion> party, int factionReputation = 0,
                           LeverageToken patronToken = LeverageToken.Connections,
                           int patronTokenCount = 0)
    {
        Data = data;
        School = wizardSchool;
        NpcPatience = data.BasePatience;

        // Set starting tension from faction reputation
        Tension = data.StartingTension + factionReputation switch
        {
            >= 2 => -2,   // Allied
            >= 1 => -1,   // Friendly
            <= -2 => 4,    // Hostile  ← must come before <= -1
            <= -1 => 2,    // Unfriendly
            _ => 0     // Neutral
        };
        Tension = Mathf.Clamp(Tension, TensionMin, TensionMax);

        // Build token pool from wizard school + companions
        BuildTokenPool(wizardSchool, party);

        // Court patron backing (C5): a courtier secured as the guild's Patron at
        // this kingdom's court lends a leverage token of THEIR archetype's type
        // (§ Court a Courtier). Who you courted shapes the bonus. Applied here,
        // not inside BuildTokenPool, because that method clears the pool first.
        if (patronTokenCount > 0)
        {
            TokenPool[patronToken] += patronTokenCount;
            AddLog($"A patron at court backs you: +{patronTokenCount} {patronToken}.",
                   NegotiationLogKind.Detail);
        }

        // v2: NPC pool. Authored per encounter, else archetype default.
        var (resolve, guile, poise) = ArchetypeBehavior.DefaultNpcPool(data.Archetype);
        NpcPool[NpcResource.Resolve] = data.NpcResolve >= 0 ? data.NpcResolve : resolve;
        NpcPool[NpcResource.Guile] = data.NpcGuile >= 0 ? data.NpcGuile : guile;
        NpcPool[NpcResource.Poise] = data.NpcPoise >= 0 ? data.NpcPoise : poise;

        // Fairness invariant [SIM 2026-07-30]: the clock must leave room to
        // out-play their pool. With patience < Resolve+Guile+margin the table
        // is unwinnable-positive regardless of skill (Monte Carlo: Commander
        // at design-doc patience 4 lost ~13g even for an informed bot).
        NpcPatience = Mathf.Max(NpcPatience,
            NpcPool[NpcResource.Resolve] + NpcPool[NpcResource.Guile]
            + NegotiationTuning.PatienceFloorOverPool);
        _resolveEmptyAnnounced = NpcPool[NpcResource.Resolve] == 0;
        _guileEmptyAnnounced = NpcPool[NpcResource.Guile] == 0;

        // v2: term board init, positions and weights.
        foreach (var term in Terms)
        {
            term.Position = term.StartingPosition == DealTerm.UNAUTHORED
                ? -1                                   // their opening offer favors them
                : Mathf.Clamp(term.StartingPosition, -2, 2);
            if (term.Weight <= 0)
                term.Weight = DeriveWeight(term);
            term.Locked = false;
        }

        // v2: opening stance + honest pre-rolled next.
        Stance = ArchetypeBehavior.RollStance(Zone, GD.Randi());
        _nextStance = ArchetypeBehavior.RollStance(Zone, GD.Randi());

        AddLog($"Negotiation begins. {data.NpcName} presents their terms.");
        AddLog(data.OpeningText, NegotiationLogKind.Dialogue);
        AddLog(NegotiationBarks.StanceTell(Data.Archetype, Stance));

        // First-table onboarding (spec §4e): guild training surfacing at the
        // natural moments, once ever, in the fiction rather than a tutorial.
        _firstTableGuidance = SaveManager.ActiveSave?.Ledger?.DeedCounts != null
            && !SaveManager.ActiveSave.Ledger.DeedCounts.ContainsKey("negotiation_resolved");
        if (_firstTableGuidance)
            AddLog("Your guild training surfaces: the meter by their portrait is " +
                   "the temper of the room. Read it before you spend anything.");
    }

    private static int DeriveWeight(DealTerm t)
    {
        int w = Mathf.RoundToInt(Mathf.Abs(t.GoldDelta) / 15f)
              + Mathf.Abs(t.ReputationDelta)
              + (string.IsNullOrEmpty(t.SpellId) ? 0 : 2)
              + Mathf.CeilToInt(Mathf.Abs(t.StepsDelta) / 2f)
              // Supplies weigh heavier than gold per unit (~10:15). Provisions
              // are war material, and the AI must not treat a supply clause as
              // fine print (the Rework target is the LOWEST-weight term).
              + Mathf.RoundToInt(Mathf.Abs(t.SuppliesDelta) / 10f);
        return Mathf.Max(1, w);
    }

    // ── Token pool building ──────────────────────────────────────────────

    private void BuildTokenPool(CardSchool school, List<Companion> party)
    {
        // Reset
        TokenPool.Clear();
        foreach (LeverageToken t in Enum.GetValues(typeof(LeverageToken)))
            TokenPool[t] = 0;

        // Wizard school innate tokens
        switch (school)
        {
            // [SIM] SchoolTokenCount copies of each innate token. Doubled
            // school identity is what opens 3-4★ deals to skilled play.
            case CardSchool.Enchanter:
                TokenPool[LeverageToken.Charm] += NegotiationTuning.SchoolTokenCount;
                TokenPool[LeverageToken.Connections] += NegotiationTuning.SchoolTokenCount;
                break;
            case CardSchool.Arcanist:
                TokenPool[LeverageToken.Persuade] += NegotiationTuning.SchoolTokenCount;
                TokenPool[LeverageToken.Insight] += NegotiationTuning.SchoolTokenCount;
                break;
            case CardSchool.Necromancer:
                // Phase 0 fix: design gives Necromancer Intimidate + Persuade.
                TokenPool[LeverageToken.Intimidate] += NegotiationTuning.SchoolTokenCount;
                TokenPool[LeverageToken.Persuade] += NegotiationTuning.SchoolTokenCount;
                break;
            case CardSchool.Elementalist:
                TokenPool[LeverageToken.Intimidate] += NegotiationTuning.SchoolTokenCount;
                TokenPool[LeverageToken.Demonstration] += NegotiationTuning.SchoolTokenCount;
                break;
            case CardSchool.Tinker:
                TokenPool[LeverageToken.Offering] += NegotiationTuning.SchoolTokenCount;
                break;
            case CardSchool.Chronomancer:
                // Phase 0 fix: the Patience school previously fell to default
                // and got 1 Persuade. Patience + Insight per the design doc.
                TokenPool[LeverageToken.Patience] += NegotiationTuning.SchoolTokenCount;
                TokenPool[LeverageToken.Insight] += NegotiationTuning.SchoolTokenCount;
                break;
            default:
                // Adept / Druid: no authored profile yet, so generalist Persuade.
                TokenPool[LeverageToken.Persuade] += NegotiationTuning.SchoolTokenCount;
                break;
        }

        // [SIM] Universal floors: everyone can make an argument and everyone
        // can put SOMETHING on the table. The exchange economy needs legs.
        TokenPool[LeverageToken.Persuade] += NegotiationTuning.UniversalPersuade;
        TokenPool[LeverageToken.Offering] = Mathf.Max(
            TokenPool[LeverageToken.Offering], NegotiationTuning.BaseOfferingFloor);

        // Every wizard gets one Demonstration per negotiation
        if (TokenPool[LeverageToken.Demonstration] == 0)
            TokenPool[LeverageToken.Demonstration]++;

        // Companion contributions
        if (party != null)
        {
            foreach (var companion in party)
            {
                switch (companion.PersonalityTrait)
                {
                    case "Reckless":
                        TokenPool[LeverageToken.Intimidate]++;
                        break;
                    case "Stoic":
                        TokenPool[LeverageToken.Patience]++;
                        break;
                    case "Cunning":
                        TokenPool[LeverageToken.Insight]++;
                        break;
                    case "Charming":
                        TokenPool[LeverageToken.Charm]++;
                        break;
                    case "Scholarly":
                        TokenPool[LeverageToken.Persuade]++;
                        break;
                }
            }
        }

        // Building contributions
        var save = SaveManager.ActiveSave;
        if (save != null)
        {
            foreach (var buildingSave in save.Buildings)
            {
                if (buildingSave.Tier <= 0)
                    continue;
                var tierData = BuildingDatabase.GetCurrentTierData(buildingSave.Id, save);
                if (tierData == null)
                    continue;
                if (tierData.BonusNegotiationTokens <= 0)
                    continue;

                if (System.Enum.TryParse<LeverageToken>(
                    tierData.BonusTokenType, out var tokenType))
                {
                    TokenPool[tokenType] += tierData.BonusNegotiationTokens;
                    GD.Print($"[Buildings] +{tierData.BonusNegotiationTokens} " +
                             $"{tokenType} from {buildingSave.Name}");
                }
            }
        }

        // Phase 0 fix: the "everyone gets 2 Patience" floor is GONE. Patience
        // is earned; the free Pass() below is the universal stall.

        AddLog($"Your leverage pool: " +
               string.Join(", ", TokenPool
                   .Where(kvp => kvp.Value > 0)
                   .Select(kvp => $"{kvp.Value}x {kvp.Key}")),
               NegotiationLogKind.Detail);
    }

    // ── Player actions (v2) ──────────────────────────────────────────────

    /// <summary>Press a clause with a social token (Charm / Persuade /
    /// Connections / Intimidate / Demonstration). Pulls the target toward
    /// the player, modified by the NPC's stance. Returns false if illegal.</summary>
    public bool PlayPress(LeverageToken token, DealTerm target)
    {
        if (IsResolved || target == null)
            return false;
        if (token is LeverageToken.Insight or LeverageToken.Patience
                  or LeverageToken.Offering)
            return false;
        if (TokenPool[token] <= 0)
        { AddLog($"You have no {token} tokens remaining."); return false; }
        if (!PullableTerms().Contains(target))
        { AddLog("That clause can't be moved right now."); return false; }

        BeginExchange();
        CaptureRewindPoint();
        TokenPool[token]--;
        PlayedCounts[token] = PlayedCounts.GetValueOrDefault(token) + 1;

        // Instant walk-away on Intimidate against Idealist (unchanged rule:
        // no stance softens a violated principle).
        if (Data.Archetype == NpcArchetypeType.Idealist
            && token == LeverageToken.Intimidate)
        {
            AddLog(ArchetypeBehavior.GetTokenEffect(Data.Archetype, token));
            AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"", NegotiationLogKind.Dialogue);
            Resolve(false, false);
            return true;
        }

        int baseDelta = ArchetypeBehavior.GetTensionDelta(Data.Archetype, token);
        int pull = 1;
        int delta = baseDelta;
        bool backfired = false;

        if (token == LeverageToken.Intimidate)
        {
            // Intimidation reads stances differently: fear lands on the
            // uncertain, hardens the guarded.
            switch (Stance)
            {
                case NpcStance.Wavering:
                    pull = NegotiationTuning.IntimidateWaveringPull;
                    delta = baseDelta + NegotiationTuning.IntimidateWaveringTension;
                    break;
                case NpcStance.Guarded:
                    pull = 1;
                    delta = baseDelta + NegotiationTuning.IntimidateGuardedTension;
                    _npcHardened = true;
                    AddLog("They harden under the threat. Expect them to pull back twice as hard.");
                    break;
                default:
                    pull = 1;
                    delta = baseDelta;
                    break;
            }
        }
        else
        {
            switch (Stance)
            {
                case NpcStance.Irritated:
                    pull = 0;
                    delta = NegotiationTuning.IrritatedBackfireTension;
                    backfired = true;
                    break;
                case NpcStance.Wavering:
                    pull = 1;
                    delta = baseDelta + NegotiationTuning.WaveringEase;
                    break;
                case NpcStance.Guarded:
                    pull = 1;
                    delta = baseDelta + NegotiationTuning.GuardedResent;
                    break;
                case NpcStance.Expansive:
                    pull = 1;
                    delta = baseDelta + NegotiationTuning.ExpansiveEase;
                    break;
                default:
                    pull = 1;
                    delta = baseDelta;
                    break;
            }
        }

        AddLog(NegotiationBarks.PressResolution(Stance, backfired));
        if (pull > 0)
            PullTerm(target, pull, byPlayer: true);
        ApplyTensionDelta(delta);
        FinishPlayerAction();
        return true;
    }

    /// <summary>Offer: the token crosses the table and becomes the NPC's
    /// Resolve, in exchange for a strong pull and cooler air. Eager moments
    /// double the pull; Guarded ones pocket the gift coldly.</summary>
    public bool PlayOffering(DealTerm target)
    {
        if (IsResolved || target == null)
            return false;
        if (TokenPool[LeverageToken.Offering] <= 0)
        { AddLog("You have no Offering tokens remaining."); return false; }
        if (!PullableTerms().Contains(target))
        { AddLog("That clause can't be moved right now."); return false; }

        BeginExchange();
        CaptureRewindPoint();
        TokenPool[LeverageToken.Offering]--;
        PlayedCounts[LeverageToken.Offering] = PlayedCounts.GetValueOrDefault(LeverageToken.Offering) + 1;
        if (_freeOfferingArmed)
        {
            // Tinker's Fabricate: covetable, but worthless to hoard.
            _freeOfferingArmed = false;
            AddLog("The fabricated marvel dazzles, but there's nothing in it to hoard. Their pool gains nothing.");
        }
        else
        {
            NpcPool[NpcResource.Resolve]++;   // the exchange economy, literally
            _resolveEmptyAnnounced = false;   // a refilled pool can run dry again
        }

        int baseDelta = ArchetypeBehavior.GetTensionDelta(Data.Archetype, LeverageToken.Offering);
        int pull;
        int delta;
        switch (Stance)
        {
            case NpcStance.Eager:
                pull = NegotiationTuning.OfferEagerPull;
                delta = baseDelta + NegotiationTuning.OfferEagerEase;
                break;
            case NpcStance.Guarded:
                pull = 1;
                delta = 0;
                break;
            default:
                pull = 1;
                delta = baseDelta;
                break;
        }

        AddLog(NegotiationBarks.OfferResolution(Stance, ResolveName));
        PullTerm(target, pull, byPlayer: true);
        ApplyTensionDelta(delta);
        FinishPlayerAction();
        return true;
    }

    /// <summary>Insight, use 1: flip the next face-down clause onto the board.
    /// A revealed clause can then be pulled/defanged like any other. Finding
    /// bad news is now the START of fixing it, not a payout penalty.</summary>
    public bool PlayInsightFlip()
    {
        if (IsResolved)
            return false;
        if (TokenPool[LeverageToken.Insight] <= 0)
        { AddLog("You have no Insight tokens remaining."); return false; }

        BeginExchange();
        CaptureRewindPoint();
        TokenPool[LeverageToken.Insight]--;
        PlayedCounts[LeverageToken.Insight] = PlayedCounts.GetValueOrDefault(LeverageToken.Insight) + 1;
        var hidden = Terms.FirstOrDefault(t => t.IsHidden && !t.IsAccepted);
        if (hidden != null)
        {
            hidden.IsHidden = false;
            AddLog($"Revealed hidden term: \"{hidden.Description}\"" +
                   (hidden.FavorPlayer
                        ? " They were holding more than they let on."
                        : " Now you can fight it."));
        }
        else
        {
            AddLog("No hidden terms remain. Your Insight finds nothing new.");
        }
        FinishPlayerAction();
        return true;
    }

    /// <summary>Insight, use 2: read the tells to learn the NPC's NEXT mood,
    /// so you can time the play that needs it.</summary>
    public bool PlayInsightRead()
    {
        if (IsResolved)
            return false;
        if (TokenPool[LeverageToken.Insight] <= 0)
        { AddLog("You have no Insight tokens remaining."); return false; }

        BeginExchange();
        CaptureRewindPoint();
        TokenPool[LeverageToken.Insight]--;
        PlayedCounts[LeverageToken.Insight] = PlayedCounts.GetValueOrDefault(LeverageToken.Insight) + 1;
        NextStanceKnown = true;
        AddLog($"You read the tells. Next they'll be {_nextStance}. Time your play.");
        FinishPlayerAction();
        return true;
    }

    /// <summary>Patience (the fixed token): skip your turn WITHOUT the NPC's
    /// patience ticking and WITHOUT the NPC acting, and their mood rerolls.
    /// A timing tool: you're fishing for the moment you need.</summary>
    public bool PlayPatience()
    {
        if (IsResolved)
            return false;
        if (TokenPool[LeverageToken.Patience] <= 0)
        { AddLog("You have no Patience tokens remaining."); return false; }

        BeginExchange();
        CaptureRewindPoint();
        TokenPool[LeverageToken.Patience]--;
        PlayedCounts[LeverageToken.Patience] = PlayedCounts.GetValueOrDefault(LeverageToken.Patience) + 1;
        TurnNumber++;
        AddLog("You let the silence stretch, unhurried. Their patience holds, and the moment shifts.");
        AdvanceStance();
        return true;
    }

    /// <summary>Pass: the free stall. The clock ticks and the NPC acts.
    /// This is what Patience looks like when you haven't paid for it.</summary>
    public bool Pass()
    {
        if (IsResolved)
            return false;
        BeginExchange();
        CaptureRewindPoint();
        AddLog("You say nothing, and let them stew.");
        FinishPlayerAction();
        return true;
    }

    // ── Squeeze (Module B) ────────────────────────────────────────────────

    /// <summary>The NPC's last demand at the handshake.</summary>
    public class SqueezeOffer
    {
        public DealTerm Target;
        public int OddsPercent;   // chance they blink if you hold firm; SHOWN to the player
    }

    /// <summary>The clause the NPC would squeeze at a handshake offered NOW:
    /// the visible clause the player has won furthest, the concession they
    /// most want back. Null when no squeeze would come: already spent, no
    /// worthwhile target, or (spec §5b, ruled 2026-08-31) their Resolve is
    /// exhausted. A counterpart with nothing left to want it WITH signs as
    /// written, which turns pool attrition into closing strategy. Single
    /// source of truth for BeginShake AND the UI's corner mark, so this
    /// tell, like the others, can never lie.</summary>
    public DealTerm PredictSqueezeTarget()
    {
        if (IsResolved || SqueezeSpent || NpcPool[NpcResource.Resolve] <= 0)
            return null;
        return Terms
            .Where(t => !t.IsHidden && t.Position > -2)
            .OrderByDescending(t => t.Position * t.Weight)
            .FirstOrDefault();
    }

    /// <summary>Begin closing. Returns the NPC's squeeze, or null when they
    /// sign as-is (squeeze already spent, Resolve exhausted, or nothing
    /// worth squeezing). In the null case the deal is ALREADY resolved when
    /// this returns.</summary>
    public SqueezeOffer BeginShake()
    {
        if (IsResolved)
            return null;

        var target = PredictSqueezeTarget();
        if (target == null)
        {
            if (!SqueezeSpent && NpcPool[NpcResource.Resolve] <= 0)
                AddLog($"Nothing left in them to haggle with: their {ResolveName} " +
                       "is spent, and the hand comes out clean.");
            AcceptDeal();
            return null;
        }

        int odds = Zone switch
        {
            TensionZone.Cordial => NegotiationTuning.SqueezeOddsCordial,
            TensionZone.Hostile => NegotiationTuning.SqueezeOddsHostile,
            _ => NegotiationTuning.SqueezeOddsStrained,
        };
        odds += Stance switch
        {
            NpcStance.Wavering => NegotiationTuning.SqueezeOddsWavering,
            NpcStance.Irritated => NegotiationTuning.SqueezeOddsIrritated,
            NpcStance.Guarded => NegotiationTuning.SqueezeOddsGuarded,
            _ => 0,
        };
        odds = Mathf.Clamp(odds, NegotiationTuning.SqueezeOddsMin, NegotiationTuning.SqueezeOddsMax);
        SqueezeWasOffered = true;

        AddLog(NegotiationBarks.SqueezeOpen(Data.Archetype, ShortName(target)),
               NegotiationLogKind.Dialogue);
        return new SqueezeOffer { Target = target, OddsPercent = odds };
    }

    /// <summary>Concede the squeeze: the target slides one notch their way
    /// and the deal signs.</summary>
    public void ResolveSqueezeConcede(SqueezeOffer offer)
    {
        if (IsResolved || offer == null)
            return;
        BeginExchange();   // the concede slide is the closing move's marker
        PullTerm(offer.Target, 1, byPlayer: false);
        AddLog("You let them have their amendment. The ink dries.");
        AcceptDeal();
    }

    /// <summary>Hold firm: the shown odds roll. Blink → signs as written.
    /// Bristle → +2 tension (which can collapse a hot table) and the
    /// negotiation continues; the next handshake signs without a squeeze.</summary>
    public bool ResolveSqueezeHoldFirm(SqueezeOffer offer)
    {
        if (IsResolved || offer == null)
            return false;
        SqueezeSpent = true;
        SqueezeWasHeld = true;
        if (GD.Randf() * 100f < offer.OddsPercent)
        {
            SqueezeDidBlink = true;
            AddLog(NegotiationBarks.SqueezeBlink, NegotiationLogKind.Dialogue);
            AcceptDeal();
            return true;
        }
        AddLog(NegotiationBarks.SqueezeBristle, NegotiationLogKind.Dialogue);
        ApplyTensionDelta(NegotiationTuning.SqueezeBristleTension);
        if (!IsResolved)
            AddLog("The handshake failed. The negotiation continues.");
        return false;
    }

    /// <summary>Withdraw the hand: back to the table, no cost.</summary>
    public void ResolveSqueezeWithdraw()
    {
        if (IsResolved)
            return;
        AddLog("You withdraw your hand. Not yet.");
    }

    /// <summary>
    /// Close the deal at current positions (post-squeeze, or squeeze-free).
    /// </summary>
    public void AcceptDeal()
    {
        if (IsResolved)
            return;
        AddLog($"You accept the terms. {Data.NpcName}: \"{Data.DialogueAccept}\"",
               NegotiationLogKind.Dialogue);
        Resolve(true, false);
    }

    /// <summary>
    /// Player walks away from the negotiation.
    /// </summary>
    public void WalkAway()
    {
        if (IsResolved)
            return;
        AddLog("You step away from the table. The negotiation ends without a deal.");
        Resolve(false, true);
    }

    // ── School signature moves (Phase 5) ─────────────────────────────────

    /// <summary>Full table snapshot for the Chronomancer's Rewind, captured
    /// at the top of every turn-consuming player action, restored on use.
    /// The log is deliberately NOT rewound: only you remember.</summary>
    private class TableSnapshot
    {
        public int Tension, NpcPatience, TurnNumber;
        public NpcStance Stance, NextStance;
        public bool NextKnown, Hardened, GiftGiven, SqueezeSpent;
        public Dictionary<LeverageToken, int> Pool;
        public Dictionary<NpcResource, int> NpcPoolCopy;
        public int[] TermPos;
        public bool[] TermHidden, TermLocked, TermAccepted;
    }

    private void CaptureRewindPoint()
    {
        if (School != CardSchool.Chronomancer)
            return;   // nobody else can use it
        _rewindPoint = new TableSnapshot
        {
            Tension = Tension,
            NpcPatience = NpcPatience,
            TurnNumber = TurnNumber,
            Stance = Stance,
            NextStance = _nextStance,
            NextKnown = NextStanceKnown,
            Hardened = _npcHardened,
            GiftGiven = _giftGiven,
            SqueezeSpent = SqueezeSpent,
            Pool = new Dictionary<LeverageToken, int>(TokenPool),
            NpcPoolCopy = new Dictionary<NpcResource, int>(NpcPool),
            TermPos = Terms.Select(t => t.Position).ToArray(),
            TermHidden = Terms.Select(t => t.IsHidden).ToArray(),
            TermLocked = Terms.Select(t => t.Locked).ToArray(),
            TermAccepted = Terms.Select(t => t.IsAccepted).ToArray(),
        };
    }

    private void RestoreRewindPoint()
    {
        var s = _rewindPoint;
        _rewindPoint = null;
        int oldTension = Tension;
        Tension = s.Tension;
        NpcPatience = s.NpcPatience;
        TurnNumber = s.TurnNumber;
        Stance = s.Stance;
        _nextStance = s.NextStance;
        NextStanceKnown = s.NextKnown;
        _npcHardened = s.Hardened;
        _giftGiven = s.GiftGiven;
        SqueezeSpent = s.SqueezeSpent;
        TokenPool = new Dictionary<LeverageToken, int>(s.Pool);
        NpcPool = new Dictionary<NpcResource, int>(s.NpcPoolCopy);
        for (int i = 0; i < Terms.Count && i < s.TermPos.Length; i++)
        {
            Terms[i].Position = s.TermPos[i];
            Terms[i].IsHidden = s.TermHidden[i];
            Terms[i].Locked = s.TermLocked[i];
            Terms[i].IsAccepted = s.TermAccepted[i];
        }
        if (oldTension != Tension)
            OnTensionChanged?.Invoke(oldTension, Tension);
        OnStanceChanged?.Invoke();
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
        CardSchool.Adept => "Gain one leverage token of your choice.",
        CardSchool.Elementalist => $"Pull a clause two steps and burn 1 of their {ResolveName}; tension +1. Uses your turn.",
        CardSchool.Druid => "The room calms: tension −2 and their mood shifts.",
        CardSchool.Necromancer => "The dead have watched them: learn their next mood and flip a face-down clause.",
        CardSchool.Tinker => "+1 Offering, and your next Offering doesn't feed their pool.",
        CardSchool.Enchanter => "Set their current mood to one of your choosing.",
        CardSchool.Arcanist => "Know their next mood for the rest of this negotiation.",
        CardSchool.Chronomancer => "Unwind the last exchange as if it never happened.",
        _ => "",
    };

    public bool CanUseSchoolMove()
    {
        if (IsResolved || SchoolMoveUsed)
            return false;
        return School switch
        {
            CardSchool.Chronomancer => _rewindPoint != null,
            CardSchool.Elementalist => PullableTerms().Count > 0,
            _ => true,
        };
    }

    /// <summary>Use the school's once-per-negotiation signature move. All
    /// moves are FREE actions (no clock tick, no NPC response) except the
    /// Elementalist's Show of Power, which plays like a turn. Args are
    /// per-school: Elementalist needs <paramref name="target"/>, Enchanter
    /// <paramref name="forcedStance"/>, Adept <paramref name="chosenToken"/>;
    /// the rest ignore them.</summary>
    public bool UseSchoolMove(DealTerm target = null,
                              NpcStance forcedStance = NpcStance.Wavering,
                              LeverageToken chosenToken = LeverageToken.Charm)
    {
        if (!CanUseSchoolMove())
            return false;
        if (School == CardSchool.Elementalist
            && (target == null || !PullableTerms().Contains(target)))
            return false;
        BeginExchange();   // a school move starts a fresh set of move markers
        switch (School)
        {
            case CardSchool.Chronomancer:
                SchoolMoveUsed = true;
                AddLog(NegotiationBarks.SchoolMoveLine(School));
                RestoreRewindPoint();
                AddLog("The moment repeats. The table is as you left it.");
                AddLog($"[Turn {TurnNumber} | Tension: {Tension}/10 | Patience: {NpcPatience}]",
                       NegotiationLogKind.Detail);
                return true;

            case CardSchool.Necromancer:
                {
                    SchoolMoveUsed = true;
                    AddLog(NegotiationBarks.SchoolMoveLine(School));
                    NextStanceKnown = true;
                    AddLog($"  · The dead whisper: next they'll be {_nextStance}.");
                    var hidden = Terms.FirstOrDefault(t => t.IsHidden && !t.IsAccepted);
                    if (hidden != null)
                    {
                        hidden.IsHidden = false;
                        AddLog($"  · And they remember the small print: \"{hidden.Description}\"");
                    }
                    return true;
                }

            case CardSchool.Enchanter:
                SchoolMoveUsed = true;
                AddLog(NegotiationBarks.SchoolMoveLine(School));
                Stance = forcedStance;
                AddLog(NegotiationBarks.StanceTell(Data.Archetype, Stance));
                OnStanceChanged?.Invoke();
                return true;

            case CardSchool.Arcanist:
                SchoolMoveUsed = true;
                _omniscient = true;
                NextStanceKnown = true;
                AddLog(NegotiationBarks.SchoolMoveLine(School));
                AddLog($"  · Next they'll be {_nextStance}, and you will always know.");
                return true;

            case CardSchool.Druid:
                SchoolMoveUsed = true;
                AddLog(NegotiationBarks.SchoolMoveLine(School));
                ApplyTensionDelta(-NegotiationTuning.QuietGroveEase);
                if (!IsResolved)
                    AdvanceStance();
                return true;

            case CardSchool.Tinker:
                SchoolMoveUsed = true;
                TokenPool[LeverageToken.Offering]++;
                _freeOfferingArmed = true;
                AddLog(NegotiationBarks.SchoolMoveLine(School));
                return true;

            case CardSchool.Adept:
                SchoolMoveUsed = true;
                TokenPool[chosenToken]++;
                AddLog(NegotiationBarks.SchoolMoveLine(School));
                AddLog($"  · +1 {chosenToken}.", NegotiationLogKind.Detail);
                return true;

            case CardSchool.Elementalist:
                SchoolMoveUsed = true;
                AddLog(NegotiationBarks.SchoolMoveLine(School));
                PullTerm(target, NegotiationTuning.ShowOfPowerPull, byPlayer: true);
                if (NpcPool[NpcResource.Resolve] > 0)
                {
                    NpcPool[NpcResource.Resolve]--;
                    AddLog($"  · Their {ResolveName} wavers before the display. (−1)");
                }
                ApplyTensionDelta(NegotiationTuning.ShowOfPowerTension);
                if (!IsResolved)
                    FinishPlayerAction();
                return true;

            default:
                return false;
        }
    }

    // ── Building hooks (Phase 5) ─────────────────────────────────────────

    /// <summary>Courier Station dossier, applied once at table-open by the
    /// manager. Tier 1: their disposition is profiled (next stance known).
    /// Tier 2: a buried clause is flagged (one hidden term flipped).
    /// Tier 3: spy-network briefing (+1 Insight). Stacks with the generic
    /// BonusNegotiationTokens the building already grants.</summary>
    public void ApplyCourierDossier(int tier)
    {
        if (tier <= 0 || IsResolved)
            return;
        AddLog("A courier dossier reached you ahead of this meeting.");
        NextStanceKnown = true;
        AddLog($"  · Their disposition is profiled: next they'll be {_nextStance}.");
        if (tier >= 2)
        {
            var hidden = Terms.FirstOrDefault(t => t.IsHidden && !t.IsAccepted);
            if (hidden != null)
            {
                hidden.IsHidden = false;
                AddLog($"  · The dossier flags a buried clause: \"{hidden.Description}\"");
            }
        }
        if (tier >= 3)
        {
            TokenPool[LeverageToken.Insight]++;
            AddLog("  · Spy-network briefing: +1 Insight.", NegotiationLogKind.Detail);
        }
    }

    /// <summary>§6b: the last table with this counterpart in THIS life walks
    /// back in the door as their re-meeting opener. The starting-tension
    /// shift is applied by the manager pre-init (the Beguile pattern); this
    /// logs the line. Called once, right after Initialize.</summary>
    public void ApplyContinuity(string lastOutcome, int lastStars)
    {
        if (IsResolved || string.IsNullOrEmpty(lastOutcome))
            return;
        var kind = lastOutcome switch
        {
            "Signed" => lastStars >= 4
                ? NegotiationContinuityKind.WarmReturn
                : NegotiationContinuityKind.CoolReturn,
            "WalkedAway" => NegotiationContinuityKind.WalkedBefore,
            "Collapsed" => NegotiationContinuityKind.CollapsedBefore,
            _ => NegotiationContinuityKind.TimedOutBefore,
        };
        // A collapsed history arms the grudge: their FIRST move is fine
        // print, never generosity (spec §6b's continuity table).
        _grudgeArmed = kind == NegotiationContinuityKind.CollapsedBefore;
        AddLog(NegotiationBarks.ContinuityLine(Data.Archetype, kind),
               NegotiationLogKind.Dialogue);
    }

    /// <summary>§6c: cross-timeline familiarity. The counterpart is the same
    /// person reborn without memory (ruled 2026-08-31); the player's
    /// chronicle kept every record. Effects mirror the Courier Dossier
    /// hooks and stack per prior-table count, unbounded (ruled 2026-08-31:
    /// mastery through repetition IS the loop). Called after Initialize,
    /// before the dossier, and the two do not double-count: each effect is
    /// idempotent.</summary>
    public void ApplyChronicleFamiliarity(int priorTables, bool sawCollapse)
    {
        if (priorTables <= 0 || IsResolved)
            return;
        AddLog(priorTables == 1
            ? "You have sat at this table before, in a life they do not remember."
            : $"You have sat at this table in {priorTables} other lives. They remember none of them.");
        NextStanceKnown = true;
        AddLog($"  · You remember how they open: next they'll be {_nextStance}.");
        if (priorTables >= 3)
        {
            var hidden = Terms.FirstOrDefault(t => t.IsHidden && !t.IsAccepted);
            if (hidden != null)
            {
                hidden.RumorText = $"You remember the small print: \"{hidden.Description}\"";
                AddLog("  · And you remember their small print. The face-down clause holds no mystery for you.");
            }
        }
        if (sawCollapse)
            AddLog($"  · You remember exactly how this goes wrong: \"{Data.DialogueWalkaway}\"");
    }

    /// <summary>§6c: a COMPLETED archmage dossier (every weakness hint
    /// revealed) arms one extra argument at that archmage's kingdom's
    /// tables: you know the master this counterpart answers to, and the
    /// argument half writes itself. Quest spec ruling 2: dossiers grant
    /// mechanical unlocks in later timelines.</summary>
    public void ApplyDossierSeam()
    {
        if (IsResolved)
            return;
        TokenPool[LeverageToken.Persuade]++;
        AddLog("You know the master this table answers to, down to the flaws. " +
               "The argument half writes itself.");
        AddLog("  · Completed dossier: +1 Persuade.", NegotiationLogKind.Detail);
    }

    /// <summary>The timing glyph's verdict for a token at the CURRENT
    /// stance, from the same rules the play methods apply. Idealists read
    /// Intimidate as Poor at every stance: no mood softens a violated
    /// principle (the instant-walkaway rule).</summary>
    public TokenTiming TimingFor(LeverageToken token)
    {
        if (token is LeverageToken.Insight or LeverageToken.Patience)
            return TokenTiming.Indifferent;
        if (token == LeverageToken.Intimidate)
        {
            if (Data.Archetype == NpcArchetypeType.Idealist)
                return TokenTiming.Poor;
            return Stance switch
            {
                NpcStance.Wavering => TokenTiming.Favorable,
                NpcStance.Guarded => TokenTiming.Poor,
                _ => TokenTiming.Indifferent,
            };
        }
        if (token == LeverageToken.Offering)
            return Stance == NpcStance.Guarded
                ? TokenTiming.Indifferent : TokenTiming.Favorable;
        // The social presses: Charm / Persuade / Connections / Demonstration.
        return Stance switch
        {
            NpcStance.Irritated => TokenTiming.Poor,
            NpcStance.Wavering or NpcStance.Expansive => TokenTiming.Favorable,
            _ => TokenTiming.Indifferent,
        };
    }

    /// <summary>The priority ladder's verdict at the CURRENT board: what the
    /// NPC will do on their turn, and to which clause. Single source of
    /// truth: <see cref="NpcTurn"/> executes this verdict, and the UI
    /// (intent line, clause-card threat markers) displays it, so the tell
    /// can never lie. Pure read; mutates nothing. Note it reads the board as
    /// it stands NOW. The player's own next move can change the verdict
    /// (advancing a clause past 0 wakes their Resolve).</summary>
    public (NpcMoveKind Kind, DealTerm Target) PredictNpcAction()
    {
        if (IsResolved)
            return (NpcMoveKind.Hold, null);

        // 1. Poise: a collapse serves no one they care about.
        if (Tension >= NegotiationTuning.PoiseTriggerTension
            && NpcPool[NpcResource.Poise] > 0)
            return (NpcMoveKind.Poise, null);

        // 1.5. Grudge (§6b): after a collapsed table this life, their first
        // move is fine print while they hold any. Cleared by NpcTurn once
        // it executes, so the UI's tell and the move stay one verdict.
        if (_grudgeArmed && NpcPool[NpcResource.Guile] > 0)
        {
            var grudgeTarget = Terms
                .Where(t => !t.IsHidden && t.Position > -2)
                .OrderBy(t => t.Weight).ThenBy(t => t.Position)
                .FirstOrDefault();
            return grudgeTarget != null
                ? (NpcMoveKind.Rework, grudgeTarget)
                : (NpcMoveKind.Threat, null);
        }

        // 2. Resolve: drag the clause you've won furthest back toward them.
        var pullTarget = Terms
            .Where(t => !t.IsHidden && t.Position >= 0 && t.Position > -2)
            .OrderByDescending(t => t.Position * t.Weight)
            .FirstOrDefault();
        if (pullTarget != null && NpcPool[NpcResource.Resolve] > 0)
            return (NpcMoveKind.Pull, pullTarget);

        // 3. Guile: rework the FINE PRINT, the lightest movable clause.
        //    (Was min position×weight, which sniped the player's biggest-
        //    ticket clause first: punishing, and invisible to boot. The bark
        //    has always said "small print"; now it behaves like it.)
        if (NpcPool[NpcResource.Guile] > 0)
        {
            var guileTarget = Terms
                .Where(t => !t.IsHidden && t.Position > -2)
                .OrderBy(t => t.Weight).ThenBy(t => t.Position)
                .FirstOrDefault();
            return guileTarget != null
                ? (NpcMoveKind.Rework, guileTarget)
                : (NpcMoveKind.Threat, null);
        }

        // 4. Cordial goodwill: once per table, warmth pays forward.
        if (Zone == TensionZone.Cordial && !_giftGiven)
            return (NpcMoveKind.Gift, null);

        // 5. Hold.
        return (NpcMoveKind.Hold, null);
    }

    /// <summary>Embassy tier-2 hook: the precise briefing line, straight off
    /// <see cref="PredictNpcAction"/>.</summary>
    public string PredictNpcMove()
    {
        var (kind, target) = PredictNpcAction();
        return kind switch
        {
            NpcMoveKind.Poise => "they're about to step back from the brink (Poise).",
            NpcMoveKind.Pull => $"they're eyeing the {ShortName(target)}. Expect a pull ({ResolveName}).",
            NpcMoveKind.Rework => $"fine print is coming for the {ShortName(target)} (Guile).",
            NpcMoveKind.Threat => "a threat is coming (Guile).",
            NpcMoveKind.Gift => "they're feeling generous.",
            _ => "they'll hold and watch you.",
        };
    }

    // ── Turn cycle internals ─────────────────────────────────────────────

    /// <summary>Everything that happens after a normal (non-Patience) player
    /// action: turn count, the NPC's move, the patience tick, stance advance.</summary>
    private void FinishPlayerAction()
    {
        if (IsResolved)
            return;

        TurnNumber++;
        NpcTurn();
        if (IsResolved)
            return;

        NpcPatience--;
        if (NpcPatience <= 0)
        {
            AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"", NegotiationLogKind.Dialogue);
            Resolve(false, false);
            return;
        }

        AdvanceStance();

        // §5c: the closing demand, seeded. Once per table, when the clause
        // they would squeeze first reaches "leaning yours", the seed drops.
        var squeezeSeed = PredictSqueezeTarget();
        if (!_squeezeSeedBarked && squeezeSeed != null && squeezeSeed.Position >= 1)
        {
            _squeezeSeedBarked = true;
            AddLog($"Their eyes keep returning to the {ShortName(squeezeSeed)}.");
        }

        AddLog($"[Turn {TurnNumber} | Tension: {Tension}/10 | Patience: {NpcPatience}]",
               NegotiationLogKind.Detail);

        if (_firstTableGuidance && !_shakeGuidanceGiven)
        {
            _shakeGuidanceGiven = true;
            AddLog("Training, again: offer the handshake whenever the signing " +
                   "preview suits you, and expect one last demand before ink.");
        }
    }

    private void AdvanceStance()
    {
        Stance = _nextStance;
        _nextStance = ArchetypeBehavior.RollStance(Zone, GD.Randi());
        NextStanceKnown = _omniscient;   // Arcanist's Omniscient Read never fades
        AddLog(NegotiationBarks.StanceTell(Data.Archetype, Stance));
        if (_firstTableGuidance && !_stanceGuidanceGiven)
        {
            _stanceGuidanceGiven = true;
            AddLog("Their mood turns between exchanges. The same argument lands " +
                   "differently on a different mood.");
        }
        OnStanceChanged?.Invoke();
    }

    /// <summary>v2: the NPC's move. Executes <see cref="PredictNpcAction"/>'s
    /// verdict, so what the UI foretold is exactly what happens.</summary>
    private void NpcTurn()
    {
        var (kind, target) = PredictNpcAction();
        _grudgeArmed = false;   // the grudge shapes one move, then it's spent
        switch (kind)
        {
            case NpcMoveKind.Poise:
                NpcPool[NpcResource.Poise]--;
                AddLog(NegotiationBarks.NpcPoiseBark(Data.Archetype), NegotiationLogKind.Dialogue);
                ApplyTensionDelta(-1);
                return;

            case NpcMoveKind.Pull:
                {
                    NpcPool[NpcResource.Resolve]--;
                    int steps = (Zone == TensionZone.Hostile ? NegotiationTuning.HostilePullSteps : 1)
                              + (_npcHardened ? NegotiationTuning.HardenedBonusSteps : 0);
                    _npcHardened = false;
                    PullTerm(target, steps, byPlayer: false);
                    AddLog(NegotiationBarks.NpcPullBark(Data.Archetype, ShortName(target),
                                                        hard: steps > 1),
                           NegotiationLogKind.Dialogue);
                    AnnouncePoolEmpty();
                    return;
                }

            case NpcMoveKind.Rework:
                NpcPool[NpcResource.Guile]--;
                PullTerm(target, 1, byPlayer: false);
                AddLog(TipTheHand() ?? NegotiationBarks.NpcGuileBark(Data.Archetype, ShortName(target)),
                       NegotiationLogKind.Dialogue);
                AnnouncePoolEmpty();
                return;

            case NpcMoveKind.Threat:
                NpcPool[NpcResource.Guile]--;
                AddLog(TipTheHand() ?? NegotiationBarks.NpcThreatBark(Data.Archetype),
                       NegotiationLogKind.Dialogue);
                ApplyTensionDelta(+1);
                AnnouncePoolEmpty();
                return;

            case NpcMoveKind.Gift:
                {
                    _giftGiven = true;
                    var gift = ArchetypeBehavior.GiftTokenFor(Data.Archetype);
                    TokenPool[gift]++;
                    AddLog(NegotiationBarks.NpcGiftBark(Data.Archetype), NegotiationLogKind.Dialogue);
                    AddLog($"Their goodwill joins your pool: +1 {gift}.");
                    return;
                }

            default:
                AddLog(TipTheHand() ?? NegotiationBarks.NpcHoldBark(Data.Archetype),
                       NegotiationLogKind.Dialogue);
                return;
        }
    }

    /// <summary>Spec §4c: once per table, on the NPC's first Hold or Guile
    /// move while a face-down clause remains, they tip their hand about the
    /// small print instead of the generic bark. Returns null when the hint
    /// doesn't apply (no hidden term left, or already fired), so callers can
    /// fall through to the normal line.</summary>
    private string TipTheHand()
    {
        if (_smallPrintHinted || !Terms.Any(t => t.IsHidden && !t.IsAccepted))
            return null;
        _smallPrintHinted = true;
        return NegotiationBarks.SmallPrintHint(Data.Archetype);
    }

    /// <summary>The "push now" tells: say it out loud when a pool runs dry.
    /// The moment the player's pulls start sticking is the moment the
    /// minigame becomes winnable, and it should never pass silently.</summary>
    private void AnnouncePoolEmpty()
    {
        if (NpcPool[NpcResource.Resolve] == 0 && !_resolveEmptyAnnounced)
        {
            _resolveEmptyAnnounced = true;
            AddLog($"Their {ResolveName} is spent. What you pull now, stays pulled.");
        }
        if (NpcPool[NpcResource.Guile] == 0 && !_guileEmptyAnnounced)
        {
            _guileEmptyAnnounced = true;
            AddLog($"Their {GuileName} is exhausted. The clauses on the table are the whole story.");
        }
    }

    private void PullTerm(DealTerm term, int steps, bool byPlayer)
    {
        int old = term.Position;
        term.Position = Mathf.Clamp(term.Position + (byPlayer ? steps : -steps), -2, 2);
        if (term.Position != old)
        {
            LastExchange.Add(new NegotiationTermMove
            {
                TermId = term.Id,
                From = old,
                To = term.Position,
                ByPlayer = byPlayer,
            });
            AddLog($"  · {ShortName(term)}: {PositionLabel(old)} → {PositionLabel(term.Position)}",
                   NegotiationLogKind.Detail);
        }
    }

    /// <summary>Display handle for a term in barks and on cards. Prefers the
    /// authored <see cref="DealTerm.ShortName"/> (the grammar-contract noun
    /// phrase, negotiation_narrative_spec_v1 §3a); the old first-few-words
    /// derivation survives only as a fallback for unauthored data, and the
    /// validator flags any term that still relies on it.</summary>
    public static string ShortName(DealTerm t)
    {
        if (t == null)
            return "";
        if (!string.IsNullOrEmpty(t.ShortName))
            return t.ShortName;
        if (string.IsNullOrEmpty(t.Description))
            return t.Id;
        var words = t.Description.Split(' ');
        int n = Mathf.Min(4, words.Length);
        string s = string.Join(" ", words.Take(n)).TrimEnd('.', ',', ';', ':');
        return words.Length > n ? s + "…" : s;
    }

    public static string PositionLabel(int pos) => pos switch
    {
        -2 => "strongly theirs",
        -1 => "leaning theirs",
        0 => "balanced",
        1 => "leaning yours",
        _ => "strongly yours",
    };

    // ── Tension / locks / resolution ─────────────────────────────────────

    private void ApplyTensionDelta(int delta)
    {
        if (delta == 0)
        { UpdateLocks(); return; }
        int oldTension = Tension;
        Tension = Mathf.Clamp(Tension + delta, TensionMin, TensionMax);
        OnTensionChanged?.Invoke(oldTension, Tension);

        if (delta < 0)
            AddLog($"Tension eases. ({oldTension} → {Tension})", NegotiationLogKind.Detail);
        else
            AddLog($"Tension rises. ({oldTension} → {Tension})", NegotiationLogKind.Detail);

        UpdateLocks();

        // Check for collapse at max tension
        if (Tension >= TensionMax)
        {
            AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"", NegotiationLogKind.Dialogue);
            Resolve(false, false);
        }
    }

    /// <summary>Hostile seals the clause you've won furthest: the NPC guards
    /// their biggest concession until the room cools.</summary>
    private void UpdateLocks()
    {
        foreach (var t in Terms)
            t.Locked = false;
        if (Zone != TensionZone.Hostile)
            return;
        var guard = Terms
            .Where(t => !t.IsHidden && t.Position > 0)
            .OrderByDescending(t => t.Position * t.Weight)
            .FirstOrDefault();
        if (guard != null)
        {
            guard.Locked = true;
            AddLog($"  · While the table is Hostile, they guard the {ShortName(guard)}. (sealed)");
        }
    }

    /// <summary>The table ended because tension hit maximum, as opposed to the
    /// player walking away, or the NPC's patience running out. The DealRecord
    /// outcome string and the escalate-to-combat branch both key off this single
    /// property so the two can never disagree about what "Collapsed" means.</summary>
    public bool Collapsed =>
        IsResolved && !DealAccepted && !PlayerWalkedAway && Tension >= TensionMax;

    private void Resolve(bool dealAccepted, bool playerWalked)
    {
        IsResolved = true;
        DealAccepted = dealAccepted;
        PlayerWalkedAway = playerWalked;
        OnResolved?.Invoke();
    }

    private void AddLog(string message, NegotiationLogKind kind = NegotiationLogKind.Scene)
    {
        if (string.IsNullOrEmpty(message))
            return;
        Log.Add(message);
        OnLogEntry?.Invoke(message, kind);
    }

    /// <summary>Forget the previous exchange's move markers. Called at the
    /// top of every player action, so <see cref="LastExchange"/> always
    /// describes what changed since the player last acted.</summary>
    private void BeginExchange() => LastExchange.Clear();

    // ── Outcomes ─────────────────────────────────────────────────────────

    /// <summary>v2: gold from the term BOARD, where each clause pays by its final
    /// slider position (favorable terms pay more pulled toward you;
    /// unfavorable ones cost less). Hidden clauses you never flipped bind at
    /// their resting position, the price of not reading the small print.
    /// The zone multiplier survives from v1.</summary>
    public int GetGoldOutcome() => DealAccepted ? ProjectGold() : 0;

    /// <summary>What the deal pays at CURRENT positions and zone, as if it
    /// signed right now. Drives the live "a handshake signs for" preview,
    /// the squeeze modal's arithmetic, and the final receipt: one source
    /// of truth for all three.</summary>
    public int ProjectGold()
    {
        float total = 0f;
        foreach (var term in Terms)
            total += term.GoldDelta * term.PlayerFraction();
        total *= ZoneGoldMult(Zone);
        return Mathf.RoundToInt(total);
    }

    public int GetSuppliesOutcome() => DealAccepted ? ProjectSupplies() : 0;

    /// <summary>Supplies at current positions, as if signed now. NO zone
    /// multiplier, since provisions are physical goods (see DealTerm.SuppliesDelta).</summary>
    public int ProjectSupplies()
    {
        float total = 0f;
        foreach (var term in Terms)
            total += term.SuppliesDelta * term.PlayerFraction();
        return Mathf.RoundToInt(total);
    }

    /// <summary>True when a signed deal carries an un-conceded supply-lines
    /// intel clause (DealTerm.RevealsSupplyCaches). Fully conceded (fraction 0)
    /// = they never handed the charts over. Hidden clauses count only once
    /// revealed/accepted, like every other term.</summary>
    public bool GetSupplyIntelOutcome()
    {
        if (!DealAccepted)
            return false;
        foreach (var term in Terms)
            if ((!term.IsHidden || term.IsAccepted) && term.RevealsSupplyCaches &&
                term.PlayerFraction() > 0f)
                return true;
        return false;
    }

    public int GetStepsOutcome() => DealAccepted ? ProjectSteps() : 0;

    /// <summary>Expedition range moved by the deal (DealTerm.StepsDelta) at
    /// current positions. NO zone multiplier, since a cleared road is a physical
    /// fact, same reasoning as ProjectSupplies. Positive = safe passage /
    /// guides / opened gates; negative = a detour or an escort you owe.
    /// Applied on return by ExpeditionManager.OnNegotiationReturned.</summary>
    public int ProjectSteps()
    {
        float total = 0f;
        foreach (var term in Terms)
            total += term.StepsDelta * term.PlayerFraction();
        return Mathf.RoundToInt(total);
    }

    public int GetReputationOutcome() => DealAccepted ? ProjectReputation() : 0;

    /// <summary>Reputation at current positions and zone, as if signed now.</summary>
    public int ProjectReputation()
    {
        float rep = 0f;
        foreach (var term in Terms)
            rep += term.ReputationDelta * term.PlayerFraction();
        return Mathf.RoundToInt(rep) + ZoneRepBonus(Zone);
    }

    /// <summary>The zone's gold multiplier (receipt shows it as its own line).</summary>
    public static float ZoneGoldMult(TensionZone z) => z switch
    {
        TensionZone.Cordial => NegotiationTuning.CordialGoldMult,
        TensionZone.Hostile => NegotiationTuning.HostileGoldMult,
        _ => 1f
    };

    /// <summary>The zone's flat reputation adjustment (bonus in Cordial,
    /// penalty in Hostile).</summary>
    public static int ZoneRepBonus(TensionZone z) => z switch
    {
        TensionZone.Cordial => NegotiationTuning.CordialRepBonus,
        TensionZone.Hostile => NegotiationTuning.HostileRepPenalty,
        _ => 0
    };

    /// <summary>One clause's signed contribution to the pre-zone totals at
    /// its current position, the receipt's line item.</summary>
    public static (int Gold, int Rep, int Supplies) TermPayout(DealTerm t) => (
        Mathf.RoundToInt(t.GoldDelta * t.PlayerFraction()),
        Mathf.RoundToInt(t.ReputationDelta * t.PlayerFraction()),
        Mathf.RoundToInt(t.SuppliesDelta * t.PlayerFraction()));

    /// <summary>Totals if <paramref name="target"/> slid one notch their way
    /// and the deal signed now, the squeeze modal's "concede" column. Pure:
    /// nudges the position, projects, restores.</summary>
    public (int Gold, int Rep, int Supplies, int Stars) ProjectIfConceded(DealTerm target)
    {
        int keep = target.Position;
        target.Position = Mathf.Clamp(keep - 1, -2, 2);
        var projected = (ProjectGold(), ProjectReputation(), ProjectSupplies(), ProjectStars());
        target.Position = keep;
        return projected;
    }

    /// <summary>S4 (overworld_spell_system §11): the spell this deal teaches,
    /// or "". A tuition term (SpellId set) grants only when the deal was
    /// accepted IN THE CORDIAL ZONE. The term's description says exactly
    /// that, so the gate is legible pre-accept (G5). Hidden spell terms
    /// count only once revealed/accepted, like every other term.</summary>
    public string GetSpellOutcome()
    {
        if (!DealAccepted || Zone != TensionZone.Cordial)
            return "";
        foreach (var term in Terms)
            if ((!term.IsHidden || term.IsAccepted) && !string.IsNullOrEmpty(term.SpellId))
                return term.SpellId;
        return "";
    }

    /// <summary>S4: does any revealed term carry tuition? (For the result
    /// panel's "the offer died with the tone" line.)</summary>
    public bool HasSpellTermOnTable()
    {
        foreach (var term in Terms)
            if ((!term.IsHidden || term.IsAccepted) && !string.IsNullOrEmpty(term.SpellId))
                return true;
        return false;
    }

    /// <summary>Deal Quality (§7b): position-weighted score + zone bonus.</summary>
    public int GetDealScore() => DealAccepted ? ProjectDealScore() : 0;

    /// <summary>Deal Quality score at current positions and zone, as if
    /// signed now.</summary>
    public int ProjectDealScore()
    {
        int score = Terms.Sum(t => t.Position * t.Weight);
        score += Zone switch
        {
            TensionZone.Cordial => NegotiationTuning.ScoreCordialBonus,
            TensionZone.Hostile => NegotiationTuning.ScoreHostilePenalty,
            _ => 0
        };
        return score;
    }

    /// <summary>Deal Quality stars, 1–5, for the result panel and the Hall
    /// of Records ledger.</summary>
    public int GetStars() => StarsFor(GetDealScore());

    /// <summary>Stars the deal would earn if signed now.</summary>
    public int ProjectStars() => StarsFor(ProjectDealScore());

    private static int StarsFor(int s) =>
        s >= NegotiationTuning.StarT5 ? 5
      : s >= NegotiationTuning.StarT4 ? 4
      : s >= NegotiationTuning.StarT3 ? 3
      : s >= NegotiationTuning.StarT2 ? 2 : 1;
}
