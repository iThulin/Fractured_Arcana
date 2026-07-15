# S1+S2 verification — overworld spell system (Grimoire, Essence, casting)

Spec: `overworld_spell_system_v1_1.docx` (project). Session: 2026-07-15.
New: GrimoireState.cs · OverworldSpellDefinition/Registry/Effects/Manager.cs ·
GrimoirePanel.cs · Data/OverworldSpells/{general,elementalist,arcanist,druid}.json
Modified: CycleState.cs · ExpeditionManager.cs · OverworldMovementCost.cs · UITheme.cs

## What shipped (one paragraph)

The noncombat magic layer, S1+S2 scope: JSON spell definitions loaded by a
lazy registry (one file per school, arrays); `GrimoireState` on CycleState
(known/prepared spells, Essence pool, cast counts, scrolls stub, beacons) with
a save round-trip assertion; `OverworldSpellManager` under ExpeditionManager
running the Idle→Targeting→resolve state machine, Essence accounting with the
corrupted-ground surcharge (+tier when casting from corrupted tiles), and a
bespoke-key effect dispatcher; `OverworldSpellEffects` (static, run-scoped)
holding timed windows that tick per party step and survive combat scene swaps;
the Grimoire panel bottom-left with cost/magnitude/tooltip and disabled-with-
reason buttons; an Essence line in the HUD cluster. Player school
(Elementalist) fully castable: Elemental Sense attunement, Force Path,
Tremorsense (+ Ember Ward learnable, debug-castable). S1 exit pair implemented
(Scrying Lens, Verdant Passage) plus Ley Tap and all four General spells,
seeded known-by-default (INTERIM until S4 acquisition). Regen per §5: +3 at
Rest, full at Outpost, +1 ending a step on Arcane Ground.

## Interpretations / deviations (all logged)

- **"The expedition window" → radius 12 of the party** (Tremorsense) — the
  W-track slid the window; party-centered radius preserves old semantics.
- **Force Path**: Mountain→Hills, water→Marsh, written to the WORLD tile
  (permanent for the cycle); Marsh's HP drain is the "may carry a hazard"
  clause, priced deterministically per G5.
- **Elemental Sense "Ice"**: no overworld Ice terrain exists — Snow stands in.
- **Attunement silhouettes chart** through the standard WriteVisibleToWorld
  pass (consistent with the W-track Silhouette⇒Charted rule; G2-legal —
  terrain shape only).
- **Panel is a fixed list**, not the doc's collapse-to-icon strip (polish later).
- **Charting façade lives on ExpeditionManager** (SpellChartHexRadius), not
  FogOfWarManager as §13 sketched — the fog manager has no world-array access;
  ExpeditionManager is the established discovery-write owner.
- General spells + first two prepared are seeded in GrimoireState field
  initializers — INTERIM, ruled 2026-07-15; S4 moves them behind acquisition.
- No prep/launch UI yet (prepared = seeded list); no scrolls, no companion
  grants, no echoes — S3–S5.

## Build checks (in order)

1. **Compiles clean; boot logs** `[OverworldSpellRegistry] Loaded 16 overworld
   spell definition(s)` on first expedition, `[S1 RoundTrip] GrimoireState
   round-trips`, `[Grimoire] School=Elementalist, Essence 10/10 …`.
2. **Panel + HUD**: Grimoire panel bottom-left lists Force Path (4✦, Overt
   tint), Tremorsense (3✦), Mending Cant (2✦), Purifying Rite (3✦, Overt
   tint); HUD shows `Essence: 10 / 10`. Unaffordable/unbuilt spells greyed
   with reason in tooltip.
3. **Tremorsense (None-target)**: cast → Essence 10→7, info names charted
   highland count, strategic map shows charted (dim) Mountain/Hills ring out
   to 12 — CHARTED not Explored (no POIs revealed).
4. **Force Path (Tile-target)**: click → adjacent Mountain/water hexes
   highlight blue; click one → terrain flips (Hills / Marsh), passable, −4
   Essence; right-click/Esc cancels cleanly; clicking a non-target consumes
   the click (no accidental move). Walk the opened Marsh: it drains HP (the
   hazard). Re-deploy over the same ground: the opened hex PERSISTS.
5. **Elemental Sense (attunement)**: walking near hidden Volcanic/water/Snow/
   Arcane Ground silhouettes them at range 3 (beyond normal vision); they
   land as Charted on the strategic map.
6. **Essence economy**: cast to <2, Mending Cant greys out ("not enough
   Essence"); Rest → +3 (log `[Grimoire] +3 Essence (Rest)`); Outpost → full;
   end a step on Arcane Ground → +1.
7. **Corrupted-ground surcharge**: debug-paint corruption (C key) under the
   party → panel costs show `2+1✦` etc.; casting charges base+tier; info line
   says how much was corruption.
8. **Campward chain**: cast Campward (armed shows in panel status row) → next
   Rest heals MaxHP/4 + MaxHP/8 and grants +5 Essence total; charge consumed.
9. **Verdant Passage (debug)**: enable DebugMode → all implemented spells
   appear; cast → Forest/Swamp move PREVIEW shows 1 and CHARGES 1 for 5
   steps (preview cannot diverge — the hook is inside StepCost); panel status
   row counts down; expiry logs "Verdant Passage fades."
10. **Purifying Rite**: on corrupted ground, drain suppressed for 10 steps
    (log line per suppressed tick), then resumes.
11. **Ember Ward (debug)**: volcanic drain negated for 8 steps.
12. **Scrying Lens (debug, Tile)**: only Charted/Explored tiles within 6
    highlight; cast charts radius 3 around the anchor — leapfrog works
    (chart, then target the new frontier).
13. **Wayfarer's Beacon**: gold diamond on the tile; persists through a
    combat round-trip AND a window slide away-and-back (marker never
    unloads); cleared on the next fresh deploy.
14. **Mid-expedition persistence (S1 exit)**: cast twice, enter combat, win,
    return: Essence/cast counts/beacons identical (no pool reset). Quit to
    desktop mid-expedition, relaunch: GrimoireState fields intact in the
    cycle JSON.

## Predictions

- P1: fresh deploy always logs a fresh 10/10 pool; combat return never does.
- P2: with Verdant Passage active, StepCost(Forest) == 1 in BOTH the
  highlight label and the charged cost, for exactly 5 steps.
- P3: casting from a tier-2 tile charges exactly base+2; the panel showed
  `+2` before the cast (G5 legibility).
- P4: Tremorsense charts only Mountain+Hills; count in the info line equals
  newly-charted tiles (re-cast on the spot charts 0).

## Known gaps (by design, S3–S5)

Attunements for 7 schools inert (logged once); Identify, Beast Envoy (Path),
Thornwall / all patrol-API spells; necro/chrono/enchanter/tinker/adept sets
unauthored; companion-granted casting + off-caster tax; scrolls; echo
emission (Overt casts log a witness line only); tier-3 casting exposure
event; launch-screen preparation UI. Grimoire panel may overlap LedgerPanel
if that also anchors bottom-left — report and we'll shift one.
