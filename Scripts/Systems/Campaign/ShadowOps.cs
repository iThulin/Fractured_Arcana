using Godot;
using System.Collections.Generic;

// ============================================================
// ShadowOps.cs
//
// Purpose:        Active, informant-driven espionage verbs (phase E4)
//                 — the player-committed operations that spend an
//                 asset's Cover rather than Concord Favor:
//                   SaboteurStrike() — a Saboteur wrecks a siege or
//                                      stalls a corruption tick (the
//                                      free-of-Favor version of the
//                                      Concord Sabotage contract; §2c).
//                   ForgeEcho()      — a deep Cutout fabricates an
//                                      EchoEvent toward a court; if
//                                      traced it detonates as a court
//                                      Exposure spike (§2c A3).
//                 Both spend Cover and may burn the asset outright.
//                 Effects reuse ShadowTick.ApplySabotage, the §4
//                 corruption-delay cap, and CouncilEcho.EmitDeed — no
//                 parallel machinery.
// Layer:          System
// Collaborators:  ShadowTick.cs (ApplySabotage / warfront lookup),
//                 CorruptionSpread.cs (§4 delay cap), CouncilEcho.cs
//                 (EmitDeed), CouncilState (informants / Exposure)
// See:            espionage_veiled_concord_spec_v1.md §2c, §4
// ============================================================

public static class ShadowOps
{
    /// <summary>A Saboteur wrecks a siege pressing its kingdom, or stalls the
    /// kingdom's next corruption tick (§4-capped). Costs Cover; may burn the
    /// asset. Requires the Saboteur role and Access >= the strike minimum.</summary>
    public static ShadowMarketResult SaboteurStrike(CycleState cycle, string informantId,
                                                    string variant)
    {
        var council = cycle?.Council;
        if (council == null)
        {
            return ShadowMarketResult.Fail("No council state.");
        }
        if (variant != ShadowVocab.SabotageSiege && variant != ShadowVocab.SabotageCorruption)
        {
            return ShadowMarketResult.Fail("Unknown sabotage variant.");
        }
        var inf = FindInformant(council, informantId);
        if (inf == null || inf.Role != ShadowVocab.RoleSaboteur)
        {
            return ShadowMarketResult.Fail("No such saboteur.");
        }
        if (inf.Access < ShadowVocab.SaboteurStrikeMinAccess)
        {
            return ShadowMarketResult.Fail(
                $"The saboteur is not embedded enough yet (access {inf.Access}/" +
                $"{ShadowVocab.SaboteurStrikeMinAccess}).");
        }
        if (inf.Cover < ShadowVocab.SaboteurStrikeCoverCost)
        {
            return ShadowMarketResult.Fail(
                $"Too little cover left to risk a strike (cover {inf.Cover}).");
        }

        inf.Cover -= ShadowVocab.SaboteurStrikeCoverCost;

        var reports = new List<HeraldReport>();
        int lun = cycle.Calendar.CurrentLunation;
        ShadowTick.ApplySabotage(cycle, inf.KingdomId, variant,
            ShadowVocab.SaboteurSiegeStrike, lun, reports, "your saboteur");

        string burnTail = BurnIfSpent(council, inf, reports, lun, cycle);
        FlushReports(council, reports);
        SaveManager.MarkDirty();

        string msg = reports.Count > 0 ? reports[0].Text : "The strike found no target.";
        return ShadowMarketResult.Pass(msg + burnTail);
    }

    /// <summary>A deep Cutout (Access 3) fabricates a deed-echo toward a court —
    /// propaganda in the guild's favor, or a smear. Reuses the echo pipeline;
    /// a trace roll (rising with Marked) can expose the forgery as a court
    /// Exposure spike. Costs Cover; may burn the asset.</summary>
    public static ShadowMarketResult ForgeEcho(CycleState cycle, string informantId,
                                               string targetKingdomId, bool positive)
    {
        var council = cycle?.Council;
        if (council == null)
        {
            return ShadowMarketResult.Fail("No council state.");
        }
        var inf = FindInformant(council, informantId);
        if (inf == null || inf.Role != ShadowVocab.RoleCutout)
        {
            return ShadowMarketResult.Fail("No such cutout.");
        }
        if (inf.Access < ShadowVocab.ForgeEchoMinAccess)
        {
            return ShadowMarketResult.Fail(
                $"The cutout is not deep enough to forge cleanly (access {inf.Access}/" +
                $"{ShadowVocab.ForgeEchoMinAccess}).");
        }
        if (!council.Courts.ContainsKey(targetKingdomId))
        {
            return ShadowMarketResult.Fail("No court there to seed a rumor into.");
        }
        if (inf.Cover < ShadowVocab.ForgeEchoCoverCost)
        {
            return ShadowMarketResult.Fail($"Too little cover to risk it (cover {inf.Cover}).");
        }

        inf.Cover -= ShadowVocab.ForgeEchoCoverCost;

        // A fabricated deed rides the real echo pipeline: a cleansing (positive)
        // or a patrol-slaying (negative), minor magnitude, landing next tick.
        string deed = positive ? CouncilEcho.CorruptionCleansed : CouncilEcho.PatrolSlain;
        CouncilEcho.EmitDeed(cycle, targetKingdomId, deed, positive, isMajor: false);

        int lun = cycle.Calendar.CurrentLunation;
        var reports = new List<HeraldReport>
        {
            new()
            {
                Lunation = lun,
                KingdomId = targetKingdomId,
                Text = $"Shadow: your cutout seeds a false tale into " +
                       $"{CouncilTick.CourtDisplayName(cycle, targetKingdomId)}.",
            },
        };

        // Trace: a forgery uncovered detonates as court Exposure (Scandal risk).
        int traceChance = ShadowVocab.ForgeEchoTraceBasePercent
                          + ShadowVocab.SellTracePerMarked * council.Marked;
        if ((int)(GD.Randi() % 100) < traceChance &&
            council.Courts.TryGetValue(targetKingdomId, out var court))
        {
            court.Exposure = Mathf.Clamp(court.Exposure + ShadowVocab.ForgeEchoExposureSpike, 0, 10);
            reports.Add(new HeraldReport
            {
                Lunation = lun,
                KingdomId = targetKingdomId,
                Text = $"Shadow: the forgery was smelled out — {CouncilTick.CourtDisplayName(cycle, targetKingdomId)} " +
                       $"grows suspicious (Exposure +{ShadowVocab.ForgeEchoExposureSpike}).",
            });
        }

        string burnTail = BurnIfSpent(council, inf, reports, lun, cycle);
        FlushReports(council, reports);
        SaveManager.MarkDirty();
        return ShadowMarketResult.Pass(reports[0].Text + burnTail);
    }

    /// <summary>Pull an informant off the board before it burns, banking its
    /// Access as cross-cycle renown so a network re-placed in that kingdom next
    /// cycle starts with more Cover (§2e / §6). The disciplined tempo trade —
    /// take the asset off the table before the enemy does.</summary>
    public static ShadowMarketResult Exfiltrate(CycleState cycle, string informantId)
    {
        var council = cycle?.Council;
        if (council == null)
        {
            return ShadowMarketResult.Fail("No council state.");
        }
        var inf = FindInformant(council, informantId);
        if (inf == null)
        {
            return ShadowMarketResult.Fail("No such informant.");
        }

        ShadowTick.BankRenown(SaveManager.ActiveSave, inf.KingdomId, inf.Access);
        council.Informants.Remove(inf);
        SaveManager.MarkDirty();
        return ShadowMarketResult.Pass(
            $"Pulled your {(string.IsNullOrEmpty(inf.Role) ? "informant" : inf.Role.ToLower())} " +
            $"out of {CouncilTick.CourtDisplayName(cycle, inf.KingdomId)} clean — the ground learned " +
            $"is banked for next time (renown {inf.Access}).");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static InformantState FindInformant(CouncilState council, string informantId)
    {
        foreach (var inf in council.Informants)
        {
            if (inf.Id == informantId)
            {
                return inf;
            }
        }
        return null;
    }

    /// <summary>If a Cover cost dropped the asset to 0, burn it (remove) and add
    /// a report line. Returns a short tail for the action message.</summary>
    private static string BurnIfSpent(CouncilState council, InformantState inf,
        List<HeraldReport> reports, int lun, CycleState cycle)
    {
        if (inf.Cover > ShadowVocab.CoverMin)
        {
            return "";
        }
        council.Informants.Remove(inf);
        reports.Add(new HeraldReport
        {
            Lunation = lun,
            KingdomId = inf.KingdomId,
            Text = $"Shadow: the work spent the asset's last cover — it is burned and gone.",
        });
        return " (the asset burned out doing it)";
    }

    private static void FlushReports(CouncilState council, List<HeraldReport> reports)
    {
        foreach (var r in reports)
        {
            council.Reports.Add(r);
            GD.Print($"[Herald] L{r.Lunation} {r.Text}");
        }
    }
}
