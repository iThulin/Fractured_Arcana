# Session log — 2026-08-24 — F1/F2 rulings applied

Your three rulings, implemented. Static-verified (no .NET SDK here). Compile +
playtest in Godot.

## 1. Supply-cache refuel = one-time on first scout
No change — this was already the F1 default. +8 fuel the first time a cache is
scouted (`!scPoi.Discovered`); never again. Un-milkable on a persistent node.

## 2. Field Hull repair → turnaround-only
Hull no longer mends at field stops. Stripped the Hull heal from:
- **Rest site:** now restores Essence + refuels + mends crew COMBAT HP (a separate
  economy) + splinters/gold. No Hull.
- **Outpost:** now full refuel + full Essence + staging point + crew mend. No Hull.
- **Waystation:** now Essence only (was a quarter-Hull heal).
- **Steward's supply train (Economic favor):** now brings **fuel** (¼ tank) instead
  of Hull plate — thematically the supply train carries fuel, not hull.

Hull's "turnaround" repair already exists for free: a fresh deploy starts at full
Hull (the `CastleHullDamageCarried = 0 = full repair each turnaround` model, §10).
So Hull is now a genuine per-sortie resource that only fully restores between
sorties — hazard/weather Hull math bites harder, as intended. The `Extract()`
turnaround banner ("dock to refuel, restock, unload, and make repairs") now reads
true.

## 3. Own seat = home dock (full refuel + full Hull repair)
Reaching the guild's **own** seat (`SettlementAt(...).IsGuildHome`) is now a home
dock — the ONE in-field place Hull mends, because the seat *is* home: full Hull
repair + full refuel + full Essence + crew mended. (Enemy capitals / lesser cities
still fall through to their services menu.) Previously reaching your own seat did
nothing (`OpenCityServices` skips it).

## Judgment call you should rule on
- **Healing spells still repair Hull.** `SpellHealParty` (Mending Cant, Minor
  Working) heals the sortie pool = Hull, so those spells can mend Hull in the field.
  I left them working — they're ACTIVE, Essence-costed magic, not passive field
  repair, so they read as a different thing than "the castle patching itself at a
  rest stop", and zeroing them would make two Grimoire spells useless. **If you want
  strict turnaround-only, say so and I'll repurpose them** (e.g. to refuel, or to
  mend crew combat HP, instead of Hull).

## Verification
- Brace/paren/bracket balance = 0; no em dashes in the new player-facing strings.
- Only remaining Hull-restore sites: fresh deploy (turnaround reset), the seat dock,
  and healing spells (flagged above). All field-stop Hull heals removed.

## Acceptance — confirm in-editor
- Cross hazards/weather, then rest/refuel at an outpost: fuel fills, Hull does NOT.
- Walk home to your seat: Hull + fuel + Essence all fill ("The castle docks…").
- New sortie after extraction: starts at full Hull (turnaround).

## Still open: F3 castle types (+ F4/F5/F6/F7). The Cinderhold weather immunity is
already live (W2); F3 will formalize it via CastleTypeDef.
