# Martial Maneuvers & Edge, Spec v1 (transcription)

Source: the PDF "Martial Maneuvers and Edge, Spec v1", Ian Thulin, 2026-09-27, 21 pages.
Transcribed into the repo on 2026-09-27 so build sessions can read it. Tables
are carried whole; prose is condensed. The PDF remains the authority on
wording; this file is the authority on nothing.

## 1. Problem and goals

Martials have one verb per turn and wizards have five. This spec gives martials
a small verb set (maneuvers), a resource that makes them plan across turns
(Edge), and a battlefield job wizards cannot do as well (roles).

Today: 3 AP (Training Grounds can raise it). Move 1 AP per step, melee 1 AP,
ranged 2 AP, stance switch 1 AP once per turn. Stances are mostly stat deltas
plus a few special tags. Companion JSONs carry two stances each. Roughly one
real decision per turn, often zero once the stance is set.

Goals: (1) two martials with the same class but different weapons and stances
play visibly differently within two turns; (2) a martial turn contains at
least two meaningful choices most turns; (3) martials own jobs the U3 enemy
roster demands (breaking intents, holding ground, cracking `chitin`, dragging
units out of `bodyguard` range); (4) martials get decisions during the enemy
phase; (5) identity stays with the character, not the loot table.

Non-goal: a card deck for martials. Edge is the martial answer to mana, not a
copy of it.

## 2. Martial roles

A role emerges from weapon class plus stance; it is not a class field.

| Role | Job | U3 threats it answers | Natural weapons |
|---|---|---|---|
| Breaker | Interrupt telegraphed enemy intents | `Channel` to `Release`, `Ritual`, `Summon`, `Shift`, `summon_cadence` | Hammer, sword & shield |
| Anchor | Own space; punish movement; protect the caster | `charge`, `pack`, `binding_geas` pressure, backline divers | Polearm, sword & shield |
| Executioner | Burst through defensive shapes; finish | `chitin`, high `Armor`, `regrowth` thresholds, `retaliate` | Two-hander, crossbow |
| Hunter | Reposition enemies and self; reach the backline | `bodyguard`, `veil`, enemy casters, `scout`/`stalker` keys | Bow, paired blades |

### 2a. Stagger (new status)

Applied by specific maneuvers. A staggered enemy's current intent is cancelled
at its next activation: a `Channel` resets to the start of its cycle and loses
its charge; `Ritual`, `Summon`, `Shift` do nothing that activation; an `Attack`
or `RangedAttack` intent becomes a move-only activation. The cycle index does
not advance on a staggered activation (the enemy repeats the beat next turn).
Stagger does not stack and does not affect `Guard` intents.

Poise: elites and bosses carry a `Poise` value (default 0, elite 1, boss 2).
Each stagger removes 1 Poise instead of staggering; at 0 the next stagger
lands, then Poise refills at the end of that activation. The marker shows a
crack glyph over the intent when stagger will land, and a Poise pip count on
elites. No hidden state.

## 3. Edge: core rules

Per-unit integer, 0 to 5, that stances generate and maneuvers spend. The
stance decides what earns Edge; the weapon decides what Edge buys; the role is
what the purchase does on the board.

| Rule | Value (launch) | Why |
|---|---|---|
| Cap | 5 (Training Grounds tier 3: 6) | Bank for a 3-cost finisher plus a reaction |
| Starting Edge | 1 | One cheap maneuver on turn 1 |
| Gain | Stance trigger only (§5), max +3 per round from all sources | One place to balance generation |
| Spend | Maneuvers cost AP and 1 to 3 Edge | AP keeps the action economy; Edge adds planning |
| Basic attack | No Edge, still 1 AP melee / 2 AP ranged | 0 Edge is weaker, never helpless |
| Idle decay | End of the unit's turn: neither gained nor spent this round, lose 1 | Kills turtling and banking to cap |
| Stance switch | Keeps Edge (still 1 AP, once per turn) | Build in one stance, cash out in another |
| Combat end | Resets to 0 | No carryover |
| Display | Pips under the health bar; count plus next-trigger text in the unit panel | Same readability rule as mana |

Decay window (2026-09-27 amendment): end of the unit's previous turn to the
end of this one, so Edge earned during the enemy phase counts.

Enemy interaction: an `edge_drain` onAttack key (struck martial loses N Edge)
and `action_tax`, which already applies. Author `edge_drain` on one faction
only at first.

## 4. Alternative models (brainstorm, kept as per-stance rules)

Generation: stance trigger is the spine. Openings (hits place tokens on the
enemy; maneuvers against it spend them) ships as one stance, Opportunist,
before any global promotion. Momentum is Skirmish's trigger. Reading the
telegraph is Vigilant's. Wound-fed is Berserk and Defensive. Warband pool
rejected as global (signature or Regalia only). Cross-class feed later. Edge
debt (spend Edge you lack for 2 self-damage per missing point) is a Reckless
special only.

Spend and hold: Honed (at 3+ Edge the basic attack gains the weapon class's
rider; below 3 turns it off). Finisher (one maneuver per class drains all Edge
for a scaling effect). Reactions cost Edge (§7): ending a turn at 0 means no
reactions.

Wild, not scheduled: Edge as intent visibility; Edge carried onto the
overworld; weapon affinity (+1 starting Edge after N fights with a class);
signature finishers usable only at 5 Edge.

## 5. Stance rewrite: stances as Edge engines

Every stance gets one Edge trigger and keeps one trimmed passive. Stance ids
do not change. Twelve stances verified against `StanceDefinition.cs` on
2026-09-27 (no Taunt stance; the new Openings stance is Opportunist, not
Duelist).

| Stance | Class | Edge trigger | Kept passive (trimmed) | Rider on maneuvers | Role lean |
|---|---|---|---|---|---|
| Aggressive | Fighter | +1 on the first hit each turn | +1 damage (was +2), push 1, -1 armor | +1 push | Breaker |
| Defensive | Fighter | +1 when struck (once per enemy activation) | +3 armor (was +4), 2 shield on hit, -1 damage | Gain 1 shield | Anchor |
| Reckless | Fighter | +2 at turn start | Hits all adjacent, 2 self-damage on attack, -2 armor | May use Edge debt | Executioner |
| Duelist | Fighter | +1 on hitting a Vulnerable target | +1 range, applies Vulnerable | Maneuvers against Vulnerable targets +1 damage | Executioner |
| Berserk | Fighter | +1 at turn start while below 50% HP | Damage scales with missing HP | Maneuver damage +1 below 50% | Executioner |
| Guardian | Fighter | +1 when an adjacent ally is attacked (once per enemy activation) | Adjacent allies +2 armor; attack taunts | Reactions trigger on attacks against adjacent allies; staggered enemies must target this unit | Anchor |
| Aimed | Ranger | +1 at end of turn if the unit did not move | +3 damage and ignores armor if unmoved | +1 range | Executioner |
| Suppression | Ranger | +1 on hitting a Suppressed target | On hit: -1 move next turn | Root and suppress durations +1 | Hunter |
| Volley | Ranger | +1 per extra enemy hit by one action | Attack pierces a line | Line and arc maneuvers +1 length | Hunter |
| Skirmish | Ranger | Momentum: +1 when the action differs from the previous one (max 2 per turn) | +1 speed, 1-tile dash after attack (was 2) | Self-displacement maneuvers +1 hex | Hunter |
| Ambush | Ranger | Starts combat at 3 Edge; +1 on hitting an enemy at full HP | First attack +50% (was double) | First maneuver each combat costs 1 less | Executioner |
| Marked | Ranger | +1 when another ally hits a Marked target | Applies Marked; next ally hit +3 | Maneuvers against Marked targets cost 1 less | Hunter |
| Vigilant (new) | Fighter | +1 per visible intent it counters at turn end, max 2 | None | Stagger maneuvers ignore 1 Poise | Breaker |
| Opportunist (new) | Either | Openings: hits place Opening tokens on the target; maneuvers against it spend Openings as Edge | None | Maneuvers against the target with most Openings +1 damage | Executioner |

Balance notes (moderate confidence): Aimed and Defensive are the turtling
risks (fallback for Aimed: gain only if the unit also attacked). Reckless at
+2 per turn is the fastest engine by design; cut to +1 if it dominates.
Opportunist is a prototype of the Openings model.

## 6. Weapon classes and maneuvers

Launch with four weapon classes and eight maneuvers. The weapon's class grants
maneuvers; the item stays passive (R1). Access: Fighter: sword & shield,
polearm, hammer, two-hander, paired blades. Ranger: bow, crossbow, sling,
paired blades. No classed weapon means basic attack only. Weapon class is
locked at Muster.

`ATK` = the unit's `AttackDamage` (base 3, +1 at Training Grounds tier 2).
Costs are AP / Edge.

### 6a. Launch set

| Weapon | Maneuver | Cost | Range / shape | Effect | Role |
|---|---|---|---|---|---|
| Sword & shield | Shield Bash | 1 / 2 | Adjacent | `ATK - 1` damage, push 1, Stagger | Breaker |
| Sword & shield | Brace | 0 / 1 (arms a reaction) | Self | Until your next turn: the first enemy to end movement adjacent is struck for `ATK` before it acts | Anchor |
| Polearm | Reach Thrust | 1 / 1 | Line of 2 | `ATK` to both hexes in a straight line | Anchor |
| Polearm | Set Against Charge | 0 / 1 (arms a reaction) | Range 2 | Until your next turn: an enemy that charges or moves into range 2 is struck for `ATK x 2` and its movement ends there | Anchor |
| Bow | Pinning Shot | 2 / 2 | Range 4 | `ATK - 1` damage, rooted 1 turn | Hunter |
| Bow | Driving Shot | 2 / 2 | Range 3 | `ATK - 1` damage, push 2 directly away; collision with a unit, wall or raised tile deals 2 to each | Hunter |
| Crossbow | Piercing Bolt | 2 / 2 | Line of 4 | `ATK` to every unit in the line; ignores armor and `chitin` | Executioner |
| Crossbow | Heavy Bolt (finisher) | 2 / all (min 2) | Range 4, unit must not have moved this turn | `ATK + 2 per Edge spent` | Executioner |

Honed riders (at 3+ Edge): sword & shield: basic attack grants 1 shield.
Polearm: basic attack has reach 2. Bow: +1 range. Crossbow: basic attack
ignores 1 armor.

### 6b. Post-launch classes

| Weapon | Maneuver A | Maneuver B | Role |
|---|---|---|---|
| Hammer | Crushing Blow: `ATK`, Stagger, -2 armor for 2 turns | Knockback: push 2; collision with raised tile or wall deals 3 | Breaker |
| Two-hander | Cleave: `ATK` to the 3-hex frontal arc | Sunder: strip all shield, then `ATK`; ignores `chitin` | Executioner |
| Paired blades | Flank Strike: `ATK + 2` if an ally is on the hex opposite the target | Shadowstep: swap places with an adjacent unit, ally or enemy | Hunter |
| Sling | Concuss: `ATK - 2` at range 3, Stagger | Ricochet: `ATK - 1`, then `ATK - 1` to the nearest other enemy within 2 | Breaker (ranged) |

Sling is the only ranged Stagger source; do not add ranged stagger elsewhere.

Authoring rule: every maneuver must change where, when, or whom on the board,
not just how much.

## 7. Reactions

Armed on the player's turn, fired automatically during the enemy phase. The
engine's priority windows make prompting possible; it is the wrong default.

Rules: (1) one armed reaction per unit; arming is a maneuver with its own AP
and Edge cost. (2) Edge is paid when arming; if the reaction has not fired by
the start of the unit's next turn, the Edge is refunded, and a refund does not
count as gaining for idle decay. (3) The armed zone is drawn on the hexes it
covers in the unit's colour; the unit panel shows the trigger text. (4)
Movement-triggered reactions resolve before the moving enemy acts;
struck-triggered reactions resolve after the damage. (5) A reaction that kills
or staggers the enemy ends its activation. (6) Guardian extends any armed
reaction's trigger to attacks aimed at adjacent allies.

Enemy AI: `charge` and `pack` units ignore armed zones. `scout`, `stalker` and
ranged keys route around armed zones when an alternative path costs at most 1
extra move. Bosses with Poise treat armed zones as a reason to use a ranged or
`Shift` beat if their cycle has one.

## 8. Architecture

Maneuvers (and §11 wand and belt actions) are data plus `IEffect` keys on the
existing trigger machinery; no third bespoke effect enum.

| Component | Change | Where |
|---|---|---|
| `WeaponClass` enum | `None, SwordShield, Polearm, Bow, Crossbow` (launch), later `Hammer, TwoHander, PairedBlades, Sling` | `ItemDefinition.cs` |
| `ItemDefinition.WeaponClass` | New field, Weapon slot only; JSON `"weaponClass"`, default `None` | `ItemDefinition.cs`, item JSONs |
| `ManeuverDefinition` | id, display name, text, weapon class, AP cost, Edge cost (or `all`), target shape (adjacent, line N, range N, arc, self), effect keys + params, `IsReaction`, reaction trigger, `IsFinisher`, role tag | `Scripts/Systems/Combat/ManeuverDefinition.cs` |
| `ManeuverRegistry` | Loads `Data/Maneuvers/*.json`; `ForWeaponClass(WeaponClass)` | new file, same pattern as `StanceRegistry` |
| Maneuver effects | One `case` + one `IEffect` per new key (`stagger`, `push`, `root`, `pierce_line`, `collision`, `finisher_scale`) in `BuildTriggeredEffect`; reuse existing push/status effects | `CombatManager.Triggers.cs` |
| Stagger | New status; `ExecuteIntent` checks it and cancels; `CycleKeyFor` index does not advance on a staggered activation; `Poise` on `UnitDefinition` | `CombatManager.EnemyIntents.cs`, `UnitDefinition.cs` |
| Unit state | `Edge`, `MaxEdge`, window counters, `ArmedReaction`, `Poise`, `MaxPoise`; reset beside `HasSwitchedStanceThisTurn` | `Unit.cs`, `CombatManager.cs` |
| Stance data | `EdgeTrigger` + `EdgeTriggerValue`; trim passives per §5 | `StanceDefinition.cs` |
| Edge triggers | Subscribe stance triggers to existing events; add `onEnemyMoveEnd` and `onAllyAttacked` call sites | `CombatManager.Triggers.cs` |
| Enemy planners | Read armed zones for `scout`/`stalker`/ranged path costs; `charge`/`pack` ignore | `CombatManager.EnemyIntents.cs` |
| Combat UI | Action tray on the card hand strip replaces a maneuver button row (§11d); stance row stays in the unit panel; Edge pips; armed-zone overlay; stagger crack glyph; Poise pips | `CombatUI.cs`, intent marker code |
| Validator | Every maneuver effect key resolves; weapon class legal; Edge cost within cap | existing data validation pass |
| Save | None beyond `weaponClass` on items; Edge and Poise are combat-scoped | |

Companion auto-battle AI: out of scope; flag if martials are ever AI-controlled.

## 9. Rulings (all accepted 2026-09-27)

R1 weapon class grants maneuvers, item stays passive (amendment to the locked
item rule). R2 stance-trigger spine; Opportunist prototypes Openings. R3 idle
decay yes. R4 reactions armed, not prompted. R5 Poise elite 1, boss 2. R6 trims
(Aggressive +1, Defensive +3, Ambush +50%). R7 sling is the only ranged
stagger. R8 unclassed levies get no maneuvers. R1b active items must be
charge-, stock- or deck-limited. R9 Belt slot (2 per unit). R10 staves are
cross-school. R11 martial wand use costs 1 Edge extra.

Open: weapon-affinity roll in the hiring matrix; a role readout at Muster.

## 10. Build order

| # | Scope | Acceptance test |
|---|---|---|
| M1 | Edge on `Unit`; stance `EdgeTrigger` for the 12 stances; idle decay; per-round cap; passive trims; Edge pips (Honed moved to M2) | Each stance's Edge rises on its trigger and decays when idle; switching keeps Edge; Defensive gains in the enemy phase prevent decay |
| M2 | `WeaponClass`, `ManeuverDefinition`, `ManeuverRegistry`, action tray with pinned technique cards; Honed riders; Stagger + Poise; Shield Bash end to end | Shield Bash on a `Channel` enemy resets it and its cycle index does not advance; on an elite the first Bash removes a Poise pip and the second staggers; crack glyph shows before the hit lands |
| M3 | Reach Thrust, Pinning Shot, Driving Shot, Piercing Bolt, Heavy Bolt; collision damage | Each fires from its button; Piercing Bolt ignores `chitin`; Driving Shot collision deals 2 to both; Heavy Bolt refuses after moving |
| M4 | Reactions: Brace, Set Against Charge; `onEnemyMoveEnd` and `onAllyAttacked`; armed-zone overlay; planner routing | A `charge` brute runs into Set Against Charge and stops; a `scout` routes around; unfired reaction refunds at turn start; Guardian extends Brace to an adjacent wizard |
| M5 | Vigilant and Opportunist; playtest pass | Goal 1 and Goal 2 measured from the run event log (2+ distinct martial choices per turn on average) |
| M6 | Belt slot; spellglass and consumable tray actions; wand charges; 1 AP item cost; martial wand Edge tax | Wand fires for 1 AP + 1 Edge; spellglass shatters; charges persist per expedition, refill at campus |
| M7 | Staff bound cards with Innate; staff item JSON binding a card id | Wizard with a staff opens every fight with the bound card in hand |

Measurement: the run event log records maneuver id, Edge before/after and
reaction fires; compute choices per martial turn before and after M5.

## 11. Active items

Staff (weapon, wizards only): adds one Innate copy of its bound card to the
deck. Wand (trinket, anyone): pinned tray action, charges per expedition
(start 3), refilled at campus. Spellglass / consumable (Belt, 2 slots,
anyone): pinned tray action, consumed on use, guild stock assigned at Muster.
Every pinned item action costs 1 AP; a martial firing a wand pays 1 Edge on
top; spellglass costs no Edge; martials stay at 0 mana. Martial consumables:
weapon oil, smoke, whetstone (+2 Edge, once per combat).

Selecting: each weapon class grants its 2 maneuvers automatically at launch;
post-launch classes grow to 4, learned at the Training Grounds, slot 2 at
Muster. In combat the card hand strip becomes one action tray: wizard sees
drawn cards left, pinned wand and belt right; martial sees pinned technique
cards left (never drawn or discarded), pinned wand and belt right. Technique
cards show AP/Edge cost, grey out when unaffordable, show the Honed state.
Flow reuses the existing two-step card targeting. Reaction cards have a
distinct frame and flip to Armed. The stance row stays in the unit panel.
Hotkeys 1 to 6 map to tray slots.

## 12. Remaining decisions (all accepted 2026-09-27)

D1 Stagger vs stunned: stun skips the whole activation; stagger cancels only
the intent, the unit may still move; stagger on a stunned unit does nothing
extra; a postponed unit keeps its stagger until the delayed activation. D2
Maneuvers and reactions count as attacks when they deal damage (fire
onAttack/onStruck, item passives, `retaliate`, Marked); push collision damage
is not an attack. D3 Maneuver resolution and item use are action seams; End
Turn confirm fires on unspent AP only, not Edge. D4 One-time migration maps
known weapon ids to a class; the validator warns on a martial fielded with a
`None` weapon. D5 Starting weapon is character design; procedural hires roll
a class-legal weapon. D6 Spellglass is the combat form; scrolls overworld
only. D7 Scribe's Tower supplies scrolls, spellglass and wand recharge; oils,
smoke, whetstones from markets and loot. D8 Every signature stance needs an
Edge trigger; candidate payoff: Sworn signature unlocks a finisher at 5 Edge.
D9 Re-verify the ranged-damage baseline with Edge on (M5). D10 No enemy
maneuvers or Edge at launch. D11 Card text for Stagger, Poise, Honed, Innate,
Armed and Edge costs goes in the style guide with sigils (M2). D12 Placeholder
attack swing plus a per-maneuver VFX tag. D13 Debug: set Edge (M1), apply
stagger, refill wand charges, force a weapon class. D14 One authored early
fight where a telegraphed `Channel` can only be stopped by Shield Bash (M5).
