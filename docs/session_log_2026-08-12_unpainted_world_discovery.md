# Session log — 2026-08-12 — Unpainted-world discovery (art pass A6)

Strategic/expedition painterly art pass, first implemented item. Ruling this session:
**painterly OVERRIDES the "crafted clockwork model" north star** (2026-08-08); this
item is A6 from `art_pass_plan_strategic_expedition_v1.md` (delivered same day,
Cowork outputs folder — copy into docs/ if adopting the plan). **UNCOMMITTED.**
Verification was STATIC only (no .NET SDK in the sandbox: brace/paren balance +
symbol greps + coordinate-space audit) — **compile in the editor before anything else.**

## The concept
Undiscovered ground renders as **raw canvas** instead of a dark void — exploration
literally paints the world in. Three states:

- **Unseen / Hidden** → flat parchment slab (`UITheme.CanvasUnseen`), deterministic
  paper-grain wobble (coordinate hash ONLY — zero world data feeds it), pencil-line
  hex under-drawing from the grout taper, matte (roughness 1.0). Height stays the
  flat slab — the no-silhouette-leak law is untouched.
- **Charted / Silhouette** → **underpainting**: `Hex3DPalette.Underpaint(baseCol)` —
  heavily desaturated pale wash (sat ×0.30, value lifted toward 0.66, 25% lerp to
  canvas), terrain hue faintly readable. Replaces the old dim-toward-dark lerp
  (`StrategicCharted`, 0.55). Charted KEEPS its height, as before.
- **Explored / Revealed** → unchanged (the painted world).

**Torn wet-edge:** canvas tiles bordering any discovered tile darken toward
`UITheme.CanvasWetEdge` by a per-tile noise amount (0.12–0.38,
`Hex3DPalette.WetEdgeAmount`) — watercolor edge-darkening, so the frontier reads as
a torn wash boundary, not a ruled line. Carries no new information (adjacency to
explored ground is already visible).

## Files touched
- **`Scripts/UI/UITheme.cs`** — `CanvasUnseen` (0.72, 0.66, 0.545 parchment),
  `CanvasWetEdge` (0.55, 0.47, 0.36 sepia). `StrategicUnseen`/`StrategicCharted`
  untouched and re-commented as 2D-fallback-only (the 2D StrategicView keeps its void).
- **`Scripts/Systems/Overworld/Hex3DPalette.cs`** — shared `Underpaint(Color)`,
  `CanvasTone(col, row, wetEdge01)`, `WetEdgeAmount(col, row)`. Shared deliberately:
  the fog colors were view-local copies before; the discovery language now cannot
  drift between the two 3D renderers (plan ruling #4, approved by adoption).
- **`Scripts/Systems/Strategic/WorldAtlas3D.cs`**
  - **Third tile layer**: `_canvasLayer` MultiMesh takes ALL Unseen tiles — land AND
    water — with one mesh (grout taper 0.96, matte). This also fixes a LATENT LEAK:
    unseen land (tapered mesh, grout lines) vs unseen water (untapered, seamless)
    previously differed, so the coastline silhouette was readable in the fog grout —
    invisible in the dark void, would have been visible on parchment.
  - `_isWaterInstance bool[]` → `_tileLayer byte[]` (LayerLand/LayerWater/LayerCanvas)
    + `LayerMultimesh(i)` helper; RebuildTiles/RecolorTiles/ApplyWindowTint updated.
    Membership now depends on discovery — safe because discovery only changes via
    SetWorld/SetRevealAll, both full rebuilds (RecolorTiles is lens-switch only).
  - `TileColor`: Unseen → `CanvasTone` (+wet edge via `HasPaintedNeighbor`, offset→
    axial→offset walk); Charted → `Underpaint(LensBaseColor)` + existing 0.02 jitter.
  - Tooltip: "Unseen —" → "Unpainted — no expedition has come this far."
- **`Scripts/Systems/Overworld/Expedition/ExpeditionWindow3D.cs`**
  - Third `MakeTileLayer("WinCanvas", …, taper 0.96, roughness 1.0)` for Hidden tiles
    (previously Hidden went into the LAND layer, colored void).
  - `HasPaintedNeighbor(Vector2I)`: **window coords are WORLD OFFSET (col,row)** —
    the first draft stepped axial directions directly on offset coords, which is
    wrong (parity); fixed to the OffsetToAxial → step → AxialToOffset round-trip the
    file already uses (patterns at :591, :777). Absent fog coords read Hidden per the
    model contract, so the window boundary stays clean canvas.
  - `TileColor`: Hidden → `CanvasTone` (safety net; RebuildTiles routes it away),
    Silhouette → `Underpaint(baseCol)`.

## Verified (static)
Brace/paren balance OK ×4; zero remaining `_isWaterInstance` / renderer references to
`StrategicUnseen`; `HexCoord.OffsetToAxial`/`AxialToOffset`/`AxialDirections`
signatures confirmed against the live HexCoord; `TileDiscovery`, `Fog` alias, switch
patterns on `const byte` all consistent with in-file usage. **V0 resolved:**
`project.godot` has no `rendering_method` override → Forward+ default, MSAA 3D 4×.

## Test protocol (user, in editor)
1. Build. Errors panel first.
2. Strategic map with debug reveal OFF (`SetRevealAll(false)` path — `_revealAll`
   defaults true in the atlas; StrategicView drives it from `_debugReveal`): unseen
   world = parchment with faint hex grid; frontier shows noisy sepia edge; charted
   ring = pale wash with faint terrain hue; explored = unchanged painted world.
3. All four lenses on a part-discovered world — canvas must stay identical across
   lenses (it does not read lens data); Charted washes take the lens's base hue.
4. Expedition run: Hidden disc = canvas, Silhouette ring = wash, vision reveal
   paints tiles in as you walk. Click-to-move into fog still works (PickTile heights
   untouched).
5. Deploy preview over a partially-unseen ring: lifted parchment should read fine.
6. Known aesthetic caveat: under the CURRENT amber raking sun the parchment will
   render warm sepia; the A4 lighting retune (plan Tier 1) is where canvas gets its
   final daylight tone. Judge geometry/states now, exact color after A4.

## Post-build fix (same day, after user screenshots)
First build CONFIRMED WORKING in play (user screenshots ×8: canvas world at cycle
start, expedition dome, wet-edge smudge at the frontier, all lenses). One artifact:
diagonal slash marks on canvas in the deploy view — shadow ACNE from thousands of
coplanar bright slabs self-shadowing under the raking sun (invisible on the old dark
void). Fix: `CastShadow = Off` on both canvas layers — canvas is the lowest geometry,
nothing below it receives; it still RECEIVES the painted world's cast shadows (the
"object resting on paper" cue stays). Fallback if any residue: sun
`ShadowNormalBias = 3f` (precedent 2026-08-08). Known, deferred to A4: the grout
reads embossed gold rather than penciled graphite under the amber lamp — lighting
item, not a material bug.

## Same session, continued — A1 palette rebuild + A4 lamp strip
User confirmed reveal-all was on in the lens screenshots (no discovery bug) and ruled:
proceed to A1 + A4. **UNCOMMITTED**, static verification only, same discipline.

**A1 — palette.** `Hex3DPalette.TerrainColor` now carries AUTHORED painterly
swatches (final lit-scene colours, muted wide-range, combat register) instead of
mapping to `UITheme.Terrain*` (which stays tuned for the 2D fallback map — authored
in Hex3DPalette on the SchoolColors/ElementColors dedicated-palette precedent).
The compensation stack is DELETED: `Grade()` removed from Hex3DPalette and both
callers, plus the window's extra ×1.35 saturation boost — lighting owns brightness,
the swatches own richness, full stop. New `JitterAmp(WorldTile)` gives per-terrain
jitter width (organic ground 0.055, water 0.02, snow 0.025, rest 0.04), used by both
renderers in place of the hardcoded 0.02/0.04. Readability guards: Desert authored
at (0.79, 0.60, 0.36) deliberately more orange/saturated than CanvasUnseen parchment;
ocean path (`OceanColor` + WorldDeep dissolve) untouched — that is A3's.

**A4 — the lamp dies.** Both renderers get the SAME daylight rig (they light a
shared palette; divergence = drift): sun (1, 0.97, 0.90) @ 1.3, pitch −45°
(mid-morning, not noon — relief still shadows; the lamp was −27°/−32° amber @
1.8/1.7); ambient (0.62, 0.63, 0.66) @ 0.75 (was violet floor 0.55/0.6) — combat's
linear-light key-to-fill discipline. Materials: land satin 0.65 → gouache-matte 0.9;
water lacquer 0.15 → satin 0.55 (interim until A3); settlement markers Metallic
0.9 → 0 / Roughness 0.7; staging beacons Metallic 0.85 → 0 / Roughness 0.6
(emission glows kept everywhere — loudness law). **The pass-2 post shader
(tilt-shift/warm-grade/CA) needed no strip: it lived in the deleted prototype
`CampusAtlasPanel` (`_to_delete/`) and the live `StrategicView.BuildAtlas3D` path
hosts a plain SubViewportContainer.** Ruling 2 (city-zoom DoF) is therefore moot
until someone reintroduces a post layer. Background stays `WorldDeep` by DECISION:
the parchment sheet framed on a dark table read correctly in play; a light sky
behind a light sheet would mush. Revisit at A3 alongside the ocean dissolve.

**Screenshot checkpoint for this increment:** terrain lens with reveal on — hues
should read true (no amber cast), biome fields livelier (wider jitter), sea calmer,
canvas cooler/paper-toned rather than sepia, grout closer to pencil than embossed
gold, beacons still findable at whole-world zoom. Political/Corruption/Reach lenses
shifted slightly (they lost Grade's +12% sat) — acceptable; retune only if a lens
stops reading.

## A4b — exposure fix (same day, after user screenshots of A1+A4)
The first daylight rig RECREATED THE PASS-1 FAILURE (milky and flat): raising the
sun from −27° to −45° moved its top-face incidence from sin27 ≈ 0.45 to
sin45 ≈ 0.71, and the fill nearly doubled — total on flat tops went ~0.95 → ~1.35
at a collapsed ~1.9:1 key-to-fill. Pastel land, blown snow, near-white parchment.
Fix (exposure only, no palette surgery — never turn two knob sets at once): sun
energy 1.3 → 1.0, ambient → (0.55, 0.56, 0.60) @ 0.5, pitch unchanged; restores
≈0.97 total at ~2.3:1 in BOTH renderers. Confirmed good in A1+A4 screenshots
despite the exposure: hue truth (no amber cast), pencil-grey grout on canvas,
desert–canvas separation, calmer sea. Watch next screenshot for: green richness
returning, snow un-blowing, parchment back to paper tone, ink-dark deep ocean.

## A1b — swatch + underpaint tune (same day, after A4b screenshots)
A4b exposure CONFIRMED correct (strategic terrain renders swatches at authored
value; canvas world at whole-world zoom is the target image). Residual "washed"
read had three real causes, none of them lighting — do NOT touch the rig again on
this complaint:
1. **Greens authored too grey** — Grassland/Forest/Hills/Swamp/Marsh deepened and
   saturated (Forest now 0.21/0.36/0.18, etc.).
2. **Underpaint ≈ canvas** (val ~0.68 vs parchment 0.72) — silhouette/charted rings
   mushed into unpainted field; a fresh expedition window (90% canvas+silhouette by
   AREA) read as one cream sheet. Underpaint now a toned wash BELOW the paper:
   sat ×0.38, val→0.56 @ 0.6, canvas lerp 0.15.
3. **Snow/Mountain/canvas crowding near white** — Snow to 0.82/0.84/0.86 (faint
   cool cast), Mountain to 0.49/0.45/0.41 so snowcaps read against bare stone and
   neither reads as unpainted paper.
Milky pale-blue sea is acknowledged and DEFERRED — that is A3 (painterly water
plane + dissolve/background decision), not palette.

## Open threads
- Viewport background (`BackgroundColor = UITheme.WorldDeep`, both renderers) is
  still the dark void — a parchment world floating in darkness at cycle start.
  Deliberately left for A4 (sky/background ownership); revisit there.
- Ocean dissolve still fades explored deep sea toward `WorldDeep` — reads as ink-wash
  sea against canvas; acceptable, re-evaluate under A3 (painterly water).
- Decoration/marker/label styling over canvas (A5/A7) unchanged this pass.
- The 2D `StrategicView` fallback intentionally keeps the void look.
