"""Line-for-line Python mirror of ProvinceInfluence.cs (slice PV2a), to test the
rules where no C# compiler is available. Worlds come from province_port (PV1's
mirror of ProvinceBuilder). A cycle is a dict:
  world: the province_port world (provinces gain 'influence' and 'held')
  kingdoms: {id: {'pi': PlayerInfluence, 'pid': PlayerInfluenceDerived,
                  'name': DisplayName, 'faction': ControllingFactionId}}
  castle: (x, y), (-1, -1) off the map
  reports: CycleState.PendingSiegeReports
"""
import sys
from pathlib import Path
# province_port.py (the PV1 mirror) sits in ../provinces_pv1 in the repo, or in pv1py here.
for _p in (Path(__file__).resolve().parent.parent.parent / "provinces_pv1", Path("/home/claude/pv1py")):
    sys.path.insert(0, str(_p))
import province_port as pp

HOLD_LINE = 60
LOSE_LINE = 50
MAX_INFLUENCE = 100
PRESENCE_GAIN = 8
PRESENCE_NEIGHBOR_GAIN = 3
HELD_NEIGHBOR_GAIN = 1
PASSIVE_CAP = 40
DECAY = 2
NEAR_RADIUS = 12
GUILD = "guild"


def has_provinces(world):
    return world.get("pmap") is not None and len(world.get("provinces", [])) > 0


def province_index_at(world, x, y):
    if not (0 <= x < world["W"] and 0 <= y < world["H"]) or not has_provinces(world):
        return -1
    p = world["pmap"][y * world["W"] + x]
    return p if p != pp.NO_PROVINCE and p < len(world["provinces"]) else -1


def province_at(world, x, y):
    p = province_index_at(world, x, y)
    return world["provinces"][p] if p >= 0 else None


def ensure_fields(world):
    for pr in world.get("provinces", []):
        pr.setdefault("influence", 0)
        pr.setdefault("held", False)


def active(cycle):
    return cycle.get("world") is not None and cycle.get("kingdoms") is not None and has_provinces(cycle["world"])


def influence_at(world, x, y):
    pr = province_at(world, x, y)
    return pr["influence"] if pr is not None else 0


def holds_any(world, kid):
    if not kid or not has_provinces(world):
        return False
    return any(pr["held"] and pr["kingdom"] == kid for pr in world["provinces"])


def castle_province(cycle):
    x, y = cycle["castle"]
    if x < 0 or y < 0:
        return -1
    return index_near(cycle["world"], x, y, None)


def sync(cycle, lines=None):
    if not active(cycle):
        return False
    changed = False
    for kid, k in cycle["kingdoms"].items():
        changed |= absorb(cycle, kid, k)
    changed |= settle_all(cycle, lines)
    return changed


def tick(cycle):
    lines = []
    if not active(cycle):
        return lines
    sync(cycle, lines)
    provinces = cycle["world"]["provinces"]
    count = len(provinces)
    castle = castle_province(cycle)
    near = [False] * count
    if castle >= 0:
        near[castle] = True
        for q in provinces[castle]["neighbors"]:
            if 0 <= q < count:
                near[q] = True
    held_before = [pr["held"] for pr in provinces]
    for i in range(count):
        pr = provinces[i]
        passive = PRESENCE_GAIN if i == castle else (PRESENCE_NEIGHBOR_GAIN if near[i] else 0)
        for q in pr["neighbors"]:
            if 0 <= q < count and held_before[q]:
                passive += HELD_NEIGHBOR_GAIN
        value = pr["influence"]
        if passive > 0 and value < PASSIVE_CAP:
            value = min(PASSIVE_CAP, value + passive)
        if not near[i] and not guild_rules(cycle, pr["kingdom"]):
            value = max(0, value - DECAY)
        pr["influence"] = clamp(value)
    settle_all(cycle, lines)
    return lines


def add(cycle, x, y, amount):
    if cycle.get("world") is None:
        return ""
    if not active(cycle):
        add_legacy(cycle, tile_kingdom(cycle["world"], x, y), amount)
        return ""
    return add_to_province(cycle, index_near(cycle["world"], x, y, None), amount)


def add_near(cycle, kid, x, y, amount):
    if cycle.get("world") is None or not kid:
        return ""
    if not active(cycle):
        add_legacy(cycle, kid, amount)
        return ""
    at = index_near(cycle["world"], x, y, kid)
    if at < 0:
        at = capital_index(cycle["world"], kid)
    if at < 0:
        add_legacy(cycle, kid, amount)
        return ""
    return add_to_province(cycle, at, amount)


def set_kingdom(cycle, kid, value):
    if cycle.get("kingdoms") is None or not kid:
        return
    value = clamp(value)
    any_ = False
    if active(cycle):
        for pr in cycle["world"]["provinces"]:
            if pr["kingdom"] != kid:
                continue
            pr["influence"] = value
            update_held(cycle, pr)
            any_ = True
    k = cycle["kingdoms"].get(kid)
    if k is None:
        return
    if any_:
        derive(cycle, kid, k)
    else:
        k["pi"] = value


def standing_line(pr):
    if pr is None:
        return ""
    name = pr["name"] or pr["id"]
    return ("The guild holds %s: influence %d, lost below %d." % (name, pr["influence"], LOSE_LINE)
            if pr["held"] else "Guild influence in %s: %d, held at %d." % (name, pr["influence"], HOLD_LINE))


def clamp(v):
    return max(0, min(MAX_INFLUENCE, v))


def guild_rules(cycle, kid):
    k = cycle["kingdoms"].get(kid) if kid else None
    return k is not None and k.get("faction", "") == GUILD


def add_to_province(cycle, index, amount):
    provinces = cycle["world"]["provinces"]
    if index < 0 or index >= len(provinces):
        return ""
    pr = provinces[index]
    kid = pr["kingdom"] or ""
    k = cycle["kingdoms"].get(kid)
    absorb(cycle, kid, k)
    pr["influence"] = clamp(pr["influence"] + amount)
    news = []
    for other in provinces:
        if (other["kingdom"] or "") != kid:
            continue
        line = update_held(cycle, other)
        if line:
            news.append(line)
    derive(cycle, kid, k)
    if not news:
        return ""
    for line in news:
        cycle["reports"].append(line)
    return " ".join(news)


def absorb(cycle, kid, k):
    if k is None or not kid:
        return False
    delta = k["pi"] - k["pid"]
    if delta == 0:
        return False
    changed = False
    for pr in cycle["world"]["provinces"]:
        if pr["kingdom"] != kid:
            continue
        value = clamp(pr["influence"] + delta)
        changed |= value != pr["influence"]
        pr["influence"] = value
    return changed


def settle_all(cycle, lines):
    changed = False
    for pr in cycle["world"]["provinces"]:
        line = update_held(cycle, pr)
        if not line:
            continue
        changed = True
        if lines is not None:
            lines.append(line)
    for kid, k in cycle["kingdoms"].items():
        changed |= derive(cycle, kid, k)
    return changed


def update_held(cycle, pr):
    if not pr["held"] and pr["influence"] >= HOLD_LINE:
        pr["held"] = True
        return "The guild now holds %s." % label(cycle, pr)
    if pr["held"] and pr["influence"] < LOSE_LINE:
        pr["held"] = False
        return "The guild's hold on %s is lost." % label(cycle, pr)
    return ""


def derive(cycle, kid, k):
    if k is None or not kid:
        return False
    s = 0
    tiles = 0
    for pr in cycle["world"]["provinces"]:
        if pr["kingdom"] != kid or pr["tiles"] <= 0:
            continue
        s += pr["influence"] * pr["tiles"]
        tiles += pr["tiles"]
    if tiles == 0:
        return False
    mean = (2 * s + tiles) // (2 * tiles)
    changed = k["pi"] != mean or k["pid"] != mean
    k["pi"] = mean
    k["pid"] = mean
    return changed


def add_legacy(cycle, kid, amount):
    k = cycle["kingdoms"].get(kid) if kid else None
    if k is None:
        return
    k["pi"] = clamp(k["pi"] + amount)


def tile_kingdom(world, x, y):
    if 0 <= x < world["W"] and 0 <= y < world["H"]:
        return world["tiles"][y * world["W"] + x]["kingdom"] or ""
    return ""


def index_near(world, x, y, kid):
    if not has_provinces(world) or not (0 <= x < world["W"] and 0 <= y < world["H"]):
        return -1
    here = province_index_at(world, x, y)
    if here >= 0 and (kid is None or world["provinces"][here]["kingdom"] == kid):
        return here
    w = world["W"]
    seen = {y * w + x}
    frontier = [(x, y)]
    ring = 1
    while ring <= NEAR_RADIUS and frontier:
        nxt = []
        for cx, cy in frontier:
            q, r = pp.off_to_ax(cx, cy)
            for d in range(6):
                dq, dr = pp.AXIAL[d]
                nx, ny = pp.ax_to_off(q + dq, r + dr)
                if not (0 <= nx < world["W"] and 0 <= ny < world["H"]) or (ny * w + nx) in seen:
                    continue
                seen.add(ny * w + nx)
                p = province_index_at(world, nx, ny)
                if p >= 0 and (kid is None or world["provinces"][p]["kingdom"] == kid):
                    return p
                nxt.append((nx, ny))
        frontier = nxt
        ring += 1
    return -1


def capital_index(world, kid):
    first = -1
    for i, pr in enumerate(world["provinces"]):
        if pr["kingdom"] != kid:
            continue
        if pr["capital"]:
            return i
        if first < 0:
            first = i
    return first


def label(cycle, pr):
    name = pr["name"] or pr["id"]
    k = cycle["kingdoms"].get(pr["kingdom"]) if pr["kingdom"] else None
    if k is None:
        return name
    return "%s, %s" % (name, k["name"] or pr["kingdom"])
