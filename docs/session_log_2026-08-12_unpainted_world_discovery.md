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

## A7 — marker language (same session; A9c rivers confirmed in play)
- **`PainterlyProps.Banner()`** — planted standard, ~3.3 tall, base at y = 0.
  TWO surfaces: 0 = pole (baked wood material), 1 = pennant (recolour per
  marker via `SetSurfaceOverrideMaterial(BannerFlagSurface, …)`). Pennant emits
  BOTH windings + the override material is `CullMode.Disabled` — a flag must
  read from every side. Built via AppendFrom (pole) + `Commit(existing)`
  (appends pennant as surface 1 — the one engine-API assumption this
  increment; if the pennant is missing at build, that call is the suspect).
- **Atlas staging beacons** → gold standards (`FlagMaterial(Gold, 0.7)`); the
  glowing orb cap stays (smaller, 0.55) — it is the loudness element, and
  AddMarker's city-tile tracking still hides portal banners in city view.
- **Warfronts** → red war banners at 1.3× scale + orb + "⚔ War" label (spike
  gone; loudness preserved).
- **POI markers, both renderers** → flattened paint-DABS (SphereMesh height
  ~0.25) sitting on the ground instead of floating balls; emission kept.
- **Kingdom-border ink** (the pass-3 candidate, now real): `_borderLayer` in
  atlas RebuildEdges — a thin dark stroke (`Hex3DPalette.BorderInk`) on every
  interior edge where two differing realms (or realm vs wilds) meet, land only,
  BOTH sides discovered (no leaking into canvas), edge drawn once via direction
  bits 0–2. All lenses; hidden in city view with rivers/roads.
- Left as-is, deliberately: shard-gate + Convergence spikes (arcane spikes read
  correctly), settlement blocks + labels (already de-metallized in A4).

NOT compile-run. Loudness check on build: staging standards and war banners
must stay findable at whole-world ortho zoom — if the pennants vanish at that
distance, raise the orb emission or banner scale, don't thicken the pennant.

## Expedition readability + stroke continuity (same session; two user rulings)
Rulings: (1) expedition window "too similar, heights busy and hard to navigate";
(2) "water and roads are not continuous on the tiles."

**Window readability (window-only; strategic untouched — the two views have
different jobs: survey relief is information, walking relief is noise):**
- `HeightScale = 0.45` — TileHeight's variable part compressed; base 0.22 and
  water heights untouched; 1.0 restores the strategic profile. PickTile shares
  TileHeight so picking stays consistent.
- Land material (window instance only): grain_scale 1.8 / grain_strength 0.11 —
  the atlas grain is too broad to read at walking distance.
- Hills shrub clumps (window decorations): 1–2 small flattened broadleaf blobs
  on 40% of Hills tiles, dry-olive, deterministic, Revealed-only — breaks the
  gold fields.

**Stroke continuity (BOTH renderers — the strategic map had the same breaks,
smaller):** the shared edge midpoint now sits at the AVERAGE of the two
rendered heights, so each tile's half-stroke meets its neighbour's at the same
point and paths SLOPE across tiles instead of jumping at seams. Rivers: the
Bézier interpolates Y through the tile-centre control → smooth vertical
profile, and a stroke toward a hidden neighbour dives to the canvas slab
(window `RenderedTileHeight` helper — TileHeight's hidden branch returns the
old void value, NOT the rendered FogSlabHeight; averaging must use what is
actually drawn). Roads: yaw-only basis → full tilted basis (local +X along the
sloped segment, orthonormal Y/Z, ×1.05 overlength so joints at slope kinks
close).

NOT compile-run (static checks only). Judge: expedition board navigable with
gentle relief; rivers running downhill continuously; roads climbing steps
without gaps; shrub-dotted hills. If 0.45 flattens too much, raise toward 0.6;
if cliffs still occlude, drop toward 0.35 — one knob.

## Window terrain break-up, stage 1 (user: "break up the terrain like combat?")
Ruling given as a staged answer: YES for the window, NO for the strategic map
(survey view keeps crisp tiles), and NOT the full combat weld port yet.

- **Stage 1 (this increment)**: `PainterlyProps.HexTileMesh(taper)` — a hex
  prism with SUBDIVIDED top (centre + mid ring + rim, 18 top tris), matching
  CylinderMesh conventions exactly (unit height ±0.5, x=sin/z=cos corner phase,
  no bottom cap; windings verified by the RH-normal sign test against the CW
  front-face rule; flat outward wall normals). New shader uniforms
  `top_undulation` (default 0 — atlas flat) + `undulation_scale`: land mode
  rolls TOP vertices by static world-space noise, world-unit amplitude through
  the instance basis (the water-swell conversion). World-space ⇒ adjacent rims
  displace identically ⇒ ground undulates ACROSS tiles, no tearing. Window land
  layer: custom mesh, grout thinned 0.96 → 0.985, undulation 0.06 @ scale 0.5.
  `MakeTileLayer` gained an optional customMesh (material set on PrimitiveMesh
  or ArrayMesh surface 0 accordingly). HexTileMesh cache ignores taper after
  first call (single caller).
- **Stage 2 (escalation, NOT built)**: true welded window mesh — merged
  ArrayMesh of the ~469 window tiles with combat-style corner averaging under
  a cliff threshold, canvas slabs hard-edged, colors baked per-vertex. ~a
  session; touches picking + recolor. Build only if stage 1 screenshots still
  read as poker chips.
- Watch on build: move-hint overlays sit ~0.02 above tile tops — undulation
  peaks (~+0.05) may poke through highlight quads; if so raise the overlay
  lift, don't drop the undulation first.

## Stage 2 — WELDED window terrain (user rulings: clipping + "full welded look")
Two rulings: stroke clipping (root cause: stage-1 undulation lived in the
SHADER, invisible to C# placement) and "I kind of want to see the full welded
look." Both solved by the same move — terrain shape becomes real geometry with
ONE ground function shared by the mesh and the strokes. Window-only; the
strategic map keeps crisp prisms. `UseWeldedTerrain = false` is the
kill-switch back to stage 1.

- **`BuildWeldedLand`** (ExpeditionWindow3D): one merged ArrayMesh for all
  revealed/silhouette land. Corners = centroid of the three meeting tile
  centres (orientation-proof); heights/colours weld by CONNECTED COMPONENTS
  over pairwise diffs ≤ `WeldThreshold` (0.30) — symmetric, so welded corners
  are crack-free by construction; chained welds smooth 2-step slopes into
  ramps (accepted). Unwelded edges emit walls down to what is really below
  (neighbour land rim / canvas slab / water top / boundary skirt); the lower
  side skips (no double walls). Vertex colours carry TileColor with corner
  blending — soft biome transitions, exactly the "natural feel" asked for.
  Winding settled PER-TRIANGLE by the RH-normal sign test (CW front rule) —
  no orientation assumptions to get wrong. Undulation (CPU value noise,
  UndulationAmp 0.06) baked into vertices.
- **`SampleGround(tile, p)`** — welded fan barycentric interpolation +
  undulation: the single source of truth for surface height.
- **Strokes**: `RiverMesh.Build` gained a grounded overload (per-tile sampler,
  lift, meanderScale); every ribbon vertex (banks included) re-heights to
  ground + lift. Rivers lift 0.045; **roads are now ribbons too** (width 0.15,
  meanderScale 0.3, matte vertex-colour material) — they hug the welded ground
  identically. Window MakeEdgeLayer deleted (dead). Atlas untouched (flat
  tops there — the old signature wraps the new one).
- **Decorations** stand on `SampleGround` (flat TileHeight would float/bury
  props by ±0.2). `_landLayer` is now `GeometryInstance3D` (welded
  MeshInstance3D or the stage-1 MultiMesh fallback).
- Stage-1 shader undulation NOT set on the welded material (geometry carries
  the roll); HexTileMesh remains for the fallback path.

RISKS (not compile-run, the biggest blind increment of this pass): brace/paren
balanced, symbols verified, but the weld emission + wall floors + barycentric
sampler have never rendered. Watch for: holes at corners (weld asymmetry —
should be impossible by construction, but that claim has never met a GPU),
inverted triangles (per-tri winding test should prevent), pawn/move-hint
overlays now sitting on welded ground they don't sample (markers float high —
likely fine; if hints clip, they need the same SampleGround treatment).
PickTile intersects flat per-tile planes — welded deviation ≤ ~0.2 is inside
its tolerance; if picking feels off near cliffs, that is where to look.

## Stage 2b — wall fix (welded terrain CONFIRMED IN PLAY, first build)
The weld rendered on first build: continuous ground, rivers/roads ON the
surface, props planted. Two wall blemishes (the dark angular "torn hole"
shards near cliffs), both fixed:
1. **Per-vertex wall floors** — `WallFloor` dropped to the neighbour's CENTRE
   height as a flat shelf, leaving triangular slivers where the neighbour's
   rim varies corner to corner. `WallFloorAt(s, i, rimVert)` now matches each
   rim vertex to the neighbour's nearest boundary vertex by XZ (+ undulation
   at that XZ) — the two sides share wall edges exactly. `WallQuad` takes
   per-vertex floors.
2. **Wall shading** — full skirt_darken (0.26) + stripes + shadow read as
   voids; window welded material now sets skirt_darken 0.14 / stripe_strength
   0.10 — cliffs read as painted banks.

## Stage 2c — dells + paper margin (user: "what are these holes?" / edge corners)
- The "holes" were NOT lakes: land pockets ≥2 compressed terrace steps below
  their surroundings, declared cliffs by WeldThreshold 0.30 → sheer-walled
  sinkholes. **WeldThreshold 0.30 → 0.50**: ≤2-step differences now weld into
  steep dells; only 3+ step drops stay true cliffs (rarer = more meaningful).
- **Canvas margin**: the window disc simply ENDED — hex-scalloped land against
  black void (no tiles beyond the streamed disc, not even canvas). Three BFS
  rings of parchment slabs now extend past the window edge (in-bounds only,
  not pickable), so the walkable painting sits on paper like the strategic
  map. Sharp hex facets on interior CLIFFS kept deliberately — carved
  painterly read, same as combat's cliff rule.

## Stage 2d — river clips, canvas sheet, hard city edges (three user rulings)
1. **Rivers still clipped** — two residual mechanisms, both fixed:
   (a) bank vertices near welded edges fell into the NEIGHBOUR's fan; the
   bound-tile sampler extrapolated its own edge plane there. `SampleGround`
   now tries the neighbours' fans (via `TryFan`) before extrapolating.
   (b) across UNWELDED cliff edges each half samples its own fan → the halves
   end at different heights and the river ran into the wall. The higher side
   now drops a short steep WATERFALL ribbon (tapered spoke, edgeMid ±0.14
   toward the lower side) when the gap exceeds 0.15. Lifts raised: rivers
   0.06, roads 0.055 (fan-crease poke margin).
2. **Parchment ring didn't match the welded ground** — `BuildCanvasSheet`:
   the entire canvas (hidden window tiles + 3-ring margin) is now ONE seamless
   flat sheet — same corner-centroid construction as the land, no grout,
   paper grain from the canvas shader mode, wet edge kept, walls where the
   sheet ends (skirt) or overhangs LOWER painted ground; higher painted ground
   still walls down to the sheet from its side. Prism canvas remains the
   non-welded fallback. `_canvasLayer` → GeometryInstance3D.
3. **Cities: hard transition** — heights still weld across a city boundary
   (the ground is continuous) but COLOURS average only among participants on
   the same side of it (`IsCityTile` gating in WeldCorner + edge colours): a
   settlement footprint is a built thing with a hard edge, not a biome
   gradient.

## Open threads
- Viewport background (`BackgroundColor = UITheme.WorldDeep`, both renderers) is
  still the dark void — a parchment world floating in darkness at cycle start.
  Deliberately left for A4 (sky/background ownership); revisit there.
- Ocean dissolve still fades explored deep sea toward `WorldDeep` — reads as ink-wash
  sea against canvas; acceptable, re-evaluate under A3 (painterly water).
- Decoration/marker/label styling over canvas (A5/A7) unchanged this pass.
- The 2D `StrategicView` fallback intentionally keeps the void look.
