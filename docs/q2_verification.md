# Q2 Verification — Item Passives on the U3 Trigger Bus

*Built 2026-07-10 against companion_item_systems_v2_1 §7a + §10 (Q2 row) + archmage_unique_units §5 (R3/R9). Console transcripts are the evidence medium; every proc is GD.Print-mirrored through the §9 log grammar.*

**The fix §7a names:** "items are mostly broken" had a specific cause — the `ItemPassiveTag` enum + bespoke ad-hoc dispatch was a second, half-built effect system beside the combat pipeline (e.g. `AttackAppliesBleed` was defined in the enum with ZERO dispatch sites). Q2 migrates item passives onto the unit-system trigger bus as string effect keys dispatched by the SAME handler map enemy abilities use.

**Change inventory:**
- `ItemDefinition.Trigger` ("none"|"onSpawn"|"onAttack"|"aura"); when set, `Passive` is read as the effect KEY and `PassiveValue` as magnitude. `ItemAbility` model carried on `ResolvedLoadout.Abilities` (BuildForRun routes trigger-items here and `continue`s past the enum `Passives` list — the two systems never double-fire the same item). `Unit.ItemAbilities` populated at spawn.
- **One dispatcher, literally:** `QueuedTrigger` generalized to serve enemy Def-keyed AND item key-keyed triggers; `BuildAbilityEffect` → `BuildTriggeredEffect` with enemy keys (requiem/deathburst) and item keys (shield_self/apply_bleed) in the same switch (§7a: "the same handler map enemy abilities use"). Drain, stack push, priority window, log grammar all shared.
- **Three exemplars** (Data/Items/): Aegis Charm (onSpawn → `shield_self` 5), Duelist's Brand (onAttack → `apply_bleed` 2), Standard of the Vigil (aura → `regen_aura` 2). Auto-granted to any armory via EnsureStarterItems (Q2 demo block) so they're equippable without a fresh save.
- Bleed status tick (2 dmg/turn) added to ProcessStatusEffects, mirroring Burn.

**Rulings logged (in code headers):**
- **onAttack rides the stack** (auto-passing), fired at the end of ResolveMartialAttack with the struck target captured — the interactive path, mid-player-turn where kills already drain safely.
- **onSpawn resolves INLINE** through the shared dispatcher (not the stack): combat hasn't started, there is no player priority window at unit-creation, and it is a no-interaction self-buff — §5's own carve-out for initial states. The HANDLER MAP is still shared, satisfying the §7a "one dispatcher" requirement. Fired after the Q1 parity assert so the ward's shield isn't read as a stat mismatch.
- **Aura is a state, not a stack event** (§5, explicit): recomputed each player-turn start in ApplyItemAuras. Regen aura chosen over armor to avoid accumulation bookkeeping — a pure per-turn heal.
- The legacy `ItemPassiveTag` enum path is UNCHANGED for unmigrated items (StartCombatWithShield / RestoreManaOnTurnStart / FirstCardCostReduction still fire the old way). Q3 finishes the migration; Q2 builds the seam + three exemplars, mirroring how U3 wired only death call sites.
- `EnemyTriggeredAbility` wrapper class name is now legacy (item procs use it too) — cosmetic, internal-only; not renamed to limit churn.

**Known follow-ups (not Q2 scope):** bleed has no status ICON in the roster (cosmetic — add to CombatUI.StatusDisplay); the remaining 5 enum passives await Q3 migration; item tooltips don't yet surface the trigger passive text (UI).

## Setup for testing
Visit the campus (Armory tab) — the three demo items are auto-granted (`[Armory] Q2 demo items granted`). Equip: Aegis Charm (Trinket, any unit — wizard or companion), Duelist's Brand (Weapon, a Fighter/Ranger companion), Standard of the Vigil (Trinket, a second companion). Deploy on expedition (item loadouts apply at spawn; debug campus fights also work).

## Checklist (named predictions)

1. **Load:** `ItemDatabase: Loaded 19 items` (16 + 3). No JSON parse errors.
2. **OnSpawn (Aegis Charm):** unit spawns → after `[Q1 Parity] … verified`, `[Aegis Charm] Ward: +5 shield to {name}`; the health bar shows 5 shield. Q1 parity does NOT report a Shield mismatch (fired post-assert).
3. **OnAttack (Duelist's Brand):** the wearer melee-attacks a surviving enemy → `[Stack] Duelist's Brand ({wearer}) enters the stack`, auto-pass (no reaction held), `[Duelist's Brand] Bleed: applied to {target} (2 turns)`. Next enemy turn: `{target} takes 2 damage from Bleed`.
4. **OnAttack lethal:** if the attack KILLS, no bleed proc (guarded on target alive) — and any death trigger still fires normally.
5. **Aura (Standard of the Vigil):** at each of your turn starts, wounded allies within 1 tile → `[Standard of the Vigil] Aura: +2 HP to N ally(ies)`; their HP rises (capped at max; full-HP allies skipped, so a fresh party logs nothing).
6. **Shared dispatcher:** an unknown item key logs `[Triggers] Unknown trigger key '…'` (the same default arm enemy keys use) — no crash.
7. **No double-fire:** a trigger-bus item never also appears in the enum `EquipmentPassives` (BuildForRun `continue`). Q1 parity `PassiveCount` still matches (trigger items excluded from that list).
8. **Reaction interaction:** with a Chronomancer reaction in hand, a Duelist's Brand proc OPENS the priority window (item procs are respondable stack objects like any ability) — auto-passes otherwise (zero clicks).
9. **Regression:** a unit with only legacy enum items (mana crystal, etc.) behaves exactly as before; no `[Triggers]` lines from it.

## Exit (companion_item_systems §10 Q2)
One OnAttack item, one OnSpawn item, one Aura item run through the shared handler map; procs appear in the combat log grammar. → checklist 2, 3, 5.

## Results

*(pending in-engine run)*
