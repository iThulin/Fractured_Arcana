# Session log, 2026-09-28: Edge M4 (reactions) and the M3 status

Martial Maneuvers & Edge spec v1. Built against the tree after M2 (which
built clean in Godot on 2026-09-28 once the stray `Claude outputs/EdgeRules.cs`
copy was excluded from compilation). Delivered as `edge_m4_full.patch`.

## M3 status

M3's five maneuvers (Reach Thrust, Pinning Shot, Driving Shot, Piercing Bolt,
Heavy Bolt) and collision damage were authored and resolved in M2's generic
resolver. Nothing new to build; the in-engine acceptance in the M2 log covers
them. Ctrl+W cycles a martial through the launch weapon classes to reach each.

## M4: what shipped

**Reactions (spec §7).** A reaction is a Self-shaped maneuver with
`isReaction` and a `reactionTrigger`. Two at launch, both authored:
`brace.json` (sword & shield, 0 AP, 1 Edge, `enemy_move_end`, ATK) and
`set_against_charge.json` (polearm, 0 AP, 1 Edge, `enemy_enter_zone`, ATK x 2
via the new `damage_multiplier` effect key). The registry now loads reactions
and validates them: Self shape, a known trigger, never a finisher.

**Arming.** Clicking the card pays AP and Edge at once and sets
`Unit.ArmedReaction` (one per unit; the second card is grey with "Already
armed"). The zone is drawn in steel on the board (one `MovementZoneRenderer`
per armed unit, on the grid), the tray header shows "Armed: Brace. <trigger
sentence>", and the card flips to "ARMED: fires in the enemy phase". Arming
counts as an action for Skirmish momentum and the End Turn gate.

**Refund (§7a rule 2).** At the unit's next turn start, an unfired reaction
returns its Edge through `EdgeRules.Refund`, which does not mark the window,
so idle decay still applies. A dead unit's reaction is dropped without refund.

**Set Against Charge, the interrupt.** New static hook `Unit.WalkStepReaction`
asked after each walked step lands inside `Unit.TryMoveTo` (the same loop the
zone-of-control strike uses). If the tile is in a Set Against Charge zone the
reactor strikes, the walk ends on that tile, and `Unit.MovementInterrupted`
makes every mover loop stop: `Unit.TryMoveTo` refuses further hops, and
`CanSpendMoveAP` reads it too. Cleared at the head of every `ExecuteIntent`
and in `StartTurn`. The enemy may still act from where it stopped if its
action is legal there (the spec ends its movement, not its activation).

**Brace, before it acts.** `FireMoveEndReactions(attacker)` runs at the head
of `StrikeTile`, before the attack's AP is spent, so the bracer's blow lands
first and a kill ends the activation. Beats that never strike (Guard, Shove,
a channel start) get the same call after `ExecuteIntent`. A Brace already
spent is null and does nothing the second time.

**Guardian rider (§7a rule 6).** With `GuardianReactions` the zone also covers
the same radius around each adjacent friendly unit, so Brace beside a wizard
punishes the enemy that closes on the wizard.

**Enemy routing (§7b).** New static hook `HexGridManager.ExtraStepCost`, read
inside `HazardPenalty` so all five cost loops see it. Zone tiles cost +2 for
scouts (`scout` tag), stalkers (`melee_hunt_wounded` behaviour key) and
ranged units (`AttackRange > 1`); `charge` and `pack` pay nothing and walk in.
+2 makes a one-tile detour strictly cheaper and a two-tile detour a tie, which
is the spec's "at most 1 extra move". Player pathing is untouched (the hook
returns 0 for player units). Bosses with Poise preferring a ranged or Shift
beat: not built, no boss units exist in the data.

## Rulings made in-session

1. Brace fires on any enemy activation that ends in its zone, whether or not
   the enemy moved this activation. A stationary adjacent attacker is
   punished too. The spec's "ends its movement adjacent" was read as "is
   adjacent when it acts".
2. Brace for non-strike beats fires after the beat, not before. Only strikes
   have a shared pre-action seam (`StrikeTile`); the executors move inside
   themselves.
3. Reactions apply damage directly (Marked bonus, presenter strike, Edge
   attack triggers included) and do not queue item onAttack procs. D2 is
   relaxed for reactions exactly as it already is for the zone-of-control
   strike, because both fire inside a mover's walk loop where the trigger
   drain must not run.
4. Stalker is matched by behaviour key, not tag, because no `stalker` tag
   exists; the spec's word maps to `melee_hunt_wounded`.
5. A forced move of the bracer (push, slide, blink) does not redraw the zone
   overlay until the next completed walk anywhere; the triggers themselves
   read the live zone, so correctness is unaffected, only the picture lags.

## Not done, owed

- `dotnet build` and the in-engine pass. Substitute: an independent review of
  the diff found three defects (skirmisher retreat loop ignored the interrupt;
  the routing union was consulted by the triggers and could lag a push; the
  interrupt outlived one activation under overdraw), all fixed.
- `.uid` for `CombatManager.Reactions.cs` after first import.
- Name collision: the action bar's existing "Brace (all AP)" (+1 armour, AP
  to 0) and the reaction Brace now coexist. Rename one. Recommendation: the
  bar action becomes "Hold" (it is a hold, not a brace).
- Struck-triggered reactions (spec §7a rule 4, "after the damage"): no launch
  reaction uses the trigger, so none is wired.

## In-engine confirm owed (M4 acceptance, spec §10)

1. Sword-and-shield Fighter at 1+ Edge: click Brace; the six adjacent hexes
   outline in steel, the card reads ARMED, header shows the trigger text,
   Edge drops by 1, AP unchanged.
2. Enemy phase: the first enemy to attack the bracer takes ATK before its
   swing (log order: `[Brace] ... strikes` then the enemy's attack). The
   overlay clears.
3. Arm Brace, let no enemy come: next turn start logs "lapses unfired: 1 Edge
   returned", and if the fighter did nothing else that round it still decays.
4. Polearm Fighter, Set Against Charge: a `charge` brute walking in is struck
   for ATK x 2 and stops on the first tile within 2; a `scout` or ranged
   enemy detours around the ring when a one-tile detour exists.
5. Guardian stance, Brace, standing beside the wizard: an enemy that ends
   adjacent to the wizard (not the fighter) is struck.
