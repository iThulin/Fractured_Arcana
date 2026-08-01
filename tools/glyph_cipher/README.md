# Glyph cipher reference implementation (spec v2)

Python mirror of `Scripts/Systems/Combat/Glyphs/GlyphCipher.cs`. Not shipped with
the game — this is the tool that produced the spec's tables, the golden checksums
in `GlyphCipherSelfTest.cs`, and the contact sheets in `docs/`.

It exists because the C# generator's correctness is invisible to the compiler:
the output depends on the *order* of RNG draws, and a reordered statement
silently changes every glyph in the game. A second implementation of the same
grammar lets the invariants be checked and the goldens regenerated without
launching the engine.

**Any change to `GlyphCipher.cs` must be mirrored here, or the goldens are lying.**

## Files

| | |
|---|---|
| `glyph_cipher_ref.py` | the generator |
| `corpus.py` | reads `Data/Cards/enchanter_*.json` and applies the spec §5 extraction rules |
| `verify.py` | invariants INV-1..7 plus determinism and edge cases, over all 42 halves |
| `goldens.py` | regenerates the golden table for the C# self-test |
| `render_sheet.py` | contact sheet, rendered from `glyph_cipher_ref.build()` so it cannot drift from the grammar the goldens assert |
| `v2_goldens.txt` | the current golden table, as pasted into the self-test |

## Use

```bash
python3 verify.py       # must print ALL ASSERTIONS PASS
python3 goldens.py      # only when the grammar intentionally changed
python3 render_sheet.py # writes v2_sheet.svg
```

`corpus.py` expects the card JSONs at the path in its `load()` default; point it
at `Data/Cards` in the repo.

## Mark languages that were tried and rejected

Recorded so they are not retried from scratch — see spec v2 §1:

- **chords across the interior (v1)** — a random walk on a ring. Reads as
  scribble; no parameter fixes it.
- **single vertical stave with branches** — reads as designed, but every name
  becomes the same feather. A comb on a straight line has no silhouette variation.
- **circular ogham inscription** — good, and best-in-class at tile scale, but a
  ring of tick groups registers as a gauge rather than a rune. Worth revisiting
  if the decal ever has to work at 32px.
