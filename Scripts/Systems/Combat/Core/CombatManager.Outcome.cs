using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.Outcome.cs
//
// Purpose:        Combat overhaul stage C1. Three things that let a
//                 fight end some way other than the last body:
//                 the slay objective (kill the leader and the rest
//                 flee), the Withdraw action (leave with no Anchor
//                 spent), and the outcome record the world reads.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.cs (DeclareVictory / DeclareDefeat,
//                   SpawnAndPlaceEnemies, the UI wiring in _Ready),
//                 CombatManager.Objectives.cs (the objective latch,
//                   NoteObjectiveUnitDeath, the banner),
//                 CombatOutcome.cs (data),
//                 EncounterRouter.cs (LastOutcome),
//                 CombatUI.cs (SetWithdrawState, WithdrawPressed)
// See:            docs/combat_c1_break_withdraw_outcome_v1.md
//
// Nothing here is serialized. Every field starts fresh with the
// scene, because one CombatManager lives for exactly one fight.
// ============================================================

public partial class CombatManager
{
    // ═══════════════════════════════════════════════════════════════════════
    // Slay: kill the leader
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>A slay fight needs this many enemies to pick a leader from. With
    /// fewer, killing one of them is most of the fight anyway.</summary>
    private const int SlayMinEnemies = 3;

    /// <summary>A leader promoted from the line gets this much more health, so
    /// killing it first is a choice with a cost. A composition's own single
    /// elite is the leader as it stands. Starting value.</summary>
    private const float SlayLeaderHealthMult = 1.5f;

    private Unit _slayTarget;
    private string _slayTargetName = "";
    private bool _slayTargetDown;

    private bool SlayActive =>
        _objective != null && _objective.Kind == CombatObjectiveDef.KindSlay;

    /// <summary>Picks and marks the leader. Called from SpawnAndPlaceEnemies once
    /// the opening roster stands, which is the first moment there is anyone to
    /// pick. A fight too small for the objective falls back to annihilate, loudly.</summary>
    private void BindSlayTarget()
    {
        if (!SlayActive || _slayTarget != null)
            return;

        var candidates = new List<Unit>();
        foreach (var u in enemyUnits)
        {
            if (u == null || !IsInstanceValid(u) || u.Stats == null || !u.Stats.IsAlive)
                continue;
            if (u.IsMapObject || u.IsStructure || u.IsMidFightSummon)
                continue;
            candidates.Add(u);
        }

        if (candidates.Count < SlayMinEnemies)
        {
            GD.Print($"[Objective] slay needs {SlayMinEnemies} or more enemies and this fight " +
                     $"has {candidates.Count}. Running as annihilate.");
            _objective = null;
            _lastObjectiveBanner = "";
            RefreshObjectiveBanner();
            return;
        }

        // The composition's one elite or boss, when it has exactly one.
        Unit leader = null;
        int ranked = 0;
        foreach (var c in candidates)
        {
            if (!IsRankedEnemy(c))
                continue;
            ranked++;
            leader = c;
        }

        bool promoted = ranked != 1;
        if (promoted)
        {
            // Otherwise the toughest body on the field, first in spawn order on a tie.
            leader = candidates[0];
            foreach (var c in candidates)
            {
                if (c.Stats.MaxHealth > leader.Stats.MaxHealth)
                    leader = c;
            }
            int raised = Mathf.CeilToInt(leader.Stats.MaxHealth * SlayLeaderHealthMult);
            leader.Stats.MaxHealth = raised;
            leader.Stats.Health = raised;
        }

        _slayTarget = leader;
        _slayTargetName = string.IsNullOrEmpty(leader.DisplayName)
            ? leader.Name.ToString()
            : leader.DisplayName;
        leader.DisplayName = $"{_slayTargetName} (leader)";
        leader.RefreshNameLabel();

        GD.Print($"[Objective] slay: the leader is '{_slayTargetName}' " +
                 $"({leader.Stats.Health} health{(promoted ? ", promoted from the line" : "")}).");
        combatUI?.AppendActionLog(
            $"── Kill the leader: {_slayTargetName}. The others flee when it falls. ──");
        _lastObjectiveBanner = "";
        RefreshObjectiveBanner();
    }

    private static bool IsRankedEnemy(Unit unit)
    {
        if (unit == null || string.IsNullOrEmpty(unit.DefinitionId))
            return false;
        var def = UnitRegistry.Get(unit.DefinitionId);
        return def != null && (def.IsElite || def.IsBoss);
    }

    /// <summary>The slay objective's banner line.</summary>
    private string SlayBannerLabel()
    {
        if (_slayTarget == null || !IsInstanceValid(_slayTarget) || _slayTarget.Stats == null)
            return "Kill the enemy leader. The others flee when it falls.";
        return $"Kill the leader: {_slayTargetName} " +
               $"({Mathf.Max(0, _slayTarget.Stats.Health)} / {_slayTarget.Stats.MaxHealth} health). " +
               "The others flee when it falls.";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Outcome record
    // ═══════════════════════════════════════════════════════════════════════

    private int _outcomeEnemiesKilled;
    private int _outcomePartyDowned;
    private bool _outcomeEliteKilled;

    /// <summary>Called first thing from NoteObjectiveUnitDeath, which HandleUnitDeath
    /// calls for every unit that is not a field object. Keeps the tallies and
    /// latches the slay victory. The declaration still flows through
    /// CheckCombatEnd, so a leader's death triggers settle before the fight ends.</summary>
    private void NoteOutcomeDeath(Unit unit)
    {
        if (unit == null)
            return;

        if (unit.TeamId != 0 && !unit.IsPlayerControlled)
        {
            if (!unit.IsMidFightSummon && !unit.IsStructure)
            {
                _outcomeEnemiesKilled++;
                if (unit == _slayTarget || IsRankedEnemy(unit))
                    _outcomeEliteKilled = true;
            }
        }
        else if (unit.IsPlayerControlled && !unit.IsStructure && !unit.IsObjectiveWard
                 && !string.IsNullOrEmpty(unit.CompanionId))
        {
            _outcomePartyDowned++;
        }

        if (SlayActive && _slayTarget != null && unit == _slayTarget && !_slayTargetDown)
        {
            _slayTargetDown = true;
            _objectiveVictory = true;
            GD.Print("[Objective] The leader has fallen. Objective met.");
            combatUI?.AppendActionLog($"── {_slayTargetName} has fallen. The others flee. ──");
        }
    }

    private int CountEnemiesLeft()
    {
        int left = 0;
        foreach (var u in enemyUnits)
        {
            if (u == null || !IsInstanceValid(u) || u.Stats == null || !u.Stats.IsAlive)
                continue;
            if (u.IsMapObject || u.IsStructure || u.IsMidFightSummon)
                continue;
            left++;
        }
        return left;
    }

    /// <summary>Builds the record and leaves it on the router. Called from the
    /// three declare tails, immediately before CombatCompleted is emitted.</summary>
    private void PublishOutcome(CombatResult result)
    {
        var outcome = new CombatOutcome
        {
            Result = result,
            ObjectiveKind = _objective == null ? CombatObjectiveDef.KindAnnihilate : _objective.Kind,
            Rounds = roundNumber,
            EnemiesKilled = _outcomeEnemiesKilled,
            EnemiesLeft = CountEnemiesLeft(),
            EliteKilled = _outcomeEliteKilled,
            PartyDowned = _outcomePartyDowned,
        };
        outcome.EnemiesFielded = outcome.EnemiesKilled + outcome.EnemiesLeft;

        switch (result)
        {
            case CombatResult.Victory:
                outcome.EndKind = _objectiveVictory
                    ? CombatEndKind.Objective
                    : CombatEndKind.Annihilated;
                break;
            case CombatResult.Withdrawn:
                outcome.EndKind = CombatEndKind.Withdrew;
                break;
            default:
                outcome.EndKind = _objectiveDefeat
                    ? CombatEndKind.ObjectiveLost
                    : CombatEndKind.PartyDown;
                break;
        }

        string tier = EncounterContextCarrier.HasEncounter
            ? EncounterContextCarrier.Tier.ToString().ToLowerInvariant()
            : "none";
        GD.Print($"[Outcome] {tier} {outcome.ObjectiveKind} {outcome.Result} {outcome.EndKind} " +
                 $"rounds={outcome.Rounds} fielded={outcome.EnemiesFielded} " +
                 $"killed={outcome.EnemiesKilled} left={outcome.EnemiesLeft} " +
                 $"partyDowned={outcome.PartyDowned}");

        if (EncounterRouter.Instance != null && !PlayerSession.DebugCombat)
            EncounterRouter.Instance.LastOutcome = outcome;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Withdraw
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Withdraw opens on this round. Round 1 is the fight you chose to start.</summary>
    private const int WithdrawFromRound = 2;

    private const string WithdrawCostLine =
        "No rewards. No Anchor spent. Units outside the deployment zone lose half " +
        "their current health. Press again to confirm.";

    private bool _withdrawArmed;
    private HashSet<Vector2I> _playerDeployTiles;
    private Timer _outcomeTicker;

    /// <summary>Field fights only in C1. A lost fight launched from the strategic
    /// map, the campus or a script has its own ending, and a withdrawal there
    /// has no reader yet.</summary>
    private bool WithdrawOffered
    {
        get
        {
            if (!PlayerSession.IsOnExpedition || PlayerSession.DebugCombat || ScriptedFight)
                return false;
            if (TutorialStepsArmed || SunderingArmed || IsCastleDefense)
                return false;
            var router = EncounterRouter.Instance;
            if (router == null || router.ExamFitting != null)
                return false;
            if (!string.IsNullOrEmpty(router.SavedCombatGuardianKey))
                return false;   // a shard guardian is fought to the end
            if (SaveManager.ActiveSave?.Cycle?.Convergence?.InProgress == true)
                return false;
            return true;
        }
    }

    /// <summary>The wizard, or the first standing companion when the wizard is
    /// not on the board. Null when nobody qualifies.</summary>
    private Unit WithdrawLeader()
    {
        Unit first = null;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || u.Stats == null || !u.Stats.IsAlive)
                continue;
            if (u.IsStructure || u.IsObjectiveWard || string.IsNullOrEmpty(u.CompanionId))
                continue;
            if (u.CompanionId == "wizard")
                return u;
            first ??= u;
        }
        return first;
    }

    private bool InPlayerDeployZone(Unit unit)
    {
        if (unit?.CurrentTile == null || grid == null)
            return false;
        if (_playerDeployTiles == null)
        {
            var tiles = new HashSet<Vector2I>();
            foreach (var zone in grid.SpawnZones)
            {
                if (zone.Side != HexGridManager.SpawnSide.Player)
                    continue;
                foreach (var coord in zone.Tiles)
                    tiles.Add(coord);
            }
            if (tiles.Count == 0)
                return false;   // not generated yet: try again on the next ask
            _playerDeployTiles = tiles;
        }
        return _playerDeployTiles.Contains(unit.CurrentTile.Axial);
    }

    private bool EnemyAdjacentTo(Unit unit)
    {
        if (unit?.CurrentTile == null || grid == null)
            return false;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || e.Stats == null || !e.Stats.IsAlive)
                continue;
            if (e.IsMapObject || e.CurrentTile == null)
                continue;
            if (grid.Distance(e.CurrentTile.Axial, unit.CurrentTile.Axial) <= 1)
                return true;
        }
        return false;
    }

    /// <summary>True when the button may be pressed. Otherwise <paramref name="reason"/>
    /// is the plain line shown beside the disabled button.</summary>
    private bool WithdrawAllowed(out string reason)
    {
        reason = "";
        if (currentPhase != CombatPhase.PlayerTurn || enemyPhaseRunning)
        {
            reason = "You can withdraw on your turn.";
            return false;
        }
        if (roundNumber < WithdrawFromRound)
        {
            reason = $"You can withdraw from round {WithdrawFromRound}.";
            return false;
        }
        if (_priorityWindowOpen || (State != null && TriggersOutstanding))
        {
            reason = "Finish the current action first.";
            return false;
        }
        var leader = WithdrawLeader();
        if (leader == null)
        {
            reason = "Nobody is left to lead the withdrawal.";
            return false;
        }
        string who = string.IsNullOrEmpty(leader.DisplayName) ? leader.Name.ToString() : leader.DisplayName;
        if (!InPlayerDeployZone(leader))
        {
            reason = $"Move {who} back to where you deployed.";
            return false;
        }
        if (EnemyAdjacentTo(leader))
        {
            reason = $"An enemy is next to {who}.";
            return false;
        }
        return true;
    }

    /// <summary>Started once from _Ready (deferred). Five times a second it pushes
    /// the Withdraw button's state and, on a slay fight, the leader's health line.
    /// A timer and not a per-move hook, so no movement or damage path has to
    /// remember to call it.</summary>
    private void StartOutcomeTicker()
    {
        if (_outcomeTicker != null)
            return;
        _outcomeTicker = new Timer
        {
            Name = "OutcomeTicker",
            WaitTime = 0.2,
            OneShot = false,
            Autostart = true,
        };
        _outcomeTicker.Timeout += OnOutcomeTick;
        AddChild(_outcomeTicker);
    }

    private void OnOutcomeTick()
    {
        if (SlayActive)
            RefreshObjectiveBanner();   // no-ops unless the leader's health changed
        RefreshWithdrawControl();
    }

    private void RefreshWithdrawControl()
    {
        if (combatUI == null)
            return;

        bool live = currentPhase == CombatPhase.PlayerTurn || currentPhase == CombatPhase.EnemyTurn;
        if (!live || !WithdrawOffered)
        {
            _withdrawArmed = false;
            combatUI.SetWithdrawState(false, false, "Withdraw", "");
            return;
        }

        bool allowed = WithdrawAllowed(out string reason);
        if (!allowed)
            _withdrawArmed = false;
        combatUI.SetWithdrawState(true, allowed,
            _withdrawArmed ? "Confirm withdraw" : "Withdraw",
            allowed ? (_withdrawArmed ? WithdrawCostLine : "") : reason);
    }

    /// <summary>First press arms and states the cost. Second press leaves.</summary>
    private void OnWithdrawPressed()
    {
        if (!WithdrawOffered || !WithdrawAllowed(out _))
        {
            RefreshWithdrawControl();
            return;
        }
        if (!_withdrawArmed)
        {
            _withdrawArmed = true;
            combatUI?.AppendActionLog($"Withdraw? {WithdrawCostLine}");
            RefreshWithdrawControl();
            return;
        }
        DeclareWithdrawn();
    }

    /// <summary>The withdrawal tail, the third sibling of DeclareVictory and
    /// DeclareDefeat. The party leaves alive: health carries out as it does after
    /// a won fight, nothing is paid, and the router is told the fight was not won
    /// so every reader of CombatWon treats the place as still held.</summary>
    private void DeclareWithdrawn()
    {
        if (currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat)
            return;

        // Units caught outside the zone pay for the retreat before health is carried out.
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || u.Stats == null || !u.Stats.IsAlive)
                continue;
            if (u.IsStructure || u.IsObjectiveWard || string.IsNullOrEmpty(u.CompanionId))
                continue;
            if (InPlayerDeployZone(u))
                continue;
            int kept = Mathf.Max(1, Mathf.CeilToInt(u.Stats.Health * 0.5f));
            GD.Print($"[Withdraw] {u.Name} was outside the deployment zone: {u.Stats.Health} to {kept} health.");
            u.Stats.Health = kept;
        }

        currentPhase = CombatPhase.Defeat;   // the ended-fight guard reads this; the banner is corrected below
        RefreshPhaseUI();
        combatUI?.SetPhaseText("Withdrawn");
        combatUI?.SetHintText("You left the fight. No rewards. No Anchor was spent.");
        combatUI?.SetWithdrawState(false, false, "Withdraw", "");
        GD.Print("=== WITHDRAWN ===");
        combatUI?.AppendActionLog("You withdrew from the fight.");
        CombatTelemetry.EndFight(false, roundNumber);

        CarryPartyHealthOut();

        // Marginalia: a fight that was not won commits no kills (see DeclareDefeat).
        if (EncounterRouter.Instance != null)
            EncounterRouter.Instance.SavedCombatFamilyKills = new Dictionary<string, int>();

        RecordTroopsAfterFight(false);
        RecordWizardEndHealth();
        PublishOutcome(CombatResult.Withdrawn);
        EmitSignal(SignalName.CombatCompleted, false);
    }

    /// <summary>K2.5: survivors carry their health into the next fight of this
    /// expedition. The same writeback DeclareVictory does, kept here as a copy
    /// because that tail is live code this stage does not restructure. If the
    /// rule changes there, change it here.</summary>
    private void CarryPartyHealthOut()
    {
        if (!PlayerSession.IsOnExpedition || SaveManager.ActiveSave == null)
            return;

        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive)
                continue;
            if (string.IsNullOrEmpty(u.CompanionId))
                continue;
            if (u.CompanionId == "wizard")
            {
                PlayerSession.WizardExpeditionHP = u.Stats.Health;
                PlayerSession.WizardExpeditionMaxHP = u.Stats.MaxHealth;
                continue;
            }
            var comp = SaveManager.ActiveSave.Companions?.Find(c => c.Id == u.CompanionId);
            if (comp == null)
                continue;
            comp.ExpeditionHP = u.Stats.Health;
        }
    }
}
