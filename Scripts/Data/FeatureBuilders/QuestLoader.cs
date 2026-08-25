using Godot;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// QuestLoader.cs. Loads QuestDefinitions from Data/Quests/*.json.
// Mirrors ItemDatabase/NarrativeEncounterLoader (camelCase, fields).
// ============================================================

/// <summary>Process-wide loader for quest definitions.</summary>
public static class QuestLoader
{
    private const string QUESTS_DIR = "res://Data/Quests/";
    private static List<QuestDefinition> _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    public static List<QuestDefinition> LoadAll()
    {
        if (_cache != null) return _cache;
        _cache = new List<QuestDefinition>();

        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(QUESTS_DIR)))
        {
            GD.Print($"QuestLoader: no quests directory at {QUESTS_DIR}");
            return _cache;
        }
        using var dir = DirAccess.Open(QUESTS_DIR);
        if (dir == null) return _cache;

        dir.ListDirBegin();
        for (string fn = dir.GetNext(); fn != ""; fn = dir.GetNext())
        {
            if (dir.CurrentIsDir() || !fn.EndsWith(".json")) continue;
            try
            {
                using var f = FileAccess.Open($"{QUESTS_DIR}{fn}", FileAccess.ModeFlags.Read);
                if (f == null) continue;
                var list = JsonSerializer.Deserialize<List<QuestDefinition>>(f.GetAsText(), JsonOptions);
                if (list != null) _cache.AddRange(list);
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"QuestLoader: error loading {fn}: {e.Message}");
            }
        }
        dir.ListDirEnd();

        GD.Print($"QuestLoader: loaded {_cache.Count} quest(s).");
        return _cache;
    }

    public static void ClearCache() => _cache = null;
}
