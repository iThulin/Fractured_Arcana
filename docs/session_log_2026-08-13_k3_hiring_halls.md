# Session log — 2026-08-13 — K3: hiring halls (companion track resumes)

Cowork session against the live repo at `6cde576`. **NOT COMPILED — no .NET SDK in
the session sandbox. Static verification only (brace/paren delta vs HEAD = 0 on all
ten files; every cross-file symbol grepped against its declaration). Godot build is
the arbiter; first-launch checklist below.**

Track context: K1/K2/K2.5 and Q1–Q3 were already built. This session opens K3 per
`docs/companion_item_systems_v2_1.docx` §5a/§5c: hiring halls + the procedural
candidate matrix + the shared dossier surface + campus storefront retirement.
Sequencing ruling for the resumed build: K3 → K4 → K5 → Q4 → Q5 → exploration
tranche 3, one thread at a time.

## Rulings made this session (logged, not asked)

1. **Candidates ARE `Companion` records** stored inside the hall state — no
   parallel candidate schema. One new save struct total (`HiringHallState`),
   under the two-struct limit. Hire = move the record into `Cycle.Companions`.
2. **Martial procedurals get `School = "None"`**, matching all 11 live martial
   templates. The §5c matrix's school axis applies to Arcane hires only.
3. **Town halls deferred.** Towns have no interaction surface (not enterable);
   halls live where the services menu already reaches — visited cities. The §5a
   town/city quality split becomes ordinary-city vs seat/capital for now.
4. **Authored companions stay findable** (§2 "the storefront dies", but found
   people must be findable): a still-unrecruited available authored companion has
   a 20% chance per hall refresh to appear alongside procedurals, at most one,
   never in two halls at once. Encounter grants (`GrantFromEncounter`) unchanged.
5. **Unsold stock does not carry over** across lunations — halls are a flow, and
   re-rolls are seeded per (city, lunation), so closing/reopening the menu cannot
   re-roll ("no scumming"), and a sold-out hall stays sold out until the moon turns.
6. **Steward discount**: −5%/point of positive Steward Regard, cap 25%. A hostile
   Steward never surcharges — the court's displeasure has its own systems.
7. **R25 deed wired**: `hire_given`, minor positive, routed to the Steward's office
   at landing (employment is the money-man's ledger).

## New files

| File | What |
|---|---|
| `Scripts/Data/SaveState/HiringHallState.cs` | The one new save struct: CityId (CityExploreService.CityId convention), LastRefreshLunation, `List<Companion>` Candidates. Additive on `CycleState.HiringHalls`, **no version bump** (CityExploreState pattern). |
| `Scripts/Data/FeatureBuilders/CandidateGenerator.cs` | The §5c matrix: class (Fighter/Ranger ×2 weight, Arcane ×1) × trait (5) × school (8, arcane only); per-class stat envelopes anchored to live template ranges; 0–2 pre-trained stances by hall quality (quality lifts the FLOOR, not the ceiling); price from stat load + training; name pools; `hire_` ids unique per (city, lunation, index). |
| `Scripts/Systems/Strategic/HiringHallService.cs` | Lazy per-lunation refresh (no tick work for unvisited cities), FNV-1a stable seeding, stale-candidate pruning, `HirePrice` (Steward discount), `TryHire` (gold → roster move, double-charge guards at refresh AND purchase, R25 deed emission), `RoundTripAssert()`. |
| `Scripts/UI/CompanionDossier.cs` | The §8 shared people-reading surface: one card (name / class / school / trait / loyalty tier / arc / stats / stances / backstory) + host-supplied action button. K3 consumer: the hall. Future: Muster, court dispatch. |

## Patched files (surgical)

- `Scripts/Data/SaveState/CycleState.cs` — `+ List<HiringHallState> HiringHalls` (additive).
- `Scripts/Systems/Campaign/CouncilEcho.cs` — `HireGiven` const + Steward routing + attribution line (3 sites).
- `Scripts/Systems/Strategic/WorldAtlas3D.cs` — `+ public WorldSettlement ActiveCity => _activeCity;`
- `Scripts/Systems/Strategic/CityServicesHost.cs` — Create() takes the settlement; Recruit section is LIVE (dossier cards, priced Hire buttons, gold readout, in-place repopulate after hire). Market/Quests remain placeholders.
- `Scripts/Systems/Strategic/StrategicView.cs` — passes `_atlas3D?.ActiveCity` to Create (1 line).
- `Scripts/Systems/Campus/CampusCompanionsPanel.cs` — **storefront retired**: unrecruited people show `[ABROAD]` + disabled "Seek them abroad", never a price. Header copy updated. `CompanionRoster.TryRecruit` now has zero callers (left in place; delete or repoint when the hall UI is confirmed in-engine).

## Known gaps / deliberate exclusions

- **Rescue-POI recruits** (the K3 line item): `PoiKind.Companion` is still a dead
  enum. The delivery path (`companionUnlock` on narrative encounters →
  `GrantFromEncounter`) already works end-to-end; what's missing is the generator
  scattering Companion POIs. Next K3 increment — small, but it touches
  ScatterPois, which this session deliberately did not.
- Refugee-discount recruits (corruption displacement), favor retainers, Unite
  adepts: **K5**, per the spec's phase table.
- Fitness vector: **K5**, still blocked on the CouncilVocab archetype-casing
  verification (standing item).
- Dossier school coloring: `SchoolColors` keys off `CardSchool` enum; companion
  School is a string. Plain text in v1 rather than a conversion shim.

## Addendum — same day: rescue POIs (K3 complete) + CS7036 fix

**CS7036 fix:** `ExpeditionManager.OpenCityServices` was the missed third
`CityServicesHost.Create` call site (my first grep was too narrow — repo-wide
grep now confirms all three conform). Side effect, intended: halls are hireable
from the expedition map too; hires land on the roster, never in the active party.
**Hall core confirmed working in-engine by Magos after this fix.**

**Rescue POIs** (`PoiKind.Companion`, dead enum since July — now live):

- `WorldGenerator.ScatterPois`: ≤1 rescue POI per kingdom, 35% chance,
  wilderness only, same spacing rules. New worlds only (no migration needed —
  the world reseeds each cycle).
- `WorldWindowBuilder.MapPoiKind` + `ExpeditionWindow3D.MapKind`:
  `Companion → POIType.Narrative` (Seat→Outpost precedent — no new marker art).
- `ExpeditionManager.TriggerNarrativeEncounter`: recovers the world-side kind
  via `PoiAt`; a rescue POI routes to `BuildRescueEncounter()`, falling through
  to the normal pool when no one is left to find (never dead-ends).
- `BuildRescueEncounter()`: synthesized two-choice encounter riding the
  existing `CompanionUnlock → GrantFromEncounter` path. Eligible pool:
  authored, `!IsRecruited && !IsPermadead && !IsAvailable` — the complement of
  the hiring halls' pool by construction, so rescues and halls can never offer
  the same person. No gold cost (found, not bought). Declining consumes the
  POI (decisions have weight); the person isn't lost — a later rescue site
  re-rolls from the remaining pool.
- **Logged deviation from §5a:** rescued companions arrive at ArcStage 0, not
  "live arc > 0" — ArcStage is derived state owned by CompanionArcTracker's
  meta-flag sync; forcing it would desync the tracker (single-source
  discipline, "never a flag on Companion"). The arrival-with-obligation beat
  moves to K4's arc-content work.

Static verification: brace/paren delta vs HEAD = 0 on all four files;
`PoiAt` / `WorldPoi.Kind` / `EncounterChoice.CompanionUnlock` /
`EncounterAssembler.ForDisplay` (pass-through, no `{tokens}` in synthesized
copy) all confirmed against declarations. No LINQ added to ExpeditionManager
(its using list bites).

Rescue checklist addition: new cycle → find a Narrative marker in wilderness →
trigger → "A Found Person" with a gated authored companion → accept → they
appear `[ROSTER]` at no gold; decline → POI consumed, nothing granted.

## Addendum 2 — bootstrap ruling (Magos, 2026-08-13)

Ruling 4 (authored drop-in at 20%) is SUPERSEDED: the chance gate created an
onboarding desert — a fresh campaign has zero companions, the politics game
requires envoys, and the five IsAvailable starters (60–120g) were behind a
lottery while being excluded from rescue POIs (which serve only
not-yet-available people). **The drop-in is now GUARANTEED**
(`HiringHallService.AuthoredGuaranteed`): every hall carries one authored
IsAvailable companion while any remain unoffered, so the first companion is a
plan — walk to any city, hire — not a roll. Still at most one per hall, never
the same person in two halls at once; the guarantee drains as starters are
hired; arc-gated companions are untouched. First hire then bootstraps the
rest: an envoy → court standing → favors/retainers → the loop the design
intends.

## First-launch checklist (owed to the build)

1. Godot build. Fix whatever the compiler finds first.
2. Call `HiringHallService.RoundTripAssert()` once from a debug entry
   (StrategicDebug or CampusScreen._Ready, same as DumpScenarios) → expect
   `[HiringHall] RoundTripAssert PASS` → remove the call.
3. Exit criteria (spec K3): visit an NPC capital → services → Hiring Hall shows
   1–3 candidates; **two same-cell candidates are visibly different people**;
   hire one → gold falls, companion appears in campus roster and is
   party-addable; hall entry gone; reopen menu → no re-roll.
4. Lunation turn → reopen the same hall → fresh stock.
5. Campus Companions tab: no price tags anywhere; unrecruited authored
   companions read `[ABROAD]`.
6. Check the Herald's Report after a hire lands: "honest work given to the
   kingdom's own at the hiring hall" (+1 Steward-routed regard).
7. Save mid-stock → reload → hall candidates identical (the round-trip in vivo).
