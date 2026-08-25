using Godot;
using System.Text;
using System.Text.Json;

// ============================================================
// ProgressionSaveAssert.cs
//
// Purpose:        Round-trip assertion for the progression save-adjacent
//                 structs added outside the council layer. The same
//                 save-file-paranoia rule CouncilSaveAssert enforces, applied
//                 to the card-progression structs. Each is built with
//                 distinctive non-default values, pushed through the EXACT
//                 serializer the save uses (SaveManager.JsonOptions), read
//                 back, and compared field-by-field. A dropped or renamed
//                 field flips the comparison and is reported loudly.
//
//                 Covers:
//                   - CardCommission (the §8 pity-timer in-flight entry)
//
// Layer:          System (debug / verification)
// Collaborators:  SaveManager.cs (JsonOptions, the real path),
//                 EternalLedger.cs (CardCommission)
// See:            docs/progression_card_acquisition_v1.md §8; save-file-paranoia
//                 rule (every save-adjacent struct asserted before ship)
//
// Usage: wired to the CampusGuildPanel debug panel ("Assert Round-Trips"),
//        or call directly: ProgressionSaveAssert.AssertAll();
// ============================================================

public static class ProgressionSaveAssert
{
    /// <summary>Run every progression round-trip assertion. Prints a PASS/FAIL
    /// report and PushErrors on any failure. Returns true only if all passed.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== PROGRESSION SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = AssertCardCommission(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Progression save-adjacent structs round-trip clean."
            : "RESULT: FAILURES ABOVE. A field is being dropped or renamed.");
        GD.Print(sb.ToString());

        if (!ok)
            GD.PushError("[ProgressionSaveAssert] Round-trip assertion FAILED. See Output panel.");

        return ok;
    }

    private static bool AssertCardCommission(StringBuilder sb)
    {
        var src = new CardCommission
        {
            BlueprintId = "elementalist:Cinderfall|Emberguard",
            LunationsRemaining = 2,
            GoldPaid = 250,
        };
        var rt = RoundTrip(src);

        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "CardCommission.BlueprintId", rt.BlueprintId == src.BlueprintId);
            ok &= Check(sb, "CardCommission.LunationsRemaining",
                rt.LunationsRemaining == src.LunationsRemaining);
            ok &= Check(sb, "CardCommission.GoldPaid", rt.GoldPaid == src.GoldPaid);
        }
        else
        {
            sb.AppendLine("    FAIL: CardCommission deserialized to null.");
        }

        sb.AppendLine(ok ? "  CardCommission: PASS" : "  CardCommission: FAIL");
        return ok;
    }

    // ── Helpers (mirror CouncilSaveAssert: same serializer, same contract) ──

    private static T RoundTrip<T>(T obj)
    {
        string json = JsonSerializer.Serialize(obj, SaveManager.JsonOptions);
        return JsonSerializer.Deserialize<T>(json, SaveManager.JsonOptions);
    }

    private static bool Check(StringBuilder sb, string field, bool equal)
    {
        if (!equal)
            sb.AppendLine($"    FAIL: {field} did not survive the round-trip.");
        return equal;
    }
}
