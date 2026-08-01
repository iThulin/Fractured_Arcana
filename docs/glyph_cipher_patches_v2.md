# Glyph Cipher — live-file patches (spec v2)

Strict one-match find/replace. Each ANCHOR below appears exactly once in the file
as it stands on disk at the time of writing; verify the match count before
applying. New files are drop-in and need no patching.

---

## 1. `Scripts/UI/UITheme.cs` — cipher colours

The project rule is that no colour is constructed inline outside this file, so
the three cipher colours live here.

**FIND** (currently at ~line 155, in the TILE HIGHLIGHTS block):

```csharp
    public static readonly Color TileGlyph = new Color(0.65f, 0.25f, 1.00f, 1f);
```

**REPLACE WITH:**

```csharp
    public static readonly Color TileGlyph = new Color(0.65f, 0.25f, 1.00f, 1f);

    // ════════════════════════════════════════════════════════════
    // GLYPH CIPHER (docs/glyph_cipher_spec_v2.md §7)
    // Two layers that must stay separable for a protanopic player.
    // Hue is the SECONDARY channel: the function layer is drawn at
    // 1.88x the identity layer's stroke weight (~3x at tile scale),
    // and it is a hub-and-spokes where the identity layer is
    // arms-and-ticks — so shape carries the split even if colour
    // fails entirely. Measured contrast ink/function: 4.57:1 normal,
    // 3.79:1 protanopic.
    // ════════════════════════════════════════════════════════════
    /// <summary>Identity layer (the encoded Name) on card stock.</summary>
    public static readonly Color CipherInk = new Color(0.102f, 0.086f, 0.078f, 1f);   // #1A1614

    /// <summary>Identity layer over the dark combat board.</summary>
    public static readonly Color CipherInkLight = new Color(0.929f, 0.894f, 0.827f, 1f); // #EDE4D3

    /// <summary>Function layer (hub + spokes). The Enchanter border rose.</summary>
    public static readonly Color CipherFunction = new Color(0.769f, 0.357f, 0.620f, 1f); // #C45B9E

    /// <summary>Default punch colour for the ALLY hub, which is a filled disc with its
    /// centre removed. Callers drawing over a different background should set
    /// GlyphCipherView.PaperColor to match it.</summary>
    public static readonly Color CipherPaper = new Color(0.910f, 0.875f, 0.808f, 1f);    // #E8DFCE
```

---

## 2. `Scripts/Systems/GameBootstrap.cs` — verification entry points

Adds `--verify-cipher` alongside `--verify-cards`, and F10 alongside F9, so the
cipher has the same headless gate the card data already has.

**FIND:**

```csharp
        // Headless verification: `godot --headless -- --verify-cards`
        // Exit code 1 on any card error, so a script can gate on it.
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--verify-cards")
            {
                bool ok = CardVerifier.RunAndReport();
                GetTree().Quit(ok ? 0 : 1);
                return;
            }
        }
    }
```

**REPLACE WITH:**

```csharp
        // Headless verification: `godot --headless -- --verify-cards`
        // Exit code 1 on any card error, so a script can gate on it.
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--verify-cards")
            {
                bool ok = CardVerifier.RunAndReport();
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            // `godot --headless -- --verify-cipher`
            // Glyph cipher goldens + invariants. Runs after the card
            // database is loaded so it can check the live Enchanter
            // corpus, not just the baked-in goldens.
            if (arg == "--verify-cipher")
            {
                bool ok = GlyphCipherSelfTest.RunAndReport();
                GetTree().Quit(ok ? 0 : 1);
                return;
            }
        }
    }
```

**FIND:**

```csharp
        // F9 in a debug build: run the card verification pass on demand.
        if (OS.IsDebugBuild()
            && e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F9)
        {
            CardVerifier.RunAndReport();
        }
```

**REPLACE WITH:**

```csharp
        // F9 in a debug build: run the card verification pass on demand.
        if (OS.IsDebugBuild()
            && e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F9)
        {
            CardVerifier.RunAndReport();
        }

        // F10: the glyph cipher's equivalent pass.
        if (OS.IsDebugBuild()
            && e is InputEventKey k2 && k2.Pressed && !k2.Echo && k2.Keycode == Key.F10)
        {
            GlyphCipherSelfTest.RunAndReport();
        }

        // F11: the glyph gallery — every Enchanter half drawn through the
        // shipping renderer, for comparison against the reference contact
        // sheet in docs/glyph_cipher_sheet.png.
        if (OS.IsDebugBuild()
            && e is InputEventKey k3 && k3.Pressed && !k3.Echo && k3.Keycode == Key.F11)
        {
            GlyphCipherGallery.Toggle(GetTree().Root);
        }
```

---

## 3. `Scripts/Cards/CardUi.cs` — glyph on the card art panel

`ShowFullCard` already has everything the cipher needs: `CardInstance.BlueprintId`
is the stable card id and `isTop` is the half. The view is created lazily as a
child of `_artPanel` and reused.

### 3a. Field

**FIND:**

```csharp
    private Panel _artPanel;
```

**REPLACE WITH:**

```csharp
    private Panel _artPanel;

    // Cipher sigil drawn over the art panel. Created lazily on first
    // full-card hover; reused thereafter. See docs/glyph_cipher_spec_v1.md.
    private GlyphCipherView _cipherView;
```

### 3b. Populate

**FIND** (inside `ShowFullCard`, the art-panel placeholder block):

```csharp
            artStyle.BorderWidthBottom = 0;
            _artPanel.AddThemeStyleboxOverride("panel", artStyle);
        }
```

**REPLACE WITH:**

```csharp
            artStyle.BorderWidthBottom = 0;
            _artPanel.AddThemeStyleboxOverride("panel", artStyle);

            // Enchanter sigil. Every half of every Enchanter card has one and
            // they are generated, not authored — so this is the card art for
            // the whole school. Other schools fall through and keep the plain
            // tinted panel.
            if (school == CardSchool.Enchanter)
            {
                if (_cipherView == null)
                {
                    _cipherView = new GlyphCipherView
                    {
                        Name = "CipherGlyph",
                        Lod = CipherLod.Card,
                        DarkBackground = true,
                        MouseFilter = MouseFilterEnum.Ignore,
                    };
                    // The ALLY hub punches its centre out, so the punch has to
                    // match the art panel behind it.
                    _cipherView.PaperColor = artStyle.BgColor;
                    _cipherView.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                    _artPanel.AddChild(_cipherView);
                }

                string cardId = CardInstance?.BlueprintId;
                _cipherView.Visible = !string.IsNullOrEmpty(cardId)
                    && _cipherView.SetSpell(cardId, isTop ? "top" : "bottom", half);
            }
            else if (_cipherView != null)
            {
                _cipherView.Visible = false;
            }
        }
```

> `DarkBackground = true` because the art panel is filled at 35% of the school
> border colour — dark stock, so the identity layer uses `CipherInkLight`.

---

## 4. Not patched — specified only

`docs/glyph_cipher_spec_v2.md` §9.2 specifies the four-step plumbing the hex-tile
decal needs (`CardHalf.SourceCardId`/`SourceHalf`, `GameState.CostContextHalf`,
`GlyphData.SourceCardId`/`SourceHalf`, then the `Label3D` → `Sprite3D` swap in
`HexTile.ShowGlyph`).

Steps 1–3 touch `RulesManager.cs` and `JsonCardLoader.cs`. Neither was read
line-by-line for this pass, so no patch is offered for them — writing a
find/replace against an unconfirmed region of a large live file is exactly the
failure mode the working convention exists to prevent. Paste those two files (or
the `TryCastWithTargets` pin region, `RulesManager.cs` ~260–410, and the
half-compilation region of `JsonCardLoader.cs`) and the patches follow
immediately.

---

## 5. Apply order

1. `UITheme.cs` — nothing compiles without the three colours.
2. Drop in the new files:
   - `Scripts/Systems/Combat/Glyphs/GlyphCipher.cs`
   - `Scripts/Systems/Combat/Glyphs/GlyphCipherTags.cs`
   - `Scripts/Systems/Combat/Glyphs/GlyphCipherTexture.cs`
   - `Scripts/UI/GlyphCipherView.cs`
   - `Scripts/Dev/GlyphCipherSelfTest.cs`
   - `Scripts/Dev/GlyphCipherGallery.cs`
3. `GameBootstrap.cs`.
4. Build. **Then run `godot --headless -- --verify-cipher` before looking at
   anything on screen.** If the generator is wrong the renderer will draw the
   wrong thing very convincingly.
5. `CardUi.cs` last — it is the only patch whose effect is visual, so it is the
   one you want to be able to trust by the time you see it.

`GlyphCipherTexture` only needs to be in the tree for the tile decal (§10.2, not
yet wired). Card art and the inspection zoom use `GlyphCipherView` directly and
need no autoload.

---

## 6. Deleting v1

`docs/glyph_cipher_spec_v1.md` is superseded and should be deleted, not kept
alongside. Its grammar is not a variant of v2's — the chord language it specifies
produces the scribble v2 exists to replace, and leaving it in the docs folder
invites someone to implement the wrong one.

The two intermediate mark languages that were tried and rejected (a single
vertical stave, and a circular ogham rim inscription) are recorded in
`glyph_cipher_spec_v2.md` §1 so they are not retried from scratch.
