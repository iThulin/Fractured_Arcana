using Godot;
using System;
using System.Text;

// ============================================================
// RunEventLog.cs
//
// Purpose:        Whole-run event journal. One READABLE .log and
//                 one machine-readable .csv per expedition under
//                 user://run_logs/ — every combat, negotiation,
//                 narrative choice, POI, drain, and the run-end
//                 banking math, each with resource deltas AND
//                 running totals. The playtest answer to "where
//                 did my gold actually go?" Third sink beside
//                 CombatTelemetry (per-fight/per-cast) and
//                 NegotiationTelemetry (per-table); this one is
//                 the chronological spine that stitches a run
//                 together.
// Layer:          System (write-only sink; no game reads)
// Collaborators:  ExpeditionManager.cs (sole caller today —
//                 Begin at deploy, Event via its LogRun helper,
//                 End from the three run-end paths)
// Notes:          Static state survives combat/negotiation scene
//                 round-trips (same process), so the run's files
//                 stay open-for-append across them. Every write
//                 is an immediate line-append: a crash loses
//                 nothing already logged. Fire-and-forget —
//                 failures print and never interrupt play.
// ============================================================

/// <summary>Per-run chronological event journal: paired
/// <c>user://run_logs/run_&lt;id&gt;.log</c> (human-readable) and
/// <c>.csv</c> (analysis) files. Call <see cref="Begin"/> at fresh
/// deploy, <see cref="Event"/> for everything that happens, and
/// <see cref="End"/> from extraction/failure. Auto-opens an
/// "unknown" run if an Event arrives with no Begin (direct-launched
/// scenes), mirroring CombatTelemetry.</summary>
public static class RunEventLog
{
    /// <summary>Master switch. On in debug builds, off in release —
    /// playtester builds that should record must set this true
    /// explicitly (and say so to the tester).</summary>
#if DEBUG
    public static bool Enabled = true;
#else
    public static bool Enabled = false;
#endif

    private const string Dir = "user://run_logs";

    private static string _logPath;
    private static string _csvPath;
    private static string _runId;
    private static int _seq;

    private const string CsvHeader =
        "run_id,when_utc,seq,event,detail,gold_delta,splinter_delta,hp_delta,steps_delta," +
        "gold_total,splinter_total,hp,steps_remaining,coord";

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Opens a new run journal. Call ONLY on a fresh deploy —
    /// combat/negotiation returns must NOT re-begin (statics carry the
    /// open run across the scene swap).</summary>
    public static void Begin(string regionId, string school,
                             int gold, int splinters, int hp, int maxHp, int steps)
    {
        if (!Enabled) return;
        try
        {
            _runId = $"{DateTime.Now:yyyyMMdd_HHmmss}";
            _logPath = $"{Dir}/run_{_runId}.log";
            _csvPath = $"{Dir}/run_{_runId}.csv";
            _seq = 0;

            AppendLog("════════════════════════════════════════════════════════════");
            AppendLog($" EXPEDITION RUN {_runId}");
            AppendLog($" Region: {regionId}   School: {school}");
            AppendLog($" Start:  HP {hp}/{maxHp}   Steps {steps}   Gold {gold}   Splinters {splinters}");
            AppendLog("════════════════════════════════════════════════════════════");

            AppendCsvRow("run_start", $"region={regionId} school={school}",
                          0, 0, 0, 0, gold, splinters, hp, steps, "");
        }
        catch (Exception e) { GD.PrintErr($"RunEventLog.Begin: {e.Message}"); }
    }

    /// <summary>Appends one event to both files. Deltas are what this
    /// event changed; totals are the state AFTER the event. Pass "" for
    /// coord when position is meaningless (run-level events).</summary>
    public static void Event(string type, string detail,
                             int goldDelta, int splinterDelta, int hpDelta, int stepsDelta,
                             int goldTotal, int splinterTotal, int hp, int stepsRemaining,
                             string coord = "")
    {
        if (!Enabled) return;
        try
        {
            if (_logPath == null)
                Begin("unknown", "unknown", goldTotal, splinterTotal, hp, hp, stepsRemaining);

            _seq++;

            var deltas = new StringBuilder();
            if (goldDelta != 0)     deltas.Append($" {Sign(goldDelta)}g");
            if (splinterDelta != 0) deltas.Append($" {Sign(splinterDelta)}sp");
            if (hpDelta != 0)       deltas.Append($" {Sign(hpDelta)}hp");
            if (stepsDelta != 0)    deltas.Append($" {Sign(stepsDelta)}st");
            string deltaStr = deltas.Length > 0 ? $"[{deltas.ToString().Trim()}]" : "";

            string at = string.IsNullOrEmpty(coord) ? "" : $" @({coord})";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] #{_seq:D3} {type,-18} {deltaStr,-22} {detail}" +
                      $"  |  G:{goldTotal} S:{splinterTotal} HP:{hp} St:{stepsRemaining}{at}");

            AppendCsvRow(type, detail, goldDelta, splinterDelta, hpDelta, stepsDelta,
                          goldTotal, splinterTotal, hp, stepsRemaining, coord);
        }
        catch (Exception e) { GD.PrintErr($"RunEventLog.Event: {e.Message}"); }
    }

    /// <summary>Closes the run journal with a summary block. Call from
    /// every run-end path (extract / emergency / fail) AFTER banking, so
    /// the banked-vs-forfeited outcome is on record.</summary>
    public static void End(string outcome, string detail,
                           int gold, int splinters, int encountersWon,
                           int hp, int stepsRemaining, bool goldBanked,
                           int materials = 0, int supplies = 0)
    {
        if (!Enabled || _logPath == null) return;
        try
        {
            AppendCsvRow("run_end", $"{outcome}: {detail}",
                          0, 0, 0, 0, gold, splinters, hp, stepsRemaining, "");

            AppendLog("════════════════════════════════════════════════════════════");
            AppendLog($" RUN END — {outcome.ToUpperInvariant()}   ({detail})");
            AppendLog($" Encounters won: {encountersWon}   HP left: {hp}   Steps left: {stepsRemaining}");
            // Materials/supplies appear only when nonzero — most runs carry none.
            string extras = (materials != 0 ? $" + {materials} materials" : "")
                          + (supplies != 0 ? $" + {supplies} supplies" : "");
            AppendLog(goldBanked
                ? $" BANKED: {gold} gold + {splinters} splinters{extras} → guild treasury"
                : $" FORFEITED: {gold} gold + {splinters} splinters{extras} lost (not banked — run failed).");
            AppendLog("════════════════════════════════════════════════════════════");
        }
        catch (Exception e) { GD.PrintErr($"RunEventLog.End: {e.Message}"); }
        finally { _logPath = null; _csvPath = null; _runId = null; }
    }

    // ═══════════════════════════════════════════════════════════════
    // File plumbing (CombatTelemetry pattern)
    // ═══════════════════════════════════════════════════════════════

    private static void AppendLog(string line)
        => AppendLine(_logPath, null, line);

    private static void AppendCsvRow(string type, string detail,
                                     int gD, int sD, int hD, int stD,
                                     int g, int s, int hp, int st, string coord)
        => AppendLine(_csvPath, CsvHeader, Csv(
            _runId, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), _seq.ToString(),
            type, detail ?? "",
            gD.ToString(), sD.ToString(), hD.ToString(), stD.ToString(),
            g.ToString(), s.ToString(), hp.ToString(), st.ToString(), coord ?? ""));

    private static void AppendLine(string path, string header, string line)
    {
        if (path == null) return;
        if (!DirAccess.DirExistsAbsolute(Dir))
            DirAccess.MakeDirRecursiveAbsolute(Dir);

        bool existed = FileAccess.FileExists(path);
        using var f = existed
            ? FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[RunEventLog] Cannot open {path}: {FileAccess.GetOpenError()}");
            return;
        }
        if (existed)
            f.SeekEnd();
        else if (header != null)
            f.StoreLine(header);
        f.StoreLine(line);
    }

    private static string Sign(int v) => v > 0 ? $"+{v}" : v.ToString();

    /// <summary>Joins fields into a CSV line, quoting anything containing
    /// a comma, quote, or newline.</summary>
    private static string Csv(params string[] fields)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            string v = fields[i] ?? "";
            if (v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0)
                sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
            else
                sb.Append(v);
        }
        return sb.ToString();
    }
}
