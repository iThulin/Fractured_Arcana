using Godot;
using System.Collections.Generic;

// ============================================================
// TilesetRegistry.cs
//
// Purpose:        Loads and stores prop/tileset manifests by id.
//                 Same pattern as MapRecipeRegistry.
// Layer:          System
// Collaborators:  TilesetManifest, HexGridManager.Props
// ============================================================

public static class TilesetRegistry
{
    private static readonly Dictionary<string, TilesetManifest> _tilesets = new();
    private static bool _loaded;

    public static IReadOnlyDictionary<string, TilesetManifest> All => _tilesets;

    public static void EnsureLoaded(string dir = "res://Data/Tilesets")
    {
        if (_loaded)
            return;

        LoadAll(dir);
        _loaded = true;
    }

    public static void Reload(string dir = "res://Data/Tilesets")
    {
        _loaded = false;
        EnsureLoaded(dir);
    }

    public static TilesetManifest Get(string id) =>
        _tilesets.TryGetValue(id, out var t) ? t : null;

    private static void LoadAll(string dir)
    {
        _tilesets.Clear();

        using var da = DirAccess.Open(dir);
        if (da == null)
        {
            GD.PushWarning($"[TilesetRegistry] Directory not found: {dir}");
            return;
        }

        foreach (var file in da.GetFiles())
        {
            if (!file.EndsWith(".json"))
                continue;

            string path = dir.TrimEnd('/') + "/" + file;

            using var fa = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (fa == null)
            {
                GD.PushWarning($"[TilesetRegistry] Could not open {path}");
                continue;
            }

            var json = new Json();
            if (json.Parse(fa.GetAsText()) != Error.Ok)
            {
                GD.PushError($"[TilesetRegistry] Parse error in {path}: {json.GetErrorMessage()} (line {json.GetErrorLine()})");
                continue;
            }

            var manifest = TilesetManifest.FromDict(json.Data.AsGodotDictionary());
            if (string.IsNullOrEmpty(manifest.Id))
            {
                GD.PushWarning($"[TilesetRegistry] {path} has no \"id\"; skipped.");
                continue;
            }

            _tilesets[manifest.Id] = manifest;
        }

        GD.Print($"[TilesetRegistry] Loaded {_tilesets.Count} tileset(s) from {dir}.");
    }
}
