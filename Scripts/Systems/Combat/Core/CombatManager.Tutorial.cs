using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// CombatManager.Tutorial.cs
//
// Purpose:        A scripted fight's checklist (ruled 2026-10-26 for the
//                 prologue trial): steps for the player to do, each with a
//                 counter in the objective banner. The fight is won once
//                 every step is done, at the first safe moment of the
//                 player's turn; the objective's rounds stay on as the
//                 cap, so the fight cannot stall. The steps are data (a
//                 fight's "steps" in prologue_fights.json, read through
//                 PrologueDirector.StepsFor) and the engine counts four:
//                   walk    the player moves a unit to another tile
//                   damage  damage lands on enemies: shield, armor and
//                           health (Unit.DamageLandedThisCombat)
//                   dodge   an enemy strike lands on the tile it locked
//                           onto a player unit, after that unit left it
//                   reflex  a Reflex half cast in a priority window
//                           during the enemy turn
// Layer:          Combat / runtime
// Collaborators:  PrologueDirector (StepsFor), EncounterRouter
//                 (ScriptedFightId, SuppressRewards), CombatManager.cs
//                 (_Process, TryMoveSelectedUnit, the cast tail),
//                 CombatManager.Objectives.cs (InitObjectiveState arms it,
//                 ObjectiveBannerText shows it, CheckCombatEnd declares
//                 it), CombatManager.EnemyIntents.cs (ResolveStrike's
//                 whiff branch), Unit.cs (DamageLandedThisCombat)
// ============================================================

public partial class CombatManager
{
    public const string TutorialStepWalk = "walk";
    public const string TutorialStepDamage = "damage";
    public const string TutorialStepDodge = "dodge";
    public const string TutorialStepReflex = "reflex";

    /// <summary>The step ids the engine counts. A step with any other id is shown but never
    /// completes, so it is left out of the completion test; the prologue's assert rejects it.</summary>
    public static readonly string[] TutorialStepIds =
        { TutorialStepWalk, TutorialStepDamage, TutorialStepDodge, TutorialStepReflex };

    private sealed class TutorialStep
    {
        public string Id = "";
        public string Label = "";
        public int Count = 1;
        public int Progress;
        public bool Known;
        public bool Done => Progress >= Count;
    }

    /// <summary>This fight's steps, or null when it has none (every fight but the trial).</summary>
    private List<TutorialStep> _tutorialSteps;

    /// <summary>Latched once the fight has been won on the checklist.</summary>
    private bool _tutorialComplete;

    /// <summary>Damage already counted, per enemy: its DamageLandedThisCombat at the last look.</summary>
    private readonly Dictionary<Unit, int> _tutorialDamageSeen = new();

    /// <summary>Each enemy's current plan as first seen: the tile it locked and the player unit
    /// standing there when it was planned. Plans are locked at the end of the enemy turn, before
    /// the player can move, so the first look sees the unit still on the tile.</summary>
    private readonly Dictionary<Unit, (EnemyIntent intent, Vector2I tile, Unit aimedAt)> _tutorialAims = new();

    private bool TutorialStepsArmed => _tutorialSteps != null;

    /// <summary>From InitObjectiveState: this fight's steps from the director, by the router's
    /// scripted fight id. Clears any previous fight's first.</summary>
    private void ArmTutorialSteps()
    {
        _tutorialSteps = null;
        _tutorialComplete = false;
        _tutorialDamageSeen.Clear();
        _tutorialAims.Clear();
        if (!ScriptedFight)
            return;
        var defs = PrologueDirector.StepsFor(EncounterRouter.Instance?.ScriptedFightId);
        if (defs == null || defs.Count == 0)
            return;

        var steps = new List<TutorialStep>();
        foreach (var d in defs)
        {
            if (d == null || string.IsNullOrEmpty(d.Id))
                continue;
            bool known = Array.IndexOf(TutorialStepIds, d.Id) >= 0;
            if (!known)
                GD.PushWarning($"[Tutorial] Step '{d.Id}' is not one the engine counts. It is shown but never completes.");
            steps.Add(new TutorialStep
            {
                Id = d.Id,
                Label = string.IsNullOrEmpty(d.Label) ? d.Id : d.Label,
                Count = Math.Max(1, d.Count),
                Known = known,
            });
        }
        if (steps.Count == 0)
            return;
        _tutorialSteps = steps;
        GD.Print($"[Tutorial] Armed {steps.Count} step(s): "
                 + string.Join(", ", steps.ConvertAll(s => $"{s.Id} x{s.Count}")) + ".");
    }

    /// <summary>Per frame, from the top of _Process: count the damage that landed, note each
    /// enemy's new plan, and win the fight once every step is done and the turn is settled.</summary>
    private void RefreshTutorialSteps()
    {
        if (_tutorialSteps == null || _tutorialComplete)
            return;

        int landed = 0;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e))
                continue;
            int now = e.DamageLandedThisCombat;
            int was = _tutorialDamageSeen.TryGetValue(e, out var w) ? w : 0;
            if (now > was)
            {
                landed += now - was;
                _tutorialDamageSeen[e] = now;
            }
        }
        if (landed > 0)
            AdvanceTutorialStep(TutorialStepDamage, landed);

        SnapshotTutorialAims();

        if (!AllTutorialStepsDone())
            return;
        // Settled: the player's own turn, no window open, nothing waiting to resolve. A step
        // finished in the enemy turn wins at the start of the next player turn.
        if (currentPhase != CombatPhase.PlayerTurn || isInDeploymentPhase || _priorityWindowOpen
            || (State != null && TriggersOutstanding))
            return;

        _tutorialComplete = true;
        _objectiveVictory = true;
        GD.Print("[Tutorial] Every step is done. The fight is won.");
        combatUI?.AppendActionLog("── Every step is done. The bell stills. ──");
        CheckCombatEnd();
    }

    /// <summary>From TryMoveSelectedUnit, after a move succeeded.</summary>
    private void NoteTutorialMove(Unit mover)
    {
        if (mover != null && mover.TeamId == 0 && mover.IsPlayerControlled)
            AdvanceTutorialStep(TutorialStepWalk, 1);
    }

    /// <summary>From the cast tail, after a cast succeeded: a Reflex half cast in a priority
    /// window during the enemy turn has answered it.</summary>
    private void NoteTutorialCast(CardHalf half)
    {
        if (half != null && half.Speed == PlaySpeed.Reflex && _priorityWindowOpen
            && currentPhase == CombatPhase.EnemyTurn)
            AdvanceTutorialStep(TutorialStepReflex, 1);
    }

    /// <summary>From ResolveStrike, when a strike lands on a tile with nobody on it: a dodge
    /// when the tile was locked onto a player unit that has since left it. The stack route
    /// names that unit (<paramref name="originalVictim"/>); the direct route does not, so the
    /// plan's first look supplies it.</summary>
    private void NoteTutorialWhiff(Unit attacker, Vector2I tile, Unit originalVictim)
    {
        if (_tutorialSteps == null || attacker == null || !IsInstanceValid(attacker) || attacker.TeamId == 0)
            return;
        bool dodged = StoodAndLeft(originalVictim, tile)
            || (_tutorialAims.TryGetValue(attacker, out var aim) && aim.tile == tile && StoodAndLeft(aim.aimedAt, tile));
        if (dodged)
            AdvanceTutorialStep(TutorialStepDodge, 1);
    }

    private static bool StoodAndLeft(Unit unit, Vector2I tile) =>
        unit != null && IsInstanceValid(unit) && unit.Stats.IsAlive && unit.TeamId == 0
        && unit.CurrentTile != null && unit.CurrentTile.Axial != tile;

    /// <summary>The first look at each enemy's current plan: the tile it locked (a release
    /// lands on the channel's tile) and the living player unit standing there.</summary>
    private void SnapshotTutorialAims()
    {
        if (grid == null)
            return;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || !e.Stats.IsAlive)
                continue;
            var intent = e.CurrentIntent;
            if (intent == null)
                continue;
            if (_tutorialAims.TryGetValue(e, out var seen) && ReferenceEquals(seen.intent, intent))
                continue;
            Vector2I? aim = (intent.Kind == IntentKind.Release ? e.ChannelTile : null) ?? intent.TargetTile;
            Unit at = null;
            if (aim.HasValue)
            {
                var occupant = grid.GetTile(aim.Value)?.Occupant;
                if (occupant != null && IsInstanceValid(occupant) && occupant.Stats.IsAlive && occupant.TeamId == 0)
                    at = occupant;
            }
            _tutorialAims[e] = (intent, aim ?? default, at);
        }
    }

    private void AdvanceTutorialStep(string id, int amount)
    {
        if (_tutorialSteps == null || _tutorialComplete || amount <= 0)
            return;
        bool changed = false;
        foreach (var s in _tutorialSteps)
        {
            if (s.Id != id || s.Done)
                continue;
            s.Progress = Math.Min(s.Count, s.Progress + amount);
            changed = true;
            if (s.Done)
            {
                GD.Print($"[Tutorial] Step done: {s.Id} ({s.Label}).");
                combatUI?.AppendActionLog($"── Done: {s.Label}. ──");
            }
        }
        if (changed)
            RefreshObjectiveBanner();
    }

    private bool AllTutorialStepsDone()
    {
        bool any = false;
        foreach (var s in _tutorialSteps)
        {
            if (!s.Known)
                continue;
            if (!s.Done)
                return false;
            any = true;
        }
        return any;
    }

    /// <summary>The turn panel's objective text while a checklist is armed: the objective's
    /// label with the round against the cap (the survive line's own format), then one indented
    /// line per step with its counter, or "done". Sized for the right column (CombatUI).</summary>
    private string TutorialBannerText()
    {
        string label = string.IsNullOrEmpty(_objective?.Description) ? "Steps" : _objective.Description;
        var sb = new StringBuilder(label);
        if (_objective != null && _objective.Rounds > 0)
            sb.Append($": round {Mathf.Min(roundNumber, _objective.Rounds)} / {_objective.Rounds}");
        foreach (var s in _tutorialSteps)
            sb.Append("\n  ").Append(s.Label).Append("   ").Append(s.Done ? "done" : $"{s.Progress} / {s.Count}");
        return sb.ToString();
    }
}
