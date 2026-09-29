using Godot;

// ============================================================
// HarvestSites.cs: sites that regrow (2026-09-28)
//
// Purpose:        Ruling 7 of 2026-09-21: an exhausted site is gone for the
//                 cycle EXCEPT a harvestable one, which regrows and can be
//                 worked again. The three WorldPoi fields for it (Harvestable,
//                 RegenLunations, RegenReadyLunation) have round-tripped in
//                 the save since increment 1 and nothing read them. This is
//                 what reads them.
//
//                 Which sites regrow: rest sites (springs, groves, hunters'
//                 shelters), which are worked for Essence, a little healing,
//                 splinters and gold. Combat, narrative, negotiation and
//                 outposts stay one-shot: a fight won or a deal struck is an
//                 event, not a resource. Supply caches never deplete at all.
//
//                 The ready lunation is compared against the calendar rather
//                 than counted down, so quitting mid-cycle cannot lose or gain
//                 regrowth time (the field's own doc comment, honoured).
// Layer:          Systems / Strategic
// Collaborators:  WorldPoi (the three fields, Consumed), ExpeditionManager
//                 .ConsumeWorldPoi (OnConsumed), StrategicView (EnsureSeeded
//                 at load, Tick at the new moon)
// ============================================================

public static class HarvestSites
{
    /// <summary>Moons a rest site takes to recover once worked. A quarter of
    /// a twelve-lunation cycle: a site can be worked three or four times in a
    /// cycle by a force that keeps coming back, which is the point of holding
    /// ground near one.</summary>
    public const int RestRegenLunations = 3;

    /// <summary>Mark the kinds that regrow. Idempotent, and never overwrites a
    /// site whose regrowth was set otherwise (a future authored harvestable).</summary>
    public static void EnsureSeeded(CycleState cycle)
    {
        var world = cycle?.World;
        if (world?.Pois == null)
        {
            return;
        }
        int marked = 0;
        foreach (var p in world.Pois)
        {
            if (p == null || p.Harvestable || p.Kind != PoiKind.Rest)
            {
                continue;
            }
            p.Harvestable = true;
            if (p.RegenLunations <= 0)
            {
                p.RegenLunations = RestRegenLunations;
            }
            marked++;
        }
        if (marked > 0)
        {
            SaveManager.MarkDirty();
            GD.Print($"[HarvestSites] {marked} rest site(s) marked to regrow.");
        }
    }

    /// <summary>A site was just worked. If it regrows, set when.</summary>
    public static void OnConsumed(CycleState cycle, WorldPoi poi)
    {
        if (poi == null || !poi.Harvestable)
        {
            return;
        }
        int now = cycle?.Calendar?.CurrentLunation ?? 0;
        poi.RegenReadyLunation = now + Mathf.Max(1, poi.RegenLunations);
    }

    /// <summary>At the new moon: every worked site whose time has come is
    /// workable again. Discovery is untouched; the player knows where it is.
    /// Returns a report line, or null.</summary>
    public static string Tick(CycleState cycle)
    {
        var world = cycle?.World;
        if (world?.Pois == null)
        {
            return null;
        }
        int now = cycle.Calendar?.CurrentLunation ?? 0;
        int recovered = 0;
        foreach (var p in world.Pois)
        {
            if (p == null || !p.Harvestable || !p.Consumed || p.RegenReadyLunation <= 0)
            {
                continue;
            }
            if (now < p.RegenReadyLunation)
            {
                continue;
            }
            p.Consumed = false;
            p.RegenReadyLunation = 0;
            recovered++;
        }
        if (recovered == 0)
        {
            return null;
        }
        SaveManager.MarkDirty();
        return recovered == 1
            ? "A rest site the guild has used recovers and can be worked again."
            : $"{recovered} rest sites the guild has used recover and can be worked again.";
    }
}
