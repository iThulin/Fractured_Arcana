using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// ============================================================
// ProvinceCourts.cs
//
// Purpose:        Where the courts meet the provinces (slice PV2b-1,
//                 docs/provinces_v1_7g_deeds_and_courts.md; rulings
//                 PR14, PR15, PR18, PR20 and PR21 of docs/provinces_v1.md
//                 section 7a). Every courtier has an estate, a province
//                 of their realm. Once the court knows the guild, a
//                 courtier who thinks well of it keeps the guild's
//                 influence in their estate from fading below 10 for
//                 each point of their Regard, and one who thinks ill of
//                 it halves the guild's deeds there. The court's band
//                 scales every deed in its realm. Aiding an invader
//                 against a court that welcomes the guild costs Regard
//                 with every courtier there, and a court gone Hostile,
//                 or a realm the guild bids to seize, gives no floor.
// Layer:          System
// Collaborators:  CouncilState.cs (CourtierState.EstateProvinceId,
//                 CourtState.Band and HasContact), ProvinceInfluence.cs
//                 (Sync gives the estates, a deed reads DeedGain, the
//                 lunation and the forecast read Floor),
//                 KingdomTickSimulation.cs (the Aid intervention),
//                 StrategicView.ProvinceHover.cs (the readout's court
//                 line), CouncilTick.cs (court and office names),
//                 ProvinceSaveAssert.cs
// See:            docs/provinces_v1.md section 7a;
//                 docs/provinces_v1_7g_deeds_and_courts.md
// ============================================================

/// <summary>Estates, the floors a friendly courtier keeps, and a court's weight on the
/// guild's deeds. Stateless: the one stored value is each courtier's EstateProvinceId.</summary>
public static class ProvinceCourts
{
    // ── Tuning (docs/provinces_v1_7g_deeds_and_courts.md section 12) ─────
    /// <summary>In a courtier's estate the guild's influence does not fade below this much
    /// per point of their Regard (PR15): 10 to 30 for Regard +1 to +3.</summary>
    public const int FloorPerRegard = 10;

    /// <summary>Regard each courtier loses when the guild aids an invader against a court
    /// that rates it Welcome or better (PR20).</summary>
    public const int BetrayalRegardCost = 1;

    /// <summary>A deed's weight in a realm, in percent, by its court's band (PR18). Unknown
    /// counts as Received.</summary>
    public static int BandPercent(CourtStandingBand band) => band switch
    {
        CourtStandingBand.Hostile => 50,
        CourtStandingBand.Welcome => 110,
        CourtStandingBand.Favored => 125,
        CourtStandingBand.Trusted => 150,
        _ => 100,
    };

    // ══════════════════════════════════════════════════════════════════════
    // Estates (PR14)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Give every courtier without an estate one. A court's courtiers take their
    /// realm's provinces other than the capital, in index order, by their place in the
    /// court's list, wrapping round when there are more courtiers than provinces; a realm
    /// with only its capital puts them all there. A courtier whose estate already names a
    /// province of their realm keeps it, so an estate never moves. ProvinceInfluence.Sync
    /// calls this. True when an estate was given, so the caller saves.</summary>
    public static bool EnsureEstates(CycleState cycle)
    {
        var world = cycle?.World;
        var courts = cycle?.Council?.Courts;
        if (world == null || courts == null || !world.HasProvinces())
            return false;
        bool changed = false;
        foreach (var kv in courts)
        {
            var court = kv.Value;
            if (court?.Courtiers == null || court.Courtiers.Count == 0)
                continue;
            string kingdomId = string.IsNullOrEmpty(court.KingdomId) ? kv.Key : court.KingdomId;
            var lands = new List<Province>();
            var mine = new HashSet<string>();
            Province capital = null;
            foreach (var province in world.Provinces)
            {
                if (province.KingdomId != kingdomId)
                    continue;
                mine.Add(province.Id);
                if (province.IsCapital)
                    capital ??= province;
                else
                    lands.Add(province);
            }
            if (lands.Count == 0 && capital != null)
                lands.Add(capital);
            if (lands.Count == 0)
                continue;
            for (int n = 0; n < court.Courtiers.Count; n++)
            {
                var courtier = court.Courtiers[n];
                if (courtier == null || (!string.IsNullOrEmpty(courtier.EstateProvinceId) && mine.Contains(courtier.EstateProvinceId)))
                    continue;
                courtier.EstateProvinceId = lands[n % lands.Count].Id;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>The courtiers whose estate is the province, in their court's order. Empty
    /// for a province of a realm with no court.</summary>
    public static List<CourtierState> EstateHolders(CycleState cycle, Province province)
    {
        var holders = new List<CourtierState>();
        var court = CourtOf(cycle, province?.KingdomId);
        if (court == null || string.IsNullOrEmpty(province.Id))
            return holders;
        foreach (var courtier in court.Courtiers)
        {
            if (courtier != null && courtier.EstateProvinceId == province.Id)
                holders.Add(courtier);
        }
        return holders;
    }

    // ══════════════════════════════════════════════════════════════════════
    // The court's weight on deeds (PR18) and ill will (PR15)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>A deed's weight in a realm, in percent: its court's band, or 100 for a realm
    /// with no court (the convergence).</summary>
    public static int BandPercent(CycleState cycle, string kingdomId)
    {
        var court = CourtOf(cycle, kingdomId);
        return court == null ? 100 : BandPercent(court.Band());
    }

    /// <summary>What a deed of the given amount gives in the province: scaled by its court's
    /// band, then halved when a courtier estated there thinks ill of the guild, each rounded
    /// half up. Zero and negative amounts pass unchanged. ProvinceInfluence applies it to
    /// every deed, never to the castle's presence, held neighbours, a fall, a seizure or a
    /// spread.</summary>
    public static int DeedGain(CycleState cycle, Province province, int amount)
    {
        if (amount <= 0 || province == null)
            return amount;
        int gain = (amount * BandPercent(cycle, province.KingdomId) + 50) / 100;
        if (HalvesDeeds(cycle, province))
            gain = (gain + 1) / 2;
        return gain;
    }

    /// <summary>True when, with the court in contact, a courtier estated in the province has
    /// negative Regard (PR15).</summary>
    public static bool HalvesDeeds(CycleState cycle, Province province)
    {
        var court = CourtOf(cycle, province?.KingdomId);
        if (court == null || !court.HasContact)
            return false;
        foreach (var courtier in court.Courtiers)
        {
            if (courtier != null && courtier.EstateProvinceId == province.Id && courtier.Regard < 0)
                return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Floors (PR15, PR21)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>How low the guild's influence in the province may fade: 10 for each point of
    /// the highest positive Regard among the courtiers estated there, or 0. See FloorHolder
    /// for when there is none.</summary>
    public static int Floor(CycleState cycle, Province province)
    {
        var holder = FloorHolder(cycle, province);
        return holder == null ? 0 : holder.Regard * FloorPerRegard;
    }

    /// <summary>The courtier whose Regard sets the province's floor: of those estated there,
    /// the one with the highest positive Regard, the first of equals. Null when there is
    /// none, when the court does not know the guild yet, when it is Hostile, or while the
    /// guild bids to seize the realm (PR21).</summary>
    public static CourtierState FloorHolder(CycleState cycle, Province province)
    {
        var court = CourtOf(cycle, province?.KingdomId);
        if (court == null || !court.HasContact || court.Band() == CourtStandingBand.Hostile
            || GuildSeizing(cycle, province.KingdomId))
            return null;
        CourtierState best = null;
        foreach (var courtier in court.Courtiers)
        {
            if (courtier != null && courtier.EstateProvinceId == province.Id && courtier.Regard > 0
                && (best == null || courtier.Regard > best.Regard))
                best = courtier;
        }
        return best;
    }

    /// <summary>True while an open front against the realm carries the guild's bid to seize
    /// it (a won Seize intervention, Warfront.PlayerSeizing).</summary>
    public static bool GuildSeizing(CycleState cycle, string kingdomId)
    {
        if (cycle?.Warfronts == null || string.IsNullOrEmpty(kingdomId))
            return false;
        foreach (var front in cycle.Warfronts)
        {
            if (front != null && !front.Closed && front.PlayerSeizing && front.DefenderKingdomId == kingdomId)
                return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Betrayal (PR20, its first half)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The guild aided an invader against the realm (an Aid intervention, won or
    /// lost). When its court rates the guild Welcome or better, every courtier there loses
    /// Regard, and the Herald's Report says why. True when Regard was lost.</summary>
    public static bool Betrayal(CycleState cycle, string kingdomId)
    {
        var court = CourtOf(cycle, kingdomId);
        if (court == null || court.Courtiers.Count == 0 || court.Band() < CourtStandingBand.Welcome)
            return false;
        foreach (var courtier in court.Courtiers)
        {
            if (courtier != null)
                courtier.Regard = Mathf.Clamp(courtier.Regard - BetrayalRegardCost, -3, 3);
        }
        string courtName = CouncilTick.CourtDisplayName(cycle, kingdomId);
        string text = $"Word reaches {courtName} that the guild marched with the invader: "
                      + $"every courtier there thinks less of the guild. Standing: {court.Band()}.";
        cycle.Council.Reports ??= new List<HeraldReport>();
        cycle.Council.Reports.Add(new HeraldReport
        {
            Lunation = cycle.Calendar?.CurrentLunation ?? 0,
            KingdomId = kingdomId,
            Text = text,
        });
        GD.Print($"[Provinces] {text}");
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Text
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>"Ilse Varn the Marshal", as the Herald's Report names a courtier.</summary>
    public static string Who(CourtierState courtier)
        => courtier == null ? "A courtier" : $"{courtier.DisplayName} the {CouncilTick.OfficeDisplay(courtier.Office)}";

    /// <summary>The province readout's court line, or "" when there is nothing to say: the
    /// courtiers estated there and what their Regard does, then the court's weight on deeds
    /// when it is not x1. Before the court knows the guild its people are figures without
    /// names or Regard, as on the council screen, so only their offices show.</summary>
    public static string ReadoutLine(CycleState cycle, Province province)
    {
        var court = CourtOf(cycle, province?.KingdomId);
        if (court == null)
            return "";
        var parts = new List<string>();
        var holders = EstateHolders(cycle, province);
        if (holders.Count > 0 && !court.HasContact)
        {
            var offices = new List<string>();
            foreach (var courtier in holders)
                offices.Add("the " + CouncilTick.OfficeDisplay(courtier.Office));
            parts.Add($"Estate of {JoinAnd(offices)}.");
        }
        else if (holders.Count > 0)
        {
            var names = new List<string>();
            bool fond = false;
            foreach (var courtier in holders)
            {
                names.Add($"{Who(courtier)} (Regard {Signed(courtier.Regard)})");
                fond |= courtier.Regard > 0;
            }
            parts.Add($"Estate of {JoinAnd(names)}.");
            int floor = Floor(cycle, province);
            if (floor > 0)
                parts.Add($"Influence here does not fade below {floor}.");
            else if (fond && court.Band() == CourtStandingBand.Hostile)
                parts.Add("The court is Hostile, so no regard protects it.");
            else if (fond && GuildSeizing(cycle, province.KingdomId))
                parts.Add("While the guild bids to seize the realm, no regard protects it.");
            if (HalvesDeeds(cycle, province))
                parts.Add("Deeds here count half.");
        }
        int percent = BandPercent(court.Band());
        if (percent != 100)
            parts.Add($"Deeds in this realm count x{Weight(percent)}: its court rates the guild {court.Band()}.");
        return string.Join(" ", parts);
    }

    /// <summary>The Province Influence report's tail for a realm: ", court Favored, deeds
    /// x1.25", or "" for a realm with no court.</summary>
    public static string CourtTag(CycleState cycle, string kingdomId)
    {
        var court = CourtOf(cycle, kingdomId);
        if (court == null)
            return "";
        return $", court {court.Band()}" + (court.HasContact ? "" : " (no contact)")
               + $", deeds x{Weight(BandPercent(court.Band()))}";
    }

    /// <summary>The Province Influence report's tail for a province: its estates with their
    /// Regard, its floor, and ill will. "" when it is no one's estate.</summary>
    public static string EstateTag(CycleState cycle, Province province)
    {
        var holders = EstateHolders(cycle, province);
        if (holders.Count == 0)
            return "";
        var sb = new StringBuilder("  estate of ");
        for (int i = 0; i < holders.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append($"{holders[i].DisplayName} ({CouncilTick.OfficeDisplay(holders[i].Office)}, {Signed(holders[i].Regard)})");
        }
        int floor = Floor(cycle, province);
        if (floor > 0)
            sb.Append($"; floor {floor}");
        if (HalvesDeeds(cycle, province))
            sb.Append("; deeds half");
        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The realm's court, or null for a realm without one.</summary>
    private static CourtState CourtOf(CycleState cycle, string kingdomId)
    {
        if (string.IsNullOrEmpty(kingdomId) || cycle?.Council?.Courts == null)
            return null;
        return cycle.Council.Courts.TryGetValue(kingdomId, out var court) && court?.Courtiers != null ? court : null;
    }

    /// <summary>"+2", "0", "-1": invariant, because some cultures write a negative number
    /// with a Unicode minus sign.</summary>
    private static string Signed(int value)
        => (value > 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A percent as a weight: 150 reads "1.5", 125 "1.25".</summary>
    private static string Weight(int percent)
        => (percent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string JoinAnd(List<string> items)
    {
        if (items.Count <= 1)
            return items.Count == 1 ? items[0] : "";
        return string.Join(", ", items.GetRange(0, items.Count - 1)) + " and " + items[items.Count - 1];
    }
}
