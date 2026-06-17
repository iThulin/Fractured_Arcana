using Godot;
using System;

// ============================================================
// TerrainTextureLibrary.cs
//
// Purpose:        Builds the Texture2DArrays consumed by
//                 terrain_splat.gdshader from the per-terrain
//                 Texture2D exports on HexGridManager. Packs TWO
//                 arrays in lockstep:
//                   - ALBEDO array  (source_color, sRGB)
//                   - NORMAL array  (linear; flat 128,128,255
//                                    fallback where unassigned)
//                 All source images are normalised to one
//                 size/format. Layer index = (int)TileTerrainType
//                 for BOTH arrays — they shift together if the
//                 enum is reordered (don't reorder it).
// Layer:          System (asset helper)
// Collaborators:  HexGridManager (texture exports),
//                 HexMeshBuilder (fallback palette),
//                 terrain_splat.gdshader (consumer)
// ============================================================

public static class TerrainTextureLibrary
{
    private static Texture2DArray _cachedAlbedo;
    private static Texture2DArray _cachedNormal;

    /// <summary>Drops both cached arrays so the next build repacks (e.g. after swapping a texture in the inspector at runtime).</summary>
    public static void Invalidate()
    {
        _cachedAlbedo = null;
        _cachedNormal = null;
    }

    /// <summary>
    /// Returns the packed terrain ALBEDO array, building both arrays on first
    /// call. Returns null only on a hard packing failure (logged).
    /// </summary>
    public static Texture2DArray GetOrBuild(HexGridManager grid, int size)
    {
        if (_cachedAlbedo != null)
            return _cachedAlbedo;

        BuildArrays(grid, size);
        return _cachedAlbedo;
    }

    /// <summary>Returns the packed terrain NORMAL array, building both arrays on first call. Null on failure.</summary>
    public static Texture2DArray GetOrBuildNormals(HexGridManager grid, int size)
    {
        if (_cachedNormal != null)
            return _cachedNormal;

        BuildArrays(grid, size);
        return _cachedNormal;
    }

    private static void BuildArrays(HexGridManager grid, int size)
    {
        size = Mathf.Clamp(size, 64, 2048);

        var albedoImages = new Godot.Collections.Array<Image>();
        var normalImages = new Godot.Collections.Array<Image>();

        foreach (TileTerrainType terrain in Enum.GetValues<TileTerrainType>())
        {
            // ── Albedo layer (sRGB, solid-colour fallback) ──────────────
            Texture2D albSrc = AlbedoFor(grid, terrain);
            Image alb = albSrc?.GetImage();
            if (alb == null)
            {
                alb = SolidLayer(HexMeshBuilder.TerrainColor(grid, terrain), size);
            }
            else
            {
                NormaliseImage(alb, size);
            }
            alb.GenerateMipmaps();
            albedoImages.Add(alb);

            // ── Normal layer (linear, flat fallback) ────────────────────
            Texture2D nrmSrc = NormalFor(grid, terrain);
            Image nrm = nrmSrc?.GetImage();
            if (nrm == null)
            {
                nrm = FlatNormalLayer(size);
            }
            else
            {
                NormaliseImage(nrm, size);
            }
            nrm.GenerateMipmaps();
            normalImages.Add(nrm);
        }

        var albArray = new Texture2DArray();
        Error eA = albArray.CreateFromImages(albedoImages);
        if (eA != Error.Ok)
        {
            GD.PushError($"[TerrainTextureLibrary] Albedo CreateFromImages failed: {eA}");
            return;
        }

        var nrmArray = new Texture2DArray();
        Error eN = nrmArray.CreateFromImages(normalImages);
        if (eN != Error.Ok)
        {
            GD.PushError($"[TerrainTextureLibrary] Normal CreateFromImages failed: {eN}");
            return;
        }

        _cachedAlbedo = albArray;
        _cachedNormal = nrmArray;
        GD.Print($"[TerrainTextureLibrary] Packed {albedoImages.Count} albedo + {normalImages.Count} normal layers at {size}x{size}.");
    }

    private static void NormaliseImage(Image img, int size)
    {
        if (img.IsCompressed())
            img.Decompress();
        img.Convert(Image.Format.Rgba8);
        if (img.GetWidth() != size || img.GetHeight() != size)
            img.Resize(size, size, Image.Interpolation.Lanczos);
    }

    private static Texture2D AlbedoFor(HexGridManager grid, TileTerrainType terrain) => terrain switch
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

    private static Texture2D NormalFor(HexGridManager grid, TileTerrainType terrain) => terrain switch
    {
        TileTerrainType.Grass => grid.GrassNormal,
        TileTerrainType.Water => grid.WaterNormal,
        TileTerrainType.Lava => grid.LavaNormal,
        TileTerrainType.Forest => grid.ForestNormal,
        TileTerrainType.Stone => grid.StoneNormal,
        TileTerrainType.Arcane => grid.ArcaneNormal,
        TileTerrainType.Ice => grid.IceNormal,
        _ => null
    };

    private static Image SolidLayer(Color color, int size)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(color);
        return img;
    }

    /// <summary>Flat tangent-space normal (0,0,1) encoded as RGB (0.5, 0.5, 1.0). No perturbation.</summary>
    private static Image FlatNormalLayer(int size)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(new Color(0.5f, 0.5f, 1.0f, 1.0f));
        return img;
    }
}