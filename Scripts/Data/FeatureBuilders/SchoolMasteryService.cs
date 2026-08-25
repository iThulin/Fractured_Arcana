using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// SchoolMasteryService.cs
//
// Purpose:        The award + read API for EternalLedger.SchoolMastery,
//                 the cross-cycle progression spine. The field, the
//                 SchoolMasteryTrack class, and GetMastery() have existed
//                 since the three-tier refactor, but had ZERO call sites:
//                 nothing ever awarded a point, so the dictionary
//                 serialized empty forever while the campus new-cycle
//                 screen promised the player their "mastery endures."
//                 This service is the missing half.
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.cs (SchoolMastery, GetMastery),
//                 ProgressionSweep.cs (the automatic writer),
//                 CampusExpeditionPanel.cs (the read surface),
//                 RegaliaService.cs (sibling permanent currency)
// See:            docs/progression_card_acquisition_v1.md §4
//
// ⚠ NAMING WARNING: TWO UNRELATED "MASTERY" SYSTEMS EXIST ⚠
//   • SchoolMastery (this file): cross-cycle, per-school, EternalLedger.
//     Feeds Fluency, Communion, and discipline declaration.
//   • CastMastery (CastMasteryTracker / CardMasteryThresholds): per-CARD
//     cast counts gating upgrade points. Completely unrelated.
//   Never write bare "mastery" in this codebase. See design doc §1c.
// ============================================================

/// <summary>
/// Awards and reads <see cref="EternalLedger.SchoolMastery"/>. All writes to
/// SchoolMastery should go through here so every award is logged with a reason.
/// SchoolMastery is invisible by nature, and an unlogged award is an undebuggable one.
/// </summary>
public static class SchoolMasteryService
{
    // ── Tuning ───────────────────────────────────────────────────────────
    // Anchor (shard_acquisition_spec_v1 §11): pick FluencyThreshold so a
    // dedicated one-school game earns Fluency around the time that school's
    // own fragment is done, i.e. it pays off on the SECOND school, not the first.

    /// <summary>Points at which Communion opens for a non-matching school (shard spec §4).</summary>
    public const int FluencyThreshold = 60;

    /// <summary>
    /// Points required (alongside a faculty source) to declare a discipline.
    ///
    /// Set to exactly two companion arc stages. Arc stage 2 is also the faculty
    /// threshold, so a single arcane companion reaching it satisfies BOTH legs at
    /// once, which is the design doc's §7e pacing verbatim: the first discipline
    /// is declarable within cycle one, off the first arcane companion recruit,
    /// rather than requiring a completed story or a full cycle of archmage work.
    /// </summary>
    public const int DeclarableThreshold = 8;

    // Award values, one table so the economy is legible in a single screen.
    public const int PointsCycleCompleted    = 25;
    public const int PointsFragmentClaimed   = 20;
    public const int PointsArchmageAllied    = 10;
    public const int PointsArchmageResolved  = 6;

    /// <summary>Per arc stage reached (1-4), not just the capstone (see DeclarableThreshold).</summary>
    public const int PointsCompanionArcStage = 4;

    public const int PointsTuition           = 2;

    /// <summary>Marginalia entry completed (marginalia_spec_v1 R6): pay scales
    /// with the rarity of the card the entry unlocks, symmetric with the kill
    /// thresholds (R2). Consumed via MarginaliaService.PointsFor.</summary>
    public const int PointsMarginaliaCommon   = 2;
    public const int PointsMarginaliaUncommon = 3;
    public const int PointsMarginaliaRare     = 5;

    // ── Milestone id helpers ─────────────────────────────────────────────
    public static string FluencyMilestone(string school) => $"fluent_{Norm(school)}";
    public static string DeclarableMilestone(string school) => $"declarable_{Norm(school)}";

    // ── Write ────────────────────────────────────────────────────────────

    /// <summary>
    /// Award SchoolMastery points. Returns the new total, or -1 if the call was
    /// rejected (null save, blank school, non-positive points). Every award logs
    /// its reason. Crossing a threshold stamps the corresponding milestone id.
    /// </summary>
    public static int Award(GuildSaveData save, string school, int points, string reason)
    {
        if (save?.Ledger == null)
        {
            GD.PrintErr("[SchoolMastery] Award called with no ledger. Ignored.");
            return -1;
        }
        if (string.IsNullOrWhiteSpace(school) || points <= 0)
            return -1;

        string key = Norm(school);
        save.Ledger.SchoolMastery ??= new Dictionary<string, SchoolMasteryTrack>();

        var track = save.Ledger.GetMastery(key);
        track.MilestoneIds ??= new List<string>();

        int before = track.Points;
        track.Points += points;

        GD.Print($"[SchoolMastery] {key} +{points} → {track.Points}  ({reason})");

        StampIfCrossed(track, before, DeclarableThreshold, DeclarableMilestone(key), key);
        StampIfCrossed(track, before, FluencyThreshold, FluencyMilestone(key), key);

        return track.Points;
    }

    private static void StampIfCrossed(SchoolMasteryTrack track, int before,
                                       int threshold, string milestoneId, string school)
    {
        if (before >= threshold || track.Points < threshold) return;
        if (track.MilestoneIds.Contains(milestoneId)) return;

        track.MilestoneIds.Add(milestoneId);
        GD.Print($"[SchoolMastery] MILESTONE {school}: {milestoneId} (at {track.Points} pts)");
    }

    /// <summary>Add a named milestone directly (for milestones not driven by a point threshold).</summary>
    public static void AddMilestone(GuildSaveData save, string school, string milestoneId)
    {
        if (save?.Ledger == null || string.IsNullOrWhiteSpace(school) ||
            string.IsNullOrWhiteSpace(milestoneId)) return;

        // GetMastery dereferences SchoolMastery without a guard, and a save
        // carrying "schoolMastery": null deserializes to null. Award() already
        // defends against this; so must this path.
        save.Ledger.SchoolMastery ??= new Dictionary<string, SchoolMasteryTrack>();

        var track = save.Ledger.GetMastery(Norm(school));
        track.MilestoneIds ??= new List<string>();
        if (track.MilestoneIds.Contains(milestoneId)) return;

        track.MilestoneIds.Add(milestoneId);
        GD.Print($"[SchoolMastery] MILESTONE {Norm(school)}: {milestoneId} (direct)");
    }

    // ── Read ─────────────────────────────────────────────────────────────

    /// <summary>SchoolMastery points for a school. 0 when untracked.</summary>
    public static int Points(GuildSaveData save, string school)
    {
        if (save?.Ledger?.SchoolMastery == null || string.IsNullOrWhiteSpace(school))
            return 0;
        return save.Ledger.SchoolMastery.TryGetValue(Norm(school), out var t) ? t.Points : 0;
    }

    public static int Points(GuildSaveData save, CardSchool school) =>
        Points(save, school.ToString());

    public static bool HasMilestone(GuildSaveData save, string school, string milestoneId)
    {
        if (save?.Ledger?.SchoolMastery == null) return false;
        return save.Ledger.SchoolMastery.TryGetValue(Norm(school), out var t)
               && t.MilestoneIds != null
               && t.MilestoneIds.Contains(milestoneId);
    }

    /// <summary>
    /// Fluency (shard_acquisition_spec_v1 §4): Communion is available when the
    /// player's current school matches the fragment's school, OR when permanent
    /// SchoolMastery in that school has reached <see cref="FluencyThreshold"/>.
    /// This is the read the shard gate encounter should call.
    /// </summary>
    public static bool IsFluent(GuildSaveData save, string school) =>
        Points(save, school) >= FluencyThreshold;

    /// <summary>
    /// The SchoolMastery leg of the faculty gate (design doc §7c). The faculty-source
    /// leg is evaluated separately; this is necessary, not sufficient.
    /// </summary>
    public static bool MeetsDeclarationThreshold(GuildSaveData save, string school) =>
        Points(save, school) >= DeclarableThreshold;

    /// <summary>Every school with a non-zero track, highest first. For display.</summary>
    public static List<(string School, int Points)> Ranked(GuildSaveData save)
    {
        if (save?.Ledger?.SchoolMastery == null)
            return new List<(string, int)>();

        return save.Ledger.SchoolMastery
            .Where(kvp => kvp.Value != null && kvp.Value.Points > 0)
            .Select(kvp => (kvp.Key, kvp.Value.Points))
            .OrderByDescending(t => t.Item2)
            .ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Canonical key form. Schools arrive from CardSchool.ToString(), from
    /// ArchmageDefinition.School, and from Companion.School, all of which are
    /// already PascalCase enum names, but normalising defends the dictionary
    /// against a stray lowercase JSON value silently creating a second track.
    /// </summary>
    private static string Norm(string school)
    {
        if (string.IsNullOrWhiteSpace(school)) return "";
        return Enum.TryParse<CardSchool>(school.Trim(), ignoreCase: true, out var s)
            ? s.ToString()
            : school.Trim();
    }
}
