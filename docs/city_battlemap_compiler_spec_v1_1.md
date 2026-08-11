# Fractured Arcana — City Battlemap Compiler Spec (v1.1)

**Written 2026-08-11 (v1.1 — reconciled against `campus_siege_and_defense_v1_1.docx`
and live code).** Companion to `battlefield_tactics_spec_v1.md` (recipe generator),
`combat_objectives_spec_v1.md` (O-track), `HANDOFF_phase2_campus_and_next.md` +
`session_log_2026-08-10_phase3_npc_city_view.md` (district lattice / P3.1), and
`world_locales_and_founding_spec_v1.md` (which reserved the settlement-combat recipe
names `city_streets` / `seat_walls` — this spec fills that reservation).

**Governing prior document:** `docs/campus_siege_and_defense_v1_1.docx` locked several
decisions before this spec existed. This spec **inherits them** and generalizes from
"defense of the campus" to "siege of any districted city" (home or NPC): two-arena
structure, three attack vectors, multi-hex variable-shape rotatable footprints,
destructible buildings, non-enterable interiors in v1, and the §4a combat tile
semantics. Where the docx references retired classes (`CampusHexGrid`) or predates
the /3 flower lattice and the 5 ft/hex scale ruling, THIS spec is current.

**Confidence key:** [H]/[M]/[L]. **[V]** = verified against live repo code 2026-08-11.

---

## 0. Rulings this spec is built on

Confirmed by Magos 2026-08-11 (this spec's sessions):
1. **One combat hex ≈ 5 ft.** All footprint math derives from this.
2. **Buildings are shells in v1** — impassable, LoS-blocking footprints; interiors
   later. (Consistent with the docx's locked "not enterable in v1".)
3. **A siege is a sequential window chain**, not one mega-map. Attacker wins an
   entry window, then fights inward.
4. **Zero new objective kinds in v1** — attacker windows resolve `annihilate`;
   `seize_zone` is the single flagged v2 addition (§7.2).
5. **Grand Hall is Seat-class** (r8–10). **Garrisons come from region siege pools**
   (`EncounterPoolLoader`, keyed by city faction). **Mid-chain retreat = full siege
   failure** (the `WarfrontStrongholdCleared` cleared-AND-extracted pattern).
   **Home-defense ships first**, before NPC-city sieges.

Locked earlier by `campus_siege_and_defense_v1_1.docx` (inherited here):
6. **Two-arena structure** — Walls Map + Town Map; town defense happens only if the
   outer contest is lost. Generalized in §6 as a 2-window defense chain.
7. **Three attack vectors** — wall/gatehouse siege, dock/skydock raid, portal strike
   (`EncounterContextCarrier` flags `WallSiege` / `DockRaid` / `PortalStrike`).
   This spec's gate-assault and wall-breach are the two *entry treatments* of
   `WallSiege`, not a fourth vector.
8. **Breach correspondence** — which wall segment falls determines the Town-Map
   entry edge (docx §2a). Generalized here as the vector→entry-edge rule (§5).
9. **Multi-hex, variable-shape, rotatable footprints; destructible buildings**
   (`Rotation`, `MaxIntegrity`, `CurrentIntegrity`, `IsDestroyed` on
   `BuildingSaveData`); destroyed footprint → rubble, not lawn.
10. **Combat tile semantics (docx §4a)** — footprint hexes set `IsBlocked = true`,
    `BlocksLineOfSight = true`, `ObstacleKind = "building:" + buildingId`, and
    **`IsWalkable` stays true** — `IsBlocked` is what gates entry (`CanEnter`), so
    a future enterable building just stops setting `IsBlocked`. Rule change avoided.

## 0.1 The core architectural decision

**The combat map is NOT a geometric child of the district lattice.** A second /3
tesselation makes buildings 7-hex pillars; a third makes district maps ~1,300 hexes
(AP economy, movement, intent ranges all tuned near ~91 tiles). Instead:

> The compiler is a **read-only function over the district lattice** that emits a
> standard battlefield **recipe JSON**, consumed by the existing generator.

This also supersedes the docx's implicit assumption that Town-Map campus tiles map
1:1 onto combat tiles — at 5 ft/hex that would make the Grand Hall one hex wide.
The lattice provides **lots and adjacency**; the stamp provides **combat-scale
shape** (§4). "True scale" = fixed footage per size class + preserved adjacency. [H]

Nothing in `CampusMapSaveData`, the lattice, or `WorldAtlas3D` changes. [V — read
in full; the adapter in §2 is purely a consumer.]

## 0.2 Verified live-code facts (2026-08-11) — assumptions retired

| Fact | Source | Consequence |
|---|---|---|
| `MoveRange = 2` tiles **per AP spent on movement**; AP is granted at spawn from TG tier — **3 AP standard in play, up to 5 with upgrades** (per Magos 2026-08-11; the `StartBaseSpeed = 2` export default is NOT the played value). Martials add `MartialAPCosts.AttackCost(range)` | `Unit.cs:93–98`, `CombatManager.cs` spawn sites; live ruling | Full sprint ≈ **6 tiles/turn typical, 10 at 5 AP** — and sprinting spends the turn. Flanking half-around Grand (r4, 12 hexes) = 2 sprint turns: tactically live. Crossing the radius-8 map ≈ 3 sprint turns: the envelope is comfortable. Deployment-near-contact is recommended discipline, not a structural necessity. |
| Generator has exactly ONE anchor pair (`PlayerLayoutAnchor` / `EnemyLayoutAnchor`); `flank:N` unimplemented | `HexGridManager.Recipes.cs:347–352` | Portal-strike "surrounded" topology CANNOT use multi-anchor spawns. Fallback is definitive: enemy anchor on the dominant lane + reinforcement waves entering from map-edge rows. No generator change in v1. |
| `PaintHeightRidge` writes `Height` (Max-composited); `PaintObstacleBand` writes `IsBlocked/IsWalkable/BlocksLineOfSight/ObstacleKind` | `HexGridManager.Generation.cs:434, 532` | Ridge + band on the same line compose — battlements work with shipped primitives. |
| `PaintObstacleBand` defaults `chance = 0.7` and sets `IsWalkable = false` | `HexGridManager.Generation.cs:532–545` | City WALLS must pass `"chance": 1.0` (a gap-toothed city wall is a connectivity lie). Building stamps must NOT reuse the band paint at all: it violates ruling 10 (`IsWalkable` must stay true). Stamps get their own paint (§4.3). |
| Building JSON already has `footprint` (offset list, single-hex today) | `Data/Buildings/*.json` | The docx's lattice-space footprint and this spec's combat-space stamp are SEPARATE fields; do not overload one for the other. |
| Phase 3 **P3.1 shipped**: NPC seat cities render as /3 districted regions (`GenerateCityLayout` reuses `CampusMapSaveData`); buildings/verbs are P3.2–P3.4 | `session_log_2026-08-10_phase3_npc_city_view.md` | The handoff's "Phase 3 NOT STARTED" is stale. `ICityCombatSource` gets its NPC implementation from P3.2 (buildings), not from scratch. |
| `gatehouse_yard` and `teleport_sigil` exist as placed buildings | `Data/Buildings/` | Gate and portal designations are building lookups, not new authored fields. Dock remains the `EntryDockType` wiring (docx §6 open question, now consumed here). |

---

## 1. Pipeline overview

```
district lattice (any city)          SiegeContext (vector, chain step, seed)
        │                                     │
        ▼                                     ▼
   ┌─────────────────────────────────────────────┐
   │  CityBattlemapCompiler (new, stateless)      │
   │  1. pick focus point (from vector/chain)     │
   │  2. extract window (content budget, §3)      │
   │  3. compile cells → recipe features          │
   │  4. stamp buildings → building paint (§4.3)  │
   │  5. attach objective + anchors + events      │
   └─────────────────────────────────────────────┘
        │
        ▼
   battlefield recipe JSON (same schema as bf_causeway / bf_cauldron)
        │
        ▼
   existing generator → existing CombatManager
```

Combat-side delta in v1 is exactly one addition: a `building_stamp` recipe op backed
by a `PaintBuildingStamp` that implements ruling 10's tile semantics (§4.3). Every
other op is shipped: `carve_lane`, `clearing`, `patch`, `obstacle_band` (kind "wall",
`chance: 1.0`), `ring`, `filled_radius`, `height_ridge`, plus map events
(`collapse_tiles`, `raise_tiles`). "Walls first, doors second" applies verbatim.

**Determinism:** compiler seed = `hash(settlementId, cycleSeed, chainStep)`. [H]

---

## 2. Input contract: what a "city" must provide

```
ICityCombatSource
  IEnumerable<DistrictCell> Cells       // fine axial (q,r), Ground, owner district
  CellKind KindOf(q, r)                 // Plaza | Lawn | Corner | Locked
  BuildingRef BuildingAt(q, r)          // null or blueprint id + rotation
  IEnumerable<(q,r)> PerimeterCells()   // city edge (wall band goes here)
  (q,r) SeatCell                        // grand_hall / archmage seat
  (q,r)? GateCell                       // gatehouse_yard's cell (null → no gate vector)
  (q,r)? DockCell                       // from EntryDockType wiring; null if landlocked
  (q,r)? TeleporterCell                 // teleport_sigil's cell (null → no portal vector)
```

- **Home campus adapter:** reads `CampusMapSaveData.Tiles/Districts` + `Ledger.Buildings`
  (`BuildingSaveData.Q/R` is the position source of truth [V]). Corner cells via
  `CornerOwners(q,r).Count == 3` [V]. Gate/teleporter = building lookups (§0.2).
  Dock = wire `EntryDockType` ("near water → Dock, else Skydock" — the docx §6
  question resolves here; Skydock keeps the vector but skins the entry edge as a
  sky-pier — **passive airship seam only**, per the DLC deferral).
- **NPC cities:** P3.1 gives the districted region; the adapter lands with P3.2
  (buildings in NPC cities). The compiler must not assume `EternalLedger` — NPC
  building sources come from wherever P3.2 puts them. [M — P3.2 unwritten.]

**Sequencing (ruled):** home-defense first — it exercises the full compiler with
shipped defender objectives (`hold_zone`, `protect`) before P3.2 exists, and it IS
the docx's Campus Defense encounter (Convergence Phase 2, "The Fracture"). [H]

---

## 3. Window extraction

- **Window** = a **content budget**: 1–2 showcase buildings (Grand/Landmark) +
  modest filler + the street break between them. The extractor walks the lattice
  from the focus cell outward, admitting lots until the budget is met.
- **Map envelope:** standard siege window ≈ **radius 8 hexagon (~217 tiles)**; the
  seat window up to radius 9–10 (271–331) because the Seat stamp dominates it.
  ~2.5–3× the 91-tile standard — a siege-only cost. At ~6 tiles/turn sprint
  (3 AP × MoveRange 2, §0.2) the envelope crosses in ~3 turns — viable without
  heroics. Discipline still applies: deployment zones near first contact, enemy
  pressure as waves rather than marches. If playtests drag, shrink the window
  budget before touching AP economy. [M on radii; H on the discipline.]
- **Map edge:** boundary cells compile to impassable rubble/wall ring except the
  attacker's entry edge (ruling 8's breach correspondence: the entry edge is the
  side whose wall segment / dock / breach the previous arena decided). The city
  continuing beyond the edge is backdrop, not tiles. [H]

---

## 4. Cell compilation rules

### 4.1 Lots and building size classes

A district cell is a **lot** — an address preserving adjacency. The building's
stamp declares its own combat-scale shape. Size classes give the default shapes;
authored shapes (docx ruling 9: variable, rotatable) override per building.

| Class | Default stamp radius | Footage (5 ft/hex) | Reads as | Per window |
|---|---|---|---|---|
| **Modest** | 2 | 25 ft | shop, cottage, stall row | filler, 2–4 |
| **Grand** | 4 | 45 ft | tavern, smithy, guildhall | the showcase — 1–2 |
| **Landmark** | 6 | 65 ft | temple, barracks, hall | 1, centerpiece |
| **Seat** | 8–10 | 85–105 ft | grand_hall, keep — Globe-class | final window ONLY |

**Calibration anchor:** the Globe Theatre (~100 ft outer diameter, ~33 ft tall,
~3,000 capacity) is the *ceiling*, not the standard. Maneuver math: a hex ring of
radius r has 6r cells → flanking half-around costs ~3r hexes. At ~6 tiles/turn
sprint (§0.2), Grand (12 hexes) is a 2-turn flank — tactically live; Landmark (18)
is a 3-turn commitment; Seat (~30) is not flanked, it IS the objective.
[H geometry; M class boundaries — playtest.]

Stamps may overhang their lot into neighbouring admitted lots; overlap resolves by
street-seam displacement (min 2-hex street [M]), never by shrinking a stamp.

### 4.2 Terrain rules (skeleton phase)

| District cell | Compiles to |
|---|---|
| Plaza | `clearing` — open ground, the arena beats |
| Lawn (no building) | `patch` grass |
| Street seams | `carve_lane` between stamps, width 1–2 — chokepoint discipline for free |
| Locked / not-unlocked | rubble (`obstacle_kind` "rock") or omitted |
| Corner cells | as owner terrain; incomplete corners → rubble |
| City perimeter in window | `obstacle_band` kind "wall" **`chance: 1.0`** [V — default 0.7 is a connectivity lie for city walls] + `height_ridge` battlements [V — composes] |

### 4.3 Building stamps

New per-blueprint field in `Data/Buildings/*.json` (additive; the existing
lattice-space `footprint` field is untouched and NOT reused for this):

```json
"combatStamp": {
  "sizeClass": "grand",
  "shape": null,
  "doors": [ { "dir": 3 } ],
  "height": 2,
  "tags": ["stone"]
}
```

- `sizeClass` → default hex stamp per §4.1. `shape` (optional) = authored offset
  list in combat hexes for irregular outlines (L-shapes, courtyards), rotated by
  the instance's `BuildingSaveData.Rotation` (docx ruling 9). `"seat"` allows an
  explicit `"radius"` override.
- **`PaintBuildingStamp` (the one new combat-side paint) implements ruling 10
  exactly:** `IsBlocked = true`, `BlocksLineOfSight = true`,
  `ObstacleKind = "building:" + buildingId`, **`IsWalkable` untouched**. It must
  NOT reuse `PaintObstacleBand` [V — the band sets `IsWalkable = false`, which
  forecloses the interiors flag-flip]. `height` raises the footprint's `Height`
  for silhouette + high-ground reads.
- `doors` are **parsed, validated, and compiled as wall in v1** — interiors later
  become a flag flip + stamp-interior data, never a JSON migration. [H]
- Missing `combatStamp` → default `"modest"`, no doors, warn at load (loud at load
  time, not fight time).
- **Destruction hook (ruling 9, not built in v1):** `ObstacleKind` carries the
  building id precisely so integrity/destruction can land later; a destroyed
  building's footprint converts via the existing "rubble" terrain modifier
  (`ApplyTerrainModifier`) — blocked ground with identity, not lawn. The compiler
  reads `IsDestroyed` when set and stamps rubble instead of walls. [M — field
  timing owned by the docx's §5 schema work.]

**Explicitly deferred:** enterable interiors, wall-edge occlusion, roof peeling,
garrison-inside-buildings, integrity combat (damaging buildings mid-fight).

---

## 5. Attack vectors (docx ruling 7, generalized)

Vector enum reuses the docx's `EncounterContextCarrier` flags. Every vector = a
focus rule + entry-edge treatment + recipe flavor + objective binding. Attacker
windows resolve `annihilate` (ruling 4); defender bindings are the home-defense set.

| Vector | Focus | Entry treatment | Signature elements | Defender objective |
|---|---|---|---|---|
| **WallSiege — gate assault** | `GateCell` | wall band with door gap at the gate | intact walls both sides, `height_ridge` battlements, killing-ground `clearing` inside | `hold_zone` on the gate tiles |
| **WallSiege — breach** | perimeter cell chosen at the strategic layer (where the engine fired / walls arena was lost) | wall band with **rubble gap**, breach pre-made | rubble scatter at the gap (cover, not wall); optional scripted second breach via telegraphed `collapse_tiles` [M — cut freely] | `hold_zone` on the breach |
| **DockRaid** | `DockCell` | open water edge attacker-side; piers as lanes over water | bf_causeway water palette; pushes toward water are lethal tempo; Skydock skins the edge as sky-piers (passive airship seam) | `hold_zone` on pier heads |
| **PortalStrike** | `TeleporterCell` — interior window, skips the perimeter | no entry edge; attacker anchor at the sigil clearing | `map_object` teleport circle; **surrounded via waves, not anchors** [V — single anchor pair; enemy anchor on the dominant lane + wave rows from map edges] | `protect` the sigil keystone, or `survive` until sealed |

Availability is diegetic: no `teleport_sigil` → no PortalStrike; landlocked → no
DockRaid; WallSiege always available where walls exist. The portal-code discovery
mechanic (how attackers get PortalStrike against YOU) stays open in the docx §6 —
espionage-layer question, out of scope here.

---

## 6. The siege chain (generalizes the docx two-arena model)

1. **Defense of the home campus** = the docx structure exactly, expressed as a
   2-window chain: **Walls window** (outer contest) → if lost, **Town window**
   (entry edge per breach correspondence). Winning the walls window ends the siege
   — the town map never loads. [Inherited, ruling 6.]
2. **Attacker sieges (NPC cities)** = the same chain generalized: entry window
   (vector rules, §5) → interior street windows (`city_streets` flavor) along the
   shortest lattice path entry-district → seat-district (ties by seed) → seat
   window (`seat_walls` flavor, Seat-class stamp dominant). City size buys chain
   length: a 3-district town is 2 windows; a capital 4–5. [H]
3. **Between windows:** expedition-level persistence only (HP, resources,
   companions). No mid-siege healing rule in v1.
4. **Chain outcome:** all windows won → city taken (strategic consequence owned by
   the kingdom/warfront layer). Retreat or loss mid-chain → **full siege failure**
   (ruled) — the `WarfrontStrongholdCleared` cleared-AND-extracted pattern.

### 6.1 State (the two-struct budget)

```
CombatStampDef          // blueprint data, NOT save-adjacent (building JSON)
SiegeChainState         // save-adjacent → round-trip assertion REQUIRED pre-ship
  string SettlementId          // stable id — NEVER a mutable list index
  EncounterVector Vector       // WallSiege | DockRaid | PortalStrike (docx enum)
  (int q, int r) EntryDistrict // stable axial coords
  int ChainStep
  int WindowsWon
  ulong Seed
```

The docx's own §5 schema additions (`Rotation`, integrity fields on
`BuildingSaveData`; `WallsMapSaveData`) belong to that document's work stream and
do NOT count against this feature's two-struct budget — but this compiler consumes
`Rotation` when present and treats the rest as absent-tolerant. [H]

Save integration: additive-field + lazy-backfill on `CycleState`; no version bump.

---

## 7. Non-goals and the v2 line

### 7.1 Not in v1
Enterable interiors (ruled); new objective kinds (ruled); NPC-city sieges before
P3.2 lands buildings; ship-to-ship anything (naval scope: traversal + boarding
only — DockRaid is a land battle with a wet edge); whole-city single maps;
building-integrity combat; any city-side lattice/atlas/save-schema change.

### 7.2 The single flagged v2 objective addition
`seize_zone` — attacker mirror of `hold_zone`: victory when a player unit holds
the zone at N round boundaries. Wanted for "take the gatehouse without killing
every rat in the district." Do not build until a playtest shows `annihilate`
windows dragging. [M needed eventually; H that v1 ships without it.]

---

## 8. Build order

1. **`ICityCombatSource` + home adapter** — building lookups for gate/sigil, dock
   wiring for `EntryDockType`, corner detection via `CornerOwners`. Python
   geometry checks on lot→stamp layout (C# `%` truncates toward zero — the
   `CornerOwners` guard already handles this correctly for negatives only because
   it tests `!= 0`; keep the same care).
2. **Compiler core** — window extraction + terrain rules, emitting recipe JSON.
   Headless-testable: feed the campus lattice, diff emitted JSON against
   hand-authored expectation. No engine needed.
3. **`combatStamp` loader + `PaintBuildingStamp` + `building_stamp` recipe op**
   (§4.3) — the one combat-side addition. First in-engine milestone: walk a
   combat map that is recognizably your campus. Compile in Godot; screenshot.
4. **Vectors** — WallSiege/gate first (all-shipped primitives), then breach
   (rubble variant), DockRaid (water palette), PortalStrike last (wave topology).
5. **`SiegeChainState` + chain driver** — round-trip assertion in the same
   increment, not owed.
6. **Home-defense siege** (Walls→Town chain, shipped defender objectives) — first
   shippable siege; this IS the docx's Campus Defense / Fracture encounter.
7. **P3.2+ NPC adapter → attacker sieges** — compiler already done; P3.2 buildings
   satisfy the interface.

Each step compiles and screenshots before the next; build output is a hard gate.

---

## 9. Open questions

Resolved since v1: ~~multi-anchor support~~ (single pair [V] — waves fallback is
definitive); ~~wall+height composition~~ (composes [V]); ~~gate designation~~
(gatehouse_yard building [V]); ~~movement values~~ (MoveRange 2, AP=BaseSpeed [V]).

Still open:
1. **Window content budget, size-class boundaries, street-seam width** — playtest
   constants. Includes verifying AI intent planners don't degrade on 250+-tile
   maps before committing the radius-8 envelope (planner code not yet reviewed).
2. **Size-class assignment for the 13 buildings** in `Data/Buildings/` — I propose
   a full table for veto once the `combatStamp` field lands (grand_hall = seat is
   ruled; gatehouse_yard/teleport_sigil are functionally special).
3. **Walls Map arena** — the docx leaves open whether it's authored
   (`WallsMapSaveData`) or derived from Gatehouse tier. The chain driver (§6)
   consumes either; the walls window's recipe is a perimeter-focused window
   either way. Decision owned by the docx work stream, needed before build step 6.
4. **Breach-to-edge authoring format** (docx §2a) — needed before build step 6;
   simplest candidate: the walls window records the breached perimeter cell into
   `SiegeChainState.EntryDistrict` + entry edge, no separate authored table. [M]
5. **Destruction consequences** (docx §6) — repairable vs. lost vs. Tier 0; only
   the rubble-stamp hook (§4.3) is consumed here.
6. **NPC building sourcing** (P3.2) — where NPC-city buildings live; the adapter
   contract is written to tolerate either a ledger-like store or generated sets.
