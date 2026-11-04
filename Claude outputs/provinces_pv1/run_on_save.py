#!/usr/bin/env python3
"""Run the PV1 province build's Python mirror (province_port.py) on a real
save's world, without Godot. Usage:

    python3 run_on_save.py <save or cycle .json> [map.png]

Finds the first JSON object holding width, height and a non-empty tiles list
(the cycle's WorldData as SaveManager writes it: camelCase fields, enums as
numbers, settlement tiles as item1/item2 pairs), builds provinces, checks every
rule, prints the numbers the in-game Province Report prints, and draws the map
when a PNG path is given and Pillow is installed. Reads the save; never writes it.
"""
import colorsys
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import province_port as pp  # noqa: E402

TERRAIN = ["Grassland", "Forest", "Road", "Ruins", "Mountain", "Swamp", "ArcaneGround", "Volcanic",
           "Water", "Hills", "Coast", "Lake", "Desert", "Tundra", "Snow", "Marsh"]


def find_world(node):
    if isinstance(node, dict):
        tiles = node.get("tiles")
        if isinstance(tiles, list) and tiles and "width" in node and "height" in node:
            return node
        for value in node.values():
            found = find_world(value)
            if found is not None:
                return found
    elif isinstance(node, list):
        for value in node:
            found = find_world(value)
            if found is not None:
                return found
    return None


def convert(data):
    tiles = []
    for t in data["tiles"]:
        terrain = t.get("terrain", 0)
        name = TERRAIN[terrain] if isinstance(terrain, int) else str(terrain)
        tiles.append({"terrain": name, "elev": float(t.get("elevation", 0.0)),
                      "kingdom": t.get("kingdomId") or "", "river": int(t.get("riverEdges", 0))})
    settlements = []
    for s in data.get("settlements", []):
        foot = [(p.get("item1"), p.get("item2")) for p in s.get("tiles", [])]
        settlements.append({"cx": s.get("centerX", 0), "cy": s.get("centerY", 0), "seat": bool(s.get("isSeat", False)),
                            "city": s.get("tier", 0) == 1, "tiles": foot, "kingdom": s.get("kingdomId", "")})
    return {"W": data["width"], "H": data["height"], "tiles": tiles, "settlements": settlements}


def render(world, path, scale=7):
    from PIL import Image, ImageDraw
    w, h = world["W"], world["H"]
    img = Image.new("RGB", (int(w * 1.5 * scale + 2 * scale), int(h * pp.SQRT3 * scale + 2 * scale)), (40, 60, 90))
    draw = ImageDraw.Draw(img)
    pmap, provinces = world["pmap"], world["provinces"]
    kingdoms = sorted({p["kingdom"] for p in provinces})
    hue = {k: (i * 0.618) % 1.0 for i, k in enumerate(kingdoms)}
    tone = [0.80, 0.68, 0.90, 0.58]
    nb = pp.neighbor_table(w, h)
    for i in range(w * h):
        cx, cy = pp.render_xy(i % w, i // w)
        cx, cy = cx * scale + scale, cy * scale + scale
        pts = [(cx + scale * math.cos(math.pi / 3 * a), cy + scale * math.sin(math.pi / 3 * a)) for a in range(6)]
        p = pmap[i]
        if p == pp.NO_PROVINCE:
            color = (40, 60, 90)
        else:
            pr = provinces[p]
            v = tone[pr["tone"]]
            if any(nb[i * 6 + d] >= 0 and pmap[nb[i * 6 + d]] != pp.NO_PROVINCE and pmap[nb[i * 6 + d]] < p
                   and provinces[pmap[nb[i * 6 + d]]]["kingdom"] == pr["kingdom"] for d in range(6)):
                v *= 0.7
            r, g, b = colorsys.hsv_to_rgb(hue[pr["kingdom"]], 0.45, v)
            color = (int(r * 255), int(g * 255), int(b * 255))
        draw.polygon(pts, fill=color)
    for i in range(w * h):
        bits = world["tiles"][i]["river"]
        if not bits:
            continue
        cx, cy = pp.render_xy(i % w, i // w)
        cx, cy = cx * scale + scale, cy * scale + scale
        for d in range(6):
            j = nb[i * 6 + d]
            if j < 0 or not bits & (1 << d):
                continue
            jx, jy = pp.render_xy(j % w, j // w)
            jx, jy = jx * scale + scale, jy * scale + scale
            mx, my = (cx + jx) / 2, (cy + jy) / 2
            vx, vy = jy - cy, -(jx - cx)
            length = math.hypot(vx, vy) or 1.0
            vx, vy = vx / length * scale * 0.55, vy / length * scale * 0.55
            draw.line([(mx - vx, my - vy), (mx + vx, my + vy)], fill=(70, 140, 230), width=2)
    for s in world["settlements"]:
        cx, cy = pp.render_xy(s["cx"], s["cy"])
        cx, cy = cx * scale + scale, cy * scale + scale
        r = 5 if s["seat"] else (4 if s["city"] else 2)
        draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(250, 240, 200) if s["seat"] else (30, 30, 30))
    for pr in provinces:
        cx, cy = pp.render_xy(*pr["seat"])
        draw.text((cx * scale + scale + 6, cy * scale + scale - 6), pr["name"], fill=(15, 15, 15))
    img.save(path)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    with open(sys.argv[1], encoding="utf-8") as fh:
        data = find_world(json.load(fh))
    if data is None:
        print("No world (width, height, tiles) found in that file.")
        sys.exit(1)
    world = convert(data)
    log = []
    pp.build(world, log)
    fails = pp.validate(world)
    provinces = world["provinces"]
    sizes = sorted(p["tiles"] for p in provinces)
    edges, edges_river, edges_high, borders, borders_river, borders_high = pp.border_stats(world)
    print("%d provinces; sizes %d..%d, median %d; build info %s" % (
        len(provinces), sizes[0], sizes[-1], sizes[len(sizes) // 2], world["build_info"]))
    print("borders inside kingdoms on a river %.0f%% (all edges %.0f%%), on high ground %.0f%% (all edges %.0f%%)" % (
        100.0 * borders_river / max(1, borders), 100.0 * edges_river / max(1, edges),
        100.0 * borders_high / max(1, borders), 100.0 * edges_high / max(1, edges)))
    print("detached island pieces:", world["detached"], " balance:", log)
    by_realm = {}
    for p in provinces:
        by_realm.setdefault(p["kingdom"], []).append("%s %d" % (p["name"], p["tiles"]))
    for realm, names in sorted(by_realm.items()):
        print("  %s (%d): %s" % (realm or "(no kingdom)", len(names), "; ".join(names)))
    print("checks:", "all hold" if not fails else fails[:10])
    if len(sys.argv) > 2:
        render(world, sys.argv[2])
        print("map drawn to", sys.argv[2])


if __name__ == "__main__":
    main()
