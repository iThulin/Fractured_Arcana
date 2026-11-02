using Godot;
using System.Text;
using System.Text.Json;

// ============================================================
// IntervalSaveAssert.cs
//
// Purpose:        Save round-trip assertions for the interval's state
//                 (docs/standing_orders_o1_interval_rules_v1.md section
//                 5): IntervalState on CycleState.Interval, with every
//                 field set to a distinctive value, and the additive
//                 default on a save written before the interval. Same
//                 serializer and contract as LeadsSaveAssert.
// Layer:          Verification
// Collaborators:  IntervalState.cs, CycleState.cs, SaveManager.cs
//                 (JsonOptions), CampusGuildPanel.cs (the "Assert
//                 Round-Trips" debug button)
// ============================================================

public static class IntervalSaveAssert
{
    /// <summary>Run every interval round-trip assertion. Prints a PASS/FAIL report and
    /// PushErrors on any failure. Returns true only if all passed.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== INTERVAL SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = AssertEveryField(sb);
        ok &= AssertLegacyDefault(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. The interval round-trips clean."
            : "RESULT: FAILURES ABOVE. A field is being dropped or renamed.");
        GD.Print(sb.ToString());

        if (!ok)
        {
            GD.PushError("[IntervalSaveAssert] Round-trip assertion FAILED. See Output panel.");
        }
        return ok;
    }

    /// <summary>An IntervalState with every field set away from its default survives the trip.</summary>
    private static bool AssertEveryField(StringBuilder sb)
    {
        var src = new CycleState
        {
            Interval = new IntervalState
            {
                Active = true,
                Placed = true,
                DoorSet = true,
                HeldLunation = 3,
                HeldDay = 17,
                RoadX = 41,
                RoadY = 22,
                StoreX = 46,
                StoreY = 25,
                CitySettlement = 7,
            },
        };
        var rt = RoundTrip(src);

        bool ok = rt?.Interval != null;
        if (ok)
        {
            var a = src.Interval;
            var b = rt.Interval;
            ok &= Check(sb, "IntervalState.Active", b.Active == a.Active);
            ok &= Check(sb, "IntervalState.Placed", b.Placed == a.Placed);
            ok &= Check(sb, "IntervalState.DoorSet", b.DoorSet == a.DoorSet);
            ok &= Check(sb, "IntervalState.HeldLunation", b.HeldLunation == a.HeldLunation);
            ok &= Check(sb, "IntervalState.HeldDay", b.HeldDay == a.HeldDay);
            ok &= Check(sb, "IntervalState.RoadX", b.RoadX == a.RoadX);
            ok &= Check(sb, "IntervalState.RoadY", b.RoadY == a.RoadY);
            ok &= Check(sb, "IntervalState.StoreX", b.StoreX == a.StoreX);
            ok &= Check(sb, "IntervalState.StoreY", b.StoreY == a.StoreY);
            ok &= Check(sb, "IntervalState.CitySettlement", b.CitySettlement == a.CitySettlement);
        }
        else
        {
            sb.AppendLine("    FAIL: CycleState.Interval came back null.");
        }
        sb.AppendLine(ok ? "  Every field: PASS" : "  Every field: FAIL");
        return ok;
    }

    /// <summary>A cycle written before the interval reads as one that is not walking.</summary>
    private static bool AssertLegacyDefault(StringBuilder sb)
    {
        var legacy = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":2}", SaveManager.JsonOptions);
        bool ok = Check(sb, "CycleState.Interval present and not walking on legacy JSON",
            legacy?.Interval != null && !legacy.Interval.Active && !legacy.Interval.Placed
            && !legacy.Interval.DoorSet && legacy.Interval.RoadX == -1 && legacy.Interval.CitySettlement == -1);
        ok &= Check(sb, "Interval.Walking false on legacy JSON", !Interval.Walking(legacy));
        sb.AppendLine(ok ? "  Legacy default: PASS" : "  Legacy default: FAIL");
        return ok;
    }

    private static T RoundTrip<T>(T obj)
    {
        string json = JsonSerializer.Serialize(obj, SaveManager.JsonOptions);
        return JsonSerializer.Deserialize<T>(json, SaveManager.JsonOptions);
    }

    private static bool Check(StringBuilder sb, string field, bool equal)
    {
        if (!equal)
        {
            sb.AppendLine($"    FAIL: {field} did not hold.");
        }
        return equal;
    }
}
