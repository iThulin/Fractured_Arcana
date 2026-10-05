#!/usr/bin/env python3
# Glyph light circle: light-blue accents (2026-10-04). Run from the repo root.
import sys
rel = 'Assets/Shaders/glyph_sigil.gdshader'
s = open(rel, encoding='utf-8').read()
pairs = [
("""/** What line cores burn toward at full heat. */
uniform vec4 core_color : source_color = vec4(0.96, 0.92, 1.00, 1.0);""",
"""/** Cool counter-tone: outer ring, rune band, sigil rim, satellites, twinkles. */
uniform vec4 cool_color : source_color = vec4(0.55, 0.82, 1.00, 1.0);
/** Master for the cool tone. 0 puts every element back on light_color. */
uniform float cool_amount : hint_range(0.0, 1.0) = 1.0;
/** What line cores burn toward at full heat. Icy rather than pink. */
uniform vec4 core_color : source_color = vec4(0.88, 0.95, 1.00, 1.0);"""),
("""    float rings = line_aa(abs(r - R_OUTER), 0.0020, pix) * 0.80
                + line_aa(abs(r - R_BAND), 0.0026, pix)
                + line_aa(abs(r - r_sig), 0.0030, pix) * 0.95;
    rings *= sweep(ang, p_rings);""",
"""    // Two-tone palette: violet for the band ring, hexagram and stave; light blue for
    // the outer ring, rune band, sigil rim, satellites and twinkles.
    vec3 cool = mix(light_color.rgb, cool_color.rgb, cool_amount);
    vec3 mid = mix(light_color.rgb, cool_color.rgb, 0.5 * cool_amount);

    float ring_out = line_aa(abs(r - R_OUTER), 0.0020, pix) * 0.80;
    float ring_band = line_aa(abs(r - R_BAND), 0.0026, pix);
    float ring_sig = line_aa(abs(r - r_sig), 0.0030, pix) * 0.95;
    float rsw = sweep(ang, p_rings);
    float rings = (ring_out + ring_band + ring_sig) * rsw;"""),
("""    float ring_light = (rings + ticks) * ring_strength;
    light += light_color.rgb * ring_light;
    core += rings * 0.7;""",
"""    light += (cool * (ring_out + ring_sig) * rsw + light_color.rgb * ring_band * rsw
              + cool * ticks) * ring_strength;
    core += rings * 0.7;"""),
("""    float ring_glow = exp(-abs(r - R_BAND) / 0.022) * 0.24
                    + exp(-abs(r - r_sig) / 0.018) * 0.20
                    + exp(-abs(r - R_OUTER) / 0.014) * 0.12;
    light += light_color.rgb * ring_glow * glow_strength * pulse * p_rings;""",
"""    vec3 ring_glow = light_color.rgb * exp(-abs(r - R_BAND) / 0.022) * 0.24
                   + cool * exp(-abs(r - r_sig) / 0.018) * 0.20
                   + cool * exp(-abs(r - R_OUTER) / 0.014) * 0.12;
    light += ring_glow * glow_strength * pulse * p_rings;"""),
("""        light += light_color.rgb * rl;""",
"""        light += cool * rl;"""),
("""        float hx = (hex * hexagram_strength + sat * 0.7) * p_hex;
        light += light_color.rgb * hx;
        light += light_color.rgb * exp(-hd / 0.010) * 0.10 * hexagram_strength * glow_strength * pulse * p_hex;""",
"""        light += (mid * hex * hexagram_strength + cool * sat * 0.7) * p_hex;
        light += light_color.rgb * exp(-hd / 0.010) * 0.10 * hexagram_strength * glow_strength * pulse * p_hex;"""),
("""            vec3 bloom = light_color.rgb * g.r * 0.8""",
"""            // The stave's bloom leans cool, so each violet line sits in a bluish halo.
            vec3 bloom = mix(light_color.rgb, cool_color.rgb, 0.35 * cool_amount) * g.r * 0.8"""),
("""            light += mix(light_color.rgb, core_color.rgb, 0.6) * s;""",
"""            light += mix(cool, core_color.rgb, 0.5) * s;"""),
("""    light += light_color.rgb * pool;""",
"""    light += mix(light_color.rgb, cool_color.rgb, 0.35 * cool_amount) * pool;"""),
("""    light += mix(light_color.rgb, core_color.rgb, 0.5) * fl;""",
"""    light += mix(cool, core_color.rgb, 0.5) * fl;"""),
]
for old, new in pairs:
    n = s.count(old)
    if n != 1:
        sys.exit(f"{rel}: expected 1 match, found {n}:\n{old[:120]}")
    s = s.replace(old, new)
open(rel, 'w', encoding='utf-8').write(s)
print("patched", rel)
