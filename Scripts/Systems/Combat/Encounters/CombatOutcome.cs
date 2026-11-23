// ============================================================
// CombatOutcome.cs
//
// Purpose:        How a fight ended, as a record richer than the
//                 won/lost bool. Transient: it rides the router out
//                 of combat the way EncounterContextCarrier rides in,
//                 and it is never serialized.
// Layer:          Data
// Collaborators:  CombatManager.Outcome.cs (writer),
//                 EncounterRouter.cs (carrier: LastOutcome),
//                 ExpeditionManager.cs (reader: the withdrawal branch)
// See:            docs/combat_c1_break_withdraw_outcome_v1.md
// ============================================================

/// <summary>Three results. A withdrawal is not a win and not a defeat.</summary>
public enum CombatResult
{
    Victory,
    Withdrawn,
    Defeat,
}

/// <summary>What ended the fight.</summary>
public enum CombatEndKind
{
    /// <summary>Every enemy fell.</summary>
    Annihilated,
    /// <summary>The objective was met with enemies still standing or not.</summary>
    Objective,
    /// <summary>The party left by the Withdraw action.</summary>
    Withdrew,
    /// <summary>The whole party fell.</summary>
    PartyDown,
    /// <summary>The objective failed (the ward fell, the zone was overrun).</summary>
    ObjectiveLost,
}

/// <summary>
/// One fight's result. Built by CombatManager at the moment the fight is declared
/// and left on EncounterRouter.LastOutcome for the scene that comes back.
/// EncounterRouter.CombatWon still exists and still means Result == Victory.
/// </summary>
public class CombatOutcome
{
    public CombatResult Result = CombatResult.Defeat;
    public CombatEndKind EndKind = CombatEndKind.PartyDown;

    /// <summary>The objective kind the fight ended under ("annihilate" when none).</summary>
    public string ObjectiveKind = CombatObjectiveDef.KindAnnihilate;

    public int Rounds;

    /// <summary>Enemies that took the field: the opening roster plus every arrival.
    /// Mid-fight summons, structures and field objects are not counted.</summary>
    public int EnemiesFielded;

    public int EnemiesKilled;

    /// <summary>Enemies still standing when the fight ended. On a victory these
    /// are the ones that fled.</summary>
    public int EnemiesLeft;

    /// <summary>An elite, a boss or the slay objective's leader fell.</summary>
    public bool EliteKilled;

    /// <summary>Party members downed during the fight.</summary>
    public int PartyDowned;

    public bool Won => Result == CombatResult.Victory;
}
