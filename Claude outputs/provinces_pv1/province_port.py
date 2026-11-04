"""Line-for-line Python mirror of ProvinceBuilder.cs (slice PV1), used to test
the build's logic where no C# compiler is available. Same steps, same tie-breaks,
same constants. Worlds are plain dicts: W, H, tiles (list of dicts with terrain,
elev, kingdom, river), settlements (list of dicts with cx, cy, seat, city, tiles).
"""
import heapq, math

TARGET_PER_KINGDOM = 6
MIN_PER_KINGDOM = 3
MAX_PER_KINGDOM = 9
MIN_SEED_SPACING = 7
ELEVATION_WEIGHT = 12.0
LOWLAND_LEVEL = 0.50
RIVER_WEIGHT = 8.0
SPLIT_FACTOR = 2.2
MERGE_FACTOR = 0.35
MERGE_FLOOR = 20
MAX_PROVINCES = 255
TONE_COUNT = 4
NO_PROVINCE = 255
RANGE_LEVEL = 0.82   # MountainShaper.MountainFloor
RANGE_CORE_MIN = 8
RANGE_FLOOR = 0.62   # MountainShaper.HillsFloor: below it a range province pays RANGE_DESCENT more per tile
RANGE_DESCENT = 40.0
SQRT3 = 1.7320508075688772
WATER = {"Water", "Lake"}

AXIAL = [(1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)]

def off_to_ax(c, r):
    return c, r - (c - (c & 1)) // 2

def ax_to_off(q, r):
    return q, r + (q - (q & 1)) // 2

def hexdist(c1, r1, c2, r2):
    q1, a1 = off_to_ax(c1, r1); q2, a2 = off_to_ax(c2, r2)
    x1, y1, z1 = q1, a1, -q1 - a1
    x2, y2, z2 = q2, a2, -q2 - a2
    return (abs(x1 - x2) + abs(y1 - y2) + abs(z1 - z2)) // 2

def is_land(t):
    return t["terrain"] not in WATER

def cs_round(x):
    # .NET Math.Round(double) and Python round() both round half to even.
    return int(round(x))

def neighbor_table(w, h):
    nb = [-1] * (w * h * 6)
    for row in range(h):
        for col in range(w):
            q, r = off_to_ax(col, row)
            at = (row * w + col) * 6
            for d in range(6):
                dq, dr = AXIAL[d]
                nc, nr = ax_to_off(q + dq, r + dr)
                nb[at + d] = nr * w + nc if (0 <= nc < w and 0 <= nr < h) else -1
    return nb

def group_keys(world):
    seen = set()
    for t in world["tiles"]:
        if is_land(t):
            seen.add(t["kingdom"] or "")
    keys = sorted(seen, key=lambda k: (k == "", k))
    if len(keys) > 1 and "" in keys:
        keys.remove("")
    return keys

def adopt_ownerless(nb, group_of, ownerless):
    from collections import deque
    n = len(group_of)
    reach = list(group_of)
    q = deque(i for i in range(n) if group_of[i] >= 0)
    while q:
        cur = q.popleft()
        for d in range(6):
            nx = nb[cur * 6 + d]
            if nx >= 0 and reach[nx] < 0:
                reach[nx] = reach[cur]; q.append(nx)
    for t in ownerless:
        group_of[t] = reach[t]

def components(label, nb):
    n = len(label)
    run = [-1] * n
    size, lab = [], []
    for i in range(n):
        if label[i] < 0 or run[i] >= 0:
            continue
        rid = len(size); count = 0
        run[i] = rid
        stack = [i]
        while stack:
            cur = stack.pop()
            count += 1
            for d in range(6):
                nx = nb[cur * 6 + d]
                if nx >= 0 and run[nx] < 0 and label[nx] == label[i]:
                    run[nx] = rid
                    stack.append(nx)
        size.append(count); lab.append(label[i])
    return run, size, lab

def targets(keys, land_of):
    kingdom_land = 0.0; kingdoms = 0; total = 0
    for gi, k in enumerate(keys):
        total += land_of[gi]
        if k:
            kingdom_land += land_of[gi]; kingdoms += 1
    mean_k = kingdom_land / kingdoms if kingdoms > 0 else float(total)
    per = max(1.0, mean_k / TARGET_PER_KINGDOM)
    tg = []; s = 0
    for gi, k in enumerate(keys):
        if not k:
            t = max(1, cs_round(land_of[gi] / per))
        else:
            t = min(max(cs_round(TARGET_PER_KINGDOM * land_of[gi] / max(1.0, mean_k)), MIN_PER_KINGDOM), MAX_PER_KINGDOM)
        t = max(1, min(t, land_of[gi]))
        tg.append(t); s += t
    return tg, total / max(1, s)

def capital_settlements(world, keys, group_of):
    cap = [-1] * len(keys)
    w = world["W"]
    for s, st in enumerate(world["settlements"]):
        if not st["seat"]:
            continue
        gi = group_of[st["cy"] * w + st["cx"]]
        if gi >= 0 and keys[gi] and cap[gi] < 0:
            cap[gi] = s
    return cap

def seed_rank(st):
    return 0 if st["seat"] else (1 if st["city"] else 2)

def seed_key(world, s):
    st = world["settlements"][s]
    return (seed_rank(st), -len(st["tiles"]), s)

def seed_order(world, group_of, group):
    w = world["W"]
    order = [s for s, st in enumerate(world["settlements"]) if group_of[st["cy"] * w + st["cx"]] == group]
    order.sort(key=lambda s: seed_key(world, s))
    return order

def crowds(world, tile, seeds):
    w = world["W"]; x, y = tile % w, tile // w
    return any(hexdist(x, y, s % w, s // w) < MIN_SEED_SPACING for s in seeds)

def enter_cost(world, frm, to, d):
    c = 1.0 + ELEVATION_WEIGHT * max(0.0, world["tiles"][to]["elev"] - LOWLAND_LEVEL)
    if world["tiles"][frm]["river"] & (1 << d):
        c += RIVER_WEIGHT
    return c

def flood(world, nb, region, rid, sources, floors=None):
    n = len(region)
    cost = [math.inf] * n; owner = [-1] * n; done = [False] * n
    pq = []
    for s, t in enumerate(sources):
        if t < 0 or t >= n or region[t] != rid or owner[t] >= 0:
            continue
        cost[t] = 0.0; owner[t] = s
        heapq.heappush(pq, (0.0, t))
    while pq:
        _, cur = heapq.heappop(pq)
        if done[cur]:
            continue
        done[cur] = True
        for d in range(6):
            nx = nb[cur * 6 + d]
            if nx < 0 or done[nx] or region[nx] != rid:
                continue
            c = cost[cur] + enter_cost(world, cur, nx, d)
            if floors is not None and world["tiles"][nx]["elev"] < floors[owner[cur]]:
                c += RANGE_DESCENT
            if c < cost[nx]:
                cost[nx] = c; owner[nx] = owner[cur]
                heapq.heappush(pq, (c, nx))
    return cost, owner

def render_xy(col, row):
    return 1.5 * col, SQRT3 * (row + 0.5 * (col & 1))

def central_tile(world, tiles):
    if not tiles:
        return -1
    w = world["W"]
    sx = sy = 0.0
    for t in tiles:
        x, y = render_xy(t % w, t // w); sx += x; sy += y
    sx /= len(tiles); sy /= len(tiles)
    best = -1; bd = float("inf")
    for t in tiles:
        x, y = render_xy(t % w, t // w)
        d = (x - sx) ** 2 + (y - sy) ** 2
        if d < bd:
            bd = d; best = t
    return best

def big_ranges(world, nb, group_of, group, seeds):
    n = len(group_of)
    label = [0 if (group_of[i] == group and world["tiles"][i]["elev"] >= RANGE_LEVEL) else -1 for i in range(n)]
    run, run_size, _ = components(label, nb)
    cores = [[] for _ in run_size]
    for i in range(n):
        if run[i] < 0:
            continue
        if all(nb[i * 6 + d] >= 0 and run[nb[i * 6 + d]] == run[i] for d in range(6)):
            cores[run[i]].append(i)
    seeded = {run[t] for t in seeds if run[t] >= 0}
    found = [c for c in range(len(cores)) if len(cores[c]) >= RANGE_CORE_MIN and c not in seeded]
    found.sort(key=lambda c: (-len(cores[c]), c))
    return [(central_tile(world, cores[c]), [i for i in range(n) if run[i] == c]) for c in found]

def group_sources(drafts, group):
    sources, owners, floors = [], [], []
    for k, dr in enumerate(drafts):
        if dr["group"] != group:
            continue
        if dr.get("region"):
            for t in dr["region"]:
                sources.append(t); owners.append(k); floors.append(RANGE_FLOOR)
        else:
            sources.append(dr["seed"]); owners.append(k); floors.append(-math.inf)
    return sources, owners, floors

def seed_group(world, nb, group_of, comp, comp_size, comp_group, group, target, min_tiles, drafts):
    w = world["W"]; n = len(group_of)
    seeds = []
    sources = []
    floors = []
    order = seed_order(world, group_of, group)
    for s in order:
        if len(seeds) >= target or len(drafts) >= MAX_PROVINCES:
            break
        st = world["settlements"][s]
        tile = st["cy"] * w + st["cx"]
        if crowds(world, tile, seeds):
            continue
        seeds.append(tile); sources.append(tile); floors.append(-math.inf)
        drafts.append({"group": group, "seed": tile, "settlement": s, "alive": True})
    for tile, region in big_ranges(world, nb, group_of, group, seeds):
        if len(drafts) >= MAX_PROVINCES:
            break
        seeds.append(tile); sources.extend(region); floors.extend([RANGE_FLOOR] * len(region))
        drafts.append({"group": group, "seed": tile, "settlement": -1, "alive": True, "range": True, "region": region})
    seeded = set(comp[t] for t in seeds)
    pieces = [c for c in range(len(comp_size)) if comp_group[c] == group]
    pieces.sort(key=lambda c: (-comp_size[c], c))
    for c in pieces:
        if len(drafts) >= MAX_PROVINCES:
            break
        if c in seeded or (comp_size[c] < min_tiles and len(seeds) > 0):
            continue
        tile = -1; settlement = -1
        for s in order:
            st = world["settlements"][s]
            at = st["cy"] * w + st["cx"]
            if comp[at] == c:
                tile = at; settlement = s; break
        if tile < 0:
            tile = central_tile(world, [i for i in range(n) if comp[i] == c])
        if tile < 0:
            continue
        seeds.append(tile); sources.append(tile); floors.append(-math.inf); seeded.add(c)
        drafts.append({"group": group, "seed": tile, "settlement": settlement, "alive": True})
    while len(seeds) < target and len(drafts) < MAX_PROVINCES:
        cost, _ = flood(world, nb, group_of, group, sources, floors)
        far = -1; fc = -1.0
        for i in range(n):
            if group_of[i] != group or cost[i] == math.inf:
                continue
            if cost[i] > fc:
                fc = cost[i]; far = i
        if far < 0 or crowds(world, far, seeds):
            break
        seeds.append(far); sources.append(far); floors.append(-math.inf)
        drafts.append({"group": group, "seed": far, "settlement": -1, "alive": True})

def attach_unreached(world, group_of, comp, comp_count, prov):
    w = world["W"]; n = len(prov)
    reached = [False] * comp_count
    for i in range(n):
        if comp[i] >= 0 and prov[i] >= 0:
            reached[comp[i]] = True
    for c in range(comp_count):
        if reached[c]:
            continue
        piece = [i for i in range(n) if comp[i] == c]
        if not piece:
            continue
        group = group_of[piece[0]]
        best = -1; bd = 1 << 30
        for j in range(n):
            if group_of[j] != group or prov[j] < 0:
                continue
            jx, jy = j % w, j // w
            for t in piece:
                d = hexdist(t % w, t // w, jx, jy)
                if d < bd:
                    bd = d; best = j
        if best < 0:
            continue
        for t in piece:
            prov[t] = prov[best]


RELAX_ROUNDS = 2
SPLIT_ROUNDS = 4

def relax_group(world, nb, group_of, comp, group, drafts, prov):
    """Wild seeds (no settlement) move to the middle of their province's body and
    the group re-floods; settlement seeds stay put. RELAX_ROUNDS times."""
    w = world["W"]; n = len(prov)
    ids = [k for k in range(len(drafts)) if drafts[k]["group"] == group]
    if not ids:
        return
    for _ in range(RELAX_ROUNDS):
        moved = False
        for k in ids:
            if drafts[k]["settlement"] >= 0 or drafts[k].get("range"):
                continue
            seed = drafts[k]["seed"]
            body = [i for i in range(n) if prov[i] == k and comp[i] == comp[seed]]
            mid = central_tile(world, body)
            if mid >= 0 and mid != seed:
                drafts[k]["seed"] = mid; moved = True
        if not moved:
            return
        srcs, owners, flrs = group_sources(drafts, group)
        _, owner = flood(world, nb, group_of, group, srcs, flrs)
        for i in range(n):
            if group_of[i] == group:
                prov[i] = owners[owner[i]] if owner[i] >= 0 else -1

def axis_seeds(world, body):
    """The body tiles nearest the centroid minus and plus half a standard deviation
    along the body's long axis (as drawn)."""
    w = world["W"]
    pts = [render_xy(t % w, t // w) for t in body]
    cx = sum(p[0] for p in pts) / len(pts); cy = sum(p[1] for p in pts) / len(pts)
    sxx = sum((p[0] - cx) ** 2 for p in pts) / len(pts)
    syy = sum((p[1] - cy) ** 2 for p in pts) / len(pts)
    sxy = sum((p[0] - cx) * (p[1] - cy) for p in pts) / len(pts)
    half = (sxx - syy) / 2.0
    lam = (sxx + syy) / 2.0 + math.sqrt(half * half + sxy * sxy)
    if abs(sxy) > 1e-12:
        ux, uy = lam - syy, sxy
    elif sxx >= syy:
        ux, uy = 1.0, 0.0
    else:
        ux, uy = 0.0, 1.0
    ln = math.hypot(ux, uy); ux /= ln; uy /= ln
    sd = math.sqrt(max(0.0, lam))
    def nearest(tx, ty):
        best = -1; bd = float("inf")
        for t, (x, y) in zip(body, pts):
            d = (x - tx) ** 2 + (y - ty) ** 2
            if d < bd:
                bd = d; best = t
        return best
    return nearest(cx - 0.5 * sd * ux, cy - 0.5 * sd * uy), nearest(cx + 0.5 * sd * ux, cy + 0.5 * sd * uy)

def sizes(prov, count):
    s = [0] * count
    for p in prov:
        if p >= 0:
            s[p] += 1
    return s

def split_pass(world, nb, prov, drafts, min_tiles, max_tiles, log):
    n = len(prov); refused = set(); guard = 0
    while guard < 64 and len(drafts) < MAX_PROVINCES:
        guard += 1
        size = sizes(prov, len(drafts))
        p = -1
        for k in range(len(drafts)):
            if not drafts[k]["alive"] or k in refused or size[k] <= max_tiles:
                continue
            if p < 0 or size[k] > size[p]:
                p = k
        if p < 0:
            return
        seed0 = drafts[p]["seed"]
        cost, _ = flood(world, nb, prov, p, [seed0])
        body = [i for i in range(n) if prov[i] == p and cost[i] != math.inf]
        if len(body) < 2 * min_tiles:
            refused.add(p); continue
        sa, sb = axis_seeds(world, body)
        if sa < 0 or sb < 0 or sa == sb:
            refused.add(p); continue
        for _ in range(SPLIT_ROUNDS):
            _, owner = flood(world, nb, prov, p, [sa, sb])
            na = central_tile(world, [i for i in body if owner[i] == 0])
            nb2 = central_tile(world, [i for i in body if owner[i] == 1])
            if na < 0 or nb2 < 0 or na == nb2 or (na == sa and nb2 == sb):
                break
            sa, sb = na, nb2
        _, owner = flood(world, nb, prov, p, [sa, sb])
        keep = owner[seed0]
        if keep < 0:
            refused.add(p); continue
        other = 1 - keep
        size_other = sum(1 for i in range(n) if prov[i] == p and owner[i] == other)
        size_keep = size[p] - size_other
        if size_keep < min_tiles or size_other < min_tiles:
            refused.add(p); log.append(("split refused", p, size_keep, size_other)); continue
        q = len(drafts)
        drafts.append({"group": drafts[p]["group"], "seed": sb if other == 1 else sa, "settlement": -1, "alive": True,
                       "range": drafts[p].get("range", False)})
        for i in range(n):
            if prov[i] == p and owner[i] == other:
                prov[i] = q
        log.append(("split", p, q, size_keep, size_other))

def nearest_province(world, prov, drafts, p):
    w = world["W"]; n = len(prov); group = drafts[p]["group"]
    own = [i for i in range(n) if prov[i] == p]
    best = -1; bd = 1 << 30
    for j in range(n):
        q = prov[j]
        if q < 0 or q == p or drafts[q]["group"] != group:
            continue
        jx, jy = j % w, j // w
        for t in own:
            d = hexdist(t % w, t // w, jx, jy)
            if d < bd or (d == bd and q < best):
                bd = d; best = q
    return best

def merge_pass(world, nb, prov, drafts, capital_tile, min_tiles, log):
    n = len(prov); refused = set()
    for guard in range(512):
        size = sizes(prov, len(drafts))
        p = -1
        for k in range(len(drafts)):
            if not drafts[k]["alive"] or k in refused or size[k] >= min_tiles:
                continue
            cap = capital_tile[drafts[k]["group"]]
            if cap >= 0 and prov[cap] == k:
                continue
            if p < 0 or size[k] < size[p]:
                p = k
        if p < 0:
            return
        group = drafts[p]["group"]
        shared = {}
        for i in range(n):
            if prov[i] != p:
                continue
            for d in range(6):
                j = nb[i * 6 + d]
                if j < 0:
                    continue
                o = prov[j]
                if o < 0 or o == p or drafts[o]["group"] != group:
                    continue
                shared[o] = shared.get(o, 0) + 1
        q = -1
        for key, val in shared.items():
            if q < 0:
                q = key; continue
            best = shared[q]
            if val > best or (val == best and (size[key] < size[q] or (size[key] == size[q] and key < q))):
                q = key
        how = "border"
        if q < 0:
            q = nearest_province(world, prov, drafts, p); how = "nearest"
        if q < 0:
            refused.add(p); continue
        for i in range(n):
            if prov[i] == p:
                prov[i] = q
        drafts[p]["alive"] = False
        log.append(("merge", p, q, size[p], how))

CHAR = {"Forest": "Woodland", "Hills": "Highland", "Mountain": "Mountain", "Swamp": "Wetland",
        "Marsh": "Wetland", "Desert": "Arid", "Volcanic": "Volcanic", "Tundra": "Frozen",
        "Snow": "Frozen", "ArcaneGround": "Arcane", "Ruins": "Ruined"}
CHARS = ["Lowland", "Woodland", "Highland", "Mountain", "Wetland", "Arid", "Volcanic", "Frozen", "Arcane", "Ruined"]
NOUN = {"Woodland": "Weald", "Highland": "Downs", "Mountain": "Peaks", "Wetland": "Fens", "Arid": "Wastes",
        "Volcanic": "Ashlands", "Frozen": "Rime", "Arcane": "Leylands", "Ruined": "Barrows", "Lowland": "Fields"}
COMPASS = ["East", "Southeast", "South", "Southwest", "West", "Northwest", "North", "Northeast"]

def character_from(hist):
    total = sum(hist)
    low = hist[0]
    if total == 0 or low * 2 >= total:
        return "Lowland"
    best = -1
    for c in range(len(CHARS)):
        if c == 0:
            continue
        if best < 0 or hist[c] > hist[best]:
            best = c
    return CHARS[best] if best >= 0 and hist[best] > 0 else "Lowland"

def range_character(hist):
    best = "Mountain"
    for c in ("Frozen", "Volcanic"):
        if hist[CHARS.index(c)] > hist[CHARS.index(best)]:
            best = c
    return best

def compass(fx, fy, tx, ty):
    ax, ay = render_xy(fx, fy); bx, by = render_xy(tx, ty)
    dx, dy = bx - ax, by - ay
    if abs(dx) < 1e-9 and abs(dy) < 1e-9:
        return "Central"
    sector = int(round(math.atan2(dy, dx) / (math.pi / 4.0)))
    return COMPASS[((sector % 8) + 8) % 8]

def build(world, log=None):
    if log is None:
        log = []
    w, h = world["W"], world["H"]; n = w * h
    nb = neighbor_table(w, h)
    keys = group_keys(world)
    gidx = {k: i for i, k in enumerate(keys)}
    group_of = [-1] * n; land_of = [0] * len(keys); ownerless = []
    for i, t in enumerate(world["tiles"]):
        if is_land(t):
            g = gidx.get(t["kingdom"] or "", -1)
            if g >= 0: group_of[i] = g
            else: ownerless.append(i)
    if ownerless:
        adopt_ownerless(nb, group_of, ownerless)
    for i in range(n):
        if group_of[i] >= 0: land_of[group_of[i]] += 1
    comp, comp_size, comp_group = components(group_of, nb)
    target, mean_p = targets(keys, land_of)
    min_tiles = max(MERGE_FLOOR, cs_round(MERGE_FACTOR * mean_p))
    max_tiles = max(2 * min_tiles, cs_round(SPLIT_FACTOR * mean_p))
    cap_set = capital_settlements(world, keys, group_of)
    cap_tile = [(world["settlements"][c]["cy"] * w + world["settlements"][c]["cx"]) if c >= 0 else -1 for c in cap_set]
    drafts = []
    for g in range(len(keys)):
        seed_group(world, nb, group_of, comp, comp_size, comp_group, g, target[g], min_tiles, drafts)
    prov = [-1] * n
    for g in range(len(keys)):
        srcs, owners, flrs = group_sources(drafts, g)
        if not srcs:
            continue
        _, owner = flood(world, nb, group_of, g, srcs, flrs)
        for i in range(n):
            if group_of[i] == g and owner[i] >= 0:
                prov[i] = owners[owner[i]]
    if RELAX_ROUNDS > 0:
        for g in range(len(keys)):
            relax_group(world, nb, group_of, comp, g, drafts, prov)
    attach_unreached(world, group_of, comp, len(comp_size), prov)
    split_pass(world, nb, prov, drafts, min_tiles, max_tiles, log)
    merge_pass(world, nb, prov, drafts, cap_tile, min_tiles, log)
    split_pass(world, nb, prov, drafts, min_tiles, max_tiles, log)
    size = sizes(prov, len(drafts))
    order = []
    for g in range(len(keys)):
        cd = prov[cap_tile[g]] if cap_tile[g] >= 0 else -1
        if cd >= 0:
            order.append(cd)
        for k in range(len(drafts)):
            if k != cd and drafts[k]["alive"] and drafts[k]["group"] == g and size[k] > 0:
                order.append(k)
    remap = [-1] * len(drafts)
    for at, k in enumerate(order):
        remap[k] = at
    pmap = [NO_PROVINCE if prov[i] < 0 or remap[prov[i]] < 0 else remap[prov[i]] for i in range(n)]
    provinces = [{"index": at, "kingdom": keys[drafts[k]["group"]], "tiles": 0, "settlements": [],
                  "capital": False, "seat_settlement": -1} for at, k in enumerate(order)]
    seed_of = [drafts[k]["seed"] for k in order]
    range_of = [bool(drafts[k].get("range")) for k in order]
    hist = [[0] * len(CHARS) for _ in provinces]
    for i in range(n):
        p = pmap[i]
        if p == NO_PROVINCE:
            continue
        provinces[p]["tiles"] += 1
        hist[p][CHARS.index(CHAR.get(world["tiles"][i]["terrain"], "Lowland"))] += 1
    for p, pr in enumerate(provinces):
        pr["character"] = range_character(hist[p]) if range_of[p] else character_from(hist[p])
        pr["range"] = range_of[p]
    for s, st in enumerate(world["settlements"]):
        p = pmap[st["cy"] * w + st["cx"]]
        if p != NO_PROVINCE:
            provinces[p]["settlements"].append(s)
    for p, pr in enumerate(provinces):
        main_piece = comp[seed_of[p]]
        seat = -1
        g = gidx.get(pr["kingdom"], -1)
        if g >= 0:
            cs = cap_set[g]
            if cs >= 0 and cs in pr["settlements"]:
                seat = cs; pr["capital"] = True
        if seat < 0:
            for s in pr["settlements"]:
                st = world["settlements"][s]
                if comp[st["cy"] * w + st["cx"]] != main_piece:
                    continue
                if seat < 0 or seed_key(world, s) < seed_key(world, seat):
                    seat = s
        if seat >= 0:
            pr["seat_settlement"] = seat
            pr["seat"] = (world["settlements"][seat]["cx"], world["settlements"][seat]["cy"])
        else:
            mid = central_tile(world, [i for i in range(n) if pmap[i] == p and comp[i] == main_piece])
            if mid < 0:
                mid = seed_of[p]
            pr["seat"] = (mid % w, mid // w)
    adj = [set() for _ in provinces]
    for i in range(n):
        a = pmap[i]
        if a == NO_PROVINCE:
            continue
        for d in range(6):
            j = nb[i * 6 + d]
            if j < 0:
                continue
            b = pmap[j]
            if b != NO_PROVINCE and b != a:
                adj[a].add(b)
    for p, pr in enumerate(provinces):
        pr["neighbors"] = sorted(adj[p])
    for p, pr in enumerate(provinces):
        used = [0] * TONE_COUNT
        for q in pr["neighbors"]:
            if q < p and provinces[q]["kingdom"] == pr["kingdom"]:
                used[provinces[q]["tone"]] += 1
        tone = next((t for t in range(TONE_COUNT) if used[t] == 0), -1)
        if tone < 0:
            tone = min(range(TONE_COUNT), key=lambda t: (used[t], t))
        pr["tone"] = tone
    # ids and names
    ordinal = {}
    for pr in provinces:
        place = ordinal.get(pr["kingdom"], 0); ordinal[pr["kingdom"]] = place + 1
        pr["id"] = "province:%s:%d" % (pr["kingdom"] or "wild", place)
    origin = {}
    for pr in provinces:
        if pr["capital"] and pr["kingdom"] not in origin:
            origin[pr["kingdom"]] = pr["seat"]
    base = []; dist = []
    for pr in provinces:
        ox, oy = origin.get(pr["kingdom"], (w // 2, h // 2))
        dist.append(hexdist(ox, oy, pr["seat"][0], pr["seat"][1]))
        base.append("Crownland" if pr["capital"] else "%s %s" % (compass(ox, oy, *pr["seat"]), NOUN[pr["character"]]))
    same = {}
    for i, pr in enumerate(provinces):
        same.setdefault(pr["kingdom"] + "|" + base[i], []).append(i)
    for holders in same.values():
        holders.sort(key=lambda i: (dist[i], i))
        for k, i in enumerate(holders):
            b = base[i]
            provinces[i]["name"] = b if k == 0 else ("Far " + b if k == 1 else ("Outer " + b if k == 2 else "%s %d" % (b, k + 1)))
    world["provinces"] = provinces
    world["pmap"] = pmap
    world["build_info"] = {"mean": mean_p, "min_tiles": min_tiles, "max_tiles": max_tiles, "targets": dict(zip(keys, target))}
    return world

def validate(world):
    fails = []
    w = world["W"]; n = len(world["tiles"]); pmap = world["pmap"]; provs = world["provinces"]; count = len(provs)
    tiles_in = [0] * count
    for i in range(n):
        p = pmap[i]; land = is_land(world["tiles"][i]); kingdom = world["tiles"][i]["kingdom"] or ""
        if p == NO_PROVINCE:
            if land and kingdom: fails.append("land tile (%d,%d) in no province" % (i % w, i // w))
            continue
        if not land:
            fails.append("water tile in province"); continue
        tiles_in[p] += 1
        if kingdom and provs[p]["kingdom"] != kingdom:
            fails.append("kingdom mismatch at (%d,%d)" % (i % w, i // w))
    nb = neighbor_table(w, world["H"])
    adj = [set() for _ in provs]
    for i in range(n):
        a = pmap[i]
        if a == NO_PROVINCE: continue
        for d in range(6):
            j = nb[i * 6 + d]
            if j >= 0 and pmap[j] != NO_PROVINCE and pmap[j] != a:
                adj[a].add(pmap[j])
    ids = set(); names = set(); caps = set()
    for p, pr in enumerate(provs):
        if pr["index"] != p: fails.append("index")
        if pr["tiles"] != tiles_in[p] or pr["tiles"] <= 0: fails.append("tilecount %s" % pr["id"])
        if pr["id"] in ids: fails.append("dup id")
        ids.add(pr["id"])
        key = pr["kingdom"] + "|" + pr["name"]
        if key in names: fails.append("dup name %s" % key)
        names.add(key)
        sx, sy = pr["seat"]
        if pmap[sy * w + sx] != p: fails.append("seat outside %s" % pr["id"])
        if set(pr["neighbors"]) != adj[p]: fails.append("neighbors %s" % pr["id"])
        if pr["capital"]:
            if pr["kingdom"] in caps: fails.append("2 capitals")
            caps.add(pr["kingdom"])
    for s, st in enumerate(world["settlements"]):
        if st["seat"]:
            p = pmap[st["cy"] * w + st["cx"]]
            if p == NO_PROVINCE or not provs[p]["capital"]:
                fails.append("seat city %d not in capital province" % s)
    # pieces
    ridx = {}
    realm_of = []
    for p in pmap:
        if p == NO_PROVINCE: realm_of.append(-1); continue
        realm_of.append(ridx.setdefault(provs[p]["kingdom"], len(ridx)))
    island, isize, _ = components(realm_of, nb)
    provof = [-1 if p == NO_PROVINCE else p for p in pmap]
    piece, psize, pprov = components(provof, nb)
    first = {}
    for i in range(n):
        if piece[i] >= 0 and piece[i] not in first:
            first[piece[i]] = i
    main = {}
    for c in range(len(psize)):
        p = pprov[c]
        if p not in main or psize[c] > psize[main[p]]:
            main[p] = c
    detached = 0; dtiles = 0
    for c in range(len(psize)):
        p = pprov[c]
        if c == main[p]: continue
        detached += 1; dtiles += psize[c]
        at = first[c]
        if isize[island[at]] != psize[c]:
            fails.append("%s split: piece of %d not a whole island" % (provs[p]["id"], psize[c]))
    world["detached"] = (detached, dtiles)
    return fails

def border_stats(world):
    w = world["W"]; n = len(world["tiles"]); pmap = world["pmap"]
    e = er = eh = b = br = bh = 0
    for i in range(n):
        if pmap[i] == NO_PROVINCE: continue
        q, r = off_to_ax(i % w, i // w)
        for d in range(3):
            dq, dr = AXIAL[d]
            nc, nr = ax_to_off(q + dq, r + dr)
            if not (0 <= nc < w and 0 <= nr < world["H"]): continue
            j = nr * w + nc
            if pmap[j] == NO_PROVINCE or world["tiles"][i]["kingdom"] != world["tiles"][j]["kingdom"]: continue
            river = bool(world["tiles"][i]["river"] & (1 << d))
            high = world["tiles"][i]["elev"] > LOWLAND_LEVEL and world["tiles"][j]["elev"] > LOWLAND_LEVEL
            e += 1; er += river; eh += high
            if pmap[i] != pmap[j]:
                b += 1; br += river; bh += high
    return e, er, eh, b, br, bh
