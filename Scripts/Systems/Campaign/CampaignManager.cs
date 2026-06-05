using Godot;
using System.Collections.Generic;

// ============================================================
// CampaignManager.cs
//
// Purpose:        Static campaign-layer controller. Owns the
//                 Chronomancer's corruption clock — the global
//                 step counter that advances regional corruption
//                 over the course of the entire campaign,
//                 independent of any single run. As the player
//                 spends steps (in any region, across any number
//                 of runs), the Astrologer's influence creeps
//                 toward the archmagi the player has not yet
//                 reached. Reach them too late and they fall.
// Layer:          System
// Collaborators:  CampaignState.cs (the data it mutates),
//                 OverworldRunManager.cs (calls TickCorruption
//                   on every step + OnRegionEntered on run start),
//                 SaveManager.cs (persists on corruption events)
// See:            README §5 — Campaign Layer
// ============================================================

/// <summary>Static controller for campaign-wide systems. Currently owns the corruption clock; later will host final-battle assembly and mentor-intel queries.</summary>
public static class CampaignManager
{
    /// <summary>
    /// Steps added to the global clock when the player returns from combat.
    /// Combat takes time, so the Astrologer's influence advances while the
    /// player fights. Tune alongside CampaignState.CorruptionTickInterval.
    /// </summary>
    public const int CombatStepDebt = 4;

    // ════════════════════════════════════════════════════════════════════════
    // Corruption clock
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advances the campaign's global step clock by the given number of steps
    /// and resolves any corruption ticks that become due. Call on every player
    /// step in the overworld, and once on combat return with the step debt.
    /// </summary>
    public static void TickCorruption(int steps)
    {
        var campaign = SaveManager.ActiveSave?.Campaign;
        if (campaign == null || steps <= 0)
            return;

        campaign.GlobalStepCount += steps;

        int elapsed = campaign.GlobalStepCount - campaign.LastCorruptionTickAt;
        int ticksDue = elapsed / campaign.CorruptionTickInterval;
        if (ticksDue <= 0)
            return;

        bool anyChange = false;

        for (int i = 0; i < ticksDue; i++)
        {
            string regionId = SelectRegionToCorrupt(campaign);
            if (regionId == null)
                break; // nothing left to corrupt — campaign is fully resolved

            bool newlyCorrupted = campaign.AdvanceCorruption(regionId);
            anyChange = true;

            int level = campaign.GetCorruption(regionId);
            string archmageId = campaign.GetArchmageForRegion(regionId);

            if (newlyCorrupted)
            {
                GD.Print($"[Corruption] '{archmageId}' in '{regionId}' has fallen to " +
                         $"the Astrologer. Disposition → Corrupted.");
            }
            else
            {
                GD.Print($"[Corruption] '{regionId}' corruption advanced to {level}/3 " +
                         $"(archmage '{archmageId}').");
            }
        }

        // Snap the tick marker to the current step so the next interval is clean.
        campaign.LastCorruptionTickAt = campaign.GlobalStepCount;

        if (anyChange)
            SaveManager.Save();
    }

    /// <summary>
    /// Selects which region the Astrologer's influence advances next.
    /// Priority: unvisited archmagi (Unknown disposition) first — the
    /// Astrologer reaches the undefended before the contested — then
    /// visited-but-unresolved (Neutral). Resolved archmagi (Allied,
    /// Coerced, Overthrown, Corrupted) and fully-corrupted regions are
    /// never selected.
    /// </summary>
    private static string SelectRegionToCorrupt(CampaignState campaign)
    {
        var unknown = new List<string>();
        var neutral = new List<string>();

        foreach (var pair in campaign.RegionArchmageMap)
        {
            string regionId = pair.Key;
            string archmageId = pair.Value;

            if (string.IsNullOrEmpty(archmageId))
                continue; // region has no archmage — nothing to corrupt
            if (campaign.GetCorruption(regionId) >= 3)
                continue; // already maxed

            switch (campaign.GetDisposition(archmageId))
            {
                case ArchmageDisposition.Unknown:
                    unknown.Add(regionId);
                    break;
                case ArchmageDisposition.Neutral:
                    neutral.Add(regionId);
                    break;
                    // Allied / Coerced / Overthrown / Corrupted → protected
            }
        }

        if (unknown.Count > 0)
            return unknown[(int)(GD.Randi() % (uint)unknown.Count)];
        if (neutral.Count > 0)
            return neutral[(int)(GD.Randi() % (uint)neutral.Count)];

        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Region entry
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Marks the archmage resident in the given region as Neutral (aware of
    /// the player) if they were previously Unknown. Call when a run begins in
    /// a region. This downgrades the region's corruption priority — the
    /// Astrologer targets the regions the player has NOT yet visited first.
    /// </summary>
    public static void OnRegionEntered(string regionId)
    {
        var campaign = SaveManager.ActiveSave?.Campaign;
        if (campaign == null || string.IsNullOrEmpty(regionId))
            return;

        string archmageId = campaign.GetArchmageForRegion(regionId);
        if (string.IsNullOrEmpty(archmageId))
            return; // unoccupied region

        if (campaign.GetDisposition(archmageId) == ArchmageDisposition.Unknown)
        {
            campaign.SetDisposition(archmageId, ArchmageDisposition.Neutral);
            GD.Print($"[Campaign] Entered '{regionId}'. Archmage '{archmageId}' " +
                     $"is now aware of you (Neutral).");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Query helpers (for HUD / mentor panel later)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Steps remaining until the next corruption tick. For HUD display.</summary>
    public static int StepsUntilNextCorruption()
    {
        var campaign = SaveManager.ActiveSave?.Campaign;
        if (campaign == null)
            return -1;

        int elapsed = campaign.GlobalStepCount - campaign.LastCorruptionTickAt;
        int remaining = campaign.CorruptionTickInterval - elapsed;
        return Mathf.Max(0, remaining);
    }
}
