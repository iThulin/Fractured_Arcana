public static class DragPayloadManager
{
    public static CardUi DraggedCard;
    public static bool IsTopHalf;
    public static bool IsDragging;

    public static void Clear()
    {
        DraggedCard = null;
        IsDragging = false;
        IsTopHalf = false;
    }
}