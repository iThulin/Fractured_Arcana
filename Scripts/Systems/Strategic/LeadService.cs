using System.Collections.Generic;

// ============================================================
// LeadService.cs
//
// Purpose:        The knowledge ladder's rules for leads
//                 (docs/knowledge_ladder_spec_v1.md). L1 holds the one
//                 rule the save needs: a lead whose place has been
//                 Sighted is gone. The sources that write leads (city
//                 talk, companions' memories, the Register, encounters)
//                 and the Leads card arrive in L4 and L5.
// Layer:          System (strategic)
// Collaborators:  Lead.cs, CycleState.cs (Leads), WorldData.cs
//                 (WorldPoi.Discovered), CycleInitializer.cs (prunes on
//                 every open), LeadsSaveAssert.cs
// ============================================================

/// <summary>Rules over <see cref="CycleState.Leads"/>, the knowledge ladder's Rumored
/// stage. Every existing reveal path writes <c>WorldPoi.Discovered</c> without knowing
/// leads exist (spec section 2: they keep working untouched), so a lead is resolved
/// where leads are read, and on every open, never at the reveal sites.</summary>
public static class LeadService
{
    /// <summary>Drop every lead whose place is Sighted (<c>WorldPoi.Discovered</c>), or
    /// whose place no longer exists in this reading's world. Returns how many went.
    /// Idempotent and cheap: safe on every open and before every read.</summary>
    public static int Prune(CycleState cycle)
    {
        if (cycle?.Leads == null || cycle.Leads.Count == 0)
            return 0;

        var pois = cycle.World?.Pois;
        int before = cycle.Leads.Count;
        cycle.Leads.RemoveAll(lead => !Stands(lead, pois));
        return before - cycle.Leads.Count;
    }

    /// <summary>The leads still standing, pruned first. Never null.</summary>
    public static List<Lead> Active(CycleState cycle)
    {
        if (cycle == null)
            return new List<Lead>();
        Prune(cycle);
        return cycle.Leads ?? new List<Lead>();
    }

    /// <summary>A lead stands while its place exists and is not yet Sighted.</summary>
    private static bool Stands(Lead lead, List<WorldPoi> pois)
    {
        if (lead == null || pois == null)
            return false;
        if (lead.PoiIndex < 0 || lead.PoiIndex >= pois.Count)
            return false;
        var poi = pois[lead.PoiIndex];
        return poi != null && !poi.Discovered;
    }
}
