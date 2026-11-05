#!/usr/bin/env python3
# Lens mock for provinces on every lens (docs/provinces_v1_7d_lenses.md).
# Renders the stand-in world (seed 23, built by the PV1 province port) through the
# atlas colour formulas as they are today and as proposed. Flat hexes only: no prism
# heights, no lighting, no trees or peaks, so the real "now" reads worse than this.
import colorsys
import math
import sys
from collections import deque

from PIL import Image, ImageDraw, ImageFont

from pathlib import Path
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[1] / "provinces_pv1"))
import province_port as pp  # noqa: E402
import test_world as tw  # noqa: E402

SS = 3
W, H = tw.W, tw.H
SQ3 = pp.SQRT3

world = tw.make_world(23)
pp.build(world)
tiles = world["tiles"]
pmap = world["pmap"]
provs = world["provinces"]
nb = pp.neighbor_table(W, H)

# Game colours: UITheme, Hex3DPalette and WorldAtlas3D as of f5195ea.
KP = [(0.56, 0.40, 0.36), (0.38, 0.50, 0.40), (0.36, 0.44, 0.58), (0.58, 0.50, 0.34),
      (0.48, 0.37, 0.55), (0.34, 0.50, 0.50), (0.60, 0.42, 0.32), (0.46, 0.48, 0.34),
      (0.42, 0.40, 0.56), (0.56, 0.44, 0.48), (0.38, 0.52, 0.46), (0.52, 0.48, 0.40)]
LUM = {"Grassland": 1.10, "Forest": 0.78, "Swamp": 0.72, "Mountain": 0.88, "Hills": 0.95,
       "Desert": 0.80, "Tundra": 0.62}
JAMP = {"Grassland": 0.055, "Forest": 0.055, "Swamp": 0.055, "Hills": 0.055}
WATER = (0.30, 0.42, 0.52)
TERRAIN_WATER = (0.18, 0.44, 0.78)
GOLD = (0.88, 0.72, 0.20)
ARCANE = (0.28, 0.55, 0.92)
INK = (0.16, 0.13, 0.12)
CANVAS = (0.72, 0.66, 0.545)
CITY = (0.42, 0.43, 0.47)
VOID = (0.10, 0.10, 0.13)
STANCE = {"Hostile": (0.75, 0.24, 0.22), "Unfriendly": (0.80, 0.46, 0.22),
          "Neutral": (0.48, 0.50, 0.55), "Friendly": (0.28, 0.62, 0.60), "Allied": (0.30, 0.72, 0.40)}
TONE = [1.00, 0.90, 1.08, 0.82]


def lerp(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def mul(a, k):
    return tuple(min(max(x * k, 0.0), 1.0) for x in a)


def jitter(c, col, row, amp):
    h = ((col * 73856093) & 0xFFFFFFFF) ^ ((row * 19349663) & 0xFFFFFFFF)
    h ^= h >> 13
    h = (h * 2654435761) & 0xFFFFFFFF
    h ^= h >> 16
    return mul(c, 1 + (((h & 1023) / 1023) - 0.5) * 2 * amp)


def is_water(i):
    return tiles[i]["terrain"] == "Water"


def amp(i):
    return 0.02 if is_water(i) else JAMP.get(tiles[i]["terrain"], 0.04)


def rxy(i):
    return pp.render_xy(i % W, i // W)


city_tiles = set()
for s in world["settlements"]:
    if s["city"]:
        for (x, y) in s["tiles"]:
            city_tiles.add(y * W + x)

ptiles = {}
for i, p in enumerate(pmap):
    if p != pp.NO_PROVINCE:
        ptiles.setdefault(p, []).append(i)


def on_edge(i, p):
    for d in range(6):
        j = nb[i * 6 + d]
        if j < 0 or pmap[j] != p:
            return True
    return False


def label_tile(p):
    """The province's most interior tile: farthest from its edge, ties to the centroid."""
    ts = ptiles[p]
    members = set(ts)
    dist = {}
    q = deque()
    for i in ts:
        if on_edge(i, p):
            dist[i] = 0
            q.append(i)
    while q:
        i = q.popleft()
        for d in range(6):
            j = nb[i * 6 + d]
            if j >= 0 and j in members and j not in dist:
                dist[j] = dist[i] + 1
                q.append(j)
    cx = sum(rxy(i)[0] for i in ts) / len(ts)
    cy = sum(rxy(i)[1] for i in ts) / len(ts)
    return max(ts, key=lambda i: (dist.get(i, 0), -((rxy(i)[0] - cx) ** 2 + (rxy(i)[1] - cy) ** 2), -i))


LABEL = {p: label_tile(p) for p in ptiles}


def kidx(kid):
    return int(kid.split("_")[-1])


def political(i):
    t = tiles[i]
    if is_water(i):
        return WATER
    return mul(KP[kidx(t["kingdom"]) % 12], LUM.get(t["terrain"], 1.0))


def shade_now(c, i):
    p = pmap[i]
    if p == pp.NO_PROVINCE:
        return c
    k = TONE[provs[p]["tone"] & 3]
    for d in range(6):
        j = nb[i * 6 + d]
        if j < 0:
            continue
        n = pmap[j]
        if n != pp.NO_PROVINCE and n < p and provs[n]["kingdom"] == provs[p]["kingdom"]:
            k *= 0.70
            break
    return mul(c, k)


# ── the scenario: two lunations of a castle parked in one province ──
def pick_window(cw, ch):
    best = None
    for y0 in range(0, H - ch, 4):
        for x0 in range(0, W - cw, 4):
            land = kings = 0
            ks, ps = set(), set()
            for y in range(y0, y0 + ch, 2):
                for x in range(x0, x0 + cw, 2):
                    i = y * W + x
                    if not is_water(i):
                        land += 1
                        ks.add(tiles[i]["kingdom"])
                        ps.add(pmap[i])
            score = (len(ks) >= 3, land, len(ps))
            if best is None or score > best[0]:
                best = (score, x0, y0)
    return best[1], best[2]


BIG = (76, 46)
bx, by = pick_window(*BIG)
cx_t, cy_t = bx + BIG[0] // 2, by + BIG[1] // 2
inside = [p for p in ptiles if bx + 6 <= LABEL[p] % W < bx + BIG[0] - 6 and by + 5 <= LABEL[p] // W < by + BIG[1] - 5]
castle = min(inside, key=lambda p: pp.hexdist(LABEL[p] % W, LABEL[p] // W, cx_t, cy_t))


def pneighbors(p):
    out = set()
    for i in ptiles[p]:
        for d in range(6):
            j = nb[i * 6 + d]
            if j >= 0 and pmap[j] not in (pp.NO_PROVINCE, p):
                out.add(pmap[j])
    return sorted(out)


infl = {p: 0 for p in ptiles}
held = {p: False for p in ptiles}
infl[castle] = 16
ring1 = pneighbors(castle)
for p in ring1:
    infl[p] = 6
ring2 = sorted({q for p in ring1 for q in pneighbors(p)} - set(ring1) - {castle},
               key=lambda q: pp.hexdist(LABEL[q] % W, LABEL[q] // W, LABEL[castle] % W, LABEL[castle] // W))
ring2 = [q for q in ring2 if q in inside] or ring2
infl[ring2[1]] = 44
infl[ring2[0]] = 66
held[ring2[0]] = True
infl[ring2[2]] = 54
held[ring2[2]] = True
if len(ring2) > 3:
    infl[ring2[3]] = 52
kingdoms = sorted({provs[p]["kingdom"] for p in ptiles})
order = ["Friendly", "Unfriendly", "Hostile", "Allied"]
stance = {}
k = 0
for kid in kingdoms:
    if kid == provs[castle]["kingdom"]:
        stance[kid] = "Neutral"
    else:
        stance[kid] = order[k % 4]
        k += 1


def reach_now(i):
    if is_water(i):
        return mul(TERRAIN_WATER, 0.45)
    p = pmap[i]
    fill = lerp(VOID, STANCE[stance[provs[p]["kingdom"]]], infl[p] / 100)
    if held[p] and on_edge(i, p):
        fill = lerp(fill, GOLD, 0.65)
    return fill


def quiet(c):
    h, s, v = colorsys.rgb_to_hsv(*c)
    return lerp(colorsys.hsv_to_rgb(h, s * 0.25, v), CANVAS, 0.28)


def reach_new(i):
    if is_water(i):
        return WATER
    p = pmap[i]
    base = quiet(political(i))
    v = infl[p]
    if held[p]:
        if v >= 60:
            return lerp(base, GOLD, 0.62)
        c = lerp(base, ARCANE, 0.62)
        return lerp(c, GOLD, 0.80) if on_edge(i, p) else c
    if v <= 0:
        return base
    if v < 40:
        return lerp(base, ARCANE, 0.26 + 0.20 * (v - 1) / 38)
    return lerp(base, ARCANE, 0.62)


def fill_of(lens, i):
    if lens == "pol_now":
        c = shade_now(political(i), i)
    elif lens == "pol_new":
        c = political(i) if is_water(i) else mul(political(i), TONE[provs[pmap[i]]["tone"] & 3])
    elif lens == "reach_now":
        c = reach_now(i)
    else:
        c = reach_new(i)
    if i in city_tiles:
        c = CITY
    return jitter(c, i % W, i // W, amp(i))


def rgb8(c, a=255):
    return (int(round(c[0] * 255)), int(round(c[1] * 255)), int(round(c[2] * 255)), a)


FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
FONT_R = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"


def spaced(draw, xy, text, font, fill, halo, track, stroke):
    widths = [draw.textlength(ch, font=font) for ch in text]
    total = sum(widths) + track * (len(text) - 1)
    x = xy[0] - total / 2
    for ch, wch in zip(text, widths):
        draw.text((x, xy[1]), ch, font=font, fill=fill, anchor="lm", stroke_width=stroke, stroke_fill=halo)
        x += wch + track


def panel(lens, x0, y0, cw, ch, S, strokes, labels):
    R = S * SS
    rx0, ry0 = 1.5 * x0, SQ3 * y0
    pad = R
    wpx = int((1.5 * cw + 0.5) * R + 2 * pad)
    hpx = int((SQ3 * ch + 0.9) * R + 2 * pad)
    img = Image.new("RGBA", (wpx, hpx), rgb8(CANVAS))
    dr = ImageDraw.Draw(img)

    def px(i):
        x, y = rxy(i)
        return ((x - rx0) * R + pad, (y - ry0) * R + pad)

    cells = [y * W + x for y in range(y0, y0 + ch) for x in range(x0, x0 + cw)]
    for i in cells:
        cx, cy = px(i)
        pts = [(cx + R * 1.02 * math.cos(math.pi / 3 * a), cy + R * 1.02 * math.sin(math.pi / 3 * a)) for a in range(6)]
        dr.polygon(pts, fill=rgb8(fill_of(lens, i)))
    over = Image.new("RGBA", img.size, (0, 0, 0, 0))
    od = ImageDraw.Draw(over)
    cellset = set(cells)

    def stroke(i, j, frac, width_px, color, alpha):
        (ax, ay), (bx2, by2) = px(i), px(j)
        mx, my = (ax + bx2) / 2, (ay + by2) / 2
        dx, dy = bx2 - ax, by2 - ay
        L = math.hypot(dx, dy)
        dx, dy = dx / L, dy / L
        ex, ey = -dy, dx
        hl = 0.5 * R * frac
        hw = 0.5 * width_px * SS
        poly = [(mx + ex * hl + dx * hw, my + ey * hl + dy * hw), (mx - ex * hl + dx * hw, my - ey * hl + dy * hw),
                (mx - ex * hl - dx * hw, my - ey * hl - dy * hw), (mx + ex * hl - dx * hw, my + ey * hl - dy * hw)]
        od.polygon(poly, fill=rgb8(color, alpha))

    for i in cells:
        if is_water(i):
            continue
        for d in range(6):
            j = nb[i * 6 + d]
            if j < 0 or j < i or j not in cellset or is_water(j):
                continue
            same_k = tiles[i]["kingdom"] == tiles[j]["kingdom"]
            if not same_k:
                if strokes == "now":
                    stroke(i, j, 0.98, 0.06 * S, INK, 255)  # A7: 0.06 world units wide
                else:
                    stroke(i, j, 1.04, 3.0, INK, 255)
            elif pmap[i] != pmap[j] and strokes == "new":
                stroke(i, j, 0.60, 2.0, INK, 205)
    img = Image.alpha_composite(img, over)
    if labels:
        ld = ImageDraw.Draw(img)
        f = ImageFont.truetype(FONT, int(12 * SS))
        fn = ImageFont.truetype(FONT, int(15 * SS))
        for p in ptiles:
            t = LABEL[p]
            if not (x0 + 1 <= t % W < x0 + cw - 1 and y0 + 1 <= t // W < y0 + ch - 1):
                continue
            cx, cy = px(t)
            name = provs[p]["name"].upper()
            spaced(ld, (cx, cy), name, f, rgb8((0.17, 0.14, 0.12)), rgb8((0.86, 0.82, 0.72)), 2.2 * SS, 2 * SS)
            if labels == "reach":
                v = infl[p]
                col = GOLD if held[p] else ((0.80, 0.88, 1.00) if v > 0 else (0.62, 0.60, 0.56))
                ld.text((cx, cy + 17 * SS), str(v), font=fn, fill=rgb8(col), anchor="mm",
                        stroke_width=3 * SS // 2, stroke_fill=rgb8((0.10, 0.09, 0.08)))
    return img.resize((img.width // SS, img.height // SS), Image.LANCZOS).convert("RGB")


def titled(img, title, sub):
    f = ImageFont.truetype(FONT, 17)
    fs = ImageFont.truetype(FONT_R, 13)
    out = Image.new("RGB", (img.width, img.height + 50), (24, 22, 30))
    out.paste(img, (0, 50))
    d = ImageDraw.Draw(out)
    d.text((10, 8), title, font=f, fill=(235, 230, 245))
    d.text((10, 30), sub, font=fs, fill=(170, 165, 185))
    return out


S_FAR = 7
far = dict(x0=bx, y0=by, cw=BIG[0], ch=BIG[1], S=S_FAR)
p1 = titled(panel("pol_now", strokes="now", labels=None, **far), "Political, now",
            "Tones 1.00/0.90/1.08/0.82, border tile 30% darker, kingdom ink 0.06 units wide")
p2 = titled(panel("pol_new", strokes="new", labels=None, **far), "Political, proposed",
            "Same fill without the darker tile; kingdom ink 3 px; province ink 2 px, dashed")
p3 = titled(panel("reach_now", strokes="now", labels=None, **far), "Reach, now",
            "Void to stance colour by influence; gold edge on held provinces")
p4 = titled(panel("reach_new", strokes="new", labels=None, **far), "Reach, proposed",
            "Quiet map; blue wash in bands at 40; gold fill held, gold edge held but below 60")

SMALL = (32, 19)
lx, ly = LABEL[castle] % W, LABEL[castle] // W
sx = min(max(lx - SMALL[0] // 2 + 2, 0), W - SMALL[0])
sy = min(max(ly - SMALL[1] // 2 + 1, 0), H - SMALL[1])
near = dict(x0=sx, y0=sy, cw=SMALL[0], ch=SMALL[1], S=18)
p5 = titled(panel("pol_new", strokes="new", labels="names", **near), "Proposed, zoomed in: Political",
            "Province names in spaced capitals at each province's most interior tile")
p6 = titled(panel("reach_new", strokes="new", labels="reach", **near), "Proposed, zoomed in: Reach",
            "Names with the guild's influence; gold numbers are held provinces")

cw_ = max(p1.width, p5.width)
rows = [(p1, p2), (p3, p4), (p5, p6)]
Ht = sum(max(a.height, b.height) for a, b in rows) + 8 * 4 + 64
sheet = Image.new("RGB", (cw_ * 2 + 8 * 3, Ht), (24, 22, 30))
y = 8
for a, b in rows:
    sheet.paste(a, (8, y))
    sheet.paste(b, (16 + cw_, y))
    y += max(a.height, b.height) + 8
d = ImageDraw.Draw(sheet)
f = ImageFont.truetype(FONT_R, 14)
sw = [("0", lerp(quiet(KP[1]), ARCANE, 0.0)), ("1 to 39", lerp(quiet(KP[1]), ARCANE, 0.34)),
      ("40 to 59", lerp(quiet(KP[1]), ARCANE, 0.62)), ("held, below 60", None), ("held, 60 or more", lerp(quiet(KP[1]), GOLD, 0.62))]
x = 12
d.text((x, y + 4), "Reach bands (proposed):", font=f, fill=(220, 215, 230))
x += 190
for label, col in sw:
    if col is None:
        d.rectangle([x, y + 2, x + 26, y + 22], fill=rgb8(lerp(quiet(KP[1]), ARCANE, 0.62))[:3], outline=rgb8(GOLD)[:3], width=4)
    else:
        d.rectangle([x, y + 2, x + 26, y + 22], fill=rgb8(col)[:3])
    d.text((x + 32, y + 4), label, font=f, fill=(220, 215, 230))
    x += 40 + int(d.textlength(label, font=f)) + 22
d.text((12, y + 32), "Stand-in world, seed 23, PV1 province port. Flat hexes: no prism heights, lighting, trees or peaks, so the game's 'now' reads worse than this.",
       font=f, fill=(160, 155, 175))
sheet.save(str(HERE / "lens_mock_7d.png"), optimize=True)
print("castle", provs[castle]["name"], provs[castle]["kingdom"], "ring1", [(provs[p]["name"], infl[p]) for p in ring1])
print("ring2 picks", [(provs[q]["name"], infl[q], held[q]) for q in ring2[:4]])
print("window", bx, by, "near", sx, sy, "sheet", sheet.size)
