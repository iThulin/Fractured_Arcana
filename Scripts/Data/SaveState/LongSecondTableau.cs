using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

// ============================================================
// LongSecondTableau.cs
//
// Purpose:        The frozen fight (S2, docs/sundering_long_second_founding_v1.md
//                 section 4): every Keeper and every summon standing when
//                 the Long Second closed, where it stood and how hurt it
//                 was; the floor's elements, reactions, growth, memorials
//                 and glyphs; the traitor; and who on the dais and the
//                 tiers the Keepers had spent. Saved once per save, on the
//                 Eternal ledger, so it survives every unmaking. The hall
//                 stage shows it (S3) and the Fracture resumes from it (S6).
//
//                 Not stored: the candidate (who walked out), the canon
//                 figures, the hall's five breaks, the spell and the folio.
//                 Canon and the fight's fixed beats put those in the same
//                 place on every visit, from the traitor and the spent list.
//
//                 Captured at the climax, the moment the last clause is
//                 spoken (nothing on the board moves after it), so a quit
//                 at the cast panel or later resumes with it already on
//                 the ledger. A save that leaves the hall without one (the
//                 full skip, or a save that left before S2) gets the
//                 authored floor, Data/Encounters/sundering_default_tableau.json
//                 (EnsureDefault, from PrologueDirector.ExitToWorld; the
//                 hall stage can ask for it too).
// Layer:          Data (save)
// Collaborators:  EternalLedger.Tableau, CombatManager.Sundering.cs (the
//                 capture), PrologueDirector (the default, the assert),
//                 SunderingScript (the spend order), SaveManager.JsonOptions
// ============================================================

/// <summary>The Sundering as the Long Second froze it. Captured once per save.</summary>
public sealed class LongSecondTableau
{
    /// <summary>True once written (by the fight, or from the default floor).</summary>
    public bool Captured;
    /// <summary>The map the fight was on (ceremony_hall_sundering).</summary>
    public string MapRecipe = "";
    /// <summary>The clause the sentence had reached: its last.</summary>
    public int Clause;
    /// <summary>The save's co-conspirator, at the sphere ("" when the save has none).</summary>
    public string TraitorId = "";
    /// <summary>Who the Keepers' counter-volleys spent, in order: "class" and archmage ids. Stored
    /// though it could be derived, so a later retune of the order never changes a hall already
    /// seen.</summary>
    public List<string> SpentFaculty = new();
    public List<TableauFigure> Figures = new();
    /// <summary>One row per tile carrying anything: "q,r|element|reaction|growth|memorial|glyph".</summary>
    public List<string> Tiles = new();

    // ── Kinds and the default floor ──────────────────────────────────────
    public const string KindKeeper = "keeper";
    public const string KindSummon = "summon";
    public const string KindConstruct = "construct";
    public const string KindSpirit = "spirit";
    public static readonly string[] Kinds = { KindKeeper, KindSummon, KindConstruct, KindSpirit };

    public const string DefaultPath = "res://Data/Encounters/sundering_default_tableau.json";

    /// <summary>The Sundering's difficulty (prologue_fights.json): the default floor's Keepers are
    /// at full health for it, as the fight's own spawner scales them (the square root, on health).</summary>
    private const float DefaultDifficulty = 0.85f;

    // ════════════════════════════════════════════════════════════════════════
    //  Rows
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>A tile's row, or null when the tile carries none of the five. "|" separates the
    /// fields, so it never appears inside one.</summary>
    public static string TileRow(Vector2I at, string element, string reaction, int growth, string memorial,
                                 string glyph)
    {
        element = Field(element);
        reaction = Field(reaction);
        memorial = Field(memorial);
        glyph = Field(glyph);
        growth = Math.Max(0, growth);
        if (element.Length == 0 && reaction.Length == 0 && growth == 0 && memorial.Length == 0 && glyph.Length == 0)
            return null;
        return string.Join("|", $"{at.X},{at.Y}", element, reaction,
                           growth.ToString(CultureInfo.InvariantCulture), memorial, glyph);
    }

    /// <summary>Reads a row back. False when it is not six fields with a coordinate and a growth.</summary>
    public static bool TryParseTileRow(string row, out Vector2I at, out string element, out string reaction,
                                       out int growth, out string memorial, out string glyph)
    {
        at = Vector2I.Zero;
        element = reaction = memorial = glyph = "";
        growth = 0;
        var fields = (row ?? "").Split('|');
        if (fields.Length != 6)
            return false;
        var qr = fields[0].Split(',');
        if (qr.Length != 2
            || !int.TryParse(qr[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int q)
            || !int.TryParse(qr[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
            || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out growth))
            return false;
        at = new Vector2I(q, r);
        element = fields[1];
        reaction = fields[2];
        memorial = fields[4];
        glyph = fields[5];
        return true;
    }

    /// <summary>A figure's statuses: "status:turns;..." and each live Name as "named:turns".</summary>
    public static string StatusList(IEnumerable<KeyValuePair<string, int>> statuses, IEnumerable<int> nameTurns)
    {
        var parts = new List<string>();
        if (statuses != null)
            foreach (var kv in statuses)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                    parts.Add($"{Field(kv.Key).Replace(":", "")}:{kv.Value.ToString(CultureInfo.InvariantCulture)}");
        if (nameTurns != null)
            foreach (int turns in nameTurns)
                if (turns > 0)
                    parts.Add($"named:{turns.ToString(CultureInfo.InvariantCulture)}");
        return string.Join(";", parts);
    }

    private static string Field(string value) => (value ?? "").Replace("|", "/").Replace(";", ",").Trim();

    // ════════════════════════════════════════════════════════════════════════
    //  The default floor
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The authored floor, as written in the file (no traitor, nobody spent, health 0 for
    /// full); null when the file is missing or unreadable.</summary>
    public static LongSecondTableau LoadDefault()
    {
        if (!FileAccess.FileExists(DefaultPath))
        {
            GD.PushError($"[Tableau] {DefaultPath} is missing.");
            return null;
        }
        try
        {
            using var f = FileAccess.Open(DefaultPath, FileAccess.ModeFlags.Read);
            if (f == null)
                return null;
            return JsonSerializer.Deserialize<LongSecondTableau>(f.GetAsText(), SaveManager.JsonOptions);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Tableau] error reading {DefaultPath}: {e.Message}");
            return null;
        }
    }

    /// <summary>Gives a save the authored floor when it has no tableau: this save's traitor, the
    /// first of the spend order for each boundary the sentence crossed (skipping the traitor), and
    /// every Keeper at full health. False when the save already has one, or the file is missing.</summary>
    public static bool EnsureDefault(GuildSaveData save)
    {
        if (save?.Ledger == null || (save.Ledger.Tableau != null && save.Ledger.Tableau.Captured))
            return false;
        var t = LoadDefault();
        if (t == null)
            return false;

        var script = PrologueDirector.ScriptFor(PrologueDirector.SunderingFightId);
        int clauses = script?.Clauses != null && script.Clauses.Count > 0 ? script.Clauses.Count : Math.Max(1, t.Clause);
        string traitor = save.Ledger.CoConspiratorId ?? "";

        t.Captured = true;
        t.Clause = clauses;
        t.TraitorId = traitor;
        t.SpentFaculty = DefaultSpent(SunderingScript.Get(script?.Volleys)?.SpendOrder, traitor, clauses - 1);
        t.Figures ??= new List<TableauFigure>();
        t.Tiles ??= new List<string>();
        foreach (var figure in t.Figures)
        {
            if (figure == null || figure.MaxHealth > 0)
                continue;
            var def = UnitRegistry.TryResolveId(figure.UnitId ?? "", out var id) ? UnitRegistry.Get(id) : null;
            int max = def != null ? Math.Max(1, Mathf.RoundToInt(def.MaxHealth * Mathf.Sqrt(DefaultDifficulty))) : 1;
            figure.MaxHealth = max;
            if (figure.Health <= 0)
                figure.Health = max;
        }
        save.Ledger.Tableau = t;
        SaveManager.MarkDirty();
        GD.Print($"[Tableau] The default floor: {t.Figures.Count} figure(s), spent {string.Join(", ", t.SpentFaculty)}.");
        return true;
    }

    /// <summary>The first <paramref name="count"/> of the spend order, skipping the traitor.</summary>
    private static List<string> DefaultSpent(IEnumerable<string> order, string traitor, int count)
    {
        var spent = new List<string>();
        if (order == null)
            return spent;
        foreach (var id in order)
        {
            if (spent.Count >= count)
                break;
            if (string.IsNullOrEmpty(id) || id == traitor || spent.Contains(id))
                continue;
            spent.Add(id);
        }
        return spent;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Checks (PrologueDirector.AssertRoundTrip runs them)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The house rule for a new save field: a tableau with every field set goes through the
    /// real save options on a ledger and comes back equal, field by field, and a ledger with none
    /// keeps none. Empty when it passes.</summary>
    public static List<string> RoundTripFailures()
    {
        var fails = new List<string>();
        try
        {
            var probe = new LongSecondTableau
            {
                Captured = true,
                MapRecipe = "probe_map",
                Clause = 5,
                TraitorId = "namer",
                SpentFaculty = new List<string> { "class", "engineer", "joren" },
            };
            probe.Figures.Add(new TableauFigure
            {
                UnitId = "probe_unit",
                Team = 1,
                Kind = KindKeeper,
                Q = 7,
                R = -2,
                Facing = -1.25f,
                Health = 9,
                MaxHealth = 21,
                Statuses = "rooted:1;named:2",
            });
            probe.Tiles.Add(TileRow(new Vector2I(3, -1), "Lightning", "Brittle", 2, "Read-Ahead", "The Seventh Name"));

            var back = JsonSerializer.Deserialize<EternalLedger>(
                JsonSerializer.Serialize(new EternalLedger { Tableau = probe }, SaveManager.JsonOptions),
                SaveManager.JsonOptions)?.Tableau;
            if (back == null)
            {
                fails.Add("the tableau did not come back from the save options");
                return fails;
            }
            if (!back.Captured || back.MapRecipe != "probe_map" || back.Clause != 5 || back.TraitorId != "namer")
                fails.Add("the tableau's header (captured, map, clause, traitor) changed on the round trip");
            if (back.SpentFaculty == null || string.Join(",", back.SpentFaculty) != "class,engineer,joren")
                fails.Add("the tableau's spent list changed on the round trip");
            var f = back.Figures != null && back.Figures.Count == 1 ? back.Figures[0] : null;
            if (f == null || f.UnitId != "probe_unit" || f.Team != 1 || f.Kind != KindKeeper || f.Q != 7 || f.R != -2
                || Math.Abs(f.Facing - (-1.25f)) > 1e-5f || f.Health != 9 || f.MaxHealth != 21
                || f.Statuses != "rooted:1;named:2")
                fails.Add("the tableau's figure changed on the round trip");
            if (back.Tiles == null || back.Tiles.Count != 1 || back.Tiles[0] != probe.Tiles[0]
                || !TryParseTileRow(back.Tiles[0], out var at, out var element, out var reaction, out int growth,
                                    out var memorial, out var glyph)
                || at != new Vector2I(3, -1) || element != "Lightning" || reaction != "Brittle" || growth != 2
                || memorial != "Read-Ahead" || glyph != "The Seventh Name")
                fails.Add("the tableau's tile row changed on the round trip");

            var none = JsonSerializer.Deserialize<EternalLedger>(
                JsonSerializer.Serialize(new EternalLedger(), SaveManager.JsonOptions), SaveManager.JsonOptions);
            if (none == null || none.Tableau != null)
                fails.Add("a ledger without a tableau came back with one");
        }
        catch (Exception e)
        {
            fails.Add($"the tableau round trip threw: {e.Message}");
        }
        return fails;
    }

    /// <summary>The default floor reads, names a map, and holds only Keepers that resolve, on
    /// distinct tiles, with tile rows that parse. Empty when it passes.</summary>
    public static List<string> DefaultFailures()
    {
        var fails = new List<string>();
        var t = LoadDefault();
        if (t == null)
        {
            fails.Add($"the default tableau ({DefaultPath}) does not read");
            return fails;
        }
        if (string.IsNullOrEmpty(t.MapRecipe))
            fails.Add("the default tableau names no map");
        var seen = new HashSet<Vector2I>();
        foreach (var figure in t.Figures ?? new List<TableauFigure>())
        {
            if (figure == null)
                continue;
            if (Array.IndexOf(Kinds, figure.Kind ?? "") < 0)
                fails.Add($"default tableau: '{figure.UnitId}' has kind '{figure.Kind}', not one of {string.Join(", ", Kinds)}");
            if (figure.Kind == KindKeeper && !UnitRegistry.TryResolveId(figure.UnitId ?? "", out _))
                fails.Add($"default tableau: Keeper '{figure.UnitId}' does not resolve");
            if (!seen.Add(new Vector2I(figure.Q, figure.R)))
                fails.Add($"default tableau: two figures stand on {figure.Q},{figure.R}");
        }
        foreach (var row in t.Tiles ?? new List<string>())
            if (!TryParseTileRow(row, out _, out _, out _, out _, out _, out _))
                fails.Add($"default tableau: tile row '{row}' does not parse");
        return fails;
    }
}

/// <summary>One unit on the floor when the hall froze (never the candidate).</summary>
public sealed class TableauFigure
{
    /// <summary>The unit's registry id (DefinitionId), or for a summon without one its kind
    /// ("spirit", "turret").</summary>
    public string UnitId = "";
    public int Team;
    /// <summary>keeper, summon, construct or spirit (LongSecondTableau.Kinds).</summary>
    public string Kind = "";
    public int Q, R;
    /// <summary>Radians about the vertical, the way HallSet.PlaceFigure turns a figure: the atan2
    /// of the facing's X and Z, so 0 faces +Z (north) and -pi/2 faces west, toward the dais. The
    /// fight never turns a unit, so the capture takes it from what the unit was turned toward: its
    /// plan's tile, else the candidate's mark (a Keeper), or the nearest Keeper (a unit of yours).</summary>
    public float Facing;
    /// <summary>The Fracture resumes them as they were. 0 in the default file is full health.</summary>
    public int Health, MaxHealth;
    /// <summary>"status:turns;..." with each live Name as "named:turns".</summary>
    public string Statuses = "";
}
