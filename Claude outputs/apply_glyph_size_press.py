#!/usr/bin/env python3
# Glyph tweaks (2026-10-04): circle fills the hex (this grid's HexRadius is 1.325), and
# the grass press clears the centre. Repo root.
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

edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [
("""    private const float GlyphDecalSize = 1.68f;""",
"""    private const float GlyphDecalSize = 2.10f;   // radius 1.05 inside a 1.15 inradius hex (HexRadius 1.325)"""),
("""    private const float GlyphGrassRadius = 0.95f;""",
"""    private const float GlyphGrassRadius = 1.15f;"""),
])

edit('Assets/Shaders/painterly_grass.gdshader', [
("""uniform float glyph_press_flatten : hint_range(0.0, 1.0) = 0.55;""",
"""uniform float glyph_press_flatten : hint_range(0.0, 1.0) = 0.72;"""),
("""uniform float glyph_press_reach : hint_range(1.0, 2.5) = 1.35;""",
"""uniform float glyph_press_reach : hint_range(1.0, 2.5) = 1.25;"""),
("""        float fall = 1.0 - smoothstep(gs.z * 0.75, reach, d);""",
"""        float fall = 1.0 - smoothstep(gs.z * 0.85, reach, d);   // full press across the sigil"""),
])
