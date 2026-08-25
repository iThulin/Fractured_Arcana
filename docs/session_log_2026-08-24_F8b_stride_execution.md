# Session log — 2026-08-24 — Stride Orders F8b: execution

Click a distant tile and the castle now marches there, tile by tile, through the
real per-step move — with halt conditions and a Halt button. Builds on F8a
(planner + preview). Static-verified (no .NET SDK here). Compile + playtest before
F8c (momentum + Grimoire lock + ambush +1).

## Behaviour

- **Click routing** (`OnWindow3DMove`): adjacent tile → one ordinary step
  (unchanged); distant reachable tile → `BeginStride`; click while marching →
  cancel (the one order accepted mid-stride, §3.4).
- **The march** (`StrideStep`, self-rescheduling ~0.25 s/tile): each beat waits for
  the pawn's previous hop to finish (paces off the new `OverworldPartyToken.IsMoving`
  rather than racing the animation), runs the halt checks, then commits the next
  step via the REAL `_party.TryMoveTo` — so fuel burn, Hull drain, vision reveal,
  patrol movement and ambush checks all fire per step exactly as a manual walk.

## Halt conditions (§3.4)
The march stops on: **arrival**; **an encounter** opened on the reached tile (scout
report / narrative / negotiation / services — POI unconsumed); a **POI revealed on
the path ahead** (non-goal); **out of fuel** for the next tile (a stride never
spends Hull to press on — it halts); **Hull ≤ 25% MaxHull** (safety); **ambush**
(`_ambushPending`) or any **run-end** (`ExpeditionComplete`); or **player cancel**
(Halt button / map click). Run-end paths (Extract / EmergencyExtract / Fail) also
clear an in-progress march so the Halt button never lingers.

## Edits — `ExpeditionManager`
- State: `_striding`, `_stridePathQueue`, `_haltButton`, `StrideStepSeconds = 0.25`.
- `BeginStride` / `StrideStep` / `ScheduleStrideTick` / `CancelStride` / `EndStride`
  / `SetHaltButton`. Single timer chain (each beat schedules exactly one next tick
  or ends — no re-entrancy).
- Halt button (top-centre, shown only while marching) → `CancelStride`.
- Hover preview suppressed while striding.
- `Extract` / `EmergencyExtract` / `FailExpedition` end any march up front.

## Edits — `OverworldPartyToken`
- `public bool IsMoving => _isMoving;` so the stride can pace on the animation.

## Why it's safe (reviewed)
- The fuel gate is checked BEFORE each step, using the SAME `StrideEdgeCost` the
  charge uses; weather advects only AFTER the charge, so the pre-check matches the
  step it gates. A stride therefore never trips the Hull-burning exhaustion path.
- If a step drives Hull to 0 (terrain/weather), `EmergencyExtract` runs and clears
  the march; the pending tick sees `ExpeditionComplete` and no-ops.
- SceneTree timers auto-disconnect from a freed manager on a combat scene change, so
  a march interrupted by a fight cannot fire onto a dead node.

## F8b acceptance — confirm in-editor (3D view)
- Click a far tile: the castle walks the previewed path one tile at a time (~0.25 s
  each); patrols move each step; fuel/Hull tick per tile.
- It halts on: reaching the tile; hitting an encounter/ambush; running low on fuel;
  Hull dropping to a quarter; or pressing **Halt** / clicking the map.
- Adjacent clicks still single-step; extracting mid-march clears the Halt button.

## Next: F8c — momentum discount (−1/tile from the 4th step), the Grimoire seals
while striding, and the ambush teleport delay +1 round while striding.
