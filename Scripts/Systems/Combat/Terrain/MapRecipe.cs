using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// MapRecipe.cs
//
// Purpose:        Data model for a JSON-authored combat map recipe.
//                 A recipe declares the grid shape, the base-terrain
//                 palette (elevation/moisture rules), optional theme
//                 atmosphere, and an ordered list of feature ops that
//                 compose the existing HexGridManager feature builders.
//                 Parsed from Godot.Json so it needs no extra packages
//                 and handles scalar-or-range fields naturally.
// Layer:          System (data)
// Collaborators:  MapRecipeRegistry (loads these), HexGridManager.Recipes
//                 (executes feature ops), MapField (ClassifyByPalette)
// ============================================================

/// <summary>One elevation/moisture rule in a base-terrain palette. First matching rule wins.</summary>
public sealed class PaletteRule
{
    public TileTerrainType Terrain;
    public float? MaxElevation;
    public float? MinElevation;
    public float? MaxMoisture;
    public float? MinMoisture;

    public static PaletteRule FromDict(Godot.Collections.Dictionary d)
    {
        return new PaletteRule
        {
            Terrain = MapRecipe.ParseTerrain(MapRecipe.Str(d, "terrain", "grass")),
            MaxElevation = d.ContainsKey("max_elevation") ? d["max_elevation"].AsSingle() : (float?)null,
            MinElevation = d.ContainsKey("min_elevation") ? d["min_elevation"].AsSingle() : (float?)null,
            MaxMoisture = d.ContainsKey("max_moisture") ? d["max_moisture"].AsSingle() : (float?)null,
            MinMoisture = d.ContainsKey("min_moisture") ? d["min_moisture"].AsSingle() : (float?)null
        };
    }
}

public sealed class ShapeSpec
{
    public MapShape Type = MapShape.Rectangle;
    public int Width = 7;
    public int Height = 6;
    public int Radius = 4;
    public float Erosion = 0.5f;

    public static ShapeSpec FromDict(Godot.Collections.Dictionary d) => new()
    {
        Type = MapRecipe.ParseShape(MapRecipe.Str(d, "type", "rectangle")),
        Width = MapRecipe.Int(d, "width", 7),
        Height = MapRecipe.Int(d, "height", 6),
        Radius = MapRecipe.Int(d, "radius", 4),
        Erosion = MapRecipe.Flt(d, "erosion", 0.5f)
    };
}

public sealed class BaseTerrainSpec
{
    public float ElevationFrequency = 0f; // 0 = use MapField default
    public float MoistureFrequency = 0f;
    public float DetailWeight = -1f;       // <0 = use default
    public int MaxHeightStep = 0;          // 0 = use default
    public int MinHeightStep = 0;
    public List<PaletteRule> Palette = new();

    public static BaseTerrainSpec FromDict(Godot.Collections.Dictionary d)
    {
        var b = new BaseTerrainSpec
        {
            ElevationFrequency = MapRecipe.Flt(d, "elevation_frequency", 0f),
            MoistureFrequency = MapRecipe.Flt(d, "moisture_frequency", 0f),
            DetailWeight = MapRecipe.Flt(d, "detail_weight", -1f),
            MaxHeightStep = MapRecipe.Int(d, "max_height_step", 0),
            MinHeightStep = MapRecipe.Int(d, "min_height_step", 0)
        };

        if (d.ContainsKey("palette"))
            foreach (var item in d["palette"].AsGodotArray())
                b.Palette.Add(PaletteRule.FromDict(item.AsGodotDictionary()));

        return b;
    }
}

public sealed class AtmosphereSpec
{
    public Color Sun = new(1, 1, 1);
    public float SunEnergy = 1f;
    public Color Ambient = new(0.5f, 0.5f, 0.5f);
    public float AmbientEnergy = 0.4f;
    public Color Fog = new(0.7f, 0.7f, 0.7f);
    public float FogDensity = 0.01f;

    public static AtmosphereSpec FromDict(Godot.Collections.Dictionary d) => new()
    {
        Sun = MapRecipe.Col(d, "sun_color", new Color(1, 1, 1)),
        SunEnergy = MapRecipe.Flt(d, "sun_energy", 1f),
        Ambient = MapRecipe.Col(d, "ambient_color", new Color(0.5f, 0.5f, 0.5f)),
        AmbientEnergy = MapRecipe.Flt(d, "ambient_energy", 0.4f),
        Fog = MapRecipe.Col(d, "fog_color", new Color(0.7f, 0.7f, 0.7f)),
        FogDensity = MapRecipe.Flt(d, "fog_density", 0.01f)
    };
}

/// <summary>
/// Optional per-recipe water personality (S1.5): a named profile from
/// HexGridManager.WaterPlane's preset table plus inline field overrides.
/// Example: { "profile": "murky_swamp", "foam_strength": 0.45 }
/// </summary>
public sealed class WaterSpec
{
    public string Profile = "";
    public Godot.Collections.Dictionary Raw;

    public static WaterSpec FromDict(Godot.Collections.Dictionary d) => new()
    {
        Profile = MapRecipe.Str(d, "profile", ""),
        Raw = d
    };

    public bool Has(string key) => Raw != null && Raw.ContainsKey(key);
    public float Flt(string key, float def) => Has(key) ? Raw[key].AsSingle() : def;
    public Color Col(string key, Color def) => Raw != null ? MapRecipe.Col(Raw, key, def) : def;
}

/// <summary>
/// Optional per-recipe sand style (S2): overrides the splat sand_ground
/// palette for this map. Example: { "light": [0.78,0.65,0.42], "warm": [0.56,0.40,0.24] }
/// </summary>
public sealed class SandSpec
{
    public Color? Light;
    public Color? Warm;

    public static SandSpec FromDict(Godot.Collections.Dictionary d) => new()
    {
        Light = d.ContainsKey("light") ? MapRecipe.Col(d, "light", default) : null,
        Warm = d.ContainsKey("warm") ? MapRecipe.Col(d, "warm", default) : null
    };
}

/// <summary>One feature operation. Typed convenience getters read from the raw param bag.</summary>
public sealed class FeatureOp
{
    public string Feature = "";
    public string Phase = "accent"; // "skeleton" (pre-spawn) or "accent" (post-spawn)
    public float Chance = 1f;
    public Godot.Collections.Dictionary Raw;

    public bool Has(string key) => Raw != null && Raw.ContainsKey(key);
    public int GetInt(string key, int def) => Has(key) ? Raw[key].AsInt32() : def;
    public float GetFloat(string key, float def) => Has(key) ? Raw[key].AsSingle() : def;
    public string GetStr(string key, string def) => Has(key) ? Raw[key].AsString() : def;
    public Variant GetVariant(string key) => Has(key) ? Raw[key] : default;

    /// <summary>Reads an int field that may be a scalar (3) or a [min,max] range. Returns the inclusive bounds.</summary>
    public (int min, int max) GetIntRange(string key, int defMin, int defMax)
    {
        if (!Has(key))
            return (defMin, defMax);

        Variant v = Raw[key];
        if (v.VariantType == Variant.Type.Array)
        {
            var a = v.AsGodotArray();
            if (a.Count >= 2)
                return (a[0].AsInt32(), a[1].AsInt32());
            if (a.Count == 1)
                return (a[0].AsInt32(), a[0].AsInt32());
            return (defMin, defMax);
        }

        int s = v.AsInt32();
        return (s, s);
    }
}

/// <summary>Battlefield E4 — one scheduled map event parsed from a recipe's
/// `map_events` array. `round` is its first firing (1-based); `telegraph` is how
/// many rounds ahead it is announced (0 = silent); `repeat_every` (0 = one-shot)
/// re-fires it on that cadence. Kind-specific params (element, at, radius, steps,
/// per_patch, announce) live in Raw.</summary>
public sealed class MapEventDef
{
    public string Kind = "";
    public int Round = 1;
    public int Telegraph = 1;
    public int RepeatEvery = 0;
    public Godot.Collections.Dictionary Raw;

    public bool Has(string key) => Raw != null && Raw.ContainsKey(key);
    public int GetInt(string key, int def) => Has(key) ? Raw[key].AsInt32() : def;
    public float GetFloat(string key, float def) => Has(key) ? Raw[key].AsSingle() : def;
    public string GetStr(string key, string def) => Has(key) ? Raw[key].AsString() : def;
    public Variant GetVariant(string key) => Has(key) ? Raw[key] : default;

    /// <summary>Kinds that make tiles lethal or impassable — subject to the telegraph
    /// law (must warn at least one round ahead; the loader clamps telegraph to >= 1).</summary>
    public static bool IsDestructiveKind(string kind) => kind == "collapse_tiles";
}

/// <summary>One off-map building mass for the siege backdrop.</summary>
public sealed class SiegeBackdropStamp
{
    public Vector2I At;
    public int Radius = 2;
    public string Id = "";
}

/// <summary>City-siege recipe extras (CityBattlemapCompiler): authored spawn
/// anchors + the gate gap. Optional — null on every hand-authored recipe, and
/// everything downstream treats absence as "use the default derivation".</summary>
public sealed class SiegeSpec
{
    public string Vector = "";
    public string Entry = "";

    /// <summary>True when the PLAYER is the defender (home defense) — gates
    /// door spawning and any future defender-only dressing.</summary>
    public bool Defending;

    public Vector2I? PlayerAnchor;
    public Vector2I? EnemyAnchor;
    public List<Vector2I> GateGap = new();

    /// <summary>hold_zone "gate" zone tiles, compiler-computed (door + inside
    /// pocket only). The runtime prefers these over its own BFS because only
    /// the compiler knows the city's inside from its outside.</summary>
    public List<Vector2I> ObjectiveZone = new();

    /// <summary>VISUAL-ONLY: wall tiles continuing past the arena edge into
    /// the vista (axial coords beyond the map radius — AxialToWorld still
    /// converts them; no TileData exists there).</summary>
    public List<Vector2I> BackdropWall = new();

    /// <summary>VISUAL-ONLY: off-map building masses (the rest of the city).</summary>
    public List<SiegeBackdropStamp> BackdropStamps = new();

    private static Vector2I? Coord(Godot.Collections.Dictionary d, string key)
    {
        if (!d.ContainsKey(key))
            return null;
        var a = d[key].AsGodotArray();
        if (a.Count < 2)
            return null;
        return new Vector2I(a[0].AsInt32(), a[1].AsInt32());
    }

    public static SiegeSpec FromDict(Godot.Collections.Dictionary d)
    {
        var s = new SiegeSpec
        {
            Vector = MapRecipe.Str(d, "vector", ""),
            Entry = MapRecipe.Str(d, "entry", ""),
            Defending = d.ContainsKey("defending") && d["defending"].AsBool(),
            PlayerAnchor = Coord(d, "player_anchor"),
            EnemyAnchor = Coord(d, "enemy_anchor"),
        };
        if (d.ContainsKey("gate_gap"))
        {
            foreach (var item in d["gate_gap"].AsGodotArray())
            {
                var a = item.AsGodotArray();
                if (a.Count >= 2)
                    s.GateGap.Add(new Vector2I(a[0].AsInt32(), a[1].AsInt32()));
            }
        }
        if (d.ContainsKey("objective_zone"))
        {
            foreach (var item in d["objective_zone"].AsGodotArray())
            {
                var a = item.AsGodotArray();
                if (a.Count >= 2)
                    s.ObjectiveZone.Add(new Vector2I(a[0].AsInt32(), a[1].AsInt32()));
            }
        }
        if (d.ContainsKey("backdrop_wall"))
        {
            foreach (var item in d["backdrop_wall"].AsGodotArray())
            {
                var a = item.AsGodotArray();
                if (a.Count >= 2)
                    s.BackdropWall.Add(new Vector2I(a[0].AsInt32(), a[1].AsInt32()));
            }
        }
        if (d.ContainsKey("backdrop_stamps"))
        {
            foreach (var item in d["backdrop_stamps"].AsGodotArray())
            {
                var sd = item.AsGodotDictionary();
                var at = Coord(sd, "at");
                if (at == null)
                    continue;
                s.BackdropStamps.Add(new SiegeBackdropStamp
                {
                    At = at.Value,
                    Radius = MapRecipe.Int(sd, "radius", 2),
                    Id = MapRecipe.Str(sd, "id", ""),
                });
            }
        }
        return s;
    }
}

public sealed class MapRecipe
{
    public string Id = "";
    public string DisplayName = "";
    public ShapeSpec Shape;
    public BaseTerrainSpec BaseTerrain;
    public AtmosphereSpec Atmosphere;
    public WaterSpec Water;
    public SandSpec Sand;
    public SiegeSpec Siege;
    public List<FeatureOp> Features = new();
    public List<MapEventDef> MapEvents = new();

    public static MapRecipe FromDict(Godot.Collections.Dictionary d)
    {
        var r = new MapRecipe
        {
            Id = Str(d, "id", ""),
            DisplayName = Str(d, "display_name", Str(d, "id", ""))
        };

        if (d.ContainsKey("shape"))
            r.Shape = ShapeSpec.FromDict(d["shape"].AsGodotDictionary());

        if (d.ContainsKey("base_terrain"))
            r.BaseTerrain = BaseTerrainSpec.FromDict(d["base_terrain"].AsGodotDictionary());

        if (d.ContainsKey("atmosphere"))
            r.Atmosphere = AtmosphereSpec.FromDict(d["atmosphere"].AsGodotDictionary());

        if (d.ContainsKey("water"))
            r.Water = WaterSpec.FromDict(d["water"].AsGodotDictionary());

        if (d.ContainsKey("sand"))
            r.Sand = SandSpec.FromDict(d["sand"].AsGodotDictionary());

        if (d.ContainsKey("siege"))
            r.Siege = SiegeSpec.FromDict(d["siege"].AsGodotDictionary());

        if (d.ContainsKey("features"))
        {
            foreach (var item in d["features"].AsGodotArray())
            {
                var fd = item.AsGodotDictionary();
                r.Features.Add(new FeatureOp
                {
                    Feature = Str(fd, "feature", ""),
                    Phase = Str(fd, "phase", "accent"),
                    Chance = Flt(fd, "chance", 1f),
                    Raw = fd
                });
            }
        }

        if (d.ContainsKey("map_events"))
        {
            foreach (var item in d["map_events"].AsGodotArray())
            {
                var ed = item.AsGodotDictionary();
                var mev = new MapEventDef
                {
                    Kind = Str(ed, "kind", ""),
                    Round = Int(ed, "round", 1),
                    Telegraph = Int(ed, "telegraph", 1),
                    RepeatEvery = Int(ed, "repeat_every", 0),
                    Raw = ed
                };
                // Telegraph law: a destructive event must warn a round ahead.
                if (MapEventDef.IsDestructiveKind(mev.Kind) && mev.Telegraph < 1)
                {
                    Godot.GD.PushWarning($"[MapRecipe] destructive map event '{mev.Kind}' had telegraph {mev.Telegraph}; forced to 1.");
                    mev.Telegraph = 1;
                }
                r.MapEvents.Add(mev);
            }
        }

        return r;
    }

    // ── Variant helpers ─────────────────────────────────────────────────────

    public static string Str(Godot.Collections.Dictionary d, string key, string def) =>
        d.ContainsKey(key) ? d[key].AsString() : def;

    public static int Int(Godot.Collections.Dictionary d, string key, int def) =>
        d.ContainsKey(key) ? d[key].AsInt32() : def;

    public static float Flt(Godot.Collections.Dictionary d, string key, float def) =>
        d.ContainsKey(key) ? d[key].AsSingle() : def;

    public static Color Col(Godot.Collections.Dictionary d, string key, Color def)
    {
        if (!d.ContainsKey(key))
            return def;

        var a = d[key].AsGodotArray();
        if (a.Count >= 3)
            return new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle(), a.Count >= 4 ? a[3].AsSingle() : 1f);

        return def;
    }

    // ── Shared enum parsers (case-insensitive) ──────────────────────────────

    public static MapShape ParseShape(string s) =>
        Enum.TryParse<MapShape>(s, true, out var v) ? v : MapShape.Rectangle;

    public static TileTerrainType ParseTerrain(string s) =>
        Enum.TryParse<TileTerrainType>(s, true, out var v) ? v : TileTerrainType.Grass;

    public static TileElementType ParseElement(string s) =>
        Enum.TryParse<TileElementType>(s, true, out var v) ? v : TileElementType.None;
}
