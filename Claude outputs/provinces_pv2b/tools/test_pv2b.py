"""ProvinceSaveAssert's PV2b-1 checks (AssertEstates, AssertCourtRules, AssertDeeds'
arithmetic, AssertBetrayal's Regard), step for step, on the Python mirrors, and a randomized
check that the forecast equals the lunation with courts, estates, floors, contact, bands and
seizing fronts in play."""
import random
import sys

from pathlib import Path

HERE = Path(__file__).resolve().parent
for _p in (Path("/home/claude/pv2b"), HERE.parent.parent / "provinces_pv1", Path("/home/claude/pv1py"), HERE):
    sys.path.insert(0, str(_p))
import province_port as pp  # noqa: E402
import influence_port as ip  # noqa: E402
from scratch_world import scratch  # noqa: E402

WEST, EAST, LOST = "kingdom_0", "kingdom_1", "kingdom_9"
OFFICES = ["Chancellor", "Marshal", "Spymaster", "Court Wizard", "Steward", "Favorite"]
fails = []


def check(name, cond):
    print(("  ok   " if cond else "  FAIL ") + name)
    if not cond:
        fails.append(name)
    return cond


def influence_cycle():
    world = scratch()
    pp.build(world, [])
    ip.ensure_fields(world)
    return {
        "world": world,
        "kingdoms": {WEST: {"pi": 25, "pid": 0, "name": "Westmarch", "faction": ""},
                     EAST: {"pi": 0, "pid": 0, "name": "Eastreach", "faction": ""}},
        "castle": (6, 15),
        "reports": [],
    }


def court(n):
    return {"contact": False, "penalty": 0,
            "courtiers": [{"name": "Courtier %d" % i, "office": OFFICES[i % 6], "regard": 0, "influence": 1,
                           "estate": ""} for i in range(n)]}


def court_cycle():
    cycle = influence_cycle()
    cycle["courts"] = {WEST: court(7), EAST: court(3), LOST: court(3)}
    cycle["warfronts"] = []
    return cycle


def set_score(c, score):
    c["contact"] = True
    c["penalty"] = 0
    raw = sum(x["regard"] * x["influence"] for x in c["courtiers"])
    c["penalty"] = raw - score


def forecast_holds(cycle):
    provinces = cycle["world"]["provinces"]
    expected = [ip.next_lunation(cycle, i)[0] for i in range(len(provinces))]
    before = [pr["influence"] for pr in provinces]
    ip.tick(cycle)
    return all(provinces[i]["influence"] - before[i] == expected[i] for i in range(len(provinces)))


def by_id(cycle, pid):
    return next(pr for pr in cycle["world"]["provinces"] if pr["id"] == pid)


# ── AssertEstates ───────────────────────────────────────────────────────
print("Estates")
cycle = court_cycle()
provinces = cycle["world"]["provinces"]
west_lands = [p["id"] for p in provinces if p["kingdom"] == WEST and not p["capital"]]
east_lands = [p["id"] for p in provinces if p["kingdom"] == EAST and not p["capital"]]
print("  west lands", west_lands, "east lands", east_lands)
west, east, lost = cycle["courts"][WEST], cycle["courts"][EAST], cycle["courts"][LOST]
west["courtiers"][3]["estate"] = west_lands[4]
west["courtiers"][4]["estate"] = east_lands[0]
check("the first sync gives estates", ip.sync(cycle))
order = all(west["courtiers"][n]["estate"] == (west_lands[4] if n == 3 else west_lands[n % len(west_lands)])
            for n in range(7))
check("west order, wrap, stored kept, foreign given again", order)
check("east's three", [c["estate"] for c in east["courtiers"]] == east_lands[:3])
check("lost court none", all(c["estate"] == "" for c in lost["courtiers"]))
holders = ip.estate_holders(cycle, by_id(cycle, west_lands[0]))
check("first west province: courtiers 0 and 5", holders == [west["courtiers"][0], west["courtiers"][5]])
check("a second sync gives nothing", not ip.sync(cycle))

# ── AssertCourtRules ────────────────────────────────────────────────────
print("Court rules")
cycle = court_cycle()
provinces = cycle["world"]["provinces"]
west = cycle["courts"][WEST]
ip.sync(cycle)
capital = pp.province_index_at(cycle["world"], 6, 15) if hasattr(pp, "province_index_at") else ip.province_index_at(cycle["world"], 6, 15)
check("capital no one's estate", capital >= 0 and ip.estate_holders(cycle, provinces[capital]) == [])


def deed_at_capital(amount):
    pr = provinces[capital]
    pr["influence"] = 0
    pr["held"] = False
    ip.add(cycle, pr["seat"][0], pr["seat"][1], amount)
    return pr["influence"]


check("before contact 10 gains 10", deed_at_capital(10) == 10)
for score, gain, b in [(-6, 5, ip.HOSTILE), (0, 10, ip.RECEIVED), (3, 11, ip.WELCOME), (9, 13, ip.FAVORED), (16, 15, ip.TRUSTED)]:
    set_score(west, score)
    got = deed_at_capital(10)
    check("at %s a deed of 10 gains %d (gained %d)" % (ip.BAND_NAMES[b], gain, got), ip.band(west) == b and got == gain)
provinces[capital]["influence"] = 0
provinces[capital]["held"] = False
ip.tick(cycle)
check("presence at Trusted still 8 (%d)" % provinces[capital]["influence"], provinces[capital]["influence"] == 8)

cold = west["courtiers"][0]
cold["regard"] = -1
set_score(west, 9)
cold_land = by_id(cycle, cold["estate"])
cold_land["influence"] = 0
cold_land["held"] = False
ip.add(cycle, cold_land["seat"][0], cold_land["seat"][1], 10)
check("Favored ill will 13 to 7 (%d)" % cold_land["influence"], cold_land["influence"] == 7 and ip.halves_deeds(cycle, cold_land))

cycle["castle"] = (-1, -1)
fond = west["courtiers"][1]
fond["regard"] = 2
set_score(west, 0)
fond_land = by_id(cycle, fond["estate"])
fond_land["influence"] = 23
fond_land["held"] = False
check("floor 20", ip.floor_of(cycle, fond_land) == 20)
check("23 to 21 as forecast", forecast_holds(cycle) and fond_land["influence"] == 21)
change, drift = ip.next_lunation(cycle, fond_land["index"])
line = ip.next_lunation_line(cycle, fond_land["index"])
print("   ", line)
check("at 21: -1, EstateFloor", change == -1 and drift == ip.ESTATE_FLOOR and "and no lower" in line
      and line.endswith("keeps it from fading below 20."))
check("stops at 20 as forecast", forecast_holds(cycle) and fond_land["influence"] == 20)
change, drift = ip.next_lunation(cycle, fond_land["index"])
line = ip.next_lunation_line(cycle, fond_land["index"])
print("   ", line)
check("at 20: no change, EstateFloor", change == 0 and drift == ip.ESTATE_FLOOR and line.startswith("Next lunation: no change."))
check("holds at 20", forecast_holds(cycle) and fond_land["influence"] == 20)
deep = west["courtiers"][2]
deep["regard"] = 3
deep_land = by_id(cycle, deep["estate"])
deep_land["influence"] = 10
deep_land["held"] = False
check("below floor 30 no fading", forecast_holds(cycle) and deep_land["influence"] == 10)
set_score(west, -6)
check("Hostile: no floor", ip.floor_of(cycle, fond_land) == 0)
check("Hostile: 20 to 18", forecast_holds(cycle) and fond_land["influence"] == 18)
set_score(west, 0)
fond_land["influence"] = 20
seize = {"defender": WEST, "seizing": True, "closed": False}
cycle["warfronts"] = [seize]
check("seizing: no floor, 20 to 18", ip.floor_of(cycle, fond_land) == 0 and ip.guild_seizing(cycle, WEST)
      and forecast_holds(cycle) and fond_land["influence"] == 18)
seize["closed"] = True
check("closed front revokes nothing", ip.floor_of(cycle, fond_land) == 20)
cycle["warfronts"] = []
west["contact"] = False
check("before contact no floor, no halving", ip.floor_of(cycle, fond_land) == 0 and not ip.halves_deeds(cycle, cold_land))
check("offices on the fond land: Marshal and Chancellor",
      [c["office"] for c in ip.estate_holders(cycle, fond_land)] == ["Marshal", "Chancellor"])

# ── AssertDeeds (the arithmetic) ────────────────────────────────────────
print("Deeds")
cycle = influence_cycle()
ip.sync(cycle)
provinces = cycle["world"]["provinces"]
capital = ip.province_index_at(cycle["world"], 6, 15)
land = ip.province_index_at(cycle["world"], 18, 5)
before = provinces[capital]["influence"]
cycle["walking"] = True
check("walking: nothing moves", ip.deed(cycle, 6, 15, 10) == "" and provinces[capital]["influence"] == before)
cycle["walking"] = False
ip.deed(cycle, 6, 15, 10)
check("after: lands", provinces[capital]["influence"] == before + 10)
before = provinces[land]["influence"]
ip.deed(cycle, 18, 5, ip.LEAD_GAIN)
check("a lead at (18,5) gives its province 5 (%d to %d)" % (before, provinces[land]["influence"]),
      provinces[land]["influence"] == before + 5)

# ── AssertBetrayal (the Regard) ─────────────────────────────────────────
print("Betrayal")
cycle = court_cycle()
west = cycle["courts"][WEST]
for c in west["courtiers"]:
    c["regard"] = 1
west["courtiers"][0]["regard"] = -3
set_score(west, 3)
check("Welcome before", ip.band(west) == ip.WELCOME)
ip.betrayal(cycle, WEST)
check("every courtier -1, to no lower than -3",
      west["courtiers"][0]["regard"] == -3 and all(c["regard"] == 0 for c in west["courtiers"][1:]))
check("one herald line", len(cycle.get("herald", [])) == 1)
set_score(west, 0)
before = [c["regard"] for c in west["courtiers"]]
check("Received: no betrayal", not ip.betrayal(cycle, WEST) and [c["regard"] for c in west["courtiers"]] == before)

# ── Randomized: forecast equals the lunation with courts in play ────────
print("Randomized forecast with courts")
rng = random.Random(2026110415)
checked = 0
drifts = set()
mismatch = 0
for trial in range(300):
    cycle = court_cycle()
    for kid in (WEST, EAST):
        for c in cycle["courts"][kid]["courtiers"]:
            c["regard"] = rng.randint(-3, 3)
            c["influence"] = rng.randint(1, 3)
        if rng.random() < 0.8:
            set_score(cycle["courts"][kid], rng.choice([-6, -2, 0, 2, 3, 8, 9, 15, 16, 20]))
            for c in cycle["courts"][kid]["courtiers"]:
                c["regard"] = rng.randint(-3, 3)  # the band moves with them too
    ip.sync(cycle)
    for pr in cycle["world"]["provinces"]:
        pr["influence"] = rng.choice([0, 1, 5, 9, 10, 11, 19, 20, 21, 29, 30, 31, 39, 40, 41, 50, 59, 60, 61, 80, 100])
        pr["held"] = pr["influence"] >= 50 and rng.random() < 0.7 or pr["influence"] >= 60
    cycle["castle"] = rng.choice([(6, 15), (30, 15), (18, 5), (-1, -1), (-1, -1)])
    cycle["warfronts"] = [{"defender": rng.choice([WEST, EAST]), "seizing": rng.random() < 0.5,
                           "closed": rng.random() < 0.3}] if rng.random() < 0.3 else []
    if rng.random() < 0.1:
        cycle["kingdoms"][EAST]["faction"] = ip.GUILD
    for lunation in range(8):
        provinces = cycle["world"]["provinces"]
        expected = [ip.next_lunation(cycle, i) for i in range(len(provinces))]
        before = [pr["influence"] for pr in provinces]
        ip.tick(cycle)
        for i, pr in enumerate(provinces):
            checked += 1
            drifts.add(expected[i][1])
            if pr["influence"] - before[i] != expected[i][0]:
                mismatch += 1
        # a deed now and then, through the courts
        if rng.random() < 0.5:
            pr = rng.choice(provinces)
            ip.add(cycle, pr["seat"][0], pr["seat"][1], rng.choice([5, 8, 10, 15, 20]))
check("forecast equals the lunation over %d province-lunations, %d reasons seen (%s)"
      % (checked, len(drifts), sorted(drifts)), mismatch == 0)
check("every reason seen, EstateFloor among them", ip.ESTATE_FLOOR in drifts and len(drifts) == 8)

print("RESULT:", "ALL PASSED" if not fails else "FAILURES: %d" % len(fails))
