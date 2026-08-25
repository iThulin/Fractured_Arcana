using System.Collections.Generic;

// ============================================================
// ICityCombatSource.cs
//
// Purpose:        Read-only contract the CityBattlemapCompiler consumes
//                 to turn a districted city (home campus or, post-P3.2,
//                 an NPC settlement) into siege battlemap recipes. The
//                 compiler never mutates city state; implementations
//                 never know combat exists.
// Layer:          System (strategic -> combat seam)
// Collaborators:  HomeCityCombatSource (campus adapter),
//                 CityBattlemapCompiler (consumer, increment 2),
//                 docs/city_battlemap_compiler_spec_v1_1.md (spec)
// See:            campus_siege_and_defense_v1_1.docx (vectors, tile
//                 semantics), tools/city_compiler_proto.py (geometry
//                 verified numerically before this file was authored)
// ============================================================

/// <summary>What a fine-lattice cell IS, for terrain compilation. Locked =
/// not part of the city (no cell laid); compiles to rubble or is omitted.</summary>
public enum CityCellKind
{
    Plaza,
    Lawn,
    Corner,
    Locked,
}

/// <summary>Siege attack vectors. Mirrors the enum locked in
/// campus_siege_and_defense_v1_1.docx §3/§5 (WallSiege covers both the
/// gate-assault and wall-breach entry treatments).</summary>
public enum SiegeVector
{
    WallSiege,
    DockRaid,
    PortalStrike,
}

/// <summary>One placed, active building as the compiler sees it. Rotation is
/// carried for the docx §5 schema work (BuildingSaveData.Rotation); the home
/// adapter reports 0 until that field lands.</summary>
public sealed class CityBuildingRef
{
    public string BlueprintId = "";
    public int Rotation = 0;
}

/// <summary>
/// Read-only view of a districted city for siege battlemap compilation.
/// Coordinates are FINE-LATTICE AXIAL (the /3 flower lattice of
/// CampusMapSaveData), the same family the combat grid uses, so no offset
/// conversion exists anywhere in this pipeline.
/// </summary>
public interface ICityCombatSource
{
    /// <summary>Every laid cell of the city (the lots).</summary>
    IEnumerable<(int q, int r)> Cells { get; }

    /// <summary>Kind of a cell; Locked for coordinates with no laid cell.</summary>
    CityCellKind KindOf(int q, int r);

    /// <summary>The active (built AND placed) building on a lot, else null.</summary>
    CityBuildingRef BuildingAt(int q, int r);

    /// <summary>The seat lot: grand_hall at home; the archmage seat in NPC
    /// cities. The final siege window is built around it.</summary>
    (int q, int r) SeatCell { get; }

    /// <summary>Gate lot (gatehouse_yard), or null → no gate-assault entry.</summary>
    (int q, int r)? GateCell { get; }

    /// <summary>Dock lot, or null if landlocked / EntryDockType unwired.</summary>
    (int q, int r)? DockCell { get; }

    /// <summary>Portal lot (teleport_sigil), or null → no PortalStrike entry.
    /// Availability is diegetic: an unbuilt sigil is not a vector.</summary>
    (int q, int r)? TeleporterCell { get; }
}
