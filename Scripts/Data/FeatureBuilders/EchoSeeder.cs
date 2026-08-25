using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// EchoSeeder.cs
//
// Purpose:        Worldgen pass that samples permanent records
//                 (DealRecords, HonoredDead, LoopHistory) and
//                 sets echo_*_eligible flags on the current
//                 CycleState.WorldFlags, so flag-gated echo
//                 encounters surface in the narrative pool.
//                 Also caches substitution data (names, counts)
//                 for save-data text templating in encounter
//                 bodies ({dead_companion_name}, {cycle_count}).
// Layer:          Data / seeder
// Collaborators:  EternalLedger.cs (data sources),
//                 CycleState.cs (WorldFlags target),
//                 EncounterAssembler.cs (reads cached subs),
//                 CampusScreen.cs (call site: EnsureCycleWorld)
// See:            quest_hooks_compendium_v1.md §5 (echoes),
//                 quest_system_narrative_spec_v1.md §6b
// ============================================================

/// <summary>Worldgen pass: sample the loom's permanent records and seed
/// echo_*_eligible flags into the current timeline's WorldFlags. Each echo
/// is gated on (a) a prerequisite record existing in the ledger and (b) the
/// corresponding echo_*_seen Eternal flag NOT being set (an echo that has
/// already been experienced is not re-seeded verbatim).</summary>
public static class EchoSeeder
{
    // ── Cached substitution values for text templating ──────────────────
    // Populated by Seed(), consumed by EncounterAssembler.ResolveSaveTokens.
    // Cleared at the start of each Seed() call (one set per cycle).
    private static readonly Dictionary<string, string> _subs = new();

    /// <summary>Read-only view of cached substitution values. Keys are bare
    /// token names (no braces): dead_companion_name, cycle_count, etc.</summary>
    public static IReadOnlyDictionary<string, string> Substitutions => _subs;

    /// <summary>Run the echo seeder against the current save state. Call once
    /// per cycle start, after world generation (CampusScreen.EnsureCycleWorld).
    /// Sets eligible flags on <paramref name="save"/>.Cycle.WorldFlags and
    /// caches substitution data for encounter text templating.</summary>
    public static void Seed(GuildSaveData save)
    {
        if (save?.Ledger == null || save.Cycle == null)
        {
            GD.Print("EchoSeeder: no save/ledger/cycle. Skipping.");
            return;
        }

        _subs.Clear();

        var ledger = save.Ledger;
        var flags = save.Cycle.WorldFlags;
        var metaFlags = ledger.MetaNarrativeFlags;
        int seeded = 0;

        // ── 5.1 The Standing Debt: any DealRecord exists ───────────────
        if (ledger.DealRecords != null && ledger.DealRecords.Count > 0)
        {
            if (TrySeed("echo_standing_debt_eligible", metaFlags, "echo_standing_debt_seen", flags))
                seeded++;

            // Cache the NPC name from the most recent deal for templating
            var latestDeal = ledger.DealRecords[ledger.DealRecords.Count - 1];
            _subs["deal_npc_name"] = latestDeal.NpcName ?? "a merchant";
            _subs["deal_faction"] = latestDeal.FactionId ?? "";
        }

        // ── 5.2 A Stranger's Shrine: HonoredDead with WasAlly ─────────
        HonoredDeadRecord chosenDead = null;
        if (ledger.HonoredDead != null)
        {
            foreach (var dead in ledger.HonoredDead)
            {
                if (dead.WasAlly)
                {
                    chosenDead = dead;
                    break;
                }
            }
        }
        if (chosenDead != null)
        {
            if (TrySeed("echo_strangers_shrine_eligible", metaFlags, "echo_strangers_shrine_seen", flags))
                seeded++;
            _subs["dead_companion_name"] = chosenDead.Name ?? "a fallen friend";
            _subs["dead_companion_school"] = chosenDead.School ?? "";
            // HonoredDeadService stores the region ID (CombatManager passes
            // EncounterContextCarrier.Current.RegionId), so the raw value is
            // "hollow_mire", not "The Hollow Mire". Substituting it straight
            // into prose puts a database key in front of the player. Resolve
            // it through RegionLoader; fall back to a phrase that reads in a
            // sentence when the id is missing or unknown.
            _subs["dead_companion_region"] = DisplayRegion(chosenDead.RegionName);
        }

        // ── 5.3 The Shape of the Scar: any fragment trial passed ───────
        if (metaFlags != null)
        {
            foreach (var f in metaFlags)
            {
                if (f.StartsWith("fragment_") && f.EndsWith("_trial_passed"))
                {
                    if (TrySeed("echo_shape_of_scar_eligible", metaFlags, "echo_shape_of_scar_seen", flags))
                        seeded++;
                    break;
                }
            }
        }

        // ── 5.4 The Song Nobody Wrote: LoopRecord with Convergence ─────
        LoopRecord convergenceRecord = null;
        if (ledger.LoopHistory != null)
        {
            foreach (var lr in ledger.LoopHistory)
            {
                if (lr.Outcome == "Victory" || lr.Outcome == "ConvergenceDefeat")
                {
                    convergenceRecord = lr;
                    // Take the most recent one
                }
            }
        }
        if (convergenceRecord != null)
        {
            if (TrySeed("echo_song_nobody_wrote_eligible", metaFlags, "echo_song_nobody_wrote_seen", flags))
                seeded++;
            _subs["convergence_outcome"] = convergenceRecord.Outcome ?? "";
            _subs["convergence_outcome_phrase"] = OutcomePhrase(convergenceRecord.Outcome);
            _subs["convergence_school"] = convergenceRecord.School ?? "";
        }

        // ── 5.5 The Guestbook: any completed campus-restoration quest ──
        if (metaFlags != null && metaFlags.Any(f => f.StartsWith("campus_") && f.EndsWith("_complete")))
        {
            if (TrySeed("echo_guestbook_eligible", metaFlags, "echo_guestbook_seen", flags))
                seeded++;
        }

        // ── 5.6 The Style: HonoredDead with a CompanionId ─────────────
        HonoredDeadRecord companionDead = null;
        if (ledger.HonoredDead != null)
        {
            foreach (var dead in ledger.HonoredDead)
            {
                if (!string.IsNullOrEmpty(dead.CompanionId))
                {
                    companionDead = dead;
                    break;
                }
            }
        }
        if (companionDead != null)
        {
            if (TrySeed("echo_the_style_eligible", metaFlags, "echo_the_style_seen", flags))
                seeded++;
            _subs["style_companion_name"] = companionDead.Name ?? "a lost warrior";
            _subs["style_companion_id"] = companionDead.CompanionId ?? "";
        }

        // ── 5.7 The Village That Always Burns: 2+ LoopRecords ──────────
        if (ledger.LoopHistory != null && ledger.LoopHistory.Count >= 2)
        {
            if (TrySeed("echo_village_burns_eligible", metaFlags, "echo_village_burns_seen", flags))
                seeded++;
        }

        // ── 5.8 Kept Notes: Astrologer dossier at 2+ hints ────────────
        if (metaFlags != null)
        {
            int astroHints = 0;
            foreach (var f in metaFlags)
                if (f.StartsWith("dossier_astrologer_hint")) astroHints++;
            if (astroHints >= 2)
            {
                if (TrySeed("echo_kept_notes_eligible", metaFlags, "echo_kept_notes_seen", flags))
                    seeded++;
            }
        }

        // ── 5.9 Unfinished Business: an archived Timeline quest ─────────
        // SaveManager.ArchiveUnfinishedQuests already records every Timeline
        // quest left Active when a cycle ended. Nothing read it until now: the
        // quest spec promised this echo family and no flag was ever emitted for
        // it. Prefer the record that got furthest before the loom took it.
        if (ledger.UnfinishedBusiness != null && ledger.UnfinishedBusiness.Count > 0)
        {
            UnfinishedQuestRecord best = null;
            foreach (var rec in ledger.UnfinishedBusiness)
                if (best == null || rec.ObjectivesDone > best.ObjectivesDone)
                    best = rec;
            if (best != null)
            {
                if (TrySeed("echo_unfinished_business_eligible", metaFlags, "echo_unfinished_business_seen", flags))
                    seeded++;
                _subs["unfinished_quest_title"] = string.IsNullOrEmpty(best.Title) ? "something you started" : best.Title;
                _subs["unfinished_objectives_done"] = best.ObjectivesDone.ToString();
                _subs["unfinished_objectives_total"] = best.ObjectivesTotal.ToString();
                _subs["unfinished_cycle"] = best.CycleNumber.ToString();
            }
        }

        // ── 5.10 / 5.11 The table you left: walkaways and collapses ─────
        // DealRecord.Outcome distinguishes "Signed" / "WalkedAway" / "TheyLeft" /
        // "Collapsed". 5.1 above fires on ANY deal record and so cannot tell the
        // difference; these two can, and a negotiation you blew up should not
        // echo the same way as one you closed.
        if (ledger.DealRecords != null)
        {
            DealRecord walked = null, collapsed = null;
            foreach (var d in ledger.DealRecords)
            {
                if (walked == null && (d.Outcome == "WalkedAway" || d.Outcome == "TheyLeft")) walked = d;
                if (collapsed == null && d.Outcome == "Collapsed") collapsed = d;
            }
            if (walked != null)
            {
                if (TrySeed("echo_walked_away_eligible", metaFlags, "echo_walked_away_seen", flags))
                    seeded++;
                _subs["walkaway_npc_name"] = string.IsNullOrEmpty(walked.NpcName) ? "someone across a table" : walked.NpcName;
            }
            if (collapsed != null)
            {
                if (TrySeed("echo_table_collapsed_eligible", metaFlags, "echo_table_collapsed_seen", flags))
                    seeded++;
                _subs["collapsed_npc_name"] = string.IsNullOrEmpty(collapsed.NpcName) ? "someone across a table" : collapsed.NpcName;
            }
        }

        // ── 5.12 The Honoured Enemy: HonoredDead with WasAlly false ─────
        // 5.2 and 5.6 both look at the friendly dead. The Ossuary also holds
        // people who died fighting you and were honoured anyway, and nothing
        // has ever surfaced them.
        HonoredDeadRecord honoredEnemy = null;
        if (ledger.HonoredDead != null)
        {
            foreach (var dead in ledger.HonoredDead)
            {
                if (!dead.WasAlly) { honoredEnemy = dead; break; }
            }
        }
        if (honoredEnemy != null)
        {
            if (TrySeed("echo_honored_enemy_eligible", metaFlags, "echo_honored_enemy_seen", flags))
                seeded++;
            _subs["honored_enemy_name"] = string.IsNullOrEmpty(honoredEnemy.Name) ? "someone you killed" : honoredEnemy.Name;
            _subs["honored_enemy_school"] = honoredEnemy.School ?? "";
            _subs["honored_enemy_region"] = DisplayRegion(honoredEnemy.RegionName);
        }

        // ── 5.13 The Second Tongue: Fluency in a school you are not ─────
        if (ledger.SchoolMastery != null)
        {
            string current = save.Cycle.SelectedSchool ?? "";
            foreach (var kv in ledger.SchoolMastery)
            {
                if (kv.Key == current) continue;
                if (kv.Value == null || kv.Value.Points < SchoolMasteryService.FluencyThreshold) continue;
                if (TrySeed("echo_fluent_tongue_eligible", metaFlags, "echo_fluent_tongue_seen", flags))
                    seeded++;
                _subs["fluent_school"] = kv.Key;
                break;
            }
        }

        // ── 5.14 The Terms Everyone Quotes: a five-star deal ────────────
        if (ledger.DeedCounts != null &&
            ledger.DeedCounts.TryGetValue("negotiation_five_star_deal", out int fiveStars) && fiveStars > 0)
        {
            if (TrySeed("echo_famous_terms_eligible", metaFlags, "echo_famous_terms_seen", flags))
                seeded++;
            _subs["five_star_count"] = fiveStars.ToString();
        }

        // ── Global substitution values ──────────────────────────────────
        _subs["cycle_count"] = (ledger.LoopHistory?.Count ?? 0).ToString();
        _subs["cycle_number"] = (save.Cycle.CycleNumber).ToString();
        _subs["guild_name"] = ledger.GuildName ?? "the guild";

        GD.Print($"EchoSeeder: seeded {seeded} echo flag(s), cached {_subs.Count} substitution(s).");
    }

    /// <summary>Turn a stored region ID into something that can appear in a
    /// sentence. Empty or unrecognised ids become "a place this world has
    /// forgotten" rather than an empty gap or a raw key.</summary>
    private static string DisplayRegion(string regionId)
    {
        if (string.IsNullOrEmpty(regionId)) return "a place this world has forgotten";
        var def = RegionLoader.LoadOrDefault(regionId);
        return string.IsNullOrEmpty(def?.DisplayName) ? regionId : def.DisplayName;
    }

    /// <summary>Prose form of a LoopRecord.Outcome. The raw values ("Victory",
    /// "ConvergenceDefeat") are storage keys and must never reach a player-facing
    /// encounter body; authored text should use {convergence_outcome_phrase}.</summary>
    private static string OutcomePhrase(string outcome) => outcome switch
    {
        "Victory" => "a victory of some description",
        "ConvergenceDefeat" => "an ending at the Convergence",
        "CorruptionLoss" => "an ending that came from the inside",
        "Abandoned" => "a thing left unfinished",
        _ => "an ending",
    };

    /// <summary>Set an eligible flag on WorldFlags if the seen flag is NOT present
    /// in MetaNarrativeFlags. Returns true if the flag was set.</summary>
    private static bool TrySeed(string eligibleFlag, List<string> metaFlags,
                                 string seenFlag, HashSet<string> worldFlags)
    {
        // Don't re-seed echoes that have already been experienced
        if (metaFlags != null && metaFlags.Contains(seenFlag))
            return false;

        // Don't double-set within the same cycle
        if (worldFlags.Contains(eligibleFlag))
            return false;

        worldFlags.Add(eligibleFlag);
        return true;
    }
}
