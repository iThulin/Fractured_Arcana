# Session log, 2026-09-27: Edge M1 (parts 1 and 2), wired against HEAD 23d7777

Martial Maneuvers & Edge spec v1, milestone 1: the Edge resource exists, every
stance earns it, it decays, and it shows. Nothing spends it yet (maneuvers are
M2+). Delivered as one git patch (`edge_m1_full.patch`, applies cleanly to
HEAD `23d7777`, 2026-09-25) plus `EdgeRules.cs` as a drop-in.

The spec document itself is NOT in the project knowledge base or the repo.
Only `EdgeRules.cs` and the part-1 patch sheet were available; upload the
spec so the next session can check M2 against it.

## Files

- `Scripts/Systems/Combat/Core/EdgeRules.cs` (new). Part 1 as delivered,
  plus one rule: a trigger that fires while capped (or after the per-round
  cap is spent) still marks the window as active
  (`EdgeTriggerFiredThisWindow`). Without it a capped Reckless fighter
  oscillated 5, 4, 5 every turn on a trigger that kept firing.
- `StanceDefinition.cs`: `EdgeTrigger` enum, four Edge fields, the 12 base
  stances re-authored per part 1 (Aggressive +2 to +1, Defensive +4 to +3,
  Skirmish free move 2 to 1, Ambush start bonus). The live file was at
  `Core/` and carried 10 K4 signature stances the project-knowledge copy did
  not have; see the ruling below.
- `Unit.cs`: Edge state fields; new `OnAttacked` event raised in
  `ApplyDamage` before mitigation; `RefreshHealthBar` pushes Edge to the bar.
- `CombatManager.cs`: `ResetForCombat` at companion spawn (after
  `ActiveStance` is set, with the Training Grounds tier); `OnTurnStart` in
  the player turn-start loop beside the `HasSwitchedStanceThisTurn` reset;
  `OnTurnEnd` in `EndPlayerTurn` before the enemy phase; `OnAction` on move,
  attack, stance switch and consumable use; `OnAttackHits` in
  `ResolveMartialAttack` before damage and on-hit statuses;
  `OnAllyHitMarkedTarget` after a hit on a target that carried Marked;
  `HandleUnitAttacked` (Defensive and Guardian, once per activation id);
  Ambush trimmed from double to +50% (integer floor); debug lever.
- `CombatManager.EnemyIntents.cs`: `_edgeActivationId++` at the head of each
  enemy activation in `RunEnemyTurn`. The player phase also bumps it once.
- `CombatUI.cs`: the stance line reads `[Defensive]   Edge ◆◆◇◇◇ 2/5`; the
  stance's description (which carries its Edge trigger text) is the tooltip.
- `HealthBarRoot.cs`: `SetEdge`; detail line gains `E◈◈···` after the AP
  pips. Only the proven `◈` glyph and U+00B7 are used.

## Rulings made in-session (revisit if wrong)

1. **Signature stances earn Edge.** The 10 `sig_*` stances had no trigger,
   which would have made an ArcStage-4 companion's best stance the only one
   that cannot build Edge. Each takes the trigger of its base-kit analogue:
   Feintwork = Duelist (hit Vulnerable), Oathwall = Guardian, Openings =
   Aggressive (first hit), Immovable = Defensive, Avalanche = Reckless (+2
   turn start), Killing Angle = Marked, Warding Volley = Volley, Read the
   Wind = Aimed, Patient Shot = Ambush (start +2, hit full HP), Storm of
   Shafts = Skirmish. Patient Shot's first strike is also trimmed to +50%,
   matching Ambush. Confidence moderate: the spec may want signatures to
   earn faster.
2. **"Struck" means the blow reached you, not that you lost HP.** The
   existing `Unit.OnStruck` fires only on HP loss, which would make the
   armour stance's own trigger self-defeating. New `OnAttacked` fires
   pre-mitigation, post veil/bodyguard/cover/sim gate. Enemy spells count
   (they set `AmbientDamageSource`). Retaliation and recoil do not (no
   source). A shot fully soaked by cover does not count.
3. **An extra turn is a turn.** `OnTurnEnd` runs at the end of the first
   turn and `OnTurnStart` again at the extra one; the per-window gain cap
   bounds what the second start can add.
4. **Volley's trigger is live but earns nothing yet.** `LinePiercing` has no
   resolver: Volley hits one target. `PerExtraTargetHit` counts real
   targets (primary plus Reckless splash), so it starts paying the day line
   piercing is built. Not built here; out of M1 scope.
5. **Debug lever (D13) is a key, not a panel.** There is no in-combat debug
   panel in the tree. Ctrl+E fills the selected martial's Edge, Ctrl+Shift+E
   empties it, debug builds only, neither touches the decay window.
6. **Ambush description now says +50%** since the resolver change shipped
   in the same patch.

## Not done, owed

- `dotnet build`. The cloud sandbox cannot reach dot.net, NuGet or the
  Ubuntu archive, so the gate is yours. Review substitute: an independent
  pass over the diff against the live tree (symbols, scoping, duplicates,
  event coverage, em-dash grep) found two items, both fixed above.
- Godot will generate `EdgeRules.cs.uid` on first import; commit it with
  the rest.
- Pre-existing bug seen in passing, not touched: in the companion spawn
  block, `unit.Stats.Health = unit.Stats.MaxHealth` (Training Grounds tier-3
  HP bonus) runs AFTER the K2.5 expedition-HP carry, so martials always
  field at full HP on expedition. Wizards are unaffected.

## In-engine confirm owed

1. Fight opens: a Fighter in Defensive shows `Edge ◆◇◇◇◇ 1/5` on the stance
   line and `E◈····` under the unit; Torrin's armor reads +3, not +4.
2. Ambush ranger opens at 3/5; first shot on a full-HP enemy prints
   `[Edge] +1 (fresh target)` and deals +50%, not double.
3. Enemy phase: an enemy hits the Defensive fighter twice in one
   activation; console shows ONE `+1 (struck)`. A second enemy hitting it
   shows another. End the next turn without acting: no idle decay (the
   window includes the enemy phase).
4. Reckless at 5/5: turn start logs nothing, end turn does NOT decay to 4.
5. Skirmish: move, attack, move logs two `momentum` gains; a third
   alternation does not.
6. Aimed: end turn without moving logs `+1 (held position)`.
7. Ctrl+E on a selected martial fills Edge in debug builds.
