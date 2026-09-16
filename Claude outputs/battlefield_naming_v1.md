# Battlefield Naming v1 — one vocabulary for maps, biomes, and map events

*Spec before code (ways-of-working). Inventory below was taken from the repo at commit `[feat] Battlefield mission types and deployment types` (2026-09-15 17:48), which matches the live files for every path listed.*

## 1. The problem

Three vocabularies describe "what kind of place is this fight in":

| Layer | Count | Examples | Where it lives |
|---|---|---|---|
| Overworld `TerrainType` | 16 | `Grassland`, `Hills`, `Marsh`, `Coast` | `OverworldHex.cs`, world gen, `terrain_map.json` keys, `battlefield_rotation.json` keys, region `battlefields` keys |
| Recipe id | 24 | `heathland`, `bf_warren`, `marsh_flats` | `Data/Maps/*.json` filenames + `"id"`, pool entries, `TerrainRecipeMap` fallback, launcher array |
| `MapTheme` enum | 10 | `Heathland`, `Wetlands`, `RiverValley` | `HexGridManager.Theme` (palette presets for the pre-recipe generator, `CharacterStagingPreview`) |

Plus a fourth, the recipe `display_name` (`"The Warren"`, `"Marsh Flats"`), which is the only one a player could ever read.

The debug launcher shows two at once (`Hills → bf_warren`), the `bf_` prefix marks ten recipes as "archetypes" for no reason the data supports (every recipe has the same schema), and map-event kinds are engineer shorthand (`advance_hazard_ring`, `imbue_patch`, `shift`) that says nothing about what the player will see.

**Ruled (this session): the recipe's display name is THE biome name.** Ids are derived from it. Everything else either keys off it or is an internal implementation detail that never surfaces.

## 2. Naming rule

- **Recipe id = snake_case(display_name) with a leading "The " dropped.** `"The Warren"` → `warren`. One id, one file, one display name, no prefixes. The `display_name` field stays (it carries the article and capitalisation).
- **Map-event kind = `subject_verb`, present tense, what the player sees.** `flood` → `tide_rises`. The kind string is also the launcher label, so it must read as English.
- **Overworld `TerrainType` is NOT a biome name and does not change.** It is the world map's vocabulary (`Hills`, `Coast`) and the key that picks a recipe pool. It stays as the pool key everywhere.
- **`MapTheme` is not renamed in this pass** — see Ruling R2.

## 3. Recipe renames (10 of 24; the other 14 already follow the rule)

| Current id / file | New id / file | display_name (unchanged) | Refs outside own file |
|---|---|---|---|
| `bf_amphitheater` | `amphitheater` | The Amphitheater | 12 |
| `bf_cauldron` | `cauldron` | The Cauldron | 12 |
| `bf_causeway` | `causeway` | The Causeway | 29 |
| `bf_courtyard` | `courtyard` | The Courtyard | 17 |
| `bf_ford` | `ford` | The Ford | 20 |
| `bf_grove` | `shattered_grove` | The Shattered Grove | 17 |
| `bf_kiln` | `kiln` | The Kiln | 14 |
| `bf_spine` | `spine` | The Spine | 18 |
| `bf_terraces` | `terraces` | The Terraces | 15 |
| `bf_warren` | `warren` | The Warren | 17 |

Reference counts include `docs/` and `README.md`; the live surfaces are: `Data/Maps/terrain_map.json`, `Data/Encounters/battlefield_rotation.json`, five region files (`frontier_wilds`, `obsidian_waste`, `the_convergence`, `the_crags`, `verdant_deep`), `CombatDebugLauncher.Patterns.cs` (`BattlefieldRecipes` array), and comments in `CombatDebugLauncher.cs` / `PlayerSession.cs` / `MapEvents.cs`.

Unchanged: `arcane_meadow`, `coastal_shallows`, `frost_steppe`, `frozen_basin`, `heathland`, `highland_crags`, `lakeshore`, `marsh_flats`, `overgrown_ruins`, `rolling_hills`, `sunbaked_barrens`, `verdant_woods`, `volcanic_scar`, `wetlands`.

Filename mechanics: `git mv` each `Data/Maps/bf_*.json`; delete the matching `.json.uid`? — no: `.uid` sidecars only exist for scripts/scenes, not data JSON (verified: `Data/Maps` has none). `MapRecipeRegistry` loads by directory scan, so the filename is cosmetic; the `"id"` field is what matters.

## 4. Map-event kind renames (all 19 kinds)

| Current | New | What the player sees | JSON files | C# sites |
|---|---|---|---|---|
| `imbue_patch` | `hazard_patch` | an element scars a patch of tiles | 0 | MapEvents, Launcher, CastleDefenseCompiler |
| `spread_element` | `hazard_spreads` | existing hazard creeps outward | 1 | MapEvents, Launcher |
| `advance_hazard_ring` | `ring_closes` | a hazard ring tightens toward the centre | 2 | MapEvents, Launcher |
| `advance_front` | `front_advances` | a hazard wall sweeps across the field | 2 | MapEvents |
| `flood` | `tide_rises` | water climbs one step each firing | 5 | MapEvents, MapRecipe (destructive list) |
| `crumble_edge` | `edge_crumbles` | the rim falls into chasm | 1 | MapEvents, MapRecipe |
| `collapse_tiles` | `ground_collapses` | tiles give way to rubble/chasm | 3 | MapEvents, MapRecipe, Launcher |
| `raise_tiles` | `ground_rises` | tiles lift a step | 0 | MapEvents, Launcher |
| `lower_tiles` | `ground_sinks` | tiles drop a step | 0 | MapEvents, Launcher |
| `spawn_object` | `object_drops` | a map object lands | 0 | MapEvents, Launcher |
| `trap` | `traps_arm` | traps appear in the lanes | 7 | MapEvents |
| `raise_wall` | `wall_rises` | a wall goes up | 1 | MapEvents, MapRecipe |
| `drop_wall` | `wall_drops` | a wall comes down | 1 | MapEvents |
| `shift` | `ground_heaves` | a band/ring of units is displaced | 1 | MapEvents, MapRecipe, CastleDefenseCompiler |
| `stomp` | `ground_crushes` | units in the zone are struck | 0 | MapEvents, MapRecipe, CastleDefenseCompiler |
| `fog` | `fog_rolls_in` | sight capped for N rounds | 1 | MapEvents (`WeatherType.cs` "fog" is the weather enum, NOT this — leave it) |
| `reinforce_from` | `reinforcements_arrive` | enemies enter from an edge | 1 | MapEvents, BattlefieldRoster (pressure add-on), CombatManager |
| `objective_change` | `objective_turns` | the objective changes mid-fight | 0 | MapEvents, BattlefieldRoster |
| `weather_tick` | `weather_turns` | storm/rain/snow tick | 5 | MapEvents, Launcher, HexGridManager.Recipes |

Also touched by the sweep: `PlayerSession.DebugMapEventKind` (string, transient), the `when: "event_fired:<id>"` triggers reference event **ids** (`tide`, `portcullis`), not kinds — untouched. `MapRecipe.IsDestructiveKind` list (line 422) is rewritten with the new names. The `"kind"` key on obstacles/objects (`rock`, `barricade`, `resonant_crystal`…) is a different namespace and is not renamed.

## 5. What is deliberately NOT renamed

- Overworld `TerrainType` values and every pool keyed on them (§2).
- Deployment ids (`line`, `close`, `far_bank`, `crest_held`…) — already `subject_state` English; leave.
- Objective ids, obstacle kinds, unit ids, region ids.
- `WeatherType.Fog` (overworld weather enum).
- City-siege compiled labels in the launcher (`campus_gate (compiled)` etc.) — dev-only, not data.

## 6. Save / runtime compatibility (checked)

- Recipe ids are **not persisted**: `BattlefieldRoster._recipeHistory` is static per process; `EncounterContextCarrier` is transient; `PendingCityFight*` stores a city id and cell, not a recipe. No migration.
- Event kinds are not persisted either; `PlayerSession.DebugMapEventKind` is static.
- `RunEventLog` writes the strings into `user://run_logs/*.csv` — historical logs will show old names. Acceptable.
- `TerrainRecipeMap._fallback = "heathland"` — unchanged (already conforms).

## 7. Sequencing (one commit, one sweep)

1. `git mv` the ten `Data/Maps/bf_*.json` files; edit each `"id"`.
2. Literal sweep of the ten recipe ids across `Data/`, `Scripts/`, `docs/`, `README.md` (word-boundary; `bf_grove` → `shattered_grove` is the one non-mechanical mapping).
3. Literal sweep of the 19 event kinds across `Data/Maps/*.json`, `Scripts/`, `docs/` — kinds only inside `"kind": "…"`, `case "…"`, `kind == "…"`, the launcher array, and `IsDestructiveKind`. **Not** the word `fog` in `WeatherType.cs`, not `"flood"` inside Necromancer card scripts (`trigger_flood`, `[Flood]` — that's Grief).
4. Rebuild the launcher's `Map event:` list and the `Map / terrain:` label (`Hills → The Warren`, display name not id).
5. `dotnet build` (hard gate), then: launch each of the ten renamed recipes from the launcher, launch every event kind once via the injector, and play one region fight in `the_crags` (its pool references three renamed recipes).

## 8. Acceptance

- Boot log `[MapRecipeRegistry] Loaded 24 map recipe(s)` with zero unknown-recipe warnings across all 15 regions + rotation file (add a one-time validator print: every `recipe` referenced in pools resolves).
- `bf_ford` → `ford` plays the `tide_rises` event with the swell (yesterday's patch) — the event rename must not break the `event_fired:tide` trigger.
- Launcher shows one biome vocabulary: display names.

## 9. Rulings needed before code

- **R1 — `bf_grove` → `shattered_grove` or `grove`?** Rule says `shattered_grove`. It's the only rename that changes the word, and it's the longest id in pools.
- **R2 — `MapTheme` enum.** Ten values, 31 uses, only meaningful to the pre-recipe generator and `CharacterStagingPreview`. Options: (a) leave it as an internal palette preset (this spec), (b) rename its values to match recipe ids where they coincide (`Heathland`, `Wetlands`, `CoastalShallows`…) and drop `RiverValley`, (c) retire it and drive the staging preview from a recipe. (c) is a separate build; (b) is cheap and can ride this sweep.
- **R3 — event kind names.** Approve the `subject_verb` column as written, or edit in place. Two I'm least sure of: `traps_arm` (vs keeping `trap`) and `ground_crushes` for `stomp`.
- **R4 — docs sweep.** Rename inside `docs/*.md` and `claude/*.md` session logs too, or leave history as written? Recommendation: rename in `docs/` (living specs), leave `claude/` logs untouched.
- **R5 — launcher label format.** `Hills → The Warren` (terrain → biome) or biome only with terrain in a tooltip?
