# Imbuement — painterly redesign notes

**Status:** proposal / idea set. Nothing implemented.
**Companion to:** `painterly_style_guide.md`, `docs/glyph_cipher_spec_v2.md`
**Subject:** `Assets/Shaders/imbuement_aura.gdshader`, `imbuement_glyph.gdshader`,
`Scripts/Systems/Combat/Core/ImbuementOverlay.cs`, `Scenes/Combat/ImbuementOverlay.tscn`

---

## 1. Why the cipher fits and the imbuement does not

Not taste. The two systems are built in **different visual idioms**, and the
style guide names the one the game is supposed to be in.

| | Painterly system (grass, flowers, canopy, terrain, cipher) | Imbuement as built |
|---|---|---|
| Blending | **fully opaque**, dither-`discard` (guide §2.3, §8) | `blend_add` + `ALPHA` |
| Texture | flat regions, toon-banded, hand jitter | smooth `vnoise` gradients |
| Motion | **one coherent world-space gust**, everything in phase (§2.1) | 8 independent per-element scrolls, 0.15–8.0 speed |
| Shapes | hand-inked, wobbled, no perfect lines | analytic SDF flames/bolts/snowflakes |
| Colour | muted, saturation ~1.1, nothing crushed | `intensity` to 4.0, additive → blows to white |
| Silhouette | horizontal — ground, grass, flowers | vertical glowing column |

Every row is the imbuement doing the opposite of the house rule. Three of them
are things the style guide calls out **by name** as the signature of the look:
opacity, phase-matched wind, and muted colour.

The blunt version: **the imbuement is written in 2000s game-VFX language —
additive glow, value noise, fast flicker, mathematically perfect symbols — while
everything else on screen is written in painted language.** That is why it reads
as pasted on.

---

## 2. The counterargument, first

**The imbuement is gameplay state, and legibility beats cohesion.** Elements are
targetable and consumable (`element_tile` selector, `ConsumeElementTileEffect`,
`ImbuePathEffect`). A glowing column is visible from any camera angle at any
distance. A tasteful ground stain may not be. **Do not trade "which tiles are on
fire" for prettiness** — that is a strictly worse game.

So the redesign has to carry the constraint, not ignore it. The glyph cipher
already solved this exact problem and the answer generalises:

> **Split the visual into a function layer and an identity layer. Make the
> function layer bold, terrain-independent and hierarchy-carried-by-weight. Let
> the identity layer be painterly.**

For the cipher that is rose hub-and-spokes (function) over an inked stave
(identity). For the imbuement it would be one unmistakable element mark
(function) over painterly ground and grass treatment (identity).

Anything below that weakens the function layer is wrong regardless of how good it
looks in a screenshot.

---

## 3. Ideas, by leverage

### 3.1 Imbue the grass and the ground — not the air above them (highest leverage)

The single biggest change, and the one most in keeping with the guide.

Ask what a painter would actually draw to say *this ground is frozen*. They would
not draw a light column. They would paint the **grass and the earth** differently.

The scatter systems already have every channel needed:

- `painterly_grass` has a multi-stop height gradient, world-space **mass-tint
  clumping**, and per-instance jitter.
- `painterly_flower` reads a palette from `INSTANCE_CUSTOM`.
- `terrain_splat` already does per-theme atmosphere and emission highlights.

An imbued tile could push a tint into the grass on that tile: frost → pale
blue-white tips and desaturated base; fire → charred base, ember-warm tips;
shadow → crushed value, low saturation.

**And the best version of this idea: change the motion, not just the colour.**

The guide calls coherent wind "the single most important motion cue." So use it
as an expressive channel:

| Element | Wind treatment |
|---|---|
| Frost | `wind_bend` → ~0. **Frozen grass does not sway.** |
| Fire | detail flutter up, wave down — agitated, not rolling |
| Air | `wind_bend` up sharply, everything else matched |
| Earth | stiffness up, bend down — heavy, planted |
| Water | slow, high amplitude, long period |

A tile where the grass simply **stops moving** while the field rolls around it is
instantly readable, costs one uniform, and is impossible to achieve in any other
art style. It is the most "this game and no other" idea in this document.

### 3.2 Replace the aura column with a ground mark

The cipher just proved the pattern end to end: flat quad, `MeasuredTileTopY()`,
shader with an inscription sweep. Reuse it wholesale.

- **Fire** — irregular charred patch, ember flecks that drift *with the shared gust*.
- **Frost** — rime crystals growing inward from the hex edges, drawn as strokes.
- **Water** — shallow pool; `painterly_water.gdshader` already exists and already
  solves banded stylised water.
- **Earth** — cracked, upheaved plates with painted shadow in the cracks.
- **Shadow** — a stain that *desaturates and darkens* what is under it rather
  than adding anything on top.

Note what shadow implies: some elements should be **subtractive**. Additive
blending cannot express "this ground is wrong now."

### 3.3 Draw the element runes in the same hand as the spell sigils

The eight glyphs are analytic SDFs — perfect flames, perfect snowflakes. The
cipher's are hand-inked polylines with seeded jitter and stroke-width variation.

**Bake the element runes through `GlyphCipherView`.** Eight textures, baked once,
cached forever — `GlyphCipherTexture` already does exactly this per card. The
machinery exists.

The payoff is fictional as well as visual: *all magic in this world is inscribed
by the same hands.* An Enchanter's sigil and a fire-imbued tile's rune should
look like they came out of the same tradition. Right now they look like they came
from different games.

### 3.4 Band the noise

Cheapest visible win available. `vnoise` produces smooth gradients — the exact
signature of shader VFX. Push it through the guide's own `toon_band()` helper
(§9) at 3–4 bands and it reads as painted immediately.

Roughly one line per element branch. Do this first if you want to see whether the
direction is right before committing to anything larger.

### 3.5 Put the aura on the shared wind

If a vertical element survives at all, it must obey §2.1's phase-matching rule:
same `wind_noise` texture, same `wave_*` / `detail_*` / `wind_dir`, tune only
`wind_bend`. Embers and mist should drift **with** the gust that is moving the
grass around them.

Currently the aura's motion is 8 unrelated `TIME` scrolls. It is the only thing
in the scene not moving with the field, which is precisely what makes the eye
reject it.

### 3.6 If a vertical presence is needed, build it like canopy

Not a noise-filled cylinder — a few **painted ribbon quads**, cross-arranged,
with a hand-authored alpha shape, dither-`discard`ed (never `ALPHA`), swaying on
the shared gust. That is `painterly_canopy.gdshader` almost verbatim, and canopy
already proves the technique at scale.

### 3.7 One grammar, parameterised — not eight recipes

Both shaders are large `if (element_id == n)` ladders, each branch its own noise
formula and speed. That is *why* the elements do not cohere: there is no shared
grammar, only eight sketches sharing a file.

The cipher's structure is the alternative: **one grammar, per-element
parameters.** Palette, band count, motion speed, mark shape, wind response — a
table, not a switch. Adding a ninth element then costs a row, and it is
automatically consistent with the other eight.

---

## 4. Suggested order

Cheap → expensive, each independently shippable, each visible on its own.

1. **Band the noise** (§3.4) and **cap intensity / mute the palette**. One
   session. Tells you whether the direction is right.
2. **Put the aura on the shared wind** (§3.5). One session, high payoff.
3. **Grass wind response per element** (§3.1) — starting with frost, because
   "frozen grass stops swaying" is the strongest single beat available.
4. **Ground mark replacing the column** (§3.2), reusing the cipher's decal path.
5. **Hand-inked element runes** (§3.3) via `GlyphCipherView`.
6. **Refactor to one parameterised grammar** (§3.7) once the vocabulary settles.

Steps 1–2 are reversible and do not touch gameplay readability. Step 4 is the
first one that can *hurt* legibility — hold it to the §2 constraint and check it
at maximum camera distance before believing it.

---

## 5. What not to do

- **Do not remove the element indicator to make it subtle.** §2.
- **Do not write `ALPHA` in anything that joins the scatter passes** (guide §8,
  the tile-edge overwrite bug). The imbuement is currently a transparent-queue
  overlay and gets away with it; anything that becomes part of the grass/flower
  layers must be opaque + dither-discard.
- **Do not give each element its own motion timing.** That is the current bug.
- **Do not solve legibility with brightness.** The cipher tried alpha and it
  failed over grass; weight and shape are terrain-independent, brightness is not.

---

## 6. Cost correction — added after implementing steps 1–2

§3.1 claimed that "frozen grass stops swaying" **"costs one uniform."** That was
wrong, and it was wrong in a way worth recording, because the error was made by
reading the *shader* and not the *material* or the *spawner*.

### 6.1 What steps 1–2 actually cost, and what they revealed

Correct as written, with one substantive amendment: **the wind values to copy
are the ones on `painterly_grass.tres`, not the ones in `painterly_grass.gdshader`.**
The material overrides nine of the eleven, and several by more than an order of
magnitude:

| uniform | shader default | live value (`painterly_grass.tres`) |
|---|---|---|
| `wind_bend` | 0.15 | **1.0** |
| `wind_amplitude` | 0.4 | **0.10** |
| `wave_scale` | 0.12 | **0.075** |
| `wave_speed` | 0.5 | **0.075** |
| `wave_strength` | 0.7 | **1.0** |
| `wave_stretch` | 0.35 | **0.599** |
| `detail_scale` | 1.2 | **0.10** |
| `detail_speed` | 0.25 | **0.035** |
| `detail_strength` | 0.25 | **0.22** |

Copying the shader defaults would have produced a column that moved on a
*plausible* wind, in phase with nothing, while looking correct in every diff.
`wave_speed` alone is off by 6.7×. This is the failure mode the whole exercise
exists to avoid.

Same class of trap on the texture: the grass samples
`Assets/Materials/PainterlyMaterials/Painterly_wind_noise.tres` — 1024², a
FastNoiseLite at frequency 0.0081. `WindNoise.CreateSeamless()`, the obvious
C#-side helper, builds a *different* texture (256², frequency 0.015, 3 octaves).
Wiring the helper in would have been one line and would have silently broken the
phase match. The `.tres` must be referenced directly, which is what
`ImbuementOverlay.tscn` now does.

### 6.2 Why step 3 is not one uniform

Three separate findings, each independently fatal to the cheap version:

1. **`INSTANCE_CUSTOM.r` is already taken.** `painterly_grass.gdshader` reads it
   for `stiffness_from_instance_height`, and the spawner writes it via
   `mm.SetInstanceCustomData(i, customHeight)`.
2. **Custom data is not always allocated.** The spawner sets
   `mm.UseCustomData = writeHeights`. When instance heights are not written,
   there is no custom-data buffer at all, so any design that leans on `.g`/`.b`
   is config-dependent — it would work on one map and silently no-op on another.
3. **Grass is chunked at `GrassChunkTiles = 3`.** Nine axial tiles share one
   MultiMesh, so nothing per-*chunk* can express per-*tile* state, and a
   material-level uniform cannot either.

There is also no existing board-space lookup to piggyback on. `terrain_splat`
looks like a candidate but is not one: its hex grid lines are computed in tile
space, and its splat indices arrive as per-vertex attributes, not from a map
texture.

### 6.3 The split that actually matters

The step is not one thing. It is two, with costs an order of magnitude apart,
and the doc lumped them together.

**The ground half is cheap — genuinely close to the original estimate.** Terrain
tiles are individual `MeshInstance3D`s and `terrain_splat.gdshader` already uses
Forward+ **instance uniforms** (`instance uniform float vista_fade`), set from
`HexTile.MarkAsVista` via `mi.SetInstanceShaderParameter(...)`. An
`instance uniform float imbue_element` / `imbue_strength` follows that pattern
exactly: a few shader lines, two lines in `HexTile.SetElement`, no new systems.
*(Caveat to check before relying on it: `HexTile` has a `_generatedMode` in which
terrain colour lives in mesh vertex data. `MarkAsVista` reaches for the
per-tile `HexMesh` unconditionally, which implies it survives, but that has not
been verified in generated mode.)*

**The grass half needs a world-space imbuement field.** A small texture keyed by
axial coordinate, written on `SetElement`, sampled in `painterly_grass`'s
`vertex()` by world XZ through the inverse hex transform, nearest-filtered so
tile boundaries stay hard. Roughly:

- ~15 lines in `painterly_grass.gdshader` (+4 uniforms)
- a new `ImbuementField` owning an `ImageTexture`, ~150 lines
- one call site in `HexTile.SetElement`
- the same texture then serves `painterly_flower`, `painterly_canopy` and
  `terrain_splat` for free, which is the argument for paying for it

**Revised estimate: one session for the ground half, one to two for the grass
half, and they should ship separately.** The ground half is also the safer one
to do first — it is additive and reversible, whereas the grass half edits the
single most widely used shader in the game, where a regression is a regression
everywhere.

### 6.4 Sequencing consequence

§4's order stands with step 3 split in two, and the ground half promoted:

3a. Per-tile ground treatment via `terrain_splat` instance uniforms. Cheap.
3b. World-space imbuement field → per-tile grass wind response. Not cheap.

Doing 3a first also de-risks step 4 (the ground mark), because it establishes
that ground-level element cues are legible at maximum camera distance *before*
anything is taken away from the column.
