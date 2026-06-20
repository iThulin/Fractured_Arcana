using Godot;
using System.Collections.Generic;

// ============================================================
// TilesetManifest.cs
//
// Purpose:        Data model for a prop/tileset manifest. Maps each
//                 terrain type to a weighted "prop kit" plus a per-tile
//                 spawn chance and count range. The grid resolves this
//                 at generation time to scatter props (batched via
//                 MultiMesh where possible). This is the bridge between
//                 the recipe/terrain system and the actual meshes.
// Layer:          System (data)
// Collaborators:  TilesetRegistry (loads these), HexGridManager.Props
//                 (consumes), MapRecipe (shared Variant/enum helpers)
// ============================================================

/// <summary>One prop in a terrain's kit. Weight drives selection; the rest is per-instance jitter.</summary>
public sealed class PropEntry
{
    public string ScenePath = "";
    public int Weight = 1;
    public float ScaleMin = 1f;
    public float ScaleMax = 1f;
    public float YOffset = 0.05f;
    public float Jitter = 0.3f;
    public bool BlocksLos = false;
    public bool Batch = true; // batch into a MultiMesh (single-mesh props); false = instantiate full scene

    public static PropEntry FromDict(Godot.Collections.Dictionary d)
    {
        var p = new PropEntry
        {
            ScenePath = MapRecipe.Str(d, "scene", ""),
            Weight = MapRecipe.Int(d, "weight", 1),
            YOffset = MapRecipe.Flt(d, "y_offset", 0.05f),
            Jitter = MapRecipe.Flt(d, "jitter", 0.3f),
            BlocksLos = d.ContainsKey("blocks_los") && d["blocks_los"].AsBool(),
            Batch = !d.ContainsKey("batch") || d["batch"].AsBool()
        };

        var (sMin, sMax) = FltRange(d, "scale", 1f, 1f);
        p.ScaleMin = sMin;
        p.ScaleMax = sMax;
        return p;
    }

    internal static (float, float) FltRange(Godot.Collections.Dictionary d, string key, float defMin, float defMax)
    {
        if (!d.ContainsKey(key))
            return (defMin, defMax);

        Variant v = d[key];
        if (v.VariantType == Variant.Type.Array)
        {
            var a = v.AsGodotArray();
            if (a.Count >= 2) return (a[0].AsSingle(), a[1].AsSingle());
            if (a.Count == 1) return (a[0].AsSingle(), a[0].AsSingle());
            return (defMin, defMax);
        }

        float s = v.AsSingle();
        return (s, s);
    }
}

public sealed class TerrainPropSet
{
    public float Chance = 0.7f;   // probability a tile of this terrain gets ANY props
    public int CountMin = 1;
    public int CountMax = 3;
    public List<PropEntry> Props = new();

    public static TerrainPropSet FromDict(Godot.Collections.Dictionary d)
    {
        var set = new TerrainPropSet { Chance = MapRecipe.Flt(d, "chance", 0.7f) };

        var (cMin, cMax) = IntRange(d, "count", 1, 3);
        set.CountMin = cMin;
        set.CountMax = cMax;

        if (d.ContainsKey("props"))
            foreach (var item in d["props"].AsGodotArray())
                set.Props.Add(PropEntry.FromDict(item.AsGodotDictionary()));

        return set;
    }

    private static (int, int) IntRange(Godot.Collections.Dictionary d, string key, int defMin, int defMax)
    {
        if (!d.ContainsKey(key))
            return (defMin, defMax);

        Variant v = d[key];
        if (v.VariantType == Variant.Type.Array)
        {
            var a = v.AsGodotArray();
            if (a.Count >= 2) return (a[0].AsInt32(), a[1].AsInt32());
            if (a.Count == 1) return (a[0].AsInt32(), a[0].AsInt32());
            return (defMin, defMax);
        }

        int s = v.AsInt32();
        return (s, s);
    }
}

public sealed class TilesetManifest
{
    public string Id = "";
    public Dictionary<TileTerrainType, TerrainPropSet> Terrains = new();

    public static TilesetManifest FromDict(Godot.Collections.Dictionary d)
    {
        var m = new TilesetManifest { Id = MapRecipe.Str(d, "id", "") };

        if (d.ContainsKey("terrains"))
        {
            var terr = d["terrains"].AsGodotDictionary();
            foreach (var key in terr.Keys)
            {
                string name = key.AsString();
                var set = TerrainPropSet.FromDict(terr[key].AsGodotDictionary());
                m.Terrains[MapRecipe.ParseTerrain(name)] = set;
            }
        }

        return m;
    }
}
