# Session log — Phase 3 P3.1: enter + render NPC seat cities as districted regions

Continues from the Phase 2 work (campus float, contour-follow, district growth). Phase 3 = large
NPC cities as enterable "Locales" with three verbs (services / siege / explore, per
`world_locales_and_founding_spec_v1`). Hard-scoped per the spec: seats only, view first, reuse the
campus renderer (don't fork). This increment is **P3.1 — enter/view only** (no verbs yet).

## What shipped
A `WorldSettlement` is already a contiguous multi-tile region (`.Tiles`), so the districted-region
data exists for every city. Generalized the home-only city view to any seat city, keeping the home
path intact:

- `WorldAtlas3D`:
  - New `_cityGrounds` (transient NPC grounds, freed on leave) + `_activeCity` (null = home) +
    `ActiveCityIsHome`. `_homeGrounds` stays the persistent home campus — untouched.
  - `EnterCityView(WorldSettlement)`: home settlement routes to the existing `EnterCityMode`; an NPC
    city gets `ComputeNpcFootprint` (footprint + camera framing from `.Tiles`/centre),
    `BuildNpcGrounds` (a transient `CampusGridManager` rendering an empty `/3` district layout via
    `GenerateCityLayout` — each settlement tile = an unlocked district, contoured to the city's
    terrain), `BuildCityBorders`, then `EnterCityMode`.
  - `GenerateCityLayout` reuses `CampusMapSaveData`/`CampusGridManager` (the Locale renderer) rather
    than forking a second one, per the spec.
  - `ChildDistrictTopWorldY(child, center)` now takes a city centre (home tile or settlement centre),
    so the same contour provider serves any city; home + NPC pass their centre via a closure.
  - `LeaveCityMode`: when leaving an NPC city, frees `_cityGrounds`, clears `_activeCity`, and
    restores the home footprint + borders.
  - `HandlePick`: clicking a **discovered seat** city (Tier==City, IsSeat, not the home) →
    `EnterCityView`. Home city entry unchanged.
  - `TryPickHomeGrounds` early-returns in an NPC city (no building/annex picks yet; prevents falling
    through to the off-screen home campus).
  - `BuildCityBorders` now frees the old borders itself (it's called from 3 sites now, not just the
    home rebuild — otherwise it would leak + double-draw).
- `StrategicView`: the "Annex a district" button is hidden in NPC cities (`ActiveCityIsHome`) — you
  can't annex someone else's city.

## Deliberately NOT in this increment
No buildings in NPC cities, no services/siege/explore verbs, seats only (not second cities/towns),
NPC grounds are decorative + non-interactive. Those are P3.2–P3.4.

## Risk / what to watch
This touched the shared city-view machinery. Home path was kept intact (home still uses
`_homeGrounds` + `EnterCityMode`, `_activeCity` stays null), but verify no home regression:
- Home city still enters/leaves normally; annex still works; contour still correct.
Static-verified only (no dotnet): braces balanced across all files; all `WorldSettlement` fields
(`Tiles`, `CenterX/Y`, `IsSeat`, `IsGuildHome`, `Tier`) and `CampusDistrict`/`RebuildTilesFromDistricts`
confirmed; contour provider centres correct.

## Test
1. HOME regression: descend into the home campus (as before) → buildings open, annex works, contour
   looks right, leave returns to world. Nothing should have changed.
2. NPC city: on the world map, click a **discovered enemy seat city** tile (gold marker, not your
   violet home) → camera descends → its tiles render as a `/3` districted region (empty flowers,
   contoured, violet borders). The Annex button should NOT appear. Clicking around does nothing.
   Leave → back to world map; the NPC grounds vanish (transient).

## P3.1 polish (map readability)
- Capitals were hard to find: added a solid slate-grey **footprint tint** for every City on the
  strategic map (`TileColor` → `CityRegionTint`), reverted the tall gold pillar to a modest cube
  (it clashed with the gold staging beacons), and gave labels linear+mipmap filtering.
- Labels are **constant on-screen size** across zoom: `MakeLabel` registers each into `_scaledLabels`
  and `UpdateLabelScales` (called from `PlaceCamera`) retunes `PixelSize` from `_camDist` (ortho:
  screenFrac = worldHeight/orthoSize). Campus building labels (`HexTile.SetPoiLabel`) got a PixelSize
  bump + linear filter to counter the 1/3 grounds scale.
- Enemy city labels were invisible because **settlement generation never assigns `Name`** — added
  `SettlementDisplayName` (kingdom `DisplayName` + tier fallback, e.g. "The Boreal March Seat").
- Then it was too busy (every town labelled, duplicated), so labels are now **seats + home only**.
- Debug reveal now re-applies at runtime: the reveal checkbox floats over the live map, so
  `StrategicView._Process` watches `DebugMode && DebugRevealStrategicMap` and re-calls
  `SetRevealAll` (full Rebuild) on change. Tightened city framing (span×1.3 → ×1.0).
- TODO: proper per-settlement place-name generation at world-gen (the kingdom-based names are a
  fallback); optional label fade for dense clusters.

## P3.2 — city services menu shell (done)
Entering an enemy **seat** capital now auto-opens a floating services menu (`CityServicesHost`,
CanvasLayer over the live city — mirrors `HomeBuildingPanelHost`) titled with the city name, listing
**Market / Recruit / Quests** as placeholder sections (each a "Coming soon" disabled button). NOT the
guild's campus panels — those are bound to the guild's own save; a foreign city gets its own menu.
Driven by `StrategicView.OnCityModeChanged` (open on entering a non-home city, close on leave);
`WorldAtlas3D.ActiveCityName` supplies the title; closing the menu leaves the city back to the world.
This is the shell — the interaction loop exists; each service is built out next.

## P3.4 — Explore (city district fog) — done
Entering an NPC seat city, its `/3` flower districts start FOGGED (dim `CityFogColor`) except the
centre district (the seat you arrived at); clicking a district reveals it (restores the real terrain
colour). Reuses the existing city-view pick.
- `CampusGridManager.ApplyDistrictFog(isRevealed, fogColor)`: dims unrevealed districts, re-applies
  `ApplyVisualToTile` on revealed ones.
- `WorldAtlas3D`: `_revealedDistricts` (transient — resets each visit), `CityFogColor`, `DistrictOf`
  (child → district = round ÷3). `EnterCityView` seeds the centre district revealed; `BuildNpcGrounds`
  applies the initial fog; `TryPickHomeGrounds` in an NPC city resolves the clicked district and
  reveals it (replacing the old "NPC = no pick" gate).
- Flow: enter capital → services auto-open → close services → the fogged city → click districts to
  scout them. Transient for the shell; persisting revealed districts per-city + tying reveals to
  rewards/services is the natural follow-up.

## Next (Phase 3 sub-phases)
- P3.3 Siege — being handled separately (brother).
- P3.2 fill-in: make each service real (Market = spend gold on items/cards; Recruit = mercenaries;
  Quests = capital contracts). Decide the data model per service.
- P3.3 Siege: wire the deferred `city_streets` / `seat_walls` combat recipes.
- P3.4 Explore: a mini fog layer over the city's districts.
- World-gen: real settlement names.
