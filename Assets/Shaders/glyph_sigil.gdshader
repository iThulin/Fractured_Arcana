shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

// ─────────────────────────────────────────────────────────────────────────
//  glyph_sigil.gdshader
//
//  The Enchanter glyph cipher's tile decal: a generated sigil lying flat on
//  the ground, waiting for a trigger.
//
//  The sigil arrives as a baked texture from GlyphCipherTexture — the same
//  stroke data the card art draws, rendered once and cached. This shader
//  supplies everything a static texture cannot: the enclosing ring, the
//  backing, the halo, the breathing, and the sequential inscription.
//
//  LAYOUT. The baked sigil's own rim sits exactly at the texture's inscribed
//  circle, so there is no room inside the image for anything outside it. The
//  texture is therefore mapped to the inner `sigil_scale` of the quad and the
//  annulus left over belongs to the enclosing ring. That is why the ring is
//  procedural rather than baked.
//
//  Sibling of imbuement_glyph.gdshader and deliberately shaped like it, but
//  blend_mix rather than blend_add: this sigil carries a dark backing so it
//  stays legible over grass, sand, stone or water, and additive blending would
//  erase exactly that. The glow is reconstructed here instead.
// ─────────────────────────────────────────────────────────────────────────

/** Baked sigil. Alpha carries the strokes; RGB carries ink and rose. */
uniform sampler2D sigil_tex : source_color, filter_linear_mipmap;

/** Fraction of the quad the baked sigil occupies. The remainder is the ring's. */
uniform float sigil_scale : hint_range(0.4, 1.0) = 0.80;

// ── Distance robustness ──────────────────────────────────────────────────
// The ring and the hub hold up as the camera pulls back — the ring because it
// is drawn procedurally here (sharp at any scale), the hub because it is a
// large solid area. The stave does not: it is thin strokes in a minified
// texture, and the mip chain averages them toward transparent long before
// anything else fades. These two uniforms fight that, and they are the whole
// reason the sigil stays readable at range.

/** Mip LOD bias. Negative keeps a sharper mip level than the hardware would pick,
    which stops thin strokes dissolving. Too negative and they alias/shimmer. */
uniform float mip_bias : hint_range(-2.0, 1.0) = -0.6;

/** Exponent on the sigil's alpha. Below 1.0 this pushes partially-covered texels
    back toward opaque, which is exactly what minification produces — so it
    effectively re-fattens thin lines at distance instead of letting them wash out. */
uniform float alpha_gain : hint_range(0.25, 1.0) = 0.55;

// ── Enclosing ring ───────────────────────────────────────────────────────
/** Ring tint. Drive from UITheme.CipherFunction so it matches the school. */
uniform vec4 ring_color : source_color = vec4(0.769, 0.357, 0.620, 1.0);
uniform float ring_radius : hint_range(0.0, 0.5) = 0.455;
uniform float ring_width : hint_range(0.0, 0.06) = 0.009;
uniform float ring_softness : hint_range(0.0, 0.06) = 0.010;
uniform float ring_strength : hint_range(0.0, 3.0) = 1.0;

// ── Backing ──────────────────────────────────────────────────────────────
/** Darkens the ground under the sigil so it reads over any terrain. */
uniform vec4 backing_color : source_color = vec4(0.055, 0.043, 0.051, 1.0);
uniform float backing_alpha : hint_range(0.0, 1.0) = 0.62;
uniform float backing_radius : hint_range(0.0, 0.5) = 0.470;
uniform float backing_softness : hint_range(0.0, 0.3) = 0.090;

// ── Glow ─────────────────────────────────────────────────────────────────
uniform vec4 glow_color : source_color = vec4(0.769, 0.357, 0.620, 1.0);
uniform float glow_strength : hint_range(0.0, 3.0) = 1.15;
uniform float glow_radius : hint_range(0.0, 0.08) = 0.022;

// ── Life ─────────────────────────────────────────────────────────────────
uniform float pulse_speed : hint_range(0.0, 4.0) = 0.55;
uniform float pulse_amount : hint_range(0.0, 1.0) = 0.35;
/** Per-tile phase offset, radians. Keeps neighbouring glyphs off each other's beat. */
uniform float phase = 0.0;
/** Slow rotation, radians per second. 0 for a static sigil. */
uniform float spin_speed : hint_range(-1.0, 1.0) = 0.045;
uniform float alpha_scale : hint_range(0.0, 1.0) = 1.0;

// ── Inscription ──────────────────────────────────────────────────────────
/** 0..1, driven by a Tween when the glyph is prepared. */
uniform float progress : hint_range(0.0, 1.0) = 1.0;
/** Arms in the stave. Each gets its own beat of the inscription. */
uniform int sweep_sectors : hint_range(1, 12) = 6;

vec2 rot_uv(vec2 uv, float a) {
    vec2 c = uv - vec2(0.5);
    float s = sin(a);
    float co = cos(a);
    return vec2(c.x * co - c.y * s, c.x * s + c.y * co) + vec2(0.5);
}

void fragment() {
    vec2 uv = rot_uv(UV, TIME * spin_speed);
    vec2 p = uv - vec2(0.5);
    float r = length(p);

    // The decal is a disc; never let the quad's corners show.
    if (r > 0.5) {
        discard;
    }

    // Sigil space: the baked image mapped to the inner sigil_scale of the quad.
    vec2 suv = p / sigil_scale + vec2(0.5);
    float sr = length(suv - vec2(0.5));
    vec4 tex = (sr <= 0.5) ? texture(sigil_tex, suv, mip_bias) : vec4(0.0);
    tex.a = pow(tex.a, alpha_gain);

    // theta measured CLOCKWISE FROM UP, which is exactly the cipher's own
    // convention (spec v2 §2), and UV-space +Y already points down — so sector 0
    // lands on arm 0 and the inscription runs in reading order for free.
    float ang = atan(p.x, -p.y);
    if (ang < 0.0) {
        ang += TAU;
    }

    float pulse = 1.0 + pulse_amount * sin(TIME * pulse_speed * TAU + phase);

    // ── Sequential inscription ───────────────────────────────────────────
    // One beat per arm. Within its beat an arm is drawn OUTWARD from the hub,
    // so each stave is struck from the centre to its tip rather than the whole
    // sigil fading up together.
    float sectors = float(sweep_sectors);
    float sector_w = TAU / sectors;
    float a2 = ang + sector_w * 0.5;          // centre sector 0 on arm 0
    if (a2 >= TAU) {
        a2 -= TAU;
    }
    float sector = floor(a2 / sector_w);

    float t = progress * sectors;
    float done = floor(t);
    float local = t - done;

    float reveal;
    if (sector < done) {
        reveal = 1.0;
    } else if (sector > done) {
        reveal = 0.0;
    } else {
        reveal = 1.0 - smoothstep(local * 0.52, local * 0.52 + 0.05, sr);
    }

    // The hub sits at the centre, where the angle is meaningless. Land it last so
    // the sigil visibly seals instead of popping in with the first arm.
    float hub = smoothstep(0.90, 1.0, progress);
    float centre_mask = 1.0 - smoothstep(0.06, 0.11, sr);
    reveal = mix(reveal, hub, centre_mask);

    // ── Halo ─────────────────────────────────────────────────────────────
    // Eight-tap ring blur of the sigil's own alpha, squared to pull the bloom
    // back against the strokes instead of fogging the whole disc.
    float halo = 0.0;
    for (int i = 0; i < 8; i++) {
        float a = TAU * float(i) / 8.0;
        vec2 o = suv + vec2(cos(a), sin(a)) * glow_radius;
        halo += (length(o - vec2(0.5)) <= 0.5) ? texture(sigil_tex, o).a : 0.0;
    }
    halo = clamp(halo * 0.125, 0.0, 1.0);
    halo *= halo;
    float g = halo * glow_strength * pulse;

    // ── Enclosing ring ───────────────────────────────────────────────────
    // Drawn by a plain angular sweep so it closes as the last arm is struck.
    float ring = 1.0 - smoothstep(ring_width, ring_width + ring_softness, abs(r - ring_radius));
    ring *= 1.0 - smoothstep(progress * TAU, progress * TAU + 0.20, ang);

    // ── Backing ──────────────────────────────────────────────────────────
    // Fades in over the first quarter of the inscription, so the ground is not
    // darkened before there is anything drawn on it.
    float back = (1.0 - smoothstep(backing_radius - backing_softness, backing_radius, r))
                 * backing_alpha * smoothstep(0.0, 0.25, progress);

    vec3 col = backing_color.rgb;
    float a = back;

    col = mix(col, tex.rgb, tex.a * reveal);
    a = max(a, tex.a * reveal);

    col += ring_color.rgb * ring * ring_strength * pulse;
    a = clamp(a + ring * ring_color.a, 0.0, 1.0);

    col += glow_color.rgb * g * reveal;
    a = clamp(a + g * glow_color.a * 0.5 * reveal, 0.0, 1.0);

    ALBEDO = col;
    ALPHA = a * alpha_scale;
}
