# Session log, 2026-09-27: Edge M2 (weapon classes, maneuvers, Honed, Stagger and Poise)

Martial Maneuvers & Edge spec v1, milestone 2, built against the tree as it
stands after M1 (HEAD `23d7777` plus the M1 patch). Delivered as
`edge_m2_full.patch`. The spec itself is now in the repo at
`docs/martial_maneuvers_edge_spec_v1.md` (transcribed from the PDF).

## What shipped

**Weapon class (spec §6, §8, D4).** `WeaponClass` enum in `ItemDefinition.cs`
(None, SwordShield, Polearm, Bow, Crossbow, plus the post-launch four).
`ItemDefinition.WeaponClass` (JSON `weaponClass`, string, parsed lazily).
`ResolvedLoadout.WeaponClass` is read from the equipped Weapon-slot item in
`BuildForRun`, and `ApplyEquipmentLoadout` copies it to `Unit.WeaponClass`.
Every martial weapon item now carries a class: iron_sword, soldiers_blade,
serrated_blade, duelists_brand and relic_conductor are SwordShield;
barbed_spear and wolfpack_glaive are Polearm; hunters_bow and hunters_longbow
are Bow; heavy_maul is Hammer; twinfang_daggers is PairedBlades. There was no
crossbow item at all, so `guild_crossbow.json` (Common, +1 damage, +1 range)
is new and joins both the fresh-armory starter list and the established-armory
ensure list in `CycleInitializer`. D4's "save migration" turned out to be
unnecessary: instances reference definitions by id, so classing the
definitions classes every instance ever saved. The validator half of D4 is a
`PushWarning` at spawn for a classed martial fielding a None weapon.

**Maneuvers (spec §6a, §8).** `ManeuverDefinition.cs` (data), `ManeuverRegistry.cs`
(loads `Data/Maneuvers/*.json`, validates every entry at load: known effect
keys, legal weapon class and shape, Edge cost within cap, R7's sling-only
ranged stagger, and refuses `isReaction` until M4), and
`CombatManager.Maneuvers.cs` (arming, targeting, resolution, Honed, Stagger,
debug levers). Six launch maneuvers are authored: Shield Bash, Reach Thrust,
Pinning Shot, Driving Shot, Piercing Bolt, Heavy Bolt. Brace and Set Against
Charge wait for M4's reaction machinery. Effect keys: `damage` (delta from
ATK), `push` (tiles, optional `collisionDamage`), `stagger`, `root` (turns),
`ignore_armor`, `ignore_chitin`, `finisher_scale` (per Edge spent). Shapes:
Self, Adjacent, Range N, Line N (click a tile on an axis line; Piercing Bolt
hits allies on the line, Reach Thrust spares them).

**Stance riders on maneuvers (spec §5 column).** `StanceDefinition.ManeuverRider`
enum, one per stance; signatures follow their base-kit analogue. Live in M2:
Aggressive push +1, Defensive gain 1 shield, Duelist +1 vs Vulnerable, Berserk
+1 below half, Aimed +1 range on ranged maneuvers, Suppression +1 root
duration, Volley +1 line length, Ambush first maneuver 1 Edge cheaper, Marked
1 cheaper vs Marked targets. Declared but inert until their milestone:
Reckless Edge debt, Guardian reactions (M4), Skirmish self-displacement (no
such maneuver at launch), Vigilant ignore-one-Poise (M5).

**Honed (spec §4b, §6a).** At 3+ Edge the basic attack gains: sword & shield
1 shield on hit; polearm reach 2 at melee price (the weapon decides swing vs
shot before the Honed reach is added, so no 2-AP spear and no "+1 from above"
shot bonus); bow +1 range; crossbow ignores 1 armor. `MartialReach` in the
hover preview and the action-bar tooltip read the same bonus.

**Stagger and Poise (spec §2a, D1, R5).** `Unit.IsStaggered` (a bool, not a
status duration: enemy statuses tick in `StartEnemyTurn` BEFORE the activation
that must read it), `Unit.Poise/MaxPoise`, `UnitDefinition.Poise` (-1 derives
from Role: elite 1, boss 2). `TryStagger` cracks Poise first, then lands. The
staggered activation (`ExecuteStaggeredActivation`, called at the head of the
enemy's turn after `ApplyPendingProfile`): Channel or Release loses its charge;
Attack becomes move-only; RangedAttack, Imbue and Shove do nothing; Guard is
exempt and keeps the stagger for the next beat; `everyNRounds` and `onTurnEnd`
abilities are not queued (that is where summon_cadence and ritual live, since
Ritual, Summon and Shift are abilities in this codebase, not intent kinds);
the cycle index does not advance; Poise refills. The intent marker gains a
`×` suffix while staggered and a `P<n>` Poise count on elites and bosses. The
unit's status row shows `×` too.

**Technique tray (spec §11d).** `Scripts/UI/TechniqueTray.cs`: a Control on the
DeckUI layer (the hand strip's parent), shown for a selected martial with a
classed weapon, header line with Edge, weapon class and Honed state, one
card-shaped button per maneuver (cost after riders, grey with a reason when
unaffordable, ARMED footer when armed). Click arms, the legal target set is
outlined in steel with rings on enemies, a board click resolves, right-click
or Esc cancels, selecting another unit disarms. Hotkeys 1 to 6 for tray slots
are NOT wired: 1 to 4 already select units. Needs a ruling.

**Double-counted weapon damage, fixed.** `ResolveMartialAttack` added
`loadout.BonusAttackDamage` on top of an `AttackDamage` that
`ApplyEquipmentLoadout` had already raised by the same amount (the Q1 parity
assert checks that sum). An Iron Sword was worth +6, not +3. Removed; basic
attacks with a weapon now hit for less than they did yesterday, and maneuvers
price off one ATK. This is a balance change you may feel immediately.

**Debug (D13).** Ctrl+G staggers the inspected enemy (Shift+1..4 or click to
inspect). Ctrl+W cycles the selected martial's weapon class through the
launch four and None. Ctrl+E / Ctrl+Shift+E from M1 still fill and empty Edge.
All debug builds only.

## Rulings made in-session

1. Stagger on a Guard intent: the Guard executes normally and the stagger is
   held for the next non-Guard activation (spec: "does not affect Guard
   intents"). A stunned or postponed unit also keeps its stagger (D1).
2. Staggered RangedAttack: no movement at all (the spec's "move-only" is
   applied to melee Attack; kiting logic for a cancelled shot was not worth
   its own branch). Melee closes without swinging.
3. Line shapes stop after a sight-blocking tile and at the map edge.
4. Maneuvers do not receive the stance's basic-attack modifiers (Aggressive
   +1 damage, Aimed +3 and pierce, Berserk scaling); the §5 riders replace
   them. A maneuver is otherwise an attack (D2): Marked bonus, item onAttack
   procs, presenter strike, Edge attack triggers, `HasAttacked` flags.
5. Reaction definitions are refused by the registry until M4 rather than
   loaded and shown inert.

## Not done, owed

- `dotnet build`: still the sandbox's gap. Substitute: an independent review
  of the diff against the live tree found two defects (hover preview wiping
  the armed envelope; the Guard exemption reading a null intent), both fixed.
- Godot `.uid` files for the four new scripts: commit them after first import.
- The existing action-bar "Brace (all AP)" will collide by name with the
  spec's Brace reaction in M4. Rename one then.
- Poise is never shown for an enemy whose intent kind is hidden (the suffix
  rides the intent marker). Fine for launch; revisit with the marker redesign.

## In-engine confirm owed (M2 acceptance, spec §10)

1. Fight with a sword-and-shield Fighter: the tray shows Shield Bash
   (1 AP · 2 Edge). At 1 Edge it is grey with "Needs 2 Edge (has 1)".
2. Ctrl+E, click Shield Bash: adjacent enemies ring in steel; click one:
   `ATK - 1` damage, pushed 1, log says STAGGERED, `×` on its marker.
3. Enemy phase: that enemy's beat is cancelled; next round it telegraphs the
   SAME beat (cycle index held). On a `Channel` caster the charge is gone.
4. Elite (`P1` on the marker): first Bash logs "poise cracks (0/1 left)",
   second Bash staggers; after its staggered activation `P1` is back.
5. At 3+ Edge the Strike tooltip shows the Honed reach for a bow, and a
   sword-and-shield basic attack logs "[Honed] gains 1 shield".
6. Iron Sword Fighter's basic attack hits for BaseAttackDamage + 3 (+1 at
   Training Grounds 2), not +6.
7. Ctrl+W to Crossbow: Piercing Bolt and Heavy Bolt appear; Heavy Bolt is
   grey after moving with "needs a planted stance".
