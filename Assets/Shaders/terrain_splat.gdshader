// ============================================================
// terrain_splat.gdshader
//
// Purpose:        Surface shader for the blended hex terrain.
//                 Blends up to FOUR terrain textures per fragment
//                 from a single Texture2DArray, driven by splat
//                 data baked into the mesh by HexMeshBuilder:
//                   CUSTOM0 = vec4 of terrain layer indices
//                             (constant across each triangle)
//                   COLOR   = vec4 of blend weights
//                             (varies per vertex)
//                 Sampling is triplanar in WORLD space, so there
//                 are no UV seams across tiles, and skirts /
//                 terrace risers don't streak. Steep faces darken
//                 automatically (replaces the old per-vertex
//                 skirt AO). Highlights arrive as per-material
//                 uniforms written by HexTile.SetTint.
// Layer:          Assets/Shaders
// Collaborators:  HexMeshBuilder.cs (splat data + geometry),
//                 TerrainTextureLibrary.cs (the texture array),
//                 HexTile.cs (highlight uniforms)
// Notes:          Layer index = (int)TileTerrainType. If the enum
//                 order ever changes, the array packing in
//                 TerrainTextureLibrary changes with it — they
//                 are coupled through that cast.
// ============================================================

shader_type spatial;

uniform sampler2DArray terrain_textures : source_color, filter_linear_mipmap, repeat_enable;

/// World units → UV. Lower = bigger texture features.
uniform float texture_scale : hint_range(0.02, 1.0) = 0.22;

uniform float roughness_value : hint_range(0.0, 1.0) = 0.85;

/// How much near-vertical faces (skirts, terrace cliffs) darken. Cheap fake AO.
uniform float wall_darkening : hint_range(0.0, 1.0) = 0.35;

/// Triplanar blend sharpness. Higher = crisper projection boundaries.
uniform float triplanar_sharpness : hint_range(1.0, 8.0) = 4.0;

/// Highlight tint — written per tile by HexTile.SetTint. strength 0 = no highlight.
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

// Triplanar sample of one array layer. bw = per-axis projection weights.
vec3 sample_layer(float layer, vec3 wp, vec3 bw) {
	vec3 cx = texture(terrain_textures, vec3(wp.zy * texture_scale, layer)).rgb;
	vec3 cy = texture(terrain_textures, vec3(wp.xz * texture_scale, layer)).rgb;
	vec3 cz = texture(terrain_textures, vec3(wp.xy * texture_scale, layer)).rgb;
	return cx * bw.x + cy * bw.y + cz * bw.z;
}

void fragment() {
	vec3 n = normalize(v_world_normal);
	vec3 bw = pow(abs(n), vec3(triplanar_sharpness));
	bw /= (bw.x + bw.y + bw.z);

	// Normalise weights (interpolation can drift the sum slightly off 1).
	vec4 w = v_weights;
	w /= max(w.r + w.g + w.b + w.a, 0.0001);

	// All four samples unconditionally — branching around texture() in
	// non-uniform control flow breaks mip derivatives at the branch seams.
	vec3 albedo =
		sample_layer(v_indices.r, v_world_pos, bw) * w.r +
		sample_layer(v_indices.g, v_world_pos, bw) * w.g +
		sample_layer(v_indices.b, v_world_pos, bw) * w.b +
		sample_layer(v_indices.a, v_world_pos, bw) * w.a;

	// Darken steep faces: map-edge skirts and terrace risers.
	float wall = smoothstep(0.45, 0.85, 1.0 - abs(n.y));
	albedo *= mix(1.0, 1.0 - wall_darkening, wall);

	ALBEDO = albedo;
	ROUGHNESS = roughness_value;
	EMISSION = highlight_color * highlight_strength;
}
