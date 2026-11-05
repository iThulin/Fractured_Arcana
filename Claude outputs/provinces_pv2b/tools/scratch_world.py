"""The scratch world ProvinceSaveAssert builds (48 by 32, two kingdoms, a ridge, a river,
a lake and two islands), for the Python mirrors. A copy of PV1's test_scratch.py, without its run."""
import sys
from pathlib import Path
HERE = Path(__file__).resolve().parent
for _p in (HERE, HERE.parent.parent / "provinces_pv1", Path("/home/claude/pv1py")):
    sys.path.insert(0, str(_p))
import province_port as pp  # noqa: E402

W, H = 48, 32

def scratch():
    tiles = []
    for y in range(H):
        for x in range(W):
            tiles.append({"terrain": "Grassland", "elev": 0.3, "kingdom": "kingdom_0" if x < 24 else "kingdom_1", "river": 0})
    def T(x, y):
        return tiles[y * W + x]
    for y in range(H):  # ridge down column 12
        T(12, y)["elev"] = 0.85; T(12, y)["terrain"] = "Hills"
    for y in range(H):  # river between columns 35 and 36
        q, r = pp.off_to_ax(35, y)
        for d in range(6):
            dq, dr = pp.AXIAL[d]
            nx, ny = pp.ax_to_off(q + dq, r + dr)
            if nx != 36 or not (0 <= ny < H):
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
    return {"W": W, "H": H, "tiles": tiles, "settlements": sets}
