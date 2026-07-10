# K2 Verification — Injury/Death Triggers + Infirmary Recovery

*Built 2026-07-09 against companion_item_systems_v2_1 §5b + §10 (K2 row) + R24. Console transcripts are the evidence medium; every roll prints its inputs and outcome.*

**Change inventory:** `Companion.InjuredLunationsRemaining` (serialized int; `IsInjured` computed, JsonIgnore) + round-trip asserted once per session against `SaveManager.JsonOptions` (`[K2 RoundTrip]` line). New `CompanionInjurySystem`: `ApplyWipe(save, territoryTier, bossContext, context)` — one §5b roll per fielded companion (Tier 1 injured / Tier 2 +15% death / Tier 3 +30% / boss 40%, Sworn −10 pts, severity roll 1–2 lunations); `TickRecovery(save)` on the lunation tick (StrategicView, after CouncilTick — R24 Training Grounds interim host). Call sites: lost-combat return (whole fielded party — defeat requires `allPlayersDead`, so downed ≡ fielded; boss flag from `EncounterRouter.CurrentTier`, new getter) and `FailExpedition` (pool → 0; `injuriesAlreadyRolled` guard prevents double-rolling when a lost combat also zeroes the pool — one roll per wipe). `TerritoryTierAt` mirrors `DifficultyMultAt`'s kingdom lookup; unclaimed ground = tier 1. Exclusions from all three demands: `CompanionRoster.GetActiveParty` filters `IsInjured` (covers combat spawns, negotiation tokens, deck contribution), `ComputePartyBaseHP` skips injured (no pool contribution), CouncilScreen envoy picker excludes injured outright (same treatment as imprisoned).

**Scope rulings (logged):**
- Downed-in-lost-combat = whole fielded party, because defeat requires `allPlayersDead`. Per-companion downed telemetry becomes necessary only if a retreat mechanic ships.
- Death sets `IsPermadead` + evidence line. The v1 morale ripple and signature destruction are **K4 scope** (they live in the loyalty delta table K4 builds) — the death print says so, so the deferral is visible in every transcript.
- Won combats roll nothing (heroism stays free, §5b).
- Mid-expedition injuries don't recompute the launch-time MaxHP pool; the next expedition's pool reflects the understaffed roster. (Launch-time pool = launch-time roster.)

## Checklist (named predictions)

1. **Round-trip:** first wipe or lunation tick prints `[K2 RoundTrip] InjuredLunationsRemaining round-trips.` — never the FAILED variant.
2. **Tier-1 wipe (K2 exit, part 1):** lose a fight or bleed out on unclaimed/tier-1 ground → every fielded companion rolls `death 0%` → all INJURED 1–2 lunations, none die. Console: one `[Injury]` line per companion with roll, chance, duration.
3. **Tier-2 wipe:** same in tier-2 territory → each companion shows `base death 15%`; a Sworn companion shows the −10 (`5%`). Outcomes vary by roll — the console evidence is the chance math, not the outcome.
4. **Boss loss:** lose to a Boss-tier encounter → `BOSS` flag in the wipe header, 40% base.
5. **One roll per wipe:** lose a combat that also zeroes the pool → exactly ONE `[Injury] Wipe rolls` block (the `FailExpedition` one is suppressed via `injuriesAlreadyRolled`).
6. **Exclusion (K2 exit, part 2):** with a companion injured — next expedition launch: pool readout omits them (`[PartyPool]` line shorter); they don't spawn into combats; the envoy picker doesn't list them; negotiation tokens don't count them.
7. **Recovery on schedule (K2 exit, part 3):** advance lunations on the strategic map → `[Infirmary] {name} recovering — 1 lunation(s) left` then `has recovered and returns to the roster`; they reappear in party/envoy/pool the same lunation.
8. **Persistence:** save with an injured companion, quit, reload → still injured with the same lunations remaining.
9. **Regression:** WON combats and clean extractions roll nothing — no `[Injury]` lines anywhere in a winning run.

---

## K2.5 — Expedition HP Persistence (RULED 2026-07-09, amends §4a/§5b)

**The ruling:** combat damage persists between fights within one expedition — **unit HP is the fights; the party pool is the journey** (pool re-scoped to traversal stamina: terrain, corruption, exhaustion). At extraction: below 25% of BaseHP → 1 lunation infirmary; stabilized at 0 (downed in a WON fight) → 1–2 lunations, **no death risk** (death stays a losing-fight thing, §5b unchanged on losses/wipes). A companion downed in a won fight is out for the REST of that expedition (stabilized, cannot field). Full reset at expedition launch and end.

**Position recorded (house rule):** the counterargument was §4a's single-attrition-currency architecture — per-companion HP is a second economy beside the pool. Overruled on the grounds that won combats fed expedition attrition NOTHING (a flawless win and an everyone-at-1-HP win were identical), and per-companion texture ("the hurt one") is the point of the people pillar. The two currencies now have disjoint jobs by construction.

**Open design item (logged for K4):** "brave and noble sacrifice" — going down in a won fight — deserves its own mechanic in the loyalty/arc layer (v1 priced downing in loyalty; K4 owns the delta table). Currently it costs stabilization + infirmary time only.

**Mechanics:** `Companion.ExpeditionHP` (serialized, −1 = fresh, 0 = stabilized; round-trip asserted alongside the injury field). Captured in `HandleUnitDeath` (companion downed → 0) and the VICTORY branch of `CheckCombatEnd` (survivors' remaining HP); consumed at companion spawn (clamped to actual MaxHealth); stabilized companions filtered from `GetActiveParty` mid-expedition (spawns, decks, negotiation); `ApplyExtractionCheck` at Extract() (banner names the injured); reset on fresh deploy and both expedition-end paths. Casualty note also added to the FailExpedition banner (the original ask).

**Predictions:**
E1. Win a fight bloodied → `[ExpeditionHP] {name} leaves the fight at X/Y — carried` on victory; next fight in the same expedition: `fields at X/Y (carried from earlier fights)` and the health bar shows it.
E2. Companion downed in a WON fight → `stabilized at 0, out for the rest of this expedition`; they do not spawn in the next fight; extraction says `carried home — N lunation(s)` and they enter the infirmary.
E3. Extract with a companion under 25% BaseHP → `injured — 1 lunation` in the extraction banner + infirmary badge at campus.
E4. Extract with everyone above 25% → no casualty text, nobody injured, ExpeditionHP silently reset (next expedition fields at full).
E5. Fresh deploy after any of the above → everyone at full HP in fight 1.
E6. Lost fight / wipe → §5b rolls exactly as before (E-path adds nothing on losses); failure banner now names the casualties.
E7. Round-trip line now reads `InjuredLunationsRemaining + ExpeditionHP round-trip.`; save mid-expedition with carried damage, reload → still carried.
E8. Campus fights (debug launcher, IsOnExpedition false) → no `[ExpeditionHP]` lines, full HP every fight — the whole system is expedition-scoped.

**K2.5 results (2026-07-09): E1–E8 ALL PASSED** (user-verified in engine). Expedition HP persistence, stabilization, extraction gate, casualty banners, round-trip, and campus-scoping all confirmed. K2 + K2.5 closed.

## Results (2026-07-09, first live console)

Predictions **1, 3, and 6 (pool half) CONFIRMED** on a real tier-2 patrol loss: `[K2 RoundTrip] … round-trips.`; `[Injury] Wipe rolls — defeated in combat (territory tier 2, base death 15%)`; `Torrin Ironward injured — 2 lunation(s) (death roll 78 ≥ 15%)`; next launch's readout `[PartyPool] wizard 20 = 20` (injured companion contributes nothing). Remaining predictions pending.

**Two defects found by the same console, both fixed same day:**
1. `CheckCombatEnd` had no already-ended guard — the enemy-turn tail and prune loop re-invoked it after the phase was decided, emitting `=== DEFEAT ===` / `CombatCompleted` **4×** and re-rolling the router's gold each time. Guard added at the top (returns true once Victory/Defeat is set).
2. **Defeat was consequence-free** — `router.DamageTaken` arrived as 0, so a fully dead party "respawned" onto the overworld at full pool. **RULED: defeat ends the expedition** (coherent with defeat = allPlayersDead): §5b wipe rolls, then `FailExpedition` (spoils lost, discovery kept, forced return). `GodModeHP` (debug) is the only escape — the run survives at 1 HP.

Re-verification predictions:
D1. Lose a fight → exactly ONE `=== DEFEAT ===` and ONE `Combat finished. Won: False` (gold rolled once).
D2. Return from the loss → `[Injury] Wipe rolls` block, then `Expedition failed: Your party was defeated in the field.` — return-to-campus button, unbanked gold forfeit, splinters kept.
D3. Same loss with GodModeHP on → expedition continues at ≥1 HP with the GodMode message.
D4. Regression: won combats unchanged (single VICTORY emission — the new guard covers both phases).

**UX addendum (2026-07-09, post-verification):** all checklist items passed, but the injury state was invisible in-game — log-only, so a missing companion in the next fight read as a surprise. Two surfaces added: the campus Companions tab shows `[INFIRMARY — N lunation(s)]` (name in danger red, sub-line notes the exclusion) and the strategic-view deploy dialog gains a "Party" manifest line plus a `✚ Infirmary: …, will not deploy` warning when anyone is recovering. Predictions: U1 — with Torrin injured, the deploy dialog reads `Party: The wizard, alone` + the infirmary warning; U2 — the campus roster card shows the badge; U3 — after recovery both surfaces revert (badge back to [PARTY]/[ROSTER], warning gone).

**Re-verification results (2026-07-09, second console): D1 + D2 CONFIRMED.** Exactly one `=== DEFEAT ===`, one `Combat finished. Won: False` (gold rolled once), wipe block printed, `Expedition failed: Your party was defeated in the field.` — forced return, spoils forfeit. Bonus confirmation: the wipe block was EMPTY of roll lines because the fielded party was the wizard alone — Torrin, still injured, was correctly excluded from fielding through a second loss (prediction 6, fielding half). Still pending: 2 (tier-1 wipe), 4 (boss 40%), 5 (one-roll guard), 6 (envoy picker), 7 (recovery on schedule), 8 (reload persistence), D3 (GodMode), D4 (victory regression).
