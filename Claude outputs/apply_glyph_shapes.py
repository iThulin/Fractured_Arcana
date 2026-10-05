#!/usr/bin/env python3
# Glyph circle: friendly (round, blue) vs hostile (pointed, pink/purple). 2026-10-04. Repo root.
import sys
def edit(rel, pairs):
    s = open(rel, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        if n != 1:
            sys.exit(f"{rel}: expected 1 match, found {n}:\n{old[:200]}")
        s = s.replace(old, new)
    open(rel, 'w', encoding='utf-8').write(s)
    print("patched", rel)

S = 'Assets/Shaders/glyph_sigil.gdshader'
edit(S, [
("""/** Master for the cool tone. 0 puts every element back on light_color. */
uniform float cool_amount : hint_range(0.0, 1.0) = 1.0;
""",
"""/** Master for the cool tone. 0 puts every element back on light_color. */
uniform float cool_amount : hint_range(0.0, 1.0) = 1.0;
/** 0 = a glyph for you and your allies to stand on (round rings, a rosette, circular
    satellites). 1 = a glyph aimed at enemies (a pointed star rim, a faceted band, a
    hexagram, diamond satellites). Set per glyph by HexTile, with the matching palette. */
uniform float hostile : hint_range(0.0, 1.0) = 0.0;
"""),
("""// One procedural rune, q in roughly [-1,1]^2, chosen by seed. Returns distance.""",
"""// Distance to the outline of a regular n-gon with inradius rin (an edge faces up).
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

// One procedural rune, q in roughly [-1,1]^2, chosen by seed. Returns distance."""),
("""    float ring_out = line_aa(abs(r - R_OUTER), 0.0020, pix) * 0.80;
    float ring_band = line_aa(abs(r - R_BAND), 0.0026, pix);""",
"""    // Friendly glyphs are round; hostile ones trade the outer ring for a 12-point star
    // and the band ring for a shallow six-point star. The sigil rim stays a circle in both.
    float h = clamp(hostile, 0.0, 1.0);
    float d_out = mix(abs(r - R_OUTER), sd_star(p, 0.499, 0.452, 12.0), h);
    float d_band = mix(abs(r - R_BAND), sd_star(p, 0.447, 0.412, 6.0), h);
    float ring_out = line_aa(d_out, 0.0020, pix) * 0.80;
    float ring_band = line_aa(d_band, 0.0026, pix);"""),
("""    vec3 ring_glow = light_color.rgb * exp(-abs(r - R_BAND) / 0.022) * 0.24
                   + cool * exp(-abs(r - r_sig) / 0.018) * 0.20
                   + cool * exp(-abs(r - R_OUTER) / 0.014) * 0.12;""",
"""    vec3 ring_glow = light_color.rgb * exp(-d_band / 0.022) * 0.24
                   + cool * exp(-abs(r - r_sig) / 0.018) * 0.20
                   + cool * exp(-d_out / 0.014) * 0.12;"""),
("""        float hd = 1e3;
        hd = min(hd, sd_seg(hp, v[0], v[2]));
        hd = min(hd, sd_seg(hp, v[2], v[4]));
        hd = min(hd, sd_seg(hp, v[4], v[0]));
        hd = min(hd, sd_seg(hp, v[1], v[3]));
        hd = min(hd, sd_seg(hp, v[3], v[5]));
        hd = min(hd, sd_seg(hp, v[5], v[1]));
        float hex = line_aa(hd, 0.0018, pix);

        float sat = 0.0;
        for (int k = 0; k < 6; k++) {
            float sd = length(hp - v[k]);
            sat += line_aa(abs(sd - 0.022), 0.0016, pix);
            sat += (1.0 - smoothstep(0.004, 0.004 + pix, sd)) * 0.9;
        }""",
"""        // Hostile: the hexagram. Friendly: a rosette of six circles, all curves.
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
        }"""),
])

# GlyphData: which glyphs are aimed at enemies.
edit('Scripts/Systems/Combat/Glyphs/GlyphData.cs', [
("""    public bool IsWardAura => Reusable && Trigger == GlyphTrigger.AllyEnter;
""",
"""    public bool IsWardAura => Reusable && Trigger == GlyphTrigger.AllyEnter;

    /// <summary>True for a glyph whose point is to hurt or hinder whoever sets it off
    /// (traps, snares, start-of-turn runes, manual damage glyphs); false for one you or
    /// your allies want to stand on (wards, sigils, anchors, mirrors, Fate Weaver).
    /// Drives the circle's shape and palette and the tooltip name colour.</summary>
    public bool AffectsEnemies => Trigger switch
    {
        GlyphTrigger.Enter or GlyphTrigger.StartOfTurn => true,
        GlyphTrigger.Manual => ReflectCharges <= 0,
        _ => false,
    };
"""),
])

# HexTile: shape + palette per glyph.
edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [
("""        // The bake takes two frames cold and is cached, so a repeat placement of the
        // same spell resolves immediately.""",
"""        // Shape and palette say who the glyph is for: round and blue for you and your
        // allies, pointed and pink/purple for the enemy who steps on it.
        bool hostile = glyph.AffectsEnemies;
        _glyphDecalMaterial.SetShaderParameter("hostile", hostile ? 1.0f : 0.0f);
        _glyphDecalMaterial.SetShaderParameter("light_color", hostile ? GlyphHostileLight : GlyphFriendlyLight);
        _glyphDecalMaterial.SetShaderParameter("cool_color", hostile ? GlyphHostileCool : GlyphFriendlyCool);
        _glyphDecalMaterial.SetShaderParameter("accent_color", hostile ? UITheme.CipherFunction : GlyphFriendlyAccent);
        _glyphDecalMaterial.SetShaderParameter("core_color", hostile ? GlyphHostileCore : GlyphFriendlyCore);

        // The bake takes two frames cold and is cached, so a repeat placement of the
        // same spell resolves immediately."""),
("""    private const string GlyphDecalShaderPath = "res://Assets/Shaders/glyph_sigil.gdshader";""",
"""    private const string GlyphDecalShaderPath = "res://Assets/Shaders/glyph_sigil.gdshader";

    // Glyph palettes (2026-10-04). Friendly: blue light, cyan accents, blue-white spokes.
    // Hostile: violet-pink light, hot pink accents, the Enchanter rose on the spokes.
    private static readonly Color GlyphFriendlyLight  = new Color(0.36f, 0.58f, 1.00f, 1f);
    private static readonly Color GlyphFriendlyCool   = new Color(0.52f, 0.90f, 1.00f, 1f);
    private static readonly Color GlyphFriendlyAccent = new Color(0.50f, 0.78f, 1.00f, 1f);
    private static readonly Color GlyphFriendlyCore   = new Color(0.86f, 0.96f, 1.00f, 1f);
    private static readonly Color GlyphHostileLight   = new Color(0.74f, 0.34f, 0.95f, 1f);
    private static readonly Color GlyphHostileCool    = new Color(1.00f, 0.44f, 0.76f, 1f);
    private static readonly Color GlyphHostileCore    = new Color(1.00f, 0.88f, 0.96f, 1f);"""),
])

# Tooltip: name in the same family colour.
edit('Scripts/Systems/TooltipManager.cs', [
("""        AddPairRow(name, GlyphAccent,""",
"""        AddPairRow(name, g.AffectsEnemies ? GlyphHostileAccent : GlyphAccent,"""),
("""    private static readonly Color GlyphAccent = new Color(0.62f, 0.82f, 1.00f, 1f);""",
"""    private static readonly Color GlyphAccent = new Color(0.62f, 0.82f, 1.00f, 1f);
    private static readonly Color GlyphHostileAccent = new Color(1.00f, 0.56f, 0.84f, 1f);"""),
])
