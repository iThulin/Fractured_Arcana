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
//                 province where it happens. It also forecasts the
//                 next lunation for the strategic map's province
//                 readout (section 7c), and sorts each province into
//                 the Reach lens's bands (section 7d). Since slice
//                 PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md) a
//                 deed is weighed by its realm's court, a friendly
//                 courtier's estate fades no lower than their floor
//                 (ProvinceCourts), and three more deeds land here: a
//                 site cleared, a contract turned in, a lead resolved.
// Layer:          System
// Collaborators:  Province.cs (Influence, Held), KingdomState.cs
//                 (PlayerInfluence, PlayerInfluenceDerived),
//                 KingdomTickSimulation.cs (interventions, falls,
//                 seizures, the front relief), ExpeditionManager.cs
//                 (secured sites, shard vaults), StrategicView.cs
//                 (the lunation tick, the place card),
//                 StrategicView.ProvinceHover.cs (the readout),
//                 CycleInitializer.cs (Sync when a cycle opens),
//                 WorldAtlas3D.cs and WorldAtlas3D.Provinces.cs
//                 (the Reach lens, the numbers on the map),
//                 CampusGuildPanel.cs (report, debug deed),
//                 ProvinceCourts.cs (estates, floors, the court's
//                 weight), CityContractService.cs and LeadService.cs
//                 (deeds), ProvinceSaveAssert.cs
// See:            docs/provinces_v1.md sections 7b and 7c;
//                 docs/provinces_v1_7d_lenses.md;
//                 docs/provinces_v1_7g_deeds_and_courts.md
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

    /// <summary>Deeds of slice PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md section 7),
    /// each weighed by its realm's court (ProvinceCourts.DeedGain): a fight won at a site
    /// that grants no staging point; a contract turned in at a city's board; a rumor whose
    /// place is Sighted.</summary>
    public const int SiteClearedGain = 10;

    public const int ContractGain = 8;

    public const int LeadGain = 5;

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
    /// when it is given; the opening of a cycle passes none. It also gives every courtier
    /// without an estate one (PR14, ProvinceCourts.EnsureEstates). True when anything
    /// changed, so the caller knows to save.</summary>
    public static bool Sync(CycleState cycle, List<string> lines = null)
    {
        if (!Active(cycle))
            return false;
        bool changed = ProvinceCourts.EnsureEstates(cycle);
        foreach (var kv in cycle.Kingdoms)
            changed |= Absorb(cycle, kv.Key, kv.Value);
        changed |= SettleAll(cycle, lines);
        return changed;
    }

    /// <summary>One lunation (docs/provinces_v1.md section 7b): Sync; the castle's
    /// presence (+8 in its province, +3 in each province beside it) and the held
    /// neighbours (+1 each), none of it past PassiveCap; decay (-2) where the castle is
    /// neither in nor beside the province, except in a kingdom the guild rules, and never
    /// below a friendly courtier's floor in their estate (PR15, ProvinceCourts.Floor); the hold
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
            {
                // It fades no lower than its floor, and not at all from below it (PR15).
                int floor = ProvinceCourts.Floor(cycle, province);
                value = Math.Max(Math.Max(0, value - Decay), Math.Min(value, floor));
            }
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

    /// <summary>A deed of slice PV2b-1 at (x,y): a site cleared, a contract turned in, a lead
    /// resolved. As Add, except that nothing moves while the interval walks (Standing
    /// Orders O3b, D204 and D205: before the founding no court hears of the guild and no
    /// province moves). Returns the news as Add does.</summary>
    public static string Deed(CycleState cycle, int x, int y, int amount)
    {
        if (Interval.Walking(cycle))
            return "";
        return Add(cycle, x, y, amount);
    }

    /// <summary>A deed in a province named by its index (slice PV2b-2: a favor spent as its
    /// deed lands in a courtier's estate, which may lie anywhere in the realm). As Deed: the
    /// court weighs it, and nothing moves while the interval walks or in a world without
    /// provinces. Returns the news as Add does.</summary>
    public static string DeedIn(CycleState cycle, int index, int amount)
    {
        if (cycle?.World == null || !Active(cycle) || Interval.Walking(cycle))
            return "";
        return AddToProvince(cycle, index, amount);
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
    // The next lunation (the province readout, docs/provinces_v1.md 7c)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Why a province's influence moves as it will next lunation.</summary>
    public enum Drift
    {
        /// <summary>Nothing reaches it, and it has nothing to lose.</summary>
        Still = 0,

        /// <summary>The castle stands in it.</summary>
        CastleHere = 1,

        /// <summary>The castle stands beside it.</summary>
        CastleBeside = 2,

        /// <summary>The castle is in it or beside it, but presence stops at PassiveCap.</summary>
        Capped = 3,

        /// <summary>The guild rules its kingdom, so it does not decay.</summary>
        Sovereign = 4,

        /// <summary>Away from the castle, held neighbours keep it up.</summary>
        HeldNeighbors = 5,

        /// <summary>Away from the castle, it fades.</summary>
        Fading = 6,

        /// <summary>Away from the castle, a friendly courtier's estate: it fades no lower
        /// than their floor (PR15).</summary>
        EstateFloor = 7,
    }

    /// <summary>What the next lunation will do to a province's influence if nothing else
    /// happens first. It is Tick's own arithmetic, read without changing anything: the
    /// castle's presence and held neighbours up to PassiveCap, then decay away from the
    /// castle unless the guild rules the kingdom, stopped at a friendly courtier's floor.
    /// drift says why. 0 and Still for anything that is not a province of a world with
    /// provinces.</summary>
    public static int NextLunation(CycleState cycle, int index, out Drift drift)
    {
        drift = Drift.Still;
        if (!Active(cycle) || index < 0 || index >= cycle.World.Provinces.Count)
            return 0;
        var provinces = cycle.World.Provinces;
        var province = provinces[index];
        int castle = CastleProvince(cycle);
        bool here = index == castle;
        bool beside = !here && castle >= 0 && provinces[castle].Neighbors.Contains(index);
        int heldNeighbors = 0;
        foreach (int q in province.Neighbors)
        {
            if (q >= 0 && q < provinces.Count && provinces[q].Held)
                heldNeighbors++;
        }
        int passive = (here ? PresenceGain : beside ? PresenceNeighborGain : 0) + heldNeighbors * HeldNeighborGain;
        int value = province.Influence;
        int next = value;
        if (passive > 0 && next < PassiveCap)
            next = Math.Min(PassiveCap, next + passive);
        bool ruled = GuildRules(cycle, province.KingdomId);
        bool floored = false;
        if (!here && !beside && !ruled)
        {
            int decayed = Math.Max(0, next - Decay);
            int kept = Math.Max(decayed, Math.Min(next, ProvinceCourts.Floor(cycle, province)));
            floored = kept > decayed;
            next = kept;
        }
        int change = Clamp(next) - value;

        if (here || beside)
            drift = change > 0 ? (here ? Drift.CastleHere : Drift.CastleBeside) : Drift.Capped;
        else if (ruled)
            drift = Drift.Sovereign;
        else if (floored && change <= 0)
            drift = Drift.EstateFloor;
        else if (change < 0)
            drift = Drift.Fading;
        else if (heldNeighbors > 0)
            drift = Drift.HeldNeighbors;
        return change;
    }

    /// <summary>Lunations until a held province is lost if the castle stays away and no deed
    /// lands, or -1 when it is not slipping. A held province stands at LoseLine or more,
    /// past the presence cap, so away from the castle it loses the same each lunation.</summary>
    public static int LunationsUntilLost(CycleState cycle, int index)
    {
        int change = NextLunation(cycle, index, out Drift drift);
        if (drift != Drift.Fading || change >= 0 || !cycle.World.Provinces[index].Held)
            return -1;
        return (cycle.World.Provinces[index].Influence - LoseLine) / -change + 1;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Bands (provinces on every lens, docs/provinces_v1_7d_lenses.md)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Where a province stands against the rules, as the Reach lens draws it.
    /// The lens, the numbers on the map and the key all read BandOf, so they agree.</summary>
    public enum Band
    {
        /// <summary>Not held, and the guild has no influence there.</summary>
        Untouched = 0,

        /// <summary>Not held, between 1 and PassiveCap: presence can still raise it.</summary>
        Growing = 1,

        /// <summary>Not held, at PassiveCap or more: as far as presence goes, so only deeds
        /// take it further.</summary>
        Ready = 2,

        /// <summary>Held, but below HoldLine: it is lost below LoseLine.</summary>
        HeldSlipping = 3,

        /// <summary>Held, at HoldLine or more.</summary>
        Held = 4,
    }

    /// <summary>The band a province is in. Held decides gold; the number decides the
    /// rest, so the lens never shows a hold the rules have not set.</summary>
    public static Band BandOf(Province province)
    {
        if (province == null)
            return Band.Untouched;
        if (province.Held)
            return province.Influence >= HoldLine ? Band.Held : Band.HeldSlipping;
        if (province.Influence <= 0)
            return Band.Untouched;
        return province.Influence < PassiveCap ? Band.Growing : Band.Ready;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Text
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The province readout's influence line: "Guild influence 34 of the 60 that
    /// holds it." or "Held by the guild: influence 72, lost below 50."</summary>
    public static string InfluenceLine(Province province)
    {
        if (province == null)
            return "";
        return province.Held
            ? $"Held by the guild: influence {province.Influence}, lost below {LoseLine}."
            : $"Guild influence {province.Influence} of the {HoldLine} that holds it.";
    }

    /// <summary>The province readout's forecast line: what the next lunation does and why,
    /// and, for a held province that is slipping, when the hold goes.</summary>
    public static string NextLunationLine(CycleState cycle, int index)
    {
        if (!Active(cycle) || index < 0 || index >= cycle.World.Provinces.Count)
            return "";
        int change = NextLunation(cycle, index, out Drift drift);
        // Invariant culture: some cultures write a negative number with a Unicode minus sign.
        string amount = change > 0
            ? "+" + change.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : change.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string who = "";
        int floor = 0;
        if (drift == Drift.EstateFloor)
        {
            var province = cycle.World.Provinces[index];
            who = ProvinceCourts.Who(ProvinceCourts.FloorHolder(cycle, province));
            floor = ProvinceCourts.Floor(cycle, province);
        }
        string line = drift switch
        {
            Drift.CastleHere => $"Next lunation: {amount}, with the castle here.",
            Drift.CastleBeside => $"Next lunation: {amount}, with the castle beside it.",
            Drift.Capped => $"Next lunation: no change. The castle's presence stops at {PassiveCap}; deeds take it further.",
            Drift.Sovereign => change > 0
                ? $"Next lunation: {amount}. The guild rules this kingdom, so its influence does not fade."
                : "Next lunation: no change. The guild rules this kingdom, so its influence does not fade.",
            Drift.HeldNeighbors => change > 0
                ? $"Next lunation: {amount}, from the held provinces beside it."
                : "Next lunation: no change. The held provinces beside it hold off the fading.",
            Drift.Fading => $"Next lunation: {amount}, with the castle away.",
            Drift.EstateFloor => change < 0
                ? $"Next lunation: {amount}, and no lower: {who}'s regard keeps it from fading below {floor}."
                : $"Next lunation: no change. {who}'s regard keeps it from fading below {floor}.",
            _ => "Next lunation: no change. Nothing reaches it with the castle away.",
        };
        int lost = LunationsUntilLost(cycle, index);
        if (lost == 1)
            line += " The hold is lost next lunation.";
        else if (lost > 1)
            line += $" The hold is lost in {lost} lunations.";
        return line;
    }

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
        sb.AppendLine($"Deeds: a site cleared +{SiteClearedGain}, a contract +{ContractGain}, a lead +{LeadGain}, " +
                      "each weighed by the realm's court; a friendly courtier's estate fades no lower than " +
                      $"{ProvinceCourts.FloorPerRegard} per point of Regard.");

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
                          + (pending != 0 ? $", {pending:+#;-#;0} not yet spread over its provinces" : "")
                          + ProvinceCourts.CourtTag(cycle, kingdomId));
            for (int i = 0; i < provinces.Count; i++)
            {
                var province = provinces[i];
                if (province.KingdomId != kingdomId)
                    continue;
                string name = string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;
                string where = i == castle ? "  castle" : near[i] ? "  beside the castle" : "";
                sb.AppendLine($"  {name,-24} {province.Influence,3}{(province.Held ? "  held" : "")}{where}"
                              + ProvinceCourts.EstateTag(cycle, province));
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
    /// then the amount lands, a gain weighed by the realm's court (ProvinceCourts.DeedGain),
    /// then the kingdom's provinces settle their hold (reported) and its value is derived
    /// again.</summary>
    private static string AddToProvince(CycleState cycle, int index, int amount)
    {
        var provinces = cycle.World.Provinces;
        if (index < 0 || index >= provinces.Count)
            return "";
        var province = provinces[index];
        string kingdomId = province.KingdomId ?? "";
        cycle.Kingdoms.TryGetValue(kingdomId, out var kingdom);
        Absorb(cycle, kingdomId, kingdom);
        // The realm's court weighs a deed, and ill will in an estate halves it (PR18, PR15).
        // The castle's presence, a fall, a seizure and a spread never pass through here.
        if (amount > 0)
            amount = ProvinceCourts.DeedGain(cycle, province, amount);
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
