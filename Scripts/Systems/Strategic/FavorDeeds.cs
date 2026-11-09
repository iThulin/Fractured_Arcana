using System.Collections.Generic;
// ============================================================
// FavorDeeds.cs: a favor spent as its deed (2026-11-05)
//
// Purpose:        Slice PV2b-2 of the provinces spec: ruling PR16 as PR44
//                 settled it (docs/provinces_v1_7g_deeds_and_courts.md,
//                 sections 14 and 17). One call-in, two uses. A favor owed
//                 to the guild still buys its field effect, or it is spent
//                 as its deed in its court's realm:
//                   Political     a charter, +20 influence (a major favor
//                                 +35) in the province the party stands in
//                   Intelligence  every site in that province becomes Known
//                   Military      +15 in the courtier's estate, and the
//                                 invasion of the realm falls back 10,
//                                 never far enough to decide it
//                   Economic      +10 in the courtier's estate, and the
//                                 estate's next yield of supplies doubles
//                                 if it is held for its benefits then
//                                 (PV2c, ProvinceBenefits.DoubleNextYield)
//                 Arcane and Passage favors have no deed until the font and
//                 the castle's hops (PV4).
//
//                 Every gain is a deed: the court's band weighs it, ill will
//                 in an estate halves it, and nothing moves while the
//                 interval walks (ProvinceInfluence.DeedIn).
// Layer:          Simulation (strategic)
// Collaborators:  ProvinceInfluence (DeedIn, MaxInfluence), ProvinceCourts
//                 (DeedGain, Who), CouncilState (Favor, CourtState,
//                 CourtierState), Warfront, ExpeditionManager (the call-in's
//                 choice of use, the charting of revealed sites),
//                 ProvinceBenefits (the Economic favor's doubled yield)
// See:            docs/provinces_v1_7g_deeds_and_courts.md
// ============================================================

/// <summary>A favor owed to the guild, spent as its deed (PR16, PR44).</summary>
public static class FavorDeeds
{
    /// <summary>A Political favor's charter, in the province the party stands in.</summary>
    public const int CharterGain = 20;
    /// <summary>A major Political favor's charter.</summary>
    public const int CharterMajorGain = 35;
    /// <summary>A Military favor, in the courtier's estate.</summary>
    public const int MarshalGain = 15;
    /// <summary>How far a Military favor moves the invasion of its realm back.</summary>
    public const int MarshalFrontPush = 10;
    /// <summary>An Economic favor, in the courtier's estate.</summary>
    public const int StewardGain = 10;
    /// <summary>The lowest a favor leaves an invasion's Advance. At 0 the invasion is thrown
    /// back, and only a fight at the front decides that, never a favor.</summary>
    public const int FrontFloor = 1;

    /// <summary>What a spent favor did: the line for the info bar, and the sites it brought
    /// to light, for the caller to chart.</summary>
    public sealed class Outcome
    {
        public string Message = "";
        public List<WorldPoi> Revealed = new();
    }

    /// <summary>True for a favor owed to the guild of a type with a deed.</summary>
    public static bool HasDeed(Favor f)
        => f != null && f.OwedToGuild
           && (f.Type == "Political" || f.Type == "Military" || f.Type == "Economic" || f.Type == "Intelligence");

    /// <summary>Why the favor cannot be spent as its deed with the party at (col,row), or
    /// null when it can. The caller checks the expedition and the territory first.</summary>
    public static string Ineligibility(CycleState cycle, Favor f, int col, int row)
    {
        if (f == null)
            return "No favor.";
        if (!f.OwedToGuild)
            return "Owed by the guild. Repay it, don't call it in.";
        if (!HasDeed(f))
            return $"{f.Type} favors have no deed yet.";
        if (cycle?.World == null || !cycle.World.HasProvinces())
            return "This world has no provinces.";
        if (Interval.Walking(cycle))
            return "Before the founding, no province moves.";
        int target = TargetProvince(cycle, f, col, row);
        if (target < 0)
            return StandsInPlace(f) ? "Stand on land in one of the realm's provinces." : "The courtier holds no estate.";
        if (f.Type == "Intelligence" && UnknownSites(cycle.World, target) == 0)
            return $"Every site in {NameOf(cycle.World.Provinces[target])} is known already.";
        return null;
    }

    /// <summary>What the deed would do now, in one line: the province by name and the gain
    /// after the court's weight.</summary>
    public static string Preview(CycleState cycle, Favor f, int col, int row)
    {
        int target = TargetProvince(cycle, f, col, row);
        if (target < 0)
            return Describe(f);
        var province = cycle.World.Provinces[target];
        string name = NameOf(province);
        switch (f.Type)
        {
            case "Intelligence":
            {
                int n = UnknownSites(cycle.World, target);
                return n == 1 ? $"One site in {name} becomes known." : $"{n} sites in {name} become known.";
            }
            case "Military":
            {
                var wf = FrontAgainst(cycle, f.KingdomId);
                int push = wf != null ? FrontPush(wf) : 0;
                string front = push > 0 ? $"; the invasion of {RealmName(cycle, wf)} falls back {push}" : "";
                return $"+{Gain(cycle, province, f)} influence in {name}, the courtier's estate{front}.";
            }
            case "Economic":
                return $"+{Gain(cycle, province, f)} influence in {name}, the courtier's estate, "
                       + "and its next yield of supplies doubles if it is held for its benefits then.";
            default:
                return $"A charter: +{Gain(cycle, province, f)} influence in {name}.";
        }
    }

    /// <summary>The deed in general, for a favor with no place to name yet.</summary>
    public static string Describe(Favor f) => f?.Type switch
    {
        "Political" => $"A charter: +{(f.IsMajor ? CharterMajorGain : CharterGain)} influence in the province the party stands in.",
        "Military" => $"+{MarshalGain} influence in the courtier's estate, and the invasion of the realm falls back {MarshalFrontPush}.",
        "Economic" => $"+{StewardGain} influence in the courtier's estate, and its next yield of supplies "
                      + "doubles if it is held for its benefits then.",
        "Intelligence" => "Every site in the province the party stands in becomes known.",
        _ => "No deed.",
    };

    /// <summary>Spend the favor as its deed. The caller has checked Ineligibility, consumes
    /// the favor and charts the revealed sites.</summary>
    public static Outcome Spend(CycleState cycle, Favor f, int col, int row)
    {
        var outcome = new Outcome();
        int target = TargetProvince(cycle, f, col, row);
        if (target < 0)
        {
            outcome.Message = Ineligibility(cycle, f, col, row) ?? "";
            return outcome;
        }
        var world = cycle.World;
        var province = world.Provinces[target];
        string name = NameOf(province);
        string who = ProvinceCourts.Who(Courtier(cycle, f));

        if (f.Type == "Intelligence")
        {
            int learned = 0;
            foreach (var poi in world.Pois)
            {
                if (poi == null || poi.Consumed || IndexAt(world, poi.X, poi.Y) != target)
                    continue;
                if (poi.Discovered && poi.Known)
                    continue;
                if (!poi.Discovered)
                    outcome.Revealed.Add(poi);
                poi.Discovered = true;
                poi.Known = true;
                learned++;
            }
            outcome.Message = learned == 1
                ? $"{who} sends a dossier on {name}: one site there is known."
                : $"{who} sends a dossier on {name}: {learned} sites there are known.";
            return outcome;
        }

        int before = province.Influence;
        string news = ProvinceInfluence.DeedIn(cycle, target, Amount(f));
        int gained = province.Influence - before;
        string line = f.Type switch
        {
            "Military" => $"{who} sends soldiers to {name}: +{gained} influence there.",
            "Economic" => $"{who} opens the markets of {name} to the guild: +{gained} influence there.",
            _ => $"{who} grants the guild a charter: +{gained} influence in {name}.",
        };
        if (f.Type == "Military")
        {
            var wf = FrontAgainst(cycle, f.KingdomId);
            int push = wf != null ? FrontPush(wf) : 0;
            if (push > 0)
            {
                wf.Advance -= push;
                line += $" The invasion of {RealmName(cycle, wf)} falls back {push}.";
            }
        }
        if (f.Type == "Economic")
        {
            // Provinces 7i (PR48): the estate's next yield of supplies doubles.
            ProvinceBenefits.DoubleNextYield(cycle, target);
            line += " Its next yield of supplies doubles, if it is held for its benefits at the new moon.";
        }
        if (!string.IsNullOrEmpty(news))
            line += " " + news;
        outcome.Message = line;
        return outcome;
    }

    // ── Places ───────────────────────────────────────────────────────────

    /// <summary>A Political or Intelligence favor lands where the party stands; the others in
    /// the courtier's estate.</summary>
    private static bool StandsInPlace(Favor f) => f.Type == "Political" || f.Type == "Intelligence";

    /// <summary>The province the deed lands in, or -1. Where the party stands, when that is
    /// land in one of the creditor's provinces; else the courtier's estate, or the realm's
    /// capital province when the courtier has none.</summary>
    private static int TargetProvince(CycleState cycle, Favor f, int col, int row)
    {
        var world = cycle?.World;
        if (world == null || f == null || !world.HasProvinces())
            return -1;
        var provinces = world.Provinces;
        if (StandsInPlace(f))
        {
            int here = IndexAt(world, col, row);
            return here >= 0 && provinces[here].KingdomId == f.KingdomId ? here : -1;
        }
        var courtier = Courtier(cycle, f);
        if (courtier != null && !string.IsNullOrEmpty(courtier.EstateProvinceId))
        {
            for (int i = 0; i < provinces.Count; i++)
            {
                if (provinces[i].Id == courtier.EstateProvinceId && provinces[i].KingdomId == f.KingdomId)
                    return i;
            }
        }
        for (int i = 0; i < provinces.Count; i++)
        {
            if (provinces[i].IsCapital && provinces[i].KingdomId == f.KingdomId)
                return i;
        }
        return -1;
    }

    /// <summary>The province of a land tile, or -1 off the map, on water or outside every province.</summary>
    private static int IndexAt(WorldData world, int x, int y)
    {
        if (!world.InBounds(x, y) || world.GetTile(x, y).IsWater)
            return -1;
        int index = world.ProvinceIndexAt(x, y);
        return index >= 0 && index < world.Provinces.Count ? index : -1;
    }

    private static int UnknownSites(WorldData world, int index)
    {
        int n = 0;
        foreach (var poi in world.Pois)
        {
            if (poi != null && !poi.Consumed && !(poi.Discovered && poi.Known) && IndexAt(world, poi.X, poi.Y) == index)
                n++;
        }
        return n;
    }

    private static string NameOf(Province province)
        => string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;

    // ── Amounts and the front ────────────────────────────────────────────

    private static int Amount(Favor f) => f.Type switch
    {
        "Political" => f.IsMajor ? CharterMajorGain : CharterGain,
        "Military" => MarshalGain,
        "Economic" => StewardGain,
        _ => 0,
    };

    /// <summary>The gain the deed would bring now: weighed by the court, up to the ceiling.</summary>
    private static int Gain(CycleState cycle, Province province, Favor f)
    {
        int gain = ProvinceCourts.DeedGain(cycle, province, Amount(f));
        return System.Math.Max(0, System.Math.Min(gain, ProvinceInfluence.MaxInfluence - province.Influence));
    }

    /// <summary>The open invasion of the realm nearest to taking it, or null.</summary>
    private static Warfront FrontAgainst(CycleState cycle, string kingdomId)
    {
        Warfront best = null;
        if (cycle?.Warfronts == null)
            return null;
        foreach (var wf in cycle.Warfronts)
        {
            if (wf == null || wf.Closed || wf.IsCacheSiege || wf.DefenderKingdomId != kingdomId)
                continue;
            if (best == null || wf.Advance > best.Advance)
                best = wf;
        }
        return best;
    }

    /// <summary>How far the favor moves the front back: its full push, but never to the line
    /// where the front is decided.</summary>
    private static int FrontPush(Warfront wf)
        => System.Math.Max(0, System.Math.Min(MarshalFrontPush, wf.Advance - FrontFloor));

    private static string RealmName(CycleState cycle, Warfront wf)
    {
        if (cycle?.Kingdoms != null && cycle.Kingdoms.TryGetValue(wf.DefenderKingdomId, out var k)
            && k != null && !string.IsNullOrEmpty(k.DisplayName))
            return k.DisplayName;
        return string.IsNullOrEmpty(wf.DefenderName) ? wf.DefenderKingdomId : wf.DefenderName;
    }

    private static CourtierState Courtier(CycleState cycle, Favor f)
    {
        if (cycle?.Council?.Courts == null || f == null || string.IsNullOrEmpty(f.KingdomId))
            return null;
        return cycle.Council.Courts.TryGetValue(f.KingdomId, out var court) && court != null
            ? court.GetCourtier(f.CourtierId)
            : null;
    }
}
