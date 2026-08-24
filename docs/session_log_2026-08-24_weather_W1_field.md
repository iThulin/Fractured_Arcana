# Session log — 2026-08-24 — Overworld Weather W1: moving-front field

New system (your call): a **dynamic, per-tile, moving-front** overworld weather
field, with all four effects (fuel/Hull/scry/combat) and 3D visuals planned. This
is bigger than one F-increment, so it's phased W1–W4. **W1 is the field only — no
gameplay effect yet.** Static-verified (no .NET SDK here); front math checked
numerically in Python. Compile + playtest in Godot before W2.

Weather also supplies what F3 was missing: Cinderhold's "immune to weather Hull
drain" and the Storm Anchors module now have a real drain to hook (in W2).

## Build order
- **W1 (this) — field + fronts:** vocabulary, moving-front sim, HUD readout, log.
- **W2 — overworld effects:** fuel (inside StepCost, preview==charge), Hull drain
  (stacks on terrain; road/vault suppress; Cinderhold immune; Storm Anchors −50%),
  scry-radius reduction (builds the vision hook F4/F5 also want).
- **W3 — combat:** router carries the tile's weather; battlefield injects the
  matching MapEventDef hazard.
- **W4 — 3D visuals:** per-front particles/tint in the expedition view.

## New files
- `Scripts/Systems/Overworld/Weather/WeatherType.cs` — `WeatherType` enum (Clear,
  Rain, Fog, Gale, Storm, Blizzard, Ashfall) + `WeatherDef` (severity, fuel/tile,
  hull/tile, scry delta, combat-hazard key, glyph, particle) + `WeatherCatalog`
  (the table, the field-shape tuning knobs, and a biome+season roll). Numbers are
  seeds; tune freely.
- `Scripts/Systems/Overworld/Weather/WeatherSystem.cs` — static moving-front field
  (mirrors the `OverworldSpellEffects` static-persistence pattern so weather
  survives the combat scene swap). Fronts are circles in `HexCoord` render space
  with a shared-wind velocity; `WeatherAt(tile)` = worst-severity covering front,
  else Clear. `Advect()` moves them one wind-step per committed stride; a front
  that leaves the window re-enters upwind with a fresh biome/season-rolled type.

## Scene-swap safety (the one real hazard)
The terrain sampler is a delegate onto the live expedition; that node is freed
across a combat scene swap. So the front DATA persists but the sampler does NOT:
`Configure()` runs EVERY deploy and re-points the sampler + window at the new
instance, while `Seed()` (fresh deploy only) is the sole thing that rebuilds
fronts. A combat-return keeps the weather it left with; only the sampler re-binds.

## ExpeditionManager wiring
- After the deploy branch: `WeatherSystem.Configure(window, TerrainAt, season)`
  always; `Seed(random)` only on a fresh deploy; baseline `_lastWeatherAtParty`.
- `OnPartyMoved`: `WeatherSystem.Advect()` once per committed stride, then announce
  when the weather over the castle changes (ShowInfo + `weather` log line).
- HUD: a "Weather: {glyph} {name}" line; severe fronts (severity ≥ 3) in the
  warning tint. Run log gets `weather_seed` at deploy and `weather` on change.
- `Reset()` on all three run-end paths (nulls the freed-node delegate too).

## Verification
- Brace/paren/bracket balance = 0 on all three files; no name collisions with
  existing types; `TerrainAt` method-group → the sampler `Func` cleanly.
- Python front-sim (25×25 window, 3 fronts r≈5, speed 0.6): ~98/625 tiles under a
  front at seed (a routable subset — you can detour around a storm), fronts drift
  and respawn over 60 strides. The "route around it" gameplay is real.

## W1 acceptance — confirm in-editor
- Deploy a sortie: HUD shows a weather line; run log has a `weather_seed` entry.
- Stride around: the weather over the castle changes as fronts drift across the
  window, each change announced and logged. No fuel/Hull/scry/combat effect yet.
- Drop into a fight and return: the weather is the same field you left (not reset).

## Interpretation / tuning flags
- Front shape (count 3, radius ~5, speed 0.6, jitter ±2) and the biome/season
  weight tables are first-pass tuning knobs in `WeatherCatalog`.
- Weather is not yet saved to disk mid-sortie; a full app reload mid-run reseeds
  it. Matches how spell windows/fog already behave. Say if you want it persisted.

## Also open (unchanged from before)
- F3 castle types: recon done, movement signatures + quirks not yet written.
  Cinderhold weather immunity now lands in W2. The rest of F3 is still queued.
- F1 rulings (supply-cache one-time refuel; seat refuel) and F2 field-Hull-repair
  question remain open with defaults in place.

## Next: W2 — overworld weather effects (fuel, Hull, scry), incl. Cinderhold immunity.
