using Godot;
using System.Collections.Generic;

// ============================================================
// CompanionArcTracker.cs
//
// Purpose:        Derives companion arc state from save flags.
//                 No separate arc-state store — reads flags
//                 (the live-projection pattern) to determine
//                 current stage, available missions, and
//                 remembrance eligibility. Provides query
//                 methods for the quest log and encounter system.
// Layer:          Tracker
// Collaborators:  CompanionArcData.cs (definitions),
//                 CompanionArcLoader.cs (loading),
//                 CompanionRoster.cs (party queries),
//                 QuestLogView.cs (mission rendering),
//                 NarrativeEncounterLoader.cs (encounter gating)
// See:            quest_hooks_compendium_v1.md §3
// ============================================================

/// <summary>Arc status for a single companion, derived from flags.</summary>
public class CompanionArcStatus
{
    public string CompanionId = "";
    public string CompanionName = "";
    public string ArcName = "";

    /// <summary>Whether the companion has been recruited this cycle.</summary>
    public bool IsRecruited = false;

    /// <summary>Current arc stage (0 = not started, 1+ = in progress,
    /// Stages.Count+1 = arc complete).</summary>
    public int CurrentStage = 0;

    /// <summary>Total number of stages in this arc.</summary>
    public int TotalStages = 0;

    /// <summary>Whether the arc is fully complete.</summary>
    public bool IsComplete = false;

    /// <summary>The next stage's data, if one is available (null if complete
    /// or if the next stage's RequiredFlag is unmet).</summary>
    public CompanionArcStage NextStage = null;

    /// <summary>Whether the next stage has a remembrance variant available
    /// (the player has the remembrance flag from a prior cycle).</summary>
    public bool HasRemembranceBranch = false;

    /// <summary>Whether the companion is currently in the active party.</summary>
    public bool IsInParty = false;

    /// <summary>True when the arc was completed in a PRIOR timeline and a
    /// reprise encounter exists — the one-beat shorthand replaces the ladder.</summary>
    public bool HasReprise = false;

    /// <summary>Whether the companion has been anchored in the Hall.</summary>
    public bool IsAnchored = false;
}

/// <summary>Companion arc tracker — stateless query layer over save flags
/// and arc definitions. All methods are pure reads.</summary>
public static class CompanionArcTracker
{
    /// <summary>Get the arc status for a single companion.</summary>
    public static CompanionArcStatus StatusOf(string companionId, GuildSaveData save)
    {
        if (save == null || string.IsNullOrEmpty(companionId))
            return null;

        var arc = CompanionArcLoader.Load(companionId);
        if (arc == null) return null;

        var status = new CompanionArcStatus
        {
            CompanionId = companionId,
            ArcName = arc.ArcName,
            TotalStages = arc.Stages?.Count ?? 0,
        };

        // Look up companion name from roster
        var companions = save.Cycle?.Companions;
        if (companions != null)
        {
            foreach (var c in companions)
            {
                if (c.Id == companionId)
                {
                    status.CompanionName = c.Name;
                    status.IsRecruited = c.IsRecruited;
                    break;
                }
            }
        }

        // Check recruitment via flag (fallback if companion not in roster yet)
        if (!status.IsRecruited && !string.IsNullOrEmpty(arc.RecruitFlag))
            status.IsRecruited = save.HasFlag(arc.RecruitFlag);

        // Derive current stage from completion flags
        if (status.IsRecruited && arc.Stages != null)
        {
            int stage = 0;
            foreach (var s in arc.Stages)
            {
                if (!string.IsNullOrEmpty(s.CompletionFlag)
                    && save.HasFlag(s.CompletionFlag))
                    stage = s.Stage;
                else
                    break;
            }
            status.CurrentStage = stage;
        }

        // Arc complete?
        status.IsComplete = status.CurrentStage >= status.TotalStages;
        if (!string.IsNullOrEmpty(arc.ArcCompleteFlag))
            status.IsComplete = status.IsComplete || save.HasFlag(arc.ArcCompleteFlag);

        // Next available stage
        if (!status.IsComplete && status.IsRecruited && arc.Stages != null)
        {
            int nextIdx = status.CurrentStage; // 0-based index for stage CurrentStage+1
            if (nextIdx < arc.Stages.Count)
            {
                var next = arc.Stages[nextIdx];
                // Check if any external gate is met
                bool gateOk = string.IsNullOrEmpty(next.RequiredFlag)
                              || save.HasFlag(next.RequiredFlag);
                if (gateOk)
                {
                    status.NextStage = next;

                    // Check remembrance branch
                    if (!string.IsNullOrEmpty(next.RemembranceFlag)
                        && !string.IsNullOrEmpty(next.RemembranceEncounterId))
                    {
                        // Remembrance flag from a PRIOR cycle (MetaNarrativeFlags)
                        var metaFlags = save.Ledger?.MetaNarrativeFlags;
                        status.HasRemembranceBranch = metaFlags != null
                            && metaFlags.Contains(next.RemembranceFlag);
                    }
                }
            }
        }

        // Reprise: arc finished in a prior timeline + reprise authored + not
        // yet complete this cycle -> the shorthand replaces the ladder.
        if (!status.IsComplete && status.IsRecruited &&
            !string.IsNullOrEmpty(arc.RepriseEncounterId) &&
            !string.IsNullOrEmpty(arc.ArcCompleteMetaFlag))
        {
            var repriseMeta = save.Ledger?.MetaNarrativeFlags;
            status.HasReprise = repriseMeta != null &&
                                repriseMeta.Contains(arc.ArcCompleteMetaFlag);
        }

        // Party membership
        var partyIds = save.Cycle?.ActivePartyCompanionIds;
        if (partyIds != null)
            status.IsInParty = partyIds.Contains(companionId);

        // Hall anchoring
        if (!string.IsNullOrEmpty(arc.HallAnchorFlag))
        {
            var metaFlags = save.Ledger?.MetaNarrativeFlags;
            status.IsAnchored = metaFlags != null && metaFlags.Contains(arc.HallAnchorFlag);
        }

        return status;
    }

    /// <summary>Get arc statuses for ALL recruited companions (for the quest log).
    /// Includes companions with available missions regardless of party membership.</summary>
    public static List<CompanionArcStatus> AllRecruitedArcs(GuildSaveData save)
    {
        var results = new List<CompanionArcStatus>();
        if (save?.Cycle?.Companions == null) return results;

        foreach (var companion in save.Cycle.Companions)
        {
            if (!companion.IsRecruited) continue;
            var status = StatusOf(companion.Id, save);
            if (status != null) results.Add(status);
        }
        return results;
    }

    /// <summary>Get arc statuses with an available next stage (active missions).
    /// Used by the quest log to prompt the player about companion missions
    /// regardless of whether the companion is in the current party.</summary>
    public static List<CompanionArcStatus> AvailableMissions(GuildSaveData save)
    {
        var results = new List<CompanionArcStatus>();
        foreach (var status in AllRecruitedArcs(save))
        {
            if (status.NextStage != null && !status.IsComplete)
                results.Add(status);
        }
        return results;
    }

    /// <summary>Get the encounter id for a companion's current stage, accounting
    /// for remembrance branches. Returns null if no stage is available or if the
    /// stage requires party presence and the companion is not in party.</summary>
    public static string GetStageEncounterId(string companionId, GuildSaveData save,
                                              bool isExpedition = true)
    {
        var status = StatusOf(companionId, save);
        if (status?.NextStage == null) return null;

        // Reprise (2026-07-22): the shorthand supersedes the ladder — any
        // location, no party requirement. You have walked this road before.
        if (status.HasReprise)
        {
            var rArc = CompanionArcLoader.Load(companionId);
            if (!string.IsNullOrEmpty(rArc?.RepriseEncounterId))
                return rArc.RepriseEncounterId;
        }

        var stage = status.NextStage;

        // Check party requirement for expedition encounters
        if (isExpedition && stage.RequiresParty && !status.IsInParty)
            return null;

        // Check location compatibility
        if (isExpedition && stage.Location == "campus") return null;
        if (!isExpedition && stage.Location == "expedition") return null;

        // Use remembrance variant if available
        if (status.HasRemembranceBranch)
            return stage.RemembranceEncounterId;

        return stage.EncounterId;
    }

    /// <summary>After a companion arc encounter completes, advance the arc stage.
    /// Sets the completion flag on WorldFlags, the remembrance flag on
    /// MetaNarrativeFlags, applies loyalty delta, and syncs ArcStage on the
    /// Companion object. Call from the encounter resolution path.</summary>
    public static void CompleteStage(string companionId, GuildSaveData save)
    {
        if (save == null || string.IsNullOrEmpty(companionId)) return;

        var arc = CompanionArcLoader.Load(companionId);
        var status = StatusOf(companionId, save);
        if (arc == null || status == null || status.NextStage == null) return;

        var stage = status.NextStage;

        // Set timeline completion flag
        if (!string.IsNullOrEmpty(stage.CompletionFlag))
            save.Cycle?.SetFlag(stage.CompletionFlag);

        // Set eternal remembrance flag
        if (!string.IsNullOrEmpty(stage.RemembranceFlag))
        {
            var metaFlags = save.Ledger?.MetaNarrativeFlags;
            if (metaFlags != null && !metaFlags.Contains(stage.RemembranceFlag))
                metaFlags.Add(stage.RemembranceFlag);
        }

        // Set any additional meta flags
        if (stage.SetMetaFlags != null)
        {
            var metaFlags = save.Ledger?.MetaNarrativeFlags;
            if (metaFlags != null)
                foreach (var f in stage.SetMetaFlags)
                    if (!metaFlags.Contains(f)) metaFlags.Add(f);
        }

        // Apply loyalty delta to the companion object
        if (stage.LoyaltyDelta != 0)
        {
            var companions = save.Cycle?.Companions;
            if (companions != null)
                foreach (var c in companions)
                    if (c.Id == companionId)
                    {
                        c.Loyalty = System.Math.Clamp(c.Loyalty + stage.LoyaltyDelta, 0, 100);
                        break;
                    }
        }

        // Sync ArcStage on the Companion object
        int newStage = stage.Stage;
        var comps = save.Cycle?.Companions;
        if (comps != null)
            foreach (var c in comps)
                if (c.Id == companionId)
                {
                    c.ArcStage = newStage;
                    break;
                }

        // Check if arc is now complete
        if (newStage >= (arc.Stages?.Count ?? 0))
        {
            if (!string.IsNullOrEmpty(arc.ArcCompleteFlag))
                save.Cycle?.SetFlag(arc.ArcCompleteFlag);
            if (!string.IsNullOrEmpty(arc.ArcCompleteMetaFlag))
            {
                var metaFlags = save.Ledger?.MetaNarrativeFlags;
                if (metaFlags != null && !metaFlags.Contains(arc.ArcCompleteMetaFlag))
                    metaFlags.Add(arc.ArcCompleteMetaFlag);
            }

            // Record a renown anchor for cross-cycle memory
            save.Ledger?.RenownAnchors?.Add(new RenownAnchor
            {
                SubjectId = companionId,
                MilestoneId = "ArcComplete",
                CycleAnchored = save.Cycle?.CycleNumber ?? 0,
            });

            GD.Print($"CompanionArcTracker: arc complete for '{companionId}'.");
        }

        GD.Print($"CompanionArcTracker: '{companionId}' completed stage {newStage}.");
    }

    // ── Encounter delivery seam (Step 9 follow-up, 2026-07-22) ──────────
    // Maps encounter ids (stage + remembrance variants) back to their
    // companion so the encounter loader can gate arc beats at pick time and
    // the resolution paths can advance the arc when one completes.

    private static Dictionary<string, string> _encounterIndex;

    private static Dictionary<string, string> EncounterIndex()
    {
        if (_encounterIndex != null) return _encounterIndex;
        _encounterIndex = new Dictionary<string, string>();
        foreach (var pair in CompanionArcLoader.LoadAll())
        {
            var arc = pair.Value;
            if (arc?.Stages == null) continue;
            foreach (var s in arc.Stages)
            {
                if (!string.IsNullOrEmpty(s.EncounterId))
                    _encounterIndex[s.EncounterId] = arc.CompanionId;
                if (!string.IsNullOrEmpty(s.RemembranceEncounterId))
                    _encounterIndex[s.RemembranceEncounterId] = arc.CompanionId;
            }
            if (!string.IsNullOrEmpty(arc.RepriseEncounterId))
                _encounterIndex[arc.RepriseEncounterId] = arc.CompanionId;
        }
        return _encounterIndex;
    }

    /// <summary>Testing hook: drop the index so edited arc JSON re-maps.</summary>
    public static void ClearEncounterIndex() => _encounterIndex = null;

    /// <summary>True when the id belongs to some companion's arc stage.</summary>
    public static bool IsStageEncounter(string encounterId) =>
        !string.IsNullOrEmpty(encounterId) && EncounterIndex().ContainsKey(encounterId);

    /// <summary>Pick-time gate for the expedition encounter pool: non-arc
    /// encounters always pass; an arc encounter passes only when it is the
    /// owning companion's CURRENT stage encounter in expedition context
    /// (recruited, prior stages complete, party present when required,
    /// correct remembrance variant).</summary>
    public static bool StageEncounterEligible(string encounterId, GuildSaveData save)
    {
        if (string.IsNullOrEmpty(encounterId)) return true;
        if (!EncounterIndex().TryGetValue(encounterId, out var companionId)) return true;
        return GetStageEncounterId(companionId, save, isExpedition: true) == encounterId;
    }

    /// <summary>Called by both encounter hosts after a narrative encounter
    /// resolves: if the encounter is the owning companion's current stage
    /// (either variant, any location), advance the arc. Returns the refreshed
    /// status for toasting ("Wren — The Room That Waited complete"), or null
    /// when the encounter is not an arc stage / not current.</summary>
    public static CompanionArcStatus TryCompleteByEncounter(string encounterId, GuildSaveData save)
    {
        if (save == null || string.IsNullOrEmpty(encounterId)) return null;
        if (!EncounterIndex().TryGetValue(encounterId, out var companionId)) return null;

        var status = StatusOf(companionId, save);
        if (status?.NextStage == null) return null;

        // Reprise: one beat completes every remaining stage (guarded against
        // authoring loops by the stage count).
        var rArc = CompanionArcLoader.Load(companionId);
        if (status.HasReprise && rArc != null && rArc.RepriseEncounterId == encounterId)
        {
            int guard = rArc.Stages?.Count ?? 0;
            while (guard-- > 0)
            {
                var cur = StatusOf(companionId, save);
                if (cur == null || cur.IsComplete || cur.NextStage == null) break;
                CompleteStage(companionId, save);
            }
            return StatusOf(companionId, save);
        }

        var stage = status.NextStage;
        bool matches = stage.EncounterId == encounterId ||
                       (!string.IsNullOrEmpty(stage.RemembranceEncounterId) &&
                        stage.RemembranceEncounterId == encounterId);
        if (!matches) return null;

        CompleteStage(companionId, save);
        return StatusOf(companionId, save);
    }
}
