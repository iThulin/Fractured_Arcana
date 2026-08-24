# Session log — 2026-08-24 — Stride Orders F8d: exploratory march into fog

Fixes the design gap you flagged: stride could only reach charted ground, so it was
a return/reposition tool, never an exploration one — and with redeploy landing you
at secured staging points, there was little reason to long-travel at all. Now you
can order the fortress toward the **unknown**, and stride becomes the exploration
verb. Static-verified (no .NET SDK here); the blind-march logic checked in Python.

## What changed

A stride goal may now be a **fog (Hidden) tile**, not just charted ground. The
march chooses its next tile fresh every step:

1. **Charted-ground routing** — A* to the goal over the real fuel cost, routing
   around known POIs/hazards (unchanged behaviour when the goal is known).
2. **Blind advance** — when the goal is in fog / unreachable by charted ground, the
   castle steps toward the bearing: the passable neighbour (loaded, not water, not
   an immediate backtrack) that most reduces hex distance to the goal, revealing
   tiles as it goes. As fog lifts near an obstacle, the next step's A* can route
   through a newly-revealed gap automatically.

Because the next tile is recomputed each step, the march flows from blind advance
back into precise routing the moment the ground ahead becomes known.

## Halts (unchanged + new)
Arrival; an encounter on the reached tile; a KNOWN encounter on the next tile
(fog tiles are unknown — that's the risk you accept); out of fuel; Hull ≤ 25%;
ambush / run-end; player cancel. New: **lost the bearing** — if several blind steps
pass without getting any closer than the best distance achieved, the march halts
rather than wandering (bounded, verified no-loop in Python).

## Preview
- Charted goal: solid pin ribbon + "~N fuel" (as before).
- Fog goal: a **dashed bearing line** from the castle to the target with a "March
  into the unknown" label — honest about the uncertainty (no fuel number, since the
  route isn't known yet).

## Edits
- `ExpeditionManager`: `BeginStride` now accepts any loaded, non-water goal
  (fog included); dropped the precomputed path queue for a dynamic
  `TryNextStrideTile` (charted A* → blind greedy) + `BlindStridePassable`; march
  progress tracking (`_strideLastTile` / `_strideBestDist` / `_strideStuck`) to
  bound wandering; `ShowStridePreview` renders the dashed bearing for fog goals.
- `ExpeditionWindow3D.ShowStridePath` gained an `exploratory` mode (dashed line +
  "into the unknown" label).

## Verification
- Brace/paren/bracket balance = 0; no dangling queue refs; no em dashes in the new
  player-facing strings.
- Python march sim: reaches an open goal; halts gracefully ("lost") on a barrier
  via the stuck counter instead of looping. (The real game does better than the sim
  because fog reveal lets the charted A* route through gaps once seen.)

## F8d acceptance — confirm in-editor
- Hover a fog tile: a dashed "March into the unknown" bearing appears (no fuel #).
- Click it: the castle paths across known ground, then presses on into the fog,
  revealing as it strides, halting on the first surprise (encounter/hazard/ambush)
  or when it loses the bearing. Charted-goal strides behave exactly as before.

## Stride Orders (F8) now covers: preview · execution · momentum/lock · exploration.
## Still open: F3 castle types; F1/F2 rulings; F6 ambush (uses SavedStrideAmbush).
