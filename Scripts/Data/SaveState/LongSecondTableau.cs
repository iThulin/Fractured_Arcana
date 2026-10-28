using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

// ============================================================
// LongSecondTableau.cs
//
// Purpose:        The frozen fight (S2, docs/sundering_long_second_founding_v1.md
//                 section 4): every Keeper and every summon standing when
//                 the Long Second closed, where it stood and how hurt it
//                 was; the floor as the fight left it; the traitor; and
//                 who on the dais and the tiers the Keepers had spent.
//                 Saved once per save, on the Eternal ledger, so it
//                 survives every unmaking. The hall stage shows it (S3)
//                 and the Fracture resumes from it (S6).
//
//                 The floor (S4a, section 16): every tile that carries a
//                 mark (an element, a reaction, growth, a memorial, a
//                 glyph) or whose ground the fight changed (its height,
//                 terrain, rubble, scar, cost, hazard, an obstacle such
//                 as a Glacier's ice) keeps a row, with every detail of
//                 both, so the fight starts again from the same floor. A
//                 row from before S4a has the marks alone, and its ground
//                 is as the map builds it.
//
//                 Not stored: the candidate (who walked out), the canon
//                 figures, the hall's breaks, the spell and the folio.
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
//                 capture), HallStage (the floor laid again),
//                 PrologueDirector (the default, the assert),
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
    /// <summary>One row per tile with a mark or a changed ground (Row, TryParseTile):
    /// "q,r|element|reaction|growth|memorial|glyph|key=value,...". A row from before the full
    /// floor stops after the glyph.</summary>
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

    /// <summary>The marks' six fields: the coordinate, element, reaction, growth, memorial, glyph.</summary>
    private const int MarkFields = 6;

    /// <summary>A tile's row: the six fields of its marks, then its ground and the marks' details as
    /// "key=value" pairs, every key written. "|" separates the fields and "," the pairs, so neither
    /// appears inside one.</summary>
    public static string Row(TableauTileRow t)
    {
        if (t == null)
            return null;
        var g = new StringBuilder();
        void Pair(string key, string value)
        {
            if (g.Length > 0)
                g.Append(',');
            g.Append(key).Append('=').Append(Value(value));
        }
        Pair("h", Int(t.Height));
        Pair("terrain", t.Terrain);
        Pair("mod", t.Modifier);
        Pair("scar", t.Scar);
        Pair("cost", Int(t.MoveCost));
        Pair("basecost", Int(t.BaseMoveCost));
        Pair("hazard", Bit(t.Hazardous));
        Pair("walk", Bit(t.Walkable));
        Pair("blocked", Bit(t.Blocked));
        Pair("sight", Bit(t.BlocksSight));
        Pair("cover", t.Cover);
        Pair("obstacle", t.Obstacle);
        Pair("obstaclehp", Int(t.ObstacleHp));
        Pair("strength", t.Strength.ToString("0.###", CultureInfo.InvariantCulture));
        Pair("rounds", Int(t.ReactionRounds));
        Pair("team", Int(t.ReactionTeam));
        Pair("growthage", Int(t.GrowthAge));
        Pair("growthteam", Int(t.GrowthTeam));
        Pair("carcass", Int(t.Carcass));
        Pair("discount", Int(t.Discount));
        Pair("discountturns", Int(t.DiscountTurns));
        Pair("mstate", t.MemorialState);
        Pair("mstrength", t.MemorialStrength);
        Pair("mally", Bit(t.MemorialAlly));
        Pair("mteam", Int(t.MemorialTeam));
        Pair("msource", Int(t.MemorialSourceTeam));
        Pair("mturns", Int(t.MemorialTurns));
        Pair("gcard", t.GlyphCard);
        Pair("ghalf", t.GlyphHalf);
        Pair("gteam", Int(t.GlyphTeam));
        Pair("gtrigger", t.GlyphTrigger);
        Pair("greflect", Int(t.GlyphReflect));
        return string.Join("|", $"{Int(t.At.X)},{Int(t.At.Y)}", Field(t.Element), Field(t.Reaction),
                           Int(Math.Max(0, t.Growth)), Field(t.Memorial), Field(t.Glyph), g.ToString());
    }

    /// <summary>Reads a row back: six fields (a row from before the full floor, Full false) or seven.
    /// False when the coordinate, the growth or a known value does not read. Keys it does not know
    /// are passed over, so a later key never breaks an older build.</summary>
    public static bool TryParseTile(string row, out TableauTileRow tile)
    {
        tile = null;
        var fields = (row ?? "").Split('|');
        if (fields.Length != MarkFields && fields.Length != MarkFields + 1)
            return false;
        var qr = fields[0].Split(',');
        if (qr.Length != 2 || !TryInt(qr[0], out int q) || !TryInt(qr[1], out int r) || !TryInt(fields[3], out int growth))
            return false;
        var t = new TableauTileRow
        {
            At = new Vector2I(q, r),
            Element = fields[1],
            Reaction = fields[2],
            Growth = Math.Max(0, growth),
            Memorial = fields[4],
            Glyph = fields[5],
        };
        if (fields.Length == MarkFields)
        {
            tile = t;
            return true;
        }
        t.Full = true;
        foreach (var pair in fields[MarkFields].Split(','))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = pair.Substring(0, eq);
            string v = pair.Substring(eq + 1);
            bool ok = true;
            switch (key)
            {
                case "h": ok = TryInt(v, out t.Height); break;
                case "terrain": t.Terrain = v; break;
                case "mod": t.Modifier = v; break;
                case "scar": t.Scar = v; break;
                case "cost": ok = TryInt(v, out t.MoveCost); break;
                case "basecost": ok = TryInt(v, out t.BaseMoveCost); break;
                case "hazard": ok = TryBit(v, out t.Hazardous); break;
                case "walk": ok = TryBit(v, out t.Walkable); break;
                case "blocked": ok = TryBit(v, out t.Blocked); break;
                case "sight": ok = TryBit(v, out t.BlocksSight); break;
                case "cover": t.Cover = v; break;
                case "obstacle": t.Obstacle = v; break;
                case "obstaclehp": ok = TryInt(v, out t.ObstacleHp); break;
                case "strength":
                    ok = float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out t.Strength);
                    break;
                case "rounds": ok = TryInt(v, out t.ReactionRounds); break;
                case "team": ok = TryInt(v, out t.ReactionTeam); break;
                case "growthage": ok = TryInt(v, out t.GrowthAge); break;
                case "growthteam": ok = TryInt(v, out t.GrowthTeam); break;
                case "carcass": ok = TryInt(v, out t.Carcass); break;
                case "discount": ok = TryInt(v, out t.Discount); break;
                case "discountturns": ok = TryInt(v, out t.DiscountTurns); break;
                case "mstate": t.MemorialState = v; break;
                case "mstrength": t.MemorialStrength = v; break;
                case "mally": ok = TryBit(v, out t.MemorialAlly); break;
                case "mteam": ok = TryInt(v, out t.MemorialTeam); break;
                case "msource": ok = TryInt(v, out t.MemorialSourceTeam); break;
                case "mturns": ok = TryInt(v, out t.MemorialTurns); break;
                case "gcard": t.GlyphCard = v; break;
                case "ghalf": t.GlyphHalf = v; break;
                case "gteam": ok = TryInt(v, out t.GlyphTeam); break;
                case "gtrigger": t.GlyphTrigger = v; break;
                case "greflect": ok = TryInt(v, out t.GlyphReflect); break;
            }
            if (!ok)
                return false;
        }
        tile = t;
        return true;
    }

    /// <summary>The ground alone, as one string: what the capture compares with the floor at the
    /// fight's opening to know which tiles the fight changed. The marks and their details are not
    /// in it.</summary>
    public static string GroundKey(TableauTileRow t)
    {
        if (t == null)
            return "";
        return string.Join(",", Int(t.Height), Value(t.Terrain), Value(t.Modifier), Value(t.Scar),
                           Int(t.MoveCost), Int(t.BaseMoveCost), Bit(t.Hazardous), Bit(t.Walkable),
                           Bit(t.Blocked), Bit(t.BlocksSight), Value(t.Cover), Value(t.Obstacle),
                           Int(t.ObstacleHp), Int(t.Carcass), Int(t.Discount), Int(t.DiscountTurns));
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

    /// <summary>A pair's value: ids, enum names and numbers, with no separator inside.</summary>
    private static string Value(string value) =>
        (value ?? "").Replace("|", "").Replace(",", "").Replace("=", "").Replace(";", "").Trim();

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Bit(bool value) => value ? "1" : "0";
    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryBit(string s, out bool value)
    {
        value = s == "1";
        return s == "1" || s == "0";
    }

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
    /// every Keeper whole (full health, armor and poise for the fight's difficulty). False when the
    /// save already has one, or the file is missing.</summary>
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
            if (def != null)
            {
                figure.Armor = def.Armor;
                figure.Poise = def.ResolvedPoise;
            }
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
    /// keeps none. A tile row with every key set comes back with every value, and a row from before
    /// the full floor still reads. Empty when it passes.</summary>
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
                Armor = 2,
                Shield = 3,
                Poise = 1,
                WitheredMaxHp = 4,
                Statuses = "rooted:1;named:2",
            });
            var tileProbe = Probe();
            probe.Tiles.Add(Row(tileProbe));
            const string OldRow = "8,-3|Lightning||0||";
            probe.Tiles.Add(OldRow);

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
                || f.Armor != 2 || f.Shield != 3 || f.Poise != 1 || f.WitheredMaxHp != 4
                || f.Statuses != "rooted:1;named:2")
                fails.Add("the tableau's figure changed on the round trip");
            if (back.Tiles == null || back.Tiles.Count != 2 || back.Tiles[0] != probe.Tiles[0] || back.Tiles[1] != OldRow)
                fails.Add("the tableau's tile rows changed on the round trip");
            else
            {
                string differs = Differs(tileProbe, back.Tiles[0]);
                if (differs.Length > 0)
                    fails.Add($"the tableau's full tile row did not read back: {differs}");
                if (!TryParseTile(back.Tiles[1], out var old) || old.Full || old.At != new Vector2I(8, -3)
                    || old.Element != "Lightning" || old.Reaction.Length > 0 || old.Growth != 0)
                    fails.Add("a tile row from before the full floor no longer reads");
            }

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

    /// <summary>A tile row with every value away from its default.</summary>
    private static TableauTileRow Probe() => new()
    {
        At = new Vector2I(3, -1),
        Element = "Lightning",
        Reaction = "Brittle",
        Growth = 2,
        Memorial = "Read-Ahead",
        Glyph = "The Seventh Name",
        Full = true,
        Height = 2,
        Terrain = "Ice",
        Modifier = "rubble",
        Scar = "rubble",
        MoveCost = 3,
        BaseMoveCost = 2,
        Hazardous = true,
        Walkable = false,
        Blocked = true,
        BlocksSight = true,
        Cover = "Low",
        Obstacle = "glacier",
        ObstacleHp = 7,
        Strength = 0.625f,
        ReactionRounds = 2,
        ReactionTeam = 1,
        GrowthAge = 3,
        GrowthTeam = 0,
        Carcass = 4,
        Discount = 1,
        DiscountTurns = 2,
        MemorialState = "Established",
        MemorialStrength = "Solid",
        MemorialAlly = true,
        MemorialTeam = 1,
        MemorialSourceTeam = 0,
        MemorialTurns = 5,
        GlyphCard = "probe_card",
        GlyphHalf = "b",
        GlyphTeam = 1,
        GlyphTrigger = "StartOfTurn",
        GlyphReflect = 2,
    };

    /// <summary>The fields of <paramref name="row"/> that do not read back as <paramref name="want"/>,
    /// named; empty when every one does.</summary>
    private static string Differs(TableauTileRow want, string row)
    {
        if (!TryParseTile(row, out var got) || got == null)
            return "the row does not parse";
        var bad = new List<string>();
        void Check(string name, bool same)
        {
            if (!same)
                bad.Add(name);
        }
        Check("coordinate", got.At == want.At);
        Check("element", got.Element == want.Element);
        Check("reaction", got.Reaction == want.Reaction);
        Check("growth", got.Growth == want.Growth);
        Check("memorial", got.Memorial == want.Memorial);
        Check("glyph", got.Glyph == want.Glyph);
        Check("full", got.Full);
        Check("height", got.Height == want.Height);
        Check("terrain", got.Terrain == want.Terrain);
        Check("modifier", got.Modifier == want.Modifier);
        Check("scar", got.Scar == want.Scar);
        Check("cost", got.MoveCost == want.MoveCost);
        Check("base cost", got.BaseMoveCost == want.BaseMoveCost);
        Check("hazard", got.Hazardous == want.Hazardous);
        Check("walkable", got.Walkable == want.Walkable);
        Check("blocked", got.Blocked == want.Blocked);
        Check("sight", got.BlocksSight == want.BlocksSight);
        Check("cover", got.Cover == want.Cover);
        Check("obstacle", got.Obstacle == want.Obstacle);
        Check("obstacle health", got.ObstacleHp == want.ObstacleHp);
        Check("strength", Math.Abs(got.Strength - want.Strength) < 1e-4f);
        Check("reaction rounds", got.ReactionRounds == want.ReactionRounds);
        Check("reaction team", got.ReactionTeam == want.ReactionTeam);
        Check("growth age", got.GrowthAge == want.GrowthAge);
        Check("growth team", got.GrowthTeam == want.GrowthTeam);
        Check("carcass", got.Carcass == want.Carcass);
        Check("discount", got.Discount == want.Discount);
        Check("discount turns", got.DiscountTurns == want.DiscountTurns);
        Check("memorial state", got.MemorialState == want.MemorialState);
        Check("memorial strength", got.MemorialStrength == want.MemorialStrength);
        Check("memorial ally", got.MemorialAlly == want.MemorialAlly);
        Check("memorial team", got.MemorialTeam == want.MemorialTeam);
        Check("memorial source", got.MemorialSourceTeam == want.MemorialSourceTeam);
        Check("memorial turns", got.MemorialTurns == want.MemorialTurns);
        Check("glyph card", got.GlyphCard == want.GlyphCard);
        Check("glyph half", got.GlyphHalf == want.GlyphHalf);
        Check("glyph team", got.GlyphTeam == want.GlyphTeam);
        Check("glyph trigger", got.GlyphTrigger == want.GlyphTrigger);
        Check("glyph reflect", got.GlyphReflect == want.GlyphReflect);
        return string.Join(", ", bad);
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
            if (!TryParseTile(row, out _))
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
    /// <summary>The rest of the damage it had taken (S4a): its armor and shield as they stood, its
    /// poise, and the maximum health withered away. The default file's Keepers are whole.</summary>
    public int Armor, Shield, Poise, WitheredMaxHp;
    /// <summary>"status:turns;..." with each live Name as "named:turns".</summary>
    public string Statuses = "";
}

/// <summary>One tile row of the tableau's floor, read (TryParseTile) or to write (Row). Never saved
/// itself: the tableau keeps the rows, so a row from before the full floor still reads, with Full
/// false and its ground left as the map builds it.</summary>
public sealed class TableauTileRow
{
    public Vector2I At;

    // ── The marks: the six fields every row has ──────────────────────────
    public string Element = "", Reaction = "", Memorial = "", Glyph = "";
    public int Growth;

    // ── The ground, and the marks' details: the seventh field (S4a) ───────
    /// <summary>True when the row carried the seventh field.</summary>
    public bool Full;
    public int Height;
    /// <summary>TileTerrainType, CoverKind and the obstacle kind by name; "" when not carried.</summary>
    public string Terrain = "", Modifier = "", Scar = "", Cover = "", Obstacle = "";
    public int MoveCost = 1, BaseMoveCost = 1, ObstacleHp;
    public bool Hazardous, Walkable = true, Blocked, BlocksSight;
    public float Strength = 1f;
    public int ReactionRounds, ReactionTeam = -1;
    /// <summary>The growth's age, and its owner's side (-1: none).</summary>
    public int GrowthAge, GrowthTeam = -1;
    public int Carcass, Discount, DiscountTurns;
    /// <summary>The memorial's MemorialState and MemorialStrength by name, and the rest of it.</summary>
    public string MemorialState = "", MemorialStrength = "";
    public bool MemorialAlly;
    public int MemorialTeam, MemorialSourceTeam = -1, MemorialTurns;
    /// <summary>The glyph's card (its sigil), its side, and its GlyphTrigger by name.</summary>
    public string GlyphCard = "", GlyphHalf = "", GlyphTrigger = "";
    public int GlyphTeam, GlyphReflect;

    /// <summary>Anything on the tile the fight marked: an element, a reaction, growth, a memorial
    /// or a glyph.</summary>
    public bool HasMarks => Element.Length > 0 || Reaction.Length > 0 || Growth > 0
                            || Memorial.Length > 0 || Glyph.Length > 0;
}
