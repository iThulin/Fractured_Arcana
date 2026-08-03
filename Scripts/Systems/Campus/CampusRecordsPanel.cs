using Godot;
using static CampusUi;

// ============================================================
// CampusRecordsPanel.cs
//
// Purpose:        The Hall of Records tab — the permanent deal
//                 ledger, every negotiation signed or spurned,
//                 across all timelines.
// Layer:          UI
// Collaborators:  CampusPanel.cs (base), CampusContext.cs,
//                 CampusUi.cs, DealRecord.cs (EternalLedger
//                 .DealRecords), UITheme.cs
// See:            negotiation doc §7b;
//                 docs/campus_tab_extraction_v1.md — Phase 2
// ============================================================

/// <summary>Hall of Records. Reads <c>EternalLedger.DealRecords</c> — permanent, so this
/// panel is one of the few that shows anything at all on a fresh cycle.
///
/// Extracted verbatim from <c>CampusScreen.BuildRecordsTab</c> / <c>RefreshRecordsTab</c>
/// on 2026-08-03. No layout, wording or aggregate maths changed.</summary>
public sealed class CampusRecordsPanel : CampusPanel
{
    /// <summary>Newest-first row cap. The ledger is unbounded across a long save, and
    /// Godot builds every row eagerly — this is a UI-sanity limit, not a data limit.</summary>
    private const int MaxRows = 50;

    private VBoxContainer _container;
    private Label _summaryLabel;

    protected override void OnBuild(ScrollContainer scroll)
    {
        var margins = MakeMargins(32, 20);
        scroll.AddChild(margins);
        var layout = MakeVBox(12);
        margins.AddChild(layout);

        AddSectionHeader(layout, "Hall of Records — Deal Ledger");

        _summaryLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _summaryLabel.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
        _summaryLabel.AddThemeColorOverride("font_color", UITheme.NegotiationNpcColor);
        layout.AddChild(_summaryLabel);

        layout.AddChild(new HSeparator());

        _container = MakeVBox(8);
        layout.AddChild(_container);
    }

    public override void Refresh()
    {
        if (_container == null) return;
        foreach (var child in _container.GetChildren())
            child.QueueFree();

        var records = Ctx?.Save?.Ledger?.DealRecords;
        if (records == null || records.Count == 0)
        {
            _summaryLabel.Text =
                "Every negotiation — signed or spurned — is remembered here, across all timelines.";
            _container.AddChild(MakeStubLabel("No deals recorded yet."));
            return;
        }

        // Aggregate line.
        int signedCount = 0, fiveStar = 0, starSum = 0;
        foreach (var r in records)
        {
            if (r.Outcome != "Signed") continue;
            signedCount++;
            starSum += r.Stars;
            if (r.Stars >= 5) fiveStar++;
        }
        string avg = signedCount > 0
            ? $"  ·  avg {(float)starSum / signedCount:0.0}★"
            : "";
        _summaryLabel.Text =
            $"{records.Count} tables recorded  ·  {signedCount} deals signed{avg}  ·  " +
            $"{fiveStar} five-star deal{(fiveStar == 1 ? "" : "s")} anchored";

        // Rows, newest first, capped for UI sanity.
        int shown = 0;
        for (int i = records.Count - 1; i >= 0 && shown < MaxRows; i--, shown++)
        {
            var r = records[i];
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);

            var starLbl = new Label
            {
                Text = r.Outcome == "Signed"
                    ? new string('★', r.Stars) + new string('☆', 5 - r.Stars)
                    : "—",
                CustomMinimumSize = new Vector2(96, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            starLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            starLbl.AddThemeColorOverride("font_color",
                r.Outcome == "Signed" ? UITheme.NegotiationTitleColor : UITheme.NegotiationHiddenTerm);
            row.AddChild(starLbl);

            var col = MakeVBox(2);
            // Control.SizeFlags, not bare SizeFlags — this class is not a Control, so the
            // unqualified name that worked inside CampusScreen does not resolve here.
            col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            var nameLbl = new Label
            {
                Text = $"{r.NpcName} — {r.Title}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            nameLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBodyFontSize);
            col.AddChild(nameLbl);

            string outcomeText = r.Outcome switch
            {
                "Signed"     => $"Deal signed in the {r.Zone} zone",
                "WalkedAway" => "You walked away",
                "Collapsed"  => "The table collapsed",
                _            => "They left the table",
            };
            string spoils = r.Outcome == "Signed"
                ? $"  ·  {(r.Gold >= 0 ? "+" : "")}{r.Gold} gold, {(r.Reputation >= 0 ? "+" : "")}{r.Reputation} rep"
                  + (string.IsNullOrEmpty(r.SpellGranted) ? "" : "  ·  spell taught")
                : "";
            var detailLbl = new Label
            {
                Text = $"Cycle {r.CycleNumber}  ·  {r.Archetype}  ·  {outcomeText}{spoils}  ·  {r.Turns} turns",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            detailLbl.AddThemeFontSizeOverride("font_size", UITheme.CampusBuildSmallFontSize);
            detailLbl.AddThemeColorOverride("font_color", UITheme.NegotiationHiddenTerm);
            col.AddChild(detailLbl);

            row.AddChild(col);
            _container.AddChild(row);
        }

        if (records.Count > MaxRows)
            _container.AddChild(MakeStubLabel(
                $"…and {records.Count - MaxRows} older entries."));
    }
}
