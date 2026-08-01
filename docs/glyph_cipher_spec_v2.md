# Enchanter Glyph Cipher — Specification v2 (radial stave)

**Status:** authoritative. **Supersedes v1** — delete `docs/glyph_cipher_spec_v1.md`.
**Scope:** Enchanter school only. 42 spell halves across 21 cards.
**Date:** 2026-08-01
**Implements:** `Scripts/Systems/Combat/Glyphs/GlyphCipher.cs`
**Verified by:** `Scripts/Dev/GlyphCipherSelfTest.cs` (`godot --headless -- --verify-cipher`)

---

## 0. What this is

Every Enchanter spell gets a procedurally generated inscribed sigil: a **radial
stave** whose arms and crossbars encode the spell's Name, overlaid with a **hub
and spokes** encoding what the spell does. One generator, 42 unique assets, zero
per-card art.

The Enchanter is the Namer school and the Weave capstone literally Names an
enemy, so a glyph that *is* the encoded Name is the tightest visual-mechanical
rhyme available. That is why the semantic vocabulary has to be honest rather than
decorative: if the glyph is a real encoding, it has to decode.

| Layer | Colour | Carries | At tile scale |
|---|---|---|---|
| **Identity** | ink (near-black) | six arms, one crossbar per letter, a terminal ornament per arm | dimmed to 30% — becomes texture |
| **Function** | school rose `#C45B9E` | hub shape = recipient, spokes = effect verbs | full opacity, weight ×1.6 |

---

## 1. Why v2 replaced v1

**v1 connected letter nodes with chords across the interior of the circle. That
is a random walk on a ring, and it reads as scribble because it is scribble.** No
amount of tuning the bow, the stroke weights or the jitter fixes it — the problem
is the mark language, not its parameters. v1 is dead; this document replaces it
entirely.

Two intermediate attempts are recorded so they are not retried:

- **A single vertical stave with branches (a "fern")** reads as designed but
  collapses into sameness. A comb of branches on a straight line has almost no
  silhouette variation, so every name becomes the same feather. Order without
  distinctiveness is the opposite failure to v1's, and just as bad.
- **A circular ogham inscription round the rim** reads well and is excellent at
  tile scale, but a ring of tick groups registers as a gauge or a dial rather
  than a rune. Worth revisiting if the tile decal ever needs to be legible at
  32 px, which it currently does not.

### 1.1 Where distinctiveness comes from in v2

Four independent channels, deliberately:

| Channel | Range | Notes |
|---|---|---|
| arm count | 3–6 | driven by name length |
| arm depth | 1–3 | letters on the fullest arm |
| crossbar lengths | 13 values × 2 forms | the actual letter data |
| terminal ornament | 6 shapes per arm | **the important one** |

The ornaments matter most because they put variation in the *silhouette*, which
is the channel that survives being shrunk to a tile. Arm count and depth alone
give 9 distinct silhouette classes over the 42-half corpus; the ornaments
multiply that.

---

## 2. Coordinate frame

Unit space. Centre `(0,0)`, rim radius `1.0`, **+Y is down** (screen convention,
so a renderer maps `screen = centre + p * radius` with no flip).

θ is degrees **clockwise from straight up**: `Polar(θ, r) = (r·sin θ, −r·cos θ)`.

### 2.1 The six-fold skeleton

| | Bearings (θ) |
|---|---|
| **Arms** (identity) | 0, 60, 120, 180, 240, 300 |
| **Spokes** (function) | 30, 90, 150, 210, 270, 330 |

Offset by exactly 30°, so **the two layers interleave and can never collide.**
Asserted as INV-7 rather than left to chance — it is the kind of constraint that
silently breaks the moment someone adds a seventh verb.

### 2.2 Radii

| Name | Value | Role |
|---|---:|---|
| `RimRadius` | 1.00 | enclosing circle |
| `ArmR1` | 0.87 | where the deepest arm ends |
| `SpokeRadius` | 0.52 | where a function spoke ends |
| `ArmR0` | 0.19 | first crossbar; everything inside is the hub's plaza |
| `ArmInner` | 0.0665 | arms are drawn from here so they meet *under* the hub |
| `HubRadius` | 0.135 | disc hub (Self / Ally) |

Arms are drawn from inside the hub rather than stopping at its edge, so the hub
sits on a solid convergence instead of floating in a gap.

---

## 3. Identity layer — the Name

### 3.1 Letter table

Two sets of 13. Membership decides the **form** of the crossbar; index within the
set decides its **length**.

| set | letters | crossbar |
|---|---|---|
| **outer** (13 most common English letters) | `A C D E H I L N O R S T U` | full, symmetric |
| **inner** (13 least common) | `B F G J K M P Q V W X Y Z` | one-sided (12% overhang on the back) |

Each set is alphabetical, so a letter is findable by scanning. Half-length is
`0.045 + slot × 0.0160`, i.e. **0.061 … 0.253** over slots 1–13 — wide enough to
read at card scale.

Symmetric-vs-one-sided is a far more legible binary at small size than
left-vs-right would be on a radial arm, where "left" flips meaning as the arm
rotates. And because the common letters are the symmetric ones, a typical stave
is mostly balanced ticks with the rare letters reading as ornament.

> Inherited from v1 and still true: an **A–M / N–Z fold does not balance
> anything**. J K L M and W X Y Z are all rare and would land on the same slots.
> Measured angular-mass coefficient of variation over the 42-name corpus: 0.659
> alphabetical vs **0.378** for this fold.

### 3.2 Arm layout

Letters fill arms **contiguously**: with `m = ceil(n / 6)`, arm 0 holds the first
`m`, arm 1 the next `m`, and so on. Reading is *"walk arm 0 outward, then arm 1"*
— no interleaving to reconstruct.

```
n = 3   → m = 1 → 3 arms × 1        (Sap)
n = 10  → m = 2 → 5 arms × 2        (Snare Glyph)
n = 17  → m = 3 → 6 arms: 3,3,3,3,3,2   (Absolute Territory)
```

Crossbar *d* on an arm sits at `rFirst + d·dr`, where the radial pitch `dr` is
normalised so the **deepest** arm's last crossbar lands at
`ArmR1 − 0.17·(ArmR1 − ArmR0)`. A single-crossbar stave places it halfway out.

**The deepest arm reaches the rim; shallower arms stop 55% of the way from their
last crossbar to it.** Normalising *every* arm to full length was tried and made
every six-arm name the same silhouette — the exact sameness that sank the fern.

### 3.3 Terminal ornaments

Chosen by `slot(last letter on this arm) mod 6`:

| kind | shape |
|---:|---|
| 0 | plain |
| 1 | filled dot |
| 2 | fork (two prongs forward) |
| 3 | wide crossbar |
| 4 | open ring |
| 5 | chevron (two prongs back) |

### 3.4 Doubled letters

A letter identical to the one before it gets an **open ring around its
crossbar**. Unlike v1 there is no degenerate zero-length case to work around —
every letter gets its own crossbar regardless, so `CrossbarCount == Letters.Length`
is an equality (INV-1), not a bound.

12 of 42 halves carry one: Sigil Link (LL), Sigil of Focus (FF), Spell Anchor
(LL), Empower Rune (RR), Maze of Mirrors (RR), Web of Fate (FF), Mirror Ward
(RR), Runic Cascade (CC), Sovereign Pillars (LL), Puppeteer (PP **and** EE — two),
The Grand Design (DD), Absolute Territory (RR).

### 3.5 Read-order marks

- **Start** — filled disc (r 0.042) on arm 0 at `r = 0.118`. Reading begins here
  and proceeds clockwise through the arms.
- **Terminal** — open ring (r 0.054) on the last letter's crossbar.

Marker sizes were enlarged in the second tuning pass — start 0.034→0.042,
terminal 0.044→0.054, retrace 0.042→0.050, ornament dot 0.028→0.038, ornament
ring 0.040→0.055. Partly taste, but mostly correctness: the tile LOD draws the
stave at 1.7× width (§8), and **an open ring whose radius stays put while its
stroke thickens closes into a solid blob.** Losing the arm-tip ornaments costs
the silhouette variety they exist to carry (§1.1), at exactly the range where
silhouette is all that survives.

These feed `CipherStroke.Weight`, so the change invalidated the goldens and they
were regenerated. Aggregate moved `0x47082FED` → **`0xEF7EE845`**.

### 3.6 Painterly compliance

Per `painterly_style_guide.md`: no geometrically perfect line. Interior polyline
samples are jittered; **endpoints never are**, so an arm and its crossbars stay
registered with each other and the stave does not fray at its joins.

| Source | Amplitude |
|---|---:|
| arm interior samples | 0.008 |
| crossbar / ornament interior samples | 0.005 |
| spoke interior samples | 0.006 |
| rim wobble | ±0.006 over 96 samples |

---

## 4. Function layer — what the spell does

Capped, permanently. The function layer is the one that cannot be reduced to
texture at tile scale, so every node added is an un-dimmable tax on learnability.

### 4.1 Recipient — the hub's shape

The single most useful thing to read off a tile at a glance, so it gets the
boldest mark on the glyph.

| Recipient | Hub | Size |
|---|---|---:|
| SELF | filled disc | 0.135 |
| ALLY | filled disc with a punched centre | 0.135 |
| TILE | filled diamond | 0.176 |
| ENEMY | filled triangle | 0.189 |

**Filled, not outlined.** At 64 px an outlined diamond disappears into the six
arms crossing behind it. The ALLY punch is why `GlyphCipherView.PaperColor`
exists — it must match whatever the glyph sits on.

ALLY is currently unpopulated in the Enchanter corpus (`friendlies_only` appears
on no Enchanter half). It is reserved rather than reallocated: ally-benefit
payloads already ride inside tile-targeted glyphs (`Empowerment Field`, `Geas`
bottom), and the first card that targets an ally directly will need it.

### 4.2 Verbs — the spokes

| Verb | θ | Covers |
|---|---:|---|
| WARD | 30 | shield, heal, buff, summon a guardian |
| MOVE | 90 | any displacement — self, ally, or enemy |
| INSCRIBE | 150 | create glyphs / persistent inscriptions |
| INVOKE | 210 | manipulate the glyph network; draw on it for resources |
| BIND | 270 | control and debuff |
| STRIKE | 330 | damage |

Declaration order is spoke order and is also the fixed precedence for the reveal.
**Never JSON field order** — field order is not semantic and will not survive a
refactor of the card data.

> The brief proposed *damage, control, buff, debuff, glyph-prepare, movement*.
> That set does not partition this corpus: "control" and "debuff" are
> inseparable here (Geas, Hex Mark and Mana Tithe are all both, and the project's
> own vocabulary uses a single `Control` tag), and it has no node for glyph
> *manipulation* — 7 of 42 halves, and the school's actual mechanical identity.

### 4.3 Corpus coverage

All 42 halves resolve to at least one verb; none falls through.

| Verb | halves | | Recipient | halves |
|---|---:|---|---|---:|
| INSCRIBE | 19 | | TILE | 19 |
| INVOKE | 10 | | SELF | 14 |
| BIND | 9 | | ENEMY | 9 |
| MOVE | 7 | | ALLY | 0 |
| WARD | 2 | | | |
| STRIKE | 2 | | | |

35 halves carry one verb, 7 carry two, none carries three. **The distribution is
the taxonomy validating itself**: a school that is 45% INSCRIBE with two damage
halves is exactly what "the Namer" should look like. If STRIKE were the largest
bucket, the vocabulary would be wrong or the school would be.

### 4.4 Accepted losses

Stated explicitly so nobody re-litigates them:

- **Area of effect is not encoded.** `prepare_glyph_area` (4 halves) reads
  identically to `prepare_glyph`. AoE is an effect *shape*, orthogonal to both
  recipient and verb; encoding it needs a seventh spoke, which would break the
  six-fold interleave in §2.1.
- **Magnitude, duration, radius and trigger type are not encoded.** The tile
  decal shows what *kind* of thing is armed here, not its stat block.
- **`gain_weave` is deliberately ignored.** It rides on 9 of 42 halves as a
  universal resource kicker and carries no discriminating information.
- **Ambiguity in the function layer is by design.** The 42 halves produce only
  **16 distinct hub-and-spoke signatures**; 14 are `TILE · INSCRIBE`. That is
  correct for a tile decal: at a glance you want "that's a trap tile," not "that
  is specifically Tripwire." Identity lives in the stave, which is unique per
  card.

---

## 5. Extraction: card data → cipher inputs

Implemented in `GlyphCipherTags.cs`. Reads the **compiled `CardHalf`**, not the
JSON: `CardUpgradeApplier` rewrites halves at runtime, so the JSON is not live
truth. Unchanged from v1.

### 5.1 Recipient

From the concrete `ITargetSelector` on `CardHalf.Targeting`:

| Selector | Recipient |
|---|---|
| `null`, `SelectSelfTarget`, `SelectGlobalTarget` | SELF |
| `SelectUnitTarget` with `friendlyOnly` | ALLY |
| `SelectUnitTarget` otherwise | ENEMY |
| `SelectTwoStepTarget` (unit-then-tile, unit-then-direction) | ALLY if `friendlyOnly`, else ENEMY |
| everything else (tile, empty tile, aoe, ring, line, cone, element tile, nearest memorial) | TILE |

A unit selector that is neither friendly-only nor enemy-only resolves to ENEMY.
The only such card is Phase Shift; the reason to cast it is to move an enemy.

### 5.2 Verbs

Walk `CardHalf.Effects` and `IEffect.Children` recursively. **Read tags from
leaves only.**

| Tag | Verb |
|---|---|
| `Damage`, `SelfDamage` | STRIKE |
| `Control`, `Status`, `Debuff` | BIND |
| `Movement`, `Displace` | MOVE |
| `Defense`, `Heal`, `Buff`, `Summon` | WARD |
| `CardDraw`, `Mana`, `Foresight` | INVOKE |
| `Weave` | *ignored* |
| `Glyph` | INSCRIBE or INVOKE — see below |

`Glyph` is worn by both halves of the school's identity, so the concrete effect
type disambiguates:

- **INSCRIBE** — `PrepareGlyphEffect`, `EnchantPillarEffect`, `ReflectWardEffect`,
  `SpellAnchorEffect`
- **INVOKE** — everything else tagged `Glyph`: `LinkGlyphsEffect`,
  `RearmGlyphsEffect`, `TriggerAllGlyphsEffect`, `SwapGlyphsEffect`,
  `GrandDesignPassiveLeafEffect`

Untagged, mapped by type: `ScryEffect` → INVOKE.

### 5.3 Two pre-existing data smells this surfaced

Neither blocks the cipher; both are worth fixing on their own merits.

1. **`retarget` is registered `.WithTag("Damage")`** in `JsonCardLoader.cs`, but
   it is a *composite* wrapping an arbitrary child. In Dominion it wraps an
   `apply_status` that deals no damage. Any tag-driven statistic over card
   effects is currently wrong for that card. The cipher sidesteps it by reading
   leaves only.
2. **`scry` carries no tag** (`CardScriptRegistry.Arcanist.cs`). It should get
   `.WithTag("Foresight")`.

### 5.4 Localisation

The cipher encodes `cipherName`, defaulting to the half's English display name.
If names are ever translated, **glyphs must not mutate** — add a stable
`cipher_name` field to the card JSON and have `GlyphCipherTags.CipherNameOf` read
it in preference to `Name`. The jitter seed is independent of the name entirely
(§6), so a name change alters which crossbars appear but not the hand-inked
character of the glyph.

---

## 6. Determinism

In order of how badly each violation bites:

1. **The seed is `"{cardId}#{half}"`** — the stable JSON id, never the display
   name. Display names are localisable and get reworded during balance passes.
   This project already has a logged bug from exactly that distinction
   (`CardDatabase.GetByName` matching display names instead of ids).
2. **`string.GetHashCode` is never used.** .NET randomises string hashing per
   process. FNV-1a 32 over UTF-8 instead.
3. **The RNG is xorshift32**, not `System.Random`, whose output has changed
   across runtime versions.
4. **The order of RNG draws is part of the format.** Reordering two statements
   that both consume the stream changes every glyph downstream. Draw sites are
   numbered in `GlyphCipher.cs`; the self-test checksums the whole corpus.
5. **All arithmetic is `double`.** Cross-checked against a reference
   implementation at 1e-4 quantisation, which `float` cannot hold.

| key | FNV-1a 32 |
|---|---|
| `enchanter_snare_glyph#top` | `0x5A0AE8D2` |
| `enchanter_the_grand_design#bottom` | `0x160C0F13` |
| `enchanter_mana_tithe#bottom` | `0xE2B10B86` |

---

## 7. Colour

All colours through `UITheme`. No inline `new Color()` — project rule.

| Constant | Value | Role |
|---|---|---|
| `UITheme.CipherInk` | `#1A1614` | stave on card stock |
| `UITheme.CipherInkLight` | `#EDE4D3` | stave on the dark board |
| `UITheme.CipherFunction` | `#C45B9E` | hub and spokes (Enchanter border rose) |
| `UITheme.CipherPaper` | `#E8DFCE` | default punch colour for the ALLY hub |

### 7.1 Accessibility

Rose-on-black collapses toward one value under protanopia. Measured contrast:

| Pair | Normal | Protanopic (Viénot) |
|---|---:|---:|
| ink / rose | 4.57 : 1 | 3.79 : 1 |
| rose / paper | 2.97 : 1 | 3.58 : 1 |

Both clear the 3:1 non-text threshold, but **hue is the secondary channel.** The
function layer is drawn at 1.88× the stave's weight (0.032 vs 0.017 unit), and at
tile scale that widens to ~3×. Weight, plus the fact that the function layer is a
*hub and spokes* while the identity layer is *arms and ticks*, is what a
protanopic player actually reads. Shape carries the split even if colour fails
completely.

---

## 8. LOD composites

One generator, three composites.

| | identity α | backing α | function weight | min px identity | min px function | pips |
|---|---:|---:|---:|---:|---:|:--:|
| **Tile** (~64 px) | 0.85 | 0.62 | ×1.6 | 1.0 | 2.6 | no |
| **Card** (~180 px) | 1.00 | 0.00 | ×1.0 | 1.0 | 1.6 | no |
| **Inspection** (≥384 px) | 1.00 | 0.00 | ×1.0 | 1.0 | 1.6 | yes |

**The tile decal gets a backing disc** (`UITheme.CipherTileBacking`, scaled by the
profile's backing α) drawn under everything else. Without it the sigil composites
straight onto whatever terrain the tile happens to be — grass, sand, stone, water
— and *no* single ink alpha is legible across all of them. A controlled backdrop
makes the composite deterministic, and reads as a rune scorched into the ground.

The stave's tile alpha was **0.30 in the first cut and that was wrong.** It was
tuned against controlled backdrops (paper in the contact sheet, a flat dark swatch
in the LOD mock) and fails outright on a real board: pale ink at 30% over bright
grass has almost no contrast, so the sigil reads as a floating hub with nothing
attached to it. Foregrounding the function layer is now carried entirely by
**weight** — ~3× the stave's width at this LOD — which is terrain-independent,
rather than by making the stave faint.

**Stroke weights are unit widths multiplied by the render RADIUS**, not diameter.

**The pixel floors are not cosmetic.** At a 64 px tile the function layer's
linear weight is `0.032 × 32 = 1.0 px` — the one layer carrying gameplay
information would be a hairline. Floors are inert at card scale and above.

Pips at inspection zoom are faint open circles on the *unused* spoke bearings, so
a player can learn the ring positions from a single glyph.

### 8.1 Draw-on

`GlyphCipherView.Progress` (0→1 over ~0.4 s on glyph prepare) reveals the ordered
strokes: arms grow outward in reading order, each followed by its crossbars and
ornament, then the function spokes. The rim is drawn immediately. The start disc
appears at once; other markers at 50%; **the hub only at the very end, so the
sigil visibly seals.**

Note this is a *reveal*, not a single continuous stroke — v1's one-unbroken-line
property did not survive the change of mark language, and a radial stave cannot
have it. The animation is better for it: growth reads as inscription.

---

## 9. Integration seams

### 9.1 Card art — ready

`CardUi.ShowFullCard(CardHalf half, bool isTop)` has everything:
`CardInstance.BlueprintId` gives the card id, `isTop` the half, `_artPanel` the
surface. See `glyph_cipher_patches_v2.md`.

### 9.2 Hex-tile decal — a flat ground rune

The decal is a **`MeshInstance3D` + `QuadMesh` tipped -90° about X**, so it lies
face-up on the ground plane. Not a billboard: this is a sigil sketched on the
earth waiting for a trigger, and it must not turn to watch the camera.

Height comes from `HexTile.MeasuredTileTopY()`, which reads the HexMesh's AABB
rather than assuming a Y — the same technique `ImbuementOverlay` uses, and for
the same reason: it survives Hex_mesh scale changes, blocker variants and
`SetHeight` adjustments instead of silently sinking into or floating above them.
Clearance above that surface is `GlyphDecalHeight` (0.055).

`Assets/Shaders/glyph_sigil.gdshader` supplies what a static texture cannot:

- **Halo** — an eight-tap ring blur of the sigil's own alpha, squared to pull the
  bloom back against the strokes rather than fogging the disc, tinted by
  `UITheme.CipherFunction`.
- **Breathing** — a slow pulse on the halo, with a per-tile `phase` derived from
  the axial coords so a field of prepared glyphs does not throb in unison. Derived
  from coordinates rather than randomised, so it is stable across saves.
- **Inscription sweep** — `progress` 0→1 over 0.55 s when the glyph is prepared,
  revealing clockwise from the top. Because the shader's θ convention matches the
  cipher's exactly (§2), the sweep reveals **arm 0 first and walks round in
  reading order for free**, with the hub landing last so the sigil visibly seals.
- **Slow spin**, off by default at 0.045 rad/s.

It is `blend_mix`, not `blend_add` like its sibling `imbuement_glyph.gdshader`:
this sigil carries a dark backing disc so it stays legible over grass, sand,
stone or water, and additive blending would erase exactly that. The glow is
reconstructed in-shader instead.

The bake is 256 px rather than 128 — the sigil now lies where the camera can get
close to it, and blurring its alpha for the halo magnifies any softness.

#### Source identity (resolved)



**The tile visual today is a `Label3D` showing "✦"** (`HexTile.ShowGlyph`,
billboarded, at y = 0.6). The board is 3D, so the decal is a `Sprite3D` with
`Billboard.Enabled`, textured from `GlyphCipherTexture` — not a 2D overlay.

The blocker is upstream of rendering: **`GlyphData` carries no reference to the
card that created it.** It has `OwnerId` (a *unit* name), `OwnerTeam`, `Owner`,
`GameState` — nothing identifying which spell armed the tile, so `ShowGlyph`
cannot know which glyph to draw.

The seam is short and half-built. `RulesManager.TryCastWithTargets` already pins
`GameState.CostContextCard` for the duration of a cast (set at
`RulesManager.cs:266`, cleared at `:407`), so the *card* is reachable from inside
`PrepareGlyphEffect.Resolve`. The *half* is not. Minimal plumbing:

1. `CardHalf` gains `SourceCardId` and `SourceHalf`, stamped by `JsonCardLoader`
   when it compiles each half — it knows both at that point.
2. `GameState` gains `CostContextHalf`, pinned and cleared alongside
   `CostContextCard` in `RulesManager`.
3. `GlyphData` gains `SourceCardId` / `SourceHalf`; `PrepareGlyphEffect.Configure`
   copies them from `s.CostContextHalf`.
4. `HexTile.ShowGlyph` calls
   `GlyphCipherTexture.Instance.RequestAsync(glyph.SourceCardId, glyph.SourceHalf, …)`
   and assigns the result to a billboarded `Sprite3D`.

Steps 1–3 touch `RulesManager.cs` and `JsonCardLoader.cs`, which were not read
line-by-line for this pass. **They are specified here rather than patched**, per
the working convention of never regenerating an unconfirmed live file. Card art
and the inspection zoom are complete and independent of this.

### 9.3 `GlyphCipherTexture` lifetime

Autoload named `GlyphCipherTexture`, or a child of the combat root before any
tile requests a glyph. A bake costs two frames (a `SubViewport` needs one to lay
out and one to resolve; one frame yields an empty texture on some drivers).
Cached per `(cardId, half, px, lod, dark)`; concurrent requests for a key share
one bake.

---

## 10. Acceptance tests

`godot --headless -- --verify-cipher` (exit 1 on failure), or **F10** in a debug
build. Mirrors `CardVerifier`'s shape.

### 10.1 Invariants — hold for any input, keep working as cards are added

| | Assertion |
|---|---|
| **INV-1** | `CrossbarCount == Letters.Length` — every letter gets exactly one crossbar |
| **INV-2** | Arm layout matches `ArmLayout(n)`, covers every letter, and fits in six arms |
| **INV-3** | No point escapes the rim (tolerance 1.01 for rim wobble) |
| **INV-4** | Exactly one start marker, one terminal marker, one hub; retrace marks match the count |
| **INV-5** | One spoke tip per verb |
| **INV-6** | Reveal indices are dense and unique over `0..OrderedCount-1` — no gaps, nothing drawn twice |
| **INV-7** | No spoke bearing coincides with an arm bearing |

Plus: two builds in one process are identical; different card ids and different
halves produce different glyphs; degenerate names (`"A"`, `"  spaced  out  "`,
`"O'Keeffe's Ward"`, over-length) do not crash; a name with no A–Z letters throws;
and **every registered Enchanter half in the live `CardDatabase` extracts at
least one verb** — a verb-less half means an effect is missing a `.WithTag(...)`
at its registration site, the same bug class as an unregistered effect key.

### 10.2 Goldens

All 42 halves, with expected letters, arm count, depth, crossbar count, retrace
count, stroke count, and a geometry checksum (FNV-1a 32 over the stroke set at
1e-4 quantisation). Aggregate: **`0xEF7EE845`**.

Worked decodes:

**Snare Glyph** — `enchanter_snare_glyph#top`, seed `0x5A0AE8D2`
```
letters     SNAREGLYPH (10)
arms        5 × depth 2       layout [2,2,2,2,2]
  arm 0 (θ  0)  S(11,common)  N(8,common)
  arm 1 (θ 60)  A(1,common)   R(10,common)
  arm 2 (θ120)  E(4,common)   G(3,rare)
  arm 3 (θ180)  L(7,common)   Y(12,rare)
  arm 4 (θ240)  P(7,rare)     H(5,common)
recipient   TILE (diamond hub)      verbs  INSCRIBE (spoke at θ150)
crossbars   10    retraces 0    strokes 27    ordered 21
checksum    0x5B6176D1
```

**Absolute Territory** — `enchanter_the_grand_design#bottom`, seed `0x160C0F13`
```
letters     ABSOLUTETERRITORY (17)   — the longest in the corpus
arms        6 × depth 3       layout [3,3,3,3,3,2]
recipient   SELF (disc hub)          verbs  BIND (spoke at θ270)
crossbars   17    retraces 1 (RR)    strokes 35    ordered 27
checksum    0x2C91D76D
```

**Sap** — `enchanter_mana_tithe#bottom`, seed `0xE2B10B86`
```
letters     SAP (3)                  — the shortest in the corpus
arms        3 × depth 1       layout [1,1,1]
recipient   ENEMY (triangle hub)     verbs  BIND, STRIKE (θ270, θ330)
crossbars   3     retraces 0         strokes 18     ordered 10
checksum    0xB9D7006B
```

### 10.3 Verification status, honestly stated

The grammar, geometry and golden values were produced and checked by a Python
reference implementation of this specification, run against the live 42-half
corpus. Every invariant above passes there, and the contact sheet was rendered
from it.

**`GlyphCipher.cs` itself has not been compiled or executed** — no C# toolchain
was reachable in the environment where it was written (both apt mirrors returned
403; the device VM has no dotnet). It is a careful transliteration of the
verified reference, and `GlyphCipherSelfTest` exists precisely to close that gap:
the first `--verify-cipher` run is the real gate. **If a checksum fails on that
run, the most likely cause is an RNG draw-order divergence** — check the numbered
draw-site comments in `GlyphCipher.cs` against §3.

---

## 11. Not in v2

- Other schools. The Namer fiction is what keeps the semantic vocabulary honest.
- A `WeaveTier` ring around the tile decal. Worth doing — board control state
  should be legible even when the cipher is not — but it is a separate visual
  system that happens to share a circle.
- AoE, magnitude, duration, trigger type (§4.4).
- Upgraded-card variants. `Binding Rune+` renders the base glyph, since the
  cipher is seeded and named from the base half. Whether an upgrade should
  visibly alter the sigil is a design question, not a technical one — the obvious
  move is an extra ring inside the rim per upgrade tier, which costs nothing in
  the encoding.
