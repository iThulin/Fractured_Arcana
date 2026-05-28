using Godot;
using System.Collections.Generic;

// ============================================================
// RegionMemoryService.cs
//
// Purpose:        Static helper that reads and writes region
//                 memory between OverworldRunManager and
//                 GuildSaveData. Keeps all persistence logic
//                 out of the run manager itself.
// Layer:          System
// Collaborators:  RegionMemorySaveData.cs (data schema),
//                 GuildSaveData.cs / SaveManager.cs (storage),
//                 OverworldHexGrid.cs (hex + fog state source),
//                 OverworldRunManager.cs (caller)
// ============================================================

/// <summary>
/// Stateless service for persisting and restoring per-region map state.
/// Call <see cref="HasMemory"/> to check if a region has been visited,
/// <see cref="Restore"/> to apply saved state to a freshly generated grid,
/// and <see cref="Save"/> to write current grid state back to the save.
/// </summary>
public static class RegionMemoryService
{
    // ════════════════════════════════════════════════════════════════════
    // Query
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Returns true if the player has previously visited this region.</summary>
    public static bool HasMemory(string regionId)
    {
        var save = SaveManager.ActiveSave;
        return save != null && save.RegionMemory.ContainsKey(regionId);
    }

    /// <summary>
    /// Returns the saved seed for a region, or 0 if no memory exists.
    /// A non-zero seed means the grid must be generated with this exact
    /// value so terrain regenerates identically.
    /// </summary>
    public static int GetSavedSeed(string regionId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return 0;
        return save.RegionMemory.TryGetValue(regionId, out var mem) ? mem.RunSeed : 0;
    }

    // ════════════════════════════════════════════════════════════════════
    // Restore
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies saved fog and POI-consumed state to an already-generated grid.
    /// Call this after grid generation completes, before placing the party.
    /// Returns false if no memory exists (fresh visit — nothing to restore).
    /// </summary>
    public static bool Restore(string regionId, OverworldHexGrid grid)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || !save.RegionMemory.TryGetValue(regionId, out var mem))
            return false;

        // ── Fog state ──────────────────────────────────────────────────
        foreach (var entry in mem.FogStates)
        {
            var coord = new Godot.Vector2I(entry.Q, entry.R);
            if (!grid.Hexes.TryGetValue(coord, out var hex)) continue;

            hex.Fog = entry.State == "Silhouette"
                ? OverworldHex.FogState.Silhouette
                : OverworldHex.FogState.Revealed;
        }

        // ── Consumed POIs ───────────────────────────────────────────────
        foreach (var entry in mem.ConsumedPOIs)
        {
            var coord = new Godot.Vector2I(entry.Q, entry.R);
            if (grid.Hexes.TryGetValue(coord, out var hex))
                hex.POIConsumed = true;
        }

        // Refresh visuals after bulk state changes
        foreach (var hex in grid.Hexes.Values)
            hex.RefreshVisuals();

        GD.Print($"[RegionMemory] Restored '{regionId}': " +
                 $"{mem.FogStates.Count} fog entries, " +
                 $"{mem.ConsumedPOIs.Count} consumed POIs " +
                 $"(visit #{mem.VisitCount})");
        return true;
    }

    // ════════════════════════════════════════════════════════════════════
    // Save
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes current grid state into GuildSaveData.RegionMemory.
    /// Call at the end of every run (win or lose).
    /// </summary>
    public static void Save(
        string regionId,
        OverworldHexGrid grid,
        Godot.Vector2I partyCoord,
        bool objectiveReached)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return;

        // Get or create memory entry
        if (!save.RegionMemory.TryGetValue(regionId, out var mem))
        {
            mem = new RegionMemorySaveData { RegionId = regionId };
            save.RegionMemory[regionId] = mem;
        }

        // Seed — set once on first visit, never changed (terrain must stay identical)
        if (mem.RunSeed == 0)
            mem.RunSeed = grid.Seed;

        // Visit tracking
        mem.VisitCount++;
        if (objectiveReached)
            mem.ObjectiveReached = true;

        // Last party position (for world map display only)
        mem.LastPartyQ = partyCoord.X;
        mem.LastPartyR = partyCoord.Y;

        // ── Fog states — only store non-Hidden ────────────────────────
        mem.FogStates.Clear();
        int totalHexes = 0;
        int revealedCount = 0;
        foreach (var kvp in grid.Hexes)
        {
            totalHexes++;
            if (kvp.Value.Fog == OverworldHex.FogState.Hidden) continue;
            revealedCount++;
            mem.FogStates.Add(new HexFogEntry
            {
                Q = kvp.Key.X,
                R = kvp.Key.Y,
                State = kvp.Value.Fog == OverworldHex.FogState.Silhouette
                    ? "Silhouette"
                    : "Revealed",
            });
        }

        mem.ExplorationPercent = totalHexes > 0
            ? (float)revealedCount / totalHexes * 100f
            : 0f;

        // ── Consumed POIs ─────────────────────────────────────────────
        // Merge: keep previously consumed entries, add new ones.
        // (Don't clear — a POI consumed on visit 1 stays consumed on visit 2.)
        var existingConsumed = new System.Collections.Generic.HashSet<(int, int)>();
        foreach (var e in mem.ConsumedPOIs)
            existingConsumed.Add((e.Q, e.R));

        foreach (var kvp in grid.Hexes)
        {
            if (!kvp.Value.POIConsumed) continue;
            if (existingConsumed.Contains((kvp.Key.X, kvp.Key.Y))) continue;
            mem.ConsumedPOIs.Add(new HexCoordEntry { Q = kvp.Key.X, R = kvp.Key.Y });
        }

        GD.Print($"[RegionMemory] Saved '{regionId}': " +
                 $"{mem.FogStates.Count} fog entries, " +
                 $"{mem.ConsumedPOIs.Count} consumed POIs, " +
                 $"explored {mem.ExplorationPercent:F0}%");
    }

    // ════════════════════════════════════════════════════════════════════
    // World map helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns display-ready stats for a region tile on the world map.
    /// Safe to call even if the region has never been visited.
    /// </summary>
    public static (int visits, float explored, bool objectiveReached) GetStats(string regionId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || !save.RegionMemory.TryGetValue(regionId, out var mem))
            return (0, 0f, false);
        return (mem.VisitCount, mem.ExplorationPercent, mem.ObjectiveReached);
    }
}
