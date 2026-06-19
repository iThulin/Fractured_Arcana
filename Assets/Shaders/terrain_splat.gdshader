// ============================================================
// terrain_splat.gdshader  (v3 — cliff retexturing)
//
// Blends up to FOUR terrain layers per fragment from albedo +
// normal Texture2DArrays, driven by splat data baked by
// HexMeshBuilder (CUSTOM0 = layer indices, COLOR = weights).
// Albedo + normal are triplanar in WORLD space.
//
// v3 — CLIFF RETEXTURING:
// Steep faces (cliffs, skirts) no longer just darken — they blend
// SHARPLY toward a dedicated cliff layer (default: Stone) so the
// black walls become rock. The blend is keyed off surface
// steepness (1 - |n.y|), so flat tops keep their terrain right up
// to the rim and only the near-vertical face turns to rock.
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
/// Texture-array layer used for cliff/steep faces. Set to the
/// (int)TileTerrainType value of Stone (or a dedicated rock layer).
uniform int cliff_layer = 4;
/// Steepness (1 - |n.y|) at which the cliff blend BEGINS. Higher =
/// the rock only appears on near-vertical faces (sharper, stays on
/// the face). 0.55 keeps grass on the rim, rock on the wall.
uniform float cliff_blend_start : hint_range(0.0, 1.0) = 0.55;
/// Blend sharpness. Higher = crisper grass→rock boundary. Large for
/// the "sharp" look.
uniform float cliff_blend_sharpness : hint_range(0.5, 16.0) = 8.0;
/// Residual darkening in the deepest crevices, on top of the rock
/// texture. Small — the texture now carries the face, not blackness.
uniform float wall_darkening : hint_range(0.0, 1.0) = 0.12;

uniform vec3 highlight_color = vec3(0.0);
uniform float highlight_strength : hint_range(0.0, 3.0) = 0.0;

varying vec3 v_world_pos;
varying vec3 v_world_normal;
varying vec4 v_indices;
varying vec4 v_weights;

void vertex() {
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_world_normal = MODEL_NORMAL_MATRIX * NORMAL;
	v_indices = CUSTOM0;
	v_weights = COLOR;
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

// Tangent-space normal sampled on the dominant (top) projection.
vec3 sample_normal_top(float layer, vec3 wp) {
	return decode_n(texture(terrain_normals, vec3(wp.xz * texture_scale, layer)).rgb);
}

// For cliff faces the dominant projection is the side, so sample on
// the zy/xy planes blended by the horizontal components.
vec3 sample_normal_wall(float layer, vec3 wp, vec3 bw) {
	vec3 nx = decode_n(texture(terrain_normals, vec3(wp.zy * texture_scale, layer)).rgb);
	vec3 nz = decode_n(texture(terrain_normals, vec3(wp.xy * texture_scale, layer)).rgb);
	// Weight by the two horizontal projection axes (ignore the tiny y).
	float wsum = max(bw.x + bw.z, 0.0001);
	return (nx * bw.x + nz * bw.z) / wsum;
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

	ALBEDO = albedo;
	ROUGHNESS = roughness_value;
	NORMAL_MAP = tn * 0.5 + 0.5;
	NORMAL_MAP_DEPTH = normal_strength;
	EMISSION = highlight_color * highlight_strength;
}
