# V2 Verification — The Reading Game's Surface

*Built 2026-07-09 against combat_ui_v2 §5–§7b/§11 + the §7a supersession ruling (build_order_v3). Visual + tooltip verification; conductor roster is the content harness.*

**Change inventory:** UnitDefinition gains Role/FactionId/IntelDescription (missing role → "line", so all pre-V2 JSONs stay valid; registry-asserted); conductor JSONs updated (Final Service = elite); Unit + both spawn paths carry role/faction. New `UIContent.cs` — plain-language behavior lines keyed by BehaviorKey + tag clauses, ability icon glyphs, role markers (content, not code). UITheme.RoleLine/Elite/Boss. Roster v2: role marker (· / » / ◆), nameplate policy (Line = ThreatLabel+index, Elite/Boss = full name in role color), ability chips with telegraph tooltips, faction-tinted HP bars (generics keep the health gradient), behavior tooltip on the name button, acting-unit ▶ marker during the enemy phase, row hover = world hover. Inspect panel (enemies): behavior line, per-ability blocks (icon + name + IntelDescription), role · faction line. Threat-range overlay: hover (world OR roster) shows reach-AND-attack envelope — movement reachability (immobile = stays put) ring-expanded by AttackRange; complements the locked-intent reticles per the supersession ruling (reticle = this turn's commitment, zone = next turn's possibility space). Deployment intel rows get role markers + intel tooltips.

## Checklist (launch a `final_service` battle-pool fight — all three conductor units)

1. **Assert Units** still ALL PASSED (14 defs; role vocabulary check added).
2. **Roster reads at a glance:** » The Final Service in elite gold, · Honored Dead / · Wake-Keeper as line dots; all three HP bars tinted Long Table violet; Wake-Keeper shows ✦ chip, Final Service ✸ chip — hover each chip for the telegraph sentence; hover a name for the behavior line.
3. **Deployment intel:** role markers + names + intel tooltips before spawn.
4. **Threat overlay:** hover the Final Service in-world → zone = movement envelope +1 ring; hover the Wake-Keeper roster ROW → same overlay appears (row hover = world hover); hover the debug Bolt Turret → zone is exactly its R4 ring around a fixed position (immobile). Un-hover restores the selected unit's move tiles.
5. **Active marker:** during the enemy phase, ▶ walks down the roster in activation order — the roster is the phase's progress bar.
6. **Inspect:** click the Final Service → left panel appends "Marks your healthiest unit and grinds toward it." / ✸ Deathburst + intel line / "Elite · The Long Table". Click a debug Pack Hound → behavior line includes the pack clause; role line reads "Line" with no faction.
7. **Generic regression:** a generics-only debug fight — gradient HP bars, dot markers, no chips, no faction line; nothing else changed.

## Deferred to V3 (tracks U3, per plan)

Ability STATE chips (charge dots, EveryN pips, spent markers), aura extents on hover, log grammar (FormatLogLine), the stack panel replacing the interim priority prompt, R22 damage preview.

## Notes

- Faction tint uses `ArchmageRegistry.Get(FactionId).FactionColorHex` lerped 25% toward white — if it reads too flat against the violet panel, the lerp constant is the knob (CombatUI.RefreshEnemyRoster).
- §7a supersession ruling is recorded in build_order_v3 (V2–V5 row); combat_ui doc still needs its v2.2 pass.
- Watch for double-telegraph clutter (reticles + hover zones + move overlay): the fallback ruled in advance is roster-hover-only for the zone.
