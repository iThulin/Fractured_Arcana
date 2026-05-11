using System.Collections.Generic;

/// <summary>
/// Token types available in negotiation.
/// </summary>
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

    // NPC dialogue lines per situation
    public string DialogueCordial = "";
    public string DialogueStrained = "";
    public string DialogueHostile = "";
    public string DialogueWalkaway = "";
    public string DialogueAccept = "";
}

/// <summary>
/// Archetype behavior rules — how NPCs respond to each token type.
/// </summary>
public static class ArchetypeBehavior
{
    /// <summary>
    /// Returns the tension delta when the player plays a token against this archetype.
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

            // Insight — no tension effect
            (_, LeverageToken.Insight)  => 0,

            // Connections
            (_, LeverageToken.Connections) => -1,

            // Patience — no tension effect
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

    /// <summary>
    /// Returns a description of what happens when this token is played.
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
                "The Merchant's eyes light up. A tangible offer — this is something they can work with.",
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