using Godot;
using System.Text;
using System.Text.Json;

// ============================================================
// CouncilSaveAssert.cs
//
// Purpose:        Round-trip assertions for the council's save-adjacent
//                 structs — the save-file-paranoia rule made real in
//                 code, not just promised in a header comment. Each
//                 struct is built with distinctive non-default values,
//                 pushed through the EXACT serializer the save uses
//                 (SaveManager.JsonOptions), read back, and compared
//                 field-by-field. A dropped or renamed field flips the
//                 comparison and is reported loudly.
//
//                 Covers the three structs flagged as owed:
//                   - HeraldReport
//                   - CourtState (emphasis: StandingPenalty, plus a
//                     nested CourtierState so the list round-trips)
//                   - ImprisonedEnvoy
//
//                 Read-only: builds throwaway instances, touches no
//                 ActiveSave, marks nothing dirty. Safe to run anytime.
// Layer:          System (debug / verification)
// Collaborators:  SaveManager.cs (JsonOptions — the real path),
//                 CouncilState.cs (the structs under test)
// See:            court_council_system_v1_1.docx §8; save-file-paranoia
//                 rule (every save-adjacent struct asserted before ship)
//
// Usage: wired to the CampusScreen debug panel ("Assert Round-Trips"),
//        or call directly: CouncilSaveAssert.AssertAll();
// ============================================================

public static class CouncilSaveAssert
{
    /// <summary>Run every council round-trip assertion. Prints a PASS/FAIL
    /// report to the Output panel and PushErrors on any failure. Returns true
    /// only if all structs survived the round-trip intact.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== COUNCIL SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = true;
        ok &= AssertHeraldReport(sb);
        ok &= AssertCourtState(sb);
        ok &= AssertImprisonedEnvoy(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED — save-adjacent council structs round-trip clean."
            : "RESULT: FAILURES ABOVE — a field is being dropped or renamed.");
        GD.Print(sb.ToString());

        if (!ok)
        {
            GD.PushError("[CouncilSaveAssert] Round-trip assertion FAILED — see Output panel.");
        }
        return ok;
    }

    /// <summary>Serialize then deserialize through the real save options —
    /// the whole point is to catch drift between a struct and its persistence.</summary>
    private static T RoundTrip<T>(T obj)
    {
        string json = JsonSerializer.Serialize(obj, SaveManager.JsonOptions);
        return JsonSerializer.Deserialize<T>(json, SaveManager.JsonOptions);
    }

    private static bool Check(StringBuilder sb, string field, bool equal)
    {
        if (!equal)
        {
            sb.AppendLine($"    FAIL: {field} did not survive the round-trip.");
        }
        return equal;
    }

    private static bool AssertHeraldReport(StringBuilder sb)
    {
        var src = new HeraldReport
        {
            Lunation = 7,
            KingdomId = "kingdom_3",
            Text = "Word spread — an em dash & ünïcode line.",
        };
        var rt = RoundTrip(src);

        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "HeraldReport.Lunation", rt.Lunation == src.Lunation);
            ok &= Check(sb, "HeraldReport.KingdomId", rt.KingdomId == src.KingdomId);
            ok &= Check(sb, "HeraldReport.Text", rt.Text == src.Text);
        }
        else
        {
            sb.AppendLine("    FAIL: HeraldReport deserialized to null.");
        }

        sb.AppendLine(ok ? "  HeraldReport: PASS" : "  HeraldReport: FAIL");
        return ok;
    }

    private static bool AssertCourtState(StringBuilder sb)
    {
        var src = new CourtState
        {
            KingdomId = "kingdom_5",
            IsRegentCourt = true,
            RegentName = "Regent Vael",
            Exposure = 6,
            PatronCourtierId = "courtier_2",
            HasContact = true,
            MissionFreezeLunations = 3,
            StandingPenalty = 7, // the emphasis: a non-zero lasting mark
        };
        src.Courtiers.Add(new CourtierState
        {
            Id = "courtier_2",
            DisplayName = "Lady Ash",
            Archetype = "Scholar",
            Office = CourtVocab.OfficeChancellor,
            Regard = 2,
            Influence = 3,
            SecretId = "secret_x",
            SecretKnown = true,
            IsCorruptedAgent = true,
        });

        var rt = RoundTrip(src);
        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "CourtState.KingdomId", rt.KingdomId == src.KingdomId);
            ok &= Check(sb, "CourtState.IsRegentCourt", rt.IsRegentCourt == src.IsRegentCourt);
            ok &= Check(sb, "CourtState.RegentName", rt.RegentName == src.RegentName);
            ok &= Check(sb, "CourtState.Exposure", rt.Exposure == src.Exposure);
            ok &= Check(sb, "CourtState.PatronCourtierId", rt.PatronCourtierId == src.PatronCourtierId);
            ok &= Check(sb, "CourtState.HasContact", rt.HasContact == src.HasContact);
            ok &= Check(sb, "CourtState.MissionFreezeLunations",
                rt.MissionFreezeLunations == src.MissionFreezeLunations);
            ok &= Check(sb, "CourtState.StandingPenalty", rt.StandingPenalty == src.StandingPenalty);

            // Nested list must round-trip too, or StandingScore lies.
            ok &= Check(sb, "CourtState.Courtiers.Count", rt.Courtiers.Count == src.Courtiers.Count);
            if (rt.Courtiers.Count == 1)
            {
                var a = src.Courtiers[0];
                var b = rt.Courtiers[0];
                ok &= Check(sb, "CourtierState.Id", b.Id == a.Id);
                ok &= Check(sb, "CourtierState.DisplayName", b.DisplayName == a.DisplayName);
                ok &= Check(sb, "CourtierState.Archetype", b.Archetype == a.Archetype);
                ok &= Check(sb, "CourtierState.Office", b.Office == a.Office);
                ok &= Check(sb, "CourtierState.Regard", b.Regard == a.Regard);
                ok &= Check(sb, "CourtierState.Influence", b.Influence == a.Influence);
                ok &= Check(sb, "CourtierState.SecretId", b.SecretId == a.SecretId);
                ok &= Check(sb, "CourtierState.SecretKnown", b.SecretKnown == a.SecretKnown);
                ok &= Check(sb, "CourtierState.IsCorruptedAgent", b.IsCorruptedAgent == a.IsCorruptedAgent);
            }

            // Derived math must recompute identically post-round-trip. This is the
            // real StandingPenalty tell: score = (2*3) - 7 = -1; if the penalty
            // were dropped to 0 it would read +6 instead.
            ok &= Check(sb, "CourtState.StandingScore() (derived, penalty applied)",
                rt.StandingScore() == src.StandingScore() && rt.StandingScore() == -1);
        }
        else
        {
            sb.AppendLine("    FAIL: CourtState deserialized to null.");
        }

        sb.AppendLine(ok ? "  CourtState (+StandingPenalty): PASS" : "  CourtState (+StandingPenalty): FAIL");
        return ok;
    }

    private static bool AssertImprisonedEnvoy(StringBuilder sb)
    {
        var src = new ImprisonedEnvoy
        {
            CompanionId = "comp_1",
            KingdomId = "kingdom_4",
            PrisonX = 37,
            PrisonY = 42,
            LunationImprisoned = 5,
        };
        var rt = RoundTrip(src);

        bool ok = rt != null;
        if (rt != null)
        {
            ok &= Check(sb, "ImprisonedEnvoy.CompanionId", rt.CompanionId == src.CompanionId);
            ok &= Check(sb, "ImprisonedEnvoy.KingdomId", rt.KingdomId == src.KingdomId);
            ok &= Check(sb, "ImprisonedEnvoy.PrisonX", rt.PrisonX == src.PrisonX);
            ok &= Check(sb, "ImprisonedEnvoy.PrisonY", rt.PrisonY == src.PrisonY);
            ok &= Check(sb, "ImprisonedEnvoy.LunationImprisoned",
                rt.LunationImprisoned == src.LunationImprisoned);
        }
        else
        {
            sb.AppendLine("    FAIL: ImprisonedEnvoy deserialized to null.");
        }

        sb.AppendLine(ok ? "  ImprisonedEnvoy: PASS" : "  ImprisonedEnvoy: FAIL");
        return ok;
    }
}
