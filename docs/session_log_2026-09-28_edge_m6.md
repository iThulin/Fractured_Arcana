# Session log, 2026-09-28: Edge M6 (belt, wands, spellglass, item actions)

Martial Maneuvers & Edge spec v1, milestone 6 (§11, R1b, R9, R11, D6, D7),
built against the tree after M5. Delivered as `edge_m6_full.patch`. First
milestone to touch save data since Edge began; the round-trip assertion is in.

## What shipped

**Belt (R9).** `UnitLoadout.BeltInstanceIds` (list, two slots, additive save
field: old loadouts read as an empty belt). `ArmoryData.GetBelt / EquipBelt /
UnequipBelt / BeltCarrier`; `RemoveItem` clears belts; `GetUnequipped`
excludes belted items. A belt item is anything in the Consumable pseudo-slot:
potions, scrolls, whetstones, spellglass. The Forces screen shows two belt
cards under the three equipment slots plus a "For the belt" list; the Armory
tab shows the same two cards (the wizard's belt is packed there, since the
Forces screen is companions only), a "Belt →" button on belt items, and a
Consumable filter.

**Every pinned item costs 1 AP (§11c).** Both the tray and the older satchel
popup charge `MartialAPCosts.UseItem`. The satchel now lists only the selected
unit's belt: a consumable reaches the field on a belt or not at all. The
deploy drawer's Provisions opt-out still applies on top. The objective ward,
which has no AP, can still have a scroll read over it for free, as before.

**Wands (§11b).** `ItemDefinition.ActiveKind = "wand"`, `BoundCardId`,
`MaxCharges`; `ItemInstance.Charges` (additive, -1 when not a charged item).
Trinket slot. Two authored: Wand of Missiles (adept_magic_missile) and Wand
of Mending (adept_minor_restoration), three charges each. Charges refill in
`EndSortieSlot`, which every expedition end path (extract, park, emergency,
fail) passes through: that is "refilled at campus". Ctrl+R refills mid-fight
in debug builds (D13). A martial firing a wand pays 1 Edge on top (R11).

**Spellglass (§11b, D6).** Generic definition `spellglass` (Consumable slot,
`ActiveKind = "spellglass"`); the card is bound per INSTANCE
(`ItemInstance.BoundCardId`, name "Spellglass: <card>"). Sealed at the
Armory tab's new "Scriptorium: Spellglass" section from the guild's unlocked
Common and Uncommon blueprints whose top half the item path can aim, 60 or
110 gold. Shatters on use. No Edge cost.

**Item-bound casting.** `GameState.ItemCastFree`, read at the top of
`ManaCost.EffectiveAmount`, waives mana for the duration of
`Rules.TryCastWithTargets` on an item cast, so a 0-mana martial can fire a
wand. The bound card's top half is instantiated from `CardDatabase`; Self,
Global and Area spells resolve on the click, Unit and Tile spells arm like a
technique card (range, LOS, cover and side checks mirror the drop path) and
resolve on the board click. `CheckCastRequirements` still applies. The
stack drains as a card cast does. Anything the rules refuse refunds AP, Edge
and leaves the charge or glass untouched.

**Whetstone (§11c).** Consumable, `consumeEffect: "edge"`, +2 Edge through
`EdgeRules.TryGain` (respects the per-round cap and marks the window), martial
only, spent on use. Weapon oil and smoke are not authored: each needs a
system that does not exist (an attack-rider status and a LoS-blocking cloud).

**Tray (§11d).** Items appear as cards to the right of techniques for a
martial and alone for a wizard; a wizard with an empty belt and no wand sees
no tray. Wand cards show charges; item cards carry a ◇ mark.

**Save assertion.** `Scripts/Systems/Verification/ArmorySaveAssert.cs`, wired
into the Guild panel's "Assert Round-Trips": ItemInstance (all fields plus the
two new ones, and legacy-JSON defaults), UnitLoadout (belt plus legacy
default), ArmoryData (belt rules, refusal of a double-carry, unequipped
exclusion, removal clearing the belt, charges surviving).

**Starter and ensure lists.** `wand_of_missiles` and `whetstone` join both,
so a fresh armory and an established one can each field the systems.

## Rulings made in-session

1. Scribe's Tower does not exist in code (D7 names it; the only reference is
   the spec). Spellglass sealing joins the Scriptorium section on the Armory
   tab, where scroll crafting already waits for that building.
2. The Muster screen was retired into the Forces screen on 2026-09-24; belts
   are packed there and on the Armory tab, and wands are ordinary trinkets.
3. A wand or spellglass cast is not a spell cast for attunement, mastery or
   the school hooks: no `SpellsCastThisTurn`, no Fate or Arcane attunement
   call, no `RecordCardCast`. It is an item action (Edge action kind Item,
   telemetry kind item). The spec's "at printed values" is read as "the
   spell, nothing of the caster's school".
4. Only Self, Global, Area, Unit and Tile selectors are item-aimable. The
   crafting list filters to those; a wand bound to anything else is refused
   at the card with the shape named.
5. `EquipBelt` does not check unit class; the Forces screen filters
   candidates by class and the satchel and tray refuse a whetstone on a
   non-martial. The wizard's Armory-tab belt can therefore hold a whetstone
   it cannot use; that is a UI nicety left for later.

## Not done, owed

- `dotnet build`, then "Assert Round-Trips" on the Guild panel MUST be run
  before this ships (ways-of-working: save-adjacent structs need the
  assertion green, not just written). Review substitute found three defects
  (arming a technique kept an armed item; the ward could no longer take a
  scroll; the satchel let a wizard drink a whetstone), all fixed.
- `.uid` files for `CombatManager.Items.cs` and `ArmorySaveAssert.cs`.
- Weapon oil, smoke: unauthored (above). Markets: consumables already roll
  from `IsConsumable`, so whetstones appear in market stock; wands roll as
  Trinket gear; spellglass never rolls (gold 0, crafted only).
- An Accelerando-style queued cost reduction is consumed by a free item cast
  and wasted. Cosmetic until someone notices.

## In-engine confirm owed (M6 acceptance, spec §11e)

1. Armory tab: Wand of Missiles equips to a Fighter's trinket; Whetstone
   gets "Belt →"; Forces screen shows BELT 1 with the whetstone.
2. In a fight the Fighter's tray shows the whetstone and the wand cards right
   of Shield Bash. Wand: "1 AP · charge 3/3 · 1 Edge".
3. Fire the wand at an enemy at 0 mana: Magic Missile resolves, AP -1, Edge
   -1, card reads 2/3. Fire three times, the fourth is grey "No charges left".
4. Next fight in the same expedition: still 0/3. Extract to campus, open the
   Armory: 3/3 again.
5. Seal a spellglass at the Scriptorium, belt it on the wizard, use it in a
   fight: the spell resolves and the glass is gone from the armory.
6. Guild panel, Assert Round-Trips: "ARMORY SAVE ROUND-TRIP ASSERTIONS ...
   ALL PASSED".
