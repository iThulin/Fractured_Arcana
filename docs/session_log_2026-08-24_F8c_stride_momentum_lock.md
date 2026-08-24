# Session log — 2026-08-24 — Stride Orders F8c: momentum + Grimoire lock

Closes out Stride Orders (§3.4): the momentum commitment bonus, the Grimoire
sealing while striding, and the ambush-while-striding capture for F6. **Stride
Orders F8 is now complete (F8a preview · F8b execution · F8c).** Static-verified
(no .NET SDK here). Compile + playtest in Godot.

## Momentum (the commitment bonus)
From the **4th consecutive** step of an uninterrupted stride, each step's fuel burn
drops by 1 (floor 1) — the castle finds its gait. `_strideConsecutive` counts steps
taken; `>= 3` at charge time means "this is the 4th or later". Applied to the
finalised `stepCost` in `OnPartyMoved`, so the charge, the retrace record, and the
stride fuel gate all agree; the preview's `FuelEstimate` already folds the same
discount in. Resets to 0 with the stride on any halt (`BeginStride`/`EndStride`).
(The Chronomancer first-3-flat interaction — "take the cheaper of the two" — waits
on F3; noted in the spec, no Chronomancer castle exists yet.)

## Grimoire lock
While striding, overworld casting is sealed. `OverworldSpellManager` gained a
`StrideLockQuery` delegate (set to `() => _striding`); `CastBlockReason` returns
**"the castle must halt to channel"** first of all, so every spell greys uniformly
and `RequestCast` (which already routes through `CastBlockReason`) refuses with that
reason. The panel is refreshed on stride begin/end so it greys and ungreys. Armed/
passive charges (Campward, etc.) are untouched — only NEW casts lock, per §3.4.

## Ambush while striding (+1 round) — captured for F6
The wizard's teleport delay lives in the F6 "Defend the Castle" combat, which isn't
built. But the state must be persisted at ambush time (the delay applies in a later
scene), so `EncounterRouter.SavedStrideAmbush` is set from `_striding` in
`CommitCombat` — true only for a stride-interrupting ambush (a normal fight cancels
the stride first) — and cleared on combat finish. **F6 reads it to add +1 round;
inert until then.**

## Edits
- `ExpeditionManager`: `_strideConsecutive` field; momentum at the `OnPartyMoved`
  charge + the stride fuel gate; `_strideConsecutive` reset in Begin/End; count bump
  after each step; `_spells.StrideLockQuery = () => _striding`; `_grimoirePanel.Refresh()`
  on stride begin/end; `router.SavedStrideAmbush = _striding` in `CommitCombat`.
- `OverworldSpellManager`: `StrideLockQuery` delegate + the block at the top of
  `CastBlockReason`.
- `EncounterRouter`: `SavedStrideAmbush` field + clear in `OnCombatFinished`.

## Verification
- Brace/paren/bracket balance = 0 on all three files.
- Momentum discounts the fuel gate and the charge by the SAME rule, so the gate
  can't halt a step the charge could actually afford.
- Preview `FuelEstimate` and execution use the identical momentum rule (from step 4).
- No em dashes in the new player-facing strings.

## F8c acceptance — confirm in-editor
- Order a long stride (5+ tiles) across cheap ground: the first three steps burn
  full cost, steps 4+ burn 1 less each (watch Fuel tick slower; the "~N fuel"
  preview already reflected the saving).
- While marching, the Grimoire panel greys and shows "the castle must halt to
  channel"; halting (arrival / Halt button / map click) ungreys it. An armed
  Campward still fires on a later Rest.

## Stride Orders (F8): COMPLETE.
## Still open: F3 castle types; F1/F2 rulings; F6 ambush "Defend the Castle" (which
will consume SavedStrideAmbush and the crew/module delay reducers).
