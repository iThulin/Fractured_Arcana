# V3 Tranche 1 Verification — Stack Panel, Log Grammar, Charge Dot

*Built 2026-07-09 against combat_ui_v2 §7c/§8/§9. TRANCHE 1 by design: V3 "lands per-roster as U3–U5 ship keys" — this covers everything U3's shipped keys can exercise. Tranche 2 (R22 damage preview, EveryN pips, mimic glyph, Mirrorstep/Rewind spent markers, aura extents, pooled response strip — see the checklist-4 resolution) lands with U4/U5.*

**Change inventory:** §7c stack strip (bottom-right above End Turn) REPLACES the interim U3 priority prompt — pending stack objects top-down (name — source, one-line intel), top item highlighted, 0.3s readability beat per resolution, Pass button only while the player holds priority (zero clicks otherwise), live re-render when a response cast lands on top. Enter guard now reads `StackWindowInteractive`. `UIContent.FormatLogLine` = the §9 grammar; Requiem and Deathburst route through it (`[Wake-Keeper] Requiem: +2 damage (2 stacks, now 7)` / `[The Final Service] Deathburst: 2 Honored Dead rise`); `Unit.AbilityUseCounts` carries per-ability counters. Roster charge dot for ranged_charge units: ○ will channel / ✸ releases next activation (UITheme.ChargeReady/ChargeSpent).

## Checklist

1. Kill The Final Service, no Reaction in hand, no stop: strip appears showing "Deathburst — The Final Service" + intel line, plays through with ZERO clicks (0.3s beat), Honored Dead rise, strip hides. Console: FormatLogLine grammar.
2. Same with "Stop on enemy triggers" checked: strip shows Pass button, game pauses, Pass resolves. Enter does NOT pass the window (guard).
3. Requiem: kill two allies of a Wake-Keeper → log shows `(1 stack, now 5)` then `(2 stacks, now 7)`.
4. Response cast during a window: the Reaction appears at the TOP of the strip immediately; Pass resolves it first (LIFO), window reopens on the trigger.
5. Wizard/Wake-Keeper roster rows: ○ before channelling, ✸ once `wizard_charging` is applied, back to ○ after release. Tooltips read correctly.
6. Regression: generic fight without abilities — strip never appears; combat identical.

## Results (2026-07-09)

Checklist 1, 2, 3, 6 **PASSED**. Checklist 5 passed for the wizard; the Wake-Keeper's "always-on star" is NOT the charge dot — it's the ✦ Requiem ability chip (permanent by design; the charge dot only renders on ranged_charge units). Two skip-deploy handoff regressions found and fixed same day: wizard no longer auto-selected at combat start, and the live enemy roster not populated until a damage event — both were side effects of the old synchronous timing, now explicit in the handoff (SelectUnit + RefreshEnemyRoster).

**DEFECT RESOLVED (2026-07-09) — checklist 4:** Root cause was (a)+(b) compounding, diagnosed from the live files without a repro run; (c) refuted from data (6 of the 9 Chronomancer halves confirmed `"speed": "Reaction"` in the JSONs — Borrowed Seconds, Ward of Hours, Undertow, Hinder, Drag, The Strings of Fate — and Session E proved the socket works for Adept). The gate was selection-scoped three ways: (1) it read only `deckManager.Hand` (the active deck); (2) `half.CanPlay(State, Me)` → `ManaCost.CanPay` falls back to `State.Mana[Me]` when `ActiveCasterUnit` is null — and `SelectUnit` syncs `State.Mana[Me]` to the SELECTED unit, so killing with a martial selected checked the Chronomancer's hand against the martial's mana; (3) selection was hard-gated to PlayerTurn, so an enemy-phase window couldn't switch to her anyway (and a selected martial hides the hand UI). In practice the window opened only when the Chronomancer was simultaneously the active deck, the selected unit, and solvent — "basically never," since she rarely lands the kill herself.

**FIX (RULED: auto-select now, pooled strip later):** `PlayerHoldsCastableReaction` → `FindReactionResponder()` scans EVERY living arcane unit's hand, evaluating affordability against that unit's OWN mana (`UnitCanPlay` pins `State.ActiveCasterUnit` for the check; Fate free-reaction mirrors the `Rules.CanCast` bypass; frozen units excluded via `CanAct`). The window auto-selects the responder (hand + mana sync ride the existing `SelectUnit`), and clicking a friendly unit mid-window switches responders — enemy-phase clicks are selection-only (movement/attacks stay phase-gated; move tiles cleared). Hardening in passing: End Turn is blocked while a window is open (the drain awaits `_priorityPassed`; ending the turn mid-window raced it). **V3 tranche-2 item logged:** the pooled response strip — every castable Reaction across all units in one surface, casting as its owner — replaces auto-select as the UX when the §7c panel grows.

## Re-verification — reaction window rework (named predictions)

R1. Martial lands the kill on The Final Service, Chronomancer (unselected, mana ≥ 1) holds Drag: console shows `[Priority] window OPEN on Deathburst (…holds a response)` + `[Priority] auto-selected <Chronomancer>` — the camera moves to her, HER hand fans out. Drop Drag → `Cast (preselected) → Drag [Reaction] (stack size 2)` — HER mana ticks down, not the martial's. Pass → Drag resolves, window reopens on Deathburst, pass → Honored Dead rise.
R2. Same kill, Chronomancer at 0 mana, no Foresight free-reaction: `[Priority] auto-pass … (no response held)` — zero clicks (affordability is per-unit now; a broke responder must not stall the fight).
R3. Same kill, Chronomancer at 0 mana WITH a Fate free reaction charged: window OPENS (the CanCast bypass is mirrored in the gate).
R4. Enemy-phase trigger (Requiem during the enemy sweep), two arcane units with Reactions: window opens on the first responder; clicking the second friendly mid-window switches — `[Priority] responder switched to <name>` — their hand appears; no unit moves from the click.
R5. End Turn clicked while a window is open: `[Priority] End Turn blocked — window open.` — turn does not end.
R6. Regression (checklist 1): no Reaction anywhere in the party, no stop → auto-pass, zero clicks, unchanged console shape.
R7. Regression (checklist 2): stop set, no Reactions → window opens `(stop set)`, Pass resolves, Enter guard still holds.

**Results (2026-07-09, live console):** R1-shape run PASSED end-to-end — Worldshaper (companion cast) killed The Final Service, `window OPEN … (dfgh holds a response)` + `auto-selected dfgh`, Misdirection cast into the window (`stack size 2`, Foresight discount applied), **redirected Deathburst onto a friendly Stone pillar** (Reaction-vs-trigger semantics work — the open design question resolved itself), pass → Deathburst resolved at the redirected context, Honored Dead rose, fight continued. Windows reopen after each response resolution per R3. R2/R3/R4/R5 not yet exercised.

**DEFECT (2026-07-09, same session) — companion casts resolve centered on the main character:** Elara Stormcaller (Elementalist companion) cast Stone Barrier and Worldshaper; pillars spawned adjacent to the MAIN character's tile, `[GiveArmor]` hit the main character, and Worldshaper's imbue centered on him. Root cause: `EffectBase.FindCasterUnit` (Effect.cs) mapped `PlayerA → s.PlayerUnit` unconditionally — with per-unit decks, PlayerA is the whole party, so every effect-side caster lookup resolved to the main character. (TargetingHelpers had the correct ActiveCasterUnit-first logic; the effect-side twin didn't.) **FIX:** (1) `EffectBase.FindCasterUnit` prefers `State.ActiveCasterUnit` for PlayerA; (2) `StackItem.CasterUnit` captured at cast + `Resolver.ResolveTop` pins/restores `ActiveCasterUnit` around resolution — required for stack-deferred casts (Reaction responses resolve AFTER the drop path clears ActiveCasterUnit); (3) housekeeping: the per-frame `[CombatEnd] deferred` print is latched to once per deferral episode (PruneDeadUnits re-arms `_pruneNeeded` every frame by design; the evidence line stays, the flood goes).

Predictions:
C1. Companion casts Stone Barrier from tile X → pillars spawn adjacent to X (not the main character), `[GiveArmor]` names the companion.
C2. Companion casts Worldshaper → imbue centers on her tile; enemies in HER radius take the damage.
C3. Reaction response cast in a trigger window by a unit other than the currently-armed caster → its effect resolves centered on the responder (exercises the ResolveTop pin — this path was broken even with fix 1 alone).
C4. Main-character casts unchanged (ActiveCasterUnit == PlayerUnit — identical resolution).
C5. Enemy ability/AI resolution unchanged (CasterUnit null → pin no-op).
C6. Next Final Service kill: exactly ONE `[CombatEnd] deferred` line per episode.

**DEFECT (2026-07-09) — skip-deploy handoff UI sync lost to build-order race:** the 07-09 handoff fixes (auto-select wizard + populate enemy roster, this doc's Results note) ran, but landed BEFORE `CombatUI.BuildUI` (both are CallDeferred; the handoff queues first). `RefreshEnemyRoster` early-outed on `_enemyRosterBox == null` WITHOUT storing state — dropped silently, roster empty until the next damage event; the attunement panel wired with `Unit: none`. **FIX:** (1) `RefreshEnemyRoster` stores `_lastRosterEnemies` before the built-check and `BuildUI` replays it (same pending pattern as selected-unit/intel/deployment); (2) the handoff's UI sync moved to `FinishSkipDeployHandoffAsync` — awaits `CombatUI.IsBuilt` (new property) + one frame for the attunement wire, then selects + refreshes; prints `[SkipDeploy] handoff UI sync complete`.

*Amended same day (screenshots showed neither `[SkipDeploy]` line — a stale build and a silently-dead fire-and-forget task are indistinguishable without an entry print): the handoff now runs an EAGER pass immediately (select + roster push; CombatUI's pending-replay applies it at build time) plus the LATE post-build re-sync, which now prints on entry and catches/logs its own exceptions.*

Predictions:
S1. Skip-deploy launch: wizard selected at first input (ring, unit card, hand up in normal colors — not the red unaffordable tint — attunement panel showing HIS school) with zero clicks; console shows `[SkipDeploy] handoff waiting for CombatUI build...` then `[SkipDeploy] handoff UI sync complete` after the `[CombatUI]` build prints.
S2. Enemy panel populated from turn 1, before any damage event (eager push + BuildUI replay — holds even if the late task dies).
S3. Normal (non-skip) deployment flow unchanged — both passes only run in skip mode.
S4. If NEITHER `[SkipDeploy]` line appears in a fresh run, the running build is stale — check the editor's MSBuild output. If the entry line appears but not the completion line, read the `[SkipDeploy] handoff sync FAILED:` error in the Debugger Errors tab.

**ROOT CAUSE FOUND (2026-07-09, live stack trace):** `MovementZoneRenderer.Clear()` NRE'd on `_immediateMesh.ClearSurfaces()` — the mesh was created in `_Ready`, but the skip-deploy handoff calls `ClearMoveTiles()` (via round-1 `StartPlayerTurn` AND via `SelectUnit`) before the renderer enters the tree. The exception aborted the entire deferred `InitializeUnitDecks`, killing select + roster sync in EVERY prior version of the handoff — this single throw was the whole defect chain, including the original 07-09 "regression fixes" that never actually ran. Round 2+ worked because the renderer was in the tree by then. **FIX:** `_immediateMesh`/`_lineMaterial` are field-initialized (Resources are tree-independent); `_costLabel` uses null-guarded; node creation stays in `_Ready`. The v4 handoff try/catches stay as permanent tripwires — a future round-1 throw prints to the Output panel instead of vanishing.

Final predictions:
S5. Fresh skip-deploy launch: NO `THREW` lines; draws → `DeckManager state` + `Selected: dfgh` + `=== Round 1: Player Turn ===` + `[SkipDeploy] eager sync OK` contiguous, then the build prints, then `[SkipDeploy] handoff UI sync complete`.
S6. The `=== Round 1: Player Turn ===` banner prints for the first time in this defect's history — StartPlayerTurn completes, so round-1 persistent-effect ticks/status/hazard processing now actually run in skip-deploy fights.
S7. Verified (2026-07-09 screenshot, pre-fix build with catches): late-pass sync alone already produced selected wizard + populated roster + normal-color hand on turn 1.
S8. Top banner reads "ROUND 1 - PLAYER TURN" + hint from launch in skip-deploy (SetPhaseText/SetHintText got the pending-replay treatment — same pre-build silent drop as the roster). Audit note: every CombatUI public entry point now either replays or is only called post-build; if a new panel is added, give its setter the same pattern.

## Exit (partial, per tranche)

Deathburst appears on the stack strip and auto-passes cleanly with no response in hand ✓ (criterion 1); Requiem stacks in the log ✓ (criterion 3). Damage preview + Mirrorstep warning glyph are tranche 2 criteria, gated on U5's keys existing.
