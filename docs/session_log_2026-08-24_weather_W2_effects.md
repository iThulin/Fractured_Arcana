# Session log — 2026-08-24 — Overworld Weather W2: overworld effects

Adds the three overworld effects to the W1 weather field: **fuel**, **Hull drain**,
and **scry-radius** penalty. Static-verified (no .NET SDK here). Compile + playtest
in Godot before W3 (combat).

## What W2 does

Weather now costs something. A front over a tile:
- **Burns more fuel** entering it (Rain/Gale/Storm +1, etc.).
- **Grinds the Hull** each tile (Storm/Blizzard/Ashfall −2), stacking on top of
  terrain/corruption drains.
- **Shrinks the lens** — scry/reveal radius drops (Fog −2, Blizzard −2, Storm −1…).

Because the field is per-tile moving fronts (W1), the play is to **route around**
the worst cells — the fuel/Hull/scry costs make that a real decision.

## Edits

### Fuel — `OverworldMovementCost.StepCost` (both overloads)
`cost += WeatherCatalog.Def(WeatherSystem.WeatherAt(to)).FuelPerTile;` applied at the
single source of truth, so the preview ribbon and the charge cannot diverge (G1).
Ordering guarantees it: the charge computes `StepCost` BEFORE `WeatherSystem.Advect()`
runs (later in `OnPartyMoved`), and the preview also reads the pre-advect field — so
both see the same weather for the pending move.

### Hull drain — `ExpeditionManager.OnPartyMoved`
A new block after the leash drain (inside the destination-known section):
- `weatherHull = WeatherAt(newCoord).HullPerTile`, stacks on terrain/corruption.
- Suppressed inside a vault sanctuary (like the other tolls).
- **Cinderhold (Elementalist) is immune** — delivered now via a `SelectedSchool`
  check, with a comment that F3 will route it through `CastleTypeDef`.
- **Storm Anchors −50%** hook is noted for F5 (module system not built yet).
- Hull-0 → `EmergencyExtract` (forced recall, spoils kept — the F2 rule), NOT a loss.
- Logged as `weather_drain`; announced ("Storm batters the castle. 2 Hull lost.").

### Scry — new shared `VisionModifiers` hook
- New `Scripts/Systems/Overworld/Expedition/VisionModifiers.cs`: a static
  `ScryBonus` both reveal paths add to their radius (floored at 0).
- `FogOfWarManager.UpdateVision` and `ExpeditionWindow3D.UpdateVision` now read it —
  the two paths that were setting reveal independently now share one modifier.
- The manager sets `ScryBonus = weatherScryDelta` at the party each move (and a
  deploy baseline); `Reset()` on run-end.
- **This is the shared vision hook** the Arcanist scry +1 (F3), Lens Room crew (F4),
  and Farseeing Array module (F5) will add into — built once, here.

### HUD
The weather line now shows the toll: e.g. `Weather: ⛈ Storm  (+1 fuel, -2 Hull, scry -1)`
— and reads "Hull immune" instead of the Hull figure when piloting the Cinderhold.

## Verification
- Brace/paren/bracket balance = 0 on all five touched files.
- Weather fuel present in BOTH `StepCost` overloads (preview + charge paths).
- `VisionModifiers.ScryBonus` read by both reveal paths, set in two manager spots,
  reset on run-end. Reveal ranges floor at 0 (you always see your own tile).
- Preview==charge holds by construction (advection is post-charge).

## W2 acceptance — confirm in-editor
- Stride into a storm/blizzard front: the fuel preview ribbon shows the higher cost
  BEFORE you commit; committing burns it; Hull ticks down (`weather_drain` in the
  log); the revealed radius visibly shrinks; all three lift when you leave the front.
- Pilot the Elementalist (Cinderhold): weather Hull drain reads "immune" and Hull
  does not fall to weather (fuel/scry still apply).

## Flags / deferrals
- Cinderhold immunity is an inline `SelectedSchool == Elementalist` check for now;
  F3 will formalize it through `CastleTypeDef` (and add the other castles).
- Storm Anchors −50% weather-Hull reduction lands with the F5 module system.
- Scry penalty updates on move (the field only advects on a committed stride), so a
  perfectly stationary castle won't see the number change until it next moves —
  correct, since fronts only drift on a stride.

## Next: W3 — combat (router carries the tile's weather; battlefield injects the
matching MapEventDef hazard).
