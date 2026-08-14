# Session log — 2026-08-13 — Consumables (v1's "actives are scrolls", built)

**NOT COMPILED — static verification only** (balance deltas 0 on five files;
all 78 item JSONs parse; RemoveItem/RefreshPlayerUnitBar/signal patterns
checked against declarations).

## What this is

Not a new item category invented today — v1's locked rule was "items are
passive; **actives are scrolls**," and the Muster spec's "scroll kit" line
assumed this system. It was designed and never built. Now it is.

## Scope rulings (logged)

1. **Combat-use only in v1.** Overworld draughts (party-pool healing, step
   restoration) are a real want under the attrition economy but a second use
   surface — deferred, not forgotten. The combat scroll is the v1 fantasy.
2. **One consumable per unit per turn** (`Unit.HasUsedConsumableThisTurn`,
   reset in StartTurn) — scrolls are a tempo choice, not a health mana-pump.
3. **Four effects, all direct stat writes, no new effect system**: heal
   (capped at max), shield, mana (capped), ap. Unknown keys refuse loudly.
4. **Unequippable by construction**: Slot "Consumable" fails `Equip`'s
   EquipmentSlot enum parse — nothing in the loadout pipeline can ever see
   one. No filter code, no parallel path.
5. **Never blighted, never enchanted** (guards in WorkshopEnchants): a
   one-use item has no slot to seal; "Any"-slot enchant lines skip potions.

## Machinery

- `ItemDefinition.ConsumeEffect/ConsumeValue` + `IsConsumable` (additive;
  every existing JSON unchanged).
- **Combat UI**: "Scrolls" button beside End Turn → satchel popup (grouped by
  definition with ×counts, gate note explaining refusals: wrong phase / no
  selection / already used). Signals `ScrollsPressed` +
  `UseConsumablePressed(instanceId)`, following the existing signal grammar.
- **CombatManager**: `OnUseConsumablePressed` re-checks every gate at use
  (the popup can outlive the state it opened in — selected unit died, phase
  turned), applies the effect, consumes the instance from the Armory
  (`RemoveItem`), logs in the action-log grammar, refreshes the unit bar.
  The ward cannot drink (IsObjectiveWard gate).
- **Distribution is automatic**: markets, combat drops, and favor gifts all
  pull `ItemDatabase.GetAll` by rarity — potions entered every pool the
  moment they were authored. The spoils card shows them like any drop.

## The set (8)

| Item | Rarity | Effect | Gold |
|---|---|---|---|
| Soldier's Ration | C | heal 5 | 30 |
| Healing Draught | C | heal 8 | 45 |
| Clarity Philter | C | mana 2 | 50 |
| Barrier Scroll | U | shield 5 | 80 |
| Greater Healing Draught | U | heal 16 | 90 |
| Mana Philter | U | mana 4 | 95 |
| Stoneskin Scroll | R | shield 9 | 145 |
| Quickening Draught | R | ap 2 | 160 |

## First-launch checklist

1. Build. Buy a Healing Draught at a city market (Commons stock often).
2. In combat, wound a unit → select it → Scrolls → the satchel lists the
   draught with count → use → HP up (capped at max), instance gone, action
   log line, button row updates.
3. Second use same unit same turn → gate note "already used one this turn."
4. Quickening Draught → +2 AP visibly spendable this turn.
5. Select the ward (can't) / enemy phase → gate notes read correctly.
6. Workshop tab: potions show no enchant verbs; corrupted-ground drops never
   produce a "Blighted Draught".

## Addendum — three playtest-driven fixes (Magos)

1. **"No consumables in the shops"** — two causes, both addressed: market
   stock is lazy-persisted per lunation, so a save that rolled stock before
   the potions existed shows them only after the next refresh (by design);
   AND shops now carry a **guaranteed sundry slot** (2 at seats) on top of
   the gear slots — Common-leaning consumable, never crowding out equipment.
2. **Deploys now land in the 3D window by default**
   (`PlayerSession.ExpeditionView3D = true`). The HUD "Switch to 2D" remains
   the escape hatch until the 2D expedition scene is formally retired.
3. **Building-panel SWAP in city view**: the floating panel's input catcher
   now covers only the card (not the screen), atlas input stays live, and
   picking another building — or bare ground — swaps the panel/construct
   card in place. Camera steering beside an open panel is now possible and
   intended.

## Addendum 2 — two kinds, two rules (Magos)

`ConsumeKind` = "potion" (default) | "scroll" — a RULES split, not flavor:

| | Potion | Scroll |
|---|---|---|
| Resource | the UNIT's — one per unit per turn | the PARTY's — one per player turn total |
| Fiction | drunk by the selected unit | an arcane reading over the selected unit |
| Stacks? | — | with a potion on the same unit |
| The ward | cannot drink | **CAN be scrolled** — the protect-mission tool |
| Effects in set | heal / mana / ap | shield |

Consequence: **the ward is selectable again** (SelectUnit's O3 gate relaxed) —
a scroll needs a way to land on it, and its detailed HP bar is protect
information. Still un-commandable by construction (0 AP/move/deck) and off
the unit bar. Popup now sections [Potion]/[Scroll] rows and the gate note
explains both budgets. `warding_scroll` (Common, shield 3) added so the
party resource exists at every rarity — 79 items total.

Checklist additions: drink a potion AND read a scroll on the same unit in one
turn (legal); two scrolls in one turn (refused); select the Anchor →
Stoneskin lands on it, Healing Draught refuses.

## Deferred

Overworld consumable use (pool heal / steps / essence — wants the expedition
HUD surface); consumable stacking as true stacks (instances are fine at this
scale); poison/blighted consumables (a different design); the Muster screen's
"scroll kit" loadout slot (rides the Muster UI pass when it comes).
