# Fractured Arcana — Build Order v4

*Sequencing roadmap · audited 2026-07-31 against HEAD `331b910` on `desktop-2e3anmt` (`D:\Development\Fractured Arcana`).*

***SUPERSEDES `docs/build_order_v3.md`*** *(written 2026-07-06, last touched 2026-07-12 — 61 commits and ~40 sessions stale; it lists the entire overworld spell system as "Not started" when S1–S5 shipped 07-15/07-16). v3's **sequencing principles survive verbatim**; its phase contents do not.*

*v3 in turn superseded `guild_expansion_action_plan.docx` (2026-04-16). That document is now two generations obsolete and should be treated as historical only.*

*Scope anchored in: `single_world_refactor_v2` · `court_council_system_v1_1` · `archmage_unique_units_v1_2` · `combat_ui_v2_1` · `combat_environments_v1_1` · `companion_item_systems_v2_1` · `overworld_spell_system_v1_1` · `convergence.docx` · `docs/narrative_frame_intro_finale_v1.md` · `claude/progression_persistence_model_v1.md` · `claude/enemy_identity_spec_v1.md` · `docs/shard_acquisition_spec_v1.md` · `docs/quest_system_narrative_spec_v1.md` · `claude/art_pass_plan_v1.md`.*

---

## 0. The Three Facts That Should Drive Everything Below

Read these before the status table. They are the reason this plan is ordered the way it is.

**1. The game cannot be won.** `CampaignState.AllArchmagiResolved()` has **one definition and zero call sites**. Nothing triggers a Convergence. `CampusScreen` hardcodes the string `"ConvergenceDefeat"`. There are 20 files that mention Convergence and not one of them ends a campaign. `convergence.docx` — the three-path finale, the whole point of the fiction — is entirely unbuilt, and `docs/narrative_frame_intro_finale_v1.md` (canonical since 07-21) recast its Phase 2 without anything to recast it *into*. Fragment collection is meta-progression with no payoff: grep for fragment power application returns nothing. This was flagged as "the next big build" on 2026-07-23 (`claude/playtest_readiness_2026-07-23.md`) and has not moved in eight days while combat polish continued.

**2. Roughly a week of work has never been compiled.** The 2026-07-29 batch — post-cast design space (41 files), card audit implementation (62 files, all Tinker T1–T4 trees), glyph-grid softlock, Arcanist modifier drain, spoken-lines, pending-gold/toast, playtest batch, stance switcher, siege/stance fixes — was all written to disk with parser-level checks only. The 07-30 negotiation fairness + layout work (v6.1 → v6.3) likewise. The working tree is now clean against `331b910`, so it *was* committed, but committed ≠ compiled ≠ played. Every static-verification session since 07-15 carries the same line: *"no compile — Godot build is the arbiter."*

**3. Systems have outrun content by roughly an order of magnitude.** 100,779 lines of C# across 257 files; 26 subsystem directories. Against that: **2** quest JSONs, **6** encounter files for **15** regions, **6** buildings, **8** negotiation tables covering 6 archetypes (with `dustreach_commander.json` still missing and falling back to `generic_merchant` — a bug first logged 2026-07-15), **0** `ripples.json`. `PoiKind.Companion` is declared at `PoiKind.cs:55` and referenced **nowhere else in the codebase** — companion-rescue POIs are dead enum. 28 of 84 unit definitions are `debug_*` scaffolding.

The uncomfortable synthesis: **this project is in danger of being permanently pre-alpha by accretion.** Each session ships a well-engineered increment to a system that already works, and the two things standing between the build and a playable campaign — an ending, and content in the world — are the two things that keep not getting built. v3's principle #1 was "finish the open thread before opening the next." That principle has been honored *within* tracks and abandoned *across* them.

---

## 1. Where the Codebase Actually Is (audited 2026-07-31)

### Inventory

| | |
|---|---|
| C# | 257 files · 100,779 LOC · 26 subsystem dirs |
| Scenes | 21 `.tscn` (no Convergence scene) |
| Cards | 177 JSONs — **176 `ready`, 1 `wip`** (`undertow`, untested cast-trigger redirect) |
| Units | 84 JSONs — **28 are `debug_*`** |
| Companions | 20 + 20 arcs |
| Regions / Maps | 15 / 15 |
| Overworld spells | 9 files, 36 registry definitions |
| Negotiations | 8 tables, 6 archetypes |
| Encounters | 6 files |
| Buildings | 6 |
| Quests | 2 |
| Items | 22 · Archmagi 8 · Starter decks 8 |

### Track status

| Track | Status |
| --- | --- |
| **World / single-world refactor** | **Done.** WorldGenerator, StrategicView, expedition windows, corruption drain. |
| **W-track — sliding window** | **Done and playtest-verified 2026-07-15** (`a235a15`). Open: `HardWindowMode` A/B parity, patrol freeze/resume (check #9), leash exact-sum under non-debug steps, W4 remainder (staging reach estimate, overworld fringe ring). Known-not-fixed: vista-bias neighbour capture off-by-one on odd columns (`ExpeditionManager.CommitCombat`, one-line fix identified). |
| **Council C1–C5** | **Done and verified.** |
| **Council C6** | **Not started — scoped** (`docs/council_c6_scope.md`, deps all met since 07-08). Four rulings R-C6a–d still due. **Now 23 days idle.** |
| **Units U1–U3** | **Done and verified** (U1/U2/U3 core 07-08/07-09). |
| **Enemy identity U3a–U3e** | **Built 07-27/07-28** (intent cycle, triggers, defensive shapes, composition keys, resource denial); U3d/U3e playtested. **U3f (chassis inheritance) not started** — no `chassis` field in any of the 84 unit JSONs. |
| **Enemy Step 3 — bosses** | **Not started.** `BuildGuardianEncounter`, its `CampusScreen` twin, and `ResolutionEncounterBuilder.BuildOverthrowCombat` still use hardcoded `"Brute"`/`"Wizard"` arrays (14 hits). Every boss in the game is a scaled generic. |
| **Units U4–U6** | U4/U5 effectively **absorbed** by the 07-27 faction-roster pass (22 new units, all four spawn leaks closed). **U6 corrupted variants not started.** |
| **Combat UI V1–V3** | V1/V2 done+verified; V3 tranche 1 built. **V3 tranche 2, V4 (gated on E1), V5 (gated on U6) not started.** Colour-blind pass deferred. |
| **Environments E1–E3** | **Not started.** R19 step costs landed; R4 deletion done. |
| **Companions K1–K2.5** | **Done and verified.** **K3–K5 not started** (no hiring halls, no Muster screen, no loyalty deltas). |
| **Items Q1–Q3** | Built; Q3 verification checks owed. **Q4–Q5 not started.** |
| **Spells S1–S5** | **Feature-complete per v1.1** (`docs/s5_verification.md`, 18/18 checks pass). S6 Grand Rituals deferred by R14. §15 #10 enemy counter-casting unruled. |
| **Negotiation v2** | **Rebuilt 07-16/07-17** (stances, closing squeeze, spoken moves, Hall of Records, portraits pipeline, school signature moves), **tuned 07-17** (Monte Carlo), **fairness + layout passes 07-30** (patience floor, Guile retarget, unified prediction, threat markers). **v6.3 never compiled or played.** 36 portrait PNGs unpainted. |
| **Exploration / discovery loop** | Tranches 1–2, signal telegraph, secondary landmarks, reach overlay, content assembler, living-map roamer — **all built 07-18**. Tranche 3 (intel rewards, item/companion gating) never built. Blight/threat creep not built. |
| **Quest system (9 steps)** | **All 9 steps built** 07-21/07-22; Step 9 compiled and ran. Content is 2 JSONs. |
| **Fragment arcs** | All six built 07-18. Guardian boss built. **Druid has no fragment** (7 schools, 6 fragments) — unresolved. |
| **Shard zones P1–P5** | Built through P5 (Sanctuary). **P6 (content binding) not started.** Spec `docs/shard_acquisition_spec_v1.md` P6a–P9 unbuilt. |
| **Strategic layer** | Kingdom sieges, warfronts, interventions, strongholds, lunation deploy cost — **all built 07-21, none verified in-engine.** |
| **Continue Campaign / NG+** | MVP built 07-20. **Not gated on a Convergence victory** because there is no Convergence. |
| **Art — rendering overhaul** | Done 07-15 (uncommitted at the time; since committed). |
| **Art — water S1/S1.5** | **Complete**, black-triangle bug root-caused and fixed, confirmed in-game. Remaining: S1.4 flow vectors, FrozenBasin cracked-ice, S12 per-theme skies. |
| **Art — sand S2** | Built 07-24. User steps owed: rebuild, confirm Sand Texture/Normal slots **empty**, debug-launch Coast + Desert. |
| **Art — S3–S13** | **Not started.** S3 canopy meshes blocks Forest; S6 emissive blocks Volcanic; S8 snow blocks Snow; S10 road injection blocks Road. |
| **Text / localization** | 5,962-row player-facing inventory delivered as `.xlsx`. **Replacement Text column empty.** |
| **Convergence / finale** | **NOT BUILT. NOT STARTED. NO SCENE, NO TRIGGER, NO ENCOUNTER.** |

> **Post-audit update (2026-08-15):** a **progression / card-acquisition track** absent from this 07-31 table was built 08-04 → 08-15 — the full slow-reveal card-unlock system (draft-pool gate, unlock seed, SchoolMastery + Fluency, declaration, Regalia, deterministic minting) plus the `progression_card_acquisition` **§8 acquisition-verb layer**. This materially advances **G7 (Cards)** and adds a track this table never listed. Folded record in **§11**. Compiles and runs as of 2026-08-15.

### Deferred by standing ruling (do not build)

Settlement combat module (R11) · Grand Rituals (R14/S6) · campus interactions with spells (R6 — campus rework owns them) · arcane arc-signatures (v1 lock) · enemy card-casting · negotiation Module C/E cores · post-cast bucket (c) "Puppeteer" · `unstable` ability key.

---

## 2. Sequencing Principles

v3's five principles stand. Three amendments, earned by what the last three weeks actually produced:

1. **Finish the open thread before opening the next.** *(v3, unchanged.)*
2. **Dependency edges are the order.** *(v3, unchanged.)*
3. **Migrations before content.** *(v3, unchanged.)*
4. **One playable loop at all times.** *(v3, unchanged.)*
5. **Design rulings are gates, not chores.** *(v3, unchanged.)*
6. **NEW — Compile before you commit, play before you build the next thing.** A static parse is not verification. The 07-29 batch is 100+ files deep on a foundation nobody has run. Every phase below ends at a **Godot build plus a played session**, not at a clean brace count.
7. **NEW — No new system until the campaign has an ending.** Phase E is a hard gate. Combat depth, art passes, and council climaxes are all improvements to a game that currently has no win state. They wait.
8. **NEW — Content debt is now a first-class track, not a footer item.** "Authored content debt" sat in v3's §7 "Later (sketched, not scheduled)" and has stayed there for 25 days while the region count went unchanged. It gets its own phase with its own exit criteria.

---

## 3. Phase E — Compile, Verify, Commit *(days, not weeks — do this first)*

**Goal: zero unverified code. The tree builds, the last three weeks of work has been seen running, and the debug scaffolding is inventoried.**

1. **Godot build of HEAD.** Fix whatever the first real compile surfaces across the 07-29 batch (post-cast design space, card audit, the five bug-fix sessions) and the 07-30 negotiation work.
2. **Run the owed checklists** in the order they were written:
   - `claude/u3e_playtest_guide.md` (resource-denial keys — `binding_geas` 2/move is the prime suspect; try `amount: 1` before judging the key)
   - the six-item negotiation v6.3 checklist in `claude/session_log_2026-07-30_negotiation_fairness_pass.md`
   - `docs/q3_verification.md`
   - strategic-layer smoke tests (sieges, warfronts, stronghold, lunation deploy) — built 07-21, **never once run in-engine**
3. **Two negotiation JSON tuning fixes** (audited 2026-07-31 — full table in §8): Opportunist's effective patience is floored to 8, identical to Merchant, erasing its "impatient" identity; Survivor sits exactly on the floor at 6.
4. **Author `Data/Negotiations/dustreach_commander.json`.** Missing since 07-15; silently falls back to `generic_merchant`.
5. **Repo hygiene.** Present at HEAD: `src_s4_tmp.tgz` (728K), `_wtest_.tmp`, `build.log`, `FracturedArcana.csproj.old`, `FracturedArcana.csproj.old.1`, and a stale `.git/index.lock`. The bridge cannot delete — these need a hand on the keyboard.
6. **Inventory the scaffolding** (do not remove yet): 28 `Data/Units/debug_*.json`, `DebugEchoEffect` + the `debug_echo` case + its `knownAbilityKeys` entry, `ShowDebugIntentMarkers` and its four call sites. Write the removal list into this doc's §7 so it is not rediscovered at ship time.

**Exit:** the game builds; a full expedition → combat → negotiation → campus → lunation-tick round-trip has been *played*; the U3e and negotiation checklists have recorded results; no file in the tree has been shipped-but-never-run.

**Deliberately not in this phase:** any new feature whatsoever.

---

## 4. Phase F — The Finale *(the phase that matters)*

**Goal: the campaign can be won and lost. `AllArchmagiResolved()` gets a caller.**

This is the largest missing piece in the project and the only one whose absence makes everything else unfinishable. It has been named "the next big build" since 2026-07-23.

### Rulings due before code

- **R-F1 — Convergence scaling.** Fixed wall (pure power check) or partial scaling (skill still matters at the capstone)? — `progression_persistence_model_v1.md` §9
- **R-F2 — Per-year Continue condition.** Survive-to-Conjunction, or win a Convergence each year? — same §9
- **R-F3 — Escalation curve steepness.** "The one knob that sets chain length and whether Continue stays a live choice." — same §9
- **R-F4 — Fragment powers.** What does a collected fragment *do* at the Convergence? Collection is currently meta-progression with no mechanical payoff.
- **R-F5 — Druid's missing fragment.** `convergence.docx` has six schools; the game has seven. Druid players take the force path on every trial. Seventh fragment, or a Druid alignment elsewhere?
- **R-F6 — Lunation clock.** 12 lunations to the Conjunction was flagged 07-23 as "the most likely structural bust," and the step-budget check underlying shard/warfront pacing is **stale** — it assumed `PhasesPerDeploy=3` (~32 expeditions/cycle) before the 07-21 one-lunation-per-deploy ruling cut it to ~12. Redo the arithmetic before tuning anything downstream.

### Deliverables (ordered)

1. **F1 — Trigger and gate.** `AllArchmagiResolved()` gets called. Conjunction arrival with all seats resolved → Convergence available. `CampusScreen`'s hardcoded `"ConvergenceDefeat"` replaced by real victory/defeat routing. Gate `Continue Campaign` on a Convergence *victory* (currently offered at every Conjunction) — logged 07-20 as "the last big downstream piece."
2. **F2 — The encounter.** Five-phase structure per `convergence.docx`, with Phase 2 ("The Fracture") recast per `narrative_frame_intro_finale_v1.md` §6. Three paths (Restoration / Dominion / Synthesis) — the three-path structure is untouched by the recast and can be built as specced.
3. **F3 — Fragment powers** (R-F4). Six or seven fragments, each altering the Convergence.
4. **F4 — Boss substrate.** Phase-transition hook + bespoke boss units. This closes **enemy Step 3**, retires the hardcoded `"Brute"`/`"Wizard"` arrays, and gives the fragment guardians real identities instead of ×1.6-scaled generics. `shield_self`/`apply_bleed` handlers are already coded and unused. It also unblocks **V5** (boss frame + phase pips).
5. **F5 — Post-Convergence.** Victory routing into NG+; the Remembrancer's Hall (arc fields `HallEligible`/`HallBlockedText`/`HallAnchorFlag` are all ready and dormant, unlock = Moment Eternal per ruling #5).
6. **F6 — Intro frame.** The Trial + Sundering scripted encounters + campus wake-up (`narrative_frame_intro_finale_v1.md` §7). Cheapest possible version: the frame's five beats as narrative encounters. A campaign with an ending and no beginning is still better than the reverse, so this comes last in the phase and can slip to Phase H without blocking.

**Exit:** a full campaign runs start → Conjunction → Convergence → victory → Continue, on all three paths, with a fragment power changing the fight.

---

## 5. Phase G — Content Density

**Goal: the world stops being 15 regions of nothing to do. This is authoring work, not engineering work — protect it from becoming a systems phase.**

1. **G1 — Encounters.** 6 files for 15 regions. The content assembler (built 07-18) is engine + one proof skeleton; author against it. Move the Primal arc out of `generic_encounters.json` into `fragment_arcs.json` while you're in there.
2. **G2 — Quests.** 2 JSONs against a nine-step architecture. Priorities from `quest_system_narrative_spec_v1.md`: the 7 §1 campus restoration quests, `q_raise_the_anchorhold`, fluency quests `q_second_tongue_<school>`, per-region threads.
3. **G3 — Ripples and echoes.** `ripples.json` does not exist. `claude/session_log_2026-07-23_arcs_complete.md` calls this "the last dormant machinery" — the companion arc system is fully built and starved.
4. **G4 — Companion POIs.** `PoiKind.Companion` is declared and referenced nowhere. Worldgen placement + window routing + auto-recruit-on-entry. This has appeared in every parked list since 07-18 (seven consecutive logs). Companions currently carry `unlockCondition: "Rescue: found at a companion POI in the wilds"` against a POI kind that never spawns.
5. **G5 — Negotiation coverage.** 8 tables, one Commander (Frontier Wilds only), two faction-bespoke. Author per region.
6. **G6 — Shard zone P6.** Bind `fragment_arcs.json` arcs to zones via FragmentKey; zone becomes the `location_known` step; retire the free-firing seek.
7. **G7 — Cards.** 176/177 `ready`. Remaining: `undertow` (`wip`), the **response-speed gap** (Arcanist / Elementalist / Necromancer / Tinker have **zero** Reaction halves — four schools cannot use the R3 stack; floor of 2–3 each), and ~49 residual schema violations.
8. **G8 — Buildings.** 6 against the campus rework's target set. *Note: R6 says the campus rework owns this — if that rework is not scheduled, either schedule it or descope the building count explicitly. Do not leave it ambiguous a third time.*

**Exit:** every region has at least one authored encounter and one negotiation; a companion can be rescued from the wilds; a full cycle can be played without seeing the same narrative encounter twice.

---

## 6. Phase H — Depth and Polish

Only after E, F, and G. In rough dependency order:

- **C6** — council Tier C climaxes (fully scoped since 07-08, deps met, four rulings due). The single best-prepared unstarted phase in the project.
- **K3–K5** — hiring halls, Muster screen, loyalty deltas, Trusted perks.
- **E1–E3 + V4** — terrain themes, feature injector, corruption overlays, context strip.
- **U3f** — chassis inheritance (84 units onto ~20 stat blocks).
- **U6 + V5** — corrupted variants, boss retinues, boss frame. *(F4 will have delivered the substrate.)*
- **Q4–Q5** — city markets, blighted items, relics, enchanting.
- **Art S3–S13** — canopy meshes (Forest), emissive (Volcanic), snow (Snow), road injection (Road), then the rest of the punch list.
- **Text replacement pass** — apply the `.xlsx` Replacement Text column back into JSON/tscn/cs once written.
- **Scaffolding removal** — the §7 list, all at once, immediately before a build people other than you will play.
- **Colour-blind pass, accessibility, tutorial, audio** — the old action plan's Phase 6 content, still valid, still last.

---

## 7. Scaffolding Removal Checklist (run once, before any external build)

- 28 × `Data/Units/debug_*.json`
- `DebugEchoEffect`, the `debug_echo` case in `CombatManager.Triggers.BuildTriggeredEffect`, its `UnitRegistry.knownAbilityKeys` entry
- `ShowDebugIntentMarkers`, `_markerLegendLogged`, `BuildIntentMarkers`, `LogMarkerLegend`, the two-line branch in `UpdateIntentDisplay`
- **Keep:** `PredictedMoveTiles`, `Unit.SetIntentDisplay`'s `fontSize` param, `debug_surfaces` in `terrain_splat` (default off — it earned its keep in the black-triangle bisect)
- Repo root: `src_s4_tmp.tgz`, `_wtest_.tmp`, `build.log`, `FracturedArcana.csproj.old`, `FracturedArcana.csproj.old.1`

---

## 8. Negotiation Table Audit (2026-07-31)

Effective patience = `max(BasePatience, Resolve + Guile + PatienceFloorOverPool)` with `PatienceFloorOverPool = 3`.

| File | Archetype | StartTension | BasePat | Resolve | Guile | Poise | Terms | Hidden | **Effective** |
|---|---|---|---|---|---|---|---|---|---|
| frontier_wilds_commander | Commander | 5 | 6 | 3 | 1 | 1 | 3 | 1 | **7** (floor +1) |
| generic_idealist | Idealist | 3 | 7 | 1 | 1 | 2 | 4 | 1 | 7 |
| generic_merchant | Merchant | 4 | 8 | 2 | 2 | 1 | 3 | 1 | 8 |
| generic_opportunist | Opportunist | 5 | 6 | 2 | 3 | 0 | 4 | 2 | **8** (floor +2) |
| generic_scholar | Scholar | 4 | 10 | 1 | 2 | 2 | 4 | 1 | 10 |
| generic_survivor | Survivor | 6 | 6 | 2 | 1 | 2 | 4 | 1 | 6 (at floor) |
| jade_coast_merchant | Merchant | 4 | 8 | 2 | 2 | 1 | 5 | 2 | 8 |
| sunken_archive_scholar | Scholar | 4 | 9 | 1 | 2 | 2 | 4 | 1 | 9 |

**Findings:** no authored table is unwinnable — the "Commander @4 / Survivor @3" cases the fairness pass was built to rescue were simulation constructs, not shipped data. The floor does real work on exactly two tables. **Opportunist has collapsed into Merchant** (both effective 8) despite being authored as the impatient archetype; its identity now rests entirely on Guile 3 / Poise 0. Raise its Poise or exempt it from the floor. **Survivor is the tightest table in the game**, sitting exactly on the floor. Coverage is thin: 8 tables, 6 archetypes, one Commander, two faction-bespoke, and `dustreach_commander.json` still missing.

---

## 9. Risks Specific to This Ordering

| Risk | Note |
| --- | --- |
| **Phase E surfaces a large compile failure** | 100+ files across 07-29/07-30 have never been through a compiler. Budget a full session for the build alone; do not batch it with feature work. Bisect by session, not by file. |
| **Phase F is genuinely large and has no precedent in the codebase** | It is the first content-heavy set-piece the project has attempted. Mitigation: F1 (trigger + gate) alone is small and delivers a *finishable* game with a placeholder fight. Ship F1 before F2 is designed. A campaign that ends badly beats a campaign that cannot end. |
| **Phase G looks boring and will be skipped** | It has been skipped for 25 days running. The honest mitigation is a rule, not a hope: **no engineering session until each Phase-G item has an owner and a date.** If authoring genuinely will not happen, cut the region count from 15 to 6 and stop pretending. |
| **Verification debt recurs** | The pattern is structural: no .NET SDK on either machine reachable from a cloud session, so every session ends static-only. Either install the SDK where the sessions run, or accept that every session's work must be compiled by hand before the next one starts — and enforce it. There is no third option, and the current de-facto third option is "let it pile up." |
| **The lunation clock busts under a real campaign** | 12 lunations to the Conjunction, flagged 07-23, still untested; and the step-budget arithmetic behind shard and warfront pacing is stale by a factor of ~2.7 (32 expeditions/cycle → ~12). Redo it before tuning. This could invalidate shard pacing, warfront cadence, and arc-beat frequency simultaneously. |
| **Untuned knobs compound** | Guardian ×1.6, `CorruptionTidePerYear` 8, `ThreatLevelPerYear` 1, siege/warfront dials, U3e denial values, leash bands — all first guesses, all interacting, none playtested together across a full 12-lunation cycle. Tune as sets, after Phase E, against one recorded campaign. |
| **Doc proliferation** | This is the third roadmap. Delete or explicitly tombstone v3 and the action plan; keep one. The 07-21 `CampusHexGrid` contradiction (compendium says it exists, the quest-event-shim log says it doesn't, the vignette log says it does — **it does, 2 files, as of today**) is what happens otherwise. |

---

## 10. Next Three Sessions

1. **Build and play.** Godot build of `331b910`. Fix the compile. Run the U3e and negotiation v6.3 checklists. Record results.
2. **Strategic smoke test + tuning fixes.** Run the never-executed 07-21 strategic checklists (sieges, warfronts, stronghold, lunation deploy). Apply the two negotiation JSON fixes. Author `dustreach_commander.json`. Clean the repo root.
3. **F1 — make it finishable.** Wire `AllArchmagiResolved()` to a Conjunction check; route victory/defeat; gate Continue on victory; drop in a placeholder Convergence encounter. Settle R-F1 through R-F6 in the same session — they are all one conversation, and they gate everything in Phase F.

*If you get stuck on any piece — the Convergence encounter structure, fragment powers, the lunation arithmetic, the campus rework's design doc — that piece deserves its own scoped spec the way C6 got one. Ad-hoc is what produced three roadmaps.*

---

## 11. Progression & Card Acquisition — the §8 acquisition-verb layer *(folded 2026-08-15)*

*Consolidates the four 2026-08-15 session logs (the individual logs are retired into this section). "§8" throughout this section refers to `docs/progression_card_acquisition_v1.md` §8, NOT this document's §8 (the Negotiation Table Audit). **Compiles and runs — confirmed by Magos 2026-08-15.***

### The correction that started it

Project memory flagged "the card unlock system is inert / highest priority." That was **stale**. Verified against the live tree, the whole spine was already built (2026-08-04+): `CardDatabase.DraftablePool`/`WeightedDraftPool` read `EternalLedger.UnlockedCardBlueprintIds` and drop Legendaries/owned Regalia; `CardRewardScreen.GenerateOffers` uses them; `StarterDeckLoader.SeedUnlockedPool` seeds all Commons+Uncommons+starters and leaves Rares locked; `SchoolMasteryService` (Fluency 60 / Declarable 8), `DeclarationService`, `RegaliaService`, `CardMintService`, and `ProgressionSweep` all exist and are wired. The gate/mastery/Regalia/mint work is **done**.

The real gap: **52 Rares are locked at seed with no discovery path** — nothing wrote them into the unlock list in normal play, so they were undraftable AND unmintable (mint requires prior discovery). §8's acquisition verbs are that path.

### §8 verb status (all eight)

| Verb | Status | Where |
| --- | --- | --- |
| **Kill → Marginalia** | Built pre-08-15 | `ProgressionSweep.SweepMarginalia` (8 faction cards) |
| **Befriend → arc capstone** | Built pre-08-15 | `ProgressionSweep.SweepCompanionArcs` (signature → Regalia at stage 4) |
| **Library → pity-timer** | **Built 08-15** | Forbidden Archives — deterministic *named* discovery |
| **Explore → named codices** | **Built 08-15** | narrative POIs — stochastic *in-school* discovery |
| **Espionage → stolen card** | **Built 08-15** | Theft contract — stochastic *off-school* (§2a exception) |
| **Death → memorial** | **Built 08-15** | companion permadeath — signature card, "loss accrues" |
| **Negotiate → card tuition** | **Built 08-15** | cordial close — *in-school*, same-school teacher only |
| **Court → archmage card** | **Skipped** | Redundant: archmage resolution already grants Regalia + SchoolMastery |

The three deliberate discovery *shapes*: in-school stochastic (codex, tuition), in-school deterministic/paid (pity-timer), off-school (theft). All write the one permanent pool via `CardAcquisition.Discover` / `CardCommissionService`.

### What each new verb does

- **Library pity-timer** — on the Arcane Library's `forbidden_archives` T3 flag (previously *set and never consumed*). Name a locked Rare, pay gold up front, it unlocks after N lunations. The delay is the design — instant unlock would collapse the slow reveal. New save struct `CardCommission` on `EternalLedger.CardCommissions` (permanent, survives reseed; stores a remaining-count, not a due-lunation, since the calendar resets each cycle). Settles on the single lunation-tick chokepoint (`StrategicView.RunLunationTick`) with a load-time `Reconcile` self-heal.
- **Explore → codices** — adds the card analogue of `SpellReward` to `EncounterChoice`: `CardReward` (named blueprint) and `CardCodex` (roll an unknown in-school Rare). Replaces the doc's dead "Hidden Vault → legendary 10%" (Legendaries aren't draftable; it was doc-only). Three biome-tagged codex encounters authored (no new card defs).
- **Espionage → theft** — the Theft contract's successful break-in now also lifts an off-school Rare of the target court's school (kingdom → region → archmage → school, reusing `CouncilTick`'s mapping). Off-school breadth is the sanctioned §2a exception; the Marked meter is its throttle.
- **Death → memorial** — on companion permadeath (`CompanionInjurySystem.ApplyWipe`), their signature card is discovered permanently. Fires at any arc stage, so distinct from the capstone Regalia; `Discover` no-ops if already known. *Open:* the doc's "altered form" wants a distinct variant blueprint per companion — content authoring, deferred.
- **Negotiate → card tuition** — a cordial-close teacher **of your own school** who grants a spell also imparts an in-school Rare. §2a-gated to same-school (off-school stays espionage-only); bounded by the once-ever spell-teaching beat, so not farmable.

### Files

New: `CardCommissionService.cs`, `CardAcquisition.cs`, `ProgressionSaveAssert.cs`.
Edited: `EternalLedger.cs` (`CardCommission` + field), `SaveManager.cs` (load-time reconcile), `StrategicView.cs` (lunation tick), `CardLibraryUi.cs` (commission surface), `CampusGuildPanel.cs` (debug "Commission Random Rare" + assert wiring), `ShadowTick.cs` (theft card), `NarrativeEncounterData.cs` (`CardReward`/`CardCodex`), `ExpeditionManager.cs` (codex + card tuition), `CompanionInjurySystem.cs` (memorial), `Data/Encounters/generic_encounters.json` (3 codices).

Save-schema: one new struct + one field (two-structs-max held), round-tripped by `ProgressionSaveAssert` (wired to the campus "Assert Round-Trips" button). The other four verbs ride the existing `UnlockedCardBlueprintIds`, so no further assertions owed.

### Tuning knobs (empirical anchors — tune as a set, per §9 "Untuned knobs compound")

- Pity-timer: `ResearchLunations = 3`; gold Rare 250 / Uncommon 120 / Common 60; max concurrent = Arcane Library tier (3 at T3). Co-tune with the mint cap — together they are the archetype-chasing dial. If grindy, cut lunations before gold; if too cheap, raise gold before shortening the timer.
- Codex: three encounters (Snow/Tundra, Ruins/ArcaneGround, Volcanic/Desert) — watch appearance frequency.
- Theft/tuition/memorial: bounded by design (Marked meter / once-ever spell beat / permadeath), low farm risk.

### First-launch checks (retained from the folded logs)

- **Pity-timer:** Library → T3, open Card Library, Commission a locked Rare, advance lunations, confirm it enters the draft pool and is mintable. (Debug: "Commission Random Rare" seeds a 1-lunation commission.) Press "Assert Round-Trips" — now runs `ProgressionSaveAssert` too.
- **Codex:** walk a fresh school onto a Snow/Tundra, Ruins/ArcaneGround, or Volcanic/Desert narrative POI; resolve the study choice; confirm the toast names a card and it shows unlocked.
- **Theft:** commission a Theft against a court of a school you are NOT playing; advance to contract resolution; confirm the Herald line names a stolen working and the (off-school) card is unlocked.
- **Memorial:** wipe on a tier-2+/boss territory with a fielded companion; on a death roll, confirm the summary names their card and it is unlocked.
- **Tuition:** close a negotiation cordially with a same-school teacher who grants a spell; confirm the return text adds "They show you the &lt;card&gt;, too" and the card is unlocked.

### Open / next

- Author the memorial **variant** blueprints (the "altered form"), if desired — the only content piece the verb defers to.
- Playtest the acquisition dial across a full 12-lunation cycle before tuning (§9). The discovery layer is functionally **complete** pending that pass.
