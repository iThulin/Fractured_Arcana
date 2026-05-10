using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Manages save/load for GuildSaveData.
/// Saves as JSON to user://saves/slot_0.json, slot_1.json, slot_2.json.
/// Handles schema versioning and migration.
/// </summary>
public static class SaveManager
{
    private const string SAVE_DIR = "user://saves/";
    private const int MAX_SLOTS = 3;
    private const int CURRENT_VERSION = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    // ── The active save (loaded into memory) ────────────────────────────
    public static GuildSaveData ActiveSave { get; private set; }
    public static int ActiveSlot { get; private set; } = -1;

    // ═══════════════════════════════════════════════════════════════════════
    // Save
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Save the active data to the active slot.
    /// Call this after every run completion and campus change.
    /// </summary>
    public static bool Save()
    {
        if (ActiveSave == null || ActiveSlot < 0)
        {
            GD.PrintErr("SaveManager: No active save to write.");
            return false;
        }

        ActiveSave.LastPlayedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return SaveToSlot(ActiveSlot, ActiveSave);
    }

    public static bool SaveToSlot(int slot, GuildSaveData data)
    {
        if (slot < 0 || slot >= MAX_SLOTS)
        {
            GD.PrintErr($"SaveManager: Invalid slot {slot}");
            return false;
        }

        EnsureSaveDirectory();

        string path = GetSlotPath(slot);
        string json = JsonSerializer.Serialize(data, JsonOptions);

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PrintErr($"SaveManager: Could not open {path} for writing. Error: {FileAccess.GetOpenError()}");
                return false;
            }
            file.StoreString(json);
            GD.Print($"SaveManager: Saved to slot {slot} ({json.Length} chars)");
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Write failed: {e.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Load
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load a save slot into ActiveSave. Returns true if successful.
    /// </summary>
    public static bool Load(int slot)
    {
        var data = LoadFromSlot(slot);
        if (data == null) return false;

        ActiveSave = data;
        ActiveSlot = slot;
        GD.Print($"SaveManager: Loaded slot {slot} (v{data.SaveVersion}, guild: {data.GuildName})");
        return true;
    }

    public static GuildSaveData LoadFromSlot(int slot)
    {
        if (slot < 0 || slot >= MAX_SLOTS) return null;

        string path = GetSlotPath(slot);
        if (!FileAccess.FileExists(path)) return null;

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return null;

            string json = file.GetAsText();
            var data = JsonSerializer.Deserialize<GuildSaveData>(json, JsonOptions);

            if (data == null) return null;

            // Run migrations if needed
            data = MigrateIfNeeded(data);
            return data;
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Load failed for slot {slot}: {e.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // New game
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a fresh save in the given slot and make it active.
    /// </summary>
    public static GuildSaveData NewGame(int slot, string guildName = "New Guild")
    {
        var data = new GuildSaveData
        {
            SaveVersion = CURRENT_VERSION,
            GuildName = guildName,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            LastPlayedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        ActiveSave = data;
        ActiveSlot = slot;
        SaveToSlot(slot, data);

        GD.Print($"SaveManager: New game in slot {slot}");
        return data;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Slot info (for the slot selection UI)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get summary info for all save slots. Used by the campus/menu UI.
    /// </summary>
    public static List<SlotInfo> GetAllSlotInfo()
    {
        var slots = new List<SlotInfo>();

        for (int i = 0; i < MAX_SLOTS; i++)
        {
            var info = new SlotInfo { Slot = i, IsEmpty = true };

            var data = LoadFromSlot(i);
            if (data != null)
            {
                info.IsEmpty = false;
                info.GuildName = data.GuildName;
                info.School = data.SelectedSchool;
                info.Gold = data.Gold;
                info.TotalRuns = data.TotalRuns;
                info.LastPlayed = data.LastPlayedAt;
            }

            slots.Add(info);
        }

        return slots;
    }

    /// <summary>
    /// Delete a save slot.
    /// </summary>
    public static void DeleteSlot(int slot)
    {
        string path = GetSlotPath(slot);
        if (FileAccess.FileExists(path))
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
            GD.Print($"SaveManager: Deleted slot {slot}");
        }

        if (ActiveSlot == slot)
        {
            ActiveSave = null;
            ActiveSlot = -1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Migration
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Migrate old save versions to the current schema.
    /// Add new cases here as the schema evolves.
    /// </summary>
    private static GuildSaveData MigrateIfNeeded(GuildSaveData data)
    {
        if (data.SaveVersion == CURRENT_VERSION)
            return data;

        GD.Print($"SaveManager: Migrating save from v{data.SaveVersion} to v{CURRENT_VERSION}");

        // Example for future use:
        // if (data.SaveVersion < 2)
        // {
        //     // v1 → v2: added CharterAlignment field
        //     data.CharterAlignment = "";
        //     data.SaveVersion = 2;
        // }
        //
        // if (data.SaveVersion < 3)
        // {
        //     // v2 → v3: renamed field, added new list
        //     data.SaveVersion = 3;
        // }

        data.SaveVersion = CURRENT_VERSION;
        return data;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static string GetSlotPath(int slot) => $"{SAVE_DIR}slot_{slot}.json";

    private static void EnsureSaveDirectory()
    {
        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(SAVE_DIR)))
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(SAVE_DIR));
        }
    }
}

/// <summary>
/// Summary info for displaying save slots in the UI.
/// </summary>
public class SlotInfo
{
    public int Slot;
    public bool IsEmpty;
    public string GuildName = "";
    public string School = "";
    public int Gold;
    public int TotalRuns;
    public string LastPlayed = "";
}