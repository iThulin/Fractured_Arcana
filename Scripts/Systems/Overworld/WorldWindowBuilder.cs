using Godot;
using System.Collections.Generic;

// ============================================================
// WorldWindowBuilder.cs
//
// Purpose:        Builds an expedition window — a radius-R hex
//                 disc of the persistent WorldData — into an
//                 OverworldHexGrid. Replaces OverworldHexGrid's
//                 region GENERATION: instead of inventing terrain
//                 from a seed, it reads authoritative world tiles
//                 and instantiates one OverworldHex per tile in
//                 the disc, mapping:
//                   world terrain   -> hex terrain
//                   tile discovery  -> hex fog
//                   world POI table -> hex POI
//                 The window is a VIEW; the world never regenerates.
//                 On extract, ExpeditionManager writes revealed
//                 state back into WorldData (the inverse of this).
// Layer:          System
// Collaborators:  WorldData.cs (source), HexCoord.cs (disc/convert),
//                 OverworldHexGrid.cs (container it fills),
//                 OverworldHex.cs (per-tile node),
//                 ExpeditionManager.cs (caller + write-back)
// See:            single_world_refactor_v2.docx §4.1 (expedition view)
//
// Coordinate mapping (verified): world stores OFFSET (col,row).
// The grid keys Hexes by AXIAL, positioned by AxialToWorld. We
// convert each world offset tile to world-axial, then recenter on
// the staging point so the staging tile sits at grid axial (0,0)
// and the window renders as a disc centered on screen — no shear.
// ============================================================

/// <summary>Maps a window of the persistent world into an OverworldHexGrid,
/// and back again on extract. One instance per expedition.</summary>
public class WorldWindowBuilder
{
    public WorldData World { get; }
    public int StagingCol { get; }
    public int StagingRow { get; }
    public int Radius { get; }

    // world-axial of the staging point (recenter origin)
    private readonly int _originQ;
    private readonly int _originR;

    // grid-local-axial  <->  world-offset, for the tiles in this window
    private readonly Dictionary<Vector2I, (int col, int row)> _localToWorld = new();
    private readonly Dictionary<(int col, int row), Vector2I> _worldToLocal = new();

    public IReadOnlyDictionary<Vector2I, (int col, int row)> LocalToWorld => _localToWorld;

    public WorldWindowBuilder(WorldData world, int stagingCol, int stagingRow, int radius)
    {
        World = world;
        StagingCol = stagingCol;
        StagingRow = stagingRow;
        Radius = radius;
        (_originQ, _originR) = HexCoord.OffsetToAxial(stagingCol, stagingRow);
    }

    /// <summary>The party's start coord in grid-local axial space — always (0,0),
    /// since the window is recentered on the staging point.</summary>
    public Vector2I PartyStartLocal => Vector2I.Zero;

    /// <summary>Populate the grid's Hexes from the world slice. The grid must be
    /// in the tree (so child OverworldHex nodes get _Ready) — call from the
    /// manager after AddChild(grid).</summary>
    public void Build(OverworldHexGrid grid)
    {
        // Clear anything the grid generated.
        foreach (var hex in grid.Hexes.Values)
            hex.QueueFree();
        grid.Hexes.Clear();
        _localToWorld.Clear();
        _worldToLocal.Clear();

        foreach (var (col, row) in World.Disc(StagingCol, StagingRow, Radius))
        {
            var (wq, wr) = HexCoord.OffsetToAxial(col, row);
            var local = new Vector2I(wq - _originQ, wr - _originR);

            var worldTile = World.GetTile(col, row);

            var hex = new OverworldHex
            {
                Axial = local,
                Terrain = worldTile.Terrain,
                Fog = FogFromDiscovery(worldTile.Discovery),
            };

            // Attach POI from the world table, if this tile has one that's been
            // discovered (undiscovered POIs aren't shown until revealed in-window).
            var poi = World.PoiAt(col, row);
            if (poi != null)
            {
                hex.POI = MapPoiKind(poi.Kind);
                // A POI already consumed in the world stays consumed.
                hex.POIConsumed = poi.Consumed;
            }

            hex.Position = grid.AxialToWorld(local);
            hex.HexClicked += grid.RaiseHexClicked;
            grid.AddChild(hex);
            grid.Hexes[local] = hex;

            _localToWorld[local] = (col, row);
            _worldToLocal[(col, row)] = local;
        }

        // The grid's entry is the staging point; no objective in the window model.
        grid.SetWindowAnchors(PartyStartLocal);

        GD.Print($"[WindowBuilder] Built window @ staging ({StagingCol},{StagingRow}) " +
                 $"R={Radius}: {grid.Hexes.Count} tiles.");
    }

    /// <summary>Convert a grid-local axial coord back to world offset coords.</summary>
    public bool TryLocalToWorld(Vector2I local, out int col, out int row)
    {
        if (_localToWorld.TryGetValue(local, out var w))
        {
            col = w.col; row = w.row;
            return true;
        }
        col = row = -1;
        return false;
    }

    // ── Discovery -> fog ─────────────────────────────────────────────────
    // Explored world tiles open as Revealed (you've been here this cycle).
    // Charted tiles open as Silhouette (seen at distance). Unseen stay Hidden
    // and get revealed by the party's vision as it explores the window.
    private static OverworldHex.FogState FogFromDiscovery(TileDiscovery d) => d switch
    {
        TileDiscovery.Explored => OverworldHex.FogState.Revealed,
        TileDiscovery.Charted => OverworldHex.FogState.Silhouette,
        _ => OverworldHex.FogState.Hidden,
    };

    // ── World PoiKind -> expedition POIType ──────────────────────────────
    // The five expedition-scale kinds map 1:1. Seat/Settlement are world-scale;
    // in-window they present as Outpost-style markers for now (bespoke
    // interactions come later), so they read as "something significant here."
    private static OverworldHex.POIType MapPoiKind(PoiKind kind) => kind switch
    {
        PoiKind.Combat => OverworldHex.POIType.Combat,
        PoiKind.Rest => OverworldHex.POIType.Rest,
        PoiKind.Narrative => OverworldHex.POIType.Narrative,
        PoiKind.Negotiation => OverworldHex.POIType.Negotiation,
        PoiKind.Outpost => OverworldHex.POIType.Outpost,
        PoiKind.Seat => OverworldHex.POIType.Outpost,
        PoiKind.Settlement => OverworldHex.POIType.Outpost,
        _ => OverworldHex.POIType.None,
    };
}
