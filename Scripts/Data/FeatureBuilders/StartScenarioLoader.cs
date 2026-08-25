using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// StartScenarioLoader.cs
//
// Purpose:        Lazy loader + per-session cache for the founding
//                 start-scenario table (Data/World/start_scenarios.json).
//                 Read at most once per process. Mirrors RegionLoader's
//                 pattern and JsonOptions (camelCase, IncludeFields).
// Layer:          Loader
// Collaborators:  StartScenario.cs (schema), NewGameScreen.cs (picker),
//                 WorldDebug.cs (validator), SaveManager/CampusScreen
//                 (world-gen wiring, later phase).
// See:            docs/world_locales_and_founding_spec_v1.md §3.1
// ============================================================

/// <summary>Loads and caches the curated founding scenarios. All accessors are
/// null-tolerant: a missing or malformed file yields an empty list rather than a
/// throw, and Default() falls back to a synthesised "Standard" scenario so
/// founding never hard-blocks on data problems.</summary>
public static class StartScenarioLoader
{
    private const string PATH = "res://Data/World/start_scenarios.json";

    private static List<StartScenario> _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>All scenarios, sorted by DifficultyRank then DisplayName.
    /// Cached after the first successful load. Never null.</summary>
    public static List<StartScenario> LoadAll()
    {
        if (_cache != null)
            return _cache;

        var list = new List<StartScenario>();

        if (!FileAccess.FileExists(PATH))
        {
            GD.PrintErr($"StartScenarioLoader: No file at {PATH}");
            _cache = list;
            return _cache;
        }

        try
        {
            using var file = FileAccess.Open(PATH, FileAccess.ModeFlags.Read);
            if (file != null)
            {
                var parsed = JsonSerializer.Deserialize<StartScenarioFile>(
                    file.GetAsText(), JsonOptions);
                if (parsed?.Scenarios != null)
                {
                    foreach (var s in parsed.Scenarios)
                        if (s != null && !string.IsNullOrEmpty(s.Id))
                            list.Add(s);
                }
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"StartScenarioLoader: Error loading {PATH}: {e.Message}");
        }

        list.Sort((a, b) =>
            a.DifficultyRank != b.DifficultyRank
                ? a.DifficultyRank.CompareTo(b.DifficultyRank)
                : string.CompareOrdinal(a.DisplayName, b.DisplayName));

        GD.Print($"StartScenarioLoader: Loaded {list.Count} start scenarios.");
        _cache = list;
        return _cache;
    }

    /// <summary>Look up a scenario by id, or null if absent.</summary>
    public static StartScenario Load(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        foreach (var s in LoadAll())
            if (s.Id == id)
                return s;
        return null;
    }

    /// <summary>A safe default for founding/migration: the "Standard"-tagged
    /// scenario if present, else the lowest-rank scenario, else a synthesised
    /// neutral scenario (all levers at today's values).</summary>
    public static StartScenario Default()
    {
        var all = LoadAll();
        foreach (var s in all)
            if (s.DifficultyTag == "Standard")
                return s;
        if (all.Count > 0)
            return all[0];
        return new StartScenario { Id = "standard", DisplayName = "Standard", DifficultyTag = "Standard", DifficultyRank = 1 };
    }

    public static void ClearCache() => _cache = null;
}
