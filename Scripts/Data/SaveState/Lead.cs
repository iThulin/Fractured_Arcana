// ============================================================
// Lead.cs
//
// Purpose:        One rumor about one place: the Rumored stage of the
//                 knowledge ladder (docs/knowledge_ladder_spec_v1.md,
//                 sections 2 and 3). A lead points at a WorldPoi by index
//                 and draws a ring that holds the place without centering
//                 on it. It goes once its place is Sighted
//                 (WorldPoi.Discovered); LeadService.Prune is the rule.
//                 Since L4b1 (section 14) a lead can be false: an empty
//                 ring with no place, which stands until it is searched.
// Layer:          Save state (CycleState.Leads, the timeline layer)
// Collaborators:  LeadService.cs (the rules), CycleState.cs (the list),
//                 WorldData.cs (WorldPoi.Discovered and Known),
//                 LeadsSaveAssert.cs (the round trip)
// ============================================================

/// <summary>A rumor about one place, the knowledge ladder's Rumored stage. Additive
/// save struct on <see cref="CycleState.Leads"/>, kept like the scry inbox's messages:
/// a save from before the ladder reads as no leads.</summary>
public sealed class Lead
{
    /// <summary>Stable id: the source plus the place's index, so one source never
    /// rumors the same place twice ("talk:dustreach:41"). A false rumor has no place,
    /// so its id is the source plus its ring's center ("talk:dustreach:at:12,30").</summary>
    public string Id = "";

    /// <summary>Index into <c>WorldData.Pois</c> of the place the rumor is about; -1 for a
    /// false rumor.</summary>
    public int PoiIndex = -1;

    /// <summary>The ring's center, in world tiles. The place sits within
    /// <see cref="Radius"/> - 1 of it, so the ring holds the place without
    /// centering on it.</summary>
    public int CenterX;
    public int CenterY;

    /// <summary>The ring's radius in tiles: 3 for a city's talk, 4 for a companion's
    /// memory (starting values, spec section 3).</summary>
    public int Radius = 3;

    /// <summary>The words: the place's signal, prefixed with the source's flavor
    /// ("Talk in Dustreach: carrion birds circling, somewhere to the south.").</summary>
    public string Line = "";

    /// <summary>Where it came from: <c>talk:&lt;kingdom&gt;</c>, <c>companion:&lt;id&gt;</c>,
    /// <c>encounter</c>, <c>register</c> or <c>debug</c>.</summary>
    public string Source = "";

    /// <summary><c>CalendarState.AbsoluteDay</c> when it was heard.</summary>
    public int CreatedDay;

    /// <summary>A false rumor (F1, spec 4.6; D216): an empty ring, with no place
    /// (<see cref="PoiIndex"/> -1). Never shown to the player. It stands until a party
    /// stops on its center or a camp sees the whole ring (D217).</summary>
    public bool IsFalse;
}
