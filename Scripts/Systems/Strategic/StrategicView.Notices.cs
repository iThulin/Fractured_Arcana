using Godot;

// ============================================================
// StrategicView.Notices.cs  (partial of StrategicView)
//
// Purpose:        Section 7j of the provinces spec
//                 (docs/provinces_v1_7j_test_fixes.md): the dialogs and
//                 the hint line, from the first play of the provinces
//                 build-out.
//                 Every dialog this screen opens wears the HUD's colours:
//                 the slate panel, the violet edge, a gold title and the
//                 HUD's buttons. They wore the game theme's grey before,
//                 and stood out from every card around them.
//                 A notice wraps its text at a fixed width, so a long one
//                 no longer stretches the dialog past the screen's edges.
//                 Arming March, or a party's move, no longer opens a notice
//                 at all: the rules go to the hint line at the foot of the
//                 screen while the mode is armed, and the map stays clear.
// Layer:          UI (strategic view)
// Collaborators:  StrategicView.cs (_Ready hooks StyleNewDialog;
//                 ShowStrategicNotice wraps at NoticeTextWidth; RefreshHint
//                 reads MarchHintLine and PartyMoveHintLine;
//                 OnMarchModeToggled calls RefreshHint), DialogFit, UITheme
// See:            docs/provinces_v1_7j_test_fixes.md
// ============================================================

public partial class StrategicView
{
    /// <summary>The width a notice's text wraps at.</summary>
    private const float NoticeTextWidth = 460f;

    /// <summary>The hint line's separator, as the line's other entries have it.</summary>
    private const string HintSeparator = "  \u00B7  ";

    /// <summary>A dialog's ground: the HUD's slate, nearly opaque.</summary>
    private static readonly Color DialogGround = new Color(UITheme.BgBase.R, UITheme.BgBase.G, UITheme.BgBase.B, 0.98f);

    // ── Dialogs in the HUD's colours ─────────────────────────────────────

    /// <summary>From ChildEnteredTree: a dialog this screen opens is styled once the code
    /// that made it has added its buttons (a deferred call).</summary>
    private void StyleNewDialog(Node node)
    {
        if (node is AcceptDialog dialog)
            Callable.From(() => StyleDialog(dialog)).CallDeferred();
    }

    /// <summary>The theme's own boxes, recoloured, so every margin and the title bar's height
    /// stay as the theme has them: the slate ground, the violet edge, a gold title, and the
    /// HUD's buttons (the default one primary).</summary>
    private static void StyleDialog(AcceptDialog dialog)
    {
        if (dialog == null || !GodotObject.IsInstanceValid(dialog) || !dialog.IsInsideTree())
            return;
        if (dialog.GetThemeStylebox("panel", "AcceptDialog") is StyleBoxFlat panel)
        {
            var ground = (StyleBoxFlat)panel.Duplicate();
            ground.BgColor = DialogGround;
            dialog.AddThemeStyleboxOverride("panel", ground);
        }
        else
        {
            dialog.AddThemeStyleboxOverride("panel", UITheme.MakePanelStyle(DialogGround, UITheme.Violet));
        }
        foreach (var name in new[] { "embedded_border", "embedded_unfocused_border" })
        {
            if (dialog.GetThemeStylebox(name, "Window") is StyleBoxFlat border)
            {
                var edge = (StyleBoxFlat)border.Duplicate();
                edge.BgColor = UITheme.BgRaised;
                edge.BorderColor = UITheme.Violet;
                dialog.AddThemeStyleboxOverride(name, edge);
            }
        }
        dialog.AddThemeColorOverride("title_color", UITheme.Gold);
        var ok = dialog.GetOkButton();
        var row = ok?.GetParent();
        if (row == null)
            return;
        foreach (var child in row.GetChildren(true))
        {
            if (child is Button button)
                UITheme.ApplyButtonStyle(button, isPrimary: button == ok);
        }
    }

    // ── The hint line while a move is armed ──────────────────────────────

    /// <summary>The hint line while March is armed for the castle: the march's rules in one
    /// line, short enough to sit between the corner cards, and how to stand down. The fuel is
    /// on the castle's card, and what is armed is on the confirm.</summary>
    private static string MarchHintLine(CycleState cycle)
    {
        string rules = cycle == null || CastleHops.CastleProvince(cycle) < 0
            ? $"{CastleMarch.FuelPerTile} fuel a tile"
            : $"{CastleHops.HopBase} fuel a hop, more for rough ground, fords and ridges"
              + HintSeparator + "the readout gives each province's cost";
        return "March: click charted ground" + HintSeparator + rules
               + HintSeparator + "press March castle to stand down.";
    }

    /// <summary>The hint line while a party's move is armed.</summary>
    private static string PartyMoveHintLine()
        => "Move: click charted ground" + HintSeparator
           + $"{FieldMarch.DaysPerTile} days a tile, {CastleReach.RoadDaysPerTile} on the castle's roads"
           + HintSeparator + "click the castle, its forward camp or a waystone to step through"
           + HintSeparator + "press Move party to stand down.";
}
