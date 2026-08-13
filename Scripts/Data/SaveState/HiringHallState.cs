using System.Collections.Generic;

// ============================================================
// HiringHallState.cs
//
// Purpose:        Per-city hiring hall stock (K3, companion_item
//                 _systems v2.1 §5a). Each visited city's hall holds
//                 generated candidates; stock refreshes each lunation
//                 (lazily, on menu open — not on the tick). Candidates
//                 are full Companion records so the dossier, save
//                 round-trip, and hire handoff all reuse the one
//                 companion model — no parallel candidate schema.
// Layer:          Data (SaveState)
// Collaborators:  HiringHallService.cs (refresh + hire),
//                 CandidateGenerator.cs (the procedural matrix),
//                 CityServicesHost.cs (the Recruit section UI).
// Notes:          Additive save field on CycleState (cycle-scoped —
//                 the world reseeds each cycle). NO version bump,
//                 same pattern as CityExploreState. Round-trip
//                 asserted in HiringHallService.RoundTripAssert.
// ============================================================

/// <summary>One city's hiring-hall stock: the candidates currently on offer and
/// the lunation the stock was last rolled. Keyed by the CityExploreService.CityId
/// convention ("{KingdomId}:{CenterX},{CenterY}") — one id grammar for all
/// per-city state. Lives in <see cref="CycleState.HiringHalls"/>.</summary>
public class HiringHallState
{
    /// <summary>Stable per-city id within a cycle (CityExploreService.CityId).</summary>
    public string CityId = "";

    /// <summary>Absolute lunation the stock was last generated. 0 = never —
    /// the first open always rolls. Refresh is lazy: compared against
    /// CalendarState.CurrentLunation when the hall is opened.</summary>
    public int LastRefreshLunation = 0;

    /// <summary>Candidates on offer, as full Companion records (IsRecruited =
    /// false while in the hall). Hiring MOVES the record into
    /// GuildSaveData.Companions — it is never in both lists.</summary>
    public List<Companion> Candidates = new();
}
