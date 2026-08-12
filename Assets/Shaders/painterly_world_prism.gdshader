shader_type spatial;
// ============================================================
// painterly_world_prism.gdshader — art pass A2/A3-lite (2026-08-12)
//
// One shader for the 3D world-map tile MultiMeshes (WorldAtlas3D +
// ExpeditionWindow3D), selected per layer via `mode`:
//   0 = LAND   — brush grain on tops, darkened striated skirts, banded toon light
//   1 = WATER  — gentle world-space swell, sky-band gradient, banded sun sparkle
//   2 = CANVAS — paper-fiber grain, matte (the unpainted world, A6)
//
// Rules honoured (painterly_style_guide.md):
// - FULLY OPAQUE: never writes ALPHA (MultiMesh sort law §8).
// - specular_disabled; all lighting through the custom banded light().
// - World-space noise so adjacent prisms agree at shared rims — the water
//   swell is continuous across tiles by construction.
// - Per-instance colour arrives via COLOR (MultiMesh instance colour;
//   the meshes carry no vertex colours, so no multiply trap).
// ============================================================
render_mode specular_disabled;

/** Layer select: 0 land, 1 water, 2 canvas. Set by PainterlyPrism.cs. */
uniform int mode = 0;

group_uniforms toon;
/** Diffuse band count. 3 reads painterly; more approaches smooth. */
uniform float toon_bands : hint_range(1.0, 6.0, 1.0) = 3.0;
/** Softness of each band edge (0 = hard cel step). */
uniform float toon_softness : hint_range(0.01, 0.49) = 0.18;
/** Light wrap so shadow sides keep colour instead of crushing. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.35;
group_uniforms;

group_uniforms land_grain;
/** Albedo brush-grain strength on land tops (fraction of base colour). */
uniform float grain_strength : hint_range(0.0, 0.3) = 0.07;
/** World-space scale of the brush grain. */
uniform float grain_scale : hint_range(0.05, 5.0) = 0.9;
group_uniforms;

group_uniforms skirt;
/** How much prism side walls darken vs tops (painted cliff bands). */
uniform float skirt_darken : hint_range(0.0, 0.6) = 0.26;
/** Strength of the vertical striation noise on side walls. */
uniform float stripe_strength : hint_range(0.0, 0.5) = 0.16;
/** Vertical scale of the striations. */
uniform float stripe_scale : hint_range(0.2, 10.0) = 2.4;
group_uniforms;

group_uniforms water;
/** Swell height in WORLD units (kept small: prism facets, not surf). */
uniform float swell_amplitude : hint_range(0.0, 0.1) = 0.025;
/** World-space scale of the swell noise. */
uniform float swell_scale : hint_range(0.005, 0.3) = 0.05;
/** Swell drift speed. */
uniform float swell_speed : hint_range(0.0, 2.0) = 0.3;
/** How much the sky-band gradient mixes over the instance colour. */
uniform float sky_mix : hint_range(0.0, 1.0) = 0.30;
/** Sky band colour over shallows / swell crests. */
uniform vec3 sky_shallow : source_color = vec3(0.55, 0.62, 0.66);
/** Sky band colour over deeps / swell troughs. */
uniform vec3 sky_deep : source_color = vec3(0.33, 0.42, 0.50);
/** Sun-glint dab strength (banded, painterly — not PBR specular). */
uniform float sparkle_strength : hint_range(0.0, 1.0) = 0.35;
/** World-space scale of the glint dab mask. */
uniform float sparkle_scale : hint_range(0.2, 10.0) = 3.0;
group_uniforms;

group_uniforms canvas;
/** Paper-fibre grain strength on the unpainted world. */
uniform float paper_grain : hint_range(0.0, 0.2) = 0.05;
/** World-space scale of the paper fibre. */
uniform float paper_scale : hint_range(0.2, 10.0) = 2.6;
group_uniforms;

varying vec3 v_world_pos;
varying vec4 v_inst_color;

// Cheap deterministic value noise (no texture dependency).
float h21(vec2 p)
{
	p = fract(p * vec2(123.34, 345.45));
	p += dot(p, p + 34.345);
	return fract(p.x * p.y);
}

float vnoise(vec2 p)
{
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = h21(i);
	float b = h21(i + vec2(1.0, 0.0));
	float c = h21(i + vec2(0.0, 1.0));
	float d = h21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm2(vec2 p)
{
	return vnoise(p) * 0.65 + vnoise(p * 2.7 + 13.5) * 0.35;
}

void vertex()
{
	v_inst_color = COLOR;
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (mode == 1)
	{
		// World-space swell on TOP vertices only (object-space top cap sits at
		// +Y). Identical world XZ ⇒ identical offset, so adjacent prisms stay
		// welded. Object-space Y is scaled by the instance height, so convert
		// the world-unit amplitude back through the basis' Y length.
		float top = step(0.0, VERTEX.y);
		float n = vnoise(wp.xz * swell_scale + TIME * swell_speed)
		        + vnoise(wp.xz * swell_scale * 2.3 - TIME * swell_speed * 0.7);
		float disp = (n - 1.0) * swell_amplitude;   // n in ~[0,2] → centred
		float ys = max(length(MODEL_MATRIX[1].xyz), 0.0001);
		VERTEX.y += top * disp / ys;
		wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	}
	v_world_pos = wp;
}

void fragment()
{
	vec3 base = v_inst_color.rgb;
	float rough = 0.9;

	if (mode == 0)
	{
		// Brush grain over everything, then the skirt treatment on side walls
		// (NORMAL.y ≈ 0 on the walls, ≈ 1 on tops).
		float g = fbm2(v_world_pos.xz * grain_scale) - 0.5;
		base *= 1.0 + g * 2.0 * grain_strength;
		float wall = 1.0 - clamp(abs(NORMAL.y) * 2.0, 0.0, 1.0);
		if (wall > 0.0)
		{
			float stripes = vnoise(vec2((v_world_pos.x + v_world_pos.z) * 0.9,
			                            v_world_pos.y * stripe_scale)) - 0.5;
			base *= 1.0 - wall * skirt_darken;
			base *= 1.0 + wall * stripes * 2.0 * stripe_strength;
		}
	}
	else if (mode == 1)
	{
		// Painted-sea read: the instance colour (depth-banded by Hex3DPalette)
		// stays the body; a drifting two-tone sky band washes over it.
		float band = fbm2(v_world_pos.xz * swell_scale * 1.6 + TIME * swell_speed * 0.5);
		vec3 sky = mix(sky_deep, sky_shallow, smoothstep(0.25, 0.75, band));
		base = mix(base, sky, sky_mix);
		rough = 0.55;
	}
	else
	{
		// Unpainted canvas: two-direction paper fibre, fully matte.
		float fx = vnoise(vec2(v_world_pos.x * paper_scale * 3.0, v_world_pos.z * paper_scale * 0.4));
		float fz = vnoise(vec2(v_world_pos.x * paper_scale * 0.4, v_world_pos.z * paper_scale * 3.0));
		base *= 1.0 + (fx + fz - 1.0) * paper_grain;
		rough = 1.0;
	}

	ALBEDO = base;
	ROUGHNESS = rough;
}

void light()
{
	// Banded, wrapped toon diffuse (style guide §2.2, solid-geometry variant:
	// front faces only, so no abs() — walls in shadow band down, never black,
	// thanks to the wrap; scene ambient adds the floor).
	float nd = dot(NORMAL, LIGHT);
	float wrapped = clamp((nd + toon_wrap) / (1.0 + toon_wrap), 0.0, 1.0);
	float x = wrapped * toon_bands;
	float band = (floor(x) + smoothstep(0.5 - toon_softness, 0.5 + toon_softness, fract(x))) / toon_bands;
	DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * ATTENUATION * band;

	if (mode == 1 && sparkle_strength > 0.0)
	{
		// Painterly glint: banded highlight gated by drifting noise dabs, so
		// the sun lays broken dashes on the swell instead of a PBR hotspot.
		vec3 h = normalize(VIEW + LIGHT);
		float spec = pow(max(dot(NORMAL, h), 0.0), 48.0);
		float dab = step(0.62, vnoise(v_world_pos.xz * sparkle_scale + TIME * 0.25));
		DIFFUSE_LIGHT += LIGHT_COLOR * ATTENUATION * spec * dab * sparkle_strength;
	}
}
