using Godot;

// ============================================================
// WorldAtlas3D.Clarity.cs: a calm sea, a sharp render, smooth names (2026-11-04)
//
// Purpose:        Section 7f of the provinces spec
//                 (docs/provinces_v1_7f_desk_and_hud.md), PR36 to PR38.
//                 The sea: the water shader's sky band (value noise
//                 sharpened into straight-edged panes) is off on the
//                 atlas's own water material, and its glints (hard-edged
//                 dabs, a speckle at map zoom) fade out as the camera
//                 pulls back. The render: the host may render the atlas at
//                 the window's own pixels, so the few places that count
//                 pointer pixels divide by RenderScale to count design
//                 pixels. The names: every label MakeLabel builds takes a
//                 copy of the project's font with mipmaps, so a name drawn
//                 smaller than its font size stays smooth.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (RebuildTiles hands over the water
//                 material; PlaceCamera calls UpdateSeaByZoom; the drag
//                 pan, the orbit and FlyToTile read RenderScale; MakeLabel
//                 takes MapLabelFont), WorldAtlas3D.Dispatch.cs (the
//                 screen pick reads RenderScale), StrategicView (sets
//                 RenderScale), PainterlyPrism (the water material)
// See:            docs/provinces_v1_7f_desk_and_hud.md
// ============================================================

public partial class WorldAtlas3D
{
    // ── The sea (PR36) ───────────────────────────────────────────────────

    /// <summary>The glints show in full at this zoom and closer, and are gone from
    /// SeaGlintGoneAt out. The whole-world view sits near 0.80.</summary>
    private const float SeaGlintFullAt = 0.10f;
    private const float SeaGlintGoneAt = 0.30f;

    /// <summary>The glints' strength up close: the water shader's own default.</summary>
    private const float SeaGlintStrength = 0.35f;

    private ShaderMaterial _seaMaterial;
    private float _seaGlintShown = -1f;

    /// <summary>From RebuildTiles: the water layer's material, the atlas's own (PainterlyPrism
    /// makes a new one per call, so the expedition window and the rivers keep theirs). The sky
    /// band goes for good; the glints follow the zoom. A flat fallback material (the painterly
    /// path off) has neither, and is left alone.</summary>
    private void AdoptSeaMaterial(Material material)
    {
        _seaMaterial = material as ShaderMaterial;
        _seaGlintShown = -1f;
        if (_seaMaterial == null)
            return;
        _seaMaterial.SetShaderParameter("sky_mix", 0.0f);
        UpdateSeaByZoom(CurrentZoom01());
    }

    /// <summary>From PlaceCamera: the glints' strength for this zoom, set only when it moves.</summary>
    private void UpdateSeaByZoom(float zoom01)
    {
        if (_seaMaterial == null)
            return;
        float close = Mathf.Clamp((SeaGlintGoneAt - zoom01) / (SeaGlintGoneAt - SeaGlintFullAt), 0f, 1f);
        float strength = SeaGlintStrength * close;
        if (Mathf.Abs(strength - _seaGlintShown) < 0.002f)
            return;
        _seaGlintShown = strength;
        _seaMaterial.SetShaderParameter("sparkle_strength", strength);
    }

    // ── The render scale (PR37) ──────────────────────────────────────────

    private float _renderScale = 1f;

    /// <summary>Render pixels per design pixel, set by the host when it renders the atlas at
    /// the window's own pixels (2 on a 4K screen with the 1920 by 1080 design). Pointer events
    /// arrive in render pixels; the pan, the orbit, the pick radii and FlyToTile's shift are
    /// tuned in design pixels, so they divide by this.</summary>
    public float RenderScale
    {
        get => _renderScale;
        set => _renderScale = Mathf.Clamp(value, 0.25f, 8f);
    }

    // ── The names (PR38) ─────────────────────────────────────────────────

    /// <summary>The project's default font (Assets/UI/GameTheme.tres).</summary>
    private const string MapLabelFontPath = "res://Assets/Fonts/Carlito-Regular.ttf";

    private static Font _mapLabelFont;
    private static bool _mapLabelFontTried;

    /// <summary>A copy of the project's font with mipmaps on, for the map's names. The imported
    /// font has none, and a 42-point name drawn 22 pixels tall at the whole-world view was
    /// minified without them. Built once; null (the default font, as before) if the font will
    /// not load.</summary>
    private static Font MapLabelFont()
    {
        if (_mapLabelFontTried)
            return _mapLabelFont;
        _mapLabelFontTried = true;
        var source = ResourceLoader.Exists(MapLabelFontPath) ? GD.Load<FontFile>(MapLabelFontPath) : null;
        if (source == null)
            return null;
        if (source.Duplicate() is not FontFile copy)
            return null;
        copy.GenerateMipmaps = true;
        _mapLabelFont = copy;
        return _mapLabelFont;
    }
}
