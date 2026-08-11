# Session log — 2026-08-11 (part 2) — Gate defense, the door, the breach, and the city beyond the rim

Continues `session_log_2026-08-11_city_battlemap_compiler.md` (increments 1–5:
compiler, walls, anchors). This part: increments 6–10 — the hold_zone defense
encounter, the destructible gate door, the wall-breach vector, and the full-city
backdrop. **ALL VERIFIED IN-ENGINE** across repeated compile+launch loops; the
proto (`tools/city_compiler_proto.py`) remains the geometry authority and now
covers both windows and both opening styles.

## Rulings made this session (Magos)
- **The door is destructible, not toggleable.** No open/close verb exists
  anywhere — deliberately: every action in this game is a card or a move, and
  an interact verb (or a "Seal the Gate" card) fights the architecture. The
  door is bought time; attackers batter it down. Sally-port open/close via a
  winch TILE (stand on it to toggle) is the flagged later pass.
- **The gate spans the full 3-tile face** (2 tiles left a mousehole beside the
  panels — the wall contour steps diagonally at the doorway).
- **The city continues past the arena edge** (backdrop), and wall heights
  follow the 5 ft/hex scale: 1.7-unit walls read as garden fencing once the
  full city was visible.

## What shipped

### hold_zone (O4) runtime — was specced, NOT built (increment 6)
`CombatManager.Objectives.cs`: breach counting (one per round-end with ≥1
living enemy on a zone tile; `breaches > BreachLimit` → defeat latch), lazy
zone build (grid doesn't exist at InitObjectiveState), banner suffix
`breaches n / limit`. `IsImplementedKind` += hold_zone — the loader gate
opened itself, zero loader changes (the O-track's belt-and-braces paid off).
New `ZoneAnchor: "gate"` reads the compiled recipe's gap. Defense orientation:
`CompileGateAssault(defending: true)` swaps anchors; launcher entry
"campus_gate DEFENSE" attaches hold_zone (Rounds 8 / BreachLimit 2 /
ZoneRadius 2 — debug values).

### Zone indicator (user request)
`MovementZoneRenderer.Objective.cs` — persistent GOLD border in the XCOM
wall-outline grammar, on its OWN mesh/material (movement-zone churn can't
erase it), solid not dash-animated (a fact, not a preview), NoDepthTest.
Show attempt rides `RefreshObjectiveBanner` (re-fires every phase change) with
a latch → lands during deployment, not at the round-2 boundary.

### Defense geometry fixes (increment 7 — caught by Magos playtest)
- Zone is **compiler-computed, inside-only** (`objective_zone` in the siege
  block; runtime prefers it): the runtime BFS spread OUTWARD through the door,
  letting besiegers "breach" from the approach. Proto assert: zone disjoint
  from the door-SEALED outside flood (with the door open, outside floods the
  whole map — the distinction only exists sealed).
- Defender anchor = the **alley courtyard at gateOuter's tile** — NOT
  "gateInner", which is the far side of the gatehouse SHELL (a defender there
  musters 8 hexes from the door it must hold).

### The gate door (increment 8)
`Data/Units/gate_door.json` (28 HP / 3 armor / speed 0 / hits nothing; role
"summon" — registry validation only knows line/elite/boss/summon) spawned
per gap tile via the team-0 `SpawnRegistryUnit` path (the Necromancer-risen
precedent). **Enemy AI needed zero changes**: a team-0 unit in the doorway IS
the nearest player unit (`FindNearestPlayerUnit`); brutes
(`melee_target_highest_hp`) prioritize it. New `Unit.IsStructure` excludes it
from: the all-players-dead defeat scan (a standing door is not a survivor),
unit-bar selection (visible with HP, not commandable), the player centroid,
and deck init (**it was dealt a starter deck and drew cards** — playerUnits
iteration knew nothing of structures). Doors spawn only when
`SiegeSpec.Defending && Entry == "gate"`; attacking finds the gap open
(breach forced pre-fight — enemy-team structures need AI exclusions we
haven't built; deliberate). Visual: tall thin BoxMesh slab (1.7×2.6×0.45)
rotated along the doorway line (gap contiguity is compiler-asserted, so every
panel has a neighbor to align to); only the body mesh rotates — labels/HP bar
keep orientation.

### Doorway spawn guards (increment 8 fix — tripwire-caught)
The zone-leak fix chain, each caught by a loud error rather than playtest
archaeology: (1) **`BuildSpawnZone` runs its own self-contained BFS** — the
increment-5 `GetSideCandidates` guard was DEAD CODE for zone building (classic
wrong-seam fix); doorway tiles are now excluded from claim AND traversal
there. (2) The shortfall-widening in `SpawnAndPlaceEnemies` had the same hole
and runs before doors exist (occupancy can't protect the gap) — same guard.
(3) `SpawnGateDoors` PrintErr's on an occupied gap tile instead of silently
degrading — that tripwire is what caught (1) and (2).

### Wall-breach vector (increment 9)
Compiler refactor: `CompileWindow(city, seed, focus, opening, defending,
mapRadius)` core; `CompileGateAssault` and `CompileWallBreach` are thin
wrappers. Breach focus = perimeter lot farthest from the gate (deterministic
tiebreak) — where the wall is least watched; campus: the armory flank (4,0).
Opening "rubble": no doors; 2 rock-cover tiles FLANK the opening (adjacent to
exactly one gap tile — never the central lane; proto-asserted to never
re-seal) + a 3-tile collapsed-masonry debris field OUTSIDE (approach cover).
Launcher: "campus_breach (compiled)", attack orientation. Recipe ids:
`city_wallsiege_{gate|breach}[def]_{seed:x8}`.

### Ground dressing (increment 9.5 — "missing detail" feedback)
Plazas pave (stone patch under the clearing); lawns get a grass apron with a
forest-grove accent core. Watch: if stone tiles read as "Mountain" at combat
zoom, swap the plaza terrain.

### Full-city layout + backdrop (increment 10 — "walls in a field" feedback)
The compiler lays out the ENTIRE lattice; the arena radius defines the window.
Consequences (all proto-verified): edge-straddling buildings paint their
in-arena tiles (the Sanctum clips over the gate map's rim); the wall region
derives from the FULL city so phantom interior arcs vanish (gate window: 21
scattered wall tiles → 9 at the true perimeter); pairwise stamp-overlap is
asserted ONLY for in-arena pairs — backdrop-side lots placed via different
parent chains may merge, and merged masses beyond the wall are dense-city
fiction (arena navigability is guarded by the connectivity asserts, not
pitch). `MaxLots` deleted. Siege block gains `backdrop_wall` (curtain
continuing past the rim, capped 2× map radius) and `backdrop_stamps`
(position/radius/id); `SpawnSiegeBackdrop` (CityStamps partial, hooked into
`SpawnObstacleVisuals`) renders them as decorative prisms at `AxialToWorld`
positions beyond the tiles — no TileData, no collision, `generated_obstacle`
group for standard cleanup.

### Height pass (increment 10.5)
Hierarchy at 5 ft/hex: curtain wall 3.2 (~16 ft), building shells 3.8 (city
tops its wall), door slabs 2.6 (fills the arch, below the parapet —
deliberately), backdrop wall 6.5 deep-based, backdrop masses 4.6 + r·0.5.

## Files touched this part
New: `MovementZoneRenderer.Objective.cs`, `CombatManager.SiegeDoors.cs`,
`Data/Units/gate_door.json`.
Patched: `CombatManager.Objectives.cs` (O4), `CombatObjectiveDef.cs`,
`CombatManager.cs` (defeat scan, unit bar, centroid, deck init, widening
guard, door spawn call), `HexGridManager.Spawns.cs` (BuildSpawnZone doorway
guard), `HexGridManager.CityStamps.cs` (accessors, backdrop spawner, heights),
`HexGridManager.Visuals.cs` (backdrop hook), `MapRecipe.cs` (SiegeSpec:
Defending, ObjectiveZone, BackdropWall/Stamps), `CityBattlemapCompiler.cs`
(defense anchors, zone, CompileWindow refactor, breach, dressing, full-city
layout, backdrop emission), `CombatDebugLauncher[.CityGate].cs` (3 entries),
`tools/city_compiler_proto.py` (parameterized windows, opening styles, zone +
debris + contiguity asserts, full-city layout).

## Test procedure
Debug launcher → Force battlefield:
- **campus_gate DEFENSE (hold the gate)** — gold zone on door+courtyard, 3
  door slabs barred (`[SiegeDoors] 3 ...`), banner "Hold the gate — round
  1/8 · breaches 0/2", enemies OUTSIDE the wall, backdrop city beyond the rim.
- **campus_breach (compiled)** — "The Breach": rubble-flanked opening on the
  armory flank, debris field on the approach, no doors, city bulk in backdrop.
Loss path check: let enemies camp the zone two round-ends → breach 1, 2,
defeat on the third.

## Open / next
1. Tuning trio: door HP 28, Rounds 8, BreachLimit 2 (+ waves checkbox for
   pressure). Does holding FEEL like holding?
2. Ranged-enemy-vs-door behavior unverified (they may idle when the wizard is
   out of range behind the door) — playtest, then planner tweak if needed.
3. Wall occlusion at 3.2 height from low camera pitch — if tactical info
   hides, apply the canopy-occlusion treatment, don't shrink the wall.
4. Reactive formation-mirroring spawner: the `[SiegeDoors]` occupied-tile
   tripwire is still armed if it ever leaks through the doorway.
5. Vectors remaining: DockRaid (needs `EntryDockType` wiring), PortalStrike
   (wave-entry topology — single anchor pair confirmed).
6. `SiegeChainState` + chain driver (Walls→Town for defense; entry→seat for
   assault). Save-adjacent: round-trip assertion in the same increment.
7. `combatStamp` authoring pass (Magos, "next week") — retire the compiler's
   placeholder `BuildingClass` table.
8. Real models: door slab, wall segments, building masses — all placeholder
   prisms are marked art-pass swap points.
