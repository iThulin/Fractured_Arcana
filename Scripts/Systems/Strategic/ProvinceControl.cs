using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ProvinceControl.cs
//
// Purpose:        Who rules each province (docs/provinces_v1_8_
//                 fronts_per_province.md, slice PV3a; rulings PR51 to
//                 PR60 ruled on their defaults, 2026-11-05). A
//                 province is ruled by its own kingdom until a front
//                 takes it; then by the kingdom that took it, by the
//                 guild (a Seize, a collapse, a claim), or by no one
//                 (its realm fell with its capital and the guild did
//                 not hold it). The ruler is a kingdom, not a faction
//                 (PR51): everything a war touches is a kingdom's.
//                 This reads the ruler for the strategic layer (the
//                 threat rolls, the readout, the card, the lens), and
//                 answers for legacy realms (section 7). It writes a
//                 change of hands: the caches follow (PR58), a province
//                 no one rules becomes the guild's once held (PR10), a
//                 Seize turns the court Hostile (PR56), and a collapse
//                 costs the courts it touches a standing step (PR55).
// Layer:          System
// Collaborators:  Province.cs (ControllerId, ControlSinceLunation),
//                 KingdomTickSimulation.Fronts.cs (the fronts that
//                 change hands), ProvinceInfluence.cs (the claim, the
//                 guild's own land), SupplyCacheSystem.cs
//                 (FollowProvince), CastleThreats.cs, FieldThreats.cs,
//                 PostingThreats.cs and SortieThreats.cs (StanceAt,
//                 KingdomAt), StrategicView.Rulers.cs and
//                 WorldAtlas3D.Rulers.cs (the readout, the card, the
//                 map), CouncilState.cs (the courts),
//                 ProvinceSaveAssert.Fronts.cs
// See:            docs/provinces_v1_8_fronts_per_province.md
//                 sections 3 and 4
// ============================================================

/// <summary>Reads and changes who rules a province. Stateless: the ruler lives on the
/// Province (ControllerId); the courts and the kingdoms keep the rest.</summary>
public static class ProvinceControl
{
    // ── Rulers that are not kingdoms ─────────────────────────────────────

    /// <summary>ControllerId of a province no one rules: its realm fell with its capital and
    /// the guild did not hold it.</summary>
    public const string Neutral = "neutral";

    /// <summary>ControllerId of the guild's own province: the sentinel the strategic layer
    /// already uses for the guild.</summary>
    public const string Guild = KingdomTickSimulation.GuildFactionId;

    // ── Tuning (section 6) ───────────────────────────────────────────────

    /// <summary>Where a front starts on the next province after taking one (PR52): an army
    /// that has just won carries its momentum.</summary>
    public const int FrontMomentumAdvance = 60;

    /// <summary>StandingPenalty a court takes when the guild gains provinces from a realm's
    /// fall (PR55, PR20's second half): one step.</summary>
    public const int CollapseStandingStep = 3;

    /// <summary>The standing score a Seize leaves the defender's court at (PR56): the top of
    /// the Hostile band.</summary>
    public const int HostileScore = -6;

    /// <summary>The marks the frontier news puts before the guild's gains and its losses, as
    /// the older lines do.</summary>
    public const string GainMark = "\u2605 ";
    public const string LossMark = "\u26A0 ";

    // ══════════════════════════════════════════════════════════════════════
    // Reading
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The province's ruler: its ControllerId when one is set; otherwise its own
    /// kingdom (Province.KingdomId, "" for land no kingdom owns), or the guild when the guild
    /// seized that whole kingdom before fronts walked (section 7: such a realm reads as the
    /// guild's on every province). "" for no province.</summary>
    public static string RulerOf(Province province, Dictionary<string, KingdomState> kingdoms)
    {
        if (province == null)
            return "";
        if (!string.IsNullOrEmpty(province.ControllerId))
            return province.ControllerId;
        string home = province.KingdomId ?? "";
        if (home.Length > 0 && kingdoms != null && kingdoms.TryGetValue(home, out var kingdom)
            && kingdom != null && kingdom.ControllingFactionId == Guild)
            return Guild;
        return home;
    }

    /// <summary>The province's ruler, with the cycle's kingdoms (RulerOf).</summary>
    public static string RulerOf(CycleState cycle, Province province) => RulerOf(province, cycle?.Kingdoms);

    /// <summary>True when a ruler is a kingdom: not the guild, not no one, and not land no
    /// kingdom owns.</summary>
    public static bool IsKingdom(string ruler)
        => !string.IsNullOrEmpty(ruler) && ruler != Guild && ruler != Neutral;

    /// <summary>Position in WorldData.Provinces of the province with a stable id, or -1.</summary>
    public static int IndexOf(WorldData world, string id)
    {
        if (world?.Provinces == null || string.IsNullOrEmpty(id))
            return -1;
        for (int i = 0; i < world.Provinces.Count; i++)
        {
            if (world.Provinces[i] != null && world.Provinces[i].Id == id)
                return i;
        }
        return -1;
    }

    /// <summary>The province with a stable id, or null.</summary>
    public static Province ProvinceById(WorldData world, string id)
    {
        int index = IndexOf(world, id);
        return index >= 0 ? world.Provinces[index] : null;
    }

    /// <summary>The ruler of the province holding (x,y); off the provinces (water, land that
    /// joined none, a world without them) the tile's own kingdom, as before PV3a. "" off the
    /// map.</summary>
    public static string RulerAt(CycleState cycle, int x, int y)
    {
        var world = cycle?.World;
        if (world == null || !world.InBounds(x, y))
            return "";
        var province = world.ProvinceAt(x, y);
        return province != null ? RulerOf(cycle, province) : (world.GetTile(x, y).KingdomId ?? "");
    }

    /// <summary>The kingdom whose soldiers walk (x,y) (PR57): the kingdom that rules it, or ""
    /// where the guild or no one rules, or no kingdom does. A threat roll names its attacker
    /// by it.</summary>
    public static string KingdomAt(CycleState cycle, int x, int y)
    {
        string ruler = RulerAt(cycle, x, y);
        return IsKingdom(ruler) ? ruler : "";
    }

    /// <summary>How whoever rules (x,y) regards the guild (PR57): the ruling kingdom's court,
    /// through CouncilQueries.StanceFor; Allied on the guild's own land; Neutral where no one
    /// rules; and where no kingdom does (the wilds, off the map), what the caller passes, so
    /// each roll keeps the odds it always had there.</summary>
    public static KingdomStance StanceAt(CycleState cycle, int x, int y, KingdomStance wilds)
    {
        string ruler = RulerAt(cycle, x, y);
        if (ruler == Guild)
            return KingdomStance.Allied;
        if (ruler == Neutral)
            return KingdomStance.Neutral;
        return string.IsNullOrEmpty(ruler) ? wilds : CouncilQueries.StanceFor(cycle, ruler);
    }

    /// <summary>True when the kingdom fell with its capital (section 3.3): another rules its
    /// capital province. Such a realm no longer ticks, presses or fights, and a capital once
    /// taken is never retaken by its own realm. A realm that fell whole before fronts walked
    /// kept its capital's ControllerId empty, so it reads as standing and keeps the old model
    /// (section 7).</summary>
    public static bool KingdomFallen(CycleState cycle, string kingdomId)
    {
        var world = cycle?.World;
        if (world == null || string.IsNullOrEmpty(kingdomId) || !world.HasProvinces())
            return false;
        foreach (var province in world.Provinces)
        {
            if (province != null && province.IsCapital && province.KingdomId == kingdomId)
                return !string.IsNullOrEmpty(province.ControllerId) && province.ControllerId != kingdomId;
        }
        return false;
    }

    /// <summary>The kingdom's capital province, or its first province when it has no capital,
    /// or -1 when it has none.</summary>
    public static int CapitalIndex(WorldData world, string kingdomId)
    {
        if (world?.Provinces == null || string.IsNullOrEmpty(kingdomId))
            return -1;
        int first = -1;
        for (int i = 0; i < world.Provinces.Count; i++)
        {
            var province = world.Provinces[i];
            if (province == null || province.KingdomId != kingdomId)
                continue;
            if (province.IsCapital)
                return i;
            if (first < 0)
                first = i;
        }
        return first;
    }

    /// <summary>A kingdom's own name (KingdomState.DisplayName), or its id.</summary>
    public static string RealmName(CycleState cycle, string kingdomId)
    {
        if (cycle?.Kingdoms != null && !string.IsNullOrEmpty(kingdomId)
            && cycle.Kingdoms.TryGetValue(kingdomId, out var kingdom) && kingdom != null
            && !string.IsNullOrEmpty(kingdom.DisplayName))
            return kingdom.DisplayName;
        return kingdomId ?? "";
    }

    /// <summary>A province's name, or its id when it has none; "" for no province.</summary>
    public static string Name(Province province)
        => province == null ? "" : string.IsNullOrEmpty(province.Name) ? province.Id : province.Name;

    /// <summary>The Province Influence report's tag for a province its own kingdom does not
    /// rule: "  ruled by Eastreach since lunation 7", "  the guild's since lunation 9",
    /// "  no one's since lunation 9, annexing: Eastreach 35" (the bids, PV3b). ""
    /// otherwise.</summary>
    public static string RulerTag(CycleState cycle, Province province)
    {
        if (province == null || string.IsNullOrEmpty(province.ControllerId))
            return "";
        string ruler = RulerOf(cycle, province);
        if (ruler == (province.KingdomId ?? ""))
            return "";
        string since = province.ControlSinceLunation >= 0 ? $" since lunation {province.ControlSinceLunation}" : "";
        if (ruler == Guild)
            return "  the guild's" + since;
        if (ruler == Neutral)
            return "  no one's" + since + AnnexTag(cycle, province);
        return $"  ruled by {RealmName(cycle, ruler)}{since}";
    }

    /// <summary>The report's bids on land no one rules (PV3b): ", annexing: Eastreach 35,
    /// Lostmere 20", the highest bar first, or "" when no realm bids.</summary>
    private static string AnnexTag(CycleState cycle, Province province)
    {
        var bids = KingdomTickSimulation.AnnexBids(cycle, province.Index);
        if (bids.Count == 0)
            return "";
        string tag = ", annexing:";
        for (int n = 0; n < bids.Count; n++)
            tag += (n > 0 ? "," : "") + $" {RealmName(cycle, bids[n].KingdomId)} {bids[n].Bar}";
        return tag;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Changing hands
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Hand a province to a ruler: a kingdom id, Guild or Neutral. Kept as "" when the
    /// ruler is its own kingdom (unless that would read as the guild's: a realm the guild
    /// seized whole before fronts walked). Stamps the lunation it changed hands in, and the
    /// caches its old ruler held there follow it (PR58, SupplyCacheSystem.FollowProvince).
    /// A change of hands ends every bid to annex it (PV3b, PR63). The guild's influence
    /// there is the caller's to set (PR54).</summary>
    public static void SetRuler(CycleState cycle, Province province, string ruler, Func<string, string> fd = null)
    {
        if (province == null || string.IsNullOrEmpty(ruler))
            return;
        string before = RulerOf(cycle, province);
        province.ControllerId = ruler;
        if (ruler == (province.KingdomId ?? ""))
        {
            province.ControllerId = "";
            if (RulerOf(cycle, province) != ruler)
                province.ControllerId = ruler;
        }
        province.ControlSinceLunation = cycle?.Calendar?.CurrentLunation ?? 0;
        if (before != ruler)
            province.Annexation?.Clear();
        if (before != ruler && cycle?.World != null)
            SupplyCacheSystem.FollowProvince(cycle, province.Index, before, ruler, fd);
    }

    /// <summary>The guild's claim (PR10, ruled 2026-11-03): a province no one rules becomes the
    /// guild's once the guild holds it, with no Seize and no hostility. ProvinceInfluence calls
    /// it wherever a hold is settled. Returns the news, or "".</summary>
    public static string ClaimNeutral(CycleState cycle, Province province)
    {
        if (province == null || !province.Held || RulerOf(cycle, province) != Neutral)
            return "";
        SetRuler(cycle, province, Guild);
        string line = $"{GainMark}No one ruled {Name(province)}; the guild's hold makes it the guild's.";
        GD.Print($"[Provinces] {line}");
        return line;
    }

    /// <summary>A Seize turns the defender's court Hostile (PR56, as P2 asked): its
    /// StandingPenalty rises until its score is HostileScore, and the court knows the guild
    /// now (HasContact), so its band reads Hostile rather than Unknown. The Herald's Report
    /// says why. False when the realm has no court.</summary>
    public static bool TurnHostile(CycleState cycle, string kingdomId, string provinceName)
    {
        var court = CourtOf(cycle, kingdomId);
        if (court == null)
            return false;
        court.HasContact = true;
        int score = court.StandingScore();
        if (score > HostileScore)
            court.StandingPenalty += score - HostileScore;
        Herald(cycle, kingdomId,
            $"{CourtName(cycle, kingdomId)} hears that the guild took {provinceName} from the war, "
            + $"and turns against it. Standing: {court.Band()}.");
        return true;
    }

    /// <summary>PR20's second half (PR55): a court loses one standing step when the guild gains
    /// provinces from a realm's fall. The Herald's Report says why. False when the realm has
    /// no court.</summary>
    public static bool StandingStep(CycleState cycle, string kingdomId, string gained, string fallen)
    {
        var court = CourtOf(cycle, kingdomId);
        if (court == null)
            return false;
        court.StandingPenalty += CollapseStandingStep;
        Herald(cycle, kingdomId,
            $"{CourtName(cycle, kingdomId)} marks how the guild came by {gained} when {fallen} fell: "
            + $"standing -{CollapseStandingStep}. Standing: {court.Band()}.");
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internals
    // ══════════════════════════════════════════════════════════════════════

    private static CourtState CourtOf(CycleState cycle, string kingdomId)
    {
        if (string.IsNullOrEmpty(kingdomId) || cycle?.Council?.Courts == null)
            return null;
        return cycle.Council.Courts.TryGetValue(kingdomId, out var court) && court?.Courtiers != null ? court : null;
    }

    private static string CourtName(CycleState cycle, string kingdomId)
        => cycle?.Kingdoms != null ? CouncilTick.CourtDisplayName(cycle, kingdomId) : kingdomId;

    /// <summary>A line in the Herald's Report, as the courts' other news goes.</summary>
    private static void Herald(CycleState cycle, string kingdomId, string text)
    {
        if (cycle?.Council != null)
        {
            cycle.Council.Reports ??= new List<HeraldReport>();
            cycle.Council.Reports.Add(new HeraldReport
            {
                Lunation = cycle.Calendar?.CurrentLunation ?? 0,
                KingdomId = kingdomId,
                Text = text,
            });
        }
        GD.Print($"[Provinces] {text}");
    }
}
