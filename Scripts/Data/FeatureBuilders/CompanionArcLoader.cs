using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// CompanionArcLoader.cs
//
// Purpose:        Loads companion arc definitions from JSON.
//                 Each companion has its own arc file at
//                 Data/Companions/Arcs/<companionId>.json.
// Layer:          Loader
// Collaborators:  CompanionArcData.cs (schema),
//                 CompanionArcTracker.cs (consumer)
// See:            quest_hooks_compendium_v1.md §3
// ============================================================

/// <summary>Loads and caches companion arc definitions from JSON files.</summary>
public static class CompanionArcLoader
{
    private const string ARCS_DIR = "res://Data/Companions/Arcs/";

    private static readonly Dictionary<string, CompanionArcData> _cache = new();
    private static bool _allLoaded = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Load a single companion's arc definition by companion id.</summary>
    public static CompanionArcData Load(string companionId)
    {
        if (string.IsNullOrEmpty(companionId)) return null;
        if (_cache.TryGetValue(companionId, out var cached)) return cached;

        string path = $"{ARCS_DIR}{companionId}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.Print($"CompanionArcLoader: no arc file at {path}");
            return null;
        }

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var arc = JsonSerializer.Deserialize<CompanionArcData>(
                file.GetAsText(), JsonOptions);

            if (arc != null)
            {
                _cache[companionId] = arc;
                GD.Print($"CompanionArcLoader: loaded arc for '{companionId}' " +
                         $"({arc.Stages?.Count ?? 0} stages)");
            }
            return arc;
        }
        catch (Exception e)
        {
            GD.PrintErr($"CompanionArcLoader: error loading {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>Load all arc definitions from the arcs directory. Returns
    /// every successfully loaded arc, keyed by companion id.</summary>
    public static Dictionary<string, CompanionArcData> LoadAll()
    {
        if (_allLoaded) return _cache;

        string dirPath = ARCS_DIR.TrimEnd('/');
        var dir = DirAccess.Open(dirPath);
        if (dir == null)
        {
            GD.Print($"CompanionArcLoader: cannot open {dirPath}");
            _allLoaded = true;
            return _cache;
        }

        dir.ListDirBegin();
        string fileName = dir.GetNext();
        while (!string.IsNullOrEmpty(fileName))
        {
            if (!dir.CurrentIsDir() && fileName.EndsWith(".json"))
            {
                string companionId = fileName.Replace(".json", "");
                if (!_cache.ContainsKey(companionId))
                    Load(companionId);
            }
            fileName = dir.GetNext();
        }
        dir.ListDirEnd();

        _allLoaded = true;
        GD.Print($"CompanionArcLoader: {_cache.Count} arc(s) loaded total.");
        return _cache;
    }

    public static void ClearCache()
    {
        _cache.Clear();
        _allLoaded = false;
    }
}
