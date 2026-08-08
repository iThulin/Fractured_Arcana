using Godot;

// ============================================================
// Hex3DPalette.cs
//
// Purpose:        The shared terrain-color palette for the 3D hex
//                 renderers. Extracted from the byte-identical copies
//                 that WorldAtlas3D (Atlas tab) and ExpeditionWindow3D
//                 (expedition view) each carried — change a terrain's
//                 look here and BOTH 3D views update in lockstep.
//
//                 Deliberately narrow: only the three helpers that were
//                 PROVABLY identical across both renderers live here —
//                 the terrain→color switch, the water dissolve, and the
//                 lit-scene grade. The per-tile Hash/Jitter noise was
//                 left in each renderer on purpose: their salts and bit
//                 masks differ (WorldAtlas3D masks &1023, the window
//                 uses &0xFFFF via a salted hash), so unifying them would
//                 visibly change one view's texture for no real gain.
//                 The 2D StrategicView keeps its own copy — it renders
//                 unlit quads, and whether it retires is CONDITIONAL: it
//                 stays unless/until the 3D atlas (WorldAtlas3D) can be
//                 scaled to show enough of the map to serve as the
//                 strategic view. Folding its unlit-tuned palette in here
//                 now would couple a maybe-dying path to the live ones.
// Layer:          UI (rendering support)
// Collaborators:  UITheme (the authored color source), WorldTile,
//                 OverworldHex (TerrainType), WorldAtlas3D +
//                 ExpeditionWindow3D (the two callers)
// See:            docs/atlas_expedition_convergence_v1.md (housekeeping —
//                 shared-palette extraction)
// ============================================================

using TT = OverworldHex.TerrainType;

/// <summary>Shared terrain palette for the 3D hex renderers (Atlas tab + expedition
/// window). The single home for "what colour is this terrain in 3D," so the two
/// views can never drift apart. Fog handling, per-tile jitter, and POI colours stay
/// with each renderer (they differ by view); only the terrain base colour, the ocean
/// dissolve, and the lit-scene grade are common — and those live here.</summary>
public static class Hex3DPalette
{
    /// <summary>Base terrain colour before fog/grade/jitter. Water dissolves toward the
    /// void on a fast ramp so open sea fades into the dark background instead of ending
    /// in a hard bright rectangle (the "map floating in the void" look); land is the
    /// flat authored terrain colour. Callers apply grading (land) and jitter.</summary>
    public static Color TerrainColorOf(in WorldTile t)
    {
        if (t.Terrain != TT.Water)
            return TerrainColor(t.Terrain);
        Color c = UITheme.OceanColor(t.OceanDepth);
        float dissolve = Mathf.Clamp(t.OceanDepth / 14f, 0f, 1f) * 0.65f;
        return c.Lerp(UITheme.WorldDeep, dissolve);
    }

    /// <summary>Terrain enum → its authored base colour from UITheme.</summary>
    public static Color TerrainColor(TT t) => t switch
    {
        TT.Grassland => UITheme.TerrainGrassland,
        TT.Forest => UITheme.TerrainForest,
        TT.Road => UITheme.TerrainRoad,
        TT.Ruins => UITheme.TerrainRuins,
        TT.Mountain => UITheme.TerrainMountain,
        TT.Swamp => UITheme.TerrainSwamp,
        TT.ArcaneGround => UITheme.TerrainArcaneGround,
        TT.Volcanic => UITheme.TerrainVolcanic,
        TT.Water => UITheme.TerrainWater,
        TT.Hills => UITheme.TerrainHills,
        TT.Coast => UITheme.TerrainCoast,
        TT.Lake => UITheme.TerrainLake,
        TT.Desert => UITheme.TerrainDesert,
        TT.Tundra => UITheme.TerrainTundra,
        TT.Snow => UITheme.TerrainSnow,
        TT.Marsh => UITheme.TerrainMarsh,
        _ => UITheme.Neutral,
    };

    /// <summary>Lit-scene compensation for a palette tuned on unlit 2D quads:
    /// saturation +12%, value +2% (lighting owns brightness, grading owns richness).
    /// Land only — callers gate on IsLand.</summary>
    public static Color Grade(Color c)
    {
        c.ToHsv(out float hue, out float sat, out float val);
        return Color.FromHsv(hue, Mathf.Clamp(sat * 1.12f, 0f, 1f),
                             Mathf.Clamp(val * 1.02f, 0f, 1f), c.A);
    }
}
