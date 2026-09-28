# Session log, 2026-09-28: Edge M5 (Vigilant, Opportunist, measurement)

Martial Maneuvers & Edge spec v1, milestone 5, built against the tree after
M4 (which built and played on 2026-09-28). Delivered as `edge_m5_full.patch`.
With this, every code milestone of the combat half of the spec (M1 to M5) is
in. M6 and M7 (belts, wands, spellglass, staves) are the item half.

## What shipped

**Vigilant (Fighter, new).** No passive. Trigger `TurnEndCounteredIntents`:
at the end of the player turn `CountCounteredIntents` reads the board and
EdgeRules pays +1 per countered intent, max 2. A visible intent counts as
countered when the unit is adjacent to a caster whose intent is Channel or
Release; adjacent to an attacker whose Attack or RangedAttack is aimed at a
tile other than the unit's own; or standing on a shortest path between a
`charge` enemy and its mark (hex-metric collinearity). Rider: stagger
maneuvers ignore 1 Poise (`IgnoreOnePoise`, already read by `TryStagger`).

**Opportunist (either class, new).** No passive. Trigger `Openings`: each
target an Opportunist hits carries one more Opening (`Unit.Openings`,
ownerless like Marked, shown as ` O<n>` on the intent marker). A maneuver
against that target spends its Openings as Edge first, then the pool. A
finisher never discounts (it drains the pool by definition). A maneuver that
spent Openings places none on its own hit, or a 1-cost maneuver would refund
itself forever; basic attacks and reactions place them. Rider
`DamageVsMostOpenings`: +1 maneuver damage against the enemy carrying the
most Openings (ties count). The tray treats a card as affordable when some
enemy's Openings could cover the Edge shortfall; picking a target with fewer
fails cleanly with the AP refunded.

**"Either" class.** `StanceDefinition.EitherClass` plus `FitsClass(MartialClass)`.
The Training tab and the hiring roll now filter through `FitsClass`, so
Opportunist is learnable by Fighters and Rangers. While there, the hiring
roll gained the `!IsSignature` filter it was missing: a procedural hire could
roll a K4 signature stance into `TrainedStanceIds`, which the K4 contract
forbids.

**Measurement (spec §10).** `CombatTelemetry` gains three calls and two
files under `user://telemetry/`. `martial_events.csv` has one row per martial
choice (`fight_id, ts_utc, round, unit, kind, id, edge_before, edge_after`)
where kind is move, attack, shove, stance, item, maneuver, reaction_arm, or
reaction_fire (logged, not a choice). `martial_fights.csv` has one row per
fight: martial turns, distinct choices, choices per turn, maneuvers, reactions
armed and fired. The denominator is every player turn a classed martial
started; the numerator counts distinct kind-plus-id per unit per round, so
two moves are one choice and Shield Bash plus Reach Thrust are two. The
Output panel prints the figure at fight end: "Martial choices per turn: 1.75
(...) Goal 2 wants 2.00+." Debug builds only, like the rest of telemetry.

## Rulings made in-session

1. Vigilant's three countering positions (above) are the whole definition.
   The spec listed two examples; the third (in a charger's path) is the
   spec's own §4a wording. Adjacent to an attacker aimed at you does not
   count: that is being the target, not countering.
2. Openings placement is skipped on a maneuver that spent them (the review
   caught the self-funding loop). The spec's "hits place tokens" is kept for
   every other hit.
3. Openings are visible on the intent marker only, like Poise. No status
   icon, because a count is the information and the status row shows
   symbols.
4. Measurement counts a stance switch and a consumable as choices. The spec's
   Goal 2 names "which maneuver, spend or bank Edge, stance swap"; banking is
   the absence of a row and cannot be counted, so 2.00 is a slightly strict
   bar.

## Not done, owed

- `dotnet build` and the playtest pass that IS M5's acceptance: two Fighters,
  Shield Bash plus Aggressive vs Polearm plus Defensive, visibly different
  within two turns (Goal 1, by eye), and choices per turn at 2.00+ across a
  full expedition (Goal 2, from `martial_fights.csv`).
- D9: re-verify the ranged damage baseline with Edge on (Aimed plus Heavy
  Bolt plus the 4-AP double shot). Playtest, then decide.
- D14: one authored early fight where a telegraphed `Channel` can only be
  stopped by Shield Bash. Content; not authored here.
- No companion JSON lists `vigilant` or `opportunist` yet. They are learnable
  at the Training Grounds and rollable by hires; give an authored Fighter
  one if you want it on turn one of a new cycle.

## In-engine confirm owed

1. Training tab on a Ranger shows Opportunist; on a Fighter shows Vigilant
   and Opportunist.
2. Opportunist Fighter: basic attack an enemy, its marker gains ` O1`; Shield
   Bash it: log says "1 Opening(s) ... spent as Edge", Edge unchanged, marker
   loses the O.
3. Vigilant Fighter ends its turn adjacent to a channeling caster: `+1
   (countering 1 intent(s))`; adjacent to two, +2; three, still +2.
4. After a fight the Output shows the choices-per-turn line and
   `user://telemetry/martial_fights.csv` has a new row.
