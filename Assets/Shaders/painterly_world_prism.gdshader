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

/** F8 diagnostic (ExpeditionWindow3D.ApplyDebugViz): 0 = normal,
    2 = toon banding bypassed (smooth wrapped diffuse), 5 = unshaded flat
    albedo via EMISSION. Isolates which layer paints an artifact. */
uniform int debug_mode = 0;

group_uniforms toon;
/** Diffuse band count. 3 reads painterly; more approaches smooth. */
uniform float toon_bands : hint_range(1.0, 6.0, 1.0) = 3.0;
/** Softness of each band edge (0 = hard cel step). */
uniform float toon_softness : hint_range(0.01, 0.49) = 0.18;
/** Light wrap so shadow sides keep colour instead of crushing. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.35;
/** Band-edge break-up: jitters the band threshold by world-space noise so
    lighting bands meet in brushy irregular edges instead of the long straight
    iso-lines the quantisation otherwise draws across continuously rolling
    ground (normals vary linearly per triangle ⇒ straight contours). Units are
    band widths peak-to-peak: 0.7 wobbles an edge by ±35% of a band.
    0 = geometric bands (default — atlas and props unchanged). */
uniform float toon_jitter : hint_range(0.0, 2.0) = 0.0;
/** World-space scale of the band jitter noise. */
uniform float toon_jitter_scale : hint_range(0.05, 5.0) = 1.3;
group_uniforms;

group_uniforms land_grain;
/** Albedo brush-grain strength on land tops (fraction of base colour). */
uniform float grain_strength : hint_range(0.0, 0.3) = 0.07;
/** World-space scale of the brush grain. */
uniform float grain_scale : hint_range(0.05, 5.0) = 0.9;
/** Gentle world-space undulation of land TOP surfaces, world units. 0 = flat
    lids (the strategic map). The expedition window sets ~0.07 on a subdivided
    tile mesh so the ground rolls naturally ACROSS tiles (world-space noise ⇒
    adjacent rims displace identically — no tearing at seams). */
uniform float top_undulation : hint_range(0.0, 0.2) = 0.0;
/** World-space scale of the undulation noise. */
uniform float undulation_scale : hint_range(0.05, 3.0) = 0.5;
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

group_uniforms color_map;
/** Land tops sample their colour from a blurred WORLD-SPACE colour map
    instead of Gouraud vertex colours. Vertex colours interpolate LINEARLY
    across the welded fans, and the gradient-direction kinks at fan/rim edges
    read as a faint straight-line lattice at tile spacing (Mach banding of the
    COLOUR field) — tessellation density cannot change that field. A blurred
    map is smooth everywhere. Fed per-rebuild by ExpeditionWindow3D. */
uniform bool use_color_map = false;
uniform sampler2D color_map : filter_linear, repeat_disable;
/** World XZ of the map's (0,0) corner, and 1/size in world units. */
uniform vec2 color_map_origin = vec2(0.0);
uniform vec2 color_map_inv_size = vec2(1.0);
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
	// Quintic fade (C2), not smoothstep (C1) — smoothstep kinks at every
	// noise-cell boundary and the axis-aligned lattice prints a rectangular
	// grid of faint straight lines on the shaded ground (see the matching
	// note on ExpeditionWindow3D.UNoise).
	f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a = h21(i);
	float b = h21(i + vec2(1.0, 0.0));
	float c = h21(i + vec2(0.0, 1.0));
	float d = h21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm2(vec2 p)
{
	// Rotated octaves (18° / 49°) — with both octaves axis-aligned their cell
	// boundaries reinforce into a regularly spaced rectangular line grid on
	// the terrain tops. Off-axis and mutually rotated, the lattice can't
	// chain into visible straight runs.
	mat2 r1 = mat2(vec2(0.9511, 0.3090), vec2(-0.3090, 0.9511));
	mat2 r2 = mat2(vec2(0.6561, 0.7547), vec2(-0.7547, 0.6561));
	return vnoise(r1 * p) * 0.65 + vnoise(r2 * p * 2.7 + 13.5) * 0.35;
}

void vertex()
{
	v_inst_color = COLOR;
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (mode == 0 && top_undulation > 0.0)
	{
		// Roll the land tops (object-space top cap sits at +0.5 on the unit
		// prism). Same world-unit conversion as the water swell; static — the
		// ground does not breathe.
		float top = step(0.49, VERTEX.y);
		float n = vnoise(wp.xz * undulation_scale)
		        + vnoise(wp.xz * undulation_scale * 2.4 + 31.7) * 0.5;
		float ys = max(length(MODEL_MATRIX[1].xyz), 0.0001);
		VERTEX.y += top * (n - 0.75) * top_undulation / ys;
		wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	}
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
		// Smooth ground colour from the world-space map (see color_map group);
		// walls sample it too, so the rim never shows a top-vs-wall tone line.
		if (use_color_map)
			base = texture(color_map, (v_world_pos.xz - color_map_origin) * color_map_inv_size).rgb;
		// Brush grain over everything, then the skirt treatment on side walls
		// (NORMAL.y ≈ 0 on the walls, ≈ 1 on tops). Same anti-alias fade as
		// the canvas fibre: undersampled grain must vanish, not streak.
		vec2 gp = v_world_pos.xz * grain_scale;
		float gw = fwidth(gp.x) + fwidth(gp.y);
		float gfade = clamp(1.5 - 2.0 * gw, 0.0, 1.0);
		float g = (fbm2(gp) - 0.5) * gfade;
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
		// Smooth paper tone from the shared world-space colour map (kills the
		// per-tile tone/wet-edge hex mosaic across the big unpainted field).
		if (use_color_map)
			base = texture(color_map, (v_world_pos.xz - color_map_origin) * color_map_inv_size).rgb;
		// Unpainted canvas: two-direction paper fibre, fully matte.
		// The fibre noise is strongly anisotropic (3.0 vs 0.4 scale) — at
		// distance/grazing angles it drops under Nyquist and ALIASES into long
		// straight streaks along the fibre axes (read as "lines interrupting
		// the map"). Fade the grain out as the pixel footprint approaches the
		// noise cell size instead of letting it alias.
		vec2 pfx = vec2(v_world_pos.x * paper_scale * 3.0, v_world_pos.z * paper_scale * 0.4);
		vec2 pfz = vec2(v_world_pos.x * paper_scale * 0.4, v_world_pos.z * paper_scale * 3.0);
		float pw = max(fwidth(pfx.x) + fwidth(pfx.y), fwidth(pfz.x) + fwidth(pfz.y));
		float pfade = clamp(1.5 - 2.0 * pw, 0.0, 1.0);
		base *= 1.0 + (vnoise(pfx) + vnoise(pfz) - 1.0) * paper_grain * pfade;
		rough = 1.0;
	}

	ALBEDO = base;
	ROUGHNESS = rough;
	if (debug_mode == 5)
	{
		// Unshaded diagnostic: colour rides EMISSION, lighting contributes
		// nothing (light() returns early; ambient multiplies the zero albedo).
		EMISSION = base;
		ALBEDO = vec3(0.0);
	}
	if (debug_mode == 6)
	{
		// CRACK TEST: perfectly uniform unlit grey — no colour map, no grain,
		// no lighting, no per-tile variation of any kind. A flawless mesh is
		// then a single blank grey shape. Any line that SURVIVES this is a
		// physical gap between triangles (background showing through) or an
		// MSAA edge — i.e. mesh topology, the only thing left.
		EMISSION = vec3(0.6);
		ALBEDO = vec3(0.0);
	}
}

void light()
{
	// Banded, wrapped toon diffuse (style guide §2.2, solid-geometry variant:
	// front faces only, so no abs() — walls in shadow band down, never black,
	// thanks to the wrap; scene ambient adds the floor).
	// NOTE: 'return' is illegal in the light processor — branch instead.
	// debug_mode 5/6 = unshaded diagnostics: EMISSION carries the colour, this
	// function contributes nothing.
	if (debug_mode != 5 && debug_mode != 6)
	{
		float nd = dot(NORMAL, LIGHT);
		float wrapped = clamp((nd + toon_wrap) / (1.0 + toon_wrap), 0.0, 1.0);
		// The canvas never receives the sun's SHADOW map: a huge flat sheet
		// across a long shadow range is the classic acne/banding receiver —
		// depth quantisation draws straight parallel stripes on it. The paper
		// is a stylised backdrop; sun lights it flat. Omni/spot lights (the
		// party lamp) keep their ATTENUATION falloff.
		float att = (mode == 2 && LIGHT_IS_DIRECTIONAL) ? 1.0 : ATTENUATION;
		if (debug_mode == 2)
		{
			DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * att * wrapped;   // smooth, no bands
		}
		else
		{
			float x = wrapped * toon_bands;
			if (toon_jitter > 0.0)
			{
				// Brushy band edges: push the threshold around with world-space
				// noise so band boundaries wander instead of tracing straight
				// N·L contours. Clamped to [0, bands] so jitter can neither go
				// negative (negative DIFFUSE_LIGHT) nor overshoot full bright.
				x += (fbm2(v_world_pos.xz * toon_jitter_scale) - 0.5) * toon_jitter;
				x = clamp(x, 0.0, toon_bands);
			}
			float band = (floor(x) + smoothstep(0.5 - toon_softness, 0.5 + toon_softness, fract(x))) / toon_bands;
			DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * att * band;
		}
	}

	if (debug_mode != 5 && mode == 1 && sparkle_strength > 0.0)
	{
		// Painterly glint: banded highlight gated by drifting noise dabs, so
		// the sun lays broken dashes on the swell instead of a PBR hotspot.
		vec3 h = normalize(VIEW + LIGHT);
		float spec = pow(max(dot(NORMAL, h), 0.0), 48.0);
		float dab = step(0.62, vnoise(v_world_pos.xz * sparkle_scale + TIME * 0.25));
		DIFFUSE_LIGHT += LIGHT_COLOR * ATTENUATION * spec * dab * sparkle_strength;
	}
}
