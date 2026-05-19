using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// ============================================================
// SaveManager.cs
//
// Purpose:        Save / load engine for GuildSaveData. Owns the
//                 active save and active slot, writes JSON to
//                 user://saves/slot_N.json, applies migrations on
//                 load, and surfaces slot metadata for the
//                 slot-selection UI.
// Layer:          System
// Collaborators:  GuildSaveData.cs (the schema),
//                 StarterDeckLoader.cs (seeds PlayerDeck on NewGame),
//                 CompanionRoster.cs, CampusScreen.cs (callers of
//                 Load / Save / NewGame)
// See:            README §6 — Save System,
//                 README §7 — schema bump procedure
// ============================================================

/// <summary>Process-wide save / load orchestrator. Holds the active <see cref="GuildSaveData"/> in memory, persists it to <c>user://saves/slot_N.json</c>, applies schema migrations on load, and exposes slot-listing helpers for the menu UI.</summary>
public static class SaveManager
{
    private const string SAVE_DIR = "user://saves/";
    private const int MAX_SLOTS = 3;
    private const int CURRENT_VERSION = 2;

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
    /// <paramref name="school"/> is required so the starter deck can be
    /// seeded immediately — the player should have already chosen their
    /// school before calling this.
    /// </summary>
    public static GuildSaveData NewGame(int slot, string guildName = "New Guild",
                                        string school = "Elementalist")
    {
        var data = new GuildSaveData
        {
            SaveVersion    = CURRENT_VERSION,
            GuildName      = guildName,
            SelectedSchool = school,
            CreatedAt      = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            LastPlayedAt   = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // Seed the persistent deck now so the first run has a real deck
        // rather than hitting the lazy-seed fallback in DeckManager.
        // CardDatabase must be loaded before NewGame is called.
        if (Enum.TryParse<CardSchool>(school, ignoreCase: true, out var cardSchool))
            StarterDeckLoader.SeedStarterDeck(data, cardSchool);
        else
            GD.PrintErr($"SaveManager: Unknown school '{school}' — PlayerDeck not seeded.");

        ActiveSave = data;
        ActiveSlot = slot;
        SaveToSlot(slot, data);

        GD.Print($"SaveManager: New game in slot {slot} (school: {school})");
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
                info.IsEmpty   = false;
                info.GuildName = data.GuildName;
                info.School    = data.SelectedSchool;
                info.Gold      = data.Gold;
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

        if (data.SaveVersion < 2)
        {
            // v1 → v2: added PlayerDeck, ArcaneDust, UnlockedCardBlueprintIds.
            // Existing saves get an empty PlayerDeck; DeckManager.InitializeFromSave
            // detects this and seeds the starter deck on the player's next run.
            data.PlayerDeck ??= new PlayerDeckSave();
            data.UnlockedCardBlueprintIds ??= new List<string>();
            data.ArcaneDust = 0;
            data.SaveVersion = 2;
        }

        // if (data.SaveVersion < 3)
        // {
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
    public string GuildName  = "";
    public string School     = "";
    public int Gold;
    public int TotalRuns;
    public string LastPlayed = "";
}
