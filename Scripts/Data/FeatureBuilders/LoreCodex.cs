using Godot;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// LoreCodex.cs. Loads the Hall of Lore's entries from
// Data/Lore/*.json (docs/lore_codex_v1.md, wire item C7). Every
// lore id the game grants has an entry: a title, a category and
// a body. QuestLogView.BuildLoreInto renders them.
// Mirrors QuestLoader (camelCase, fields, one cache). Files load
// in file-name order; each lists its categories in display order,
// and the first file to list a category places it.
// ============================================================

/// <summary>One Hall of Lore entry. Id matches an id in EternalLedger.UnlockedLoreEntries.</summary>
public sealed class LoreEntry
{
    public string Id = "";
    public string Title = "";
    public string Category = "";
    public string Body = "";
}

/// <summary>One Data/Lore file: its categories in display order, and its entries.</summary>
public sealed class LoreFile
{
    public int Version = 1;
    public List<string> Categories = new();
    public List<LoreEntry> Entries = new();
}

/// <summary>Process-wide loader for the Hall of Lore.</summary>
public static class LoreCodex
{
    private const string LORE_DIR = "res://Data/Lore/";
    private static Dictionary<string, LoreEntry> _byId;
    private static List<string> _categoryOrder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    private static void EnsureLoaded()
    {
        if (_byId != null) return;
        _byId = new Dictionary<string, LoreEntry>();
        _categoryOrder = new List<string>();

        // DirAccess.Open returns null for a missing directory, in the editor and in an export.
        using var dir = DirAccess.Open(LORE_DIR);
        if (dir == null)
        {
            GD.Print($"LoreCodex: no lore directory at {LORE_DIR}");
            return;
        }
        var files = new List<string>();
        dir.ListDirBegin();
        for (string fn = dir.GetNext(); fn != ""; fn = dir.GetNext())
        {
            if (dir.CurrentIsDir() || !fn.EndsWith(".json")) continue;
            files.Add(fn);
        }
        dir.ListDirEnd();
        files.Sort(System.StringComparer.Ordinal);

        foreach (var fn in files)
        {
            try
            {
                using var f = FileAccess.Open($"{LORE_DIR}{fn}", FileAccess.ModeFlags.Read);
                if (f == null) continue;
                var file = JsonSerializer.Deserialize<LoreFile>(f.GetAsText(), JsonOptions);
                if (file == null) continue;
                if (file.Categories != null)
                    foreach (var cat in file.Categories)
                        if (!string.IsNullOrEmpty(cat) && !_categoryOrder.Contains(cat))
                            _categoryOrder.Add(cat);
                if (file.Entries == null) continue;
                foreach (var entry in file.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                    if (_byId.ContainsKey(entry.Id))
                    {
                        GD.PrintErr($"LoreCodex: {fn} repeats the entry {entry.Id}; keeping the first.");
                        continue;
                    }
                    _byId[entry.Id] = entry;
                }
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"LoreCodex: error loading {fn}: {e.Message}");
            }
        }

        string noun = _byId.Count == 1 ? "entry" : "entries";
        GD.Print($"LoreCodex: loaded {_byId.Count} lore {noun}.");
    }

    /// <summary>The entry for <paramref name="id"/>, or null when the data has none.</summary>
    public static LoreEntry Get(string id)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(id)) return null;
        return _byId.TryGetValue(id, out var entry) ? entry : null;
    }

    /// <summary>The unlocked ids grouped for the Hall of Lore. Categories come in the order the
    /// data lists them, then any category the data does not list, in first-unlock order; each
    /// category's entries come in unlock order. Ids with no entry go to
    /// <paramref name="uncatalogued"/>, in unlock order. Repeated ids count once.</summary>
    public static List<(string Category, List<LoreEntry> Entries)> Group(
        IEnumerable<string> unlocked, out List<string> uncatalogued)
    {
        EnsureLoaded();
        uncatalogued = new List<string>();
        var byCategory = new Dictionary<string, List<LoreEntry>>();
        var unlisted = new List<string>();
        var seen = new HashSet<string>();
        if (unlocked != null)
        {
            foreach (var id in unlocked)
            {
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                if (!_byId.TryGetValue(id, out var entry))
                {
                    uncatalogued.Add(id);
                    continue;
                }
                string category = string.IsNullOrEmpty(entry.Category) ? "Other" : entry.Category;
                if (!byCategory.TryGetValue(category, out var bucket))
                {
                    bucket = new List<LoreEntry>();
                    byCategory[category] = bucket;
                    if (!_categoryOrder.Contains(category)) unlisted.Add(category);
                }
                bucket.Add(entry);
            }
        }

        var groups = new List<(string Category, List<LoreEntry> Entries)>();
        foreach (var listed in _categoryOrder)
            if (byCategory.TryGetValue(listed, out var entries))
                groups.Add((listed, entries));
        foreach (var extra in unlisted)
            groups.Add((extra, byCategory[extra]));
        return groups;
    }

    public static void ClearCache()
    {
        _byId = null;
        _categoryOrder = null;
    }
}
