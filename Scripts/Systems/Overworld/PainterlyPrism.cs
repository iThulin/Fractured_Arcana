using Godot;

// ============================================================
// PainterlyPrism.cs — art pass A2/A3-lite (2026-08-12)
//
// Purpose:        The single factory for the 3D world-map tile
//                 materials, shared by WorldAtlas3D and
//                 ExpeditionWindow3D (same rule as Hex3DPalette:
//                 one home, the two views can never drift).
//                 Returns a ShaderMaterial on
//                 painterly_world_prism.gdshader — or, with
//                 Enabled = false (the instant kill-switch the
//                 art pass plan requires) or a failed shader
//                 load, the pre-A2 StandardMaterial3D fallback.
// Layer:          UI (rendering support)
// Collaborators:  painterly_world_prism.gdshader, WorldAtlas3D,
//                 ExpeditionWindow3D
// ============================================================

/// <summary>Material factory for the painterly world-tile prisms. Modes match the
/// shader: 0 = land, 1 = water, 2 = canvas (unpainted world).</summary>
public static class PainterlyPrism
{
    public const int Land = 0;
    public const int Water = 1;
    public const int Canvas = 2;

    /// <summary>Kill-switch: false restores the pre-A2 StandardMaterial3D look
    /// everywhere the factory is used. Flip here (or from a debug hook) — the
    /// tile layers rebuild their materials on every RebuildTiles.</summary>
    public static bool Enabled = true;

    private static Shader _shader;
    private static bool _loadFailed;

    /// <summary>A material for one tile layer. Falls back to the pre-A2
    /// StandardMaterial3D (vertex-colour albedo at the given roughness) when the
    /// painterly path is disabled or the shader fails to load — never crashes
    /// the map over an art asset.</summary>
    public static Material TileMaterial(int mode, float fallbackRoughness)
    {
        if (Enabled && !_loadFailed)
        {
            if (_shader == null)
            {
                _shader = GD.Load<Shader>("res://Assets/Shaders/painterly_world_prism.gdshader");
                if (_shader == null)
                {
                    _loadFailed = true;
                    GD.PushWarning("PainterlyPrism: shader load failed; using StandardMaterial3D fallback.");
                }
            }
            if (_shader != null)
            {
                var m = new ShaderMaterial { Shader = _shader };
                m.SetShaderParameter("mode", mode);
                return m;
            }
        }
        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = fallbackRoughness,
        };
    }

    private static Material _riverMat;

    /// <summary>Material for the A9b river ribbons: the water shader retuned for a
    /// thin flat ribbon lying on terrain — no vertex swell (a displaced ribbon
    /// would poke through its banks), smaller/subtler sky wash, finer sparkle.
    /// Vertex colours (bank/waterline baked by RiverMesh) flow through COLOR
    /// exactly like MultiMesh instance colours do.</summary>
    public static Material RiverMaterial()
    {
        if (_riverMat != null) return _riverMat;
        var m = TileMaterial(Water, 0.6f);
        if (m is ShaderMaterial sm)
        {
            sm.SetShaderParameter("swell_amplitude", 0.0f);
            sm.SetShaderParameter("sky_mix", 0.18f);
            sm.SetShaderParameter("sparkle_scale", 7.0f);
            sm.SetShaderParameter("sparkle_strength", 0.25f);
        }
        _riverMat = m;
        return _riverMat;
    }
}
