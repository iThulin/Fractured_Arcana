using System.Collections.Generic;

// ============================================================
// HeraldReport.cs
//
// Purpose:        One attributed line of the Herald's Report.
//                 Replaces the flat List<string> in CouncilTick so
//                 lines carry queryable structure. KingdomId is the
//                 unlock for court-card echo history (D2). Lunation
//                 separator rows are gone: lunation is a field now,
//                 so display groups by it instead of parsing markers.
//
//                 Save-adjacent: persisted inside CouncilState (cycle
//                 tier). Plain public fields only, with no delegates,
//                 nodes, or refs, so IncludeFields carries it cleanly.
//                 Round-trip asserted before ship (paranoia rule).
// Layer:          System
// Collaborators:  CouncilTick.cs (appends), CouncilState.cs (holds),
//                 CouncilPanel.cs (renders), SaveManager (serializes)
// See:            court_council_system_v1_1.docx §8 (Herald's Report)
// ============================================================

/// <summary>One attributed Herald's Report line. KingdomId is "" for
/// guild-wide lines (no single court source); a real id for court-
/// sourced lines, which is what court-card echo history queries on.</summary>
public class HeraldReport
{
    public int Lunation;
    public string KingdomId = "";
    public string Text = "";
}
