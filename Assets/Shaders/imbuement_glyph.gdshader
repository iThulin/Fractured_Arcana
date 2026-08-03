shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

// ============================================================
// imbuement_glyph.gdshader
//
// Purpose:        The element rune hovering over an imbued hex.
// Layer:          Tiles / VFX
// Collaborators:  ImbuementOverlay.cs (sets element_id + tint_color),
//                 Scenes/Combat/ImbuementOverlay.tscn (material + wind_noise),
//                 painterly_grass.gdshader (the wind this now shares)
// See:            docs/imbuement_painterly_redesign_v1.md §3.3, §3.5
// ============================================================
//
// PAINTERLY PASS 1 — what changed and why.
//
// 1. THE HAND.
//    These eight runes are analytic SDFs: a mathematically perfect
//    snowflake, a perfect hexagram, a bolt made of exact line segments. The
//    glyph cipher's marks are hand-inked polylines with seeded jitter and
//    stroke-width variation, and that difference is most of why the two
//    systems look like they came from different games.
//
//    `ink_wobble` warps the sampling coordinate with low-frequency noise
//    before the SDF is evaluated. Because every rune here is a threshold on
//    a distance, warping the input both bends the strokes AND varies their
//    width — one term buys both, and it applies to all eight branches
//    without touching any of them.
//
//    This is a preview of the real fix, not the real fix. Redesign §3.3 is
//    to bake these through GlyphCipherView so they are drawn by literally
//    the same code that draws an Enchanter's sigil. That is step 5.
//
// 2. ONE WIND, NOT EIGHT CLOCKS.
//    Frost rotated at TIME * 0.3, air at 0.6, arcane at 0.2, fire jittered
//    at TIME * 8.0. All of it now rides the shared world-space gust, name
//    for name and value for value with painterly_grass.tres — including the
//    pulse, which now crests when the field crests.
//
//    `wind_noise` MUST be Painterly_wind_noise.tres, the same asset the
//    grass samples. A different noise is a different phase.
//
// 3. MUTED.
//    `intensity` capped at 2.0 (was 4.0), default 1.1; `alpha_ceiling` stops
//    the additive blend blowing to white over bright terrain.
//
// DELIBERATELY UNCHANGED: `blend_add` + writing `ALPHA`. See the note at the
// head of imbuement_aura.gdshader — this pass is reversible by design.
// ============================================================

// Element selector — driven from C#:
//   0 = Fire, 1 = Frost, 2 = Lightning, 3 = Earth,
//   4 = Water, 5 = Air, 6 = Arcane, 7 = Shadow
uniform int element_id : hint_range(0, 7) = 0;

/** Element colour. Driven per tile from UITheme.ElementTint* via ImbuementOverlay. */
uniform vec4 tint_color : source_color = vec4(0.86, 0.44, 0.22, 1.0);

/** Ink gain. Capped at 2.0 — 4.0 existed only to blow out. */
uniform float intensity : hint_range(0.0, 2.0) = 1.1;

/** Hard cap on emitted alpha. The additive blend has no other brake. */
uniform float alpha_ceiling : hint_range(0.0, 1.0) = 0.72;

/** Breathing rate, in gust-relative units rather than raw seconds. */
uniform float pulse_speed : hint_range(0.0, 4.0) = 0.9;

/** Depth of that breath. */
uniform float pulse_amount : hint_range(0.0, 1.0) = 0.22;

group_uniforms ink;

/** Warp applied to the SDF input. Bends strokes and varies their width at once. */
uniform float ink_wobble : hint_range(0.0, 0.1) = 0.022;

/** Frequency of that warp. Low = a slow hand, high = a shaky one. */
uniform float ink_wobble_scale : hint_range(0.5, 16.0) = 6.0;

/** How much of the rune's drift comes from the gust vs. its own slow turn. */
uniform float turn_rate : hint_range(0.0, 1.0) = 0.18;

group_uniforms;

group_uniforms rune;

/**
 * Baked hand-inked rune from ElementRunes.cs, drawn by GlyphCipherView — the same
 * renderer that draws an Enchanter's spell sigil. Set from ImbuementOverlay.cs.
 *
 * ONLY THE ALPHA IS READ. Colour comes from tint_color, because the tile's element
 * tint is the thing the player is actually reading and it must not be filtered
 * through whatever ink colour the bake happened to use.
 */
uniform sampler2D rune_tex : hint_default_black, filter_linear_mipmap;

/**
 * False until a bake has actually landed. The analytic glyphs below stay as the
 * fallback and are NOT dead code: a tile with no element marker is a gameplay bug,
 * so every failure path — no autoload, unknown element, empty viewport, bake in
 * flight — must still produce a readable symbol.
 */
uniform bool use_rune_tex = false;

/** Fraction of the quad the rune fills. Below 1.0 the rim gets breathing room. */
uniform float rune_scale : hint_range(0.3, 1.5) = 0.98;

group_uniforms;

group_uniforms wind;

/** MUST be Painterly_wind_noise.tres — the same asset the grass samples. */
uniform sampler2D wind_noise : hint_default_black, repeat_enable, filter_linear;

/** Gust direction in world XZ. Match painterly_grass.tres. */
uniform vec2  wind_dir = vec2(1.0, 0.35);

/** Constant lean added to the gust. Match painterly_grass.tres. */
uniform float wind_amplitude : hint_range(0.0, 1.0) = 0.10;

/** Main wave world scale. Match painterly_grass.tres. */
uniform float wave_scale : hint_range(0.01, 1.0) = 0.075;

/** Main wave travel speed. Match painterly_grass.tres. */
uniform float wave_speed : hint_range(0.0, 3.0) = 0.075;

/** Main wave weight. Match painterly_grass.tres. */
uniform float wave_strength : hint_range(0.0, 2.0) = 1.0;

/** Across-wind stretch of the wave. Match painterly_grass.tres. */
uniform float wave_stretch : hint_range(0.05, 1.0) = 0.599;

/** Detail flutter world scale. Match painterly_grass.tres. */
uniform float detail_scale : hint_range(0.1, 6.0) = 0.10;

/** Detail flutter speed. Match painterly_grass.tres. */
uniform float detail_speed : hint_range(0.0, 3.0) = 0.035;

/** Detail flutter weight. Match painterly_grass.tres. */
uniform float detail_strength : hint_range(0.0, 1.0) = 0.22;

/** Base motion speed, before the per-element rate. Sits under the gust. */
uniform float flow_speed : hint_range(0.0, 2.0) = 0.35;

group_uniforms;

// Full gust (moving part + constant lean), exactly as the grass computes it.
varying float v_gust;
// Moving part of the gust only.
varying float v_flow;

// ─────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

// Rotate a UV around (0.5, 0.5)
vec2 rotate_uv(vec2 uv, float angle) {
    vec2 c = uv - 0.5;
    float s = sin(angle);
    float co = cos(angle);
    return vec2(c.x * co - c.y * s, c.x * s + c.y * co) + 0.5;
}

// Smooth ring at radius r, width w
float ring(vec2 uv, float r, float w) {
    float d = abs(distance(uv, vec2(0.5)) - r);
    return 1.0 - smoothstep(0.0, w, d);
}

// Solid disc at radius r, soft edge w
float disc(vec2 uv, float r, float w) {
    float d = distance(uv, vec2(0.5));
    return 1.0 - smoothstep(r - w, r + w, d);
}

// N-pointed star pattern in polar coords
float star(vec2 uv, int points, float inner, float outer) {
    vec2 c = uv - 0.5;
    float r = length(c);
    float a = atan(c.y, c.x);
    float petal = abs(sin(a * float(points) * 0.5));
    float target = mix(inner, outer, petal);
    return 1.0 - smoothstep(target - 0.02, target + 0.02, r);
}

// ─────────────────────────────────────────────────────────────────────────
// Per-element glyphs. Each returns alpha 0..1 for the element's symbol.
// All centered on UV (0.5, 0.5). Designed to be unmistakable at a glance.
//
// The SHAPES are untouched from the original — legibility is gameplay state
// (redesign §2) and this pass is not allowed to cost any of it. What changed
// is the coordinate they are evaluated on, which is warped before it gets
// here, and the clock, which is now shared.
// ─────────────────────────────────────────────────────────────────────────

// FIRE — three-pointed flame (jagged, points up)
float glyph_fire(vec2 uv) {
    vec2 c = uv - 0.5;
    // Tall narrow body
    float body = 1.0 - smoothstep(0.05 + abs(c.x) * 1.5, 0.06 + abs(c.x) * 1.5,
                                   abs(c.y - sin(c.x * 8.0) * 0.04));
    // Pointed top
    float tip = step(c.y, 0.0) * (1.0 - smoothstep(0.0, 0.18, abs(c.x) + c.y * 0.8));
    // Two side flames
    vec2 l = c - vec2(-0.18, 0.05);
    vec2 r = c - vec2( 0.18, 0.05);
    float side_l = 1.0 - smoothstep(0.04, 0.08, length(l));
    float side_r = 1.0 - smoothstep(0.04, 0.08, length(r));
    return clamp(tip + side_l * 0.6 + side_r * 0.6, 0.0, 1.0);
}

// FROST — six-pointed snowflake
float glyph_frost(vec2 uv) {
    float total = 0.0;
    for (int i = 0; i < 6; i++) {
        float angle = float(i) * PI / 3.0;
        vec2 c = uv - 0.5;
        // Rotate point so we test along x-axis
        float s = sin(-angle);
        float co = cos(-angle);
        vec2 r = vec2(c.x * co - c.y * s, c.x * s + c.y * co);
        // Main spoke
        float spoke = (1.0 - smoothstep(0.012, 0.022, abs(r.y))) * step(0.0, r.x) * step(r.x, 0.4);
        // Two small branches at mid-spoke
        float branch_y = abs(abs(r.y) - (r.x - 0.18) * 0.5) - 0.012;
        float branch = (1.0 - smoothstep(0.0, 0.01, branch_y))
                     * step(0.18, r.x) * step(r.x, 0.30);
        total = max(total, spoke + branch);
    }
    return clamp(total, 0.0, 1.0);
}

// LIGHTNING — zigzag bolt
float glyph_lightning(vec2 uv) {
    vec2 c = uv - 0.5;
    // Zigzag path: y position drives a piecewise-linear x
    float y = c.y;
    float zig;
    if (y > 0.15)       zig =  0.10 - (y - 0.15) * 0.4;
    else if (y > 0.0)   zig = -0.05 + (y - 0.0)  * 1.0;
    else if (y > -0.15) zig =  0.05 + (y + 0.0)  * 0.6;
    else                zig = -0.04 - (y + 0.15) * 0.5;
    float thickness = 0.025;
    float bolt = 1.0 - smoothstep(thickness, thickness + 0.012, abs(c.x - zig));
    bolt *= step(abs(y), 0.32);
    return bolt;
}

// EARTH — diamond / mountain triangle
float glyph_earth(vec2 uv) {
    vec2 c = uv - 0.5;
    // Diamond outline
    float d = abs(c.x) + abs(c.y);
    float outline = (1.0 - smoothstep(0.018, 0.030, abs(d - 0.28)));
    // Inner small dot
    float dot = 1.0 - smoothstep(0.04, 0.06, length(c));
    return clamp(outline + dot, 0.0, 1.0);
}

// WATER — horizontal wavy lines. `flow` replaces the raw TIME this used to
// read, so the waves travel on the same gust as everything else.
float glyph_water(vec2 uv, float flow) {
    vec2 c = uv - 0.5;
    float t = flow * 0.8;
    float wave1 = abs(c.y - 0.10 - sin(c.x * 14.0 + t * 2.0) * 0.025);
    float wave2 = abs(c.y + 0.10 - sin(c.x * 14.0 + t * 2.0 + 2.0) * 0.025);
    float l1 = (1.0 - smoothstep(0.012, 0.022, wave1)) * step(abs(c.x), 0.28);
    float l2 = (1.0 - smoothstep(0.012, 0.022, wave2)) * step(abs(c.x), 0.28);
    return clamp(l1 + l2, 0.0, 1.0);
}

// AIR — three concentric arcs (swirl)
float glyph_air(vec2 uv) {
    vec2 c = uv - 0.5;
    float r = length(c);
    float a = atan(c.y, c.x);
    // Three arcs at different radii, each only on one side
    float arc1 = ring(uv, 0.32, 0.022) * smoothstep(-0.5, 0.0, sin(a + 0.5));
    float arc2 = ring(uv, 0.22, 0.020) * smoothstep(-0.5, 0.0, sin(a + 2.5));
    float arc3 = ring(uv, 0.12, 0.018) * smoothstep(-0.5, 0.0, sin(a + 4.5));
    return clamp(arc1 + arc2 + arc3, 0.0, 1.0);
}

// ARCANE — six-pointed star inside a ring
float glyph_arcane(vec2 uv) {
    float ring_a = ring(uv, 0.34, 0.018);
    // Two overlapping triangles forming a hexagram
    float st = star(uv, 6, 0.10, 0.28);
    return clamp(ring_a + st * 0.7, 0.0, 1.0);
}

// SHADOW — eye-like vertical slit with surrounding wisps
float glyph_shadow(vec2 uv) {
    vec2 c = uv - 0.5;
    // Vertical lens
    float lens_y = abs(c.y) - (0.18 - abs(c.x) * 0.6);
    float lens_x = abs(c.x) - 0.10;
    float eye = (1.0 - smoothstep(0.0, 0.018, max(lens_x, lens_y) + 0.005));
    // Pupil
    float pupil = 1.0 - smoothstep(0.025, 0.045, length(c));
    return clamp(eye + pupil, 0.0, 1.0);
}

float select_glyph(vec2 uv, float flow) {
    if (element_id == 0) return glyph_fire(uv);
    if (element_id == 1) return glyph_frost(uv);
    if (element_id == 2) return glyph_lightning(uv);
    if (element_id == 3) return glyph_earth(uv);
    if (element_id == 4) return glyph_water(uv, flow);
    if (element_id == 5) return glyph_air(uv);
    if (element_id == 6) return glyph_arcane(uv);
    return glyph_shadow(uv);
}

// ── Per-element response table ───────────────────────────────────────────
//
// Same rule as the aura's: the ladder selects PARAMETERS, never code, and
// every row draws from the SAME phase source. Do not add a second clock.
//
//   x = spin     turn rate (x turn_rate). 0 = the rune does not rotate.
//   y = wobble   ink-wobble multiplier — how unsteady the hand is
//   z = pulse    breathing depth multiplier
//   w = rate     motion speed (x flow_speed)
vec4 element_ink(int id) {
    if (id == 0) return vec4(0.00, 1.40, 1.30, 1.60);  // FIRE      guttering, no turn
    if (id == 1) return vec4(0.00, 0.30, 0.45, 0.30);  // FROST     LOCKED. ice does not turn.
    if (id == 2) return vec4(0.00, 0.80, 1.70, 1.40);  // LIGHTNING snaps, never drifts
    if (id == 3) return vec4(0.00, 0.45, 0.35, 0.22);  // EARTH     inert
    if (id == 4) return vec4(0.00, 1.20, 0.80, 0.70);  // WATER     undulates, does not spin
    if (id == 5) return vec4(2.40, 1.30, 1.00, 1.30);  // AIR       spins hardest
    if (id == 6) return vec4(0.80, 0.60, 1.10, 0.90);  // ARCANE    slow deliberate drift
    return       vec4(0.15, 1.50, 0.70, 0.40);         // SHADOW    crawls, very unsteady
}

void vertex() {
    // Sample the gust at the TILE, not per vertex — the rune is one object in
    // one gust. Constant across the quad, so the pulse fires as a whole.
    vec3 tile_ws = MODEL_MATRIX[3].xyz;

    vec2 ndir = normalize(wind_dir);
    vec2 perp = vec2(-ndir.y, ndir.x);

    float along  = dot(tile_ws.xz, ndir);
    float across = dot(tile_ws.xz, perp);

    vec2 wave_uv = vec2(along * wave_scale + TIME * wave_speed,
                        across * wave_scale * wave_stretch);
    float wave = texture(wind_noise, wave_uv).r * 2.0 - 1.0;

    vec2 detail_uv = vec2(along, across) * detail_scale + ndir * TIME * detail_speed;
    float detail = texture(wind_noise, detail_uv).r * 2.0 - 1.0;

    v_flow = wave * wave_strength + detail * detail_strength;
    v_gust = v_flow + wind_amplitude;
}

void fragment() {
    vec4 k = element_ink(element_id);

    // The shared clock, scaled by this element's rate. Same construction as
    // the aura's, and the same rule: one phase source, eight tempos.
    float flow = TIME * flow_speed * k.w + v_flow * 0.9;

    vec2 uv = UV;

    // Drift. Most runes now do NOT turn at all — a rotating snowflake is the
    // single loudest "this is a shader effect" tell in the old build, and ice
    // that spins is ice that is not frozen. Only air and arcane rotate; shadow
    // barely crawls.
    float turn = flow * turn_rate * k.x;
    if (k.x > 0.0) uv = rotate_uv(uv, turn);

    if (element_id == 0) {
        // Fire flickers rather than turns — on the gust, so it gutters when
        // the field gusts.
        float wob = sin(flow * 3.0) * 0.010 + v_flow * 0.008;
        uv += vec2(wob, sin(flow * 2.2) * 0.005);
    }

    // THE HAND. Warping the coordinate a distance field is thresholded on
    // both bends the stroke and varies its width. The warp itself creeps
    // with `flow`, so the ink looks re-drawn rather than vibrating.
    vec2 wob2 = vec2(vnoise(uv * ink_wobble_scale + vec2(flow * 0.15, 0.0)),
                     vnoise(uv * ink_wobble_scale + vec2(4.7, 9.1) - vec2(0.0, flow * 0.15)));
    uv += (wob2 - 0.5) * ink_wobble * k.y;

    float glyph_alpha;
    if (use_rune_tex) {
        // The baked rune already carries the hand — seeded jitter, round caps,
        // stroke-weight variation. The wobble above still applies to the sampling
        // coordinate, which reads as the ink settling rather than as the rune
        // vibrating, and is why k.y is small for the rigid elements.
        vec2 ruv = (uv - 0.5) / max(rune_scale, 0.05) + 0.5;
        glyph_alpha = texture(rune_tex, ruv).a;
        // Outside the baked square there is nothing to draw. Without this the
        // linear sampler smears the outermost texel ring outward into a haze.
        glyph_alpha *= step(0.0, ruv.x) * step(ruv.x, 1.0)
                     * step(0.0, ruv.y) * step(ruv.y, 1.0);
    } else {
        glyph_alpha = select_glyph(uv, flow);
    }

    // Breathe on the gust rather than on a private sine, so the rune brightens
    // as the field surges. `v_gust` is roughly [-1.1, 1.3]; remap to [0,1].
    float gust01 = clamp(v_gust * 0.5 + 0.5, 0.0, 1.0);
    float breath = sin(flow * pulse_speed) * 0.5 + 0.5;
    float depth  = clamp(pulse_amount * k.z, 0.0, 1.0);
    float pulse  = 1.0 - depth + depth * mix(breath, gust01, 0.6);

    float final_alpha = glyph_alpha * intensity * pulse;

    ALBEDO = tint_color.rgb;
    ALPHA  = clamp(final_alpha * tint_color.a, 0.0, alpha_ceiling);
}
