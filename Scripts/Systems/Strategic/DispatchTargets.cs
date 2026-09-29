using Godot;
using System.Collections.Generic;

// ============================================================
// DispatchTargets.cs: where a force can go from here, and at what cost (2026-09-28)
//
// Purpose:        The one list behind the dispatch rail and the map's
//                 destination rings. Asked for after the sortie flow fought
//                 the player: forces could only be chosen from the roster,
//                 and a waystone only counted if the click hit its exact
//                 ground tile, under an orb drawn well above it.
//
//                 Every anchor the world already knows (ExpeditionAnchors.All)
//                 becomes a row with the travel method the rules already
//                 decide: a field party STEPS THROUGH to a waystone, the
//                 parked castle or the dock, and WALKS anywhere else; the
//                 castle MARCHES on fuel. Nothing here is a new rule. The
//                 refusals are the rule classes' own sentences.
// Layer:          Systems / Strategic
// Collaborators:  ExpeditionAnchors (All), FieldMarch (CanTeleportTo,
//                 CanMarchTo, DaysTo), CastleMarch (CanMarchTo, CostTo),
//                 WorldClock (PartyBusy), StrategicView (the rail),
//                 WorldAtlas3D (the rings)
// ============================================================

public enum DispatchMethod
{
    StepThrough,
    OnFoot,
    CastleMarch,
}

public sealed class DispatchTarget
{
    public int X;
    public int Y;
    public string Name = "";
    public DispatchMethod Method;
    public int Tiles;
    public int Days;
    public int Fuel;
    public bool CanGo;
    public string Why = "";

    /// <summary>A step-through leaves the party standing free on an anchor,
    /// so it can take the field in the same order.</summary>
    public bool CanTakeFieldOnArrival;

    /// <summary>The cost in a few words, for the rail and the map label.</summary>
    public string CostLine => Method switch
    {
        DispatchMethod.StepThrough => "step through  ·  now",
        DispatchMethod.OnFoot => $"on foot  ·  {Tiles} tile(s)  ·  {Days} day(s)",
        _ => $"march  ·  {Tiles} tile(s)  ·  {Fuel} fuel",
    };

    /// <summary>The map label: shorter than the rail's line.</summary>
    public string ShortCost => Method switch
    {
        DispatchMethod.StepThrough => "step · now",
        DispatchMethod.OnFoot => $"walk · {Days}d",
        _ => $"march · {Fuel} fuel",
    };
}

public static class DispatchTargets
{
    /// <summary>How far a click may land from a waystone, in tiles, and still
    /// mean the waystone. Only step-through targets snap: a walk or a march
    /// to a tile beside a city is a real order and must not be rerouted.</summary>
    public const int SnapTiles = 2;

    /// <summary>Every anchor a field party could be sent to, sorted: what it
    /// can do first, step-throughs before walks, nearest first.</summary>
    public static List<DispatchTarget> ForParty(CycleState cycle, FieldParty party)
    {
        var list = new List<DispatchTarget>();
        if (cycle?.World == null || party == null || party.X < 0 || party.Y < 0)
        {
            return list;
        }
        bool freeAfter = !WorldClock.PartyBusy(cycle, party);
        foreach (var (x, y, name) in Places(cycle))
        {
            if (x == party.X && y == party.Y)
            {
                continue;
            }
            var t = new DispatchTarget
            {
                X = x,
                Y = y,
                Name = name,
                Tiles = cycle.World.HexDistance(party.X, party.Y, x, y),
            };
            if (FieldMarch.CanTeleportTo(cycle, party, x, y, out _))
            {
                t.Method = DispatchMethod.StepThrough;
                t.CanGo = true;
                t.CanTakeFieldOnArrival = freeAfter;
            }
            else
            {
                t.Method = DispatchMethod.OnFoot;
                t.CanGo = FieldMarch.CanMarchTo(cycle, party, x, y, out string why);
                t.Why = why ?? "";
                t.Days = FieldMarch.DaysTo(cycle, party, x, y);
            }
            list.Add(t);
        }
        Sort(list);
        return list;
    }

    /// <summary>Every anchor the parked castle could march to.</summary>
    public static List<DispatchTarget> ForCastle(CycleState cycle)
    {
        var list = new List<DispatchTarget>();
        if (cycle?.World == null || !cycle.World.InBounds(cycle.CastleX, cycle.CastleY))
        {
            return list;
        }
        foreach (var (x, y, name) in Places(cycle))
        {
            if (x == cycle.CastleX && y == cycle.CastleY)
            {
                continue;
            }
            var t = new DispatchTarget
            {
                X = x,
                Y = y,
                Name = name,
                Method = DispatchMethod.CastleMarch,
                Tiles = cycle.World.HexDistance(cycle.CastleX, cycle.CastleY, x, y),
                Fuel = CastleMarch.CostTo(cycle, x, y),
            };
            t.CanGo = CastleMarch.CanMarchTo(cycle, x, y, out string why);
            t.Why = why ?? "";
            list.Add(t);
        }
        Sort(list);
        return list;
    }

    /// <summary>The target a click at (col,row) means: a step-through target on
    /// that tile or within SnapTiles of it, nearest first. Null when the click
    /// is not near one, and the caller treats it as an ordinary tile.</summary>
    public static DispatchTarget SnapAt(WorldData world, List<DispatchTarget> targets, int col, int row)
    {
        if (world == null || targets == null)
        {
            return null;
        }
        DispatchTarget best = null;
        int bestD = int.MaxValue;
        foreach (var t in targets)
        {
            if (t == null || !t.CanGo || t.Method != DispatchMethod.StepThrough)
            {
                continue;
            }
            int d = world.HexDistance(col, row, t.X, t.Y);
            if (d <= SnapTiles && d < bestD)
            {
                best = t;
                bestD = d;
            }
        }
        return best;
    }

    public static DispatchTarget At(List<DispatchTarget> targets, int x, int y)
        => targets?.Find(t => t != null && t.X == x && t.Y == y);

    /// <summary>Every place worth a row, once per tile, named the way the map
    /// names it (a waystone carries its affix letters from its staging
    /// point's name).</summary>
    private static List<(int x, int y, string name)> Places(CycleState cycle)
    {
        var result = new List<(int, int, string)>();
        var seen = new HashSet<Vector2I>();
        foreach (var a in ExpeditionAnchors.All(cycle))
        {
            if (a == null || !a.Available || !cycle.World.InBounds(a.X, a.Y))
            {
                continue;
            }
            var key = new Vector2I(a.X, a.Y);
            if (!seen.Add(key))
            {
                continue;
            }
            string name = string.IsNullOrEmpty(a.Name) ? "A place" : a.Name;
            if (a.Kind == AnchorKind.Built)
            {
                var sp = cycle.World.StagingPoints?.Find(s => s != null && s.X == a.X && s.Y == a.Y
                                                            && s.Source == ExpeditionAnchors.WaypointStagingSource);
                if (sp != null && !string.IsNullOrEmpty(sp.Name))
                {
                    name = sp.Name;
                }
            }
            else if (a.Kind != AnchorKind.CastlePark && cycle.CastleParked
                     && a.X == cycle.CastleX && a.Y == cycle.CastleY)
            {
                name += "  ·  the castle";
            }
            result.Add((a.X, a.Y, name));
        }
        return result;
    }

    private static void Sort(List<DispatchTarget> list)
    {
        list.Sort((a, b) =>
        {
            int c = b.CanGo.CompareTo(a.CanGo);
            if (c != 0)
            {
                return c;
            }
            c = ((int)a.Method).CompareTo((int)b.Method);
            return c != 0 ? c : a.Tiles.CompareTo(b.Tiles);
        });
    }
}
