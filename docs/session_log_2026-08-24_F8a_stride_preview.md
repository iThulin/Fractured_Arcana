# Session log — 2026-08-24 — Stride Orders F8a + marker fixes

Two things this pass: fixed the move-marker complaints from playtest, and built
**F8a** — the first slice of Stride Orders (§3.4): the A* planner + the hover path
preview. Execution (F8b) and momentum/Grimoire-lock (F8c) follow. Static-verified
(no .NET SDK here); A* checked numerically in Python.

## Marker fixes (playtest feedback)
- **Move-cost rings clipped into terrain.** They rendered with normal depth on flat
  meshes, so undulating/sloped tiles poked through. Now the ring + underlay use
  `NoDepthTest` (the same treatment the POI pins already use) and float a touch
  higher — they read as UI pins over the map, never welded to a slope.
- **Scry could hide adjacent move costs.** Weather scry −2 dropped the reveal radius
  to 0, hiding the tiles you can step into. Both reveal paths now floor at **1**, so
  the castle always scries its own tile + the adjacent ring (weather blinds the far
  lens, never the near one).

## F8a — planner + preview

### `StridePlanner.cs` (new)
Pure A* over the fuel-cost field, fully delegate-driven (no node dependency), so the
preview and the future execution call the ONE function (G1). Also `FuelEstimate`
with the momentum discount folded in (from the 4th step, −1, floor 1) for the
preview number. Verified in Python: routes around blocked tiles and around
POI-penalty tiles, reaching the goal from an unpenalized approach.

### `ExpeditionManager` — plan + hover wiring
- `StrideOrderable(local)`: in-window, not water, not Hidden (Silhouette IS
  orderable). The lens cannot command unscried ground; a Hidden goal is unorderable.
- `StrideEdgeCost(from,to)`: the SAME `OverworldMovementCost.StepCost` the live move
  charges (weather surcharge included), with Silhouette planned at a pessimistic
  flat 2 and a `+6` POI penalty on every tile except the goal (route around
  encounters; a POI destination is ordered normally).
- `PlanStride(goal)` → `StridePlanner.Plan` with `_grid.GetNeighbors`,
  `_grid.Distance` heuristic (admissible — min edge cost 1).
- On 3D hover, `ShowStridePreview` plans to the hovered tile, converts the path to
  world coords, and hands the window the ribbon + fuel estimate; cleared on unhover.

### `ExpeditionWindow3D` — the ribbon
- `ShowStridePath(worldPath, fuel)` / `ClearStridePath()`: a dotted line of
  NoDepthTest pins along the path (bigger pin at the goal) with a "~N fuel" label
  floating over the goal. Cleared when the party moves (stale path invalidated in
  `RebuildMoveHints`).

## Verification
- Brace/paren/bracket balance = 0 on all touched files.
- `PriorityQueue` confirmed already used across the repo (net8.0).
- Cost function is literally shared between planner and charge (G1: the preview
  can't lie — same `StepCost`).
- Python A*: routes around a wall and a POI-penalty tile to the goal.

## F8a acceptance — confirm in-editor (3D view)
- Hover a distant revealed/silhouette tile: a dotted path lights up from the castle
  to it with a "~N fuel" estimate; it bends around known POIs and never crosses
  water or Hidden ground. Hover a Hidden tile: no path.
- Move the castle: the old ribbon clears.
- Clicking still does a single adjacent step (execution is F8b — not wired yet).

## Next
- **F8b — execution:** click a distant tile → stride there step-by-step (~0.25 s/
  tile) through the real per-step move, with halt conditions (arrival, encounter/
  ambush, fuel-out, Hull ≤ 25%, cancel) and a Halt button.
- **F8c — momentum + Grimoire lock + ambush +1 round** while striding.
- Still open: F3 castle types; F1/F2 rulings.
