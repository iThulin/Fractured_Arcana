using Godot;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Bridges companion templates (loaded from JSON) with runtime state
/// (stored in GuildSaveData.Companions).
/// 
/// On first access for a save, ensures every template has a runtime entry.
/// </summary>
public static class CompanionRoster
{
    /// <summary>
    /// Ensure every companion template has a corresponding entry in the save.
    /// Adds missing ones with default state. Call after loading a save.
    /// </summary>
    public static void EnsureRoster(GuildSaveData save)
    {
        if (save == null) return;
        var templates = CompanionLoader.LoadAll();

        foreach (var template in templates)
        {
            if (save.Companions.Any(c => c.Id == template.Id)) continue;

            // Clone template into save with default state
            var entry = new Companion
            {
                Id = template.Id,
                Name = template.Name,
                School = template.School,
                PersonalityTrait = template.PersonalityTrait,
                Backstory = template.Backstory,
                ContributedCardIds = new List<string>(template.ContributedCardIds),
                RecruitmentCost = template.RecruitmentCost,
                UnlockCondition = template.UnlockCondition,

                // Default starting state
                IsRecruited = false,
                IsAvailable = template.IsAvailable, // some companions start available
                IsPermadead = false,
                Loyalty = 50,
                ArcStage = 0,
            };
            save.Companions.Add(entry);
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