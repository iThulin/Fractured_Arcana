# Fractured Arcana — Build Order v3

*Sequencing roadmap · 2026-07-06 · SUPERSEDES the sequencing in guild_expansion_action_plan (the "Guild of Wizards" six-phase plan). That document's guiding principles survive; its phase contents do not — it predates the single-world refactor, Court & Council, the unit registry, the overworld spell system, and the 2026-07-03 ballot rulings.*

*Scope anchored in: single_world_refactor_v2 · court_council_system_v1_1 · archmage_unique_units_v1_2 · combat_ui_v2_1 · combat_environments_v1_1 · companion_item_systems_v2_1 · overworld_spell_system_v1_1.*

---

## 1. Where the Codebase Actually Is (audited 2026-07-06)

| Track | Doc | Status in code |
| --- | --- | --- |
| World (1a–1c) | single_world_refactor §8 | **Done.** WorldGenerator, world array, StrategicView, expedition windows, zoom, corruption drain reading world tiles. |
| World (Phase 2, living world) | single_world_refactor §8 | **Done / in verification.** CouncilTick + CorruptionSpread + kingdom drift live. |
| Council C1–C3 | court_council §14 | **Done and verified** (C3 fully: call-ins, petitions, obligation decay, ledger persistence). |
| Council C4 (Word Spreads) | court_council §14 | **Done and verified (2026-07-08).** Verification queue (Sessions C, D, F + regressions) run and passed. |
| Council C5 | court_council §14 | **Done (2026-07-08).** Exposure ladder + Scandal/Expulsion/Imprisonment thresholds; Imprisonment → Prison POI rescue (release keyed on stable world coords, not a `Pois` list index); Court a Courtier + **archetype-typed** Patron token feeding the negotiation pool; Rumor/Discredit. Save round-trip assertions for HeraldReport / CourtState.StandingPenalty / ImprisonedEnvoy (`CouncilSaveAssert`, debug-panel button). |
| Council C6 | court_council §14 | **Not started — scoped.** Tier C climaxes, Broker the Compact → Allied, Unite/Coerce standing gates, Astrologer agent + deflection + Expose the Agent, Hall of Records renown. Hard deps (negotiation encounter system, ArchmageDisposition, C1–C5) all met → buildable now despite its Phase-D placement. See `docs/council_c6_scope.md`. |
| Units U1 | archmage_unique_units §13 | **Done (2026-07-08).** UnitRegistry + UnitDefinition + Data/Units/generic_* (5 defs at verified stat parity); EnemyArchetypeData is now a facade over the registry; EncounterPoolLoader resolves unit-id aliases; PendingEnemySpawn carries the resolved Def (U2 seam). Parity + round-trip assertion wired ("Assert Units" debug button). |
| Units U2 | archmage_unique_units §13 | **Done and verified (2026-07-09).** PlanIntent dispatches on BehaviorKey (string → handler map); melee_hunt_wounded (stalker, lowest-CURRENT-HP ruling affirmed) added; five tag hooks (pack/bulwark/charge/scout/immobile) around the intent plan/execute split; EnemyArchetype enum + EnemyArchetypeData facade DELETED; EnemySlot/spawns rekeyed off unit ids; debug launcher roster registry-driven; six debug_* tagged defs remain as standing tag fixtures. Sessions A–F passed (`docs/u2_verification.md`). Shipped alongside: threat tiles moved to the always-visible kind tier + INTERIM ◆ tile reticle — V2/V3 must replace it (see verification doc). |
| Units U3 | archmage_unique_units §13 | **Done and verified (2026-07-09, Sessions A–F all passed).** Enemy trigger bus (CombatManager.Triggers.cs): death triggers queue → push as first-class StackItems on the RulesManager stack → priority window before EACH resolution → AI auto-passes; auto-pass costs zero clicks unless a castable Reaction is held or DebugStopOnTriggers set; Reaction-speed response casts land on top (LIFO). UnitDefinition.Abilities[] (cap 2, registry-asserted); summon seam registry branch (SpawnRegistryUnit); conductor roster (Honored Dead/Wake-Keeper/Final Service) + Necromancer.json pools rekeyed; CheckCombatEnd defers while triggers outstanding; INTERIM CombatUI priority prompt (V3's stack panel replaces). Only death call sites wired — U4 rosters bring their own. Queue: `docs/u3_verification.md` (Sessions A–F). |
| Units U4–U6 | archmage_unique_units §13 | **Not started.** U4 next after U3 verifies (or V2 first per Phase B ordering — V2 needs only U1–U2). |
| Combat UI V1 | combat_ui §14 | **Done and verified (2026-07-09) on Mac + 4K Windows.** Open watch item: ultrawide left-offset perception — `[HandFan]` paired diagnostics shipped, window-position suspect documented in v1_verification.md; debug with hardware later, not a gate. Post-playtest additions: Enter ends turn (guarded against blind-passing U3 windows), hint line rides the banner, symmetric hand reserves. Design-space constants in UITheme (canvas_items/1920×1080 were already set; 1280×720 minimum added); DeckUiManager dead HandBound* exports deleted, fan geometry on UITheme constants; §5 redistribution: banner top-center, End Turn bottom-right, party chips + 3-line ticker (click = history popup) bottom-left, hint + deck/grave flanking the fan; left panel = unit card + attunement only; U3 priority prompt relocated top-center. Fixed in passing: _hintLabel was never constructed (SetHintText was a no-op). Checklist: `docs/v1_verification.md`. |
| Combat UI V2 | combat_ui §14 | **Done and verified (2026-07-09).** Role/FactionId/IntelDescription schema; UIContent plain-language strings; roster v2 (role markers, nameplate policy, faction-tinted bars, ability chips, acting-unit marker, row hover = world hover); enemy inspect blocks; threat-range overlay (reach+attack envelope, tag-aware, per the §7a supersession); deployment intel role markers. ScoutReportPanel names landed in U2. Checklist: `docs/v2_verification.md`. |
| Combat UI V3 | combat_ui §14 | **Tranche 1 built (2026-07-09), in verification** (`docs/v3_verification.md`): §7c stack strip (interim prompt DELETED — strip is display-only during auto-pass, interactive during windows, live-updates on response casts), §9 FormatLogLine grammar (Requiem/Deathburst routed; Unit.AbilityUseCounts), ranged_charge dot (○/✸). **Tranche 2 gated on U4/U5 keys:** R22 damage preview (real-resolver sim), EveryN pips, mimic next-mode glyph, Mirrorstep/Rewind spent markers, aura extents. |
| Combat UI V4–V5 | combat_ui §14 | **Not started.** V4 needs E1 (context payload); V5 needs U6. **§7a SUPERSESSION RULED (2026-07-09):** §7a's case against exact intent preview is overtaken — the live AI (post-doc intent rework) locks real plans at end of enemy phase; there is no simulation to drift (§7a's maintenance argument is structurally moot: the telegraph IS the AI's own locked state, not a parallel model). The identity argument survives in modified form: intents telegraph THIS turn's locked commitments (already shipped: glyphs + U2 reticles); V2's threat-range overlay telegraphs NEXT turn's reach envelope on hover (Fire Emblem danger zone). The two are complementary layers, not competitors — locked present + probabilistic future. V2 builds the overlay as specced; §7a's prohibition is void; combat_ui doc needs a v2.2 pass to record this. Confidence high on the maintenance point, moderate on identity (playtest may show double-telegraphing reads as clutter — if so, the overlay becomes hover-only-on-roster, not world hover). |
| Environments E1–E3 | combat_environments §9 | **Not started.** R19 step costs LANDED (OverworldMovementCost has Hills/Desert/Tundra/Snow cases, tuned past the starting values). R4 deletion **DONE (2026-07-08)** — ReclassifyTerrainPerRegion + its palette cache removed from WorldGenerator.cs. |
| Companions K1–K5 | companion_item_systems §10 | **Not started.** ComputePartyBaseHP exists but is the OLD formula (full BaseHP, no floor/2, no loyalty bonus) — K1 is a change, not a creation. No injury state, no hiring halls, no Muster screen. |
| Items Q1–Q5 | companion_item_systems §10 | **Q1 partial.** Equipment loadouts apply at spawn (BuildEquipmentLoadouts); passive dispatch still the old ItemPassiveTag path. Q2 blocked on U3. |
| Spells S1–S6 | overworld_spell §14 | **Not started.** No Data/OverworldSpells, no GrimoireState. |
| Card content | — | 162 cards: Elementalist set `ready` (20), all seven other schools `wip` (142, Adept and Tinker included). Tinker set complete per handoff package — merge + flip statuses. |
| Authored content | — | Thin everywhere: 2 encounter pools, 2 negotiations, 6 buildings, 15 regions (most with nothing to route to). |

**Explicitly deferred by ruling (do not build):** settlement combat module (R11, spec frozen in combat_environments §6), Grand Rituals (R14, interface frozen), campus interactions with spells (R6 — the campus rework owns them), arcane arc-signatures (v1 lock), enemy card-casting (follow-on after R3 stack).

---

## 2. Sequencing Principles

1. **Finish the open thread before opening the next.** C4's verification queue is half-run; an unverified echo pipeline under three new systems is unfindable-bug territory.
2. **Dependency edges are the order.** U1–U2 → V2 · U3 → V3 and Q2 · E1 → V4 · U6 → V5 · C5 → C6 · Tier C climaxes (C6) consume the negotiation encounter loader.
3. **Migrations before content.** The U-track exists so that eight rosters, item procs, and future bosses land on one dispatcher. Author nothing against the enum that will die.
4. **One playable loop at all times** (the one principle carried verbatim from the old plan). Every phase below ends at a verifiable state; U1's exit is behavioral parity, so the loop never breaks.
5. **Design rulings are gates, not chores.** Where a phase has a "ruling due" flag, the ruling happens before the code.

---

## 3. Phase A — Close the Books (short)

*Goal: no open verification, no queued patches, canonical card statuses.*

- ✅ **Done (2026-07-08).** C4 verification queue run and passed: Sessions C, D, F + regressions (E3 boundary landing).
- **Removed from Phase A scope (2026-07-08).** The Finding 2 question (courier-station echo delay — verified a no-op under current tick ordering) is deferred: Courier Station is being reworked entirely, and the rework owns its court effect. No longer a Phase A gate.
- ✅ **Done (2026-07-08).** Queued live patches landed: R4 (ReclassifyTerrainPerRegion + palette cache deleted, dead call removed); WorldDebug `k.Stance` clear (no references); CameraController HandleZoom clamp present.
- ✅ **Done.** Tinker handoff merged; all Tinker card statuses `ready`.
- ✅ **Done.** The three-standing-systems ruling: KingdomStance is derived-only (stored Stance removed → `CouncilQueries.StanceFor`); FactionReputation survives for non-kingdom factions; court standing is the single source of truth. The phase's real deliverable.

**Exit — MET (2026-07-08).** C4 verified in the doc trail; the standing ruling recorded; queued patches landed; Finding 2 descoped (Courier Station rework owns it). **Phase A closed.**

---

## 4. Phase B — The Reading Game (units + combat UI core)

*Goal: enemies are data, abilities are on the stack, and the HUD can say so. This is the largest phase and the prerequisite for almost everything downstream (item triggers, boss content, corrupted variants, environment context).*

Ordered deliverables:

1. **V1** (no dependencies — start immediately, even in parallel with Phase A): canvas_items stretch + 1920×1080 design space, hand-bound migration, layout redistribution. Cheapest moment is before new elements exist.
2. **U1 — DONE 2026-07-08.** UnitRegistry + UnitDefinition + five generic_* definitions + loader aliases + PendingEnemySpawn Def seam. Stats moved to Data/Units JSON behind a facade (zero call-site change → parity by construction); parity+round-trip asserted via the "Assert Units" debug button. Playtest exit (one encounter per tier reads identically) is the user's to confirm in-engine.
3. **U2**: BehaviorKey dispatch + the five tags + stalker. (This retires the deferred Druid wildlife dispatcher thread — it lands here as `pack`/`scout` etc., not as its own build.)
4. **V2** (needs U1–U2): EnemyRosterRow, nameplates, faction-tinted bars, inspect blocks, threat-range overlay, deployment intel upgrade, ScoutReportPanel name pass-through.
5. **U3** (the phase's heavy lift, R3 stack-first): stack objects, priority windows, auto-pass/stops, Long Table keys, conductor roster end-to-end. The existing PriorityManager is the foundation; U3 makes enemy triggers first-class on it.
6. **V3** (tracks U3): ability state widgets, aura extents, log grammar via FormatLogLine, stack panel, R22 damage preview (real-resolver simulation mode).
7. **Q1 completion — DONE (2026-07-09):** spawn-time parity assertion inside ApplyEquipmentLoadout — baseline captured pre-apply, every stat verified post-apply (`[Q1 Parity] ... verified item-for-item` / PushError on mismatch). Q2's floor exists. Verify: equip any item, spawn into a fight, see the parity line.

**Exit:** Deathburst enters the stack, auto-passes with zero clicks when no response is held; a mixed line+elite encounter reads at every ladder rung; all pre-existing encounters fight identically to pre-migration.

**Deliberately not in this phase:** rosters beyond the conductor (U4–U5), corrupted variants (U6), boss frame (V5), context strip (V4 — needs E1).

---

## 5. Phase C — People and Things (the demand economy)

*Goal: the "I hire good people" pillar becomes mechanical: pool HP with loyalty, injury as the third demand, hiring in the world, item passives on the shared bus, and the intrigue tier of the court game.*

1. **K1**: rewrite ComputePartyBaseHP to the v2.1 formula — `20 + Σ floor(BaseHP/2) + loyalty bonus (Devoted +2, Sworn +4)` — with pool readout at launch. Small diff, big tuning consequence; do it before K2 so injury math tunes against the real pool.
2. **K2**: injury/death triggers (§5b tiers, Sworn −10 death) + infirmary recovery on the lunation tick (Training Grounds interim host per R24).
3. **Q2** (needs U3, now done): item passives migrate to the trigger bus. One OnAttack, one OnSpawn, one Aura item through the shared handler map; procs in the log grammar.
4. **Q3**: CorruptionWard/HazardWard + overworld/court passive families + the tier×2 cap and floor-1 rule.
5. **K3**: recruitment v2 — hiring halls, procedural candidate matrix, dossier panel, rescue-POI recruits; campus-menu recruiting retired. Includes the **Muster screen** (§8) — party + loadout + (later) grimoire in one surface. Build Muster here even though grimoire slots are empty until Phase D; it's the natural host.
6. **C5 — DONE 2026-07-08** (was gated on the Phase A standing ruling): Rumor/Discredit, Exposure thresholds, Scandal/Expulsion, Imprisonment → Prison POI rescue, Court a Courtier + Patron token wiring into negotiation pools. See §1 status row.

**Exit:** a tier-2 wipe injures per the rolls and understaffs the next two lunations; a hired procedural candidate is visibly different from their cell-twin; CorruptionWard measurably reduces attrition under the cap; exposure 10 spawns a rescue expedition that works.

**Watch item (from companion doc §1):** demand ratio 1.5–2× fieldable companions is a first-class tuning target from the moment K2+C5 both exist — this is the first phase where the player can be genuinely understaffed.

---

## 6. Phase D — The Magic Layer and the Court's Climax

*Goal: wizards feel like wizards between fights, and the council layer completes its arc into the archmage pipeline.*

1. **S1–S2**: spell schema/registry/GrimoireState/Essence pool → Grimoire panel, Essence bar, cast mode, player-school innates. (S1's save round-trip assertion is non-negotiable — the EchoesInFlight precedent.)
2. **S3**: all eight school sets + companion-granted casting (+1 Essence, Adept waiver) — this retroactively enriches every K3 hiring decision.
3. **U4–U5**: the remaining seven faction rosters + their ability keys, landing per-roster with V3's widgets already live.
4. **E1–E3**: TerrainThemeMap + FrostSteppe/SunbakedBarrens; FeatureInjector (river edges, bridges, coasts, roads); corruption overlays. Then **V4** context strip (valence tags everywhere; Witnessed badge stays deferred with R11).
5. **S4–S5**: acquisition (lore POIs, negotiation deals, scrolls) + echo/corruption integration (SpellcraftAid/Transgression, Parley Compulsion end-to-end).
6. **C6**: Tier C interactive climaxes preloaded from court state; Broker the Compact → Allied; standing gates on Unite/Coerce; Astrologer agent + deflection + Expose the Agent; Hall of Records renown. **Scoped in `docs/council_c6_scope.md` (2026-07-08); dependencies met, so buildable ahead of the rest of Phase D if the council track keeps priority.**

**Exit:** the doc-specified full arc — build a court from Unknown to Trusted across a cycle and Unite an archmage entirely through the council layer — plus an Overt necromantic cast landing a traceable echo, plus every generator terrain launching onto a real theme.

---

## 7. Later (sketched, not scheduled)

- **U6 + V5**: corrupted variant selector, Keeper bleed-through, boss retinues, betrayal second-phase swaps, boss frame. Then the **archmagi-as-units** content pass (explicitly after combat_ui per units §9).
- **Q4–Q5**: city markets, favor redemptions, blighted items + Workshop Cleanse, 8 authored relics; enchanting tiers + signature binding.
- **K4–K5**: loyalty delta hooks, Trusted perks, ArcStage signatures; fitness vector into court missions (blocked on the CouncilVocab casing verification).
- **Card content push**: 142 `wip` → `ready` school by school — schedule as authoring blocks, one school per block, against live telemetry. **Includes the response-speed gap (U3 Session E finding, 2026-07-09):** Reaction halves per school are Chronomancer 9 / Adept 3 / Druid 1 / Enchanter 1 / Arcanist·Elementalist·Necromancer·Tinker 0 — four schools cannot use the R3 stack. Floor of 2–3 school-flavored reaction halves each, authored within their school's block. |
- **Authored content debt**: encounter pools and negotiations for the 13 regions that have none; buildings 6 → the campus rework's target set.
- **The campus rework** (R6): owns spell grants/upgrades, Essence modifiers, infirmary's final home, building set. Blocked on its own design doc — the old guild_campus_v2 is pre-refactor and should be superseded, not implemented.
- **Settlement combat un-defer** (R11): E4–E5 + §6d echo escalations, spec frozen.
- **Grand Rituals** (R14): frozen interface, later cycle.
- **Weather layer**: owns snow/sand movement mechanics (R19's costs are placeholders for it) and returns Stormcall.
- **Cycle endgame / final battle**: the convergence doc predates the Kassian/Conjunction frame in the newer docs; needs its own supersession pass before any endgame build.
- Unruled: enemy overworld casting (spell doc §15 #10); Tinker construct/summon convergence into UnitRegistry (units §14 #7, "after U6").

---

## 8. Risks Specific to This Ordering

| Risk | Note |
| --- | --- |
| U3 scope (stack-first, per R3 against the units doc's own recommendation) | It is the critical path for V3, Q2, and all roster content. If it balloons, everything queues behind it. Mitigation: U3's scope is exactly one roster's keys; resist landing U4 keys "while in there." |
| Phase B is UI+plumbing with little new player-facing content | Accepted: it is the enabling investment for every content phase after it. The Tinker/card status merge in Phase A keeps some content motion visible. |
| Three new systems (units, spells, injuries) all writing save data | Save-file paranoia rule applies to each: round-trip assertion per new struct, including mid-expedition saves, before the phase closes. |
| Demand-economy tuning (K2 + C5 + envoys) can strand a playtest roster | Tune the injury durations and death percentages against the 1.5–2× ratio target, and keep the Sworn armor visible in the dossier so the player can reason about risk. |
| The old action plan's phase names still circulate | This document supersedes its sequencing; mark the old doc superseded in the repo per the one-authoritative-doc rule. |
