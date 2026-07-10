# K1 Verification — ComputePartyBaseHP v2.1 Formula

*Built 2026-07-09 against companion_item_systems_v2_1 §4a + §10 (K1 row). Console transcripts are the evidence medium; the pool readout is GD.Print-mirrored per house rule.*

**Change inventory:** `ExpeditionManager.ComputePartyBaseHP` rewritten to the §4a formula — `PartyPool = 20 (wizard base) + Σ per-companion (⌊BaseHP/2⌋ + loyalty bonus)`, replacing the old full-BaseHP sum; per-companion breakdown printed at expedition launch (`[PartyPool] wizard 20 + Elara 12 (⌊24/2⌋) + … = N`). `Companion` gains `LoyaltyTier` enum + `GetLoyaltyTier()` + `LoyaltyPoolBonus()` (Devoted +2, Sworn +4) — the single tier derivation K4/K5 must reuse. `RestoreFromCombat` clamps `CurrentHP` to the recomputed `MaxHP` (a companion permadying in the combat you return from shrinks the pool; the saved HP must not exceed the new ceiling — latent bug fixed in passing).

**ASSUMPTION LOGGED (needs ruling):** the docs lock Wary 0–24 / Hired 25–49 / Trusted 50–74 but never numerically pin the Devoted/Sworn split. K1 starting values: **Devoted 75–89, Sworn 90–100**, as `Companion.DevotedThreshold` / `SwornThreshold` consts. If the ruling differs, tune the two consts — nothing else re-derives tiers.

**Save round-trip:** no new serialized state. The formula reads only fields already in `GuildSaveData` (`BaseHP`, `Loyalty`, `ActivePartyCompanionIds`, recruit/permadeath flags) → the pool is deterministic across save/load by construction. The combat detour path (`EncounterRouter.SavedCurrentHP`) is unchanged except for the clamp.

## Checklist (named predictions)

1. **Solo wizard launch:** expedition with no companions → `[PartyPool] wizard 20 = 20`; HUD MaxHP = 20 + campus BonusHP.
2. **Trusted party:** launch with companions at default Loyalty 50 → each contributes exactly ⌊BaseHP/2⌋, no bonus term in the readout; total matches hand-sum. With the 16–30 template range and party of 2–3, pool lands ~45–60 (§4a's stated early band).
3. **Loyalty bonus:** set one companion's Loyalty to 90+ in the save JSON → readout shows `+4 Sworn` and the pool is exactly 4 higher than run 2; 75–89 shows `+2 Devoted`.
4. **Attrition consumes the pool (K1 exit):** corruption/exhaustion step drains reduce CurrentHP against the new MaxHP; extraction heal (`MaxHP/4` at camps) scales with the new pool.
5. **Combat detour round-trip:** enter combat mid-expedition, win, return → CurrentHP restored exactly (unchanged path); readout re-prints with identical total (deterministic recompute).
6. **Permadeath clamp:** if a companion permadies in the detour combat, the return readout shows the smaller pool and CurrentHP ≤ new MaxHP.
7. **Regression:** old saves load without migration (no new fields); Boons/campus BonusHP still adds on top (order: formula → campus bonus → restore clamp).

## Results

*(pending in-engine run)*
