using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// BuildingDatabase.cs
//
// Purpose:        Loader + registry for Building templates from
//                 Data/Buildings/*.json. Merges static template
//                 data with runtime upgrade state stored in
//                 GuildSaveData.Buildings.
// Layer:          Loader
// Collaborators:  BuildingDefinition.cs (Building, BuildingTier),
//                 GuildSaveData.cs (BuildingSaveData entries),
//                 CampusScreen.cs, BuildingEffectApplier.cs
// See:            README §4.4 (Adding a Building)
// ============================================================

/// <summary>Process-wide loader and registry for campus building templates. Caches templates on first load; <see cref="EnsureBuildings"/> backfills missing entries on the save side so newly-added buildings appear at tier 0.</summary>
public static class BuildingDatabase
{
    private const string BUILDINGS_DIR = "res://Data/Buildings/";

    private static List<Building> _templates;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Load all building templates. Cached after first load.
    /// </summary>
    public static List<Building> LoadAll()
    {
        if (_templates != null) return _templates;
        _templates = new List<Building>();

        using var dir = DirAccess.Open(BUILDINGS_DIR);
        if (dir == null)
        {
            GD.PrintErr($"BuildingDatabase: Could not open {BUILDINGS_DIR}");
            return _templates;
        }

        dir.ListDirBegin();
        string filename = dir.GetNext();
        while (filename != "")
        {
            if (!dir.CurrentIsDir() && filename.EndsWith(".json"))
            {
                var building = LoadFile($"{BUILDINGS_DIR}{filename}");
                if (building != null) _templates.Add(building);
            }
            filename = dir.GetNext();
        }
        dir.ListDirEnd();

        GD.Print($"BuildingDatabase: Loaded {_templates.Count} building templates.");
        return _templates;
    }

    private static Building LoadFile(string path)
    {
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            return JsonSerializer.Deserialize<Building>(file.GetAsText(), JsonOptions);
        }
        catch (Exception e)
        {
            GD.PrintErr($"BuildingDatabase: Failed to load {path}: {e.Message}");
            return null;
        }
    }

    public static Building GetTemplate(string id)
    {
        foreach (var b in LoadAll())
            if (b.Id == id) return b;
        return null;
    }

    /// <summary>
    /// Ensure every template has a runtime entry in the save.
    /// Adds missing ones at tier 0 (not built).
    /// </summary>
    public static void EnsureBuildings(GuildSaveData save)
    {
        if (save == null) return;
        var templates = LoadAll();

        foreach (var template in templates)
        {
            bool exists = false;
            foreach (var b in save.Buildings)
                if (b.Id == template.Id) { exists = true; break; }

            if (!exists)
            {
                save.Buildings.Add(new BuildingSaveData
                {
                    Id = template.Id,
                    Name = template.Name,
                    Tier = 0,
                    Category = template.Category,
                    SchoolAffinity = template.SchoolAffinity,
                });
            }
        }
    }

    /// <summary>
    /// Get the current tier data for a building, based on save state.
    /// Returns null if not built or tier data missing.
    /// </summary>
    public static BuildingTier GetCurrentTierData(string buildingId, GuildSaveData save)
    {
        if (save == null) return null;

        int currentTier = 0;
        foreach (var b in save.Buildings)
            if (b.Id == buildingId) { currentTier = b.Tier; break; }

        if (currentTier <= 0) return null;

        var template = GetTemplate(buildingId);
        if (template == null) return null;

        foreach (var tier in template.Tiers)
            if (tier.Tier == currentTier) return tier;

        return null;
    }

    public static void ClearCache() => _templates = null;
}