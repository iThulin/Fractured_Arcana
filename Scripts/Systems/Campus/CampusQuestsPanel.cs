using Godot;
using static CampusUi;

// ============================================================
// CampusQuestsPanel.cs
//
// Purpose:        The Quests tab: story log + Hall of Lore,
//                 and the campus launch point for companion-arc
//                 mission stages.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 QuestLogView.cs (THE renderer, shared with the
//                 global QuestLogScreen overlay), QuestTracker.cs,
//                 CompanionArcTracker.cs, NarrativeEncounterLoader.cs
// See:            docs/campus_tab_extraction_v1.md, Phase 2
// ============================================================

/// <summary>Quests tab. Deliberately thin: <see cref="QuestLogView"/> owns the actual
/// rendering and is shared with the global <c>QuestLogScreen</c> overlay, so this panel is
/// a host and a mission-launcher, not a second quest renderer.
///
/// <para><b>This is not a duplicate of the overlay.</b> It looks like one, since both are
/// reachable at once and the overlay opens from the HudManager bar. They differ in exactly
/// one capability:
/// <code>
/// QuestLogScreen   QuestLogView.BuildInto(box, save);
/// this panel       QuestLogView.BuildInto(box, save, OnBeginCompanionMission);
/// </code>
/// The overlay cannot start a companion-arc stage, because doing so needs a narrative host
/// and the overlay has none. Anyone tempted to delete this tab as redundant loses that entry
/// point silently, with no compile error and no log line. Fix the overlay first if you want them
/// equivalent.</para>
///
/// <para>Extracted from <c>CampusScreen.BuildQuestsTab</c> / <c>RefreshQuestsTab</c> /
/// <c>OnBeginCompanionMission</c> on 2026-08-03, unchanged.</para></summary>
public sealed class CampusQuestsPanel : CampusPanel
{
    private VBoxContainer _questContainer;
    private Label _questSummaryLabel;
    private VBoxContainer _loreContainer;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(12);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Quests");

        _questSummaryLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _questSummaryLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _questSummaryLabel.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        layout.AddChild(_questSummaryLabel);

        layout.AddChild(new HSeparator());

        _questContainer = MakeVBox(10);
        layout.AddChild(_questContainer);

        // Lore codex, consolidated here from the Records tab.
        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Hall of Lore");
        _loreContainer = MakeVBox(6);
        layout.AddChild(_loreContainer);
    }

    public override void Refresh()
    {
        if (_questContainer == null) return;

        var save = Ctx?.Save;
        if (save != null) QuestTracker.SyncCompletions(save);
        QuestLogView.BuildLoreInto(_loreContainer, save);

        _questSummaryLabel.Text = QuestLogView.BuildInto(_questContainer, save,
            OnBeginCompanionMission);
    }

    /// <summary>Step 9 follow-up: launch a campus-located companion arc stage
    /// from its quest-log mission card, on the campus narrative host.</summary>
    private void OnBeginCompanionMission(CompanionArcStatus m)
    {
        var save = Ctx?.Save;
        if (save == null || m == null) return;

        string encId = CompanionArcTracker.GetStageEncounterId(
            m.CompanionId, save, isExpedition: false);
        if (string.IsNullOrEmpty(encId)) return;

        var enc = NarrativeEncounterLoader.FindMissionById(encId);
        if (enc == null)
        {
            GD.PrintErr($"[CompanionArc] Mission encounter '{encId}' not found in companion_missions.json.");
            return;
        }

        // ShowNarrative, not the raw panel: the shell also wires the completion handler
        // that persists flags/gold/meta (see CampusContext.ShowNarrative).
        Ctx.ShowNarrative?.Invoke(enc);
    }
}
