using System.Collections.Generic;

// ============================================================
// WorldData.cs
//
// Purpose:        The authoritative, Civ-scale world for one
//                 cycle — pure data, no Godot nodes. A flat array
//                 of WorldTile indexed (y * Width + x), plus side
//                 tables for POIs and staging points. Read by
//                 both renderers (strategic MultiMesh + expedition
//                 window); written by expeditions (discovery, POI
//                 reveal) and the lunation tick (corruption, etc).
//                 Serializes into CycleState as plain data.
// Layer:          Data
// Collaborators:  WorldGenerator.cs (produces this),
//                 CycleState.cs (stores it),
//                 StrategicView (Phase 1b — paints it),
//                 OverworldHexGrid (Phase 1c — windows into it),
//                 KingdomState.cs (territories reference tiles)
// See:            single_world_refactor_v2.docx §3
//
// Indexing: flat row-major. tile(x,y) = Tiles[y * Width + x].
// Coordinates are OFFSET (x,y) for the array; the expedition
// view converts to axial when it instantiates window hexes, the
// same convention OverworldHexGrid already uses internally.
// ============================================================

/// <summary>Per-tile discovery state. Persistent for the whole cycle —
/// written by expeditions, read by both renderers. The illumination of
/// the strategic map across a cycle IS the exploration game.</summary>
public enum TileDiscovery
{
    /// <summary>Never entered an expedition window. Dark on the strategic view.</summary>
    Unseen = 0,
    /// <summary>Seen at distance (window fringe / intel): terrain + faction tint
    /// known, POIs and fine detail not yet discovered.</summary>
    Charted = 1,
    /// <summary>Entered and fully revealed by an expedition. Stays Explored
    /// for the rest of the cycle.</summary>
    Explored = 2,
}

/// <summary>One world cell. A plain struct — never a Godot node. The
/// expedition view builds an OverworldHex from this when it renders the
/// window; the strategic view reads it to color one quad.</summary>
public struct WorldTile
{
    public OverworldHex.TerrainType Terrain;

    /// <summary>Field scalars sampled once at generation, stored so the
    /// expedition view need not resample noise per window.</summary>
    public float Elevation;
    public float Moisture;

    /// <summary>Owning territory id, or empty for wilderness.</summary>
    public string KingdomId;

    /// <summary>Chronomancer corruption at this tile, 0–3. Spreads tile-to-tile.</summary>
    public byte Corruption;

    public TileDiscovery Discovery;

    /// <summary>Index into WorldData.Pois, or -1 for none.</summary>
    public int PoiIndex;

    /// <summary>True if an expedition may launch from here.</summary>
    public bool IsStagingPoint;

    /// <summary>River edges as a 6-bit mask in HexCoord.AxialDirections order;
    /// bit i set = a river runs along edge i. Set on BOTH tiles sharing the edge
    /// (neighbor across edge i carries bit (i+3)%6) so a window-fringe tile knows
    /// its own edges without its neighbor loaded. 0 = no river.</summary>
    public byte RiverEdges;

    /// <summary>Bridge/ford edges, same bit convention as RiverEdges. A set bit
    /// means a road crosses the river here at no crossing penalty.</summary>
    public byte BridgeEdges;

    // ── Terrain category predicates (route here, never compare == Water) ──
    public bool IsOcean => TerrainClass.IsOcean(Terrain);
    public bool IsLake => TerrainClass.IsLake(Terrain);
    public bool IsWater => TerrainClass.IsWater(Terrain);
    public bool IsLand => TerrainClass.IsLand(Terrain);
    public bool IsCoast => TerrainClass.IsCoast(Terrain);
}

/// <summary>A point of interest on the world map. Discovery is separate
/// from the tile's Discovery: a tile can be Explored while distant POIs
/// stay hidden, and a POI is only shown on the strategic view once found.</summary>
public class WorldPoi
{
    public int X;
    public int Y;

    /// <summary>POI category. Spans expedition-scale and world-scale kinds.</summary>
    public PoiKind Kind = PoiKind.Combat;

    /// <summary>Owning kingdom id, or empty.</summary>
    public string KingdomId = "";

    /// <summary>True once an expedition has discovered it. Undiscovered POIs
    /// are absent from the strategic view.</summary>
    public bool Discovered = false;

    /// <summary>True once resolved (combat won, rest used, narrative completed).
    /// A consumed POI persists as consumed so it isn't re-offered on revisit.</summary>
    public bool Consumed = false;

    /// <summary>True if discovering/securing this POI grants a staging point.</summary>
    public bool GrantsStaging = false;
}

/// <summary>One launch location. Accumulates as the world opens.</summary>
public class StagingPoint
{
    public int X;
    public int Y;
    public string Name = "";

    /// <summary>How it was gained: "Start","Outpost","Settlement","Secured".</summary>
    public string Source = "Start";

    /// <summary>True if currently selectable (always true in Phase 1;
    /// reputation/stance may gate some later).</summary>
    public bool Available = true;
}

/// <summary>The whole-world data for one cycle. Flat tile array + side
/// tables. Pure data; serializes into CycleState.</summary>
public class WorldData
{
    public int Width = 96;
    public int Height = 96;

    /// <summary>Row-major OFFSET storage: tile(col,row) = Tiles[row * Width + col].
    /// The world is a Civ-6-style rectangular hex map — flat-top, odd-q. Use
    /// HexCoord for distance/neighbors/disc; (col,row) are offset coordinates,
    /// not square coordinates.</summary>
    public WorldTile[] Tiles = System.Array.Empty<WorldTile>();

    public List<WorldPoi> Pois = new();
    public List<StagingPoint> StagingPoints = new();

    /// <summary>World coordinate of Kassian's seat (the Convergence). Corruption
    /// radiates from here; it is the cycle's terminal location.</summary>
    public int ConvergenceX = -1;
    public int ConvergenceY = -1;

    /// <summary>The rolled continental topology, for save/debug.</summary>
    public string ContinentStyle = "";

    // ── Access ───────────────────────────────────────────────────────────
    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public WorldTile GetTile(int x, int y) => Tiles[y * Width + x];

    public void SetTile(int x, int y, WorldTile t) => Tiles[y * Width + x] = t;

    /// <summary>Mutate a tile in place via index (avoids struct-copy mistakes
    /// at call sites). Returns false if out of bounds.</summary>
    public bool TryIndex(int x, int y, out int index)
    {
        if (!InBounds(x, y))
        { index = -1; return false; }
        index = y * Width + x;
        return true;
    }

    public WorldPoi PoiAt(int x, int y)
    {
        if (!InBounds(x, y))
            return null;
        int pi = GetTile(x, y).PoiIndex;
        return (pi >= 0 && pi < Pois.Count) ? Pois[pi] : null;
    }

    // ── Hex topology (the world is a flat-top odd-q rectangular hex map) ──
    /// <summary>Hex distance between two tiles in offset coordinates.</summary>
    public int HexDistance(int col1, int row1, int col2, int row2)
        => HexCoord.OffsetDistance(col1, row1, col2, row2);

    /// <summary>In-bounds offset cells within hex radius R of a center —
    /// the expedition window footprint.</summary>
    public System.Collections.Generic.List<(int col, int row)> Disc(int col, int row, int radius)
        => HexCoord.Disc(col, row, radius, Width, Height);
}
