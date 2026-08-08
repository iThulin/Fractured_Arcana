using Godot;
using System.Collections.Generic;

// ============================================================
// ExpeditionFogModel.cs
//
// Purpose:        The run's fog state as PLAIN DATA — grid-local
//                 coord → FogState, no Godot nodes. Step 1 of the
//                 atlas/expedition convergence: fog authority moves
//                 out of OverworldHex.Fog (render nodes) into this
//                 model, so gameplay gates and the world write-back
//                 read data, and the 2D hexes become a display
//                 mirror. A future 3D expedition view renders the
//                 same model without touching any of this.
// Layer:          Data (pure, no nodes)
// Collaborators:  FogOfWarManager.cs (owns one; keeps the node
//                 mirror in sync), ExpeditionManager.cs (reads it
//                 for gates + WriteVisibleToWorld)
// See:            docs/atlas_expedition_convergence_v1.md §Step 1
//
// Scope contract: entries mirror the LOADED window (the streaming
// disc), exactly the set the old node-scraping code iterated. An
// absent coord reads Hidden — the same answer TryGetValue-miss gave
// the old gates. Unloaded ground persists through WorldTile.Discovery
// as before; the fog↔Discovery ratchet is unchanged.
// ============================================================

/// <summary>Fog-of-war state for the active expedition window, keyed by grid-local
/// axial coord. Plain data: renderer-independent, cheap to iterate, trivially
/// serializable later if mid-run persistence is ever wanted.</summary>
public class ExpeditionFogModel
{
    private readonly Dictionary<Vector2I, OverworldHex.FogState> _fog = new();

    /// <summary>Fog at a coord; Hidden when unknown/unloaded — the same answer the
    /// old node-lookup miss produced, so gate behaviour is unchanged.</summary>
    public OverworldHex.FogState FogAt(Vector2I local)
        => _fog.TryGetValue(local, out var f) ? f : OverworldHex.FogState.Hidden;

    public bool TryGet(Vector2I local, out OverworldHex.FogState fog)
        => _fog.TryGetValue(local, out fog);

    public void Set(Vector2I local, OverworldHex.FogState fog) => _fog[local] = fog;

    public void Clear() => _fog.Clear();

    /// <summary>Every tracked (coord, fog) pair — the loaded window. This is what
    /// WriteVisibleToWorld iterates now, instead of scraping scene nodes.</summary>
    public IReadOnlyDictionary<Vector2I, OverworldHex.FogState> All => _fog;
}
