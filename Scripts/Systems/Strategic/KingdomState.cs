using System.Collections.Generic;

// ============================================================
// KingdomState.cs
//
// Purpose:        Per-province dynamic strategic state — the
//                 §3.2 state block. One per generated province,
//                 stored in CycleState.Kingdoms keyed by region
//                 id. Phase 1 ships the fields, accessors, and
//                 generation defaults; the lunation TICK that
//                 mutates Stability / BorderPressure / Stance is
//                 Phase 2. Corruption is NOT duplicated here — it
//                 remains single-sourced in
//                 CampaignState.CorruptionLevels; read it through
//                 CampaignState, not a copy.
// Layer:          Data
// Collaborators:  StrategicMapGenerator.cs (creates + seeds),
//                 CycleState.cs (owns the dictionary),
//                 StrategicMapScreen.cs (renders),
//                 CampaignState.cs (corruption + archmage truth),
//                 KingdomTickSimulation (Phase 2 — mutates)
// See:            open_world_refactor_v1.docx §3.2
//
// Province identity: for Phase 1, a province's id EQUALS its
// source template's region id (each template is used at most
// once). TemplateRegionId is kept distinct so a future cycle can
// instance one template into several provinces without touching
// callers.
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
/// Dynamic strategic state for one province. Topology (grid
/// position, adjacency, display) lives in the StrategicNode; this
/// holds what changes over a cycle.
/// </summary>
public class KingdomState
{
    // ── Identity ─────────────────────────────────────────────────────────
    /// <summary>Province id. Phase 1: equals the source template's region id.</summary>
    public string RegionId = "";

    /// <summary>Source template this province generated from (terrain gen reads this).</summary>
    public string TemplateRegionId = "";

    /// <summary>Cached display name (authoritative copy is on the StrategicNode).</summary>
    public string DisplayName = "";

    // ── Control ──────────────────────────────────────────────────────────
    public string ControllingFactionId = "";

    /// <summary>Player standing with the controlling faction's hold here.</summary>
    public KingdomStance Stance = KingdomStance.Neutral;

    // ── Difficulty ───────────────────────────────────────────────────────
    /// <summary>1–3 difficulty tier, derived at generation from distance to
    /// the player's start. Replaces the deleted completedRegions ramp as the
    /// per-province difficulty floor.</summary>
    public int Tier = 1;

    // ── Simulation state (mutated in Phase 2) ────────────────────────────
    /// <summary>Internal cohesion 0–100. Low stability invites border pressure
    /// and accelerates corruption uptake. Player deeds raise it.</summary>
    public int Stability = 50;

    /// <summary>Zone-of-influence value 0–100. Raised by outposts, resolved
    /// landmarks, Allied stance, archmage resolution. Painted on the map.</summary>
    public int PlayerInfluence = 0;

    /// <summary>Accumulated tension per neighboring province id. At threshold a
    /// border flips on the lunation tick (Phase 2).</summary>
    public Dictionary<string, int> BorderPressure = new();

    // ── Convenience cache (truth lives in CampaignState) ─────────────────
    /// <summary>Cached archmage id resident here. Authoritative source is
    /// CampaignState.RegionArchmageMap; this is a generation-time copy for UI
    /// convenience. Empty if no archmage was placed.</summary>
    public string ArchmageId = "";

    // ── Helpers ──────────────────────────────────────────────────────────
    /// <summary>True if the player meets at least the given stance.</summary>
    public bool AtLeast(KingdomStance required) => Stance >= required;

    /// <summary>Whether the player may currently establish an outpost here
    /// (Neutral+). The hard gate; cost is handled elsewhere.</summary>
    public bool CanBuildOutpost => Stance >= KingdomStance.Neutral;
}
