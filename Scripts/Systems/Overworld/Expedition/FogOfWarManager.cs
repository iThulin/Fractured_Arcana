using Godot;
using System.Collections.Generic;

// ============================================================
// FogOfWarManager.cs
//
// Purpose:        Owns fog state across the overworld hex grid.
//                 Reveals tiles within vision radius of the
//                 party, marks the fringe as silhouettes, leaves
//                 the rest hidden. Persists revealed state into
//                 RegionMemorySaveData via save manager.
// Layer:          System
// Collaborators:  OverworldHexGrid.cs (parent),
//                 OverworldHex.cs (FogState target),
//                 GuildSaveData.cs (RegionMemory persistence)
// See:            README §6 — Fog of War
// ============================================================

/// <summary>Manages fog-of-war state for the overworld hex grid. Phase 1 implementation is a simple radius reveal — vision range + 1 row of silhouettes. Phase 2+ will add intel-based long-range reveals and per-school abilities.</summary>
public partial class FogOfWarManager : Node2D
{
    [Export] public int BaseVisionRadius = 1;

    private OverworldHexGrid _grid;

    public override void _Ready()
    {
        _grid = GetParent<OverworldHexGrid>();
        if (_grid == null)
            GD.PrintErr("FogOfWarManager: must be a child of OverworldHexGrid");
    }

    /// <summary>
    /// Call this whenever the party moves. Reveals hexes within vision radius
    /// and sets silhouettes on the fringe.
    /// </summary>
    public void UpdateVision(Vector2I partyCoord, int bonusRadius = 0)
    {
        int revealRange = BaseVisionRadius + bonusRadius;
        int silhouetteRange = revealRange + 1;

        foreach (var kvp in _grid.Hexes)
        {
            var coord = kvp.Key;
            var hex = kvp.Value;
            int dist = _grid.Distance(partyCoord, coord);

            if (dist <= revealRange)
            {
                // Full reveal — terrain, POIs, everything visible
                hex.Fog = OverworldHex.FogState.Revealed;
            }
            else if (dist <= silhouetteRange && hex.Fog == OverworldHex.FogState.Hidden)
            {
                // Silhouette — can see terrain shape but not POI content
                hex.Fog = OverworldHex.FogState.Silhouette;
            }
            // Note: already-revealed hexes stay revealed (no re-fogging)

            hex.RefreshVisuals();
        }
    }

    /// <summary>
    /// Make the objective landmark always visible through fog (as a silhouette).
    /// Per the design doc: "its general direction is always known."
    /// </summary>
    // ── Secondary-landmark lures (discovery_loop_spec_v1 Layer C) ────────
    private const int MaxSecondaryLandmarks = 3;
    /// <summary>A landmark must be at least this far from the entry to read as a
    /// distant horizon rather than something already underfoot.</summary>
    private const int LandmarkMinDistanceFromStart = 5;
    /// <summary>Chosen landmarks are kept at least this far apart so the frontier
    /// choices point in genuinely different directions.</summary>
    private const int LandmarkSpread = 3;

    public void RevealLandmarks()
    {
        // Objective — general direction always known (silhouette).
        var objCoord = _grid.ObjectiveCoord;
        if (_grid.Hexes.TryGetValue(objCoord, out var objHex))
        {
            if (objHex.Fog == OverworldHex.FogState.Hidden)
                objHex.Fog = OverworldHex.FogState.Silhouette;
            objHex.RefreshVisuals();
        }

        RevealSecondaryLandmarks(objCoord);
    }

    /// <summary>Force-reveal 2-3 distant, kind-varied POIs through the fog as
    /// frontier lures — so the player chooses which horizon to push under
    /// step-budget pressure, not just walks to the exit. Revealed (not
    /// silhouette) so each advertises its flavour: a court reads as Negotiation,
    /// a cache as Combat, a Narrative anomaly shows its signal on hover.
    /// UpdateVision never re-hides a Revealed tile, so these persist for the run.</summary>
    private void RevealSecondaryLandmarks(Vector2I objCoord)
    {
        if (MaxSecondaryLandmarks <= 0) return;
        var start = _grid.EntryCoord;

        var candidates = new List<KeyValuePair<Vector2I, OverworldHex>>();
        foreach (var kvp in _grid.Hexes)
        {
            var hex = kvp.Value;
            if (hex.POI == OverworldHex.POIType.None || hex.POIConsumed) continue;
            if (kvp.Key == objCoord || kvp.Key == start) continue;
            if (hex.Fog == OverworldHex.FogState.Revealed) continue;   // already in sight
            if (_grid.Distance(start, kvp.Key) < LandmarkMinDistanceFromStart) continue;
            candidates.Add(kvp);
        }
        if (candidates.Count == 0) return;

        // Farthest-first: favour the true horizon.
        candidates.Sort((a, b) =>
            _grid.Distance(start, b.Key).CompareTo(_grid.Distance(start, a.Key)));

        var chosen = new List<KeyValuePair<Vector2I, OverworldHex>>();
        var chosenKeys = new HashSet<Vector2I>();
        var usedKinds = new HashSet<OverworldHex.POIType>();

        void Take(KeyValuePair<Vector2I, OverworldHex> kvp)
        { chosen.Add(kvp); chosenKeys.Add(kvp.Key); }

        // Pass 1: one landmark per distinct POI kind — maximises frontier flavour.
        foreach (var kvp in candidates)
        {
            if (chosen.Count >= MaxSecondaryLandmarks) break;
            if (usedKinds.Add(kvp.Value.POI)) Take(kvp);
        }
        // Pass 2: fill remaining slots, keeping landmarks spread apart.
        foreach (var kvp in candidates)
        {
            if (chosen.Count >= MaxSecondaryLandmarks) break;
            if (chosenKeys.Contains(kvp.Key)) continue;
            bool tooClose = false;
            foreach (var c in chosen)
                if (_grid.Distance(c.Key, kvp.Key) < LandmarkSpread) { tooClose = true; break; }
            if (!tooClose) Take(kvp);
        }

        foreach (var kvp in chosen)
        {
            kvp.Value.Fog = OverworldHex.FogState.Revealed;
            kvp.Value.IsLandmark = true;
            kvp.Value.RefreshVisuals();
        }
        GD.Print($"[Fog] Revealed {chosen.Count} secondary landmark(s) as frontier lures " +
                 $"(from {candidates.Count} candidate POI(s) in the opening window).");
    }

    /// <summary>
    /// Reveal a specific hex fully (used by intel systems in Phase 2+).
    /// </summary>
    public void RevealHex(Vector2I coord)
    {
        if (_grid.Hexes.TryGetValue(coord, out var hex))
        {
            hex.Fog = OverworldHex.FogState.Revealed;
            hex.RefreshVisuals();
        }
    }
}