# Session log, 2026-09-28: Edge M7 (staves with Innate bound cards)

Martial Maneuvers & Edge spec v1, milestone 7, the last of the spec's build
order. Delivered as `edge_m7_full.patch`, built against the tree after M6.

## What shipped

**Staff binding (spec §11b).** `ItemDefinition.ActiveKind = "staff"` plus the
existing `BoundCardId` (from M6) marks a Weapon-slot wizard focus that adds one
copy of its bound card to the holder's deck. `IsStaff` on the definition. All
thirteen wizard foci carry a binding now: Apprentice's Focus binds Magic
Missile; Stormcaller Staff binds Chain Lightning; each school focus binds a
Common of its school and each greater focus an Uncommon (the full list is in
the item JSONs). The Emberheart relic and the Emberwood Wand are left unbound:
the relic per R1 (legendaries add riders, not cards) and the wand because it
is a focus by slot despite its name.

**Innate.** `Card.Innate` (per instance, never on a blueprint).
`UnitDeckData.Initialize` shuffles, then moves Innate cards to the top of the
pile, so the first draw of the fight holds them. `Reshuffle` deliberately does
not, which is the spec's "then shuffles normally".

**Deck build.** `CombatManager.AddStaffInnate(unit, cards)` runs in
`InitializeUnitDecks` for the wizard (persistent deck plus companion cards)
and for each arcane companion (curated starter deck), reading the equipped
weapon from the armory loadout. A binding that names an unknown card warns
and adds nothing.

## Rulings made in-session

1. The staff's card is an extra copy, not a replacement: a 10-card starter
   deck becomes 11. The spec accepts the dilution for exactly this item type.
2. No card-face marker for Innate. The card is an ordinary copy once drawn;
   the log line at deck build names it.

## Not done, owed

- `dotnet build` and the in-engine pass. The diff is 89 lines across 18
  files (13 of them one-line JSON bindings); it had a manual read rather
  than an independent review.
- `.uid` files: none new (no new scripts).

## In-engine confirm owed (M7 acceptance, spec §11e)

1. Equip Apprentice's Focus on the wizard (Armory tab). Start any fight: the
   Output prints "[Staff] ... Magic Missile joins the deck as Innate" and the
   opening hand holds Magic Missile. Every fight, not just the first.
2. Cast it; it goes to the discard and comes back on a reshuffle at a random
   position, like any card.
3. An arcane companion with a school focus equipped opens with that focus's
   card in its own hand.
4. Unequip the staff: the deck is back to its old size and the opening hand
   is unaffected.

## The spec, end to end

M1 Edge resource and stance triggers. M2 weapon classes, maneuvers, tray,
Honed, Stagger and Poise. M3 the remaining launch maneuvers (inside M2's
resolver). M4 reactions and enemy routing. M5 Vigilant, Opportunist and the
choices-per-turn measurement. M6 belts, wands, spellglass, item actions. M7
staves. Every code milestone is in the tree. What the spec still asks of you
is play: Goal 1 by eye, Goal 2 from `martial_fights.csv`, D9's ranged
baseline, and D14's authored teaching fight.
