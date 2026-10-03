using Godot;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

// ============================================================
// UnitNameplate.Intent.cs
//
// Purpose:        The enemy intent pill on top of a nameplate.
//                 One drawn icon per IntentKind (sword, arrow,
//                 swirl, burst, armor plate, droplet, push arrow),
//                 each in its own colour; the value in white when
//                 revealed and a dim "?" when not; then small state
//                 icons for stagger, Poise, Openings and a postponed
//                 turn.
//
//                 CombatManager's ASCII marker line (MOV4 AP3 SKIRM
//                 ...) is no longer drawn on the plate. It is
//                 translated into plain sentences in the pill's
//                 tooltip, where there is room to say what it means.
//
// Layer:          UI
// Collaborators:  UnitNameplate.cs, HealthBarRoot.cs (fills the
//                 intent fields), CombatManager.EnemyIntents.cs
//                 (IntentKind, marker tokens), UITheme.cs
// ============================================================

public partial class UnitNameplate
{
    private const float IntentPadX    = 6f;
    private const float IntentIcon    = 18f;
    private const float IntentStateGap = 6f;

    // ── Measure ──────────────────────────────────────────────────────

    private string IntentValueText()
        => _data.IntentRevealed ? _data.IntentValue.ToString() : "?";

    private float MeasureIntent()
    {
        float w = IntentPadX + IntentIcon + 5f;
        w += _bold.GetStringSize(IntentValueText(), HorizontalAlignment.Left, -1, IntentValueFont).X;
        foreach (float sw in IntentStateWidths())
            w += IntentStateGap + sw;
        return Mathf.Ceil(w + IntentPadX + 1f);
    }

    /// <summary>Widths of the state icons, in draw order.</summary>
    private IEnumerable<float> IntentStateWidths()
    {
        var d = _data;
        if (d.Staggered) yield return 11f;
        if (d.MaxPoise > 0) yield return d.MaxPoise * 8f - 2f;
        if (d.Openings > 0) yield return 12f + _bold.GetStringSize(d.Openings.ToString(), HorizontalAlignment.Left, -1, 12).X;
        if (d.Postponed > 0) yield return 10f;
        foreach (var tag in IntentTags())
            yield return TagWidth(tag);
    }

    // ── State tags (Defer / Advance, predicted reaction) ─────────────

    private const int TagFont = 11;
    private const float TagIcon = 12f;

    private readonly struct IntentTag
    {
        public readonly string Text;
        public readonly Color Color;
        public readonly bool Clock;
        public IntentTag(string text, Color color, bool clock) { Text = text; Color = color; Clock = clock; }
    }

    private static Color TemporalColor => ElementColors.Get("temporal");

    /// <summary>Small worded tags after the state icons, in draw order:
    /// DEFERRED (clock) or SPENT, then the reaction the intent would form.</summary>
    private List<IntentTag> IntentTags()
    {
        var tags = new List<IntentTag>();
        var d = _data;
        if (d.IntentTiming == IntentTiming.Deferred)
            tags.Add(new IntentTag("DEFERRED", TemporalColor.Lightened(0.15f), clock: true));
        else if (d.IntentTiming == IntentTiming.Spent)
            tags.Add(new IntentTag("SPENT", UITheme.TextSecondary, clock: false));
        if (d.IntentReaction != ElementReaction.None)
            tags.Add(new IntentTag(ElementReactions.DisplayName(d.IntentReaction).ToUpperInvariant(),
                                   ElementColors.Reaction(d.IntentReaction), clock: false));
        if (!string.IsNullOrEmpty(d.IntentNext))
            tags.Add(new IntentTag($"THEN {d.IntentNext}", TemporalColor.Lightened(0.35f) with { A = 0.85f }, clock: false));
        return tags;
    }

    private float TagWidth(IntentTag tag)
        => (tag.Clock ? TagIcon + 3f : 0f) + _bold.GetStringSize(tag.Text, HorizontalAlignment.Left, -1, TagFont).X;

    // ── Colour and text ──────────────────────────────────────────────

    private Color IntentKindColor() => _data.IntentKind switch
    {
        IntentKind.Attack       => UITheme.IntentAttack,
        IntentKind.RangedAttack => UITheme.IntentRanged,
        IntentKind.Channel      => UITheme.IntentChannel,
        IntentKind.Release      => UITheme.IntentRelease,
        IntentKind.Guard        => UITheme.IntentGuard,
        IntentKind.Imbue        => UITheme.IntentImbue,
        IntentKind.Shove        => UITheme.IntentShove,
        _                       => UITheme.TextSecondary,
    };

    private string IntentTitle()
    {
        string kind = _data.IntentKind switch
        {
            IntentKind.Attack       => "Attack",
            IntentKind.RangedAttack => "Ranged Attack",
            IntentKind.Channel      => "Channel",
            IntentKind.Release      => "Release",
            IntentKind.Guard        => "Guard",
            IntentKind.Imbue        => "Imbue",
            IntentKind.Shove        => "Shove",
            _                       => "Intent",
        };
        return _data.IntentRevealed ? $"{kind} {_data.IntentValue}" : $"{kind} (details hidden)";
    }

    private string IntentTooltip()
    {
        var d = _data;
        bool rv = d.IntentRevealed;
        int v = d.IntentValue;
        var sb = new StringBuilder();

        sb.Append(d.IntentKind switch
        {
            IntentKind.Attack       => rv ? $"Strikes the marked tile for {v} damage." : "Strikes a marked tile.",
            IntentKind.RangedAttack => rv ? $"Shoots the marked tile for {v} damage. Needs line of sight when it fires." : "Shoots a marked tile. Needs line of sight when it fires.",
            IntentKind.Channel      => rv ? $"Charging a blast of {v}. It lands on the marked tile next turn." : "Charging a blast. It lands on the marked tile next turn.",
            IntentKind.Release      => rv ? $"The channelled blast lands on the marked tile this turn for {v} damage." : "The channelled blast lands this turn.",
            IntentKind.Guard        => rv ? $"Moves to defend and gains {v} armor." : "Moves to defend and gains armor.",
            IntentKind.Imbue        => string.IsNullOrEmpty(d.IntentElement)
                                        ? "Writes its element onto the marked tiles."
                                        : $"Writes {d.IntentElement} onto the marked tiles.",
            IntentKind.Shove        => $"Force-moves a target {(d.IntentShoveTiles > 0 ? d.IntentShoveTiles : 3)} tiles along the marked path.",
            _                       => "Its plan is unknown.",
        });
        if (!rv)
            sb.Append(" Reveal it to see the number.");

        if (d.Staggered)
            sb.Append("\nStaggered: this action will be cancelled.");
        if (d.MaxPoise > 0)
            sb.Append($"\nPoise {d.Poise}/{d.MaxPoise}: staggers it can shrug off.");
        if (d.Openings > 0)
            sb.Append($"\nOpenings {d.Openings} placed on it.");
        if (d.Postponed > 0)
            sb.Append(d.Postponed == 1 ? "\nIts turn is postponed by 1." : $"\nIts turn is postponed by {d.Postponed}.");
        if (d.IntentTiming == IntentTiming.Deferred)
            sb.Append("\nDeferred: it still moves this turn, but the blow lands at the end of the round on the marked tile, on whoever stands there then.");
        else if (d.IntentTiming == IntentTiming.Spent)
            sb.Append("\nSpent: this attack already landed. It moves this turn but does not strike.");
        if (d.IntentReaction != ElementReaction.None)
            sb.Append($"\nWill form {ElementReactions.DisplayName(d.IntentReaction)} on the marked ground: {ElementReactions.Describe(d.IntentReaction)}");
        if (!string.IsNullOrEmpty(d.IntentNext))
            sb.Append($"\nNext turn it {d.IntentNextNote}. Where it strikes is decided when it plans.");

        var lines = IntentMarkerText.Translate(d.IntentMarkers);
        if (lines.Count > 0)
        {
            sb.Append('\n');
            foreach (var line in lines)
                sb.Append('\n').Append(line);
        }
        return sb.ToString();
    }

    // ── Draw ─────────────────────────────────────────────────────────

    private void DrawIntent()
    {
        var d = _data;
        if (!d.HasIntent)
            return;

        var r = _intentRect;
        bool spent = d.IntentTiming == IntentTiming.Spent;
        bool deferred = d.IntentTiming == IntentTiming.Deferred;
        Color kc = IntentKindColor();
        if (spent)
            kc = kc.Lerp(UITheme.TextDim, 0.6f);   // the blow is gone; the pill reads as a memory of it
        Color border = deferred ? TemporalColor
            : d.IntentRevealed ? kc : (kc.Darkened(0.35f) with { A = 0.85f });

        var sb = new StyleBoxFlat { BgColor = UITheme.BgDeep with { A = spent ? 0.70f : 0.94f }, BorderColor = border, AntiAliasing = true };
        sb.SetBorderWidthAll(d.IntentRevealed || deferred ? 2 : 1);
        sb.SetCornerRadiusAll(6);
        sb.ShadowColor = new Color(0, 0, 0, 0.45f);
        sb.ShadowSize = 3;
        DrawStyleBox(sb, r);

        float cy = r.Position.Y + r.Size.Y * 0.5f;
        float x = r.Position.X + IntentPadX;
        DrawIntentIcon(new Vector2(x + IntentIcon * 0.5f, cy), d.IntentKind, kc);
        x += IntentIcon + 5f;

        string value = IntentValueText();
        float asc = _bold.GetAscent(IntentValueFont);
        float desc = _bold.GetDescent(IntentValueFont);
        var pos = new Vector2(x, cy + (asc - desc) * 0.5f);
        Color vc = spent ? UITheme.TextDim : d.IntentRevealed ? Colors.White : UITheme.TextDim;
        DrawStringOutline(_bold, pos, value, HorizontalAlignment.Left, -1, IntentValueFont, 3, new Color(0, 0, 0, 0.9f));
        DrawString(_bold, pos, value, HorizontalAlignment.Left, -1, IntentValueFont, vc);
        float valueW = _bold.GetStringSize(value, HorizontalAlignment.Left, -1, IntentValueFont).X;
        if (spent)
            DrawLine(new Vector2(x - 1f, cy), new Vector2(x + valueW + 1f, cy), UITheme.TextSecondary, 1.6f, true);   // struck through
        x += valueW;

        if (d.Staggered)
        {
            x += IntentStateGap;
            DrawCrack(new Vector2(x + 5.5f, cy));
            x += 11f;
        }
        if (d.MaxPoise > 0)
        {
            x += IntentStateGap;
            for (int i = 0; i < d.MaxPoise; i++)
            {
                var c = new Vector2(x + 3f + i * 8f, cy);
                var dia = new[] { c + new Vector2(0, -4), c + new Vector2(3, 0), c + new Vector2(0, 4), c + new Vector2(-3, 0) };
                if (i < d.Poise)
                    DrawColoredPolygon(dia, UITheme.PlateArmor);
                else
                    DrawPolyline(new[] { dia[0], dia[1], dia[2], dia[3], dia[0] }, UITheme.PlateArmor.Darkened(0.35f), 1.2f, true);
            }
            x += d.MaxPoise * 8f - 2f;
        }
        if (d.Openings > 0)
        {
            x += IntentStateGap;
            var c = new Vector2(x + 5f, cy);
            DrawArc(c, 4.5f, 0, Mathf.Tau, 20, UITheme.Gold, 1.6f, true);
            DrawCircle(c, 1.6f, UITheme.Gold);
            string n = d.Openings.ToString();
            float a2 = _bold.GetAscent(12), d2 = _bold.GetDescent(12);
            DrawString(_bold, new Vector2(x + 12f, cy + (a2 - d2) * 0.5f), n, HorizontalAlignment.Left, -1, 12, UITheme.Gold);
            x += 12f + _bold.GetStringSize(n, HorizontalAlignment.Left, -1, 12).X;
        }
        if (d.Postponed > 0)
        {
            x += IntentStateGap;
            DrawHourglass(new Vector2(x + 5f, cy));
            x += 10f;
        }
        foreach (var tag in IntentTags())
        {
            x += IntentStateGap;
            if (tag.Clock)
            {
                DrawClock(new Vector2(x + TagIcon * 0.5f, cy), tag.Color);
                x += TagIcon + 3f;
            }
            float ta = _bold.GetAscent(TagFont), td = _bold.GetDescent(TagFont);
            var tp = new Vector2(x, cy + (ta - td) * 0.5f);
            DrawStringOutline(_bold, tp, tag.Text, HorizontalAlignment.Left, -1, TagFont, 3, new Color(0, 0, 0, 0.9f));
            DrawString(_bold, tp, tag.Text, HorizontalAlignment.Left, -1, TagFont, tag.Color);
            x += _bold.GetStringSize(tag.Text, HorizontalAlignment.Left, -1, TagFont).X;
        }
    }

    /// <summary>A small clock face: the deferred-attack mark.</summary>
    private void DrawClock(Vector2 c, Color col)
    {
        DrawCircle(c + Vector2.One, 5.6f, new Color(0, 0, 0, 0.6f));
        DrawArc(c, 5f, 0f, Mathf.Tau, 24, col, 1.6f, true);
        DrawLine(c, c + new Vector2(0, -3.6f), col, 1.5f, true);
        DrawLine(c, c + new Vector2(2.8f, 0.8f), col, 1.5f, true);
    }

    /// <summary>One silhouette per intent kind, drawn in an 18 px box around c.</summary>
    private void DrawIntentIcon(Vector2 c, IntentKind kind, Color col)
    {
        var shadow = new Color(0, 0, 0, 0.75f);
        switch (kind)
        {
            case IntentKind.Attack:
            {
                // Sword, point to the upper right.
                var blade = Rot(c, 45f, new[] { new Vector2(0, -9), new Vector2(2, -6.5f), new Vector2(2, 3), new Vector2(-2, 3), new Vector2(-2, -6.5f) });
                var guard = Rot(c, 45f, new[] { new Vector2(-5.5f, 3), new Vector2(5.5f, 3), new Vector2(5.5f, 5), new Vector2(-5.5f, 5) });
                var grip  = Rot(c, 45f, new[] { new Vector2(-1.2f, 5), new Vector2(1.2f, 5), new Vector2(1.2f, 8), new Vector2(-1.2f, 8) });
                Vector2 pommel = Rot(c, 45f, new[] { new Vector2(0, 9.2f) })[0];
                FillShadowed(blade, col, shadow);
                FillShadowed(guard, col.Darkened(0.2f), shadow);
                FillShadowed(grip, col.Darkened(0.45f), shadow);
                DrawCircle(pommel, 1.7f, col.Darkened(0.2f));
                DrawLine(Rot(c, 45f, new[] { new Vector2(0, -7) })[0], Rot(c, 45f, new[] { new Vector2(0, 2.5f) })[0], new Color(1, 1, 1, 0.45f), 1f, true);
                break;
            }
            case IntentKind.RangedAttack:
            {
                // Arrow, point to the upper right, with fletching.
                var head = Rot(c, 45f, new[] { new Vector2(0, -10), new Vector2(4, -4.5f), new Vector2(-4, -4.5f) });
                Vector2 s0 = Rot(c, 45f, new[] { new Vector2(0, -5) })[0];
                Vector2 s1 = Rot(c, 45f, new[] { new Vector2(0, 9) })[0];
                DrawLine(s0 + Vector2.One, s1 + Vector2.One, shadow, 2.4f, true);
                DrawLine(s0, s1, col, 2.2f, true);
                FillShadowed(head, col, shadow);
                // Fletching: two swept-back feathers at the tail.
                FillShadowed(Rot(c, 45f, new[] { new Vector2(0, 4.5f), new Vector2(-3.4f, 7.5f), new Vector2(-3.4f, 10.5f), new Vector2(0, 8f) }), col.Darkened(0.15f), shadow);
                FillShadowed(Rot(c, 45f, new[] { new Vector2(0, 4.5f), new Vector2(3.4f, 7.5f), new Vector2(3.4f, 10.5f), new Vector2(0, 8f) }), col.Darkened(0.15f), shadow);
                break;
            }
            case IntentKind.Channel:
            {
                // Inward swirl: power gathering.
                var pts = new List<Vector2>();
                for (int i = 0; i <= 40; i++)
                {
                    float t = i / 40f * Mathf.Tau * 1.6f;
                    float rad = 8.5f - t * 1.15f;
                    pts.Add(c + new Vector2(Mathf.Cos(t - Mathf.Pi / 2), Mathf.Sin(t - Mathf.Pi / 2)) * Mathf.Max(0.5f, rad));
                }
                var arr = pts.ToArray();
                var sh = new Vector2[arr.Length];
                for (int i = 0; i < arr.Length; i++) sh[i] = arr[i] + Vector2.One;
                DrawPolyline(sh, shadow, 2.8f, true);
                DrawPolyline(arr, col, 2.2f, true);
                DrawCircle(c, 1.8f, Colors.White);
                break;
            }
            case IntentKind.Release:
            {
                // Burst: the blast going off.
                FillShadowed(Star(c, 8, 9.5f, 3.8f), col, shadow);
                DrawCircle(c, 2.8f, Colors.White);
                break;
            }
            case IntentKind.Guard:
            {
                // The armor plate from the armor badge: a Guard grants armor.
                var hex = new[] { c + new Vector2(-4.5f, -8), c + new Vector2(4.5f, -8), c + new Vector2(8.5f, 0), c + new Vector2(4.5f, 8), c + new Vector2(-4.5f, 8), c + new Vector2(-8.5f, 0) };
                FillShadowed(hex, col.Darkened(0.45f), shadow);
                DrawPolyline(new[] { hex[0], hex[1], hex[2], hex[3], hex[4], hex[5], hex[0] }, col, 1.8f, true);
                var inner = new[] { c + new Vector2(-2.2f, -4), c + new Vector2(2.2f, -4), c + new Vector2(4.2f, 0), c + new Vector2(2.2f, 4), c + new Vector2(-2.2f, 4), c + new Vector2(-4.2f, 0) };
                DrawColoredPolygon(inner, col);
                break;
            }
            case IntentKind.Imbue:
            {
                // Droplet: an element poured onto the ground.
                var drop = new List<Vector2> { c + new Vector2(0, -9.5f) };
                for (int i = 0; i <= 16; i++)
                {
                    float a = Mathf.Pi * (-0.15f + 1.3f * i / 16f);
                    drop.Add(c + new Vector2(0, 2.5f) + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 5.8f);
                }
                FillShadowed(drop.ToArray(), col, shadow);
                DrawCircle(c + new Vector2(-2, 2), 1.4f, new Color(1, 1, 1, 0.6f));
                break;
            }
            case IntentKind.Shove:
            {
                // Push: a heavy arrow with motion lines behind it.
                var arrow = new[] { c + new Vector2(-3, -2.8f), c + new Vector2(3, -2.8f), c + new Vector2(3, -7), c + new Vector2(9.5f, 0), c + new Vector2(3, 7), c + new Vector2(3, 2.8f), c + new Vector2(-3, 2.8f) };
                FillShadowed(arrow, col, shadow);
                DrawLine(c + new Vector2(-9.5f, -5), c + new Vector2(-5.5f, -5), col, 1.8f, true);
                DrawLine(c + new Vector2(-10.5f, 0), c + new Vector2(-5, 0), col, 1.8f, true);
                DrawLine(c + new Vector2(-9.5f, 5), c + new Vector2(-5.5f, 5), col, 1.8f, true);
                break;
            }
            default:
            {
                float a = _bold.GetAscent(16), dd = _bold.GetDescent(16);
                DrawString(_bold, new Vector2(c.X - 9, c.Y + (a - dd) * 0.5f), "?", HorizontalAlignment.Center, 18, 16, col);
                break;
            }
        }
    }

    private void DrawCrack(Vector2 c)
    {
        var pts = new[] { c + new Vector2(1, -7), c + new Vector2(-2.5f, -2), c + new Vector2(2.5f, 1), c + new Vector2(-1, 7) };
        var sh = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++) sh[i] = pts[i] + Vector2.One;
        DrawPolyline(sh, new Color(0, 0, 0, 0.75f), 3f, true);
        DrawPolyline(pts, UITheme.ConditionDebuff.Lightened(0.15f), 2.2f, true);
    }

    private void DrawHourglass(Vector2 c)
    {
        Color col = UITheme.IntentChannel.Lightened(0.25f);
        DrawColoredPolygon(new[] { c + new Vector2(-4, -6), c + new Vector2(4, -6), c + new Vector2(0, 0) }, col);
        DrawColoredPolygon(new[] { c + new Vector2(0, 0), c + new Vector2(4, 6), c + new Vector2(-4, 6) }, col);
    }

    private void FillShadowed(Vector2[] pts, Color col, Color shadow)
    {
        var sh = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++)
            sh[i] = pts[i] + Vector2.One;
        DrawColoredPolygon(sh, shadow);
        DrawColoredPolygon(pts, col);
    }

    /// <summary>Rotates local points by <paramref name="degrees"/> (clockwise on
    /// screen) and moves them to <paramref name="c"/>.</summary>
    private static Vector2[] Rot(Vector2 c, float degrees, Vector2[] local)
    {
        float a = Mathf.DegToRad(degrees);
        float cs = Mathf.Cos(a), sn = Mathf.Sin(a);
        var o = new Vector2[local.Length];
        for (int i = 0; i < local.Length; i++)
            o[i] = c + new Vector2(local[i].X * cs - local[i].Y * sn, local[i].X * sn + local[i].Y * cs);
        return o;
    }

    private static Vector2[] Star(Vector2 c, int points, float outer, float inner)
    {
        var pts = new Vector2[points * 2];
        for (int i = 0; i < points * 2; i++)
        {
            float ang = -Mathf.Pi / 2 + i * Mathf.Pi / points;
            float rad = i % 2 == 0 ? outer : inner;
            pts[i] = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rad;
        }
        return pts;
    }
}

/// <summary>Turns CombatManager's placeholder marker line (BuildIntentMarkers)
/// into sentences for the intent tooltip. Token meanings follow that file's
/// LogMarkerLegend. Unknown tokens pass through as written, so a new marker is
/// never silently dropped.</summary>
public static class IntentMarkerText
{
    private static readonly (Regex re, System.Func<Match, string> text)[] Rules =
    {
        (new Regex(@"^\*ELITE\*$"),            m => "Elite."),
        (new Regex(@"^IMM$"),                  m => "Cannot move."),
        (new Regex(@"^PLANT$"),                m => "Holds its ground this turn."),
        (new Regex(@"^MOV(\d+)$"),             m => $"Can cover {m.Groups[1].Value} tiles."),
        (new Regex(@"^AP(\d+)$"),              m => $"{m.Groups[1].Value} action points."),
        (new Regex(@"^CYC-/(\d+)$"),           m => $"Its {m.Groups[1].Value}-step opening script is spent."),
        (new Regex(@"^CYC(\d+)/(\d+)>(\w+)$"), m => $"Script step {m.Groups[1].Value} of {m.Groups[2].Value}. Next: {Beat(m.Groups[3].Value)}."),
        (new Regex(@"^CHG\+(\d+)$"),           m => $"Charge: +{m.Groups[1].Value} damage when it moves before striking."),
        (new Regex(@"^PACK\+(\d+)$"),          m => $"Pack: +{m.Groups[1].Value} damage, a packmate is beside it now."),
        (new Regex(@"^PACK$"),                 m => "Pack: hits harder beside a packmate."),
        (new Regex(@"^FLANK$"),                m => "Breaks off when crowded."),
        (new Regex(@"^SKIRM$"),                m => "Shoots, then falls back to cover."),
        (new Regex(@"^PIN$"),                  m => "Ends beside a caster. Leaving it costs a strike."),
        (new Regex(@"^BLWK$"),                 m => "Plants itself to protect wounded allies."),
        (new Regex(@"^HUNTS-WEAK$"),           m => "Targets the lowest current HP."),
        (new Regex(@"^CHITIN-(\d+)$"),         m => $"Every hit against it is reduced by {m.Groups[1].Value}."),
        (new Regex(@"^VEIL$"),                 m => "Immune to damage from beyond 1 tile."),
        (new Regex(@"^SHIFTED$"),              m => "Already transformed."),
        (new Regex(@"^SHIFT@(\d+)$"),          m => $"Transforms after {m.Groups[1].Value} more damage."),
        (new Regex(@"^GUARDED$"),              m => "An ally is taking its hits."),
        (new Regex(@"^GUARDS-r(\d+)$"),        m => $"Takes hits for allies within {m.Groups[1].Value}."),
        (new Regex(@"^RITUAL\+(\d+)\((\d+)/(\d+)\)$"), m => $"Ritual: allies gain +{m.Groups[1].Value} damage each time ({m.Groups[2].Value} of {m.Groups[3].Value})."),
        (new Regex(@"^SUMMON(\d+)@(\d+)$"),    m => $"Summons {m.Groups[1].Value} in {m.Groups[2].Value} rounds."),
        (new Regex(@"^REPAIR@(\d+)$"),         m => $"Armors an ally in {m.Groups[1].Value} rounds."),
        (new Regex(@"^THORNS(\d+)$"),          m => $"Hits back for {m.Groups[1].Value} when struck."),
        (new Regex(@"^REGROW>(\d+)/rnd$"),     m => $"Heals fully unless it takes {m.Groups[1].Value} damage this round."),
        (new Regex(@"^TITHE\+(\d+)$"),         m => $"Your spells cost {m.Groups[1].Value} more mana."),
        (new Regex(@"^REDACT(\d+)\(exile\)$"), m => $"Its attacks burn {m.Groups[1].Value} cards from your hand."),
        (new Regex(@"^HANDCAP-(\d+)$"),        m => $"You hold {m.Groups[1].Value} fewer cards. Overflow is discarded at end of turn."),
        (new Regex(@"^TAX-(\d+)AP/r(\d+)$"),   m => $"Your units within {m.Groups[2].Value} start with {m.Groups[1].Value} fewer AP."),
        (new Regex(@"^GEAS(\d+)/move$"),       m => $"You take {m.Groups[1].Value} damage every time you move."),
        (new Regex(@"^GRUDGE:(\w+)\+(\d+)(\(x(\d+)\))?$"), m => $"Gains {m.Groups[2].Value} damage per {m.Groups[1].Value} spell you cast" + (m.Groups[4].Success ? $" ({m.Groups[4].Value} so far)." : ".")),
        (new Regex(@"^OVERDRAW@(\d+)cards$"),  m => $"Acts twice if you play {m.Groups[1].Value} cards in a turn."),
        (new Regex(@"^OVERDRAWN>ACTS-TWICE$"), m => "Acts twice this round."),
        (new Regex(@"^DRAGGED@(\d+)$"),        m => $"Its channel is held {m.Groups[1].Value} more activations."),
        (new Regex(@"^SPELL:(.+)$"),           m => $"Channel releases {m.Groups[1].Value}."),
        (new Regex(@"^ONDEATH:SPAWN(\d+)$"),   m => $"Spawns {m.Groups[1].Value} when it dies."),
        (new Regex(@"^REQ\+(\d+)(\(x(\d+)\))?$"), m => $"Requiem: +{m.Groups[1].Value} damage per ally death" + (m.Groups[3].Success ? $" ({m.Groups[3].Value} stacks)." : ".")),
    };

    public static List<string> Translate(string markers)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(markers))
            return lines;
        foreach (var raw in markers.Split(new[] { ' ', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string text = null;
            foreach (var (re, fn) in Rules)
            {
                var m = re.Match(raw);
                if (m.Success) { text = fn(m); break; }
            }
            lines.Add(text ?? raw);
        }
        return lines;
    }

    private static string Beat(string token) => token switch
    {
        "GRD" => "brace",
        "ADV" => "advance on the nearest",
        "BIG" => "go for the highest HP",
        "WEK" => "go for the lowest HP",
        "HLD" => "hold",
        "KIT" => "kite",
        "CHN" => "channel",
        _     => "unknown",
    };
}
