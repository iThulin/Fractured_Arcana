# U2 Verification Queue — BehaviorKey Dispatch + Tags

*Built 2026-07-08 against archmage_unique_units §4/4a + build_order_v3 §4. Run in-engine; every session names its prediction and the log line that proves it. All sessions use the Combat Debug Launcher (the enemy roster is now registry-driven — every Data/Units/*.json appears with its tags in brackets).*

**Change inventory:** PlanIntent now dispatches on `Unit.BehaviorKey` (string → handler map); `melee_hunt_wounded` (stalker) added; five tag hooks (pack/bulwark/charge/scout/immobile) around plan/execute; `EnemyArchetype` enum + `EnemyArchetypeData` facade DELETED; `EnemySlot` keyed by unit id; `PendingEnemySpawn` rekeyed off the resolved Def; ScoutReportPanel + CombatDebugLauncher + ChronomancerEffects debug dump migrated; six `debug_*` tagged definitions authored as this queue's harness.

**Telegraph rule (design, logged):** `EnemyIntent.Value` = plan-time estimate incl. tag bonuses; `EnemyIntent.BaseValue` = untagged base; execution recomputes pack/charge bonuses against the board the player left. Untagged units: `Value == BaseValue`, execution identical to pre-U2 by construction.

## Session A — Regression: legacy parity (blocking)

1. Campus debug panel → **Assert Units** → expect `RESULT: ALL PASSED` including the new checks: round-trip incl. tags, additive-schema (missing `behaviorTags` → empty), alias resolution, BehaviorKey/Tag catalog audit of every loaded JSON.
2. Launch one encounter per tier (Skirmish/Battle/Siege/Ambush) with generics only. Prediction: identical reads to U1 — soldier chases nearest, brute marks highest-HP, defender guards then strikes adjacent, ranger kites at R3, wizard channel→release with slow rider. No `[EnemyAI] Unknown BehaviorKey` lines anywhere in the run.
3. One overworld (non-debug) encounter from a region pool: authored `"archetype": "Soldier"` strings must still resolve (alias path). Failure mode to watch: `EncounterPoolLoader: Unknown unit '...'` naming a pool.

## Session B — pack (2× Pack Hound + 1 Soldier)

- Prediction: hounds converge on adjacent tiles when step distances tie; strike log shows `strikes with the pack (+1)` only while adjacent to the OTHER hound.
- Counterplay check: kill or displace one hound before the enemy phase → the survivor's strike drops the +1 even though the telegraph showed it. (Telegraph rule above — this is intended, not a defect.)
- **First run 2026-07-08 (3 hounds): PASSED on damage arithmetic** — all strikes landed for 5 vs base 4, i.e. the +1 fired every activation. The `(+1)` callout was missing from the console because tag-evidence lines went only to AppendActionLog (UI log), not GD.Print — instrumentation defect, fixed same day (all tag lines now console-mirrored). Re-run needed only for the counterplay half: isolate one hound → its strike must land for 4.

## Session C — bulwark (1 Bulwark Guardian + 1 Soldier escort)

- Wound the escort below half HP while it stands adjacent to the guardian. Prediction: `plants itself in front of <escort>` and the guardian does not move (guard braces in place; melee strikes only if already in reach). Heal or kill the escort → normal movement resumes next activation.

## Session D — charge (1 Charging Boar, party 3+ tiles away)

- Prediction: boar takes MULTIPLE steps in one activation (`charges toward its mark!`), and when it lands adjacent: `charges in (+1)!`. Blocked path or AP exhaustion → normal advance, no bonus. Legacy single-step is otherwise unchanged for untagged melee.

## Session E — scout (1 Scout Flanker vs 2 clustered + 1 detached player unit)

- Cluster two units within 2 of the flanker. Prediction: at next plan, its intent re-aims at the detached unit (threat tile moves there on reveal). Approach paths prefer arrival tiles not adjacent to your other units when distances tie.

## Session F — immobile + stalker (1 Bolt Turret + 1 Stalker)

- Turret: never moves, shoots at R4, logs `mark out of range, shot wasted` when you leave its envelope (no repositioning — by design).
- Stalker: the metric is **absolute lowest CURRENT HP**, not most-wounded — a full-HP companion with a small pool legitimately outranks a wounded wizard with a bigger one. Test procedure (corrected after the 2026-07-08 run, where Corvin at 14/14 correctly outranked dfgh at 15/20 and read as a nearest-target miss): wound the party's lowest-MAX-HP unit below every other unit's current HP, keep a healthier unit CLOSER to the stalker. Prediction: it walks past the closer unit; console shows `[Stalker] <name> marks <target> (x/y HP — lowest current)` each plan. Taunt does NOT divert it (ruling: taunt is a nearest-selection nudge; stalker ignores nearest-selection). RedirectAll/decoys still override.
- **Design fork logged, resolved as-is:** current-HP (kill proximity, deterministic, pressures fragile companions → feeds the K-track demand economy) over missing-HP/fraction (wound-seeking). Doc §4 says "lowest-current-HP"; implementation matches. Revisit only with playtest evidence that stalkers camping squishy companions reads as unfair rather than threatening.

## Exit (units doc §13 U2)

Tagged debug encounter shows each tag's movement/targeting signature in the logs above; Session A parity holds; zero unknown-key warnings. Then mark U2 done in build_order_v3 §1 and delete this file's harness note — the `debug_*` defs stay (they're the standing tag test fixtures).

## Results (2026-07-08/09)

Sessions A–E: **PASSED.** Session F: turret **PASSED** (shot, whiffed on the dodged locked tile, never moved); stalker **PASSED after test correction** — it targeted the 14/14 companion over the 15/20 wizard, which is exactly the doc's lowest-CURRENT-HP rule; ruling affirmed (kill proximity over wound-seeking). Residual, low-risk, unverified: Session B's counterplay half (isolated hound strikes for exactly 4) — same code path as the proven +1, adjacency false; catch it incidentally in U3 play.

**U2 exit criteria met. Marked done in build_order_v3.**

## Interim UI shipped with U2 (superseded by combat_ui_v2)

The Session F whiff confusion exposed that locked tiles were invisible at the kind tier. Two changes shipped: (1) info-tier ruling — threat TILES moved to the always-visible kind tier, damage values stay reveal-gated; (2) a floating ◆ Label3D reticle over threatened tiles (tile TINT is structurally buried under the move-zone overlay for any tile inside the player's movement range — which shots at player units nearly always are). **The reticle is a stopgap by explicit agreement**: V2/V3 (combat_ui_v2 §7) owns the real threat visualization and must replace it, not inherit it. Colors in UITheme (TileThreatDim, TileThreatReticle/Dim).

## Known deferrals (logged, not defects)

- Wildlife summons (Bestiary) still bypass UnitRegistry — convergence is units doc §14 #7, after U6. Their JSON `tags` don't feed `Unit.BehaviorTags`; no cross-contamination with enemy pack logic.
- `QueueEncounterFromContext` scales damage as well as HP (Option B, sqrt/linear) — live code had already superseded R17's HP-only rule before U2; untouched.
- Scout flank preference is single-step lookahead (arrival-tile quality), not full flank pathing. Revisit only if Compact Runner reads wrong in U4.
