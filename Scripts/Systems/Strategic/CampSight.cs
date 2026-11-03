using Godot;
using System.Collections.Generic;

// ============================================================
// CampSight.cs
//
// Purpose:        The knowledge ladder's camp sighting
//                 (docs/knowledge_ladder_spec_v1.md, section 4.1,
//                 build step L2). Wherever the castle makes camp, its
//                 lookouts mark every undiscovered place in sight: the
//                 place becomes Sighted (WorldPoi.Discovered) and its
//                 tile is charted. One desk note and one line of
//                 frontier news name what was seen, nearest first.
// Layer:          System (strategic; static, no nodes)
// Collaborators:  ExpeditionAnchors.ParkCastleAt (the one hook, for
//                 every camp: a march's arrival, a sortie's end, a
//                 banked furnace), LeadService (a lead on a sighted
//                 place resolves), ScryInbox (the desk),
//                 CycleState.PendingSiegeReports (the map's frontier
//                 news), CrewStations (the Lens Room), WorldAtlas3D
//                 (the unvisited ring, through SightedUnvisited),
//                 ExpeditionManager.StampSighted (L3: the field's
//                 beacons, through FieldBeacon), LeadService (L4a: a
//                 true lead's note names the place, PlacePhrase).
//
// Sighting from camp never grants staging. Staging stays contact by
// foot: ExpeditionManager.WriteVisibleToWorld grants it on every
// revealed settlement tile, sighted first or not. Concord nodes are
// never sighted, because reaching one is the espionage layer's first
// contact. Off under the Classic Exploration debug switch, which is
// the A/B baseline.
// ============================================================

/// <summary>What a camp's lookouts see, and what they report.</summary>
public static class CampSight
{
    /// <summary>Base sight from camp, in tiles (ruling C1). Tune it from the
    /// [CampSight] log line.</summary>
    public const int CampSightRadius = 8;

    /// <summary>Added while a suited, steady hand works the Lens Room: the same
    /// test that lands the sortie's scry bonus (CrewStations.Compute).</summary>
    public const int LensRoomBonus = 1;

    /// <summary>The most places the frontier line names. The desk note names
    /// every one.</summary>
    private const int FrontierLineMax = 3;

    private const string RingLine =
        "Places in the wild are ringed on your map until someone goes to look.";

    private static readonly string[] Compass =
    {
        "east", "northeast", "north", "northwest", "west", "southwest", "south", "southeast",
    };

    /// <summary>One line of the report: a wild place, or a settlement new to the
    /// map.</summary>
    private sealed class Sighting
    {
        public string What = "";
        public int X;
        public int Y;
        public int Distance;
    }

    /// <summary>Look out from a camp at (x, y). Every undiscovered place within
    /// the sight radius becomes Sighted and its tile charted (never downgraded);
    /// leads on those places resolve; the desk and the frontier news list what
    /// was seen. Returns how many tiles held newly sighted places.</summary>
    public static int Sight(CycleState cycle, int x, int y, string castleName)
    {
        var world = cycle?.World;
        if (world?.Pois == null || !world.InBounds(x, y))
        {
            return 0;
        }
        if (PlayerSession.DebugClassicExploration)
        {
            GD.Print($"[CampSight] off at ({x},{y}): Classic Exploration is on.");
            return 0;
        }

        int ground = HighGroundBonus(world, x, y);
        int lens = LensRoomManned(cycle) ? LensRoomBonus : 0;
        int radius = CampSightRadius + ground + lens;
        var settlements = world.Settlements ?? new List<WorldSettlement>();

        // Pass 1 reads only: the places in sight, and whether each settlement
        // they stand in was on the map before this camp. A town the map already
        // shows is not announced again when more of its streets come into view.
        var inSight = new List<WorldPoi>();
        var settlementOnMap = new Dictionary<int, bool>();
        foreach (var poi in world.Pois)
        {
            if (poi == null || poi.Discovered || poi.Kind == PoiKind.Concord
                || !world.InBounds(poi.X, poi.Y)
                || world.HexDistance(x, y, poi.X, poi.Y) > radius)
            {
                continue;
            }
            inSight.Add(poi);
            int si = world.GetTile(poi.X, poi.Y).SettlementIndex;
            if (si >= 0 && si < settlements.Count && !settlementOnMap.ContainsKey(si))
            {
                settlementOnMap[si] = OnMap(world, settlements[si]);
            }
        }

        // Pass 2 writes. Sighted is WorldPoi.Discovered, and the tile is charted
        // the way SupplyCacheSystem.Discover does it.
        var sightings = new List<Sighting>();
        var markedTiles = new HashSet<(int, int)>();   // worldgen can stack places on one tile
        var listedSettlements = new HashSet<int>();
        int wild = 0;
        foreach (var poi in inSight)
        {
            poi.Discovered = true;
            if (world.TryIndex(poi.X, poi.Y, out int idx)
                && world.Tiles[idx].Discovery == TileDiscovery.Unseen)
            {
                world.Tiles[idx].Discovery = TileDiscovery.Charted;
            }
            if (!markedTiles.Add((poi.X, poi.Y)))
            {
                continue;
            }

            int si = world.GetTile(poi.X, poi.Y).SettlementIndex;
            if (si >= 0 && si < settlements.Count)
            {
                var s = settlements[si];
                bool wasOnMap = settlementOnMap.TryGetValue(si, out bool onMap) && onMap;
                if (s == null || s.IsGuildHome || wasOnMap || !listedSettlements.Add(si))
                {
                    continue;
                }
                sightings.Add(new Sighting
                {
                    What = SettlementPhrase(world, s),
                    X = s.CenterX,
                    Y = s.CenterY,
                    Distance = world.HexDistance(x, y, s.CenterX, s.CenterY),
                });
                continue;
            }

            wild++;
            sightings.Add(new Sighting
            {
                What = PlacePhrase(world, poi),
                X = poi.X,
                Y = poi.Y,
                Distance = world.HexDistance(x, y, poi.X, poi.Y),
            });
        }

        int resolved = LeadService.Prune(cycle);
        if (markedTiles.Count > 0 || resolved > 0)
        {
            SaveManager.MarkDirty();
        }

        if (sightings.Count > 0)
        {
            sightings.Sort(Nearest);
            Report(cycle, x, y, castleName, sightings, wild > 0);
        }

        GD.Print($"[CampSight] radius {radius} sighted {sightings.Count} at ({x},{y}): "
                 + $"{markedTiles.Count} tile(s) of places marked, {wild} in the wild; "
                 + $"high ground +{ground}, Lens Room +{lens}; {resolved} lead(s) resolved.");
        return markedTiles.Count;
    }

    /// <summary>The camp tile's high ground, on the same terms as a sortie's sight
    /// (FogOfWarManager): Hills +1, Mountain +2.</summary>
    public static int HighGroundBonus(WorldData world, int x, int y)
    {
        if (world == null || !world.TryIndex(x, y, out int idx) || idx >= world.Tiles.Length)
        {
            return 0;
        }
        return world.Tiles[idx].Terrain switch
        {
            OverworldHex.TerrainType.Mountain => FogOfWarManager.MountainSightBonus,
            OverworldHex.TerrainType.Hills => FogOfWarManager.HillsSightBonus,
            _ => 0,
        };
    }

    /// <summary>True while a crew member suited to the Lens Room, and not Wary,
    /// works it: the condition that lands the sortie's scry bonus.</summary>
    public static bool LensRoomManned(CycleState cycle)
    {
        if (cycle == null)
        {
            return false;
        }
        var stations = CrewStations.AutoAssign(ExpeditionAnchors.Crew(cycle));
        return CrewStations.Compute(stations).BonusScry > 0;
    }

    /// <summary>The ladder's "Sighted, unvisited" (spec section 8): a place in the
    /// wild that is on the map, not yet Known, and not resolved. Only the kinds a
    /// party goes to in person count; settlements, seats, caches and Concord nodes
    /// have other faces on the map. The atlas rings these places; the Leads card
    /// (L5) will list them.</summary>
    public static bool SightedUnvisited(WorldData world, WorldPoi poi)
    {
        if (world == null || poi == null || !poi.Discovered || poi.Known || poi.Consumed)
        {
            return false;
        }
        return WildPlace(world, poi);
    }

    /// <summary>Knowledge ladder L3 (spec sections 5 and 13, D175): a place that
    /// stands in a sortie window as a beacon, even under fog. Sighted, not resolved,
    /// and a wild place. Known places count too: knowing more about a place is no
    /// reason to hide it in the field. ExpeditionManager.StampSighted marks and
    /// reveals these.</summary>
    public static bool FieldBeacon(WorldData world, WorldPoi poi)
        => poi != null && poi.Discovered && !poi.Consumed && WildPlace(world, poi);

    /// <summary>A place in the wild of a kind a party goes to in person (D176): the
    /// places the ladder rings, sets as beacons and rumors. A place on a settlement's
    /// tile is part of the settlement, and settlements, seats, caches and Concord
    /// nodes have other faces on the map.</summary>
    public static bool WildPlace(WorldData world, WorldPoi poi)
    {
        if (world == null || poi == null || !VisitedInPerson(poi.Kind) || !world.InBounds(poi.X, poi.Y))
        {
            return false;
        }
        return world.GetTile(poi.X, poi.Y).SettlementIndex < 0;
    }

    private static bool VisitedInPerson(PoiKind kind) => kind switch
    {
        PoiKind.Combat or PoiKind.Rest or PoiKind.Narrative or PoiKind.Negotiation
            or PoiKind.Outpost or PoiKind.Prison or PoiKind.Companion => true,
        _ => false,
    };

    /// <summary>One desk note naming every place, and one line of frontier news
    /// naming the nearest few, so the camp's sightings read at the stone and on
    /// the map.</summary>
    private static void Report(CycleState cycle, int x, int y, string castleName,
                               List<Sighting> sightings, bool anyWild)
    {
        string name = string.IsNullOrEmpty(castleName) ? "The castle" : castleName;
        var lines = new List<string>();
        foreach (var s in sightings)
        {
            lines.Add($"{s.What}, {Where(x, y, s)}");
        }

        string count = lines.Count == 1 ? "one new place" : $"{lines.Count} new places";
        string body = $"From the walls, the lookouts mark {count}:\n- " + string.Join("\n- ", lines)
                      + (anyWild ? "\n" + RingLine : "");
        ScryInbox.Post(cycle, ScryChannel.Note, $"{name}: seen from camp", body, "", "campsight");

        int shown = System.Math.Min(lines.Count, FrontierLineMax);
        string listed = lines.Count == 1
            ? lines[0]
            : $"{lines.Count} new places: " + JoinPlaces(lines.GetRange(0, shown), lines.Count - shown);
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add($"From the camp, the lookouts mark {listed}."
                                      + (anyWild ? " " + RingLine : ""));
    }

    /// <summary>"A; B; and C". Each place already carries a comma ("a town, 6
    /// tiles to the west"), so the places are parted with semicolons.</summary>
    private static string JoinPlaces(List<string> places, int more)
    {
        var parts = new List<string>(places);
        if (more > 0)
        {
            parts.Add($"{more} more");
        }
        if (parts.Count == 1)
        {
            return parts[0];
        }
        return string.Join("; ", parts.GetRange(0, parts.Count - 1)) + "; and " + parts[parts.Count - 1];
    }

    private static string Where(int x, int y, Sighting s)
    {
        if (s.Distance <= 0)
        {
            return "beside the camp";
        }
        string tiles = s.Distance == 1 ? "1 tile" : $"{s.Distance} tiles";
        string bearing = Bearing(x, y, s.X, s.Y);
        return bearing.Length == 0 ? $"{tiles} off" : $"{tiles} to the {bearing}";
    }

    /// <summary>The compass word from one tile toward another. The world is
    /// flat-top odd-q (WorldData): columns sit 1.5 apart, rows sqrt(3) apart, and
    /// odd columns half a row lower (WorldAtlas3D.TileOrigin). Row 0 is north, the
    /// top of the map in the atlas's default view.</summary>
    public static string Bearing(int fromX, int fromY, int toX, int toY)   // also the Next line's (O1b)
    {
        double east = (toX - fromX) * 1.5;
        double fromRow = fromY + ((fromX & 1) == 1 ? 0.5 : 0.0);
        double toRow = toY + ((toX & 1) == 1 ? 0.5 : 0.0);
        double south = (toRow - fromRow) * System.Math.Sqrt(3.0);
        if (System.Math.Abs(east) < 1e-6 && System.Math.Abs(south) < 1e-6)
        {
            return "";
        }
        double degrees = System.Math.Atan2(-south, east) * 180.0 / System.Math.PI;   // 0 east, 90 north
        int sector = ((int)System.Math.Round(degrees / 45.0) % 8 + 8) % 8;
        return Compass[sector];
    }

    private static int Nearest(Sighting a, Sighting b)
    {
        if (a.Distance != b.Distance)
        {
            return a.Distance.CompareTo(b.Distance);
        }
        return a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
    }

    /// <summary>What the lookouts can say of a wild place from afar: its kind,
    /// never its contents (that is Known). A narrative site reads as its signal,
    /// so it stays an anomaly worth the steps (PoiSignal). The leads use it too:
    /// a true lead's note, and the debug lever's line (L4a). The Academy's field
    /// store goes by its name (Standing Orders O3, D191).</summary>
    public static string PlacePhrase(WorldData world, WorldPoi poi)
        => IntervalStore.IsStore(SaveManager.ActiveSave?.Cycle, world, poi) ? IntervalStore.StoreName : KindPhrase(world, poi);

    /// <summary>The phrase for the place's kind alone.</summary>
    private static string KindPhrase(WorldData world, WorldPoi poi) => poi.Kind switch
    {
        PoiKind.Combat => "a hostile encampment",
        PoiKind.Rest => "a refuge",
        PoiKind.Narrative => PoiSignal.Signal(world.GetTile(poi.X, poi.Y).Terrain, new Vector2I(poi.X, poi.Y)),
        PoiKind.Negotiation => "a meeting place",
        PoiKind.Outpost => "an outpost",
        PoiKind.Prison => "a prison",
        PoiKind.Companion => "someone in trouble",
        PoiKind.SupplyCache => "a supply cache",
        PoiKind.Seat or PoiKind.Convergence => "a seat of power",
        PoiKind.Settlement => "a settlement",
        _ => "something worth a look",
    };

    private static string SettlementPhrase(WorldData world, WorldSettlement s)
    {
        var center = world.PoiAt(s.CenterX, s.CenterY);
        bool seat = s.IsSeat
            || (center != null && (center.Kind == PoiKind.Seat || center.Kind == PoiKind.Convergence));
        if (seat)
        {
            return "a seat of power";
        }
        return s.Tier == SettlementTier.City ? "a city" : "a town";
    }

    /// <summary>True when any of a settlement's tiles is charted or explored, so the
    /// atlas already draws it (WorldAtlas3D.SettlementVisible).</summary>
    private static bool OnMap(WorldData world, WorldSettlement s)
    {
        if (s?.Tiles == null)
        {
            return false;
        }
        foreach (var (tx, ty) in s.Tiles)
        {
            if (world.InBounds(tx, ty) && world.GetTile(tx, ty).Discovery != TileDiscovery.Unseen)
            {
                return true;
            }
        }
        return false;
    }
}
