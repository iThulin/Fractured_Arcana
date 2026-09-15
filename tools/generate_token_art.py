#!/usr/bin/env python3
"""Regenerate Assets/UI/Tokens/*.png placeholder art: colored disc + drawn
symbol per token. Replaces the original letter discs, whose initials became
ambiguous once the verb band grouped tokens (Sway held two C's and a P;
Patience, Persuade, and Pass all collided on P). Each token keeps its
established hue; the symbol is drawn geometry, no font or emoji dependency.
Re-run any time: python3 tools/generate_token_art.py"""
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Assets", "UI", "Tokens")
S = 4              # supersample factor
SZ = 128 * S       # working canvas
C = SZ // 2        # center
WHITE = (246, 242, 250, 255)

# Established hues, sampled from the original letter discs.
BASE = {
    "charm":         (134, 85, 157),
    "connections":   (50, 128, 114),
    "demonstration": (164, 107, 42),
    "guile":         (64, 114, 64),
    "insight":       (85, 71, 157),
    "intimidate":    (143, 57, 50),
    "offering":      (153, 128, 64),
    "patience":      (121, 114, 92),
    "persuade":      (64, 100, 157),
    "poise":         (78, 92, 121),
    "resolve":       (135, 100, 35),
    "pass":          (90, 86, 110),
}

def shade(c, f):
    return tuple(min(255, int(v * f)) for v in c)

def pt(x, y):
    return (C + x * S, C + y * S)

def disc(draw, base):
    rim = shade(base, 0.49) + (255,)
    lite = shade(base, 1.45) + (186,)
    r = 60 * S
    draw.ellipse([C - r, C - r, C + r, C + r], fill=lite)          # soft edge
    r2 = 57 * S
    draw.ellipse([C - r2, C - r2, C + r2, C + r2], fill=base + (255,))
    draw.ellipse([C - r2, C - r2, C + r2, C + r2],
                 outline=rim, width=6 * S)

def poly(draw, pts, fill=WHITE):
    draw.polygon([pt(x, y) for x, y in pts], fill=fill)

def sym_charm(d, base):
    # A heart: warmth offered across the table.
    d.ellipse([*pt(-26, -26), *pt(2, 2)], fill=WHITE)    # left lobe
    d.ellipse([*pt(-2, -26), *pt(26, 2)], fill=WHITE)    # right lobe
    poly(d, [(-25, -8), (25, -8), (0, 36)])

def sym_persuade(d, base):
    # Balance scales: the argument, weighed.
    w = 5 * S
    d.line([pt(-32, -14), pt(32, -14)], fill=WHITE, width=w)       # beam
    d.line([pt(0, -26), pt(0, 24)], fill=WHITE, width=w)           # post
    d.line([pt(-18, 30), pt(18, 30)], fill=WHITE, width=w)         # base
    d.ellipse([*pt(-4, -34), pt(4, -26)[0], pt(4, -26)[1]], fill=WHITE)
    for sx in (-32, 32):
        d.line([pt(sx, -14), pt(sx - 10, 2)], fill=WHITE, width=3 * S)
        d.line([pt(sx, -14), pt(sx + 10, 2)], fill=WHITE, width=3 * S)
        d.chord([*pt(sx - 13, -6), pt(sx + 13, 16)[0], pt(sx + 13, 16)[1]],
                0, 180, fill=WHITE)

def sym_connections(d, base):
    # Two linked rings.
    for cx in (-14, 14):
        d.ellipse([*pt(cx - 20, -20), pt(cx + 20, 20)[0], pt(cx + 20, 20)[1]],
                  outline=WHITE, width=7 * S)

def sym_intimidate(d, base):
    # A dagger, point down.
    poly(d, [(0, 42), (-9, -2), (9, -2)])                          # blade
    poly(d, [(-21, -2), (21, -2), (21, -10), (-21, -10)])          # guard
    poly(d, [(-5, -10), (5, -10), (5, -30), (-5, -30)])            # hilt
    r = 7 * S
    x, y = pt(0, -36)
    d.ellipse([x - r, y - r, x + r, y + r], fill=WHITE)

def sym_demonstration(d, base):
    # A four-point sparkle: the precise display of power.
    poly(d, [(0, -40), (8, -8), (40, 0), (8, 8), (0, 40), (-8, 8), (-40, 0), (-8, -8)])
    poly(d, [(27, -34), (30, -25), (39, -22), (30, -19), (27, -10), (24, -19), (15, -22), (24, -25)])

def sym_offering(d, base):
    # A drawstring pouch.
    d.ellipse([*pt(-27, -8), pt(27, 40)[0], pt(27, 40)[1]], fill=WHITE)
    poly(d, [(-10, -12), (10, -12), (17, -30), (-17, -30)])
    poly(d, [(-13, -16), (13, -16), (13, -9), (-13, -9)],
         fill=shade(base, 0.49) + (255,))

def sym_insight(d, base):
    # An eye: the read.
    d.ellipse([*pt(-38, -19), pt(38, 19)[0], pt(38, 19)[1]],
              outline=WHITE, width=6 * S)
    r = 13 * S
    d.ellipse([C - r, C - r, C + r, C + r], fill=WHITE)
    r2 = 5 * S
    d.ellipse([C - r2, C - r2, C + r2, C + r2], fill=base + (255,))

def sym_patience(d, base):
    # An hourglass.
    poly(d, [(-23, -38), (23, -38), (23, -31), (-23, -31)])
    poly(d, [(-23, 31), (23, 31), (23, 38), (-23, 38)])
    poly(d, [(-19, -31), (19, -31), (0, -2)])
    poly(d, [(-19, 31), (19, 31), (0, 2)])

def sym_resolve(d, base):
    # A flame: what still drives them.
    poly(d, [(0, -38), (14, -16), (10, -6), (23, 10), (16, 30), (0, 38),
             (-16, 30), (-23, 10), (-10, -6), (-14, -16)])
    poly(d, [(0, 2), (9, 16), (0, 30), (-9, 16)], fill=base + (255,))

def sym_guile(d, base):
    # A document with a folded corner and fine print.
    rim = shade(base, 0.49) + (255,)
    poly(d, [(-21, -30), (9, -30), (21, -18), (21, 30), (-21, 30)])
    poly(d, [(9, -30), (21, -18), (9, -18)], fill=rim)
    for i, y in enumerate((-8, 2, 12, 22)):
        wdt = 28 if i < 3 else 16
        poly(d, [(-14, y), (-14 + wdt, y), (-14 + wdt, y + 4), (-14, y + 4)],
             fill=base + (255,))

def sym_poise(d, base):
    # Stacked stones: balance held.
    d.ellipse([*pt(-30, 14), pt(30, 38)[0], pt(30, 38)[1]], fill=WHITE)
    d.ellipse([*pt(-23, -10), pt(23, 12)[0], pt(23, 12)[1]], fill=WHITE)
    d.ellipse([*pt(-15, -34), pt(15, -12)[0], pt(15, -12)[1]], fill=WHITE)

def sym_pass(d, base):
    # The silence, stretched.
    for cx in (-22, 0, 22):
        r = 6 * S
        x, y = pt(cx, 0)
        d.ellipse([x - r, y - r, x + r, y + r], fill=WHITE)

SYMBOLS = {
    "charm": sym_charm, "persuade": sym_persuade, "connections": sym_connections,
    "intimidate": sym_intimidate, "demonstration": sym_demonstration,
    "offering": sym_offering, "insight": sym_insight, "patience": sym_patience,
    "resolve": sym_resolve, "guile": sym_guile, "poise": sym_poise,
    "pass": sym_pass,
}

for name, base in BASE.items():
    im = Image.new("RGBA", (SZ, SZ), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    disc(d, base)
    SYMBOLS[name](d, base)
    im = im.resize((128, 128), Image.LANCZOS)
    im.save(os.path.join(OUT, f"{name}.png"))
    print(f"drew {name}.png")
print("done")
