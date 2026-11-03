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
//                 CharterSaveAssert. Since L3 and L4a (spec section 13):
//                 scripted leads and their rings, the note a true lead
//                 leaves, and which places stand as beacons in the field.
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
        ok &= AssertScripted(sb);
        ok &= AssertHeldTrue(sb);
        ok &= AssertFieldBeacon(sb);

        sb.AppendLine(ok
            ? "RESULT: ALL PASSED. Leads and Known round-trip clean, and the scripted leads, their notes and the field beacons check out."
            : "RESULT: FAILURES ABOVE. A field is being dropped or renamed, or a lead rule broke.");
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

    /// <summary>L4a: a scripted lead. Its id is the source plus the place's index, its
    /// ring holds the place without centering on it, hearing it again adds nothing, the
    /// same seed draws the same ring, a ring at the world's edge stays in bounds, and a
    /// place already Sighted or missing gets no lead.</summary>
    private static bool AssertScripted(StringBuilder sb)
    {
        var cycle = ScratchCycle(4242);
        var world = cycle.World;
        int target = AddPlace(world, 15, 15, PoiKind.Combat, false);
        int seen = AddPlace(world, 5, 5, PoiKind.Combat, true);
        int corner = AddPlace(world, 0, 0, PoiKind.Rest, false);

        var lead = LeadService.AddScripted(cycle, target, "register", "The Register: a test.", LeadService.CityRadius);
        bool ok = Check(sb, "LeadService.AddScripted heard the lead", lead != null);
        if (lead != null)
        {
            int off = world.HexDistance(lead.CenterX, lead.CenterY, 15, 15);
            ok &= Check(sb, "A scripted lead's id is its source and the place's index", lead.Id == $"register:{target}");
            ok &= Check(sb, "A scripted lead keeps its source, line and radius",
                lead.Source == "register" && lead.Line == "The Register: a test." && lead.Radius == LeadService.CityRadius);
            ok &= Check(sb, "The ring holds the place without centering on it", off >= 1 && off <= lead.Radius - 1);
            ok &= Check(sb, "The lead is heard on the cycle's day", lead.CreatedDay == cycle.Calendar.AbsoluteDay);
            ok &= Check(sb, "Hearing it again adds nothing",
                LeadService.AddScripted(cycle, target, "register", "again", LeadService.CityRadius) == lead
                && cycle.Leads.Count == 1);

            var twin = ScratchCycle(4242);
            int twinTarget = AddPlace(twin.World, 15, 15, PoiKind.Combat, false);
            var twinLead = LeadService.AddScripted(twin, twinTarget, "register", "", LeadService.CityRadius);
            ok &= Check(sb, "The same seed draws the same ring",
                twinLead != null && twinLead.CenterX == lead.CenterX && twinLead.CenterY == lead.CenterY);
        }

        var edge = LeadService.AddScripted(cycle, corner, LeadService.DebugSource, "", LeadService.CompanionRadius);
        int edgeOff = edge == null ? -1 : world.HexDistance(edge.CenterX, edge.CenterY, 0, 0);
        ok &= Check(sb, "A ring at the world's edge centers in bounds and holds the place",
            edge != null && world.InBounds(edge.CenterX, edge.CenterY)
            && edgeOff >= 1 && edgeOff <= LeadService.CompanionRadius - 1);
        ok &= Check(sb, "No lead on a place already Sighted",
            LeadService.AddScripted(cycle, seen, "register", "", LeadService.CityRadius) == null);
        ok &= Check(sb, "No lead on a place that does not exist",
            LeadService.AddScripted(cycle, 99, "register", "", LeadService.CityRadius) == null);

        sb.AppendLine(ok ? "  Scripted lead: PASS" : "  Scripted lead: FAIL");
        return ok;
    }

    /// <summary>L4a: a lead whose place is Sighted goes with one "held true" note at the
    /// desk and one line of frontier news, a lead whose place is gone goes silently, and
    /// a second prune posts nothing. LeadService.Stands is pure.</summary>
    private static bool AssertHeldTrue(StringBuilder sb)
    {
        var cycle = ScratchCycle(7);
        var world = cycle.World;
        int place = AddPlace(world, 12, 12, PoiKind.Combat, false);
        var lead = LeadService.AddScripted(cycle, place, "register", "The Register: a test.", LeadService.CityRadius);
        cycle.Leads.Add(new Lead { Id = "city:a:9", PoiIndex = 9, CenterX = 3, CenterY = 3, Radius = 3 });
        int notes = cycle.ScryInbox?.Count ?? 0;
        int news = cycle.PendingSiegeReports?.Count ?? 0;

        bool ok = Check(sb, "LeadService.Stands while the place is unseen",
            lead != null && LeadService.Stands(lead, world.Pois));
        world.Pois[place].Discovered = true;
        ok &= Check(sb, "LeadService.Stands is false once the place is Sighted, and removes nothing",
            !LeadService.Stands(lead, world.Pois) && cycle.Leads.Count == 2);
        ok &= Check(sb, "The prune takes the sighted place's lead and the missing place's",
            LeadService.Prune(cycle) == 2 && cycle.Leads.Count == 0);
        ok &= Check(sb, "One desk note, titled as held true",
            (cycle.ScryInbox?.Count ?? 0) == notes + 1 && cycle.ScryInbox[notes].Title == LeadService.HeldTitle);
        ok &= Check(sb, "One line of frontier news", (cycle.PendingSiegeReports?.Count ?? 0) == news + 1);
        ok &= Check(sb, "A second prune posts nothing",
            LeadService.Prune(cycle) == 0 && (cycle.ScryInbox?.Count ?? 0) == notes + 1
            && (cycle.PendingSiegeReports?.Count ?? 0) == news + 1);

        sb.AppendLine(ok ? "  Held true: PASS" : "  Held true: FAIL");
        return ok;
    }

    /// <summary>L3: which places stand in a sortie window as beacons. Sighted, not
    /// resolved, in the wild, of a kind a party goes to in person; Known places count.
    /// An unseen place, a resolved one, a cache and a place on a settlement's tile do
    /// not.</summary>
    private static bool AssertFieldBeacon(StringBuilder sb)
    {
        var world = ScratchCycle(1).World;
        int wild = AddPlace(world, 4, 4, PoiKind.Combat, true);
        int known = AddPlace(world, 6, 4, PoiKind.Narrative, true);
        world.Pois[known].Known = true;
        int unseen = AddPlace(world, 8, 4, PoiKind.Rest, false);
        int done = AddPlace(world, 10, 4, PoiKind.Combat, true);
        world.Pois[done].Consumed = true;
        int cache = AddPlace(world, 12, 4, PoiKind.SupplyCache, true);
        int town = AddPlace(world, 14, 4, PoiKind.Negotiation, true);
        if (world.TryIndex(14, 4, out int townTile))
        {
            world.Tiles[townTile].SettlementIndex = 0;
        }

        bool ok = Check(sb, "A sighted place in the wild is a beacon", CampSight.FieldBeacon(world, world.Pois[wild]));
        ok &= Check(sb, "A Known place is still a beacon", CampSight.FieldBeacon(world, world.Pois[known]));
        ok &= Check(sb, "An unseen place is not a beacon", !CampSight.FieldBeacon(world, world.Pois[unseen]));
        ok &= Check(sb, "A resolved place is not a beacon", !CampSight.FieldBeacon(world, world.Pois[done]));
        ok &= Check(sb, "A cache is not a beacon", !CampSight.FieldBeacon(world, world.Pois[cache]));
        ok &= Check(sb, "A place on a settlement's tile is not a beacon (staging is contact by foot)",
            !CampSight.FieldBeacon(world, world.Pois[town]));

        sb.AppendLine(ok ? "  Field beacons: PASS" : "  Field beacons: FAIL");
        return ok;
    }

    /// <summary>A small all-grass world for the lead and beacon checks. Every tile is
    /// empty, unsettled and outside every shard zone: the struct defaults alias index
    /// 0, so each index is set to -1, as the generator does.</summary>
    private static CycleState ScratchCycle(int seed)
    {
        var cycle = new CycleState { WorldSeed = seed };
        var world = cycle.World;
        world.Width = 30;
        world.Height = 30;
        world.Tiles = new WorldTile[world.Width * world.Height];
        for (int i = 0; i < world.Tiles.Length; i++)
        {
            world.Tiles[i].Terrain = OverworldHex.TerrainType.Grassland;
            world.Tiles[i].PoiIndex = -1;
            world.Tiles[i].SettlementIndex = -1;
            world.Tiles[i].ShardZoneIndex = -1;
        }
        return cycle;
    }

    /// <summary>Add a place to a scratch world and point its tile at it. Returns the
    /// place's index.</summary>
    private static int AddPlace(WorldData world, int x, int y, PoiKind kind, bool discovered)
    {
        world.Pois.Add(new WorldPoi { X = x, Y = y, Kind = kind, Discovered = discovered });
        int index = world.Pois.Count - 1;
        if (world.TryIndex(x, y, out int tile))
        {
            world.Tiles[tile].PoiIndex = index;
        }
        return index;
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
