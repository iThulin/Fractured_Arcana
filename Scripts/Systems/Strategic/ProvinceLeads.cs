using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ProvinceLeads.cs
//
// Purpose:        Rumors on provinces (docs/provinces_v1_13_orders_and_
//                 leads.md, slice PV8a; X5, PR96 to PR98). A rumor still
//                 draws its ring, and now it points to a province: its
//                 place's, when it is true, or the province its ring's
//                 center lies in, when it is false (the province it
//                 claims). The ring's label names that province, the
//                 province card counts the rumors that point into it, and
//                 the castle's survey runs them down: standing in a
//                 province, the castle sights its places (PV5), so a true
//                 rumor there holds true, and a false one comes to
//                 nothing (the field's search, D217, at its center). Read
//                 from the leads as they stand: no new save field.
// Layer:          System (strategic; static, no nodes)
// Collaborators:  LeadService.cs (Stands, SearchAt, RecordLine; LabelFor
//                 calls WhereLine), Lead.cs, CastleReach.cs (the survey
//                 calls SettleProvince), StrategicView.Leads.cs (the
//                 card's line), ProvinceSaveAssert.Leads.cs
// See:            docs/provinces_v1_13_orders_and_leads.md section 3
// ============================================================

/// <summary>Rumors read on provinces (PV8a). Stateless.</summary>
public static class ProvinceLeads
{
    /// <summary>How many rumors the province card quotes.</summary>
    public const int CardQuotes = 2;

    /// <summary>The province a rumor points to: its place's for a true rumor, its ring
    /// center's for a false one. -1 where the world has no provinces, or the ground none.</summary>
    public static int ProvinceOf(CycleState cycle, Lead lead)
    {
        var world = cycle?.World;
        if (world == null || lead == null || !world.HasProvinces())
            return -1;
        if (!lead.IsFalse && world.Pois != null && lead.PoiIndex >= 0 && lead.PoiIndex < world.Pois.Count
            && world.Pois[lead.PoiIndex] != null)
        {
            var poi = world.Pois[lead.PoiIndex];
            return world.InBounds(poi.X, poi.Y) ? world.ProvinceIndexAt(poi.X, poi.Y) : -1;
        }
        return world.InBounds(lead.CenterX, lead.CenterY) ? world.ProvinceIndexAt(lead.CenterX, lead.CenterY) : -1;
    }

    /// <summary>"In Northeast Fields.": the last line of a rumor's ring label. "" with no province.</summary>
    public static string WhereLine(CycleState cycle, Lead lead)
    {
        int province = ProvinceOf(cycle, lead);
        if (province < 0 || province >= cycle.World.Provinces.Count)
            return "";
        return $"In {ProvinceControl.Name(cycle.World.Provinces[province])}.";
    }

    /// <summary>The rumors standing that point into the province, in the order heard. Pure:
    /// reads LeadService.Stands, as the atlas does, and never prunes.</summary>
    public static List<Lead> StandingIn(CycleState cycle, int province)
    {
        var leads = new List<Lead>();
        if (cycle?.Leads == null || province < 0)
            return leads;
        var pois = cycle.World?.Pois;
        foreach (var lead in cycle.Leads)
        {
            if (lead != null && LeadService.Stands(lead, pois) && ProvinceOf(cycle, lead) == province)
                leads.Add(lead);
        }
        return leads;
    }

    /// <summary>The castle's survey runs down the rumors that point into its province: each
    /// false one is searched at its center and comes to nothing, with the field search's own
    /// notes and its kingdom's record. A true one's place lies in the province, so the survey
    /// has sighted it already, and it holds true at the next prune. Returns how many false
    /// rumors came to nothing. Changes only the cycle: the caller saves.</summary>
    public static int SettleProvince(CycleState cycle, int province)
    {
        if (cycle?.Leads == null || cycle.Leads.Count == 0 || province < 0)
            return 0;
        var centers = new List<(int X, int Y)>();
        int before = 0;
        foreach (var lead in cycle.Leads)
        {
            if (lead == null || !lead.IsFalse || ProvinceOf(cycle, lead) != province)
                continue;
            before++;
            if (!centers.Contains((lead.CenterX, lead.CenterY)))
                centers.Add((lead.CenterX, lead.CenterY));
        }
        if (before == 0)
            return 0;
        foreach (var (x, y) in centers)
            LeadService.SearchAt(cycle, x, y);
        int after = 0;
        foreach (var lead in cycle.Leads)
        {
            if (lead != null && lead.IsFalse && ProvinceOf(cycle, lead) == province)
                after++;
        }
        int settled = Math.Max(0, before - after);
        if (settled > 0)
            GD.Print($"[Leads] The castle's survey ran down {settled} rumor(s) in province {province}.");
        return settled;
    }

    /// <summary>The province card's line: how many rumors point here, the first of them in their
    /// own words, and how to run them down. "" when none does.</summary>
    public static string CardLine(CycleState cycle, int province)
    {
        var leads = StandingIn(cycle, province);
        if (leads.Count == 0)
            return "";
        var quoted = new List<string>();
        for (int i = 0; i < leads.Count && i < CardQuotes; i++)
        {
            string record = LeadService.RecordLine(cycle, leads[i]);
            string words = string.IsNullOrEmpty(leads[i].Line) ? "a rumor" : leads[i].Line;
            quoted.Add(record.Length > 0 ? $"{words} ({record})" : words);
        }
        string more = leads.Count > CardQuotes ? $" And {leads.Count - CardQuotes} more." : "";
        return $"Rumors point here: {leads.Count}. {string.Join(" ", quoted)}{more} "
               + "Stand the castle here to run them down.";
    }
}
