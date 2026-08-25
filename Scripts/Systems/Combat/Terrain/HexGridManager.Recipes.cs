using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Recipes.cs  (partial of HexGridManager)
//
// Purpose:        Executes JSON-authored map recipes. When MapRecipeId
//                 is set and resolves, the recipe drives shape, base
//                 terrain palette, features (by phase), and atmosphere,
//                 replacing the enum theme/layout path. The feature
//                 dispatch maps recipe feature names to the C# builders
//                 in HexGridManager.Features.cs.
// Layer:          System (generation)
// Collaborators:  MapRecipe / MapRecipeRegistry (data),
//                 HexGridManager.Features (builders), MapField (palette)
// ============================================================

public partial class HexGridManager : Node3D
{
    private MapRecipe _activeRecipe;

    /// <summary>Resolves MapRecipeId → recipe and copies its shape into the existing shape exports so GenerateBaseGrid is unchanged. Null recipe = enum path.</summary>
    private void ResolveRecipe()
    {
        _activeRecipe = null;

        if (string.IsNullOrEmpty(MapRecipeId))
            return;

        MapRecipeRegistry.EnsureLoaded();
        _activeRecipe = MapRecipeRegistry.Get(MapRecipeId);

        if (_activeRecipe == null)
        {
            GD.PushWarning($"[MapRecipe] '{MapRecipeId}' not found; falling back to enum theme/layout.");
            return;
        }

        if (_activeRecipe.Shape is ShapeSpec s)
        {
            Shape = s.Type;
            if (s.Width > 0) GridWidth = s.Width;
            if (s.Height > 0) GridHeight = s.Height;
            if (s.Radius > 0) MapRadius = s.Radius;
            BlobErosion = s.Erosion;
        }
    }

    /// <summary>Builds a MapField using the recipe's base-terrain params, falling back to MapField defaults where unset.</summary>
    private MapField BuildFieldFromRecipe(MapRecipe r)
    {
        int fieldSeed = (int)_rng.Randi();
        var f = new MapField(fieldSeed);

        if (r.BaseTerrain is BaseTerrainSpec b)
        {
            if (b.ElevationFrequency > 0f) f.ElevationFrequency = b.ElevationFrequency;
            if (b.MoistureFrequency > 0f) f.MoistureFrequency = b.MoistureFrequency;
            if (b.DetailWeight >= 0f) f.DetailWeight = b.DetailWeight;
            if (b.MaxHeightStep != 0) f.MaxHeightStep = b.MaxHeightStep;
            if (b.MinHeightStep != 0) f.MinHeightStep = b.MinHeightStep;
        }

        return f;
    }

    /// <summary>Runs every feature op tagged with the given phase ("skeleton" pre-spawn, "accent" post-spawn).</summary>
    // ── E4: map-event runtime operations ────────────────────────
    // ── E3: map objects the map_object op records for CombatManager to spawn post-gen ──
    public readonly System.Collections.Generic.List<(Vector2I coord, string kind, int count)> PendingMapObjects = new();

    private static readonly System.Collections.Generic.List<MapEventDef> _noMapEvents = new();
    /// <summary>The active recipe's scheduled map events (E4). Empty on the enum path.</summary>
    public System.Collections.Generic.IReadOnlyList<MapEventDef> ActiveMapEvents
    {
        get
        {
            var baseList = _activeRecipe?.MapEvents ?? _noMapEvents;
            var dbg = BuildDebugMapEvent();
            var wx = BuildWeatherMapEvent();
            if (dbg == null && wx == null)
                return baseList;
            var merged = new System.Collections.Generic.List<MapEventDef>(baseList);
            if (dbg != null) merged.Add(dbg);
            if (wx != null) merged.Add(wx);
            return merged;
        }
    }

    /// <summary>Mobile Fortress W3: when the sortie deployed into a fight under
    /// weather, the battlefield inherits a matching recurring weather_tick hazard
    /// (storm=lightning, snow=ice, rain=rising water). Reads the weather the
    /// overworld stashed on the router; null when the fight had no weather (or is
    /// a non-overworld combat, which clears SavedWeather on finish). Fires round 2,
    /// telegraphed 1 ahead, then every 3 rounds: present but not overwhelming.</summary>
    private static MapEventDef BuildWeatherMapEvent()
    {
        var router = EncounterRouter.Instance;
        if (router == null)
            return null;
        string param = WeatherCatalog.Def(router.SavedWeather).CombatHazard;
        if (string.IsNullOrEmpty(param))
            return null;

        var raw = new Godot.Collections.Dictionary();
        raw["weather"] = param;
        raw["per_patch"] = param == "snow" ? 2 : 1;
        raw["announce"] = $"the {WeatherCatalog.Name(router.SavedWeather).ToLower()} reaches the field";
        return new MapEventDef { Kind = "weather_tick", Round = 2, Telegraph = 1, RepeatEvery = 3, Raw = raw };
    }

    /// <summary>CombatDebugLauncher hook: when PlayerSession.DebugMapEventKind is set,
    /// synthesize a recurring MapEventDef so E4 events can be watched on any map. Fires
    /// round 2, telegraphed 1 ahead, then every 2 rounds. Null when no debug kind set.</summary>
    private static MapEventDef BuildDebugMapEvent()
    {
        string kind = PlayerSession.DebugMapEventKind;
        if (string.IsNullOrEmpty(kind))
            return null;
        var raw = new Godot.Collections.Dictionary();
        raw["element"] = PlayerSession.DebugMapEventElement ?? "fire";
        raw["at"] = "midpoint";
        raw["radius"] = kind.EndsWith("_tiles") ? 1 : 4;   // collapse/raise/lower stay small
        raw["into"] = "rubble";                             // debug collapse -> difficult terrain, not water
        raw["steps"] = 1;
        raw["per_patch"] = 1;
        raw["announce"] = $"[debug] {kind}";
        return new MapEventDef { Kind = kind, Round = 2, Telegraph = 1, RepeatEvery = 2, Raw = raw };
    }

    /// <summary>Midpoint of the spawn anchors, the contested centre most events aim at.</summary>
    public Vector2I RecipeMidpoint => GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);
    public Vector2I RecipeCenter => _centerCoord;

    private static bool EventWritable(TileData t) =>
        t != null && t.IsWalkable && !t.IsBlocked && t.TerrainType != TileTerrainType.Water;

    /// <summary>imbue_patch: write `element` onto every writable tile within `radius` of `center`.</summary>
    public int MapEventImbuePatch(Vector2I center, int radius, TileElementType element)
    {
        int n = 0;
        foreach (var kv in Tiles)
        {
            if (Distance(center, kv.Key) > radius) continue;
            if (!EventWritable(kv.Value)) continue;
            TileEntryReactions.ImbueTile(kv.Value, element);
            n++;
        }
        return n;
    }

    /// <summary>advance_hazard_ring: imbue the hex ring at exactly `ringRadius` from `center`.</summary>
    public int MapEventImbueRing(Vector2I center, int ringRadius, TileElementType element)
    {
        int n = 0;
        foreach (var kv in Tiles)
        {
            if (Distance(center, kv.Key) != ringRadius) continue;
            if (!EventWritable(kv.Value)) continue;
            TileEntryReactions.ImbueTile(kv.Value, element);
            n++;
        }
        return n;
    }

    /// <summary>spread_element: each existing `element` tile spreads to up to `perPatch`
    /// adjacent writable non-element tiles. Deterministic (lowest axial first), with no
    /// boundary RNG, so replays and saves stay honest. Targets are collected before any
    /// write so a tile imbued this tick can't seed further spread until next tick.</summary>
    public int MapEventSpreadElement(TileElementType element, int perPatch)
    {
        var targets = new System.Collections.Generic.HashSet<Vector2I>();
        foreach (var kv in Tiles)
        {
            if (kv.Value?.ElementType != element) continue;
            int added = 0;
            foreach (var nb in GetNeighbors(kv.Key))
            {
                if (added >= perPatch) break;
                var t = GetTile(nb);
                if (!EventWritable(t) || t.ElementType == element) continue;
                if (targets.Add(nb)) added++;
            }
        }
        var ordered = new System.Collections.Generic.List<Vector2I>(targets);
        ordered.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        foreach (var c in ordered) TileEntryReactions.ImbueTile(GetTile(c), element);
        return ordered.Count;
    }

    private void RunRecipeFeatures(MapRecipe r, string phase)
    {
        // E3: reset the placement list at the first phase of each (re)generation.
        if (phase == "skeleton")
            PendingMapObjects.Clear();
        if (r?.Features == null)
            return;

        foreach (var op in r.Features)
        {
            string p = string.IsNullOrEmpty(op.Phase) ? "accent" : op.Phase;
            if (p != phase)
                continue;

            if (op.Chance < 1f && _rng.Randf() > op.Chance)
                continue;

            ExecuteFeature(op);
        }
    }

    /// <summary>Maps a recipe feature name to a builder call, resolving its parameters.</summary>
    private void ExecuteFeature(FeatureOp op)
    {
        switch (op.Feature)
        {
            case "lake":
                CarveLake(CoordFromOp(op, "at", _centerCoord), Roll(op, "radius", 2, 3), Roll(op, "depth", 1, 2));
                break;

            case "river":
                CarveRiver(CoordFromOp(op, "from", PickHighTile()), Roll(op, "length", 10, 14), Roll(op, "width", 0, 1));
                break;

            case "stream":
                CarveStream(CoordFromOp(op, "from", PickHighTile()), Roll(op, "length", 6, 9));
                break;

            case "crevice":
                CarveCrevice(CoordFromOp(op, "at", GetRandomCoord()), ResolveDir(op), Roll(op, "length", 4, 6), Roll(op, "depth", 3, 4));
                break;

            case "mountainside":
                RaiseMountainside(Roll(op, "peak", 3, 4));
                break;

            case "meadow":
                PlantMeadow(CoordFromOp(op, "at", _centerCoord), Roll(op, "radius", 2, 3));
                break;

            case "clearing":
                CarveClearing(CoordFromOp(op, "at", _centerCoord), Roll(op, "radius", 2, 2));
                break;

            case "scatter_copses":
                ScatterCopses(Roll(op, "count", 2, 3), Roll(op, "radius", 1, 2));
                break;

            case "rocky_outcrop":
                RockyOutcrop(CoordFromOp(op, "at", GetRandomCoord()), Roll(op, "radius", 1, 2));
                break;

            case "obstacle_cluster":
                PaintObstacleCluster(CoordFromOp(op, "at", GetRandomCoord()), op.GetStr("kind", "rock"), Roll(op, "size", 2, 3));
                break;

            case "height_hill":
                PaintHeightHill(CoordFromOp(op, "at", _centerCoord), Roll(op, "radius", 2, 2), Roll(op, "peak", 2, 2));
                break;

            case "height_basin":
                PaintHeightBasin(CoordFromOp(op, "at", _centerCoord), Roll(op, "radius", 2, 2), Roll(op, "depth", 2, 2));
                break;

            case "carve_lane":
                CarveLane(CoordFromOp(op, "from", PlayerLayoutAnchor), CoordFromOp(op, "to", EnemyLayoutAnchor), Roll(op, "width", 0, 1));
                break;

            case "patch":
                PaintOrganicPatch(CoordFromOp(op, "at", GetRandomCoord()), MapRecipe.ParseTerrain(op.GetStr("terrain", "grass")), Roll(op, "radius", 2, 3));
                break;

            case "element_patch":
                PaintElementPatch(CoordFromOp(op, "at", _centerCoord), MapRecipe.ParseElement(op.GetStr("element", "arcane")), Roll(op, "radius", 1, 2), op.GetFloat("strength", 1f));
                break;

            case "leyline":
                PaintLeyline(CoordFromOp(op, "at", GetRandomCoord()), ResolveDir(op), Roll(op, "length", 6, 10), op.GetFloat("branch", 0.35f));
                break;

            case "obstacle_band":
                PaintObstacleBand(CoordFromOp(op, "at", _centerCoord), ResolveDir(op), Roll(op, "length", 4, 6), op.GetStr("kind", "rock"), op.GetFloat("chance", 0.7f));
                break;

            case "height_ridge":
                PaintHeightRidge(CoordFromOp(op, "at", _centerCoord), ResolveDir(op), Roll(op, "length", 5, 7), Roll(op, "height", 1, 2));
                break;

            case "ring":
                PaintRingFeature(CoordFromOp(op, "at", _centerCoord), Roll(op, "radius", 2, 3), RecipeTileApplier(op), op.GetFloat("chance", 1f));
                break;

            case "filled_radius":
                PaintFilledRadius(CoordFromOp(op, "at", _centerCoord), Roll(op, "radius", 2, 3), RecipeTileApplier(op), op.GetFloat("chance", 1f));
                break;

            case "map_object":
                PendingMapObjects.Add((CoordFromOp(op, "at", _centerCoord), op.GetStr("kind", ""), op.GetInt("count", 1)));
                break;

            case "building_stamp":
                // City siege shells (CityBattlemapCompiler). MUST run after any
                // wall obstacle_band ops, because the stamp restores IsWalkable on
                // footprint tiles a band crossed (see HexGridManager.CityStamps).
                PaintBuildingStamp(CoordFromOp(op, "at", _centerCoord), op.GetInt("radius", 2),
                    op.GetStr("building_id", "unknown"), op.GetInt("height", 0));
                break;

            default:
                GD.PushWarning($"[MapRecipe] Unknown feature '{op.Feature}'.");
                break;
        }
    }

    private void ApplyRecipeAtmosphere(AtmosphereSpec a)
    {
        if (ThemeSun != null)
        {
            ThemeSun.LightColor = a.Sun;
            ThemeSun.LightEnergy = a.SunEnergy;
        }

        if (ThemeWorldEnvironment?.Environment is Godot.Environment env)
        {
            env.AmbientLightColor = a.Ambient;
            env.AmbientLightEnergy = a.AmbientEnergy;
            env.FogEnabled = true;
            env.FogLightColor = a.Fog;
            env.FogDensity = a.FogDensity;
        }

        // Outer vista rings + the backdrop plane melt toward this fog colour
        // (see HexGridManager.Vista.cs).
        ApplyHorizon(a.Fog);
    }

    // ── Param resolvers ─────────────────────────────────────────────────────

    private int Roll(FeatureOp op, string key, int defMin, int defMax)
    {
        var (a, b) = op.GetIntRange(key, defMin, defMax);
        return _rng.RandiRange(Math.Min(a, b), Math.Max(a, b));
    }

    private Vector2I CoordFromOp(FeatureOp op, string key, Vector2I fallback) =>
        op.Has(key) ? ResolveCoord(op.GetVariant(key)) : fallback;

    /// <summary>E2.3: midpoint of the anchors, shifted `n` tiles along (or perpendicular
    /// to) the player->enemy axis. Backs the axis:N / flank:N coord tokens.</summary>
    private Vector2I AxisShift(int n, bool perpendicular)
    {
        var mid = GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor);
        int di = HexDirection.Pick(PlayerLayoutAnchor, EnemyLayoutAnchor, 6);
        if (perpendicular) di = (di + 2) % 6;
        return mid + HexDirs[di] * n;
    }

    private Vector2I ResolveCoord(Variant spec)
    {
        if (spec.VariantType == Variant.Type.Array)
        {
            var a = spec.AsGodotArray();
            if (a.Count >= 2)
                return new Vector2I(a[0].AsInt32(), a[1].AsInt32());
        }

        string s = spec.VariantType == Variant.Type.String ? spec.AsString() : "center";

        // E2.3: axis:N shifts the midpoint N tiles along the player->enemy axis
        // (negative = toward the player); flank:N shifts it N tiles perpendicular.
        if (s.StartsWith("axis:") && int.TryParse(s.Substring(5), out int _an))
            return AxisShift(_an, perpendicular: false);
        if (s.StartsWith("flank:") && int.TryParse(s.Substring(6), out int _fn))
            return AxisShift(_fn, perpendicular: true);

        return s switch
        {
            "center" => _centerCoord,
            "midpoint" => GetMidpoint(PlayerLayoutAnchor, EnemyLayoutAnchor),
            "random" => GetRandomCoord(),
            "high_tile" => PickHighTile(),
            "low_tile" => PickLowTile(),
            "player_anchor" => PlayerLayoutAnchor,
            "enemy_anchor" => EnemyLayoutAnchor,
            _ => _centerCoord
        };
    }

    /// <summary>E2.2: builds the per-tile writer for `ring` / `filled_radius` from the
    /// op's payload: element, obstacle_kind (a LoS-blocking wall/rock), terrain, or
    /// height. First key present wins.</summary>
    private System.Action<TileData> RecipeTileApplier(FeatureOp op)
    {
        if (op.Has("element"))
        {
            var el = MapRecipe.ParseElement(op.GetStr("element", "arcane"));
            float strength = op.GetFloat("strength", 1f);
            return t => { t.ElementType = el; t.ElementStrength = strength; if (el == TileElementType.Fire) t.IsHazardous = true; };
        }
        if (op.Has("obstacle_kind"))
        {
            string kind = op.GetStr("obstacle_kind", "wall");
            return t => { t.IsBlocked = true; t.IsWalkable = false; t.BlocksLineOfSight = true; t.ObstacleKind = kind; };
        }
        if (op.Has("terrain"))
        {
            var tr = MapRecipe.ParseTerrain(op.GetStr("terrain", "grass"));
            // Full-fidelity write (2026-08-11): TerrainType alone left gameplay
            // flags stale, so recipe-written water was WALKABLE. ApplyTerrainType
            // sets walkability/cost/hazard to match the terrain. NOTE it also
            // clears obstacle flags, so terrain ops must never target wall tiles
            // (the city compiler orders its ops accordingly).
            return t => ApplyTerrainType(t, tr);
        }
        if (op.Has("height"))
        {
            int h = op.GetInt("height", 1);
            return t => { t.Height = System.Math.Max(t.Height, h); };
        }
        return t => { };
    }

    private Vector2I ResolveDir(FeatureOp op)
    {
        if (op.Has("dir"))
        {
            Variant v = op.GetVariant("dir");
            if (v.VariantType == Variant.Type.Int)
            {
                int i = v.AsInt32();
                return HexDirs[((i % HexDirs.Length) + HexDirs.Length) % HexDirs.Length];
            }
        }

        return HexDirs[_rng.RandiRange(0, HexDirs.Length - 1)];
    }

    private Vector2I PickLowTile()
    {
        Vector2I best = Vector2I.Zero;
        int bestH = int.MaxValue;
        bool found = false;

        foreach (var kvp in Tiles)
        {
            if (IsReserved(kvp.Key))
                continue;
            if (!found || kvp.Value.Height < bestH)
            {
                bestH = kvp.Value.Height;
                best = kvp.Key;
                found = true;
            }
        }

        return found ? best : GetRandomCoord();
    }

    private void PaintOrganicPatch(Vector2I center, TileTerrainType terrain, int radius)
    {
        OrganicBlob(center, radius, 0.65f, 0.5f, (tile, t) =>
        {
            if (tile.TerrainType == TileTerrainType.Water && terrain != TileTerrainType.Water)
                return;
            ApplyTerrainType(tile, terrain);
        });
    }
}
