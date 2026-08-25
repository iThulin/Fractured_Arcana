using System.Collections.Generic;

// ============================================================
// WorldData.cs
//
// Purpose:        The authoritative, Civ-scale world for one
//                 cycle: pure data, no Godot nodes. A flat array
//                 of WorldTile indexed (y * Width + x), plus side
//                 tables for POIs and staging points. Read by
//                 both renderers (strategic MultiMesh + expedition
//                 window); written by expeditions (discovery, POI
//                 reveal) and the lunation tick (corruption, etc).
//                 Serializes into CycleState as plain data.
// Layer:          Data
// Collaborators:  WorldGenerator.cs (produces this),
//                 CycleState.cs (stores it),
//                 StrategicView (Phase 1b, paints it),
//                 OverworldHexGrid (Phase 1c, windows into it),
//                 KingdomState.cs (territories reference tiles)
// See:            single_world_refactor_v2.docx §3
//
// Indexing: flat row-major. tile(x,y) = Tiles[y * Width + x].
// Coordinates are OFFSET (x,y) for the array; the expedition
// view converts to axial when it instantiates window hexes, the
// same convention OverworldHexGrid already uses internally.
// ============================================================

/// <summary>Per-tile discovery state. Persistent for the whole cycle,
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

/// <summary>One world cell. A plain struct, never a Godot node. The
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

    /// <summary>Chronomancer corruption at this tile, 0–100 (a "fully fallen" tile
    /// saturates at 100; CorruptionSpread clamps to that range). NOT the kingdom
    /// corruption LEVEL 0–3. That level maps onto this per-tile 0–100 scale
    /// (0→0, 1→40, 2→70, 3→100). Spreads tile-to-tile.</summary>
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

    /// <summary>Road edges, same 6-bit/both-sides convention as RiverEdges. A road
    /// runs ALONG edge i. Roads are edges, not tiles, so the underlying terrain is
    /// never overwritten, so roads run through cities over their real biome.</summary>
    public byte RoadEdges;

    /// <summary>Spring edges: thin headwater streams from high ground, same 6-bit/
    /// both-sides convention. Rendered thinner than RiverEdges.</summary>
    public byte SpringEdges;

    /// <summary>A bridge is DERIVED: an edge carrying both a road and a river is a
    /// road crossing a river, i.e. a bridge, fast to cross, with no ford penalty. Kept
    /// as a property so readers are unchanged and road∩river can never desync.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte BridgeEdges => (byte)(RoadEdges & RiverEdges);

    /// <summary>Hex distance from the nearest land for ocean tiles (1 = shoreline,
    /// rising to the open sea); 0 for land and lakes. Drives shallow→deep ocean
    /// shading and future ship navigation. Set by Bathymetry; struct-default 0 is
    /// correct for land, so no constructor init needed.</summary>
    public byte OceanDepth;

    /// <summary>Index into WorldData.Settlements, or -1 for none. A tile inside a
    /// city/town carries this; the settlement is an AREA, not a POI. MUST be set to
    /// -1 at construction. The struct default 0 would alias Settlements[0].</summary>
    public int SettlementIndex;

    /// <summary>Index into WorldData.ShardZones, or -1 for none. A tile inside a
    /// shard sub-region carries this. MUST be set to -1 at construction. The
    /// struct default 0 would alias ShardZones[0]. Independent of SettlementIndex:
    /// shard zones sit on wilderness tiles, never inside a settlement footprint.</summary>
    public int ShardZoneIndex;

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

    // ── Supply caches only (Kind == PoiKind.SupplyCache) ──────────────────
    /// <summary>Who harvests this cache: a kingdom id, or "guild" for the
    /// player. Empty (pre-feature saves) reads as the host KingdomId. Use
    /// SupplyCacheSystem.ControllerOf, never this field directly.</summary>
    public string SupplyControllerId = "";

    /// <summary>Companion posted to oversee a GUILD-controlled cache (+yield;
    /// injured if the cache falls). Empty = none. Availability is derived from
    /// this field (SupplyCacheSystem.IsOverseer), never from a flag on Companion,
    /// same single-source discipline as envoy missions.</summary>
    public string OverseerCompanionId = "";
}

/// <summary>Settlement scale. City = several tiles, grants staging, studded with
/// POIs; Town = a few tiles down to one, no staging, lightly studded.</summary>
public enum SettlementTier { Town, City }

/// <summary>A settlement AREA: a contiguous run of tiles, not a POI. Tiles keep
/// their biome and back-reference this via WorldTile.SettlementIndex. POIs are
/// studded into the footprint by the generator; staging (cities only) is granted
/// by a POI at the centre, through the normal POI-discovery path.</summary>
public class WorldSettlement
{
    public SettlementTier Tier = SettlementTier.Town;
    public int CenterX;
    public int CenterY;
    public string KingdomId = "";
    public string Name = "";

    /// <summary>Cities true, towns false. The staging itself is a POI at the centre;
    /// this flag is for rendering + intent.</summary>
    public bool GrantsStaging = false;

    /// <summary>True for the kingdom's primary city, grown from its archmage seat.
    /// Its centre already carries the Seat POI, so ScatterPois doesn't add a second
    /// staging POI there.</summary>
    public bool IsSeat = false;

    /// <summary>True for the settlement that hosts the guild's campus this cycle,
    /// the seat city grown from the start capital. The eternal campus is "located"
    /// here; re-derived each cycle since the world reseeds. (Phase 2: the campus is
    /// an actual place in the world.)</summary>
    public bool IsGuildHome = false;

    /// <summary>Every tile in this settlement's footprint (offset coords).</summary>
    public List<(int x, int y)> Tiles = new();
}

/// <summary>A shard acquisition sub-region: a contiguous footprint of tiles near
/// an archmage seat, holding one fragment. Like a settlement it is an AREA (tiles
/// back-reference via WorldTile.ShardZoneIndex), but it is its OWN system, not a
/// SettlementTier: it carries reduced-fog + step behaviour, a guardian GATE tile,
/// and an inner SANCTUM tile that holds the shard. Cleared once the guardian falls
/// (stamps fragment_&lt;key&gt;_collected) and grants staging at its centre.</summary>
public class ShardZone
{
    /// <summary>Fragment key: axiom|binding|deathless|moment|schema|primal. Binds
    /// the zone to its arc in fragment_arcs.json and to its guardian encounter.</summary>
    public string FragmentKey = "";

    public string KingdomId = "";
    public string Name = "";

    public int CenterX;
    public int CenterY;

    /// <summary>Guardian gate tile. Entering it launches the fragment trial +
    /// guardian boss. Defaults to the centre until sited.</summary>
    public int GateX;
    public int GateY;

    /// <summary>Inner sanctum tile. Holds the shard; collectable only after the
    /// guardian is cleared.</summary>
    public int SanctumX;
    public int SanctumY;

    /// <summary>True once an expedition has entered/charted any footprint tile;
    /// discovery opens the whole footprint to reduced fog (the vault layout reads).</summary>
    public bool Discovered = false;

    /// <summary>True once the guardian boss has fallen (fragment_&lt;key&gt;_trial_passed).</summary>
    public bool GuardianCleared = false;

    /// <summary>True once the shard has been taken (fragment_&lt;key&gt;_collected).</summary>
    public bool ShardCollected = false;

    /// <summary>Every tile in this zone's footprint (offset coords).</summary>
    public List<(int x, int y)> Tiles = new();
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
    /// The world is a Civ-6-style rectangular hex map: flat-top, odd-q. Use
    /// HexCoord for distance/neighbors/disc; (col,row) are offset coordinates,
    /// not square coordinates.</summary>
    public WorldTile[] Tiles = System.Array.Empty<WorldTile>();

    public List<WorldPoi> Pois = new();
    public List<StagingPoint> StagingPoints = new();
    public List<WorldSettlement> Settlements = new();
    public List<ShardZone> ShardZones = new();

    /// <summary>World coordinate of Kassian's seat (the Convergence). Corruption
    /// radiates from here; it is the cycle's terminal location.</summary>
    public int ConvergenceX = -1;
    public int ConvergenceY = -1;

    /// <summary>World coordinate of the guild's home this cycle, the start capital's
    /// seat, where the campus is sited. -1 until the generator sets it. The campus is
    /// eternal; this binding is per-cycle (the world reseeds each timeline).
    /// (Phase 2: campus-as-world-location.)</summary>
    public int HomeX = -1;
    public int HomeY = -1;

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

    public WorldSettlement SettlementAt(int x, int y)
    {
        if (!InBounds(x, y))
            return null;
        int si = GetTile(x, y).SettlementIndex;
        return (si >= 0 && si < Settlements.Count) ? Settlements[si] : null;
    }

    public ShardZone ShardZoneAt(int x, int y)
    {
        if (!InBounds(x, y))
            return null;
        int zi = GetTile(x, y).ShardZoneIndex;
        return (zi >= 0 && zi < ShardZones.Count) ? ShardZones[zi] : null;
    }

    // ── Hex topology (the world is a flat-top odd-q rectangular hex map) ──
    /// <summary>Hex distance between two tiles in offset coordinates.</summary>
    public int HexDistance(int col1, int row1, int col2, int row2)
        => HexCoord.OffsetDistance(col1, row1, col2, row2);

    /// <summary>In-bounds offset cells within hex radius R of a center,
    /// the expedition window footprint.</summary>
    public System.Collections.Generic.List<(int col, int row)> Disc(int col, int row, int radius)
        => HexCoord.Disc(col, row, radius, Width, Height);
}
