# Session log — 2026-08-11 — City battlemap compiler (district map → siege combat)

Spec: `docs/city_battlemap_compiler_spec_v1_1.md` (written and revised this same
session). Governing prior doc: `docs/campus_siege_and_defense_v1_1.docx` (locked:
two-arena, three vectors, shell buildings, §4a tile semantics). **VERIFIED
IN-ENGINE**: the WallSiege gate-assault window of the home campus compiles from
live save data and plays — walls, gatehouse shell, one door, correct deployment
(`[SpawnPlan] Player anchor: (-6, 0), Enemy anchor: (0, 6)` confirmed in console).

## Rulings made this session (Magos)
- One combat hex ≈ **5 ft**; building size classes Modest r2 / Grand r4 /
  Landmark r6 / Seat r8–10 (Globe Theatre ≈ ceiling, not standard). Ratified as
  starting values; per-building authoring pass planned "next week".
- Buildings are **shells** v1 (impassable, LoS-blocking; interiors later).
- Siege = **sequential window chain**; entry vectors gate / breach / docks /
  teleporter (docx enum WallSiege / DockRaid / PortalStrike).
- Grand Hall = Seat class; garrisons from region siege pools; mid-chain
  retreat = full siege failure; **home-defense ships first**.
- AP correction: MoveRange 2 is per-AP; **3 AP standard, 5 upgraded** → sprint
  ≈ 6–10 tiles/turn (the `StartBaseSpeed = 2` export default is NOT the played
  value — TG tier grants AP at spawn).

## What shipped (all compiled + launched by Magos)
**New files**
- `tools/city_compiler_proto.py` — numeric geometry reference. Replicates the
  /3 flower lattice (22/22 vs `GenerateDefault`), window extraction, lot
  layout, walls, gate, asserts (overlap, seal/partition, connectivity). **Keep
  in lockstep with the C# compiler — it is the constructor's proof.**
- `Scripts/Systems/Strategic/CityCombat/ICityCombatSource.cs` — read-only city
  contract (fine-lattice axial; same coord family as combat — no conversion
  anywhere in the pipeline).
- `Scripts/Systems/Strategic/CityCombat/HomeCityCombatSource.cs` — campus
  adapter: `CampusMap.Tiles` + `Ledger.Buildings` (Tier>0 && IsPlaced gate,
  same as `CampusGridManager.LoadFromSave`), corners via `CornerOwners`==3,
  gate=`gatehouse_yard`, portal=`teleport_sigil`, dock=null until
  `EntryDockType` wires.
- `Scripts/Systems/Strategic/CityCombat/CityBattlemapCompiler.cs` — Godot-free;
  `CompileGateAssault(city, seed)` → MapRecipe-schema JSON + spawn geometry.
- `Scripts/Systems/Combat/Terrain/HexGridManager.CityStamps.cs` —
  `PaintBuildingStamp` (docx §4a EXACTLY: IsBlocked, BlocksLineOfSight,
  ObstacleKind="building:"+id, **IsWalkable stays true** — the interiors
  flag-flip; do NOT reroute through PaintObstacleBand/RecipeTileApplier, both
  set IsWalkable=false) + `SpawnCityObstaclePlaceholder` (hex prisms; grey
  walls, char-sum-hash tint per building — NOT string.GetHashCode, which is
  per-process randomized).
- `Scripts/Dev/CombatDebugLauncher.CityGate.cs` — "campus_gate (compiled)"
  Force-battlefield entry; fixed seed 0xC1717E; hard-fails to status label.

**Patched (surgical)**
- `HexGridManager.Recipes.cs` — `building_stamp` dispatch case.
- `MapRecipeRegistry.cs` — `Register()` (EnsureLoaded first so lazy load can't
  Clear a runtime recipe).
- `HexGridManager.Visuals.cs` — placeholder branch for "wall"/"building:*"
  (pre-existing gap: the obstacle switch only knew rock/crystal — "wall" was
  ALWAYS invisible; our recipe was just the first to hit it).
- `MapRecipe.cs` — `SiegeSpec` (additive; null on hand-authored recipes) parsed
  from the recipe's `siege` block: vector, entry, anchors, gate gap.
- `HexGridManager.Spawns.cs` — `DetermineLayoutAnchors` override from
  SiegeSpec; siege spawn candidates = depth-3 WALKABLE flood (not raw
  distance: raw distance leaks through the wall, and zones on wall tiles would
  be bulldozed by `EnsureReservedTilesArePlayable`). `EnsureConnectivity`
  needed no change — BFS respects IsBlocked and the door path exists, so the
  carve never fires.

## Key design/geometry decisions (why, not just what)
1. **No literal re-tesselation.** /3 again = 7-hex buildings (unenterable
   ever); /9 = ~1,300-hex maps (breaks AP economy). The compiler is a
   read-only function district-lattice → recipe; "true scale" = fixed footage
   per class + preserved adjacency.
2. **Lots, not cells.** A district cell is an address; the stamp declares its
   own radius (size class). Stamps may clip at the arena edge (assault the
   Seat's façade; the building continues into the backdrop).
3. **Wall = region boundary (v3).** City region = union of (stamp+2) disks
   (+2 makes adjacent lots' disks provably overlap, pitch rA+rB+3 < rA+rB+4);
   wall = the region's outer boundary — closed, 1-thick BY CONSTRUCTION,
   clipped at arena edge; patrol alley falls out between wall and stamps.
   Discarded on assert/in-engine evidence: per-lot face segments (fragmented),
   flood-adjacency shell (2–3-thick blobs), minimal-prune (over-pruned to a
   11-tile fence — topology-minimal ≠ fiction).
4. **The gate goes AROUND the gatehouse.** A shell can't be walked through;
   the door (2 boundary tiles nearest the outward ray, explicit q,r tiebreak —
   symmetric float ties WILL differ between Python and C# otherwise) opens
   into a ring courtyard around the gate structure. Kill-pocket by accident,
   kept on purpose.
5. **Perimeter = missing lattice neighbors.** Outward direction and wall
   placement derive from where the lattice ends (locked districts), not from
   centroid heuristics (which put the gate district's own plaza outside the
   walls). Generalizes to NPC cities unchanged.
6. **Wall ops emitted per-tile** (`filled_radius` radius 0, chance 1.0) — the
   contour is snake-shaped; also `PaintObstacleBand` BREAKS at the first
   missing tile, so band runs crossing the arena edge silently truncate.

## Verification discipline used
Python proto first for ALL geometry (asserts: stamp overlap, gate-only
partition via flood, street connectivity); C# ported only after asserts green;
proto↔C# cross-checked to the EXACT tile set (22 walls); braces/parens balance
+ symbol greps before every handoff; Magos compiled + screenshotted each
increment (5 in-engine launches this session).

## Test procedure (repeatable)
Debug launcher → Force battlefield: **campus_gate (compiled)** → Launch.
Expect: radius-8 hexagon, 22-tile grey curtain with 2-tile door at the west
lane, 19-tile tinted gatehouse cluster inside, player deploys at the approach
(-6,0), enemies behind the wall at the plaza (0,6). Console:
`[CityCompiler] forced 'city_wallsiege_gate_00c1717e': walls=22 stampTiles=19
gap=2` and `[SpawnPlan]` echoing the same anchors.

## Known gaps / next (in value order)
1. **`hold_zone` objective on the gate gap** — home-defense encounter def;
   O-track machinery ships already, this is encounter JSON + context wiring.
2. **Wall height/battlements** — curtain tiles are flat prisms; add `height`
   dressing so walls read at combat zoom (raise_tiles or stamp height path).
3. **Breach vector** — rubble gap instead of door; mostly a variant flag in
   the compiler (skip gate, pick perimeter cell, rubble scatter ops).
4. **Docks / Portal vectors** — docks needs `EntryDockType` wiring; portal =
   interior window + wave-entry (single anchor pair confirmed in generator —
   "surrounded" comes from waves, not anchors).
5. **`SiegeChainState` + chain driver** — save-adjacent: round-trip assertion
   in the same increment, stable ids/coords only (PrisonPoiIndex lesson).
6. **`combatStamp` authoring pass** — size classes per building JSON (Magos,
   "next week"); compiler's `BuildingClass` table is the placeholder to retire.
7. **Real wall/building models** — placeholder prisms are the art-pass swap
   point (`SpawnCityObstaclePlaceholder`).
8. NPC cities: P3.2 buildings → second `ICityCombatSource` implementation;
   compiler needs nothing new.
