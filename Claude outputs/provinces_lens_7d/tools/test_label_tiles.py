"""WorldAtlas3D.ProvinceLabelTile (docs/provinces_v1_7d_lenses.md), line for line in
Python, checked against an independent count on the assert's scratch world and on
stand-in worlds: a tile's depth from the breadth-first walk must equal one less than
the hex distance to the nearest place outside its region, found by searching rings
outward. Also runs ProvinceSaveAssert.AssertLabelTiles' two passes (all painted, then
only x < 18 painted) so the assert's expectations hold before Godot runs them."""
import random
import sys
from collections import deque
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[1] / "provinces_pv1"))
import province_port as pp  # noqa: E402

SQ3 = pp.SQRT3


def drawn(col, row):
    return col * 1.5, row * SQ3 + (SQ3 * 0.5 if (col & 1) == 1 else 0.0)


def settlement_tiles(world):
    out = set()
    W = world["W"]
    for s in world["settlements"]:
        for (x, y) in s["tiles"]:
            if 0 <= x < W and 0 <= y < world["H"]:
                out.add(y * W + x)
    return out


def label_tile(world, p, painted, nb, settle):
    """The C# ProvinceLabelTile. painted: None for revealAll, else a set of tile indices."""
    W, H, pm = world["W"], world["H"], world["pmap"]
    region = [i for i in range(W * H) if pm[i] == p and (painted is None or i in painted)]
    if not region:
        return -1, {}
    inr = set(region)
    depth = {}
    q = deque()
    for i in region:
        edge = any(nb[i * 6 + d] < 0 or nb[i * 6 + d] not in inr for d in range(6))
        if edge:
            depth[i] = 0
            q.append(i)
        else:
            depth[i] = -1
    while q:
        i = q.popleft()
        for d in range(6):
            n = nb[i * 6 + d]
            if n >= 0 and n in inr and depth[n] < 0:
                depth[n] = depth[i] + 1
                q.append(n)
    cx = sum(drawn(i % W, i // W)[0] for i in region) / len(region)
    cz = sum(drawn(i % W, i // W)[1] for i in region) / len(region)
    any_open = any(i not in settle for i in region)
    best, best_depth, best_dist = -1, -10 ** 9, float("inf")
    for i in region:
        if any_open and i in settle:
            continue
        x, z = drawn(i % W, i // W)
        dist = (x - cx) ** 2 + (z - cz) ** 2
        if depth[i] > best_depth or (depth[i] == best_depth and dist < best_dist):
            best, best_depth, best_dist = i, depth[i], dist
    return best, depth


def ring(col, row, k):
    """Offset positions at hex distance k from (col, row), unbounded."""
    q0, r0 = pp.off_to_ax(col, row)
    out = []
    q, r = q0 + pp.AXIAL[4][0] * k, r0 + pp.AXIAL[4][1] * k
    for side in range(6):
        for _ in range(k):
            out.append(pp.ax_to_off(q, r))
            q, r = q + pp.AXIAL[side][0], r + pp.AXIAL[side][1]
    return out


def depth_by_rings(world, inside, i):
    W, H = world["W"], world["H"]
    col, row = i % W, i // W
    k = 1
    while True:
        for (x, y) in ring(col, row, k):
            if not (0 <= x < W and 0 <= y < H and inside(x, y)):
                return k - 1
        k += 1


def check_world(world, name, paint_sets):
    W, H, pm = world["W"], world["H"], world["pmap"]
    nb = pp.neighbor_table(W, H)
    settle = settlement_tiles(world)
    fails = 0
    labels = 0
    for painted in paint_sets:
        for p in range(len(world["provinces"])):
            def inside(x, y, p=p, painted=painted):
                j = y * W + x
                return pm[j] == p and (painted is None or j in painted)
            tile, depth = label_tile(world, p, painted, nb, settle)
            region = list(depth.keys())
            if not region:
                if tile != -1:
                    fails += 1
                continue
            labels += 1
            for i in region:  # the walk against the rings, every tile
                if depth[i] != depth_by_rings(world, inside, i):
                    fails += 1
                    print("  depth mismatch", name, p, i, depth[i], depth_by_rings(world, inside, i))
                    break
            any_open = any(i not in settle for i in region)
            cands = [i for i in region if not any_open or i not in settle]
            deepest = max(depth_by_rings(world, inside, i) for i in cands)
            ok = (tile in depth and (not any_open or tile not in settle)
                  and depth_by_rings(world, inside, tile) == deepest)
            if not ok:
                fails += 1
                print("  label fails", name, p, tile)
    print("%-28s %3d labels checked, %d failures" % (name, labels, fails))
    return fails


# The scratch world ProvinceSaveAssert builds (copied from PV1's test_scratch.py).
SW, SH = 48, 32

def scratch():
    tiles = []
    for y in range(SH):
        for x in range(SW):
            tiles.append({"terrain": "Grassland", "elev": 0.3, "kingdom": "kingdom_0" if x < 24 else "kingdom_1", "river": 0})
    def T(x, y):
        return tiles[y * SW + x]
    for y in range(SH):  # ridge down column 12
        T(12, y)["elev"] = 0.85; T(12, y)["terrain"] = "Hills"
    for y in range(SH):  # river between columns 35 and 36
        q, r = pp.off_to_ax(35, y)
        for d in range(6):
            dq, dr = pp.AXIAL[d]
            nx, ny = pp.ax_to_off(q + dq, r + dr)
            if nx != 36 or not (0 <= ny < SH):
                continue
            T(35, y)["river"] |= 1 << d
            T(nx, ny)["river"] |= 1 << ((d + 3) % 6)
    for y in range(0, 11):  # woods north of the east towns
        for x in range(36, 48):
            T(x, y)["terrain"] = "Forest"
    for y in range(4, 7):  # a lake
        for x in range(30, 33):
            T(x, y)["terrain"] = "Lake"; T(x, y)["kingdom"] = ""
    def island(x0, x1, y0, y1):
        for y in range(y0 - 1, y1 + 2):
            for x in range(x0 - 1, x1 + 2):
                if x0 <= x <= x1 and y0 <= y <= y1:
                    continue
                T(x, y)["terrain"] = "Water"; T(x, y)["kingdom"] = ""
    island(2, 4, 27, 29)      # small: 9 tiles
    island(39, 46, 24, 30)    # large: 56 tiles
    sets = []
    def add(x, y, kingdom, city, seat):
        foot = [(x, y)]
        if city:
            q, r = pp.off_to_ax(x, y)
            for dq, dr in pp.AXIAL:
                foot.append(pp.ax_to_off(q + dq, r + dr))
        sets.append({"cx": x, "cy": y, "seat": seat, "city": city, "tiles": foot, "kingdom": kingdom})
    add(6, 15, "kingdom_0", True, True)
    add(30, 15, "kingdom_1", True, True)
    add(18, 5, "kingdom_0", False, False)
    add(18, 25, "kingdom_0", False, False)
    add(3, 4, "kingdom_0", False, False)
    add(42, 6, "kingdom_1", False, False)
    add(43, 18, "kingdom_1", False, False)
    return {"W": SW, "H": SH, "tiles": tiles, "settlements": sets}


def main():
    total = 0
    sw = scratch()
    pp.build(sw)
    W = sw["W"]
    west = {i for i in range(sw["W"] * sw["H"]) if i % W < 18}
    total += check_world(sw, "scratch (all, then x < 18)", [None, west])
    import test_world as tw
    rnd = random.Random(7)
    for seed in (11, 23, 37, 106):
        w = tw.make_world(seed)
        pp.build(w)
        n = w["W"] * w["H"]
        # a painted blob around a random centre, as exploration leaves it
        cx, cy = rnd.randrange(w["W"]), rnd.randrange(w["H"])
        blob = {i for i in range(n) if pp.hexdist(i % w["W"], i // w["W"], cx, cy) < 40}
        total += check_world(w, "stand-in seed %d" % seed, [None, blob])
    print("ALL HOLD" if total == 0 else "FAILURES: %d" % total)


if __name__ == "__main__":
    main()
