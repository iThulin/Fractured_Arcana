using System.Collections.Generic;

// ============================================================
// RegionMemorySaveData.cs
//
// Purpose:        Persistent memory for one visited region.
//                 Stores the run seed (so terrain regenerates
//                 identically on return), per-hex fog state,
//                 consumed POI coords, visit tracking, and
//                 objective status. Lives inside GuildSaveData
//                 .RegionMemory keyed by regionId.
// Layer:          Data
// Collaborators:  GuildSaveData.cs (host dictionary),
//                 RegionMemoryService.cs (read/write helper),
//                 OverworldRunManager.cs (consumer),
//                 OverworldHexGrid.cs (seed consumer)
// See:            README §6 — Save System
// ============================================================

/// <summary>
/// Full persistent state for one explored region.
/// Replaces the old lightweight <c>RegionMemorySaveData</c> stub —
/// this version stores everything needed to restore a map exactly.
/// </summary>
public class RegionMemorySaveData
{
    // ── Identity ─────────────────────────────────────────────────────────
    public string RegionId = "";

    // ── Seed — must stay fixed so terrain regenerates identically ────────
    public int RunSeed = 0;

    // ── Exploration tracking ─────────────────────────────────────────────
    public int VisitCount = 0;
    public bool ObjectiveReached = false;

    /// <summary>0–100 percentage of hexes the player has fully revealed.</summary>
    public float ExplorationPercent = 0f;

    /// <summary>
    /// Last hex the party was standing on when the run ended.
    /// Used to display progress on the world map, not to restore position
    /// (each run starts fresh from the entry coord).
    /// </summary>
    public int LastPartyQ = -1;
    public int LastPartyR = -1;

    // ── Fog state — only stores non-Hidden hexes to keep save size small ─
    /// <summary>
    /// Hexes that are Revealed or Silhouette. Hidden hexes are omitted —
    /// anything absent from this list is treated as Hidden on restore.
    /// </summary>
    public List<HexFogEntry> FogStates = new();

    // ── Consumed POIs ─────────────────────────────────────────────────────
    /// <summary>
    /// Coords of hexes whose POI has been consumed (fought, looted, etc.).
    /// POIs not in this list respawn on next visit.
    /// Combat POIs stay consumed permanently; rest/narrative can respawn.
    /// </summary>
    public List<HexCoordEntry> ConsumedPOIs = new();

    // ── Legacy compat (from old stub) — kept so old saves don't crash ────
    public List<RevealedHexData> RevealedHexes = new();
    public List<string> CompletedLandmarks = new();
    public System.Collections.Generic.Dictionary<string, string> FactionControl = new();
}

/// <summary>Compact fog entry: coord + state string.</summary>
public class HexFogEntry
{
    public int Q;
    public int R;
    /// <summary>"Revealed" or "Silhouette".</summary>
    public string State = "Revealed";
}

/// <summary>Compact coord-only entry for consumed POI list.</summary>
public class HexCoordEntry
{
    public int Q;
    public int R;
}

/// <summary>Legacy type kept for migration compatibility. Do not use in new code.</summary>
public class RevealedHexData
{
    public int Q;
    public int R;
    public string FogState = "Revealed";
}
