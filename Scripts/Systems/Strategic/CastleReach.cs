using Godot;
using System.Collections.Generic;

// ============================================================
// CastleReach.cs
//
// Purpose:        The party works from the castle's province (docs/
//                 provinces_v1_11_small_places.md, slice PV5, X2). The
//                 castle's reach is the font's: its province, the ones
//                 beside it, and the ones beside a forward camp. Within
//                 it a party walks the castle's roads, a tile a day
//                 instead of FieldMarch.DaysPerTile (PR82); the forward
//                 camp is a waystone a party can step through to (PR83).
//                 And the castle surveys at province scale (PR80, PR81):
//                 wherever it comes to stand, and at each new moon, its
//                 province and the ones beside it are Charted, and the
//                 places in its own province are Sighted. The stride is
//                 not retired (PR84): the Resolve-from-Camp A/B has not
//                 run.
// Layer:          System (strategic)
// Collaborators:  CastleManeuvers.cs (Reach, the forward camp; OnMoved
//                 and NewMoon call Survey), FieldMarch.cs (DaysTo,
//                 StepDay and IsWaystone read the roads and the forward
//                 camp; StepToward), StrategicView.cs (the party's
//                 notice and confirm), ProvinceSaveAssert.Reach.cs
// See:            docs/provinces_v1_11_small_places.md
// ============================================================

/// <summary>The castle's reach for the party, and its survey (PV5).</summary>
public static class CastleReach
{
    /// <summary>Days a tile on the castle's roads, within its reach (PR82).</summary>
    public const int RoadDaysPerTile = 1;

    /// <summary>True when the tile lies in a province within the castle's reach.</summary>
    public static bool InReach(CycleState cycle, int x, int y)
    {
        var world = cycle?.World;
        if (world == null || !world.HasProvinces())
            return false;
        int province = world.ProvinceIndexAt(x, y);
        return province >= 0 && CastleManeuvers.Reach(cycle).Contains(province);
    }

    /// <summary>Days a step from this tile takes on foot: a tile a day within the castle's
    /// reach, FieldMarch.DaysPerTile beyond it.</summary>
    public static int DaysPerTileAt(CycleState cycle, int x, int y)
        => InReach(cycle, x, y) ? RoadDaysPerTile : FieldMarch.DaysPerTile;

    /// <summary>Days on foot from one tile to another along the line a party walks: each step
    /// at the pace of the tile it leaves.</summary>
    public static int DaysAlong(CycleState cycle, int fromX, int fromY, int toX, int toY)
    {
        var world = cycle?.World;
        if (world == null)
            return 0;
        if (!world.HasProvinces())
            return world.HexDistance(fromX, fromY, toX, toY) * FieldMarch.DaysPerTile;
        var reach = CastleManeuvers.Reach(cycle);
        int days = 0, x = fromX, y = fromY;
        int guard = world.HexDistance(fromX, fromY, toX, toY) * 3 + 8;   // FM1: the way round water is longer than the line
        while ((x != toX || y != toY) && guard-- > 0)
        {
            int province = world.ProvinceIndexAt(x, y);
            days += province >= 0 && reach.Contains(province) ? RoadDaysPerTile : FieldMarch.DaysPerTile;
            (x, y) = FieldMarch.StepToward(world, x, y, toX, toY, 1);
        }
        return days;
    }

    /// <summary>True on the forward camp's tile while it stands (Deploy Waystation).</summary>
    public static bool IsForwardCamp(CycleState cycle, int x, int y)
    {
        var state = cycle?.Maneuvers;
        return state != null && !string.IsNullOrEmpty(state.WaystationProvinceId)
               && state.WaystationX == x && state.WaystationY == y;
    }

    /// <summary>The castle surveys from where it stands (PR80, PR81): every unseen tile of its
    /// province and the ones beside it is Charted, and every place of its own province not yet
    /// on the map is Sighted, as a held province's places are (PR47): WorldPoi.Discovered, never
    /// a Veiled Concord node, nothing under Classic Exploration. Returns a line of news when it
    /// found something, else "".</summary>
    public static string Survey(CycleState cycle)
    {
        var world = cycle?.World;
        int castle = CastleHops.CastleProvince(cycle);
        if (world == null || castle < 0 || PlayerSession.DebugClassicExploration)
            return "";
        var ring = new HashSet<int> { castle };
        foreach (int q in world.Provinces[castle].Neighbors)
        {
            if (q >= 0 && q < world.Provinces.Count)
                ring.Add(q);
        }
        int charted = 0;
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            if (world.Tiles[i].Discovery != TileDiscovery.Unseen)
                continue;
            int p = world.ProvinceIndexAt(i % world.Width, i / world.Width);
            if (p < 0 || !ring.Contains(p))
                continue;
            world.Tiles[i].Discovery = TileDiscovery.Charted;
            charted++;
        }
        int sighted = 0;
        if (world.Pois != null)
        {
            foreach (var poi in world.Pois)
            {
                if (poi == null || poi.Discovered || poi.Kind == PoiKind.Concord || !world.InBounds(poi.X, poi.Y)
                    || world.ProvinceIndexAt(poi.X, poi.Y) != castle)
                {
                    continue;
                }
                poi.Discovered = true;
                sighted++;
            }
        }
        // Provinces PV8a: the survey runs down the rumors that point into the castle's province.
        // A true one's place was sighted above and holds true at the next prune; a false one is
        // searched at its center and comes to nothing, with its own notes.
        ProvinceLeads.SettleProvince(cycle, castle);
        if (charted == 0 && sighted == 0)
            return "";
        string name = ProvinceControl.Name(world.Provinces[castle]);
        string line = $"The castle's lens surveys {name} and the provinces beside it"
                      + (charted > 0 ? $": {charted} tile(s) newly charted" : "")
                      + (sighted > 0 ? $"{(charted > 0 ? "," : ":")} {sighted} place(s) sighted in {name}" : "")
                      + ".";
        GD.Print($"[CastleReach] {line}");
        return line;
    }
}
