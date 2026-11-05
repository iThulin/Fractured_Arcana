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
//                 on it. Since L4b1 (section 14): a settlement's talk,
//                 heard where the guild is; false rumors (F1), empty
//                 rings that stand until searched; the search, by a
//                 party at a ring's center or a camp that sees it
//                 whole; and each kingdom's record. Companions'
//                 memories and RevealPois arrive in L4b2, the Leads
//                 card in L5.
// Layer:          System (strategic)
// Collaborators:  Lead.cs, LeadRecord.cs, CycleState.cs (Leads,
//                 LeadRecords, WorldSeed, Kingdoms), WorldData.cs
//                 (WorldPoi.Discovered, Disc, Settlements),
//                 CycleInitializer.cs (prunes on every open),
//                 CampSight.cs (prunes, searches and hears talk after
//                 a camp; names places), ExpeditionManager.cs (the
//                 search in the field, talk on foot, the field's
//                 rings), ScryInbox (the desk),
//                 CycleState.PendingSiegeReports (the map's frontier
//                 news), WorldAtlas3D.cs (draws the rings through
//                 Stands and LabelFor), StrategicDebug.cs (the levers),
//                 CouncilQueries (the Courier Station's tier),
//                 LeadsSaveAssert.cs
// ============================================================

/// <summary>Rules over <see cref="CycleState.Leads"/>, the knowledge ladder's Rumored
/// stage. Every existing reveal path writes <c>WorldPoi.Discovered</c> without knowing
/// leads exist (spec section 2: they keep working untouched), so a lead is resolved
/// where leads are read, and on every open, never at the reveal sites (D130).
/// <see cref="Prune"/>, <see cref="AddScripted"/>, <see cref="HearTalk"/> and the
/// searches change only the cycle: their callers save it.</summary>
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

    // ── L4b1: talk, false rumors and the search (spec section 14, D212 to D222) ──

    /// <summary>A camp this close to a settlement's center hears its talk (D213).</summary>
    public const int TalkReach = 3;

    /// <summary>A settlement's talk is about places this close to it (D214).</summary>
    public const int TalkRange = 15;

    /// <summary>A false rumor's center lies at least this far from the settlement (D216).</summary>
    public const int FalseMinDistance = 3;

    /// <summary>The source, and the record's key, of a kingdom's talk (D218).</summary>
    public const string TalkPrefix = "talk:";

    /// <summary>The record key that keeps a settlement's once-a-lunation rule (D218).</summary>
    public const string CityPrefix = "city:";

    /// <summary>The desk note's title when a rumor is searched and comes to nothing (D219;
    /// placeholder copy).</summary>
    public const string NothingTitle = "A lead came to nothing";

    /// <summary>A kingdom's talk, for the reading (D215).</summary>
    public enum Reliability
    {
        Sound,
        Mixed,
        Idle,
    }

    /// <summary>The kinds a false rumor names, in the wild's mix (W1).</summary>
    private static readonly PoiKind[] FalseKinds =
    {
        PoiKind.Combat, PoiKind.Combat, PoiKind.Combat, PoiKind.Narrative, PoiKind.Narrative,
        PoiKind.Negotiation, PoiKind.Rest, PoiKind.Outpost,
    };

    /// <summary>Drop every lead whose place is Sighted (<c>WorldPoi.Discovered</c>), or
    /// whose place no longer exists in this reading's world. A lead that goes because
    /// its place was Sighted held true and says so: one desk note and one line of
    /// frontier news each, in the order the leads were heard (D182). A lead whose
    /// place is gone goes silently. A false lead has no place, and stands until it is
    /// searched (D217). Returns how many went. Idempotent and cheap: safe on every
    /// open and before every read. The caller saves when it returns more than zero, so
    /// a note is never posted twice.</summary>
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

    /// <summary>A lead stands while its place exists and is not yet Sighted; a false lead
    /// stands until it is searched (D217). Pure: the atlas and the field draw their rings
    /// through this and never prune while they draw (D183).</summary>
    public static bool Stands(Lead lead, List<WorldPoi> pois)
    {
        if (lead == null)
        {
            return false;
        }
        if (lead.IsFalse)
        {
            return true;
        }
        if (pois == null || lead.PoiIndex < 0 || lead.PoiIndex >= pois.Count)
        {
            return false;
        }
        var poi = pois[lead.PoiIndex];
        return poi != null && !poi.Discovered;
    }

    /// <summary>Hear a scripted lead (D180): the arc's (the Register's, a companion's
    /// memory, chapter III's city talk) or the debug lever's. Scripted leads are always
    /// true (spec section 4.6); a settlement's true talk is heard through this too. The
    /// id is the source plus the place's index, so hearing it again returns the lead
    /// already standing. The ring is centered by <see cref="RingCenter"/>. Returns the
    /// lead, or null when there is nothing to rumor: no such place, or a place already
    /// Sighted, whose lead would only resolve at once. Changes only the cycle: the caller
    /// saves.</summary>
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

    // ── Talk (D213 to D216) ──────────────────────────────────────────────

    /// <summary>A kingdom's talk for the reading (D215): sound half the time, mixed three
    /// times in ten, idle one time in five. Drawn from the world seed and the kingdom's id
    /// through fixed hashes, so it holds for the whole reading and in every session.</summary>
    public static Reliability TalkReliability(int seed, string kingdomId)
    {
        uint roll = Mix(seed, (int)Fnv(kingdomId ?? "")) % 10u;
        return roll < 5u ? Reliability.Sound : roll < 8u ? Reliability.Mixed : Reliability.Idle;
    }

    /// <summary>How many rumors a settlement tells in a lunation (D214): two with a Courier
    /// Station at tier 2, otherwise one.</summary>
    public static int TalkCount()
        => CouncilQueries.BuildingTier(SaveManager.ActiveSave, "courier_station") >= 2 ? 2 : 1;

    /// <summary>The castle makes camp at (x, y) (D213): the nearest settlement whose center
    /// lies within <see cref="TalkReach"/> talks, if it has not this lunation. The guild's
    /// home never talks. Returns the leads heard. Changes only the cycle: the caller
    /// saves.</summary>
    public static List<Lead> HearTalkNear(CycleState cycle, int x, int y)
    {
        var world = cycle?.World;
        WorldSettlement nearest = null;
        int best = int.MaxValue;
        foreach (var s in world?.Settlements ?? new List<WorldSettlement>())
        {
            if (s == null || s.IsGuildHome)
            {
                continue;
            }
            int d = world.HexDistance(x, y, s.CenterX, s.CenterY);
            if (d <= TalkReach && d < best)
            {
                best = d;
                nearest = s;
            }
        }
        return nearest == null ? new List<Lead>() : HearTalk(cycle, nearest, TalkCount());
    }

    /// <summary>A settlement talks (D213, D214): once a lunation, up to
    /// <paramref name="count"/> rumors. Each is true or false by its kingdom's
    /// reliability (D215). A lie that finds no empty ground is told true instead, and a
    /// settlement with nothing true to say and no lie to tell says nothing. Each rumor
    /// heard counts in its kingdom's record and leaves a desk note and a line of frontier
    /// news (D219). Returns the leads heard. Changes only the cycle: the caller saves.
    /// <paramref name="anyLunation"/> and <paramref name="lie"/> are the debug levers'
    /// (D221).</summary>
    public static List<Lead> HearTalk(CycleState cycle, WorldSettlement s, int count,
                                      bool anyLunation = false, bool lie = false)
    {
        var heard = new List<Lead>();
        var world = cycle?.World;
        if (world?.Pois == null || s == null || s.IsGuildHome || !world.InBounds(s.CenterX, s.CenterY))
        {
            return heard;
        }
        int lunation = cycle.Calendar?.CurrentLunation ?? 1;
        string cityId = CityExploreService.CityId(s);
        var city = Record(cycle, CityPrefix + cityId);
        if (!anyLunation && city.LastLunation == lunation)
        {
            return heard;
        }
        city.LastLunation = lunation;

        string kingdom = s.KingdomId ?? "";
        var reliability = TalkReliability(cycle.WorldSeed, kingdom);
        for (int n = 0; n < count; n++)
        {
            uint roll = Mix(unchecked(cycle.WorldSeed + lunation * 7919), (int)(Fnv(cityId) & 0x7fffffffu) + n);
            Lead lead = null;
            if (lie || Lies(reliability, roll))
            {
                lead = AddFalse(cycle, s, roll);
            }
            lead ??= AddTrueTalk(cycle, s);
            if (lead == null)
            {
                break;
            }
            Record(cycle, TalkPrefix + kingdom).Heard++;
            AnnounceHeard(cycle, lead);
            heard.Add(lead);
        }
        GD.Print($"[Leads] Talk at ({s.CenterX},{s.CenterY}), a {reliability} kingdom, on lunation {lunation}: "
                 + $"{heard.Count} rumor(s), {heard.FindAll(l => l.IsFalse).Count} false.");
        return heard;
    }

    /// <summary>The name a kingdom's talk goes by: its display name, or "town" when it has
    /// none (placeholder copy).</summary>
    public static string KingdomName(CycleState cycle, string kingdomId)
    {
        if (cycle?.Kingdoms != null && !string.IsNullOrEmpty(kingdomId)
            && cycle.Kingdoms.TryGetValue(kingdomId, out var k) && !string.IsNullOrEmpty(k?.DisplayName))
        {
            return k.DisplayName;
        }
        return "town";
    }

    /// <summary>Whether a rumor with this roll is false, for its kingdom's reliability (D215):
    /// never when sound, one time in three when mixed, one time in two when idle.</summary>
    private static bool Lies(Reliability reliability, uint roll) => reliability switch
    {
        Reliability.Mixed => roll % 3u == 0u,
        Reliability.Idle => roll % 2u == 0u,
        _ => false,
    };

    /// <summary>A true rumor from a settlement's talk (D214): the nearest undiscovered place
    /// in the wild within <see cref="TalkRange"/> of it with no lead standing.</summary>
    private static Lead AddTrueTalk(CycleState cycle, WorldSettlement s)
    {
        var world = cycle.World;
        var led = new HashSet<int>();
        foreach (var standing in cycle.Leads ?? new List<Lead>())
        {
            if (standing != null && !standing.IsFalse)
            {
                led.Add(standing.PoiIndex);
            }
        }

        int best = -1;
        int bestDist = int.MaxValue;
        for (int i = 0; i < world.Pois.Count; i++)
        {
            var poi = world.Pois[i];
            if (poi == null || poi.Discovered || poi.Consumed || led.Contains(i) || !CampSight.WildPlace(world, poi))
            {
                continue;
            }
            int d = world.HexDistance(s.CenterX, s.CenterY, poi.X, poi.Y);
            if (d <= TalkRange && d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        if (best < 0)
        {
            return null;
        }

        var place = world.Pois[best];
        var (cx, cy) = RingCenter(world, cycle.WorldSeed, best, CityRadius);
        string line = TalkLine(cycle, s, CampSight.PlacePhrase(world, place), cx, cy);
        return AddScripted(cycle, best, TalkPrefix + (s.KingdomId ?? ""), line, CityRadius);
    }

    /// <summary>A false rumor (D216): an empty ring. It names a kind from the wild's mix, in
    /// the words a true rumor of that kind would use, and is drawn on unexplored land in the
    /// wild, 3 to 15 tiles from the settlement and clear of every standing ring, where
    /// nothing of that kind lies within it. Null when no such ground exists.</summary>
    private static Lead AddFalse(CycleState cycle, WorldSettlement s, uint roll)
    {
        var world = cycle.World;
        int r = CityRadius;
        var kind = FalseKinds[(int)(roll % (uint)FalseKinds.Length)];
        var spots = new List<(int col, int row)>();
        foreach (var (col, row) in world.Disc(s.CenterX, s.CenterY, TalkRange))
        {
            if (world.HexDistance(s.CenterX, s.CenterY, col, row) >= FalseMinDistance
                && EmptyGround(cycle, col, row, r, kind))
            {
                spots.Add((col, row));
            }
        }
        if (spots.Count == 0)
        {
            GD.Print($"[Leads] No empty ground for a false rumor of {kind} near ({s.CenterX},{s.CenterY}).");
            return null;
        }

        var (cx, cy) = spots[(int)((roll / 8u) % (uint)spots.Count)];
        string source = TalkPrefix + (s.KingdomId ?? "");
        string id = $"{source}:at:{cx},{cy}";
        cycle.Leads ??= new List<Lead>();
        foreach (var standing in cycle.Leads)
        {
            if (standing != null && standing.Id == id)
            {
                return standing;
            }
        }

        var lead = new Lead
        {
            Id = id,
            PoiIndex = -1,
            CenterX = cx,
            CenterY = cy,
            Radius = r,
            Line = TalkLine(cycle, s, CampSight.KindPhrase(world, kind, cx, cy), cx, cy),
            Source = source,
            CreatedDay = cycle.Calendar?.AbsoluteDay ?? 0,
            IsFalse = true,
        };
        cycle.Leads.Add(lead);
        GD.Print($"[Leads] Heard {id}: false, no {kind} within {r} of ({cx},{cy}).");
        return lead;
    }

    /// <summary>Ground a false ring may stand on (D216): in bounds, land, in the wild, its
    /// center unexplored and fewer than half its tiles explored, nothing of the kind within
    /// the ring, and no standing ring's center within twice the radius.</summary>
    private static bool EmptyGround(CycleState cycle, int x, int y, int r, PoiKind kind)
    {
        var world = cycle.World;
        if (!world.InBounds(x, y))
        {
            return false;
        }
        var t = world.GetTile(x, y);
        if (TerrainClass.IsWater(t.Terrain) || t.SettlementIndex >= 0 || t.Discovery == TileDiscovery.Explored)
        {
            return false;
        }
        var disc = world.Disc(x, y, r);
        int explored = 0;
        foreach (var (col, row) in disc)
        {
            if (world.GetTile(col, row).Discovery == TileDiscovery.Explored)
            {
                explored++;
            }
        }
        if (explored * 2 >= disc.Count)
        {
            return false;
        }
        foreach (var poi in world.Pois)
        {
            if (poi != null && poi.Kind == kind && world.HexDistance(x, y, poi.X, poi.Y) <= r)
            {
                return false;
            }
        }
        foreach (var standing in cycle.Leads ?? new List<Lead>())
        {
            if (standing != null && world.HexDistance(x, y, standing.CenterX, standing.CenterY) <= 2 * r)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A rumor's words (D222, placeholder copy): "Talk in Dustreach: a refuge,
    /// somewhere to the south." The bearing runs from the settlement to the ring's center.</summary>
    private static string TalkLine(CycleState cycle, WorldSettlement s, string what, int cx, int cy)
    {
        string bearing = CampSight.Bearing(s.CenterX, s.CenterY, cx, cy);
        string toward = bearing.Length == 0 ? "close by" : $"somewhere to the {bearing}";
        return $"Talk in {KingdomName(cycle, s.KingdomId)}: {what}, {toward}.";
    }

    // ── The search (D217) ────────────────────────────────────────────────

    /// <summary>A party stops on a tile in the field (D217): every standing lead centered
    /// there is searched. A true lead's place is Sighted (its tile charted), and the prune
    /// that follows notes that it held true; a false lead comes to nothing. Returns the
    /// field's line for what the search found ("" when no lead is centered here; placeholder
    /// copy). Changes only the cycle: the caller saves.</summary>
    public static string SearchAt(CycleState cycle, int x, int y)
    {
        var world = cycle?.World;
        if (cycle?.Leads == null || cycle.Leads.Count == 0 || world?.Pois == null)
        {
            return "";
        }
        var found = new List<string>();
        var nothing = new List<Lead>();
        foreach (var lead in cycle.Leads)
        {
            if (lead == null || lead.CenterX != x || lead.CenterY != y)
            {
                continue;
            }
            if (lead.IsFalse)
            {
                nothing.Add(lead);
                continue;
            }
            if (!Stands(lead, world.Pois))
            {
                continue;
            }
            var poi = world.Pois[lead.PoiIndex];
            poi.Discovered = true;
            if (world.TryIndex(poi.X, poi.Y, out int idx) && world.Tiles[idx].Discovery == TileDiscovery.Unseen)
            {
                world.Tiles[idx].Discovery = TileDiscovery.Charted;
            }
            found.Add(CampSight.PlacePhrase(world, poi));
        }
        foreach (var lead in nothing)
        {
            ComeToNothing(cycle, lead);
        }
        Prune(cycle);

        if (found.Count > 0)
        {
            return $"You search the ring, and there it is: {string.Join(", ", found)}.";
        }
        return nothing.Count > 0
            ? "You search the ring. Nothing of the kind is here: the rumor came to nothing."
            : "";
    }

    /// <summary>A camp at (x, y) sees <paramref name="radius"/> tiles (D217): every false lead
    /// whose whole ring lies in sight comes to nothing. A true lead's place lies inside its
    /// ring, so the camp's sighting runs it down. Returns how many came to nothing. Changes
    /// only the cycle: the caller saves.</summary>
    public static int SearchFromCamp(CycleState cycle, int x, int y, int radius)
    {
        var world = cycle?.World;
        if (cycle?.Leads == null || cycle.Leads.Count == 0 || world == null)
        {
            return 0;
        }
        var nothing = new List<Lead>();
        foreach (var lead in cycle.Leads)
        {
            if (lead != null && lead.IsFalse
                && world.HexDistance(x, y, lead.CenterX, lead.CenterY) + lead.Radius <= radius)
            {
                nothing.Add(lead);
            }
        }
        foreach (var lead in nothing)
        {
            ComeToNothing(cycle, lead);
        }
        return nothing.Count;
    }

    // ── The record (D218) ────────────────────────────────────────────────

    /// <summary>The record kept under <paramref name="key"/> (D218), made when first asked
    /// for.</summary>
    public static LeadRecord Record(CycleState cycle, string key)
    {
        cycle.LeadRecords ??= new List<LeadRecord>();
        foreach (var r in cycle.LeadRecords)
        {
            if (r != null && r.Key == key)
            {
                return r;
            }
        }
        var made = new LeadRecord { Key = key ?? "" };
        cycle.LeadRecords.Add(made);
        return made;
    }

    /// <summary>A rumor from a kingdom's talk (D218): the sources that keep a record.</summary>
    public static bool IsTalk(Lead lead)
        => lead?.Source != null && lead.Source.StartsWith(TalkPrefix, System.StringComparison.Ordinal);

    /// <summary>The record line for a lead's source (D218; placeholder copy): "Talk in
    /// Dustreach: 2 of 3 held true." "" for the sources that keep none (the Register, a
    /// companion, a script). Reads only.</summary>
    public static string RecordLine(CycleState cycle, Lead lead)
    {
        if (!IsTalk(lead))
        {
            return "";
        }
        LeadRecord rec = null;
        foreach (var r in cycle?.LeadRecords ?? new List<LeadRecord>())
        {
            if (r != null && r.Key == lead.Source)
            {
                rec = r;
                break;
            }
        }
        string name = KingdomName(cycle, lead.Source.Substring(TalkPrefix.Length));
        int run = rec == null ? 0 : rec.HeldTrue + rec.CameToNothing;
        return run == 0
            ? $"Talk in {name}: none run down yet."
            : $"Talk in {name}: {rec.HeldTrue} of {run} held true.";
    }

    /// <summary>A ring's label (D218): the rumor's words, with its source's record under
    /// them. Reads only.</summary>
    public static string LabelFor(CycleState cycle, Lead lead)
    {
        if (lead == null)
        {
            return "";
        }
        string record = RecordLine(cycle, lead);
        return record.Length == 0 ? lead.Line ?? "" : $"{lead.Line}\n{record}";
    }

    // ── Notes ────────────────────────────────────────────────────────────

    /// <summary>A lead that went because its place was Sighted: it held true.</summary>
    private static bool HeldTrue(Lead lead, List<WorldPoi> pois)
    {
        if (lead == null || lead.IsFalse || pois == null || lead.PoiIndex < 0 || lead.PoiIndex >= pois.Count)
        {
            return false;
        }
        var poi = pois[lead.PoiIndex];
        return poi != null && poi.Discovered;
    }

    /// <summary>The note a true lead leaves (D182): at the desk, its own words and what
    /// the place turned out to be, and for talk its kingdom's record (D219); on the map,
    /// one line. The place reads as the camp's lookouts would name it
    /// (CampSight.PlacePhrase). Placeholder copy (D186). Since provinces PV2b-1 it is also
    /// a deed in the place's province (ProvinceInfluence.LeadGain).</summary>
    private static void AnnounceHeld(CycleState cycle, Lead lead, WorldPoi poi)
    {
        var world = cycle.World;
        string what = world?.Tiles != null && world.TryIndex(poi.X, poi.Y, out int idx) && idx < world.Tiles.Length
            ? CampSight.PlacePhrase(world, poi)
            : "the place";
        if (IsTalk(lead))
        {
            Record(cycle, lead.Source).HeldTrue++;
        }
        string heard = string.IsNullOrEmpty(lead.Line) ? "" : lead.Line + "\n";
        string record = RecordLine(cycle, lead);
        ScryInbox.Post(cycle, ScryChannel.Note, HeldTitle,
                       $"{heard}It held true. The place is on your map: {what}."
                       + (record.Length > 0 ? "\n" + record : ""), "", "lead");
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add($"A lead held true. On your map now: {what}.");
        GD.Print($"[Leads] {lead.Id} held true: {what} at ({poi.X},{poi.Y}).");
        // Provinces PV2b-1 (docs/provinces_v1_7g_deeds_and_courts.md): a rumor followed home is
        // a deed in its place's province, once, as the lead goes.
        ProvinceInfluence.Deed(cycle, poi.X, poi.Y, ProvinceInfluence.LeadGain);
    }

    /// <summary>The note a rumor leaves when it is heard (D219): at the desk, titled for its
    /// kingdom's talk, with its words and the record; on the map, its words.</summary>
    private static void AnnounceHeard(CycleState cycle, Lead lead)
    {
        string record = RecordLine(cycle, lead);
        string title = IsTalk(lead)
            ? $"Talk in {KingdomName(cycle, lead.Source.Substring(TalkPrefix.Length))}"
            : "A rumor";
        ScryInbox.Post(cycle, ScryChannel.Note, title,
                       (lead.Line ?? "") + (record.Length > 0 ? "\n" + record : ""), "", "lead");
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add(lead.Line ?? "");
    }

    /// <summary>A false lead searched (D217, D219): it goes, its kingdom's record counts it,
    /// and the desk and the frontier news say so. Placeholder copy.</summary>
    private static void ComeToNothing(CycleState cycle, Lead lead)
    {
        cycle.Leads.Remove(lead);
        if (IsTalk(lead))
        {
            Record(cycle, lead.Source).CameToNothing++;
        }
        string record = RecordLine(cycle, lead);
        string heard = string.IsNullOrEmpty(lead.Line) ? "" : lead.Line + "\n";
        ScryInbox.Post(cycle, ScryChannel.Note, NothingTitle,
                       $"{heard}It came to nothing: nothing of the kind is in that ring."
                       + (record.Length > 0 ? "\n" + record : ""), "", "lead");
        cycle.PendingSiegeReports ??= new List<string>();
        cycle.PendingSiegeReports.Add(record.Length > 0 ? $"A lead came to nothing. {record}" : "A lead came to nothing.");
        GD.Print($"[Leads] {lead.Id} came to nothing at ({lead.CenterX},{lead.CenterY}).");
    }

    // ── Hashes ───────────────────────────────────────────────────────────

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

    /// <summary>A fixed hash of a string (FNV-1a over its UTF-16 units), for ids such as a
    /// kingdom's or a settlement's. .NET randomizes string hashing per process, so it is not
    /// used.</summary>
    private static uint Fnv(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in s ?? "")
            {
                h ^= c;
                h *= 16777619u;
            }
            return h;
        }
    }
}
