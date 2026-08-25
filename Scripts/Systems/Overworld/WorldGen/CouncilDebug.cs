using Godot;
using System.Linq;
using System.Text;

// ============================================================
// CouncilDebug.cs
//
// Purpose:        Verification tooling for the C4 (Word Spreads)
//                 test sessions. Two dumps:
//                   DumpEchoes()  prints every echo in flight, with
//                                   deed tag, valence, magnitude,
//                                   landing lunation, and cancel
//                                   flag. The direct assertion
//                                   surface for C3 (one-echo
//                                   priority), E2 (cancel removes
//                                   from selection), and F
//                                   (dissipation removal).
//                   DumpRegard()  prints a per-court courtier table with
//                                   exact Regard integers, for the
//                                   E3 / F zero-movement pre/post
//                                   comparisons.
//                 Both print to the Output panel. Read-only:
//                 neither mutates state nor marks the save dirty.
// Layer:          System (debug)
// Collaborators:  SaveManager.cs (ActiveSave.Cycle),
//                 CouncilState.cs (EchoesInFlight, Courts),
//                 CouncilTick.cs (CourtDisplayName, OfficeDisplay)
// See:            court_council_system_v1_1.docx §7, §13;
//                 C4 verification handoff (Sessions C–F)
//
// Usage: wired to the CampusScreen debug panel buttons, or call
// directly from any debug hook:
//   CouncilDebug.DumpEchoes();
//   CouncilDebug.DumpRegard();            // all courts
//   CouncilDebug.DumpRegard("kingdom_3"); // one court
// ============================================================

public static class CouncilDebug
{
    /// <summary>Print every echo in flight: deed tag, valence,
    /// magnitude, landing lunation (or due/cancelled status).
    /// Read-only.</summary>
    public static void DumpEchoes()
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null)
        {
            GD.Print("[CouncilDebug] No active cycle/council. Load a save with a generated world first.");
            return;
        }

        int now = cycle.Calendar.CurrentLunation;
        var sb = new StringBuilder();
        sb.AppendLine($"=== ECHOES IN FLIGHT, lunation {now}: {council.EchoesInFlight.Count} echo(es) ===");

        if (council.EchoesInFlight.Count == 0)
        {
            sb.AppendLine("  (flight empty)");
        }
        else
        {
            int i = 0;
            foreach (var e in council.EchoesInFlight)
            {
                string valence = e.Valence > 0 ? "+" : "\u2212";
                string mag = e.IsMajor ? "MAJOR" : "minor";
                string status;
                if (e.Cancelled)
                {
                    status = $"CANCELLED (would land L{e.LandsOnLunation})";
                }
                else if (e.LandsOnLunation <= now)
                {
                    status = "DUE, lands at next tick";
                }
                else
                {
                    status = $"lands L{e.LandsOnLunation}";
                }
                sb.AppendLine($"  [{i}] {e.KingdomId,-14} {e.DeedTag,-30} {valence} {mag,-6} {status}");
                i++;
            }
        }
        GD.Print(sb.ToString());
    }

    /// <summary>Print every courtier's exact Regard, Influence,
    /// office, and archetype, per court, plus the court's standing
    /// band. Pass a kingdom id to restrict to one court. Read-only.
    /// Use for pre/post landing comparisons (E3, F: assert zero
    /// movement).</summary>
    public static void DumpRegard(string kingdomId = null)
    {
        var cycle = SaveManager.ActiveSave?.Cycle;
        var council = cycle?.Council;
        if (council == null)
        {
            GD.Print("[CouncilDebug] No active cycle/council. Load a save with a generated world first.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== COURT REGARD, lunation {cycle.Calendar.CurrentLunation} ===");

        bool any = false;
        foreach (var kvp in council.Courts.OrderBy(k => k.Key))
        {
            if (kingdomId != null && kvp.Key != kingdomId)
            {
                continue;
            }
            any = true;
            var court = kvp.Value;
            sb.AppendLine($"  {kvp.Key} ({CouncilTick.CourtDisplayName(cycle, kvp.Key)}), standing: {court.Band()}");
            foreach (var c in court.Courtiers)
            {
                string regard = (c.Regard > 0 ? "+" : "") + c.Regard;
                sb.AppendLine($"    {c.DisplayName,-24} {CouncilTick.OfficeDisplay(c.Office),-14} " +
                              $"arch={c.Archetype,-12} I={c.Influence}  Regard={regard}");
            }
        }
        if (!any)
        {
            sb.AppendLine(kingdomId != null
                ? $"  (no court found for '{kingdomId}')"
                : "  (no courts)");
        }
        GD.Print(sb.ToString());
    }
}
