# Map Pressure v1

Fractured Arcana. Built 2026-09-02 on the E4 map event system. Not yet run in-engine.

## 1. Why

Only 3 of 24 recipes carried a map event, so the ground never pushed back: a player who found a good tile could hold it all fight. Pressure is the ground changing on a clock the player can read a round ahead, so holding still has a cost and the map's own hazards become weapons for whoever positions better.

## 2. New event kinds (`CombatManager.MapEvents.cs`)

All keyed by round / repeat_every / telegraph like the existing kinds. `at` now accepts the full recipe coordinate vocabulary (`midpoint`, `center`, `player_anchor`, `enemy_anchor`, `axis:N`, `flank:N`, `random`, `high_tile`, `low_tile`) through `HexGridManager.ResolveRecipeCoord`.

| Kind | What it does | Keys |
|---|---|---|
| `flood` | Every walkable tile at or below the water level becomes water. Occupants are shoved to the nearest dry tile within 3 and take `damage`. Level starts at `level` (default -1) and rises `rise` per firing. | level, rise, damage |
| `advance_front` | A hazard shell expanding from `at` by `steps` per firing from `radius`. From a side anchor it reads as a front sweeping the field; from the midpoint it is the cauldron ring in reverse. | at, element, radius, steps |
| `crumble_edge` | Everything at or beyond the current radius from `at` becomes `into` (chasm by default), evicting occupants inward. Radius starts at the map radius and shrinks `steps` per firing, floored at 2. | at, radius, steps, into |
| `trap` | Plants `count` neutral glyphs (team 2: they trip for both sides) on open tiles within `radius` of `at`, biased toward the player-enemy lane and never within 2 of a deployment anchor. `damage`, optional `status` / `duration`, `hidden`. | at, radius, count, damage, status, duration, hidden |

Telegraphs now cover every kind that lands on specific ground (`imbue_patch`, `advance_hazard_ring`, `advance_front`, `flood`, `crumble_edge`, the destructive kinds, and storm strikes): the affected tiles light a round ahead and sit in `TelegraphedTiles`, so enemy pathing prices them as hazards. Visible field traps are path hazards for the AI too; hidden ones are a surprise for both sides on purpose.

Round-1 events now fire: `EvaluateMapEvents` runs once at the end of deployment (and on the skip-deploy path), since the round boundary only starts at round 2. That is what lets traps be on the ground before the first move.

Fixed in passing: `spawn_object` read its object kind from `"kind"`, which is the event kind, so it always asked the catalog for "spawn_object". The key is `"object"` now.

## 3. Authored events

Every recipe except `rolling_hills` and the three with existing events now carries one clock matched to its identity:

| Recipe | Event |
|---|---|
| bf_ford, wetlands, marsh_flats, coastal_shallows, lakeshore | flood (tide) at varying pace |
| bf_kiln, sunbaked_barrens | advance_front fire from the enemy side |
| bf_causeway | crumble_edge from the midpoint |
| bf_amphitheater, bf_courtyard | visible traps in the lanes |
| bf_warren, overgrown_ruins | hidden traps |
| bf_grove, verdant_woods | rooting traps (2 damage, Rooted 1) |
| bf_spine, heathland | storm strikes |
| bf_terraces, highland_crags | rockfall (collapse_tiles) |
| volcanic_scar | spread_element fire |
| frozen_basin, frost_steppe | snow |
| arcane_meadow | advance_hazard_ring arcane |

bf_cauldron keeps its fire ring, bf_causeway its collapse, bf_ford its weather.

## 4. Open items

1. Not run in-engine. Watch: the flood on the Ford (does the first rise drown the deployments? spawn tiles are not exempt by design, but the anchors are picked at 12 percent of the X span, which on a river map may be low ground); the crumble on the Causeway (radius 5 on a blob shape may take the first bite too early); trap placement on maps with no clear lane.
2. Flood eviction uses `SuppressFalling` and a flat drown damage; no swimming, no wading. Units with no dry tile within 3 take the damage and stay put in the water tile, which the tile then rejects for everyone else. Acceptable for v1; a "drowning" status per round would be the follow-up.
3. No telegraph visual for traps beyond the glyph mark itself; a hidden trap that fires reveals nothing about the others.
4. Pacing knobs are per-recipe JSON. The tactics report does not yet score pressure.

## 5. v2: agency and conditions (built 2026-09-02)

### 5a. Scheduling

Events gained `id`, `when`, and `lever`. Runtime state (awakened round, lever delay, suppression, spent, fired count) lives on the def and is reset on the first evaluation of each combat, since recipe defs are shared.

**Awaken.** With `when`, an event sleeps until the condition first holds at a round boundary; `round` then counts from that boundary (1 = the same one), and `repeat_every` runs from there. Conditions: `player_enters:coord:radius`, `enemy_enters:coord:radius`, `enemy_count_below:N`, `player_count_below:N`, `first_blood`, `object_destroyed:kind`, `event_fired:id`, `round:N`. `wake` is the log line when it first holds.

**Lever.** `"lever": {"at": coord, "mode": hold | delay | pull, "amount": N}` spawns a Lever map object (catalog kind `lever`, immovable, 10 HP) at the first evaluation. At every boundary, a living unit of either side adjacent to it is holding it. `hold`: the event is suppressed while held (the tide waits at the sluice). `delay`: each held boundary pushes the clock back `amount` rounds. `pull`: the event fires now (or wakes, if it was sleeping), the lever breaks, and a one-shot event is spent. The AI does not seek levers; it only trips them by standing there. That is deliberate for v2: levers are the player's tool first.

### 5b. New kinds

| Kind | What | Keys |
|---|---|---|
| `raise_wall` | A band of obstacle (`wall`: kind or role, default high) rises through `at` along `dir` (`0-5`, `axis`, `flank`), `length` long, `width` rows each side, optional middle `gaps`. Occupied tiles are skipped. | at, dir, length, width, wall, gaps |
| `drop_wall` | Clears every non-building obstacle in the band. | at, dir, length, width |
| `shift` | Every unit in the band is shoved `tiles` along `push` through the forced-move resolver, front of the shove first. Collisions use momentum or `damage`. | at, dir, length, width, push, tiles, damage |
| `fog` | Sight capped at `sight` tiles for `turns` rounds. One cap covers bolts, martial shots, the ranger's clear-shot test, and the cast preview, since all read `HasLineOfSight`. Cast-fail text names the first tile past the cap. | sight, turns |
| `reinforce_from` | Spawns `units` (registry ids) on the nearest open tiles to `at`; the arrival tiles telegraph a round ahead. | at, units, difficulty |

`shift` and `raise_wall` count as destructive: telegraph is forced to at least 1.

### 5c. Breakable obstacles

Catalog entries carry `hp` (0 = indestructible). `ApplyObstacle` copies it to `TileData.ObstacleHp`. A Burst cast erodes every breakable obstacle its fill reached at the cast's damage (`TargetSet.BurstTiles`); a body shoved into a breakable wall cracks it for the collision damage. At 0 the tile becomes rubble: walkable at cost 2, Low cover. Bolts and arcs never erode cover: a wall stops an arrow, it does not fall to one. Walls, rock, pillars, standing stones, and columns are 0; low walls 10, barricades 6, fences 4, crates 5, logs 6, ice ridges 6, crystals 6.

### 5d. Authored

Ford: the tide has a sluice lever on the flank (hold). Kiln: the fire front sleeps until a player crosses the midline, and a vent lever delays it. Cauldron: the ring waits for first blood. Warren: reinforcements crawl from a burrow mouth on the flank once the enemy is down to one. Courtyard: a portcullis drops across the midpoint on round 3; a winch lever on the enemy side lifts it (pull, chained on `event_fired:portcullis`). Wetlands: fog every fourth round. Frost Steppe: wind shoves everyone one tile across the field every third round.

### 5e. Open items (v2)

1. Not run in-engine. First things to watch: the Courtyard portcullis raising on tiles the ring wall already holds (it skips blocked tiles, so the gap logic may put the gate somewhere odd); the Steppe wind pushing units into the map edge for momentum damage every third round (set `damage` or shorten the band if it is too punishing).
2. Enemy AI does not path to levers or away from telegraphed `shift` bands beyond the hazard cost.
3. Breakable cover and the tactics report: the report counts cover at generation, not after erosion.
