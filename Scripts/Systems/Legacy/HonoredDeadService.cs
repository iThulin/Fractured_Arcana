using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// HonoredDeadService.cs
//
// Purpose:        Static service class for managing the player's
//                 "honored dead" spirits in the Ossuary. Provides
//                 methods for recording deaths, claiming records to
//                 summon spirits in combat, and querying records for
//                 display in the Ossuary UI.
// Layer:          Combat
// Collaborators:  HonoredDeadRecord.cs (data class representing a single
//                 honored dead record), OssuaryUI.cs (displays the list of
//                 honored dead and their details), HonoredDeadManager.cs
//                 (manages the list of honored dead records and their persistence),
//                 CombatManager.cs (calls RecordDeath when units die and summons spirits based on these records)
// See:            README §6.4 (Honored Dead and the Ossuary)
// ============================================================

public static class HonoredDeadService
{
    // Tracks indices claimed this combat so spirits prefer fresh deaths
    // Cleared at combat start — re-used records are allowed if pool runs dry
    private static readonly HashSet<int> _claimedThisCombat = new();
    private static string _currentRegionName = "";

    // ── Combat lifecycle ─────────────────────────────────────────────

    public static void OnCombatStart(string regionName = "")
    {
        _claimedThisCombat.Clear();
        _currentRegionName = regionName;
        GD.Print($"[HonoredDead] Combat started. {Count} records in guild memory.");
    }

    // ── Recording deaths ─────────────────────────────────────────────

    // Call from CombatManager.HandleUnitDeath BEFORE the unit is removed
    public static void RecordDeath(Unit unit)
    {
        if (unit == null) return;
        var save = SaveManager.ActiveSave;
        if (save == null) return;

        // Don't record spirits — they're already echoes
        if (unit.IsSpirit) return;

        var meshNode = unit.GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (meshNode?.Mesh == null) return;

        string meshPath = meshNode.Mesh.ResourcePath;
        if (string.IsNullOrEmpty(meshPath)) return; // procedural mesh, can't serialize

        save.HonoredDead.Add(new HonoredDeadRecord
        {
            MeshResourcePath = meshPath,
            Name             = unit.DisplayName?.Length > 0 ? unit.DisplayName : unit.Name,
            WasAlly          = unit.TeamId == 0,
            School           = unit.School.ToString(),
            CompanionId      = unit.CompanionId ?? "",
            RunNumber        = save.TotalRuns,
            RegionName       = _currentRegionName,
            HasBeenSummoned  = false,
        });

        SaveManager.MarkDirty();
        GD.Print($"[HonoredDead] Recorded: {unit.Name} — {save.HonoredDead.Count} total.");
    }

    // ── Claiming records for spirit summons ──────────────────────────

    // Priority:
    //   1. Fell this combat, not yet claimed this combat
    //   2. Any ally/companion record not yet claimed this combat
    //   3. Any unclaimed record from any run
    //   4. Most recent record (re-use — pool exhausted)
    public static HonoredDeadRecord Claim()
    {
        var save = SaveManager.ActiveSave;
        if (save?.HonoredDead == null || save.HonoredDead.Count == 0)
            return null;

        var all = save.HonoredDead;
        int currentRun = save.TotalRuns;

        // 1. Recent death this combat, unclaimed
        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (all[i].RunNumber == currentRun && !_claimedThisCombat.Contains(i))
                return ClaimAt(i);
        }

        // 2. Any ally/companion, unclaimed
        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (all[i].WasAlly && !_claimedThisCombat.Contains(i))
                return ClaimAt(i);
        }

        // 3. Any unclaimed record
        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (!_claimedThisCombat.Contains(i))
                return ClaimAt(i);
        }

        // 4. Pool exhausted — recycle most recent
        GD.Print("[HonoredDead] Pool exhausted — recycling most recent record.");
        return all[all.Count - 1];
    }

    // Claim a specific companion's record by companion id
    public static HonoredDeadRecord ClaimByCompanionId(string companionId)
    {
        var save = SaveManager.ActiveSave;
        if (save?.HonoredDead == null) return null;

        for (int i = save.HonoredDead.Count - 1; i >= 0; i--)
        {
            if (save.HonoredDead[i].CompanionId == companionId)
                return ClaimAt(i);
        }
        return null;
    }

    // ── Ossuary UI queries ───────────────────────────────────────────

    public static List<HonoredDeadRecord> GetAllAllies()
        => SaveManager.ActiveSave?.HonoredDead
            ?.Where(r => r.WasAlly)
            .OrderByDescending(r => r.RunNumber)
            .ToList() ?? new List<HonoredDeadRecord>();

    public static List<HonoredDeadRecord> GetAllEnemies()
        => SaveManager.ActiveSave?.HonoredDead
            ?.Where(r => !r.WasAlly)
            .OrderByDescending(r => r.RunNumber)
            .ToList() ?? new List<HonoredDeadRecord>();

    public static int Count
        => SaveManager.ActiveSave?.HonoredDead?.Count ?? 0;

    // ── Private helpers ──────────────────────────────────────────────

    private static HonoredDeadRecord ClaimAt(int index)
    {
        var save = SaveManager.ActiveSave;
        _claimedThisCombat.Add(index);
        save.HonoredDead[index].HasBeenSummoned = true;
        SaveManager.MarkDirty();
        return save.HonoredDead[index];
    }
}