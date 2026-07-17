// ============================================================
// DealRecord.cs
//
// Purpose:        One negotiation outcome, remembered forever —
//                 the Hall of Records entry (negotiation doc
//                 §7b Deal Quality). Append-only, every table,
//                 every timeline: signed deals, walkaways,
//                 collapses. Lives on EternalLedger because the
//                 loom remembers deals even when the timeline
//                 that made them is gone. Record only — grants
//                 no power (tier-3 rule).
// Layer:          Data
// Collaborators:  EternalLedger.cs (owning list),
//                 NegotiationManager.cs (writer, on resolution),
//                 CampusScreen.cs (Records tab reader)
// See:            negotiation_redesign_v1.md Phase 5;
//                 negotiation_system.docx §7b
// ============================================================

/// <summary>One resolved negotiation, as the Hall of Records remembers it.
/// Written by NegotiationManager for EVERY resolution (any outcome);
/// rendered by the campus Records tab, newest first.</summary>
public class DealRecord
{
    /// <summary>Cycle the table happened in (timelines die; records don't).</summary>
    public int CycleNumber = 0;

    /// <summary>ISO-8601 UTC wall-clock timestamp, for slot bookkeeping.</summary>
    public string When = "";

    public string EncounterId = "";
    public string Title = "";
    public string NpcName = "";
    public string Archetype = "";
    public string FactionId = "";

    /// <summary>"Signed", "WalkedAway", "TheyLeft" (patience ran out),
    /// or "Collapsed" (tension hit 10).</summary>
    public string Outcome = "";

    /// <summary>Deal Quality stars (1–5) when signed; 0 otherwise.</summary>
    public int Stars = 0;

    /// <summary>Raw position-weighted deal score (see NegotiationState.GetDealScore).</summary>
    public int Score = 0;

    public int Gold = 0;
    public int Reputation = 0;

    /// <summary>Tension zone at resolution: "Cordial" / "Strained" / "Hostile".</summary>
    public string Zone = "";

    /// <summary>Turns the table lasted.</summary>
    public int Turns = 0;

    /// <summary>S4: spell taught by a cordial close, or "".</summary>
    public string SpellGranted = "";
}
