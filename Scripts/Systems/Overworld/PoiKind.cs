// ============================================================
// PoiKind.cs
//
// Purpose:        Strongly-typed POI category for the world map.
//                 Replaces the loose "Combat"/"Seat"/... strings
//                 in WorldPoi before they propagate into the
//                 strategic renderer and expedition-window code.
//                 Spans both expedition-scale POIs (the ones the
//                 old OverworldHex.POIType covered) and world-
//                 scale locations (kingdom seats, settlements).
// Layer:          Data
// Collaborators:  WorldData.cs (WorldPoi.Kind),
//                 WorldGenerator.cs (assigns),
//                 StrategicView (Phase 1b — icon/color per kind),
//                 OverworldHexGrid window builder (Phase 1c —
//                 maps these to OverworldHex.POIType where they
//                 overlap)
// See:            single_world_refactor_v2.docx §3.1
//
// Mapping to the expedition layer (Phase 1c): Combat/Rest/
// Narrative/Negotiation/Outpost correspond 1:1 to existing
// OverworldHex.POIType members. Seat/Settlement are world-scale
// only — they render on the strategic view and, inside a window,
// resolve to bespoke interactions rather than a plain POIType.
// ============================================================

/// <summary>Category of a world-map point of interest.</summary>
public enum PoiKind
{
    /// <summary>A hostile encounter site.</summary>
    Combat,
    /// <summary>A rest site (partial heal).</summary>
    Rest,
    /// <summary>A narrative/event location.</summary>
    Narrative,
    /// <summary>A negotiation contact.</summary>
    Negotiation,
    /// <summary>An outpost — full-heal checkpoint; grants a staging point when secured.</summary>
    Outpost,
    /// <summary>An archmage's seat — the heart of a kingdom (world-scale only).</summary>
    Seat,
    /// <summary>A settlement — a friendly/neutral hub; may grant staging (world-scale only).</summary>
    Settlement,
    /// <summary>The convergence seat — Kassian's seat, the endgame objective (world-scale only).</summary>
    Convergence,
}
