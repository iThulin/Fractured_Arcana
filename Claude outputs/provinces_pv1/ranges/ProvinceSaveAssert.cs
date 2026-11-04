using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

// ============================================================
// ProvinceSaveAssert.cs
//
// Purpose:        Save round-trip and build assertions for provinces
//                 (docs/provinces_v1.md, slice PV1): a Province with
//                 every field set; a built world through the save
//                 serializer (WorldData.Provinces and ProvinceMap);
//                 the additive defaults on legacy JSON; the partition
//                 of a scratch world (two kingdoms, a ridge, a river,
//                 a lake, a small island and a large one); a big range
//                 becoming a province of its own; determinism;
//                 the legacy build; land no kingdom owns (water opened
//                 into marsh in play); and, read-only, the checks on
//                 the live world when it has provinces. Same serializer
//                 and contract as LeadsSaveAssert.
// Layer:          Verification
// Collaborators:  Province.cs, ProvinceBuilder.cs, WorldData.cs,
//                 SaveManager.cs (JsonOptions), CampusGuildPanel.cs
//                 (the "Assert Round-Trips" debug button)
// ============================================================

public static class ProvinceSaveAssert
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
        ok &= AssertLiveWorld(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Provinces and the province map round-trip clean, legacy worlds load empty and build once, and the partition keeps its rules."
            : "RESULT: FAILURES ABOVE. A province field is being dropped or renamed, or a partition rule broke.");
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
            && sparse.Character == ProvinceCharacter.Lowland && !sparse.IsCapital);

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
    /// map is as it would have been; opened after the build, it is in no province and
    /// every rule still holds.</summary>
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

        sb.AppendLine(ok ? "  Opened land: PASS" : "  Opened land: FAIL");
        return ok;
    }

    /// <summary>The live world's provinces against every rule. Read-only: a world
    /// without provinces is skipped, never built here.</summary>
    private static bool AssertLiveWorld(StringBuilder sb)
    {
        var live = SaveManager.ActiveSave?.Cycle?.World;
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
        sb.AppendLine(ok ? $"  Live world ({live.Provinces.Count} provinces): PASS" : "  Live world: FAIL");
        return ok;
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
           && a.Tone == b.Tone
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
