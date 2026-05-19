using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// RegionLoader.cs
//
// Purpose:        Lazy loader and cache for RegionDefinition
//                 JSON files. <see cref="LoadOrDefault"/> falls
//                 back to "frontier_wilds" if the requested
//                 region is missing.
// Layer:          Loader
// Collaborators:  RegionDefinition.cs (the schema),
//                 OverworldHexGrid.cs, POIGenerator.cs,
//                 OverworldRunManager.cs (consumers)
// See:            README §4.2 (Adding a Region)
// ============================================================

/// <summary>Lazy loader + per-session cache for region JSON. Each region file is read at most once per process.</summary>
public static class RegionLoader
{
    private const string REGIONS_DIR = "res://Data/Regions/";
    private const string DEFAULT_REGION = "frontier_wilds";

    private static readonly Dictionary<string, RegionDefinition> _cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    /// <summary>
    /// Load a region by id. Returns null if not found.
    /// </summary>
    public static RegionDefinition Load(string regionId)
    {
        if (string.IsNullOrEmpty(regionId)) return null;
        if (_cache.TryGetValue(regionId, out var cached)) return cached;

        string path = $"{REGIONS_DIR}{regionId}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.PrintErr($"RegionLoader: No region file at {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var region = JsonSerializer.Deserialize<RegionDefinition>(
                file.GetAsText(), JsonOptions);

            if (region == null) return null;

            _cache[regionId] = region;
            GD.Print($"RegionLoader: Loaded '{region.DisplayName}' from {path}");
            return region;
        }
        catch (Exception e)
        {
            GD.PrintErr($"RegionLoader: Error loading {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load with fallback to the default region if not found.
    /// </summary>
    public static RegionDefinition LoadOrDefault(string regionId)
    {
        var region = Load(regionId);
        if (region != null) return region;

        GD.Print($"RegionLoader: Falling back to '{DEFAULT_REGION}'");
        return Load(DEFAULT_REGION);
    }

    public static List<RegionDefinition> LoadAll()
    {
        var results = new List<RegionDefinition>();

        using var dir = DirAccess.Open(REGIONS_DIR);
        if (dir == null)
        {
            GD.PrintErr($"RegionLoader: Could not open {REGIONS_DIR}");
            return results;
        }

        dir.ListDirBegin();
        string filename = dir.GetNext();
        while (filename != "")
        {
            if (!dir.CurrentIsDir() && filename.EndsWith(".json"))
            {
                string regionId = filename.Replace(".json", "");
                var region = Load(regionId);
                if (region != null) results.Add(region);
            }
            filename = dir.GetNext();
        }
        dir.ListDirEnd();

        GD.Print($"RegionLoader: Loaded {results.Count} regions.");
        return results;
    }

    public static void ClearCache() => _cache.Clear();
}