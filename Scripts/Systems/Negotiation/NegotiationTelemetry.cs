using Godot;
using System.Collections.Generic;   // GetValueOrDefault extension
using System.Text;

// ============================================================
// NegotiationTelemetry.cs
//
// Purpose:        One CSV row per resolved negotiation, appended
//                 to user://negotiation_telemetry.csv — the
//                 playtest data source for the tuning loop
//                 (claude/negotiation_tuning_v1.md). Mirrors the
//                 CombatTelemetry pattern. Read the file into a
//                 spreadsheet (or hand it to Claude) after a
//                 playtest session; delete it to start a fresh
//                 sample after changing NegotiationTuning values.
// Layer:          System (write-only sink; no game reads)
// Collaborators:  NegotiationManager.cs (caller, at resolution),
//                 NegotiationState.cs (PlayedCounts + squeeze
//                 flags), DealRecord.cs (outcome fields)
// ============================================================

/// <summary>Appends one CSV row per resolved negotiation to
/// user://negotiation_telemetry.csv (header written on create).
/// Fire-and-forget: failures log and never interrupt play.</summary>
public static class NegotiationTelemetry
{
    private const string PATH = "user://negotiation_telemetry.csv";

    private const string HEADER =
        "when,school,archetype,encounterId,outcome,stars,score,gold,rep,zone," +
        "turns,tensionEnd,patienceLeft,squeezeOffered,squeezeHeld,squeezeBlinked," +
        "schoolMoveUsed,charm,persuade,connections,intimidate,demonstration," +
        "offering,insight,patience";

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
            sb.Append(record.Gold).Append(',');
            sb.Append(record.Reputation).Append(',');
            sb.Append(record.Zone).Append(',');
            sb.Append(record.Turns).Append(',');
            sb.Append(state.Tension).Append(',');
            sb.Append(state.NpcPatience).Append(',');
            sb.Append(state.SqueezeWasOffered ? 1 : 0).Append(',');
            sb.Append(state.SqueezeWasHeld ? 1 : 0).Append(',');
            sb.Append(state.SqueezeDidBlink ? 1 : 0).Append(',');
            sb.Append(state.SchoolMoveUsed ? 1 : 0).Append(',');
            foreach (var tok in new[]
            {
                LeverageToken.Charm, LeverageToken.Persuade, LeverageToken.Connections,
                LeverageToken.Intimidate, LeverageToken.Demonstration,
                LeverageToken.Offering, LeverageToken.Insight, LeverageToken.Patience,
            })
            {
                sb.Append(state.PlayedCounts.GetValueOrDefault(tok));
                if (tok != LeverageToken.Patience) sb.Append(',');
            }
            file.StoreLine(sb.ToString());
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"NegotiationTelemetry: {e.Message}");
        }
    }
}
