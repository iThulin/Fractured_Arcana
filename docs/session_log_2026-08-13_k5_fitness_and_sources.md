# Session log — 2026-08-13 — K5: fitness vector + recruitment sources (K track COMPLETE)

Third increment of the 2026-08-13 Cowork session (after K3 halls/rescues and K4
loyalty/perks/signatures — both verified in-engine by Magos before this began).
**NOT COMPILED — static verification only** (balance deltas 0, symbols grepped).

## The standing blocker, resolved first

**CouncilVocab archetype-casing verification (spec §6 gate): CLEAN.**
Canonical archetype set is `CourtVocab.Archetypes` = {Merchant, Commander,
Scholar, Idealist, Opportunist, Survivor}, PascalCase, negotiation-shared. Both
live comparisons in CouncilEcho use valid members. The `"Soldier"` /
`"Brute"` / etc. strings are `ArchmageEnemySlot` / unit JSON archetypes — a
different vocabulary, no collision. The §6 table was therefore filled.

## Fitness vector (§6) — `CouncilTick.FitnessMod` rebuilt

The C2 stub (ArcStage≥4 → 1) became the four-term vector, clamped [−3,+3],
still ×15 on the d100 at all three roll sites:

| Term | Value |
|---|---|
| Standing (loyalty) | Wary −2 ("they are not yours"), Sworn +1 |
| Experience | ArcStage 4 → +1 (the old stub, preserved as one term) |
| Archetype matchup | trait vs TARGET courtier's archetype, ±1 from the new `CourtVocab.TraitArchetypeAffinity` table (one affinity + one friction per trait; K5 STARTING VALUES, tune there) |
| School | envoy school == court's archmage school → +1 (via Kingdoms → TemplateRegionId → RegionArchmageMap → ArchmageRegistry) |

Call sites: gifts (full vector), secret sweep (no target — archetype term sits
out), smear (full vector — the smear's target IS the counterpart). A Wary
mismatched envoy now swings −45; a Sworn matched veteran +45. The spec's K5
exit criterion ("a mismatched envoy underperforms a matched one on identical
missions") is now mechanical fact.

## Recruitment sources (§5a remainder)

- **`RecruitmentSources.cs`** (new): Unite adepts + favor retainers, both
  rolled through the K3 matrix via new `forceClass`/`forceSchool` params on
  `CandidateGenerator.Generate` — one people-generation surface.
- **Unite adept**: both resolution sites (ExpeditionManager + CampusScreen
  `HandleResolutionChoice`) call `OnArchmageUnited` — an Arcane candidate of
  the united school, seat quality, free, idempotent per archmage
  (`hire_unite_<id>` existence check).
- **Favor retainer — SCOPE RULING (logged)**: spec offered "any Major favor";
  implemented as the **Arcane Major** call-in specifically, because Arcane was
  the one favor type with no field effect ("no field effect yet") — fills the
  empty slot with real behavior, overloads nothing, needs zero new UI. Arcane
  minor still refuses without consuming. The "patron watches how you treat
  them" echo hook is copy-only for now — noted for espionage/court work.
- **Displacement refugees** (in `HiringHallService.RollStock`): when any other
  kingdom's region sits at CorruptionLevel 2+, 40% chance per hall refresh of
  one extra candidate at 60% price. Enters at 50 like everyone (v1 locked).
  **SIMPLIFICATION (logged)**: "adjacent halls" widened to any hall outside
  the collapsing kingdom — no kingdom-adjacency table exists, and roads carry
  the desperate far. Tighten to border-pressure pairs if it ever matters.

## Files

New: `Scripts/Data/FeatureBuilders/RecruitmentSources.cs`.
Patched: `CouncilState.cs` (TraitArchetypeAffinity beside CourtVocab),
`CouncilTick.cs` (FitnessMod + 3 call sites), `CandidateGenerator.cs`
(force params), `HiringHallService.cs` (refugees), `ExpeditionManager.cs`
(Unite hook + Arcane call-in), `CampusScreen.cs` (Unite hook).
No new save structs — every new person is a `Companion` in the existing list.

## First-launch checklist

1. Build.
2. Dispatch a Wary envoy and a Sworn ArcStage-4 envoy on the same mission type
   at the same court: outcomes should visibly diverge over a few tries
   (−30-to-−45 vs +30-to-+45 swing).
3. Unite an archmage: toast "…is seconded to the guild", adept of that school
   in the roster, free. Re-resolve via debug: no duplicate.
4. Call in an Arcane MAJOR favor in the creditor's territory: retainer joins,
   favor consumed. Arcane minor: refused, favor kept.
5. Debug a region to CorruptionLevel 2+, refresh a hall elsewhere across a
   lunation: eventually a discounted "Displaced by the corruption's spread"
   candidate appears (~40%/refresh).

## K track status: COMPLETE (pending compile + the checklist)

K1–K5 all built. Remaining companion-adjacent work, deliberately deferred:
the Muster screen (§8 — a UI consolidation pass, not a K phase; the dossier
card is ready for it), dossier perk/signature display lines, rescue-arrival
arc beat (arc content work). Next per the session's sequencing ruling:
**Q4** (city markets, blighted items + Cleanse, archmage relics).
