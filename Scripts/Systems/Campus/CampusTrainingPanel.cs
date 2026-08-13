using Godot;
using static CampusUi;

// ============================================================
// CampusTrainingPanel.cs
//
// Purpose:        The Training tab — stance training for martial
//                 companions, gated by Training Grounds tier.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 StanceRegistry.cs, Companion.cs,
//                 GuildSaveData.MartialStanceSlots / TrainingGroundsTier
// See:            docs/campus_tab_extraction_v1.md — Phase 2
// ============================================================

/// <summary>Stance training. The one tab that is legitimately empty until a building is
/// built: with <c>TrainingGroundsTier == 0</c> it shows a single "Build Training Grounds"
/// stub. That is the diegetic behaviour the campus-map redesign wants — no yard on the map,
/// no training — and the reason Training Grounds stayed an OPTIONAL building when the
/// foundational set was authored.
///
/// <para>Extracted from <c>CampusScreen</c> on 2026-08-03. Rendering, gating and the 50g
/// training cost are unchanged, including two redundancies that were preserved rather than
/// tidied — see <see cref="Refresh"/> and the learn button.</para></summary>
public sealed class CampusTrainingPanel : CampusPanel
{
    /// <summary>Flat training cost per stance. Was an inline literal with a "could be
    /// data-driven later" note; kept a constant here so the later move has one place to
    /// start from.</summary>
    private const int StanceTrainingCost = 50;

    private VBoxContainer _container;
    private string _selectedCompanionId = null;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var outer = MakeMargins(20, 16);
        scroll.AddChild(outer);

        _container = MakeVBox(12);
        _container.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        outer.AddChild(_container);

        Refresh();
    }

    public override void Refresh()
    {
        if (_container == null)
            return;
        foreach (Node child in _container.GetChildren())
            child.QueueFree();

        var save = Ctx?.Save;
        if (save == null)
        {
            _container.AddChild(MakeStubLabel("No save loaded."));
            return;
        }

        // Redundant on the RefreshAll path (which repaints gold itself) but load-bearing
        // on the tab-switch path. Preserved as extracted.
        Ctx.RefreshGold?.Invoke();

        int tgTier = save.TrainingGroundsTier;
        if (tgTier == 0)
        {
            _container.AddChild(MakeStubLabel(
                "Build Training Grounds to unlock stance training."));
            return;
        }

        AddSectionHeader(_container, "Stance Training");

        var note = new Label
        {
            Text = $"Training Grounds Tier {tgTier} — " +
                   $"{save.MartialStanceSlots} stance slot(s) active per companion.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        note.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        _container.AddChild(note);

        // ── Companion selector ────────────────────────────────────────────
        AddSectionHeader(_container, "Select Companion");
        BuildCompanionSelector(save);

        if (_selectedCompanionId == null)
            return;

        var companion = save.Companions.Find(
            c => c.Id == _selectedCompanionId);
        if (companion == null || companion.IsPermadead)
            return;

        bool isMartial = companion.UnitClass == "Fighter" ||
                         companion.UnitClass == "Ranger";
        if (!isMartial)
        {
            _container.AddChild(MakeStubLabel(
                $"{companion.Name} is arcane — no stance training available."));
            return;
        }

        // ── Current trained stances ───────────────────────────────────────
        AddSectionHeader(_container, $"{companion.Name}'s Trained Stances");
        BuildTrainedStanceList(companion, save);

        // ── Available stances to learn ────────────────────────────────────
        AddSectionHeader(_container, "Available to Learn");
        BuildLearnableStanceList(companion, save);
    }

    private void BuildCompanionSelector(GuildSaveData save)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _container.AddChild(row);

        foreach (var companion in save.Companions)
        {
            if (!companion.IsRecruited || companion.IsPermadead)
                continue;
            bool isMartial = companion.UnitClass == "Fighter" ||
                             companion.UnitClass == "Ranger";

            bool isSelected = _selectedCompanionId == companion.Id;
            var btn = new Button
            {
                Text = companion.Name,
                ToggleMode = true,
                ButtonPressed = isSelected,
                CustomMinimumSize = new Vector2(120, 36),
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            // Reuses the tab-bar styling for an in-panel toggle row. Noted because when the
            // tab bar is retired, ApplyTabStyle still has this consumer.
            ApplyTabStyle(btn, isSelected);

            if (!isMartial)
                btn.Modulate = new Color(1, 1, 1, 0.5f); // dim arcane companions

            string captured = companion.Id;
            btn.Pressed += () =>
            {
                _selectedCompanionId = captured;
                Refresh();
            };
            row.AddChild(btn);
        }
    }

    private void BuildTrainedStanceList(Companion companion, GuildSaveData save)
    {
        int slots = save.MartialStanceSlots;

        if (companion.TrainedStanceIds.Count == 0)
        {
            _container.AddChild(MakeStubLabel("No stances trained yet."));
        }
        else
        {
            for (int i = 0; i < companion.TrainedStanceIds.Count; i++)
            {
                bool slotActive = i < slots;
                var stance = StanceRegistry.Get(companion.TrainedStanceIds[i]);
                if (stance == null)
                    continue;

                var row = BuildStanceRow(stance, companion, save,
                    isActive: slotActive, canForget: true);
                _container.AddChild(row);
            }
        }

        // Show locked slots
        for (int i = companion.TrainedStanceIds.Count; i < 3; i++)
        {
            bool unlocked = i < slots;
            var slotLbl = new Label
            {
                Text = unlocked
                    ? $"Slot {i + 1}: Empty — learn a stance below"
                    : $"Slot {i + 1}: Locked (Training Grounds Tier {i + 1} required)",
            };
            slotLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            slotLbl.AddThemeColorOverride("font_color",
                unlocked ? UITheme.TextSecondary : UITheme.TextDim);
            _container.AddChild(slotLbl);
        }
    }

    private void BuildLearnableStanceList(Companion companion, GuildSaveData save)
    {
        // Stances this companion can learn based on their class
        // that they haven't learned yet
        var martialClass = companion.UnitClass == "Fighter"
            ? MartialClass.Fighter : MartialClass.Ranger;

        bool anyLearnable = false;
        foreach (var stance in StanceRegistry.All.Values)
        {
            if (stance.Class != martialClass)
                continue;
            // K4: signatures are earned (ArcStage 4), never bought — the
            // Training Grounds is the global floor, the signature is the
            // personal ceiling. Granted at spawn by EligibleSignature.
            if (stance.IsSignature)
                continue;
            if (companion.TrainedStanceIds.Contains(stance.Id))
                continue;

            anyLearnable = true;
            bool canLearn = companion.TrainedStanceIds.Count < save.MartialStanceSlots;

            int cost = StanceTrainingCost;
            bool canAfford = save.Gold >= cost;

            var row = BuildLearnStanceRow(stance, companion, save,
                cost, canLearn, canAfford);
            _container.AddChild(row);
        }

        if (!anyLearnable)
            _container.AddChild(MakeStubLabel(
                $"{companion.Name} has learned all available stances."));
    }

    private Control BuildStanceRow(StanceDefinition stance, Companion companion,
        GuildSaveData save, bool isActive, bool canForget)
    {
        var panel = new PanelContainer();
        var style = UITheme.MakePanelStyle(
            isActive ? UITheme.BgRaised : UITheme.BgBase,
            isActive ? UITheme.Violet : UITheme.Neutral);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(row);

        var info = MakeVBox(2);
        info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(info);

        var nameLbl = new Label { Text = stance.DisplayName };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        nameLbl.AddThemeColorOverride("font_color",
            isActive ? UITheme.TextPrimary : UITheme.TextDim);
        info.AddChild(nameLbl);

        var descLbl = new Label
        {
            Text = stance.Description,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        descLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        descLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        info.AddChild(descLbl);

        if (!isActive)
        {
            var inactiveLbl = new Label { Text = "Inactive — upgrade Training Grounds" };
            inactiveLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            inactiveLbl.AddThemeColorOverride("font_color", UITheme.Warning);
            info.AddChild(inactiveLbl);
        }

        if (canForget)
        {
            var forgetBtn = new Button
            {
                Text = "Forget",
                CustomMinimumSize = new Vector2(70, 28),
            };
            forgetBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            UITheme.ApplyButtonStyle(forgetBtn, isPrimary: false);

            string stanceId = stance.Id;
            forgetBtn.Pressed += () =>
            {
                companion.TrainedStanceIds.Remove(stanceId);
                SaveManager.Save();
                Refresh();
            };
            row.AddChild(forgetBtn);
        }

        return panel;
    }

    private Control BuildLearnStanceRow(StanceDefinition stance, Companion companion,
        GuildSaveData save, int cost, bool canLearn, bool canAfford)
    {
        var panel = new PanelContainer();
        var style = UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Neutral);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(row);

        var info = MakeVBox(2);
        info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(info);

        var nameLbl = new Label { Text = stance.DisplayName };
        nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        nameLbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        info.AddChild(nameLbl);

        var descLbl = new Label
        {
            Text = stance.Description,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        descLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        descLbl.AddThemeColorOverride("font_color", UITheme.TextSecondary);
        info.AddChild(descLbl);

        var learnBtn = new Button
        {
            Text = $"Train ({cost}g)",
            CustomMinimumSize = new Vector2(90, 32),
            Disabled = !canLearn || !canAfford,
        };
        learnBtn.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
        UITheme.ApplyButtonStyle(learnBtn, isPrimary: canLearn && canAfford);

        if (!canLearn)
        {
            var reasonLbl = new Label { Text = "No open slots" };
            reasonLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            reasonLbl.AddThemeColorOverride("font_color", UITheme.TextDim);
            info.AddChild(reasonLbl);
        }
        else if (!canAfford)
        {
            var reasonLbl = new Label { Text = $"Need {cost}g" };
            reasonLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
            reasonLbl.AddThemeColorOverride("font_color", UITheme.Danger);
            info.AddChild(reasonLbl);
        }

        string stanceId = stance.Id;
        learnBtn.Pressed += () =>
        {
            save.Gold -= cost;
            companion.TrainedStanceIds.Add(stanceId);
            SaveManager.Save();
            // Both calls preserved as extracted. RequestRefreshAll fans out to this panel's
            // Refresh too, so the list rebuilds twice — pre-existing, and deliberately not
            // "fixed" in an extraction commit. Drop the first line when someone is testing
            // training specifically, not while moving code.
            Refresh();
            Ctx.RequestRefreshAll?.Invoke(); // update gold display
        };
        row.AddChild(learnBtn);

        return panel;
    }
}
