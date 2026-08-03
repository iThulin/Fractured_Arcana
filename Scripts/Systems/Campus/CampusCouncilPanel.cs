using Godot;
using static CampusUi;

// ============================================================
// CampusCouncilPanel.cs
//
// Purpose:        The Council tab — archmage sentiment overview,
//                 the "Seek Resolution" audience list, and the
//                 mentor panel.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 CouncilOverviewPanel.cs, CampusMentorPanel.cs,
//                 ArchmageRegistry.cs, ResolutionEncounterBuilder.cs
// See:            Step 9; docs/campus_tab_extraction_v1.md — Phase 2
// ============================================================

/// <summary>Council tab. Mostly a host: <see cref="CouncilOverviewPanel"/> and
/// <see cref="CampusMentorPanel"/> already existed as their own Controls, so the only
/// rendering this class owns is the audience list between them.
///
/// <para><b>Not the same thing as <c>CouncilScreen</c>.</b> There is a global council
/// overlay on the HudManager bar, and it shares NO renderer with this tab — a ~1,400-line
/// CanvasLayer versus this. They are two independent implementations of overlapping ideas.
/// Reconciling them is real design work, not a dedup, and should happen when the campus map
/// decides which building the council lives behind. Until then, do not assume either is a
/// superset of the other.</para>
///
/// <para>Extracted from <c>CampusScreen.BuildCouncilTab</c> / <c>RefreshCouncilTab</c> /
/// <c>OpenResolutionEncounter</c> on 2026-08-03, unchanged.</para></summary>
public sealed class CampusCouncilPanel : CampusPanel
{
    private CouncilOverviewPanel _overview;
    private CampusMentorPanel _mentor;
    private VBoxContainer _audienceContainer;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(12);
        margins.AddChild(layout);

        _overview = new CouncilOverviewPanel();
        layout.AddChild(_overview);

        layout.AddChild(new HSeparator());
        AddSectionHeader(layout, "Seek Resolution");
        layout.AddChild(MakeStubLabel(
            "An audience ends an archmage's question — by pact, by pressure, or by force. " +
            "Or you withdraw, and it keeps."));
        _audienceContainer = MakeVBox(8);
        layout.AddChild(_audienceContainer);

        layout.AddChild(new HSeparator());
        _mentor = new CampusMentorPanel();
        layout.AddChild(_mentor);
    }

    public override void Refresh()
    {
        if (_overview == null) return;
        var save = Ctx?.Save;
        if (save == null) return;

        _overview.Build(save);
        _mentor?.Build(save);

        foreach (var child in _audienceContainer.GetChildren())
            child.QueueFree();

        var campaign = save.Cycle?.Campaign;
        if (campaign == null)
        {
            _audienceContainer.AddChild(MakeStubLabel("No campaign in progress."));
            return;
        }

        foreach (var pair in campaign.RegionArchmageMap)
        {
            string id = pair.Value;
            if (string.IsNullOrEmpty(id)) continue;
            var def = ArchmageRegistry.Get(id);
            if (def == null || def.IsVillainFaction) continue;

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);

            var name = new Label
            {
                Text = $"{def.DisplayName} — {def.Title}",
                // Control.SizeFlags — this class is not a Control.
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            name.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            name.AddThemeColorOverride("font_color", new Color(def.FactionColorHex));
            row.AddChild(name);

            var (can, gateReason) = ResolutionEncounterBuilder.AudienceGate(save, id);
            var btn = MakeButton(
                can ? "Seek audience" : gateReason,
                170, 36, UITheme.CampusBuildSmallFontSize, isPrimary: can);
            btn.Disabled = !can;
            string captured = id;
            btn.Pressed += () => OpenResolutionEncounter(captured);
            row.AddChild(btn);

            _audienceContainer.AddChild(row);
        }

        SaveManager.SaveIfDirty(); // mentor visit count / delivered hints
    }

    /// <summary>Step 9: open the resolution audience with an archmage on the
    /// campus narrative host. Unite/Coerce resolve here; Overthrow launches
    /// the boss fight with a campus return.</summary>
    private void OpenResolutionEncounter(string archmageId)
    {
        var campaign = Ctx?.Save?.Cycle?.Campaign;
        var enc = ResolutionEncounterBuilder.BuildAudience(campaign, archmageId);
        if (enc == null) return;

        // The shell's ShowNarrative also wires the completion handler that persists the
        // outcome, and no-ops if the overlay does not exist — so the old
        // `_campusNarrativePanel == null` guard is now redundant rather than dropped.
        Ctx.ShowNarrative?.Invoke(enc);
    }
}
