> **STATUS: INSTALLED.** Everything in §2 has been applied to this repo — the six
> new files are in place, all twelve live-file edits landed, and
> `GlyphCipherTexture` is registered as an autoload in `project.godot`. What
> remains is the part that needs a C# toolchain, which the installing session did
> not have:
>
> ```
> dotnet build FracturedArcana.sln
> godot --headless -- --verify-cipher      # expect exit 0, aggregate 0xEF7EE845
> ```
>
> The Python reference implementation has already been run against this repo's
> own `Data/Cards` and reproduces that aggregate exactly, so the golden table is
> confirmed correct. What is still unverified is only whether `GlyphCipher.cs`
> transliterates it faithfully — that is what the headless run answers.
>
> `_to_delete/_cipher_payload.tgz` is a leftover transfer artifact; the mount
> would not let the installing session remove it. Delete that folder.

# Glyph Cipher — building it into the game

Companion to `glyph_cipher_spec_v2.md` (the grammar) and
`glyph_cipher_patches_v2.md` (the exact find/replace blocks). This document is
the order of operations and the things that will bite.

---

## 1. How the shipping renderer differs from the contact sheet

Worth reading before you look at anything on screen, because you *will* spot
differences and it is useful to know which ones are expected.

**The generator is the same. The renderer is not.**

| | Contact sheet | In-game |
|---|---|---|
| stroke generator | `tools/glyph_cipher/glyph_cipher_ref.py` | `GlyphCipher.cs` |
| renderer | Python → SVG → Chromium | `GlyphCipherView._Draw` → Godot |

The two generators are line-for-line mirrors and `GlyphCipherSelfTest` proves it
with a geometry checksum over all 42 halves. **Every vertex is identical.** If
the shapes differ, the self-test has already failed and you have a real bug.

The two *renderers* are independent implementations against different drawing
APIs, and that is where honest differences live. Three of them mattered enough to
compensate for in code:

**Line caps.** Godot's `DrawPolyline` has no cap or joint control at all — every
stroke ends in a flat butt cap. The reference used SVG's
`stroke-linecap="round"`. On a stave this is not subtle: arms and crossbars are
short strokes, and flat ends make the ink read as machined. `DrawStrokePolyline`
now caps by hand with an antialiased disc at each end.

**Circle antialiasing.** `DrawCircle`'s `antialiased` parameter *defaults to
false*. A jaggy 4 px dot next to antialiased strokes is very visible. Every disc
in the view now passes it explicitly.

**Polygon antialiasing.** `DrawColoredPolygon` has no antialiasing parameter at
all. That is fine on a large shape and not fine on the hub, which is the
most-read mark on the glyph and often only ~20 px across. `FillPolygonSmooth`
fills the polygon and then strokes its outline with a thin antialiased polyline
in the same colour to feather the edge.

Two differences remain and are accepted:

- **Joint mitring.** Godot mitres polyline joints; SVG rounded them. Every
  polyline here is a jittered straight run, so no joint bends far enough to show.
- **Marker draw order.** The reference draws all strokes, then all markers. The
  view draws in stroke-array order, so a marker lands immediately after the
  stroke it belongs to. Only observable where a marker overlaps a later stroke,
  which the geometry avoids.

Everything else — radii, weights, jitter, ornaments, hub shapes, LOD alphas and
the pixel floors — comes from shared constants and matches by construction.

---

## 2. Order of operations

Do these in order. Steps 1–4 get you a verified generator with nothing on screen;
step 6 is the first time you see anything.

### Step 1 — `UITheme.cs` first

Apply patch §1 from `glyph_cipher_patches_v2.md`. It adds four colours.

**Nothing else compiles until this lands** — `GlyphCipherView` references
`UITheme.CipherInk`, `CipherInkLight`, `CipherFunction` and `CipherPaper`. If you
drop the new files in first you will get four `CS0117` errors and it will look
like the new code is broken when it is not.

### Step 2 — drop in the new files

```
Scripts/Systems/Combat/Glyphs/GlyphCipher.cs
Scripts/Systems/Combat/Glyphs/GlyphCipherTags.cs
Scripts/Systems/Combat/Glyphs/GlyphCipherTexture.cs
Scripts/UI/GlyphCipherView.cs
Scripts/Dev/GlyphCipherSelfTest.cs
Scripts/Dev/GlyphCipherGallery.cs
```

No project-file edit needed: `FracturedArcana.csproj` is SDK-style
(`Godot.NET.Sdk/4.6.2`) and globs `**/*.cs`. Do not hand-write `.cs.uid` files —
the editor generates them on import, and a hand-made one with a colliding GUID
causes a genuinely confusing load failure.

All six files are in the global namespace, matching the rest of the project.
`RootNamespace` in the csproj says `WizardCardGame` but no existing file declares
a namespace, so these do not either.

### Step 3 — `GameBootstrap.cs`

Apply patch §2. Adds `--verify-cipher`, F10 (self-test) and F11 (gallery).

### Step 4 — build

```bash
dotnet build FracturedArcana.sln
```

or the editor's Build button. Expect the two pre-existing warnings
(`PainterlyGrassTuner` CS0108, `FogOfWarManager` CS0162) and no new ones.

### Step 5 — run the gate. Do this before looking at anything.

```bash
godot --headless -- --verify-cipher
echo $?        # 0 = pass, 1 = fail
```

This is not optional ceremony. **`GlyphCipher.cs` has never been compiled or
executed** — no C# toolchain was reachable where it was written, so it is a
careful transliteration of a verified reference and nothing more. This run is the
first real evidence it is correct.

You should see:

```
── GlyphCipher self-test (spec v2, radial stave) ──
  goldens        : 42
  live Enchanter : 42 halves, 0 failed
  aggregate      : 0xEF7EE845 (expected 0xEF7EE845)
  failures       : 0
```

**If a checksum fails**, the generator's output changed. In descending order of
likelihood:

1. **An RNG draw got reordered.** The output depends on the *order* of draws from
   the stream, which no compiler checks. The draw sites are numbered `DRAW SITE
   1`…`6` in `GlyphCipher.cs`; walk them against spec §3 and against
   `tools/glyph_cipher/glyph_cipher_ref.py`, which is the same algorithm in
   Python.
2. **A constant was edited.** Compare the `const` block against spec §2–3.
3. **`float` crept in somewhere.** Every intermediate must be `double`; the
   checksum quantises at 1e-4 and `float` cannot hold that across a full stave.

Structural failures (`letters`, `arms`, `crossbars`) point at `Normalise`,
`ArmLayout` or `TryLetterSlot` rather than the RNG.

**If a `LIVE` line fails** with *"no verb extracted"*, that half's effect is
missing a `.WithTag(...)` at its registration site. That is the same bug class as
an unregistered effect key — the card silently does nothing visible. Fix the tag,
not the cipher.

### Step 6 — look at it: F11

Run a debug build and press **F11**. Every Enchanter half, drawn through the real
`_Draw` path, in a grid.

- **L** cycles LOD (Card → Tile → Inspection)
- **D** toggles dark board vs card stock
- **R** replays the 0.4 s draw-on
- **Esc** closes

Put `docs/glyph_cipher_sheet.png` next to it. Shapes must match exactly; the only
differences you should see are the cap/antialias ones in §1.

### Step 7 — `CardUi.cs`

Apply patch §3. This is the only patch whose effect is visual, which is why it
goes last — by the time you see it on a card you already trust the generator and
the renderer.

Hover any Enchanter card. The sigil draws over the art panel, `DarkBackground`
on, `PaperColor` matched to the panel fill so the ALLY hub's punch is invisible
against it.

---

## 3. What is *not* wired, and why

**The hex-tile decal.** `GlyphCipherTexture.cs` ships complete and unused.

The blocker is not rendering. **`GlyphData` carries no reference to the card that
created it** — it has `OwnerId` (a *unit* name), `OwnerTeam`, `Owner` and
`GameState`, none of which say which spell armed the tile. `HexTile.ShowGlyph`
therefore cannot know which glyph to draw. (Separately: the current tile visual is
a `Label3D` showing `✦` at y = 0.6, so the replacement is a billboarded
`Sprite3D`, not a 2D overlay — the board is 3D.)

Four steps, specified in spec §9.2:

1. `CardHalf` gains `SourceCardId` / `SourceHalf`, stamped by `JsonCardLoader` as
   it compiles each half.
2. `GameState` gains `CostContextHalf`, pinned and cleared beside
   `CostContextCard` in `RulesManager` (set `:266`, cleared `:407`).
3. `GlyphData` gains the same two fields; `PrepareGlyphEffect.Configure` copies
   them from `s.CostContextHalf`.
4. `HexTile.ShowGlyph` calls `GlyphCipherTexture.Instance.RequestAsync(...)` and
   assigns the result to a `Sprite3D`.

Steps 1–3 touch `RulesManager.cs` and `JsonCardLoader.cs`. Neither was read
line-by-line, so no patch is offered — writing a find/replace against an
unconfirmed region of a large live file is the failure mode the working
convention exists to prevent. Paste those two (or just `RulesManager.cs`
~260–410 and the half-compilation region of `JsonCardLoader.cs`) and the patches
follow immediately.

When you do wire it, `GlyphCipherTexture` needs to be in the tree: add it as an
autoload named `GlyphCipherTexture` (Project Settings → Autoload), or as a child
of the combat root before any tile asks for a glyph. Without an instance,
`RequestAsync` is a no-op and tiles keep whatever placeholder they had. A cold
bake costs two frames — a `SubViewport` needs one to lay out and one to resolve,
and one frame yields an empty texture on some drivers. Results cache per
`(cardId, half, px, lod, dark)` and concurrent requests for a key share a bake.

---

## 4. Changing the design later

**Anything that changes stroke geometry invalidates the goldens.** That is
intended — it is the mechanism that stops silent drift. The loop is:

1. Edit `tools/glyph_cipher/glyph_cipher_ref.py`.
2. `python3 verify.py` → must print `ALL ASSERTIONS PASS`.
3. `python3 render2.py` and look at the sheet.
4. Mirror the change into `GlyphCipher.cs`, statement for statement, watching
   draw order.
5. `python3 goldens.py` → paste the table and the new aggregate into
   `GlyphCipherSelfTest.cs`.
6. Bump the spec version and say what changed.

Skipping step 5 leaves the self-test asserting the old grammar, which will fail
loudly — that is the system working. Skipping step 4 is the dangerous one: the
sheet and the game silently disagree and nothing catches it.

Cheap changes that need none of this: LOD alphas and pixel floors, colours, cap
and antialias handling. Those live in `GlyphCipherView` / `UITheme` and touch no
geometry.

---

## 5. Quick reference

| | |
|---|---|
| headless gate | `godot --headless -- --verify-cipher` |
| self-test in editor | **F10** |
| gallery | **F11** |
| card art | hover any Enchanter card after patch §3 |
| aggregate checksum | `0xEF7EE845` |
| reference impl | `tools/glyph_cipher/` |
| grammar | `docs/glyph_cipher_spec_v2.md` |
| patches | `docs/glyph_cipher_patches_v2.md` |
