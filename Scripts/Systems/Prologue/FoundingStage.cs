using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

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
// Layer:          Prologue / presentation
// Collaborators:  StageField, HallSet (.GrandHall: the name, the Register,
//                 the Roll, the lectern; .Sundering: PlaceFigure),
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

    /// <summary>Frames to wait for the grid to build the hall before giving up.</summary>
    private const int BuildPatienceFrames = 300;
    /// <summary>Frames between building the opening shot and starting, so it has been drawn once.</summary>
    private const int SettleFrames = 2;

    // ── The beats, in seconds ────────────────────────────────────────────
    private const float OpenHold = 1.0f;         // the doors, before the first letter
    private const float NameSeconds = 2.6f;      // the whole name, cut in
    private const float NameHold = 1.1f;         // the name, cut
    private const float FadeSeconds = 0.4f;      // each half of the cut to the west end
    private const float WestHold = 0.6f;
    private const float RegisterSeconds = 0.5f;  // the Register, set down
    private const float RollDelay = 0.4f;
    private const float RollSeconds = 1.4f;      // the Roll's first line, written
    private const float PanelDelay = 0.9f;
    private const float StepSeconds = 1.4f;      // round the lectern to the reader's place
    private const float TurnSeconds = 0.3f;      // and the turn to face the room
    private const float DoorDelay = 1.0f;
    private const float PageSeconds = 1.8f;      // the lens down onto the open page

    // ── The lens: each shot is an eye and a point it looks at ────────────
    private const float LensFov = 55f;
    private const float DoorsBack = 9.0f;        // inside the doors, from the lintel
    private const float DoorsEye = 1.9f;
    private const float DoorsAside = 1.2f;
    private const float LintelLook = 1.0f;       // below the lintel's middle: the doors and the name
    private const float WestBack = 4.0f;         // east of the lectern and north of it, looking west at
    private const float WestEye = 1.85f;         // it, the Roll beyond it and you beside it
    private const float WestAside = 2.4f;
    private const float ReaderBack = 5.5f;       // the reader, seen from the room
    private const float ReaderEye = 1.7f;
    private const float ReaderAside = -0.8f;
    private const float LookHeight = 1.5f;       // where the lens looks on a standing figure
    private const float PageWest = 0.45f;        // over the open page at the end, from just over the
    private const float PageRise = 0.85f;        // reader's shoulder (the slope faces the reader)
    private const float PageAside = 0.15f;

    // ── You, at the lectern ───────────────────────────────────────────────
    private const float FrontOfLectern = 0.7f;   // where you stand to set the Register down: its east
    private const float FrontAside = 0.55f;      // side, a little south, so the lens sees it past you
    private const float AroundLectern = 1.7f;    // the walk round it bows this far south (its candle is north)

    private const int FadeLayer = 90;
    private const int PanelLayer = 95;

    private static FoundingStageWords _words;

    private StageField _field;
    private HallSet _hall;
    private Camera3D _lens;
    private Vector3 _eye;
    private Vector3 _at;
    private Node3D _candidate;
    private CanvasLayer _panelLayer;
    private NarrativeEncounterPanel _panel;
    private ColorRect _fade;
    private int _waited;
    private bool _built;
    private int _settle = -1;
    private bool _leaving;

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
            _field.Camera.AcceptInput = false;   // the founding has a lens of its own
        _lens = new Camera3D { Name = "Lens", Fov = LensFov, Near = 0.05f, Far = 400f };
        AddChild(_lens);
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
        if (_lens != null && IsInstanceValid(_lens) && !_lens.Current)
            _lens.MakeCurrent();
        if (_settle > 0 && --_settle == 0)
            Begin();
    }

    /// <summary>The opening shot: the hall as the founding finds it (the name not cut, the Register
    /// not set down, you inside the doors), or, back after the founding's choice, the founded hall
    /// with you at the reader's place.</summary>
    private void Build(HallSet hall)
    {
        _built = true;
        _hall = hall;
        _lens.MakeCurrent();
        bool founded = PrologueDirector.Founded(SaveManager.ActiveSave);
        if (founded)
        {
            _candidate = hall.PlaceFigure("candidate", hall.GrandReaderPoint, Vector3.Right, HallSet.ClassRobe);
            Look(ReaderShotEye(), ReaderShotAt());
        }
        else
        {
            hall.HideFoundingPieces();
            _candidate = hall.PlaceFigure("candidate", hall.GrandDoorsPoint, Vector3.Right, HallSet.ClassRobe);
            DoorsShot();
        }
        _settle = SettleFrames;
        GD.Print($"[FoundingStage] The Grand Hall, " + (founded ? "founded: on to the Door." : "for the founding."));
    }

    /// <summary>The founding, beat by beat; or, back after its choice, the Door.</summary>
    private async void Begin()
    {
        var save = SaveManager.ActiveSave;
        if (PrologueDirector.Founded(save))
        {
            await Pause(DoorDelay);
            if (!Gone)
                ShowDoor();
            return;
        }
        await Pause(OpenHold);
        if (Gone)
            return;
        int letters = _hall.CutGuildName(NameSeconds);
        await Pause((letters > 0 ? NameSeconds : 0f) + NameHold);
        if (Gone)
            return;

        // The cut to the west end: you in front of the lectern, facing it, the Roll behind it.
        await Fade(1f, FadeSeconds);
        if (Gone)
            return;
        _candidate.GlobalPosition = FrontOfTheLectern();
        Face(_candidate, _hall.GrandLecternPoint - FrontOfTheLectern());
        WestShot();
        await Fade(0f, FadeSeconds);
        if (Gone)
            return;
        await Pause(WestHold);
        if (Gone)
            return;
        _hall.SetRegisterDown(RegisterSeconds);
        await Pause(RegisterSeconds + RollDelay);
        if (Gone)
            return;
        _hall.WriteRollLine(save, RollSeconds);
        await Pause(RollSeconds + PanelDelay);
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
        Vector3 lectern = _hall.GrandLecternPoint;
        Vector3 from = _candidate.GlobalPosition;
        Vector3 bow = new(lectern.X, lectern.Y, lectern.Z - AroundLectern);
        Vector3 to = _hall.GrandReaderPoint;
        var glide = Glide(ReaderShotEye(), ReaderShotAt(), StepSeconds + TurnSeconds);
        await WalkRound(_candidate, from, bow, to, StepSeconds);
        if (Gone)
            return;
        await TurnTo(_candidate, Vector3.Right, TurnSeconds);
        await glide;
        if (Gone)
            return;
        await Pause(DoorDelay);
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
        await Glide(_hall.GrandPagePoint + new Vector3(-PageWest, PageRise, PageAside), _hall.GrandPagePoint, PageSeconds);
        if (Gone)
            return;
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

    // ════════════════════════════════════════════════════════════════════════
    //  The panels, and the ways out
    // ════════════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        var fadeLayer = new CanvasLayer { Name = "FadeLayer", Layer = FadeLayer };
        AddChild(fadeLayer);
        _fade = new ColorRect { Name = "Fade", Color = new Color(UITheme.CardInk, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _fade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        fadeLayer.AddChild(_fade);

        _panelLayer = new CanvasLayer { Name = "PanelLayer", Layer = PanelLayer };
        AddChild(_panelLayer);
        _panel = new NarrativeEncounterPanel { Name = "FoundingPanel", Visible = false };
        _panelLayer.AddChild(_panel);
    }

    /// <summary>A prologue panel over the hall, applied as the city applies them (NarrativeOutcome);
    /// <paramref name="next"/> runs once it is answered.</summary>
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
            NarrativeOutcome.Apply(enc, choice);
            next?.Invoke();
        };
        GD.Print($"[FoundingStage] Over the hall: {enc.Id}.");
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

    /// <summary>With no save there is nothing to found: back to the city.</summary>
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
    /// so the city does not send the save back here in a loop, and the world opens.</summary>
    private void Fail(string why)
    {
        if (_leaving)
            return;
        GD.PushError($"[FoundingStage] {why}: the guild is founded without its scene, and the prologue completes.");
        NarrativeOutcome.Apply(null, Flags(PrologueDirector.FoundingDoneFlag, PrologueCompleteFlag));
        Leave();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The lens, the walk and the waits
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True once the stage is leaving or gone: a beat that waited stops there.</summary>
    private bool Gone => !IsInstanceValid(this) || !IsInsideTree() || _leaving || _hall == null || !IsInstanceValid(_hall);

    private void DoorsShot()
    {
        Vector3 lintel = _hall.GrandLintelPoint;
        Vector3 doors = _hall.GrandDoorsPoint;
        Look(new Vector3(lintel.X - DoorsBack, doors.Y + DoorsEye, lintel.Z + DoorsAside),
             lintel + new Vector3(0f, -LintelLook, 0f));
    }

    private void WestShot()
    {
        Vector3 lectern = _hall.GrandLecternPoint;
        Look(new Vector3(lectern.X + WestBack, lectern.Y + WestEye, lectern.Z + WestAside),
             new Vector3(lectern.X - 0.4f, lectern.Y + LookHeight, lectern.Z));
    }

    /// <summary>The reader seen from the room: the eye.</summary>
    private Vector3 ReaderShotEye()
    {
        Vector3 lectern = _hall.GrandLecternPoint;
        return new Vector3(lectern.X + ReaderBack, lectern.Y + ReaderEye, lectern.Z + ReaderAside);
    }

    /// <summary>The reader seen from the room: where the lens looks.</summary>
    private Vector3 ReaderShotAt()
    {
        Vector3 reader = _hall.GrandReaderPoint;
        return new Vector3(reader.X, reader.Y + LookHeight, reader.Z);
    }

    private Vector3 FrontOfTheLectern()
    {
        Vector3 lectern = _hall.GrandLecternPoint;
        return new Vector3(lectern.X + FrontOfLectern, lectern.Y, lectern.Z - FrontAside);
    }

    private void Look(Vector3 eye, Vector3 at)
    {
        _eye = eye;
        _at = at;
        if (_lens != null && IsInstanceValid(_lens))
            _lens.LookAtFromPosition(eye, at, Vector3.Up);
    }

    /// <summary>The lens from where it is to a new eye and point, eased, over <paramref name="seconds"/>.</summary>
    private async Task Glide(Vector3 eye, Vector3 at, float seconds)
    {
        Vector3 eye0 = _eye, at0 = _at;
        var tween = CreateTween();
        tween.TweenMethod(Callable.From<float>(t => Look(eye0.Lerp(eye, t), at0.Lerp(at, t))), 0f, 1f, Mathf.Max(0.05f, seconds))
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        await ToSignal(tween, Tween.SignalName.Finished);
    }

    /// <summary>A figure along a curve from <paramref name="from"/> to <paramref name="to"/> that bows
    /// toward <paramref name="bow"/>, facing the way it goes. It glides: the figures are stand-ins.</summary>
    private async Task WalkRound(Node3D figure, Vector3 from, Vector3 bow, Vector3 to, float seconds)
    {
        var tween = CreateTween();
        tween.TweenMethod(Callable.From<float>(t =>
        {
            if (figure == null || !IsInstanceValid(figure))
                return;
            float u = 1f - t;
            figure.GlobalPosition = u * u * from + 2f * u * t * bow + t * t * to;
            Face(figure, 2f * u * (bow - from) + 2f * t * (to - bow));
        }), 0f, 1f, Mathf.Max(0.05f, seconds)).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        await ToSignal(tween, Tween.SignalName.Finished);
    }

    /// <summary>A figure turned to face <paramref name="direction"/> the short way round.</summary>
    private async Task TurnTo(Node3D figure, Vector3 direction, float seconds)
    {
        if (figure == null || !IsInstanceValid(figure))
            return;
        float now = figure.Rotation.Y;
        float target = now + Mathf.Wrap(Mathf.Atan2(direction.X, direction.Z) - now, -Mathf.Pi, Mathf.Pi);
        var tween = CreateTween();
        tween.TweenProperty(figure, "rotation:y", target, Mathf.Max(0.05f, seconds))
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        await ToSignal(tween, Tween.SignalName.Finished);
    }

    /// <summary>Turns a figure to face along <paramref name="direction"/> (the rig faces +Z, as
    /// HallSet.PlaceFigure turns it).</summary>
    private static void Face(Node3D figure, Vector3 direction)
    {
        direction.Y = 0f;
        if (figure != null && direction.LengthSquared() > 1e-6f)
            figure.Rotation = new Vector3(0f, Mathf.Atan2(direction.X, direction.Z), 0f);
    }

    /// <summary>The screen to <paramref name="to"/> dark (1) or clear (0), over <paramref name="seconds"/>.</summary>
    private async Task Fade(float to, float seconds)
    {
        if (_fade == null || !IsInstanceValid(_fade))
            return;
        var tween = CreateTween();
        tween.TweenProperty(_fade, "color:a", to, Mathf.Max(0.05f, seconds));
        await ToSignal(tween, Tween.SignalName.Finished);
    }

    /// <summary>Waits <paramref name="seconds"/> of the scene's time: the pause menu holds it.</summary>
    private async Task Pause(float seconds)
    {
        if (seconds <= 0f)
            return;
        await ToSignal(GetTree().CreateTimer(seconds, processAlways: false), SceneTreeTimer.SignalName.Timeout);
    }
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
