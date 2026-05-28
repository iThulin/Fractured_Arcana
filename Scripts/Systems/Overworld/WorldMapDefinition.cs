using System.Collections.Generic;

// ============================================================
// WorldMapDefinition.cs
//
// Purpose:        Schema for the fixed world map — a grid of
//                 RegionNode entries that form the deployable
//                 overworld. Each node references a RegionId,
//                 holds a world-grid position, and lists which
//                 adjacent nodes it connects to. Loaded once
//                 from Data/World/world_map.json.
// Layer:          Data
// Collaborators:  WorldMapLoader.cs (JSON parser),
//                 WorldMapScreen.cs (UI consumer),
//                 RegionMemoryService.cs (per-node stats),
//                 GuildSaveData.cs (UnlockedRegionIds)
// ============================================================

/// <summary>
/// The full world map definition. One instance per game, loaded from JSON.
/// Describes the fixed geography: which regions exist, where they sit on the
/// world grid, how they connect, and what gates access to each.
/// </summary>
public class WorldMapDefinition
{
    /// <summary>Display name for the world map screen title.</summary>
    public string WorldName = "The Fractured Realm";

    /// <summary>
    /// The region the player starts in on a new guild.
    /// Must match a RegionNode.RegionId in the Nodes list.
    /// </summary>
    public string StartingRegionId = "frontier_wilds";

    /// <summary>All region nodes on the world map.</summary>
    public List<RegionNode> Nodes = new();
}

/// <summary>
/// One region tile on the world map grid.
/// </summary>
public class RegionNode
{
    // ── Identity ─────────────────────────────────────────────────────────
    /// <summary>Must match a file in Data/Regions/{regionId}.json.</summary>
    public string RegionId = "";

    /// <summary>Short display name shown on the world map tile.</summary>
    public string DisplayName = "";

    /// <summary>One-line description shown in the deployment panel.</summary>
    public string Description = "";

    // ── World grid position ───────────────────────────────────────────────
    /// <summary>Column on the world map grid (0 = leftmost).</summary>
    public int Col = 0;

    /// <summary>Row on the world map grid (0 = topmost).</summary>
    public int Row = 0;

    // ── Adjacency ────────────────────────────────────────────────────────
    /// <summary>
    /// RegionIds this node connects to. Adjacency is one-directional in
    /// the JSON (list both sides or use the loader to auto-mirror).
    /// </summary>
    public List<string> AdjacentTo = new();

    // ── Access gates ─────────────────────────────────────────────────────
    /// <summary>
    /// If true, this region is available from the start with no unlock
    /// requirements. The starting region is always unlocked.
    /// </summary>
    public bool UnlockedByDefault = false;

    /// <summary>
    /// RegionId that must have its objective completed before this region
    /// unlocks. Empty string = no requirement.
    /// </summary>
    public string RequiresRegionCleared = "";

    /// <summary>
    /// Minimum guild gold required to deploy here (represents travel cost).
    /// 0 = no cost.
    /// </summary>
    public int DeploymentCost = 0;

    // ── Flavor ───────────────────────────────────────────────────────────
    public string SchoolAffinity = "";
    public string Atmosphere = "";      // "Hostile", "Neutral", "Friendly"
    public string TerrainFlavor = "";   // "Dense Forest", "Volcanic Wastes", etc.
}
