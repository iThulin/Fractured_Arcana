// ============================================================
// terrain_splat.gdshader  (v4 — pastel ground gradient)
//
// v4 — PASTEL GROUND GRADIENT:
// A soft pastel wash is layered over the blended terrain albedo,
// driven by WORLD HEIGHT (valleys -> hilltops) by default, or by a
// world-space noise field (random patches) when pastel_from_noise is
// on. It applies to the GROUND only: the overlay is weighted by
// (1 - cliff_mix), so steep faces keep their rock texture untouched.
// Lighting/normals are unaffected (albedo-only), so rolling hills
// still shade — you get flat pastel colour with full 3D form.
//
// v3 — CLIFF RETEXTURING (unchanged): steep faces blend toward a
// dedicated cliff/rock layer keyed off steepness (1 - |n.y|).
//
// REQUIRES: HexMeshBuilder calls st.GenerateTangents() after
// st.GenerateNormals(). Without tangents, NORMAL_MAP is inert.
// ============================================================

shader_type spatial;

uniform sampler2DArray terrain_textures : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2DArray terrain_normals : filter_linear_mipmap, repeat_enable;

uniform float texture_scale : hint_range(0.02, 1.0) = 0.22;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.85;
uniform float triplanar_sharpness : hint_range(1.0, 8.0) = 4.0;

/// Strength of the normal-map perturbation. Applied via NORMAL_MAP_DEPTH.
uniform float normal_strength : hint_range(0.0, 2.0) = 1.0;

/// Flip green channel if your maps are DirectX (-Y) instead of OpenGL (+Y).
uniform bool flip_normal_y = false;

// ── Cliff retexturing ──────────────────────────────────────────
uniform int cliff_layer = 4;
uniform float cliff_blend_start : hint_range(0.0, 1.0) = 0.55;
uniform float cliff_blend_sharpness : hint_range(0.5, 16.0) = 8.0;
uniform float wall_darkening : hint_range(0.0, 1.0) = 0.12;

// ── Pastel ground gradient ─────────────────────────────────────
group_uniforms pastel_ground;
uniform bool  use_pastel_ground = true;
/// Terrain layer the pastel applies to. Set to (int)TileTerrainType.Grass
/// (same convention as cliff_layer). Only this layer's ground gets the wash;
/// other terrain types are untouched, and grass↔other seams taper cleanly.
uniform int   grass_layer = 0;
/// 0 = keep terrain albedo, 1 = full flat pastel. Mid values tint while
/// keeping some texture grain.
uniform float pastel_strength : hint_range(0.0, 1.0) = 0.7;
/// Three-stop gradient: valley -> slope -> hilltop.
uniform vec3  pastel_low : source_color  = vec3(0.93, 0.89, 0.76);  // valleys (warm cream)
uniform vec3  pastel_mid : source_color  = vec3(0.80, 0.86, 0.64);  // slopes (soft sage)
uniform vec3  pastel_high : source_color = vec3(0.90, 0.94, 0.82);  // hilltops (pale mint)
uniform float pastel_mid_pos : hint_range(0.05, 0.95) = 0.5;
/// HEIGHT MODE: world Y that maps to gradient 0 (low) and 1 (high).
/// Set these to your terrain's valley/peak world heights.
uniform float pastel_height_low = 0.0;
uniform float pastel_height_high = 4.0;
/// NOISE MODE: drive the gradient from a world-space noise field instead of
/// height — random pastel patches rather than elevation shading.
uniform bool      pastel_from_noise = false;
uniform sampler2D pastel_noise_tex : hint_default_white, repeat_enable, filter_linear;
uniform float     pastel_noise_scale : hint_range(0.005, 0.4) = 0.05;

// ── Painterly ground detail (mottling + small stones) ──────────
group_uniforms ground_detail;
uniform bool      use_ground_detail = false;
/// Detail field. Use a CELLULAR (Voronoi) NoiseTexture2D for stone blobs,
/// or any seamless noise for soft mottling. Sampled in world XZ.
uniform sampler2D ground_detail_tex : hint_default_white, repeat_enable, filter_linear;
uniform float     ground_detail_scale : hint_range(0.05, 2.0) = 0.6;   // higher = smaller stones
/// Soft painterly value variation across the ground (breaks the flat pastel).
uniform float     ground_mottle : hint_range(0.0, 0.5) = 0.12;
/// Soil HUE variation: the detail field also blends the ground toward soil_tint
/// in its patches, so the ground breaks into warm/cool soil rather than one
/// flat colour. This is the part that sells "soil" under toon lighting.
uniform vec3      soil_tint : source_color = vec3(0.52, 0.40, 0.28);    // damp warm-brown patches
uniform float     soil_tint_strength : hint_range(0.0, 1.0) = 0.0;      // 0 = off; raise to ~0.3-0.5
/// SECOND noise field (independent scale) drives the soil-tint patches, so the
/// hue variation doesn't line up 1:1 with the value mottle — reads more natural.
uniform float     soil_tint_scale : hint_range(0.01, 1.0) = 0.12;       // lower = bigger patches
/// Small painterly stones painted where the stone field dips below threshold.
/// Stones use their OWN field (below) so pebble blobs don't share the smooth mottle.
uniform sampler2D stone_tex : hint_default_white, repeat_enable, filter_linear; // assign a CELLULAR (Voronoi) NoiseTexture2D
uniform float     stone_scale : hint_range(0.05, 2.0) = 0.5;           // higher = smaller, denser stones
uniform vec3      rock_color : source_color = vec3(0.55, 0.52, 0.48);
uniform float     rock_amount : hint_range(0.0, 1.0) = 0.0;             // 0 = no stones; raise to add them
uniform float     rock_threshold : hint_range(0.0, 1.0) = 0.42;        // how much of the field becomes stone
uniform float     rock_sharpness : hint_range(0.5, 16.0) = 5.0;        // lower = softer painted edge
uniform float     rock_variation : hint_range(0.0, 0.5) = 0.15;        // per-stone value jitter so they're not uniform

uniform vec3 highlight_color = vec3(0.0);
uniform float highlight_strength : hint_range(0.0, 3.0) = 0.0;

// ── Persistent hex grid lines (tile-edge darkening, painterly furrow) ──
group_uniforms grid_lines;
uniform bool  use_grid_lines = false;
uniform vec3  grid_line_color : source_color = vec3(0.18, 0.22, 0.16);
uniform float grid_hex_radius = 1.0;                            // = HexGridManager.HexRadius
uniform float grid_line_width : hint_range(0.0, 0.3) = 0.04;    // world units in from each edge (half the visible line)
uniform float grid_line_softness : hint_range(0.001, 0.2) = 0.02;
uniform float grid_line_strength : hint_range(0.0, 1.0) = 0.4;

varying vec3 v_world_pos;
varying vec3 v_world_normal;
varying vec4 v_indices;
varying vec4 v_weights;
varying vec2 v_local_xz;

void vertex() {
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_world_normal = MODEL_NORMAL_MATRIX * NORMAL;
	v_indices = CUSTOM0;
	v_weights = COLOR;
	v_local_xz = VERTEX.xz; // tile-local position (mesh is authored centred on the tile)
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

	// ── Base terrain albedo (the four splat layers) ──────────────
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

	// ── Sharp steepness blend ────────────────────────────────────
	float steep = 1.0 - abs(geom.y);                       // 0 flat .. 1 vertical
	float cliff_mix = smoothstep(0.0, 1.0,
		clamp((steep - cliff_blend_start) * cliff_blend_sharpness, 0.0, 1.0));

	vec3 albedo = mix(terrain_albedo, cliff_albedo, cliff_mix);
	vec3 tn = normalize(mix(terrain_n, cliff_n, cliff_mix));

	// Residual crevice darkening (small now — texture carries the face).
	albedo *= mix(1.0, 1.0 - wall_darkening, cliff_mix);

	// ── Pastel ground gradient (grassland only; cliffs keep their rock) ──
	if (use_pastel_ground) {
		// Grass mask: sum the weights of the layers whose terrain index is the
		// grass layer. 1.0 on a pure grass surface, 0.0 on non-grass tiles,
		// and it tapers across grass↔other blend seams.
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

		// Painterly ground detail: soft value mottling + soil hue variation +
		// small stone blobs, sampled in world XZ so it stays put as the camera moves.
		if (use_ground_detail) {
			float d = texture(ground_detail_tex, v_world_pos.xz * ground_detail_scale).r;
			pastel *= 1.0 + (d - 0.5) * (ground_mottle * 2.0);

			// Soil hue patches from a second, larger-scale sample of the field.
			float st = texture(ground_detail_tex, v_world_pos.xz * soil_tint_scale).r;
			pastel = mix(pastel, soil_tint, smoothstep(0.35, 0.85, st) * soil_tint_strength);

			// Stones from a dedicated cellular field (rounded pebble blobs), with
			// a touch of per-stone value jitter so they don't read as one flat colour.
			float sd = texture(stone_tex, v_world_pos.xz * stone_scale).r;
			float rock = (1.0 - smoothstep(rock_threshold, rock_threshold + 1.0 / rock_sharpness, sd)) * rock_amount;
			vec3 stone = rock_color * (1.0 + (sd - 0.5) * rock_variation * 2.0);
			pastel = mix(pastel, stone, rock);
		}

		float ground_mask = (1.0 - cliff_mix) * pastel_strength * grass_weight;
		albedo = mix(albedo, pastel, ground_mask);
	}

	// ── Persistent hex grid lines ──────────────────────────────────
	// Distance from this fragment to the nearest hex edge, computed in
	// tile-local space (exact footprint, unaffected by height noise).
	// Each tile paints its half of a shared edge, so neighbours meet seamlessly.
	if (use_grid_lines) {
		float apothem = grid_hex_radius * 0.8660254;
		float hx = max(abs(dot(v_local_xz, vec2(0.8660254, 0.5))),
			max(abs(dot(v_local_xz, vec2(0.0, 1.0))),
				abs(dot(v_local_xz, vec2(-0.8660254, 0.5)))));
		float dist_to_edge = apothem - hx;
		float line = 1.0 - smoothstep(grid_line_width, grid_line_width + grid_line_softness, dist_to_edge);
		float top_face = smoothstep(0.45, 0.8, abs(geom.y)); // suppress on cliff/skirt walls
		albedo = mix(albedo, grid_line_color, line * grid_line_strength * top_face);
	}

	ALBEDO = albedo;
	ROUGHNESS = roughness_value;
	NORMAL_MAP = tn * 0.5 + 0.5;
	NORMAL_MAP_DEPTH = normal_strength;
	EMISSION = highlight_color * highlight_strength;
}
