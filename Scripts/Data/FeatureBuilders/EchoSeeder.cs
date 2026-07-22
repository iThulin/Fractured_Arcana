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
            GD.Print("EchoSeeder: no save/ledger/cycle — skipping.");
            return;
        }

        _subs.Clear();

        var ledger = save.Ledger;
        var flags = save.Cycle.WorldFlags;
        var metaFlags = ledger.MetaNarrativeFlags;
        int seeded = 0;

        // ── 5.1 The Standing Debt — any DealRecord exists ───────────────
        if (ledger.DealRecords != null && ledger.DealRecords.Count > 0)
        {
            if (TrySeed("echo_standing_debt_eligible", metaFlags, "echo_standing_debt_seen", flags))
                seeded++;

            // Cache the NPC name from the most recent deal for templating
            var latestDeal = ledger.DealRecords[ledger.DealRecords.Count - 1];
            _subs["deal_npc_name"] = latestDeal.NpcName ?? "a merchant";
            _subs["deal_faction"] = latestDeal.FactionId ?? "";
        }

        // ── 5.2 A Stranger's Shrine — HonoredDead with WasAlly ─────────
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
            _subs["dead_companion_region"] = chosenDead.RegionName ?? "";
        }

        // ── 5.3 The Shape of the Scar — any fragment trial passed ───────
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

        // ── 5.4 The Song Nobody Wrote — LoopRecord with Convergence ─────
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
            _subs["convergence_school"] = convergenceRecord.School ?? "";
        }

        // ── 5.5 The Guestbook — any completed campus-restoration quest ──
        if (metaFlags != null && metaFlags.Any(f => f.StartsWith("campus_") && f.EndsWith("_complete")))
        {
            if (TrySeed("echo_guestbook_eligible", metaFlags, "echo_guestbook_seen", flags))
                seeded++;
        }

        // ── 5.6 The Style — HonoredDead with a CompanionId ─────────────
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

        // ── 5.7 The Village That Always Burns — 2+ LoopRecords ──────────
        if (ledger.LoopHistory != null && ledger.LoopHistory.Count >= 2)
        {
            if (TrySeed("echo_village_burns_eligible", metaFlags, "echo_village_burns_seen", flags))
                seeded++;
        }

        // ── 5.8 Kept Notes — Astrologer dossier at 2+ hints ────────────
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

        // ── Global substitution values ──────────────────────────────────
        _subs["cycle_count"] = (ledger.LoopHistory?.Count ?? 0).ToString();
        _subs["cycle_number"] = (save.Cycle.CycleNumber).ToString();
        _subs["guild_name"] = ledger.GuildName ?? "the guild";

        GD.Print($"EchoSeeder: seeded {seeded} echo flag(s), cached {_subs.Count} substitution(s).");
    }

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
