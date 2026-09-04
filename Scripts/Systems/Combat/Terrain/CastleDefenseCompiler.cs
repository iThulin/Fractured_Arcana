using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// ============================================================
// CastleDefenseCompiler.cs
//
// Purpose:        Compiles the "Defend the Castle" battlefield
//                 (castle_defense_v1, mobile fortress spec F6): the
//                 walking castle sits on one rim of the map as a two-deep
//                 half ring (outer wall, inner rampart) around a courtyard
//                 with the Castle Heart at the rim, a three-tile gate
//                 facing the field, station tiles on the rampart for the
//                 installed castle modules, and backdrop towers past the
//                 rim so the castle reads as continuing off the map. The
//                 field in front carries cover lines and an approach lane.
//                 Emits a MapRecipe JSON in the same shape the city
//                 compiler does, so HexGridManager, SiegeDoors, the
//                 objective zone, and the backdrop all consume it unchanged.
// Layer:          Systems / Combat / Terrain
// Collaborators:  MapRecipe (siege block), CombatManager.SiegeDoors (gate
//                 door units), CombatManager.CastleDefense (Heart,
//                 stations, wizard arrival), CastleModules (stations)
// See:            docs/castle_defense_v1.md
// ============================================================

public sealed class CastleWindowResult
{
    public string RecipeId = "";
    public string RecipeJson = "";
    public (int q, int r) Heart;
    public (int q, int r) PlayerAnchor;
    public (int q, int r) EnemyAnchor;
    public List<(int q, int r)> GateGap = new();
    public List<(int q, int r)> WallTiles = new();
    public List<(int q, int r)> RampartTiles = new();
    public List<(int q, int r)> Courtyard = new();
    public List<((int q, int r) at, string module)> Stations = new();
}

public static class CastleDefenseCompiler
{
    public const int DefaultMapRadius = 7;
    public const int CourtyardRadius = 1;     // Heart + ring 1 = courtyard floor
    public const int RampartRadius = 2;       // walkable, raised
    public const int WallRadius = 3;          // the curtain wall
    public const int RampartHeight = 2;

    // Clockwise from east. MUST match HexDirection.All / HexGridManager.HexDirs.
    private static readonly (int q, int r)[] Dirs =
    {
        (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1),
    };

    private static int HexDist((int q, int r) a, (int q, int r) b)
    {
        int dq = a.q - b.q, dr = a.r - b.r;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
    }

    private static IEnumerable<(int q, int r)> Disk((int q, int r) c, int radius)
    {
        for (int q = -radius; q <= radius; q++)
            for (int r = Math.Max(-radius, -q - radius); r <= Math.Min(radius, -q + radius); r++)
                yield return (c.q + q, c.r + r);
    }

    /// <summary>Flat-top world X of an axial coord (HexGridManager.AxialToWorld, R = 1).</summary>
    private static float WorldX((int q, int r) c) => 1.5f * c.q;
    private static float WorldZ((int q, int r) c) => 1.7320508f * (c.r + c.q / 2f);

    /// <summary>Overworld terrain name to the tile terrain the field is made of.</summary>
    private static string FieldTerrain(string overworldTerrain) => (overworldTerrain ?? "").ToLowerInvariant() switch
    {
        "forest" => "forest",
        "mountain" or "hills" => "stone",
        "snow" or "tundra" => "ice",
        "desert" => "sand",
        "swamp" or "marsh" => "grass",
        _ => "grass",
    };

    /// <summary>Compile the castle-defence window. <paramref name="modules"/> are the
    /// installed castle module ids in station order; each gets a rampart tile,
    /// gate flanks first. <paramref name="seed"/> only names the recipe: the
    /// geometry is deterministic, the field's base noise is seeded by the grid.</summary>
    public static CastleWindowResult Compile(string overworldTerrain, IReadOnlyList<string> modules,
                                             ulong seed, int mapRadius = DefaultMapRadius)
    {
        var result = new CastleWindowResult();
        int R = mapRadius;

        // The arena.
        var arena = new HashSet<(int q, int r)>(Disk((0, 0), R));

        // Keep centre on the -X rim, centre row (world Z ~ 0): q = -R + 1, r = -q / 2.
        var heart = (q: -R + 1, r: (R - 1) / 2);
        if (!arena.Contains(heart))
            heart = (q: -R + 1, r: (R - 1) / 2 + ((R - 1) % 2 == 0 ? 0 : -1));
        result.Heart = heart;

        var courtyard = Disk(heart, CourtyardRadius).Where(arena.Contains).ToList();
        var rampart = Disk(heart, RampartRadius).Where(t => HexDist(t, heart) == RampartRadius).ToList();
        var wallAll = Disk(heart, WallRadius).Where(t => HexDist(t, heart) == WallRadius).ToList();
        var wallIn = wallAll.Where(arena.Contains).ToList();
        var wallOut = wallAll.Where(t => !arena.Contains(t)).ToList();     // backdrop
        var rampartIn = rampart.Where(arena.Contains).ToList();

        // Gate: the three mutually adjacent wall tiles furthest toward +X (the field).
        var front = wallIn.OrderByDescending(WorldX).ThenBy(t => Math.Abs(WorldZ(t) - WorldZ(heart))).ToList();
        var gate = new List<(int q, int r)> { front[0] };
        foreach (var t in front.Skip(1))
        {
            if (gate.Count >= 3) break;
            if (gate.Any(g => HexDist(g, t) == 1))
                gate.Add(t);
        }
        result.GateGap = gate;
        result.WallTiles = wallIn.Where(t => !gate.Contains(t)).ToList();
        result.RampartTiles = rampartIn;
        result.Courtyard = courtyard;

        // Anchors: the player musters in the courtyard just inside the gate; the
        // enemy comes from the far rim.
        var gateCentre = gate.OrderBy(t => Math.Abs(WorldZ(t) - WorldZ(heart))).First();
        var inside = rampartIn.Where(t => gate.Any(g => HexDist(g, t) == 1)).OrderBy(t => HexDist(t, heart)).FirstOrDefault();
        result.PlayerAnchor = courtyard.OrderByDescending(WorldX).First();
        result.EnemyAnchor = arena.OrderByDescending(WorldX).ThenBy(t => Math.Abs(WorldZ(t))).First();

        // Stations: rampart tiles, gate flanks first (the towers), then spread
        // along the arc by distance from the gate.
        var stationOrder = rampartIn
            .Where(t => t != inside)
            .OrderBy(t => gate.Min(g => HexDist(g, t)))
            .ThenByDescending(WorldX)
            .ToList();
        // Interleave sides so two stations do not stack on the same flank.
        var left = stationOrder.Where(t => WorldZ(t) < WorldZ(heart)).ToList();
        var right = stationOrder.Where(t => WorldZ(t) >= WorldZ(heart)).ToList();
        var slots = new List<(int q, int r)>();
        for (int i = 0; slots.Count < stationOrder.Count; i++)
        {
            if (i < left.Count) slots.Add(left[i]);
            if (i < right.Count) slots.Add(right[i]);
        }
        for (int i = 0; i < modules.Count && i < slots.Count; i++)
            result.Stations.Add((slots[i], modules[i]));

        // ── Features ──────────────────────────────────────────────────────────
        var features = new JsonArray();
        string terrain = FieldTerrain(overworldTerrain);

        // Approach lane from the far rim to the gate, then the cover the field needs.
        features.Add(new JsonObject
        {
            ["feature"] = "carve_lane", ["phase"] = "skeleton",
            ["from"] = new JsonArray(result.EnemyAnchor.q, result.EnemyAnchor.r),
            ["to"] = new JsonArray(gateCentre.q, gateCentre.r),
            ["width"] = 0,
        });
        features.Add(new JsonObject
        {
            ["feature"] = "cover_line", ["phase"] = "skeleton",
            ["at"] = "axis:1", ["length"] = 5, ["kind"] = "low", ["gaps"] = 1, ["fill"] = 0.85,
        });
        features.Add(new JsonObject
        {
            ["feature"] = "cover_line", ["phase"] = "skeleton",
            ["at"] = "axis:4", ["length"] = 5, ["kind"] = "low", ["gaps"] = 2, ["fill"] = 0.8,
        });

        // Courtyard and rampart floors are paved stone; the rampart is raised.
        foreach (var t in courtyard.Concat(rampartIn).Concat(gate))
        {
            features.Add(Tile(t, "terrain", "stone"));
        }
        foreach (var t in rampartIn)
            features.Add(Tile(t, "height", RampartHeight));

        // The curtain wall.
        foreach (var t in result.WallTiles)
            features.Add(Tile(t, "obstacle_kind", "wall"));

        // Parapet: the wall's own tiles are High cover for anyone on the rampart
        // beside them, so no extra op is needed. The gate tiles stay open ground;
        // SiegeDoors fields the doors.

        // Field dressing: two rock clusters and a cask near the approach.
        features.Add(new JsonObject
        {
            ["feature"] = "obstacle_cluster", ["phase"] = "accent", ["at"] = "flank:3",
            ["kind"] = "high", ["size"] = 2, ["chance"] = 1.0,
        });
        features.Add(new JsonObject
        {
            ["feature"] = "obstacle_cluster", ["phase"] = "accent", ["at"] = "flank:-3",
            ["kind"] = "high", ["size"] = 2, ["chance"] = 1.0,
        });
        features.Add(new JsonObject
        {
            ["feature"] = "map_object", ["phase"] = "accent", ["at"] = "axis:2",
            ["kind"] = "powder_cask", ["count"] = 1,
        });

        // ── Recipe ────────────────────────────────────────────────────────────
        result.RecipeId = $"castle_defense_{terrain}_{seed:x8}";
        var recipe = new JsonObject
        {
            ["id"] = result.RecipeId,
            ["display_name"] = "The Castle Gate",
            ["shape"] = new JsonObject { ["type"] = "hexagon", ["radius"] = R },
            ["base_terrain"] = new JsonObject
            {
                ["elevation_frequency"] = 0.08,
                ["moisture_frequency"] = 0.08,
                ["detail_weight"] = 0.15,
                ["max_height_step"] = 1,
                ["min_height_step"] = 0,
                ["palette"] = new JsonArray(new JsonObject { ["terrain"] = terrain }),
            },
            ["tactics"] = new JsonObject { ["max_visibility"] = 0.5, ["min_cover"] = 0.25 },
            ["features"] = features,
            ["siege"] = new JsonObject
            {
                ["vector"] = "CastleDefense",
                ["entry"] = "gate",
                ["defending"] = true,
                ["player_anchor"] = new JsonArray(result.PlayerAnchor.q, result.PlayerAnchor.r),
                ["enemy_anchor"] = new JsonArray(result.EnemyAnchor.q, result.EnemyAnchor.r),
                ["gate_gap"] = new JsonArray(gate.Select(t => (JsonNode)new JsonArray(t.q, t.r)).ToArray()),
                ["objective_zone"] = new JsonArray(courtyard.Concat(rampartIn).Select(t => (JsonNode)new JsonArray(t.q, t.r)).ToArray()),
                ["backdrop_wall"] = new JsonArray(wallOut.Select(t => (JsonNode)new JsonArray(t.q, t.r)).ToArray()),
                ["heart"] = new JsonArray(heart.q, heart.r),
                ["stations"] = new JsonArray(result.Stations.Select(s => (JsonNode)new JsonObject
                {
                    ["at"] = new JsonArray(s.at.q, s.at.r),
                    ["module"] = s.module,
                }).ToArray()),
            },
        };

        result.RecipeJson = recipe.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return result;
    }

    /// <summary>One call for both callers (patrol ambush, debug launcher): compile
    /// the castle for this terrain and the guild's installed modules, register
    /// the recipe, point the encounter at it, attach the protect objective with
    /// the Castle Heart, and flag the next combat as a castle defence. Returns
    /// false (and leaves the def untouched) when the recipe JSON fails to parse.</summary>
    public static bool Arm(EncounterDefinition def, string overworldTerrain, ulong seed)
    {
        if (def == null)
            return false;
        var win = Compile(overworldTerrain, CastleModules.InstalledStationIds(), seed);
        var parsed = Json.ParseString(win.RecipeJson);
        var dict = parsed.AsGodotDictionary();
        if (dict == null || dict.Count == 0)
        {
            GD.PushWarning($"[CastleDefense] unparseable recipe JSON:\n{win.RecipeJson}");
            return false;
        }
        MapRecipeRegistry.Register(MapRecipe.FromDict(dict));
        def.MapRecipe = win.RecipeId;
        def.Objective = new CombatObjectiveDef
        {
            Kind = CombatObjectiveDef.KindProtect,
            WardUnitId = "castle_heart",
            Description = "Defend the castle. If the Heart breaks, the castle limps home.",
        };
        CombatManager.NextCombatIsCastleDefense = true;
        GD.Print($"[CastleDefense] armed '{win.RecipeId}': walls={win.WallTiles.Count} rampart={win.RampartTiles.Count} " +
                 $"gate={win.GateGap.Count} stations={win.Stations.Count} heart=({win.Heart.q},{win.Heart.r})");
        return true;
    }

    private static JsonObject Tile((int q, int r) t, string key, JsonNode value) => new()
    {
        ["feature"] = "filled_radius",
        ["phase"] = "skeleton",
        ["at"] = new JsonArray(t.q, t.r),
        ["radius"] = 0,
        [key] = value,
        ["chance"] = 1.0,
    };
}
