using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// FoundingStage.cs
//
// Purpose:        The founding (S4, docs/sundering_long_second_founding_v1.md
//                 section 6): the guild founded in the story, in the Grand
//                 Hall, between the Anchorhold and the Door. The Anchorhold's
//                 "Walk to the Grand Hall." brings you here: the city and the
//                 campus send you while PrologueDirector.FoundingDue holds,
//                 and again after a quit.
//
//                 The Grand Hall as the exams build it (the fight's own
//                 scene without the fight, StageField), seen through a lens
//                 of its own. You stand inside the east doors as the guild's
//                 name cuts itself into the lintel over them, one letter at
//                 a time. At the west end you set the Register down on the
//                 lectern, and the Roll of Cycles takes its first line. The
//                 founding's panel shows over the hall; "Take the lectern."
//                 walks you round to where a reader stands, facing the room.
//                 Then the Door, the Register's next entry, and its "Go out."
//                 takes the lens down onto the Register's open page and out
//                 into the world, rising from the home city where the bastion
//                 waits (PlayerSession.ZoomFromHomeOnOpen).
//
//                 A save that made the founding's choice and then quit comes
//                 back to the founded hall, at the reader's place, for the
//                 Door. If the hall cannot be built, the founding and the
//                 prologue are written as done without it, so no save is ever
//                 sent round in a loop.
//
//                 S5: on the way out, a guild founded after the first
//                 lunation has been finished on this computer is asked
//                 whether to mark its steps done (FirstLunation).
//
//                 Stage direction (SD2, docs/stage_direction_v1.md section
//                 5): how the founding is shot and how you move is its
//                 direction scene, Scenes/Prologue/Direction/founding.tscn.
//                 The lens, your puppet and the fade are keyed in five
//                 sequences (open, west, take_lectern, door_wait, go_out),
//                 with cue keys for the hall's own beats (cut_name,
//                 register_down, roll_line) and for your model's clips
//                 (candidate:CLIP). This file decides when each sequence
//                 plays and performs the cues. S4's shots and timings are
//                 the built-in direction (DefaultDirections.Founding), which
//                 runs while the scene is missing and is what the bake writes.
//
//                 Rehearse the Founding (Debug Mode, PROLOGUE): the founding
//                 from its first beat on the current save, with its panels
//                 shown and nothing applied, then back to the city.
// Layer:          Prologue / presentation
// Collaborators:  StageField, HallSet (.GrandHall: the name, the Register,
//                 the Roll, the lectern; .Sundering: PlaceFigure),
//                 StageDirection, DefaultDirections, DirectionCues,
//                 PrologueDirector (the panels and the flags),
//                 NarrativeOutcome, NarrativeEncounterPanel, SceneTransition,
//                 PlayerSession, StrategicView and CampusScreen (the way in),
//                 FirstLunation (the skip)
// ============================================================

/// <summary>The founding in the Grand Hall (see the file header). The root of FoundingStage.tscn.</summary>
public partial class FoundingStage : Node3D
{
    public const string ScenePath = "res://Scenes/Prologue/FoundingStage.tscn";
    public const string WordsPath = "res://Data/Encounters/founding_stage.json";
    /// <summary>The Grand Hall as the exams build it. It is authored throughout, so any seed builds
    /// the same hall.</summary>
    private const string Recipe = "grand_hall";
    private const string PrologueCompleteFlag = "prologue_complete";

    // ── The direction (docs/stage_direction_v1.md section 5) ─────────────
    public const string CandidatePuppet = "Candidate";
    public const string Open = "open";
    public const string West = "west";
    public const string TakeLectern = "take_lectern";
    public const string DoorWait = "door_wait";
    public const string GoOutSequence = "go_out";
    public const string CueCutName = "cut_name";
    public const string CueRegisterDown = "register_down";
    public const string CueRollLine = "roll_line";
    /// <summary>A clip for your model: "candidate:walk" plays its walk, when it has one.</summary>
    public const string CueCandidateClip = "candidate:";

    /// <summary>What the founding needs of its direction (StageDirection checks it on load and in the assert).</summary>
    public static readonly StageDirection.Needs DirectionNeeds = new()
    {
        Id = StageDirection.FoundingId,
        Lens = true,
        Puppets = new[] { CandidatePuppet },
        Sequences = new[] { Open, West, TakeLectern, DoorWait, GoOutSequence },
        Cues = new[] { CueCutName, CueRegisterDown, CueRollLine, CueCandidateClip },
    };

    /// <summary>Frames to wait for the grid to build the hall before giving up.</summary>
    private const int BuildPatienceFrames = 300;
    /// <summary>Frames between building the opening shot and starting, so it has been drawn once.</summary>
    private const int SettleFrames = 2;
    private const int PanelLayer = 95;

    private static FoundingStageWords _words;
    /// <summary>Set by Rehearse for the next founding stage to open.</summary>
    private static bool _rehearseNext;

    private StageField _field;
    private HallSet _hall;
    private StageDirection _direction;
    private Node3D _candidate;
    private CanvasLayer _panelLayer;
    private NarrativeEncounterPanel _panel;
    private int _waited;
    private bool _built;
    private int _settle = -1;
    private bool _leaving;
    /// <summary>A rehearsal: the panels show and nothing is applied, and the way out is back to the city.</summary>
    private bool _rehearsing;
    /// <summary>Back after the founding's choice: at the reader's place, for the Door.</summary>
    private bool _founded;

    // ════════════════════════════════════════════════════════════════════════
    //  The way in, and the words
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The founding waits in the Grand Hall (PrologueDirector.FoundingDue).</summary>
    public static bool Due(GuildSaveData save) => PrologueDirector.FoundingDue(save);

    /// <summary>Into the Grand Hall, behind the curtain. A host that polls asks again while the curtain
    /// is already going down; that is passed over quietly.</summary>
    public static void Enter(SceneTree tree)
    {
        if (SceneTransition.IsTransitioning)
            return;
        var words = Words();
        SceneTransition.Go(tree, ScenePath, Fill(words.EnterTitle), Fill(words.EnterSubtitle));
    }

    /// <summary>Rehearse the Founding (Debug Mode, PROLOGUE): the founding from its first beat on the current
    /// save, with its panels shown and nothing applied, then back to the city. For watching a change to its
    /// direction scene without founding a new guild.</summary>
    public static void Rehearse(SceneTree tree)
    {
        if (tree == null || SceneTransition.IsTransitioning || SaveManager.ActiveSave?.Ledger == null)
            return;
        _rehearseNext = true;
        SceneTransition.Go(tree, ScenePath, Fill(Words().EnterTitle), "A rehearsal: nothing you choose here is kept.");
    }

    /// <summary>The stage's words (WordsPath), read once. Empty words when the file is missing or does
    /// not read; the assert names that (WordsFailures).</summary>
    public static FoundingStageWords Words()
    {
        if (_words != null)
            return _words;
        _words = new FoundingStageWords();
        if (!FileAccess.FileExists(WordsPath))
        {
            GD.PushError($"[FoundingStage] {WordsPath} is missing.");
            return _words;
        }
        try
        {
            using var f = FileAccess.Open(WordsPath, FileAccess.ModeFlags.Read);
            if (f != null)
                _words = JsonSerializer.Deserialize<FoundingStageWords>(f.GetAsText(), SaveManager.JsonOptions)
                         ?? new FoundingStageWords();
        }
        catch (Exception e)
        {
            GD.PrintErr($"[FoundingStage] error reading {WordsPath}: {e.Message}");
            _words = new FoundingStageWords();
        }
        return _words;
    }

    /// <summary>The assert's check (PrologueDirector.AssertRoundTrip): the words read, and both
    /// curtains have a title. Empty when it passes.</summary>
    public static List<string> WordsFailures()
    {
        var fails = new List<string>();
        _words = null;   // read the file as it is now
        var w = Words();
        if (string.IsNullOrEmpty(w.EnterTitle) || string.IsNullOrEmpty(w.LeaveTitle))
            fails.Add($"the founding stage's words ({WordsPath}) have no title for the way in or the way out (enterTitle, leaveTitle)");
        return fails;
    }

    /// <summary>A line with its tokens filled, as the Sundering fills its own.</summary>
    private static string Fill(string text) => CombatManager.SunderingFill(text);

    private static string Capital(string text) =>
        text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;

    // ════════════════════════════════════════════════════════════════════════
    //  The scene
    // ════════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        _rehearsing = _rehearseNext;
        _rehearseNext = false;
        if (SaveManager.ActiveSave?.Ledger == null)
        {
            GD.PushWarning("[FoundingStage] No save is loaded: back to the city.");
            CallDeferred(nameof(BackToTheCity));
            return;
        }
        _field = StageField.Build(this, Recipe, 1, "FoundingStage");
        if (_field == null)
        {
            CallDeferred(nameof(FailDeferred));
            return;
        }
        if (_field.Camera != null && IsInstanceValid(_field.Camera))
            _field.Camera.AcceptInput = false;   // the founding has a lens of its own: its direction's
        BuildUi();
    }

    public override void _Process(double delta)
    {
        if (_leaving)
            return;
        if (!_built)
        {
            var hall = _field?.FindHall(h => h.IsGrandHall);
            if (hall != null)
            {
                Build(hall);
                return;
            }
            if (++_waited > BuildPatienceFrames)
                Fail($"the grid built no Grand Hall in {BuildPatienceFrames} frames");
            return;
        }
        var lens = _direction?.Lens;
        if (lens != null && IsInstanceValid(lens) && !lens.Current)
            lens.MakeCurrent();
        if (_settle > 0 && --_settle == 0)
            Begin();
    }

    /// <summary>The direction, at the hall's origin, and you in it: as the founding finds the hall (the name
    /// not cut, the Register not set down, you inside the doors), or, back after the founding's choice, the
    /// founded hall with you at the reader's place.</summary>
    private void Build(HallSet hall)
    {
        _built = true;
        _hall = hall;
        _direction = StageDirection.Load(StageDirection.FoundingId, this, hall.SetOrigin,
                                         () => DefaultDirections.Founding(hall), DirectionNeeds);
        if (_direction == null)
        {
            Fail("the founding's direction did not load");
            return;
        }
        _direction.Cued += OnCue;
        _direction.Lens?.MakeCurrent();
        _founded = !_rehearsing && PrologueDirector.Founded(SaveManager.ActiveSave);
        if (!_founded)
            hall.HideFoundingPieces();
        _candidate = hall.PlaceFigure("candidate", hall.GrandDoorsPoint, Vector3.Right, HallSet.ClassRobe);
        _direction.Bind(CandidatePuppet, _candidate);
        if (_founded)
            _direction.Pose(TakeLectern, atEnd: true);   // the reader's place, facing the room
        else
            _direction.Pose(Open, atEnd: false);         // the opening shot, drawn once before it starts
        _settle = SettleFrames;
        GD.Print("[FoundingStage] The Grand Hall, " + (_founded ? "founded: on to the Door."
                 : _rehearsing ? "for a rehearsal of the founding: its panels show and nothing is applied."
                 : "for the founding."));
    }

    /// <summary>The founding, sequence by sequence; or, back after its choice, the Door.</summary>
    private async void Begin()
    {
        if (_founded)
        {
            await _direction.Play(DoorWait);
            if (!Gone)
                ShowDoor();
            return;
        }
        await _direction.Play(Open);
        if (Gone)
            return;
        await _direction.Play(West);
        if (!Gone)
            ShowFounding();
    }

    /// <summary>The founding's panel. Its one choice ("Take the lectern.") writes the guild founded
    /// (the director adds the flag) and walks you round to the reader's place.</summary>
    private void ShowFounding()
    {
        var enc = PrologueDirector.Panel(PrologueDirector.FoundingPanelId, SaveManager.ActiveSave);
        if (enc == null)
        {
            GD.PushError("[FoundingStage] The founding's panel is missing: the guild is founded without it.");
            if (!_rehearsing)
                NarrativeOutcome.Apply(null, Flags(PrologueDirector.FoundingDoneFlag));
            TakeTheLectern();
            return;
        }
        ShowPanel(enc, TakeTheLectern);
    }

    /// <summary>"Take the lectern.": round it to where a reader stands, then turned to face the room,
    /// the lens moving with you to see you from the room. Then the Door.</summary>
    private async void TakeTheLectern()
    {
        if (Gone)
            return;
        await _direction.Play(TakeLectern);
        if (Gone)
            return;
        await _direction.Play(DoorWait);
        if (!Gone)
            ShowDoor();
    }

    /// <summary>The Door, the Register's next entry. Its choice ("Go out.") completes the prologue.</summary>
    private void ShowDoor()
    {
        var enc = PrologueDirector.Panel(PrologueDirector.DoorPanelId, SaveManager.ActiveSave);
        if (enc == null)
        {
            GD.PushError("[FoundingStage] The Door's panel is missing: the prologue completes without it.");
            if (!_rehearsing)
                NarrativeOutcome.Apply(null, Flags(PrologueCompleteFlag));
            GoOut();
            return;
        }
        ShowPanel(enc, GoOut);
    }

    /// <summary>"The camera rises off the Register's open page into the world map": the lens comes
    /// down onto the page, and the world opens behind the curtain, rising from the home city.</summary>
    private async void GoOut()
    {
        if (Gone)
            return;
        await _direction.Play(GoOutSequence);
        if (Gone)
            return;
        if (_rehearsing)
        {
            BackToTheCity();
            return;
        }
        // S5: a guild founded after the first lunation was finished on this computer may skip its steps.
        if (FirstLunation.SkipOffered(SaveManager.ActiveSave))
        {
            OfferFirstLunationSkip();
            return;
        }
        Leave();
    }

    /// <summary>S5: the first lunation's skip, a system question outside the fiction like the prologue's
    /// own (PrologueScreen.OfferSkips). Either answer goes out to the world. The cancel button, Escape and
    /// the close box all count as "Walk it again"; whichever answer arrives first wins.</summary>
    private void OfferFirstLunationSkip()
    {
        bool answered = false;
        var dialog = new ConfirmationDialog
        {
            Title = "Skip the first lunation?",
            DialogText = "You have finished the first lunation before on this computer. Mark its steps done? "
                         + "The top bar's Next line and the quest log will not walk you through it again.",
            OkButtonText = "Mark them done",
            CancelButtonText = "Walk it again",
        };
        dialog.Confirmed += () =>
        {
            if (answered)
                return;
            answered = true;
            dialog.QueueFree();
            FirstLunation.Skip(SaveManager.ActiveSave);
            SaveManager.SaveIfDirty();
            Leave();
        };
        dialog.Canceled += () =>
        {
            if (answered)
                return;
            answered = true;
            dialog.Hide();   // frees the exclusive slot now, as the prologue's Ask does
            dialog.QueueFree();
            Leave();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>A cue from the direction (DirectionCues): the hall's own beats, and your model's clips.</summary>
    private void OnCue(string id, float seconds)
    {
        if (Gone)
            return;
        switch (id)
        {
            case CueCutName:
                _hall.CutGuildName(seconds > 0f ? seconds : DefaultDirections.NameSeconds);
                break;
            case CueRegisterDown:
                _hall.SetRegisterDown(seconds);
                break;
            case CueRollLine:
                _hall.WriteRollLine(SaveManager.ActiveSave, seconds);   // drawn here; the Roll's own record is the save's
                break;
            default:
                if (id.StartsWith(CueCandidateClip, StringComparison.Ordinal) && id.Length > CueCandidateClip.Length)
                    StageDirection.PlayClip(_candidate, id.Substring(CueCandidateClip.Length));
                else
                    _direction?.UnknownCue(id);
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The panels, and the ways out
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The panels' layer. The fade is the direction's (FadeLayer).</summary>
    private void BuildUi()
    {
        _panelLayer = new CanvasLayer { Name = "PanelLayer", Layer = PanelLayer };
        AddChild(_panelLayer);
        _panel = new NarrativeEncounterPanel { Name = "FoundingPanel", Visible = false };
        _panelLayer.AddChild(_panel);
    }

    /// <summary>A prologue panel over the hall, applied as the city applies them (NarrativeOutcome), but not in a
    /// rehearsal; <paramref name="next"/> runs once it is answered.</summary>
    private void ShowPanel(NarrativeEncounterData enc, Action next)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || _panel == null || !IsInstanceValid(_panel))
            return;
        _panel.Visible = true;
        _panel.ShowEncounter(enc, save.HasFlag, save.Cycle?.SelectedSchool, save.Gold, save.Cycle?.Campaign,
            hasItem: id => save.Armory.OwnedItems.Exists(i => i.DefinitionId == id),
            hasCompanion: id => CompanionRoster.GetActiveParty().Exists(c => c.Id == id));
        _panel.OnCompleted = choice =>
        {
            if (_panel != null && IsInstanceValid(_panel))
                _panel.Visible = false;
            if (!_rehearsing)
                NarrativeOutcome.Apply(enc, choice);
            next?.Invoke();
        };
        GD.Print($"[FoundingStage] Over the hall: {enc.Id}" + (_rehearsing ? " (a rehearsal: not applied)." : "."));
    }

    /// <summary>A choice that only sets meta flags: what a missing panel's choice would have set.</summary>
    private static EncounterChoice Flags(params string[] flags) => new() { SetMetaFlags = new List<string>(flags) };

    /// <summary>Out to the world, rising from the home city, behind the curtain.</summary>
    private void Leave()
    {
        if (_leaving)
            return;
        _leaving = true;
        PlayerSession.StartInCityOnOpen = false;
        PlayerSession.ZoomFromHomeOnOpen = true;
        var words = Words();
        SceneTransition.Go(GetTree(), PrologueDirector.StrategicScenePath, Capital(Fill(words.LeaveTitle)),
                           Fill(words.LeaveSubtitle));
    }

    /// <summary>Back to the city: with no save there is nothing to found, and a rehearsal ends there.</summary>
    private void BackToTheCity()
    {
        if (_leaving)
            return;
        _leaving = true;
        PlayerSession.StartInCityOnOpen = true;
        SceneTransition.Go(GetTree(), PrologueDirector.StrategicScenePath, Capital(Fill(Words().LeaveTitle)));
    }

    private void FailDeferred() => Fail("the battlefield did not build");

    /// <summary>The hall cannot be built: the founding and the prologue are written as done without it,
    /// so the city does not send the save back here in a loop, and the world opens. A rehearsal writes
    /// nothing, and ends in the city.</summary>
    private void Fail(string why)
    {
        if (_leaving)
            return;
        if (_rehearsing)
        {
            GD.PushError($"[FoundingStage] {why}: the rehearsal ends.");
            BackToTheCity();
            return;
        }
        GD.PushError($"[FoundingStage] {why}: the guild is founded without its scene, and the prologue completes.");
        NarrativeOutcome.Apply(null, Flags(PrologueDirector.FoundingDoneFlag, PrologueCompleteFlag));
        Leave();
    }

    /// <summary>True once the stage is leaving or gone: a sequence that was waited on stops there.</summary>
    private bool Gone => !IsInstanceValid(this) || !IsInsideTree() || _leaving || _hall == null || !IsInstanceValid(_hall)
                         || _direction == null;
}

/// <summary>The founding stage's words (Data/Encounters/founding_stage.json): the curtain into the
/// Grand Hall, and the one out into the world.</summary>
public sealed class FoundingStageWords
{
    public string EnterTitle = "";
    public string EnterSubtitle = "";
    public string LeaveTitle = "";
    public string LeaveSubtitle = "";
}
