using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// KeeperHunt.cs
//
// Purpose:        The Keeper who hunts the castle (docs/provinces_v1_10_
//                 keeper.md, slice PV6; X3, X7). One Keeper walks the
//                 province graph, a hop at a time, toward where it
//                 believes the castle is. It plays a revealed intent
//                 deck, one card each new moon, the next always shown,
//                 as enemies show their intents in combat (PR75): Hunt,
//                 Hunt, Cut Supply, Hunt, Rouse a Front, Lurk. It learns
//                 where the castle is when the castle moves, unless the
//                 move was veiled, when a maneuver is cast (casting is
//                 loud), and when the castle stands within its sight
//                 (PR77). Standing in the castle's province, it falls on
//                 the camp, then draws back a hop and loses the trail
//                 (PR78).
// Layer:          System (strategic)
// Collaborators:  KeeperState.cs (the state), StrategicView.cs (NewMoon
//                 in the lunation, the mark on the map),
//                 StrategicView.Keeper.cs (the readout, the mark),
//                 WorldAtlas3D.Keeper.cs (the mark), CastleManeuvers.cs
//                 (a move and a cast tell it), CastleHops.cs (fouled
//                 roads cost more), CastleThreats.cs (the strike),
//                 StrategicDebug.cs (DebugWake), ProvinceSaveAssert.
//                 Keeper.cs
// See:            docs/provinces_v1_10_keeper.md
// ============================================================

/// <summary>A card of the Keeper's intent deck.</summary>
public enum KeeperIntent
{
    Hunt = 0,
    CutSupply = 1,
    RouseFront = 2,
    Lurk = 3,
}

/// <summary>The Keeper's hunt (PV6).</summary>
public static class KeeperHunt
{
    // ── Tuning (section 3, PR74 to PR78) ─────────────────────────────────

    /// <summary>The lunation from which the Keeper wakes, at the first new moon on or after it.</summary>
    public const int WakeLunation = 3;

    /// <summary>What a hop into a province of fouled roads costs more (Cut Supply).</summary>
    public const int CutSupplyCost = 3;

    /// <summary>What Rouse a Front adds to the nearest front's Advance.</summary>
    public const int RouseAdvance = 20;

    /// <summary>How many hops off the Keeper sees the castle; Lurk sees farther.</summary>
    public const int Sight = 1;
    public const int LurkSight = 2;

    /// <summary>The intent deck, played in order and again from the top (PR75).</summary>
    public static readonly KeeperIntent[] Deck =
    {
        KeeperIntent.Hunt, KeeperIntent.Hunt, KeeperIntent.CutSupply,
        KeeperIntent.Hunt, KeeperIntent.RouseFront, KeeperIntent.Lurk,
    };

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>True where the Keeper can walk: a world with provinces.</summary>
    public static bool Walks(CycleState cycle)
        => cycle?.World != null && cycle.World.HasProvinces() && cycle.Keeper != null;

    /// <summary>The province the Keeper stands in, or null while it sleeps.</summary>
    public static Province Where(CycleState cycle)
        => Walks(cycle) && cycle.Keeper.Awake ? ProvinceControl.ProvinceById(cycle.World, cycle.Keeper.ProvinceId) : null;

    /// <summary>The Keeper's next card.</summary>
    public static KeeperIntent NextIntent(CycleState cycle)
        => Deck[Math.Abs(cycle?.Keeper?.DeckIndex ?? 0) % Deck.Length];

    /// <summary>A card's name.</summary>
    public static string IntentName(KeeperIntent intent) => intent switch
    {
        KeeperIntent.Hunt => "Hunt",
        KeeperIntent.CutSupply => "Cut Supply",
        KeeperIntent.RouseFront => "Rouse a Front",
        _ => "Lurk",
    };

    /// <summary>The next card in words, aimed: "Hunt toward Crownland", "Hunt (it has lost the
    /// trail)", "Cut Supply around North Weald", "Rouse a Front", "Lurk".</summary>
    public static string IntentText(CycleState cycle)
    {
        var intent = NextIntent(cycle);
        var where = Where(cycle);
        switch (intent)
        {
            case KeeperIntent.Hunt:
            {
                var known = ProvinceControl.ProvinceById(cycle.World, cycle.Keeper.KnownCastleProvinceId);
                return known != null ? $"Hunt toward {ProvinceControl.Name(known)}" : "Hunt (it has lost the trail)";
            }
            case KeeperIntent.CutSupply:
                return where != null ? $"Cut Supply around {ProvinceControl.Name(where)}" : "Cut Supply";
            default:
                return IntentName(intent);
        }
    }

    /// <summary>What a hop into this province costs more for the Keeper's fouled roads.</summary>
    public static int SupplyCut(CycleState cycle, int province)
    {
        if (!Walks(cycle) || string.IsNullOrEmpty(cycle.Keeper.SupplyCutProvinceId))
            return 0;
        var centre = ProvinceControl.ProvinceById(cycle.World, cycle.Keeper.SupplyCutProvinceId);
        if (centre == null)
            return 0;
        return centre.Index == province || centre.Neighbors.Contains(province) ? CutSupplyCost : 0;
    }

    /// <summary>Hops from one province to every other on the graph; -1 where none reaches.</summary>
    private static int[] Distances(WorldData world, int from)
    {
        var dist = new int[world.Provinces.Count];
        for (int i = 0; i < dist.Length; i++)
            dist[i] = -1;
        if (from < 0 || from >= dist.Length)
            return dist;
        dist[from] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in world.Provinces[u].Neighbors)
            {
                if (v < 0 || v >= dist.Length || dist[v] >= 0)
                    continue;
                dist[v] = dist[u] + 1;
                queue.Enqueue(v);
            }
        }
        return dist;
    }

    /// <summary>One hop from <paramref name="from"/> toward <paramref name="to"/> on the
    /// shortest way (the lower index on a tie); <paramref name="from"/> when none leads there.</summary>
    private static int Step(WorldData world, int from, int to)
    {
        if (from == to)
            return from;
        var dist = Distances(world, to);
        if (from < 0 || dist[from] <= 0)
            return from;
        int best = -1;
        foreach (int v in world.Provinces[from].Neighbors)
        {
            if (v >= 0 && v < dist.Length && dist[v] == dist[from] - 1 && (best < 0 || v < best))
                best = v;
        }
        return best < 0 ? from : best;
    }

    // ══════════════════════════════════════════════════════════════════════
    // What the Keeper learns (PR77)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The castle moved: the Keeper reads the trail, unless the move was veiled.</summary>
    public static void Learn(CycleState cycle, bool veiled)
    {
        if (veiled || Where(cycle) == null)
            return;
        int castle = CastleHops.CastleProvince(cycle);
        if (castle >= 0)
            cycle.Keeper.KnownCastleProvinceId = cycle.World.Provinces[castle].Id;
    }

    /// <summary>A maneuver was cast: casting is loud, and the Keeper hears where the castle
    /// stands. Veil is cast quietly.</summary>
    public static void HearCast(CycleState cycle, string spellId)
    {
        if (spellId != "veil")
            Learn(cycle, false);
    }

    /// <summary>The castle within the Keeper's sight is found.</summary>
    private static void Watch(CycleState cycle, int sight)
    {
        var where = Where(cycle);
        int castle = CastleHops.CastleProvince(cycle);
        if (where == null || castle < 0)
            return;
        int hops = Distances(cycle.World, where.Index)[castle];
        if (hops >= 0 && hops <= sight)
            cycle.Keeper.KnownCastleProvinceId = cycle.World.Provinces[castle].Id;
    }

    // ══════════════════════════════════════════════════════════════════════
    // The new moon
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The Keeper's new moon: it wakes (PR74), or plays its card (PR76), looks about,
    /// and falls on the castle if it stands in the castle's province (PR78). Returns the news.</summary>
    public static List<string> NewMoon(CycleState cycle, Func<string, string> fd = null)
    {
        var lines = new List<string>();
        if (!Walks(cycle))
            return lines;
        var keeper = cycle.Keeper;
        var world = cycle.World;
        keeper.SupplyCutProvinceId = "";   // the fouled roads clear at the new moon
        if (!keeper.Awake)
        {
            if ((cycle.Calendar?.CurrentLunation ?? 0) < WakeLunation)
                return lines;
            var home = WakePlace(cycle);
            if (home == null)
                return lines;
            Wake(cycle, home);
            lines.Add($"{ProvinceControl.LossMark}The Keeper wakes in {ProvinceControl.Name(home)}. It knows where the castle "
                      + $"stands. Its first move: {IntentText(cycle)}.");
            return lines;
        }
        var here = Where(cycle);
        if (here == null)
            return lines;

        var intent = NextIntent(cycle);
        keeper.DeckIndex = (keeper.DeckIndex + 1) % Deck.Length;
        int sight = Sight;
        switch (intent)
        {
            case KeeperIntent.Hunt:
            {
                var target = ProvinceControl.ProvinceById(world, keeper.KnownCastleProvinceId);
                if (target == null)
                {
                    sight = LurkSight;
                    lines.Add($"The Keeper has lost the castle's trail. It waits in {ProvinceControl.Name(here)}, watching.");
                    break;
                }
                int next = Step(world, here.Index, target.Index);
                keeper.ProvinceId = world.Provinces[next].Id;
                lines.Add(next == here.Index
                    ? $"The Keeper hunts, but no road leads it on from {ProvinceControl.Name(here)}."
                    : $"The Keeper hunts: it moves to {ProvinceControl.Name(world.Provinces[next])}.");
                break;
            }
            case KeeperIntent.CutSupply:
                keeper.SupplyCutProvinceId = here.Id;
                lines.Add($"The Keeper fouls the roads around {ProvinceControl.Name(here)}: a hop into it or a "
                          + $"province beside it costs {CutSupplyCost} more fuel until the next new moon.");
                break;
            case KeeperIntent.RouseFront:
                lines.Add(Rouse(cycle, here));
                break;
            default:
                sight = LurkSight;
                lines.Add($"The Keeper lurks in {ProvinceControl.Name(here)}, watching the roads.");
                break;
        }
        Watch(cycle, sight);
        string strike = Strike(cycle);
        if (strike.Length > 0)
            lines.Add(strike);
        lines.Add($"The Keeper's next move: {IntentText(cycle)}.");
        return lines;
    }

    /// <summary>Where the Keeper wakes: the capital province of the Convergence's realm, or,
    /// without one, the province farthest from the castle's (the lower index on a tie).</summary>
    private static Province WakePlace(CycleState cycle)
    {
        var world = cycle.World;
        string seat = SeatRealm(world);
        if (seat.Length > 0)
        {
            int capital = ProvinceControl.CapitalIndex(world, seat);
            if (capital >= 0)
                return world.Provinces[capital];
        }
        int castle = CastleHops.CastleProvince(cycle);
        if (castle < 0)
            return null;
        var dist = Distances(world, castle);
        int best = -1;
        for (int i = 0; i < dist.Length; i++)
        {
            if (dist[i] > 0 && (best < 0 || dist[i] > dist[best]))
                best = i;
        }
        return best < 0 ? null : world.Provinces[best];
    }

    /// <summary>The Convergence's realm, or "".</summary>
    private static string SeatRealm(WorldData world)
        => world.ConvergenceX >= 0 && world.InBounds(world.ConvergenceX, world.ConvergenceY)
            ? world.GetTile(world.ConvergenceX, world.ConvergenceY).KingdomId ?? ""
            : "";

    private static void Wake(CycleState cycle, Province home)
    {
        var keeper = cycle.Keeper;
        keeper.Awake = true;
        keeper.ProvinceId = home.Id;
        keeper.HomeProvinceId = home.Id;
        keeper.DeckIndex = 0;
        keeper.KnownCastleProvinceId = "";
        Learn(cycle, false);
    }

    /// <summary>Rouse a Front: the open front nearest the Keeper gains RouseAdvance.</summary>
    private static string Rouse(CycleState cycle, Province here)
    {
        var dist = Distances(cycle.World, here.Index);
        Warfront best = null;
        int bestHops = int.MaxValue;
        foreach (var wf in cycle.Warfronts ?? new List<Warfront>())
        {
            if (wf == null || wf.Closed || wf.IsCacheSiege)
                continue;
            var contested = ProvinceControl.ProvinceById(cycle.World, wf.ProvinceId);
            if (contested == null || dist[contested.Index] < 0 || dist[contested.Index] >= bestHops)
                continue;
            best = wf;
            bestHops = dist[contested.Index];
        }
        if (best == null)
            return $"The Keeper looks for a war to stir from {ProvinceControl.Name(here)} and finds none.";
        best.Advance += RouseAdvance;
        var at = ProvinceControl.ProvinceById(cycle.World, best.ProvinceId);
        return $"The Keeper stirs the war at {ProvinceControl.Name(at)}: the front gains {RouseAdvance}.";
    }

    /// <summary>Standing in the castle's province, the Keeper falls on its camp (PR78): a castle
    /// defence is owed, as a kingdom's assault owes one. Then it draws back a hop toward where
    /// it woke and loses the trail. A warded camp turns it aside. Returns the news, or "".</summary>
    private static string Strike(CycleState cycle)
    {
        var keeper = cycle.Keeper;
        var here = Where(cycle);
        int castle = CastleHops.CastleProvince(cycle);
        if (here == null || castle != here.Index || !cycle.CastleParked
            || !string.IsNullOrEmpty(cycle.PendingCastleAssaultKingdomId))
        {
            return "";
        }
        if (CastleManeuvers.CampWarded(cycle))
            return $"The Keeper finds the castle in {ProvinceControl.Name(here)}, but the wards turn it aside.";
        var world = cycle.World;
        var home = ProvinceControl.ProvinceById(world, keeper.HomeProvinceId);
        string realm = SeatRealm(world);
        if (realm.Length == 0)
            realm = home?.KingdomId ?? "";
        var result = CastleThreats.ForceThreat(cycle, CastleThreatKind.KingdomAssault, realm);
        if (!result.Happened)
            return "";
        keeper.StruckLunation = cycle.Calendar?.CurrentLunation ?? 0;
        int back = Step(world, here.Index, home != null ? home.Index : here.Index);
        if (back == here.Index)
        {
            foreach (int v in here.Neighbors)
            {
                if (v >= 0 && v < world.Provinces.Count && (back == here.Index || v < back))
                    back = v;
            }
        }
        keeper.ProvinceId = world.Provinces[back].Id;
        keeper.KnownCastleProvinceId = "";
        GD.Print($"[Keeper] strikes the castle in {ProvinceControl.Name(here)}; draws back to {ProvinceControl.Name(world.Provinces[back])}.");
        return $"{ProvinceControl.LossMark}The Keeper falls on the castle in {ProvinceControl.Name(here)}. The crews scatter: "
               + $"the resupply stands at {WorldClock.ResupplyDaysLeft(cycle)} day(s), and the camp must be defended. "
               + $"The Keeper draws back to {ProvinceControl.Name(world.Provinces[back])} and loses the castle's trail.";
    }

    /// <summary>[DEBUG] Gate PV6's lever (Wake the Keeper): the Keeper wakes now, two hops from
    /// the castle (the lower index), knowing where the castle is. Returns a line for the log, or
    /// null where it cannot walk.</summary>
    public static string DebugWake(CycleState cycle)
    {
        if (!Walks(cycle))
            return null;
        int castle = CastleHops.CastleProvince(cycle);
        if (castle < 0)
            return null;
        var dist = Distances(cycle.World, castle);
        int pick = -1;
        for (int i = 0; i < dist.Length; i++)
        {
            if (dist[i] == 2)
            {
                pick = i;
                break;
            }
        }
        var home = pick >= 0 ? cycle.World.Provinces[pick] : WakePlace(cycle);
        if (home == null)
            return null;
        Wake(cycle, home);
        return $"The Keeper wakes in {ProvinceControl.Name(home)}, two hops from the castle. Its first move: {IntentText(cycle)}.";
    }
}
