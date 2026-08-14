# Session log — 2026-08-13 — Exploration tranche 3: intel + item/companion gates

Final open item of the original 2026-08-13 request (companions → items →
exploration). **NOT COMPILED — static verification only** (balance deltas 0 on
five files; `_grid.Hexes` / `RevealHex` / `IsLandmark` / `List.Exists` all
checked against declarations; encounter JSON re-parses, 17 entries).

## Scope, reconciled against what already exists

The discovery-loop spec's T3 = the `RevealPois` intel verb ("information is
the primary resource") + item/companion gates on choices. The T2 log's other
deferral — the Companion POI through worldgen — was ALREADY BUILT this
morning as K3's rescue POIs; retired without new work.

## Built

**Gates** (`EncounterChoice.RequiredItem` / `RequiredCompanion`):
- RequiredItem — choice surfaces only if the Armory owns the item id.
- RequiredCompanion — only if that companion is in the ACTIVE fielded party.
- Same omit-when-unmet convention as RequiredFlag (hidden doors, not greyed
  teases — panel precedent). Wired as predicates through
  `NarrativeEncounterPanel.ShowEncounter` at all three hosts: expedition
  (loop-based, no LINQ in ExpeditionManager), city events, campus narratives.

**Intel verb** (`EncounterChoice.RevealPois = N`):
- `ExpeditionManager.RevealNearestPois`: N nearest hidden, unconsumed POI
  hexes revealed as landmark BEACONS — `IsLandmark` set BEFORE the fog write
  (the 08-08 ordering lesson), distance from the party's hex, returns actual
  count (window may hold fewer). Banner: "Intel: N sites marked on the map."

**Authored consumers** (3 new encounters in `generic_encounters.json`):
- *The Old Scout's Cairn* — Wayfarer's Map-gated 3-POI reveal; ungated 1-POI
  fallback (the item makes you better at it, not solely able).
- *The Captured Courier* — Wren Holloway-gated free 3-POI reveal (the K3
  starter earns her keep); 25g for 2; a kindness option.
- *Signal Mirrors on the Ridge* — Mountain/Hills-tagged: 2 steps for 2 POIs
  (intel priced in the run's real currency).

## Known caveat (logged, accepted)

City/campus narrative hosts don't handle `RevealPois` (no expedition window)
— a generic intel encounter drawn by a CITY event resolves its other deltas
but reveals nothing. Rare and harmless; if it grates in play, tag the intel
encounters to expedition-only terrain or add a city fallback (gold for the
worthless map). Deliberately not invented now.

## First-launch checklist

1. Build.
2. Expedition → find a narrative POI until *Old Scout's Cairn* draws: without
   the Wayfarer's Map the gated option is ABSENT; buy/equip the map (city
   market stocks it) → option appears → 3 beacons bloom on the fog.
3. *Captured Courier* with Wren fielded → free 3-POI option present; bench
   her → absent.
4. Beacons render with the landmark ring/glow, not plain revealed hexes.
5. `revealPois` on a step-poor run: Signal Mirrors' 2-step price refuses
   nothing but hurts correctly (StepDelta floor 0).

## Tranche 3 status: COMPLETE pending compile. The original three-track
request (K, Q, exploration) is fully built. Blight/threat-creep surfacing
(discovery spec Layer E) remains deliberately unbuilt — it is a Phase G /
living-map decision, not a T3 item.
