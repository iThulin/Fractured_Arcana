using Godot;
using System;
using System.Collections.Generic;
using System.Text;

// ============================================================
// ProvinceInfluence.cs
//
// Purpose:        The guild's influence, held province by province
//                 (docs/provinces_v1.md section 7b, slice PV2a).
//                 Each province carries Influence (0 to 100) and
//                 Held: held at 60, lost below 50. A kingdom's
//                 PlayerInfluence, which every older reader still
//                 reads, is derived from its provinces: their mean,
//                 weighted by tiles. Each lunation the castle's
//                 presence raises its province and the provinces
//                 beside it, a held province steadies its neighbours,
//                 and influence fades where the castle is not. None
//                 of the passive gains lifts a province past 40, so
//                 holding takes deeds, and a deed lands on the
//                 province where it happens.
// Layer:          System
// Collaborators:  Province.cs (Influence, Held), KingdomState.cs
//                 (PlayerInfluence, PlayerInfluenceDerived),
//                 KingdomTickSimulation.cs (interventions, falls,
//                 seizures, the front relief), ExpeditionManager.cs
//                 (secured sites, shard vaults), StrategicView.cs
//                 (the lunation tick, the place card),
//                 CycleInitializer.cs (Sync when a cycle opens),
//                 WorldAtlas3D.cs (the Reach lens),
//                 CampusGuildPanel.cs (report, debug deed),
//                 ProvinceSaveAssert.cs
// See:            docs/provinces_v1.md section 7b
// ============================================================

/// <summary>Moves the guild's influence in each province and keeps each kingdom's
/// PlayerInfluence equal to the tile-weighted mean of its provinces. Stateless: every
/// value lives in the save (Province, KingdomState).</summary>
public static class ProvinceInfluence
{
    // ── Tuning (docs/provinces_v1.md section 7b) ─────────────────────────
    /// <summary>A province is held at this influence or more (ruling P2).</summary>
    public const int HoldLine = 60;

    /// <summary>A held province stays held until its influence falls below this (PR3).</summary>
    public const int LoseLine = 50;

    public const int MaxInfluence = 100;

    /// <summary>Each lunation the castle's province gains this much.</summary>
    public const int PresenceGain = 8;

    /// <summary>Each lunation each province beside the castle's gains this much, across
    /// kingdom borders too.</summary>
    public const int PresenceNeighborGain = 3;

    /// <summary>Each lunation a province gains this much for each neighbour the guild holds.</summary>
    public const int HeldNeighborGain = 1;

    /// <summary>Presence and held neighbours never lift a province past this, which is
    /// below the hold line: holding takes deeds (the idle loop, section 0).</summary>
    public const int PassiveCap = 40;

    /// <summary>Each lunation a province neither under nor beside the castle loses this
    /// much, down to 0, unless the guild rules its kingdom.</summary>
    public const int Decay = 2;

    /// <summary>How far, in hexes, a deed on land outside every province (water opened
    /// since the build) or a front's focus looks for a province to count for.</summary>
    public const int NearRadius = 12;

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>True when the cycle's world has provinces, so influence lives on them. A
    /// world without provinces keeps the kingdom-wide influence of old.</summary>
    public static bool Active(CycleState cycle)
        => cycle?.World != null && cycle.Kingdoms != null && cycle.World.HasProvinces();

    /// <summary>The guild's influence in the province holding a tile, or 0 where none does.</summary>
    public static int InfluenceAt(WorldData world, int x, int y)
    {
        var province = world?.ProvinceAt(x, y);
        return province != null ? province.Influence : 0;
    }

    /// <summary>True when the guild holds any province of the kingdom.</summary>
    public static bool HoldsAny(WorldData world, string kingdomId)
    {
        if (world == null || string.IsNullOrEmpty(kingdomId) || !world.HasProvinces())
            return false;
        foreach (var province in world.Provinces)
        {
            if (province.Held && province.KingdomId == kingdomId)
                return true;
        }
        return false;
    }

    /// <summary>Position in WorldData.Provinces of the province the castle stands in, or
    /// -1 when the castle is not on the map. On land outside every province, the nearest
    /// province within NearRadius.</summary>
    public static int CastleProvince(CycleState cycle)
    {
        if (cycle?.World == null || cycle.CastleX < 0 || cycle.CastleY < 0)
            return -1;
        return IndexNear(cycle.World, cycle.CastleX, cycle.CastleY, null);
    }

    // ══════════════════════════════════════════════════════════════════════
    // The bridge and the lunation
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Bring every province up to date with its kingdom's PlayerInfluence, then
    /// derive each kingdom's value again. A difference between PlayerInfluence and
    /// PlayerInfluenceDerived is a write made outside the province layer, or a save from
    /// before provinces carried influence: the whole difference is added to every province
    /// of the kingdom, so the kingdom's mean moves by it. That is also the migration: a
    /// legacy save starts every province at its kingdom's value. Hold changes go to lines
    /// when it is given; the opening of a cycle passes none. True when anything changed,
    /// so the caller knows to save.</summary>
    public static bool Sync(CycleState cycle, List<string> lines = null)
    {
        if (!Active(cycle))
            return false;
        bool changed = false;
        foreach (var kv in cycle.Kingdoms)
            changed |= Absorb(cycle, kv.Key, kv.Value);
        changed |= SettleAll(cycle, lines);
        return changed;
    }

    /// <summary>One lunation (docs/provinces_v1.md section 7b): Sync; the castle's
    /// presence (+8 in its province, +3 in each province beside it) and the held
    /// neighbours (+1 each), none of it past PassiveCap; decay (-2) where the castle is
    /// neither in nor beside the province, except in a kingdom the guild rules; the hold
    /// lines; each kingdom's value derived again. Returns the frontier news: a line for
    /// each province gained or lost. StrategicView.RunLunationTick calls it once a
    /// lunation, before the kingdom tick.</summary>
    public static List<string> Tick(CycleState cycle)
    {
        var lines = new List<string>();
        if (!Active(cycle))
            return lines;
        Sync(cycle, lines);

        var provinces = cycle.World.Provinces;
        int count = provinces.Count;
        int castle = CastleProvince(cycle);
        var near = new bool[count];
        if (castle >= 0)
        {
            near[castle] = true;
            foreach (int q in provinces[castle].Neighbors)
            {
                if (q >= 0 && q < count)
                    near[q] = true;
            }
        }
        var heldBefore = new bool[count];
        for (int i = 0; i < count; i++)
            heldBefore[i] = provinces[i].Held;

        for (int i = 0; i < count; i++)
        {
            var province = provinces[i];
            int passive = i == castle ? PresenceGain : near[i] ? PresenceNeighborGain : 0;
            foreach (int q in province.Neighbors)
            {
                if (q >= 0 && q < count && heldBefore[q])
                    passive += HeldNeighborGain;
            }
            int value = province.Influence;
            if (passive > 0 && value < PassiveCap)
                value = Math.Min(PassiveCap, value + passive);
            if (!near[i] && !GuildRules(cycle, province.KingdomId))
                value = Math.Max(0, value - Decay);
            province.Influence = Clamp(value);
        }

        SettleAll(cycle, lines);
        return lines;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Deeds
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>A deed at (x,y): the guild gains (or, for a negative amount, loses)
    /// influence in the province there. On land outside every province (water opened
    /// since the build), the nearest province within NearRadius counts. Returns the news
    /// when a province crosses the hold line or the loss line, else ""; the news also goes
    /// to the frontier news (CycleState.PendingSiegeReports). In a world without provinces
    /// the tile's kingdom gains, as before provinces.</summary>
    public static string Add(CycleState cycle, int x, int y, int amount)
    {
        if (cycle?.World == null)
            return "";
        if (!Active(cycle))
        {
            AddLegacy(cycle, TileKingdom(cycle.World, x, y), amount);
            return "";
        }
        return AddToProvince(cycle, IndexNear(cycle.World, x, y, null), amount);
    }

    /// <summary>A deed for a kingdom near (x,y): the guild gains in the kingdom's province
    /// nearest that tile, or in its capital province when the tile is off the map or no
    /// province of the kingdom lies within NearRadius of it. A front uses it with its
    /// focus, a border tile of the defender. Returns the news as Add does.</summary>
    public static string AddNear(CycleState cycle, string kingdomId, int x, int y, int amount)
    {
        if (cycle?.World == null || string.IsNullOrEmpty(kingdomId))
            return "";
        if (!Active(cycle))
        {
            AddLegacy(cycle, kingdomId, amount);
            return "";
        }
        int at = IndexNear(cycle.World, x, y, kingdomId);
        if (at < 0)
            at = CapitalIndex(cycle.World, kingdomId);
        if (at < 0)
        {
            AddLegacy(cycle, kingdomId, amount);
            return "";
        }
        return AddToProvince(cycle, at, amount);
    }

    /// <summary>Set the guild's influence in every province of a kingdom at once: 0 when
    /// it falls, 100 when the guild seizes it. The hold follows silently, because the fall
    /// or the seizure reports itself. In a world without provinces the kingdom's value is
    /// set, as before provinces.</summary>
    public static void SetKingdom(CycleState cycle, string kingdomId, int value)
    {
        if (cycle?.Kingdoms == null || string.IsNullOrEmpty(kingdomId))
            return;
        value = Clamp(value);
        bool any = false;
        if (Active(cycle))
        {
            foreach (var province in cycle.World.Provinces)
            {
                if (province.KingdomId != kingdomId)
                    continue;
                province.Influence = value;
                UpdateHeld(cycle, province);
                any = true;
            }
        }
        if (!cycle.Kingdoms.TryGetValue(kingdomId, out var kingdom) || kingdom == null)
            return;
        if (any)
            Derive(cycle, kingdomId, kingdom);
        else
            kingdom.PlayerInfluence = value;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Text
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The place card's line for a province: its influence, and the line that
    /// holds it or loses it.</summary>
    public static string StandingLine(Province province)
    {
        if (province == null)
            return "";
        string name = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
        return province.Held
            ? $"The guild holds {name}: influence {province.Influence}, lost below {LoseLine}."
            : $"Guild influence in {name}: {province.Influence}, held at {HoldLine}.";
    }

    /// <summary>A plain-text account for the guild panel's Province Influence button: the
    /// castle's province, the rules, and every kingdom's value with its provinces'.</summary>
    public static string Report(CycleState cycle)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PROVINCE INFLUENCE (docs/provinces_v1.md section 7b) ===");
        if (!Active(cycle))
        {
            sb.AppendLine("The active world has no provinces, so the guild's influence is still kingdom-wide.");
            return sb.ToString();
        }
        var world = cycle.World;
        var provinces = world.Provinces;
        int castle = CastleProvince(cycle);
        var near = new bool[provinces.Count];
        if (castle >= 0)
        {
            near[castle] = true;
            foreach (int q in provinces[castle].Neighbors)
            {
                if (q >= 0 && q < provinces.Count)
                    near[q] = true;
            }
        }
        int held = 0;
        foreach (var province in provinces)
        {
            if (province.Held)
                held++;
        }
        sb.AppendLine(castle >= 0
            ? $"The castle stands in {Label(cycle, provinces[castle])}."
            : "The castle is not on the map.");
        sb.AppendLine($"Held: {held} of {provinces.Count} provinces. Held at {HoldLine}, lost below {LoseLine}. " +
                      $"Each lunation: +{PresenceGain} in the castle's province, +{PresenceNeighborGain} beside it, " +
                      $"+{HeldNeighborGain} per held neighbour, none of it past {PassiveCap}; -{Decay} away from the castle.");

        var kingdomIds = new List<string>(cycle.Kingdoms.Keys);
        kingdomIds.Sort(string.CompareOrdinal);
        foreach (string kingdomId in kingdomIds)
        {
            var kingdom = cycle.Kingdoms[kingdomId];
            if (kingdom == null)
                continue;
            string realm = string.IsNullOrEmpty(kingdom.DisplayName) ? kingdomId : kingdom.DisplayName;
            int pending = kingdom.PlayerInfluence - kingdom.PlayerInfluenceDerived;
            sb.AppendLine($"{realm} ({kingdomId}): influence {kingdom.PlayerInfluence}"
                          + (GuildRules(cycle, kingdomId) ? ", ruled by the guild" : "")
                          + (pending != 0 ? $", {pending:+#;-#;0} not yet spread over its provinces" : ""));
            for (int i = 0; i < provinces.Count; i++)
            {
                var province = provinces[i];
                if (province.KingdomId != kingdomId)
                    continue;
                string name = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
                string where = i == castle ? "  castle" : near[i] ? "  beside the castle" : "";
                sb.AppendLine($"  {name,-24} {province.Influence,3}{(province.Held ? "  held" : "")}{where}");
            }
        }
        return sb.ToString();
    }

    /// <summary>[DEBUG] A deed of the given amount in the castle's province, as the guild
    /// panel's Province Deed button gives it. Returns a line for the log.</summary>
    public static string DebugDeed(CycleState cycle, int amount)
    {
        if (!Active(cycle))
            return "[Debug] The active world has no provinces, so there is no province to work.";
        int at = CastleProvince(cycle);
        if (at < 0)
            return "[Debug] The castle is not on the map, so there is no province to work.";
        var province = cycle.World.Provinces[at];
        int before = province.Influence;
        string news = AddToProvince(cycle, at, amount);
        return $"[Debug] Deed in {Label(cycle, province)}: influence {before} to {province.Influence}."
               + (news.Length > 0 ? " " + news : "");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals
    // ══════════════════════════════════════════════════════════════════════

    private static int Clamp(int value) => Math.Clamp(value, 0, MaxInfluence);

    /// <summary>True when the guild rules the kingdom (a won Seize): its provinces are
    /// sovereign land and do not decay.</summary>
    private static bool GuildRules(CycleState cycle, string kingdomId)
        => !string.IsNullOrEmpty(kingdomId) && cycle.Kingdoms.TryGetValue(kingdomId, out var kingdom)
           && kingdom != null && kingdom.ControllingFactionId == KingdomTickSimulation.GuildFactionId;

    /// <summary>The deed itself: a write made outside the province layer is spread first,
    /// then the amount lands, then the kingdom's provinces settle their hold (reported)
    /// and its value is derived again.</summary>
    private static string AddToProvince(CycleState cycle, int index, int amount)
    {
        var provinces = cycle.World.Provinces;
        if (index < 0 || index >= provinces.Count)
            return "";
        var province = provinces[index];
        string kingdomId = province.KingdomId ?? "";
        cycle.Kingdoms.TryGetValue(kingdomId, out var kingdom);
        Absorb(cycle, kingdomId, kingdom);
        province.Influence = Clamp(province.Influence + amount);

        var news = new List<string>();
        foreach (var other in provinces)
        {
            if ((other.KingdomId ?? "") != kingdomId)
                continue;
            string line = UpdateHeld(cycle, other);
            if (line.Length > 0)
                news.Add(line);
        }
        Derive(cycle, kingdomId, kingdom);
        if (news.Count == 0)
            return "";
        cycle.PendingSiegeReports ??= new List<string>();
        foreach (string line in news)
        {
            cycle.PendingSiegeReports.Add(line);
            GD.Print($"[Provinces] {line}");
        }
        return string.Join(" ", news);
    }

    /// <summary>Spread a kingdom's undelivered difference (PlayerInfluence against
    /// PlayerInfluenceDerived) over its provinces, all of it to each. True when a
    /// province changed. A kingdom without provinces keeps its own value.</summary>
    private static bool Absorb(CycleState cycle, string kingdomId, KingdomState kingdom)
    {
        if (kingdom == null || string.IsNullOrEmpty(kingdomId))
            return false;
        int delta = kingdom.PlayerInfluence - kingdom.PlayerInfluenceDerived;
        if (delta == 0)
            return false;
        bool changed = false;
        foreach (var province in cycle.World.Provinces)
        {
            if (province.KingdomId != kingdomId)
                continue;
            int value = Clamp(province.Influence + delta);
            changed |= value != province.Influence;
            province.Influence = value;
        }
        return changed;
    }

    /// <summary>Every province's hold against the lines, reported into lines when it is
    /// given, then every kingdom's value derived. True when anything changed.</summary>
    private static bool SettleAll(CycleState cycle, List<string> lines)
    {
        bool changed = false;
        foreach (var province in cycle.World.Provinces)
        {
            string line = UpdateHeld(cycle, province);
            if (line.Length == 0)
                continue;
            changed = true;
            lines?.Add(line);
            GD.Print($"[Provinces] {line}");
        }
        foreach (var kv in cycle.Kingdoms)
            changed |= Derive(cycle, kv.Key, kv.Value);
        return changed;
    }

    /// <summary>Held at HoldLine, lost below LoseLine. Returns the news of a change, else "".</summary>
    private static string UpdateHeld(CycleState cycle, Province province)
    {
        if (!province.Held && province.Influence >= HoldLine)
        {
            province.Held = true;
            return $"The guild now holds {Label(cycle, province)}.";
        }
        if (province.Held && province.Influence < LoseLine)
        {
            province.Held = false;
            return $"The guild's hold on {Label(cycle, province)} is lost.";
        }
        return "";
    }

    /// <summary>A kingdom's value: the mean of its provinces' influence weighted by their
    /// tiles, rounded half up, written to PlayerInfluence and PlayerInfluenceDerived. A
    /// kingdom without provinces keeps its own value. True when either field changed.</summary>
    private static bool Derive(CycleState cycle, string kingdomId, KingdomState kingdom)
    {
        if (kingdom == null || string.IsNullOrEmpty(kingdomId))
            return false;
        long sum = 0, tiles = 0;
        foreach (var province in cycle.World.Provinces)
        {
            if (province.KingdomId != kingdomId || province.TileCount <= 0)
                continue;
            sum += (long)province.Influence * province.TileCount;
            tiles += province.TileCount;
        }
        if (tiles == 0)
            return false;
        int mean = (int)((2 * sum + tiles) / (2 * tiles));
        bool changed = kingdom.PlayerInfluence != mean || kingdom.PlayerInfluenceDerived != mean;
        kingdom.PlayerInfluence = mean;
        kingdom.PlayerInfluenceDerived = mean;
        return changed;
    }

    /// <summary>Before provinces (or in a world without them): the kingdom's own value.</summary>
    private static void AddLegacy(CycleState cycle, string kingdomId, int amount)
    {
        if (cycle?.Kingdoms == null || string.IsNullOrEmpty(kingdomId)
            || !cycle.Kingdoms.TryGetValue(kingdomId, out var kingdom) || kingdom == null)
            return;
        kingdom.PlayerInfluence = Clamp(kingdom.PlayerInfluence + amount);
    }

    private static string TileKingdom(WorldData world, int x, int y)
        => world.InBounds(x, y) && world.Tiles != null && world.Tiles.Length == world.Width * world.Height
            ? world.Tiles[y * world.Width + x].KingdomId ?? ""
            : "";

    /// <summary>The province holding (x,y) when it belongs to the kingdom (any kingdom
    /// when kingdomId is null); otherwise the nearest province of that kingdom within
    /// NearRadius hexes, by a breadth-first walk over land and water alike in direction
    /// order, so a tile always finds the same province. -1 when there is none.</summary>
    private static int IndexNear(WorldData world, int x, int y, string kingdomId)
    {
        if (world == null || !world.HasProvinces() || !world.InBounds(x, y))
            return -1;
        int here = world.ProvinceIndexAt(x, y);
        if (here >= 0 && (kingdomId == null || world.Provinces[here].KingdomId == kingdomId))
            return here;
        int w = world.Width;
        var seen = new HashSet<int> { y * w + x };
        var frontier = new List<(int x, int y)> { (x, y) };
        for (int ring = 1; ring <= NearRadius && frontier.Count > 0; ring++)
        {
            var next = new List<(int x, int y)>();
            foreach (var (cx, cy) in frontier)
            {
                var (q, r) = HexCoord.OffsetToAxial(cx, cy);
                for (int d = 0; d < 6; d++)
                {
                    var (dq, dr) = HexCoord.AxialDirections[d];
                    var (nx, ny) = HexCoord.AxialToOffset(q + dq, r + dr);
                    if (!world.InBounds(nx, ny) || !seen.Add(ny * w + nx))
                        continue;
                    int p = world.ProvinceIndexAt(nx, ny);
                    if (p >= 0 && (kingdomId == null || world.Provinces[p].KingdomId == kingdomId))
                        return p;
                    next.Add((nx, ny));
                }
            }
            frontier = next;
        }
        return -1;
    }

    /// <summary>The kingdom's capital province, or its first province when it has no
    /// capital, or -1 when it has none.</summary>
    private static int CapitalIndex(WorldData world, string kingdomId)
    {
        int first = -1;
        for (int i = 0; i < world.Provinces.Count; i++)
        {
            if (world.Provinces[i].KingdomId != kingdomId)
                continue;
            if (world.Provinces[i].IsCapital)
                return i;
            if (first < 0)
                first = i;
        }
        return first;
    }

    /// <summary>"North Weald, Veyra": the province's name, then its realm's.</summary>
    private static string Label(CycleState cycle, Province province)
    {
        string name = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
        if (string.IsNullOrEmpty(province.KingdomId) || cycle?.Kingdoms == null
            || !cycle.Kingdoms.TryGetValue(province.KingdomId, out var kingdom) || kingdom == null)
            return name;
        string realm = string.IsNullOrEmpty(kingdom.DisplayName) ? province.KingdomId : kingdom.DisplayName;
        return $"{name}, {realm}";
    }
}
