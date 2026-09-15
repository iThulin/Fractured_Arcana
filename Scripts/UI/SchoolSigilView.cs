using Godot;
using System;

// ============================================================
// SchoolSigilView.cs
//
// Purpose:        Per-school watermark drawn behind a card half's rules
//                 text (card face decor, layer 2). One procedural motif
//                 per school with one canonical seed, so the mark is the
//                 same on every card of the school. Enchanter (cipher) and
//                 Elementalist (element-lit star) are the exceptions
//                 and vary per card, because their marks carry information.
//                 Enchanter delegates to the existing GlyphCipherView. Drawn at UITheme.CardSigilAlpha, the
//                 one deliberate exception to "nothing under the text".
// Layer:          UI
// Collaborators:  CardUi.cs (creates one per split half),
//                 GlyphCipherView.cs (Enchanter motif),
//                 ElementColors.cs (Elementalist hex tints)
// See:            claude/session_log_2026-09-13_card_face_typography.md
// ============================================================

/// <summary>
/// Draws a school's watermark motif into its own rect. Call
/// <see cref="SetHalf"/> and the control redraws. Square aspect: the motif is
/// inscribed in the rect and centred. Everything is ink at watermark alpha,
/// except the Elementalist star, whose points light in the half's element colours.
/// </summary>
public partial class SchoolSigilView : Control
{
    private CardSchool _school = CardSchool.Adept;
    private string[] _tags = Array.Empty<string>();
    private uint _seed;
    private GlyphCipherView _cipher;

    /// <summary>Assigns the half whose motif to draw. <paramref name="cardId"/> and <paramref name="whichHalf"/> seed the variation.</summary>
    public void SetHalf(CardHalf half, string cardId, string whichHalf)
    {
        _school = half?.School ?? CardSchool.Adept;
        _tags = half?.Tags ?? Array.Empty<string>();

        // One canonical mark per school, so it can be learned and recognised at a
        // glance. Only the two motifs that carry information vary per card: the
        // Enchanter cipher (a per-spell encoding by construction) and the
        // Elementalist star (which points light up follows the half's element
        // tags). Ruled 2026-09-14.
        // The Elementalist star needs no seed: it is a function of the tag set.
        _seed = _school == CardSchool.Enchanter
            ? Fnv1a((cardId ?? "") + "/" + (whichHalf ?? ""))
            : Fnv1a("school/" + _school);

        if (_school == CardSchool.Enchanter)
        {
            if (_cipher == null)
            {
                _cipher = new GlyphCipherView
                {
                    Name = "Cipher",
                    Lod = CipherLod.Card,
                    DarkBackground = false,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                AddChild(_cipher);
                _cipher.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            }
            _cipher.PaperColor = UITheme.SurfaceLight;
            _cipher.Modulate = new Color(1f, 1f, 1f, UITheme.CardSigilAlpha * UITheme.CardSigilCipherBoost);
            _cipher.Visible = !string.IsNullOrEmpty(cardId)
                && _cipher.SetSpell(cardId, whichHalf, half);
        }
        else if (_cipher != null)
        {
            _cipher.Visible = false;
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_school == CardSchool.Enchanter) return;   // the cipher child draws itself

        var rng = new RandomNumberGenerator { Seed = _seed };
        float s = Mathf.Min(Size.X, Size.Y);
        var c = Size * 0.5f;
        float r = s * 0.46f;
        var ink = UITheme.CardInk;
        ink.A = UITheme.CardSigilAlpha;
        float w = Mathf.Max(1.5f, s * 0.022f);

        switch (_school)
        {
            case CardSchool.Adept: DrawSeal(rng, c, r, ink, w); break;
            case CardSchool.Elementalist: DrawElementStar(c, r, ink, w); break;
            case CardSchool.Druid: DrawLeafRing(rng, c, r, ink, w); break;
            case CardSchool.Necromancer: DrawCandle(c, r, ink, w); break;
            case CardSchool.Tinker: DrawSchematic(rng, c, r, ink, w); break;
            case CardSchool.Arcanist: DrawArcaneCircle(c, r, ink, w); break;
            case CardSchool.Chronomancer: DrawAstrolabe(rng, c, r, ink, w); break;
        }
    }

    // ── Adept: wax seal. A filled disc with a wobbly rim, an inner ring and
    //    a spoked star. Rotation and wobble are the seeded parts.
    private void DrawSeal(RandomNumberGenerator rng, Vector2 c, float r, Color ink, float w)
    {
        float rot = rng.RandfRange(0f, Mathf.Tau);
        var rim = new Vector2[48];
        for (int i = 0; i < rim.Length; i++)
        {
            float a = rot + i * Mathf.Tau / rim.Length;
            float rr = r * (0.96f + rng.RandfRange(-0.035f, 0.035f));
            rim[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
        }
        var fill = ink; fill.A *= 0.35f;
        DrawColoredPolygon(rim, fill);
        DrawArc(c, r * 0.72f, 0f, Mathf.Tau, 64, ink, w, true);
        int spokes = rng.RandiRange(5, 7);
        for (int i = 0; i < spokes; i++)
        {
            float a = rot + i * Mathf.Tau / spokes;
            DrawLine(c, c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.62f, ink, w, true);
        }
        DrawCircle(c, r * 0.12f, ink, true, -1f, true);
    }

    // ── Elementalist: a four-point star inside a hex, one point per element
    //    at a fixed bearing (Fire N, Storm E, Ice S, Earth W). A point the half
    //    carries is filled in its element colour with a burst of rays past the
    //    ring; a point it lacks is an empty ink outline. The mark is a function
    //    of the tag set alone, so it reads like a compass once learned.
    private static readonly string[] StarOrder = { "fire", "storm", "ice", "earth" };

    private void DrawElementStar(Vector2 c, float r, Color ink, float w)
    {
        // Hex, pointy-top, the board reference.
        var hex = new Vector2[7];
        for (int k = 0; k < 7; k++)
        {
            float a = Mathf.Pi / 6f + k * Mathf.Pi / 3f;
            hex[k] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.98f;
        }
        DrawPolyline(hex, ink, w * 0.7f, true);
        DrawArc(c, r * 0.55f, 0f, Mathf.Tau, 64, ink, w, true);

        for (int i = 0; i < StarOrder.Length; i++)
        {
            string el = StarOrder[i];
            bool has = Array.Exists(_tags, t => string.Equals(t, el, StringComparison.OrdinalIgnoreCase));
            float a = -Mathf.Pi / 2f + i * Mathf.Pi / 2f;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var nrm = new Vector2(-dir.Y, dir.X);
            float baseHalf = r * 0.13f;
            var pts = new[]
            {
                c + dir * r * 0.08f,
                c + nrm * baseHalf + dir * r * 0.20f,
                c + dir * r * 0.62f,
                c - nrm * baseHalf + dir * r * 0.20f,
            };
            if (has)
            {
                var col = ElementColors.Get(el);
                var fill = col; fill.A = UITheme.CardSigilAlpha * UITheme.CardSigilTintBoost;
                DrawColoredPolygon(pts, fill);
                DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, ink, w, true);
                var ray = col; ray.A = UITheme.CardSigilAlpha * UITheme.CardSigilTintBoost;
                for (int k = -2; k <= 2; k++)
                {
                    float aa = a + k * 0.16f;
                    float len = k == 0 ? 0.98f : 0.85f;
                    var rd = new Vector2(Mathf.Cos(aa), Mathf.Sin(aa));
                    DrawLine(c + rd * r * 0.68f, c + rd * r * len, ray, w, true);
                }
            }
            else
            {
                DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, ink, w * 0.7f, true);
            }
        }
        DrawCircle(c, r * 0.08f, ink, true, -1f, true);
    }

    // ── Druid: a ring of leaves, each a pointed ellipse with a midrib. Leaf
    //    count and rotation are seeded.
    private void DrawLeafRing(RandomNumberGenerator rng, Vector2 c, float r, Color ink, float w)
    {
        int leaves = rng.RandiRange(7, 9);
        float rot = rng.RandfRange(0f, Mathf.Tau);
        float len = r * 0.58f, half = r * 0.17f, inner = r * 0.36f;
        for (int i = 0; i < leaves; i++)
        {
            float a = rot + i * Mathf.Tau / leaves;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var nrm = new Vector2(-dir.Y, dir.X);
            var b = c + dir * inner;
            var t = c + dir * (inner + len);
            // Outline: out along one side of the midrib, back along the other.
            var pts = new Vector2[13];
            for (int k = 0; k < 13; k++)
            {
                bool outbound = k <= 6;
                float u = outbound ? k / 6f : (12 - k) / 6f;
                float bulge = Mathf.Sin(u * Mathf.Pi) * half * (outbound ? 1f : -1f);
                pts[k] = b.Lerp(t, u) + nrm * bulge;
            }
            DrawPolyline(pts, ink, w, true);
            DrawLine(b, t, ink, w * 0.7f, true);
        }
        DrawArc(c, inner * 0.7f, 0f, Mathf.Tau, 48, ink, w, true);
    }

    // ── Necromancer: a vigil candle. Holder, taper, teardrop flame, a faint
    //    halo. Remembrance, not a grave. Fixed geometry; the seed is unused.
    private void DrawCandle(Vector2 c, float r, Color ink, float w)
    {
        // Holder: base line, cup.
        DrawLine(new Vector2(c.X - r * 0.50f, c.Y + r * 0.75f), new Vector2(c.X + r * 0.50f, c.Y + r * 0.75f), ink, w, true);
        DrawPolyline(new[]
        {
            new Vector2(c.X - r * 0.32f, c.Y + r * 0.55f), new Vector2(c.X - r * 0.32f, c.Y + r * 0.75f),
            new Vector2(c.X + r * 0.32f, c.Y + r * 0.75f), new Vector2(c.X + r * 0.32f, c.Y + r * 0.55f),
            new Vector2(c.X - r * 0.32f, c.Y + r * 0.55f),
        }, ink, w, true);

        // Taper, its top slightly canted where the wax has run.
        DrawPolyline(new[]
        {
            new Vector2(c.X - r * 0.20f, c.Y + r * 0.55f), new Vector2(c.X - r * 0.20f, c.Y - r * 0.20f),
            new Vector2(c.X + r * 0.20f, c.Y - r * 0.25f), new Vector2(c.X + r * 0.20f, c.Y + r * 0.55f),
        }, ink, w, true);

        // Wick and teardrop flame.
        DrawLine(new Vector2(c.X, c.Y - r * 0.25f), new Vector2(c.X, c.Y - r * 0.40f), ink, w * 0.7f, true);
        var flame = new Vector2[25];
        for (int k = 0; k < 25; k++)
        {
            float a = Mathf.Pi / 2f + (k / 24f) * Mathf.Tau;
            float up = Mathf.Max(0f, Mathf.Sin(a));
            float rr = r * 0.18f * (1f + 0.9f * up * up);
            flame[k] = new Vector2(c.X + Mathf.Cos(a) * r * 0.16f, c.Y - r * 0.50f - Mathf.Sin(a) * rr);
        }
        DrawPolyline(flame, ink, w, true);

        // Halo.
        var halo = ink; halo.A *= 0.6f;
        DrawArc(new Vector2(c.X, c.Y - r * 0.50f), r * 0.50f, 0f, Mathf.Tau, 48, halo, w * 0.6f, true);
    }

    // ── Tinker: a gear outline with a dimension line across it. Tooth count
    //    and the dimension's angle are seeded.
    private void DrawSchematic(RandomNumberGenerator rng, Vector2 c, float r, Color ink, float w)
    {
        int teeth = rng.RandiRange(8, 12);
        float ro = r * 0.78f, ri = r * 0.64f;
        var pts = new Vector2[teeth * 4 + 1];
        for (int i = 0; i < teeth; i++)
        {
            float a0 = i * Mathf.Tau / teeth, a1 = a0 + Mathf.Tau / teeth * 0.5f;
            float a2 = a0 + Mathf.Tau / teeth * 0.25f, a3 = a0 + Mathf.Tau / teeth * 0.75f;
            pts[i * 4 + 0] = c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * ri;
            pts[i * 4 + 1] = c + new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * ro;
            pts[i * 4 + 2] = c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * ro;
            pts[i * 4 + 3] = c + new Vector2(Mathf.Cos(a3), Mathf.Sin(a3)) * ri;
        }
        pts[^1] = pts[0];
        DrawPolyline(pts, ink, w, true);
        DrawArc(c, r * 0.22f, 0f, Mathf.Tau, 32, ink, w, true);

        float da = rng.RandfRange(0f, Mathf.Pi);
        var dir = new Vector2(Mathf.Cos(da), Mathf.Sin(da));
        var nrm = new Vector2(-dir.Y, dir.X);
        var a = c - dir * r * 0.95f + nrm * r * 0.2f;
        var b = c + dir * r * 0.95f + nrm * r * 0.2f;
        DrawLine(a, b, ink, w * 0.8f, true);
        DrawLine(a - nrm * r * 0.08f, a + nrm * r * 0.08f, ink, w * 0.8f, true);
        DrawLine(b - nrm * r * 0.08f, b + nrm * r * 0.08f, ink, w * 0.8f, true);
    }

    // ── Arcanist: an arcane circle. Two squares turned 45 degrees inside a
    //    ring, nodes at the eight points, a small inner ring. Fixed geometry.
    private void DrawArcaneCircle(Vector2 c, float r, Color ink, float w)
    {
        DrawArc(c, r * 0.90f, 0f, Mathf.Tau, 64, ink, w, true);
        DrawArc(c, r * 0.30f, 0f, Mathf.Tau, 32, ink, w * 0.7f, true);
        float rr = r * 0.78f;
        for (int sq = 0; sq < 2; sq++)
        {
            var pts = new Vector2[5];
            for (int k = 0; k < 4; k++)
            {
                float a = -Mathf.Pi / 2f + sq * Mathf.Pi / 4f + k * Mathf.Pi / 2f;
                pts[k] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
            }
            pts[4] = pts[0];
            DrawPolyline(pts, ink, w, true);
        }
        for (int k = 0; k < 8; k++)
        {
            float a = -Mathf.Pi / 2f + k * Mathf.Pi / 4f;
            DrawCircle(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr, w * 1.5f, ink, true, -1f, true);
        }
    }

    // ── Chronomancer: an astrolabe. Throne ring at the top, mater with a
    //    ticked limb, an off-centre ecliptic ring and a small inner ring for the
    //    rete with five star pointers, and the rule across. The rule's angle is
    //    the one seeded part.
    private void DrawAstrolabe(RandomNumberGenerator rng, Vector2 c, float r, Color ink, float w)
    {
        // Drop the body a little so the throne fits in the square.
        var m = new Vector2(c.X, c.Y + r * 0.06f);
        float R = r * 0.90f;

        // Throne.
        DrawArc(new Vector2(m.X, m.Y - R * 1.02f), R * 0.10f, 0f, Mathf.Tau, 24, ink, w, true);

        // Mater and limb.
        DrawArc(m, R, 0f, Mathf.Tau, 72, ink, w, true);
        DrawArc(m, R * 0.86f, 0f, Mathf.Tau, 64, ink, w * 0.7f, true);
        for (int k = 0; k < 36; k++)
        {
            float a = k * Mathf.Tau / 36f;
            float inner = (k % 3 == 0) ? 0.86f : 0.90f;
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(m + d * R * inner, m + d * R, ink, w * 0.7f, true);
        }

        // Rete: ecliptic ring off centre, inner ring, star pointers.
        DrawArc(new Vector2(m.X, m.Y - R * 0.22f), R * 0.55f, 0f, Mathf.Tau, 64, ink, w, true);
        DrawArc(m, R * 0.28f, 0f, Mathf.Tau, 32, ink, w * 0.7f, true);
        var pointers = new (float angle, float len)[] { (-2.2f, 0.70f), (-0.6f, 0.75f), (0.9f, 0.62f), (2.6f, 0.68f), (-1.3f, 0.50f) };
        foreach (var (angle, len) in pointers)
        {
            var from = m + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * R * 0.28f;
            var to = m + new Vector2(Mathf.Cos(angle + 0.25f), Mathf.Sin(angle + 0.25f)) * R * len;
            DrawLine(from, to, ink, w, true);
            var barb = to + new Vector2(Mathf.Cos(angle - 0.9f), Mathf.Sin(angle - 0.9f)) * R * 0.07f;
            DrawLine(to, barb, ink, w, true);
        }

        // Rule.
        float ra = rng.RandfRange(-0.9f, -0.2f);
        var rd = new Vector2(Mathf.Cos(ra), Mathf.Sin(ra));
        DrawLine(m - rd * R * 0.95f, m + rd * R * 0.95f, ink, w * 1.3f, true);
        DrawCircle(m, w * 1.6f, ink, true, -1f, true);
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261u;
        foreach (char ch in s) { h ^= ch; h *= 16777619u; }
        return h;
    }
}
