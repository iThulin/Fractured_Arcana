# Session log — 2026-08-23 — Mobile Fortress F1: Fuel skin + field refueling

Implements **F1** of `mobile_fortress_expedition_spec_v1.md` (v1.1). Increment
boundary honoured: this compiles and plays on its own; F2 (Hull) is untouched.

**Verification note:** no .NET SDK in this environment — checks were STATIC
(brace/paren/bracket balance = 0 on every edited file; presence greps; overload
audit of the one new Godot API call). **You compile + playtest in the Godot
editor** before I start F2, per the handoff discipline.

## What F1 does (spec §3)

Steps ARE fuel. The per-tile burn is still `OverworldMovementCost.StepCost` —
the entire route-planning economy carries over **untouched**. F1 is a relabel
plus the one new verb (field refueling) plus the tank cap it needs.

## Edits

### `Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs`
- **Tuning fields** (`[Export]`, near `OperatingRange`): `RestRefuel = 5`,
  `CacheRefuel = 8` (§3.2 / §13). `OperatingRange` keeps its name — it IS
  MaxFuel (§10: no serialized rename).
- **`MaxFuel`** (new public runtime field): tank capacity =
  `OperatingRange + bonuses.BonusSteps`, set once per `Deploy()` at the same
  site the step budget is seeded (before the combat-return branch, so it is
  correct on both fresh deploy and combat round-trip without riding the router —
  it's deterministic from campus state, which is constant within a run).
- **`Refuel(amount, source, at, full)`** (new helper): raises `StepsRemaining`
  toward `MaxFuel`, never past it, never *lowers* a pre-existing negotiation
  overrun; logs a `refuel` fuel line; no-ops on ≤0.
- **Refuel hooks** (§3.2):
  - Outpost case → `Refuel(full: true)` on the existing full-heal arrival handler.
  - Rest case → `Refuel(RestRefuel)`; ShowInfo gains `+N fuel`.
  - SupplyCache case → `Refuel(CacheRefuel)` **gated to first discovery**
    (`!scPoi.Discovered`). See "Interpretation calls" below.
- **HUD** (§3.1): `_stepLabel` now reads `Fuel: X / MaxFuel` (and `Fuel: ∞
  [DEBUG]`); new `_fuelGauge` `ProgressBar` under it, styled as an ember furnace
  dial (`background`/`fill` stylebox overrides), fill = fuel/MaxFuel clamped.
- **Flavor strings** relabeled to the fuel/recall vocabulary: deploy line,
  fuel-spent warning, campus-bonus log detail.

### `Scripts/Systems/Overworld/Expedition/RunEventLog.cs` (§11)
- CSV header **adds** `fuel_delta,fuel_remaining` after the existing columns —
  the `steps_*` columns are **kept** (not replaced), mirrored into the new ones.
- Human `.log`: `Steps`→`Fuel` (start/end/inline `St:`→`Fuel:`), delta tag
  `st`→`fuel`.

### Player-facing "steps"→"fuel" relabels (the fuel *resource* only)
- `NarrativeEncounterPanel.cs`: `±N steps` reward chips → `±N fuel`.
- `ScoutReportPanel.cs`: "step cost already paid" → "fuel already burned".
- `NegotiationManager.cs`: deal preview + result `range`/`rng` → `fuel`.

## The deliberate line I drew: what stays "steps"

I relabeled only the **fuel resource** (gauge, its spend, grants of it).
I left "step/steps" wherever it means **tiles moved or a duration in tiles** —
the supply-leash drain ("each step out here drains the party"), safe-conduct
duration, and spell durations ("for N steps"). The castle strides tile by tile,
so that fiction is intact and those are not the fuel gauge. Flag if you want
those swept too.

## Interpretation calls you should sanity-check

1. **Supply cache refuel is one-time (first scout), not per-visit.** The spec
   says "+8 on collection", but in this codebase a cache is a *persistent*
   strategic node (never consumed, harvested per-lunation by its controller).
   Per-visit +8 would be an infinite refuel by stepping on/off. First-discovery
   is the faithful reading of "collection". **Ruling wanted** if you intended
   repeatable.
2. **Seat refuel deferred.** §3.2 says "outposts and seats refuel fully." The
   Outpost handler grants full-heal-rest here, so refuel slotted in cleanly.
   The Seat/Settlement case only opens city services — no full-heal on that
   handler — so I did NOT bolt a refuel on blind. Tell me where seat full-rest
   lives (city services?) and I'll add it there.
3. **MaxFuel excludes the negotiation overrun** (matches §3.1 "shows the overrun
   honestly"): refuel tops up to MaxFuel; a bargained surplus above it survives
   and reads as a full furnace on the dial.

## F1 acceptance (spec §12) — to confirm in-editor
- Sortie plays identically except refuel points extend range.
- Rest site → `+5 fuel` (watch note §3.2); cache first scout → `+8 fuel`;
  outpost → tank full.
- Run log shows fuel lines (`refuel` events; `fuel_*` CSV columns).

## Next: F2 — Hull (overworld drains → Hull; Hull-0 recall; turnaround line).
