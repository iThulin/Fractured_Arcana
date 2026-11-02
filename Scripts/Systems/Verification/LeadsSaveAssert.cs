using Godot;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

// ============================================================
// LeadsSaveAssert.cs
//
// Purpose:        Save round-trip assertions for the knowledge ladder's
//                 data (docs/knowledge_ladder_spec_v1.md section 3): the
//                 Lead struct on CycleState.Leads and WorldPoi.Known. The
//                 spec's three cases: a Lead with every field set; a place
//                 both rumoured and sighted (the lead must be gone after
//                 load); Known true on a sighted place. Plus the additive
//                 defaults on legacy JSON. Same serializer and contract as
//                 CharterSaveAssert.
// Layer:          Verification
// Collaborators:  Lead.cs, LeadService.cs, CycleState.cs, WorldData.cs,
//                 SaveManager.cs (JsonOptions), CampusGuildPanel.cs
//                 (the "Assert Round-Trips" debug button)
// ============================================================

public static class LeadsSaveAssert
{
    /// <summary>Run every knowledge ladder round-trip assertion. Prints a PASS/FAIL
    /// report and PushErrors on any failure. Returns true only if all passed.</summary>
    public static bool AssertAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== KNOWLEDGE LADDER SAVE ROUND-TRIP ASSERTIONS ===");

        bool ok = AssertLead(sb);
        ok &= AssertSightedLeadGone(sb);
        ok &= AssertKnown(sb);
        ok &= AssertLegacyDefaults(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Leads and Known round-trip clean."
            : "RESULT: FAILURES ABOVE. A field is being dropped or renamed.");
        GD.Print(sb.ToString());

        if (!ok)
        {
            GD.PushError("[LeadsSaveAssert] Round-trip assertion FAILED. See Output panel.");
        }
        return ok;
    }

    /// <summary>A Lead with every field set to a distinctive value survives the trip.</summary>
    private static bool AssertLead(StringBuilder sb)
    {
        var src = new CycleState
        {
            Leads = new List<Lead>
            {
                new Lead
                {
                    Id = "city:vaelmoor:41",
                    PoiIndex = 41,
                    CenterX = 63,
                    CenterY = 17,
                    Radius = 4,
                    Line = "Talk in Vaelmoor: carrion birds circling, somewhere south of the river.",
                    Source = "city:vaelmoor",
                    CreatedDay = 37,
                },
            },
        };
        var rt = RoundTrip(src);

        bool ok = rt != null && rt.Leads != null && rt.Leads.Count == 1 && rt.Leads[0] != null;
        if (ok)
        {
            var a = src.Leads[0];
            var b = rt.Leads[0];
            ok &= Check(sb, "Lead.Id", b.Id == a.Id);
            ok &= Check(sb, "Lead.PoiIndex", b.PoiIndex == a.PoiIndex);
            ok &= Check(sb, "Lead.CenterX", b.CenterX == a.CenterX);
            ok &= Check(sb, "Lead.CenterY", b.CenterY == a.CenterY);
            ok &= Check(sb, "Lead.Radius", b.Radius == a.Radius);
            ok &= Check(sb, "Lead.Line", b.Line == a.Line);
            ok &= Check(sb, "Lead.Source", b.Source == a.Source);
            ok &= Check(sb, "Lead.CreatedDay", b.CreatedDay == a.CreatedDay);
        }
        else
        {
            sb.AppendLine("    FAIL: CycleState.Leads did not come back with its one lead.");
        }

        sb.AppendLine(ok ? "  Lead: PASS" : "  Lead: FAIL");
        return ok;
    }

    /// <summary>A place both rumoured and sighted: after the trip and the open-time prune,
    /// the sighted place's lead is gone and the unsighted place's lead stands.</summary>
    private static bool AssertSightedLeadGone(StringBuilder sb)
    {
        var src = new CycleState();
        src.World.Pois.Add(new WorldPoi { X = 10, Y = 12, Kind = PoiKind.Combat, Discovered = false });
        src.World.Pois.Add(new WorldPoi { X = 20, Y = 22, Kind = PoiKind.Combat, Discovered = true });
        src.Leads.Add(new Lead { Id = "city:a:0", PoiIndex = 0, CenterX = 11, CenterY = 12, Radius = 3 });
        src.Leads.Add(new Lead { Id = "city:a:1", PoiIndex = 1, CenterX = 21, CenterY = 22, Radius = 3 });
        src.Leads.Add(new Lead { Id = "city:a:9", PoiIndex = 9, CenterX = 5, CenterY = 5, Radius = 3 });
        var rt = RoundTrip(src);

        bool tripped = rt != null && rt.Leads != null && rt.Leads.Count == 3;
        bool ok = Check(sb, "CycleState.Leads came back with all three leads", tripped);
        if (tripped)
        {
            int gone = LeadService.Prune(rt);
            ok &= Check(sb, "LeadService.Prune removed the sighted and the missing place's leads", gone == 2);
            ok &= Check(sb, "The unsighted place's lead stands",
                rt.Leads.Count == 1 && rt.Leads[0] != null && rt.Leads[0].PoiIndex == 0);
            ok &= Check(sb, "LeadService.Prune is idempotent", LeadService.Prune(rt) == 0);
        }

        sb.AppendLine(ok ? "  Sighted lead gone: PASS" : "  Sighted lead gone: FAIL");
        return ok;
    }

    /// <summary>Known true on a sighted place survives the trip.</summary>
    private static bool AssertKnown(StringBuilder sb)
    {
        var rt = RoundTrip(new WorldPoi { X = 4, Y = 6, Discovered = true, Known = true });
        bool ok = Check(sb, "WorldPoi.Discovered", rt != null && rt.Discovered);
        ok &= Check(sb, "WorldPoi.Known", rt != null && rt.Known);

        sb.AppendLine(ok ? "  Known: PASS" : "  Known: FAIL");
        return ok;
    }

    /// <summary>The additive defaults: a cycle saved before the ladder has no leads (an
    /// empty list, not null), and a place saved before it is not Known.</summary>
    private static bool AssertLegacyDefaults(StringBuilder sb)
    {
        var legacyCycle = JsonSerializer.Deserialize<CycleState>("{\"cycleNumber\":2}", SaveManager.JsonOptions);
        bool ok = Check(sb, "CycleState.Leads empty (not null) on legacy JSON",
            legacyCycle != null && legacyCycle.Leads != null && legacyCycle.Leads.Count == 0);

        var legacyPoi = JsonSerializer.Deserialize<WorldPoi>("{\"x\":3,\"y\":4,\"discovered\":true}", SaveManager.JsonOptions);
        ok &= Check(sb, "WorldPoi.Known default false on legacy JSON",
            legacyPoi != null && legacyPoi.Discovered && !legacyPoi.Known);

        sb.AppendLine(ok ? "  Legacy defaults: PASS" : "  Legacy defaults: FAIL");
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
