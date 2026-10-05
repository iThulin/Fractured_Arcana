shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

// ─────────────────────────────────────────────────────────────────────────
//  glyph_aura.gdshader  (2026-10-04)
//
//  A faint column of light rising off a prepared glyph: the spell is bound
//  to the ground and leaks. Drawn on an open cylinder standing on the tile
//  (HexTile builds it beside the sigil decal). Brightest at the ground and
//  gone by the top, with slow vertical streaks drifting up the wall and a
//  few motes rising through it. Kept dim on purpose: a unit standing in it
//  must still read clearly.
// ─────────────────────────────────────────────────────────────────────────

/** Body of the light. Set per glyph from its palette. */
uniform vec4 aura_color : source_color = vec4(0.40, 0.62, 1.00, 1.0);
/** Rising motes. */
uniform vec4 mote_color : source_color = vec4(0.86, 0.96, 1.00, 1.0);
uniform float strength : hint_range(0.0, 2.0) = 0.42;
/** World height of the cylinder, so the fade is measured in the mesh's own units. */
uniform float aura_height = 1.1;
/** Per-tile phase, radians. */
uniform float phase = 0.0;
/** 0..1, tweened in after the inscription so the column rises once the spell is bound. */
uniform float progress : hint_range(0.0, 1.0) = 1.0;
uniform float mote_density : hint_range(0.0, 1.0) = 0.35;

varying float v_h;

float hash11(float n) {
    return fract(sin(n * 127.1 + 11.7) * 43758.5453);
}

void vertex() {
    v_h = clamp(VERTEX.y / max(aura_height, 0.001) + 0.5, 0.0, 1.0);
}

void fragment() {
    float a = UV.x * TAU;
    float t = TIME;
    float h = v_h;

    // The column rises with progress: nothing above the current top.
    float top = progress;
    float cut = 1.0 - smoothstep(top - 0.15, top, h);

    float fall = pow(1.0 - h, 2.4);

    // Slow streaks drifting up the wall.
    float s = 0.5 + 0.5 * sin(a * 7.0 + sin(a * 3.0 + t * 0.6 + phase) * 1.6 + phase);
    s = pow(s, 5.0);
    float scroll = 0.5 + 0.5 * sin(h * 8.0 - t * 1.8 + a * 2.0 + phase);
    float body = fall * (0.22 + 0.78 * s * scroll);
    float breathe = 0.85 + 0.15 * sin(t * 0.9 + phase);

    // Motes: one candidate per column cell, each rising at its own speed.
    float cells = 22.0;
    float ci = floor(UV.x * cells);
    float hs = hash11(ci + floor(phase * 13.0));
    float mote = 0.0;
    if (hs < mote_density) {
        float speed = 0.18 + 0.30 * hash11(ci * 3.1 + 1.0);
        float y = fract(t * speed + hs * 7.0);
        float dx = (fract(UV.x * cells) - 0.5) * 0.9;
        float dy = (h - y) * 7.0;
        float d = length(vec2(dx, dy));
        mote = exp(-d / 0.06) * (1.0 - y) * smoothstep(0.0, 0.1, y);
    }

    vec3 col = aura_color.rgb * body * breathe + mote_color.rgb * mote * 1.4;
    ALBEDO = col * strength * cut * progress;
}
