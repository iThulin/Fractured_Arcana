#!/usr/bin/env python3
# Glyph cast readability (2026-10-04): no cast burst over a glyph, a slower piece-by-piece
# inscription with a glowing pen tip, a softer seal flash, a calmer hub. Repo root.
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
("""    // Inscription schedule.
    float p_ground = smoothstep(0.0, 0.20, P);
    float p_rings = clamp(P / 0.40, 0.0, 1.0);
    float p_runes = clamp((P - 0.10) / 0.40, 0.0, 1.0);
    float p_hex = smoothstep(0.25, 0.60, P);
    float p_cipher = clamp((P - 0.35) / 0.60, 0.0, 1.0);
    float p_spark = smoothstep(0.80, 1.0, P);""",
"""    // Inscription schedule, drawn piece by piece: rings sweep round, the runes follow,
    // the inner pattern settles, then the cipher's arms are struck one at a time from
    // the hub out, the hub seals, and the twinkles arrive last.
    float p_ground = smoothstep(0.0, 0.12, P);
    float p_rings = clamp(P / 0.24, 0.0, 1.0);
    float p_runes = clamp((P - 0.12) / 0.24, 0.0, 1.0);
    float p_hex = smoothstep(0.26, 0.40, P);
    float p_cipher = clamp((P - 0.38) / 0.56, 0.0, 1.0);
    float p_spark = smoothstep(0.90, 1.0, P);"""),
("""            float hub = smoothstep(0.90, 1.0, p_cipher);
            float centre_mask = 1.0 - smoothstep(0.06, 0.11, sr);
            reveal = mix(reveal, hub, centre_mask);

            vec3 lit = light_color.rgb * m.r * 1.05
                     + accent_color.rgb * m.g * 1.35
                     + accent_color.rgb * m.b * 0.42;""",
"""            float hub = smoothstep(0.90, 1.0, p_cipher);
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

            vec3 lit = light_color.rgb * m.r * 1.05
                     + accent_color.rgb * m.g * 1.35
                     + accent_color.rgb * m.b * 0.30;"""),
("""                       + accent_color.rgb * (g.g * 1.1 + g.b * 0.5);""",
"""                       + accent_color.rgb * (g.g * 1.0 + g.b * 0.25);"""),
("""    float fl = flash * (0.35 + 0.65 * exp(-abs(r - R_BAND) / 0.03));""",
"""    // The seal: a ripple along the band ring, not a sheet of light over the whole circle.
    float fl = flash * (0.08 + 0.55 * exp(-abs(r - R_BAND) / 0.025));"""),
("""    light += core_color.rgb * heat * heat * core_heat * (1.0 + flash);""",
"""    light += core_color.rgb * heat * heat * core_heat * (1.0 + 0.4 * flash);"""),
])

edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [
("""    private const float GlyphInscribeSeconds = 1.35f;""",
"""    private const float GlyphInscribeSeconds = 2.2f;"""),
("""    private const float GlyphFlashSeconds = 0.7f;""",
"""    private const float GlyphFlashSeconds = 0.5f;"""),
])

# No cast burst over a glyph: the inscription IS the cast visual. Only halves that
# prepare a glyph, and only when the card authored no vfx archetype of its own.
edit('Scripts/Systems/Combat/Presentation/VisualEvent.cs', [
("""            ev.VfxArchetype = half.VfxArchetype;
            ev.VfxStyle = half.VfxStyle;""",
"""            ev.VfxArchetype = half.VfxArchetype;
            ev.VfxStyle = half.VfxStyle;
            if (string.IsNullOrEmpty(ev.VfxArchetype) && PreparesGlyph(half.Effects, 0))
                ev.VfxArchetype = VfxLibrary.ArchNone;"""),
("""    public static VisualEvent ForCast(StackItem item)""",
"""    /// <summary>True when the effect tree prepares a glyph on the board. Its tile shows the
    /// glyph being inscribed, which a cast burst on the same tile would bury.</summary>
    private static bool PreparesGlyph(IEnumerable<IEffect> effects, int depth)
    {
        if (effects == null || depth > 8)
            return false;
        foreach (var e in effects)
        {
            if (e is PrepareGlyphEffect or ReflectWardEffect or SpellAnchorEffect or EnchantPillarEffect)
                return true;
            if (PreparesGlyph(e?.Children, depth + 1))
                return true;
        }
        return false;
    }

    public static VisualEvent ForCast(StackItem item)"""),
])
