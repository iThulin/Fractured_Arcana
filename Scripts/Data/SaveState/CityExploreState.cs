using System.Collections.Generic;

// ============================================================
// CityExploreState.cs
//
// Purpose:        Per-city explore progress for visited NPC cities:
//                 which districts are revealed / cleared and what
//                 typed content each holds (Event / Service / Fight /
//                 Story / Empty). Cycle-scoped: the world reseeds each
//                 cycle, so this lives in CycleState.CityExplore and is
//                 keyed by CityExploreState.CityId.
// Layer:          Data (SaveState)
// Collaborators:  CityExploreService.cs (generation + lookup),
//                 WorldAtlas3D.cs (reveal + markers),
//                 StrategicView.cs (trigger dispatch).
// Notes:          Additive save field, no SaveManager version bump.
// ============================================================

/// <summary>Typed content a city district can hold. Stored as an int on
/// <see cref="CityDistrictEntry.Content"/> for JSON friendliness.</summary>
public enum DistrictContentType
{
    Empty = 0,
    Service = 1,
    Event = 2,
    Story = 3,
    Fight = 4,
}

/// <summary>One district's persisted explore state within a city: its axial
/// delta from the city centre, the content it holds, and whether it has been
/// revealed (scouted) or cleared (consumed).</summary>
public class CityDistrictEntry
{
    /// <summary>Axial delta (q,r) of this district from the city centre. The
    /// centre district is (0,0). Matches WorldAtlas3D's DistrictOf grouping.</summary>
    public int Dq = 0;
    public int Dr = 0;

    /// <summary><see cref="DistrictContentType"/> as an int (JSON-friendly).</summary>
    public int Content = 0;

    /// <summary>Optional content reference, e.g. a narrative encounter id, once
    /// an event has been bound. Empty until used.</summary>
    public string ContentRef = "";

    /// <summary>Scouted: the fog is lifted and the content marker is visible.</summary>
    public bool Revealed = false;

    /// <summary>Consumed: the content has been triggered and resolved. A cleared
    /// district shows no marker (a service district is never cleared; it stays reopenable).</summary>
    public bool Cleared = false;
}

/// <summary>Explore progress for one visited NPC city: the generated per-district
/// content plus reveal/clear flags. Persisted in <see cref="CycleState.CityExplore"/>
/// (cycle-scoped). <see cref="CityId"/> is stable within a cycle.</summary>
public class CityExploreState
{
    /// <summary>Stable per-city id within a cycle: "{KingdomId}:{CenterX},{CenterY}".</summary>
    public string CityId = "";

    /// <summary>True once content has been assigned to the districts. Guards against
    /// regenerating (and re-rolling) content for an already-visited city.</summary>
    public bool Generated = false;

    public List<CityDistrictEntry> Districts = new();
}
