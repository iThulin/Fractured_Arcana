shader_type spatial;
// Two-sided toon light() (abs NdotL) with an ambient floor so petals never go
// black. If flowers render black on GL Compatibility, delete light() and add
// `diffuse_toon` to render_mode.
render_mode cull_disabled, specular_disabled;

// ============================================================
//  painterly_flower.gdshader
//
//  Companion to painterly_grass.gdshader for the flower-prop MultiMesh.
//  Shares the SAME wind model so flowers sway with the grass — assign the
//  grass's wind_noise texture and copy the grass wind values here.
//
//  ONE shader, used per mesh SURFACE (petal / stem / centre):
//    - Petal surface : use_flat_color OFF -> colour comes from the per-instance
//      palette (INSTANCE_CUSTOM), giving the random array variation.
//    - Stem / centre : use_flat_color ON  -> one flat_color, still swayed + lit
//      so the whole flower moves in one piece.
//
//  NOTE: inspector tooltips use /** ... */ doc comments (two leading asterisks)
//  placed directly above each uniform. Plain // comments are NOT shown on hover.
// ============================================================

// ---- Wind (mirror of the grass shader; copy values from the grass tuner) ----
group_uniforms wind;

/** Seamless noise texture that drives the wind. Use the SAME texture assigned to the grass material so flowers and grass ride the same gust. */
uniform sampler2D wind_noise : hint_default_black, repeat_enable, filter_linear;

/** World-space direction the wind blows, as a 2D vector on the ground plane (x, z). Match the grass material's value so both lean the same way. */
uniform vec2 wind_dir = vec2(1.0, 0.35);

/** How far the flower bends in the wind. Flowers are stiffer than grass, so this is usually a touch lower. Keep the wave_* values matched to the grass even if this differs, so the gust timing stays in phase. */
uniform float wind_bend : hint_range(0.0, 1.0) = 0.12;

/** Constant baseline lean added to every gust, so flowers never stand perfectly still. Higher = a permanent tilt into the wind. */
uniform float wind_amplitude : hint_range(0.0, 1.0) = 0.4;

/** Spatial size of the big rolling gust. Lower = broad gusts sweeping across the whole field; higher = tighter, choppier waves. Match the grass for a shared gust. */
uniform float wave_scale : hint_range(0.01, 1.0) = 0.12;

/** How fast the main gust scrolls across the field over time. Match the grass value to keep flowers and grass in sync. */
uniform float wave_speed : hint_range(0.0, 3.0) = 0.5;

/** Strength of the main rolling gust's contribution to the bend. The dominant motion term. */
uniform float wave_strength : hint_range(0.0, 2.0) = 0.7;

/** Stretches the gust pattern across the wind direction. Lower = long streaky gusts aligned to the wind; 1.0 = round, even patches. */
uniform float wave_stretch : hint_range(0.05, 1.0) = 0.35;

/** Spatial size of the fine secondary flutter layered on top of the main gust. Higher = smaller, busier ripples. */
uniform float detail_scale : hint_range(0.1, 6.0) = 1.2;

/** Scroll speed of the fine flutter layer. */
uniform float detail_speed : hint_range(0.0, 3.0) = 0.25;

/** Strength of the fine flutter layer. Small values add life without overpowering the main gust. */
uniform float detail_strength : hint_range(0.0, 1.0) = 0.25;

// ---- Colour (petal path) ----
group_uniforms color;

/** Petals only: lightens the colour toward the petal tips for a bit of form. 0 = flat colour, no tip highlight. */
uniform float tip_brighten : hint_range(0.0, 0.6) = 0.22;

/** Subtle per-flower brightness jitter so identical meshes don't look cloned. Applies to petals and (at half strength) flat surfaces. */
uniform float petal_variation : hint_range(0.0, 0.5) = 0.10;

/** Colour saturation. 1.0 = unchanged, >1 richer/more painterly, <1 muted. Applies to the whole flower. */
uniform float saturation : hint_range(0.0, 2.0) = 1.05;

/** Petal colour used ONLY when no per-instance custom data is supplied (e.g. previewing the mesh alone). In the live scatter the palette overrides this, so a yellow flower here means custom data isn't reaching the shader. */
uniform vec3 fallback_color : source_color = vec3(0.97, 0.84, 0.30);

// ---- Flat colour (stem / centre surfaces) ----
group_uniforms flat_fill;

/** Turn ON for the stem and centre surfaces: the surface renders one solid flat_color (ignoring the palette) but still sways and lights with the petals. Leave OFF for the petal surface so it uses the palette. */
uniform bool use_flat_color = false;

/** The solid colour used when use_flat_color is on. Green for stems, a dark tone for centres. On a gradient centre this is the OUTER (lighter) colour. */
uniform vec3 flat_color : source_color = vec3(0.20, 0.42, 0.16);

/** Radial darkening on the flat surface: blends flat_center_color in the middle out to flat_color at the edge. Turn ON for the flower CENTRE surface, OFF for stems. */
uniform bool flat_gradient = false;

/** Colour at the very centre of the flat surface (the dark heart). */
uniform vec3 flat_center_color : source_color = vec3(0.03, 0.02, 0.01);

/** Radius over which the centre colour fades out to flat_color. In UV units (UV mode) or mesh-local units (object mode). */
uniform float flat_gradient_radius : hint_range(0.02, 1.0) = 0.5;

/** Source of the radial distance. ON = UV distance from (0.5, 0.5) — works per-bloom in clusters, needs the centre disc UV-mapped centred. OFF = object-space distance from the mesh origin (single flowers only). */
uniform bool flat_gradient_uv_space = true;

// ---- Toon ----
group_uniforms toon;

/** Number of flat shading steps in the toon ramp. Higher = smoother gradient, lower = chunkier cel banding. */
uniform float toon_bands : hint_range(1.0, 6.0) = 3.0;

/** Softness of the edge between toon bands. 0 = hard cel edges, higher = blended steps. */
uniform float toon_softness : hint_range(0.0, 1.0) = 0.20;

/** Wraps light around the surface so shadowed sides aren't fully dark. Higher = flatter, softer lighting. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.25;

/** Minimum lit value floor. Guarantees shadowed petals keep their colour instead of going black. Raise if flowers read too dark in shadow. */
uniform float ambient_fill : hint_range(0.0, 1.0) = 0.40;

// ---- Stiffness (base=0 -> tip=1; drives sway weight + tip brighten) ----
group_uniforms stiffness;

/** Source of the base->tip weight. OFF = use the mesh UV.y (standard). ON = derive it from local height instead, for meshes without clean vertical UVs. */
uniform bool stiffness_from_model_height = false;

/** Mesh height used to normalise the height-based stiffness when stiffness_from_model_height is on. Set to the flower's local height. */
uniform float model_height : hint_range(0.05, 4.0) = 0.4;

varying float v_stiffness;
varying float v_jitter;
varying vec3  v_bloom;    // per-instance palette colour (from custom data)
varying float v_obj_r;    // mesh-local XZ distance from origin (object-space flat gradient)

float hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 345.45));
    p += dot(p, p + 34.345);
    return fract(p.x * p.y);
}

float toon_band(float x, float bands, float softness)
{
    x = clamp(x, 0.0, 1.0);
    float scaled = x * bands;
    float lower = floor(scaled);
    float f = scaled - lower;
    float soft = smoothstep(0.5 - softness * 0.5, 0.5 + softness * 0.5, f);
    return (lower + soft) / bands;
}

void vertex()
{
    float stiffness = stiffness_from_model_height
        ? clamp(VERTEX.y / max(model_height, 0.0001), 0.0, 1.0)
        : clamp(UV.y, 0.0, 1.0);
    v_stiffness = stiffness;

    // Object-space radius (before sway) for the flat gradient's object-space mode.
    v_obj_r = length(VERTEX.xz);

    v_bloom = INSTANCE_CUSTOM.rgb;

    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_jitter = hash21(floor(world_pos.xz * 7.0)) * 2.0 - 1.0;

    vec2 ndir = normalize(wind_dir);
    vec2 perp = vec2(-ndir.y, ndir.x);
    float along = dot(world_pos.xz, ndir);
    float across = dot(world_pos.xz, perp);

    vec2 wave_uv = vec2(along * wave_scale + TIME * wave_speed,
                        across * wave_scale * wave_stretch);
    float wave = texture(wind_noise, wave_uv).r * 2.0 - 1.0;

    vec2 detail_uv = vec2(along, across) * detail_scale + ndir * TIME * detail_speed;
    float detail = texture(wind_noise, detail_uv).r * 2.0 - 1.0;

    float gust = wave * wave_strength + detail * detail_strength + wind_amplitude;
    float sway = gust * wind_bend * stiffness * stiffness;
    VERTEX.x += ndir.x * sway;
    VERTEX.z += ndir.y * sway;
    VERTEX.y -= abs(sway) * 0.15 * stiffness;
}

void fragment()
{
    vec3 col;

    if (use_flat_color)
    {
        // Stem / centre surface: one colour (or a radial gradient), still swayed + lit.
        col = flat_color;
        if (flat_gradient)
        {
            float rdist = flat_gradient_uv_space
                ? length(UV - vec2(0.5))
                : v_obj_r;
            float t = smoothstep(0.0, 1.0, clamp(rdist / max(flat_gradient_radius, 0.0001), 0.0, 1.0));
            col = mix(flat_center_color, flat_color, t);
        }
        col *= 1.0 + v_jitter * petal_variation * 0.5;
    }
    else
    {
        // Petal surface: per-instance palette colour from custom data.
        vec3 bloom = v_bloom;
        if (dot(bloom, vec3(1.0)) < 0.003)
            bloom = fallback_color;

        col = bloom * (1.0 + v_jitter * petal_variation);
        col += vec3(tip_brighten) * smoothstep(0.45, 1.0, v_stiffness);
    }

    if (saturation != 1.0)
    {
        float luma = dot(col, vec3(0.299, 0.587, 0.114));
        col = mix(vec3(luma), col, saturation);
    }

    ALBEDO = clamp(col, 0.0, 1.0);
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
}

void light()
{
    vec3 N = normalize(NORMAL);
    float ndl = abs(dot(N, normalize(LIGHT)));
    ndl = (ndl + toon_wrap) / (1.0 + toon_wrap);
    float banded = max(toon_band(ndl, toon_bands, toon_softness), ambient_fill);
    DIFFUSE_LIGHT += banded * ATTENUATION * LIGHT_COLOR * ALBEDO;
}
