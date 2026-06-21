// ============================================================
// DragPayloadManager.cs
//
// Purpose:        Process-wide scratchpad holding the current
//                 card drag payload. Set by CardUi on drag-start,
//                 polled each frame by CardDropHandler to test
//                 for hovered tiles, cleared on drag-end.
// Layer:          System
// Collaborators:  CardUi.cs (writer), CardDropHandler.cs (reader)
// See:            (none)
// ============================================================

/// <summary>Process-wide drag-payload state. Holds the currently-dragged <see cref="CardUi"/> and which half (top/bottom) is being played. Lives across frames so the 3D drop handler can see it from a separate scene node.</summary>
public static class DragPayloadManager
{
    /// <summary>The card currently being dragged. Null when no drag is active.</summary>
    public static CardUi DraggedCard;

    /// <summary>True when the top half is being played; false for the bottom half.</summary>
    public static bool IsTopHalf;

    /// <summary>True while a drag is in progress.</summary>
    public static bool IsDragging;

    /// <summary>Resets all drag state. Called when a drag completes or cancels.</summary>
    public static void Clear()
    {
        DraggedCard = null;
        IsDragging = false;
        IsTopHalf = false;
    }
}