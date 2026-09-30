using Godot;
using System.Collections.Generic;

// ============================================================
// Charters.cs
//
// Purpose:        Doctrines and charters (campus_building_upgrades_
//                 design_v1 §2). A building's top tier offers two
//                 doctrines; only the one chartered at the Grand Hall
//                 is in force. The Grand Hall's tiers set how many
//                 charters a timeline may hold (1/2/3). Charters live
//                 on CycleState, so every new timeline begins with
//                 none and its first fills are free; a fill past the
//                 free count is a refit that costs materials and takes
//                 effect from the next lunation.
// Layer:          Data / Feature builder
// Collaborators:  BuildingDefinition.cs (BuildingTier.Doctrines,
//                 CharterSlots, DoctrineDefinition), CycleState.cs
//                 (Charters, CharterFillsUsed), CalendarState
//                 (CurrentLunation), CharterSection.cs (Grand Hall UI),
//                 every doctrine consumer (IsActive).
// Rulings:        2026-09-29, Magos: charters as designed; 1/2/3 slots.
// Deviation:      The design says a refit "closes the building for a
//                 lunation". Here only the DOCTRINE is dark for that
//                 lunation (the building works at the tier below);
//                 closing every door of a building would touch every
//                 consumer for no extra decision.
// ============================================================

/// <summary>One chartered doctrine. Save-adjacent: round-trip asserted by
/// CharterSaveAssert.</summary>
public class CharterEntry
{
    public string BuildingId = "";
    public string DoctrineId = "";

    /// <summary>First lunation the doctrine is in force. A free fill is in force
    /// at once (the lunation it was set); a refit waits one lunation.</summary>
    public int ActiveFromLunation = 0;
}

/// <summary>Reads and writes charters. Stateless; state lives on CycleState.</summary>
public static class Charters
{
    /// <summary>Materials a refit costs (design §2b). A starting value.</summary>
    public const int RefitMaterials = 60;

    public const string GrandHallId = "grand_hall";

    // ── Reads ────────────────────────────────────────────────────────────

    /// <summary>Charter slots the guild holds: CharterSlots summed over the Grand
    /// Hall's built tiers.</summary>
    public static int Slots(GuildSaveData save)
    {
        var hall = Find(save, GrandHallId);
        var template = BuildingDatabase.GetTemplate(GrandHallId);
        if (hall == null || template == null || !hall.IsFunctional)
        {
            return 0;
        }
        int slots = 0;
        foreach (var t in template.Tiers)
        {
            if (t.Tier <= hall.Tier)
            {
                slots += t.CharterSlots;
            }
        }
        // A blighted Grand Hall (campus corruption, §11c) holds one fewer:
        // the blight sits in a chair at the council table.
        if (CampusBlight.IsBlighted(save, GrandHallId))
        {
            slots = System.Math.Max(0, slots - 1);
        }
        return slots;
    }

    /// <summary>The tier that offers doctrines, or null when the building has none.</summary>
    public static BuildingTier DoctrineTier(Building template)
    {
        if (template?.Tiers == null)
        {
            return null;
        }
        foreach (var t in template.Tiers)
        {
            if (t.Doctrines != null && t.Doctrines.Count > 0)
            {
                return t;
            }
        }
        return null;
    }

    public static DoctrineDefinition Doctrine(string buildingId, string doctrineId)
    {
        var tier = DoctrineTier(BuildingDatabase.GetTemplate(buildingId));
        if (tier == null)
        {
            return null;
        }
        foreach (var d in tier.Doctrines)
        {
            if (d.Id == doctrineId)
            {
                return d;
            }
        }
        return null;
    }

    /// <summary>Has the building reached the tier that offers its doctrines, and
    /// is it standing on the map?</summary>
    public static bool CanHoldDoctrine(GuildSaveData save, string buildingId)
    {
        var entry = Find(save, buildingId);
        var tier = DoctrineTier(BuildingDatabase.GetTemplate(buildingId));
        return entry != null && tier != null && entry.IsFunctional && entry.Tier >= tier.Tier;
    }

    /// <summary>The charter entry for a building this timeline, or null.</summary>
    public static CharterEntry EntryFor(GuildSaveData save, string buildingId)
    {
        var list = save?.Cycle?.Charters;
        if (list == null)
        {
            return null;
        }
        foreach (var c in list)
        {
            if (c != null && c.BuildingId == buildingId)
            {
                return c;
            }
        }
        return null;
    }

    /// <summary>THE doctrine gate. True when this doctrine is chartered, in force
    /// this lunation, and the building still stands at the doctrine tier.</summary>
    public static bool IsActive(GuildSaveData save, string buildingId, string doctrineId)
    {
        var c = EntryFor(save, buildingId);
        if (c == null || c.DoctrineId != doctrineId)
        {
            return false;
        }
        if (!CanHoldDoctrine(save, buildingId))
        {
            return false;
        }
        // Blighted ground (campus corruption, §11): the doctrine goes dark and the
        // building's blighted form takes over. Cleansing the land relights it.
        if (CampusBlight.IsBlighted(save, buildingId))
        {
            return false;
        }
        // A Grand Hall short of seats (blighted, or lost) cannot hold every
        // charter: the latest charters past the seat count go dark (audit
        // 2026-09-29; before, a full hall kept all of them in force).
        if (IsOverSlots(save, buildingId))
        {
            return false;
        }
        return CurrentLunation(save) >= c.ActiveFromLunation;
    }

    /// <summary>True when this building's charter sits past the Grand Hall's
    /// current seat count (charters are ranked in the order they were made; the
    /// free school seat never counts).</summary>
    public static bool IsOverSlots(GuildSaveData save, string buildingId)
    {
        var list = save?.Cycle?.Charters;
        if (list == null || IsFreeSeat(save, buildingId))
        {
            return false;
        }
        int rank = 0;
        foreach (var c in list)
        {
            if (c == null || IsFreeSeat(save, c.BuildingId))
            {
                continue;
            }
            if (c.BuildingId == buildingId)
            {
                return rank >= Slots(save);
            }
            rank++;
        }
        return false;
    }

    /// <summary>True when the charter exists but its refit has not landed yet.</summary>
    public static bool IsRefitting(GuildSaveData save, string buildingId)
    {
        var c = EntryFor(save, buildingId);
        return c != null && CurrentLunation(save) < c.ActiveFromLunation;
    }

    /// <summary>Charters that occupy a Grand Hall slot. The seat of the timeline's
    /// own school is chartered free (ruling §9.5) and does not count.</summary>
    public static int Used(GuildSaveData save)
    {
        var list = save?.Cycle?.Charters;
        if (list == null)
        {
            return 0;
        }
        int n = 0;
        foreach (var c in list)
        {
            if (c != null && !IsFreeSeat(save, c.BuildingId))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>True for the school seat of the school this timeline plays: its
    /// charter needs no Grand Hall slot and its first fill is always free.</summary>
    public static bool IsFreeSeat(GuildSaveData save, string buildingId)
    {
        var template = BuildingDatabase.GetTemplate(buildingId);
        return template != null && template.IsSchoolSeat
               && string.Equals(template.SchoolAffinity, save?.Cycle?.SelectedSchool,
                                System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Would the next fill be free (inside this timeline's free count)?</summary>
    public static bool NextFillIsFree(GuildSaveData save)
        => save?.Cycle != null && save.Cycle.CharterFillsUsed < Slots(save);

    // ── Writes ───────────────────────────────────────────────────────────

    /// <summary>Why this doctrine cannot be chartered now, or null when it can.</summary>
    public static string CannotCharterReason(GuildSaveData save, string buildingId, string doctrineId)
    {
        if (save?.Cycle == null)
        {
            return "No save loaded.";
        }
        var d = Doctrine(buildingId, doctrineId);
        if (d == null)
        {
            return "No such doctrine.";
        }
        if (d.IsBlocked)
        {
            return d.Blocked;
        }
        if (!CanHoldDoctrine(save, buildingId))
        {
            return "The building has not reached its doctrine tier, or is not sited.";
        }
        var existing = EntryFor(save, buildingId);
        if (existing != null && existing.DoctrineId == doctrineId)
        {
            return "Already chartered.";
        }
        bool free = IsFreeSeat(save, buildingId);
        if (existing == null && !free && Used(save) >= Slots(save))
        {
            return $"Every charter is held ({Used(save)}/{Slots(save)}). Unseat one, or raise the Grand Hall.";
        }
        bool refit = IsRefit(save, buildingId);
        if (refit && save.BuildMaterials < RefitMaterials)
        {
            return $"A refit costs {RefitMaterials} materials; the stores hold {save.BuildMaterials}.";
        }
        return null;
    }

    /// <summary>Is chartering this doctrine a refit (paid, delayed a lunation)?</summary>
    public static bool IsRefit(GuildSaveData save, string buildingId)
        => EntryFor(save, buildingId) != null
           || (!IsFreeSeat(save, buildingId) && !NextFillIsFree(save));

    /// <summary>Charter a doctrine. Returns the report line, or null if refused.</summary>
    public static string TryCharter(GuildSaveData save, string buildingId, string doctrineId)
    {
        if (CannotCharterReason(save, buildingId, doctrineId) != null)
        {
            return null;
        }
        var cycle = save.Cycle;
        cycle.Charters ??= new List<CharterEntry>();
        int now = CurrentLunation(save);
        bool refit = IsRefit(save, buildingId);
        var d = Doctrine(buildingId, doctrineId);

        var entry = EntryFor(save, buildingId);
        if (entry == null)
        {
            entry = new CharterEntry { BuildingId = buildingId };
            cycle.Charters.Add(entry);
            if (!IsFreeSeat(save, buildingId))
            {
                cycle.CharterFillsUsed++;
            }
        }
        entry.DoctrineId = doctrineId;

        string line;
        if (refit)
        {
            save.BuildMaterials -= RefitMaterials;
            entry.ActiveFromLunation = now + 1;
            line = $"{d.Name} is chartered. The refit costs {RefitMaterials} materials and holds from the next moon.";
        }
        else
        {
            entry.ActiveFromLunation = now;
            line = $"{d.Name} is chartered and in force.";
        }
        SaveManager.MarkDirty();
        GD.Print($"[Charters] {buildingId}: {doctrineId} (refit {refit}, from lunation {entry.ActiveFromLunation}).");
        return line;
    }

    /// <summary>Unseat a building's charter. Frees the slot; never refunds the fill.</summary>
    public static string Unseat(GuildSaveData save, string buildingId)
    {
        var entry = EntryFor(save, buildingId);
        if (entry == null)
        {
            return null;
        }
        save.Cycle.Charters.Remove(entry);
        SaveManager.MarkDirty();
        GD.Print($"[Charters] {buildingId}: unseated.");
        var d = Doctrine(buildingId, entry.DoctrineId);
        return $"{d?.Name ?? entry.DoctrineId} is unseated. The charter is free for another doctrine.";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static BuildingSaveData Find(GuildSaveData save, string buildingId)
    {
        if (save?.Buildings == null)
        {
            return null;
        }
        foreach (var b in save.Buildings)
        {
            if (b.Id == buildingId)
            {
                return b;
            }
        }
        return null;
    }

    private static int CurrentLunation(GuildSaveData save)
        => save?.Cycle?.Calendar?.CurrentLunation ?? 1;
}
