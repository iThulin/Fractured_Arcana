using System.Collections.Generic;

// ============================================================
// KingdomState.cs
//
// Purpose:        Per-kingdom dynamic strategic state, the
//                 §3.2 state block. One per generated kingdom,
//                 stored in CycleState.Kingdoms keyed by region
//                 id. Phase 1 ships the fields, accessors, and
//                 generation defaults; the lunation TICK that
//                 mutates Stability / BorderPressure / Stance is
//                 Phase 2. Corruption is NOT duplicated here; it
//                 remains single-sourced in
//                 CampaignState.CorruptionLevels; read it through
//                 CampaignState, not a copy.
// Layer:          Data
// Collaborators:  StrategicMapGenerator.cs (creates + seeds),
//                 CycleState.cs (owns the dictionary),
//                 StrategicMapScreen.cs (renders),
//                 CampaignState.cs (corruption + archmage truth),
//                 KingdomTickSimulation (Phase 2, mutates)
// See:            open_world_refactor_v1.docx §3.2
//
// Kingdom identity: a kingdom's id EQUALS its source template's
// region id (each template is used at most once). TemplateRegionId
// is kept distinct so a future cycle can instance one template into
// several kingdoms without touching callers. The Phase 1 documents
// called a kingdom a "province"; since docs/provinces_v1.md a
// province is a region of a kingdom (Province.cs), about six to a
// kingdom, and the guild's influence is held on provinces
// (ProvinceInfluence.cs).
// ============================================================

/// <summary>The player's standing with a kingdom. The soft difficulty
/// gate of the open structure (Phase 2 wires reputation → stance →
/// access). Ordered weakest to strongest.</summary>
public enum KingdomStance
{
    Hostile,
    Unfriendly,
    Neutral,
    Friendly,
    Allied,
}

/// <summary>
/// Dynamic strategic state for one kingdom. Topology (grid
/// position, adjacency, display) lives in the StrategicNode; this
/// holds what changes over a cycle.
/// </summary>
public class KingdomState
{
    // ── Identity ─────────────────────────────────────────────────────────
    /// <summary>Kingdom id: equals the source template's region id.</summary>
    public string RegionId = "";

    /// <summary>Source template this kingdom generated from (terrain gen reads this).</summary>
    public string TemplateRegionId = "";

    /// <summary>Cached display name (authoritative copy is on the StrategicNode).</summary>
    public string DisplayName = "";

    // ── Control ──────────────────────────────────────────────────────────
    public string ControllingFactionId = "";

    // Stance REMOVED (v1.2 standing unification): derived from court
    // standing via CouncilQueries.StanceFor(cycle, kingdomId). Old saves'
    // stored stance values are ignored on load.

    // ── Difficulty ───────────────────────────────────────────────────────
    /// <summary>Difficulty tier, 1 to 3, derived at generation from distance to
    /// the player's start. Replaces the deleted completedRegions ramp as the
    /// per-kingdom difficulty floor.</summary>
    public int Tier = 1;

    // ── Simulation state (mutated in Phase 2) ────────────────────────────
    /// <summary>Internal cohesion 0–100. Low stability invites border pressure
    /// and accelerates corruption uptake. Player deeds raise it.</summary>
    public int Stability = 50;

    /// <summary>The guild's influence over the kingdom, 0 to 100. Since provinces
    /// carry influence (docs/provinces_v1.md section 7b) it is derived: the mean of
    /// the guild's influence in the kingdom's provinces, weighted by their tiles,
    /// kept current by ProvinceInfluence. Read it freely. To change it, act on a
    /// province (ProvinceInfluence.Add, AddNear, SetKingdom); a write made here
    /// anyway is spread over the kingdom's provinces at the next sync.</summary>
    public int PlayerInfluence = 0;

    /// <summary>The value ProvinceInfluence last derived into PlayerInfluence. A
    /// difference between the two is a write made outside the province layer, or a
    /// save from before provinces carried influence, and the next sync spreads it
    /// over the kingdom's provinces. 0 on such a save, so every province then starts
    /// at its kingdom's value.</summary>
    public int PlayerInfluenceDerived = 0;

    /// <summary>Accumulated tension per neighboring kingdom id. At threshold a
    /// border flips on the lunation tick (Phase 2).</summary>
    public Dictionary<string, int> BorderPressure = new();

    /// <summary>Harvested supplies 0–100 (docs/supply_cache_spec_v1), the
    /// kingdom's war chest. Fed each lunation by controlled supply caches
    /// (SupplyCacheSystem.Tick), burned by upkeep. Read as WAR MUSCLE
    /// (warfront advance), civic glue (stability drift), and patrol funding
    /// (OverworldFactionManager). A starved kingdom (0) destabilizes.</summary>
    public int SupplyStock = 0;

    // ── Convenience cache (truth lives in CampaignState) ─────────────────
    /// <summary>Cached archmage id resident here. Authoritative source is
    /// CampaignState.RegionArchmageMap; this is a generation-time copy for UI
    /// convenience. Empty if no archmage was placed.</summary>
    public string ArchmageId = "";

// ── Helpers ──────────────────────────────────────────────────────────
    // AtLeast / CanBuildOutpost REMOVED with the stored Stance. Gate reads:
    //   CouncilQueries.StanceFor(cycle, RegionId) >= KingdomStance.Neutral
}
