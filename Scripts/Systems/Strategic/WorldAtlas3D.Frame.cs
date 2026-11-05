using Godot;

// ============================================================
// WorldAtlas3D.Frame.cs: the map's frame, on its desk (2026-11-04)
//
// Purpose:        Section 7e of the provinces spec
//                 (docs/provinces_v1_7e_map_polish.md), PR31, as changed
//                 by section 7f (docs/provinces_v1_7f_desk_and_hud.md),
//                 PR39. The whole-world view fits the framed world into
//                 the part of the screen the host's panels leave free,
//                 with the camera's pitch counted, and the camera's centre
//                 of view sits in the middle of that free screen at every
//                 zoom. The world lies in a wooden frame with a gold fillet
//                 along its inner edge; the frame covers the hex teeth at
//                 the world's rim (always sea: ContinentShaper rings every
//                 world in ocean). Under it and far past it lies a dark
//                 wooden desk. 7e's paper sheet is gone.
//
//                 The desk and the frame are procedural placeholders. Each
//                 has a texture slot (DeskTexturePath, FrameTexturePath):
//                 a path there replaces the drawn grain with painted art.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (OverviewDist calls FitWorldDist;
//                 PlaceCamera calls PitchDegFor and ApplyViewCentre;
//                 RebuildTiles calls RebuildDeskAndFrame; the environment
//                 takes DeskVoid), StrategicView.Dispatch.cs (calls
//                 SetViewInsets)
// See:            docs/provinces_v1_7f_desk_and_hud.md
// ============================================================

public partial class WorldAtlas3D
{
    // ── Tuning (sections 7e and 7f) ──────────────────────────────────────

    /// <summary>Margin around the framed world in the whole-world view, as a share of the
    /// fitted size: the 4 percent the fit always had.</summary>
    private const float OverviewPad = 1.04f;

    /// <summary>The frame's width in world units: about 18 pixels at the whole-world view
    /// on a 1080-pixel screen.</summary>
    private const float FrameWidth = 3f;

    /// <summary>The frame's top. The sea at the world's rim stands 0.08 high, so the frame
    /// rises a little over it and throws a short shadow onto the map's margin.</summary>
    private const float FrameTop = 0.5f;

    /// <summary>How far the frame's inner edge reaches inside the line where the rim's tiles
    /// stop being toothed, so no sliver of desk shows between the frame and the sea.</summary>
    private const float FrameOverlap = 0.25f;

    /// <summary>The gold fillet along the frame's inner edge: its width, and how far it
    /// stands above the frame's top.</summary>
    private const float FilletWidth = 0.32f;
    private const float FilletRise = 0.05f;

    /// <summary>The desk's half width in world units: far past anything the camera shows
    /// before its far plane cuts in.</summary>
    private const float DeskHalf = 3000f;

    /// <summary>The desk lies just under the tiles' feet (every prism stands on y 0), and the
    /// frame stands on it.</summary>
    private const float DeskY = -0.02f;

    /// <summary>The background past the far plane: the desk's darkest wood, so a view that
    /// reaches that far ends in shadow, not a band of colour.</summary>
    private static readonly Color DeskVoid = new Color(0.09f, 0.06f, 0.045f);

    /// <summary>Texture slots for painted art. Empty: the procedural grain.</summary>
    private const string DeskTexturePath = "";
    private const string FrameTexturePath = "";

    private static readonly Color FilletGold = new Color(0.70f, 0.55f, 0.24f);

    // ── State ────────────────────────────────────────────────────────────

    /// <summary>The screen the host's panels cover, as shares of the view: left, top, right
    /// and bottom. Zero until the host says (SetViewInsets).</summary>
    private float _insetLeft, _insetTop, _insetRight, _insetBottom;

    private MeshInstance3D _desk;
    private Node3D _mapFrame;
    private static Shader _deskShader;
    private static Shader _frameShader;

    // ── The free screen ──────────────────────────────────────────────────

    /// <summary>The host's panels, as shares of the screen (each held to 0.45 at most). The
    /// camera's centre of view moves to the middle of what they leave free, and the
    /// whole-world view fits the framed world inside it. Re-places the camera at once; the
    /// zoom is left alone.</summary>
    public void SetViewInsets(float left, float top, float right, float bottom)
    {
        _insetLeft = Mathf.Clamp(left, 0f, 0.45f);
        _insetTop = Mathf.Clamp(top, 0f, 0.45f);
        _insetRight = Mathf.Clamp(right, 0f, 0.45f);
        _insetBottom = Mathf.Clamp(bottom, 0f, 0.45f);
        if (_camera != null && _world != null)
            PlaceCamera();
    }

    /// <summary>The pitch the camera takes at a distance: the player's own pitch if they set
    /// one, else steep far out and shallower close in. PlaceCamera and the whole-world fit
    /// both read it, so the fit always counts the pitch the camera will have.</summary>
    private float PitchDegFor(float camDist)
        => _pitchOverrideDeg >= 0f
            ? _pitchOverrideDeg
            : Mathf.Lerp(40f, 68f, Mathf.InverseLerp(CamDistMin, CamDistMax, camDist));

    /// <summary>The camera distance at which the framed world fits the free screen. The
    /// camera looks at WorldCenter, so the fit takes the frame's farther side from it on
    /// each axis. The world's north to south depth shows on screen shortened by the sine of
    /// the pitch, and the pitch follows the distance, so the fit settles over a few rounds
    /// (it moves by a fraction of a unit after the third).</summary>
    private float FitWorldDist(float aspect)
    {
        Rect2 outer = FrameInnerRect().Grow(FrameWidth);
        Vector3 centre = WorldCenter();
        float w = 2f * Mathf.Max(centre.X - outer.Position.X, outer.End.X - centre.X);
        float h = 2f * Mathf.Max(centre.Z - outer.Position.Y, outer.End.Y - centre.Z);
        float freeW = Mathf.Max(0.1f, 1f - _insetLeft - _insetRight);
        float freeH = Mathf.Max(0.1f, 1f - _insetTop - _insetBottom);
        float dist = CamDistMax;
        for (int i = 0; i < 6; i++)
        {
            float sinPitch = Mathf.Sin(Mathf.DegToRad(PitchDegFor(dist)));
            float viewH = Mathf.Max(h * sinPitch / freeH, w / (Mathf.Max(0.1f, aspect) * freeW)) * OverviewPad;
            dist = Mathf.Clamp(viewH / OrthoSizeFactor, CamDistMin, CamDistMax);
        }
        return dist;
    }

    /// <summary>From PlaceCamera: shift the view so the camera's target shows in the middle
    /// of the free screen. Camera3D's own offsets move the picture, the picking rays and
    /// UnprojectPosition together, so pointing and clicking stay exact.</summary>
    private void ApplyViewCentre()
    {
        if (_camera == null)
            return;
        float viewH = _orthographic
            ? _camera.Size
            : 2f * _camDist * Mathf.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f);
        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        float aspect = vp.Y > 1f ? vp.X / vp.Y : 1.7778f;
        // The free centre lies right of the screen's by half the left inset less half the
        // right one, and below it by half the top inset less half the bottom one.
        _camera.HOffset = -(_insetLeft - _insetRight) * 0.5f * viewH * aspect;
        _camera.VOffset = (_insetTop - _insetBottom) * 0.5f * viewH;
    }

    // ── The frame's rectangle ────────────────────────────────────────────

    /// <summary>The frame's inner edge on the ground, as x and z (Rect2's y is the world's
    /// z). Flat-top hexes with odd columns pushed half a row south leave teeth at the rim:
    /// west and east between half a radius and a radius out from the outer columns' centres,
    /// north between the even and the odd columns' top edges, south between their bottom
    /// edges. Inside those lines the tiles leave no gap, so the frame starts there, plus
    /// FrameOverlap.</summary>
    private Rect2 FrameInnerRect()
    {
        float x0 = -HexR * 0.5f + FrameOverlap;
        float x1 = (_world.Width - 1) * ColSpacing + HexR * 0.5f - FrameOverlap;
        float z0 = FrameOverlap;
        float z1 = (_world.Height - 1) * RowSpacing + RowSpacing * 0.5f - FrameOverlap;
        return new Rect2(x0, z0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, z1 - z0));
    }

    // ── The desk and the frame ───────────────────────────────────────────

    /// <summary>From RebuildTiles, beside the tile layers: the desk under the world and far
    /// past it, and the frame around the world. Rebuilt with the tiles, since the frame
    /// follows the world's size.</summary>
    private void RebuildDeskAndFrame()
    {
        if (_desk != null && IsInstanceValid(_desk))
            _desk.QueueFree();
        if (_mapFrame != null && IsInstanceValid(_mapFrame))
            _mapFrame.QueueFree();
        _desk = null;
        _mapFrame = null;
        if (_world == null)
            return;
        Rect2 inner = FrameInnerRect();
        Rect2 outer = inner.Grow(FrameWidth);
        BuildDesk(outer);
        BuildMapFrame(inner, outer);
    }

    /// <summary>The desk: one plane, unshaded, so the sun's shadow map never stretches over
    /// it (the canvas layer's old acne). The frame's contact shadow and the darkening toward
    /// the screen's edges are drawn by its shader.</summary>
    private void BuildDesk(Rect2 outer)
    {
        var material = new ShaderMaterial { Shader = DeskShader() };
        material.SetShaderParameter("frame_outer",
            new Vector4(outer.Position.X, outer.Position.Y, outer.End.X, outer.End.Y));
        ApplyWoodTexture(material, DeskTexturePath);
        Vector3 centre = WorldCenter();
        _desk = new MeshInstance3D
        {
            Name = "Desk",
            Mesh = new PlaneMesh { Size = new Vector2(DeskHalf * 2f, DeskHalf * 2f) },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(centre.X, DeskY, centre.Z),
        };
        AddChild(_desk);
    }

    /// <summary>The frame: four bars standing on the desk (north and south run the full
    /// width, west and east fit between them), each with the grain along its length, and a
    /// gold fillet on the inner edge. The bars take the sun and throw its shadow; the fillets
    /// throw none.</summary>
    private void BuildMapFrame(Rect2 inner, Rect2 outer)
    {
        _mapFrame = new Node3D { Name = "MapFrame" };
        AddChild(_mapFrame);

        var alongX = FrameMaterial(false);
        var alongZ = FrameMaterial(true);
        var gold = new StandardMaterial3D { AlbedoColor = FilletGold, Roughness = 0.45f };

        float ix0 = inner.Position.X, iz0 = inner.Position.Y, ix1 = inner.End.X, iz1 = inner.End.Y;
        float ox0 = outer.Position.X, oz0 = outer.Position.Y, ox1 = outer.End.X, oz1 = outer.End.Y;

        AddFramePart("North", ox0, ox1, DeskY, FrameTop, oz0, iz0, alongX, true);
        AddFramePart("South", ox0, ox1, DeskY, FrameTop, iz1, oz1, alongX, true);
        AddFramePart("West", ox0, ix0, DeskY, FrameTop, iz0, iz1, alongZ, true);
        AddFramePart("East", ix1, ox1, DeskY, FrameTop, iz0, iz1, alongZ, true);

        float f = FilletWidth;
        float fy0 = FrameTop - 0.01f, fy1 = FrameTop + FilletRise;
        AddFramePart("FilletNorth", ix0 - f, ix1 + f, fy0, fy1, iz0 - f, iz0, gold, false);
        AddFramePart("FilletSouth", ix0 - f, ix1 + f, fy0, fy1, iz1, iz1 + f, gold, false);
        AddFramePart("FilletWest", ix0 - f, ix0, fy0, fy1, iz0, iz1, gold, false);
        AddFramePart("FilletEast", ix1, ix1 + f, fy0, fy1, iz0, iz1, gold, false);
    }

    /// <summary>One box of the frame, from its two corners.</summary>
    private void AddFramePart(string name, float x0, float x1, float y0, float y1, float z0, float z1,
                              Material material, bool castsShadow)
    {
        var size = new Vector3(Mathf.Max(0.01f, x1 - x0), Mathf.Max(0.01f, y1 - y0), Mathf.Max(0.01f, z1 - z0));
        _mapFrame.AddChild(new MeshInstance3D
        {
            Name = name,
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material,
            CastShadow = castsShadow
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f),
        });
    }

    private static ShaderMaterial FrameMaterial(bool grainAlongZ)
    {
        var material = new ShaderMaterial { Shader = FrameShader() };
        material.SetShaderParameter("grain_along_z", grainAlongZ);
        ApplyWoodTexture(material, FrameTexturePath);
        return material;
    }

    /// <summary>A texture slot filled: the shader samples it instead of drawing its grain.
    /// A path that does not load leaves the grain.</summary>
    private static void ApplyWoodTexture(ShaderMaterial material, string path)
    {
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
            return;
        var texture = GD.Load<Texture2D>(path);
        if (texture == null)
            return;
        material.SetShaderParameter("wood_texture", texture);
        material.SetShaderParameter("use_texture", true);
    }

    private static Shader DeskShader() => _deskShader ??= new Shader { Code = DeskShaderCode };
    private static Shader FrameShader() => _frameShader ??= new Shader { Code = FrameShaderCode };

    // Planks run east to west, plank_width apart, each board board_length long with its
    // joints staggered. The grain is rings bent by noise along the board, with long
    // streaks over them; seams and joints darken over a pixel or so. Past the frame's
    // outer edge the desk darkens over contact_width (the frame standing on it), and
    // toward the screen's edges by vignette.
    private const string DeskShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled;

uniform vec3 wood_dark : source_color = vec3(0.150, 0.094, 0.063);
uniform vec3 wood_mid : source_color = vec3(0.235, 0.153, 0.102);
uniform vec3 wood_light : source_color = vec3(0.320, 0.216, 0.145);
uniform float plank_width = 12.0;
uniform float board_length = 70.0;
uniform float grain_per_unit = 0.85;
uniform bool use_texture = false;
uniform sampler2D wood_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform float texture_repeat = 0.02;
uniform vec4 frame_outer = vec4(0.0, 0.0, 1.0, 1.0);
uniform float contact_width = 2.5;
uniform float contact_strength = 0.55;
uniform float vignette = 0.5;

varying vec3 v_world;

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 345.45));
    p += dot(p, p + 34.345);
    return fract(p.x * p.y);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void vertex() {
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 p = v_world.xz;
    vec3 col;
    if (use_texture) {
        col = texture(wood_texture, p * texture_repeat).rgb;
    } else {
        float row = p.y / plank_width;
        float plank = floor(row);
        float across = fract(row);
        float warp = vnoise(vec2(p.x * 0.035, plank * 7.13)) * 5.0
                   + vnoise(vec2(p.x * 0.11, plank * 3.71 + 9.0)) * 1.4;
        float rings = sin((across * plank_width + warp) * grain_per_unit * 6.2831853);
        float streak = vnoise(vec2(p.x * 0.018 + plank * 17.0, p.y * 1.7));
        float t = clamp(0.5 + 0.26 * rings + 0.32 * (streak - 0.5), 0.0, 1.0);
        col = t < 0.5 ? mix(wood_dark, wood_mid, t * 2.0) : mix(wood_mid, wood_light, (t - 0.5) * 2.0);
        col *= 1.0 + (hash21(vec2(plank, 3.7)) - 0.5) * 0.16;
        float seam = min(across, 1.0 - across) * plank_width;
        float run = fract((p.x + hash21(vec2(plank, 11.0)) * board_length) / board_length);
        float joint = min(run, 1.0 - run) * board_length;
        float aa = max(fwidth(p.x) + fwidth(p.y), 0.0001);
        col *= mix(0.42, 1.0, smoothstep(0.03, 0.03 + aa * 1.5, seam));
        col *= mix(0.5, 1.0, smoothstep(0.03, 0.03 + aa * 1.5, joint));
    }
    vec2 past = max(frame_outer.xy - p, p - frame_outer.zw);
    float outside = length(max(past, vec2(0.0)));
    col *= 1.0 - contact_strength * (1.0 - smoothstep(0.0, contact_width, outside));
    vec2 s = SCREEN_UV * 2.0 - 1.0;
    col *= clamp(1.0 - vignette * 0.5 * dot(s, s), 0.45, 1.0);
    ALBEDO = col;
}
";

    // The frame's bars: lit, so the sun shades them and they cast. The grain runs along each
    // bar (grain_along_z for the west and east bars); the height folds into the across
    // coordinate so the bars' sides carry it too.
    private const string FrameShaderCode = @"
shader_type spatial;

uniform vec3 wood_dark : source_color = vec3(0.17, 0.10, 0.06);
uniform vec3 wood_light : source_color = vec3(0.41, 0.26, 0.16);
uniform bool grain_along_z = false;
uniform float grain_per_unit = 1.2;
uniform float roughness_value = 0.7;
uniform bool use_texture = false;
uniform sampler2D wood_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform float texture_repeat = 0.25;

varying vec3 v_world;

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 345.45));
    p += dot(p, p + 34.345);
    return fract(p.x * p.y);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void vertex() {
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 p = grain_along_z ? vec2(v_world.z, v_world.x + v_world.y) : vec2(v_world.x, v_world.z + v_world.y);
    vec3 col;
    if (use_texture) {
        col = texture(wood_texture, p * texture_repeat).rgb;
    } else {
        float warp = vnoise(vec2(p.x * 0.08, 3.1)) * 2.5 + vnoise(vec2(p.x * 0.3, 7.7)) * 0.6;
        float rings = sin((p.y + warp) * grain_per_unit * 6.2831853);
        float streak = vnoise(vec2(p.x * 0.05, p.y * 4.0));
        float t = clamp(0.5 + 0.3 * rings + 0.3 * (streak - 0.5), 0.0, 1.0);
        col = mix(wood_dark, wood_light, t);
    }
    ALBEDO = col;
    ROUGHNESS = roughness_value;
    SPECULAR = 0.2;
}
";
}
