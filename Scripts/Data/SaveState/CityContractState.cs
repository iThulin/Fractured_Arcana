using System.Collections.Generic;

// ============================================================
// CityContractState.cs
//
// Purpose:        Per-city CONTRACTS BOARD stock for visited NPC
//                 cities — the Phase 3 "Quests" service. A board
//                 posts a small set of kingdom-scoped contracts
//                 (scout districts / purge enclaves / aid citizens);
//                 accepting one tracks progress against the city-
//                 explore verbs, and turning it in pays gold and a
//                 Steward-routed echo.
// Layer:          Data (SaveState)
// Collaborators:  CityContractService.cs (generation, progress,
//                 turn-in), CityServicesHost.cs (the board UI),
//                 StrategicView.cs / WorldAtlas3D.cs (progress hooks).
// Notes:          Additive save field — no SaveManager version bump.
//                 Same CityId + lazy per-lunation refresh convention
//                 as CityMarketState / HiringHallState.
// ============================================================

/// <summary>One posted contract. Kind is a string for JSON friendliness:
/// "scout" (reveal districts anywhere in the posting kingdom), "purge"
/// (defeat district enclaves in the kingdom), "aid" (resolve district
/// events in the kingdom). Unaccepted offers reroll each lunation;
/// accepted contracts persist until turned in.</summary>
public class CityContract
{
    public string Id = "";
    public string Kind = "scout";

    /// <summary>How many qualifying deeds complete the contract, and how many
    /// are done. Progress only advances while Accepted.</summary>
    public int Target = 1;
    public int Progress = 0;

    public int GoldReward = 0;

    public bool Accepted = false;
    public bool Completed = false;

    public int PostedLunation = 0;
}

/// <summary>One city's contracts board: its posted offers plus the lazy
/// per-lunation refresh stamp. Persisted in CycleState.CityContractBoards
/// (cycle-scoped — the world reseeds each cycle).</summary>
public class CityContractBoardState
{
    /// <summary>CityExploreService.CityId convention: "{KingdomId}:{Cx},{Cy}".
    /// The kingdom prefix doubles as the contract's deed scope.</summary>
    public string CityId = "";

    public int LastRefreshLunation = -1;

    public List<CityContract> Offers = new();
}
