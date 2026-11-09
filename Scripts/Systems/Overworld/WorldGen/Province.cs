using System.Collections.Generic;

// ============================================================
// Province.cs
//
// Purpose:        One province: a region of a kingdom's land, the
//                 unit the strategic layer is rebuilt around (docs/
//                 provinces_v1.md). Each kingdom holds about six.
//                 Slice PV1 made them data: built once per world,
//                 drawn on the Political lens, named on the place
//                 card. Slice PV2a gives each the guild's influence
//                 there and whether the guild holds it. Fronts (PV3)
//                 and the castle's march (PV4) attach to them later.
//                 Slice PV2c adds the Economic favor's doubled yield
//                 (ProvinceBenefits.cs). Slice PV3a gives each a
//                 ruler (ControllerId, read through ProvinceControl.cs).
//                 Slice PV3b gives land no one rules the bars of its
//                 annexation (Annexation).
// Layer:          Data
// Collaborators:  ProvinceBuilder.cs (builds them), WorldData.cs
//                 (Provinces, ProvinceMap), ProvinceInfluence.cs
//                 (Influence, Held), WorldAtlas3D.cs (the Political
//                 and Reach lenses), StrategicView.cs (the place
//                 card), ProvinceSaveAssert.cs (round trip)
// See:            docs/provinces_v1.md sections 3, 4 and 7b
// ============================================================

/// <summary>What a province's ground mostly is, read from its tiles when it is built:
/// Lowland unless half or more of it is something else, then the most common of the
/// rest. Drives the placeholder name now, and later what the province offers (troop
/// lines in PV7, the castle's crossing costs in PV4). Saved as a number, so members
/// are only ever appended, never reordered.</summary>
public enum ProvinceCharacter
{
    Lowland = 0,
    Woodland = 1,
    Highland = 2,
    Mountain = 3,
    Wetland = 4,
    Arid = 5,
    Volcanic = 6,
    Frozen = 7,
    Arcane = 8,
    Ruined = 9,
}

/// <summary>A province of the world. Lives in WorldData.Provinces, which is built once
/// and never reordered or shortened, so Index is also the value WorldData.ProvinceMap
/// stores for its tiles. Save data outside WorldData names a province by Id, never by
/// Index. Plain data; no Godot types.</summary>
public class Province
{
    /// <summary>Position in WorldData.Provinces. -1 until built.</summary>
    public int Index = -1;

    /// <summary>Stable id: "province:" + the kingdom id ("wild" for land no kingdom
    /// owns) + ":" + the province's place in its kingdom, the capital being 0.</summary>
    public string Id = "";

    /// <summary>Placeholder name (docs/provinces_v1.md section 4.7): "Crownland" for
    /// the capital's province, otherwise a compass point from the capital plus a word
    /// for the ground, as in "North Weald".</summary>
    public string Name = "";

    /// <summary>The kingdom whose land this is (WorldTile.KingdomId), or empty for
    /// land no kingdom owns. Fixed at the build: who rules the province is
    /// ControllerId (PV3a).</summary>
    public string KingdomId = "";

    /// <summary>The province's seat: the centre of its chief settlement, or for a
    /// province with none, the tile nearest the middle of its land.</summary>
    public int SeatX = -1;
    public int SeatY = -1;

    /// <summary>Index into WorldData.Settlements of the settlement at the seat, or -1.</summary>
    public int SeatSettlementIndex = -1;

    /// <summary>True for the province holding its kingdom's seat city.</summary>
    public bool IsCapital = false;

    /// <summary>Tiles in the province.</summary>
    public int TileCount = 0;

    public ProvinceCharacter Character = ProvinceCharacter.Lowland;

    /// <summary>Indices into WorldData.Settlements of every settlement whose centre lies
    /// in the province, ascending.</summary>
    public List<int> Settlements = new();

    /// <summary>Indices of the provinces sharing a border with this one, ascending,
    /// across kingdom borders too. Water is no border.</summary>
    public List<int> Neighbors = new();

    /// <summary>Map tone, 0 to 3: provinces of one kingdom that share a border draw in
    /// different tones on the Political lens.</summary>
    public int Tone = 0;

    // ── The guild's influence (docs/provinces_v1.md section 7b, PV2a) ──────
    /// <summary>The guild's influence here, 0 to 100. Deeds in the province raise it; the
    /// castle's presence and held neighbours raise it too, but never past 40; it fades a
    /// little each lunation the castle is neither here nor beside it. Moved only through
    /// ProvinceInfluence, which derives each kingdom's PlayerInfluence from it: the mean
    /// of its provinces, weighted by their tiles.</summary>
    public int Influence = 0;

    /// <summary>True while the guild holds the province: set when Influence reaches 60,
    /// cleared when it falls below 50. Stored, because that hysteresis needs the past.
    /// Holding brings benefits (PV2c), never sovereignty.</summary>
    public bool Held = false;

    /// <summary>The lunation whose new moon pays this province's supply yield twice
    /// (docs/provinces_v1_7i_held_benefits.md, PR48): set by the Economic favor spent as
    /// its deed, to the lunation after the one it was spent in, and cleared by the first
    /// new moon that reaches it, held then or not. -1 for none.</summary>
    public int YieldDoubledLunation = -1;

    // ── Who rules it (docs/provinces_v1_8_fronts_per_province.md, PV3a) ────
    /// <summary>The province's ruler when its own kingdom does not rule it: the id of the
    /// kingdom that took it, "guild" for the guild's own province, "neutral" for one no
    /// one rules. Empty while its own kingdom rules it. Read it through
    /// ProvinceControl.RulerOf, which also answers for a kingdom the guild seized whole
    /// before fronts walked; change it through ProvinceControl.SetRuler.</summary>
    public string ControllerId = "";

    /// <summary>The lunation the province last changed hands in, for the readout and the
    /// card; -1 while it never has.</summary>
    public int ControlSinceLunation = -1;

    /// <summary>Each kingdom's bar toward annexing the province while no one rules it
    /// (docs/provinces_v1_8_fronts_per_province.md section 13, PV3b): kingdom id to 0 to
    /// 100, filled each lunation by KingdomTickSimulation.TickAnnexation and emptied when
    /// the province changes hands (ProvinceControl.SetRuler). Empty for every province
    /// someone rules.</summary>
    public Dictionary<string, int> Annexation = new();
}
