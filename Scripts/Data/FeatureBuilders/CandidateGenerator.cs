using Godot;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CandidateGenerator.cs
//
// Purpose:        The K3 procedural hireling matrix (companion_item
//                 _systems v2.1 §5c): Class (Fighter/Ranger/Arcane)
//                 × Trait (5) × School (8, Arcane only — martial
//                 templates ship School "None" and procedurals match
//                 that convention), stats rolled inside per-class
//                 envelopes, 0–2 pre-trained stances by settlement
//                 quality. Produces plain Companion records with
//                 "hire_" ids; every downstream system (party, injury,
//                 training, combat spawn) treats them identically to
//                 authored companions — both die by the same rules
//                 (R5).
// Layer:          Data (FeatureBuilders)
// Collaborators:  HiringHallService.cs (the only caller),
//                 CompanionDefinition.cs (the model),
//                 StanceRegistry (pre-trained stances).
// See:            docs/companion_item_systems_v2_1.docx §5a/§5c
// ============================================================

/// <summary>Rolls procedural hireling candidates for city hiring halls. Stat
/// envelopes are anchored to the live authored-template ranges (BaseHP 12–30)
/// so a procedural hire is a peer of an authored one, not a discount tier —
/// "two hires are never twins" comes from the matrix roll, not from stat
/// inflation. All constants are K3 starting values; tune here.</summary>
public static class CandidateGenerator
{
    // ── The matrix axes ──────────────────────────────────────────────────

    private static readonly string[] Classes = { "Fighter", "Fighter", "Ranger", "Ranger", "Arcane" };
    // Fighter/Ranger weighted 2:2:1 — halls sell muscle more often than
    // wizards; arcane hires are the rarer find (and the pricier one).

    private static readonly string[] Traits = { "Cunning", "Loyal", "Curious", "Stoic", "Reckless" };

    private static readonly string[] Schools =
    {
        "Elementalist", "Arcanist", "Enchanter", "Tinker",
        "Druid", "Necromancer", "Chronomancer", "Adept",
    };

    // ── Name pools (procedural people need names, not ids) ──────────────

    private static readonly string[] GivenNames =
    {
        "Aldric", "Brenna", "Cael", "Doriya", "Edwyn", "Fenna", "Garrick",
        "Halya", "Ilsa", "Joren", "Kessa", "Lunet", "Marrek", "Nims",
        "Odile", "Petra", "Quill", "Rosalind", "Soren", "Tavia", "Ulric",
        "Vessa", "Wilmot", "Yara", "Zeff",
    };

    private static readonly string[] Epithets =
    {
        "of the Ford", "Greyhand", "the Younger", "Thornfield", "Ashvale",
        "Redmoor", "of the Low Road", "Kettleburn", "Duskwalker", "Harrow",
        "of the Nine Wells", "Stoneleigh", "the Quiet", "Fernbrake",
        "Coppersworth", "of the Long March", "Bramblewood", "Palewater",
    };

    // ═════════════════════════════════════════════════════════════════════
    // Entry
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Roll one candidate. <paramref name="quality"/>: 0 = ordinary
    /// city hall, 1 = seat/capital hall (better stat floors, more pre-trained
    /// stances — "city halls outdraw town halls in quality", §5a). The id is
    /// unique per (city, lunation, index) so re-rolls never collide with a
    /// previously hired companion's save entry.</summary>
    public static Companion Generate(RandomNumberGenerator rng, int quality,
        string cityId, int lunation, int index,
        string forceClass = null, string forceSchool = null)
    {
        // K5: forceClass/forceSchool let the non-hall sources (Unite adepts,
        // favor retainers) draw from the same matrix — a seconded adept is a
        // rolled person with a fixed school, not a separate schema.
        string unitClass = forceClass ?? Classes[rng.RandiRange(0, Classes.Length - 1)];
        string trait = Traits[rng.RandiRange(0, Traits.Length - 1)];
        string school = unitClass == "Arcane"
            ? (forceSchool ?? Schools[rng.RandiRange(0, Schools.Length - 1)])
            : "None";

        var c = new Companion
        {
            Id = $"hire_{Sanitize(cityId)}_{lunation}_{index}",
            Name = $"{GivenNames[rng.RandiRange(0, GivenNames.Length - 1)]} " +
                   $"{Epithets[rng.RandiRange(0, Epithets.Length - 1)]}",
            School = school,
            PersonalityTrait = trait,
            UnitClass = unitClass,
            IsAvailable = true,
            IsRecruited = false,
            Loyalty = 50,          // v1 locked: everyone enters at 50
            ArcStage = 0,          // hirelings have no authored arc
        };

        RollStats(c, rng, quality);
        RollStances(c, rng, quality);
        c.RecruitmentCost = Price(c, quality);
        c.Backstory = BackstoryLine(c);
        return c;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Stats — per-class envelopes (template range: BaseHP 12–30)
    // ═════════════════════════════════════════════════════════════════════

    private static void RollStats(Companion c, RandomNumberGenerator rng, int quality)
    {
        // Quality lifts the FLOOR, not the ceiling — a capital hall's worst
        // candidate is decent; its best is no better than anywhere's best.
        int q = quality > 0 ? 2 : 0;

        switch (c.UnitClass)
        {
            case "Fighter":
                c.BaseHP = rng.RandiRange(18 + q, 26);
                c.BaseArmor = rng.RandiRange(quality > 0 ? 1 : 0, 3);
                c.BaseAttackDamage = rng.RandiRange(4, 6);
                c.BaseAttackRange = 1;
                c.BaseSpeed = rng.RandiRange(2, 3);
                c.BaseMana = 0;
                break;

            case "Ranger":
                c.BaseHP = rng.RandiRange(14 + q, 20);
                c.BaseArmor = rng.RandiRange(0, 1);
                c.BaseAttackDamage = rng.RandiRange(3, 5);
                c.BaseAttackRange = rng.RandiRange(2, 3);
                c.BaseSpeed = rng.RandiRange(3, 4);
                c.BaseMana = 0;
                break;

            default: // Arcane — combat stats mostly superseded by school kit
                c.BaseHP = rng.RandiRange(12 + q, 16);
                c.BaseArmor = 0;
                c.BaseAttackDamage = rng.RandiRange(2, 3);
                c.BaseAttackRange = rng.RandiRange(1, 2);
                c.BaseSpeed = 2;
                c.BaseMana = rng.RandiRange(3, 5);
                break;
        }

        c.BaseActionPoints = 3; // levy default; Training Grounds tier lifts it
    }

    // ═════════════════════════════════════════════════════════════════════
    // Pre-trained stances — 0–2 by hall quality (§5c)
    // ═════════════════════════════════════════════════════════════════════

    private static void RollStances(Companion c, RandomNumberGenerator rng, int quality)
    {
        if (c.UnitClass != "Fighter" && c.UnitClass != "Ranger") return;

        // Ordinary hall: 0–1. Seat hall: 1–2. Pre-training is the legible
        // "this one costs more for a reason" line on the dossier.
        int count = quality > 0 ? rng.RandiRange(1, 2) : rng.RandiRange(0, 1);
        if (count == 0) return;

        var cls = c.UnitClass == "Fighter" ? MartialClass.Fighter : MartialClass.Ranger;
        var pool = StanceRegistry.All.Values
            .Where(s => s.Class == cls)
            .Select(s => s.Id)
            .ToList();

        for (int i = 0; i < count && pool.Count > 0; i++)
        {
            int pick = rng.RandiRange(0, pool.Count - 1);
            c.TrainedStanceIds.Add(pool[pick]);
            pool.RemoveAt(pick);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Price — stats + training priced in, gold (RecruitmentCost relocated
    // from the campus storefront to the hall, per §5a)
    // ═════════════════════════════════════════════════════════════════════

    private static int Price(Companion c, int quality)
    {
        int statLoad = c.BaseHP + c.BaseArmor * 4 + c.BaseAttackDamage * 5
                       + c.BaseAttackRange * 3 + c.BaseSpeed * 3 + c.BaseMana * 6;
        int price = 40 + statLoad * 2 + c.TrainedStanceIds.Count * 45
                    + (quality > 0 ? 25 : 0);
        return Mathf.Clamp(price / 5 * 5, 80, 420); // round to 5g steps
    }

    private static string BackstoryLine(Companion c) => c.UnitClass switch
    {
        "Fighter" => $"A {c.PersonalityTrait.ToLower()} sell-sword between patrons, waiting out the season in the hall.",
        "Ranger" => $"A {c.PersonalityTrait.ToLower()} scout who knows the roads out of town better than the roads in.",
        _ => $"A {c.PersonalityTrait.ToLower()} {c.School} adept who never found a seat at a court — and stopped waiting for one.",
    };

    private static string Sanitize(string cityId) =>
        cityId.Replace(":", "_").Replace(",", "_").Replace(" ", "_");
}
