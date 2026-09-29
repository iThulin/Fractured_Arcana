using Godot;
using System.Collections.Generic;

// ============================================================
// WaystoneAffixes.cs: the ground a waystone opens onto (2026-09-28)
//
// Purpose:        Ruled 2026-09-28 (Magos): "read, then temper". A raised
//                 waystone READS its site and arrives with one or two
//                 affixes; each is a pair, a danger and a reward, stated in a
//                 line. The place card then offers TEMPER (materials: strip
//                 one, its reward goes with it) and DEEPEN (materials: roll
//                 one more). Path of Exile's scour and alchemy, without the
//                 menu of choosing affixes outright, which is the version
//                 that gets solved.
//
//                 Every affix lands on a hook the expedition already has:
//                 fight strength (DifficultyMultAt), the rewards a won fight
//                 pays, Essence, rations at the start, the scrying reveal,
//                 and the supply drain. Nothing here invents a mechanic.
//
//                 An affix belongs to the waystone, not the dive, so it holds
//                 across every charge the stone has. The site is the site.
// Layer:          Data / FeatureBuilders
// Collaborators:  Waypoint (Affixes), ExpeditionAnchors (ConjureWaypoint),
//                 StrategicView (place card: Temper, Deepen),
//                 ExpeditionManager (reads the affixes of the tile it
//                 launched from)
// ============================================================

public static class WaystoneAffixes
{
    public const int MaxAffixes = 3;
    public const int TemperMaterials = 40;
    public const int DeepenMaterials = 60;

    public sealed class Def
    {
        public string Id;
        public string Name;
        public string Letter;
        public string Danger;
        public string Reward;
    }

    public const string Haunted = "haunted";
    public const string Warlords = "warlords";
    public const string LeyCrossed = "leycrossed";
    public const string FogBound = "fogbound";
    public const string FarFlung = "farflung";

    // ── The numbers each affix moves ───────────────────────────────────────

    /// <summary>Every fight in the window is this much harder.</summary>
    public const float FightMult = 1.25f;
    /// <summary>A won fight's splinters (Haunted) or gold (Warlords) scale by this.</summary>
    public const float RewardMult = 1.5f;
    /// <summary>Ley-crossed: Essence restored per fight won.</summary>
    public const int EssencePerWin = 2;
    /// <summary>Fog-bound: rations lost at the start, places revealed.</summary>
    public const int FogRations = 6;
    public const int FogReveal = 3;
    /// <summary>Far-flung: rations gained at the start; supply drain multiplier.</summary>
    public const int FarRations = 10;
    public const int FarDrainMult = 2;

    public static readonly Def[] All =
    {
        new Def { Id = Haunted,    Name = "Haunted ground",    Letter = "H",
                  Danger = "every fight a quarter harder",     Reward = "half again the splinters a won fight pays" },
        new Def { Id = Warlords,   Name = "Warlords' country", Letter = "W",
                  Danger = "every fight a quarter harder",     Reward = "half again the gold a won fight pays" },
        new Def { Id = LeyCrossed, Name = "Ley-crossed",       Letter = "L",
                  Danger = "every fight a quarter harder",     Reward = $"{EssencePerWin} Essence back for every fight won" },
        new Def { Id = FogBound,   Name = "Fog-bound",         Letter = "F",
                  Danger = $"{FogRations} fewer rations at the start", Reward = $"the {FogReveal} nearest hidden places marked on arrival" },
        new Def { Id = FarFlung,   Name = "Far-flung",         Letter = "R",
                  Danger = "the supply line drains twice as hard", Reward = $"{FarRations} more rations at the start" },
    };

    public static Def Get(string id)
    {
        foreach (var d in All)
        {
            if (d.Id == id)
            {
                return d;
            }
        }
        return null;
    }

    public static string Line(Def d) => $"{d.Name}: {d.Danger}; {d.Reward}.";

    // ── Reading the site ───────────────────────────────────────────────────

    /// <summary>What the ground suggests, weighted. Corruption leans haunted,
    /// hostile or warring ground leans warlords, heavy country leans fog, and
    /// ground far from any other anchor leans far-flung. Ley-crossed is
    /// always possible.</summary>
    private static List<(string id, int weight)> WeightsAt(CycleState cycle, int x, int y)
    {
        var world = cycle.World;
        var tile = world.GetTile(x, y);
        var list = new List<(string, int)>
        {
            (LeyCrossed, 3),
            (Haunted, tile.Corruption >= 20 ? 6 : 1),
            (Warlords, 2),
            (FogBound, 2),
            (FarFlung, 2),
        };
        string kid = tile.KingdomId ?? "";
        if (!string.IsNullOrEmpty(kid) && CouncilQueries.StanceFor(cycle, kid) <= KingdomStance.Unfriendly)
        {
            list[2] = (Warlords, 6);
        }
        string terrain = tile.Terrain.ToString();
        if (terrain.Contains("Forest") || terrain.Contains("Swamp") || terrain.Contains("Marsh") || terrain.Contains("Jungle"))
        {
            list[3] = (FogBound, 6);
        }
        int nearest = int.MaxValue;
        foreach (var sp in world.StagingPoints ?? new List<StagingPoint>())
        {
            if (sp == null || !sp.Available || (sp.X == x && sp.Y == y))
            {
                continue;
            }
            nearest = Mathf.Min(nearest, world.HexDistance(x, y, sp.X, sp.Y));
        }
        if (nearest > 12)
        {
            list[4] = (FarFlung, 6);
        }
        return list;
    }

    /// <summary>Roll the affixes a new waystone reads from its site: one, or
    /// two where the ground is corrupted or hostile. Deterministic per tile and
    /// lunation, so a reload cannot reroll a waystone.</summary>
    public static List<string> ReadSite(CycleState cycle, int x, int y)
    {
        var result = new List<string>();
        if (cycle?.World == null || !cycle.World.InBounds(x, y))
        {
            return result;
        }
        var tile = cycle.World.GetTile(x, y);
        string kid = tile.KingdomId ?? "";
        bool harsh = tile.Corruption >= 20
                     || (!string.IsNullOrEmpty(kid) && CouncilQueries.StanceFor(cycle, kid) == KingdomStance.Hostile);
        int count = harsh ? 2 : 1;
        var rng = RngFor(cycle, x, y, 0);
        for (int i = 0; i < count; i++)
        {
            string pick = Pick(cycle, x, y, result, rng);
            if (pick != null)
            {
                result.Add(pick);
            }
        }
        return result;
    }

    private static string Pick(CycleState cycle, int x, int y, List<string> except, System.Random rng)
    {
        var weights = WeightsAt(cycle, x, y);
        int total = 0;
        foreach (var (id, w) in weights)
        {
            if (!except.Contains(id))
            {
                total += w;
            }
        }
        if (total <= 0)
        {
            return null;
        }
        int roll = rng.Next(total);
        foreach (var (id, w) in weights)
        {
            if (except.Contains(id))
            {
                continue;
            }
            if (roll < w)
            {
                return id;
            }
            roll -= w;
        }
        return null;
    }

    private static System.Random RngFor(CycleState cycle, int x, int y, int salt)
    {
        unchecked
        {
            int seed = cycle.WorldSeed;
            seed = (seed * 397) ^ x;
            seed = (seed * 397) ^ y;
            seed = (seed * 397) ^ (cycle.Calendar?.CurrentLunation ?? 0);
            seed = (seed * 397) ^ salt;
            return new System.Random(seed);
        }
    }

    // ── Temper and deepen ─────────────────────────────────────────────────

    public static Waypoint WaypointAt(CycleState cycle, int x, int y)
        => cycle?.Waypoints?.Find(w => w != null && !w.IsSpent && w.X == x && w.Y == y);

    public static bool CanTemper(CycleState cycle, Waypoint w, out string why)
    {
        why = "";
        if (w == null || w.Affixes == null || w.Affixes.Count == 0)
        {
            why = "Nothing to temper.";
            return false;
        }
        if (cycle.BuildMaterials < TemperMaterials)
        {
            why = $"Tempering costs {TemperMaterials} materials; the stores hold {cycle.BuildMaterials}.";
            return false;
        }
        return true;
    }

    public static bool CanDeepen(CycleState cycle, Waypoint w, out string why)
    {
        why = "";
        if (w == null)
        {
            why = "No waystone.";
            return false;
        }
        if ((w.Affixes?.Count ?? 0) >= MaxAffixes)
        {
            why = $"A waystone holds at most {MaxAffixes}.";
            return false;
        }
        if (cycle.BuildMaterials < DeepenMaterials)
        {
            why = $"Deepening costs {DeepenMaterials} materials; the stores hold {cycle.BuildMaterials}.";
            return false;
        }
        return true;
    }

    /// <summary>Strip one affix. Its danger and its reward both go.</summary>
    public static string Temper(CycleState cycle, Waypoint w, string affixId)
    {
        if (!CanTemper(cycle, w, out _) || !w.Affixes.Contains(affixId))
        {
            return null;
        }
        cycle.BuildMaterials -= TemperMaterials;
        w.Affixes.Remove(affixId);
        RefreshName(cycle, w);
        SaveManager.MarkDirty();
        return $"The waystone at ({w.X},{w.Y}) is tempered: {Get(affixId)?.Name ?? affixId} is gone, and what it paid with it.";
    }

    /// <summary>Roll one more affix from the site.</summary>
    public static string Deepen(CycleState cycle, Waypoint w)
    {
        if (!CanDeepen(cycle, w, out _))
        {
            return null;
        }
        w.Affixes ??= new List<string>();
        var rng = RngFor(cycle, w.X, w.Y, 1 + w.Affixes.Count);
        string pick = Pick(cycle, w.X, w.Y, w.Affixes, rng);
        if (pick == null)
        {
            return null;
        }
        cycle.BuildMaterials -= DeepenMaterials;
        w.Affixes.Add(pick);
        RefreshName(cycle, w);
        SaveManager.MarkDirty();
        return $"The waystone at ({w.X},{w.Y}) is deepened: {Line(Get(pick))}";
    }

    /// <summary>The waystone's staging point carries its letters in its name,
    /// so the map beacon's label and the place card title show them.</summary>
    public static void RefreshName(CycleState cycle, Waypoint w)
    {
        var sp = cycle?.World?.StagingPoints?.Find(s => s != null && s.X == w.X && s.Y == w.Y
                                                      && s.Source == ExpeditionAnchors.WaypointStagingSource);
        if (sp == null)
        {
            return;
        }
        string baseName = sp.Name;
        int cut = baseName.IndexOf("  [", System.StringComparison.Ordinal);
        if (cut >= 0)
        {
            baseName = baseName.Substring(0, cut);
        }
        sp.Name = baseName + Letters(w);
    }

    public static string Letters(Waypoint w)
    {
        if (w?.Affixes == null || w.Affixes.Count == 0)
        {
            return "";
        }
        var parts = new List<string>();
        foreach (var id in w.Affixes)
        {
            var d = Get(id);
            if (d != null)
            {
                parts.Add($"[{d.Letter}]");
            }
        }
        return parts.Count == 0 ? "" : "  " + string.Join("", parts);
    }

    // ── Read by the expedition ─────────────────────────────────────────────

    /// <summary>The affixes of the waystone a run launched from, or empty.</summary>
    public static List<string> ActiveFor(CycleState cycle, int stagingX, int stagingY)
    {
        var w = cycle?.Waypoints?.FindLast(x => x != null && x.X == stagingX && x.Y == stagingY);
        return w?.Affixes != null ? new List<string>(w.Affixes) : new List<string>();
    }
}
