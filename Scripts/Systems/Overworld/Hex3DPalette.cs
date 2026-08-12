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

    /// <summary>Terrain enum → its authored PAINTERLY base colour (art pass A1,
    /// 2026-08-12). These are FINAL lit-scene swatches for the daylight rig (A4):
    /// muted, wide-range hues in the combat painterly register — no post-grade, no
    /// per-view saturation compensation (the old Grade()/×1.35 stack is deleted;
    /// lighting owns brightness, THESE own richness). Authored here rather than in
    /// UITheme on the SchoolColors/ElementColors precedent: this class IS the
    /// dedicated colour source for 3D terrain; UITheme's Terrain* set stays tuned
    /// for the unlit 2D fallback map. Readability law: every pair of swatches must
    /// stay tellable apart at whole-world zoom, and explored Desert must never be
    /// mistaken for unpainted canvas (CanvasUnseen 0.72/0.66/0.545 — Desert is
    /// deliberately more orange and more saturated).</summary>
    public static Color TerrainColor(TT t) => t switch
    {
        // A1b (screenshot tune): exposure verified correct — these read on screen at
        // authored value now, so richness is edited HERE, not in the lights. Greens
        // deepened/saturated (v1 was authored too grey); Snow pulled off pure white
        // and Mountain darkened so snowcaps, bare stone, and unpainted canvas stop
        // crowding each other at whole-world zoom.
        TT.Grassland => new Color(0.43f, 0.53f, 0.26f),   // meadow green, richer
        TT.Forest => new Color(0.21f, 0.36f, 0.18f),      // deep leaf green
        TT.Road => new Color(0.60f, 0.51f, 0.38f),        // worn earth track
        TT.Ruins => new Color(0.56f, 0.53f, 0.45f),       // weathered masonry
        TT.Mountain => new Color(0.49f, 0.45f, 0.41f),    // bare stone, darker + warmer
        TT.Swamp => new Color(0.30f, 0.36f, 0.22f),       // murk green
        TT.ArcaneGround => new Color(0.49f, 0.40f, 0.58f),// muted violet
        TT.Water => new Color(0.30f, 0.42f, 0.52f),       // fallback; real ocean via OceanColor
        TT.Hills => new Color(0.55f, 0.51f, 0.28f),       // dry olive-gold, more sat
        TT.Coast => new Color(0.68f, 0.64f, 0.46f),       // dune grass
        TT.Lake => new Color(0.33f, 0.46f, 0.53f),        // clear inland blue
        TT.Desert => new Color(0.79f, 0.60f, 0.36f),      // hot sand (kept off parchment)
        TT.Tundra => new Color(0.53f, 0.56f, 0.48f),      // cold sage
        TT.Snow => new Color(0.82f, 0.84f, 0.86f),        // snowfield, faint cool cast (off canvas + off pure white)
        TT.Marsh => new Color(0.52f, 0.55f, 0.32f),       // pale sedge, more sat
        _ => UITheme.Neutral,
    };

    /// <summary>Per-terrain jitter amplitude (art pass A1): the map-scale cousin of
    /// combat's per-blade jitter. Organic ground gets wider wobble so big biome
    /// fields read as painted masses with internal variation, not fill-tool flats;
    /// water stays calm; snow stays clean.</summary>
    public static float JitterAmp(in WorldTile t)
    {
        if (t.IsWater) return 0.02f;
        return t.Terrain switch
        {
            TT.Grassland or TT.Forest or TT.Swamp or TT.Marsh or TT.Hills => 0.055f,
            TT.Snow => 0.025f,
            _ => 0.04f,
        };
    }

    // ── Rivers & roads (art pass A9/A9b, 2026-08-12) ──

    /// <summary>River waterline — the ribbon's centre colour (A9b; deepened +
    /// saturated in A9c after "hard to see" — it must SEPARATE from olive ground,
    /// not harmonize with it).</summary>
    public static readonly Color RiverWater = new Color(0.25f, 0.41f, 0.58f);

    /// <summary>River bank — darker edge of the ribbon; the recessed-channel cue.</summary>
    public static readonly Color RiverBank = new Color(0.10f, 0.18f, 0.28f);

    /// <summary>Road stroke — a warm worn-earth line.</summary>
    public static readonly Color RoadStroke = new Color(0.56f, 0.47f, 0.34f);

    /// <summary>Kingdom-border ink (A7) — the dark drawn line where two realms
    /// meet on the painting.</summary>
    public static readonly Color BorderInk = new Color(0.16f, 0.13f, 0.12f);

    // ── Painterly discovery: the "unpainted world" (art pass A6, 2026-08-12) ──
    // Shared by BOTH 3D renderers so the discovery language can never drift between
    // the strategic map and the expedition window. (The per-view fog colors this
    // replaces were view-local dark-void lerps toward UITheme.StrategicCharted.)

    /// <summary>Charted/Silhouette ground as a flat UNDERPAINTING: a pale, heavily
    /// desaturated wash of the tile's real color pulled toward raw canvas. The terrain
    /// hue stays faintly readable (it is charted — the shape and kind are known), but
    /// the ground clearly hasn't been "painted in" by an expedition yet. Replaces the
    /// old dim-toward-dark treatment.</summary>
    public static Color Underpaint(Color c)
    {
        // A1b: the v1 wash (val→~0.68, 25% toward parchment) sat within a few
        // percent of CanvasUnseen (0.72) — charted/silhouette rings mushed into
        // the unpainted field, and a fresh expedition window read as one cream
        // sheet. The underpainting must sit clearly BELOW the canvas: a toned
        // wash on the paper, darker than the paper itself.
        c.ToHsv(out float hue, out float sat, out float val);
        Color washed = Color.FromHsv(hue, sat * 0.38f, Mathf.Lerp(val, 0.56f, 0.6f), 1f);
        return washed.Lerp(UITheme.CanvasUnseen, 0.15f);
    }

    /// <summary>Unseen/Hidden ground as raw canvas, with a deterministic per-tile
    /// paper-grain wobble. The wobble hashes ONLY the coordinate — it carries zero
    /// world data (terrain, height, contents never feed it), so it cannot be read as
    /// information; it just keeps a big unpainted field from rendering as one flat
    /// fill. <paramref name="wetEdge01"/> &gt; 0 darkens toward the watercolor
    /// edge-line tone where the canvas borders painted ground.</summary>
    public static Color CanvasTone(int col, int row, float wetEdge01 = 0f)
    {
        uint h = (uint)(col * 73856093) ^ (uint)(row * 19349663) ^ 0x9E3779B9u;
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        float grain = 1f + (((h & 1023u) / 1023f) - 0.5f) * 2f * 0.018f;
        Color c = UITheme.CanvasUnseen;
        c = new Color(
            Mathf.Clamp(c.R * grain, 0f, 1f),
            Mathf.Clamp(c.G * grain, 0f, 1f),
            Mathf.Clamp(c.B * grain, 0f, 1f), 1f);
        if (wetEdge01 > 0f)
            c = c.Lerp(UITheme.CanvasWetEdge, Mathf.Clamp(wetEdge01, 0f, 1f));
        return c;
    }

    /// <summary>Noise amount for the torn wet-edge blend on a boundary canvas tile:
    /// 0.12–0.38 by coordinate hash, so the painted world's edge reads as a torn
    /// watercolor boundary instead of a ruled line. Deterministic, data-free.</summary>
    public static float WetEdgeAmount(int col, int row)
    {
        uint h = (uint)(col * 40503) ^ (uint)(row * 20011) ^ 0x85EBCA6Bu;
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return 0.12f + ((h & 1023u) / 1023f) * 0.26f;
    }
}
