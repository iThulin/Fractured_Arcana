using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// HexGridManager.Recipes.cs  (partial of HexGridManager)
//
// Purpose:        Executes JSON-authored map recipes. When MapRecipeId
//                 is set and resolves, the recipe drives shape, base
//                 terrain palette, features (by phase), and atmosphere —
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
    private void RunRecipeFeatures(MapRecipe r, string phase)
    {
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
    }

    // ── Param resolvers ─────────────────────────────────────────────────────

    private int Roll(FeatureOp op, string key, int defMin, int defMax)
    {
        var (a, b) = op.GetIntRange(key, defMin, defMax);
        return _rng.RandiRange(Math.Min(a, b), Math.Max(a, b));
    }

    private Vector2I CoordFromOp(FeatureOp op, string key, Vector2I fallback) =>
        op.Has(key) ? ResolveCoord(op.GetVariant(key)) : fallback;

    private Vector2I ResolveCoord(Variant spec)
    {
        if (spec.VariantType == Variant.Type.Array)
        {
            var a = spec.AsGodotArray();
            if (a.Count >= 2)
                return new Vector2I(a[0].AsInt32(), a[1].AsInt32());
        }

        string s = spec.VariantType == Variant.Type.String ? spec.AsString() : "center";
        return s switch
        {
            "center" => _centerCoord,
            "random" => GetRandomCoord(),
            "high_tile" => PickHighTile(),
            "low_tile" => PickLowTile(),
            "player_anchor" => PlayerLayoutAnchor,
            "enemy_anchor" => EnemyLayoutAnchor,
            _ => _centerCoord
        };
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
