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
    /// <summary>Longest job the skeleton accepts, in days. A guard against a
    /// typo, not a design number.</summary>
    public const int MaxDays = 8 * CalendarState.DaysPerLunation;

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
    public static string Begin(CycleState cycle, FieldParty party, string kind, string zoneId, int days)
    {
        if (!CanBegin(cycle, party, out _))
        {
            return null;
        }
        // Field Party v1 (2026-09-24): 0 days is OPEN-ENDED. A garrison or a
        // held line runs until stopped; StepDay does not count it down.
        days = days <= 0 ? 0 : Mathf.Clamp(days, 1, MaxDays);
        party.State = FieldPartyState.Working;
        party.WorkKind = string.IsNullOrEmpty(kind) ? "work" : kind;
        party.WorkZoneId = zoneId ?? "";
        party.WorkDaysLeft = days;
        party.WorkDaysTotal = days;
        string span = days > 0 ? $"{days} day(s)" : "until recalled";
        GD.Print($"[FieldWork] {party.Name} begins {party.WorkKind} at {party.WorkZoneId}, {span}.");
        return $"{party.Name} begins {Verb(party.WorkKind)} at ({party.X},{party.Y}): {span}.";
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
        int done = party.WorkDaysTotal - party.WorkDaysLeft;
        ClearWork(party);
        GD.Print($"[FieldWork] {party.Name} stops {kind} after {done} day(s).");
        return $"{party.Name} stops {Verb(kind)} after {done} day(s). Nothing comes of it.";
    }

    /// <summary>One DAY for every working party. Returns a line per job that
    /// finished, or null. Jobs in progress say nothing: the roster shows the
    /// clock, and a line a day would bury the events that matter.</summary>
    public static string StepDay(CycleState cycle)
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
            // Cost is paid at the new moon in Supplies (FieldPostings.TickLunation),
            // not here: the day clock only counts a timed job down.
            if (party.WorkDaysTotal <= 0)
            {
                continue;   // open-ended: a garrison or a held line never "finishes"
            }
            party.WorkDaysLeft = Mathf.Max(0, party.WorkDaysLeft - 1);
            if (party.WorkDaysLeft > 0)
            {
                continue;
            }
            string line = OnComplete(cycle, party);
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
        // Field Party v1 (2026-09-24): the postings own their completions.
        // A survey is the only timed one so far; the open-ended kinds never
        // reach here.
        string posted = FieldPostings.OnComplete(cycle, party, kind, zone);
        if (posted != null)
        {
            return posted;
        }
        switch (kind)
        {
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
            : p.WorkDaysTotal > 0
                ? $"{Verb(p.WorkKind)}, {p.WorkDaysLeft} of {p.WorkDaysTotal} day(s) left"
                : $"{Verb(p.WorkKind)}, until recalled";

    private static void ClearWork(FieldParty party)
    {
        party.State = FieldPartyState.AtAnchor;
        party.WorkKind = "";
        party.WorkZoneId = "";
        party.WorkDaysLeft = 0;
        party.WorkDaysTotal = 0;
        party.WorkStalled = false;
        party.WorkProgress = 0;
        party.WorkSide = 0;
    }

    private static string Verb(string kind)
    {
        switch (kind)
        {
            case "survey": return "surveying";
            case "garrison": return "garrisoning";
            case "hold": return "holding the line";
            case "siege": return "besieging";
            default: return string.IsNullOrEmpty(kind) ? "working" : kind;
        }
    }
}
