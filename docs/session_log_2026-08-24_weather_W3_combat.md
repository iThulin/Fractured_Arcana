# Session log — 2026-08-24 — Overworld Weather W3: combat

Weather now follows the castle into a fight. Deploying into a front injects a
matching battlefield hazard. Static-verified (no .NET SDK here). Compile +
playtest in Godot before W4 (visuals).

## The reuse (extend, don't parallelize)

The battlefield ALREADY has a `weather_tick` map-event kind (E4 map events) with
three handlers: `StormStrike` (lightning), `RainTick` (rising water), `SnowTick`
(creeping ice). W3 doesn't build a combat weather system — it just **injects a
`weather_tick` event** when a fight starts under weather, exactly the way the debug
launcher already synthesizes map events.

## Flow

1. **Overworld → combat:** `ExpeditionManager.CommitCombat` (the single combat
   launch seam, used by normal and ambush paths) stashes
   `router.SavedWeather = WeatherAt(hexCoord)`.
2. **Router carries it:** new `EncounterRouter.SavedWeather` (default Clear).
   Cleared in `OnCombatFinished` so a later non-overworld fight (debug, city gate)
   never inherits stale weather.
3. **Battlefield injects it:** `HexGridManager.ActiveMapEvents` now merges a
   `BuildWeatherMapEvent()` alongside the existing debug event. It reads
   `router.SavedWeather`, maps it to a `weather_tick` param, and returns a recurring
   event (round 2, telegraph 1, every 3 rounds) — or null for no/absent weather.
   `EvaluateMapEvents()` runs each round boundary unconditionally, so it fires on
   any map (recipe or enum path), gated by the normal `FiresOn` schedule +
   telegraph law.

## Weather → battlefield hazard map (`WeatherDef.CombatHazard`)
- Storm → `storm` (lightning strikes)
- Blizzard → `snow` (creeping ice)
- Ashfall → `storm` (falling embers reuse the lightning handler — see flag)
- Rain → `rain` (rising water)
- Clear / Fog / Gale → none

## Edits
- `EncounterRouter.cs`: `SavedWeather` field + clear in `OnCombatFinished`.
- `ExpeditionManager.CommitCombat`: set `SavedWeather` from the combat tile.
- `HexGridManager.Recipes.cs`: `BuildWeatherMapEvent()` + merge into `ActiveMapEvents`.
- `WeatherType.cs`: `CombatHazard` values are now the concrete `weather_tick` params.

## Verification
- Brace/paren/bracket balance = 0 on all four files.
- `weather_tick` handlers (`StormStrike`/`RainTick`/`SnowTick`) confirmed present.
- `SavedWeather` set (CommitCombat), read (BuildWeatherMapEvent), cleared
  (OnCombatFinished) — no stale-weather leak into unrelated fights.
- `EvaluateMapEvents` confirmed called unconditionally at the round boundary, so
  the hazard fires on any combat map, telegraphed one round ahead.

## W3 acceptance — confirm in-editor
- Stand the castle in a Storm front and start a fight: from round 2 (telegraphed
  round 1) lightning strikes the battlefield on the `weather_tick` cadence; the
  action log announces "the storm reaches the field". Blizzard → ice, Rain → water.
- Start a fight in Clear/Fog/Gale: no weather hazard on the field.
- Win/lose and start an unrelated debug fight: no leftover weather hazard.

## Flags / tuning
- **Ashfall reuses the lightning (`storm`) handler** — there's no dedicated ash
  hazard. Falling embers as lightning-like strikes reads fine; add an `ash` branch
  to `weather_tick` later if you want distinct behavior.
- Cadence (round 2 / telegraph 1 / every 3) and per_patch (snow 2, else 1) are
  tuning seeds in `BuildWeatherMapEvent`.
- Weather applies to ALL overworld combats, so when the F6 ambush "defend the
  castle" fight lands, weather is already carried into it for free.

## Next: W4 — 3D visuals (per-front particles/tint in the expedition view).
