# Session log — 2026-08-24 — Mobile Fortress F4a: crew stations

The active party now crews the castle (§5): each member mans a station and grants
an effect scaled by their fit and loyalty. F4a is the model + auto-assign + the
effects that hook already-built systems. The reorder UI + persistence + Quartermaster
loot are F4b. Static-verified (no .NET SDK here); scaling checked in Python.

## The archetype gap, and how it's resolved (interpretation — please sanity-check)
§5 keys the stations to negotiation archetypes (Survivor/Commander/Scholar/Merchant/
Idealist), but **companions carry no such archetype** — only a `PersonalityTrait`.
The K5 `TraitArchetypeAffinity` table already maps each trait to one archetype, and
those line up 1:1 with the stations:
- Stoic → Survivor → **Helm**
- Loyal → Commander → **Furnace**
- Curious → Scholar → **Lens Room**
- Cunning → (Opportunist ≈ Merchant) → **Quartermaster**
- Reckless → Idealist → **Wardroom**

So a companion's existing trait IS their station archetype — no new fields (§5).
Companions in the data use exactly these five traits. **If you'd rather derive the
station fit from School, UnitClass, or an explicit archetype, say so.**

## New file: `CrewStations.cs`
`CrewStation` enum, `CrewEffects` struct, trait→station mapping, `AutoAssign`
(best-in-slot first, then leftovers fill as mismatches), and `Compute`:
- **Matched** station → full effect; **mismatched** → half (×0.5).
- **Loyalty** scales on top (§5): Wary ×0.5, mid ×1.0, Sworn ×1.25.
- The small +1 perks (Lens/Wardroom/Quartermaster) need a matched, non-Wary hand to
  land (a wary crew member works the station badly → 0).

Verified magnitudes: Helm ×0.90 (match), ×0.95 (mismatch/Wary), ×0.875 (Sworn);
Furnace +5 / +2 / +6.

## Wired now
- **Helm** — `OverworldMovementCost.CrewFuelMultiplier` shaves the finished per-tile
  burn INSIDE StepCost, so the preview ribbon and the charge agree (G1).
- **Furnace** — folded into `MaxFuel` at deploy.
- **Lens Room** — into the shared `VisionModifiers.ScryBonus` (beside weather + Arcanist).
- Deploy logs the assignment + resulting burn×/fuel/scry (`crew` line).

## Stored for later (on `_crew`, not yet consumed)
- **Wardroom** ambush-delay −1 → F6 (Defend the Castle).
- **Quartermaster** loot rarity shift → the loot pass (F4b/F5).

## Not in F4a (→ F4b)
- The reorder UI panel and the `CrewStations` save field (§10). For now the crew is
  **auto-assigned every deploy** from the roster — but the §12 accept criterion is
  still testable: change which Stoic companion is in your party and the Helm burn
  multiplier changes (the `crew` log line shows it).

## Caveat
Helm applies per-tile with `RoundToInt`, so a cost-1 tile (grassland) doesn't drop
(−10% of 1 rounds to 1). Helm bites on cost ≥ ~2 tiles. Faithful to "−10% of burn";
flag if you want it accumulated over the sortie instead.

## Verification
- Brace/paren/bracket balance = 0 on all three files; no name collisions; Companion
  `PersonalityTrait`/`Name`/`GetLoyaltyTier` confirmed; Python scaling matches §5.

## F4a acceptance — confirm in-editor
- Field a party with a Stoic companion vs without: the `crew` log line shows the Helm
  slot filled and a lower burn multiplier; step costs on cost-2+ tiles drop.
- A Curious companion → Lens Room → reveal one ring further; a Loyal one → Furnace →
  +5 MaxFuel. Sworn loyalty widens the effect; Wary halves it.

## Next: F4b (reorder UI + save field + Quartermaster loot), then F5/F6/F7.
