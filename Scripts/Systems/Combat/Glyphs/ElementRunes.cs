using System;
using System.Collections.Generic;

// ============================================================
// ElementRunes.cs
//
// Purpose:        The eight elemental imbuement runes, authored as
//                 CipherStroke sets so they are drawn by the SAME
//                 renderer that draws an Enchanter's spell sigil.
// Layer:          Combat / Glyphs
// Collaborators:  GlyphCipher.cs (unit space, weights, jitter
//                 constants — NOT its generator), GlyphCipherView.cs
//                 (the renderer), GlyphCipherTexture.cs (the bake),
//                 ImbuementOverlay.cs (the consumer)
// See:            docs/imbuement_painterly_redesign_v1.md §3.3
// ============================================================
//
// WHY THESE ARE AUTHORED AND NOT GENERATED
//
// The obvious move is to run the element names through GlyphCipher.Build and
// get eight sigils for free, in guaranteed-identical hand. That would be
// wrong, and it is worth stating why before someone "finishes the job".
//
// A spell sigil is allowed to be unreadable on first sight. There are forty-two
// of them, they are learned over a campaign, and the cipher's whole premise is
// that the shape IS the encoded name — arbitrary by design.
//
// An element rune is the opposite. There are eight, forever, and the player
// must read one across the board in a quarter second while deciding whether to
// step onto that tile. Element is targetable and consumable state
// (`element_tile`, ConsumeElementTileEffect, ImbuePathEffect). Generating them
// would trade the one property that matters — instant recognition — for the
// one property that does not.
//
// So the SHAPES are conserved from the analytic SDFs they replace: a flame, a
// six-fold snowflake, a bolt, a diamond, waves, a swirl, a hexagram, an eye.
// What changes is the HAND. Every stroke here is a jittered polyline with
// round caps, drawn at the cipher's own weights, enclosed by the cipher's rim.
// Same tradition, same tools, different purpose — which is exactly the fiction.
//
// ── Coordinate space ────────────────────────────────────────────────────
// Cipher unit space: centre (0,0), rim radius 1.0, +Y DOWN (screen
// convention). Runes occupy r <= ~0.62 so the rim never crowds them.
//
// ── Layer discipline ────────────────────────────────────────────────────
// Identity + Rim ONLY. No Function layer, deliberately.
//
// The cipher splits identity from function because a spell has both a name and
// a job. An element has only a name. More practically: the function layer is
// drawn in UITheme.CipherFunction (rose), and these runes are baked to a
// texture whose RGB is DISCARDED — the imbuement shader takes alpha only and
// supplies colour from the tile's element tint. A rose stroke would bake to
// the same alpha as an ink stroke and the distinction would vanish silently.
// One layer, one colour, no lie.
// ============================================================

/// <summary>
/// Builds the eight elemental runes as <see cref="CipherGlyph"/>s renderable by
/// <see cref="GlyphCipherView"/>. Deterministic: each element's jitter is seeded from
/// its own name, so a rune looks the same in every session and on every machine.
/// </summary>
public static class ElementRunes
{
    /// <summary>Rim radius for the enclosing circle. Inside the cipher's 1.0 so the rune reads as a smaller, humbler mark than a spell sigil.</summary>
    private const double RimR = 0.90;

    /// <summary>Jitter amplitude for rune strokes. Between the cipher's arm (0.008) and crossbar (0.005) jitter — these strokes are longer than crossbars and want a visible waver.</summary>
    private const double Jit = 0.007;

    /// <summary>Jitter for the enclosing rim. Small: a wobbly circle reads as a mistake, not as a hand.</summary>
    private const double RimJit = 0.004;

    // ── Public entry ────────────────────────────────────────────────

    private static readonly Dictionary<TileElementType, CipherGlyph> Cache = new();

    /// <summary>
    /// Returns the rune for <paramref name="element"/>, building it on first request.
    /// Returns null for <see cref="TileElementType.None"/> and anything unmapped —
    /// callers must treat null as "keep whatever marker you already had", never as
    /// "draw nothing". A tile with no element marker is a gameplay bug.
    /// </summary>
    public static CipherGlyph Build(TileElementType element)
    {
        if (element == TileElementType.None) return null;
        if (Cache.TryGetValue(element, out var hit)) return hit;

        var strokes = new List<CipherStroke>();
        var rng = new Rng(GlyphCipher.Fnv1a32("element_rune:" + element));

        Rim(ref rng, strokes);

        switch (element)
        {
            case TileElementType.Fire:      Fire(ref rng, strokes);      break;
            case TileElementType.Frost:     Frost(ref rng, strokes);     break;
            case TileElementType.Lightning: Lightning(ref rng, strokes); break;
            case TileElementType.Earth:     Earth(ref rng, strokes);     break;
            case TileElementType.Water:     Water(ref rng, strokes);     break;
            case TileElementType.Air:       Air(ref rng, strokes);       break;
            case TileElementType.Arcane:    Arcane(ref rng, strokes);    break;
            case TileElementType.Shadow:    Shadow(ref rng, strokes);    break;
            default: return null;
        }

        var g = new CipherGlyph
        {
            Strokes = strokes.ToArray(),
            Letters = element.ToString().ToUpperInvariant(),
            ArmCount = 0,
            DeepestArm = 0,
            CrossbarCount = 0,
            RetraceCount = 0,
            // Zero means the draw-on reveal never gates anything: every stroke here
            // carries Order = -1 and every marker passes MarkerRevealed at any
            // progress. These runes appear whole, because a tile's element is either
            // true or it is not — there is no "half imbued".
            OrderedCount = 0,
            // Unused: no stroke here carries CipherMark.Hub, so Target is never read.
            Target = CipherTarget.Tile,
            // Must stay None. Non-None would light spokes that do not exist.
            // (CipherLod.Inspection would then draw pips for the UNSET verbs, i.e.
            //  all six — which is why these are baked at CipherLod.Card.)
            Verbs = CipherVerb.None,
            SeedKey = "element:" + element,
        };

        Cache[element] = g;
        return g;
    }

    /// <summary>Drops the built runes. Only useful when hot-editing this file's geometry.</summary>
    public static void ClearCache() => Cache.Clear();

    // ── The eight ───────────────────────────────────────────────────

    // FIRE — a flame: outer silhouette, inner curl, two rising sparks.
    private static void Fire(ref Rng rng, List<CipherStroke> s)
    {
        // Thirteen knots, not eight. The first draft used eight and read as a tent:
        // too few samples around the shoulders and the silhouette straightens into
        // facets. A flame is all curve — it needs the density.
        Closed(ref rng, s, new[]
        {
            P( 0.03, -0.62), P( 0.19, -0.36), P( 0.25, -0.12), P( 0.34, 0.12),
            P( 0.29,  0.36), P( 0.15,  0.51), P( 0.00,  0.55), P(-0.16, 0.50),
            P(-0.29,  0.34), P(-0.34,  0.10), P(-0.25, -0.14), P(-0.21, -0.32),
            P(-0.07, -0.25),   // the lick: dips inward before rising to the tip
        }, 3, Jit);

        Closed(ref rng, s, new[]
        {
            P( 0.01, 0.41), P( 0.14, 0.25), P( 0.12, 0.03),
            P( 0.00, -0.16), P(-0.12, 0.05), P(-0.11, 0.27),
        }, 3, Jit * 0.8);

        s.Add(Dot(P(-0.48, -0.12), GlyphCipher.MarkDot * 0.85));
        s.Add(Dot(P( 0.46,  0.08), GlyphCipher.MarkDot * 0.70));
    }

    // FROST — six-fold snowflake. The most load-bearing rune in the set: frost is
    // the element with the most mechanical text attached to it, so its shape gets
    // the least licence. Two branch pairs per spoke, exactly as the SDF had.
    private static void Frost(ref Rng rng, List<CipherStroke> s)
    {
        for (int i = 0; i < 6; i++)
        {
            double a = 60.0 * i;
            Open(ref rng, s, new[] { At(a, 0.05), At(a, 0.62) }, 6, Jit);

            Branch(ref rng, s, a, 0.28, 40.0, 0.18);
            Branch(ref rng, s, a, 0.46, 40.0, 0.13);
        }
        s.Add(Dot(P(0, 0), GlyphCipher.MarkDot * 0.9));
    }

    // LIGHTNING — the standard bolt polygon, drawn as an outline rather than a
    // filled shape so it carries the same stroke weight as everything else.
    private static void Lightning(ref Rng rng, List<CipherStroke> s)
    {
        Closed(ref rng, s, new[]
        {
            P( 0.18, -0.60), P(-0.22, 0.00), P(-0.02, 0.00),
            P(-0.16,  0.60), P( 0.24, -0.02), P( 0.04, -0.02),
        }, 3, Jit);
    }

    // EARTH — diamond and a planted core. Two strata lines in the lower half give
    // it weight without adding a shape to recognise.
    private static void Earth(ref Rng rng, List<CipherStroke> s)
    {
        Closed(ref rng, s, new[]
        {
            P(0, -0.54), P(0.54, 0), P(0, 0.54), P(-0.54, 0),
        }, 5, Jit);

        Open(ref rng, s, new[] { P(-0.26, 0.12), P(0.26, 0.12) }, 5, Jit * 0.8);
        Open(ref rng, s, new[] { P(-0.16, 0.28), P(0.16, 0.28) }, 4, Jit * 0.8);
        s.Add(Dot(P(0, -0.16), GlyphCipher.MarkDot));
    }

    // WATER — three travelling waves. Sampled from a sine rather than knotted, so
    // the crests stay even and only the hand moves them.
    private static void Water(ref Rng rng, List<CipherStroke> s)
    {
        double[] rows = { -0.26, 0.00, 0.26 };
        for (int r = 0; r < rows.Length; r++)
        {
            const int n = 15;
            var pts = new CipherPoint[n];
            for (int i = 0; i < n; i++)
            {
                double u = (double)i / (n - 1);
                double x = -0.52 + 1.04 * u;
                double y = rows[r] + 0.075 * Math.Sin(u * Math.PI * 3.0 + r * 0.9);
                if (i > 0 && i < n - 1) { x += rng.Sym() * Jit; y += rng.Sym() * Jit; }
                pts[i] = new CipherPoint(x, y);
            }
            s.Add(Stroke(pts, false));
        }
    }

    // AIR — three nested arcs, each open on a different bearing so the eye reads a
    // rotation that is not actually there.
    private static void Air(ref Rng rng, List<CipherStroke> s)
    {
        Arc(ref rng, s, 0.56,  20.0, 265.0, 16);
        Arc(ref rng, s, 0.38, 140.0, 385.0, 14);
        Arc(ref rng, s, 0.20, 260.0, 505.0, 12);
        s.Add(Ring(At(20.0, 0.56), GlyphCipher.MarkOpenDot * 0.8));
    }

    // ARCANE — hexagram in the rim. Two triangles, drawn as two strokes so the
    // interlace reads at a glance.
    private static void Arcane(ref Rng rng, List<CipherStroke> s)
    {
        Closed(ref rng, s, new[] { At(0, 0.56), At(120, 0.56), At(240, 0.56) }, 5, Jit);
        Closed(ref rng, s, new[] { At(60, 0.56), At(180, 0.56), At(300, 0.56) }, 5, Jit);
        s.Add(Dot(P(0, 0), GlyphCipher.MarkDot * 0.85));
    }

    // SHADOW — the eye. The only rune whose meaning is "something is watching"
    // rather than "something is here", and the only one that needs a filled centre
    // to land.
    private static void Shadow(ref Rng rng, List<CipherStroke> s)
    {
        Open(ref rng, s, new[]
        {
            P(-0.56, 0.00), P(-0.30, -0.24), P(0.00, -0.32), P(0.30, -0.24), P(0.56, 0.00),
        }, 4, Jit);

        Open(ref rng, s, new[]
        {
            P(-0.56, 0.00), P(-0.30, 0.24), P(0.00, 0.32), P(0.30, 0.24), P(0.56, 0.00),
        }, 4, Jit);

        s.Add(Ring(P(0, 0), GlyphCipher.MarkOpenDot * 1.15));
        s.Add(Dot(P(0, 0), GlyphCipher.MarkDot * 0.85));
    }

    // ── Construction helpers ────────────────────────────────────────

    private static void Rim(ref Rng rng, List<CipherStroke> s)
    {
        const int n = 72;
        var pts = new CipherPoint[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double a = 360.0 * i / n;
            double r = RimR + (i > 0 && i < n ? rng.Sym() * RimJit : 0.0);
            pts[i] = At(a, r);
        }
        // Endpoint forced back onto the exact start so the ring closes cleanly —
        // the same rule the cipher's own rim follows.
        pts[n] = pts[0];

        s.Add(new CipherStroke
        {
            Layer = CipherLayer.Rim,
            Points = pts,
            Weight = GlyphCipher.WeightRim,
            Mark = CipherMark.None,
            Closed = true,
            Order = -1,
        });
    }

    /// <summary>A branch pair hanging off a snowflake spoke at radius <paramref name="r0"/>.</summary>
    private static void Branch(ref Rng rng, List<CipherStroke> s,
                               double spokeDeg, double r0, double spreadDeg, double len)
    {
        CipherPoint root = At(spokeDeg, r0);
        for (int sign = -1; sign <= 1; sign += 2)
        {
            CipherPoint dir = At(spokeDeg + spreadDeg * sign, 1.0);
            var tip = new CipherPoint(root.X + dir.X * len, root.Y + dir.Y * len);
            Open(ref rng, s, new[] { root, tip }, 3, Jit * 0.7);
        }
    }

    private static void Arc(ref Rng rng, List<CipherStroke> s,
                            double r, double a0, double a1, int n)
    {
        var pts = new CipherPoint[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            double a = a0 + (a1 - a0) * t;
            double rr = r + (i > 0 && i < n - 1 ? rng.Sym() * Jit : 0.0);
            pts[i] = At(a, rr);
        }
        s.Add(Stroke(pts, false));
    }

    /// <summary>Chains jittered straight runs through <paramref name="knots"/>, leaving the path open.</summary>
    private static void Open(ref Rng rng, List<CipherStroke> s, CipherPoint[] knots, int perSeg, double amp)
        => s.Add(Stroke(Chain(ref rng, knots, perSeg, amp, false), false));

    /// <summary>As <see cref="Open"/>, but returns to the first knot exactly.</summary>
    private static void Closed(ref Rng rng, List<CipherStroke> s, CipherPoint[] knots, int perSeg, double amp)
        => s.Add(Stroke(Chain(ref rng, knots, perSeg, amp, true), true));

    /// <summary>
    /// Samples each knot-to-knot segment to <paramref name="perSeg"/> interior steps.
    /// KNOTS ARE EXACT — jitter only touches interior samples, so corners stay sharp and
    /// adjacent strokes stay registered. Same rule as GlyphCipher.Line, and for the same
    /// reason: a shape whose corners waver reads as a mistake, not as a hand.
    /// </summary>
    private static CipherPoint[] Chain(ref Rng rng, CipherPoint[] knots, int perSeg, double amp, bool close)
    {
        int segs = close ? knots.Length : knots.Length - 1;
        var pts = new List<CipherPoint>(segs * perSeg + 1);

        for (int k = 0; k < segs; k++)
        {
            CipherPoint a = knots[k];
            CipherPoint b = knots[(k + 1) % knots.Length];
            for (int i = 0; i < perSeg; i++)
            {
                double t = (double)i / perSeg;
                double x = a.X + (b.X - a.X) * t;
                double y = a.Y + (b.Y - a.Y) * t;
                if (i > 0) { x += rng.Sym() * amp; y += rng.Sym() * amp; }
                pts.Add(new CipherPoint(x, y));
            }
        }
        pts.Add(close ? knots[0] : knots[knots.Length - 1]);
        return pts.ToArray();
    }

    private static CipherStroke Stroke(CipherPoint[] pts, bool closed) => new CipherStroke
    {
        Layer = CipherLayer.Identity,
        Points = pts,
        Weight = GlyphCipher.WeightIdentity,
        Mark = CipherMark.None,
        Closed = closed,
        Order = -1,
    };

    private static CipherStroke Dot(CipherPoint at, double size) => Marker(at, size, CipherMark.Dot);
    private static CipherStroke Ring(CipherPoint at, double size) => Marker(at, size, CipherMark.OpenDot);

    private static CipherStroke Marker(CipherPoint at, double size, CipherMark mark) => new CipherStroke
    {
        Layer = CipherLayer.Identity,
        Points = new[] { at },
        Weight = size,
        Mark = mark,
        Closed = false,
        Order = -1,
    };

    private static CipherPoint P(double x, double y) => new CipherPoint(x, y);
    private static CipherPoint At(double thetaDeg, double r) => GlyphCipher.Polar(thetaDeg, r);

    // ── RNG ─────────────────────────────────────────────────────────
    //
    // A local copy of GlyphCipher's xorshift32, because that type is private there
    // and MUST stay private: the cipher's draw order is part of its wire format and
    // is pinned by 42 golden checksums. Nothing in this file may ever draw from that
    // stream. Same algorithm, separate stream, separate seed namespace
    // ("element_rune:"), zero risk to the goldens.
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
}
