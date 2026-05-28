using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// WorldMapLoader.cs
//
// Purpose:        Loads and caches WorldMapDefinition from
//                 Data/World/world_map.json. Auto-mirrors
//                 adjacency so JSON only needs one-sided links.
// Layer:          Loader
// Collaborators:  WorldMapDefinition.cs (schema),
//                 WorldMapScreen.cs (consumer)
// ============================================================

public static class WorldMapLoader
{
    private const string MAP_PATH = "res://Data/World/world_map.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    private static WorldMapDefinition _cached;

    public static WorldMapDefinition Load()
    {
        if (_cached != null) return _cached;

        if (!FileAccess.FileExists(MAP_PATH))
        {
            GD.PrintErr($"WorldMapLoader: No world map file at {MAP_PATH}");
            return BuildFallback();
        }

        try
        {
            using var file = FileAccess.Open(MAP_PATH, FileAccess.ModeFlags.Read);
            if (file == null) return BuildFallback();

            var def = JsonSerializer.Deserialize<WorldMapDefinition>(
                file.GetAsText(), JsonOptions);

            if (def == null) return BuildFallback();

            MirrorAdjacency(def);
            _cached = def;
            GD.Print($"WorldMapLoader: Loaded '{def.WorldName}' " +
                     $"({def.Nodes.Count} regions)");
            return def;
        }
        catch (Exception e)
        {
            GD.PrintErr($"WorldMapLoader: Parse error — {e.Message}");
            return BuildFallback();
        }
    }

    public static void ClearCache() => _cached = null;

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures adjacency is symmetric — if A lists B as adjacent,
    /// B also lists A, even if the JSON only declared one side.
    /// </summary>
    private static void MirrorAdjacency(WorldMapDefinition def)
    {
        var lookup = new Dictionary<string, RegionNode>();
        foreach (var node in def.Nodes)
            lookup[node.RegionId] = node;

        foreach (var node in def.Nodes)
        {
            foreach (var neighborId in node.AdjacentTo)
            {
                if (!lookup.TryGetValue(neighborId, out var neighbor)) continue;
                if (!neighbor.AdjacentTo.Contains(node.RegionId))
                    neighbor.AdjacentTo.Add(node.RegionId);
            }
        }
    }

    /// <summary>
    /// Returns a single-region fallback if the JSON file is missing.
    /// Prevents a null crash during early development.
    /// </summary>
    private static WorldMapDefinition BuildFallback()
    {
        GD.Print("WorldMapLoader: Using single-region fallback.");
        return new WorldMapDefinition
        {
            WorldName = "The Fractured Realm",
            StartingRegionId = "frontier_wilds",
            Nodes = new List<RegionNode>
            {
                new RegionNode
                {
                    RegionId         = "frontier_wilds",
                    DisplayName      = "Frontier Wilds",
                    Description      = "Dense wilderness beyond the guild's reach.",
                    Col = 0, Row = 1,
                    UnlockedByDefault = true,
                    SchoolAffinity   = "Elementalist",
                    Atmosphere       = "Hostile",
                    TerrainFlavor    = "Dense Forest",
                }
            }
        };
    }
}
