using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

// ============================================================
// CardVerifier.cs
//
// Purpose:        Static verification pass over Data/Cards/*.json.
//                 Checks that every card parses, every effect/
//                 predicate/targeter type resolves against the
//                 CardScriptRegistry (catching the "card silently
//                 no-ops" bug — README §7), element tags are valid
//                 (the earth-vs-stone pip trap), required fields
//                 are present, and ids are unique. Writes a full
//                 report to user://card_verification.txt.
// Layer:          Dev tooling
// Collaborators:  CardScriptRegistry (partial — registry queries
//                 below), JsonCardLoader (schema conventions),
//                 GameBootstrap.cs (F9 hotkey / --verify-cards)
// Usage:          F9 in a debug build, or:
//                 godot --headless -- --verify-cards
// ============================================================
//
// A card that PASSES here is functionally loadable: it parses, all
// its script types resolve, and its effects construct without
// throwing. This is the gate for flipping status wip → ready.
// It says nothing about balance — that is telemetry's job.

/// <summary>Registry queries for the verifier. Lives in the same partial class so it can read the private factory tables without widening their surface.</summary>
public static partial class CardScriptRegistry
{
    /// <summary>True when an effect factory is registered for the JSON `type` key.</summary>
    public static bool HasEffect(string key)
        => key != null && _effects.ContainsKey(key.ToLowerInvariant());

    /// <summary>True when a predicate factory is registered for the JSON `type` key.</summary>
    public static bool HasPredicate(string key)
        => key != null && _predicates.ContainsKey(key.ToLowerInvariant());

    /// <summary>True when a targeter factory is registered for the JSON `type` key.</summary>
    public static bool HasTargeter(string key)
        => key != null && _targeters.ContainsKey(key.ToLowerInvariant());
}

/// <summary>
/// Verification harness over the card JSON directory. Call <see cref="RunAndReport"/>
/// from a debug hotkey or the --verify-cards command line. Errors are load-breaking
/// or silent-no-op problems; warnings are suspicious-but-loadable. A card with zero
/// errors is safe to flip to "ready".
/// </summary>
public static class CardVerifier
{
    private const string CardsDir = "res://Data/Cards";
    private const string ReportPath = "user://card_verification.txt";

    /// <summary>The element tag vocabulary — must match ElementColors.Get. "stone" is the known legacy trap (renders a broken pip); the checker names it explicitly. Ruled 2026-07-06: growth + glyph added; charm/ward/binding fold to enchant, summon/beast/flock/hex fold to growth.</summary>
    private static readonly HashSet<string> ValidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "fire", "ice", "storm", "earth", "arcane",
        "necrotic", "spirit", "temporal", "enchant", "construct",
        "growth", "glyph"
    };

    // ── Per-run state ───────────────────────────────────────────────
    private sealed class RunState
    {
        public readonly List<string> Errors = new();
        public readonly List<string> Warnings = new();
        public readonly Dictionary<string, string> SeenIds = new();          // id → file
        public readonly Dictionary<string, int> StatusInventory = new();     // apply_status strings
        public readonly Dictionary<string, int> SummonInventory = new();     // summon unit kinds
        public readonly Dictionary<string, int[]> PerSchool = new();         // school → [ok, fail]
        public readonly Dictionary<string, int> ByStatus = new();            // ready/wip/stub counts
        public int Scanned;
    }

    // ═══════════════════════════════════════════════════════════════
    // Entry point
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Runs the full verification pass, prints a summary to the console, writes the detailed report to user://card_verification.txt. Returns true when no card produced an error.</summary>
    public static bool RunAndReport()
    {
        CardScriptRegistry.RegisterBuiltins(); // idempotent — factories are keyed assignments

        var run = new RunState();

        using var dir = DirAccess.Open(CardsDir);
        if (dir == null)
        {
            GD.PrintErr($"[CardVerifier] Cannot open {CardsDir}: {DirAccess.GetOpenError()}");
            return false;
        }

        dir.ListDirBegin();
        string file;
        while ((file = dir.GetNext()) != "")
        {
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            VerifyFile(file, run);
        }
        dir.ListDirEnd();

        string report = BuildReport(run);
        WriteReport(report);
        GD.Print(report);
        GD.Print($"[CardVerifier] Full report: {ProjectSettings.GlobalizePath(ReportPath)}");
        return run.Errors.Count == 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Per-file verification
    // ═══════════════════════════════════════════════════════════════

    private static void VerifyFile(string file, RunState run)
    {
        run.Scanned++;
        int errorsBefore = run.Errors.Count;
        string school = "unknown";

        string path = $"{CardsDir}/{file}";
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            run.Errors.Add($"{file} — cannot open: {FileAccess.GetOpenError()}");
            return;
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(f.GetAsText()).RootElement;
        }
        catch (Exception ex)
        {
            run.Errors.Add($"{file} — JSON parse failure: {ex.Message}");
            return;
        }

        // ── Top-level fields ────────────────────────────────────────
        string id = GetString(root, "id");
        if (string.IsNullOrEmpty(id))
            run.Errors.Add($"{file} — missing or empty 'id'");
        else if (run.SeenIds.TryGetValue(id, out var other))
            run.Errors.Add($"{file} — duplicate id '{id}' (also in {other})");
        else
            run.SeenIds[id] = file;

        if (string.IsNullOrEmpty(GetString(root, "name")))
            run.Errors.Add($"{file} — missing or empty 'name'");

        string schoolStr = GetString(root, "school");
        if (schoolStr == null || !Enum.TryParse<CardSchool>(schoolStr, true, out _))
            run.Errors.Add($"{file} — 'school' missing or not a CardSchool: '{schoolStr}'");
        else
            school = schoolStr.ToLowerInvariant();

        string status = GetString(root, "status")?.ToLowerInvariant() ?? "(missing)";
        if (status != "ready" && status != "wip" && status != "stub")
            run.Errors.Add($"{file} — invalid status '{status}' (ready|wip|stub)");
        run.ByStatus[status] = run.ByStatus.GetValueOrDefault(status) + 1;

        string rarity = GetString(root, "rarity");
        if (rarity != null && !Enum.TryParse<CardRarity>(rarity, true, out _))
            run.Warnings.Add($"{file} — unknown rarity '{rarity}'");

        // ── Halves ──────────────────────────────────────────────────
        bool hasTop = root.TryGetProperty("top", out var top);
        bool hasBottom = root.TryGetProperty("bottom", out var bottom);
        if (!hasTop && !hasBottom)
            run.Errors.Add($"{file} — card has neither 'top' nor 'bottom' half");
        if (hasTop) VerifyHalf(top, $"{file}:top", run);
        if (hasBottom) VerifyHalf(bottom, $"{file}:bottom", run);

        // Upgrade tiers patch effect subtrees in via field paths — walk any
        // patch value that is itself a node, or a leaf the checks care about.
        if (root.TryGetProperty("upgrades", out var upgrades) && upgrades.ValueKind == JsonValueKind.Array)
            VerifyUpgrades(upgrades, file, run);

        // ── Per-school tally ────────────────────────────────────────
        if (!run.PerSchool.TryGetValue(school, out var tally))
            run.PerSchool[school] = tally = new int[2];
        tally[run.Errors.Count == errorsBefore ? 0 : 1]++;
    }

    private static void VerifyHalf(JsonElement half, string where, RunState run)
    {
        if (!half.TryGetProperty("mana", out var mana) || mana.ValueKind != JsonValueKind.Number)
            run.Errors.Add($"{where} — missing numeric 'mana'");

        string speed = GetString(half, "speed");
        if (speed != null && !Enum.TryParse<PlaySpeed>(speed, true, out _))
            run.Errors.Add($"{where} — unknown speed '{speed}'");

        // Element pips (JSON `tags`) must be in the ElementColors vocabulary.
        if (half.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
                CheckElement(t.GetString(), $"{where}.tags", run);

        if (half.TryGetProperty("targeting", out var targeting))
            WalkTargeter(targeting, $"{where}.targeting", run);

        if (half.TryGetProperty("effect", out var effect))
        {
            WalkEffect(effect, $"{where}.effect", run);

            // Construction test: unknown types already reported by the walk;
            // this catches factories that throw on missing required properties
            // (e.g. damage without amount, summon without unit).
            try
            {
                CardScriptRegistry.BuildEffect(effect);
            }
            catch (Exception ex)
            {
                run.Errors.Add($"{where} — effect throws on build: {ex.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Upgrade patches (CardUpgradeApplier field-path format)
    // ═══════════════════════════════════════════════════════════════

    private static void VerifyUpgrades(JsonElement upgrades, string file, RunState run)
    {
        int ui = 0;
        foreach (var upgrade in upgrades.EnumerateArray())
        {
            if (upgrade.TryGetProperty("changes", out var changes)
                && changes.ValueKind == JsonValueKind.Array)
            {
                int ci = 0;
                foreach (var change in changes.EnumerateArray())
                {
                    string field = GetString(change, "field") ?? "";
                    string where = $"{file}:upgrades[{ui}].changes[{ci}] ({field})";

                    if (change.TryGetProperty("value", out var value))
                    {
                        string leaf = field.Contains('.') ? field[(field.LastIndexOf('.') + 1)..] : field;

                        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out _))
                        {
                            // A whole node patched in — route to the right walker.
                            if (leaf == "targeting")
                                WalkTargeter(value, where, run);
                            else if (leaf == "if" || leaf == "predicate")
                                WalkPredicate(value, where, run);
                            else
                                WalkEffect(value, where, run);
                        }
                        else if (leaf == "element" && value.ValueKind == JsonValueKind.String)
                            CheckElement(value.GetString(), where, run);
                        else if (leaf == "tags" && value.ValueKind == JsonValueKind.Array)
                            foreach (var t in value.EnumerateArray())
                                CheckElement(t.GetString(), where, run);
                        else if (leaf == "status" && value.ValueKind == JsonValueKind.String)
                        {
                            string sv = value.GetString();
                            if (!string.IsNullOrEmpty(sv))
                                run.StatusInventory[sv] = run.StatusInventory.GetValueOrDefault(sv) + 1;
                        }
                    }
                    ci++;
                }
            }
            ui++;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Recursive walks — mirror the composite shapes RegisterBuiltins defines
    // ═══════════════════════════════════════════════════════════════

    private static void WalkEffect(JsonElement n, string path, RunState run)
    {
        if (n.ValueKind == JsonValueKind.Null)
            return;
        if (n.ValueKind != JsonValueKind.Object)
        {
            run.Errors.Add($"{path} — effect node is not an object");
            return;
        }

        string type = GetString(n, "type")?.ToLowerInvariant();
        if (type == null)
        {
            run.Errors.Add($"{path} — effect node has no 'type'");
            return;
        }
        if (!CardScriptRegistry.HasEffect(type))
        {
            run.Errors.Add($"{path} — unknown effect type '{type}' (would silently no-op)");
            return;
        }

        CheckElementProperty(n, path, run);
        CollectInventory(n, type, run);

        // Composite recursion
        switch (type)
        {
            case "sequence":
                if (n.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var s in steps.EnumerateArray())
                        WalkEffect(s, $"{path}.steps[{i++}]", run);
                }
                else
                    run.Errors.Add($"{path} — sequence without 'steps' array");
                break;

            case "conditional":
                if (n.TryGetProperty("if", out var pred))
                    WalkPredicate(pred, $"{path}.if", run);
                else
                    run.Errors.Add($"{path} — conditional without 'if'");
                if (n.TryGetProperty("then", out var then))
                    WalkEffect(then, $"{path}.then", run);
                else
                    run.Errors.Add($"{path} — conditional without 'then'");
                if (n.TryGetProperty("else", out var els))
                    WalkEffect(els, $"{path}.else", run);
                break;

            case "for_each_target":
                if (n.TryGetProperty("do", out var feDo))
                    WalkEffect(feDo, $"{path}.do", run);
                else
                    run.Errors.Add($"{path} — for_each_target without 'do'");
                break;

            case "retarget":
                if (n.TryGetProperty("targeting", out var rt))
                    WalkTargeter(rt, $"{path}.targeting", run);
                else
                    run.Errors.Add($"{path} — retarget without 'targeting'");
                if (n.TryGetProperty("do", out var rtDo))
                    WalkEffect(rtDo, $"{path}.do", run);
                else
                    run.Errors.Add($"{path} — retarget without 'do'");
                break;
        }
    }

    private static void WalkPredicate(JsonElement n, string path, RunState run)
    {
        if (n.ValueKind == JsonValueKind.Null)
            return;

        string type = GetString(n, "type")?.ToLowerInvariant();
        if (type == null)
        {
            run.Errors.Add($"{path} — predicate node has no 'type'");
            return;
        }
        if (!CardScriptRegistry.HasPredicate(type))
        {
            run.Errors.Add($"{path} — unknown predicate type '{type}' (would default to AlwaysTrue)");
            return;
        }

        if (type == "and" || type == "or")
        {
            if (n.TryGetProperty("predicates", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var p in parts.EnumerateArray())
                    WalkPredicate(p, $"{path}.predicates[{i++}]", run);
            }
        }
        else if (type == "not" && n.TryGetProperty("predicate", out var inner))
            WalkPredicate(inner, $"{path}.predicate", run);
        else if (type == "has_elements_near_caster" && n.TryGetProperty("elements", out var els))
            foreach (var e in els.EnumerateArray())
                CheckElement(e.GetString(), path, run);
    }

    private static void WalkTargeter(JsonElement n, string path, RunState run)
    {
        if (n.ValueKind == JsonValueKind.Null)
            return;

        string type = GetString(n, "type")?.ToLowerInvariant();
        if (type == null)
        {
            run.Errors.Add($"{path} — targeting node has no 'type'");
            return;
        }
        if (!CardScriptRegistry.HasTargeter(type))
        {
            run.Errors.Add($"{path} — unknown targeter type '{type}' (would fall back to no targeting)");
            return;
        }
        CheckElementProperty(n, path, run);
    }

    // ═══════════════════════════════════════════════════════════════
    // Field checks and inventories
    // ═══════════════════════════════════════════════════════════════

    private static void CheckElementProperty(JsonElement n, string path, RunState run)
    {
        if (n.TryGetProperty("element", out var el))
            CheckElement(el.GetString(), path, run);
    }

    private static void CheckElement(string element, string path, RunState run)
    {
        if (string.IsNullOrEmpty(element) || ValidElements.Contains(element))
            return;
        if (element.Equals("stone", StringComparison.OrdinalIgnoreCase))
            run.Errors.Add($"{path} — element 'stone' is the legacy trap; the canonical tag is 'earth' (broken pip otherwise)");
        else
            run.Errors.Add($"{path} — unknown element '{element}' (valid: {string.Join(", ", ValidElements)})");
    }

    private static void CollectInventory(JsonElement n, string type, RunState run)
    {
        // Status strings are free-form in the schema; the report inventories them
        // so drift ("hasted" vs "haste") is visible at a glance rather than enforced
        // against a list that lives nowhere canonical yet.
        if (n.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
        {
            string key = s.GetString();
            if (!string.IsNullOrEmpty(key))
                run.StatusInventory[key] = run.StatusInventory.GetValueOrDefault(key) + 1;
        }

        if (type == "summon" && n.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String)
        {
            string kind = u.GetString() ?? "";
            run.SummonInventory[kind] = run.SummonInventory.GetValueOrDefault(kind) + 1;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Report
    // ═══════════════════════════════════════════════════════════════

    private static string BuildReport(RunState run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ Card Verification Report ═══");
        sb.AppendLine($"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.Append($"Scanned: {run.Scanned} cards (");
        var statusParts = new List<string>();
        foreach (var kv in run.ByStatus)
            statusParts.Add($"{kv.Key} {kv.Value}");
        sb.Append(string.Join(", ", statusParts));
        sb.AppendLine(")");
        sb.AppendLine($"Result: {(run.Errors.Count == 0 ? "PASS" : "FAIL")} — {run.Errors.Count} errors, {run.Warnings.Count} warnings");
        sb.AppendLine();

        sb.AppendLine("── Per school (ok / fail) ──");
        foreach (var kv in run.PerSchool)
            sb.AppendLine($"  {kv.Key,-14} {kv.Value[0],3} ok / {kv.Value[1],2} fail");
        sb.AppendLine();

        if (run.Errors.Count > 0)
        {
            sb.AppendLine($"── ERRORS ({run.Errors.Count}) — fix before flipping to ready ──");
            foreach (var e in run.Errors)
                sb.AppendLine($"  ✗ {e}");
            sb.AppendLine();
        }

        if (run.Warnings.Count > 0)
        {
            sb.AppendLine($"── WARNINGS ({run.Warnings.Count}) ──");
            foreach (var w in run.Warnings)
                sb.AppendLine($"  ⚠ {w}");
            sb.AppendLine();
        }

        sb.AppendLine("── Status-string inventory (eyeball for drift) ──");
        foreach (var kv in Sorted(run.StatusInventory))
            sb.AppendLine($"  {kv.Key,-16} ×{kv.Value}");
        sb.AppendLine();

        sb.AppendLine("── Summon unit kinds ──");
        foreach (var kv in Sorted(run.SummonInventory))
            sb.AppendLine($"  {kv.Key,-24} ×{kv.Value}");

        return sb.ToString();
    }

    private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> d)
    {
        var list = new List<KeyValuePair<string, int>>(d);
        list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    private static void WriteReport(string report)
    {
        using var f = FileAccess.Open(ReportPath, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[CardVerifier] Cannot write report: {FileAccess.GetOpenError()}");
            return;
        }
        f.StoreString(report);
    }

    // ── Helpers ─────────────────────────────────────────────────────
    private static string GetString(JsonElement n, string key)
        => n.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
