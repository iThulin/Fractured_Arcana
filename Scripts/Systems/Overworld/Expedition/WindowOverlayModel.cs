using Godot;
using System.Collections.Generic;

// ============================================================
// WindowOverlayModel.cs
//
// Purpose:        The window's per-tile GAMEPLAY overlay as plain
//                 data: the effective POI (world-mapped + civic/
//                 stronghold stamps), consumed state, objective/
//                 landmark flags, and contested-ground tint. Step 2
//                 of the atlas/expedition convergence: these flags
//                 previously lived ONLY as OverworldHex node
//                 properties (the stronghold objective literally
//                 didn't exist outside the scene); now the model is
//                 the authority and the 2D hexes mirror it.
// Layer:          Data (pure, no nodes)
// Collaborators:  ExpeditionManager.cs (owns one; SetOverlay is the
//                 write seam that keeps the node mirror in sync),
//                 FogOfWarManager.cs (reads it for landmark lures)
// See:            docs/atlas_expedition_convergence_v1.md §Step 2
//
// Scope contract: same as ExpeditionFogModel. Entries mirror the
// LOADED window; absent coord reads TileOverlay.None, the answer
// the old TryGetValue-miss produced. Persistent truths (WorldPoi.
// Discovered/Consumed) stay in WorldData; this model carries the
// window-scoped view of them plus the stamps that were never data.
// ============================================================

/// <summary>Everything gameplay knows about one window tile beyond terrain and
/// fog. A plain struct so a copy-modify-Set write pattern stays cheap.</summary>
public struct TileOverlay
{
    /// <summary>Effective POI on this tile: the world POI's mapped type, or a
    /// civic/stronghold stamp. None for an ordinary tile.</summary>
    public OverworldHex.POIType Poi;
    public bool Consumed;
    /// <summary>Warfront stronghold marker; draws the gold objective star.</summary>
    public bool Objective;
    /// <summary>Frontier-lure / stronghold beacon styling.</summary>
    public bool Landmark;
    /// <summary>Inside the active warfront's contested ground.</summary>
    public bool Contested;

    public static readonly TileOverlay None = new() { Poi = OverworldHex.POIType.None };
}

/// <summary>Per-coord overlay for the active expedition window, keyed by grid-local
/// axial coord. Plain data: renderer-independent; a 3D window view renders this the
/// same way the 2D hexes mirror it.</summary>
public class WindowOverlayModel
{
    private readonly Dictionary<Vector2I, TileOverlay> _tiles = new();

    /// <summary>Overlay at a coord; TileOverlay.None when unknown/unloaded, the
    /// same answer the old node-lookup miss produced.</summary>
    public TileOverlay OverlayAt(Vector2I local)
        => _tiles.TryGetValue(local, out var o) ? o : TileOverlay.None;

    public bool TryGet(Vector2I local, out TileOverlay overlay)
        => _tiles.TryGetValue(local, out overlay);

    public void Set(Vector2I local, TileOverlay overlay) => _tiles[local] = overlay;

    public void Clear() => _tiles.Clear();

    public IReadOnlyDictionary<Vector2I, TileOverlay> All => _tiles;
}
