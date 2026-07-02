using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CourtGenerator.cs  (v2)
//
// Purpose:        Seeded, headless generator for one cycle's
//                 courts (Court & Council phase C1). One court
//                 per kingdom except the convergence territory.
//                 Each court gets 3-5 named courtiers with
//                 negotiation archetypes, offices, seeded Regard
//                 and Influence, and one hidden secret each.
//                 Archmage-less kingdoms get regent courts.
//
//                 v2 changes (rulings after first seeded dumps):
//                 - INITIAL STANDING CLAMP: generated standing
//                   score is clamped to [-5, +2] (the Received
//                   band). Hostile and Welcome must both be
//                   EARNED in play, never rolled at generation.
//                   The clamp is deterministic (no RNG), so
//                   same-seed rosters stay reproducible.
//                 - FIRST-NAME UNIQUENESS PER COURT: no two
//                   courtiers at one court share a first name
//                   (dispatches and reports refer to courtiers
//                   by first name; ambiguity is a UI bug).
//                   Full-name uniqueness remains world-global.
//
//                 DETERMINISM: each kingdom's court derives from
//                 its own RNG seeded seed ^ FNV1a(kingdomId).
//                 This generator never touches WorldGenerator's
//                 RNG stream, so adding it changes NOTHING about
//                 existing world output; and per-kingdom seeding
//                 makes courts stable regardless of generation
//                 order. FNV1a (not string.GetHashCode, which is
//                 randomized per-process in .NET and would break
//                 save-seed determinism).
// Layer:          System
// Collaborators:  WorldGenerator.cs (calls after kingdoms are
//                 seeded), CouncilState.cs (output),
//                 KingdomState.cs (ArchmageId cache read at
//                 generation time), PlayerSession (debug dump)
// See:            court_council_system_v1_1.docx §3, §14 (C1);
//                 v1.2 errata: initial-standing clamp
// ============================================================

/// <summary>Builds all courts for one generated world. Headless.</summary>
public static class CourtGenerator
{
    // ── Generation rules ─────────────────────────────────────────────────

    /// <summary>Floor for the generated standing score. Below this, contact
    /// would open in the Hostile band — a rolled trap. Hostile is earned.</summary>
    private const int InitialScoreMin = -5;

    /// <summary>Ceiling for the generated standing score. Above this, contact
    /// would open in the Welcome band — a rolled windfall that teaches the
    /// player bands come from dice. Welcome is earned. Severable: raise or
    /// delete this constant to allow windfall courts.</summary>
    private const int InitialScoreMax = 2;

    // ── Authored pools ───────────────────────────────────────────────────
    // Kept in code for C1; migrate to authored JSON (with names per
    // faction culture) when kingdom DisplayNames stop being raw ids.

    private static readonly string[] FirstNames =
    {
        "Maren", "Tobias", "Isolde", "Corwin", "Alys", "Bertran",
        "Sable", "Edric", "Rosalind", "Garvin", "Petra", "Lucan",
        "Mirelle", "Osric", "Thessaly", "Aldous", "Brenna", "Cassia",
        "Dorian", "Elspeth", "Ferrand", "Gwenna", "Halric", "Imogen",
        "Joss", "Katrin", "Leoric", "Maud", "Nerissa", "Orin",
    };

    private static readonly string[] Surnames =
    {
        "Vance", "Aldermere", "Kestrel", "Boward", "Fenwick",
        "Marlowe", "Ashcombe", "Draval", "Ellery", "Ferro",
        "Grimsby", "Halloway", "Ilverton", "Karst", "Lowe",
        "Mercer", "Norwood", "Ostrander", "Pell", "Quill",
    };

    /// <summary>Secret table ids (§3a). Blackmail leverage or, returned
    /// quietly, the fastest legitimate Regard gain in the game.</summary>
    private static readonly string[] SecretPool =
    {
        "gambling_debts", "hidden_heir", "forged_lineage",
        "smuggling_ring", "secret_faith", "poisoned_rival",
        "embezzled_treasury", "forbidden_affair", "pact_with_astrologer",
        "cowardice_in_battle", "selling_intelligence", "counterfeit_credentials",
    };

    /// <summary>Office → preferred archetypes. 60% chance to draw from the
    /// preferred set; otherwise uniform over all six, so courts stay
    /// legible without becoming stereotyped.</summary>
    private static readonly Dictionary<string, string[]> OfficeAffinity = new()
    {
        { CourtVocab.OfficeChancellor,  new[] { "Scholar", "Opportunist" } },
        { CourtVocab.OfficeMarshal,     new[] { "Commander" } },
        { CourtVocab.OfficeSpymaster,   new[] { "Opportunist", "Survivor" } },
        { CourtVocab.OfficeCourtWizard, new[] { "Scholar", "Idealist" } },
        { CourtVocab.OfficeSteward,     new[] { "Merchant" } },
        { CourtVocab.OfficeFavorite,    new[] { "Idealist", "Opportunist" } },
    };

    // ── Entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates the full CouncilState for one world. Call after
    /// KingdomStates are seeded (needs the ArchmageId cache).
    /// </summary>
    public static CouncilState Generate(int seed,
        Dictionary<string, KingdomState> kingdoms, string convergenceKingdomId)
    {
        var council = new CouncilState();
        var usedFullNames = new HashSet<string>();
        var rng = new RandomNumberGenerator();

        // Ordered iteration: dictionary order is not contractual, and name
        // uniqueness rerolls make generation order-sensitive without it.
        foreach (var kingdomId in kingdoms.Keys.OrderBy(k => k, System.StringComparer.Ordinal))
        {
            if (kingdomId == convergenceKingdomId)
            {
                continue; // Kassian holds no court a guild envoy attends.
            }

            rng.Seed = ((ulong)(uint)seed << 32) | Fnv1a(kingdomId);
            var court = BuildCourt(kingdomId, kingdoms[kingdomId], rng, usedFullNames);
            council.Courts[kingdomId] = court;
        }

        int regents = council.Courts.Values.Count(c => c.IsRegentCourt);
        int courtiers = council.Courts.Values.Sum(c => c.Courtiers.Count);
        GD.Print($"[CourtGenerator] {council.Courts.Count} courts " +
                 $"({regents} regent), {courtiers} courtiers seeded.");

        if (PlayerSession.DebugMode)
        {
            DumpCourts(council, kingdoms);
        }

        return council;
    }

    // ── Court assembly ───────────────────────────────────────────────────

    private static CourtState BuildCourt(string kingdomId, KingdomState kingdom,
        RandomNumberGenerator rng, HashSet<string> usedFullNames)
    {
        var court = new CourtState
        {
            KingdomId = kingdomId,
            IsRegentCourt = string.IsNullOrEmpty(kingdom.ArchmageId),
        };

        // First names used at THIS court (the regent counts — dispatches
        // name the regent too).
        var courtFirstNames = new HashSet<string>();

        if (court.IsRegentCourt)
        {
            court.RegentName = "Regent " + UniqueName(rng, usedFullNames, courtFirstNames);
        }

        // Court size: 3 (20%), 4 (60%), 5 (20%).
        uint sizeRoll = rng.Randi() % 10;
        int size = sizeRoll < 2 ? 3 : (sizeRoll < 8 ? 4 : 5);

        // Offices: Chancellor always (Political/Passage favors and the
        // §7 echo routing assume one exists); remainder sampled without
        // replacement from the other five.
        var offices = new List<string> { CourtVocab.OfficeChancellor };
        var others = CourtVocab.Offices
            .Where(o => o != CourtVocab.OfficeChancellor).ToList();
        Shuffle(others, rng);
        offices.AddRange(others.Take(size - 1));

        // Influence bags guarantee exactly one Influence-3 power broker per
        // court, so Discredit missions always have a meaningful target.
        int[] influenceBag = size switch
        {
            3 => new[] { 3, 2, 1 },
            4 => new[] { 3, 2, 2, 1 },
            _ => new[] { 3, 2, 2, 1, 1 },
        };
        var influences = influenceBag.ToList();
        Shuffle(influences, rng);

        // Secrets: without replacement within a court.
        var secrets = SecretPool.ToList();
        Shuffle(secrets, rng);

        for (int n = 0; n < size; n++)
        {
            court.Courtiers.Add(new CourtierState
            {
                Id = $"{kingdomId}_courtier_{n}",
                DisplayName = UniqueName(rng, usedFullNames, courtFirstNames),
                Office = offices[n],
                Archetype = RollArchetype(offices[n], rng),
                Regard = RollInitialRegard(rng),
                Influence = influences[n],
                SecretId = secrets[n],
                SecretKnown = false,
                IsCorruptedAgent = false, // manifests during play at corruption 2
            });
        }

        ClampInitialStanding(court);

        return court;
    }

    /// <summary>Clamp the generated standing score into the Received band
    /// [InitialScoreMin, InitialScoreMax]. Hostile and Welcome are earned in
    /// play, never rolled. Deterministic: adjusts the most extreme Regard by
    /// one step at a time (ties broken by list order), so same-seed rosters
    /// remain byte-identical across runs. Converges because every adjustment
    /// moves the score toward the band by at least 1 (Influence ≥ 1).</summary>
    private static void ClampInitialStanding(CourtState court)
    {
        int guard = 64; // |score| ≤ 45 worst case; 64 steps is unreachable

        while (court.StandingScore() < InitialScoreMin && guard-- > 0)
        {
            CourtierState worst = court.Courtiers[0];
            foreach (var c in court.Courtiers)
            {
                if (c.Regard < worst.Regard)
                {
                    worst = c;
                }
            }
            worst.Regard += 1;
        }

        while (court.StandingScore() > InitialScoreMax && guard-- > 0)
        {
            CourtierState best = court.Courtiers[0];
            foreach (var c in court.Courtiers)
            {
                if (c.Regard > best.Regard)
                {
                    best = c;
                }
            }
            best.Regard -= 1;
        }
    }

    // ── Rolls ────────────────────────────────────────────────────────────

    private static string RollArchetype(string office, RandomNumberGenerator rng)
    {
        if (OfficeAffinity.TryGetValue(office, out var preferred) &&
            rng.Randi() % 10 < 6)
        {
            return preferred[(int)(rng.Randi() % (uint)preferred.Length)];
        }
        return CourtVocab.Archetypes[(int)(rng.Randi() % (uint)CourtVocab.Archetypes.Length)];
    }

    /// <summary>Initial Regard: 0 (60%), ±1 (15% each), ±2 (5% each).
    /// Courts start with texture; ClampInitialStanding keeps the SUM inside
    /// the Received band regardless of individual rolls.</summary>
    private static int RollInitialRegard(RandomNumberGenerator rng)
    {
        uint r = rng.Randi() % 20;
        if (r < 12) return 0;
        if (r < 15) return 1;
        if (r < 18) return -1;
        if (r < 19) return 2;
        return -2;
    }

    /// <summary>A "First Last" name unique on BOTH axes: the full name is
    /// unique across the whole world (two courts must never contain the same
    /// person), and the first name is unique within this court (reports and
    /// dispatches refer to courtiers by first name).</summary>
    private static string UniqueName(RandomNumberGenerator rng,
        HashSet<string> usedFullNames, HashSet<string> courtFirstNames)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            string first = FirstNames[(int)(rng.Randi() % (uint)FirstNames.Length)];
            if (courtFirstNames.Contains(first))
            {
                continue;
            }
            string full = first + " "
                + Surnames[(int)(rng.Randi() % (uint)Surnames.Length)];
            if (usedFullNames.Add(full))
            {
                courtFirstNames.Add(first);
                return full;
            }
        }
        // 30 first names vs ≤6 per court and 600 combos vs ~50 per world:
        // exhaustion is unreachable, but never loop unbounded on an RNG.
        string fallback = $"Courtier {usedFullNames.Count}";
        usedFullNames.Add(fallback);
        return fallback;
    }

    // ── Utilities ────────────────────────────────────────────────────────

    /// <summary>Stable 32-bit FNV-1a. string.GetHashCode() is randomized
    /// per-process in .NET and must never feed a save seed.</summary>
    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261u;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    private static void Shuffle<T>(List<T> list, RandomNumberGenerator rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.Randi() % (uint)(i + 1));
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ── Debug (C1 exit criterion) ────────────────────────────────────────

    private static void DumpCourts(CouncilState council,
        Dictionary<string, KingdomState> kingdoms)
    {
        foreach (var kvp in council.Courts.OrderBy(k => k.Key, System.StringComparer.Ordinal))
        {
            var court = kvp.Value;
            string seat = court.IsRegentCourt
                ? court.RegentName
                : $"Archmage {kingdoms[kvp.Key].ArchmageId}";
            GD.Print($"[Court] {kvp.Key} — seat: {seat} — " +
                     $"score {court.StandingScore()} ({court.Band()})");
            foreach (var c in court.Courtiers)
            {
                GD.Print($"    {c.DisplayName,-22} {c.Office,-11} {c.Archetype,-11} " +
                         $"R={c.Regard,2} I={c.Influence} secret={c.SecretId}");
            }
        }
    }
}
