shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

// Element selector — driven from C#:
//   0 = Fire, 1 = Frost, 2 = Lightning, 3 = Earth,
//   4 = Water, 5 = Air, 6 = Arcane, 7 = Shadow
uniform int element_id : hint_range(0, 7) = 0;

uniform vec4 tint_color : source_color = vec4(1.0, 0.4, 0.1, 1.0);
uniform float intensity : hint_range(0.0, 4.0) = 2.0;
uniform float pulse_speed : hint_range(0.0, 4.0) = 1.5;
uniform float pulse_amount : hint_range(0.0, 1.0) = 0.25;

// ─────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
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

// WATER — horizontal wavy lines
float glyph_water(vec2 uv) {
    vec2 c = uv - 0.5;
    float wave1 = abs(c.y - 0.10 - sin(c.x * 14.0 + TIME * 1.5) * 0.025);
    float wave2 = abs(c.y + 0.10 - sin(c.x * 14.0 + TIME * 1.5 + 2.0) * 0.025);
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

float select_glyph(vec2 uv) {
    if (element_id == 0) return glyph_fire(uv);
    if (element_id == 1) return glyph_frost(uv);
    if (element_id == 2) return glyph_lightning(uv);
    if (element_id == 3) return glyph_earth(uv);
    if (element_id == 4) return glyph_water(uv);
    if (element_id == 5) return glyph_air(uv);
    if (element_id == 6) return glyph_arcane(uv);
    return glyph_shadow(uv);
}

void fragment() {
    // Slow rotation, slightly different per element to keep it visually varied.
    // Lightning doesn't rotate (it'd look weird); fire only flickers.
    vec2 uv = UV;
    if (element_id == 1) uv = rotate_uv(uv, TIME * 0.3);   // Frost rotates clockwise-ish
    if (element_id == 5) uv = rotate_uv(uv, TIME * 0.6);   // Air swirls faster
    if (element_id == 6) uv = rotate_uv(uv, TIME * 0.2);   // Arcane drifts
    // Fire: small jitter scaled with TIME instead of full rotation
    if (element_id == 0) {
        float wob = sin(TIME * 8.0) * 0.01;
        uv += vec2(wob, sin(TIME * 6.0) * 0.005);
    }

    float glyph_alpha = select_glyph(uv);

    // Slow brightness pulse so even static glyphs feel alive
    float pulse = 1.0 - pulse_amount + pulse_amount * (sin(TIME * pulse_speed) * 0.5 + 0.5);

    float final_alpha = glyph_alpha * intensity * pulse;

    ALBEDO = tint_color.rgb;
    ALPHA  = clamp(final_alpha * tint_color.a, 0.0, 1.0);
}
