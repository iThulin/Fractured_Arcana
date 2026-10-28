using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Sundering.cs  (partial of CombatManager)
//
// Purpose:        The Sundering, rebuilt (S1,
//                 docs/sundering_long_second_founding_v1.md section 2).
//                 A scripted fight whose script carries a sentence runs
//                 on it: one clause a round, shown on the objective
//                 line, and the fight ends when the last clause does,
//                 never on an empty board. Around that timer:
//                  - the opening is theirs: an enemy phase before the
//                    first turn, outside the round count, with the
//                    camera left on the dais so it lands from behind;
//                  - the hold: lethal damage leaves the candidate at 1
//                    health until the sentence ends (Unit.HeldBySentence);
//                  - the volley phase: after the constructs and before
//                    the enemies, the faculty on the dais, the Examiner
//                    and the class on the tiers each cast once, as set
//                    figures off the board (SunderingScript's rules);
//                  - the round boundary: the Provost adds a clause, the
//                    Keepers' counter-volley spends one figure, and a
//                    piece of the hall breaks (HallSet.BreakPiece);
//                  - the floor minimum: at each player turn the doors
//                    make the Keepers up to the script's floor;
//                  - the traitor's line, from the hand at the sphere to
//                    the lectern, all fight (CombatManager.AimLines.cs);
//                  - three framings: the dais, the side, the climax;
//                  - the climax: the candidate is drawn onto the mark,
//                    the spell leaves the hand and stops an arm's length
//                    away, and the fight is won. The cast panel follows.
//                 Inert in every fight whose script has no sentence.
// Layer:          Combat / runtime
// Collaborators:  PrologueDirector (PrologueFightScript), SunderingScript
//                 (the volley set), HallSet.Sundering.cs (the points, the
//                 figures, the breaks), Unit (HeldBySentence,
//                 SentenceHeld), CombatPresenter (EmitVolley, BaseSpeed),
//                 CameraController (SwingToward), CombatUI
//                 (SetObjectiveTooltip), CombatManager.cs (the hooks),
//                 CombatManager.Objectives.cs (ArmSundering, the banner),
//                 CombatManager.AimLines.cs (the traitor's line)
// ============================================================

public partial class CombatManager
{
    // ── Tuning (starting values, tuned in play) ──────────────────────────
    /// <summary>The script's camera mode that hands the framings to this file.</summary>
    private const string SunderingCameraMode = "sundering";
    /// <summary>The side view's center, as a fraction of the board from its west edge: left of
    /// the middle, so the dais is in frame at the left of the screen and the doors at the right.</summary>
    private const float SunderingSideAlong = 0.38f;
    private const float SunderingSideZoom = 15f;
    private const float SunderingDaisZoom = 10f;
    private const float SunderingClimaxZoom = 10f;
    /// <summary>Seconds after the first turn starts before the camera swings to the side, so the
    /// swing follows the turn-start selection glide rather than fighting it.</summary>
    private const double SunderingSideDelay = 0.35;
    /// <summary>A standing figure's chest above its feet (the shared rig's stand-in).</summary>
    private const float SunderingChest = 0.95f;
    /// <summary>Where the spell stops short of the candidate's chest, along its line.</summary>
    private const float SunderingArmLength = 0.7f;
    private const float SunderingDrawLift = 0.35f;
    private const float SunderingDrawSeconds = 1.4f;
    private const float SunderingSpellSeconds = 0.9f;
    /// <summary>How far from the candidate Write That Down's turret may land.</summary>
    private const int SunderingTurretReach = 2;
    /// <summary>The graduating class: twenty-six, and you (canon sheet).</summary>
    private const int SunderingClassSize = 26;
    /// <summary>How far a spent member of the class sinks: down behind the benches.</summary>
    private const float SunderingClassDrop = 0.6f;
    /// <summary>The counter-volley at the tiers flies at this many of the class.</summary>
    private const int SunderingClassTargets = 6;
    /// <summary>The longest one beat waits on the presenter, in seconds.</summary>
    private const float SunderingPresenterCap = 6f;
    /// <summary>A Name the faculty writes belongs to no team on the board, so none of your own
    /// Enchanter effects (the Weave, The Price Compounds, The Seventh Name) reads it as yours.</summary>
    private const int SunderingFacultyTeam = -1;

    private const string SunderingClassId = "class";
    private const string SunderingExaminerId = "examiner";
    private const string SunderingProvostId = "wenna";
    private const string SunderingWitnessId = "hess";
    private const string SunderingLastClause = "The Last Clause";

    /// <summary>The six chairs in Commencement's order (the Astrologer's is the seventh, at the
    /// north end). The traitor's is the honored chair, and the others fill the ring from the
    /// south end in this order. The Witness left before the wards broke, so hers stays empty.</summary>
    private static readonly string[] SunderingChairOrder =
    {
        "aurel", "hess", "joren", "namer", "conductor", "engineer",
    };

    private static readonly string[] SunderingGlobals =
    {
        "wizard_name", "guild_name", "traitor_full", "traitor_short",
    };

    // ── State (one fight; ArmSundering clears it) ────────────────────────
    private PrologueFightScript _sunderingScript;
    private SunderingVolleySet _sunderingSet;
    private string _sunderingTraitor = "";
    private HallSet _sunderingHall;
    private bool _sunderingStaged;
    private bool _sunderingSideFramed;
    private bool _sunderingClimaxStarted;
    private bool _sunderingTurretLanded;
    private int _sunderingHoldRound = -1;
    private Unit _sunderingHeld;
    /// <summary>The casting figures by volley id: the loyal archmagi and the Examiner.</summary>
    private readonly Dictionary<string, Node3D> _sunderingFigures = new();
    private readonly List<Node3D> _sunderingClass = new();
    private readonly HashSet<string> _sunderingSpent = new();
    private readonly HashSet<Unit> _sunderingWatched = new();
    /// <summary>Where Keepers fell since Last Orders last read it, oldest first.</summary>
    private readonly List<Vector2I> _sunderingFallen = new();
    private readonly Random _sunderingRng = new();

    // ── Predicates (the hooks in CombatManager.cs read these) ────────────
    /// <summary>This fight runs on a sentence (the Sundering).</summary>
    private bool SunderingArmed => _sunderingScript != null;

    /// <summary>The Keepers act before the first player turn.</summary>
    private bool SunderingOpensWithEnemies => SunderingArmed && _sunderingScript.EnemiesOpen;

    /// <summary>The fight ends when its sentence does, never on an empty board.</summary>
    private bool SunderingEndsOnSentence => SunderingArmed && _sunderingScript.EndsOnSentence;

    /// <summary>This file frames the camera (the dais, the side, the climax).</summary>
    private bool SunderingOwnsCamera => SunderingArmed && _sunderingScript.HallCamera == SunderingCameraMode;

    /// <summary>The last clause's enemy phase is over: the climax comes instead of a round.</summary>
    private bool SunderingClimaxDue =>
        SunderingArmed && !_sunderingClimaxStarted && roundNumber >= _sunderingScript.Clauses.Count;

    /// <summary>One of the five stands at the sphere this reading.</summary>
    private bool SunderingHasTraitor =>
        Array.IndexOf(CampaignGenerator.TraitorPool, _sunderingTraitor ?? "") >= 0;

    private Unit SunderingCandidate =>
        playerUnits.Find(u => u != null && IsInstanceValid(u) && u.CompanionId == "wizard");

    // ════════════════════════════════════════════════════════════════════════
    //  Arming and staging
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Combat start (InitObjectiveState): read the scripted fight's sentence, its volley
    /// set and this reading's traitor. Leaves everything off for any fight without a sentence.</summary>
    private void ArmSundering()
    {
        _sunderingScript = null;
        _sunderingSet = null;
        _sunderingTraitor = "";
        _sunderingHall = null;
        _sunderingStaged = false;
        _sunderingSideFramed = false;
        _sunderingClimaxStarted = false;
        _sunderingTurretLanded = false;
        _sunderingHoldRound = -1;
        _sunderingHeld = null;
        _sunderingFigures.Clear();
        _sunderingClass.Clear();
        _sunderingSpent.Clear();
        _sunderingWatched.Clear();
        _sunderingFallen.Clear();
        if (!ScriptedFight)
            return;
        var script = PrologueDirector.ScriptFor(EncounterRouter.Instance?.ScriptedFightId);
        if (script?.Clauses == null || script.Clauses.Count == 0)
            return;
        _sunderingScript = script;
        _sunderingSet = SunderingScript.Get(script.Volleys);
        _sunderingTraitor = SaveManager.ActiveSave?.Ledger?.CoConspiratorId ?? "";
        if (_sunderingSet == null && !string.IsNullOrEmpty(script.Volleys))
            GD.PushError($"[Sundering] The volley set '{script.Volleys}' is not in {SunderingScript.DataPath}; "
                         + "the fight runs without its faculty.");
        GD.Print($"[Sundering] Armed: {script.Clauses.Count} clause(s), volleys '{script.Volleys}', traitor '"
                 + (SunderingHasTraitor ? _sunderingTraitor : "none") + "'.");
    }

    /// <summary>OrientCameraForCombat's hook (deployment): stand the figures, hold the candidate,
    /// set the presenter's pace and the objective line's hover text, and frame the dais. True
    /// when the camera was framed here, so the default facing is skipped.</summary>
    private bool BeginSunderingStage(CameraController controller)
    {
        if (!SunderingArmed)
            return false;
        StageSunderingFigures();
        HoldTheCandidate();
        ApplySunderingPace();
        combatUI?.SetObjectiveTooltip(SunderingBannerTooltip());
        RefreshObjectiveBanner();
        if (!SunderingOwnsCamera || controller == null)
            return false;
        FrameSunderingDais(controller);
        return true;
    }

    /// <summary>The Ceremony Hall round the board, or null on any other map.</summary>
    private HallSet SunderingHall
    {
        get
        {
            if (_sunderingHall != null && IsInstanceValid(_sunderingHall) && !_sunderingHall.IsQueuedForDeletion())
                return _sunderingHall;
            _sunderingHall = null;
            if (grid == null || !IsInstanceValid(grid))
                return null;
            foreach (Node child in grid.GetChildren())
            {
                if (child is HallSet hall && !hall.IsQueuedForDeletion() && hall.IsCeremonyHall)
                {
                    _sunderingHall = hall;
                    break;
                }
            }
            return _sunderingHall;
        }
    }

    /// <summary>Stands the set figures once (D39 to D41): the loyal archmagi in front of their
    /// chairs, the traitor at the sphere, the Provost at the lectern facing the dais, the Examiner
    /// at the foot of the dais, and the class along both walks. Figures belong to the hall.</summary>
    private void StageSunderingFigures()
    {
        if (_sunderingStaged || grid == null)
            return;
        var hall = SunderingHall;
        if (hall == null)
        {
            GD.PushWarning("[Sundering] No Ceremony Hall round the board: no figures, no breaks, no traitor's line.");
            return;
        }
        _sunderingStaged = true;
        Vector3 board = (grid.GridBoundsMin + grid.GridBoundsMax) * 0.5f;

        // The chairs: the traitor's is the honored one (they are at the sphere, out of it), the
        // Astrologer's stays empty, and the rest fill from the south end.
        bool hasTraitor = SunderingHasTraitor;
        var chairs = new List<int>();
        for (int i = 0; i < HallSet.ChairCount; i++)
            if (i != HallSet.AstrologerChair && !(hasTraitor && i == HallSet.HonoredChair))
                chairs.Add(i);
        int next = 0;
        foreach (var id in SunderingChairOrder)
        {
            if (id == _sunderingTraitor || next >= chairs.Count)
                continue;
            int chair = chairs[next++];
            if (id == SunderingWitnessId)
                continue;   // the Witness left before the wards broke: her chair stands empty
            Vector3 feet = hall.ChairStandPoint(chair);
            _sunderingFigures[id] = hall.PlaceFigure(id, feet, board - feet, SunderingRobe(id));
        }

        if (hasTraitor)
        {
            Vector3 stand = hall.TraitorStandPoint;
            hall.PlaceFigure("traitor", stand, hall.SphereCenter - stand, SunderingRobe(_sunderingTraitor));
        }
        hall.PlaceFigure("provost", hall.ReaderPoint, hall.DaisCenter - hall.ReaderPoint, SunderingRobe(SunderingProvostId));
        _sunderingFigures[SunderingExaminerId] = hall.PlaceFigure("examiner", hall.ExaminerPoint,
            board - hall.ExaminerPoint, HallSet.ExaminerRobe);

        var points = hall.ClassStandPoints(SunderingClassSize);
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 p = points[i];
            _sunderingClass.Add(hall.PlaceFigure("class_" + i, p, new Vector3(0f, 0f, board.Z - p.Z), HallSet.ClassRobe));
        }
        GD.Print($"[Sundering] Staged {_sunderingFigures.Count - 1} archmagi on the dais, the Examiner, the Provost"
                 + (hasTraitor ? ", the traitor at the sphere" : "") + $" and {_sunderingClass.Count} of the class.");
    }

    /// <summary>A figure's robe: the school of the archmagus, gold when unknown.</summary>
    private static Color SunderingRobe(string archmageId)
    {
        var def = ArchmageRegistry.Get(archmageId);
        return def != null && Enum.TryParse<CardSchool>(def.School, true, out var school)
            ? SchoolColors.GetBorderColor(school)
            : UITheme.Gold;
    }

    /// <summary>The presenter's pace for the whole fight (the script's presenterSpeed; 2x).</summary>
    private void ApplySunderingPace()
    {
        var presenter = CombatPresenter.Current;
        if (!SunderingArmed || presenter == null || !IsInstanceValid(presenter))
            return;
        float speed = Mathf.Max(0.25f, _sunderingScript.PresenterSpeed);
        presenter.BaseSpeed = speed;
        presenter.SpeedScale = Mathf.Max(presenter.SpeedScale, speed);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The hold
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The unfinished sentence holds the candidate: lethal damage leaves them at 1
    /// health, and nothing can strip it (a unit field, as the wards are). Idempotent.</summary>
    private void HoldTheCandidate()
    {
        if (!SunderingArmed || !_sunderingScript.Hold || _sunderingClimaxStarted)
            return;
        var candidate = SunderingCandidate;
        if (candidate == null || candidate == _sunderingHeld)
            return;
        candidate.HeldBySentence = true;
        candidate.SentenceHeld += OnSentenceHeld;
        _sunderingHeld = candidate;
        candidate.RefreshHealthBar();
        GD.Print($"[Sundering] {candidate.Name} is held by the sentence until it ends.");
    }

    /// <summary>The hold kept the candidate standing: the log line and the ink's flare, once a
    /// round at most. The hold itself never lapses.</summary>
    private void OnSentenceHeld(Unit unit)
    {
        if (!IsInstanceValid(this) || unit == null || !IsInstanceValid(unit) || _sunderingHoldRound == roundNumber)
            return;
        _sunderingHoldRound = roundNumber;
        SunderingLog(_sunderingSet?.HoldLog);
        // A beat after the blow, so the flare answers the hit rather than racing its flight.
        GetTree().CreateTimer(0.3).Timeout += () => SunderingFlare(unit);
    }

    /// <summary>The ink of the half-written line flares round the candidate.</summary>
    private void SunderingFlare(Unit unit)
    {
        if (!IsInstanceValid(this) || unit == null || !IsInstanceValid(unit) || !unit.IsInsideTree())
            return;
        var burst = new BurstVfx { Tint = UITheme.Gold, Radius = 1.1f, GroundRing = true };
        AddChild(burst);
        burst.GlobalPosition = unit.GlobalPosition + new Vector3(0f, SunderingChest, 0f);
        _ = burst.Play(1f);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The opening, and each player turn
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>EndDeploymentPhase's hook: the wards break from inside and the Keepers act first,
    /// on the intents planned at spawn. It is neither a clause nor a round: it skips the round's
    /// advance and its wave, and hands straight to round 1's player turn.</summary>
    private async void RunSunderingOpening()
    {
        if (!SunderingArmed)
        {
            StartPlayerTurn();
            return;
        }
        currentPhase = CombatPhase.EnemyTurn;
        enemyPhaseRunning = true;
        _zoneRenderer?.Clear();
        StageSunderingFigures();
        HoldTheCandidate();
        ApplySunderingPace();

        try
        {
            PlanAllEnemyIntents();
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Sundering] The opening's plans THREW: {e}");
        }
        foreach (var enemy in enemyUnits)
            if (IsValidActor(enemy))
                enemy.StartTurn();
        WatchSunderingKeepers();
        RefreshPhaseUI();
        RefreshSelectedUnitUI();
        combatUI?.SetPhaseText("The Opening");
        combatUI?.SetHintText("The Keepers act first.");

        SunderingLog(_sunderingSet?.OpeningLog);
        SunderingBreaks(0);
        await SunderingPause(0.5f);

        // Their first spell, from the doors, between your shoulders (D37).
        var strike = _sunderingSet?.OpeningStrike;
        var candidate = SunderingCandidate;
        if (strike != null && strike.Damage > 0 && IsValidActor(candidate))
        {
            CombatPresenter.EmitVolley(SunderingDoorway(), new[] { VisualEvent.RulesPositionOf(candidate) },
                                       VfxLibrary.ArchBolt, SunderingKeeperStyle);
            candidate.ApplyDamage(strike.Damage);
            SunderingLog(strike.Log);
            await SunderingAwaitPresenter();
            await SunderingPause(0.3f);
        }

        // Their phase, out of frame: the camera stays on the dais while they come in behind you.
        var camera = CombatCamera;
        CombatCamera = null;
        try
        {
            await RunEnemyTurn();
        }
        finally
        {
            if (IsInstanceValid(this))
                CombatCamera = camera;
        }
        if (!IsInstanceValid(this))
            return;
        PruneDeadUnits();
        if (CheckCombatEnd())
            return;
        GD.Print("[Sundering] The opening is over: round 1, and the first clause.");
        StartPlayerTurn();
    }

    /// <summary>StartPlayerTurn's hook: the hold, the floor minimum (D48), the Keepers to watch for
    /// Last Orders, and on the first turn the swing to the side view.</summary>
    private void SunderingTurnStart()
    {
        if (!SunderingArmed || _sunderingClimaxStarted)
            return;
        HoldTheCandidate();
        TopUpSunderingFloor();
        WatchSunderingKeepers();
        if (SunderingOwnsCamera && !_sunderingSideFramed)
        {
            _sunderingSideFramed = true;
            GetTree().CreateTimer(SunderingSideDelay).Timeout += FrameSunderingSide;
        }
    }

    /// <summary>The floor never empties before the last clause: when fewer Keepers stand than
    /// the script's floor, more come through the doors (the volley set's floor unit), far from
    /// you, with their plans shown, as a wave's do.</summary>
    private void TopUpSunderingFloor()
    {
        int floor = _sunderingScript?.FloorMinimum ?? 0;
        string unitId = _sunderingSet?.FloorUnit ?? "";
        if (floor <= 0 || unitId.Length == 0)
            return;
        int shortBy = floor - SunderingKeepers().Count;
        if (shortBy <= 0)
            return;
        if (!UnitRegistry.TryResolveId(unitId, out var resolved))
        {
            GD.PushError($"[Sundering] The floor unit '{unitId}' does not resolve.");
            return;
        }
        var tiles = CollectEnemyArrivalTiles(shortBy);
        int spawned = 0;
        for (int i = 0; i < shortBy && i < tiles.Count; i++)
        {
            if (SpawnRegistryUnit(resolved, tiles[i], teamId: 1, difficultyMult: _sunderingSet.FloorUnitMult,
                                  isMidFightSummon: false) != null)
                spawned++;
        }
        if (spawned == 0)
            return;
        PlanAllEnemyIntents();
        RefreshEnemyRoster();
        SunderingLog(_sunderingSet.FloorLog);
        GD.Print($"[Sundering] The floor minimum: {spawned} more through the doors ({floor} must stand).");
    }

    /// <summary>Every Keeper standing, so Last Orders knows where each one falls. Called at each
    /// turn start and volley phase; a Keeper is watched once.</summary>
    private void WatchSunderingKeepers()
    {
        foreach (var unit in enemyUnits)
            if (unit != null && IsInstanceValid(unit) && unit.Stats.IsAlive && _sunderingWatched.Add(unit))
                unit.OnDied += OnSunderingKeeperFell;
    }

    /// <summary>OnDied runs before Die clears the tile, so the tile is still where it fell.</summary>
    private void OnSunderingKeeperFell(Unit unit)
    {
        if (unit?.CurrentTile != null && unit.TeamId != 0)
            _sunderingFallen.Add(unit.CurrentTile.Axial);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The volley phase
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>EndPlayerTurn's hook, after the construct phase: each figure still casting casts
    /// once, in the volley set's order, one beat each. The phase belongs to no one on the board,
    /// so the player's input waits (the enemy phase follows at once).</summary>
    private async Task RunSunderingVolleys()
    {
        if (!SunderingArmed || _sunderingSet?.Volleys == null || _sunderingClimaxStarted)
            return;
        if (currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat)
            return;
        currentPhase = CombatPhase.EnemyTurn;
        combatUI?.SetPhaseText($"Round {roundNumber} - The Faculty");
        combatUI?.SetHintText("The dais and the tiers answer.");
        WatchSunderingKeepers();
        FrameSunderingSide();
        await SunderingPause(0.3f);

        foreach (var volley in _sunderingSet.Volleys)
        {
            if (!IsInstanceValid(this) || currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat)
                return;
            if (volley == null || !SunderingCasts(volley.Id))
                continue;
            if (!await CastSunderingVolley(volley))
                continue;
            await SunderingAwaitPresenter();
            await SunderingPause(0.15f);
        }

        PruneDeadUnits();
        await DrainTriggerStackAsync();
        RefreshEnemyRoster();
        RefreshThreatTiles();
        RefreshAllUI();
    }

    /// <summary>True for a figure that still casts: the class, the Examiner or one of the five,
    /// not the traitor (at the sphere) and not spent.</summary>
    private bool SunderingCasts(string id)
    {
        if (string.IsNullOrEmpty(id) || id == _sunderingTraitor || _sunderingSpent.Contains(id))
            return false;
        return id == SunderingClassId || id == SunderingExaminerId
               || Array.IndexOf(CampaignGenerator.TraitorPool, id) >= 0;
    }

    /// <summary>One volley by its rule (SunderingScript's constants). False when it had nothing
    /// to cast at, so it takes no beat.</summary>
    private async Task<bool> CastSunderingVolley(SunderingVolley v)
    {
        switch (v.Rule)
        {
            case SunderingScript.RuleWorstIntent:
                return VolleyWorstIntent(v);
            case SunderingScript.RuleClusterStorm:
                return await VolleyClusterStorm(v);
            case SunderingScript.RuleNameNearest:
                return VolleyNameNearest(v);
            case SunderingScript.RuleRaiseFallen:
                return await VolleyRaiseFallen(v);
            case SunderingScript.RuleTurretThenBolt:
                return VolleyTurretThenBolt(v);
            case SunderingScript.RuleUnerringNearest:
                return VolleyBolt(v, v.Log);
            case SunderingScript.RuleRandomBolts:
                return VolleyRandomBolts(v);
        }
        GD.PushWarning($"[Sundering] Volley '{v.Id}' has rule '{v.Rule}', which the fight does not run.");
        return false;
    }

    /// <summary>The Corrected Text: the Keeper with the most dangerous plan takes the damage, and
    /// with Negate its next action is struck. A plan aimed at you outranks any other.</summary>
    private bool VolleyWorstIntent(SunderingVolley v)
    {
        var keepers = SunderingKeepers();
        if (keepers.Count == 0)
            return false;
        var anchor = SunderingAnchor();
        var target = keepers
            .Where(k => !(v.Negate && k.NegateNextAction))
            .OrderByDescending(SunderingDanger)
            .ThenBy(k => grid.Distance(anchor, k.CurrentTile.Axial))
            .FirstOrDefault() ?? SunderingNearestKeeper(keepers);
        if (target == null)
            return false;
        string name = target.DisplayName;
        SunderingHit(v, new List<Unit> { target }, v.Damage);
        if (v.Negate && IsValidActor(target))
            target.NegateNextAction = true;   // its next action is struck from the minutes
        SunderingLog(v.Log, ("caster", SunderingCasterName(v.Id)), ("target", name), ("damage", v.Damage.ToString()));
        return true;
    }

    /// <summary>How much a Keeper's locked plan threatens: its value times the tiles it covers,
    /// and far more when it is aimed at the candidate.</summary>
    private int SunderingDanger(Unit keeper)
    {
        var intent = keeper?.CurrentIntent;
        if (intent == null || intent.Kind == IntentKind.Guard || intent.Kind == IntentKind.Unknown)
            return 0;
        int score = Math.Max(1, intent.Value) * Math.Max(1, intent.ThreatTiles?.Count ?? 0);
        var candidate = SunderingCandidate;
        if (candidate?.CurrentTile != null)
        {
            var at = candidate.CurrentTile.Axial;
            bool atYou = intent.TargetTile == at
                         || (intent.ThreatTiles != null && intent.ThreatTiles.Contains(at))
                         || (intent.Unerring && intent.TargetUnit == candidate);
            if (atYou)
                score += 1000;
        }
        return score;
    }

    /// <summary>The First Argument: storm over the thickest cluster of Keepers (Radius about one of
    /// them; the nearest cluster to you on a tie), the damage to each, then the ground there takes
    /// Storm, except under your own side.</summary>
    private async Task<bool> VolleyClusterStorm(SunderingVolley v)
    {
        var keepers = SunderingKeepers();
        if (keepers.Count == 0)
            return false;
        int radius = Math.Max(0, v.Radius);
        var anchor = SunderingAnchor();
        Unit eye = null;
        int bestCount = -1;
        int bestDistance = int.MaxValue;
        foreach (var k in keepers)
        {
            int count = keepers.Count(o => grid.Distance(k.CurrentTile.Axial, o.CurrentTile.Axial) <= radius);
            int distance = grid.Distance(anchor, k.CurrentTile.Axial);
            if (count > bestCount || (count == bestCount && distance < bestDistance))
            {
                eye = k;
                bestCount = count;
                bestDistance = distance;
            }
        }
        if (eye == null)
            return false;
        var at = eye.CurrentTile.Axial;
        var hit = keepers.Where(k => grid.Distance(at, k.CurrentTile.Axial) <= radius).ToList();
        SunderingHit(v, hit, v.Damage);
        SunderingLog(v.Log, ("caster", SunderingCasterName(v.Id)), ("count", hit.Count.ToString()),
                     ("damage", v.Damage.ToString()));

        await SunderingAwaitPresenter();   // the ground takes it once the storm has broken
        foreach (var kv in grid.Tiles)
        {
            var tile = kv.Value;
            if (tile == null || grid.Distance(at, kv.Key) > radius)
                continue;
            if (tile.Occupant != null && tile.Occupant.TeamId == 0)
                continue;
            ElementReactions.Imbue(tile, TileElementType.Lightning, 1f, null);
        }
        return true;
    }

    /// <summary>Terms: the Count Keepers nearest you that the faculty has not already Named are
    /// Named for Turns: Damage if they move.</summary>
    private bool VolleyNameNearest(SunderingVolley v)
    {
        var anchor = SunderingAnchor();
        var named = SunderingKeepers()
            .Where(k => !k.Names.Any(n => n.Source == v.Name && n.TurnsRemaining > 0))
            .OrderBy(k => grid.Distance(anchor, k.CurrentTile.Axial))
            .Take(Math.Max(1, v.Count))
            .ToList();
        if (named.Count == 0)
            return false;
        SunderingHit(v, named, 0);
        foreach (var k in named)
        {
            Names.Write(k, new NameCondition
            {
                Triggers = NameTrigger.Move,
                Damage = v.Damage,
                TurnsRemaining = Math.Max(1, v.Turns),
                OwnerTeam = SunderingFacultyTeam,
                Source = v.Name,
            }, State.Log);
        }
        SunderingLog(v.Log, ("caster", SunderingCasterName(v.Id)), ("targets", SunderingList(named)),
                     ("damage", v.Damage.ToString()));
        return true;
    }

    /// <summary>Last Orders: the Keeper that fell most recently since the last Last Orders rises
    /// where it fell (or beside it) as a spirit on your side; then every spirit of yours beside a
    /// Keeper strikes it. With nothing fallen and no blow to strike, a plain bolt (D46).</summary>
    private async Task<bool> VolleyRaiseFallen(SunderingVolley v)
    {
        string caster = SunderingCasterName(v.Id);
        bool rose = false;
        for (int i = _sunderingFallen.Count - 1; i >= 0 && !rose; i--)
        {
            var tile = grid.GetTile(_sunderingFallen[i]);
            if (tile == null)
                continue;
            var risen = NecroEffectUtil.SpawnSpirit(State, "spirit", tile, 0, v.Health, v.Damage, v.Speed, v.Name);
            if (risen == null)
                continue;
            rose = true;
            CombatPresenter.EmitVolley(SunderingOrigin(v.Id), new[] { VisualEvent.RulesPositionOf(risen) },
                                       SunderingArchetype(v), v.Style);
            SunderingLog(v.Log, ("caster", caster));
        }
        _sunderingFallen.Clear();   // the next Last Orders reads only what falls after this one
        if (rose)
        {
            RefreshPlayerUnitBar();
            await SunderingAwaitPresenter();
        }

        // Her spirits strike: every spirit of yours standing beside a Keeper, at the weakest.
        int blows = 0;
        foreach (var spirit in playerUnits.Where(u => IsValidActor(u) && u.IsSpirit).ToList())
        {
            var keeper = SunderingKeepers()
                .Where(k => grid.Distance(spirit.CurrentTile.Axial, k.CurrentTile.Axial) <= 1)
                .OrderBy(k => k.Stats.Health)
                .FirstOrDefault();
            if (keeper == null)
                continue;
            int damage = spirit.ModifyOutgoingAttackDamage(spirit.AttackDamage);
            if (damage <= 0)
                continue;
            CombatPresenter.EmitStrike(spirit, keeper, Delivery.Melee);
            keeper.ApplyDamage(damage, spirit, Delivery.Melee);
            blows++;
        }
        if (blows > 0)
            SunderingLog(v.StrikeLog, ("caster", caster), ("count", blows.ToString()));
        if (rose || blows > 0)
            return true;

        // Nothing fell and no spirit stands beside a Keeper: Last Orders is a plain bolt.
        return VolleyBolt(v, v.FallbackLog);
    }

    /// <summary>Write That Down: the first time, a turret off the apparatus lands on the nearest
    /// free tile within reach of you, as your construct (D47); after that, or when it cannot land,
    /// the bolt at the Keeper nearest you.</summary>
    private bool VolleyTurretThenBolt(SunderingVolley v)
    {
        if (!_sunderingTurretLanded && State?.OnSummonRequested != null)
        {
            var tile = SunderingTurretTile();
            var turret = tile != null ? State.OnSummonRequested("turret", tile, 0) : null;
            if (turret != null)
            {
                _sunderingTurretLanded = true;
                CombatPresenter.EmitVolley(SunderingOrigin(v.Id), new[] { VisualEvent.RulesPositionOf(turret) },
                                           SunderingArchetype(v), v.Style);
                RefreshPlayerUnitBar();
                SunderingLog(v.Log, ("caster", SunderingCasterName(v.Id)));
                return true;
            }
        }
        return VolleyBolt(v, v.RepeatLog);
    }

    /// <summary>The nearest free floor to the candidate within the turret's reach, toward the
    /// Keepers on a tie; null when none is free.</summary>
    private TileData SunderingTurretTile()
    {
        var candidate = SunderingCandidate;
        if (candidate?.CurrentTile == null || grid == null)
            return null;
        var at = candidate.CurrentTile.Axial;
        var keepers = SunderingKeepers();
        TileData best = null;
        int bestDistance = int.MaxValue;
        int bestReach = int.MaxValue;
        foreach (var kv in grid.Tiles)
        {
            var tile = kv.Value;
            if (tile == null || !tile.IsWalkable || tile.IsBlocked || tile.IsOccupied)
                continue;
            int distance = grid.Distance(at, kv.Key);
            if (distance < 1 || distance > SunderingTurretReach)
                continue;
            int reach = keepers.Count == 0 ? 0 : keepers.Min(k => grid.Distance(kv.Key, k.CurrentTile.Axial));
            if (distance < bestDistance || (distance == bestDistance && reach < bestReach))
            {
                best = tile;
                bestDistance = distance;
                bestReach = reach;
            }
        }
        return best;
    }

    /// <summary>The volley's damage (or <paramref name="damage"/>) at the Keeper nearest the
    /// candidate: the Examiner's mark, and the bolt that Write That Down and Last Orders fall back
    /// to. <paramref name="log"/> takes {caster}, {target} and {damage}.</summary>
    private bool VolleyBolt(SunderingVolley v, string log, int damage = -1)
    {
        var target = SunderingNearestKeeper(SunderingKeepers());
        if (target == null)
            return false;
        int dealt = damage >= 0 ? damage : v.Damage;
        string name = target.DisplayName;
        SunderingHit(v, new List<Unit> { target }, dealt);
        SunderingLog(log, ("caster", SunderingCasterName(v.Id)), ("target", name), ("damage", dealt.ToString()));
        return true;
    }

    /// <summary>What they sat this morning: Count bolts of Damage at Keepers drawn at random, each
    /// from a different member of the class.</summary>
    private bool VolleyRandomBolts(SunderingVolley v)
    {
        var keepers = SunderingKeepers();
        if (keepers.Count == 0)
            return false;
        int fired = 0;
        for (int i = 0; i < Math.Max(1, v.Count); i++)
        {
            keepers.RemoveAll(k => !IsValidActor(k));
            if (keepers.Count == 0)
                break;
            var target = keepers[_sunderingRng.Next(keepers.Count)];
            CombatPresenter.EmitVolley(SunderingClassOrigin(), new[] { VisualEvent.RulesPositionOf(target) },
                                       SunderingArchetype(v), v.Style);
            target.ApplyDamage(v.Damage);
            fired++;
        }
        SunderingLog(v.Log, ("count", fired.ToString()), ("damage", v.Damage.ToString()));
        return fired > 0;
    }

    /// <summary>The volley's flight from its caster to each target, then the damage. The numbers
    /// wait for the flight in the presenter's queue, as a card's do.</summary>
    private void SunderingHit(SunderingVolley v, List<Unit> targets, int damage)
    {
        var points = new List<Vector3>();
        foreach (var t in targets)
            if (IsValidActor(t))
                points.Add(VisualEvent.RulesPositionOf(t));
        CombatPresenter.EmitVolley(SunderingOrigin(v.Id), points, SunderingArchetype(v), v.Style);
        if (damage <= 0)
            return;
        foreach (var t in targets)
            if (IsValidActor(t))
                t.ApplyDamage(damage);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The round boundary
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>StartEnemyTurn's hook, after the round has advanced and before the wave: the
    /// Provost adds a clause, the Keepers' counter-volley spends one figure, and the hall breaks.
    /// One moment, then the doors.</summary>
    private async Task SunderingBoundaryBeat()
    {
        if (!SunderingArmed || _sunderingClimaxStarted)
            return;
        int boundary = roundNumber;   // the round about to begin
        FrameSunderingSide();

        string clause = SunderingClause(boundary);
        if (clause.Length > 0)
            combatUI?.AppendActionLog($"The Provost reads on: \"{clause}\"");
        RefreshObjectiveBanner();

        string spent = SpendSunderingFigure();
        if (spent != null)
            await SunderingAwaitPresenter();   // the counter-volley lands, then they go down
        SunderingKneel(spent);
        SunderingBreaks(boundary);
        await SunderingPause(0.45f);
    }

    /// <summary>The next in the spend order still casting (never the traitor or the Examiner):
    /// the Keepers read one spell together from the east doors at it, and it casts no more.
    /// Returns its id, or null when nobody is left to spend.</summary>
    private string SpendSunderingFigure()
    {
        if (_sunderingSet?.SpendOrder == null)
            return null;
        foreach (var id in _sunderingSet.SpendOrder)
        {
            if (string.IsNullOrEmpty(id) || id == SunderingExaminerId || !SunderingCasts(id))
                continue;
            _sunderingSpent.Add(id);
            var targets = new List<Vector3>();
            if (id == SunderingClassId)
            {
                var standing = _sunderingClass.Where(f => f != null && IsInstanceValid(f)).ToList();
                int n = Math.Min(SunderingClassTargets, standing.Count);
                for (int i = 0; i < n; i++)
                    targets.Add(standing[i * standing.Count / n].GlobalPosition);
                SunderingLog(_sunderingSet.SpendClassLog);
            }
            else
            {
                if (_sunderingFigures.TryGetValue(id, out var archmage) && archmage != null && IsInstanceValid(archmage))
                    targets.Add(archmage.GlobalPosition);
                SunderingLog(_sunderingSet.SpendArchmageLog, ("archmage", SunderingCasterName(id)));
            }
            CombatPresenter.EmitVolley(SunderingDoorway(), targets,
                id == SunderingClassId ? VfxLibrary.ArchArc : VfxLibrary.ArchBolt, SunderingKeeperStyle);
            GD.Print($"[Sundering] The counter-volley spends {id}.");
            return id;
        }
        return null;
    }

    /// <summary>A spent figure goes down: an archmagus to one knee, the class behind the benches.</summary>
    private void SunderingKneel(string id)
    {
        var hall = SunderingHall;
        if (hall == null || string.IsNullOrEmpty(id))
            return;
        if (id == SunderingClassId)
        {
            foreach (var member in _sunderingClass)
                hall.Kneel(member, (float)_sunderingRng.NextDouble() * 0.25f, SunderingClassDrop);
        }
        else if (_sunderingFigures.TryGetValue(id, out var archmage))
        {
            hall.Kneel(archmage, 0f);
        }
    }

    /// <summary>The hall's breaks at a boundary (0 is the opening): visual only (D50).</summary>
    private void SunderingBreaks(int boundary)
    {
        if (_sunderingSet?.SetBeats == null)
            return;
        var hall = SunderingHall;
        foreach (var beat in _sunderingSet.SetBeats)
        {
            if (beat == null || beat.Boundary != boundary)
                continue;
            hall?.BreakPiece(beat.Piece);
            SunderingLog(beat.Log);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The climax
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>StartEnemyTurn's hook in place of the sixth round: "Step forward and be written."
    /// The candidate is drawn onto the mark, the spell leaves the traitor's hand down its line and
    /// stops an arm's length from the candidate's chest, and the fight is won (D43). The cast panel
    /// follows through the router, as before.</summary>
    private async void RunSunderingClimax()
    {
        if (_sunderingClimaxStarted)
            return;
        _sunderingClimaxStarted = true;
        GD.Print("[Sundering] The last clause is spoken. The climax.");
        combatUI?.SetPhaseText(SunderingLastClause);
        combatUI?.SetHintText("");
        FrameSunderingClimax();
        await SunderingAwaitPresenter();

        var hall = SunderingHall;
        var candidate = SunderingCandidate;
        bool present = candidate != null && IsInstanceValid(candidate) && candidate.IsInsideTree();

        // 1. The hold's last act draws the candidate off the floor and onto the mark.
        SunderingLog(_sunderingSet?.ClimaxDrawLog);
        if (hall != null && present)
        {
            Vector3 from = candidate.GlobalPosition;
            Vector3 to = hall.MarkPoint;
            var draw = CreateTween();
            draw.TweenMethod(Callable.From<float>(t =>
                {
                    if (IsInstanceValid(candidate))
                        candidate.GlobalPosition = from.Lerp(to, t)
                            + new Vector3(0f, SunderingDrawLift * 4f * t * (1f - t), 0f);
                }), 0f, 1f, SunderingDrawSeconds)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            await ToSignal(draw, Tween.SignalName.Finished);
        }
        await SunderingPause(0.5f);

        // 2. The traitor's hand comes up, and the spell leaves it, down the line it was drawn on.
        SunderingLog(_sunderingSet?.ClimaxSpellLog);
        if (SunderingTraitorAim(out var hand, out var lectern))
        {
            Vector3 chest = (present ? candidate.GlobalPosition : hall.MarkPoint) + new Vector3(0f, SunderingChest, 0f);
            Vector3 line = lectern - hand;
            float length = line.Length();
            Vector3 dir = length > 0.01f ? line / length : Vector3.Right;
            float along = Mathf.Clamp((chest - hand).Dot(dir) - SunderingArmLength, 0f, length);
            var spell = MakeSunderingSpell();
            spell.GlobalPosition = hand;
            var flash = new BurstVfx { Tint = UITheme.TextPrimary, Radius = 0.5f, GroundRing = false };
            AddChild(flash);
            flash.GlobalPosition = hand;
            _ = flash.Play(1f);
            var flight = CreateTween();
            flight.TweenProperty(spell, "global_position", hand + dir * along, SunderingSpellSeconds)
                  .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
            await ToSignal(flight, Tween.SignalName.Finished);
        }

        // 3. An arm's length from your chest, it stops (S2 makes the stillness). The fight is won.
        await SunderingPause(1.2f);
        await DrainTriggerStackAsync();
        _objectiveVictory = true;
        CheckCombatEnd();
        combatUI?.SetPhaseText(SunderingLastClause);
        combatUI?.SetHintText("");
    }

    /// <summary>The spell: a small white light that hangs where it stops.</summary>
    private Node3D MakeSunderingSpell()
    {
        var spell = new Node3D { Name = "SunderingSpell", TopLevel = true };
        spell.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.16f, Height = 0.32f, RadialSegments = 16, Rings = 8 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = UITheme.TextPrimary,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        spell.AddChild(new OmniLight3D
        {
            LightColor = UITheme.TextPrimary,
            LightEnergy = 1.6f,
            OmniRange = 3.2f,
            ShadowEnabled = false,
        });
        AddChild(spell);
        return spell;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The banner, the traitor's line, the camera
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The objective line: the label, then the sentence so far.</summary>
    private string SunderingBannerText()
    {
        string label = SunderingFill(_sunderingSet?.BannerLabel);
        if (label.Length == 0)
            label = _objective != null && !string.IsNullOrEmpty(_objective.Description) ? _objective.Description : "Survive";
        string clause = SunderingClause(roundNumber);
        return clause.Length > 0 ? $"{label}: \"{clause}\"" : label;
    }

    /// <summary>The objective line's hover text: what the sentence is and what it holds, then the
    /// traitor's spell (D42: the line itself cannot be hovered).</summary>
    private string SunderingBannerTooltip()
    {
        string about = SunderingFill(_sunderingSet?.BannerTooltip);
        string spell = SunderingHasTraitor ? SunderingFill(_sunderingSet?.TraitorLine) : "";
        if (about.Length > 0 && spell.Length > 0)
            return about + "\n\n" + spell;
        return about + spell;
    }

    /// <summary>The sentence as far as round <paramref name="round"/>, tokens filled.</summary>
    private string SunderingClause(int round)
    {
        var clauses = _sunderingScript?.Clauses;
        if (clauses == null || clauses.Count == 0)
            return "";
        return SunderingFill(clauses[Math.Clamp(round, 1, clauses.Count) - 1]);
    }

    /// <summary>AimLines' hook: the traitor's spell, from the hand at the sphere to the Provost at
    /// the lectern, once the stage is set. False when there is no traitor or no hall.</summary>
    private bool SunderingTraitorAim(out Vector3 from, out Vector3 to)
    {
        from = Vector3.Zero;
        to = Vector3.Zero;
        if (!SunderingArmed || !_sunderingStaged || !SunderingHasTraitor)
            return false;
        var hall = SunderingHall;
        if (hall == null)
            return false;
        from = hall.TraitorHandPoint;
        to = hall.ReaderPoint + new Vector3(0f, SunderingChest, 0f);
        return true;
    }

    /// <summary>The line of white fire (canon: "the spell in its line of white fire").</summary>
    private static Color SunderingSpellColor
    {
        get
        {
            var c = UITheme.TextPrimary;
            c.A = 0.9f;
            return c;
        }
    }

    private CameraController SunderingCamera =>
        CombatCamera != null && IsInstanceValid(CombatCamera)
            ? CombatCamera
            : GetNodeOrNull<CameraController>("../CameraController");

    /// <summary>The opening's framing: over the candidate's shoulder at the lectern, the mark and
    /// the dais, with the doors behind the camera.</summary>
    private void FrameSunderingDais(CameraController camera)
    {
        if (camera == null || grid == null)
            return;
        Vector3 min = grid.GridBoundsMin;
        Vector3 max = grid.GridBoundsMax;
        var candidate = SunderingCandidate;
        Vector3 center = candidate != null && candidate.IsInsideTree()
            ? candidate.GlobalPosition
            : new Vector3(Mathf.Lerp(min.X, max.X, 0.25f), max.Y, (min.Z + max.Z) * 0.5f);
        center.Y = max.Y;
        Vector3 toward = center + new Vector3(-1f, 0f, 0f);   // west: the lectern, the mark, the dais
        camera.FaceToward(center, toward);
        camera.SwingToward(center, toward, SunderingDaisZoom);
    }

    /// <summary>The fight's framing (D38): from over the north tiers looking south, the dais at the
    /// left and the doors at the right, the board across the screen. Swung to at the first turn,
    /// at each volley phase and at each boundary; the player may turn it in between.</summary>
    private void FrameSunderingSide()
    {
        if (!IsInstanceValid(this) || !SunderingOwnsCamera || _sunderingClimaxStarted || grid == null)
            return;
        var camera = SunderingCamera;
        if (camera == null)
            return;
        Vector3 min = grid.GridBoundsMin;
        Vector3 max = grid.GridBoundsMax;
        var center = new Vector3(Mathf.Lerp(min.X, max.X, SunderingSideAlong), max.Y, (min.Z + max.Z) * 0.5f);
        camera.SwingToward(center, center + new Vector3(0f, 0f, -1f), SunderingSideZoom);
    }

    /// <summary>The climax's framing: as far west as the rig may stand, still looking south, so the
    /// mark, the lectern and the traitor's line sit across the screen.</summary>
    private void FrameSunderingClimax()
    {
        if (!SunderingOwnsCamera || grid == null)
            return;
        var camera = SunderingCamera;
        if (camera == null)
            return;
        Vector3 min = grid.GridBoundsMin;
        Vector3 max = grid.GridBoundsMax;
        float x = min.X + Mathf.Min(camera.PanEdgeInset, (max.X - min.X) * 0.5f);
        var center = new Vector3(x, max.Y, (min.Z + max.Z) * 0.5f);
        camera.SwingToward(center, center + new Vector3(0f, 0f, -1f), SunderingClimaxZoom);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Every Keeper standing on the board.</summary>
    private List<Unit> SunderingKeepers() =>
        enemyUnits.Where(u => IsValidActor(u) && u.TeamId != 0 && !u.IsStructure).ToList();

    /// <summary>The candidate's tile, or the party's middle when the candidate has none.</summary>
    private Vector2I SunderingAnchor()
    {
        var candidate = SunderingCandidate;
        return candidate?.CurrentTile != null ? candidate.CurrentTile.Axial : ComputePlayerCentroid();
    }

    private Unit SunderingNearestKeeper(List<Unit> keepers)
    {
        if (keepers == null || keepers.Count == 0)
            return null;
        var anchor = SunderingAnchor();
        return keepers
            .OrderBy(k => grid.Distance(anchor, k.CurrentTile.Axial))
            .ThenBy(k => k.Stats.Health)
            .First();
    }

    /// <summary>Where a volley leaves from: its figure's chest, a member of the class, or the dais.</summary>
    private Vector3 SunderingOrigin(string id)
    {
        if (id == SunderingClassId)
            return SunderingClassOrigin();
        if (_sunderingFigures.TryGetValue(id ?? "", out var figure) && figure != null && IsInstanceValid(figure))
            return figure.GlobalPosition + new Vector3(0f, SunderingChest, 0f);
        var hall = SunderingHall;
        if (hall != null)
            return hall.DaisCenter + new Vector3(0f, 1.6f, 0f);
        return SunderingBoardEdge(west: true);
    }

    private Vector3 SunderingClassOrigin()
    {
        var standing = _sunderingClass.Where(f => f != null && IsInstanceValid(f)).ToList();
        if (standing.Count > 0)
            return standing[_sunderingRng.Next(standing.Count)].GlobalPosition + new Vector3(0f, SunderingChest, 0f);
        return SunderingBoardEdge(west: true);
    }

    /// <summary>The east doors, where the Keepers' spells come from.</summary>
    private Vector3 SunderingDoorway() => SunderingHall?.DoorwayPoint ?? SunderingBoardEdge(west: false);

    private Vector3 SunderingBoardEdge(bool west)
    {
        if (grid == null)
            return Vector3.Zero;
        Vector3 min = grid.GridBoundsMin;
        Vector3 max = grid.GridBoundsMax;
        return new Vector3(west ? min.X - 3f : max.X + 3f, max.Y + 2f, (min.Z + max.Z) * 0.5f);
    }

    /// <summary>The Keepers' palette: the opening strike's style, temporal when it names none.</summary>
    private string SunderingKeeperStyle
    {
        get
        {
            string style = _sunderingSet?.OpeningStrike?.Style;
            return string.IsNullOrEmpty(style) ? "temporal" : style;
        }
    }

    private static string SunderingArchetype(SunderingVolley v) =>
        string.IsNullOrEmpty(v?.Archetype) ? VfxLibrary.ArchBolt : v.Archetype;

    /// <summary>{caster} and {archmage}: an archmagus's full name, the Examiner or the class.</summary>
    private static string SunderingCasterName(string id)
    {
        if (id == SunderingExaminerId)
            return "The Examiner";
        if (id == SunderingClassId)
            return "The class";
        var def = ArchmageRegistry.Get(id);
        if (def == null)
            return id ?? "";
        return !string.IsNullOrEmpty(def.FullName) ? def.FullName : def.DisplayName;
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string SunderingList(List<Unit> units)
    {
        var names = units.Select(u => u.DisplayName).ToList();
        if (names.Count <= 1)
            return names.Count == 1 ? names[0] : "";
        return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
    }

    /// <summary>Fills a line's tokens: the arguments first, then the four globals
    /// (ChronicleScheduler.Token, with its fallbacks when no save is loaded).</summary>
    private static string SunderingFill(string text, params (string key, string value)[] args)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        foreach (var (key, value) in args)
            text = text.Replace("{" + key + "}", value ?? "");
        foreach (var name in SunderingGlobals)
        {
            string token = "{" + name + "}";
            if (text.Contains(token))
                text = text.Replace(token, ChronicleScheduler.Token(name) ?? SunderingGlobalFallback(name));
        }
        return text;
    }

    private static string SunderingGlobalFallback(string name) => name switch
    {
        "wizard_name" => "the candidate",
        "guild_name" => "the guild",
        "traitor_full" => "one of the archmagi",
        _ => "the traitor",
    };

    /// <summary>A filled line to the action log (and the console); nothing when it is empty.</summary>
    private void SunderingLog(string template, params (string key, string value)[] args)
    {
        string line = SunderingFill(template, args);
        if (line.Length == 0)
            return;
        GD.Print($"[Sundering] {line}");
        combatUI?.AppendActionLog(line);
    }

    private async Task SunderingPause(float seconds)
    {
        if (seconds > 0f && IsInsideTree())
            await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>Waits until the presenter has shown everything queued (capped), so each volley
    /// and each boundary is one beat the player can follow.</summary>
    private async Task SunderingAwaitPresenter()
    {
        float waited = 0f;
        while (IsInsideTree() && waited < SunderingPresenterCap)
        {
            var presenter = CombatPresenter.Current;
            if (presenter == null || !IsInstanceValid(presenter) || presenter.IsIdle)
                return;
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            waited += 0.05f;
        }
    }
}
