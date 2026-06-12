using Godot;
using System;

// ============================================================
// TerrainTextureLibrary.cs
//
// Purpose:        Builds the Texture2DArray consumed by
//                 terrain_splat.gdshader from the per-terrain
//                 Texture2D exports on HexGridManager. All source
//                 images are normalised to one size/format
//                 (decompress → RGBA8 → resize → mipmaps), which
//                 Texture2DArray.CreateFromImages requires.
//                 Terrains with no texture assigned get a solid-
//                 colour layer from HexMeshBuilder.TerrainColor,
//                 so the system degrades gracefully while textures
//                 are authored one at a time.
// Layer:          System (asset helper)
// Collaborators:  HexGridManager (texture exports),
//                 HexMeshBuilder (fallback palette),
//                 terrain_splat.gdshader (consumer)
// Notes:          Layer index = (int)TileTerrainType. The enum is
//                 contiguous from 0 (Grass) to 6 (Ice); if that
//                 ever changes, this packing and the splat indices
//                 in HexMeshBuilder shift together (both cast the
//                 enum) — but any SAVED expectations about which
//                 layer is which would not. Don't reorder the enum.
// ============================================================

public static class TerrainTextureLibrary
{
    private static Texture2DArray _cached;

    /// <summary>Drops the cached array so the next GetOrBuild repacks (e.g. after swapping a texture in the inspector at runtime).</summary>
    public static void Invalidate() => _cached = null;

    /// <summary>
    /// Returns the packed terrain texture array, building it on first call.
    /// Returns null only on a hard packing failure (logged).
    /// </summary>
    public static Texture2DArray GetOrBuild(HexGridManager grid, int size)
    {
        if (_cached != null)
            return _cached;

        size = Mathf.Clamp(size, 64, 2048);
        var images = new Godot.Collections.Array<Image>();

        foreach (TileTerrainType terrain in Enum.GetValues<TileTerrainType>())
        {
            Texture2D src = TextureFor(grid, terrain);
            Image img = src?.GetImage();

            if (img == null)
            {
                img = SolidLayer(HexMeshBuilder.TerrainColor(grid, terrain), size);
            }
            else
            {
                if (img.IsCompressed())
                    img.Decompress();
                img.Convert(Image.Format.Rgba8);
                if (img.GetWidth() != size || img.GetHeight() != size)
                    img.Resize(size, size, Image.Interpolation.Lanczos);
            }

            img.GenerateMipmaps();
            images.Add(img);
        }

        var array = new Texture2DArray();
        Error err = array.CreateFromImages(images);
        if (err != Error.Ok)
        {
            GD.PushError($"[TerrainTextureLibrary] CreateFromImages failed: {err}");
            return null;
        }

        _cached = array;
        GD.Print($"[TerrainTextureLibrary] Packed {images.Count} terrain layers at {size}x{size}.");
        return _cached;
    }

    private static Texture2D TextureFor(HexGridManager grid, TileTerrainType terrain) => terrain switch
    {
        TileTerrainType.Grass => grid.GrassTexture,
        TileTerrainType.Water => grid.WaterTexture,
        TileTerrainType.Lava => grid.LavaTexture,
        TileTerrainType.Forest => grid.ForestTexture,
        TileTerrainType.Stone => grid.StoneTexture,
        TileTerrainType.Arcane => grid.ArcaneTexture,
        TileTerrainType.Ice => grid.IceTexture,
        _ => null
    };

    private static Image SolidLayer(Color color, int size)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(color);
        return img;
    }
}
