"""A full-size stand-in world (158x96, ten kingdoms, rivers, ridges, islands)
generated here only to exercise the province build at scale: sizes, counts,
islands, splits and merges, names, timing. It is NOT the game's generator; the
kingdom partition is a port of WorldGenerator.AssignTerritories."""
import sys, time, math, heapq, random
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "provinces_pv1"))
import numpy as np
import province_port as pp

W, H = 158, 96

def value_noise(rng, w, h, scale):
    gw, gh = int(w / scale) + 3, int(h / scale) + 3
    grid = rng.random((gh, gw))
    ys, xs = np.mgrid[0:h, 0:w]
    fx, fy = xs / scale, ys / scale
    x0, y0 = fx.astype(int), fy.astype(int)
    tx, ty = fx - x0, fy - y0
    tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty)
    a = grid[y0, x0]; b = grid[y0, x0 + 1]; c = grid[y0 + 1, x0]; d = grid[y0 + 1, x0 + 1]
    return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty

def fractal(rng, w, h, base, octaves=5):
    out = np.zeros((h, w)); amp = 1.0; tot = 0
    for o in range(octaves):
        out += amp * value_noise(rng, w, h, base / (2 ** o)); tot += amp; amp *= 0.5
    return out / tot

def make_world(seed, land_frac=0.5, blobs=4):
    rng = np.random.default_rng(seed)
    elev = fractal(rng, W, H, 40.0)
    ridge = 1.0 - np.abs(fractal(rng, W, H, 30.0) * 2 - 1)
    ys, xs = np.mgrid[0:H, 0:W]
    mask = np.zeros((H, W))
    for _ in range(blobs):
        cx, cy = rng.uniform(0.2, 0.8) * W, rng.uniform(0.25, 0.75) * H
        rad = rng.uniform(0.22, 0.38) * W
        mask = np.maximum(mask, 1.0 - np.sqrt(((xs - cx) / rad) ** 2 + ((ys - cy) / (rad * 0.7)) ** 2))
    raw = 0.55 * elev + 0.55 * mask + 0.25 * ridge ** 3
    sea = np.quantile(raw, 1.0 - land_frac)
    land = raw > sea
    e = np.where(land, 0.2 + 0.8 * (raw - sea) / (raw.max() - sea), 0.0)
    moist = fractal(rng, W, H, 25.0)
    tiles = []
    for y in range(H):
        for x in range(W):
            if not land[y, x]:
                tiles.append({"terrain": "Water", "elev": 0.0, "kingdom": "", "river": 0})
                continue
            el = float(np.float32(e[y, x])); m = moist[y, x]
            lat = abs(y - H / 2) / (H / 2)
            if el > 0.78: t = "Mountain"
            elif el > 0.62: t = "Hills"
            elif lat > 0.85: t = "Tundra"
            elif m > 0.62 and el < 0.35: t = "Swamp"
            elif m > 0.55: t = "Forest"
            elif m < 0.33 and lat < 0.4: t = "Desert"
            else: t = "Grassland"
            tiles.append({"terrain": t, "elev": el, "kingdom": "", "river": 0})
    nb = pp.neighbor_table(W, H)
    n = W * H
    # coast
    for i in range(n):
        if tiles[i]["terrain"] in ("Grassland", "Forest", "Desert") and any(
                nb[i * 6 + d] >= 0 and tiles[nb[i * 6 + d]]["terrain"] == "Water" for d in range(6)):
            tiles[i]["terrain"] = "Coast"
    # rivers: steepest descent paths with flow; a river tile walls its non-path edges
    order = sorted([i for i in range(n) if tiles[i]["terrain"] != "Water"], key=lambda i: -tiles[i]["elev"])
    down = {}
    for i in order:
        best = -1; be = tiles[i]["elev"]
        for d in range(6):
            j = nb[i * 6 + d]
            if j >= 0 and (tiles[j]["terrain"] == "Water" or tiles[j]["elev"] < be):
                be = tiles[j]["elev"] if tiles[j]["terrain"] != "Water" else -1; best = j
        down[i] = best
    flow = {i: 1 for i in order}
    for i in order:
        j = down[i]
        if j >= 0 and j in flow:
            flow[j] += flow[i]
    river_tiles = {i for i in order if flow[i] >= 60}
    for i in river_tiles:
        keep = {down[i]} | {k for k in river_tiles if down.get(k) == i}
        for d in range(6):
            j = nb[i * 6 + d]
            if j < 0 or j in keep or tiles[j]["terrain"] == "Water":
                continue
            tiles[i]["river"] |= 1 << d
            tiles[j]["river"] |= 1 << ((d + 3) % 6)
    # capitals: farthest point over land, hex distance
    rnd = random.Random(seed)
    landidx = [i for i in range(n) if tiles[i]["terrain"] != "Water"]
    caps = [rnd.choice(landidx)]
    while len(caps) < 10:
        best = max(landidx, key=lambda i: (min(pp.hexdist(i % W, i // W, c % W, c // W) for c in caps), -i))
        caps.append(best)
    # AssignTerritories port (float32-ish costs, river weight 5)
    cost = [math.inf] * n; owner = [-1] * n; done = [False] * n; pq = []
    for k, c in enumerate(caps):
        cost[c] = 0.0; owner[c] = k; heapq.heappush(pq, (0.0, c))
    while pq:
        _, cur = heapq.heappop(pq)
        if done[cur]: continue
        done[cur] = True
        for d in range(6):
            j = nb[cur * 6 + d]
            if j < 0 or done[j] or tiles[j]["terrain"] == "Water": continue
            c = 1.0 + 12.0 * max(0.0, tiles[j]["elev"] - 0.5)
            if tiles[cur]["river"] & (1 << d): c += 5.0
            nc = cost[cur] + c
            if nc < cost[j]:
                cost[j] = nc; owner[j] = owner[cur]; heapq.heappush(pq, (nc, j))
    for i in landidx:
        o = owner[i]
        if o < 0:
            o = min(range(10), key=lambda k: (pp.hexdist(i % W, i // W, caps[k] % W, caps[k] // W), k))
        tiles[i]["kingdom"] = "kingdom_%d" % o
    # settlements
    sets = []; taken = set()
    def add(i, city, seat, kingdom, foot_r):
        x, y = i % W, i // W
        foot = [(x, y)]
        if foot_r:
            for d in range(6):
                j = nb[i * 6 + d]
                if j >= 0 and tiles[j]["terrain"] != "Water":
                    foot.append((j % W, j // W))
        sets.append({"cx": x, "cy": y, "seat": seat, "city": city, "tiles": foot, "kingdom": kingdom})
        taken.add(i)
    for k, c in enumerate(caps):
        add(c, True, True, "kingdom_%d" % k, True)
    for k in range(10):
        kid = "kingdom_%d" % k
        mine = [i for i in landidx if tiles[i]["kingdom"] == kid]
        count = min(max(round(len(mine) / 200), 1), 6)
        if len(mine) > 650:
            count += 1
        tries = 0; placed = 0
        while placed < count and tries < 500:
            tries += 1
            i = rnd.choice(mine)
            if any(pp.hexdist(i % W, i // W, t % W, t // W) < 6 for t in taken): continue
            city = placed == 0 and len(mine) > 650
            add(i, city, False, kid, city or rnd.random() < 0.3)
            placed += 1
    for _ in range(5):  # junction towns anywhere
        i = rnd.choice(landidx)
        if all(pp.hexdist(i % W, i // W, t % W, t // W) >= 3 for t in taken):
            add(i, False, False, tiles[i]["kingdom"], False)
    return {"W": W, "H": H, "tiles": tiles, "settlements": sets}

def render(world, path):
    from PIL import Image, ImageDraw
    S = 7
    img = Image.new("RGB", (int(W * 1.5 * S + 2 * S), int(H * pp.SQRT3 * S + 2 * S)), (40, 60, 90))
    dr = ImageDraw.Draw(img)
    pmap = world["pmap"]; provs = world["provinces"]
    hues = {}
    import colorsys
    kingdoms = sorted({p["kingdom"] for p in provs})
    for k, kid in enumerate(kingdoms):
        hues[kid] = (k * 0.618) % 1.0
    tone_v = [0.80, 0.68, 0.90, 0.58]
    nb = pp.neighbor_table(W, H)
    for i in range(W * H):
        x, y = i % W, i // W
        cx, cy = pp.render_xy(x, y); cx = cx * S + S; cy = cy * S + S
        pts = [(cx + S * math.cos(math.pi / 3 * a), cy + S * math.sin(math.pi / 3 * a)) for a in range(6)]
        p = pmap[i]
        if p == pp.NO_PROVINCE:
            col = (40, 60, 90)
        else:
            pr = provs[p]
            v = tone_v[pr["tone"]]
            border = any(nb[i * 6 + d] >= 0 and pmap[nb[i * 6 + d]] != pp.NO_PROVINCE and pmap[nb[i * 6 + d]] < p
                         and provs[pmap[nb[i * 6 + d]]]["kingdom"] == pr["kingdom"] for d in range(6))
            if border: v *= 0.7
            r, g, b = colorsys.hsv_to_rgb(hues[pr["kingdom"]], 0.45, v)
            col = (int(r * 255), int(g * 255), int(b * 255))
        dr.polygon(pts, fill=col)
    # rivers: draw river edges
    for i in range(W * H):
        rb = world["tiles"][i]["river"]
        if not rb: continue
        x, y = i % W, i // W
        cx, cy = pp.render_xy(x, y); cx = cx * S + S; cy = cy * S + S
        for d in range(6):
            if rb & (1 << d):
                j = nb[i * 6 + d]
                if j < 0: continue
                jx, jy = pp.render_xy(j % W, j // W); jx = jx * S + S; jy = jy * S + S
                mx, my = (cx + jx) / 2, (cy + jy) / 2
                vx, vy = (jy - cy), -(jx - cx)
                ln = math.hypot(vx, vy) or 1
                vx, vy = vx / ln * S * 0.55, vy / ln * S * 0.55
                dr.line([(mx - vx, my - vy), (mx + vx, my + vy)], fill=(70, 140, 230), width=2)
    for st in world["settlements"]:
        cx, cy = pp.render_xy(st["cx"], st["cy"]); cx = cx * S + S; cy = cy * S + S
        r = 5 if st["seat"] else (4 if st["city"] else 2)
        dr.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(250, 240, 200) if st["seat"] else (30, 30, 30))
    for pr in provs:
        cx, cy = pp.render_xy(*pr["seat"]); cx = cx * S + S; cy = cy * S + S
        dr.text((cx + 6, cy - 6), pr["name"], fill=(15, 15, 15))
    img.save(path)

if __name__ == "__main__":
    for seed, frac, blobs in [(11, 0.50, 4), (23, 0.45, 5), (37, 0.38, 9)]:
        world = make_world(seed, frac, blobs)
        t0 = time.time(); log = []
        pp.build(world, log)
        dt = time.time() - t0
        fails = pp.validate(world)
        provs = world["provinces"]
        sizes = [p["tiles"] for p in provs]
        per = {}
        for p in provs:
            per.setdefault(p["kingdom"], []).append(p["tiles"])
        e, er, eh, b, br, bh = pp.border_stats(world)
        print("seed %d land %.2f: %d provinces, sizes %d..%d mean %.0f; build %.1fs (python); info %s" % (
            seed, frac, len(provs), min(sizes), max(sizes), sum(sizes) / len(sizes), dt,
            {k: v for k, v in world["build_info"].items() if k != "targets"}))
        print("  per kingdom:", {k: (len(v), sum(v)) for k, v in sorted(per.items())})
        print("  targets:", world["build_info"]["targets"])
        print("  borders on river %.0f%% (all %.0f%%), high %.0f%% (all %.0f%%); detached %s; seats on settlements %d" % (
            100 * br / max(1, b), 100 * er / max(1, e), 100 * bh / max(1, b), 100 * eh / max(1, e), world["detached"],
            sum(1 for p in provs if p["seat_settlement"] >= 0)))
        ops = {}
        for entry in log:
            ops[entry[0]] = ops.get(entry[0], 0) + 1
        print("  balance ops:", ops, " fails:", fails[:5])
        chars = {}
        for p in provs:
            chars[p["character"]] = chars.get(p["character"], 0) + 1
        print("  grounds:", chars)
        print("  names:", [p["name"] for p in provs if p["kingdom"] == "kingdom_0"])
        render(world, str(Path(__file__).resolve().parent / ("world_%d.png" % seed)))
