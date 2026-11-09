using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

// ============================================================
// ProvinceSaveAssert.Bastion.cs  (partial of ProvinceSaveAssert)
//
// Purpose:        The asserts of slice PV4, the Bastion (docs/
//                 provinces_v1_9_bastion.md section 8), on the scratch
//                 world: the maneuver state round-trips and defaults on
//                 legacy JSON; hop costs, routes and the chassis (PR67 to
//                 PR69); a march and its Retrace; the maneuvers (PR70,
//                 PR71); and the war that comes to the castle (PR72).
//                 The expectations were fixed by a Python mirror of the
//                 hop rules on the same scratch world before the C# was
//                 built (Claude outputs/provinces_pv4/tools/hops_port.py).
// Layer:          Verification
// Collaborators:  ProvinceSaveAssert.cs (AssertAll calls AssertBastion;
//                 Check, RoundTrip, SetScore), ProvinceSaveAssert.Fronts.cs
//                 (FrontCycle), CastleHops.cs, CastleManeuvers.cs,
//                 CastleMarch.cs, CastleThreats.cs,
//                 KingdomTickSimulation.Castle.cs
// See:            docs/provinces_v1_9_bastion.md section 8
// ============================================================

public static partial class ProvinceSaveAssert
{
    /// <summary>Every Bastion assert (PV4).</summary>
    private static bool AssertBastion(StringBuilder sb)
    {
        bool ok = AssertBastionFields(sb);
        ok &= AssertHops(sb);
        ok &= AssertMarchAndRetrace(sb);
        ok &= AssertManeuvers(sb);
        ok &= AssertCastleInWar(sb);
        return ok;
    }

    /// <summary>CastleManeuverState survives the trip, and legacy JSON reads a full font with
    /// nothing armed.</summary>
    private static bool AssertBastionFields(StringBuilder sb)
    {
        var state = RoundTrip(new CastleManeuverState
        {
            FontEssence = 7, ForcePathArmed = true, VerdantLunation = 4, VeilArmed = true, LastMoveVeiled = true,
            CampwardArmed = true, WardedCampX = 3, WardedCampY = 4, BeaconArmed = true, RetraceLunation = 2,
            LastMarchValid = true, LastMarchFromX = 6, LastMarchFromY = 15, LastMarchToX = 30, LastMarchToY = 15,
            LastMarchFuel = 16, LastMarchLunation = 5, FrozenFronts = new Dictionary<string, int> { ["kingdom_1>kingdom_0"] = 1 },
            WaystationX = 20, WaystationY = 24, WaystationProvinceId = "province:kingdom_0:2",
        });
        bool ok = Check(sb, "CastleManeuverState survives the trip",
            state != null && state.FontEssence == 7 && state.ForcePathArmed && state.VerdantLunation == 4 && state.VeilArmed
            && state.LastMoveVeiled && state.CampwardArmed && state.WardedCampX == 3 && state.WardedCampY == 4
            && state.BeaconArmed && state.RetraceLunation == 2 && state.LastMarchValid && state.LastMarchFromX == 6
            && state.LastMarchToX == 30 && state.LastMarchFuel == 16 && state.LastMarchLunation == 5
            && state.FrozenFronts != null && state.FrozenFronts.TryGetValue("kingdom_1>kingdom_0", out int frozen) && frozen == 1
            && state.WaystationX == 20 && state.WaystationProvinceId == "province:kingdom_0:2");
        var legacy = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":2}", SaveManager.JsonOptions);
        ok &= Check(sb, "legacy JSON: a full font and nothing armed",
            legacy?.Maneuvers != null && legacy.Maneuvers.FontEssence == CastleManeuvers.FontMax
            && !legacy.Maneuvers.ForcePathArmed && !legacy.Maneuvers.LastMarchValid
            && legacy.Maneuvers.FrozenFronts != null && legacy.Maneuvers.FrozenFronts.Count == 0);

        sb.AppendLine(ok ? "  Bastion fields: PASS" : "  Bastion fields: FAIL");
        return ok;
    }

    /// <summary>Hop costs and routes (PR67 to PR69): the west capital to the east's in two hops
    /// of 8 by Northeast Fields; to Northeast Weald in 28, its last hop 12 (woodland +1, a ford
    /// +3); 26 for the Verdant Ark, 25 for the Lantern Keep, 25 with Force Path, 27 with Verdant
    /// Passage; the east's capital to East Fields in 11 (the ford), 8 for the Lantern Keep and
    /// for the Hourglass Redoubt's flat first hop; a bridge makes it 7, 6 for the Gearspire; the
    /// island beyond reach; 2 within the castle's province.</summary>
    private static bool AssertHops(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int northFields = world.ProvinceIndexAt(18, 5), eastCapital = world.ProvinceIndexAt(30, 15);
        int weald = world.ProvinceIndexAt(40, 8), eastFields = world.ProvinceIndexAt(43, 20);
        if (northFields < 0 || eastCapital < 0 || weald < 0 || eastFields < 0)
        {
            sb.AppendLine("  Hops: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        var adept = CastleTypes.For(CardSchool.Adept);
        var plan = CastleHops.Plan(cycle, 6, 15, 30, 15, adept);
        bool ok = Check(sb, $"the west capital to the east's: two hops of 8 by Northeast Fields ({PlanText(plan)})",
            plan != null && plan.Reachable && plan.Fuel == 16 && plan.Hops.Count == 2
            && plan.Hops[0].Province == northFields && plan.Hops[1].Province == eastCapital && plan.Waypoints.Count == 3);

        plan = CastleHops.Plan(cycle, 6, 15, 40, 8, adept);
        ok &= Check(sb, $"to Northeast Weald in 28, the last hop 12 for woodland and a ford ({PlanText(plan)})",
            plan != null && plan.Fuel == 28 && plan.Hops.Count == 3 && plan.Hops[2].Province == weald
            && plan.Hops[2].Cost == 12 && plan.Hops[2].Why.Contains("woodland +1") && plan.Hops[2].Why.Contains("a ford +3"));
        int druid = CastleHops.Plan(cycle, 6, 15, 40, 8, CastleTypes.For(CardSchool.Druid))?.Fuel ?? -1;
        int lantern = CastleHops.Plan(cycle, 6, 15, 40, 8, CastleTypes.For(CardSchool.Enchanter))?.Fuel ?? -1;
        ok &= Check(sb, $"the Verdant Ark 26 (cheap woodland), the Lantern Keep 25 (no ford) ({druid}, {lantern})",
            druid == 26 && lantern == 25);
        cycle.Maneuvers.ForcePathArmed = true;
        int forced = CastleHops.Plan(cycle, 6, 15, 40, 8, adept)?.Fuel ?? -1;
        cycle.Maneuvers.ForcePathArmed = false;
        cycle.Maneuvers.VerdantLunation = cycle.Calendar.CurrentLunation;
        int verdant = CastleHops.Plan(cycle, 6, 15, 40, 8, adept)?.Fuel ?? -1;
        cycle.Calendar.CurrentLunation += 1;
        int lapsed = CastleHops.Plan(cycle, 6, 15, 40, 8, adept)?.Fuel ?? -1;
        cycle.Calendar.CurrentLunation -= 1;
        cycle.Maneuvers.VerdantLunation = -1;
        ok &= Check(sb, $"Force Path 25; Verdant Passage 27 this lunation, 28 the next ({forced}, {verdant}, {lapsed})",
            forced == 25 && verdant == 27 && lapsed == 28);

        int ford = CastleHops.Plan(cycle, 30, 15, 43, 20, adept)?.Fuel ?? -1;
        int keep = CastleHops.Plan(cycle, 30, 15, 43, 20, CastleTypes.For(CardSchool.Enchanter))?.Fuel ?? -1;
        int hourglass = CastleHops.Plan(cycle, 30, 15, 43, 20, CastleTypes.For(CardSchool.Chronomancer))?.Fuel ?? -1;
        ok &= Check(sb, $"the east's capital to East Fields: 11 with the ford, 8 for the Lantern Keep and the Hourglass Redoubt ({ford}, {keep}, {hourglass})",
            ford == 11 && keep == 8 && hourglass == 8);

        // A bridge: a road over the river between (35,15) and (36,15).
        world.Tiles[15 * world.Width + 35].RoadEdges |= 1 << 1;
        world.Tiles[15 * world.Width + 36].RoadEdges |= 1 << 4;
        CastleHops.ResetBorders();
        int bridged = CastleHops.Plan(cycle, 30, 15, 43, 20, adept)?.Fuel ?? -1;
        int gearspire = CastleHops.Plan(cycle, 30, 15, 43, 20, CastleTypes.For(CardSchool.Tinker))?.Fuel ?? -1;
        ok &= Check(sb, $"a bridge makes it 7, and 6 for the Gearspire's doubled road ({bridged}, {gearspire})",
            bridged == 7 && gearspire == 6);
        CastleHops.ResetBorders();

        var island = CastleHops.Plan(cycle, 6, 15, 42, 27, adept);
        bool refused = !CastleMarch.CanMarchTo(cycle, 42, 27, out string why) && why.Contains("No land route");
        ok &= Check(sb, $"an island no land route reaches is refused ({why})",
            island != null && !island.Reachable && refused);
        var within = CastleHops.Plan(cycle, 6, 15, 4, 16, adept);
        ok &= Check(sb, "a march within the castle's province costs 2",
            within != null && within.Hops.Count == 0 && within.Fuel == CastleHops.InProvinceCost
            && CastleMarch.CostTo(cycle, 4, 16) == CastleHops.InProvinceCost);

        sb.AppendLine(ok ? "  Hops: PASS" : "  Hops: FAIL");
        return ok;
    }

    /// <summary>A march and its Retrace: the castle marches to the east's capital for 16, makes
    /// a camp of four days, and the march is recorded; Retrace brings it back with the fuel and
    /// no camp, costs 4 Essence, and cannot be cast twice in a lunation.</summary>
    private static bool AssertMarchAndRetrace(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        bool can = CastleMarch.CanMarchTo(cycle, 30, 15, out string why);
        string line = CastleMarch.Execute(cycle, 30, 15, "The Bastion Errant");
        var state = cycle.Maneuvers;
        bool ok = Check(sb, $"the march goes, 16 fuel, a camp of four days ({why}{line})",
            can && line.Contains("marches through 2 province(s)") && cycle.CastleX == 30 && cycle.CastleY == 15
            && cycle.CastleFuel == 29 && cycle.CastleRepairLunations == CastleMarch.ArrivalCampDays
            && CastleHops.LastPlan != null && CastleHops.LastPlan.Hops.Count == 2);
        ok &= Check(sb, "the march is recorded for Retrace",
            state.LastMarchValid && state.LastMarchFromX == 6 && state.LastMarchFromY == 15 && state.LastMarchFuel == 16
            && state.LastMarchLunation == cycle.Calendar.CurrentLunation);

        cycle.Grimoire.KnownSpellIds.Add("retrace");
        bool cast = CastleManeuvers.Cast(cycle, "retrace", "", out string retraced, out bool moved);
        ok &= Check(sb, $"Retrace: back where it began, the fuel returned, no camp, 4 Essence ({retraced})",
            cast && moved && cycle.CastleX == 6 && cycle.CastleY == 15 && cycle.CastleFuel == 45
            && cycle.CastleRepairLunations == 0 && state.FontEssence == CastleManeuvers.FontMax - 4
            && !state.LastMarchValid && state.LastMoveVeiled);
        bool again = CastleManeuvers.Cast(cycle, "retrace", "", out string twice, out _);
        ok &= Check(sb, $"and not twice in a lunation ({twice})", !again && twice.Contains("this lunation"));

        sb.AppendLine(ok ? "  March and Retrace: PASS" : "  March and Retrace: FAIL");
        return ok;
    }

    /// <summary>The maneuvers (PR70, PR71): a lit beacon spares the camp in a held province; a
    /// warded camp takes no threat; Force Path is used up by the march it eases; a Veil hides
    /// one move; a forward camp stands beside the castle, widens the font's reach and is struck
    /// when the castle moves; the Skimmer charts a province; Ley Leap reaches a held province
    /// three hops off with no fuel; a castle in a held province casts for 1 less; an unknown
    /// spell and an empty font refuse; the new moon fills the font.</summary>
    private static bool AssertManeuvers(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        var provinces = world.Provinces;
        int northFields = world.ProvinceIndexAt(18, 5), south = world.ProvinceIndexAt(18, 25);
        int farSouth = world.ProvinceIndexAt(9, 26), eastFields = world.ProvinceIndexAt(43, 20);
        int weald = world.ProvinceIndexAt(40, 8);
        if (northFields < 0 || south < 0 || farSouth < 0 || eastFields < 0 || weald < 0)
        {
            sb.AppendLine("  Maneuvers: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        foreach (var def in CastleManeuvers.All)
            cycle.Grimoire.KnownSpellIds.Add(def.SpellId);

        // Wayfarer's Beacon and Campward.
        provinces[northFields].Influence = 70;
        provinces[northFields].Held = true;
        bool lit = CastleManeuvers.Cast(cycle, "wayfarers_beacon", "", out _, out _);
        CastleMarch.Execute(cycle, 18, 8, "The Bastion Errant");
        bool ok = Check(sb, "a lit beacon spares the camp at the end of a march into a held province",
            lit && cycle.CastleX == 18 && cycle.CastleY == 8 && cycle.CastleRepairLunations == 0 && !cycle.Maneuvers.BeaconArmed);
        bool warded = CastleManeuvers.Cast(cycle, "campward", "", out _, out _);
        CastleMarch.Execute(cycle, 6, 16, "The Bastion Errant");
        var threat = CastleThreats.RollForDay(cycle);
        ok &= Check(sb, "Campward wards the next camp, and a warded camp takes no threat",
            warded && cycle.CastleExposed && CastleManeuvers.CampWarded(cycle) && !threat.Happened);

        // Force Path and Veil.
        cycle.CastleRepairLunations = 0;
        cycle.CastleFuel = 45;
        cycle.Maneuvers.FontEssence = CastleManeuvers.FontMax;
        bool laid = CastleManeuvers.Cast(cycle, "force_path", "", out _, out _);
        bool veiled = CastleManeuvers.Cast(cycle, "veil", "", out _, out _);
        int cost = CastleMarch.CostTo(cycle, 40, 8);
        CastleMarch.Execute(cycle, 40, 8, "The Bastion Errant");
        ok &= Check(sb, $"Force Path eases the march it is laid for and is used up; the Veil hides that move ({cost})",
            laid && veiled && cost == 25 && cycle.CastleFuel == 20 && !cycle.Maneuvers.ForcePathArmed
            && cycle.Maneuvers.LastMoveVeiled && !cycle.Maneuvers.VeilArmed);

        // A forward camp, the font's reach, and the Skimmer.
        cycle = BastionCycle("Adept");
        world = cycle.World;
        provinces = world.Provinces;
        foreach (var def in CastleManeuvers.All)
            cycle.Grimoire.KnownSpellIds.Add(def.SpellId);
        var reachBefore = CastleManeuvers.Reach(cycle);
        bool camped = CastleManeuvers.Cast(cycle, "deploy_waystation", south.ToString(), out _, out _);
        var reachAfter = CastleManeuvers.Reach(cycle);
        ok &= Check(sb, $"a forward camp stands in Southeast Fields as a staging point and widens the reach ({reachBefore.Count} to {reachAfter.Count})",
            camped && world.StagingPoints.Any(sp => sp.Source == CastleManeuvers.WaystationSource)
            && cycle.Maneuvers.WaystationProvinceId == provinces[south].Id
            && !reachBefore.Contains(world.ProvinceIndexAt(30, 27)) && reachAfter.Contains(world.ProvinceIndexAt(30, 27)));
        foreach (int i in Enumerable.Range(0, world.Tiles.Length))
        {
            if (world.ProvinceIndexAt(i % world.Width, i / world.Width) == farSouth)
                world.Tiles[i].Discovery = TileDiscovery.Unseen;
        }
        bool skimmed = CastleManeuvers.Cast(cycle, "clockwork_skimmer", farSouth.ToString(), out string skim, out _);
        ok &= Check(sb, $"the Skimmer charts Far South Fields ({skim})",
            skimmed && Enumerable.Range(0, world.Tiles.Length).All(i =>
                world.ProvinceIndexAt(i % world.Width, i / world.Width) != farSouth
                || world.Tiles[i].Discovery != TileDiscovery.Unseen));
        CastleMarch.Execute(cycle, 4, 16, "The Bastion Errant");
        ok &= Check(sb, "the forward camp is struck when the castle moves",
            !world.StagingPoints.Any(sp => sp.Source == CastleManeuvers.WaystationSource)
            && cycle.Maneuvers.WaystationProvinceId == "");

        // Ley Leap, the held discount, the refusals, the new moon.
        cycle = BastionCycle("Adept");
        world = cycle.World;
        provinces = world.Provinces;
        foreach (var def in CastleManeuvers.All)
            cycle.Grimoire.KnownSpellIds.Add(def.SpellId);
        provinces[eastFields].Influence = 70;
        provinces[eastFields].Held = true;
        var leaps = CastleManeuvers.Targets(cycle, CastleManeuvers.Get("ley_tap"));
        bool leapt = CastleManeuvers.Cast(cycle, "ley_tap", eastFields.ToString(), out string leap, out bool moved);
        ok &= Check(sb, $"Ley Leap reaches East Fields, held and three hops off, with no fuel and a camp ({leap})",
            leaps.Count == 1 && leapt && moved && world.ProvinceIndexAt(cycle.CastleX, cycle.CastleY) == eastFields
            && cycle.CastleFuel == 45 && cycle.CastleRepairLunations == CastleMarch.ArrivalCampDays
            && cycle.Maneuvers.FontEssence == CastleManeuvers.FontMax - 6);
        int discounted = CastleManeuvers.CostFor(cycle, CastleManeuvers.Get("force_path"));
        ok &= Check(sb, $"in a held province the font casts for 1 less ({discounted})", discounted == 3);
        cycle.Grimoire.KnownSpellIds.Remove("veil");
        bool unknown = CastleManeuvers.Cast(cycle, "veil", "", out string notKnown, out _);
        cycle.Maneuvers.FontEssence = 1;
        bool empty = CastleManeuvers.Cast(cycle, "force_path", "", out string tooLow, out _);
        ok &= Check(sb, $"an unknown spell and an empty font refuse ({notKnown} / {tooLow})",
            !unknown && notKnown.Contains("does not know") && !empty && tooLow.Contains("Essence"));
        CastleManeuvers.NewMoon(cycle);
        ok &= Check(sb, "the new moon fills the font", cycle.Maneuvers.FontEssence == CastleManeuvers.FontMax);

        sb.AppendLine(ok ? "  Maneuvers: PASS" : "  Maneuvers: FAIL");
        return ok;
    }

    /// <summary>The war that comes to the castle (PR72), with Thornwall and Stasis Snare: the
    /// east's front on Southeast Fields, the castle camped there; Thornwall holds the front at
    /// its Advance through the new moon while one not held moves; the east's soldiers fall on
    /// the camp and a defence is owed; a Friendly east passes it by; a warded camp holds; and
    /// Stasis Snare reaches the front from the west capital, one province off.</summary>
    private static bool AssertCastleInWar(StringBuilder sb)
    {
        // A front not held, for the contrast.
        var control = BastionCycle("Adept");
        var controlFront = KingdomTickSimulation.OpenFront(control, West, East);
        int controlStart = controlFront?.Advance ?? -1;
        KingdomTickSimulation.Tick(control);

        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int south = world.ProvinceIndexAt(18, 25);
        var front = KingdomTickSimulation.OpenFront(cycle, West, East);
        if (south < 0 || front == null || front.ProvinceId != world.Provinces[south].Id || controlFront == null)
        {
            sb.AppendLine("  The castle in the war: FAIL (the front did not open on Southeast Fields)");
            return false;
        }
        foreach (var def in CastleManeuvers.All)
            cycle.Grimoire.KnownSpellIds.Add(def.SpellId);
        var (campX, campY) = CastleHops.CampTile(world, world.Provinces[south]);
        cycle.CastleX = campX;
        cycle.CastleY = campY;
        int start = front.Advance;
        bool held = CastleManeuvers.Cast(cycle, "thornwall", front.Id, out string thorn, out _);
        int reports = cycle.PendingSiegeReports.Count;
        KingdomTickSimulation.Tick(cycle);
        var news = cycle.PendingSiegeReports.Skip(reports).ToList();
        bool ok = Check(sb, $"Thornwall holds the front at {start} through the new moon, while one not held moves ({front.Advance}, {controlFront.Advance}; {thorn})",
            held && front.Advance == start && controlFront.Advance != controlStart
            && news.Any(l => l.Contains("stands still this lunation")) && cycle.Maneuvers.FrozenFronts.Count == 0);
        ok &= Check(sb, $"the east's soldiers fall on the castle's camp, and a defence is owed ({cycle.PendingCastleAssaultKingdomId})",
            cycle.PendingCastleAssaultKingdomId == East && cycle.CastleRepairLunations > 0
            && news.Any(l => l.Contains("The war comes to the castle")));

        var friendly = BastionCycle("Adept");
        KingdomTickSimulation.OpenFront(friendly, West, East);
        var (fx, fy) = CastleHops.CampTile(friendly.World, friendly.World.Provinces[south]);
        friendly.CastleX = fx;
        friendly.CastleY = fy;
        SetScore(friendly.Council.Courts[East], 9);   // Favored: Friendly
        KingdomTickSimulation.Tick(friendly);
        ok &= Check(sb, "a Friendly east passes the camp by", string.IsNullOrEmpty(friendly.PendingCastleAssaultKingdomId));

        var guarded = BastionCycle("Adept");
        KingdomTickSimulation.OpenFront(guarded, West, East);
        var (gx, gy) = CastleHops.CampTile(guarded.World, guarded.World.Provinces[south]);
        guarded.CastleX = gx;
        guarded.CastleY = gy;
        guarded.Maneuvers.WardedCampX = gx;
        guarded.Maneuvers.WardedCampY = gy;
        reports = guarded.PendingSiegeReports.Count;
        KingdomTickSimulation.Tick(guarded);
        ok &= Check(sb, "a warded camp holds",
            string.IsNullOrEmpty(guarded.PendingCastleAssaultKingdomId)
            && guarded.PendingSiegeReports.Skip(reports).Any(l => l.Contains("the wards hold")));

        var snare = BastionCycle("Adept");
        var snared = KingdomTickSimulation.OpenFront(snare, West, East);
        foreach (var def in CastleManeuvers.All)
            snare.Grimoire.KnownSpellIds.Add(def.SpellId);
        bool reached = snared != null && CastleManeuvers.Cast(snare, "stasis_snare", snared.Id, out _, out _);
        ok &= Check(sb, "Stasis Snare reaches the front from the west capital, one province off",
            reached && snare.Maneuvers.FrozenFronts.ContainsKey(snared.Id)
            && CastleManeuvers.Targets(snare, CastleManeuvers.Get("thornwall")).Count == 0);

        sb.AppendLine(ok ? "  The castle in the war: PASS" : "  The castle in the war: FAIL");
        return ok;
    }

    // ── Bastion helpers ───────────────────────────────────────────────────

    /// <summary>The front cycle with the castle parked at the west capital (6,15), its camp
    /// done, a full tank of 45, a school's chassis, and the whole map charted.</summary>
    private static CycleState BastionCycle(string school)
    {
        var cycle = FrontCycle();
        cycle.SelectedSchool = school;
        cycle.CastleX = 6;
        cycle.CastleY = 15;
        cycle.CastleParked = true;
        cycle.CastleRepairLunations = 0;
        cycle.CastleMaxFuel = 45;
        cycle.CastleFuel = 45;
        cycle.PendingCastleAssaultKingdomId = "";
        cycle.Grimoire.KnownSpellIds.Clear();
        for (int i = 0; i < cycle.World.Tiles.Length; i++)
            cycle.World.Tiles[i].Discovery = TileDiscovery.Charted;
        CastleHops.ResetBorders();
        CastleHops.LastPlan = null;
        return cycle;
    }

    /// <summary>A plan for a message: "Northeast Fields 8, Crownland 8 = 16", or "none".</summary>
    private static string PlanText(MarchPlan plan)
    {
        if (plan == null)
            return "none";
        if (!plan.Reachable)
            return "unreachable";
        return string.Join(", ", plan.Hops.Select(h => $"{h.Province}:{h.Cost}")) + $" = {plan.Fuel}";
    }
}
