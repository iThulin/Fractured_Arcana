using Godot;
using System.Collections.Generic;

// ============================================================
// SpeechBubble.cs
//
// Purpose:        A comic speech bubble over a figure in the 3D world: a
//                 rounded card of light paper outlined in ink, with a
//                 tail to the speaker's head, its words typed in a letter
//                 at a time. Every frame it follows the speaker on screen,
//                 keeps inside the screen and clear of the HUD panels it
//                 is given, and hides while the speaker is behind the
//                 camera. Say shows a line, held for a time or until the
//                 next one. Freeze stops the typing, the hold and the fade
//                 where they are while the bubble goes on following its
//                 speaker, so the line hangs in the air: the Sundering's
//                 Long Second, the Provost's last words an instant before
//                 the discipline. ShowStill puts a line up that way at
//                 once, for the hall seen again.
// Layer:          UI
// Collaborators:  CombatManager.Sundering.cs (the Provost's clauses),
//                 HallStage (her last words, still), UITheme (the paper,
//                 the ink, the size)
// ============================================================

/// <summary>A speech bubble over a 3D speaker (see the file header). It covers the screen and
/// ignores the mouse, so nothing under it loses a click.</summary>
public partial class SpeechBubble : Control
{
    /// <summary>Who is speaking: the tail points <see cref="HeadHeight"/> above this node's origin
    /// (its feet).</summary>
    public Node3D Speaker;
    public float HeadHeight = 2.1f;
    /// <summary>Letters a second as a line types in.</summary>
    public float LettersPerSecond = 38f;
    /// <summary>HUD panels the bubble moves clear of.</summary>
    public readonly List<Control> KeepClearOf = new();

    private const string FontPath = "res://Assets/Fonts/Carlito-Bold.ttf";
    private const float MaxTextWidth = 360f;
    private const float TailLength = 44f;
    private const float TailHalfBase = 12f;
    private const float ScreenMargin = 14f;
    private const float PopSeconds = 0.2f;
    private const float FadeSeconds = 0.45f;
    private const int Ink = 3;        // the outline, in pixels
    private const int Corner = 16;
    private const int PadX = 14;
    private const int PadY = 9;

    private static Font _font;

    private PanelContainer _panel;
    private Label _label;
    private Control _tail;
    private bool _speaking;
    private bool _frozen;
    private float _typed;
    private float _hold;
    private float _held;
    private float _age;
    private float _fade = -1f;
    private Vector2 _tip;

    /// <summary>A line is up (typing, held, fading or frozen).</summary>
    public bool Speaking => _speaking;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;

        var paper = new StyleBoxFlat
        {
            BgColor = UITheme.SurfaceLight,
            BorderColor = UITheme.CardInk,
            ShadowColor = new Color(UITheme.CardInk, 0.3f),
            ShadowSize = 4,
            ShadowOffset = new Vector2(2f, 3f),
            ContentMarginLeft = PadX,
            ContentMarginRight = PadX,
            ContentMarginTop = PadY,
            ContentMarginBottom = PadY + 1,
        };
        paper.SetBorderWidthAll(Ink);
        paper.SetCornerRadiusAll(Corner);
        _panel = new PanelContainer { Name = "Bubble", MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddThemeStyleboxOverride("panel", paper);
        AddChild(_panel);

        _label = new Label
        {
            Name = "Words",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            VisibleCharactersBehavior = TextServer.VisibleCharactersBehavior.CharsAfterShaping,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _label.AddThemeColorOverride("font_color", UITheme.CardInk);
        _label.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
        var font = BubbleFont();
        if (font != null)
            _label.AddThemeFontOverride("font", font);
        _panel.AddChild(_label);

        // The tail draws after the panel, so its paper covers the panel's ink where it leaves it.
        _tail = new Control { Name = "Tail", MouseFilter = MouseFilterEnum.Ignore };
        _tail.SetAnchorsPreset(LayoutPreset.FullRect);
        _tail.Draw += DrawTail;
        AddChild(_tail);
    }

    /// <summary>Says <paramref name="text"/>: the bubble pops up over the speaker and types it in,
    /// holds it <paramref name="hold"/> seconds once it is all there, and fades. At 0 or less it holds
    /// the line until the next one.</summary>
    public void Say(string text, float hold)
    {
        if (_label == null)
            return;
        _label.Text = text ?? "";
        _speaking = _label.Text.Length > 0;
        _frozen = false;
        _typed = 0f;
        _label.VisibleCharacters = 0;
        _hold = hold;
        _held = 0f;
        _age = 0f;
        _fade = -1f;
        SetAlpha(1f);
        Fit();
        if (_speaking)
            Place();
        else
            Visible = false;
    }

    /// <summary>The line stops where it is (typed so far, held, half faded) and stays over the speaker
    /// until the next Say.</summary>
    public void Freeze() => _frozen = true;

    /// <summary>Shows <paramref name="text"/> as Freeze left it: all there, no pop, stopped (the hall
    /// seen again).</summary>
    public void ShowStill(string text)
    {
        Say(text, 0f);
        if (!_speaking)
            return;
        _label.VisibleCharacters = -1;
        _typed = float.MaxValue;
        _age = PopSeconds;
        _frozen = true;
        Place();
    }

    /// <summary>Gone at once.</summary>
    public void Silence()
    {
        _speaking = false;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!_speaking)
            return;
        if (!_frozen)
            Advance((float)delta);
        if (_speaking)
            Place();
    }

    /// <summary>The line's own time: the pop, the typing, the hold, the fade.</summary>
    private void Advance(float dt)
    {
        _age += dt;
        int total = _label.GetTotalCharacterCount();
        if (_typed < total)
        {
            _typed = Mathf.Min(total, _typed + LettersPerSecond * dt);
            _label.VisibleCharacters = _typed >= total ? -1 : (int)_typed;
            return;
        }
        if (_fade < 0f)
        {
            if (_hold <= 0f)
                return;
            _held += dt;
            if (_held >= _hold)
                _fade = 0f;
            return;
        }
        _fade += dt;
        SetAlpha(1f - _fade / FadeSeconds);
        if (_fade >= FadeSeconds)
            Silence();
    }

    /// <summary>Over the speaker's head on screen, leaning right of it, clear of the HUD and inside the
    /// screen; hidden while the head is behind the camera.</summary>
    private void Place()
    {
        var viewport = GetViewport();
        var camera = viewport?.GetCamera3D();
        if (camera == null || Speaker == null || !IsInstanceValid(Speaker) || !Speaker.IsInsideTree())
        {
            Visible = false;
            return;
        }
        Vector3 head = Speaker.GlobalPosition + new Vector3(0f, HeadHeight, 0f);
        if (camera.IsPositionBehind(head))
        {
            Visible = false;
            return;
        }
        Rect2 screen = viewport.GetVisibleRect();
        Vector2 tip = camera.UnprojectPosition(head);
        tip.X = Mathf.Clamp(tip.X, screen.Position.X + ScreenMargin, screen.End.X - ScreenMargin);
        tip.Y = Mathf.Clamp(tip.Y, screen.Position.Y + ScreenMargin + TailLength, screen.End.Y - ScreenMargin);

        Vector2 size = _panel.GetCombinedMinimumSize();
        var box = new Rect2(new Vector2(tip.X - size.X * 0.3f, tip.Y - TailLength - size.Y), size);
        box = KeepClear(box, screen);
        _panel.Position = box.Position;
        _panel.Size = size;
        _tip = tip;

        float pop = Mathf.Clamp(_age / PopSeconds, 0f, 1f);
        PivotOffset = tip;
        Scale = Vector2.One * Mathf.Lerp(0.55f, 1f, BackOut(pop));
        Visible = true;
        _tail.QueueRedraw();
    }

    /// <summary>Moves the bubble off any HUD panel it overlaps (to the panel's right, or under it when
    /// there is no room there), then inside the screen.</summary>
    private Rect2 KeepClear(Rect2 box, Rect2 screen)
    {
        foreach (var panel in KeepClearOf)
        {
            if (panel == null || !IsInstanceValid(panel) || !panel.IsVisibleInTree())
                continue;
            Rect2 hud = panel.GetGlobalRect();
            if (!box.Intersects(hud))
                continue;
            if (hud.End.X + ScreenMargin + box.Size.X <= screen.End.X - ScreenMargin)
                box.Position = new Vector2(hud.End.X + ScreenMargin, box.Position.Y);
            else
                box.Position = new Vector2(box.Position.X, hud.End.Y + ScreenMargin);
        }
        float left = screen.Position.X + ScreenMargin;
        float top = screen.Position.Y + ScreenMargin;
        float x = Mathf.Clamp(box.Position.X, left, Mathf.Max(left, screen.End.X - ScreenMargin - box.Size.X));
        float y = Mathf.Clamp(box.Position.Y, top, Mathf.Max(top, screen.End.Y - ScreenMargin - box.Size.Y));
        return new Rect2(new Vector2(x, y), box.Size);
    }

    /// <summary>The tail: a wedge of the bubble's paper from its edge nearest the speaker's head to the
    /// head, inked along its two sides. None when the head is under the bubble.</summary>
    private void DrawTail()
    {
        if (!Visible || !_speaking)
            return;
        var box = new Rect2(_panel.Position, _panel.Size);
        Vector2 tip = _tip;
        float lo = Corner + TailHalfBase;
        Vector2 a;
        Vector2 b;
        if (tip.Y >= box.End.Y)
        {
            float x = Mathf.Clamp(tip.X, box.Position.X + lo, box.End.X - lo);
            a = new Vector2(x - TailHalfBase, box.End.Y - Ink);
            b = new Vector2(x + TailHalfBase, box.End.Y - Ink);
        }
        else if (tip.Y <= box.Position.Y)
        {
            float x = Mathf.Clamp(tip.X, box.Position.X + lo, box.End.X - lo);
            a = new Vector2(x - TailHalfBase, box.Position.Y + Ink);
            b = new Vector2(x + TailHalfBase, box.Position.Y + Ink);
        }
        else if (tip.X < box.Position.X)
        {
            float y = Mathf.Clamp(tip.Y, box.Position.Y + lo, box.End.Y - lo);
            a = new Vector2(box.Position.X + Ink, y - TailHalfBase);
            b = new Vector2(box.Position.X + Ink, y + TailHalfBase);
        }
        else if (tip.X > box.End.X)
        {
            float y = Mathf.Clamp(tip.Y, box.Position.Y + lo, box.End.Y - lo);
            a = new Vector2(box.End.X - Ink, y - TailHalfBase);
            b = new Vector2(box.End.X - Ink, y + TailHalfBase);
        }
        else
        {
            return;
        }
        if ((tip - (a + b) * 0.5f).LengthSquared() < 16f)
            return;
        _tail.DrawColoredPolygon(new[] { a, b, tip }, UITheme.SurfaceLight);
        _tail.DrawLine(a, tip, UITheme.CardInk, Ink, true);
        _tail.DrawLine(b, tip, UITheme.CardInk, Ink, true);
    }

    /// <summary>Sizes the words: one line when they fit MaxTextWidth, wrapped at it when not.</summary>
    private void Fit()
    {
        var font = _label.GetThemeFont("font");
        int size = _label.GetThemeFontSize("font_size");
        float width = font != null
            ? font.GetStringSize(_label.Text, HorizontalAlignment.Left, -1f, size).X
            : MaxTextWidth;
        _label.CustomMinimumSize = new Vector2(Mathf.Min(MaxTextWidth, Mathf.Ceil(width) + 2f), 0f);
        _panel.ResetSize();
    }

    private void SetAlpha(float alpha)
    {
        Color m = Modulate;
        m.A = Mathf.Clamp(alpha, 0f, 1f);
        Modulate = m;
    }

    /// <summary>An ease that overshoots a little and settles: the bubble's pop.</summary>
    private static float BackOut(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    private static Font BubbleFont()
    {
        if (_font == null && ResourceLoader.Exists(FontPath))
            _font = GD.Load<Font>(FontPath);
        return _font;
    }
}
