using Godot;

// ============================================================
// FieldWork.cs: field-party zone work, the skeleton (2026-09-23)
//
// Purpose:        A party doing a multi-lunation job where it stands. The
//                 strategic layer owns it: Begin sets the clock, Tick runs it
//                 down each lunation, OnComplete is the one switch every zone
//                 type will add a case to. The expedition scene never sees a
//                 working party; it is one lunation of walking and this is
//                 not that.
// Layer:          Systems / Strategic
// Collaborators:  FieldParty (state + Work fields), CycleState, ScryInbox,
//                 StrategicView (verbs, status, tick), FieldMarch
//                 (CanOrderAtAll refuses a working party)
//
// What is deliberately NOT here yet: what work costs (rations are not
// persisted between sorties, see the 2026-09-23 design note), what it
// yields, and the threat table for a party camped in one place for several
// moons. Each is a ruling, and the skeleton is built so each lands in one
// place: cost in Tick, yield in OnComplete, threats beside SortieThreats.
// ============================================================

public static class FieldWork
{
    /// <summary>Longest job the skeleton accepts. A guard against a typo, not
    /// a design number.</summary>
    public const int MaxLunations = 8;

    /// <summary>Can this party be put to work where it stands, and if not why.</summary>
    public static bool CanBegin(CycleState cycle, FieldParty party, out string reason)
    {
        if (!FieldMarch.CanOrderAtAll(cycle, party, out reason))
        {
            return false;
        }
        if (party.State != FieldPartyState.AtAnchor)
        {
            reason = "The party is not standing free to take up work.";
            return false;
        }
        return true;
    }

    /// <summary>Put the party to work. Returns the report line, or null when
    /// refused.</summary>
    public static string Begin(CycleState cycle, FieldParty party, string kind, string zoneId, int lunations)
    {
        if (!CanBegin(cycle, party, out _))
        {
            return null;
        }
        lunations = Mathf.Clamp(lunations, 1, MaxLunations);
        party.State = FieldPartyState.Working;
        party.WorkKind = string.IsNullOrEmpty(kind) ? "work" : kind;
        party.WorkZoneId = zoneId ?? "";
        party.WorkLunationsLeft = lunations;
        party.WorkLunationsTotal = lunations;
        GD.Print($"[FieldWork] {party.Name} begins {party.WorkKind} at {party.WorkZoneId} for {lunations} lunation(s).");
        return $"{party.Name} begins {Verb(party.WorkKind)} at ({party.X},{party.Y}): {lunations} lunation(s).";
    }

    /// <summary>Stop the job. Nothing is yielded from a job abandoned; the
    /// lunations already spent are simply spent.</summary>
    public static string Cancel(CycleState cycle, FieldParty party)
    {
        if (party == null || party.State != FieldPartyState.Working)
        {
            return null;
        }
        string kind = party.WorkKind;
        int done = party.WorkLunationsTotal - party.WorkLunationsLeft;
        ClearWork(party);
        GD.Print($"[FieldWork] {party.Name} stops {kind} after {done} lunation(s).");
        return $"{party.Name} stops {Verb(kind)} after {done} lunation(s). Nothing comes of it.";
    }

    /// <summary>One lunation for every working party. Returns a joined report
    /// line for the map, or null when nobody was working.</summary>
    public static string Tick(CycleState cycle)
    {
        if (cycle?.FieldParties == null)
        {
            return null;
        }
        string report = null;
        foreach (var party in cycle.FieldParties)
        {
            if (party == null || party.State != FieldPartyState.Working)
            {
                continue;
            }
            // Cost goes HERE when the ration ruling lands: a per-party pool
            // drawn down per lunation, refilled at a parked castle or a dock.
            party.WorkLunationsLeft = Mathf.Max(0, party.WorkLunationsLeft - 1);
            string line;
            if (party.WorkLunationsLeft > 0)
            {
                line = $"{party.Name}: {Verb(party.WorkKind)}, {party.WorkLunationsLeft} lunation(s) to go.";
            }
            else
            {
                line = OnComplete(cycle, party);
            }
            report = report == null ? line : report + "\n" + line;
        }
        return report;
    }

    /// <summary>The job is done. One case per zone type will live here; the
    /// default posts a note to the desk and hands the party back.</summary>
    private static string OnComplete(CycleState cycle, FieldParty party)
    {
        string kind = party.WorkKind;
        string zone = party.WorkZoneId;
        ClearWork(party);
        switch (kind)
        {
            // case "salvage": shard fragment zone ruling goes here.
            // case "resupply": depot ruling goes here.
            // case "trade": city ruling goes here.
            default:
                ScryInbox.Post(cycle, ScryChannel.Note,
                               $"{party.Name}, work done",
                               $"They finish {Verb(kind)} at {zone} and await orders at ({party.X},{party.Y}).",
                               party.Id, "work");
                return $"{party.Name} finishes {Verb(kind)} at ({party.X},{party.Y}).";
        }
    }

    /// <summary>One line for the roster and the desk: what and how long.</summary>
    public static string Describe(FieldParty p)
        => p == null || p.State != FieldPartyState.Working
            ? ""
            : $"{Verb(p.WorkKind)}, {p.WorkLunationsLeft} of {p.WorkLunationsTotal} lunation(s) left";

    private static void ClearWork(FieldParty party)
    {
        party.State = FieldPartyState.AtAnchor;
        party.WorkKind = "";
        party.WorkZoneId = "";
        party.WorkLunationsLeft = 0;
        party.WorkLunationsTotal = 0;
    }

    private static string Verb(string kind)
    {
        switch (kind)
        {
            case "survey": return "surveying";
            case "salvage": return "salvaging";
            case "resupply": return "resupplying";
            case "trade": return "trading";
            default: return string.IsNullOrEmpty(kind) ? "working" : kind;
        }
    }
}
