shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled, shadows_disabled;

// ─────────────────────────────────────────────────────────────────────────
//  glyph_sigil.gdshader  (v2, "light circle", 2026-10-04)
//
//  The Enchanter glyph on a hex tile, drawn as LIGHT rather than ink: a
//  spell circle burned onto the ground in the manner of an arcane summoning
//  ring. Thin hot cores, a coloured bloom around every line, a ring of
//  runes, a slowly turning hexagram, satellite circles and a scatter of
//  twinkles, with the generated cipher sigil at the centre carrying the
//  gameplay information (hub = recipient, spokes = verbs).
//
//  WHY IT CHANGED. v1 baked the cipher as INK (colour + alpha) and alpha-
//  blended it over a dark backing. Antialiased polylines, round-cap discs
//  and crossing strokes all double up their alpha where they overlap, and a
//  transparent viewport premultiplies the ink toward black at every edge,
//  so crossings and joints came out as blotches with dark fringes.
//
//  v2 bakes a COVERAGE MASK instead (GlyphCipherView.LightMask): drawn with
//  additive canvas blending into separate channels, so overlaps saturate to
//  a clean union rather than stacking.
//      R = identity stave (arms, crossbars, ornaments)
//      G = function layer (spokes, spoke tips, markers, hub OUTLINE)
//      B = hub fill
//  The enclosing rim is NOT in the mask; it is drawn procedurally here,
//  where it is sharp at any distance.
//
//  BLENDING. blend_premul_alpha does both jobs in one pass:
//      result = ALBEDO + ground * (1 - ALPHA)
//  ALPHA is a soft darkening disc (so the light reads over bright grass and
//  sand), ALBEDO is pure emitted light (so crossings brighten, exactly as
//  two beams of light do, instead of blotching).
//
//  The mask is NOT source_color: it is coverage, not colour, and an sRGB
//  decode would crush every antialiased edge.
// ─────────────────────────────────────────────────────────────────────────

/** Baked cipher coverage mask. R = stave, G = function, B = hub fill. */
uniform sampler2D mask_tex : filter_linear_mipmap, hint_default_black;

/** Fraction of the quad the cipher occupies. The rest belongs to the rings. */
uniform float sigil_scale : hint_range(0.4, 1.0) = 0.80;

group_uniforms colour;
/** The circle's own light: rings, runes, hexagram, stave. */
uniform vec4 light_color : source_color = vec4(0.60, 0.40, 1.00, 1.0);
/** The function layer (spokes and hub). Drive from UITheme.CipherFunction. */
uniform vec4 accent_color : source_color = vec4(0.769, 0.357, 0.620, 1.0);
/** Cool counter-tone: outer ring, rune band, sigil rim, satellites, twinkles. */
uniform vec4 cool_color : source_color = vec4(0.55, 0.82, 1.00, 1.0);
/** Master for the cool tone. 0 puts every element back on light_color. */
uniform float cool_amount : hint_range(0.0, 1.0) = 1.0;
/** 0 = a glyph for you and your allies to stand on (round rings, a rosette, circular
    satellites). 1 = a glyph aimed at enemies (a pointed star rim, a faceted band, a
    hexagram, diamond satellites). Set per glyph by HexTile, with the matching palette. */
uniform float hostile : hint_range(0.0, 1.0) = 0.0;
/** What line cores burn toward at full heat. Icy rather than pink. */
uniform vec4 core_color : source_color = vec4(0.88, 0.95, 1.00, 1.0);
uniform float intensity : hint_range(0.0, 3.0) = 1.15;
/** How far line cores whiten. 0 keeps every line its pure colour. */
uniform float core_heat : hint_range(0.0, 1.5) = 0.42;
uniform float glow_strength : hint_range(0.0, 3.0) = 1.3;
/** Soft light cast on the ground under the circle. */
uniform float pool_strength : hint_range(0.0, 1.0) = 0.07;

group_uniforms ground;
/** Darkening under the circle, so it reads on bright terrain. 0 for pure light. */
uniform float backing_alpha : hint_range(0.0, 1.0) = 0.38;
uniform float backing_radius : hint_range(0.0, 0.5) = 0.49;

group_uniforms ornament;
uniform float ring_strength : hint_range(0.0, 3.0) = 1.0;
uniform float rune_strength : hint_range(0.0, 3.0) = 0.85;
uniform float hexagram_strength : hint_range(0.0, 3.0) = 0.38;
uniform float sparkle_strength : hint_range(0.0, 3.0) = 0.9;
/** Fraction of sparkle cells that hold a star. */
uniform float sparkle_density : hint_range(0.0, 1.0) = 0.22;
/** Hexagram or rosette, and the satellites, radians per second. */
uniform float ornament_spin : hint_range(-1.0, 1.0) = 0.14;
/** Rune band, radians per second. Opposite sign to the hexagram reads as machinery. */
uniform float rune_spin : hint_range(-1.0, 1.0) = -0.10;
/** The cipher's slow turn, radians per second. The tooltip carries its meaning, so it may move. */
uniform float cipher_spin : hint_range(-1.0, 1.0) = 0.035;
/** The cipher's gentle rocking on top of its turn, radians. */
uniform float cipher_sway : hint_range(0.0, 0.5) = 0.07;
/** Outer ring or star, and its ticks, radians per second. */
uniform float ring_spin : hint_range(-1.0, 1.0) = 0.05;
/** Band ring or star, radians per second. */
uniform float band_spin : hint_range(-1.0, 1.0) = -0.08;
/** How far the rings swell and settle as the circle breathes (UV units). */
uniform float breath_amount : hint_range(0.0, 0.02) = 0.005;

group_uniforms energy;
/** The colour that runs through the circle: comets on the rings, the cipher pulse,
    rune flickers. Set per glyph from its palette. */
uniform vec4 pop_color : source_color = vec4(0.78, 0.60, 1.00, 1.0);
uniform float pop_strength : hint_range(0.0, 3.0) = 1.0;
/** Laps per second of the ring comets. */
uniform float current_speed : hint_range(0.0, 1.0) = 0.12;
/** Seconds between pulses rolling out through the cipher. */
uniform float wave_period : hint_range(0.5, 10.0) = 3.4;
/** How far the stave's colour drifts toward pop_color and back. */
uniform float hue_drift : hint_range(0.0, 1.0) = 0.18;

group_uniforms distance;
/** Mip LOD bias on the mask. Negative keeps thin strokes from dissolving at range. */
uniform float mip_bias : hint_range(-2.0, 1.0) = -0.5;
/** Exponent on mask coverage. Below 1 re-fattens strokes the mip chain thinned. */
uniform float mask_gain : hint_range(0.25, 1.0) = 0.6;

group_uniforms life;
uniform float pulse_speed : hint_range(0.0, 4.0) = 0.45;
uniform float pulse_amount : hint_range(0.0, 1.0) = 0.18;
/** Per-tile phase, radians. Keeps neighbouring glyphs off each other's beat. */
uniform float phase = 0.0;
uniform float alpha_scale : hint_range(0.0, 1.0) = 1.0;

group_uniforms inscription;
/** 0..1, tweened when the glyph is prepared. Rings, runes, hexagram, then the cipher. */
uniform float progress : hint_range(0.0, 1.0) = 1.0;
/** 1 -> 0 burst tweened after the inscription completes. */
uniform float flash : hint_range(0.0, 1.0) = 0.0;
/** Arms in the stave. Each gets its own beat of the inscription. */
uniform int sweep_sectors : hint_range(1, 12) = 6;

// ── Layout (UV units, quad radius 0.5) ───────────────────────────────────
const float R_OUTER = 0.488;     // thin outer ring
const float R_RUNE = 0.466;      // centre line of the rune band
const float RUNE_H = 0.011;      // rune half height
const float R_BAND = 0.445;      // inner band ring; hexagram vertices sit on it
const float RUNE_CELLS = 40.0;

vec2 rot(vec2 p, float a) {
    float s = sin(a);
    float c = cos(a);
    return vec2(p.x * c - p.y * s, p.x * s + p.y * c);
}

float hash11(float n) {
    return fract(sin(n * 127.1 + 11.7) * 43758.5453);
}

vec4 hash42(vec2 p) {
    vec4 q = vec4(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)),
                  dot(p, vec2(419.2, 371.9)), dot(p, vec2(97.3, 151.7)));
    return fract(sin(q) * 43758.5453);
}

float sd_seg(vec2 p, vec2 a, vec2 b) {
    vec2 pa = p - a;
    vec2 ba = b - a;
    float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h);
}

// A line of half-width w at distance d, antialiased against the pixel footprint.
// When the line would be thinner than a pixel it is drawn one pixel wide at
// proportionally lower intensity, so it neither vanishes nor shimmers at range.
float line_aa(float d, float w, float pix) {
    float we = max(w, pix * 0.6);
    return (1.0 - smoothstep(we - pix, we + pix, d)) * (w / we);
}

// Angle clockwise from up (the cipher's own convention), 0..TAU.
float bearing(vec2 p) {
    float a = atan(p.x, -p.y);
    return a < 0.0 ? a + TAU : a;
}

// An angular sweep that has reached fraction t of the full turn.
float sweep(float ang, float t) {
    return 1.0 - smoothstep(t * TAU, t * TAU + 0.25, ang);
}

// Distance to the outline of a regular n-gon with inradius rin (an edge faces up).
float sd_ngon(vec2 p, float rin, float n) {
    float sector = TAU / n;
    float a = atan(p.x, -p.y);
    a = mod(a + sector * 0.5, sector) - sector * 0.5;
    return abs(length(p) * cos(a) - rin);
}

// Distance to the outline of an n-pointed star: tips at radius ro, valleys at ri.
float sd_star(vec2 p, float ro, float ri, float n) {
    float sector = TAU / n;
    float a = mod(atan(p.x, -p.y), sector);
    a = min(a, sector - a);
    vec2 q = length(p) * vec2(sin(a), cos(a));
    vec2 tip = vec2(0.0, ro);
    vec2 val = ri * vec2(sin(sector * 0.5), cos(sector * 0.5));
    return sd_seg(q, tip, val);
}

// One procedural rune, q in roughly [-1,1]^2, chosen by seed. Returns distance.
float rune(vec2 q, float seed) {
    float h = hash11(seed);
    int bits = int(h * 64.0);
    float d = 1e3;
    float sx = ((bits & 1) != 0) ? 0.0 : -0.45;
    d = min(d, sd_seg(q, vec2(sx, -0.9), vec2(sx, 0.9)));                      // stem
    if ((bits & 2) != 0)  d = min(d, sd_seg(q, vec2(-0.55, 0.9), vec2(0.55, 0.9)));
    if ((bits & 4) != 0)  d = min(d, sd_seg(q, vec2(-0.55, -0.6), vec2(0.55, 0.2)));
    if ((bits & 8) != 0)  d = min(d, sd_seg(q, vec2(sx, -0.9), vec2(0.6, -0.35)));
    if ((bits & 16) != 0) d = min(d, length(q - vec2(0.5, 0.45)) - 0.08);
    if ((bits & 32) != 0) {
        d = min(d, sd_seg(q, vec2(-0.6, 0.1), vec2(0.0, 0.6)));
        d = min(d, sd_seg(q, vec2(0.0, 0.6), vec2(0.6, 0.1)));
    }
    return d;
}

void fragment() {
    // Derivatives first, while every fragment of the quad is still alive.
    vec2 fw2 = fwidth(UV);
    float pix = max(fw2.x, fw2.y);

    vec2 p = UV - vec2(0.5);
    float r = length(p);
    if (r > 0.5) {
        discard;
    }
    // Fine detail (runes, sparkles, ticks) fades out before it can alias.
    float detail = 1.0 - smoothstep(0.0022, 0.0060, pix);

    float ang = bearing(p);
    float pulse = 1.0 + pulse_amount * sin(TIME * pulse_speed * TAU + phase);

    // Motion: each layer turns on its own clock, and the rings breathe out of step.
    float tt = TIME;
    vec2 po = rot(p, tt * ring_spin);
    vec2 pb = rot(p, tt * band_spin);
    float breath = sin(tt * 0.8 + phase);
    float breath2 = sin(tt * 0.63 + phase * 1.7 + 1.3);
    vec3 pop = pop_color.rgb * pop_strength;
    float P = progress;

    // Inscription schedule, drawn piece by piece: rings sweep round, the runes follow,
    // the inner pattern settles, then the cipher's arms are struck one at a time from
    // the hub out, the hub seals, and the twinkles arrive last.
    float p_ground = smoothstep(0.0, 0.12, P);
    float p_rings = clamp(P / 0.24, 0.0, 1.0);
    float p_runes = clamp((P - 0.12) / 0.24, 0.0, 1.0);
    float p_hex = smoothstep(0.26, 0.40, P);
    float p_cipher = clamp((P - 0.38) / 0.56, 0.0, 1.0);
    float p_spark = smoothstep(0.90, 1.0, P);

    vec3 light = vec3(0.0);
    float core = 0.0;       // accumulated line-core coverage, for whitening

    // ── Rings ────────────────────────────────────────────────────────────
    float r_sig = sigil_scale * 0.5;
    // Two-tone palette: violet for the band ring, hexagram and stave; light blue for
    // the outer ring, rune band, sigil rim, satellites and twinkles.
    vec3 cool = mix(light_color.rgb, cool_color.rgb, cool_amount);
    vec3 mid = mix(light_color.rgb, cool_color.rgb, 0.5 * cool_amount);

    // Friendly glyphs are round; hostile ones trade the outer ring for a 12-point star
    // and the band ring for a shallow six-point star. The sigil rim stays a circle in both.
    float h = clamp(hostile, 0.0, 1.0);
    float d_out = mix(abs(r - (R_OUTER + breath_amount * breath)), sd_star(po, 0.499, 0.452, 12.0), h);
    float d_band = mix(abs(r - (R_BAND - breath_amount * breath2)), sd_star(pb, 0.447, 0.412, 6.0), h);
    float ring_out = line_aa(d_out, 0.0020, pix) * 0.80;
    float ring_band = line_aa(d_band, 0.0026, pix);
    float ring_sig = line_aa(abs(r - r_sig), 0.0030, pix) * 0.95;
    float rsw = sweep(ang, p_rings);
    float rings = (ring_out + ring_band + ring_sig) * rsw;

    // Fine bearing ticks just outside the cipher: 72 short, every 6th long.
    float tang = bearing(po);
    float tick_i = floor(tang / (TAU / 72.0) + 0.5);
    float tick_a = tick_i * (TAU / 72.0);
    float tick_d = abs(sin(tang - tick_a)) * r;
    float tick_len = (mod(tick_i, 6.0) < 0.5) ? 0.020 : 0.010;
    float tick_band = 1.0 - smoothstep(0.0, pix * 1.5, max(r_sig + 0.006 - r, r - (r_sig + 0.006 + tick_len)));
    float ticks = line_aa(tick_d, 0.0013, pix) * tick_band * 0.45 * detail * sweep(ang, p_rings);

    light += (cool * (ring_out + ring_sig) * rsw + light_color.rgb * ring_band * rsw
              + cool * ticks) * ring_strength;
    core += rings * 0.7;

    vec3 ring_glow = light_color.rgb * exp(-d_band / 0.022) * 0.24
                   + cool * exp(-abs(r - r_sig) / 0.018) * 0.20
                   + cool * exp(-d_out / 0.014) * 0.12;
    light += ring_glow * glow_strength * pulse * p_rings;

    // Energy currents: a comet laps the outer ring one way and the band the other.
    float headA = fract(tt * current_speed + phase * 0.159) * TAU;
    float headB = TAU - fract(tt * current_speed * 0.8 + phase * 0.37) * TAU;
    float dA = abs(mod(ang - headA + TAU * 0.5, TAU) - TAU * 0.5);
    float dB = abs(mod(ang - headB + TAU * 0.5, TAU) - TAU * 0.5);
    float cometA = exp(-dA * 2.5);
    float cometB = exp(-dB * 2.5);
    light += pop * (ring_out * cometA + ring_band * cometB) * 1.6 * rsw;
    light += pop * (exp(-d_out / 0.02) * exp(-dA * 6.0) + exp(-d_band / 0.02) * exp(-dB * 6.0)) * 0.45 * p_rings;

    // ── Rune band ────────────────────────────────────────────────────────
    float band = 1.0 - smoothstep(RUNE_H + 0.004, RUNE_H + 0.004 + pix, abs(r - R_RUNE));
    if (band > 0.0) {
        float ra = ang + TIME * rune_spin;
        ra = mod(ra, TAU);
        float cw = TAU / RUNE_CELLS;
        float cell = floor(ra / cw);
        float local = (fract(ra / cw) - 0.5) * cw * R_RUNE;      // arc length, UV units
        vec2 q = vec2(local, r - R_RUNE) / RUNE_H;
        float rd;
        if (mod(cell, 5.0) < 0.5) {
            rd = length(q) - 0.18;                                // separator dot
        } else {
            rd = rune(q, cell + floor(phase * 10.0));
        }
        float runes = line_aa(rd * RUNE_H, 0.0016, pix);
        // Now and then a rune lights up in the energy colour.
        float flick = pow(max(0.0, sin(tt * 1.3 + hash11(cell * 1.7) * TAU)), 14.0);
        light += pop * runes * band * flick * 1.2 * detail * sweep(ang, p_runes);
        // At range the runes become a soft glowing band rather than noise.
        float haze = 0.22;
        float rl = mix(haze, runes, detail) * band;
        rl *= sweep(ang, p_runes) * rune_strength;
        light += cool * rl;
        core += runes * band * 0.5 * detail * sweep(ang, p_runes);
    }

    // ── Hexagram and satellites ──────────────────────────────────────────
    {
        vec2 hp = rot(p, TIME * ornament_spin);
        vec2 v[6];
        for (int k = 0; k < 6; k++) {
            float a = float(k) * TAU / 6.0;
            v[k] = vec2(sin(a), -cos(a)) * R_BAND;
        }
        // Hostile: the hexagram. Friendly: a rosette of six circles, all curves.
        float hd_hex = 1e3;
        hd_hex = min(hd_hex, sd_seg(hp, v[0], v[2]));
        hd_hex = min(hd_hex, sd_seg(hp, v[2], v[4]));
        hd_hex = min(hd_hex, sd_seg(hp, v[4], v[0]));
        hd_hex = min(hd_hex, sd_seg(hp, v[1], v[3]));
        hd_hex = min(hd_hex, sd_seg(hp, v[3], v[5]));
        hd_hex = min(hd_hex, sd_seg(hp, v[5], v[1]));
        float hd_ros = 1e3;
        for (int k = 0; k < 6; k++) {
            float a = (float(k) + 0.5) * TAU / 6.0;
            vec2 c = vec2(sin(a), -cos(a)) * 0.330;
            hd_ros = min(hd_ros, abs(length(hp - c) - 0.112));
        }
        float hd = mix(hd_ros, hd_hex, h);
        float hex = line_aa(hd, 0.0018, pix);

        // Satellites: circles for friendly glyphs, radial diamonds for hostile ones.
        float sat = 0.0;
        for (int k = 0; k < 6; k++) {
            vec2 q = hp - v[k];
            vec2 radial = normalize(v[k]);
            vec2 q2 = vec2(dot(q, vec2(radial.y, -radial.x)), dot(q, radial));
            float sd = mix(length(q), (abs(q2.x) * 1.35 + abs(q2.y)) * 0.62, h);
            sat += line_aa(abs(sd - 0.022), 0.0016, pix);
            sat += (1.0 - smoothstep(0.004, 0.004 + pix, sd)) * 0.9;
        }

        light += (mid * hex * hexagram_strength + cool * sat * 0.7) * p_hex;
        light += light_color.rgb * exp(-hd / 0.010) * 0.10 * hexagram_strength * glow_strength * pulse * p_hex;
        core += sat * 0.4 * p_hex;
    }

    // ── Cipher (the gameplay layer) ──────────────────────────────────────
    {
        vec2 cp = rot(p, tt * cipher_spin + cipher_sway * sin(tt * 0.45 + phase));
        vec2 suv = cp / sigil_scale + vec2(0.5);
        float sr = length(suv - vec2(0.5));
        // Sampled unconditionally (implicit derivatives are undefined inside a
        // divergent branch) and masked to the sigil disc instead.
        float inside = 1.0 - step(0.5, sr);
        {
            vec3 m = texture(mask_tex, suv, mip_bias).rgb * inside;
            m = pow(clamp(m, 0.0, 1.0), vec3(mask_gain));

            // Bloom from the mip chain: cheap, smooth, and it widens with the stroke.
            // Three widths: a tight halo, a body glow, and a wide soft wash.
            vec3 g = textureLod(mask_tex, suv, 2.0).rgb * 0.45;
            float o = 0.022;
            g += (textureLod(mask_tex, suv + vec2(o, 0.0), 4.0).rgb
                + textureLod(mask_tex, suv - vec2(o, 0.0), 4.0).rgb
                + textureLod(mask_tex, suv + vec2(0.0, o), 4.0).rgb
                + textureLod(mask_tex, suv - vec2(0.0, o), 4.0).rgb) * 0.20;
            g += textureLod(mask_tex, suv, 5.5).rgb * 0.9;
            g *= inside;

            // Sequential inscription: one beat per arm, each struck from the hub out.
            float cang = bearing(cp);
            float sectors = float(sweep_sectors);
            float sector_w = TAU / sectors;
            float a2 = cang + sector_w * 0.5;
            if (a2 >= TAU) {
                a2 -= TAU;
            }
            float sector = floor(a2 / sector_w);
            float t = p_cipher * sectors;
            float done = floor(t);
            float lt = t - done;
            float reveal;
            if (sector < done) {
                reveal = 1.0;
            } else if (sector > done) {
                reveal = 0.0;
            } else {
                reveal = 1.0 - smoothstep(lt * 0.52, lt * 0.52 + 0.05, sr);
            }
            float hub = smoothstep(0.90, 1.0, p_cipher);
            float centre_mask = 1.0 - smoothstep(0.06, 0.11, sr);
            reveal = mix(reveal, hub, centre_mask);

            // The pen: a bright point riding the tip of the arm being struck.
            if (p_cipher > 0.0 && p_cipher < 0.9) {
                float arm_ang = done * sector_w;
                float tip_r = min(lt * 0.52, 0.48) * sigil_scale;
                vec2 tip = vec2(sin(arm_ang), -cos(arm_ang)) * tip_r;
                float dtip = length(cp - tip);
                light += core_color.rgb * (exp(-dtip / 0.010) * 1.4 + exp(-dtip / 0.035) * 0.35);
            }

            // The stave's colour drifts toward the energy colour and back.
            vec3 stave = mix(light_color.rgb, pop_color.rgb, hue_drift * (0.5 + 0.5 * sin(tt * 0.35 + phase)));
            vec3 lit = stave * m.r * 1.05
                     + accent_color.rgb * m.g * 1.35
                     + accent_color.rgb * m.b * 0.30;

            // Once bound, a pulse rolls out from the hub through the strokes.
            float wr = fract(tt / wave_period + phase * 0.13) * 0.62;
            float wave = exp(-abs(sr - wr) / 0.028) * (1.0 - smoothstep(0.35, 0.62, wr));
            light += pop * (m.r * 0.9 + m.g * 1.2 + g.r * 0.5 + g.g * 0.5) * wave * 1.3
                     * reveal * step(0.999, p_cipher);
            light += lit * reveal;
            core += (m.r * 0.65 + m.g) * reveal;

            // The stave's bloom leans cool, so each violet line sits in a bluish halo.
            vec3 bloom = mix(light_color.rgb, cool_color.rgb, 0.35 * cool_amount) * g.r * 0.8
                       + accent_color.rgb * (g.g * 1.0 + g.b * 0.25);
            light += bloom * glow_strength * pulse * reveal;
        }
    }

    // ── Sparkles ─────────────────────────────────────────────────────────
    {
        float cell_size = 0.05;
        vec2 cid = floor(UV / cell_size);
        vec4 h = hash42(cid + vec2(floor(phase * 7.0), 3.0));
        if (h.x < sparkle_density) {
            vec2 c = (cid + vec2(0.2 + 0.6 * h.y, 0.2 + 0.6 * h.z)) * cell_size;
            vec2 d = UV - c;
            float dl = length(d);
            float tw = pow(max(0.0, sin(TIME * (0.8 + 1.6 * h.w) + h.y * TAU)), 10.0);
            float star = exp(-dl / 0.0035)
                       + exp(-abs(d.x) / 0.0012) * exp(-abs(d.y) / 0.010) * 0.6
                       + exp(-abs(d.y) / 0.0012) * exp(-abs(d.x) / 0.010) * 0.6;
            float rr = length(c - vec2(0.5));
            float inside = 1.0 - smoothstep(0.44, 0.49, rr);
            float s = star * tw * inside * sparkle_strength * detail * p_spark;
            light += mix(cool, core_color.rgb, 0.5) * s;
        }
    }

    // ── Ground light and the finish flash ────────────────────────────────
    // Brightest at the centre and under the band ring, where the light is densest.
    float pool = pool_strength * (pow(max(0.0, 1.0 - r / 0.5), 2.0)
                                  + 0.8 * exp(-abs(r - R_BAND) / 0.05)) * pulse * p_ground;
    light += mix(light_color.rgb, cool_color.rgb, 0.35 * cool_amount) * pool;

    // The seal: a ripple along the band ring, not a sheet of light over the whole circle.
    float fl = flash * (0.08 + 0.55 * exp(-abs(r - R_BAND) / 0.025));
    light += mix(cool, core_color.rgb, 0.5) * fl;

    // Hot cores: where lines are dense the light burns toward white.
    float heat = clamp(core, 0.0, 1.0);
    light += core_color.rgb * heat * heat * core_heat * (1.0 + 0.4 * flash);

    float back = (1.0 - smoothstep(backing_radius - 0.14, backing_radius, r)) * backing_alpha * p_ground;

    ALBEDO = light * intensity * alpha_scale;
    ALPHA = back * alpha_scale;
}
