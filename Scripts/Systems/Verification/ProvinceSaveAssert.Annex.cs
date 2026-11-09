using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

// ============================================================
// ProvinceSaveAssert.Annex.cs  (partial of ProvinceSaveAssert)
//
// Purpose:        The asserts of slice PV3b, the annexation of land no
//                 one rules (docs/provinces_v1_8_fronts_per_province.md
//                 section 13), on the scratch world as the west's fall
//                 leaves it: the bars round-trip and default on legacy
//                 JSON; who bids and at what pace (PR61); the guild's
//                 influence slowing every bar (PR62); the race, the
//                 forecast, the news, the report and the cache that
//                 follows (PR63, PR64); the tie rules; a realm that stops
//                 bordering losing its bar; the guild's claim ending every
//                 bid; an island no realm borders; and the debug lever.
//                 The expectations were fixed by a Python mirror of the
//                 rules on the same scratch world before the C# was built.
// Layer:          Verification
// Collaborators:  ProvinceSaveAssert.cs (AssertAll calls
//                 AssertAnnexation; Check, RoundTrip),
//                 ProvinceSaveAssert.Fronts.cs (FrontCycle, AddCache),
//                 KingdomTickSimulation.Annex.cs (AnnexBids,
//                 AnnexForecast, TickAnnexation, DebugAnnex),
//                 ProvinceControl.cs, ProvinceInfluence.cs
// See:            docs/provinces_v1_8_fronts_per_province.md section 13
// ============================================================

public static partial class ProvinceSaveAssert
{
    /// <summary>Every annexation assert (PV3b).</summary>
    private static bool AssertAnnexation(StringBuilder sb)
    {
        bool ok = AssertAnnexField(sb);
        ok &= AssertAnnexRace(sb);
        ok &= AssertAnnexTies(sb);
        ok &= AssertAnnexEnds(sb);
        ok &= AssertAnnexLever(sb);
        return ok;
    }

    /// <summary>Province.Annexation survives the trip, and legacy JSON reads an empty table
    /// rather than none.</summary>
    private static bool AssertAnnexField(StringBuilder sb)
    {
        var province = RoundTrip(new Province
        {
            Index = 5, Id = "province:kingdom_0:5", KingdomId = West, ControllerId = ProvinceControl.Neutral,
            Annexation = new Dictionary<string, int> { [East] = 25, [Lost] = 20 },
        });
        bool ok = Check(sb, "Province.Annexation survives the trip",
            province != null && province.Annexation != null && province.Annexation.Count == 2
            && Bar(province, East) == 25 && Bar(province, Lost) == 20);
        var legacy = JsonSerializer.Deserialize<Province>("{\"name\":\"North Weald\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "legacy JSON: no bids, as an empty table",
            legacy != null && legacy.Annexation != null && legacy.Annexation.Count == 0);

        sb.AppendLine(ok ? "  Annexation fields: PASS" : "  Annexation fields: FAIL");
        return ok;
    }

    /// <summary>The race after the west's fall (PR61, PR63, PR64). A realm with no state in
    /// the cycle bids for nothing. With one, a lunation (the whole Tick) starts three bids:
    /// the east's on Northeast Fields at 35 (four borders), the east's and the third realm's
    /// on Far South Fields at 25 and 20, each with its news. The report lists them; the
    /// forecast reads the east in 2 and 3 lunations; the guild's influence at 45 slows the
    /// bars to 15 and 10 a lunation (PR62), at 39 it does not. The east annexes Northeast
    /// Fields in the third lunation, the cache no one held there with it and the guild's
    /// influence there untouched, then Far South Fields in the fourth, and the third
    /// realm's bar goes.</summary>
    private static bool AssertAnnexRace(StringBuilder sb)
    {
        var cycle = AnnexCycle(false, out int north, out int farSouth);
        if (north < 0)
        {
            sb.AppendLine("  Annexation race: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        var world = cycle.World;
        var provinces = world.Provinces;
        var stateless = KingdomTickSimulation.AnnexBids(cycle, farSouth);
        bool ok = Check(sb, $"a realm with no state in the cycle bids for nothing: the east alone bids for Far South Fields, 25 a lunation on two borders ({Gains(stateless)})",
            stateless.Count == 1 && stateless[0].KingdomId == East && stateless[0].Borders == 2 && stateless[0].Gain == 25);

        cycle.Kingdoms[Lost] = LostState();
        int cache = AddCache(world, 18, 8, West, ProvinceControl.Neutral);
        provinces[north].Influence = 20;
        var bids = KingdomTickSimulation.AnnexBids(cycle, farSouth);
        string lead = KingdomTickSimulation.AnnexForecast(bids, out int lunations);
        ok &= Check(sb, $"with a state the third realm bids too, 20 a lunation on one border, and the east would annex it in 4 lunations ({Gains(bids)}; {lead} in {lunations})",
            bids.Count == 2 && bids[0].KingdomId == East && bids[0].Gain == 25
            && bids[1].KingdomId == Lost && bids[1].Gain == 20 && lead == East && lunations == 4);

        // The first lunation is the whole Tick, so the wiring is asserted too.
        int reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.Tick(cycle);
        var news = cycle.PendingSiegeReports.Skip(reports).ToList();
        ok &= Check(sb, $"a lunation starts three bids: the east's on Northeast Fields at 35, the east's and the third realm's on Far South Fields at 25 and 20 (PR61; {Bars(provinces[north])}; {Bars(provinces[farSouth])})",
            Bar(provinces[north], East) == 35 && provinces[north].Annexation.Count == 1
            && Bar(provinces[farSouth], East) == 25 && Bar(provinces[farSouth], Lost) == 20
            && provinces[farSouth].Annexation.Count == 2);
        ok &= Check(sb, "the news says so, once for each bid",
            news.Count(l => l.Contains("moves to annex Northeast Fields, which no one rules.")) == 1
            && news.Count(l => l.Contains("moves to annex Far South Fields, which no one rules.")) == 2);
        string report = ProvinceInfluence.Report(cycle);
        ok &= Check(sb, "the report lists the bids on land no one rules",
            report.Contains("no one's, annexing: Eastreach 35") && report.Contains("no one's, annexing: Eastreach 25, Lostmere 20"));
        var northBids = KingdomTickSimulation.AnnexBids(cycle, north);
        string northLead = KingdomTickSimulation.AnnexForecast(northBids, out int northIn);
        bids = KingdomTickSimulation.AnnexBids(cycle, farSouth);
        lead = KingdomTickSimulation.AnnexForecast(bids, out lunations);
        ok &= Check(sb, $"the forecast: the east annexes Northeast Fields in 2 lunations and Far South Fields in 3 ({northLead} in {northIn}; {lead} in {lunations})",
            northLead == East && northIn == 2 && lead == East && lunations == 3);

        provinces[farSouth].Influence = 45;
        var slowed = KingdomTickSimulation.AnnexBids(cycle, farSouth);
        string slowedLead = KingdomTickSimulation.AnnexForecast(slowed, out int slowedIn);
        provinces[farSouth].Influence = 39;
        var unslowed = KingdomTickSimulation.AnnexBids(cycle, farSouth);
        provinces[farSouth].Influence = 0;
        ok &= Check(sb, $"the guild's influence at 45 slows every bar by 10 and the east then needs 5 lunations; at 39 it does not (PR62; {Gains(slowed)}; {slowedLead} in {slowedIn}; {Gains(unslowed)})",
            slowed.Count == 2 && slowed[0].Gain == 15 && slowed[1].Gain == 10 && slowedLead == East && slowedIn == 5
            && unslowed.Count == 2 && unslowed[0].Gain == 25 && unslowed[1].Gain == 20);

        reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.TickAnnexation(cycle);
        ok &= Check(sb, $"a second lunation: 70 on Northeast Fields, 50 and 40 on Far South Fields, and no news ({Bars(provinces[north])}; {Bars(provinces[farSouth])})",
            Bar(provinces[north], East) == 70 && Bar(provinces[farSouth], East) == 50 && Bar(provinces[farSouth], Lost) == 40
            && cycle.PendingSiegeReports.Count == reports);

        reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.TickAnnexation(cycle);
        news = cycle.PendingSiegeReports.Skip(reports).ToList();
        ok &= Check(sb, $"in the third the east annexes Northeast Fields, in this lunation, and its bids end (PR63, PR64; {Bars(provinces[north])})",
            ProvinceControl.RulerOf(cycle, provinces[north]) == East && provinces[north].ControllerId == East
            && provinces[north].ControlSinceLunation == 5 && provinces[north].Annexation.Count == 0);
        ok &= Check(sb, $"the cache no one held there passes to the east, the annexation's news before the cache's, and the guild's influence there is untouched ({provinces[north].Influence})",
            world.Pois[cache].SupplyControllerId == East && provinces[north].Influence == 20
            && news.Count >= 2 && news[0].Contains("annexes Northeast Fields, which no one ruled.")
            && news[1].Contains("supply cache"));
        ok &= Check(sb, $"Far South Fields stands at 75 and 60 ({Bars(provinces[farSouth])})",
            Bar(provinces[farSouth], East) == 75 && Bar(provinces[farSouth], Lost) == 60);

        reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.TickAnnexation(cycle);
        news = cycle.PendingSiegeReports.Skip(reports).ToList();
        ok &= Check(sb, $"in the fourth the east fills first and annexes Far South Fields, and the third realm's bar goes with the bids ({Bars(provinces[farSouth])})",
            ProvinceControl.RulerOf(cycle, provinces[farSouth]) == East && provinces[farSouth].Annexation.Count == 0
            && news.Any(l => l.Contains("annexes Far South Fields, which no one ruled.")));
        reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.TickAnnexation(cycle);
        ok &= Check(sb, "with no land left to no one, a further lunation changes nothing",
            cycle.PendingSiegeReports.Count == reports && ProvinceControl.RulerOf(cycle, provinces[north]) == East
            && ProvinceControl.RulerOf(cycle, provinces[farSouth]) == East);

        sb.AppendLine(ok ? "  Annexation race: PASS" : "  Annexation race: FAIL");
        return ok;
    }

    /// <summary>Bars that fill in the same lunation (PR63): the one that would go furthest
    /// wins (80 and 90 go to 105 and 110, so the third realm, on one border to two), and when
    /// they would go as far, the most borders (75 and 80 both go to 100: the east).</summary>
    private static bool AssertAnnexTies(StringBuilder sb)
    {
        bool ok = true;
        foreach (var (eastBar, lostBar, winner) in new[] { (80, 90, Lost), (75, 80, East) })
        {
            var cycle = AnnexCycle(true, out int north, out int farSouth);
            if (north < 0)
            {
                sb.AppendLine("  Annexation ties: FAIL (the scratch world's provinces are not where they were)");
                return false;
            }
            var province = cycle.World.Provinces[farSouth];
            province.Annexation[East] = eastBar;
            province.Annexation[Lost] = lostBar;
            KingdomTickSimulation.TickAnnexation(cycle);
            string ruler = ProvinceControl.RulerOf(cycle, province);
            ok &= Check(sb, $"bars at {eastBar} and {lostBar} that fill together give Far South Fields to {winner} ({ruler})",
                ruler == winner && province.Annexation.Count == 0);
        }

        sb.AppendLine(ok ? "  Annexation ties: PASS" : "  Annexation ties: FAIL");
        return ok;
    }

    /// <summary>How bids end short of an annexation: a realm that stops bordering loses its
    /// bar; the guild's hold claims the province first and ends every bid, and no realm bids
    /// for the guild's own land (PR10, PR63); an island no realm borders draws no bid.</summary>
    private static bool AssertAnnexEnds(StringBuilder sb)
    {
        var cycle = AnnexCycle(true, out int north, out int farSouth);
        var world = cycle.World;
        var provinces = world.Provinces;
        int southFields = world.ProvinceIndexAt(2, 24);
        int island = world.ProvinceIndexAt(42, 27);   // the east's Southeast Fields, an island
        if (north < 0 || southFields < 0 || island < 0)
        {
            sb.AppendLine("  Annexation ends: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        var province = provinces[farSouth];
        province.Annexation[East] = 25;
        province.Annexation[Lost] = 20;
        provinces[southFields].ControllerId = ProvinceControl.Guild;
        KingdomTickSimulation.TickAnnexation(cycle);
        bool ok = Check(sb, $"with South Fields the guild's, the third realm no longer borders Far South Fields and loses its bar; the east goes on to 50 ({Bars(province)})",
            Bar(province, East) == 50 && !province.Annexation.ContainsKey(Lost));

        province.Influence = 30;
        string claim = ProvinceInfluence.Add(cycle, 9, 26, 30);
        ok &= Check(sb, $"the guild's hold claims Far South Fields first, and every bid ends (PR10, PR63; {Bars(province)})",
            ProvinceControl.RulerOf(cycle, province) == ProvinceControl.Guild && province.Annexation.Count == 0
            && claim.Contains("the guild's hold makes it the guild's"));
        KingdomTickSimulation.TickAnnexation(cycle);
        ok &= Check(sb, "no realm bids for the guild's own land",
            KingdomTickSimulation.AnnexBids(cycle, farSouth).Count == 0 && province.Annexation.Count == 0
            && ProvinceControl.RulerOf(cycle, province) == ProvinceControl.Guild);

        provinces[island].ControllerId = ProvinceControl.Neutral;
        KingdomTickSimulation.TickAnnexation(cycle);
        ok &= Check(sb, "an island no realm borders draws no bid, and stays no one's",
            KingdomTickSimulation.AnnexBids(cycle, island).Count == 0 && provinces[island].Annexation.Count == 0
            && ProvinceControl.RulerOf(cycle, provinces[island]) == ProvinceControl.Neutral);

        sb.AppendLine(ok ? "  Annexation ends: PASS" : "  Annexation ends: FAIL");
        return ok;
    }

    /// <summary>Gate PV3b's lever (Push Annex +40): it pushes the bid furthest along, the
    /// east's on Northeast Fields (the first province no one rules with a bidder, all bars
    /// being 0), to 40 and 80, and the third push annexes it at once.</summary>
    private static bool AssertAnnexLever(StringBuilder sb)
    {
        var cycle = AnnexCycle(true, out int north, out int farSouth);
        if (north < 0)
        {
            sb.AppendLine("  Annexation lever: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        var provinces = cycle.World.Provinces;
        string first = KingdomTickSimulation.DebugAnnex(cycle, 40);
        string second = KingdomTickSimulation.DebugAnnex(cycle, 40);
        bool ok = Check(sb, $"the lever pushes the east's bid on Northeast Fields to 40 and 80 ({Bars(provinces[north])}; {Bars(provinces[farSouth])})",
            first != null && first.Contains("moves to annex Northeast Fields") && second != null
            && Bar(provinces[north], East) == 80 && provinces[farSouth].Annexation.Count == 0
            && ProvinceControl.RulerOf(cycle, provinces[north]) == ProvinceControl.Neutral);
        string third = KingdomTickSimulation.DebugAnnex(cycle, 40);
        ok &= Check(sb, "a third push annexes it at once",
            third != null && third.Contains("annexes Northeast Fields, which no one ruled.")
            && ProvinceControl.RulerOf(cycle, provinces[north]) == East && provinces[north].Annexation.Count == 0);

        sb.AppendLine(ok ? "  Annexation lever: PASS" : "  Annexation lever: FAIL");
        return ok;
    }

    // ── Annexation helpers ────────────────────────────────────────────────

    /// <summary>The front cycle as the west's fall leaves it (AssertCollapse): the east rules
    /// the west capital and Southeast Fields, the guild North Fields, the third realm South
    /// Fields, and no one Northeast Fields and Far South Fields, whose indices come out. The
    /// third realm has a state in the cycle when <paramref name="lostState"/>. Both indices are
    /// -1 when the scratch world's provinces are not where they were.</summary>
    private static CycleState AnnexCycle(bool lostState, out int north, out int farSouth)
    {
        var cycle = FrontCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        int capital = world.ProvinceIndexAt(6, 15);
        int south = world.ProvinceIndexAt(18, 25);
        int northFields = world.ProvinceIndexAt(3, 4);
        int southFields = world.ProvinceIndexAt(2, 24);
        north = world.ProvinceIndexAt(18, 5);
        farSouth = world.ProvinceIndexAt(9, 26);
        if (capital < 0 || south < 0 || northFields < 0 || southFields < 0 || north < 0 || farSouth < 0)
        {
            north = -1;
            farSouth = -1;
            return cycle;
        }
        provinces[capital].ControllerId = East;
        provinces[south].ControllerId = East;
        provinces[northFields].ControllerId = ProvinceControl.Guild;
        provinces[southFields].ControllerId = Lost;
        provinces[north].ControllerId = ProvinceControl.Neutral;
        provinces[farSouth].ControllerId = ProvinceControl.Neutral;
        if (lostState)
            cycle.Kingdoms[Lost] = LostState();
        return cycle;
    }

    /// <summary>The third realm's state, so that it may annex: Lostmere, ruled by "f_lost".</summary>
    private static KingdomState LostState()
        => new KingdomState { RegionId = Lost, DisplayName = "Lostmere", ControllingFactionId = "f_lost" };

    /// <summary>A kingdom's bar on a province, or -1 when it has none.</summary>
    private static int Bar(Province province, string kingdomId)
        => province?.Annexation != null && province.Annexation.TryGetValue(kingdomId, out int bar) ? bar : -1;

    /// <summary>A province's bars for a message: "kingdom_1 25, kingdom_9 20", or "none".</summary>
    private static string Bars(Province province)
        => province?.Annexation == null || province.Annexation.Count == 0
            ? "none"
            : string.Join(", ", province.Annexation.OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
                .Select(kv => $"{kv.Key} {kv.Value}"));

    /// <summary>Bids for a message: "kingdom_1 +25, kingdom_9 +20", or "none".</summary>
    private static string Gains(List<KingdomTickSimulation.AnnexBid> bids)
        => bids == null || bids.Count == 0
            ? "none"
            : string.Join(", ", bids.Select(b => $"{b.KingdomId} +{b.Gain}"));
}
