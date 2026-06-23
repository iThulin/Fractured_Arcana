using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// ============================================================
// SaveManager.cs
//
// Purpose:        Save / load engine for the three-tier schema.
//                 Owns the active save (the in-memory envelope),
//                 writes TWO files per slot:
//                   slot_N_ledger.json — EternalLedger (tier 3,
//                     atomic write with .bak protection; the only
//                     permanent-loss vector in the game)
//                   slot_N_cycle.json  — CycleState (tier 2,
//                     replaced wholesale at cycle reset)
//                 v100 is a clean break: legacy slot_N.json saves
//                 are not migrated and are ignored (and removed
//                 by DeleteSlot).
// Layer:          System
// Collaborators:  GuildSaveData.cs (envelope + shims),
//                 EternalLedger.cs, CycleState.cs (the tiers),
//                 StarterDeckLoader.cs (seeds PlayerDeck),
//                 CompanionRoster.cs, CampusScreen.cs (callers)
// See:            open_world_refactor_v1.docx §10 — Save Schema
// ============================================================

/// <summary>
/// Process-wide save / load orchestrator for the three-tier schema.
/// Holds the active <see cref="GuildSaveData"/> envelope in memory and
/// persists its two halves to separate files per slot.
/// </summary>
public static class SaveManager
{
    private const string SAVE_DIR = "user://saves/";
    private const int MAX_SLOTS = 3;

    /// <summary>
    /// Schema version for BOTH tier files. v100 marks the three-tier era;
    /// anything older is a legacy save and is rejected, not migrated.
    /// Referenced by CycleState and EternalLedger field initializers.
    /// </summary>
    public const int CURRENT_VERSION = 100;

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
    private static bool _isDirty = false;

    public static void MarkDirty() => _isDirty = true;

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
        _isDirty = false;

        ActiveSave.Ledger.LastPlayedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return SaveToSlot(ActiveSlot, ActiveSave);
    }

    public static void SaveIfDirty()
    {
        if (_isDirty)
            Save();
    }

    /// <summary>
    /// Write both tier files for a slot. The ledger is written atomically
    /// with a .bak of the previous version; the cycle file is written via
    /// temp-and-rename (no .bak — a lost cycle is recoverable by design).
    /// </summary>
    public static bool SaveToSlot(int slot, GuildSaveData data)
    {
        if (slot < 0 || slot >= MAX_SLOTS)
        {
            GD.PrintErr($"SaveManager: Invalid slot {slot}");
            return false;
        }

        EnsureSaveDirectory();

        // Keep both files stamped with the same version.
        data.Ledger.SaveVersion = CURRENT_VERSION;
        data.Cycle.SaveVersion = CURRENT_VERSION;

        string ledgerJson = JsonSerializer.Serialize(data.Ledger, JsonOptions);
        string cycleJson = JsonSerializer.Serialize(data.Cycle, JsonOptions);

        bool ledgerOk = WriteFileSafe(GetLedgerPath(slot), ledgerJson, keepBackup: true,
                                      verify: VerifyLedgerJson);
        bool cycleOk = WriteFileSafe(GetCyclePath(slot), cycleJson, keepBackup: false,
                                     verify: VerifyCycleJson);

        if (ledgerOk && cycleOk)
            GD.Print($"SaveManager: Saved slot {slot} " +
                     $"(ledger {ledgerJson.Length} chars, cycle {cycleJson.Length} chars)");
        else
            GD.PrintErr($"SaveManager: Save to slot {slot} incomplete " +
                        $"(ledger={ledgerOk}, cycle={cycleOk})");

        return ledgerOk && cycleOk;
    }

    /// <summary>
    /// Temp-write → verify → swap. If keepBackup, the previous file is
    /// preserved as {path}.bak before the swap. Returns true on success;
    /// on any failure the existing file is left untouched.
    /// </summary>
    private static bool WriteFileSafe(string path, string contents, bool keepBackup,
                                      Func<string, bool> verify)
    {
        string tmpPath = path + ".tmp";
        string bakPath = path + ".bak";

        // 1) Write the temp file.
        try
        {
            using var file = FileAccess.Open(tmpPath, FileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PrintErr($"SaveManager: Could not open {tmpPath} for writing. " +
                            $"Error: {FileAccess.GetOpenError()}");
                return false;
            }
            file.StoreString(contents);
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Temp write failed for {path}: {e.Message}");
            return false;
        }

        // 2) Read the temp file back and verify it parses.
        try
        {
            using var check = FileAccess.Open(tmpPath, FileAccess.ModeFlags.Read);
            if (check == null || !verify(check.GetAsText()))
            {
                GD.PrintErr($"SaveManager: Verification failed for {tmpPath} — " +
                            "existing file left untouched.");
                return false;
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Verification failed for {tmpPath}: {e.Message}");
            return false;
        }

        // 3) Swap: existing → .bak (or removed), tmp → final.
        string gPath = ProjectSettings.GlobalizePath(path);
        string gTmp = ProjectSettings.GlobalizePath(tmpPath);
        string gBak = ProjectSettings.GlobalizePath(bakPath);

        if (FileAccess.FileExists(path))
        {
            if (keepBackup)
            {
                if (FileAccess.FileExists(bakPath))
                    DirAccess.RemoveAbsolute(gBak);
                if (DirAccess.RenameAbsolute(gPath, gBak) != Error.Ok)
                {
                    GD.PrintErr($"SaveManager: Could not back up {path} — aborting swap.");
                    return false;
                }
            }
            else
            {
                DirAccess.RemoveAbsolute(gPath);
            }
        }

        if (DirAccess.RenameAbsolute(gTmp, gPath) != Error.Ok)
        {
            GD.PrintErr($"SaveManager: Final rename failed for {path}.");
            // Best effort: restore the backup so the slot isn't left empty.
            if (keepBackup && FileAccess.FileExists(bakPath))
                DirAccess.RenameAbsolute(gBak, gPath);
            return false;
        }

        return true;
    }

    private static bool VerifyLedgerJson(string json)
    {
        try { return JsonSerializer.Deserialize<EternalLedger>(json, JsonOptions) != null; }
        catch { return false; }
    }

    private static bool VerifyCycleJson(string json)
    {
        try { return JsonSerializer.Deserialize<CycleState>(json, JsonOptions) != null; }
        catch { return false; }
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
        if (data == null)
            return false;

        ActiveSave = data;
        ActiveSlot = slot;
        GD.Print($"SaveManager: Loaded slot {slot} " +
                 $"(v{data.Ledger.SaveVersion}, guild: {data.Ledger.GuildName}, " +
                 $"cycle {data.Cycle.CycleNumber})");
        return true;
    }

    /// <summary>
    /// Assemble a GuildSaveData envelope from a slot's two files.
    /// The ledger is required (with .bak fallback). A missing cycle file
    /// is a legitimate between-cycles state — a fresh CycleState is
    /// created (school selection happens at cycle start, not here).
    /// </summary>
    public static GuildSaveData LoadFromSlot(int slot)
    {
        if (slot < 0 || slot >= MAX_SLOTS)
            return null;

        // ── Tier 3: the ledger (required) ───────────────────────────────
        var ledger = ReadJson<EternalLedger>(GetLedgerPath(slot));
        if (ledger == null)
        {
            string bak = GetLedgerPath(slot) + ".bak";
            ledger = ReadJson<EternalLedger>(bak);
            if (ledger != null)
                GD.PrintErr($"SaveManager: Ledger for slot {slot} was unreadable — " +
                            "RECOVERED FROM BACKUP. Last session's ledger changes may be lost.");
        }

        if (ledger == null)
            return null; // empty slot (or pre-v100 legacy — ignored by design)

        if (ledger.SaveVersion != CURRENT_VERSION)
        {
            GD.PrintErr($"SaveManager: Slot {slot} ledger is v{ledger.SaveVersion}, " +
                        $"expected v{CURRENT_VERSION}. Incompatible save — not loaded.");
            return null;
        }

        // ── Tier 2: the cycle (optional — between-cycles is valid) ──────
        var cycle = ReadJson<CycleState>(GetCyclePath(slot));
        if (cycle == null)
        {
            GD.Print($"SaveManager: Slot {slot} has no cycle file — between cycles. " +
                     "Creating a fresh CycleState (school unselected).");
            cycle = new CycleState { CycleNumber = ledger.LoopHistory.Count + 1 };
        }
        else if (cycle.SaveVersion != CURRENT_VERSION)
        {
            GD.PrintErr($"SaveManager: Slot {slot} cycle is v{cycle.SaveVersion}, " +
                        $"expected v{CURRENT_VERSION}. Incompatible save — not loaded.");
            return null;
        }

        return new GuildSaveData { Ledger = ledger, Cycle = cycle };
    }

    private static T ReadJson<T>(string path) where T : class
    {
        if (!FileAccess.FileExists(path))
            return null;

        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null)
                return null;
            return JsonSerializer.Deserialize<T>(file.GetAsText(), JsonOptions);
        }
        catch (Exception e)
        {
            GD.PrintErr($"SaveManager: Read failed for {path}: {e.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // New game / new cycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a fresh guild (new ledger AND first cycle) in the given slot
    /// and make it active. <paramref name="school"/> is required so the
    /// starter deck can be seeded immediately.
    /// </summary>
    public static GuildSaveData NewGame(int slot, string guildName = "New Guild",
                                        string school = "Elementalist")
    {
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var data = new GuildSaveData
        {
            Ledger = new EternalLedger
            {
                GuildName = guildName,
                CreatedAt = now,
                LastPlayedAt = now,
            },
            Cycle = new CycleState
            {
                CycleNumber = 1,
                SelectedSchool = school,
            },
        };

        SeedDeckForSchool(data, school);

        ActiveSave = data;
        ActiveSlot = slot;
        SaveToSlot(slot, data);

        GD.Print($"SaveManager: New guild in slot {slot} (school: {school})");
        return data;
    }

    /// <summary>
    /// End the current cycle and begin the next timeline. Archives a
    /// LoopRecord into the eternal ledger, replaces the CycleState
    /// wholesale, and seeds the new school's starter deck.
    /// Phase 5 expands this (trace-back eclipse scheduling on losses,
    /// renown anchoring prompts, Kassian adaptation inputs).
    /// </summary>
    /// <param name="school">School for the new cycle (one cycle, one school).</param>
    /// <param name="outcome">"Victory", "ConvergenceDefeat", "CorruptionLoss", or "Abandoned".</param>
    /// <param name="resolutionPath">"Restoration", "Harness", "Synthesis", or "" for non-victories.</param>
    public static GuildSaveData BeginNewCycle(string school, string outcome,
                                              string resolutionPath = "")
    {
        if (ActiveSave == null || ActiveSlot < 0)
        {
            GD.PrintErr("SaveManager: No active save — cannot begin a new cycle.");
            return null;
        }

        var old = ActiveSave.Cycle;

        // ── Archive the ended timeline into the loom ────────────────────
        var record = new LoopRecord
        {
            CycleNumber = old.CycleNumber,
            School = old.SelectedSchool,
            Outcome = outcome,
            ResolutionPath = resolutionPath,
            LunationsElapsed = old.Calendar.CurrentLunation,
            RunsCompleted = old.TotalRuns,
        };
        foreach (var kvp in old.Campaign.Dispositions)
            record.FinalDispositions[kvp.Key] = kvp.Value.ToString();

        ActiveSave.Ledger.LoopHistory.Add(record);

        // ── A new timeline ──────────────────────────────────────────────
        ActiveSave.Cycle = new CycleState
        {
            CycleNumber = old.CycleNumber + 1,
            SelectedSchool = school,
        };

        SeedDeckForSchool(ActiveSave, school);

        Save();
        GD.Print($"SaveManager: Cycle {old.CycleNumber} archived ({outcome}). " +
                 $"Cycle {ActiveSave.Cycle.CycleNumber} begun (school: {school}).");
        return ActiveSave;
    }

    private static void SeedDeckForSchool(GuildSaveData data, string school)
    {
        // CardDatabase must be loaded before this is called.
        if (Enum.TryParse<CardSchool>(school, ignoreCase: true, out var cardSchool))
            StarterDeckLoader.SeedStarterDeck(data, cardSchool);
        else
            GD.PrintErr($"SaveManager: Unknown school '{school}' — PlayerDeck not seeded.");
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
                info.GuildName = data.Ledger.GuildName;
                info.School = data.Cycle.SelectedSchool;
                info.Gold = data.Cycle.Gold;
                info.TotalRuns = data.Cycle.TotalRuns;
                info.CycleNumber = data.Cycle.CycleNumber;
                info.LastPlayed = data.Ledger.LastPlayedAt;
            }

            slots.Add(info);
        }

        return slots;
    }

    /// <summary>
    /// Delete a save slot — both tier files, their backups/temps, and any
    /// legacy pre-v100 single-file save occupying the slot name.
    /// </summary>
    public static void DeleteSlot(int slot)
    {
        string[] paths =
        {
            GetCyclePath(slot),
            GetCyclePath(slot) + ".tmp",
            GetLedgerPath(slot),
            GetLedgerPath(slot) + ".bak",
            GetLedgerPath(slot) + ".tmp",
            GetLegacyPath(slot),
        };

        foreach (var path in paths)
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }

        GD.Print($"SaveManager: Deleted slot {slot}");

        if (ActiveSlot == slot)
        {
            ActiveSave = null;
            ActiveSlot = -1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static string GetCyclePath(int slot) => $"{SAVE_DIR}slot_{slot}_cycle.json";
    private static string GetLedgerPath(int slot) => $"{SAVE_DIR}slot_{slot}_ledger.json";
    private static string GetLegacyPath(int slot) => $"{SAVE_DIR}slot_{slot}.json";

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
    public int CycleNumber;
    public string LastPlayed = "";
}
