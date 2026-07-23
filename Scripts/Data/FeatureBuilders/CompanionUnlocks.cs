using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CompanionUnlocks.cs — roster sustainability pass (2026-07-22)
//
// Purpose:        Two jobs the roster never had:
//                 1. ENFORCE unlock conditions. Companion JSON
//                    carried human-readable unlockCondition text,
//                    but nothing ever flipped IsAvailable — only
//                    the 5 starters were ever recruitable. Sync()
//                    evaluates real save-state rules each campus
//                    refresh and unlocks (returns newly-available
//                    names for the host to toast).
//                 2. ROTATE the starters. SeedCycleRotation picks
//                    a per-cycle subset of the base-available
//                    companions (seeded from the cycle) so each
//                    rendering's hiring hall differs.
// Layer:          Data / FeatureBuilders
// Collaborators:  CompanionRoster.cs (runtime roster),
//                 CompanionLoader.cs (templates),
//                 CampusScreen.cs (call sites + toasts)
// ============================================================

/// <summary>Evaluates companion unlock rules and the per-cycle starter rotation.</summary>
public static class CompanionUnlocks
{
    /// <summary>How many of the base-available starters appear per cycle.</summary>
    private const int RotationCount = 3;

    private const string RotationSeededFlag = "roster_rotation_seeded";

    /// <summary>Per-cycle rotation: of the companions whose TEMPLATE is
    /// base-available (no unlock condition), only <see cref="RotationCount"/>
    /// appear in a given cycle — deterministic on the cycle's world seed, so
    /// each rendering's hiring hall differs. Runs once per cycle (flag-gated);
    /// call from EnsureCycleWorld after world generation.</summary>
    public static void SeedCycleRotation(GuildSaveData save)
    {
        if (save?.Cycle == null) return;
        if (save.HasFlag(RotationSeededFlag)) return;

        CompanionRoster.EnsureRoster(save);

        var baseIds = CompanionLoader.LoadAll()
            .Where(t => t.IsAvailable)
            .Select(t => t.Id)
            .OrderBy(id => id) // stable order before the seeded shuffle
            .ToList();
        if (baseIds.Count == 0) return;

        // Seeded shuffle (Fisher-Yates on a cycle-derived RNG).
        var rng = new RandomNumberGenerator();
        rng.Seed = (ulong)(save.Cycle.WorldSeed ^ (save.Cycle.CycleNumber * 7919));
        for (int i = baseIds.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.Randi() % (uint)(i + 1));
            (baseIds[i], baseIds[j]) = (baseIds[j], baseIds[i]);
        }
        var present = new HashSet<string>(baseIds.Take(RotationCount));

        foreach (var c in save.Companions)
        {
            if (!baseIds.Contains(c.Id)) continue; // gated companions untouched
            if (c.IsRecruited) continue;           // never un-hire someone
            c.IsAvailable = present.Contains(c.Id);
        }

        save.Cycle.SetFlag(RotationSeededFlag);
        SaveManager.MarkDirty();
        GD.Print($"[CompanionUnlocks] Cycle rotation: {string.Join(", ", present)} present " +
                 $"({baseIds.Count - present.Count} starters elsewhere this rendering).");
    }

    /// <summary>Evaluate unlock rules against current save state and flip
    /// IsAvailable for any newly-earned companions. Returns the display names
    /// that just became available (for toasting). Never revokes availability.
    /// Grant-style companions (bram — wilds rescue; isolde — court favor) are
    /// unlocked by their encounters/systems via CompanionRoster.GrantFromEncounter,
    /// not here.</summary>
    public static List<string> Sync(GuildSaveData save)
    {
        var newly = new List<string>();
        if (save?.Companions == null) return newly;

        int combatWon = 0;
        save.Ledger?.DeedCounts?.TryGetValue("combat_won", out combatWon);
        int honoredDead = save.Ledger?.HonoredDead?.Count ?? 0;
        int loops = save.Ledger?.LoopHistory?.Count ?? 0;
        int lore = save.UnlockedLoreEntries?.Count ?? 0;
        int fragments = save.Ledger?.MetaNarrativeFlags?
            .Count(f => f.StartsWith("fragment_") && f.EndsWith("_trial_passed")) ?? 0;
        bool corruptionAnywhere = save.Cycle?.Campaign?.CorruptionLevels?.Values
            .Any(v => v > 0) ?? false;
        bool courtFavored = save.Cycle?.Council?.Courts?.Values
            .Any(ct => ct.Band() >= CourtStandingBand.Favored) ?? false;

        foreach (var c in save.Companions)
        {
            if (c.IsAvailable || c.IsRecruited || c.IsPermadead) continue;

            bool unlock = c.Id switch
            {
                // Proven-guild hires — the guild's cross-cycle combat record.
                "kael_ashblade"   => combatWon >= 2,
                "sable_voss"      => combatWon >= 2,
                "miro_fletch"     => combatWon >= 3,
                "corvin_ashdown"  => combatWon >= 4,

                // System-tied unlocks.
                "fenna_boltwright"   => save.HasFlag("qe_building_completed"),
                "maren_gravesong"    => honoredDead >= 3,
                "ruslan_vane"        => corruptionAnywhere,
                "seraphine_duskwell" => save.HasFlag("qe_negotiation_deal"),
                "isolde_marrec"      => courtFavored,

                // New companions (roster expansion, 2026-07-22).
                "odile_vantrec"   => save.HasFlag("qe_siege_fell"),
                "petra_quillane"  => loops >= 1,
                "harl_denner"     => fragments >= 1,
                "tamsin_greywood" => lore >= 5,

                _ => false,
            };

            if (unlock)
            {
                c.IsAvailable = true;
                newly.Add(c.Name);
            }
        }

        // Ondrej is SECONDED, not hired: when the Arcanist archmage unites,
        // he joins the guild outright, once per cycle.
        if (save.HasFlag("qe_archmage_united_aurel") && !save.HasFlag("ondrej_seconded"))
        {
            var ondrej = save.Companions.FirstOrDefault(x => x.Id == "ondrej_vael");
            if (ondrej != null && !ondrej.IsPermadead && !ondrej.IsRecruited)
            {
                ondrej.IsAvailable = true;
                ondrej.IsRecruited = true;
                newly.Add($"{ondrej.Name} (seconded by the Annotated Circle)");
            }
            save.Cycle?.SetFlag("ondrej_seconded");
        }

        if (newly.Count > 0) SaveManager.MarkDirty();
        return newly;
    }
}
