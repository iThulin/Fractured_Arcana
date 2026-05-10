using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Loads companion definitions from Data/Companions/*.json.
/// Each file defines one companion. Caches results.
/// </summary>
public static class CompanionLoader
{
    private const string COMPANIONS_DIR = "res://Data/Companions/";

    private static List<Companion> _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    /// <summary>
    /// Load all companion definitions. Returns the master list.
    /// These are the static templates — runtime state lives in GuildSaveData.
    /// </summary>
    public static List<Companion> LoadAll()
    {
        if (_cache != null) return _cache;
        _cache = new List<Companion>();

        using var dir = DirAccess.Open(COMPANIONS_DIR);
        if (dir == null)
        {
            GD.PrintErr($"CompanionLoader: Could not open {COMPANIONS_DIR}");
            return _cache;
        }

        dir.ListDirBegin();
        string filename = dir.GetNext();
        while (filename != "")
        {
            if (!dir.CurrentIsDir() && filename.EndsWith(".json"))
            {
                var companion = LoadFile($"{COMPANIONS_DIR}{filename}");
                if (companion != null) _cache.Add(companion);
            }
            filename = dir.GetNext();
        }
        dir.ListDirEnd();

        GD.Print($"CompanionLoader: Loaded {_cache.Count} companions.");
        return _cache;
    }

    private static Companion LoadFile(string path)
    {
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            var c = JsonSerializer.Deserialize<Companion>(file.GetAsText(), JsonOptions);
            return c;
        }
        catch (Exception e)
        {
            GD.PrintErr($"CompanionLoader: Failed to load {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Find a companion template by id.
    /// </summary>
    public static Companion GetById(string id)
    {
        foreach (var c in LoadAll())
            if (c.Id == id) return c;
        return null;
    }

    public static void ClearCache() => _cache = null;
}