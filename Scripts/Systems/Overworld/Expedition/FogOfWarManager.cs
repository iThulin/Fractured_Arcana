using Godot;
using System.Collections.Generic;

// ============================================================
// FogOfWarManager.cs
//
// Purpose:        Owns fog state across the overworld hex grid.
//                 Reveals tiles within vision radius of the
//                 party, marks the fringe as silhouettes, leaves
//                 the rest hidden.
//
//                 STEP 1 (atlas/expedition convergence): fog
//                 authority is now the ExpeditionFogModel (plain
//                 data). Every write lands in the model FIRST and
//                 is mirrored onto OverworldHex.Fog for display;
//                 every read this class makes goes through the
//                 model (with a node fallback for hexes streamed
//                 in since the last sync). Gameplay callers use
//                 FogAt/SetFog instead of touching hex.Fog.
// Layer:          System
// Collaborators:  ExpeditionFogModel.cs (the authority),
//                 OverworldHexGrid.cs (parent; loaded-set topology),
//                 OverworldHex.cs (display mirror),
//                 ExpeditionManager.cs (gates + world write-back)
// See:            README §6 — Fog of War;
//                 docs/atlas_expedition_convergence_v1.md §Step 1
// ============================================================

/// <summary>Manages fog-of-war state for the overworld hex grid. Phase 1 implementation is a simple radius reveal — vision range + 1 row of silhouettes. Phase 2+ will add intel-based long-range reveals and per-school abilities.</summary>
public partial class FogOfWarManager : Node2D
{
    [Export] public int BaseVisionRadius = 1;

    private OverworldHexGrid _grid;

    /// <summary>The run's fog as plain data — the authority. The 2D hexes mirror it.</summary>
    public ExpeditionFogModel Model { get; } = new();

    /// <summary>Step 2: the window overlay model, injected by ExpeditionManager so
    /// the landmark-lure scan reads POI data instead of node properties. Null in
    /// isolation (falls back to node reads — same belt-and-braces as EffectiveFog).</summary>
    public WindowOverlayModel Overlay;

    public override void _Ready()
    {
        _grid = GetParent<OverworldHexGrid>();
        if (_grid == null)
            GD.PrintErr("FogOfWarManager: must be a child of OverworldHexGrid");
    }

    // ── The seam gameplay talks through ──────────────────────────────────

    /// <summary>Fog at a grid-local coord, from the model. Hidden for unloaded
    /// ground — the same answer the old node-lookup miss produced.</summary>
    public OverworldHex.FogState FogAt(Vector2I coord) => Model.FogAt(coord);

    /// <summary>Set fog on a LOADED hex: model first, node mirror + redraw second.
    /// No-op for unloaded coords, matching every pre-Step-1 write pattern (all of
    /// which guarded on Hexes.TryGetValue). Unloaded ground persists through
    /// WorldTile.Discovery, unchanged.</summary>
    public void SetFog(Vector2I coord, OverworldHex.FogState state)
    {
        if (_grid == null || !_grid.Hexes.TryGetValue(coord, out var hex))
            return;
        Model.Set(coord, state);
        hex.Fog = state;
        hex.RefreshVisuals();
    }

    /// <summary>Rebuild the model to mirror the loaded window. Called after the
    /// window is built and after every StreamTo slide: streamed-in hexes arrive
    /// carrying FogFromDiscovery (WorldWindowBuilder), streamed-out coords drop.
    /// Node→model here is lossless because every mid-run write goes through
    /// SetFog, which keeps the two in lockstep.</summary>
    public void SyncFromWindow()
    {
        if (_grid == null)
            return;
        Model.Clear();
        foreach (var kvp in _grid.Hexes)
            Model.Set(kvp.Key, kvp.Value.Fog);
    }

    /// <summary>Model value, falling back to the node for a hex streamed in since
    /// the last sync — belt-and-braces so a missed sync site degrades to the old
    /// behaviour instead of treating known ground as Hidden.</summary>
    private OverworldHex.FogState EffectiveFog(Vector2I coord, OverworldHex hex)
        => Model.TryGet(coord, out var f) ? f : hex.Fog;

    /// <summary>Overlay value with node fallback — Step 2's twin of EffectiveFog.</summary>
    private TileOverlay EffectiveOverlay(Vector2I coord, OverworldHex hex)
        => Overlay != null && Overlay.TryGet(coord, out var o)
            ? o
            : new TileOverlay
            {
                Poi = hex.POI, Consumed = hex.POIConsumed,
                Objective = hex.IsObjective, Landmark = hex.IsLandmark,
                Contested = hex.Contested,
            };

    // ── Vision ───────────────────────────────────────────────────────────

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

            var fog = EffectiveFog(coord, hex);
            if (dist <= revealRange)
            {
                // Full reveal — terrain, POIs, everything visible
                fog = OverworldHex.FogState.Revealed;
            }
            else if (dist <= silhouetteRange && fog == OverworldHex.FogState.Hidden)
            {
                // Silhouette — can see terrain shape but not POI content
                fog = OverworldHex.FogState.Silhouette;
            }
            // Note: already-revealed hexes stay revealed (no re-fogging)

            Model.Set(coord, fog);
            hex.Fog = fog;
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
            if (EffectiveFog(objCoord, objHex) == OverworldHex.FogState.Hidden)
                SetFog(objCoord, OverworldHex.FogState.Silhouette);
            else
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
        // (No MaxSecondaryLandmarks<=0 guard: the const is 3, so the check is
        // dead code — CS0162. The Pass-1 `chosen.Count >= Max` break already
        // no-ops any non-positive value identically.)
        var start = _grid.EntryCoord;

        var candidates = new List<KeyValuePair<Vector2I, OverworldHex>>();
        foreach (var kvp in _grid.Hexes)
        {
            var hex = kvp.Value;
            // Step 2: candidate POIs read the overlay model, not node properties.
            var ov = EffectiveOverlay(kvp.Key, hex);
            if (ov.Poi == OverworldHex.POIType.None || ov.Consumed) continue;
            // Supply caches are earned knowledge (supply_cache spec v1.1) — a
            // free force-reveal at window-open would leak them as lures.
            if (ov.Poi == OverworldHex.POIType.SupplyCache) continue;
            if (kvp.Key == objCoord || kvp.Key == start) continue;
            if (EffectiveFog(kvp.Key, hex) == OverworldHex.FogState.Revealed) continue;   // already in sight
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
            if (usedKinds.Add(EffectiveOverlay(kvp.Key, kvp.Value).Poi)) Take(kvp);
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
            // Step 2: landmark is overlay data; node mirrored alongside. Mirror
            // before SetFog so its redraw picks the beacon styling up.
            if (Overlay != null)
            {
                var chosenOv = EffectiveOverlay(kvp.Key, kvp.Value);
                chosenOv.Landmark = true;
                Overlay.Set(kvp.Key, chosenOv);
            }
            kvp.Value.IsLandmark = true;
            SetFog(kvp.Key, OverworldHex.FogState.Revealed);
        }
        GD.Print($"[Fog] Revealed {chosen.Count} secondary landmark(s) as frontier lures " +
                 $"(from {candidates.Count} candidate POI(s) in the opening window).");
    }

    /// <summary>
    /// Reveal a specific hex fully (used by intel systems in Phase 2+).
    /// </summary>
    public void RevealHex(Vector2I coord)
        => SetFog(coord, OverworldHex.FogState.Revealed);
}
