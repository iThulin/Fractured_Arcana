using Godot;
using System.Collections.Generic;

// ============================================================
// WorldWindowBuilder.cs
//
// Purpose:        Builds and SLIDES the expedition window: a
//                 radius-R hex disc of the persistent WorldData
//                 rendered into an OverworldHexGrid. Replaces
//                 OverworldHexGrid's region GENERATION: instead of
//                 inventing terrain from a seed, it reads
//                 authoritative world tiles and instantiates one
//                 OverworldHex per tile in the disc, mapping:
//                   world terrain   -> hex terrain
//                   tile discovery  -> hex fog
//                   world POI table -> hex POI
//                 The window is a VIEW; the world never regenerates.
//
//                 W1 (sliding window, 2026-07-15): the disc is no
//                 longer fixed at the staging point. StreamTo()
//                 diffs the loaded tile set against a new center:
//                 tiles entering the load radius are instantiated
//                 from world data, tiles beyond the (larger) unload
//                 radius are freed. The hard perimeter is gone;
//                 range is governed by the step/HP economy and the
//                 W3 supply leash in ExpeditionManager, not by
//                 geometry. Discovery persists in WorldData, so a
//                 tile that unloads and later reloads returns with
//                 its illumination intact.
// Layer:          System
// Collaborators:  WorldData.cs (source), HexCoord.cs (disc/convert),
//                 OverworldHexGrid.cs (container it fills),
//                 OverworldHex.cs (per-tile node),
//                 ExpeditionManager.cs (caller + write-back + slide
//                 trigger)
// See:            single_world_refactor_v2.docx §4.1 (expedition view),
//                 claude/expedition_window_sliding_v1.md (W-track)
//
// Coordinate mapping (verified): world stores OFFSET (col,row).
// The grid keys Hexes by AXIAL, positioned by AxialToWorld. We
// convert each world offset tile to world-axial, then recenter on
// the staging point so the staging tile sits at grid axial (0,0),
// no shear. The local frame is a FIXED TRANSLATION of world-axial
// space (origin = staging, set once per expedition): it never moves
// when the window slides, so existing nodes never move, saved local
// coords (combat round-trips) stay valid, and LocalOf/WorldOf are
// pure formulas with no per-tile lookup tables.
// ============================================================

/// <summary>Maps a sliding window of the persistent world into an
/// OverworldHexGrid, and back again on extract. One instance per expedition.</summary>
public class WorldWindowBuilder
{
    public WorldData World { get; }
    public int StagingCol { get; }
    public int StagingRow { get; }

    /// <summary>Load radius: tiles within this hex distance of the window
    /// center are instantiated.</summary>
    public int Radius { get; }

    /// <summary>Unload radius: loaded tiles beyond this hex distance of the
    /// window center are freed. Larger than Radius so pacing back and forth
    /// over a seam doesn't thrash instantiate/free (hysteresis).</summary>
    public int UnloadRadius { get; }

    // world-axial of the staging point (the local frame's fixed origin)
    private readonly int _originQ;
    private readonly int _originR;

    public WorldWindowBuilder(WorldData world, int stagingCol, int stagingRow,
                              int radius, int unloadMargin = 3)
    {
        World = world;
        StagingCol = stagingCol;
        StagingRow = stagingRow;
        Radius = radius;
        UnloadRadius = radius + Mathf.Max(0, unloadMargin);
        (_originQ, _originR) = HexCoord.OffsetToAxial(stagingCol, stagingRow);
    }

    /// <summary>The party's start coord in grid-local axial space: always (0,0),
    /// since the local frame is anchored on the staging point.</summary>
    public Vector2I PartyStartLocal => Vector2I.Zero;

    // ── Coordinate mapping (pure formulas, total over the whole world) ──

    /// <summary>Grid-local axial coord of a world offset tile.</summary>
    public Vector2I LocalOf(int col, int row)
    {
        var (q, r) = HexCoord.OffsetToAxial(col, row);
        return new Vector2I(q - _originQ, r - _originR);
    }

    /// <summary>World offset coords of a grid-local axial coord. Total: does
    /// not require the tile to be loaded (may be out of world bounds).</summary>
    public (int col, int row) WorldOf(Vector2I local)
        => HexCoord.AxialToOffset(local.X + _originQ, local.Y + _originR);

    /// <summary>Convert a grid-local axial coord back to world offset coords.
    /// Formula-based (works for ANY coord, loaded or not); false only when the
    /// coord falls outside the world bounds.</summary>
    public bool TryLocalToWorld(Vector2I local, out int col, out int row)
    {
        (col, row) = WorldOf(local);
        if (World.InBounds(col, row))
            return true;
        col = row = -1;
        return false;
    }

    // ── Build / slide ─────────────────────────────────────────────────────

    /// <summary>Populate the grid's Hexes with the initial disc. Defaults to the
    /// staging point; pass <paramref name="centerLocal"/> to build directly
    /// around somewhere else: a combat/negotiation return with the party far
    /// afield builds around the PARTY instead, rather than paying for 469 tiles
    /// at staging that the restore recenter immediately frees (the +391/−469
    /// double-build observed in the 2026-07-15 playtest). The grid must be in
    /// the tree (so child OverworldHex nodes get _Ready); call from the
    /// manager after AddChild(grid).</summary>
    public void Build(OverworldHexGrid grid, Vector2I? centerLocal = null)
    {
        // Clear anything the grid generated.
        foreach (var hex in grid.Hexes.Values)
            hex.QueueFree();
        grid.Hexes.Clear();

        // Resolve the requested center; fall back to staging if it's off-world.
        int col = StagingCol, row = StagingRow;
        if (centerLocal.HasValue && !TryLocalToWorld(centerLocal.Value, out col, out row))
        { col = StagingCol; row = StagingRow; }

        StreamTo(grid, col, row);

        // The grid's entry is the staging point; no objective in the window model.
        // (Pure data, valid even when the staging tile itself isn't loaded.)
        grid.SetWindowAnchors(PartyStartLocal);

        bool atStaging = col == StagingCol && row == StagingRow;
        GD.Print($"[WindowBuilder] Built window @ ({col},{row})" +
                 $"{(atStaging ? " [staging]" : " [return]")} " +
                 $"R={Radius}: {grid.Hexes.Count} tiles.");
    }

    /// <summary>Slide the loaded window to a new center (world offset coords):
    /// instantiate tiles entering the load radius, free tiles beyond the unload
    /// radius. Idempotent; cost is O(perimeter · drift), not O(window). Returns
    /// (added, removed) tile counts for diagnostics.</summary>
    public (int added, int removed) StreamTo(OverworldHexGrid grid, int centerCol, int centerRow)
    {
        int added = 0;

        // ── Load: every world tile in the disc that has no live hex yet ──
        foreach (var (col, row) in World.Disc(centerCol, centerRow, Radius))
        {
            var local = LocalOf(col, row);
            if (grid.Hexes.ContainsKey(local))
                continue;
            grid.Hexes[local] = CreateHex(grid, local, col, row);
            added++;
        }

        // ── Unload: live hexes beyond the unload radius of the new center ──
        List<Vector2I> drop = null;
        foreach (var kvp in grid.Hexes)
        {
            var (col, row) = WorldOf(kvp.Key);
            if (World.HexDistance(col, row, centerCol, centerRow) > UnloadRadius)
                (drop ??= new List<Vector2I>()).Add(kvp.Key);
        }
        int removed = 0;
        if (drop != null)
        {
            foreach (var local in drop)
            {
                grid.Hexes[local].QueueFree();
                grid.Hexes.Remove(local);
                removed++;
            }
        }
        return (added, removed);
    }

    /// <summary>Instantiate one OverworldHex from its world tile and add it to
    /// the grid. Discovery persists in WorldData, so a reloaded tile returns
    /// with its illumination (fog state) intact.</summary>
    private OverworldHex CreateHex(OverworldHexGrid grid, Vector2I local, int col, int row)
    {
        var worldTile = World.GetTile(col, row);

        var hex = new OverworldHex
        {
            Axial = local,
            Terrain = worldTile.Terrain,
            Fog = FogFromDiscovery(worldTile.Discovery),
            RiverEdges = worldTile.RiverEdges,
            RoadEdges = worldTile.RoadEdges,
            SpringEdges = worldTile.SpringEdges,
            OceanDepth = worldTile.OceanDepth,
        };

        // Attach POI from the world table, if this tile has one. Visibility is
        // fog-gated (markers render only on Revealed tiles); a POI already
        // consumed in the world stays consumed.
        var poi = World.PoiAt(col, row);
        if (poi != null)
        {
            hex.POI = MapPoiKind(poi.Kind);
            hex.POIConsumed = poi.Consumed;
        }

        hex.Position = grid.AxialToWorld(local);
        hex.HexClicked += grid.RaiseHexClicked;
        grid.AddChild(hex);
        return hex;
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
        PoiKind.Prison => OverworldHex.POIType.Prison,
        // K3: rescue POIs present as Narrative sites in-window; the manager
        // detects the world-side kind at trigger time and routes to a rescue.
        PoiKind.Companion => OverworldHex.POIType.Narrative,
        // v1.1: caches render in-window as a green crate landmark. Sieges and
        // overseers still live on the strategic map; walking onto one is
        // reconnaissance (discovery + a report), not an encounter.
        PoiKind.SupplyCache => OverworldHex.POIType.SupplyCache,
        // Espionage E1c: Concord nodes are world-scale only; a bespoke broker
        // interaction (E3) replaces this. Non-rendering in-window for now,
        // explicit rather than falling to the default.
        PoiKind.Concord => OverworldHex.POIType.None,
        _ => OverworldHex.POIType.None,
    };
}
