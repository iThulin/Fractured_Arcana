using Godot;
using System.Collections.Generic;

// ============================================================
// CompanionWhereabouts.cs: where every companion is, derived (2026-09-24)
//
// Purpose:        One answer to "where is Elara". Assignment is spread over
//                 six sources that each own their own truth: Companion.Posting
//                 and FieldPartyId (crew and field), Council.ActiveMissions
//                 (envoys), Council.Imprisoned (the gaol), WorldPoi.Overseer-
//                 CompanionId (depots), InjuredLunationsRemaining (the
//                 infirmary). Nothing here STORES a whereabout; it reads the
//                 six and ranks them, so the muster board, the map figures and
//                 the picker can never disagree with the systems they describe.
// Layer:          Data / FeatureBuilders
// Collaborators:  CycleState, FieldParty, FieldPostings, ExpeditionAnchors,
//                 CrewStations, CouncilQueries, SupplyCacheSystem
// ============================================================

/// <summary>Where a companion is, in the order the sources outrank each other.</summary>
public enum Station
{
    Campus = 0,
    Crew = 1,
    FieldParty = 2,
    Detachment = 3,
    Envoy = 4,
    Overseer = 5,
    Imprisoned = 6,
}

public sealed class Whereabout
{
    public Companion Companion;
    public Station Station;
    /// <summary>The place, for a row: "Castle crew, Helm", "Field Party",
    /// "Ashfeld depot", "the court at Vaelmoor", "the gaol at Vaelmoor".</summary>
    public string Place = "";
    /// <summary>A second line: the posting, the mission, days injured.</summary>
    public string Detail = "";
    /// <summary>The force to select on the map, if any ("" for the castle,
    /// a FieldParty.Id, or null when there is no piece to select).</summary>
    public string PieceId;
    public int X = -1;
    public int Y = -1;
    public CrewStation? CrewStation;
    public bool Injured;
    public bool Able => Companion != null && Companion.IsRecruited && !Companion.IsPermadead && !Injured;
}

public static class CompanionWhereabouts
{
    /// <summary>Every recruited, living companion, resolved. Roster order.</summary>
    public static List<Whereabout> Resolve(CycleState cycle)
    {
        var list = new List<Whereabout>();
        if (cycle?.Companions == null)
        {
            return list;
        }

        // Crew stations are computed the way the sortie computes them, from
        // the able crew, so the muster names the station the Helm will use.
        var crewAssign = CrewStations.AutoAssign(ExpeditionAnchors.Crew(cycle));
        var stationOf = new Dictionary<string, CrewStation>();
        foreach (var kv in crewAssign)
        {
            if (kv.Value != null)
            {
                stationOf[kv.Value.Id] = kv.Key;
            }
        }

        foreach (var c in cycle.Companions)
        {
            if (c == null || !c.IsRecruited || c.IsPermadead)
            {
                continue;
            }
            var w = new Whereabout { Companion = c, Injured = c.IsInjured };

            if (CouncilQueries.IsImprisoned(c.Id))
            {
                w.Station = Station.Imprisoned;
                var gaol = cycle.Council?.Imprisoned?.Find(i => i != null && i.CompanionId == c.Id);
                string court = gaol != null ? CouncilTick.CourtDisplayName(cycle, gaol.KingdomId) : "a foreign court";
                w.Place = $"the gaol at {court}";
                w.Detail = "Held. The prison is a place on the map; a party can walk in after them.";
                if (gaol != null)
                {
                    w.X = gaol.PrisonX;
                    w.Y = gaol.PrisonY;
                }
            }
            else if (CouncilQueries.IsOnMission(c.Id))
            {
                w.Station = Station.Envoy;
                EnvoyMission m = null;
                foreach (var mission in cycle.Council?.ActiveMissions ?? new List<EnvoyMission>())
                {
                    if (mission != null && mission.CompanionId == c.Id)
                    {
                        m = mission;
                        break;
                    }
                }
                string court = m != null ? CouncilTick.CourtDisplayName(cycle, m.KingdomId) : "a court";
                var def = m != null ? CouncilMissions.Get(m.MissionType) : null;
                w.Place = $"the court at {court}";
                w.Detail = m == null ? "envoy"
                         : m.Recalled ? "recalled, a moon from home"
                         : $"{def?.DisplayName ?? m.MissionType}, {m.LunationsRemaining} moon(s) left";
                // An envoy posted from the field still belongs to a detachment.
                var det = PartyOf(cycle, c);
                if (det != null)
                {
                    w.PieceId = det.Id;
                    w.X = det.X;
                    w.Y = det.Y;
                }
            }
            else if (c.Posting == CompanionPosting.Field)
            {
                var party = PartyOf(cycle, c);
                if (party != null && FieldPostings.IsDetachment(party))
                {
                    w.Station = Station.Detachment;
                    w.Place = party.Name;
                    w.Detail = party.State == FieldPartyState.Working
                        ? FieldPostings.Describe(cycle, party)
                        : "awaiting collection";
                }
                else
                {
                    w.Station = Station.FieldParty;
                    w.Place = party?.Name ?? "Field Party";
                    w.Detail = party == null ? ""
                             : party.Sortie != null && party.Sortie.IsLive() ? "in the field"
                             : party.State == FieldPartyState.Travelling ? "on the road"
                             : party.State == FieldPartyState.Working ? FieldPostings.Describe(cycle, party)
                             : "with the party";
                }
                if (party != null)
                {
                    w.PieceId = party.Id;
                    w.X = party.X;
                    w.Y = party.Y;
                }
                // An overseer is a field posting that happens to hold a depot:
                // the depot is the more useful word for the place.
                if (SupplyCacheSystem.IsOverseer(c.Id))
                {
                    w.Station = Station.Overseer;
                    var depot = DepotOf(cycle, c.Id);
                    w.Place = depot != null ? $"the depot in {SupplyCacheSystem.HostName(cycle, depot)}" : "a depot";
                    w.Detail = party != null ? $"overseer, with {party.Name}" : "overseer";
                }
            }
            else if (SupplyCacheSystem.IsOverseer(c.Id))
            {
                // An overseer assigned before overseer-by-garrison was ruled
                // (a save from before 2026-09-24). Still true, still shown.
                w.Station = Station.Overseer;
                var depot = DepotOf(cycle, c.Id);
                w.Place = depot != null ? $"the depot in {SupplyCacheSystem.HostName(cycle, depot)}" : "a depot";
                w.Detail = "overseer";
                if (depot != null)
                {
                    w.X = depot.X;
                    w.Y = depot.Y;
                }
            }
            else if (c.Posting == CompanionPosting.Crew)
            {
                w.Station = Station.Crew;
                w.PieceId = "";
                w.X = cycle.CastleX;
                w.Y = cycle.CastleY;
                if (stationOf.TryGetValue(c.Id, out var st))
                {
                    w.CrewStation = st;
                    w.Place = $"Castle crew, {StationName(st)}";
                    var best = CrewStations.BestStationFor(c.PersonalityTrait);
                    w.Detail = best.HasValue && best.Value == st ? "best in slot" : "manning it";
                }
                else
                {
                    w.Place = "Castle crew";
                    w.Detail = w.Injured ? "no station while hurt" : "no station";
                }
            }
            else
            {
                w.Station = Station.Campus;
                w.Place = "Campus";
                w.Detail = "at the guild";
            }

            if (w.Injured)
            {
                w.Detail = $"injured, {c.InjuredLunationsRemaining} lunation(s)"
                         + (string.IsNullOrEmpty(w.Detail) ? "" : $"  ·  {w.Detail}");
            }
            list.Add(w);
        }
        return list;
    }

    /// <summary>School tints of the able members of a force, for the map
    /// figures. Injured members are drawn too, dimmer, so the piece never
    /// looks smaller than it is.</summary>
    public static List<(Color tint, bool injured)> FigureTints(CycleState cycle, IEnumerable<string> companionIds)
    {
        var list = new List<(Color, bool)>();
        if (cycle?.Companions == null || companionIds == null)
        {
            return list;
        }
        foreach (var id in companionIds)
        {
            var c = cycle.Companions.Find(x => x != null && x.Id == id);
            if (c == null || !c.IsRecruited || c.IsPermadead)
            {
                continue;
            }
            list.Add((TintFor(c), c.IsInjured));
        }
        return list;
    }

    public static Color TintFor(Companion c)
    {
        if (c != null && System.Enum.TryParse<CardSchool>(c.School, true, out var school))
        {
            return SchoolColors.GetBorderColor(school);
        }
        return UITheme.Gold;
    }

    public static string StationName(CrewStation st) => st switch
    {
        global::CrewStation.Helm => "Helm",
        global::CrewStation.Furnace => "Furnace",
        global::CrewStation.LensRoom => "Lens Room",
        global::CrewStation.Quartermaster => "Quartermaster",
        global::CrewStation.Wardroom => "Wardroom",
        _ => st.ToString(),
    };

    public static string StationTitle(Station s) => s switch
    {
        Station.Campus => "At the campus",
        Station.Crew => "Castle crew",
        Station.FieldParty => "Field parties",
        Station.Detachment => "Detachments",
        Station.Envoy => "At court",
        Station.Overseer => "Overseeing depots",
        Station.Imprisoned => "Imprisoned",
        _ => s.ToString(),
    };

    private static FieldParty PartyOf(CycleState cycle, Companion c)
    {
        if (cycle?.FieldParties == null || c == null)
        {
            return null;
        }
        string id = string.IsNullOrEmpty(c.FieldPartyId) ? ExpeditionAnchors.PrimaryFieldPartyId : c.FieldPartyId;
        return cycle.FieldParties.Find(p => p != null && p.Id == id);
    }

    private static WorldPoi DepotOf(CycleState cycle, string companionId)
    {
        var world = cycle?.World;
        if (world?.Pois == null)
        {
            return null;
        }
        foreach (var p in world.Pois)
        {
            if (p != null && p.Kind == PoiKind.SupplyCache && p.OverseerCompanionId == companionId)
            {
                return p;
            }
        }
        return null;
    }
}
