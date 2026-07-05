// ============================================================
// TerrainClass.cs
//
// Purpose:        The single source of truth for terrain category
//                 predicates. Replaces scattered `== Water` /
//                 `!= Water` checks that conflated four distinct
//                 questions: is this ocean, inland lake, any water,
//                 land, or coast. Introducing Lake + Coast as terrain
//                 types breaks every raw == Water site silently
//                 (a lake gets owned by a kingdom, a capital lands in
//                 water) unless those sites route through these
//                 predicates instead.
// Layer:          Data (pure, no nodes)
// Collaborators:  WorldTile (delegates its instance helpers here),
//                 WorldGenerator, StrategicView, OverworldHexGrid,
//                 WorldDebug — every former == Water site.
// ============================================================

using TT = OverworldHex.TerrainType;

public static class TerrainClass
{
    /// <summary>Open sea — the continent mask's ocean. Blocks, unownable,
    /// rings the world.</summary>
    public static bool IsOcean(TT t) => t == TT.Water;

    /// <summary>Inland water — a filled depression. Blocks movement and is
    /// unownable like ocean, but is NOT the continent-mask sea (so it sits
    /// inside a landmass, surrounded by owned land).</summary>
    public static bool IsLake(TT t) => t == TT.Lake;

    /// <summary>Any impassable water (ocean OR lake). Use for blocking and for
    /// "don't place a capital/POI/road here".</summary>
    public static bool IsWater(TT t) => IsOcean(t) || IsLake(t);

    /// <summary>Walkable, ownable land — anything that isn't water. Coast counts
    /// as land (it's a beach you stand on, adjacent to ocean).</summary>
    public static bool IsLand(TT t) => !IsWater(t);

    /// <summary>Beach: land directly adjacent to ocean. A land subtype, so
    /// IsLand is true; classified in a post-pass after territories.</summary>
    public static bool IsCoast(TT t) => t == TT.Coast;
}