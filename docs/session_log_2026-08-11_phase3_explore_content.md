# Session log — Phase 3 explore content (framework + events + services)

Deepens the P3.4 explore shell (fog + click-to-reveal, transient) into a real content layer:
each district of a visited NPC city holds typed content, revealing shows a marker, and clicking a
revealed district triggers it. First increment per the confirmed scope: **framework + events +
services**, **persisted per city (saved)**. Fights and story beats are visible but STUBBED.
Siege (P3.3) untouched — brother's system.

## What shipped

### Persistent per-city content model (additive save — no version bump)
- `Scripts/Data/SaveState/CityExploreState.cs` (new): `DistrictContentType` enum
  (Empty/Service/Event/Story/Fight), `CityDistrictEntry` (Dq/Dr, Content:int, ContentRef, Revealed,
  Cleared), `CityExploreState` (CityId, Generated, Districts).
- `CycleState.CityExplore` (new `List<CityExploreState>`): cycle-scoped (the world reseeds each
  cycle), keyed by CityId. Additive field; `SaveManager.CURRENT_VERSION` unchanged (still 102).

### Generation service
- `Scripts/Systems/Strategic/CityExploreService.cs` (new): `CityId(city)` = `"{KingdomId}:{Cx},{Cy}"`
  (stable within a cycle); `GetOrGenerate(cycle, city, districtDeltas)` fetches or generates + persists;
  `FindDistrict`. Content is assigned **deterministically** from an FNV-1a hash of the city id XOR'd
  with district coords, so a regenerated state matches. Centre district (0,0) is always **Service**
  and starts **Revealed** (the seat you arrive at). Non-centre weights: Event 35 / Fight 25 /
  Story 15 / Empty 25.

### Reveal + markers (WorldAtlas3D)
- `_cityExplore` (active city's persisted state), `_cityContentMarkers` (transient glyphs),
  `DistrictContentTriggered` event (→ host).
- `EnterCityView`: get-or-generate the state from `SaveManager.ActiveSave.Cycle`, seed
  `_revealedDistricts` from persisted `Revealed` entries, then `RebuildCityContentMarkers`.
- `DistrictDeltas(city)`: the axial deltas GenerateCityLayout renders (kept in sync with it).
- `RebuildCityContentMarkers` / `MakeContentMarker`: one billboarded `Label3D` glyph per revealed,
  uncleared, non-Empty district, floating above the district-centre child tile
  (`_cityGrounds.GetTileView((3dq,3dr)).GlobalPosition`). Glyphs: Service ⚒ gold, Event ? cyan,
  Story ✦ violet, Fight ⚔ red. NoDepthTest + outline so they read over the tiles.
- `RefreshCityContentMarkers()` (public): host hook after a district is cleared.
- `TryPickHomeGrounds` NPC branch is now **reveal-or-trigger**: a fogged district → scout it
  (reveal, persist via `SaveManager.Save()`, rebuild markers); a revealed district with live
  content → `DistrictContentTriggered.Invoke(entry, city)`.
- `LeaveCityMode` NPC branch frees the markers and drops `_cityExplore` (the persisted entries live
  on in `cycle.CityExplore`).

### Trigger dispatch (StrategicView)
- Subscribes `_atlas3D.DistrictContentTriggered += OnDistrictContentTriggered`.
- **Service** → `ShowCityServices()` (reopenable — never cleared).
- **Event** → `TriggerCityEvent`: pick from `NarrativeEncounterLoader.LoadForRegion(KingdomId)`
  (the generic pool is always included) via `PickRandom`, show on a city-hosted
  `NarrativeEncounterPanel` (lazily built under a `CityExploreLayer` CanvasLayer, atlas input gated
  while up). Empty pool → small gold cache (never dead-ends). `OnCityEventCompleted` applies the
  choice's gold/SetFlags/SetMetaFlags/CompletedEvents to the guild save (mirrors the campus,
  non-expedition path — HP/steps don't apply here; item/companion rewards deferred), then clears.
- **Fight / Story** → toast stub ("coming soon") + clear. Real routing (combat via EncounterRouter
  like the Convergence finale; story beats) is the next increment.
- `ClearDistrict`: sets `Cleared`, refreshes atlas markers, `SaveManager.Save()`.

## Flow
Enter capital → services auto-open → close → fogged city. Click a fogged district → it reveals with
a content glyph. Click the glyph's district again → Service reopens the menu / Event runs a narrative
choice / Fight+Story toast a placeholder. Cleared content stops showing a marker. All reveal/clear
progress persists per city this cycle; re-entering the city restores it.

## Risk / what to watch
- Static-verified only (no dotnet): braces/parens/brackets balanced across all 5 files; APIs
  confirmed against existing callers — `NarrativeEncounterLoader.LoadForRegion`/`PickRandom`,
  `NarrativeEncounterPanel.ShowEncounter`/`OnCompleted`, `ToastManager.Push(text, QuestToastKind)`,
  `GuildSaveData.SetFlag`/`Gold`/`CompletedEvents`/`Ledger.MetaNarrativeFlags`, `SaveManager.Save()`,
  `CampusGridManager.GetTileView`.
- **Marker scale is a guess** (`PixelSize 0.004`, +0.55 world Y) — the district glyphs may need
  tuning at city zoom (the place-name labels use a zoom-tracking scale; markers use a fixed size for
  now). First thing to eyeball on a screenshot.
- Verify the district-centre child `(3dq,3dr)` resolves to a real tile for every settlement shape
  (edge districts). A miss just skips that marker (safe), but a systematically-offset glyph would
  mean the centre-child assumption is off for some layouts.
- `SaveManager.Save()` fires on every reveal and clear — fine for correctness; if it's heavy, batch
  later.

## Next increments
- District **Fights**: route via `EncounterRouter.Instance` + strategic-scene combat return
  resolution (the Convergence finale pattern in `StrategicView.ConsumeConvergenceReturn`), then mark
  the district cleared + grant rewards on victory.
- **Story beats**: authored beat content (reuse the narrative panel or a lighter dialog).
- Event **rewards** (items/companions), density tuning, and possibly zoom-tracking marker scale.
