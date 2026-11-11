using System.Linq;
using System.Text;

// ============================================================
// ProvinceSaveAssert.Reach.cs  (partial of ProvinceSaveAssert)
//
// Purpose:        The asserts of slice PV5, the party working from the
//                 castle's province (docs/provinces_v1_11_small_places.md
//                 section 6), on the scratch world with the castle at the
//                 west capital: the castle's roads (a tile a day within
//                 its reach, four beyond); the forward camp as a waystone;
//                 the survey (the castle's province and the ones beside it
//                 charted, the places of its own province sighted), and a
//                 march that surveys where it ends.
// Layer:          Verification
// Collaborators:  ProvinceSaveAssert.cs (AssertAll calls AssertReach;
//                 Check), ProvinceSaveAssert.Bastion.cs (BastionCycle),
//                 CastleReach.cs, FieldMarch.cs, CastleManeuvers.cs,
//                 CastleMarch.cs
// See:            docs/provinces_v1_11_small_places.md section 6
// ============================================================

public static partial class ProvinceSaveAssert
{
    /// <summary>Every PV5 assert.</summary>
    private static bool AssertReach(StringBuilder sb)
    {
        bool ok = AssertCastleRoads(sb);
        ok &= AssertSurvey(sb);
        return ok;
    }

    /// <summary>The castle's roads and the forward camp (PR82, PR83).</summary>
    private static bool AssertCastleRoads(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        bool ok = Check(sb, "a tile a day on the castle's roads (its province and the ones beside it), four beyond",
            CastleReach.DaysPerTileAt(cycle, 6, 16) == CastleReach.RoadDaysPerTile
            && CastleReach.DaysPerTileAt(cycle, 18, 8) == CastleReach.RoadDaysPerTile
            && CastleReach.DaysPerTileAt(cycle, 30, 15) == FieldMarch.DaysPerTile);
        var party = new FieldParty { Id = "field_assert", Name = "Assert Party", X = 6, Y = 15 };
        cycle.FieldParties ??= new System.Collections.Generic.List<FieldParty>();
        cycle.FieldParties.Add(party);
        int near = world.HexDistance(6, 15, 4, 16);
        int far = world.HexDistance(6, 15, 30, 15);
        int nearDays = FieldMarch.DaysTo(cycle, party, 4, 16);
        int farDays = FieldMarch.DaysTo(cycle, party, 30, 15);
        ok &= Check(sb, $"within the castle's province a walk takes a day a tile ({near} tile(s), {nearDays} day(s)); a walk that leaves the reach takes longer ({far} tile(s), {farDays} day(s))",
            nearDays == near && farDays >= far && farDays < far * FieldMarch.DaysPerTile);   // FM2: by terrain

        cycle.Grimoire.KnownSpellIds.Add("deploy_waystation");
        int south = world.ProvinceIndexAt(18, 25);
        bool camped = CastleManeuvers.Cast(cycle, "deploy_waystation", south.ToString(), out _, out _);
        int wx = cycle.Maneuvers.WaystationX, wy = cycle.Maneuvers.WaystationY;
        bool waystone = FieldMarch.IsWaystone(cycle, wx, wy, out string what);
        bool canStep = FieldMarch.CanTeleportTo(cycle, party, wx, wy, out string why);
        ok &= Check(sb, $"the forward camp is a waystone a party at the castle can step through to ({what}{why})",
            camped && waystone && what == "the forward camp" && canStep);

        sb.AppendLine(ok ? "  Castle roads: PASS" : "  Castle roads: FAIL");
        return ok;
    }

    /// <summary>The survey (PR80, PR81): with the castle in Southeast Fields and the map unseen,
    /// that province and the ones beside it are charted and its places sighted, while Northeast
    /// Weald and its places stay unseen; and a march into Southeast Fields surveys it.</summary>
    private static bool AssertSurvey(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int south = world.ProvinceIndexAt(18, 25), eastSouth = world.ProvinceIndexAt(30, 27);
        int weald = world.ProvinceIndexAt(40, 8);
        if (south < 0 || eastSouth < 0 || weald < 0)
        {
            sb.AppendLine("  Survey: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        for (int i = 0; i < world.Tiles.Length; i++)
            world.Tiles[i].Discovery = TileDiscovery.Unseen;
        int inSouth = AddUnseenPlace(world, 22, 28);
        int inWeald = AddUnseenPlace(world, 40, 9);
        cycle.CastleX = 18;
        cycle.CastleY = 26;
        string line = CastleReach.Survey(cycle);
        bool ringCharted = Enumerable.Range(0, world.Tiles.Length).All(i =>
        {
            int p = world.ProvinceIndexAt(i % world.Width, i / world.Width);
            bool inRing = p == south || world.Provinces[south].Neighbors.Contains(p);
            return p < 0 || !inRing || world.Tiles[i].Discovery != TileDiscovery.Unseen;
        });
        bool ok = Check(sb, $"the castle's province and the ones beside it are charted, its places sighted ({line})",
            line.Contains("surveys Southeast Fields") && ringCharted && world.Pois[inSouth].Discovered);
        ok &= Check(sb, "Northeast Weald, beyond the ring, stays unseen with its places",
            world.Tiles[8 * world.Width + 40].Discovery == TileDiscovery.Unseen && !world.Pois[inWeald].Discovered);

        var marched = BastionCycle("Adept");
        var mworld = marched.World;
        for (int i = 0; i < mworld.Tiles.Length; i++)
            mworld.Tiles[i].Discovery = TileDiscovery.Unseen;
        mworld.Tiles[26 * mworld.Width + 18].Discovery = TileDiscovery.Charted;
        CastleMarch.Execute(marched, 18, 26, "The Bastion Errant");
        ok &= Check(sb, "a march into Southeast Fields surveys it: the east's South Fields beside it is charted",
            marched.CastleX == 18 && marched.CastleY == 26
            && mworld.Tiles[27 * mworld.Width + 30].Discovery != TileDiscovery.Unseen
            && marched.PendingSiegeReports.Any(l => l.Contains("surveys Southeast Fields")));

        sb.AppendLine(ok ? "  Survey: PASS" : "  Survey: FAIL");
        return ok;
    }

    /// <summary>A place no one has seen at (x,y). Returns its index.</summary>
    private static int AddUnseenPlace(WorldData world, int x, int y)
    {
        world.Pois.Add(new WorldPoi { X = x, Y = y, Kind = PoiKind.Combat, Discovered = false });
        int index = world.Pois.Count - 1;
        world.Tiles[y * world.Width + x].PoiIndex = index;
        return index;
    }
}
