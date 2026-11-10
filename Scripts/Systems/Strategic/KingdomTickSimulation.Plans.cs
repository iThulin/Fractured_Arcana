using System;
using System.Collections.Generic;

// ============================================================
// KingdomTickSimulation.Plans.cs
//
// Purpose:        Rival archmages, stage R1 (docs/
//                 rival_archmages_v1.md). Each realm holds one plan
//                 per moon, chosen here after the kingdom tick from
//                 what the tick is already doing for it: pushing a
//                 front, bidding to annex, hunting the guild,
//                 holding a front, or working its supply caches.
//                 R1 adds no effect. It names what the realm is
//                 doing so the map can show it. R2 hands the plan
//                 to the realm's patrols in the field.
// Layer:          System
// Collaborators:  KingdomState.cs (Plan), KingdomTickSimulation
//                 (fronts, annexation), CouncilQueries (stance),
//                 SupplyCacheSystem.cs (who works a cache),
//                 StrategicView.cs (calls ChoosePlans at the moon),
//                 StrategicView.ProvinceCard.cs (shows PlanCardLine)
// See:            docs/rival_archmages_v1.md sections 1, 2 and 4
// ============================================================

/// <summary>What a realm is working toward this moon. Saved as a number, so members are only
/// ever appended. Seek and Court are named in the proposal and are not chosen until R3.</summary>
public enum RivalPlanKind
{
    None = 0,
    Expand = 1,
    Fortify = 2,
    Gather = 3,
    Seek = 4,
    Court = 5,
    Hunt = 6,
}

/// <summary>A realm's plan for the moon. Plain data on KingdomState.</summary>
public class RivalPlan
{
    public RivalPlanKind Kind = RivalPlanKind.None;

    /// <summary>The province the plan is aimed at (Province.Id), or empty.</summary>
    public string TargetProvinceId = "";

    /// <summary>The realm the plan is aimed at or against, or empty.</summary>
    public string TargetKingdomId = "";
}

public static partial class KingdomTickSimulation
{
    /// <summary>Chooses every realm's plan for the moon. Call after Tick, so the fronts and
    /// annexation bids it reads are this moon's. A plan that turns to Expand or Hunt is
    /// reported, one line a realm.</summary>
    public static void ChoosePlans(CycleState cycle, Func<string, string> fd = null)
    {
        if (cycle?.World == null || cycle.Kingdoms == null)
        {
            return;
        }
        cycle.PendingSiegeReports ??= new List<string>();
        string convergence = ConvergenceKingdomId(cycle.World);
        bool fronts = FrontsWalk(cycle);
        foreach (var kvp in cycle.Kingdoms)
        {
            string kid = kvp.Key;
            var k = kvp.Value;
            if (k == null || kid == convergence)
            {
                continue;
            }
            if (fronts && ProvinceControl.KingdomFallen(cycle, kid))
            {
                k.Plan = new RivalPlan();
                continue;
            }
            var next = ChoosePlan(cycle, kid, fronts);
            var was = k.Plan;
            bool changed = was == null || was.Kind != next.Kind
                           || was.TargetProvinceId != next.TargetProvinceId;
            k.Plan = next;
            if (changed && (next.Kind == RivalPlanKind.Expand || next.Kind == RivalPlanKind.Hunt))
            {
                Report(cycle, $"{RealmOf(k, kid)}'s plan this moon. {PlanText(cycle, kid, next)}");
            }
        }
    }

    /// <summary>The first that is true: it pushes a front, it leads a bid to annex, its court
    /// is Hostile to the guild, it holds a front, it works a supply cache.</summary>
    private static RivalPlan ChoosePlan(CycleState cycle, string kid, bool fronts)
    {
        var plan = new RivalPlan();
        if (cycle.Warfronts != null)
        {
            foreach (var wf in cycle.Warfronts)
            {
                if (wf != null && wf.AggressorKingdomId == kid)
                {
                    plan.Kind = RivalPlanKind.Expand;
                    plan.TargetProvinceId = wf.ProvinceId ?? "";
                    plan.TargetKingdomId = wf.DefenderKingdomId ?? "";
                    return plan;
                }
            }
        }

        if (fronts)
        {
            var provinces = cycle.World.Provinces;
            int bestBar = -1;
            for (int i = 0; i < provinces.Count; i++)
            {
                var province = provinces[i];
                if (province == null || ProvinceControl.RulerOf(cycle, province) != ProvinceControl.Neutral)
                {
                    continue;
                }
                foreach (var bid in AnnexBids(cycle, i))
                {
                    if (bid.KingdomId == kid && bid.Gain > 0 && bid.Bar > bestBar)
                    {
                        bestBar = bid.Bar;
                        plan.Kind = RivalPlanKind.Expand;
                        plan.TargetProvinceId = province.Id;
                        plan.TargetKingdomId = "";
                    }
                }
            }
            if (plan.Kind == RivalPlanKind.Expand)
            {
                return plan;
            }
        }

        if (cycle.Council?.Courts != null && cycle.Council.Courts.ContainsKey(kid)
            && CouncilQueries.StanceFor(cycle, kid) == KingdomStance.Hostile)
        {
            plan.Kind = RivalPlanKind.Hunt;
            return plan;
        }

        if (cycle.Warfronts != null)
        {
            foreach (var wf in cycle.Warfronts)
            {
                if (wf != null && wf.DefenderKingdomId == kid)
                {
                    plan.Kind = RivalPlanKind.Fortify;
                    plan.TargetProvinceId = wf.ProvinceId ?? "";
                    plan.TargetKingdomId = wf.AggressorKingdomId ?? "";
                    return plan;
                }
            }
        }

        if (cycle.World.Pois != null)
        {
            foreach (var p in cycle.World.Pois)
            {
                if (p != null && p.Kind == PoiKind.SupplyCache && SupplyCacheSystem.ControllerOf(p) == kid)
                {
                    plan.Kind = RivalPlanKind.Gather;
                    return plan;
                }
            }
        }
        return plan;
    }

    /// <summary>The plan in plain words: the verb, then what it means on the map.</summary>
    public static string PlanText(CycleState cycle, string kid, RivalPlan plan)
    {
        if (plan == null)
        {
            return "";
        }
        string where = "";
        if (!string.IsNullOrEmpty(plan.TargetProvinceId) && cycle?.World != null)
        {
            where = ProvinceControl.Name(ProvinceControl.ProvinceById(cycle.World, plan.TargetProvinceId));
        }
        string whom = string.IsNullOrEmpty(plan.TargetKingdomId)
            ? ""
            : ProvinceControl.RealmName(cycle, plan.TargetKingdomId);
        switch (plan.Kind)
        {
            case RivalPlanKind.Expand:
                if (whom.Length > 0)
                {
                    return where.Length > 0
                        ? $"Expand: it is pushing a front into {where}, held by {whom}."
                        : $"Expand: it is pushing a front against {whom}.";
                }
                return where.Length > 0
                    ? $"Expand: it is annexing {where}."
                    : "Expand: it is taking land.";
            case RivalPlanKind.Hunt:
                return "Hunt: its patrols are looking for the guild.";
            case RivalPlanKind.Fortify:
                return where.Length > 0
                    ? $"Fortify: it is holding {where}" + (whom.Length > 0 ? $" against {whom}." : ".")
                    : "Fortify: it is holding its borders.";
            case RivalPlanKind.Gather:
                return "Gather: it is working its supply caches.";
            case RivalPlanKind.Seek:
                return "Seek: it is after a shard.";
            case RivalPlanKind.Court:
                return "Court: it is winning a neighbour's favour.";
            default:
                return "";
        }
    }

    /// <summary>The province card's line for the realm that rules a province: "Ruler's plan
    /// this moon. Expand: ...". Empty for land no realm rules, land the guild rules, and a
    /// realm with no plan.</summary>
    public static string PlanCardLine(CycleState cycle, Province province)
    {
        if (cycle?.Kingdoms == null || province == null)
        {
            return "";
        }
        string ruler = ProvinceControl.RulerOf(cycle, province);
        if (!ProvinceControl.IsKingdom(ruler)
            || !cycle.Kingdoms.TryGetValue(ruler, out var k) || k?.Plan == null)
        {
            return "";
        }
        string text = PlanText(cycle, ruler, k.Plan);
        return text.Length > 0 ? "Ruler's plan this moon. " + text : "";
    }
}
