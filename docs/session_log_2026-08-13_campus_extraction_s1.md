# Session log — 2026-08-13 — CampusScreen extraction, session one

**NOT COMPILED — static verification only** (balance deltas 0; symbols
checked). Part of the ruled two-session plan to retire the full-screen
CampusScreen: session one re-homes what floats cheaply; session two moves the
cycle-transition seam; then the scene is a deletable shell. **Do removal
AFTER Convergence I2** so the finale routes against the new home once, not
twice.

## The audit that set the scope

Context-verb consumption of the three non-floatable panels:
- CampusQuestsPanel: `Ctx.ShowNarrative` ×1 — trivially floatable once the
  city view can host a narrative.
- CampusCouncilPanel: `Ctx.ShowNarrative` ×1 — BUT its narratives carry
  ResolutionKind / LaunchGuardian (archmage resolution, guardian trials):
  the deep seam. Stays on the overlay this session.
- CampusExpeditionPanel: BeginNextCycle + EnterStrategicMap + Host +
  RefreshGold — the cycle seam. Session two, alongside I2's aftermath
  routing.

## Built

- **`StrategicView.ApplyNarrativeOutcome`** — the shared non-expedition
  narrative applier, extracted from the city-event handler and brought to
  parity with CampusScreen's (adds: ItemReward, CompanionUnlock,
  reputation, LoreId, companion-arc advance with toast). Side benefit: city
  district events now pay item/companion/rep/lore rewards — the "deferred"
  note from the Phase-3 explore log is retired.
- **`ShowFloatedPanelNarrative`** — floated panels' narratives host on the
  city narrative layer with full T3 gating (flags/school/gold/item/
  companion), outcome through the shared applier, then
  `RefreshHostedPanel()` re-reads the panel.
- **HomeBuildingPanelHost**: optional `showNarrative` delegate wired into
  the hosted CampusContext (no longer an inert stub); `RefreshHostedPanel`;
  **Quests added to CanFloat/CreatePanel**.

## Where the retirement now stands

Floating from the city: Guild, Companions, Armory, Training, Records,
Workshop, **Quests** (7 of 10). Overlay-only: Council (resolution
machinery), Expedition (cycle seam), Campus tab (rotation placement tool —
the city construct card covers anchor-at-click already). Session two closes
those three + moves BeginNextCycle onto the strategic scene, then delete.

## Addendum — landmarks clickable in city view (Magos screenshot)

The gold landmark labels were dead — `OnHomeLandmarkPicked` was a Phase-2
no-op ("beats live inside the campus systems"). The session-one host is
exactly what it was waiting for: landmark click → `GetEncounter(HasFlag)` →
the restoration beat floats over the live city → shared applier → and an
`onApplied` hook rebuilds the 3D grounds so ruined → active → restored
restamps immediately. Fully-restored landmarks toast "— restored." Verified
first: landmark beats are PURE narrative (no LaunchGuardian/ResolutionKind
anywhere in CampusLandmarkData), so nothing routes into the unsupported
verbs. Also same-day: the declump pass (sanctum → arcane plaza, infirmary →
martial ring, library wing → arcane ring; the shared corner tile freed for
the annexation reveal it exists for).

## Checklist

1. Build. City view → click the quests building (scriptorum-adjacent —
   whichever hosts "quests") → Quests panel floats.
2. Trigger a quest narrative from it → encounter appears over the city,
   choices gate correctly → outcome applies (gold/flags/etc.), panel
   refreshes in place, no full-screen campus.
3. City district EVENT with an item/companion reward → now actually grants
   (the deferred verbs).
