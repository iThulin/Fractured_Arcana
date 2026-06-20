// ============================================================
// terrain_splat.gdshader  (v6 — Ghibli rock: contact shadow + moss, solid skirts)
//
// v6 over v5.1:
//   - CLIFF TREATMENT reworked toward the painted-rock reference: the primary
//     cue is now CONTACT SHADOW (faces darken toward their base, where rocks
//     stack), not regular strata. Strata removed. Cracks are an irregular,
//     dialed-DOWN world-space noise. Moss is TOP-BIASED (settles on upward
//     parts of the face), not random.
//   - SOLID BOUNDARY SKIRTS: map-edge skirts (flagged by HexMeshBuilder via
//     vertex-colour alpha = 1) are painted a flat colour and skip cliff
//     treatment + grid lines. Interior cliffs (alpha = 0) are unaffected.
//   - Fixes the v5 hand-edit compile error (stray 'albedo' ref).
//
// COMPATIBILITY: custom light() works on GL Compatibility (rock/grass already
// do). If terrain renders black, delete light() and add diffuse_toon.
// REQUIRES: HexMeshBuilder GenerateTangents(); section-4 skirt alpha flag.
// ============================================================

shader_type spatial;
render_mode specular_disabled;

/** Packed per-terrain albedo array (sRGB). Layer index = (int)TileTerrainType. */
uniform sampler2DArray terrain_textures : source_color, filter_linear_mipmap, repeat_enable;
/** Packed per-terrain normal array (linear). Same layer indices as albedo. */
uniform sampler2DArray terrain_normals : filter_linear_mipmap, repeat_enable;

/** World units to UV for terrain texture sampling. Lower = larger features. */
uniform float texture_scale : hint_range(0.02, 1.0) = 0.22;
/** Flat PBR roughness for the terrain surface. */
uniform float roughness_value : hint_range(0.0, 1.0) = 0.85;
/** Triplanar blend sharpness. Higher = tighter plane transitions. */
uniform float triplanar_sharpness : hint_range(1.0, 8.0) = 4.0;

/** Strength of the terrain normal-map perturbation. */
uniform float normal_strength : hint_range(0.0, 2.0) = 1.0;
/** Flip normal green channel if maps are DirectX (-Y) not OpenGL (+Y). */
uniform bool flip_normal_y = false;

// ── Cliff retexturing ──────────────────────────────────────────
group_uniforms cliff_retexture;
/** Terrain array layer used for steep cliff faces. = (int)TileTerrainType rock. */
uniform int cliff_layer = 4;
/** Steepness (0 flat .. 1 vertical) where faces begin blending to the cliff layer. */
uniform float cliff_blend_start : hint_range(0.0, 1.0) = 0.55;
/** How abruptly the top texture gives way to the cliff layer. */
uniform float cliff_blend_sharpness : hint_range(0.5, 16.0) = 8.0;
/** Residual darkening multiplied onto cliff faces on top of the texture. */
uniform float wall_darkening : hint_range(0.0, 1.0) = 0.12;

// ── Toon lighting (match the grass tuner) ──────────────────────
group_uniforms toon;
/** Number of flat shading steps. Match the scene contract (6). */
uniform float toon_bands : hint_range(1.0, 6.0) = 6.0;
/** Band-edge softness. 1.0 = near-smooth ramp (current look). */
uniform float toon_softness : hint_range(0.0, 1.0) = 1.0;
/** Wrap light so the shadowed side isn't pure black. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.27;
/** Floor on lit value so faces keep colour and never crush to black. */
uniform float ambient_fill : hint_range(0.0, 1.0) = 0.45;

// ── Boundary skirts (map edge) ─────────────────────────────────
group_uniforms boundary_skirt;
/** Paint map-edge skirt faces a flat solid colour (the "edge of the board"). */
uniform bool  use_solid_skirt = true;
/** The flat skirt colour. Dark, reads as off-board. */
uniform vec3  skirt_color : source_color = vec3(0.10, 0.09, 0.14);

// ── Pastel ground gradient ─────────────────────────────────────
group_uniforms pastel_ground;
/** Wash a soft pastel gradient over grassland tops (cliffs keep rock). */
uniform bool  use_pastel_ground = true;
/** Terrain layer the pastel applies to. = (int)TileTerrainType.Grass. */
uniform int   grass_layer = 0;
/** 0 = keep terrain albedo, 1 = full flat pastel. */
uniform float pastel_strength : hint_range(0.0, 1.0) = 0.7;
/** Pastel gradient — valley colour. Warm cream. */
uniform vec3  pastel_low : source_color  = vec3(0.93, 0.89, 0.76);
/** Pastel gradient — slope colour. Soft sage. */
uniform vec3  pastel_mid : source_color  = vec3(0.80, 0.86, 0.64);
/** Pastel gradient — hilltop colour. Pale mint. */
uniform vec3  pastel_high : source_color = vec3(0.90, 0.94, 0.82);
/** Where mid sits along valley->hilltop (0..1). */
uniform float pastel_mid_pos : hint_range(0.05, 0.95) = 0.5;
/** HEIGHT MODE: world Y mapping to gradient 0 (valley). */
uniform float pastel_height_low = 0.0;
/** HEIGHT MODE: world Y mapping to gradient 1 (hilltop). */
uniform float pastel_height_high = 4.0;
/** Drive the gradient from world-space noise instead of height. */
uniform bool      pastel_from_noise = false;
/** Seamless noise used when pastel_from_noise is on. */
uniform sampler2D pastel_noise_tex : hint_default_white, repeat_enable, filter_linear;
/** World-space scale of the pastel noise. Lower = larger patches. */
uniform float     pastel_noise_scale : hint_range(0.005, 0.4) = 0.05;

// ── Stone ground tonal mass (stone tops only) ──────────────────
group_uniforms stone_ground;
/** Recolour stone tops with cool<->warm tonal drift instead of flat grey. */
uniform bool  use_stone_ground = true;
/** = (int)TileTerrainType.Stone. VERIFY against your enum. */
uniform int   stone_layer = 2;
/** 0 = keep terrain albedo, 1 = full tonal recolour of stone tops. */
uniform float stone_strength : hint_range(0.0, 1.0) = 0.85;
/** Seamless noise driving the tonal drift. ASSIGN (Painterly_wind_noise). */
uniform sampler2D stone_noise_tex : hint_default_white, repeat_enable, filter_linear;
/** Cool end of the stone hue drift. Desaturated. */
uniform vec3  stone_cool : source_color = vec3(0.42, 0.45, 0.50);
/** Warm end of the stone hue drift. */
uniform vec3  stone_warm : source_color = vec3(0.52, 0.50, 0.47);
/** World-space scale of the cool/warm drift. Lower = bigger masses. */
uniform float stone_tint_scale : hint_range(0.005, 0.4) = 0.05;
/** Finer brightness mottle, sampled independently of hue. */
uniform float stone_mottle : hint_range(0.0, 0.5) = 0.12;
/** World-space scale of the brightness mottle. */
uniform float stone_mottle_scale : hint_range(0.01, 1.0) = 0.25;

// ── Cliff treatment (steep faces: contact shadow + sparse cracks + top moss) ──
group_uniforms cliff_treatment;
/** Distinct rock tone, base contact shadow, sparse cracks, and top-settled moss. */
uniform bool  use_cliff_treatment = true;
/** Cool blue-grey rock tone (Ghibli target — keep cool, the purple ambient helps). */
uniform vec3  cliff_tint : source_color = vec3(0.44, 0.45, 0.48);
/** How strongly the cliff tint replaces the face albedo. */
uniform float cliff_tint_strength : hint_range(0.0, 1.0) = 0.5;
/** PRIMARY CUE: darken faces toward their base, where rocks stack (contact shadow). */
uniform float cliff_contact_strength : hint_range(0.0, 1.0) = 0.45;
/** World-Y height above a face's foot over which the contact shadow fades out. */
uniform float cliff_contact_height : hint_range(0.1, 4.0) = 1.2;
/** Seamless noise (reuse stone_noise_tex) for irregular crevices. */
uniform sampler2D cliff_crack_tex : hint_default_white, repeat_enable, filter_linear;
/** World-space scale of cracks. Higher = finer. */
uniform float cliff_crack_scale : hint_range(0.05, 2.0) = 0.5;
/** Depth of crevice darkening. Kept LOW — reference rock is smooth. */
uniform float cliff_crack_strength : hint_range(0.0, 0.8) = 0.15;
/** Moss colour settling on upward parts of the face. */
uniform vec3  cliff_moss : source_color = vec3(0.40, 0.50, 0.28);
/** Moss coverage. 0 = bare; raise for mossier ledges. */
uniform float cliff_moss_amount : hint_range(0.0, 1.0) = 0.0;

// ── Painterly ground detail (mottling + small stones) ──────────
group_uniforms ground_detail;
/** Add value mottling, soil patches, and painted pebbles to the pastel ground. */
uniform bool      use_ground_detail = false;
/** Detail field (seamless or cellular NoiseTexture2D), sampled in world XZ. */
uniform sampler2D ground_detail_tex : hint_default_white, repeat_enable, filter_linear;
/** World-space scale of the ground detail field. */
uniform float     ground_detail_scale : hint_range(0.05, 2.0) = 0.6;
/** Soft value mottling across the ground. */
uniform float     ground_mottle : hint_range(0.0, 0.5) = 0.12;
/** Warm-brown soil colour blended into the detail patches. */
uniform vec3      soil_tint : source_color = vec3(0.52, 0.40, 0.28);
/** Soil-tint strength. 0 = off. */
uniform float     soil_tint_strength : hint_range(0.0, 1.0) = 0.0;
/** World-space scale of soil patches. Lower = bigger. */
uniform float     soil_tint_scale : hint_range(0.01, 1.0) = 0.12;
/** Cellular (Voronoi) NoiseTexture2D for painted pebbles. */
uniform sampler2D stone_tex : hint_default_white, repeat_enable, filter_linear;
/** World-space scale of the pebble field. */
uniform float     stone_scale : hint_range(0.05, 2.0) = 0.5;
/** Pebble colour. */
uniform vec3      rock_color : source_color = vec3(0.55, 0.52, 0.48);
/** Pebble coverage. 0 = none. */
uniform float     rock_amount : hint_range(0.0, 1.0) = 0.0;
/** How much of the pebble field becomes stone. */
uniform float     rock_threshold : hint_range(0.0, 1.0) = 0.42;
/** Pebble edge hardness. */
uniform float     rock_sharpness : hint_range(0.5, 16.0) = 5.0;
/** Per-pebble value jitter. */
uniform float     rock_variation : hint_range(0.0, 0.5) = 0.15;

/** Highlight emission colour (hover/move; added after lighting). */
uniform vec3 highlight_color = vec3(0.0);
/** Highlight emission intensity. 0 = none. */
uniform float highlight_strength : hint_range(0.0, 3.0) = 0.0;

// ── Water (water-layer tops: depth colour + ripple + shore foam) ──
group_uniforms water;
/** Shade water tiles with depth colour, toon ripple, and shore foam. */
uniform bool  use_water = true;
/** = (int)TileTerrainType.Water. VERIFY (grass=0, stone=2 confirmed; water likely 3). */
uniform int   water_layer = 3;
/** Shallow water colour (near shore / high ground). */
uniform vec3  water_shallow : source_color = vec3(0.30, 0.62, 0.78);
/** Deep water colour (low ground). Reads as the impassable barrier body. */
uniform vec3  water_deep : source_color = vec3(0.10, 0.26, 0.52);
/** World Y at/below which water is fully deep. */
uniform float water_deep_at : hint_range(-4.0, 2.0) = -1.0;
/** World Y at/above which water is fully shallow. */
uniform float water_shallow_at : hint_range(-2.0, 4.0) = 0.5;
/** Ripple flow direction (x, z). */
uniform vec2  water_flow = vec2(1.0, 0.35);
/** Big swell spatial size. Lower = broader. */
uniform float water_wave_scale : hint_range(0.01, 1.0) = 0.12;
/** Swell scroll speed. */
uniform float water_wave_speed : hint_range(0.0, 3.0) = 0.4;
/** Fine ripple spatial size. */
uniform float water_detail_scale : hint_range(0.1, 6.0) = 1.4;
/** Fine ripple scroll speed. */
uniform float water_detail_speed : hint_range(0.0, 3.0) = 0.3;
/** Ripple value variation strength. */
uniform float water_ripple_strength : hint_range(0.0, 0.6) = 0.18;
/** Sun-glint colour on ripple crests. */
uniform vec3  water_glint : source_color = vec3(0.80, 0.92, 1.0);
/** Glint intensity. */
uniform float water_glint_strength : hint_range(0.0, 1.0) = 0.25;
/** Bright shore foam where water meets land. */
uniform vec3  water_foam : source_color = vec3(0.92, 0.96, 1.0);
/** Foam band width (water-weight below this is foam). */
uniform float water_foam_width : hint_range(0.0, 0.6) = 0.30;
/** Foam edge softness. */
uniform float water_foam_softness : hint_range(0.01, 0.4) = 0.12;
/** Reuse stone_noise_tex (or wind noise) for the ripple. ASSIGN it. */
uniform sampler2D water_noise_tex : hint_default_black, repeat_enable, filter_linear;
/** World-space scale of the colour tonal drift. Lower = bigger colour masses. Drives variety, not height. */
uniform float water_tone_scale : hint_range(0.005, 0.4) = 0.06;
/** Fake-undulation strength: how hard the ripple perturbs the lit normal. The "moving surface" cue. */
uniform float water_normal_strength : hint_range(0.0, 1.0) = 0.3;

// ── Persistent hex grid lines ──────────────────────────────────
group_uniforms grid_lines;
/** Darken a thin furrow along every hex tile edge. */
uniform bool  use_grid_lines = false;
/** Colour of the grid furrow. */
uniform vec3  grid_line_color : source_color = vec3(0.18, 0.22, 0.16);
/** Must equal HexGridManager.HexRadius (set from code). */
uniform float grid_hex_radius = 1.0;
/** Line half-width in world units from each edge. */
uniform float grid_line_width : hint_range(0.0, 0.3) = 0.04;
/** Softness of the line's outer falloff. */
uniform float grid_line_softness : hint_range(0.001, 0.2) = 0.02;
/** Grid line opacity. 0 = off. */
uniform float grid_line_strength : hint_range(0.0, 1.0) = 0.4;

varying vec3 v_world_pos;
varying vec3 v_world_normal;
varying vec4 v_indices;
varying vec4 v_weights;
varying vec2 v_local_xz;
varying float v_skirt;   // 1.0 on map-edge skirt faces (from vertex-colour alpha)

float toon_band(float x, float bands, float softness) {
	x = clamp(x, 0.0, 1.0);
	float scaled = x * bands;
	float lower = floor(scaled);
	float f = scaled - lower;
	float soft = smoothstep(0.5 - softness * 0.5, 0.5 + softness * 0.5, f);
	return (lower + soft) / bands;
}

void vertex() {
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_world_normal = MODEL_NORMAL_MATRIX * NORMAL;
	v_indices = CUSTOM0;
	v_weights = COLOR;
	v_skirt = COLOR.a;          // section-4 skirts set A=1; interior faces A=0
	v_local_xz = VERTEX.xz;
}

vec3 sample_albedo(float layer, vec3 wp, vec3 bw) {
	vec3 cx = texture(terrain_textures, vec3(wp.zy * texture_scale, layer)).rgb;
	vec3 cy = texture(terrain_textures, vec3(wp.xz * texture_scale, layer)).rgb;
	vec3 cz = texture(terrain_textures, vec3(wp.xy * texture_scale, layer)).rgb;
	return cx * bw.x + cy * bw.y + cz * bw.z;
}

vec3 decode_n(vec3 s) {
	vec3 n = s * 2.0 - 1.0;
	if (flip_normal_y) n.y = -n.y;
	return n;
}

vec3 sample_normal_top(float layer, vec3 wp) {
	return decode_n(texture(terrain_normals, vec3(wp.xz * texture_scale, layer)).rgb);
}

vec3 sample_normal_wall(float layer, vec3 wp, vec3 bw) {
	vec3 nx = decode_n(texture(terrain_normals, vec3(wp.zy * texture_scale, layer)).rgb);
	vec3 nz = decode_n(texture(terrain_normals, vec3(wp.xy * texture_scale, layer)).rgb);
	float wsum = max(bw.x + bw.z, 0.0001);
	return (nx * bw.x + nz * bw.z) / wsum;
}

vec3 pastel_gradient(float t) {
	t = clamp(t, 0.0, 1.0);
	if (t < pastel_mid_pos)
		return mix(pastel_low, pastel_mid, t / max(pastel_mid_pos, 0.0001));
	return mix(pastel_mid, pastel_high, (t - pastel_mid_pos) / max(1.0 - pastel_mid_pos, 0.0001));
}

void fragment() {
	vec3 geom = normalize(v_world_normal);
	vec3 bw = pow(abs(geom), vec3(triplanar_sharpness));
	bw /= (bw.x + bw.y + bw.z);

	vec4 w = v_weights;
	w /= max(w.r + w.g + w.b + w.a, 0.0001);

	// ── Base terrain albedo ──────────────────────────────────────
	vec3 terrain_albedo =
		sample_albedo(v_indices.r, v_world_pos, bw) * w.r +
		sample_albedo(v_indices.g, v_world_pos, bw) * w.g +
		sample_albedo(v_indices.b, v_world_pos, bw) * w.b +
		sample_albedo(v_indices.a, v_world_pos, bw) * w.a;

	vec3 terrain_n =
		sample_normal_top(v_indices.r, v_world_pos) * w.r +
		sample_normal_top(v_indices.g, v_world_pos) * w.g +
		sample_normal_top(v_indices.b, v_world_pos) * w.b +
		sample_normal_top(v_indices.a, v_world_pos) * w.a;

	// ── Cliff layer (rock) ───────────────────────────────────────
	float cl = float(cliff_layer);
	vec3 cliff_albedo = sample_albedo(cl, v_world_pos, bw);
	vec3 cliff_n = sample_normal_wall(cl, v_world_pos, bw);

	float steep = 1.0 - abs(geom.y);
	float cliff_mix = smoothstep(0.0, 1.0,
		clamp((steep - cliff_blend_start) * cliff_blend_sharpness, 0.0, 1.0));

	vec3 albedo = mix(terrain_albedo, cliff_albedo, cliff_mix);
	vec3 tn = normalize(mix(terrain_n, cliff_n, cliff_mix));

	albedo *= mix(1.0, 1.0 - wall_darkening, cliff_mix);

	// Skirt detection: section-4 boundary faces carry vertex-colour alpha = 1.
	float is_skirt = step(0.5, v_skirt);

	// ── Pastel ground gradient (grassland tops only) ─────────────
	if (use_pastel_ground) {
		float gl = float(grass_layer);
		float grass_weight =
			(abs(v_indices.r - gl) < 0.5 ? w.r : 0.0) +
			(abs(v_indices.g - gl) < 0.5 ? w.g : 0.0) +
			(abs(v_indices.b - gl) < 0.5 ? w.b : 0.0) +
			(abs(v_indices.a - gl) < 0.5 ? w.a : 0.0);

		float gt = pastel_from_noise
			? texture(pastel_noise_tex, v_world_pos.xz * pastel_noise_scale).r
			: smoothstep(pastel_height_low, pastel_height_high, v_world_pos.y);
		vec3 pastel = pastel_gradient(gt);

		if (use_ground_detail) {
			float d = texture(ground_detail_tex, v_world_pos.xz * ground_detail_scale).r;
			pastel *= 1.0 + (d - 0.5) * (ground_mottle * 2.0);

			float st = texture(ground_detail_tex, v_world_pos.xz * soil_tint_scale).r;
			pastel = mix(pastel, soil_tint, smoothstep(0.35, 0.85, st) * soil_tint_strength);

			float sd = texture(stone_tex, v_world_pos.xz * stone_scale).r;
			float rock = (1.0 - smoothstep(rock_threshold, rock_threshold + 1.0 / rock_sharpness, sd)) * rock_amount;
			vec3 stone = rock_color * (1.0 + (sd - 0.5) * rock_variation * 2.0);
			pastel = mix(pastel, stone, rock);
		}

		float ground_mask = (1.0 - cliff_mix) * pastel_strength * grass_weight * (1.0 - is_skirt);
		albedo = mix(albedo, pastel, ground_mask);
	}

	// ── Stone ground tonal mass (stone tops only) ────────────────
	if (use_stone_ground) {
		float sl = float(stone_layer);
		float stone_weight =
			(abs(v_indices.r - sl) < 0.5 ? w.r : 0.0) +
			(abs(v_indices.g - sl) < 0.5 ? w.g : 0.0) +
			(abs(v_indices.b - sl) < 0.5 ? w.b : 0.0) +
			(abs(v_indices.a - sl) < 0.5 ? w.a : 0.0);

		float hue = texture(stone_noise_tex, v_world_pos.xz * stone_tint_scale).r;
		vec3 stone_col = mix(stone_cool, stone_warm, hue);

		float mv = texture(stone_noise_tex, v_world_pos.xz * stone_mottle_scale + vec2(0.37, 0.61)).r;
		stone_col *= 1.0 + (mv - 0.5) * stone_mottle * 2.0;

		float stone_mask = (1.0 - cliff_mix) * stone_strength * stone_weight * (1.0 - is_skirt);
		albedo = mix(albedo, stone_col, stone_mask);
	}

	// ── Water (water-layer tops only) ────────────────────────────
	if (use_water) {
		float wl = float(water_layer);
		float water_weight =
			(abs(v_indices.r - wl) < 0.5 ? w.r : 0.0) +
			(abs(v_indices.g - wl) < 0.5 ? w.g : 0.0) +
			(abs(v_indices.b - wl) < 0.5 ? w.b : 0.0) +
			(abs(v_indices.a - wl) < 0.5 ? w.a : 0.0);

		vec2 ndir = normalize(water_flow);
		vec2 perp = vec2(-ndir.y, ndir.x);
		float along = dot(v_world_pos.xz, ndir);
		float across = dot(v_world_pos.xz, perp);

		// Two ripple layers moving in DIFFERENT directions (crossed flow) so the
		// surface doesn't all translate one way. Slow — water moves under grass.
		float wave = texture(water_noise_tex,
			vec2(along * water_wave_scale + TIME * water_wave_speed, across * water_wave_scale)).r * 2.0 - 1.0;
		float det = texture(water_noise_tex,
			vec2(across * water_detail_scale + TIME * water_detail_speed, along * water_detail_scale)).r * 2.0 - 1.0;

		// Tonal-mass colour drift (NOT height) — low-freq world-space, like stone.
		// This is what gives the pool cool/warm variety instead of one flat blue.
		float tone = texture(water_noise_tex, v_world_pos.xz * water_tone_scale + vec2(0.4, 0.7)).r;
		vec3 wcol = mix(water_deep, water_shallow, tone);

		// Ripple value variation on top of the tonal drift.
		wcol *= 1.0 + (wave * 0.6 + det * 0.4) * water_ripple_strength;

		// FAKE UNDULATION: perturb the surface normal with the ripple gradient so
		// toon lighting bands across the "waves". No geometry change, no seam risk.
		vec3 wn = normalize(vec3(wave * water_normal_strength, 1.0, det * water_normal_strength));
		tn = normalize(mix(tn, wn, water_weight * (1.0 - is_skirt)));

		// Glint on crests — does NOT scroll with the flow, so it shimmers in place
		// instead of gliding. Sampled static-ish for a sparkle that sits on the water.
		float crest = smoothstep(0.5, 0.9, texture(water_noise_tex,
			v_world_pos.xz * water_detail_scale + vec2(TIME * 0.05)).r);
		wcol += water_glint * crest * water_glint_strength;

		// Shore foam.
		float foam = 1.0 - smoothstep(water_foam_width, water_foam_width + water_foam_softness, water_weight);
		wcol = mix(wcol, water_foam, foam);

		float water_mask = water_weight * (1.0 - is_skirt);
		albedo = mix(albedo, wcol, water_mask);
	}

	// ── Cliff treatment (interior steep faces only — skirts excluded) ──
	if (use_cliff_treatment) {
		float face = cliff_mix * (1.0 - is_skirt);

		vec3 cliff_col = mix(albedo, cliff_tint, cliff_tint_strength);

		// Sparse irregular crevices (kept low — reference rock is smooth).
		// Sample on the face plane so cracks run down the wall, not around it.
		vec2 crack_uv = mix(v_world_pos.zy, v_world_pos.xy, step(abs(geom.x), abs(geom.z)));
		float crack = texture(cliff_crack_tex, crack_uv * cliff_crack_scale).r;
		cliff_col *= 1.0 - (1.0 - smoothstep(0.35, 0.55, crack)) * cliff_crack_strength;

		// Top-settled moss: bias to the more upward-facing part of the band.
		float upface = smoothstep(0.15, 0.5, geom.y);
		float li = texture(cliff_crack_tex, crack_uv * cliff_crack_scale * 0.4 + vec2(0.7, 0.2)).r;
		cliff_col = mix(cliff_col, cliff_moss, smoothstep(0.6, 0.9, li) * upface * cliff_moss_amount);

		albedo = mix(albedo, cliff_col, face);
	}
	// ── Solid boundary skirt ─────────────────────────────────────
	if (use_solid_skirt) {
		albedo = mix(albedo, skirt_color, is_skirt);
	}

	// ── Persistent hex grid lines (skip skirts) ──────────────────
	if (use_grid_lines) {
		float apothem = grid_hex_radius * 0.8660254;
		float hx = max(abs(dot(v_local_xz, vec2(0.8660254, 0.5))),
			max(abs(dot(v_local_xz, vec2(0.0, 1.0))),
				abs(dot(v_local_xz, vec2(-0.8660254, 0.5)))));
		float dist_to_edge = apothem - hx;
		float line = 1.0 - smoothstep(grid_line_width, grid_line_width + grid_line_softness, dist_to_edge);
		float top_face = smoothstep(0.45, 0.8, abs(geom.y));
		albedo = mix(albedo, grid_line_color, line * grid_line_strength * top_face * (1.0 - is_skirt));
	}

	ALBEDO = albedo;
	ROUGHNESS = roughness_value;
	NORMAL_MAP = tn * 0.5 + 0.5;
	NORMAL_MAP_DEPTH = normal_strength;
	EMISSION = highlight_color * highlight_strength;
}

void light() {
	vec3 N = normalize(NORMAL);
	float ndl = dot(N, normalize(LIGHT));
	ndl = clamp((ndl + toon_wrap) / (1.0 + toon_wrap), 0.0, 1.0);
	float banded = max(toon_band(ndl, toon_bands, toon_softness), ambient_fill);
	DIFFUSE_LIGHT += banded * ATTENUATION * LIGHT_COLOR * ALBEDO;
}