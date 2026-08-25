using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// MarginaliaService.cs
//
// Purpose:        The Marginalia, enemy-knowledge card acquisition
//                 (progression_card_acquisition_v1 §8 "Kill", spec:
//                 docs/marginalia_spec_v1.md). Defeat N of an enemy
//                 family and the blueprint that IS that family's
//                 signature trick unlocks into the permanent pool.
//                 This service is the taxonomy + read/commit API:
//                 family table, thresholds, kill counts, progress rows.
//                 The UNLOCK itself is settled by ProgressionSweep,
//                 never here. One automatic writer, per its header.
// Layer:          Data / Feature builder
// Collaborators:  EternalLedger.cs (DeedCounts host),
//                 ProgressionSweep.cs (SweepMarginalia, the writer),
//                 CombatManager.cs (per-fight tally),
//                 ExpeditionManager.cs (victory-gated commit + toasts),
//                 ArchmageRegistry.cs (family → school/name),
//                 CardDatabase.cs (rarity → threshold/points),
//                 StarterDeckLoader.cs (seed exclusion),
//                 CampusRecordsPanel.cs / CardLibraryUi.cs (read surfaces)
//
// ⚠ THIS IS NOT THE BESTIARY. Scripts/Cards/Loader/Bestiary.cs is
//   Druid wildlife definitions for GrowthManager, and unrelated (design
//   doc §1c). Flag namespace here: marginalia_<family>. Deed namespace:
//   marginalia_kill_<family>. And never write bare "mastery": the points
//   paid on completion are SchoolMastery, not CardMastery or CastMastery.
// ============================================================

/// <summary>
/// One Marginalia family's progress, for UI rows.
/// </summary>
public struct MarginaliaProgress
{
    public string FamilyId;      // archmage id, e.g. "conductor"
    public string FactionName;   // ArchmageDefinition.FactionName
    public string School;        // "Necromancer"
    public int Kills;
    public int Threshold;        // -1 when the card is missing from the database
    public bool Complete;        // prog_paid stamp present (sweep has settled it)
    public string CardId;
    public string CardName;      // display name, "" when unresolvable
}

/// <summary>
/// Taxonomy and read API for the Marginalia (marginalia_spec_v1). Families are
/// the 8 archmage factions (R1); thresholds and SchoolMastery pay scale with the
/// unlocked card's rarity (R2/R6); the reward is permanent breadth (R3), settled
/// by <see cref="ProgressionSweep"/>.
/// </summary>
public static class MarginaliaService
{
    // ── The family table (R1) ────────────────────────────────────────────
    //
    // family id = UnitDefinition.FactionId = ArchmageDefinition.Id. One
    // authored card per family: the family's signature trick as a card.
    // Additions (wildlife families, the 8 school casters) go here; the rest
    // of the system keys off this table and needs no other change.
    private static readonly Dictionary<string, string> FamilyCard = new(StringComparer.Ordinal)
    {
        { "wenna",      "adept_invigilation" },
        { "aurel",      "arcanist_second_verse" },
        { "astrologer", "chronomancer_foregone_conclusion" },
        { "hess",       "druid_watching_pack" },
        { "joren",      "elementalist_threefold_argument" },
        { "namer",      "enchanter_seventh_layer" },
        { "conductor",  "necromancer_the_wake" },
        { "engineer",   "tinker_bench_turret" },
    };

    // Reverse map, built lazily, because the CardLibraryUi lock note asks "is this
    // blueprint a Marginalia card, and whose?" per detail-panel open.
    private static Dictionary<string, string> _cardFamily;

    /// <summary>All family ids, stable order (dictionary insertion order).</summary>
    public static IEnumerable<string> FamilyIds => FamilyCard.Keys;

    // ── Namespaces ───────────────────────────────────────────────────────

    /// <summary>DeedCounts key for a family's cross-cycle kill total.
    /// QuestTracker reads it as "deed:marginalia_kill_&lt;family&gt;" for free.</summary>
    public static string DeedKey(string family) => "marginalia_kill_" + family;

    /// <summary>The public completion flag (the design doc's stated namespace),
    /// stamped on MetaNarrativeFlags by the sweep alongside its paid flag.</summary>
    public static string PublicFlag(string family) => "marginalia_" + family;

    /// <summary>ProgressionSweep's once-ever paid stamp for a family.</summary>
    public static string PaidFlag(string family) => "prog_paid_marginalia_" + family;

    // ── Card / school / threshold lookups ────────────────────────────────

    /// <summary>The family's card blueprint, or null when the id is unknown or
    /// the card is not (yet) in the database. Callers must treat null as
    /// "defer", mirroring the sweep's Legendary pattern.</summary>
    public static CardBlueprint CardFor(string family)
    {
        if (string.IsNullOrEmpty(family) || !FamilyCard.TryGetValue(family, out var cardId))
            return null;
        return CardDatabase.GetByName(cardId);
    }

    /// <summary>True when this blueprint id is a Marginalia reward card.
    /// StarterDeckLoader must NOT seed these into the day-one unlock pool,
    /// or the entries would be pointless (6 of 8 are Common/Uncommon).</summary>
    public static bool IsMarginaliaCard(string blueprintId)
    {
        if (string.IsNullOrEmpty(blueprintId)) return false;
        return TryGetFamilyForCard(blueprintId, out _);
    }

    /// <summary>Reverse lookup: blueprint id → family id.</summary>
    public static bool TryGetFamilyForCard(string blueprintId, out string family)
    {
        family = "";
        if (string.IsNullOrEmpty(blueprintId)) return false;
        if (_cardFamily == null)
        {
            _cardFamily = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in FamilyCard)
                _cardFamily[kvp.Value] = kvp.Key;
        }
        return _cardFamily.TryGetValue(blueprintId, out family);
    }

    /// <summary>School credited for a family (ArchmageDefinition.School).
    /// "" when the registry has no entry.</summary>
    public static string SchoolOf(string family) => ArchmageRegistry.Get(family)?.School ?? "";

    /// <summary>Kills required (R2): 8 Common / 12 Uncommon / 20 Rare, keyed to
    /// the rarity of the card the entry unlocks. -1 while the card is missing
    /// from the database (treat as "not completable yet").</summary>
    public static int Threshold(string family)
    {
        var bp = CardFor(family);
        if (bp == null) return -1;
        return bp.Rarity switch
        {
            CardRarity.Common => 8,
            CardRarity.Uncommon => 12,
            CardRarity.Rare => 20,
            // A Legendary here would be a design error: Legendaries are Regalia
            // (§6a) and must never enter the draft pool through this door.
            _ => -1,
        };
    }

    /// <summary>SchoolMastery points paid on completion (R6): 2 / 3 / 5 by
    /// rarity. 0 when the card is missing (nothing is paid on a deferral).</summary>
    public static int PointsFor(string family)
    {
        var bp = CardFor(family);
        if (bp == null) return 0;
        return bp.Rarity switch
        {
            CardRarity.Common => SchoolMasteryService.PointsMarginaliaCommon,
            CardRarity.Uncommon => SchoolMasteryService.PointsMarginaliaUncommon,
            CardRarity.Rare => SchoolMasteryService.PointsMarginaliaRare,
            _ => 0,
        };
    }

    // ── Reads ────────────────────────────────────────────────────────────

    /// <summary>Cross-cycle kill count for a family.</summary>
    public static int KillCount(GuildSaveData save, string family)
    {
        if (save?.Ledger?.DeedCounts == null || string.IsNullOrEmpty(family)) return 0;
        return save.Ledger.DeedCounts.TryGetValue(DeedKey(family), out int n) ? n : 0;
    }

    /// <summary>True once the sweep has settled the family's unlock.</summary>
    public static bool IsComplete(GuildSaveData save, string family)
    {
        var flags = save?.Ledger?.MetaNarrativeFlags;
        return flags != null && flags.Contains(PaidFlag(family));
    }

    /// <summary>All families as UI progress rows, table order.</summary>
    public static List<MarginaliaProgress> Progress(GuildSaveData save)
    {
        var rows = new List<MarginaliaProgress>(FamilyCard.Count);
        foreach (var kvp in FamilyCard)
        {
            var def = ArchmageRegistry.Get(kvp.Key);
            var bp = CardDatabase.GetByName(kvp.Value);
            rows.Add(new MarginaliaProgress
            {
                FamilyId = kvp.Key,
                FactionName = def?.FactionName ?? kvp.Key,
                School = def?.School ?? "",
                Kills = KillCount(save, kvp.Key),
                Threshold = Threshold(kvp.Key),
                Complete = IsComplete(save, kvp.Key),
                CardId = kvp.Value,
                CardName = bp?.Prebuilt?.CardName ?? "",
            });
        }
        return rows;
    }

    // ── Commit (victory-gated; ExpeditionManager.EmitCombatDeed) ─────────

    /// <summary>One family's movement from a committed fight, for toasts.</summary>
    public struct CommitResult
    {
        public string FamilyId;
        public string FactionName;
        public int Kills;          // new cross-cycle total
        public int Threshold;
        public bool CompletedNow;  // crossed the threshold in THIS commit
        public string CardName;
    }

    /// <summary>
    /// Record a won fight's per-family kill tally into DeedCounts (R2: committed
    /// on victory only; the tally is assembled by CombatManager and travels via
    /// EncounterRouter). Unknown family ids are recorded too (deeds are cheap and
    /// honest) but only table families are reported back. Families already
    /// settled by the sweep are recorded silently, with no 13/12 toasts. The unlock
    /// itself is derived by ProgressionSweep on the next save.
    /// </summary>
    public static List<CommitResult> CommitKills(GuildSaveData save, Dictionary<string, int> tally)
    {
        var results = new List<CommitResult>();
        if (save?.Ledger == null || tally == null || tally.Count == 0) return results;

        foreach (var kvp in tally)
        {
            string family = kvp.Key;
            int count = kvp.Value;
            if (string.IsNullOrEmpty(family) || count <= 0) continue;

            bool wasComplete = IsComplete(save, family);
            int before = KillCount(save, family);
            int after = save.Ledger.RecordDeed(DeedKey(family), count);
            GD.Print($"[Marginalia] {family}: +{count} → {after} defeated (cross-cycle).");

            if (!FamilyCard.ContainsKey(family)) continue;   // recorded, not reported
            if (wasComplete) continue;                        // settled, so stay quiet

            int threshold = Threshold(family);
            var def = ArchmageRegistry.Get(family);
            var bp = CardFor(family);
            results.Add(new CommitResult
            {
                FamilyId = family,
                FactionName = def?.FactionName ?? family,
                Kills = after,
                Threshold = threshold,
                CompletedNow = threshold > 0 && before < threshold && after >= threshold,
                CardName = bp?.Prebuilt?.CardName ?? "",
            });
        }
        return results;
    }
}
