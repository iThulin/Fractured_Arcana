using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

// ============================================================
// CombatManager.Sundering.cs  (partial of CombatManager)
//
// Purpose:        The Sundering, rebuilt (S1,
//                 docs/sundering_long_second_founding_v1.md section 2),
//                 and how it ends: the Long Second and the walk out (S2,
//                 section 3).
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
//                  - the climax: the candidate is drawn onto the mark and
//                    the board is saved as it stands (the tableau, on the
//                    Eternal ledger). The cast panel shows over the hall,
//                    and its answer releases the spell, which leaves the
//                    hand and stops an arm's length away;
//                  - the freeze (S2): a phase of its own, Frozen. Every
//                    unit but the candidate, the presenter, every burst
//                    still open and the hall's set stop where they are,
//                    and the log counts the Keepers still standing. The
//                    Long Second and the Margin show over the frozen
//                    hall; then the candidate steps down from the mark
//                    and walks out, free, around the frozen Keepers. The
//                    mouse reads one line off whatever it rests on, and
//                    the east doors are the way out, to the city (P5).
//                 At the freeze the Godot output gets one timing line:
//                 how long the player's turns and each watched phase took.
//                 S2b, after the first play: the watched phases at the
//                 presenter's own pace, with longer holds at each beat;
//                 the hold shown each time it catches a blow (the
//                 candidate staggers and springs back up in a ring of the
//                 sentence's ink, and HELD rises over them); the
//                 Provost's clauses in a speech bubble over her, which
//                 the Long Second stops in the air; four breaches blown
//                 through the walls; and the hall at war from the
//                 opening to the freeze: stray spells crossing it, wild
//                 spells with every volley, lightning with the storm, and
//                 the camera shaken by the heaviest blows.
//                 Inert in every fight whose script has no sentence.
// Layer:          Combat / runtime
// Collaborators:  PrologueDirector (PrologueFightScript, the panels, the
//                 climax's result, the way out), SunderingScript (the
//                 volley set), HallSet.Sundering.cs (the points, the
//                 figures, the breaks, the stillness), Unit (HeldBySentence,
//                 SentenceHeld), CombatPresenter (EmitVolley, BaseSpeed),
//                 CameraController (SwingToward, AcceptInput), CombatUI
//                 (SetObjectiveTooltip, SetTurnControlsVisible),
//                 NarrativeEncounterPanel (the hall's panels),
//                 SpeechBubble (the Provost's words), ProjectileVfx and
//                 BurstVfx (the stray spells),
//                 LongSecondTableau (the save), CombatManager.cs (the
//                 hooks), CombatManager.Objectives.cs (ArmSundering, the
//                 banner), CombatManager.AimLines.cs (the traitor's line)
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
    private const float SunderingChest = HallSet.FigureChest;
    /// <summary>Where the spell stops short of the candidate's chest, along its line (the hall seen
    /// again hangs it at the same point: HallSet.SpellStopPoint).</summary>
    private const float SunderingArmLength = HallSet.SpellArmLength;
    private const float SunderingDrawLift = 0.35f;
    private const float SunderingDrawSeconds = 1.4f;
    private const float SunderingSpellSeconds = 0.9f;
    /// <summary>How far from the candidate Write That Down's turret may land.</summary>
    private const int SunderingTurretReach = 2;
    /// <summary>How far a spent member of the class sinks: down behind the benches.</summary>
    private const float SunderingClassDrop = HallSet.ClassDrop;
    /// <summary>The counter-volley at the tiers flies at this many of the class.</summary>
    private const int SunderingClassTargets = 6;
    /// <summary>The longest one beat waits on the presenter, in seconds.</summary>
    private const float SunderingPresenterCap = 6f;
    /// <summary>A Name the faculty writes belongs to no team on the board, so none of your own
    /// Enchanter effects (the Weave, The Price Compounds, The Seventh Name) reads it as yours.</summary>
    private const int SunderingFacultyTeam = -1;

    // ── The freeze and the way out (S2) ──────────────────────────────────
    /// <summary>Seconds of stillness between the freeze and the voice.</summary>
    private const float SunderingFreezeHold = 1.6f;
    private const float SunderingStepDownSeconds = 0.5f;
    /// <summary>Seconds a step takes on the way out (the presenter walks a tile in 0.14).</summary>
    private const float SunderingStepSeconds = 0.16f;
    private const float SunderingWalkZoom = 13f;
    /// <summary>Seconds on the doors' tiles before the hall lets you go.</summary>
    private const float SunderingExitPause = 0.3f;
    /// <summary>The hall's panels draw over the HUD (90) and under the pause menu (100).</summary>
    private const int SunderingPanelLayer = 95;
    private const string SunderingFrozenPhase = "The Long Second";
    private const string SunderingWalkPhase = "The Way Out";

    // ── The pace (S2b: the first play found the watched phases too quick to read) ──
    /// <summary>Seconds before the faculty's first volley, and between volleys.</summary>
    private const float SunderingVolleyLead = 0.6f;
    private const float SunderingVolleyGap = 0.4f;
    /// <summary>A boundary: the new clause in the bubble before the counter-volley, the spent figure
    /// down before the hall breaks, and the break before the doors.</summary>
    private const float SunderingClauseLead = 1.2f;
    private const float SunderingKneelHold = 0.7f;
    private const float SunderingBreakHold = 1.4f;
    /// <summary>After the draw onto the mark, before the cast panel.</summary>
    private const float SunderingClimaxHold = 0.9f;

    // ── The Provost's words (S2b) ─────────────────────────────────────────
    /// <summary>A clause stays in her bubble this long once it is all there.</summary>
    private const float SunderingClauseHold = 7f;
    /// <summary>Her head above her feet: where the bubble's tail points.</summary>
    private const float SunderingSpeakerHead = 2.1f;
    /// <summary>The bubble draws over the HUD (90) and under the hall's panels (95); the hold's gold
    /// edges draw under the HUD.</summary>
    private const int SunderingSpeechLayer = 91;
    private const int SunderingEdgeLayer = 89;
    /// <summary>The HUD panels (CombatUI's) the bubble keeps clear of.</summary>
    private static readonly string[] SunderingBubbleClear = { "LeftPanel", "PartyStrip" };

    // ── The hold, shown (S2b) ─────────────────────────────────────────────
    private const float SunderingHoldSeconds = 1.1f;
    /// <summary>A small hold this soon after the last is not shown again.</summary>
    private const ulong SunderingHoldGapMs = 450;
    /// <summary>The ring of ink: at most this many of the clause's letters, this far out.</summary>
    private const int SunderingInkLetters = 24;
    private const float SunderingInkRadius = 0.9f;
    private const float SunderingHeldHeight = 2.2f;
    /// <summary>The Register's hand, for the ink and HELD.</summary>
    private const string SunderingInkFontPath = "res://Assets/Fonts/Caladea-Bold.ttf";

    // ── The hall at war (S2b) ─────────────────────────────────────────────
    /// <summary>Seconds between stray spells: in the watched phases and the climax, and in your turns
    /// (each round a little less).</summary>
    private const float SunderingStrayHotMin = 0.25f;
    private const float SunderingStrayHotMax = 0.7f;
    private const float SunderingStrayCoolMin = 0.9f;
    private const float SunderingStrayCoolMax = 2.2f;
    /// <summary>Wild spells: with each faculty volley, with the class's, with a counter-volley, and as
    /// the traitor's spell leaves the hand; and the storm's lightning beyond its Keepers.</summary>
    private const int SunderingVolleyWild = 2;
    private const int SunderingClassWild = 5;
    private const int SunderingCounterWild = 8;
    private const int SunderingReleaseStrays = 8;
    private const int SunderingStormExtra = 3;
    /// <summary>The lightning falls from this high, this fast.</summary>
    private const float SunderingLightningDrop = 14f;
    private const float SunderingLightningSpeed = 90f;
    /// <summary>Camera shakes, in meters of lens: the opening strike, the round's first hold, a
    /// counter-volley landing, the storm, a breach, and a heavy stray.</summary>
    private const float SunderingStrikeShake = 0.25f;
    private const float SunderingHoldShake = 0.12f;
    private const float SunderingCounterShake = 0.3f;
    private const float SunderingStormShake = 0.15f;
    private const float SunderingBreachShake = 0.45f;
    private const float SunderingStrayShake = 0.05f;

    private const string SunderingClassId = "class";
    private const string SunderingExaminerId = "examiner";
    private const string SunderingLastClause = "The Last Clause";

    private static readonly string[] SunderingGlobals =
    {
        "wizard_name", "guild_name", "traitor_full", "traitor_short",
    };

    /// <summary>{count} in the freeze's line, as a word.</summary>
    private static readonly string[] SunderingCounts =
    {
        "none", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen", "twenty",
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

    // ── State of the climax, the freeze and the way out (S2) ─────────────
    /// <summary>Who the counter-volleys spent, in the order they went down (the tableau keeps it).</summary>
    private readonly List<string> _sunderingSpentOrder = new();
    /// <summary>Each tile's ground at the opening (LongSecondTableau.GroundKey), so the capture keeps
    /// every tile the fight changed (S4a).</summary>
    private readonly Dictionary<Vector2I, string> _sunderingFloorAtStart = new();
    private bool _sunderingReleased;
    private bool _sunderingLeaving;
    private CanvasLayer _hallPanelLayer;
    private NarrativeEncounterPanel _hallPanel;
    private bool _walkOn;
    private bool _walking;
    /// <summary>The doors are walled off by frozen Keepers: the way out is as far east as you can get.</summary>
    private bool _walkWalled;
    private Vector2I _walkAt;
    private readonly HashSet<Vector2I> _walkReach = new();
    private readonly HashSet<Vector2I> _walkExits = new();
    private Vector2I? _walkHover;
    private Unit _walkHoverUnit;
    private string _walkHint = "";
    // The timing line: real milliseconds by phase, from the opening to the freeze.
    private readonly Dictionary<string, ulong> _sunderingPhaseMs = new();
    private string _sunderingPhaseNow = "";
    private ulong _sunderingPhaseSince;
    private int _sunderingVolleyPhases;

    // ── State of the Provost's words, the hold shown and the hall at war (S2b) ──
    private Node3D _sunderingProvost;
    private CanvasLayer _sunderingSpeechLayer;
    private SpeechBubble _sunderingBubble;
    private CanvasLayer _sunderingEdgeLayer;
    private TextureRect _sunderingEdge;
    private ulong _sunderingHoldShownMs;
    private Vector3? _sunderingHoldRest;
    private Tween _sunderingStagger;
    private bool _sunderingChaosOn;
    private static Font _sunderingInkFont;

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
        _sunderingSpentOrder.Clear();
        _sunderingFloorAtStart.Clear();
        _sunderingReleased = false;
        _sunderingLeaving = false;
        _walkOn = false;
        _walking = false;
        _walkWalled = false;
        _walkReach.Clear();
        _walkExits.Clear();
        _walkHover = null;
        _walkHoverUnit = null;
        _walkHint = "";
        _sunderingPhaseMs.Clear();
        _sunderingPhaseNow = "";
        _sunderingPhaseSince = 0;
        _sunderingVolleyPhases = 0;
        _sunderingProvost = null;
        _sunderingHoldShownMs = 0;
        _sunderingHoldRest = null;
        _sunderingStagger = null;
        _sunderingChaosOn = false;
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

    /// <summary>Stands the set figures once (D39 to D41), as the hall stages them for the fight and
    /// for the hall seen again alike (HallSet.StageSundering): the loyal archmagi in front of their
    /// chairs, the traitor at the sphere, the Provost at the lectern facing the dais, the Examiner at
    /// the foot of the dais, and the class along both walks. Figures belong to the hall.</summary>
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
        var cast = hall.StageSundering(_sunderingTraitor, board);
        foreach (var kv in cast.Archmagi)
            _sunderingFigures[kv.Key] = kv.Value;
        _sunderingFigures[SunderingExaminerId] = cast.Examiner;
        _sunderingProvost = cast.Provost;
        _sunderingClass.AddRange(cast.Class);
        GD.Print($"[Sundering] Staged {_sunderingFigures.Count - 1} archmagi on the dais, the Examiner, the Provost"
                 + (cast.Traitor != null ? ", the traitor at the sphere" : "") + $" and {_sunderingClass.Count} of the class.");
    }

    /// <summary>The presenter's pace for the whole fight (the script's presenterSpeed).</summary>
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

    /// <summary>The hold kept the candidate standing. Every time it does, it shows (S2b): the
    /// candidate staggers and springs back up in a ring of the sentence's ink, the ink flares, and
    /// HELD rises over them. The first time in a round the log says so, the edges of the screen
    /// flush gold and the camera jolts. The hold itself never lapses.</summary>
    private void OnSentenceHeld(Unit unit)
    {
        if (!IsInstanceValid(this) || unit == null || !IsInstanceValid(unit))
            return;
        bool first = _sunderingHoldRound != roundNumber;
        if (first)
        {
            _sunderingHoldRound = roundNumber;
            SunderingLog(_sunderingSet?.HoldLog);
        }
        // ApplyDamage raises this before it queues the blow's own number, so the hold is queued at
        // the end of the frame: it plays right after the blow lands, not before it flies.
        Callable.From(() => QueueSunderingHold(unit, first)).CallDeferred();
    }

    /// <summary>The hold, in the presenter's queue behind the blow it caught (at once when there is
    /// no presenter).</summary>
    private void QueueSunderingHold(Unit unit, bool big)
    {
        if (!IsInstanceValid(this) || unit == null || !IsInstanceValid(unit))
            return;
        if (!CombatPresenter.EmitCallback(() => PlaySunderingHold(unit, big)))
            PlaySunderingHold(unit, big);
    }

    /// <summary>The hold, shown once. A small one too soon after the last is skipped, so a flurry of
    /// blows reads as one; the round's first always plays, and with it the gold edges and the jolt.</summary>
    private void PlaySunderingHold(Unit unit, bool big)
    {
        if (!IsInstanceValid(this) || unit == null || !IsInstanceValid(unit) || !unit.IsInsideTree()
            || currentPhase == CombatPhase.Frozen)
            return;
        ulong now = Time.GetTicksMsec();
        if (!big && now - _sunderingHoldShownMs < SunderingHoldGapMs)
            return;
        _sunderingHoldShownMs = now;
        SunderingStagger(unit);
        SunderingFlare(unit);
        SunderingInkRing(unit);
        SunderingHeldWord(unit);
        if (!big)
            return;
        SunderingEdgePulse();
        SunderingCamera?.Shake(SunderingHoldShake, 0.3f);
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

    /// <summary>The blow lands and the candidate stays up: squashed down onto their feet, then sprung
    /// back to their height (the whole unit, about its feet).</summary>
    private void SunderingStagger(Unit unit)
    {
        _sunderingHoldRest ??= unit.Scale;
        Vector3 rest = _sunderingHoldRest.Value;
        if (_sunderingStagger != null && _sunderingStagger.IsValid())
            _sunderingStagger.Kill();
        _sunderingStagger = unit.CreateTween();
        _sunderingStagger.TweenProperty(unit, "scale", new Vector3(rest.X * 1.16f, rest.Y * 0.72f, rest.Z * 1.16f), 0.07)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        _sunderingStagger.TweenProperty(unit, "scale", rest, 0.55)
            .SetTrans(Tween.TransitionType.Elastic).SetEase(Tween.EaseType.Out);
    }

    /// <summary>The sentence's ink round the candidate: the clause so far, its letters in a ring at the
    /// chest that opens out, turns and fades.</summary>
    private void SunderingInkRing(Unit unit)
    {
        string words = SunderingClause(roundNumber).Trim('.', ',', ' ');
        if (words.Length > SunderingInkLetters)
        {
            // Its last few words, whole.
            int cut = words.Length - SunderingInkLetters;
            int space = words.IndexOf(' ', cut);
            words = space >= 0 ? words.Substring(space + 1) : words.Substring(cut);
        }
        if (words.Length == 0)
            return;
        var ring = new Node3D { Name = "HeldInk", TopLevel = true };
        AddChild(ring);
        ring.GlobalPosition = unit.GlobalPosition + new Vector3(0f, SunderingChest, 0f);
        ring.Scale = Vector3.One * 0.45f;
        var font = SunderingInkFont();
        var tw = ring.CreateTween().SetParallel(true);
        for (int i = 0; i < words.Length; i++)
        {
            if (char.IsWhiteSpace(words[i]))
                continue;
            float a = Mathf.Tau * i / words.Length;
            var letter = new Label3D
            {
                Text = words[i].ToString(),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Shaded = false,
                DoubleSided = true,
                FontSize = 46,
                OutlineSize = 10,
                PixelSize = 0.0045f,
                Modulate = UITheme.Gold,
                OutlineModulate = UITheme.CardInk,
                Position = new Vector3(Mathf.Cos(a) * SunderingInkRadius, 0.12f * Mathf.Sin(a * 3f),
                                       Mathf.Sin(a) * SunderingInkRadius),
            };
            if (font != null)
                letter.Font = font;
            ring.AddChild(letter);
            tw.TweenProperty(letter, "modulate:a", 0f, 0.5).SetDelay(SunderingHoldSeconds - 0.5f);
            tw.TweenProperty(letter, "outline_modulate:a", 0f, 0.5).SetDelay(SunderingHoldSeconds - 0.5f);
        }
        tw.TweenProperty(ring, "scale", Vector3.One * 1.15f, SunderingHoldSeconds)
          .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(ring, "rotation:y", 1.4f, SunderingHoldSeconds)
          .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.Chain().TweenCallback(Callable.From(ring.QueueFree));
    }

    /// <summary>HELD, rising over the candidate as a blow's number does, in the sentence's gold.</summary>
    private void SunderingHeldWord(Unit unit)
    {
        string text = SunderingFill(_sunderingSet?.HoldWord);
        if (text.Length == 0)
            return;
        var word = new Label3D
        {
            Name = "HeldWord",
            Text = text,
            TopLevel = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Shaded = false,
            DoubleSided = true,
            FontSize = 60,
            OutlineSize = 16,
            PixelSize = 0.006f,
            Modulate = UITheme.Gold,
            OutlineModulate = UITheme.CardInk,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var font = SunderingInkFont();
        if (font != null)
            word.Font = font;
        AddChild(word);
        Vector3 at = unit.GlobalPosition + new Vector3(0f, SunderingHeldHeight, 0f);
        word.GlobalPosition = at;
        word.Scale = Vector3.One * 0.5f;
        var tw = word.CreateTween().SetParallel(true);
        tw.TweenProperty(word, "scale", Vector3.One, 0.2)
          .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(word, "global_position", at + new Vector3(0f, 0.6f, 0f), 1.1)
          .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(word, "modulate:a", 0f, 0.45).SetDelay(0.65);
        tw.TweenProperty(word, "outline_modulate:a", 0f, 0.45).SetDelay(0.65);
        tw.Chain().TweenCallback(Callable.From(word.QueueFree));
    }

    /// <summary>The edges of the screen flush the sentence's gold and fade.</summary>
    private void SunderingEdgePulse()
    {
        if (_sunderingEdge == null || !IsInstanceValid(_sunderingEdge))
        {
            _sunderingEdgeLayer = new CanvasLayer { Name = "HoldEdgeLayer", Layer = SunderingEdgeLayer };
            AddChild(_sunderingEdgeLayer);
            var ramp = new Gradient();
            ramp.Offsets = new float[] { 0f, 0.55f, 1f };
            ramp.Colors = new Color[] { new Color(UITheme.Gold, 0f), new Color(UITheme.Gold, 0f), new Color(UITheme.Gold, 0.55f) };
            _sunderingEdge = new TextureRect
            {
                Name = "HoldEdge",
                Texture = new GradientTexture2D
                {
                    Gradient = ramp,
                    Fill = GradientTexture2D.FillEnum.Radial,
                    FillFrom = new Vector2(0.5f, 0.5f),
                    FillTo = new Vector2(1.05f, 0.5f),
                    Width = 256,
                    Height = 256,
                },
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = Colors.Transparent,
            };
            _sunderingEdgeLayer.AddChild(_sunderingEdge);
            _sunderingEdge.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        }
        var tw = _sunderingEdge.CreateTween();
        tw.TweenProperty(_sunderingEdge, "modulate:a", 1f, 0.08);
        tw.TweenProperty(_sunderingEdge, "modulate:a", 0f, 0.7)
          .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
    }

    /// <summary>The Register's hand (Caladea Bold), for the ink and HELD; null for the default.</summary>
    private static Font SunderingInkFont()
    {
        if (_sunderingInkFont == null && ResourceLoader.Exists(SunderingInkFontPath))
            _sunderingInkFont = GD.Load<Font>(SunderingInkFontPath);
        return _sunderingInkFont;
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
        SunderingMark("opening");
        SnapSunderingFloor();   // before anyone acts: the floor the fight starts from
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
        SunderingSay(SunderingClause(1), SunderingClauseHold);   // she does not stop speaking (S2b)
        RunSunderingChaos();
        await SunderingBreaks(0);
        await SunderingPause(0.5f);

        // Their first spell, from the doors, between your shoulders (D37).
        var strike = _sunderingSet?.OpeningStrike;
        var candidate = SunderingCandidate;
        if (strike != null && strike.Damage > 0 && IsValidActor(candidate))
        {
            CombatPresenter.EmitVolley(SunderingDoorway(), new[] { VisualEvent.RulesPositionOf(candidate) },
                                       VfxLibrary.ArchBolt, SunderingKeeperStyle);
            SunderingImpactQueued(null, SunderingStrikeShake);   // S2b: it lands hard
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
        SunderingMark("turns");
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
        _sunderingVolleyPhases++;
        SunderingMark("faculty");
        currentPhase = CombatPhase.EnemyTurn;
        combatUI?.SetPhaseText($"Round {roundNumber} - The Faculty");
        combatUI?.SetHintText("The dais and the tiers answer.");
        WatchSunderingKeepers();
        FrameSunderingSide();
        await SunderingPause(SunderingVolleyLead);

        foreach (var volley in _sunderingSet.Volleys)
        {
            if (!IsInstanceValid(this) || currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat)
                return;
            if (volley == null || !SunderingCasts(volley.Id))
                continue;
            if (!await CastSunderingVolley(volley))
                continue;
            await SunderingAwaitPresenter();
            await SunderingPause(SunderingVolleyGap);
        }

        PruneDeadUnits();
        await DrainTriggerStackAsync();
        RefreshEnemyRoster();
        RefreshThreatTiles();
        RefreshAllUI();
        SunderingMark("keepers");   // the Keepers' phase follows at once
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
        SunderingLightningQueued(hit);   // S2b: lightning down on them as the storm breaks
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
            Vector3 origin = SunderingOrigin(v.Id);
            SunderingWildQueued(() => origin, SunderingVolleyWild, west: false, SunderingTint(v.Style));
            CombatPresenter.EmitVolley(origin, new[] { VisualEvent.RulesPositionOf(risen) },
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
        // S2b: the whole of the tiers casts, and most of it goes wide.
        SunderingWildQueued(SunderingClassOrigin, SunderingClassWild, west: false, SunderingTint(v.Style), 1.0f);
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
    /// wait for the flight in the presenter's queue, as a card's do. Wild spells leave with it (S2b),
    /// except from a storm, which brings its own lightning.</summary>
    private void SunderingHit(SunderingVolley v, List<Unit> targets, int damage)
    {
        var points = new List<Vector3>();
        foreach (var t in targets)
            if (IsValidActor(t))
                points.Add(VisualEvent.RulesPositionOf(t));
        Vector3 origin = SunderingOrigin(v.Id);
        if (SunderingArchetype(v) != VfxLibrary.ArchBurst)
            SunderingWildQueued(() => origin, SunderingVolleyWild, west: false, SunderingTint(v.Style));
        CombatPresenter.EmitVolley(origin, points, SunderingArchetype(v), v.Style);
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
    /// Provost reads the next clause (in her bubble, with a moment to read it), the Keepers'
    /// counter-volley spends one figure, it goes down, and the hall breaks. Then the doors.</summary>
    private async Task SunderingBoundaryBeat()
    {
        if (!SunderingArmed || _sunderingClimaxStarted)
            return;
        SunderingMark("boundaries");   // the beat and the wave behind it
        int boundary = roundNumber;   // the round about to begin
        FrameSunderingSide();

        string clause = SunderingClause(boundary);
        if (clause.Length > 0)
        {
            combatUI?.AppendActionLog($"The Provost reads on: \"{clause}\"");
            SunderingSay(clause, SunderingClauseHold);
        }
        RefreshObjectiveBanner();
        await SunderingPause(SunderingClauseLead);
        if (!IsInstanceValid(this))
            return;

        string spent = SpendSunderingFigure();
        if (spent != null)
            await SunderingAwaitPresenter();   // the counter-volley lands, then they go down
        SunderingKneel(spent);
        await SunderingPause(SunderingKneelHold);
        if (!IsInstanceValid(this))
            return;
        await SunderingBreaks(boundary);
        await SunderingPause(SunderingBreakHold);
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
            _sunderingSpentOrder.Add(id);
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
            // S2b: their spell flies in a storm of wild ones at the dais end, and the hall shakes as it lands.
            SunderingWildQueued(SunderingKeeperMuzzle, SunderingCounterWild, west: true,
                                SunderingTint(SunderingKeeperStyle), 0.9f);
            CombatPresenter.EmitVolley(SunderingDoorway(), targets,
                id == SunderingClassId ? VfxLibrary.ArchArc : VfxLibrary.ArchBolt, SunderingKeeperStyle);
            SunderingImpactQueued(targets, SunderingCounterShake);
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

    /// <summary>The hall's breaks at a boundary (0 is the opening): visual only (D50). A breach in
    /// the south wall is found first by one of their spells from the doors; the ones beside the doors
    /// come in from outside. Each breach shakes the hall (S2b).</summary>
    private async Task SunderingBreaks(int boundary)
    {
        if (_sunderingSet?.SetBeats == null)
            return;
        foreach (var beat in _sunderingSet.SetBeats)
        {
            if (beat == null || beat.Boundary != boundary)
                continue;
            var hall = SunderingHall;
            if (hall != null && HallSet.IsBreach(beat.Piece))
            {
                if (!HallSet.IsEastBreach(beat.Piece))
                    await SunderingBreachBolt(hall.PiecePoint(beat.Piece));
                if (!IsInstanceValid(this) || !IsInstanceValid(hall))
                    return;
                if (hall.BreakPiece(beat.Piece))
                    SunderingCamera?.Shake(SunderingBreachShake, 0.55f);
            }
            else
            {
                hall?.BreakPiece(beat.Piece);
            }
            SunderingLog(beat.Log);
        }
    }

    /// <summary>One of their spells from the doors, at the wall it is about to take out.</summary>
    private async Task SunderingBreachBolt(Vector3 at)
    {
        if (!IsInsideTree())
            return;
        var bolt = new ProjectileVfx { Name = "BreachBolt", Tint = SunderingTint(SunderingKeeperStyle) };
        AddChild(bolt);
        await bolt.Launch(SunderingDoorway(), at, 1f);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The climax
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>StartEnemyTurn's hook in place of the sixth round, after the last clause's enemy
    /// phase: "Step forward and be written." The board is saved as it stands (nothing on it moves
    /// again), the candidate is drawn onto the mark, and the cast panel shows over the hall (S2).
    /// Its answer releases the spell (ReleaseTheSpell). The fight is never won: it freezes.</summary>
    private async void RunSunderingClimax()
    {
        if (_sunderingClimaxStarted)
            return;
        _sunderingClimaxStarted = true;
        SunderingMark("climax");
        GD.Print("[Sundering] The last clause is spoken. The climax.");
        combatUI?.SetPhaseText(SunderingLastClause);
        combatUI?.SetHintText("");
        SunderingHideTurnControls();
        FrameSunderingClimax();
        await SunderingAwaitPresenter();
        await DrainTriggerStackAsync();
        if (!IsInstanceValid(this))
            return;

        // The tableau, saved now (S2, section 4): no one on the board acts again, and a quit from
        // here on resumes at the cast panel with it already on the ledger.
        CaptureSunderingTableau();
        PrologueDirector.NoteSunderingClimax(SaveManager.ActiveSave);

        var hall = SunderingHall;
        var candidate = SunderingCandidate;
        bool present = candidate != null && IsInstanceValid(candidate) && candidate.IsInsideTree();

        // "Step forward and be written": the hold's last act draws the candidate off the floor and
        // onto the mark.
        SunderingSay(SunderingFill(_sunderingSet?.ClimaxSay), 0f);
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
        await SunderingPause(SunderingClimaxHold);
        if (!IsInstanceValid(this))
            return;

        // The last clause, in the hall: its answer is the spell. Her words so far stay in the air.
        SunderingSay(SunderingClause(_sunderingScript.Clauses.Count), 0f);
        SunderingMark("reading");
        ShowHallPanel(PrologueDirector.SunderingCastPanelId, _ => ReleaseTheSpell());
    }

    /// <summary>The cast panel is answered: the traitor's hand comes up, and the spell leaves it,
    /// down the line it was drawn on all fight. As it flies the sphere breaks and the folio slips;
    /// an arm's length from the candidate's chest the spell stops, and the hall freezes with it. A
    /// beat of stillness, then the Long Second, then the Margin, then the way out.</summary>
    private async void ReleaseTheSpell()
    {
        if (_sunderingReleased || !IsInstanceValid(this))
            return;
        _sunderingReleased = true;
        SunderingMark("climax");
        var hall = SunderingHall;
        var candidate = SunderingCandidate;
        bool present = candidate != null && IsInstanceValid(candidate) && candidate.IsInsideTree();

        SunderingLog(_sunderingSet?.ClimaxSpellLog);
        hall?.CatchSphere(SunderingSpellSeconds);
        hall?.DropFolio(SunderingSpellSeconds);
        SunderingReleaseFlurry();   // S2b: both sides throw what they have left; the freeze catches it in the air
        if (SunderingTraitorAim(out var hand, out var lectern))
        {
            Vector3 chest = (present ? candidate.GlobalPosition : hall.MarkPoint) + new Vector3(0f, SunderingChest, 0f);
            Vector3 line = lectern - hand;
            float length = line.Length();
            Vector3 dir = length > 0.01f ? line / length : Vector3.Right;
            float along = Mathf.Clamp((chest - hand).Dot(dir) - SunderingArmLength, 0f, length);
            var spell = MakeSunderingSpell();
            spell.GlobalPosition = hand;
            // The flash at the hand is slow to open, so the freeze catches it half open.
            var flash = new BurstVfx
            {
                Tint = UITheme.TextPrimary,
                Radius = 0.5f,
                GroundRing = false,
                FlashDuration = SunderingSpellSeconds * 2f,
            };
            AddChild(flash);
            flash.GlobalPosition = hand;
            _ = flash.Play(1f);
            var flight = CreateTween();
            flight.TweenProperty(spell, "global_position", hand + dir * along, SunderingSpellSeconds)
                  .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
            await ToSignal(flight, Tween.SignalName.Finished);
        }
        else
        {
            await SunderingPause(SunderingSpellSeconds);
        }
        if (!IsInstanceValid(this))
            return;

        // It does not arrive.
        FreezeTheHall();
        await SunderingPause(SunderingFreezeHold);
        if (!IsInstanceValid(this))
            return;
        ShowHallPanel(PrologueDirector.LongSecondPanelId,
            first => ShowHallPanel(PrologueDirector.MarginPanelId, second =>
            {
                SunderingHall?.TakeRegister();
                BeginWalkOut();
            }));
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
    //  The freeze (S2)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The Long Second: the phase becomes Frozen, and everything but the candidate stops
    /// where it is. Every other unit stops processing (its tweens, animations and particles with it;
    /// its nameplate still follows the camera), as do the presenter and every burst or projectile
    /// still open, under this node and under the board, and the hall's set (HallSet.Still). Nothing
    /// is selected, aimed or threatened any more. The log counts the Keepers still standing, and the
    /// Godot output gets the fight's timing line.</summary>
    private void FreezeTheHall()
    {
        if (currentPhase == CombatPhase.Frozen)
            return;
        currentPhase = CombatPhase.Frozen;
        var candidate = SunderingCandidate;

        // Nothing is selected, aimed or threatened any more.
        DisarmAction(refresh: false);
        DisarmManeuver(refresh: false);
        foreach (var held in new[] { selectedUnit, inspectedEnemyUnit, candidate })
        {
            if (held == null || !IsInstanceValid(held))
                continue;
            held.SetSelected(false);
            held.SetDetailedBar(false);
        }
        if (_hoveredUnit != null && IsInstanceValid(_hoveredUnit))
            _hoveredUnit.SetHovered(false);
        selectedUnit = null;
        inspectedEnemyUnit = null;
        _hoveredUnit = null;
        ClearMoveTiles();
        _zoneRenderer?.Clear();
        foreach (var enemy in enemyUnits)
        {
            if (enemy == null || !IsInstanceValid(enemy))
                continue;
            enemy.CurrentIntent = null;
            enemy.ClearIntentDisplay();
        }
        RefreshThreatTiles();
        RefreshCoverMarkers();

        // Everyone else stops mid-step. Their plates keep following the camera.
        int stopped = 0;
        foreach (Node node in GetTree().GetNodesInGroup("units"))
        {
            if (node is not Unit unit || unit == candidate || !IsInstanceValid(unit))
                continue;
            unit.ProcessMode = ProcessModeEnum.Disabled;
            var plate = unit.GetNodeOrNull<HealthBarRoot>("HealthBarRoot");
            if (plate != null)
                plate.ProcessMode = ProcessModeEnum.Pausable;
            stopped++;
        }
        var presenter = CombatPresenter.Current;
        if (presenter != null && IsInstanceValid(presenter))
            presenter.ProcessMode = ProcessModeEnum.Disabled;
        SunderingStillUnder(this);
        if (grid != null && IsInstanceValid(grid))
            SunderingStillUnder(grid);
        SunderingHall?.Still();
        if (_sunderingBubble != null && IsInstanceValid(_sunderingBubble))
            _sunderingBubble.Freeze();   // her words stop in the air, an instant before the discipline

        SunderingHideTurnControls();
        combatUI?.SetPhaseText(SunderingFrozenPhase);
        combatUI?.SetHintText("");
        CombatTelemetry.EndFight(true, roundNumber);

        int standing = SunderingKeepers().Count;
        string count = standing == 0 ? _sunderingSet?.FreezeLogNone
            : standing == 1 ? _sunderingSet?.FreezeLogOne
            : _sunderingSet?.FreezeLog;
        SunderingLog(count, ("count", SunderingCountWord(standing)));
        GD.Print($"[Sundering] The Long Second: {stopped} unit(s) stopped where they stood, {standing} of them Keepers.");
        SunderingMark("");
        PrintSunderingTiming();
    }

    /// <summary>Stops every burst, projectile, particle system, animation player and tile overlay
    /// under <paramref name="root"/>, so whatever was open stays open. Units are left alone (they
    /// are stopped one by one, and the candidate keeps moving), and nothing else is touched: the
    /// board's tiles keep their colliders for the mouse, and the set keeps its cutaway.</summary>
    private static void SunderingStillUnder(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is Unit)
                continue;
            if (child is BurstVfx || child is ProjectileVfx || child is GpuParticles3D || child is CpuParticles3D
                || child is AnimationMixer || child is ImbuementOverlay)
            {
                child.ProcessMode = ProcessModeEnum.Disabled;
                continue;
            }
            SunderingStillUnder(child);
        }
    }

    /// <summary>The fight is over for the hand and the turn controls: from the last clause on there
    /// are no cards to play and no turns to end.</summary>
    private void SunderingHideTurnControls()
    {
        var deck = GetNodeOrNull<CanvasLayer>("../DeckUI");
        if (deck != null)
            deck.Visible = false;
        combatUI?.SetTurnControlsVisible(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The hall's panels (S2): the cast, the Long Second and the Margin
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Shows a prologue panel over the hall, as the hall screen would (its tokens, its
    /// gating, the Register's mode for it), with the camera held still, and on its answer applies
    /// the choice, saves, and hands the choice on. A missing panel closes the prologue as a full
    /// skip and leaves the hall, as the hall screen does.</summary>
    private void ShowHallPanel(string id, Action<EncounterChoice> then)
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
        {
            then?.Invoke(null);
            return;
        }
        RegisterManager.PrologueMode = PrologueDirector.RegisterModeFor(id);
        var panel = PrologueDirector.Panel(id, save);
        if (panel == null)
        {
            GD.PushError($"[Sundering] Panel {id} is missing; the prologue closes as a skip.");
            PrologueDirector.ForceComplete(save);
            LeaveTheHall();
            return;
        }
        var camera = SunderingCamera;
        if (camera != null)
            camera.AcceptInput = false;
        EnsureHallPanel();
        _hallPanel.OnCompleted = choice =>
        {
            var live = SaveManager.ActiveSave;
            if (live != null)
            {
                PrologueDirector.ApplyChoice(live, choice);
                SaveManager.SaveIfDirty();
            }
            then?.Invoke(choice);
        };
        _hallPanel.ShowEncounter(panel, save.HasFlag);
        GD.Print($"[Sundering] Over the hall: {id}.");
    }

    /// <summary>The hall's narrative panel: one, on a canvas layer of its own, made the first time.</summary>
    private void EnsureHallPanel()
    {
        if (_hallPanel != null && IsInstanceValid(_hallPanel))
            return;
        _hallPanelLayer = new CanvasLayer { Name = "HallPanelLayer", Layer = SunderingPanelLayer };
        AddChild(_hallPanelLayer);
        _hallPanel = new NarrativeEncounterPanel { Name = "HallPanel", Visible = false };
        _hallPanelLayer.AddChild(_hallPanel);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The way out (S2)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The Margin is answered: the candidate steps down from the mark onto the nearest free
    /// floor, and may walk anywhere the frozen Keepers leave open, free, with no action points. The
    /// east doors' tiles are the way out (drawn in gold); when frozen Keepers wall them off, the way
    /// out is as near the doors as the candidate can get, so the hall can never shut anyone in.</summary>
    private async void BeginWalkOut()
    {
        if (!IsInstanceValid(this) || _sunderingLeaving || _walkOn || _walking)
            return;
        var candidate = SunderingCandidate;
        if (candidate == null || !IsInstanceValid(candidate) || !candidate.IsInsideTree() || grid == null)
        {
            LeaveTheHall();
            return;
        }
        var hall = SunderingHall;
        var start = SunderingStepDownTile(candidate, hall != null ? hall.MarkPoint : candidate.GlobalPosition);
        if (start == null)
        {
            LeaveTheHall();
            return;
        }
        combatUI?.SetPhaseText(SunderingWalkPhase);
        combatUI?.SetHintText("");
        _walkAt = start.Axial;
        _walking = true;

        Vector3 to = start.TileView != null ? start.TileView.GlobalPosition : candidate.GlobalPosition;
        var camera = SunderingCamera;
        if (camera != null)
        {
            camera.AcceptInput = true;
            camera.SwingToward(to, to + Vector3.Right, SunderingWalkZoom);   // toward the doors
            camera.FocusOn(candidate);
        }
        var step = CreateTween();
        step.TweenProperty(candidate, "global_position", to, SunderingStepDownSeconds)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        await ToSignal(step, Tween.SignalName.Finished);
        if (!IsInstanceValid(this))
            return;
        _walking = false;

        SunderingFindReach(candidate);
        _walkHint = SunderingCapital(SunderingFill(_walkWalled ? _sunderingSet?.WalkBlocked : _sunderingSet?.WalkHint));
        combatUI?.SetHintText(_walkHint);
        if (_walkExits.Count > 0)
            _zoneRenderer?.ShowObjectiveZone(new HashSet<Vector2I>(_walkExits), grid);
        _walkOn = true;
        GD.Print($"[Sundering] The way out: {_walkReach.Count} tile(s) open, {_walkExits.Count} at the "
                 + (_walkWalled ? "nearest the doors you can get." : "doors."));
        if (_walkExits.Contains(_walkAt))
        {
            await SunderingPause(SunderingExitPause);
            LeaveTheHall();
        }
    }

    /// <summary>CombatManager._UnhandledInput's hook: false unless the hall is frozen. Frozen, it takes
    /// every event (no card, turn or unit control answers), and a left click on the floor walks the
    /// candidate there. The camera keeps its own input.</summary>
    private bool SunderingWalkInput(InputEvent e)
    {
        if (currentPhase != CombatPhase.Frozen)
            return false;
        if (_walkOn && !_walking && !_sunderingLeaving
            && e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            var at = GetHoveredTile();
            if (at.HasValue)
                WalkTo(at.Value);
        }
        return true;
    }

    /// <summary>Walks the candidate to <paramref name="target"/> by the shortest open way, a step at
    /// a time, the camera following; nothing on the way reacts (it is all stopped). On the doors'
    /// tiles, the hall lets the candidate go.</summary>
    private async void WalkTo(Vector2I target)
    {
        if (!_walkOn || _walking || _sunderingLeaving || grid == null)
            return;
        var candidate = SunderingCandidate;
        if (candidate == null || !IsInstanceValid(candidate))
        {
            LeaveTheHall();
            return;
        }
        var path = _walkReach.Contains(target) ? SunderingPath(_walkAt, target, candidate) : null;
        if (path == null)
        {
            var tile = grid.GetTile(target);
            if (tile != null && (tile.Occupant == null || tile.Occupant == candidate))
                combatUI?.SetHintText(SunderingCapital(SunderingFill(_sunderingSet?.WalkBlocked)));
            return;
        }

        _walking = true;
        SunderingClearWalkHover();
        _walkHover = null;
        combatUI?.SetHintText(_walkHint);
        var steps = new List<Vector3>();
        foreach (var at in path)
        {
            var view = grid.GetTileView(at);
            if (view != null)
                steps.Add(view.GlobalPosition);
        }
        if (steps.Count > 0)
        {
            var walk = CreateTween();
            foreach (var point in steps)
                walk.TweenProperty(candidate, "global_position", point, SunderingStepSeconds);
            SunderingCamera?.FocusOn(candidate);
            await ToSignal(walk, Tween.SignalName.Finished);
            if (!IsInstanceValid(this))
                return;
        }
        _walkAt = target;
        _walking = false;
        if (_walkExits.Contains(_walkAt))
        {
            await SunderingPause(SunderingExitPause);
            LeaveTheHall();
        }
    }

    /// <summary>Out through the east doors: the round trip is closed by hand (the fight never ends
    /// through the router, so nothing comes back to the hall screen), and the director takes the
    /// save to the city, where the Anchorhold waits (P5).</summary>
    private void LeaveTheHall()
    {
        if (_sunderingLeaving)
            return;
        _sunderingLeaving = true;
        _walkOn = false;
        SunderingClearWalkHover();
        _zoneRenderer?.ClearObjectiveZone();
        combatUI?.SetHintText("");
        var router = EncounterRouter.Instance;
        if (router != null)
        {
            router.HasPendingReturn = false;
            router.ReturnSceneOverride = "";
            router.SavedCombatFamilyKills = new Dictionary<string, int>();   // P-1: no Marginalia
        }
        EncounterContextCarrier.Clear();
        PrologueDirector.ClearScriptedSession();
        GD.Print("[Sundering] Out through the east doors.");
        PrologueDirector.ExitToWorld(GetTree());
    }

    /// <summary>_Process's hook while frozen: the tile under the mouse, read once each time it changes.
    /// A frozen unit there takes the hover ring and its full plate.</summary>
    private void SunderingWalkHover()
    {
        if (!_walkOn || _sunderingLeaving)
            return;
        Vector2I? at = _walking ? null : GetHoveredTile();
        if (at == _walkHover)
            return;
        _walkHover = at;
        SunderingClearWalkHover();
        string line = at.HasValue ? SunderingHoverLine(at.Value) : "";
        combatUI?.SetHintText(line.Length > 0 ? line : _walkHint);
    }

    /// <summary>One line for a tile of the frozen hall: whoever stands on it, else the way out, a
    /// memorial, or what lies on the ground. Empty when there is nothing to say.</summary>
    private string SunderingHoverLine(Vector2I at)
    {
        var tile = grid?.GetTile(at);
        if (tile == null)
            return "";
        var set = _sunderingSet;
        var unit = tile.Occupant;
        if (unit != null && IsInstanceValid(unit) && unit != SunderingCandidate && unit.Stats.IsAlive)
        {
            _walkHoverUnit = unit;
            unit.SetHovered(true);
            unit.SetDetailedBar(true);
            return unit.TeamId != 0
                ? SunderingCapital(SunderingFill(set?.HoverKeeper, ("name", SunderingArticled(SunderingLabel(unit)))))
                : SunderingCapital(SunderingFill(set?.HoverAlly, ("name", "your " + SunderingLabel(unit).ToLowerInvariant())));
        }
        if (_walkExits.Contains(at))
            return SunderingCapital(SunderingFill(_walkWalled ? set?.WalkBlocked : set?.HoverDoors));
        if (tile.Memorial != null)
        {
            string name = SunderingCleanName(tile.Memorial.SourceName);
            return SunderingCapital(SunderingFill(set?.HoverMemorial,
                ("name", tile.Memorial.WasAlly ? "your " + name.ToLowerInvariant() : SunderingArticled(name))));
        }
        string ground = SunderingGroundWord(tile);
        return ground.Length > 0 ? SunderingCapital(SunderingFill(set?.HoverGround, ("element", ground))) : "";
    }

    private void SunderingClearWalkHover()
    {
        if (_walkHoverUnit != null && IsInstanceValid(_walkHoverUnit))
        {
            _walkHoverUnit.SetHovered(false);
            _walkHoverUnit.SetDetailedBar(false);
        }
        _walkHoverUnit = null;
    }

    /// <summary>Floor the candidate can stand on in the frozen hall: walkable, not blocked, and no one
    /// else standing there (the candidate's last tile is free: they were drawn off it).</summary>
    private bool SunderingWalkable(TileData tile, Unit candidate)
    {
        if (tile == null || !tile.IsWalkable || tile.IsBlocked)
            return false;
        var occupant = tile.Occupant;
        return occupant == null || occupant == candidate || !IsInstanceValid(occupant) || !occupant.Stats.IsAlive;
    }

    /// <summary>The free floor nearest <paramref name="point"/> (the mark), or null when there is none.</summary>
    private TileData SunderingStepDownTile(Unit candidate, Vector3 point)
    {
        TileData best = null;
        float bestDistance = float.MaxValue;
        foreach (var kv in grid.Tiles)
        {
            var tile = kv.Value;
            if (tile?.TileView == null || !SunderingWalkable(tile, candidate))
                continue;
            Vector3 p = tile.TileView.GlobalPosition;
            float d = new Vector2(p.X - point.X, p.Z - point.Z).LengthSquared();
            if (d < bestDistance)
            {
                bestDistance = d;
                best = tile;
            }
        }
        return best;
    }

    /// <summary>Everywhere the candidate can walk from where they stand, and the way out: the reach's
    /// tiles in the board's east column, at the doors, or when frozen Keepers wall that off, the
    /// reach's tiles nearest it.</summary>
    private void SunderingFindReach(Unit candidate)
    {
        _walkReach.Clear();
        _walkExits.Clear();
        var queue = new Queue<Vector2I>();
        _walkReach.Add(_walkAt);
        queue.Enqueue(_walkAt);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var next in grid.GetNeighbors(at))
            {
                if (_walkReach.Contains(next) || !SunderingWalkable(grid.GetTile(next), candidate))
                    continue;
                _walkReach.Add(next);
                queue.Enqueue(next);
            }
        }
        int east = grid.Tiles.Count > 0 ? grid.Tiles.Keys.Max(k => k.X) : _walkAt.X;
        foreach (var at in _walkReach)
            if (at.X == east)
                _walkExits.Add(at);
        _walkWalled = _walkExits.Count == 0;
        if (!_walkWalled)
            return;
        int nearest = _walkReach.Max(k => k.X);
        foreach (var at in _walkReach)
            if (at.X == nearest)
                _walkExits.Add(at);
    }

    /// <summary>The shortest open way from <paramref name="from"/> to <paramref name="to"/>, without
    /// the first tile; empty when they are the same; null when there is none.</summary>
    private List<Vector2I> SunderingPath(Vector2I from, Vector2I to, Unit candidate)
    {
        var path = new List<Vector2I>();
        if (from == to)
            return path;
        var cameFrom = new Dictionary<Vector2I, Vector2I> { [from] = from };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(from);
        while (queue.Count > 0 && !cameFrom.ContainsKey(to))
        {
            var at = queue.Dequeue();
            foreach (var next in grid.GetNeighbors(at))
            {
                if (cameFrom.ContainsKey(next) || !SunderingWalkable(grid.GetTile(next), candidate))
                    continue;
                cameFrom[next] = at;
                queue.Enqueue(next);
            }
        }
        if (!cameFrom.ContainsKey(to))
            return null;
        for (var at = to; at != from; at = cameFrom[at])
            path.Add(at);
        path.Reverse();
        return path;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The tableau (S2, section 4)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Saves the board as the Long Second will hold it, on the Eternal ledger: every unit but
    /// the candidate (where it stands, which way it was turned, its health, statuses and Names), the
    /// floor (elements, reactions, growth, memorials, glyphs), the traitor and who the Keepers spent,
    /// in order. Called at the climax, after the last clause's enemy phase: nothing on the board acts
    /// after it.</summary>
    private void CaptureSunderingTableau()
    {
        var save = SaveManager.ActiveSave;
        if (!SunderingArmed || save?.Ledger == null || grid == null)
            return;
        var candidate = SunderingCandidate;
        var hall = SunderingHall;
        Vector3 mark = hall != null ? hall.MarkPoint
            : candidate != null && IsInstanceValid(candidate) ? candidate.GlobalPosition
            : (grid.GridBoundsMin + grid.GridBoundsMax) * 0.5f;
        var keepers = SunderingKeepers();
        var t = new LongSecondTableau
        {
            Captured = true,
            MapRecipe = grid.MapRecipeId ?? "",
            Clause = Math.Clamp(roundNumber, 1, Math.Max(1, _sunderingScript.Clauses.Count)),
            TraitorId = SunderingHasTraitor ? _sunderingTraitor : "",
            SpentFaculty = new List<string>(_sunderingSpentOrder),
        };

        foreach (Node node in GetTree().GetNodesInGroup("units"))
        {
            if (node is not Unit unit || unit == candidate || !IsValidActor(unit) || unit.IsDeathQueued
                || unit.IsStructure || unit.IsMapObject || unit.IsObjectiveWard)
                continue;
            bool keeper = unit.TeamId != 0;
            Vector3 at = unit.GlobalPosition;
            Vector3 look = keeper ? SunderingPlanPoint(unit) ?? mark : SunderingNearestKeeperPoint(keepers, at);
            Vector3 facing = look - at;
            facing.Y = 0f;
            t.Figures.Add(new TableauFigure
            {
                UnitId = !string.IsNullOrEmpty(unit.DefinitionId) ? unit.DefinitionId
                    : unit.IsSpirit ? "spirit"
                    : SunderingCleanId(unit.Name.ToString()),
                Team = unit.TeamId,
                Kind = keeper ? LongSecondTableau.KindKeeper
                    : unit.IsConstruct ? LongSecondTableau.KindConstruct
                    : unit.IsSpirit ? LongSecondTableau.KindSpirit
                    : LongSecondTableau.KindSummon,
                Q = unit.CurrentTile.Axial.X,
                R = unit.CurrentTile.Axial.Y,
                Facing = facing.LengthSquared() > 1e-4f ? Mathf.Atan2(facing.X, facing.Z) : 0f,
                Health = unit.Stats.Health,
                MaxHealth = unit.Stats.MaxHealth,
                Armor = unit.Stats.Armor,
                Shield = unit.Stats.Shield,
                Poise = unit.Poise,
                WitheredMaxHp = unit.Stats.WitheredMaxHp,
                Statuses = LongSecondTableau.StatusList(unit.Stats.StatusEffects,
                    unit.Names.Where(n => n != null && n.TurnsRemaining > 0).Select(n => n.TurnsRemaining)),
            });
        }

        // The floor (S4a): every tile with a mark, and every tile whose ground the fight changed
        // since the opening (all of them when there was no opening to compare with), each with all
        // of both.
        int marked = 0;
        foreach (var kv in grid.Tiles)
        {
            var tile = kv.Value;
            if (tile == null)
                continue;
            var row = SunderingTileRow(kv.Key, tile);
            bool changed = !_sunderingFloorAtStart.TryGetValue(kv.Key, out var before)
                           || before != LongSecondTableau.GroundKey(row);
            if (!row.HasMarks && !changed)
                continue;
            if (row.HasMarks)
                marked++;
            t.Tiles.Add(LongSecondTableau.Row(row));
        }

        save.Ledger.Tableau = t;
        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();
        int keeperCount = t.Figures.Count(f => f.Kind == LongSecondTableau.KindKeeper);
        GD.Print($"[Sundering] The tableau is on the ledger: {keeperCount} Keeper(s) and "
                 + $"{t.Figures.Count - keeperCount} of yours, {t.Tiles.Count} tile(s) of floor ({marked} marked, "
                 + $"{t.Tiles.Count - marked} with the ground changed alone), clause {t.Clause}, "
                 + $"spent [{string.Join(", ", t.SpentFaculty)}].");
    }

    /// <summary>The floor at the opening, before anyone acts: each tile's ground, for the capture to
    /// compare with (S4a).</summary>
    private void SnapSunderingFloor()
    {
        _sunderingFloorAtStart.Clear();
        if (grid == null)
            return;
        foreach (var kv in grid.Tiles)
            if (kv.Value != null)
                _sunderingFloorAtStart[kv.Key] = LongSecondTableau.GroundKey(SunderingTileRow(kv.Key, kv.Value));
    }

    /// <summary>A tile as the tableau keeps it: its marks (the names cleaned of their board numbers)
    /// and every detail of them, and its ground, with the scar its view shows.</summary>
    private static TableauTileRow SunderingTileRow(Vector2I at, TileData tile)
    {
        var memorial = tile.Memorial;
        var glyph = tile.Glyph != null && !tile.Glyph.Consumed ? tile.Glyph : null;
        return new TableauTileRow
        {
            At = at,
            Element = tile.ElementType != TileElementType.None ? tile.ElementType.ToString() : "",
            Reaction = tile.Reaction != ElementReaction.None ? tile.Reaction.ToString() : "",
            Growth = tile.GrowthStage,
            Memorial = memorial != null ? SunderingCleanName(memorial.SourceName) : "",
            Glyph = glyph != null ? SunderingGlyphName(glyph) : "",
            Full = true,
            Height = tile.Height,
            Terrain = tile.TerrainType.ToString(),
            Modifier = tile.TerrainModifier ?? "",
            Scar = tile.TileView?.TerrainScar ?? "",
            MoveCost = tile.MoveCost,
            BaseMoveCost = tile.BaseMoveCost,
            Hazardous = tile.IsHazardous,
            Walkable = tile.IsWalkable,
            Blocked = tile.IsBlocked,
            BlocksSight = tile.BlocksLineOfSight,
            Cover = tile.AuthoredCover.ToString(),
            Obstacle = tile.ObstacleKind ?? "",
            ObstacleHp = tile.ObstacleHp,
            Strength = tile.ElementStrength,
            ReactionRounds = tile.ReactionRounds,
            ReactionTeam = tile.ReactionTeam,
            GrowthAge = tile.GrowthAge,
            GrowthTeam = tile.GrowthOwner != null && IsInstanceValid(tile.GrowthOwner) ? tile.GrowthOwner.TeamId : -1,
            Carcass = tile.CarcassTicks,
            Discount = tile.SummonDiscount,
            DiscountTurns = tile.SummonDiscountTurns,
            MemorialState = memorial != null ? memorial.State.ToString() : "",
            MemorialStrength = memorial != null ? memorial.Strength.ToString() : "",
            MemorialAlly = memorial != null && memorial.WasAlly,
            MemorialTeam = memorial?.OwnerTeamId ?? 0,
            MemorialSourceTeam = memorial?.SourceTeamId ?? -1,
            MemorialTurns = memorial?.TurnsAlive ?? 0,
            GlyphCard = glyph?.SourceCardId ?? "",
            GlyphHalf = glyph?.SourceHalf ?? "",
            GlyphTeam = glyph?.OwnerTeam ?? 0,
            GlyphTrigger = glyph != null ? glyph.Trigger.ToString() : "",
            GlyphReflect = glyph?.ReflectCharges ?? 0,
        };
    }

    /// <summary>Where a Keeper's plan was aimed: its target's tile, or the tile it was going to take.
    /// Null when it had no plan left (the Keepers have just acted).</summary>
    private Vector3? SunderingPlanPoint(Unit keeper)
    {
        var intent = keeper?.CurrentIntent;
        if (intent == null)
            return null;
        if (intent.TargetUnit != null && IsInstanceValid(intent.TargetUnit) && intent.TargetUnit.IsInsideTree())
            return intent.TargetUnit.GlobalPosition;
        var view = intent.TargetTile.HasValue ? grid.GetTileView(intent.TargetTile.Value) : null;
        return view?.GlobalPosition;
    }

    /// <summary>The nearest Keeper to <paramref name="at"/>, or the doors' way when none stands.</summary>
    private static Vector3 SunderingNearestKeeperPoint(List<Unit> keepers, Vector3 at)
    {
        Vector3 best = at + Vector3.Right;
        float bestDistance = float.MaxValue;
        foreach (var k in keepers)
        {
            float d = k.GlobalPosition.DistanceSquaredTo(at);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = k.GlobalPosition;
            }
        }
        return best;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The timing line (S2: how long the fight took to play, and to watch)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Closes the phase being timed and opens <paramref name="phase"/> ("" closes only).
    /// Real time: the pause menu counts.</summary>
    private void SunderingMark(string phase)
    {
        if (!SunderingArmed)
            return;
        ulong now = Time.GetTicksMsec();
        if (_sunderingPhaseNow.Length > 0)
        {
            _sunderingPhaseMs.TryGetValue(_sunderingPhaseNow, out ulong sum);
            _sunderingPhaseMs[_sunderingPhaseNow] = sum + (now - _sunderingPhaseSince);
        }
        _sunderingPhaseNow = phase ?? "";
        _sunderingPhaseSince = now;
    }

    /// <summary>One line in the Godot output at the freeze: the player's turns against the phases
    /// the player watches (the faculty, the Keepers, the boundaries with their waves, the opening
    /// and the climax), and what a round takes to watch. The cast panel is reading, not counted.</summary>
    private void PrintSunderingTiming()
    {
        ulong turns = SunderingMs("turns");
        ulong faculty = SunderingMs("faculty");
        ulong keepers = SunderingMs("keepers");
        ulong boundaries = SunderingMs("boundaries");
        ulong opening = SunderingMs("opening");
        ulong climax = SunderingMs("climax");
        ulong reading = SunderingMs("reading");
        ulong watching = faculty + keepers + boundaries + opening + climax;
        ulong total = watching + turns;
        int rounds = Math.Max(1, _sunderingVolleyPhases);
        ulong perRound = (faculty + keepers + boundaries) / (ulong)rounds;
        int share = total > 0 ? (int)Math.Round(100.0 * watching / total) : 0;
        GD.Print($"[Sundering] Timing: {SunderingSpan(total)} from the opening to the freeze, {share}% of it watching. "
                 + $"Your turns {SunderingSpan(turns)}. The faculty {SunderingSpan(faculty)}, the Keepers "
                 + $"{SunderingSpan(keepers)}, the boundaries {SunderingSpan(boundaries)}: about "
                 + $"{SunderingSpan(perRound)} a round to watch, over {rounds} round(s). The opening "
                 + $"{SunderingSpan(opening)}, the climax {SunderingSpan(climax)}; the cast panel's "
                 + $"{SunderingSpan(reading)} is not counted.");
    }

    private ulong SunderingMs(string phase) => _sunderingPhaseMs.TryGetValue(phase, out ulong ms) ? ms : 0;

    /// <summary>Minutes and seconds, "4:07".</summary>
    private static string SunderingSpan(ulong ms)
    {
        ulong seconds = (ms + 500) / 1000;
        return (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":"
               + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The banner, the traitor's line, the camera
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The objective line: the label, then how far the sentence has come ("clause 2 of 5").
    /// The words themselves are in the Provost's bubble (S2b).</summary>
    private string SunderingBannerText()
    {
        string label = SunderingFill(_sunderingSet?.BannerLabel);
        if (label.Length == 0)
            label = _objective != null && !string.IsNullOrEmpty(_objective.Description) ? _objective.Description : "Survive";
        int clauses = _sunderingScript?.Clauses?.Count ?? 0;
        if (clauses == 0)
            return label;
        string progress = SunderingFill(_sunderingSet?.BannerProgress,
            ("clause", Math.Clamp(roundNumber, 1, clauses).ToString(CultureInfo.InvariantCulture)),
            ("clauses", clauses.ToString(CultureInfo.InvariantCulture)));
        return progress.Length > 0 ? $"{label}: {progress}" : label;
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
    //  The Provost's words (S2b)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The Provost says <paramref name="text"/> in a bubble over her, held
    /// <paramref name="hold"/> seconds once it is all there (0: until she says the next thing, or the
    /// hall stops). Nothing without her figure.</summary>
    private void SunderingSay(string text, float hold)
    {
        if (string.IsNullOrEmpty(text) || _sunderingProvost == null || !IsInstanceValid(_sunderingProvost))
            return;
        if (_sunderingBubble == null || !IsInstanceValid(_sunderingBubble))
        {
            _sunderingSpeechLayer = new CanvasLayer { Name = "SpeechLayer", Layer = SunderingSpeechLayer };
            AddChild(_sunderingSpeechLayer);
            _sunderingBubble = new SpeechBubble { Name = "ProvostBubble" };
            _sunderingSpeechLayer.AddChild(_sunderingBubble);
            foreach (string name in SunderingBubbleClear)
            {
                var panel = combatUI?.GetNodeOrNull<Control>(name);
                if (panel != null)
                    _sunderingBubble.KeepClearOf.Add(panel);
            }
        }
        _sunderingBubble.Speaker = _sunderingProvost;
        _sunderingBubble.HeadHeight = SunderingSpeakerHead;
        _sunderingBubble.Say(text, hold);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The hall at war (S2b): stray spells, wild spells, lightning, shakes
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>From the opening to the freeze, stray spells cross the hall between the beats: the
    /// Keepers' from the doors (now and then from one of them on the floor) at the dais end, the
    /// faculty's and the class's at the doors' end, each bursting on stone off the board. Visual only.
    /// Hot in the watched phases and the climax, cooler in your turns and while the cast panel is
    /// read, hotter each round; the Keepers' share grows as the faculty is spent. The pause menu holds
    /// it.</summary>
    private async void RunSunderingChaos()
    {
        if (_sunderingChaosOn || !SunderingArmed || SunderingHall == null)
            return;
        _sunderingChaosOn = true;
        try
        {
            while (IsInstanceValid(this) && IsInsideTree() && currentPhase != CombatPhase.Frozen && !_sunderingLeaving)
            {
                bool hot = currentPhase != CombatPhase.PlayerTurn && !SunderingPanelUp;
                float heat = Mathf.Clamp(1f - 0.08f * (roundNumber - 1), 0.6f, 1f);
                float wait = hot ? SunderingRandom(SunderingStrayHotMin, SunderingStrayHotMax)
                                 : SunderingRandom(SunderingStrayCoolMin, SunderingStrayCoolMax);
                await SunderingPause(wait * heat);
                if (!IsInstanceValid(this) || !IsInsideTree() || currentPhase == CombatPhase.Frozen || _sunderingLeaving)
                    break;
                if (!GetTree().Paused)
                    FireSunderingStray();
            }
        }
        finally
        {
            if (IsInstanceValid(this))
                _sunderingChaosOn = false;
        }
    }

    /// <summary>One of the hall's panels is up (the cast panel, before the spell).</summary>
    private bool SunderingPanelUp => _hallPanel != null && IsInstanceValid(_hallPanel) && _hallPanel.Visible;

    /// <summary>One stray spell, the Keepers' or the faculty's.</summary>
    private void FireSunderingStray()
    {
        var hall = SunderingHall;
        if (hall == null)
            return;
        var casting = SunderingStrayCasters();
        float keeperShare = Mathf.Clamp(0.45f + 0.07f * roundNumber, 0.45f, 0.85f);
        bool theirs = casting.Count == 0 || _sunderingRng.NextDouble() < keeperShare;
        bool lobbed = _sunderingRng.NextDouble() < 0.35;
        bool heavy = _sunderingRng.NextDouble() < 0.18;
        if (theirs)
        {
            SunderingStray(SunderingKeeperMuzzle(), hall.StrayTarget(_sunderingRng, west: true),
                           SunderingTint(SunderingKeeperStyle), lobbed, heavy);
            return;
        }
        var (origin, style) = casting[_sunderingRng.Next(casting.Count)];
        SunderingStray(origin, hall.StrayTarget(_sunderingRng, west: false), SunderingTint(style), lobbed, heavy);
    }

    /// <summary>Everyone on the dais and the tiers still casting: where each casts from, and its palette.</summary>
    private List<(Vector3 origin, string style)> SunderingStrayCasters()
    {
        var casters = new List<(Vector3 origin, string style)>();
        if (_sunderingSet?.Volleys == null)
            return casters;
        foreach (var v in _sunderingSet.Volleys)
            if (v != null && SunderingCasts(v.Id))
                casters.Add((SunderingOrigin(v.Id), v.Style));
        return casters;
    }

    /// <summary>Where a Keepers' stray leaves from: the doorway, or now and then one of them on the floor.</summary>
    private Vector3 SunderingKeeperMuzzle()
    {
        var keepers = SunderingKeepers();
        if (keepers.Count > 0 && _sunderingRng.NextDouble() < 0.35)
            return keepers[_sunderingRng.Next(keepers.Count)].GlobalPosition + new Vector3(0f, SunderingChest, 0f);
        return SunderingDoorway() + new Vector3(0.4f, SunderingRandom(-1.0f, 1.4f), SunderingRandom(-2.0f, 2.0f));
    }

    /// <summary>A stray spell from <paramref name="origin"/> to <paramref name="to"/>, bursting there with
    /// a puff of grit; a heavy one jolts the camera. It is this node's child, so the freeze stops it in
    /// the air. Nothing once the hall is still.</summary>
    private async void SunderingStray(Vector3 origin, Vector3 to, Color tint, bool lobbed, bool heavy,
                                      float speed = 0f, bool grit = true)
    {
        if (!IsInstanceValid(this) || !IsInsideTree() || currentPhase == CombatPhase.Frozen || _sunderingLeaving)
            return;
        var bolt = new ProjectileVfx { Name = "Stray", Tint = tint, Lobbed = lobbed };
        if (speed > 0f)
            bolt.BoltSpeed = speed;
        AddChild(bolt);
        await bolt.Launch(origin, to, 1f);
        if (!IsInstanceValid(this) || !IsInsideTree() || currentPhase == CombatPhase.Frozen)
            return;
        var burst = new BurstVfx { Tint = tint, Radius = heavy ? 0.9f : 0.5f, GroundRing = false };
        AddChild(burst);
        burst.GlobalPosition = to;
        _ = burst.Play(1f);
        if (grit)
            SunderingHall?.ImpactDust(to, heavy ? 1.2f : 0.6f);
        if (heavy)
            SunderingCamera?.Shake(SunderingStrayShake, 0.2f);
    }

    /// <summary>Wild spells with a volley: <paramref name="count"/> strays from <paramref name="origin"/>
    /// over <paramref name="over"/> seconds, set off when the presenter reaches this point in its queue,
    /// so they leave with the spell they belong to.</summary>
    private void SunderingWildQueued(Func<Vector3> origin, int count, bool west, Color tint, float over = 0.35f)
    {
        if (count <= 0 || SunderingHall == null)
            return;
        void Fire() => SunderingFlurry(origin, count, west, tint, over);
        if (!CombatPresenter.EmitCallback(Fire))
            Fire();
    }

    private void SunderingFlurry(Func<Vector3> origin, int count, bool west, Color tint, float over)
    {
        var hall = SunderingHall;
        if (hall == null || !IsInsideTree())
            return;
        for (int i = 0; i < count; i++)
        {
            Vector3 from = origin();
            Vector3 to = hall.StrayTarget(_sunderingRng, west);
            bool lobbed = _sunderingRng.NextDouble() < 0.3;
            float delay = count > 1 ? over * i / (count - 1) : 0f;
            if (delay <= 0.01f)
                SunderingStray(from, to, tint, lobbed, false);
            else
                GetTree().CreateTimer(delay).Timeout += () => SunderingStray(from, to, tint, lobbed, false);
        }
    }

    /// <summary>The First Argument's lightning: a bolt straight down on each Keeper the storm takes and a
    /// few on the floor round them, the moment it breaks; the hall shakes.</summary>
    private void SunderingLightningQueued(List<Unit> struck)
    {
        var points = new List<Vector3>();
        Vector3 middle = Vector3.Zero;
        int count = 0;
        foreach (var k in struck)
        {
            if (!IsValidActor(k))
                continue;
            Vector3 feet = VisualEvent.RulesPositionOf(k);
            points.Add(feet + new Vector3(0f, SunderingChest, 0f));
            middle += feet;
            count++;
        }
        if (count == 0)
            return;
        middle /= count;
        for (int i = 0; i < SunderingStormExtra; i++)
            points.Add(middle + new Vector3(SunderingRandom(-1.4f, 1.4f), 0.05f, SunderingRandom(-1.4f, 1.4f)));
        Color tint = SunderingTint("lightning");
        void Strike()
        {
            for (int n = 0; n < points.Count; n++)
            {
                Vector3 to = points[n];
                Vector3 from = to + new Vector3(SunderingRandom(-0.6f, 0.6f), SunderingLightningDrop, SunderingRandom(-0.6f, 0.6f));
                float delay = n * 0.06f;
                if (delay <= 0.01f)
                    SunderingStray(from, to, tint, false, false, SunderingLightningSpeed, grit: false);
                else
                    GetTree().CreateTimer(delay).Timeout += () =>
                        SunderingStray(from, to, tint, false, false, SunderingLightningSpeed, grit: false);
            }
            SunderingCamera?.Shake(SunderingStormShake, 0.35f);
        }
        if (!CombatPresenter.EmitCallback(Strike))
            Strike();
    }

    /// <summary>When the presenter reaches this point (a spell just landed): the hall shakes, and grit
    /// bursts where it struck.</summary>
    private void SunderingImpactQueued(List<Vector3> at, float shake)
    {
        void Land()
        {
            SunderingCamera?.Shake(shake, 0.45f);
            var hall = SunderingHall;
            if (hall == null || at == null)
                return;
            foreach (var p in at)
                hall.ImpactDust(p + new Vector3(0f, SunderingChest, 0f), 1.3f);
        }
        if (!CombatPresenter.EmitCallback(Land))
            Land();
    }

    /// <summary>As the spell leaves the hand, both sides throw what they have left: strays set off
    /// across the spell's flight, so the freeze catches most of them in the air.</summary>
    private void SunderingReleaseFlurry()
    {
        var hall = SunderingHall;
        if (hall == null || !IsInsideTree())
            return;
        var casting = SunderingStrayCasters();
        Color theirs = SunderingTint(SunderingKeeperStyle);
        for (int i = 0; i < SunderingReleaseStrays; i++)
        {
            Vector3 from;
            Vector3 to;
            Color tint;
            if (casting.Count == 0 || i % 2 == 0)
            {
                from = SunderingKeeperMuzzle();
                to = hall.StrayTarget(_sunderingRng, west: true);
                tint = theirs;
            }
            else
            {
                var (origin, style) = casting[_sunderingRng.Next(casting.Count)];
                from = origin;
                to = hall.StrayTarget(_sunderingRng, west: false);
                tint = SunderingTint(style);
            }
            bool lobbed = _sunderingRng.NextDouble() < 0.4;
            float delay = SunderingSpellSeconds * (0.1f + 0.75f * i / (SunderingReleaseStrays - 1));
            GetTree().CreateTimer(delay).Timeout += () => SunderingStray(from, to, tint, lobbed, false);
        }
    }

    /// <summary>A palette by VfxLibrary's style names ("temporal", "arcane", "lightning"...).</summary>
    private static Color SunderingTint(string style) => VfxLibrary.TintFor(new VisualEvent { VfxStyle = style ?? "" });

    private float SunderingRandom(float a, float b) => a + (b - a) * (float)_sunderingRng.NextDouble();

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
    /// (ChronicleScheduler.Token, with its fallbacks when no save is loaded). The hall seen again
    /// fills its words the same way (HallStage).</summary>
    internal static string SunderingFill(string text, params (string key, string value)[] args)
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

    /// <summary>The line with its first letter capitalized: a token can open it ("a Read-Ahead,
    /// stopped mid-step", "seven of them still stand").</summary>
    private static string SunderingCapital(string line)
    {
        if (string.IsNullOrEmpty(line) || !char.IsLower(line[0]))
            return line ?? "";
        return char.ToUpperInvariant(line[0]) + line.Substring(1);
    }

    private static string SunderingCountWord(int n) =>
        n >= 0 && n < SunderingCounts.Length ? SunderingCounts[n] : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a unit is called on its own: its kind's label (Read-Ahead, Keeper of the Seal),
    /// not the numbered name the board gives each one.</summary>
    private static string SunderingLabel(Unit unit)
    {
        if (!string.IsNullOrEmpty(unit.DefinitionId) && UnitRegistry.TryResolveId(unit.DefinitionId, out var id))
        {
            string label = UnitRegistry.Get(id)?.ThreatLabel;
            if (!string.IsNullOrEmpty(label))
                return label;
        }
        return SunderingCleanName(!string.IsNullOrEmpty(unit.DisplayName) ? unit.DisplayName : unit.Name.ToString());
    }

    /// <summary>"a Read-Ahead", "an Archivist", "the Foregone Conclusion".</summary>
    private static string SunderingArticled(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "something";
        if (name.StartsWith("The ", StringComparison.Ordinal))
            return "the " + name.Substring(4);
        return ("aeiouAEIOU".IndexOf(name[0]) >= 0 ? "an " : "a ") + name;
    }

    /// <summary>A board name without its number: "Read-Ahead_2" and "Turret2" read "Read-Ahead" and
    /// "Turret".</summary>
    private static string SunderingCleanName(string name)
    {
        string s = (name ?? "").Trim();
        int cut = s.Length;
        while (cut > 0 && char.IsDigit(s[cut - 1]))
            cut--;
        if (cut == 0)
            cut = s.Length;   // all digits: leave it be
        s = s.Substring(0, cut).TrimEnd('_', ' ').Replace('_', ' ').Trim();
        return s.Length > 0 ? s : "it";
    }

    /// <summary>A summon's kind as an id, for the tableau when it has no registry id: "turret".</summary>
    private static string SunderingCleanId(string name)
    {
        var chars = SunderingCleanName(name).ToLowerInvariant().Replace(' ', '_')
            .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_').ToArray();
        return chars.Length > 0 ? new string(chars) : "summon";
    }

    private static string SunderingGlyphName(GlyphData glyph)
    {
        if (!string.IsNullOrEmpty(glyph.SourceName))
            return glyph.SourceName;
        return !string.IsNullOrEmpty(glyph.SourceCardId) ? glyph.SourceCardId : "glyph";
    }

    /// <summary>{element} in the ground's hover line: the reaction on a tile, else its element, else
    /// its growth; empty for bare floor.</summary>
    private static string SunderingGroundWord(TileData tile)
    {
        switch (tile.Reaction)
        {
            case ElementReaction.Steam: return "steam";
            case ElementReaction.Fulgurite: return "fused stone";
            case ElementReaction.Magma: return "magma";
            case ElementReaction.Wildfire: return "wildfire";
            case ElementReaction.Brittle: return "brittle frost";
            case ElementReaction.Glacier: return "ice";
        }
        switch (tile.ElementType)
        {
            case TileElementType.Fire: return "fire";
            case TileElementType.Water: return "water";
            case TileElementType.Earth: return "earth";
            case TileElementType.Air: return "wind";
            case TileElementType.Arcane: return "arcane light";
            case TileElementType.Frost: return "frost";
            case TileElementType.Shadow: return "shadow";
            case TileElementType.Lightning: return "storm";
        }
        return tile.GrowthStage > 0 ? "growth" : "";
    }

    /// <summary>A filled line to the action log (and the console); nothing when it is empty.</summary>
    private void SunderingLog(string template, params (string key, string value)[] args)
    {
        string line = SunderingCapital(SunderingFill(template, args));
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
