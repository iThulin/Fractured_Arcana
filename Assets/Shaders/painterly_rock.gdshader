shader_type spatial;
// ---------------------------------------------------------------------------
// painterly_rock.gdshader
//
// Direct port of the Blender "rock_shader" node graph (rock_shader.001/.002/
// .004/.009). The Blender material terminates in an Emission node at strength
// 1.0 — it is UNLIT / flat. This shader reproduces that by routing the colour
// through EMISSION and dialling ALBEDO down by the same amount, so shade_mix
// = 0.0 is a faithful flat match and higher values blend scene lighting back
// in if the rock otherwise floats off the terrain.
//
// Graph structure being reproduced, in order:
//   1. Plate mosaic  : Voronoi(SMOOTH_F1, scale 7).Color  soft-light
//                      Voronoi(scale 24.8).Color          -> flat per-cell plates
//   2. Base ramp     : 3-stop colour ramp over the mosaic value
//   3. Patch tints   : three noise-driven blotch mixes (Mix.007/.008/.009)
//   4. Height ramp   : linear gradient on normalised local height, broken up
//                      0.3 by the mosaic (so bands follow cell edges)
//   5. Lower mask    : MULTIPLY at 0.9  -> dark lower body
//   6. Top mask      : MIX toward a light flood colour -> sunlit top plate
//
// NOTE: this uses the Voronoi *Color* output (per-cell random colour), not the
// distance output. That is what makes flat plates instead of crack lines.
//
// Inspector tooltips use /** ... */ doc comments (two leading asterisks).
// ---------------------------------------------------------------------------
render_mode cull_back, specular_disabled;

// --------------------------------------------------------------- 1. mosaic --
group_uniforms coords;
/** Blender's "Generated" coords are 0..1 across the object bounding box — they
    are SCALE-INVARIANT, so a pebble and a boulder get the same number of
    voronoi cells across them. Raw local vertex coords are not: on a small mesh
    every procedural feature ends up larger than the rock and the whole rock
    collapses to one flat colour. On = project onto a unit sphere (scale-
    invariant, correct for blob-shaped rocks). Off = raw local space / height_extent. */
uniform bool normalize_coords = true;
/** Overall feature density multiplier once coords are normalised. */
uniform float coord_scale : hint_range(0.1, 4.0) = 1.0;

group_uniforms mosaic;
/** Scale of the large plates (Blender Voronoi scale 7). */
uniform float plate_scale : hint_range(0.5, 40.0) = 7.0;
/** Scale of the fine chips soft-light blended over the plates (Blender 24.8 / 43.0). */
uniform float chip_scale : hint_range(1.0, 80.0) = 24.8;
/** Blend of the fine chip layer over the plates (Blender Mix fac 0.55 / 1.0 / 0.45). */
uniform float chip_mix : hint_range(0.0, 1.0) = 0.55;
/** Cell edge softness. Blender drives this from a noise ramp; this scales it. */
uniform float plate_smoothness : hint_range(0.05, 1.0) = 0.55;
/** Cell centre jitter (Blender Randomness 0.6). */
uniform float plate_randomness : hint_range(0.0, 1.0) = 0.6;

// ------------------------------------------------------------ 2. base ramp --
group_uniforms base_ramp;
/** Ramp stop positions, ascending. Defaults are rock_shader.001 (cool blue-grey).
    NOTE: `source_color` uniforms are sRGB-encoded and Godot converts them to
    linear. The node-to-python dump gives LINEAR values, so every colour here is
    the Blender value re-encoded to sRGB — otherwise it lands ~8x too dark. */
uniform float ramp_pos_0 : hint_range(0.0, 1.0) = 0.0409;
uniform float ramp_pos_1 : hint_range(0.0, 1.0) = 0.6659;
uniform float ramp_pos_2 : hint_range(0.0, 1.0) = 0.9682;
uniform vec3 ramp_col_0 : source_color = vec3(0.423, 0.479, 0.544);
uniform vec3 ramp_col_1 : source_color = vec3(0.329, 0.345, 0.271);
uniform vec3 ramp_col_2 : source_color = vec3(0.590, 0.615, 0.670);

// ----------------------------------------------------------- 3. patch tints --
group_uniforms patch_tints;
/** Mix.007 — noise scale 5, dark accent blotches. */
uniform vec3 patch_a_color : source_color = vec3(0.111, 0.0, 0.316);
uniform float patch_a_scale : hint_range(0.5, 40.0) = 5.0;
uniform float patch_a_strength : hint_range(0.0, 1.0) = 1.0;
/** Mix.008 — noise scale 2, mossy green. */
uniform vec3 patch_b_color : source_color = vec3(0.362, 0.431, 0.155);
uniform float patch_b_scale : hint_range(0.5, 40.0) = 2.0;
uniform float patch_b_strength : hint_range(0.0, 1.0) = 1.0;
/** Mix.009 — noise scale 4, teal wash. */
uniform vec3 patch_c_color : source_color = vec3(0.540, 0.735, 0.721);
uniform float patch_c_scale : hint_range(0.5, 40.0) = 4.0;
uniform float patch_c_strength : hint_range(0.0, 1.0) = 1.0;
/** Distribution width of the blotch noise. Blender's Noise Texture is tighter
    around 0.5 than a plain value-noise FBM, so without this correction far more
    of the surface crosses the 0.582 blotch threshold and the rock drowns in
    patch colour. Lower = fewer, smaller blotches. */
uniform float patch_coverage : hint_range(0.1, 2.0) = 0.65;

// --------------------------------------------------------- 4/5/6. height ----
group_uniforms height_masks;
/** How much the plate mosaic breaks up the height bands (Blender Mix.003 fac 0.3). */
uniform float height_break : hint_range(0.0, 1.0) = 0.3;
/** Only used when normalize_coords is OFF: local-space half-height in metres. */
uniform float height_extent : hint_range(0.1, 20.0) = 1.0;
/** Lower mask ramp (ColorRamp.002): below lo = fully shaded, above hi = clean. */
uniform float lower_lo : hint_range(0.0, 1.0) = 0.3909;
uniform float lower_hi : hint_range(0.0, 1.0) = 0.4909;
/** Strength of the MULTIPLY darkening on the lower body (Blender Mix.004 fac 0.9). */
uniform float lower_darken : hint_range(0.0, 1.0) = 0.9;
/** Floor on the lower-body multiply, so the base does not go pure black. */
uniform float lower_floor : hint_range(0.0, 1.0) = 0.30;
/** Top mask ramp (ColorRamp.003). */
uniform float top_lo : hint_range(0.0, 1.0) = 0.7182;
uniform float top_hi : hint_range(0.0, 1.0) = 0.8455;
/** Sunlit top flood colour (Blender Mix.005 B input). */
uniform vec3 top_color : source_color = vec3(0.663, 0.663, 0.600);
/** Ceiling on the top flood, so tops do not blow out completely. */
uniform float top_strength : hint_range(0.0, 1.0) = 1.0;

// ------------------------------------------------------------ instancing ----
group_uniforms instancing;
/** Multiply the result by the per-instance palette tint from INSTANCE_CUSTOM. */
uniform bool use_instance_tint = true;
/** Tint used when no custom data is supplied (previewing the mesh alone). */
uniform vec3 fallback_tint : source_color = vec3(1.0, 1.0, 1.0);
/** Per-instance noise offset, so identical meshes do not share a mosaic. */
uniform float instance_variation : hint_range(0.0, 50.0) = 12.0;
/** Saturation. 1.0 = unchanged. */
uniform float saturation : hint_range(0.0, 2.0) = 1.0;
/** Overall brightness trim. */
uniform float brightness : hint_range(0.0, 3.0) = 1.0;

// ---------------------------------------------------------------- lighting --
group_uniforms lighting;
/** 0.0 = fully unlit (matches the Blender Emission output). 1.0 = full toon shade. */
uniform float shade_mix : hint_range(0.0, 1.0) = 0.25;
/** Toon band threshold. */
uniform float shade_threshold : hint_range(0.0, 1.0) = 0.45;
/** How dark the toon shadow band gets. */
uniform float shade_depth : hint_range(0.0, 1.0) = 0.45;

group_uniforms debug;
/** Isolate a stage to diagnose a bad look. 0 = off, 1 = plate mosaic,
    2 = height gradient, 3 = lower mask, 4 = top mask, 5 = base ramp only
    (no patches, no height masks), 6 = combined blotch coverage. */
uniform int debug_view : hint_range(0, 6) = 0;

// ---------------------------------------------------------------- varyings --
varying vec3 v_local_pos;
varying vec3 v_seed;
varying vec3 v_tint;

// ------------------------------------------------------------------ noise ---
vec3 rk_hash3(vec3 p) {
	p = vec3(dot(p, vec3(127.1, 311.7, 74.7)),
	         dot(p, vec3(269.5, 183.3, 246.1)),
	         dot(p, vec3(113.5, 271.9, 124.6)));
	return fract(sin(p) * 43758.5453123);
}

float rk_hash1(vec3 p) {
	return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453123);
}

float rk_value_noise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = rk_hash1(i + vec3(0.0, 0.0, 0.0));
	float n100 = rk_hash1(i + vec3(1.0, 0.0, 0.0));
	float n010 = rk_hash1(i + vec3(0.0, 1.0, 0.0));
	float n110 = rk_hash1(i + vec3(1.0, 1.0, 0.0));
	float n001 = rk_hash1(i + vec3(0.0, 0.0, 1.0));
	float n101 = rk_hash1(i + vec3(1.0, 0.0, 1.0));
	float n011 = rk_hash1(i + vec3(0.0, 1.0, 1.0));
	float n111 = rk_hash1(i + vec3(1.0, 1.0, 1.0));
	return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
	           mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

// FBM equivalent of Blender's Noise Texture (fac output).
float rk_fbm(vec3 p, int octaves) {
	float sum = 0.0;
	float amp = 0.5;
	float norm = 0.0;
	for (int i = 0; i < octaves; i++) {
		sum += rk_value_noise(p) * amp;
		norm += amp;
		p *= 2.0;
		amp *= 0.5;
	}
	return sum / max(norm, 0.0001);
}

// Blender Voronoi 3D, SMOOTH_F1, "Color" output: the random colour of the
// winning cell, softly blended between neighbours by `smoothness`. Returned
// as a single luminance value, which is what the downstream graph consumes.
float rk_voronoi_color(vec3 p, float randomness, float smoothness) {
	vec3 base = floor(p);
	vec3 f = fract(p);
	float sm = max(smoothness, 0.001) * 0.5;

	float min_d = 8.0;
	for (int x = -1; x <= 1; x++) {
		for (int y = -1; y <= 1; y++) {
			for (int z = -1; z <= 1; z++) {
				vec3 cell = vec3(float(x), float(y), float(z));
				vec3 h = rk_hash3(base + cell);
				vec3 point = cell + (h - 0.5) * randomness + 0.5;
				min_d = min(min_d, length(point - f));
			}
		}
	}

	float acc = 0.0;
	float wsum = 0.0;
	for (int x = -1; x <= 1; x++) {
		for (int y = -1; y <= 1; y++) {
			for (int z = -1; z <= 1; z++) {
				vec3 cell = vec3(float(x), float(y), float(z));
				vec3 h = rk_hash3(base + cell);
				vec3 point = cell + (h - 0.5) * randomness + 0.5;
				float d = length(point - f);
				// per-cell random "colour", collapsed to luminance
				float c = dot(rk_hash3(base + cell + vec3(17.0)), vec3(0.3333));
				float w = exp(-(d - min_d) / sm);
				acc += c * w;
				wsum += w;
			}
		}
	}
	return clamp(acc / max(wsum, 0.0001), 0.0, 1.0);
}

// Blender soft-light blend (scalar form).
float rk_soft_light(float b, float s) {
	float lo = b - (1.0 - 2.0 * s) * b * (1.0 - b);
	float d = (b < 0.25) ? ((16.0 * b - 12.0) * b + 4.0) * b : sqrt(max(b, 0.0));
	float hi = b + (2.0 * s - 1.0) * (d - b);
	return (s < 0.5) ? lo : hi;
}

// Three-stop colour ramp with Blender's linear interpolation.
vec3 rk_ramp3(float t, float p0, vec3 c0, float p1, vec3 c1, float p2, vec3 c2) {
	if (t <= p0) { return c0; }
	if (t >= p2) { return c2; }
	if (t < p1) { return mix(c0, c1, (t - p0) / max(p1 - p0, 0.0001)); }
	return mix(c1, c2, (t - p1) / max(p2 - p1, 0.0001));
}

// The blotch factor ramp shared by Mix.007/.008/.009: black at 0.582,
// 0.7439 at 0.591, 0.3720 at 0.711 — a hard-edged blotch that spikes just
// past the threshold and settles back to a partial wash.
float rk_blotch(float n_raw, float coverage) {
	// Narrow the FBM distribution around 0.5 to match Blender's Noise Texture.
	float n = 0.5 + (n_raw - 0.5) * coverage;
	if (n <= 0.582) { return 0.0; }
	if (n < 0.591) { return mix(0.0, 0.7439, (n - 0.582) / 0.009); }
	if (n < 0.711) { return mix(0.7439, 0.3720, (n - 0.591) / 0.120); }
	return 0.3720;
}

void vertex() {
	v_local_pos = VERTEX;
	// Blender feeds Object Info -> Location into the Mapping node so every
	// instance samples a different part of the noise field. The instance
	// world origin is the equivalent hook here.
	vec3 origin = MODEL_MATRIX[3].xyz;
	v_seed = fract(origin * 0.13) * instance_variation;
	v_tint = fallback_tint;
	if (use_instance_tint && dot(INSTANCE_CUSTOM.rgb, vec3(1.0)) > 0.0) {
		v_tint = INSTANCE_CUSTOM.rgb;
	}
}

void fragment() {
	// --- 0. object-space coords (Blender "Generated" equivalent) ------------
	vec3 lp = v_local_pos;
	vec3 dir = lp / max(length(lp), 0.0001);
	vec3 obj = normalize_coords
		? dir * coord_scale
		: lp / max(height_extent, 0.0001);
	// Normalised height: -1..1 down the object, remapped to 0..1.
	float h = clamp((normalize_coords ? dir.y : lp.y / max(height_extent, 0.0001))
		* 0.5 + 0.5, 0.0, 1.0);
	vec3 p = obj + v_seed;

	// --- 1. plate mosaic ---------------------------------------------------
	// Blender drives Smoothness from a scale-100 detail-15 noise ramp; that
	// high-frequency wobble on the cell edges is what stops the plates from
	// reading as a regular lattice.
	float sm_noise = rk_fbm(p * 100.0, 3);
	float smoothness = clamp(mix(0.318, 1.0, sm_noise) * plate_smoothness, 0.05, 1.0);

	float plates = rk_voronoi_color(p * plate_scale, plate_randomness, smoothness);
	float chips  = rk_voronoi_color(p * chip_scale,  plate_randomness, smoothness);
	float mosaic = clamp(mix(plates, rk_soft_light(plates, chips), chip_mix), 0.0, 1.0);

	// --- 2. base ramp ------------------------------------------------------
	vec3 col = rk_ramp3(mosaic,
		ramp_pos_0, ramp_col_0,
		ramp_pos_1, ramp_col_1,
		ramp_pos_2, ramp_col_2);

	vec3 base_only = col;

	// --- 3. patch tints ----------------------------------------------------
	float na = rk_fbm(p * patch_a_scale, 4);
	col = mix(col, patch_a_color, rk_blotch(na, patch_coverage) * patch_a_strength);
	float nb = rk_fbm(p * patch_b_scale + vec3(31.0), 4);
	col = mix(col, patch_b_color, rk_blotch(nb, patch_coverage) * patch_b_strength);
	float nc = rk_fbm(p * patch_c_scale + vec3(73.0), 4);
	col = mix(col, patch_c_color, rk_blotch(nc, patch_coverage) * patch_c_strength);

	// --- 4. height gradient, broken by the mosaic --------------------------
	// Blender uses a LINEAR Gradient Texture over rotated Generated coords —
	// normalised height inside the object bounding box. `h` is computed at the
	// top of fragment() alongside the coord normalisation.
	float grad = mix(h, mosaic, height_break);

	// --- 5. lower mask MULTIPLY -------------------------------------------
	float lower = mix(lower_floor, 1.0, smoothstep(lower_lo, lower_hi, grad));
	col = mix(col, col * lower, lower_darken);

	// --- 6. top mask flood -------------------------------------------------
	float top = smoothstep(top_lo, top_hi, grad);
	col = mix(col, top_color, top * top_strength);

	// --- instance tint / grade --------------------------------------------
	col *= v_tint;
	float lum = dot(col, vec3(0.2126, 0.7152, 0.0722));
	col = max(mix(vec3(lum), col, saturation) * brightness, vec3(0.0));

	// --- debug isolation ---------------------------------------------------
	if (debug_view == 1) { col = vec3(mosaic); }
	if (debug_view == 2) { col = vec3(grad); }
	if (debug_view == 3) { col = vec3(lower); }
	if (debug_view == 4) { col = vec3(top); }
	if (debug_view == 5) { col = base_only; }
	if (debug_view == 6) {
		col = vec3(rk_blotch(na, patch_coverage),
		           rk_blotch(nb, patch_coverage),
		           rk_blotch(nc, patch_coverage));
	}

	// The Blender material is Emission-only. Route (1 - shade_mix) of the
	// colour through EMISSION so shade_mix = 0 is exactly flat, and leave the
	// remainder in ALBEDO for the toon light() pass below.
	ALBEDO = col * shade_mix;
	EMISSION = col * (1.0 - shade_mix);
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
}

void light() {
	float ndotl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
	float band = mix(1.0 - shade_depth, 1.0, step(shade_threshold, ndotl));
	DIFFUSE_LIGHT += ALBEDO * ATTENUATION * band;
}
