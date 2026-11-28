using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.Outcome.cs
//
// Purpose:        Combat overhaul stage C1. Three things that let a
//                 fight end some way other than the last body:
//                 the slay objective (kill the leader and the rest
//                 flee), the Withdraw action (each unit leaves by the
//                 exit, stage C5), and the outcome record the world reads.
// Layer:          Combat / runtime
// Collaborators:  CombatManager.cs (DeclareVictory / DeclareDefeat,
//                   SpawnAndPlaceEnemies, the UI wiring in _Ready),
//                 CombatManager.Objectives.cs (the objective latch,
//                   NoteObjectiveUnitDeath, the banner),
//                 CombatOutcome.cs (data),
//                 EncounterRouter.cs (LastOutcome),
//                 CombatUI.cs (SetWithdrawState, WithdrawPressed)
// See:            docs/combat_c1_break_withdraw_outcome_v1.md,
//                 docs/combat_c5_edge_withdraw_v1.md
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

    private const string WithdrawNoRewardsLine =
        "The fight ends with no rewards. No Anchor is spent.";

    /// <summary>Leaving the board is a move: it costs what one move costs.</summary>
    private const int WithdrawActionPointCost = 1;

    /// <summary>Party units that withdrew from this fight, by CompanionId. Their
    /// health was carried out at the moment they left the board.</summary>
    private readonly HashSet<string> _leftFight = new HashSet<string>();

    /// <summary>Reach fights: the units in <see cref="_leftFight"/> that left by the
    /// far exit (the objective's marked ground) and so count toward the objective.</summary>
    private readonly HashSet<string> _leftThrough = new HashSet<string>();

    /// <summary>Reach fights: the far exit plus, where retreat is offered, the
    /// deployment tiles. Built once the objective zone exists.</summary>
    private HashSet<Vector2I> _reachExitTiles;

    private int _leftWizardHealth = -1;
    private int _leftWizardMaxHealth = -1;
    private bool _wizardSeenOnBoard;
    private bool _exitZoneShown;

    /// <summary>Set by a mission that names its own way out (an extraction point).
    /// Null means the exit is the player's deployment tiles.</summary>
    private HashSet<Vector2I> _exitTilesOverride;
    private HashSet<Vector2I> _playerDeployTiles;
    private Timer _outcomeTicker;

    /// <summary>The Withdraw button is shown. True wherever a unit may leave the way
    /// it came in (<see cref="RetreatOffered"/>), and in every reach fight, where
    /// leaving by the far exit is how the objective is met.</summary>
    private bool WithdrawOffered => RetreatOffered || (ReachActive && !ScriptedFight);

    /// <summary>Field fights only in C1. A lost fight launched from the strategic
    /// map, the campus or a script has its own ending, and a withdrawal there
    /// has no reader yet.</summary>
    private bool RetreatOffered
    {
        get
        {
            // C5: the debug launcher's "Allow Withdraw" box offers it off the field.
            if (PlayerSession.DebugCombat && PlayerSession.DebugAllowWithdraw && !ScriptedFight)
                return true;
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
    /// not on the board. Null when nobody qualifies. Read by the reach objective
    /// (CombatManager.Reach.cs). A unit that withdrew is no longer on the board,
    /// so the next companion becomes the leader.</summary>
    private Unit WithdrawLeader()
    {
        Unit first = null;
        foreach (var u in playerUnits)
        {
            if (!IsPartyUnit(u))
                continue;
            if (u.CompanionId == "wizard")
                return u;
            first ??= u;
        }
        return first;
    }

    /// <summary>A living companion or the wizard. Summons, structures and the
    /// objective ward are not party units: they cannot withdraw and they do not
    /// keep a fight going once the party has left.</summary>
    private bool IsPartyUnit(Unit u)
    {
        if (u == null || !IsInstanceValid(u) || u.Stats == null || !u.Stats.IsAlive)
            return false;
        if (u.IsStructure || u.IsObjectiveWard || string.IsNullOrEmpty(u.CompanionId))
            return false;
        return true;
    }

    private int PartyOnBoardCount()
    {
        int n = 0;
        foreach (var u in playerUnits)
            if (IsPartyUnit(u))
                n++;
        return n;
    }

    private static string UnitLabel(Unit u) =>
        string.IsNullOrEmpty(u.DisplayName) ? u.Name.ToString() : u.DisplayName;

    /// <summary>The unit the Withdraw button acts on: the selected unit, when it
    /// is a party unit. Null otherwise.</summary>
    private Unit WithdrawingUnit() => IsPartyUnit(selectedUnit) ? selectedUnit : null;

    /// <summary>The tiles a unit withdraws from. A mission's own exit when one is
    /// set, otherwise the player's deployment tiles. Null until the board has
    /// its spawn zones.</summary>
    private HashSet<Vector2I> ExitTiles()
    {
        if (_exitTilesOverride != null && _exitTilesOverride.Count > 0)
            return _exitTilesOverride;
        if (grid == null)
            return null;
        if (ReachActive)
        {
            if (_reachExitTiles != null)
                return _reachExitTiles;
            EnsureObjectiveZone();
            if (_objectiveZone == null || _objectiveZone.Count == 0)
                return null;   // the far exit is not placed yet: try again on the next ask
            var both = new HashSet<Vector2I>(_objectiveZone);
            if (RetreatOffered)
            {
                var deploy = DeployTiles();
                if (deploy == null)
                    return null;
                both.UnionWith(deploy);
            }
            _reachExitTiles = both;
            return _reachExitTiles;
        }
        return DeployTiles();
    }

    /// <summary>The player's deployment tiles, cached. Null until the board has
    /// its spawn zones.</summary>
    private HashSet<Vector2I> DeployTiles()
    {
        if (grid == null)
            return null;
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
                return null;   // not generated yet: try again on the next ask
            _playerDeployTiles = tiles;
        }
        return _playerDeployTiles;
    }

    private bool OnExitTile(Unit unit)
    {
        var tiles = ExitTiles();
        return unit?.CurrentTile != null && tiles != null && tiles.Contains(unit.CurrentTile.Axial);
    }

    /// <summary>For a mission with its own way out. Pass null to go back to the
    /// deployment tiles. The outline is redrawn on the next tick.</summary>
    private void SetExitTiles(HashSet<Vector2I> tiles)
    {
        _exitTilesOverride = tiles;
        _exitZoneShown = false;
    }

    /// <summary>True when a wipe of the units still on the board is a withdrawal
    /// and not a defeat: the wizard already left, or the wizard was never
    /// fielded and somebody left. Read by CheckCombatEnd.</summary>
    private bool WithdrawalStandsOnWipe =>
        _leftFight.Contains("wizard") || (!_wizardSeenOnBoard && _leftFight.Count > 0);

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
        if (selectedUnit != null && IsInstanceValid(selectedUnit) && !IsPartyUnit(selectedUnit))
        {
            reason = $"{UnitLabel(selectedUnit)} cannot withdraw. Select one of your party.";
            return false;
        }
        var unit = WithdrawingUnit();
        if (unit == null)
        {
            reason = "Select a unit to withdraw it from the fight.";
            return false;
        }
        string who = UnitLabel(unit);
        if (!OnExitTile(unit))
        {
            reason = $"Move {who} to the exit (green outline) to withdraw.";
            return false;
        }
        if (EnemyAdjacentTo(unit))
        {
            reason = $"An enemy is next to {who}.";
            return false;
        }
        if (OnFarExit(unit))
        {
            var holder = EnemyHoldingFarExit();
            if (holder != null)
            {
                reason = $"{UnitLabel(holder)} is standing on the far exit. Kill it or move it off before anyone leaves.";
                return false;
            }
        }
        if (!unit.CanSpendAP(WithdrawActionPointCost))
        {
            reason = $"{who} has no action points left. Withdrawing costs {WithdrawActionPointCost}, like a move.";
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
        if (SlayActive || ReachActive)
            RefreshObjectiveBanner();   // no-ops unless the line changed (leader health, who is on the marked ground)
        RefreshWithdrawControl();
        // Ground marks are a held view, not always on: the ground's own material is the
        // first read (docs/snow_ice_ground_v1.md SG3). Hold Alt to draw them.
        if (Input.IsKeyPressed(Key.Alt))
            _zoneRenderer?.RefreshGroundMarks(grid);
        else
            _zoneRenderer?.ClearGroundMarks();
    }

    private void RefreshWithdrawControl()
    {
        if (combatUI == null)
            return;

        bool live = currentPhase == CombatPhase.PlayerTurn || currentPhase == CombatPhase.EnemyTurn;
        if (!live || !WithdrawOffered)
        {
            if (_exitZoneShown)
            {
                _zoneRenderer?.ClearExitZone();
                _exitZoneShown = false;
            }
            combatUI.SetWithdrawState(false, false, "Withdraw", "");
            return;
        }

        if (!_wizardSeenOnBoard)
        {
            foreach (var u in playerUnits)
            {
                if (u != null && IsInstanceValid(u) && u.CompanionId == "wizard")
                {
                    _wizardSeenOnBoard = true;
                    break;
                }
            }
        }

        if (!_exitZoneShown && _zoneRenderer != null)
        {
            var exit = ExitTiles();
            if (exit != null)
            {
                _zoneRenderer.ShowExitZone(exit, grid);
                _exitZoneShown = true;
            }
        }

        bool allowed = WithdrawAllowed(out string reason);
        string line = allowed ? WithdrawCostLine(WithdrawingUnit()) : reason;
        combatUI.SetWithdrawState(true, allowed, "Withdraw", line);
    }

    /// <summary>What pressing the button does, in plain words, shown above it
    /// while it is enabled. The last party unit on the board ends the fight
    /// when it leaves.</summary>
    private string WithdrawCostLine(Unit unit)
    {
        string who = UnitLabel(unit);
        string cost = WithdrawActionPointCost == 1
            ? "1 action point"
            : $"{WithdrawActionPointCost} action points";
        if (ReachActive)
        {
            if (OnFarExit(unit))
            {
                if (ReachMetIfLeaves(unit))
                {
                    int behind = ReachLeftBehindCount(unit);
                    string behindLine = behind == 0
                        ? " Nobody is left behind."
                        : behind == 1
                            ? " 1 unit is still on the board and will lose half its health."
                            : $" {behind} units are still on the board and will each lose half their health.";
                    return $"Withdrawing costs {cost}. {who} leaves by the far exit. " +
                           $"That meets the objective and wins the fight.{behindLine}";
                }
                return $"Withdrawing costs {cost}. {who} leaves by the far exit, counts toward the objective and cannot come back.";
            }
            if (PartyOnBoardCount() <= 1)
                return $"{who} is your last unit on the board. Withdrawing costs {cost} and ends the fight: " +
                       "the objective is not met, no rewards, no Anchor spent.";
            return $"Withdrawing costs {cost}. {who} leaves the way you came in, does not count toward " +
                   "the objective and cannot come back.";
        }
        if (PartyOnBoardCount() <= 1)
            return $"{who} is your last unit on the board. Withdrawing costs {cost} and ends the fight: " +
                   "no rewards, no Anchor spent.";
        return $"Withdrawing costs {cost}. {who} leaves with its current health and cannot come back. " +
               "The fight goes on.";
    }

    /// <summary>One press. The line above the button has already said what it
    /// costs and what it ends.</summary>
    private void OnWithdrawPressed()
    {
        if (!WithdrawOffered || !WithdrawAllowed(out _))
        {
            RefreshWithdrawControl();
            return;
        }
        var unit = WithdrawingUnit();
        if (unit == null)
            return;
        WithdrawUnit(unit);
    }

    /// <summary>One unit leaves by the exit, alive. Its health is written out
    /// now, because the declare tails only carry out units still on the board.
    /// It is taken off the board and out of the unit lists, so nothing targets
    /// it, and it does not come back. When it was the last party unit the fight
    /// ends as a withdrawal.</summary>
    private void WithdrawUnit(Unit unit)
    {
        if (!IsPartyUnit(unit) || !unit.CanSpendAP(WithdrawActionPointCost))
            return;
        unit.CurrentActionPoints -= WithdrawActionPointCost;
        string who = UnitLabel(unit);
        bool isWizard = unit.CompanionId == "wizard";
        bool through = OnFarExit(unit);   // read now: the unit has no tile once it is lifted

        if (PlayerSession.IsOnExpedition && SaveManager.ActiveSave != null)
        {
            if (isWizard)
            {
                PlayerSession.WizardExpeditionHP = unit.Stats.Health;
                PlayerSession.WizardExpeditionMaxHP = unit.Stats.MaxHealth;
            }
            else
            {
                var comp = SaveManager.ActiveSave.Companions?.Find(c => c.Id == unit.CompanionId);
                if (comp != null)
                    comp.ExpeditionHP = unit.Stats.Health;
            }
        }
        if (isWizard)
        {
            _leftWizardHealth = unit.Stats.Health;
            _leftWizardMaxHealth = unit.Stats.MaxHealth;
        }
        _leftFight.Add(unit.CompanionId);
        if (through)
            _leftThrough.Add(unit.CompanionId);

        DropReactionOnDeath(unit);             // an armed reaction does not outlast its unit
        ConduitLinkSystem.OnUnitDied(unit);    // nor does a link

        if (selectedUnit == unit)
        {
            unit.SetSelected(false);
            selectedUnit = null;
            ClearMoveTiles();
            ClearTargetHighlight();
        }
        if (_hoveredUnit == unit)
            _hoveredUnit = null;

        unit.LiftFromBoard();
        playerUnits.Remove(unit);
        State?.UnitsInPlay.Remove(unit);

        GD.Print($"[Withdraw] {unit.Name} left the fight at {unit.Stats.Health} health.");
        combatUI?.AppendActionLog(through
            ? $"{who} left by the far exit."
            : $"{who} withdrew from the fight.");

        // Reach: enough of the party is out by the far exit. The latch is declared
        // by CheckCombatEnd like every other objective win, before the empty-board test.
        if (ReachActive && NoteReachExit() && CheckCombatEnd())
            return;

        // A latched win that CheckCombatEnd deferred is declared on a later check,
        // so an empty board must not be called a withdrawal in the meantime.
        if (PartyOnBoardCount() == 0 && !_objectiveVictory)
        {
            DeclareWithdrawn();
            return;
        }

        RefreshSelectedUnitUI();
        RefreshThreatTiles();
        RefreshPlayerUnitBar();
        RefreshEnemyRoster();
        CallDeferred(nameof(SelectNextLivingAfterDeath));
    }

    /// <summary>The withdrawal tail, the third sibling of DeclareVictory and
    /// DeclareDefeat. The party leaves alive: health carries out as it does after
    /// a won fight, nothing is paid, and the router is told the fight was not won
    /// so every reader of CombatWon treats the place as still held.</summary>
    private void DeclareWithdrawn()
    {
        if (currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat)
            return;

        currentPhase = CombatPhase.Defeat;   // the ended-fight guard reads this; the banner is corrected below
        RefreshPhaseUI();
        combatUI?.SetPhaseText("Withdrawn");
        combatUI?.SetHintText("You left the fight. No rewards. No Anchor was spent.");
        combatUI?.SetWithdrawState(false, false, "Withdraw", "");
        _zoneRenderer?.ClearExitZone();
        _exitZoneShown = false;
        GD.Print("=== WITHDRAWN ===");
        combatUI?.AppendActionLog("You withdrew from the fight.");
        CombatTelemetry.EndFight(false, roundNumber);

        CarryPartyHealthOut();

        // Marginalia: a fight that was not won commits no kills (see DeclareDefeat).
        if (EncounterRouter.Instance != null)
            EncounterRouter.Instance.SavedCombatFamilyKills = new Dictionary<string, int>();

        RecordTroopsAfterFight(false);
        RecordWizardEndHealth();
        if (_leftWizardHealth >= 0 && EncounterRouter.Instance != null)
        {
            // The wizard left by the exit, so it is no longer in the unit list that call reads.
            EncounterRouter.Instance.LastWizardHealth = _leftWizardHealth;
            EncounterRouter.Instance.LastWizardMaxHealth = _leftWizardMaxHealth;
        }
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
