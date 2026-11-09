using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// KingdomTickSimulation.Fronts.cs  (partial of KingdomTickSimulation)
//
// Purpose:        Fronts per province (docs/provinces_v1_8_fronts_
//                 per_province.md, slice PV3a). Where the world has
//                 provinces a front contests one province of the
//                 defender at a time (PR53): of the defender's
//                 provinces that border ground the aggressor rules, the
//                 one nearest the defender's capital on the province
//                 graph; ties go to the longer shared border, then to
//                 the lower index. At 100 the aggressor takes it and the
//                 front moves on at 60 (PR52); taking the capital fells
//                 the realm by the collapse rule (PR6, PR55). At 0 the
//                 invasion is thrown back and the aggressor keeps what
//                 it took, or a won Seize makes the province the guild's
//                 and turns the court Hostile (PR56). Kingdoms border
//                 where the provinces they rule touch, so a conqueror
//                 borders what it conquered. A world without provinces,
//                 a cache siege, and a front opened before fronts walked
//                 that finds no province keep the old rules in
//                 KingdomTickSimulation.cs.
// Layer:          System
// Collaborators:  KingdomTickSimulation.cs (Tick, OpenWarfront,
//                 ResolveWarfront and ApplyIntervention call in here),
//                 ProvinceControl.cs (rulers, the court's turn, the
//                 standing step), ProvinceInfluence.cs (SetProvince),
//                 SupplyCacheSystem.cs (NeighborsOf; the caches follow
//                 through ProvinceControl.SetRuler), Warfront.cs
//                 (ProvinceId), CycleState.cs (PendingWarfrontProvinceId),
//                 StrategicDebug.cs (DebugPush),
//                 ProvinceSaveAssert.Fronts.cs (OpenFront, Settle)
// See:            docs/provinces_v1_8_fronts_per_province.md
//                 sections 3.2 to 3.4 and 7
// ============================================================

public static partial class KingdomTickSimulation
{
    /// <summary>True when fronts walk province by province: the cycle's world has provinces.</summary>
    private static bool FrontsWalk(CycleState cycle)
        => cycle?.World != null && cycle.Kingdoms != null && cycle.World.HasProvinces();

    // ══════════════════════════════════════════════════════════════════════
    // Who borders whom
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Each province's ruler, by its index (ProvinceControl.RulerOf).</summary>
    private static string[] Rulers(CycleState cycle)
    {
        var provinces = cycle.World.Provinces;
        var rulers = new string[provinces.Count];
        for (int i = 0; i < rulers.Length; i++)
            rulers[i] = ProvinceControl.RulerOf(cycle, provinces[i]);
        return rulers;
    }

    /// <summary>Kingdom id to the kingdoms it borders: where provinces exist, the kingdoms whose
    /// ruled provinces touch (read afresh, since provinces change hands); otherwise the tile
    /// adjacency of old, cached per world.</summary>
    private static Dictionary<string, HashSet<string>> Adjacency(CycleState cycle)
    {
        if (FrontsWalk(cycle))
            return RuledAdjacency(cycle);
        EnsureAdjacency(cycle.World);
        return _adjacency;
    }

    /// <summary>The kingdoms whose ruled provinces touch. The guild's own land, land no one
    /// rules and land no kingdom owns border no one, so no realm presses on them and they press
    /// on no realm. A realm fallen with its capital rules nothing, so it borders no one.</summary>
    private static Dictionary<string, HashSet<string>> RuledAdjacency(CycleState cycle)
    {
        var provinces = cycle.World.Provinces;
        var rulers = Rulers(cycle);
        var adjacency = new Dictionary<string, HashSet<string>>();
        for (int i = 0; i < provinces.Count; i++)
        {
            string mine = rulers[i];
            if (!ProvinceControl.IsKingdom(mine))
                continue;
            if (!adjacency.TryGetValue(mine, out var set))
            {
                set = new HashSet<string>();
                adjacency[mine] = set;
            }
            foreach (int q in provinces[i].Neighbors)
            {
                if (q < 0 || q >= provinces.Count)
                    continue;
                string theirs = rulers[q];
                if (ProvinceControl.IsKingdom(theirs) && theirs != mine)
                    set.Add(theirs);
            }
        }
        return adjacency;
    }

    /// <summary>The kingdoms <paramref name="kid"/> borders, by the provinces they rule where
    /// the world has provinces (SupplyCacheSystem's envy pass reads it); the tile adjacency of
    /// old otherwise.</summary>
    public static IEnumerable<string> NeighborsOf(CycleState cycle, string kid)
    {
        if (cycle?.World == null || string.IsNullOrEmpty(kid))
            return Array.Empty<string>();
        if (!FrontsWalk(cycle))
            return NeighborsOf(cycle.World, kid);
        return RuledAdjacency(cycle).TryGetValue(kid, out var n)
            ? (IEnumerable<string>)n
            : Array.Empty<string>();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Which province, and where on it (PR53)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The province a front of the aggressor contests: of the provinces the defender
    /// rules that border one the aggressor rules, the one nearest the defender's capital on
    /// the province graph (the capital itself when it borders the aggressor); ties go to the
    /// longer shared border, counted in tile edges, then to the lower index. -1 when none
    /// borders the aggressor, or the world has no provinces.</summary>
    internal static int ContestedProvince(CycleState cycle, string defenderKid, string aggressorKid)
    {
        if (!FrontsWalk(cycle) || string.IsNullOrEmpty(defenderKid) || string.IsNullOrEmpty(aggressorKid))
            return -1;
        var world = cycle.World;
        var provinces = world.Provinces;
        var rulers = Rulers(cycle);
        int[] distance = null;
        int[] edges = null;
        int best = -1;
        for (int i = 0; i < provinces.Count; i++)
        {
            if (rulers[i] != defenderKid || !Borders(provinces[i], rulers, aggressorKid))
                continue;
            if (distance == null)
            {
                distance = GraphDistances(world, ProvinceControl.CapitalIndex(world, defenderKid));
                edges = BorderEdges(world, rulers, defenderKid, aggressorKid);
            }
            if (best < 0 || distance[i] < distance[best]
                || (distance[i] == distance[best] && edges[i] > edges[best]))
                best = i;
        }
        return best;
    }

    /// <summary>True when a neighbour of the province is ruled by <paramref name="ruler"/>.</summary>
    private static bool Borders(Province province, string[] rulers, string ruler)
    {
        foreach (int q in province.Neighbors)
        {
            if (q >= 0 && q < rulers.Length && rulers[q] == ruler)
                return true;
        }
        return false;
    }

    /// <summary>Steps on the province graph from a province to every other; int.MaxValue for
    /// one it cannot reach (an island). From no province (-1), every distance is 0.</summary>
    private static int[] GraphDistances(WorldData world, int from)
    {
        var provinces = world.Provinces;
        var distance = new int[provinces.Count];
        if (from < 0 || from >= provinces.Count)
            return distance;
        for (int i = 0; i < distance.Length; i++)
            distance[i] = int.MaxValue;
        distance[from] = 0;
        var frontier = new List<int> { from };
        while (frontier.Count > 0)
        {
            var next = new List<int>();
            foreach (int p in frontier)
            {
                foreach (int q in provinces[p].Neighbors)
                {
                    if (q < 0 || q >= distance.Length || distance[q] != int.MaxValue)
                        continue;
                    distance[q] = distance[p] + 1;
                    next.Add(q);
                }
            }
            frontier = next;
        }
        return distance;
    }

    /// <summary>For each province the defender rules, the tile edges it shares with ground the
    /// aggressor rules.</summary>
    private static int[] BorderEdges(WorldData world, string[] rulers, string defenderKid, string aggressorKid)
    {
        var edges = new int[rulers.Length];
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                int p = world.ProvinceIndexAt(x, y);
                if (p < 0 || rulers[p] != defenderKid)
                    continue;
                foreach (var (nx, ny) in HexCoord.Neighbors(x, y, world.Width, world.Height))
                {
                    int q = world.ProvinceIndexAt(nx, ny);
                    if (q >= 0 && rulers[q] == aggressorKid)
                        edges[p]++;
                }
            }
        }
        return edges;
    }

    /// <summary>Site a front on a province (PR53): the focus is the province's tile on its
    /// border with the aggressor's ground nearest the province's seat (the first in row order
    /// on a tie), where a party deploys; the stronghold is sited from it as before, in the
    /// aggressor's ground. (-1,...) when the province does not border the aggressor.</summary>
    private static (int fc, int fr, int sc, int sr) SiteProvinceFront(CycleState cycle, int index, string aggressorKid)
    {
        var world = cycle.World;
        var province = world.Provinces[index];
        var rulers = Rulers(cycle);
        int fc = -1, fr = -1, best = int.MaxValue;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                if (world.ProvinceIndexAt(x, y) != index)
                    continue;
                bool border = false;
                foreach (var (nx, ny) in HexCoord.Neighbors(x, y, world.Width, world.Height))
                {
                    int q = world.ProvinceIndexAt(nx, ny);
                    if (q >= 0 && rulers[q] == aggressorKid)
                    {
                        border = true;
                        break;
                    }
                }
                if (!border)
                    continue;
                int d = province.SeatX >= 0 ? HexCoord.OffsetDistance(x, y, province.SeatX, province.SeatY) : 0;
                if (d < best)
                {
                    best = d;
                    fc = x;
                    fr = y;
                }
            }
        }
        if (fc < 0)
            return (-1, -1, -1, -1);
        var (sc, sr) = SiteStronghold(world, rulers, fc, fr, aggressorKid);
        return (fc, fr, sc, sr);
    }

    /// <summary>The besieging stronghold, as FindFrontAndStronghold sites it, in the ground the
    /// aggressor rules: a tile 2 to 3 hexes from the front, reachable on foot, with no place on
    /// it preferred; else the nearest such tile.</summary>
    private static (int sc, int sr) SiteStronghold(WorldData world, string[] rulers, int x, int y, string aggressorKid)
    {
        var reachable = LandReachableFrom(world, x, y, StrongholdReachSteps);
        int sc = -1, sr = -1, bestScore = int.MaxValue;
        int fbC = -1, fbR = -1, fbD = int.MaxValue;
        for (int y2 = Math.Max(0, y - 4); y2 <= Math.Min(world.Height - 1, y + 4); y2++)
        {
            for (int x2 = Math.Max(0, x - 4); x2 <= Math.Min(world.Width - 1, x + 4); x2++)
            {
                int q = world.ProvinceIndexAt(x2, y2);
                if (q < 0 || rulers[q] != aggressorKid || world.GetTile(x2, y2).IsWater)
                    continue;
                if (!reachable.Contains((x2, y2)))
                    continue;
                int d = HexCoord.OffsetDistance(x, y, x2, y2);
                if (d > 0 && d < fbD)
                {
                    fbD = d;
                    fbC = x2;
                    fbR = y2;
                }
                if (d >= 2 && d <= 3)
                {
                    int score = Math.Abs(d - 3) + (world.PoiAt(x2, y2) != null ? 100 : 0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        sc = x2;
                        sr = y2;
                    }
                }
            }
        }
        if (sc < 0)
        {
            sc = fbC;
            sr = fbR;
        }
        return (sc, sr);
    }

    /// <summary>Put a front's focus and stronghold on a province.</summary>
    private static void Site(CycleState cycle, Warfront wf, int index)
    {
        var (fc, fr, sc, sr) = SiteProvinceFront(cycle, index, wf.AggressorKingdomId);
        wf.FocusCol = fc;
        wf.FocusRow = fr;
        wf.StrongholdCol = sc;
        wf.StrongholdRow = sr;
    }

    // ══════════════════════════════════════════════════════════════════════
    // The lunation
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Section 7: a front opened before fronts walked gets its province, the
    /// defender's province at its focus when there is one, keeping its focus and stronghold;
    /// otherwise the one PR53 picks, sited afresh. One with no province of the defender to
    /// contest keeps the old rules.</summary>
    internal static void MigrateLegacyFronts(CycleState cycle)
    {
        if (!FrontsWalk(cycle) || cycle.Warfronts == null)
            return;
        var world = cycle.World;
        foreach (var wf in cycle.Warfronts)
        {
            if (wf == null || wf.Closed || wf.IsCacheSiege || !string.IsNullOrEmpty(wf.ProvinceId))
                continue;
            int at = wf.HasFocus ? world.ProvinceIndexAt(wf.FocusCol, wf.FocusRow) : -1;
            bool atFocus = at >= 0 && ProvinceControl.RulerOf(cycle, world.Provinces[at]) == wf.DefenderKingdomId;
            if (!atFocus)
                at = ContestedProvince(cycle, wf.DefenderKingdomId, wf.AggressorKingdomId);
            if (at < 0)
                continue;
            wf.ProvinceId = world.Provinces[at].Id;
            if (!atFocus)
                Site(cycle, wf, at);
            GD.Print($"[Warfront] {wf.Id} opened before fronts walked; it contests {wf.ProvinceId} now.");
        }
    }

    /// <summary>Before a front advances: it ends when a realm on either side has fallen (the
    /// invasion collapses with its realm), and one whose province is no longer the defender's
    /// moves to the next by PR53, or ends when there is none. True when it closed.</summary>
    private static bool SettleRulers(CycleState cycle, Warfront wf)
    {
        if (!FrontsWalk(cycle) || wf.IsCacheSiege)
            return false;
        if (ProvinceControl.KingdomFallen(cycle, wf.AggressorKingdomId)
            || ProvinceControl.KingdomFallen(cycle, wf.DefenderKingdomId))
        {
            wf.Closed = true;
            wf.Resolution = "collapsed";
            Report(cycle, $"The invasion of {Named(wf.DefenderName, wf.DefenderKingdomId)} ends: a realm on one side has fallen.");
            return true;
        }
        if (string.IsNullOrEmpty(wf.ProvinceId))
            return false;
        int at = ProvinceControl.IndexOf(cycle.World, wf.ProvinceId);
        if (at >= 0 && ProvinceControl.RulerOf(cycle, cycle.World.Provinces[at]) == wf.DefenderKingdomId)
            return false;
        int next = ContestedProvince(cycle, wf.DefenderKingdomId, wf.AggressorKingdomId);
        if (next < 0)
        {
            wf.Closed = true;
            wf.Resolution = "halted";
            Report(cycle, $"The war on {Named(wf.DefenderName, wf.DefenderKingdomId)} ends: nothing of it lies within the invader's reach.");
            return true;
        }
        wf.ProvinceId = cycle.World.Provinces[next].Id;
        wf.PlayerSeizing = false;
        Site(cycle, wf, next);
        return false;
    }

    /// <summary>The guild's influence that relieves a front (provinces PV2a): since PV3a, in the
    /// province the front contests; for a front without one, at its focus or across the
    /// kingdom, as before.</summary>
    private static int ReliefInfluence(CycleState cycle, Warfront wf, KingdomState def)
    {
        if (FrontsWalk(cycle) && !string.IsNullOrEmpty(wf.ProvinceId))
        {
            var province = ProvinceControl.ProvinceById(cycle.World, wf.ProvinceId);
            if (province != null)
                return province.Influence;
        }
        return Math.Max(def.PlayerInfluence, ProvinceInfluence.InfluenceAt(cycle.World, wf.FocusCol, wf.FocusRow));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Settling a front on a province
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The boundary rules for a front that contests a province: taken at 100 (the
    /// front moves on, or the realm falls with its capital), thrown back or seized at 0,
    /// otherwise kept inside the bar. False for a cache siege or a front without a province,
    /// which the old rules settle.</summary>
    private static bool ResolveProvinceFront(CycleState cycle, Warfront wf, KingdomState def, Func<string, string> fd)
    {
        if (wf.IsCacheSiege || string.IsNullOrEmpty(wf.ProvinceId) || !FrontsWalk(cycle))
            return false;
        int at = ProvinceControl.IndexOf(cycle.World, wf.ProvinceId);
        if (at < 0)
        {
            // A province this world does not have: end the war rather than fell a realm whole.
            wf.Closed = true;
            wf.Resolution = "halted";
            GD.PushWarning($"[Warfront] {wf.Id} contests {wf.ProvinceId}, which this world does not have; the war ends.");
            return true;
        }
        if (wf.Advance >= 100)
        {
            TakeProvince(cycle, wf, def, at, fd);
        }
        else if (wf.Advance <= 0)
        {
            if (wf.PlayerSeizing)
                SeizeProvince(cycle, wf, def, at, fd);
            else
                RepelFront(cycle, wf, def, at);
        }
        else
        {
            wf.Advance = Mathf.Clamp(wf.Advance, 1, 99);
        }
        return true;
    }

    /// <summary>At 100 the aggressor takes the province (PR52, PR54): it rules it now, the
    /// guild's influence there drops to 0 and its hold with it, and the caches its old ruler
    /// held pass to the aggressor. The defender's capital fells the realm (Collapse); any
    /// other province moves the front on to the next at 60, or ends the war when nothing more
    /// of the defender lies within reach.</summary>
    private static void TakeProvince(CycleState cycle, Warfront wf, KingdomState def, int at, Func<string, string> fd)
    {
        var province = cycle.World.Provinces[at];
        string defName = RealmOf(def, wf.DefenderKingdomId);
        int mark = ReportCount(cycle);
        bool wasHeld = province.Held;
        ProvinceControl.SetRuler(cycle, province, wf.AggressorKingdomId, fd);
        // The guild's influence goes with the province (PR54), silently: the line below says so.
        ProvinceInfluence.SetProvince(cycle, at, 0);
        string loss = wasHeld ? ProvinceControl.LossMark : "";
        string lost = wasHeld ? " Your hold there is lost." : "";

        if (province.IsCapital && province.KingdomId == wf.DefenderKingdomId)
        {
            wf.Closed = true;
            wf.Resolution = "fell";
            ReportAt(cycle, mark, loss + Collapse(cycle, wf, def, province, wf.AggressorKingdomId, fd, lost));
            return;
        }

        string line = $"{loss}{AggressorName(cycle, wf, fd)} takes {ProvinceControl.Name(province)} from {defName}.{lost}";
        int next = ContestedProvince(cycle, wf.DefenderKingdomId, wf.AggressorKingdomId);
        if (next >= 0)
        {
            MoveFront(cycle, wf, next);
            line += $" The front moves on to {ProvinceControl.Name(cycle.World.Provinces[next])}.";
        }
        else
        {
            wf.Closed = true;
            wf.Resolution = "halted";
            line += $" Nothing more of {defName} lies within its reach, and the war ends there.";
        }
        ReportAt(cycle, mark, line);
    }

    /// <summary>The front moves on to the next province at FrontMomentumAdvance (PR52): an army
    /// that has just won carries its momentum. A bid to seize was for the province taken.</summary>
    private static void MoveFront(CycleState cycle, Warfront wf, int next)
    {
        wf.ProvinceId = cycle.World.Provinces[next].Id;
        wf.Advance = ProvinceControl.FrontMomentumAdvance;
        wf.PlayerSeizing = false;
        Site(cycle, wf, next);
    }

    /// <summary>A won Seize driven to 0 (PR56): the guild rules the province (influence 100,
    /// held), the war ends, the archmage's sentiment -12 as before, and the defender's court
    /// turns Hostile (P2). A seized capital fells the realm, with the guild as the taker.</summary>
    private static void SeizeProvince(CycleState cycle, Warfront wf, KingdomState def, int at, Func<string, string> fd)
    {
        var province = cycle.World.Provinces[at];
        string name = ProvinceControl.Name(province);
        int mark = ReportCount(cycle);
        // Sentiment: seizing an archmage's land for the guild is a hostile act, as it was.
        ShiftDefenderSentiment(cycle, def, -12);
        ProvinceControl.SetRuler(cycle, province, ProvinceControl.Guild, fd);
        ProvinceInfluence.SetProvince(cycle, at, ProvinceInfluence.MaxInfluence);
        bool hostile = ProvinceControl.TurnHostile(cycle, wf.DefenderKingdomId, name);
        wf.Closed = true;
        wf.Resolution = "seized";
        string turned = hostile ? " Its court turns Hostile." : "";
        string line = province.IsCapital && province.KingdomId == wf.DefenderKingdomId
            ? Collapse(cycle, wf, def, province, ProvinceControl.Guild, fd, turned)
            : $"{ProvinceControl.GainMark}The guild takes {name} from the war on {RealmOf(def, wf.DefenderKingdomId)}.{turned}";
        ReportAt(cycle, mark, line);
    }

    /// <summary>At 0 without a bid the invasion is thrown back (stability +10, sentiment +8, as
    /// before) and the war ends. The aggressor keeps what it took (PR52).</summary>
    private static void RepelFront(CycleState cycle, Warfront wf, KingdomState def, int at)
    {
        ShiftDefenderSentiment(cycle, def, +8);
        def.Stability = Mathf.Min(100, def.Stability + 10);
        wf.Closed = true;
        wf.Resolution = "repelled";
        Report(cycle, $"The invasion of {RealmOf(def, wf.DefenderKingdomId)} is thrown back at "
                      + $"{ProvinceControl.Name(cycle.World.Provinces[at])}.");
    }

    /// <summary>A realm falls with its capital (section 3.3: PR6 as ruled 2026-11-03, PR55).
    /// The taker (the aggressor, or the guild by a Seize) holds the capital already. What the
    /// aggressor took stays its; each province the fallen realm still rules goes to the guild
    /// when the guild holds it, else to no one; the realm stops ticking, and its faction becomes
    /// the taker's, so older readers see the victor; the aggressor's court and every court whose
    /// realm borders a province the guild gained lose one standing step; its other wars end.
    /// Returns the news.</summary>
    private static string Collapse(CycleState cycle, Warfront wf, KingdomState def, Province capital,
                                   string taker, Func<string, string> fd, string capitalNote)
    {
        string fallenKid = wf.DefenderKingdomId;
        string fallen = RealmOf(def, fallenKid);
        var provinces = cycle.World.Provinces;

        // Sentiment: a realm falling is a major blow, as a fall always was.
        ShiftDefenderSentiment(cycle, def, -15);

        var gained = new List<int>();
        int none = 0;
        for (int i = 0; i < provinces.Count; i++)
        {
            var province = provinces[i];
            if (province == null || ProvinceControl.RulerOf(cycle, province) != fallenKid)
                continue;
            if (province.Held)
            {
                ProvinceControl.SetRuler(cycle, province, ProvinceControl.Guild, fd);
                gained.Add(i);
            }
            else
            {
                ProvinceControl.SetRuler(cycle, province, ProvinceControl.Neutral, fd);
                none++;
            }
        }
        int kept = 0;
        foreach (var province in provinces)
        {
            if (province != null && province.KingdomId == fallenKid && ProvinceControl.RulerOf(cycle, province) == taker)
                kept++;
        }

        // The realm stops ticking; older readers see the victor.
        def.ControllingFactionId = taker == ProvinceControl.Guild ? GuildFactionId : TakerFaction(cycle, wf, taker);
        def.Stability = SiegeStabilityFloor;
        def.BorderPressure.Clear();

        // PR20's second half (PR55): once each, the aggressor's court and every court whose realm
        // borders a province the guild gained.
        var gainedNames = new List<string>();
        foreach (int g in gained)
            gainedNames.Add(ProvinceControl.Name(provinces[g]));
        if (gained.Count > 0)
        {
            var courts = new List<string>();
            AddCourt(courts, wf.AggressorKingdomId, fallenKid);
            foreach (int g in gained)
            {
                foreach (int q in provinces[g].Neighbors)
                {
                    if (q >= 0 && q < provinces.Count)
                        AddCourt(courts, ProvinceControl.RulerOf(cycle, provinces[q]), fallenKid);
                }
            }
            string list = NameList(gainedNames);
            foreach (string kid in courts)
                ProvinceControl.StandingStep(cycle, kid, list, fallen);
        }

        CloseWarsOf(cycle, fallenKid, wf, fallen);

        var sb = new StringBuilder();
        if (taker == ProvinceControl.Guild)
        {
            sb.Append($"{ProvinceControl.GainMark}{fallen} falls with {ProvinceControl.Name(capital)}, "
                      + $"which the guild took from the war.{capitalNote}");
        }
        else
        {
            string keeps = kept == 1 ? "1 province" : kept + " provinces";
            sb.Append($"{fallen} falls with {ProvinceControl.Name(capital)}.{capitalNote} "
                      + $"{AggressorName(cycle, wf, fd)} keeps {keeps}.");
        }
        if (gained.Count > 0)
            sb.Append($" The guild rules {NameList(gainedNames)}, which it held.");
        if (none > 0)
            sb.Append(none == 1 ? " 1 goes to no one." : $" {none} go to no one.");
        GD.Print($"[Warfront] {fallenKid} fell with {capital.Id}: the taker keeps {kept}, the guild {gained.Count}, no one {none}.");
        return sb.ToString();
    }

    /// <summary>A court for the standing step: a kingdom's, other than the fallen realm's, once.</summary>
    private static void AddCourt(List<string> courts, string kid, string fallenKid)
    {
        if (ProvinceControl.IsKingdom(kid) && kid != fallenKid && !courts.Contains(kid))
            courts.Add(kid);
    }

    /// <summary>The faction a fallen realm takes: its taker's.</summary>
    private static string TakerFaction(CycleState cycle, Warfront wf, string taker)
        => cycle.Kingdoms.TryGetValue(taker ?? "", out var kingdom) && kingdom != null
           && !string.IsNullOrEmpty(kingdom.ControllingFactionId)
            ? kingdom.ControllingFactionId
            : wf.AggressorFactionId;

    /// <summary>A fallen realm's other wars end with it: its invasions collapse, the war on it
    /// is over, and its sieges of caches lift. Sieges of caches on its land go on: the cache's
    /// holder defends them.</summary>
    private static void CloseWarsOf(CycleState cycle, string fallenKid, Warfront current, string fallen)
    {
        foreach (var other in cycle.Warfronts)
        {
            if (other == null || other == current || other.Closed)
                continue;
            bool attacker = other.AggressorKingdomId == fallenKid;
            if (!attacker && (other.IsCacheSiege || other.DefenderKingdomId != fallenKid))
                continue;
            other.Closed = true;
            other.Resolution = "collapsed";
            Report(cycle, other.IsCacheSiege
                ? $"The siege of {Named(other.DefenderName, "a supply cache")} lifts: {fallen} has fallen."
                : attacker
                    ? $"The invasion of {Named(other.DefenderName, other.DefenderKingdomId)} collapses with its realm."
                    : $"The war on {fallen} ends with its fall.");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Interventions
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>PR52: a party that went to defend or take a province (PendingWarfrontProvinceId)
    /// and returns after the front moved on to another finds it gone, as one that finds its
    /// front over does, and the swing is not applied. True when it moved on.</summary>
    private static bool FrontMovedOn(CycleState cycle, Warfront wf)
    {
        string went = cycle.PendingWarfrontProvinceId ?? "";
        if (wf.IsCacheSiege || went.Length == 0 || string.IsNullOrEmpty(wf.ProvinceId) || went == wf.ProvinceId)
            return false;
        string then = Named(ProvinceControl.Name(ProvinceControl.ProvinceById(cycle.World, went)), went);
        string now = Named(ProvinceControl.Name(ProvinceControl.ProvinceById(cycle.World, wf.ProvinceId)), wf.ProvinceId);
        Report(cycle, $"You reach the front at {then}, but it has moved on: the fighting is in {now} now.");
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // The asserts and the debug lever
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>For the asserts: open a front of the aggressor against the defender now, by the
    /// rule the lunation uses when pressure boils over. Returns the front, or null.</summary>
    internal static Warfront OpenFront(CycleState cycle, string defenderKid, string aggressorKid,
                                       Func<string, string> fd = null)
    {
        if (cycle?.Kingdoms == null || string.IsNullOrEmpty(defenderKid)
            || !cycle.Kingdoms.TryGetValue(defenderKid, out var def) || def == null)
            return null;
        cycle.Warfronts ??= new List<Warfront>();
        cycle.PendingSiegeReports ??= new List<string>();
        string faction = !string.IsNullOrEmpty(aggressorKid) && cycle.Kingdoms.TryGetValue(aggressorKid, out var agg)
                         && agg != null ? agg.ControllingFactionId : "";
        OpenWarfront(cycle, def, defenderKid, aggressorKid, faction, fd);
        return cycle.Warfronts[cycle.Warfronts.Count - 1];
    }

    /// <summary>For the asserts and the debug lever: settle a front at its Advance by the rules
    /// a lunation applies, then drop it if it closed.</summary>
    internal static void Settle(CycleState cycle, Warfront wf, Func<string, string> fd = null)
    {
        if (cycle?.Kingdoms == null || wf == null || string.IsNullOrEmpty(wf.DefenderKingdomId)
            || !cycle.Kingdoms.TryGetValue(wf.DefenderKingdomId, out var def) || def == null)
            return;
        cycle.Warfronts ??= new List<Warfront>();
        cycle.PendingSiegeReports ??= new List<string>();
        ResolveWarfront(cycle, wf, def, fd);
        cycle.Warfronts.RemoveAll(w => w.Closed);
    }

    /// <summary>[DEBUG] Gate PV3a's lever (Push Front +40): the first open war (not a cache
    /// siege) moves by <paramref name="amount"/> toward its aggressor and settles at once by the
    /// rules a lunation applies, so a gate need not wait out lunations. Returns a line for the
    /// log with the news it made, or null when no war is open.</summary>
    public static string DebugPush(CycleState cycle, int amount, Func<string, string> fd = null)
    {
        if (cycle?.Warfronts == null || cycle.Kingdoms == null)
            return null;
        MigrateLegacyFronts(cycle);
        Warfront front = null;
        foreach (var wf in cycle.Warfronts)
        {
            if (wf != null && !wf.Closed && !wf.IsCacheSiege)
            {
                front = wf;
                break;
            }
        }
        if (front == null)
            return null;
        cycle.PendingSiegeReports ??= new List<string>();
        int before = cycle.PendingSiegeReports.Count;
        int from = front.Advance;
        string where = ProvinceControl.Name(ProvinceControl.ProvinceById(cycle.World, front.ProvinceId));
        front.Advance += amount;
        Settle(cycle, front, fd);
        var news = cycle.PendingSiegeReports.GetRange(before, cycle.PendingSiegeReports.Count - before);
        return $"Pushed the front {front.Id}{(where.Length > 0 ? " in " + where : "")} from {from} by {amount}."
               + (news.Count > 0 ? " " + string.Join(" ", news) : " It stands at " + front.Advance + ".");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    private static int ReportCount(CycleState cycle) => cycle.PendingSiegeReports?.Count ?? 0;

    private static void Report(CycleState cycle, string line) => ReportAt(cycle, -1, line);

    /// <summary>A line of frontier news, at a place in the list when one is given: a taking's
    /// line goes before the caches' lines it caused.</summary>
    private static void ReportAt(CycleState cycle, int at, string line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        cycle.PendingSiegeReports ??= new List<string>();
        if (at >= 0 && at <= cycle.PendingSiegeReports.Count)
            cycle.PendingSiegeReports.Insert(at, line);
        else
            cycle.PendingSiegeReports.Add(line);
        GD.Print($"[Warfront] {line}");
    }

    /// <summary>The defender's archmage's sentiment, as the front's outcomes always moved it.</summary>
    private static void ShiftDefenderSentiment(CycleState cycle, KingdomState def, int delta)
    {
        var campaign = cycle?.Campaign;
        if (campaign == null || def == null)
            return;
        string archmage = campaign.GetArchmageForRegion(def.TemplateRegionId ?? "");
        if (!string.IsNullOrEmpty(archmage))
            campaign.ShiftSentiment(archmage, delta);
    }

    /// <summary>The aggressor's name, as the war's news names it: its kingdom's ruling faction.</summary>
    private static string AggressorName(CycleState cycle, Warfront wf, Func<string, string> fd)
    {
        if (cycle.Kingdoms.TryGetValue(wf.AggressorKingdomId ?? "", out var kingdom) && kingdom != null
            && !string.IsNullOrEmpty(kingdom.ControllingFactionId))
            return ResolveName(fd, kingdom.ControllingFactionId);
        return !string.IsNullOrEmpty(wf.AggressorName) ? wf.AggressorName : ResolveName(fd, wf.AggressorFactionId);
    }

    private static string RealmOf(KingdomState def, string kid)
        => def == null || string.IsNullOrEmpty(def.DisplayName) ? kid : def.DisplayName;

    private static string Named(string name, string fallback) => string.IsNullOrEmpty(name) ? fallback : name;

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string NameList(List<string> names)
    {
        if (names.Count == 0)
            return "";
        if (names.Count == 1)
            return names[0];
        return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
    }
}
