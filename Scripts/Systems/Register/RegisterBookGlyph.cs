using Godot;
using System;

// ============================================================
// RegisterBookGlyph.cs
//
// Purpose:        The Register's docked book glyph: a small open
//                 book drawn in code (no art asset), docked on the
//                 left edge of every gameplay screen. Click opens
//                 the margin notes. Glows while a note is waiting.
// Layer:          UI
// Collaborators:  RegisterManager.cs (owner)
// See:            docs/the_register_v1.md §4 (UI)
// ============================================================

/// <summary>Code-drawn open book that docks the Register on screen. Raises <see cref="Clicked"/> on a left click.</summary>
public partial class RegisterBookGlyph : Control
{
    public const float GlyphSize = 44f;

    /// <summary>Raised on a left click.</summary>
    public event Action Clicked;

    /// <summary>True while an explanation is waiting to be read. Makes the glyph glow.</summary>
    public bool Pending { get; set; }

    private bool _hover;
    private float _t;

    private static readonly Color Parchment = UITheme.CipherInkLight;
    private static readonly Color Ink = UITheme.CipherInk;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(GlyphSize, GlyphSize);
        Size = CustomMinimumSize;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        TooltipText = "The Register: margin notes";
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }

    public override void _Process(double delta)
    {
        if (!Pending && _t == 0f)
            return;
        _t = Pending ? _t + (float)delta : 0f;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            Clicked?.Invoke();
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        float s = GlyphSize;
        var center = new Vector2(s * 0.5f, s * 0.5f);

        // Backing disc, so the book reads over any scene behind it.
        float pulse = Pending ? 0.5f + 0.5f * Mathf.Sin(_t * 4f) : 0f;
        var disc = new Color(UITheme.BgBase.R, UITheme.BgBase.G, UITheme.BgBase.B, 0.92f);
        DrawCircle(center, s * 0.5f, disc);
        var ring = Pending ? UITheme.Gold.Lerp(Parchment, 0.4f * pulse) : (_hover ? Parchment : UITheme.TextSecondary);
        DrawArc(center, s * 0.5f - 1.5f, 0f, Mathf.Tau, 40, ring, Pending ? 3f : 2f, true);

        // The open book: two pages meeting at the spine.
        float w = s * 0.30f;      // page width
        float h = s * 0.36f;      // page height
        float sag = s * 0.05f;    // the pages dip toward the spine
        var spineTop = new Vector2(center.X, center.Y - h * 0.5f + sag);
        var spineBot = new Vector2(center.X, center.Y + h * 0.5f + sag);

        var left = new[]
        {
            spineTop,
            new Vector2(center.X - w, center.Y - h * 0.5f),
            new Vector2(center.X - w, center.Y + h * 0.5f),
            spineBot,
        };
        var right = new[]
        {
            spineTop,
            new Vector2(center.X + w, center.Y - h * 0.5f),
            new Vector2(center.X + w, center.Y + h * 0.5f),
            spineBot,
        };
        DrawColoredPolygon(left, Parchment);
        DrawColoredPolygon(right, Parchment);
        DrawLine(spineTop, spineBot, Ink, 1.5f, true);

        // Unfinished lines of ink. The last one stops short: the sentence it never finished.
        for (int i = 0; i < 3; i++)
        {
            float y = center.Y - h * 0.22f + i * h * 0.22f;
            DrawLine(new Vector2(center.X - w * 0.82f, y), new Vector2(center.X - w * 0.18f, y + sag * 0.6f), Ink, 1f, true);
            float end = i == 2 ? 0.45f : 0.82f;
            DrawLine(new Vector2(center.X + w * 0.18f, y + sag * 0.6f), new Vector2(center.X + w * end, y), Ink, 1f, true);
        }
    }
}
