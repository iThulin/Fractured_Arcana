using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// ============================================================
// CastleDefenseCompiler.cs
//
// Purpose:        Compiles the "Defend the Castle" battlefield
//                 (castle_defense_v2, mobile fortress spec F6). The
//                 walking castle is a BODY on one rim of the map, not a
//                 walled yard: a stone platform one step above the ground
//                 around the Castle Heart, ringed by a waist-high iron
//                 bulwark at its edge, with one gate at ground level. The
//                 field ring around the platform is flattened so the step
//                 is exactly one everywhere; the bulwark makes the gate the
//                 only way up, so the gate is the fight. Past the
//                 rim the hull continues as off-map sections, two legs,
//                 and chimneys, so what stands on the map reads as the
//                 flank of something huge. Station tiles sit on the deck's
//                 outer ring; two smoke stacks on the deck break sight.
//                 The castle also ACTS: the compiler authors map events
//                 (leg stomps, a furnace vent onto the gangway foot, a
//                 hull lurch) so the body moves during the fight.
//                 Emits a MapRecipe JSON in the same shape the city
//                 compiler does, so HexGridManager, the objective zone,
//                 the backdrop, and MapEvents all consume it unchanged.
// Layer:          Systems / Combat / Terrain
// Collaborators:  MapRecipe (siege block, backdrop stamps), CombatManager
//                 .CastleDefense (Heart, stations, wizard arrival, Heart
//                 pulse), CombatManager.MapEvents (stomp, shift ring,
//                 imbue_patch), CastleModules (stations)
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
    public const int DeckRadius = 2;          // Heart + rings 1..2 = the deck
    public const int EdgeRadius = 3;          // bulwark ring at the deck's edge
    public const int SkirtRadius = 4;         // the field ring around the edge, flattened to 0
    public const int DeckHeight = 1;          // one step above the flattened skirt
    public const float DeckLift = DeckHeight * 0.6f;   // HexTile.HeightStep, world Y of the deck line

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

        var deck = Disk(heart, DeckRadius).Where(arena.Contains).ToList();
        var edge = Disk(heart, EdgeRadius).Where(t => HexDist(t, heart) == EdgeRadius && arena.Contains(t)).ToList();
        var outerRing = deck.Where(t => HexDist(t, heart) == DeckRadius).ToList();

        // Gate: the edge tile furthest toward +X (the field), at ground level, and
        // the skirt tile in front of it (the gate foot). The deck is one step up
        // from the gate, so the gate is the only way onto it that is not a wall.
        var gangTop = edge.OrderByDescending(WorldX).ThenBy(t => Math.Abs(WorldZ(t) - WorldZ(heart))).First();
        var dirOut = Dirs
            .Where(d => HexDist((gangTop.q + d.q, gangTop.r + d.r), heart) == EdgeRadius + 1)
            .OrderByDescending(d => WorldX((gangTop.q + d.q, gangTop.r + d.r)))
            .First();
        var gangA = (q: gangTop.q + dirOut.q, r: gangTop.r + dirOut.r);
        if (!arena.Contains(gangA)) gangA = gangTop;
        var gangFoot = gangA;
        var gangway = new List<(int q, int r)> { gangTop, gangA };
        result.GateGap = gangway;
        // The skirt: the field ring just outside the edge, flattened to ground so
        // the deck is exactly one step above it everywhere.
        var skirt = Disk(heart, SkirtRadius).Where(t => arena.Contains(t) && HexDist(t, heart) == SkirtRadius).ToList();

        // The bulwark: every edge tile but the gangway top.
        var bulwark = edge.Where(t => t != gangTop).ToList();
        result.WallTiles = bulwark;
        result.RampartTiles = outerRing;
        result.Courtyard = deck;

        // Anchors: the crew musters on the deck by the gangway; the enemy comes
        // from the far rim.
        var inside = outerRing.Where(t => HexDist(t, gangTop) == 1).OrderBy(t => HexDist(t, heart)).FirstOrDefault();
        result.PlayerAnchor = deck.OrderByDescending(WorldX).First();
        result.EnemyAnchor = arena.OrderByDescending(WorldX).ThenBy(t => Math.Abs(WorldZ(t))).First();

        // Two smoke stacks on the deck's outer ring, one per flank, never beside
        // the gangway. They break sight across the deck.
        var stackCandidates = outerRing.Where(t => t != inside && HexDist(t, gangTop) > 1).ToList();
        var stacks = new List<(int q, int r)>();
        if (stackCandidates.Count >= 2)
        {
            stacks.Add(stackCandidates.OrderBy(WorldZ).First());
            stacks.Add(stackCandidates.OrderByDescending(WorldZ).First());
        }

        // Stations: outer-ring deck tiles, nearest the gangway first, sides
        // interleaved so two stations do not stack on one flank.
        var stationOrder = outerRing
            .Where(t => t != inside && !stacks.Contains(t))
            .OrderBy(t => HexDist(t, gangTop))
            .ThenByDescending(WorldX)
            .ToList();
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

        // ── The body past the rim ─────────────────────────────────────────────
        // One ring of hull plates where the edge ring leaves the arena (kept low:
        // the camera sits behind this rim and must see over it), two legs further
        // out on each flank, and two chimneys behind the Heart.
        var hullOut = Disk(heart, EdgeRadius)
            .Where(t => !arena.Contains(t) && HexDist(t, heart) == EdgeRadius)
            .ToList();
        var legRing = Disk(heart, EdgeRadius + 3).Where(t => !arena.Contains(t) && HexDist(t, heart) == EdgeRadius + 3).ToList();
        var legs = new List<(int q, int r)>();
        if (legRing.Count >= 2)
        {
            legs.Add(legRing.OrderBy(WorldZ).First());
            legs.Add(legRing.OrderByDescending(WorldZ).First());
        }
        var chimneys = Disk(heart, EdgeRadius + 1)
            .Where(t => !arena.Contains(t) && HexDist(t, heart) == 2)
            .OrderBy(t => Math.Abs(WorldZ(t) - WorldZ(heart)))
            .Take(2)
            .ToList();

        // ── Where the castle acts on the field ────────────────────────────────
        // Leg stomps land on the field beside each flank; the furnace vents onto
        // the gangway foot; the hull lurch shoves whoever presses the deck edge.
        var fieldRing = Disk(heart, EdgeRadius + 2)
            .Where(t => arena.Contains(t) && HexDist(t, heart) == EdgeRadius + 2 && t != gangFoot)
            .ToList();
        var leftFoot = fieldRing.OrderBy(WorldZ).First();
        var rightFoot = fieldRing.OrderByDescending(WorldZ).First();

        // ── Features ──────────────────────────────────────────────────────────
        var features = new JsonArray();
        string terrain = FieldTerrain(overworldTerrain);

        // Approach lane from the far rim to the gangway foot, then the cover the field needs.
        features.Add(new JsonObject
        {
            ["feature"] = "carve_lane", ["phase"] = "skeleton",
            ["from"] = new JsonArray(result.EnemyAnchor.q, result.EnemyAnchor.r),
            ["to"] = new JsonArray(gangFoot.q, gangFoot.r),
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

        // The ground is made normally; the skirt ring is flattened to 0; the deck
        // and edge are a stone platform one step up; the gate and its foot stay at
        // ground level.
        foreach (var t in skirt)
            features.Add(Tile(t, "height", 0));
        foreach (var t in deck.Concat(edge))
        {
            features.Add(Tile(t, "terrain", "stone"));
            features.Add(Tile(t, "height", DeckHeight));
        }
        features.Add(Tile(gangTop, "height", 0));
        features.Add(Tile(gangA, "terrain", "stone"));
        features.Add(Tile(gangA, "height", 0));

        // Bulwark at the edge, stacks on the deck.
        foreach (var t in bulwark)
            features.Add(Tile(t, "obstacle_kind", "hull_bulwark"));
        foreach (var t in stacks)
            features.Add(Tile(t, "obstacle_kind", "smoke_stack"));

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

        // ── The castle's beats (map events) ───────────────────────────────────
        var events = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "stomp_left", ["kind"] = "stomp", ["round"] = 2, ["repeat_every"] = 4, ["telegraph"] = 1,
                ["at"] = $"{leftFoot.q},{leftFoot.r}", ["radius"] = 1, ["damage"] = 6,
                ["announce"] = "The castle shifts its weight: a leg comes down on the left flank.",
            },
            new JsonObject
            {
                ["id"] = "stomp_right", ["kind"] = "stomp", ["round"] = 4, ["repeat_every"] = 4, ["telegraph"] = 1,
                ["at"] = $"{rightFoot.q},{rightFoot.r}", ["radius"] = 1, ["damage"] = 6,
                ["announce"] = "The castle shifts its weight: a leg comes down on the right flank.",
            },
            new JsonObject
            {
                ["id"] = "furnace_vent", ["kind"] = "imbue_patch", ["round"] = 3, ["repeat_every"] = 3, ["telegraph"] = 1,
                ["at"] = $"{gangFoot.q},{gangFoot.r}", ["radius"] = 1, ["element"] = "fire",
                ["announce"] = "The furnace vents: fire rolls over the gangway foot.",
            },
            new JsonObject
            {
                ["id"] = "hull_lurch", ["kind"] = "shift", ["round"] = 5, ["repeat_every"] = 5, ["telegraph"] = 1,
                ["ring"] = "heart", ["radius"] = EdgeRadius + 1, ["tiles"] = 1, ["damage"] = 2,
                ["announce"] = "The hull lurches: everything pressed against it is thrown back.",
            },
        };

        // ── Recipe ────────────────────────────────────────────────────────────
        result.RecipeId = $"castle_defense_{terrain}_{seed:x8}";
        var recipe = new JsonObject
        {
            ["id"] = result.RecipeId,
            ["display_name"] = "The Gangway",
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
            // A siege is meant to be seen across: the deck overlooks the field by
            // design, so the visibility ceiling that protects open maps is off here.
            ["tactics"] = new JsonObject { ["max_visibility"] = 1.0, ["min_cover"] = 0.25 },
            ["features"] = features,
            ["map_events"] = events,
            ["siege"] = new JsonObject
            {
                ["vector"] = "CastleDefense",
                ["entry"] = "gangway",
                ["defending"] = true,
                ["player_anchor"] = new JsonArray(result.PlayerAnchor.q, result.PlayerAnchor.r),
                ["enemy_anchor"] = new JsonArray(result.EnemyAnchor.q, result.EnemyAnchor.r),
                ["gate_gap"] = new JsonArray(gangway.Select(t => (JsonNode)new JsonArray(t.q, t.r)).ToArray()),
                ["objective_zone"] = new JsonArray(deck.Append(gangTop).Select(t => (JsonNode)new JsonArray(t.q, t.r)).ToArray()),
                ["backdrop_stamps"] = new JsonArray(
                    hullOut.Select(t => (JsonNode)Stamp(t, "hull", $"hull_{t.q}_{t.r}", 0, 2.2f, DeckLift))
                    .Concat(legs.Select(t => (JsonNode)Stamp(t, "leg", $"leg_{t.q}_{t.r}", 0, 7f, DeckLift)))
                    .Concat(chimneys.Select(t => (JsonNode)Stamp(t, "stack", $"stack_{t.q}_{t.r}", 0, 6f, DeckLift)))
                    .ToArray()),
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
        GD.Print($"[CastleDefense] armed '{win.RecipeId}': deck={win.Courtyard.Count} bulwark={win.WallTiles.Count} " +
                 $"gangway={win.GateGap.Count} stations={win.Stations.Count} heart=({win.Heart.q},{win.Heart.r})");
        return true;
    }

    private static JsonObject Stamp((int q, int r) t, string kind, string id, int radius, float height, float lift) => new()
    {
        ["at"] = new JsonArray(t.q, t.r),
        ["kind"] = kind,
        ["id"] = id,
        ["radius"] = radius,
        ["height"] = height,
        ["lift"] = lift,
    };

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
