using System.Linq;
using System.Text;
using System.Text.Json;

// ============================================================
// ProvinceSaveAssert.Keeper.cs  (partial of ProvinceSaveAssert)
//
// Purpose:        The asserts of slice PV6, the Keeper (docs/provinces_
//                 v1_10_keeper.md section 6), on the scratch world with
//                 the castle at the west capital: the state round-trips
//                 and sleeps on legacy JSON; the Keeper wakes in the
//                 province farthest from the castle (Northeast Weald),
//                 hunts by East Crownland and Northeast Fields, fouls the
//                 roads (a hop into Southeast Fields 8 becomes 11), falls
//                 on the castle in the west capital, draws back to
//                 Northeast Fields and loses the trail; a veiled march
//                 leaves no trail, an unveiled one and a loud cast do;
//                 Rouse a Front pushes the war 20; Lurk sees two hops;
//                 a warded camp turns it aside.
// Layer:          Verification
// Collaborators:  ProvinceSaveAssert.cs (AssertAll calls AssertKeeper;
//                 Check, RoundTrip), ProvinceSaveAssert.Bastion.cs
//                 (BastionCycle), KeeperHunt.cs, CastleManeuvers.cs,
//                 CastleMarch.cs, CastleHops.cs
// See:            docs/provinces_v1_10_keeper.md section 6
// ============================================================

public static partial class ProvinceSaveAssert
{
    /// <summary>Every Keeper assert (PV6).</summary>
    private static bool AssertKeeper(StringBuilder sb)
    {
        bool ok = AssertKeeperFields(sb);
        ok &= AssertKeeperHunt(sb);
        ok &= AssertKeeperTrail(sb);
        ok &= AssertKeeperCards(sb);
        return ok;
    }

    /// <summary>KeeperState survives the trip, and legacy JSON reads a Keeper asleep.</summary>
    private static bool AssertKeeperFields(StringBuilder sb)
    {
        var keeper = RoundTrip(new KeeperState
        {
            Awake = true, ProvinceId = "province:kingdom_1:1", HomeProvinceId = "province:kingdom_1:1",
            KnownCastleProvinceId = "province:kingdom_0:0", DeckIndex = 4, SupplyCutProvinceId = "province:kingdom_0:1",
            StruckLunation = 6,
        });
        bool ok = Check(sb, "KeeperState survives the trip",
            keeper != null && keeper.Awake && keeper.ProvinceId == "province:kingdom_1:1"
            && keeper.HomeProvinceId == "province:kingdom_1:1" && keeper.KnownCastleProvinceId == "province:kingdom_0:0"
            && keeper.DeckIndex == 4 && keeper.SupplyCutProvinceId == "province:kingdom_0:1" && keeper.StruckLunation == 6);
        var legacy = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":2}", SaveManager.JsonOptions);
        ok &= Check(sb, "legacy JSON: the Keeper sleeps",
            legacy?.Keeper != null && !legacy.Keeper.Awake && legacy.Keeper.ProvinceId == "" && legacy.Keeper.DeckIndex == 0);

        sb.AppendLine(ok ? "  Keeper fields: PASS" : "  Keeper fields: FAIL");
        return ok;
    }

    /// <summary>The hunt: the Keeper wakes in Northeast Weald knowing the castle is in the west
    /// capital; Hunt takes it to East Crownland and Northeast Fields; Cut Supply fouls the roads
    /// around Northeast Fields, so a hop into Southeast Fields costs 11; Hunt brings it into the
    /// west capital, where it falls on the castle for the east's crown (its home), draws back to
    /// Northeast Fields and loses the trail.</summary>
    private static bool AssertKeeperHunt(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int capital = world.ProvinceIndexAt(6, 15), northFields = world.ProvinceIndexAt(18, 5);
        int eastCapital = world.ProvinceIndexAt(30, 15), weald = world.ProvinceIndexAt(40, 8);
        int south = world.ProvinceIndexAt(18, 25);
        if (capital < 0 || northFields < 0 || eastCapital < 0 || weald < 0 || south < 0)
        {
            sb.AppendLine("  Keeper hunt: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        var provinces = world.Provinces;
        var news = KeeperHunt.NewMoon(cycle);
        bool ok = Check(sb, $"the Keeper wakes in Northeast Weald, the farthest province, knowing where the castle is ({string.Join(" ", news)})",
            cycle.Keeper.Awake && cycle.Keeper.ProvinceId == provinces[weald].Id
            && cycle.Keeper.KnownCastleProvinceId == provinces[capital].Id
            && news.Any(l => l.Contains("The Keeper wakes in Northeast Weald")));

        KeeperHunt.NewMoon(cycle);
        string first = cycle.Keeper.ProvinceId;
        KeeperHunt.NewMoon(cycle);
        string second = cycle.Keeper.ProvinceId;
        ok &= Check(sb, $"Hunt, Hunt: to East Crownland, then Northeast Fields ({first}, {second})",
            first == provinces[eastCapital].Id && second == provinces[northFields].Id);

        int before = CastleHops.Plan(cycle, 6, 15, 20, 24, CastleTypes.For(CardSchool.Adept))?.Fuel ?? -1;
        news = KeeperHunt.NewMoon(cycle);
        int fouled = CastleHops.Plan(cycle, 6, 15, 20, 24, CastleTypes.For(CardSchool.Adept))?.Fuel ?? -1;
        ok &= Check(sb, $"Cut Supply fouls the roads around Northeast Fields: a hop into Southeast Fields costs {fouled}, not {before}",
            before == 8 && fouled == 11 && cycle.Keeper.SupplyCutProvinceId == provinces[northFields].Id
            && news.Any(l => l.Contains("fouls the roads")));

        news = KeeperHunt.NewMoon(cycle);
        ok &= Check(sb, $"Hunt brings it into the west capital: it falls on the castle, a defence is owed, it draws back and loses the trail ({cycle.PendingCastleAssaultKingdomId})",
            cycle.PendingCastleAssaultKingdomId == East && cycle.CastleRepairLunations > 0
            && cycle.Keeper.ProvinceId == provinces[northFields].Id && cycle.Keeper.KnownCastleProvinceId == ""
            && cycle.Keeper.SupplyCutProvinceId == "" && news.Any(l => l.Contains("The Keeper falls on the castle")));

        sb.AppendLine(ok ? "  Keeper hunt: PASS" : "  Keeper hunt: FAIL");
        return ok;
    }

    /// <summary>The trail (PR77): a veiled march leaves none, a cast is heard, an unveiled march
    /// is read.</summary>
    private static bool AssertKeeperTrail(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int capital = world.ProvinceIndexAt(6, 15), eastCapital = world.ProvinceIndexAt(30, 15);
        KeeperHunt.NewMoon(cycle);   // wakes
        cycle.Keeper.KnownCastleProvinceId = "";
        cycle.Grimoire.KnownSpellIds.Add("veil");
        cycle.Grimoire.KnownSpellIds.Add("force_path");
        bool veiled = CastleManeuvers.Cast(cycle, "veil", "", out _, out _);
        CastleMarch.Execute(cycle, 30, 15, "The Bastion Errant");
        bool ok = Check(sb, "a veiled march leaves no trail, and casting the Veil is quiet",
            veiled && cycle.CastleX == 30 && cycle.Keeper.KnownCastleProvinceId == "");
        bool cast = CastleManeuvers.Cast(cycle, "force_path", "", out _, out _);
        ok &= Check(sb, "a cast is loud: the Keeper hears where the castle stands",
            cast && cycle.Keeper.KnownCastleProvinceId == world.Provinces[eastCapital].Id);
        cycle.CastleRepairLunations = 0;
        cycle.CastleFuel = 45;
        CastleMarch.Execute(cycle, 6, 15, "The Bastion Errant");
        ok &= Check(sb, "an unveiled march is read",
            cycle.CastleX == 6 && cycle.Keeper.KnownCastleProvinceId == world.Provinces[capital].Id);

        sb.AppendLine(ok ? "  Keeper trail: PASS" : "  Keeper trail: FAIL");
        return ok;
    }

    /// <summary>The other cards: Rouse a Front pushes the nearest war 20; Lurk sees two hops and
    /// no farther; a warded camp turns the Keeper aside.</summary>
    private static bool AssertKeeperCards(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int northFields = world.ProvinceIndexAt(18, 5), weald = world.ProvinceIndexAt(40, 8);
        int capital = world.ProvinceIndexAt(6, 15);
        KeeperHunt.NewMoon(cycle);   // wakes in Northeast Weald
        var front = KingdomTickSimulation.OpenFront(cycle, West, East);
        int start = front?.Advance ?? -1;
        cycle.Keeper.DeckIndex = 4;   // Rouse a Front
        var news = KeeperHunt.NewMoon(cycle);
        bool ok = Check(sb, $"Rouse a Front pushes the nearest war {KeeperHunt.RouseAdvance} ({start} to {front?.Advance})",
            front != null && front.Advance == start + KeeperHunt.RouseAdvance && news.Any(l => l.Contains("stirs the war")));

        cycle.Keeper.KnownCastleProvinceId = "";
        cycle.Keeper.DeckIndex = 5;   // Lurk, two hops of sight: the castle three hops off is not seen
        KeeperHunt.NewMoon(cycle);
        bool unseen = cycle.Keeper.KnownCastleProvinceId == "";
        cycle.CastleX = 18;
        cycle.CastleY = 8;            // Northeast Fields, two hops from Northeast Weald
        cycle.Keeper.DeckIndex = 5;
        KeeperHunt.NewMoon(cycle);
        ok &= Check(sb, "Lurk sees the castle two hops off, and not three",
            cycle.Keeper.ProvinceId == world.Provinces[weald].Id && unseen
            && cycle.Keeper.KnownCastleProvinceId == world.Provinces[northFields].Id);

        var warded = BastionCycle("Adept");
        KeeperHunt.NewMoon(warded);
        warded.Keeper.ProvinceId = warded.World.Provinces[capital].Id;
        warded.Keeper.DeckIndex = 5;   // Lurk in the castle's own province
        warded.Maneuvers.WardedCampX = warded.CastleX;
        warded.Maneuvers.WardedCampY = warded.CastleY;
        news = KeeperHunt.NewMoon(warded);
        ok &= Check(sb, "a warded camp turns the Keeper aside",
            string.IsNullOrEmpty(warded.PendingCastleAssaultKingdomId) && news.Any(l => l.Contains("the wards turn it aside")));

        sb.AppendLine(ok ? "  Keeper cards: PASS" : "  Keeper cards: FAIL");
        return ok;
    }
}
