using Godot;
using System.Collections.Generic;

// ============================================================
// GlyphGrassField.cs
//
// Purpose:        Tells the painterly grass where prepared glyphs lie, so
//                 the blades right around each sigil are pressed flat and
//                 pushed outward, rippling, as if the glyph were projecting
//                 a force into the turf (2026-10-04).
// Layer:          Terrain
// Collaborators:  HexTile.cs (sets a site when a glyph's circle appears,
//                 ramps its strength, removes it on clear/exit),
//                 painterly_grass.gdshader (glyph_sites / glyph_site_count),
//                 HexGridManager.PainterlyGrass.cs (attaches the material)
// ============================================================
//
// WHY A UNIFORM ARRAY AND NOT THE IMBUEMENT TEXTURE
//
// ImbuementField already packs all four channels, and a glyph wants
// something a texel cannot give cheaply: a CENTRE, so each blade knows which
// way is "away from the sigil". A handful of glyphs at most are ever on the
// board, so a small array of (x, z, radius, strength) the grass loops over is
// both exact and cheap. Sites beyond MaxSites are simply not pressed.
// ============================================================

/// <summary>World positions of glyph circles, pushed to every attached grass material.</summary>
public static class GlyphGrassField
{
    /// <summary>Must match the array size in painterly_grass.gdshader.</summary>
    public const int MaxSites = 24;

    private struct Site
    {
        public Vector2 Xz;
        public float Radius;
        public float Strength;
    }

    private static readonly Dictionary<ulong, Site> _sites = new();
    private static readonly List<ShaderMaterial> _materials = new();

    /// <summary>Registers a grass material. Safe to call repeatedly.</summary>
    public static void Attach(ShaderMaterial material)
    {
        if (material == null || _materials.Contains(material))
            return;
        _materials.Add(material);
        Push();
    }

    /// <summary>Adds or moves a site, keeping its current strength.</summary>
    public static void Set(ulong id, Vector3 worldPos, float radius, float strength)
    {
        _sites[id] = new Site { Xz = new Vector2(worldPos.X, worldPos.Z), Radius = radius, Strength = strength };
        Push();
    }

    /// <summary>Changes only the strength (0 = untouched grass, 1 = fully pressed).</summary>
    public static void SetStrength(ulong id, float strength)
    {
        if (!_sites.TryGetValue(id, out var s))
            return;
        s.Strength = strength;
        _sites[id] = s;
        Push();
    }

    public static void Remove(ulong id)
    {
        if (_sites.Remove(id))
            Push();
    }

    public static void Clear()
    {
        _sites.Clear();
        Push();
    }

    private static void Push()
    {
        var arr = new Vector4[MaxSites];
        int n = 0;
        foreach (var s in _sites.Values)
        {
            if (n >= MaxSites)
                break;
            if (s.Strength <= 0.001f)
                continue;
            arr[n++] = new Vector4(s.Xz.X, s.Xz.Y, s.Radius, s.Strength);
        }
        _materials.RemoveAll(m => m == null || !GodotObject.IsInstanceValid(m));
        foreach (var m in _materials)
        {
            m.SetShaderParameter("glyph_sites", arr);
            m.SetShaderParameter("glyph_site_count", n);
        }
    }
}
