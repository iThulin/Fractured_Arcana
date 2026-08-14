# Session log — 2026-08-13 — Item effect repair (playset Phase A)

Prompted by Magos: "we don't actually have a playset of items built." The
audit found something prior: **ten of the thirty existing items were dead** —
five passive families declared in Q1's enum with ZERO consumers ever written
(they appear only in the Armory panel's display strings). This includes two of
the 2026-08-13 relics (joren, engineer) — the relic log's "every passive a
live dispatching key" claim was WRONG for those two; the keys existed, nothing
consumed them. The "items are mostly broken" residue survived Q2 for exactly
the enum tags the trigger-bus migration didn't cover. **NOT COMPILED — static
verification only** (balance deltas 0 on five files; deconstruction sweep
clean; JSONs re-parsed).

## Findings that constrain the playset

1. The item-usable trigger-bus keys are only `shield_self`, `apply_bleed`,
   `regen_aura` (aura path). The enemy handler map is richer (retaliate,
   deathburst, field_repair, …) — candidates for Phase B widening.
2. **Cards carry SCHOOL, not element.** `FireSpellBonusDamage` /
   `StormSpellCostReduction` were designed against a taxonomy that does not
   exist on cards — unimplementable as named. Replaced by school-keyed
   equivalents (below).
3. Runtime `Card` has no school field — school reads via
   `BlueprintId → CardDatabase.Blueprints`.

## The repair

**Tuple param extension** — `ResolvedLoadout.Passives` and
`Unit.EquipmentPassives` are now `(tag, value, param)`; all six
deconstruction/copy sites migrated (BuildForRun, RulesManager discount,
CombatManager turn-start/spawn/parity ×3). Param carries `PassiveParam`
(empty for non-keyed passives).

**New tags + consumers:**

- `SchoolSpellDamage` (param = school, empty = all): pinned onto
  `CasterUnit.BonusSpellDamage` during exactly that card's resolution — the
  same pin/unpin shape as the Perfected bonus, so every damage leaf prices it
  with no effect changes. `SchoolOfCard` helper reads the blueprint.
- `SchoolSpellCostReduction` (param likewise): joins the mana-discount block
  beside FirstCardCostReduction, priced per-card via `sourceCard`.
- `BonusDamageAboveHalfHP`: implemented in `ResolveMartialAttack` (healthy
  fighters hit harder; sits beside the loadout damage bonus).
- `DamageReductionPerHit`: implemented in `Unit.ApplyDamage`, **floor 1**
  (the wards' "relief is bought, immunity does not exist" guardrail).
  ORDERING (one defect caught in self-review): after the bodyguard redirect
  (the interposing guard takes the original hit; their recursive ApplyDamage
  applies THEIR plate) and before the R22 sim gate (the drag preview prices
  the true target's reduction).

**Retired tags** (kept in the enum for parse safety, commented):
`FireSpellBonusDamage`, `StormSpellCostReduction`, `AttackAppliesBleed`
(superseded by the trigger-bus `apply_bleed`).

**JSON migrations:** `serrated_blade` → apply_bleed/onAttack;
`emberwood_wand` + `relic_joren` → SchoolSpellDamage(Elementalist);
`stormcaller_staff` → SchoolSpellCostReduction(Elementalist).

## First-launch checklist

1. Build. (The tuple extension touches Unit/CombatManager/RulesManager — any
   missed deconstruction fails loudly at compile; the sweep says none remain.)
2. Equip Emberwood Wand on the wizard, cast an Elementalist damage card:
   damage +2 vs unequipped; a non-Elementalist card unchanged.
3. Stormcaller Staff: Elementalist card costs 1 less; other schools full price.
4. Serrated Blade on a martial: bleed procs in the combat log grammar
   (`[X] Serrated Blade: Bleed applied`).
5. Ironhide items (DamageReductionPerHit): incoming hits reduced, never below
   1; interpose a bodyguard — the guard takes the ORIGINAL amount.
6. BonusDamageAboveHalfHP item: +N while above half, gone below half.

## Phase B — the onStruck seam (same day, after Phase A verified in-engine)

Phase A confirmed working by Magos (sampled). Phase B added ONE seam and got
three effects from it: `QueueItemStruckTriggers` in `HandleUnitStruck` (the
U3b single onStruck site — fires only on lost-HP-and-survived, same
contract), mirroring QueueItemAttackTriggers with ItemTarget = the ATTACKER.
Because BuildTriggeredEffect dispatches by KEY, existing keys gained
defensive readings free: `shield_self` onStruck = harden when wounded,
`apply_bleed` onStruck = barbed armor. Plus `retaliate` opened to items
(enemy path reads its ability def; item path reads ItemValue / ItemTarget).
Null-target guards on retaliate + apply_bleed for sourceless chip damage.

## Phase C — the playset (40 new items, all live keys)

Rarity budget enforced: Common flat stats / Uncommon one numeric passive /
Rare decision-changer / Legendary = the 8 relics only. Post-authoring sweep:
**70 total items, zero dead passives.** New items flow into every source
automatically (markets, combat drops, favor gifts all pull ItemDatabase).

| Axis | Items |
|---|---|
| School builds | 8 Uncommon foci (SchoolSpellDamage 1, one per school) + 3 Rare greater foci (+2 & spellDamage) |
| Caster tempo | 3 Rare thrift trinkets (SchoolSpellCostReduction: Druid/Chronomancer/Enchanter) + Mana Prism + Archivist's Ring (FirstCard 2) + Scribe's Charm |
| Caster defense | Padded Vestment, Warded Mantle (shield 3), Sigil Carapace (shield 5), **Aegis of the Unbroken** (Rare: shield_self 4 onStruck) |
| Martial aggression | Soldier's Blade, Heavy Maul, Barbed Spear (bleed), Hunter's Longbow (+1 range), **Wolfpack Glaive** (AboveHalfHP 3), **Twinfang Daggers** (+1 dmg, bleed 2) |
| Tank / retribution | Iron Cuirass, Skirmisher's Leathers, Bulwark Plate (DR/hit 1), **Thornmail** (retaliate 2 onStruck), **Barbed Harness** (bleed onStruck) |
| Traversal logistics | Wayfarer's Map / Dune Compass / Coldpath Talisman (pathfinder Mountain/Desert/Tundra), Lesser+Greater Wardstone (2/4), Emberproof Charm, Lucky Coin, **Standard of the March** (regen_aura 1 + HP) |

Checklist additions: equip Thornmail on a fielded companion, get hit — the
attacker takes 2 and the proc reads in the log grammar; Aegis of the Unbroken
— shield appears on wound, not at spawn; a school focus boosts ONLY its
school's cards; market/drop pools visibly deeper on next refresh/lunation.

## Addendum — post-combat spoils card (Magos request)

`CombatSummaryPanel` (new): one modal centered card on combat-victory return
replacing the scatter of reward toasts — gold + splinters line, each item
drop rarity-colored with a BLIGHTED flag, relic grants in Legendary gold,
guardian-felled beat in violet. Collected as `(text, Color)` lines through
the existing reward block in `RestoreFromCombat`; shown at the victory
branch's end; its own dimmed backdrop (UITheme.BgOverlay — no new colors)
gates map input until Continue. The per-item toast was removed (the card
supersedes it); quest/dossier/warfront toasts stay toasts — they're events,
not spoils. **Defeat deliberately has no card**: FailExpedition's banner +
casualty note own that beat. Empty-spoils wins show "the field yields
nothing but the win itself."
