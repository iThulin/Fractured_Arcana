shader_type spatial;
// Custom two-sided toon light() (abs NdotL) with an ambient floor so petals
// never collapse to black. If flowers render black on the GL Compatibility
// backend, delete light() and add `diffuse_toon` to render_mode.
render_mode cull_disabled, specular_disabled;

// ============================================================
//  painterly_flower.gdshader
//
//  Companion to painterly_grass.gdshader for the flower-prop MultiMesh.
//  Shares the SAME two-layer world-space wind model, so flowers sway in
//  lockstep with the grass (copy the wind values straight from the grass
//  tuner). Unlike the grass shader there is NO green height gradient and
//  NO root AO darkening — ALBEDO is the per-instance palette colour
//  (MultiMesh UseColors -> COLOR), lightly brightened toward the petal
//  tips, so the palette reads clean instead of muddied to dark green.
//
//  Requires the flower MultiMesh to have UseColors = true (the C# scatter
//  sets this when UseFlowerColorVariation is on).
// ============================================================

// ---- Wind (mirror of the grass shader; copy values from the grass tuner) ----
group_uniforms wind;
uniform sampler2D wind_noise : hint_default_black, repeat_enable, filter_linear;
uniform vec2  wind_dir = vec2(1.0, 0.35);
uniform float wind_bend : hint_range(0.0, 1.0) = 0.12;        // flowers usually bend a touch less than grass
uniform float wind_amplitude : hint_range(0.0, 1.0) = 0.4;
uniform float wave_scale : hint_range(0.01, 1.0) = 0.12;
uniform float wave_speed : hint_range(0.0, 3.0) = 0.5;
uniform float wave_strength : hint_range(0.0, 2.0) = 0.7;
uniform float wave_stretch : hint_range(0.05, 1.0) = 0.35;
uniform float detail_scale : hint_range(0.1, 6.0) = 1.2;
uniform float detail_speed : hint_range(0.0, 3.0) = 0.25;
uniform float detail_strength : hint_range(0.0, 1.0) = 0.25;

// ---- Colour ----
group_uniforms color;
uniform float tip_brighten : hint_range(0.0, 0.6) = 0.22;     // petals lighten toward the tips (form without a gradient)
uniform float petal_variation : hint_range(0.0, 0.5) = 0.10;  // subtle per-flower brightness jitter
uniform float saturation : hint_range(0.0, 2.0) = 1.05;
uniform vec3  fallback_color : source_color = vec3(0.97, 0.84, 0.30); // used if the mesh delivers no instance colour

// Dark heart: fade each flower toward a darker colour near its centre axis.
// center_color is the target the centre fades to — leave it near-black for a
// darker shade of each flower's own hue, or set an absolute colour (e.g. brown)
// for a uniform "eye". center_size is the heart radius in MESH-LOCAL units, so
// tune it to your petal mesh (the procedural fallback is ~0.12 across).
uniform vec3  center_color : source_color = vec3(0.06, 0.04, 0.02);
uniform float center_darkness : hint_range(0.0, 1.0) = 0.65;
uniform float center_size : hint_range(0.0, 0.6) = 0.04;
uniform float center_softness : hint_range(0.001, 0.4) = 0.05;

// ---- Toon ----
group_uniforms toon;
uniform float toon_bands : hint_range(1.0, 6.0) = 3.0;
uniform float toon_softness : hint_range(0.0, 1.0) = 0.20;
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.40;
uniform float ambient_fill : hint_range(0.0, 1.0) = 0.40;     // floor on lit value so shadowed petals keep their colour

// ---- Stiffness source (base=0 -> tip=1; drives sway weight + tip brighten) ----
group_uniforms stiffness;
uniform bool  stiffness_from_model_height = false;
uniform float model_height : hint_range(0.05, 4.0) = 0.4;

varying float v_stiffness;
varying float v_jitter;
varying float v_radial;   // mesh-local XZ distance from the flower's centre axis

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

    // Captured BEFORE sway so the dark heart stays anchored to the true centre.
    v_radial = length(VERTEX.xz);

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
    // Per-instance palette colour. If the mesh delivers no colour (COLOR ~ 0),
    // fall back to a visible default instead of rendering black.
    vec3 col = COLOR.rgb;
    if (dot(col, vec3(1.0)) < 0.01)
        col = fallback_color;

    col *= 1.0 + v_jitter * petal_variation;
    col += vec3(tip_brighten) * smoothstep(0.45, 1.0, v_stiffness);

    // Dark heart: fade toward center_color near the flower's centre axis.
    float center_mask = 1.0 - smoothstep(center_size, center_size + center_softness, v_radial);
    col = mix(col, center_color, center_mask * center_darkness);

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
