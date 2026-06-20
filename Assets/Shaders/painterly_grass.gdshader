shader_type spatial;
// Custom multi-band toon lighting (see light()), so NO diffuse_toon here.
// COMPATIBILITY FALLBACK: if grass renders black on the GL Compatibility
// backend, delete light() and add `diffuse_toon` to render_mode.
//
// alpha_to_coverage lets the distance-fade alpha resolve against MSAA samples
// (smooth, no transparency sorting). If your renderer ignores it and far grass
// pops instead of fading, flip `use_dither_fade` ON to use ordered dithering
// instead (works everywhere, costs a faint grain that distance hides).
render_mode cull_disabled, specular_disabled, alpha_to_coverage;

// ============================================================
//  painterly_grass.gdshader  (v6 — wind-driven mass tint)
//
//  Inspector tooltips use /** ... */ doc comments (two leading asterisks)
//  placed directly above each uniform. Plain // comments are NOT shown on hover.
//
//  Systems: multi-stop height gradient, world-space mass-tint clumping (x2,
//  wind-following), AO root-darkening, optional albedo + normal maps, two-layer
//  coherent world-space wind, multi-band toon light(), distance fade
//  (alpha_to_coverage / dither fallback), atmospheric depth tint, tip
//  translucency / backlight.
// ============================================================

// ---- Multi-stop height gradient ----
group_uniforms gradient;

/** Colour at the blade base (root). The darkest stop of the height gradient. */
uniform vec3  base_color : source_color = vec3(0.10, 0.34, 0.09);

/** Colour at mid-height. The dominant meadow green. */
uniform vec3  mid_color : source_color = vec3(0.32, 0.62, 0.18);

/** Colour at the blade tips. The lightest green before the highlight kicks in. */
uniform vec3  tip_color : source_color = vec3(0.65, 0.90, 0.35);

/** Near-white highlight blended into the very tips (above highlight_start) for sun-catch. */
uniform vec3  highlight_color : source_color = vec3(0.96, 1.00, 0.78);

/** Where the mid_color sits along the base->tip height (0 = base, 1 = tip). Shifts the gradient balance. */
uniform float mid_position : hint_range(0.05, 0.95) = 0.5;

/** Curves the height ramp. >1 pushes colour toward the base (greener lower down); <1 toward the tips. */
uniform float tip_bias : hint_range(0.5, 4.0) = 1.5;

/** Height (0..1) at which highlight_color begins blending in. Higher = highlight only at the very tips. */
uniform float highlight_start : hint_range(0.0, 1.0) = 0.78;

/** Per-blade brightness jitter (world-space hash) so blades don't look uniform. */
uniform float color_variation : hint_range(0.0, 0.3) = 0.07;

/** Saturation / vibrance. 1.0 = unchanged; push to ~1.3-1.5 for richer Ghibli greens. */
uniform float color_saturation : hint_range(0.0, 2.0) = 1.0;

// ---- Mass clumping (world-space, low frequency) ----
group_uniforms mass_clumping;

/** Enable the first mass tint — recolours whole world-space regions (clumps), not single blades. */
uniform bool      use_mass_tint = false;

/** Low-frequency noise defining the mass-tint regions. Sampled in world space so clumps stay put as the camera moves. */
uniform sampler2D mass_tex : hint_default_white, repeat_enable, filter_linear;

/** Colour multiplied into the darker patches of the mass field (tint 1). */
uniform vec3      mass_tint_color : source_color = vec3(0.50, 0.62, 0.42);

/** How strongly tint 1 recolours its patches. 0 = off. */
uniform float     mass_strength : hint_range(0.0, 1.0) = 0.55;

/** World-space scale of the mass pattern. Lower = larger clumps. */
uniform float     mass_scale : hint_range(0.005, 0.4) = 0.06;

/** Enable a second mass tint — same pattern offset to different patches, for more colour variety with no extra texture. */
uniform bool      use_mass_tint2 = false;

/** Colour for the second mass tint's patches. */
uniform vec3      mass_tint_color2 : source_color = vec3(0.50, 0.46, 0.26);

/** Strength of the second mass tint. */
uniform float     mass_strength2 : hint_range(0.0, 1.0) = 0.40;

/** Scrolls the mass-tint pattern along the wind with the SAME gust that bends the blades, so colour clumps roll across the field. 0 = static tint. */
uniform float     mass_wind_follow : hint_range(0.0, 1.0) = 0.0;

// ---- AO root-darkening ----
group_uniforms ambient_occlusion;

/** Darkens the blade roots for contact-shadow depth. 0 = off. */
uniform float ao_strength : hint_range(0.0, 1.0) = 0.45;

/** Height up the blade over which the root darkening fades out. */
uniform float ao_height : hint_range(0.0, 0.6) = 0.28;

// ---- Per-blade albedo / detail texture (multiply) ----
group_uniforms albedo_texture;

/** Multiply a detail/albedo texture over the blades. */
uniform bool      use_albedo_tex = false;

/** The detail texture, multiplied into the gradient colour. */
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;

/** Blend amount of the detail texture. 0 = none, 1 = full multiply. */
uniform float     albedo_tex_strength : hint_range(0.0, 1.0) = 0.5;

/** Sample the texture by object-space position instead of mesh UV (for instanced blades without unique UVs). */
uniform bool      albedo_tex_object_space = false;

/** Scale of the object-space texture sampling (only used when albedo_tex_object_space is on). */
uniform float     albedo_obj_scale : hint_range(0.1, 8.0) = 2.0;

// ---- Per-blade tangent-space normal map ----
group_uniforms normal_map;

/** Apply a tangent-space normal map. Requires mesh tangents (call GenerateTangents on the SurfaceTool). */
uniform bool      use_normal_map = false;

/** The tangent-space normal map. */
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;

/** Normal map intensity. */
uniform float     normal_strength : hint_range(0.0, 2.0) = 1.0;

// ---- Stiffness (root->tip weight) ----
group_uniforms stiffness;

/** Derive the base->tip weight from local mesh height instead of UV.y. Use for meshes without clean vertical UVs. */
uniform bool  stiffness_from_model_height = false;

/** Mesh height used to normalise height-based stiffness (when stiffness_from_model_height is on). */
uniform float model_height : hint_range(0.05, 4.0) = 1.0;

// ---- Wind: two-layer, world-space (coherent gusts sweeping in sections) ----
group_uniforms wind;

/** Seamless noise driving the wind. Use the SAME texture on the flower material so flowers and grass share the gust. */
uniform sampler2D wind_noise : hint_default_black, repeat_enable, filter_linear;

/** Wind direction on the ground plane (x, z). */
uniform vec2  wind_dir = vec2(1.0, 0.35);

/** How far blades bend in the wind. Keep the wave_* values matched across any props sharing the wind so they stay in phase. */
uniform float wind_bend : hint_range(0.0, 1.0) = 0.15;

/** Constant baseline lean added to every gust, so grass never stands perfectly still. */
uniform float wind_amplitude : hint_range(0.0, 1.0) = 0.4;

/** Spatial size of the big rolling gust. Lower = broad gusts sweeping the whole field. */
uniform float wave_scale : hint_range(0.01, 1.0) = 0.12;

/** Scroll speed of the main gust over time. */
uniform float wave_speed : hint_range(0.0, 3.0) = 0.5;

/** Strength of the main rolling gust (the dominant motion term). */
uniform float wave_strength : hint_range(0.0, 2.0) = 0.7;

/** Stretches the gust across the wind direction. Lower = long streaky gusts; 1.0 = round patches. */
uniform float wave_stretch : hint_range(0.05, 1.0) = 0.35;

/** Spatial size of the fine secondary flutter layered on the main gust. Higher = busier ripples. */
uniform float detail_scale : hint_range(0.1, 6.0) = 1.2;

/** Scroll speed of the fine flutter layer. */
uniform float detail_speed : hint_range(0.0, 3.0) = 0.25;

/** Strength of the fine flutter layer. */
uniform float detail_strength : hint_range(0.0, 1.0) = 0.25;

// ---- Toon lighting ----
group_uniforms toon;

/** Number of flat shading steps in the toon ramp. Higher = smoother, lower = chunkier cel banding. */
uniform float toon_bands : hint_range(1.0, 6.0) = 3.0;

/** Softness of the edge between toon bands. 0 = hard cel edges. */
uniform float toon_softness : hint_range(0.0, 1.0) = 0.12;

/** Wraps light around the blade so shadowed sides aren't pure black. Higher = flatter, softer light. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.25;

// ---- Distance fade (kills far-field blade crawl; reads as soft depth) ----
group_uniforms distance_fade;

/** Fade far blades out instead of letting them alias into shimmering noise. */
uniform bool  use_distance_fade = true;

/** Use ordered dithering instead of alpha_to_coverage. Turn ON if your backend ignores alpha_to_coverage and far grass pops instead of fading. */
uniform bool  use_dither_fade = false;

/** Camera distance (world units) where blades begin fading. */
uniform float fade_start : hint_range(1.0, 80.0) = 18.0;

/** Camera distance (world units) where blades are fully gone. */
uniform float fade_end : hint_range(1.0, 120.0) = 40.0;

// ---- Atmospheric depth tint (distant grass drifts toward haze) ----
group_uniforms atmospheric_depth;

/** Tint distant grass toward a haze colour for aerial recession. */
uniform bool  use_depth_tint = true;

/** Haze colour distant grass drifts toward. Match your fog/sky for the best recession. */
uniform vec3  depth_color : source_color = vec3(0.62, 0.68, 0.72);

/** Distance where the haze tint begins. */
uniform float depth_start : hint_range(0.0, 80.0) = 8.0;

/** Distance where the haze tint reaches full strength. */
uniform float depth_end : hint_range(0.0, 120.0) = 30.0;

/** Maximum haze blend amount. */
uniform float depth_strength : hint_range(0.0, 1.0) = 0.5;

// ---- Tip translucency / backlight (sun through the blades) ----
group_uniforms translucency;

/** Light passing through the blades toward the viewer — rim-glow when the sun is behind the grass. */
uniform bool  use_translucency = true;

/** Colour of the through-blade glow. */
uniform vec3  translucency_color : source_color = vec3(0.75, 0.95, 0.40);

/** Intensity of the backlight glow. */
uniform float translucency_strength : hint_range(0.0, 3.0) = 1.0;

/** Tightness of the glow rim. Higher = sharper, narrower rim. */
uniform float translucency_power : hint_range(1.0, 12.0) = 4.0;

/** How much the blade normal bends the through-light direction. */
uniform float translucency_distortion : hint_range(0.0, 1.0) = 0.3;

varying float v_stiffness;
varying float v_jitter;
varying vec2  v_world_xz;
varying vec2  v_obj_uv;
varying float v_view_dist;
varying float v_wave;          // signed coherent wind wave, shared with the mass tint
varying vec2  v_wind_ndir;     // normalized wind direction, shared with the mass tint

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

// Ordered 4x4 Bayer dither — stable in screen space, used only when
// use_dither_fade is on (alpha_to_coverage unavailable on the backend).
float dither_bayer(vec2 fc)
{
    int x = int(mod(fc.x, 4.0));
    int y = int(mod(fc.y, 4.0));
    int i = x + y * 4;
    float m[16] = float[](
         0.0,  8.0,  2.0, 10.0,
        12.0,  4.0, 14.0,  6.0,
         3.0, 11.0,  1.0,  9.0,
        15.0,  7.0, 13.0,  5.0);
    return (m[i] + 0.5) / 16.0;
}

void vertex()
{
    float stiffness = stiffness_from_model_height
        ? clamp(VERTEX.y / max(model_height, 0.0001), 0.0, 1.0)
        : clamp(UV.y, 0.0, 1.0);
    v_stiffness = stiffness;

    v_obj_uv = VERTEX.xy * albedo_obj_scale;

    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_xz = world_pos.xz;
    v_jitter = hash21(floor(world_pos.xz * 7.0)) * 2.0 - 1.0;

    // Camera distance (view space) for fade + depth tint.
    v_view_dist = length((VIEW_MATRIX * vec4(world_pos, 1.0)).xyz);

    // --- Two-layer world-space wind ---
    vec2 ndir = normalize(wind_dir);
    vec2 perp = vec2(-ndir.y, ndir.x);
    v_wind_ndir = ndir;

    float along = dot(world_pos.xz, ndir);
    float across = dot(world_pos.xz, perp);

    vec2 wave_uv = vec2(along * wave_scale + TIME * wave_speed,
                        across * wave_scale * wave_stretch);
    float wave = texture(wind_noise, wave_uv).r * 2.0 - 1.0;

    vec2 detail_uv = vec2(along, across) * detail_scale + ndir * TIME * detail_speed;
    float detail = texture(wind_noise, detail_uv).r * 2.0 - 1.0;

    float gust = wave * wave_strength + detail * detail_strength + wind_amplitude;

    // Moving part of the gust (excludes the constant lean) — drives the mass
    // tint so the colour wave travels with the same gust that bends the blades.
    v_wave = wave * wave_strength + detail * detail_strength;

    float sway = gust * wind_bend * stiffness * stiffness;
    VERTEX.x += ndir.x * sway;
    VERTEX.z += ndir.y * sway;
    VERTEX.y -= abs(sway) * 0.15 * stiffness;
}

void fragment()
{
    // 1. Multi-stop gradient.
    float h = pow(clamp(v_stiffness, 0.0, 1.0), tip_bias);
    vec3 col;
    if (h < mid_position)
    {
        col = mix(base_color, mid_color, h / max(mid_position, 0.0001));
    }
    else
    {
        col = mix(mid_color, tip_color, (h - mid_position) / max(1.0 - mid_position, 0.0001));
    }
    col = mix(col, highlight_color, smoothstep(highlight_start, 1.0, h));

    // 2. Per-blade brightness jitter.
    col *= 1.0 + v_jitter * color_variation;

    // 3. Mass clumping — world-space, whole regions, not single blades.
    //    The tint UV is pushed along the wind by the coherent gust (v_wave),
    //    so the darker/lighter patches roll across the meadow with the sway.
    vec2 mass_flow = v_wind_ndir * v_wave * mass_wind_follow;
    if (use_mass_tint)
    {
        float m = texture(mass_tex, v_world_xz * mass_scale + mass_flow).r;
        float darken = (1.0 - m) * mass_strength;
        col = mix(col, col * mass_tint_color, darken);
    }
    if (use_mass_tint2)
    {
        // Same texture + scale as tint 1 (so the pattern matches), offset to a
        // different region so this colour lands on its own patches.
        const vec2 mass2_offset = vec2(0.5, 0.37);
        float m2 = texture(mass_tex, v_world_xz * mass_scale + mass2_offset + mass_flow).r;
        float darken2 = (1.0 - m2) * mass_strength2;
        col = mix(col, col * mass_tint_color2, darken2);
    }

    // 3.5 Saturation / vibrance — push richness for the painterly look.
    if (color_saturation != 1.0)
    {
        float luma = dot(col, vec3(0.299, 0.587, 0.114));
        col = mix(vec3(luma), col, color_saturation);
    }

    // 4. AO root-darkening.
    float ao = mix(1.0 - ao_strength, 1.0, smoothstep(0.0, max(ao_height, 0.0001), v_stiffness));
    col *= ao;

    // 5. Albedo/detail texture (multiply).
    if (use_albedo_tex)
    {
        vec2 tuv = albedo_tex_object_space ? v_obj_uv : UV;
        vec3 t = texture(albedo_tex, tuv).rgb;
        col *= mix(vec3(1.0), t, albedo_tex_strength);
    }

    // 6. Atmospheric depth tint — distant grass recedes toward haze.
    if (use_depth_tint)
    {
        float dt = smoothstep(depth_start, depth_end, v_view_dist) * depth_strength;
        col = mix(col, depth_color, dt);
    }

    // Final albedo. COLOR.rgb is a per-instance/vertex tint hook (white = no-op).
    ALBEDO = col * COLOR.rgb;
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;

    // 7. Tangent-space normal map (sampled by UV; needs mesh tangents).
    if (use_normal_map)
    {
        NORMAL_MAP = texture(normal_tex, UV).rgb;
        NORMAL_MAP_DEPTH = normal_strength;
    }

    // 8. Distance fade — far blades fade instead of aliasing into noise.
    if (use_distance_fade)
    {
        float fade = 1.0 - smoothstep(fade_start, fade_end, v_view_dist);
        if (use_dither_fade)
        {
            if (fade < dither_bayer(FRAGCOORD.xy))
                discard;
            ALPHA = 1.0;
        }
        else
        {
            ALPHA = fade; // resolved by alpha_to_coverage against MSAA samples
        }
    }
    else
    {
        ALPHA = 1.0;
    }
}

void light()
{
    // Two-sided multi-band toon diffuse. Grass blades are thin, so light
    // whichever face is toward the sun: abs(NdotL) stops away-facing blades
    // (and backfaces under cull_disabled) from clamping to pure black — which
    // was turning the whole field dark wherever you weren't looking down-sun.
    vec3 N = normalize(NORMAL);
    float ndl = abs(dot(N, normalize(LIGHT)));
    ndl = (ndl + toon_wrap) / (1.0 + toon_wrap);
    float banded = toon_band(ndl, toon_bands, toon_softness);
    DIFFUSE_LIGHT += banded * ATTENUATION * LIGHT_COLOR * ALBEDO;

    // Tip translucency — light passing THROUGH the blade toward the viewer.
    // Strongest when the sun is behind the grass; weighted to the tips so the
    // meadow rim-glows when backlit. (All vectors are view-space in light().)
    if (use_translucency)
    {
        vec3 through = normalize(LIGHT + N * translucency_distortion);
        float back = pow(clamp(dot(VIEW, -through), 0.0, 1.0), translucency_power);
        DIFFUSE_LIGHT += back * translucency_strength * v_stiffness
                         * translucency_color * LIGHT_COLOR * ATTENUATION;
    }
}
