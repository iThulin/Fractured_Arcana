// ============================================================
// Lead.cs
//
// Purpose:        One rumour about one place: the Rumored stage of the
//                 knowledge ladder (docs/knowledge_ladder_spec_v1.md,
//                 sections 2 and 3). A lead points at a WorldPoi by index
//                 and draws a ring that holds the place without centring
//                 on it. It goes once its place is Sighted
//                 (WorldPoi.Discovered); LeadService.Prune is the rule.
// Layer:          Save state (CycleState.Leads, the timeline layer)
// Collaborators:  LeadService.cs (the rules), CycleState.cs (the list),
//                 WorldData.cs (WorldPoi.Discovered and Known),
//                 LeadsSaveAssert.cs (the round trip)
// ============================================================

/// <summary>A rumour about one place, the knowledge ladder's Rumored stage. Additive
/// save struct on <see cref="CycleState.Leads"/>, kept like the scry inbox's messages:
/// a save from before the ladder reads as no leads.</summary>
public sealed class Lead
{
    /// <summary>Stable id: the source plus the place's index, so one source never
    /// rumours the same place twice ("city:vaelmoor:41").</summary>
    public string Id = "";

    /// <summary>Index into <c>WorldData.Pois</c> of the place the rumour is about.</summary>
    public int PoiIndex = -1;

    /// <summary>The ring's centre, in world tiles. The place sits within
    /// <see cref="Radius"/> - 1 of it, so the ring holds the place without
    /// centring on it.</summary>
    public int CenterX;
    public int CenterY;

    /// <summary>The ring's radius in tiles: 3 for a city's talk, 4 for a companion's
    /// memory (starting values, spec section 3).</summary>
    public int Radius = 3;

    /// <summary>The words: the place's signal, prefixed with the source's flavour
    /// ("Talk in Vaelmoor: carrion birds circling, somewhere south of the river.").</summary>
    public string Line = "";

    /// <summary>Where it came from: <c>city:&lt;id&gt;</c>, <c>companion:&lt;id&gt;</c>,
    /// <c>encounter</c> or <c>register</c>.</summary>
    public string Source = "";

    /// <summary><c>CalendarState.AbsoluteDay</c> when it was heard.</summary>
    public int CreatedDay;
}
