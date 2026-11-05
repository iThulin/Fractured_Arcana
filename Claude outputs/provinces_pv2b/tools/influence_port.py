"""Line-for-line Python mirror of ProvinceInfluence.cs (slices PV2a to PV2b-1) and
ProvinceCourts.cs (PV2b-1), to test the
rules where no C# compiler is available. Worlds come from province_port (PV1's
mirror of ProvinceBuilder). A cycle is a dict:
  world: the province_port world (provinces gain 'influence' and 'held')
  kingdoms: {id: {'pi': PlayerInfluence, 'pid': PlayerInfluenceDerived,
                  'name': DisplayName, 'faction': ControllingFactionId}}
  castle: (x, y), (-1, -1) off the map
  reports: CycleState.PendingSiegeReports
  courts (PV2b-1, optional): {kid: {'contact': bool, 'penalty': int, 'courtiers':
         [{'name', 'office', 'regard', 'influence', 'estate'}]}}
  warfronts (optional): [{'defender', 'seizing', 'closed'}]
  walking (optional): the interval walks
  herald (optional): the Herald's Report lines
"""
import sys
from pathlib import Path
# province_port.py (the PV1 mirror) sits in Claude outputs/provinces_pv1 in the repo, or in pv1py here.
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
SITE_CLEARED_GAIN = 10
CONTRACT_GAIN = 8
LEAD_GAIN = 5
FLOOR_PER_REGARD = 10
BETRAYAL_REGARD_COST = 1
UNKNOWN, HOSTILE, RECEIVED, WELCOME, FAVORED, TRUSTED = range(6)
BAND_NAMES = ["Unknown", "Hostile", "Received", "Welcome", "Favored", "Trusted"]


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
    changed = ensure_estates(cycle)
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
            floor = floor_of(cycle, pr)
            value = max(max(0, value - DECAY), min(value, floor))
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
    if amount > 0:
        amount = deed_gain(cycle, pr, amount)
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


# ── The next lunation (the province readout, section 7c) ─────────────────
STILL, CASTLE_HERE, CASTLE_BESIDE, CAPPED, SOVEREIGN, HELD_NEIGHBORS, FADING, ESTATE_FLOOR = range(8)


def next_lunation(cycle, index):
    """Returns (change, drift): Tick's arithmetic, read without changing anything."""
    if not active(cycle) or index < 0 or index >= len(cycle["world"]["provinces"]):
        return 0, STILL
    provinces = cycle["world"]["provinces"]
    pr = provinces[index]
    castle = castle_province(cycle)
    here = index == castle
    beside = (not here) and castle >= 0 and index in provinces[castle]["neighbors"]
    held_neighbors = sum(1 for q in pr["neighbors"] if 0 <= q < len(provinces) and provinces[q]["held"])
    passive = (PRESENCE_GAIN if here else (PRESENCE_NEIGHBOR_GAIN if beside else 0)) + held_neighbors * HELD_NEIGHBOR_GAIN
    value = pr["influence"]
    nxt = value
    if passive > 0 and nxt < PASSIVE_CAP:
        nxt = min(PASSIVE_CAP, nxt + passive)
    ruled = guild_rules(cycle, pr["kingdom"])
    floored = False
    if not here and not beside and not ruled:
        decayed = max(0, nxt - DECAY)
        kept = max(decayed, min(nxt, floor_of(cycle, pr)))
        floored = kept > decayed
        nxt = kept
    change = clamp(nxt) - value
    if here or beside:
        drift = (CASTLE_HERE if here else CASTLE_BESIDE) if change > 0 else CAPPED
    elif ruled:
        drift = SOVEREIGN
    elif floored and change <= 0:
        drift = ESTATE_FLOOR
    elif change < 0:
        drift = FADING
    elif held_neighbors > 0:
        drift = HELD_NEIGHBORS
    else:
        drift = STILL
    return change, drift


def lunations_until_lost(cycle, index):
    change, drift = next_lunation(cycle, index)
    pr = cycle["world"]["provinces"][index]
    if drift != FADING or change >= 0 or not pr["held"]:
        return -1
    return (pr["influence"] - LOSE_LINE) // -change + 1


def next_lunation_line(cycle, index):
    if not active(cycle) or index < 0 or index >= len(cycle["world"]["provinces"]):
        return ""
    change, drift = next_lunation(cycle, index)
    amount = "+%d" % change if change > 0 else "%d" % change
    if drift == CASTLE_HERE:
        line = "Next lunation: %s, with the castle here." % amount
    elif drift == CASTLE_BESIDE:
        line = "Next lunation: %s, with the castle beside it." % amount
    elif drift == CAPPED:
        line = "Next lunation: no change. The castle's presence stops at %d; deeds take it further." % PASSIVE_CAP
    elif drift == SOVEREIGN:
        line = ("Next lunation: %s. The guild rules this kingdom, so its influence does not fade." % amount
                if change > 0 else "Next lunation: no change. The guild rules this kingdom, so its influence does not fade.")
    elif drift == HELD_NEIGHBORS:
        line = ("Next lunation: %s, from the held provinces beside it." % amount
                if change > 0 else "Next lunation: no change. The held provinces beside it hold off the fading.")
    elif drift == FADING:
        line = "Next lunation: %s, with the castle away." % amount
    elif drift == ESTATE_FLOOR:
        pr = cycle["world"]["provinces"][index]
        who = who_of(floor_holder(cycle, pr))
        floor = floor_of(cycle, pr)
        line = ("Next lunation: %s, and no lower: %s's regard keeps it from fading below %d." % (amount, who, floor)
                if change < 0 else
                "Next lunation: no change. %s's regard keeps it from fading below %d." % (who, floor))
    else:
        line = "Next lunation: no change. Nothing reaches it with the castle away."
    lost = lunations_until_lost(cycle, index)
    if lost == 1:
        line += " The hold is lost next lunation."
    elif lost > 1:
        line += " The hold is lost in %d lunations." % lost
    return line


# ── ProvinceCourts (slice PV2b-1) ────────────────────────────────────────
def court_of(cycle, kid):
    if not kid:
        return None
    return (cycle.get("courts") or {}).get(kid)


def band(court):
    if not court["contact"]:
        return UNKNOWN
    score = sum(c["regard"] * c["influence"] for c in court["courtiers"]) - court.get("penalty", 0)
    if score <= -6:
        return HOSTILE
    if score <= 2:
        return RECEIVED
    if score <= 8:
        return WELCOME
    if score <= 15:
        return FAVORED
    return TRUSTED


def band_percent_of(b):
    return {HOSTILE: 50, WELCOME: 110, FAVORED: 125, TRUSTED: 150}.get(b, 100)


def band_percent(cycle, kid):
    court = court_of(cycle, kid)
    return 100 if court is None else band_percent_of(band(court))


def ensure_estates(cycle):
    world = cycle.get("world")
    courts = cycle.get("courts")
    if world is None or not courts or not world.get("provinces"):
        return False
    changed = False
    for kid, court in courts.items():
        if not court["courtiers"]:
            continue
        lands = []
        mine = set()
        capital = None
        for pr in world["provinces"]:
            if pr["kingdom"] != kid:
                continue
            mine.add(pr["id"])
            if pr["capital"]:
                if capital is None:
                    capital = pr
            else:
                lands.append(pr)
        if not lands and capital is not None:
            lands.append(capital)
        if not lands:
            continue
        for n, c in enumerate(court["courtiers"]):
            if c.get("estate") and c["estate"] in mine:
                continue
            c["estate"] = lands[n % len(lands)]["id"]
            changed = True
    return changed


def estate_holders(cycle, pr):
    court = court_of(cycle, pr["kingdom"]) if pr else None
    if court is None:
        return []
    return [c for c in court["courtiers"] if c.get("estate") == pr["id"]]


def halves_deeds(cycle, pr):
    court = court_of(cycle, pr["kingdom"]) if pr else None
    if court is None or not court["contact"]:
        return False
    return any(c.get("estate") == pr["id"] and c["regard"] < 0 for c in court["courtiers"])


def deed_gain(cycle, pr, amount):
    if amount <= 0 or pr is None:
        return amount
    gain = (amount * band_percent(cycle, pr["kingdom"]) + 50) // 100
    if halves_deeds(cycle, pr):
        gain = (gain + 1) // 2
    return gain


def guild_seizing(cycle, kid):
    return any((not f.get("closed")) and f.get("seizing") and f.get("defender") == kid
               for f in cycle.get("warfronts") or [])


def floor_holder(cycle, pr):
    court = court_of(cycle, pr["kingdom"]) if pr else None
    if court is None or not court["contact"] or band(court) == HOSTILE or guild_seizing(cycle, pr["kingdom"]):
        return None
    best = None
    for c in court["courtiers"]:
        if c.get("estate") == pr["id"] and c["regard"] > 0 and (best is None or c["regard"] > best["regard"]):
            best = c
    return best


def floor_of(cycle, pr):
    holder = floor_holder(cycle, pr)
    return 0 if holder is None else holder["regard"] * FLOOR_PER_REGARD


def who_of(c):
    return "A courtier" if c is None else "%s the %s" % (c["name"], c["office"])


def deed(cycle, x, y, amount):
    if cycle.get("walking"):
        return ""
    return add(cycle, x, y, amount)


def betrayal(cycle, kid):
    court = court_of(cycle, kid)
    if court is None or not court["courtiers"] or band(court) < WELCOME:
        return False
    for c in court["courtiers"]:
        c["regard"] = max(-3, min(3, c["regard"] - BETRAYAL_REGARD_COST))
    cycle.setdefault("herald", []).append((kid, BAND_NAMES[band(court)]))
    return True
