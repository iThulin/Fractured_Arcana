using System.Collections.Generic;
using System.Linq;

// ============================================================
// CampaignState.cs
//
// Purpose:        The persistent campaign-level state for one
//                 guild. Tracks: the seeded archmage placement
//                 across all regions, each archmage's disposition
//                 toward the player, regional corruption levels
//                 (the Chronomancer's advancing influence), the
//                 global step counter driving corruption ticks,
//                 and mentor interaction history.
//                 Lives as a field inside GuildSaveData so it
//                 serializes/loads with the rest of the save.
// Layer:          Data
// Collaborators:  CampaignGenerator.cs (creates from seed),
//                 ArchmageRegistry.cs (looks up definitions),
//                 OverworldRunManager.cs (ticks GlobalStepCount),
//                 CampusMentorPanel.cs (mentor dialogue),
//                 FinalBattleManager.cs (reads dispositions)
// See:            README §5 — Campaign Layer
// ============================================================

/// <summary>
/// How the player has resolved (or not yet encountered) one archmage.
/// Drives what options are available in their region and how they
/// appear in the final battle.
/// </summary>
public enum ArchmageDisposition
{
    /// <summary>Player has not yet entered this archmage's region.</summary>
    Unknown,
    /// <summary>Player has entered the region; archmage is aware of the player but no resolution yet.</summary>
    Neutral,
    /// <summary>Fully united — archmage fights at full strength for the player in the final battle.</summary>
    Allied,
    /// <summary>Coerced into alliance — fights for the player at reduced effectiveness; Chronomancer can flip them.</summary>
    Coerced,
    /// <summary>Defeated in boss combat — shard invocation available; archmage is absent from the final battle.</summary>
    Overthrown,
    /// <summary>Chronomancer's corruption reached maximum before the player resolved them; fights against the player in the final battle.</summary>
    Corrupted
}

/// <summary>
/// Full campaign-level persistent state for one guild.
/// Generated once at new game from a seeded RNG and updated
/// as the player progresses through regions. Serialized as
/// a field of <see cref="GuildSaveData"/>.
/// </summary>
public class CampaignState
{
    // ── Generation ────────────────────────────────────────────────────────
    /// <summary>Seed used to generate this campaign's archmage placement. Fixed at new game; never changes.</summary>
    public int CampaignSeed = 0;

    /// <summary>
    /// Id of the archmage who co-conspired with the Chronomancer to break
    /// the magisphere seal. Revealed in the intro scripted encounter.
    /// Always the archmage assigned to the highest-tier region adjacent
    /// to The Convergence (determined by CampaignGenerator).
    /// </summary>
    public string CoConspirator = "";

    // ── Dynamic world map ─────────────────────────────────────────────────
    /// <summary>
    /// Maps regionId → archmageid. Regions without an assigned archmage
    /// have an empty string value. Locked in at campaign creation.
    /// </summary>
    public Dictionary<string, string> RegionArchmageMap = new();

    // ── Archmage dispositions ─────────────────────────────────────────────
    /// <summary>
    /// Current disposition of each archmage toward the player.
    /// Keys are archmageid strings. Defaults to Unknown on campaign start.
    /// </summary>
    public Dictionary<string, ArchmageDisposition> Dispositions = new();

    // ── Corruption ────────────────────────────────────────────────────────
    /// <summary>
    /// The Chronomancer's influence level per region (0–3).
    /// 0 = no presence. 3 = archmage fully corrupted (if not already resolved).
    /// Keys are regionId strings.
    /// </summary>
    public Dictionary<string, int> CorruptionLevels = new();

    // ── Chronomancer's clock ──────────────────────────────────────────────
    /// <summary>
    /// Total player steps taken across ALL runs in this campaign.
    /// Every CorruptionTickInterval steps, corruption advances in one region.
    /// </summary>
    public int GlobalStepCount = 0;

    /// <summary>
    /// Steps between corruption ticks. Default 60 (~2 full regions worth of
    /// steps). Tune downward to increase pressure; upward to give more breathing room.
    /// </summary>
    public int CorruptionTickInterval = 60;

    /// <summary>Step count at which the last corruption tick fired. Used to detect tick boundaries.</summary>
    public int LastCorruptionTickAt = 0;

    // ── Mentor state ──────────────────────────────────────────────────────
    /// <summary>Ids of mentor hint types already delivered, so they don't repeat.</summary>
    public List<string> MentorHintsDelivered = new();
    /// <summary>Total number of mentor visits (campus visits after run returns).</summary>
    public int MentorVisitCount = 0;

    // ── Final battle ─────────────────────────────────────────────────────
    /// <summary>True when all regions have been resolved (any disposition except Unknown/Neutral) and The Convergence is accessible.</summary>
    public bool FinalBattleUnlocked = false;
    /// <summary>True after the final battle has been completed (win or lose).</summary>
    public bool CampaignComplete = false;
    /// <summary>"Victory", "Defeat", or "" if not yet completed.</summary>
    public string CampaignOutcome = "";

    // ═══════════════════════════════════════════════════════════════════════
    // Convenience accessors (not serialized — computed from above state)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Returns the archmageid assigned to the given region, or "" if none.</summary>
    public string GetArchmageForRegion(string regionId) =>
        RegionArchmageMap.TryGetValue(regionId, out var id) ? id : "";

    /// <summary>Returns the regionId where the given archmage is assigned, or "" if not placed.</summary>
    public string GetRegionForArchmage(string archmageid) =>
        RegionArchmageMap.FirstOrDefault(kvp => kvp.Value == archmageid).Key ?? "";

    /// <summary>Returns the current disposition of an archmage, or Unknown if not tracked.</summary>
    public ArchmageDisposition GetDisposition(string archmageid) =>
        Dispositions.TryGetValue(archmageid, out var d) ? d : ArchmageDisposition.Unknown;

    /// <summary>Returns the corruption level of a region (0–3).</summary>
    public int GetCorruption(string regionId) =>
        CorruptionLevels.TryGetValue(regionId, out var c) ? c : 0;

    /// <summary>True when this archmage's school matches the player's selected school — the betrayal encounter.</summary>
    public bool IsSchoolBetrayal(string archmageid, string playerSchool)
    {
        var def = ArchmageRegistry.Get(archmageid);
        return def != null && string.Equals(def.School, playerSchool,
            System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns all archmagi who will fight FOR the player in the final battle
    /// (Allied or Coerced dispositions).
    /// </summary>
    public List<string> GetAllies() =>
        Dispositions
            .Where(kvp => kvp.Value == ArchmageDisposition.Allied ||
                          kvp.Value == ArchmageDisposition.Coerced)
            .Select(kvp => kvp.Key)
            .ToList();

    /// <summary>
    /// Returns all archmagi who will fight AGAINST the player in the final battle
    /// (Corrupted disposition only — Overthrown are absent).
    /// </summary>
    public List<string> GetEnemies() =>
        Dispositions
            .Where(kvp => kvp.Value == ArchmageDisposition.Corrupted)
            .Select(kvp => kvp.Key)
            .ToList();

    /// <summary>Returns archmageid shards available as one-use invocations (Overthrown only).</summary>
    public List<string> GetShardInvocations() =>
        Dispositions
            .Where(kvp => kvp.Value == ArchmageDisposition.Overthrown)
            .Select(kvp => kvp.Key)
            .ToList();

    /// <summary>
    /// True when every placed archmage has been resolved
    /// (Allied, Coerced, Overthrown, or Corrupted — not Unknown or Neutral).
    /// Used to unlock the final battle.
    /// </summary>
    public bool AllArchmagiResolved()
    {
        foreach (var pair in RegionArchmageMap)
        {
            if (string.IsNullOrEmpty(pair.Value))
                continue; // unoccupied region — skip

            var disposition = GetDisposition(pair.Value);
            if (disposition == ArchmageDisposition.Unknown ||
                disposition == ArchmageDisposition.Neutral)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Sets an archmage's disposition. Guards against downgrading
    /// a resolved state (e.g., you can't un-unite someone).
    /// </summary>
    public void SetDisposition(string archmageid, ArchmageDisposition newDisposition)
    {
        var current = GetDisposition(archmageid);

        // Don't downgrade resolved states
        bool isResolved = current == ArchmageDisposition.Allied ||
                          current == ArchmageDisposition.Coerced ||
                          current == ArchmageDisposition.Overthrown ||
                          current == ArchmageDisposition.Corrupted;

        if (isResolved && newDisposition == ArchmageDisposition.Neutral)
            return;

        Dispositions[archmageid] = newDisposition;
    }

    /// <summary>
    /// Advances corruption in a region by 1. If corruption reaches 3
    /// and the archmage is still Neutral or Unknown, marks them Corrupted.
    /// Returns true if an archmage was newly corrupted.
    /// </summary>
    public bool AdvanceCorruption(string regionId)
    {
        int current = GetCorruption(regionId);
        if (current >= 3)
            return false; // already maxed

        CorruptionLevels[regionId] = current + 1;

        if (CorruptionLevels[regionId] >= 3)
        {
            string archmageid = GetArchmageForRegion(regionId);
            if (!string.IsNullOrEmpty(archmageid))
            {
                var disposition = GetDisposition(archmageid);
                if (disposition == ArchmageDisposition.Unknown ||
                    disposition == ArchmageDisposition.Neutral)
                {
                    Dispositions[archmageid] = ArchmageDisposition.Corrupted;
                    return true; // newly corrupted
                }
            }
        }

        return false;
    }
}
