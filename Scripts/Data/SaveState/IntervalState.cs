// ============================================================
// IntervalState.cs
//
// Purpose:        The interval's state for one reading (Standing Orders,
//                 docs/standing_orders_o1_interval_rules_v1.md section 5):
//                 whether the interval is being walked, where its route
//                 lies, and whether the door is set. Timeline state on
//                 CycleState.Interval, so a later reading starts with a
//                 fresh one that is not walking.
// Layer:          Data (save)
// Collaborators:  Interval.cs (the rules), CycleState.cs,
//                 IntervalStore.cs (the store's place, O3),
//                 IntervalSaveAssert.cs
// ============================================================

/// <summary>The weeks between the Sundering and the first new moon, for one reading.
/// Additive: a save from before the interval reads as not walking.</summary>
public sealed class IntervalState
{
    /// <summary>The interval is being walked: the moon is held and, until the door is set,
    /// the home is shut.</summary>
    public bool Active = false;

    /// <summary>The route is placed and the castle stands at its start.</summary>
    public bool Placed = false;

    /// <summary>The door hangs at {home}, and the home is open.</summary>
    public bool DoorSet = false;

    /// <summary>The lunation held for the whole interval, so an assert can tell whether a
    /// day ever passed.</summary>
    public int HeldLunation = 1;

    /// <summary>The day within the held lunation (0-based, as CalendarState.DayOfLunation).</summary>
    public int HeldDay = 0;

    /// <summary>The castle's start on the road.</summary>
    public int RoadX = -1;
    public int RoadY = -1;

    /// <summary>The Academy's field store (chapter II).</summary>
    public int StoreX = -1;
    public int StoreY = -1;

    /// <summary>The city of chapter III: an index into WorldData.Settlements, or -1.</summary>
    public int CitySettlement = -1;

    /// <summary>The Academy's field store as a place (Standing Orders O3, D187): an index into
    /// WorldData.Pois, or -1. Recorded so the store is known by identity, not by coordinates, and kept
    /// when a walk begins again, so the new walk reuses the same place (D188).</summary>
    public int StorePoiIndex = -1;
}
