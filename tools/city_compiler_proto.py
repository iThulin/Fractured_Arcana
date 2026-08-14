#!/usr/bin/env python3
"""
city_compiler_proto.py — numeric verification of the CityBattlemapCompiler
window/layout geometry BEFORE any C# is written (per project discipline).

Models, against the REAL default campus data (CampusMapSaveData.GenerateDefault
+ Data/Buildings startsBuiltAt):
  1. the /3 flower lattice (districts (0,0),(1,0),(0,1) -> 22 cells),
  2. gate-assault window extraction (focus = gatehouse_yard lot),
  3. lot layout in combat-axial space (stamps declare radius; clip at map edge),
  4. wall band + gate gap + street lanes,
  5. asserts: no stamp overlap, outside->inside connectivity through the gate
     ONLY, all admitted lot centers street-connected, envelope respected.

Spec: docs/city_battlemap_compiler_spec_v1_1.md  (sections 3, 4, 5)
"""

# ---------------------------------------------------------------- hex basics
# axial directions in ROTATIONAL (60-degree) order — index arithmetic on this
# list is angle arithmetic, which the wall face-tangent derivation relies on
DIRS = [(1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)]

def hexdist(a, b):
    dq, dr = a[0] - b[0], a[1] - b[1]
    return (abs(dq) + abs(dr) + abs(dq + dr)) // 2

def disk(center, radius):
    cq, cr = center
    out = []
    for q in range(-radius, radius + 1):
        for r in range(max(-radius, -q - radius), min(radius, -q + radius) + 1):
            out.append((cq + q, cr + r))
    return out

def line(start, direction, length):
    q, r = start
    dq, dr = direction
    return [(q + i * dq, r + i * dr) for i in range(length)]

# ------------------------------------------------- campus lattice (mirrors C#)
def district_centre(dq, dr): return (3 * dq, 3 * dr)

CORNER_DIRS = [(2, -1), (1, 1), (-1, 2), (-2, 1), (-1, -1), (1, -2)]

def corner_owners(q, r):
    owners = []
    for a, b in CORNER_DIRS:
        nq, nr = q + a, r + b
        if nq % 3 != 0 or nr % 3 != 0:
            continue
        owners.append((nq // 3, nr // 3))
    return owners

def rebuild_tiles(unlocked):
    tiles = {}
    for d in unlocked:
        c = district_centre(*d)
        tiles.setdefault(c, "Plaza")
        for a, b in DIRS:
            tiles.setdefault((c[0] + a, c[1] + b), "Lawn")
    for d in unlocked:
        c = district_centre(*d)
        for a, b in CORNER_DIRS:
            cell = (c[0] + a, c[1] + b)
            if cell in tiles:
                continue
            owners = corner_owners(*cell)
            if len(owners) == 3 and all(o in unlocked for o in owners):
                tiles[cell] = "Corner"
    return tiles

FOUNDING = [(0, 0), (1, 0), (0, 1)]
CAMPUS = rebuild_tiles(FOUNDING)
assert len(CAMPUS) == 22, f"lattice mismatch: {len(CAMPUS)} != 22 (C# GenerateDefault)"

# Real default placements (Data/Buildings/*.json startsBuiltAt) + PLACEHOLDER
# size classes (open question 2 — Magos to ratify the class table).
SIZE = {"modest": 2, "grand": 4, "landmark": 6, "seat": 8}
BUILDINGS = {           # lot -> (id, class)
    (0, 0): ("grand_hall", "seat"),
    (1, 0): ("dormitory", "grand"),
    (2, 0): ("armory", "grand"),
    (1, 1): ("sanctum", "grand"),      # the shared bonus-corner cell
    (0, 2): ("gatehouse_yard", "modest"),   # a YARD: small gate structure, open ground
}
GATE_LOT = (0, 2)
STREET = 2               # min clear hexes between stamps
MAP_RADIUS = 8           # standard siege envelope (spec section 3)

# ---------------------------------------------------------- window extraction
def extract_window(focus, max_lots=None):
    """BFS over lattice adjacency from the focus. max_lots=None = the WHOLE
    city (v2: full layout — the arena clips the window; the remainder becomes
    the visual backdrop, so the city continues past the map edge)."""
    admitted, frontier, seen = [focus], [focus], {focus}
    while frontier and (max_lots is None or len(admitted) < max_lots):
        nxt = []
        for cell in frontier:
            for a, b in DIRS:
                n = (cell[0] + a, cell[1] + b)
                if n in CAMPUS and n not in seen:
                    seen.add(n)
                    admitted.append(n)
                    nxt.append(n)
                    if max_lots is not None and len(admitted) >= max_lots:
                        break
            if max_lots is not None and len(admitted) >= max_lots:
                break
        frontier = nxt
    return admitted

def stamp_radius(lot):
    if lot in BUILDINGS:
        return SIZE[BUILDINGS[lot][1]]
    return 1  # empty lot: a small plaza/lawn patch

# -------------------------------------------------------------- lot layout
def layout(admitted, focus):
    """Place lot centers in combat-axial space. Focus at origin; children along
    their lattice direction from their BFS parent at pitch r_p + r_c + STREET + 1."""
    pos = {focus: (0, 0)}
    parent = {focus: None}
    order = sorted(admitted, key=lambda c: hexdist(c, focus))
    for lot in order:
        if lot == focus:
            continue
        # parent: the admitted neighbor closest to focus (already placed)
        cands = [( (lot[0]-a, lot[1]-b), (a, b) ) for a, b in DIRS
                 if (lot[0]-a, lot[1]-b) in pos]
        assert cands, f"lot {lot} has no placed lattice neighbor"
        par, d = min(cands, key=lambda t: hexdist(t[0], focus))
        pitch = stamp_radius(par) + stamp_radius(lot) + STREET + 1
        pos[lot] = (pos[par][0] + d[0] * pitch, pos[par][1] + d[1] * pitch)
        parent[lot] = par
    return pos, parent

# ------------------------------------------------------------------- compile
def compile_window(focus=GATE_LOT, opening="door"):
    all_lots = extract_window(focus)              # the WHOLE city
    pos, parent = layout(all_lots, focus)

    # window = lots whose centers land in the arena; the rest is BACKDROP
    # (their in-arena stamp tiles still paint — edge-clipped buildings)
    admitted = [l for l in all_lots if hexdist(pos[l], (0, 0)) <= MAP_RADIUS]

    # outward = the gate lot's MISSING lattice neighbor(s): the city perimeter
    # is where the lattice ends, not a centroid guess. (Generalizes to NPC
    # cities unchanged.)
    def missing_dirs(lot):
        return [d for d in DIRS if (lot[0] + d[0], lot[1] + d[1]) not in CAMPUS]
    gate_missing = missing_dirs(focus)
    assert gate_missing, "gate lot is not on the city perimeter"
    d_out = gate_missing[0]
    d_in = (-d_out[0], -d_out[1])

    arena = set(disk((0, 0), MAP_RADIUS))
    tiles = {t: "ground" for t in arena}

    # stamps: EVERY positioned building paints its in-arena tiles — a lot whose
    # center sits beyond the rim still pokes its edge into the map (clipped
    # buildings ARE the city continuing past the edge)
    stamps = {}
    for lot in all_lots:
        r = stamp_radius(lot)
        if lot in BUILDINGS:
            body = [t for t in disk(pos[lot], r) if t in arena]
            if body:
                stamps[lot] = set(body)
                for t in body:
                    tiles[t] = "bldg:" + BUILDINGS[lot][0]
        elif lot in admitted:
            for t in disk(pos[lot], 1):
                if t in arena:
                    tiles[t] = "plaza" if CAMPUS[lot] == "Plaza" else "lawn"

    # Overlap: HARD assert only when both lots are in the arena window —
    # backdrop-side stamps placed via different parent chains may merge, and
    # merged masses beyond the wall read as dense city blocks (correct
    # fiction, mechanically inert; arena navigability is guarded by the
    # connectivity asserts below, not by pitch).
    lots = [l for l in all_lots if l in BUILDINGS]
    for i in range(len(lots)):
        for j in range(i + 1, len(lots)):
            a, b = lots[i], lots[j]
            need = stamp_radius(a) + stamp_radius(b) + 1
            got = hexdist(pos[a], pos[b])
            if a in admitted and b in admitted:
                assert got >= need, f"OVERLAP {a}{b}: dist {got} < {need}"

    # wall v2 — CONTINUOUS contour (the v1 per-lot face segments read as
    # scattered rocks in-engine). Method: 2-thick candidate annuli around every
    # perimeter lot -> flood the OUTSIDE from the attacker edge -> wall = shell
    # tiles adjacent to outside. Continuity + sealing are enforced by the
    # partition assert below, not by construction hope.
    gate_r = stamp_radius(focus)
    gate_outer = (pos[focus][0] + d_out[0] * (gate_r + 1),
                  pos[focus][1] + d_out[1] * (gate_r + 1))
    # City region = union of disks (stamp + 2-tile margin). +2 guarantees
    # adjacent lots' disks overlap (pitch = rA+rB+3 < rA+rB+4), so the region
    # is one connected blob and its outer boundary is a CLOSED, 1-thick
    # contour by construction — the curtain wall, clipped by the arena edge.
    region = set()
    for lot in all_lots:                 # FULL city — no phantom interior walls
        for t in disk(pos[lot], stamp_radius(lot) + 2):
            region.add(t)
    boundary = set()
    for t in region:
        for dq, dr in DIRS:
            n = (t[0] + dq, t[1] + dr)
            if n not in region and n in arena:
                boundary.add(n)

    # gate gap: the 2 boundary tiles nearest the outward ray from the gate lot
    import math
    def cart(t):
        return (1.5 * t[0], math.sqrt(3) * (t[1] + t[0] / 2.0))
    gx, gy = cart(pos[focus])
    nx, ny = cart((pos[focus][0] + d_out[0], pos[focus][1] + d_out[1]))
    nx, ny = nx - gx, ny - gy
    nlen = math.hypot(nx, ny)
    nx, ny = nx / nlen, ny / nlen
    def ray_key(t):
        px, py = cart(t)
        px, py = px - gx, py - gy
        along = px * nx + py * ny
        across = abs(-px * ny + py * nx)
        # (q, r) tiebreak: symmetric boundary pairs tie exactly on floats, and
        # the C# port must pick the SAME two tiles (lockstep determinism)
        return (across, -along, t[0], t[1]) if along > 0 else (1e9, 0, t[0], t[1])
    GATE_GAP_WIDTH = 3   # ruled 2026-08-11: the door spans the full gate face
    gap = set(sorted(boundary, key=ray_key)[:GATE_GAP_WIDTH])
    # the door tiles must form one contiguous face, not scattered notches
    for g in gap:
        assert any((g[0] + d[0], g[1] + d[1]) in gap for d in DIRS), \
            f"gate gap tile {g} is not contiguous with the rest of the door"

    shell = boundary - gap

    blocked_for_flood = shell | {t for t, k in tiles.items() if k.startswith("bldg")}
    seed = (gate_outer[0] + d_out[0] * 3, gate_outer[1] + d_out[1] * 3)
    outside = set()
    if seed in arena and seed not in blocked_for_flood:
        stack = [seed]
        outside.add(seed)
        while stack:
            c = stack.pop()
            for dq, dr in DIRS:
                n = (c[0] + dq, c[1] + dr)
                if n in arena and n not in blocked_for_flood and n not in outside:
                    outside.add(n)
                    stack.append(n)

    # inside = flood from the city side (deepest interior lot centre); a wall
    # tile must border BOTH outside and inside — a "wall" with outside on both
    # faces separates nothing and renders as free-standing blob clutter (seen
    # in-engine 2026-08-11); such shell tiles are dropped entirely.
    interior0 = [l for l in admitted if l != focus and not missing_dirs(l)]
    inside_seed = pos[interior0[0]] if interior0 else pos[[l for l in admitted if l != focus][0]]
    inside = set()
    if inside_seed in arena and inside_seed not in blocked_for_flood:
        stack = [inside_seed]
        inside.add(inside_seed)
        while stack:
            c = stack.pop()
            for dq, dr in DIRS:
                n = (c[0] + dq, c[1] + dr)
                if n in arena and n not in blocked_for_flood and n not in inside:
                    inside.add(n)
                    stack.append(n)

    # The boundary IS the curtain: closed + 1-thick by construction.
    wall_set = set(shell)
    bldg_tiles = {t for t, k in tiles.items() if k.startswith("bldg")}

    # RAMPARTS (2026-08-11 ruling): wall tiles within 2 of the gap become
    # WALKABLE stone at height 4 — fighting positions over the entrance. The
    # seal moves from "blocked" to the CLIFF RULE (CliffHeightThreshold = 2:
    # ground 0 -> rampart 4 is an illegal step). One stair tile (height 2)
    # per flank inside the courtyard gives defenders a legal 0->2->4 climb;
    # enemies that force the door can storm the stairs — correct fiction.
    heights = {}
    rampart = set()
    if opening == "door":   # a collapsed breach has no pristine fighting platforms
        rampart = {t for t in wall_set if any(hexdist(t, g) <= 2 for g in gap)}
    wall_set -= rampart
    for t in rampart:
        heights[t] = 4

    def step_ok(a, b):
        return abs(heights.get(a, 0) - heights.get(b, 0)) <= 2  # CliffHeightThreshold

    def cross_side(t):
        # which flank of the outward ray a tile sits on (cartesian cross sign)
        px, py = cart(t)
        px, py = px - gx, py - gy
        return (-px * ny + py * nx) >= 0

    stairs = set()
    for side in (True, False):
        cands = sorted(
            n
            for r_t in rampart if cross_side(r_t) == side
            for n in [(r_t[0] + d[0], r_t[1] + d[1]) for d in DIRS]
            if n in arena and n in region and n not in gap
            and n not in wall_set and n not in rampart
            and not tiles.get(n, "").startswith("bldg"))
        if cands:
            stairs.add(cands[0])
    for t in stairs:
        heights[t] = 2

    for t in rampart:
        tiles[t] = "rampart"
    for t in stairs:
        tiles[t] = "stair"

    def sealed(wset):
        passable = (arena - wset - bldg_tiles) - gap
        seed_t = (gate_outer[0] + d_out[0] * 3, gate_outer[1] + d_out[1] * 3)
        if seed_t not in passable or inside_seed not in passable:
            return True
        seen, stack = {seed_t}, [seed_t]
        while stack:
            c = stack.pop()
            for dq, dr in DIRS:
                n = (c[0] + dq, c[1] + dr)
                if n in passable and n not in seen and step_ok(c, n):
                    seen.add(n)
                    stack.append(n)
        # sealed = neither the interior NOR the rampart top is reachable —
        # the cliff rule is now part of the seal, so it is asserted, not hoped
        return inside_seed not in seen and not (rampart & seen)
    assert sealed(wall_set), "curtain path does not seal the approach"

    for t in wall_set:
        tiles[t] = "wall"

    # Objective zone (hold_zone "gate"): the door + the INSIDE pocket only —
    # gap tiles plus region tiles within 2 of the gap that aren't stamps.
    # Region membership is what excludes the outside approach: enemies must
    # come THROUGH the door to breach, not mill about in front of it.
    objective_zone = set(gap)
    for t in region:
        if t in arena and not tiles.get(t, "").startswith("bldg") and t not in wall_set:
            if any(hexdist(t, g) <= 2 for g in gap):
                objective_zone.add(t)
    # outside-with-the-door-SEALED is the true attacker side; the zone (minus
    # the door itself) must lie entirely on the city side of it
    sealed_blocked = wall_set | bldg_tiles | gap
    out2 = set()
    seed2 = (gate_outer[0] + d_out[0] * 3, gate_outer[1] + d_out[1] * 3)
    if seed2 in arena and seed2 not in sealed_blocked:
        stack = [seed2]
        out2.add(seed2)
        while stack:
            c = stack.pop()
            for dq, dr in DIRS:
                n = (c[0] + dq, c[1] + dr)
                if n in arena and n not in sealed_blocked and n not in out2 and step_ok(c, n):
                    out2.add(n)
                    stack.append(n)
    assert not (objective_zone - gap) & out2, \
        "objective zone leaked outside the wall"
    assert len(objective_zone) >= 4, "objective zone implausibly small"

    # opening == "rubble" (wall breach): no doors — the collapsed wall chokes
    # the opening with cover instead. Up to 2 pocket tiles that flank the
    # breach (adjacent to exactly ONE gap tile — never the central lane),
    # deterministic order. Connectivity asserts below remain the guard that
    # rubble never re-seals the breach.
    if opening == "rubble":
        # candidates: any zone-pocket tile flanking the opening (zone already
        # excludes walls and stamps; plaza rubble is fine — collapsed masonry)
        flank = sorted(
            t for t in (objective_zone - gap)
            if sum(1 for g in gap if hexdist(t, g) == 1) == 1)
        for t in flank[:2]:
            tiles[t] = "rubble"
        # collapsed-masonry debris field OUTSIDE the breach (approach side):
        # up to 3 scattered blocked tiles within 2 of the opening — cover for
        # the attacker, dressing for the fiction. Outside = not in region.
        debris = sorted(
            t for t in arena
            if t not in region and t not in gap and t not in wall_set
            and tiles.get(t, "") == "ground"
            and any(hexdist(t, g) <= 2 for g in gap))
        for t in debris[:3]:
            tiles[t] = "rubble"

    # opening == "dock": the approach pocket floods as HARBOR WATER (impassable);
    # the quay (gap) stays ground, and the landing barge + pier are carved back
    # in with the lanes below. Wall tiles stay wall (sea wall).
    if opening == "dock":
        wseed = (gate_outer[0] + d_out[0] * 3, gate_outer[1] + d_out[1] * 3)
        pocket = set()
        if wseed in arena and wseed not in region:
            stack = [wseed]
            pocket.add(wseed)
            while stack:
                c = stack.pop()
                for dq, dr in DIRS:
                    n = (c[0] + dq, c[1] + dr)
                    if (n in arena and n not in region and n not in boundary
                            and n not in gap and n not in pocket):
                        pocket.add(n)
                        stack.append(n)
        for t in pocket:
            if tiles.get(t, "") == "ground":
                tiles[t] = "water"
        # the landing barge: walkable deck around the attacker anchor
        for t in disk(wseed, 1):
            if tiles.get(t, "") == "water":
                tiles[t] = "pier"

    # streets: lanes lot->parent + approach lane through the gap
    def carve(a, b):
        cur = a
        while cur != b:
            best = min([(cur[0] + dq, cur[1] + dr) for dq, dr in DIRS],
                       key=lambda t: hexdist(t, b))
            cur = best
            if cur in arena and tiles[cur] in ("ground", "lawn", "wall", "water"):
                if tiles[cur] == "wall" and cur not in gap:
                    continue                        # walls first, doors second
                tiles[cur] = "pier" if tiles[cur] == "water" else "street"
    for lot in admitted:
        if parent[lot]:
            carve(pos[lot], pos[parent[lot]])
    player_anchor = (gate_outer[0] + d_out[0] * 3, gate_outer[1] + d_out[1] * 3)
    interior = [l for l in admitted if l != focus and not missing_dirs(l)]
    enemy_lot = interior[0] if interior else [l for l in admitted if l != focus][0]
    enemy_anchor = pos[enemy_lot]
    carve(player_anchor, gate_outer)
    carve(gate_outer, pos[focus])

    # connectivity asserts
    passable = {t for t, k in tiles.items()
                if not k.startswith("bldg") and k not in ("wall", "rubble", "water")}
    def flood(start):
        seen, stack = {start}, [start]
        while stack:
            c = stack.pop()
            for dq, dr in DIRS:
                n = (c[0] + dq, c[1] + dr)
                if n in passable and n not in seen and step_ok(c, n):
                    seen.add(n)
                    stack.append(n)
        return seen
    if player_anchor not in passable:
        passable.add(player_anchor); tiles[player_anchor] = "street"
    reach = flood(player_anchor)
    inside_probe = enemy_anchor
    assert inside_probe in reach, "gate gap does not connect outside to inside"
    # wall must otherwise partition: removing the gap should disconnect
    passable_nogap = passable - gap
    seen2 = {player_anchor}; stack = [player_anchor]
    while stack:
        c = stack.pop()
        for dq, dr in DIRS:
            n = (c[0] + dq, c[1] + dr)
            if n in passable_nogap and n not in seen2 and step_ok(c, n):
                seen2.add(n); stack.append(n)
    assert inside_probe not in seen2, "wall is porous: inside reachable without the gate gap"
    if rampart:
        assert rampart & reach, "rampart unreachable: stairs failed"
    for lot in admitted:
        anchor_t = pos[lot]
        near = [t for t in disk(anchor_t, stamp_radius(lot) + 1) if t in reach]
        assert near, f"lot {lot} street-island: unreachable from player anchor"

    return tiles, pos, admitted, player_anchor, enemy_anchor, d_out

# --------------------------------------------------------------------- render
def render(tiles):
    qs = [t[0] for t in tiles]; rs = [t[1] for t in tiles]
    sym = {"ground": ",", "lawn": ",", "plaza": "^", "street": ".", "wall": "#", "rubble": "%", "water": "~", "pier": "=", "rampart": "R", "stair": "s"}
    rows = []
    for r in range(min(rs), max(rs) + 1):
        row = " " * (r - min(rs))
        for q in range(min(qs), max(qs) + 1):
            k = tiles.get((q, r))
            if k is None:
                row += "  "
            elif k.startswith("bldg"):
                row += k[5].upper() + " "
            else:
                row += sym[k] + " "
        rows.append(row)
    return "\n".join(rows)

def compile_portal(focus=(0, 1)):
    """PortalStrike window: interior focus (the teleport_sigil lot — modelled
    as a modest building even though the default save leaves it unplaced).
    No perimeter opening: the wall is the FULL boundary; enemies erupt at the
    sigil ring, defenders muster at the far admitted lot. Asserts: anchors
    passable + mutually connected, in-arena stamp overlap."""
    all_lots = extract_window(focus)
    pos, parent = layout(all_lots, focus)
    admitted = [l for l in all_lots if hexdist(pos[l], (0, 0)) <= MAP_RADIUS]
    arena = set(disk((0, 0), MAP_RADIUS))
    tiles = {t: "ground" for t in arena}

    sigil_r = 2   # modelled modest
    for lot in all_lots:
        r = sigil_r if lot == focus else stamp_radius(lot)
        if lot in BUILDINGS or lot == focus:
            name = BUILDINGS[lot][0] if lot in BUILDINGS else "teleport_sigil"
            for t in disk(pos[lot], r):
                if t in arena:
                    tiles[t] = "bldg:" + name
        elif lot in admitted:
            for t in disk(pos[lot], 1):
                if t in arena:
                    tiles[t] = "plaza" if CAMPUS[lot] == "Plaza" else "lawn"

    region = set()
    for lot in all_lots:
        r = sigil_r if lot == focus else stamp_radius(lot)
        for t in disk(pos[lot], r + 2):
            region.add(t)
    for t in region:
        for d in DIRS:
            n = (t[0] + d[0], t[1] + d[1])
            if n not in region and n in arena and not tiles.get(n, "").startswith("bldg"):
                tiles[n] = "wall"

    # enemy anchor: first free ring tile around the sigil; player: farthest lot
    enemy_anchor = None
    for t in sorted(disk((0, 0), sigil_r + 1)):
        if hexdist(t, (0, 0)) == sigil_r + 1 and tiles.get(t, "") in ("ground", "lawn", "plaza"):
            enemy_anchor = t
            break
    assert enemy_anchor, "no free ring tile around the sigil"
    others = [l for l in admitted if l != focus]
    assert others, "portal window admitted only the sigil lot"
    # defenders muster at the farthest admitted lot — on its centre if the lot
    # is empty ground, else on a free ring tile beside its building stamp
    player_lot = max(others, key=lambda l: (hexdist(pos[l], (0, 0)), l))
    player_anchor = pos[player_lot]
    if tiles.get(player_anchor, "").startswith("bldg"):
        ring_r = stamp_radius(player_lot) + 1
        for t in sorted(disk(player_anchor, ring_r)):
            if (hexdist(t, player_anchor) == ring_r and t in arena
                    and tiles.get(t, "") in ("ground", "lawn", "plaza")):
                player_anchor = t
                break
    assert not tiles.get(player_anchor, "").startswith("bldg"), \
        "portal window: no standable defender anchor"

    passable = {t for t, k in tiles.items()
                if not k.startswith("bldg") and k != "wall"}
    assert enemy_anchor in passable and player_anchor in passable
    seen, stack = {enemy_anchor}, [enemy_anchor]
    while stack:
        c = stack.pop()
        for dq, dr in DIRS:
            n = (c[0] + dq, c[1] + dr)
            if n in passable and n not in seen:
                seen.add(n)
                stack.append(n)
    assert player_anchor in seen, "portal window: anchors disconnected"
    return tiles, pos, admitted, player_anchor, enemy_anchor


def breach_focus():
    """Perimeter lot for the wall-breach window: farthest (lattice) from the
    gate among lots with missing neighbors; deterministic tiebreak."""
    cands = [c for c in CAMPUS
             if any((c[0] + d[0], c[1] + d[1]) not in CAMPUS for d in DIRS)
             and c != GATE_LOT]
    return max(sorted(cands), key=lambda c: (hexdist(c, GATE_LOT), c))


def compile_gate_window():
    return compile_window(GATE_LOT, "door")


if __name__ == "__main__":
    tiles, pos, admitted, pa, ea, d_out = compile_gate_window()
    n = len(tiles)
    walls = sum(1 for k in tiles.values() if k == "wall")
    bldg = sum(1 for k in tiles.values() if k.startswith("bldg"))
    print(f"arena tiles: {n} (radius {MAP_RADIUS}); wall: {walls}; building: {bldg}; "
          f"open: {n - walls - bldg}")
    print(f"admitted lots: {admitted}")
    print(f"player anchor {pa}  approach dir {d_out}")
    print(render(tiles))
    print("ALL ASSERTS PASSED")
