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

    // ── Sentiment (Step 8, quest_hooks_compendium §7) ─────────────────────
    /// <summary>Archmage id → sentiment value (−100 to +100). 0 = neutral.
    /// Positive = favoring the player, negative = drifting toward corruption.
    /// The continuous scale beneath the discrete Disposition enum — sentiment
    /// accumulates from player actions and corruption pressure; the final
    /// resolution (Allied/Coerced/Overthrown/Corrupted) happens when the
    /// player triggers a resolution encounter at the right threshold.
    /// Visible in the council overview tied to each archmage's kingdom.</summary>
    public Dictionary<string, int> Sentiments = new();

    /// <summary>Returns the current sentiment of an archmage (0 if not tracked).</summary>
    public int GetSentiment(string archmageid) =>
        Sentiments.TryGetValue(archmageid, out var s) ? s : 0;

    /// <summary>Shift an archmage's sentiment by <paramref name="delta"/>, applying
    /// the archmage's sway resistance from their definition. Returns the new
    /// sentiment value. Clamps to [−100, +100]. No-ops on already-resolved
    /// archmages (Allied/Coerced/Overthrown/Corrupted).</summary>
    public int ShiftSentiment(string archmageid, int delta)
    {
        // Don't shift already-resolved archmages
        var disp = GetDisposition(archmageid);
        if (disp == ArchmageDisposition.Allied || disp == ArchmageDisposition.Coerced
            || disp == ArchmageDisposition.Overthrown || disp == ArchmageDisposition.Corrupted)
            return GetSentiment(archmageid);

        // Apply sway resistance from archmage definition
        var def = ArchmageRegistry.Get(archmageid);
        float resistance = def?.SwayResistance ?? 0f;
        int adjusted = (int)(delta * (1f - resistance));
        if (adjusted == 0 && delta != 0) adjusted = delta > 0 ? 1 : -1; // floor to ±1

        int current = GetSentiment(archmageid);
        int result = System.Math.Clamp(current + adjusted, -100, 100);
        Sentiments[archmageid] = result;

        // Auto-transition to Neutral if they were Unknown and sentiment moved
        if (disp == ArchmageDisposition.Unknown && result != 0)
            Dispositions[archmageid] = ArchmageDisposition.Neutral;

        return result;
    }

    /// <summary>Returns the resolution options available for an archmage based
    /// on their current sentiment and corruption level. Used by resolution
    /// encounters to gate unite/coerce/overthrow choices.
    /// <para>When <paramref name="hasFlag"/> is supplied (save.HasFlag), Coerce
    /// additionally requires LEVERAGE: at least 2 revealed dossier hints
    /// (`dossier_{id}_hint_2` — hints reveal sequentially). Coercion is knowing
    /// where it hurts; the sentiment window alone is not leverage (Step 9
    /// gating ruling, 2026-07-22). Callers without flag access get the old
    /// sentiment-only behaviour.</para></summary>
    public (bool canUnite, bool canCoerce, bool canOverthrow) ResolutionOptions(
        string archmageid, System.Func<string, bool> hasFlag = null)
    {
        var def = ArchmageRegistry.Get(archmageid);
        if (def == null) return (false, false, true); // unknown archmage, only overthrow

        int sentiment = GetSentiment(archmageid);
        string regionId = GetRegionForArchmage(archmageid);
        int corruption = GetCorruption(regionId);

        bool canUnite = sentiment >= 40 && corruption <= def.MaxCorruptionForUnite;
        bool canCoerce = sentiment >= -20 && sentiment < 40
                         && corruption <= def.MaxCorruptionForCoerce
                         && (hasFlag == null || hasFlag(DossierService.HintFlag(archmageid, 2)));
        bool canOverthrow = true; // always available as the combat path

        return (canUnite, canCoerce, canOverthrow);
    }

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
