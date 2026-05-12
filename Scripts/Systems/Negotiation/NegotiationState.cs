using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// State machine for a single negotiation encounter.
/// Driven by NegotiationScene; decoupled from UI.
/// </summary>
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
        <= 3 => TensionZone.Cordial,
        <= 7 => TensionZone.Strained,
        _    => TensionZone.Hostile
    };

    // ── Token pool ───────────────────────────────────────────────────────
    public Dictionary<LeverageToken, int> TokenPool { get; private set; } = new();
    public int PatienceUsed { get; private set; } = 0;
    public const int MaxPatience = 2;

    // ── Patience / turns ─────────────────────────────────────────────────
    public int NpcPatience { get; private set; }
    public int TurnNumber { get; private set; } = 0;

    // ── Deal terms ───────────────────────────────────────────────────────
    public List<DealTerm> Terms => Data.Terms;
    public List<DealTerm> RevealedTerms =>
        Terms.Where(t => !t.IsHidden || t.IsAccepted).ToList();

    // ── Resolution ───────────────────────────────────────────────────────
    public bool IsResolved { get; private set; } = false;
    public bool DealAccepted { get; private set; } = false;
    public bool PlayerWalkedAway { get; private set; } = false;

    // ── Log ──────────────────────────────────────────────────────────────
    public List<string> Log { get; private set; } = new();

    // ── Events ───────────────────────────────────────────────────────────
    public event Action<int, int> OnTensionChanged;   // oldTension, newTension
    public event Action<string> OnLogEntry;
    public event Action OnResolved;

    // ── Init ─────────────────────────────────────────────────────────────

    public void Initialize(NegotiationEncounterData data, CardSchool wizardSchool,
                           List<Companion> party, int factionReputation = 0)
    {
        Data = data;
        NpcPatience = data.BasePatience;

        // Set starting tension from faction reputation
        Tension = data.StartingTension + factionReputation switch
        {
            >= 2  => -2,   // Allied
            >= 1  => -1,   // Friendly
            <= -2 => 4,    // Hostile  ← must come before <= -1
            <= -1 => 2,    // Unfriendly
            _     => 0     // Neutral
        };
        Tension = Mathf.Clamp(Tension, TensionMin, TensionMax);

        // Build token pool from wizard school + companions
        BuildTokenPool(wizardSchool, party);

        AddLog($"Negotiation begins. {data.NpcName} presents their terms.");
        AddLog(data.OpeningText);
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
            case CardSchool.Enchanter:
                TokenPool[LeverageToken.Charm]++;
                TokenPool[LeverageToken.Connections]++;
                break;
            case CardSchool.Arcanist:
                TokenPool[LeverageToken.Persuade]++;
                TokenPool[LeverageToken.Insight]++;
                break;
            case CardSchool.Necromancer:
                TokenPool[LeverageToken.Intimidate]++;
                break;
            case CardSchool.Elementalist:
                TokenPool[LeverageToken.Intimidate]++;
                TokenPool[LeverageToken.Demonstration]++;
                break;
            case CardSchool.Tinker:
                TokenPool[LeverageToken.Offering]++;
                break;
            default:
                TokenPool[LeverageToken.Persuade]++;
                break;
        }

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
                if (buildingSave.Tier <= 0) continue;
                var tierData = BuildingDatabase.GetCurrentTierData(buildingSave.Id, save);
                if (tierData == null) continue;
                if (tierData.BonusNegotiationTokens <= 0) continue;

                if (System.Enum.TryParse<LeverageToken>(
                    tierData.BonusTokenType, out var tokenType))
                {
                    TokenPool[tokenType] += tierData.BonusNegotiationTokens;
                    GD.Print($"[Buildings] +{tierData.BonusNegotiationTokens} " +
                             $"{tokenType} from {buildingSave.Name}");
                }
            }
        }

        // Everyone gets base Patience tokens
        TokenPool[LeverageToken.Patience] = Mathf.Max(
            TokenPool[LeverageToken.Patience], 2);

        AddLog($"Your leverage pool: " +
               string.Join(", ", TokenPool
                   .Where(kvp => kvp.Value > 0)
                   .Select(kvp => $"{kvp.Value}x {kvp.Key}")));
    }

    // ── Player actions ───────────────────────────────────────────────────

    /// <summary>
    /// Play a leverage token. Returns false if token not available.
    /// </summary>
    public bool PlayToken(LeverageToken token)
    {
        if (IsResolved) return false;

        // Patience special handling
        if (token == LeverageToken.Patience)
        {
            if (PatienceUsed >= MaxPatience)
            {
                AddLog("You've already used your Patience twice this negotiation.");
                return false;
            }
            PatienceUsed++;
            AddLog("You hold your ground without pressing. The moment stretches.");
        }
        else
        {
            if (TokenPool[token] <= 0)
            {
                AddLog($"You have no {token} tokens remaining.");
                return false;
            }
            TokenPool[token]--;
        }

        // Calculate and apply tension delta
        string effectText = ArchetypeBehavior.GetTokenEffect(Data.Archetype, token);
        AddLog(effectText);

        int delta = ArchetypeBehavior.GetTensionDelta(Data.Archetype, token);

        // Instant walk-away on Intimidate against Idealist
        if (Data.Archetype == NpcArchetypeType.Idealist
            && token == LeverageToken.Intimidate)
        {
            AddLog(Data.DialogueWalkaway);
            Resolve(false, false);
            return true;
        }

        // Insight — reveal a hidden term
        if (token == LeverageToken.Insight)
        {
            var hidden = Terms.FirstOrDefault(t => t.IsHidden && !t.IsAccepted);
            if (hidden != null)
            {
                hidden.IsHidden = false;
                AddLog($"Revealed hidden term: \"{hidden.Description}\"");
            }
            else
            {
                AddLog("No hidden terms remain. Your Insight finds nothing new.");
            }
        }

        ApplyTensionDelta(delta);

        // Advance turn and NPC patience
        TurnNumber++;
        NpcPatience--;

        // Check for NPC walk-away
        if (NpcPatience <= 0)
        {
            AddLog(Data.DialogueWalkaway);
            Resolve(false, false);
            return true;
        }

        // NPC response
        string npcResponse = ArchetypeBehavior.GetNpcResponse(Data.Archetype, Zone, Data);
        if (!string.IsNullOrEmpty(npcResponse))
            AddLog($"{Data.NpcName}: \"{npcResponse}\"");

        AddLog($"[Turn {TurnNumber} | Tension: {Tension}/10 | Patience: {NpcPatience}]");

        return true;
    }

    /// <summary>
    /// Player accepts the current deal terms.
    /// </summary>
    public void AcceptDeal()
    {
        if (IsResolved) return;
        AddLog($"You accept the terms. {Data.NpcName}: \"{Data.DialogueAccept}\"");
        Resolve(true, false);
    }

    /// <summary>
    /// Player walks away from the negotiation.
    /// </summary>
    public void WalkAway()
    {
        if (IsResolved) return;
        AddLog("You step away from the table. The negotiation ends without a deal.");
        Resolve(false, true);
    }

    // ── Internal ─────────────────────────────────────────────────────────

    private void ApplyTensionDelta(int delta)
    {
        if (delta == 0) return;
        int oldTension = Tension;
        Tension = Mathf.Clamp(Tension + delta, TensionMin, TensionMax);
        OnTensionChanged?.Invoke(oldTension, Tension);

        if (delta < 0)
            AddLog($"Tension eases. ({oldTension} → {Tension})");
        else
            AddLog($"Tension rises. ({oldTension} → {Tension})");

        // Check for collapse at max tension
        if (Tension >= TensionMax)
        {
            AddLog($"{Data.NpcName}: \"{Data.DialogueWalkaway}\"");
            Resolve(false, false);
        }
    }

    private void Resolve(bool dealAccepted, bool playerWalked)
    {
        IsResolved = true;
        DealAccepted = dealAccepted;
        PlayerWalkedAway = playerWalked;
        OnResolved?.Invoke();
    }

    private void AddLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Log.Add(message);
        OnLogEntry?.Invoke(message);
    }

    /// <summary>
    /// Calculate total gold outcome from accepted terms.
    /// </summary>
    public int GetGoldOutcome()
    {
        if (!DealAccepted) return 0;
        int total = 0;
        foreach (var term in Terms.Where(t => !t.IsHidden || t.IsAccepted))
            total += term.GoldDelta;

        // Tension zone modifier
        total = Zone switch
        {
            TensionZone.Cordial  => (int)(total * 1.2f),
            TensionZone.Hostile  => (int)(total * 0.8f),
            _                    => total
        };
        return total;
    }

    public int GetReputationOutcome()
    {
        if (!DealAccepted) return 0;
        int rep = Terms.Sum(t => t.ReputationDelta);
        // Bonus rep in Cordial, penalty in Hostile
        rep += Zone switch { TensionZone.Cordial => 1, TensionZone.Hostile => -1, _ => 0 };
        return rep;
    }
}