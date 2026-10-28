using Godot;
using System;

// ============================================================
// PrologueScreen.cs
//
// Purpose:        The examination hall (wire item C4): the scene a new
//                 save opens in. Hosts P1 to P4 of the prologue
//                 (docs/prologue_script_v1.md, section 1) through one
//                 narrative panel, offers the skips as system dialogs,
//                 launches the three fights and takes them back, then
//                 hands over to the city. Every decision is
//                 PrologueDirector's; this is only the host.
// Layer:          UI / scene host
// Collaborators:  PrologueDirector.cs (the steps, panels, fights),
//                 NarrativeEncounterPanel (the panel),
//                 SceneTransition (the way out), CycleInitializer (the
//                 world and the pinned co-conspirator the panels name)
// Scene:          Scenes/Prologue/PrologueScene.tscn
// ============================================================

public partial class PrologueScreen : Control
{
    private NarrativeEncounterPanel _panel;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        var hall = new ColorRect { Name = "Hall", Color = UITheme.BgDeep, MouseFilter = MouseFilterEnum.Ignore };
        hall.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(hall);

        _panel = new NarrativeEncounterPanel { Name = "ProloguePanel", Visible = false };
        AddChild(_panel);

        if (SaveManager.ActiveSave == null)
            SaveManager.AutoLoadLast();
        if (SaveManager.ActiveSave == null)
        {
            CallDeferred(nameof(RouteToCampus));
            return;
        }
        // The same boot the strategic view does: a seeded save and this reading's world,
        // which also pins the co-conspirator the Commencement panels name (C3).
        CycleInitializer.EnsureSaveSeeded();
        CycleInitializer.EnsureCycleWorld();
        // The wizard fights in the session's school (and the Adept's mana is granted from it),
        // so it must follow the save on a resume from a cold boot too.
        if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave.SelectedSchool, true, out var school))
            PlayerSession.SelectedSchool = school;
        PrologueDirector.AssertRoundTripOnce();   // C4 item 9: logs only, once per process
        CallDeferred(nameof(Begin));
    }

    private void RouteToCampus() => GetTree().ChangeSceneToFile(PrologueDirector.CampusScenePath);

    /// <summary>A fight that just came back routes to its verdict; otherwise the flags say
    /// where the save stands.</summary>
    private void Begin()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        Advance(PrologueDirector.ConsumeReturn(save) ?? PrologueDirector.NextStep(save));
    }

    private void Advance(string next)
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        if (string.IsNullOrEmpty(next) || next == PrologueDirector.StepWorld)
        {
            ExitToWorld();
            return;
        }
        if (next == PrologueDirector.StepSkips)
        {
            OfferSkips();
            return;
        }
        if (next.StartsWith(PrologueDirector.FightPrefix, StringComparison.Ordinal))
        {
            string fightId = next.Substring(PrologueDirector.FightPrefix.Length);
            if (!PrologueDirector.Launch(GetTree(), fightId, save))
            {
                GD.PushError($"[Prologue] {fightId} could not launch; the prologue closes as a skip.");
                PrologueDirector.ForceComplete(save);
                ExitToWorld();
            }
            return;
        }
        ShowPanel(next);
    }

    private void ShowPanel(string id)
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        RegisterManager.PrologueMode = PrologueDirector.RegisterModeFor(id);
        var enc = PrologueDirector.Panel(id, save);
        if (enc == null)
        {
            GD.PushError($"[Prologue] Panel {id} is missing; the prologue closes as a skip.");
            PrologueDirector.ForceComplete(save);
            ExitToWorld();
            return;
        }
        _panel.ShowEncounter(enc, save.HasFlag);
        _panel.OnCompleted = choice => OnPanelDone(id, choice);
    }

    private void OnPanelDone(string id, EncounterChoice choice)
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        PrologueDirector.ApplyChoice(save, choice);
        SaveManager.SaveIfDirty();
        Advance(PrologueDirector.After(id, save));
    }

    // ── The skips (P-2, P-3): system dialogs, outside the fiction ────────

    /// <summary>Right after the stated intention: the full skip when this practical was
    /// played on this computer, then (declined, or not offered) the part-one skip when any
    /// was. Dismissing a dialog plays the prologue.</summary>
    private void OfferSkips()
    {
        var save = SaveManager.ActiveSave;
        if (save == null)
            return;
        RegisterManager.PrologueMode = "trial";
        if (!PrologueDirector.FullSkipOffered(save))
        {
            OfferPartOneSkip();
            return;
        }
        Ask("Skip Commencement?",
            "You have played the prologue before on this computer. "
            + "Skip to the gate? You will read one page that names who betrayed the hall in this save.",
            "Skip to the gate", "Play the prologue",
            () => ShowPanel(PrologueDirector.RecapPanelId),
            OfferPartOneSkip);
    }

    private void OfferPartOneSkip()
    {
        if (!PrologueDirector.PartOneSkipOffered())
        {
            ShowPanel(PrologueDirector.RulesPanelId);
            return;
        }
        Ask("Skip part one?",
            "You have played the opening duel before on this computer. Go straight to your practical?",
            "Skip the duel", "Play it",
            () => ShowPanel(PrologueDirector.RulesWaivedPanelId),
            () => ShowPanel(PrologueDirector.RulesPanelId));
    }

    /// <summary>A two-button question. The cancel button, Escape and the close box all count
    /// as the second answer; whichever arrives first wins.
    ///
    /// <para>The second answer hides the dialog itself before it goes on. Godot hides a
    /// confirmed dialog before it emits <c>confirmed</c>, but a canceled one only on the next
    /// idle (<c>AcceptDialog._cancel_pressed</c> defers its hide, then emits <c>canceled</c>),
    /// so while <paramref name="onNo"/> runs the dialog is still the root's exclusive child.
    /// Declining the full skip asks the part-one question at once, and that popup logged
    /// "Attempting to make child window exclusive, but the parent window already has another
    /// exclusive child" (2026-10-27).</para></summary>
    private void Ask(string title, string body, string yes, string no, Action onYes, Action onNo)
    {
        bool answered = false;
        var dialog = new ConfirmationDialog
        {
            Title = title,
            DialogText = body,
            OkButtonText = yes,
            CancelButtonText = no,
        };
        dialog.Confirmed += () =>
        {
            if (answered)
                return;
            answered = true;
            dialog.QueueFree();
            onYes?.Invoke();
        };
        dialog.Canceled += () =>
        {
            if (answered)
                return;
            answered = true;
            dialog.Hide();   // frees the exclusive slot now; see the summary
            dialog.QueueFree();
            onNo?.Invoke();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    // ── The way out ──────────────────────────────────────────────────────

    /// <summary>The Margin is answered (or the recap read): out to the city, where the
    /// Anchorhold and the Door wait for the first quiet moment.</summary>
    private void ExitToWorld()
    {
        var save = SaveManager.ActiveSave;
        RegisterManager.PrologueMode = "";
        if (save != null)
        {
            PrologueDirector.NoteProfileCompletion(save);
            SaveManager.Save();
        }
        PlayerSession.StartInCityOnOpen = true;
        string title = save != null && save.HasFlag("prologue_complete") ? "The Gate" : "The Anchorhold";
        SceneTransition.Go(GetTree(), PrologueDirector.StrategicScenePath, title);
    }
}
