shader_type spatial;

// ============================================================
// painterly_water.gdshader  (v1.0)
//
// The painterly "Ghibli mirror" water plane — S1 items 1–3 of the
// art pass plan (vertex ripple, depth tint, shore foam) plus the
// stylized sky-mirror reflection model.
//
// THE ONE SANCTIONED TRANSPARENT SURFACE (style guide §2.3 / art
// plan S1): this material writes ALPHA. That is safe ONLY because
// the water plane is a single welded mesh — there is no MultiMesh
// buffer-order hazard. Do not copy this pattern into any scatter
// shader. depth_draw_opaque keeps depth writes on so opaque
// grass/units/terrain intersect the surface per-fragment.
//
// Baked vertex COLOR channels (written by HexGridManager.WaterPlane.cs):
//   R = shore distance, 0 at the land edge .. 1 in open water
//   G = depth, 0 shallow .. 1 at max depth
//   A = spill distance across the shore skirt (0 over water tiles,
//       1 at the skirt's far edge) — drives the spill feather
//   B = reserved for the S1.4 flow (packed angle; do not repurpose)
//
// The noise texture is NOT auto-injected (same rule as grass
// wind_noise) — assign a seamless noise texture in the .tres.
// ============================================================

// cull_disabled as insurance: the plane is flat and viewed from above, so
// two-sided costs nothing — and it can never vanish to a winding mistake.
render_mode depth_draw_opaque, cull_disabled, specular_disabled;

// ---- Surface motion (S1.1 vertex ripple) ----
group_uniforms ripple;
/** Seamless tiling noise driving ripple, clouds, foam wobble and sparkle. Assign in the material — it is not auto-injected. */
uniform sampler2D water_noise : hint_default_black, repeat_enable, filter_linear;
/** Direction of the first long swell on the ground plane (x, z). */
uniform vec2 swell_dir_a = vec2(1.0, 0.25);
/** Direction of the second, crossing swell (x, z). */
uniform vec2 swell_dir_b = vec2(-0.35, 1.0);
/** Vertical amplitude of the two swells, world units. Painterly = subtle (art plan: 0.03–0.06). */
uniform float swell_amplitude : hint_range(0.0, 0.15, 0.005) = 0.04;
/** Spatial frequency of the swells. Higher = shorter waves. */
uniform float swell_frequency : hint_range(0.1, 8.0, 0.05) = 1.4;
/** Scroll speed of the swells. Ghibli water is SLOW — resist turning this up. */
uniform float swell_speed : hint_range(0.0, 3.0, 0.05) = 0.45;
/** Amplitude of the irregular noise ripple layered on the swells, world units. */
uniform float ripple_amplitude : hint_range(0.0, 0.15, 0.005) = 0.025;
/** Spatial scale of the noise ripple (world-space UV multiplier). */
uniform float ripple_scale : hint_range(0.01, 2.0, 0.01) = 0.18;
/** Drift velocity of the noise ripple (x, z), world units per second. */
uniform vec2 ripple_drift = vec2(0.03, 0.045);
/** Shore-distance span over which the surface calms toward the land edge (keeps the foam line stable). */
uniform float shore_calm_range : hint_range(0.01, 1.0, 0.01) = 0.35;
/** Fraction of full amplitude remaining right at the shore. */
uniform float shore_calm_floor : hint_range(0.0, 1.0, 0.05) = 0.25;
/** World-space step used to finite-difference the surface for per-pixel normals. */
uniform float normal_sample_dist : hint_range(0.02, 0.5, 0.01) = 0.12;
/** Strength of the extra fine micro-ripple bent into the normal only (adds liquid wobble without moving vertices). */
uniform float micro_normal_strength : hint_range(0.0, 1.0, 0.01) = 0.22;
/** Spatial scale of the micro-ripple normal layer. */
uniform float micro_normal_scale : hint_range(0.1, 8.0, 0.1) = 1.6;

// ---- The mirror (stylized sky reflection) ----
group_uniforms mirror_sky;
/** Sky color reflected near the horizon (low reflected rays). The warm/pale tone. */
uniform vec3 sky_horizon : source_color = vec3(0.82, 0.88, 0.90);
/** Sky color reflected overhead (steep reflected rays). The deep blue tone. */
uniform vec3 sky_zenith : source_color = vec3(0.30, 0.50, 0.72);
/** Number of discrete paint bands in the sky gradient. 2–4 reads hand-painted. */
uniform float reflection_bands : hint_range(1.0, 6.0, 1.0) = 3.0;
/** Softness of the band edges. */
uniform float reflection_band_softness : hint_range(0.0, 1.0, 0.05) = 0.35;
/** How much noise wobbles the band edges so they read as brushwork, not stripes. */
uniform float band_wobble : hint_range(0.0, 0.5, 0.01) = 0.10;
/** Overall strength of the mirror layer. 0 = dead water, 1 = full sky mirror. */
uniform float reflection_strength : hint_range(0.0, 1.0, 0.05) = 0.85;
/** Reflection amount when looking straight down (the floor of the fresnel curve). */
uniform float reflection_base : hint_range(0.0, 1.0, 0.05) = 0.35;
/** Fresnel exponent — how fast the surface turns to mirror at glancing angles. */
uniform float fresnel_power : hint_range(0.5, 8.0, 0.25) = 3.0;

// ---- Reflected clouds (the strongest mirror cue at a tactical camera) ----
group_uniforms mirror_clouds;
/** Color of reflected clouds. Slightly warm off-white sells sunlit cloud. */
uniform vec3 cloud_color : source_color = vec3(0.93, 0.93, 0.88);
/** How much of the sky reflection the clouds may cover. 0 disables the layer. */
uniform float cloud_strength : hint_range(0.0, 1.0, 0.05) = 0.55;
/** Spatial scale of the cloud shapes (world-space UV multiplier). Small = huge slow clouds. */
uniform float cloud_scale : hint_range(0.005, 0.5, 0.005) = 0.035;
/** Cloud drift velocity (x, z). Clouds move SLOWLY — this is what makes the mirror feel vast. */
uniform vec2 cloud_drift = vec2(0.012, 0.004);
/** Noise threshold above which cloud appears. Higher = sparser clouds. */
uniform float cloud_threshold : hint_range(0.0, 1.0, 0.01) = 0.58;
/** Softness of the cloud edge. Keep some softness — hard clouds read as spills. */
uniform float cloud_softness : hint_range(0.01, 0.5, 0.01) = 0.16;
/** How strongly the ripple normal smears the cloud shapes (the liquid shimmer of the mirror). */
uniform float cloud_distort : hint_range(0.0, 30.0, 0.5) = 9.0;

// ---- Water body + depth tint (S1.2) ----
group_uniforms water_body;
/** Water color in the shallows (seen through low reflection, near shore). */
uniform vec3 shallow_color : source_color = vec3(0.28, 0.50, 0.47);
/** Water color at full depth. */
uniform vec3 deep_color : source_color = vec3(0.10, 0.22, 0.34);
/** Saturation of the final water color. 1.0 = unchanged (style guide default ~1.05–1.1). */
uniform float saturation : hint_range(0.0, 2.0, 0.05) = 1.05;
/** Opacity in the shallows (lower = terrain shows through, reads as clear water). */
uniform float alpha_shallow : hint_range(0.0, 1.0, 0.05) = 0.55;
/** Opacity at full depth. */
uniform float alpha_deep : hint_range(0.0, 1.0, 0.05) = 0.92;
/** Extra opacity where the mirror is strong (a reflecting surface hides what is under it). */
uniform float mirror_opacity_boost : hint_range(0.0, 1.0, 0.05) = 0.55;

// ---- Depth-texture refinements (uniform-gated for the laptop floor spec) ----
group_uniforms depth_reads;
/** Enables per-pixel depth reads: soft intersection fade + true-depth tint. Costs one depth sample; safe to disable on the floor spec. */
uniform bool use_depth_reads = true;
/** Distance (world units) over which the water fades out where it meets terrain — kills the hard intersection line. Keep tighter than foam_depth_range or the fade eats the foam. */
uniform float edge_fade_distance : hint_range(0.01, 1.0, 0.01) = 0.08;
/** Water thickness (world units) that reaches full deep tint when depth reads are on. */
uniform float depth_tint_distance : hint_range(0.1, 6.0, 0.1) = 1.6;
uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;

// ---- Shore foam (S1.3) ----
group_uniforms foam;
/** Foam color. Off-white, never pure white — pure white fights the sparkle dabs. */
uniform vec3 foam_color : source_color = vec3(0.90, 0.93, 0.90);
/** Shore-distance width of the main foam band. */
uniform float foam_width : hint_range(0.01, 0.6, 0.01) = 0.10;
/** Position (in shore distance) of a second, detached thin foam line. OFF by default — on hex maps every pond edge doubles up and reads busy; enable for long coastlines. */
uniform float foam_line_pos : hint_range(0.0, 0.8, 0.01) = 0.0;
/** Thickness of the detached second line. */
uniform float foam_line_thickness : hint_range(0.005, 0.2, 0.005) = 0.030;
/** How much noise wobbles the foam edges (brush wobble + slow churn). */
uniform float foam_wobble : hint_range(0.0, 0.4, 0.01) = 0.06;
/** Scale of the foam wobble noise. */
uniform float foam_noise_scale : hint_range(0.05, 4.0, 0.05) = 0.30;
/** Drift of the foam wobble — slow in-and-out breathing of the shoreline. */
uniform vec2 foam_drift = vec2(0.012, 0.018);
/** Overall foam opacity. */
uniform float foam_strength : hint_range(0.0, 1.0, 0.05) = 0.55;
/** Foam hugs the TRUE waterline (and rocks/units standing in water) via depth reads, instead of the baked hex shore distance. Requires use_depth_reads; falls back to baked when off. */
uniform bool foam_from_depth = true;
/** Water thickness (world units) spanned by the depth-based foam band. */
uniform float foam_depth_range : hint_range(0.02, 1.0, 0.01) = 0.18;

// ---- Shore spill (water at grade seeping into neighboring tiles) ----
group_uniforms spill;
/** Where along the shore skirt (0 = water-tile boundary, 1 = far edge) the spilled water starts fading out. */
uniform float spill_fade_start : hint_range(0.0, 1.0, 0.05) = 0.40;
/** Where the spill has fully faded. Keep below 1.0 so nothing ever touches the skirt's outer hex edge. */
uniform float spill_fade_end : hint_range(0.1, 1.0, 0.05) = 0.85;

// ---- Sparkle dabs ----
group_uniforms sparkle;
/** Sparkle dab color (emissive paint daubs). */
uniform vec3 sparkle_color : source_color = vec3(1.0, 0.99, 0.92);
/** Sun direction the glint band favors (points FROM the sun). Set from the map DirectionalLight at spawn. */
uniform vec3 sun_direction = vec3(-0.5, -0.7, -0.3);
/** Sparkle coverage. Higher threshold = rarer, more precious dabs. */
uniform float sparkle_threshold : hint_range(0.5, 0.99, 0.01) = 0.86;
/** Spatial scale of the sparkle field. */
uniform float sparkle_scale : hint_range(0.1, 8.0, 0.1) = 1.9;
/** Scroll speed of the sparkle field — controls how fast dabs pop in and out. */
uniform float sparkle_speed : hint_range(0.0, 2.0, 0.01) = 0.16;
/** Baseline sparkle everywhere on the water (a little life even off the glint). */
uniform float sparkle_ambient : hint_range(0.0, 2.0, 0.05) = 0.25;
/** Extra sparkle where the reflected ray looks toward the sun. */
uniform float sparkle_sun_boost : hint_range(0.0, 6.0, 0.1) = 2.2;
/** Tightness of the sun-glint region. Higher = narrower lane of glitter. */
uniform float sparkle_sun_focus : hint_range(1.0, 32.0, 1.0) = 8.0;

// ---- Toon lighting ----
group_uniforms toon;
/** Number of flat shading steps on the diffuse response. */
uniform float toon_bands : hint_range(1.0, 6.0, 1.0) = 2.0;
/** Softness of the diffuse band edges. */
uniform float toon_softness : hint_range(0.0, 1.0, 0.05) = 0.30;
/** Wraps light around so the off-sun side never goes dead. */
uniform float toon_wrap : hint_range(0.0, 1.0, 0.05) = 0.40;
/** Floor on the lit value — water keeps its color in shadow. */
uniform float ambient_fill : hint_range(0.0, 1.0, 0.05) = 0.55;
/** How much the sun's diffuse modulates the water at all (water is mostly sky-lit; keep low). */
uniform float diffuse_energy : hint_range(0.0, 1.0, 0.05) = 0.35;
/** Threshold of the banded specular glint from the actual scene light. */
uniform float glint_threshold : hint_range(0.5, 0.999, 0.001) = 0.985;
/** Softness of the glint band edge. */
uniform float glint_softness : hint_range(0.0005, 0.05, 0.0005) = 0.006;
/** Intensity of the banded glint. */
uniform float glint_intensity : hint_range(0.0, 4.0, 0.05) = 0.9;

varying vec3 v_world_pos;   // pre-displacement world position (stable pattern space)
varying float v_shore;      // COLOR.r — shore distance 0..1
varying float v_depth;      // COLOR.g — baked depth 0..1
varying float v_spill;      // COLOR.a — skirt spill distance 0..1
varying float v_calm;       // shore damping factor for the surface motion

float toon_band(float x, float bands, float softness)
{
    x = clamp(x, 0.0, 1.0);
    float scaled = x * bands;
    float lower = floor(scaled);
    float f = scaled - lower;
    float soft = smoothstep(0.5 - softness * 0.5, 0.5 + softness * 0.5, f);
    return clamp((lower + soft) / bands, 0.0, 1.0);
}

// The animated water surface height at world-plane position p.
// Used by vertex() for displacement and by fragment() (finite
// differences) for per-pixel normals — they MUST stay in sync.
float surf_height(vec2 p, float t)
{
    vec2 da = normalize(swell_dir_a);
    vec2 db = normalize(swell_dir_b);
    float h = 0.0;
    h += sin(dot(p, da) * swell_frequency + t * swell_speed) * swell_amplitude;
    h += sin(dot(p, db) * swell_frequency * 1.31 + t * swell_speed * 0.83 + 1.7) * swell_amplitude * 0.8;
    h += (texture(water_noise, p * ripple_scale + ripple_drift * t).r - 0.5) * 2.0 * ripple_amplitude;
    return h;
}

void vertex()
{
    v_shore = COLOR.r;
    v_depth = COLOR.g;
    v_spill = COLOR.a;
    v_calm = mix(shore_calm_floor, 1.0, smoothstep(0.0, shore_calm_range, v_shore));

    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_pos = wp;

    VERTEX.y += surf_height(wp.xz, TIME) * v_calm;
}

void fragment()
{
    vec2 p = v_world_pos.xz;
    float t = TIME;

    // ── Per-pixel surface normal from the height field ──
    float e = normal_sample_dist;
    float hx = surf_height(p + vec2(e, 0.0), t) - surf_height(p - vec2(e, 0.0), t);
    float hz = surf_height(p + vec2(0.0, e), t) - surf_height(p - vec2(0.0, e), t);
    vec2 slope = vec2(hx, hz) * (v_calm / (2.0 * e));

    // Fine micro-ripple bent into the normal only.
    vec2 mp = p * micro_normal_scale;
    float m1 = texture(water_noise, mp + vec2(t * 0.021, t * 0.017)).r;
    float m2 = texture(water_noise, mp * 1.37 - vec2(t * 0.015, t * 0.024)).r;
    slope += (vec2(m1, m2) - 0.5) * micro_normal_strength * v_calm;

    vec3 n_world = normalize(vec3(-slope.x, 1.0, -slope.y));
    NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);

    // ── Depth reads (gated): true water thickness + soft intersection ──
    float depth01 = v_depth;
    float edge_fade = 1.0;
    float thickness_ws = 1000.0; // sentinel: "deep" when depth reads are off
    if (use_depth_reads)
    {
        float depth_raw = texture(depth_texture, SCREEN_UV).x;
        vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth_raw, 1.0);
        float scene_dist = -(upos.xyz / upos.w).z;
        float water_dist = -VERTEX.z;
        thickness_ws = max(scene_dist - water_dist, 0.0);
        depth01 = max(depth01, clamp(thickness_ws / depth_tint_distance, 0.0, 1.0));
        edge_fade = clamp(thickness_ws / edge_fade_distance, 0.0, 1.0);
    }

    // ── The mirror: banded sky gradient by reflected ray ──
    vec3 view_world = normalize(CAMERA_POSITION_WORLD - v_world_pos);
    vec3 refl = reflect(-view_world, n_world);

    float wob = (texture(water_noise, p * 0.13 + vec2(t * 0.008, -t * 0.006)).r - 0.5) * band_wobble;
    float sky_t = clamp(clamp(refl.y, 0.0, 1.0) + wob, 0.0, 1.0);
    vec3 sky_col = mix(sky_horizon, sky_zenith, toon_band(sky_t, reflection_bands, reflection_band_softness));

    // Reflected clouds, smeared by the ripple normal.
    if (cloud_strength > 0.0)
    {
        vec2 cp = p * cloud_scale + cloud_drift * t + n_world.xz * cloud_distort * cloud_scale;
        float c = texture(water_noise, cp).r;
        float cloud_mask = smoothstep(cloud_threshold, cloud_threshold + cloud_softness, c);
        sky_col = mix(sky_col, cloud_color, cloud_mask * cloud_strength);
    }

    // ── Fresnel blend of body color vs mirror ──
    float ndv = clamp(dot(n_world, view_world), 0.0, 1.0);
    float fres = pow(1.0 - ndv, fresnel_power);
    float refl_w = clamp(reflection_base + fres * (1.0 - reflection_base), 0.0, 1.0) * reflection_strength;

    vec3 body = mix(shallow_color, deep_color, depth01);
    vec3 col = mix(body, sky_col, refl_w);

    // ── Shore foam: main band + optional detached line (S1.3) ──
    // Depth mode: the band spans foam_depth_range of true water thickness, so
    // foam hugs the terrain∩surface waterline exactly (and rims anything
    // opaque standing in the water). Baked mode: hex shore distance fallback.
    float fwob = (texture(water_noise, p * foam_noise_scale + foam_drift * t).r - 0.5) * foam_wobble;
    float s_src;
    float w_src;
    if (foam_from_depth && use_depth_reads)
    {
        s_src = clamp(thickness_ws / foam_depth_range, 0.0, 4.0);
        w_src = 1.0;
    }
    else
    {
        s_src = v_shore;
        w_src = foam_width;
    }
    float s = max(s_src + fwob, 0.0);
    float foam_main = 1.0 - smoothstep(w_src * 0.55, w_src, s);
    float foam_line = 0.0;
    if (foam_line_pos > 0.0)
    {
        float d = abs(s - foam_line_pos);
        foam_line = 1.0 - smoothstep(foam_line_thickness * 0.5, foam_line_thickness, d);
    }
    float foam_mask = smoothstep(0.22, 0.5, clamp(foam_main + foam_line * 0.9, 0.0, 1.0)) * foam_strength;
    col = mix(col, foam_color, foam_mask);

    // ── Sparkle dabs (emissive paint daubs) ──
    float sp1 = texture(water_noise, p * sparkle_scale * 0.31 + vec2(t * sparkle_speed, t * sparkle_speed * 0.7)).r;
    float sp2 = texture(water_noise, p * sparkle_scale * 0.53 - vec2(t * sparkle_speed * 0.8, t * sparkle_speed * 1.1)).r;
    float sp = smoothstep(sparkle_threshold, sparkle_threshold + 0.05, sp1 * sp2 * 1.35);
    float sun_align = pow(clamp(dot(refl, -normalize(sun_direction)), 0.0, 1.0), sparkle_sun_focus);
    EMISSION = sparkle_color * sp * (sparkle_ambient + sun_align * sparkle_sun_boost) * (1.0 - foam_mask);

    // ── Saturation + outputs ──
    if (saturation != 1.0)
    {
        float luma = dot(col, vec3(0.299, 0.587, 0.114));
        col = mix(vec3(luma), col, saturation);
    }

    ALBEDO = clamp(col, 0.0, 1.0);
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;

    float a = mix(alpha_shallow, alpha_deep, depth01);
    a = mix(a, max(a, 0.97), refl_w * mirror_opacity_boost);
    a = max(a, foam_mask * 0.95);
    // Spill feather: water seeping onto neighboring tiles thins out and
    // vanishes well before the skirt's outer hex edge could show.
    float spill_fade = 1.0 - smoothstep(spill_fade_start, spill_fade_end, v_spill);
    ALPHA = clamp(a, 0.0, 1.0) * edge_fade * spill_fade;
}

void light()
{
    // Gentle banded diffuse — water is mostly sky-lit; the sun only modulates.
    vec3 n = normalize(NORMAL);
    float ndl = clamp(dot(n, normalize(LIGHT)), 0.0, 1.0);
    ndl = (ndl + toon_wrap) / (1.0 + toon_wrap);
    float banded = max(toon_band(ndl, toon_bands, toon_softness), ambient_fill);
    DIFFUSE_LIGHT += banded * diffuse_energy * ATTENUATION * LIGHT_COLOR * ALBEDO;

    // Banded glint from the real scene light: one crisp painted highlight step.
    vec3 h = normalize(normalize(VIEW) + normalize(LIGHT));
    float spec = clamp(dot(n, h), 0.0, 1.0);
    float band = smoothstep(glint_threshold - glint_softness, glint_threshold + glint_softness, spec);
    SPECULAR_LIGHT += band * glint_intensity * ATTENUATION * LIGHT_COLOR;
}
