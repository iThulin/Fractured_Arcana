# S3 verification — all schools, patrol APIs, companion-granted casting

Spec: `overworld_spell_system_v1_1.docx` §7, §10, S3 row of §14. Session: 2026-07-15.
New: Data/OverworldSpells/{necromancer,chronomancer,enchanter,tinker,adept}.json
Modified: OverworldSpellManager · OverworldSpellEffects · GrimoirePanel ·
GrimoireState · ExpeditionManager · PatrolToken · OverworldFactionManager ·
NegotiationContext · NegotiationManager

## What shipped

All eight school sets castable (36 definitions total; registry log should read
36). Patrol APIs per §10: stun (holds position, distinct from rout-home
disengage), Veil (detection + interception fail; committed hunters lose the
trail), Thornwall hex-deny on patrol pathing, Fulminant Charge traps (spring
on first entry, no expiry), Stasis Snare (tile-target a visible patrol),
Parley Compulsion (armed charge converts the next interception into a
negotiation via a bespoke trigger that skips POI consumption). Path targeting
(Bone Scout from a Remnant, Beast Envoy) — click-to-extend, click-the-end to
send, live count, Esc/right-click cancel. Retrace (once/exp hard cap): undoes
the last committed move, refunds the charged step cost (HP drains are NOT
refunded); last-move memory clears on any scene swap. Deploy Waystation: one
rest use (quarter-heal +3 Essence) AND a supply anchor incl. free extraction
while standing (W-track ruling #2 — 5✦ Overt; tuning watch). Companion-granted
casting: active-party companions of other schools grant their two innates at
+1 Essence (off-caster tax, shown as `base+1✦` and "(+1 off-school)" in the
tooltip), waived for the Adept. Emulate recasts the last resolved spell at
cost+1 through its full flow (targeting included; once-caps still bind — an
Emulated Retrace stays impossible after Retrace has fired). Minor Working via
a three-option popup. Remnants recorded on every combat win (school-agnostic,
markers drawn when a necromancer is aboard). Attunements: Elemental Sense /
Wildsense / Deathsight (fog), Foreboding (pursuit vectors on committed
patrols), True Names (full archmage names on tokens), Surveyor's Eye +
Arcane Literacy (hover tooltip extras), Versatility (tax waiver; the +1 slot
waits on the S4 prep UI).

## Interpretations (flag if disagreeable)

- Beguile's "one tension band" = −2 StartingTension, applied in
  NegotiationManager before state init, consumed via NegotiationContext.TensionShift.
- Bone Scout is gated on a Remnant within 1 hex but the path DRAWS from the
  party's tile (scout raised beside you).
- Speak with the Fallen does both halves ("or" in the doc): charts every
  patrol's ground (r1 — their ghosts surface) AND names the nearest
  undiscovered POI's bearing. Lore drops deferred to S4.
- Auspice is a heuristic: marks loaded clean tiles adjacent to corrupted
  ground (the flood's frontier); it does not simulate the kingdom-pressure
  half of the tick. Marks persist until recast or expedition end.
- Foreboding: pursuit vectors only — ambush POIs don't exist yet.
- True Names: identity only — disposition + negotiation-archetype preread
  wait on content that exists at hover time (S4/S5).
- Identify (Arcanist) NOT implemented — greyed out. An interim version would
  have charged Essence for nothing (G5); it needs the ScoutReportPanel seam.
- Pallid Bargain requires HP > 4 ("not enough blood to bargain").

## Build checks

1. Boot: `[OverworldSpellRegistry] Loaded 36 overworld spell definition(s)`;
   round-trip line covers remnants/waystations/armed flags.
2. **Companion grant (S3 exit criterion)**: add a non-Elementalist companion
   to the active party → their school's two innates appear at `cost+1✦`
   with "(+1 off-school)"; remove them → spells vanish. Injured/dead
   companions grant nothing.
3. **Adept waiver (S3 exit criterion)**: new Adept guild → companion-granted
   spells show base cost, no tax.
4. **Veil**: cast with a committed pursuer adjacent — it breaks off and
   wanders; walk past it for 5 steps unmolested; step 6, it can re-commit.
5. **Stasis Snare**: visible patrol tiles highlight; frozen patrol holds
   position (does NOT teleport home) and resumes after 6 party steps.
6. **Thornwall**: block a chokepoint — pursuing patrol routes around or
   stalls; party walks the thorned hex freely; expires with the wither log.
7. **Fulminant Charge**: rig, retreat, kite a patrol across it — spring log,
   4-step freeze, second patrol crosses freely (charge consumed).
8. **Parley Compulsion**: arm (panel shows it), get intercepted → negotiation
   opens instead of combat; stance/echoes write normally on return; patrol
   disengages via the standard restore. Second interception fights normally.
9. **Beguile**: arm, open any negotiation → log `starting tension eased by 2`.
10. **Retrace**: move onto swamp (pay 3), Retrace → back on the previous hex,
    3 steps refunded, HP drain kept; button greys ("already cast"); after a
    combat return, "no step to retrace".
11. **Bone Scout**: win a fight (Remnant sliver appears for necro access),
    stand by it, draw a 5-hex path through fog, send — path charts. Beast
    Envoy same from anywhere (6 hexes).
12. **Waystation**: deploy deep in band 2 → supply readout snaps to
    `in range`, Extract button reads "Extract" while standing on it; step
    off/on → one-use rest fires, marker gone, leash resumes.
13. **Emulate**: cast Tremorsense (3✦), Emulate → Tremorsense again at 4✦;
    Emulate after Retrace → "already cast this expedition".
14. **Minor Working**: popup's three options — heal, adjacent-unseen chart
    (targeting), 3-step drain ward.
15. **Attunements** (debug through schools or new guilds): Wildsense reveals
    Rest POIs at 4; Deathsight silhouettes Ruins; Foreboding arrows on
    committed patrols; True Names full names; Surveyor's Eye cost/hazard on
    silhouette hover; Arcane Literacy reward category on revealed POI hover.
16. **Persistence**: armed flags (Parley/Beguile/Campward), remnants,
    waystations, LastCast survive a combat round-trip and a save/reload;
    all reset on fresh deploy.

## Predictions

- P1: a stunned patrol's coord is identical before/after its stun window.
- P2: veiled steps never emit `PatrolCapturedPlayer` even sharing a hex.
- P3: Emulate of a 3✦ spell from tier-1 corrupted ground charges exactly 5
  (3+1 emulate premium +1 corruption).
- P4: Retrace refunds exactly `Min(StepsRemaining-at-move, stepCost)` — never
  more than was charged.

## Known gaps (S4/S5)

Identify; scrolls + acquisition + prep UI (+1 Adept slot enforcement); lore
drops from Speak with the Fallen; echo emission for Overt casts (witness log
only); tier-3 casting exposure event; True Names negotiation preread;
Stormcall (waits on weather). Tuning watch: waystation-as-free-extraction;
Beguile band size (−2).
