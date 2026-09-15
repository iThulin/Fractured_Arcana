# Negotiation Tuning v1 — Method, Findings, Knob Map

**Date:** 2026-07-17 · **Tools:** Monte Carlo harness (`negotiation_sim.js`, faithful JS port of the C# rules), `NegotiationTuning.cs` (all constants, one file), `NegotiationTelemetry.cs` (CSV per real table).

---

> **2026-08-31:** the narrative and UI redesign shipped (`negotiation_narrative_spec_v1.md`). One mechanical change affects these targets: the closing squeeze now fires only while the NPC holds Resolve (spec s5b/s10). Squeeze-at-close drops from ~98% to ~11% in simulation (`tools/negotiation_squeeze_sim.py`, replaces the uncommitted negotiation_sim.js); other targets moved less than a point. Watch the squeeze rate in playtests; the reserve-model fallback is described in the spec's s10.

## 1. Target metrics — what "tuned" means

| Metric | Target | Why |
|---|---|---|
| Table length | 6–9 turns | Patience clock felt but not frantic |
| Skill gap | naive median 2★, skilled median 3–4★ | The game rewards reading stances, not button quality |
| 5★ rate | ~5% of signed deals | Renown anchors must mean something |
| Collapse rate | 5–10% under careless play, ~0% skilled | Real threat, avoidable consequence |
| Walkout (TheyLeft) | punishes token-dumping, spares deliberate play | The clock is the pressure |
| Token usage | every type sees play | Unused token = dead design |
| Cordial close rate | ~30–50% skilled | Cordial is earned, not default |

## 2. What the simulation found (10k+ tables per experiment)

**Finding 1 — 4–5★ deals were mathematically unreachable.** Old economy: player holds ~4 tokens (1 per school innate + 1 Demonstration) vs NPC pools of 4–6 on boards starting at score ≈ −8. Best-case achievable score ≈ +1 against a 5★ threshold of 8. Every policy — naive, greedy, skilled — signed ~1★ deals. **No numeric threshold tweak fixes this; the economy was starved.**

**Finding 2 — skill didn't express.** Skilled vs naive gap: 0.2★. With so few tokens, sequencing barely matters.

**Finding 3 — dead tokens.** In 6,000 skilled tables: Offering played 245 times, Patience 3 times. Both are school-locked (Tinker/Chronomancer only); the exchange economy and the timing tool were fiction for 6 of 8 schools.

**Finding 4 — Commander starved skilled play.** basePatience 4 with a 3-Resolve pool: skilled play (which spends turns on reads) walked out of 44% of tables while naive play "won" by signing −31-gold garbage fast.

## 3. Changes applied (validated by re-simulation)

1. **Economy** (`NegotiationTuning`): `SchoolTokenCount = 2` (school innates doubled), `BaseOfferingFloor = 1`, `UniversalPersuade = 1` → ~7 player tokens. Result: tables ~7 turns, signed-score P50 skilled ≈ 3 vs naive ≈ 1, P95 ≈ 6–9, and **patience becomes the binding constraint** — token-dumpers get walked out on, which is the design intent.
2. **Patience**: `frontier_wilds_commander` 4 → 6, `generic_survivor` 5 → 6 (the richer economy needs the extra beats; Merchant 8 unchanged).
3. **Star thresholds kept at 8/5/2/−2** — under the new economy they land exactly on target: skilled median 3★, top-quartile 4★, 5★ ≈ P95. Bots understate human skill, so expect real skilled play slightly higher; revisit after telemetry.
4. **Every constant centralized** in `NegotiationTuning.cs` (39 references refactored out of `NegotiationState`), each documented with the metric it moves.

## 4. The knob map (what to turn when playtests say…)

| Playtest says | Turn this | In |
|---|---|---|
| "Tables end too fast / drag" | encounter `basePatience`; `SchoolTokenCount` | JSONs; Tuning |
| "I just spam my best token" | stance-modifier spread (`WaveringEase`, `GuardedResent`, `IrritatedBackfireTension`) — bigger spread = timing matters more | Tuning |
| "Offerings feel mandatory / useless" | `OfferEagerPull`, `BaseOfferingFloor` | Tuning |
| "Hostile is a death spiral / toothless" | `HostilePullSteps`, `StrainedMax`, `ScoreHostilePenalty` | Tuning |
| "NPC feels passive / oppressive" | `npcResolve/Guile/Poise` per encounter; archetype defaults | JSONs; `ArchetypeBehavior.DefaultNpcPool` |
| "Squeeze always worth holding / never" | `SqueezeOdds*` (these are SHOWN to the player — they're UI) | Tuning |
| "Stars too generous / stingy" | `StarT5/T4/T3/T2` | Tuning |
| "Cordial close feels unearned" | stance bags in `ArchetypeBehavior.RollStance`; `CordialMax` | NpcArchetype; Tuning |

## 5. The workflow

1. Change ONE value in `NegotiationTuning.cs` (or one encounter JSON). Rebuild.
2. Play 5–10 tables. Every resolution appends a row to `user://negotiation_telemetry.csv` (Windows: `%APPDATA%\Godot\app_userdata\Fractured Arcana\`). Delete the file when you change values, so each sample is clean.
3. Open the CSV — outcome, stars, score, turns, zone, per-token play counts, squeeze results — and compare against §1's targets. Or hand the CSV to Claude with "compare to the tuning targets."
4. For big questions ("what if pulls were 2 steps?"), edit `BASE_KNOBS` in `negotiation_sim.js` and run `node negotiation_sim.js 2000` before touching the game.

**Caveats:** the sim's bots are heuristics — they understate human skill and don't model companion/building tokens (both add to the player side, so real play runs slightly richer than sim). Treat sim numbers as relative signals, human playtests as truth, telemetry as the bridge.
