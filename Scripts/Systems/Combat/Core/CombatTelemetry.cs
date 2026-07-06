using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// CombatTelemetry.cs
//
// Purpose:        Balance-signal telemetry. Appends one row per
//                 fight to fights.csv, one row per successful
//                 card cast to card_events.csv, and maintains
//                 per-blueprint lifetime aggregates in
//                 cards_lifetime.csv — all under user://telemetry/.
//                 Gated on the Enabled flag; zero allocation cost
//                 when disabled. Analysis happens outside the game
//                 (spreadsheet / pandas); this class only records.
// Layer:          System
// Collaborators:  CombatManager.cs (BeginFight / RecordCardCast /
//                 EndFight call sites), PlayerSession (school)
// See:            docs/build_order_v3.md — balance triage plan
// ============================================================
//
// Triage queries this data answers:
//   never-cast cards      → cards_lifetime rows with total_casts 0
//                           (join against CardDatabase for the full list)
//   auto-includes         → high casts-per-fight in card_events
//   win-rate correlation  → wins_when_cast vs losses_when_cast
//   school deltas         → fights.csv result grouped by school

/// <summary>
/// Static telemetry recorder. Call <see cref="BeginFight"/> when an encounter's
/// composition is queued, <see cref="RecordCardCast"/> on every successful cast,
/// and <see cref="EndFight"/> from the victory/defeat branches. All writes are
/// line-appends at event time, so a crash mid-fight loses nothing already logged.
/// </summary>
public static class CombatTelemetry
{
    /// <summary>Master switch. On in debug builds, off in release — playtester builds that should record must set this true explicitly (and say so to the tester).</summary>
#if DEBUG
    public static bool Enabled = true;
#else
    public static bool Enabled = false;
#endif

    private const string Dir = "user://telemetry";
    private const string FightsPath = Dir + "/fights.csv";
    private const string EventsPath = Dir + "/card_events.csv";
    private const string LifetimePath = Dir + "/cards_lifetime.csv";

    private const string FightsHeader = "fight_id,started_utc,ended_utc,encounter_id,region_id,tier,school,enemies,enemy_count,rounds,result,casts";
    private const string EventsHeader = "fight_id,ts_utc,round,blueprint_id,half,school,mana";
    private const string LifetimeHeader = "blueprint_id,total_casts,fights_cast_in,wins_when_cast,losses_when_cast,last_cast_utc";

    // ── Current-fight state ─────────────────────────────────────────
    private static string _fightId;
    private static string _startedUtc;
    private static string _encounterId = "";
    private static string _regionId = "";
    private static string _tier = "";
    private static string _school = "";
    private static string _enemies = "";
    private static int _enemyCount;
    private static int _casts;
    private static readonly HashSet<string> _castThisFight = new();

    // ── Lifetime aggregates (lazy-loaded, rewritten at fight end) ───
    private sealed class Lifetime
    {
        public int TotalCasts;
        public int FightsCastIn;
        public int Wins;
        public int Losses;
        public string LastCastUtc = "";
    }
    private static Dictionary<string, Lifetime> _lifetime;

    // ═══════════════════════════════════════════════════════════════
    // Public API — the three hooks
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Opens a fight record. Safe to call again before EndFight (the previous unfinished fight is dropped — e.g. a restarted debug scene).</summary>
    public static void BeginFight(string encounterId, string regionId, string tier, IEnumerable<string> enemyKinds)
    {
        if (!Enabled) return;

        _fightId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{GD.Randi() % 10000:D4}";
        _startedUtc = Utc();
        _encounterId = encounterId ?? "";
        _regionId = regionId ?? "";
        _tier = tier ?? "";
        _school = PlayerSession.SelectedSchool.ToString();
        _casts = 0;
        _castThisFight.Clear();

        var kinds = new List<string>(enemyKinds ?? Array.Empty<string>());
        _enemyCount = kinds.Count;
        _enemies = string.Join("|", kinds);
    }

    /// <summary>Records one successful cast. Called next to CastMasteryTracker.RecordCast. Auto-opens an "unknown" fight if no BeginFight ran (direct-launched combat scenes).</summary>
    public static void RecordCardCast(string blueprintId, string half, string school, int mana, int round)
    {
        if (!Enabled) return;
        if (_fightId == null)
            BeginFight("unknown", "", "", null);

        _casts++;
        if (!string.IsNullOrEmpty(blueprintId))
        {
            _castThisFight.Add(blueprintId);
            _castsPerBlueprint[blueprintId] = _castsPerBlueprint.GetValueOrDefault(blueprintId) + 1;
        }

        AppendLine(EventsPath, EventsHeader, Csv(
            _fightId, Utc(), round.ToString(), blueprintId ?? "", half ?? "", school ?? "", mana.ToString()));
    }

    /// <summary>Closes the fight record and folds this fight into the lifetime aggregates. No-op when no fight is open.</summary>
    public static void EndFight(bool victory, int rounds)
    {
        if (!Enabled || _fightId == null) return;

        AppendLine(FightsPath, FightsHeader, Csv(
            _fightId, _startedUtc, Utc(), _encounterId, _regionId, _tier, _school,
            _enemies, _enemyCount.ToString(), rounds.ToString(),
            victory ? "victory" : "defeat", _casts.ToString()));

        UpdateLifetime(victory);
        _fightId = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifetime aggregates
    // ═══════════════════════════════════════════════════════════════

    private static void UpdateLifetime(bool victory)
    {
        _lifetime ??= LoadLifetime();
        string now = Utc();

        foreach (var id in _castThisFight)
        {
            if (!_lifetime.TryGetValue(id, out var row))
                _lifetime[id] = row = new Lifetime();
            row.FightsCastIn++;
            if (victory) row.Wins++; else row.Losses++;
            row.LastCastUtc = now;
        }

        // TotalCasts counts every cast (not distinct-per-fight); fed by the
        // per-blueprint counter RecordCardCast maintains during the fight.
        foreach (var kv in _castsPerBlueprint)
        {
            if (!_lifetime.TryGetValue(kv.Key, out var row))
                _lifetime[kv.Key] = row = new Lifetime();
            row.TotalCasts += kv.Value;
        }
        _castsPerBlueprint.Clear();

        var sb = new StringBuilder();
        sb.AppendLine(LifetimeHeader);
        var ids = new List<string>(_lifetime.Keys);
        ids.Sort(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var r = _lifetime[id];
            sb.AppendLine(Csv(id, r.TotalCasts.ToString(), r.FightsCastIn.ToString(),
                              r.Wins.ToString(), r.Losses.ToString(), r.LastCastUtc));
        }

        EnsureDir();
        using var f = FileAccess.Open(LifetimePath, FileAccess.ModeFlags.Write);
        f?.StoreString(sb.ToString());
    }

    private static readonly Dictionary<string, int> _castsPerBlueprint = new();

    private static Dictionary<string, Lifetime> LoadLifetime()
    {
        var result = new Dictionary<string, Lifetime>();
        if (!FileAccess.FileExists(LifetimePath))
            return result;

        using var f = FileAccess.Open(LifetimePath, FileAccess.ModeFlags.Read);
        if (f == null) return result;

        bool first = true;
        while (!f.EofReached())
        {
            string line = f.GetLine();
            if (first) { first = false; continue; }        // header
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = line.Split(',');
            if (cols.Length < 6) continue;
            result[cols[0]] = new Lifetime
            {
                TotalCasts = ParseInt(cols[1]),
                FightsCastIn = ParseInt(cols[2]),
                Wins = ParseInt(cols[3]),
                Losses = ParseInt(cols[4]),
                LastCastUtc = cols[5]
            };
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // File plumbing
    // ═══════════════════════════════════════════════════════════════

    private static void AppendLine(string path, string header, string line)
    {
        EnsureDir();
        bool existed = FileAccess.FileExists(path);
        using var f = existed
            ? FileAccess.Open(path, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[Telemetry] Cannot open {path}: {FileAccess.GetOpenError()}");
            return;
        }
        if (existed)
            f.SeekEnd();
        else
            f.StoreLine(header);
        f.StoreLine(line);
    }

    private static void EnsureDir()
    {
        if (!DirAccess.DirExistsAbsolute(Dir))
            DirAccess.MakeDirRecursiveAbsolute(Dir);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static string Utc() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    /// <summary>Joins fields into a CSV line, quoting anything containing a comma, quote, or newline.</summary>
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
