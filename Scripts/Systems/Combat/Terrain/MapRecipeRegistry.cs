using Godot;
using System.Collections.Generic;

// ============================================================
// MapRecipeRegistry.cs
//
// Purpose:        Loads and stores JSON map recipes, keyed by id.
//                 Mirrors the card/effect registry pattern. Lazy:
//                 EnsureLoaded() loads once on first use.
// Layer:          System
// Collaborators:  MapRecipe (parsed instances), HexGridManager.Recipes
// Notes:          Uses DirAccess on res:// which works in the editor and
//                 most exports. If your card loader uses a manifest for
//                 exported builds, point this at the same mechanism.
// ============================================================

public static class MapRecipeRegistry
{
    private static readonly Dictionary<string, MapRecipe> _recipes = new();
    private static bool _loaded;

    public static IReadOnlyDictionary<string, MapRecipe> All => _recipes;

    public static void EnsureLoaded(string dir = "res://Data/Maps")
    {
        if (_loaded)
            return;

        LoadAll(dir);
        _loaded = true;
    }

    public static void Reload(string dir = "res://Data/Maps")
    {
        _loaded = false;
        EnsureLoaded(dir);
    }

    public static MapRecipe Get(string id) =>
        _recipes.TryGetValue(id, out var r) ? r : null;

    private static void LoadAll(string dir)
    {
        _recipes.Clear();

        using var da = DirAccess.Open(dir);
        if (da == null)
        {
            GD.PushWarning($"[MapRecipeRegistry] Directory not found: {dir}");
            return;
        }

        foreach (var file in da.GetFiles())
        {
            if (!file.EndsWith(".json"))
                continue;
            if (file == "terrain_map.json")
                continue;

            string path = dir.TrimEnd('/') + "/" + file;

            using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (fa == null)
            {
                GD.PushWarning($"[MapRecipeRegistry] Could not open {path}");
                continue;
            }

            var json = new Json();
            if (json.Parse(fa.GetAsText()) != Error.Ok)
            {
                GD.PushError($"[MapRecipeRegistry] Parse error in {path}: {json.GetErrorMessage()} (line {json.GetErrorLine()})");
                continue;
            }

            var recipe = MapRecipe.FromDict(json.Data.AsGodotDictionary());
            if (string.IsNullOrEmpty(recipe.Id))
            {
                GD.PushWarning($"[MapRecipeRegistry] {path} has no \"id\"; skipped.");
                continue;
            }

            _recipes[recipe.Id] = recipe;
        }

        GD.Print($"[MapRecipeRegistry] Loaded {_recipes.Count} map recipe(s) from {dir}.");
    }
}
