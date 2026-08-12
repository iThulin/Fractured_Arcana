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

## A2/A3-lite — painterly prism shader (same session; A1b approved "good enough")
One shader replaces the StandardMaterials on ALL tile MultiMeshes in both
renderers. Deliberate re-scope: the full welded water-plane port (A3-full) stays
on the books; a mode on the prism shader gets most of the win with zero geometry
risk.

- **`Assets/Shaders/painterly_world_prism.gdshader`** (new) — one file, `mode`
  uniform: 0 LAND (world-space fbm brush grain on tops; side walls darkened +
  vertically striated — the combat S11 skirt learning), 1 WATER (world-space
  swell on top vertices only, amplitude converted through the instance basis' Y
  length so the world-unit height survives per-instance scaling; adjacent prisms
  share world XZ ⇒ identical offset ⇒ welded seams by construction; drifting
  two-tone sky-band wash over the instance colour; banded sun-glint dabs in
  light(), noise-gated so the sun draws broken dashes, not a PBR hotspot),
  2 CANVAS (two-direction paper fibre, matte). Custom light(): wrapped banded
  toon (3 bands, solid-geometry variant — no abs(), front faces only), shadow
  via ATTENUATION. FULLY OPAQUE — no ALPHA anywhere (MultiMesh sort law).
  Procedural hash noise — no texture dependency, nothing to assign.
- **`Scripts/Systems/Overworld/PainterlyPrism.cs`** (new) — shared material
  factory (Hex3DPalette rule: one home, two views, no drift).
  **`PainterlyPrism.Enabled = false` is the kill-switch** → pre-A2
  StandardMaterial3D fallback (also automatic on shader-load failure, with a
  PushWarning).
- **`WorldAtlas3D.RebuildTiles`** — three mesh materials now from the factory.
- **`ExpeditionWindow3D.MakeTileLayer`** — gained `prismMode`; three call sites.
- `.uid` siblings for the two new files generate on project open — commit them.

NOT compile-run; the shader has never been through a GLSL compiler — errors
panel first. Live-tune via Remote inspector on the layers' materials (uniforms
are grouped + documented); disk edits to the .gdshader hot-reload shaders but a
running scene keeps old MATERIAL instances — restart the scene after C# edits.
Judge: sea gains motion + structure (the milky flat read should break up),
mountains get painted cliff-band skirts, canvas gains paper tooth. Banding
strobe risk at whole-world zoom was the known A2 risk — if bands shimmer, raise
`toon_softness` or drop `toon_bands` to 2.

## A5 — painterly canopy props (same session; A2/A3-lite confirmed in play)
User screenshots confirmed the prism shader in play (terrain lens reads as a
painted world; sea has body + cloud-washed structure). Continued to A5.

- **`Scripts/Systems/Overworld/PainterlyProps.cs`** (new) — shared procedural
  prop-mesh factory (one home, both views). `BroadleafCanopy()` = three
  overlapping flattened blobs (Ghibli forest mound, no trunk — at map zoom a
  forest is canopy mass, and one mesh = one MultiMesh layer);
  `ConiferCanopy()` = three stacked squashed blobs (soft pine, not a traffic
  cone). Built once via `SurfaceTool.AppendFrom` (low-poly SphereMesh merged →
  single ArrayMesh), statically cached, matte instance-coloured material.
  Canopies are BASE-AT-Y=0; `PeakCone()` keeps the stage-1 cone AND its centre
  origin (peaks still read as spires; callers' maths untouched; NOTE the cache
  ignores params after first call — both call sites pass identical values).
- **Both `RebuildDecorations`** — Forest split 60/40 broadleaf/conifer by hash,
  random yaw per instance (kills the clone read), placed at ground height;
  colours: broadleaf (0.21, 0.35, 0.15) jitter 0.12, conifer (0.13, 0.25, 0.15)
  jitter 0.10 (window trees also gained jitter — they had none).
- **Latent scatter-law violation fixed**: the deco layers NEVER set
  `CustomAabb` (style guide §8 — auto AABB on world-space instance transforms
  frustum-culls the whole layer as one unit). Both `MakeDecoLayer`s now compute
  min/max over instance origins grown by mesh extent. This was one camera turn
  away from popping the entire forest.

NOT compile-run (static checks only). `SurfaceTool.AppendFrom(Mesh, int,
Transform3D)` is the one engine-API risk — if it errors at build, the fallback
is committing each sphere's arrays manually. Judge: forests as soft canopy
masses at map zoom + walkable-scale mounds in the expedition window; Explored-
only gating unchanged (props are still discovery's reward).

## A9 — rivers & roads as drawn strokes (same session; A5 confirmed in play)
User screenshots confirmed A5 (forests read as canopy masses at both zooms).
Continued to A9. Audit finding that shaped it: the WINDOW already drew edges the
right way (centre→edge-midpoint halves meeting at boundaries = continuous
polylines); the ATLAS had the worse boundary-dash model. So:

- **Atlas `RebuildEdges` REPLACED** with the window's centre-out model. Each half
  hugs its OWN tile's top (+0.02) — lines follow terrain and step at cliffs like
  ink over relief. Water tiles now skipped (matches window: no strokes across
  lakes); Unseen skipped (nothing drawn on canvas); **Charted keeps strokes** —
  inked chart lines on the underpainting, deliberate.
- **Styling both renderers**: `Hex3DPalette.RiverInk` (0.25, 0.34, 0.44 slate) /
  `RoadStroke` (0.56, 0.47, 0.34 worn earth) — replaces bright
  TerrainWaterShallow / TerrainRoad.Lightened; thinner profile (Y 0.025–0.03;
  river width 0.24–0.26, road 0.14–0.15); matte 0.95; window strokes dropped
  from +0.05 to +0.03 above ground.
- **City view**: strokes hidden in EnterCityMode, restored on Leave (+ set on
  rebuild from `_cityMode`) — closes the Phase-2 "litter at city zoom" polish
  note.
- **Scatter law**: both `MakeEdgeLayer`s gained explicit `CustomAabb` (latent,
  same as the deco layers).

NOT compile-run (static checks only). Judge: rivers as continuous slate lines
threading the terrain, roads as faint earth lines, neither floating nor
fluorescent; fog frontier truncates strokes naturally (a hidden neighbour never
draws its half). Bridges (river+road cross) just overdraw for now — a tick-mark
feature belongs to A7/polish.

## A9b — winding river ribbons (same session; user rejected the line look)
User ruling: straight strokes don't work — rivers need flow and natural winding.
Roads keep the straight strokes (roads ARE straight); rivers rebuilt as GEOMETRY:

- **`Scripts/Systems/Overworld/RiverMesh.cs`** (new, shared) — one merged
  ArrayMesh of flat ribbons. Per tile: 2 river edges → ONE quadratic Bézier from
  edge-mid to edge-mid, control at the tile centre (the river bends THROUGH the
  tile); 1 edge → tapering source spoke (born thin); 3+ → confluence spokes.
  Deterministic meander perpendicular to the tangent; envelope 16t²(1−t)² has
  zero value AND slope at endpoints, and endpoint tangents are collinear with
  the neighbour's spoke line ⇒ C1 continuity across tile boundaries by
  construction. 3-vert cross-sections (bank/waterline/bank) with vertex colours
  darkening to `Hex3DPalette.RiverBank` at the edges (recessed-channel cue),
  width breathing ±15% along the run. CW winding per the Godot front-face
  gotcha — **if rivers render invisible from above, flip the tri order in
  Quad(), that's the one untested geometry assumption.**
- **`PainterlyPrism.RiverMaterial()`** — the water-mode shader retuned for a
  ribbon: swell_amplitude 0 (a displaced ribbon would poke through its banks),
  sky_mix 0.18, finer sparkle. Vertex colours ride COLOR exactly like instance
  colours. StandardMaterial fallback path intact.
- **Both renderers**: `_riverLayer` is now a `MeshInstance3D`; RebuildEdges
  collects per-tile (centre, edge-mid list) for rivers, roads unchanged.
  `RiverInk` retired from Hex3DPalette; `RiverWater`/`RiverBank` added.
- Auto AABB is CORRECT here (real vertices, not world-space instances) — no
  CustomAabb needed on the ribbon.

NOT compile-run. Tuning knobs (RiverMesh.Path): amp 0.10–0.22, freq 0.8–1.3,
segs 14/8; width 0.22 at the call sites.

## A9c — river visibility tune (user: geometry better, lines too thin)
Ribbons CONFIRMED rendering + winding in play (CW winding assumption held).
Width 0.22 → 0.36 atlas / 0.30 window (map rivers are exaggerated, not to
scale); `RiverWater` deepened + saturated to (0.25, 0.41, 0.58) and `RiverBank`
to (0.10, 0.18, 0.28) — the water must separate from olive ground, not
harmonize with it. If still faint at whole-world zoom the next knob is
`sky_mix` → 0.10 on RiverMaterial (the wash lightens the base), then width.

## Open threads
- Viewport background (`BackgroundColor = UITheme.WorldDeep`, both renderers) is
  still the dark void — a parchment world floating in darkness at cycle start.
  Deliberately left for A4 (sky/background ownership); revisit there.
- Ocean dissolve still fades explored deep sea toward `WorldDeep` — reads as ink-wash
  sea against canvas; acceptable, re-evaluate under A3 (painterly water).
- Decoration/marker/label styling over canvas (A5/A7) unchanged this pass.
- The 2D `StrategicView` fallback intentionally keeps the void look.
