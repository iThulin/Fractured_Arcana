#!/usr/bin/env python3
# Provinces, slice PV2b-1: deeds and the courts (docs/provinces_v1_7g_deeds_and_courts.md,
# section 7g of the provinces spec, 2026-11-04; rulings PR14, PR15, PR18, PR20 and PR21 of
# section 7a, and PR22 on its default). Three deeds: a site cleared (+10), a settlement
# contract (+8), a lead resolved (+5). Every courtier gets an estate; once the court knows
# the guild, a friend's estate fades no lower than 10 per point of Regard and ill will
# halves deeds there; the court's band weighs every deed in its realm; aiding an invader
# against a welcoming court costs Regard; a Hostile court or a realm the guild bids to seize
# gives no floor; a realm where the guild holds a province steadies.
#
# Drops in one new file kept beside this script (ProvinceCourts.cs) and edits nine live files
# surgically (ProvinceInfluence.cs, CouncilState.cs, CouncilSaveAssert.cs,
# ProvinceSaveAssert.cs, ExpeditionManager.cs, CityContractService.cs, LeadService.cs,
# KingdomTickSimulation.cs and StrategicView.ProvinceHover.cs). Rules: every starting blob
# checked (a new file must not exist yet), every anchor found exactly once, added text ASCII
# apart from box-drawing characters, no banned dash, brackets balanced per edit, every result
# equal to the blob it was validated at, nothing written unless every step succeeds, every
# written file verified, and a second run refuses.
#
# Usage: python3 apply_provinces_pv2b.py <repo_root> [--dry-run]

import collections
import difflib
import hashlib
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# The dashes the house style bans, built from code points so this file carries none.
EM_DASH = chr(0x2014)
EN_DASH = chr(0x2013)
ESCAPED_EM_DASH = "\\" + "u2014"


def blob(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


PAIRS = [("(", ")"), ("[", "]"), ("{", "}")]

# DROP_INS: (path in the repo, blob it must have now or None for a new file, blob it is
# written at). EDITS: (path, blob now, blob after, [(anchor, replacement)]).

DROP_INS = [
    ("Scripts/Systems/Strategic/ProvinceCourts.cs", None, "b4d1467f55c47259dd34f55ab812d1f4f1e5246e"),
]

EDITS = [
    (
        "Scripts/Systems/Strategic/ProvinceInfluence.cs",
        "d8a6941b5f516d65e042607ee8462b71ec7dcea5",
        "f024a8982dfaf09c7c9016bae7f184e9c5b9621e",
        [
            (
                r"""//                 readout (section 7c), and sorts each province into
//                 the Reach lens's bands (section 7d).
""",
                r"""//                 readout (section 7c), and sorts each province into
//                 the Reach lens's bands (section 7d). Since slice
//                 PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md) a
//                 deed is weighed by its realm's court, a friendly
//                 courtier's estate fades no lower than their floor
//                 (ProvinceCourts), and three more deeds land here: a
//                 site cleared, a contract turned in, a lead resolved.
""",
            ),
            (
                r"""//                 CampusGuildPanel.cs (report, debug deed),
//                 ProvinceSaveAssert.cs
// See:            docs/provinces_v1.md sections 7b and 7c;
//                 docs/provinces_v1_7d_lenses.md
""",
                r"""//                 CampusGuildPanel.cs (report, debug deed),
//                 ProvinceCourts.cs (estates, floors, the court's
//                 weight), CityContractService.cs and LeadService.cs
//                 (deeds), ProvinceSaveAssert.cs
// See:            docs/provinces_v1.md sections 7b and 7c;
//                 docs/provinces_v1_7d_lenses.md;
//                 docs/provinces_v1_7g_deeds_and_courts.md
""",
            ),
            (
                r"""    public const int NearRadius = 12;
""",
                r"""    public const int NearRadius = 12;

    /// <summary>Deeds of slice PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md section 7),
    /// each weighed by its realm's court (ProvinceCourts.DeedGain): a fight won at a site
    /// that grants no staging point; a contract turned in at a city's board; a rumor whose
    /// place is Sighted.</summary>
    public const int SiteClearedGain = 10;

    public const int ContractGain = 8;

    public const int LeadGain = 5;
""",
            ),
            (
                r"""    /// legacy save starts every province at its kingdom's value. Hold changes go to lines
    /// when it is given; the opening of a cycle passes none. True when anything changed,
    /// so the caller knows to save.</summary>
    public static bool Sync(CycleState cycle, List<string> lines = null)
    {
        if (!Active(cycle))
            return false;
        bool changed = false;
""",
                r"""    /// legacy save starts every province at its kingdom's value. Hold changes go to lines
    /// when it is given; the opening of a cycle passes none. It also gives every courtier
    /// without an estate one (PR14, ProvinceCourts.EnsureEstates). True when anything
    /// changed, so the caller knows to save.</summary>
    public static bool Sync(CycleState cycle, List<string> lines = null)
    {
        if (!Active(cycle))
            return false;
        bool changed = ProvinceCourts.EnsureEstates(cycle);
""",
            ),
            (
                r"""    /// neighbours (+1 each), none of it past PassiveCap; decay (-2) where the castle is
    /// neither in nor beside the province, except in a kingdom the guild rules; the hold
""",
                r"""    /// neighbours (+1 each), none of it past PassiveCap; decay (-2) where the castle is
    /// neither in nor beside the province, except in a kingdom the guild rules, and never
    /// below a friendly courtier's floor in their estate (PR15, ProvinceCourts.Floor); the hold
""",
            ),
            (
                r"""            if (!near[i] && !GuildRules(cycle, province.KingdomId))
                value = Math.Max(0, value - Decay);
            province.Influence = Clamp(value);
""",
                r"""            if (!near[i] && !GuildRules(cycle, province.KingdomId))
            {
                // It fades no lower than its floor, and not at all from below it (PR15).
                int floor = ProvinceCourts.Floor(cycle, province);
                value = Math.Max(Math.Max(0, value - Decay), Math.Min(value, floor));
            }
            province.Influence = Clamp(value);
""",
            ),
            (
                r"""    /// <summary>Set the guild's influence in every province of a kingdom at once: 0 when
""",
                r"""    /// <summary>A deed of slice PV2b-1 at (x,y): a site cleared, a contract turned in, a lead
    /// resolved. As Add, except that nothing moves while the interval walks (Standing
    /// Orders O3b, D204 and D205: before the founding no court hears of the guild and no
    /// province moves). Returns the news as Add does.</summary>
    public static string Deed(CycleState cycle, int x, int y, int amount)
    {
        if (Interval.Walking(cycle))
            return "";
        return Add(cycle, x, y, amount);
    }

    /// <summary>Set the guild's influence in every province of a kingdom at once: 0 when
""",
            ),
            (
                r"""        /// <summary>Away from the castle, it fades.</summary>
        Fading = 6,
    }
""",
                r"""        /// <summary>Away from the castle, it fades.</summary>
        Fading = 6,

        /// <summary>Away from the castle, a friendly courtier's estate: it fades no lower
        /// than their floor (PR15).</summary>
        EstateFloor = 7,
    }
""",
            ),
            (
                r"""    /// castle's presence and held neighbours up to PassiveCap, then decay away from the
    /// castle unless the guild rules the kingdom. drift says why. 0 and Still for anything
    /// that is not a province of a world with provinces.</summary>
""",
                r"""    /// castle's presence and held neighbours up to PassiveCap, then decay away from the
    /// castle unless the guild rules the kingdom, stopped at a friendly courtier's floor.
    /// drift says why. 0 and Still for anything that is not a province of a world with
    /// provinces.</summary>
""",
            ),
            (
                r"""        bool ruled = GuildRules(cycle, province.KingdomId);
        if (!here && !beside && !ruled)
            next = Math.Max(0, next - Decay);
        int change = Clamp(next) - value;

        if (here || beside)
            drift = change > 0 ? (here ? Drift.CastleHere : Drift.CastleBeside) : Drift.Capped;
        else if (ruled)
            drift = Drift.Sovereign;
        else if (change < 0)
""",
                r"""        bool ruled = GuildRules(cycle, province.KingdomId);
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
""",
            ),
            (
                r"""        string amount = change > 0
            ? "+" + change.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : change.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string line = drift switch
        {
""",
                r"""        string amount = change > 0
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
""",
            ),
            (
                r"""            Drift.Fading => $"Next lunation: {amount}, with the castle away.",
""",
                r"""            Drift.Fading => $"Next lunation: {amount}, with the castle away.",
            Drift.EstateFloor => change < 0
                ? $"Next lunation: {amount}, and no lower: {who}'s regard keeps it from fading below {floor}."
                : $"Next lunation: no change. {who}'s regard keeps it from fading below {floor}.",
""",
            ),
            (
                r"""                      $"+{HeldNeighborGain} per held neighbour, none of it past {PassiveCap}; -{Decay} away from the castle.");
""",
                r"""                      $"+{HeldNeighborGain} per held neighbour, none of it past {PassiveCap}; -{Decay} away from the castle.");
        sb.AppendLine($"Deeds: a site cleared +{SiteClearedGain}, a contract +{ContractGain}, a lead +{LeadGain}, " +
                      "each weighed by the realm's court; a friendly courtier's estate fades no lower than " +
                      $"{ProvinceCourts.FloorPerRegard} per point of Regard.");
""",
            ),
            (
                r"""                          + (pending != 0 ? $", {pending:+#;-#;0} not yet spread over its provinces" : ""));
""",
                r"""                          + (pending != 0 ? $", {pending:+#;-#;0} not yet spread over its provinces" : "")
                          + ProvinceCourts.CourtTag(cycle, kingdomId));
""",
            ),
            (
                r"""                sb.AppendLine($"  {name,-24} {province.Influence,3}{(province.Held ? "  held" : "")}{where}");
""",
                r"""                sb.AppendLine($"  {name,-24} {province.Influence,3}{(province.Held ? "  held" : "")}{where}"
                              + ProvinceCourts.EstateTag(cycle, province));
""",
            ),
            (
                r"""    /// <summary>The deed itself: a write made outside the province layer is spread first,
    /// then the amount lands, then the kingdom's provinces settle their hold (reported)
    /// and its value is derived again.</summary>
""",
                r"""    /// <summary>The deed itself: a write made outside the province layer is spread first,
    /// then the amount lands, a gain weighed by the realm's court (ProvinceCourts.DeedGain),
    /// then the kingdom's provinces settle their hold (reported) and its value is derived
    /// again.</summary>
""",
            ),
            (
                r"""        Absorb(cycle, kingdomId, kingdom);
        province.Influence = Clamp(province.Influence + amount);
""",
                r"""        Absorb(cycle, kingdomId, kingdom);
        // The realm's court weighs a deed, and ill will in an estate halves it (PR18, PR15).
        // The castle's presence, a fall, a seizure and a spread never pass through here.
        if (amount > 0)
            amount = ProvinceCourts.DeedGain(cycle, province, amount);
        province.Influence = Clamp(province.Influence + amount);
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campaign/CouncilState.cs",
        "49209b62dfff575d0d0f90387b4cf4fc5855bb08",
        "3474f7ddb3b698a93f368a100020fac0cdf234c2",
        [
            (
                r"""    public bool IsCorruptedAgent = false;
}
""",
                r"""    public bool IsCorruptedAgent = false;

    /// <summary>The courtier's estate (provinces PR14,
    /// docs/provinces_v1_7g_deeds_and_courts.md): the Id of a province of
    /// their realm, where their Regard keeps the guild's influence from
    /// fading and their ill will halves its deeds. Empty until
    /// ProvinceInfluence.Sync gives it; given once and never moved.
    /// Additive: a court from before provinces gets its estates at the
    /// first sync.</summary>
    public string EstateProvinceId = "";
}
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Campaign/CouncilSaveAssert.cs",
        "64561802945d068b82873b45a1ca53beb1512a9a",
        "ea75f431282e9a49b6df1cead94a0d4f272b063a",
        [
            (
                r"""            SecretKnown = true,
            IsCorruptedAgent = true,
        });
""",
                r"""            SecretKnown = true,
            IsCorruptedAgent = true,
            EstateProvinceId = "province:kingdom_x:3", // provinces PR14
        });
""",
            ),
            (
                r"""                ok &= Check(sb, "CourtierState.IsCorruptedAgent", b.IsCorruptedAgent == a.IsCorruptedAgent);
""",
                r"""                ok &= Check(sb, "CourtierState.IsCorruptedAgent", b.IsCorruptedAgent == a.IsCorruptedAgent);
                ok &= Check(sb, "CourtierState.EstateProvinceId", b.EstateProvinceId == a.EstateProvinceId);
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Verification/ProvinceSaveAssert.cs",
        "dc55e3c7bb763a933a2a23954d574de58110cb19",
        "d13e30133871fb0f07e5188dd54b7e603802c70b",
        [
            (
                r"""//                 forecasts; the Reach lens's bands, where a province's
""",
                r"""//                 forecasts; the courts on provinces (estates, floors,
//                 the court's weight on deeds, the deeds of PV2b-1 and
//                 betrayal, docs/provinces_v1_7g_deeds_and_courts.md);
//                 the Reach lens's bands, where a province's
""",
            ),
            (
                r"""// Collaborators:  Province.cs, ProvinceBuilder.cs, ProvinceInfluence.cs,
""",
                r"""// Collaborators:  Province.cs, ProvinceBuilder.cs, ProvinceInfluence.cs,
//                 ProvinceCourts.cs, LeadService.cs, CityContractService.cs,
//                 KingdomTickSimulation.cs (ApplyIntervention),
""",
            ),
            (
                r"""        ok &= AssertForecast(sb);
        ok &= AssertBands(sb);
""",
                r"""        ok &= AssertForecast(sb);
        ok &= AssertEstates(sb);
        ok &= AssertCourtRules(sb);
        ok &= AssertDeeds(sb);
        ok &= AssertBetrayal(sb);
        ok &= AssertBands(sb);
""",
            ),
            (
                r"""            ? "RESULT: ALL PASSED. Provinces and the province map round-trip clean, legacy worlds load empty and build once, the partition keeps its rules, the guild's influence keeps its rules on provinces, and the map's bands, names and inks keep theirs."
            : "RESULT: FAILURES ABOVE. A province field is being dropped or renamed, a partition rule broke, an influence rule broke, or a lens rule broke.");
""",
                r"""            ? "RESULT: ALL PASSED. Provinces and the province map round-trip clean, legacy worlds load empty and build once, the partition keeps its rules, the guild's influence keeps its rules on provinces, the courts keep theirs, and the map's bands, names and inks keep theirs."
            : "RESULT: FAILURES ABOVE. A province field is being dropped or renamed, a partition rule broke, an influence rule broke, a court rule broke, or a lens rule broke.");
""",
            ),
            (
                r"""    // ── Provinces on every lens (docs/provinces_v1_7d_lenses.md) ─────────
""",
                r"""    // ── The courts on provinces (docs/provinces_v1_7g_deeds_and_courts.md) ──

    /// <summary>A realm with a court and no provinces.</summary>
    private const string Lost = "kingdom_9";

    /// <summary>Estates (PR14). The first sync gives them: the west court's seven
    /// courtiers take the west's provinces other than its capital, in index order, by their
    /// place in the court, wrapping round; a stored estate of the realm stays where it is,
    /// and one naming another realm's province is given again; the east's three take the
    /// east's first three; a court whose realm has no provinces gets none; and a second
    /// sync gives nothing.</summary>
    private static bool AssertEstates(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var provinces = cycle.World.Provinces;
        var westLands = provinces.Where(p => p.KingdomId == West && !p.IsCapital).Select(p => p.Id).ToList();
        var eastLands = provinces.Where(p => p.KingdomId == East && !p.IsCapital).Select(p => p.Id).ToList();
        if (westLands.Count < 5 || eastLands.Count < 3)
        {
            sb.AppendLine("  Estates: FAIL (the scratch world's realms have too few provinces)");
            return false;
        }
        var west = cycle.Council.Courts[West];
        var east = cycle.Council.Courts[East];
        var lost = cycle.Council.Courts[Lost];
        west.Courtiers[3].EstateProvinceId = westLands[4];
        west.Courtiers[4].EstateProvinceId = eastLands[0];

        bool ok = Check(sb, "the first sync gives estates", ProvinceInfluence.Sync(cycle));
        bool order = true;
        for (int n = 0; n < west.Courtiers.Count; n++)
        {
            string expected = n == 3 ? westLands[4] : westLands[n % westLands.Count];
            order &= west.Courtiers[n].EstateProvinceId == expected;
        }
        ok &= Check(sb, $"the west's {west.Courtiers.Count} courtiers take its {westLands.Count} provinces past the capital in order, wrapping round; a stored estate of the realm stays and one of another realm is given again",
            order);
        ok &= Check(sb, "the east's three take its first three provinces past the capital",
            east.Courtiers[0].EstateProvinceId == eastLands[0] && east.Courtiers[1].EstateProvinceId == eastLands[1]
            && east.Courtiers[2].EstateProvinceId == eastLands[2]);
        ok &= Check(sb, "a court whose realm has no provinces gets no estates",
            lost.Courtiers.All(c => c.EstateProvinceId == ""));
        var first = provinces.First(p => p.Id == westLands[0]);
        var holders = ProvinceCourts.EstateHolders(cycle, first);
        ok &= Check(sb, "the first west province is the estate of the first and the sixth, in the court's order",
            holders.Count == 2 && holders[0] == west.Courtiers[0] && holders[1] == west.Courtiers[5]);
        ok &= Check(sb, "a second sync gives nothing", !ProvinceInfluence.Sync(cycle));

        sb.AppendLine(ok ? "  Estates: PASS" : "  Estates: FAIL");
        return ok;
    }

    /// <summary>The court's weight and the floors (PR15, PR18, PR21). A deed of 10 at the
    /// west capital, no one's estate, gains 10 before contact, then 5 Hostile, 10 Received,
    /// 11 Welcome, 13 Favored and 15 Trusted; the castle's presence is never weighed. Ill
    /// will halves a deed in its holder's estate (Favored: 13 to 7). With the castle away a
    /// friend's estate at Regard +2 stops at 20, and one already below its floor does not
    /// fade; the forecast names the reason and agrees with the lunation. No floor while the
    /// court is Hostile, while the guild bids to seize the realm, or before contact; and the
    /// readout's court line says each.</summary>
    private static bool AssertCourtRules(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var world = cycle.World;
        var provinces = world.Provinces;
        var west = cycle.Council.Courts[West];
        ProvinceInfluence.Sync(cycle);
        int capital = world.ProvinceIndexAt(6, 15);
        bool ok = Check(sb, "the west capital is a province and no one's estate",
            capital >= 0 && ProvinceCourts.EstateHolders(cycle, provinces[capital]).Count == 0);
        if (!ok)
        {
            sb.AppendLine("  Court rules: FAIL");
            return false;
        }

        // The court's weight, deed by deed at the capital.
        int gained = DeedAtCapital(cycle, capital, 10);
        ok &= Check(sb, $"before contact a deed of 10 gains 10 (it gained {gained})", gained == 10);
        var weights = new (int score, int gain, CourtStandingBand band)[]
        {
            (-6, 5, CourtStandingBand.Hostile),
            (0, 10, CourtStandingBand.Received),
            (3, 11, CourtStandingBand.Welcome),
            (9, 13, CourtStandingBand.Favored),
            (16, 15, CourtStandingBand.Trusted),
        };
        foreach (var (score, gain, band) in weights)
        {
            SetScore(west, score);
            gained = DeedAtCapital(cycle, capital, 10);
            ok &= Check(sb, $"at {band} a deed of 10 gains {gain} (it gained {gained})",
                west.Band() == band && gained == gain);
        }
        provinces[capital].Influence = 0;
        provinces[capital].Held = false;
        ProvinceInfluence.Tick(cycle);
        ok &= Check(sb, $"at Trusted the castle's presence still gives 8 (now {provinces[capital].Influence})",
            provinces[capital].Influence == ProvinceInfluence.PresenceGain);

        // Ill will halves a deed in the holder's estate.
        var cold = west.Courtiers[0];
        cold.Regard = -1;
        SetScore(west, 9);
        var coldLand = provinces.First(p => p.Id == cold.EstateProvinceId);
        coldLand.Influence = 0;
        coldLand.Held = false;
        ProvinceInfluence.Add(cycle, coldLand.SeatX, coldLand.SeatY, 10);
        ok &= Check(sb, $"at Favored, ill will halves a deed of 10 in the holder's estate, 13 to 7 (now {coldLand.Influence})",
            coldLand.Influence == 7 && ProvinceCourts.HalvesDeeds(cycle, coldLand));
        string coldLine = ProvinceCourts.ReadoutLine(cycle, coldLand);
        ok &= Check(sb, "the readout says deeds there count half, and the realm's x1.25",
            coldLine.Contains("Deeds here count half.") && coldLine.Contains("x1.25"));

        // A friend's estate stops at its floor, with the castle away.
        cycle.CastleX = -1;
        cycle.CastleY = -1;
        var fond = west.Courtiers[1];
        fond.Regard = 2;
        SetScore(west, 0);
        var fondLand = provinces.First(p => p.Id == fond.EstateProvinceId);
        fondLand.Influence = 23;
        fondLand.Held = false;
        ok &= Check(sb, "a friend at Regard +2 sets a floor of 20", ProvinceCourts.Floor(cycle, fondLand) == 20);
        ok &= Check(sb, "the readout names the floor",
            ProvinceCourts.ReadoutLine(cycle, fondLand).Contains("Influence here does not fade below 20."));
        ok &= Check(sb, "above the floor it fades as before (23 to 21), as forecast", ForecastHolds(cycle) && fondLand.Influence == 21);
        int change = ProvinceInfluence.NextLunation(cycle, fondLand.Index, out var drift);
        string line = ProvinceInfluence.NextLunationLine(cycle, fondLand.Index);
        ok &= Check(sb, "at 21 it forecasts -1 and no lower, for the friend's regard",
            change == -1 && drift == ProvinceInfluence.Drift.EstateFloor
            && line.Contains("and no lower") && line.EndsWith("keeps it from fading below 20.", System.StringComparison.Ordinal));
        ok &= Check(sb, "it stops at 20, as forecast", ForecastHolds(cycle) && fondLand.Influence == 20);
        change = ProvinceInfluence.NextLunation(cycle, fondLand.Index, out drift);
        ok &= Check(sb, "at 20 it forecasts no change, for the friend's regard",
            change == 0 && drift == ProvinceInfluence.Drift.EstateFloor
            && ProvinceInfluence.NextLunationLine(cycle, fondLand.Index).StartsWith("Next lunation: no change.", System.StringComparison.Ordinal));
        ok &= Check(sb, "and holds there, as forecast", ForecastHolds(cycle) && fondLand.Influence == 20);
        var deep = west.Courtiers[2];
        deep.Regard = 3;
        var deepLand = provinces.First(p => p.Id == deep.EstateProvinceId);
        deepLand.Influence = 10;
        deepLand.Held = false;
        ok &= Check(sb, "below its floor of 30 an estate does not fade at all, as forecast",
            ForecastHolds(cycle) && deepLand.Influence == 10);

        // No floor while the court is Hostile, while the guild bids to seize, or before contact.
        SetScore(west, -6);
        ok &= Check(sb, "no floor at a Hostile court, and the readout says why",
            ProvinceCourts.Floor(cycle, fondLand) == 0
            && ProvinceCourts.ReadoutLine(cycle, fondLand).Contains("The court is Hostile, so no regard protects it."));
        ok &= Check(sb, "so the estate fades (20 to 18), as forecast", ForecastHolds(cycle) && fondLand.Influence == 18);
        SetScore(west, 0);
        fondLand.Influence = 20;
        var seize = new Warfront { Id = "wf_seize", AggressorKingdomId = East, DefenderKingdomId = West, PlayerSeizing = true };
        cycle.Warfronts = new List<Warfront> { seize };
        ok &= Check(sb, "no floor while the guild bids to seize the realm",
            ProvinceCourts.Floor(cycle, fondLand) == 0 && ProvinceCourts.GuildSeizing(cycle, West)
            && ForecastHolds(cycle) && fondLand.Influence == 18);
        seize.Closed = true;
        ok &= Check(sb, "a closed front's bid revokes nothing", ProvinceCourts.Floor(cycle, fondLand) == 20);
        cycle.Warfronts.Clear();
        west.HasContact = false;
        ok &= Check(sb, "before contact no floor, and the readout names only the offices",
            ProvinceCourts.Floor(cycle, fondLand) == 0 && !ProvinceCourts.HalvesDeeds(cycle, coldLand)
            && ProvinceCourts.ReadoutLine(cycle, fondLand) == "Estate of the Marshal and the Chancellor.");
        ok &= Check(sb, "the report names the court and the estates",
            ProvinceInfluence.Report(cycle).Contains("court Unknown (no contact)") && ProvinceInfluence.Report(cycle).Contains("estate of"));

        sb.AppendLine(ok ? "  Court rules: PASS" : "  Court rules: FAIL");
        return ok;
    }

    /// <summary>The deeds of PV2b-1: 10, 8 and 5. A deed moves nothing while the interval
    /// walks and lands once it does not. A lead whose place is unseen stands and gives
    /// nothing; once the place is Sighted the lead goes and its place's province gains 5,
    /// once. A contract board's id names its city's tile.</summary>
    private static bool AssertDeeds(StringBuilder sb)
    {
        var cycle = InfluenceCycle(buildProvinces: true);
        var world = cycle.World;
        var provinces = world.Provinces;
        ProvinceInfluence.Sync(cycle);
        int capital = world.ProvinceIndexAt(6, 15);
        int land = world.ProvinceIndexAt(18, 5);
        bool ok = Check(sb, "the deeds are 10, 8 and 5",
            ProvinceInfluence.SiteClearedGain == 10 && ProvinceInfluence.ContractGain == 8
            && ProvinceInfluence.LeadGain == 5);
        if (capital < 0 || land < 0)
        {
            sb.AppendLine("  Deeds: FAIL (the scratch world's places are not in provinces)");
            return false;
        }

        int before = provinces[capital].Influence;
        cycle.Interval.Active = true;
        ok &= Check(sb, "while the interval walks a deed moves nothing",
            ProvinceInfluence.Deed(cycle, 6, 15, 10) == "" && provinces[capital].Influence == before);
        cycle.Interval.Active = false;
        ProvinceInfluence.Deed(cycle, 6, 15, 10);
        ok &= Check(sb, "once it does not, the deed lands", provinces[capital].Influence == before + 10);

        // A rumor followed home.
        int place = world.Pois.Count;
        world.Pois.Add(new WorldPoi { X = 18, Y = 5, Kind = PoiKind.Combat, KingdomId = West });
        cycle.Leads.Add(new Lead
        {
            Id = "debug:" + place, PoiIndex = place, CenterX = 18, CenterY = 6, Radius = 3,
            Source = "debug", Line = "A test of the deed.",
        });
        before = provinces[land].Influence;
        LeadService.Prune(cycle);
        ok &= Check(sb, "a lead whose place is unseen stands and gives nothing",
            cycle.Leads.Count == 1 && provinces[land].Influence == before);
        world.Pois[place].Discovered = true;
        LeadService.Prune(cycle);
        ok &= Check(sb, $"once its place is Sighted the lead goes and its province gains 5 ({before} to {provinces[land].Influence})",
            cycle.Leads.Count == 0 && provinces[land].Influence == before + ProvinceInfluence.LeadGain);
        LeadService.Prune(cycle);
        ok &= Check(sb, "and only once", provinces[land].Influence == before + ProvinceInfluence.LeadGain);

        // A contract board names its city's tile.
        var board = new CityContractBoardState { CityId = West + ":18,25" };
        ok &= Check(sb, "a board's id names its city's tile, and an id with none names nothing",
            CityContractService.TryBoardCenter(board, out int boardX, out int boardY) && boardX == 18 && boardY == 25
            && !CityContractService.TryBoardCenter(new CityContractBoardState { CityId = "nowhere" }, out _, out _));

        sb.AppendLine(ok ? "  Deeds: PASS" : "  Deeds: FAIL");
        return ok;
    }

    /// <summary>Betrayal (PR20): aiding the invader against a realm whose court rates the
    /// guild Welcome costs every courtier there 1 Regard, to no lower than -3, lost or won,
    /// with a line in the Herald's Report; against a Received court it costs nothing.</summary>
    private static bool AssertBetrayal(StringBuilder sb)
    {
        var cycle = CourtCycle();
        var west = cycle.Council.Courts[West];
        foreach (var courtier in west.Courtiers)
            courtier.Regard = 1;
        west.Courtiers[0].Regard = -3;
        SetScore(west, 3);
        cycle.Warfronts = new List<Warfront> { Front("wf_aid") };
        int reports = cycle.Council.Reports.Count;
        KingdomTickSimulation.ApplyIntervention(cycle, "wf_aid", WarfrontSide.Aid, success: false);
        bool ok = Check(sb, "aiding the invader against a Welcome court costs every courtier 1 Regard, to no lower than -3",
            west.Courtiers[0].Regard == -3 && west.Courtiers.Skip(1).All(c => c.Regard == 0));
        ok &= Check(sb, "and the Herald's Report says so",
            cycle.Council.Reports.Count == reports + 1 && cycle.Council.Reports[reports].KingdomId == West);
        SetScore(west, 0);
        cycle.Warfronts = new List<Warfront> { Front("wf_aid_2") };
        var regard = west.Courtiers.Select(c => c.Regard).ToArray();
        KingdomTickSimulation.ApplyIntervention(cycle, "wf_aid_2", WarfrontSide.Aid, success: true);
        ok &= Check(sb, "against a Received court it costs nothing",
            west.Courtiers.Select(c => c.Regard).SequenceEqual(regard) && cycle.Council.Reports.Count == reports + 1);

        sb.AppendLine(ok ? "  Betrayal: PASS" : "  Betrayal: FAIL");
        return ok;
    }

    // ── Provinces on every lens (docs/provinces_v1_7d_lenses.md) ─────────
""",
            ),
            (
                r"""        ok &= Check(sb, "live world: each kingdom's influence is the tile-weighted mean of its provinces",
            DerivedMatches(liveCycle, skipWaiting: true));
""",
                r"""        ok &= Check(sb, "live world: each kingdom's influence is the tile-weighted mean of its provinces",
            DerivedMatches(liveCycle, skipWaiting: true));
        int strayEstates = StrayEstates(liveCycle);
        ok &= Check(sb, $"live world: every courtier's estate is a province of their realm ({strayEstates} are not)",
            strayEstates == 0);
""",
            ),
            (
                r"""    // ── Influence helpers ─────────────────────────────────────────────────
""",
                r"""    // ── Court helpers ─────────────────────────────────────────────────────
    /// <summary>The influence cycle with courts: the west's of seven courtiers, the east's of
    /// three, and one for a realm with no provinces. Every courtier at Regard 0 and Influence
    /// 1, no court in contact.</summary>
    private static CycleState CourtCycle()
    {
        var cycle = InfluenceCycle(buildProvinces: true);
        cycle.Council = new CouncilState();
        cycle.Council.Courts[West] = Court(West, 7);
        cycle.Council.Courts[East] = Court(East, 3);
        cycle.Council.Courts[Lost] = Court(Lost, 3);
        return cycle;
    }

    private static CourtState Court(string kingdomId, int courtiers)
    {
        var court = new CourtState { KingdomId = kingdomId };
        for (int n = 0; n < courtiers; n++)
        {
            court.Courtiers.Add(new CourtierState
            {
                Id = $"{kingdomId}_courtier_{n}",
                DisplayName = $"Courtier {n}",
                Office = CourtVocab.Offices[n % CourtVocab.Offices.Length],
                Regard = 0,
                Influence = 1,
            });
        }
        return court;
    }

    /// <summary>Put a court in contact at a standing score, by its StandingPenalty, whatever
    /// its courtiers' Regard: -6 Hostile, 0 Received, 3 Welcome, 9 Favored, 16 Trusted.</summary>
    private static void SetScore(CourtState court, int score)
    {
        court.HasContact = true;
        court.StandingPenalty = 0;
        court.StandingPenalty = court.StandingScore() - score;
    }

    /// <summary>A deed at the capital's seat from 0; returns what it gained.</summary>
    private static int DeedAtCapital(CycleState cycle, int capital, int amount)
    {
        var province = cycle.World.Provinces[capital];
        province.Influence = 0;
        province.Held = false;
        ProvinceInfluence.Add(cycle, province.SeatX, province.SeatY, amount);
        return province.Influence;
    }

    /// <summary>An open front of the east against the west, half way.</summary>
    private static Warfront Front(string id) => new Warfront
    {
        Id = id, AggressorKingdomId = East, DefenderKingdomId = West, Advance = 50, FocusCol = 22, FocusRow = 15,
    };

    /// <summary>Courtiers whose estate names no province of their realm. An empty estate is
    /// not counted: the first sync gives it.</summary>
    private static int StrayEstates(CycleState cycle)
    {
        if (cycle?.Council?.Courts == null || cycle.World == null || !cycle.World.HasProvinces())
            return 0;
        var realmOf = new Dictionary<string, string>();
        foreach (var province in cycle.World.Provinces)
            realmOf[province.Id] = province.KingdomId;
        int stray = 0;
        foreach (var kv in cycle.Council.Courts)
        {
            if (kv.Value?.Courtiers == null)
                continue;
            string realm = string.IsNullOrEmpty(kv.Value.KingdomId) ? kv.Key : kv.Value.KingdomId;
            foreach (var courtier in kv.Value.Courtiers)
            {
                if (courtier == null || string.IsNullOrEmpty(courtier.EstateProvinceId))
                    continue;
                if (!realmOf.TryGetValue(courtier.EstateProvinceId, out string owner) || owner != realm)
                    stray++;
            }
        }
        return stray;
    }

    // ── Influence helpers ─────────────────────────────────────────────────
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs",
        "2ee0f8cd3a8483f67662bd1317a4b3b4dae73a24",
        "2b18da456b77ed4227d53116dd2c9d14788f1501",
        [
            (
                r"""    /// <summary>Mark a world POI consumed (resolved) so it isn't re-offered.</summary>
    private void ConsumeWorldPoi(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return;
        var poi = _world.PoiAt(col, row);
""",
                r"""    /// <summary>Mark a world POI consumed (resolved) so it isn't re-offered. Returns the
    /// place it consumed, or null when there was none left to consume (provinces PV2b-1: a
    /// won fight's deed reads it).</summary>
    private WorldPoi ConsumeWorldPoi(Vector2I local)
    {
        if (!_window.TryLocalToWorld(local, out int col, out int row))
            return null;
        var poi = _world.PoiAt(col, row);
""",
            ),
            (
                r"""            HarvestSites.OnConsumed(SaveManager.ActiveSave?.Cycle, poi);
            SaveManager.MarkDirty();
        }
    }
""",
                r"""            HarvestSites.OnConsumed(SaveManager.ActiveSave?.Cycle, poi);
            SaveManager.MarkDirty();
            return poi;
        }
        return null;
    }
""",
            ),
            (
                r"""            ConsumeOverlayPoi(resultHex);   // Step 2: unloaded-guard built into the seam
            ConsumeWorldPoi(resultHex);
            GrantStagingPointAt(resultHex); // securing a seat/settlement via combat can grant staging
            ShowInfo($"Victory! +{router.GoldReward} gold, +{router.SplinterReward} Splinters, +{router.MaterialReward} materials.");
            EmitCombatDeed(router, resultHex);
""",
                r"""            ConsumeOverlayPoi(resultHex);   // Step 2: unloaded-guard built into the seam
            var clearedSite = ConsumeWorldPoi(resultHex);
            GrantStagingPointAt(resultHex); // securing a seat/settlement via combat can grant staging
            ShowInfo($"Victory! +{router.GoldReward} gold, +{router.SplinterReward} Splinters, +{router.MaterialReward} materials.");
            EmitCombatDeed(router, resultHex);
            // Provinces PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md): a site cleared is a deed
            // in its province, weighed by its realm's court. A site that grants a staging point
            // counted as secured just above, so it is not paid twice.
            if (clearedSite != null && !clearedSite.GrantsStaging)
            {
                string clearedNews = ProvinceInfluence.Deed(SaveManager.ActiveSave?.Cycle, clearedSite.X, clearedSite.Y,
                                                            ProvinceInfluence.SiteClearedGain);
                if (!string.IsNullOrEmpty(clearedNews))
                    _toasts?.Push(clearedNews, QuestToastKind.Complete);
            }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/CityContractService.cs",
        "2f80868c962c975e766e286e851eac3faf06dae2",
        "da6cb72d73725e82e3dd3377b19f8a4656c7ae9e",
        [
            (
                r"""    /// <summary>Pay out a completed contract at its board and remove the posting:
    /// gold now, and word of dependable work reaching the kingdom's court
    /// (Steward-routed echo, minor positive). Returns the echo's toast line
    /// (null when the kingdom has no court).</summary>
""",
                r"""    /// <summary>Pay out a completed contract at its board and remove the posting:
    /// gold now, a deed in the city's province (provinces PV2b-1), and word of
    /// dependable work reaching the kingdom's court (Steward-routed echo, minor
    /// positive). Returns the echo's toast line (null when the kingdom has no
    /// court).</summary>
""",
            ),
            (
                r"""        if (save != null) save.Gold += c.GoldReward;
        board.Offers.Remove(c);
        SaveManager.MarkDirty();
""",
                r"""        if (save != null) save.Gold += c.GoldReward;
        board.Offers.Remove(c);
        // Provinces PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md): dependable work for a
        // city is a deed in the city's province, weighed by its realm's court.
        if (TryBoardCenter(board, out int cityX, out int cityY))
            ProvinceInfluence.Deed(cycle, cityX, cityY, ProvinceInfluence.ContractGain);
        SaveManager.MarkDirty();
""",
            ),
            (
                r"""        int i = board.CityId.IndexOf(':');
        return i <= 0 ? "" : board.CityId.Substring(0, i);
    }
""",
                r"""        int i = board.CityId.IndexOf(':');
        return i <= 0 ? "" : board.CityId.Substring(0, i);
    }

    /// <summary>The tile of a board's city: the centre its CityId carries after the
    /// kingdom ("{KingdomId}:{Cx},{Cy}"). False for an id that carries none.</summary>
    public static bool TryBoardCenter(CityContractBoardState board, out int x, out int y)
    {
        x = -1;
        y = -1;
        if (board == null || string.IsNullOrEmpty(board.CityId))
            return false;
        int colon = board.CityId.IndexOf(':');
        if (colon < 0)
            return false;
        string[] parts = board.CityId.Substring(colon + 1).Split(',');
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, invariant, out x)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, invariant, out y);
    }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/LeadService.cs",
        "c45e9a45174ab6381bebb0a25206ddb2dbb002d9",
        "0812dbec6bffef2d5d93bb9c208b6d676279b52f",
        [
            (
                r"""    /// (CampSight.PlacePhrase). Placeholder copy (D186).</summary>
    private static void AnnounceHeld(CycleState cycle, Lead lead, WorldPoi poi)
""",
                r"""    /// (CampSight.PlacePhrase). Placeholder copy (D186). Since provinces PV2b-1 it is also
    /// a deed in the place's province (ProvinceInfluence.LeadGain).</summary>
    private static void AnnounceHeld(CycleState cycle, Lead lead, WorldPoi poi)
""",
            ),
            (
                r"""        GD.Print($"[Leads] {lead.Id} held true: {what} at ({poi.X},{poi.Y}).");
    }
""",
                r"""        GD.Print($"[Leads] {lead.Id} held true: {what} at ({poi.X},{poi.Y}).");
        // Provinces PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md): a rumor followed home is
        // a deed in its place's province, once, as the lead goes.
        ProvinceInfluence.Deed(cycle, poi.X, poi.Y, ProvinceInfluence.LeadGain);
    }
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/KingdomTickSimulation.cs",
        "143dd1bea9237d4676800dfd9e1bfc01e312df59",
        "b68b3bfae30c073d8c2a07278b4c83162093564c",
        [
            (
                r"""            if (k.PlayerInfluence >= InfluenceStabilizeThreshold)
                delta += StabilityFromInfluence;
""",
                r"""            // Provinces PR22 (built on its default, docs/provinces_v1_7g_deeds_and_courts.md
            // section 10): a realm where the guild holds a province steadies as one at 40 did.
            if (k.PlayerInfluence >= InfluenceStabilizeThreshold || ProvinceInfluence.HoldsAny(world, kid))
                delta += StabilityFromInfluence;
""",
            ),
            (
                r"""            case WarfrontSide.Aid:
                wf.Advance += success ? WarfrontWinSwing : -WarfrontLossSwing;
""",
                r"""            case WarfrontSide.Aid:
                // Provinces PR20 (docs/provinces_v1_7g_deeds_and_courts.md section 8): marching with
                // the invader, won or lost, costs Regard at a court that welcomed the guild.
                ProvinceCourts.Betrayal(cycle, wf.DefenderKingdomId);
                wf.Advance += success ? WarfrontWinSwing : -WarfrontLossSwing;
""",
            ),
        ],
    ),
    (
        "Scripts/Systems/Strategic/StrategicView.ProvinceHover.cs",
        "0d88ebddd429de03a034e3b4e9849bf4612ef1d1",
        "f7c9d177bf94c4a10d894ee0c8b71aa4d8ee4d5e",
        [
            (
                r"""//                 next lunation will do to it, and why. While it shows,
""",
                r"""//                 next lunation will do to it, and why; and the
//                 courtiers estated there and their court's weight on
//                 deeds (section 7g). While it shows,
""",
            ),
            (
                r"""//                 the lines the key names), CouncilTick.cs (StanceFor),
""",
                r"""//                 the lines the key names), ProvinceCourts.cs (the
//                 court line), CouncilTick.cs (StanceFor),
""",
            ),
            (
                r"""    private Label _provinceHoverNext;
    private Label _lensKey;
""",
                r"""    private Label _provinceHoverNext;
    private Label _provinceHoverCourt;
    private Label _lensKey;
""",
            ),
            (
                r"""        _provinceHoverNext.Text = ProvinceInfluence.NextLunationLine(cycle, _provinceHoverIndex);
        _provinceHoverPanel.Visible = true;
""",
                r"""        _provinceHoverNext.Text = ProvinceInfluence.NextLunationLine(cycle, _provinceHoverIndex);
        // Provinces 7g (PV2b-1): the estates there and the court's weight on deeds.
        string courtLine = ProvinceCourts.ReadoutLine(cycle, province);
        _provinceHoverCourt.Text = courtLine;
        _provinceHoverCourt.Visible = courtLine.Length > 0;
        _provinceHoverPanel.Visible = true;
""",
            ),
            (
                r"""        _provinceHoverNext = HoverLabel(UITheme.CampusTinyFontSize, UITheme.TextSecondary);
        stack.AddChild(_provinceHoverNext);
    }
""",
                r"""        _provinceHoverNext = HoverLabel(UITheme.CampusTinyFontSize, UITheme.TextSecondary);
        stack.AddChild(_provinceHoverNext);
        _provinceHoverCourt = HoverLabel(UITheme.CampusTinyFontSize, UITheme.TextSecondary);
        _provinceHoverCourt.Visible = false;
        stack.AddChild(_provinceHoverCourt);
    }
""",
            ),
        ],
    ),
]

def fail(msg: str) -> None:
    print("REFUSED: " + msg)
    sys.exit(1)


def added_chars_ok(anchor: str, new: str):
    """Characters the edit adds must be ASCII or box-drawing (U+2500..U+257F)."""
    extra = collections.Counter(c for c in new if ord(c) > 127)
    extra.subtract(collections.Counter(c for c in anchor if ord(c) > 127))
    return [c for c, k in extra.items() if k > 0 and not (0x2500 <= ord(c) <= 0x257F)]


def check_text(name: str, text: str) -> None:
    if "\r" in text:
        fail(f"{name} has CR characters")
    bad = sorted({c for c in text if ord(c) > 127 and not (0x2500 <= ord(c) <= 0x257F)})
    if bad:
        fail(f"{name} carries characters outside ASCII and box drawing: {bad!r}")
    if EM_DASH in text or ESCAPED_EM_DASH in text or EN_DASH in text:
        fail(f"{name} carries a dash the house style bans")


def main() -> None:
    if len(sys.argv) < 2:
        fail("usage: " + Path(__file__).name + " <repo_root> [--dry-run]")
    root = Path(sys.argv[1])
    dry = "--dry-run" in sys.argv[2:]

    drops = []
    for rel, before, after in DROP_INS:
        target = root / rel
        if before is None:
            if target.exists():
                fail(f"{rel} already exists (blob {blob(target.read_bytes())})")
            got = None
        else:
            if not target.exists():
                fail(f"{rel} is missing")
            got = blob(target.read_bytes())
            if got != before:
                fail(f"{rel} is at blob {got}, expected {before}")
        source = HERE / Path(rel).name
        if not source.exists():
            fail(f"{source.name} is missing beside this script")
        data = source.read_bytes()
        check_text(source.name, data.decode("utf-8"))
        if blob(data) != after:
            fail(f"{source.name} is at blob {blob(data)}, expected {after}")
        drops.append((rel, target, data, got, after))

    results = []
    for rel, expected, expected_after, edits in EDITS:
        path = root / rel
        data = path.read_bytes()
        got = blob(data)
        if got != expected:
            fail(f"{rel} is at blob {got}, expected {expected}")
        text = data.decode("utf-8")
        if "\r" in text:
            fail(f"{rel} has CR characters; this script writes LF only")
        original = text
        for anchor, new in edits:
            count = text.count(anchor)
            if count != 1:
                fail(f"{rel}: anchor found {count} times: {anchor.splitlines()[0]!r}")
            bad = added_chars_ok(anchor, new)
            if bad:
                fail(f"{rel}: replacement adds characters outside ASCII and box drawing: {bad!r}")
            if EM_DASH in new or EN_DASH in new or ESCAPED_EM_DASH in new:
                fail(f"{rel}: replacement carries a dash the house style bans")
            for o, c in PAIRS:
                d_open = new.count(o) - anchor.count(o)
                d_close = new.count(c) - anchor.count(c)
                if d_open != d_close:
                    fail(f"{rel}: replacement unbalances {o}{c} ({d_open} vs {d_close})")
            text = text.replace(anchor, new, 1)
        out = text.encode("utf-8")
        if blob(out) != expected_after:
            fail(f"{rel}: the edited file is at blob {blob(out)}, not the {expected_after} it was validated at")
        results.append((rel, path, got, expected_after, original, text, out))

    for rel, target, data, before, after in drops:
        print(f"{rel}: {'new' if before is None else before[:7]} -> {after[:7]}")
    for rel, path, before, after, original, text, out in results:
        diff = list(difflib.unified_diff(original.splitlines(), text.splitlines(),
                                         fromfile="a/" + rel, tofile="b/" + rel, lineterm="", n=0))
        added = sum(1 for l in diff if l.startswith("+") and not l.startswith("+++"))
        removed = sum(1 for l in diff if l.startswith("-") and not l.startswith("---"))
        print(f"{rel}: {before[:7]} -> {after[:7]}  (+{added} -{removed})")

    if dry:
        print("DRY RUN: nothing written.")
        return

    for rel, target, data, before, after in drops:
        target.parent.mkdir(parents=True, exist_ok=True)
        with open(target, "wb") as fh:
            fh.write(data)
    for rel, path, before, after, original, text, out in results:
        with open(path, "wb") as fh:
            fh.write(out)
    for rel, target, data, before, after in drops:
        if blob(target.read_bytes()) != after:
            fail(f"{rel}: did not verify after writing")
    for rel, path, before, after, original, text, out in results:
        if blob(path.read_bytes()) != after:
            fail(f"{rel}: did not verify after writing")
    print(f"WRITTEN: all {len(drops) + len(results)} files verified against their expected blobs.")


if __name__ == "__main__":
    main()
