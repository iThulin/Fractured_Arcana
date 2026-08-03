using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
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
        GD.Print($"[BuildingDB] Loaded buildings: {string.Join(", ", _templates.Select(b => b.Id))}");
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
    /// Ensure every template has a runtime entry in the save. Two passes, and the
    /// difference between them is the whole point:
    ///
    /// <para><b>SEED</b> — runs only when a template has no entry at all. Adds it at tier 0
    /// / unplaced, or, when the template authors <see cref="Building.StartsBuiltAt"/>,
    /// already built and sited there. Once per building per save; a player who later moves
    /// or demolishes an ordinary starting building is never overridden back.</para>
    ///
    /// <para><b>REPAIR</b> — runs on EVERY load, for foundational buildings only. Floors the
    /// tier at 1, re-sites an unplaced entry at its authored anchor, and restores destroyed
    /// integrity. Foundational buildings host systems the game is unplayable without, so
    /// they must be standing at the start of every reset window no matter what the previous
    /// cycle, a siege, or an older save did to them. Seeding alone cannot promise that: the
    /// seed branch only fires when the entry is MISSING, and a destroyed or tier-0 entry is
    /// present, so it would be skipped forever.</para>
    ///
    /// <para>Repair deliberately does not move a foundational building the player has
    /// relocated (IsPlaced stays true, Q/R are left alone). It also cannot detect a
    /// collision when it re-sites — there is no grid at this layer — so a re-sited anchor
    /// that another building has since occupied resolves at stamp time in
    /// CampusGridManager.LoadFromSave, last writer winning. Pre-existing behaviour; worth
    /// tightening if relocation ever ships.</para>
    /// </summary>
    public static void EnsureBuildings(GuildSaveData save)
    {
        if (save == null) return;
        var templates = LoadAll();

        foreach (var template in templates)
        {
            bool anchored = template.StartsBuiltAt != null;

            if (template.IsFoundational && !anchored)
            {
                GD.PrintErr($"BuildingDatabase: '{template.Id}' is marked foundational but authors " +
                            "no startsBuiltAt anchor — there is nowhere to seed or repair it to. " +
                            "Treating it as an ordinary constructed building.");
            }

            var entry = save.Buildings.Find(b => b.Id == template.Id);

            // ── SEED ────────────────────────────────────────────────────────
            if (entry == null)
            {
                entry = new BuildingSaveData
                {
                    Id = template.Id,
                    Name = template.Name,
                    Tier = 0,
                    Category = template.Category,
                    SchoolAffinity = template.SchoolAffinity,
                    MaxIntegrity = 20,       // flat baseline for now — see BuildingSaveData
                    CurrentIntegrity = 20,   // and campus_siege_and_defense_v1 §4
                };

                if (anchored)
                {
                    // Pre-built, not purchased — the guild starts with this one standing.
                    entry.Tier = 1;
                    entry.Q = template.StartsBuiltAt.Q;
                    entry.R = template.StartsBuiltAt.R;
                    entry.Rotation = 0;
                    entry.IsPlaced = true;
                }

                save.Buildings.Add(entry);
                continue;   // just built it correctly — nothing to repair
            }

            // ── REPAIR ──────────────────────────────────────────────────────
            if (!template.IsFoundational || !anchored)
                continue;

            if (entry.MaxIntegrity <= 0)
                entry.MaxIntegrity = 20;

            if (entry.Tier < 1)
            {
                GD.Print($"[BuildingDB] Foundational '{template.Id}' was tier {entry.Tier} — restored to tier 1.");
                entry.Tier = 1;
            }

            if (!entry.IsPlaced)
            {
                entry.Q = template.StartsBuiltAt.Q;
                entry.R = template.StartsBuiltAt.R;
                entry.Rotation = 0;
                entry.IsPlaced = true;
                GD.Print($"[BuildingDB] Foundational '{template.Id}' was unplaced — re-sited at " +
                         $"({entry.Q}, {entry.R}).");
            }

            if (entry.CurrentIntegrity <= 0)
            {
                entry.CurrentIntegrity = entry.MaxIntegrity;
                GD.Print($"[BuildingDB] Foundational '{template.Id}' was destroyed — integrity restored.");
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