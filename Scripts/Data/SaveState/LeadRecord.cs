// ============================================================
// LeadRecord.cs
//
// Purpose:        A source of rumors, and its record for the reading
//                 (docs/knowledge_ladder_spec_v1.md, section 14, D218).
//                 A kingdom's talk keeps how many of its rumors were
//                 heard, held true and came to nothing; a settlement
//                 keeps the lunation it last talked, for the once-a-
//                 lunation rule (R1, D214).
// Layer:          Save state (CycleState.LeadRecords, the timeline layer)
// Collaborators:  LeadService.cs (the rules), CycleState.cs (the list),
//                 LeadsSaveAssert.cs (the round trip)
// ============================================================

/// <summary>One source of rumors and its record for the reading (D218). Additive save
/// struct on <see cref="CycleState.LeadRecords"/>: a save from before L4b reads as no
/// records.</summary>
public sealed class LeadRecord
{
    /// <summary><c>talk:&lt;kingdom id&gt;</c> for a kingdom's record, or
    /// <c>city:&lt;settlement id&gt;</c> (CityExploreService.CityId) for a settlement's
    /// once-a-lunation rule.</summary>
    public string Key = "";

    /// <summary>Rumors heard from this source.</summary>
    public int Heard;

    /// <summary>Rumors from this source whose place was Sighted.</summary>
    public int HeldTrue;

    /// <summary>Rumors from this source that were searched and came to nothing.</summary>
    public int CameToNothing;

    /// <summary>The lunation this settlement last talked (0: never). Read for
    /// <c>city:</c> keys.</summary>
    public int LastLunation;
}
