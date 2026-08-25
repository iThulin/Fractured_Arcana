using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// GlyphCipher.cs
//
// Purpose:        Procedural sigil generator for Enchanter spells.
//                 Encodes a spell's Name as a radial stave (a
//                 galdrastafur: arms radiating from a hub, each
//                 bearing crossbar ticks for its letters), overlaid
//                 with a rose hub-and-spoke mark encoding the
//                 casting function. Pure C#: NO Godot types, no
//                 rendering, no I/O. Deterministic from (cardId,
//                 half) alone.
// Layer:          Data / generation
// Collaborators:  GlyphCipherTags.cs (extracts recipient + verbs
//                 from a compiled CardHalf), GlyphCipherView.cs,
//                 GlyphCipherTexture.cs, GlyphCipherSelfTest.cs
// See:            docs/glyph_cipher_spec_v2.md
// ============================================================
//
// WHY v2 REPLACED v1
//
// v1 connected the letter nodes with chords across the interior of
// the circle. That is a random walk on a ring, and no amount of
// tuning the bow, the weights or the jitter makes a random walk look
// deliberate -- it reads as scribble, because it IS scribble. The
// fix was structural, not parametric: replace the mark language.
//
// A radial stave gets order from its six-fold skeleton and identity
// from what hangs off it. Two intermediate attempts are worth
// recording so they are not retried:
//
//   * A single vertical stave with branches (a "fern") reads as
//     designed but collapses into sameness -- every name becomes the
//     same feather, because a comb of branches on a straight line has
//     almost no silhouette variation.
//   * A circular ogham inscription round the rim reads well and is
//     excellent at tile scale, but the tick ring registers as a gauge
//     or a dial rather than a rune.
//
// Distinctiveness in v2 comes from four independent channels: arm
// count (3-6), arm depth (1-3), crossbar lengths, and the per-arm
// terminal ornament. The ornaments matter most -- they put the
// variation in the silhouette, where it survives being shrunk.
//
// DETERMINISM CONTRACT
//
//   1. The RNG is seeded from the STABLE CARD ID plus the half
//      ("enchanter_snare_glyph#top"), never the display name.
//      Display names are localisable and get reworded during balance
//      passes. This project already has a logged bug from exactly
//      that distinction (CardDatabase.GetByName matching display
//      names instead of ids).
//   2. System.String.GetHashCode is NOT used and must never be:
//      .NET randomises string hashing per process. FNV-1a over UTF-8
//      is used instead.
//   3. THE ORDER OF RNG DRAWS IS PART OF THE FORMAT. Reordering two
//      statements that both consume the stream changes every glyph
//      downstream. Draw sites are numbered below; GlyphCipherSelfTest
//      asserts a checksum over the whole 42-half corpus.
//   4. All arithmetic is double. The generator is cross-checked
//      against a reference implementation at 1e-4 quantisation.
//
// ============================================================

/// <summary>Which of the three drawing layers a stroke belongs to.</summary>
public enum CipherLayer
{
    /// <summary>The enclosing circle. Always drawn.</summary>
    Rim,
    /// <summary>The stave: arms, crossbars and ornaments. The encoded Name. Dims to texture at tile scale.</summary>
    Identity,
    /// <summary>The hub and spokes. Casting function. Gameplay information; never reduced.</summary>
    Function
}

/// <summary>Marker kind for point-strokes. <see cref="CipherStroke.Points"/> holds one point and <see cref="CipherStroke.Weight"/> the marker's size.</summary>
public enum CipherMark
{
    /// <summary>An ordinary polyline: an arm, a crossbar, or an ornament stroke.</summary>
    None,
    /// <summary>Filled disc on the first arm, near the hub. Where reading starts.</summary>
    Start,
    /// <summary>Open ring on the last letter's crossbar. Where reading ends.</summary>
    Terminal,
    /// <summary>Ring around a crossbar whose letter repeats the one before it.</summary>
    Retrace,
    /// <summary>Filled dot terminal ornament.</summary>
    Dot,
    /// <summary>Open ring terminal ornament.</summary>
    OpenDot,
    /// <summary>Filled dot at the end of a function spoke.</summary>
    SpokeTip,
    /// <summary>The central hub. Its shape is read from <see cref="CipherGlyph.Target"/>.</summary>
    Hub
}

/// <summary>Who the spell is for. Encoded as the shape of the central hub.</summary>
public enum CipherTarget
{
    /// <summary>Filled disc.</summary>
    Self,
    /// <summary>Filled disc with a punched centre.</summary>
    Ally,
    /// <summary>Filled diamond.</summary>
    Tile,
    /// <summary>Filled triangle.</summary>
    Enemy
}

/// <summary>
/// What the spell does, as a spoke from the hub. Exactly six, capped deliberately:
/// the function layer cannot be dimmed to texture the way the stave can, so every
/// node added is a permanent tax on tile-scale legibility. Declaration order is
/// also the order of the spokes around the hub.
/// </summary>
[Flags]
public enum CipherVerb
{
    None = 0,
    /// <summary>Protect, shield, heal, summon a guardian.</summary>
    Ward = 1 << 0,
    /// <summary>Displace anything: self, ally, or enemy.</summary>
    Move = 1 << 1,
    /// <summary>Create glyphs or persistent inscriptions on the board.</summary>
    Inscribe = 1 << 2,
    /// <summary>Manipulate the existing glyph network, or draw on it for resources.</summary>
    Invoke = 1 << 3,
    /// <summary>Control or debuff a unit.</summary>
    Bind = 1 << 4,
    /// <summary>Deal damage.</summary>
    Strike = 1 << 5
}

/// <summary>A point in cipher unit space: centre (0,0), rim radius 1.0, +Y down (screen convention).</summary>
public readonly struct CipherPoint
{
    public readonly double X;
    public readonly double Y;
    public CipherPoint(double x, double y) { X = x; Y = y; }
    public double Length => Math.Sqrt(X * X + Y * Y);
    public override string ToString() => $"({X:F4},{Y:F4})";
}

/// <summary>
/// One drawable element. Polyline strokes carry a sampled path in <see cref="Points"/> and a
/// stroke width in <see cref="Weight"/>. Marker strokes carry a single point and reuse
/// <see cref="Weight"/> as the marker's radius.
/// </summary>
public sealed class CipherStroke
{
    /// <summary>Which layer this belongs to. Drives colour, weight multiplier, and LOD opacity.</summary>
    public CipherLayer Layer;

    /// <summary>Polyline in unit space, or a single point for markers.</summary>
    public CipherPoint[] Points;

    /// <summary>Stroke width in unit space for polylines; marker size for markers. Multiply by render RADIUS.</summary>
    public double Weight;

    /// <summary>Marker kind, or <see cref="CipherMark.None"/> for a polyline.</summary>
    public CipherMark Mark;

    /// <summary>True for the rim, which is a closed loop.</summary>
    public bool Closed;

    /// <summary>
    /// Reveal index for the draw-on animation: arms grow outward in reading order, each
    /// followed by its crossbars and its ornament, then the function spokes. -1 for the rim
    /// and for markers, which are not traced.
    /// </summary>
    public int Order;
}

/// <summary>The generated sigil: an ordered stroke set plus the decode that produced it.</summary>
public sealed class CipherGlyph
{
    /// <summary>Rim first, then the stave in reading order, then the function layer, then markers.</summary>
    public CipherStroke[] Strokes;

    /// <summary>The normalised letters actually encoded (A-Z, uppercase, everything else stripped).</summary>
    public string Letters;

    /// <summary>How many arms carry letters, 1..6.</summary>
    public int ArmCount;

    /// <summary>Letters on the fullest arm, 1..4. Together with <see cref="ArmCount"/> this is the silhouette class.</summary>
    public int DeepestArm;

    /// <summary>Crossbars drawn. Always equals <c>Letters.Length</c>, since every letter gets exactly one.</summary>
    public int CrossbarCount;

    /// <summary>Crossbars marked as repeating the letter before them.</summary>
    public int RetraceCount;

    /// <summary>Number of strokes carrying a reveal index.</summary>
    public int OrderedCount;

    /// <summary>Recipient. Read by the renderer to pick the hub's shape.</summary>
    public CipherTarget Target;

    /// <summary>Function verbs, one spoke each.</summary>
    public CipherVerb Verbs;

    /// <summary>The stable key the RNG was seeded from. Useful in error messages.</summary>
    public string SeedKey;
}

/// <summary>
/// Generates a <see cref="CipherGlyph"/> for a spell half. Stateless and thread-safe; the
/// only mutable state is the per-call RNG. See docs/glyph_cipher_spec_v2.md for the grammar
/// and the worked decodes <c>GlyphCipherSelfTest</c> asserts against.
/// </summary>
public static class GlyphCipher
{
    // ── Geometry (unit space) ────────────────────────────────────────
    /// <summary>Enclosing circle radius.</summary>
    public const double RimRadius = 1.00;

    /// <summary>Arms start here. Everything inside is the hub's plaza.</summary>
    public const double ArmR0 = 0.19;

    /// <summary>The deepest arm ends here.</summary>
    public const double ArmR1 = 0.87;

    /// <summary>Arms are drawn from here so they meet under the hub rather than stopping at its edge.</summary>
    public const double ArmInner = ArmR0 * 0.35;

    /// <summary>Fraction of arm length reserved past the last crossbar, for the ornament.</summary>
    public const double ArmOver = 0.17;

    /// <summary>An arm shallower than the deepest ends this far along the remaining distance to the rim.</summary>
    public const double ShortArm = 0.55;

    /// <summary>Crossbar half-length base. Half-length = ThisPlus slot × <see cref="BarSlotStep"/>.</summary>
    public const double BarMin = 0.045;

    /// <summary>Crossbar half-length per slot. Range over slots 1..13 is 0.061 … 0.253, wide enough to read at card scale.</summary>
    public const double BarSlotStep = 0.0160;

    /// <summary>Overhang on the short side of a rare-letter (inner-ring) crossbar, as a fraction of its half-length.</summary>
    public const double HalfStub = 0.12;

    /// <summary>Hub radius for <see cref="CipherTarget.Self"/> and <see cref="CipherTarget.Ally"/>.</summary>
    public const double HubRadius = 0.135;

    /// <summary>Where a function spoke ends.</summary>
    public const double SpokeRadius = 0.52;

    /// <summary>Maximum arms. Six, so they interleave exactly with the six spokes.</summary>
    public const int MaxArms = 6;

    // ── Stroke weights (unit space; multiply by render RADIUS) ───────
    /// <summary>Rim stroke width.</summary>
    public const double WeightRim = 0.016;

    /// <summary>Stave stroke width for arms, crossbars, and ornaments.</summary>
    public const double WeightIdentity = 0.017;

    /// <summary>Function spoke width. Nearly twice the stave: under protanopia the hue
    /// separation collapses and weight becomes the primary channel distinguishing the layers.</summary>
    public const double WeightFunction = 0.032;

    // ── Marker sizes ────────────────────────────────────────────────
    //
    // Enlarged in the second tuning pass, from (0.034, 0.044, 0.042) and
    // (0.028, 0.040). Two reasons, and the first is a correctness one: the tile
    // LOD draws the stave at 1.7x width to survive minification, and an open ring
    // whose radius stays put while its stroke thickens closes into a solid blob.
    // The second is that the arm-tip ornaments are the channel carrying silhouette
    // variety (spec §1.1), so anything that erases them costs distinctiveness at
    // exactly the range where distinctiveness is all you have left.
    //
    // These feed CipherStroke.Weight, so changing them changes the stroke data and
    // invalidates the goldens. That is intended (see the integration guide §4).
    /// <summary>Filled disc marking where reading starts.</summary>
    public const double MarkStart = 0.042;

    /// <summary>Open ring marking the last letter.</summary>
    public const double MarkTerminal = 0.054;

    /// <summary>Ring around a doubled letter's crossbar.</summary>
    public const double MarkRetrace = 0.050;

    /// <summary>Filled-dot terminal ornament.</summary>
    public const double MarkDot = 0.038;

    /// <summary>Open-ring terminal ornament.</summary>
    public const double MarkOpenDot = 0.055;

    /// <summary>Dot at the end of a function spoke.</summary>
    public const double MarkSpokeTip = 0.050;

    // ── Painterly jitter ────────────────────────────────────────────
    /// <summary>Interior-sample jitter on arms.</summary>
    public const double ArmJitter = 0.008;

    /// <summary>Interior-sample jitter on crossbars, ornaments and spokes.</summary>
    public const double BarJitter = 0.005;

    /// <summary>Interior-sample jitter on function spokes.</summary>
    public const double SpokeJitter = 0.006;

    /// <summary>Number of points on the rim polyline (the last repeats the first).</summary>
    public const int RimSamples = 96;

    /// <summary>
    /// Defensive cap on encoded letters. Not a design limit. The longest name in the
    /// Enchanter corpus is "Absolute Territory" at 17 and encodes fine. This exists so a
    /// pathological future name cannot allocate without bound.
    /// </summary>
    public const int MaxLetters = 24;

    /// <summary>Arm bearings, degrees clockwise from up. Six-fold, offset 30° from the spokes so the two layers interleave and never collide.</summary>
    public static readonly double[] ArmAngles = { 0.0, 60.0, 120.0, 180.0, 240.0, 300.0 };

    /// <summary>Spoke order around the hub, which is also the fixed red-path precedence.</summary>
    public static readonly CipherVerb[] VerbRingOrder =
    {
        CipherVerb.Ward, CipherVerb.Move, CipherVerb.Inscribe,
        CipherVerb.Invoke, CipherVerb.Bind, CipherVerb.Strike
    };

    // ── Letter table ────────────────────────────────────────────────
    //
    // Frequency-balanced, NOT A-M / N-Z. The outer set is the 13 most common English
    // letters and the inner set the 13 least common; each is alphabetical so a letter
    // is still findable by scanning. Membership is what distinguishes a full crossbar
    // from a one-sided one, so putting the common letters together keeps most ticks
    // symmetrical and makes the one-sided ones read as ornament.

    /// <summary>Common letters: full (two-sided) crossbars. Index + 1 is the slot.</summary>
    public const string OuterLetters = "ACDEHILNORSTU";

    /// <summary>Rare letters: one-sided crossbars. Index + 1 is the slot.</summary>
    public const string InnerLetters = "BFGJKMPQVWXYZ";

    /// <summary>Bearing of a function spoke, degrees clockwise from up.</summary>
    public static double VerbAngle(CipherVerb v)
    {
        for (int i = 0; i < VerbRingOrder.Length; i++)
            if (VerbRingOrder[i] == v) return 30.0 + 60.0 * i;
        return 30.0;
    }

    /// <summary>Hub radius for a recipient. Diamond and triangle need more area to read at the same weight as a disc.</summary>
    public static double HubSize(CipherTarget t) => t switch
    {
        CipherTarget.Tile  => HubRadius * 1.30,
        CipherTarget.Enemy => HubRadius * 1.40,
        _ => HubRadius
    };

    /// <summary>Polar to cartesian. Theta is degrees CLOCKWISE from straight up; +Y is down.</summary>
    public static CipherPoint Polar(double thetaDeg, double r)
    {
        double t = thetaDeg * Math.PI / 180.0;
        return new CipherPoint(r * Math.Sin(t), -r * Math.Cos(t));
    }

    /// <summary>Slot (1..13) and ring membership for a letter. False for anything outside A-Z.</summary>
    public static bool TryLetterSlot(char ch, out int slot, out bool common)
    {
        int i = OuterLetters.IndexOf(ch);
        if (i >= 0) { slot = i + 1; common = true; return true; }
        i = InnerLetters.IndexOf(ch);
        if (i >= 0) { slot = i + 1; common = false; return true; }
        slot = 0; common = false; return false;
    }

    /// <summary>Uppercases, strips everything outside A-Z, truncates to <see cref="MaxLetters"/>.</summary>
    public static string Normalise(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (char c0 in name)
        {
            char c = char.ToUpperInvariant(c0);
            if (c >= 'A' && c <= 'Z')
            {
                sb.Append(c);
                if (sb.Length >= MaxLetters) break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// How the letters divide across arms. Letters fill arms CONTIGUOUSLY (arm 0 holds the
    /// first m, arm 1 the next m), so reading is "walk arm 0 outward, then arm 1", with no
    /// interleaving to reconstruct.
    /// </summary>
    public static int[] ArmLayout(int letterCount)
    {
        int m = Math.Max(1, (letterCount + MaxArms - 1) / MaxArms);
        int arms = Math.Min(MaxArms, (letterCount + m - 1) / m);
        var counts = new int[arms];
        int left = letterCount;
        for (int i = 0; i < arms; i++) { counts[i] = Math.Min(m, left); left -= counts[i]; }
        return counts;
    }

    /// <summary>The stable seed key for a spell half. Card id, never display name.</summary>
    public static string SeedKey(string cardId, string half) => $"{cardId}#{half}";

    /// <summary>FNV-1a 32 over UTF-8. Specified and stable; <c>string.GetHashCode</c> is not.</summary>
    public static uint Fnv1a32(string s)
    {
        uint h = 0x811C9DC5u;
        byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
        for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 0x01000193u; }
        return h;
    }

    // xorshift32: small, fully specified, and identical in any language with 32-bit
    // unsigned wraparound. System.Random is none of those across runtime versions.
    private struct Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }
        public uint NextU32()
        {
            uint x = _s;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _s = x; return x;
        }
        /// <summary>[0,1)</summary>
        public double Unit() => NextU32() / 4294967296.0;
        /// <summary>[-1,1)</summary>
        public double Sym() => Unit() * 2.0 - 1.0;
    }

    /// <summary>
    /// Builds the sigil for one spell half.
    /// </summary>
    /// <param name="cardId">Stable JSON card id, e.g. "enchanter_snare_glyph". Seeds the RNG.</param>
    /// <param name="half">"top" or "bottom". Each half is its own spell and its own glyph.</param>
    /// <param name="cipherName">Name to encode. Must be a STABLE English string (see the localisation note in the spec).</param>
    /// <param name="target">Recipient, from <see cref="GlyphCipherTags"/>.</param>
    /// <param name="verbs">Function verbs, from <see cref="GlyphCipherTags"/>.</param>
    public static CipherGlyph Build(string cardId, string half, string cipherName,
                                    CipherTarget target, CipherVerb verbs)
    {
        string letters = Normalise(cipherName);
        if (letters.Length == 0)
            throw new ArgumentException(
                $"cipher name '{cipherName}' for {cardId}#{half} contains no A-Z letters", nameof(cipherName));

        string seedKey = SeedKey(cardId, half);
        var rng = new Rng(Fnv1a32(seedKey));

        int[] arms = ArmLayout(letters.Length);
        int deepest = 0;
        foreach (int c in arms) if (c > deepest) deepest = c;

        double usable = (ArmR1 - ArmR0) * (1.0 - ArmOver);
        double dr = deepest > 1 ? usable / (deepest - 1) : 0.0;
        double rFirst = ArmR0 + (deepest == 1 ? usable * 0.5 : 0.0);

        var strokes = new List<CipherStroke>(48);

        // ── Rim ─────────────────────────────────────────────────────
        // DRAW SITE 1: exactly RimSamples+1 draws. The final point is overwritten with
        // the first to close the loop, but its draw still happened. Do not "optimise"
        // that away or the whole stream shifts.
        var rim = new CipherPoint[RimSamples + 1];
        for (int i = 0; i <= RimSamples; i++)
        {
            double a = 2.0 * Math.PI * i / RimSamples;
            double r = RimRadius + rng.Sym() * 0.006;
            rim[i] = new CipherPoint(r * Math.Sin(a), -r * Math.Cos(a));
        }
        rim[RimSamples] = rim[0];
        strokes.Add(new CipherStroke
        {
            Layer = CipherLayer.Rim, Points = rim, Weight = WeightRim,
            Mark = CipherMark.None, Closed = true, Order = -1
        });

        int order = 0, retraces = 0, bars = 0, letterIdx = 0;
        char prev = '\0';
        bool havePrev = false;
        double lastBarRadius = rFirst;
        double lastArmAngle = ArmAngles[0];

        for (int ai = 0; ai < arms.Length; ai++)
        {
            double ang = ArmAngles[ai];
            int count = arms[ai];
            double lastR = rFirst + dr * (count - 1);

            // The DEEPEST arm reaches the rim; shallower arms stop short. Normalising
            // every arm to full length made every six-arm name the same silhouette,
            // which is the sameness that sank the earlier single-stave design.
            double armEnd = count == deepest ? ArmR1 : lastR + (ArmR1 - lastR) * ShortArm;

            // DRAW SITE 2: the arm, 7 samples => 5 interior => 10 draws.
            strokes.Add(new CipherStroke
            {
                Layer = CipherLayer.Identity, Points = Line(ref rng, Polar(ang, ArmInner), Polar(ang, armEnd), 7, ArmJitter),
                Weight = WeightIdentity, Mark = CipherMark.None, Order = order
            });
            order++;

            var u = Polar(ang, 1.0);
            double px = -u.Y, py = u.X;                    // unit normal to the arm

            for (int d = 0; d < count; d++)
            {
                char ch = letters[letterIdx++];
                if (!TryLetterSlot(ch, out int slot, out bool common))
                    throw new InvalidOperationException($"normalised letter '{ch}' is not in the table");

                double r = rFirst + dr * d;
                var c = Polar(ang, r);
                double halfLen = BarMin + slot * BarSlotStep;
                // Common letters get a full, symmetric crossbar; rare letters a one-sided
                // one. Symmetric-vs-one-sided is a far more legible binary at small size
                // than left-vs-right would be on a radial arm.
                double back = common ? halfLen : halfLen * HalfStub;

                // DRAW SITE 3: the crossbar, 4 samples => 2 interior => 4 draws.
                strokes.Add(new CipherStroke
                {
                    Layer = CipherLayer.Identity,
                    Points = Line(ref rng,
                                  new CipherPoint(c.X - px * back, c.Y - py * back),
                                  new CipherPoint(c.X + px * halfLen, c.Y + py * halfLen), 4, BarJitter),
                    Weight = WeightIdentity, Mark = CipherMark.None, Order = order
                });
                order++;
                bars++;

                if (havePrev && ch == prev)
                {
                    strokes.Add(Marker(CipherLayer.Identity, c, MarkRetrace, CipherMark.Retrace));
                    retraces++;
                }
                prev = ch; havePrev = true;
                lastBarRadius = r;
            }

            // DRAW SITE 4: the terminal ornament. Kinds 2, 3 and 5 draw; 0, 1 and 4 do not.
            TryLetterSlot(letters[letterIdx - 1], out int lastSlot, out _);
            Ornament(ref rng, strokes, ref order, ang, armEnd, lastSlot % 6);
            lastArmAngle = ang;
        }

        strokes.Add(Marker(CipherLayer.Identity, Polar(ArmAngles[0], ArmR0 * 0.62), MarkStart, CipherMark.Start));
        strokes.Add(Marker(CipherLayer.Identity, Polar(lastArmAngle, lastBarRadius), MarkTerminal, CipherMark.Terminal));

        // ── Function: hub and spokes ────────────────────────────────
        foreach (var v in VerbRingOrder)
        {
            if ((verbs & v) == 0) continue;
            double a = VerbAngle(v);
            // DRAW SITE 5: the spoke, 5 samples => 3 interior => 6 draws.
            strokes.Add(new CipherStroke
            {
                Layer = CipherLayer.Function,
                Points = Line(ref rng, Polar(a, HubRadius * 1.15), Polar(a, SpokeRadius), 5, SpokeJitter),
                Weight = WeightFunction, Mark = CipherMark.None, Order = order
            });
            order++;
            strokes.Add(Marker(CipherLayer.Function, Polar(a, SpokeRadius), MarkSpokeTip, CipherMark.SpokeTip));
        }
        strokes.Add(Marker(CipherLayer.Function, new CipherPoint(0.0, 0.0), HubSize(target), CipherMark.Hub));

        return new CipherGlyph
        {
            Strokes = strokes.ToArray(),
            Letters = letters,
            ArmCount = arms.Length,
            DeepestArm = deepest,
            CrossbarCount = bars,
            RetraceCount = retraces,
            OrderedCount = order,
            Target = target,
            Verbs = verbs,
            SeedKey = seedKey
        };
    }

    /// <summary>
    /// Terminal ornament for an arm, chosen by the last letter's slot mod 6. This is where
    /// silhouette variety lives: two names that both fill six arms still end in six
    /// different shapes, and silhouette is the channel that survives being shrunk to a tile.
    /// </summary>
    private static void Ornament(ref Rng rng, List<CipherStroke> strokes, ref int order,
                                 double ang, double r, int kind)
    {
        var u = Polar(ang, 1.0);
        double ux = u.X, uy = u.Y, px = -u.Y, py = u.X;
        var tip = Polar(ang, r);

        switch (kind)
        {
            case 0:                                   // plain, no ornament
                break;

            case 1:
                strokes.Add(Marker(CipherLayer.Identity, tip, MarkDot, CipherMark.Dot));
                break;

            case 2:                                   // fork
                for (int s = -1; s <= 1; s += 2)
                {
                    strokes.Add(new CipherStroke
                    {
                        Layer = CipherLayer.Identity,
                        Points = Line(ref rng, tip,
                                      new CipherPoint(tip.X + ux * 0.10 + px * 0.085 * s,
                                                      tip.Y + uy * 0.10 + py * 0.085 * s), 4, BarJitter),
                        Weight = WeightIdentity, Mark = CipherMark.None, Order = order
                    });
                    order++;
                }
                break;

            case 3:                                   // wide crossbar
                strokes.Add(new CipherStroke
                {
                    Layer = CipherLayer.Identity,
                    Points = Line(ref rng,
                                  new CipherPoint(tip.X - px * 0.11, tip.Y - py * 0.11),
                                  new CipherPoint(tip.X + px * 0.11, tip.Y + py * 0.11), 4, BarJitter),
                    Weight = WeightIdentity, Mark = CipherMark.None, Order = order
                });
                order++;
                break;

            case 4:
                strokes.Add(Marker(CipherLayer.Identity, tip, MarkOpenDot, CipherMark.OpenDot));
                break;

            default:                                  // chevron
                for (int s = -1; s <= 1; s += 2)
                {
                    strokes.Add(new CipherStroke
                    {
                        Layer = CipherLayer.Identity,
                        Points = Line(ref rng, tip,
                                      new CipherPoint(tip.X - ux * 0.085 + px * 0.085 * s,
                                                      tip.Y - uy * 0.085 + py * 0.085 * s), 4, BarJitter),
                        Weight = WeightIdentity, Mark = CipherMark.None, Order = order
                    });
                    order++;
                }
                break;
        }
    }

    private static CipherStroke Marker(CipherLayer layer, CipherPoint at, double size, CipherMark mark)
        => new CipherStroke
        {
            Layer = layer, Points = new[] { at }, Weight = size,
            Mark = mark, Closed = false, Order = -1
        };

    /// <summary>
    /// A straight run sampled to <paramref name="n"/> points. Endpoints are EXACT. Jitter is
    /// applied only to interior samples, so an arm and its crossbars stay registered with each
    /// other and the stave does not fray at its joins.
    /// </summary>
    private static CipherPoint[] Line(ref Rng rng, CipherPoint p0, CipherPoint p1, int n, double amp)
    {
        var pts = new CipherPoint[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            double x = p0.X + (p1.X - p0.X) * t;
            double y = p0.Y + (p1.Y - p0.Y) * t;
            if (i > 0 && i < n - 1)
            {
                // DRAW SITE 6: x then y, per interior sample.
                x += rng.Sym() * amp;
                y += rng.Sym() * amp;
            }
            pts[i] = new CipherPoint(x, y);
        }
        return pts;
    }
}
