using System.Collections.Generic;

// ============================================================
// CityMarketState.cs
//
// Purpose:        Q4 city markets (companion_item_systems v2.1
//                 §7c): per-city item stock, refreshed each
//                 lunation (lazily, on menu open) — the item-side
//                 twin of HiringHallState. Stock is item IDS only;
//                 prices are derived at display time (GoldValue ×
//                 markup − Steward discount), never persisted.
// Layer:          Data (SaveState)
// Collaborators:  CityMarketService.cs (refresh + purchase),
//                 CityServicesHost.cs (the Market section UI).
// Notes:          Additive save field on CycleState.CityMarkets —
//                 NO version bump (CityExploreState pattern).
// ============================================================

/// <summary>One city market's stock this lunation. Keyed by the
/// CityExploreService.CityId convention, same as halls and explore state.</summary>
public class CityMarketState
{
    public string CityId = "";

    /// <summary>Absolute lunation the stock was last rolled. 0 = never.</summary>
    public int LastRefreshLunation = 0;

    /// <summary>ItemDefinition ids on the shelf. Buying removes the id;
    /// sold out stays sold out until the lunation turns (no re-roll scumming
    /// — same discipline as the halls).</summary>
    public List<string> StockItemIds = new();
}
