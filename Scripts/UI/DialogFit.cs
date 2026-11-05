using Godot;

/// <summary>
/// Sizing for the hand-built dialogs: a VBox of labels and lists inside an
/// AcceptDialog or a ConfirmationDialog.
///
/// <para>The trap. A Label that wraps reports a minimum width of one pixel.
/// A dialog sizes itself from its contents (Window.WrapControls, on for every
/// dialog), and it can measure them while they are still narrow, before the
/// dialog has laid them out at its real width. A wrapping label measured that
/// narrow is many lines tall. The dialog grows to fit, keeps its position, and
/// never shrinks back once the label settles, so it runs off the bottom of the
/// screen, and its buttons, which sit on its bottom edge, go with it. "Take the
/// field" lost its Go button this way (2026-11-05).</para>
///
/// <para>Two remedies. Wrap gives a label a real width to be measured at,
/// which is enough for a dialog that should still size itself to its text.
/// PopupFixed stops the dialog sizing itself at all: it pops up at the size
/// asked for (shrunk to fit a small screen), the buttons sit on its bottom
/// edge, and the contents take the room above them, so a list inside a
/// ScrollContainer scrolls.</para>
/// </summary>
public static class DialogFit
{
    /// <summary>The width a wrapping label in a dialog is measured at. It fits
    /// a dialog 540 or more wide once the panel's margins are taken.</summary>
    public const float TextWidth = 500f;

    /// <summary>The width for a wrapping label that shares its row with a
    /// button about 110 wide.</summary>
    public const float RowTextWidth = 360f;

    /// <summary>Room kept between a fixed dialog and the screen's edges, so its
    /// title bar and a margin stay on the screen.</summary>
    private const int ScreenMarginX = 48;
    private const int ScreenMarginY = 96;

    /// <summary>Wrap a label's text and give it a real width to be measured at.</summary>
    public static Label Wrap(Label label, float width = TextWidth)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(width, label.CustomMinimumSize.Y);
        return label;
    }

    /// <summary>Add the dialog under the host and pop it up centered at a fixed
    /// size, shrunk to fit the visible screen when that is smaller.</summary>
    public static void PopupFixed(Node host, AcceptDialog dlg, Vector2I size)
    {
        Vector2 room = host.GetViewport()?.GetVisibleRect().Size ?? new Vector2(size.X, size.Y);
        var fit = new Vector2I(
            Mathf.Min(size.X, Mathf.Max(320, (int)room.X - ScreenMarginX)),
            Mathf.Min(size.Y, Mathf.Max(240, (int)room.Y - ScreenMarginY)));
        dlg.WrapControls = false;
        dlg.MinSize = new Vector2I(Mathf.Min(dlg.MinSize.X, fit.X), Mathf.Min(dlg.MinSize.Y, fit.Y));
        host.AddChild(dlg);
        dlg.PopupCentered(fit);
    }
}
