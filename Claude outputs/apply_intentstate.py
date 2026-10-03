import sys, os
root = sys.argv[1]

def edit(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print("ok", rel)

# ── Data: the intent carries its predicted reaction; the plate carries timing + reaction.
edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [
(
'''    public TileElementType ImbueElement = TileElementType.None;
''',
'''    public TileElementType ImbueElement = TileElementType.None;

    /// <summary>The element reaction this intent would form if it resolved on the board
    /// as it stands (imbue intents only). Kept current by UpdateIntentDisplay; the
    /// nameplate draws it as a coloured tag.</summary>
    public ElementReaction PredictedReaction = ElementReaction.None;
'''),
# The Label3D body no longer carries FORMS / DEFERRED / SPENT words: the nameplate
# parses everything after the first line as marker tokens, so the words landed in the
# tooltip as raw tokens. The plate now reads the state structurally instead.
(
'''        var reaction = enemy.AttackTiming == IntentTiming.Spent ? ElementReaction.None : PredictIntentReaction(intent);
        if (reaction != ElementReaction.None)
            body += $"\\nFORMS {ElementReactions.DisplayName(reaction).ToUpperInvariant()}";

        // class_identity_chronomancer_v1 §2a: an attack moved in time says so.
        bool timed = enemy.AttackTiming != IntentTiming.Normal;
        if (enemy.AttackTiming == IntentTiming.Deferred)
            body += "\\nDEFERRED";
        else if (enemy.AttackTiming == IntentTiming.Spent)
            body += "\\nSPENT";

        // The marker line is reference text, not a glyph, so shrink it and two lines
        // don't swallow the board.
        int size = !string.IsNullOrEmpty(markers) ? 24 : (reaction != ElementReaction.None || timed) ? 30 : 40;
''',
'''        // Predicted reaction (elementalist §2a item 7) and timing (chronomancer §2a) are
        // drawn by the nameplate from the intent and the unit, not written into this body.
        intent.PredictedReaction = enemy.AttackTiming == IntentTiming.Spent
            ? ElementReaction.None : PredictIntentReaction(intent);

        // The marker line is reference text, not a glyph, so shrink it and two lines
        // don't swallow the board.
        int size = !string.IsNullOrEmpty(markers) ? 24 : 40;
'''),
])

edit('Scripts/UI/UnitNameplate.cs', [
(
'''    /// <summary>CombatManager's ASCII marker line; translated into the intent tooltip.</summary>
    public string IntentMarkers = "";
''',
'''    /// <summary>CombatManager's ASCII marker line; translated into the intent tooltip.</summary>
    public string IntentMarkers = "";
    /// <summary>Chronomancer Defer / Advance state of the attack.</summary>
    public IntentTiming IntentTiming = IntentTiming.Normal;
    /// <summary>The element reaction the intent would form (imbue intents).</summary>
    public ElementReaction IntentReaction = ElementReaction.None;
'''),
(
'''.Append(Openings).Append(',').Append(Postponed).Append(IntentMarkers)
''',
'''.Append(Openings).Append(',').Append(Postponed).Append(IntentMarkers)
          .Append(',').Append(IntentTiming).Append(',').Append(IntentReaction)
'''),
])

edit('Scripts/UI/HealthBarRoot.cs', [(
'''            d.Postponed = _unit.PostponedTurns;
''',
'''            d.Postponed = _unit.PostponedTurns;
            d.IntentTiming = _unit.AttackTiming;
            d.IntentReaction = intent.PredictedReaction;
''')])

edit('Scripts/UI/UnitNameplate.Intent.cs', [
# Measure the tags.
(
'''        if (d.Postponed > 0) yield return 10f;
    }
''',
'''        if (d.Postponed > 0) yield return 10f;
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
        return tags;
    }

    private float TagWidth(IntentTag tag)
        => (tag.Clock ? TagIcon + 3f : 0f) + _bold.GetStringSize(tag.Text, HorizontalAlignment.Left, -1, TagFont).X;
'''),
# Tooltip lines.
(
'''        if (d.Postponed > 0)
            sb.Append(d.Postponed == 1 ? "\\nIts turn is postponed by 1." : $"\\nIts turn is postponed by {d.Postponed}.");
''',
'''        if (d.Postponed > 0)
            sb.Append(d.Postponed == 1 ? "\\nIts turn is postponed by 1." : $"\\nIts turn is postponed by {d.Postponed}.");
        if (d.IntentTiming == IntentTiming.Deferred)
            sb.Append("\\nDeferred: it still moves this turn, but the blow lands at the end of the round on the marked tile, on whoever stands there then.");
        else if (d.IntentTiming == IntentTiming.Spent)
            sb.Append("\\nSpent: this attack already landed. It moves this turn but does not strike.");
        if (d.IntentReaction != ElementReaction.None)
            sb.Append($"\\nWill form {ElementReactions.DisplayName(d.IntentReaction)} on the marked ground: {ElementReactions.Describe(d.IntentReaction)}");
'''),
# Pill styling: deferred gets a temporal border; spent is drawn faded.
(
'''        var r = _intentRect;
        Color kc = IntentKindColor();
        Color border = d.IntentRevealed ? kc : (kc.Darkened(0.35f) with { A = 0.85f });

        var sb = new StyleBoxFlat { BgColor = UITheme.BgDeep with { A = 0.94f }, BorderColor = border, AntiAliasing = true };
        sb.SetBorderWidthAll(d.IntentRevealed ? 2 : 1);
''',
'''        var r = _intentRect;
        bool spent = d.IntentTiming == IntentTiming.Spent;
        bool deferred = d.IntentTiming == IntentTiming.Deferred;
        Color kc = IntentKindColor();
        if (spent)
            kc = kc.Lerp(UITheme.TextDim, 0.6f);   // the blow is gone; the pill reads as a memory of it
        Color border = deferred ? TemporalColor
            : d.IntentRevealed ? kc : (kc.Darkened(0.35f) with { A = 0.85f });

        var sb = new StyleBoxFlat { BgColor = UITheme.BgDeep with { A = spent ? 0.70f : 0.94f }, BorderColor = border, AntiAliasing = true };
        sb.SetBorderWidthAll(d.IntentRevealed || deferred ? 2 : 1);
'''),
(
'''        Color vc = d.IntentRevealed ? Colors.White : UITheme.TextDim;
        DrawStringOutline(_bold, pos, value, HorizontalAlignment.Left, -1, IntentValueFont, 3, new Color(0, 0, 0, 0.9f));
        DrawString(_bold, pos, value, HorizontalAlignment.Left, -1, IntentValueFont, vc);
        x += _bold.GetStringSize(value, HorizontalAlignment.Left, -1, IntentValueFont).X;
''',
'''        Color vc = spent ? UITheme.TextDim : d.IntentRevealed ? Colors.White : UITheme.TextDim;
        DrawStringOutline(_bold, pos, value, HorizontalAlignment.Left, -1, IntentValueFont, 3, new Color(0, 0, 0, 0.9f));
        DrawString(_bold, pos, value, HorizontalAlignment.Left, -1, IntentValueFont, vc);
        float valueW = _bold.GetStringSize(value, HorizontalAlignment.Left, -1, IntentValueFont).X;
        if (spent)
            DrawLine(new Vector2(x - 1f, cy), new Vector2(x + valueW + 1f, cy), UITheme.TextSecondary, 1.6f, true);   // struck through
        x += valueW;
'''),
# Draw the tags last.
(
'''        if (d.Postponed > 0)
        {
            x += IntentStateGap;
            DrawHourglass(new Vector2(x + 5f, cy));
        }
    }
''',
'''        if (d.Postponed > 0)
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
'''),
])
