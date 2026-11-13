using Godot;

// ============================================================
// GlassPanel.cs
//
// Purpose:        A PanelContainer that draws a dim inner hairline
//                 over its stylebox, which gives the black and gold
//                 panels their double line.
// Layer:          UI
// Collaborators:  UITheme (Glass tokens, MakeGlassPanelStyle),
//                 CombatUI (the only user during the trial)
// See:            Trial on branch ui/daylight-glass. The stylebox
//                 is still set by the caller, as with any panel.
// ============================================================

/// <summary>
/// Panel for the daylight glass trial. Behaves as a plain PanelContainer for
/// layout. The hairline sits just inside the border, clear of the content.
/// </summary>
public partial class GlassPanel : PanelContainer
{
    public override void _Ready()
    {
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        float inset = UITheme.GlassInnerLineInset;
        Vector2 size = Size;
        if (size.X <= inset * 2f || size.Y <= inset * 2f)
        {
            return;
        }

        var inner = new Rect2(new Vector2(inset, inset), size - new Vector2(inset * 2f, inset * 2f));
        DrawRect(inner, UITheme.GlassLineDim, false, 1f);
    }
}
