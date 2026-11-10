using Godot;
using System.Collections.Generic;

// ============================================================
// Interval.cs
//
// Purpose:        The interval's rules (Standing Orders O1a,
//                 docs/standing_orders_o1_interval_rules_v1.md): the
//                 weeks between the Sundering and the first new moon,
//                 lived once. While the interval walks, the moon is held
//                 (no order costs days and no day passes), and until the
//                 door is set the home is shut. This file holds the
//                 predicates every gate reads, the placement of the
//                 route and the castle, the walk's start and end, and
//                 the held-date check.
// Layer:          System (tutorial; static, no nodes)
// Collaborators:  IntervalState.cs (the save). The gates that read it:
//                 StrategicView (the landing, Pass time, the straggle
//                 debt, the lunation tick, the castle's sortie, the
//                 home buttons), StrategicView.Dispatch (the time bar),
//                 StrategicView.Tutorial (the home's rooms),
//                 ExpeditionManager (a sortie's days, the recall, the
//                 seat), ExpeditionAnchors (camps, the refuel),
//                 CompanionInjurySystem, FieldMarch, FieldPostings,
//                 CastleMarch, WorldClock, WorldAtlas3D (the home's
//                 entry and marker, the dock's beacon), HudManager (the
//                 top bar's date). StandingOrders.cs walks the quest
//                 over it (O1b), and IntervalStore.cs holds chapter
//                 II's store (O3). CampusGuildPanel holds the debug
//                 buttons.
// ============================================================

/// <summary>The interval's rules: the predicates, the placement, the walk's start and end.</summary>
public static class Interval
{
    // ── The lines the gates say (placeholders for the copy pass) ─────────
    public const string HeldLine = "Time does not pass until you finish founding the guild (the Standing Orders quest).";
    public const string DoorLine = "Not available until you finish founding the guild. Follow the Standing Orders quest.";
    /// <summary>The date while the moon is held, on the map's time bar and the top bar (D164).</summary>
    public const string DateLine = "Before the first new moon";
    public const string CastleSortieLine =
        "Until you finish founding the guild, the castle can march but cannot go on an expedition itself. Send your party out from the castle's camp instead.";
    public const string FootLine =
        "Until you finish founding the guild, your party cannot march across the map. "
        + "Send it out from the castle's camp instead.";
    public const string PostingLine =
        "Postings are not available until you finish founding the guild.";
    public const string RecallLine =
        "Your party returns to the castle. No time passes.";
    // Why the world is held (O3c, D208): until the guild first sits, at the first new moon, the only
    // business in order is founding it, and the Register will not risk the founding on anything else.
    // A house that does business before it sits is no house, and only a house's orders stand. The
    // running world runs; it is the guild that waits. These lines say so where the player meets the
    // hold (D209).

    /// <summary>Said when a dive arrives on a place the interval holds (O3b, D198) and no line for
    /// its kind applies (HeldLineFor).</summary>
    public const string PlaceHeldLine = "'Out of order,' says the Register. 'Entered for the first sitting.'" + HeldPlain;
    /// <summary>The Register explains the held world the first time it matters (O3b, D202). Renamed
    /// from interval.held in O3c, so a guild that saw the old explanation sees this one.</summary>
    public const string HeldBark = "interval.out_of_order";
    /// <summary>The plain sentence under every held place's line (voice first, then this).</summary>
    public const string HeldPlain =
        "\n\nYou cannot use this place until you finish founding the guild (the Standing Orders quest). It will still be here afterward.";
    /// <summary>The held kinds that are not places (O3c): a shard zone's gate or sanctum, and a
    /// blight site. Places are held by their POIType's name.</summary>
    public const string HeldShard = "shard";
    public const string HeldFacet = "facet";
    /// <summary>A held place's word in the field's tooltip (O3c, D209).</summary>
    public const string HeldTip = " (available after you found the guild)";
    /// <summary>The stride's halt on a held place (O3c, D209): the place is named, then this.</summary>
    public const string HeldSightTail = "Not available until you finish founding the guild. Keep going.";
    /// <summary>The banner of a fight lost while the interval walks (O3c, D209): what D147, D195 and
    /// D206 do, in the Register's words.</summary>
    public const string RoutLine =
        "You lost the fight. Until the guild is founded, losing costs you nothing: no Anchor, no injuries. "
        + "The place is still there.";

    /// <summary>What a dive is told on arriving at a held place (O3c, D209), by the kind
    /// ExpeditionManager found there: a POIType's name, HeldShard or HeldFacet.</summary>
    public static string HeldLineFor(string kind) => kind switch
    {
        nameof(OverworldHex.POIType.Combat) =>
            "Whoever holds this place would fight for it. The Register closes on your thumb. "
            + "'Out of order,' it says. 'Entered for the first sitting.'" + HeldPlain,
        nameof(OverworldHex.POIType.Rest) =>
            "A refuge, and someone keeping it. 'Sessional business,' says the Register. "
            + "'It will keep for the first sitting. Refuges do.'" + HeldPlain,
        nameof(OverworldHex.POIType.Narrative) =>
            "Something here is waiting to be told. The Register turns its page face down. "
            + "'Entered for the first sitting. The Register does not... read ahead.'" + HeldPlain,
        nameof(OverworldHex.POIType.Negotiation) =>
            "Someone at the meeting place looks up, ready to deal. 'A guild that has not sat cannot "
            + "treat,' says the Register, 'and this one has not sat.'" + HeldPlain,
        nameof(OverworldHex.POIType.Outpost) =>
            "An outpost, held for whoever takes it. 'The house holds nothing before it sits,' says the "
            + "Register. 'Not even this.'" + HeldPlain,
        nameof(OverworldHex.POIType.Prison) =>
            "A jail, and someone in it. 'Entered first in the order of business,' says the Register, "
            + "very carefully, 'for the first sitting.'" + HeldPlain,
        HeldShard =>
            "The air here tastes of the hall, the afternoon it broke. The Register goes very still. "
            + "'Not this,' it says. 'Not before the guild sits.'" + HeldPlain,
        HeldFacet =>
            "Something bright lies in the blighted ground. 'The guild will want that,' says the "
            + "Register, 'once there is a guild to carry it home.'" + HeldPlain,
        _ => PlaceHeldLine,
    };

    /// <summary>WorldGenerator's Home Camp: the guild's dock.</summary>
    public const string DockSource = "Start";

    public const string BegunFlag = "interval_begun";
    public const string DoneFlag = "interval_done";

    /// <summary>The guild walks the arc (Standing Orders O2, D166): chapter I in the Grand Hall instead
    /// of the founding, and the interval from the gate. Written at the founding screen while Debug
    /// Mode's Walk the Arc is on, until the arc can be walked end to end (O6).</summary>
    public const string ArcFlag = "interval_arc";

    // ── Placement, in tiles (docs/standing_orders_first_reading_v1.md, section 8) ──
    public const int CityMin = 10;
    public const int CityMax = 15;
    public const int CityIdeal = 12;
    public const int CityNearest = 6;
    public const int StoreMin = 10;
    public const int StoreMax = 15;
    public const int StoreIdeal = 12;
    public const int StartMin = 5;
    public const int StartMax = 6;
    public const int StartFuel = 8;
    public const int ChartRadius = 3;

    // ── Predicates ───────────────────────────────────────────────────────

    /// <summary>The interval is being walked: the moon is held.</summary>
    public static bool Walking(CycleState cycle) => cycle?.Interval != null && cycle.Interval.Active;

    /// <summary>The interval is being walked and the door is not yet set: the home is shut.</summary>
    public static bool DoorShut(CycleState cycle) => Walking(cycle) && !cycle.Interval.DoorSet;

    /// <summary>The errand's places while the interval walks (O3b, D198): the only places a dive can work
    /// before the founding. The field store (O3); chapter IV's patrol and picket join it in O5.</summary>
    public static bool ErrandAt(CycleState cycle, int x, int y) => IntervalStore.IsStoreAt(cycle, x, y);

    /// <summary>The tile lies inside the guild's home city.</summary>
    public static bool IsHomeGround(CycleState cycle, int x, int y)
        => cycle?.World?.SettlementAt(x, y) is { IsGuildHome: true };

    /// <summary>A guard for the places a day could still pass (WorldClock.StepDay, the
    /// lunation tick): while the interval walks it logs an error and returns true, and the
    /// caller does nothing. Every way in is gated before this, so the error means a new way.</summary>
    public static bool RefuseDay(CycleState cycle, string where)
    {
        if (!Walking(cycle))
        {
            return false;
        }
        GD.PushError($"[Interval] {where} was asked to pass time while the moon is held. Nothing passed.");
        return true;
    }

    // ── The walk ─────────────────────────────────────────────────────────

    /// <summary>The guild walks the arc (ArcFlag on its ledger).</summary>
    public static bool ArcFor(GuildSaveData save) =>
        save?.Ledger?.MetaNarrativeFlags?.Contains(ArcFlag) ?? false;

    /// <summary>Debug (the founding screen, while Walk the Arc is on): this new guild walks the arc.
    /// False when there is no save.</summary>
    public static bool MarkArcForDebug(GuildSaveData save)
    {
        if (save?.Ledger == null)
        {
            return false;
        }
        SetMeta(save, ArcFlag);
        SaveManager.MarkDirty();
        GD.Print("[Interval] Debug: this guild walks the arc. Chapter I waits in the Grand Hall.");
        return true;
    }

    /// <summary>Begin the interval on this reading: the gate's "Go out." for a guild walking the arc
    /// (O2), and the debug panel's Walk the Interval. The moon is held at today's date, and the castle
    /// takes the road at once (EnsureStarted); the map shows it from its next load.</summary>
    public static void Begin(GuildSaveData save)
    {
        var cycle = save?.Cycle;
        if (cycle?.World == null)
        {
            GD.PrintErr("[Interval] There is no reading to walk.");
            return;
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            GD.PrintErr("[Interval] The castle is in the field. Bring it in, then walk the interval.");
            return;
        }
        int store = IntervalStore.Detach(cycle);   // O3 (D188): a walk begun again reuses its store
        cycle.Interval = new IntervalState
        {
            Active = true,
            HeldLunation = cycle.Calendar?.CurrentLunation ?? 1,
            HeldDay = cycle.Calendar?.DayOfLunation ?? 0,
            StorePoiIndex = store,
        };
        cycle.PendingStraggleLunations = 0;
        SetMeta(save, BegunFlag);
        RemoveMeta(save, DoneFlag);   // O1b: a walk begun again is not over (the debug panel's Walk)
        GD.Print($"[Interval] Walking. The moon is held at lunation {cycle.Interval.HeldLunation}, "
                 + $"day {cycle.Interval.HeldDay + 1}.");
        EnsureStarted(cycle);
    }

    /// <summary>End the interval (the debug button for now; the U17 skip and the founding
    /// later): the door is set, the moon runs again, and the castle berths at {home} with a
    /// full furnace, its parties beside it.</summary>
    public static void End(GuildSaveData save)
    {
        var cycle = save?.Cycle;
        if (!Walking(cycle))
        {
            GD.Print("[Interval] The interval is not being walked; nothing to end.");
            return;
        }
        cycle.Interval.Active = false;
        cycle.Interval.DoorSet = true;
        SetDockAvailable(cycle.World, true);
        BerthAtHome(cycle);
        SetMeta(save, DoneFlag);
        SaveManager.MarkDirty();
        GD.Print("[Interval] Ended. The door is set, and the moon runs again.");
    }

    /// <summary>Set the door at {home} (D163; chapter IV's "Set the door" in O5, the debug
    /// panel's Next Standing Order for now): the home opens and the dock with it, and the castle
    /// berths at {home} with a full furnace. The moon stays held until the founding.</summary>
    public static void SetDoor(GuildSaveData save)
    {
        var cycle = save?.Cycle;
        if (!Walking(cycle))
        {
            GD.Print("[Interval] The interval is not being walked; there is no door to set.");
            return;
        }
        if (cycle.Interval.DoorSet)
        {
            return;
        }
        cycle.Interval.DoorSet = true;
        SetDockAvailable(cycle.World, true);
        BerthAtHome(cycle);
        SaveManager.MarkDirty();
        GD.Print("[Interval] The door is set at home. The home is open; the moon stays held until the founding.");
    }

    /// <summary>Place the route and stand the castle on the road, once, while the interval
    /// walks. StrategicView._Ready calls it just before ExpeditionAnchors.EnsureCastleSited,
    /// which then leaves the castle where this put it.</summary>
    public static void EnsureStarted(CycleState cycle)
    {
        if (!Walking(cycle) || cycle.World == null)
        {
            return;
        }
        if (cycle.Interval.Placed)
        {
            IntervalStore.Place(cycle);   // O3 (D187): a walk placed before the store was a place gets it now
            return;
        }
        var world = cycle.World;
        var iv = cycle.Interval;
        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            GD.PrintErr("[Interval] The castle is in the field; it takes the road when it comes back.");
            return;
        }

        // The dock is shut whatever the route does.
        SetDockAvailable(world, false);
        iv.Placed = true;
        SaveManager.MarkDirty();

        if (!world.InBounds(world.HomeX, world.HomeY))
        {
            GD.PrintErr("[Interval] There is no home on the map, so there is no route: the castle stays where it is.");
            return;
        }
        if (!PlaceRoute(world, iv))
        {
            return;   // PlaceRoute said why; the castle stays where it is
        }
        IntervalStore.Place(cycle);   // O3 (D187): the store becomes a place on the route
        StandOnRoad(cycle, iv.RoadX, iv.RoadY);
    }

    // ── Placement ────────────────────────────────────────────────────────

    /// <summary>Choose the route (docs/standing_orders_first_reading_v1.md, section 8): the city
    /// nearest 12 tiles from {home}, the store 10 to 15 tiles past it, and the castle's start 5
    /// to 6 tiles past the store, each on the far side from {home} and on a road where one
    /// fits. Records them on the state. False, with a log line, when there is no city or no
    /// store to place.</summary>
    public static bool PlaceRoute(WorldData world, IntervalState iv)
    {
        int hx = world.HomeX;
        int hy = world.HomeY;
        int city = PickCity(world, hx, hy, out int cityDist);
        if (city < 0)
        {
            GD.PrintErr($"[Interval] No city at least {CityNearest} tiles from home ({hx},{hy}): no route.");
            return false;
        }
        var c = world.Settlements[city];
        if (!PickBeyond(world, c.CenterX, c.CenterY, hx, hy, StoreMin, StoreMax, StoreIdeal, out int sx, out int sy)
            && !PickBeyond(world, c.CenterX, c.CenterY, hx, hy, StoreMin - 3, StoreMax + 5, StoreIdeal, out sx, out sy))
        {
            GD.PrintErr($"[Interval] No ground for the store past {Describe(c)}: no route.");
            return false;
        }
        if (!PickBeyond(world, sx, sy, c.CenterX, c.CenterY, StartMin, StartMax, StartMin, out int rx, out int ry)
            && !PickBeyond(world, sx, sy, c.CenterX, c.CenterY, StartMin - 2, StartMax + 1, StartMin, out rx, out ry))
        {
            GD.PrintErr($"[Interval] No ground for the castle's start near the store at ({sx},{sy}): no route.");
            return false;
        }

        iv.CitySettlement = city;
        iv.StoreX = sx;
        iv.StoreY = sy;
        iv.RoadX = rx;
        iv.RoadY = ry;
        GD.Print($"[Interval] Route: home ({hx},{hy}); {Describe(c)}, {cityDist} tiles out; "
                 + $"the store ({sx},{sy}), {world.HexDistance(c.CenterX, c.CenterY, sx, sy)} past the city"
                 + $"{(IsRoad(world, sx, sy) ? ", on a road" : "")}; the start ({rx},{ry}), "
                 + $"{world.HexDistance(sx, sy, rx, ry)} past the store{(IsRoad(world, rx, ry) ? ", on a road" : "")}.");
        return true;
    }

    /// <summary>A city, not the home and not Kassian's seat, nearest <see cref="CityIdeal"/>
    /// tiles from (hx, hy), inside CityMin..CityMax if any is, and never nearer than
    /// CityNearest. -1 if there is none.</summary>
    private static int PickCity(WorldData world, int hx, int hy, out int dist)
    {
        dist = 0;
        int best = -1;
        int bestScore = int.MaxValue;
        var list = world.Settlements;
        if (list == null)
        {
            return -1;
        }
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s == null || s.IsGuildHome || s.Tier != SettlementTier.City || !world.InBounds(s.CenterX, s.CenterY))
            {
                continue;
            }
            if (s.CenterX == world.ConvergenceX && s.CenterY == world.ConvergenceY)
            {
                continue;
            }
            int d = world.HexDistance(hx, hy, s.CenterX, s.CenterY);
            if (d < CityNearest)
            {
                continue;
            }
            int score = System.Math.Abs(d - CityIdeal) + (d >= CityMin && d <= CityMax ? 0 : 100);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                dist = d;
            }
        }
        return best;
    }

    /// <summary>A free land tile min..max tiles from (ax, ay) that lies on the far side of it
    /// from (bx, by), as near the line from b through a as the ground allows. Prefers a road
    /// and the distance nearest <paramref name="ideal"/>. Scans row by row, so ties go to the
    /// first row, then the first column.</summary>
    private static bool PickBeyond(WorldData world, int ax, int ay, int bx, int by,
                                   int min, int max, int ideal, out int x, out int y)
    {
        x = -1;
        y = -1;
        int ab = world.HexDistance(ax, ay, bx, by);
        int bestScore = int.MaxValue;
        for (int ty = 0; ty < world.Height; ty++)
        {
            for (int tx = 0; tx < world.Width; tx++)
            {
                int ta = world.HexDistance(tx, ty, ax, ay);
                if (ta < min || ta > max || !IsFreeLand(world, tx, ty))
                {
                    continue;
                }
                int tb = world.HexDistance(tx, ty, bx, by);
                if (tb <= ab)
                {
                    continue;   // not past (ax, ay) as seen from (bx, by)
                }
                int bend = ab + ta - tb;   // 0 on the line from b through a
                int score = bend * 4 + System.Math.Abs(ta - ideal) + (IsRoad(world, tx, ty) ? 0 : 5);
                if (score < bestScore)
                {
                    bestScore = score;
                    x = tx;
                    y = ty;
                }
            }
        }
        return x >= 0;
    }

    /// <summary>Land with no settlement and no place on it.</summary>
    private static bool IsFreeLand(WorldData world, int x, int y)
    {
        if (!world.TryIndex(x, y, out int idx) || idx >= world.Tiles.Length)
        {
            return false;
        }
        var t = world.Tiles[idx];
        return !t.IsWater && t.SettlementIndex < 0 && t.PoiIndex < 0;
    }

    private static bool IsRoad(WorldData world, int x, int y)
        => world.TryIndex(x, y, out int idx) && idx < world.Tiles.Length
           && world.Tiles[idx].Terrain == OverworldHex.TerrainType.Road;

    private static string Describe(WorldSettlement s)
        => $"{(string.IsNullOrEmpty(s.Name) ? "a city" : s.Name)} ({s.CenterX},{s.CenterY})";

    /// <summary>The castle stands at (x, y): parked, its own launch point under it, no
    /// resupply owed and not busy, the furnace at <see cref="StartFuel"/>. The ground around it
    /// is charted, and every party standing at an anchor joins it. Written directly rather
    /// than through ExpeditionAnchors.ParkCastleAt, so the start sights nothing (D151).</summary>
    private static void StandOnRoad(CycleState cycle, int x, int y)
    {
        var world = cycle.World;
        var castle = CastleTypes.For(PlayerSession.SelectedSchool);
        string name = string.IsNullOrEmpty(castle?.Name) ? "The castle" : castle.Name;

        cycle.CastleX = x;
        cycle.CastleY = y;
        cycle.CastleParked = true;
        cycle.CastleParkedLunation = cycle.Calendar?.CurrentLunation ?? 1;
        cycle.CastleRepairLunations = 0;
        cycle.CastleBusyUntilDay = 0;
        cycle.CastleMaxFuel = Mathf.Max(cycle.CastleMaxFuel, ExpeditionAnchors.ProvisionalMaxFuel + (castle?.BonusMaxFuel ?? 0));
        cycle.CastleFuel = Mathf.Min(StartFuel, cycle.CastleMaxFuel);

        world.StagingPoints ??= new List<StagingPoint>();
        world.StagingPoints.RemoveAll(sp => sp != null && sp.Source == ExpeditionAnchors.CastleStagingSource);
        world.StagingPoints.Add(new StagingPoint
        {
            X = x,
            Y = y,
            Name = name,
            Source = ExpeditionAnchors.CastleStagingSource,
            Available = true,
        });

        int charted = 0;
        foreach (var (cx, cy) in world.Disc(x, y, ChartRadius))
        {
            if (world.TryIndex(cx, cy, out int idx) && idx < world.Tiles.Length
                && world.Tiles[idx].Discovery == TileDiscovery.Unseen)
            {
                world.Tiles[idx].Discovery = TileDiscovery.Charted;
                charted++;
            }
        }

        int joined = 0;
        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null || !string.IsNullOrEmpty(p.ParentId) || p.State != FieldPartyState.AtAnchor)
                {
                    continue;   // detachments stay posted; a party on the road or at work is left be
                }
                p.X = x;
                p.Y = y;
                p.AnchorKey = ExpeditionAnchors.CastleKeyOf(x, y);
                p.BusyUntilDay = 0;
                joined++;
            }
        }
        SaveManager.MarkDirty();
        GD.Print($"[Interval] {name} stands on the road at ({x},{y}), furnace {cycle.CastleFuel}/{cycle.CastleMaxFuel}; "
                 + $"{charted} tile(s) charted around it, {joined} party(ies) beside it.");
    }

    /// <summary>The castle berths at {home} as a new reading's castle does
    /// (ExpeditionAnchors.EnsureCastleSited): parked on the dock with no camp of its own, no
    /// resupply owed, the furnace full. The parties that stood with it go with it.</summary>
    private static void BerthAtHome(CycleState cycle)
    {
        var world = cycle.World;
        if (world == null || !world.InBounds(world.HomeX, world.HomeY))
        {
            return;
        }
        if (cycle.CastleSortie != null && cycle.CastleSortie.IsLive())
        {
            return;   // in the field: it docks when it comes back
        }
        int ox = cycle.CastleX;
        int oy = cycle.CastleY;
        cycle.CastleX = world.HomeX;
        cycle.CastleY = world.HomeY;
        cycle.CastleParked = true;
        cycle.CastleRepairLunations = 0;
        if (cycle.CastleMaxFuel > 0)
        {
            cycle.CastleFuel = cycle.CastleMaxFuel;
        }
        world.StagingPoints?.RemoveAll(sp => sp != null && sp.Source == ExpeditionAnchors.CastleStagingSource);

        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null || !string.IsNullOrEmpty(p.ParentId) || p.State != FieldPartyState.AtAnchor
                    || p.X != ox || p.Y != oy)
                {
                    continue;
                }
                p.X = world.HomeX;
                p.Y = world.HomeY;
                p.AnchorKey = ExpeditionAnchors.CastleKeyOf(world.HomeX, world.HomeY);
            }
        }
        GD.Print($"[Interval] The castle berths at home ({world.HomeX},{world.HomeY}) with a full furnace.");
    }

    /// <summary>Home Camp opens or shuts as a launch point, a supply anchor and a free
    /// extraction (the dock waits for the door).</summary>
    private static void SetDockAvailable(WorldData world, bool available)
    {
        if (world?.StagingPoints == null)
        {
            return;
        }
        foreach (var sp in world.StagingPoints)
        {
            if (sp != null && sp.Source == DockSource)
            {
                sp.Available = available;
            }
        }
    }

    // ── The held date ────────────────────────────────────────────────────

    /// <summary>On each map load while the interval walks: the moon still holds where it was
    /// held. Logs only.</summary>
    public static void AssertHeld(CycleState cycle, string where)
    {
        if (!Walking(cycle) || cycle.Calendar == null)
        {
            return;
        }
        var iv = cycle.Interval;
        var cal = cycle.Calendar;
        if (cal.CurrentLunation != iv.HeldLunation || cal.DayOfLunation != iv.HeldDay)
        {
            GD.PushError($"[Interval Assert] FAIL at {where}: the moon was held at lunation {iv.HeldLunation}, "
                         + $"day {iv.HeldDay + 1}, and it is lunation {cal.CurrentLunation}, day {cal.DayOfLunation + 1}.");
            return;
        }
        GD.Print($"[Interval Assert] PASS at {where}: the moon holds at lunation {iv.HeldLunation}, day {iv.HeldDay + 1}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void SetMeta(GuildSaveData save, string flag)
    {
        if (string.IsNullOrEmpty(flag) || save?.Ledger == null)
        {
            return;
        }
        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        if (!save.Ledger.MetaNarrativeFlags.Contains(flag))
        {
            save.Ledger.MetaNarrativeFlags.Add(flag);
        }
    }

    private static void RemoveMeta(GuildSaveData save, string flag)
    {
        save?.Ledger?.MetaNarrativeFlags?.Remove(flag);
    }
}
