shader_type spatial;
// Two-sided multi-band toon light() (see light()), so NO diffuse_toon here.
// COMPATIBILITY FALLBACK: if the canopy renders black on the GL Compatibility
// backend, delete light() and add `diffuse_toon` to render_mode.
//
// cull_disabled: canopy blobs read as soft volumes, and two-sided abs(NdotL)
// lighting keeps away-facing leaf masses from clamping to black (same reasoning
// as the grass). alpha_to_coverage resolves BOTH the distance fade AND the
// unit-occlusion cutout against MSAA samples — no transparency sorting. If your
// backend ignores it and canopy pops instead of fading, flip use_dither_fade ON.
render_mode cull_disabled, specular_disabled, alpha_to_coverage;

// ============================================================
//  painterly_canopy.gdshader  (v1)
//
//  Forest canopy props (MultiMesh). Family member of painterly_grass /
//  painterly_flower / painterly_rock — same wind, toon, and translucency model,
//  so the canopy breathes with the grass and flowers.
//
//  PER-INSTANCE CUSTOM DATA (set by the canopy scatter, like flowers/rocks):
//    INSTANCE_CUSTOM.rgb = per-instance TINT (multiplies the gradient — a near-
//                          white palette nudges each canopy warmer/cooler, it
//                          does NOT replace the gradient colour).
//    INSTANCE_CUSTOM.a   = PERMANENT flag. >0.5 means this canopy never fades
//                          for the unit-occlusion cutout (board-edge / impassable
//                          forest). 0 means it clears when a unit stands under it.
//
//  UNIT-OCCLUSION CUTOUT: a per-frame feeder writes `occluders` + `occluder_count`
//  on this material (xy = unit world XZ, z = unit feet world Y, w = active).
//  Each fragment fades where it sits within `cutout_radius` of an active unit AND
//  above that unit's feet — so the crown over a unit clears down to a readable
//  column while trunks / low foliage stay solid. See CanopyOcclusion feeder.
//
//  Inspector tooltips use /** ... */ doc comments (two leading asterisks).
// ============================================================

// ---- Multi-stop height gradient (base = shaded underside, tip = lit crown) ----
group_uniforms gradient;

/** Colour at the canopy base / underside. The darkest, most-shadowed stop. */
uniform vec3  base_color : source_color = vec3(0.06, 0.20, 0.07);

/** Dominant canopy green at mid-height. */
uniform vec3  mid_color : source_color = vec3(0.16, 0.40, 0.14);

/** Colour toward the lit crown, before the highlight. */
uniform vec3  tip_color : source_color = vec3(0.42, 0.66, 0.26);

/** Sun-catch highlight blended into the very top (above highlight_start). */
uniform vec3  highlight_color : source_color = vec3(0.85, 0.95, 0.55);

/** Where mid_color sits along base->crown (0 = base, 1 = crown). Push >0.5 so most of the mass reads shadowed and only the top lights up. */
uniform float mid_position : hint_range(0.05, 0.95) = 0.55;

/** Curves the height ramp. >1 pushes colour toward the base (darker lower mass). */
uniform float tip_bias : hint_range(0.5, 4.0) = 1.6;

/** Height (0..1) at which the crown highlight begins. */
uniform float highlight_start : hint_range(0.0, 1.0) = 0.80;

/** Per-canopy brightness jitter (world-space hash) so blobs don't look cloned. */
uniform float color_variation : hint_range(0.0, 0.3) = 0.06;

/** Saturation / vibrance. ~1.1 for richer Ghibli greens. */
uniform float color_saturation : hint_range(0.0, 2.0) = 1.1;

// ---- Per-instance tint (from custom data; MULTIPLIES the gradient) ----
group_uniforms instance_tint;

/** Multiply the gradient by the per-instance palette tint in INSTANCE_CUSTOM.rgb (set by the scatter). Keep palette entries near-white — they nudge tone, not recolour. */
uniform bool use_instance_tint = true;

/** Tint used when no custom data is supplied (e.g. a hand-placed test blob). */
uniform vec3 fallback_tint : source_color = vec3(1.0, 1.0, 1.0);

// ---- Mass clumping (world-space colour masses — different clumps run warmer/cooler) ----
group_uniforms mass_clumping;

/** Recolour whole world-space regions (canopy masses), not single blobs. */
uniform bool      use_mass_tint = false;

/** Low-frequency noise defining the mass regions. Sample the same field the grass uses for cohesion. */
uniform sampler2D mass_tex : hint_default_white, repeat_enable, filter_linear;

/** Colour multiplied into the darker patches of the mass field. */
uniform vec3      mass_tint_color : source_color = vec3(0.30, 0.46, 0.22);

/** Strength of the mass tint. 0 = off. */
uniform float     mass_strength : hint_range(0.0, 1.0) = 0.45;

/** World-space scale of the mass pattern. Lower = larger masses. */
uniform float     mass_scale : hint_range(0.005, 0.4) = 0.05;

/** Scroll the mass pattern along the wind with the gust, so colour masses drift with the sway. 0 = static. */
uniform float     mass_wind_follow : hint_range(0.0, 1.0) = 0.0;

// ---- AO (under-canopy contact darkening at the base) ----
group_uniforms ambient_occlusion;

/** Darken the canopy base for under-canopy shadow depth. 0 = off. */
uniform float ao_strength : hint_range(0.0, 1.0) = 0.40;

/** Height up the canopy over which the base darkening fades out. */
uniform float ao_height : hint_range(0.0, 0.6) = 0.30;

// ---- Wind (mirror of the grass shader; copy the grass tuner's wave_* values) ----
group_uniforms wind;

/** Seamless noise driving the wind. Use the SAME texture as the grass so canopy and field share the gust. */
uniform sampler2D wind_noise : hint_default_black, repeat_enable, filter_linear;

/** Wind direction on the ground plane (x, z). Match the grass. */
uniform vec2  wind_dir = vec2(1.0, 0.35);

/** How far the canopy bends. Big masses sway LITTLE — keep this low. Keep wave_* matched to the grass so the gust stays in phase. */
uniform float wind_bend : hint_range(0.0, 1.0) = 0.05;

/** Constant baseline lean so the canopy never stands perfectly still. */
uniform float wind_amplitude : hint_range(0.0, 1.0) = 0.4;

/** Spatial size of the big rolling gust. Match the grass. */
uniform float wave_scale : hint_range(0.01, 1.0) = 0.12;

/** Scroll speed of the main gust. Match the grass. */
uniform float wave_speed : hint_range(0.0, 3.0) = 0.5;

/** Strength of the main gust. */
uniform float wave_strength : hint_range(0.0, 2.0) = 0.7;

/** Stretches the gust across the wind. Lower = long streaky gusts. Match the grass. */
uniform float wave_stretch : hint_range(0.05, 1.0) = 0.35;

/** Spatial size of the fine flutter layer. */
uniform float detail_scale : hint_range(0.1, 6.0) = 1.2;

/** Scroll speed of the fine flutter. */
uniform float detail_speed : hint_range(0.0, 3.0) = 0.25;

/** Strength of the fine flutter. */
uniform float detail_strength : hint_range(0.0, 1.0) = 0.25;

// ---- Toon ----
group_uniforms toon;

/** Flat shading steps. Lower for chunkier cel canopy; 2-3 keeps it soft and voluminous. */
uniform float toon_bands : hint_range(1.0, 6.0) = 3.0;

/** Softness of the band edges. 0 = hard cel. */
uniform float toon_softness : hint_range(0.0, 1.0) = 0.15;

/** Wraps light around so shadowed canopy sides aren't pure black. Higher = flatter, softer. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.30;

// ---- Tip translucency / backlight (sun through the leaves — the Ghibli signature) ----
group_uniforms translucency;

/** Light passing through the canopy toward the viewer — rim-glow when the sun is behind the leaves. */
uniform bool  use_translucency = true;

/** Colour of the through-leaf glow. */
uniform vec3  translucency_color : source_color = vec3(0.55, 0.85, 0.30);

/** Intensity of the backlight glow. */
uniform float translucency_strength : hint_range(0.0, 3.0) = 1.2;

/** Tightness of the glow rim. Higher = sharper, narrower rim. */
uniform float translucency_power : hint_range(1.0, 12.0) = 3.5;

/** How much the surface normal bends the through-light direction. */
uniform float translucency_distortion : hint_range(0.0, 1.0) = 0.3;

// ---- Unit-occlusion cutout (fade canopy that covers a unit) ----
group_uniforms occlusion;

/** Fade canopy crown above any active unit so the board stays readable. Permanent canopy (INSTANCE_CUSTOM.a > 0.5) ignores this. */
uniform bool  use_occlusion_cutout = true;

/** Unit occluders, written per-frame by the feeder: xy = world XZ, z = feet world Y, w = active (1/0). Up to 16. */
uniform vec4  occluders[16];

/** Number of valid entries at the front of `occluders`. */
uniform int   occluder_count = 0;

/** Horizontal radius (world units) around a unit within which canopy clears. ~one hex. */
uniform float cutout_radius : hint_range(0.2, 4.0) = 1.2;

/** Soft edge width of the cutout (world units). */
uniform float cutout_soft : hint_range(0.05, 2.0) = 0.6;

/** Vertical distance above a unit's feet over which the cutout ramps from solid (at feet) to fully clear. ~unit height, so only canopy above the head clears. */
uniform float cutout_rise : hint_range(0.1, 6.0) = 1.2;

// ---- Distance fade (kills far-field canopy crawl; reads as soft depth) ----
group_uniforms distance_fade;

/** Fade far canopy out instead of letting it alias into shimmering noise. */
uniform bool  use_distance_fade = true;

/** Use ordered dithering instead of alpha_to_coverage. Turn ON if your backend ignores alpha_to_coverage. */
uniform bool  use_dither_fade = false;

/** Camera distance where canopy begins fading. */
uniform float fade_start : hint_range(1.0, 120.0) = 28.0;

/** Camera distance where canopy is fully gone. */
uniform float fade_end : hint_range(1.0, 160.0) = 60.0;

// ---- Atmospheric depth tint (distant canopy drifts toward haze) ----
group_uniforms atmospheric_depth;

/** Tint distant canopy toward a haze colour for aerial recession. */
uniform bool  use_depth_tint = true;

/** Haze colour. Match your fog/sky for the best recession. */
uniform vec3  depth_color : source_color = vec3(0.55, 0.66, 0.60);

/** Distance where the haze tint begins. */
uniform float depth_start : hint_range(0.0, 120.0) = 12.0;

/** Distance where the haze tint reaches full strength. */
uniform float depth_end : hint_range(0.0, 160.0) = 45.0;

/** Maximum haze blend amount. */
uniform float depth_strength : hint_range(0.0, 1.0) = 0.5;

// ---- Stiffness (base=0 -> crown=1; drives sway weight + translucency weighting) ----
group_uniforms stiffness;

/** Derive the base->crown weight from local mesh height instead of UV.y. Default ON for canopy — blob meshes rarely have clean vertical UVs. */
uniform bool  stiffness_from_model_height = true;

/** Mesh height used to normalise height-based stiffness. Set to the canopy mesh's local height. */
uniform float model_height : hint_range(0.05, 8.0) = 2.0;

varying float v_stiffness;
varying float v_jitter;
varying vec2  v_world_xz;
varying vec3  v_world_pos;     // full world position — needed by the occlusion cutout
varying float v_view_dist;
varying float v_wave;          // signed coherent wind wave, shared with the mass tint
varying vec2  v_wind_ndir;     // normalized wind direction, shared with the mass tint
varying vec3  v_tint;          // per-instance tint  (INSTANCE_CUSTOM.rgb)
varying float v_permanent;     // permanent flag     (INSTANCE_CUSTOM.a)

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

// Ordered 4x4 Bayer dither — used only when use_dither_fade is on.
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

    v_tint = INSTANCE_CUSTOM.rgb;
    v_permanent = INSTANCE_CUSTOM.a;

    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_pos = world_pos;
    v_world_xz = world_pos.xz;
    v_jitter = hash21(floor(world_pos.xz * 7.0)) * 2.0 - 1.0;

    v_view_dist = length((VIEW_MATRIX * vec4(world_pos, 1.0)).xyz);

    // --- Two-layer world-space wind (identical model to the grass) ---
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
    v_wave = wave * wave_strength + detail * detail_strength;

    float sway = gust * wind_bend * stiffness * stiffness;
    VERTEX.x += ndir.x * sway;
    VERTEX.z += ndir.y * sway;
    VERTEX.y -= abs(sway) * 0.15 * stiffness;
}

void fragment()
{
    // 1. Multi-stop gradient (shaded base -> lit crown).
    float h = pow(clamp(v_stiffness, 0.0, 1.0), tip_bias);
    vec3 col;
    if (h < mid_position)
        col = mix(base_color, mid_color, h / max(mid_position, 0.0001));
    else
        col = mix(mid_color, tip_color, (h - mid_position) / max(1.0 - mid_position, 0.0001));
    col = mix(col, highlight_color, smoothstep(highlight_start, 1.0, h));

    // 2. Per-instance tint (multiply — keeps the gradient shape).
    if (use_instance_tint)
    {
        vec3 tint = v_tint;
        if (dot(tint, vec3(1.0)) < 0.003)
            tint = fallback_tint;
        col *= tint;
    }

    // 3. Per-canopy brightness jitter.
    col *= 1.0 + v_jitter * color_variation;

    // 4. Mass clumping — world-space colour masses.
    if (use_mass_tint)
    {
        vec2 mass_flow = v_wind_ndir * v_wave * mass_wind_follow;
        float m = texture(mass_tex, v_world_xz * mass_scale + mass_flow).r;
        float darken = (1.0 - m) * mass_strength;
        col = mix(col, col * mass_tint_color, darken);
    }

    // 5. Saturation.
    if (color_saturation != 1.0)
    {
        float luma = dot(col, vec3(0.299, 0.587, 0.114));
        col = mix(vec3(luma), col, color_saturation);
    }

    // 6. AO base-darkening (under-canopy shadow).
    float ao = mix(1.0 - ao_strength, 1.0, smoothstep(0.0, max(ao_height, 0.0001), v_stiffness));
    col *= ao;

    // 7. Atmospheric depth tint.
    if (use_depth_tint)
    {
        float dt = smoothstep(depth_start, depth_end, v_view_dist) * depth_strength;
        col = mix(col, depth_color, dt);
    }

    ALBEDO = col;
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;

    // 8. Unit-occlusion cutout — clear the crown above any active unit.
    float occ_alpha = 1.0;
    if (use_occlusion_cutout && v_permanent < 0.5)
    {
        float clear = 0.0;
        for (int i = 0; i < 16; i++)
        {
            if (i >= occluder_count)
                break;
            vec4 o = occluders[i];
            if (o.w < 0.5)
                continue;
            float d = distance(v_world_pos.xz, o.xy);
            float radial = 1.0 - smoothstep(cutout_radius - cutout_soft, cutout_radius, d);
            float above = clamp((v_world_pos.y - o.z) / max(cutout_rise, 0.0001), 0.0, 1.0);
            clear = max(clear, radial * above);
        }
        occ_alpha = 1.0 - clear;
    }

    // 9. Distance fade, combined with the cutout. Both resolve via coverage/dither.
    float fade = 1.0;
    if (use_distance_fade)
        fade = 1.0 - smoothstep(fade_start, fade_end, v_view_dist);

    float final_alpha = fade * occ_alpha;

    if (use_dither_fade)
    {
        if (final_alpha < dither_bayer(FRAGCOORD.xy))
            discard;
        ALPHA = 1.0;
    }
    else
    {
        ALPHA = final_alpha; // resolved by alpha_to_coverage against MSAA samples
    }
}

void light()
{
    // Two-sided multi-band toon diffuse (abs NdotL) so away-facing canopy and
    // backfaces under cull_disabled don't crush to black.
    vec3 N = normalize(NORMAL);
    float ndl = abs(dot(N, normalize(LIGHT)));
    ndl = (ndl + toon_wrap) / (1.0 + toon_wrap);
    float banded = toon_band(ndl, toon_bands, toon_softness);
    DIFFUSE_LIGHT += banded * ATTENUATION * LIGHT_COLOR * ALBEDO;

    // Tip translucency — sun through the leaves, weighted to the crown.
    if (use_translucency)
    {
        vec3 through = normalize(LIGHT + N * translucency_distortion);
        float back = pow(clamp(dot(VIEW, -through), 0.0, 1.0), translucency_power);
        DIFFUSE_LIGHT += back * translucency_strength * v_stiffness
                         * translucency_color * LIGHT_COLOR * ATTENUATION;
    }
}
