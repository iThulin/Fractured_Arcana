// ============================================================
// KeeperState.cs
//
// Purpose:        The Keeper who hunts the castle (docs/provinces_v1_10_
//                 keeper.md, slice PV6, X3): whether it is awake, the
//                 province it stands in and the one it woke in, where it
//                 believes the castle is, its next card in the intent
//                 deck, the roads it has fouled, and when it last struck.
//                 Plain data with safe defaults: a save from before PV6
//                 reads as a Keeper still asleep.
// Layer:          Data
// Collaborators:  CycleState.cs (Keeper), KeeperHunt.cs (reads and
//                 writes all of it), ProvinceSaveAssert.Keeper.cs
//                 (round trip)
// See:            docs/provinces_v1_10_keeper.md section 4
// ============================================================

/// <summary>The Keeper: one per cycle, on CycleState.Keeper. Provinces are named by Id.</summary>
public class KeeperState
{
    /// <summary>True once the Keeper has woken this cycle.</summary>
    public bool Awake = false;

    /// <summary>The province it stands in.</summary>
    public string ProvinceId = "";

    /// <summary>The province it woke in, where it draws back to after a strike.</summary>
    public string HomeProvinceId = "";

    /// <summary>Where it believes the castle is; "" when it has lost the trail.</summary>
    public string KnownCastleProvinceId = "";

    /// <summary>Its next card in the intent deck (KeeperHunt.Deck), shown before it is played.</summary>
    public int DeckIndex = 0;

    /// <summary>Cut Supply: the roads into this province and the ones beside it are fouled
    /// until the next new moon; "" for none.</summary>
    public string SupplyCutProvinceId = "";

    /// <summary>The lunation it last struck the castle in; -1 for never.</summary>
    public int StruckLunation = -1;
}
