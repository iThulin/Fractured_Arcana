using System.Collections.Generic;
using System.Text;

// ============================================================
// ProvinceSaveAssert.Leads.cs  (partial of ProvinceSaveAssert)
//
// Purpose:        The asserts of slice PV8a, rumors on provinces (docs/
//                 provinces_v1_13_orders_and_leads.md section 3.4), on the
//                 scratch world: a true rumor points to its place's
//                 province and a false one to its ring center's; the
//                 ring's label names it; the card counts the rumors in a
//                 province; the castle in a province runs its false
//                 rumors down and its survey sights a true one's place,
//                 which then holds true.
// Layer:          Verification
// Collaborators:  ProvinceSaveAssert.cs (AssertAll calls
//                 AssertProvinceLeads; Check), ProvinceSaveAssert.Bastion.cs
//                 (BastionCycle), ProvinceSaveAssert.Reach.cs
//                 (AddUnseenPlace), ProvinceLeads.cs, LeadService.cs,
//                 CastleReach.cs
// See:            docs/provinces_v1_13_orders_and_leads.md section 3.4
// ============================================================

public static partial class ProvinceSaveAssert
{
    /// <summary>Every PV8a assert, in one line of the report.</summary>
    private static bool AssertProvinceLeads(StringBuilder sb)
    {
        var cycle = BastionCycle("Adept");
        var world = cycle.World;
        int capital = world.ProvinceIndexAt(6, 15), fields = world.ProvinceIndexAt(18, 5), south = world.ProvinceIndexAt(20, 24);
        if (capital < 0 || fields < 0 || south < 0 || world.ProvinceIndexAt(18, 8) != fields)
        {
            sb.AppendLine("  Rumors on provinces: FAIL (the scratch world's provinces are not where they were)");
            return false;
        }
        cycle.Leads ??= new List<Lead>();
        cycle.Leads.Clear();
        int place = AddUnseenPlace(world, 18, 8);
        var truth = LeadService.AddScripted(cycle, place, LeadService.DebugSource, "Word (debug): a test.", LeadService.CityRadius);
        var lie = new Lead
        {
            Id = "talk:kingdom_0:at:20,24", PoiIndex = -1, CenterX = 20, CenterY = 24, Radius = LeadService.CityRadius,
            Line = "Talk in the west: a refuge, somewhere to the south.", Source = "talk:" + West, IsFalse = true,
        };
        cycle.Leads.Add(lie);

        bool ok = Check(sb, "a true rumor points to its place's province, a false one to its ring center's",
            truth != null && ProvinceLeads.ProvinceOf(cycle, truth) == fields && ProvinceLeads.ProvinceOf(cycle, lie) == south);
        string fieldsName = ProvinceControl.Name(world.Provinces[fields]);
        ok &= Check(sb, $"the ring's label names the province ({LeadService.LabelFor(cycle, truth)})",
            truth != null && LeadService.LabelFor(cycle, truth).EndsWith($"In {fieldsName}."));
        ok &= Check(sb, "the card counts the rumors that point into a province, and none elsewhere",
            ProvinceLeads.StandingIn(cycle, fields).Count == 1 && ProvinceLeads.StandingIn(cycle, south).Count == 1
            && ProvinceLeads.StandingIn(cycle, capital).Count == 0
            && ProvinceLeads.CardLine(cycle, south).StartsWith("Rumors point here: 1.")
            && ProvinceLeads.CardLine(cycle, capital) == "");

        int settled = ProvinceLeads.SettleProvince(cycle, fields);
        ok &= Check(sb, "in Northeast Fields there is no false rumor to run down: both rumors stand",
            settled == 0 && cycle.Leads.Contains(truth) && cycle.Leads.Contains(lie));
        settled = ProvinceLeads.SettleProvince(cycle, south);
        ok &= Check(sb, $"the castle in Southeast Fields runs the false rumor down, with its note ({settled})",
            settled == 1 && !cycle.Leads.Contains(lie) && cycle.Leads.Contains(truth)
            && cycle.PendingSiegeReports != null
            && cycle.PendingSiegeReports.Exists(l => l.StartsWith("A lead came to nothing")));

        cycle.CastleX = 18;
        cycle.CastleY = 5;
        string survey = CastleReach.Survey(cycle);
        int pruned = LeadService.Prune(cycle);
        ok &= Check(sb, $"the castle's survey in Northeast Fields sights the place, and the true rumor holds true ({survey})",
            world.Pois[place].Discovered && pruned == 1 && truth != null && !cycle.Leads.Contains(truth)
            && cycle.PendingSiegeReports.Exists(l => l.StartsWith("A lead held true")));

        var later = new Lead
        {
            Id = "talk:kingdom_0:at:18,8", PoiIndex = -1, CenterX = 18, CenterY = 8, Radius = LeadService.CityRadius,
            Line = "Talk in the west: a hostile encampment, somewhere to the north.", Source = "talk:" + West, IsFalse = true,
        };
        cycle.Leads.Add(later);
        CastleReach.Survey(cycle);
        ok &= Check(sb, "a later survey there runs down a false rumor heard since", !cycle.Leads.Contains(later));

        sb.AppendLine(ok ? "  Rumors on provinces: PASS" : "  Rumors on provinces: FAIL");
        return ok;
    }
}
