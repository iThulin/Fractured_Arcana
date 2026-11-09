using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

// ============================================================
// ProvinceSaveAssert.cs
//
// Purpose:        Save round-trip and build assertions for provinces
//                 (docs/provinces_v1.md, slices PV1 and PV2a): a
//                 Province with every field set; a built world through
//                 the save serializer (WorldData.Provinces and
//                 ProvinceMap); the additive defaults on legacy JSON;
//                 the partition of a scratch world (two kingdoms, a
//                 ridge, a river, a lake, a small island and a large
//                 one); a big range becoming a province of its own;
//                 determinism; the legacy build; land no kingdom owns
//                 (water opened into marsh in play); the guild's
//                 influence on provinces (the bridge to the kingdom
//                 value, the lunation, deeds, the hold lines, falls
//                 and seizures, a world without provinces); the
//                 province readout's forecast against the lunation it
//                 forecasts; the courts on provinces (estates, floors,
//                 the court's weight on deeds, the deeds of PV2b-1 and
//                 betrayal, docs/provinces_v1_7g_deeds_and_courts.md);
//                 what holding a province gives (PV2c: held, a
//                 Patron's estate, the new moon's yield and sighting,
//                 land opened in play joining a province); fronts
//                 per province (PV3a, ProvinceSaveAssert.Fronts.cs:
//                 rulers, a front that walks, a capital's fall, a
//                 Seize, the guild's claim);
//                 the Reach lens's bands, where a province's
//                 name goes on the map, the names' fade and the inks'
//                 least width (docs/provinces_v1_7d_lenses.md); and,
//                 read-only, the checks on the live world when it has
//                 provinces. Same serializer and contract as
//                 LeadsSaveAssert.
// Layer:          Verification
// Collaborators:  Province.cs, ProvinceBuilder.cs, ProvinceInfluence.cs,
//                 ProvinceBenefits.cs,
//                 ProvinceCourts.cs, LeadService.cs, CityContractService.cs,
//                 KingdomTickSimulation.cs (ApplyIntervention),
//                 KingdomState.cs, WorldData.cs, SaveManager.cs
//                 (JsonOptions), WorldAtlas3D.Provinces.cs (the label
//                 tile, the name fade, the ink width), CampusGuildPanel.cs
//                 (the "Assert Round-Trips" debug button)
// ============================================================

public static partial class ProvinceSaveAssert
{
    private const string West = "kingdom_0";
    private const string East = "kingdom_1";

    /// <summary>Run every province assertion. Prints a PASS/FAIL report and PushErrors
    /// on any failure. Returns true only if all passed.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PROVINCES SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = AssertProvince(sb);
        ok &= AssertWorldRoundTrip(sb);
        ok &= AssertLegacyDefaults(sb);
        ok &= AssertPartition(sb);
        ok &= AssertRange(sb);
        ok &= AssertDeterminism(sb);
        ok &= AssertLegacyBuild(sb);
        ok &= AssertOpenedLand(sb);
        ok &= AssertInfluenceFields(sb);
        ok &= AssertInfluenceRules(sb);
        ok &= AssertInfluenceWithoutProvinces(sb);
        ok &= AssertForecast(sb);
        ok &= AssertEstates(sb);
        ok &= AssertCourtRules(sb);
        ok &= AssertDeeds(sb);
        ok &= AssertBetrayal(sb);
        ok &= AssertFavorDeeds(sb);
        ok &= AssertBenefits(sb);
        ok &= AssertFronts(sb);
        ok &= AssertAnnexation(sb);
        ok &= AssertBastion(sb);
        ok &= AssertKeeper(sb);
        ok &= AssertReach(sb);
        ok &= AssertArmy(sb);
        ok &= AssertProvinceLeads(sb);
        ok &= AssertBands(sb);
        ok &= AssertLabelTiles(sb);
        ok &= AssertLensScales(sb);
        ok &= AssertLiveWorld(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Provinces and the province map round-trip clean, legacy worlds load empty and build once, the partition keeps its rules, the guild's influence keeps its rules on provinces, the courts keep theirs, holding gives what it should, and the map's bands, names and inks keep theirs."
            : "RESULT: FAILURES ABOVE. A province field is being dropped or renamed, a partition rule broke, an influence rule broke, a court rule broke, a held benefit broke, or a lens rule broke.");
        GD.Print(sb.ToString());

        if (!ok)
        {
            GD.PushError("[ProvinceSaveAssert] Round-trip assertion FAILED. See Output panel.");
        }
        return ok;
    }

    /// <summary>A Province with every field set to a distinctive value survives the trip.</summary>
    private static bool AssertProvince(StringBuilder sb)
    {
        var src = new Province
        {
            Index = 7,
            Id = "province:kingdom_3:2",
            Name = "Far North Weald",
            KingdomId = "kingdom_3",
            SeatX = 41,
            SeatY = 17,
            SeatSettlementIndex = 12,
            IsCapital = true,
            TileCount = 133,
            Character = ProvinceCharacter.Wetland,
            Settlements = new List<int> { 12, 15 },
            Neighbors = new List<int> { 2, 6, 9 },
            Tone = 3,
            Influence = 73,
            Held = true,
            YieldDoubledLunation = 9,
        };
        var rt = RoundTrip(src);
        bool ok = Check(sb, "Province survives the trip", rt != null);
        if (rt != null)
        {
            ok &= Check(sb, "Province.Index", rt.Index == 7);
            ok &= Check(sb, "Province.Id", rt.Id == src.Id);
            ok &= Check(sb, "Province.Name", rt.Name == src.Name);
            ok &= Check(sb, "Province.KingdomId", rt.KingdomId == src.KingdomId);
            ok &= Check(sb, "Province.SeatX/SeatY", rt.SeatX == 41 && rt.SeatY == 17);
            ok &= Check(sb, "Province.SeatSettlementIndex", rt.SeatSettlementIndex == 12);
            ok &= Check(sb, "Province.IsCapital", rt.IsCapital);
            ok &= Check(sb, "Province.TileCount", rt.TileCount == 133);
            ok &= Check(sb, "Province.Character", rt.Character == ProvinceCharacter.Wetland);
            ok &= Check(sb, "Province.Settlements", rt.Settlements != null && rt.Settlements.SequenceEqual(src.Settlements));
            ok &= Check(sb, "Province.Neighbors", rt.Neighbors != null && rt.Neighbors.SequenceEqual(src.Neighbors));
            ok &= Check(sb, "Province.Tone", rt.Tone == 3);
            ok &= Check(sb, "Province.Influence", rt.Influence == 73);
            ok &= Check(sb, "Province.Held", rt.Held);
            ok &= Check(sb, "Province.YieldDoubledLunation", rt.YieldDoubledLunation == 9);
        }

        sb.AppendLine(ok ? "  Province: PASS" : "  Province: FAIL");
        return ok;
    }

    /// <summary>A built world through the save serializer: the map comes back byte for
    /// byte and every province field for field.</summary>
    private static bool AssertWorldRoundTrip(StringBuilder sb)
    {
        var world = ScratchWorld();
        ProvinceBuilder.Build(world, log: false);
        bool ok = Check(sb, "scratch world built", world.HasProvinces());
        var rt = RoundTrip(world);
        ok &= Check(sb, "WorldData survives the trip", rt != null);
        if (rt != null && world.HasProvinces())
        {
            ok &= Check(sb, "WorldData.ProvinceMap byte for byte",
                rt.ProvinceMap != null && rt.ProvinceMap.SequenceEqual(world.ProvinceMap));
            ok &= Check(sb, "WorldData.Provinces count",
                rt.Provinces != null && rt.Provinces.Count == world.Provinces.Count);
            bool same = rt.Provinces != null && rt.Provinces.Count == world.Provinces.Count;
            for (int i = 0; same && i < world.Provinces.Count; i++)
                same = SameProvince(world.Provinces[i], rt.Provinces[i]);
            ok &= Check(sb, "every province field after load", same);
            ok &= Check(sb, "HasProvinces after load", rt.HasProvinces());
            ok &= Check(sb, "ProvinceAt after load",
                rt.ProvinceAt(6, 15) != null && rt.ProvinceAt(6, 15).Id == world.ProvinceAt(6, 15)?.Id);
        }

        sb.AppendLine(ok ? "  World round-trip: PASS" : "  World round-trip: FAIL");
        return ok;
    }

    /// <summary>The additive defaults: a world saved before provinces has no map (null)
    /// and no provinces (an empty list, not null), and every province lookup says none.
    /// A sparse Province keeps its -1 and empty defaults.</summary>
    private static bool AssertLegacyDefaults(StringBuilder sb)
    {
        var legacy = JsonSerializer.Deserialize<WorldData>("{\"width\":4,\"height\":3}", SaveManager.JsonOptions);
        bool ok = Check(sb, "WorldData.ProvinceMap null on legacy JSON", legacy != null && legacy.ProvinceMap == null);
        ok &= Check(sb, "WorldData.Provinces empty (not null) on legacy JSON",
            legacy != null && legacy.Provinces != null && legacy.Provinces.Count == 0);
        ok &= Check(sb, "HasProvinces false on legacy JSON", legacy != null && !legacy.HasProvinces());
        ok &= Check(sb, "ProvinceAt and ProvinceIndexAt say none on legacy JSON",
            legacy != null && legacy.ProvinceAt(0, 0) == null && legacy.ProvinceIndexAt(0, 0) == -1);

        var sparse = JsonSerializer.Deserialize<Province>("{\"name\":\"North Weald\"}", SaveManager.JsonOptions);
        ok &= Check(sb, "Province defaults on sparse JSON",
            sparse != null && sparse.Name == "North Weald" && sparse.Index == -1 && sparse.KingdomId == ""
            && sparse.SeatSettlementIndex == -1 && sparse.Settlements != null && sparse.Neighbors != null
            && sparse.Character == ProvinceCharacter.Lowland && !sparse.IsCapital
            && sparse.Influence == 0 && !sparse.Held && sparse.YieldDoubledLunation == -1);

        sb.AppendLine(ok ? "  Legacy defaults: PASS" : "  Legacy defaults: FAIL");
        return ok;
    }

    /// <summary>The scratch world's partition: every rule holds; each kingdom holds three
    /// to ten provinces; each seat city heads its kingdom's capital, Crownland; the small
    /// island joins a province of its kingdom that also holds mainland; the large island
    /// is a province of its own; water is in none; the river holds as a border on at
    /// least three quarters of its edges (without the river weight it holds on about a
    /// fifth); and the town in the woods heads a Woodland province.</summary>
    private static bool AssertPartition(StringBuilder sb)
    {
        var world = ScratchWorld();
        ProvinceBuilder.Build(world, log: false);
        var fails = ProvinceBuilder.Validate(world);
        foreach (string fail in fails)
            sb.AppendLine("    FAIL: scratch partition: " + fail);
        bool ok = fails.Count == 0;
        if (!world.HasProvinces())
        {
            sb.AppendLine("  Partition: FAIL (no provinces built)");
            return false;
        }

        int west = 0, east = 0;
        foreach (var province in world.Provinces)
        {
            if (province.KingdomId == West)
                west++;
            else if (province.KingdomId == East)
                east++;
        }
        ok &= Check(sb, $"each kingdom holds 3 to 10 provinces (west {west}, east {east})",
            west >= 3 && west <= 10 && east >= 3 && east <= 10);

        var westSeat = world.ProvinceAt(6, 15);
        ok &= Check(sb, "the west seat city heads the west capital, Crownland",
            westSeat != null && westSeat.IsCapital && westSeat.Name == "Crownland" && westSeat.KingdomId == West
            && westSeat.SeatSettlementIndex == 0 && westSeat.SeatX == 6 && westSeat.SeatY == 15);
        var eastSeat = world.ProvinceAt(30, 15);
        ok &= Check(sb, "the east seat city heads the east capital, Crownland",
            eastSeat != null && eastSeat.IsCapital && eastSeat.Name == "Crownland" && eastSeat.KingdomId == East
            && eastSeat.SeatSettlementIndex == 1 && eastSeat.SeatX == 30 && eastSeat.SeatY == 15);

        int small = world.ProvinceIndexAt(3, 28);
        bool smallWhole = small >= 0;
        for (int y = 27; y <= 29; y++)
        {
            for (int x = 2; x <= 4; x++)
                smallWhole &= world.ProvinceIndexAt(x, y) == small;
        }
        ok &= Check(sb, "the small island joins one west province that also holds mainland",
            smallWhole && world.Provinces[small].KingdomId == West && world.Provinces[small].TileCount > 9);

        int large = world.ProvinceIndexAt(42, 27);
        bool largeWhole = large >= 0;
        for (int y = 24; y <= 30; y++)
        {
            for (int x = 39; x <= 46; x++)
                largeWhole &= world.ProvinceIndexAt(x, y) == large;
        }
        ok &= Check(sb, "the large island is an east province of its own",
            largeWhole && world.Provinces[large].TileCount == 56 && world.Provinces[large].KingdomId == East);

        ok &= Check(sb, "the lake and the sea are in no province",
            world.ProvinceIndexAt(31, 5) == -1 && world.ProvinceIndexAt(1, 26) == -1 && world.ProvinceIndexAt(38, 23) == -1);

        int riverEdges = 0, riverBorders = 0;
        for (int y = 0; y < world.Height; y++)
        {
            var (q, r) = HexCoord.OffsetToAxial(35, y);
            for (int d = 0; d < 6; d++)
            {
                var (dq, dr) = HexCoord.AxialDirections[d];
                var (nx, ny) = HexCoord.AxialToOffset(q + dq, r + dr);
                if (nx != 36 || !world.InBounds(nx, ny))
                    continue;
                int here = world.ProvinceIndexAt(35, y), there = world.ProvinceIndexAt(nx, ny);
                if (here < 0 || there < 0)
                    continue;
                riverEdges++;
                if (here != there)
                    riverBorders++;
            }
        }
        ok &= Check(sb, $"the river is a province border on at least three quarters of its edges ({riverBorders} of {riverEdges})",
            riverEdges > 0 && riverBorders * 4 >= riverEdges * 3);

        var woods = world.ProvinceAt(42, 6);
        ok &= Check(sb, "the town in the woods heads a Woodland province",
            woods != null && woods.Character == ProvinceCharacter.Woodland && woods.SeatSettlementIndex == 5);

        sb.AppendLine(ok
            ? $"  Partition ({world.Provinces.Count} provinces, river border {riverBorders} of {riverEdges}): PASS"
            : "  Partition: FAIL");
        return ok;
    }

    /// <summary>A big range is a province of its own (ruling 2026-11-03). On the range
    /// world, the nine-by-nine block of mountain and its ring of hills are one province:
    /// Mountain, named "... Peaks", with no settlement and not the capital, and it takes
    /// no land below the hills (121 tiles exactly).</summary>
    private static bool AssertRange(StringBuilder sb)
    {
        var world = RangeWorld();
        ProvinceBuilder.Build(world, log: false);
        var fails = ProvinceBuilder.Validate(world);
        foreach (string fail in fails)
            sb.AppendLine("    FAIL: range world: " + fail);
        bool ok = fails.Count == 0;

        int range = world.ProvinceIndexAt(20, 14);
        bool whole = range >= 0;
        for (int y = 9; y <= 19; y++)
        {
            for (int x = 15; x <= 25; x++)
                whole &= world.ProvinceIndexAt(x, y) == range;
        }
        ok &= Check(sb, "the mountains and their hills are one province", whole);
        if (range >= 0)
        {
            var province = world.Provinces[range];
            ok &= Check(sb, "the range's province is Mountain, named Peaks, with no settlement, and not the capital",
                province.Character == ProvinceCharacter.Mountain
                && province.Name.EndsWith("Peaks", System.StringComparison.Ordinal)
                && province.SeatSettlementIndex == -1 && !province.IsCapital);
            ok &= Check(sb, $"the range takes no land below its hills ({province.TileCount} tiles, 121 expected)",
                province.TileCount == 121);
        }

        sb.AppendLine(ok ? "  Range: PASS" : "  Range: FAIL");
        return ok;
    }

    /// <summary>The same world always yields the same partition: two scratch worlds, and
    /// one world built twice.</summary>
    private static bool AssertDeterminism(StringBuilder sb)
    {
        var first = ScratchWorld();
        var second = ScratchWorld();
        ProvinceBuilder.Build(first, log: false);
        ProvinceBuilder.Build(second, log: false);
        bool ok = Check(sb, "two builds of one world give one map",
            first.ProvinceMap != null && second.ProvinceMap != null && first.ProvinceMap.SequenceEqual(second.ProvinceMap));
        bool same = first.Provinces.Count == second.Provinces.Count;
        for (int i = 0; same && i < first.Provinces.Count; i++)
            same = SameProvince(first.Provinces[i], second.Provinces[i]);
        ok &= Check(sb, "two builds of one world give the same provinces", same);

        if (first.ProvinceMap != null)
        {
            var before = (byte[])first.ProvinceMap.Clone();
            ProvinceBuilder.Build(first, log: false);
            ok &= Check(sb, "rebuilding a world gives the same map",
                first.ProvinceMap != null && first.ProvinceMap.SequenceEqual(before));
        }

        sb.AppendLine(ok ? "  Determinism: PASS" : "  Determinism: FAIL");
        return ok;
    }

    /// <summary>The legacy path CycleInitializer takes: a world without provinces is
    /// built once and saved; a world with them is left alone, before and after a trip
    /// through the serializer.</summary>
    private static bool AssertLegacyBuild(StringBuilder sb)
    {
        var world = ScratchWorld();
        bool ok = Check(sb, "a scratch world starts without provinces", !world.HasProvinces());
        bool built = ProvinceBuilder.EnsureBuilt(world, log: false);
        ok &= Check(sb, "EnsureBuilt builds a world without provinces", built && world.HasProvinces());
        var map = world.ProvinceMap;
        bool again = ProvinceBuilder.EnsureBuilt(world, log: false);
        ok &= Check(sb, "EnsureBuilt leaves a built world alone", !again && ReferenceEquals(map, world.ProvinceMap));
        var rt = RoundTrip(world);
        ok &= Check(sb, "a built world still has its provinces after a save, and is not rebuilt",
            rt != null && rt.HasProvinces() && !ProvinceBuilder.EnsureBuilt(rt, log: false));

        sb.AppendLine(ok ? "  Legacy build: PASS" : "  Legacy build: FAIL");
        return ok;
    }

    /// <summary>Land no kingdom owns, as play makes it by opening water into marsh. Opened
    /// before the build, it joins a province of the nearest kingdom and the rest of the
    /// map is as it would have been; opened after the build, it is in no province until
    /// it joins the nearest as it opens (PR50), once, and every rule holds throughout,
    /// a causeway to an island included.</summary>
    private static bool AssertOpenedLand(StringBuilder sb)
    {
        var plain = ScratchWorld();
        ProvinceBuilder.Build(plain, log: false);
        var opened = ScratchWorld();
        int before = 4 * opened.Width + 30;
        opened.Tiles[before].Terrain = OverworldHex.TerrainType.Marsh;
        ProvinceBuilder.Build(opened, log: false);
        var joined = opened.ProvinceAt(30, 4);
        bool ok = Check(sb, "land opened before the build joins an east province",
            joined != null && joined.KingdomId == East);
        bool restSame = plain.ProvinceMap != null && opened.ProvinceMap != null
                        && plain.ProvinceMap.Length == opened.ProvinceMap.Length;
        for (int i = 0; restSame && i < plain.ProvinceMap.Length; i++)
        {
            if (i != before && plain.ProvinceMap[i] != opened.ProvinceMap[i])
                restSame = false;
        }
        ok &= Check(sb, "the rest of the map is as it would have been", restSame);
        ok &= Check(sb, "the rules hold with the opened land", ProvinceBuilder.Validate(opened).Count == 0);

        opened.Tiles[6 * opened.Width + 32].Terrain = OverworldHex.TerrainType.Marsh;
        ok &= Check(sb, "land opened after the build is in no province", opened.ProvinceIndexAt(32, 6) == -1);
        ok &= Check(sb, "the rules still hold after it opens", ProvinceBuilder.Validate(opened).Count == 0);

        // Provinces 7i (PR50): land opened in play joins the nearest province.
        int nearest = ProvinceInfluence.NearestProvince(opened, 32, 6);
        int tilesBefore = nearest >= 0 ? opened.Provinces[nearest].TileCount : -1;
        int joinedIndex = ProvinceBenefits.JoinOpenedLand(opened, 32, 6);
        ok &= Check(sb, "as it opens in play it joins the nearest province, which counts it (PR50)",
            joinedIndex >= 0 && joinedIndex == nearest && opened.ProvinceIndexAt(32, 6) == joinedIndex
            && opened.Provinces[joinedIndex].TileCount == tilesBefore + 1);
        ok &= Check(sb, "the rules hold once it joins", ProvinceBuilder.Validate(opened).Count == 0);
        ok &= Check(sb, "and it joins only once", ProvinceBenefits.JoinOpenedLand(opened, 32, 6) == -1);

        // A causeway to an island: the island rule is judged on the land as built.
        var bridged = ScratchWorld();
        ProvinceBuilder.Build(bridged, log: false);
        bridged.Tiles[30 * bridged.Width + 3].Terrain = OverworldHex.TerrainType.Marsh;   // south of the small island
        ok &= Check(sb, "a causeway opened to an island joins a province, and every rule still holds",
            ProvinceBenefits.JoinOpenedLand(bridged, 3, 30) >= 0 && ProvinceBuilder.Validate(bridged).Count == 0);

        sb.AppendLine(ok ? "  Opened land: PASS" : "  Opened land: FAIL");
        return ok;
    }

    // ── The guild's influence (docs/provinces_v1.md section 7b, PV2a) ───────
    /// <summary>KingdomState.PlayerInfluenceDerived survives the trip, and a kingdom saved
    /// before provinces carried influence loads it as 0, so the first sync spreads the
    /// kingdom's whole value over its provinces.</summary>
    private static bool AssertInfluenceFields(StringBuilder sb)
    {
        var src = new KingdomState { RegionId = West, PlayerInfluence = 41, PlayerInfluenceDerived = 37 };
        var rt = RoundTrip(src);
        bool ok = Check(sb, "KingdomState.PlayerInfluence and PlayerInfluenceDerived",
            rt != null && rt.PlayerInfluence == 41 && rt.PlayerInfluenceDerived == 37);
        var legacy = JsonSerializer.Deserialize<KingdomState>(
            "{\"regionId\":\"kingdom_0\",\"playerInfluence\":25}", SaveManager.JsonOptions);
        ok &= Check(sb, "KingdomState.PlayerInfluenceDerived is 0 on legacy JSON",
            legacy != null && legacy.PlayerInfluence == 25 && legacy.PlayerInfluenceDerived == 0);

        sb.AppendLine(ok ? "  Influence fields: PASS" : "  Influence fields: FAIL");
        return ok;
    }

    /// <summary>The influence rules on the scratch world, the castle at the west seat
    /// city: the migration; a lunation; the presence cap (parking alone never holds); a
    /// deed that holds; the cap with a held neighbour; the hold lines; a write made
    /// outside the province layer; a seizure and a fall; deeds near a front; sovereign
    /// land; water; and a trip through the serializer.</summary>
    private static bool AssertInfluenceRules(StringBuilder sb)
    {
        var cycle = InfluenceCycle(buildProvinces: true);
        var world = cycle.World;
        var west = cycle.Kingdoms[West];
        var east = cycle.Kingdoms[East];
        if (!world.HasProvinces())
        {
            sb.AppendLine("  Influence rules: FAIL (no provinces built)");
            return false;
        }
        var provinces = world.Provinces;
        int capital = world.ProvinceIndexAt(6, 15);
        int eastCapital = world.ProvinceIndexAt(30, 15);
        bool ok = Check(sb, "both seats stand in provinces", capital >= 0 && eastCapital >= 0);
        if (!ok)
        {
            sb.AppendLine("  Influence rules: FAIL");
            return false;
        }

        // The migration: every province starts at its kingdom's value.
        ok &= Check(sb, "the first sync reports a change", ProvinceInfluence.Sync(cycle));
        ok &= Check(sb, "every west province starts at 25 and every east one at 0",
            provinces.All(p => p.Influence == (p.KingdomId == West ? 25 : 0)));
        ok &= Check(sb, "each kingdom's value is derived (25 and 0)",
            west.PlayerInfluence == 25 && west.PlayerInfluenceDerived == 25
            && east.PlayerInfluence == 0 && east.PlayerInfluenceDerived == 0);
        ok &= Check(sb, "nothing is held at the start", !provinces.Any(p => p.Held));
        ok &= Check(sb, "a second sync changes nothing", !ProvinceInfluence.Sync(cycle));

        // One lunation with the castle at the west seat.
        var near = new HashSet<int>(provinces[capital].Neighbors);
        int[] before = provinces.Select(p => p.Influence).ToArray();
        var news = ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, $"the castle's province gains 8 (now {provinces[capital].Influence})",
            provinces[capital].Influence == before[capital] + ProvinceInfluence.PresenceGain);
        bool rest = true;
        for (int i = 0; i < provinces.Count; i++)
        {
            if (i == capital)
                continue;
            int expected = near.Contains(i)
                ? before[i] + ProvinceInfluence.PresenceNeighborGain
                : System.Math.Max(0, before[i] - ProvinceInfluence.Decay);
            rest &= provinces[i].Influence == expected;
        }
        ok &= Check(sb, "each province beside it gains 3, and every other loses 2, down to 0", rest);
        ok &= Check(sb, "a lunation with no change of hold has no news", news.Count == 0);
        ok &= Check(sb, "each kingdom's value is the tile-weighted mean after a lunation", DerivedMatches(cycle));

        // Parking alone never holds.
        for (int lunation = 0; lunation < 30; lunation++)
            ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, $"thirty more lunations leave the castle's province at 40 (now {provinces[capital].Influence})",
            provinces[capital].Influence == ProvinceInfluence.PassiveCap);
        ok &= Check(sb, "every province beside it stops at 40 too",
            near.All(i => provinces[i].Influence == ProvinceInfluence.PassiveCap));
        ok &= Check(sb, "parking the castle without deeds holds nothing", !provinces.Any(p => p.Held));

        // A deed holds.
        int reports = cycle.PendingSiegeReports.Count;
        string heldNews = ProvinceInfluence.Add(cycle, 6, 15, 20);
        ok &= Check(sb, "a deed of 20 at the seat takes the castle's province to 60 and holds it",
            provinces[capital].Influence == 60 && provinces[capital].Held);
        ok &= Check(sb, "the hold is news, returned and in the frontier reports",
            heldNews.Length > 0 && cycle.PendingSiegeReports.Count == reports + 1);
        ok &= Check(sb, "HoldsAny says the west and not the east",
            ProvinceInfluence.HoldsAny(world, West) && !ProvinceInfluence.HoldsAny(world, East));
        ok &= Check(sb, "each kingdom's value is the tile-weighted mean after a deed", DerivedMatches(cycle));

        // A held neighbour does not lift a province past the cap.
        news = ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, "beside the castle and a held province, a province still stops at 40",
            near.All(i => provinces[i].Influence == ProvinceInfluence.PassiveCap));
        ok &= Check(sb, "the held province under the castle neither gains nor fades",
            provinces[capital].Influence == 60 && provinces[capital].Held && news.Count == 0);

        // The hold lines: held at 60, lost below 50.
        cycle.CastleX = -1;
        cycle.CastleY = -1;
        for (int lunation = 0; lunation < 5; lunation++)
            ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, $"five lunations with the castle gone: 50 and still held (now {provinces[capital].Influence})",
            provinces[capital].Influence == 50 && provinces[capital].Held);
        news = ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, "the sixth: 48, lost, and the loss is news",
            provinces[capital].Influence == 48 && !provinces[capital].Held && news.Count == 1);

        // A write made outside the province layer is spread over its kingdom.
        before = provinces.Select(p => p.Influence).ToArray();
        west.PlayerInfluence += 15;
        ProvinceInfluence.Sync(cycle);
        bool spread = true;
        for (int i = 0; i < provinces.Count; i++)
        {
            int expected = provinces[i].KingdomId == West ? System.Math.Min(100, before[i] + 15) : before[i];
            spread &= provinces[i].Influence == expected;
        }
        ok &= Check(sb, "a write of +15 to the west reaches every west province and no east one", spread);
        ok &= Check(sb, "and each kingdom's value is derived again", DerivedMatches(cycle));

        // A seizure and a fall set the whole kingdom, silently.
        reports = cycle.PendingSiegeReports.Count;
        ProvinceInfluence.SetKingdom(cycle, West, 100);
        ok &= Check(sb, "a seizure: every west province 100 and held, the kingdom 100",
            provinces.Where(p => p.KingdomId == West).All(p => p.Influence == 100 && p.Held)
            && west.PlayerInfluence == 100);
        ProvinceInfluence.SetKingdom(cycle, West, 0);
        ok &= Check(sb, "a fall: every west province 0 and not held, the kingdom 0",
            provinces.Where(p => p.KingdomId == West).All(p => p.Influence == 0 && !p.Held)
            && west.PlayerInfluence == 0);
        ok &= Check(sb, "a seizure or a fall adds no news of its own", cycle.PendingSiegeReports.Count == reports);

        // A deed near a front lands on the kingdom's nearest province, or on its capital.
        before = provinces.Select(p => p.Influence).ToArray();
        ProvinceInfluence.AddNear(cycle, East, 6, 15, 15);
        ok &= Check(sb, "a focus with no east province within reach counts for the east capital",
            Changed(provinces, before) == 1
            && provinces[eastCapital].Influence == System.Math.Min(100, before[eastCapital] + 15));
        before = provinces.Select(p => p.Influence).ToArray();
        ProvinceInfluence.AddNear(cycle, East, 23, 5, 7);
        int gained = -1;
        for (int i = 0; i < provinces.Count; i++)
        {
            if (provinces[i].Influence != before[i])
                gained = i;
        }
        ok &= Check(sb, "a focus on the west side of the border counts for the east province across it",
            Changed(provinces, before) == 1 && gained >= 0 && provinces[gained].KingdomId == East
            && (gained == world.ProvinceIndexAt(24, 5) || gained == world.ProvinceIndexAt(24, 6))
            && provinces[gained].Influence == System.Math.Min(100, before[gained] + 7));

        // Sovereign land does not slip.
        ProvinceInfluence.SetKingdom(cycle, East, 70);
        east.ControllingFactionId = KingdomTickSimulation.GuildFactionId;
        ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, "the provinces of a kingdom the guild rules do not decay",
            provinces.Where(p => p.KingdomId == East).All(p => p.Influence == 70 && p.Held));
        east.ControllingFactionId = "";

        ok &= Check(sb, "water reads 0", ProvinceInfluence.InfluenceAt(world, 31, 5) == 0);
        ok &= Check(sb, "the place card's line, held and not, and the report",
            ProvinceInfluence.StandingLine(provinces[eastCapital]).Contains("holds")
            && ProvinceInfluence.StandingLine(provinces[capital]).Contains("held at")
            && ProvinceInfluence.Report(cycle).Contains("Westmarch"));

        // Influence and the hold survive a save.
        var rt = RoundTrip(world);
        bool same = rt != null && rt.Provinces != null && rt.Provinces.Count == provinces.Count;
        for (int i = 0; same && i < provinces.Count; i++)
            same = SameProvince(provinces[i], rt.Provinces[i]);
        ok &= Check(sb, "influence and the hold after a trip through the serializer", same);

        sb.AppendLine(ok ? $"  Influence rules ({provinces.Count} provinces): PASS" : "  Influence rules: FAIL");
        return ok;
    }

    /// <summary>A world without provinces keeps the kingdom-wide influence of old: no sync,
    /// no lunation, and a deed, a front, a fall or a seizure writes the kingdom's own value.</summary>
    private static bool AssertInfluenceWithoutProvinces(StringBuilder sb)
    {
        var cycle = InfluenceCycle(buildProvinces: false);
        var west = cycle.Kingdoms[West];
        bool ok = Check(sb, "a world without provinces is not active", !ProvinceInfluence.Active(cycle));
        ok &= Check(sb, "sync and the lunation do nothing there",
            !ProvinceInfluence.Sync(cycle) && ProvinceInfluence.Tick(cycle).Count == 0 && west.PlayerInfluence == 25);
        ProvinceInfluence.Add(cycle, 6, 15, 20);
        ok &= Check(sb, "a deed raises the tile's kingdom (45)", west.PlayerInfluence == 45);
        ProvinceInfluence.AddNear(cycle, West, -1, -1, 10);
        ok &= Check(sb, "a front's deed raises the kingdom (55)", west.PlayerInfluence == 55);
        ProvinceInfluence.SetKingdom(cycle, West, 0);
        ok &= Check(sb, "a fall sets the kingdom (0)", west.PlayerInfluence == 0);

        sb.AppendLine(ok ? "  Influence without provinces: PASS" : "  Influence without provinces: FAIL");
        return ok;
    }

    /// <summary>The province readout's forecast (docs/provinces_v1.md section 7c) is the
    /// lunation's own arithmetic. In four situations (the castle at the west seat; a held
    /// capital under the castle; the castle gone with the capital held; the east ruled by
    /// the guild) the forecast for every province equals what the next lunation then does to
    /// it, each reason is the expected one, and a held province's count of lunations until
    /// it is lost is exact.</summary>
    private static bool AssertForecast(StringBuilder sb)
    {
        var cycle = InfluenceCycle(buildProvinces: true);
        var world = cycle.World;
        int capital = world.ProvinceIndexAt(6, 15);
        int eastCapital = world.ProvinceIndexAt(30, 15);
        if (capital < 0 || eastCapital < 0 || world.Provinces[capital].Neighbors.Count == 0)
        {
            sb.AppendLine("  Forecast: FAIL (the scratch world's seats are not in provinces)");
            return false;
        }
        var provinces = world.Provinces;
        int beside = provinces[capital].Neighbors[0];
        ProvinceInfluence.Sync(cycle);

        // The castle at the west seat.
        int here = ProvinceInfluence.NextLunation(cycle, capital, out var hereDrift);
        int nextDoor = ProvinceInfluence.NextLunation(cycle, beside, out var nextDoorDrift);
        int far = ProvinceInfluence.NextLunation(cycle, eastCapital, out var farDrift);
        bool ok = Check(sb, "the castle's province forecasts +8, with the castle here",
            here == ProvinceInfluence.PresenceGain && hereDrift == ProvinceInfluence.Drift.CastleHere);
        ok &= Check(sb, "a province beside it forecasts +3, with the castle beside it",
            nextDoor == ProvinceInfluence.PresenceNeighborGain && nextDoorDrift == ProvinceInfluence.Drift.CastleBeside);
        ok &= Check(sb, "an untouched province far away forecasts nothing",
            far == 0 && farDrift == ProvinceInfluence.Drift.Still);
        ok &= Check(sb, "the forecast line reads as written",
            ProvinceInfluence.NextLunationLine(cycle, capital) == "Next lunation: +8, with the castle here.");
        ok &= Check(sb, "with the castle at the west seat, every forecast is what the lunation does", ForecastHolds(cycle));

        // A held capital under the castle: presence stops at the cap.
        ProvinceInfluence.Add(cycle, 6, 15, 35);
        here = ProvinceInfluence.NextLunation(cycle, capital, out hereDrift);
        nextDoor = ProvinceInfluence.NextLunation(cycle, beside, out nextDoorDrift);
        ok &= Check(sb, $"a held capital at {provinces[capital].Influence} under the castle forecasts no change, capped",
            provinces[capital].Held && here == 0 && hereDrift == ProvinceInfluence.Drift.Capped);
        ok &= Check(sb, "beside it, the held capital adds 1 to the castle's 3",
            nextDoor == ProvinceInfluence.PresenceNeighborGain + ProvinceInfluence.HeldNeighborGain
            && nextDoorDrift == ProvinceInfluence.Drift.CastleBeside);
        ok &= Check(sb, "with a held capital, every forecast is what the lunation does", ForecastHolds(cycle));

        // The castle gone: the held capital fades, and the count of lunations is exact.
        cycle.CastleX = -1;
        cycle.CastleY = -1;
        here = ProvinceInfluence.NextLunation(cycle, capital, out hereDrift);
        int lost = ProvinceInfluence.LunationsUntilLost(cycle, capital);
        ok &= Check(sb, $"with the castle gone the held capital forecasts -2 and a loss in {lost} lunations",
            here == -ProvinceInfluence.Decay && hereDrift == ProvinceInfluence.Drift.Fading
            && lost == (provinces[capital].Influence - ProvinceInfluence.LoseLine) / ProvinceInfluence.Decay + 1
            && ProvinceInfluence.NextLunationLine(cycle, capital).EndsWith($"The hold is lost in {lost} lunations.",
                System.StringComparison.Ordinal));
        ok &= Check(sb, "with the castle gone, every forecast is what the lunation does", ForecastHolds(cycle));
        int lunations = 1;
        while (provinces[capital].Held && lunations < 100)
        {
            ProvinceInfluence.Tick(cycle);
            lunations++;
        }
        ok &= Check(sb, $"the hold went after {lunations} lunations, as forecast", lunations == lost);

        // The east ruled by the guild: sovereign land does not fade.
        ProvinceInfluence.SetKingdom(cycle, East, 30);
        cycle.Kingdoms[East].ControllingFactionId = KingdomTickSimulation.GuildFactionId;
        far = ProvinceInfluence.NextLunation(cycle, eastCapital, out farDrift);
        ok &= Check(sb, "a province the guild rules forecasts no change, sovereign",
            far == 0 && farDrift == ProvinceInfluence.Drift.Sovereign);
        ok &= Check(sb, "with the east ruled by the guild, every forecast is what the lunation does", ForecastHolds(cycle));
        cycle.Kingdoms[East].ControllingFactionId = "";

        sb.AppendLine(ok ? "  Forecast: PASS" : "  Forecast: FAIL");
        return ok;
    }

    // ── The courts on provinces (docs/provinces_v1_7g_deeds_and_courts.md) ──

    /// <summary>A realm with a court and no provinces.</summary>
    private const string Lost = "kingdom_9";

    /// <summary>Estates (PR14). The first sync gives them: the west court's seven
    /// courtiers take the west's provinces other than its capital, in index order, by their
    /// place in the court, wrapping round; a stored estate of the realm stays where it is,
    /// and one naming another realm's province is given again; the east's three take the
    /// east's first three; a court whose realm has no provinces gets none; and a second
    /// sync gives nothing.</summary>
    private static bool AssertEstates(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var provinces = cycle.World.Provinces;
        var westLands = provinces.Where(p => p.KingdomId == West && !p.IsCapital).Select(p => p.Id).ToList();
        var eastLands = provinces.Where(p => p.KingdomId == East && !p.IsCapital).Select(p => p.Id).ToList();
        if (westLands.Count < 5 || eastLands.Count < 3)
        {
            sb.AppendLine("  Estates: FAIL (the scratch world's realms have too few provinces)");
            return false;
        }
        var west = cycle.Council.Courts[West];
        var east = cycle.Council.Courts[East];
        var lost = cycle.Council.Courts[Lost];
        west.Courtiers[3].EstateProvinceId = westLands[4];
        west.Courtiers[4].EstateProvinceId = eastLands[0];

        bool ok = Check(sb, "the first sync gives estates", ProvinceInfluence.Sync(cycle));
        bool order = true;
        for (int n = 0; n < west.Courtiers.Count; n++)
        {
            string expected = n == 3 ? westLands[4] : westLands[n % westLands.Count];
            order &= west.Courtiers[n].EstateProvinceId == expected;
        }
        ok &= Check(sb, $"the west's {west.Courtiers.Count} courtiers take its {westLands.Count} provinces past the capital in order, wrapping round; a stored estate of the realm stays and one of another realm is given again",
            order);
        ok &= Check(sb, "the east's three take its first three provinces past the capital",
            east.Courtiers[0].EstateProvinceId == eastLands[0] && east.Courtiers[1].EstateProvinceId == eastLands[1]
            && east.Courtiers[2].EstateProvinceId == eastLands[2]);
        ok &= Check(sb, "a court whose realm has no provinces gets no estates",
            lost.Courtiers.All(c => c.EstateProvinceId == ""));
        var first = provinces.First(p => p.Id == westLands[0]);
        var holders = ProvinceCourts.EstateHolders(cycle, first);
        ok &= Check(sb, "the first west province is the estate of the first and the sixth, in the court's order",
            holders.Count == 2 && holders[0] == west.Courtiers[0] && holders[1] == west.Courtiers[5]);
        ok &= Check(sb, "a second sync gives nothing", !ProvinceInfluence.Sync(cycle));

        sb.AppendLine(ok ? "  Estates: PASS" : "  Estates: FAIL");
        return ok;
    }

    /// <summary>The court's weight and the floors (PR15, PR18, PR21). A deed of 10 at the
    /// west capital, no one's estate, gains 10 before contact, then 5 Hostile, 10 Received,
    /// 11 Welcome, 13 Favored and 15 Trusted; the castle's presence is never weighed. Ill
    /// will halves a deed in its holder's estate (Favored: 13 to 7). With the castle away a
    /// friend's estate at Regard +2 stops at 20, and one already below its floor does not
    /// fade; the forecast names the reason and agrees with the lunation. No floor while the
    /// court is Hostile, while the guild bids to seize the realm, or before contact; and the
    /// readout's court line says each.</summary>
    private static bool AssertCourtRules(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        var west = cycle.Council.Courts[West];
        ProvinceInfluence.Sync(cycle);
        int capital = world.ProvinceIndexAt(6, 15);
        bool ok = Check(sb, "the west capital is a province and no one's estate",
            capital >= 0 && ProvinceCourts.EstateHolders(cycle, provinces[capital]).Count == 0);
        if (!ok)
        {
            sb.AppendLine("  Court rules: FAIL");
            return false;
        }

        // The court's weight, deed by deed at the capital.
        int gained = DeedAtCapital(cycle, capital, 10);
        ok &= Check(sb, $"before contact a deed of 10 gains 10 (it gained {gained})", gained == 10);
        var weights = new (int score, int gain, CourtStandingBand band)[]
        {
            (-6, 5, CourtStandingBand.Hostile),
            (0, 10, CourtStandingBand.Received),
            (3, 11, CourtStandingBand.Welcome),
            (9, 13, CourtStandingBand.Favored),
            (16, 15, CourtStandingBand.Trusted),
        };
        foreach (var (score, gain, band) in weights)
        {
            SetScore(west, score);
            gained = DeedAtCapital(cycle, capital, 10);
            ok &= Check(sb, $"at {band} a deed of 10 gains {gain} (it gained {gained})",
                west.Band() == band && gained == gain);
        }
        provinces[capital].Influence = 0;
        provinces[capital].Held = false;
        ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, $"at Trusted the castle's presence still gives 8 (now {provinces[capital].Influence})",
            provinces[capital].Influence == ProvinceInfluence.PresenceGain);

        // Ill will halves a deed in the holder's estate.
        var cold = west.Courtiers[0];
        cold.Regard = -1;
        SetScore(west, 9);
        var coldLand = provinces.First(p => p.Id == cold.EstateProvinceId);
        coldLand.Influence = 0;
        coldLand.Held = false;
        ProvinceInfluence.Add(cycle, coldLand.SeatX, coldLand.SeatY, 10);
        ok &= Check(sb, $"at Favored, ill will halves a deed of 10 in the holder's estate, 13 to 7 (now {coldLand.Influence})",
            coldLand.Influence == 7 && ProvinceCourts.HalvesDeeds(cycle, coldLand));
        string coldLine = ProvinceCourts.ReadoutLine(cycle, coldLand);
        ok &= Check(sb, "the readout says deeds there count half, and the realm's x1.25",
            coldLine.Contains("Deeds here count half.") && coldLine.Contains("x1.25"));

        // A friend's estate stops at its floor, with the castle away.
        cycle.CastleX = -1;
        cycle.CastleY = -1;
        var fond = west.Courtiers[1];
        fond.Regard = 2;
        SetScore(west, 0);
        var fondLand = provinces.First(p => p.Id == fond.EstateProvinceId);
        fondLand.Influence = 23;
        fondLand.Held = false;
        ok &= Check(sb, "a friend at Regard +2 sets a floor of 20", ProvinceCourts.Floor(cycle, fondLand) == 20);
        ok &= Check(sb, "the readout names the floor",
            ProvinceCourts.ReadoutLine(cycle, fondLand).Contains("Influence here does not fade below 20."));
        ok &= Check(sb, "above the floor it fades as before (23 to 21), as forecast", ForecastHolds(cycle) && fondLand.Influence == 21);
        int change = ProvinceInfluence.NextLunation(cycle, fondLand.Index, out var drift);
        string line = ProvinceInfluence.NextLunationLine(cycle, fondLand.Index);
        ok &= Check(sb, "at 21 it forecasts -1 and no lower, for the friend's regard",
            change == -1 && drift == ProvinceInfluence.Drift.EstateFloor
            && line.Contains("and no lower") && line.EndsWith("keeps it from fading below 20.", System.StringComparison.Ordinal));
        ok &= Check(sb, "it stops at 20, as forecast", ForecastHolds(cycle) && fondLand.Influence == 20);
        change = ProvinceInfluence.NextLunation(cycle, fondLand.Index, out drift);
        ok &= Check(sb, "at 20 it forecasts no change, for the friend's regard",
            change == 0 && drift == ProvinceInfluence.Drift.EstateFloor
            && ProvinceInfluence.NextLunationLine(cycle, fondLand.Index).StartsWith("Next lunation: no change.", System.StringComparison.Ordinal));
        ok &= Check(sb, "and holds there, as forecast", ForecastHolds(cycle) && fondLand.Influence == 20);
        var deep = west.Courtiers[2];
        deep.Regard = 3;
        var deepLand = provinces.First(p => p.Id == deep.EstateProvinceId);
        deepLand.Influence = 10;
        deepLand.Held = false;
        ok &= Check(sb, "below its floor of 30 an estate does not fade at all, as forecast",
            ForecastHolds(cycle) && deepLand.Influence == 10);

        // No floor while the court is Hostile, while the guild bids to seize, or before contact.
        SetScore(west, -6);
        ok &= Check(sb, "no floor at a Hostile court, and the readout says why",
            ProvinceCourts.Floor(cycle, fondLand) == 0
            && ProvinceCourts.ReadoutLine(cycle, fondLand).Contains("The court is Hostile, so no regard protects it."));
        ok &= Check(sb, "so the estate fades (20 to 18), as forecast", ForecastHolds(cycle) && fondLand.Influence == 18);
        SetScore(west, 0);
        fondLand.Influence = 20;
        var seize = new Warfront { Id = "wf_seize", AggressorKingdomId = East, DefenderKingdomId = West, PlayerSeizing = true };
        cycle.Warfronts = new List<Warfront> { seize };
        ok &= Check(sb, "no floor while the guild bids to seize the realm",
            ProvinceCourts.Floor(cycle, fondLand) == 0 && ProvinceCourts.GuildSeizing(cycle, West)
            && ForecastHolds(cycle) && fondLand.Influence == 18);
        seize.Closed = true;
        ok &= Check(sb, "a closed front's bid revokes nothing", ProvinceCourts.Floor(cycle, fondLand) == 20);
        cycle.Warfronts.Clear();
        west.HasContact = false;
        ok &= Check(sb, "before contact no floor, and the readout names only the offices",
            ProvinceCourts.Floor(cycle, fondLand) == 0 && !ProvinceCourts.HalvesDeeds(cycle, coldLand)
            && ProvinceCourts.ReadoutLine(cycle, fondLand) == "Estate of the Marshal and the Chancellor.");
        ok &= Check(sb, "the report names the court and the estates",
            ProvinceInfluence.Report(cycle).Contains("court Unknown (no contact)") && ProvinceInfluence.Report(cycle).Contains("estate of"));

        sb.AppendLine(ok ? "  Court rules: PASS" : "  Court rules: FAIL");
        return ok;
    }

    /// <summary>The deeds of PV2b-1: 10, 8 and 5. A deed moves nothing while the interval
    /// walks and lands once it does not. A lead whose place is unseen stands and gives
    /// nothing; once the place is Sighted the lead goes and its place's province gains 5,
    /// once. A contract board's id names its city's tile.</summary>
    private static bool AssertDeeds(StringBuilder sb)
    {
        var cycle = InfluenceCycle(buildProvinces: true);
        var world = cycle.World;
        var provinces = world.Provinces;
        ProvinceInfluence.Sync(cycle);
        int capital = world.ProvinceIndexAt(6, 15);
        int land = world.ProvinceIndexAt(18, 5);
        bool ok = Check(sb, "the deeds are 10, 8 and 5",
            ProvinceInfluence.SiteClearedGain == 10 && ProvinceInfluence.ContractGain == 8
            && ProvinceInfluence.LeadGain == 5);
        if (capital < 0 || land < 0)
        {
            sb.AppendLine("  Deeds: FAIL (the scratch world's places are not in provinces)");
            return false;
        }

        int before = provinces[capital].Influence;
        cycle.Interval.Active = true;
        ok &= Check(sb, "while the interval walks a deed moves nothing",
            ProvinceInfluence.Deed(cycle, 6, 15, 10) == "" && provinces[capital].Influence == before);
        cycle.Interval.Active = false;
        ProvinceInfluence.Deed(cycle, 6, 15, 10);
        ok &= Check(sb, "once it does not, the deed lands", provinces[capital].Influence == before + 10);

        // A rumor followed home.
        int place = world.Pois.Count;
        world.Pois.Add(new WorldPoi { X = 18, Y = 5, Kind = PoiKind.Combat, KingdomId = West });
        cycle.Leads.Add(new Lead
        {
            Id = "debug:" + place, PoiIndex = place, CenterX = 18, CenterY = 6, Radius = 3,
            Source = "debug", Line = "A test of the deed.",
        });
        before = provinces[land].Influence;
        LeadService.Prune(cycle);
        ok &= Check(sb, "a lead whose place is unseen stands and gives nothing",
            cycle.Leads.Count == 1 && provinces[land].Influence == before);
        world.Pois[place].Discovered = true;
        LeadService.Prune(cycle);
        ok &= Check(sb, $"once its place is Sighted the lead goes and its province gains 5 ({before} to {provinces[land].Influence})",
            cycle.Leads.Count == 0 && provinces[land].Influence == before + ProvinceInfluence.LeadGain);
        LeadService.Prune(cycle);
        ok &= Check(sb, "and only once", provinces[land].Influence == before + ProvinceInfluence.LeadGain);

        // A contract board names its city's tile.
        var board = new CityContractBoardState { CityId = West + ":18,25" };
        ok &= Check(sb, "a board's id names its city's tile, and an id with none names nothing",
            CityContractService.TryBoardCenter(board, out int boardX, out int boardY) && boardX == 18 && boardY == 25
            && !CityContractService.TryBoardCenter(new CityContractBoardState { CityId = "nowhere" }, out _, out _));

        sb.AppendLine(ok ? "  Deeds: PASS" : "  Deeds: FAIL");
        return ok;
    }

    /// <summary>Betrayal (PR20): aiding the invader against a realm whose court rates the
    /// guild Welcome costs every courtier there 1 Regard, to no lower than -3, lost or won,
    /// with a line in the Herald's Report; against a Received court it costs nothing.</summary>
    private static bool AssertBetrayal(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var west = cycle.Council.Courts[West];
        foreach (var courtier in west.Courtiers)
            courtier.Regard = 1;
        west.Courtiers[0].Regard = -3;
        SetScore(west, 3);
        cycle.Warfronts = new List<Warfront> { Front("wf_aid") };
        int reports = cycle.Council.Reports.Count;
        KingdomTickSimulation.ApplyIntervention(cycle, "wf_aid", WarfrontSide.Aid, success: false);
        bool ok = Check(sb, "aiding the invader against a Welcome court costs every courtier 1 Regard, to no lower than -3",
            west.Courtiers[0].Regard == -3 && west.Courtiers.Skip(1).All(c => c.Regard == 0));
        ok &= Check(sb, "and the Herald's Report says so",
            cycle.Council.Reports.Count == reports + 1 && cycle.Council.Reports[reports].KingdomId == West);
        SetScore(west, 0);
        cycle.Warfronts = new List<Warfront> { Front("wf_aid_2") };
        var regard = west.Courtiers.Select(c => c.Regard).ToArray();
        KingdomTickSimulation.ApplyIntervention(cycle, "wf_aid_2", WarfrontSide.Aid, success: true);
        ok &= Check(sb, "against a Received court it costs nothing",
            west.Courtiers.Select(c => c.Regard).SequenceEqual(regard) && cycle.Council.Reports.Count == reports + 1);

        sb.AppendLine(ok ? "  Betrayal: PASS" : "  Betrayal: FAIL");
        return ok;
    }

    /// <summary>Favors as deeds (PR16, PR44): a charter of 20 (a major one 35) in the province
    /// the party stands in, in the creditor's realm only; a Military favor's 15 in the
    /// courtier's estate, with the invasion of the realm 10 back but never to the line that
    /// decides it; an Economic favor's 10 in the estate; an Intelligence favor makes every
    /// site in the province Discovered and Known, and has nothing to spend on once they are;
    /// Arcane and Passage favors have no deed, a favor owed by the guild is never one, and no
    /// favor is spent as a deed while the interval walks.</summary>
    private static bool AssertFavorDeeds(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        ProvinceInfluence.Sync(cycle);   // gives the courtiers their estates
        var west = cycle.Council.Courts[West];
        int capital = world.ProvinceIndexAt(6, 15);
        var courtier = west.Courtiers.FirstOrDefault(c => !string.IsNullOrEmpty(c.EstateProvinceId));
        int estate = courtier == null ? -1 : provinces.FindIndex(p => p.Id == courtier.EstateProvinceId);
        bool ok = Check(sb, "the favor deeds are 20 (major 35), 15 with the front 10, and 10",
            FavorDeeds.CharterGain == 20 && FavorDeeds.CharterMajorGain == 35 && FavorDeeds.MarshalGain == 15
            && FavorDeeds.MarshalFrontPush == 10 && FavorDeeds.StewardGain == 10);
        if (capital < 0 || estate < 0)
        {
            sb.AppendLine("  Favor deeds: FAIL (the scratch world's capital or a courtier's estate is missing)");
            return false;
        }
        Favor Owed(string type, bool major = false) => new Favor
        {
            Id = "favor_" + type, OwedToGuild = true, KingdomId = West, CourtierId = courtier.Id,
            Type = type, IsMajor = major,
        };

        ok &= Check(sb, "Arcane and Passage favors have no deed, and a favor owed by the guild is never one",
            !FavorDeeds.HasDeed(Owed("Arcane")) && !FavorDeeds.HasDeed(Owed("Passage"))
            && !FavorDeeds.HasDeed(new Favor { OwedToGuild = false, Type = "Political", KingdomId = West })
            && FavorDeeds.HasDeed(Owed("Political")) && FavorDeeds.HasDeed(Owed("Intelligence")));

        // A charter, where the party stands.
        provinces[capital].Influence = 0;
        provinces[capital].Held = false;
        ok &= Check(sb, "a charter can be spent standing in the realm, and not standing in another",
            FavorDeeds.Ineligibility(cycle, Owed("Political"), 6, 15) == null
            && FavorDeeds.Ineligibility(cycle, Owed("Political"), 30, 15) != null);
        FavorDeeds.Spend(cycle, Owed("Political"), 6, 15);
        int charter = provinces[capital].Influence;
        provinces[capital].Influence = 0;
        FavorDeeds.Spend(cycle, Owed("Political", major: true), 6, 15);
        ok &= Check(sb, $"a charter gives 20 in the province the party stands in, a major one 35 ({charter}, {provinces[capital].Influence})",
            charter == 20 && provinces[capital].Influence == 35);

        // The Marshal and the Steward, in the estate.
        cycle.Warfronts = new List<Warfront> { Front("wf_favor") };
        provinces[estate].Influence = 0;
        provinces[estate].Held = false;
        FavorDeeds.Spend(cycle, Owed("Military"), 6, 15);
        ok &= Check(sb, $"a Military favor gives 15 in the courtier's estate ({provinces[estate].Influence}) and the invasion falls back 10 ({cycle.Warfronts[0].Advance})",
            provinces[estate].Influence == 15 && cycle.Warfronts[0].Advance == 40);
        cycle.Warfronts[0].Advance = 4;
        FavorDeeds.Spend(cycle, Owed("Military"), 6, 15);
        ok &= Check(sb, "but never to the line that decides it",
            cycle.Warfronts[0].Advance == FavorDeeds.FrontFloor && !cycle.Warfronts[0].Closed);
        provinces[estate].Influence = 0;
        FavorDeeds.Spend(cycle, Owed("Economic"), 6, 15);
        ok &= Check(sb, "an Economic favor gives 10 in the estate", provinces[estate].Influence == 10);

        // The Spymaster's dossier: two sites in the capital province, one of them unfound.
        var spots = new List<(int x, int y)>();
        for (int y = 0; y < world.Height && spots.Count < 2; y++)
        {
            for (int x = 0; x < world.Width && spots.Count < 2; x++)
            {
                if (!world.GetTile(x, y).IsWater && world.ProvinceIndexAt(x, y) == capital)
                    spots.Add((x, y));
            }
        }
        int first = world.Pois.Count;
        world.Pois.Add(new WorldPoi { X = spots[0].x, Y = spots[0].y, Kind = PoiKind.Combat, KingdomId = West });
        world.Pois.Add(new WorldPoi { X = spots[1].x, Y = spots[1].y, Kind = PoiKind.Rest, KingdomId = West, Discovered = true });
        var learned = FavorDeeds.Spend(cycle, Owed("Intelligence"), 6, 15);
        ok &= Check(sb, "an Intelligence favor makes every site in the province Discovered and Known, and names the newly found",
            world.Pois[first].Discovered && world.Pois[first].Known && world.Pois[first + 1].Known
            && learned.Revealed.Contains(world.Pois[first]) && !learned.Revealed.Contains(world.Pois[first + 1]));
        ok &= Check(sb, "and once they are, it has nothing to spend on",
            FavorDeeds.Ineligibility(cycle, Owed("Intelligence"), 6, 15) != null);

        // The interval.
        cycle.Interval.Active = true;
        ok &= Check(sb, "while the interval walks no favor is spent as a deed",
            FavorDeeds.Ineligibility(cycle, Owed("Political"), 6, 15) != null);
        cycle.Interval.Active = false;

        sb.AppendLine(ok ? "  Favor deeds: PASS" : "  Favor deeds: FAIL");
        return ok;
    }

    // ── What holding gives (docs/provinces_v1_7i_held_benefits.md, PV2c) ───

    /// <summary>Held for its benefits is a held province or a Patron's estate (PR17), and the
    /// Patron's estate is not Held. At the new moon each such province sends 2 supplies, and
    /// the Economic favor's mark pays twice once, at the next new moon, held then or not. Each
    /// such province's places not yet on the map are Sighted and their tiles charted, never a
    /// Concord node, never a place elsewhere, and nothing under Classic Exploration. The tile
    /// reads agree.</summary>
    private static bool AssertBenefits(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        ProvinceInfluence.Sync(cycle);   // gives the courtiers their estates
        foreach (var province in provinces)
        {
            province.Influence = 0;
            province.Held = false;
            province.YieldDoubledLunation = -1;
        }
        int capital = world.ProvinceIndexAt(6, 15);
        int other = world.ProvinceIndexAt(30, 15);
        var west = cycle.Council.Courts[West];
        string capitalId = capital >= 0 ? provinces[capital].Id : "";
        var courtier = west.Courtiers.FirstOrDefault(c => !string.IsNullOrEmpty(c.EstateProvinceId)
                                                          && c.EstateProvinceId != capitalId);
        int estate = courtier == null ? -1 : provinces.FindIndex(p => p.Id == courtier.EstateProvinceId);
        bool ok = Check(sb, "held benefits are 2 supplies, 1 Essence and an ally's -25 on the camp",
            ProvinceBenefits.HeldYield == 2 && ProvinceBenefits.HeldCastDiscount == 1
            && ProvinceBenefits.HeldCampModifier == -25);
        if (capital < 0 || other < 0 || estate < 0)
        {
            sb.AppendLine("  Held benefits: FAIL (a capital, or an estate outside the west capital, is missing)");
            return false;
        }
        (int x, int y) TileIn(int index, int nth)
        {
            int seen = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (!world.GetTile(x, y).IsWater && world.ProvinceIndexAt(x, y) == index && seen++ == nth)
                        return (x, y);
                }
            }
            return (-1, -1);
        }

        // Held for its benefits: held, a Patron's estate, or neither.
        provinces[capital].Influence = 70;
        provinces[capital].Held = true;
        ok &= Check(sb, "a held province is held for its benefits, and one neither held nor a Patron's estate is not",
            ProvinceBenefits.HeldForBenefits(cycle, provinces[capital])
            && !ProvinceBenefits.HeldForBenefits(cycle, provinces[estate])
            && !ProvinceBenefits.HeldForBenefits(cycle, provinces[other]));
        west.HasContact = true;
        west.PatronCourtierIds.Add(courtier.Id);
        ok &= Check(sb, "a Patron's estate is held for its benefits, and is not Held (PR17)",
            ProvinceBenefits.HeldForBenefits(cycle, provinces[estate]) && !provinces[estate].Held
            && ProvinceBenefits.EstatePatron(cycle, provinces[estate]) == courtier);
        ok &= Check(sb, "the readout's note marks the Patron's estate and nothing else",
            ProvinceBenefits.PatronNote(cycle, provinces[estate]).Length > 0
            && ProvinceBenefits.PatronNote(cycle, provinces[capital]).Length == 0
            && ProvinceBenefits.PatronNote(cycle, provinces[other]).Length == 0);
        ok &= Check(sb, "the tile reads agree: the west seat is held ground, the east seat and the lake are not",
            ProvinceBenefits.HeldForBenefitsAt(cycle, 6, 15) && !ProvinceBenefits.HeldForBenefitsAt(cycle, 30, 15)
            && !ProvinceBenefits.HeldForBenefitsAt(cycle, 31, 5));

        // The new moon's yield (PR48). No places yet, so nothing to sight.
        cycle.Supplies = 0;
        cycle.Calendar.CurrentLunation = 3;
        var lines = ProvinceBenefits.NewMoon(cycle);
        ok &= Check(sb, $"at the new moon each province held for its benefits sends 2 supplies, in one line ({cycle.Supplies}, {lines.Count} line(s))",
            cycle.Supplies == 4 && lines.Count == 1);
        ProvinceBenefits.DoubleNextYield(cycle, capital);
        ProvinceBenefits.DoubleNextYield(cycle, other);
        ProvinceBenefits.NewMoon(cycle);
        ok &= Check(sb, $"the Economic favor's mark waits for the next new moon ({cycle.Supplies})",
            cycle.Supplies == 8 && provinces[capital].YieldDoubledLunation == 4
            && provinces[other].YieldDoubledLunation == 4);
        cycle.Calendar.CurrentLunation = 4;
        ProvinceBenefits.NewMoon(cycle);
        ok &= Check(sb, $"then pays twice once, and a mark on a province not held is spent all the same ({cycle.Supplies})",
            cycle.Supplies == 14 && provinces[capital].YieldDoubledLunation == -1
            && provinces[other].YieldDoubledLunation == -1);
        ProvinceBenefits.NewMoon(cycle);
        ok &= Check(sb, $"and the next new moon pays the plain yield ({cycle.Supplies})", cycle.Supplies == 18);

        // The new moon's sighting (PR47), under camp sight's rules.
        var (cx, cy) = TileIn(capital, 3);
        var (nx, ny) = TileIn(capital, 5);
        var (ex, ey) = TileIn(estate, 0);
        var (ox, oy) = TileIn(other, 0);
        int first = world.Pois.Count;
        world.Pois.Add(new WorldPoi { X = cx, Y = cy, Kind = PoiKind.Combat, KingdomId = West });
        world.Pois.Add(new WorldPoi { X = nx, Y = ny, Kind = PoiKind.Concord, KingdomId = West });
        world.Pois.Add(new WorldPoi { X = ex, Y = ey, Kind = PoiKind.Rest, KingdomId = West });
        world.Pois.Add(new WorldPoi { X = ox, Y = oy, Kind = PoiKind.Combat, KingdomId = East });
        bool classic = PlayerSession.DebugClassicExploration;
        int supplies = cycle.Supplies;
        PlayerSession.DebugClassicExploration = false;
        try
        {
            lines = ProvinceBenefits.NewMoon(cycle);
        }
        finally
        {
            PlayerSession.DebugClassicExploration = classic;
        }
        ok &= Check(sb, $"at the new moon the places of each province held for its benefits are Sighted, a line for each beside the yield's ({lines.Count} lines)",
            world.Pois[first].Discovered && world.Pois[first + 2].Discovered && lines.Count == 3
            && cycle.Supplies == supplies + 4);
        ok &= Check(sb, "and their tiles are charted",
            world.GetTile(cx, cy).Discovery != TileDiscovery.Unseen && world.GetTile(ex, ey).Discovery != TileDiscovery.Unseen);
        ok &= Check(sb, "a Concord node is never Sighted, nor a place in a province not held for its benefits",
            !world.Pois[first + 1].Discovered && !world.Pois[first + 3].Discovered);
        var (lx, ly) = TileIn(capital, 7);
        int late = world.Pois.Count;
        world.Pois.Add(new WorldPoi { X = lx, Y = ly, Kind = PoiKind.Rest, KingdomId = West });
        PlayerSession.DebugClassicExploration = true;
        try
        {
            ProvinceBenefits.NewMoon(cycle);
        }
        finally
        {
            PlayerSession.DebugClassicExploration = classic;
        }
        ok &= Check(sb, "nothing is Sighted under Classic Exploration", !world.Pois[late].Discovered);

        sb.AppendLine(ok ? "  Held benefits: PASS" : "  Held benefits: FAIL");
        return ok;
    }

    // ── Provinces on every lens (docs/provinces_v1_7d_lenses.md) ─────────

    /// <summary>The Reach lens's bands sit on the rules' lines: 0 untouched; 1 to 39
    /// growing; 40 and up, not held, ready (60 too, until a hold is set); held below 60,
    /// slipping; held at 60 or more, held.</summary>
    private static bool AssertBands(StringBuilder sb)
    {
        bool ok = BandIs(sb, 0, false, ProvinceInfluence.Band.Untouched);
        ok &= BandIs(sb, 1, false, ProvinceInfluence.Band.Growing);
        ok &= BandIs(sb, ProvinceInfluence.PassiveCap - 1, false, ProvinceInfluence.Band.Growing);
        ok &= BandIs(sb, ProvinceInfluence.PassiveCap, false, ProvinceInfluence.Band.Ready);
        ok &= BandIs(sb, ProvinceInfluence.HoldLine - 1, false, ProvinceInfluence.Band.Ready);
        ok &= BandIs(sb, ProvinceInfluence.HoldLine, false, ProvinceInfluence.Band.Ready);
        ok &= BandIs(sb, ProvinceInfluence.LoseLine, true, ProvinceInfluence.Band.HeldSlipping);
        ok &= BandIs(sb, ProvinceInfluence.HoldLine - 1, true, ProvinceInfluence.Band.HeldSlipping);
        ok &= BandIs(sb, ProvinceInfluence.HoldLine, true, ProvinceInfluence.Band.Held);
        ok &= BandIs(sb, ProvinceInfluence.MaxInfluence, true, ProvinceInfluence.Band.Held);
        ok &= Check(sb, "a missing province reads as untouched",
            ProvinceInfluence.BandOf(null) == ProvinceInfluence.Band.Untouched);
        sb.AppendLine(ok ? "  Bands: PASS" : "  Bands: FAIL");
        return ok;
    }

    private static bool BandIs(StringBuilder sb, int influence, bool held, ProvinceInfluence.Band expected)
    {
        var band = ProvinceInfluence.BandOf(new Province { Influence = influence, Held = held });
        return Check(sb, $"influence {influence}{(held ? ", held," : "")} reads as {expected} (it read {band})",
            band == expected);
    }

    /// <summary>A province's name goes at the most interior of its painted tiles. On the
    /// scratch world with everything painted, every province has a label tile, inside it,
    /// never on a settlement while the province has open ground, as deep as any of its
    /// tiles, and the same on a second call. Depth is counted apart from the atlas: one
    /// less than the hex distance to the nearest place outside the region, a place just
    /// off the map counting as outside. Then with only the tiles west of x = 18 painted:
    /// a province with nothing painted has no label, and every other label is a painted
    /// tile, as deep as any painted tile of its province.</summary>
    private static bool AssertLabelTiles(StringBuilder sb)
    {
        var world = ScratchWorld();
        ProvinceBuilder.Build(world, log: false);
        int w = world.Width;
        bool ok = true;
        for (int pass = 0; pass < 2; pass++)
        {
            bool revealAll = pass == 0;
            if (!revealAll)
            {
                for (int i = 0; i < world.Tiles.Length; i++)
                    world.Tiles[i].Discovery = i % w < 18 ? TileDiscovery.Explored : TileDiscovery.Unseen;
            }
            for (int p = 0; p < world.Provinces.Count; p++)
            {
                int province = p;
                System.Func<int, int, bool> inRegion = (x, y) => world.ProvinceIndexAt(x, y) == province
                    && (revealAll || world.GetTile(x, y).Discovery != TileDiscovery.Unseen);
                var region = new List<int>();
                for (int i = 0; i < world.Tiles.Length; i++)
                {
                    if (inRegion(i % w, i / w))
                        region.Add(i);
                }
                int tile = WorldAtlas3D.ProvinceLabelTile(world, p, revealAll);
                string where = revealAll ? $"province {p}" : $"province {p}, west painted,";
                if (region.Count == 0)
                {
                    ok &= Check(sb, $"{where} with nothing painted has no label", tile == -1);
                    continue;
                }
                if (tile < 0 || !inRegion(tile % w, tile / w))
                {
                    ok &= Check(sb, $"{where} has a label on its painted ground", false);
                    continue;
                }
                bool anyOpen = region.Any(i => world.Tiles[i].SettlementIndex < 0);
                ok &= Check(sb, $"{where}'s label is off the settlements while it has open ground",
                    !anyOpen || world.Tiles[tile].SettlementIndex < 0);
                int deepest = region.Where(i => !anyOpen || world.Tiles[i].SettlementIndex < 0)
                                    .Max(i => DepthByDistance(world, inRegion, i));
                ok &= Check(sb, $"{where}'s label is as deep as any of its tiles",
                    DepthByDistance(world, inRegion, tile) == deepest);
                ok &= Check(sb, $"{where}'s label is the same on a second call",
                    WorldAtlas3D.ProvinceLabelTile(world, p, revealAll) == tile);
            }
        }
        sb.AppendLine(ok ? $"  Label tiles ({world.Provinces.Count} provinces): PASS" : "  Label tiles: FAIL");
        return ok;
    }

    /// <summary>Hex steps from a tile in to its region's edge, counted apart from the atlas's
    /// walk: one less than the hex distance to the nearest place outside the region, the
    /// ring just off the map counting as outside.</summary>
    private static int DepthByDistance(WorldData world, System.Func<int, int, bool> inRegion, int tile)
    {
        int w = world.Width, h = world.Height;
        int col = tile % w, row = tile / w;
        int nearest = int.MaxValue;
        for (int y = -1; y <= h; y++)
        {
            for (int x = -1; x <= w; x++)
            {
                bool inside = x >= 0 && x < w && y >= 0 && y < h && inRegion(x, y);
                if (!inside)
                    nearest = System.Math.Min(nearest, HexCoord.OffsetDistance(col, row, x, y));
            }
        }
        return nearest - 1;
    }

    /// <summary>The names' fade and the inks' least width, from the atlas's own functions:
    /// names gone at zoom01 0.45 and out, whole at 0.34 and in, half shown half way; 3 px
    /// of ink on a 1080-pixel view at the default world's whole-world zoom (camera
    /// distance 115.3, a view 173 units high) is about 0.48 units, and close in the ink
    /// keeps its painted 0.06.</summary>
    private static bool AssertLensScales(StringBuilder sb)
    {
        bool ok = Check(sb, "names are gone at zoom01 0.45", WorldAtlas3D.NameAlpha(0.45f) == 0f);
        ok &= Check(sb, "names are gone at the whole-world view (zoom01 0.76)", WorldAtlas3D.NameAlpha(0.76f) == 0f);
        ok &= Check(sb, "names are whole at zoom01 0.34", Mathf.IsEqualApprox(WorldAtlas3D.NameAlpha(0.34f), 1f));
        ok &= Check(sb, "names are whole close in", WorldAtlas3D.NameAlpha(0f) == 1f);
        ok &= Check(sb, "names are half shown half way", Mathf.Abs(WorldAtlas3D.NameAlpha(0.395f) - 0.5f) < 0.01f);
        float far = WorldAtlas3D.InkWorldWidth(115.3f, 3f / 1080f, 0.06f);
        ok &= Check(sb, $"3 px of ink at the whole-world view is about 0.48 units (it was {far})",
            Mathf.Abs(far - 0.48f) < 0.01f);
        ok &= Check(sb, "close in, the ink keeps its painted width",
            WorldAtlas3D.InkWorldWidth(6f, 3f / 1080f, 0.06f) == 0.06f);
        sb.AppendLine(ok ? "  Lens scales: PASS" : "  Lens scales: FAIL");
        return ok;
    }

    /// <summary>Forecast every province, run one lunation, and compare.</summary>
    private static bool ForecastHolds(CycleState cycle)
    {
        var provinces = cycle.World.Provinces;
        var expected = new int[provinces.Count];
        var before = new int[provinces.Count];
        for (int i = 0; i < provinces.Count; i++)
        {
            expected[i] = ProvinceInfluence.NextLunation(cycle, i, out _);
            before[i] = provinces[i].Influence;
        }
        ProvinceInfluence.Tick(cycle);
        for (int i = 0; i < provinces.Count; i++)
        {
            if (provinces[i].Influence - before[i] != expected[i])
                return false;
        }
        return true;
    }

    /// <summary>The live world's provinces against every rule, and the influence
    /// invariants: every hold agrees with its influence, and every kingdom with nothing
    /// waiting to spread has the tile-weighted mean of its provinces. Read-only: a world
    /// without provinces is skipped, never built here.</summary>
    private static bool AssertLiveWorld(StringBuilder sb)
    {
        var liveCycle = SaveManager.ActiveSave?.Cycle;
        var live = liveCycle?.World;
        if (live == null || live.Tiles == null || live.Tiles.Length == 0)
        {
            sb.AppendLine("  Live world: SKIPPED (no world in the active save)");
            return true;
        }
        if (!live.HasProvinces())
        {
            sb.AppendLine("  Live world: SKIPPED (no provinces yet; the strategic view builds them when it opens the cycle)");
            return true;
        }
        var fails = ProvinceBuilder.Validate(live);
        foreach (string fail in fails)
            sb.AppendLine("    FAIL: live world: " + fail);
        bool ok = fails.Count == 0;

        int disagree = 0;
        foreach (var province in live.Provinces)
        {
            if (province.Influence < 0 || province.Influence > ProvinceInfluence.MaxInfluence
                || (province.Held && province.Influence < ProvinceInfluence.LoseLine)
                || (!province.Held && province.Influence >= ProvinceInfluence.HoldLine))
                disagree++;
        }
        ok &= Check(sb, $"live world: every province's hold agrees with its influence ({disagree} do not)", disagree == 0);
        int waiting = 0;
        if (liveCycle.Kingdoms != null)
        {
            foreach (var kv in liveCycle.Kingdoms)
            {
                if (kv.Value != null && kv.Value.PlayerInfluence != kv.Value.PlayerInfluenceDerived)
                    waiting++;
            }
        }
        ok &= Check(sb, "live world: each kingdom's influence is the tile-weighted mean of its provinces",
            DerivedMatches(liveCycle, skipWaiting: true));
        int strayEstates = StrayEstates(liveCycle);
        ok &= Check(sb, $"live world: every courtier's estate is a province of their realm ({strayEstates} are not)",
            strayEstates == 0);

        sb.AppendLine(ok
            ? $"  Live world ({live.Provinces.Count} provinces, {waiting} kingdom(s) with influence still to spread): PASS"
            : "  Live world: FAIL");
        return ok;
    }

    // ── Court helpers ─────────────────────────────────────────────────────
    /// <summary>The influence cycle with courts: the west's of seven courtiers, the east's of
    /// three, and one for a realm with no provinces. Every courtier at Regard 0 and Influence
    /// 1, no court in contact.</summary>
    private static CycleState CourtCycle()
    {
        var cycle = InfluenceCycle(buildProvinces: true);
        cycle.Council = new CouncilState();
        cycle.Council.Courts[West] = Court(West, 7);
        cycle.Council.Courts[East] = Court(East, 3);
        cycle.Council.Courts[Lost] = Court(Lost, 3);
        return cycle;
    }

    private static CourtState Court(string kingdomId, int courtiers)
    {
        var court = new CourtState { KingdomId = kingdomId };
        for (int n = 0; n < courtiers; n++)
        {
            court.Courtiers.Add(new CourtierState
            {
                Id = $"{kingdomId}_courtier_{n}",
                DisplayName = $"Courtier {n}",
                Office = CourtVocab.Offices[n % CourtVocab.Offices.Length],
                Regard = 0,
                Influence = 1,
            });
        }
        return court;
    }

    /// <summary>Put a court in contact at a standing score, by its StandingPenalty, whatever
    /// its courtiers' Regard: -6 Hostile, 0 Received, 3 Welcome, 9 Favored, 16 Trusted.</summary>
    private static void SetScore(CourtState court, int score)
    {
        court.HasContact = true;
        court.StandingPenalty = 0;
        court.StandingPenalty = court.StandingScore() - score;
    }

    /// <summary>A deed at the capital's seat from 0; returns what it gained.</summary>
    private static int DeedAtCapital(CycleState cycle, int capital, int amount)
    {
        var province = cycle.World.Provinces[capital];
        province.Influence = 0;
        province.Held = false;
        ProvinceInfluence.Add(cycle, province.SeatX, province.SeatY, amount);
        return province.Influence;
    }

    /// <summary>An open front of the east against the west, half way.</summary>
    private static Warfront Front(string id) => new Warfront
    {
        Id = id, AggressorKingdomId = East, DefenderKingdomId = West, Advance = 50, FocusCol = 22, FocusRow = 15,
    };

    /// <summary>Courtiers whose estate names no province of their realm. An empty estate is
    /// not counted: the first sync gives it.</summary>
    private static int StrayEstates(CycleState cycle)
    {
        if (cycle?.Council?.Courts == null || cycle.World == null || !cycle.World.HasProvinces())
            return 0;
        var realmOf = new Dictionary<string, string>();
        foreach (var province in cycle.World.Provinces)
            realmOf[province.Id] = province.KingdomId;
        int stray = 0;
        foreach (var kv in cycle.Council.Courts)
        {
            if (kv.Value?.Courtiers == null)
                continue;
            string realm = string.IsNullOrEmpty(kv.Value.KingdomId) ? kv.Key : kv.Value.KingdomId;
            foreach (var courtier in kv.Value.Courtiers)
            {
                if (courtier == null || string.IsNullOrEmpty(courtier.EstateProvinceId))
                    continue;
                if (!realmOf.TryGetValue(courtier.EstateProvinceId, out string owner) || owner != realm)
                    stray++;
            }
        }
        return stray;
    }

    // ── Influence helpers ─────────────────────────────────────────────────
    /// <summary>The scratch world in a cycle: the west kingdom at 25 (the start kingdom's
    /// value), the east at 0, the castle at the west seat city.</summary>
    private static CycleState InfluenceCycle(bool buildProvinces)
    {
        var world = ScratchWorld();
        if (buildProvinces)
            ProvinceBuilder.Build(world, log: false);
        var cycle = new CycleState { World = world, CastleX = 6, CastleY = 15 };
        cycle.Kingdoms = new Dictionary<string, KingdomState>
        {
            [West] = new KingdomState { RegionId = West, DisplayName = "Westmarch", PlayerInfluence = 25 },
            [East] = new KingdomState { RegionId = East, DisplayName = "Eastreach" },
        };
        return cycle;
    }

    /// <summary>True when every kingdom with provinces holds the tile-weighted mean of
    /// their influence, rounded half up, in PlayerInfluence and PlayerInfluenceDerived.
    /// Computed apart from ProvinceInfluence, in floating point. With skipWaiting, a
    /// kingdom whose two values differ (a write still to spread) is passed over.</summary>
    private static bool DerivedMatches(CycleState cycle, bool skipWaiting = false)
    {
        if (cycle?.Kingdoms == null || cycle.World == null)
            return true;
        foreach (var kv in cycle.Kingdoms)
        {
            var kingdom = kv.Value;
            if (kingdom == null)
                continue;
            if (skipWaiting && kingdom.PlayerInfluence != kingdom.PlayerInfluenceDerived)
                continue;
            double sum = 0, tiles = 0;
            foreach (var province in cycle.World.Provinces)
            {
                if (province.KingdomId != kv.Key)
                    continue;
                sum += (double)province.Influence * province.TileCount;
                tiles += province.TileCount;
            }
            if (tiles <= 0)
                continue;
            int mean = (int)System.Math.Floor(sum / tiles + 0.5);
            if (kingdom.PlayerInfluence != mean || kingdom.PlayerInfluenceDerived != mean)
                return false;
        }
        return true;
    }

    private static int Changed(List<Province> provinces, int[] before)
    {
        int changed = 0;
        for (int i = 0; i < provinces.Count; i++)
        {
            if (provinces[i].Influence != before[i])
                changed++;
        }
        return changed;
    }

    // ── The scratch world ────────────────────────────────────────────────
    /// <summary>48 x 32, all lowland grass. Columns 0 to 23 are the west kingdom and 24
    /// to 47 the east. A ridge runs down column 12; a river runs between columns 35 and
    /// 36; woods fill the north-east corner; a three-by-three lake sits at (30..32,
    /// 4..6). A three-by-three west island at (2..4, 27..29) and an eight-by-seven east
    /// island at (39..46, 24..30) are each ringed by sea. Each kingdom has a seat city
    /// (west at (6,15), east at (30,15)); the west has towns at (18,5), (18,25) and
    /// (3,4), the east at (42,6) in the woods and (43,18). Every index is set to -1, as
    /// the generator does.</summary>
    private static WorldData ScratchWorld()
    {
        var world = new WorldData { Width = 48, Height = 32 };
        int w = world.Width, h = world.Height;
        world.Tiles = new WorldTile[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                world.Tiles[i].Terrain = OverworldHex.TerrainType.Grassland;
                world.Tiles[i].Elevation = 0.3f;
                world.Tiles[i].KingdomId = x < 24 ? West : East;
                world.Tiles[i].PoiIndex = -1;
                world.Tiles[i].SettlementIndex = -1;
                world.Tiles[i].ShardZoneIndex = -1;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int i = y * w + 12;
            world.Tiles[i].Elevation = 0.85f;
            world.Tiles[i].Terrain = OverworldHex.TerrainType.Hills;
        }
        for (int y = 0; y < h; y++)
            MarkRiverEast(world, 35, y);
        for (int y = 0; y <= 10; y++)
        {
            for (int x = 36; x < w; x++)
                world.Tiles[y * w + x].Terrain = OverworldHex.TerrainType.Forest;
        }
        for (int y = 4; y <= 6; y++)
        {
            for (int x = 30; x <= 32; x++)
                SetWater(world, x, y, OverworldHex.TerrainType.Lake);
        }
        Island(world, 2, 4, 27, 29);
        Island(world, 39, 46, 24, 30);

        AddSettlement(world, 6, 15, West, SettlementTier.City, true);
        AddSettlement(world, 30, 15, East, SettlementTier.City, true);
        AddSettlement(world, 18, 5, West, SettlementTier.Town, false);
        AddSettlement(world, 18, 25, West, SettlementTier.Town, false);
        AddSettlement(world, 3, 4, West, SettlementTier.Town, false);
        AddSettlement(world, 42, 6, East, SettlementTier.Town, false);
        AddSettlement(world, 43, 18, East, SettlementTier.Town, false);
        return world;
    }

    /// <summary>40 x 30, one kingdom (the west id), lowland grass at elevation 0.3 but for
    /// a nine-by-nine block of mountain (0.9) at (16..24, 10..18) inside a ring of hills
    /// (0.7) at (15..25, 9..19). A seat city at (5,15); towns at (34,5), (34,25), (20,3)
    /// and (20,27). Every index is set to -1, as the generator does.</summary>
    private static WorldData RangeWorld()
    {
        var world = new WorldData { Width = 40, Height = 30 };
        int w = world.Width, h = world.Height;
        world.Tiles = new WorldTile[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                bool hills = x >= 15 && x <= 25 && y >= 9 && y <= 19;
                bool peaks = x >= 16 && x <= 24 && y >= 10 && y <= 18;
                world.Tiles[i].Terrain = peaks ? OverworldHex.TerrainType.Mountain
                    : hills ? OverworldHex.TerrainType.Hills : OverworldHex.TerrainType.Grassland;
                world.Tiles[i].Elevation = peaks ? 0.9f : hills ? 0.7f : 0.3f;
                world.Tiles[i].KingdomId = West;
                world.Tiles[i].PoiIndex = -1;
                world.Tiles[i].SettlementIndex = -1;
                world.Tiles[i].ShardZoneIndex = -1;
            }
        }
        AddSettlement(world, 5, 15, West, SettlementTier.City, true);
        AddSettlement(world, 34, 5, West, SettlementTier.Town, false);
        AddSettlement(world, 34, 25, West, SettlementTier.Town, false);
        AddSettlement(world, 20, 3, West, SettlementTier.Town, false);
        AddSettlement(world, 20, 27, West, SettlementTier.Town, false);
        return world;
    }

    /// <summary>A river along every edge between (x,y) and its neighbours in column x+1,
    /// marked on both tiles as the generator marks rivers.</summary>
    private static void MarkRiverEast(WorldData world, int x, int y)
    {
        var (q, r) = HexCoord.OffsetToAxial(x, y);
        for (int d = 0; d < 6; d++)
        {
            var (dq, dr) = HexCoord.AxialDirections[d];
            var (nx, ny) = HexCoord.AxialToOffset(q + dq, r + dr);
            if (nx != x + 1 || !world.InBounds(nx, ny))
                continue;
            world.Tiles[y * world.Width + x].RiverEdges |= (byte)(1 << d);
            world.Tiles[ny * world.Width + nx].RiverEdges |= (byte)(1 << ((d + 3) % 6));
        }
    }

    private static void SetWater(WorldData world, int x, int y, OverworldHex.TerrainType water)
    {
        int i = y * world.Width + x;
        world.Tiles[i].Terrain = water;
        world.Tiles[i].KingdomId = "";
    }

    /// <summary>Sea on every tile within one step of the block (x0..x1, y0..y1) but
    /// outside it, so the block is an island.</summary>
    private static void Island(WorldData world, int x0, int x1, int y0, int y1)
    {
        for (int y = y0 - 1; y <= y1 + 1; y++)
        {
            for (int x = x0 - 1; x <= x1 + 1; x++)
            {
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                    continue;
                SetWater(world, x, y, OverworldHex.TerrainType.Water);
            }
        }
    }

    /// <summary>A settlement whose tiles point at it, as a generated footprint does: a
    /// city covers its centre and the six around it, a town its centre.</summary>
    private static void AddSettlement(WorldData world, int x, int y, string kingdom, SettlementTier tier, bool seat)
    {
        var settlement = new WorldSettlement
        {
            Tier = tier,
            CenterX = x,
            CenterY = y,
            KingdomId = kingdom,
            IsSeat = seat,
            GrantsStaging = tier == SettlementTier.City,
        };
        settlement.Tiles.Add((x, y));
        if (tier == SettlementTier.City)
        {
            var (q, r) = HexCoord.OffsetToAxial(x, y);
            foreach (var (dq, dr) in HexCoord.AxialDirections)
            {
                var (nx, ny) = HexCoord.AxialToOffset(q + dq, r + dr);
                if (world.InBounds(nx, ny))
                    settlement.Tiles.Add((nx, ny));
            }
        }
        world.Settlements.Add(settlement);
        int index = world.Settlements.Count - 1;
        foreach (var (tx, ty) in settlement.Tiles)
            world.Tiles[ty * world.Width + tx].SettlementIndex = index;
    }

    private static bool SameProvince(Province a, Province b)
        => a != null && b != null
           && a.Index == b.Index && a.Id == b.Id && a.Name == b.Name && a.KingdomId == b.KingdomId
           && a.SeatX == b.SeatX && a.SeatY == b.SeatY && a.SeatSettlementIndex == b.SeatSettlementIndex
           && a.IsCapital == b.IsCapital && a.TileCount == b.TileCount && a.Character == b.Character
           && a.Tone == b.Tone && a.Influence == b.Influence && a.Held == b.Held
           && a.Settlements != null && b.Settlements != null && a.Settlements.SequenceEqual(b.Settlements)
           && a.Neighbors != null && b.Neighbors != null && a.Neighbors.SequenceEqual(b.Neighbors);

    private static T RoundTrip<T>(T obj)
    {
        string json = JsonSerializer.Serialize(obj, SaveManager.JsonOptions);
        return JsonSerializer.Deserialize<T>(json, SaveManager.JsonOptions);
    }

    private static bool Check(StringBuilder sb, string field, bool equal)
    {
        if (!equal)
        {
            sb.AppendLine($"    FAIL: {field} did not hold.");
        }
        return equal;
    }
}
