using Godot;

// ============================================================
// PauseButton.cs
//
// Purpose:        Drop-in top-right pause button. Anchors itself
//                 in code at _Ready, so any scene that adds this
//                 node gets a working pause button with zero
//                 additional wiring. Routes presses to the
//                 PauseManager autoload.
// Layer:          UI
// Collaborators:  PauseManager.cs (singleton it talks to),
//                 UITheme.cs (font size)
// See:            (none)
// ============================================================

/// <summary>Drop-in top-right pause button. Anchors itself to the parent's top-right corner at <see cref="_Ready"/> so any scene gets a working pause button by adding this node alone. Routes presses to <see cref="PauseManager"/>.</summary>
public partial class PauseButton : Button
{
    [Export] public bool AnchorTopRight = true;
    [Export] public Vector2 OffsetFromCorner = new Vector2(-12, 12);

    public override void _Ready()
    {
        // Default appearance: small, unobtrusive corner button.
        Text = "☰";
        if (CustomMinimumSize == Vector2.Zero)
            CustomMinimumSize = new Vector2(40, 40);
        AddThemeFontSizeOverride("font_size", UITheme.FontSizePauseButton);
        FocusMode = FocusModeEnum.None;
        ProcessMode = ProcessModeEnum.Always;

        if (AnchorTopRight)
        {
            // Anchor to top-right corner of parent.
            AnchorLeft = 1f;
            AnchorTop = 0f;
            AnchorRight = 1f;
            AnchorBottom = 0f;
            // Offsets are measured from the anchor point.
            OffsetLeft = OffsetFromCorner.X - CustomMinimumSize.X;
            OffsetTop = OffsetFromCorner.Y;
            OffsetRight = OffsetFromCorner.X;
            OffsetBottom = OffsetFromCorner.Y + CustomMinimumSize.Y;
        }

        Pressed += OpenPauseMenu;
    }

    private void OpenPauseMenu()
    {
        if (PauseManager.Instance != null)
            PauseManager.Instance.OpenPauseMenu();
        else
            GD.PrintErr("[PauseButton] PauseManager autoload not found.");
    }
}
