using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// GlyphCipherSelfTest.cs
//
// Purpose:        Verification gate for the glyph cipher. Asserts
//                 structural invariants that hold for ANY input,
//                 golden checksums for the 42 Enchanter spell
//                 halves, and cross-run determinism.
// Layer:          Dev
// Collaborators:  GlyphCipher.cs (subject), GlyphCipherTags.cs,
//                 CardDatabase.cs (live corpus),
//                 GameBootstrap.cs (--verify-cipher entry, F10)
// See:            docs/glyph_cipher_spec_v2.md §10 — acceptance tests
// ============================================================
//
// Run headless:   godot --headless -- --verify-cipher
// Run in editor:  F10 in a debug build.
//
// THE GOLDENS ARE NOT DECORATION. The generator's output depends on
// the ORDER of RNG draws, which is invisible to the compiler: moving
// one statement changes every glyph in the game and nothing else will
// notice. The checksums below were produced by a reference
// implementation of the same grammar. If they fail after an edit to
// GlyphCipher.cs, the edit changed the format. That may be intended —
// regenerate them deliberately and bump the spec — but it is never
// accidental.
//
// The invariant assertions (INV-*) are independent of the goldens and
// hold for any name, so they keep working as new cards are added.
//
// ============================================================

/// <summary>One expected decode. Structure is asserted exactly; geometry via a quantised checksum.</summary>
public readonly struct CipherGolden
{
    public readonly string CardId, Half, Name, Letters;
    public readonly CipherTarget Target;
    public readonly CipherVerb Verbs;
    public readonly int Arms, Deepest, Crossbars, Retraces, Strokes;
    public readonly uint Checksum;

    public CipherGolden(string cardId, string half, string name, string letters,
                        CipherTarget target, CipherVerb verbs,
                        int arms, int deepest, int crossbars, int retraces,
                        int strokes, uint checksum)
    {
        CardId = cardId; Half = half; Name = name; Letters = letters;
        Target = target; Verbs = verbs;
        Arms = arms; Deepest = deepest; Crossbars = crossbars;
        Retraces = retraces; Strokes = strokes; Checksum = checksum;
    }
}

/// <summary>Headless verification pass for the glyph cipher. Mirrors <c>CardVerifier</c>'s shape.</summary>
public static class GlyphCipherSelfTest
{
    /// <summary>Quantisation used by the geometry checksum. 1e-4 in unit space is 0.013px at a 128px render.</summary>
    private const string Q = "F4";
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>FNV-1a 32 over the stroke set, quantised. Must match the reference implementation exactly.</summary>
    public static uint Checksum(CipherGlyph g)
    {
        uint h = 0x811C9DC5u;
        void Feed(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 0x01000193u; }
        }
        foreach (var s in g.Strokes)
        {
            Feed(s.Layer.ToString());
            Feed(s.Mark.ToString());
            Feed(s.Weight.ToString("F6", Inv));
            Feed(s.Order.ToString(Inv));
            foreach (var p in s.Points)
            {
                Feed(p.X.ToString(Q, Inv));
                Feed(p.Y.ToString(Q, Inv));
            }
        }
        return h;
    }

    /// <summary>Aggregate of every golden checksum, in card-id order. One number to eyeball in a diff.</summary>
    public const uint AggregateChecksum = 0xEF7EE845u;

    /// <summary>
    /// Expected decode for all 42 Enchanter spell halves as of spec v2.
    /// Regenerate deliberately (and bump the spec version) if the grammar changes.
    /// </summary>
    public static readonly CipherGolden[] Goldens =
    {
        new("enchanter_binding_chains", "bottom", "Drain Glyph", "DRAINGLYPH", CipherTarget.Tile, CipherVerb.Inscribe, 5, 2, 10, 0, 25, 0xB2E2D2E8u),
        new("enchanter_binding_chains", "top", "Binding Chains", "BINDINGCHAINS", CipherTarget.Enemy, CipherVerb.Bind, 5, 3, 13, 0, 34, 0x0E0D0E13u),
        new("enchanter_binding_rune", "bottom", "Sigil Link", "SIGILLINK", CipherTarget.Tile, CipherVerb.Invoke, 5, 2, 9, 1, 26, 0x5CF501E0u),
        new("enchanter_binding_rune", "top", "Binding Rune", "BINDINGRUNE", CipherTarget.Tile, CipherVerb.Inscribe, 6, 2, 11, 0, 30, 0xAF047233u),
        new("enchanter_charm_drift", "bottom", "Ward Stone", "WARDSTONE", CipherTarget.Tile, CipherVerb.Inscribe, 5, 2, 9, 0, 25, 0x4E07DBA0u),
        new("enchanter_charm_drift", "top", "Charm Drift", "CHARMDRIFT", CipherTarget.Enemy, CipherVerb.Move, 5, 2, 10, 0, 25, 0x302A4DCBu),
        new("enchanter_compel", "bottom", "Sigil of Focus", "SIGILOFFOCUS", CipherTarget.Tile, CipherVerb.Inscribe | CipherVerb.Invoke, 6, 2, 12, 1, 34, 0x5B9FF997u),
        new("enchanter_compel", "top", "Compel", "COMPEL", CipherTarget.Enemy, CipherVerb.Move, 6, 1, 6, 0, 24, 0x8E07250Bu),
        new("enchanter_contingency_glyph", "bottom", "Read the Weave", "READTHEWEAVE", CipherTarget.Self, CipherVerb.Invoke, 6, 2, 12, 0, 31, 0x47F95858u),
        new("enchanter_contingency_glyph", "top", "Contingency", "CONTINGENCY", CipherTarget.Tile, CipherVerb.Inscribe | CipherVerb.Invoke, 6, 2, 11, 0, 31, 0xEC3A63B6u),
        new("enchanter_dispel_walk", "bottom", "Glyph Bolt", "GLYPHBOLT", CipherTarget.Enemy, CipherVerb.Strike, 5, 2, 9, 0, 24, 0x87310326u),
        new("enchanter_dispel_walk", "top", "Dispel Walk", "DISPELWALK", CipherTarget.Self, CipherVerb.Move | CipherVerb.Bind, 5, 2, 10, 0, 28, 0x8E607E08u),
        new("enchanter_dominion", "bottom", "Sanctuary", "SANCTUARY", CipherTarget.Self, CipherVerb.Inscribe, 5, 2, 9, 0, 25, 0x88F2A1F3u),
        new("enchanter_dominion", "top", "Dominion", "DOMINION", CipherTarget.Tile, CipherVerb.Bind, 4, 2, 8, 0, 21, 0x3803E4E0u),
        new("enchanter_ensnaring_web", "bottom", "Rearm", "REARM", CipherTarget.Self, CipherVerb.Invoke, 5, 1, 5, 0, 20, 0x5B552AA0u),
        new("enchanter_ensnaring_web", "top", "Ensnaring Web", "ENSNARINGWEB", CipherTarget.Tile, CipherVerb.Inscribe, 6, 2, 12, 0, 33, 0xD7098F3Bu),
        new("enchanter_fate_weaver", "bottom", "Glyph Storm", "GLYPHSTORM", CipherTarget.Self, CipherVerb.Inscribe, 5, 2, 10, 0, 26, 0x120D2137u),
        new("enchanter_fate_weaver", "top", "Fate Weaver", "FATEWEAVER", CipherTarget.Tile, CipherVerb.Inscribe, 5, 2, 10, 0, 26, 0x6F377134u),
        new("enchanter_geas", "bottom", "Empowerment Field", "EMPOWERMENTFIELD", CipherTarget.Tile, CipherVerb.Inscribe, 6, 3, 16, 0, 35, 0xF6CBDBADu),
        new("enchanter_geas", "top", "Geas", "GEAS", CipherTarget.Enemy, CipherVerb.Bind, 4, 1, 4, 0, 19, 0x4A6DE560u),
        new("enchanter_glyph_network", "bottom", "Spell Anchor", "SPELLANCHOR", CipherTarget.Tile, CipherVerb.Inscribe, 6, 2, 11, 1, 31, 0x68F2A1AAu),
        new("enchanter_glyph_network", "top", "Glyph Network", "GLYPHNETWORK", CipherTarget.Self, CipherVerb.Invoke, 6, 2, 12, 0, 31, 0x0E9B7D8Du),
        new("enchanter_hex_mark", "bottom", "Empower Rune", "EMPOWERRUNE", CipherTarget.Tile, CipherVerb.Inscribe, 6, 2, 11, 1, 30, 0x5D1EE911u),
        new("enchanter_hex_mark", "top", "Hex Mark", "HEXMARK", CipherTarget.Enemy, CipherVerb.Bind, 4, 2, 7, 0, 21, 0x3AB81532u),
        new("enchanter_mana_tithe", "bottom", "Sap", "SAP", CipherTarget.Enemy, CipherVerb.Bind | CipherVerb.Strike, 3, 1, 3, 0, 18, 0xB9D7006Bu),
        new("enchanter_mana_tithe", "top", "Mana Tithe", "MANATITHE", CipherTarget.Enemy, CipherVerb.Bind, 5, 2, 9, 0, 25, 0x42B8DFFDu),
        new("enchanter_maze_of_mirrors", "bottom", "Web of Fate", "WEBOFFATE", CipherTarget.Self, CipherVerb.Invoke, 5, 2, 9, 1, 26, 0x091AC0ACu),
        new("enchanter_maze_of_mirrors", "top", "Maze of Mirrors", "MAZEOFMIRRORS", CipherTarget.Self, CipherVerb.Ward, 5, 3, 13, 1, 32, 0x04FB47C2u),
        new("enchanter_mirror_ward", "bottom", "Phase Shift", "PHASESHIFT", CipherTarget.Enemy, CipherVerb.Move, 5, 2, 10, 0, 27, 0xE046EB14u),
        new("enchanter_mirror_ward", "top", "Mirror Ward", "MIRRORWARD", CipherTarget.Tile, CipherVerb.Inscribe, 5, 2, 10, 1, 26, 0xF6B1DA45u),
        new("enchanter_runic_cascade", "bottom", "Glyph Warp", "GLYPHWARP", CipherTarget.Tile, CipherVerb.Invoke, 5, 2, 9, 0, 25, 0xCDBA4547u),
        new("enchanter_runic_cascade", "top", "Runic Cascade", "RUNICCASCADE", CipherTarget.Tile, CipherVerb.Inscribe, 6, 2, 12, 1, 32, 0xA0041821u),
        new("enchanter_runic_trap", "bottom", "Sigil Slide", "SIGILSLIDE", CipherTarget.Self, CipherVerb.Move | CipherVerb.Inscribe, 5, 2, 10, 0, 26, 0xEEEE4C01u),
        new("enchanter_runic_trap", "top", "Runic Trap", "RUNICTRAP", CipherTarget.Tile, CipherVerb.Inscribe, 5, 2, 9, 0, 23, 0x954CB4B5u),
        new("enchanter_snare_glyph", "bottom", "Rune Step", "RUNESTEP", CipherTarget.Self, CipherVerb.Move | CipherVerb.Invoke, 4, 2, 8, 0, 23, 0xE0E73D15u),
        new("enchanter_snare_glyph", "top", "Snare Glyph", "SNAREGLYPH", CipherTarget.Tile, CipherVerb.Inscribe, 5, 2, 10, 0, 27, 0x5B6176D1u),
        new("enchanter_sovereign_will", "bottom", "Puppeteer", "PUPPETEER", CipherTarget.Self, CipherVerb.Bind, 5, 2, 9, 2, 26, 0x50AFFE9Cu),
        new("enchanter_sovereign_will", "top", "Sovereign Pillars", "SOVEREIGNPILLARS", CipherTarget.Tile, CipherVerb.Inscribe, 6, 3, 16, 1, 37, 0xABBF4FD3u),
        new("enchanter_the_grand_design", "bottom", "Absolute Territory", "ABSOLUTETERRITORY", CipherTarget.Self, CipherVerb.Bind, 6, 3, 17, 1, 35, 0x2C91D76Du),
        new("enchanter_the_grand_design", "top", "The Grand Design", "THEGRANDDESIGN", CipherTarget.Self, CipherVerb.Invoke, 5, 3, 14, 1, 31, 0x87F141E2u),
        new("enchanter_warding_step", "bottom", "Tripwire", "TRIPWIRE", CipherTarget.Tile, CipherVerb.Inscribe, 4, 2, 8, 0, 21, 0x163DE3C8u),
        new("enchanter_warding_step", "top", "Warding Step", "WARDINGSTEP", CipherTarget.Self, CipherVerb.Ward | CipherVerb.Move, 6, 2, 11, 0, 33, 0x52C88AA0u),
    };

    /// <summary>Runs every check and prints a report. Returns true when everything passes.</summary>
    public static bool RunAndReport()
    {
        var fails = new List<string>();
        int checks = 0;

        foreach (var g in Goldens)
        {
            checks++;
            CipherGlyph glyph;
            try { glyph = GlyphCipher.Build(g.CardId, g.Half, g.Name, g.Target, g.Verbs); }
            catch (Exception ex)
            {
                fails.Add($"GOLD {g.CardId}#{g.Half}: threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (glyph.Letters != g.Letters)
                fails.Add($"GOLD {g.CardId}#{g.Half}: letters '{glyph.Letters}' != '{g.Letters}'");
            if (glyph.ArmCount != g.Arms)
                fails.Add($"GOLD {g.CardId}#{g.Half}: arms {glyph.ArmCount} != {g.Arms}");
            if (glyph.DeepestArm != g.Deepest)
                fails.Add($"GOLD {g.CardId}#{g.Half}: deepest {glyph.DeepestArm} != {g.Deepest}");
            if (glyph.CrossbarCount != g.Crossbars)
                fails.Add($"GOLD {g.CardId}#{g.Half}: crossbars {glyph.CrossbarCount} != {g.Crossbars}");
            if (glyph.RetraceCount != g.Retraces)
                fails.Add($"GOLD {g.CardId}#{g.Half}: retraces {glyph.RetraceCount} != {g.Retraces}");
            if (glyph.Strokes.Length != g.Strokes)
                fails.Add($"GOLD {g.CardId}#{g.Half}: strokes {glyph.Strokes.Length} != {g.Strokes}");

            uint cs = Checksum(glyph);
            if (cs != g.Checksum)
                fails.Add($"GOLD {g.CardId}#{g.Half}: checksum 0x{cs:X8} != 0x{g.Checksum:X8} " +
                          "(the generator's output changed — see the numbered draw sites in GlyphCipher.cs)");

            fails.AddRange(Invariants(glyph, $"{g.CardId}#{g.Half}"));
        }

        checks++;
        uint agg = 0x811C9DC5u;
        foreach (var g in Goldens)
        {
            var glyph = GlyphCipher.Build(g.CardId, g.Half, g.Name, g.Target, g.Verbs);
            byte[] b = Encoding.UTF8.GetBytes(Checksum(glyph).ToString("X8"));
            for (int i = 0; i < b.Length; i++) { agg ^= b[i]; agg *= 0x01000193u; }
        }
        if (agg != AggregateChecksum)
            fails.Add($"AGGREGATE 0x{agg:X8} != 0x{AggregateChecksum:X8}");

        foreach (var g in Goldens)
        {
            checks++;
            var a = GlyphCipher.Build(g.CardId, g.Half, g.Name, g.Target, g.Verbs);
            var b = GlyphCipher.Build(g.CardId, g.Half, g.Name, g.Target, g.Verbs);
            if (Checksum(a) != Checksum(b))
                fails.Add($"DETERMINISM {g.CardId}#{g.Half}: two builds in one process differ");
        }

        checks++;
        var s1 = GlyphCipher.Build("card_x", "top", "Snare Glyph", CipherTarget.Tile, CipherVerb.Inscribe);
        var s2 = GlyphCipher.Build("card_y", "top", "Snare Glyph", CipherTarget.Tile, CipherVerb.Inscribe);
        var s3 = GlyphCipher.Build("card_x", "bottom", "Snare Glyph", CipherTarget.Tile, CipherVerb.Inscribe);
        if (Checksum(s1) == Checksum(s2)) fails.Add("SEED: different card ids produced identical glyphs");
        if (Checksum(s1) == Checksum(s3)) fails.Add("SEED: different halves produced identical glyphs");

        checks++;
        foreach (var name in new[] { "A", "Zz", "  spaced  out  ", "O'Keeffe's Ward",
                                     "A very long spell name indeed that exceeds the cap" })
        {
            try
            {
                var g = GlyphCipher.Build("edge_case", "top", name, CipherTarget.Self, CipherVerb.None);
                fails.AddRange(Invariants(g, $"edge:'{name}'"));
                if (g.Letters.Length > GlyphCipher.MaxLetters)
                    fails.Add($"edge:'{name}': {g.Letters.Length} letters exceeds MaxLetters");
            }
            catch (Exception ex) { fails.Add($"edge:'{name}': threw {ex.GetType().Name}: {ex.Message}"); }
        }
        checks++;
        try
        {
            GlyphCipher.Build("edge_case", "top", "1234 !!", CipherTarget.Self, CipherVerb.None);
            fails.Add("edge: a name with no A-Z letters should throw, but did not");
        }
        catch (ArgumentException) { /* expected */ }

        int live = 0, liveFail = 0;
        foreach (var bp in CardDatabase.Blueprints)
        {
            if (bp.School != CardSchool.Enchanter) continue;
            foreach (var (half, data) in new[] { ("top", bp.Prebuilt?.TopHalf), ("bottom", bp.Prebuilt?.BottomHalf) })
            {
                if (data == null) continue;
                live++;
                var g = GlyphCipherTags.BuildFor(bp.Id, half, data);
                if (g == null) { fails.Add($"LIVE {bp.Id}#{half}: BuildFor returned null"); liveFail++; continue; }
                if (g.Verbs == CipherVerb.None)
                { fails.Add($"LIVE {bp.Id}#{half} ('{data.Name}'): no verb extracted — add a .WithTag(...) at the effect's registration site"); liveFail++; }
                fails.AddRange(Invariants(g, $"live:{bp.Id}#{half}"));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("── GlyphCipher self-test (spec v2, radial stave) ──");
        sb.AppendLine($"  goldens        : {Goldens.Length}");
        sb.AppendLine($"  checks         : {checks}");
        sb.AppendLine($"  live Enchanter : {live} halves, {liveFail} failed");
        sb.AppendLine($"  aggregate      : 0x{agg:X8} (expected 0x{AggregateChecksum:X8})");
        sb.AppendLine($"  failures       : {fails.Count}");
        foreach (var f in fails) sb.AppendLine("    - " + f);

        if (fails.Count == 0) GD.Print(sb.ToString());
        else GD.PrintErr(sb.ToString());
        return fails.Count == 0;
    }

    /// <summary>
    /// Assertions that must hold for any glyph from any input. These are the real gate:
    /// unlike the goldens they keep working when new cards are added.
    /// </summary>
    private static List<string> Invariants(CipherGlyph g, string where)
    {
        var f = new List<string>();

        // INV-1 every letter gets exactly one crossbar. Unlike v1 there is no
        //       zero-length degenerate case, so this is an equality, not a bound.
        if (g.CrossbarCount != g.Letters.Length)
            f.Add($"INV-1 {where}: {g.CrossbarCount} crossbars for {g.Letters.Length} letters");

        // INV-2 the arm layout is the one ArmLayout would produce, and fits in six arms.
        var expect = GlyphCipher.ArmLayout(g.Letters.Length);
        int expectDeepest = 0;
        int expectTotal = 0;
        foreach (int c in expect) { if (c > expectDeepest) expectDeepest = c; expectTotal += c; }
        if (g.ArmCount != expect.Length) f.Add($"INV-2 {where}: {g.ArmCount} arms, layout implies {expect.Length}");
        if (g.DeepestArm != expectDeepest) f.Add($"INV-2 {where}: deepest {g.DeepestArm}, layout implies {expectDeepest}");
        if (expectTotal != g.Letters.Length) f.Add($"INV-2 {where}: layout covers {expectTotal} of {g.Letters.Length} letters");
        if (g.ArmCount > GlyphCipher.MaxArms) f.Add($"INV-2 {where}: {g.ArmCount} arms exceeds MaxArms");

        // INV-3 nothing escapes the rim (the rim's own wobble is +/-0.006).
        foreach (var s in g.Strokes)
            foreach (var p in s.Points)
                if (p.Length > 1.01)
                    f.Add($"INV-3 {where}: point at r={p.Length:F4} escapes the rim");

        // INV-4 exactly one start marker, one terminal marker, one hub.
        int starts = 0, terminals = 0, hubs = 0, tips = 0, retraceMarks = 0;
        foreach (var s in g.Strokes)
        {
            switch (s.Mark)
            {
                case CipherMark.Start: starts++; break;
                case CipherMark.Terminal: terminals++; break;
                case CipherMark.Hub: hubs++; break;
                case CipherMark.SpokeTip: tips++; break;
                case CipherMark.Retrace: retraceMarks++; break;
            }
        }
        if (starts != 1) f.Add($"INV-4 {where}: {starts} start markers, expected 1");
        if (terminals != 1) f.Add($"INV-4 {where}: {terminals} terminal markers, expected 1");
        if (hubs != 1) f.Add($"INV-4 {where}: {hubs} hubs, expected 1");
        if (retraceMarks != g.RetraceCount) f.Add($"INV-4 {where}: {retraceMarks} retrace marks, expected {g.RetraceCount}");

        // INV-5 one spoke and one spoke tip per verb.
        int verbCount = 0;
        foreach (var v in GlyphCipher.VerbRingOrder)
            if ((g.Verbs & v) != 0) verbCount++;
        if (tips != verbCount) f.Add($"INV-5 {where}: {tips} spoke tips, expected {verbCount}");

        // INV-6 reveal indices are dense and unique over 0..OrderedCount-1, so the
        //       draw-on animation has no gaps and nothing is drawn twice.
        var seen = new bool[g.OrderedCount];
        foreach (var s in g.Strokes)
        {
            if (s.Order < 0) continue;
            if (s.Order >= g.OrderedCount) { f.Add($"INV-6 {where}: order {s.Order} >= OrderedCount {g.OrderedCount}"); continue; }
            if (seen[s.Order]) f.Add($"INV-6 {where}: duplicate order {s.Order}");
            seen[s.Order] = true;
        }
        for (int i = 0; i < seen.Length; i++)
            if (!seen[i]) { f.Add($"INV-6 {where}: gap at order {i}"); break; }

        // INV-7 arms and spokes never collide: arm bearings are multiples of 60,
        //       spoke bearings are those plus 30.
        foreach (var v in GlyphCipher.VerbRingOrder)
        {
            double a = GlyphCipher.VerbAngle(v);
            foreach (double arm in GlyphCipher.ArmAngles)
            {
                double d = Math.Abs(((a - arm) % 360.0 + 360.0) % 360.0);
                if (d < 1e-9 || Math.Abs(d - 360.0) < 1e-9)
                    f.Add($"INV-7 {where}: spoke {v} at {a} collides with an arm");
            }
        }

        return f;
    }
}
