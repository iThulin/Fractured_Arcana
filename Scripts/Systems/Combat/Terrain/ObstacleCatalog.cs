using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// ObstacleCatalog.cs
//
// Purpose:        Registry of obstacle KINDS a recipe or generator may
//                 paint onto a tile: what rules the kind carries (Low or
//                 High cover), what it looks like until sculpted
//                 (silhouette), and which material family it belongs to
//                 so meshes can be shared. Loaded once from
//                 Data/Obstacles/obstacle_catalog.json. Role is the only
//                 field the rules read; everything else is dressing.
// Layer:          Systems / Combat / Terrain
// Collaborators:  HexGridManager.ApplyObstacle (role), HexGridManager
//                 .Visuals (silhouette, colour, scene), MapRecipe
//                 (obstacle palettes resolve roles to kinds)
// See:            docs/cover_and_zoc_v1.md §9
// ============================================================

public enum ObstacleRole
{
    /// <summary>Blocks movement, not sight. Low cover.</summary>
    Low,
    /// <summary>Blocks movement and sight. High cover.</summary>
    High
}

public enum ObstacleSilhouette
{
    /// <summary>A wall piece aligned to its run of same-kind neighbours.</summary>
    Slab,
    /// <summary>A round column: tall, narrow, burst wraps around it.</summary>
    Pillar,
    /// <summary>A hex prism filling the tile.</summary>
    Mass,
    /// <summary>An authored scene (falls back to Mass when none loads).</summary>
    Scene
}

public sealed class ObstacleSpec
{
    public string Kind = "";
    public ObstacleRole Role = ObstacleRole.High;
    public ObstacleSilhouette Silhouette = ObstacleSilhouette.Mass;
    public string Material = "rock";
    public Color Color = new(0.46f, 0.44f, 0.41f);
    public string Label = "";
    /// <summary>Placeholder height override. 0 = the silhouette's default for the role.</summary>
    public float Height = 0f;
    /// <summary>res:// path for Silhouette.Scene. Empty = use the grid's exported scene
    /// for this kind if one exists (rock, crystal), else Mass.</summary>
    public string ScenePath = "";

    public bool IsLow => Role == ObstacleRole.Low;
}

public static class ObstacleCatalog
{
    private static readonly Dictionary<string, ObstacleSpec> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static IReadOnlyDictionary<string, ObstacleSpec> All { get { EnsureLoaded(); return _kinds; } }

    public static void EnsureLoaded(string path = "res://Data/Obstacles/obstacle_catalog.json")
    {
        if (_loaded)
            return;
        _loaded = true;
        Load(path);
    }

    public static void Reload(string path = "res://Data/Obstacles/obstacle_catalog.json")
    {
        _loaded = false;
        _warned.Clear();
        EnsureLoaded(path);
    }

    /// <summary>Resolve a kind. False for an unknown key. Callers that must paint
    /// SOMETHING use <see cref="GetOrFallback"/> instead.</summary>
    public static bool TryGet(string kind, out ObstacleSpec spec)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(kind))
        {
            spec = null;
            return false;
        }
        return _kinds.TryGetValue(kind, out spec);
    }

    /// <summary>Resolve a kind, or warn once and return a High rock mass so an
    /// unknown kind is still a visible, sight-blocking obstacle rather than a
    /// silent hole in the rules.</summary>
    public static ObstacleSpec GetOrFallback(string kind)
    {
        if (TryGet(kind, out var spec))
            return spec;
        if (!string.IsNullOrEmpty(kind) && !kind.StartsWith("building:") && _warned.Add(kind))
            GD.PushWarning($"[ObstacleCatalog] Unknown obstacle kind '{kind}': treated as a High rock mass. Add it to Data/Obstacles/obstacle_catalog.json.");
        return new ObstacleSpec { Kind = kind ?? "", Role = ObstacleRole.High, Silhouette = ObstacleSilhouette.Mass, Material = "rock" };
    }

    /// <summary>True when the kind is registered as Low cover. Unknown kinds are High.</summary>
    public static bool IsLow(string kind) => TryGet(kind, out var s) && s.IsLow;

    private static void Load(string path)
    {
        _kinds.Clear();

        using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (fa == null)
        {
            GD.PushWarning($"[ObstacleCatalog] Could not open {path}; every kind will be a High rock mass.");
            return;
        }

        var json = new Json();
        if (json.Parse(fa.GetAsText()) != Error.Ok)
        {
            GD.PushError($"[ObstacleCatalog] Parse error in {path}: {json.GetErrorMessage()} (line {json.GetErrorLine()})");
            return;
        }

        var root = json.Data.AsGodotDictionary();
        if (!root.ContainsKey("kinds"))
        {
            GD.PushWarning($"[ObstacleCatalog] {path} has no \"kinds\" array.");
            return;
        }

        foreach (var item in root["kinds"].AsGodotArray())
        {
            var d = item.AsGodotDictionary();
            var spec = new ObstacleSpec
            {
                Kind = MapRecipe.Str(d, "kind", ""),
                Material = MapRecipe.Str(d, "material", "rock"),
                Label = MapRecipe.Str(d, "label", ""),
                Height = MapRecipe.Flt(d, "height", 0f),
                ScenePath = MapRecipe.Str(d, "scene", ""),
            };
            if (string.IsNullOrEmpty(spec.Kind))
                continue;
            if (string.IsNullOrEmpty(spec.Label))
                spec.Label = spec.Kind;

            spec.Role = MapRecipe.Str(d, "role", "high").ToLowerInvariant() == "low"
                ? ObstacleRole.Low : ObstacleRole.High;

            spec.Silhouette = MapRecipe.Str(d, "silhouette", "mass").ToLowerInvariant() switch
            {
                "slab" => ObstacleSilhouette.Slab,
                "pillar" => ObstacleSilhouette.Pillar,
                "scene" => ObstacleSilhouette.Scene,
                _ => ObstacleSilhouette.Mass
            };

            if (d.ContainsKey("color"))
                spec.Color = MapRecipe.Col(d, "color", spec.Color);

            _kinds[spec.Kind] = spec;
        }

        GD.Print($"[ObstacleCatalog] Loaded {_kinds.Count} obstacle kind(s) from {path}.");
    }
}
