using Godot;

// ============================================================
// CombatManager.Obstructions.cs  (partial of CombatManager)
//
// Purpose:        Combat overhaul stage C8: decides whether this fight's
//                 map gets an obstruction (HexGridManager.Obstruction.cs)
//                 and tells the player about it. The grid builds it.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.cs (ConfigureAndGenerateMap calls
//                   ConfigureGridObstruction before GenerateMap),
//                 CombatManager.Posts.cs (AssignEnemyPosts calls
//                   AnnounceObstruction once the log is in use),
//                 CombatDebugLauncher (PlayerSession.DebugObstruction)
// See:            docs/combat_c8_obstructions_v1.md
// ============================================================

public partial class CombatManager
{
    /// <summary>Chance of a barrier in a fight that neither forces nor forbids one.
    /// Starting values.</summary>
    private const float BarrierChanceSkirmish = 0.15f;
    private const float BarrierChanceOther = 0.35f;

    /// <summary>Sets the grid's obstruction knobs for this fight. A debug fight
    /// does what the launcher says. A reach fight has its own barricade and gets
    /// none. A fight with a zone or a ward to defend gets none, because a wall
    /// through the middle would cut the objective in two.</summary>
    private void ConfigureGridObstruction(CombatObjectiveDef obj)
    {
        if (grid == null)
            return;
        grid.ObstructionKind = "none";
        grid.ObstructionChance = 0f;

        if (PlayerSession.DebugCombat && PlayerSession.DebugObstruction != null)
        {
            grid.ObstructionKind = PlayerSession.DebugObstruction;
            return;
        }
        if (ScriptedFight || EncounterRouter.Instance?.ExamFitting != null)
            return;
        if (obj != null
            && obj.Kind != CombatObjectiveDef.KindAnnihilate
            && obj.Kind != CombatObjectiveDef.KindSlay
            && obj.Kind != CombatObjectiveDef.KindSurvive)
            return;

        grid.ObstructionKind = "";   // roll
        grid.ObstructionChance = EncounterContextCarrier.HasEncounter
            && EncounterContextCarrier.Tier == EncounterTier.Skirmish
                ? BarrierChanceSkirmish
                : BarrierChanceOther;
    }

    /// <summary>One line at the start of the fight when the map has a barrier.</summary>
    private void AnnounceObstruction()
    {
        if (grid == null || grid.ActiveObstruction != "barrier")
            return;
        combatUI?.AppendActionLog(
            "A palisade crosses the board with one gate, which is open. It blocks sight and movement. " +
            "The Gate Winch is on your side. Stand beside it and use Interact to close or open the gate. " +
            "Enemies shut out have to hammer the gate (16 health) or a section of the palisade (10 health).");
    }
}
