using System;
using System.Collections.Generic;

// ============================================================
// KingdomTickSimulation.Annex.cs  (partial of KingdomTickSimulation)
//
// Purpose:        Slice PV3b of the provinces spec (docs/provinces_v1_8_
//                 fronts_per_province.md, section 13): the annexation
//                 of provinces no one rules (PR11, ruled 2026-11-03). A
//                 province goes to no one when its realm falls with its
//                 capital and the guild did not hold it. Each lunation
//                 every kingdom that rules a province bordering it pushes
//                 a bar of its own toward 100 (PR61); the guild's
//                 influence there at 40 or more slows every bar (PR62);
//                 the first to fill its bar takes the province, and a
//                 change of hands ends every bid (PR63). The guild can
//                 still claim it first by holding it (PR10,
//                 ProvinceControl.ClaimNeutral), which the lunation
//                 settles before the kingdom tick runs.
// Layer:          System
// Collaborators:  KingdomTickSimulation.cs (Tick calls TickAnnexation
//                 before the fronts advance), KingdomTickSimulation.
//                 Fronts.cs (FrontsWalk and the news helpers),
//                 ProvinceControl.cs (SetRuler hands the province over
//                 and ends the bids; RulerTag lists them), Province.cs
//                 (Annexation), StrategicView.Rulers.cs (the readout's
//                 and the card's note), StrategicDebug.cs (PushAnnex),
//                 ProvinceSaveAssert.Annex.cs
// See:            docs/provinces_v1_8_fronts_per_province.md section 13
// ============================================================

public static partial class KingdomTickSimulation
{
    // ── Tuning (section 13) ──────────────────────────────────────────────

    /// <summary>The bar a kingdom fills to annex a province no one rules.</summary>
    public const int AnnexFill = 100;

    /// <summary>What a bordering kingdom's bar gains each lunation (PR61).</summary>
    public const int AnnexBase = 20;

    /// <summary>What it gains more for each further province of its own that borders the
    /// province (PR61): a realm that surrounds the vacuum fills it faster.</summary>
    public const int AnnexPerBorder = 5;

    /// <summary>What every bar gains less where the guild's influence in the province is
    /// AnnexReliefAt or more (PR62): its people resist an annexer.</summary>
    public const int AnnexGuildRelief = 10;

    /// <summary>The guild's influence that slows the bars: where the castle's presence stops.</summary>
    public const int AnnexReliefAt = ProvinceInfluence.PassiveCap;

    /// <summary>One kingdom's bid for a province no one rules.</summary>
    public readonly struct AnnexBid
    {
        /// <summary>The kingdom bidding.</summary>
        public readonly string KingdomId;

        /// <summary>Its bar, 0 to AnnexFill.</summary>
        public readonly int Bar;

        /// <summary>How many of its provinces border the province.</summary>
        public readonly int Borders;

        /// <summary>What its bar gains next lunation.</summary>
        public readonly int Gain;

        public AnnexBid(string kingdomId, int bar, int borders, int gain)
        {
            KingdomId = kingdomId;
            Bar = bar;
            Borders = borders;
            Gain = gain;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Every kingdom bidding for the province at <paramref name="index"/>, a province no
    /// one rules: each kingdom of the cycle, not fallen, that rules a province bordering it,
    /// with its bar and what the next lunation adds; the highest bar first, then the most
    /// borders, then the lower id. Empty for a province someone rules, an island, or a world
    /// without provinces. The lunation adds exactly these gains, so the readout's forecast is
    /// the lunation's own arithmetic.</summary>
    public static List<AnnexBid> AnnexBids(CycleState cycle, int index)
    {
        var bids = new List<AnnexBid>();
        if (!FrontsWalk(cycle) || index < 0 || index >= cycle.World.Provinces.Count)
            return bids;
        var provinces = cycle.World.Provinces;
        var province = provinces[index];
        if (province == null || ProvinceControl.RulerOf(cycle, province) != ProvinceControl.Neutral)
            return bids;
        var borders = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (int q in province.Neighbors)
        {
            if (q < 0 || q >= provinces.Count || provinces[q] == null)
                continue;
            string ruler = ProvinceControl.RulerOf(cycle, provinces[q]);
            if (!Annexes(cycle, ruler))
                continue;
            if (!borders.ContainsKey(ruler))
            {
                borders[ruler] = 0;
                order.Add(ruler);
            }
            borders[ruler]++;
        }
        int relief = province.Influence >= AnnexReliefAt ? AnnexGuildRelief : 0;
        foreach (string kid in order)
        {
            int bar = province.Annexation != null && province.Annexation.TryGetValue(kid, out int stored) ? stored : 0;
            int gain = Math.Max(0, AnnexBase + AnnexPerBorder * (borders[kid] - 1) - relief);
            // C1 (Sow Discord): a realm in discord gains nothing on its bid.
            if (cycle.Kingdoms != null && cycle.Kingdoms.TryGetValue(kid, out var bidder)
                && bidder != null && bidder.HinderedMoons > 0)
                gain = 0;
            bids.Add(new AnnexBid(kid, bar, borders[kid], gain));
        }
        bids.Sort((x, y) => x.Bar != y.Bar
            ? y.Bar.CompareTo(x.Bar)
            : x.Borders != y.Borders
                ? y.Borders.CompareTo(x.Borders)
                : string.CompareOrdinal(x.KingdomId, y.KingdomId));
        return bids;
    }

    /// <summary>Who annexes the province first if nothing changes, and in how many lunations
    /// (<paramref name="lunations"/>): the lunation's own rule run forward over
    /// <paramref name="bids"/>, as AnnexBids gives them. Null, with -1, when no bid gains.</summary>
    public static string AnnexForecast(List<AnnexBid> bids, out int lunations)
    {
        lunations = -1;
        if (bids == null || bids.Count == 0)
            return null;
        for (int n = 1; n <= AnnexFill; n++)
        {
            string winner = null;
            int winTotal = -1, winBorders = -1;
            foreach (var bid in bids)
            {
                int total = bid.Bar + n * bid.Gain;
                if (total < AnnexFill || !Beats(total, bid.Borders, bid.KingdomId, winTotal, winBorders, winner))
                    continue;
                winner = bid.KingdomId;
                winTotal = total;
                winBorders = bid.Borders;
            }
            if (winner != null)
            {
                lunations = n;
                return winner;
            }
        }
        return null;
    }

    /// <summary>True when a ruler can annex: a kingdom of the cycle that has not fallen. The
    /// guild claims by holding (PR10), never by a bar.</summary>
    private static bool Annexes(CycleState cycle, string ruler)
        => ProvinceControl.IsKingdom(ruler) && cycle.Kingdoms.ContainsKey(ruler)
           && !ProvinceControl.KingdomFallen(cycle, ruler);

    /// <summary>The rule among bars that fill in the same lunation (PR63): the one that would
    /// go furthest, then the most borders, then the lower id. True when this bar beats the
    /// winner so far (none yet when <paramref name="winner"/> is null).</summary>
    private static bool Beats(int total, int borders, string kid, int winTotal, int winBorders, string winner)
        => winner == null || total > winTotal
           || (total == winTotal && (borders > winBorders
               || (borders == winBorders && string.CompareOrdinal(kid, winner) < 0)));

    // ══════════════════════════════════════════════════════════════════════
    // The lunation
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>One lunation of annexation (PR11, PR61 to PR63). Every bid is read before any
    /// is settled, so the lunation is simultaneous. For each province no one rules, every
    /// bid's bar gains its gain; a kingdom that no longer borders it loses its bar; the first
    /// bar to fill takes the province, its caches with it. A bid's first lunation and an
    /// annexation are news. Tick runs it before the fronts advance, so land a fall leaves to
    /// no one this lunation is bid for from the next.</summary>
    internal static void TickAnnexation(CycleState cycle, Func<string, string> fd = null)
    {
        if (!FrontsWalk(cycle))
            return;
        cycle.PendingSiegeReports ??= new List<string>();
        var provinces = cycle.World.Provinces;
        var open = new List<int>();
        var bidsOf = new List<List<AnnexBid>>();
        for (int i = 0; i < provinces.Count; i++)
        {
            if (provinces[i] == null || ProvinceControl.RulerOf(cycle, provinces[i]) != ProvinceControl.Neutral)
                continue;
            open.Add(i);
            bidsOf.Add(AnnexBids(cycle, i));
        }
        for (int n = 0; n < open.Count; n++)
        {
            var province = provinces[open[n]];
            province.Annexation ??= new Dictionary<string, int>();
            province.Annexation.Clear();
            string winner = null;
            int winTotal = -1, winBorders = -1;
            foreach (var bid in bidsOf[n])
            {
                int total = bid.Bar + bid.Gain;
                province.Annexation[bid.KingdomId] = Math.Min(AnnexFill, total);
                if (bid.Bar <= 0 && total > 0)
                    Report(cycle, $"{AnnexerName(cycle, bid.KingdomId, fd)} moves to annex "
                                  + $"{ProvinceControl.Name(province)}, which no one rules.");
                if (total < AnnexFill || !Beats(total, bid.Borders, bid.KingdomId, winTotal, winBorders, winner))
                    continue;
                winner = bid.KingdomId;
                winTotal = total;
                winBorders = bid.Borders;
            }
            if (winner != null)
                Annex(cycle, province, winner, fd);
        }
    }

    /// <summary>A kingdom annexes a province no one rules (PR64): it rules it now, every bid
    /// ends (ProvinceControl.SetRuler), and the caches no one held there pass to it. The
    /// guild's influence there is untouched: no force was used. The annexation's line goes
    /// before the caches' lines it caused.</summary>
    private static void Annex(CycleState cycle, Province province, string kid, Func<string, string> fd)
    {
        int mark = ReportCount(cycle);
        ProvinceControl.SetRuler(cycle, province, kid, fd);
        ReportAt(cycle, mark, $"{AnnexerName(cycle, kid, fd)} annexes {ProvinceControl.Name(province)}, which no one ruled.");
    }

    /// <summary>An annexer's name, as the war's news names an aggressor: its ruling faction,
    /// else its realm's name.</summary>
    private static string AnnexerName(CycleState cycle, string kid, Func<string, string> fd)
        => cycle.Kingdoms.TryGetValue(kid ?? "", out var kingdom) && kingdom != null
           && !string.IsNullOrEmpty(kingdom.ControllingFactionId)
            ? ResolveName(fd, kingdom.ControllingFactionId)
            : ProvinceControl.RealmName(cycle, kid);

    // ══════════════════════════════════════════════════════════════════════
    // The debug lever
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>[DEBUG] Gate PV3b's lever (Push Annex +40): of the provinces no one rules, the one
    /// whose leading bid is furthest along gains <paramref name="amount"/> on that bid, and it
    /// settles at once by the lunation's rule: a full bar annexes. Returns a line for the log
    /// with the news it made, or null when no province no one rules has a bidder.</summary>
    public static string DebugAnnex(CycleState cycle, int amount, Func<string, string> fd = null)
    {
        if (!FrontsWalk(cycle))
            return null;
        var provinces = cycle.World.Provinces;
        int best = -1;
        AnnexBid lead = default;
        for (int i = 0; i < provinces.Count; i++)
        {
            var bids = AnnexBids(cycle, i);
            if (bids.Count == 0 || (best >= 0 && bids[0].Bar <= lead.Bar))
                continue;
            best = i;
            lead = bids[0];
        }
        if (best < 0)
            return null;
        var province = provinces[best];
        province.Annexation ??= new Dictionary<string, int>();
        cycle.PendingSiegeReports ??= new List<string>();
        int before = cycle.PendingSiegeReports.Count;
        int total = lead.Bar + amount;
        province.Annexation[lead.KingdomId] = Math.Min(AnnexFill, total);
        string name = AnnexerName(cycle, lead.KingdomId, fd);
        if (lead.Bar <= 0 && total > 0)
            Report(cycle, $"{name} moves to annex {ProvinceControl.Name(province)}, which no one rules.");
        if (total >= AnnexFill)
            Annex(cycle, province, lead.KingdomId, fd);
        var news = cycle.PendingSiegeReports.GetRange(before, cycle.PendingSiegeReports.Count - before);
        return $"Pushed {name}'s bid for {ProvinceControl.Name(province)} from {lead.Bar} by {amount}."
               + (news.Count > 0 ? " " + string.Join(" ", news) : $" It stands at {province.Annexation[lead.KingdomId]}.");
    }
}
