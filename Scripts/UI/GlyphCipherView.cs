using Godot;
using System;

// ============================================================
// GlyphCipherView.cs
//
// Purpose:        Draws a CipherGlyph into a Control via _Draw.
//                 Used for card art, the inspection zoom, and as
//                 the source Control the tile-decal baker renders.
//                 Owns the LOD composites and the draw-on reveal.
// Layer:          UI
// Collaborators:  GlyphCipher.cs (the stroke source),
//                 GlyphCipherTags.cs (semantic extraction),
//                 GlyphCipherTexture.cs (bakes this Control),
//                 UITheme.cs (all colours)
// See:            docs/glyph_cipher_spec_v2.md §8 — LOD composites
// ============================================================

/// <summary>Which composite to render. The generator output is identical in all three; only the compositing differs.</summary>
public enum CipherLod
{
    /// <summary>Hex-tile decal, ~64px. Stave dimmed to texture, hub and spokes boosted. The function layer must survive here.</summary>
    Tile,
    /// <summary>Card art, ~180px. Both layers at full weight.</summary>
    Card,
    /// <summary>Inspection zoom, 384px+. Adds faint pips on the unused spoke bearings so the ring is learnable.</summary>
    Inspection
}

/// <summary>
/// Renders a <see cref="CipherGlyph"/> as a painterly inked radial stave. Set
/// <see cref="Glyph"/> (or call <see cref="SetSpell"/>) and the control redraws. Square
/// aspect: the sigil is inscribed in the largest circle that fits, centred.
/// </summary>
public partial class GlyphCipherView : Control
{
    // ── LOD composite table ─────────────────────────────────────────
    //
    // The pixel floors are not cosmetic. Unit weights scale by render RADIUS, so at a
    // 64px tile the function layer is 0.032 * 32 = 1.0px before the boost — the one
    // layer carrying gameplay information would be the least visible thing on the
    // tile. Floors are inert at card scale and above.
    private readonly struct Profile
    {
        public readonly float IdentityAlpha, IdentityWeightMul, BackingAlpha, FunctionWeightMul, MinPxIdentity, MinPxFunction;
        public readonly bool Pips;
        public Profile(float ia, float iw, float backing, float fw, float mpi, float mpf, bool pips)
        { IdentityAlpha = ia; IdentityWeightMul = iw; BackingAlpha = backing; FunctionWeightMul = fw; MinPxIdentity = mpi; MinPxFunction = mpf; Pips = pips; }
    }

    // Tile carries a backing disc and a nearly-opaque stave; card and inspection carry
    // neither, because they are drawn onto a surface this code controls.
    //
    // The stave was originally dimmed to 0.30 on tiles, to foreground the function layer.
    // That was tuned against controlled backdrops — paper, and a flat dark swatch — and it
    // fails completely on a real board: pale ink at 30% over bright grass has almost no
    // contrast, and the sigil reads as a floating hub with nothing attached. Foregrounding
    // is now carried entirely by WEIGHT (the function layer is ~3x the stave's width at
    // this LOD), which is terrain-independent, rather than by making the stave faint.
    private static Profile ProfileFor(CipherLod lod) => lod switch
    {
        // The tile stave is thickened 1.7x and fully opaque, and this is not a taste
        // call — it is a minification fix. The ring and hub survive at distance because
        // the ring is drawn procedurally in the shader (sharp at any scale) and the hub
        // is a large solid area; the stave was ~2.2px in a 256px bake against ~6.6px for
        // the rose, so the mip chain averaged the thin strokes toward transparent first
        // and the sigil lost its identity layer as the camera pulled back. Widening the
        // bake is the only part of that the texture can fix; the shader handles the rest.
        //
        // Tile backing is 0 because glyph_sigil.gdshader draws it instead. It has to:
        // the enclosing ring sits OUTSIDE the baked sigil, so a baked disc would stop
        // short of it and leave the ring floating on bare grass. Rendering a Tile-LOD
        // view through a Control (the F11 gallery) therefore shows no backing — that is
        // the dev preview being honest about what the texture actually contains.
        CipherLod.Tile       => new Profile(1.00f, 1.70f, 0.00f, 1.6f, 1.0f, 2.6f, false),
        CipherLod.Inspection => new Profile(1.00f, 1.00f, 0.00f, 1.0f, 1.0f, 1.6f, true),
        _                    => new Profile(1.00f, 1.00f, 0.00f, 1.0f, 1.0f, 1.6f, false),
    };

    private CipherGlyph _glyph;
    private CipherLod _lod = CipherLod.Card;
    private float _progress = 1f;
    private bool _darkBackground;
    private Color _paper = UITheme.CipherPaper;

    /// <summary>The sigil to draw. Setting this queues a redraw.</summary>
    public CipherGlyph Glyph
    {
        get => _glyph;
        set { _glyph = value; QueueRedraw(); }
    }

    /// <summary>Which composite to render.</summary>
    public CipherLod Lod
    {
        get => _lod;
        set { if (_lod != value) { _lod = value; QueueRedraw(); } }
    }

    /// <summary>
    /// Draw-on reveal, 0..1, over the ordered strokes: arms grow outward in reading order,
    /// each followed by its crossbars and ornament, then the function spokes. At 1 the glyph
    /// is whole. The rim is always drawn; it is not part of the reveal.
    /// </summary>
    public float Progress
    {
        get => _progress;
        set { float v = Mathf.Clamp(value, 0f, 1f); if (!Mathf.IsEqualApprox(v, _progress)) { _progress = v; QueueRedraw(); } }
    }

    /// <summary>Use the light ink variant, for glyphs drawn over a dark board rather than card stock.</summary>
    public bool DarkBackground
    {
        get => _darkBackground;
        set { if (_darkBackground != value) { _darkBackground = value; QueueRedraw(); } }
    }

    /// <summary>
    /// The colour behind the glyph. Only the ALLY hub reads it — that hub is a filled disc
    /// with a punched centre, and the punch has to match whatever it sits on. An outlined
    /// ring would vanish into the arms crossing behind it at tile scale, which is why the
    /// punch exists at all.
    /// </summary>
    public Color PaperColor
    {
        get => _paper;
        set { _paper = value; QueueRedraw(); }
    }

    /// <summary>Builds and assigns the glyph for a spell half. Returns false if the half could not be encoded.</summary>
    public bool SetSpell(string cardId, string half, CardHalf data)
    {
        var g = GlyphCipherTags.BuildFor(cardId, half, data);
        Glyph = g;
        return g != null;
    }

    public override void _Ready()
    {
        Resized += QueueRedraw;
        MouseFilter = MouseFilterEnum.Ignore;   // pure decoration; never eats clicks
    }

    public override void _Draw()
    {
        if (_glyph?.Strokes == null) return;

        Vector2 size = Size;
        float diameter = Mathf.Min(size.X, size.Y);
        if (diameter <= 2f) return;
        float radius = diameter * 0.5f;
        Vector2 centre = size * 0.5f;

        var p = ProfileFor(_lod);
        Color ink = _darkBackground ? UITheme.CipherInkLight : UITheme.CipherInk;
        Color inkA = new Color(ink, ink.A * p.IdentityAlpha);
        Color fn = UITheme.CipherFunction;

        int reveal = Mathf.RoundToInt(_glyph.OrderedCount * _progress);

        Vector2 ToScreen(CipherPoint pt) => centre + new Vector2((float)pt.X * radius, (float)pt.Y * radius);

        float identityW = Mathf.Max(p.MinPxIdentity, (float)GlyphCipher.WeightIdentity * radius * p.IdentityWeightMul);
        float functionW = Mathf.Max(p.MinPxFunction, (float)GlyphCipher.WeightFunction * radius * p.FunctionWeightMul);

        // Backing first, under everything, so the composite no longer depends on what
        // terrain happens to be under the tile.
        if (p.BackingAlpha > 0.001f)
        {
            // Stacked discs rather than one flat fill: a single hard-edged circle on
            // grass reads as a sticker, whereas an accumulating vignette reads as
            // scorched ground. Six steps is enough to hide the banding at 128px.
            Color back = UITheme.CipherTileBacking;
            const int steps = 6;
            float perStep = back.A * p.BackingAlpha / steps * 1.6f;
            for (int i = 0; i < steps; i++)
            {
                float rr = radius * (1f - i / (float)steps * 0.45f);
                DrawCircle(centre, rr, new Color(back, perStep), true, -1f, true);
            }
        }

        if (p.Pips) DrawPips(ToScreen, radius, fn);

        // Order matters: rim, then the stave, then the function layer on top. The
        // function layer is the readable one and must never be occluded by the stave.
        foreach (var s in _glyph.Strokes)
        {
            if (s.Layer != CipherLayer.Rim) continue;
            DrawStrokePolyline(s, ToScreen, inkA, Mathf.Max(p.MinPxIdentity, (float)s.Weight * radius * p.IdentityWeightMul));
        }

        foreach (var s in _glyph.Strokes)
        {
            if (s.Layer != CipherLayer.Identity) continue;
            if (s.Order >= 0 && s.Order >= reveal) continue;
            if (s.Mark == CipherMark.None) DrawStrokePolyline(s, ToScreen, inkA, identityW);
            else if (MarkerRevealed(s, reveal)) DrawMarker(s, ToScreen, inkA, radius, identityW);
        }

        foreach (var s in _glyph.Strokes)
        {
            if (s.Layer != CipherLayer.Function) continue;
            if (s.Order >= 0 && s.Order >= reveal) continue;
            if (s.Mark == CipherMark.None) DrawStrokePolyline(s, ToScreen, fn, functionW);
            else if (MarkerRevealed(s, reveal)) DrawMarker(s, ToScreen, fn, radius, functionW);
        }
    }

    // Markers hang off ordered strokes rather than carrying their own index. The start
    // disc appears immediately (it is where reading begins); everything else once the
    // reveal is past halfway, and the hub only at the very end so the sigil "seals".
    private bool MarkerRevealed(CipherStroke s, int reveal)
    {
        if (_progress >= 1f) return true;
        if (s.Mark == CipherMark.Start) return true;
        if (s.Mark == CipherMark.Hub) return reveal >= _glyph.OrderedCount;
        return reveal >= _glyph.OrderedCount / 2;
    }

    /// <summary>
    /// Draws one polyline with ROUND CAPS.
    ///
    /// Godot's DrawPolyline has no line-cap or joint control — every stroke ends in a
    /// flat butt cap. The reference renderer this design was tuned against used SVG's
    /// stroke-linecap="round", and the difference is not subtle on a stave: an arm and
    /// its crossbars are short strokes, and flat ends make the ink read as machined
    /// rather than inked. Capping by hand with a disc at each end costs two extra draw
    /// calls per stroke and restores it. Joints are left mitred — the polylines here are
    /// jittered straight runs, so no joint bends far enough for it to show.
    /// </summary>
    private void DrawStrokePolyline(CipherStroke s, Func<CipherPoint, Vector2> toScreen, Color col, float width)
    {
        if (s.Points.Length < 2) return;
        var v = new Vector2[s.Points.Length];
        for (int i = 0; i < s.Points.Length; i++) v[i] = toScreen(s.Points[i]);
        DrawPolyline(v, col, width, true);

        if (s.Closed) return;                       // the rim needs no caps
        float cap = width * 0.5f;
        if (cap < 0.6f) return;                     // below this the cap is invisible anyway
        DrawCircle(v[0], cap, col, true, -1f, true);
        DrawCircle(v[v.Length - 1], cap, col, true, -1f, true);
    }

    private void DrawPips(Func<CipherPoint, Vector2> toScreen, float radius, Color fn)
    {
        var faint = new Color(fn, 0.22f);
        float w = Mathf.Max(1f, 0.006f * radius);
        foreach (var v in GlyphCipher.VerbRingOrder)
        {
            if ((_glyph.Verbs & v) != 0) continue;
            var at = toScreen(GlyphCipher.Polar(GlyphCipher.VerbAngle(v), GlyphCipher.SpokeRadius));
            DrawArc(at, (float)GlyphCipher.MarkSpokeTip * radius * 0.6f, 0f, Mathf.Tau, 12, faint, w, true);
        }
    }

    private void DrawMarker(CipherStroke s, Func<CipherPoint, Vector2> toScreen, Color col, float radius, float lineW)
    {
        Vector2 at = toScreen(s.Points[0]);
        float size = (float)s.Weight * radius;

        switch (s.Mark)
        {
            case CipherMark.Start:
            case CipherMark.Dot:
            case CipherMark.SpokeTip:
                // DrawCircle's `antialiased` parameter defaults to FALSE. Every filled
                // disc in this file passes it explicitly; a jaggy 4px dot is very
                // visible next to antialiased strokes.
                DrawCircle(at, size, col, true, -1f, true);
                break;

            case CipherMark.Terminal:
            case CipherMark.OpenDot:
            case CipherMark.Retrace:
                DrawArc(at, size, 0f, Mathf.Tau, 20, col, lineW, true);
                break;

            case CipherMark.Hub:
                DrawHub(at, size, col);
                break;
        }
    }

    /// <summary>
    /// The recipient, as the hub's shape. Filled, not outlined: at 64px an outlined diamond
    /// disappears into the six arms crossing behind it, and the recipient is the single most
    /// useful thing to read off a tile at a glance.
    /// </summary>
    private void DrawHub(Vector2 at, float size, Color col)
    {
        switch (_glyph.Target)
        {
            case CipherTarget.Self:
                DrawCircle(at, size, col, true, -1f, true);
                break;

            case CipherTarget.Ally:
                DrawCircle(at, size, col, true, -1f, true);
                DrawCircle(at, size * 0.46f, _paper, true, -1f, true);
                break;

            case CipherTarget.Tile:
                FillPolygonSmooth(new[]
                {
                    at + new Vector2(0, -size), at + new Vector2(size, 0),
                    at + new Vector2(0, size),  at + new Vector2(-size, 0)
                }, col);
                break;

            default: // Enemy
                FillPolygonSmooth(new[]
                {
                    at + new Vector2(0, -size * 1.05f),
                    at + new Vector2(size * 0.91f, size * 0.58f),
                    at + new Vector2(-size * 0.91f, size * 0.58f)
                }, col);
                break;
        }
    }

    /// <summary>
    /// Filled polygon with a softened edge.
    ///
    /// DrawColoredPolygon has NO antialiasing parameter — its edges are hard. That is
    /// tolerable on a large shape and not tolerable on the hub, which is the single
    /// most-read mark on the glyph and is often only ~20px across. Stroking the outline
    /// with a thin antialiased polyline in the same colour feathers the edge at
    /// essentially no cost. The diamond and triangle hubs are the only polygons drawn
    /// here, so this is not a hot path.
    /// </summary>
    private void FillPolygonSmooth(Vector2[] pts, Color col)
    {
        DrawColoredPolygon(pts, col);

        var loop = new Vector2[pts.Length + 1];
        Array.Copy(pts, loop, pts.Length);
        loop[pts.Length] = pts[0];
        DrawPolyline(loop, col, 1.25f, true);
    }
}
