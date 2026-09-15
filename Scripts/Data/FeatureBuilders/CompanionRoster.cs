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
//                 README §6 (Save System)
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
                // New companion: add with all fields from template
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
                    SignatureStanceId = template.SignatureStanceId, // K4
                });
            }
            else
            {
                // Existing companion: migrate any missing fields from template
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
                // K4: backfill the signature override on saves that predate it.
                if (string.IsNullOrEmpty(existing.SignatureStanceId) &&
                    !string.IsNullOrEmpty(template.SignatureStanceId))
                    existing.SignatureStanceId = template.SignatureStanceId;
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
    /// <summary>Debug-only party override set by CombatDebugLauncher. When non-null,
    /// GetActiveParty returns this synthetic list so a standalone fight can field
    /// arbitrary companions. Cleared on return to campus.</summary>
    public static List<Companion> DebugPartyOverride = null;

    public static List<Companion> GetActiveParty()
    {
        if (DebugPartyOverride != null) return DebugPartyOverride;
        var save = SaveManager.ActiveSave;
        if (save == null) return new List<Companion>();
        // K2 (§5b): injured companions are excluded from all three demands.
        // They cannot be fielded (this filter also covers negotiation tokens
        // and combat spawns, which all read the party through here).
        // K2.5: mid-expedition, a companion stabilized at 0 (downed in a won
        // fight) is out for the REST of the expedition.
        return save.Companions
            .Where(c => save.ActivePartyCompanionIds.Contains(c.Id) && !c.IsPermadead && !c.IsInjured
                        && !(PlayerSession.IsOnExpedition && c.ExpeditionHP == 0))
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

    /// <summary>[Exploration reward] Make a companion available AND recruited
    /// for this timeline, free of the gold cost. They were *found*, not bought.
    /// No-op (returns null) if unknown, dead, or already recruited. Returns the
    /// recruited companion's name on success.</summary>
    public static string GrantFromEncounter(string companionId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || string.IsNullOrEmpty(companionId)) return null;

        var c = save.Companions.FirstOrDefault(x => x.Id == companionId);
        if (c == null || c.IsPermadead || c.IsRecruited) return null;

        c.IsAvailable = true;
        c.IsRecruited = true;
        SaveManager.MarkDirty();
        GD.Print($"[Companion] {c.Name} recruited via exploration.");
        return c.Name;
    }

    /// <summary>The castle's helmsman: the one companion every timeline starts
    /// with. Stoic, so best-in-slot at the Helm (CrewStations), and the crew that
    /// holds the walls in a castle defence until the wizard arrives.</summary>
    public const string StartingDriverId = "brannoc_helm";
    private const string DriverSeededFlag = "starting_driver_seeded";

    /// <summary>Grant the starting driver once per cycle: recruited, free, and in
    /// the active party. Flag-gated on the cycle so benching him later sticks.
    /// Companions live on the CycleState, so this runs again for every new
    /// timeline. A driver who died in a past timeline is a new man in the next:
    /// the roster is rebuilt from templates when the cycle is unmade.</summary>
    public static void EnsureStartingDriver(GuildSaveData save)
    {
        if (save?.Cycle == null || save.Companions == null) return;
        if (save.HasFlag(DriverSeededFlag)) return;

        var c = save.Companions.FirstOrDefault(x => x.Id == StartingDriverId);
        if (c == null)
        {
            GD.PushWarning($"[Companion] Starting driver '{StartingDriverId}' is not in the roster; no template?");
            return;
        }

        c.IsAvailable = true;
        c.IsRecruited = true;
        if (!c.IsPermadead && !save.ActivePartyCompanionIds.Contains(c.Id)
            && save.ActivePartyCompanionIds.Count < save.MaxPartySize)
            save.ActivePartyCompanionIds.Add(c.Id);

        save.Cycle.SetFlag(DriverSeededFlag);
        SaveManager.MarkDirty();
        GD.Print($"[Companion] {c.Name} takes the Helm: starting driver seeded for cycle {save.Cycle.CycleNumber}.");
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

        // Envoys are afield, derived from CouncilState.ActiveMissions
        // (single-source; never a flag on Companion).
        if (CouncilQueries.IsOnMission(companionId)) return false;

        // Envoys imprisoned after a Scandal spiral are held until rescued,
        // derived from CouncilState.Imprisoned, same single-source discipline.
        if (CouncilQueries.IsImprisoned(companionId)) return false;

        // Cache overseers are posted afield, derived from
        // WorldPoi.OverseerCompanionId (SupplyCacheSystem), never a flag.
        if (SupplyCacheSystem.IsOverseer(companionId)) return false;

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