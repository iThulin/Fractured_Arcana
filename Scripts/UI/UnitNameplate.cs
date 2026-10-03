using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// UnitNameplate.cs
//
// Purpose:        Screen-space nameplate drawn over one unit:
//                 intent, name, HP bar with shield / armor / cover
//                 badges, mana, AP and Edge pips, and condition
//                 chips with hover tooltips. HealthBarRoot owns one
//                 and moves it over its unit every frame.
//
//                 Everything is drawn in _Draw with the project
//                 font (letters and digits only) plus vector shapes,
//                 so nothing depends on OS fallback glyphs, and the
//                 plate is pixel-crisp at every camera distance.
//
//                 Input: MouseFilter is Ignore. A Pass or Stop filter
//                 would make the viewport mark clicks on the plate
//                 as handled, and CombatManager reads clicks in
//                 _UnhandledInput, so a plate over a unit's head
//                 would eat the click. Tooltips come from polling
//                 the mouse instead.
//
// Layer:          UI
// Collaborators:  HealthBarRoot.cs (owner), StatusCatalog.cs,
//                 UITheme.cs
// ============================================================

/// <summary>Everything a plate shows. Built by HealthBarRoot from the live
/// unit; compared by Signature so the plate only re-lays-out on change.</summary>
public sealed class NameplateData
{
    public string Name = "";
    public Color NameColor = Colors.White;
    /// <summary>0 = your side, 1 = enemy, 2 = neutral (map objects).</summary>
    public int Side = 0;
    public bool Detailed = false;

    public int Hp, MaxHp, Withered;
    public int Shield, Armor, BraceArmor, Cover;
    public int Mana, MaxMana;
    public int Ap, MaxAp;
    public int Edge, MaxEdge;

    public List<UnitCondition> Conditions = new();

    // Enemy intent, read structurally from Unit.CurrentIntent. Shown only while
    // the unit's own intent label is visible (CombatManager's reveal policy).
    public bool HasIntent;
    public IntentKind IntentKind = IntentKind.Unknown;
    public bool IntentRevealed;
    public int IntentValue;
    public int IntentShoveTiles;
    public string IntentElement = "";
    public bool Staggered;
    public int Poise, MaxPoise, Openings, Postponed;
    /// <summary>CombatManager's ASCII marker line; translated into the intent tooltip.</summary>
    public string IntentMarkers = "";
    /// <summary>Chronomancer Defer / Advance state of the attack.</summary>
    public IntentTiming IntentTiming = IntentTiming.Normal;
    /// <summary>The element reaction the intent would form (imbue intents).</summary>
    public ElementReaction IntentReaction = ElementReaction.None;
    /// <summary>Revealed lookahead: the next beat's kind word ("" when hidden) and note.</summary>
    public string IntentNext = "";
    public string IntentNextNote = "";
    public string CoverTag = "";
    public Color CoverTagColor = Colors.White;

    public string Signature()
    {
        var sb = new StringBuilder(128);
        sb.Append(Name).Append('|').Append(NameColor.ToHtml()).Append('|').Append(Side).Append(Detailed)
          .Append('|').Append(Hp).Append(',').Append(MaxHp).Append(',').Append(Withered)
          .Append('|').Append(Shield).Append(',').Append(Armor).Append(',').Append(BraceArmor).Append(',').Append(Cover)
          .Append('|').Append(Mana).Append(',').Append(MaxMana)
          .Append('|').Append(Ap).Append(',').Append(MaxAp).Append(',').Append(Edge).Append(',').Append(MaxEdge)
          .Append('|').Append(HasIntent).Append(IntentKind).Append(IntentRevealed).Append(IntentValue).Append(',').Append(IntentShoveTiles).Append(IntentElement)
          .Append(Staggered).Append(Poise).Append('/').Append(MaxPoise).Append(',').Append(Openings).Append(',').Append(Postponed).Append(IntentMarkers)
          .Append(',').Append(IntentTiming).Append(',').Append(IntentReaction)
          .Append(',').Append(IntentNext).Append(IntentNextNote)
          .Append('|').Append(CoverTag).Append(CoverTagColor.ToHtml()).Append('|');
        foreach (var c in Conditions)
            sb.Append(c.Key).Append(':').Append(c.Count).Append(':').Append(c.Description).Append(';');
        return sb.ToString();
    }
}

public partial class UnitNameplate : Control
{
    // ── Metrics (design pixels at 1920x1080) ─────────────────────────
    private const int BarWidth        = 120;
    private const int BarHeightSmall  = 8;
    private const int BarHeightLarge  = 15;
    private const int BadgeSize       = 20;
    private const int BadgeGap        = 2;
    private const int RowGap          = 3;
    private const int ChipHeight      = 15;
    private const int ChipPadX        = 4;
    private const int ChipGap         = 3;
    private const int ChipFontSmall   = 11;
    private const int ChipFontLarge   = 12;
    private const int ChipRowsCompact = 2;
    private const int NameFontSize    = 15;
    private const int HpFontSize      = 12;
    private const int BadgeFontSize   = 13;
    private const int IntentHeight    = 24;
    private const int IntentValueFont = 17;
    private const int TagFontSize     = 11;
    private const int ManaHeight      = 4;
    private const int PipRowHeight    = 10;
    private const int TooltipWidth    = 230;
    private const int TooltipFont     = 13;
    private const float TooltipDwell  = 0.18f;
    /// <summary>CanvasItem z ceiling (RenderingServer.CANVAS_ITEM_Z_MAX).</summary>
    private const int TooltipZ        = 4096;

    // ── State ────────────────────────────────────────────────────────
    private NameplateData _data = new();
    private string _signature = "";
    private int _previewLoss = 0;
    private bool _previewWarn = false;

    private Font _font;
    private Font _bold;

    // Layout, computed in Relayout, consumed by _Draw.
    private float _intentTop, _nameTop, _barTop, _manaTop, _pipTop, _chipTop;
    private Rect2 _barRect;
    private Rect2 _intentRect;
    private readonly List<(Rect2 rect, int kind, int value)> _badges = new();   // kind 0 shield, 1 armor, 2 cover
    private Rect2 _tagRect;
    private readonly List<(Rect2 rect, UnitCondition cond, string text)> _chips = new();
    private int _hiddenChips = 0;
    private Rect2 _moreChipRect;
    private readonly List<(Rect2 rect, string title, string body, Color accent)> _hits = new();

    // Tooltip
    private int _hoverIndex = -1;
    private float _hoverTime = 0f;
    private bool _tooltipShown = false;

    /// <summary>Base z order set by the owner (nearer units draw on top).</summary>
    public int BaseZ { get; set; } = 0;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        _font = GetThemeDefaultFont() ?? ThemeDB.FallbackFont;
        _bold = GD.Load<Font>("res://Assets/Fonts/Carlito-Bold.ttf") ?? _font;
        Relayout();
    }

    // ── Public API ───────────────────────────────────────────────────

    public void SetData(NameplateData data)
    {
        if (data == null)
            return;
        string sig = data.Signature();
        if (sig == _signature)
            return;
        _signature = sig;
        _data = data;
        Relayout();
        QueueRedraw();
    }

    public void SetDamagePreview(int hpLoss, bool warn)
    {
        _previewLoss = Math.Max(0, hpLoss);
        _previewWarn = warn;
        QueueRedraw();
    }

    public void ClearDamagePreview()
    {
        if (_previewLoss == 0)
            return;
        _previewLoss = 0;
        QueueRedraw();
    }

    /// <summary>Hangs the plate's bottom center from <paramref name="anchor"/>,
    /// kept inside <paramref name="screen"/>. When the camera is close enough that
    /// the anchor above the unit's head leaves the top of the view, the plate pins
    /// to the top edge instead of vanishing.</summary>
    public void PlaceAt(Vector2 anchor, Rect2 screen)
    {
        const float margin = 6f;
        float x = anchor.X - Size.X * 0.5f;
        float y = anchor.Y - Size.Y;
        x = Mathf.Clamp(x, screen.Position.X + margin, Mathf.Max(screen.Position.X + margin, screen.End.X - Size.X - margin));
        y = Mathf.Clamp(y, screen.Position.Y + margin, Mathf.Max(screen.Position.Y + margin, screen.End.Y - Size.Y - margin));
        Position = new Vector2(Mathf.Round(x), Mathf.Round(y));
    }

    // ── Frame ────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (!Visible)
        {
            _hoverIndex = -1;
            _tooltipShown = false;
            return;
        }

        if (_previewLoss > 0)
            QueueRedraw();   // flashing segment

        // Tooltip hover by polling. Hit rects are in local space.
        Vector2 m = GetLocalMousePosition();
        int idx = -1;
        // A HUD panel under the mouse wins: the plate layer draws beneath it.
        bool overHud = GetViewport()?.GuiGetHoveredControl() != null;
        for (int i = 0; !overHud && i < _hits.Count; i++)
        {
            if (_hits[i].rect.Grow(1f).HasPoint(m))
            {
                idx = i;
                break;
            }
        }

        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            _hoverTime = 0f;
            if (_tooltipShown)
            {
                _tooltipShown = false;
                QueueRedraw();
            }
        }
        else if (idx >= 0 && !_tooltipShown)
        {
            _hoverTime += (float)delta;
            if (_hoverTime >= TooltipDwell)
            {
                _tooltipShown = true;
                QueueRedraw();
            }
        }

        ZIndex = _tooltipShown ? TooltipZ : BaseZ;
    }

    // ── Layout ───────────────────────────────────────────────────────

    private void Relayout()
    {
        if (_font == null)
            return;

        var d = _data;
        _badges.Clear();
        _chips.Clear();
        _hits.Clear();
        _hiddenChips = 0;

        // Bar row spans: badges hang left of the bar, the cover tag right.
        int badgeCount = (d.Shield > 0 ? 1 : 0) + (d.Armor > 0 ? 1 : 0) + (d.Cover > 0 ? 1 : 0);
        float leftSpan = badgeCount > 0 ? badgeCount * (BadgeSize + BadgeGap) + 1 : 0;
        float tagW = 0;
        if (!string.IsNullOrEmpty(d.CoverTag))
            tagW = _bold.GetStringSize(d.CoverTag, HorizontalAlignment.Left, -1, TagFontSize).X + 8;
        float rightSpan = tagW > 0 ? tagW + 3 : 0;
        float side = Mathf.Max(leftSpan, rightSpan);
        float width = BarWidth + side * 2;

        // Intent pill (UnitNameplate.Intent.cs).
        float intentW = 0, intentH = 0;
        if (d.HasIntent)
        {
            intentW = MeasureIntent();
            intentH = IntentHeight;
            width = Mathf.Max(width, intentW);
        }

        float nameW = 0;
        bool showName = d.Detailed && !string.IsNullOrEmpty(d.Name);
        if (showName)
        {
            nameW = _bold.GetStringSize(d.Name, HorizontalAlignment.Left, -1, NameFontSize).X + 4;
            width = Mathf.Max(width, nameW);
        }

        // Chips wrap inside this width.
        int chipFont = d.Detailed ? ChipFontLarge : ChipFontSmall;
        float wrapW = Mathf.Max(width, d.Detailed ? 200 : 150);
        var rows = new List<List<(float w, UnitCondition c, string text)>>();
        var cur = new List<(float w, UnitCondition c, string text)>();
        float curW = 0;
        foreach (var c in d.Conditions)
        {
            string text = c.ChipText(d.Detailed);
            float w = _bold.GetStringSize(text, HorizontalAlignment.Left, -1, chipFont).X + ChipPadX * 2;
            float need = cur.Count == 0 ? w : curW + ChipGap + w;
            if (cur.Count > 0 && need > wrapW)
            {
                rows.Add(cur);
                cur = new List<(float, UnitCondition, string)>();
                curW = 0;
                need = w;
            }
            cur.Add((w, c, text));
            curW = need;
        }
        if (cur.Count > 0)
            rows.Add(cur);

        if (!d.Detailed && rows.Count > ChipRowsCompact)
        {
            for (int r = ChipRowsCompact; r < rows.Count; r++)
                _hiddenChips += rows[r].Count;
            rows.RemoveRange(ChipRowsCompact, rows.Count - ChipRowsCompact);
        }

        float chipsW = 0;
        foreach (var row in rows)
        {
            float rw = 0;
            foreach (var ch in row)
                rw += (rw > 0 ? ChipGap : 0) + ch.w;
            chipsW = Mathf.Max(chipsW, rw);
        }
        float moreW = _hiddenChips > 0
            ? _bold.GetStringSize($"+{_hiddenChips}", HorizontalAlignment.Left, -1, chipFont).X + ChipPadX * 2 + ChipGap
            : 0;
        width = Mathf.Max(width, chipsW + moreW);
        width = Mathf.Ceil(width);

        // Vertical stack.
        float y = 0;
        _intentTop = y;
        if (intentH > 0)
        {
            _intentRect = new Rect2((width - intentW) * 0.5f, y, intentW, intentH);
            _hits.Add((_intentRect, IntentTitle(), IntentTooltip(), IntentKindColor()));
            y += intentH + RowGap;
        }

        _nameTop = y;
        if (showName)
            y += NameFontSize + 2;

        int barH = d.Detailed ? BarHeightLarge : BarHeightSmall;
        float rowH = Mathf.Max(barH, badgeCount > 0 ? BadgeSize : 0);
        rowH = Mathf.Max(rowH, tagW > 0 ? TagFontSize + 5 : 0);
        _barTop = y;
        float barX = (width - BarWidth) * 0.5f;
        _barRect = new Rect2(barX, y + (rowH - barH) * 0.5f, BarWidth, barH);
        _hits.Add((_barRect, "Health", HealthTooltip(), FillFor(d.Side)));

        // Badges, right to left from the bar: shield nearest the bar (it goes first).
        float bx = barX - BadgeGap - BadgeSize;
        float by = y + (rowH - BadgeSize) * 0.5f;
        if (d.Shield > 0) { AddBadge(new Rect2(bx, by, BadgeSize, BadgeSize), 0, d.Shield); bx -= BadgeSize + BadgeGap; }
        if (d.Armor > 0)  { AddBadge(new Rect2(bx, by, BadgeSize, BadgeSize), 1, d.Armor);  bx -= BadgeSize + BadgeGap; }
        if (d.Cover > 0)  { AddBadge(new Rect2(bx, by, BadgeSize, BadgeSize), 2, d.Cover); }

        if (tagW > 0)
        {
            _tagRect = new Rect2(barX + BarWidth + 3, y + (rowH - (TagFontSize + 5)) * 0.5f, tagW, TagFontSize + 5);
            _hits.Add((_tagRect, d.CoverTag, CoverTagTooltip(d.CoverTag), d.CoverTagColor));
        }
        y += rowH;

        _manaTop = y;
        if (d.Detailed && d.MaxMana > 0)
        {
            y += 2;
            _manaTop = y;
            var manaRect = new Rect2(barX, y, BarWidth, ManaHeight);
            _hits.Add((manaRect, $"Mana {d.Mana}/{d.MaxMana}", "Spent to cast spells. Refills at the start of the turn.", UITheme.StatBarMana));
            y += ManaHeight;
        }

        _pipTop = y;
        if (d.Detailed && (d.MaxAp > 0 || d.MaxEdge > 0))
        {
            y += RowGap;
            _pipTop = y;
            var (apRect, edgeRect) = PipRects(width);
            if (d.MaxAp > 0)
                _hits.Add((apRect, $"Action Points {d.Ap}/{d.MaxAp}", "Spent to move, attack and use techniques. Refill at the start of the turn.", UITheme.PlateAp));
            if (d.MaxEdge > 0)
                _hits.Add((edgeRect, $"Edge {d.Edge}/{d.MaxEdge}", "Martial resource. Earned by the active stance, spent on maneuvers.", UITheme.PlateEdge));
            y += PipRowHeight;
        }

        _chipTop = y;
        if (rows.Count > 0)
        {
            y += RowGap;
            _chipTop = y;
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                float rw = 0;
                foreach (var ch in row)
                    rw += (rw > 0 ? ChipGap : 0) + ch.w;
                bool lastRow = r == rows.Count - 1;
                if (lastRow) rw += moreW;
                float cx = (width - rw) * 0.5f;
                foreach (var ch in row)
                {
                    var rect = new Rect2(cx, y, ch.w, ChipHeight);
                    _chips.Add((rect, ch.c, ch.text));
                    _hits.Add((rect, ch.c.TooltipTitle(), ch.c.Description, StatusCatalog.ToneColor(ch.c.Tone)));
                    cx += ch.w + ChipGap;
                }
                if (lastRow && _hiddenChips > 0)
                {
                    _moreChipRect = new Rect2(cx, y, moreW - ChipGap, ChipHeight);
                    _hits.Add((_moreChipRect, $"{_hiddenChips} more", "Hover or select the unit to see every condition.", UITheme.TextSecondary));
                }
                y += ChipHeight + (lastRow ? 0 : 2);
            }
        }

        Size = new Vector2(width, Mathf.Ceil(y));
        _hoverIndex = -1;
        _tooltipShown = false;
    }

    private void AddBadge(Rect2 rect, int kind, int value)
    {
        _badges.Add((rect, kind, value));
        var d = _data;
        switch (kind)
        {
            case 0:
                _hits.Add((rect, $"Shield {value}",
                    "Absorbs damage first, before armor and HP. Your units' shield clears when your turn ends.",
                    UITheme.PlateShield));
                break;
            case 1:
                string brace = d.BraceArmor > 0
                    ? $" Includes {Math.Min(d.BraceArmor, value)} from Brace, which comes down at this unit's next turn."
                    : "";
                _hits.Add((rect, $"Armor {value}",
                    "Absorbs damage after shield and before HP. It is worn away by what it stops." + brace,
                    UITheme.PlateArmor));
                break;
            default:
                _hits.Add((rect, $"Cover {value}",
                    "Stops shots arriving from a covered direction. Bursts, arcs and flanking shots ignore it. Refills each turn.",
                    UITheme.PlateCover));
                break;
        }
    }

    private (Rect2 ap, Rect2 edge) PipRects(float width)
    {
        var d = _data;
        float apW = d.MaxAp > 0 ? d.MaxAp * 10 - 3 : 0;
        float edgeW = d.MaxEdge > 0 ? d.MaxEdge * 10 - 2 : 0;
        float gap = apW > 0 && edgeW > 0 ? 12 : 0;
        float total = apW + gap + edgeW;
        float x = (width - total) * 0.5f;
        var ap = new Rect2(x, _pipTop, apW, PipRowHeight);
        var edge = new Rect2(x + apW + gap, _pipTop, edgeW, PipRowHeight);
        return (ap, edge);
    }

    // ── Draw ─────────────────────────────────────────────────────────

    public override void _Draw()
    {
        if (_font == null)
            return;
        var d = _data;

        DrawIntent();

        if (d.Detailed && !string.IsNullOrEmpty(d.Name))
        {
            var pos = new Vector2(0, _nameTop + _bold.GetAscent(NameFontSize));
            DrawStringOutline(_bold, pos, d.Name, HorizontalAlignment.Center, Size.X, NameFontSize, 4, new Color(0, 0, 0, 0.85f));
            DrawString(_bold, pos, d.Name, HorizontalAlignment.Center, Size.X, NameFontSize, d.NameColor);
        }

        DrawHealthBar();

        foreach (var b in _badges)
            DrawBadge(b.rect, b.kind, b.value);

        if (!string.IsNullOrEmpty(d.CoverTag))
            DrawPill(_tagRect, d.CoverTag, TagFontSize, d.CoverTagColor, new Color(0, 0, 0, 0.7f), d.CoverTagColor);

        if (d.Detailed && d.MaxMana > 0)
        {
            var back = new Rect2(_barRect.Position.X, _manaTop, BarWidth, ManaHeight);
            DrawRect(back, UITheme.PlateBarBack);
            float pct = Mathf.Clamp((float)d.Mana / d.MaxMana, 0f, 1f);
            if (pct > 0)
                DrawRect(new Rect2(back.Position, new Vector2(BarWidth * pct, ManaHeight)), UITheme.StatBarMana);
        }

        if (d.Detailed && (d.MaxAp > 0 || d.MaxEdge > 0))
            DrawPips();

        int chipFont = d.Detailed ? ChipFontLarge : ChipFontSmall;
        foreach (var ch in _chips)
        {
            Color tone = StatusCatalog.ToneColor(ch.cond.Tone);
            DrawPill(ch.rect, ch.text, chipFont, UITheme.TextPrimary, tone.Darkened(0.62f) with { A = 0.92f }, tone);
        }
        if (_hiddenChips > 0)
            DrawPill(_moreChipRect, $"+{_hiddenChips}", chipFont, UITheme.TextSecondary, new Color(0, 0, 0, 0.75f), UITheme.TextDim);

        if (_tooltipShown && _hoverIndex >= 0 && _hoverIndex < _hits.Count)
            DrawTooltip(_hits[_hoverIndex]);
    }

    private void DrawHealthBar()
    {
        var d = _data;
        var r = _barRect;
        Color fill = FillFor(d.Side);

        // Shield rim: a cyan frame reads as "protected" before the badge is read.
        if (d.Shield > 0)
            DrawRect(r.Grow(2f), UITheme.PlateShield, false, 2f);

        DrawRect(r.Grow(1f), new Color(0, 0, 0, 0.9f));
        DrawRect(r, UITheme.PlateBarBack);

        int total = Math.Max(1, d.MaxHp + Math.Max(0, d.Withered));
        float hpPct = Mathf.Clamp((float)d.Hp / total, 0f, 1f);
        float hpW = r.Size.X * hpPct;
        if (hpW > 0)
        {
            DrawRect(new Rect2(r.Position, new Vector2(hpW, r.Size.Y)), fill);
            // Top sheen and bottom shade give the fill some body.
            DrawRect(new Rect2(r.Position, new Vector2(hpW, Mathf.Max(1f, r.Size.Y * 0.3f))), new Color(1, 1, 1, 0.22f));
            DrawRect(new Rect2(r.Position.X, r.End.Y - Mathf.Max(1f, r.Size.Y * 0.25f), hpW, Mathf.Max(1f, r.Size.Y * 0.25f)), new Color(0, 0, 0, 0.22f));
        }

        if (d.Withered > 0)
        {
            float ww = r.Size.X * Mathf.Clamp((float)d.Withered / total, 0f, 1f);
            DrawRect(new Rect2(r.End.X - ww, r.Position.Y, ww, r.Size.Y), UITheme.WitherFill);
        }

        // Damage preview: flash the span that the predicted hit removes.
        if (_previewLoss > 0 && d.Hp > 0)
        {
            float lossW = r.Size.X * Mathf.Clamp((float)Math.Min(_previewLoss, d.Hp) / total, 0f, 1f);
            float t = (float)Time.GetTicksMsec() / 1000f;
            float a = 0.35f + 0.55f * (0.5f + 0.5f * Mathf.Sin(t * Mathf.Tau * 1.8f));
            Color c = _previewWarn ? UITheme.Warning : UITheme.PlatePreview;
            DrawRect(new Rect2(r.Position.X + hpW - lossW, r.Position.Y, lossW, r.Size.Y), c with { A = a });
        }

        // Tick every 5 HP when that stays readable.
        if (total >= 10 && total <= 80)
        {
            for (int hp = 5; hp < total; hp += 5)
            {
                float x = Mathf.Round(r.Position.X + r.Size.X * hp / total);
                DrawLine(new Vector2(x, r.Position.Y + 1), new Vector2(x, r.End.Y - 1), new Color(0, 0, 0, 0.35f), 1f);
            }
        }

        if (d.Detailed)
        {
            string text = d.Withered > 0 ? $"{d.Hp}/{d.MaxHp} (−{d.Withered})" : $"{d.Hp}/{d.MaxHp}";
            float asc = _bold.GetAscent(HpFontSize);
            float desc = _bold.GetDescent(HpFontSize);
            var pos = new Vector2(r.Position.X, r.Position.Y + (r.Size.Y + asc - desc) * 0.5f);
            DrawStringOutline(_bold, pos, text, HorizontalAlignment.Center, r.Size.X, HpFontSize, 3, new Color(0, 0, 0, 0.9f));
            DrawString(_bold, pos, text, HorizontalAlignment.Center, r.Size.X, HpFontSize, Colors.White);
        }
    }

    private void DrawBadge(Rect2 r, int kind, int value)
    {
        Vector2 o = r.Position;
        float s = r.Size.X / 20f;
        Vector2[] pts;
        Color edge, body;
        switch (kind)
        {
            case 0: // shield: kite shape
                pts = new[] { new Vector2(2, 1.5f), new Vector2(18, 1.5f), new Vector2(18, 9), new Vector2(14.5f, 15), new Vector2(10, 19), new Vector2(5.5f, 15), new Vector2(2, 9) };
                edge = UITheme.PlateShield;
                body = UITheme.PlateShield.Darkened(0.55f);
                break;
            case 1: // armor: hexagonal plate
                pts = new[] { new Vector2(5, 1.5f), new Vector2(15, 1.5f), new Vector2(19, 10), new Vector2(15, 18.5f), new Vector2(5, 18.5f), new Vector2(1, 10) };
                edge = UITheme.PlateArmor;
                body = UITheme.PlateArmor.Darkened(0.62f);
                break;
            default: // cover: crenellated wall
                pts = new[] { new Vector2(1.5f, 18.5f), new Vector2(1.5f, 4), new Vector2(6, 4), new Vector2(6, 7), new Vector2(8, 7), new Vector2(8, 4), new Vector2(12, 4), new Vector2(12, 7), new Vector2(14, 7), new Vector2(14, 4), new Vector2(18.5f, 4), new Vector2(18.5f, 18.5f) };
                edge = UITheme.PlateCover;
                body = UITheme.PlateCover.Darkened(0.6f);
                break;
        }

        var poly = new Vector2[pts.Length];
        var shadow = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            poly[i] = o + pts[i] * s;
            shadow[i] = poly[i] + new Vector2(1, 1);
        }
        DrawColoredPolygon(shadow, new Color(0, 0, 0, 0.6f));
        DrawColoredPolygon(poly, body);
        var outline = new Vector2[poly.Length + 1];
        Array.Copy(poly, outline, poly.Length);
        outline[poly.Length] = poly[0];
        DrawPolyline(outline, edge, 1.5f, true);

        string text = value > 99 ? "99+" : value.ToString();
        int fs = text.Length >= 3 ? BadgeFontSize - 3 : BadgeFontSize;
        float asc = _bold.GetAscent(fs);
        float desc = _bold.GetDescent(fs);
        float yBias = kind == 0 ? -1.5f : (kind == 2 ? 1.5f : 0f);
        var pos = new Vector2(r.Position.X, r.Position.Y + (r.Size.Y + asc - desc) * 0.5f + yBias);
        DrawStringOutline(_bold, pos, text, HorizontalAlignment.Center, r.Size.X, fs, 3, new Color(0, 0, 0, 0.9f));
        DrawString(_bold, pos, text, HorizontalAlignment.Center, r.Size.X, fs, Colors.White);
    }

    private void DrawPips()
    {
        var d = _data;
        var (apRect, edgeRect) = PipRects(Size.X);
        float cy = _pipTop + PipRowHeight * 0.5f;

        for (int i = 0; i < d.MaxAp; i++)
        {
            var c = new Vector2(apRect.Position.X + 3.5f + i * 10, cy);
            DrawCircle(c + new Vector2(1, 1), 4f, new Color(0, 0, 0, 0.6f));
            if (i < d.Ap)
                DrawCircle(c, 3.5f, UITheme.PlateAp);
            else
            {
                DrawCircle(c, 3.5f, new Color(0, 0, 0, 0.55f));
                DrawArc(c, 3.2f, 0, Mathf.Tau, 16, UITheme.PlateAp.Darkened(0.25f), 1.2f, true);
            }
        }

        for (int i = 0; i < d.MaxEdge; i++)
        {
            var c = new Vector2(edgeRect.Position.X + 4f + i * 10, cy);
            var dia = new[] { c + new Vector2(0, -4.5f), c + new Vector2(4, 0), c + new Vector2(0, 4.5f), c + new Vector2(-4, 0) };
            var sh = new[] { dia[0] + Vector2.One, dia[1] + Vector2.One, dia[2] + Vector2.One, dia[3] + Vector2.One };
            DrawColoredPolygon(sh, new Color(0, 0, 0, 0.6f));
            if (i < d.Edge)
                DrawColoredPolygon(dia, UITheme.PlateEdge);
            else
            {
                DrawColoredPolygon(dia, new Color(0, 0, 0, 0.55f));
                DrawPolyline(new[] { dia[0], dia[1], dia[2], dia[3], dia[0] }, UITheme.PlateEdge.Darkened(0.3f), 1.2f, true);
            }
        }
    }

    // ── Pills and tooltip ────────────────────────────────────────────

    private void DrawPillBox(Rect2 r, Color bg, Color border)
    {
        var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border, AntiAliasing = true };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll((int)Mathf.Min(6, r.Size.Y * 0.5f));
        DrawStyleBox(sb, r);
    }

    private void DrawPill(Rect2 r, string text, int fontSize, Color textColor, Color bg, Color border)
    {
        DrawPillBox(r, bg, border);
        float asc = _bold.GetAscent(fontSize);
        float desc = _bold.GetDescent(fontSize);
        var pos = new Vector2(r.Position.X, r.Position.Y + (r.Size.Y + asc - desc) * 0.5f);
        DrawString(_bold, pos, text, HorizontalAlignment.Center, r.Size.X, fontSize, textColor);
    }

    private void DrawTooltip((Rect2 rect, string title, string body, Color accent) hit)
    {
        float innerW = TooltipWidth - 16;
        Vector2 titleSize = _bold.GetStringSize(hit.title, HorizontalAlignment.Left, innerW, TooltipFont + 1);
        Vector2 bodySize = string.IsNullOrEmpty(hit.body) ? Vector2.Zero
            : _font.GetMultilineStringSize(hit.body, HorizontalAlignment.Left, innerW, TooltipFont);
        float h = 8 + titleSize.Y + (bodySize.Y > 0 ? 3 + bodySize.Y : 0) + 8;

        // Above the plate, clamped to the screen.
        var vp = GetViewportRect().Size;
        var global = GetGlobalTransform().Origin;
        float x = hit.rect.GetCenter().X - TooltipWidth * 0.5f;
        float y = -h - 6;
        x = Mathf.Clamp(x, -global.X + 4, vp.X - global.X - TooltipWidth - 4);
        if (global.Y + y < 4)
            y = Size.Y + 6;   // no room above: drop below the plate
        var box = new Rect2(Mathf.Round(x), Mathf.Round(y), TooltipWidth, Mathf.Ceil(h));

        var sb = new StyleBoxFlat { BgColor = UITheme.BgDeep with { A = 0.96f }, BorderColor = hit.accent, AntiAliasing = true };
        sb.SetBorderWidthAll(1);
        sb.BorderWidthLeft = 3;
        sb.SetCornerRadiusAll(4);
        sb.ShadowColor = new Color(0, 0, 0, 0.5f);
        sb.ShadowSize = 4;
        DrawStyleBox(sb, box);

        var tp = new Vector2(box.Position.X + 9, box.Position.Y + 8 + _bold.GetAscent(TooltipFont + 1));
        DrawString(_bold, tp, hit.title, HorizontalAlignment.Left, innerW, TooltipFont + 1, hit.accent.Lightened(0.15f));
        if (bodySize.Y > 0)
        {
            var bp = new Vector2(box.Position.X + 9, box.Position.Y + 8 + titleSize.Y + 3 + _font.GetAscent(TooltipFont));
            DrawMultilineString(_font, bp, hit.body, HorizontalAlignment.Left, innerW, TooltipFont, -1, UITheme.TextPrimary);
        }
    }

    private static Color FillFor(int side) => side switch
    {
        0 => UITheme.PlateAllyFill,
        1 => UITheme.PlateEnemyFill,
        _ => UITheme.PlateNeutralFill,
    };

    private string HealthTooltip()
    {
        var d = _data;
        var sb = new StringBuilder();
        sb.Append($"{d.Hp} of {d.MaxHp} HP.");
        if (d.Withered > 0)
            sb.Append($" Poison has eaten {d.Withered} max HP (the violet end of the bar).");
        if (d.Shield > 0 || d.Armor > 0)
            sb.Append($" Damage hits shield ({d.Shield}) first, then armor ({d.Armor}), then HP.");
        return sb.ToString();
    }

    private static string CoverTagTooltip(string tag)
    {
        string t = tag.ToUpperInvariant();
        if (t.Contains("FLANK"))
            return "Relative to your selected unit: this target has no cover from that direction.";
        if (t.Contains("COVER"))
            return "Relative to your selected unit: shots from there are stopped by this target's cover.";
        return "Position relative to your selected unit.";
    }
}

/// <summary>One screen-space CanvasLayer per viewport holding every nameplate.
/// Layer 0 keeps plates above the 3D world and under the combat HUD
/// (CombatUI and DeckUI sit on layer 1, overlays higher).</summary>
public static class NameplateLayer
{
    private static readonly Dictionary<ulong, CanvasLayer> Layers = new();

    public static CanvasLayer For(Node owner)
    {
        var vp = owner?.GetViewport();
        if (vp == null)
            return null;
        ulong id = vp.GetInstanceId();
        if (Layers.TryGetValue(id, out var existing)
            && GodotObject.IsInstanceValid(existing) && !existing.IsQueuedForDeletion())
            return existing;

        var layer = new CanvasLayer { Name = "UnitNameplates", Layer = 0 };
        // Deferred: the viewport may be mid-setup when the first unit readies.
        vp.CallDeferred(Node.MethodName.AddChild, layer);
        Layers[id] = layer;
        return layer;
    }
}
