# U3 Verification Queue — Stack-Integrated Trigger Framework (R3)

*Built 2026-07-09 against archmage_unique_units §5 (R3 stack-first) + §7a (conductor roster) + build_order_v3 §4. Console transcripts are the evidence medium; every new evidence line is GD.Print-mirrored (Session B lesson).*

**Change inventory:** UnitDefinition gains `Abilities[]` ({key, trigger, name, intelDescription, params}, hard cap 2, registry-asserted); enemy trigger bus (`CombatManager.Triggers.cs`) — death triggers queue synchronously, drain pushes them as first-class StackItems on the RulesManager stack, priority window before EACH resolution, AI auto-passes; `EnemyTriggeredAbility : Ability` rides the existing GameStack/Resolver; summon seam gains a registry-resolved branch (`SpawnRegistryUnit` — risen units are config-identical to deployed ones); conductor roster JSONs (Honored Dead 16/2/0/R1/4 pack · Wake-Keeper 12/2/0/R3/3 Requiem · The Final Service 55/1/2/R1/9 Deathburst); Necromancer.json pools rekeyed to roster ids; CombatUI priority prompt (INTERIM until V3's stack panel); `PlayerSession.DebugStopOnTriggers` + launcher checkbox; CheckCombatEnd defers while triggers are outstanding.

**Rulings logged (in code headers):**
- `onAllyDeath` added to the trigger taxonomy — §5 lists OnDeath (self); Requiem is specced "OnDeath of any ally"; a distinct trigger string keeps JSON declarative.
- Only DEATH call sites wired in U3 (all the conductor needs). The bus supports the full §5 taxonomy; U4 rosters add onTurnEnd/onAttack/etc. call sites with their keys — per build_order §8: resist landing U4 "while in there".
- Mid-fight summons spawn at BASE stats — difficulty mult applies to encounter slots, not ability output.
- Response-hand check reads the ACTIVE deck only; multi-unit reaction hands and drag-targeting polish are V3 UX.
- Trigger contexts capture name/tile/team at queue time — source nodes may be pruned before resolution.

## Session A — Regressions (blocking)

1. **Assert Units**: expect ALL PASSED — now includes ability round-trip (requiem params), ability key/trigger catalog audit, cap-2 check. 14 defs should load (5 generic + 6 debug + 3 conductor).
2. Generic-only debug fight, no stops set: identical to U2 behavior, ZERO priority prompts, zero new console lines except `[Priority] auto-pass` never appearing (no triggers exist on generics). U2 tags (pack hounds etc.) unchanged.
3. A normal card cast with kills (no enemy abilities in play) resolves exactly as before; victory on last kill still fires (per-frame re-check covers the deferral).

## Session B — Deathburst enters the stack (the R3 exit criterion)

Debug launch vs 1× The Final Service (+ anything). Kill it. Predictions, in console order:

1. `The Final Service has died.`
2. `[Stack] Deathburst (The Final Service) enters the stack (size 1).`
3. `[Priority] auto-pass on Deathburst (The Final Service) (no response held).` — ZERO extra clicks with no Reaction in hand and no stop set.
4. `[Stack] Resolving Deathburst...`
5. `[Summon] Registry unit conductor_honored_dead rises at (x, y).` ×2 — adjacent free tiles; fewer with a crowded corpse (log: `no room at the table`).
6. `[CombatEnd] deferred — triggers outstanding` if it was the last enemy — then combat CONTINUES against the risen pair (no premature VICTORY).
7. Risen Honored Dead lock intents at the next plan and fight with the pack tag.

## Session C — The window (stop set)

Same fight, launcher checkbox "Stop on enemy triggers" ON. Kill the Final Service → `[Priority] window OPEN on Deathburst (stop set)`, bottom-center prompt appears, game PAUSES. Pressing Pass → `[Priority] passed`, resolution proceeds. Evidence that the window is a real pause, not a log line.

## Session D — Requiem (onAllyDeath, stacking)

Battle pool `the_honored_dead` (2× Honored Dead + Wake-Keeper) or debug-launch equivalent. Kill the two Honored Dead one at a time:

- Each death: `[Stack] Requiem (Wake-Keeper) enters the stack` → auto-pass → `[Wake-Keeper] Requiem: +2 damage (now 5)`, then `(now 7)`.
- Wake-Keeper's NEXT intent telegraphs the raised value (it plans from AttackDamage).
- Kill the Wake-Keeper and an Honored Dead in the same sweep, Wake-Keeper first → its queued Requiem logs `fizzles — its source is gone`.
- Deathburst chain: kill Final Service while a Wake-Keeper lives → the risen Honored Dead dying later STILL feeds Requiem (risen units are real allies).

## Session E — Response cast (Reaction in hand)

Needs a Reaction-speed card in the active deck (14 exist — check the library; Adept has candidates). With one in hand and mana available, kill a trigger unit:

- Window opens (`you hold a response`). Drop the Reaction → `Cast (preselected) → ... (stack size 2)` — it lands ON TOP of the trigger.
- Sorcery/Instant drops during the window are rejected: `Only Reaction-speed cards can respond.`
- Press Pass → the REACTION resolves first, window reopens on the trigger, pass again → trigger resolves. LIFO order visible in the log.
- Known limitation (V3): no target highlight while dragging during the window (hover preview is phase-gated); the drop itself works.

## Session F — Conductor pools end-to-end

Walk the overworld to a Long Table territory fight (or force via pools): scout report shows "Honored Dead / Wake-Keeper" names (not archetype genera), spawns resolve exact-id, fight reads per the faction thesis — grind = steeper bill, killing the elite is mandatory and costly.

## Results (2026-07-09)

**Sessions A–F: ALL PASSED. U3 exit criteria met — marked done in build_order_v3.**

Finding from Session E: the response socket works but the content to use it barely exists. Reaction-speed halves by school: Chronomancer 9, Adept 3, Druid 1, Enchanter 1, Arcanist/Elementalist/Necromancer/Tinker **0**. Four schools cannot interact with the enemy stack at all. Logged as a card-content-push line item in build_order §7 — the R3 socket was built ahead of its content by design ("a card-schema capability this creates the socket for"), but the zero-school gap should close as part of the wip→ready authoring blocks, with school-flavored responses (a Necromancer death-response and an Elementalist punish-cast are obvious identities). Suggested floor: 2–3 reaction halves per school. Not U-track work; do not let it creep into U4.

## Exit (units doc §13 U3)

Deathburst visibly enters the stack and resolves after a priority pass; the Honored Dead rise via the summon seam; ability log lines appear; auto-pass adds zero clicks when the player holds no response. Then U3 → done in build_order_v3; next: V2 or U4 per Phase B ordering (V2 needs only U1–U2 and unblocks nameplates/telegraphs that U3's abilities now feed).
