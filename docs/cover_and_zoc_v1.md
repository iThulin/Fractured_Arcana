# Cover, Delivery, and Zone of Control v1

Fractured Arcana. Built 2026-09-02 against the live combat stack. Not yet run in-engine: see §8.

## 1. Why

Playtest finding: encounters play out in the open and the player kites freely. Line of sight existed but never mattered. Three causes, all in code, none in the map art:

1. No rule punished disengaging. Movement was symmetric (2 tiles per AP both sides), so a ranged unit retreated 4 and shot while a melee enemy closed 4 and never arrived.
2. Line of sight was binary and symmetric. Standing next to a rock gave nothing; it only produced "walk around the rock".
3. Area magic was pure hex distance. `SelectAreaTarget` collected everything within `radius` straight through walls, so obstacles never shaped a spell.

Ruling (Magos, 2026-09-02): cover keys off how a hit travels, not who sent it. Magic goes over low walls; arrows do not. Bursts fill space and wrap around pillars but stop at walls. Cover soaks damage as a small per-turn pool rather than a miss chance, because damage is deterministic.

## 2. Cover

### 2a. Kinds (`CoverKind`, TileData.cs)

| Kind | What | Movement | Sight | Effect on units beside it |
|------|------|----------|-------|---------------------------|
| None | open ground | free | clear | nothing |
| Low | low wall, fence, hedge, barricade, crate, sandbag, stump, rubble, sapling (growth 1), any map object that does not block sight (brazier, cask, ward stone) | blocked (rubble: walkable, cost 2) | clear | Bolts lose damage to cover armour; arcs ignore it; bursts spend +1 step to cross it |
| High | wall, rock, crystal, tree, pillar, boulder, thicket (growth 2+), building | blocked | blocked | Bolts and martial shots cannot target the unit from that side at all; bursts do not spread through it |

`HexGridManager.CoverAt(tile)` is the only reader. High is derived from `BlocksLineOfSight`; Low comes from `TileData.AuthoredCover` (set by `ApplyObstacle` for the low kinds), the rubble modifier, growth stage 1, or a living map-object occupant. Nothing stores High.

Recipes author Low cover by naming a low kind: `{"feature": "obstacle_band", "kind": "low_wall", "at": "midpoint", "length": 5, "chance": 0.85}`. `obstacle_cluster` and `ring` with `obstacle_kind` accept the same names. Roles (`low`, `high`, `pillar`) resolve through the recipe's palette (§8). Placeholder silhouettes come from the catalog (§8).

### 2b. Direction (`HexGridManager.Cover.cs`)

`CoverBetween(defender, attacker)`: take the defender's neighbour direction nearest the attack vector (two when the attacker sits on the bisector, cosine threshold 0.84); the best cover among those neighbours is the defender's cover. The attacker's own tile never counts, so an adjacent attacker is always past the wall. Cover is asymmetric: the shooter gets nothing from the defender's wall, and any other approach is a flank.

Height (extends the 2026-08-11 high-ground ruling): an attacker on a higher tile shoots over Low cover. High cover is never negated by height.

### 2c. Cover armour (`Stats.CoverArmor`, Unit.cs)

A pool of `CoverArmorPerTurn = 2`, refilled at the start of the unit's turn and at the end of every walk (`RefreshCoverArmor`), sized by `HasAnyCover(tile)`. It is spent only by a Bolt arriving from a covered direction, before shield and armour. It is not Armor: bursts, arcs, melee, and flanking bolts never see it. Any forced move (push, pull, slide, teleport) zeroes it: being shoved from behind a wall is the point of the shove. Map objects never hold it.

Order in `ApplyDamage`: veil, bodyguard, cover armour, equipment flat reduction, sim gate, links, mitigation. Cover sits before the sim gate so the R22 drag preview prices it without spending it.

## 3. Delivery (`Delivery.cs`)

| Delivery | Who | LOS | Low cover | High cover facing the shooter |
|----------|-----|-----|-----------|-------------------------------|
| Bolt | martial ranged strikes (player and enemy), cards with `"delivery": "bolt"` | required | soaked by cover armour | shot impossible |
| Arc | default for every school card with `unit` targeting | as the card's `los` flag | ignored | blocked only when the hex-line LOS trace says so |
| Burst | `aoe`, `ring`, `cone`, `aoe_all`, map-object shatter | n/a | +1 spread step | stops the spread |
| Ground | tile writes | as the card | none | none |
| Melee | adjacent strikes, free strikes | n/a | none | none |
| Untyped | DoT, terrain, self damage, retaliation, every legacy call | n/a | none | none |

`TargetSet.Delivery` carries the selector's ruling to `DealDamageEffect`, which passes it to `Unit.ApplyDamage(amount, source, delivery)`. Selectors that do not set it leave Untyped, so every pre-cover effect is byte-for-byte unchanged.

Card JSON: `{"type": "unit", "range": 4, "delivery": "bolt"}`. Bolt implies `los: true`. No existing card sets it; the retrofit pass is a content task (§8).

## 4. Burst fill (`HexGridManager.BurstFill`)

Dijkstra from the aim point through non-High tiles, edge cost 1, or 2 into a Low tile, capped at `radius`. `BurstReach` is the membership set; `BurstRing` is the set at exactly `radius`. Height is ignored on purpose: the high-ground reward lives in Bolt range. The origin is always in the fill even when it is an obstacle (a crystal bursts from its own tile).

Consumers: `SelectAreaTarget`, `SelectRingTarget`, `SelectConeTarget` (cone shape intersected with the fill), `AoeAllEffect`, `MapObjectBurst`, and the aoe/ring target highlights, which now draw the same fill the cast resolves. Other radius effects (`imbue_area`, `terraform`, `consume_element_tile`, `primordial_surge`) still use raw distance; convert as each school's pass comes up.

The resulting dilemma: spread out and hug cover against bolts and flankers, or clump behind a wall and eat a burst that wraps around it. Cover is strong against soldiers, weak against wizards; wizards are strong against clumps.

## 5. Zone of control (`CombatManager.ZoneOfControl.cs`)

Leaving a tile adjacent to a hostile unit that `ExertsZoneOfControl()` (alive, not a map object, can act, `AttackDamage > 0`) for a tile NOT adjacent to it gives that unit one free melee swing, resolved before the step lands. Circling an enemy while staying adjacent draws nothing; stepping out of reach does. One swing per enemy per walk. Damage is `ModifyOutgoingAttackDamage(AttackDamage)` flat: no stance riders, no Ambush doubling, no charge, no AP, no `HasAttackedThisTurn`. A lethal swing ends the walk. Forced moves never trigger it, and neither do teleports, blinks, or swaps (ruled 2026-09-02): only `TryMoveTo`'s walk loop asks for strikes, and every relocation effect uses `PlaceOnTile`. Escaping a melee lock is a job for a card, which is the point.

Both sides: enemies use the same `TryMoveTo`, so a ranger backing off from a player martial pays too.

The resolver is a static hook (`Unit.ZoneOfControlStrike`) installed by CombatManager on ready and cleared on exit, so headless card tests never strike.

Player feedback: the move hover label gains a second line, `free strike: -N` and/or `cover`, when either applies. Open-ground moves read exactly as before.

## 6. Enemy AI

`BestMoveDestination` adds `PositionalScore` to every mover's score: `-15` per point of free-strike damage the walk draws (a 5-damage strike weighs 75, under one tile of progress at 100), and `+40` when the destination has Low cover against every player within 6 tiles (worst case, so a flanked tile earns nothing). Ranged movers (`MoveToDistance`, `MoveAwayFrom`) also take `-100` for any tile from which the mark sits behind High cover. `PlanRanger.Viable` and the ranged shot execution refuse a target behind High cover.

## 7. Map side (built 2026-09-02, same session)

### 7a. `cover_line` recipe op

`{"feature": "cover_line", "phase": "skeleton", "at": "axis:-3", "length": 5, "kind": "low_wall", "gaps": 1, "fill": 0.85}`

A band of cover laid across the player-to-enemy axis (the flank direction by default; `dir` overrides), centred on `at`, `length` tiles long, with `gaps` tiles guaranteed open (never the two end tiles, so the opening reads as a gate) and `fill` as the per-tile density on top. Any kind in `LowObstacleKinds` gives Low cover; a High kind (`wall`, `rock`) makes a gated wall. Skips water, occupied, and reserved tiles.

Order matters: list cover lines AFTER `carve_lane` ops in the skeleton, so the lane exists and the line's gap becomes its gate. Spawn zones BFS around blocked tiles, so a line two or three tiles from an anchor does not eat the deployment.

Trap to know about: on `obstacle_band`, `chance` is applied twice, once at the op level in `RunRecipeFeatures` (the whole band is skipped that often) and again per tile. A band at `chance: 0.7` exists 70% of the time and is 70% dense when it does. This is a large part of why the `bf_*` maps played open. `cover_line` uses `fill` for per-tile density so its `chance` means only what it says.

### 7b. Tactical report

`HexGridManager.Tactics.cs` runs after the connectivity carve and logs `[Tactics] <recipe> seed <n>: visibility X (a/b zone pairs), cover Y (c/d open tiles)`. Visibility is the fraction of player-zone x enemy-zone tile pairs with clear sight; cover is the fraction of open, unreserved tiles with cover on at least one side. A recipe may promise `"tactics": {"max_visibility": 0.45, "min_cover": 0.30}`; a miss logs a warning and never fails the map. `TacticalVisibility` and `TacticalCoverFraction` are public for a dev overlay later.

No reroll loop: `GenerateBaseGrid` instantiates tile nodes, so a data-only regeneration needs the node phase split out first. Until then the report is the tool: iterate seeds in the staging scene and read the log.

### 7c. Recipe changes

All ten `bf_*` recipes gained two skeleton `cover_line` ops at `axis:-2` or `axis:-3` and the mirror, listed after their lanes, plus a `tactics` block. Kinds vary by identity: `low_wall` in built places (courtyard, kiln, cauldron, terraces, spine, warren, amphitheater), `stump` at 0.7 fill in the grove, `barricade` on the causeway and ford. The courtyard's accent-phase rock cluster (`chance: 0.6`) moved to the skeleton at chance 1. The thresholds (0.45 to 0.55 visibility, 0.25 to 0.35 cover) are a first guess with no in-engine numbers behind them: read the report on a few seeds per map and tune.

Not touched: the terrain-default wilderness recipes (`verdant_woods`, `heathland`, and so on). They get cover today from forest (High) and map objects (Low), and their density follows the tier preset. Apply the same pass once the `bf_*` numbers are known.

`Schemas/map.schema.json` was stale (no `obstacle_band`, `ring`, `map_object`, `axis:N` / `flank:N`, `siege`, `map_events`). It now validates every recipe in `Data/Maps` except `terrain_map.json`, which is a routing table, not a recipe.

## 8. Obstacle catalog and dressing palettes (built 2026-09-02)

Problem found after the map pass: the first cover lines put `low_wall` masonry on the Warren (routed from Hills), the Terraces (Mountain), and the Cauldron (Volcanic). The cause was structural: an op named a concrete kind, and the kind carried both the rule and the look. Fix, following the terrain reskin ruling (combat_environments §4a: mechanics by type, visuals by palette):

**Catalog** (`Data/Obstacles/obstacle_catalog.json`, `ObstacleCatalog.cs`). One entry per kind: `role` (low or high, the only field the rules read), `silhouette` (slab, pillar, mass, scene) for the placeholder, `material` (masonry, rock, basalt, wood, foliage, ice, sand, arcane, cloth, crystal) so meshes can be shared with a material swap, `color`, optional `height` and `scene`. `LowObstacleKinds` is gone; `IsLowObstacleKind` reads the catalog. Unknown kinds warn once and paint as a High rock mass, never as nothing.

**Palette** (`"obstacles": {"low": "rock_ledge", "high": "rock", "pillar": "standing_stone"}` on the recipe). Ops write `"kind": "low"`, `"high"`, or `"pillar"` and `ApplyObstacle` resolves the role through the active recipe's palette, else through `ObstaclePalette.DefaultFor(dominant base terrain, role)`. Concrete kinds still work where the layout is about that thing (the courtyard ring is a `wall`, the grove's crystal clusters are `crystal`). Every recipe in `Data/Maps` now declares a palette; the `bf_*` cover lines were retargeted to the `low` role.

**Visuals** read the catalog: `scene` kinds load their path (or the exported RockObstacleScene / CrystalObstacleScene), slabs align to their run of same-kind neighbours, pillars are round columns, masses are hex prisms; colour and height come from the entry. Walkable `rubble` is a terrain scar and gets no body.

**Tectonic Shatter** now breaks any blocked kind whose material is rock, masonry, basalt, or sand, so a themed ledge shatters like the legacy `rock` did.

Sculpt list this implies: four silhouettes (low slab, tall slab, pillar, half mass) across the material families that actually appear on routed maps (masonry, rock, basalt, wood, ice), with foliage, sand, arcane, and coast able to reuse a rock or wood mesh under a different material until they earn their own. Kinds are catalog rows, not meshes.

Palette table as authored:

| Recipes | low | high | pillar |
|---|---|---|---|
| courtyard, amphitheater, overgrown_ruins | broken_wall | wall | cracked_column |
| kiln | cooled_crust | wall | basalt_column |
| cauldron, volcanic_scar | cooled_crust | basalt_column | basalt_column |
| terraces, spine, highland_crags | rock_ledge | rock | standing_stone |
| warren, rolling_hills | low_boulder | rock | standing_stone |
| grove, verdant_woods | fallen_log | old_trunk | old_trunk |
| causeway, ford | driftwood | sunk_piling | sunk_piling |
| lakeshore | driftwood | old_trunk | sunk_piling |
| marsh_flats, wetlands | reed_bank | sunk_piling | sunk_piling |
| frost_steppe, frozen_basin | ice_ridge | crystal | ice_spire |
| sunbaked_barrens | sandstone_ledge | hoodoo | hoodoo |
| heathland | drystone_wall | gorse | standing_stone |
| arcane_meadow | rune_stone | crystal | ley_pillar |
| coastal_shallows | tidal_rock | sea_stack | sea_stack |

Not routed through the palette: forest High cover from tileset props (`blocks_los` trees), the enum-theme generator's hand-painted `rock` and `crystal` (both High in the catalog, so the rules agree), and city building shells.

## 9. Legibility pass and card retrofit (built 2026-09-02)

Cover armour shows as `COV n` on the unit panel stat line and as `{n}` in the nameplate detail text (plain ASCII, per the Label3D glyph-coverage note). Every living enemy carries a marker relative to the selected player unit: `FULL COVER`, `COVER`, or `FLANKED` (only when the enemy has cover somewhere and the selected unit is on its open side; an enemy in the open is just in the open). Adjacent enemies get no marker. Refreshed on selection, on any move, and with the roster (`CombatManager.CoverMarkers.cs`).

The enemy threat overlay now gates ranged reach: a tile the shooter cannot see from a stand-tile, or whose facing side holds High cover, is not painted as threatened from there. Ring 1 (melee) ignores cover. The cone highlight clips its spines to the burst fill.

Radius effects converted to the burst fill: `imbue_area`, `terraform` (skips obstacle tiles and only pushes units it reached), `consume_element_tile`, `primordial_surge`. None of the radius effects use raw hex distance any more.

Card survey for `bolt` (rule widened 2026-09-02, Magos: a bolt is anything that flies straight from caster to target, whatever it is made of). Ten halves carry it: Frost Lance, Frost Shard, Ember Bolt, Primer Bolt, Arcane Missile, Cascade Bolt (the primary hit; its chain hops are a fresh target set and arc), Arcane Bolt (Warding Glyph's bottom), Paradox Bolt (Temporal Anchor's bottom), Conduit Bolt, and Focused Strike (a drilled lunge at range 3, the one non-projectile: it is straight and physical, so it respects a wall). Bolt cards show a grey "Bolt" pip in the element-tag row. Left as arcs on purpose: Magic Missile (`los: false`, "finds its own path"), Boulder Hurl (lobbed, `los: false`), Shock Net (thrown), Arcane Lash (a whip), Jolt and Chain Lightning (conducted current), every surge, flow, eruption, and everything spectral or ground-rooted. The original narrower survey had found only the two Elementalist shards. Boulder Hurl is lobbed (it already had `los: false`) and stays an arc; Magic Missile "finds its own path"; every Tinker and Arcanist "bolt" is current or arcane and arcs by the ruling. So cover armour is, in card terms, an anti-Elementalist-shard rule and otherwise a martial-versus-martial rule. That matches the fantasy ruling and is worth knowing before tuning: casters will feel cover mostly through bursts stopping at walls, not through soaked hits.

## 10. Aim on the card face (built 2026-09-02)

`TargetingSummary.Describe(selector)` gives every half one label and one tooltip: `Bolt 6`, `Arc 6`, `Ally 3`, `Tile 4`, `Open tile 2`, `Burst 2`, `Ring 2`, `Cone 3`, `Line 4`, `Adjacent`, `Nearest 2`, `Fire tile 4`, `Unit 4, tile 2`, `Self`, `All`. On the split card it is the first pip in the element-tag row, tinted by delivery (bolt darkest, burst warm, arc cool, ground neutral) with the cover rule in its tooltip. In the full view the same label and rule sit as a line above the rules text. The card text style guide's "range on the face only when it is the point" stands: the pip carries range now, so rules text keeps omitting it.

## 11. Cast preview: outline, markers, trajectory, aim shape (built 2026-09-02)

The per-tile range disc is gone. `CombatManager.CastPreview.cs` replaces `ShowTargetHighlight` / `ClearTargetHighlight` / `GetValidTargetCoords` with four layers, all driven off the selector so they cannot disagree with the drop validation:

**Envelope.** A second `MovementZoneRenderer` in outline mode (`ShowOutline`: low parchment lip, no fill) around the tiles the selector would accept from the caster's tile: range and sight and, for a bolt, the full-cover rule; burst fill for aoe; burst ring for ring; the fill for cone; six sight-stopped lines for line. A wall bites a notch out of a bolt's envelope; an arc's envelope wraps a low wall.

**Unit markers.** Every unit the card could want gets a torus ring: gold when reachable, dim when not, with the reason floating above the dim ones (`full cover`, `no sight`, `out of range`, `out of reach`). Adjacent enemies are simply in the envelope. `Unit.SetTargetable` and `SetBlockedReason` are separate nodes from the hover ring, so hover, selection, and targeting never fight over one mesh.

**Trajectory.** On drag hover over a unit, `TrajectoryTrace` draws a camera-facing ribbon from caster to target: straight at chest height for a bolt, a low parabola for an arc, coloured from the first sight blocker onward in red, or red end to end when the cast cannot land.

**Aim shape.** On drag hover, a third renderer shows the burst / ring / cone / line / adjacent set at the hovered aim point with a faint ember fill, or the single hovered tile in moss for tile-target cards. It clears when the cursor leaves.

Still painted per tile, on purpose: two-step legal tiles (a modal state with its own list) and the construct aura ring. `HexTile.SetRangeHighlight` stays for those two callers.

## 12. Combat touches (built 2026-09-02)

**Martial preview.** A selected martial unit hovering an enemy gets the same treatment as a dragged card: amber envelope of tiles it could strike from where it stands (melee: neighbours it can reach across the cliff rule; ranged: range, sight, and no full cover on the facing side, +1 from above), gold or dim rings on enemies with the block reason, and a straight trajectory to the hovered enemy. `ShowMartialPreview` / `ClearMartialPreview` in `CombatManager.CastPreview.cs`, driven from the hover block in `_Process`. Suppressed while a card is dragged.

**Routes avoid free strikes.** `GetPathTo` now keys on (movement cost, strikes drawn) lexicographically: the route never costs more move points than `GetMoveCostTo` promised, but among equal-cost routes it takes the one that does not break contact. Both sides get this for free; `ZoneOfControlCostTo` reads the same path, so the hover label and the AI's penalty agree.

**Rangers flank.** In `ExecuteRangedIntent`, a ranger in range whose mark sits behind full cover or out of sight spends its movement on `MoveForClearShot`: a clear shot within range+1 is worth ten tiles, distance from the preferred band costs a tile each, and the shooter's own cover breaks ties through `PositionalScore`. Logs "moves for a clear shot".

**`obstacle_band` chance fixed.** Per-tile density is `fill` (default 0.7); `chance` gates the op only, like every other op. The two authored bands (kiln, warren) were migrated `chance` to `fill`, so they are now always present at the density the author wrote. `ring` and `filled_radius` still pass `chance` to their per-tile roll as well; the city compiler emits 1.0 so it is inert there, but an author using `chance: 0.5` on a ring gets 25%.

**Wilderness cover.** All fourteen wilderness recipes gained two `cover_line` ops (`low` role, length 4, fill 0.75) at `axis:-2` / `axis:2` and a soft `tactics` block (0.60 / 0.20). Their palettes decide the dressing.

**Card schema.** `Schemas/card.schema.json` rejected 43 of 197 cards. Added the 28 effect keys the loader accepts but the schema did not list (glyph ally bonuses, Arcanist charge economy, Druid `stage`, and so on, each with a "Used by" description), the status names `named`, `temporal_drag`, `hasted`, `mana_taxed`, `delayed`, `stat: movement`, `mode: halved`, `value` on predicates, and `turns` / `radius` at minimum 0. All 197 validate.

## 13. Open items

1. Never run in-engine. Smoke test: a `low_wall` band across a courtyard; a martial archer shooting a unit beside it (expect 2 soaked, log line); the same archer from the flank (no soak); an aoe card aimed beside a wall (expect the far side untouched, highlight matching); a player unit walking out of an enemy's adjacency (expect one strike, hover label showing it first).
2. Content retrofit: which school cards should be `bolt`. Ruling of record is none by default (magic is Arc); candidates are the physical projectiles (thrown daggers, conjured arrows, Tinker bolts).
3. Done: every radius effect uses the burst fill (§9).
4. Cone highlight: spines clipped to the fill (§9); a true fan highlight is still a nice-to-have.
5. Threat overlay accounts for cover (§9). The move hover label prices free strikes, and routes now avoid them where a same-cost route exists (§12).
6. Map recipes: done, see §7. Remaining: the wilderness recipes, and tuning the thresholds against real seeds.
7. R22 drag preview for `bolt` cards: the preview path that calls `MitigateCore` directly does not model cover armour. Only matters once a bolt card exists.
