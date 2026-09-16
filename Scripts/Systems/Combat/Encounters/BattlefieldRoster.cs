using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// BattlefieldRoster.cs
//
// Purpose:        Rotation for combat maps and objectives
//                 (docs/battlefield_variety_spec_v1.md §2, §4).
//                 Replaces the 1:1 terrain → recipe lookup with
//                 weighted per-tier pools: a global table in
//                 Data/Encounters/battlefield_rotation.json, which
//                 each region may supplement with its own
//                 `battlefields` block. Also rolls an objective for
//                 compositions that authored none. Keeps a short
//                 process-lifetime history so the same map does not
//                 repeat back to back.
// Layer:          System (data lookup)
// Collaborators:  CombatManager.ConfigureAndGenerateMap (caller),
//                 TerrainRecipeMap (fallback when a pool is empty),
//                 EncounterPoolLoader (ObjectiveData shape + the loud
//                 objective validation), MapRecipeRegistry (ids)
// ============================================================

public static class BattlefieldRoster
{
    public sealed class RecipeEntry
    {
        public string Recipe = "";
        public float Weight = 1f;
        /// <summary>Overworld terrain names this entry applies to. Empty = any.</summary>
        public HashSet<string> Terrains = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ObjectiveEntry
    {
        public float Weight = 1f;
        public ObjectiveData Data = new();
    }

    /// <summary>One pressure add-on template (battlefield_variety_v1.1 §7). Kind
    /// "none" is a weighted blank so authors control frequency in the same list.
    /// Kind "arrival" becomes a reinforcements_arrive map event whose units are drawn from
    /// the REGION's own encounter pool at `Pool` tier, so the burrow on the Crags
    /// spits out crag units, not generic soldiers.</summary>
    public sealed class PressureEntry
    {
        public string Kind = "none";
        public float Weight = 1f;
        public string Id = "";
        public int Round = 3;
        public int RepeatEvery = 0;
        public int Telegraph = 1;
        public string When = "";
        public string At = "flank:3";
        public int Count = 2;
        public string Pool = "";          // tier name; empty = the fight's tier
        public string Announce = "";
        public string Wake = "";
        public string LeverAt = "";
        public string LeverMode = "";
        public int LeverAmount = 1;

        // kind "turn" (objective_turns, v1.2 §8)
        public string From = "annihilate";   // "annihilate" | "any" | a kind: the objective the fight must currently have
        public string To = "survive";
        public int Rounds = 0;               // relative
        public string ZoneAnchor = "midpoint";
        public int ZoneRadius = 2;
        public int BreachLimit = 2;
        public string Description = "";
    }

    private sealed class Pools
    {
        // tier → terrain → entries   (terrain "*" = any)
        public readonly Dictionary<string, Dictionary<string, List<RecipeEntry>>> Recipes =
            new(StringComparer.OrdinalIgnoreCase);
        // tier → entries
        public readonly Dictionary<string, List<ObjectiveEntry>> Objectives =
            new(StringComparer.OrdinalIgnoreCase);
        // tier → entries
        public readonly Dictionary<string, List<PressureEntry>> Pressure =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private const string GlobalPath = "res://Data/Encounters/battlefield_rotation.json";
    private const string AnyTerrain = "*";

    private static Pools _global;
    private static bool _globalLoaded;
    private static int _historyWindow = 2;
    private static readonly Dictionary<string, Pools> _regionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _recipeHistory = new();
    private static string _lastDeploymentId = "";

    /// <summary>The deployment id the previous fight rolled, so the grid can steer
    /// away from repeating it. Empty before the first fight.</summary>
    public static string LastDeploymentId => _lastDeploymentId;

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Resolves the battlefield recipe for a fight. Order: the forced id (a
    /// composition's map_recipe, or a debug override) → weighted pick from the region
    /// + global pools for this tier and terrain, minus the recent-history window →
    /// TerrainRecipeMap (the pre-spec 1:1 table). Never returns empty.</summary>
    public static string ResolveRecipe(string regionId, EncounterTier tier, string terrain, string forcedRecipe)
    {
        if (!string.IsNullOrEmpty(forcedRecipe))
            return forcedRecipe;

        EnsureGlobalLoaded();
        string tierKey = TierKey(tier);
        var pool = new List<RecipeEntry>();
        Collect(_global, tierKey, terrain, pool);
        Collect(LoadRegion(regionId), tierKey, terrain, pool);

        // Drop the last N recipes unless that empties the pool: variety, but never
        // at the price of a fight with no map.
        var fresh = pool.FindAll(e => !_recipeHistory.Contains(e.Recipe));
        if (fresh.Count > 0)
            pool = fresh;

        string picked = WeightedPick(pool);
        if (string.IsNullOrEmpty(picked))
        {
            picked = TerrainRecipeMap.Resolve(terrain);
            GD.Print($"[Roster] no pool for {tierKey}/{terrain ?? "?"} in '{regionId}': terrain table → '{picked}'.");
        }
        else
        {
            GD.Print($"[Roster] {tierKey}/{terrain} in '{regionId}': '{picked}' from {pool.Count} candidate(s) " +
                     $"(history: {string.Join(",", _recipeHistory)}).");
        }
        return picked;
    }

    /// <summary>Rolls an objective for a fight whose composition authored none. Null =
    /// annihilate (either rolled, or no pool for the tier). Every rolled entry goes
    /// through EncounterPoolLoader.BuildObjective, the same loud validation an
    /// authored objective gets, so a bad pool entry is logged, not silently degraded.</summary>
    public static CombatObjectiveDef RollObjective(string regionId, EncounterTier tier)
    {
        EnsureGlobalLoaded();
        string tierKey = TierKey(tier);
        var pool = new List<ObjectiveEntry>();
        if (_global.Objectives.TryGetValue(tierKey, out var g))
            pool.AddRange(g);
        var region = LoadRegion(regionId);
        if (region != null && region.Objectives.TryGetValue(tierKey, out var r))
            pool.AddRange(r);
        if (pool.Count == 0)
            return null;

        float total = 0f;
        foreach (var e in pool)
            total += Mathf.Max(0f, e.Weight);
        if (total <= 0f)
            return null;
        float roll = GD.Randf() * total;
        ObjectiveEntry chosen = pool[pool.Count - 1];
        foreach (var e in pool)
        {
            roll -= Mathf.Max(0f, e.Weight);
            if (roll <= 0f)
            { chosen = e; break; }
        }

        var def = EncounterPoolLoader.BuildObjective(chosen.Data, $"roster:{regionId}/{tierKey}");
        GD.Print($"[Roster] objective for {tierKey} in '{regionId}': {(def == null ? "annihilate" : def.Kind)}.");
        return def;
    }

    /// <summary>Rolls one pressure add-on for the fight (battlefield_variety_v1.1 §7)
    /// and materialises it as a map event, or returns null (a "none" roll, no pool,
    /// or nothing to draw units from). Rules the caller relies on: at most one add-on
    /// per fight; the caller skips fights whose composition authored waves or whose
    /// recipe already carries a reinforcements_arrive event, so the Warren's burrow and an
    /// authored wave never stack with a rolled arrival.</summary>
    public static MapEventDef RollPressure(string regionId, EncounterTier tier, float difficultyMult,
                                           string currentObjectiveKind = "annihilate")
    {
        EnsureGlobalLoaded();
        string tierKey = TierKey(tier);
        var pool = new List<PressureEntry>();
        if (_global.Pressure.TryGetValue(tierKey, out var g))
            pool.AddRange(g);
        var region = LoadRegion(regionId);
        if (region != null && region.Pressure.TryGetValue(tierKey, out var r))
            pool.AddRange(r);
        if (pool.Count == 0)
            return null;

        float total = 0f;
        foreach (var e in pool)
            total += Mathf.Max(0f, e.Weight);
        if (total <= 0f)
            return null;
        float roll = GD.Randf() * total;
        PressureEntry chosen = pool[pool.Count - 1];
        foreach (var e in pool)
        {
            roll -= Mathf.Max(0f, e.Weight);
            if (roll <= 0f)
            { chosen = e; break; }
        }

        if (chosen.Kind == "turn")
            return BuildTurn(chosen, regionId, tierKey, currentObjectiveKind);

        if (chosen.Kind != "arrival")
        {
            GD.Print($"[Roster] pressure for {tierKey} in '{regionId}': none.");
            return null;
        }

        var units = DrawUnits(regionId, string.IsNullOrEmpty(chosen.Pool) ? tierKey : chosen.Pool, chosen.Count);
        if (units.Count == 0)
        {
            GD.Print($"[Roster] pressure arrival '{chosen.Id}' in '{regionId}': no units to draw from pool '{chosen.Pool}'; skipped.");
            return null;
        }

        var raw = new Godot.Collections.Dictionary();
        raw["at"] = chosen.At;
        var arr = new Godot.Collections.Array();
        foreach (var u in units)
            arr.Add(u);
        raw["units"] = arr;
        raw["difficulty"] = difficultyMult;
        if (!string.IsNullOrEmpty(chosen.Announce))
            raw["announce"] = chosen.Announce;
        if (!string.IsNullOrEmpty(chosen.Wake))
            raw["wake"] = chosen.Wake;

        var ev = new MapEventDef
        {
            Kind = "reinforcements_arrive",
            Id = string.IsNullOrEmpty(chosen.Id) ? "roster_arrival" : chosen.Id,
            Round = Math.Max(1, chosen.Round),
            RepeatEvery = Math.Max(0, chosen.RepeatEvery),
            Telegraph = Math.Max(1, chosen.Telegraph),   // arrivals always warn a round ahead
            When = chosen.When ?? "",
            LeverAt = chosen.LeverAt ?? "",
            LeverMode = chosen.LeverMode ?? "",
            LeverAmount = Math.Max(1, chosen.LeverAmount),
            Raw = raw,
        };

        GD.Print($"[Roster] pressure for {tierKey} in '{regionId}': arrival '{ev.Id}' at {chosen.At}, " +
                 $"round {ev.Round}{(ev.RepeatEvery > 0 ? $" every {ev.RepeatEvery}" : "")}" +
                 $"{(string.IsNullOrEmpty(ev.When) ? "" : $" when {ev.When}")}, units [{string.Join(",", units)}]" +
                 $"{(string.IsNullOrEmpty(ev.LeverAt) ? "" : $", lever {ev.LeverMode} at {ev.LeverAt}")}.");
        return ev;
    }

    /// <summary>kind "turn": an objective_turns event (v1.2 §8). Applies only when the
    /// fight's current objective matches the entry's `from` ("any" = always), so a
    /// rolled hold_zone is never silently overwritten by a rolled survive.</summary>
    private static MapEventDef BuildTurn(PressureEntry chosen, string regionId, string tierKey, string currentKind)
    {
        string cur = string.IsNullOrEmpty(currentKind) ? "annihilate" : currentKind.ToLowerInvariant();
        string from = string.IsNullOrEmpty(chosen.From) ? "annihilate" : chosen.From.ToLowerInvariant();
        if (from != "any" && from != cur)
        {
            GD.Print($"[Roster] pressure for {tierKey} in '{regionId}': turn '{chosen.Id}' needs objective '{from}', fight has '{cur}'; none.");
            return null;
        }

        var raw = new Godot.Collections.Dictionary();
        raw["to"] = chosen.To ?? "survive";
        raw["rounds"] = chosen.Rounds;
        raw["zoneAnchor"] = chosen.ZoneAnchor ?? "midpoint";
        raw["zoneRadius"] = chosen.ZoneRadius;
        raw["breachLimit"] = chosen.BreachLimit;
        if (!string.IsNullOrEmpty(chosen.Description))
            raw["description"] = chosen.Description;
        if (!string.IsNullOrEmpty(chosen.Announce))
            raw["announce"] = chosen.Announce;
        if (!string.IsNullOrEmpty(chosen.Wake))
            raw["wake"] = chosen.Wake;

        var ev = new MapEventDef
        {
            Kind = "objective_turns",
            Id = string.IsNullOrEmpty(chosen.Id) ? "roster_turn" : chosen.Id,
            Round = Math.Max(1, chosen.Round),
            RepeatEvery = 0,
            Telegraph = Math.Max(1, chosen.Telegraph),
            When = chosen.When ?? "",
            Raw = raw,
        };
        GD.Print($"[Roster] pressure for {tierKey} in '{regionId}': turn '{ev.Id}' → {chosen.To}" +
                 $"{(chosen.Rounds > 0 ? $" for {chosen.Rounds} round(s)" : "")}, round {ev.Round}" +
                 $"{(string.IsNullOrEmpty(ev.When) ? "" : $" when {ev.When}")}.");
        return ev;
    }

    /// <summary>Draws `count` resolved unit ids from one random composition of the
    /// region's pool at `tierKey`, cycling through the composition when it is shorter
    /// than `count`. Empty when the region has no pool at that tier.</summary>
    private static List<string> DrawUnits(string regionId, string tierKey, int count)
    {
        var ids = new List<string>();
        var pool = EncounterPoolLoader.Load(regionId);
        if (pool == null || count <= 0)
            return ids;
        List<CompositionData> comps = tierKey switch
        {
            "skirmish" => pool.Skirmish,
            "battle" => pool.Battle,
            "siege" => pool.Siege,
            "ambush" => pool.Ambush,
            _ => pool.Battle,
        };
        if (comps == null || comps.Count == 0)
            return ids;
        var comp = comps[(int)(GD.Randi() % (uint)comps.Count)];
        var resolved = new List<string>();
        foreach (var slot in comp.Enemies)
            if (UnitRegistry.TryResolveId(slot.Archetype, out var unitId))
                resolved.Add(unitId);
        if (resolved.Count == 0)
            return ids;
        for (int i = 0; i < count; i++)
            ids.Add(resolved[i % resolved.Count]);
        return ids;
    }

    /// <summary>Records what the fight actually generated, for the history window.</summary>
    public static void RecordFight(string recipeId, string deploymentId)
    {
        if (!string.IsNullOrEmpty(recipeId))
        {
            _recipeHistory.Add(recipeId);
            while (_recipeHistory.Count > _historyWindow)
                _recipeHistory.RemoveAt(0);
        }
        _lastDeploymentId = deploymentId ?? "";
    }

    /// <summary>Test/debug: forget the global + region tables and the history.</summary>
    public static void Reload()
    {
        _globalLoaded = false;
        _global = null;
        _regionCache.Clear();
        _recipeHistory.Clear();
        _lastDeploymentId = "";
        EnsureGlobalLoaded();
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    private static string TierKey(EncounterTier tier) => tier.ToString().ToLowerInvariant();

    private static void EnsureGlobalLoaded()
    {
        if (_globalLoaded)
            return;
        _globalLoaded = true;
        _global = new Pools();

        using var fa = FileAccess.Open(GlobalPath, FileAccess.ModeFlags.Read);
        if (fa == null)
        {
            GD.PushWarning($"[Roster] Missing {GlobalPath}; every fight will use the terrain table and annihilate.");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(fa.GetAsText());
            var root = doc.RootElement;
            if (root.TryGetProperty("history_window", out var hw) && hw.TryGetInt32(out int w))
                _historyWindow = Math.Max(0, w);
            if (root.TryGetProperty("terrain_pools", out var tp))
                ParseTerrainPools(tp, _global);
            if (root.TryGetProperty("objective_pools", out var op))
                ParseObjectivePools(op, _global);
            if (root.TryGetProperty("pressure_pools", out var pp))
                ParsePressurePools(pp, _global);
            int n = 0;
            foreach (var t in _global.Recipes.Values)
                foreach (var l in t.Values)
                    n += l.Count;
            GD.Print($"[Roster] Loaded {n} recipe pool entries across {_global.Recipes.Count} tier(s), " +
                     $"{_global.Objectives.Count} objective tier(s), {_global.Pressure.Count} pressure tier(s), " +
                     $"history window {_historyWindow}.");
        }
        catch (Exception e)
        {
            GD.PushError($"[Roster] Parse error in {GlobalPath}: {e.Message}");
        }
    }

    /// <summary>The region's `battlefields` block, cached. Null when the region has none.</summary>
    private static Pools LoadRegion(string regionId)
    {
        if (string.IsNullOrEmpty(regionId))
            return null;
        if (_regionCache.TryGetValue(regionId, out var cached))
            return cached;

        Pools pools = null;
        string path = $"res://Data/Regions/{regionId}.json";
        if (FileAccess.FileExists(path))
        {
            try
            {
                using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                if (file != null)
                {
                    using var doc = JsonDocument.Parse(file.GetAsText());
                    if (doc.RootElement.TryGetProperty("battlefields", out var bf) &&
                        bf.ValueKind == JsonValueKind.Object)
                    {
                        pools = new Pools();
                        foreach (var tierProp in bf.EnumerateObject())
                        {
                            if (tierProp.NameEquals("objectives"))
                            {
                                ParseObjectivePools(tierProp.Value, pools);
                                continue;
                            }
                            if (tierProp.NameEquals("pressure"))
                            {
                                ParsePressurePools(tierProp.Value, pools);
                                continue;
                            }
                            if (tierProp.Value.ValueKind != JsonValueKind.Array)
                                continue;
                            var byTerrain = GetOrAdd(pools.Recipes, tierProp.Name);
                            foreach (var item in tierProp.Value.EnumerateArray())
                            {
                                var entry = ParseRecipeEntry(item, $"{regionId}/{tierProp.Name}");
                                if (entry == null)
                                    continue;
                                // Region entries are keyed by their own terrain list
                                // (or "*"), so Collect() can filter them like global ones.
                                if (entry.Terrains.Count == 0)
                                    GetOrAdd(byTerrain, AnyTerrain).Add(entry);
                                else
                                    foreach (var t in entry.Terrains)
                                        GetOrAdd(byTerrain, t).Add(entry);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                GD.PushError($"[Roster] Error reading battlefields block of {path}: {e.Message}");
            }
        }

        _regionCache[regionId] = pools;
        return pools;
    }

    private static void ParseTerrainPools(JsonElement tp, Pools into)
    {
        if (tp.ValueKind != JsonValueKind.Object)
            return;
        foreach (var tierProp in tp.EnumerateObject())
        {
            if (tierProp.Value.ValueKind != JsonValueKind.Object)
                continue;
            var byTerrain = GetOrAdd(into.Recipes, tierProp.Name);
            foreach (var terrainProp in tierProp.Value.EnumerateObject())
            {
                if (terrainProp.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var list = GetOrAdd(byTerrain, terrainProp.Name);
                foreach (var item in terrainProp.Value.EnumerateArray())
                {
                    var entry = ParseRecipeEntry(item, $"global/{tierProp.Name}/{terrainProp.Name}");
                    if (entry != null)
                        list.Add(entry);
                }
            }
        }
    }

    private static void ParseObjectivePools(JsonElement op, Pools into)
    {
        if (op.ValueKind != JsonValueKind.Object)
            return;
        foreach (var tierProp in op.EnumerateObject())
        {
            if (tierProp.Value.ValueKind != JsonValueKind.Array)
                continue;
            var list = GetOrAdd(into.Objectives, tierProp.Name);
            foreach (var item in tierProp.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var entry = new ObjectiveEntry
                {
                    Weight = ReadFloat(item, "weight", 1f),
                    Data = new ObjectiveData
                    {
                        Kind = ReadString(item, "kind", "annihilate"),
                        Rounds = ReadInt(item, "rounds", 0),
                        WardUnitId = ReadString(item, "wardUnitId", ""),
                        BreachLimit = ReadInt(item, "breachLimit", 2),
                        ZoneAnchor = ReadString(item, "zoneAnchor", "player_spawn"),
                        ZoneRadius = ReadInt(item, "zoneRadius", 2),
                        Description = ReadString(item, "description", ""),
                    }
                };
                list.Add(entry);
            }
        }
    }

    private static void ParsePressurePools(JsonElement pp, Pools into)
    {
        if (pp.ValueKind != JsonValueKind.Object)
            return;
        foreach (var tierProp in pp.EnumerateObject())
        {
            if (tierProp.Value.ValueKind != JsonValueKind.Array)
                continue;
            var list = GetOrAdd(into.Pressure, tierProp.Name);
            foreach (var item in tierProp.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var e = new PressureEntry
                {
                    Kind = ReadString(item, "kind", "none").Trim().ToLowerInvariant(),
                    Weight = ReadFloat(item, "weight", 1f),
                    Id = ReadString(item, "id", ""),
                    Round = ReadInt(item, "round", 3),
                    RepeatEvery = ReadInt(item, "repeat_every", 0),
                    Telegraph = ReadInt(item, "telegraph", 1),
                    When = ReadString(item, "when", ""),
                    At = ReadString(item, "at", "flank:3"),
                    Count = ReadInt(item, "count", 2),
                    Pool = ReadString(item, "pool", ""),
                    Announce = ReadString(item, "announce", ""),
                    Wake = ReadString(item, "wake", ""),
                };
                e.From = ReadString(item, "from", "annihilate");
                e.To = ReadString(item, "to", "survive");
                e.Rounds = ReadInt(item, "rounds", 0);
                e.ZoneAnchor = ReadString(item, "zoneAnchor", "midpoint");
                e.ZoneRadius = ReadInt(item, "zoneRadius", 2);
                e.BreachLimit = ReadInt(item, "breachLimit", 2);
                e.Description = ReadString(item, "description", "");
                if (item.TryGetProperty("lever", out var lv) && lv.ValueKind == JsonValueKind.Object)
                {
                    e.LeverAt = ReadString(lv, "at", "midpoint");
                    e.LeverMode = ReadString(lv, "mode", "hold");
                    e.LeverAmount = ReadInt(lv, "amount", 1);
                }
                if (e.Kind != "none" && e.Kind != "arrival" && e.Kind != "turn")
                {
                    GD.PushWarning($"[Roster] pressure entry '{e.Id}' ({tierProp.Name}): unknown kind '{e.Kind}'; treated as none.");
                    e.Kind = "none";
                }
                list.Add(e);
            }
        }
    }

    private static RecipeEntry ParseRecipeEntry(JsonElement item, string where)
    {
        if (item.ValueKind == JsonValueKind.String)
            return new RecipeEntry { Recipe = item.GetString() ?? "" };
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var e = new RecipeEntry
        {
            Recipe = ReadString(item, "recipe", ""),
            Weight = ReadFloat(item, "weight", 1f),
        };
        if (string.IsNullOrEmpty(e.Recipe))
        {
            GD.PushWarning($"[Roster] {where}: pool entry with no 'recipe'; skipped.");
            return null;
        }
        if (item.TryGetProperty("terrains", out var ts) && ts.ValueKind == JsonValueKind.Array)
            foreach (var t in ts.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String)
                    e.Terrains.Add(t.GetString());
        return e;
    }

    // ── Selection ───────────────────────────────────────────────────────────

    private static void Collect(Pools pools, string tierKey, string terrain, List<RecipeEntry> into)
    {
        if (pools == null || !pools.Recipes.TryGetValue(tierKey, out var byTerrain))
            return;
        if (!string.IsNullOrEmpty(terrain) && byTerrain.TryGetValue(terrain, out var exact))
            into.AddRange(exact);
        if (byTerrain.TryGetValue(AnyTerrain, out var any))
            into.AddRange(any);
    }

    private static string WeightedPick(List<RecipeEntry> pool)
    {
        float total = 0f;
        foreach (var e in pool)
            total += Mathf.Max(0f, e.Weight);
        if (total <= 0f)
            return "";
        float roll = GD.Randf() * total;
        foreach (var e in pool)
        {
            roll -= Mathf.Max(0f, e.Weight);
            if (roll <= 0f)
                return e.Recipe;
        }
        return pool[pool.Count - 1].Recipe;
    }

    // ── Small helpers ───────────────────────────────────────────────────────

    private static TValue GetOrAdd<TValue>(Dictionary<string, TValue> d, string key) where TValue : new()
    {
        if (!d.TryGetValue(key, out var v))
        {
            v = new TValue();
            d[key] = v;
        }
        return v;
    }

    private static string ReadString(JsonElement o, string key, string def) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;

    private static int ReadInt(JsonElement o, string key, int def) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : def;

    private static float ReadFloat(JsonElement o, string key, float def) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : def;
}
