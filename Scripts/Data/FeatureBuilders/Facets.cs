using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// Facets.cs
//
// Purpose:        Partial shards (campus_building_upgrades_design_v1
//                 §8b, ruled 2026-09-29). A facet is a face of a
//                 fragment, cut away by the Sundering and shed by its
//                 leak. Five named facets per fragment school, each
//                 earned by one deed, once per save; they gate the
//                 school seats' second and third tiers (3 and 5).
//
//                 Stored as flags on EternalLedger.MetaNarrativeFlags
//                 (facet_<school>_<source>), so no new save struct:
//                 facets are eternal, like the fragments they come from.
//
//                 Sources for a fragment school:
//                   survey   a field party surveys the school's zone
//                   gate     the zone's guardian falls (or its gate opens)
//                   sanctum  the fragment is claimed, by any path
//                   seat     the school's archmage is resolved
//                            (Allied, Coerced or Overthrown)
//                   blight   retrieved from the site the leak sheds
//                            beside the zone once its kingdom is
//                            corrupted (SiteBlightFacets, TryRetrieve)
//                 The first four are read from state by Sweep, which
//                 ProgressionSweep runs on every save. The fifth is
//                 granted on retrieval.
//
//                 Druid: seat (Hess) today. The green facets (one grown
//                 at each claimed zone, retrieved by a field party) wait
//                 on their story work. Adept: counts every facet the
//                 guild holds, of any school (T2 at 5, T3 at 12).
// Layer:          Data / Feature builder
// Collaborators:  ProgressionSweep.cs (SchoolOfFragment, the save-time
//                 sweep), ShardZone (Surveyed, GuardianCleared,
//                 ShardCollected, FacetSitePoiIndex), CampaignState
//                 (dispositions, corruption), ArchmageRegistry,
//                 WorldGenerator.SiteRuntimePoiNear, ScryInbox,
//                 CampusConstruction (tier gate), CampusSeatPanel (UI).
// Owed:           story work for every source (Magos 2026-09-29): the
//                 blight lines below are first drafts.
// ============================================================

/// <summary>Facet reads, grants and the blight site. Stateless.</summary>
public static class Facets
{
    public const string Survey = "survey";
    public const string Gate = "gate";
    public const string Sanctum = "sanctum";
    public const string Seat = "seat";
    public const string Blight = "blight";

    /// <summary>The five sources of a fragment school, in the order a guild
    /// usually meets them.</summary>
    public static readonly string[] FragmentSources = { Survey, Blight, Gate, Sanctum, Seat };

    /// <summary>Facets a seat's tier 2 and tier 3 need (ruling 4). The Adept seat
    /// reads its own thresholds from its JSON.</summary>
    public const int SeatTier2 = 3;
    public const int SeatTier3 = 5;

    public static string Flag(string school, string source)
        => $"facet_{Norm(school)}_{source}";

    // ── Reads ────────────────────────────────────────────────────────────

    public static bool Has(GuildSaveData save, string school, string source)
        => save?.Ledger?.MetaNarrativeFlags?.Contains(Flag(school, source)) ?? false;

    /// <summary>Facets a school holds. Adept counts every facet of every school.</summary>
    public static int Count(GuildSaveData save, string school)
    {
        var flags = save?.Ledger?.MetaNarrativeFlags;
        if (flags == null)
        {
            return 0;
        }
        bool all = string.Equals(Norm(school), "adept", StringComparison.Ordinal);
        string prefix = $"facet_{Norm(school)}_";
        int n = 0;
        foreach (var f in flags)
        {
            if (f == null || !f.StartsWith("facet_", StringComparison.Ordinal))
            {
                continue;
            }
            if (all || f.StartsWith(prefix, StringComparison.Ordinal))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>The sources a school can ever earn from, for the seat panel.</summary>
    public static string[] SourcesFor(string school)
    {
        if (!string.IsNullOrEmpty(FragmentKeyOf(school)))
        {
            return FragmentSources;
        }
        if (string.Equals(Norm(school), "druid", StringComparison.Ordinal))
        {
            return new[] { Seat };
        }
        return Array.Empty<string>();
    }

    /// <summary>One line telling the player where a facet comes from.</summary>
    public static string SourceHint(string school, string source)
    {
        string zone = ZoneNameOf(school);
        return source switch
        {
            Survey => $"Survey {zone} with a field party.",
            Blight => $"Retrieve what {zone} sheds once its kingdom is corrupted. The site appears beside the zone.",
            Gate => $"Pass the guardian's gate at {zone}.",
            Sanctum => $"Claim the fragment in {zone}.",
            Seat => $"Resolve the {school} archmage: ally, coerce or overthrow.",
            _ => source,
        };
    }

    /// <summary>The fragment key a school answers to, or "".</summary>
    public static string FragmentKeyOf(string school)
    {
        foreach (var key in ShardZones.FragmentKeys)
        {
            if (string.Equals(ProgressionSweep.SchoolOfFragment(key), school, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }
        return "";
    }

    // ── The sweep (state-derived sources) ────────────────────────────────

    /// <summary>Grant every facet the save has earned from state. Idempotent;
    /// run by ProgressionSweep before each save. Returns the number granted.</summary>
    public static int Sweep(GuildSaveData save, HashSet<string> paid)
    {
        if (save?.Ledger == null || paid == null)
        {
            return 0;
        }
        int granted = 0;

        // Zone deeds, this timeline.
        var zones = save.Cycle?.World?.ShardZones;
        if (zones != null)
        {
            foreach (var z in zones)
            {
                string school = ProgressionSweep.SchoolOfFragment(z?.FragmentKey);
                if (string.IsNullOrEmpty(school))
                {
                    continue;
                }
                if (z.Surveyed)
                {
                    granted += GrantInternal(save, paid, school, Survey);
                }
                if (z.GuardianCleared || z.ShardCollected)
                {
                    granted += GrantInternal(save, paid, school, Gate);
                }
                if (z.ShardCollected)
                {
                    granted += GrantInternal(save, paid, school, Sanctum);
                }
            }
        }

        // Fragments claimed in an earlier timeline: the gate was passed then too.
        foreach (var key in ShardZones.FragmentKeys)
        {
            if (!paid.Contains($"fragment_{key}_collected"))
            {
                continue;
            }
            string school = ProgressionSweep.SchoolOfFragment(key);
            granted += GrantInternal(save, paid, school, Gate);
            granted += GrantInternal(save, paid, school, Sanctum);
        }

        // Archmage seats, this timeline.
        var campaign = save.Cycle?.Campaign;
        if (campaign != null)
        {
            foreach (var kv in ArchmageRegistry.All)
            {
                var def = kv.Value;
                if (def == null || string.IsNullOrEmpty(def.School))
                {
                    continue;
                }
                var d = campaign.GetDisposition(def.Id);
                if (d == ArchmageDisposition.Allied || d == ArchmageDisposition.Coerced
                    || d == ArchmageDisposition.Overthrown)
                {
                    granted += GrantInternal(save, paid, def.School, Seat);
                }
            }
        }
        return granted;
    }

    // ── The blight site (source 5) ───────────────────────────────────────

    /// <summary>Lunation tick: site the blight facet beside every zone whose
    /// kingdom has taken corruption and whose facet is still out there. One
    /// Narrative POI per zone, sited once per timeline. Returns report lines.</summary>
    public static List<string> SiteBlightFacets(CycleState cycle, GuildSaveData save)
    {
        var lines = new List<string>();
        var world = cycle?.World;
        var campaign = cycle?.Campaign;
        if (world?.ShardZones == null || campaign == null || cycle.Kingdoms == null)
        {
            return lines;
        }
        foreach (var z in world.ShardZones)
        {
            string school = ProgressionSweep.SchoolOfFragment(z.FragmentKey);
            if (string.IsNullOrEmpty(school))
            {
                continue;
            }
            // A site whose facet the guild already holds (another timeline, a
            // debug grant) is spent, so it never plays as a plain encounter.
            if (z.FacetSitePoiIndex >= 0 && z.FacetSitePoiIndex < world.Pois.Count && Has(save, school, Blight))
            {
                world.Pois[z.FacetSitePoiIndex].Consumed = true;
            }
            if (z.FacetSitePoiIndex >= 0 || Has(save, school, Blight))
            {
                continue;
            }
            if (!cycle.Kingdoms.TryGetValue(z.KingdomId, out var kingdom)
                || campaign.GetCorruption(kingdom.TemplateRegionId) < 1)
            {
                continue;
            }
            int idx = WorldGenerator.SiteRuntimePoiNear(world, PoiKind.Narrative, z.KingdomId,
                                                        z.CenterX, z.CenterY, minDist: 3);
            if (idx < 0)
            {
                continue;
            }
            z.FacetSitePoiIndex = idx;
            string line = $"{z.Name} is leaking. Something it shed lies in the open nearby: a {school} facet, "
                        + "there for a field party to take.";
            ScryInbox.Post(cycle, ScryChannel.Messenger, "A facet in the blight", line, "", "facet");
            lines.Add(line);
            GD.Print($"[Facets] Blight facet for {school} sited at POI {idx} beside {z.Name}.");
        }
        return lines;
    }

    /// <summary>The zone whose blight facet sits on this world tile, untaken, or null.</summary>
    public static ShardZone BlightSiteAt(WorldData world, GuildSaveData save, int x, int y)
    {
        if (world?.ShardZones == null || world.Pois == null)
        {
            return null;
        }
        foreach (var z in world.ShardZones)
        {
            int i = z.FacetSitePoiIndex;
            if (i < 0 || i >= world.Pois.Count)
            {
                continue;
            }
            var poi = world.Pois[i];
            if (poi.X != x || poi.Y != y || poi.Consumed)
            {
                continue;
            }
            string school = ProgressionSweep.SchoolOfFragment(z.FragmentKey);
            if (!Has(save, school, Blight))
            {
                return z;
            }
        }
        return null;
    }

    /// <summary>Take the blight facet at a zone's site. Consumes the site POI.
    /// Returns the story line to show, or null if there was nothing to take.</summary>
    public static string RetrieveBlight(GuildSaveData save, WorldData world, ShardZone z)
    {
        if (save?.Ledger == null || world == null || z == null)
        {
            return null;
        }
        string school = ProgressionSweep.SchoolOfFragment(z.FragmentKey);
        if (string.IsNullOrEmpty(school) || !Grant(save, school, Blight))
        {
            return null;
        }
        if (z.FacetSitePoiIndex >= 0 && z.FacetSitePoiIndex < world.Pois.Count)
        {
            world.Pois[z.FacetSitePoiIndex].Consumed = true;
        }
        return BlightLine(z.FragmentKey) + $" A {school} facet: {Count(save, school)} of {SeatTier3}.";
    }

    /// <summary>First-draft retrieval lines, one per fragment (story work owed).</summary>
    private static string BlightLine(string key) => key switch
    {
        "primal" => "The storm that stays has thrown off a piece of itself: a shard-face still warm, "
                  + "turning the rain around it into sleet and back.",
        "axiom" => "Lines of a proof are cut into the ground here, and at their end, the face of the argument itself.",
        "moment" => "Everything within a pace of it is a heartbeat behind. You take it before the delay reaches you.",
        "binding" => "It is set in a ring of flowers nobody planted. It is glad you came.",
        "schema" => "A little engine has built itself around the facet, and stops when you lift it out.",
        "deathless" => "The marsh has kept it the way the dead are kept: cold, and exactly as it was.",
        _ => "A face of the fragment lies in the open.",
    };

    // ── Grants ───────────────────────────────────────────────────────────

    /// <summary>Grant one facet. True if it was new.</summary>
    public static bool Grant(GuildSaveData save, string school, string source)
    {
        if (save?.Ledger == null || string.IsNullOrEmpty(school))
        {
            return false;
        }
        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        string flag = Flag(school, source);
        if (save.Ledger.MetaNarrativeFlags.Contains(flag))
        {
            return false;
        }
        save.Ledger.MetaNarrativeFlags.Add(flag);
        SaveManager.MarkDirty();
        GD.Print($"[Facets] {flag} granted ({Count(save, school)} for {school}).");
        return true;
    }

    private static int GrantInternal(GuildSaveData save, HashSet<string> paid, string school, string source)
    {
        string flag = Flag(school, source);
        if (paid.Contains(flag))
        {
            return 0;
        }
        save.Ledger.MetaNarrativeFlags.Add(flag);
        paid.Add(flag);
        ScryInbox.Post(save.Cycle, ScryChannel.Note, "A facet recovered",
            $"A {school} facet is the guild's ({SourceLabel(source)}). The {school} seat reads {Count(save, school)} of {SeatTier3}.",
            "", "facet");
        GD.Print($"[Facets] {flag} granted by the sweep.");
        return 1;
    }

    private static string SourceLabel(string source) => source switch
    {
        Survey => "the survey",
        Gate => "the guardian's gate",
        Sanctum => "the sanctum",
        Seat => "the archmage's seat",
        Blight => "the blight",
        _ => source,
    };

    private static string ZoneNameOf(string school)
    {
        return FragmentKeyOf(school) switch
        {
            "axiom" => "The Infinite Athenaeum",
            "binding" => "The Bound Vault",
            "deathless" => "The Deathless Reliquary",
            "moment" => "The Stilled Hour",
            "schema" => "The Pattern Sanctum",
            "primal" => "The Primal Heart",
            _ => "its zone",
        };
    }

    private static string Norm(string school) => (school ?? "").Trim().ToLowerInvariant();
}
