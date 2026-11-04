"""ProvinceSaveAssert's influence checks (AssertInfluenceRules and
AssertInfluenceWithoutProvinces), step for step, on the Python mirrors, so the
assert's expectations are right before Godot runs them."""
import math, sys
from pathlib import Path
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import influence_port as ip
pp = ip.pp

# The scratch world ProvinceSaveAssert builds (copied from PV1's test_scratch.py).
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



WEST, EAST = "kingdom_0", "kingdom_1"
fails = []


def check(name, cond):
    print(("  ok   " if cond else "  FAIL ") + name)
    if not cond:
        fails.append(name)
    return cond


def influence_cycle(build):
    world = scratch()
    if build:
        pp.build(world, [])
        ip.ensure_fields(world)
    else:
        world["provinces"] = []
        world["pmap"] = None
    return {
        "world": world,
        "kingdoms": {WEST: {"pi": 25, "pid": 0, "name": "Westmarch", "faction": ""},
                     EAST: {"pi": 0, "pid": 0, "name": "Eastreach", "faction": ""}},
        "castle": (6, 15),
        "reports": [],
    }


def derived_matches(cycle, skip_waiting=False):
    for kid, k in cycle["kingdoms"].items():
        if skip_waiting and k["pi"] != k["pid"]:
            continue
        s = 0.0; tiles = 0.0
        for pr in cycle["world"]["provinces"]:
            if pr["kingdom"] != kid:
                continue
            s += pr["influence"] * pr["tiles"]; tiles += pr["tiles"]
        if tiles <= 0:
            continue
        mean = int(math.floor(s / tiles + 0.5))
        if k["pi"] != mean or k["pid"] != mean:
            return False
    return True


def changed(provinces, before):
    return sum(1 for i, pr in enumerate(provinces) if pr["influence"] != before[i])


def rules():
    print("AssertInfluenceRules")
    cycle = influence_cycle(True)
    world = cycle["world"]
    west, east = cycle["kingdoms"][WEST], cycle["kingdoms"][EAST]
    provinces = world["provinces"]
    capital = ip.province_index_at(world, 6, 15)
    east_capital = ip.province_index_at(world, 30, 15)
    check("both seats stand in provinces", capital >= 0 and east_capital >= 0)

    check("the first sync reports a change", ip.sync(cycle))
    check("every west province starts at 25 and every east one at 0",
          all(p["influence"] == (25 if p["kingdom"] == WEST else 0) for p in provinces))
    check("each kingdom's value is derived (25 and 0)",
          west["pi"] == 25 and west["pid"] == 25 and east["pi"] == 0 and east["pid"] == 0)
    check("nothing is held at the start", not any(p["held"] for p in provinces))
    check("a second sync changes nothing", not ip.sync(cycle))

    near = set(provinces[capital]["neighbors"])
    before = [p["influence"] for p in provinces]
    news = ip.tick(cycle)
    check("the castle's province gains 8 (now %d)" % provinces[capital]["influence"],
          provinces[capital]["influence"] == before[capital] + ip.PRESENCE_GAIN)
    rest = True
    for i in range(len(provinces)):
        if i == capital:
            continue
        expected = before[i] + ip.PRESENCE_NEIGHBOR_GAIN if i in near else max(0, before[i] - ip.DECAY)
        rest &= provinces[i]["influence"] == expected
    check("each province beside it gains 3, and every other loses 2, down to 0", rest)
    check("a lunation with no change of hold has no news", len(news) == 0)
    check("each kingdom's value is the tile-weighted mean after a lunation", derived_matches(cycle))
    print("    after one lunation:", [p["influence"] for p in provinces], "west", west["pi"])

    for _ in range(30):
        ip.tick(cycle)
    check("thirty more lunations leave the castle's province at 40 (now %d)" % provinces[capital]["influence"],
          provinces[capital]["influence"] == ip.PASSIVE_CAP)
    check("every province beside it stops at 40 too", all(provinces[i]["influence"] == ip.PASSIVE_CAP for i in near))
    check("parking the castle without deeds holds nothing", not any(p["held"] for p in provinces))

    reports = len(cycle["reports"])
    held_news = ip.add(cycle, 6, 15, 20)
    check("a deed of 20 at the seat takes the castle's province to 60 and holds it",
          provinces[capital]["influence"] == 60 and provinces[capital]["held"])
    check("the hold is news, returned and in the frontier reports",
          len(held_news) > 0 and len(cycle["reports"]) == reports + 1)
    print("    news:", held_news)
    check("HoldsAny says the west and not the east", ip.holds_any(world, WEST) and not ip.holds_any(world, EAST))
    check("each kingdom's value is the tile-weighted mean after a deed", derived_matches(cycle))

    news = ip.tick(cycle)
    check("beside the castle and a held province, a province still stops at 40",
          all(provinces[i]["influence"] == ip.PASSIVE_CAP for i in near))
    check("the held province under the castle neither gains nor fades",
          provinces[capital]["influence"] == 60 and provinces[capital]["held"] and len(news) == 0)

    cycle["castle"] = (-1, -1)
    for _ in range(5):
        ip.tick(cycle)
    check("five lunations with the castle gone: 50 and still held (now %d)" % provinces[capital]["influence"],
          provinces[capital]["influence"] == 50 and provinces[capital]["held"])
    news = ip.tick(cycle)
    check("the sixth: 48, lost, and the loss is news",
          provinces[capital]["influence"] == 48 and not provinces[capital]["held"] and len(news) == 1)
    print("    news:", news)

    before = [p["influence"] for p in provinces]
    west["pi"] += 15
    ip.sync(cycle)
    spread = all(p["influence"] == (min(100, before[i] + 15) if p["kingdom"] == WEST else before[i])
                 for i, p in enumerate(provinces))
    check("a write of +15 to the west reaches every west province and no east one", spread)
    check("and each kingdom's value is derived again", derived_matches(cycle))

    reports = len(cycle["reports"])
    ip.set_kingdom(cycle, WEST, 100)
    check("a seizure: every west province 100 and held, the kingdom 100",
          all(p["influence"] == 100 and p["held"] for p in provinces if p["kingdom"] == WEST) and west["pi"] == 100)
    ip.set_kingdom(cycle, WEST, 0)
    check("a fall: every west province 0 and not held, the kingdom 0",
          all(p["influence"] == 0 and not p["held"] for p in provinces if p["kingdom"] == WEST) and west["pi"] == 0)
    check("a seizure or a fall adds no news of its own", len(cycle["reports"]) == reports)

    before = [p["influence"] for p in provinces]
    ip.add_near(cycle, EAST, 6, 15, 15)
    check("a focus with no east province within reach counts for the east capital",
          changed(provinces, before) == 1 and provinces[east_capital]["influence"] == min(100, before[east_capital] + 15))
    before = [p["influence"] for p in provinces]
    ip.add_near(cycle, EAST, 23, 5, 7)
    gained = -1
    for i, p in enumerate(provinces):
        if p["influence"] != before[i]:
            gained = i
    check("a focus on the west side of the border counts for the east province across it",
          changed(provinces, before) == 1 and gained >= 0 and provinces[gained]["kingdom"] == EAST
          and gained in (ip.province_index_at(world, 24, 5), ip.province_index_at(world, 24, 6))
          and provinces[gained]["influence"] == min(100, before[gained] + 7))
    print("    gained:", gained, provinces[gained]["name"], "| (24,5) in", ip.province_index_at(world, 24, 5),
          "(24,6) in", ip.province_index_at(world, 24, 6), "| east capital", east_capital)

    ip.set_kingdom(cycle, EAST, 70)
    east["faction"] = ip.GUILD
    ip.tick(cycle)
    check("the provinces of a kingdom the guild rules do not decay",
          all(p["influence"] == 70 and p["held"] for p in provinces if p["kingdom"] == EAST))
    east["faction"] = ""

    check("water reads 0", ip.influence_at(world, 31, 5) == 0)
    check("the place card's line, held and not",
          "holds" in ip.standing_line(provinces[east_capital]) and "held at" in ip.standing_line(provinces[capital]))
    print("    lines:", ip.standing_line(provinces[east_capital]), "|", ip.standing_line(provinces[capital]))


def without():
    print("AssertInfluenceWithoutProvinces")
    cycle = influence_cycle(False)
    west = cycle["kingdoms"][WEST]
    check("a world without provinces is not active", not ip.active(cycle))
    check("sync and the lunation do nothing there",
          not ip.sync(cycle) and len(ip.tick(cycle)) == 0 and west["pi"] == 25)
    ip.add(cycle, 6, 15, 20)
    check("a deed raises the tile's kingdom (45)", west["pi"] == 45)
    ip.add_near(cycle, WEST, -1, -1, 10)
    check("a front's deed raises the kingdom (55)", west["pi"] == 55)
    ip.set_kingdom(cycle, WEST, 0)
    check("a fall sets the kingdom (0)", west["pi"] == 0)


rules()
without()
print("RESULT:", "ALL PASSED" if not fails else "%d FAILED" % len(fails))
