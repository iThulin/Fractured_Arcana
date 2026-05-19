using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CompanionRoster.cs
//
// Purpose:        Bridges companion templates (loaded from JSON)
//                 with the per-save runtime state. Backfills
//                 missing entries on save load, migrates fields
//                 added since the save was created, and exposes
//                 recruit/party/recruitable queries against the
//                 active save.
// Layer:          System
// Collaborators:  CompanionLoader.cs (templates),
//                 CompanionDefinition.cs (Companion model),
//                 SaveManager.cs (ActiveSave + Save trigger),
//                 GuildSaveData.cs (state container)
// See:            README §4.5 (Adding a Companion),
//                 README §6 — Save System
// ============================================================

/// <summary>Bridges companion JSON templates with the per-save runtime state. <see cref="EnsureRoster"/> backfills missing entries and migrates new template fields onto existing saves. The recruit/party query helpers all operate on <c>SaveManager.ActiveSave</c>.</summary>
public static class CompanionRoster
{
    /// <summary>
    /// Ensure every companion template has a corresponding entry in the save.
    /// Adds missing ones with default state. Call after loading a save.
    /// </summary>
    public static void EnsureRoster(GuildSaveData save)
    {
        if (save == null) return;
        CompanionLoader.ClearCache();
        var templates = CompanionLoader.LoadAll();

        foreach (var template in templates)
        {
            var existing = save.Companions.Find(c => c.Id == template.Id);

            if (existing == null)
            {
                // New companion — add with all fields from template
                save.Companions.Add(new Companion
                {
                    Id = template.Id,
                    Name = template.Name,
                    School = template.School,
                    PersonalityTrait = template.PersonalityTrait,
                    Backstory = template.Backstory,
                    ContributedCardIds = new List<string>(template.ContributedCardIds),
                    RecruitmentCost = template.RecruitmentCost,
                    UnlockCondition = template.UnlockCondition,
                    IsRecruited = false,
                    IsAvailable = template.IsAvailable,
                    IsPermadead = false,
                    Loyalty = 50,
                    ArcStage = 0,
                    // ── New fields ──────────────────────────
                    UnitClass = template.UnitClass,
                    BaseHP = template.BaseHP,
                    BaseSpeed = template.BaseSpeed,
                    BaseArmor = template.BaseArmor,
                    BaseAttackDamage = template.BaseAttackDamage,
                    BaseAttackRange = template.BaseAttackRange,
                    BaseMana = template.BaseMana,
                });
            }
            else
            {
                // Existing companion — migrate any missing fields from template
                // This handles saves created before new fields were added
                if (string.IsNullOrEmpty(existing.UnitClass) || existing.UnitClass == "None")
                    existing.UnitClass = template.UnitClass;
                if (existing.BaseHP <= 0)
                    existing.BaseHP = template.BaseHP;
                if (existing.BaseSpeed <= 0)
                    existing.BaseSpeed = template.BaseSpeed;
                if (existing.BaseAttackDamage <= 0)
                    existing.BaseAttackDamage = template.BaseAttackDamage;
                if (existing.BaseAttackRange <= 0)
                    existing.BaseAttackRange = template.BaseAttackRange;
                if (existing.BaseMana == 0 && template.BaseMana > 0)
                    existing.BaseMana = template.BaseMana;
                if (existing.BaseArmor == 0 && template.BaseArmor > 0)
                    existing.BaseArmor = template.BaseArmor;
            }
        }
    }

    /// <summary>
    /// Get all recruited companions from the active save.
    /// </summary>
    public static List<Companion> GetRecruited()
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return new List<Companion>();
        return save.Companions
            .Where(c => c.IsRecruited && !c.IsPermadead)
            .ToList();
    }

    /// <summary>
    /// Get companions currently in the active party (chosen for next run).
    /// </summary>
    public static List<Companion> GetActiveParty()
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return new List<Companion>();
        return save.Companions
            .Where(c => save.ActivePartyCompanionIds.Contains(c.Id) && !c.IsPermadead)
            .ToList();
    }

    /// <summary>
    /// Get companions available for recruitment but not yet recruited.
    /// </summary>
    public static List<Companion> GetRecruitable()
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return new List<Companion>();
        return save.Companions
            .Where(c => c.IsAvailable && !c.IsRecruited && !c.IsPermadead)
            .ToList();
    }

    public static bool TryRecruit(string companionId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return false;

        var c = save.Companions.FirstOrDefault(x => x.Id == companionId);
        if (c == null || c.IsRecruited || !c.IsAvailable || c.IsPermadead)
            return false;

        if (save.Gold < c.RecruitmentCost) return false;

        save.Gold -= c.RecruitmentCost;
        c.IsRecruited = true;
        SaveManager.Save();
        GD.Print($"Recruited {c.Name} for {c.RecruitmentCost} gold.");
        return true;
    }

    public static bool TryAddToParty(string companionId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return false;

        var c = save.Companions.FirstOrDefault(x => x.Id == companionId);
        if (c == null || !c.IsRecruited || c.IsPermadead) return false;

        if (save.ActivePartyCompanionIds.Contains(companionId)) return false;
        if (save.ActivePartyCompanionIds.Count >= save.MaxPartySize) return false;

        save.ActivePartyCompanionIds.Add(companionId);
        SaveManager.Save();
        return true;
    }

    public static bool RemoveFromParty(string companionId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null) return false;
        bool removed = save.ActivePartyCompanionIds.Remove(companionId);
        if (removed) SaveManager.Save();
        return removed;
    }
}