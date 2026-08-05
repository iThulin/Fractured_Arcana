using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ProgressionSweep.cs
//
// Purpose:        The single automatic writer for SchoolMastery, Regalia,
//                 and Marginalia unlocks.
//                 Rather than bolting an award call onto each of the ~8
//                 scattered sites that resolve a fragment, a disposition, or
//                 a companion arc (six SetDisposition call sites across two
//                 1000+ line files alone), this sweep RECONCILES the
//                 already-persisted state against a set of "already paid"
//                 metaflags and awards whatever is outstanding.
//
//                 Consequences of that shape, all deliberate:
//                   • It cannot be missed — a new reward site needs no wiring.
//                   • It is self-healing — a save whose grant was lost to a
//                     crash gets it on the next write.
//                   • It is retroactive — an existing save that allied three
//                     archmagi before this system existed is paid on load.
//
// Layer:          Data / Feature builder
// Collaborators:  SchoolMasteryService.cs, RegaliaService.cs,
//                 SaveManager.Save() (the sole caller),
//                 ArchmageRegistry.cs (archmage → school),
//                 ShardZones.cs (the six fragment keys)
// See:            docs/progression_card_acquisition_v1.md §4, §6d, §8;
//                 docs/marginalia_spec_v1.md (SweepMarginalia)
// ============================================================

/// <summary>
/// Reconciles permanent progression rewards against persisted world state.
/// Cheap by construction (≤6 fragments + ≤8 archmagi + party-sized companion
/// list, all against a HashSet), so it is safe to run on every save.
/// </summary>
public static class ProgressionSweep
{
    // ── Fragment → school ────────────────────────────────────────────────
    //
    // Keys must match ShardZones.FragmentKeys exactly:
    //   { "axiom", "binding", "deathless", "moment", "schema", "primal" }
    //
    // ALL SIX ARE RULED. User-confirmed 2026-08-04; see
    // docs/progression_card_acquisition_v1_1.md A7. Do not re-derive these from
    // zone names in a future session — they are settled.
    //   • primal    → Elementalist  shard_acquisition_spec_v1 §9
    //                 ("The Primal Heart (primal, Elementalist)")
    //   • moment    → Chronomancer  narrative_frame_intro_finale_v1 R3
    //                 (the Chronomancer fragment IS the Moment Eternal)
    //   • deathless → Necromancer   "The Deathless Reliquary"
    //   • binding   → Enchanter     "The Bound Vault"
    //   • axiom     → Arcanist      "The Infinite Athenaeum"
    //   • schema    → Tinker        "The Pattern Sanctum"
    //
    // Druid is deliberately unmapped: seven non-Adept schools, six fragments.
    // A Druid-main has no aligned fragment and reaches Communion only through
    // Fluency — shard_acquisition_spec_v1 §4 calls this out as intended.
    private static readonly Dictionary<string, string> FragmentSchool = new()
    {
        { "primal",    "Elementalist" },
        { "moment",    "Chronomancer" },
        { "deathless", "Necromancer"  },
        { "binding",   "Enchanter"    },
        { "axiom",     "Arcanist"     },
        { "schema",    "Tinker"       },
    };

    // ── Paid-flag namespaces (on EternalLedger.MetaNarrativeFlags) ───────
    private const string PaidFragment      = "prog_paid_frag_";     // + key            (once ever)
    private const string PaidFragmentArt   = "prog_paid_fragart_";  // + key            (once ever)
    private const string PaidCompanionStage = "prog_paid_compstage_"; // + id + "_s" + n (once ever)
    private const string PaidArchmageCycle = "prog_paid_arch_";     // + id + "_c" + n  (once per cycle)
    private const string PaidArchmageArt   = "prog_paid_archart_";  // + id            (once ever)
    private const string PaidCompanionArt  = "prog_paid_comparcart_"; // + id          (once ever)
    // Marginalia paid flags are built by MarginaliaService.PaidFlag ("prog_paid_marginalia_" + family)
    // so the namespace lives in ONE place with the deed and public-flag namespaces.

    /// <summary>
    /// Award anything outstanding. Returns the number of awards made (0 on a
    /// steady-state save, which is the common case). Never throws — a sweep
    /// failure must not be able to block a save.
    /// </summary>
    public static int Run(GuildSaveData save)
    {
        if (save?.Ledger == null) return 0;

        // HARD REQUIREMENT: the card database must be populated first.
        //
        // The sweep writes once-ever "paid" stamps. If it runs against an empty
        // CardDatabase — Load() is called from CampusScreen._Ready one line before
        // LoadCardsFromJson, and the autoload that normally primes it first is not
        // guaranteed — then PickLegendaryForSchool returns null for every school
        // and every outstanding fragment gets stamped paid with no artifact ever
        // granted. Irreversible, silent, and once only.
        //
        // Skipping a pass costs nothing: this is a reconciler, so the next call
        // settles whatever this one didn't.
        if (CardDatabase.Blueprints == null || CardDatabase.Blueprints.Count == 0)
        {
            GD.Print("[ProgressionSweep] Card database not loaded yet — deferring the sweep.");
            return 0;
        }

        try
        {
            save.Ledger.MetaNarrativeFlags ??= new List<string>();
            var paid = new HashSet<string>(save.Ledger.MetaNarrativeFlags, StringComparer.Ordinal);

            // Each sweep is isolated: one malformed flag or missing definition
            // must not be able to starve the other two on every save forever.
            int awards = 0;
            awards += Guarded("fragments", () => SweepFragments(save, paid));
            awards += Guarded("archmagi", () => SweepArchmagi(save, paid));
            awards += Guarded("companion arcs", () => SweepCompanionArcs(save, paid));
            awards += Guarded("marginalia", () => SweepMarginalia(save, paid));

            if (awards > 0)
                GD.Print($"[ProgressionSweep] {awards} outstanding award(s) settled.");
            return awards;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[ProgressionSweep] Sweep failed (save continues): {e.Message}");
            return 0;
        }
    }

    private static int Guarded(string label, Func<int> sweep)
    {
        try { return sweep(); }
        catch (Exception e)
        {
            GD.PrintErr($"[ProgressionSweep] '{label}' sweep failed (others continue): {e.Message}");
            return 0;
        }
    }

    // ── Fragments ────────────────────────────────────────────────────────

    private static int SweepFragments(GuildSaveData save, HashSet<string> paid)
    {
        int awards = 0;

        foreach (var flag in save.Ledger.MetaNarrativeFlags.ToList())
        {
            if (string.IsNullOrEmpty(flag)) continue;
            if (!flag.StartsWith("fragment_", StringComparison.Ordinal)) continue;
            if (!flag.EndsWith("_collected", StringComparison.Ordinal)) continue;

            // A flag that is BOTH prefixed and suffixed but has nothing between
            // them (a literal "fragment_collected") would make the Substring
            // below throw. Guard the length before slicing.
            const int Affixes = 9 + 10;   // "fragment_" + "_collected"
            if (flag.Length <= Affixes) continue;

            // fragment_<key>_collected  →  <key>
            string key = flag.Substring("fragment_".Length,
                                        flag.Length - "fragment_".Length - "_collected".Length);
            if (string.IsNullOrEmpty(key)) continue;

            // Two flags, one per reward — same reason as the archmage branch.
            // A single flag would let "mastery paid, no Legendary available"
            // stamp the artifact as settled, forfeiting it forever even after a
            // new Legendary is authored for that school.
            string masteryFlag = PaidFragment + key;
            string artFlag = PaidFragmentArt + key;
            if (paid.Contains(masteryFlag) && paid.Contains(artFlag)) continue;

            if (!FragmentSchool.TryGetValue(key, out var school))
            {
                GD.PrintErr($"[ProgressionSweep] Fragment '{key}' has no school mapping — " +
                            $"no award made. Add it to FragmentSchool.");
                continue;
            }

            if (!paid.Contains(masteryFlag))
            {
                int total = SchoolMasteryService.Award(save, school,
                    SchoolMasteryService.PointsFragmentClaimed, $"fragment '{key}' claimed");
                if (total >= 0)
                {
                    Stamp(save, paid, masteryFlag);
                    awards++;
                }
            }

            if (!paid.Contains(artFlag))
            {
                var legendary = RegaliaService.PickLegendaryForSchool(save, school);
                if (legendary != null)
                {
                    RegaliaService.Grant(save, legendary.Id, $"fragment '{key}' claimed");
                    Stamp(save, paid, artFlag);
                    awards++;
                }
                else if (OS.IsStdOutVerbose())
                {
                    GD.Print($"[ProgressionSweep] No ungranted {school} Legendary for fragment " +
                             $"'{key}' — artifact deferred, mastery already paid.");
                }
            }
        }

        return awards;
    }

    // ── Archmagi ─────────────────────────────────────────────────────────

    private static int SweepArchmagi(GuildSaveData save, HashSet<string> paid)
    {
        var dispositions = save.Cycle?.Campaign?.Dispositions;
        if (dispositions == null || dispositions.Count == 0) return 0;

        int cycleNumber = save.Cycle?.CycleNumber ?? 1;
        int awards = 0;

        foreach (var kvp in dispositions)
        {
            string id = kvp.Key;
            var disposition = kvp.Value;
            if (string.IsNullOrEmpty(id)) continue;

            // Unknown and Neutral are not resolutions. Corrupted is a failure
            // state the player did not author — it teaches nothing about the
            // school, so it pays nothing.
            bool resolved = disposition == ArchmageDisposition.Allied
                         || disposition == ArchmageDisposition.Coerced
                         || disposition == ArchmageDisposition.Overthrown;
            if (!resolved) continue;

            string school = ArchmageRegistry.Get(id)?.School;
            if (string.IsNullOrWhiteSpace(school))
            {
                GD.PrintErr($"[ProgressionSweep] Archmage '{id}' has no school — skipped.");
                continue;
            }

            // SchoolMastery: once per cycle. You learn from each confrontation,
            // including a repeat of one you resolved in a lost timeline.
            string cycleFlag = $"{PaidArchmageCycle}{id}_c{cycleNumber}";
            if (!paid.Contains(cycleFlag))
            {
                int points = disposition == ArchmageDisposition.Allied
                    ? SchoolMasteryService.PointsArchmageAllied
                    : SchoolMasteryService.PointsArchmageResolved;

                SchoolMasteryService.Award(save, school, points,
                    $"archmage '{id}' {disposition} (cycle {cycleNumber})");

                Stamp(save, paid, cycleFlag);
                awards++;
            }

            // Regalia: once ever. The artifact is unique; the lesson is not.
            string artFlag = PaidArchmageArt + id;
            if (!paid.Contains(artFlag))
            {
                var legendary = RegaliaService.PickLegendaryForSchool(save, school);
                if (legendary != null)
                {
                    RegaliaService.Grant(save, legendary.Id, $"archmage '{id}' {disposition}");
                    Stamp(save, paid, artFlag);
                    awards++;
                }
                else if (OS.IsStdOutVerbose())
                {
                    // Expected for Adept (zero Legendaries by design — the
                    // undeclared school has no artifacts) and for any school
                    // whose Legendaries are exhausted. Do NOT stamp: if a new
                    // Legendary is authored later, the sweep pays it then.
                    // Verbose-gated because this branch is re-reached on EVERY
                    // save, for every resolved archmage of an exhausted school.
                    GD.Print($"[ProgressionSweep] No ungranted {school} Legendary for " +
                             $"archmage '{id}' — artifact deferred.");
                }
            }
        }

        return awards;
    }

    // ── Companion arcs ───────────────────────────────────────────────────

    private static int SweepCompanionArcs(GuildSaveData save, HashSet<string> paid)
    {
        var companions = save.Cycle?.Companions;
        if (companions == null || companions.Count == 0) return 0;

        int awards = 0;

        foreach (var c in companions)
        {
            if (c == null || string.IsNullOrEmpty(c.Id)) continue;
            if (c.ArcStage < 1) continue;                 // 0 = not started

            // Off-school companions (the 12 martials) have no school to credit
            // and no contributed cards — they teach nothing. Their arcs pay in
            // the timeline layer, which is correct.
            string signature = c.ContributedCardIds?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(signature)) continue;

            // Mastery accrues at EVERY arc stage, not only the capstone. This is
            // what makes the design doc's §7e pacing real: a companion reaching
            // arc stage 2 both becomes a faculty source AND carries their school
            // past the declaration threshold, so the first discipline is
            // declarable off the first arcane companion rather than requiring a
            // completed story. One flag per stage so a save that jumped stages
            // still gets paid for each.
            int reached = Math.Min(c.ArcStage, 4);
            for (int stage = 1; stage <= reached; stage++)
            {
                string stageFlag = $"{PaidCompanionStage}{c.Id}_s{stage}";
                if (paid.Contains(stageFlag)) continue;

                int total = SchoolMasteryService.Award(save, c.School,
                    SchoolMasteryService.PointsCompanionArcStage,
                    $"companion '{c.Id}' reached arc stage {stage}");

                if (total < 0) break;   // blank school — nothing will pay, stop trying
                Stamp(save, paid, stageFlag);
                awards++;
            }

            if (c.ArcStage < 4) continue;                 // 4 = complete; artifact below

            string artFlag = PaidCompanionArt + c.Id;
            if (!paid.Contains(artFlag))
            {
                // The card outlives the companion — permanently, even after they die.
                // Grant returns false both when it fails AND when the blueprint is
                // already owned (two companions can share a signature card), so
                // check ownership rather than trusting the return value alone.
                RegaliaService.Grant(save, signature, $"companion '{c.Id}' arc complete");

                if (RegaliaService.IsOwned(save, signature))
                {
                    Stamp(save, paid, artFlag);
                    awards++;
                }
                else if (OS.IsStdOutVerbose())
                {
                    // Left outstanding on purpose: if the signature id is repaired
                    // later, the next sweep pays it. Verbose-gated so a permanently
                    // broken id cannot spam the console on every save.
                    GD.Print($"[ProgressionSweep] Companion '{c.Id}' signature " +
                             $"'{signature}' could not be granted — artifact deferred.");
                }
            }
        }

        return awards;
    }

    // ── Marginalia (marginalia_spec_v1) ──────────────────────────────────

    private static int SweepMarginalia(GuildSaveData save, HashSet<string> paid)
    {
        int awards = 0;

        foreach (var family in MarginaliaService.FamilyIds)
        {
            string paidFlag = MarginaliaService.PaidFlag(family);
            if (paid.Contains(paidFlag)) continue;

            // DeedCounts is the source of truth (cross-cycle, victory-committed
            // by ExpeditionManager) — the sweep only DERIVES from it, so a crash
            // between commit and save self-heals like every other pass.
            int threshold = MarginaliaService.Threshold(family);
            if (threshold <= 0) continue;   // card missing from DB — defer, never stamp
            if (MarginaliaService.KillCount(save, family) < threshold) continue;

            var bp = MarginaliaService.CardFor(family);
            if (bp == null) continue;       // same deferral, belt and braces

            string school = MarginaliaService.SchoolOf(family);
            if (string.IsNullOrWhiteSpace(school))
            {
                GD.PrintErr($"[ProgressionSweep] Marginalia family '{family}' has no " +
                            "school in ArchmageRegistry — no award made.");
                continue;
            }

            // R3/R4: permanent breadth, unlocked whatever school is being played.
            // A dormant off-school unlock is access, not a card payment (§2a).
            save.Ledger.UnlockedCardBlueprintIds ??= new List<string>();
            if (!save.Ledger.UnlockedCardBlueprintIds.Contains(bp.Id))
                save.Ledger.UnlockedCardBlueprintIds.Add(bp.Id);

            SchoolMasteryService.Award(save, school, MarginaliaService.PointsFor(family),
                $"marginalia '{family}' entry complete");

            Stamp(save, paid, paidFlag);
            // The public flag — the design doc's stated namespace — for quests
            // and feature gates that should not care about sweep bookkeeping.
            Stamp(save, paid, MarginaliaService.PublicFlag(family));
            awards++;

            GD.Print($"[ProgressionSweep] Marginalia '{family}' settled — " +
                     $"'{bp.Id}' unlocked, {school} SchoolMastery paid.");
        }

        return awards;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void Stamp(GuildSaveData save, HashSet<string> paid, string flag)
    {
        if (paid.Add(flag))
            save.Ledger.MetaNarrativeFlags.Add(flag);
    }
}
