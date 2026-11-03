using Godot;
using System.Collections.Generic;

// ============================================================
// LeadService.cs
//
// Purpose:        The knowledge ladder's rules for leads
//                 (docs/knowledge_ladder_spec_v1.md). A lead whose
//                 place has been Sighted is gone, and since L4a it
//                 goes with a note that it held true. Scripted leads
//                 (the arc's, and the debug lever's) are heard here,
//                 with a ring that holds the place without centering
//                 on it. The other sources (city talk, companions'
//                 memories, encounters), false rumors (F1) and the
//                 Leads card arrive in L4b and L5.
// Layer:          System (strategic)
// Collaborators:  Lead.cs, CycleState.cs (Leads, WorldSeed),
//                 WorldData.cs (WorldPoi.Discovered, Disc),
//                 CycleInitializer.cs (prunes on every open),
//                 CampSight.cs (prunes after a camp; names the
//                 place), ScryInbox (the desk),
//                 CycleState.PendingSiegeReports (the map's frontier
//                 news), WorldAtlas3D.cs (draws the rings through
//                 Stands), StrategicDebug.cs (Rumor the Nearest
//                 Place), LeadsSaveAssert.cs
// ============================================================

/// <summary>Rules over <see cref="CycleState.Leads"/>, the knowledge ladder's Rumored
/// stage. Every existing reveal path writes <c>WorldPoi.Discovered</c> without knowing
/// leads exist (spec section 2: they keep working untouched), so a lead is resolved
/// where leads are read, and on every open, never at the reveal sites (D130).
/// <see cref="Prune"/> and <see cref="AddScripted"/> change only the cycle: their
/// callers save it.</summary>
public static class LeadService
{
    /// <summary>A city's talk draws its ring this wide, in tiles (spec section 3,
    /// a starting value).</summary>
    public const int CityRadius = 3;

    /// <summary>A companion's memory draws its ring this wide, in tiles (spec
    /// section 3, a starting value).</summary>
    public const int CompanionRadius = 4;

    /// <summary>The debug lever's source (StrategicDebug.RumorNearestPlace).</summary>
    public const string DebugSource = "debug";

    /// <summary>The desk note's title when a lead's place is Sighted (D182;
    /// placeholder copy for the copy pass).</summary>
    public const string HeldTitle = "A lead held true";

    /// <summary>Drop every lead whose place is Sighted (<c>WorldPoi.Discovered</c>), or
    /// whose place no longer exists in this reading's world. A lead that goes because
    /// its place was Sighted held true and says so: one desk note and one line of
    /// frontier news each, in the order the leads were heard (D182). A lead whose
    /// place is gone goes silently. Returns how many went. Idempotent and cheap: safe
    /// on every open and before every read. The caller saves when it returns more
    /// than zero, so a note is never posted twice.</summary>
    public static int Prune(CycleState cycle)
    {
        if (cycle?.Leads == null || cycle.Leads.Count == 0)
        {
            return 0;
        }

        var pois = cycle.World?.Pois;
        var kept = new List<Lead>(cycle.Leads.Count);
        var held = new List<Lead>();
        foreach (var lead in cycle.Leads)
        {
            if (Stands(lead, pois))
            {
                kept.Add(lead);
            }
            else if (HeldTrue(lead, pois))
            {
                held.Add(lead);
            }
        }

        int gone = cycle.Leads.Count - kept.Count;
        if (gone == 0)
        {
            return 0;
        }
        cycle.Leads.Clear();
        cycle.Leads.AddRange(kept);
        foreach (var lead in held)
        {
            AnnounceHeld(cycle, lead, pois[lead.PoiIndex]);
        }
        return gone;
    }

    /// <summary>The leads still standing, pruned first. Never null. A prune here can
    /// post a note, so the save is marked dirty when a lead goes.</summary>
    public static List<Lead> Active(CycleState cycle)
    {
        if (cycle == null)
        {
            return new List<Lead>();
        }
        if (Prune(cycle) > 0)
        {
            SaveManager.MarkDirty();
        }
        return cycle.Leads ?? new List<Lead>();
    }

    /// <summary>A lead stands while its place exists and is not yet Sighted. Pure: the
    /// atlas draws its rings through this and never prunes while it draws (D183).</summary>
    public static bool Stands(Lead lead, List<WorldPoi> pois)
    {
        if (lead == null || pois == null)
        {
            return false;
        }
        if (lead.PoiIndex < 0 || lead.PoiIndex >= pois.Count)
        {
            return false;
        }
        var poi = pois[lead.PoiIndex];
        return poi != null && !poi.Discovered;
    }

    /// <summary>Hear a scripted lead (D180): the arc's (the Register's, a companion's
    /// memory, chapter III's city talk) or the debug lever's. Scripted leads are always
    /// true (spec section 4.6). The id is the source plus the place's index, so hearing
    /// it again returns the lead already standing. The ring is centered by
    /// <see cref="RingCenter"/>. Returns the lead, or null when there is nothing to
    /// rumor: no such place, or a place already Sighted, whose lead would only resolve
    /// at once. Changes only the cycle: the caller saves.</summary>
    public static Lead AddScripted(CycleState cycle, int poiIndex, string source, string line, int radius)
    {
        var world = cycle?.World;
        if (world?.Pois == null || string.IsNullOrEmpty(source)
            || poiIndex < 0 || poiIndex >= world.Pois.Count || world.Pois[poiIndex] == null)
        {
            GD.Print($"[Leads] No place {poiIndex} for a lead from '{source}'.");
            return null;
        }
        if (world.Pois[poiIndex].Discovered)
        {
            GD.Print($"[Leads] Place {poiIndex} is already on the map, so '{source}' adds no lead.");
            return null;
        }

        string id = $"{source}:{poiIndex}";
        cycle.Leads ??= new List<Lead>();
        foreach (var standing in cycle.Leads)
        {
            if (standing != null && standing.Id == id)
            {
                return standing;
            }
        }

        int r = System.Math.Max(1, radius);
        var (cx, cy) = RingCenter(world, cycle.WorldSeed, poiIndex, r);
        var lead = new Lead
        {
            Id = id,
            PoiIndex = poiIndex,
            CenterX = cx,
            CenterY = cy,
            Radius = r,
            Line = line ?? "",
            Source = source,
            CreatedDay = cycle.Calendar?.AbsoluteDay ?? 0,
        };
        cycle.Leads.Add(lead);
        GD.Print($"[Leads] Heard {id}: a ring of {r} around ({cx},{cy}).");
        return lead;
    }

    /// <summary>Where a lead's ring is centered (D181): one of the in-bounds tiles 1 to
    /// radius - 1 from the place, so a ring of that radius always holds the place and
    /// never centers on it (spec section 3). Picked by the reading's world seed and the
    /// place's index through a fixed integer hash, so the same lead draws the same ring
    /// in every session. String hashing is randomized per process in .NET, so it is not
    /// used. A radius of 1 centers on the place. (-1, -1) for no such place.</summary>
    public static (int x, int y) RingCenter(WorldData world, int seed, int poiIndex, int radius)
    {
        if (world?.Pois == null || poiIndex < 0 || poiIndex >= world.Pois.Count || world.Pois[poiIndex] == null)
        {
            return (-1, -1);
        }
        var poi = world.Pois[poiIndex];
        int reach = System.Math.Max(0, radius - 1);
        if (reach == 0)
        {
            return (poi.X, poi.Y);
        }
        var cells = world.Disc(poi.X, poi.Y, reach);
        cells.RemoveAll(c => c.col == poi.X && c.row == poi.Y);
        if (cells.Count == 0)
        {
            return (poi.X, poi.Y);
        }
        var pick = cells[(int)(Mix(seed, poiIndex) % (uint)cells.Count)];
        return (pick.col, pick.row);
    }

    /// <summary>A lead that went because its place was Sighted: it held true.</summary>
    private static bool HeldTrue(Lead lead, List<WorldPoi> pois)
    {
        if (lead == null || pois == null || lead.PoiIndex < 0 || lead.PoiIndex >= pois.Count)
        {
            return false;
        }
        var poi = pois[lead.PoiIndex];
        return poi != null && poi.Discovered;
    }

    /// <summary>The note a true lead leaves (D182): at the desk, its own words and what
    /// the place turned out to be; on the map, one line. The place reads as the camp's
    /// lookouts would name it (CampSight.PlacePhrase). Placeholder copy (D186).</summary>
    private static void AnnounceHeld(CycleState cycle, Lead lead, WorldPoi poi)
    {
        var world = cycle.World;
        string what = world?.Tiles != null && world.TryIndex(poi.X, poi.Y, out int idx) && idx < world.Tiles.Length
            ? CampSight.PlacePhrase(world, poi)
            : "the place";
        string heard = string.IsNullOrEmpty(lead.Line) ? "" : lead.Line + "\n";
        ScryInbox.Post(cycle, ScryChannel.Note, HeldTitle,
                       $"{heard}It held true. The place is on your map: {what}.", "", "lead");
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add($"A lead held true. On your map now: {what}.");
        GD.Print($"[Leads] {lead.Id} held true: {what} at ({poi.X},{poi.Y}).");
    }

    /// <summary>A fixed integer hash of the seed and the place's index (a multiply and
    /// xor-shift mix), stable across sessions and platforms.</summary>
    private static uint Mix(int seed, int poiIndex)
    {
        unchecked
        {
            uint h = (uint)seed * 2654435761u;
            h ^= (uint)(poiIndex + 1) * 2246822519u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }
}
