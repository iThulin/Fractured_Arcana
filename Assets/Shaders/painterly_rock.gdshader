shader_type spatial;
// Solid toon-shaded rock. cull_back since rocks are closed meshes — if a rock
// shows black/inside-out faces, its normals are flipped (fix in Blender) or
// switch to cull_disabled.
//
// Inspector tooltips use /** ... */ doc comments (two leading asterisks).
render_mode cull_back, specular_disabled;

// ============================================================
//  painterly_rock.gdshader
//
//  Stylized rock-prop shader for the rock-scatter MultiMesh. Shares the
//  toon-lighting family with the grass/flower shaders — NO wind (rocks don't
//  sway). Flat cel banding, a lighter/mossy "top tint" on upward faces, contact
//  darkening at the base, optional world-space mottle to break flat colour, and
//  optional per-instance tint via custom data (set by the scatter).
// ============================================================

// ---- Base colour ----
group_uniforms color;

/** Base rock colour. The per-instance tint (if on) multiplies this. */
uniform vec3 rock_base : source_color = vec3(0.44, 0.42, 0.40);

/** Per-instance brightness jitter (world-space hash) so identical meshes vary. */
uniform float rock_variation : hint_range(0.0, 0.5) = 0.10;

/** Saturation. 1.0 = unchanged, >1 richer, <1 muted. */
uniform float saturation : hint_range(0.0, 2.0) = 1.0;

/** Multiply rock_base by the per-instance palette tint from custom data (set by the scatter). */
uniform bool use_instance_tint = true;

/** Tint used if no custom data is supplied (e.g. previewing the mesh alone). */
uniform vec3 fallback_tint : source_color = vec3(1.0, 1.0, 1.0);

// ---- Top tint (dust / moss on upward faces) ----
group_uniforms top_tint;

/** Tint upward-facing surfaces toward top_color — dusty light or mossy green for weathered rock. */
uniform bool use_top_tint = true;

/** Colour blended onto the tops. Mossy green or pale dust. */
uniform vec3 top_color : source_color = vec3(0.50, 0.54, 0.34);

/** Strength of the top tint. 0 = off. */
uniform float top_strength : hint_range(0.0, 1.0) = 0.35;

/** How flat a face must be to count as "up". Higher = only near-horizontal tops get the tint. */
uniform float top_sharpness : hint_range(1.0, 8.0) = 2.5;

// ---- Base contact darkening ----
group_uniforms base_shade;

/** Darken the rock near its base where it meets the ground (fake contact shadow). */
uniform float base_darken : hint_range(0.0, 1.0) = 0.30;

/** Mesh-local height over which the base darkening fades out. */
uniform float base_height : hint_range(0.01, 2.0) = 0.25;

// ---- Mottle (world-space colour break-up) ----
group_uniforms mottle;

/** Break up flat rock colour with a soft world-space value variation. */
uniform bool use_mottle = false;

/** Seamless noise for the mottle. */
uniform sampler2D mottle_tex : hint_default_white, repeat_enable, filter_linear;

/** World-space scale of the mottle. Higher = finer. */
uniform float mottle_scale : hint_range(0.05, 4.0) = 1.0;

/** Strength of the mottle value variation. */
uniform float mottle_strength : hint_range(0.0, 0.6) = 0.15;

// ---- Normal map (optional; bold/large detail reads under toon, fine washes out) ----
group_uniforms normal_map;
uniform bool use_normal_map = false;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float normal_strength : hint_range(0.0, 2.0) = 1.0;

// ---- Toon ----
group_uniforms toon;

/** Number of flat shading steps. */
uniform float toon_bands : hint_range(1.0, 6.0) = 3.0;

/** Softness of the band edges. 0 = hard cel. */
uniform float toon_softness : hint_range(0.0, 1.0) = 0.15;

/** Wraps light around so the shadowed side isn't pure black. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.25;

/** Floor on the lit value so shadowed faces keep some colour. */
uniform float ambient_fill : hint_range(0.0, 1.0) = 0.30;

varying vec3  v_world_normal;
varying float v_local_y;
varying vec2  v_world_xz;
varying float v_jitter;
varying vec3  v_tint;

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
    v_world_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);
    v_local_y = VERTEX.y;

    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_xz = world_pos.xz;
    v_jitter = hash21(floor(world_pos.xz * 5.0)) * 2.0 - 1.0;

    v_tint = INSTANCE_CUSTOM.rgb;
}

void fragment()
{
    vec3 tint = vec3(1.0);
    if (use_instance_tint)
    {
        tint = v_tint;
        if (dot(tint, vec3(1.0)) < 0.003)
            tint = fallback_tint;
    }

    vec3 col = rock_base * tint;
    col *= 1.0 + v_jitter * rock_variation;

    // Top tint — mossy/dusty on upward-facing surfaces.
    if (use_top_tint)
    {
        float up = pow(clamp(v_world_normal.y, 0.0, 1.0), top_sharpness);
        col = mix(col, top_color, up * top_strength);
    }

    // Base contact darkening near the ground.
    float base_t = clamp(v_local_y / max(base_height, 0.0001), 0.0, 1.0);
    col *= mix(1.0 - base_darken, 1.0, base_t);

    // World-space mottle.
    if (use_mottle)
    {
        float m = texture(mottle_tex, v_world_xz * mottle_scale).r;
        col *= 1.0 + (m - 0.5) * mottle_strength * 2.0;
    }

    if (saturation != 1.0)
    {
        float luma = dot(col, vec3(0.299, 0.587, 0.114));
        col = mix(vec3(luma), col, saturation);
    }

    ALBEDO = clamp(col, 0.0, 1.0);
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;

    if (use_normal_map)
    {
        NORMAL_MAP = texture(normal_tex, UV).rgb;
        NORMAL_MAP_DEPTH = normal_strength;
    }
}

void light()
{
    // Solid rock: real NdotL (not abs) so the shadowed side stays darker,
    // softened by wrap and floored by ambient_fill so it never goes pure black.
    vec3 N = normalize(NORMAL);
    float ndl = dot(N, normalize(LIGHT));
    ndl = clamp((ndl + toon_wrap) / (1.0 + toon_wrap), 0.0, 1.0);
    float banded = max(toon_band(ndl, toon_bands, toon_softness), ambient_fill);
    DIFFUSE_LIGHT += banded * ATTENUATION * LIGHT_COLOR * ALBEDO;
}
