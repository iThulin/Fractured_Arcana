# Session log — 2026-08-21 — Phase 3 buildout: district fights, towns, contracts board, story beats

Bound to the mac working copy, `/Users/ianthulin/Development/Fractured_Arcana`. **NOT
COMMITTED — compile + test first.** Follows `session_log_2026-08-13_*` (K3/Q4/Q5) and
closes the four Phase 3 gaps ruled this session: fights, towns & non-seat cities, the
Quests service, and story beats. Siege (P3.3) untouched — brother's system.

## 1. District FIGHTS are real (was a "coming soon" toast)

- **Launch** (`StrategicView.LaunchDistrictFight`): composes from the owning kingdom's
  region pool via `EncounterPoolLoader.Pick` — seat = Battle tier, ordinary settlement
  = Skirmish — at expedition-parity difficulty (region `EnemyDifficultyMult` ×
  kingdom-tier factor 1.0/1.25/1.5 × `CampaignEscalation.CombatDifficultyMult`).
  Terrain = the city centre's world tile. Round-trips via the proven strategic
  pattern (OpenAnchorhold litany): reset router flags → `ReturnSceneOverride =
  StrategicScenePath` → carrier → combat scene. Unresolvable roster → the enclave
  "scatters" (small gold, cleared) — never a dead click.
- **Pending record** (`CycleState.PendingCityFightCityId/Dq/Dr`, additive, no version
  bump): which district is being fought, saved before the swap. A stale record with
  no router return (mid-combat reload) is dropped; the district stays live.
- **Return** (`StrategicView.ConsumeDistrictFightReturn`, called in `_Ready` beside
  `ConsumeConvergenceReturn`): victory banks `GoldReward` + `ArcaneSplinters`, rolls
  the **Q4.4 loot faucet** (`CombatLootTable.Roll` at the kingdom's tier — no blight,
  city ground isn't corrupted), marks the district **Cleared**. Defeat: district
  stays, toast says so. Either way the player **lands back inside the fought city**:
  `_reenterNpcCity` is consumed by `BuildAtlas3D` (checked before the
  `StartInCityOnOpen` hub landing), with the services auto-open suppressed for that
  one entry (`_suppressServicesOnce`) so you land on the city, not a menu.

## 2. Towns & non-seat cities descend (was seats only)

- **Gate** (`WorldAtlas3D.HandlePick`): any discovered NPC settlement now enters city
  view. Seats descend from every footprint tile (unchanged); for a **non-seat**
  settlement the staging-beacon tile keeps its deploy action and the other footprint
  tiles descend. `EnterCityView` already handled arbitrary footprints — towns render
  their (small — junction towns are 2 tiles) /3 flower districts with fog + content.
- **Click-conflict fix** (`StrategicView.OnAtlas3DTilePicked`): early-return when the
  atlas is in city mode. Previously the descend click ALSO ran the world verbs — most
  visibly the staging snap (tolerance 3), which would pop a deploy window over the
  freshly entered city. This also covers the latent home-campus variant of the bug.
- **Service tiering**: towns are **market-only** — no hiring hall (city institutions,
  K3), no contracts board — with a town subtitle; markets stock **2** slots
  (`CityMarketService.TownStock`, additive const; city 3 / seat 4 unchanged).
  Explore content (fog/scout/markers) works in towns as in cities.

## 3. Quests service = the CONTRACTS BOARD (was a disabled placeholder)

- **Model** (`CityContractState.cs`, new; `CycleState.CityContractBoards`, additive):
  per-city boards, same CityId + lazy per-lunation refresh convention as markets.
  Offers: seat 3 / city 2. Deterministic roll (FNV ^ lunation). Unaccepted offers
  reroll each lunation; accepted contracts persist until turned in.
- **Kinds** (`CityContractService.cs`, new), all scoped to the posting kingdom and
  fed by the city-explore verbs — no new bookkeeping systems:
  - **scout** — reveal 3–5 districts anywhere in the kingdom (hook: new
    `WorldAtlas3D.DistrictScouted` event at the reveal point);
  - **purge** — break 1–2 hostile enclaves (hook: the fight return + scatter path);
  - **aid** — resolve 1–2 district events (hook: `OnCityEventCompleted`, which now
    takes the city).
  Gold on turn-in = per-unit (25/70/45) × target × kingdom-tier pct (100/140/180).
- **Turn-in**: gold now + a **Steward-routed echo** — new `CouncilEcho.ContractHonored`
  deed tag ("a posted contract honored to the letter"), minor positive, wired into
  the routing + story-line switches beside K3's `HireGiven`.
- **UI** (`CityServicesHost.BuildQuestsSection`): posting rows with
  Accept / progress (x/y, disabled) / Turn in (Ng) buttons, mirroring the Market
  section's build/populate pattern; completions toast via the city explore toasts
  ("Contract fulfilled … collect at the board").

## 4. STORY beats are real (was a "coming soon" toast)

- **Pool** (`Data/Encounters/city_stories.json`, new): 10 authored city vignettes in
  the standard `NarrativeEncounterData` shape — all id'd (one-shot per save via
  `CompletedEvents`), including one flag-gated chain (`cs_bellfounders_apprentice` →
  `cs_bell_heard_again`) proving cross-city continuity, and two entries that write
  permanent `setMetaFlags` (ledger material for future ripples).
  **⚠ VOICE: every body/result is a DRAFT for your rewrite pass** (content plan §8.1
  is unresolved) — each body carries a trailing `DRAFT: rewrite in author voice`
  marker so unrewritten ones are greppable and visible in playtest.
- **Loader**: `NarrativeEncounterLoader.LoadCityStories()` — separate from
  `LoadForRegion` so road expeditions never draw a city-staged beat.
- **Routing** (`StrategicView.TriggerCityStory`): same city-hosted narrative panel as
  Events; outcomes through `ApplyNarrativeOutcome`; clears the district; exhausted
  pool → a closing line, never a dead click. Story beats do NOT credit "aid"
  contracts (deliberate — aid is the events' verb).

## Static verification (no dotnet in this environment — Godot build owed)

- Brace/paren/bracket balance: all 10 touched/new files match their pre-edit deltas
  exactly (the two off-by-ones in StrategicView/WorldAtlas3D/Loader are pre-existing
  artifacts of the crude comment/string stripper, unchanged old→new).
- `city_stories.json` parses; field names verified against `NarrativeEncounterData`
  (camelCase policy, `IncludeFields`) — uses only existing fields.
- API presence greps confirmed: `SnapToTileClose`, `EnterCityView`,
  `RegionLoader.LoadOrDefault`, `CampaignEscalation.CombatDifficultyMult`,
  `EncounterPoolLoader.Pick`, `EncounterContextCarrier.Set/SetContext`,
  `CombatLootTable.Roll`, `ItemInstance.FromDefinition`, `Armory.AddItem`,
  `ArcaneSplinters`, `CityExploreService.Get/FindDistrict/CityId`,
  `CityServicesHost.Close`, `PopulateRecruits`, `UITheme.*`, `OfficeSteward`,
  `CompanionRoster.GetActiveParty`, `EnsureCityNarrativePanel`.

## First-launch checklist

1. Godot build; fix whatever the compiler finds first.
2. **Fight**: enter a capital → scout until a ⚔ district appears → click it → real
   combat launches. Win → card reward (non-Adept) → land back INSIDE the same city,
   no services menu, toast with gold/splinters/items, ⚔ marker gone. Save persists.
3. **Fight loss**: lose one → land back in the city, "Driven back" toast, ⚔ still
   there, retry works.
4. **Mid-combat quit**: launch a fight, quit to desktop from combat, relaunch →
   strategic map normal, district still live (stale pending dropped).
5. **Town**: click a discovered town (not its beacon if it has one) → tiny districted
   town, fog works, services = Market only (2 slots), subtitle "waystop town".
   Non-seat city: beacon tile still deploys; other tiles descend; menu shows
   Market(3) + Recruit + Contracts Board.
6. **No deploy-popup regression**: descending into any settlement must NOT open a
   deploy window on top (the city-mode guard); confirm normal deploys from beacons
   still work on the world map.
7. **Contracts**: board shows 2–3 postings with pay; Accept → scout/fight/event in
   that kingdom ticks progress (reopen menu to see x/y); completion toasts; Turn in
   pays gold and prints the echo line; Herald's Report next lunation shows "a posted
   contract honored to the letter" (+Steward regard). Lunation turn rerolls only
   unaccepted postings.
8. **Story**: a ✦ district opens a vignette on the narrative panel; choices apply
   gold/flags; district clears; the same vignette never repeats this save. After
   `cs_bell_rung` is set, `cs_bell_heard_again` can appear in a later ✦ district.
9. Save/load through all of the above — boards, contract progress, cleared districts
   identical after reload.

## Addendum — playtest fix: unreachable bootstrap outposts

**Report:** consistently cannot reach an outpost within the step budget.
**Diagnosis (verified in code, not vibes):** `OperatingRange = 40` is a hard
one-way tank — Rest sites restore no steps, every `bonusStartingSteps` in
`Data/Buildings/` is 0, nothing else refunds. But `SeedBootstrapOutpost` placed
the guaranteed outposts by **crow-flies hex distance** (Frontier 10–12, Distant
13–18) with no terrain/path check, while the budget is spent in COST units
(forest/hills 2, swamp 3, mountain 4, unbridged ford +2). On rough maps the
near outpost alone costs 30–45 steps under perfect play — before fog, before
any of the 6–10 POIs a run targets. `RegionDefinition.StepBudget = 22` is dead
code — evidence the constants drifted apart unnoticed. The deploy dialog's
"~24 tiles across" claim is only true on roads/grass.

**Fix (WorldGenerator.cs):**
- `StepCostMap` (new): Dijkstra from the start over the REAL movement-cost
  arithmetic (TerrainStep + RoadDiscount/FordPenalty shared constants; spell
  adjustments deliberately excluded — no expedition exists at worldgen).
- `SeedBootstrapOutpost` now takes **walked-cost bands**, not rings:
  Frontier 18–24 (≈ half budget one-way), Distant 28–34 (one-way at 70–85% of
  budget; arrival grants staging = free extract, so one-way IS the contract),
  Waystation 14–20. Empty band (or no foreign ground for the anti-softlock
  Distant) widens once by +10 before warning.
- **Road bias:** preference order foreign-road > foreign > road > any —
  outposts sit on roads, and a road approach floors near 1/step.
- Seed log line now prints the actual walked cost + road adjacency.

**⚠ Applies to NEW worldgen only** — the current save's outposts are already
persisted. To feel the fix: new save, or next cycle reseed (unmake), or debug
regenerate the world.

**Test:** new world → console shows each bootstrap outpost's walked cost inside
its band → deploy from Home Camp and walk to the Frontier Outpost reasonably
directly → arrive with ≥ ~14 steps remaining; Distant reachable one-way with
≥ ~5 remaining. Rough-region founding scenarios (Mire/Crags) especially.

**Watch:** on cheap terrain (long roads, grassland) the cost bands land
farther in HEXES than the old rings — reachability is what matters, but if the
Frontier feels visually far on easy maps, consider a hex-distance ceiling as a
second filter. Tuning values are starting values, per pillar.

## Addendum 2 — expedition-view readability pass (ExpeditionWindow3D.cs)

Playtest feedback: too dark overall; move hints are hex fills on a map that no
longer shows hexes and their colours blend into terrain; the fog dropoff to the
canvas slab reads as an artifact. Three changes:

1. **Brightness — chamber knobs only.** `ChamberAmbientEnergy` 0.40→0.62,
   `ChamberFogDensity` 0.05→0.032 (the dark distance fog was eating the
   projection), `GlowEnergy` 3.2→3.6. The base sun/ambient rig is untouched —
   it is A4b palette-parity with the strategic atlas and must stay identical.
   All three are Inspector exports (scene stores no overrides, code defaults
   rule); tune live in the editor if still dim.
2. **Move hints: rings, not hex fills.** Each adjacent walkable tile now gets a
   crisp UNSHADED torus ring (cost-coloured, full alpha — immune to scene
   lighting) over a near-black underlay disc that guarantees contrast on any
   terrain hue, plus the cost label with a heavier dark outline. Circles
   deliberately — the smooth heightmap has no cells to echo. The 30%-alpha
   `UITheme.MoveHighlight*` constants are unchanged (other views use them);
   the 3D path overrides alpha locally.
3. **Swirling mist over undiscovered ground — rev 2, volumetric.** Playtest on
   rev 1 ("flat sheet, terrain colour bleeds through") → replaced the single
   translucent plane with a three-layer stack that fakes a volume on any
   renderer: a subdivided DECK plane vertex-displaced by curl-bent scrolling
   noise (real lumpy geometry, finite-difference normals, manual lambert — the
   crest/hollow shading is what makes it read as a body), running NEAR-OPAQUE
   (α≈0.96) over fully hidden ground so the canvas colour underneath is gone;
   plus two thresholded-puff wisp sheets above at different scales/speeds/
   directions whose parallax against the deck sells the depth. Alpha still
   rides the 112² blurred hidden-mask (world-space lookup, rebaked every
   rebuild); silhouette-ring tiles stay unmisted; tint/density restyle with
   the B-cycled surround. Deck subdiv 100² ≈ 10k verts, three draw calls.
   Rev 3 (playtest: mist rectangles hung past the table): all three layers
   clip to the SCRYING DISC — same centre/radius the land mesh inscribes
   (`_mapCenterX/Z`, `_mapDiscR`), alpha fading over a 6u rim band and the
   deck's displacement flattening with it so no lump pokes past the disc
   silhouette. The projection is a round island; the mist now is too.
   Rev 4 (playtest: "bubbling liquid, not swirling cloud" + "contain it in
   the frame"): (a) motion reworked from churn to ADVECTION — the old
   counter-scrolling octaves had zero net drift (in-place boil); now every
   octave in both shaders drifts one shared `wind` heading at different
   rates with a slowly evolving curl field, wispB ~20° off-heading for
   shear, no negative speeds; deck features broadened (p×0.13 → ×0.09) so
   lumps read as banks, not bubbles. (b) The scrying rig grew a LENS FRAME:
   a dark barrel wall (cap-less cylinder, cull-disabled) from table top to
   `rimY = deck base + amp + 0.35`, with the glowing annulus moved up onto
   its lip — the mist now sits visibly inside the vessel. Watch: barrel
   occluding the map edge at grazing angles is intended; `rimY` margin and
   `wind` are the tuning knobs.
   Rev 5 (playtest: jagged fog seam at the rim): the land mesh clips to the
   disc at WHOLE-QUAD granularity — a staircase edge that silhouetted
   against the dark barrel once the mist faded out before reaching it. Fix:
   split the deck's rim into two bands — displacement still flattens over
   the inner `rim_fade` band (nothing crests the lip), but ALPHA now stays
   near-full to `rim_reach` (0.5u) PAST the disc edge, just inside the
   barrel's inner wall, on all three layers. The fog laps the vessel like
   liquid in a basin and the staircase is buried under opaque mist.
   Rev 6 (playtest: rim band still read as bubbling with a clipped edge):
   rev 5's displacement-flatten band was the culprit — 6u of FLAT deck
   where only the colour animated (in-place shimmer = the boil look), with
   a visible boundary against the rolling interior. Removed: the lip
   already sits at deck base + full amp + 0.35, so full-height cloud now
   rolls all the way to the barrel wall and still clears the frame. Alpha
   handling (rim_reach past the disc edge) unchanged.
   Rev 7 (playtest, with screenshot: blue/brown jagged border half-veiled by
   fog): the staircase is the LAND MESH's whole-quad disc clip, and its
   teeth protrude up to ~a quad PAST discR — beyond where mist alpha
   reaches, so no amount of fog opacity could bury it. Fixed at the source
   in BuildHeightmapSurface: the outermost 2u band of vertices SINKS
   (smoothstep) from terrain height to FogSlabHeight − 0.6 = exactly
   TableTopY, so the mesh edge dives under the mist deck and meets the
   barrel base with no gap. The band is always hidden-margin canvas — no
   real terrain is lost. The rim's colour fade to _surroundEdge unchanged.
   Rev 8 (playtest, arrowed screenshot: pale sawtooth in the MIST edge
   itself): the deck computed rim + mask alpha PER-VERTEX (v_rim/v_mask),
   so the circular fade was linearly interpolated across the 100² grid's
   triangles — a quantized sawtooth. (The wisp sheets never showed it —
   they were per-fragment from day one, which is why "the upper layer
   worked.") Deck rim + mask lookups moved to the fragment stage via a
   v_world varying; the edge is now a true per-pixel circle. Displacement
   stays per-vertex (geometry), unaffected.
   Rev 9 (playtest: STILL there): the persistent teeth were the land mesh's
   whole-quad clip OUTLINE — geometry, so no alpha/sink cover-up could kill
   it; edge-on it kept silhouetting through the translucent rim band. Final
   fix: the disc clip is now a CLAMP — vertices beyond the disc are pulled
   radially onto the circle instead of their quads being dropped. The mesh
   boundary IS the circle (outer quads compress to slivers along the arc;
   zero-area ones render nothing; the `outside[]` cull is now dead-false).
   Combined with the rev-7 sink, the boundary is a smooth circle at table
   height under opaque mist. There is no sawtooth left to hide.

**Test:** F6 the ExpeditionWindow3D scene standalone or deploy: map visibly
brighter in the default chamber; move rings + numbers legible over savanna,
snow, and swamp alike; mist animates over undiscovered ground, thins at the
frontier, never covers painted terrain; B-cycling surrounds restyles the mist;
walking reveals ground out from under the mist. Watch: mist plane vs. tall
mountain silhouettes at grazing angles (depth-tested, should occlude
correctly), and NoiseTexture2D generates async — first frame may show thin
mist for an instant.

## Addendum 3 — deploy-flow streamline

The old chain: Gatehouse → Expedition tab (full overlay) → "Open Strategic
Map" (animating open the map you were already on) → find + click a beacon →
side drawer → Deploy. Five interactions, one of them a no-op screen. Now:

- **Gatehouse click = the deploy order** (`StrategicView.BeginDeployFlow`):
  fly out of the city (`LeaveCityMode`), open the launch drawer directly on
  the **last-used staging point** (`CycleState.LastDeployStagingKey`,
  additive; recorded in `Deploy()`), falling back Home Camp → first
  available. Lifecycle moments the Expedition tab still owns (post-
  Conjunction school pick + regalia carry, unwoven world, no staging) fall
  back to the old overlay unchanged.
- **The map stays live under the drawer.** The full-rect Stop guard is gone;
  `OnAtlas3DTilePicked` filters to staging-retarget-only while `_deployUi`
  is up (click another beacon → drawer rebuilds there; caches/warfronts
  can't stack dialogs). New `WorldAtlas3D.SuppressCityEntry` stops
  settlement clicks descending into city view underneath the drawer (set on
  drawer open, cleared on close; both home and NPC entry points guarded).
- **Consumable loadout on the launch screen** ("Provisions" section, after
  Grimoire prep): one checkbox per owned consumable kind, default checked.
  Unchecked kinds go to `CycleState.ExcludedConsumableIds` (additive,
  persists this cycle) and are filtered from the combat consumable popup in
  `CombatManager` — opt-out, so the default sortie is byte-identical to
  before the UI existed.
- **Scriptorium rehomed to the Armory panel** (scrolls are items; the
  Armory floats and is on the normal path). The Expedition tab keeps its
  copy for fallback appearances — flagged duplication, unify when the
  Scribe's Tower claims scroll crafting (R8).

Click count: was 4–5 (with a dead screen), now Gatehouse → Deploy = **2**,
plus optional retarget/provisions in between.

**Test:** (1) Gatehouse from city view → camera flies out, drawer opens on
Home Camp (fresh cycle) with Provisions listed; Deploy launches. (2) Second
sortie: drawer opens on the beacon you last launched from. (3) With drawer
open: pan/zoom works, clicking another beacon retargets, clicking a city
does NOT descend, cache/warfront clicks do nothing. (4) Uncheck a draught →
in combat the consumable popup lacks it; recheck next deploy → it's back.
(5) Post-Conjunction Gatehouse click → the school picker overlay as before.
(6) Armory panel shows the Scriptorium and scribing works from the city
float.

## Addendum 4 — title screen

Cold boot used to land directly in the city hub (auto-loading the last save),
which read as the app dumping you mid-game. New `TitleScreen`
(`Scenes/UI/TitleScreen.tscn` + `Scripts/UI/TitleScreen.cs`, now the
`main_scene`): name + subtitle over `UITheme.WorldDeep`, and the boot's three
verbs — **Continue** (only when `SaveManager.AnySaveExists()`, new scan-only
query; routes through the exact old cold-boot path: `AutoLoadLast` →
`StartInCityOnOpen` → StrategicScene), **Guild Hall / Found a Guild** (the
campus slot-picker/founding room), **Settings** (SettingsMenu instanced as an
overlay, `ReturnScenePath=""` → its Back QueueFrees), **Quit**. Keyboard
focus grabs the primary action so Enter resumes. Appears exactly once — no
in-game flow routes back to it; the hub stays the game's home. PauseMenu's
"main menu" still points at the hub (deliberate; retarget to the title later
if wanted).

Renderer note (corrected): the project RUNS **Forward+** — no
`rendering/renderer/rendering_method` override exists, and the desktop
default is forward_plus. The `config/features` "GL Compatibility" tag was
stale creation-time metadata (project-manager display only), now updated to
"Forward Plus". The mist stack is renderer-independent either way.

**Test:** launch → title; Enter → city hub with last save. Fresh user://
(no saves) → no Continue, "Found a Guild" primary → founding room. Settings
opens/closes over the title. Quit quits.

## Next / deferred

- Fight difficulty numbers (tier mapping, purse) are STARTING VALUES — tune in
  playtest, per pillar.
- Story voice rewrite pass (grep `DRAFT: rewrite in author voice`).
- Contracts: no quest-log surfacing yet (board-local by design this increment);
  consider a small "active contracts" line in the quest log later.
- Town/city explore density + marker scale tuning at the smaller footprints.
- P3.3 Siege: still the brother's track — the city battlemap compiler and gate
  defense logs are untouched by this session.
