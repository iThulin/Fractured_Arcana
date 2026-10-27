using Godot;

// ============================================================
// StrategicView.Exam.cs  (partial of StrategicView)
//
// Purpose:        The strategic hub's half of the declaration exam
//                 (DisciplineExam, R7e): take the exam's return on the
//                 way in, before every other consumer, and show its
//                 outcome (the conferral, or the teacher's note) at the
//                 first quiet moment, through the city narrative host.
//                 The exam is sat from the Grand Hall's floated panel or
//                 the campus overlay, both inside this scene, and lands
//                 back in the home city (PlayerSession.StartInCityOnOpen).
// Layer:          UI + orchestration (strategic)
// Collaborators:  DisciplineExam.cs (ConsumeReturn, Outcome),
//                 StrategicView.cs (_Ready, PollChronicle,
//                 ShowFloatedPanelNarrative, the city toasts)
// ============================================================

public partial class StrategicView
{
    /// <summary>An exam's outcome waiting for the first quiet moment, or null.</summary>
    private DisciplineExam.Outcome _examOutcome;

    /// <summary>From _Ready, ahead of the other consumers: an exam sat from this scene came back.
    /// Keyed on DisciplineExam's pending school, so no other fight's return is touched, and
    /// consuming it first leaves the router clear for their stale-marker checks.</summary>
    private void ConsumeExamReturn()
    {
        var outcome = DisciplineExam.ConsumeReturn(StrategicScenePath);
        if (outcome != null)
            _examOutcome = outcome;
    }

    /// <summary>From PollChronicle, once the map is quiet: show the waiting outcome. True when
    /// one was shown, so the chronicle waits for the next quiet moment.</summary>
    private bool ShowPendingExamOutcome()
    {
        var outcome = _examOutcome;
        if (outcome == null)
            return false;
        _examOutcome = null;
        if (!string.IsNullOrEmpty(outcome.Toast))
        {
            EnsureCityExploreToasts();
            _cityExploreToasts?.Push(outcome.Toast, outcome.ToastKind);
        }
        if (outcome.Panel != null)
            ShowFloatedPanelNarrative(outcome.Panel);
        return true;
    }
}
