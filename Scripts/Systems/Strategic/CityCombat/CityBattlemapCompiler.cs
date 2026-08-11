using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// ============================================================
// CityBattlemapCompiler.cs
//
// Purpose:        Read-only function over an ICityCombatSource that
//                 emits a battlefield recipe (MapRecipe-schema JSON)
//                 for one siege WINDOW. Increment 2 scope: the
//                 WallSiege gate-assault window. Geometry is a direct
//                 port of tools/city_compiler_proto.py (all asserts
//                 verified numerically 2026-08-11) — keep the two in
//                 lockstep when editing either.
// Layer:          System (strategic -> combat seam). Godot-free.
// Collaborators:  ICityCombatSource (input), MapRecipe/MapRecipeRegistry
//                 (schema this JSON targets), HexGridManager.Recipes
//                 (executes ops; `building_stamp` op lands in step 3)
// See:            docs/city_battlemap_compiler_spec_v1_1.md §3–§5
// Notes:          Direction indices follow HexDirection.All /
//                 HexGridManager.HexDirs (clockwise from east) — the
//                 rotational order is load-bearing: index arithmetic
//                 on Dirs IS angle arithmetic (face tangents = i±2).
//                 Walls emit per-tile at "chance": 1.0 — a gap-toothed
//                 city wall is a connectivity lie. Lanes are emitted
//                 BEFORE walls because CarveLane clears obstacles.
// ============================================================

/// <summary>One compiled siege window: the recipe JSON plus the spawn/objective
/// geometry the encounter wiring (build-order step 4) will need. Tile lists are
/// kept for debug dumps and the in-engine diff against the Python expectation.</summary>
public sealed class CityWindowResult
{
    public string RecipeId = "";
    public string RecipeJson = "";
    public (int q, int r) PlayerAnchor;
    public (int q, int r) EnemyAnchor;
    public List<(int q, int r)> GateGap = new();
    public List<(int q, int r)> WallTiles = new();
    public Dictionary<(int q, int r), string> StampTiles = new();
}

public static class CityBattlemapCompiler
{
    // ── Size classes (spec §4.1) ─────────────────────────────────────────────
    // Ratified as STARTING VALUES 2026-08-11; migrates into a per-blueprint
    // `combatStamp` field in Data/Buildings/*.json in the next authoring pass.
    private static readonly Dictionary<string, int> ClassRadius = new()
    {
        ["modest"] = 2,
        ["grand"] = 4,
        ["landmark"] = 6,
        ["seat"] = 8,
    };

    private static readonly Dictionary<string, string> BuildingClass = new()
    {
        ["grand_hall"] = "seat",
        ["sanctum"] = "grand",
        ["armory"] = "grand",
        ["dormitory"] = "grand",
        ["gatehouse_yard"] = "modest",   // a YARD: small gate structure, open ground
    };

    private const string DefaultClass = "modest";
    private const int EmptyLotRadius = 1;

    public const int DefaultMapRadius = 8;   // spec §3 envelope
    public const int StreetWidth = 2;        // min clear hexes between stamps
    public const int MaxLots = 7;

    // Clockwise from east — MUST match HexDirection.All / HexGridManager.HexDirs.
    private static readonly (int q, int r)[] Dirs =
    {
        (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1),
    };

    // ── Hex helpers ─────────────────────────────────────────────────────────

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

    private static int StampRadius(ICityCombatSource city, (int q, int r) lot)
    {
        var b = city.BuildingAt(lot.q, lot.r);
        if (b == null)
            return EmptyLotRadius;
        string cls = BuildingClass.TryGetValue(b.BlueprintId, out string c) ? c : DefaultClass;
        return ClassRadius[cls];
    }

    private static List<(int q, int r)> MissingDirs(ICityCombatSource city, (int q, int r) lot)
    {
        var outp = new List<(int q, int r)>();
        foreach (var d in Dirs)
            if (city.KindOf(lot.q + d.q, lot.r + d.r) == CityCellKind.Locked)
                outp.Add(d);
        return outp;
    }

    // ── Window extraction + layout (proto: extract_window / layout) ─────────

    private static List<(int q, int r)> ExtractWindow(ICityCombatSource city, (int q, int r) focus)
    {
        var admitted = new List<(int q, int r)> { focus };
        var seen = new HashSet<(int q, int r)> { focus };
        var frontier = new List<(int q, int r)> { focus };

        while (frontier.Count > 0 && admitted.Count < MaxLots)
        {
            var next = new List<(int q, int r)>();
            foreach (var cell in frontier)
            {
                foreach (var d in Dirs)
                {
                    var n = (q: cell.q + d.q, r: cell.r + d.r);
                    if (city.KindOf(n.q, n.r) == CityCellKind.Locked || !seen.Add(n))
                        continue;
                    admitted.Add(n);
                    next.Add(n);
                    if (admitted.Count >= MaxLots)
                        break;
                }
                if (admitted.Count >= MaxLots)
                    break;
            }
            frontier = next;
        }
        return admitted;
    }

    private static (Dictionary<(int q, int r), (int q, int r)> pos,
                    Dictionary<(int q, int r), (int q, int r)?> parent)
        Layout(ICityCombatSource city, List<(int q, int r)> admitted, (int q, int r) focus)
    {
        var pos = new Dictionary<(int q, int r), (int q, int r)> { [focus] = (0, 0) };
        var parent = new Dictionary<(int q, int r), (int q, int r)?> { [focus] = null };

        foreach (var lot in admitted.OrderBy(c => HexDist(c, focus)))
        {
            if (lot == focus)
                continue;

            // parent: the already-placed lattice neighbor closest to the focus
            (int q, int r)? bestPar = null;
            (int q, int r) bestDir = default;
            foreach (var d in Dirs)
            {
                var cand = (q: lot.q - d.q, r: lot.r - d.r);
                if (!pos.ContainsKey(cand))
                    continue;
                if (bestPar == null || HexDist(cand, focus) < HexDist(bestPar.Value, focus))
                {
                    bestPar = cand;
                    bestDir = d;
                }
            }
            if (bestPar == null)
                continue;   // isolated admission — cannot place, drop silently

            var par = bestPar.Value;
            int pitch = StampRadius(city, par) + StampRadius(city, lot) + StreetWidth + 1;
            pos[lot] = (pos[par].q + bestDir.q * pitch, pos[par].r + bestDir.r * pitch);
            parent[lot] = par;
        }
        return (pos, parent);
    }

    // ── The gate-assault window (proto: compile_gate_window) ────────────────

    /// <summary>Compiles the WallSiege gate-assault window. Throws if the city
    /// has no gate lot on the perimeter — availability is diegetic, and callers
    /// should have checked <see cref="ICityCombatSource.GateCell"/> first.</summary>
    public static CityWindowResult CompileGateAssault(
        ICityCombatSource city, ulong seed, int mapRadius = DefaultMapRadius)
    {
        if (city.GateCell == null)
            throw new InvalidOperationException("[CityCompiler] city has no gate lot.");
        var gate = city.GateCell.Value;

        var admitted = ExtractWindow(city, gate);
        var (pos, parent) = Layout(city, admitted, gate);

        // windowing rule: a lot placed outside the arena is not in this window
        admitted = admitted
            .Where(l => pos.ContainsKey(l) && HexDist(pos[l], (0, 0)) <= mapRadius)
            .ToList();

        var gateMissing = MissingDirs(city, gate);
        if (gateMissing.Count == 0)
            throw new InvalidOperationException("[CityCompiler] gate lot is not on the city perimeter.");
        var dOut = gateMissing[0];

        var arena = new HashSet<(int q, int r)>(Disk((0, 0), mapRadius));
        var result = new CityWindowResult();

        // stamps (clip at arena edge — buildings may continue into the backdrop)
        foreach (var lot in admitted)
        {
            var b = city.BuildingAt(lot.q, lot.r);
            if (b == null)
                continue;
            foreach (var t in Disk(pos[lot], StampRadius(city, lot)))
                if (arena.Contains(t))
                    result.StampTiles[t] = b.BlueprintId;
        }

        // Wall v3 — region-boundary curtain (port of city_compiler_proto.py,
        // asserts passed 2026-08-11). City region = union of disks
        // (stamp radius + 2) over admitted lots; +2 guarantees adjacent lots'
        // disks overlap (pitch = rA+rB+3 < rA+rB+4), so the region is one blob
        // and its outer boundary is a CLOSED, 1-thick contour by construction —
        // the curtain, clipped at the arena edge. Walls stand ~2 off stamps
        // (patrol alley inside the wall). The gate gap = the 2 boundary tiles
        // nearest the outward ray from the gate lot. Sealing is asserted in the
        // proto; keep both files in lockstep.
        int gateR = StampRadius(city, gate);
        var gateOuter = (q: pos[gate].q + dOut.q * (gateR + 1),
                         r: pos[gate].r + dOut.r * (gateR + 1));

        var region = new HashSet<(int q, int r)>();
        foreach (var lot in admitted)
            foreach (var t in Disk(pos[lot], StampRadius(city, lot) + 2))
                region.Add(t);

        var boundary = new HashSet<(int q, int r)>();
        foreach (var t in region)
        {
            foreach (var d in Dirs)
            {
                var n = (q: t.q + d.q, r: t.r + d.r);
                if (!region.Contains(n) && arena.Contains(n))
                    boundary.Add(n);
            }
        }

        // gate gap: the 2 boundary tiles nearest the outward ray (cartesian
        // projection matching AxialToWorld: x = 1.5q, y = sqrt3 * (r + q/2))
        (double x, double y) Cart((int q, int r) t) =>
            (1.5 * t.q, Math.Sqrt(3.0) * (t.r + t.q / 2.0));
        var g0 = Cart(pos[gate]);
        var g1 = Cart((pos[gate].q + dOut.q, pos[gate].r + dOut.r));
        double nxv = g1.x - g0.x, nyv = g1.y - g0.y;
        double nlen = Math.Sqrt(nxv * nxv + nyv * nyv);
        nxv /= nlen; nyv /= nlen;
        (double across, double negAlong) RayKey((int q, int r) t)
        {
            var c = Cart(t);
            double px = c.x - g0.x, py = c.y - g0.y;
            double along = px * nxv + py * nyv;
            double across = Math.Abs(-px * nyv + py * nxv);
            return along > 0 ? (across, -along) : (1e9, 0);
        }
        var gap = new HashSet<(int q, int r)>(boundary
            .OrderBy(t => RayKey(t).across)
            .ThenBy(t => RayKey(t).negAlong)
            .ThenBy(t => t.q).ThenBy(t => t.r)
            .Take(2));
        result.GateGap = gap.ToList();

        foreach (var t in boundary)
            if (!gap.Contains(t))
                result.WallTiles.Add(t);

        // anchors: attacker beyond the gate; defender at the deepest interior lot
        result.PlayerAnchor = (gateOuter.q + dOut.q * 3, gateOuter.r + dOut.r * 3);
        var interior = admitted
            .Where(l => l != gate && MissingDirs(city, l).Count == 0)
            .ToList();
        var enemyLot = interior.Count > 0 ? interior[0] : admitted.First(l => l != gate);
        result.EnemyAnchor = pos[enemyLot];

        // ── emit recipe JSON ────────────────────────────────────────────────
        var features = new JsonArray();

        // ground: plaza clearings / lawn patches on empty lots
        foreach (var lot in admitted)
        {
            if (city.BuildingAt(lot.q, lot.r) != null)
                continue;
            string kind = city.KindOf(lot.q, lot.r) == CityCellKind.Plaza ? "clearing" : "patch";
            var op = new JsonObject
            {
                ["feature"] = kind,
                ["phase"] = "skeleton",
                ["at"] = new JsonArray(pos[lot].q, pos[lot].r),
                ["radius"] = EmptyLotRadius + 1,
            };
            if (kind == "patch")
                op["terrain"] = "grass";
            features.Add(op);
        }

        // lanes BEFORE walls (CarveLane clears obstacles on its path)
        foreach (var lot in admitted)
        {
            if (parent[lot] == null)
                continue;
            features.Add(new JsonObject
            {
                ["feature"] = "carve_lane",
                ["phase"] = "skeleton",
                ["from"] = new JsonArray(pos[lot].q, pos[lot].r),
                ["to"] = new JsonArray(pos[parent[lot].Value].q, pos[parent[lot].Value].r),
                ["width"] = 1,
            });
        }
        features.Add(new JsonObject
        {
            ["feature"] = "carve_lane",
            ["phase"] = "skeleton",
            ["from"] = new JsonArray(result.PlayerAnchor.q, result.PlayerAnchor.r),
            ["to"] = new JsonArray(pos[gate].q, pos[gate].r),
            ["width"] = 1,
        });

        // walls: the contour is snake-shaped, so emit PER-TILE ops rather than
        // direction runs — filled_radius at radius 0 paints exactly one tile
        // (verified: dist-0 hit, edge roll defeated by chance 1.0). ~50 ops is
        // trivial; exactness beats compression here.
        foreach (var t in result.WallTiles)
        {
            features.Add(new JsonObject
            {
                ["feature"] = "filled_radius",
                ["phase"] = "skeleton",
                ["at"] = new JsonArray(t.q, t.r),
                ["radius"] = 0,
                ["obstacle_kind"] = "wall",
                ["chance"] = 1.0,
            });
        }

        // building stamps LAST — the step-3 `building_stamp` paint overwrites any
        // band tile that crossed a footprint, and explicitly restores
        // IsWalkable = true (the docx §4a interiors-forward-compat rule)
        foreach (var lot in admitted)
        {
            var b = city.BuildingAt(lot.q, lot.r);
            if (b == null)
                continue;
            features.Add(new JsonObject
            {
                ["feature"] = "building_stamp",
                ["phase"] = "skeleton",
                ["at"] = new JsonArray(pos[lot].q, pos[lot].r),
                ["radius"] = StampRadius(city, lot),
                ["building_id"] = b.BlueprintId,
                ["rotation"] = b.Rotation,
            });
        }

        result.RecipeId = $"city_wallsiege_gate_{seed:x8}";
        var recipe = new JsonObject
        {
            ["id"] = result.RecipeId,
            ["display_name"] = "The Gate",
            ["shape"] = new JsonObject { ["type"] = "hexagon", ["radius"] = mapRadius },
            ["base_terrain"] = new JsonObject
            {
                ["elevation_frequency"] = 0.08,
                ["moisture_frequency"] = 0.08,
                ["detail_weight"] = 0.15,
                ["max_height_step"] = 1,
                ["palette"] = new JsonArray(new JsonObject { ["terrain"] = "grass" }),
            },
            ["features"] = features,
            // Unknown keys are ignored by MapRecipe.FromDict — this block carries
            // the spawn/objective geometry for the step-4 encounter wiring.
            ["siege"] = new JsonObject
            {
                ["vector"] = "WallSiege",
                ["entry"] = "gate",
                ["player_anchor"] = new JsonArray(result.PlayerAnchor.q, result.PlayerAnchor.r),
                ["enemy_anchor"] = new JsonArray(result.EnemyAnchor.q, result.EnemyAnchor.r),
                ["gate_gap"] = new JsonArray(
                    result.GateGap.Select(t => (JsonNode)new JsonArray(t.q, t.r)).ToArray()),
            },
        };

        result.RecipeJson = recipe.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return result;
    }
}
