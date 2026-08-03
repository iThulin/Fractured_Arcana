# Stone pillar — how to build it

**Status:** recommendation. Nothing implemented.
**Subject:** `stone_pillar` / `boulder` summons (`CombatManager.OnSummonRequested`,
currently `DummyUnitScene`, HP 12, ARM 5, SPD 0)
**Companion to:** `ImbuementRocks.cs`, `ImbuementOverlay.cs`

---

## 1. The counterargument first

The obvious answer — **sculpt a custom pillar mesh with authored damage states** —
is the one I would not take, and the reason is not cost.

It is that **a hand-made pillar is the one rock on the board that cannot follow
the map.** Every other stone — the ambient scatter, the imbuement boulders, the
debris — comes out of `HexGridManager`'s pool and inherits whatever the scene
assigns. Swap `MountainRockMeshes` (as happened mid-session) and they all change
together; the sculpted pillar does not. It becomes the object that looks right on
grassland and wrong everywhere else, and every new damage state doubles that
problem.

That is the same trap the hardcoded `res://` paths in `ImbuementRocks` fell into,
and it cost a round to find.

So: **build the pillar out of the pool.**

---

## 2. Recommendation

**A spire core with a rubble skirt, all from the existing mountain pool.**

- **Core:** `Boulder_Spire.obj` — already in `MountainRockMeshes`. Tall, clean
  silhouette, unmistakably a pillar rather than a heap.
- **Skirt:** 3–4 smaller boulders from the same pool wedged around its base,
  placed with the golden-angle scatter `ImbuementRocks` already uses.
- **Material:** `MountainRockMaterial`, read from the grid. Same as everything
  else, so it changes when the map does.

Zero new art. Biome-correct by construction.

### Damage

Map HP to **bands, not points** — a 1-damage tick must not make the object
flicker. At HP 12 with 4 bands, each band is 3 damage:

| band | state |
|---|---|
| 12–10 | intact: spire + full skirt |
| 9–7 | one skirt piece knocked off |
| 6–4 | skirt reduced to one piece; spire swaps to `Boulder_Wedge` (shorter, blunter) |
| 3–1 | spire swaps to `Boulder_Low`; the thing is a stump |
| 0 | everything collapses into the pile |

The spire swap is what sells it. Because the pool is a *set of related rocks*,
"damaged" can be spelled as "a shorter rock from the same family" rather than as
a new asset. Three states cost three array indices.

### Where the pieces go

Every piece removed by damage should **fall onto the tile's rubble**, not vanish.
The tile is already Earth-imbued — the summon does that (`SummonEffect.SpawnImbues`)
— so `ImbuementRocks`' scatter is already standing there. A knocked-off piece
becomes another debris instance in it, with a short tumble.

That closes a loop worth having: **the pillar's death feeds the ground it came
out of.** By the time it dies the tile is visibly a pile of its remains, and the
Earth imbuement it created is still there and still consumable.

---

## 3. The fallback, if the spire reads wrong

**Stack 4 boulders vertically**, scale shrinking with height, seeded per unit.
Damage removes them top-down — one band, one stone, and the stack visibly
shortens.

This is mechanically cleaner (damage state IS stone count, no swap table) and
worse-looking: a stack of rounded boulders reads as a **cairn**, not a pillar,
and looks balanced rather than erupted. Take it only if `Boulder_Spire` at
pillar scale looks too tidy next to the rubble.

Do not take the custom mesh unless both fail.

---

## 4. Architecture

**One new file, `Scripts/Systems/Combat/Core/StonePillarVisual.cs`**, a child of
the summoned unit that hides `DummyUnitScene`'s capsule and draws the rock form
instead.

A child, **not a new scene**: the unit carries collision, a health bar anchor,
selection and threat plumbing that all live on the dummy scene. Replacing the
scene means re-deriving all of it; hiding one mesh means none of it.

Hook the damage bands to `Unit.RefreshHealthBar()` — it is already called on
every path that changes HP (six call sites in `Unit.cs`), so there is no new
event to add and no path that can silently miss.

### Extract the rise profile first

The pillar wants the same **strain → break through → bed down** it already has:
a spire being forced out of the ground is the same beat as a boulder, only
slower and taller.

That profile currently lives inline in `ImbuementOverlay.ApplyRise`. Move it to
`ImbuementRocks` as a pure function

```
static float RiseOffset(float u, float depth, float rumbleFraction, float overshoot)
```

and have both call it. **Do this before writing the pillar, not after** — the
alternative is two copies that drift, and the sequel to that is a pillar that
erupts on a slightly different curve from the stones around it, which is exactly
the kind of thing nobody can name but everybody sees.

---

## 5. Order

1. Extract `RiseOffset` into `ImbuementRocks`; `ImbuementOverlay` calls it.
   No behaviour change — verify by eye that nothing moved.
2. `StonePillarVisual` with the intact state only. Spire + skirt, no damage, no
   animation. Confirms the silhouette before anything is built on it.
3. Wire the rise, reusing `RiseOffset` with a longer duration.
4. Damage bands: skirt loss first (cheapest, most visible).
5. Spire swaps.
6. Death collapse into the rubble.

Steps 1–2 answer "does this look like a pillar" for the cost of an afternoon,
and everything after is additive. Step 5 is the first one that can look worse
than what it replaces.
