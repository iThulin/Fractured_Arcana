using Godot;
using System.Collections.Generic;

// ============================================================
// WorldClock.cs: the day clock under the lunation (2026-09-23)
//
// Purpose:        Ruled 2026-09-23 (Magos): the lunation is a month, orders
//                 cost days, and the world still changes at the new moon.
//                 This is the day-stepping engine. StrategicView drives it:
//                 PassTime advances one day at a time, calls StepDay for the
//                 things that move daily (marches, work, the repair camp,
//                 busy forces coming free), runs RunLunationTick when a day
//                 is a new moon, and stops at the first thing that happened.
//                 Ruled: stop at EVERY event for now; a silent rider that
//                 only stops for decisions comes later, once we know which
//                 events never need one.
// Layer:          System (strategic)
// Collaborators:  CalendarState (AdvanceDay, AbsoluteDay), CycleState
//                 (CastleBusyUntilDay, CastleRepairDayAccum), FieldParty
//                 (BusyUntilDay, TravelDayAccum, Work*), FieldMarch.StepDay,
//                 FieldWork.StepDay, ExpeditionAnchors.TickCastleRepair
// ============================================================

public static class WorldClock
{
    /// <summary>Days a castle sortie costs per fuel burned. A full 40 tank is
    /// twenty days, most of a month; a ten-fuel poke is five. Proportional by
    /// ruling: walking further must cost more than walking less, and the tank
    /// doubles as the time budget.</summary>
    public const float DaysPerFuel = 0.5f;

    /// <summary>Days a field dive costs per ration eaten. Quarter the castle's
    /// rate: a full 40-ration pack is ten days, so about three dives a month,
    /// which is the throughput the old three-per-lunation budget bought.</summary>
    public const float DaysPerRation = 0.25f;

    /// <summary>A sortie is never free, however short.</summary>
    public const int MinSortieDays = 1;

    /// <summary>Days a sortie costs from the fuel it burned.</summary>
    public static int SortieDays(int fuelBurned, bool fieldRun)
    {
        float perUnit = fieldRun ? DaysPerRation : DaysPerFuel;
        return Mathf.Max(MinSortieDays, Mathf.CeilToInt(Mathf.Max(0, fuelBurned) * perUnit));
    }

    public static int Now(CycleState cycle) => cycle?.Calendar?.AbsoluteDay ?? 0;

    public static bool CastleBusy(CycleState cycle)
        => cycle != null && cycle.CastleBusyUntilDay > Now(cycle);

    public static bool PartyBusy(CycleState cycle, FieldParty p)
        => p != null && p.BusyUntilDay > Now(cycle);

    /// <summary>The next absolute day on which something the player ordered
    /// completes, or -1 when nothing is scheduled. The new moon is not in
    /// this list; the caller adds it.</summary>
    public static int NextEventDay(CycleState cycle, out string what)
    {
        what = "";
        int now = Now(cycle);
        int best = -1;
        string bestLabel = "";   // an out parameter cannot be captured by the local function

        void Consider(int day, string label)
        {
            if (day <= now)
            {
                return;
            }
            if (best < 0 || day < best)
            {
                best = day;
                bestLabel = label;
            }
        }

        if (cycle == null)
        {
            return -1;
        }
        if (cycle.CastleBusyUntilDay > now)
        {
            Consider(cycle.CastleBusyUntilDay, "the castle comes free");
        }
        if (cycle.CastleParked && cycle.CastleRepairLunations > 0)
        {
            int daysLeft = cycle.CastleRepairLunations * CalendarState.DaysPerLunation - cycle.CastleRepairDayAccum;
            Consider(now + Mathf.Max(1, daysLeft), "the castle's resupply finishes");
        }
        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p == null)
                {
                    continue;
                }
                if (p.BusyUntilDay > now)
                {
                    Consider(p.BusyUntilDay, $"{p.Name} comes free");
                }
                if (p.State == FieldPartyState.Travelling && p.TravelPhasesRemaining > 0)
                {
                    int days = p.TravelPhasesRemaining * FieldMarch.DaysPerTile - p.TravelDayAccum;
                    Consider(now + Mathf.Max(1, days), $"{p.Name} arrives");
                }
                if (p.State == FieldPartyState.Working && p.WorkDaysLeft > 0)
                {
                    Consider(now + p.WorkDaysLeft, $"{p.Name} finishes their work");
                }
            }
        }
        what = bestLabel;
        return best;
    }

    /// <summary>Everything that moves by the day, for ONE day that has just
    /// been advanced. Appends a line per thing that completed; the caller
    /// stops when the list is non-empty.</summary>
    public static void StepDay(CycleState cycle, List<string> events)
    {
        if (cycle == null || events == null)
        {
            return;
        }
        int now = Now(cycle);

        if (cycle.CastleBusyUntilDay > 0 && cycle.CastleBusyUntilDay == now)
        {
            events.Add("The castle is free to be ordered again.");
        }

        if (cycle.CastleParked && cycle.CastleRepairLunations > 0)
        {
            cycle.CastleRepairDayAccum++;
            if (cycle.CastleRepairDayAccum >= CalendarState.DaysPerLunation)
            {
                cycle.CastleRepairDayAccum = 0;
                if (ExpeditionAnchors.TickCastleRepair(cycle))
                {
                    events.Add("The waystone closes over the camp. The castle is refuelled, restocked and whole, "
                             + "and the hold is home.");
                }
            }
        }
        else
        {
            cycle.CastleRepairDayAccum = 0;
        }

        if (cycle.FieldParties != null)
        {
            foreach (var p in cycle.FieldParties)
            {
                if (p != null && p.BusyUntilDay > 0 && p.BusyUntilDay == now)
                {
                    events.Add($"{p.Name} is free to be ordered again.");
                }
            }
        }

        string march = FieldMarch.StepDay(cycle);
        if (!string.IsNullOrEmpty(march))
        {
            events.Add(march);
        }
        string work = FieldWork.StepDay(cycle);
        if (!string.IsNullOrEmpty(work))
        {
            events.Add(work);
        }
    }
}
