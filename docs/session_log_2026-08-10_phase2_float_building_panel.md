# Session log — Phase 2: float a building's panel over the city (retire the overlay, increment 1)

Continues from `session_log_2026-08-10_phase2_flower_lattice.md` and the handoff
`HANDOFF_phase2_campus_and_next.md` → "Immediate next step — retire the overlay".

## Goal of this increment
Finish the "build in place" goal by opening a building's menu **in place** over the live
city view instead of swapping to the full-screen `CampusScene` overlay. The world stays
rendered behind the panel; closing returns to the **city view**, not the world map.

## What shipped
- **New:** `Scripts/Systems/Strategic/HomeBuildingPanelHost.cs` — a `CanvasLayer` that hosts
  ONE `CampusPanel` as a right-docked card (width 560, matching the old Campus-tab list dock)
  over the strategic scene. Full-rect input catcher (`MouseFilter.Stop`) gates clicks so they
  don't fall through to the hex grid/camera, while the world stays visible. Builds its UI in
  `_Ready` via `CallDeferred(nameof(BuildOverlay))` (Godot 4.6 compat, README §8).
- **Edited:** `Scripts/Systems/Strategic/StrategicView.cs`
  - Added field `_floatingPanel`.
  - `OnHomeBuildingPicked`: panel destinations that `HomeBuildingPanelHost.CanFloat` accepts
    now `ShowFloatingPanel(...)`; the rest fall back to `ShowCampusOverlay(...)`. Scene
    destinations (deck/library/upgrade) unchanged.
  - Added `ShowFloatingPanel` (sets `_atlas3D.AcceptInput = false`, titles the card from
    `BuildingDatabase.GetTemplate(id).Name`, mounts the host) and `HideFloatingPanel`
    (re-enables atlas input, drops the ref; **does not** leave city view — the host frees
    itself).

## The load-bearing design decision
Panels reach the shell only through `CampusContext`, and that seam carries real cycle
lifecycle: `ShowNarrative` (with its persist-on-completion wiring), `BeginNextCycle`,
`EnterStrategicMap`, `EnsureSaveSeeded`. Reconstructing those **outside** `CampusScreen` would
be a parallel system that looks correct and silently drops persistence — exactly the failure
mode the project conventions warn against.

So this increment floats **only** the panels whose context surface is fully satisfiable without
that lifecycle:

| Floatable now (`CanFloat`) | Ctx surface used | Not yet — stays on overlay | Why |
|---|---|---|---|
| Guild, Companions, Armory, Training, Records | `Save`, `Host`, `RefreshGold`, `RequestRefreshAll`, `EnsureSaveSeeded` | Expedition | needs `BeginNextCycle` + `EnterStrategicMap` |
| | | Quests, Council | need `ShowNarrative` persist-on-completion |

For the floated panels the context wires: `Host = StrategicView` (scene changes / confirm
dialogs), `RequestRefreshAll` and `RefreshGold` → `panel.Refresh()` (a single floated panel's
"redraw me"), a live `ToastManager`, and inert fallbacks for the lifecycle verbs
(`EnterStrategicMap` → close back to city). This is why `CampusScreen` was **not** reused as a
chromeless host: it paints its own full-screen background and a second in-world 3D campus map
(`BuildCampusMap()`), so hosting it "over the city" would redraw a redundant campus and hide the
world — the opposite of the goal.

## Why NOT reuse CampusScreen (recorded so we don't relitigate)
`CampusScreen.BuildUI` builds an opaque background + title bar + tab bar + its own 3D campus
grid + dimmer + nine panels. Making it chromeless would mean gutting most of `BuildUI`. Hosting
one extracted panel directly is smaller and leaves `CampusScreen` intact as the fallback for the
three lifecycle-heavy panels.

## Verification done (no dotnet in this environment → static)
- Brace/paren balance OK on both files.
- All five floatable panel classes are `public sealed`, default (implicit) constructor.
- Symbols confirmed present: `UITheme.CampusTitleColor`, `ToastManager()` (no explicit ctor),
  `BuildingDefinition.Name`, `CampusContext` ctor param names match the named args exactly.
- **Save shape untouched**: no save-version bump, no new save fields, no save files modified.
- Godot .NET SDK auto-globs `.cs`, so the new file needs no `.csproj` edit.
- **NOT compiled or run** — build in the Godot editor before trusting.

## Test steps
1. Open the game, load a save, enter the world, wheel-zoom into the home city (city view).
2. Click the **Grand Hall** (`grand_hall`, hosts `guild`).
   - Expect: the **Guild** panel floats as a right-docked card; the city is still visible and
     rendered behind it; camera doesn't pan while it's open.
   - Click **✕ Close** → panel gone, still in **city view** (not the world map).
3. Sanity: click a building that routes to Expedition/Quests/Council → still opens the full
   overlay (fallback intact). Click the deck/library/upgrade doors → still change scene.

## Known limitations (intentional, this increment)
- Slot-switching from a floated Guild panel calls the no-op `EnsureSaveSeeded`/`RefreshGold`
  fallbacks. Slots are chrome slated to leave the Guild panel in Phase 3 (see
  `CampusGuildPanel` class doc); acceptable for in-world use now, noted so it isn't a surprise.
- Card layout (docked strip, header, close) is untuned — adjust after the first screenshot.

## Follow-up fixes (same session, after first screenshot)
The float works (Grand Hall → Guild panel over the live city). Two bugs surfaced and were fixed:

### 1. City-view click landed ~1 tile off
`CampusGridManager.TryPickRay` intersected the ray with the **tile-top** plane
(`planeY = GlobalPosition.Y`), but the player aims at the billboarded **name label**, which
floats `UITheme.Label3DPoiHeight = 1.55` above the tile (`HexTile.SetPoiLabel`). Numerically:
1.55 × 1/3 grounds scale ÷ tan(city pitch ≈ 40°) ≈ 0.62 world, vs. flower-tile spacing
√3 × 1/3 ≈ 0.58 world → ~1 tile of error, matching the report. **Fix:** intersect the ray at
the LABEL plane (`planeY += Label3DPoiHeight × GlobalTransform.Basis.Y.Length()`), which recovers
the labelled tile's centre EXACTLY at any camera pitch (pitch only set the error magnitude, not
the fix). Scale-proof via `GlobalTransform`. File: `Scripts/Systems/Campus/CampusGridManager.cs`.

### 2. `teleport_sigil` off-grid → error + skipped
Its `startsBuiltAt (-3,3)` = `DistrictCentre(-1,1)`, a **locked** (non-founding) district, and its
footprint is a whole 7-hex flower — it can't fit in the 3-district founding city (each founding
flower already holds a single-tile starter). It's a placeholder whole-district portal, so it
shouldn't be a founding building. **Fix (two parts):**
- Removed `startsBuiltAt` from `Data/Buildings/teleport_sigil.json` (not auto-placed on new saves;
  built later once district growth frees a district). Not foundational, so no anchor warning.
- `CampusGridManager.LoadFromSave` now **unplaces** a stranded building (`IsPlaced = false`) instead
  of `PrintErr`+skip on every load, so existing saves self-heal (fires at most once — the IsPlaced
  guard skips it next load). Matches the lattice migration's off-grid-unplacement philosophy.

No save-version bump; no new save fields (both are additive/lazy). Static-verified only — build in
Godot. Test: enter city → click a building's label → correct panel; the teleport_sigil error should
be gone (replaced by a one-time `[CampusGrid] ... unplaced` info line on the current save).

## District growth — part 1 (preview) + contour-follow pivot
Chosen interaction: click a locked preview flower to annex; placeholder gold cost. Then a design
pivot surfaced from the first preview screenshot.

### A1: locked-district preview now renders
`BuildSurroundingPreview` was never called. Wired it into `BuildHomeGrounds` (2 rings), toggled
with city view via new `CampusGridManager.SetSurroundingPreviewVisible`. Added a `HomeDistrictPicked`
event on WorldAtlas3D (consumed in A2, still to come). Files: `WorldAtlas3D.cs`, `CampusGridManager.cs`.

### Contour-follow (replaces the city plateau) — user picked "full contour-follow"
The preview flowers floated because the whole city was a flat plateau (`_cityPlateau = maxH+0.35`)
lifting every city tile to one height, with preview flowers pinned there over lower neighbor terrain.
User wants the city/campus to follow the land's contour. **Reversed the plateau decision:**
- `TileHeightAt` now returns each tile's natural `TileHeight` (no city override). `_cityPlateau`
  repurposed as the camera-framing height (home tile's terrain top). Removed the now-dead `AxialDirs`.
- Campus grid follows per-district: new `CampusGridManager.ChildTopWorldY` provider + `ApplyChildContour`
  push each child tile to its DISTRICT's strategic-tile terrain height (world→local via the grid's
  global transform, so it's correct at the 1/3 scale). `WorldAtlas3D.ChildDistrictTopWorldY` supplies
  it (child → nearest district ÷3 → that strategic tile's `TileHeightAt`). Applied to BOTH real tiles
  (`LoadFromSave`) and preview flowers (`BuildSurroundingPreview`) — the latter fixes the floating.
- `BuildHomeGrounds` reordered: transform + provider set BEFORE `LoadFromSave`, so the height
  conversion reads a valid global transform. City borders now per-tile height (`BuildCityBorders()`).
- **Pick fix for varying heights:** a single shared pick plane only worked when all tiles were on
  the plateau. Rewrote `TryPickRay` to pick per-tile in WORLD space — intersect each tile's own
  (label-lifted) top plane via `TileView.GlobalPosition`, keep the nearest-to-its-own-centre within a
  hex circumradius. Keeps the label-parallax fix and is now contour- and scale-proof.

Tradeoffs accepted (per the plateau's original rationale): buildings can sit on sloped/stepped
districts, and a taller neighbor tile can now occlude the city. No save changes.

### Test
Enter the city: it should sit INTO the terrain (following contour, stepping between districts) rather
than on a floating mesa, and the locked preview flowers should nestle on their own tiles (no float).
Click buildings across districts of different heights — the panel should still open under the cursor
(per-tile pick). Static-verified only (no dotnet): braces balanced, symbols/ctors confirmed.

## Post-contour fixes + district growth (A2)

### Crash: opening Armory/Training floated panel → stack overflow
`CampusArmoryPanel.Refresh()` and `CampusTrainingPanel.Refresh()` call `Ctx.RefreshGold()`, and the
floating host had wired `refreshGold` → `_panel.Refresh()`, so `Refresh → RefreshGold → Refresh …`
recursed to a crash. Grand Hall (Guild) doesn't call RefreshGold in Refresh, which is why it worked.
**Fix:** `refreshGold` is a no-op in `HomeBuildingPanelHost` (the float has no gold readout;
gold-dependent widgets repaint via `requestRefreshAll`, which panels call from button handlers).

### District growth complete (click a preview flower to annex)
Bug 2 ("preview only visible when about to buy") + A2 done together via an **annex mode**:
- `WorldAtlas3D.SetAnnexMode(on)` builds the annexable preview to the CURRENT frontier
  (`FrontierDistricts` = locked districts adjacent to an unlocked one) and shows it; off hides it.
  The preview is no longer built in `BuildHomeGrounds` or shown on city entry — only in annex mode.
- StrategicView adds an "＋ Annex a district" toggle button (city view only, resets on exit). While
  pressed, `TryPickHomeGrounds` routes clicks to `CampusGridManager.TryPickPreviewDistrict` (same
  world-space per-tile analytic pick as `TryPickRay`, but over preview flowers and with NO label lift
  — they carry no label) → `HomeDistrictPicked(district)`.
- `StrategicView.OnHomeDistrictPicked`: a `ConfirmationDialog` (OK disabled when unaffordable) →
  spend `DistrictAnnexCost = 250` gold (placeholder) → `CampusMapSaveData.UnlockDistrict` →
  `SaveManager.Save()` → `WorldAtlas3D.RefreshCityGrowth()` (recompute footprint + rebuild world +
  grounds, snap back into city without a swoop). Save stays additive (UnlockDistrict rebuilds
  `map.Tiles`); no version bump.
- Preview flowers are now tracked in `_previewTiles`/`_previewDistrictOf` (not in `Tiles`, so not
  buildable) so a click resolves to its district. Removed the old rings-based `BuildSurroundingPreview`
  + `DistrictsWithin`.

Note: the always-on contoured preview flowers were likely what read as "buildings not confined to the
district" — gating them behind annex mode should also declutter that.

### Test
1. Enter city → **no** preview flowers by default (decluttered). Click buildings across districts of
   different heights → correct panel opens under the cursor; **Armory/Training no longer crash**.
2. Click "＋ Annex a district" → dim annexable flowers appear on the frontier (nestled on their tiles).
   Click one → confirm dialog (OK greyed if <250 gold) → buy → city rebuilds with the new district,
   still in city view, gold reduced, annex toggle reset.
3. Leave to world map → annex toggle + preview gone.

Static-verified only (no dotnet): braces balanced across all 4 files; `Toggled`/`Canceled`/`GetOkButton`
match existing codebase usage; annex flow wired end-to-end.

## Next
- Screenshot review → tune the card (width/position/scroll) and confirm close-returns-to-city; tune
  the annex cost; consider a "reveal beat" when a vertex's third district completes its bonus corner.
- Then Phase 3 (large NPC cities as districted regions, reusing this machinery).
- Then district growth (`UnlockDistrict` spend → annex a tile), then Phase 3 (large NPC cities
  reuse this machinery — and the generalization of `CampusContext` hosting is where the
  Expedition/Quests/Council panels become floatable too).
