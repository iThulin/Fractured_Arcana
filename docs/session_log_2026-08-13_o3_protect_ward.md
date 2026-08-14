# Session log — 2026-08-13 — O3: the protect objective (O-track COMPLETE)

**VERIFIED IN-ENGINE (Magos): ward spawn, exclusions, defeat-on-ward-death,
and hunt_ward pressure all confirmed — "it all works as described."**

*(Original caveat, now retired:)* **NOT COMPILED — static verification only** (balance deltas 0 on six files;
call sites grepped; unit JSON parses). Build + the checklist below are owed.

## The audit correction (read first)

The morning's build-order analysis claimed O1–O3 were unbuilt. WRONG — stale
by two days: **O1 (waves), O2 (survive), and even O4 (hold_zone) were already
built and verified in-engine** — O4 landed 2026-08-11 with the gate-defense
work (`session_log_2026-08-11_gate_defense_and_breach.md`), out of spec
order. The only missing kind was **O3 (protect)**, explicitly gated off by
`IsImplementedKind`. This session built exactly that — one objective kind,
not three. The existing substrate's belt-and-braces (loader gate + runtime
gate) meant a not-yet-built kind could never silently degrade; opening the
gate was one line once the kind was real.

## O3 — what shipped

- **`Unit.IsObjectiveWard`** (beside IsSpirit): player-side, targetable,
  benefits from shields/heals/auras — but not a combatant.
- **`SpawnObjectiveWard()`** (Objectives partial, called from the
  SpawnTestUnits tail after the party): stats from
  `UnitRegistry.Get(WardUnitId)`, spawned via `SpawnUnitFromSide` (all three
  events wired, real spawn slot), 0 speed / 0 AP / 0 move, added to
  playerUnits. Degrades LOUDLY to annihilate on a missing WardUnitId or a
  failed slot claim — a content bug must not make an unwinnable fight.
- **Death hook**: `NoteObjectiveUnitDeath` in HandleUnitDeath right after
  QueueDeathTriggers (corpse tile valid, trigger order intact) — latches
  `_objectiveDefeat`; declaration still flows through CheckCombatEnd.
- **Exclusions (ruling 5)**: defeat scan skips the ward (beside the existing
  IsStructure skip — "a standing door is not a survivor, and neither is the
  ward"); SelectUnit refuses it; the party bar skips it (its health reads on
  the board, where the mission is).
- **Semantics that fell out free** of the existing substrate: protect+waves
  (enemies-dead is only victory when no waves pend — ruling 4, already in
  the scan); protect+Rounds ("hold out N rounds around the ward" — the
  boundary rounds-check was already kind-agnostic); banner label ("Protect
  the ward" was already in DefaultObjectiveLabel, waiting).
- **`IsImplementedKind` += protect** — the loader gate opened itself.
- **Test surface**: launcher checkbox "protect the Anchor" attaches the
  objective with the new **`Data/Units/anchor_moment.json`** ward (30 HP,
  1 armor, 0 damage, immobile — the convergence spec's Fracture ward,
  authored now so the finale inherits it).

## Playtest fix 1 — "banner without a body" (Magos screenshot)

First launch showed the objective banner armed with NO ward on the board.
Cause: `SpawnObjectiveWard()` was placed after the companion loop — but
`InitObjectiveState` runs inside `QueueEncounterFromContext`, LATER in
SpawnTestUnits, so the spawn saw `_objective == null` and no-opped; then the
init armed the banner. Fixed: the call moved to after the encounter-queue
block (also keeps the ward out of the `companion_N` equipment-loadout index
mapping — a second latent bug). Plus: `ConfigureAndGenerateMap` player-spawn
sizing now adds +1 slot on a protect encounter — a full party would otherwise
leave the ward slotless.

## Known small caveat

Deployment-phase repositioning may allow dragging the ward (selection is
blocked, but deployment drag may use a different path) — harmless if so
(repositioning your dependent is arguably a feature); check in play.

## First-launch checklist

1. Build. Debug launcher → tick "protect the Anchor" + a few enemies (+ waves
   for the full Fracture shape) → launch.
2. The Anchor spawns player-side (gold-tinted), banner reads "Protect the
   Anchor"; it is NOT in the party bar and clicking it selects nothing.
3. Enemies will path to and strike it (default planners — watch whether they
   ignore it; that's the `hunt_ward` playtest question, deferred by ruling).
4. Let it die → immediate Defeat with "The ward falls." — even with your
   party at full health.
5. Kill all enemies (waves done) → Victory through the normal spoils flow.
6. Shield/heal cards can target it (protecting it with the toolkit IS the
   mission).

## Playtest fix 2 — `hunt_ward` (the §3.5 deferred ruling, threshold met)

Magos, in-engine: "enemies only target the ward if it is the closest." That
is the exact playtest evidence §3.5 deferred the key on — opportunistic-only
targeting makes protect degenerate into annihilate-with-a-bystander, and the
Fracture needs real pressure on the Anchor. Built:

- **`hunt_ward` BehaviorKey** (planner + registry oracle entry): hunts the
  objective ward through everything except spell-level target overrides
  (the Stalker rule — redirects rewrite reality, not preference). **Taunt
  does not divert it** (same ruling as Stalker, logged): body-blocking,
  shields, and heals are the counterplay — exactly the protect toolkit.
  No ward standing → behaves as melee_advance, so authored hunters reuse
  cleanly outside protect fights.
- **`Data/Units/fracture_keeper.json`** — "Keeper of the Unmaking", the
  convergence spec's Phase-2 Keeper-wave enemy, authored on hunt_ward. Shows
  up in the debug launcher roster automatically (registry-driven).

Checklist addition: launcher → protect + a couple of fracture_keepers in the
roster → the Keepers should walk PAST your party toward the Anchor while
ordinary units fight normally; kill the Anchor's attackers with body-block
positioning and the mission reads like a mission.

## O-track status: ALL FOUR KINDS COMPLETE (pending this compile).
**Convergence I2 (the finale director + the Fracture as a protect mission) is
unblocked.** Next per the finale spec's implementation order: I2 — scene +
phase machine, with `anchor_moment` already on disk as the Fracture's ward.
