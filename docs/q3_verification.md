# Q3 Verification — Overworld Resistance Passives (§4b + §7b)

*Built 2026-07-10 against companion_item_systems_v2_1 §4b (traversal lever) + §7b (overworld passive families) + §10 (Q3 row). Console transcripts are the evidence medium.*

**What Q3 is (and is NOT):** a DIFFERENT surface from Q2. Q2 put combat item passives on the trigger bus; Q3's passives apply during **expedition traversal** — read from equipped items, summed across the WHOLE party (§4b stacking), consumed at the movement/attrition call sites. No combat, no stack.

**Change inventory:**
- `ItemDefinition.PassiveParam` (Pathfinder's terrain arg). Overworld passive KEYS parsed in `EquipmentLoadout.BuildForRun` into new `ResolvedLoadout` fields (CorruptionWard / HazardWard / Pathfinder list) — routed BEFORE the Q2 trigger block and the enum path, so an overworld item is inert in combat (not a trigger, not an ItemPassiveTag).
- Party-sum accessors: `EquipmentLoadout.PartyCorruptionWard()` / `PartyHazardWard()` / `PartyPathfinder(terrain)` — Σ across every equipped party member.
- `OverworldMovementCost.StepCost` gains an optional `pathfinderReduction` (final `Max(1,…)` keeps the floor); BOTH callers — the charge path (ExpeditionManager) and the preview (OverworldPartyToken) — pass the same value, so the highlighted cost never diverges from the cost paid.
- ExpeditionManager attrition hooks: **CorruptionWard** (Σ, capped at `CorruptionTierAt × 2`, drain floored at 1), **HazardWard** (Σ, drain floored at 1), **Pathfinder** (via StepCost). `CorruptionTierAt` bands the 0–100 world corruption to tier 1/2/3 (30/60/90 breakpoints). `[PartyResist]` readout at deploy.
- Three exemplars (Data/Items/): Wardstone Amulet (corruption_ward 3, Trinket), Cinderweave Cloak (hazard_ward 2, Armor), Trailwarden's Compass (pathfinder Swamp 1, Trinket). Auto-granted with the Q2 items in EnsureStarterItems.

**Scope (disciplined, per the recurring rule):** the exit names CorruptionWard (cap/floor) + Pathfinder. Built those + HazardWard (CorruptionWard's §4b twin) — the three families with LIVE host systems (corruption drain, hazard drain, step cost). The other §7b families are **deferred to when their hosts exist**, and this is deliberate, not an omission:
- EssenceWell (+2 max Essence) → needs the spell system's Essence pool (S1, not built).
- Chartwright (+1 silhouette radius), Provisioner (Rest +25%) → need the silhouette/rest-heal knobs surfaced.
- Court items (TokenBearer, Seal of Introduction, Gift of Standing) → need the dispatch-takes-gear path.
These are logged in build_order §7 as Q3-adjacent, buildable when their systems land — the same dependency discipline U3/Q2 used.

## Setup for testing
Campus → Armory: the three Q3 items auto-grant (`[Armory] Q2/Q3 demo items granted`). Equip across the party (they SUM): Wardstone Amulet + Trailwarden's Compass on two trinket slots, Cinderweave Cloak on an armor slot. Deploy. Corruption is a late-cycle phenomenon — use a corrupted region (or the debug corruption tools) to reach corrupted tiles; Swamp tiles are common for Pathfinder; Swamp/Marsh/Snow/Volcanic for HazardWard.

## Checklist (named predictions)

1. **Load:** `ItemDatabase: Loaded 22 items` (19 + 3). No parse errors. Deploy with any ward equipped → `[PartyResist] CorruptionWard N, HazardWard M (+ Pathfinder per-terrain)`.
2. **CorruptionWard reduces attrition (EXIT):** cross a corrupted tile with CorruptionWard 3 equipped, on a tier-1 corrupted tile (corruption 30–59, base drain ~2) → drain is floored to **1** (`Lost 1 HP`), not fully negated. Without the ward: `Lost 2 HP`.
3. **Cap holds (EXIT):** on a tier-3 core tile (corruption 90+, base drain ~10) with CorruptionWard 3 → ward is capped at tier×2 = 6, but you only have 3, so drain = 10−3 = 7. Stack a second Wardstone (Σ 6) → drain = 10−6 = 4. A third (Σ 9) → still capped at 6 → drain = 4 (deep stacking past the cap is inert). On a tier-1 tile the SAME Σ6 ward is capped at 2.
4. **Floor holds:** no corrupted-tile crossing ever shows `Lost 0 HP` — minimum 1.
5. **HazardWard:** cross Swamp (base drain 3) with Cinderweave Cloak (2) → `Lost 1 HP`. Two cloaks (Σ4) → still floored at 1 (never 0).
6. **Pathfinder + preview parity (EXIT "Pathfinder floor holds"):** with Trailwarden's Compass (Swamp −1), the move-preview number over a Swamp tile drops by 1 AND the actual step charged matches it. A road-discounted Swamp step still floors at 1 (never free). Non-Swamp terrain unaffected.
7. **Party sum:** ward values from DIFFERENT party members add (equip Wardstone on companion A, a second on companion B → Σ 6). Removing a companion from the party drops their contribution.
8. **Combat-inert:** these items grant NO combat effect — a unit wearing Wardstone/Cinderweave/Compass shows no `[Triggers]`/proc lines in a fight; Q1 parity still verifies (they're not enum passives).
9. **Regression:** with no ward/pathfinder items equipped, all drains and step costs are exactly as before; no `[PartyResist]` line.

## Exit (companion_item_systems §10 Q3)
CorruptionWard measurably reduces attrition, capped at tier×2, floor 1 (checklist 2–4); Pathfinder floor holds (checklist 6).

## Results

*(pending in-engine run)*
