using Godot;
using System.Collections.Generic;

// ============================================================
// Vocations.cs: what a companion is good at on the MAP (2026-09-25)
//
// Purpose:        Ruled 2026-09-24 (Magos): a second identity axis beside
//                 Trait. Trait owns combat (one perk each, CompanionPerks);
//                 Vocation owns the field postings and nothing else. Six
//                 kinds, one per companion, rank 1 to 3 earned by MOONS
//                 WORKED at the posting the vocation names. Effects live
//                 only at the rank thresholds, as words, never as a slope,
//                 the same shape as Loyalty's tiers and a courtier's
//                 Influence 1 to 3.
//
//                 Two rules keep it from becoming a stat sheet by the back
//                 door. A vocation NEVER GATES a posting, it only improves
//                 one: anyone can garrison, a Warden garrisons well. And a
//                 vocation NEVER REACHES INTO COMBAT: the moment a Warden
//                 gets Armor, this is a stat sheet with extra steps.
//
// Layer:          Data / FeatureBuilders
// Collaborators:  Companion (Vocation, VocationMoons), FieldPostings (the
//                 effects), PostingThreats (Warden, Physician),
//                 SupplyCacheSystem (Quartermaster yield), CompanionRoster
//                 (backfill), CandidateGenerator (the hall's roll)
// ============================================================

public static class Vocations
{
    public const string Quartermaster = "Quartermaster";
    public const string Scout = "Scout";
    public const string Courtier = "Courtier";
    public const string Warden = "Warden";
    public const string Sapper = "Sapper";
    public const string Physician = "Physician";

    public static readonly string[] All = { Quartermaster, Scout, Courtier, Warden, Sapper, Physician };

    /// <summary>Moons worked at the vocation's posting that earn each rank.
    /// Rank 1 comes with the vocation; these are the two steps above it.
    /// Playtest numbers.</summary>
    public const int Rank2Moons = 4;
    public const int Rank3Moons = 10;
    public const int MaxRank = 3;

    // ── The effects, as numbers the postings read ─────────────────────────

    /// <summary>Quartermaster: depot yield on top of the overseer bonus.</summary>
    public const int QuartermasterYieldR1 = 2;
    public const int QuartermasterYieldR3 = 4;
    /// <summary>Scout: extra survey rings per moon, and extra splinters.</summary>
    public const int ScoutExtraRing = 1;
    public const int ScoutExtraSplintersR2 = 6;
    /// <summary>Scout rank 3: a survey takes two moons instead of three.</summary>
    public const int ScoutSurveyMoonsR3 = 2;
    /// <summary>Courtier rank 1: envoy gold divisor (the field already halves it).</summary>
    public const int CourtierGoldDivisor = 3;
    /// <summary>Warden rank 1: posting threat chance, in points.</summary>
    public const int WardenThreatReduction = 10;
    /// <summary>Warden rank 2: garrison supplies per moon.</summary>
    public const int WardenGarrisonSupplies = 1;
    /// <summary>Sapper: siege advance per moon.</summary>
    public const int SapperAdvanceR1 = 4;
    public const int SapperAdvanceR2 = 8;
    /// <summary>Sapper rank 3: siege supplies per moon.</summary>
    public const int SapperSiegeSupplies = 2;
    /// <summary>Physician rank 2: a posting-threat injury lasts this long.</summary>
    public const int PhysicianInjuryMoons = 1;

    /// <summary>Which posting kind a vocation is good at (FieldPostings kinds).
    /// A Physician is good at all of them, in the one way a Physician is.</summary>
    public static string PostingKindOf(string vocation) => vocation switch
    {
        Quartermaster => FieldPostings.Garrison,
        Scout => FieldPostings.Survey,
        Courtier => FieldPostings.Envoy,
        Warden => FieldPostings.Garrison,
        Sapper => FieldPostings.Siege,
        Physician => "",
        _ => "",
    };

    public static int RankOf(Companion c)
    {
        if (c == null || string.IsNullOrEmpty(c.Vocation))
        {
            return 0;
        }
        if (c.VocationMoons >= Rank3Moons) return 3;
        if (c.VocationMoons >= Rank2Moons) return 2;
        return 1;
    }

    public static string RankName(int rank) => rank switch
    {
        3 => "Master",
        2 => "Journeyman",
        1 => "Apprentice",
        _ => "",
    };

    /// <summary>"Master Scout", "Apprentice Warden", "" for none.</summary>
    public static string Describe(Companion c)
    {
        int r = RankOf(c);
        return r == 0 ? "" : $"{RankName(r)} {c.Vocation}";
    }

    /// <summary>The best rank of <paramref name="vocation"/> among a force's
    /// able members, or 0. Two Sappers do not stack; the better one counts.</summary>
    public static int BestRank(CycleState cycle, FieldParty force, string vocation)
    {
        int best = 0;
        if (cycle?.Companions == null || force?.MemberCompanionIds == null)
        {
            return 0;
        }
        foreach (var id in force.MemberCompanionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c == null || !c.IsRecruited || c.IsPermadead || c.IsInjured || c.Vocation != vocation)
            {
                continue;
            }
            best = Mathf.Max(best, RankOf(c));
        }
        return best;
    }

    /// <summary>The best rank of a vocation among the garrison standing on a
    /// tile, or 0. Read by the depot's recapture defence.</summary>
    public static int GarrisonRankAt(CycleState cycle, int x, int y, string vocation)
    {
        int best = 0;
        if (cycle?.FieldParties == null)
        {
            return 0;
        }
        foreach (var p in cycle.FieldParties)
        {
            if (p != null && p.State == FieldPartyState.Working && p.WorkKind == FieldPostings.Garrison
                && p.X == x && p.Y == y)
            {
                best = Mathf.Max(best, BestRank(cycle, p, vocation));
            }
        }
        return best;
    }

    /// <summary>Rank of the vocation a single companion holds, if it is this one.</summary>
    public static int RankIf(Companion c, string vocation)
        => c != null && c.Vocation == vocation ? RankOf(c) : 0;

    /// <summary>A moon of work at <paramref name="kind"/> was done by this
    /// force: every able member whose vocation names that kind earns a moon.
    /// A Physician earns a moon at any posting, since their work is the
    /// people, not the place. Returns the names that ranked up.</summary>
    public static List<string> RecordMoon(CycleState cycle, FieldParty force, string kind)
    {
        var promoted = new List<string>();
        if (cycle?.Companions == null || force?.MemberCompanionIds == null)
        {
            return promoted;
        }
        foreach (var id in force.MemberCompanionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c == null || !c.IsRecruited || c.IsPermadead || string.IsNullOrEmpty(c.Vocation))
            {
                continue;
            }
            string mine = PostingKindOf(c.Vocation);
            if (c.Vocation != Physician && mine != kind)
            {
                continue;
            }
            int before = RankOf(c);
            c.VocationMoons++;
            int after = RankOf(c);
            if (after > before)
            {
                promoted.Add($"{c.Name} is now a {RankName(after)} {c.Vocation}");
                GD.Print($"[Vocations] {c.Name}: {c.Vocation} rank {before} -> {after} ({c.VocationMoons} moons).");
            }
        }
        return promoted;
    }

    /// <summary>One line for the posting sheet: what this force's vocations
    /// bring to a posting of <paramref name="kind"/>, or "".</summary>
    public static string SheetLine(CycleState cycle, FieldParty force, string kind, IEnumerable<string> memberIds)
    {
        if (cycle?.Companions == null || memberIds == null)
        {
            return "";
        }
        var parts = new List<string>();
        var best = new Dictionary<string, (int rank, string name)>();
        foreach (var id in memberIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c == null || string.IsNullOrEmpty(c.Vocation) || c.IsInjured)
            {
                continue;
            }
            int r = RankOf(c);
            if (!best.TryGetValue(c.Vocation, out var cur) || r > cur.rank)
            {
                best[c.Vocation] = (r, c.Name);
            }
        }
        foreach (var kv in best)
        {
            string effect = EffectLine(kv.Key, kv.Value.rank, kind);
            if (!string.IsNullOrEmpty(effect))
            {
                parts.Add($"{kv.Value.name} ({RankName(kv.Value.rank)} {kv.Key}): {effect}");
            }
        }
        return string.Join("  ·  ", parts);
    }

    /// <summary>What a vocation at a rank does for a posting of this kind,
    /// in words, or "" when it does nothing here.</summary>
    public static string EffectLine(string vocation, int rank, string kind)
    {
        switch (vocation)
        {
            case Quartermaster:
                if (kind != FieldPostings.Garrison) return "";
                return rank >= 3 ? $"+{QuartermasterYieldR3} depot yield, garrison costs nothing"
                     : rank >= 2 ? $"+{QuartermasterYieldR1} depot yield, garrison costs nothing"
                     : $"+{QuartermasterYieldR1} depot yield";
            case Scout:
                if (kind != FieldPostings.Survey) return "";
                return rank >= 3 ? $"+{ScoutExtraRing} ring a moon, +{ScoutExtraSplintersR2} splinters, done in {ScoutSurveyMoonsR3} moons"
                     : rank >= 2 ? $"+{ScoutExtraRing} ring a moon, +{ScoutExtraSplintersR2} splinters"
                     : $"+{ScoutExtraRing} ring a moon";
            case Courtier:
                if (kind != FieldPostings.Envoy) return "";
                return rank >= 3 ? "a third of the gold, a moon faster, contact on arrival"
                     : rank >= 2 ? "a third of the gold, a moon faster"
                     : "a third of the gold";
            case Warden:
                if (kind == FieldPostings.Garrison)
                {
                    return rank >= 3 ? $"threats -{WardenThreatReduction}, garrison costs {WardenGarrisonSupplies}, twice the defence against a recapture"
                         : rank >= 2 ? $"threats -{WardenThreatReduction}, garrison costs {WardenGarrisonSupplies}"
                         : $"threats -{WardenThreatReduction}";
                }
                return $"threats -{WardenThreatReduction}";
            case Sapper:
                if (kind != FieldPostings.Siege) return "";
                return rank >= 3 ? $"+{SapperAdvanceR2} siege a moon, siege costs {SapperSiegeSupplies}"
                     : rank >= 2 ? $"+{SapperAdvanceR2} siege a moon"
                     : $"+{SapperAdvanceR1} siege a moon";
            case Physician:
                return rank >= 3 ? "the hurt heal a moon faster, a skirmish never wounds them"
                     : rank >= 2 ? $"the hurt heal a moon faster, skirmish wounds last {PhysicianInjuryMoons} moon"
                     : "the hurt heal a moon faster";
        }
        return "";
    }

    // ── Sources ────────────────────────────────────────────────────────────

    /// <summary>The hall's roll: even across the six. A market that always
    /// offers what the guild is short of is the valve against a vocation
    /// dying with its only holder.</summary>
    public static string Roll(RandomNumberGenerator rng)
        => All[rng.RandiRange(0, All.Length - 1)];

    /// <summary>Saves that predate vocations, and templates that name one:
    /// copy the template's vocation onto the saved companion when the saved
    /// one is empty. Never overwrites a vocation already held, since rank
    /// lives with it.</summary>
    public static void Backfill(Companion existing, Companion template)
    {
        if (existing == null || template == null)
        {
            return;
        }
        if (string.IsNullOrEmpty(existing.Vocation) && !string.IsNullOrEmpty(template.Vocation))
        {
            existing.Vocation = template.Vocation;
        }
    }
}
