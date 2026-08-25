// ============================================================
// Warfront.cs
//
// Purpose:  An active, multi-lunation zone of open conflict between
//           two provinces: an aggressor faction pressing into a
//           defender province. The VISIBLE, INTERVENABLE form of a
//           siege: instead of a border silently flipping when
//           BorderPressure crosses a threshold (the old
//           KingdomTickSimulation behaviour), tension boils over into
//           a warfront with an Advance bar (0–100). Each lunation the
//           aggressor pushes the bar up; at 100 the defender falls, at
//           0 the invasion is repelled. The player can deploy into the
//           front and take a side (Defend / Seize / Aid), swinging the
//           bar by the expedition's outcome.
// Layer:    Data
// Collaborators: KingdomTickSimulation (opens / advances / resolves +
//                applies interventions), StrategicView (renders the
//                marker + intervention dialog; carries the pending
//                intervention through the expedition round-trip),
//                CycleState (owns the list).
// See:      run_structure_v2 (Warfront region archetype); session log
//           2026-07-21 kingdom_sieges_and_seams.
// ============================================================

/// <summary>The side the guild takes when it deploys into a warfront.</summary>
public enum WarfrontSide
{
    /// <summary>Hold the line for the defender. A win pushes the advance bar back.</summary>
    Defend,
    /// <summary>Beat both sides. A win banks toward the province falling to the guild.</summary>
    Seize,
    /// <summary>Spearhead the aggressor. A win drives the advance bar up.</summary>
    Aid,
}

/// <summary>One active warfront. Serialized in CycleState.Warfronts.</summary>
public class Warfront
{
    /// <summary>Stable key "aggressorKid&gt;defenderKid". One warfront per directed pair.</summary>
    public string Id = "";

    public string AggressorKingdomId = "";
    public string DefenderKingdomId = "";

    /// <summary>Faction that annexes the defender if the front reaches 100.</summary>
    public string AggressorFactionId = "";
    public string DefenderFactionId = "";

    /// <summary>Cached display names for reports / the intervention dialog.</summary>
    public string AggressorName = "";
    public string DefenderName = "";

    /// <summary>Aggressor's progress, 0–100. ≥100 → defender falls; ≤0 → repelled.</summary>
    public int Advance = 0;

    public int OpenedLunation = 0;

    /// <summary>Border tile the front sits on: the deploy target for intervention
    /// (a defender tile bordering the aggressor; the party lands here).</summary>
    public int FocusCol = -1;
    public int FocusRow = -1;

    /// <summary>The besieging stronghold: an aggressor tile a few hexes into enemy
    /// ground. The intervention objective: the party marches from the front and
    /// storms it. Stamped as a Combat landmark in the expedition window.</summary>
    public int StrongholdCol = -1;
    public int StrongholdRow = -1;

    /// <summary>True once the guild has landed a successful Seize push. If the front
    /// is then driven to 0 the province falls to the guild rather than merely repelling.</summary>
    public bool PlayerSeizing = false;

    /// <summary>≥0 = this is a CACHE SIEGE: a warfront scoped to a single supply
    /// cache (index into WorldData.Pois) rather than a whole province. Cache
    /// sieges share the marker/intervention/deploy pipeline but advance and
    /// resolve in SupplyCacheSystem: at 100 the CACHE's controller flips, the
    /// province never falls. -1 = a normal province warfront.</summary>
    public int TargetPoiIndex = -1;

    /// <summary>True when this warfront besieges a single supply cache.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCacheSiege => TargetPoiIndex >= 0;

    public bool Closed = false;

    /// <summary>How it ended: "" (open), "fell", "repelled", "seized".</summary>
    public string Resolution = "";

    /// <summary>True once a valid front tile was found (deployable on the map).</summary>
    public bool HasFocus => FocusCol >= 0 && FocusRow >= 0;

    /// <summary>True once a besieging-stronghold tile was sited.</summary>
    public bool HasStronghold => StrongholdCol >= 0 && StrongholdRow >= 0;
}
