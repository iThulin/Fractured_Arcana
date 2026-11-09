using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

// ============================================================
// ProvinceSaveAssert.Fronts.cs  (partial of ProvinceSaveAssert)
//
// Purpose:        The asserts of slice PV3a, fronts per province
//                 (docs/provinces_v1_8_fronts_per_province.md section
//                 9, item 1), on the scratch world: the new fields
//                 round-trip and default on legacy JSON; the ruler
//                 and the stance the threat rolls read (PR57); the
//                 province a front contests and where it stands
//                 (PR53); a province taken, the front moving on at 60,
//                 a party finding it moved on, a front thrown back
//                 keeping its takings (PR52, PR54); the caches
//                 following (PR58); a capital taken and the collapse
//                 (PR6, PR55); a fallen realm that no longer ticks; the
//                 guild's claim of land no one rules (PR10); a Seize per
//                 province and the Hostile court (PR56); and a seized
//                 capital felling its realm. The expectations were
//                 fixed by a Python mirror of the rules on the same
//                 scratch world before the C# was built.
// Layer:          Verification
// Collaborators:  ProvinceSaveAssert.cs (AssertAll calls AssertFronts;
//                 the scratch world, the court cycle, Check, RoundTrip,
//                 SetScore), KingdomTickSimulation.Fronts.cs (OpenFront,
//                 Settle, MigrateLegacyFronts, NeighborsOf),
//                 ProvinceControl.cs, ProvinceInfluence.cs,
//                 SupplyCacheSystem.cs
// See:            docs/provinces_v1_8_fronts_per_province.md
// ============================================================

public static partial class ProvinceSaveAssert
{
    /// <summary>Every fronts assert (PV3a).</summary>
    private static bool AssertFronts(StringBuilder sb)
    {
        bool ok = AssertFrontFields(sb);
        ok &= AssertRulers(sb);
        ok &= AssertWalkingFront(sb);
        ok &= AssertCollapse(sb);
        ok &= AssertSeize(sb);
        ok &= AssertSeizedCapital(sb);
        return ok;
    }

    /// <summary>Province.ControllerId and ControlSinceLunation, Warfront.ProvinceId and
    /// CycleState.PendingWarfrontProvinceId survive the trip, and legacy JSON reads a province
    /// ruled by its own kingdom, never changed, a front without a province and nothing pending.</summary>
    private static bool AssertFrontFields(StringBuilder sb)
    {
        var province = RoundTrip(new Province
        {
            Index = 2, Id = "province:kingdom_0:2", KingdomId = West, ControllerId = East, ControlSinceLunation = 7,
        });
        bool ok = Check(sb, "Province.ControllerId and ControlSinceLunation survive the trip",
            province != null && province.ControllerId == East && province.ControlSinceLunation == 7);
        var front = RoundTrip(new Warfront { Id = "kingdom_1>kingdom_0", ProvinceId = "province:kingdom_0:2", Advance = 60 });
        ok &= Check(sb, "Warfront.ProvinceId survives the trip",
            front != null && front.ProvinceId == "province:kingdom_0:2" && front.Advance == 60);
        var cycle = RoundTrip(new CycleState { PendingWarfrontProvinceId = "province:kingdom_1:4" });
        ok &= Check(sb, "CycleState.PendingWarfrontProvinceId survives the trip",
            cycle != null && cycle.PendingWarfrontProvinceId == "province:kingdom_1:4");

        var legacyProvince = JsonSerializer.Deserialize<Province>("{\"name\":\"North Weald\"}", SaveManager.JsonOptions);
        var legacyFront = JsonSerializer.Deserialize<Warfront>("{\"id\":\"kingdom_1>kingdom_0\",\"advance\":40}", SaveManager.JsonOptions);
        var legacyCycle = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":2}", SaveManager.JsonOptions);
        ok &= Check(sb, "legacy JSON: a province its own kingdom rules and has never lost, a front with no province, nothing pending",
            legacyProvince != null && legacyProvince.ControllerId == "" && legacyProvince.ControlSinceLunation == -1
            && legacyFront != null && legacyFront.ProvinceId == "" && legacyFront.Advance == 40
            && legacyCycle != null && legacyCycle.PendingWarfrontProvinceId == "");

        sb.AppendLine(ok ? "  Front fields: PASS" : "  Front fields: FAIL");
        return ok;
    }

    /// <summary>The ruler and the stance the four threat rolls read (PR57): a province's own
    /// kingdom and its court; the kingdom that took it and that court; the guild's own land as
    /// Allied with no kingdom's soldiers; land no one rules as Neutral; water as the caller's
    /// wilds; a realm the guild seized whole before fronts walked as the guild's (section 7);
    /// and neighbours by the provinces the realms rule.</summary>
    private static bool AssertRulers(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        SetScore(cycle.Council.Courts[West], 3);    // Welcome: Friendly
        SetScore(cycle.Council.Courts[East], -6);   // Hostile
        int south = world.ProvinceIndexAt(18, 25);  // Southeast Fields, the west's
        int eastCapital = world.ProvinceIndexAt(30, 15);
        if (south < 0 || eastCapital < 0)
        {
            sb.AppendLine("  Rulers: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        var province = provinces[south];
        bool ok = Check(sb, "a province is ruled by its own kingdom, and the stance is that court's",
            ProvinceControl.RulerOf(cycle, province) == West && ProvinceControl.KingdomAt(cycle, 18, 25) == West
            && ProvinceControl.StanceAt(cycle, 18, 25, KingdomStance.Hostile) == KingdomStance.Friendly);
        province.ControllerId = East;
        ok &= Check(sb, "taken by the east, it reads the east and its Hostile court",
            ProvinceControl.KingdomAt(cycle, 18, 25) == East
            && ProvinceControl.StanceAt(cycle, 18, 25, KingdomStance.Neutral) == KingdomStance.Hostile);
        province.ControllerId = ProvinceControl.Guild;
        ok &= Check(sb, "the guild's own land reads as Allied, and no kingdom's soldiers walk it",
            ProvinceControl.KingdomAt(cycle, 18, 25) == ""
            && ProvinceControl.StanceAt(cycle, 18, 25, KingdomStance.Hostile) == KingdomStance.Allied);
        province.ControllerId = ProvinceControl.Neutral;
        ok &= Check(sb, "land no one rules reads as Neutral, with no kingdom",
            ProvinceControl.KingdomAt(cycle, 18, 25) == ""
            && ProvinceControl.StanceAt(cycle, 18, 25, KingdomStance.Hostile) == KingdomStance.Neutral);
        province.ControllerId = "";
        ok &= Check(sb, "water reads as the wilds the caller passes",
            ProvinceControl.KingdomAt(cycle, 31, 5) == ""
            && ProvinceControl.StanceAt(cycle, 31, 5, KingdomStance.Neutral) == KingdomStance.Neutral
            && ProvinceControl.StanceAt(cycle, 31, 5, KingdomStance.Hostile) == KingdomStance.Hostile);
        cycle.Kingdoms[East].ControllingFactionId = KingdomTickSimulation.GuildFactionId;
        ok &= Check(sb, "a realm the guild seized whole before fronts walked reads as the guild's on every province, standing",
            ProvinceControl.RulerOf(cycle, provinces[eastCapital]) == ProvinceControl.Guild
            && ProvinceControl.StanceAt(cycle, 30, 15, KingdomStance.Hostile) == KingdomStance.Allied
            && !ProvinceControl.KingdomFallen(cycle, East));
        cycle.Kingdoms[East].ControllingFactionId = "";
        ok &= Check(sb, "the realms border where the provinces they rule touch",
            KingdomTickSimulation.NeighborsOf(cycle, West).Contains(East)
            && KingdomTickSimulation.NeighborsOf(cycle, East).Contains(West));

        sb.AppendLine(ok ? "  Rulers: PASS" : "  Rulers: FAIL");
        return ok;
    }

    /// <summary>A front of the east on the west (PR52, PR53, PR54, PR58): it opens on
    /// Southeast Fields (as near the west capital as Northeast Fields, with the longer border
    /// to the east, 33 edges to 30), its focus on that border nearest the seat at (23,22) and
    /// its stronghold at (24,20); at 100 the east takes it in this lunation, the guild's hold
    /// there goes, the west's cache there passes to the east while the guild's and a third
    /// realm's stay, and the front moves on to the west capital at 60, sited at (12,16) and
    /// (15,15); a party that went to Southeast Fields finds it moved on and swings nothing, one
    /// that went to Crownland swings it 45; at 0 it is thrown back, and the east keeps what it
    /// took. A front opened before fronts walked contests the province at its focus.</summary>
    private static bool AssertWalkingFront(StringBuilder sb)
    {
        var cycle = FrontCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        int capital = world.ProvinceIndexAt(6, 15);
        int south = world.ProvinceIndexAt(18, 25);
        int north = world.ProvinceIndexAt(22, 15);   // Northeast Fields, where a legacy front stands
        if (capital < 0 || south < 0 || north < 0)
        {
            sb.AppendLine("  Walking front: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }

        // Section 7: a front from before fronts walked contests the province at its focus.
        var legacy = new Warfront
        {
            Id = "kingdom_1>kingdom_0", AggressorKingdomId = East, DefenderKingdomId = West, Advance = 30,
            FocusCol = 22, FocusRow = 15, StrongholdCol = 25, StrongholdRow = 15,
        };
        cycle.Warfronts.Add(legacy);
        KingdomTickSimulation.MigrateLegacyFronts(cycle);
        bool ok = Check(sb, "a front opened before fronts walked contests the province at its focus and keeps its focus and stronghold",
            legacy.ProvinceId == provinces[north].Id && legacy.FocusCol == 22 && legacy.FocusRow == 15
            && legacy.StrongholdCol == 25 && legacy.StrongholdRow == 15);
        cycle.Warfronts.Clear();

        int westCache = AddCache(world, 20, 28, West, "");
        int guildCache = AddCache(world, 21, 29, West, ProvinceControl.Guild);
        int thirdCache = AddCache(world, 19, 29, West, Lost);
        provinces[south].Influence = 70;
        provinces[south].Held = true;

        int reports = cycle.PendingSiegeReports.Count;
        var front = KingdomTickSimulation.OpenFront(cycle, West, East);
        if (front == null)
        {
            sb.AppendLine("  Walking front: FAIL (no front opened)");
            return false;
        }
        ok &= Check(sb, $"the front opens on Southeast Fields at 15, nearest the capital with the longer border (PR53; {front.ProvinceId}, {front.Advance})",
            front.ProvinceId == provinces[south].Id && front.Advance == 15);
        ok &= Check(sb, $"its focus on that border nearest the seat (23,22), its stronghold in the east's ground (24,20); now ({front.FocusCol},{front.FocusRow}) and ({front.StrongholdCol},{front.StrongholdRow})",
            front.FocusCol == 23 && front.FocusRow == 22 && front.StrongholdCol == 24 && front.StrongholdRow == 20);
        ok &= Check(sb, "the war's news names the province",
            cycle.PendingSiegeReports.Count == reports + 1 && cycle.PendingSiegeReports[reports].Contains("Southeast Fields"));

        // At 100 the east takes it, and the front moves on at 60.
        front.PlayerSeizing = true;
        front.Advance = 100;
        reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.Settle(cycle, front);
        var news = cycle.PendingSiegeReports.Skip(reports).ToList();
        ok &= Check(sb, "at 100 the east takes Southeast Fields, in this lunation",
            ProvinceControl.RulerOf(cycle, provinces[south]) == East && provinces[south].ControllerId == East
            && provinces[south].ControlSinceLunation == 5);
        ok &= Check(sb, "the guild's influence there drops to 0 and its hold with it, and the news says so (PR54)",
            provinces[south].Influence == 0 && !provinces[south].Held
            && news.Count > 0 && news[0].Contains("takes Southeast Fields") && news[0].Contains("Your hold there is lost"));
        ok &= Check(sb, $"the front moves on to the west capital at 60, and the bid goes with the province taken (PR52; {front.ProvinceId}, {front.Advance})",
            cycle.Warfronts.Contains(front) && !front.Closed && front.ProvinceId == provinces[capital].Id
            && front.Advance == ProvinceControl.FrontMomentumAdvance && !front.PlayerSeizing
            && news[0].Contains("The front moves on to Crownland"));
        ok &= Check(sb, $"it stands on Crownland's new border (12,16), its stronghold in the ground just taken (15,15); now ({front.FocusCol},{front.FocusRow}) and ({front.StrongholdCol},{front.StrongholdRow})",
            front.FocusCol == 12 && front.FocusRow == 16 && front.StrongholdCol == 15 && front.StrongholdRow == 15);
        ok &= Check(sb, "the west's cache there passes to the east; the guild's and a third realm's stay theirs (PR58)",
            world.Pois[westCache].SupplyControllerId == East
            && world.Pois[guildCache].SupplyControllerId == ProvinceControl.Guild
            && world.Pois[thirdCache].SupplyControllerId == Lost);
        ok &= Check(sb, "the east and the west still border, across the new line",
            KingdomTickSimulation.NeighborsOf(cycle, East).Contains(West));

        // A party that went to Southeast Fields finds the front gone; one that went to Crownland swings it.
        cycle.PendingWarfrontProvinceId = provinces[south].Id;
        reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.ApplyIntervention(cycle, front.Id, WarfrontSide.Defend, success: true);
        ok &= Check(sb, "a party that went to a province the front has left finds it moved on, and swings nothing",
            front.Advance == 60 && cycle.PendingSiegeReports.Skip(reports).Any(l => l.Contains("moved on")));
        cycle.PendingWarfrontProvinceId = provinces[capital].Id;
        KingdomTickSimulation.ApplyIntervention(cycle, front.Id, WarfrontSide.Defend, success: true);
        ok &= Check(sb, $"one that went to Crownland holds it: 60 to 15 ({front.Advance})", front.Advance == 15);
        cycle.PendingWarfrontProvinceId = "";

        // At 0 the invasion is thrown back, and the east keeps what it took.
        front.Advance = 0;
        reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.Settle(cycle, front);
        ok &= Check(sb, "at 0 it is thrown back at Crownland and the war ends; the east keeps Southeast Fields (PR52)",
            !cycle.Warfronts.Contains(front) && front.Resolution == "repelled"
            && ProvinceControl.RulerOf(cycle, provinces[south]) == East
            && ProvinceControl.RulerOf(cycle, provinces[capital]) == West
            && cycle.PendingSiegeReports.Skip(reports).Any(l => l.Contains("thrown back at Crownland")));
        ok &= Check(sb, "the report names the taken province's ruler",
            ProvinceInfluence.Report(cycle).Contains("ruled by Eastreach since lunation 5"));

        sb.AppendLine(ok ? "  Walking front: PASS" : "  Walking front: FAIL");
        return ok;
    }

    /// <summary>A capital taken (PR6, PR55): with Southeast Fields the east's from an earlier
    /// war and South Fields a third realm's, the next war contests the west capital itself. At
    /// 100 the west falls: the east keeps Crownland and Southeast Fields; the guild rules North
    /// Fields and Far South Fields, which it held; Northeast Fields goes to no one; the third
    /// realm keeps South Fields; the west's faction is the east's; one standing step at the
    /// east's court and at the third realm's (it borders Far South Fields), none at the fallen
    /// court; the west's own war ends; the caches follow. The fallen west borders no one and
    /// no longer ticks. Then the guild's claim (PR10): land no one rules fades as before, and a
    /// deed that holds it makes it the guild's, which does not fade.</summary>
    private static bool AssertCollapse(StringBuilder sb)
    {
        var cycle = FrontCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        int capital = world.ProvinceIndexAt(6, 15);
        int south = world.ProvinceIndexAt(18, 25);
        int north = world.ProvinceIndexAt(18, 5);        // Northeast Fields
        int northFields = world.ProvinceIndexAt(3, 4);   // North Fields
        int southFields = world.ProvinceIndexAt(2, 24);  // South Fields
        int farSouth = world.ProvinceIndexAt(9, 26);     // Far South Fields
        if (capital < 0 || south < 0 || north < 0 || northFields < 0 || southFields < 0 || farSouth < 0)
        {
            sb.AppendLine("  Collapse: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        provinces[south].ControllerId = East;
        provinces[southFields].ControllerId = Lost;
        provinces[northFields].Influence = 70;
        provinces[northFields].Held = true;
        provinces[farSouth].Influence = 65;
        provinces[farSouth].Held = true;
        int noOnesCache = AddCache(world, 18, 8, West, "");
        int heldCache = AddCache(world, 5, 2, West, "");
        var courts = cycle.Council.Courts;
        int eastPenalty = courts[East].StandingPenalty;
        int lostPenalty = courts[Lost].StandingPenalty;
        int westPenalty = courts[West].StandingPenalty;
        var westWar = new Warfront { Id = "kingdom_0>kingdom_9", AggressorKingdomId = West, DefenderKingdomId = Lost, Advance = 40 };
        cycle.Warfronts.Add(westWar);

        var front = KingdomTickSimulation.OpenFront(cycle, West, East);
        bool ok = Check(sb, "with Southeast Fields the east's, the next war contests the west capital itself",
            front != null && front.ProvinceId == provinces[capital].Id);
        if (front == null)
        {
            sb.AppendLine("  Collapse: FAIL (no front opened)");
            return false;
        }
        front.Advance = 100;
        int reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.Settle(cycle, front);
        var news = cycle.PendingSiegeReports.Skip(reports).ToList();
        ok &= Check(sb, "taking the capital fells the west: the east keeps Crownland and Southeast Fields",
            ProvinceControl.RulerOf(cycle, provinces[capital]) == East && ProvinceControl.RulerOf(cycle, provinces[south]) == East
            && ProvinceControl.KingdomFallen(cycle, West) && front.Resolution == "fell" && !cycle.Warfronts.Contains(front));
        ok &= Check(sb, "the guild rules what it held; Northeast Fields goes to no one; the third realm keeps South Fields",
            ProvinceControl.RulerOf(cycle, provinces[northFields]) == ProvinceControl.Guild
            && ProvinceControl.RulerOf(cycle, provinces[farSouth]) == ProvinceControl.Guild
            && ProvinceControl.RulerOf(cycle, provinces[north]) == ProvinceControl.Neutral
            && ProvinceControl.RulerOf(cycle, provinces[southFields]) == Lost);
        ok &= Check(sb, "the west's faction is the east's now, so older readers see the victor",
            cycle.Kingdoms[West].ControllingFactionId == "f_east");
        ok &= Check(sb, $"one standing step at the east's court and the third realm's, none at the fallen court (PR55; {courts[East].StandingPenalty - eastPenalty}, {courts[Lost].StandingPenalty - lostPenalty}, {courts[West].StandingPenalty - westPenalty})",
            courts[East].StandingPenalty == eastPenalty + ProvinceControl.CollapseStandingStep
            && courts[Lost].StandingPenalty == lostPenalty + ProvinceControl.CollapseStandingStep
            && courts[West].StandingPenalty == westPenalty);
        ok &= Check(sb, "the west's own war ends with it",
            westWar.Closed && westWar.Resolution == "collapsed" && !cycle.Warfronts.Contains(westWar));
        ok &= Check(sb, "the news names the fall, what the east keeps, what the guild rules and what goes to no one",
            news.Count > 0 && news[0].Contains("falls with Crownland") && news[0].Contains("keeps 2 provinces")
            && news[0].Contains("North Fields and Far South Fields") && news[0].Contains("1 goes to no one"));
        ok &= Check(sb, "the caches follow: Northeast Fields' to no one, North Fields' to the guild",
            world.Pois[noOnesCache].SupplyControllerId == ProvinceControl.Neutral
            && world.Pois[heldCache].SupplyControllerId == ProvinceControl.Guild);
        ok &= Check(sb, "the fallen west borders no one, and no one borders it",
            !KingdomTickSimulation.NeighborsOf(cycle, West).Any() && !KingdomTickSimulation.NeighborsOf(cycle, East).Contains(West));
        int westStability = cycle.Kingdoms[West].Stability;
        int eastStability = cycle.Kingdoms[East].Stability;
        KingdomTickSimulation.Tick(cycle);
        ok &= Check(sb, $"a lunation passes it by (stability {westStability} to {cycle.Kingdoms[West].Stability}) while the east ticks ({eastStability} to {cycle.Kingdoms[East].Stability})",
            cycle.Kingdoms[West].Stability == westStability && cycle.Kingdoms[East].Stability != eastStability
            && !cycle.Warfronts.Any(w => !w.Closed && (w.DefenderKingdomId == West || w.AggressorKingdomId == West)));

        // The guild's claim (PR10).
        cycle.CastleX = -1;
        cycle.CastleY = -1;
        provinces[north].Influence = 30;
        provinces[north].Held = false;
        ProvinceInfluence.NextLunation(cycle, north, out var fading);
        ok &= Check(sb, $"land no one rules fades away from the castle ({fading})", fading == ProvinceInfluence.Drift.Fading);
        string claim = ProvinceInfluence.Add(cycle, 18, 5, 30);
        ok &= Check(sb, $"a deed that holds it makes it the guild's, with the news (PR10; {provinces[north].Influence})",
            provinces[north].Held && ProvinceControl.RulerOf(cycle, provinces[north]) == ProvinceControl.Guild
            && claim.Contains("the guild's hold makes it the guild's"));
        ProvinceInfluence.NextLunation(cycle, north, out var sovereign);
        ok &= Check(sb, $"and the guild's own land does not fade ({sovereign})", sovereign == ProvinceInfluence.Drift.Sovereign);
        ok &= Check(sb, "its cache comes to the guild, and the claim turns no court",
            world.Pois[noOnesCache].SupplyControllerId == ProvinceControl.Guild
            && courts[East].StandingPenalty == eastPenalty + ProvinceControl.CollapseStandingStep
            && courts[West].StandingPenalty == westPenalty);

        sb.AppendLine(ok ? "  Collapse and claim: PASS" : "  Collapse and claim: FAIL");
        return ok;
    }

    /// <summary>A Seize per province (PR56): a won bid on Southeast Fields driven to 0 makes it
    /// the guild's (influence 100, held), ends the war, and turns the west's court from Favored
    /// to Hostile at -6, with a line in the Herald's Report. The rest of the west stands.</summary>
    private static bool AssertSeize(StringBuilder sb)
    {
        var cycle = FrontCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        int capital = world.ProvinceIndexAt(6, 15);
        int south = world.ProvinceIndexAt(18, 25);
        var west = cycle.Council.Courts[West];
        SetScore(west, 9);   // Favored
        var front = KingdomTickSimulation.OpenFront(cycle, West, East);
        if (capital < 0 || south < 0 || front == null || front.ProvinceId != provinces[south].Id)
        {
            sb.AppendLine("  Seize: FAIL (the front did not open on Southeast Fields)");
            return false;
        }
        int heralds = cycle.Council.Reports.Count;
        front.PlayerSeizing = true;
        front.Advance = 0;
        int reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.Settle(cycle, front);
        bool ok = Check(sb, "a won Seize driven to 0 makes Southeast Fields the guild's, held at 100, and ends the war",
            ProvinceControl.RulerOf(cycle, provinces[south]) == ProvinceControl.Guild
            && provinces[south].Influence == 100 && provinces[south].Held
            && front.Resolution == "seized" && !cycle.Warfronts.Contains(front));
        ok &= Check(sb, $"the west's court turns Hostile, at -6 ({west.Band()}, {west.StandingScore()})",
            west.Band() == CourtStandingBand.Hostile && west.StandingScore() == ProvinceControl.HostileScore);
        ok &= Check(sb, "the news and the Herald's Report say so",
            cycle.PendingSiegeReports.Skip(reports).Any(l => l.Contains("The guild takes Southeast Fields") && l.Contains("Its court turns Hostile"))
            && cycle.Council.Reports.Skip(heralds).Any(r => r.KingdomId == West && r.Text.Contains("Standing: Hostile")));
        ok &= Check(sb, "the rest of the west stands",
            !ProvinceControl.KingdomFallen(cycle, West) && ProvinceControl.RulerOf(cycle, provinces[capital]) == West);

        sb.AppendLine(ok ? "  Seize: PASS" : "  Seize: FAIL");
        return ok;
    }

    /// <summary>A seized capital (PR56 with PR6): the east's capital borders the west, so a
    /// war of the west contests it at once; a won Seize there makes it the guild's and fells
    /// the east with the guild as the taker: East Fields, which the guild held, is the guild's,
    /// the rest of the east goes to no one, the east's faction is the guild's, its court is
    /// Hostile, and the west's court, the aggressor's, loses one standing step.</summary>
    private static bool AssertSeizedCapital(StringBuilder sb)
    {
        var cycle = FrontCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        int eastCapital = world.ProvinceIndexAt(30, 15);
        int eastFields = world.ProvinceIndexAt(43, 18);
        var east = cycle.Council.Courts[East];
        var west = cycle.Council.Courts[West];
        SetScore(east, 3);   // Welcome
        int westPenalty = west.StandingPenalty;
        if (eastCapital < 0 || eastFields < 0)
        {
            sb.AppendLine("  Seized capital: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        provinces[eastFields].Influence = 70;
        provinces[eastFields].Held = true;
        var front = KingdomTickSimulation.OpenFront(cycle, East, West);
        bool ok = Check(sb, "a capital on the border is contested at once",
            front != null && front.ProvinceId == provinces[eastCapital].Id);
        if (front == null)
        {
            sb.AppendLine("  Seized capital: FAIL (no front opened)");
            return false;
        }
        front.PlayerSeizing = true;
        front.Advance = 0;
        int reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.Settle(cycle, front);
        var others = provinces.Where(p => p.KingdomId == East && p.Index != eastCapital && p.Index != eastFields).ToList();
        ok &= Check(sb, "a seized capital fells the east, with the guild as the taker",
            ProvinceControl.RulerOf(cycle, provinces[eastCapital]) == ProvinceControl.Guild
            && ProvinceControl.KingdomFallen(cycle, East)
            && cycle.Kingdoms[East].ControllingFactionId == KingdomTickSimulation.GuildFactionId);
        ok &= Check(sb, $"East Fields, which the guild held, is the guild's; the other {others.Count} go to no one",
            ProvinceControl.RulerOf(cycle, provinces[eastFields]) == ProvinceControl.Guild
            && others.Count > 0 && others.All(p => ProvinceControl.RulerOf(cycle, p) == ProvinceControl.Neutral));
        ok &= Check(sb, $"the east's court is Hostile, and the west's, the aggressor's, loses one step ({west.StandingPenalty - westPenalty})",
            east.Band() == CourtStandingBand.Hostile && west.StandingPenalty == westPenalty + ProvinceControl.CollapseStandingStep);
        ok &= Check(sb, "the news says the guild took the capital from the war",
            cycle.PendingSiegeReports.Skip(reports).Any(l => l.Contains("falls with Crownland, which the guild took from the war")));

        sb.AppendLine(ok ? "  Seized capital: PASS" : "  Seized capital: FAIL");
        return ok;
    }

    // ── Fronts helpers ────────────────────────────────────────────────────

    /// <summary>The court cycle at lunation 5, each realm's faction named (the west "f_west",
    /// the east "f_east"), the estates given, and every province at 0 and not held, with the
    /// kingdoms derived from them.</summary>
    private static CycleState FrontCycle()
    {
        var cycle = CourtCycle();
        cycle.Calendar.CurrentLunation = 5;
        cycle.Kingdoms[West].ControllingFactionId = "f_west";
        cycle.Kingdoms[East].ControllingFactionId = "f_east";
        ProvinceInfluence.Sync(cycle);
        foreach (var province in cycle.World.Provinces)
        {
            province.Influence = 0;
            province.Held = false;
        }
        ProvinceInfluence.Sync(cycle);
        return cycle;
    }

    /// <summary>A supply cache at (x,y) on the host realm's land, held by a controller ("" for
    /// its host). Returns its index.</summary>
    private static int AddCache(WorldData world, int x, int y, string host, string controller)
    {
        world.Pois.Add(new WorldPoi { X = x, Y = y, Kind = PoiKind.SupplyCache, KingdomId = host, SupplyControllerId = controller });
        int index = world.Pois.Count - 1;
        world.Tiles[y * world.Width + x].PoiIndex = index;
        return index;
    }
}
