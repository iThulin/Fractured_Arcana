using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// CombatManager.SignalFire.cs  (partial of CombatManager)
//
// Purpose:        Combat overhaul stage C9: the first object the ENEMY
//                 works. A Signal Fire stands on the map. One enemy is
//                 told off to run to it. If it gets there it lights the
//                 fire, and more enemies arrive the round after next.
//                 The party stops it by killing the runner, breaking the
//                 fire, or putting it out with Interact.
//
//                 Same shape as a post (CombatManager.Posts.cs): a
//                 runtime-only behaviour key given to one unit for one
//                 fight, a planner that returns a plain Guard beat, and
//                 a hook at the head of ExecuteGuardIntent that does the
//                 walking. Nothing is remembered between planning passes
//                 except which unit is the runner and whether the fire
//                 is lit.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.EnemyIntents.cs (the planner table,
//                   ExecuteGuardIntent, MoveTowardTile),
//                 CombatManager.Posts.cs (AssignEnemyPosts calls
//                   AssignSignalRunner),
//                 CombatManager.WorkObjects.cs (putting it out),
//                 CombatManager.Objectives.cs (the pending waves),
//                 HexGridManager.Obstruction.cs (places one behind a barrier),
//                 UIContent.cs (the behaviour line)
// See:            docs/combat_c9_enemy_interactions_v1.md
// ============================================================

public partial class CombatManager
{
    private const string SignalKind = "signal_fire";
    private const string LightSignalKey = "light_signal";

    /// <summary>Enemies a lit fire calls. Starting value.</summary>
    private const int SignalWaveSize = 2;

    /// <summary>Rounds between the fire being lit and the arrival. 2 means the
    /// party gets one full round of warning.</summary>
    private const int SignalWaveDelay = 2;

    private Unit _signalRunner;
    private string _signalRunnerOldKey = "";
    private bool _signalLit;

    /// <summary>The standing, unlit Signal Fire, or null.</summary>
    private Unit StandingSignalFire()
    {
        if (_signalLit)
            return null;
        foreach (var o in fieldObjects)
        {
            if (o == null || !IsInstanceValid(o) || o.IsDeathQueued || o.CurrentTile == null)
                continue;
            if (o.MapObjectKind == SignalKind && o.Stats.IsAlive)
                return o;
        }
        return null;
    }

    /// <summary>From AssignEnemyPosts. Picks the runner: the plain melee enemy
    /// nearest the fire that holds no post and is not the slay leader.</summary>
    private void AssignSignalRunner()
    {
        var fire = StandingSignalFire();
        if (fire == null)
            return;
        Unit runner = null;
        int best = int.MaxValue;
        foreach (var u in enemyUnits)
        {
            if (!CanHoldPost(u) || u == _slayTarget || _posts.ContainsKey(u))
                continue;
            int d = grid.Distance(u.CurrentTile.Axial, fire.CurrentTile.Axial);
            if (d < best)
            {
                best = d;
                runner = u;
            }
        }
        if (runner == null)
        {
            GD.Print("[Signal] a Signal Fire stands but no enemy can run to it.");
            return;
        }
        _signalRunner = runner;
        _signalRunnerOldKey = runner.BehaviorKey;
        runner.BehaviorKey = LightSignalKey;
        string who = string.IsNullOrEmpty(runner.DisplayName) ? runner.Name.ToString() : runner.DisplayName;
        GD.Print($"[Signal] {runner.Name} runs for the Signal Fire at {fire.CurrentTile.Axial}.");
        combatUI?.AppendActionLog(
            $"── {who} is running to the Signal Fire. If it gets there, {SignalWaveSize} more enemies arrive " +
            $"{SignalWaveDelay} rounds later. Kill it, break the fire, or stand beside the fire and use Interact to put it out. ──");
    }

    /// <summary>The runner goes back to its own routine: the fire is lit, out or broken.</summary>
    private void ReleaseSignalRunner()
    {
        if (_signalRunner != null && IsInstanceValid(_signalRunner)
            && _signalRunner.BehaviorKey == LightSignalKey)
            _signalRunner.BehaviorKey = _signalRunnerOldKey;
        _signalRunner = null;
    }

    /// <summary>The light_signal planner. While the fire stands unlit the runner
    /// shows a Guard beat and does its walking in TryExecuteLightSignal. Once the
    /// fire is gone or lit it is an ordinary soldier again.</summary>
    private EnemyIntent PlanLightSignal(Unit enemy)
    {
        if (StandingSignalFire() == null)
        {
            if (enemy == _signalRunner)
                ReleaseSignalRunner();
            else if (enemy != null && enemy.BehaviorKey == LightSignalKey)
                enemy.BehaviorKey = "melee_advance";
            return PlanSoldier(enemy);
        }
        return new EnemyIntent
        {
            Kind = IntentKind.Guard,
            TargetUnit = enemy,
            Value = 0,
            BaseValue = 0
        };
    }

    /// <summary>From the head of ExecuteGuardIntent. True when the unit is the
    /// runner and its beat was handled here: walk to the fire, and light it once beside it.</summary>
    private async Task<bool> TryExecuteLightSignal(Unit enemy)
    {
        if (enemy == null || enemy.BehaviorKey != LightSignalKey)
            return false;
        var fire = StandingSignalFire();
        if (fire == null)
        {
            ReleaseSignalRunner();
            return true;
        }
        Vector2I at = fire.CurrentTile.Axial;
        if (grid.Distance(enemy.CurrentTile.Axial, at) > 1 && MayMove(enemy, out _))
            await MoveTowardTile(enemy, at, quiet: true);   // stops beside the goal
        if (!IsValidActor(enemy) || enemy.CurrentTile == null)
            return true;
        if (grid.Distance(enemy.CurrentTile.Axial, at) > 1)
        {
            combatUI?.AppendActionLog($"{enemy.Name} runs for the Signal Fire.");
            return true;
        }
        LightSignalFire(enemy, fire);
        return true;
    }

    private void LightSignalFire(Unit enemy, Unit fire)
    {
        _signalLit = true;
        fire.SetBodyColor(new Color(1.0f, 0.55f, 0.10f));
        fire.DisplayName = "Signal Fire (lit)";
        fire.RefreshNameLabel();
        ReleaseSignalRunner();

        var roster = EncounterContextCarrier.Current?.Enemies;
        if (roster == null || roster.Count == 0)
        {
            combatUI?.AppendActionLog($"{enemy.Name} lights the Signal Fire, but nobody answers it.");
            return;
        }
        var wave = new ReinforcementWave
        {
            Round = roundNumber + SignalWaveDelay,
            Announce = "The Signal Fire is answered. Reinforcements arrive.",
        };
        for (int i = 0; i < SignalWaveSize; i++)
        {
            var slot = roster[i % roster.Count];
            wave.Enemies.Add(new EnemySlot(slot.UnitId, slot.DifficultyMult));
        }
        _pendingWaves ??= new List<ReinforcementWave>();
        _pendingWaves.Add(wave);
        _pendingWaves.Sort((a, b) => a.Round.CompareTo(b.Round));
        GD.Print($"[Signal] lit by {enemy.Name}: {SignalWaveSize} arrive on round {wave.Round}.");
        combatUI?.AppendActionLog(
            $"── {enemy.Name} lights the Signal Fire. {SignalWaveSize} more enemies arrive on round {wave.Round}. " +
            "The fight is not won until they have arrived and fallen. ──");
        RefreshObjectiveBanner();
    }
}
