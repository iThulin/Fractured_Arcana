using Godot;
using System.Collections.Generic;   // GetValueOrDefault extension
using System.Text;

// ============================================================
// NegotiationTelemetry.cs
//
// Purpose:        One CSV row per resolved negotiation, appended
//                 to user://negotiation_telemetry.csv. The
//                 playtest data source for the tuning loop.
//                 v3 columns: goodwill at close, par, surplus,
//                 grievance, squeeze flags, token plays.
//                 Writes a NEW file name so v2 rows never
//                 misalign with v3 columns.
// Layer:          System (write-only sink; no game reads)
// Collaborators:  NegotiationManager.cs (caller, at resolution),
//                 NegotiationState.cs (PlayedCounts + flags),
//                 DealRecord.cs (outcome fields)
// ============================================================

/// <summary>Appends one CSV row per resolved negotiation to
/// user://negotiation_telemetry_v3.csv (header written on create).
/// Fire-and-forget: failures log and never interrupt play.</summary>
public static class NegotiationTelemetry
{
    private const string PATH = "user://negotiation_telemetry_v3.csv";

    private const string HEADER =
        "when,school,archetype,encounterId,outcome,stars,surplus,par,gold,rep,supplies,mood," +
        "turns,goodwillEnd,patienceLeft,grievance,squeezeOffered,squeezeHeld,squeezeBlinked," +
        "schoolMoveUsed,charm,persuade,connections,intimidate,demonstration,offering,insight,patience";

    public static void Record(DealRecord record, NegotiationState state)
    {
        try
        {
            bool fresh = !FileAccess.FileExists(PATH);
            using var file = fresh
                ? FileAccess.Open(PATH, FileAccess.ModeFlags.Write)
                : FileAccess.Open(PATH, FileAccess.ModeFlags.ReadWrite);
            if (file == null)
            {
                GD.PrintErr($"NegotiationTelemetry: cannot open {PATH}");
                return;
            }
            if (fresh) file.StoreLine(HEADER);
            else file.SeekEnd();

            var sb = new StringBuilder();
            sb.Append(record.When).Append(',');
            sb.Append(state.School).Append(',');
            sb.Append(record.Archetype).Append(',');
            sb.Append(record.EncounterId).Append(',');
            sb.Append(record.Outcome).Append(',');
            sb.Append(record.Stars).Append(',');
            sb.Append(record.Score).Append(',');
            sb.Append(state.Par).Append(',');
            sb.Append(record.Gold).Append(',');
            sb.Append(record.Reputation).Append(',');
            sb.Append(record.Supplies).Append(',');
            sb.Append(record.Zone).Append(',');
            sb.Append(record.Turns).Append(',');
            sb.Append(state.Goodwill).Append(',');
            sb.Append(state.Patience).Append(',');
            sb.Append(state.HasGrievance ? 1 : 0).Append(',');
            sb.Append(state.SqueezeWasOffered ? 1 : 0).Append(',');
            sb.Append(state.SqueezeWasHeld ? 1 : 0).Append(',');
            sb.Append(state.SqueezeDidBlink ? 1 : 0).Append(',');
            sb.Append(state.SchoolMoveUsed ? 1 : 0).Append(',');
            var toks = new[]
            {
                LeverageToken.Charm, LeverageToken.Persuade, LeverageToken.Connections,
                LeverageToken.Intimidate, LeverageToken.Demonstration,
                LeverageToken.Offering, LeverageToken.Insight, LeverageToken.Patience,
            };
            for (int i = 0; i < toks.Length; i++)
            {
                sb.Append(state.PlayedCounts.GetValueOrDefault(toks[i]));
                if (i < toks.Length - 1) sb.Append(',');
            }
            file.StoreLine(sb.ToString());
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"NegotiationTelemetry: {e.Message}");
        }
    }
}
