using Godot;

// ============================================================
// StrategicView.Keeper.cs  (partial of StrategicView)
//
// Purpose:        The Keeper in the strategic view (docs/provinces_v1_10_
//                 keeper.md, slice PV6, PR79): its mark on the map,
//                 labelled with its next card, and the readout's note on
//                 the province it stands in and on roads it has fouled.
// Layer:          UI (strategic view)
// Collaborators:  KeeperHunt.cs (where it is, its next card, the fouled
//                 roads), WorldAtlas3D.Keeper.cs (the mark),
//                 StrategicView.cs (calls ShowKeeperOnAtlas with the
//                 warfronts), StrategicView.ProvinceHover.cs (the note)
// See:            docs/provinces_v1_10_keeper.md section 5
// ============================================================

public partial class StrategicView
{
    /// <summary>Hand the atlas the Keeper's mark: its province's seat and its next card. Set
    /// before the warfronts, whose rebuild draws it.</summary>
    private void ShowKeeperOnAtlas(CycleState cycle)
    {
        if (_atlas3D == null)
            return;
        var where = KeeperHunt.Where(cycle);
        _atlas3D.SetKeeper(
            where != null ? new Vector2I(where.SeatX, where.SeatY) : new Vector2I(-1, -1),
            where != null ? $"The Keeper: {KeeperHunt.IntentName(KeeperHunt.NextIntent(cycle))}" : "");
    }

    /// <summary>The readout's note on the Keeper: where it stands and what it does next, and
    /// roads it has fouled.</summary>
    private string KeeperNote(CycleState cycle, Province province)
    {
        if (cycle == null || province == null)
            return "";
        string note = "";
        var where = KeeperHunt.Where(cycle);
        if (where != null && where.Index == province.Index)
            note += $" The Keeper stands here. Its next move: {KeeperHunt.IntentText(cycle)}.";
        int cut = KeeperHunt.SupplyCut(cycle, province.Index);
        if (cut > 0)
            note += $" The Keeper has fouled the roads: a hop into it costs {cut} more fuel.";
        return note;
    }
}
