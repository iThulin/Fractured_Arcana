# Session log — 2026-08-24 — Mobile Fortress F3: castle types

The castle is now the school's expression (§4): each founding school gets a chassis
with a movement signature + an operating quirk. Static-verified (no .NET SDK here);
the movement math checked numerically in Python. Compile + playtest in Godot.

## New file: `CastleTypeDef.cs`
`CastleTypeDef` + a static `CastleTypes.For(CardSchool)` table (all 8, code table
per §10). Fields feed the movement signature (into StepCost), the Chronomancer flat
counter (charge site), and the quirks (MaxFuel / rest refuel / corruption / weather
/ scry now; module slots / ambush / reveal-reroll stored for later).

## Movement signatures — inside `OverworldMovementCost.StepCost` (both overloads)
Static ambient (`CastleCheapTerrains` / `CastleTerrainDiscount` / `CastleExtraRoadDiscount`
/ `CastleWaiveFord`), set once per deploy, so the preview and the charge apply the
identical modifier (G1). Verified in Python:
- Verdant Ark (Druid) Forest **1** vs Adept **2** — the §12 accept criterion.
- Gearspire (Tinker) Mountain-road **2** vs Adept **3** (road discount doubled).
- Lantern Keep (Enchanter) ford **1** vs Adept **3** (ford waived).
- Cinderhold (Elementalist) Volcanic **2** vs Adept **3**.
- Ossuary (Necromancer) Ruins −1, Orrery (Arcanist) Hills/Mountain −1 (same shape).

## Hourglass Redoubt (Chronomancer) first-3-flat
Stateful, so handled at the CHARGE (`OnPartyMoved`), not in StepCost:
`PlayerSession.ChronoFlatMovesLeft` (seeded on fresh deploy only — deliberately NOT
in `ClearRunState`, which runs every deploy — so it survives combat round-trips),
decremented per committed move. Flat overrides terrain+edge to a flat 1. Flat and
the F8 momentum discount are temporally disjoint (flat = the sortie's first 3 moves,
momentum = stride step 4+), so the flat takes priority and momentum only applies
when the flat did not — "the cheaper of the two, never both" (§3.4).

## Operating quirks wired now
- Adept **+5 MaxFuel** (folded into the tank at deploy).
- Druid **rest-refuel doubled** (RestRefuel × multiplier).
- Necromancer **corruption Hull drain halved**.
- Elementalist **weather Hull immunity** — formalised from the W2 inline check
  through `CastleTypeDef.WeatherHullImmune` (HUD "Hull immune" now reads from it too).
- Arcanist **scry +1** — summed into `VisionModifiers.ScryBonus` alongside the
  weather penalty (the shared vision hook built in W2).
- Castle name + quirk logged at deploy (`castle` line).

## Deferred (data present in the def; wired by later increments)
- Tinker **+1 module slot** → F5 (module system).
- Enchanter **ambush chance −20%** → F6 (ambush roll).
- Chronomancer **free district/POI reveal re-roll** → later (needs the scout-report
  reroll hook).
- Necromancer "corrupted **ground**" stride −1 → only Ruins **terrain** is wired;
  the corruption-tile discount needs corruption info inside the movement calc (not
  available there). Flagged.

## One preview caveat (safe direction)
The Chronomancer flat is applied at the charge, not in StepCost, so the move-cost
preview / stride estimate shows the REAL terrain cost for the first 3 moves — an
OVER-estimate (you pay the flat 1, i.e. less than shown). Never an under-estimate,
so it can't mislead you into an unaffordable move. (Momentum, by contrast, is folded
into the stride estimate.)

## Verification
- Brace/paren/bracket balance = 0 on all four files; 28 castle refs resolve; no name
  collisions. Python movement-signature check matches the §4 table.

## F3 acceptance — confirm in-editor
- Deploy as Druid vs Adept and cross forest: the Verdant Ark burns 1/tile where the
  Bastion Errant burns 2 (the run log's step lines show it).
- Elementalist in a storm: weather Hull reads "immune"; Arcanist reveals one ring
  further; Chronomancer's first 3 strides each cost 1 flat; Tinker's roads are
  cheaper; Enchanter fords rivers free.

## Still open: F4 (crew stations), F5 (modules — Tinker slot, Storm Anchors),
F6 (ambush "Defend the Castle" — Enchanter −20%, SavedStrideAmbush +1), F7 (council).
