# Movement & Repositioning Effects — Refactor Inventory

_Compiled 2026-07-06. Companion to the movement-model fix (see `Unit.EffectiveMovement`)._

## Context you need before touching these

- **Movement is gated by AP + `MoveRange`.** Each move action costs 1 AP (`Unit.TryMoveTo` → `TrySpendAP(1)`); a single move's path cost is capped by `MoveRange` (default 3). `Stats.MovePoints` is **written but never read** for gating — it's a dead field.
- **AP is scarce; fungibility depends on unit type.** `MaxActionPoints = Stats.BaseSpeed` (default 2), so one AP is ~half a turn. Crucially: **spell casting is gated by MANA, not AP** (verified — card play checks `Stats.Mana`, never spends AP). So for wizards/caster companions, AP is spent on **movement only** — granting them AP is pure extra repositioning, no free casts. For **martials**, AP also pays for attacks (`MartialAPCosts`), so their AP is genuinely fungible (move *or* attack). This asymmetry is the crux of the two-currency model below.
- **`BaseSpeed` does triple duty:** AP count, the reachable-tile highlight radius (`GetReachableTiles`), and the `MovePoints` init value. Any rework that changes one touches the others.

---

## Category A — Caster self-movement (the actual rework candidates)

These move the **caster**. They are the only effects where "grant AP instead" is even meaningful.

| Effect type | Class | Current behavior | Status |
|---|---|---|---|
| `move` (self-targeted) | `DashEffect` self-branch | grants `BonusMoveRange += Tiles` (movespeed currency) | **Fixed** — was dead (`MovePoints`) |
| `teleport` | `TeleportEffect` | Clears/places caster onto a targeted tile; ignores pathing/terrain, respects occupancy | **Works** (true teleport) |
| `teleport_to_glyph` | `TeleportToGlyphEffect` | Caster → a glyph tile | Works (glyph-dependent) |
| `move_per_spell_cast` | `MovePerSpellCastEffect` | Grants armor/shield per spell; **movement not built** — log says "pending move helper" | **STUB** |
| `walk_between` | phasing self-move (`the_price_of_knowing`) | Phasing pathfind (touches Ghost Road) | Works; risky (pathfinding) |

**Self-move cards routing through the dead Dash branch (movement currently does nothing):**
`adept_field_step`, `adept_primer_bolt`, `arcanist_spell_weave`, `chronomancer_borrowed_time`, `elementalist_eruption`, `enchanter_dispel_walk`, `enchanter_runic_trap`, `enchanter_warding_step`, `necromancer_grief_strike`, `necromancer_the_price_of_knowing`, `tinker_conduit_bolt` — **11 cards.**

`teleport` cards (functional, self-teleport): `adept_blink`, `arcanist_spell_weave`, `chronomancer_borrowed_time`, `enchanter_runic_trap`, `enchanter_warding_step`, `necromancer_elegy`, `necromancer_grief_strike`.

---

## Category B — Forced movement of OTHER units (push / pull)

Move the **target**, not the caster. "Grant AP" does not apply. These mostly work (direct tile relocation).

- `push` (`PushEffect`) — 14 cards: `adept_arcane_push`, `adept_jolt`, `chronomancer_decoy_of_hours`, `elementalist_boulder_hurl`, `elementalist_eruption`, `elementalist_frost_shard`, `elementalist_frost_step`, `elementalist_static_field`, `elementalist_storm_lord`, `elementalist_tectonic_shatter`, `elementalist_tremor_step`, `necromancer_the_departure`, `necromancer_the_procession`, `necromancer_unfinished_business`
- `push_damage` — `druid_call_the_boar`, `druid_pathmaker`, `elementalist_lava_flow`
- `pull` (`PullEffect`) — `elementalist_storm_lord`, `elementalist_tectonic_shatter`
- `push_to_glyph` / `pull_to_glyph` (`MoveToGlyphEffect`) — `enchanter_charm_drift`, `enchanter_compel`
- Memorial family: `pull_to_memorial`, `pull_all_to_memorial`, `push_all_from_memorial`, `push_all_to_memorial`, `pull_memorials_and_merge` — necromancer (`the_procession`, `unfinished_business`, `the_honored_dead`, `unrest`)
- `move` (target-branch): `tinker_conduit_link` (unit) works as a push; `tinker_chainflex` (tile) is **suspect** — Dash's push branch resolves *units*, a tile target likely no-ops. Verify.

---

## Category C — Position swaps (not AP candidates)

- `swap_units` (`SwapUnitsEffect`) — `enchanter_mirror_ward`
- `swap_with_spirit` (`SwapWithSpiritEffect`) — `necromancer_last_rite`
- `spirit_swap_with_nearest_enemy` — `necromancer_last_rite`

## Category D — Minion / summon movement (not AP candidates — not the player)

- `advance_all_spirits` (`AdvanceAllSpiritsEffect`) — `necromancer_dirge`, `march_and_remember`, `the_procession`
- `teleport_all_spirits_to_nearest_memorial` — `necromancer_elegy`

## Dead / unused effect types (registered, zero cards)

`pull_damage`, `teleport_to_anchor`, `teleport_to_phase_tile` — decide keep vs. cut.

---

## Intricacies to resolve before reworking

1. **Two currencies, because AP means different things per unit type** (corrects an earlier draft that called AP "fungible — leaks free casts"; that's false for casters, whose casting is mana-gated). The system splits movement into two independent levers:
   - **Movespeed** (`Stats.BonusMoveRange`, read by `EffectiveMovement`): raises reach-per-move for the turn. Mobility only — grants no action, so it never hands a martial a free attack. Symmetric across unit types; safe for any school to distribute. **This is the currency the self-move (Dash) cards now use.**
   - **AP** (e.g. the `hasted` status, `+1` action): grants an extra *action*. On a wizard that's purely an extra move (casting is mana-gated); on a martial it's a move **or** an attack — strictly stronger. Because it's the most powerful lever and martial-asymmetric, concentrate AP-granting in one school. (`hasted` currently lives in **chronomancer** — precognition, tempo_shift — which is a fine home.)
   - Rule of thumb: "reposition / dash" cards → grant movespeed; "act again / tempo" cards → grant AP.
   - **Semantic note on the Dash conversion:** `move N` (self) now grants `+N` move *range for the turn* (affects every move you make this turn), not a one-shot pool. Card rules-text may need rewording from "move N tiles" to "+N move range this turn," and per-card `N` values likely want a designer pass now that base `MoveRange` is 3 (so `+2` → reach 5).
2. **Teleport ≠ walking.** Teleport ignores terrain, gaps, walls, occupied intermediate tiles, and uses arbitrary targeting range. AP-granted walking cannot reproduce it. **Keep teleports as teleports**; don't fold them into the AP/move rework.
3. **Several candidates are already broken, not just inelegant.** The 11 self-`move` cards (dead `MovePoints`) and `move_per_spell_cast` (explicit TODO). The refactor is a *fix*, not just a cleanup — prioritize accordingly.
4. **Split the dual `move`/Dash effect.** One type doing self-grant vs. target-push chosen by targeting is a footgun (and hides the dead branch). Split into `move_self` and `push`.
5. **Interaction with the just-fixed movement statuses.** If self-move becomes "walk via the unified system," rooted/slowed correctly gate it (good, consistent). Teleports bypass rooted by design — confirm that's the intended ruling (teleport should probably escape a root).
6. **AP baseline knowledge.** Size any grant against `MaxActionPoints = BaseSpeed` (≈2). "+1 AP" is +50% of a turn; "+2" doubles it.

## Implementation status (2026-07-06)

**Done — movespeed currency wired; ALL immediate movement grants migrated:**
- `Stats.BonusMoveRange` added; reset to 0 each turn (`Unit.StartTurn` + extra-turn path). Read by `Unit.EffectiveMovement` (`budget = base + BonusMoveRange`, then root→0 / slow→half).
- Every immediate "gain N move" grant now writes `BonusMoveRange` instead of the dead `MovePoints`: `DashEffect` self (`move`), **`ImbuePathEffect` (the elementalist step cards — Frost Step etc.)**, `ImbuePathMemorialEffect` (Ghost Road), the elementalist bonus-speed effect, `AdvanceAllSpirits` spirit advance, and both martial stance speed grants (Skirmish, passive). This was the real fix — the inventory originally missed the ImbuePath family, so Frost Step's move did nothing.
- `hasted` = purely the **AP** currency now (`+1` action); its dead `MovePoints` write removed. `temporal_drag`'s dead write removed (half-move is read-side).
- **UI:** the SPD stat shows `EffectiveMovement(BaseSpeed)` and renders `SPD base→eff` when buffed/debuffed, so grants are visible.
- **Post-cast refresh:** the move zone is recomputed after a card resolves (`ShowMoveTilesWithCost`), so the extended range shows immediately — previously the zone only refreshed on re-select.

**Done — multi-turn / cross-turn sites migrated:**
- `TempBuffEffect` `stat:"movement"` → `BonusMoveRange`. Single-turn drops the old end-of-turn cleanup (the reset handles it); multi-turn re-applies via `MovementBuffEffect`.
- `MovementBuffEffect` rewritten to **re-apply** `+amount` each turn (it ticks after `StartTurn`'s reset) rather than subtract on expiry — the old version was doubly broken (dead field + reset wiped it).
- `AttunementResolver` Earth Quake now applies the **`slowed` status** instead of a movement number — a cross-turn debuff (player turn → enemy turn) must survive the enemy's `StartTurn` reset, which a `BonusMoveRange` write would not. This is the general rule: **same-turn grants → `BonusMoveRange`; cross-turn effects → the status system.**

`Stats.MovePoints` is now fully dead (write-only, zero reads) — safe to delete as a future cleanup once save-serialization is confirmed clear.

**Audit of movement in secondary/triggered activations (2026-07-06):**
- **FIXED — `slowed` double-nerf (regression):** `ApplyStatus` still halved AP for `slowed` while `EffectiveMovement` now also halves reach → movement was quartered. Removed the AP-halve; `slowed` is reach-halve only. Also dropped the dead `rooted`/`bound` `MovePoints = 0` immediate writes (`EffectiveMovement` handles them).
- **RESOLVED (2026-07-06) — `BaseSpeed` grants stay as action-economy (AP) buffs.** Ruling: these are intentional extra-move-*action* buffs, not reach buffs; no conversion. Applies to:
  - Colossus storm absorption (`Unit.cs` ~455, `+1 Speed` on absorbing a Lightning tile) — grants +1 AP.
  - Avatar transform (`ElementalistEffects` ~610) — `BaseSpeed +=` (AP, persists for avatar duration) plus a turn-1 `BonusMoveRange` reach bump. Accepted as-is.
  - Summon spawn `BaseSpeed = Speed` (spirits/constructs) — a summon's "Speed" sets its AP (how many moves); reach = its `MoveRange`.
- **Working as-is (budget-independent by design):** direct relocations — `push`/`pull`/`teleport`/`swap`/`ball_lightning`/memorial pulls — move units via `PlaceOnTile`, bypassing the reach budget. Correct for forced/teleport movement (they honor terrain/occupancy, not `MoveRange`). They do *not* respect `rooted` — pushing a rooted unit still moves it, which is standard.

**Design decisions still open:**
- Reword self-move card rules-text + retune `N` values (see Intricacies §1).
- ~~**Baseline unification**~~ **DONE (unified on `MoveRange`).** All movement paths — player highlight (`GetReachableTiles`), cost map, player commit (`TryMoveTo`), both enemy commits, and the SPD stat — now read `Unit.EffectiveMoveRange` (= `EffectiveMovement(MoveRange)`). `MoveRange` was the right source (not `BaseSpeed`): it already carried martial-4/caster-3, immobile-0, and construct-speed, and enemy AI already used it; `BaseSpeed` only drives the AP count. SPD now displays the real reach (`MoveRange`+grants), not `BaseSpeed`. Note: per-move reach no longer equals the AP number — intentional (AP = how many moves, `MoveRange` = how far each).
- Confirm teleports should bypass `rooted` (they don't path — probably intended as root-escape).
- `tinker_chainflex` (`move` on a *tile* target) — likely no-ops; verify.

Keep teleports and push/pull as their own primitives — do not fold them into the movespeed/AP rework.
